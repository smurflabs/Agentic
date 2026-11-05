Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ''' <summary>JSON admin API. Every state-changing call is authorized, validated and written to the audit log.</summary>
    Partial Friend NotInheritable Class AdminApi
        Private ReadOnly _h As HostServices
        Private ReadOnly _auth As AuthService
        Private _sseClients As Integer
        ''' <summary>Each account's open live event streams, oldest first (guarded by itself).</summary>
        Private ReadOnly _sseByUser As New Dictionary(Of String, List(Of CancellationTokenSource))(StringComparer.OrdinalIgnoreCase)

        Public Sub New(host As HostServices, auth As AuthService)
            _h = host
            _auth = auth
        End Sub

        Public Sub Register(r As Router)
            r.Map("GET", "/healthz", AuthLevel.Anonymous, AddressOf Healthz)
            r.Map("POST", "/api/auth/login", AuthLevel.Anonymous, AddressOf Login)
            r.Map("POST", "/api/auth/logout", AuthLevel.Read, AddressOf Logout)
            r.Map("GET", "/api/auth/me", AuthLevel.Read, AddressOf Me_)

            r.Map("GET", "/api/status", AuthLevel.Read, AddressOf Status)
            r.Map("GET", "/api/meta/agent-form", AuthLevel.Admin, AddressOf AgentFormOptions)

            ' Agents
            r.Map("GET", "/api/agents", AuthLevel.Read, AddressOf ListAgents)
            r.Map("POST", "/api/agents", AuthLevel.Admin, AddressOf SaveAgent)
            r.Map("GET", "/api/agents/{id}", AuthLevel.Read, AddressOf GetAgent)
            r.Map("DELETE", "/api/agents/{id}", AuthLevel.Admin, AddressOf DeleteAgent)
            r.Map("GET", "/api/agents/{id}/definition", AuthLevel.Admin, AddressOf AgentDefinition)
            r.Map("GET", "/api/agents/{id}/memory", AuthLevel.Admin, AddressOf AgentMemory)
            r.Map("DELETE", "/api/agents/{id}/memory/{memoryId}", AuthLevel.Admin, AddressOf DeleteMemory)
            r.Map("GET", "/api/agents/{id}/activity", AuthLevel.Read, AddressOf AgentActivity)
            r.Map("GET", "/api/agents/{id}/messages", AuthLevel.Read, AddressOf AgentMessages)
            r.Map("GET", "/api/agents/{id}/communications", AuthLevel.Read, AddressOf AgentCommunications)
            r.Map("GET", "/api/agents/{id}/tools", AuthLevel.Read, AddressOf AgentTools)
            r.Map("GET", "/api/agents/{id}/ai-usage", AuthLevel.Read, AddressOf AgentAiUsage)
            r.Map("GET", "/api/agents/{id}/ledger", AuthLevel.Read, AddressOf AgentLedger)
            r.Map("GET", "/api/agents/{id}/schedules", AuthLevel.Read, AddressOf AgentSchedules)
            r.Map("GET", "/api/agents/{id}/artifacts", AuthLevel.Read, AddressOf AgentArtifacts)
            r.Map("POST", "/api/agents/{id}/start", AuthLevel.Admin, Function(rc) Control(rc, "start"))
            r.Map("POST", "/api/agents/{id}/stop", AuthLevel.Admin, Function(rc) Control(rc, "stop"))
            r.Map("POST", "/api/agents/{id}/pause", AuthLevel.Admin, Function(rc) Control(rc, "pause"))
            r.Map("POST", "/api/agents/{id}/resume", AuthLevel.Admin, Function(rc) Control(rc, "resume"))
            r.Map("POST", "/api/agents/{id}/restart", AuthLevel.Admin, Function(rc) Control(rc, "restart"))
            r.Map("POST", "/api/agents/{id}/run-now", AuthLevel.Admin, Function(rc) Control(rc, "run-now"))
            r.Map("POST", "/api/agents/{id}/message", AuthLevel.Admin, AddressOf AdminMessage)
            r.Map("POST", "/api/agents/{id}/profit", AuthLevel.Admin, AddressOf RecordProfit)
            r.Map("POST", "/api/agents/{id}/adjust", AuthLevel.Admin, AddressOf AdjustBalance)
            r.Map("POST", "/api/agents/{id}/daily-limit", AuthLevel.Admin, AddressOf SetDailyLimit)

            ' Task queue management (per agent, so token agent allowlists apply)
            r.Map("GET", "/api/agents/{id}/tasks", AuthLevel.Read, AddressOf AgentTasks)
            r.Map("POST", "/api/agents/{id}/task", AuthLevel.SubmitTask, AddressOf SubmitTask)
            r.Map("POST", "/api/agents/{id}/tasks/bulk", AuthLevel.Admin, AddressOf BulkTasks)
            r.Map("GET", "/api/agents/{id}/tasks/{taskId}", AuthLevel.Read, AddressOf AgentTaskDetail)
            r.Map("PATCH", "/api/agents/{id}/tasks/{taskId}", AuthLevel.Admin, AddressOf EditTask)
            r.Map("POST", "/api/agents/{id}/tasks/{taskId}/move", AuthLevel.Admin, AddressOf MoveTask)
            r.Map("POST", "/api/agents/{id}/tasks/{taskId}/hold", AuthLevel.Admin, Function(rc) TaskAction(rc, "hold"))
            r.Map("POST", "/api/agents/{id}/tasks/{taskId}/release", AuthLevel.Admin, Function(rc) TaskAction(rc, "release"))
            r.Map("POST", "/api/agents/{id}/tasks/{taskId}/clone", AuthLevel.Admin, Function(rc) TaskAction(rc, "clone"))
            r.Map("POST", "/api/agents/{id}/tasks/{taskId}/cancel", AuthLevel.Admin, Function(rc) TaskAction(rc, "cancel"))
            r.Map("POST", "/api/agents/{id}/tasks/{taskId}/retry", AuthLevel.Admin, AddressOf RetryTask)
            r.Map("POST", "/api/agents/{id}/cancel-task", AuthLevel.Admin, AddressOf CancelTask)
            r.Map("GET", "/api/tasks", AuthLevel.Read, AddressOf AllTasks)

            ' Subagents
            r.Map("GET", "/api/subagents", AuthLevel.Read, AddressOf ListSubagents)
            r.Map("GET", "/api/subagents/{subagentId}", AuthLevel.Read, AddressOf GetSubagentInfo)
            r.Map("GET", "/api/agents/{id}/subagents", AuthLevel.Read, AddressOf AgentSubagentsList)
            r.Map("POST", "/api/agents/{id}/subagents/{subagentId}/cancel", AuthLevel.Admin, AddressOf CancelSubagent)

            ' Models, custom models and per-agent model grants
            r.Map("GET", "/api/models", AuthLevel.Read, AddressOf Models)
            r.Map("POST", "/api/models/{key}/reset-circuit", AuthLevel.Admin, AddressOf ResetCircuit)
            r.Map("GET", "/api/models/paused-providers", AuthLevel.Read, AddressOf PausedProviders)
            r.Map("GET", "/api/models/context-limits", AuthLevel.Read, AddressOf ContextLimitsList)
            r.Map("DELETE", "/api/models/context-limits", AuthLevel.Admin, AddressOf ContextLimitForget)
            r.Map("POST", "/api/models/providers/{provider}/resume", AuthLevel.Admin, AddressOf ResumeProvider)
            r.Map("GET", "/api/models/custom", AuthLevel.Read, AddressOf CustomModels)
            r.Map("POST", "/api/models/custom", AuthLevel.Admin, Function(rc) SaveCustomModel(rc, True))
            r.Map("PUT", "/api/models/custom/{key}", AuthLevel.Admin, Function(rc) SaveCustomModel(rc, False))
            r.Map("DELETE", "/api/models/custom/{key}", AuthLevel.Admin, AddressOf DeleteCustomModel)
            r.Map("GET", "/api/model-grants", AuthLevel.Read, AddressOf ListGrants)
            r.Map("GET", "/api/agents/{id}/model-access", AuthLevel.Read, AddressOf AgentModelAccess)
            r.Map("PUT", "/api/agents/{id}/model-grants/{key}", AuthLevel.Admin, AddressOf GrantModel)
            r.Map("DELETE", "/api/agents/{id}/model-grants/{key}", AuthLevel.Admin, AddressOf RevokeGrant)
            r.Map("GET", "/api/ai/status", AuthLevel.Read, AddressOf AiStatus)
            r.Map("GET", "/api/ai/available-models", AuthLevel.Admin, AddressOf AvailableModels)
            r.Map("POST", "/api/ai/check", AuthLevel.Admin, AddressOf AiCheck)
            r.Map("GET", "/api/ai/calls", AuthLevel.Read, AddressOf AiCalls)

            ' Operations center
            r.Map("GET", "/api/operations", AuthLevel.Read, AddressOf Operations)
            r.Map("POST", "/api/operations/pause-all", AuthLevel.Admin, Function(rc) GlobalControl(rc, "pause-all"))
            r.Map("POST", "/api/operations/resume-all", AuthLevel.Admin, Function(rc) GlobalControl(rc, "resume-all"))
            r.Map("POST", "/api/operations/emergency-stop", AuthLevel.Admin, Function(rc) GlobalControl(rc, "emergency-stop"))
            r.Map("GET", "/api/stats/hourly", AuthLevel.Read, AddressOf HourlyStats)
            RegisterLearning(r)
            RegisterMetrics(r)
            RegisterWebSearch(r)
            RegisterUsers(r)
            RegisterModelControl(r)
            RegisterGlobalTasks(r)
            RegisterResults(r)
            RegisterInsights(r)
            RegisterOpenAi(r)
            RegisterAnthropic(r)
            RegisterPlatform(r)
            RegisterChat(r)
            RegisterChatSchedules(r)
            RegisterChatMonitor(r)
            RegisterChatMore(r)
            RegisterChatArtifacts(r)
            RegisterChatNav(r)
            RegisterMcpServers(r)
            RegisterSandbox(r)
            RegisterUsage(r)
            RegisterUpdates(r)
            RegisterInstall(r)
            RegisterAccounts(r)
            RegisterKeybindings(r)
            RegisterAudio(r)
            RegisterImages(r)
            RegisterMusic(r)
            RegisterMarketsAndNews(r)
            RegisterStocks(r)
            RegisterPolymarket(r)
            RegisterAutonomy(r)
            RegisterAutonomyPlus(r)
            _router = r

            ' Communications
            r.Map("GET", "/api/communications", AuthLevel.Read, AddressOf Communications)
            r.Map("GET", "/api/communications/topology", AuthLevel.Read, AddressOf Topology)
            r.Map("GET", "/api/correlations/{correlationId}", AuthLevel.Read, AddressOf Correlation)
            r.Map("POST", "/api/messages/{messageId}/cancel", AuthLevel.Admin, AddressOf CancelMessage)

            r.Map("GET", "/api/mcp", AuthLevel.Read, AddressOf McpStatus)
            r.Map("POST", "/api/mcp/{serverId}/reconnect", AuthLevel.Admin, AddressOf McpReconnect)

            r.Map("GET", "/api/finance", AuthLevel.Read, AddressOf Finance)
            r.Map("GET", "/api/finance/chat-usage", AuthLevel.Read, AddressOf FinanceChatUsage)
            r.Map("GET", "/api/finance/ledger", AuthLevel.Read, AddressOf FinanceLedger)
            r.Map("GET", "/api/pricing", AuthLevel.Read, AddressOf Pricing)
            r.Map("PUT", "/api/pricing/{key}", AuthLevel.Admin, AddressOf SetPricing)

            r.Map("GET", "/api/approvals", AuthLevel.Read, AddressOf ListApprovals)
            r.Map("POST", "/api/approvals/{approvalId}/resolve", AuthLevel.Admin, AddressOf ResolveApproval)
            r.Map("GET", "/api/approval-rules", AuthLevel.Read, AddressOf ListApprovalRules)
            r.Map("POST", "/api/approval-rules", AuthLevel.Admin, AddressOf AddApprovalRule)
            r.Map("DELETE", "/api/approval-rules/{ruleId}", AuthLevel.Admin, AddressOf RevokeApprovalRule)

            r.Map("GET", "/api/config/validate", AuthLevel.Admin, AddressOf ValidateConfig)
            r.Map("GET", "/api/config/summary", AuthLevel.Admin, AddressOf ConfigSummary)
            r.Map("GET", "/api/audit", AuthLevel.Admin, AddressOf Audit)
            r.Map("GET", "/api/events/recent", AuthLevel.Read, AddressOf RecentEvents)
            r.Map("GET", "/api/events/stream", AuthLevel.Read, AddressOf EventStream)
            r.Map("GET", "/api/artifacts/{artifactId}/content", AuthLevel.Admin, AddressOf ArtifactContent)
        End Sub

        ' =========================================================================================
        ' Helpers
        ' =========================================================================================

        Private Function AgentIdOrThrow(rc As RequestContext) As String
            Dim id = rc.Route("id")
            If Not _h.Agents.Exists(id) Then Throw ApiException.NotFound("AGENT_NOT_FOUND", "Agent does not exist.")
            Return id
        End Function

        Private Function AuditAsync(rc As RequestContext, action As String, targetType As String, targetId As String, Optional details As Object = Nothing) As Task
            _h.Events.Emit(AgentEventType.AdminAction, $"{rc.User?.Name}: {action} {targetType} {targetId}", If(targetType = "agent", targetId, Nothing))
            Return _h.AgentsRepo.AuditAsync(If(rc.User?.Name, "anonymous"), action, targetType, targetId, details, rc.RemoteAddress)
        End Function

        Private Shared Function ResultAsync(rc As RequestContext, r As AgentOperationResult) As Task
            If r.Success Then Return rc.OkAsync(r)
            Dim status = If(r.Code = "AGENT_NOT_FOUND" OrElse r.Code = "TASK_NOT_FOUND", 404, 409)
            Throw New ApiException(status, r.Code, r.Message)
        End Function

        Private Shared Function ParseMoney(el As JsonElement, name As String, Optional required As Boolean = True) As Decimal?
            Dim p As JsonElement
            If Not el.TryGetProperty(name, p) OrElse p.ValueKind = JsonValueKind.Null Then
                If required Then Throw ApiException.BadRequest($"'{name}' is required.")
                Return Nothing
            End If
            Dim v As Decimal
            If p.ValueKind = JsonValueKind.Number AndAlso p.TryGetDecimal(v) Then Return v
            If p.ValueKind = JsonValueKind.String AndAlso Decimal.TryParse(p.GetString(), Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture, v) Then Return v
            Throw ApiException.BadRequest($"'{name}' must be a number.")
        End Function

        Private Shared Function TaskDto(t As AgentTask) As Object
            Return New With {
                .id = t.Id, .agentId = t.AgentId, .name = t.Name, .description = TextUtil.Truncate(t.Description, 2000), .kind = t.Kind.ToString(),
                .handler = t.Handler, .purpose = t.Purpose?.ToString(), .status = t.Status.ToString(), .priority = t.Priority.ToString(),
                .retryCount = t.RetryCount, .maxRetries = t.MaxRetries, .nextRunUtc = t.NextRunUtc, .createdUtc = t.CreatedUtc,
                .startedUtc = t.StartedUtc, .completedUtc = t.CompletedUtc, .error = t.ErrorMessage, .result = JsonUtil.ParseElementOrNull(t.Result),
                .parentTaskId = t.ParentTaskId, .parentAgentId = t.ParentAgentId, .delegatingAgentId = t.DelegatingAgentId, .correlationId = t.CorrelationId,
                .delegationDepth = t.DelegationDepth, .chainLength = t.ChainLength, .ancestry = t.Ancestry, .sourceMessageId = t.SourceMessageId,
                .scheduleId = t.ScheduleId, .ladderLevel = t.CurrentLadderLevel, .escalations = t.EscalationCount, .waitingOn = t.WaitingOn,
                .waitDeadlineUtc = t.WaitDeadlineUtc, .costMicros = t.CostMicros, .createdBy = t.CreatedBy, .queueRank = t.QueueRank, .modelKey = t.ModelKey,
                .startTier = t.StartTier, .updatedUtc = t.UpdatedUtc, .deadlineUtc = t.DeadlineUtc, .deadlineEnforced = t.DeadlineEnforced,
                .runPastDeadline = t.RunPastDeadline, .deadlinePassedUtc = t.DeadlinePassedUtc, .autoApproveTools = t.AutoApproveTools}
        End Function

        Private Shared Function MessageDto(m As AgentMessage, Optional includeContent As Boolean = True) As Object
            Return New With {
                .id = m.Id, .correlationId = m.CorrelationId, .parentMessageId = m.ParentMessageId, .sender = m.SenderAgentId, .recipient = m.RecipientAgentId,
                .type = m.MessageType.ToString(), .subject = m.Subject, .content = If(includeContent, TextUtil.Truncate(m.Content, 4000), Nothing),
                .payload = If(includeContent, JsonUtil.ParseElementOrNull(m.StructuredPayload), Nothing), .attachments = m.Attachments,
                .priority = m.Priority.ToString(), .status = m.Status.ToString(), .createdUtc = m.CreatedUtc, .deliveredUtc = m.DeliveredUtc,
                .startedUtc = m.StartedUtc, .completedUtc = m.CompletedUtc, .durationMs = m.DurationMs, .expiresUtc = m.ExpiresUtc,
                .replyTo = m.ReplyToMessageId, .requiresResponse = m.RequiresResponse, .responseMessageId = m.ResponseMessageId,
                .relatedTaskId = m.RelatedTaskId, .senderTaskId = m.SenderTaskId, .delegationDepth = m.DelegationDepth, .chainLength = m.ChainLength,
                .ancestry = m.Ancestry, .rejectionReason = m.RejectionReason, .contentPurged = m.ContentPurgedUtc.HasValue}
        End Function

        Private Function WalletDto(w As WalletSnapshot) As Object
            If w Is Nothing Then Return Nothing
            Return New With {
                .agentId = w.AgentId, .balanceMicros = w.BalanceMicros, .reservedMicros = w.ReservedMicros, .availableMicros = w.AvailableMicros,
                .lifetimeSpendMicros = w.LifetimeSpendMicros, .lifetimeProfitMicros = w.LifetimeProfitMicros, .lifetimeCreditMicros = w.LifetimeCreditMicros,
                .spentTodayMicros = w.SpentTodayMicros, .dailyLimitMicros = w.DailyLimitMicros, .netMicros = w.NetMicros, .roi = w.Roi, .updatedUtc = w.UpdatedUtc,
                .burnRateMicrosPerDay = w.BurnRateMicrosPerDay, .runwayDays = w.RunwayDays, .lowRunway = _h.Wallets.IsLowRunway(w)}
        End Function

        Private Shared Function ProfileDto(p As EffectiveProfile) As Object
            If p Is Nothing Then Return Nothing
            Return New With {
                .id = p.Id, .name = p.Name, .role = p.Role, .description = p.Description, .personality = p.Personality, .source = p.Source,
                .isSubagent = p.IsSubagent, .ownerAgentId = p.OwnerAgentId, .maxSubagents = p.MaxSubagents, .subagentDefaultSpendCap = p.SubagentDefaultSpendCap,
                .spendCapMicros = p.SpendCapMicros,
                .enabled = p.Enabled, .baseProfileId = p.BaseProfileId, .systemPrompt = p.SystemPrompt, .safetyRules = p.SafetyRules, .goals = p.Goals,
                .allowedModels = p.AllowedModels.OrderBy(Function(x) x), .maxTier = p.MaxTier, .defaultPurpose = p.DefaultPurpose.ToString(),
                .allowedMcpServers = p.AllowedMcpServers, .allowedMcpTools = p.AllowedMcpTools, .deniedMcpTools = p.DeniedMcpTools,
                .internalTools = p.InternalTools.OrderBy(Function(x) x),
                .communication = New With {
                    .allowedRecipients = p.AllowedRecipients.OrderBy(Function(x) x), .sendTypes = p.AllowedSendMessageTypes.Select(Function(x) x.ToString()),
                    .receiveTypes = p.AllowedReceiveMessageTypes.Select(Function(x) x.ToString()), .maxSendPriority = p.MaxSendPriority.ToString(),
                    .groups = p.Groups, .canBroadcast = p.CanBroadcast, .maxMessagesPerMinute = p.MaxMessagesPerMinute,
                    .maxRequestsPerMinute = p.MaxRequestsPerMinute, .maxDelegationsPerHour = p.MaxDelegationsPerHour, .maxOutstandingRequests = p.MaxOutstandingRequests},
                .ai = New With {.maxAiCallsPerHour = p.MaxAiCallsPerHour, .maxAiCallsPerDay = p.MaxAiCallsPerDay, .maxTokensPerHour = p.MaxTokensPerHour,
                                .maxTokensPerDay = p.MaxTokensPerDay, .maxConcurrentAiCalls = p.MaxConcurrentAiCalls},
                .tasks = New With {.maxReasoningTurns = p.MaxReasoningTurns, .maxToolCalls = p.MaxToolCalls, .maxTotalTurns = p.MaxTotalTurns, .maxTaskDurationSeconds = p.MaxTaskDurationSeconds,
                                   .maxToolOutputChars = p.MaxToolOutputChars, .maxRetries = p.MaxRetries, .escalateOnRetry = p.EscalateOnRetry},
                .memory = New With {.maxMemories = p.MaxMemories, .recallCount = p.RecallCount, .autoRecordTaskResults = p.AutoRecordTaskResults},
                .economy = New With {.initialBalance = p.InitialBalance, .dailySpendLimit = p.DailySpendLimit},
                .idleDelaySeconds = p.IdleDelaySeconds,
                .schedules = p.Schedules.Select(Function(s) New With {.id = s.Id, .name = s.Name, .handler = s.Handler, .intervalSeconds = s.IntervalSeconds, .cron = s.Cron, .enabled = s.Enabled}),
                .autonomy = New With {.level = p.AutonomyLevel, .supervisorAgentId = p.SupervisorAgentId, .canHire = p.CanHire, .initiativeMinutes = p.InitiativeMinutes,
                                      .verifyPasses = p.VerifyPasses, .levelTools = p.LevelTools.OrderBy(Function(x) x)}}
        End Function

        ' =========================================================================================
        ' Auth / status
        ' =========================================================================================

        Private Function ProviderSummary() As Object
            Dim p = _h.LastProviderCheck
            If p Is Nothing Then Return Nothing
            Return New With {.checkedUtc = p.CheckedUtc, .reachable = p.Reachable, .errorMessage = p.ErrorMessage, .availableModelCount = p.AvailableModels.Count, .missingModels = p.MissingModels}
        End Function

        Private Function Healthz(rc As RequestContext) As Task
            Return rc.OkAsync(New With {.status = "ok"})
        End Function

        Private Async Function Login(rc As RequestContext) As Task
            Dim body = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim s = Await _auth.LoginAsync(JsonUtil.GetString(body, "username"), JsonUtil.GetString(body, "password"), rc.RemoteAddress).ConfigureAwait(False)
            rc.Http.Response.Headers.Add("Set-Cookie", SessionCookie(s.Id))
            Await _h.AgentsRepo.AuditAsync(s.User.Name, "login", "session", Nothing, Nothing, rc.RemoteAddress).ConfigureAwait(False)
            Await rc.OkAsync(New With {.user = s.User.Name, .csrfToken = s.CsrfToken, .scopes = s.User.Scopes}).ConfigureAwait(False)
        End Function

        Private Function Logout(rc As RequestContext) As Task
            _auth.Logout(rc.User.SessionId)
            rc.Http.Response.Headers.Add("Set-Cookie", $"{AuthService.CookieName}=; Path=/; HttpOnly; SameSite=Strict; Max-Age=0")
            Return rc.OkAsync(New With {.ok = True})
        End Function

        ''' <summary>The sign-in cookie; it lasts as long as the session (Server.SessionMinutes) from when it was last sent.</summary>
        Private Function SessionCookie(sessionId As String) As String
            Return $"{AuthService.CookieName}={sessionId}; Path=/; HttpOnly; SameSite=Strict; Max-Age={CLng(_auth.SessionLifetime.TotalSeconds)}" &
                   If(_h.Options.Server.UseHttps, "; Secure", "")
        End Function

        Private Async Function Me_(rc As RequestContext) As Task
            Dim u = rc.User
            ' The cookie is sent again on every page load, so it keeps sliding with the session instead of ending a fixed time after sign-in.
            If u.ViaSession AndAlso Not String.IsNullOrEmpty(u.SessionId) Then rc.Http.Response.Headers.Add("Set-Cookie", SessionCookie(u.SessionId))
            ' Effective permissions: "View workforce" comes with every permission that acts on agents.
            Dim perms = If(u.IsAdmin, Permissions.Catalog.Select(Function(p) p.Key).ToList(), u.Permissions.ToList())
            If u.SeesWorkforce AndAlso Not perms.Contains(Permissions.ViewWorkforce) Then perms.Add(Permissions.ViewWorkforce)
            Dim acc = If(u.ViaSession OrElse u.ViaApiKey, Await _h.Accounts.GetAccountAsync(u.Name).ConfigureAwait(False), Nothing)
            Await rc.OkAsync(New With {.user = u.Name, .displayName = If(u.DisplayName, u.Name), .role = u.Role, .isAdmin = u.IsAdmin, .csrfToken = u.CsrfToken, .scopes = u.Scopes,
                                       .allowedAgents = u.AllowedAgents, .allowedModels = u.AllowedModels, .permissions = perms, .workforce = u.SeesWorkforce,
                                       .balanceMicros = If(acc?.BalanceMicros, 0L), .charged = acc IsNot Nothing AndAlso acc.Charged,
                                       .surcharge = Not u.IsAdmin AndAlso Await _h.Accounts.HasSurchargeAsync(u.Name).ConfigureAwait(False), .publicOrigin = PublicOrigin(rc)}).ConfigureAwait(False)
        End Function

        Private Async Function Status(rc As RequestContext) As Task
            If Not rc.User.SeesWorkforce Then
                ' Without the Workforce: the console's own state only, nothing about agents.
                Await rc.OkAsync(New With {.name = _h.Options.Host.InstanceName, .version = _h.Version, .startedUtc = _h.StartedUtc,
                                           .uptimeSeconds = CLng((DateTime.UtcNow - _h.StartedUtc).TotalSeconds)}).ConfigureAwait(False)
                Return
            End If
            Dim counts = _h.Agents.CountByState()
            Dim mcpStatus = _h.Mcp.GetStatus()
            Dim pending = Await _h.Approvals.CountPendingAsync().ConfigureAwait(False)
            Dim c = _h.Options.Communication
            Await rc.OkAsync(New With {
                .name = _h.Options.Host.InstanceName, .version = _h.Version, .startedUtc = _h.StartedUtc,
                .uptimeSeconds = CLng((DateTime.UtcNow - _h.StartedUtc).TotalSeconds),
                .agents = New With {.total = _h.Agents.AgentCount, .byState = counts, .activeSubagents = _h.Agents.ActiveSubagentCount},
                .mcp = New With {.configured = mcpStatus.Count, .connected = mcpStatus.Where(Function(m) m.State = Mcp.McpConnectionState.Connected).Count(),
                                 .enabled = mcpStatus.Where(Function(m) m.Enabled).Count()},
                .ai = New With {.provider = ProviderSummary(), .spillovers = _h.Gateway.SpilloverCount, .escalations = _h.Gateway.EscalationCount,
                                .localQueue = _h.Gateway.ModelStatus().Where(Function(m) CBool(m("isLocal"))).ToList()},
                .communication = New With {.enabled = c.Enabled, .maxDelegationDepth = c.MaxDelegationDepth, .maxChainLength = c.MaxCommunicationChainLength},
                .economy = New With {.enabled = _h.Options.Economy.Enabled, .globalSpentTodayMicros = _h.Wallets.GlobalSpentTodayMicros,
                                     .globalDailyLimit = _h.Options.Economy.GlobalDailyCloudLimit},
                .pendingApprovals = pending,
                .newResults = Await _h.Results.CountNewAsync().ConfigureAwait(False),
                .activeGlobalTasks = If(_h.GlobalTasks Is Nothing, 0L, Await _h.GlobalTasks.CountActiveAsync().ConfigureAwait(False)),
                .deadAgents = _h.Agents.GetSnapshots(False).Where(Function(a) a.State = "Dead").Select(Function(a) a.Id).ToList(),
                .pausedProviders = PausedProviderItems(),
                .eventsDropped = _h.Events.DroppedInbound}).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Agents
        ' =========================================================================================

        ''' <summary>?details=1 adds queue counts, wallet and subagent/grant counts; ?includeSubagents=1 lists running subagents too.</summary>
        Private Async Function ListAgents(rc As RequestContext) As Task
            Dim list = _h.Agents.GetSnapshots(rc.QueryString("includeSubagents") = "1").Where(Function(s) rc.User.CanAccessAgent(If(s.ParentAgentId, s.Id))).ToList()
            If rc.QueryString("details") = "1" Then
                Dim open = Await _h.Tasks.OpenCountsAsync().ConfigureAwait(False)
                Dim wallets = (Await _h.Wallets.ListAsync().ConfigureAwait(False)).ToDictionary(Function(w) w.AgentId, StringComparer.OrdinalIgnoreCase)
                Dim running = Await _h.Agents.ListSubagentsAsync(Nothing, Nothing, SubagentStatus.Running.ToString(), 500).ConfigureAwait(False)
                For Each s In list
                    Dim c As Dictionary(Of String, Long) = Nothing
                    open.TryGetValue(s.Id, c)
                    Dim n = Function(k As String) If(c IsNot Nothing AndAlso c.ContainsKey(k), c(k), 0L)
                    s.QueuedTasks = n("Pending") + n("Retrying")
                    s.WaitingTasks = n("Waiting")
                    s.HeldTasks = n("Blocked")
                    Dim w As WalletSnapshot = Nothing
                    If wallets.TryGetValue(If(s.ParentAgentId, s.Id), w) Then
                        s.AvailableMicros = w.AvailableMicros
                        s.SpentTodayMicros = w.SpentTodayMicros
                        s.RunwayDays = w.RunwayDays
                        s.LowRunway = _h.Wallets.IsLowRunway(w)
                    End If
                    s.ActiveSubagents = running.Where(Function(x) String.Equals(x.ParentAgentId, s.Id, StringComparison.OrdinalIgnoreCase)).Count()
                    s.GrantedModels = _h.ModelAccess.GrantsFor(s.Id).Count
                Next
            End If
            Await rc.OkAsync(list).ConfigureAwait(False)
        End Function

        Private Async Function GetAgent(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim counts = Await _h.Tasks.CountsByStatusAsync(id).ConfigureAwait(False)
            Dim profile = _h.Agents.GetEffectiveProfile(id)
            Dim wallet = Await _h.Wallets.GetAsync(If(profile?.BillingAgentId, id)).ConfigureAwait(False)
            Dim subagent = If(profile IsNot Nothing AndAlso profile.IsSubagent, Await _h.Agents.GetSubagentAsync(id).ConfigureAwait(False), Nothing)
            Dim snap = _h.Agents.GetSnapshot(id)
            If snap IsNot Nothing AndAlso Not snap.IsSubagent Then
                snap.ActiveSubagents = (Await _h.Agents.ListSubagentsAsync(id, Nothing, SubagentStatus.Running.ToString(), 100).ConfigureAwait(False)).Count
                snap.GrantedModels = _h.ModelAccess.GrantsFor(id).Count
            End If
            Await rc.OkAsync(New With {
                .snapshot = snap, .profile = ProfileDto(profile), .wallet = WalletDto(wallet),
                .taskCounts = counts, .subagent = subagent, .grants = _h.ModelAccess.GrantsFor(id)}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Creates or updates an admin-managed agent. Body: a ProfileOptions object, or {"profile": {...}, "grants": [{modelKey, useFor, allowSubagents}]}
        ''' to also set the agent's additional model grants (the list replaces the agent's grants).
        ''' </summary>
        Private Async Function SaveAgent(rc As RequestContext) As Task
            Dim body = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim profileEl As JsonElement
            Dim hasWrapper = body.TryGetProperty("profile", profileEl) AndAlso profileEl.ValueKind = JsonValueKind.Object
            Dim p As ProfileOptions
            Try
                p = JsonSerializer.Deserialize(Of ProfileOptions)(If(hasWrapper, profileEl.GetRawText(), body.GetRawText()), JsonUtil.Options)
            Catch ex As JsonException
                Throw ApiException.BadRequest("Invalid profile: " & ex.Message, "INVALID_JSON")
            End Try
            If p Is Nothing OrElse String.IsNullOrWhiteSpace(p.Id) Then Throw ApiException.BadRequest("Profile 'id' is required.")
            Dim grants As List(Of ModelGrant) = Nothing
            Dim grantsEl As JsonElement
            If hasWrapper AndAlso body.TryGetProperty("grants", grantsEl) AndAlso grantsEl.ValueKind = JsonValueKind.Array Then
                grants = grantsEl.EnumerateArray().Select(Function(g) New ModelGrant With {
                    .ModelKey = JsonUtil.GetString(g, "modelKey"), .UseFor = JsonUtil.GetString(g, "useFor", ""), .AllowSubagents = JsonUtil.GetBool(g, "allowSubagents", False)}).ToList()
                ' Validate grants before saving anything.
                For Each g In grants
                    Dim m = _h.ModelRegistry.TryGet(g.ModelKey)
                    If m Is Nothing Then Throw ApiException.BadRequest($"Grant: model '{g.ModelKey}' does not exist.")
                    If m.Options.IsLocal Then Throw ApiException.BadRequest($"Grant: '{g.ModelKey}' is a local model.")
                    If If(g.UseFor, "").Trim().Length < 3 Then Throw ApiException.BadRequest($"Grant: describe what {p.Id} should use '{g.ModelKey}' for.")
                    If Not m.Options.IsCustom AndAlso p.AllowedModels IsNot Nothing AndAlso p.AllowedModels.Contains(g.ModelKey, StringComparer.OrdinalIgnoreCase) Then
                        Throw ApiException.BadRequest($"Grant: '{g.ModelKey}' is already in the agent's standard models.")
                    End If
                Next
            End If
            ' A User account may only give an agent the models it may use itself.
            For Each key In If(p.AllowedModels, New List(Of String)).Concat(If(grants, New List(Of ModelGrant)).Select(Function(g) g.ModelKey))
                RequireModelKeyAccess(rc, key)
            Next
            ' Who pays for the agent: a new agent belongs to the account that creates it (administrators' agents never stop for
            ' money); only administrators give an agent to another account.
            Dim isNew = Not _h.Agents.Exists(p.Id)
            Dim ownerEl As JsonElement
            Dim ownerGiven = hasWrapper AndAlso body.TryGetProperty("owner", ownerEl) AndAlso (ownerEl.ValueKind = JsonValueKind.String OrElse ownerEl.ValueKind = JsonValueKind.Null)
            Dim owner As String = Nothing
            If ownerGiven AndAlso rc.User.IsAdmin Then
                owner = If(ownerEl.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrWhiteSpace(ownerEl.GetString()), ownerEl.GetString().Trim(), Nothing)
                If owner IsNot Nothing AndAlso Await _h.AgentsRepo.GetUserAsync(owner).ConfigureAwait(False) Is Nothing Then Throw ApiException.BadRequest($"Owner: there is no account '{owner}'.")
            ElseIf isNew AndAlso Not rc.User.IsAdmin Then
                owner = rc.User.Name
                ownerGiven = True
            End If
            ' A User account may only give an agent the MCP servers it may use itself (Users & access).
            If rc.User.IsRestrictedUser AndAlso rc.User.AllowedMcpServers.Count > 0 Then
                Dim notAllowed = If(p.AllowedMcpServers, New List(Of String)).Where(Function(s) s = "*" OrElse Not rc.User.CanUseMcp(s)).ToList()
                If notAllowed.Count > 0 Then Throw New ApiException(403, "MCP_NOT_ALLOWED", "You may not give an agent these MCP servers: " & String.Join(", ", notAllowed.Select(Function(s) If(s = "*", "every server (*)", s))))
            End If
            Dim modelKeys = _h.Options.Models.Select(Function(m) m.Key).ToList()
            Dim mcpIds = _h.Options.McpServers.Select(Function(m) m.Id).ToList()
            Dim report = Await _h.Agents.SaveAdminAgentAsync(p, mcpIds, modelKeys).ConfigureAwait(False)
            If Not report.IsValid Then
                Await rc.WriteJsonAsync(400, New With {.error = New With {.code = "INVALID_PROFILE", .message = String.Join(" ", report.Errors)}, .errors = report.Errors, .warnings = report.Warnings}).ConfigureAwait(False)
                Return
            End If
            Dim grantErrors As New List(Of String)
            If grants IsNot Nothing Then grantErrors = Await _h.ModelAccess.SyncGrantsAsync(p.Id, grants, rc.User.Name).ConfigureAwait(False)
            If ownerGiven AndAlso Not String.Equals(_h.Agents.OwnerOf(p.Id), owner, StringComparison.OrdinalIgnoreCase) Then
                Await _h.Agents.SetOwnerAsync(p.Id, owner).ConfigureAwait(False)
                ' The new owner's money decides whether the agent runs.
                If owner IsNot Nothing Then Await _h.Accounts.AnnounceAsync(owner).ConfigureAwait(False)
            End If
            Await AuditAsync(rc, "save-agent", "agent", p.Id, New With {.profile = p, .grants = grants, .owner = If(ownerGiven, owner, _h.Agents.OwnerOf(p.Id))}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(200, New With {.ok = True, .warnings = report.Warnings.Concat(grantErrors).ToList()}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteAgent(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim r = Await _h.Agents.DeleteAdminAgentAsync(id).ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "delete-agent", "agent", id).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        Private Async Function AgentTasks(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim statusText = rc.QueryString("status")
            Dim statuses As AgentTaskStatus() = Nothing
            If statusText = "queue" Then
                Dim q = Await _h.Tasks.ListQueueAsync(id, rc.QueryInt("limit", 100, 1, 500)).ConfigureAwait(False)
                Await rc.OkAsync(q.Select(AddressOf TaskDto).ToList()).ConfigureAwait(False)
                Return
            End If
            If Not String.IsNullOrEmpty(statusText) Then
                statuses = statusText.Split(","c).Select(Function(s)
                                                             Dim st As AgentTaskStatus
                                                             If Not [Enum].TryParse(s.Trim(), True, st) Then Throw ApiException.BadRequest($"Unknown status '{s}'.")
                                                             Return st
                                                         End Function).ToArray()
            End If
            Dim list = Await _h.Tasks.ListAsync(id, statuses, rc.QueryInt("limit", 50, 1, 500), rc.QueryInt("offset", 0, 0)).ConfigureAwait(False)
            Await rc.OkAsync(list.Select(AddressOf TaskDto).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function AgentTaskDetail(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim t = Await _h.Tasks.GetForAgentAsync(id, rc.Route("taskId")).ConfigureAwait(False)
            If t Is Nothing Then Throw ApiException.NotFound("TASK_NOT_FOUND", "Task does not exist.")
            Dim history = Await _h.Tasks.ListHistoryAsync(t.Id).ConfigureAwait(False)
            Dim ai = Await _h.Telemetry.ListAiCallsAsync(id, 100, t.Id).ConfigureAwait(False)
            Dim tools = Await _h.Telemetry.ListToolCallsAsync(id, 200, t.Id).ConfigureAwait(False)
            Dim cp = TaskCheckpoint.Parse(t.Checkpoint)
            Dim transcript As Object = Nothing
            ' Each step with the calls it made (name and arguments) and the tool it answers, so a failed task can be followed.
            If cp IsNot Nothing AndAlso rc.QueryString("transcript") = "1" Then
                transcript = cp.Transcript.Select(Function(m) New With {.role = m.Role, .content = TextUtil.Truncate(m.Content, 3000), .name = m.Name,
                    .toolCalls = m.ToolCalls?.Select(Function(c) New With {.name = c.Name, .arguments = TextUtil.Truncate(c.ArgumentsJson, 2000)}).ToList(), .toolCallId = m.ToolCallId}).ToList()
            ElseIf rc.QueryString("transcript") = "1" Then
                ' Finished reasoning tasks keep their conversation.
                Dim kept = JsonUtil.ParseElementOrNull(Await _h.Tasks.GetTranscriptAsync(t.Id).ConfigureAwait(False))
                If kept.HasValue Then
                    transcript = kept.Value.EnumerateArray().Select(Function(m) New With {.role = JsonUtil.GetString(m, "role"), .content = TextUtil.Truncate(JsonUtil.GetString(m, "content"), 3000),
                        .name = JsonUtil.GetString(m, "name"),
                        .toolCalls = If(m.TryGetProperty("toolCalls", Nothing) AndAlso m.GetProperty("toolCalls").ValueKind = JsonValueKind.Array,
                                        m.GetProperty("toolCalls").EnumerateArray().Select(Function(c) New With {.name = JsonUtil.GetString(c, "name"), .arguments = JsonUtil.GetString(c, "arguments")}).ToList(), Nothing)}).ToList()
                End If
            End If
            Dim canSeeCriteria = rc.User.HasPermission("tasks.manage") OrElse rc.User.IsAdmin
            Await rc.OkAsync(New With {
                .task = TaskDto(t), .history = history, .aiCalls = ai, .toolCalls = tools,
                .delivery = t.Delivery, .reviewCriteria = If(canSeeCriteria, t.ReviewCriteria, Nothing),
                .resultId = (Await _h.Results.FindBySourceAsync("Task", If(t.Delivery?.RootTaskId, t.Id)).ConfigureAwait(False))?.Id,
                .checkpoint = If(cp Is Nothing, Nothing, New With {.turn = cp.Turn, .toolCallsUsed = cp.ToolCallsUsed, .ladderLevel = cp.LadderLevel,
                                                                    .transcriptMessages = cp.Transcript.Count, .pending = cp.PendingToolCalls, .plan = cp.Plan}),
                .transcript = transcript}).ConfigureAwait(False)
        End Function

        Private Async Function RetryTask(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim r = Await _h.Agents.RetryTaskAsync(id, rc.Route("taskId")).ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "retry-task", "task", rc.Route("taskId")).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        Private Async Function AgentMemory(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim q = rc.QueryString("q")
            Dim limit = rc.QueryInt("limit", 100, 1, 500)
            Dim items = If(String.IsNullOrWhiteSpace(q), Await _h.MemoryStore.RecentAsync(id, limit).ConfigureAwait(False),
                                                         Await _h.MemoryStore.SearchAsync(id, q, Nothing, limit).ConfigureAwait(False))
            Dim total = Await _h.MemoryStore.CountAsync(id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.total = total, .items = items}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteMemory(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim ok = Await _h.MemoryStore.DeleteAsync(id, rc.Route("memoryId")).ConfigureAwait(False)
            If Not ok Then Throw ApiException.NotFound("MEMORY_NOT_FOUND", "Memory does not exist.")
            Await AuditAsync(rc, "delete-memory", "agent", id, New With {.memoryId = rc.Route("memoryId")}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function AgentActivity(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim list = Await _h.Telemetry.ListActivityAsync(id, rc.QueryInt("limit", 100, 1, 1000), eventType:=rc.QueryString("type"), beforeId:=rc.QueryInt("before", 0, 0)).ConfigureAwait(False)
            Await rc.OkAsync(list).ConfigureAwait(False)
        End Function

        Private Async Function AgentMessages(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim dir = rc.QueryString("direction", "all")
            Dim q As New MessageQuery With {.Limit = rc.QueryInt("limit", 100, 1, 500), .Offset = rc.QueryInt("offset", 0, 0)}
            Select Case dir
                Case "in" : q.RecipientAgentId = id
                Case "out" : q.SenderAgentId = id
                Case Else : q.AgentId = id
            End Select
            Dim list = Await _h.Messages.QueryAsync(q).ConfigureAwait(False)
            Await rc.OkAsync(list.Select(Function(m) MessageDto(m)).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function AgentCommunications(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim edges = Await _h.Messages.TopologyAsync(DateTime.UtcNow.AddDays(-7)).ConfigureAwait(False)
            Dim mine = edges.Where(Function(e) String.Equals(CStr(e("from")), id, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(CStr(e("to")), id, StringComparison.OrdinalIgnoreCase)).ToList()
            Dim p = _h.Agents.GetEffectiveProfile(id)
            Dim canReceiveFrom = _h.Profiles.AllProfiles().Where(Function(x) x.AllowedRecipients.Contains(id) OrElse x.AllowedRecipients.Contains("*")).Select(Function(x) x.Id).ToList()
            Await rc.OkAsync(New With {.allowedRecipients = p.AllowedRecipients, .canReceiveFrom = canReceiveFrom, .traffic7d = mine,
                                       .contacts = _h.Comms.GetContacts(id)}).ConfigureAwait(False)
        End Function

        Private Function AgentTools(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim reg = _h.ToolCatalog.BuildForAgent(_h.Agents.GetEffectiveProfile(id))
            Return rc.OkAsync(reg.All().Select(Function(t) New With {.name = t.Name, .description = t.Description, .source = t.Source.ToString(),
                                                                      .idempotent = t.IsIdempotent, .requiresApproval = t.RequiresApproval, .parameters = t.Parameters}).OrderBy(Function(t) t.name).ToList())
        End Function

        Private Async Function AgentAiUsage(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim day = Await _h.Telemetry.AiUsageByModelAsync(DateTime.UtcNow.AddDays(-1), id).ConfigureAwait(False)
            Dim week = Await _h.Telemetry.AiUsageByModelAsync(DateTime.UtcNow.AddDays(-7), id).ConfigureAwait(False)
            Dim recent = Await _h.Telemetry.ListAiCallsAsync(id, rc.QueryInt("limit", 50, 1, 500)).ConfigureAwait(False)
            Dim stats = Await _h.Telemetry.StatisticsAsync(id, DateTime.UtcNow.AddDays(-2)).ConfigureAwait(False)
            Await rc.OkAsync(New With {.last24h = day, .last7d = week, .recent = recent, .hourly = stats}).ConfigureAwait(False)
        End Function

        Private Async Function AgentLedger(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim w = Await _h.Wallets.GetAsync(id).ConfigureAwait(False)
            Dim entries = Await _h.Wallets.ListLedgerAsync(id, rc.QueryInt("limit", 100, 1, 1000), rc.QueryString("type")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.wallet = WalletDto(w), .entries = entries}).ConfigureAwait(False)
        End Function

        Private Async Function AgentSchedules(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Await rc.OkAsync(Await _h.Scheduler.ListAsync(id).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function AgentArtifacts(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Await rc.OkAsync(Await _h.Artifacts.ListAllAsync(rc.QueryInt("limit", 100, 1, 500), id).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function Control(rc As RequestContext, action As String) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim r As AgentOperationResult
            Select Case action
                Case "start" : r = Await _h.Agents.StartAgentAsync(id).ConfigureAwait(False)
                Case "stop" : r = Await _h.Agents.StopAgentAsync(id).ConfigureAwait(False)
                Case "pause" : r = Await _h.Agents.PauseAgentAsync(id).ConfigureAwait(False)
                Case "resume" : r = Await _h.Agents.ResumeAgentAsync(id).ConfigureAwait(False)
                Case "restart" : r = Await _h.Agents.RestartAgentAsync(id).ConfigureAwait(False)
                Case Else : r = Await _h.Agents.RunNowAsync(id).ConfigureAwait(False)
            End Select
            If r.Success Then Await AuditAsync(rc, action, "agent", id).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        Private Async Function SubmitTask(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim purposeText = JsonUtil.GetString(b, "purpose")
            Dim purpose As AiPurpose
            Dim pr As PriorityLevel = PriorityLevel.Normal
            If Not [Enum].TryParse(JsonUtil.GetString(b, "priority", "Normal"), True, pr) Then Throw ApiException.BadRequest("Invalid priority.")
            If purposeText IsNot Nothing AndAlso Not [Enum].TryParse(purposeText, True, purpose) Then Throw ApiException.BadRequest("Invalid purpose.")
            Dim payload = JsonUtil.GetRaw(b, "payload")
            Dim delay = JsonUtil.GetInt(b, "delaySeconds", 0)
            Dim tier = JsonUtil.GetInt(b, "startTier", 0)
            Dim req As New NewTaskRequest With {
                .Name = JsonUtil.GetString(b, "name"), .Description = JsonUtil.GetString(b, "description"), .Handler = JsonUtil.GetString(b, "handler", "reasoning"),
                .Purpose = If(purposeText Is Nothing, CType(Nothing, AiPurpose?), purpose), .Priority = pr, .Payload = payload,
                .NextRunUtc = If(delay > 0, DateTime.UtcNow.AddSeconds(delay), CType(Nothing, DateTime?)), .StartTier = If(tier > 0, tier, CType(Nothing, Integer?)),
                .CreatedBy = rc.User.Name, .IdempotencyKey = JsonUtil.GetString(b, "idempotencyKey"),
                .ModelKey = If(String.IsNullOrWhiteSpace(JsonUtil.GetString(b, "modelKey")), Nothing, JsonUtil.GetString(b, "modelKey")),
                .AddToFront = String.Equals(JsonUtil.GetString(b, "position"), "front", StringComparison.OrdinalIgnoreCase)}
            Dim runAt = JsonUtil.GetString(b, "runAtUtc")
            Dim runAtValue As DateTime
            If Not String.IsNullOrWhiteSpace(runAt) Then
                If Not DateTime.TryParse(runAt, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, runAtValue) Then
                    Throw ApiException.BadRequest("runAtUtc must be an ISO-8601 date/time.")
                End If
                req.NextRunUtc = runAtValue
            End If
            Dim maxRetries = JsonUtil.GetInt(b, "maxRetries", -1)
            If maxRetries >= 0 Then req.MaxRetries = Math.Min(maxRetries, 10)
            Dim delivery = Await ParseDeliveryAsync(rc, b, id).ConfigureAwait(False)
            req.Delivery = delivery.Delivery
            req.ReviewCriteria = delivery.Criteria
            ' Run options: approve tools automatically; a deadline (from when it is queued or starts) and whether to keep working past it.
            Dim run = ParseRunOptions(rc, b)
            req.AutoApproveTools = run.AutoApproveTools
            req.RunPastDeadline = run.RunPastDeadline
            If run.DeadlineMinutes.HasValue Then
                req.DeadlineUtc = If(req.NextRunUtc, DateTime.UtcNow).AddMinutes(run.DeadlineMinutes.Value)
                req.DeadlineEnforced = True
            End If
            Dim r = Await _h.Agents.SubmitTaskAsync(id, req).ConfigureAwait(False)
            If Not r.Success Then Throw New ApiException(If(r.Code = "DUPLICATE_TASK", 409, 400), r.Code, r.Message)
            Await AuditAsync(rc, "submit-task", "agent", id, New With {.taskId = r.TaskId, .name = req.Name, .modelKey = req.ModelKey, .front = req.AddToFront}).ConfigureAwait(False)
            ' Handed over from a chat: the chat notes it now, and gets the result when the task ends.
            If req.Delivery?.ChatId IsNot Nothing AndAlso _h.ChatReports IsNot Nothing Then
                Await _h.ChatReports.PostHandOverAsync(rc.User.Name, req.Delivery.ChatId, id, req.Name, $"/agents/{Uri.EscapeDataString(id)}?task={Uri.EscapeDataString(r.TaskId)}", False).ConfigureAwait(False)
            End If
            Await rc.WriteJsonAsync(201, r).ConfigureAwait(False)
        End Function

        Private Async Function CancelTask(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim r = Await _h.Agents.CancelTaskAsync(id, JsonUtil.GetString(b, "taskId")).ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "cancel-task", "task", r.TaskId).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function

        Private Async Function AdminMessage(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim typeText = JsonUtil.GetString(b, "type", "Information")
            Dim mt As MessageType
            If Not [Enum].TryParse(typeText, True, mt) OrElse Not {MessageType.Information, MessageType.Request, MessageType.StatusRequest, MessageType.Question}.Contains(mt) Then
                Throw ApiException.BadRequest("type must be Information, Question, Request or StatusRequest.")
            End If
            Dim r = Await _h.Comms.SendMessageAsync(AgentCommunicationService.AdminSender, New SendContext(), New OutgoingMessage With {
                .RecipientAgentId = id, .MessageType = mt, .Subject = JsonUtil.GetString(b, "subject", "Message from administrator"),
                .Content = JsonUtil.GetString(b, "content", ""), .Priority = PriorityLevel.High}, rc.CancellationToken).ConfigureAwait(False)
            If Not r.Success Then Throw New ApiException(409, r.ErrorCode, r.ErrorMessage)
            Await AuditAsync(rc, "message-agent", "agent", id, New With {.type = mt.ToString(), .messageId = r.MessageId}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, r).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Economy
        ' =========================================================================================

        Private Async Function RecordProfit(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim amount = ParseMoney(b, "amount").Value
            If amount <= 0 OrElse amount > 10000000D Then Throw ApiException.BadRequest("amount must be a positive number (USD).")
            Dim key = JsonUtil.GetString(b, "idempotencyKey")
            If String.IsNullOrWhiteSpace(key) Then Throw ApiException.BadRequest("idempotencyKey is required so the same profit is never credited twice.")
            ' Optional attribution: which task produced the profit (used by success case studies).
            Dim taskId = JsonUtil.GetString(b, "taskId")
            If String.IsNullOrWhiteSpace(taskId) Then taskId = Nothing
            If taskId IsNot Nothing AndAlso Await _h.Tasks.GetForAgentAsync(id, taskId).ConfigureAwait(False) Is Nothing Then Throw ApiException.BadRequest("taskId does not belong to this agent.")
            Dim r = Await _h.Wallets.CreditProfitAsync(id, TextUtil.DollarsToMicros(amount), JsonUtil.GetString(b, "description", "Profit credited"),
                                                       JsonUtil.GetString(b, "reference"), key, rc.User.Name, taskId).ConfigureAwait(False)
            If r.Applied Then Await AuditAsync(rc, "record-profit", "agent", id, New With {.amount = amount, .reference = JsonUtil.GetString(b, "reference"), .taskId = taskId, .entryId = r.EntryId}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.applied = r.Applied, .duplicate = r.Duplicate, .balanceMicros = r.BalanceAfterMicros, .entryId = r.EntryId}).ConfigureAwait(False)
        End Function

        Private Async Function AdjustBalance(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim amount = ParseMoney(b, "amount").Value
            If amount = 0 OrElse Math.Abs(amount) > 10000000D Then Throw ApiException.BadRequest("amount must be non-zero (USD, may be negative).")
            Dim key = If(JsonUtil.GetString(b, "idempotencyKey"), Ids.NewId("adj"))
            Dim r = Await _h.Wallets.AdjustAsync(id, TextUtil.DollarsToMicros(amount), JsonUtil.GetString(b, "description", "Balance adjustment"), key, rc.User.Name).ConfigureAwait(False)
            If r.Applied Then Await AuditAsync(rc, "adjust-balance", "agent", id, New With {.amount = amount, .entryId = r.EntryId}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.applied = r.Applied, .duplicate = r.Duplicate, .balanceMicros = r.BalanceAfterMicros}).ConfigureAwait(False)
        End Function

        Private Async Function SetDailyLimit(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim amount = ParseMoney(b, "amount", required:=False)
            If amount.HasValue AndAlso amount.Value < 0 Then Throw ApiException.BadRequest("amount must be >= 0 or null.")
            Await _h.Wallets.SetDailyLimitAsync(id, If(amount.HasValue, TextUtil.DollarsToMicros(amount.Value), CType(Nothing, Long?))).ConfigureAwait(False)
            Await AuditAsync(rc, "set-daily-limit", "agent", id, New With {.amount = amount}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function Finance(rc As RequestContext) As Task
            Dim wallets = (Await _h.Wallets.ListAsync().ConfigureAwait(False)).Where(Function(w) Sees(rc, w.AgentId)).ToList()
            Dim byModel = Await _h.Wallets.SpendByModelAsync(DateTime.UtcNow.AddDays(-7)).ConfigureAwait(False)
            Dim byModelToday = Await _h.Wallets.SpendByModelAsync(DateTime.UtcNow.Date).ConfigureAwait(False)
            Dim subSpend = (Await _h.Wallets.SubagentSpendAsync(DateTime.UtcNow.AddDays(-30)).ConfigureAwait(False)).Where(Function(x) Sees(rc, CStr(x("agentId")))).ToList()
            ' "Total balance" is the signed-in account's own money (its agents' wallets are their spending limits).
            Dim acc = If(rc.User.ViaSession OrElse rc.User.ViaApiKey, Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False), Nothing)
            Await rc.OkAsync(New With {
                .account = New With {.username = rc.User.Name, .balanceMicros = If(acc?.BalanceMicros, 0L), .charged = acc IsNot Nothing AndAlso acc.Charged},
                .subagentSpend30d = subSpend,
                .wallets = wallets.Select(AddressOf WalletDto).ToList(),
                .totals = New With {.balanceMicros = wallets.Sum(Function(w) w.BalanceMicros), .lifetimeSpendMicros = wallets.Sum(Function(w) w.LifetimeSpendMicros),
                                    .lifetimeProfitMicros = wallets.Sum(Function(w) w.LifetimeProfitMicros), .spentTodayMicros = wallets.Sum(Function(w) w.SpentTodayMicros)},
                .globalSpentTodayMicros = _h.Wallets.GlobalSpentTodayMicros, .globalDailyLimit = _h.Options.Economy.GlobalDailyCloudLimit,
                .lowRunwayDays = _h.Wallets.LowRunwayDays, .runwayWindowDays = _h.Options.Economy.RunwayWindowDays,
                .spendByModel7d = byModel, .spendByModelToday = byModelToday, .currency = _h.Options.Economy.Currency}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' How User accounts spend their balance on models through the Chat tab (messages, images, videos), for the Finance
        ''' graphs. Amounts are the real charges, each priced at the Model Square price in force when it happened.
        ''' </summary>
        Private Async Function FinanceChatUsage(rc As RequestContext) As Task
            Dim days = rc.QueryInt("days", 30, 1, 365)
            Dim since = DateTime.UtcNow.AddDays(-days)
            Dim byModel = Await _h.Accounts.ChatSpendByModelAsync(since).ConfigureAwait(False)
            Dim byUser = Await _h.Accounts.ChatSpendByUserAsync(since).ConfigureAwait(False)
            Dim daily = Await _h.Accounts.ChatSpendDailyAsync(since).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .days = days, .currency = _h.Options.Economy.Currency,
                .totalMicros = byModel.Sum(Function(m) Convert.ToInt64(m("micros"))),
                .charges = byModel.Sum(Function(m) Convert.ToInt64(m("calls"))),
                .byModel = byModel, .byUser = byUser, .daily = daily}).ConfigureAwait(False)
        End Function

        Private Async Function FinanceLedger(rc As RequestContext) As Task
            RequireAgentAccess(rc, rc.QueryString("agent"))
            Dim list = Await _h.Wallets.ListLedgerAsync(rc.QueryString("agent"), rc.QueryInt("limit", 200, 1, 1000), rc.QueryString("type")).ConfigureAwait(False)
            Await rc.OkAsync(list.Where(Function(e) Sees(rc, e.AgentId)).ToList()).ConfigureAwait(False)
        End Function

        Private Function Pricing(rc As RequestContext) As Task
            Return rc.OkAsync(_h.Pricing.All().Where(Function(p) Not ByoService.IsByoId(p.ModelKey)).ToList())
        End Function

        Private Async Function SetPricing(rc As RequestContext) As Task
            Dim key = rc.Route("key")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Try
                Dim inP = ParseMoney(b, "inputPerMillion", False), outP = ParseMoney(b, "outputPerMillion", False)
                Await _h.Pricing.SetPriceAsync(key, inP, outP, ParseMoney(b, "perImage", False), rc.User.Name).ConfigureAwait(False)
                ' Model Square has one price per model: an agent model's price is that model's price for people too.
                Dim m = _h.ModelRegistry.TryGet(key)
                If m IsNot Nothing AndAlso Not m.Options.IsLocal AndAlso inP.HasValue AndAlso outP.HasValue Then
                    Await _h.Accounts.SetPriceAsync(m.Options.ModelId, inP.Value, outP.Value, rc.User.Name).ConfigureAwait(False)
                End If
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("MODEL_NOT_FOUND", ex.Message)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "set-pricing", "model", key, JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await rc.OkAsync(_h.Pricing.GetPrice(key)).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Tasks / communications
        ' =========================================================================================

        ''' <summary>Task search across agents: ?agent=&amp;status=A,B&amp;q=text&amp;limit=&amp;offset=</summary>
        Private Async Function AllTasks(rc As RequestContext) As Task
            Dim statuses = If(rc.QueryString("status"), "").Split(","c, StringSplitOptions.RemoveEmptyEntries Or StringSplitOptions.TrimEntries)
            Dim list = Await _h.Tasks.SearchAsync(rc.QueryString("agent"), statuses, rc.QueryString("q"), rc.QueryInt("limit", 100, 1, 500), rc.QueryInt("offset", 0, 0)).ConfigureAwait(False)
            Await rc.OkAsync(list.Where(Function(t) rc.User.CanAccessAgent(t.AgentId)).Select(AddressOf TaskDto).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function Communications(rc As RequestContext) As Task
            Dim q As New MessageQuery With {
                .AgentId = rc.QueryString("agent"), .SenderAgentId = rc.QueryString("sender"), .RecipientAgentId = rc.QueryString("recipient"),
                .CorrelationId = rc.QueryString("correlationId"), .FromUtc = rc.QueryDate("from"), .ToUtc = rc.QueryDate("to"),
                .Limit = rc.QueryInt("limit", 100, 1, 500), .Offset = rc.QueryInt("offset", 0, 0)}
            Dim t = rc.QueryString("type")
            If t IsNot Nothing Then
                Dim mt As MessageType
                If Not [Enum].TryParse(t, True, mt) Then Throw ApiException.BadRequest("Unknown message type.")
                q.MessageType = mt
            End If
            Dim s = rc.QueryString("status")
            If s IsNot Nothing Then
                Dim ms As MessageStatus
                If Not [Enum].TryParse(s, True, ms) Then Throw ApiException.BadRequest("Unknown status.")
                q.Status = ms
            End If
            RequireAgentAccess(rc, q.AgentId)
            Dim list = (Await _h.Messages.QueryAsync(q).ConfigureAwait(False)).Where(Function(m) MessageVisible(rc, m)).ToList()
            Await rc.OkAsync(list.Select(Function(m) MessageDto(m, includeContent:=rc.QueryString("content") = "1")).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function Topology(rc As RequestContext) As Task
            Dim hours = rc.QueryInt("hours", 24, 1, 24 * 30)
            Dim traffic = (Await _h.Messages.TopologyAsync(DateTime.UtcNow.AddHours(-hours)).ConfigureAwait(False)).
                Where(Function(t) Sees(rc, CStr(t("from"))) AndAlso Sees(rc, CStr(t("to")))).ToList()
            Dim nodes = _h.Agents.GetSnapshots().Where(Function(s) Sees(rc, s.Id)).Select(Function(s) New With {.id = s.Id, .name = s.Name, .role = s.Role, .state = s.State}).ToList()
            Dim permissions = _h.Profiles.AllProfiles().Where(Function(p) Sees(rc, p.Id)).
                SelectMany(Function(p) p.AllowedRecipients.Where(Function(r) Sees(rc, r)).Select(Function(r) New With {.from = p.Id, .[to] = r})).ToList()
            Await rc.OkAsync(New With {.nodes = nodes, .permissions = permissions, .traffic = traffic, .hours = hours}).ConfigureAwait(False)
        End Function

        Private Async Function Correlation(rc As RequestContext) As Task
            Dim id = rc.Route("correlationId")
            Dim tasks = (Await _h.Tasks.ListByCorrelationAsync(id).ConfigureAwait(False)).Where(Function(t) Sees(rc, t.AgentId)).ToList()
            Dim messages = (Await _h.Messages.QueryAsync(New MessageQuery With {.CorrelationId = id, .Limit = 500}).ConfigureAwait(False)).Where(Function(m) MessageVisible(rc, m)).ToList()
            Dim cost = Await _h.Wallets.CostByCorrelationAsync(id).ConfigureAwait(False)
            Dim globalTask = Await _h.GlobalTasks.FindByCorrelationAsync(id).ConfigureAwait(False)
            If tasks.Count = 0 AndAlso messages.Count = 0 AndAlso globalTask Is Nothing Then Throw ApiException.NotFound("CORRELATION_NOT_FOUND", "No tasks or messages with this correlation id.")
            Dim timeline = Await WorkflowTimelineAsync(rc, id, tasks.Select(Function(t) t.Id).ToList()).ConfigureAwait(False)
            Await rc.OkAsync(New With {.correlationId = id, .totalCostMicros = cost, .tasks = tasks.Select(AddressOf TaskDto).ToList(),
                                       .messages = messages.OrderBy(Function(m) m.CreatedUtc).Select(Function(m) MessageDto(m, False)).ToList(),
                                       .timeline = timeline,
                                       .globalTask = If(globalTask Is Nothing, Nothing, New With {.id = globalTask.Id, .name = globalTask.Name, .status = globalTask.Status})}).ConfigureAwait(False)
        End Function

        Private Async Function CancelMessage(rc As RequestContext) As Task
            Dim id = rc.Route("messageId")
            Dim msg = Await _h.Messages.GetAsync(id).ConfigureAwait(False)
            If msg IsNot Nothing AndAlso Not MessageVisible(rc, msg) Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to this agent.")
            Dim ok = Await _h.Comms.CancelMessageAsync(AgentCommunicationService.AdminSender, id, "Cancelled by administrator").ConfigureAwait(False)
            If Not ok Then Throw New ApiException(409, "MESSAGE_NOT_CANCELLABLE", "Message not found or no longer pending.")
            Await AuditAsync(rc, "cancel-message", "message", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Models / AI / MCP
        ' =========================================================================================

        Private Async Function Models(rc As RequestContext) As Task
            Dim usage = Await _h.Telemetry.AiUsageByModelAsync(DateTime.UtcNow.AddDays(-1)).ConfigureAwait(False)
            ' Other accounts' own models (My models) stay private, administrators included.
            Dim status = _h.Gateway.ModelStatus().Where(Function(x) Not ByoService.IsByoId(CStr(x("key"))) OrElse String.Equals(_h.Byo?.OwnerOf(CStr(x("key"))), rc.User.Name, StringComparison.OrdinalIgnoreCase)).ToList()
            Dim grants = _h.ModelAccess.AllGrants()
            For Each m In status
                Dim key = CStr(m("key"))
                m("pricing") = _h.Pricing.GetPrice(key)
                m("usage24h") = usage.FirstOrDefault(Function(u) String.Equals(CStr(u("modelKey")), key, StringComparison.OrdinalIgnoreCase))
                m("grantedTo") = grants.Where(Function(g) String.Equals(g.ModelKey, key, StringComparison.OrdinalIgnoreCase) AndAlso Sees(rc, g.AgentId)).Select(Function(g) g.AgentId).ToList()
                m("profileAgents") = _h.Profiles.AllProfiles().Where(Function(p) Not p.IsSubagent AndAlso p.AllowedModels.Contains(key) AndAlso Sees(rc, p.Id)).Select(Function(p) p.Id).ToList()
            Next
            Await rc.OkAsync(New With {
                .models = status, .imageGeneration = New With {.enabled = _h.Options.ImageGeneration.Enabled, .model = _h.Options.ImageGeneration.Model,
                                                              .pricing = _h.Pricing.GetPrice(PricingService.ImageGenerationKey),
                                                              .usage24h = usage.FirstOrDefault(Function(u) CStr(u("modelKey")) = PricingService.ImageGenerationKey)},
                .vision = New With {.models = _h.Options.Vision.ModelKeys, .expert = _h.Options.Vision.ExpertModelKeys},
                .routing = _h.Options.Routing, .spillovers = _h.Gateway.SpilloverCount, .escalations = _h.Gateway.EscalationCount}).ConfigureAwait(False)
        End Function

        Private Async Function ResetCircuit(rc As RequestContext) As Task
            Dim key = rc.Route("key")
            Dim m = _h.ModelRegistry.TryGet(key)
            If m Is Nothing Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "Model does not exist.")
            m.Breaker.Reset()
            Await AuditAsync(rc, "reset-circuit", "model", key).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>AI providers paused because their account is out of credit: [{provider, models, sinceUtc, retryUtc, lastError, times}].</summary>
        Private Function PausedProviders(rc As RequestContext) As Task
            Return rc.OkAsync(New With {.items = PausedProviderItems()})
        End Function

        Private Function PausedProviderItems() As List(Of Object)
            Return _h.Gateway.PausedProviders().Select(Function(p) CObj(New With {.provider = p.Provider, .models = p.Models, .sinceUtc = p.SinceUtc, .retryUtc = p.RetryUtc,
                                                                                .lastError = p.LastError, .times = p.Times})).ToList()
        End Function

        ''' <summary>
        ''' Context windows learned from providers (what they answered and refused): [{modelId, configured, effective, maxAccepted,
        ''' minRejected, reported, accepted, rejected, lastError, source, updatedUtc}].
        ''' </summary>
        Private Function ContextLimitsList(rc As RequestContext) As Task
            Dim items As New List(Of Object)
            For Each e In If(_h.ContextLimits?.All(), New List(Of AI.ContextLimits.Entry))
                Dim agentModel = _h.ModelRegistry.All().FirstOrDefault(Function(m) String.Equals(m.Options.ModelId, e.ModelId, StringComparison.OrdinalIgnoreCase))
                Dim configured = If(agentModel IsNot Nothing, agentModel.Options.ContextWindowTokens, _h.Chat.ConfiguredWindow(e.ModelId))
                items.Add(New With {.modelId = e.ModelId, .models = _h.ModelRegistry.All().Where(Function(m) String.Equals(m.Options.ModelId, e.ModelId, StringComparison.OrdinalIgnoreCase)).Select(Function(m) m.Key).ToList(),
                                    .configured = configured, .effective = _h.ContextLimits.Effective(e.ModelId, configured), .maxAccepted = e.MaxAccepted, .minRejected = e.MinRejected,
                                    .reported = e.Reported, .accepted = e.Accepted, .rejected = e.Rejected, .lastError = e.LastError, .source = e.Source, .updatedUtc = e.UpdatedUtc})
            Next
            Return rc.OkAsync(New With {.items = items})
        End Function

        ''' <summary>?model=…: forgets what was learned about a model's window (its configured size applies again).</summary>
        Private Async Function ContextLimitForget(rc As RequestContext) As Task
            Dim model = rc.QueryString("model")
            If _h.ContextLimits Is Nothing OrElse Not Await _h.ContextLimits.ForgetAsync(model).ConfigureAwait(False) Then Throw ApiException.NotFound("NOT_LEARNED", "Nothing was learned about that model.")
            Await AuditAsync(rc, "forget-context-limit", "model", model).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Turns a provider paused for being out of credit back on at once (balance was added): {models: [..]}.</summary>
        Private Async Function ResumeProvider(rc As RequestContext) As Task
            Dim provider = rc.Route("provider")
            Dim models = _h.Gateway.ResumeProvider(provider, rc.User.Name)
            If models.Count = 0 Then Throw ApiException.NotFound("PROVIDER_NOT_FOUND", $"No cloud model uses the provider {provider}.")
            Await AuditAsync(rc, "resume-provider", "provider", provider).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True, .models = models, .items = PausedProviderItems()}).ConfigureAwait(False)
        End Function

        Private Function AiStatus(rc As RequestContext) As Task
            Return rc.OkAsync(New With {.provider = _h.LastProviderCheck, .spillovers = _h.Gateway.SpilloverCount, .escalations = _h.Gateway.EscalationCount})
        End Function

        Private Async Function AiCheck(rc As RequestContext) As Task
            _h.LastProviderCheck = Await HostApplication.CheckProviderAsync(_h, rc.CancellationToken).ConfigureAwait(False)
            Await rc.OkAsync(_h.LastProviderCheck).ConfigureAwait(False)
        End Function

        Private Async Function AiCalls(rc As RequestContext) As Task
            RequireAgentAccess(rc, rc.QueryString("agent"))
            Dim list = Await _h.Telemetry.ListAiCallsAsync(rc.QueryString("agent"), rc.QueryInt("limit", 100, 1, 500)).ConfigureAwait(False)
            Await rc.OkAsync(list.Where(Function(c) Sees(rc, c.AgentId)).ToList()).ConfigureAwait(False)
        End Function

        Private Function McpStatus(rc As RequestContext) As Task
            Return rc.OkAsync(_h.Mcp.GetStatus())
        End Function

        Private Async Function McpReconnect(rc As RequestContext) As Task
            Dim id = rc.Route("serverId")
            If _h.Mcp.GetServerOptions(id) Is Nothing Then Throw ApiException.NotFound("MCP_NOT_FOUND", "MCP server does not exist.")
            _h.Mcp.RequestReconnect(id)
            Await AuditAsync(rc, "mcp-reconnect", "mcp", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ' =========================================================================================
        ' Approvals / config / audit / events / artifacts
        ' =========================================================================================

        Private Async Function ListApprovals(rc As RequestContext) As Task
            Dim s = rc.QueryString("status")
            Dim st As ApprovalStatus
            Dim filter As ApprovalStatus? = Nothing
            If s IsNot Nothing Then
                If Not [Enum].TryParse(s, True, st) Then Throw ApiException.BadRequest("Unknown status.")
                filter = st
            End If
            RequireAgentAccess(rc, rc.QueryString("agent"))
            Dim list = Await _h.Approvals.ListAsync(filter, rc.QueryInt("limit", 100, 1, 500), rc.QueryString("agent")).ConfigureAwait(False)
            ' A command a tool would run gets a risk level, so people see at a glance what they are approving.
            For Each a In list.Where(Function(x) x.Kind = ApprovalKind.ToolApproval AndAlso x.Status = ApprovalStatus.Pending)
                Dim cmd = CommandRisk.CommandOf(ApprovalService.ToolOf(a), a.Context)
                If cmd Is Nothing Then Continue For
                Dim risk = CommandRisk.Classify(cmd)
                a.Risk = risk.Level.ToString()
                a.RiskReason = risk.Why
            Next
            Await rc.OkAsync(list.Where(Function(a) Sees(rc, a.AgentId)).ToList()).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' A standing rule from a request's "rule" object: {scope: "agent"|"all", tool?: name or "*", mode: "all"|"safe",
        ''' hours?: number, expiresUtc?: ISO time} (neither hours nor expiresUtc = until revoked).
        ''' </summary>
        Private Shared Function RuleExpiry(el As JsonElement) As DateTime?
            Dim iso = JsonUtil.GetString(el, "expiresUtc")
            If Not String.IsNullOrWhiteSpace(iso) Then
                Dim d As DateTimeOffset
                If Not DateTimeOffset.TryParse(iso, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal, d) Then Throw ApiException.BadRequest("expiresUtc is not a date.")
                Return d.UtcDateTime
            End If
            Dim h As JsonElement
            If el.TryGetProperty("hours", h) AndAlso h.ValueKind = JsonValueKind.Number AndAlso h.GetDouble() > 0 Then
                Return DateTime.UtcNow.AddHours(Math.Min(h.GetDouble(), 24 * 365))
            End If
            Return Nothing
        End Function

        Private Async Function ListApprovalRules(rc As RequestContext) As Task
            Dim now = DateTime.UtcNow
            Dim all = Await _h.Approvals.Rules.ListAsync(rc.QueryInt("limit", 100, 1, 500)).ConfigureAwait(False)
            Await rc.OkAsync(all.Where(Function(r) r.AgentId = "*" OrElse Sees(rc, r.AgentId)).Select(Function(r) New With {
                r.Id, r.AgentId, r.Tool, r.Mode, r.ExpiresUtc, r.Note, r.CreatedBy, r.CreatedUtc, r.RevokedUtc, r.RevokedBy, r.Uses, r.LastUsedUtc,
                .active = r.IsActive(now), .description = r.Describe()}).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function AddApprovalRule(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim agentId = If(JsonUtil.GetString(b, "agentId"), "*").Trim()
            If agentId <> "*" Then RequireAgentAccess(rc, agentId)
            Dim rule As ApprovalRule
            Try
                rule = Await _h.Approvals.Rules.AddAsync(agentId, JsonUtil.GetString(b, "tool"), JsonUtil.GetString(b, "mode", "all"), RuleExpiry(b), JsonUtil.GetString(b, "note"), rc.User.Name).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Dim covered = Await _h.Approvals.ApprovePendingCoveredByAsync(rule, rc.User.Name).ConfigureAwait(False)
            Await AuditAsync(rc, "add-approval-rule", "approval-rule", rule.Id, New With {rule.AgentId, rule.Tool, rule.Mode, rule.ExpiresUtc}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.rule = rule, .alsoApproved = covered}).ConfigureAwait(False)
        End Function

        Private Async Function RevokeApprovalRule(rc As RequestContext) As Task
            Dim id = rc.Route("ruleId")
            Dim rule = Await _h.Approvals.Rules.GetAsync(id).ConfigureAwait(False)
            If rule Is Nothing Then Throw ApiException.NotFound("RULE_NOT_FOUND", "No such rule.")
            If rule.AgentId <> "*" Then RequireAgentAccess(rc, rule.AgentId)
            If Not Await _h.Approvals.Rules.RevokeAsync(id, rc.User.Name).ConfigureAwait(False) Then Throw New ApiException(409, "RULE_NOT_ACTIVE", "The rule was already revoked.")
            Await AuditAsync(rc, "revoke-approval-rule", "approval-rule", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function ResolveApproval(rc As RequestContext) As Task
            Dim id = rc.Route("approvalId")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim st As ApprovalStatus
            If Not [Enum].TryParse(JsonUtil.GetString(b, "status", ""), True, st) OrElse st = ApprovalStatus.Pending OrElse st = ApprovalStatus.Expired Then
                Throw ApiException.BadRequest("status must be Approved, Rejected or Answered.")
            End If
            Dim existing = Await _h.Approvals.GetAsync(id).ConfigureAwait(False)
            If existing IsNot Nothing Then RequireAgentAccess(rc, existing.AgentId)
            ' "Approve and allow for a while": a standing rule for this agent (or every agent) and tool, made before deciding so the
            ' requests it covers that are waiting now are approved with this one.
            Dim ruleEl As JsonElement
            Dim rule As ApprovalRule = Nothing
            If st = ApprovalStatus.Approved AndAlso existing IsNot Nothing AndAlso existing.Kind = ApprovalKind.ToolApproval AndAlso
               b.ValueKind = JsonValueKind.Object AndAlso b.TryGetProperty("rule", ruleEl) AndAlso ruleEl.ValueKind = JsonValueKind.Object Then
                Dim scope = JsonUtil.GetString(ruleEl, "scope", "agent")
                Dim tool = If(JsonUtil.GetString(ruleEl, "tool"), ApprovalService.ToolOf(existing))
                If tool Is Nothing Then Throw ApiException.BadRequest("This request names no tool to make a rule for.")
                Try
                    rule = Await _h.Approvals.Rules.AddAsync(If(scope = "all", "*", existing.AgentId), tool, JsonUtil.GetString(ruleEl, "mode", "all"), RuleExpiry(ruleEl),
                                                             $"From the Inbox (request {existing.Id}).", rc.User.Name).ConfigureAwait(False)
                Catch ex As ArgumentException
                    Throw ApiException.BadRequest(ex.Message)
                End Try
                Await AuditAsync(rc, "add-approval-rule", "approval-rule", rule.Id, New With {rule.AgentId, rule.Tool, rule.Mode, rule.ExpiresUtc}).ConfigureAwait(False)
            End If
            ' What the decision does (a budget credit, a behaviour change, a hire) is applied by the approval service itself.
            Dim rec = Await _h.Approvals.ResolveAsync(id, st, JsonUtil.GetString(b, "response"), rc.User.Name).ConfigureAwait(False)
            If rec Is Nothing Then Throw New ApiException(409, "APPROVAL_NOT_PENDING", "Approval not found or already resolved.")
            Dim alsoApproved = If(rule Is Nothing, 0, Await _h.Approvals.ApprovePendingCoveredByAsync(rule, rc.User.Name).ConfigureAwait(False))
            Await AuditAsync(rc, "resolve-approval", "approval", id, New With {.status = st.ToString()}).ConfigureAwait(False)
            If rule Is Nothing Then
                Await rc.OkAsync(rec).ConfigureAwait(False)
            Else
                Await rc.OkAsync(New With {rec.Id, rec.Status, rec.Response, .rule = rule, .ruleDescription = rule.Describe(), .alsoApproved = alsoApproved}).ConfigureAwait(False)
            End If
        End Function

        Private Function ValidateConfig(rc As RequestContext) As Task
            Dim report = ConfigValidator.Validate(_h.Options)
            Return rc.OkAsync(New With {.valid = report.IsValid, .errors = report.Errors, .warnings = report.Warnings, .configPath = _h.ConfigPath})
        End Function

        Private Function ConfigSummary(rc As RequestContext) As Task
            Dim o = _h.Options
            Return rc.OkAsync(New With {
                .host = o.Host, .server = New With {o.Server.Host, o.Server.Port, o.Server.UseHttps, o.Server.MaxConcurrentRequests},
                .providers = o.AIProviders.Select(Function(kv) New With {.name = kv.Key, .baseUrl = kv.Value.BaseUrl, .apiKeySet = Not String.IsNullOrEmpty(kv.Value.ApiKey),
                                                                         .useStreaming = kv.Value.UseStreaming, .timeoutSeconds = kv.Value.TimeoutSeconds}),
                .models = o.Models, .routing = o.Routing, .vision = o.Vision,
                .imageGeneration = New With {o.ImageGeneration.Enabled, o.ImageGeneration.Model, o.ImageGeneration.DefaultSize, o.ImageGeneration.MaxPerAgentPerDay},
                .economy = o.Economy,
                .mcpServers = o.McpServers.Select(Function(s) New With {s.Id, s.Name, s.Description, s.Enabled, .transport = s.Transport.ToString(), s.Endpoint, s.Command,
                                                                        .authType = s.Auth?.Type, .secretEnvVar = s.Auth?.SecretEnvVar, s.TimeoutSeconds, s.MaxConcurrency}),
                .communication = o.Communication, .limits = o.Limits, .supervision = o.Supervision, .retention = o.Retention,
                .baseProfiles = o.BaseProfiles.Select(Function(b) b.Id), .agentCount = o.Agents.Count})
        End Function

        Private Async Function Audit(rc As RequestContext) As Task
            Await rc.OkAsync(Await _h.AgentsRepo.ListAuditAsync(rc.QueryInt("limit", 200, 1, 1000), rc.QueryString("target")).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function RecentEvents(rc As RequestContext) As Task
            Dim agent = rc.QueryString("agent")
            RequireAgentAccess(rc, agent)
            Dim list = Await _h.Telemetry.ListActivityAsync(agent, rc.QueryInt("limit", 100, 1, 1000), rc.QueryString("correlationId"), rc.QueryString("type"),
                                                            rc.QueryInt("before", 0, 0)).ConfigureAwait(False)
            Await rc.OkAsync(list.Where(Function(e) Sees(rc, e.AgentId)).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function ArtifactContent(rc As RequestContext) As Task
            Try
                Dim a = Await _h.Artifacts.ReadAnyAsync(rc.Route("artifactId")).ConfigureAwait(False)
                If Not Sees(rc, a.Info.OwnerAgentId) Then Throw New ArtifactAccessException("Artifact not found.")
                Dim inline = a.Info.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) OrElse a.Info.MimeType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
                Dim safeName = New String(a.Info.Name.Where(Function(c) Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "-"c OrElse c = "_"c).ToArray())
                Await rc.WriteBytesAsync(200, If(inline, a.Info.MimeType, "application/octet-stream"), a.Data, New Dictionary(Of String, String) From {
                    {"Content-Disposition", $"{If(inline, "inline", "attachment")}; filename=""{If(safeName.Length = 0, "artifact.bin", safeName)}"""},
                    {"Cache-Control", "no-store"}}).ConfigureAwait(False)
            Catch ex As ArtifactAccessException
                Throw ApiException.NotFound("ARTIFACT_NOT_FOUND", ex.Message)
            End Try
        End Function

        ''' <summary>Server-Sent Events live feed (bounded per-client queue; slow clients drop oldest events).</summary>
        Private Async Function EventStream(rc As RequestContext) As Task
            ' Per account: one stream per open window, up to MaxSseClientsPerUser; one more closes that account's oldest stream
            ' (a window that went away without closing it, which otherwise holds its place until a write fails). The server-wide
            ' MaxSseClients is only a safety net, so one account's windows never keep everyone else's out.
            Dim owner = If(rc.User.Name, "")
            Dim mine As New CancellationTokenSource()
            Dim evicted As CancellationTokenSource = Nothing
            SyncLock _sseByUser
                If _sseClients >= Math.Max(1, _h.Options.Server.MaxSseClients) Then
                    mine.Dispose()
                    Throw New ApiException(503, "TOO_MANY_STREAMS", "Too many live event streams are open on this server.")
                End If
                Dim list As List(Of CancellationTokenSource) = Nothing
                If Not _sseByUser.TryGetValue(owner, list) Then
                    list = New List(Of CancellationTokenSource)()
                    _sseByUser(owner) = list
                End If
                If list.Count >= Math.Max(1, _h.Options.Server.MaxSseClientsPerUser) Then
                    evicted = list(0)
                    list.RemoveAt(0)
                    _sseClients -= 1
                End If
                list.Add(mine)
                _sseClients += 1
            End SyncLock
            Try
                evicted?.Cancel()
            Catch ex As ObjectDisposedException
            End Try
            Try
                Await RunEventStreamAsync(rc, mine.Token).ConfigureAwait(False)
            Finally
                SyncLock _sseByUser
                    Dim list As List(Of CancellationTokenSource) = Nothing
                    If _sseByUser.TryGetValue(owner, list) AndAlso list.Remove(mine) Then
                        _sseClients -= 1
                        If list.Count = 0 Then _sseByUser.Remove(owner)
                    End If
                End SyncLock
                mine.Dispose()
            End Try
        End Function

        ''' <summary>One live event stream until the client goes away or a newer window of the same account replaces it.</summary>
        Private Async Function RunEventStreamAsync(rc As RequestContext, replaced As CancellationToken) As Task
            Dim agentFilter = rc.QueryString("agent")
            Dim user = rc.User
            ' Without the Workforce the stream stays open (pages expect it) but carries no events: they are all about agents.
            Dim workforce = user.SeesWorkforce
            Dim subscription = _h.Events.Subscribe("sse:" & rc.RequestId, 1000,
                Function(e) workforce AndAlso (agentFilter Is Nothing OrElse String.Equals(e.AgentId, agentFilter, StringComparison.OrdinalIgnoreCase)) AndAlso
                            (e.AgentId Is Nothing OrElse user.CanAccessAgent(e.AgentId)))
            Try
                Dim resp = rc.Http.Response
                resp.StatusCode = 200
                resp.ContentType = "text/event-stream; charset=utf-8"
                resp.SendChunked = True
                resp.Headers("Cache-Control") = "no-cache"
                resp.Headers("X-Accel-Buffering") = "no"
                rc.MarkResponded()
                Dim output = resp.OutputStream
                Using linked = CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken, replaced)
                Dim ct = linked.Token
                Await WriteSseAsync(output, ": connected" & vbLf & vbLf, ct).ConfigureAwait(False)
                While Not ct.IsCancellationRequested
                    Dim waitTask = subscription.Reader.WaitToReadAsync(ct).AsTask()
                    Dim completed = Await Task.WhenAny(waitTask, Task.Delay(TimeSpan.FromSeconds(15), ct)).ConfigureAwait(False)
                    If completed Is waitTask Then
                        If Not waitTask.Result Then Exit While
                        Dim sb As New StringBuilder()
                        Dim e As AgentEvent = Nothing
                        Dim n = 0
                        While n < 200 AndAlso subscription.Reader.TryRead(e)
                            n += 1
                            sb.Append("id: ").Append(e.Sequence).Append(vbLf)
                            sb.Append("data: ").Append(JsonUtil.ToJson(New With {
                                .seq = e.Sequence, .type = e.EventType.ToString(), .level = e.Level.ToString(), .ts = e.TimestampUtc, .agentId = e.AgentId,
                                .taskId = e.TaskId, .messageId = e.MessageId, .correlationId = e.CorrelationId, .summary = e.Summary, .data = e.Data})).Append(vbLf).Append(vbLf)
                        End While
                        If sb.Length > 0 Then Await WriteSseAsync(output, sb.ToString(), ct).ConfigureAwait(False)
                    Else
                        Await WriteSseAsync(output, ": ping" & vbLf & vbLf, ct).ConfigureAwait(False)
                    End If
                End While
                End Using
            Catch ex As HttpListenerException
                ' client went away
            Catch ex As IO.IOException
            Catch ex As OperationCanceledException
                ' client went away, or a newer window of the same account took this stream's place
            Finally
                _h.Events.Unsubscribe(subscription)
            End Try
        End Function

        Private Shared Async Function WriteSseAsync(output As IO.Stream, text As String, ct As CancellationToken) As Task
            Dim bytes = Encoding.UTF8.GetBytes(text)
            Await output.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(False)
            Await output.FlushAsync(ct).ConfigureAwait(False)
        End Function
    End Class

End Namespace
