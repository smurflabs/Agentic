Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports Agentic.Security

Namespace Configuration

    Public NotInheritable Class ValidationReport
        Public ReadOnly Property Errors As New List(Of String)
        Public ReadOnly Property Warnings As New List(Of String)

        Public ReadOnly Property IsValid As Boolean
            Get
                Return Errors.Count = 0
            End Get
        End Property

        Public Sub AddError(msg As String)
            Errors.Add(msg)
        End Sub

        Public Sub AddWarning(msg As String)
            Warnings.Add(msg)
        End Sub
    End Class

    ''' <summary>
    ''' Validates the complete configuration before anything starts. Any error prevents startup
    ''' (no partial start). Warnings are printed but do not block.
    ''' </summary>
    Public NotInheritable Class ConfigValidator

        Public Shared ReadOnly KnownInternalTools As String() =
            ProfileResolver.DefaultInternalTools.Concat({"analyze_image", "generate_image", "broadcast_notification", "*",
                                                         "run_code", "http_request", "list_secrets", "search_knowledge"}).
                Concat(AutonomyLevels.BaseTools).Concat(AutonomyLevels.SelfDirectionTools).Concat(AutonomyLevels.ManagerTools).Concat(AutonomyLevels.HiringTools).
                Distinct(StringComparer.OrdinalIgnoreCase).ToArray()

        ''' <summary>Checks an agent's raw Autonomy settings (the level name and numbers; who it names is checked on the resolved profile).</summary>
        Public Shared Sub ValidateAutonomyOptions(a As AgentAutonomyOptions, r As ValidationReport, label As String)
            If a Is Nothing Then Return
            If Not String.IsNullOrWhiteSpace(a.Level) AndAlso AutonomyLevels.Normalize(a.Level) Is Nothing Then
                r.AddError($"{label} Autonomy.Level '{a.Level}' is not supervised, assisted or autonomous.")
            End If
            If a.AutoApproveBudgetUsd.HasValue AndAlso a.AutoApproveBudgetUsd.Value < 0 Then r.AddError($"{label} Autonomy.AutoApproveBudgetUsd must be >= 0.")
            If a.AutoApproveBudgetPerDayUsd.HasValue AndAlso a.AutoApproveBudgetPerDayUsd.Value < 0 Then r.AddError($"{label} Autonomy.AutoApproveBudgetPerDayUsd must be >= 0.")
            If a.MaxHires.HasValue AndAlso (a.MaxHires.Value < 0 OrElse a.MaxHires.Value > 20) Then r.AddError($"{label} Autonomy.MaxHires must be 0-20.")
            If a.VerifyPasses.HasValue AndAlso (a.VerifyPasses.Value < 0 OrElse a.VerifyPasses.Value > 5) Then r.AddError($"{label} Autonomy.VerifyPasses must be 0-5.")
            If a.InitiativeMinutes.HasValue AndAlso a.InitiativeMinutes.Value <> 0 AndAlso a.InitiativeMinutes.Value < 10 Then r.AddError($"{label} Autonomy.InitiativeMinutes must be 0 (off) or at least 10.")
            If a.AskHumanTimeoutMinutes.HasValue AndAlso a.AskHumanTimeoutMinutes.Value < 0 Then r.AddError($"{label} Autonomy.AskHumanTimeoutMinutes must be >= 0.")
            For Each t In If(a.DeniedTools, New List(Of String))
                If Not KnownInternalTools.Contains(t, StringComparer.OrdinalIgnoreCase) Then r.AddWarning($"{label} Autonomy.DeniedTools lists unknown tool '{t}'.")
            Next
            For Each h In If(a.AllowedHttpHosts, New List(Of String))
                If h Is Nothing OrElse h.Contains("/") OrElse h.Contains(" ") Then r.AddError($"{label} Autonomy.AllowedHttpHosts entry '{h}' must be a host name (example.com, *.example.com or *).")
            Next
        End Sub

        Public Shared ReadOnly KnownHandlers As String() = {"reasoning", "mcp-tool", "http-check", "noop"}

        Public Shared Function Validate(o As HostOptions, Optional requireApiKey As Boolean = True) As ValidationReport
            Dim r As New ValidationReport()
            ValidateServer(o, r)
            ValidateProviders(o, r, requireApiKey)
            Dim modelKeys = ValidateModels(o, r)
            ValidateRouting(o, r, modelKeys)
            Dim mcpIds = ValidateMcp(o, r)
            ValidateAgents(o, r, modelKeys, mcpIds)
            ValidateLimits(o, r)
            Return r
        End Function

        Private Shared Sub ValidateServer(o As HostOptions, r As ValidationReport)
            If o.Server.Enabled Then
                If o.Server.Port < 1 OrElse o.Server.Port > 65535 Then r.AddError($"Server.Port {o.Server.Port} is out of range.")
                Dim h = o.Server.Host
                If String.IsNullOrWhiteSpace(h) Then
                    r.AddError("Server.Host is required.")
                ElseIf h <> "+" AndAlso h <> "*" AndAlso Not h.Equals("localhost", StringComparison.OrdinalIgnoreCase) Then
                    Dim ip As IPAddress = Nothing
                    If Not IPAddress.TryParse(h, ip) AndAlso Uri.CheckHostName(h) = UriHostNameType.Unknown Then
                        r.AddError($"Server.Host '{h}' is not a valid address or host name.")
                    End If
                End If
                If h = "+" OrElse h = "*" OrElse h = "0.0.0.0" Then
                    r.AddWarning("Server.Host binds to all interfaces. Only expose the admin UI on a private LAN/VPN or behind an HTTPS reverse proxy.")
                End If
                ' Security: ExposeErrorDetails leaks internal state (file paths, SQL fragments, stack traces)
                If o.Server.ExposeErrorDetails AndAlso (h = "+" OrElse h = "*" OrElse h = "0.0.0.0") Then
                    r.AddWarning("Server.ExposeErrorDetails is True with a non-loopback bind. This leaks exception details to remote callers. Set it to False for production deployments.")
                End If
                For Each u In o.Admin.Users
                    If String.IsNullOrWhiteSpace(u.Username) Then r.AddError("Admin.Users entry has no Username.")
                    If Not PasswordHasher.IsHash(u.PasswordHash) Then r.AddError($"Admin user '{u.Username}' PasswordHash is not a PBKDF2 hash. Generate one with: Agentic.exe --hash-password")
                Next
                For Each t In o.Admin.ApiTokens
                    If String.IsNullOrWhiteSpace(t.Name) Then r.AddError("Admin.ApiTokens entry has no Name.")
                    If String.IsNullOrWhiteSpace(t.TokenSha256) AndAlso String.IsNullOrWhiteSpace(t.TokenEnvVar) Then r.AddError($"API token '{t.Name}' needs TokenSha256 or TokenEnvVar.")
                    For Each s In t.Scopes
                        If s <> "admin" AndAlso s <> "read" AndAlso s <> "submit-task" Then r.AddError($"API token '{t.Name}' has unknown scope '{s}'.")
                    Next
                Next
            End If
        End Sub

        Private Shared Sub ValidateProviders(o As HostOptions, r As ValidationReport, requireApiKey As Boolean)
            If o.AIProviders.Count = 0 Then r.AddError("AIProviders must define at least one provider (e.g. OpenWebUI).")
            For Each kv In o.AIProviders
                Dim p = kv.Value
                Dim u As Uri = Nothing
                If String.IsNullOrWhiteSpace(p.BaseUrl) OrElse Not Uri.TryCreate(p.BaseUrl, UriKind.Absolute, u) OrElse (u.Scheme <> "http" AndAlso u.Scheme <> "https") Then
                    r.AddError($"AIProviders.{kv.Key}.BaseUrl must be an absolute http(s) URL.")
                End If
                If requireApiKey AndAlso String.IsNullOrWhiteSpace(p.ApiKey) Then
                    r.AddError($"AIProviders.{kv.Key}.ApiKey is not set. Set environment variable {ConfigLoader.EnvPrefix}AIProviders__{kv.Key}__ApiKey.")
                End If
                If p.TimeoutSeconds < 5 Then r.AddError($"AIProviders.{kv.Key}.TimeoutSeconds must be >= 5.")
            Next
        End Sub

        Private Shared Function ValidateModels(o As HostOptions, r As ValidationReport) As HashSet(Of String)
            Dim keys As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            If o.Models.Count = 0 Then r.AddError("Models must define at least one model.")
            For Each m In o.Models
                If Not InputValidation.IsValidId(m.Key) Then
                    r.AddError($"Model key '{m.Key}' is invalid (1-64 chars: letters, digits, _ - .).")
                    Continue For
                End If
                If Not keys.Add(m.Key) Then r.AddError($"Duplicate model key '{m.Key}'.")
                If String.IsNullOrWhiteSpace(m.ModelId) Then r.AddError($"Model '{m.Key}' has no ModelId.")
                If Not o.AIProviders.ContainsKey(If(m.Provider, String.Empty)) Then r.AddError($"Model '{m.Key}' references unknown provider '{m.Provider}'.")
                If m.MaxTokens < 16 Then r.AddError($"Model '{m.Key}' MaxTokens must be >= 16.")
                If m.MaxConcurrency < 1 Then r.AddError($"Model '{m.Key}' MaxConcurrency must be >= 1.")
                If m.Tier < 1 OrElse m.Tier > 9 Then r.AddError($"Model '{m.Key}' Tier must be 1-9.")
                If m.Temperature.HasValue AndAlso (m.Temperature.Value < 0 OrElse m.Temperature.Value > 2) Then r.AddError($"Model '{m.Key}' Temperature must be 0-2.")
                If m.ContextWindowTokens < 1024 Then r.AddError($"Model '{m.Key}' ContextWindowTokens must be >= 1024.")
                If m.AllowedPurposes.Contains(AiPurpose.Vision) AndAlso Not m.Capabilities.Contains(ModelCapability.Vision) Then
                    r.AddError($"Model '{m.Key}' allows purpose Vision but does not declare the Vision capability.")
                End If
                If m.AllowedPurposes.Count = 0 Then r.AddWarning($"Model '{m.Key}' has no AllowedPurposes; it will only be used where referenced explicitly.")
                If Not m.IsLocal AndAlso m.Enabled Then
                    Dim priced = m.Pricing IsNot Nothing AndAlso m.Pricing.InputPerMillion.HasValue AndAlso m.Pricing.OutputPerMillion.HasValue
                    If Not priced Then
                        If o.Economy.Enabled AndAlso o.Economy.RequirePricingForCloudModels Then
                            r.AddWarning($"Cloud model '{m.Key}' has no Pricing (InputPerMillion/OutputPerMillion). It stays unusable until an admin sets prices (config or dashboard → Finance → Pricing).")
                        End If
                    ElseIf m.Pricing.InputPerMillion.Value < 0 OrElse m.Pricing.OutputPerMillion.Value < 0 Then
                        r.AddError($"Model '{m.Key}' has negative pricing.")
                    End If
                End If
            Next
            For Each m In o.Models
                For Each f In m.Fallbacks
                    If Not keys.Contains(f) Then r.AddError($"Model '{m.Key}' fallback '{f}' is not a configured model.")
                    If String.Equals(f, m.Key, StringComparison.OrdinalIgnoreCase) Then r.AddError($"Model '{m.Key}' lists itself as a fallback.")
                Next
            Next
            Dim lm = o.LocalModels
            If lm IsNot Nothing Then
                If lm.Slots < 0 Then r.AddError("LocalModels.Slots must be 0 (each model's own MaxConcurrency) or more.")
                For Each kv In If(lm.SlotsByModel, New Dictionary(Of String, Integer))
                    If kv.Value < 1 Then r.AddError($"LocalModels.SlotsByModel.{kv.Key} must be 1 or more.")
                    If lm.Enabled AndAlso Not o.Models.Any(Function(x) x.IsLocal AndAlso String.Equals(x.Key, kv.Key, StringComparison.OrdinalIgnoreCase)) Then
                        r.AddWarning($"LocalModels.SlotsByModel names '{kv.Key}', which is not a local model.")
                    End If
                Next
            End If
            Return keys
        End Function

        Private Shared Sub ValidateRouting(o As HostOptions, r As ValidationReport, modelKeys As HashSet(Of String))
            For Each kv In o.Routing.Ladders
                If Not kv.Key.Equals("Default", StringComparison.OrdinalIgnoreCase) Then
                    Dim p As AiPurpose
                    If Not [Enum].TryParse(kv.Key, True, p) Then r.AddError($"Routing.Ladders has unknown purpose '{kv.Key}'.")
                End If
                If kv.Value Is Nothing OrElse kv.Value.Count = 0 Then r.AddError($"Routing.Ladders.{kv.Key} is empty.")
                For Each k In If(kv.Value, New List(Of String))
                    If Not modelKeys.Contains(k) Then r.AddError($"Routing.Ladders.{kv.Key} references unknown model '{k}'.")
                Next
            Next
            If Not o.Routing.Ladders.ContainsKey("Default") Then r.AddWarning("Routing.Ladders has no 'Default' ladder; purposes without a ladder will use any allowed model by priority.")
            Dim sp = o.Routing.Spillover
            If sp.Enabled Then
                If Not modelKeys.Contains(If(sp.FromModel, String.Empty)) Then r.AddError($"Routing.Spillover.FromModel '{sp.FromModel}' is not a configured model.")
                For Each c In sp.Candidates
                    If Not modelKeys.Contains(c) Then r.AddError($"Routing.Spillover candidate '{c}' is not a configured model.")
                Next
                For Each c In sp.LocalCandidates
                    Dim lm = o.Models.FirstOrDefault(Function(x) String.Equals(x.Key, c, StringComparison.OrdinalIgnoreCase))
                    If lm Is Nothing Then
                        r.AddError($"Routing.Spillover local candidate '{c}' is not a configured model.")
                    ElseIf Not lm.IsLocal Then
                        r.AddError($"Routing.Spillover local candidate '{c}' is not a local model (cloud ones go in Candidates).")
                    End If
                Next
                If sp.LocalKeepFreeSlots < 0 Then r.AddError("Routing.Spillover.LocalKeepFreeSlots must be 0 or more.")
            End If
            For Each k In o.Vision.ModelKeys.Concat(o.Vision.ExpertModelKeys)
                If Not modelKeys.Contains(k) Then
                    r.AddError($"Vision model '{k}' is not a configured model.")
                Else
                    Dim m = o.Models.First(Function(x) String.Equals(x.Key, k, StringComparison.OrdinalIgnoreCase))
                    If Not m.Capabilities.Contains(ModelCapability.Vision) Then r.AddError($"Vision model '{k}' does not declare the Vision capability.")
                End If
            Next
            ' Pictures and videos come only from their own APIs (never from the chat provider).
            For Each media In {("ImageGeneration", o.ImageGeneration.Enabled, o.ImageGeneration.BaseUrl, o.ImageGeneration.ApiKey),
                               ("VideoGeneration", o.VideoGeneration.Enabled, o.VideoGeneration.BaseUrl, o.VideoGeneration.ApiKey)}
                If Not media.Item2 Then Continue For
                If String.IsNullOrWhiteSpace(media.Item3) Then
                    r.AddWarning($"{media.Item1}.BaseUrl is not set: {If(media.Item1 = "ImageGeneration", "image", "video")} generation is off.")
                ElseIf Not Uri.IsWellFormedUriString(media.Item3, UriKind.Absolute) Then
                    r.AddError($"{media.Item1}.BaseUrl '{media.Item3}' is not a URL.")
                ElseIf String.IsNullOrWhiteSpace(media.Item4) Then
                    r.AddWarning($"{media.Item1}.ApiKey is not set (AGENTIC_{media.Item1}__ApiKey): {If(media.Item1 = "ImageGeneration", "image", "video")} generation is off.")
                End If
            Next
            If o.VideoGeneration.MaxSeconds < 1 OrElse o.VideoGeneration.DefaultSeconds < 1 Then r.AddError("VideoGeneration.DefaultSeconds and MaxSeconds must be at least 1.")
        End Sub

        Private Shared Function ValidateMcp(o As HostOptions, r As ValidationReport) As HashSet(Of String)
            Dim ids As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each s In o.McpServers
                If Not InputValidation.IsValidId(s.Id) Then
                    r.AddError($"MCP server id '{s.Id}' is invalid.")
                    Continue For
                End If
                If Not ids.Add(s.Id) Then r.AddError($"Duplicate MCP server id '{s.Id}'.")
                If s.Transport = McpTransportKind.Stdio Then
                    If String.IsNullOrWhiteSpace(s.Command) Then r.AddError($"MCP server '{s.Id}' (Stdio) needs Command.")
                Else
                    Dim u As Uri = Nothing
                    If String.IsNullOrWhiteSpace(s.Endpoint) OrElse Not Uri.TryCreate(s.Endpoint, UriKind.Absolute, u) Then r.AddError($"MCP server '{s.Id}' (Http) needs an absolute Endpoint URL.")
                    Dim modeOk = {"AutoDetect", "StreamableHttp", "Sse"}.Contains(If(s.HttpMode, String.Empty), StringComparer.OrdinalIgnoreCase)
                    If Not modeOk Then r.AddError($"MCP server '{s.Id}' HttpMode must be AutoDetect, StreamableHttp or Sse.")
                End If
                Dim authType = If(s.Auth?.Type, "None")
                If Not {"None", "Bearer", "Header"}.Contains(authType, StringComparer.OrdinalIgnoreCase) Then r.AddError($"MCP server '{s.Id}' Auth.Type must be None, Bearer or Header.")
                Dim needsSecret = Not authType.Equals("None", StringComparison.OrdinalIgnoreCase)
                If needsSecret AndAlso String.IsNullOrWhiteSpace(s.Auth?.SecretEnvVar) AndAlso String.IsNullOrWhiteSpace(s.Auth?.Secret) Then
                    r.AddError($"MCP server '{s.Id}' Auth requires Secret or SecretEnvVar.")
                End If
                If s.Enabled AndAlso needsSecret AndAlso String.IsNullOrWhiteSpace(s.Auth?.Secret) AndAlso Not String.IsNullOrWhiteSpace(s.Auth?.SecretEnvVar) AndAlso
                   String.IsNullOrEmpty(Environment.GetEnvironmentVariable(s.Auth.SecretEnvVar)) Then
                    r.AddWarning($"MCP server '{s.Id}': environment variable {s.Auth.SecretEnvVar} is not set and no Secret is configured.")
                End If
                If s.TimeoutSeconds < 1 Then r.AddError($"MCP server '{s.Id}' TimeoutSeconds must be >= 1.")
            Next
            ValidateWebSearch(o.WebSearch, r)
            If o.GlobalTasks IsNot Nothing AndAlso o.GlobalTasks.Enabled Then
                For Each k In o.GlobalTasks.PlannerModelKeys
                    Dim m = o.Models.FirstOrDefault(Function(x) String.Equals(x.Key, k, StringComparison.OrdinalIgnoreCase))
                    If m Is Nothing Then
                        r.AddError($"GlobalTasks.PlannerModelKeys references unknown model '{k}'.")
                    ElseIf Not m.IsLocal Then
                        r.AddError($"Global task planner model '{k}' must be a local model (the coordinator has no budget for cloud models).")
                    End If
                Next
                If o.GlobalTasks.MaxSteps < 1 OrElse o.GlobalTasks.MaxSteps > 20 Then r.AddError("GlobalTasks.MaxSteps must be 1-20.")
            End If
            Return ids
        End Function

        Private Shared Sub ValidateWebSearch(w As WebSearchOptions, r As ValidationReport)
            If w Is Nothing OrElse Not w.Enabled Then Return
            ' Provider is from older configurations; the services and their order are in Order (empty: brave, bing, searxng).
            Dim configured = w.Order IsNot Nothing AndAlso w.Order.Any(Function(x) Not String.IsNullOrWhiteSpace(x))
            Dim order = If(configured, w.Order, WebSearchOptions.DefaultOrder.ToList()).Select(Function(x) If(x, "").Trim().ToLowerInvariant()).Where(Function(x) x.Length > 0).ToList()
            For Each name In order.Where(Function(x) Not WebSearchOptions.Services.Contains(x))
                r.AddError($"WebSearch.Order: unknown service '{name}' (use brave, bing or searxng).")
            Next
            Dim hasKey = Not String.IsNullOrWhiteSpace(w.BraveApiKey)
            Dim hasSearx = Not String.IsNullOrWhiteSpace(w.UrlTemplate) OrElse If(w.Endpoints, New List(Of String)).Any(Function(x) Not String.IsNullOrWhiteSpace(x))
            If configured AndAlso order.Contains("brave") AndAlso Not hasKey Then r.AddWarning("WebSearch.Order lists brave but WebSearch.BraveApiKey is empty: Brave is skipped.")
            If configured AndAlso order.Contains("searxng") AndAlso Not hasSearx Then r.AddWarning("WebSearch.Order lists searxng but WebSearch.UrlTemplate is empty: SearXNG is skipped.")
            If Not order.Any(Function(x) x = "bing" OrElse (x = "brave" AndAlso hasKey) OrElse (x = "searxng" AndAlso hasSearx)) Then
                r.AddError("WebSearch has no usable service: add bing to WebSearch.Order, a BraveApiKey, or a SearXNG UrlTemplate.")
            End If
            Dim template = If(w.UrlTemplate, "")
            Dim u As Uri = Nothing
            If template.Length = 0 Then
                ' No SearXNG: nothing more to check.
            ElseIf Not (template.Contains("{query}") OrElse template.Contains("<query>")) Then
                r.AddError("WebSearch.UrlTemplate must contain {query}, e.g. http://host:8080/search?q={query}&format=json.")
            ElseIf Not Uri.TryCreate(template.Replace("{query}", "x").Replace("<query>", "x"), UriKind.Absolute, u) OrElse (u.Scheme <> Uri.UriSchemeHttp AndAlso u.Scheme <> Uri.UriSchemeHttps) Then
                r.AddError("WebSearch.UrlTemplate must be an absolute http(s) URL.")
            ElseIf Not template.Contains("format=json", StringComparison.OrdinalIgnoreCase) Then
                r.AddWarning("WebSearch.UrlTemplate has no format=json; SearXNG returns HTML unless JSON is requested (the host adds it).")
            End If
            If w.MaxResults < 1 OrElse w.MaxResults > 20 Then r.AddError("WebSearch.MaxResults must be 1-20.")
            If w.TimeoutSeconds < 1 Then r.AddError("WebSearch.TimeoutSeconds must be >= 1.")
            If w.MaxConcurrency < 1 Then r.AddError("WebSearch.MaxConcurrency must be >= 1.")
        End Sub

        Private Shared Sub ValidateAgents(o As HostOptions, r As ValidationReport, modelKeys As HashSet(Of String), mcpIds As HashSet(Of String))
            Dim baseIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each b In o.BaseProfiles
                If Not InputValidation.IsValidId(b.Id) Then r.AddError($"BaseProfile id '{b.Id}' is invalid.") : Continue For
                If Not baseIds.Add(b.Id) Then r.AddError($"Duplicate BaseProfile id '{b.Id}'.")
            Next

            If o.Agents.Count > o.Host.MaxAgents Then r.AddError($"{o.Agents.Count} agents configured; the maximum is {o.Host.MaxAgents}.")
            Dim agentIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each a In o.Agents
                If Not InputValidation.IsValidId(a.Id) Then r.AddError($"Agent id '{a.Id}' is invalid (1-64 chars: letters, digits, _ - .).") : Continue For
                If a.Id.Equals("admin", StringComparison.OrdinalIgnoreCase) OrElse a.Id.Equals("host", StringComparison.OrdinalIgnoreCase) Then r.AddError($"Agent id '{a.Id}' is reserved.")
                If Not agentIds.Add(a.Id) Then r.AddError($"Duplicate agent id '{a.Id}'.")
            Next

            Dim resolver As New ProfileResolver(o.BaseProfiles, o.Communication, o.Subagents, o.Autonomy)
            For Each a In o.Agents
                If a.Id Is Nothing Then Continue For
                If a.BaseProfileId IsNot Nothing AndAlso Not baseIds.Contains(a.BaseProfileId) Then r.AddError($"Agent '{a.Id}' references unknown BaseProfile '{a.BaseProfileId}'.")
                ValidateAutonomyOptions(a.Autonomy, r, $"Agent '{a.Id}'")
                Dim p = resolver.Resolve(a)
                ValidateProfile(o, r, p, modelKeys, mcpIds, agentIds, $"Agent '{a.Id}'")
            Next
            For Each g In o.Communication.Groups
                For Each member In g.Value
                    If Not agentIds.Contains(member) Then r.AddError($"Communication group '{g.Key}' references unknown agent '{member}'.")
                Next
            Next
        End Sub

        ''' <summary>Validates one resolved profile. Also used when admins create/edit agents at runtime.</summary>
        Public Shared Sub ValidateProfile(o As HostOptions, r As ValidationReport, p As EffectiveProfile, modelKeys As ICollection(Of String),
                                          mcpIds As ICollection(Of String), agentIds As ICollection(Of String), label As String)
            If p.AllowedModels.Count = 0 Then r.AddWarning($"{label} has no AllowedModels; it can only run deterministic tasks.")
            For Each m In p.AllowedModels
                If Not modelKeys.Contains(m) Then r.AddError($"{label} allows unknown model '{m}'.")
            Next
            Dim enabledAllowed = o.Models.Where(Function(m) m.Enabled AndAlso p.AllowedModels.Contains(m.Key) AndAlso m.Tier <= p.MaxTier).ToList()
            If p.AllowedModels.Count > 0 AndAlso enabledAllowed.Count = 0 Then
                r.AddError($"{label}: impossible model policy - no allowed model is enabled at or below MaxTier {p.MaxTier}.")
            End If
            If p.AllowedModels.Count > 0 AndAlso Not enabledAllowed.Any(Function(m) m.Capabilities.Contains(ModelCapability.ToolCalling)) Then
                r.AddWarning($"{label}: none of its models declares ToolCalling; reasoning tasks cannot use tools.")
            End If
            For Each s In p.AllowedMcpServers
                If s <> "*" AndAlso Not mcpIds.Contains(s) Then r.AddError($"{label} allows unknown MCP server '{s}'.")
            Next
            For Each t In p.AllowedMcpTools.Concat(p.DeniedMcpTools)
                Dim server = t.Split(":"c)(0)
                If server <> "*" AndAlso Not mcpIds.Contains(server) Then r.AddError($"{label} MCP tool rule '{t}' references unknown server '{server}' (format server:tool).")
            Next
            For Each t In p.InternalTools
                If Not KnownInternalTools.Contains(t, StringComparer.OrdinalIgnoreCase) Then r.AddError($"{label} lists unknown internal tool '{t}'.")
            Next
            For Each rcp In p.AllowedRecipients
                If rcp = "*" Then
                    r.AddWarning($"{label} may message any agent ('*').")
                ElseIf Not agentIds.Contains(rcp) Then
                    r.AddError($"{label} lists unknown recipient '{rcp}'.")
                ElseIf rcp.Equals(p.Id, StringComparison.OrdinalIgnoreCase) Then
                    r.AddError($"{label} lists itself as a recipient.")
                End If
            Next
            For Each g In p.Groups
                If Not o.Communication.Groups.ContainsKey(g) Then r.AddError($"{label} belongs to unknown group '{g}'.")
            Next
            If p.MaxReasoningTurns < 1 OrElse p.MaxReasoningTurns > 200 Then r.AddError($"{label} MaxReasoningTurns must be 1-200.")
            If p.MaxTotalTurns > 0 AndAlso p.MaxTotalTurns < p.MaxReasoningTurns Then r.AddError($"{label} MaxTotalTurns must be 0 (no fixed total) or at least MaxReasoningTurns.")
            If p.MaxTaskDurationSeconds < 10 Then r.AddError($"{label} MaxTaskDurationSeconds must be >= 10.")
            If p.DailySpendLimit.HasValue AndAlso p.DailySpendLimit.Value < 0 Then r.AddError($"{label} DailySpendLimit must be >= 0.")
            If p.MaxSubagents > 20 Then r.AddError($"{label} Subagents.MaxConcurrent must be 0-20.")
            If p.SubagentDefaultSpendCap.HasValue AndAlso p.SubagentDefaultSpendCap.Value < 0 Then r.AddError($"{label} Subagents.DefaultSpendCap must be >= 0.")
            If p.Id IsNot Nothing AndAlso p.Id.Contains(".sub-", StringComparison.OrdinalIgnoreCase) Then r.AddError($"{label}: ids containing '.sub-' are reserved for subagents.")
            ' Autonomy: whom it answers to and whom it manages must exist.
            If p.SupervisorAgentId IsNot Nothing AndAlso Not agentIds.Contains(p.SupervisorAgentId) Then
                r.AddError($"{label} Autonomy.SupervisorAgentId '{p.SupervisorAgentId}' is not an agent.")
            End If
            For Each m In p.Manages
                If m = "*" Then
                    r.AddWarning($"{label} manages every agent (Autonomy.Manages '*').")
                ElseIf Not agentIds.Contains(m) Then
                    r.AddError($"{label} Autonomy.Manages lists unknown agent '{m}'.")
                End If
            Next
            If p.AllowedHttpHosts.Contains("*") Then r.AddWarning($"{label} may call any public web address (Autonomy.AllowedHttpHosts '*').")
            If p.IsAutonomous AndAlso p.SupervisorAgentId Is Nothing Then
                r.AddWarning($"{label} is autonomous without a supervisor agent: what its policy does not decide waits for a person.")
            End If
            Dim schedIds As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each s In p.Schedules
                Dim sl = $"{label} schedule '{If(s.Id, s.Name)}'"
                If String.IsNullOrWhiteSpace(s.Id) Then r.AddError($"{label} has a schedule without Id.") : Continue For
                If Not schedIds.Add(s.Id) Then r.AddError($"{label} has duplicate schedule id '{s.Id}'.")
                If Not KnownHandlers.Contains(If(s.Handler, String.Empty), StringComparer.OrdinalIgnoreCase) Then r.AddError($"{sl} has unknown Handler '{s.Handler}'.")
                If Not s.IntervalSeconds.HasValue AndAlso String.IsNullOrWhiteSpace(s.Cron) Then r.AddError($"{sl} needs IntervalSeconds or Cron.")
                If s.IntervalSeconds.HasValue AndAlso s.IntervalSeconds.Value < 5 Then r.AddError($"{sl} IntervalSeconds must be >= 5.")
                If Not String.IsNullOrWhiteSpace(s.Cron) Then
                    Try
                        Dim fields = s.Cron.Trim().Split(" "c, StringSplitOptions.RemoveEmptyEntries).Length
                        Cronos.CronExpression.Parse(s.Cron, If(fields = 6, Cronos.CronFormat.IncludeSeconds, Cronos.CronFormat.Standard))
                    Catch ex As Exception
                        r.AddError($"{sl} has invalid Cron '{s.Cron}': {ex.Message}")
                    End Try
                End If
                If String.Equals(s.Handler, "mcp-tool", StringComparison.OrdinalIgnoreCase) Then
                    Dim server As String = Nothing
                    If Not s.Parameters.TryGetValue("Server", server) OrElse Not mcpIds.Contains(server) Then r.AddError($"{sl} (mcp-tool) needs Parameters.Server referencing a configured MCP server.")
                    If Not s.Parameters.ContainsKey("Tool") Then r.AddError($"{sl} (mcp-tool) needs Parameters.Tool.")
                End If
                If String.Equals(s.Handler, "http-check", StringComparison.OrdinalIgnoreCase) Then
                    Dim url As String = Nothing
                    Dim u As Uri = Nothing
                    If Not s.Parameters.TryGetValue("Url", url) OrElse Not Uri.TryCreate(url, UriKind.Absolute, u) Then r.AddError($"{sl} (http-check) needs Parameters.Url.")
                End If
            Next
        End Sub

        Private Shared Sub ValidateLimits(o As HostOptions, r As ValidationReport)
            Dim c = o.Communication
            If c.MaxDelegationDepth < 1 OrElse c.MaxDelegationDepth > 20 Then r.AddError("Communication.MaxDelegationDepth must be 1-20.")
            If c.MaxCommunicationChainLength < 2 Then r.AddError("Communication.MaxCommunicationChainLength must be >= 2.")
            If c.MaxMessageCharacters < 100 Then r.AddError("Communication.MaxMessageCharacters must be >= 100.")
            If o.Limits.MaxConcurrentAIRequests < 1 Then r.AddError("Limits.MaxConcurrentAIRequests must be >= 1.")
            If o.Limits.MaxDatabaseOperations < 1 Then r.AddError("Limits.MaxDatabaseOperations must be >= 1.")
            If o.Supervision.MaxRestartAttempts < 0 Then r.AddError("Supervision.MaxRestartAttempts must be >= 0.")
            If o.Economy.GlobalDailyCloudLimit.HasValue AndAlso o.Economy.GlobalDailyCloudLimit.Value < 0 Then r.AddError("Economy.GlobalDailyCloudLimit must be >= 0.")
            Dim s = o.Subagents
            If s.MaxPerAgent < 0 OrElse s.MaxPerAgent > 20 Then r.AddError("Subagents.MaxPerAgent must be 0-20.")
            If s.MaxTotal < 0 OrElse s.MaxTotal > 200 Then r.AddError("Subagents.MaxTotal must be 0-200.")
            If s.MaxPerTask < 1 Then r.AddError("Subagents.MaxPerTask must be >= 1.")
            If s.DefaultTimeoutMinutes < 1 OrElse s.MaxTimeoutMinutes < s.DefaultTimeoutMinutes Then r.AddError("Subagents: DefaultTimeoutMinutes must be >= 1 and <= MaxTimeoutMinutes.")
            If s.DefaultSpendCap < 0 OrElse (s.MaxSpendCap.HasValue AndAlso s.MaxSpendCap.Value < s.DefaultSpendCap) Then r.AddError("Subagents: DefaultSpendCap must be >= 0 and <= MaxSpendCap.")
            Dim l = o.Learning
            If l.Enabled Then
                If l.ModelKeys.Count = 0 Then r.AddWarning("Learning.ModelKeys is empty; studies will use the rule-based analysis only.")
                For Each k In l.ModelKeys
                    Dim m = o.Models.FirstOrDefault(Function(x) String.Equals(x.Key, k, StringComparison.OrdinalIgnoreCase))
                    If m Is Nothing Then
                        r.AddError($"Learning.ModelKeys references unknown model '{k}'.")
                    ElseIf Not m.IsLocal Then
                        r.AddError($"Learning model '{k}' must be a local model (studies have no budget for cloud models).")
                    End If
                Next
                If l.BrokeThreshold < 0 OrElse l.MinProfit < 0 OrElse l.LargeProfit < 0 OrElse l.MinRoi < 0 Then r.AddError("Learning thresholds must be >= 0.")
                If l.MaxLessonsPerStudy < 1 OrElse l.MaxLessonsPerStudy > 20 Then r.AddError("Learning.MaxLessonsPerStudy must be 1-20.")
                If l.PromptLessons < 0 OrElse l.PromptLessons > 20 Then r.AddError("Learning.PromptLessons must be 0-20.")
            End If
        End Sub

    End Class

End Namespace
