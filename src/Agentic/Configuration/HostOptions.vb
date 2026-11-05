Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Logging

Namespace Configuration

    ''' <summary>Root of appsettings.json. Every section has safe defaults.</summary>
    Public Class HostOptions
        Public Property Host As New HostSection()
        Public Property Server As New ServerOptions()
        Public Property Admin As New AdminOptions()
        Public Property AIProviders As New Dictionary(Of String, AIProviderOptions)(StringComparer.OrdinalIgnoreCase)
        Public Property Models As New List(Of ModelOptions)
        Public Property Routing As New RoutingOptions()
        Public Property Vision As New VisionOptions()
        Public Property ImageGeneration As New ImageGenerationOptions()
        Public Property VideoGeneration As New VideoGenerationOptions()
        ''' <summary>Speech from text through a self-hosted XTTS-v2 server (see TextToSpeechOptions).</summary>
        Public Property TextToSpeech As New TextToSpeechOptions()
        ''' <summary>Pictures from a self-hosted Stable Diffusion 1.5 server: the Images tab (see StableDiffusionOptions).</summary>
        Public Property StableDiffusion As New StableDiffusionOptions()
        ''' <summary>Songs from Suno through the local Suno API wrapper: the Music tab (see MusicOptions).</summary>
        Public Property Music As New MusicOptions()
        ''' <summary>Crypto prices and exchange rates: the Money tab, and the market_prices tool (see MarketsOptions).</summary>
        Public Property Markets As New MarketsOptions()
        ''' <summary>News from RSS feeds: the News tab, and the news_articles tool (see NewsOptions).</summary>
        Public Property News As New NewsOptions()
        ''' <summary>Stock and index quotes: the Stocks tab, and the stock_quotes tool (see StocksOptions).</summary>
        Public Property Stocks As New StocksOptions()
        ''' <summary>Prediction markets: the Polymarket tab, and the polymarket tool (see PolymarketOptions).</summary>
        Public Property Polymarket As New PolymarketOptions()
        Public Property Payments As New PaymentOptions()
        Public Property Rag As New RagOptions()
        Public Property Scheduling As New SchedulingOptions()
        Public Property Notifications As New NotificationOptions()
        Public Property Autonomy As New AutonomyOptions()
        Public Property Economy As New EconomyOptions()
        Public Property Subagents As New SubagentOptions()
        Public Property Learning As New LearningOptions()
        Public Property McpServers As New List(Of McpServerOptions)
        Public Property WebSearch As New WebSearchOptions()
        Public Property GlobalTasks As New GlobalTaskOptionsConfig()
        ''' <summary>Agents load MCP tool descriptions only when they need them (see ToolLoadingOptions).</summary>
        Public Property ToolLoading As New ToolLoadingOptions()
        Public Property Chat As New ChatOptions()
        Public Property Updates As New UpdateOptions()
        Public Property Communication As New CommunicationOptions()
        Public Property Limits As New LimitsOptions()
        Public Property Supervision As New SupervisionOptions()
        Public Property Retention As New RetentionOptions()
        Public Property Logging As New LoggingOptions()
        Public Property Alerts As New AlertOptions()
        Public Property BaseProfiles As New List(Of ProfileOptions)
        Public Property Agents As New List(Of ProfileOptions)
        ''' <summary>
        ''' Anthropic model ids that clients ask for -> the model here that answers them, e.g.
        ''' {"claude-sonnet-5": "sev/claude-sonnet-5", "*": "sev/claude-sonnet-5"}. Claude Code uses Anthropic's own ids
        ''' (claude-sonnet-5 for auto mode's safety classifier, claude-opus-5-5 for "opus"), which a provider lists only under
        ''' its prefix. Ids not listed here resolve on their own to a provider's copy ("prefix/claude-sonnet-5", a priced one
        ''' first, cheapest first); "*" answers Claude ids that no provider has.
        ''' </summary>
        Public Property ClaudeModelAliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
    End Class

    Public Class HostSection
        Public Property InstanceName As String = "Agentic"
        Public Property DataDirectory As String = "data"
        Public Property ShutdownTimeoutSeconds As Integer = 30
        Public Property MaxAgents As Integer = 50
    End Class

    Public Class ServerOptions
        Public Property Enabled As Boolean = True
        Public Property Host As String = "127.0.0.1"
        Public Property Port As Integer = 8080
        Public Property MaxConcurrentRequests As Integer = 64
        Public Property MaxRequestBodyBytes As Integer = 1048576
        Public Property RequestTimeoutSeconds As Integer = 60
        Public Property ExposeErrorDetails As Boolean = False
        Public Property UseHttps As Boolean = False
        ''' <summary>
        ''' The address people reach this server at (e.g. https://agentic.example.com). Share links use it, so a link made in
        ''' the desktop app or VS Code (which show the web app from 127.0.0.1) points at the server. Empty = taken from the
        ''' request (X-Forwarded-Proto/X-Forwarded-Host, then Host).
        ''' </summary>
        Public Property PublicUrl As String = ""
        Public Property SessionMinutes As Integer = 43200
        ''' <summary>Live event streams (/api/events/stream) open at once on the whole server: a safety net, not a per-user limit.</summary>
        Public Property MaxSseClients As Integer = 500
        ''' <summary>Live event streams one account may hold (one per open web, desktop or VS Code window). One more closes that
        ''' account's oldest stream instead of being refused: usually a window that was closed or put to sleep without saying so.</summary>
        Public Property MaxSseClientsPerUser As Integer = 12
        ''' <summary>
        ''' A model API call (/v1, /v2) that has not answered after this many seconds sends its headers and keeps the connection
        ''' alive (ping events, SSE comments or JSON whitespace), so a proxy that drops a silent response (Cloudflare: HTTP 524
        ''' after 100–120 s) does not cut off a slow local model. Faster calls answer as usual, errors with their real status.
        ''' </summary>
        Public Property ModelKeepAliveAfterSeconds As Double = 20
        ''' <summary>Seconds between keep-alive writes while a model call or a streamed answer is silent.</summary>
        Public Property ModelKeepAliveIntervalSeconds As Double = 10
    End Class

    Public Class AdminOptions
        Public Property Users As New List(Of AdminUserOptions)
        Public Property ApiTokens As New List(Of ApiTokenOptions)
        Public Property MaxLoginAttemptsPerMinute As Integer = 10
    End Class

    Public Class AdminUserOptions
        Public Property Username As String
        ''' <summary>PBKDF2 hash produced by <c>Agentic.exe --hash-password</c>. Plaintext is rejected.</summary>
        Public Property PasswordHash As String
    End Class

    Public Class ApiTokenOptions
        Public Property Name As String
        ''' <summary>SHA-256 hex of the token (preferred), or use TokenEnvVar to read the raw token from the environment.</summary>
        Public Property TokenSha256 As String
        Public Property TokenEnvVar As String
        ''' <summary>admin | read | submit-task</summary>
        Public Property Scopes As New List(Of String)
        ''' <summary>Empty = all agents.</summary>
        Public Property AllowedAgents As New List(Of String)
    End Class

    Public Class AIProviderOptions
        Public Property BaseUrl As String
        ''' <summary>Never put the key in the file; set AGENTIC_AIProviders__{Name}__ApiKey instead.</summary>
        Public Property ApiKey As String
        Public Property ApiKeyEnvVar As String
        Public Property TimeoutSeconds As Integer = 600
        Public Property ConnectTimeoutSeconds As Integer = 20
        Public Property UseStreaming As Boolean = True
        Public Property ChatPath As String = "/chat/completions"
        Public Property ModelsPath As String = "/models"
        Public Property ImageGenerationPath As String = "/v1/images/generations"
        ''' <summary>Picture edits (multipart); empty = ImageGenerationPath with "edits" for "generations".</summary>
        Public Property ImageEditPath As String
        Public Property MaxRetries As Integer = 2
    End Class

    Public Class PricingOptions
        ''' <summary>USD per 1M input (prompt) tokens.</summary>
        Public Property InputPerMillion As Decimal?
        ''' <summary>USD per 1M output (completion, incl. reasoning) tokens.</summary>
        Public Property OutputPerMillion As Decimal?
        ''' <summary>USD per generated image.</summary>
        Public Property PerImage As Decimal?
    End Class

    Public Class ModelOptions
        Public Property Key As String
        Public Property ModelId As String
        Public Property Provider As String = "OpenWebUI"
        Public Property Description As String
        Public Property Enabled As Boolean = True
        Public Property Tier As Integer = 1
        Public Property IsLocal As Boolean = False
        Public Property Capabilities As New List(Of ModelCapability)
        Public Property AllowedPurposes As New List(Of AiPurpose)
        Public Property Priority As Integer = 100
        Public Property MaxTokens As Integer = 4096
        Public Property Temperature As Double? = 0.2
        Public Property TimeoutSeconds As Integer = 300
        Public Property ContextWindowTokens As Integer = 128000
        Public Property MaxConcurrency As Integer = 8
        Public Property Pricing As New PricingOptions()
        Public Property Fallbacks As New List(Of String)
        Public Property CircuitBreakerFailures As Integer = 3
        Public Property CircuitBreakerSeconds As Integer = 120
        ''' <summary>Default guidance shown to granted agents about what this model is for (custom models).</summary>
        Public Property UseFor As String
        ''' <summary>Registered by an administrator at runtime (stored in the database, not appsettings.json). Usable only via explicit grants.</summary>
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property IsCustom As Boolean
        Public Property CreatedBy As String
        ''' <summary>A model a user added with their own API key (My models): only that user, and agents they own that list
        ''' it in AllowedModels, may use it, and it is free. Nothing for the server's own models.</summary>
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property ByoOwner As String
    End Class

    Public Class RoutingOptions
        ''' <summary>
        ''' Purpose name → ordered list of model keys (escalation ladder). Index 0 is the default; each later
        ''' entry is one escalation step. The "Default" ladder is used for purposes that have no entry.
        ''' Per-model Fallbacks are tried at the same ladder level before escalating.
        ''' </summary>
        Public Property Ladders As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
        Public Property Spillover As New SpilloverOptions()
        Public Property Escalation As New EscalationOptions()
        ''' <summary>
        ''' A provider whose account has run out of credit (insufficient balance/quota) is paused this long: all its models'
        ''' circuits open, and agents use other providers' models (fallbacks, other tiers, the local model) meanwhile.
        ''' </summary>
        Public Property OutOfCreditPauseMinutes As Integer = 30
    End Class

    ''' <summary>
    ''' Agents get the full descriptions of their MCP tools only when they need them. Every model call used to carry all of
    ''' them - 20-40k tokens for an agent with 8-11 MCP servers (LedgerLite) - before any work. Now a task starts with its
    ''' host tools, a core set of MCP tools (CoreTools plus the agent's most used ones) and a name list of the rest; the
    ''' agent calls load_tools to get the ones it needs, which then stay loaded for the task.
    ''' </summary>
    Public Class ToolLoadingOptions
        Public Property Enabled As Boolean = True
        ''' <summary>MCP tools always loaded ("server:tool"), when the agent has them.</summary>
        Public Property CoreTools As New List(Of String) From {
            "code-exec:run_command", "code-exec:read_file", "code-exec:write_file", "code-exec:list_files", "code-exec:get_process_status",
            "code-exec:glob_search", "code-exec:grep_search", "storage:read_file", "storage:write_file", "storage:list_files",
            "gitea:get_file_contents", "gitea:get_repository_tree", "gitea:create_or_update_file"}
        ''' <summary>Also always loaded: this many of the agent's most used MCP tools over the last RecentDays.</summary>
        Public Property RecentlyUsedTools As Integer = 8
        Public Property RecentDays As Integer = 14
        ''' <summary>Below this many characters of descriptions left to defer, every tool is loaded at once (nothing to save).</summary>
        Public Property MinDeferredChars As Integer = 6000
    End Class

    Public Class SpilloverOptions
        Public Property Enabled As Boolean = True
        ''' <summary>Model key whose queue may spill over (the local model).</summary>
        Public Property FromModel As String = "local-main"
        ''' <summary>Cloud model keys eligible for spillover; the cheapest healthy, allowed, affordable one is chosen.</summary>
        Public Property Candidates As New List(Of String)
        ''' <summary>
        ''' Other local models (keys) that take FromModel's text work while it is full, tried before any cloud model: only
        ''' when they have a free slot right now (no queueing), and usable by every agent that may use FromModel.
        ''' </summary>
        Public Property LocalCandidates As New List(Of String)
        ''' <summary>Slots each local candidate keeps free for its own work (vision, chat) when taking spillover.</summary>
        Public Property LocalKeepFreeSlots As Integer = 1
        ''' <summary>
        ''' Share agent work between FromModel and LocalCandidates all the time, not only when FromModel is full: each agent
        ''' task goes to the local model with the fewest tasks working on it (per slot) and stays there while it has a free
        ''' slot, so that server keeps the task's prompt in its cache. Without it, four agents on one 4-slot server overflow
        ''' its shared context and every call is processed from scratch (LedgerLite: 210-240 s per call, the other GPU idle).
        ''' </summary>
        Public Property LocalBalance As Boolean = True
        ''' <summary>How long a task counts as working on a local model after its last call (balancing).</summary>
        Public Property LocalBalanceWindowSeconds As Integer = 600
        Public Property LowAfterSeconds As Integer = 120
        Public Property NormalAfterSeconds As Integer = 45
        Public Property HighAfterSeconds As Integer = 15
        Public Property CriticalAfterSeconds As Integer = 5
        ''' <summary>Maximum total time a request waits for the local model before failing into retry logic.</summary>
        Public Property QueueTimeoutSeconds As Integer = 900
        ''' <summary>Waiting requests gain one priority level per this many seconds (prevents starvation).</summary>
        Public Property AgingSeconds As Integer = 120
    End Class

    Public Class EscalationOptions
        Public Property Enabled As Boolean = True
        Public Property OnModelUnavailable As Boolean = True
        Public Property OnTruncation As Boolean = True
        Public Property OnTurnLimit As Boolean = True
        Public Property OnRetry As Boolean = True
        Public Property AllowModelRequest As Boolean = True
        Public Property MaxEscalationsPerTask As Integer = 2
    End Class

    ''' <summary>
    ''' RAG (Retrieval-Augmented Generation) for the Chat tab: named knowledge bases built from uploaded files, their text
    ''' chunked and embedded, and the chunks most like the question retrieved as cited context. Needs an embedding model on
    ''' the provider (EmbeddingModel, else the first model whose id looks like an embedding model is used).
    ''' </summary>
    Public Class RagOptions
        Public Property Enabled As Boolean = True
        ''' <summary>The embedding model id on the provider (e.g. bge-m3, text-embedding-3-small). Empty = auto-detect.</summary>
        Public Property EmbeddingModel As String
        ''' <summary>Characters per chunk, and how much neighbouring chunks overlap (keeps sentences whole across the cut).</summary>
        Public Property ChunkChars As Integer = 1800
        Public Property ChunkOverlap As Integer = 250
        ''' <summary>How many chunks to give the model per message, and the smallest cosine score worth including (0..1).</summary>
        Public Property TopK As Integer = 6
        Public Property MinScore As Double = 0.15
        ''' <summary>The retrieved context never exceeds this many characters.</summary>
        Public Property MaxContextChars As Integer = 12000
        ''' <summary>How many texts are embedded in one request to the provider.</summary>
        Public Property EmbedBatch As Integer = 32
        ''' <summary>A base holds at most this many chunks (its whole vector set is kept in memory for search).</summary>
        Public Property MaxChunksPerBase As Integer = 60000
        ''' <summary>Largest file text ingested into a base, in characters.</summary>
        Public Property MaxDocChars As Integer = 4000000
        Public Property TimeoutSeconds As Integer = 120
    End Class

    ''' <summary>Queue scheduling: an off-peak window for low-urgency ("OffPeak") tasks and the deadline window that overrides it.</summary>
    Public Class SchedulingOptions
        Public Property Enabled As Boolean = True
        ''' <summary>Off-peak window start and end hour (UTC, 0-23). When start = end there is no window and OffPeak tasks always run.</summary>
        Public Property OffPeakStartHourUtc As Integer = 0
        Public Property OffPeakEndHourUtc As Integer = 6
        ''' <summary>A task whose deadline is within this many minutes jumps the queue and runs even outside the off-peak window.</summary>
        Public Property DeadlineWindowMinutes As Integer = 30

        ''' <summary>True if OffPeak tasks are allowed to run at <paramref name="nowUtc"/> (inside the window, or when disabled / no window).</summary>
        Public Function IsOffPeakNow(nowUtc As DateTime) As Boolean
            If Not Enabled OrElse OffPeakStartHourUtc = OffPeakEndHourUtc Then Return True
            Dim h = nowUtc.Hour
            If OffPeakStartHourUtc < OffPeakEndHourUtc Then Return h >= OffPeakStartHourUtc AndAlso h < OffPeakEndHourUtc
            Return h >= OffPeakStartHourUtc OrElse h < OffPeakEndHourUtc   ' window wraps midnight
        End Function
    End Class

    ''' <summary>How agents reach a human asynchronously (email and/or incoming-webhook channels such as Slack/Discord/Teams).</summary>
    Public Class NotificationOptions
        Public Property Enabled As Boolean = False
        Public Property Email As New EmailOptions()
        ''' <summary>Named webhook channels. An agent's notify_human picks one by name (the first is the default).</summary>
        Public Property Channels As New List(Of NotificationChannel)
    End Class

    Public Class EmailOptions
        Public Property Enabled As Boolean = False
        Public Property SmtpHost As String
        Public Property Port As Integer = 587
        Public Property UseStartTls As Boolean = True
        Public Property FromAddress As String
        Public Property ToAddress As String
        ''' <summary>Env var names holding the SMTP username/password (never stored in config).</summary>
        Public Property UserEnvVar As String = "AGENTIC_SMTP_USER"
        Public Property PasswordEnvVar As String = "AGENTIC_SMTP_PASSWORD"
    End Class

    Public Class NotificationChannel
        Public Property Name As String
        ''' <summary>webhook | email</summary>
        Public Property Type As String = "webhook"
        ''' <summary>Incoming-webhook URL for type=webhook (Slack/Discord/Teams compatible: posts {"text": ...}).</summary>
        Public Property Url As String
    End Class

    ''' <summary>Switches for the autonomy features (goals review loop, self-critique verification, self-modification proposals).</summary>
    Public Class AutonomyOptions
        ''' <summary>Run the standing-goal review loop (injects a review task on each goal's cadence).</summary>
        Public Property GoalsEnabled As Boolean = True
        ''' <summary>How often the goal loop wakes to look for due reviews.</summary>
        Public Property GoalSweepSeconds As Integer = 120
        ''' <summary>Default self-critique passes before an agent's successful complete_task is accepted (0 = off; profile can override).</summary>
        Public Property VerifyPasses As Integer = 0
        ''' <summary>Allow agents to propose changes to their own behaviour (applied only after an administrator approves).</summary>
        Public Property AllowSelfModification As Boolean = True
        ''' <summary>Largest webhook/file-watch payload turned into a task, in characters.</summary>
        Public Property MaxTriggerPayloadChars As Integer = 8000

        ' ---- Autonomy levels: each agent's Autonomy.Level (supervised | assisted | autonomous) ----

        ''' <summary>Apply agents' approval policies (decide within limits, send to a supervisor agent). Off: every request waits for a person.</summary>
        Public Property PoliciesEnabled As Boolean = True
        ''' <summary>How often the autonomy loop runs (supervisor time-outs, unanswered questions, initiative, evaluations, mailboxes).</summary>
        Public Property SweepSeconds As Integer = 60
        ''' <summary>A supervisor agent that has not decided within this many minutes hands the request to a person.</summary>
        Public Property SupervisorTimeoutMinutes As Integer = 60
        ''' <summary>Budget requests up to this many USD are approved without a person (assisted agents; an agent can set its own).</summary>
        Public Property AssistedBudgetUsd As Decimal = 2D
        ''' <summary>The same for autonomous agents.</summary>
        Public Property AutonomousBudgetUsd As Decimal = 10D
        ''' <summary>At most this much budget per agent per day is approved without a person.</summary>
        Public Property AutoBudgetPerDayUsd As Decimal = 25D
        ''' <summary>An autonomous agent's question nobody answered for this long goes to its supervisor, or the agent decides itself.</summary>
        Public Property AskHumanTimeoutMinutes As Integer = 240
        ''' <summary>Assisted and autonomous agents that have been idle this long get an initiative task (look for the most useful work).</summary>
        Public Property InitiativeEnabled As Boolean = True
        Public Property InitiativeMinutes As Integer = 180
        Public Property MaxInitiativesPerDay As Integer = 6
        ''' <summary>Limits on what an agent sets up for itself (schedules, goals, webhooks).</summary>
        Public Property MaxSelfSchedules As Integer = 10
        Public Property MaxSelfGoals As Integer = 10
        Public Property MaxSelfWebhooks As Integer = 5
        ''' <summary>
        ''' Bounties: a task can carry a reward that is paid when its work is accepted (approved by its reviewer, or finished
        ''' without a review) - from the agent that offered it, or credited as profit for a person's task.
        ''' </summary>
        Public Property BountiesEnabled As Boolean = True
        Public Property MaxBountyUsd As Decimal = 25D
        ''' <summary>Agents allowed to hire (Autonomy.CanHire) may add helper agents; the hirer's limits and tools bound the hire.</summary>
        Public Property HiringEnabled As Boolean = True
        Public Property MaxHiresPerAgent As Integer = 3
        ''' <summary>Evaluation suites: after a behaviour change, the agent re-runs its test cases; a change that lowers the score is rolled back.</summary>
        Public Property EvalsEnabled As Boolean = True
        Public Property EvalAfterBehaviorChange As Boolean = True
        Public Property EvalTimeoutMinutes As Integer = 90
        ''' <summary>A drop of the average score (out of 10) by more than this rolls the change back.</summary>
        Public Property EvalRollbackMargin As Double = 0.5
        ''' <summary>Learning from failure: this many failed tasks in a day starts a case study of the agent (0 = off).</summary>
        Public Property FailureStudyThreshold As Integer = 3
        ''' <summary>Read-only tool calls of one model reply run at the same time (up to this many).</summary>
        Public Property ParallelTools As Boolean = True
        Public Property MaxParallelTools As Integer = 4
        ''' <summary>http_request: time limit and how much of a response the agent sees.</summary>
        Public Property HttpTimeoutSeconds As Integer = 30
        Public Property MaxHttpResponseChars As Integer = 20000
        ''' <summary>Email inboxes: new mail becomes a task (IMAP).</summary>
        Public Property EmailEnabled As Boolean = True
        Public Property MaxEmailChars As Integer = 12000
    End Class

    Public Class VisionOptions
        Public Property Enabled As Boolean = True
        Public Property ModelKeys As New List(Of String)
        Public Property ExpertModelKeys As New List(Of String)
        Public Property MaxImageBytes As Integer = 8 * 1024 * 1024
        Public Property MaxTokens As Integer = 1500
    End Class

    Public Class ImageGenerationOptions
        Public Property Enabled As Boolean = True
        Public Property Provider As String = "OpenWebUI"
        ''' <summary>Informational: the engine/model configured inside OpenWebUI (Admin → Settings → Images).</summary>
        Public Property Model As String = "gpt-image-2"
        Public Property DefaultSize As String = "512x512"
        Public Property AllowedSizes As New List(Of String)
        Public Property TimeoutSeconds As Integer = 240
        Public Property PricePerImage As Decimal?
        Public Property MaxConcurrency As Integer = 2
        Public Property MaxPerAgentPerDay As Integer = 25
        ''' <summary>
        ''' The images API all pictures come from (OpenAI-compatible: POST {BaseUrl}/images/generations), e.g.
        ''' https://yuanqiaoyun.com/v1. No other source is ever used: without it (and its key) image generation is off.
        ''' </summary>
        Public Property BaseUrl As String
        ''' <summary>The images API's key. Better kept out of files: AGENTIC_ImageGeneration__ApiKey.</summary>
        Public Property ApiKey As String
        ''' <summary>When Model cannot make an image, these models of the same API are asked next, in order.</summary>
        Public Property FallbackModels As New List(Of String)
        ''' <summary>Also ask the API's other image models (from its model list).</summary>
        Public Property AutoFallback As Boolean = True
        ''' <summary>A model that failed is skipped for this long (unless nothing else is left).</summary>
        Public Property FailedModelPauseSeconds As Integer = 180
    End Class

    ''' <summary>
    ''' Deposits (Balance → Deposit): an account pays a BTCPay Server invoice and, once the payment server confirms it
    ''' (POST /api/payments/deposit with the X-Payment-Key header), the amount is added to the account's balance.
    ''' </summary>
    Public Class PaymentOptions
        Public Property Enabled As Boolean = True
        ''' <summary>The BTCPay Server (Greenfield API at {BtcPayUrl}/api/v1, checkout widget at {BtcPayUrl}/modal/btcpay.js).</summary>
        Public Property BtcPayUrl As String = ""
        ''' <summary>The BTCPay store's id (Stores → Settings). Deposits stay off until BtcPayUrl, StoreId and ApiKey are all set.</summary>
        Public Property StoreId As String = ""
        ''' <summary>A Greenfield API key with permission to create invoices. Better kept out of files: AGENTIC_Payments__ApiKey.</summary>
        Public Property ApiKey As String = ""
        Public Property Currency As String = "USD"
        ''' <summary>Whole amounts (in Currency) an account may deposit at once.</summary>
        Public Property MinDeposit As Integer = 1
        Public Property MaxDeposit As Integer = 500
        ''' <summary>Where BTCPay posts the invoice's webhook (the payment server, which then calls /api/payments/deposit). Empty = the store's own webhook.</summary>
        Public Property NotificationUrl As String = ""
        ''' <summary>Where the checkout sends the payer afterwards. Empty = none (the widget simply closes).</summary>
        Public Property RedirectUrl As String = ""
        ''' <summary>The key the payment server sends (header X-Payment-Key) to credit a paid deposit.</summary>
        Public Property DepositApiKey As String = DefaultDepositApiKey
        Public Const DefaultDepositApiKey As String = "57863927-c36c-41a3-b393-184369e96f83"

        Public ReadOnly Property Configured As Boolean
            Get
                Return Enabled AndAlso Not String.IsNullOrWhiteSpace(BtcPayUrl) AndAlso Not String.IsNullOrWhiteSpace(StoreId) AndAlso Not String.IsNullOrWhiteSpace(ApiKey)
            End Get
        End Property
    End Class

    ''' <summary>Video generation (chat's generate_video tool): one video API, such as Seedance through yuanqiaoyun.</summary>
    Public Class VideoGenerationOptions
        Public Property Enabled As Boolean = True
        ''' <summary>The video API (new-api tasks: POST {BaseUrl}/video/generations, then GET …/{task_id}). Without it videos are off.</summary>
        Public Property BaseUrl As String
        ''' <summary>The video API's key. Better kept out of files: AGENTIC_VideoGeneration__ApiKey.</summary>
        Public Property ApiKey As String
        Public Property Model As String = "seedance-2.0-fast"
        ''' <summary>Asked next when Model cannot make the video.</summary>
        Public Property FallbackModels As New List(Of String)
        Public Property DefaultSeconds As Integer = 5
        Public Property MaxSeconds As Integer = 10
        ''' <summary>480p | 720p | 1080p (the price grows with it).</summary>
        Public Property Resolution As String = "720p"
        Public Property DefaultRatio As String = "16:9"
        ''' <summary>How long one video may take, queue included.</summary>
        Public Property TimeoutSeconds As Integer = 900
        Public Property PollSeconds As Integer = 5
        Public Property MaxConcurrency As Integer = 2
        Public Property MaxVideoMb As Integer = 200
        ''' <summary>What one second of video costs a User account (USD); ModelPricePerSecond overrides it per model.</summary>
        Public Property PricePerSecond As Decimal?
        Public Property ModelPricePerSecond As New Dictionary(Of String, Decimal)(StringComparer.OrdinalIgnoreCase)
        Public Property FailedModelPauseSeconds As Integer = 180
    End Class

    ''' <summary>
    ''' Speech from text through a self-hosted XTTS-v2 server (POST /tts, /tts/stream, cloned voices). The Chat tab reads answers
    ''' aloud with it, Dictation mode speaks a short summary of each answer, and the Audio tab uses all of its options. Without
    ''' BaseUrl the browser's own voices read answers and the Audio tab is off.
    ''' </summary>
    Public Class TextToSpeechOptions
        Public Property Enabled As Boolean = True
        ''' <summary>The XTTS server, e.g. http://192.168.1.20:8020.</summary>
        Public Property BaseUrl As String
        ''' <summary>Its X-API-Key. Better kept out of files: the vault entry ApiKeySecret (the Audio tab's Settings set it) or AGENTIC_TextToSpeech__ApiKey.</summary>
        Public Property ApiKey As String
        ''' <summary>The vault entry with the key, used when ApiKey is empty. No agent may use it.</summary>
        Public Property ApiKeySecret As String = "xtts-api-key"
        Public Property DefaultSpeaker As String = "Ana Florence"
        Public Property DefaultLanguage As String = "en"
        ''' <summary>Requests the server works on at once (it makes one at a time); the rest wait in line here, first come first served.</summary>
        Public Property MaxConcurrency As Integer = 1
        ''' <summary>How long one request may take (speech is made at about 1.3 s per second of audio).</summary>
        Public Property TimeoutSeconds As Integer = 1800
        Public Property MaxTextChars As Integer = 20000
        ''' <summary>The largest clip accepted for cloning a voice.</summary>
        Public Property MaxCloneClipMb As Integer = 25
        ''' <summary>Clips the Audio tab keeps per user (the oldest go first).</summary>
        Public Property MaxClipsPerUser As Integer = 200
        ''' <summary>The model that writes Dictation mode's spoken summary when the voice's personality names none: empty = the model that wrote the answer.</summary>
        Public Property SummaryModel As String
        ''' <summary>About how many words the spoken summary has (each user can change it).</summary>
        Public Property SummaryWords As Integer = 60
        ''' <summary>
        ''' How long the models together may take to write a spoken summary; then the answer's first sentences are spoken
        ''' instead. Keep it well under a proxy's limit (Cloudflare cuts a request off at 100 s).
        ''' </summary>
        Public Property SummaryTimeoutSeconds As Integer = 40
        ''' <summary>
        ''' What speech costs a User account (USD), like tokens in and out: per 1M characters of text spoken, per minute of audio
        ''' made, and per voice upload (cloning or renewing a voice). Nothing = free. Administrators change them on the Audio tab
        ''' or Finance (kept in AudioPricing); these are the starting prices.
        ''' </summary>
        Public Property PricePerMillionChars As Decimal?
        Public Property PricePerMinute As Decimal?
        Public Property PricePerVoiceUpload As Decimal?
        ''' <summary>The personality of a voice that has none of its own: how its spoken summaries sound.</summary>
        Public Property DefaultSummaryPrompt As String = "You are the voice of the user's assistant, giving them a quick spoken recap of its answer. Sound like a person talking: clear, natural and to the point."
    End Class

    ''' <summary>
    ''' Pictures from a self-hosted Stable Diffusion 1.5 server (FastAPI: /jobs, /preprocess, /options, /health): the Images tab
    ''' uses all of it - txt2img, img2img, inpainting, LoRAs, ControlNets, hires fix. Without BaseUrl the tab is off.
    ''' </summary>
    Public Class StableDiffusionOptions
        Public Property Enabled As Boolean = True
        ''' <summary>The server, e.g. http://192.168.1.30:8000.</summary>
        Public Property BaseUrl As String
        ''' <summary>Its API key (X-API-Key). Better kept out of files: the vault entry ApiKeySecret (the Images tab's Settings set it) or AGENTIC_StableDiffusion__ApiKey.</summary>
        Public Property ApiKey As String
        ''' <summary>The vault entry with the key, used when ApiKey is empty. No agent may use it.</summary>
        Public Property ApiKeySecret As String = "sd-api-key"
        ''' <summary>How often a running job is asked for its progress.</summary>
        Public Property PollMilliseconds As Integer = 1000
        ''' <summary>
        ''' Requests the server works on at once (it makes one at a time): jobs and preprocessor runs beyond that wait in line
        ''' here, first come first served, and are sent when the one before them ends.
        ''' </summary>
        Public Property MaxConcurrency As Integer = 1
        ''' <summary>The longest a job may take once it is sent to the server (it is cancelled then); waiting in line does not count.</summary>
        Public Property JobTimeoutSeconds As Integer = 3600
        ''' <summary>Jobs one account may have queued or running at once.</summary>
        Public Property MaxActiveJobsPerUser As Integer = 3
        ''' <summary>Pictures the gallery keeps per user (the oldest unstarred go first).</summary>
        Public Property MaxImagesPerUser As Integer = 500
        ''' <summary>The largest picture accepted as an input (init image, mask, control image).</summary>
        Public Property MaxInputImageMb As Integer = 20
        ''' <summary>
        ''' What pictures cost a User account (USD): per picture made, and per minute the GPU worked on the job (its elapsed
        ''' time). Nothing = free. Administrators change them on the Images tab or Finance (kept in SdPricing).
        ''' </summary>
        Public Property PricePerImage As Decimal?
        Public Property PricePerGpuMinute As Decimal?
    End Class

    ''' <summary>
    ''' Songs from Suno through the Suno API wrapper (C:\Users\Administrator\Desktop\suno\server.py, the SunoAPI scheduled task):
    ''' the Music tab - simple (a description) or custom (lyrics, style, title) songs, instrumentals, AI-written lyrics. Every
    ''' song uses the Suno account the wrapper is signed in to (its credits). Without BaseUrl the tab is off.
    ''' </summary>
    ''' <summary>
    ''' The Money tab: crypto prices from CoinMarketCap's public simple-price API (no key) about every CryptoPollSeconds, and
    ''' exchange rates from Frankfurter (the ECB's daily rates, no key) every FxPollMinutes, kept for the charts. The watch
    ''' lists start from CryptoSymbols and FxQuotes; administrators change them on the Money tab.
    ''' </summary>
    Public Class MarketsOptions
        Public Property Enabled As Boolean = True
        Public Property CryptoUrl As String = "https://pro-api.coinmarketcap.com/public-api/v2/simple/price"
        ''' <summary>
        ''' A CoinMarketCap API key (free plan: sign in at coinmarketcap.com), sent as X-CMC_PRO_API_KEY. Without one CoinMarketCap
        ''' allows only a few anonymous calls (then 429, error 1022), and prices come from CryptoFallbackUrl meanwhile.
        ''' </summary>
        Public Property CryptoApiKey As String
        ''' <summary>Used while CoinMarketCap refuses (rate limit, error): Coinbase's public exchange rates, no key.</summary>
        Public Property CryptoFallbackUrl As String = "https://api.coinbase.com/v2/exchange-rates?currency=USD"
        ''' <summary>Empty: BTC, ETH, LTC, XRP, USDT.</summary>
        Public Property CryptoSymbols As New List(Of String)
        Public Property CryptoPollSeconds As Integer = 60
        Public Property FxUrl As String = "https://api.frankfurter.dev/v2/rates"
        Public Property FxBase As String = "USD"
        ''' <summary>Empty: EUR, GBP, AUD, CNY.</summary>
        Public Property FxQuotes As New List(Of String)
        Public Property FxPollMinutes As Integer = 60
        ''' <summary>Days of exchange-rate history fetched when a currency is first watched.</summary>
        Public Property FxHistoryDays As Integer = 90
        ''' <summary>Every price is kept this long; older ones are thinned to one an hour.</summary>
        Public Property KeepAllDays As Integer = 14
        ''' <summary>Prices are deleted after this long (0 = kept).</summary>
        Public Property KeepDays As Integer = 365
        Public Property TimeoutSeconds As Integer = 20

        Public Shared ReadOnly DefaultCrypto As String() = {"BTC", "ETH", "LTC", "XRP", "USDT"}
        Public Shared ReadOnly DefaultFx As String() = {"EUR", "GBP", "AUD", "CNY"}
    End Class

    ''' <summary>
    ''' The Stocks tab: quotes for the watched tickers from Yahoo Finance's chart API (no key) every PollSeconds while a market
    ''' is open (ClosedPollMinutes otherwise), charts of any range from Yahoo, and the ticker list from NASDAQ Trader's symbol
    ''' directory (once a day). Symbols: the defaults every account watches until it saves its own list; empty: the S&P 500,
    ''' Nasdaq and Dow indexes and seven large companies.
    ''' </summary>
    Public Class StocksOptions
        Public Property Enabled As Boolean = True
        Public Property ChartUrl As String = "https://query1.finance.yahoo.com/v8/finance/chart"
        Public Property NasdaqListedUrl As String = "https://www.nasdaqtrader.com/dynamic/SymDir/nasdaqlisted.txt"
        Public Property OtherListedUrl As String = "https://www.nasdaqtrader.com/dynamic/SymDir/otherlisted.txt"
        Public Property Symbols As New List(Of String)
        Public Property PollSeconds As Integer = 60
        Public Property ClosedPollMinutes As Integer = 15
        Public Property TimeoutSeconds As Integer = 20

        Public Shared ReadOnly DefaultSymbols As String() = {"^GSPC", "^IXIC", "^DJI", "AAPL", "MSFT", "NVDA", "AMZN", "GOOGL", "META", "TSLA"}
    End Class

    ''' <summary>
    ''' The Polymarket tab: prediction markets from Polymarket's public APIs (no key). Gamma (GammaUrl) lists and searches the
    ''' events and their markets; the CLOB (ClobUrl) has prices, order books and price history; one WebSocket (WebSocketUrl)
    ''' carries live prices, books and trades for the busiest markets and the ones open on someone's page; the Data API
    ''' (DataUrl) has trades, holders, wallets and the leaderboard.
    ''' </summary>
    Public Class PolymarketOptions
        Public Property Enabled As Boolean = True
        Public Property GammaUrl As String = "https://gamma-api.polymarket.com"
        Public Property ClobUrl As String = "https://clob.polymarket.com"
        Public Property DataUrl As String = "https://data-api.polymarket.com"
        Public Property WebSocketUrl As String = "wss://ws-subscriptions-clob.polymarket.com/ws/market"
        ''' <summary>The site, for each market's link.</summary>
        Public Property SiteUrl As String = "https://polymarket.com"
        Public Property TimeoutSeconds As Integer = 20
        ''' <summary>Most outcome tokens followed live at once.</summary>
        Public Property MaxLiveTokens As Integer = 1500
        ''' <summary>How many of the busiest open markets are always followed live, so the tab opens on live prices.</summary>
        Public Property LiveTopMarkets As Integer = 60
    End Class

    ''' <summary>
    ''' The News tab: articles from RSS and Atom feeds, read every PollMinutes and kept KeepDays. Feeds empty: the built-in list
    ''' (international, US, UK, Europe, Australia, agencies through Google News, investigative, business).
    ''' </summary>
    Public Class NewsOptions
        Public Property Enabled As Boolean = True
        Public Property PollMinutes As Integer = 10
        Public Property KeepDays As Integer = 30
        Public Property TimeoutSeconds As Integer = 20
        ''' <summary>Feeds read at once.</summary>
        Public Property Concurrency As Integer = 6
        Public Property Feeds As New List(Of NewsFeedOptions)
    End Class

    Public Class NewsFeedOptions
        Public Property Name As String
        Public Property Url As String
        ''' <summary>international | us | uk | europe | australia | agencies | investigative | business (or any word: a tab of its own).</summary>
        Public Property Category As String
        Public Property Enabled As Boolean = True
    End Class

    Public Class MusicOptions
        Public Property Enabled As Boolean = True
        ''' <summary>The wrapper, e.g. http://127.0.0.1:8000.</summary>
        Public Property BaseUrl As String
        ''' <summary>The wrapper's key (its api_key.txt). Better kept out of files: the vault entry ApiKeySecret (the Music tab's Settings set it) or AGENTIC_Music__ApiKey.</summary>
        Public Property ApiKey As String
        ''' <summary>The vault entry with the key, used when ApiKey is empty. No agent may use it.</summary>
        Public Property ApiKeySecret As String = "suno-api-key"
        ''' <summary>How often songs being made are checked.</summary>
        Public Property PollSeconds As Integer = 5
        ''' <summary>The longest a song may take (Suno usually needs one to three minutes).</summary>
        Public Property JobTimeoutSeconds As Integer = 900
        ''' <summary>Songs one account may have being made at once (each makes two).</summary>
        Public Property MaxActiveJobsPerUser As Integer = 2
        ''' <summary>Songs the library keeps per user (the oldest unstarred go first).</summary>
        Public Property MaxSongsPerUser As Integer = 300
        Public Property DefaultModel As String = "chirp-goose"
        ''' <summary>The Suno models offered (id → name); any other id can be typed.</summary>
        Public Property Models As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {{"chirp-goose", "v6 mini"}}
        ''' <summary>
        ''' What music costs a User account (USD): per song made (a request makes two), and per lyrics written by AI. Nothing =
        ''' free. Administrators change them on the Music tab or Finance (kept in MusicPricing).
        ''' </summary>
        Public Property PricePerSong As Decimal?
        Public Property PricePerLyrics As Decimal?
    End Class

    Public Class EconomyOptions
        Public Property Enabled As Boolean = True
        Public Property Currency As String = "USD"
        Public Property DefaultInitialBalance As Decimal = 5D
        ''' <summary>Hard cap on total cloud spend per UTC day across all agents (Nothing = unlimited).</summary>
        Public Property GlobalDailyCloudLimit As Decimal?
        ''' <summary>Cloud models without configured prices cannot be used (fail closed).</summary>
        Public Property RequirePricingForCloudModels As Boolean = True
        ''' <summary>If reported prompt tokens are below this fraction of the local estimate, the estimate is billed.</summary>
        Public Property UsageEstimateFloorRatio As Double = 0.25
        ''' <summary>Days of recent spend used for the burn rate and the runway forecast (balance / burn rate).</summary>
        Public Property RunwayWindowDays As Integer = 7
        ''' <summary>Agents whose balance lasts fewer days than this at their burn rate are flagged and told to economize.</summary>
        Public Property LowRunwayDays As Double = 3
        ''' <summary>
        ''' An agent whose money runs out dies: it is shut down, its running work is abandoned and it stays dead until an
        ''' administrator credits its wallet (which revives it). Agents that never had money are exempt.
        ''' </summary>
        Public Property BankruptcyEndsAgent As Boolean = True
        ''' <summary>"Out of money" means a balance below this (USD).</summary>
        Public Property BankruptcyThreshold As Decimal = 0.01D
    End Class

    ''' <summary>
    ''' Temporary subagents that agents can spawn for focused subtasks. Subagents run on the parent's permitted models;
    ''' local-model use is free, and any cloud usage is charged to the parent's wallet (capped per subagent).
    ''' </summary>
    Public Class SubagentOptions
        Public Property Enabled As Boolean = True
        ''' <summary>Default maximum concurrently running subagents per agent (profile Subagents.MaxConcurrent overrides).</summary>
        Public Property MaxPerAgent As Integer = 3
        ''' <summary>Host-wide cap on running subagents.</summary>
        Public Property MaxTotal As Integer = 20
        ''' <summary>Maximum subagents one parent task may spawn over its lifetime.</summary>
        Public Property MaxPerTask As Integer = 8
        Public Property DefaultTimeoutMinutes As Integer = 30
        Public Property MaxTimeoutMinutes As Integer = 240
        ''' <summary>Cloud spend cap (USD, from the parent's wallet) when the parent does not specify one.</summary>
        Public Property DefaultSpendCap As Decimal = 0.5D
        ''' <summary>Largest cap a parent may give a single subagent (Nothing = limited only by the parent's balance).</summary>
        Public Property MaxSpendCap As Decimal? = 5D
        ''' <summary>Finished subagent records kept for this many days (results stay visible in the dashboard).</summary>
        Public Property RetainDays As Integer = 30
    End Class

    ''' <summary>
    ''' Organizational learning: when an agent goes broke or becomes very profitable, the host studies the decisions that
    ''' led there and publishes generalized lessons that all agents receive (relevant ones in their prompts, plus a search tool).
    ''' Analysis runs on local models only, so studying costs nothing.
    ''' </summary>
    Public Class LearningOptions
        Public Property Enabled As Boolean = True
        ''' <summary>Local model keys used for the analysis, in order of preference. Empty: rule-based analysis only.
        ''' (No default entries: the configuration binder appends to a pre-filled list instead of replacing it.)</summary>
        Public Property ModelKeys As New List(Of String)
        Public Property StudyFailures As Boolean = True
        ''' <summary>An agent counts as broke when its available balance falls below this (USD) or a paid call is refused for lack of funds.</summary>
        Public Property BrokeThreshold As Decimal = 0.05D
        Public Property StudySuccesses As Boolean = True
        ''' <summary>"Very profitable": lifetime profit / AI spend at or above this, with at least MinProfit credited.</summary>
        Public Property MinRoi As Double = 3.0
        Public Property MinProfit As Decimal = 10D
        ''' <summary>A single profit credit this large (USD) always triggers a success study.</summary>
        Public Property LargeProfit As Decimal = 50D
        Public Property FailureCooldownHours As Integer = 24
        Public Property SuccessCooldownHours As Integer = 72
        ''' <summary>How far back a study looks when there is no earlier study of the agent.</summary>
        Public Property WindowDays As Integer = 14
        ''' <summary>When true, new lessons wait for an administrator's approval before agents see them.</summary>
        Public Property RequireApproval As Boolean = False
        Public Property MaxLessonsPerStudy As Integer = 5
        ''' <summary>Relevant lessons added to each task prompt (0 disables) and their total character budget.</summary>
        Public Property PromptLessons As Integer = 4
        Public Property PromptLessonChars As Integer = 1800
        Public Property AnalysisTimeoutSeconds As Integer = 600
    End Class

    Public Class McpAuthOptions
        ''' <summary>None | Bearer | Header</summary>
        Public Property Type As String = "None"
        Public Property HeaderName As String = "Authorization"
        ''' <summary>Environment variable holding the secret (preferred; wins over Secret when set).</summary>
        Public Property SecretEnvVar As String
        ''' <summary>The secret itself, stored in the config file (like AIProviders ApiKey). Redacted from logs and the dashboard.</summary>
        Public Property Secret As String

        ''' <summary>The secret to send: the environment variable when set, otherwise the inline value.</summary>
        Public Function ResolveSecret() As String
            If Not String.IsNullOrWhiteSpace(SecretEnvVar) Then
                Dim v = Environment.GetEnvironmentVariable(SecretEnvVar)
                If Not String.IsNullOrEmpty(v) Then Return v
            End If
            Return If(String.IsNullOrWhiteSpace(Secret), Nothing, Secret)
        End Function
    End Class

    ''' <summary>
    ''' Built-in web search (search_web tool) through a SearXNG instance's JSON API. Agents only supply the query; the
    ''' endpoint is fixed here, so the tool cannot be pointed at other hosts.
    ''' </summary>
    Public Class WebSearchOptions
        ''' <summary>The search services WebSearch.Order can name.</summary>
        Public Shared ReadOnly Services As String() = {"brave", "bing", "searxng"}
        ''' <summary>The order when WebSearch.Order is empty: the Brave Search API (when it has a key), Bing, then SearXNG.</summary>
        Public Shared ReadOnly DefaultOrder As String() = {"brave", "bing", "searxng"}

        Public Property Enabled As Boolean = False
        ''' <summary>From older configurations (always SearXNG): which services are asked, and in what order, is Order.</summary>
        Public Property Provider As String = "SearXNG"
        ''' <summary>
        ''' The search services, asked in this order until one finds something: "brave" (Brave Search API, needs BraveApiKey),
        ''' "bing" (Bing's results as RSS, no key) and "searxng" (UrlTemplate, then Endpoints). A service missing what it needs is
        ''' skipped. Empty: brave, bing, searxng.
        ''' </summary>
        Public Property Order As New List(Of String)
        ''' <summary>Brave Search API subscription token (https://brave.com/search/api/); without it Brave is skipped.</summary>
        Public Property BraveApiKey As String
        ''' <summary>SearXNG search URL with a {query} (or &lt;query&gt;) placeholder, e.g. http://host:8080/search?q={query}&amp;format=json</summary>
        Public Property UrlTemplate As String
        ''' <summary>More search URL templates tried in order when the first fails or returns nothing (public SearXNG relays, like Hermes Web Search Plus). Same {query} placeholder.</summary>
        Public Property Endpoints As New List(Of String)
        Public Property MaxResults As Integer = 8
        Public Property MaxSnippetChars As Integer = 400
        Public Property TimeoutSeconds As Integer = 20
        Public Property MaxConcurrency As Integer = 4
        ''' <summary>Optional SearXNG parameters added to every search when the agent does not set them.</summary>
        Public Property DefaultLanguage As String
        Public Property SafeSearch As Integer? = 1
    End Class

    Public Class McpServerOptions
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        ''' <summary>
        ''' What agents that have this server's tools should know about it, put in their system prompt ("Your tool servers"),
        ''' e.g. that git in the code sandbox is signed in to Gitea, so they clone and push instead of copying files by hand.
        ''' </summary>
        Public Property AgentInstructions As String
        Public Property Enabled As Boolean = True
        Public Property Transport As McpTransportKind = McpTransportKind.Http
        Public Property Command As String
        Public Property Arguments As New List(Of String)
        Public Property WorkingDirectory As String
        Public Property EnvironmentVariables As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property Endpoint As String
        ''' <summary>AutoDetect | StreamableHttp | Sse</summary>
        Public Property HttpMode As String = "AutoDetect"
        Public Property Auth As New McpAuthOptions()
        Public Property TimeoutSeconds As Integer = 60
        Public Property ConnectTimeoutSeconds As Integer = 30
        Public Property MaxConcurrency As Integer = 4
        Public Property ToolRefreshMinutes As Integer = 15
        Public Property IncludeTools As New List(Of String)
        Public Property ExcludeTools As New List(Of String)
        ''' <summary>Tools that are safe to re-run after a restart (merged with the server's idempotentHint).</summary>
        Public Property IdempotentTools As New List(Of String)
        ''' <summary>Tools that require admin approval before each call.</summary>
        Public Property ApprovalRequiredTools As New List(Of String)
        ''' <summary>
        ''' Who added it at run time (the MCP page, not appsettings.json): "*" an administrator, for everyone; a user name for that
        ''' user's own server (only their chats use it, never agents). Nothing for servers from appsettings.json.
        ''' </summary>
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property Owner As String
        ''' <summary>
        ''' Let calls through that would destroy or take down the machine (rm -rf /, mkfs, shutdown, removing the sandbox's own
        ''' container …). Off: such calls to a server that runs commands are refused (Security.DestructiveCommands).
        ''' </summary>
        Public Property AllowDestructiveCommands As Boolean
        ''' <summary>HTTP headers sent with every request (a runtime server's own; kept encrypted in mcp-servers.json).</summary>
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property Headers As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>The OAuth access token to send (a server signed in to from the MCP page); Nothing when there is none.</summary>
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property TokenProvider As Func(Of Task(Of String))
        ''' <summary>A runtime server whose tools ask before each call in chat (every permission mode).</summary>
        <System.Text.Json.Serialization.JsonIgnore>
        Public Property AskFirst As Boolean

        ''' <summary>
        ''' Whether each call of a tool needs an administrator's approval: listed in ApprovalRequiredTools, or the background form
        ''' of a listed tool (run_command_background when run_command is listed), so it cannot be used to skip that approval.
        ''' </summary>
        Public Function RequiresApproval(toolName As String) As Boolean
            If String.IsNullOrEmpty(toolName) Then Return False
            If ApprovalRequiredTools.Contains(toolName, StringComparer.OrdinalIgnoreCase) Then Return True
            Const background As String = "_background"
            Return toolName.EndsWith(background, StringComparison.OrdinalIgnoreCase) AndAlso
                   ApprovalRequiredTools.Contains(toolName.Substring(0, toolName.Length - background.Length), StringComparer.OrdinalIgnoreCase)
        End Function

        ''' <summary>
        ''' Arguments added to a tool's calls when the model leaves them out, per tool name ("*" = every tool of the server), e.g.
        ''' "run_command": {"wait": 60} so a command's output comes back in the same call instead of "status: running" (models
        ''' then re-run the command or poll with a garbled process id). Only arguments the tool's schema declares are added.
        ''' </summary>
        Public Property DefaultArguments As New Dictionary(Of String, Dictionary(Of String, String))(StringComparer.OrdinalIgnoreCase)
        ''' <summary>
        ''' Environment variables merged into a tool's "env" argument, per tool name ("*" = every tool that takes env); the model's
        ''' own values win. E.g. "run_command": {"GIT_PAGER": "cat", "GIT_TERMINAL_PROMPT": "0"} so commands never wait in a pager
        ''' or at a prompt (a sandbox that runs commands in a terminal otherwise keeps them "running" forever).
        ''' </summary>
        Public Property DefaultEnv As New Dictionary(Of String, Dictionary(Of String, String))(StringComparer.OrdinalIgnoreCase)
        Public Property ReconnectBaseSeconds As Integer = 5
        Public Property ReconnectMaxSeconds As Integer = 300
        ''' <summary>Watchdog: a cheap tool call that proves the server still answers (Nothing = no watchdog).</summary>
        Public Property HealthCheck As McpHealthCheckOptions
    End Class

    ''' <summary>
    ''' MCP server watchdog. The host calls Tool (e.g. run_command "echo ok") every IntervalSeconds, and at once after a
    ''' run of call timeouts. After FailuresBeforeRestart failed checks the server is "not responding": agents' calls to it fail
    ''' at once with a clear message (instead of each waiting TimeoutSeconds), the connection is reset (a new session), and
    ''' if that does not help RestartCommand runs on the host (e.g. an ssh command that restarts the sandbox container).
    ''' It is responding again as soon as a check passes.
    ''' </summary>
    Public Class McpHealthCheckOptions
        Public Property Tool As String
        ''' <summary>The check's arguments (values given as text; numbers and true/false keep their type).</summary>
        Public Property Arguments As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Text the result must contain (empty = any successful result).</summary>
        Public Property Expect As String
        Public Property IntervalSeconds As Integer = 300
        ''' <summary>Checks while it is not responding: the first after this long, then twice as long each time, up to DownMaxIntervalSeconds.</summary>
        Public Property DownIntervalSeconds As Integer = 30
        Public Property DownMaxIntervalSeconds As Integer = 600
        ''' <summary>
        ''' When the check starts a process that is still running when it returns (Open Terminal's run_command answers with its id
        ''' and status "running"), this tool stops it with {process_id, force: true}, e.g. "kill_process". Empty = left running.
        ''' </summary>
        Public Property StopTool As String
        Public Property TimeoutSeconds As Integer = 30
        Public Property FailuresBeforeRestart As Integer = 2
        ''' <summary>Call timeouts in a row that trigger a check at once.</summary>
        Public Property TimeoutsBeforeCheck As Integer = 2
        ''' <summary>Run (by cmd.exe /c or /bin/sh -c) when resetting the connection did not help; empty = none.</summary>
        Public Property RestartCommand As String
        Public Property RestartCommandTimeoutSeconds As Integer = 120
    End Class

    Public Class CommunicationOptions
        Public Property Enabled As Boolean = True
        Public Property MaxMessageCharacters As Integer = 100000
        Public Property MaxStructuredPayloadBytes As Integer = 65536
        Public Property MaxAttachments As Integer = 10
        Public Property MaxDelegationDepth As Integer = 5
        Public Property MaxCommunicationChainLength As Integer = 12
        Public Property MaxChildTasksPerTask As Integer = 10
        Public Property MaxMessagesPerCorrelation As Integer = 200
        Public Property MaxMessagesPerMinute As Integer = 100
        Public Property MaxRequestsPerMinute As Integer = 30
        Public Property MaxDelegationsPerHour As Integer = 60
        Public Property MaxOutstandingRequests As Integer = 10
        Public Property MaxMailboxSize As Integer = 500
        ''' <summary>Longest an agent's request to another agent (question or delegated task) waits for its answer.</summary>
        Public Property MaxAgentToAgentWaitSeconds As Integer = 14400
        ''' <summary>
        ''' Shortest such wait, whatever the asking agent sets: the other agent often needs a while (it may be busy or doing
        ''' real work for the answer), and an answer that arrives after the request expired helps nobody.
        ''' </summary>
        Public Property MinAgentToAgentWaitSeconds As Integer = 3600
        Public Property InlineWaitSeconds As Integer = 20
        Public Property DefaultMessageTtlMinutes As Integer = 1440
        Public Property AllowCycleRevisit As Boolean = False
        Public Property RecordInformationAsMemory As Boolean = True
        ''' <summary>Named broadcast groups: group → agent IDs.</summary>
        Public Property Groups As New Dictionary(Of String, List(Of String))(StringComparer.OrdinalIgnoreCase)
    End Class

    Public Class LimitsOptions
        Public Property MaxConcurrentAIRequests As Integer = 16
        Public Property MaxConcurrentMcpCalls As Integer = 16
        Public Property MaxConcurrentAgentMessages As Integer = 32
        Public Property MaxDatabaseOperations As Integer = 16
        Public Property MaxArtifactBytes As Integer = 20 * 1024 * 1024
        ''' <summary>Largest file an administrator can attach to a task (MB).</summary>
        Public Property MaxUploadMb As Integer = 50
        Public Property MaxArtifactStoragePerAgentMb As Integer = 500
    End Class

    Public Class SupervisionOptions
        Public Property HeartbeatIntervalSeconds As Integer = 10
        Public Property StuckAfterSeconds As Integer = 1200
        Public Property WatchdogIntervalSeconds As Integer = 15
        Public Property AutoRestart As Boolean = True
        Public Property MaxRestartAttempts As Integer = 5
        Public Property RestartWindowMinutes As Integer = 60
        Public Property RestartDelaySeconds As Integer = 5
        Public Property ExponentialBackoff As Boolean = True
        Public Property MaxBackoffSeconds As Integer = 300
        Public Property RestartStuckAgents As Boolean = True
    End Class

    Public Class RetentionOptions
        Public Property RunIntervalMinutes As Integer = 60
        Public Property ActivityDays As Integer = 14
        Public Property AiCallDetailDays As Integer = 30
        Public Property AiCallMetricDays As Integer = 180
        Public Property ToolCallPayloadDays As Integer = 30
        Public Property ToolCallMetricDays As Integer = 180
        Public Property MessageContentDays As Integer = 30
        Public Property MessageMetadataDays As Integer = 180
        Public Property CompletedTaskDays As Integer = 90
        ''' <summary>
        ''' Polling tasks that completed normally (a health check every minute adds 1,440 a day) are kept this long; failed ones
        ''' follow CompletedTaskDays. State changes stay in the agent's memory and the investigations they started.
        ''' </summary>
        Public Property PollingTaskDays As Integer = 3
        Public Property TaskHistoryDays As Integer = 90
        Public Property ArtifactDays As Integer = 90
        Public Property AuditDays As Integer = 365
        Public Property StatisticsDays As Integer = 365
        ''' <summary>Raw evidence of learning case studies is dropped after this; reports and lessons are kept.</summary>
        Public Property CaseStudyEvidenceDays As Integer = 180
        Public Property BackupIntervalHours As Integer = 24
        Public Property BackupCount As Integer = 7
    End Class

    Public Class LoggingOptions
        Public Property MinimumLevel As LogSeverity = LogSeverity.Info
        Public Property ConsoleLevel As LogSeverity = LogSeverity.Info
        Public Property Directory As String = "logs"
        Public Property RetainDays As Integer = 14
        Public Property MaxFileMB As Integer = 50
        ''' <summary>When true, prompts/responses are stored in AiCalls (subject to retention). Never written to log files.</summary>
        Public Property StoreAiContent As Boolean = False
    End Class

    Public Class AlertOptions
        Public Property WebhookUrl As String
        Public Property WebhookUrlEnvVar As String
        ''' <summary>Event type names to forward. Empty = sensible default set.</summary>
        Public Property Events As New List(Of String)
        Public Property MinIntervalSeconds As Integer = 60
    End Class

    ' ---------------------------------------------------------------------------------------------
    ' Agent profiles. BaseProfiles and Agents share one shape; Nothing/null means "inherit".
    ' ---------------------------------------------------------------------------------------------

    Public Class ProfileOptions
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property Role As String
        Public Property Personality As String
        ''' <summary>Replaces the base system prompt when set.</summary>
        Public Property SystemPrompt As String
        ''' <summary>Appended to the (base or replaced) system prompt.</summary>
        Public Property AppendSystemPrompt As String
        Public Property SafetyRules As List(Of String)
        Public Property Goals As List(Of String)
        Public Property Enabled As Boolean?
        Public Property BaseProfileId As String
        Public Property AllowedModels As List(Of String)
        Public Property MaxTier As Integer?
        Public Property DefaultPurpose As AiPurpose?
        Public Property AllowedMcpServers As List(Of String)
        Public Property AllowedMcpTools As List(Of String)
        Public Property DeniedMcpTools As List(Of String)
        Public Property InternalTools As List(Of String)
        Public Property Communication As CommunicationPolicyOptions
        Public Property Ai As AiPolicyOptions
        Public Property Tasks As TaskPolicyOptions
        Public Property Memory As MemoryPolicyOptions
        Public Property Economy As AgentEconomyOptions
        Public Property Subagents As SubagentPolicyOptions
        Public Property Idle As IdleOptions
        Public Property Schedules As List(Of ScheduleOptions)
        Public Property Autonomy As AgentAutonomyOptions
    End Class

    ''' <summary>How independently an agent works (Nothing = inherit from the base profile, then the level's defaults).</summary>
    Public Class AgentAutonomyOptions
        ''' <summary>
        ''' supervised (people decide everything; the default) | assisted (small decisions are automatic; the agent takes initiative and
        ''' directs itself: goals, schedules, webhooks, code) | autonomous (no person in the loop: its policy or a supervisor agent decides).
        ''' </summary>
        Public Property Level As String
        ''' <summary>Agent that decides this agent's requests its policy does not decide (instead of a person).</summary>
        Public Property SupervisorAgentId As String
        ''' <summary>Budget requests up to this many USD are approved automatically.</summary>
        Public Property AutoApproveBudgetUsd As Decimal?
        ''' <summary>Automatic budget approvals stop after this many USD in a day.</summary>
        Public Property AutoApproveBudgetPerDayUsd As Decimal?
        ''' <summary>Tools whose approval is automatic ("*" = all).</summary>
        Public Property AutoApproveTools As List(Of String)
        ''' <summary>Apply the agent's proposed behaviour changes at once (an evaluation then keeps or rolls back each one).</summary>
        Public Property AutoApplyBehaviorChanges As Boolean?
        ''' <summary>Approve the agent's hires within MaxHires without a person.</summary>
        Public Property AutoApproveHires As Boolean?
        ''' <summary>A question nobody answered for this long goes to the supervisor, or the agent decides itself (0 = wait for a person).</summary>
        Public Property AskHumanTimeoutMinutes As Integer?
        ''' <summary>Idle this long: the agent gets an initiative task (0 = never).</summary>
        Public Property InitiativeMinutes As Integer?
        ''' <summary>The agent may hire helper agents (hire_agent, retire_agent).</summary>
        Public Property CanHire As Boolean?
        Public Property MaxHires As Integer?
        ''' <summary>Agents this agent manages: it sees their work and can assign or cancel it. Its hires are always included.</summary>
        Public Property Manages As List(Of String)
        ''' <summary>Hosts http_request may call ("*" = any public host; empty = no http_request).</summary>
        Public Property AllowedHttpHosts As List(Of String)
        ''' <summary>Knowledge bases search_knowledge may search (ids; "*" = all of its owner's).</summary>
        Public Property KnowledgeBases As List(Of String)
        ''' <summary>Self-critique passes before a successful result is accepted (Nothing = the host's Autonomy.VerifyPasses).</summary>
        Public Property VerifyPasses As Integer?
        Public Property MaxSelfSchedules As Integer?
        Public Property MaxSelfGoals As Integer?
        ''' <summary>Run code in the sandbox (run_code). Default: assisted and autonomous agents.</summary>
        Public Property RunCode As Boolean?
        ''' <summary>Tools this agent must not have, even if its level grants them.</summary>
        Public Property DeniedTools As List(Of String)
    End Class

    Public Class SubagentPolicyOptions
        ''' <summary>Maximum concurrently running subagents for this agent (0 disables spawning).</summary>
        Public Property MaxConcurrent As Integer?
        ''' <summary>Default per-subagent cloud spend cap in USD.</summary>
        Public Property DefaultSpendCap As Decimal?
    End Class

    Public Class CommunicationPolicyOptions
        Public Property AllowedRecipients As List(Of String)
        Public Property AllowedSendMessageTypes As List(Of MessageType)
        Public Property AllowedReceiveMessageTypes As List(Of MessageType)
        Public Property MaxSendPriority As PriorityLevel?
        Public Property Groups As List(Of String)
        Public Property CanBroadcast As Boolean?
        Public Property MaxMessagesPerMinute As Integer?
        Public Property MaxRequestsPerMinute As Integer?
        Public Property MaxDelegationsPerHour As Integer?
        Public Property MaxOutstandingRequests As Integer?
    End Class

    Public Class AiPolicyOptions
        Public Property MaxAiCallsPerHour As Integer?
        Public Property MaxAiCallsPerDay As Integer?
        Public Property MaxTokensPerHour As Long?
        Public Property MaxTokensPerDay As Long?
        Public Property MaxConcurrentAiCalls As Integer?
    End Class

    Public Class TaskPolicyOptions
        ''' <summary>
        ''' Turns (and tool calls) between progress checks, not a total: when they are used up, a task that is still making
        ''' progress goes on; one that is not (only failing or repeated calls) stops with a report of its work.
        ''' </summary>
        Public Property MaxReasoningTurns As Integer?
        Public Property MaxToolCalls As Integer?
        ''' <summary>Turns in all before a task stops (0 or empty = no fixed total: it goes on while it makes progress, within its budget and AI limits).</summary>
        Public Property MaxTotalTurns As Integer?
        ''' <summary>One go of work on a task: one still making progress then pauses and goes on from its checkpoint (not a failed attempt).</summary>
        Public Property MaxTaskDurationSeconds As Integer?
        Public Property MaxToolOutputChars As Integer?
        Public Property MaxRetries As Integer?
        Public Property RetryBaseDelaySeconds As Integer?
        Public Property EscalateOnRetry As Boolean?
        Public Property MaxContextMessages As Integer?
    End Class

    Public Class MemoryPolicyOptions
        Public Property MaxMemories As Integer?
        Public Property RecallCount As Integer?
        Public Property AutoRecordTaskResults As Boolean?
        Public Property MaxMemoryChars As Integer?
        Public Property PromptMemoryChars As Integer?
    End Class

    Public Class AgentEconomyOptions
        Public Property InitialBalance As Decimal?
        Public Property DailySpendLimit As Decimal?
    End Class

    Public Class IdleOptions
        Public Property IdleDelaySeconds As Integer?
    End Class

    Public Class ScheduleOptions
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property Enabled As Boolean = True
        ''' <summary>Run every N seconds (interval schedule).</summary>
        Public Property IntervalSeconds As Integer?
        ''' <summary>Cron expression (5 or 6 fields, UTC). Used when IntervalSeconds is not set.</summary>
        Public Property Cron As String
        ''' <summary>reasoning | mcp-tool | http-check | noop</summary>
        Public Property Handler As String = "reasoning"
        Public Property Purpose As AiPurpose?
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
        Public Property Kind As TaskKind = TaskKind.Recurring
        Public Property SkipIfRunning As Boolean = True
        Public Property RunOnStart As Boolean = False
        Public Property Parameters As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Time zone for Cron: "UTC" (default) or "Local" (the server's time zone, used by dashboard schedules).</summary>
        Public Property TimeZone As String
        ''' <summary>No runs before this time.</summary>
        Public Property StartUtc As DateTime?
        ''' <summary>No runs after this time (the schedule then stops).</summary>
        Public Property EndUtc As DateTime?
        ''' <summary>Stop after this many runs.</summary>
        Public Property MaxRuns As Integer?
        ''' <summary>Model to pin the created tasks to (optional).</summary>
        Public Property ModelKey As String
        Public Property StartTier As Integer?
        Public Property MaxRetries As Integer?
        ''' <summary>Human-readable description of the recurrence ("Every 2 hours", "Weekly on Mon, Fri at 09:00").</summary>
        Public Property Summary As String
        ''' <summary>Review, delivery and attachments copied to every run (dashboard schedules).</summary>
        Public Property Delivery As Global.Agentic.Work.TaskDelivery
        ''' <summary>Expected outcome for the reviewer of every run.</summary>
        Public Property ReviewCriteria As String
        ''' <summary>Every run's tool calls are approved automatically.</summary>
        Public Property AutoApproveTools As Boolean
        ''' <summary>Each run must finish within this many minutes of being queued (0 = no deadline).</summary>
        Public Property DeadlineMinutes As Integer
        ''' <summary>A run past its deadline keeps working instead of being cancelled.</summary>
        Public Property RunPastDeadline As Boolean
    End Class

    ''' <summary>
    ''' Updates for the desktop app and the CLI. Each app has a folder under Directory that holds its exe (and, for the desktop
    ''' app, its www folder); clients compare their exe's SHA-256 and version with it and download what changed.
    ''' </summary>
    Public Class UpdateOptions
        Public Property Enabled As Boolean = True
        ''' <summary>Relative paths start at the folder of appsettings.json.</summary>
        Public Property Directory As String = "updates"
        ''' <summary>How often clients look for updates while they run.</summary>
        Public Property CheckIntervalMinutes As Integer = 360
        ''' <summary>
        ''' One-line installs without signing in: /install.ps1 and /install.sh (the CLI), the CLI binaries they download
        ''' (/install/bin/{channel}) and the desktop app's MSI (/install/desktop.msi). Off = only signed-in downloads.
        ''' </summary>
        Public Property PublicInstall As Boolean = True
    End Class

    ''' <summary>The Chat tab.</summary>
    Public Class ChatOptions
        Public Property Enabled As Boolean = True
        ''' <summary>Model preselected for new chats (Nothing = the first provider model).</summary>
        Public Property DefaultModel As String
        ''' <summary>Provider models never offered in Chat or the API: comma-separated ids, or prefixes ending in *.</summary>
        Public Property HiddenModels As String = "arena-model"
        ''' <summary>
        ''' Providers that are one (two connections to the same provider): {"dddai2": "dddai"} lists dddai2's models under dddai,
        ''' hides those dddai also has, and when a call to one fails it goes to the other's copy of the model.
        ''' </summary>
        Public Property ProviderMerges As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Prepended to every chat's system prompt.</summary>
        Public Property SystemPrompt As String
        ''' <summary>MCP server that runs Python, Node and shell code (needs run_command and write_file tools, e.g. Open Terminal).</summary>
        Public Property CodeServerId As String = "code-exec"
        ''' <summary>When the code server is unavailable, run code on this host (dangerous: it runs as the host's account). Administrators only.</summary>
        Public Property AllowLocalCodeExecution As Boolean = False
        Public Property CodeTimeoutSeconds As Integer = 60
        ''' <summary>
        ''' How often (in tool-calling rounds) an answer's progress is checked - not a stop. An answer still getting things done
        ''' (its calls in the last stretch gave results it had not seen) goes on for another stretch; one whose calls only repeated
        ''' earlier results or failed for a whole stretch is asked to answer from what it has. Only MaxTotalToolRounds, or a limit
        ''' the user set for the chat (Limits in the composer), stops at a count.
        ''' </summary>
        Public Property MaxToolRounds As Integer = 24
        ''' <summary>The progress check for a message whose client runs tools itself (CLI, desktop app, VS Code), unless it asks for another number.</summary>
        Public Property ClientToolRounds As Integer = 50
        ''' <summary>The longest stretch a client may ask for (maxToolRounds on the message).</summary>
        Public Property MaxClientToolRounds As Integer = 200
        ''' <summary>Tool rounds one answer may take in all (0 = no fixed total: progress, the user's Stop and the balance decide).</summary>
        Public Property MaxTotalToolRounds As Integer = 0
        ''' <summary>search_web calls per answer; after that the model must answer from what it found (0 = no limit).</summary>
        Public Property MaxWebSearches As Integer = 30
        ''' <summary>search_web calls per answer while the chat has a goal (0 = no limit).</summary>
        Public Property GoalMaxWebSearches As Integer = 100
        ''' <summary>Web pages read (read_page) per answer, and while the chat has a goal (0 = no limit).</summary>
        Public Property MaxPageReads As Integer = 60
        Public Property GoalMaxPageReads As Integer = 150
        ''' <summary>
        ''' After an answer that researched (searched the web, read pages - itself or through subagents), the harness saves the
        ''' chat's findings as Markdown: attached to the answer, and written to ResearchNotesFolder/&lt;chat&gt;.md in the workspace of
        ''' the computer the chat works on (CLI, VS Code, desktop app).
        ''' </summary>
        Public Property ResearchNotes As Boolean = True
        ''' <summary>The workspace folder research notes go to (relative to the workspace).</summary>
        Public Property ResearchNotesFolder As String = "research"
        ''' <summary>The progress check (tool rounds) while the chat has a goal (/goal).</summary>
        Public Property GoalMaxRounds As Integer = 80
        ''' <summary>How often an answer that stops before the goal is met is told to keep going (per message).</summary>
        Public Property GoalMaxContinues As Integer = 8
        ''' <summary>How many times in a row the model may stop without making progress (no tool call) before the goal is
        ''' paused instead of nudged again — so a model that only repeats a "holding" message is not asked over and over.</summary>
        Public Property GoalMaxStalls As Integer = 2
        ''' <summary>
        ''' A goal is worked on until it is met: when an answer ends with the goal still open (out of rounds, tools switched off,
        ''' the model stopping early, failures), the next answer starts by itself after GoalResumeDelaySeconds - at most
        ''' GoalMaxAutoResumes in a row without the user writing.
        ''' </summary>
        Public Property GoalAutoResume As Boolean = True
        Public Property GoalMaxAutoResumes As Integer = 25
        Public Property GoalResumeDelaySeconds As Integer = 5
        ''' <summary>Retries after a model failure (offline, overloaded, timed out) while the chat has a goal, and the wait between them.</summary>
        Public Property GoalRetries As Integer = 5
        Public Property GoalRetrySeconds As Integer = 120
        ''' <summary>Retries after a model failure (offline, overloaded, timed out) in any chat, and the wait between them.</summary>
        Public Property Retries As Integer = 5
        Public Property RetrySeconds As Integer = 120
        ''' <summary>
        ''' Retries for an answer that already has work in it - tool calls made, a task list with open steps, or a failed answer
        ''' it continues: giving up after Retries threw away a plan in progress over a provider's short outage (each failed
        ''' answer then needed the user to come back and say "keep going").
        ''' </summary>
        Public Property RetriesWithWork As Integer = 12
        ''' <summary>
        ''' Longest wait for a tool that runs on the user's computer (desktop app, VS Code, CLI): there is no shorter limit while that
        ''' computer stays connected, since builds, test suites and installs can take long (the app ends a command at its own timeout).
        ''' </summary>
        Public Property ClientToolTimeoutSeconds As Integer = 14400
        ''' <summary>How long a tool call waits for the user's approval; after that it does not run (the model hears the user may be away).</summary>
        Public Property ApprovalTimeoutSeconds As Integer = 900
        ''' <summary>A model stream that sends nothing (not even thinking) for this long is treated as failed and asked again.</summary>
        Public Property StreamIdleSeconds As Integer = 300
        ''' <summary>How long a model may take to start answering a request before it is treated as failed and asked again.</summary>
        Public Property ModelResponseSeconds As Integer = 600
        ''' <summary>How often an answer cut off by the model's output limit is continued by itself.</summary>
        Public Property MaxLengthContinues As Integer = 3
        ''' <summary>Compact a conversation automatically when it fills this share of the model's context window (0 = never).</summary>
        Public Property AutoCompactPercent As Integer = 80
        ''' <summary>Permission mode of new chats: auto (never asks) or manual (asks before new tools and sites).</summary>
        Public Property DefaultPermissionMode As String = "auto"
        ''' <summary>Context window per model, by part of its id (e.g. "my-model": 32000); known families are built in.</summary>
        Public Property ContextWindows As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Messages of history sent with each prompt.</summary>
        Public Property HistoryMessages As Integer = 40
        ''' <summary>Model for small background jobs: naming conversations and writing web search queries (Nothing = the chat's own model).</summary>
        Public Property TaskModel As String
        ''' <summary>Downloading files out of the code sandbox, and reaping long-lived sandbox processes and containers.</summary>
        Public Property Sandbox As New SandboxOptions()
        ''' <summary>
        ''' Subagents (the task tool, or the CLI's Task) a single answer runs at the same time. The model is told to split work into
        ''' independent parts and start them together; they share the local model's slots with everything else (queued fairly).
        ''' </summary>
        Public Property MaxParallelSubagents As Integer = 6
        ''' <summary>Read-only tool calls of one round (reading and searching files on the user's computer) run at the same time, up to this many.</summary>
        Public Property MaxParallelTools As Integer = 8
        ''' <summary>
        ''' How often (in model steps) a subagent's progress is checked - not a stop: one still getting things done goes on for another
        ''' stretch. One that made no progress in a stretch is told 3 steps ahead to finish, and its last step (without tools) is its
        ''' report; it can be continued with its whole context (the task tool's resume) instead of started over.
        ''' </summary>
        Public Property SubagentMaxRounds As Integer = 50
        ''' <summary>Model steps a subagent may take in all (0 = no fixed total: its progress decides).</summary>
        Public Property SubagentMaxTotalRounds As Integer = 0
        ''' <summary>How deep subagents may start their own subagents: 1 = only the answer starts them; 3 = two more levels (at most 5).</summary>
        Public Property MaxSubagentDepth As Integer = 3
        ''' <summary>How often due scheduled prompts (/schedule) are looked for, in seconds.</summary>
        Public Property SchedulePollSeconds As Integer = 30
        ''' <summary>
        ''' Public links to artifacts (the Artifacts page): HTML and SVG from chat answers, and websites uploaded from projects by
        ''' the desktop app, VS Code or the CLI. Off: no new links, and existing ones stop opening until it is on again.
        ''' </summary>
        Public Property ArtifactSharing As Boolean = True
        ''' <summary>Shared artifacts one user may have at a time.</summary>
        Public Property MaxSharedArtifacts As Integer = 50
        ''' <summary>The largest website one may share, in MB (the upload, and all its files unpacked).</summary>
        Public Property MaxSharedSiteMb As Integer = 25
    End Class

    ''' <summary>
    ''' The code sandbox (the Open Terminal MCP server) as a place to keep files and run long-lived things. Two jobs:
    ''' letting the user download what is made there, and stopping processes and Docker containers the host started so they
    ''' do not run forever. The reaper only ever touches resources the host itself recorded; it never touches anything that
    ''' already existed on the sandbox host, nor any Docker Compose project (infrastructure) unless its name is listed in
    ''' ManagedComposeProjects.
    ''' </summary>
    Public Class SandboxOptions
        ''' <summary>Let the user and the model download files (and zipped folders) out of the sandbox.</summary>
        Public Property AllowDownloads As Boolean = True
        ''' <summary>Largest single download pulled out of the sandbox, in MB (it travels base64-encoded over MCP, so keep it modest).</summary>
        Public Property MaxDownloadMb As Integer = 100
        ''' <summary>Stop long-lived sandbox processes and containers the host started once they go idle or reach their maximum age.</summary>
        Public Property ReaperEnabled As Boolean = True
        ''' <summary>How often the reaper looks (minutes).</summary>
        Public Property ReaperIntervalMinutes As Integer = 10
        ''' <summary>A background process started in the sandbox is stopped after this long with no new output (0 = never on idle).</summary>
        Public Property ProcessIdleMinutes As Integer = 120
        ''' <summary>A background process is always stopped once this old, busy or not (0 = no maximum).</summary>
        Public Property ProcessMaxLifeMinutes As Integer = 1440
        ''' <summary>A Docker container the host started is stopped once this old (0 = no maximum). Idle is not measured for containers.</summary>
        Public Property ContainerMaxLifeMinutes As Integer = 1440
        ''' <summary>Also remove (docker rm) a sandbox container after it is stopped, so it does not pile up.</summary>
        Public Property RemoveStoppedContainers As Boolean = True
        ''' <summary>Manage Docker containers at all. Needs passwordless `sudo docker` in the sandbox; off = processes only.</summary>
        Public Property ManageContainers As Boolean = True
        ''' <summary>
        ''' Compose projects the reaper may stop as well (by `docker compose down`). Empty by default: Compose stacks are treated
        ''' as infrastructure and left alone. Only ad-hoc `docker run` containers (no Compose project) are reaped by default.
        ''' </summary>
        Public Property ManagedComposeProjects As New List(Of String)
        ''' <summary>Container names or Compose projects the reaper must never stop, even if recorded (defense in depth for infrastructure).</summary>
        Public Property NeverTouch As New List(Of String)
        ''' <summary>Notify the owner chat when one of its sandbox resources is stopped.</summary>
        Public Property NotifyOwner As Boolean = True
        ''' <summary>
        ''' App previews: open a web app running in a sandbox container at /container/{id}/ on the host's own port (a reverse
        ''' proxy to the container's published port). Only containers the host started can be previewed, never infrastructure.
        ''' </summary>
        Public Property Previews As Boolean = True
        ''' <summary>The sandbox's Docker host as seen from this host (Nothing = the host of the code server's MCP endpoint).</summary>
        Public Property PreviewHost As String
        ''' <summary>How long a preview link works (hours); it can be revoked earlier.</summary>
        Public Property PreviewHours As Integer = 24
        ''' <summary>
        ''' sandbox (default): previewed pages run as an isolated origin (Content-Security-Policy sandbox), so the app's scripts
        ''' cannot use your Agentic session; apps that need their own cookies or a login may not work. none: the app runs as the
        ''' Agentic site itself — every app's scripts could act as the signed-in user. Only for apps you trust.
        ''' </summary>
        Public Property PreviewIsolation As String = "sandbox"
        ''' <summary>
        ''' How previews reach the app: auto (default: straight to the published port when this host can reach it, else through
        ''' the sandbox connection), direct, or mcp (always through the sandbox connection: no setup needed, but slower and
        ''' without WebSockets). Docker in WSL2 on Windows publishes ports only inside its VM, so there it is mcp unless the
        ''' Windows host forwards the port.
        ''' </summary>
        Public Property PreviewTransport As String = "auto"
        ''' <summary>
        ''' Host ports the sandbox host forwards to this host for app previews, e.g. "20000-20499". Apps are told to publish on a
        ''' free one, and their links go straight to it (full speed, WebSockets). Empty = none known: previews go through the
        ''' sandbox connection whenever the port cannot be reached.
        ''' </summary>
        Public Property PreviewPorts As String = ""
    End Class

    Public Class GlobalTaskOptionsConfig
        Public Property Enabled As Boolean = True
        ''' <summary>Local models used to plan and combine (empty: every local model).</summary>
        Public Property PlannerModelKeys As New List(Of String)
        Public Property MaxSteps As Integer = 8
        Public Property DefaultDeadlineMinutes As Integer = 240
        Public Property PlanningTimeoutSeconds As Integer = 600
    End Class

End Namespace
