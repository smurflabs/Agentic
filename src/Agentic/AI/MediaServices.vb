Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace AI

    Public NotInheritable Class MediaCallContext
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property Profile As EffectiveProfile
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
    End Class

    ''' <summary>
    ''' Image understanding. The local model cannot see images, so vision is exposed to agents as a tool backed by
    ''' dedicated vision models (default gemini-3.8-flash, fallback claude-sonnet-5, expert glm-5.3-flash).
    ''' </summary>
    Public NotInheritable Class VisionService
        Private ReadOnly _gateway As ModelGateway
        Private ReadOnly _opt As VisionOptions

        Public Sub New(gateway As ModelGateway, options As VisionOptions)
            _gateway = gateway
            _opt = options
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled AndAlso _opt.ModelKeys.Count > 0
            End Get
        End Property

        Public Function AgentCanUse(profile As EffectiveProfile) As Boolean
            If Not Enabled Then Return False
            Return _opt.ModelKeys.Concat(_opt.ExpertModelKeys).Any(Function(k) _gateway.Selection.IsUsableBy(_gateway.Registry.TryGet(k), profile, {ModelCapability.Vision}))
        End Function

        Public Async Function AnalyzeAsync(ctx As MediaCallContext, imageUrl As String, question As String, expert As Boolean, ct As CancellationToken) As Task(Of AiCallOutcome)
            Dim keys = If(expert AndAlso _opt.ExpertModelKeys.Count > 0, _opt.ExpertModelKeys.Concat(_opt.ModelKeys).ToList(), _opt.ModelKeys.Concat(_opt.ExpertModelKeys).ToList())
            Dim callCtx As New AiCallContext With {
                .AgentId = ctx.AgentId, .TaskId = ctx.TaskId, .CorrelationId = ctx.CorrelationId, .Profile = ctx.Profile, .Priority = ctx.Priority,
                .Purpose = AiPurpose.Vision, .AllowSpillover = False, .ExplicitModels = keys, .ReasonOverride = AiCallReason.Vision,
                .MaxTokensOverride = _opt.MaxTokens}
            callCtx.RequiredCapabilities.Add(ModelCapability.Vision)
            Dim messages As New List(Of ChatMessage) From {
                ChatMessage.ForSystem("You are a precise vision assistant. Describe only what is actually visible. If something is unreadable or uncertain, say so. Never invent details."),
                New ChatMessage With {.Role = "user", .Content = If(String.IsNullOrWhiteSpace(question), "Describe this image in detail.", question), .ImageUrls = New List(Of String) From {imageUrl}}
            }
            Return Await _gateway.ChatAsync(callCtx, messages, Nothing, ct).ConfigureAwait(False)
        End Function
    End Class

    ''' <summary>Image generation through OpenWebUI's configured image engine (gpt-image-2). Results become artifacts owned by the agent.</summary>
    Public NotInheritable Class ImageGenerationService
        Private ReadOnly _gateway As ModelGateway
        Private ReadOnly _opt As ImageGenerationOptions
        Private ReadOnly _artifacts As ArtifactStore
        Private ReadOnly _wallets As WalletService
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _telemetry As TelemetryRepository
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _economy As EconomyOptions
        Private ReadOnly _limiter As SlidingWindowRateLimiter
        Private ReadOnly _concurrency As SemaphoreSlim

        Public Sub New(gateway As ModelGateway, options As ImageGenerationOptions, artifacts As ArtifactStore, wallets As WalletService, pricing As PricingService,
                       telemetry As TelemetryRepository, events As IAgentEventBus, economy As EconomyOptions, clock As IClock)
            _gateway = gateway
            _opt = options
            _artifacts = artifacts
            _wallets = wallets
            _pricing = pricing
            _telemetry = telemetry
            _events = events
            _economy = economy
            _limiter = New SlidingWindowRateLimiter(clock)
            _concurrency = New SemaphoreSlim(Math.Max(1, options.MaxConcurrency))
        End Sub

        ''' <summary>On, with the images API set up (ImageGeneration.BaseUrl and ApiKey).</summary>
        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled AndAlso Router IsNot Nothing AndAlso Router.Enabled
            End Get
        End Property

        ''' <summary>The images API and its models (set by the host). Pictures come only from there.</summary>
        Public Property Router As ImageRouter

        Public Async Function GenerateAsync(ctx As MediaCallContext, prompt As String, size As String, ct As CancellationToken) As Task(Of ArtifactInfo)
            If Not _opt.Enabled Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "Image generation is disabled.")
            If String.IsNullOrWhiteSpace(prompt) Then Throw New AiException(AiErrorKind.InvalidRequest, "Prompt is required.")
            Dim finalSize = If(String.IsNullOrWhiteSpace(size), _opt.DefaultSize, size)
            If _opt.AllowedSizes.Count > 0 AndAlso Not _opt.AllowedSizes.Contains(finalSize, StringComparer.OrdinalIgnoreCase) Then
                Throw New AiException(AiErrorKind.InvalidRequest, $"Size must be one of: {String.Join(", ", _opt.AllowedSizes)}.")
            End If
            ' Subagents spend from (and count against the limits of) their parent's wallet.
            Dim payer = If(ctx.Profile?.BillingAgentId, ctx.AgentId)
            Dim spentBy = If(String.Equals(payer, ctx.AgentId, StringComparison.OrdinalIgnoreCase), Nothing, ctx.AgentId)
            If Not _limiter.TryAcquire(payer & ":img", _opt.MaxPerAgentPerDay, TimeSpan.FromDays(1)) Then
                Throw New AiException(AiErrorKind.QuotaExceeded, $"Daily image generation limit ({_opt.MaxPerAgentPerDay}) reached.")
            End If
            Dim price = _pricing.GetPrice(PricingService.ImageGenerationKey)
            Dim cost = PricingService.ImageCostMicros(price, 1)
            If _economy.Enabled AndAlso _economy.RequirePricingForCloudModels AndAlso Not price.PerImage.HasValue Then
                Throw New AiException(AiErrorKind.BudgetExceeded, "Image generation has no price configured (Finance → Pricing → image-generation).")
            End If
            If cost > 0 Then
                If Not _gateway.SpendCaps.TryReserve(ctx.AgentId, cost) Then Throw New AiException(AiErrorKind.BudgetExceeded, "Subagent spend cap reached for image generation.")
                Dim rr = Await _wallets.TryReserveAsync(payer, cost).ConfigureAwait(False)
                If rr <> ReserveResult.Ok Then
                    _gateway.SpendCaps.Release(ctx.AgentId, cost)
                    Throw New AiException(AiErrorKind.BudgetExceeded, $"Insufficient budget for image generation ({TextUtil.FormatMicros(cost)}): {rr}")
                End If
            End If
            Dim settled = False
            Dim sw = Stopwatch.StartNew()
            Dim callId = Ids.NewId("img")
            Try
                Await _concurrency.WaitAsync(ct).ConfigureAwait(False)
                Dim result As ImageGenerationResult
                Dim madeBy = _opt.Model
                Try
                    _events.Emit(AgentEventType.AiCallStarted, "Image generation requested", ctx.AgentId, ctx.TaskId, correlationId:=ctx.CorrelationId,
                                 data:=New Dictionary(Of String, Object) From {{"model", PricingService.ImageGenerationKey}})
                    ' Only the images API's models (ImageGeneration.Model, then its other models when one cannot make the picture).
                    If Router Is Nothing OrElse Not Router.Enabled Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "Image generation is not set up (ImageGeneration.BaseUrl and ApiKey).")
                    Dim routed = Await Router.GenerateAsync(prompt, finalSize, ct).ConfigureAwait(False)
                    result = routed.Result
                    madeBy = routed.Model
                Finally
                    _concurrency.Release()
                End Try
                Dim info = Await _artifacts.SaveAsync(ctx.AgentId, $"generated-{DateTime.UtcNow:yyyyMMddHHmmss}.png", If(result.MimeType, "image/png"), result.Data,
                                                      "Generated image: " & TextUtil.Truncate(prompt, 500), ctx.TaskId).ConfigureAwait(False)
                If cost > 0 Then
                    _gateway.SpendCaps.Settle(ctx.AgentId, cost, cost)
                    Await _wallets.SettleAsync(payer, cost, cost, New ChargeDetails With {
                        .EntryType = LedgerEntryType.ImageCharge, .ModelKey = PricingService.ImageGenerationKey, .AiCallId = callId, .TaskId = ctx.TaskId,
                        .CorrelationId = ctx.CorrelationId, .SpentByAgentId = spentBy,
                        .Description = "Image generation " & finalSize & If(spentBy Is Nothing, "", $" by subagent {spentBy}")}).ConfigureAwait(False)
                End If
                settled = True
                Await _telemetry.InsertAiCallAsync(New AiCallRecord With {
                    .Id = callId, .AgentId = ctx.AgentId, .TaskId = ctx.TaskId, .CorrelationId = ctx.CorrelationId, .ModelKey = PricingService.ImageGenerationKey,
                    .ModelId = madeBy, .Purpose = AiPurpose.ImageGeneration.ToString(), .Tier = 0, .Reason = AiCallReason.ImageGeneration,
                    .DurationMs = sw.ElapsedMilliseconds, .Success = True, .CostMicros = cost, .CreatedUtc = DateTime.UtcNow}).ConfigureAwait(False)
                _events.Emit(AgentEventType.AiCallCompleted, $"Image generated ({info.SizeBytes \ 1024} KB) in {sw.ElapsedMilliseconds}ms", ctx.AgentId, ctx.TaskId,
                             correlationId:=ctx.CorrelationId, data:=New Dictionary(Of String, Object) From {
                                {"model", PricingService.ImageGenerationKey}, {"success", True}, {"durationMs", sw.ElapsedMilliseconds}, {"costMicros", cost}, {"artifactId", info.Id}})
                Return info
            Catch ex As AiException
                Dim rec = New AiCallRecord With {
                    .Id = callId, .AgentId = ctx.AgentId, .TaskId = ctx.TaskId, .CorrelationId = ctx.CorrelationId, .ModelKey = PricingService.ImageGenerationKey,
                    .ModelId = _opt.Model, .Purpose = AiPurpose.ImageGeneration.ToString(), .Reason = AiCallReason.ImageGeneration, .DurationMs = sw.ElapsedMilliseconds,
                    .Success = False, .ErrorKind = ex.Kind.ToString(), .ErrorMessage = ex.Message, .CreatedUtc = DateTime.UtcNow}
                Dim ignored = _telemetry.InsertAiCallAsync(rec)
                Throw
            Finally
                If Not settled AndAlso cost > 0 Then
                    _gateway.SpendCaps.Release(ctx.AgentId, cost)
                    Dim ignored2 = _wallets.ReleaseAsync(payer, cost)
                End If
            End Try
        End Function
    End Class

    Public NotInheritable Class ScopedVision
        Private ReadOnly _service As VisionService
        Private ReadOnly _agentId As String

        Friend Sub New(service As VisionService, agentId As String)
            _service = service
            _agentId = agentId
        End Sub

        Public Function AgentCanUse(profile As EffectiveProfile) As Boolean
            Return _service.AgentCanUse(profile)
        End Function

        Public Function AnalyzeAsync(profile As EffectiveProfile, taskId As String, correlationId As String, priority As PriorityLevel, imageUrl As String, question As String, expert As Boolean, ct As CancellationToken) As Task(Of AiCallOutcome)
            Return _service.AnalyzeAsync(New MediaCallContext With {.AgentId = _agentId, .TaskId = taskId, .CorrelationId = correlationId, .Profile = profile, .Priority = priority},
                                         imageUrl, question, expert, ct)
        End Function
    End Class

    Public NotInheritable Class ScopedImageGeneration
        Private ReadOnly _service As ImageGenerationService
        Private ReadOnly _agentId As String

        Friend Sub New(service As ImageGenerationService, agentId As String)
            _service = service
            _agentId = agentId
        End Sub

        Public ReadOnly Property Enabled As Boolean
            Get
                Return _service.Enabled
            End Get
        End Property

        Public Function GenerateAsync(profile As EffectiveProfile, taskId As String, correlationId As String, prompt As String, size As String, ct As CancellationToken) As Task(Of ArtifactInfo)
            Return _service.GenerateAsync(New MediaCallContext With {.AgentId = _agentId, .TaskId = taskId, .CorrelationId = correlationId, .Profile = profile}, prompt, size, ct)
        End Function
    End Class

End Namespace
