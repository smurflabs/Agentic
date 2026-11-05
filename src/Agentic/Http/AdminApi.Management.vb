Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: operations center, task queue management, subagents, custom models and model grants, agent editor metadata.
    Partial Friend NotInheritable Class AdminApi

        Private Shared ReadOnly InternalToolDescriptions As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"remember", "Save private long-term memories"}, {"recall", "Search private memory"}, {"forget", "Delete own memories"},
            {"complete_task", "Finish a task with a result (always available)"}, {"report_progress", "Write progress notes to the activity log"},
            {"create_followup_task", "Queue follow-up tasks for itself"}, {"list_contacts", "See permitted contacts"},
            {"send_agent_message", "Send information to other agents"}, {"request_agent_help", "Ask other agents and wait for the answer"},
            {"delegate_task", "Assign tasks to other agents"}, {"reply_to_agent", "Answer messages"}, {"broadcast_notification", "Notify a group"},
            {"get_my_budget", "See its balance and model prices"}, {"request_escalation", "Ask for a stronger model"},
            {"save_artifact", "Store files/reports"}, {"read_artifact", "Read owned or shared files"}, {"list_artifacts", "List files"},
            {"ask_human", "Ask an administrator (Inbox)"}, {"analyze_image", "Vision via cloud vision models (paid)"},
            {"generate_image", "Image generation (paid)"}, {"spawn_subagent", "Spawn temporary parallel subagents"},
            {"wait_for_subagents", "Collect subagent results"}, {"list_subagents", "List own subagents"}, {"cancel_subagent", "Stop own subagents"},
            {"consult_model", "Ask an admin-granted additional model"}, {"switch_model", "Continue a task on an admin-granted model"},
            {"update_plan", "Keep a plan for the current task (every agent)"}, {"read_web_page", "Read a public web page (with web search)"},
            {"run_code", "Run Python/Node/bash in the code sandbox (assisted+)"}, {"http_request", "Call web APIs on the allowed hosts, with vault secrets"},
            {"list_secrets", "See the names of vault secrets it may use"}, {"search_knowledge", "Search its knowledge bases"},
            {"set_goal", "Set its own standing goals (assisted+)"}, {"update_goal", "Pause, finish or change its goals (assisted+)"},
            {"schedule_task", "Schedule recurring work for itself (assisted+)"}, {"list_schedules", "List its schedules"}, {"cancel_schedule", "Remove schedules it set"},
            {"create_plan", "Plan bigger work as dependent tasks (assisted+)"}, {"create_webhook", "Create webhooks that give it tasks (assisted+)"},
            {"list_webhooks", "List its webhooks"}, {"delete_webhook", "Delete webhooks it created"},
            {"team_status", "See the agents it manages and hired"}, {"assign_task", "Give its team work, with bounties and reviews"},
            {"cancel_agent_task", "Cancel work it assigned"}, {"hire_agent", "Hire helper agents (Autonomy.CanHire)"}, {"retire_agent", "Retire agents it hired"},
            {"notify_human", "Message a person out-of-band (chat webhook or email)"}, {"request_budget", "Ask for more budget"},
            {"list_goals", "List its standing goals"}, {"record_goal_progress", "Record progress on a goal"}, {"author_skill", "Save a reusable procedure"},
            {"list_skills", "List its skills"}, {"use_skill", "Load one of its skills"}, {"propose_profile_change", "Propose a change to its own behaviour"},
            {"search_lessons", "Search the team's lessons"}, {"search_web", "Search the web"},
            {"market_prices", "Crypto prices and exchange rates (the Money tab; every agent)"}, {"news_articles", "Recent news from the RSS feeds (the News tab; every agent)"},
            {"stock_quotes", "Stock and index quotes for any ticker (the Stocks tab; every agent)"},
            {"polymarket", "Polymarket prediction markets: odds, live prices, trades, holders, wallets (the Polymarket tab; every agent)"}}

        Private Function ModelInfo(m As ModelRuntime) As Object
            Dim p = _h.Pricing.GetPrice(m.Key)
            Return New With {
                .key = m.Key, .modelId = m.Options.ModelId, .tier = m.Options.Tier, .isLocal = m.Options.IsLocal, .isCustom = m.Options.IsCustom,
                .enabled = m.Options.Enabled, .priced = p.IsPriced, .inputPerMillion = p.InputPerMillion, .outputPerMillion = p.OutputPerMillion,
                .capabilities = m.Options.Capabilities.Select(Function(c) c.ToString()).ToList(), .description = m.Options.Description, .useFor = m.Options.UseFor,
                .byo = m.Options.ByoOwner IsNot Nothing, .name = If(m.Options.ByoOwner IsNot Nothing, _h.Byo?.Get(m.Key)?.Name, Nothing)}
        End Function

        ' =========================================================================================
        ' Agent editor
        ' =========================================================================================

        ''' <summary>Everything the agent editor needs to offer valid choices.</summary>
        Private Async Function AgentFormOptions(rc As RequestContext) As Task
            Dim o = _h.Options
            ' Owners an administrator can give the agent to (User accounts pay for their agents; administrators' never stop).
            Dim owners As Object = Nothing
            If rc.User.IsAdmin Then
                owners = (Await _h.Accounts.ListAccountsAsync().ConfigureAwait(False)).Select(Function(a) New With {.username = a.Username, .isAdmin = a.IsAdmin, .balanceMicros = a.BalanceMicros}).ToList()
            End If
            ' Only the models this account may use itself.
            ' A user's own model (My models): only for its owner's agents, whatever their other model limits are.
            Dim usable = Function(m As ModelRuntime) If(m.Options.ByoOwner IsNot Nothing, String.Equals(m.Options.ByoOwner, rc.User.Name, StringComparison.OrdinalIgnoreCase), rc.User.CanUseModel(m.Options.ModelId))
            Await rc.OkAsync(New With {
                .owners = owners, .isAdmin = rc.User.IsAdmin,
                .baseProfiles = _h.Agents.Resolver.BaseProfileIds,
                .models = _h.ModelRegistry.All().Where(Function(m) Not m.Options.IsCustom AndAlso usable(m)).Select(AddressOf ModelInfo).ToList(),
                .grantableModels = _h.ModelRegistry.All().Where(Function(m) Not m.Options.IsLocal AndAlso m.Options.Enabled AndAlso m.Options.ByoOwner Is Nothing AndAlso usable(m)).Select(AddressOf ModelInfo).ToList(),
                .internalTools = ConfigValidator.KnownInternalTools.Where(Function(t) t <> "*").Select(Function(t) New With {
                    .name = t, .description = If(InternalToolDescriptions.ContainsKey(t), InternalToolDescriptions(t), ""),
                    .isDefault = ProfileResolver.DefaultInternalTools.Contains(t)}).ToList(),
                .mcpServers = o.McpServers.Where(Function(s) rc.User.CanUseMcp(s.Id)).Select(Function(s) New With {.id = s.Id, .name = s.Name, .enabled = s.Enabled}).ToList(),
                .agents = _h.Agents.GetSnapshots().Select(Function(s) New With {.id = s.Id, .name = s.Name, .role = s.Role}).ToList(),
                .groups = o.Communication.Groups.Keys.ToList(),
                .purposes = [Enum].GetNames(Of AiPurpose)(), .handlers = ConfigValidator.KnownHandlers,
                .messageTypes = [Enum].GetNames(Of MessageType)(), .priorities = [Enum].GetNames(Of PriorityLevel)(),
                .subagents = New With {o.Subagents.Enabled, o.Subagents.MaxPerAgent, o.Subagents.DefaultSpendCap, o.Subagents.MaxSpendCap,
                                       o.Subagents.DefaultTimeoutMinutes, o.Subagents.MaxTimeoutMinutes},
                .economy = New With {o.Economy.DefaultInitialBalance, o.Economy.Currency},
                .maxAgents = o.Host.MaxAgents, .agentCount = _h.Agents.AgentCount}).ConfigureAwait(False)
        End Function

        ''' <summary>The agent's definition as stored (overrides only), plus whether it can be edited here.</summary>
        Private Async Function AgentDefinition(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim d = Await _h.Agents.GetDefinitionAsync(id).ConfigureAwait(False)
            If d.Profile Is Nothing Then Throw ApiException.NotFound("AGENT_NOT_FOUND", "No definition for this agent.")
            Await rc.OkAsync(New With {.source = d.Source, .editable = d.Source = "Admin", .profile = d.Profile, .owner = _h.Agents.OwnerOf(id),
                                       .grants = _h.ModelAccess.GrantsFor(id).Select(Function(g) New With {g.ModelKey, g.UseFor, g.AllowSubagents}).ToList()}).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Task queue management
        ' =========================================================================================

        Private Async Function EditTask(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim taskId = rc.Route("taskId")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim e As New TaskEdit With {.Name = JsonUtil.GetString(b, "name"), .Description = JsonUtil.GetString(b, "description")}
            Dim prText = JsonUtil.GetString(b, "priority")
            Dim pr As PriorityLevel
            If prText IsNot Nothing Then
                If Not [Enum].TryParse(prText, True, pr) Then Throw ApiException.BadRequest("Invalid priority.")
                e.Priority = pr
            End If
            Dim puEl As JsonElement
            If b.TryGetProperty("purpose", puEl) Then
                If puEl.ValueKind = JsonValueKind.Null OrElse (puEl.ValueKind = JsonValueKind.String AndAlso puEl.GetString() = "") Then
                    e.ClearPurpose = True
                Else
                    Dim pu As AiPurpose
                    If Not [Enum].TryParse(JsonUtil.GetString(b, "purpose"), True, pu) Then Throw ApiException.BadRequest("Invalid purpose.")
                    e.Purpose = pu
                End If
            End If
            Dim mkEl As JsonElement
            If b.TryGetProperty("modelKey", mkEl) Then
                If mkEl.ValueKind = JsonValueKind.Null OrElse (mkEl.ValueKind = JsonValueKind.String AndAlso mkEl.GetString() = "") Then e.ClearModel = True Else e.ModelKey = mkEl.GetString()
            End If
            If JsonUtil.GetBool(b, "runNow", False) Then e.RunNow = True
            Dim runAt = JsonUtil.GetString(b, "runAtUtc")
            Dim runAtValue As DateTime
            If Not String.IsNullOrWhiteSpace(runAt) Then
                If Not DateTime.TryParse(runAt, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, runAtValue) Then
                    Throw ApiException.BadRequest("runAtUtc must be an ISO-8601 date/time.")
                End If
                e.NextRunUtc = runAtValue
            End If
            Dim mr = JsonUtil.GetInt(b, "maxRetries", -1)
            If mr >= 0 Then e.MaxRetries = mr
            Dim r = Await _h.Agents.EditTaskAsync(id, taskId, e).ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "edit-task", "task", taskId, JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {"position":"top"|"bottom"} or {"before": taskId} or {"after": taskId}.</summary>
        Private Async Function MoveTask(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim taskId = rc.Route("taskId")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim position = JsonUtil.GetString(b, "position")
            Dim r = Await _h.Agents.MoveTaskAsync(id, taskId, JsonUtil.GetString(b, "before"), JsonUtil.GetString(b, "after"),
                                                  position = "top", position = "bottom").ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "move-task", "task", taskId, JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        Private Async Function TaskAction(rc As RequestContext, action As String) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim taskId = rc.Route("taskId")
            Dim r As AgentOperationResult
            Select Case action
                Case "hold" : r = Await _h.Agents.HoldTaskAsync(id, taskId).ConfigureAwait(False)
                Case "release" : r = Await _h.Agents.ReleaseTaskAsync(id, taskId).ConfigureAwait(False)
                Case "clone" : r = Await _h.Agents.CloneTaskAsync(id, taskId, rc.User.Name).ConfigureAwait(False)
                Case Else : r = Await _h.Agents.CancelTaskAsync(id, taskId).ConfigureAwait(False)
            End Select
            If r.Success Then Await AuditAsync(rc, action & "-task", "task", taskId, New With {.agentId = id, .newTaskId = If(action = "clone", r.TaskId, Nothing)}).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {"action":"cancel|hold|release|retry|top|bottom", "taskIds":[...]}.</summary>
        Private Async Function BulkTasks(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim action = JsonUtil.GetString(b, "action", "")
            Dim ids = JsonUtil.GetStringList(b, "taskIds")
            If ids.Count = 0 Then Throw ApiException.BadRequest("taskIds is required.")
            If Not {"cancel", "hold", "release", "retry", "top", "bottom"}.Contains(action) Then Throw ApiException.BadRequest("Unknown action.")
            Dim results = Await _h.Agents.BulkTaskActionAsync(id, action, ids, rc.User.Name).ConfigureAwait(False)
            Await AuditAsync(rc, "bulk-" & action, "agent", id, New With {.taskIds = ids, .succeeded = results.Where(Function(x) x.Success).Count()}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.succeeded = results.Where(Function(x) x.Success).Count(), .failed = results.Where(Function(x) Not x.Success).Count(), .results = results}).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Subagents
        ' =========================================================================================

        Private Async Function ListSubagents(rc As RequestContext) As Task
            Dim list = Await _h.Agents.ListSubagentsAsync(rc.QueryString("parent"), Nothing, rc.QueryString("status"), rc.QueryInt("limit", 100, 1, 500)).ConfigureAwait(False)
            Await rc.OkAsync(list.Where(Function(s) rc.User.CanAccessAgent(s.ParentAgentId)).ToList()).ConfigureAwait(False)
        End Function

        ''' <summary>One subagent (its result too), however long ago it ran.</summary>
        Private Async Function GetSubagentInfo(rc As RequestContext) As Task
            Dim s = Await _h.Agents.GetSubagentAsync(rc.Route("subagentId")).ConfigureAwait(False)
            If s Is Nothing OrElse Not rc.User.CanAccessAgent(s.ParentAgentId) Then Throw ApiException.NotFound("SUBAGENT_NOT_FOUND", "Subagent does not exist.")
            Await rc.OkAsync(s).ConfigureAwait(False)
        End Function

        Private Async Function AgentSubagentsList(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Await rc.OkAsync(Await _h.Agents.ListSubagentsAsync(id, rc.QueryString("taskId"), rc.QueryString("status"), rc.QueryInt("limit", 100, 1, 500)).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function CancelSubagent(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim subId = rc.Route("subagentId")
            Dim s = Await _h.Agents.GetSubagentAsync(subId).ConfigureAwait(False)
            If s Is Nothing OrElse Not String.Equals(s.ParentAgentId, id, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.NotFound("SUBAGENT_NOT_FOUND", "Subagent does not exist for this agent.")
            Dim r = Await _h.Agents.CancelSubagentAsync(subId, "Cancelled by administrator " & rc.User.Name).ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "cancel-subagent", "agent", id, New With {.subagentId = subId}).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Custom models and grants
        ' =========================================================================================

        Private Function CustomModels(rc As RequestContext) As Task
            Dim grants = _h.ModelAccess.AllGrants()
            Return rc.OkAsync(_h.ModelAccess.CustomModels().Select(Function(m) New With {
                .definition = m, .pricing = _h.Pricing.GetPrice(m.Key),
                .grants = grants.Where(Function(g) String.Equals(g.ModelKey, m.Key, StringComparison.OrdinalIgnoreCase)).ToList()}).ToList())
        End Function

        Private Async Function SaveCustomModel(rc As RequestContext, isNew As Boolean) As Task
            Dim def = Await rc.ReadJsonAsync(Of ModelOptions)().ConfigureAwait(False)
            If def Is Nothing Then Throw ApiException.BadRequest("Model definition is required.")
            If Not isNew Then def.Key = rc.Route("key")
            Try
                def = Await _h.ModelAccess.SaveCustomModelAsync(def, rc.User.Name, isNew).ConfigureAwait(False)
            Catch ex As ModelAccessException
                Throw ApiException.BadRequest(ex.Message, "INVALID_MODEL")
            End Try
            Await AuditAsync(rc, If(isNew, "register-model", "update-model"), "model", def.Key, def).ConfigureAwait(False)
            Await rc.WriteJsonAsync(If(isNew, 201, 200), New With {.ok = True, .definition = def}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteCustomModel(rc As RequestContext) As Task
            Dim key = rc.Route("key")
            If Not Await _h.ModelAccess.DeleteCustomModelAsync(key, rc.User.Name).ConfigureAwait(False) Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "Custom model does not exist.")
            Await AuditAsync(rc, "delete-model", "model", key).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Active grants (?agent=, ?model=), or the full history with ?history=1.</summary>
        Private Async Function ListGrants(rc As RequestContext) As Task
            If rc.QueryString("history") = "1" Then
                Await rc.OkAsync(Await _h.ModelAccess.ListHistoryAsync(rc.QueryString("agent"), rc.QueryString("model"), rc.QueryInt("limit", 200, 1, 1000)).ConfigureAwait(False)).ConfigureAwait(False)
                Return
            End If
            Dim agent = rc.QueryString("agent")
            Dim model = rc.QueryString("model")
            Await rc.OkAsync(_h.ModelAccess.AllGrants().Where(Function(g) (agent Is Nothing OrElse String.Equals(g.AgentId, agent, StringComparison.OrdinalIgnoreCase)) AndAlso
                                                                       (model Is Nothing OrElse String.Equals(g.ModelKey, model, StringComparison.OrdinalIgnoreCase)) AndAlso
                                                                       rc.User.CanAccessAgent(g.AgentId)).ToList()).ConfigureAwait(False)
        End Function

        ''' <summary>The agent's models: from its profile, granted by administrators, and still grantable.</summary>
        Private Async Function AgentModelAccess(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim p = _h.Agents.GetEffectiveProfile(id)
            Dim grants = _h.ModelAccess.GrantsFor(id)
            Dim all = VisibleModels(rc).ToList()
            Dim profileModels = all.Where(Function(m) _h.Selection.AccessFor(m, p) = ModelAccessKind.Profile).Select(Function(m) New With {
                .model = ModelInfo(m), .usable = _h.Selection.IsUsableBy(m, p, Nothing), .aboveMaxTier = m.Options.Tier > p.MaxTier}).ToList()
            Dim grantable = all.Where(Function(m) Not m.Options.IsLocal AndAlso m.Options.Enabled AndAlso m.Options.ByoOwner Is Nothing AndAlso _h.Selection.AccessFor(m, p) = ModelAccessKind.None).Select(AddressOf ModelInfo).ToList()
            Dim history = Await _h.ModelAccess.ListHistoryAsync(id, Nothing, 50).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .isSubagent = p.IsSubagent, .maxTier = p.MaxTier, .profileModels = profileModels,
                .grants = grants.Select(Function(g) New With {.grant = g, .model = If(_h.ModelRegistry.TryGet(g.ModelKey) Is Nothing, Nothing, ModelInfo(_h.ModelRegistry.TryGet(g.ModelKey))),
                                                              .usable = _h.Selection.IsUsableBy(_h.ModelRegistry.TryGet(g.ModelKey), p, Nothing)}).ToList(),
                .grantable = grantable, .history = history}).ConfigureAwait(False)
        End Function

        ''' <summary>PUT body: {"useFor": "...", "allowSubagents": false}. Grants or updates access to an additional cloud model.</summary>
        Private Async Function GrantModel(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim key = rc.Route("key")
            RequireModelKeyAccess(rc, key)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim g As ModelGrant
            Try
                g = Await _h.ModelAccess.GrantAsync(id, key, JsonUtil.GetString(b, "useFor"), JsonUtil.GetBool(b, "allowSubagents", False), rc.User.Name).ConfigureAwait(False)
            Catch ex As ModelAccessException
                Throw ApiException.BadRequest(ex.Message, "INVALID_GRANT")
            End Try
            Await AuditAsync(rc, "grant-model", "agent", id, New With {.model = key, g.UseFor, g.AllowSubagents}).ConfigureAwait(False)
            Await rc.OkAsync(g).ConfigureAwait(False)
        End Function

        Private Async Function RevokeGrant(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim key = rc.Route("key")
            If Not Await _h.ModelAccess.RevokeAsync(id, key, rc.User.Name).ConfigureAwait(False) Then Throw ApiException.NotFound("GRANT_NOT_FOUND", "This agent has no grant for that model.")
            Await AuditAsync(rc, "revoke-model", "agent", id, New With {.model = key}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Model ids the provider offers (from the last provider check), marked when already configured.</summary>
        Private Function AvailableModels(rc As RequestContext) As Task
            Dim configured = _h.ModelRegistry.All().ToDictionary(Function(m) m.Options.ModelId, Function(m) m.Key, StringComparer.OrdinalIgnoreCase)
            Dim ids = If(_h.LastProviderCheck?.AvailableModels, New List(Of String))
            Return rc.OkAsync(New With {
                .checkedUtc = _h.LastProviderCheck?.CheckedUtc,
                .models = ids.Select(Function(m) New With {.modelId = m, .configuredAs = If(configured.ContainsKey(m), configured(m), Nothing)}).ToList()})
        End Function

        ' =========================================================================================
        ' Operations center
        ' =========================================================================================

        Private Async Function Operations(rc As RequestContext) As Task
            Dim running = _h.Agents.RunningWork().Where(Function(s) rc.User.CanAccessAgent(If(s.ParentAgentId, s.Id))).ToList()
            Dim waiting = Await _h.Tasks.ListByStatusAllAsync(AgentTaskStatus.Waiting, 200).ConfigureAwait(False)
            Dim held = Await _h.Tasks.ListByStatusAllAsync(AgentTaskStatus.Blocked, 200).ConfigureAwait(False)
            Dim failed = Await _h.Tasks.ListFailedSinceAsync(DateTime.UtcNow.AddDays(-1), 25).ConfigureAwait(False)
            Dim open = Await _h.Tasks.OpenCountsAsync().ConfigureAwait(False)
            Dim subagents = Await _h.Agents.ListSubagentsAsync(Nothing, Nothing, SubagentStatus.Running.ToString(), 200).ConfigureAwait(False)
            Dim snapshots = _h.Agents.GetSnapshots()
            Dim queues = snapshots.Select(Function(s)
                                              Dim c As Dictionary(Of String, Long) = Nothing
                                              open.TryGetValue(s.Id, c)
                                              Dim n = Function(k As String) If(c IsNot Nothing AndAlso c.ContainsKey(k), c(k), 0L)
                                              Return New With {.agentId = s.Id, .name = s.Name, .state = s.State, .queued = n("Pending") + n("Retrying"),
                                                               .waiting = n("Waiting"), .held = n("Blocked"), .running = n("Running")}
                                          End Function).Where(Function(q) rc.User.CanAccessAgent(q.agentId)).ToList()
            Dim pending = Await _h.Approvals.CountPendingAsync().ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .running = running, .waiting = waiting.Where(Function(t) rc.User.CanAccessAgent(t.AgentId)).Select(AddressOf TaskDto).ToList(),
                .held = held.Where(Function(t) rc.User.CanAccessAgent(t.AgentId)).Select(AddressOf TaskDto).ToList(),
                .recentFailures = failed.Where(Function(t) rc.User.CanAccessAgent(t.AgentId)).Select(AddressOf TaskDto).ToList(),
                .subagents = subagents.Where(Function(s) rc.User.CanAccessAgent(s.ParentAgentId)).ToList(),
                .queues = queues, .models = _h.Gateway.ModelStatus(), .pendingApprovals = pending,
                .paused = snapshots.Where(Function(s) s.State = AgentState.Paused.ToString()).Select(Function(s) s.Id).ToList(),
                .totals = New With {.running = running.Count, .queued = queues.Sum(Function(q) q.queued), .waiting = queues.Sum(Function(q) q.waiting),
                                    .held = queues.Sum(Function(q) q.held), .subagents = subagents.Count},
                .economy = New With {.globalSpentTodayMicros = _h.Wallets.GlobalSpentTodayMicros, .globalDailyLimit = _h.Options.Economy.GlobalDailyCloudLimit}}).ConfigureAwait(False)
        End Function

        Private Async Function GlobalControl(rc As RequestContext, action As String) As Task
            Dim result As Object
            Select Case action
                Case "pause-all"
                    Dim n = Await _h.Agents.PauseAllAsync().ConfigureAwait(False)
                    result = New With {.ok = True, .paused = n, .message = $"Paused {n} agent(s); running tasks finish first."}
                Case "resume-all"
                    Dim n = Await _h.Agents.ResumeAllAsync().ConfigureAwait(False)
                    result = New With {.ok = True, .resumed = n, .message = $"Resumed {n} agent(s)."}
                Case Else
                    Dim r = Await _h.Agents.EmergencyStopAsync(rc.User.Name).ConfigureAwait(False)
                    result = New With {.ok = True, .paused = r.Paused, .cancelledTasks = r.CancelledTasks, .cancelledSubagents = r.CancelledSubagents,
                                       .message = $"Emergency stop: {r.Paused} paused, {r.CancelledTasks} running task(s) and {r.CancelledSubagents} subagent(s) cancelled. Queued work is kept; use Resume all to continue."}
            End Select
            Await AuditAsync(rc, action, "host", Nothing, result).ConfigureAwait(False)
            Await rc.OkAsync(result).ConfigureAwait(False)
        End Function

        ''' <summary>Hourly totals across agents for the last N hours (gaps are zero-filled).</summary>
        Private Async Function HourlyStats(rc As RequestContext) As Task
            Dim hours = rc.QueryInt("hours", 24, 1, 24 * 14)
            Dim now = DateTime.UtcNow
            Dim start = New DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-(hours - 1))
            Dim rows = Await _h.Telemetry.StatisticsAsync(rc.QueryString("agent"), start).ConfigureAwait(False)
            Dim byBucket = rows.ToDictionary(Function(r) CStr(r("bucketUtc")), StringComparer.Ordinal)
            Dim result As New List(Of Dictionary(Of String, Object))
            For i = 0 To hours - 1
                Dim b = start.AddHours(i)
                Dim key = b.ToString("yyyy-MM-ddTHH:00:00Z", Globalization.CultureInfo.InvariantCulture)
                Dim row As Dictionary(Of String, Object) = Nothing
                If Not byBucket.TryGetValue(key, row) Then
                    row = New Dictionary(Of String, Object) From {{"bucketUtc", key}, {"tasksCompleted", 0L}, {"tasksFailed", 0L}, {"aiCalls", 0L}, {"aiFailures", 0L},
                                                                  {"toolCalls", 0L}, {"messagesSent", 0L}, {"tokens", 0L}, {"costMicros", 0L}}
                End If
                result.Add(row)
            Next
            Await rc.OkAsync(result).ConfigureAwait(False)
        End Function
    End Class

End Namespace
