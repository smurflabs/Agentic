Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Threading
Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Http
Imports Agentic.Logging
Imports Agentic.Mcp
Imports Agentic.Memory
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work

''' <summary>
''' Composition root and lifecycle owner. Builds every service explicitly (no DI container), performs startup recovery,
''' runs maintenance loops and implements graceful shutdown in the documented order.
''' </summary>
Public NotInheritable Class HostApplication
    Implements IDisposable

    ''' <summary>The server's version: &lt;Version&gt; in Agentic.vbproj, which tools\publish-server.ps1 raises with every publish.</summary>
    Public Shared ReadOnly Version As String = ReadVersion()

    Private Shared Function ReadVersion() As String
        Dim v = GetType(HostApplication).Assembly.GetName().Version
        Return If(v Is Nothing, "0.0.0", $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}")
    End Function

    ''' <summary>
    ''' Agents pay per model key (Models in the config); a key's price follows the Model Square price of the model it
    ''' calls. Pointing a key at another model in the config therefore never leaves it billed at its old model's price.
    ''' </summary>
    Private Async Function SyncCatalogPricesAsync(h As HostServices, o As HostOptions) As Task
        Dim square = h.Accounts.Prices()
        For Each m In o.Models.Where(Function(x) Not x.IsLocal AndAlso Not String.IsNullOrWhiteSpace(x.ModelId))
            Dim sp As SquarePrice = Nothing
            If Not square.TryGetValue(m.ModelId, sp) OrElse (sp.InputPerMillion = 0D AndAlso sp.OutputPerMillion = 0D) Then Continue For
            Dim cur = h.Pricing.GetPrice(m.Key)
            If cur.Source = "none" OrElse (cur.InputPerMillion.HasValue AndAlso cur.InputPerMillion.Value = sp.InputPerMillion AndAlso
                                           cur.OutputPerMillion.HasValue AndAlso cur.OutputPerMillion.Value = sp.OutputPerMillion) Then Continue For
            Await h.Pricing.SetPriceAsync(m.Key, sp.InputPerMillion, sp.OutputPerMillion, cur.PerImage, "model-square").ConfigureAwait(False)
            _log.Info("Pricing", $"{m.Key} is billed at the Model Square price of {m.ModelId}: ${sp.InputPerMillion} in / ${sp.OutputPerMillion} out per 1M tokens (was {If(cur.InputPerMillion, 0D)} / {If(cur.OutputPerMillion, 0D)}).")
        Next
    End Function

    Private ReadOnly _options As HostOptions
    Private ReadOnly _configPath As String
    Private ReadOnly _log As StructuredLogger
    Private ReadOnly _redactor As SecretRedactor
    Private ReadOnly _clock As IClock
    Private ReadOnly _mcpFactory As IMcpClientFactory
    Private ReadOnly _providerOverride As IAIProvider
    Private _h As HostServices
    Private _http As HttpAdminServer
    ''' <summary>The admin API (it also runs the scheduled chat prompts).</summary>
    Private _api As AdminApi
    Private _cts As CancellationTokenSource
    Private ReadOnly _background As New List(Of Task)
    Private _stopped As Integer

    Public Sub New(options As HostOptions, configPath As String, log As StructuredLogger, redactor As SecretRedactor,
                   Optional clock As IClock = Nothing, Optional mcpFactory As IMcpClientFactory = Nothing, Optional providerOverride As IAIProvider = Nothing)
        _options = options
        _configPath = configPath
        _log = log
        _redactor = redactor
        _clock = If(clock, SystemClock.Instance)
        _mcpFactory = If(mcpFactory, New SdkMcpClientFactory())
        _providerOverride = providerOverride
    End Sub

    Public ReadOnly Property Services As HostServices
        Get
            Return _h
        End Get
    End Property

    Public ReadOnly Property AdminUrl As String
        Get
            Return _http?.DisplayUrl
        End Get
    End Property

    ' ---------------------------------------------------------------------------------------------
    ' Build
    ' ---------------------------------------------------------------------------------------------

    Public Async Function BuildAsync() As Task
        Dim o = _options
        For Each kv In o.AIProviders
            _redactor.AddSecret(kv.Value.ApiKey)
        Next
        For Each s In o.McpServers
            If s.Auth IsNot Nothing Then _redactor.AddSecret(s.Auth.ResolveSecret())
        Next
        Directory.CreateDirectory(o.Host.DataDirectory)

        Dim h As New HostServices With {
            .Options = o, .ConfigPath = _configPath, .Version = Version, .StartedUtc = DateTime.UtcNow,
            .InstanceId = Environment.MachineName & ":" & Environment.ProcessId.ToString(), .Log = _log, .Redactor = _redactor, .Clock = _clock}
        _h = h

        ' Persistence
        h.Database = New Database(ResolveDatabasePath(o.Host.DataDirectory, _log), o.Limits.MaxDatabaseOperations)
        Dim applied = Await New SchemaMigrator(h.Database).MigrateAsync().ConfigureAwait(False)
        If applied > 0 Then _log.Info("Database", $"Applied {applied} schema migration(s).")
        h.Tasks = New TaskRepository(h.Database)
        h.Messages = New MessageRepository(h.Database)
        h.Telemetry = New TelemetryRepository(h.Database)
        h.AgentsRepo = New AgentRepository(h.Database)
        h.MemoryStore = New SqliteMemoryStore(h.Database)

        ' Events + consumers
        h.Events = New AgentEventBus(_log)
        h.Statistics = New StatisticsAggregator(h.Events, h.Telemetry, _log)
        Await h.Statistics.LoadAsync(h.Database).ConfigureAwait(False)
        h.Activity = New ActivityPersister(h.Events, h.Telemetry, _log)
        h.Alerts = New AlertService(h.Events, o.Alerts, _log)

        ' Economy
        h.Wallets = New WalletService(h.Database, h.Events, o.Economy, _clock)
        Await h.Wallets.InitializeAsync().ConfigureAwait(False)
        h.Pricing = New PricingService(h.Database, o.Models, o.ImageGeneration)
        ' Account balances (users pay for their chats, API calls and agents) and Model Square prices.
        h.Accounts = New AccountService(h.Database, _log)
        h.Deposits = New DepositService(h.Database, h.Accounts, o.Payments, _log)
        ' The BTCPay checkout (Balance > Deposit) is shown in a frame on the pages.
        Http.HttpAdminServer.ExtraFrameSources = If(h.Deposits.CheckoutOrigin, "")
        Await h.Accounts.LoadAsync().ConfigureAwait(False)

        ' AI
        h.Providers = New Dictionary(Of String, IAIProvider)(StringComparer.OrdinalIgnoreCase)
        For Each kv In o.AIProviders
            h.Providers(kv.Key) = If(_providerOverride, CType(New OpenAICompatibleProvider(kv.Key, kv.Value), IAIProvider))
        Next
        Dim events = h.Events
        h.ModelRegistry = New ModelRegistry(o.Models, o.Routing.Spillover.AgingSeconds, _clock,
            Sub(key, fromState, toState)
                events.Emit(AgentEventType.ModelHealthChanged, $"Model {key}: circuit {fromState} → {toState}",
                            level:=If(toState = CircuitState.Open, EventLevel.Warning, EventLevel.Info),
                            data:=New Dictionary(Of String, Object) From {{"model", key}, {"state", toState.ToString()}})
            End Sub)
        ' Profiles are needed by model access checks (subagents inherit their parent's access).
        h.Profiles = New ProfileRegistry()
        ' Custom models and per-agent grants (admin-managed, stored in the database). Custom model prices are registered
        ' before admin price overrides load so the latest pricing history wins.
        h.ModelAccess = New ModelAccessService(h.Database, h.ModelRegistry, h.Pricing, h.Events, o.Models, o.AIProviders.Keys, _clock)
        h.ModelAccess.AttachProfiles(h.Profiles)
        Await h.ModelAccess.LoadAsync().ConfigureAwait(False)
        Await h.Pricing.LoadOverridesAsync().ConfigureAwait(False)
        Await SyncCatalogPricesAsync(h, o).ConfigureAwait(False)
        ' My models: users' own API keys (OpenAI, Anthropic, Amazon Bedrock). Registered as free models of provider "byo",
        ' which reaches each one through ByoHandler; only the owner and agents they own may use them.
        h.Byo = New ByoService(h.Database, New Global.Agentic.Security.SecretBox(Path.Combine(o.Host.DataDirectory, "secrets.key")), h.ModelRegistry, h.Pricing, _log)
        Await h.Byo.LoadAsync().ConfigureAwait(False)
        h.Providers(ByoService.ProviderName) = New OpenAICompatibleProvider(ByoService.ProviderName,
            New AIProviderOptions With {.BaseUrl = "http://byo.invalid", .ChatPath = "/chat/completions", .UseStreaming = False, .TimeoutSeconds = 600, .MaxRetries = 1},
            h.Byo.Handler)
        Dim hostRef = h
        h.ModelAccess.AttachOwners(Function(agentId) hostRef.Agents?.OwnerOf(agentId))
        h.Selection = New ModelSelectionService(h.ModelRegistry, o.Routing, h.Pricing, o.Economy, h.ModelAccess)
        Dim usage As New UsageLimiter(_clock)
        Dim providerRetries = If(o.AIProviders.Values.FirstOrDefault()?.MaxRetries, 2)
        h.SpendCaps = New SpendCapTracker()
        h.Gateway = New ModelGateway(h.Providers, h.ModelRegistry, h.Selection, h.Wallets, h.Pricing, h.Telemetry, h.Events, _log, usage, o.Limits, o.Economy,
                                     o.Logging.StoreAiContent, _clock, providerRetries, h.SpendCaps)

        ' Artifacts, approvals, media
        h.Artifacts = New ArtifactStore(h.Database, Path.Combine(o.Host.DataDirectory, "artifacts"), o.Limits.MaxArtifactBytes,
                                        CLng(o.Limits.MaxArtifactStoragePerAgentMb) * 1024L * 1024L, o.Retention.ArtifactDays, h.Events)
        h.Approvals = New ApprovalService(h.Database, h.Events) With {.TaskAutoApproval = AddressOf h.Tasks.AutoApproveSourceAsync}
        h.Vision = New VisionService(h.Gateway, o.Vision)
        h.Images = New ImageGenerationService(h.Gateway, o.ImageGeneration, h.Artifacts, h.Wallets, h.Pricing, h.Telemetry, h.Events, o.Economy, _clock)

        ' Communication
        h.Comms = New AgentCommunicationService(h.Database, h.Messages, h.Tasks, h.Profiles, h.Artifacts, h.Events, _log, o.Communication, o.Limits, _clock)
        h.Resolver = New WaitResolver(h.Database, h.Tasks)

        ' MCP + tools
        h.Mcp = New McpServerManager(o.McpServers, _mcpFactory, h.Events, _log, o.Limits)
        h.McpTools = New McpToolRegistry(h.Mcp, o.McpServers)
        ' Servers added from the web app (datamcp-servers.json): started with the others.
        h.McpUser = New Global.Agentic.Mcp.McpUserServers(o.Host.DataDirectory, New Global.Agentic.Security.SecretBox(Path.Combine(o.Host.DataDirectory, "secrets.key")), h.Mcp, _log)
        h.McpUser.LoadAll()
        ' Organizational learning: case studies of agents that go broke or become very profitable, shared as lessons.
        h.Lessons = New Learning.LessonStore(h.Database)
        h.Learning = New Learning.LearningService(o.Learning, h.Database, h.Lessons, h.Wallets, h.Telemetry, h.Gateway, h.Profiles, h.MemoryStore, h.Events, _log, _clock)
        If o.Learning.Enabled Then h.Wallets.Observer = h.Learning

        ' Built-in web search (search_web) through the configured SearXNG instance.
        h.WebSearch = New Tools.WebSearchService(o.WebSearch)
        h.ToolCatalog = New ToolCatalog(h.Profiles, h.McpTools, h.Vision, h.Images, o.Communication, o.Vision, h.Events, o.Subagents, h.ModelAccess, h.Selection, h.Pricing,
                                        If(o.Learning.Enabled, h.Lessons, Nothing), h.WebSearch)

        ' Files attached to tasks, and the Results inbox.
        Dim sharedRoot = Path.Combine(o.Host.DataDirectory, "shared")
        h.Uploads = New UploadStore(h.Database, Path.Combine(o.Host.DataDirectory, "uploads"), CLng(Math.Max(1, o.Limits.MaxUploadMb)) * 1024L * 1024L)
        h.Attachments = New AttachmentService(h.Uploads, h.Artifacts, sharedRoot)
        h.Results = New ResultService(h.Database, h.Tasks, h.Messages, h.Artifacts, h.Wallets, h.MemoryStore, h.Events, _log, sharedRoot,
                                      Path.Combine(o.Host.DataDirectory, "results"))

        ' Task execution
        Dim prompts As New PromptBuilder(h.Messages, h.Tasks, h.Lessons, o.Learning, h.Attachments) With {
            .McpServerNotes = o.McpServers.Where(Function(s) s.Enabled AndAlso Not String.IsNullOrWhiteSpace(s.AgentInstructions)).
                                           Select(Function(s) (s.Id, s.Name, s.AgentInstructions)).ToList()}
        Dim toolExec As New ToolExecutor(h.Telemetry, h.Events, _log)
        Dim handlers As New List(Of ITaskHandler) From {
            New ReasoningTaskHandler(h.Gateway, prompts, h.ToolCatalog, toolExec, h.Telemetry, h.Events, _log, o.Routing.Escalation, o.Autonomy.VerifyPasses) With {
                .ParallelTools = o.Autonomy.ParallelTools, .MaxParallelTools = o.Autonomy.MaxParallelTools, .ToolLoadingOptions = o.ToolLoading},
            New HttpCheckTaskHandler(), New McpToolTaskHandler(h.Mcp), New NoopTaskHandler()}
        Dim executor As New TaskExecutor(handlers, h.Tasks, h.Comms, h.Approvals, h.Resolver, h.Events, _log, _clock)
        Dim mailbox As New MailboxProcessor(h.Messages, h.Tasks, h.Comms, h.Resolver, h.Events, _log, o.Communication, _clock)
        Dim runtimeServices As New RuntimeServices With {
            .Tasks = h.Tasks, .Executor = executor, .Mailbox = mailbox, .Events = h.Events, .Log = _log, .Clock = _clock,
            .Supervision = o.Supervision, .Scheduling = o.Scheduling, .InstanceId = h.InstanceId}
        Dim scopes As New AgentScopeFactory With {
            .MemoryStore = h.MemoryStore, .Comms = h.Comms, .Artifacts = h.Artifacts, .Wallets = h.Wallets, .Pricing = h.Pricing,
            .Registry = h.ModelRegistry, .Tasks = h.Tasks, .Approvals = h.Approvals, .Vision = h.Vision, .Images = h.Images, .Events = h.Events,
            .Gateway = h.Gateway, .ModelAccess = h.ModelAccess, .SpendCaps = h.SpendCaps}
        h.Agents = New AgentManager(o, h.Profiles, runtimeServices, scopes, h.AgentsRepo, h.Tasks, h.Wallets, h.Statistics, h.Events, _log)
        h.Agents.AttachServices(h.Database, h.ModelAccess, h.SpendCaps, h.Resolver, h.Artifacts, h.Selection)
        executor.Observer = h.Agents
        h.Comms.SetWaker(h.Agents)
        h.Scheduler = New WorkScheduler(h.Database, h.Tasks, h.Profiles, h.Agents, h.Events, _log, _clock)
        h.Agents.AttachScheduler(h.Scheduler)
        ' Global tasks: requests the host plans, distributes across agents and combines.
        h.GlobalTasks = New GlobalTaskService(o.GlobalTasks, h.Database, h.Agents, h.Tasks, h.Gateway, o.McpServers, h.Events, _log, _clock)
        Dim globalRef = h.GlobalTasks
        prompts.ClosedWorkProvider = Function(agentId) globalRef.ClosedSectionAsync(agentId)
        ' Reviews and delivery of finished work; agents live and die by their wallets.
        h.Outcomes = New OutcomeService(h.Database, h.Agents, h.Tasks, h.Results, h.Attachments, h.Artifacts, h.Events, _log)
        h.Agents.Outcomes = h.Outcomes
        h.GlobalTasks.Outcomes = h.Outcomes
        h.GlobalTasks.Results = h.Results
        h.GlobalTasks.Attachments = h.Attachments
        h.Wallets.BalanceChanged = AddressOf h.Agents.OnBalanceChanged
        ' An agent's cloud use is also charged to the account that owns it; that account's agents stop when it has no money.
        h.Accounts.BalanceChanged = AddressOf h.Agents.OnAccountBalance
        Dim accountsRef = h.Accounts
        Dim agentsRef = h.Agents
        h.Gateway.AgentCharged = Async Function(agentId, micros, modelKey, description)
                                     Dim owner = agentsRef.OwnerOf(agentId)
                                     If owner Is Nothing Then Return
                                     Await accountsRef.ChargeAsync(owner, micros, AccountService.EntryAgent, modelKey, Nothing, Nothing, agentId, agentId & ": " & description).ConfigureAwait(False)
                                 End Function
        ' People-facing model access: the OpenAI-compatible API, custom models and the Chat tab.
        h.ModelHub = New ModelHub(o.AIProviders.Values.FirstOrDefault(), h.Database, _log)
        h.ModelHub.SetHiddenModels(o.Chat.HiddenModels)
        h.ModelHub.SetProviderMerges(o.Chat.ProviderMerges)
        h.ModelHub.AttachByo(h.Byo)
        ' Model control: models an administrator turned off are used by nobody (Chat, the API, custom-model routes, agents, images, videos).
        Await h.ModelHub.LoadDisabledAsync().ConfigureAwait(False)
        Dim hubRef = h.ModelHub
        h.Gateway.Selection.TurnedOff = AddressOf hubRef.IsDisabled
        h.Updates = New UpdateService(o.Updates.Directory, o.Updates.Enabled, o.Updates.CheckIntervalMinutes, h.Database, _log, Path.Combine(o.Host.DataDirectory, "cache", "update-zips")) With {
            .PublicInstall = o.Updates.PublicInstall}
        h.Rag = New Global.Agentic.Work.RagService(h.Database, h.ModelHub, h.Uploads, o.Rag, _log)
        ' Semantic agent memory reuses the RAG embedder: memories carry vectors and recall ranks by meaning (keyword-only without an embedding model).
        Dim memStore = TryCast(h.MemoryStore, SqliteMemoryStore)
        If memStore IsNot Nothing Then memStore.AttachSemantic(h.Rag.VectorEmbedder, Function(ct) h.Rag.ResolveModelAsync(ct))
        h.Chat = New ChatService(h.Database, h.ModelHub, h.Mcp, o.McpServers, h.WebSearch, h.Uploads, o.Chat, _log)
        ' Real context windows, learned from what providers accept and refuse (agents and chat both teach and use them).
        h.ContextLimits = New Global.Agentic.AI.ContextLimits(h.Database, _log)
        Await h.ContextLimits.LoadAsync().ConfigureAwait(False)
        h.ModelRegistry.Limits = h.ContextLimits
        h.Chat.Limits = h.ContextLimits
        h.ModelHealth = New Global.Agentic.AI.ModelHealth(h.Database, _log)
        h.Chat.Health = h.ModelHealth
        h.Gateway.Health = h.ModelHealth
        h.Chat.Accounts = h.Accounts
        h.Chat.Gateway = h.Gateway
        h.Chat.Byo = h.Byo
        h.Chat.Rag = h.Rag
        ' Work handed to agents from a chat comes back into that chat.
        h.ChatReports = New ChatReportService(h.Chat, h.Agents, _log)
        h.Agents.ChatReports = h.ChatReports
        h.Outcomes.WorkFinalized = AddressOf h.ChatReports.OnWorkFinalizedAsync
        h.GlobalTasks.Finished = AddressOf h.ChatReports.OnGlobalTaskFinishedAsync
        ' The code sandbox: pulling files out of it, and reaping long-lived processes/containers the host started there.
        h.SandboxActivity = New Global.Agentic.Work.SandboxActivity()
        h.Sandbox = New Global.Agentic.Work.SandboxGateway(h.Mcp, _log)
        h.SandboxResources = New Global.Agentic.Work.SandboxResourceStore(h.Database)
        h.Chat.SandboxActivity = h.SandboxActivity
        h.Chat.Sandbox = h.Sandbox
        h.SandboxReaper = New Global.Agentic.Work.SandboxReaper(h.Sandbox, h.SandboxResources, o.Chat.Sandbox, o.Chat.CodeServerId, h.Chat,
            h.SandboxActivity, AddressOf h.AgentsRepo.GetStateAsync, Function(k, v) h.AgentsRepo.SetStateAsync(k, v), _log)
        ' App previews: a web app running in a sandbox container opens at /container/{id}/ on this host's own port.
        h.SandboxPreviews = New Global.Agentic.Work.SandboxPreviewStore(h.Database)
        h.SandboxReaper.Previews = h.SandboxPreviews
        h.SandboxReaper.PreviewHost = PreviewHostOf(o)
        ' The code server's watchdog checks are never tracked as sandbox work.
        Dim codeOptions = o.McpServers.FirstOrDefault(Function(s) String.Equals(s.Id, o.Chat.CodeServerId, StringComparison.OrdinalIgnoreCase))
        Dim probe As String = Nothing
        If codeOptions?.HealthCheck IsNot Nothing Then codeOptions.HealthCheck.Arguments.TryGetValue("command", probe)
        h.SandboxReaper.HealthCheckCommand = probe
        h.Chat.SandboxReaper = h.SandboxReaper
        h.PreviewProxy = New Http.PreviewProxy(h.SandboxPreviews, _log) With {
            .Isolation = If(o.Chat.Sandbox.PreviewIsolation, "sandbox"), .Validate = AddressOf h.SandboxReaper.ValidatePreviewAsync}
        ' When this host cannot reach the sandbox's published ports, previews go through the sandbox connection.
        Dim sandboxRef = h.Sandbox
        Dim codeServer = o.Chat.CodeServerId
        Dim previewMax = CLng(Math.Max(1, o.Chat.Sandbox.MaxDownloadMb)) * 1024L * 1024L
        h.PreviewProxy.Tunnel = Function(p, method, path, headers, body, ct) sandboxRef.TunnelHttpAsync(codeServer, p.TargetPort, method, path, headers, body, previewMax, ct)
        ' Autonomy services: inbound triggers, standing goals, authored skills, self-modification proposals and outbound notifications.
        h.Notifications = New Global.Agentic.Events.NotificationService(o.Notifications, _log)
        h.Skills = New Global.Agentic.Work.SkillStore(h.Database)
        h.Proposals = New Global.Agentic.Work.ProfileProposalService(h.Database, _log)
        h.Goals = New Global.Agentic.Work.GoalService(h.Database, h.Tasks, h.Profiles, h.Agents, h.Events, o.Autonomy, _log, _clock)
        h.Triggers = New Global.Agentic.Work.TriggerService(h.Database, h.Tasks, h.Profiles, h.Agents, h.Events, o.Autonomy, _log)
        h.ToolCatalog.AttachAutonomy(h.Notifications, h.Goals, h.Skills, h.Proposals, o.Autonomy.AllowSelfModification)
        prompts.PatchProvider = Function(agentId) h.Proposals.GetPatchAsync(agentId)
        ' Fully autonomous agents: approval policies and supervisor agents, initiative, bounties, hires, evaluations (with
        ' rollback of changes that make an agent worse), the secrets vault and mailboxes that start tasks.
        h.Secrets = New Global.Agentic.Work.SecretVault(h.Database, New Global.Agentic.Security.SecretBox(Path.Combine(o.Host.DataDirectory, "secrets.key")), _redactor)
        Await h.Secrets.LoadAsync().ConfigureAwait(False)
        ' Speech (XTTS): its key comes from the config file or the vault, so the vault is there first.
        _redactor.AddSecret(o.TextToSpeech.ApiKey)
        h.Speech = New Global.Agentic.Work.SpeechService(o.TextToSpeech, h.Secrets, _log)
        h.Audio = New Global.Agentic.Work.AudioStore(h.Database, Path.Combine(o.Host.DataDirectory, "audio"))
        ' Pictures (Stable Diffusion, the Images tab): the key from the config file or the vault, like speech.
        _redactor.AddSecret(o.StableDiffusion.ApiKey)
        h.Diffusion = New Global.Agentic.Work.DiffusionService(o.StableDiffusion, h.Secrets, _log)
        h.DiffusionStore = New Global.Agentic.Work.DiffusionStore(h.Database, Path.Combine(o.Host.DataDirectory, "images"))
        h.DiffusionJobs = New Global.Agentic.Work.DiffusionJobs(h.Diffusion, h.DiffusionStore, h.Accounts, _log)
        ' Songs (Suno through the local wrapper, the Music tab), likewise.
        _redactor.AddSecret(o.Music.ApiKey)
        h.Music = New Global.Agentic.Work.MusicService(o.Music, h.Secrets, _log)
        h.MusicStore = New Global.Agentic.Work.MusicStore(h.Database, Path.Combine(o.Host.DataDirectory, "music"))
        h.MusicJobs = New Global.Agentic.Work.MusicJobs(h.Music, h.MusicStore, h.Accounts, _log)
        ' Prices (the Money tab) and news (the News tab): read on their own, for the pages, every agent and chats that turn them on.
        _redactor.AddSecret(o.Markets.CryptoApiKey)
        h.Markets = New Global.Agentic.Work.MarketsService(h.Database, o.Markets, _log)
        h.News = New Global.Agentic.Work.NewsService(h.Database, o.News, _log)
        h.Stocks = New Global.Agentic.Work.StocksService(h.Database, o.Stocks, _log)
        h.Polymarket = New Global.Agentic.Work.PolymarketService(o.Polymarket, _log)
        h.Chat.Markets = h.Markets
        h.Chat.News = h.News
        h.Chat.Stocks = h.Stocks
        h.Chat.Polymarket = h.Polymarket
        ' The Images and Music tabs in chat (stable_diffusion, make_song).
        h.Chat.Diffusion = h.Diffusion
        h.Chat.DiffusionJobs = h.DiffusionJobs
        h.Chat.DiffusionStore = h.DiffusionStore
        h.Chat.Music = h.Music
        h.Chat.MusicJobs = h.MusicJobs
        h.Chat.MusicStore = h.MusicStore
        h.ToolCatalog.AttachData(h.Markets, h.News, h.Stocks, h.Polymarket)
        h.Autonomy = New Global.Agentic.Work.AutonomyService(h.Database, o, h.Approvals, h.Agents, h.Tasks, h.Wallets, h.Resolver, h.Events, _log, _clock) With {
            .Proposals = h.Proposals, .Notifications = h.Notifications, .CaseStudies = If(o.Learning.Enabled, h.Learning, Nothing)}
        h.Evals = New Global.Agentic.Work.EvalService(h.Database, h.Agents, h.Tasks, h.Gateway, o, h.Events, _log, _clock) With {.Proposals = h.Proposals}
        Dim notifyRef = h.Notifications
        ' An AI provider's account out of credit: besides the dashboard's notice (with its "Turn back on" button), the
        ' administrator hears it on the notification channel, once per pause.
        h.Gateway.OnProviderPaused = Sub(p)
                                         If notifyRef Is Nothing OrElse Not notifyRef.Enabled Then Return
                                         Dim unused = notifyRef.NotifyAsync(Nothing, $"AI provider {p.Provider} is out of credit",
                                             $"Its models ({String.Join(", ", p.Models)}) are paused and agents use other providers meanwhile. Add balance, then turn it back on " &
                                             $"on the Models page (or wait: it is tried again by itself every {o.Routing.OutOfCreditPauseMinutes} min). Provider said: {p.LastError}",
                                             "host", CancellationToken.None)
                                     End Sub
        h.Evals.Notify = Async Function(subject, message, agentId)
                             If notifyRef IsNot Nothing AndAlso notifyRef.Enabled Then Await notifyRef.NotifyAsync(Nothing, subject, message, agentId, CancellationToken.None).ConfigureAwait(False)
                         End Function
        ' A global task step stuck on its agent's AI usage limit or budget: the administrator hears it once.
        h.GlobalTasks.AdminNotice = Async Function(subject, message)
                                        If notifyRef IsNot Nothing AndAlso notifyRef.Enabled Then Await notifyRef.NotifyAsync(Nothing, subject, message, "host", CancellationToken.None).ConfigureAwait(False)
                                    End Function
        h.Inboxes = New Global.Agentic.Work.EmailInboxService(h.Database, h.Secrets, h.Agents, o.Autonomy, h.Events, _log, _clock)
        h.Autonomy.Evals = h.Evals
        h.Autonomy.Inboxes = h.Inboxes
        Await h.Autonomy.InitializeAsync().ConfigureAwait(False)
        h.Agents.Autonomy = h.Autonomy
        h.Outcomes.PayBounty = AddressOf h.Autonomy.PayBountyAsync
        h.GlobalTasks.PayBounty = AddressOf h.Autonomy.PayGlobalBountyAsync
        prompts.ManagedProvider = AddressOf h.Autonomy.ManagedAgents
        Dim chatRef = h.Chat
        Dim mcpRef = h.Mcp
        Dim codeServerId = o.Chat.CodeServerId
        h.ToolCatalog.AttachAutonomyPlus(h.Autonomy, h.Scheduler, h.Triggers, h.Secrets, h.Rag, Function(agentId) hostRef.Agents?.OwnerOf(agentId),
                                         Function(language, code, ct) chatRef.RunCodeAsync(language, code, False, ct),
                                         Function() Not String.IsNullOrEmpty(codeServerId) AndAlso mcpRef.GetTools(codeServerId).Any(Function(t) t.Name = "run_command"),
                                         o.Autonomy)
        ' Pictures and videos come only from their own APIs (ImageGeneration and VideoGeneration, e.g. yuanqiaoyun), never
        ' from the chat provider. ImageGeneration.Model first; when it cannot make a picture, the API's next image model.
        ' User accounts pay the per-image price (Finance → Pricing) and VideoGeneration's per-second price.
        Dim imageRouter = Global.Agentic.AI.ImageRouter.ForApi(o.ImageGeneration, Global.Agentic.AI.ImageRouter.CreateApi(o.ImageGeneration))
        h.ImageRouter = imageRouter
        h.Videos = New Global.Agentic.AI.VideoService(o.VideoGeneration)
        h.Videos.TurnedOff = AddressOf hubRef.IsDisabled
        If imageRouter IsNot Nothing Then imageRouter.TurnedOff = AddressOf hubRef.IsDisabled
        h.Images.Router = imageRouter
        h.Chat.ImageRouter = imageRouter
        h.Chat.Videos = h.Videos
        h.Chat.ImageOptions = o.ImageGeneration
        h.Chat.ImagePriceMicros = Function() Global.Agentic.Economy.PricingService.ImageCostMicros(h.Pricing.GetPrice(Global.Agentic.Economy.PricingService.ImageGenerationKey), 1)
        ' Pictures in a chat message for a model that cannot read them go to a vision model: Vision.ModelKeys first, then the
        ' other models configured with the Vision capability. Models configured without it are known not to read images.
        Dim visionModels As New List(Of String)
        For Each key In If(o.Vision?.ModelKeys, New List(Of String))
            Dim vm = o.Models.FirstOrDefault(Function(x) String.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))
            If vm IsNot Nothing AndAlso Not visionModels.Contains(vm.ModelId, StringComparer.OrdinalIgnoreCase) Then visionModels.Add(vm.ModelId)
        Next
        For Each vm In o.Models.Where(Function(x) x.Capabilities.Contains(ModelCapability.Vision))
            If Not visionModels.Contains(vm.ModelId, StringComparer.OrdinalIgnoreCase) Then visionModels.Add(vm.ModelId)
        Next
        h.Chat.VisionModels = visionModels
        h.Chat.NoVisionModels = New HashSet(Of String)(o.Models.Where(Function(x) Not x.Capabilities.Contains(ModelCapability.Vision)).Select(Function(x) x.ModelId), StringComparer.OrdinalIgnoreCase)

        ' Seed usage limits from the last day of history (limits survive restarts).
        ' (done after agents are known, in StartAsync)
        _usageLimiter = usage

        ' Approvals resolved by an admin resume the waiting task.
        Dim resolver = h.Resolver
        Dim agents = h.Agents
        Dim log = _log
        h.Approvals.SetResolvedCallback(
            Sub(rec)
                If String.IsNullOrEmpty(rec.AgentId) Then Return
                Dim t = Task.Run(Async Function()
                                     Try
                                         Await resolver.ResolveAsync(rec.AgentId, rec.WaitKey(), TaskExecutor.ResolutionFor(rec), "Approval " & rec.Status.ToString()).ConfigureAwait(False)
                                         agents.Wake(rec.AgentId)
                                     Catch ex As Exception
                                         log.Warn("Approvals", "Failed to resume task after approval: " & ex.Message, rec.AgentId)
                                     End Try
                                 End Function)
            End Sub)

        ' HTTP
        If o.Server.Enabled Then
            Await BootstrapAdminUsersAsync().ConfigureAwait(False)
            Dim auth As New AuthService(h.AgentsRepo, o.Admin, o.Server.SessionMinutes, _clock, _redactor)
            Dim router As New Router()
            Dim api As New AdminApi(h, auth)
            api.Register(router)
            _api = api
            _http = New HttpAdminServer(o.Server, router, auth, New StaticFiles(), _log)
            If o.Chat.Sandbox.Previews AndAlso h.PreviewProxy IsNot Nothing Then _http.ContainerProxy = AddressOf h.PreviewProxy.HandleAsync
            _http.SharedArtifacts = AddressOf api.ServeSharedArtifactAsync
        End If
    End Function

    ''' <summary>
    ''' Where the sandbox's published ports are reached: Chat.Sandbox.PreviewHost, else the host of the code server's MCP
    ''' endpoint (its containers publish ports on that machine).
    ''' </summary>
    Friend Shared Function PreviewHostOf(o As HostOptions) As String
        If Not String.IsNullOrWhiteSpace(o.Chat.Sandbox.PreviewHost) Then Return o.Chat.Sandbox.PreviewHost.Trim()
        Dim code = o.McpServers.FirstOrDefault(Function(s) String.Equals(s.Id, o.Chat.CodeServerId, StringComparison.OrdinalIgnoreCase))
        Dim u As Uri = Nothing
        If code IsNot Nothing AndAlso Uri.TryCreate(If(code.Endpoint, ""), UriKind.Absolute, u) Then Return u.Host
        Return "127.0.0.1"
    End Function

    Private _usageLimiter As UsageLimiter

    ''' <summary>Config users are upserted; if no admin exists at all, a one-time bootstrap password is generated and printed once.</summary>
    Private Async Function BootstrapAdminUsersAsync() As Task
        For Each u In _options.Admin.Users
            Await _h.AgentsRepo.ApplyConfigUserAsync(u.Username, u.PasswordHash).ConfigureAwait(False)
        Next
        If Await _h.AgentsRepo.CountAdminUsersAsync().ConfigureAwait(False) = 0 Then
            Dim password = Ids.NewToken(15)
            Await _h.AgentsRepo.UpsertAdminUserAsync("admin", PasswordHasher.Hash(password), "Bootstrap").ConfigureAwait(False)
            Try
                Console.WriteLine()
                Console.WriteLine("  *** First run: created dashboard login  admin / " & password)
                Console.WriteLine("  *** This password is shown only once. Add your own user (--hash-password) to Admin.Users to replace it.")
                Console.WriteLine()
            Catch
            End Try
            _log.Write(LogSeverity.Warn, "Auth", "Bootstrap admin user created (password printed to console once).")
        End If
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Start
    ' ---------------------------------------------------------------------------------------------

    Public Async Function StartAsync(Optional externalToken As CancellationToken = Nothing) As Task
        _cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken)
        Dim ct = _cts.Token
        Dim h = _h
        h.Events.Emit(AgentEventType.HostLifecycle, $"Agentic {Version} starting")

        h.Statistics.Start(ct)
        h.Activity.Start(ct)
        h.Alerts.Start(ct)

        ' Startup recovery.
        Dim requeued = Await h.Tasks.RequeueInterruptedAsync().ConfigureAwait(False)
        Dim orphaned = Await h.Messages.ResetOrphanedProcessingAsync().ConfigureAwait(False)
        If requeued > 0 OrElse orphaned > 0 Then _log.Info("Recovery", $"Re-queued {requeued} interrupted task(s); reset {orphaned} orphaned message(s).")

        ' MCP servers connect in the background; agents do not wait for them.
        Await h.Mcp.StartAsync(ct).ConfigureAwait(False)

        ' Agents
        Await h.Agents.InitializeAsync().ConfigureAwait(False)
        ' Agents of accounts without money wait until money is added (administrators' agents never do).
        For Each owner In h.Agents.Owners()
            Dim acc = Await h.Accounts.GetAccountAsync(owner).ConfigureAwait(False)
            If acc IsNot Nothing AndAlso acc.Charged AndAlso acc.BalanceMicros <= 0 Then h.Agents.MarkUnfundedOwner(owner)
        Next
        ' Token limits count cloud use only (see ModelGateway): the last day's local tokens are left out after a restart too.
        Dim localModelKeys = h.ModelRegistry.All().Where(Function(mr) mr.Options.IsLocal).Select(Function(mr) mr.Key).ToList()
        For Each p In h.Profiles.AllProfiles()
            Dim used = Await h.Telemetry.AiUsageByHourSinceAsync(p.Id, DateTime.UtcNow.AddDays(-1), localModelKeys).ConfigureAwait(False)
            If used.Count > 0 Then _usageLimiter.Seed(p.Id, used)
        Next
        h.Agents.StartAll(ct)
        h.Scheduler.Start(ct)
        h.Goals.Start(ct)
        h.Autonomy.Start(ct)
        Await h.Triggers.StartWatchersAsync(ct).ConfigureAwait(False)
        ' Picture jobs that had not ended before the restart are followed again.
        h.DiffusionJobs.Start(ct)
        h.MusicJobs.Start(ct)
        h.Markets.Start(ct)
        h.News.Start(ct)
        h.Stocks.Start(ct)
        h.Polymarket.Start(ct)

        ' Maintenance loops.
        _background.Add(MaintenanceLoopAsync(ct))
        _background.Add(RetentionLoopAsync(ct))
        _background.Add(BackupLoopAsync(ct))
        _background.Add(ProviderCheckLoopAsync(ct))
        _background.Add(SandboxReaperLoopAsync(ct))
        _background.Add(ChatSchedulesLoopAsync(ct))
        _background.Add(Task.Run(Function() h.Learning.RunAsync(ct)))
        _background.Add(Task.Run(Function() h.GlobalTasks.RunAsync(ct)))

        If _http IsNot Nothing Then _http.Start(ct)
        Await h.Mcp.WaitForInitialConnectAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(False)
        h.LastProviderCheck = Await CheckProviderAsync(h, ct).ConfigureAwait(False)
        h.Events.Emit(AgentEventType.HostLifecycle, $"Agentic {Version} started with {h.Agents.AgentCount} agent(s)")
    End Function

    ''' <summary>Checks the AI provider is reachable and that every configured model id exists.</summary>
    Public Shared Async Function CheckProviderAsync(h As HostServices, ct As CancellationToken) As Task(Of ProviderCheckResult)
        Dim result As New ProviderCheckResult With {.CheckedUtc = DateTime.UtcNow}
        Try
            Dim available As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            ' Users' own models (provider "byo") are checked by their owners (My models → Test), not here.
            For Each p In h.Providers.Where(Function(kv) Not String.Equals(kv.Key, ByoService.ProviderName, StringComparison.OrdinalIgnoreCase)).Select(Function(kv) kv.Value).Distinct()
                For Each id In Await p.ListModelsAsync(ct).ConfigureAwait(False)
                    available.Add(id)
                Next
            Next
            result.Reachable = True
            result.AvailableModels = available.OrderBy(Function(x) x).ToList()
            result.MissingModels = h.Options.Models.Where(Function(m) m.Enabled AndAlso Not available.Contains(m.ModelId)).Select(Function(m) $"{m.Key} ({m.ModelId})").ToList()
            If result.MissingModels.Count > 0 Then h.Log.Warn("AI", "Configured models not offered by the provider: " & String.Join(", ", result.MissingModels))
        Catch ex As Exception When Not (ExceptionUtil.IsCancellation(ex) AndAlso ct.IsCancellationRequested)
            ' A provider that does not answer in time (its own 30 s limit, not a shutdown) is unreachable; it must not stop the start.
            result.Reachable = False
            result.ErrorMessage = If(ExceptionUtil.IsCancellation(ex), "The provider's model list did not answer within 30 s.", ex.Message)
            h.Log.Warn("AI", "AI provider check failed: " & result.ErrorMessage)
        End Try
        Return result
    End Function

    Private Async Function ProviderCheckLoopAsync(ct As CancellationToken) As Task
        While Not ct.IsCancellationRequested
            Try
                Await Task.Delay(TimeSpan.FromMinutes(10), ct).ConfigureAwait(False)
                _h.LastProviderCheck = Await CheckProviderAsync(_h, ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            Catch ex As Exception
                _log.Warn("AI", "Provider check loop error: " & ex.Message)
            End Try
        End While
    End Function

    ''' <summary>Every 15 s: expire overdue messages (resuming waiting senders) and overdue approvals.</summary>
    Private Async Function MaintenanceLoopAsync(ct As CancellationToken) As Task
        While Not ct.IsCancellationRequested
            Try
                Await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
            Try
                Dim expired = Await _h.Messages.ExpireDueAsync(_clock.UtcNow).ConfigureAwait(False)
                For Each m In expired
                    _h.Events.Emit(AgentEventType.MessageExpired, $"Message {m.Id} from {m.SenderAgentId} to {m.RecipientAgentId} expired", m.SenderAgentId,
                                   messageId:=m.Id, correlationId:=m.CorrelationId, level:=EventLevel.Warning)
                    If m.RequiresResponse AndAlso m.SenderAgentId <> AgentCommunicationService.AdminSender Then
                        Await _h.Resolver.ResolveAsync(m.SenderAgentId, "msg:" & m.Id, Resolutions.Timeout($"{m.RecipientAgentId} did not answer before the request expired."), "Request expired").ConfigureAwait(False)
                        _h.Agents.Wake(m.SenderAgentId)
                    End If
                    If m.MessageType = MessageType.TaskDelegation AndAlso Not String.IsNullOrEmpty(m.RelatedTaskId) Then
                        ' The delegator has been told the request expired and moved on, so the work stops wherever it is - also
                        ' while it runs (a daily review's follow-up ran on for hours after the review gave up on it).
                        Dim delegated = Await _h.Tasks.GetAsync(m.RelatedTaskId).ConfigureAwait(False)
                        If delegated IsNot Nothing AndAlso Not delegated.IsTerminal Then
                            Await _h.Agents.CancelTaskAsync(delegated.AgentId, delegated.Id, $"Delegation expired: {m.SenderAgentId} stopped waiting for it").ConfigureAwait(False)
                        End If
                    End If
                Next
                Await _h.Approvals.ExpireDueAsync(_clock.UtcNow).ConfigureAwait(False)
                Dim orphaned = Await _h.Approvals.CancelOrphanedAsync().ConfigureAwait(False)
                If orphaned > 0 Then _log.Info("Approvals", $"Withdrew {orphaned} approval request(s) whose task had already ended.")
                Await EnforceTaskDeadlinesAsync().ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Maintenance", "Maintenance pass failed: " & ex.Message)
            End Try
        End While
    End Function

    ''' <summary>
    ''' Tasks created with a deadline (DeadlineEnforced): when it passes, the task is cancelled; with "keep working past
    ''' the deadline" it is marked late, the event is raised and it goes on.
    ''' </summary>
    Friend Async Function EnforceTaskDeadlinesAsync() As Task
        Dim now = _clock.UtcNow
        For Each t In Await _h.Tasks.ListDeadlineDueAsync(now).ConfigureAwait(False)
            If Not Await _h.Tasks.MarkDeadlinePassedAsync(t.Id, now).ConfigureAwait(False) Then Continue For
            If t.RunPastDeadline Then
                _h.Events.Emit(AgentEventType.TaskProgress, $"Past its deadline ({t.DeadlineUtc.GetValueOrDefault():u}); keeps working as asked: {t.Name}", t.AgentId, t.Id,
                               correlationId:=t.CorrelationId, level:=EventLevel.Warning,
                               data:=New Dictionary(Of String, Object) From {{"deadlineUtc", t.DeadlineUtc}, {"runPastDeadline", True}})
            Else
                Dim r = Await _h.Agents.CancelTaskAsync(t.AgentId, t.Id, $"Deadline reached ({t.DeadlineUtc.GetValueOrDefault():u}).").ConfigureAwait(False)
                _h.Events.Emit(AgentEventType.TaskCancelled, $"Deadline reached; {If(r.Success, "cancelled", "could not be cancelled")}: {t.Name}", t.AgentId, t.Id,
                               correlationId:=t.CorrelationId, level:=EventLevel.Warning, data:=New Dictionary(Of String, Object) From {{"deadlineUtc", t.DeadlineUtc}})
            End If
        Next
    End Function

    Private Async Function RetentionLoopAsync(ct As CancellationToken) As Task
        Dim retention As New RetentionService(_h.Database, _options.Retention, _h.MemoryStore, _h.Artifacts, _log)
        Try
            Await Task.Delay(TimeSpan.FromMinutes(2), ct).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            Return
        End Try
        While Not ct.IsCancellationRequested
            Try
                Await retention.RunOnceAsync(_clock.UtcNow).ConfigureAwait(False)
                Await _h.Agents.PurgeFinishedSubagentsAsync(_clock.UtcNow.AddDays(-Math.Max(1, _options.Subagents.RetainDays))).ConfigureAwait(False)
                ' Temporary chats are gone a day after their last message.
                Await _h.Chat.PurgeTemporaryAsync().ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Retention", "Retention pass failed: " & ex.Message)
            End Try
            Try
                Await Task.Delay(TimeSpan.FromMinutes(Math.Max(5, _options.Retention.RunIntervalMinutes)), ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
        End While
    End Function

    ''' <summary>Stops long-lived sandbox processes and containers the host started once they go idle or reach their maximum age.</summary>
    ''' <summary>Scheduled chat prompts (/schedule): the due ones are sent every 30 seconds, also while no window is open.</summary>
    Private Async Function ChatSchedulesLoopAsync(ct As CancellationToken) As Task
        If _api Is Nothing OrElse Not _options.Chat.Enabled Then Return
        Try
            Await Task.Delay(TimeSpan.FromSeconds(Math.Min(15, Math.Max(1, _options.Chat.SchedulePollSeconds))), ct).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            Return
        End Try
        While Not ct.IsCancellationRequested
            Try
                Await _api.RunDueSchedulesAsync(_clock.UtcNow, ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            Catch ex As Exception
                _log.Warn("Chat", "Scheduled prompts pass failed: " & ex.Message)
            End Try
            Try
                Await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.Chat.SchedulePollSeconds)), ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
        End While
    End Function

    Private Async Function SandboxReaperLoopAsync(ct As CancellationToken) As Task
        If _h.SandboxReaper Is Nothing OrElse Not _options.Chat.Sandbox.ReaperEnabled Then Return
        Try
            ' Give MCP servers time to connect before the first pass (and so the startup snapshot sees the real container set).
            Await Task.Delay(TimeSpan.FromMinutes(3), ct).ConfigureAwait(False)
        Catch ex As OperationCanceledException
            Return
        End Try
        While Not ct.IsCancellationRequested
            Try
                Await _h.SandboxReaper.RunOnceAsync(_clock.UtcNow, ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            Catch ex As Exception
                _log.Warn("Sandbox", "Sandbox reaper pass failed: " & ex.Message)
            End Try
            Try
                Await Task.Delay(TimeSpan.FromMinutes(Math.Max(1, _options.Chat.Sandbox.ReaperIntervalMinutes)), ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
        End While
    End Function

    Private Async Function BackupLoopAsync(ct As CancellationToken) As Task
        If _options.Retention.BackupIntervalHours <= 0 Then Return
        Dim backups As New BackupService(_h.Database, Path.Combine(_options.Host.DataDirectory, "backups"), _options.Retention.BackupCount, _log)
        While Not ct.IsCancellationRequested
            Try
                Dim lastText = Await _h.AgentsRepo.GetStateAsync("LastBackupUtc").ConfigureAwait(False)
                Dim due = String.IsNullOrEmpty(lastText) OrElse _clock.UtcNow - Db.ParseTs(lastText) >= TimeSpan.FromHours(_options.Retention.BackupIntervalHours)
                If due Then
                    Await backups.BackupAsync().ConfigureAwait(False)
                    Await _h.AgentsRepo.SetStateAsync("LastBackupUtc", Db.Ts(_clock.UtcNow)).ConfigureAwait(False)
                End If
            Catch ex As Exception
                _log.Warn("Backup", "Backup failed: " & ex.Message)
            End Try
            Try
                Await Task.Delay(TimeSpan.FromMinutes(30), ct).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                Exit While
            End Try
        End While
    End Function

    ' ---------------------------------------------------------------------------------------------
    ' Console banner
    ' ---------------------------------------------------------------------------------------------

    Public Sub PrintBanner()
        Dim h = _h
        Dim counts = h.Agents.CountByState()
        Dim running = counts.Where(Function(kv) kv.Key <> "Stopped" AndAlso kv.Key <> "Disabled" AndAlso kv.Key <> "Unhealthy" AndAlso kv.Key <> "Error").Sum(Function(kv) kv.Value)
        Dim mcp = h.Mcp.GetStatus()
        Dim pc = h.LastProviderCheck
        Dim lines As New List(Of String) From {
            "", "Agentic", $"Version: {Version}", "",
            "Admin UI:", "    " & If(_http Is Nothing, "(disabled)", _http.DisplayUrl), "",
            "Agents:", $"    {h.Agents.AgentCount} configured", $"    {running} running", $"    {h.Agents.AgentCount - running} stopped", "",
            "MCP:", $"    {mcp.Count} configured", $"    {mcp.Where(Function(m) m.State = McpConnectionState.Connected).Count()} connected", "",
            "AI:"}
        For Each kv In _options.AIProviders
            lines.Add($"    {kv.Key} " & If(pc IsNot Nothing AndAlso pc.Reachable, "connected", "NOT reachable" & If(pc?.ErrorMessage Is Nothing, "", $" ({TextUtil.Truncate(pc.ErrorMessage, 80)})")))
        Next
        If pc IsNot Nothing AndAlso pc.MissingModels.Count > 0 Then lines.Add("    Missing models: " & String.Join(", ", pc.MissingModels))
        lines.AddRange({"", "Communication:", If(_options.Communication.Enabled, "    Enabled", "    Disabled"),
                        $"    Max delegation depth: {_options.Communication.MaxDelegationDepth}", "", "Press Ctrl+C to shut down gracefully.", ""})
        Try
            For Each l In lines
                Console.WriteLine(l)
            Next
        Catch
        End Try
    End Sub

    ' ---------------------------------------------------------------------------------------------
    ' Graceful shutdown
    ' ---------------------------------------------------------------------------------------------

    Public Async Function StopAsync() As Task
        If Interlocked.Exchange(_stopped, 1) = 1 Then Return
        Dim h = _h
        If h Is Nothing Then Return
        Dim timeout = TimeSpan.FromSeconds(Math.Max(5, _options.Host.ShutdownTimeoutSeconds))
        _log.Info("Host", "Graceful shutdown started")
        h.Events.Emit(AgentEventType.HostLifecycle, "Agentic shutting down")
        ' 1. Stop accepting new HTTP commands.
        _http?.StopAcceptingCommands()
        ' 2. Stop scheduling new work.
        h.Scheduler?.StopScheduling()
        h.Goals?.Stop()
        h.Autonomy?.Stop()
        h.Triggers?.StopWatchers()
        ' 3. Stop new agent delegations/requests (replies still flow).
        h.Comms?.StopAcceptingNewWork()
        ' 4-6. Signal agents; let current operations finish within the timeout, then cancel the rest.
        Await h.Agents.StopAllAsync(timeout).ConfigureAwait(False)
        _cts?.Cancel()
        Try
            Await Task.WhenAll(_background).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(False)
        Catch
        End Try
        ' 7. Close MCP connections.
        Await h.Mcp.StopAsync().ConfigureAwait(False)
        ' 8. Flush events, statistics and logs.
        h.Events.Complete(TimeSpan.FromSeconds(3))
        Await h.Activity.DrainAsync().ConfigureAwait(False)
        Await h.Statistics.FlushAsync().ConfigureAwait(False)
        ' 9. Stop HTTP server.
        If _http IsNot Nothing Then Await _http.StopAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(False)
        ' 10. Close database.
        h.Database.Dispose()
        For Each p In h.Providers.Values.OfType(Of IDisposable)()
            p.Dispose()
        Next
        _log.Info("Host", "Shutdown complete")
    End Function

    ''' <summary>
    ''' The database is data\agentic.db. A data folder from before the rename holds agenthost.db; move it
    ''' (and its WAL/SHM side files) across once so existing agents, wallets and history carry over.
    ''' </summary>
    Friend Shared Function ResolveDatabasePath(dataDirectory As String, log As StructuredLogger) As String
        Dim current = Path.Combine(dataDirectory, "agentic.db")
        Dim legacy = Path.Combine(dataDirectory, "agenthost.db")
        If File.Exists(current) OrElse Not File.Exists(legacy) Then Return current
        Try
            File.Move(legacy, current)
            For Each suffix In {"-wal", "-shm"}
                If File.Exists(legacy & suffix) Then File.Move(legacy & suffix, current & suffix, overwrite:=True)
            Next
            log?.Info("Database", $"Renamed legacy database {legacy} to {current}.")
            Return current
        Catch ex As IOException
            ' Another process still has the old file open; keep using it rather than starting empty.
            log?.Warn("Database", $"Could not rename {legacy} ({ex.Message}); using it in place.")
            Return legacy
        End Try
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        Try
            StopAsync().Wait(TimeSpan.FromSeconds(60))
        Catch
        End Try
        _cts?.Dispose()
    End Sub
End Class
