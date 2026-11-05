Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.AI
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Memory
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Tools

    ' =============================================================================================
    ' Resolution payloads delivered to parked tool calls
    ' =============================================================================================

    Public Module Resolutions
        Public Function AgentReply(m As AgentMessage) As String
            Dim contract As TaskResultContract = Nothing
            If Not String.IsNullOrEmpty(m.StructuredPayload) Then
                Try
                    contract = JsonUtil.FromJson(Of TaskResultContract)(m.StructuredPayload)
                Catch
                End Try
            End If
            Dim o As New Dictionary(Of String, Object) From {
                {"kind", "agent_reply"}, {"from", m.SenderAgentId}, {"messageType", m.MessageType.ToString()}, {"messageId", m.Id},
                {"success", If(contract IsNot Nothing, contract.Success, m.MessageType <> MessageType.Error)},
                {"summary", If(contract?.Summary, m.Content)}}
            If contract?.Data IsNot Nothing Then o("data") = contract.Data
            If contract?.Warnings IsNot Nothing AndAlso contract.Warnings.Count > 0 Then o("warnings") = contract.Warnings
            If m.Attachments IsNot Nothing AndAlso m.Attachments.Count > 0 Then o("attachments") = m.Attachments
            Return JsonUtil.ToJson(o)
        End Function

        Public Function Timeout(message As String) As String
            Return JsonUtil.ToJson(New Dictionary(Of String, Object) From {{"kind", "timeout"}, {"success", False}, {"message", message}})
        End Function

        ''' <summary>decidedBy: who answered when it was not a person (agent:&lt;id&gt;, policy or timeout).</summary>
        Public Function HumanAnswer(status As String, response As String, Optional decidedBy As String = Nothing) As String
            Dim o As New Dictionary(Of String, Object) From {{"kind", "human_answer"}, {"status", status}, {"response", response}}
            If Not String.IsNullOrEmpty(decidedBy) AndAlso decidedBy <> "human" Then o("answeredBy") = decidedBy
            Return JsonUtil.ToJson(o)
        End Function

        Public Function Approval(status As String, response As String, Optional decidedBy As String = Nothing) As String
            Dim o As New Dictionary(Of String, Object) From {{"kind", "approval"}, {"status", status}, {"response", response}}
            If Not String.IsNullOrEmpty(decidedBy) Then o("decidedBy") = decidedBy
            Return JsonUtil.ToJson(o)
        End Function

        ''' <summary>A finished task delivered to the agent that assigned it (assign_task with wait_for_result).</summary>
        Public Function TaskResult(t As AgentTask) As String
            Dim result = t.GetResult()
            Dim o As New Dictionary(Of String, Object) From {
                {"kind", "task_result"}, {"task", t.Id}, {"agent", t.AgentId}, {"name", t.Name}, {"status", t.Status.ToString()},
                {"success", t.Status = AgentTaskStatus.Completed AndAlso result IsNot Nothing AndAlso result.Success},
                {"summary", If(result?.Summary, If(t.ErrorMessage, t.Status.ToString()))}}
            If result?.Data IsNot Nothing Then o("data") = result.Data
            Return JsonUtil.ToJson(o)
        End Function

        ''' <summary>Result of a finished subagent, delivered to the parent's waiting tool call.</summary>
        Public Function SubagentResult(subagentId As String, name As String, status As String, result As TaskResultContract,
                                       artifacts As IEnumerable(Of String), spentMicros As Long) As String
            Dim o As New Dictionary(Of String, Object) From {
                {"kind", "subagent_result"}, {"subagent", subagentId}, {"name", name}, {"status", status},
                {"success", result IsNot Nothing AndAlso result.Success AndAlso status = "Completed"},
                {"summary", If(result?.Summary, status)}, {"cloudCost", TextUtil.FormatMicros(spentMicros)}}
            If result?.Data IsNot Nothing Then o("data") = result.Data
            If result?.Warnings IsNot Nothing AndAlso result.Warnings.Count > 0 Then o("warnings") = result.Warnings
            Dim arts = If(artifacts, Enumerable.Empty(Of String)()).ToList()
            If arts.Count > 0 Then o("artifacts") = arts
            Return JsonUtil.ToJson(o)
        End Function

        Public Function KindOf(resolutionJson As String) As String
            Dim el As JsonElement
            If JsonUtil.TryParseElement(resolutionJson, el) Then Return JsonUtil.GetString(el, "kind")
            Return Nothing
        End Function
    End Module

    ' =============================================================================================
    ' Memory tools
    ' =============================================================================================

    Public NotInheritable Class RememberTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "remember"
        Public Overrides ReadOnly Property Description As String = "Store an important fact, decision, lesson or note in your private long-term memory. Only you can read it."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"content"},
            Schema.Prop("content", Schema.Str("What to remember (concise, self-contained).", 4000)),
            Schema.Prop("category", Schema.OneOf("Memory category", [Enum].GetNames(Of MemoryCategory)())),
            Schema.Prop("tags", Schema.Str("Comma-separated keywords", 300)),
            Schema.Prop("importance", Schema.Int("1 (low) to 5 (critical)", 1, 5)))

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim cat As MemoryCategory = MemoryCategory.Fact
            [Enum].TryParse(JsonUtil.GetString(args, "category", "Fact"), True, cat)
            Dim item = Await ctx.Scope.Memory.SaveMemoryAsync(cat, JsonUtil.GetString(args, "content"), JsonUtil.GetString(args, "tags"),
                                                              JsonUtil.GetInt(args, "importance", 3), ctx.CurrentTask?.Id).ConfigureAwait(False)
            Return ToolResult.Ok($"Saved memory {item.Id}.")
        End Function
    End Class

    Public NotInheritable Class RecallTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "recall"
        Public Overrides ReadOnly Property Description As String = "Search your private long-term memory by keywords."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"query"},
            Schema.Prop("query", Schema.Str("Keywords to search for", 500)),
            Schema.Prop("category", Schema.OneOf("Optional category filter", [Enum].GetNames(Of MemoryCategory)())),
            Schema.Prop("limit", Schema.Int("Max results (default 8)", 1, 30)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim catText = JsonUtil.GetString(args, "category")
            Dim cat As MemoryCategory
            Dim catFilter As MemoryCategory? = If(catText IsNot Nothing AndAlso [Enum].TryParse(catText, True, cat), cat, CType(Nothing, MemoryCategory?))
            Dim items = Await ctx.Scope.Memory.SearchMemoriesAsync(JsonUtil.GetString(args, "query"), catFilter, JsonUtil.GetInt(args, "limit", 8)).ConfigureAwait(False)
            If items.Count = 0 Then Return ToolResult.Ok("No matching memories.")
            Return ToolResult.OkJson(items.Select(Function(m) New With {.id = m.Id, .category = m.Category.ToString(), .content = m.Content, .tags = m.Tags,
                                                                        .importance = m.Importance, .createdUtc = m.CreatedUtc}).ToList())
        End Function
    End Class

    Public NotInheritable Class ForgetTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "forget"
        Public Overrides ReadOnly Property Description As String = "Delete one of your memories that is wrong or obsolete."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"memory_id"}, Schema.Prop("memory_id", Schema.Str("Memory id from recall", 100)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim ok = Await ctx.Scope.Memory.DeleteMemoryAsync(JsonUtil.GetString(args, "memory_id")).ConfigureAwait(False)
            Return If(ok, ToolResult.Ok("Memory deleted."), ToolResult.Fail("No such memory."))
        End Function
    End Class

    ' =============================================================================================
    ' Task tools
    ' =============================================================================================

    Public NotInheritable Class CompleteTaskTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "complete_task"
        Public Overrides ReadOnly Property Description As String = "Finish the current task and report the outcome honestly. Call this exactly once when the work is done or cannot be done."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"success", "summary"},
            Schema.Prop("success", Schema.Bool("True only if the task was actually accomplished.")),
            Schema.Prop("summary", Schema.Str("Concise result summary (or the complete answer when the administrator reads it), or why it failed.", 30000)),
            Schema.Prop("data", Schema.AnyObject("Optional structured result data.")),
            Schema.Prop("warnings", Schema.StrArray("Optional caveats.")))

        Public Overrides Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim dataEl As JsonElement
            Dim data As JsonElement? = Nothing
            If args.TryGetProperty("data", dataEl) AndAlso dataEl.ValueKind = JsonValueKind.Object Then data = dataEl.Clone()
            Dim contract As New TaskResultContract With {
                .Success = JsonUtil.GetBool(args, "success", False), .Summary = JsonUtil.GetString(args, "summary", ""),
                .Data = data, .Warnings = JsonUtil.GetStringList(args, "warnings")}
            Return Task.FromResult(New ToolResult With {.Success = True, .Content = "Task marked complete.", .Signal = ToolSignal.Complete, .Completion = contract})
        End Function
    End Class

    Public NotInheritable Class ReportProgressTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "report_progress"
        Public Overrides ReadOnly Property Description As String = "Record a short human-readable progress note in your activity log (visible to administrators)."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"note"}, Schema.Prop("note", Schema.Str("Progress note", 500)))

        Friend Property Emit As Action(Of String, String, String)

        Public Overrides Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Emit?.Invoke(ctx.AgentId, ctx.CurrentTask?.Id, JsonUtil.GetString(args, "note"))
            Return Task.FromResult(ToolResult.Ok("Noted."))
        End Function
    End Class

    Public NotInheritable Class CreateFollowUpTaskTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "create_followup_task"
        Public Overrides ReadOnly Property Description As String = "Schedule a follow-up task in your own queue (e.g. to check something again later)."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"name", "description"},
            Schema.Prop("name", Schema.Str("Short task name", 200)),
            Schema.Prop("description", Schema.Str("What to do", 4000)),
            Schema.Prop("delay_minutes", Schema.Int("Start after this many minutes (default 0)", 0, 10080)),
            Schema.Prop("priority", Schema.OneOf("Priority", {"Low", "Normal", "High"})))

        Private ReadOnly _maxChildren As Integer

        Public Sub New(maxChildren As Integer)
            _maxChildren = maxChildren
        End Sub

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim pr As PriorityLevel = PriorityLevel.Normal
            [Enum].TryParse(JsonUtil.GetString(args, "priority", "Normal"), True, pr)
            If pr > PriorityLevel.High Then pr = PriorityLevel.High
            Dim delay = JsonUtil.GetInt(args, "delay_minutes", 0)
            Try
                Dim t = Await ctx.Scope.Queue.CreateFollowUpAsync(ctx.CurrentTask, New NewTaskRequest With {
                    .Name = JsonUtil.GetString(args, "name"), .Description = JsonUtil.GetString(args, "description"), .Priority = pr,
                    .NextRunUtc = If(delay > 0, DateTime.UtcNow.AddMinutes(delay), CType(Nothing, DateTime?)),
                    .IdempotencyKey = $"{ctx.CurrentTask?.Id}:{ctx.ToolCallId}"}, _maxChildren).ConfigureAwait(False)
                Return ToolResult.Ok($"Follow-up task {t.Id} created" & If(delay > 0, $" (starts in {delay} min).", "."))
            Catch ex As InvalidOperationException
                Return ToolResult.Fail(ex.Message)
            End Try
        End Function
    End Class

    ' =============================================================================================
    ' Communication tools (only built when the agent has permitted recipients)
    ' =============================================================================================

    Public MustInherit Class CommunicationTool
        Inherits InternalTool

        Protected ReadOnly Recipients As List(Of String)

        Protected Sub New(recipients As List(Of String))
            Me.Recipients = recipients
        End Sub

        Public Overrides ReadOnly Property Source As ToolSource
            Get
                Return ToolSource.Communication
            End Get
        End Property

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                ' Sends are idempotent per (task, tool call id) via message idempotency keys.
                Return True
            End Get
        End Property

        Protected Shared Function ParsePriority(args As JsonElement) As PriorityLevel
            Dim pr As PriorityLevel = PriorityLevel.Normal
            [Enum].TryParse(JsonUtil.GetString(args, "priority", "Normal"), True, pr)
            Return pr
        End Function

        Protected Shared Function PayloadOf(args As JsonElement) As String
            Return JsonUtil.GetRaw(args, "payload")
        End Function

        Protected Shared Function FormatSendError(r As SendResult) As ToolResult
            Return ToolResult.Fail($"{r.ErrorCode}: {r.ErrorMessage}")
        End Function

        ''' <summary>Waits briefly for a reply; returns the reply message or Nothing.</summary>
        Protected Shared Async Function InlineWaitAsync(ctx As ToolInvocationContext, messageId As String, seconds As Integer) As Task(Of AgentMessage)
            Dim deadline = DateTime.UtcNow.AddSeconds(Math.Max(0, seconds))
            While DateTime.UtcNow < deadline
                Dim reply = Await ctx.Scope.Comms.FindReplyAsync(messageId).ConfigureAwait(False)
                If reply IsNot Nothing Then
                    Await ctx.Scope.Comms.ConsumeReplyAsync(reply.Id).ConfigureAwait(False)
                    Return reply
                End If
                Await Task.Delay(750, ctx.CancellationToken).ConfigureAwait(False)
            End While
            Return Nothing
        End Function

        Protected Shared Function ReplyResult(reply As AgentMessage) As ToolResult
            Return New ToolResult With {.Success = True, .Content = Resolutions.AgentReply(reply), .Untrusted = True, .UntrustedSource = "agent:" & reply.SenderAgentId}
        End Function
    End Class

    Public NotInheritable Class ListContactsTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "list_contacts"
        Public Overrides ReadOnly Property Description As String = "List the agents you are permitted to communicate with, their roles and current status."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)())

        Public Overrides Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Return Task.FromResult(ToolResult.OkJson(ctx.Scope.Comms.GetContacts()))
        End Function
    End Class

    Public NotInheritable Class SendAgentMessageTool
        Inherits CommunicationTool

        Public Sub New(recipients As List(Of String))
            MyBase.New(recipients)
        End Sub

        Public Overrides ReadOnly Property Name As String = "send_agent_message"
        Public Overrides ReadOnly Property Description As String = "Send a one-way informational message or notification to another agent (no reply expected)."

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"recipient", "subject", "content"},
                    Schema.Prop("recipient", Schema.OneOf("Agent id", Recipients)),
                    Schema.Prop("subject", Schema.Str("Short subject", 300)),
                    Schema.Prop("content", Schema.Str("Message text")),
                    Schema.Prop("payload", Schema.AnyObject("Optional structured JSON payload")),
                    Schema.Prop("priority", Schema.OneOf("Priority", {"Low", "Normal", "High", "Critical"})),
                    Schema.Prop("attachments", Schema.StrArray("Artifact ids you own to share with the recipient")))
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim r = Await ctx.Scope.Comms.SendMessageAsync(ctx.SendContext(), New OutgoingMessage With {
                .RecipientAgentId = JsonUtil.GetString(args, "recipient"), .MessageType = MessageType.Information,
                .Subject = JsonUtil.GetString(args, "subject"), .Content = JsonUtil.GetString(args, "content"), .PayloadJson = PayloadOf(args),
                .Priority = ParsePriority(args), .Attachments = JsonUtil.GetStringList(args, "attachments")}, ctx.CancellationToken).ConfigureAwait(False)
            If Not r.Success Then Return FormatSendError(r)
            Return ToolResult.Ok($"Message {r.MessageId} sent.")
        End Function
    End Class

    Public NotInheritable Class RequestAgentHelpTool
        Inherits CommunicationTool

        Public Sub New(recipients As List(Of String))
            MyBase.New(recipients)
        End Sub

        Public Overrides ReadOnly Property Name As String = "request_agent_help"
        Public Overrides ReadOnly Property Description As String = "Ask another agent a question or request an answer/analysis. You will receive their structured reply; your task waits (without blocking other work) until it arrives or times out."

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"recipient", "subject", "question"},
                    Schema.Prop("recipient", Schema.OneOf("Agent id", Recipients)),
                    Schema.Prop("subject", Schema.Str("Short subject", 300)),
                    Schema.Prop("question", Schema.Str("What you need from them, with all necessary context")),
                    Schema.Prop("payload", Schema.AnyObject("Optional structured JSON payload")),
                    Schema.Prop("timeout_seconds", Schema.Int("Max time to wait for the answer (default 7200 = 2 hours; the host always waits at least 1 hour, since the other agent may need a while)", 30, 14400)),
                    Schema.Prop("priority", Schema.OneOf("Priority", {"Low", "Normal", "High", "Critical"})),
                    Schema.Prop("attachments", Schema.StrArray("Artifact ids you own to share")))
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim timeoutSec = JsonUtil.GetInt(args, "timeout_seconds", 7200)
            Dim r = Await ctx.Scope.Comms.SendRequestAsync(ctx.SendContext(), New OutgoingMessage With {
                .RecipientAgentId = JsonUtil.GetString(args, "recipient"), .MessageType = MessageType.Request,
                .Subject = JsonUtil.GetString(args, "subject"), .Content = JsonUtil.GetString(args, "question"), .PayloadJson = PayloadOf(args),
                .Priority = ParsePriority(args), .TimeoutSeconds = timeoutSec, .Attachments = JsonUtil.GetStringList(args, "attachments")}, ctx.CancellationToken).ConfigureAwait(False)
            If Not r.Success Then Return FormatSendError(r)
            Dim reply = Await InlineWaitAsync(ctx, r.MessageId, ctx.Scope.Comms.InlineWaitSeconds).ConfigureAwait(False)
            If reply IsNot Nothing Then Return ReplyResult(reply)
            Return ToolResult.Park("msg:" & r.MessageId, If(r.ExpiresUtc, DateTime.UtcNow.AddSeconds(timeoutSec)).AddSeconds(30),
                                   $"Request {r.MessageId} sent to {JsonUtil.GetString(args, "recipient")}; waiting for the reply.")
        End Function
    End Class

    Public NotInheritable Class DelegateTaskTool
        Inherits CommunicationTool

        Public Sub New(recipients As List(Of String))
            MyBase.New(recipients)
        End Sub

        Public Overrides ReadOnly Property Name As String = "delegate_task"
        Public Overrides ReadOnly Property Description As String =
            "Assign a task to another agent. It becomes a task in their queue; they return a structured result. Set wait_for_result=true to pause until it is done." &
            " A bounty_usd (from your wallet) is paid to them when the work is done successfully."

        ''' <summary>Checks a bounty the agent offers (payer, micro-USD): Nothing when it can be offered, else why not. Nothing = no bounties.</summary>
        Public Property BountyCheck As Func(Of String, Long, Task(Of String))

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Dim props As New List(Of KeyValuePair(Of String, JsonNode)) From {
                    Schema.Prop("recipient", Schema.OneOf("Agent id", Recipients)),
                    Schema.Prop("task_name", Schema.Str("Short task name", 200)),
                    Schema.Prop("instructions", Schema.Str("Complete instructions and context for the task")),
                    Schema.Prop("payload", Schema.AnyObject("Optional structured input data")),
                    Schema.Prop("wait_for_result", Schema.Bool("Pause this task until the delegated task finishes (default true)")),
                    Schema.Prop("timeout_seconds", Schema.Int("Max time to wait for the result (default 7200 = 2 hours; the host always waits at least 1 hour)", 60, 86400)),
                    Schema.Prop("priority", Schema.OneOf("Priority", {"Low", "Normal", "High", "Critical"})),
                    Schema.Prop("attachments", Schema.StrArray("Artifact ids you own to share"))}
                If BountyCheck IsNot Nothing Then props.Add(Schema.Prop("bounty_usd", Schema.Num("Optional reward from your wallet, paid when the work succeeds", 0, 1000)))
                Return Schema.Obj({"recipient", "task_name", "instructions"}, props.ToArray())
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim wait = JsonUtil.GetBool(args, "wait_for_result", True)
            Dim timeoutSec = JsonUtil.GetInt(args, "timeout_seconds", 7200)
            Dim bounty = If(BountyCheck Is Nothing, 0L, TextUtil.DollarsToMicros(Math.Max(0D, JsonUtil.GetDecimal(args, "bounty_usd", 0D))))
            If bounty > 0 Then
                Dim why = Await BountyCheck(ctx.Profile.BillingAgentId, bounty).ConfigureAwait(False)
                If why IsNot Nothing Then Return ToolResult.Fail(why)
            End If
            Dim r = Await ctx.Scope.Comms.DelegateTaskAsync(ctx.SendContext(), New OutgoingMessage With {
                .RecipientAgentId = JsonUtil.GetString(args, "recipient"), .TaskName = JsonUtil.GetString(args, "task_name"),
                .Subject = JsonUtil.GetString(args, "task_name"), .Content = JsonUtil.GetString(args, "instructions"), .PayloadJson = PayloadOf(args),
                .Priority = ParsePriority(args), .TimeoutSeconds = timeoutSec, .Attachments = JsonUtil.GetStringList(args, "attachments"),
                .Delivery = If(bounty > 0, New TaskDelivery With {.BountyMicros = bounty, .BountyFrom = ctx.Profile.BillingAgentId}, Nothing)}, ctx.CancellationToken).ConfigureAwait(False)
            If Not r.Success Then Return FormatSendError(r)
            If Not wait Then Return ToolResult.Ok($"Delegated as task {r.TaskId} (message {r.MessageId}). The result will arrive in your mailbox later.")
            Return ToolResult.Park("msg:" & r.MessageId, If(r.ExpiresUtc, DateTime.UtcNow.AddSeconds(timeoutSec)).AddSeconds(30),
                                   $"Delegated as task {r.TaskId}; waiting for the result.")
        End Function
    End Class

    Public NotInheritable Class ReplyToAgentTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "reply_to_agent"
        Public Overrides ReadOnly Property Description As String = "Reply to a message another agent sent you (normally your task result is sent automatically; use this to answer early or a different message)."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"message_id", "success", "summary"},
            Schema.Prop("message_id", Schema.Str("Id of the message you are answering", 100)),
            Schema.Prop("success", Schema.Bool("Whether you could fulfil the request")),
            Schema.Prop("summary", Schema.Str("Your answer")),
            Schema.Prop("data", Schema.AnyObject("Optional structured data")))

        Public Overrides ReadOnly Property Source As ToolSource
            Get
                Return ToolSource.Communication
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim dataEl As JsonElement
            Dim data As JsonElement? = Nothing
            If args.TryGetProperty("data", dataEl) AndAlso dataEl.ValueKind = JsonValueKind.Object Then data = dataEl.Clone()
            Dim r = Await ctx.Scope.Comms.ReplyAsync(JsonUtil.GetString(args, "message_id"), New TaskResultContract With {
                .Success = JsonUtil.GetBool(args, "success", True), .Summary = JsonUtil.GetString(args, "summary"), .Data = data}, ctx.CancellationToken).ConfigureAwait(False)
            If Not r.Success Then Return ToolResult.Fail($"{r.ErrorCode}: {r.ErrorMessage}")
            Return ToolResult.Ok(If(r.Duplicate, "This message was already answered.", "Reply sent."))
        End Function
    End Class

    Public NotInheritable Class BroadcastTool
        Inherits InternalTool

        Private ReadOnly _groups As List(Of String)

        Public Sub New(groups As List(Of String))
            _groups = groups
        End Sub

        Public Overrides ReadOnly Property Name As String = "broadcast_notification"
        Public Overrides ReadOnly Property Description As String = "Send a notification to every member of a communication group you belong to."

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"group", "subject", "content"},
                    Schema.Prop("group", Schema.OneOf("Group name", _groups)),
                    Schema.Prop("subject", Schema.Str("Subject", 300)),
                    Schema.Prop("content", Schema.Str("Notification text", 4000)))
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim results = Await ctx.Scope.Comms.BroadcastAsync(ctx.SendContext(), JsonUtil.GetString(args, "group"), JsonUtil.GetString(args, "subject"),
                                                               JsonUtil.GetString(args, "content"), ctx.CancellationToken).ConfigureAwait(False)
            Dim ok = results.Where(Function(r) r.Success).Count()
            Dim failed = results.Where(Function(r) Not r.Success).Select(Function(r) r.ErrorCode).ToList()
            Return ToolResult.Ok($"Delivered to {ok} agent(s)." & If(failed.Count > 0, " Failures: " & String.Join(", ", failed), ""))
        End Function
    End Class

    ' =============================================================================================
    ' Budget / escalation
    ' =============================================================================================

    Public NotInheritable Class GetMyBudgetTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "get_my_budget"
        Public Overrides ReadOnly Property Description As String = "Show your AI budget: balance, spend today and lifetime, profit credited, and the prices of the models you may use."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)())

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Return ToolResult.Ok(Await ctx.Scope.Budget.DescribeAsync(True).ConfigureAwait(False))
        End Function
    End Class

    Public NotInheritable Class RequestEscalationTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "request_escalation"
        Public Overrides ReadOnly Property Description As String = "Ask the host to continue this task with a stronger (more expensive) model because the current one cannot handle it. The host decides based on your permissions and budget."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"reason"}, Schema.Prop("reason", Schema.Str("Why a stronger model is needed", 1000)))

        Public Overrides Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Return Task.FromResult(New ToolResult With {.Success = True, .Signal = ToolSignal.Escalate, .EscalationReason = JsonUtil.GetString(args, "reason"),
                                                        .Content = "Escalation requested; the host will decide."})
        End Function
    End Class

    ' =============================================================================================
    ' Artifacts
    ' =============================================================================================

    Public NotInheritable Class SaveArtifactTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "save_artifact"
        Public Overrides ReadOnly Property Description As String = "Store text content (report, data, code) as an artifact you own. Share it with other agents by listing its id in a message's attachments."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"name", "content"},
            Schema.Prop("name", Schema.Str("File name", 200)),
            Schema.Prop("content", Schema.Str("Text content")),
            Schema.Prop("mime_type", Schema.Str("MIME type (default text/plain)", 100)),
            Schema.Prop("description", Schema.Str("Short description", 500)))

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Try
                Dim info = Await ctx.Scope.Artifacts.SaveAsync(JsonUtil.GetString(args, "name"), JsonUtil.GetString(args, "mime_type", "text/plain"),
                                                               Encoding.UTF8.GetBytes(JsonUtil.GetString(args, "content", "")), JsonUtil.GetString(args, "description"),
                                                               ctx.CurrentTask?.Id).ConfigureAwait(False)
                Return ToolResult.Ok($"Saved artifact {info.Id} ({info.SizeBytes} bytes).")
            Catch ex As ArtifactAccessException
                Return ToolResult.Fail(ex.Message)
            Catch ex As ArgumentException
                Return ToolResult.Fail(ex.Message)
            End Try
        End Function
    End Class

    Public NotInheritable Class ReadArtifactTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "read_artifact"
        Public Overrides ReadOnly Property Description As String = "Read an artifact you own or that was shared with you. Text, code, Word, Excel, PowerPoint, PDF, RTF and ZIP files are returned as text; use analyze_image for images."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"artifact_id"},
            Schema.Prop("artifact_id", Schema.Str("Artifact id", 100)),
            Schema.Prop("max_chars", Schema.Int("Maximum characters to return (default 8000)", 100, 50000)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Try
                Dim a = Await ctx.Scope.Artifacts.ReadAsync(JsonUtil.GetString(args, "artifact_id")).ConfigureAwait(False)
                Dim mime = If(a.Info.MimeType, "")
                If mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) OrElse mime.Contains("json") OrElse mime.Contains("xml") OrElse mime.Contains("csv") Then
                    Dim text = Encoding.UTF8.GetString(a.Data)
                    Return New ToolResult With {.Success = True, .Content = TextUtil.Truncate(text, JsonUtil.GetInt(args, "max_chars", 8000)),
                                                .Untrusted = True, .UntrustedSource = "artifact:" & a.Info.Id}
                End If
                If mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase) Then
                    Return ToolResult.Ok($"Artifact {a.Info.Id} '{a.Info.Name}' is an image ({FileExtractor.Extract(a.Info.Name, a.Data).Summary}). Use analyze_image to look at it.")
                End If
                ' Documents, spreadsheets, presentations, PDFs, archives and source files: their text.
                Dim ex = FileExtractor.Extract(a.Info.Name, a.Data, JsonUtil.GetInt(args, "max_chars", 8000))
                If String.IsNullOrWhiteSpace(ex.Text) Then Return ToolResult.Ok($"Artifact {a.Info.Id} '{a.Info.Name}': {ex.Summary}. It has no readable text.")
                Return New ToolResult With {.Success = True, .Content = $"{a.Info.Name}: {ex.Summary}" & vbLf & ex.Text,
                                            .Untrusted = True, .UntrustedSource = "artifact:" & a.Info.Id}
            Catch ex As ArtifactAccessException
                Return ToolResult.Fail(ex.Message)
            End Try
        End Function
    End Class

    Public NotInheritable Class ListArtifactsTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "list_artifacts"
        Public Overrides ReadOnly Property Description As String = "List artifacts you own or that were shared with you."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)())

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim list = Await ctx.Scope.Artifacts.ListAsync(50).ConfigureAwait(False)
            Return ToolResult.OkJson(list.Select(Function(a) New With {.id = a.Id, .name = a.Name, .mimeType = a.MimeType, .sizeBytes = a.SizeBytes,
                                                                       .owner = a.OwnerAgentId, .description = a.Description, .createdUtc = a.CreatedUtc}).ToList())
        End Function
    End Class

    ' =============================================================================================
    ' Vision / image generation
    ' =============================================================================================

    Public NotInheritable Class AnalyzeImageTool
        Inherits InternalTool

        Private ReadOnly _maxBytes As Integer

        Public Sub New(maxBytes As Integer)
            _maxBytes = maxBytes
        End Sub

        Public Overrides ReadOnly Property Name As String = "analyze_image"
        Public Overrides ReadOnly Property Description As String = "Have a vision model look at an image (an artifact id or an https URL) and answer a question about it. Costs budget."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"question"},
            Schema.Prop("artifact_id", Schema.Str("Image artifact id (owned or shared with you)", 100)),
            Schema.Prop("image_url", Schema.Str("Public https URL of the image", 2000)),
            Schema.Prop("question", Schema.Str("What you want to know about the image", 4000)),
            Schema.Prop("expert", Schema.Bool("Use the expert (more capable, more expensive) vision model")))

        Public Overrides ReadOnly Property Source As ToolSource
            Get
                Return ToolSource.AiCapability
            End Get
        End Property

        Public Overrides ReadOnly Property Timeout As TimeSpan
            Get
                Return TimeSpan.FromMinutes(6)
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim imageRef As String
            Dim artifactId = JsonUtil.GetString(args, "artifact_id")
            Dim url = JsonUtil.GetString(args, "image_url")
            If Not String.IsNullOrEmpty(artifactId) Then
                Try
                    Dim a = Await ctx.Scope.Artifacts.ReadAsync(artifactId).ConfigureAwait(False)
                    If Not If(a.Info.MimeType, "").StartsWith("image/", StringComparison.OrdinalIgnoreCase) Then Return ToolResult.Fail("Artifact is not an image.")
                    If a.Data.Length > _maxBytes Then Return ToolResult.Fail($"Image exceeds {_maxBytes \ 1024} KB.")
                    imageRef = $"data:{a.Info.MimeType};base64,{Convert.ToBase64String(a.Data)}"
                Catch ex As ArtifactAccessException
                    Return ToolResult.Fail(ex.Message)
                End Try
            ElseIf Not String.IsNullOrEmpty(url) Then
                Dim u As Uri = Nothing
                If Not Uri.TryCreate(url, UriKind.Absolute, u) OrElse u.Scheme <> Uri.UriSchemeHttps Then Return ToolResult.Fail("image_url must be an absolute https URL.")
                imageRef = u.ToString()
            Else
                Return ToolResult.Fail("Provide artifact_id or image_url.")
            End If
            Try
                Dim outcome = Await ctx.Scope.Vision.AnalyzeAsync(ctx.Profile, ctx.CurrentTask?.Id, ctx.CurrentTask?.CorrelationId, If(ctx.CurrentTask?.Priority, PriorityLevel.Normal),
                                                                  imageRef, JsonUtil.GetString(args, "question"), JsonUtil.GetBool(args, "expert", False), ctx.CancellationToken).ConfigureAwait(False)
                If ctx.CurrentTask IsNot Nothing Then ctx.CurrentTask.CostMicros += outcome.CostMicros
                Return New ToolResult With {.Success = True, .Content = If(outcome.Response.Content, "(no description)") & $" [vision model: {outcome.ModelKey}]",
                                            .Untrusted = True, .UntrustedSource = "vision"}
            Catch ex As AiException
                Return ToolResult.Fail($"Vision failed ({ex.Kind}): {ex.Message}")
            End Try
        End Function
    End Class

    Public NotInheritable Class GenerateImageTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "generate_image"
        Public Overrides ReadOnly Property Description As String = "Generate an image from a text prompt. The image is saved as an artifact you own. Costs budget."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"prompt"},
            Schema.Prop("prompt", Schema.Str("Detailed description of the image", 4000)),
            Schema.Prop("size", Schema.Str("e.g. 512x512 or 1024x1024 (default from config)", 20)))

        Public Overrides ReadOnly Property Source As ToolSource
            Get
                Return ToolSource.AiCapability
            End Get
        End Property

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property Timeout As TimeSpan
            Get
                Return TimeSpan.FromMinutes(6)
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Try
                Dim info = Await ctx.Scope.Images.GenerateAsync(ctx.Profile, ctx.CurrentTask?.Id, ctx.CurrentTask?.CorrelationId, JsonUtil.GetString(args, "prompt"),
                                                                JsonUtil.GetString(args, "size"), ctx.CancellationToken).ConfigureAwait(False)
                Return ToolResult.Ok($"Image generated and saved as artifact {info.Id} ({info.MimeType}, {info.SizeBytes \ 1024} KB). Use analyze_image to check it or attach it to a message.")
            Catch ex As AiException
                Return ToolResult.Fail($"Image generation failed ({ex.Kind}): {ex.Message}")
            Catch ex As ArtifactAccessException
                Return ToolResult.Fail(ex.Message)
            End Try
        End Function
    End Class

    ' =============================================================================================
    ' Human in the loop
    ' =============================================================================================

    Public NotInheritable Class AskHumanTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "ask_human"
        Public Overrides ReadOnly Property Description As String = "Ask a human administrator a question (decision, missing information, approval). The task waits until they answer in the dashboard."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"question"},
            Schema.Prop("question", Schema.Str("Your question", 4000)),
            Schema.Prop("context", Schema.Str("Relevant context for the human", 8000)),
            Schema.Prop("timeout_minutes", Schema.Int("How long to wait (default 1440)", 5, 20160)))

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim minutes = JsonUtil.GetInt(args, "timeout_minutes", 1440)
            Dim deadline = DateTime.UtcNow.AddMinutes(minutes)
            Dim question = JsonUtil.GetString(args, "question")
            Dim rec = Await ctx.Scope.Approvals.AskAsync(ctx.CurrentTask?.Id, ctx.ToolCallId, ApprovalKind.AskHuman, TextUtil.Truncate(question, 120),
                                                         question, JsonUtil.GetString(args, "context"), deadline).ConfigureAwait(False)
            ' Answered already (by the agent's policy): no need to wait.
            If rec.Status <> ApprovalStatus.Pending Then
                Return New ToolResult With {.Success = True, .Untrusted = True, .UntrustedSource = "answer", .Content = If(rec.Response, rec.Status.ToString())}
            End If
            Dim who = If(rec.StagedAgent IsNot Nothing, $"your supervisor {rec.StagedAgent}", "the administrators")
            Return ToolResult.Park(rec.WaitKey(), deadline.AddSeconds(30), $"Question {rec.Id} sent to {who}; waiting for an answer.")
        End Function
    End Class

End Namespace
