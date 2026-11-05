Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net.Http
Imports System.Text
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Events

    ''' <summary>Live per-agent counters (observability). Updated only from events; agents never touch it.</summary>
    Public NotInheritable Class AgentCounters
        Public TasksCompleted As Long
        Public TasksFailed As Long
        Public TasksRetried As Long
        Public AiCalls As Long
        Public AiFailures As Long
        Public AiLatencyMs As Long
        Public ToolCalls As Long
        Public ToolFailures As Long
        Public ToolLatencyMs As Long
        Public MessagesSent As Long
        Public MessagesReceived As Long
        Public Delegations As Long
        Public TaskDurationMs As Long
        Public TaskDurationSamples As Long
        Public Tokens As Long
        Public CostMicros As Long
        Public LastAiCallUtc As DateTime?
        Public LastToolCallUtc As DateTime?
        Public LastSuccessfulTaskUtc As DateTime?
        Public LastActivityUtc As DateTime?
        Public LastActivity As String
        Public LastError As String
        Public LastErrorUtc As DateTime?
    End Class

    ''' <summary>Consumes events to maintain live counters and flushes hourly buckets to the Statistics table.</summary>
    Public NotInheritable Class StatisticsAggregator
        Private ReadOnly _counters As New ConcurrentDictionary(Of String, AgentCounters)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _pending As New ConcurrentDictionary(Of String, StatisticsDelta)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _telemetry As TelemetryRepository
        Private ReadOnly _log As ILog
        Private ReadOnly _sub As EventSubscription
        Private ReadOnly _bus As IAgentEventBus
        Private _loop As Task
        Private _flushLoop As Task

        Public Sub New(bus As IAgentEventBus, telemetry As TelemetryRepository, log As ILog)
            _bus = bus
            _telemetry = telemetry
            _log = log
            _sub = bus.Subscribe("statistics", 20000)
        End Sub

        Public Async Function LoadAsync(database As Database) As Task
            Dim rows = Await database.QueryAsync("SELECT AgentId, SUM(TasksCompleted) TC, SUM(TasksFailed) TF, SUM(TasksRetried) TR, SUM(AiCalls) AI, SUM(AiFailures) AF, " &
                                           "SUM(AiLatencyMsTotal) AL, SUM(ToolCalls) TO1, SUM(ToolFailures) TOF, SUM(ToolLatencyMsTotal) TL, SUM(MessagesSent) MS, " &
                                           "SUM(MessagesReceived) MR, SUM(Delegations) D, SUM(TaskDurationMsTotal) TD, SUM(Tokens) TOK, SUM(CostMicros) C FROM Statistics GROUP BY AgentId",
                                           Function(r)
                                               Dim c As New AgentCounters With {
                                                   .TasksCompleted = Db.Lng(r, "TC"), .TasksFailed = Db.Lng(r, "TF"), .TasksRetried = Db.Lng(r, "TR"),
                                                   .AiCalls = Db.Lng(r, "AI"), .AiFailures = Db.Lng(r, "AF"), .AiLatencyMs = Db.Lng(r, "AL"),
                                                   .ToolCalls = Db.Lng(r, "TO1"), .ToolFailures = Db.Lng(r, "TOF"), .ToolLatencyMs = Db.Lng(r, "TL"),
                                                   .MessagesSent = Db.Lng(r, "MS"), .MessagesReceived = Db.Lng(r, "MR"), .Delegations = Db.Lng(r, "D"),
                                                   .TaskDurationMs = Db.Lng(r, "TD"), .TaskDurationSamples = Db.Lng(r, "TC") + Db.Lng(r, "TF"),
                                                   .Tokens = Db.Lng(r, "TOK"), .CostMicros = Db.Lng(r, "C")}
                                               Return New KeyValuePair(Of String, AgentCounters)(Db.Str(r, "AgentId"), c)
                                           End Function).ConfigureAwait(False)
            For Each kv In rows
                _counters(kv.Key) = kv.Value
            Next
            ' Restore "last seen" timestamps so the dashboard is meaningful right after a restart.
            Dim lastAi = Await database.QueryAsync("SELECT AgentId, MAX(CreatedUtc) AS T FROM AiCalls GROUP BY AgentId", Function(r) (Db.Str(r, "AgentId"), Db.DtN(r, "T"))).ConfigureAwait(False)
            For Each x In lastAi
                [Get](x.Item1).LastAiCallUtc = x.Item2
            Next
            Dim lastTool = Await database.QueryAsync("SELECT AgentId, MAX(StartedUtc) AS T FROM ToolCalls GROUP BY AgentId", Function(r) (Db.Str(r, "AgentId"), Db.DtN(r, "T"))).ConfigureAwait(False)
            For Each x In lastTool
                [Get](x.Item1).LastToolCallUtc = x.Item2
            Next
            Dim lastOk = Await database.QueryAsync("SELECT AgentId, MAX(CompletedUtc) AS T FROM AgentTasks WHERE Status='Completed' GROUP BY AgentId", Function(r) (Db.Str(r, "AgentId"), Db.DtN(r, "T"))).ConfigureAwait(False)
            For Each x In lastOk
                [Get](x.Item1).LastSuccessfulTaskUtc = x.Item2
            Next
        End Function

        Public Sub Start(ct As CancellationToken)
            _loop = ConsumeAsync(ct)
            _flushLoop = FlushLoopAsync(ct)
        End Sub

        Public Function [Get](agentId As String) As AgentCounters
            Return _counters.GetOrAdd(agentId, Function(k) New AgentCounters())
        End Function

        Private Function Delta(agentId As String, ts As DateTime) As StatisticsDelta
            Dim bucket = New DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:00:00Z")
            Return _pending.GetOrAdd(agentId & "|" & bucket, Function(k) New StatisticsDelta With {.AgentId = agentId, .BucketUtc = bucket})
        End Function

        Private Shared Function DataLong(e As AgentEvent, key As String) As Long
            Dim v As Object = Nothing
            If e.Data IsNot Nothing AndAlso e.Data.TryGetValue(key, v) AndAlso v IsNot Nothing Then
                Dim l As Long
                If Long.TryParse(Convert.ToString(v, Globalization.CultureInfo.InvariantCulture), l) Then Return l
            End If
            Return 0
        End Function

        Private Shared Function DataBool(e As AgentEvent, key As String, defaultValue As Boolean) As Boolean
            Dim v As Object = Nothing
            If e.Data IsNot Nothing AndAlso e.Data.TryGetValue(key, v) AndAlso TypeOf v Is Boolean Then Return DirectCast(v, Boolean)
            Return defaultValue
        End Function

        Friend Sub Apply(e As AgentEvent)
            If String.IsNullOrEmpty(e.AgentId) Then Return
            Dim c = [Get](e.AgentId)
            Dim d = Delta(e.AgentId, e.TimestampUtc)
            SyncLock c
                c.LastActivityUtc = e.TimestampUtc
                c.LastActivity = TextUtil.Truncate(e.Summary, 200)
                Select Case e.EventType
                    Case AgentEventType.TaskCompleted
                        c.TasksCompleted += 1 : d.TasksCompleted += 1
                        c.LastSuccessfulTaskUtc = e.TimestampUtc
                        Dim ms = DataLong(e, "durationMs")
                        c.TaskDurationMs += ms : c.TaskDurationSamples += 1 : d.TaskDurationMs += ms
                    Case AgentEventType.TaskFailed
                        c.TasksFailed += 1 : d.TasksFailed += 1
                        Dim ms = DataLong(e, "durationMs")
                        c.TaskDurationMs += ms : c.TaskDurationSamples += 1 : d.TaskDurationMs += ms
                    Case AgentEventType.TaskRetrying
                        c.TasksRetried += 1 : d.TasksRetried += 1
                    Case AgentEventType.AiCallCompleted
                        Dim ok = DataBool(e, "success", True)
                        Dim ms = DataLong(e, "durationMs")
                        c.AiCalls += 1 : d.AiCalls += 1
                        c.AiLatencyMs += ms : d.AiLatencyMs += ms
                        c.LastAiCallUtc = e.TimestampUtc
                        If Not ok Then c.AiFailures += 1 : d.AiFailures += 1
                        Dim tok = DataLong(e, "tokens")
                        Dim cost = DataLong(e, "costMicros")
                        c.Tokens += tok : d.Tokens += tok
                        c.CostMicros += cost : d.CostMicros += cost
                    Case AgentEventType.ToolCompleted
                        Dim ok = DataBool(e, "success", True)
                        Dim ms = DataLong(e, "durationMs")
                        c.ToolCalls += 1 : d.ToolCalls += 1
                        c.ToolLatencyMs += ms : d.ToolLatencyMs += ms
                        c.LastToolCallUtc = e.TimestampUtc
                        If Not ok Then c.ToolFailures += 1 : d.ToolFailures += 1
                    Case AgentEventType.MessageSent
                        c.MessagesSent += 1 : d.MessagesSent += 1
                    Case AgentEventType.MessageReceived
                        c.MessagesReceived += 1 : d.MessagesReceived += 1
                    Case AgentEventType.DelegationCreated
                        c.Delegations += 1 : d.Delegations += 1
                    Case AgentEventType.ErrorOccurred, AgentEventType.AgentUnhealthy
                        c.LastError = e.Summary
                        c.LastErrorUtc = e.TimestampUtc
                End Select
            End SyncLock
        End Sub

        Private Async Function ConsumeAsync(ct As CancellationToken) As Task
            Try
                While Await _sub.Reader.WaitToReadAsync(ct).ConfigureAwait(False)
                    Dim e As AgentEvent = Nothing
                    While _sub.Reader.TryRead(e)
                        Apply(e)
                    End While
                End While
            Catch ex As OperationCanceledException
            Catch ex As Exception
                _log.Err("Statistics", "Statistics consumer stopped", ex)
            End Try
        End Function

        Private Async Function FlushLoopAsync(ct As CancellationToken) As Task
            While Not ct.IsCancellationRequested
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                Await FlushAsync().ConfigureAwait(False)
            End While
        End Function

        Public Async Function FlushAsync() As Task
            Dim batch As New List(Of StatisticsDelta)
            For Each key In _pending.Keys.ToList()
                Dim d As StatisticsDelta = Nothing
                If _pending.TryRemove(key, d) Then batch.Add(d)
            Next
            If batch.Count = 0 Then Return
            Try
                Await _telemetry.FlushStatisticsAsync(batch).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Statistics", "Statistics flush failed: " & ex.Message)
            End Try
        End Function
    End Class

    ''' <summary>Persists events to ActivityEvents in batches (subject to retention).</summary>
    Public NotInheritable Class ActivityPersister
        Private ReadOnly _sub As EventSubscription
        Private ReadOnly _telemetry As TelemetryRepository
        Private ReadOnly _log As ILog
        Private _loop As Task

        Public Sub New(bus As IAgentEventBus, telemetry As TelemetryRepository, log As ILog)
            _telemetry = telemetry
            _log = log
            ' Very chatty start events are not persisted; their completion counterparts are. Nor is routine churn (see IsRoutine).
            _sub = bus.Subscribe("activity", 20000, Function(e) e.EventType <> AgentEventType.AiCallStarted AndAlso e.EventType <> AgentEventType.ToolStarted AndAlso Not IsRoutine(e))
        End Sub

        Private Shared ReadOnly RoutineStates As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"Idle", "Running", "Waiting", "Starting"}

        ''' <summary>
        ''' Events that only repeat what is always true, kept out of the activity history (they are still sent live): an agent
        ''' going Idle ↔ Running ↔ Waiting, and a polling task (a health check every minute) being created, started and finished
        ''' normally. A minute-by-minute check otherwise added about 7,000 rows a day and buried everything else.
        ''' </summary>
        Friend Shared Function IsRoutine(e As AgentEvent) As Boolean
            If e Is Nothing OrElse e.Level <> EventLevel.Info OrElse e.Data Is Nothing Then Return False
            Dim v As Object = Nothing
            Select Case e.EventType
                Case AgentEventType.AgentStateChanged
                    Dim fromState As Object = Nothing, toState As Object = Nothing
                    Return e.Data.TryGetValue("from", fromState) AndAlso e.Data.TryGetValue("to", toState) AndAlso
                           RoutineStates.Contains(If(fromState?.ToString(), "")) AndAlso RoutineStates.Contains(If(toState?.ToString(), ""))
                Case AgentEventType.TaskCreated, AgentEventType.TaskStarted, AgentEventType.TaskCompleted
                    Return e.Data.TryGetValue("kind", v) AndAlso String.Equals(v?.ToString(), "Polling", StringComparison.OrdinalIgnoreCase)
            End Select
            Return False
        End Function

        Public Sub Start(ct As CancellationToken)
            _loop = RunAsync(ct)
        End Sub

        Public ReadOnly Property Dropped As Long
            Get
                Return _sub.Dropped
            End Get
        End Property

        Private Async Function RunAsync(ct As CancellationToken) As Task
            Dim batch As New List(Of AgentEvent)
            While True
                Dim more As Boolean
                Try
                    more = Await _sub.Reader.WaitToReadAsync(ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    more = False
                End Try
                Dim e As AgentEvent = Nothing
                While batch.Count < 500 AndAlso _sub.Reader.TryRead(e)
                    batch.Add(e)
                End While
                If batch.Count > 0 Then
                    Try
                        Await _telemetry.InsertActivityBatchAsync(batch).ConfigureAwait(False)
                    Catch ex As Exception
                        _log.Warn("Activity", $"Failed to persist {batch.Count} events: {ex.Message}")
                    End Try
                    batch.Clear()
                End If
                If Not more Then Exit While
                If Not ct.IsCancellationRequested Then
                    Try
                        Await Task.Delay(500, ct).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                    End Try
                End If
            End While
        End Function

        ''' <summary>Drains whatever is buffered (shutdown).</summary>
        Public Async Function DrainAsync() As Task
            Dim batch As New List(Of AgentEvent)
            Dim e As AgentEvent = Nothing
            While _sub.Reader.TryRead(e)
                batch.Add(e)
            End While
            If batch.Count > 0 Then
                Try
                    Await _telemetry.InsertActivityBatchAsync(batch).ConfigureAwait(False)
                Catch
                End Try
            End If
        End Function
    End Class

    ''' <summary>Forwards important events to an external webhook (rate limited per event type).</summary>
    Public NotInheritable Class AlertService
        Private Shared ReadOnly DefaultTypes As AgentEventType() = {
            AgentEventType.AgentUnhealthy, AgentEventType.AgentStuck, AgentEventType.McpServerStateChanged, AgentEventType.ModelHealthChanged,
            AgentEventType.BudgetInsufficient, AgentEventType.ApprovalRequested, AgentEventType.ErrorOccurred}

        Private ReadOnly _sub As EventSubscription
        Private ReadOnly _opt As AlertOptions
        Private ReadOnly _log As ILog
        Private ReadOnly _http As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(15)}
        Private ReadOnly _lastSent As New ConcurrentDictionary(Of String, DateTime)
        Private ReadOnly _types As HashSet(Of String)
        Private _loop As Task

        Public Sub New(bus As IAgentEventBus, options As AlertOptions, log As ILog)
            _opt = options
            _log = log
            _types = New HashSet(Of String)(If(options.Events.Count > 0, options.Events, DefaultTypes.Select(Function(t) t.ToString()).ToList()), StringComparer.OrdinalIgnoreCase)
            If Not String.IsNullOrWhiteSpace(options.WebhookUrl) Then
                _sub = bus.Subscribe("alerts", 1000, Function(e) _types.Contains(e.EventType.ToString()) AndAlso
                                                                  (e.EventType <> AgentEventType.McpServerStateChanged OrElse e.Level <> EventLevel.Info) AndAlso
                                                                  (e.EventType <> AgentEventType.ModelHealthChanged OrElse e.Level <> EventLevel.Info))
            End If
        End Sub

        Public Sub Start(ct As CancellationToken)
            If _sub IsNot Nothing Then _loop = RunAsync(ct)
        End Sub

        Private Async Function RunAsync(ct As CancellationToken) As Task
            Try
                While Await _sub.Reader.WaitToReadAsync(ct).ConfigureAwait(False)
                    Dim e As AgentEvent = Nothing
                    While _sub.Reader.TryRead(e)
                        Dim key = e.EventType.ToString() & "|" & e.AgentId
                        Dim last As DateTime
                        If _lastSent.TryGetValue(key, last) AndAlso DateTime.UtcNow - last < TimeSpan.FromSeconds(_opt.MinIntervalSeconds) Then Continue While
                        _lastSent(key) = DateTime.UtcNow
                        Dim body = JsonUtil.ToJson(New With {.text = $"[Agentic] {e.EventType}: {e.Summary}", .eventType = e.EventType.ToString(), .level = e.Level.ToString(),
                                                             .agentId = e.AgentId, .timestampUtc = e.TimestampUtc, .summary = e.Summary})
                        Try
                            Using content As New StringContent(body, Encoding.UTF8, "application/json")
                                Using resp = Await _http.PostAsync(_opt.WebhookUrl, content, ct).ConfigureAwait(False)
                                End Using
                            End Using
                        Catch ex As OperationCanceledException When ct.IsCancellationRequested
                            Return
                        Catch ex As Exception
                            _log.Warn("Alerts", "Webhook delivery failed: " & ex.Message)
                        End Try
                    End While
                End While
            Catch ex As OperationCanceledException
            End Try
        End Function
    End Class

End Namespace
