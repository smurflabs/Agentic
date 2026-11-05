Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Mcp
Imports Agentic.Memory
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities
Imports Agentic.Work

''' <summary>
''' Shared host services (composition root output). Used by host infrastructure such as the admin API.
''' Never passed to agents or tools — agents only ever receive their own AgentScope.
''' </summary>
Public NotInheritable Class HostServices
    Public Property Options As HostOptions
    Public Property ConfigPath As String
    Public Property Version As String
    Public Property StartedUtc As DateTime
    Public Property InstanceId As String
    Public Property Log As ILog
    Public Property Redactor As SecretRedactor
    Public Property Clock As IClock
    Public Property Database As Database
    Public Property Events As AgentEventBus
    Public Property Tasks As TaskRepository
    Public Property Messages As MessageRepository
    Public Property Telemetry As TelemetryRepository
    Public Property AgentsRepo As AgentRepository
    Public Property MemoryStore As IMemoryStore
    Public Property Wallets As WalletService
    Public Property Pricing As PricingService
    Public Property Accounts As AccountService
    Public Property Deposits As DepositService
    Public Property Providers As Dictionary(Of String, IAIProvider)
    Public Property ModelRegistry As ModelRegistry
    Public Property ContextLimits As AI.ContextLimits
    Public Property Selection As ModelSelectionService
    Public Property ModelAccess As ModelAccessService
    Public Property SpendCaps As SpendCapTracker
    Public Property Gateway As ModelGateway
    Public Property Vision As VisionService
    Public Property Images As ImageGenerationService
    ''' <summary>The images API (ImageGeneration): every picture comes from its models.</summary>
    Public Property ImageRouter As ImageRouter
    ''' <summary>The video API (VideoGeneration): every video comes from its models.</summary>
    Public Property Videos As VideoService
    Public Property Artifacts As ArtifactStore
    Public Property Approvals As ApprovalService
    Public Property Comms As AgentCommunicationService
    Public Property Profiles As ProfileRegistry
    Public Property Mcp As McpServerManager
    Public Property McpTools As McpToolRegistry
    ''' <summary>MCP servers added from the web app (administrators' shared ones, users' own).</summary>
    Public Property McpUser As Mcp.McpUserServers
    Public Property ToolCatalog As ToolCatalog
    Public Property Resolver As WaitResolver
    Public Property Agents As AgentManager
    Public Property Scheduler As WorkScheduler
    Public Property Statistics As StatisticsAggregator
    Public Property Activity As ActivityPersister
    Public Property Alerts As AlertService
    Public Property LastProviderCheck As ProviderCheckResult
    Public Property WebSearch As Global.Agentic.Tools.WebSearchService
    Public Property GlobalTasks As Global.Agentic.Work.GlobalTaskService
    Public Property Lessons As Global.Agentic.Learning.LessonStore
    Public Property Learning As Global.Agentic.Learning.LearningService
    Public Property Uploads As UploadStore
    Public Property Attachments As AttachmentService
    Public Property Results As ResultService
    Public Property Outcomes As OutcomeService
    Public Property ModelHub As ModelHub
    ''' <summary>My models: models users added with their own API keys.</summary>
    Public Property Byo As ByoService
    Public Property Chat As ChatService
    ''' <summary>Agents' work posted back to the chat it came from.</summary>
    Public Property ChatReports As ChatReportService
    ''' <summary>Files in the code sandbox (list, download) and stopping long-lived sandbox processes/containers.</summary>
    Public Property Sandbox As Work.SandboxGateway
    Public Property SandboxResources As Work.SandboxResourceStore
    Public Property SandboxReaper As Work.SandboxReaper
    Public Property SandboxActivity As Work.SandboxActivity
    ''' <summary>How each model has been doing lately (the green, yellow or red dot in the model picker).</summary>
    Public Property ModelHealth As Global.Agentic.AI.ModelHealth
    ''' <summary>App previews: links to web apps running in sandbox containers, served at /container/{id}/.</summary>
    Public Property SandboxPreviews As Work.SandboxPreviewStore
    Public Property PreviewProxy As Http.PreviewProxy
    Public Property Rag As Work.RagService
    Public Property Updates As UpdateService
    Public Property Triggers As Work.TriggerService
    Public Property Goals As Work.GoalService
    Public Property Skills As Work.SkillStore
    Public Property Proposals As Work.ProfileProposalService
    Public Property Notifications As Global.Agentic.Events.NotificationService
    ''' <summary>Fully autonomous agents: approval policies, supervisors, initiative, bounties, hires.</summary>
    Public Property Autonomy As Work.AutonomyService
    ''' <summary>Secrets tools use without the model seeing them.</summary>
    Public Property Secrets As Work.SecretVault
    Public Property Evals As Work.EvalService
    Public Property Inboxes As Work.EmailInboxService
    ''' <summary>The XTTS speech server (TextToSpeech): reading answers aloud, Dictation mode, the Audio tab.</summary>
    Public Property Speech As Work.SpeechService
    ''' <summary>Users' voices, personalities, Audio tab clips and cloned voices' clips.</summary>
    Public Property Audio As Work.AudioStore
    ''' <summary>The Stable Diffusion server (StableDiffusion): the Images tab.</summary>
    Public Property Diffusion As Work.DiffusionService
    ''' <summary>The Images tab's jobs, pictures and prices.</summary>
    Public Property DiffusionStore As Work.DiffusionStore
    ''' <summary>Follows the Images tab's jobs on the server until their pictures are kept and paid for.</summary>
    Public Property DiffusionJobs As Work.DiffusionJobs
    ''' <summary>The Suno wrapper (Music): the Music tab.</summary>
    Public Property Music As Work.MusicService
    ''' <summary>The Music tab's requests, songs and prices.</summary>
    Public Property MusicStore As Work.MusicStore
    ''' <summary>Follows the Music tab's requests until their songs are kept and paid for.</summary>
    Public Property MusicJobs As Work.MusicJobs
    ''' <summary>Crypto prices and exchange rates: the Money tab and the market_prices tool.</summary>
    Public Property Markets As Work.MarketsService
    ''' <summary>Articles from the RSS feeds: the News tab and the news_articles tool.</summary>
    Public Property News As Work.NewsService
    ''' <summary>Stock and index quotes: the Stocks tab and the stock_quotes tool.</summary>
    Public Property Stocks As Work.StocksService
    ''' <summary>Prediction markets: the Polymarket tab and the polymarket tool.</summary>
    Public Property Polymarket As Work.PolymarketService
End Class

Public NotInheritable Class ProviderCheckResult
    Public Property CheckedUtc As DateTime
    Public Property Reachable As Boolean
    Public Property ErrorMessage As String
    Public Property AvailableModels As New List(Of String)
    Public Property MissingModels As New List(Of String)
End Class
