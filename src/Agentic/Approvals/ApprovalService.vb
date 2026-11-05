Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Events
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Approvals

    Public Class ApprovalRecord
        Public Property Id As String
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property ToolCallId As String
        Public Property Kind As ApprovalKind
        Public Property Title As String
        Public Property Question As String
        Public Property Context As String
        Public Property Status As ApprovalStatus
        Public Property Response As String
        Public Property CreatedUtc As DateTime
        Public Property ResolvedUtc As DateTime?
        Public Property ResolvedBy As String
        Public Property ExpiresUtc As DateTime?
        ''' <summary>Who decided: human | policy | agent:&lt;id&gt; | timeout | host.</summary>
        Public Property DecidedBy As String
        ''' <summary>Why the policy or supervisor decided as it did, or where the request went.</summary>
        Public Property Reason As String
        ''' <summary>Nothing = waiting for a person; "supervisor:&lt;agent&gt;" = sent to that agent to decide.</summary>
        Public Property Stage As String
        ''' <summary>For a tool request that runs a command: how risky the command looks (Low, Medium, High) and why. Not stored.</summary>
        Public Property Risk As String
        Public Property RiskReason As String
        ''' <summary>The supervisor's decision task while staged.</summary>
        Public Property StageTaskId As String

        Public Function WaitKey() As String
            Return "approval:" & Id
        End Function

        ''' <summary>The agent a staged request waits for (Nothing when a person decides).</summary>
        Public ReadOnly Property StagedAgent As String
            Get
                If Stage Is Nothing OrElse Not Stage.StartsWith("supervisor:", StringComparison.Ordinal) Then Return Nothing
                Return Stage.Substring(11)
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Human-in-the-loop requests (ask_human, tool approvals, budget, behaviour changes, hires). The requesting task parks in
    ''' Waiting until someone decides: a person in the dashboard, the agent's policy (within its limits) or its supervisor agent.
    ''' The answer is delivered back to the task when it resumes.
    ''' </summary>
    Public NotInheritable Class ApprovalService
        Private ReadOnly _db As Database
        Private ReadOnly _events As IAgentEventBus
        Private _onResolved As Action(Of ApprovalRecord)

        Public Sub New(db As Database, events As IAgentEventBus)
            _db = db
            _events = events
            Rules = New ApprovalRuleStore(db)
        End Sub

        ''' <summary>Standing approvals: tool calls they cover are approved the moment they are asked for.</summary>
        Public ReadOnly Property Rules As ApprovalRuleStore

        ''' <summary>The tool a tool approval is for ("Approve {tool}?").</summary>
        Public Shared Function ToolOf(rec As ApprovalRecord) As String
            Dim m = Text.RegularExpressions.Regex.Match(If(rec?.Title, ""), "^Approve (.+)\?$")
            Return If(m.Success, m.Groups(1).Value.Trim(), Nothing)
        End Function

        ''' <summary>
        ''' Approves the waiting tool requests a new rule covers (people often get the same request many times in a row), and
        ''' returns how many.
        ''' </summary>
        Public Async Function ApprovePendingCoveredByAsync(rule As ApprovalRule, actor As String) As Task(Of Integer)
            Dim pending = Await _db.QueryAsync("SELECT * FROM Approvals WHERE Status='Pending' AND Kind='ToolApproval' ORDER BY CreatedUtc LIMIT 500", AddressOf Map).ConfigureAwait(False)
            Dim n = 0
            For Each a In pending
                Dim tool = ToolOf(a)
                If tool Is Nothing OrElse Not rule.CoversAgent(a.AgentId) OrElse Not rule.CoversTool(tool) Then Continue For
                If rule.Mode = "safe" Then
                    Dim cmd = CommandRisk.CommandOf(tool, a.Context)
                    If cmd Is Nothing OrElse CommandRisk.Classify(cmd).Level <> CommandRiskLevel.Low Then Continue For
                End If
                If Await ResolveAsync(a.Id, ApprovalStatus.Approved, "Approved by a standing rule.", actor, "policy", $"Covered by the rule just added ({rule.Describe()}).").ConfigureAwait(False) IsNot Nothing Then n += 1
            Next
            Return n
        End Function

        ''' <summary>Hook used by the host to wake the owning agent when an approval is resolved.</summary>
        Friend Sub SetResolvedCallback(callback As Action(Of ApprovalRecord))
            _onResolved = callback
        End Sub

        ''' <summary>
        ''' Runs on every new request before anyone is told about it: the agent's policy may decide it at once or send it to a
        ''' supervisor agent. Returns the request as it is afterwards.
        ''' </summary>
        Friend Property CreatedHook As Func(Of ApprovalRecord, Task(Of ApprovalRecord))

        ''' <summary>
        ''' For a task id: the task whose "approve tool use automatically" covers it (itself or its workflow), or Nothing.
        ''' Tool approvals of such tasks are approved at once.
        ''' </summary>
        Friend Property TaskAutoApproval As Func(Of String, Task(Of String))

        ''' <summary>Applies what a decision does (credit a budget, apply a behaviour change, create a hire) before the task resumes.</summary>
        Friend Property EffectHook As Func(Of ApprovalRecord, Task)

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ApprovalRecord
            Return New ApprovalRecord With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .TaskId = Db.Str(r, "TaskId"), .ToolCallId = Db.Str(r, "ToolCallId"),
                .Kind = Db.EnumOf(r, "Kind", ApprovalKind.AskHuman), .Title = Db.Str(r, "Title"), .Question = Db.Str(r, "Question"),
                .Context = Db.Str(r, "Context"), .Status = Db.EnumOf(r, "Status", ApprovalStatus.Pending), .Response = Db.Str(r, "Response"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"), .ResolvedUtc = Db.DtN(r, "ResolvedUtc"), .ResolvedBy = Db.Str(r, "ResolvedBy"),
                .ExpiresUtc = Db.DtN(r, "ExpiresUtc"), .DecidedBy = Db.Str(r, "DecidedBy"), .Reason = Db.Str(r, "Reason"),
                .Stage = Db.Str(r, "Stage"), .StageTaskId = Db.Str(r, "StageTaskId")}
        End Function

        ''' <param name="id">An id the caller chose (to link a record to it before policies decide); Nothing = a new one.</param>
        Friend Async Function CreateAsync(agentId As String, taskId As String, toolCallId As String, kind As ApprovalKind, title As String, question As String, context As String,
                                          expiresUtc As DateTime?, Optional id As String = Nothing) As Task(Of ApprovalRecord)
            Dim rec As New ApprovalRecord With {
                .Id = If(id, Ids.NewId("apr")), .AgentId = agentId, .TaskId = taskId, .ToolCallId = toolCallId, .Kind = kind,
                .Title = TextUtil.Truncate(title, 200), .Question = TextUtil.Truncate(question, 4000), .Context = TextUtil.Truncate(context, 8000),
                .Status = ApprovalStatus.Pending, .CreatedUtc = DateTime.UtcNow, .ExpiresUtc = expiresUtc}
            Await _db.ExecuteAsync("INSERT INTO Approvals(Id, AgentId, TaskId, ToolCallId, Kind, Title, Question, Context, Status, CreatedUtc, ExpiresUtc) " &
                                   "VALUES (@id, @a, @t, @tc, @k, @ti, @q, @c, 'Pending', @cr, @e)",
                                   ("@id", CObj(rec.Id)), ("@a", agentId), ("@t", taskId), ("@tc", toolCallId), ("@k", kind), ("@ti", rec.Title),
                                   ("@q", rec.Question), ("@c", rec.Context), ("@cr", Db.Ts(rec.CreatedUtc)), ("@e", Db.Ts(expiresUtc))).ConfigureAwait(False)
            ' The task was created with "approve tool use automatically" (it, or the task whose work this is): approved at once.
            Dim auto = TaskAutoApproval
            If kind = ApprovalKind.ToolApproval AndAlso auto IsNot Nothing AndAlso Not String.IsNullOrEmpty(taskId) Then
                Dim source = Await auto(taskId).ConfigureAwait(False)
                If source IsNot Nothing Then
                    Dim reason = If(source = taskId, $"Task {taskId} was started with ""approve tool use automatically"".",
                                    $"It is part of task {source}'s work, which was started with ""approve tool use automatically"".")
                    Dim approved = Await ResolveAsync(rec.Id, ApprovalStatus.Approved, "Approved automatically: the task allows every tool.", "task:" & source, "policy", reason).ConfigureAwait(False)
                    If approved IsNot Nothing Then Return approved
                End If
            End If
            ' A standing rule (set from the Inbox) approves a covered tool call at once, before any policy or person sees it.
            If kind = ApprovalKind.ToolApproval Then
                Dim tool = ToolOf(rec)
                If tool IsNot Nothing Then
                    Dim match = Await Rules.MatchAsync(agentId, tool, context, DateTime.UtcNow).ConfigureAwait(False)
                    If match.Rule IsNot Nothing Then
                        Dim approved = Await ResolveAsync(rec.Id, ApprovalStatus.Approved, "Approved automatically by a standing rule.", "rule:" & match.Rule.Id, "policy", match.Reason).ConfigureAwait(False)
                        If approved IsNot Nothing Then Return approved
                    End If
                End If
            End If
            Dim hook = CreatedHook
            If hook IsNot Nothing Then
                Try
                    rec = If(Await hook(rec).ConfigureAwait(False), rec)
                Catch ex As Exception
                    _events.Emit(AgentEventType.ApprovalRequested, $"Approval policy failed for {rec.Id}: {ex.Message}", agentId, taskId, level:=EventLevel.Warning)
                End Try
            End If
            If rec.Status = ApprovalStatus.Pending Then
                Dim staged = rec.StagedAgent
                _events.Emit(AgentEventType.ApprovalRequested, If(staged Is Nothing, $"{kind}: {rec.Title}", $"{kind}: {rec.Title} (sent to {staged})"), agentId, taskId,
                             level:=If(staged Is Nothing, EventLevel.Warning, EventLevel.Info),
                             data:=New Dictionary(Of String, Object) From {{"approvalId", rec.Id}, {"stage", rec.Stage}})
            End If
            Return rec
        End Function

        Public Function GetAsync(id As String) As Task(Of ApprovalRecord)
            Return _db.QuerySingleAsync("SELECT * FROM Approvals WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Public Function ListAsync(Optional status As ApprovalStatus? = Nothing, Optional limit As Integer = 100, Optional agentId As String = Nothing) As Task(Of List(Of ApprovalRecord))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If status.HasValue Then where.Add("Status=@s") : ps.Add(("@s", status.Value.ToString()))
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a") : ps.Add(("@a", agentId))
            ps.Add(("@l", Math.Clamp(limit, 1, 500)))
            Return _db.QueryAsync("SELECT * FROM Approvals" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY CreatedUtc DESC LIMIT @l", AddressOf Map, ps.ToArray())
        End Function

        ''' <summary>Requests waiting for a person (not the ones a supervisor agent is deciding).</summary>
        Public Function CountPendingAsync() As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM Approvals WHERE Status='Pending' AND Stage IS NULL")
        End Function

        ''' <summary>Pending requests a supervisor agent is deciding.</summary>
        Public Function ListStagedAsync() As Task(Of List(Of ApprovalRecord))
            Return _db.QueryAsync("SELECT * FROM Approvals WHERE Status='Pending' AND Stage IS NOT NULL ORDER BY CreatedUtc LIMIT 200", AddressOf Map)
        End Function

        ''' <summary>Pending requests of one kind created before a time (unanswered questions).</summary>
        Public Function ListPendingBeforeAsync(kind As ApprovalKind, beforeUtc As DateTime) As Task(Of List(Of ApprovalRecord))
            Return _db.QueryAsync("SELECT * FROM Approvals WHERE Status='Pending' AND Kind=@k AND CreatedUtc <= @b ORDER BY CreatedUtc LIMIT 200", AddressOf Map,
                                  ("@k", CObj(kind.ToString())), ("@b", Db.Ts(beforeUtc)))
        End Function

        ''' <summary>Hands a pending request to a supervisor agent (stage "supervisor:&lt;id&gt;") or back to people (stage Nothing).</summary>
        Public Async Function StageAsync(id As String, stage As String, stageTaskId As String, reason As String) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("UPDATE Approvals SET Stage=@s, StageTaskId=@t, Reason=@r WHERE Id=@id AND Status='Pending'",
                                           ("@s", CObj(stage)), ("@t", stageTaskId), ("@r", TextUtil.Truncate(reason, 1000)), ("@id", id)).ConfigureAwait(False)) > 0
        End Function

        ''' <summary>Adds to a decided request's response (for example the id of the agent a hire created).</summary>
        Public Async Function AppendResponseAsync(rec As ApprovalRecord, text As String) As Task
            If String.IsNullOrWhiteSpace(text) Then Return
            rec.Response = If(String.IsNullOrWhiteSpace(rec.Response), text, rec.Response & vbLf & text)
            Await _db.ExecuteAsync("UPDATE Approvals SET Response=@r WHERE Id=@id", ("@r", CObj(TextUtil.Truncate(rec.Response, 8000))), ("@id", rec.Id)).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Resolves a pending approval. Returns Nothing if it was not pending. decidedBy: human (default for a person), policy,
        ''' agent:&lt;id&gt;, timeout or host.
        ''' </summary>
        Public Async Function ResolveAsync(id As String, status As ApprovalStatus, response As String, actor As String,
                                           Optional decidedBy As String = Nothing, Optional reason As String = Nothing) As Task(Of ApprovalRecord)
            If status = ApprovalStatus.Pending Then Throw New ArgumentException("Cannot resolve to Pending.")
            Dim by = If(decidedBy, If(String.Equals(actor, "host", StringComparison.Ordinal), "host", "human"))
            Dim n = Await _db.ExecuteAsync("UPDATE Approvals SET Status=@s, Response=@r, ResolvedUtc=@now, ResolvedBy=@by, DecidedBy=@db, Reason=COALESCE(@why, Reason) " &
                                           "WHERE Id=@id AND Status='Pending'",
                                           ("@s", CObj(status.ToString())), ("@r", TextUtil.Truncate(response, 8000)), ("@now", Db.Ts(DateTime.UtcNow)),
                                           ("@by", actor), ("@db", by), ("@why", TextUtil.Truncate(reason, 1000)), ("@id", id)).ConfigureAwait(False)
            If n = 0 Then Return Nothing
            Dim rec = Await GetAsync(id).ConfigureAwait(False)
            _events.Emit(AgentEventType.ApprovalResolved, $"{rec.Kind} '{rec.Title}' {status} by {actor}", rec.AgentId, rec.TaskId,
                         data:=New Dictionary(Of String, Object) From {{"approvalId", id}, {"status", status.ToString()}, {"decidedBy", by}})
            Dim effect = EffectHook
            If effect IsNot Nothing Then
                Try
                    Await effect(rec).ConfigureAwait(False)
                Catch ex As Exception
                    _events.Emit(AgentEventType.ApprovalResolved, $"Could not apply the decision on {rec.Id}: {ex.Message}", rec.AgentId, rec.TaskId, level:=EventLevel.Warning)
                End Try
            End If
            _onResolved?.Invoke(rec)
            Return rec
        End Function

        ''' <summary>
        ''' Pending approvals whose task has ended (cancelled, failed or completed): no one can use the answer any more, so they
        ''' leave the inbox instead of waiting there for a day. Requests that never had a task (budget, behaviour changes) stay.
        ''' </summary>
        Public Async Function CancelOrphanedAsync() As Task(Of Integer)
            Dim orphans = Await _db.QueryAsync("SELECT a.* FROM Approvals a JOIN AgentTasks t ON t.Id = a.TaskId " &
                                               "WHERE a.Status='Pending' AND t.Status IN ('Completed', 'Failed', 'Cancelled') AND a.Kind IN ('AskHuman', 'ToolApproval', 'HireAgent') LIMIT 100",
                                               AddressOf Map).ConfigureAwait(False)
            Dim n = 0
            For Each a In orphans
                If Await ResolveAsync(a.Id, ApprovalStatus.Cancelled, "The task ended before anyone answered.", "host").ConfigureAwait(False) IsNot Nothing Then n += 1
            Next
            Return n
        End Function

        Public Async Function ExpireDueAsync(now As DateTime) As Task(Of List(Of ApprovalRecord))
            Dim due = Await _db.QueryAsync("SELECT * FROM Approvals WHERE Status='Pending' AND ExpiresUtc IS NOT NULL AND ExpiresUtc <= @now LIMIT 100", AddressOf Map, ("@now", CObj(Db.Ts(now)))).ConfigureAwait(False)
            Dim resolved As New List(Of ApprovalRecord)
            For Each a In due
                Dim r = Await ResolveAsync(a.Id, ApprovalStatus.Expired, "No answer before the deadline.", "host", "timeout").ConfigureAwait(False)
                If r IsNot Nothing Then resolved.Add(r)
            Next
            Return resolved
        End Function
    End Class

End Namespace
