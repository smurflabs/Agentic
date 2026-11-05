Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' Host-managed scheduling of recurring, scheduled and polling responsibilities. Schedules come from agent profiles
    ''' and are persisted (last/next run survive restarts). Due schedules create normal tasks in the agent's queue.
    ''' SkipIfRunning prevents unbounded pile-up when an agent is slow or stopped.
    ''' </summary>
    Public NotInheritable Class WorkScheduler
        Private ReadOnly _db As Database
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _profiles As IProfileProvider
        Private ReadOnly _waker As IAgentWaker
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _clock As IClock
        Private _loop As Task
        Private _accepting As Boolean = True

        Public Sub New(db As Database, tasks As TaskRepository, profiles As IProfileProvider, waker As IAgentWaker, events As IAgentEventBus, log As ILog, clock As IClock)
            _db = db
            _tasks = tasks
            _profiles = profiles
            _waker = waker
            _events = events
            _log = log
            _clock = clock
        End Sub

        ''' <summary>Next run after fromUtc (respecting StartUtc, EndUtc and the schedule's time zone); Nothing when it has ended.</summary>
        Public Shared Function NextOccurrence(s As ScheduleOptions, fromUtc As DateTime) As DateTime?
            Dim baseTime = fromUtc
            If s.StartUtc.HasValue AndAlso s.StartUtc.Value > fromUtc Then
                If s.IntervalSeconds.HasValue Then Return s.StartUtc.Value
                baseTime = s.StartUtc.Value.AddSeconds(-1)
            End If
            Dim result As DateTime?
            If s.IntervalSeconds.HasValue Then
                result = baseTime.AddSeconds(Math.Max(5, s.IntervalSeconds.Value))
            ElseIf String.IsNullOrWhiteSpace(s.Cron) Then
                Return Nothing
            Else
                Dim fields = s.Cron.Trim().Split(" "c, StringSplitOptions.RemoveEmptyEntries).Length
                Dim expr = Cronos.CronExpression.Parse(s.Cron, If(fields = 6, Cronos.CronFormat.IncludeSeconds, Cronos.CronFormat.Standard))
                Dim zone = If(String.Equals(s.TimeZone, "Local", StringComparison.OrdinalIgnoreCase), TimeZoneInfo.Local, TimeZoneInfo.Utc)
                result = expr.GetNextOccurrence(DateTime.SpecifyKind(baseTime, DateTimeKind.Utc), zone)
            End If
            If result.HasValue AndAlso s.EndUtc.HasValue AndAlso result.Value > s.EndUtc.Value Then Return Nothing
            Return result
        End Function

        ''' <summary>
        ''' Adds a recurring task created in the dashboard. Such schedules are kept when the agent's configured schedules are
        ''' synchronized. firstRunUtc: when to run first (Nothing = the first regular occurrence).
        ''' </summary>
        Public Async Function CreateAdminScheduleAsync(agentId As String, s As ScheduleOptions, createdBy As String, firstRunUtc As DateTime?) As Task(Of (Id As String, NextRunUtc As DateTime?))
            Dim now = _clock.UtcNow
            s.Id = "adm-" & Ids.NewToken(6).ToLowerInvariant()
            Dim id = agentId & ":" & s.Id
            Dim nextRun = If(firstRunUtc, NextOccurrence(s, now))
            Await _db.ExecuteAsync("INSERT INTO TaskSchedules(Id, AgentId, ScheduleId, DefinitionJson, Enabled, NextRunUtc, Source, RunCount, CreatedBy, CreatedUtc) " &
                                   "VALUES (@id, @a, @s, @d, 1, @n, 'Admin', 0, @by, @c)",
                                   ("@id", CObj(id)), ("@a", agentId), ("@s", s.Id), ("@d", JsonUtil.ToJson(s)), ("@n", Db.Ts(nextRun)), ("@by", createdBy), ("@c", Db.Ts(now))).ConfigureAwait(False)
            Return (id, nextRun)
        End Function

        Private Async Function GetDefinitionAsync(scheduleKey As String) As Task(Of (Definition As ScheduleOptions, Source As String))
            Dim rows = Await _db.QueryAsync("SELECT DefinitionJson, Source FROM TaskSchedules WHERE Id=@id",
                                            Function(r) (JsonUtil.FromJson(Of ScheduleOptions)(Db.Str(r, "DefinitionJson")), Db.Str(r, "Source")), ("@id", CObj(scheduleKey))).ConfigureAwait(False)
            If rows.Count = 0 Then Return (CType(Nothing, ScheduleOptions), CType(Nothing, String))
            Return rows(0)
        End Function

        ''' <summary>Pauses or resumes a schedule (resuming plans the next regular run).</summary>
        Public Async Function SetEnabledAsync(scheduleKey As String, enabled As Boolean) As Task(Of Boolean)
            Dim d = Await GetDefinitionAsync(scheduleKey).ConfigureAwait(False)
            If d.Definition Is Nothing Then Return False
            Dim nextRun = If(enabled, NextOccurrence(d.Definition, _clock.UtcNow), CType(Nothing, DateTime?))
            Await _db.ExecuteAsync("UPDATE TaskSchedules SET Enabled=@e, NextRunUtc=@n WHERE Id=@id", ("@e", CObj(enabled)), ("@n", Db.Ts(nextRun)), ("@id", scheduleKey)).ConfigureAwait(False)
            Return True
        End Function

        ''' <summary>Makes a schedule due now (the scheduler creates its task within seconds).</summary>
        Public Async Function RunScheduleNowAsync(scheduleKey As String) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("UPDATE TaskSchedules SET NextRunUtc=@n, Enabled=1 WHERE Id=@id", ("@n", CObj(Db.Ts(_clock.UtcNow))), ("@id", scheduleKey)).ConfigureAwait(False) > 0
        End Function

        ''' <summary>Deletes a dashboard-created schedule (configured schedules can only be paused). Returns Nothing, "not-found" or "config".</summary>
        Public Async Function DeleteAdminScheduleAsync(scheduleKey As String) As Task(Of String)
            Dim d = Await GetDefinitionAsync(scheduleKey).ConfigureAwait(False)
            If d.Definition Is Nothing Then Return "not-found"
            If Not String.Equals(d.Source, "Admin", StringComparison.OrdinalIgnoreCase) Then Return "config"
            Await _db.ExecuteAsync("DELETE FROM TaskSchedules WHERE Id=@id", ("@id", CObj(scheduleKey))).ConfigureAwait(False)
            Return Nothing
        End Function

        ''' <summary>Upserts the agent's schedules (keeping last/next run) and removes ones no longer configured.</summary>
        Public Async Function SyncAgentSchedulesAsync(profile As EffectiveProfile) As Task
            Dim now = _clock.UtcNow
            Dim ids As New List(Of String)
            For Each s In profile.Schedules
                Dim id = profile.Id & ":" & s.Id
                ids.Add(id)
                Dim existingNext = Await _db.ScalarAsync("SELECT NextRunUtc FROM TaskSchedules WHERE Id=@id", ("@id", CObj(id))).ConfigureAwait(False)
                Dim nextRun As DateTime?
                If existingNext IsNot Nothing Then
                    nextRun = Db.ParseTs(CStr(existingNext))
                Else
                    nextRun = If(s.RunOnStart, now.AddSeconds(5), NextOccurrence(s, now))
                End If
                Await _db.ExecuteAsync("INSERT INTO TaskSchedules(Id, AgentId, ScheduleId, DefinitionJson, Enabled, NextRunUtc) VALUES (@id, @a, @s, @d, @e, @n) " &
                                       "ON CONFLICT(Id) DO UPDATE SET DefinitionJson=@d, Enabled=@e, NextRunUtc=COALESCE(NextRunUtc, @n)",
                                       ("@id", CObj(id)), ("@a", profile.Id), ("@s", s.Id), ("@d", JsonUtil.ToJson(s)), ("@e", s.Enabled), ("@n", Db.Ts(nextRun))).ConfigureAwait(False)
            Next
            ' Only configured schedules are pruned; the ones created in the dashboard stay.
            Dim existing = Await _db.QueryAsync("SELECT Id FROM TaskSchedules WHERE AgentId=@a AND Source='Profile'", Function(r) Db.Str(r, "Id"), ("@a", CObj(profile.Id))).ConfigureAwait(False)
            For Each e In existing.Where(Function(x) Not ids.Contains(x))
                Await _db.ExecuteAsync("DELETE FROM TaskSchedules WHERE Id=@id", ("@id", CObj(e))).ConfigureAwait(False)
            Next
        End Function

        ''' <summary>Removes all of an agent's schedules (the agent was deleted).</summary>
        Public Function RemoveAgentSchedulesAsync(agentId As String) As Task(Of Integer)
            Return _db.ExecuteAsync("DELETE FROM TaskSchedules WHERE AgentId=@a", ("@a", CObj(agentId)))
        End Function

        Public Sub Start(ct As CancellationToken)
            _loop = LoopAsync(ct)
        End Sub

        Public Sub StopScheduling()
            _accepting = False
        End Sub

        Private Async Function LoopAsync(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                If Not _accepting Then Continue While
                Try
                    Await RunDueAsync(_clock.UtcNow, Nothing).ConfigureAwait(False)
                Catch ex As Exception
                    _log.Warn("Scheduler", "Scheduler pass failed: " & ex.Message)
                End Try
            End While
        End Function

        Private Class DueSchedule
            Public Id As String
            Public AgentId As String
            Public ScheduleId As String
            Public Definition As ScheduleOptions
            Public RunCount As Integer
        End Class

        ''' <summary>Creates tasks for due schedules. When agentFilter is set, that agent's schedules run now (Run Now button).</summary>
        Friend Async Function RunDueAsync(now As DateTime, agentFilter As String) As Task(Of Integer)
            Dim sql = "SELECT Id, AgentId, ScheduleId, DefinitionJson, RunCount FROM TaskSchedules WHERE Enabled=1 AND " &
                      If(agentFilter Is Nothing, "NextRunUtc IS NOT NULL AND NextRunUtc <= @now", "AgentId=@a") & " LIMIT 200"
            Dim due = Await _db.QueryAsync(sql, Function(r) New DueSchedule With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .ScheduleId = Db.Str(r, "ScheduleId"), .RunCount = Db.Int(r, "RunCount"),
                .Definition = JsonUtil.FromJson(Of ScheduleOptions)(Db.Str(r, "DefinitionJson"))}, ("@now", CObj(Db.Ts(now))), ("@a", agentFilter)).ConfigureAwait(False)
            Dim created = 0
            For Each d In due
                Dim s = d.Definition
                Dim profile = _profiles.TryGetProfile(d.AgentId)
                Dim nextRun = If(s Is Nothing, CType(Nothing, DateTime?), NextOccurrence(s, now))
                Dim taskId As String = Nothing
                If s IsNot Nothing AndAlso profile IsNot Nothing AndAlso profile.Enabled AndAlso _waker.IsRunning(d.AgentId) Then
                    Dim skip = s.SkipIfRunning AndAlso Await _tasks.HasActiveForScheduleAsync(d.Id).ConfigureAwait(False)
                    If Not skip Then
                        Dim payload As New Dictionary(Of String, String)(s.Parameters, StringComparer.OrdinalIgnoreCase)
                        Dim t As New AgentTask With {
                            .Id = Ids.NewId("tsk"), .AgentId = d.AgentId, .Name = If(s.Name, s.Id), .Description = s.Description, .Kind = s.Kind,
                            .Handler = If(s.Handler, "reasoning"), .Purpose = s.Purpose, .Priority = s.Priority,
                            .Payload = If(payload.Count > 0, JsonUtil.ToJson(payload), Nothing), .MaxRetries = If(s.MaxRetries, If(s.Handler = "reasoning", 1, 0)),
                            .ModelKey = s.ModelKey, .StartTier = s.StartTier, .Delivery = s.Delivery?.CloneSettings(), .ReviewCriteria = s.ReviewCriteria,
                            .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = Ids.NewId("cor"), .ScheduleId = d.Id, .CreatedBy = "scheduler",
                            .AutoApproveTools = s.AutoApproveTools, .RunPastDeadline = s.RunPastDeadline,
                            .DeadlineUtc = If(s.DeadlineMinutes > 0, now.AddMinutes(s.DeadlineMinutes), CType(Nothing, DateTime?)), .DeadlineEnforced = s.DeadlineMinutes > 0}
                        If Await _tasks.InsertAsync(t).ConfigureAwait(False) Then
                            taskId = t.Id
                            created += 1
                            _events.Emit(AgentEventType.TaskCreated, $"Scheduled task: {t.Name}", d.AgentId, t.Id, correlationId:=t.CorrelationId,
                                         data:=New Dictionary(Of String, Object) From {{"schedule", s.Id}, {"kind", t.Kind.ToString()}})
                            _waker.Wake(d.AgentId)
                        End If
                    End If
                End If
                ' A schedule ends when its end date passes or it reached its maximum number of runs.
                Dim runs = d.RunCount + If(taskId Is Nothing, 0, 1)
                Dim finished = s IsNot Nothing AndAlso (Not nextRun.HasValue OrElse (s.MaxRuns.HasValue AndAlso runs >= s.MaxRuns.Value))
                Await _db.ExecuteAsync("UPDATE TaskSchedules SET LastRunUtc=@l, NextRunUtc=@n, LastTaskId=COALESCE(@t, LastTaskId), RunCount=@rc, Enabled=CASE WHEN @f=1 THEN 0 ELSE Enabled END WHERE Id=@id",
                                       ("@l", CObj(Db.Ts(now))), ("@n", If(finished, Nothing, Db.Ts(nextRun))), ("@t", taskId), ("@rc", runs), ("@f", If(finished, 1, 0)), ("@id", d.Id)).ConfigureAwait(False)
            Next
            Return created
        End Function

        Public Function TriggerAgentNowAsync(agentId As String) As Task(Of Integer)
            Return RunDueAsync(_clock.UtcNow, agentId)
        End Function

        Public Function ListAsync(Optional agentId As String = Nothing) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim where = If(agentId Is Nothing, "", "WHERE AgentId=@a")
            Return _db.QueryAsync($"SELECT * FROM TaskSchedules {where} ORDER BY AgentId, ScheduleId",
                                  Function(r)
                                      Dim def = JsonUtil.FromJson(Of ScheduleOptions)(Db.Str(r, "DefinitionJson"))
                                      Return New Dictionary(Of String, Object) From {
                                          {"id", Db.Str(r, "Id")}, {"agentId", Db.Str(r, "AgentId")}, {"scheduleId", Db.Str(r, "ScheduleId")},
                                          {"enabled", Db.Bool(r, "Enabled")}, {"lastRunUtc", Db.Str(r, "LastRunUtc")}, {"nextRunUtc", Db.Str(r, "NextRunUtc")},
                                          {"lastTaskId", Db.Str(r, "LastTaskId")}, {"source", Db.Str(r, "Source")}, {"runCount", Db.Int(r, "RunCount")},
                                          {"createdBy", Db.Str(r, "CreatedBy")}, {"summary", If(def Is Nothing, Nothing, Recurrence.Describe(def))},
                                          {"name", def?.Name}, {"definition", JsonUtil.ParseElementOrNull(Db.Str(r, "DefinitionJson"))}}
                                  End Function,
                                  ("@a", CObj(agentId)))
        End Function
    End Class

End Namespace
