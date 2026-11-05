Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Events
Imports Agentic.Utilities

Namespace Persistence

    Public Class AiCallRecord
        Public Property Id As String
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property ModelKey As String
        Public Property ModelId As String
        Public Property Purpose As String
        Public Property Tier As Integer
        Public Property Reason As AiCallReason
        Public Property QueueWaitMs As Long
        Public Property DurationMs As Long
        Public Property Success As Boolean
        Public Property ErrorKind As String
        Public Property ErrorMessage As String
        Public Property PromptTokens As Long
        Public Property CompletionTokens As Long
        Public Property ReasoningTokens As Long
        Public Property UsageEstimated As Boolean
        Public Property CostMicros As Long
        Public Property InputPricePerM As Decimal?
        Public Property OutputPricePerM As Decimal?
        Public Property FinishReason As String
        Public Property RequestPreview As String
        Public Property ResponsePreview As String
        Public Property CreatedUtc As DateTime
    End Class

    Public Class ToolCallRecord
        Public Property Id As String
        Public Property ToolCallId As String
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property ToolName As String
        Public Property Source As ToolSource
        Public Property Arguments As String
        Public Property StartedUtc As DateTime
        Public Property CompletedUtc As DateTime?
        Public Property Success As Boolean?
        Public Property Result As String
        Public Property ErrorMessage As String
        Public Property DurationMs As Long?
    End Class

    Public Class ActivityRecord
        Public Property Id As Long
        Public Property EventType As String
        Public Property Level As String
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property MessageId As String
        Public Property CorrelationId As String
        Public Property Summary As String
        Public Property DataJson As String
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>Activity events, AI calls, tool calls and hourly statistics.</summary>
    Public NotInheritable Class TelemetryRepository
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        ' ---------------- Activity ----------------

        Public Async Function InsertActivityBatchAsync(events As IReadOnlyList(Of AgentEvent)) As Task
            If events.Count = 0 Then Return
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    For Each e In events
                        Await Database.TxExecAsync(conn, tx,
                            "INSERT INTO ActivityEvents(EventType, Level, AgentId, TaskId, MessageId, CorrelationId, Summary, DataJson, CreatedUtc) " &
                            "VALUES (@t, @l, @a, @task, @m, @c, @s, @d, @ts)",
                            ("@t", CObj(e.EventType.ToString())), ("@l", e.Level.ToString()), ("@a", e.AgentId), ("@task", e.TaskId), ("@m", e.MessageId),
                            ("@c", e.CorrelationId), ("@s", TextUtil.Truncate(e.Summary, 1000)),
                            ("@d", If(e.Data Is Nothing OrElse e.Data.Count = 0, Nothing, TextUtil.Truncate(JsonUtil.ToJson(e.Data), 4000))),
                            ("@ts", Db.Ts(e.TimestampUtc))).ConfigureAwait(False)
                    Next
                    Return True
                End Function).ConfigureAwait(False)
        End Function

        Public Function ListActivityAsync(agentId As String, limit As Integer, Optional correlationId As String = Nothing, Optional eventType As String = Nothing, Optional beforeId As Long = 0) As Task(Of List(Of ActivityRecord))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a") : ps.Add(("@a", agentId))
            If Not String.IsNullOrEmpty(correlationId) Then where.Add("CorrelationId=@c") : ps.Add(("@c", correlationId))
            If Not String.IsNullOrEmpty(eventType) Then where.Add("EventType=@t") : ps.Add(("@t", eventType))
            If beforeId > 0 Then where.Add("Id<@b") : ps.Add(("@b", beforeId))
            ps.Add(("@l", Math.Clamp(limit, 1, 1000)))
            Dim sql = "SELECT * FROM ActivityEvents" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY Id DESC LIMIT @l"
            Return _db.QueryAsync(sql, Function(r) New ActivityRecord With {
                .Id = Db.Lng(r, "Id"), .EventType = Db.Str(r, "EventType"), .Level = Db.Str(r, "Level"), .AgentId = Db.Str(r, "AgentId"),
                .TaskId = Db.Str(r, "TaskId"), .MessageId = Db.Str(r, "MessageId"), .CorrelationId = Db.Str(r, "CorrelationId"),
                .Summary = Db.Str(r, "Summary"), .DataJson = Db.Str(r, "DataJson"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}, ps.ToArray())
        End Function

        ' ---------------- AI calls ----------------

        Public Function InsertAiCallAsync(c As AiCallRecord) As Task(Of Integer)
            Return _db.ExecuteAsync(
                "INSERT INTO AiCalls(Id, AgentId, TaskId, CorrelationId, ModelKey, ModelId, Purpose, Tier, Reason, QueueWaitMs, DurationMs, Success, ErrorKind, Error, " &
                "PromptTokens, CompletionTokens, ReasoningTokens, UsageEstimated, CostMicros, InputPricePerM, OutputPricePerM, FinishReason, RequestPreview, ResponsePreview, CreatedUtc) " &
                "VALUES (@Id, @AgentId, @TaskId, @Corr, @MK, @MI, @P, @Tier, @Reason, @QW, @Dur, @S, @EK, @E, @PT, @CT, @RT, @UE, @Cost, @IP, @OP, @FR, @Req, @Resp, @C)",
                ("@Id", CObj(c.Id)), ("@AgentId", c.AgentId), ("@TaskId", c.TaskId), ("@Corr", c.CorrelationId), ("@MK", c.ModelKey), ("@MI", c.ModelId),
                ("@P", c.Purpose), ("@Tier", c.Tier), ("@Reason", c.Reason), ("@QW", c.QueueWaitMs), ("@Dur", c.DurationMs), ("@S", c.Success),
                ("@EK", c.ErrorKind), ("@E", TextUtil.Truncate(c.ErrorMessage, 2000)), ("@PT", c.PromptTokens), ("@CT", c.CompletionTokens),
                ("@RT", c.ReasoningTokens), ("@UE", c.UsageEstimated), ("@Cost", c.CostMicros),
                ("@IP", If(c.InputPricePerM.HasValue, CObj(c.InputPricePerM.Value), Nothing)), ("@OP", If(c.OutputPricePerM.HasValue, CObj(c.OutputPricePerM.Value), Nothing)),
                ("@FR", c.FinishReason), ("@Req", c.RequestPreview), ("@Resp", c.ResponsePreview), ("@C", Db.Ts(c.CreatedUtc)))
        End Function

        Public Function ListAiCallsAsync(agentId As String, limit As Integer, Optional taskId As String = Nothing) As Task(Of List(Of AiCallRecord))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a") : ps.Add(("@a", agentId))
            If Not String.IsNullOrEmpty(taskId) Then where.Add("TaskId=@t") : ps.Add(("@t", taskId))
            ps.Add(("@l", Math.Clamp(limit, 1, 500)))
            Dim sql = "SELECT * FROM AiCalls" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY CreatedUtc DESC LIMIT @l"
            Return _db.QueryAsync(sql, Function(r) New AiCallRecord With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .TaskId = Db.Str(r, "TaskId"), .CorrelationId = Db.Str(r, "CorrelationId"),
                .ModelKey = Db.Str(r, "ModelKey"), .ModelId = Db.Str(r, "ModelId"), .Purpose = Db.Str(r, "Purpose"), .Tier = Db.Int(r, "Tier"),
                .Reason = Db.EnumOf(r, "Reason", AiCallReason.Primary), .QueueWaitMs = Db.Lng(r, "QueueWaitMs"), .DurationMs = Db.Lng(r, "DurationMs"),
                .Success = Db.Bool(r, "Success"), .ErrorKind = Db.Str(r, "ErrorKind"), .ErrorMessage = Db.Str(r, "Error"),
                .PromptTokens = Db.Lng(r, "PromptTokens"), .CompletionTokens = Db.Lng(r, "CompletionTokens"), .ReasoningTokens = Db.Lng(r, "ReasoningTokens"),
                .UsageEstimated = Db.Bool(r, "UsageEstimated"), .CostMicros = Db.Lng(r, "CostMicros"), .InputPricePerM = Db.Dec(r, "InputPricePerM"),
                .OutputPricePerM = Db.Dec(r, "OutputPricePerM"), .FinishReason = Db.Str(r, "FinishReason"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}, ps.ToArray())
        End Function

        ''' <summary>Aggregates per model since a point in time (calls, failures, tokens, cost, avg latency, reasons).</summary>
        Public Function AiUsageByModelAsync(sinceUtc As DateTime, Optional agentId As String = Nothing) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim where = "WHERE CreatedUtc>=@s" & If(String.IsNullOrEmpty(agentId), "", " AND AgentId=@a")
            Return _db.QueryAsync(
                "SELECT ModelKey, COUNT(*) AS Calls, SUM(CASE WHEN Success=0 THEN 1 ELSE 0 END) AS Failures, SUM(PromptTokens) AS PT, SUM(CompletionTokens) AS CT, " &
                "SUM(CostMicros) AS Cost, AVG(DurationMs) AS AvgMs, AVG(QueueWaitMs) AS AvgQueue, " &
                "SUM(CASE WHEN Reason='Spillover' THEN 1 ELSE 0 END) AS Spill, SUM(CASE WHEN Reason='Escalation' THEN 1 ELSE 0 END) AS Esc, " &
                "SUM(CASE WHEN Reason='Fallback' THEN 1 ELSE 0 END) AS Fb " &
                $"FROM AiCalls {where} GROUP BY ModelKey ORDER BY Calls DESC",
                Function(r) New Dictionary(Of String, Object) From {
                    {"modelKey", Db.Str(r, "ModelKey")}, {"calls", Db.Lng(r, "Calls")}, {"failures", Db.Lng(r, "Failures")},
                    {"promptTokens", Db.Lng(r, "PT")}, {"completionTokens", Db.Lng(r, "CT")}, {"costMicros", Db.Lng(r, "Cost")},
                    {"avgDurationMs", If(r.IsDBNull(r.GetOrdinal("AvgMs")), 0.0, r.GetDouble(r.GetOrdinal("AvgMs")))},
                    {"avgQueueMs", If(r.IsDBNull(r.GetOrdinal("AvgQueue")), 0.0, r.GetDouble(r.GetOrdinal("AvgQueue")))},
                    {"spillovers", Db.Lng(r, "Spill")}, {"escalations", Db.Lng(r, "Esc")}, {"fallbacks", Db.Lng(r, "Fb")}},
                ("@s", CObj(Db.Ts(sinceUtc))), ("@a", agentId))
        End Function

        ''' <summary>Sum of calls and tokens since a time for an agent (used to seed usage limits after restart).</summary>
        Public Async Function AiUsageSinceAsync(agentId As String, sinceUtc As DateTime) As Task(Of (Calls As Long, Tokens As Long))
            Dim rows = Await _db.QueryAsync("SELECT COUNT(*) AS C, COALESCE(SUM(PromptTokens+CompletionTokens),0) AS T FROM AiCalls WHERE AgentId=@a AND CreatedUtc>=@s",
                                            Function(r) (Db.Lng(r, "C"), Db.Lng(r, "T")), ("@a", CObj(agentId)), ("@s", Db.Ts(sinceUtc))).ConfigureAwait(False)
            If rows.Count = 0 Then Return (0L, 0L)
            Return rows(0)
        End Function

        ''' <summary>An agent's most used MCP tools since a time, most used first (the ones its tasks start with loaded).</summary>
        Public Function TopMcpToolsAsync(agentId As String, sinceUtc As DateTime, limit As Integer) As Task(Of List(Of String))
            Return _db.QueryAsync("SELECT ToolName, COUNT(*) AS N FROM ToolCalls WHERE AgentId=@a AND StartedUtc>=@s AND ToolName LIKE 'mcp\_\_%' ESCAPE '\' " &
                                  "GROUP BY ToolName ORDER BY N DESC, ToolName LIMIT @l",
                                  Function(r) Db.Str(r, "ToolName"), ("@a", CObj(agentId)), ("@s", Db.Ts(sinceUtc)), ("@l", Math.Clamp(limit, 1, 100)))
        End Function

        ''' <summary>
        ''' An agent's AI calls and tokens since a time, per hour, each stamped with that hour's last call (UTC). Tokens of the
        ''' models in <paramref name="tokenFreeModels"/> (the local ones) are not counted; their calls are.
        ''' </summary>
        Public Function AiUsageByHourSinceAsync(agentId As String, sinceUtc As DateTime, Optional tokenFreeModels As IEnumerable(Of String) = Nothing) As Task(Of List(Of (HourUtc As DateTime, Calls As Long, Tokens As Long)))
            Dim keys = If(tokenFreeModels, Enumerable.Empty(Of String)()).Where(Function(k) Not String.IsNullOrEmpty(k)).Distinct().ToList()
            Dim ps As New List(Of (String, Object)) From {("@a", CObj(agentId)), ("@s", Db.Ts(sinceUtc))}
            For i = 0 To keys.Count - 1
                ps.Add(($"@k{i}", CObj(keys(i))))
            Next
            Dim tokens = If(keys.Count = 0, "PromptTokens+CompletionTokens",
                            $"CASE WHEN ModelKey IN ({String.Join(",", keys.Select(Function(k, i) $"@k{i}"))}) THEN 0 ELSE PromptTokens+CompletionTokens END")
            Return _db.QueryAsync($"SELECT MAX(CreatedUtc) AS L, COUNT(*) AS C, COALESCE(SUM({tokens}),0) AS T FROM AiCalls " &
                                  "WHERE AgentId=@a AND CreatedUtc>=@s GROUP BY substr(CreatedUtc,1,13) ORDER BY L",
                                  Function(r) (Db.Dt(r, "L"), Db.Lng(r, "C"), Db.Lng(r, "T")), ps.ToArray())
        End Function

        ' ---------------- Tool calls ----------------

        Public Function InsertToolCallAsync(t As ToolCallRecord) As Task(Of Integer)
            Return _db.ExecuteAsync(
                "INSERT INTO ToolCalls(Id, ToolCallId, AgentId, TaskId, ToolName, Source, Arguments, StartedUtc) VALUES (@Id, @TC, @A, @T, @N, @S, @Args, @St)",
                ("@Id", CObj(t.Id)), ("@TC", t.ToolCallId), ("@A", t.AgentId), ("@T", t.TaskId), ("@N", t.ToolName), ("@S", t.Source),
                ("@Args", TextUtil.Truncate(t.Arguments, 8000)), ("@St", Db.Ts(t.StartedUtc)))
        End Function

        Public Function CompleteToolCallAsync(id As String, success As Boolean, result As String, errorMessage As String, durationMs As Long) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE ToolCalls SET CompletedUtc=@c, Success=@s, Result=@r, Error=@e, DurationMs=@d WHERE Id=@id",
                                    ("@c", CObj(Db.Ts(DateTime.UtcNow))), ("@s", success), ("@r", TextUtil.Truncate(result, 4000)),
                                    ("@e", TextUtil.Truncate(errorMessage, 2000)), ("@d", durationMs), ("@id", id))
        End Function

        ''' <summary>Tool calls of a task that started but never completed (crash/restart in the middle of a call).</summary>
        Public Function ListIncompleteToolCallsAsync(taskId As String) As Task(Of List(Of ToolCallRecord))
            Return _db.QueryAsync("SELECT * FROM ToolCalls WHERE TaskId=@t AND CompletedUtc IS NULL", AddressOf MapTool, ("@t", CObj(taskId)))
        End Function

        Public Function ListToolCallsAsync(agentId As String, limit As Integer, Optional taskId As String = Nothing) As Task(Of List(Of ToolCallRecord))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a") : ps.Add(("@a", agentId))
            If Not String.IsNullOrEmpty(taskId) Then where.Add("TaskId=@t") : ps.Add(("@t", taskId))
            ps.Add(("@l", Math.Clamp(limit, 1, 500)))
            Return _db.QueryAsync("SELECT * FROM ToolCalls" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY StartedUtc DESC LIMIT @l",
                                  AddressOf MapTool, ps.ToArray())
        End Function

        Private Shared Function MapTool(r As Microsoft.Data.Sqlite.SqliteDataReader) As ToolCallRecord
            Return New ToolCallRecord With {
                .Id = Db.Str(r, "Id"), .ToolCallId = Db.Str(r, "ToolCallId"), .AgentId = Db.Str(r, "AgentId"), .TaskId = Db.Str(r, "TaskId"),
                .ToolName = Db.Str(r, "ToolName"), .Source = Db.EnumOf(r, "Source", ToolSource.Internal), .Arguments = Db.Str(r, "Arguments"),
                .StartedUtc = Db.Dt(r, "StartedUtc"), .CompletedUtc = Db.DtN(r, "CompletedUtc"), .Success = Db.BoolN(r, "Success"),
                .Result = Db.Str(r, "Result"), .ErrorMessage = Db.Str(r, "Error"), .DurationMs = Db.LngN(r, "DurationMs")}
        End Function

        ' ---------------- Statistics ----------------

        Public Async Function FlushStatisticsAsync(rows As IEnumerable(Of StatisticsDelta)) As Task
            Dim list = rows.ToList()
            If list.Count = 0 Then Return
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    For Each d In list
                        Await Database.TxExecAsync(conn, tx,
                            "INSERT INTO Statistics(AgentId, BucketUtc, TasksCompleted, TasksFailed, TasksRetried, AiCalls, AiFailures, AiLatencyMsTotal, ToolCalls, ToolFailures, " &
                            "ToolLatencyMsTotal, MessagesSent, MessagesReceived, Delegations, TaskDurationMsTotal, Tokens, CostMicros) " &
                            "VALUES (@a, @b, @tc, @tf, @tr, @ai, @af, @al, @to, @tof, @tl, @ms, @mr, @d, @td, @tok, @cost) " &
                            "ON CONFLICT(AgentId, BucketUtc) DO UPDATE SET TasksCompleted=TasksCompleted+@tc, TasksFailed=TasksFailed+@tf, TasksRetried=TasksRetried+@tr, " &
                            "AiCalls=AiCalls+@ai, AiFailures=AiFailures+@af, AiLatencyMsTotal=AiLatencyMsTotal+@al, ToolCalls=ToolCalls+@to, ToolFailures=ToolFailures+@tof, " &
                            "ToolLatencyMsTotal=ToolLatencyMsTotal+@tl, MessagesSent=MessagesSent+@ms, MessagesReceived=MessagesReceived+@mr, Delegations=Delegations+@d, " &
                            "TaskDurationMsTotal=TaskDurationMsTotal+@td, Tokens=Tokens+@tok, CostMicros=CostMicros+@cost",
                            ("@a", CObj(d.AgentId)), ("@b", d.BucketUtc), ("@tc", d.TasksCompleted), ("@tf", d.TasksFailed), ("@tr", d.TasksRetried),
                            ("@ai", d.AiCalls), ("@af", d.AiFailures), ("@al", d.AiLatencyMs), ("@to", d.ToolCalls), ("@tof", d.ToolFailures),
                            ("@tl", d.ToolLatencyMs), ("@ms", d.MessagesSent), ("@mr", d.MessagesReceived), ("@d", d.Delegations),
                            ("@td", d.TaskDurationMs), ("@tok", d.Tokens), ("@cost", d.CostMicros)).ConfigureAwait(False)
                    Next
                    Return True
                End Function).ConfigureAwait(False)
        End Function

        Public Function StatisticsAsync(agentId As String, sinceUtc As DateTime) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim where = "WHERE BucketUtc>=@s" & If(String.IsNullOrEmpty(agentId), "", " AND AgentId=@a")
            Return _db.QueryAsync(
                "SELECT BucketUtc, SUM(TasksCompleted) TC, SUM(TasksFailed) TF, SUM(AiCalls) AI, SUM(AiFailures) AF, SUM(ToolCalls) TO1, SUM(MessagesSent) MS, " &
                $"SUM(Tokens) TOK, SUM(CostMicros) COST FROM Statistics {where} GROUP BY BucketUtc ORDER BY BucketUtc",
                Function(r) New Dictionary(Of String, Object) From {
                    {"bucketUtc", Db.Str(r, "BucketUtc")}, {"tasksCompleted", Db.Lng(r, "TC")}, {"tasksFailed", Db.Lng(r, "TF")},
                    {"aiCalls", Db.Lng(r, "AI")}, {"aiFailures", Db.Lng(r, "AF")}, {"toolCalls", Db.Lng(r, "TO1")},
                    {"messagesSent", Db.Lng(r, "MS")}, {"tokens", Db.Lng(r, "TOK")}, {"costMicros", Db.Lng(r, "COST")}},
                ("@s", CObj(Db.Ts(sinceUtc))), ("@a", agentId))
        End Function
    End Class

    Public Class StatisticsDelta
        Public Property AgentId As String
        Public Property BucketUtc As String
        Public Property TasksCompleted As Long
        Public Property TasksFailed As Long
        Public Property TasksRetried As Long
        Public Property AiCalls As Long
        Public Property AiFailures As Long
        Public Property AiLatencyMs As Long
        Public Property ToolCalls As Long
        Public Property ToolFailures As Long
        Public Property ToolLatencyMs As Long
        Public Property MessagesSent As Long
        Public Property MessagesReceived As Long
        Public Property Delegations As Long
        Public Property TaskDurationMs As Long
        Public Property Tokens As Long
        Public Property CostMicros As Long
    End Class

End Namespace
