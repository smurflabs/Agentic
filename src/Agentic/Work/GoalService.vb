Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Threading
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    Public NotInheritable Class GoalDef
        Public Property Id As String
        Public Property AgentId As String
        Public Property Title As String
        Public Property Detail As String
        Public Property Metric As String
        Public Property Status As String
        Public Property Progress As String
        Public Property ProgressPct As Integer
        Public Property CadenceMinutes As Integer
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
        Public Property LastReviewUtc As DateTime?
        Public Property NextReviewUtc As DateTime?
        Public Property Reviews As Integer
    End Class

    ''' <summary>
    ''' Standing goals (north-star objectives) an agent pursues without being told task by task. On each goal's cadence the
    ''' host injects a review task so the agent judges its progress and schedules its own follow-up work toward the goal.
    ''' </summary>
    Public NotInheritable Class GoalService
        Private ReadOnly _db As Database
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _profiles As IProfileProvider
        Private ReadOnly _waker As IAgentWaker
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _opt As AutonomyOptions
        Private ReadOnly _log As ILog
        Private ReadOnly _clock As IClock
        Private _loop As Task
        Private _accepting As Boolean = True

        Public Sub New(db As Database, tasks As TaskRepository, profiles As IProfileProvider, waker As IAgentWaker,
                       events As IAgentEventBus, opt As AutonomyOptions, log As ILog, clock As IClock)
            _db = db
            _tasks = tasks
            _profiles = profiles
            _waker = waker
            _events = events
            _opt = opt
            _log = log
            _clock = clock
        End Sub

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As GoalDef
            Return New GoalDef With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .Title = Db.Str(r, "Title"), .Detail = Db.Str(r, "Detail"),
                .Metric = Db.Str(r, "Metric"), .Status = Db.Str(r, "Status"), .Progress = Db.Str(r, "Progress"), .ProgressPct = Db.Int(r, "ProgressPct"),
                .CadenceMinutes = Db.Int(r, "CadenceMinutes"), .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"),
                .UpdatedUtc = Db.Dt(r, "UpdatedUtc"), .LastReviewUtc = Db.DtN(r, "LastReviewUtc"), .NextReviewUtc = Db.DtN(r, "NextReviewUtc"),
                .Reviews = Db.Int(r, "Reviews")}
        End Function

        Public Async Function CreateGoalAsync(agentId As String, title As String, detail As String, metric As String,
                                              cadenceMinutes As Integer, createdBy As String) As Task(Of GoalDef)
            If _profiles.TryGetProfile(agentId) Is Nothing Then Throw New InvalidOperationException($"No such agent '{agentId}'.")
            If String.IsNullOrWhiteSpace(title) Then Throw New InvalidOperationException("A goal title is required.")
            Dim now = _clock.UtcNow
            Dim g As New GoalDef With {
                .Id = Ids.NewId("goal"), .AgentId = agentId, .Title = TextUtil.Truncate(title, 300), .Detail = detail, .Metric = metric,
                .Status = "active", .ProgressPct = 0, .CadenceMinutes = Math.Max(5, cadenceMinutes), .CreatedBy = createdBy,
                .CreatedUtc = now, .UpdatedUtc = now, .NextReviewUtc = now, .Reviews = 0}
            Await _db.ExecuteAsync("INSERT INTO AgentGoals(Id, AgentId, Title, Detail, Metric, Status, ProgressPct, CadenceMinutes, CreatedBy, CreatedUtc, UpdatedUtc, NextReviewUtc, Reviews) " &
                                   "VALUES (@id, @a, @t, @d, @m, 'active', 0, @c, @by, @now, @now, @nr, 0)",
                                   ("@id", CObj(g.Id)), ("@a", g.AgentId), ("@t", g.Title), ("@d", g.Detail), ("@m", g.Metric),
                                   ("@c", g.CadenceMinutes), ("@by", createdBy), ("@now", Db.Ts(now)), ("@nr", Db.Ts(g.NextReviewUtc))).ConfigureAwait(False)
            Return g
        End Function

        Public Function ListGoalsAsync(Optional agentId As String = Nothing, Optional status As String = Nothing) As Task(Of List(Of GoalDef))
            Dim clauses As New List(Of String)
            If agentId IsNot Nothing Then clauses.Add("AgentId=@a")
            If status IsNot Nothing Then clauses.Add("Status=@s")
            Dim where = If(clauses.Count > 0, " WHERE " & String.Join(" AND ", clauses), "")
            Return _db.QueryAsync($"SELECT * FROM AgentGoals{where} ORDER BY CreatedUtc DESC LIMIT 500", AddressOf Map,
                                  ("@a", If(agentId, CObj(DBNull.Value))), ("@s", If(status, CObj(DBNull.Value))))
        End Function

        Public Function GetGoalAsync(agentId As String, id As String) As Task(Of GoalDef)
            Return _db.QuerySingleAsync("SELECT * FROM AgentGoals WHERE Id=@id AND AgentId=@a", AddressOf Map, ("@id", CObj(id)), ("@a", CObj(agentId)))
        End Function

        ''' <summary>Admin edits to a goal: status, cadence or detail (any Nothing is left unchanged).</summary>
        Public Async Function UpdateGoalAsync(id As String, status As String, cadenceMinutes As Integer?, detail As String, metric As String) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("UPDATE AgentGoals SET Status=COALESCE(@s, Status), CadenceMinutes=COALESCE(@c, CadenceMinutes), " &
                                           "Detail=COALESCE(@d, Detail), Metric=COALESCE(@m, Metric), UpdatedUtc=@now WHERE Id=@id",
                                           ("@s", If(status, CObj(DBNull.Value))), ("@c", If(cadenceMinutes.HasValue, CObj(cadenceMinutes.Value), DBNull.Value)),
                                           ("@d", If(detail, CObj(DBNull.Value))), ("@m", If(metric, CObj(DBNull.Value))), ("@now", Db.Ts(_clock.UtcNow)), ("@id", CObj(id))).ConfigureAwait(False)
            Return n > 0
        End Function

        ''' <summary>The agent's own progress report against a goal (from the record_goal_progress tool).</summary>
        Public Async Function RecordProgressAsync(agentId As String, goalId As String, progress As String, percent As Integer?, done As Boolean) As Task(Of Boolean)
            Dim status = If(done, "done", Nothing)
            Dim n = Await _db.ExecuteAsync("UPDATE AgentGoals SET Progress=@p, ProgressPct=COALESCE(@pct, ProgressPct), Status=COALESCE(@st, Status), UpdatedUtc=@now " &
                                           "WHERE Id=@id AND AgentId=@a",
                                           ("@p", CObj(TextUtil.Truncate(progress, 2000))), ("@pct", If(percent.HasValue, CObj(Math.Clamp(percent.Value, 0, 100)), DBNull.Value)),
                                           ("@st", If(status, CObj(DBNull.Value))), ("@now", Db.Ts(_clock.UtcNow)), ("@id", CObj(goalId)), ("@a", CObj(agentId))).ConfigureAwait(False)
            Return n > 0
        End Function

        Public Async Function DeleteGoalAsync(id As String) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("DELETE FROM AgentGoals WHERE Id=@id", ("@id", CObj(id))).ConfigureAwait(False)) > 0
        End Function

        ' ---------------------------------------------------------------- review loop

        Public Sub Start(ct As CancellationToken)
            If Not _opt.GoalsEnabled Then Return
            _loop = LoopAsync(ct)
        End Sub

        Public Sub [Stop]()
            _accepting = False
        End Sub

        Private Async Function LoopAsync(ct As CancellationToken) As Task
            Dim period = TimeSpan.FromSeconds(Math.Max(15, _opt.GoalSweepSeconds))
            While Not ct.IsCancellationRequested
                Try
                    Await Task.Delay(period, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                If Not _accepting Then Continue While
                Try
                    Await ReviewDueAsync(_clock.UtcNow).ConfigureAwait(False)
                Catch ex As Exception
                    _log.Warn("Goals", "Goal review pass failed: " & ex.Message)
                End Try
            End While
        End Function

        Friend Async Function ReviewDueAsync(now As DateTime) As Task(Of Integer)
            Dim due = Await _db.QueryAsync("SELECT * FROM AgentGoals WHERE Status='active' AND NextReviewUtc IS NOT NULL AND NextReviewUtc <= @now LIMIT 100",
                                           AddressOf Map, ("@now", CObj(Db.Ts(now)))).ConfigureAwait(False)
            Dim created = 0
            For Each g In due
                Dim profile = _profiles.TryGetProfile(g.AgentId)
                Dim next_ = now.AddMinutes(g.CadenceMinutes)
                If profile IsNot Nothing AndAlso profile.Enabled AndAlso _waker.IsRunning(g.AgentId) Then
                    Dim t As New AgentTask With {
                        .Id = Ids.NewId("tsk"), .AgentId = g.AgentId, .Name = TextUtil.Truncate("Goal review: " & g.Title, 200),
                        .Description = ReviewPrompt(g), .Kind = TaskKind.EventTriggered, .Handler = "reasoning", .Priority = PriorityLevel.Low,
                        .MaxRetries = 0, .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = Ids.NewId("cor"), .CreatedBy = "goal:" & g.Id}
                    If Await _tasks.InsertAsync(t).ConfigureAwait(False) Then
                        created += 1
                        _events.Emit(AgentEventType.TaskCreated, $"Goal review: {g.Title}", g.AgentId, t.Id, correlationId:=t.CorrelationId,
                                     data:=New Dictionary(Of String, Object) From {{"goal", g.Id}})
                        _waker.Wake(g.AgentId)
                    End If
                End If
                Await _db.ExecuteAsync("UPDATE AgentGoals SET LastReviewUtc=@now, NextReviewUtc=@nr, Reviews=Reviews+1 WHERE Id=@id",
                                       ("@now", CObj(Db.Ts(now))), ("@nr", CObj(Db.Ts(next_))), ("@id", CObj(g.Id))).ConfigureAwait(False)
            Next
            Return created
        End Function

        Private Shared Function ReviewPrompt(g As GoalDef) As String
            Dim sb As New StringBuilder()
            sb.AppendLine($"This is a review of your standing goal (id {g.Id}). Pursue it on your own initiative.")
            sb.AppendLine($"Goal: {g.Title}")
            If Not String.IsNullOrWhiteSpace(g.Detail) Then sb.AppendLine($"Detail: {g.Detail}")
            If Not String.IsNullOrWhiteSpace(g.Metric) Then sb.AppendLine($"How progress is judged: {g.Metric}")
            If Not String.IsNullOrWhiteSpace(g.Progress) Then sb.AppendLine($"Your last progress note ({g.ProgressPct}%): {g.Progress}")
            sb.AppendLine()
            sb.AppendLine("Do this now:")
            sb.AppendLine("1. Judge where the goal stands and what the single most useful next step is.")
            sb.AppendLine("2. If there is concrete work to do, schedule it with create_followup_task (one task per next step).")
            sb.AppendLine("3. Call record_goal_progress with your updated progress note and percent (set done=true only if the goal is fully met).")
            sb.AppendLine("4. Then call complete_task. Do not do the whole goal in this review — just assess and queue the next steps.")
            Return sb.ToString()
        End Function
    End Class

End Namespace
