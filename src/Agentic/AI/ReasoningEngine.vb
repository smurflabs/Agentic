Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Mcp
Imports Agentic.Persistence
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work

Namespace AI

    ''' <summary>Builds the per-task tool registry for an agent from its permissions (the model sees nothing else).</summary>
    Public NotInheritable Class ToolCatalog
        Private ReadOnly _profiles As IProfileProvider
        Private ReadOnly _mcp As IMcpToolRegistry
        Private ReadOnly _vision As VisionService
        Private ReadOnly _images As ImageGenerationService
        Private ReadOnly _comm As CommunicationOptions
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _visionMaxBytes As Integer
        Private ReadOnly _subagents As SubagentOptions
        Private ReadOnly _access As ModelAccessService
        Private ReadOnly _selection As ModelSelectionService
        Private ReadOnly _pricing As Economy.PricingService
        Private ReadOnly _lessons As Learning.LessonStore
        Private ReadOnly _webSearch As Tools.WebSearchService
        ' Autonomy services, attached after construction (they depend on the agent manager / options built later).
        Private _notify As Global.Agentic.Events.NotificationService
        Private _goals As Work.GoalService
        Private _skills As Work.SkillStore
        Private _proposals As Work.ProfileProposalService
        Private _allowSelfMod As Boolean = True

        Public Sub New(profiles As IProfileProvider, mcp As IMcpToolRegistry, vision As VisionService, images As ImageGenerationService,
                       comm As CommunicationOptions, visionOptions As VisionOptions, events As IAgentEventBus,
                       Optional subagents As SubagentOptions = Nothing, Optional access As ModelAccessService = Nothing,
                       Optional selection As ModelSelectionService = Nothing, Optional pricing As Economy.PricingService = Nothing,
                       Optional lessons As Learning.LessonStore = Nothing, Optional webSearch As Tools.WebSearchService = Nothing)
            _lessons = lessons
            _webSearch = webSearch
            _profiles = profiles
            _mcp = mcp
            _vision = vision
            _images = images
            _comm = comm
            _events = events
            _visionMaxBytes = visionOptions.MaxImageBytes
            _subagents = If(subagents, New SubagentOptions With {.Enabled = False})
            _access = access
            _selection = selection
            _pricing = pricing
        End Sub

        ''' <summary>Wires the autonomy tools' services in once they exist (they depend on services built after this catalog).</summary>
        Public Sub AttachAutonomy(notify As Global.Agentic.Events.NotificationService, goals As Work.GoalService,
                                  skills As Work.SkillStore, proposals As Work.ProfileProposalService, allowSelfMod As Boolean)
            _notify = notify
            _goals = goals
            _skills = skills
            _proposals = proposals
            _allowSelfMod = allowSelfMod
        End Sub

        ' Fully autonomous agents: their action, self-direction and team tools.
        Private _autonomy As Work.AutonomyService
        Private _scheduler As Work.WorkScheduler
        Private _triggers As Work.TriggerService
        Private _vault As Work.SecretVault
        Private _rag As Work.RagService
        Private _ownerOf As Func(Of String, String)
        Private _runCode As Func(Of String, String, CancellationToken, Task(Of Work.CodeRunResult))
        Private _codeAvailable As Func(Of Boolean)
        Private _autonomyOptions As AutonomyOptions

        ''' <summary>Wires the tools of fully autonomous agents in (the services exist only after the agents do).</summary>
        Public Sub AttachAutonomyPlus(autonomy As Work.AutonomyService, scheduler As Work.WorkScheduler, triggers As Work.TriggerService, vault As Work.SecretVault,
                                      rag As Work.RagService, ownerOf As Func(Of String, String),
                                      runCode As Func(Of String, String, CancellationToken, Task(Of Work.CodeRunResult)), codeAvailable As Func(Of Boolean),
                                      options As AutonomyOptions)
            _autonomy = autonomy
            _scheduler = scheduler
            _triggers = triggers
            _vault = vault
            _rag = rag
            _ownerOf = ownerOf
            _runCode = runCode
            _codeAvailable = codeAvailable
            _autonomyOptions = options
        End Sub

        Private _markets As Work.MarketsService
        Private _news As Work.NewsService
        Private _stocks As Work.StocksService
        Private _polymarket As Work.PolymarketService

        ''' <summary>
        ''' The Money, News, Stocks and Polymarket tabs' data, for every agent's market_prices, news_articles, stock_quotes and
        ''' polymarket tools.
        ''' </summary>
        Public Sub AttachData(markets As Work.MarketsService, news As Work.NewsService, Optional stocks As Work.StocksService = Nothing,
                              Optional polymarket As Work.PolymarketService = Nothing)
            _markets = markets
            _news = news
            _stocks = stocks
            _polymarket = polymarket
        End Sub

        ''' <summary>Tools from the agent's autonomy level and settings (each only when what it needs is there).</summary>
        Private Sub AddAutonomyTools(reg As AgentToolRegistry, profile As EffectiveProfile, has As Func(Of String, Boolean))
            If has("update_plan") Then reg.Add(New UpdatePlanTool())
            If has("read_web_page") Then reg.Add(New ReadWebPageTool())
            If has("run_code") AndAlso _runCode IsNot Nothing AndAlso (_codeAvailable Is Nothing OrElse _codeAvailable()) Then reg.Add(New RunCodeTool(_runCode))
            If has("http_request") AndAlso profile.AllowedHttpHosts.Length > 0 AndAlso _autonomyOptions IsNot Nothing Then
                reg.Add(New HttpRequestTool(_vault, profile.AllowedHttpHosts, _autonomyOptions.HttpTimeoutSeconds, _autonomyOptions.MaxHttpResponseChars))
                If has("list_secrets") AndAlso _vault IsNot Nothing Then reg.Add(New ListSecretsTool(_vault))
            End If
            If has("search_knowledge") AndAlso _rag IsNot Nothing AndAlso profile.KnowledgeBases.Count > 0 Then reg.Add(New SearchKnowledgeTool(_rag, _ownerOf))
            If has(Work.MarketsService.ToolName) AndAlso _markets IsNot Nothing AndAlso _markets.Enabled Then reg.Add(New MarketPricesTool(_markets))
            If has(Work.NewsService.ToolName) AndAlso _news IsNot Nothing AndAlso _news.Enabled Then reg.Add(New NewsArticlesTool(_news))
            If has(Work.StocksService.ToolName) AndAlso _stocks IsNot Nothing AndAlso _stocks.Enabled Then reg.Add(New StockQuotesTool(_stocks))
            If has(Work.PolymarketService.ToolName) AndAlso _polymarket IsNot Nothing AndAlso _polymarket.Enabled Then reg.Add(New PolymarketTool(_polymarket))
            If profile.IsSubagent Then Return
            If _goals IsNot Nothing Then
                If has("set_goal") Then reg.Add(New SetGoalTool(_goals))
                If has("update_goal") Then reg.Add(New UpdateGoalTool(_goals))
            End If
            If _scheduler IsNot Nothing Then
                If has("schedule_task") Then reg.Add(New ScheduleTaskTool(_scheduler))
                If has("list_schedules") Then reg.Add(New ListSchedulesTool(_scheduler))
                If has("cancel_schedule") Then reg.Add(New CancelScheduleTool(_scheduler))
            End If
            If _triggers IsNot Nothing AndAlso _autonomyOptions IsNot Nothing Then
                If has("create_webhook") Then reg.Add(New CreateWebhookTool(_triggers, _autonomyOptions.MaxSelfWebhooks))
                If has("list_webhooks") Then reg.Add(New ListWebhooksTool(_triggers))
                If has("delete_webhook") Then reg.Add(New DeleteWebhookTool(_triggers))
            End If
            If _autonomy Is Nothing Then Return
            If has("create_plan") Then reg.Add(New CreatePlanTool(_autonomy))
            Dim team = _autonomy.ManagedAgents(profile.Id)
            If has("team_status") AndAlso (team.Count > 0 OrElse profile.CanHire) Then reg.Add(New TeamStatusTool(_autonomy))
            If team.Count > 0 Then
                If has("assign_task") Then reg.Add(New AssignTaskTool(_autonomy, team))
                If has("cancel_agent_task") Then reg.Add(New CancelAgentTaskTool(_autonomy, team))
            End If
            If profile.CanHire AndAlso has("hire_agent") AndAlso profile.AllowedModels.Count > 0 Then
                reg.Add(New HireAgentTool(profile.AllowedModels.OrderBy(Function(m) m).ToList()))
                Dim hires = _autonomy.ActiveHires(profile.Id).Select(Function(h) h.AgentId).ToList()
                If hires.Count > 0 AndAlso has("retire_agent") Then reg.Add(New RetireAgentTool(_autonomy, hires))
            End If
        End Sub

        ''' <summary>Granted additional models usable right now (enabled, priced, still granted), for consult/switch tools.</summary>
        Private Function GrantedTools(profile As EffectiveProfile, requireTools As Boolean) As List(Of (Key As String, UseFor As String, Price As String))
            Dim result As New List(Of (Key As String, UseFor As String, Price As String))
            If _access Is Nothing OrElse _selection Is Nothing Then Return result
            For Each x In _access.GrantedModelsFor(profile)
                If Not _selection.IsUsableBy(x.Model, profile, Nothing) Then Continue For
                If requireTools AndAlso Not x.Model.Has(ModelCapability.ToolCalling) Then Continue For
                Dim p = _pricing?.GetPrice(x.Model.Key)
                Dim price = If(p IsNot Nothing AndAlso p.IsPriced, $"${p.InputPerMillion}/M in, ${p.OutputPerMillion}/M out", "priced")
                result.Add((x.Model.Key, TextUtil.Truncate(x.Grant.UseFor, 300), price))
            Next
            Return result
        End Function

        Public Function BuildForAgent(profile As EffectiveProfile) As AgentToolRegistry
            Dim reg As New AgentToolRegistry()
            Dim has = Function(n As String) profile.HasInternalTool(n)

            If has("remember") Then reg.Add(New RememberTool())
            If has("recall") Then reg.Add(New RecallTool())
            If has("forget") Then reg.Add(New ForgetTool())
            reg.Add(New CompleteTaskTool())
            If has("report_progress") Then
                reg.Add(New ReportProgressTool With {.Emit = Sub(a, t, note) _events.Emit(AgentEventType.TaskProgress, note, a, t)})
            End If
            If has("create_followup_task") Then reg.Add(New CreateFollowUpTaskTool(_comm.MaxChildTasksPerTask))
            If has("get_my_budget") Then reg.Add(New GetMyBudgetTool())
            If has("request_escalation") Then reg.Add(New RequestEscalationTool())
            If has("save_artifact") Then reg.Add(New SaveArtifactTool())
            If has("read_artifact") Then reg.Add(New ReadArtifactTool())
            If has("list_artifacts") Then reg.Add(New ListArtifactsTool())
            If has("ask_human") Then reg.Add(New AskHumanTool())
            If has("analyze_image") AndAlso _vision.AgentCanUse(profile) Then reg.Add(New AnalyzeImageTool(_visionMaxBytes))
            If has("generate_image") AndAlso _images.Enabled Then reg.Add(New GenerateImageTool())
            If has("search_lessons") AndAlso _lessons IsNot Nothing Then reg.Add(New SearchLessonsTool(_lessons))

            ' Autonomy tools.
            If has("notify_human") AndAlso _notify IsNot Nothing AndAlso _notify.Enabled Then reg.Add(New NotifyHumanTool(_notify))
            If has("request_budget") Then reg.Add(New RequestBudgetTool())
            If _goals IsNot Nothing Then
                If has("list_goals") Then reg.Add(New ListGoalsTool(_goals))
                If has("record_goal_progress") Then reg.Add(New RecordGoalProgressTool(_goals))
            End If
            If _skills IsNot Nothing Then
                If has("author_skill") Then reg.Add(New AuthorSkillTool(_skills))
                If has("list_skills") Then reg.Add(New ListSkillsTool(_skills))
                If has("use_skill") Then reg.Add(New UseSkillTool(_skills))
            End If
            If has("propose_profile_change") AndAlso _proposals IsNot Nothing AndAlso _allowSelfMod Then reg.Add(New ProposeProfileChangeTool(_proposals))
            If has("search_web") AndAlso _webSearch IsNot Nothing AndAlso _webSearch.Enabled Then reg.Add(New SearchWebTool(_webSearch))
            AddAutonomyTools(reg, profile, Function(n) profile.HasInternalTool(n))

            ' Subagents (regular agents only; subagents cannot nest).
            If _subagents.Enabled AndAlso Not profile.IsSubagent AndAlso profile.MaxSubagents > 0 AndAlso has("spawn_subagent") Then
                Dim models = If(_access IsNot Nothing, _access.SpawnableModels(profile), profile.AllowedModels.ToList())
                If models.Count > 0 Then
                    reg.Add(New SpawnSubagentTool(models, _subagents.DefaultTimeoutMinutes, _subagents.MaxTimeoutMinutes,
                                                  If(profile.SubagentDefaultSpendCap, _subagents.DefaultSpendCap), profile.MaxSubagents))
                    If has("wait_for_subagents") Then reg.Add(New WaitForSubagentsTool())
                    If has("list_subagents") Then reg.Add(New ListSubagentsTool())
                    If has("cancel_subagent") Then reg.Add(New CancelSubagentTool())
                End If
            End If

            ' Administrator-granted additional models.
            If has("consult_model") Then
                Dim granted = GrantedTools(profile, False)
                If granted.Count > 0 Then reg.Add(New ConsultModelTool(granted))
            End If
            If has("switch_model") Then
                Dim switchable = GrantedTools(profile, True)
                If switchable.Count > 0 Then reg.Add(New SwitchModelTool(switchable))
            End If

            If _comm.Enabled Then
                ' Recipients are exposed as an enum of exactly the permitted agents (never subagents).
                Dim recipients As List(Of String)
                If profile.AllowedRecipients.Contains("*") Then
                    recipients = _profiles.AllProfiles().Where(Function(p) Not String.Equals(p.Id, profile.Id, StringComparison.OrdinalIgnoreCase) AndAlso p.Enabled AndAlso Not p.IsSubagent).Select(Function(p) p.Id).ToList()
                Else
                    recipients = profile.AllowedRecipients.Where(Function(r)
                                                                     Dim rp = _profiles.TryGetProfile(r)
                                                                     Return rp IsNot Nothing AndAlso Not rp.IsSubagent
                                                                 End Function).OrderBy(Function(r) r).ToList()
                End If
                If recipients.Count > 0 Then
                    If has("list_contacts") Then reg.Add(New ListContactsTool())
                    If has("send_agent_message") AndAlso (profile.AllowedSendMessageTypes.Contains(MessageType.Information) OrElse profile.AllowedSendMessageTypes.Contains(MessageType.Notification)) Then
                        reg.Add(New SendAgentMessageTool(recipients))
                    End If
                    If has("request_agent_help") AndAlso profile.AllowedSendMessageTypes.Contains(MessageType.Request) Then reg.Add(New RequestAgentHelpTool(recipients))
                    If has("delegate_task") AndAlso profile.AllowedSendMessageTypes.Contains(MessageType.TaskDelegation) Then
                        Dim delegateTool As New DelegateTaskTool(recipients)
                        If _autonomy IsNot Nothing AndAlso _autonomyOptions IsNot Nothing AndAlso _autonomyOptions.BountiesEnabled Then delegateTool.BountyCheck = AddressOf _autonomy.CheckBountyAsync
                        reg.Add(delegateTool)
                    End If
                End If
                If has("reply_to_agent") Then reg.Add(New ReplyToAgentTool())
                If has("broadcast_notification") AndAlso profile.CanBroadcast AndAlso profile.Groups.Count > 0 Then
                    reg.Add(New BroadcastTool(profile.Groups.Where(Function(g) _comm.Groups.ContainsKey(g)).ToList()))
                End If
            End If

            For Each t In _mcp.GetToolsForAgent(profile)
                reg.Add(t)
            Next
            Return reg
        End Function
    End Class

    ''' <summary>
    ''' Validates and executes model-requested tool calls: the tool must be in the agent's registry, arguments must
    ''' match the schema, approvals are enforced, output is bounded and untrusted content is wrapped. Every call is
    ''' recorded (ToolCalls table + events).
    ''' </summary>
    Public NotInheritable Class ToolExecutor
        Private ReadOnly _telemetry As TelemetryRepository
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog

        Public Sub New(telemetry As TelemetryRepository, events As IAgentEventBus, log As ILog)
            _telemetry = telemetry
            _events = events
            _log = log
        End Sub

        Public Async Function ExecuteAsync(ctx As ToolInvocationContext, cal As ToolCallRequest, registry As AgentToolRegistry, Optional approved As Boolean = False) As Task(Of ToolResult)
            Dim tool = registry.TryGet(cal.Name)
            If tool Is Nothing Then Return ToolResult.Fail($"Tool '{cal.Name}' is not available to you. Available tools: {String.Join(", ", registry.Names())}.")
            ' Code a model wrote HTML-escaped (&lt; for <) is repaired before a file tool writes it (see CodeEntities).
            Dim repaired = Global.Agentic.Work.CodeEntities.FixToolArguments(cal.ArgumentsJson)
            If repaired IsNot Nothing Then cal.ArgumentsJson = repaired
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(cal.ArgumentsJson), "{}", cal.ArgumentsJson), args) Then
                Return ToolResult.Fail("Arguments were not valid JSON.")
            End If
            Dim schemaError As String
            Try
                schemaError = JsonSchemaValidator.Validate(tool.Parameters, args)
            Catch ex As Exception
                schemaError = Nothing
                _log.Warn("Tools", $"Schema validation for {tool.Name} failed internally: {ex.Message}", ctx.AgentId)
            End Try
            If schemaError IsNot Nothing Then Return ToolResult.Fail(schemaError)

            If tool.RequiresApproval AndAlso Not approved Then
                Dim rec = Await ctx.Scope.Approvals.AskAsync(ctx.CurrentTask?.Id, cal.Id, ApprovalKind.ToolApproval, $"Approve {tool.Name}?",
                                                             $"Agent {ctx.AgentId} wants to call {tool.Name}.", TextUtil.Truncate(cal.ArgumentsJson, 4000),
                                                             DateTime.UtcNow.AddDays(1)).ConfigureAwait(False)
                If rec.Status = ApprovalStatus.Approved Then
                    ' The agent's policy approved it at once: run it now.
                    approved = True
                ElseIf rec.Status <> ApprovalStatus.Pending Then
                    Return ToolResult.Fail($"This call was not approved ({rec.Status}{If(rec.DecidedBy Is Nothing, "", " by " & rec.DecidedBy)}). {rec.Response}")
                Else
                    Dim who = If(rec.StagedAgent IsNot Nothing, $"your supervisor {rec.StagedAgent}", "an administrator")
                    Return ToolResult.Park(rec.WaitKey(), DateTime.UtcNow.AddDays(1).AddMinutes(1), $"This tool requires approval from {who} (request {rec.Id}). Waiting.")
                End If
            End If

            Dim recordId = Ids.NewId("tc")
            Dim started = DateTime.UtcNow
            Try
                Await _telemetry.InsertToolCallAsync(New ToolCallRecord With {
                    .Id = recordId, .ToolCallId = cal.Id, .AgentId = ctx.AgentId, .TaskId = ctx.CurrentTask?.Id, .ToolName = tool.Name, .Source = tool.Source,
                    .Arguments = cal.ArgumentsJson, .StartedUtc = started}).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Tools", "Could not record tool call start", ctx.AgentId, ex:=ex)
            End Try
            _events.Emit(AgentEventType.ToolStarted, $"Called {tool.Name}", ctx.AgentId, ctx.CurrentTask?.Id, correlationId:=ctx.CurrentTask?.CorrelationId,
                         data:=New Dictionary(Of String, Object) From {{"tool", tool.Name}, {"source", tool.Source.ToString()}})

            Dim sw = Stopwatch.StartNew()
            Dim result As ToolResult = Nothing
            Dim outerCt = ctx.CancellationToken
            Using toolCts = CancellationTokenSource.CreateLinkedTokenSource(outerCt)
                toolCts.CancelAfter(tool.Timeout)
                Dim innerCtx As New ToolInvocationContext With {
                    .Scope = ctx.Scope, .Profile = ctx.Profile, .CurrentTask = ctx.CurrentTask, .ToolCallId = cal.Id,
                    .CancellationToken = toolCts.Token, .Resolution = ctx.Resolution}
                Try
                    result = Await tool.ExecuteAsync(innerCtx, args).ConfigureAwait(False)
                Catch ex As OperationCanceledException When Not outerCt.IsCancellationRequested
                    result = ToolResult.Fail($"Tool timed out after {tool.Timeout.TotalSeconds:0}s.")
                Catch ex As OperationCanceledException
                    Throw
                Catch ex As Exception
                    _log.Warn("Tools", $"Tool {tool.Name} failed", ctx.AgentId, ctx.CurrentTask?.Id, ex)
                    result = ToolResult.Fail($"{tool.Name} failed: {ex.Message}")
                End Try
            End Using
            sw.Stop()
            If result Is Nothing Then result = ToolResult.Fail("Tool returned no result.")

            ' Bound output and mark untrusted data.
            Dim content = TextUtil.Truncate(If(result.Content, ""), ctx.Profile.MaxToolOutputChars, "…[output truncated]")
            If result.Untrusted Then content = Untrusted.Wrap(If(result.UntrustedSource, tool.Name), content)
            result.Content = content

            Try
                Await _telemetry.CompleteToolCallAsync(recordId, result.Success, TextUtil.Truncate(content, 2000), If(result.Success, Nothing, TextUtil.Truncate(content, 500)), sw.ElapsedMilliseconds).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Tools", "Could not record tool call completion", ctx.AgentId, ex:=ex)
            End Try
            _events.Emit(AgentEventType.ToolCompleted, $"{tool.Name} {If(result.Success, "completed", "failed")} in {sw.ElapsedMilliseconds}ms", ctx.AgentId, ctx.CurrentTask?.Id,
                         correlationId:=ctx.CurrentTask?.CorrelationId, level:=If(result.Success, EventLevel.Info, EventLevel.Warning),
                         data:=New Dictionary(Of String, Object) From {{"tool", tool.Name}, {"success", result.Success}, {"durationMs", sw.ElapsedMilliseconds}})
            Return result
        End Function
    End Class

    ''' <summary>
    ''' The bounded reasoning/tool loop for "reasoning" tasks: build bounded context → call an allowed model → validate
    ''' and run permitted tools → repeat until complete_task, a park (waiting on an agent/human), or a limit.
    ''' Escalates along the purpose ladder when the host's escalation policy allows and the budget covers it.
    ''' State is checkpointed every turn so tasks resume after waits and restarts.
    ''' </summary>
    Friend NotInheritable Class ReasoningTaskHandler
        Implements ITaskHandler

        Private ReadOnly _gateway As ModelGateway
        Private ReadOnly _prompts As IAgentPromptBuilder
        Private ReadOnly _catalog As ToolCatalog
        Private ReadOnly _tools As ToolExecutor
        Private ReadOnly _telemetry As TelemetryRepository
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _escalation As EscalationOptions
        Private ReadOnly _verifyPasses As Integer

        Public Sub New(gateway As ModelGateway, prompts As IAgentPromptBuilder, catalog As ToolCatalog, tools As ToolExecutor, telemetry As TelemetryRepository,
                       events As IAgentEventBus, log As ILog, escalation As EscalationOptions, Optional verifyPasses As Integer = 0)
            _gateway = gateway
            _prompts = prompts
            _catalog = catalog
            _tools = tools
            _telemetry = telemetry
            _events = events
            _log = log
            _escalation = escalation
            _verifyPasses = Math.Max(0, verifyPasses)
        End Sub

        ''' <summary>Read-only calls of one reply may run at the same time (Autonomy.ParallelTools).</summary>
        Public Property ParallelTools As Boolean = True
        Public Property MaxParallelTools As Integer = 4
        ''' <summary>MCP tool descriptions are loaded on demand (ToolLoading); Nothing = every tool on every call.</summary>
        Public Property ToolLoadingOptions As ToolLoadingOptions

        ''' <summary>Loop-guard calls and seen results a checkpoint keeps (the oldest go first).</summary>
        Private Const MaxSeenCalls As Integer = 400
        Private Const MaxSeenResults As Integer = 600

        ''' <summary>A result fingerprint as the checkpoint keeps it (16 hex characters are plenty to tell results apart).</summary>
        Private Shared Function ShortHash(h As String) As String
            Return If(h Is Nothing OrElse h.Length <= 16, h, h.Substring(0, 16))
        End Function

        ''' <summary>The loop guard's key for a call: a hash of its name and canonical arguments (a file write's arguments hold the whole file).</summary>
        Friend Shared Function GuardKey(name As String, argsJson As String) As String
            Dim key = Global.Agentic.Work.ChatService.ToolCallKey(name, argsJson)
            Return Convert.ToHexString(Global.System.Security.Cryptography.SHA256.HashData(Text.Encoding.UTF8.GetBytes(key))).Substring(0, 32)
        End Function

        ''' <summary>
        ''' After compaction shortened or removed earlier results, the loop guard no longer holds "the same result again" against a
        ''' call: the compactor tells the agent to re-run what it still needs, and the guard then refused it with "use the result
        ''' you already have" although that result was gone (LedgerLite: 429 refused calls, each a full model turn).
        ''' </summary>
        Friend Shared Sub ForgetCompactedResults(cp As TaskCheckpoint, seenCalls As Dictionary(Of String, (Count As Integer, LastHash As String, SameResults As Integer)))
            For Each k In seenCalls.Keys.ToList()
                Dim v = seenCalls(k)
                If v.SameResults = 0 Then Continue For
                seenCalls(k) = (v.Count, v.LastHash, 0)
                RememberSeenCall(cp, k, seenCalls(k))
            Next
        End Sub

        ''' <summary>
        ''' Reports progress every minute while a model call is under way. A call is bounded by its own timeout; without this a
        ''' slow model (two 600 s timeouts in a row) looked like a hung task to the watchdog, which cancelled it as stuck and
        ''' retried it on a paid cloud model (LedgerLite's CI step).
        ''' </summary>
        Private Shared Function ProgressWhileWaiting(ctx As TaskExecutionContext, label As String) As IDisposable
            Dim stopper As New CancellationTokenSource()
            If ctx.Progress Is Nothing Then Return stopper
            Dim started = Stopwatch.StartNew()
            Dim unused = Task.Run(Async Function()
                                      Try
                                          While True
                                              Await Task.Delay(TimeSpan.FromSeconds(60), stopper.Token).ConfigureAwait(False)
                                              ctx.Progress?.Invoke($"{label}: {started.Elapsed.TotalSeconds:0} s")
                                          End While
                                      Catch ex As OperationCanceledException
                                      End Try
                                  End Function)
            Return New CallbackDisposable(Sub()
                                              stopper.Cancel()
                                              stopper.Dispose()
                                          End Sub)
        End Function

        Private NotInheritable Class CallbackDisposable
            Implements IDisposable
            Private _action As Action
            Public Sub New(action As Action)
                _action = action
            End Sub
            Public Sub Dispose() Implements IDisposable.Dispose
                Dim a = Interlocked.Exchange(_action, Nothing)
                a?.Invoke()
            End Sub
        End Class

        ''' <summary>Keeps the loop guard's record of a call in the checkpoint, so it holds across time slices.</summary>
        Private Shared Sub RememberSeenCall(cp As TaskCheckpoint, key As String, v As (Count As Integer, LastHash As String, SameResults As Integer))
            If cp.SeenCalls Is Nothing Then cp.SeenCalls = New Dictionary(Of String, SeenCall)(StringComparer.Ordinal)
            cp.SeenCalls.Remove(key)
            cp.SeenCalls(key) = New SeenCall With {.Count = v.Count, .LastHash = v.LastHash, .SameResults = v.SameResults}
            If cp.SeenCalls.Count > MaxSeenCalls Then cp.SeenCalls.Remove(cp.SeenCalls.Keys.First())
        End Sub

        ''' <summary>The tools offered to the model on this call: every tool, or with ToolLoading the host tools and the loaded MCP tools.</summary>
        Friend Shared Function ToolsForCall(registry As AgentToolRegistry, cp As TaskCheckpoint) As List(Of ToolDefinition)
            If cp Is Nothing OrElse Not cp.ToolLoading Then Return registry.Definitions()
            Dim loaded As New HashSet(Of String)(cp.LoadedTools, StringComparer.Ordinal)
            Return registry.All().Where(Function(x) x.Source <> ToolSource.Mcp OrElse loaded.Contains(x.Name)).
                Select(Function(x) New ToolDefinition With {.Name = x.Name, .Description = x.Description, .Parameters = x.Parameters}).ToList()
        End Function

        ''' <summary>
        ''' A new task's tools under ToolLoading: its core MCP tools are loaded, load_tools is added, and the system prompt
        ''' names the other MCP tools. Leaves everything as it is when there is nothing worth deferring.
        ''' </summary>
        Private Async Function StartToolLoadingAsync(cp As TaskCheckpoint, registry As AgentToolRegistry, agentId As String) As Task
            Dim opts = ToolLoadingOptions
            If opts Is Nothing OrElse Not opts.Enabled Then Return
            Dim recent As List(Of String) = Nothing
            Try
                If opts.RecentlyUsedTools > 0 Then recent = Await _telemetry.TopMcpToolsAsync(agentId, DateTime.UtcNow.AddDays(-Math.Max(1, opts.RecentDays)), opts.RecentlyUsedTools).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Reasoning", "Could not read the agent's most used tools: " & ex.Message, agentId)
            End Try
            Dim core = ToolLoading.CoreTools(registry, opts, recent)
            If core Is Nothing Then Return
            cp.ToolLoading = True
            cp.LoadedTools = core
            Dim catalog = ToolLoading.Catalog(registry, cp.LoadedTools)
            If catalog IsNot Nothing AndAlso cp.Transcript.Count > 0 AndAlso cp.Transcript(0).Role = "system" Then cp.Transcript(0).Content &= vbLf & vbLf & catalog
        End Function

        ''' <summary>Tools that only read: running several of them at once cannot change what any of them sees.</summary>
        Friend Shared ReadOnly ParallelSafeTools As New HashSet(Of String)(StringComparer.Ordinal) From {
            "recall", "read_artifact", "list_artifacts", "search_web", "read_web_page", "search_knowledge", "search_lessons", "get_my_budget", "market_prices", "news_articles", "stock_quotes", "polymarket",
            "list_goals", "list_skills", "list_contacts", "list_subagents", "team_status", "list_schedules", "list_webhooks", "list_secrets",
            "analyze_image", "consult_model"}

        ''' <summary>
        ''' Progress checks in a row at which an agent had only looked (read, listed, searched, re-planned) without changing
        ''' anything, saving anything or finishing a plan step; then it counts as stuck. New slices of the same files are new
        ''' results, so a local model read a repository for two hours (600 reads, no write) and passed every check.
        ''' </summary>
        Friend Const MaxLookingStretches As Integer = 4

        Private Shared ReadOnly LookPrefixes As String() = {"get_", "list_", "read_", "search_", "find_", "view_", "show_", "describe_"}

        ''' <summary>
        ''' A call that only looks: a read-only host tool, update_plan or load_tools, an MCP tool named like a read (get_...,
        ''' list_..., read_...), or a shell command that only reads (ShellOnlyReads).
        ''' </summary>
        Friend Shared Function OnlyLooks(name As String, Optional argsJson As String = Nothing) As Boolean
            If String.IsNullOrEmpty(name) Then Return False
            If ParallelSafeTools.Contains(name) OrElse name = "update_plan" OrElse name = "load_tools" Then Return True
            If Not name.StartsWith("mcp__", StringComparison.Ordinal) Then Return False
            Dim i = name.LastIndexOf("__", StringComparison.Ordinal)
            Dim own = If(i >= 0, name.Substring(i + 2), name)
            If ShellToolNames.Contains(own) AndAlso argsJson IsNot Nothing Then
                Dim el As JsonElement
                Return JsonUtil.TryParseElement(argsJson, el) AndAlso el.ValueKind = JsonValueKind.Object AndAlso ShellOnlyReads(JsonUtil.GetString(el, "command"))
            End If
            Return LookPrefixes.Any(Function(p) own.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared ReadOnly ShellToolNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"run_command", "execute_command", "exec_command", "bash", "shell"}
        Private Shared ReadOnly ReadVerbs As New HashSet(Of String)(StringComparer.Ordinal) From {
            "cat", "head", "tail", "grep", "egrep", "fgrep", "rg", "ls", "tree", "wc", "pwd", "echo", "printf", "stat", "file", "du", "df", "diff",
            "nl", "sort", "uniq", "cut", "tr", "awk", "column", "jq", "which", "type", "whoami", "date", "cd", "true", "basename", "dirname",
            "realpath", "readlink", "md5sum", "sha256sum", "xxd", "od", "hexdump"}
        Private Shared ReadOnly GitReads As New HashSet(Of String)(StringComparer.Ordinal) From {
            "status", "log", "show", "diff", "blame", "ls-files", "ls-tree", "rev-parse", "describe", "shortlog", "grep", "reflog"}
        Private Shared ReadOnly HarmlessRedirects As New Text.RegularExpressions.Regex("(?:\b[12]|&)?>\s*/dev/null|\b2>&1", Text.RegularExpressions.RegexOptions.CultureInvariant)

        ''' <summary>
        ''' A shell command that only reads: every part (split at &amp;&amp;, ||, ; and |) is cat, sed -n, grep, ls, find, git
        ''' log/show/diff/status... with no output redirect. LedgerLite's CI step ran 354 commands, nearly all cat/sed -n of the
        ''' same files, and each counted as changing something, so the "only reading" check never saw it.
        ''' </summary>
        Friend Shared Function ShellOnlyReads(command As String) As Boolean
            Dim cmd = If(command, "").Trim()
            If cmd.Length = 0 OrElse cmd.Contains("<<") OrElse cmd.Contains("$(") OrElse cmd.Contains("`"c) Then Return False
            ' Quoted text is a pattern or a message (grep "def \|class ", echo "=====>"), not shell syntax.
            cmd = Text.RegularExpressions.Regex.Replace(cmd, "'[^']*'|""(?:[^""\\]|\\.)*""", "Q")
            cmd = HarmlessRedirects.Replace(cmd, " ")
            If cmd.Contains(">"c) Then Return False
            For Each part In Text.RegularExpressions.Regex.Split(cmd, "&&|\|\||;|\||\r?\n")
                Dim words = part.Trim().Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                If words.Length = 0 Then Continue For
                Dim verb = words(0)
                Select Case verb
                    Case "sed"
                        If Not words.Contains("-n") OrElse words.Any(Function(w) w.StartsWith("-i", StringComparison.Ordinal) OrElse w.StartsWith("--in-place", StringComparison.Ordinal)) Then Return False
                    Case "find"
                        If words.Any(Function(w) w = "-delete" OrElse w.StartsWith("-exec", StringComparison.Ordinal) OrElse w.StartsWith("-ok", StringComparison.Ordinal) OrElse w.StartsWith("-fprint", StringComparison.Ordinal)) Then Return False
                    Case "git"
                        Dim k = 1
                        While k < words.Length AndAlso words(k).StartsWith("-", StringComparison.Ordinal)
                            k += If(words(k) = "-C" OrElse words(k) = "-c", 2, 1)
                        End While
                        If k >= words.Length Then Return False
                        Dim rest = words.Skip(k + 1).ToArray()
                        Select Case words(k)
                            Case "branch"
                                If rest.Any(Function(w) Not w.StartsWith("-", StringComparison.Ordinal) OrElse {"-d", "-D", "-m", "-M", "-c", "-C", "--delete", "--move", "--copy", "-u", "--set-upstream-to"}.Contains(w)) Then Return False
                            Case "tag"
                                If rest.Length > 0 AndAlso Not rest.Any(Function(w) w = "-l" OrElse w = "--list") Then Return False
                            Case "remote"
                                If rest.Length > 0 AndAlso Not {"-v", "show", "get-url"}.Contains(rest(0)) Then Return False
                            Case Else
                                If Not GitReads.Contains(words(k)) Then Return False
                        End Select
                    Case Else
                        If Not ReadVerbs.Contains(verb) Then Return False
                End Select
            Next
            Return True
        End Function

        ''' <summary>Steps of a plan marked done.</summary>
        Private Shared Function DoneSteps(plan As List(Of PlanStep)) As Integer
            Return If(plan, New List(Of PlanStep)).Where(Function(p) p.Status = "done").Count()
        End Function

        ''' <summary>
        ''' When every call of a reply is read-only (and more than one would run), runs them at the same time and returns their
        ''' results by call id; the normal loop then takes them in order (loop guard and bookkeeping unchanged).
        ''' Nothing when the reply has to run one call after another.
        ''' </summary>
        Private Async Function RunReadOnlyCallsTogetherAsync(ctx As TaskExecutionContext, cp As TaskCheckpoint, calls As List(Of ToolCallRequest), registry As AgentToolRegistry,
                                                            seenCalls As Dictionary(Of String, (Count As Integer, LastHash As String, SameResults As Integer)),
                                                            profile As EffectiveProfile) As Task(Of Dictionary(Of String, ToolResult))
            If Not ParallelTools OrElse calls Is Nothing OrElse calls.Count < 2 OrElse cp.ToolsOff Then Return Nothing
            If Not calls.All(Function(c) ParallelSafeTools.Contains(c.Name) AndAlso registry.TryGet(c.Name) IsNot Nothing) Then Return Nothing
            ' The same checks the loop makes, in order: only calls that would run (not a rut), and only the first of identical calls.
            ' (No count: Tasks.MaxToolCalls is where a stretch ends for a progress check, and every call of a reply runs.)
            Dim keys As New HashSet(Of String)(StringComparer.Ordinal)
            Dim runs As New List(Of ToolCallRequest)
            For Each c In calls
                Dim key = GuardKey(c.Name, c.ArgumentsJson)
                Dim seen As (Count As Integer, LastHash As String, SameResults As Integer) = Nothing
                Dim hasSeen = seenCalls.TryGetValue(key, seen)
                Dim polling = Global.Agentic.Work.ChatService.IsPollingTool(c.Name)
                If hasSeen AndAlso seen.SameResults >= If(polling, Global.Agentic.Work.ChatService.MaxSameToolCalls - 1, 2) Then Continue For
                If keys.Add(key) Then runs.Add(c)
            Next
            If runs.Count < 2 Then Return Nothing
            ctx.Progress?.Invoke($"Tools {String.Join(", ", runs.Select(Function(c) c.Name).Distinct())} (together)")
            Dim results As New Global.System.Collections.Concurrent.ConcurrentDictionary(Of String, ToolResult)(StringComparer.Ordinal)
            Using gate As New SemaphoreSlim(Math.Max(1, MaxParallelTools))
                Await Task.WhenAll(runs.Select(Async Function(c)
                                                   Await gate.WaitAsync(ctx.CancellationToken).ConfigureAwait(False)
                                                   Try
                                                       results(c.Id) = Await _tools.ExecuteAsync(ToolCtx(ctx, c.Id, Nothing), c, registry).ConfigureAwait(False)
                                                   Finally
                                                       gate.Release()
                                                   End Try
                                               End Function)).ConfigureAwait(False)
            End Using
            Return New Dictionary(Of String, ToolResult)(results, StringComparer.Ordinal)
        End Function

        ''' <summary>
        ''' The host's note when little time is left before the task's deadline (a fifth of its time, at least 15 minutes), once
        ''' per deadline; else Nothing. A step of a global task otherwise worked on until it was cancelled at the deadline with its
        ''' deliverables unsaved (LedgerLite).
        ''' </summary>
        Friend Shared Function DeadlineWarning(t As AgentTask, cp As TaskCheckpoint, now As DateTime) As String
            If t Is Nothing OrElse Not t.DeadlineUtc.HasValue OrElse cp Is Nothing Then Return Nothing
            Dim deadline = t.DeadlineUtc.Value
            If cp.DeadlineWarnedFor.HasValue AndAlso cp.DeadlineWarnedFor.Value = deadline Then Return Nothing
            Dim span = deadline - If(t.StartedUtc, t.CreatedUtc)
            Dim warnAt = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMinutes(15).Ticks, span.Ticks \ 5))
            Dim left = deadline - now
            If left > warnAt Then Return Nothing
            Return $"[host] Time check: this task's deadline is {deadline:HH:mm} UTC ({PromptBuilder.LeftText(left)}). Finish the part you are on, " &
                   "save every deliverable (files, commits) now, and call complete_task before the deadline with what is done and what is still open. " &
                   "Do not start new work that cannot be finished in time."
        End Function

        ''' <summary>After older messages were dropped, the agent's plan goes into the host note so it keeps its bearings.</summary>
        Private Shared Sub KeepPlanVisible(cp As TaskCheckpoint)
            If cp.Plan Is Nothing OrElse cp.Plan.Count = 0 OrElse cp.Transcript.Count <= 2 Then Return
            Dim note = cp.Transcript(2)
            If note.Role <> "user" OrElse note.Content Is Nothing OrElse Not note.Content.StartsWith("[host] ", StringComparison.Ordinal) Then Return
            Const marker As String = "Your current plan (update_plan):"
            Dim text = note.Content
            Dim i = text.IndexOf(vbLf & marker, StringComparison.Ordinal)
            If i >= 0 Then text = text.Substring(0, i)
            note.Content = text & vbLf & marker & vbLf & PlanStep.Render(cp.Plan)
        End Sub

        ''' <summary>The self-critique instruction injected before a successful complete_task is accepted.</summary>
        Private Shared Function VerifyPrompt(pass As Integer, total As Integer, t As AgentTask) As String
            Return $"[host] Verification pass {pass} of {total}. Before this task is accepted, critically re-check your result against what the task actually asked:" & vbLf &
                   "- Does it fully and correctly satisfy the request, with nothing made up or assumed?" & vbLf &
                   "- Are claims grounded in what you actually did or verified (tool results, files), not guesses?" & vbLf &
                   "If it holds up, call complete_task again with the same result. If you find a gap or error, fix it first (use tools as needed), then call complete_task."
        End Function

        Public ReadOnly Property Name As String = "reasoning" Implements ITaskHandler.Name

        Public Async Function ExecuteAsync(ctx As TaskExecutionContext) As Task(Of TaskOutcome) Implements ITaskHandler.ExecuteAsync
            Dim t = ctx.CurrentTask
            Dim profile = ctx.Profile
            Dim ct = ctx.CancellationToken
            Dim selection = _gateway.Selection
            Dim purpose = selection.ResolvePurpose(t.Purpose, profile)
            Dim registry = _catalog.BuildForAgent(profile)
            Dim required As New HashSet(Of ModelCapability) From {ModelCapability.ToolCalling}
            ' Self-critique passes: the agent's own setting (autonomous agents default to one), else the host's.
            Dim verifyPasses = Math.Max(0, If(profile.VerifyPasses, _verifyPasses))

            If profile.AllowedModels.Count = 0 Then Return TaskOutcome.Fail("This agent has no AI models assigned, so it cannot run reasoning tasks.")
            Dim baseLevel = selection.InitialLevel(profile, purpose, required, t.StartTier)
            If Not baseLevel.HasValue Then
                required.Clear()
                baseLevel = selection.InitialLevel(profile, purpose, required, t.StartTier)
            End If
            If Not baseLevel.HasValue Then Return TaskOutcome.RetryLater($"No permitted, healthy and priced model is available for purpose {purpose}.", TimeSpan.FromMinutes(10))

            Dim cp = TaskCheckpoint.Parse(t.Checkpoint)
            If cp Is Nothing Then
                Dim level = Math.Max(baseLevel.Value, t.CurrentLadderLevel)
                If selection.CandidatesAt(profile, purpose, level, required).Count = 0 Then level = baseLevel.Value
                cp = New TaskCheckpoint With {.LadderLevel = level, .Transcript = Await _prompts.BuildAsync(profile, ctx.Scope, t, registry).ConfigureAwait(False)}
                ' Administrator pinned this task to a specific (permitted) model.
                If Not String.IsNullOrEmpty(t.ModelKey) Then cp.ModelOverride = t.ModelKey
                ' MCP tool descriptions only when needed: the core set now, the rest named in the system prompt.
                Await StartToolLoadingAsync(cp, registry, t.AgentId).ConfigureAwait(False)
                ctx.Progress?.Invoke("Building context")
            Else
                Dim repark = Await ResumeAsync(ctx, cp, registry).ConfigureAwait(False)
                If repark.Keys.Count > 0 Then
                    Return New TaskOutcome With {.Kind = OutcomeKind.Park, .WaitKeys = repark.Keys, .WaitDeadlineUtc = If(repark.Deadline, DateTime.UtcNow.AddHours(1))}
                End If
            End If
            If cp.ToolLoading Then registry.Add(New LoadToolsTool(registry))

            ' Escalation requested by a previous failed attempt (EscalateOnRetry).
            If cp.EscalationRequested Then
                cp.EscalationRequested = False
                TryEscalate(ctx, cp, purpose, required, If(cp.EscalationReason, "Previous attempt failed"))
            End If

            Dim maxTokensBoost As Integer? = Nothing
            Dim compactions = 0
            Dim budgetFallbackUsed = False
            ' Tool-call loops: the same tool with the same arguments, after the same result twice in a row (or too often in all),
            ' is not run again; the agent is told to use what it has (the same guard as Chat's).
            Dim seenCalls As New Dictionary(Of String, (Count As Integer, LastHash As String, SameResults As Integer))(StringComparer.Ordinal)
            For Each kv In If(cp.SeenCalls, New Dictionary(Of String, SeenCall))
                seenCalls(kv.Key) = (kv.Value.Count, kv.Value.LastHash, kv.Value.SameResults)
            Next
            ' A task that keeps getting things done gives way after half its time slice (Tasks.MaxTaskDurationSeconds) and goes
            ' on from its checkpoint, instead of being cut off in the middle of a step and counted as a failed attempt.
            Dim attemptWatch = Stopwatch.StartNew()
            Dim progressAtStart = cp.Progress
            Dim seenResults As New HashSet(Of String)(If(cp.SeenResults, New List(Of String)), StringComparer.Ordinal)
            Dim extendAfter = TimeSpan.FromSeconds(Math.Max(10, profile.MaxTaskDurationSeconds) * 0.5)

            While True
                ' A stretch: Tasks.MaxReasoningTurns turns or Tasks.MaxToolCalls tool calls, then a progress check (below).
                While cp.Turn < profile.MaxReasoningTurns AndAlso (profile.MaxToolCalls <= 0 OrElse cp.StretchToolCalls < profile.MaxToolCalls)
                    ct.ThrowIfCancellationRequested()
                    If cp.Progress > progressAtStart AndAlso attemptWatch.Elapsed >= extendAfter Then
                        ' Giving way only helps when another task waits for this agent. Otherwise the slice starts over (a pause
                        ' and resume costs a re-read of the work so far: LedgerLite's steps were paused 32 times in 10 hours with
                        ' nothing else queued). Either way it happens here, from half the slice: a turn started later could run
                        ' into the hard end of the slice and be cut off mid-call as a failed attempt (a 10-minute local-model
                        ' turn started at minute 22 of 30).
                        Dim others = ctx.OthersWaitingAsync Is Nothing OrElse Await ctx.OthersWaitingAsync.Invoke().ConfigureAwait(False)
                        If others OrElse ctx.ExtendSlice Is Nothing Then
                            Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                            Return TaskOutcome.GiveWay($"Paused after {attemptWatch.Elapsed.TotalMinutes:0} min of work (half its time slice, Tasks.MaxTaskDurationSeconds, with another task waiting); it goes on from its checkpoint.")
                        Else
                            ctx.ExtendSlice.Invoke()
                            attemptWatch.Restart()
                            progressAtStart = cp.Progress
                        End If
                    End If
                    cp.Turn += 1
                    cp.TotalTurns += 1
                    ctx.Progress?.Invoke($"Reasoning turn {cp.TotalTurns} (level {cp.LadderLevel})")

                    Dim overrideModel = If(cp.ModelOverride Is Nothing, Nothing, _gateway.Registry.TryGet(cp.ModelOverride))
                    If cp.ModelOverride IsNot Nothing AndAlso (overrideModel Is Nothing OrElse Not selection.IsUsableBy(overrideModel, profile, Nothing)) Then
                        cp.Transcript.Add(ChatMessage.ForUser($"[host] Model {cp.ModelOverride} is no longer available to you; continuing on your standard models."))
                        cp.ModelOverride = Nothing
                        overrideModel = Nothing
                    End If
                    ' Time awareness: once, when a fifth of the task's time (at least 15 minutes) is left before its deadline.
                    Dim nearDeadline = DeadlineWarning(t, cp, DateTime.UtcNow)
                    If nearDeadline IsNot Nothing Then
                        cp.Transcript.Add(ChatMessage.ForUser(nearDeadline))
                        cp.DeadlineWarnedFor = t.DeadlineUtc
                    End If
                    ' Budget awareness before a hard stop (only a total cap, Tasks.MaxTotalTurns, is one; stretches are not): work that
                    ' is not saved is lost when it hits.
                    If profile.MaxTotalTurns > 3 AndAlso profile.MaxTotalTurns - cp.TotalTurns + 1 = 2 Then
                        cp.Transcript.Add(ChatMessage.ForUser("[host] Only 2 turns are left for this task (this one and the next). Finish now: save any deliverable, then call " &
                                                              "complete_task with your result - or with success=false, what you found so far and what is still open."))
                    End If
                    Dim window = If(overrideModel IsNot Nothing, overrideModel.ContextWindow,
                                    selection.CandidatesAt(profile, purpose, cp.LadderLevel, required).Select(Function(c) c.Model.ContextWindow).DefaultIfEmpty(32000).Min())
                    If ContextCompactor.Compact(cp.Transcript, window, profile.MaxContextMessages) Then
                        KeepPlanVisible(cp)
                        ForgetCompactedResults(cp, seenCalls)
                    End If

                    Dim callCtx As New AiCallContext With {
                        .AgentId = t.AgentId, .TaskId = t.Id, .CorrelationId = t.CorrelationId, .Profile = profile, .Priority = t.Priority,
                        .Purpose = purpose, .LadderLevel = cp.LadderLevel, .RequiredCapabilities = required, .MaxTokensOverride = maxTokensBoost,
                        .ReasonOverride = If(cp.LadderLevel > baseLevel.Value, AiCallReason.Escalation, CType(Nothing, AiCallReason?))}
                    If overrideModel IsNot Nothing Then
                        ' Explicit model (switch_model or admin pin): no ladder, no spillover.
                        callCtx.ExplicitModels = New List(Of String) From {overrideModel.Key}
                        callCtx.AllowSpillover = False
                        callCtx.SkipPurposeCheck = True
                        callCtx.ReasonOverride = If(selection.AccessFor(overrideModel, profile) = ModelAccessKind.Grant, AiCallReason.CustomModel, AiCallReason.Primary)
                        If Not overrideModel.Has(ModelCapability.ToolCalling) Then callCtx.RequiredCapabilities = New HashSet(Of ModelCapability)
                    End If
                    Dim outcome As AiCallOutcome = Nothing
                    Dim aiError As AiException = Nothing
                    Try
                        ' After too many failed calls in a row only complete_task is offered, so the agent reports instead of thrashing.
                        Dim toolDefs = ToolsForCall(registry, cp)
                        Dim offeredTools = If(cp.ToolsOff, toolDefs.Where(Function(d) d.Name = "complete_task").ToList(), toolDefs)
                        Using ProgressWhileWaiting(ctx, $"Waiting for the model (turn {cp.TotalTurns})")
                            outcome = Await _gateway.ChatAsync(callCtx, cp.Transcript, If(offeredTools.Count > 0 AndAlso required.Contains(ModelCapability.ToolCalling), offeredTools, Nothing), ct).ConfigureAwait(False)
                        End Using
                    Catch ex As AiException
                        aiError = ex
                    End Try

                    If aiError IsNot Nothing Then
                        cp.Turn -= 1
                        cp.TotalTurns -= 1
                        ' The provider's own error text goes to the log; the task and its transcript get the kind of failure in a few words.
                        If aiError.Kind <> AiErrorKind.Cancelled Then _log.Warn("Reasoning", $"Model call failed for task {t.Id} ({aiError.Kind}): {TextUtil.Truncate(aiError.Message, 1000)}", t.AgentId, t.Id)
                        ' An explicitly chosen model that fails or is unaffordable falls back to the normal routing ladder.
                        If cp.ModelOverride IsNot Nothing AndAlso aiError.Kind <> AiErrorKind.Cancelled AndAlso aiError.Kind <> AiErrorKind.ContextOverflow Then
                            _events.Emit(AgentEventType.AiEscalation, $"Model {cp.ModelOverride} unavailable ({aiError.Kind}); back to standard models", t.AgentId, t.Id,
                                         correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                            cp.Transcript.Add(ChatMessage.ForUser($"[host] {cp.ModelOverride} could not be used ({ModelErrors.ForUser(aiError).TrimEnd("."c)}). Continuing on your standard models."))
                            cp.ModelOverride = Nothing
                            Continue While
                        End If
                        Select Case aiError.Kind
                            Case AiErrorKind.ContextOverflow
                                compactions += 1
                                If compactions > 2 OrElse Not ContextCompactor.Compact(cp.Transcript, window, profile.MaxContextMessages, aggressive:=True) Then
                                    Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                                    Return TaskOutcome.Fail("Context window exceeded and could not be reduced further.")
                                End If
                                KeepPlanVisible(cp)
                                ForgetCompactedResults(cp, seenCalls)
                                Continue While
                            Case AiErrorKind.BudgetExceeded
                                If cp.LadderLevel > baseLevel.Value AndAlso Not budgetFallbackUsed Then
                                    budgetFallbackUsed = True
                                    _events.Emit(AgentEventType.AiEscalation, $"Escalated model unaffordable; continuing on level {baseLevel.Value}", t.AgentId, t.Id, correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                                    cp.LadderLevel = baseLevel.Value
                                    cp.Transcript.Add(ChatMessage.ForUser("[host] A stronger model is not affordable with your current budget. Continue with the current model and do your best."))
                                    Continue While
                                End If
                                Return Await WaitForAiAsync(ctx, cp, "Insufficient budget for any permitted model: " & aiError.Message, TimeSpan.FromMinutes(30)).ConfigureAwait(False)
                            Case AiErrorKind.NoModelAvailable, AiErrorKind.ModelUnavailable, AiErrorKind.Timeout, AiErrorKind.Transient, AiErrorKind.RateLimited, AiErrorKind.InvalidResponse
                                ' The models at this level are out (provider out of credit, circuits open) or have been failing a
                                ' while: go to another level that works - the nearest cheaper one first, down to the free local model.
                                Dim levelOut = aiError.OutOfCredit OrElse Not _gateway.Selection.LevelHasWorkingModel(ctx.Profile, purpose, cp.LadderLevel, required)
                                Dim waitedLong = cp.WaitingForAiSinceUtc.HasValue AndAlso DateTime.UtcNow - cp.WaitingForAiSinceUtc.Value >= SwitchLevelAfter
                                If (levelOut OrElse waitedLong) AndAlso TrySwitchLevel(ctx, cp, purpose, required,
                                                                                         If(levelOut, "its models are unavailable (" & aiError.Kind.ToString() & ")",
                                                                                            "it kept failing for " & CInt(SwitchLevelAfter.TotalMinutes) & " min")) Then Continue While
                                If _escalation.OnModelUnavailable AndAlso TryEscalate(ctx, cp, purpose, required, "Model unavailable: " & aiError.Kind.ToString()) Then Continue While
                                Return Await WaitForAiAsync(ctx, cp, "AI unavailable: " & ModelErrors.ForUser(aiError), Nothing).ConfigureAwait(False)
                            Case AiErrorKind.CapabilityUnsupported
                                If _escalation.OnModelUnavailable AndAlso TryEscalate(ctx, cp, purpose, required, "Model unavailable: " & aiError.Kind.ToString()) Then Continue While
                                Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                                Return TaskOutcome.RetryLater("AI unavailable: " & ModelErrors.ForUser(aiError))
                            Case AiErrorKind.QuotaExceeded
                                ' The cloud token limit (the local models have none): an escalated task goes on at its starting
                                ' level when a free local model is there, instead of waiting for the limit to free up.
                                Dim localLevel = If(cp.LadderLevel <> baseLevel.Value AndAlso aiError.Message.Contains("cloud models") AndAlso
                                                    selection.CandidatesAt(profile, purpose, baseLevel.Value, required).Any(Function(c) c.Model.Options.IsLocal), baseLevel, CType(Nothing, Integer?))
                                If localLevel.HasValue AndAlso cp.ModelOverride Is Nothing Then
                                    cp.LadderLevel = localLevel.Value
                                    cp.Transcript.Add(ChatMessage.ForUser("[host] Your cloud token limit for today is used up; this task continues on the local model."))
                                    _events.Emit(AgentEventType.AiEscalation, "Cloud token limit reached; back to the local model", t.AgentId, t.Id, correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                                    Continue While
                                End If
                                Return Await WaitForAiAsync(ctx, cp, aiError.Message, TimeSpan.FromMinutes(15)).ConfigureAwait(False)
                            Case AiErrorKind.InvalidRequest
                                ' Usually model-specific (a fallback was already tried); retry later, bounded by MaxRetries.
                                Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                                Return TaskOutcome.RetryLater("AI request rejected: " & ModelErrors.ForUser(aiError), escalate:=True)
                            Case AiErrorKind.Cancelled
                                Throw New OperationCanceledException(ct)
                            Case Else
                                Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                                Return TaskOutcome.Fail($"AI request failed permanently: {ModelErrors.ForUser(aiError)}")
                        End Select
                    End If

                    t.CostMicros += outcome.CostMicros
                    cp.WaitingForAiSinceUtc = Nothing
                    Dim response = outcome.Response
                    maxTokensBoost = Nothing

                    If response.HasToolCalls Then
                        ' A key written twice in a call's arguments keeps its last value, before anything reads them.
                        For Each c In response.ToolCalls
                            c.ArgumentsJson = JsonUtil.CollapseDuplicateKeys(c.ArgumentsJson)
                        Next
                        cp.Transcript.Add(ChatMessage.ForAssistant(response.Content, response.ToolCalls))
                        cp.InFlightToolCalls = New List(Of ToolCallRequest)(response.ToolCalls)
                        Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)

                        Dim completion As TaskResultContract = Nothing
                        Dim escalateReason As String = Nothing
                        Dim waitKeys As New List(Of String)
                        Dim deadline As DateTime? = Nothing
                        ' Several read-only calls in one reply run at the same time; their results are then handled in order as usual.
                        Dim prefetched = Await RunReadOnlyCallsTogetherAsync(ctx, cp, response.ToolCalls, registry, seenCalls, profile).ConfigureAwait(False)
                        For Each cal In response.ToolCalls
                            Dim result As ToolResult
                            Dim callKey = GuardKey(cal.Name, cal.ArgumentsJson)
                            Dim seen As (Count As Integer, LastHash As String, SameResults As Integer) = Nothing
                            Dim hasSeen = seenCalls.TryGetValue(callKey, seen)
                            ' A rut is the same call giving the same result again and again. A call whose result changes always runs
                            ' (rebuilding or re-testing after each fix is how work gets done), and polling a status tool gets more room.
                            Dim polling = Global.Agentic.Work.ChatService.IsPollingTool(cal.Name)
                            Dim looping = cal.Name <> "complete_task" AndAlso hasSeen AndAlso
                                          seen.SameResults >= If(polling, Global.Agentic.Work.ChatService.MaxSameToolCalls - 1, 2)
                            If looping Then
                                result = ToolResult.Fail($"Not run: you already called {cal.Name} with exactly these arguments {seen.Count} times" &
                                                         If(seen.SameResults >= 2, " and got the same result each time", "") & ", so calling it again will not change anything. " &
                                                         "Use the result you already have, change the arguments, try a different approach, or call complete_task with what you have.")
                                _events.Emit(AgentEventType.TaskProgress, $"Tool loop: {cal.Name} called again with the same arguments; not run", t.AgentId, t.Id,
                                             correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                                cp.StretchRefusals += 1
                            Else
                                cp.ToolCallsUsed += 1
                                cp.StretchToolCalls += 1
                                ctx.Progress?.Invoke("Tool " & cal.Name)
                                Dim early As ToolResult = Nothing
                                If prefetched IsNot Nothing AndAlso prefetched.TryGetValue(cal.Id, early) Then
                                    result = early
                                    prefetched.Remove(cal.Id)
                                Else
                                    result = Await _tools.ExecuteAsync(ToolCtx(ctx, cal.Id, Nothing), cal, registry).ConfigureAwait(False)
                                End If
                                Dim h = Global.Agentic.Work.ChatService.ToolResultFingerprint(result.Success, result.Content)
                                seenCalls(callKey) = If(hasSeen, (seen.Count + 1, h, If(seen.LastHash = h, seen.SameResults + 1, 0)), (1, h, 0))
                                RememberSeenCall(cp, callKey, seenCalls(callKey))
                                ' Progress: a call that worked and gave something new - a result not seen before in this task
                                ' (ten searches that each find nothing are not ten steps forward).
                                If result.Success AndAlso seenResults.Add(ShortHash(h)) Then
                                    cp.SeenResults.Add(ShortHash(h))
                                    If cp.SeenResults.Count > MaxSeenResults Then cp.SeenResults.RemoveRange(0, cp.SeenResults.Count - MaxSeenResults)
                                    cp.StretchProgress += 1
                                    cp.Progress += 1
                                    If Not OnlyLooks(cal.Name, cal.ArgumentsJson) Then cp.StretchChanges += 1
                                End If
                                ' Calls failing one after another with different arguments each time (a "let me try…" loop the
                                ' same-arguments guard misses): tell the agent to change approach, then leave it only complete_task.
                                If result.Signal = ToolSignal.None Then
                                    If result.Success Then
                                        cp.FailStreak = 0
                                    Else
                                        cp.FailStreak += 1
                                        If cp.FailStreak = Global.Agentic.Work.ChatService.FailStreakNudge Then
                                            result.Content &= vbLf & vbLf & $"[host] {cp.FailStreak} tool calls have now failed in a row. Stop trying small variations of the same thing: " &
                                                              "work out the root cause from the errors you already have and change your approach, or call complete_task with success=false and what is blocking you."
                                            _events.Emit(AgentEventType.TaskProgress, $"{cp.FailStreak} tool calls failed in a row; told the agent to change approach", t.AgentId, t.Id,
                                                         correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                                        ElseIf cp.FailStreak >= Global.Agentic.Work.ChatService.FailStreakStop AndAlso Not cp.ToolsOff Then
                                            cp.ToolsOff = True
                                            result.Content &= vbLf & vbLf & "[host] Too many tool calls have failed in a row. Your other tools are switched off for this task: " &
                                                              "call complete_task now with what you found, what is blocking you and what you need (success=false if it is not done)."
                                            _events.Emit(AgentEventType.TaskProgress, $"{cp.FailStreak} tool calls failed in a row; only complete_task is left", t.AgentId, t.Id,
                                                         correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                                        End If
                                    End If
                                End If
                            End If
                            ' ToolLoading: what load_tools loaded, and an MCP tool the agent called by name without loading it, are
                            ' offered from the next call on.
                            If cp.ToolLoading Then
                                For Each n In If(result.LoadTools, New List(Of String))
                                    If Not cp.LoadedTools.Contains(n) Then cp.LoadedTools.Add(n)
                                Next
                                Dim calledTool = registry.TryGet(cal.Name)
                                If calledTool IsNot Nothing AndAlso calledTool.Source = ToolSource.Mcp AndAlso Not cp.LoadedTools.Contains(cal.Name) Then cp.LoadedTools.Add(cal.Name)
                            End If
                            If result.Plan IsNot Nothing Then
                                ' A step done for the first time is getting things done; the same steps done again after a
                                ' plan was reset are not (LedgerLite: 8 of 9 done, then a fresh plan and the same work again).
                                Dim done = DoneSteps(result.Plan)
                                If done > cp.PlanDoneHigh Then
                                    cp.PlanDoneHigh = done
                                    cp.StretchChanges += 1
                                ElseIf done < DoneSteps(cp.Plan) AndAlso cp.PlanDoneHigh > 0 Then
                                    result.Content &= vbLf & vbLf & $"[host] Earlier in this task {cp.PlanDoneHigh} step(s) of your plan were done; this update marks {done} done. " &
                                                      "Do not redo finished work: check briefly that its results are still there and carry on from where you were."
                                End If
                                cp.Plan = result.Plan
                                _events.Emit(AgentEventType.TaskProgress, "Plan: " & TextUtil.Truncate(PlanStep.Render(result.Plan).Replace(vbLf, " | "), 300), t.AgentId, t.Id,
                                             correlationId:=t.CorrelationId, data:=New Dictionary(Of String, Object) From {{"plan", result.Plan}})
                            End If
                            Select Case result.Signal
                                Case ToolSignal.Park
                                    cp.PendingToolCalls.Add(New PendingToolCall With {.ToolCallId = cal.Id, .ToolName = cal.Name, .WaitKey = result.WaitKey,
                                                                                      .ExtraWaitKeys = New List(Of String)(If(result.ExtraWaitKeys, New List(Of String)))})
                                    waitKeys.Add(result.WaitKey)
                                    If result.ExtraWaitKeys IsNot Nothing Then waitKeys.AddRange(result.ExtraWaitKeys)
                                    If result.WaitDeadlineUtc.HasValue AndAlso (Not deadline.HasValue OrElse result.WaitDeadlineUtc.Value > deadline.Value) Then deadline = result.WaitDeadlineUtc
                                Case ToolSignal.Complete
                                    completion = result.Completion
                                    cp.Transcript.Add(ChatMessage.ForTool(cal.Id, cal.Name, result.Content))
                                Case ToolSignal.Escalate
                                    escalateReason = If(result.EscalationReason, "Agent requested escalation")
                                    cp.Transcript.Add(ChatMessage.ForTool(cal.Id, cal.Name, result.Content))
                                Case ToolSignal.SwitchModel
                                    Dim target = _gateway.Registry.TryGet(result.ModelKey)
                                    Dim content As String
                                    If target IsNot Nothing AndAlso target.Has(ModelCapability.ToolCalling) AndAlso selection.AccessFor(target, profile) = ModelAccessKind.Grant AndAlso
                                       selection.IsUsableBy(target, profile, Nothing) Then
                                        cp.ModelOverride = target.Key
                                        content = result.Content
                                        _events.Emit(AgentEventType.AiEscalation, $"Switched to granted model {target.Key}", t.AgentId, t.Id, correlationId:=t.CorrelationId,
                                                     data:=New Dictionary(Of String, Object) From {{"to", target.Key}, {"reason", "switch_model"}})
                                    Else
                                        content = $"ERROR: You cannot switch to '{result.ModelKey}' (not granted, disabled, unpriced or without tool support)."
                                    End If
                                    cp.Transcript.Add(ChatMessage.ForTool(cal.Id, cal.Name, content))
                                Case Else
                                    cp.Transcript.Add(ChatMessage.ForTool(cal.Id, cal.Name, ContextCompactor.RepeatOrOutput(cp.Transcript, cal.Name, result.Content, cal.Id)))
                            End Select
                        Next
                        cp.InFlightToolCalls.Clear()

                        If completion IsNot Nothing Then
                            ' Any parked calls in the same turn are abandoned; the agent chose to finish.
                            cp.PendingToolCalls.Clear()
                            ' Optional self-critique: send a successful result back for one or more verification passes before accepting it.
                            If completion.Success AndAlso verifyPasses > 0 AndAlso cp.Verifications < verifyPasses Then
                                cp.Verifications += 1
                                cp.Transcript.Add(ChatMessage.ForUser(VerifyPrompt(cp.Verifications, verifyPasses, t)))
                                _events.Emit(AgentEventType.TaskProgress, $"Verification pass {cp.Verifications}/{verifyPasses}", t.AgentId, t.Id, correlationId:=t.CorrelationId)
                                Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                                Continue While
                            End If
                            Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                            Return TaskOutcome.Done(completion)
                        End If
                        If waitKeys.Count > 0 Then
                            Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                            Return New TaskOutcome With {.Kind = OutcomeKind.Park, .WaitKeys = waitKeys, .WaitDeadlineUtc = deadline}
                        End If
                        If escalateReason IsNot Nothing Then
                            If Not (_escalation.AllowModelRequest AndAlso TryEscalate(ctx, cp, purpose, required, "Agent request: " & escalateReason)) Then
                                cp.Transcript.Add(ChatMessage.ForUser("[host] Escalation is not possible (no stronger permitted/affordable model or escalation limit reached). Continue with the current model."))
                            End If
                        End If
                        Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                        Continue While
                    End If

                    ' No tool calls.
                    If String.IsNullOrWhiteSpace(response.Content) Then
                        cp.TruncationRetries += 1
                        If cp.TruncationRetries = 1 Then
                            Dim current = selection.CandidatesAt(profile, purpose, cp.LadderLevel, required).FirstOrDefault()
                            maxTokensBoost = Math.Min(If(current?.Model.Options.MaxTokens, 4096) * 2, 32768)
                            cp.Transcript.Add(ChatMessage.ForUser("[host] Your last reply was empty. Respond with tool calls or a final answer; keep internal reasoning short."))
                            Continue While
                        End If
                        If _escalation.OnTruncation AndAlso TryEscalate(ctx, cp, purpose, required, "Model produced empty/truncated output") Then
                            cp.TruncationRetries = 0
                            Continue While
                        End If
                        Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                        Return TaskOutcome.RetryLater("Model repeatedly produced no usable output.", escalate:=True)
                    End If

                    ' A reply that announces its next step ("Let me check the logs.") without calling a tool did nothing: it is
                    ' not the result. Send it back (twice at most) instead of finishing the task with the announcement.
                    If registry.Count > 0 AndAlso cp.DanglingNudges < 2 AndAlso AnnouncesNextStep(response.Content) Then
                        cp.DanglingNudges += 1
                        cp.Transcript.Add(ChatMessage.ForAssistant(response.Content))
                        cp.Transcript.Add(ChatMessage.ForUser("[host] Your reply announced a next step but did not call a tool, so nothing happened. If you meant to do it, " &
                                                              "call the tool now; if the task is finished, call complete_task with your result."))
                        Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                        Continue While
                    End If

                    ' A plain final answer without complete_task is accepted as the result summary.
                    Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                    Return TaskOutcome.Done(TaskResultContract.Ok(response.Content.Trim()))
                End While

                ' A stretch is over (Tasks.MaxReasoningTurns turns or Tasks.MaxToolCalls tool calls) and the task is not done.
                ' Autonomous work does not stop at a count: a task still getting things done goes on (its context is kept compact
                ' every turn). It stops when it is stuck - nothing new in the whole stretch, or only complete_task left after
                ' failures - at the optional total cap (Tasks.MaxTotalTurns), or when its budget or AI limits run out.
                Dim totalCapHit = profile.MaxTotalTurns > 0 AndAlso cp.TotalTurns >= profile.MaxTotalTurns
                ' New results do not always mean progress: not when refused repeats outweigh them (going in circles), and not when
                ' the agent has only looked around for several checks in a row (MaxLookingStretches).
                cp.LookingStretches = If(cp.StretchChanges > 0, 0, cp.LookingStretches + 1)
                Dim stuckWhy As String = Nothing
                If cp.StretchRefusals > 0 AndAlso cp.StretchRefusals >= cp.StretchProgress Then
                    stuckWhy = $"{cp.StretchRefusals} repeated calls were refused against {cp.StretchProgress} new results in the last {cp.Turn} turns"
                ElseIf cp.LookingStretches >= MaxLookingStretches Then
                    stuckWhy = $"it has only been reading for {cp.LookingStretches} progress checks in a row (nothing changed or saved, no plan step done)"
                End If
                If Not totalCapHit AndAlso cp.StretchProgress > 0 AndAlso Not cp.ToolsOff AndAlso stuckWhy Is Nothing Then
                    Dim useful = cp.StretchProgress
                    cp.Stretches += 1
                    cp.Turn = 0
                    cp.StretchToolCalls = 0
                    cp.StretchProgress = 0
                    cp.StretchRefusals = 0
                    cp.StretchChanges = 0
                    cp.Transcript.Add(ChatMessage.ForUser($"[host] Progress check after {cp.TotalTurns} turns: you are getting things done, so carry on. Keep your plan " &
                                                          "current (update_plan), save deliverables as you go, and call complete_task as soon as the task is done."))
                    _events.Emit(AgentEventType.TaskProgress, $"Still working after {cp.TotalTurns} turns ({useful} new results in the last stretch); continuing",
                                 t.AgentId, t.Id, correlationId:=t.CorrelationId)
                    Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                    Continue While
                End If
                ' Stuck: a stronger model gets one more stretch first (a total cap is final).
                If Not totalCapHit AndAlso _escalation.OnTurnLimit AndAlso cp.TurnLimitEscalations < 1 AndAlso
                   TryEscalate(ctx, cp, purpose, required, If(stuckWhy, $"No progress in {cp.Turn} turns")) Then
                    cp.TurnLimitEscalations += 1
                    cp.Turn = 0
                    cp.StretchToolCalls = 0
                    cp.StretchProgress = 0
                    cp.StretchRefusals = 0
                    cp.StretchChanges = 0
                    cp.LookingStretches = 0
                    cp.FailStreak = 0
                    cp.ToolsOff = False
                    cp.Transcript.Add(ChatMessage.ForUser("[host] The previous model stopped making progress" & If(stuckWhy Is Nothing, "", $" ({stuckWhy})") &
                                                          ". You are now running on a stronger model: take a fresh look at what is blocking the task, finish it efficiently and call complete_task."))
                    Continue While
                End If
                ' One last call without tools, so the work done so far reaches the administrator (and a parent agent or delegator)
                ' instead of only "stopped".
                Dim limitText = If(totalCapHit, $"Turn limit ({profile.MaxTotalTurns} turns in all, Tasks.MaxTotalTurns) reached without completing the task.",
                                   If(stuckWhy IsNot Nothing, $"Stopped after {cp.TotalTurns} turns without completing the task: {stuckWhy}.",
                                      $"Stopped after {cp.TotalTurns} turns without completing the task: no progress in the last {cp.Turn} turns " &
                                      "(its tool calls failed, repeated earlier ones, or none were made)."))
                Dim report = Await WrapUpAsync(ctx, cp, purpose, baseLevel.Value).ConfigureAwait(False)
                Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
                If String.IsNullOrWhiteSpace(report) Then Return TaskOutcome.Fail(limitText)
                Return New TaskOutcome With {.Kind = OutcomeKind.Failed, .ErrorMessage = limitText,
                                             .Result = New TaskResultContract With {.Success = False, .Summary = limitText & " Report of the work so far:" & vbLf & report}}
            End While
            Return TaskOutcome.Fail("unreachable")
        End Function

        ''' <summary>How long a task waits for the AI (down, busy, quota or budget used up) before such waits count as failed attempts.</summary>
        Friend Shared AiWaitLimit As TimeSpan = TimeSpan.FromHours(24)
        ''' <summary>The first wait for the AI; later ones grow with the time already waited, up to 15 times this.</summary>
        Friend Shared AiWaitStep As TimeSpan = TimeSpan.FromMinutes(1)

        ''' <summary>
        ''' The AI cannot be used right now: the task waits and tries again later without using up a retry - soon at first, then
        ''' every 15 minutes at most - for up to AiWaitLimit; after that a wait counts as a failed attempt as before.
        ''' </summary>
        Friend Shared Async Function WaitForAiAsync(ctx As TaskExecutionContext, cp As TaskCheckpoint, message As String, after As TimeSpan?) As Task(Of TaskOutcome)
            Dim now = DateTime.UtcNow
            cp.WaitingForAiSinceUtc = If(cp.WaitingForAiSinceUtc, now)
            Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
            Dim waited = now - cp.WaitingForAiSinceUtc.Value
            ' A subagent has its own deadline and spend cap (a used-up cap never refills): it fails over to its parent as before -
            ' except for an hourly limit (its parent's, which it shares), which frees up within the hour: it waits up to an hour
            ' (two subagents failed at once on 2026-11-01 with "Retries exhausted (1): hourly AI call limit (300) reached").
            Dim refills = If(message, "").IndexOf("hourly", StringComparison.OrdinalIgnoreCase) >= 0
            If waited >= AiWaitLimit OrElse (ctx.Profile.IsSubagent AndAlso Not (refills AndAlso waited < TimeSpan.FromHours(1))) Then Return TaskOutcome.RetryLater(message, after)
            Return TaskOutcome.WaitForAi(message, If(after, TimeSpan.FromTicks(Math.Min(15 * AiWaitStep.Ticks, AiWaitStep.Ticks + waited.Ticks \ 4))))
        End Function

        ''' <summary>
        ''' A short reply that only announces what comes next ("Let me check the logs.", "Next, I'll run the tests:") instead of
        ''' being a result: it ends with a colon, or its last sentence starts with "let me", "I'll", "I'm going to", "next I"...
        ''' </summary>
        Friend Shared Function AnnouncesNextStep(reply As String) As Boolean
            Dim r = If(reply, "").Trim()
            If r.Length = 0 Then Return False
            If Global.Agentic.Work.ChatService.DanglingReply(r, True) = "announced" Then Return True
            ' A substantial answer is a result even when it ends by saying what comes next.
            If r.Length > 600 Then Return False
            Dim last = Text.RegularExpressions.Regex.Split(r, "(?<=[.!?…])\s+").LastOrDefault(Function(s) s.Trim().Length > 0)
            If last Is Nothing Then Return False
            Return Text.RegularExpressions.Regex.IsMatch(last.Trim(),
                "^(?:(?:ok(?:ay)?|now|next|first|then|so|alright)[,\s]+)*(?:let me|let's|i'?ll|i will|i'?m going to|i am going to|i need to)\b",
                Text.RegularExpressions.RegexOptions.IgnoreCase)
        End Function

        ''' <summary>
        ''' The turn limit was reached: one more call (only complete_task offered) asking for a report of the work so far - what
        ''' was done and found, exact values and paths, what is unfinished. Nothing when no model answers.
        ''' </summary>
        Private Async Function WrapUpAsync(ctx As TaskExecutionContext, cp As TaskCheckpoint, purpose As AiPurpose, baseLevel As Integer) As Task(Of String)
            Dim t = ctx.CurrentTask
            Dim profile = ctx.Profile
            Dim selection = _gateway.Selection
            Dim completeOnly = _catalog.BuildForAgent(profile).Definitions().Where(Function(d) d.Name = "complete_task").ToList()
            Dim required As New HashSet(Of ModelCapability)
            If completeOnly.Count > 0 AndAlso selection.CandidatesAt(profile, purpose, cp.LadderLevel, New HashSet(Of ModelCapability) From {ModelCapability.ToolCalling}).Count > 0 Then
                required.Add(ModelCapability.ToolCalling)
            End If
            Dim messages As New List(Of ChatMessage)(cp.Transcript)
            messages.Add(ChatMessage.ForUser("[host] This task stops here (it reached its turn limit or stopped making progress), so no more work can be done in it. Report now: call complete_task with success=false " &
                                             "and a summary of what you did, what you found (exact values, file paths, ids, commands that worked), what is not finished and what " &
                                             "the next step should be. Someone else will continue from your report."))
            Dim window = selection.CandidatesAt(profile, purpose, cp.LadderLevel, required).Select(Function(c) c.Model.ContextWindow).DefaultIfEmpty(32000).Min()
            ContextCompactor.Compact(messages, window, profile.MaxContextMessages)
            Dim callCtx As New AiCallContext With {
                .AgentId = t.AgentId, .TaskId = t.Id, .CorrelationId = t.CorrelationId, .Profile = profile, .Priority = t.Priority,
                .Purpose = purpose, .LadderLevel = cp.LadderLevel, .RequiredCapabilities = required,
                .ReasonOverride = If(cp.LadderLevel > baseLevel, AiCallReason.Escalation, CType(Nothing, AiCallReason?))}
            Dim overrideModel = If(cp.ModelOverride Is Nothing, Nothing, _gateway.Registry.TryGet(cp.ModelOverride))
            If overrideModel IsNot Nothing AndAlso selection.IsUsableBy(overrideModel, profile, Nothing) Then
                callCtx.ExplicitModels = New List(Of String) From {overrideModel.Key}
                callCtx.AllowSpillover = False
                callCtx.SkipPurposeCheck = True
                callCtx.ReasonOverride = If(selection.AccessFor(overrideModel, profile) = ModelAccessKind.Grant, AiCallReason.CustomModel, AiCallReason.Primary)
                If Not overrideModel.Has(ModelCapability.ToolCalling) Then callCtx.RequiredCapabilities = New HashSet(Of ModelCapability)
            End If
            Try
                ctx.Progress?.Invoke("Writing a report of the work so far")
                Dim outcome = Await _gateway.ChatAsync(callCtx, messages, If(callCtx.RequiredCapabilities.Contains(ModelCapability.ToolCalling), completeOnly, Nothing),
                                                       ctx.CancellationToken).ConfigureAwait(False)
                t.CostMicros += outcome.CostMicros
                Dim report = If(outcome.Response.Content, "")
                Dim done = outcome.Response.ToolCalls?.FirstOrDefault(Function(c) c.Name = "complete_task")
                If done IsNot Nothing Then
                    Dim args As JsonElement
                    If JsonUtil.TryParseElement(Global.Agentic.Work.ChatService.ValidToolArguments(done.ArgumentsJson), args) Then
                        Dim summary = JsonUtil.GetString(args, "summary")
                        If Not String.IsNullOrWhiteSpace(summary) Then report = summary
                    End If
                End If
                report = Text.RegularExpressions.Regex.Replace(report, "<think>[\s\S]*?</think>", "", Text.RegularExpressions.RegexOptions.IgnoreCase).Trim()
                Return If(report.Length = 0, Nothing, TextUtil.Truncate(report, 20000))
            Catch ex As AiException
                _log.Warn("Reasoning", $"No report of the work so far for task {t.Id}: {ex.Message}", t.AgentId, t.Id)
                Return Nothing
            End Try
        End Function

        Private Shared Function ToolCtx(ctx As TaskExecutionContext, toolCallId As String, resolution As String) As ToolInvocationContext
            Return New ToolInvocationContext With {
                .Scope = ctx.Scope, .Profile = ctx.Profile, .CurrentTask = ctx.CurrentTask, .ToolCallId = toolCallId,
                .CancellationToken = ctx.CancellationToken, .Resolution = resolution}
        End Function

        ''' <summary>How long a task waits for the AI at one level before it moves to another level that works.</summary>
        Friend Shared ReadOnly SwitchLevelAfter As TimeSpan = TimeSpan.FromMinutes(10)

        ''' <summary>
        ''' Moves the task to another level of its ladder with a working model (a provider out of credit or down must not stall
        ''' it): the nearest cheaper level first, down to the free local model, then higher ones. Not counted as an escalation.
        ''' </summary>
        Private Function TrySwitchLevel(ctx As TaskExecutionContext, cp As TaskCheckpoint, purpose As AiPurpose, required As HashSet(Of ModelCapability), why As String) As Boolean
            ' An explicitly chosen model (switch_model / admin pin) is not switched away from.
            If cp.ModelOverride IsNot Nothing Then Return False
            Dim selection = _gateway.Selection
            Dim target = selection.OtherWorkingLevel(ctx.Profile, purpose, cp.LadderLevel, required)
            If Not target.HasValue Then Return False
            Dim t = ctx.CurrentTask
            Dim fromModel = selection.CandidatesAt(ctx.Profile, purpose, cp.LadderLevel, required).FirstOrDefault()?.Model.Key
            Dim toModel = selection.CandidatesAt(ctx.Profile, purpose, target.Value, required).FirstOrDefault(Function(c) c.Model.Breaker.State <> CircuitState.Open)?.Model.Key
            Dim down = target.Value < cp.LadderLevel
            cp.LadderLevel = target.Value
            t.CurrentLadderLevel = target.Value
            cp.WaitingForAiSinceUtc = Nothing
            _events.Emit(AgentEventType.AiEscalation, $"Moved {If(fromModel, "?")} → {If(toModel, "?")} ({If(down, "down", "up")} a level): {why}", t.AgentId, t.Id,
                         correlationId:=t.CorrelationId, level:=EventLevel.Warning,
                         data:=New Dictionary(Of String, Object) From {{"from", fromModel}, {"to", toModel}, {"reason", why}, {"level", target.Value}, {"switch", True}})
            _log.Info("Reasoning", $"Task {t.Id} moved to {toModel}: {why}", t.AgentId, t.Id)
            Return True
        End Function

        ''' <summary>Moves to the next permitted ladder level (host policy, never the model's decision).</summary>
        Private Function TryEscalate(ctx As TaskExecutionContext, cp As TaskCheckpoint, purpose As AiPurpose, required As HashSet(Of ModelCapability), reason As String) As Boolean
            If Not _escalation.Enabled Then Return False
            ' An explicitly chosen model (switch_model / admin pin) is not escalated away from.
            If cp.ModelOverride IsNot Nothing Then Return False
            Dim t = ctx.CurrentTask
            If t.EscalationCount >= _escalation.MaxEscalationsPerTask Then Return False
            Dim nextLevel = _gateway.Selection.NextLevel(ctx.Profile, purpose, cp.LadderLevel, required)
            If Not nextLevel.HasValue Then Return False
            Dim fromModel = _gateway.Selection.CandidatesAt(ctx.Profile, purpose, cp.LadderLevel, required).FirstOrDefault()?.Model.Key
            Dim toModel = _gateway.Selection.CandidatesAt(ctx.Profile, purpose, nextLevel.Value, required).FirstOrDefault()?.Model.Key
            cp.LadderLevel = nextLevel.Value
            t.CurrentLadderLevel = nextLevel.Value
            t.EscalationCount += 1
            _gateway.NoteEscalation()
            _events.Emit(AgentEventType.AiEscalation, $"Escalated {If(fromModel, "?")} → {If(toModel, "?")}: {TextUtil.Truncate(reason, 200)}", t.AgentId, t.Id,
                         correlationId:=t.CorrelationId, level:=EventLevel.Warning,
                         data:=New Dictionary(Of String, Object) From {{"from", fromModel}, {"to", toModel}, {"reason", reason}, {"level", nextLevel.Value}})
            _log.Info("Reasoning", $"Escalated task {t.Id} to {toModel}: {reason}", t.AgentId, t.Id)
            Return True
        End Function

        ''' <summary>Re-attaches results for parked/in-flight tool calls when a task resumes.</summary>
        ''' <returns>Wait keys (and deadline) when a re-run idempotent call had to park again; the task must then wait.</returns>
        Private Async Function ResumeAsync(ctx As TaskExecutionContext, cp As TaskCheckpoint, registry As AgentToolRegistry) As Task(Of (Keys As List(Of String), Deadline As DateTime?))
            Dim reparked As New List(Of PendingToolCall)
            Dim reparkDeadline As DateTime? = Nothing
            ' 1. Tool calls interrupted by a crash/restart.
            If cp.InFlightToolCalls IsNot Nothing AndAlso cp.InFlightToolCalls.Count > 0 Then
                Dim answered = New HashSet(Of String)(cp.Transcript.Where(Function(m) m.Role = "tool").Select(Function(m) m.ToolCallId))
                For Each cal In cp.InFlightToolCalls
                    If answered.Contains(cal.Id) OrElse cp.PendingToolCalls.Any(Function(p) p.ToolCallId = cal.Id) Then Continue For
                    Dim tool = registry.TryGet(cal.Name)
                    Dim content As String
                    If tool IsNot Nothing AndAlso tool.IsIdempotent Then
                        Dim r = Await _tools.ExecuteAsync(ToolCtx(ctx, cal.Id, Nothing), cal, registry).ConfigureAwait(False)
                        content = r.Content
                        If r.Signal = ToolSignal.Park Then
                            Dim pc As New PendingToolCall With {.ToolCallId = cal.Id, .ToolName = cal.Name, .WaitKey = r.WaitKey,
                                                                .ExtraWaitKeys = New List(Of String)(If(r.ExtraWaitKeys, New List(Of String)))}
                            cp.PendingToolCalls.Add(pc)
                            reparked.Add(pc)
                            If r.WaitDeadlineUtc.HasValue AndAlso (Not reparkDeadline.HasValue OrElse r.WaitDeadlineUtc.Value > reparkDeadline.Value) Then reparkDeadline = r.WaitDeadlineUtc
                            Continue For
                        End If
                    Else
                        content = "ERROR: Outcome unknown — the host restarted while this tool call was running. Verify the current state before repeating it."
                    End If
                    cp.Transcript.Add(ChatMessage.ForTool(cal.Id, cal.Name, content))
                Next
                cp.InFlightToolCalls.Clear()
            End If

            ' 2. Parked calls whose results have arrived (agent replies, human answers, approvals, subagents, timeouts).
            Dim stillPending As New List(Of PendingToolCall)
            For Each p In cp.PendingToolCalls
                If reparked.Contains(p) Then Continue For
                Dim keys = p.AllWaitKeys()
                If keys.Count > 1 Then
                    ' One call waiting on several results (wait_for_subagents): return them all together.
                    Dim parts = keys.Select(Function(k)
                                                Dim res As String = Nothing
                                                If cp.Resolutions.TryGetValue(k, res) Then Return res
                                                Return Resolutions.Timeout($"No result for {k}.")
                                            End Function).ToList()
                    cp.Transcript.Add(ChatMessage.ForTool(p.ToolCallId, p.ToolName, Untrusted.Wrap("subagents", "[" & String.Join(",", parts) & "]")))
                    For Each k In keys
                        cp.Resolutions.Remove(k)
                    Next
                    Continue For
                End If
                Dim resolution As String = Nothing
                If Not cp.Resolutions.TryGetValue(p.WaitKey, resolution) Then
                    stillPending.Add(p)
                    Continue For
                End If
                Dim kind = Resolutions.KindOf(resolution)
                Dim content As String
                If kind = "subagent_result" Then
                    content = Untrusted.Wrap("subagent", resolution)
                ElseIf kind = "approval" Then
                    Dim el As JsonElement
                    JsonUtil.TryParseElement(resolution, el)
                    Dim status = JsonUtil.GetString(el, "status")
                    If String.Equals(status, "Approved", StringComparison.OrdinalIgnoreCase) Then
                        Dim original = cp.Transcript.Where(Function(m) m.ToolCalls IsNot Nothing).SelectMany(Function(m) m.ToolCalls).FirstOrDefault(Function(c) c.Id = p.ToolCallId)
                        If original IsNot Nothing Then
                            Dim r = Await _tools.ExecuteAsync(ToolCtx(ctx, p.ToolCallId, Nothing), original, registry, approved:=True).ConfigureAwait(False)
                            content = r.Content
                        Else
                            content = "ERROR: Approved, but the original call could not be found."
                        End If
                    Else
                        Dim decidedBy = JsonUtil.GetString(el, "decidedBy")
                        content = $"ERROR: This call was not approved ({status}{If(String.IsNullOrEmpty(decidedBy) OrElse decidedBy = "human", " by an administrator", " by " & decidedBy)}). " &
                                  JsonUtil.GetString(el, "response")
                    End If
                ElseIf kind = "timeout" Then
                    content = "ERROR: " & resolution
                Else
                    Dim source = If(kind = "human_answer", "human", "agent-reply")
                    content = Untrusted.Wrap(source, resolution)
                End If
                cp.Transcript.Add(ChatMessage.ForTool(p.ToolCallId, p.ToolName, content))
                cp.Resolutions.Remove(p.WaitKey)
            Next
            ' Any call still unresolved (should not normally happen) is answered with a timeout so the transcript stays valid.
            For Each p In stillPending
                cp.Transcript.Add(ChatMessage.ForTool(p.ToolCallId, p.ToolName, "ERROR: No result was received for this call."))
            Next
            cp.PendingToolCalls = reparked
            Await ctx.SaveCheckpointAsync(cp).ConfigureAwait(False)
            Return (reparked.SelectMany(Function(p) p.AllWaitKeys()).ToList(), reparkDeadline)
        End Function
    End Class

End Namespace
