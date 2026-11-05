Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace AI

    Public Module TokenEstimator
        ''' <summary>Conservative token estimate (≈3.5 chars/token; images ≈1000 tokens each).</summary>
        Public Function EstimateTokens(messages As IEnumerable(Of ChatMessage), Optional tools As IEnumerable(Of ToolDefinition) = Nothing) As Long
            Dim chars As Long = 0
            Dim images As Long = 0
            For Each m In messages
                chars += m.EstimatedChars()
                If m.ImageUrls IsNot Nothing Then images += m.ImageUrls.Count
            Next
            If tools IsNot Nothing Then
                For Each t In tools
                    chars += If(t.Name, "").Length + If(t.Description, "").Length + If(t.Parameters?.ToJsonString(), "").Length
                Next
            End If
            Return CLng(Math.Ceiling(chars / 3.5)) + images * 1000
        End Function

        Public Function EstimateTokens(text As String) As Long
            Return CLng(Math.Ceiling(If(text, "").Length / 3.5))
        End Function
    End Module

    ''' <summary>Per-agent AI call/token budgets over sliding hour/day windows (in-memory, seeded from history at startup).</summary>
    Public NotInheritable Class UsageLimiter
        Private Class Window
            Public ReadOnly Lock As New Object()
            Public ReadOnly Calls As New Queue(Of DateTime)
            Public ReadOnly Tokens As New Queue(Of (DateTime, Long))
        End Class

        Private ReadOnly _windows As New ConcurrentDictionary(Of String, Window)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _clock As IClock

        Public Sub New(clock As IClock)
            _clock = clock
        End Sub

        Public Sub Seed(agentId As String, callsLastDay As Long, tokensLastDay As Long)
            Seed(agentId, {(_clock.UtcNow.AddHours(-2), callsLastDay, tokensLastDay)})
        End Sub

        ''' <summary>
        ''' The last day's use after a restart, hour by hour (oldest first), so the window frees up when that use really
        ''' turns a day old. Stamping it all at one time kept an agent locked out for up to a day after every restart.
        ''' </summary>
        Public Sub Seed(agentId As String, hours As IEnumerable(Of (HourUtc As DateTime, Calls As Long, Tokens As Long)))
            Dim w = _windows.GetOrAdd(agentId, Function(k) New Window())
            SyncLock w.Lock
                For Each h In hours.OrderBy(Function(x) x.HourUtc)
                    For i = 1 To CInt(Math.Min(h.Calls, 100000))
                        w.Calls.Enqueue(h.HourUtc)
                    Next
                    If h.Tokens > 0 Then w.Tokens.Enqueue((h.HourUtc, h.Tokens))
                Next
            End SyncLock
        End Sub

        ''' <summary>
        ''' Checks the profile's limits. Subagents share their parent's window (BillingAgentId), so spawning cannot bypass them.
        ''' The token limits count cloud tokens only (Record gets 0 for a local call); with <paramref name="includeTokens"/>
        ''' False only the call limits are checked, so the free local models keep working once the cloud tokens are used up.
        ''' </summary>
        Public Function Check(profile As EffectiveProfile, Optional includeTokens As Boolean = True) As String
            Dim w = _windows.GetOrAdd(profile.BillingAgentId, Function(k) New Window())
            Dim now = _clock.UtcNow
            SyncLock w.Lock
                While w.Calls.Count > 0 AndAlso now - w.Calls.Peek() > TimeSpan.FromDays(1)
                    w.Calls.Dequeue()
                End While
                While w.Tokens.Count > 0 AndAlso now - w.Tokens.Peek().Item1 > TimeSpan.FromDays(1)
                    w.Tokens.Dequeue()
                End While
                Dim hourAgo = now.AddHours(-1)
                If profile.MaxAiCallsPerDay.HasValue AndAlso w.Calls.Count >= profile.MaxAiCallsPerDay.Value Then Return $"daily AI call limit ({profile.MaxAiCallsPerDay.Value}) reached"
                If profile.MaxAiCallsPerHour.HasValue AndAlso w.Calls.Where(Function(t) t > hourAgo).Count() >= profile.MaxAiCallsPerHour.Value Then Return $"hourly AI call limit ({profile.MaxAiCallsPerHour.Value}) reached"
                If Not includeTokens Then Return Nothing
                If profile.MaxTokensPerDay.HasValue AndAlso w.Tokens.Sum(Function(t) t.Item2) >= profile.MaxTokensPerDay.Value Then Return $"daily token limit ({profile.MaxTokensPerDay.Value}) reached"
                If profile.MaxTokensPerHour.HasValue AndAlso w.Tokens.Where(Function(t) t.Item1 > hourAgo).Sum(Function(t) t.Item2) >= profile.MaxTokensPerHour.Value Then Return $"hourly token limit ({profile.MaxTokensPerHour.Value}) reached"
            End SyncLock
            Return Nothing
        End Function

        ''' <summary>The cloud token limit reached, if any (Nothing while the agent may still use cloud models).</summary>
        Public Function CheckCloudTokens(profile As EffectiveProfile) As String
            Dim calls = Check(profile, includeTokens:=False)
            Dim all = Check(profile)
            Return If(calls Is Nothing, all, Nothing)
        End Function

        Public Sub Record(agentId As String, tokens As Long)
            Dim w = _windows.GetOrAdd(agentId, Function(k) New Window())
            Dim now = _clock.UtcNow
            SyncLock w.Lock
                w.Calls.Enqueue(now)
                If tokens > 0 Then w.Tokens.Enqueue((now, tokens))
            End SyncLock
        End Sub
    End Class

    Public NotInheritable Class AiCallContext
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property Profile As EffectiveProfile
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
        Public Property Purpose As AiPurpose = AiPurpose.GeneralReasoning
        Public Property LadderLevel As Integer
        Public Property RequiredCapabilities As New HashSet(Of ModelCapability)
        Public Property AllowSpillover As Boolean = True
        ''' <summary>Explicit model list (bypasses the ladder), e.g. vision models. Still permission-checked.</summary>
        Public Property ExplicitModels As List(Of String)
        Public Property ReasonOverride As AiCallReason?
        Public Property MaxTokensOverride As Integer?
        ''' <summary>Explicitly chosen model (consult/switch/pinned): ignore the model's AllowedPurposes list.</summary>
        Public Property SkipPurposeCheck As Boolean
        ''' <summary>Set by the gateway: the agent's cloud token limit reached (cloud models are skipped), else Nothing.</summary>
        Public Property CloudTokenLimit As String
    End Class

    Public NotInheritable Class AiCallOutcome
        Public Property Response As ChatResponse
        Public Property ModelKey As String
        Public Property ModelId As String
        Public Property Tier As Integer
        Public Property Reason As AiCallReason
        Public Property CostMicros As Long
        Public Property QueueWaitMs As Long
        Public Property UsageEstimated As Boolean
        Public Property AiCallId As String
        Public Property ContextWindowTokens As Integer
        Public Property PromptTokens As Long
        Public Property CompletionTokens As Long
    End Class

    ''' <summary>
    ''' Executes AI calls for agents: permission-filtered candidate models, local-model priority queue with
    ''' budget-gated spillover, per-model circuit breakers and fallbacks, retries with backoff, wallet
    ''' reservation/settlement, usage limits, auditing (AiCalls) and events.
    ''' </summary>
    ''' <summary>A person's hold on a local model slot (Chat, the API); disposing it frees the slot.</summary>
    Friend NotInheritable Class SlotLease
        Implements IDisposable
        Private ReadOnly _ticket As GateTicket
        Private ReadOnly _model As ModelRuntime
        Private _done As Integer

        Public Sub New(ticket As GateTicket, model As ModelRuntime)
            _ticket = ticket
            _model = model
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_done, 1) = 1 Then Return
            _model.NoteChatSlot(-1)
            _ticket.Dispose()
        End Sub
    End Class

    Public NotInheritable Class ModelGateway
        Private ReadOnly _providers As IReadOnlyDictionary(Of String, IAIProvider)
        Private ReadOnly _registry As ModelRegistry
        Private ReadOnly _selection As ModelSelectionService
        Private ReadOnly _wallets As WalletService
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _telemetry As TelemetryRepository
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _usage As UsageLimiter
        Private ReadOnly _global As SemaphoreSlim
        Private ReadOnly _economy As EconomyOptions
        Private ReadOnly _storeContent As Boolean
        Private ReadOnly _clock As IClock
        Private ReadOnly _providerRetries As Integer
        Private ReadOnly _agentConcurrency As New ConcurrentDictionary(Of String, SemaphoreSlim)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _caps As SpendCapTracker
        Private _spillovers As Long
        Private _escalations As Long
        Private _balancer As LocalBalancer

        Public Sub New(providers As IReadOnlyDictionary(Of String, IAIProvider), registry As ModelRegistry, selection As ModelSelectionService, wallets As WalletService, pricing As PricingService,
                       telemetry As TelemetryRepository, events As IAgentEventBus, log As ILog, usage As UsageLimiter, limits As LimitsOptions,
                       economy As EconomyOptions, storeContent As Boolean, clock As IClock, Optional providerRetries As Integer = 2, Optional caps As SpendCapTracker = Nothing)
            _caps = If(caps, New SpendCapTracker())
            _providers = providers
            _registry = registry
            _selection = selection
            _wallets = wallets
            _pricing = pricing
            _telemetry = telemetry
            _events = events
            _log = log
            _usage = usage
            _global = New SemaphoreSlim(Math.Max(1, limits.MaxConcurrentAIRequests))
            _economy = economy
            _storeContent = storeContent
            _clock = clock
            _providerRetries = Math.Max(0, providerRetries)
            _balancer = New LocalBalancer(clock)
        End Sub

        Public ReadOnly Property Selection As ModelSelectionService
            Get
                Return _selection
            End Get
        End Property

        Public ReadOnly Property Registry As ModelRegistry
            Get
                Return _registry
            End Get
        End Property

        Public ReadOnly Property SpendCaps As SpendCapTracker
            Get
                Return _caps
            End Get
        End Property

        Public ReadOnly Property SpilloverCount As Long
            Get
                Return Interlocked.Read(_spillovers)
            End Get
        End Property

        Public Sub NoteEscalation()
            Interlocked.Increment(_escalations)
        End Sub

        Public ReadOnly Property EscalationCount As Long
            Get
                Return Interlocked.Read(_escalations)
            End Get
        End Property

        ''' <summary>
        ''' Called after an agent's cloud model use was charged to its wallet: (paying agent, micro-USD, model key, description).
        ''' The host charges the account that owns the agent as well.
        ''' </summary>
        Public Property AgentCharged As Func(Of String, Long, String, String, Task)

        ''' <summary>Where each call's speed goes (the model picker's response time and tokens per second); Nothing = nowhere.</summary>
        Public Property Health As ModelHealth

        ''' <summary>The local model (its slots are shared with the agents) that serves this model id, or Nothing.</summary>
        Public Function LocalModelFor(modelId As String) As ModelRuntime
            If String.IsNullOrWhiteSpace(modelId) Then Return Nothing
            Return _registry.All().FirstOrDefault(Function(m) m.Options.IsLocal AndAlso m.Options.Enabled AndAlso String.Equals(m.Options.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>
        ''' A slot of a local model for one request from a person (Chat, the API). The model's slots (4 for the local Qwen) are
        ''' shared with the agents, so the dashboard counts it; people go before Normal-priority agent work. Returns Nothing for
        ''' models without shared slots. While every slot is busy, onQueued gets (requests ahead, slots) about every 2 seconds.
        ''' </summary>
        Public Async Function TakeLocalSlotAsync(modelId As String, onQueued As Func(Of Integer, Integer, Task), ct As CancellationToken) As Task(Of IDisposable)
            Dim m = LocalModelFor(modelId)
            If m Is Nothing Then Return Nothing
            Dim ticket = m.Gate.Enter(PriorityLevel.High)
            Try
                If Not ticket.IsGranted AndAlso onQueued IsNot Nothing Then Await onQueued(Math.Max(0, m.Gate.PositionOf(ticket)), m.Gate.Capacity).ConfigureAwait(False)
                While Not Await ticket.WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(False)
                    If onQueued IsNot Nothing Then Await onQueued(Math.Max(0, m.Gate.PositionOf(ticket)), m.Gate.Capacity).ConfigureAwait(False)
                End While
            Catch
                ticket.Dispose()
                Throw
            End Try
            m.NoteChatSlot(1)
            Return New SlotLease(ticket, m)
        End Function

        Public Function GetProvider(name As String) As IAIProvider
            Dim p As IAIProvider = Nothing
            If _providers.TryGetValue(If(name, ""), p) Then Return p
            Return _providers.Values.FirstOrDefault()
        End Function

        Public Async Function ChatAsync(ctx As AiCallContext, messages As List(Of ChatMessage), tools As List(Of ToolDefinition), ct As CancellationToken) As Task(Of AiCallOutcome)
            Dim limitReason = _usage.Check(ctx.Profile, includeTokens:=False)
            If limitReason IsNot Nothing Then Throw New AiException(AiErrorKind.QuotaExceeded, "AI usage limit: " & limitReason)
            ' Token limits stop cloud models only; the free local models go on.
            Dim cloudLimit = _usage.CheckCloudTokens(ctx.Profile)
            ctx.CloudTokenLimit = cloudLimit

            Dim candidates As List(Of ModelCandidate)
            If ctx.ExplicitModels IsNot Nothing Then
                candidates = New List(Of ModelCandidate)
                For Each k In ctx.ExplicitModels
                    Dim m = _registry.TryGet(k)
                    If _selection.IsUsableBy(m, ctx.Profile, ctx.RequiredCapabilities, If(ctx.SkipPurposeCheck, CType(Nothing, AiPurpose?), ctx.Purpose)) Then
                        candidates.Add(New ModelCandidate With {.Model = m, .Reason = If(candidates.Count = 0, If(ctx.ReasonOverride, AiCallReason.Primary), AiCallReason.Fallback),
                                                               .Price = Nothing})
                    End If
                Next
            Else
                candidates = _selection.CandidatesAt(ctx.Profile, ctx.Purpose, ctx.LadderLevel, ctx.RequiredCapabilities)
                If ctx.ReasonOverride.HasValue AndAlso candidates.Count > 0 Then candidates(0).Reason = ctx.ReasonOverride.Value
            End If
            If candidates.Count = 0 Then
                Throw New AiException(AiErrorKind.NoModelAvailable, $"No permitted model is available for {ctx.Purpose} at escalation level {ctx.LadderLevel}.")
            End If

            Dim agentSem = _agentConcurrency.GetOrAdd(ctx.AgentId, Function(k) New SemaphoreSlim(Math.Max(1, ctx.Profile.MaxConcurrentAiCalls)))
            Await agentSem.WaitAsync(ct).ConfigureAwait(False)
            Try
                Dim estimatedPrompt = TokenEstimator.EstimateTokens(messages, tools)
                Dim errors As New List(Of String)
                Dim lastError As AiException = Nothing
                Dim budgetBlocked = False
                Dim quotaBlocked = False
                For Each cand In candidates
                    ct.ThrowIfCancellationRequested()
                    Dim m = cand.Model
                    If cloudLimit IsNot Nothing AndAlso Not m.Options.IsLocal Then
                        errors.Add($"{m.Key}: {cloudLimit}")
                        quotaBlocked = True
                        Continue For
                    End If
                    If Not m.Breaker.AllowRequest() Then
                        errors.Add($"{m.Key}: circuit open")
                        Continue For
                    End If
                    Dim outcome As AiCallOutcome = Nothing
                    Dim failure As AiException = Nothing
                    Try
                        If m.Options.IsLocal AndAlso ctx.AllowSpillover AndAlso _selection.IsSpilloverSource(m.Key) Then
                            outcome = Await CallLocalWithSpilloverAsync(ctx, cand, messages, tools, estimatedPrompt, ct).ConfigureAwait(False)
                        Else
                            outcome = Await CallModelAsync(ctx, cand, messages, tools, estimatedPrompt, Nothing, ct).ConfigureAwait(False)
                        End If
                    Catch ex As AiException
                        failure = ex
                    End Try
                    If outcome IsNot Nothing Then Return outcome
                    lastError = failure
                    errors.Add($"{m.Key}: {failure.Kind} {TextUtil.Truncate(failure.Message, 200)}")
                    If failure.OutOfCredit Then PauseProvider(m, failure, ctx)
                    ' Too long for it: the provider usually names its real window - learned, so the next turn is compacted to fit.
                    If failure.Kind = AiErrorKind.ContextOverflow AndAlso Not m.Options.IsLocal Then _registry.Limits?.RecordRejected(m.Options.ModelId, estimatedPrompt, failure.Message, "agents")
                    Select Case failure.Kind
                        Case AiErrorKind.ContextOverflow, AiErrorKind.Auth, AiErrorKind.Cancelled, AiErrorKind.QuotaExceeded
                            Throw failure
                        Case AiErrorKind.BudgetExceeded
                            budgetBlocked = True
                    End Select
                Next
                If quotaBlocked AndAlso lastError Is Nothing Then Throw New AiException(AiErrorKind.QuotaExceeded, "AI usage limit: " & cloudLimit & " (cloud models; the local models are not limited)")
                Dim kind = If(budgetBlocked AndAlso (lastError Is Nothing OrElse lastError.Kind = AiErrorKind.BudgetExceeded), AiErrorKind.BudgetExceeded,
                              If(lastError?.Kind = AiErrorKind.ModelUnavailable OrElse lastError Is Nothing, AiErrorKind.NoModelAvailable, lastError.Kind))
                Throw New AiException(kind, "All candidate models failed: " & String.Join("; ", errors))
            Finally
                agentSem.Release()
            End Try
        End Function

        ''' <summary>
        ''' Local model path: join the priority queue; if the wait exceeds the spillover threshold, try the cheapest
        ''' affordable cloud spillover model (keeping our queue position). If none is affordable, keep waiting until
        ''' the queue timeout.
        ''' </summary>
        Private Async Function CallLocalWithSpilloverAsync(ctx As AiCallContext, cand As ModelCandidate, messages As List(Of ChatMessage), tools As List(Of ToolDefinition),
                                                           estimatedPrompt As Long, ct As CancellationToken) As Task(Of AiCallOutcome)
            Dim m = cand.Model
            Dim sw = Stopwatch.StartNew()
            ' Shared all the time: the task may belong on the other local model (fewer tasks on it), not only when this one is full.
            If _selection.LocalBalance Then
                Dim balanced = Await TryBalancedLocalAsync(ctx, m, sw, messages, tools, estimatedPrompt, ct).ConfigureAwait(False)
                If balanced IsNot Nothing Then Return balanced
            End If
            Using ticket = m.Gate.Enter(ctx.Priority)
                ' Full: another local model with a free slot (local-vision) answers at once, free, before any waiting.
                If Not ticket.IsGranted Then
                    Dim localNow = Await TryLocalSpillAsync(ctx, m, sw, messages, tools, estimatedPrompt, ct).ConfigureAwait(False)
                    If localNow IsNot Nothing Then Return localNow
                End If
                Dim granted = Await ticket.WaitAsync(_selection.SpillAfter(ctx.Priority), ct).ConfigureAwait(False)
                If Not granted Then
                    Dim localLater = Await TryLocalSpillAsync(ctx, m, sw, messages, tools, estimatedPrompt, ct).ConfigureAwait(False)
                    If localLater IsNot Nothing Then Return localLater
                    ' Cloud spillover only while the agent's cloud token limit allows it.
                    Dim spills = If(ctx.CloudTokenLimit Is Nothing, _selection.SpilloverCandidates(ctx.Profile, ctx.RequiredCapabilities, ctx.Purpose), New List(Of ModelCandidate))
                    For Each sc In spills
                        Dim spillOutcome As AiCallOutcome = Nothing
                        Try
                            spillOutcome = Await CallModelAsync(ctx, sc, messages, tools, estimatedPrompt, Nothing, ct, queueWaitMs:=sw.ElapsedMilliseconds).ConfigureAwait(False)
                        Catch ex As AiException When ex.Kind <> AiErrorKind.Cancelled AndAlso ex.Kind <> AiErrorKind.ContextOverflow
                            _log.Dbg("Gateway", $"Spillover to {sc.Model.Key} not possible: {ex.Kind}", ctx.AgentId, ctx.TaskId)
                        End Try
                        If spillOutcome IsNot Nothing Then
                            Interlocked.Increment(_spillovers)
                            _events.Emit(AgentEventType.AiSpillover, $"Local queue busy for {sw.Elapsed.TotalSeconds:0}s; spilled to {sc.Model.Key}", ctx.AgentId, ctx.TaskId,
                                         correlationId:=ctx.CorrelationId, data:=New Dictionary(Of String, Object) From {
                                            {"from", m.Key}, {"to", sc.Model.Key}, {"costMicros", spillOutcome.CostMicros}, {"waitMs", sw.ElapsedMilliseconds}})
                            Return spillOutcome
                        End If
                    Next
                    Dim remaining = _selection.QueueTimeout - sw.Elapsed
                    granted = Await ticket.WaitAsync(remaining, ct).ConfigureAwait(False)
                    If Not granted Then
                        Throw New AiException(AiErrorKind.Timeout, $"Timed out after {sw.Elapsed.TotalSeconds:0}s waiting for local model '{m.Key}' (queue {m.Gate.QueueLength}).")
                    End If
                End If
                Return Await CallModelAsync(ctx, cand, messages, tools, estimatedPrompt, ticket, ct, queueWaitMs:=sw.ElapsedMilliseconds).ConfigureAwait(False)
            End Using
        End Function

        ''' <summary>The provider part of a model id ("yqy/glm-5.3-flash" → "yqy"); Nothing for an id without one.</summary>
        Friend Shared Function ProviderOf(modelId As String) As String
            Dim i = If(modelId, "").IndexOf("/"c)
            Return If(i > 0, modelId.Substring(0, i), Nothing)
        End Function

        ''' <summary>
        ''' The model's upstream account is out of credit: every model of that provider is paused for
        ''' Routing.OutOfCreditPauseMinutes, so no agent keeps calling it; fallbacks, other tiers and the local model take over.
        ''' </summary>
        Private Sub PauseProvider(m As ModelRuntime, failure As AiException, ctx As AiCallContext)
            Dim provider = ProviderOf(m.Options.ModelId)
            Dim pause = _selection.OutOfCreditPause
            Dim newlyPaused As New List(Of String)
            For Each other In _registry.All()
                If other.Options.IsLocal Then Continue For
                Dim same = If(provider Is Nothing, other.Key = m.Key, String.Equals(ProviderOf(other.Options.ModelId), provider, StringComparison.OrdinalIgnoreCase))
                If Not same Then Continue For
                If other.Breaker.State <> CircuitState.Open Then newlyPaused.Add(other.Key)
                other.Breaker.TripFor(pause)
            Next
            If newlyPaused.Count = 0 Then Return
            Dim who = If(provider, m.Key)
            ' Remembered until one of its models answers again or an administrator turns it back on: the dashboard shows it
            ' with a button for that, and the administrator is told once (not again at every retry while it is still empty).
            Dim now = _clock.UtcNow
            Dim isNew = False
            Dim entry = _paused.GetOrAdd(who, Function(k)
                                                  isNew = True
                                                  Return New PausedProvider With {.Provider = k, .SinceUtc = now}
                                              End Function)
            SyncLock entry
                entry.Models = _registry.All().Where(Function(o) Not o.Options.IsLocal AndAlso
                    If(provider Is Nothing, o.Key = m.Key, String.Equals(ProviderOf(o.Options.ModelId), provider, StringComparison.OrdinalIgnoreCase))).Select(Function(o) o.Key).ToList()
                entry.RetryUtc = now + pause
                entry.LastError = TextUtil.Truncate(failure.Message, 300)
                entry.Times += 1
            End SyncLock
            _events.Emit(AgentEventType.ModelHealthChanged, $"Provider {who} is out of credit; its models ({String.Join(", ", newlyPaused)}) are paused for {pause.TotalMinutes:0} min and agents use other providers meanwhile. " &
                         "Add balance, then turn it back on (Models page).",
                         ctx.AgentId, ctx.TaskId, correlationId:=ctx.CorrelationId, level:=EventLevel.Warning,
                         data:=New Dictionary(Of String, Object) From {{"provider", who}, {"models", newlyPaused}, {"minutes", pause.TotalMinutes}, {"error", TextUtil.Truncate(failure.Message, 300)},
                                                                       {"outOfCredit", True}, {"firstTime", isNew}})
            _log.Warn("Gateway", $"Provider {who} is out of credit ({TextUtil.Truncate(failure.Message, 200)}); paused {String.Join(", ", newlyPaused)} for {pause.TotalMinutes:0} min.")
            If isNew Then
                Try
                    OnProviderPaused?.Invoke(entry)
                Catch ex As Exception
                    _log.Warn("Gateway", "Out-of-credit notice failed: " & ex.Message)
                End Try
            End If
        End Sub

        ''' <summary>A provider whose account ran out of credit: its models wait (agents use others) until it works again.</summary>
        Public NotInheritable Class PausedProvider
            Public Property Provider As String
            Public Property Models As New List(Of String)
            Public Property SinceUtc As DateTime
            ''' <summary>When one call tries it again by itself (the pause ends).</summary>
            Public Property RetryUtc As DateTime
            Public Property LastError As String
            ''' <summary>How often it was found empty since it was first paused.</summary>
            Public Property Times As Integer
        End Class

        Private ReadOnly _paused As New ConcurrentDictionary(Of String, PausedProvider)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>Called once when a provider is first found out of credit (the host tells the administrator).</summary>
        Public Property OnProviderPaused As Action(Of PausedProvider)

        ''' <summary>Providers out of credit right now, oldest first.</summary>
        Public Function PausedProviders() As List(Of PausedProvider)
            Return _paused.Values.OrderBy(Function(p) p.SinceUtc).ToList()
        End Function

        ''' <summary>
        ''' Turns a paused provider back on at once (after balance was added): its models' circuits close, so the next calls
        ''' use them again. Returns the models turned back on (empty when none is known by that name).
        ''' </summary>
        Public Function ResumeProvider(provider As String, Optional byWhom As String = Nothing) As List(Of String)
            Dim resumed As New List(Of String)
            If String.IsNullOrWhiteSpace(provider) Then Return resumed
            For Each other In _registry.All()
                If other.Options.IsLocal Then Continue For
                If Not String.Equals(If(ProviderOf(other.Options.ModelId), other.Key), provider, StringComparison.OrdinalIgnoreCase) Then Continue For
                other.Breaker.Reset()
                resumed.Add(other.Key)
            Next
            Dim gone As PausedProvider = Nothing
            _paused.TryRemove(provider, gone)
            If resumed.Count > 0 OrElse gone IsNot Nothing Then
                _events.Emit(AgentEventType.ModelHealthChanged, $"Provider {provider} turned back on{If(String.IsNullOrEmpty(byWhom), "", " by " & byWhom)}: its models ({String.Join(", ", resumed)}) take calls again.",
                             level:=EventLevel.Info, data:=New Dictionary(Of String, Object) From {{"provider", provider}, {"models", resumed}, {"resumed", True}})
                _log.Info("Gateway", $"Provider {provider} turned back on{If(String.IsNullOrEmpty(byWhom), "", " by " & byWhom)} ({String.Join(", ", resumed)}).")
            End If
            Return resumed
        End Function

        ''' <summary>A call to a paused provider's model worked (its pause ended and the trial call went through): it is back.</summary>
        Private Sub ProviderWorksAgain(m As ModelRuntime)
            Dim key = If(ProviderOf(m.Options.ModelId), m.Key)
            Dim gone As PausedProvider = Nothing
            If Not _paused.TryRemove(key, gone) Then Return
            _events.Emit(AgentEventType.ModelHealthChanged, $"Provider {key} works again (a call to {m.Key} went through): its models take calls again.",
                         level:=EventLevel.Info, data:=New Dictionary(Of String, Object) From {{"provider", key}, {"resumed", True}})
            _log.Info("Gateway", $"Provider {key} works again ({m.Key} answered).")
        End Sub

        ''' <summary>
        ''' Another local model (Spillover.LocalCandidates) takes the call when it has a free slot right now, keeping
        ''' LocalKeepFreeSlots for its own work; Nothing when none can (the caller keeps its place in the queue).
        ''' </summary>
        Private Async Function TryLocalSpillAsync(ctx As AiCallContext, source As ModelRuntime, sw As Stopwatch, messages As List(Of ChatMessage), tools As List(Of ToolDefinition),
                                                  estimatedPrompt As Long, ct As CancellationToken) As Task(Of AiCallOutcome)
            For Each lc In _selection.LocalSpilloverCandidates(ctx.Profile, ctx.RequiredCapabilities, ctx.Purpose, source)
                Dim slot = lc.Model.Gate.TryEnterIfFree(ctx.Priority, _selection.LocalKeepFreeSlots)
                If slot Is Nothing Then Continue For
                Dim outcome As AiCallOutcome = Nothing
                Try
                    outcome = Await CallModelAsync(ctx, lc, messages, tools, estimatedPrompt, slot, ct, queueWaitMs:=sw.ElapsedMilliseconds).ConfigureAwait(False)
                Catch ex As AiException When ex.Kind <> AiErrorKind.Cancelled AndAlso ex.Kind <> AiErrorKind.ContextOverflow
                    _log.Dbg("Gateway", $"Local spillover to {lc.Model.Key} not possible: {ex.Kind}", ctx.AgentId, ctx.TaskId)
                Finally
                    slot.Dispose()
                End Try
                If outcome IsNot Nothing Then
                    Interlocked.Increment(_spillovers)
                    _balancer.Note(ctx.TaskId, lc.Model.Key)
                    _events.Emit(AgentEventType.AiSpillover, $"{source.Key} busy; {lc.Model.Key} took the call (local, free)", ctx.AgentId, ctx.TaskId,
                                 correlationId:=ctx.CorrelationId, data:=New Dictionary(Of String, Object) From {
                                    {"from", source.Key}, {"to", lc.Model.Key}, {"costMicros", 0L}, {"waitMs", sw.ElapsedMilliseconds}, {"local", True}})
                    Return outcome
                End If
            Next
            Return Nothing
        End Function

        ''' <summary>
        ''' Spillover.LocalBalance: the local model this task's call belongs on (LocalBalancer). When it is another local model
        ''' with a free slot right now, that model answers; Nothing means the source model's usual path (queue, spillover).
        ''' </summary>
        Private Async Function TryBalancedLocalAsync(ctx As AiCallContext, source As ModelRuntime, sw As Stopwatch, messages As List(Of ChatMessage), tools As List(Of ToolDefinition),
                                                     estimatedPrompt As Long, ct As CancellationToken) As Task(Of AiCallOutcome)
            Dim others = _selection.LocalSpilloverCandidates(ctx.Profile, ctx.RequiredCapabilities, ctx.Purpose, source)
            If others.Count = 0 Then Return Nothing
            Dim keep = _selection.LocalKeepFreeSlots
            Dim choices As New List(Of LocalBalancer.Choice) From {
                New LocalBalancer.Choice With {.Key = source.Key, .Capacity = source.Gate.Capacity, .InUse = source.Gate.InUse, .Waiting = source.Gate.QueueLength}}
            For Each c In others
                choices.Add(New LocalBalancer.Choice With {.Key = c.Model.Key, .Capacity = Math.Max(1, c.Model.Gate.Capacity - keep), .InUse = c.Model.Gate.InUse, .Waiting = c.Model.Gate.QueueLength})
            Next
            Dim key = _balancer.Pick(ctx.TaskId, choices, _selection.LocalBalanceWindow)
            If String.Equals(key, source.Key, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim lc = others.First(Function(c) String.Equals(c.Model.Key, key, StringComparison.OrdinalIgnoreCase))
            Dim slot = lc.Model.Gate.TryEnterIfFree(ctx.Priority, keep)
            If slot Is Nothing Then
                _balancer.Note(ctx.TaskId, source.Key)
                Return Nothing
            End If
            Try
                Return Await CallModelAsync(ctx, lc, messages, tools, estimatedPrompt, slot, ct, queueWaitMs:=sw.ElapsedMilliseconds).ConfigureAwait(False)
            Catch ex As AiException When ex.Kind <> AiErrorKind.Cancelled AndAlso ex.Kind <> AiErrorKind.ContextOverflow
                ' That server failed: this call and the task's next ones go to the source model.
                _log.Dbg("Gateway", $"Balanced call on {lc.Model.Key} failed ({ex.Kind}); {source.Key} takes it", ctx.AgentId, ctx.TaskId)
                _balancer.Note(ctx.TaskId, source.Key)
                Return Nothing
            Finally
                slot.Dispose()
            End Try
        End Function

        ''' <summary>Calls one model (acquiring its gate unless already held), with wallet reservation and retries.</summary>
        Private Async Function CallModelAsync(ctx As AiCallContext, cand As ModelCandidate, messages As List(Of ChatMessage), tools As List(Of ToolDefinition),
                                              estimatedPrompt As Long, heldTicket As GateTicket, ct As CancellationToken, Optional queueWaitMs As Long = 0) As Task(Of AiCallOutcome)
            Dim m = cand.Model
            Dim opt = m.Options
            Dim price = If(cand.Price, _pricing.GetPrice(m.Key))
            Dim maxTokens = If(ctx.MaxTokensOverride, opt.MaxTokens)
            Dim sendTools = If(m.Has(ModelCapability.ToolCalling), tools, Nothing)

            ' Budget reservation for cloud models. Local models are free and never touch a wallet. A subagent's cloud use is
            ' charged to its parent's wallet (BillingAgentId) and limited by the subagent's own spend cap.
            Dim payer = ctx.Profile.BillingAgentId
            Dim spentBy = If(String.Equals(payer, ctx.AgentId, StringComparison.OrdinalIgnoreCase), Nothing, ctx.AgentId)
            Dim reserved As Long = 0
            If Not opt.IsLocal AndAlso _economy.Enabled Then
                If Not price.IsPriced Then Throw New AiException(AiErrorKind.BudgetExceeded, $"Model '{m.Key}' has no price configured.")
                reserved = Math.Max(1, PricingService.CostMicros(price, estimatedPrompt, maxTokens))
                If Not _caps.TryReserve(ctx.AgentId, reserved) Then
                    _events.Emit(AgentEventType.BudgetInsufficient, $"Subagent spend cap reached; cannot use {m.Key} (needs ≈{TextUtil.FormatMicros(reserved)})", ctx.AgentId, ctx.TaskId,
                                 correlationId:=ctx.CorrelationId, level:=EventLevel.Warning,
                                 data:=New Dictionary(Of String, Object) From {{"model", m.Key}, {"neededMicros", reserved}, {"result", "SpendCap"}})
                    Throw New AiException(AiErrorKind.BudgetExceeded, $"Subagent spend cap reached for {m.Key}.")
                End If
                Dim rr = Await _wallets.TryReserveAsync(payer, reserved).ConfigureAwait(False)
                If rr <> ReserveResult.Ok Then
                    _caps.Release(ctx.AgentId, reserved)
                    _events.Emit(AgentEventType.BudgetInsufficient, $"Cannot afford {m.Key} (needs ≈{TextUtil.FormatMicros(reserved)}): {rr}" & If(spentBy Is Nothing, "", $" [wallet of {payer}]"),
                                 ctx.AgentId, ctx.TaskId, correlationId:=ctx.CorrelationId, level:=EventLevel.Warning,
                                 data:=New Dictionary(Of String, Object) From {{"model", m.Key}, {"neededMicros", reserved}, {"result", rr.ToString()}, {"payer", payer}})
                    Throw New AiException(AiErrorKind.BudgetExceeded, $"Insufficient budget for {m.Key}: {rr}")
                End If
            End If

            Dim ownTicket As GateTicket = Nothing
            Dim settled = False
            Try
                Dim waitSw = Stopwatch.StartNew()
                If heldTicket Is Nothing Then
                    ownTicket = m.Gate.Enter(ctx.Priority)
                    If Not Await ownTicket.WaitAsync(_selection.QueueTimeout, ct).ConfigureAwait(False) Then
                        Throw New AiException(AiErrorKind.Timeout, $"Timed out waiting for a '{m.Key}' slot.")
                    End If
                End If
                Await _global.WaitAsync(ct).ConfigureAwait(False)
                Dim totalQueueMs = queueWaitMs + waitSw.ElapsedMilliseconds
                Try
                    Dim provider = GetProvider(opt.Provider)
                    Dim request As New ChatRequest With {
                        .ModelId = opt.ModelId, .Messages = messages, .Tools = sendTools, .MaxTokens = maxTokens,
                        .Temperature = opt.Temperature, .Stream = True, .Timeout = TimeSpan.FromSeconds(Math.Max(10, opt.TimeoutSeconds))}
                    Dim callId = Ids.NewId("ai")
                    _events.Emit(AgentEventType.AiCallStarted, $"AI request → {m.Key} ({ctx.Purpose})", ctx.AgentId, ctx.TaskId, correlationId:=ctx.CorrelationId,
                                 data:=New Dictionary(Of String, Object) From {{"model", m.Key}, {"reason", cand.Reason.ToString()}, {"queueMs", totalQueueMs}})
                    Dim attempt = 0
                    While True
                        attempt += 1
                        Dim sw = Stopwatch.StartNew()
                        Dim response As ChatResponse = Nothing
                        Dim err As AiException = Nothing
                        Try
                            response = Await provider.ChatAsync(request, ct).ConfigureAwait(False)
                            If response.IsTruncatedEmpty AndAlso Not response.HasToolCalls AndAlso String.IsNullOrEmpty(response.ReasoningContent) Then
                                Throw New AiException(AiErrorKind.InvalidResponse, "Model returned an empty response.")
                            End If
                        Catch ex As AiException
                            err = ex
                        Catch ex As OperationCanceledException
                            err = New AiException(AiErrorKind.Cancelled, "AI call cancelled.", 0, ex)
                        End Try
                        sw.Stop()
                        If err Is Nothing Then
                            m.Breaker.RecordSuccess()
                            m.RecordCall(True, sw.ElapsedMilliseconds, Nothing)
                            If Not _paused.IsEmpty Then ProviderWorksAgain(m)
                            Dim usage = ResolveUsage(response, estimatedPrompt)
                            ' A prompt the provider counted and answered: the model's window is at least that big.
                            If Not opt.IsLocal AndAlso Not usage.Estimated Then _registry.Limits?.RecordAccepted(opt.ModelId, usage.Prompt, "agents")
                            Dim cost = If(opt.IsLocal, 0L, PricingService.CostMicros(price, usage.Prompt, usage.Completion, usage.Cached))
                            If reserved > 0 OrElse cost > 0 Then
                                _caps.Settle(ctx.AgentId, reserved, cost)
                                Await _wallets.SettleAsync(payer, reserved, cost, New ChargeDetails With {
                                    .ModelKey = m.Key, .AiCallId = callId, .TaskId = ctx.TaskId, .CorrelationId = ctx.CorrelationId, .SpentByAgentId = spentBy,
                                    .Description = $"{ctx.Purpose} via {m.Key} ({usage.Prompt}+{usage.Completion} tokens{If(usage.Cached > 0, $", {usage.Cached} cached", "")})" & If(spentBy Is Nothing, "", $" by subagent {spentBy}")}).ConfigureAwait(False)
                                If cost > 0 AndAlso AgentCharged IsNot Nothing Then
                                    Try
                                        Await AgentCharged(payer, cost, m.Key, $"{ctx.Purpose} via {m.Key} ({usage.Prompt}+{usage.Completion} tokens{If(usage.Cached > 0, $", {usage.Cached} cached", "")})" & If(spentBy Is Nothing, "", $" by subagent {spentBy}")).ConfigureAwait(False)
                                    Catch ex As Exception
                                        _log.Warn("Gateway", "Could not charge the agent's owner: " & ex.Message, ctx.AgentId)
                                    End Try
                                End If
                            End If
                            settled = True
                            ' Token limits cover cloud use only: the local models are free, and the agent's wallet already
                            ' bounds what cloud use costs (three LedgerLite agents stopped for hours on free local tokens).
                            _usage.Record(payer, If(opt.IsLocal, 0L, usage.Prompt + usage.Completion))
                            Await RecordAsync(callId, ctx, m, cand.Reason, totalQueueMs, sw.ElapsedMilliseconds, True, Nothing, usage, cost, price, response, messages).ConfigureAwait(False)
                            _events.Emit(AgentEventType.AiCallCompleted, $"AI response from {m.Key} in {sw.ElapsedMilliseconds}ms" & If(cost > 0, $" ({TextUtil.FormatMicros(cost)})", ""),
                                         ctx.AgentId, ctx.TaskId, correlationId:=ctx.CorrelationId,
                                         data:=New Dictionary(Of String, Object) From {
                                            {"model", m.Key}, {"success", True}, {"durationMs", sw.ElapsedMilliseconds}, {"tokens", usage.Prompt + usage.Completion},
                                            {"costMicros", cost}, {"reason", cand.Reason.ToString()}})
                            If Health IsNot Nothing Then
                                Dim unused = Health.RecordSpeedAsync(opt.ModelId, Nothing, "agents", response.FirstTokenMs, sw.ElapsedMilliseconds, usage.Completion)
                            End If
                            Return New AiCallOutcome With {
                                .Response = response, .ModelKey = m.Key, .ModelId = opt.ModelId, .Tier = opt.Tier, .Reason = cand.Reason, .CostMicros = cost,
                                .QueueWaitMs = totalQueueMs, .UsageEstimated = usage.Estimated, .AiCallId = callId, .ContextWindowTokens = opt.ContextWindowTokens,
                                .PromptTokens = usage.Prompt, .CompletionTokens = usage.Completion}
                        End If

                        ' Failure handling
                        m.RecordCall(False, sw.ElapsedMilliseconds, err.Message)
                        If err.Kind = AiErrorKind.ModelUnavailable OrElse err.Kind = AiErrorKind.Transient OrElse err.Kind = AiErrorKind.Timeout Then m.Breaker.RecordFailure()
                        Await RecordAsync(callId & "-" & attempt.ToString(), ctx, m, cand.Reason, totalQueueMs, sw.ElapsedMilliseconds, False, err, Nothing, 0, price, Nothing, messages).ConfigureAwait(False)
                        _events.Emit(AgentEventType.AiCallCompleted, $"AI call to {m.Key} failed: {err.Kind}", ctx.AgentId, ctx.TaskId, correlationId:=ctx.CorrelationId,
                                     level:=EventLevel.Warning, data:=New Dictionary(Of String, Object) From {
                                        {"model", m.Key}, {"success", False}, {"errorKind", err.Kind.ToString()}, {"durationMs", sw.ElapsedMilliseconds}})
                        If err.Kind = AiErrorKind.Cancelled Then Throw New OperationCanceledException(ct)
                        If err.IsRetryable AndAlso attempt <= _providerRetries AndAlso m.Breaker.State <> CircuitState.Open Then
                            Dim delay = Backoff.Compute(attempt - 1, TimeSpan.FromSeconds(If(err.Kind = AiErrorKind.RateLimited, 5, 2)), TimeSpan.FromSeconds(60))
                            Await Task.Delay(delay, ct).ConfigureAwait(False)
                            Continue While
                        End If
                        Throw err
                    End While
                    Throw New InvalidOperationException("unreachable")
                Finally
                    _global.Release()
                End Try
            Finally
                ownTicket?.Dispose()
                If Not settled AndAlso reserved > 0 Then
                    ' Release reservation without awaiting inside Finally (VB restriction); fire-and-log.
                    Dim agentId = ctx.AgentId
                    Dim r = reserved
                    _caps.Release(agentId, r)
                    Dim releaseTask = _wallets.ReleaseAsync(payer, r).ContinueWith(
                        Sub(t)
                            If t.IsFaulted Then _log.Warn("Gateway", "Failed to release reservation", agentId, ex:=t.Exception)
                        End Sub, TaskScheduler.Default)
                End If
            End Try
        End Function

        Private Function ResolveUsage(response As ChatResponse, estimatedPrompt As Long) As (Prompt As Long, Completion As Long, Estimated As Boolean, Cached As Long)
            Dim estimated = False
            Dim prompt As Long
            Dim cached As Long = 0
            If response.Usage.PromptTokens.HasValue AndAlso response.Usage.PromptTokens.Value >= CLng(estimatedPrompt * _economy.UsageEstimateFloorRatio) Then
                prompt = response.Usage.PromptTokens.Value
                cached = Math.Min(prompt, Math.Max(0L, response.Usage.CachedTokens.GetValueOrDefault()))
            Else
                prompt = estimatedPrompt
                estimated = True
            End If
            Dim completion As Long
            If response.Usage.CompletionTokens.HasValue AndAlso response.Usage.CompletionTokens.Value > 0 Then
                completion = response.Usage.CompletionTokens.Value
            Else
                Dim chars = If(response.Content, "").Length + If(response.ReasoningContent, "").Length
                For Each tc In response.ToolCalls
                    chars += If(tc.ArgumentsJson, "").Length + If(tc.Name, "").Length
                Next
                completion = CLng(Math.Ceiling(chars / 3.5))
                estimated = True
            End If
            Return (prompt, completion, estimated, cached)
        End Function

        Private Async Function RecordAsync(callId As String, ctx As AiCallContext, m As ModelRuntime, reason As AiCallReason, queueMs As Long, durationMs As Long, success As Boolean,
                                           err As AiException, usage As (Prompt As Long, Completion As Long, Estimated As Boolean, Cached As Long)?, cost As Long, price As ModelPrice,
                                           response As ChatResponse, messages As List(Of ChatMessage)) As Task
            Try
                Dim rec As New AiCallRecord With {
                    .Id = callId, .AgentId = ctx.AgentId, .TaskId = ctx.TaskId, .CorrelationId = ctx.CorrelationId, .ModelKey = m.Key, .ModelId = m.Options.ModelId,
                    .Purpose = ctx.Purpose.ToString(), .Tier = m.Options.Tier, .Reason = reason, .QueueWaitMs = queueMs, .DurationMs = durationMs, .Success = success,
                    .ErrorKind = err?.Kind.ToString(), .ErrorMessage = err?.Message,
                    .PromptTokens = If(usage.HasValue, usage.Value.Prompt, 0L), .CompletionTokens = If(usage.HasValue, usage.Value.Completion, 0L),
                    .ReasoningTokens = If(response?.Usage?.ReasoningTokens, 0L), .UsageEstimated = usage.HasValue AndAlso usage.Value.Estimated,
                    .CostMicros = cost, .InputPricePerM = price?.InputPerMillion, .OutputPricePerM = price?.OutputPerMillion,
                    .FinishReason = response?.FinishReason, .CreatedUtc = _clock.UtcNow}
                If _storeContent Then
                    Dim lastUser = messages.LastOrDefault(Function(x) x.Role = "user" OrElse x.Role = "tool")
                    rec.RequestPreview = TextUtil.Truncate(lastUser?.Content, 2000)
                    rec.ResponsePreview = TextUtil.Truncate(If(response?.Content, String.Join(", ", If(response?.ToolCalls, New List(Of ToolCallRequest)).Select(Function(t) t.Name))), 2000)
                End If
                Await _telemetry.InsertAiCallAsync(rec).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Gateway", "Failed to record AI call", ctx.AgentId, ex:=ex)
            End Try
        End Function

        ''' <summary>Snapshot for the dashboard: per-model health, gate usage and queue depth.</summary>
        Public Function ModelStatus() As List(Of Dictionary(Of String, Object))
            Return _registry.All().Select(Function(m) New Dictionary(Of String, Object) From {
                {"key", m.Key}, {"modelId", m.Options.ModelId}, {"enabled", m.Options.Enabled}, {"tier", m.Options.Tier}, {"isLocal", m.Options.IsLocal},
                {"isCustom", m.Options.IsCustom}, {"description", m.Options.Description}, {"useFor", m.Options.UseFor},
                {"maxTokens", m.Options.MaxTokens}, {"contextWindowTokens", m.Options.ContextWindowTokens}, {"contextWindowLearned", If(m.ContextWindow <> m.Options.ContextWindowTokens, m.ContextWindow, 0)}, {"maxConcurrency", m.Options.MaxConcurrency},
                {"capabilities", m.Options.Capabilities.Select(Function(c) c.ToString()).ToList()},
                {"purposes", m.Options.AllowedPurposes.Select(Function(p) p.ToString()).ToList()},
                {"circuit", m.Breaker.State.ToString()}, {"consecutiveFailures", m.Breaker.ConsecutiveFailures},
                {"slotsInUse", m.Gate.InUse}, {"slotsCapacity", m.Gate.Capacity}, {"queueLength", m.Gate.QueueLength}, {"chatSlots", m.ChatSlots},
                {"calls", m.Calls}, {"failures", m.Failures}, {"avgLatencyMs", Math.Round(m.AverageLatencyMs)},
                {"lastError", m.LastError}, {"lastErrorUtc", m.LastErrorUtc}, {"lastSuccessUtc", m.LastSuccessUtc},
                {"fallbacks", m.Options.Fallbacks}}).ToList()
        End Function
    End Class

End Namespace
