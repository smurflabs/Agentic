Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Events
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Tools

    ' =============================================================================================
    ' Autonomy tools: reach a human out-of-band, ask for budget, pursue goals, author/reuse skills,
    ' and propose a change to the agent's own behaviour (approval-gated).
    ' =============================================================================================

    ''' <summary>Sends a message to a human through a configured channel (chat webhook or email). Does not pause the task.</summary>
    Public NotInheritable Class NotifyHumanTool
        Inherits InternalTool

        Private ReadOnly _notify As NotificationService
        Private ReadOnly _channels As List(Of String)

        Public Sub New(notify As NotificationService)
            _notify = notify
            _channels = notify.ChannelNames()
        End Sub

        Public Overrides ReadOnly Property Name As String = "notify_human"
        Public Overrides ReadOnly Property Description As String =
            "Send a message to a human out-of-band (chat or email), for example when you are blocked or have finished something important. This does NOT pause the task; use ask_human when you need an answer back."

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides ReadOnly Property Parameters As JsonObject
            Get
                Return Schema.Obj({"message"},
                    Schema.Prop("message", Schema.Str("What to tell the human", 2000)),
                    Schema.Prop("subject", Schema.Str("Short subject", 200)),
                    Schema.Prop("channel", Schema.OneOf("Which channel (default the first configured)", _channels)))
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim r = Await _notify.NotifyAsync(JsonUtil.GetString(args, "channel"), JsonUtil.GetString(args, "subject"),
                                              JsonUtil.GetString(args, "message"), ctx.AgentId, ctx.CancellationToken).ConfigureAwait(False)
            Return If(r.Ok, ToolResult.Ok("Message sent to the human."), ToolResult.Fail(If(r.Err, "Could not send.")))
        End Function
    End Class

    ''' <summary>Asks an administrator to add money to the agent's wallet. Returns at once; the credit only appears if approved.</summary>
    Public NotInheritable Class RequestBudgetTool
        Inherits InternalTool

        Public Overrides ReadOnly Property Name As String = "request_budget"
        Public Overrides ReadOnly Property Description As String =
            "Ask for money to be added to your wallet, with a justification and the value you expect to deliver. Small requests within your budget policy are approved at once; " &
            "others go to your supervisor or an administrator. Returns immediately; the money appears only once approved."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"amount_usd", "justification"},
            Schema.Prop("amount_usd", Schema.Num("Amount requested, in USD", 0.01, 1000)),
            Schema.Prop("justification", Schema.Str("Why you need it and the value it will produce", 1500)))

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim usd = JsonUtil.GetDecimal(args, "amount_usd", 0D)
            If usd <= 0D Then Return ToolResult.Fail("amount_usd must be greater than zero.")
            Dim micros = CLng(usd * 1000000D)
            Dim justification = JsonUtil.GetString(args, "justification", "")
            Dim context = JsonUtil.ToJson(New With {.micros = micros, .amountUsd = usd, .justification = justification})
            Dim rec = Await ctx.Scope.Approvals.AskAsync(ctx.CurrentTask?.Id, ctx.ToolCallId, ApprovalKind.BudgetRequest,
                                                         "Budget request", $"Agent {ctx.AgentId} requests ${usd} of budget.", context,
                                                         DateTime.UtcNow.AddDays(7)).ConfigureAwait(False)
            Select Case rec.Status
                Case ApprovalStatus.Approved
                    Return ToolResult.Ok($"Approved ({DecidedText(rec)}): ${usd} was added to your wallet.")
                Case ApprovalStatus.Rejected
                    Return ToolResult.Ok($"Rejected ({DecidedText(rec)}): {rec.Response}")
                Case Else
                    Return ToolResult.Ok($"Budget request submitted (id {rec.Id}). {WhoDecides(rec)} must approve it before the money is added.")
            End Select
        End Function

        ''' <summary>"by your policy", "by agent X", "by an administrator".</summary>
        Friend Shared Function DecidedText(rec As Approvals.ApprovalRecord) As String
            Dim by = If(rec.DecidedBy, "human")
            If by = "policy" Then Return "by your policy"
            If by.StartsWith("agent:", StringComparison.Ordinal) Then Return "by " & by.Substring(6)
            If by = "timeout" Then Return "nobody answered in time"
            Return "by an administrator"
        End Function

        ''' <summary>Who decides a pending request.</summary>
        Friend Shared Function WhoDecides(rec As Approvals.ApprovalRecord) As String
            Return If(rec.StagedAgent IsNot Nothing, $"Your supervisor {rec.StagedAgent}", "An administrator")
        End Function
    End Class

    ''' <summary>Lists the agent's standing goals and their progress.</summary>
    Public NotInheritable Class ListGoalsTool
        Inherits InternalTool

        Private ReadOnly _goals As GoalService

        Public Sub New(goals As GoalService)
            _goals = goals
        End Sub

        Public Overrides ReadOnly Property Name As String = "list_goals"
        Public Overrides ReadOnly Property Description As String = "List your standing goals (long-running objectives) and how far along each is."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)())

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim goals = Await _goals.ListGoalsAsync(ctx.AgentId, "active").ConfigureAwait(False)
            If goals.Count = 0 Then Return ToolResult.Ok("You have no active goals.")
            Return ToolResult.OkJson(goals.Select(Function(g) New With {.id = g.Id, .title = g.Title, .detail = g.Detail, .metric = g.Metric,
                                                                        .progressPct = g.ProgressPct, .progress = g.Progress}).ToList())
        End Function
    End Class

    ''' <summary>Records the agent's progress against one of its goals.</summary>
    Public NotInheritable Class RecordGoalProgressTool
        Inherits InternalTool

        Private ReadOnly _goals As GoalService

        Public Sub New(goals As GoalService)
            _goals = goals
        End Sub

        Public Overrides ReadOnly Property Name As String = "record_goal_progress"
        Public Overrides ReadOnly Property Description As String = "Record where you have got to on a standing goal (from list_goals)."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"goal_id", "progress"},
            Schema.Prop("goal_id", Schema.Str("Goal id from list_goals", 100)),
            Schema.Prop("progress", Schema.Str("A concise progress note", 2000)),
            Schema.Prop("percent", Schema.Int("How complete the goal is now, 0-100", 0, 100)),
            Schema.Prop("done", Schema.Bool("Set true only if the goal is fully achieved")))

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim pct As Integer? = Nothing
            Dim pe As JsonElement
            If args.TryGetProperty("percent", pe) AndAlso pe.ValueKind = JsonValueKind.Number Then pct = pe.GetInt32()
            Dim ok = Await _goals.RecordProgressAsync(ctx.AgentId, JsonUtil.GetString(args, "goal_id"), JsonUtil.GetString(args, "progress"),
                                                      pct, JsonUtil.GetBool(args, "done", False)).ConfigureAwait(False)
            Return If(ok, ToolResult.Ok("Progress recorded."), ToolResult.Fail("No such goal."))
        End Function
    End Class

    ''' <summary>Saves a reusable procedure the agent can load later (instructions only, never code).</summary>
    Public NotInheritable Class AuthorSkillTool
        Inherits InternalTool

        Private ReadOnly _skills As SkillStore

        Public Sub New(skills As SkillStore)
            _skills = skills
        End Sub

        Public Overrides ReadOnly Property Name As String = "author_skill"
        Public Overrides ReadOnly Property Description As String =
            "Save a reusable procedure (a checklist or method you worked out) under a short name, so you can load it with use_skill next time. Instructions only — never executable code."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"name", "instructions"},
            Schema.Prop("name", Schema.Str("Short, unique skill name", 100)),
            Schema.Prop("description", Schema.Str("One-line summary", 500)),
            Schema.Prop("instructions", Schema.Str("The procedure to follow when this skill is used", 8000)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Try
                Dim s = Await _skills.SaveAsync(ctx.AgentId, JsonUtil.GetString(args, "name"), JsonUtil.GetString(args, "description"),
                                                JsonUtil.GetString(args, "instructions")).ConfigureAwait(False)
                Return ToolResult.Ok($"Saved skill '{s.Name}'. Load it later with use_skill.")
            Catch ex As InvalidOperationException
                Return ToolResult.Fail(ex.Message)
            End Try
        End Function
    End Class

    ''' <summary>Lists the agent's authored skills.</summary>
    Public NotInheritable Class ListSkillsTool
        Inherits InternalTool

        Private ReadOnly _skills As SkillStore

        Public Sub New(skills As SkillStore)
            _skills = skills
        End Sub

        Public Overrides ReadOnly Property Name As String = "list_skills"
        Public Overrides ReadOnly Property Description As String = "List the reusable skills you have authored."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj(Array.Empty(Of String)())

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim skills = Await _skills.ListAsync(ctx.AgentId).ConfigureAwait(False)
            If skills.Count = 0 Then Return ToolResult.Ok("You have not authored any skills yet.")
            Return ToolResult.OkJson(skills.Select(Function(s) New With {.name = s.Name, .description = s.Description, .uses = s.Uses}).ToList())
        End Function
    End Class

    ''' <summary>Loads an authored skill's instructions into the task.</summary>
    Public NotInheritable Class UseSkillTool
        Inherits InternalTool

        Private ReadOnly _skills As SkillStore

        Public Sub New(skills As SkillStore)
            _skills = skills
        End Sub

        Public Overrides ReadOnly Property Name As String = "use_skill"
        Public Overrides ReadOnly Property Description As String = "Load one of your authored skills (from list_skills) so you can follow its procedure now."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"name"},
            Schema.Prop("name", Schema.Str("Skill name from list_skills", 100)))

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim s = Await _skills.UseAsync(ctx.AgentId, JsonUtil.GetString(args, "name")).ConfigureAwait(False)
            If s Is Nothing Then Return ToolResult.Fail("No such skill.")
            Return ToolResult.Ok($"Skill '{s.Name}':" & vbLf & s.Instructions)
        End Function
    End Class

    ''' <summary>Proposes a change to the agent's own standing behaviour; applied only after an administrator approves.</summary>
    Public NotInheritable Class ProposeProfileChangeTool
        Inherits InternalTool

        Private ReadOnly _proposals As ProfileProposalService

        Public Sub New(proposals As ProfileProposalService)
            _proposals = proposals
        End Sub

        Public Overrides ReadOnly Property Name As String = "propose_profile_change"
        Public Overrides ReadOnly Property Description As String =
            "Propose a lasting change to your own behaviour — a rule or focus you want to always follow. Once approved (by an administrator, or by your policy or supervisor if you are autonomous) it is appended to your standing instructions; an evaluation may roll it back if it makes you worse. This CANNOT change your permissions, tools or models."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({"change", "rationale"},
            Schema.Prop("change", Schema.Str("The behaviour to adopt, phrased as an instruction to yourself", 2000)),
            Schema.Prop("rationale", Schema.Str("Why this will make you more effective", 1000)))

        Public Overrides ReadOnly Property IsIdempotent As Boolean
            Get
                Return False
            End Get
        End Property

        Public Overrides Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim change = JsonUtil.GetString(args, "change", "")
            Dim rationale = JsonUtil.GetString(args, "rationale", "")
            If String.IsNullOrWhiteSpace(change) Then Return ToolResult.Fail("Describe the behaviour change.")
            ' The proposal exists before the request, so a policy that approves at once can apply it.
            Dim approvalId = Ids.NewId("apr")
            Await _proposals.CreateAsync(ctx.AgentId, "behavior", change, rationale, approvalId).ConfigureAwait(False)
            Dim rec = Await ctx.Scope.Approvals.AskAsync(ctx.CurrentTask?.Id, ctx.ToolCallId, ApprovalKind.ProfileChange,
                                                         "Self-modification", $"Agent {ctx.AgentId} proposes a behaviour change.",
                                                         change & vbLf & "Why: " & rationale, DateTime.UtcNow.AddDays(7), approvalId).ConfigureAwait(False)
            Select Case rec.Status
                Case ApprovalStatus.Approved
                    Return ToolResult.Ok($"Applied ({RequestBudgetTool.DecidedText(rec)}): it is part of your standing instructions from your next task. " &
                                         "If you have evaluation cases they run now, and a change that makes you worse is rolled back.")
                Case ApprovalStatus.Rejected
                    Return ToolResult.Ok($"Rejected ({RequestBudgetTool.DecidedText(rec)}): {rec.Response}")
                Case Else
                    Return ToolResult.Ok($"Proposal submitted (id {rec.Id}). It takes effect only when approved; {RequestBudgetTool.WhoDecides(rec)} decides.")
            End Select
        End Function
    End Class

End Namespace
