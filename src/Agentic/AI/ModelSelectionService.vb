Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Configuration
Imports Agentic.Economy

Namespace AI

    Public NotInheritable Class ModelCandidate
        Public Property Model As ModelRuntime
        Public Property Reason As AiCallReason
        Public Property Price As ModelPrice
    End Class

    ''' <summary>
    ''' Decides whether AI is needed, which purpose applies and which models an agent may use at each escalation level.
    ''' Enforces agent model permissions (AllowedModels, MaxTier), capabilities and pricing — the model cannot
    ''' influence any of these decisions.
    ''' </summary>
    Public NotInheritable Class ModelSelectionService
        Private ReadOnly _registry As ModelRegistry
        Private ReadOnly _routing As RoutingOptions
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _economy As EconomyOptions
        Private ReadOnly _access As ModelAccessService

        Public Sub New(registry As ModelRegistry, routing As RoutingOptions, pricing As PricingService, economy As EconomyOptions, Optional access As ModelAccessService = Nothing)
            _registry = registry
            _routing = routing
            _pricing = pricing
            _economy = economy
            _access = access
        End Sub

        Public ReadOnly Property Access As ModelAccessService
            Get
                Return _access
            End Get
        End Property

        ''' <summary>How a profile may use a model: via its profile, via an admin grant, or not at all.</summary>
        Public Function AccessFor(m As ModelRuntime, profile As EffectiveProfile) As ModelAccessKind
            If m Is Nothing OrElse profile Is Nothing Then Return ModelAccessKind.None
            If _access IsNot Nothing Then Return _access.AccessFor(profile, m)
            Return If(Not m.Options.IsCustom AndAlso profile.AllowedModels.Contains(m.Key), ModelAccessKind.Profile, ModelAccessKind.None)
        End Function

        ''' <summary>Deterministic handlers never call a model; only reasoning work does.</summary>
        Public Shared Function RequiresAi(handler As String) As Boolean
            Return String.Equals(If(handler, "reasoning"), "reasoning", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function ResolvePurpose(taskPurpose As AiPurpose?, profile As EffectiveProfile) As AiPurpose
            Return If(taskPurpose, profile.DefaultPurpose)
        End Function

        ''' <summary>Escalation ladder (model keys by level) for a purpose.</summary>
        Public Function GetLadder(purpose As AiPurpose) As List(Of String)
            Dim ladder As List(Of String) = Nothing
            If _routing.Ladders.TryGetValue(purpose.ToString(), ladder) AndAlso ladder IsNot Nothing AndAlso ladder.Count > 0 Then Return ladder
            If _routing.Ladders.TryGetValue("Default", ladder) AndAlso ladder IsNot Nothing AndAlso ladder.Count > 0 Then Return ladder
            ' No ladder configured: every enabled model that allows the purpose, by tier then priority.
            Return _registry.All().Where(Function(m) m.Options.Enabled AndAlso m.Options.AllowedPurposes.Contains(purpose)).
                OrderBy(Function(m) m.Options.Tier).ThenBy(Function(m) m.Options.Priority).Select(Function(m) m.Key).ToList()
        End Function

        ''' <summary>Models an administrator turned off for everyone (Model control), by model id or key; agents do not use them.</summary>
        Public Property TurnedOff As Func(Of String, Boolean)

        ''' <param name="accessVia">A model whose stand-in <paramref name="m"/> is (the primary it is a fallback of, or the local
        ''' model it takes spillover for): the agent may use <paramref name="m"/> when it may use that one, so a fallback on
        ''' another provider needs no grant of its own.</param>
        Public Function IsUsableBy(m As ModelRuntime, profile As EffectiveProfile, required As ICollection(Of ModelCapability), Optional purpose As AiPurpose? = Nothing,
                                   Optional accessVia As ModelRuntime = Nothing) As Boolean
            If m Is Nothing OrElse Not m.Options.Enabled Then Return False
            If TurnedOff IsNot Nothing AndAlso (TurnedOff(m.Options.ModelId) OrElse TurnedOff(m.Key)) Then Return False
            If purpose.HasValue AndAlso m.Options.AllowedPurposes.Count > 0 AndAlso Not m.Options.AllowedPurposes.Contains(purpose.Value) Then Return False
            Dim accessModel = If(accessVia, m)
            Dim kind = AccessFor(accessModel, profile)
            If kind = ModelAccessKind.None Then Return False
            ' MaxTier limits profile models; an explicit admin grant is a deliberate exception to it.
            If kind = ModelAccessKind.Profile AndAlso accessModel.Options.Tier > profile.MaxTier Then Return False
            If required IsNot Nothing Then
                For Each c In required
                    If Not m.Has(c) Then Return False
                Next
            End If
            If Not m.Options.IsLocal AndAlso _economy.Enabled AndAlso _economy.RequirePricingForCloudModels AndAlso Not _pricing.GetPrice(m.Key).IsPriced Then Return False
            Return True
        End Function

        ''' <summary>Primary model for the level followed by its fallbacks, filtered to what this agent may use.</summary>
        Public Function CandidatesAt(profile As EffectiveProfile, purpose As AiPurpose, level As Integer, required As ICollection(Of ModelCapability)) As List(Of ModelCandidate)
            Dim result As New List(Of ModelCandidate)
            Dim ladder = GetLadder(purpose)
            If level < 0 OrElse level >= ladder.Count Then Return result
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim primary = _registry.TryGet(ladder(level))
            AddCandidate(result, seen, primary, profile, required, AiCallReason.Primary, purpose)
            If primary IsNot Nothing Then
                ' Fallbacks (the same kind of model at other providers) stand in for the primary: usable by whoever may use it.
                For Each fk In primary.Options.Fallbacks
                    AddCandidate(result, seen, _registry.TryGet(fk), profile, required, AiCallReason.Fallback, purpose, primary)
                Next
            End If
            Return result
        End Function

        Private Sub AddCandidate(list As List(Of ModelCandidate), seen As HashSet(Of String), m As ModelRuntime, profile As EffectiveProfile, required As ICollection(Of ModelCapability), reason As AiCallReason, purpose As AiPurpose,
                                 Optional accessVia As ModelRuntime = Nothing)
            If m Is Nothing OrElse Not seen.Add(m.Key) Then Return
            If Not IsUsableBy(m, profile, required, purpose, accessVia) Then Return
            list.Add(New ModelCandidate With {.Model = m, .Reason = If(list.Count = 0 AndAlso reason = AiCallReason.Fallback, AiCallReason.Fallback, reason), .Price = _pricing.GetPrice(m.Key)})
        End Sub

        ''' <summary>First level with at least one usable model, at or above the requested starting tier.</summary>
        Public Function InitialLevel(profile As EffectiveProfile, purpose As AiPurpose, required As ICollection(Of ModelCapability), Optional startTier As Integer? = Nothing) As Integer?
            Dim ladder = GetLadder(purpose)
            For i = 0 To ladder.Count - 1
                Dim m = _registry.TryGet(ladder(i))
                If startTier.HasValue AndAlso m IsNot Nothing AndAlso m.Options.Tier < startTier.Value Then Continue For
                If CandidatesAt(profile, purpose, i, required).Count > 0 Then Return i
            Next
            ' Requested tier not reachable: fall back to anything usable.
            If startTier.HasValue Then Return InitialLevel(profile, purpose, required, Nothing)
            Return Nothing
        End Function

        ''' <summary>Next escalation level above the current one that this agent can actually use.</summary>
        Public Function NextLevel(profile As EffectiveProfile, purpose As AiPurpose, current As Integer, required As ICollection(Of ModelCapability)) As Integer?
            Dim ladder = GetLadder(purpose)
            For i = current + 1 To ladder.Count - 1
                If CandidatesAt(profile, purpose, i, required).Count > 0 Then Return i
            Next
            Return Nothing
        End Function

        ''' <summary>Spillover targets for the local model, cheapest first (by combined per-million price).</summary>
        Public Function SpilloverCandidates(profile As EffectiveProfile, required As ICollection(Of ModelCapability), purpose As AiPurpose) As List(Of ModelCandidate)
            Dim sp = _routing.Spillover
            Dim list As New List(Of ModelCandidate)
            If Not sp.Enabled Then Return list
            For Each k In sp.Candidates
                Dim m = _registry.TryGet(k)
                If m Is Nothing OrElse m.Options.IsLocal OrElse Not IsUsableBy(m, profile, required, purpose) Then Continue For
                If m.Breaker.State = CircuitState.Open Then Continue For
                list.Add(New ModelCandidate With {.Model = m, .Reason = AiCallReason.Spillover, .Price = _pricing.GetPrice(m.Key)})
            Next
            Return list.OrderBy(Function(c) c.Price.InputPerMillion.GetValueOrDefault() + c.Price.OutputPerMillion.GetValueOrDefault()).ToList()
        End Function

        ''' <summary>
        ''' Other local models that may take the source local model's work while it is full (Spillover.LocalCandidates), in
        ''' order; an agent that may use the source may use them for this.
        ''' </summary>
        Public Function LocalSpilloverCandidates(profile As EffectiveProfile, required As ICollection(Of ModelCapability), purpose As AiPurpose, source As ModelRuntime) As List(Of ModelCandidate)
            Dim list As New List(Of ModelCandidate)
            If Not _routing.Spillover.Enabled Then Return list
            For Each k In _routing.Spillover.LocalCandidates
                Dim m = _registry.TryGet(k)
                If m Is Nothing OrElse Not m.Options.IsLocal OrElse (source IsNot Nothing AndAlso m.Key = source.Key) Then Continue For
                If m.Breaker.State = CircuitState.Open OrElse Not IsUsableBy(m, profile, required, purpose, source) Then Continue For
                list.Add(New ModelCandidate With {.Model = m, .Reason = AiCallReason.Spillover, .Price = _pricing.GetPrice(m.Key)})
            Next
            Return list
        End Function

        ''' <summary>Slots a local spillover candidate keeps for its own work.</summary>
        Public ReadOnly Property LocalKeepFreeSlots As Integer
            Get
                Return Math.Max(0, _routing.Spillover.LocalKeepFreeSlots)
            End Get
        End Property

        ''' <summary>Whether agent work is shared between the local models all the time (Spillover.LocalBalance).</summary>
        Public ReadOnly Property LocalBalance As Boolean
            Get
                Return _routing.Spillover.Enabled AndAlso _routing.Spillover.LocalBalance
            End Get
        End Property

        ''' <summary>How long a task counts as working on a local model after its last call.</summary>
        Public ReadOnly Property LocalBalanceWindow As TimeSpan
            Get
                Return TimeSpan.FromSeconds(Math.Max(30, _routing.Spillover.LocalBalanceWindowSeconds))
            End Get
        End Property

        ''' <summary>How long a provider out of credit is paused (Routing.OutOfCreditPauseMinutes).</summary>
        Public ReadOnly Property OutOfCreditPause As TimeSpan
            Get
                Return TimeSpan.FromMinutes(Math.Max(1, _routing.OutOfCreditPauseMinutes))
            End Get
        End Property

        ''' <summary>Whether a level of the ladder has a model this agent may use whose circuit is not open.</summary>
        Public Function LevelHasWorkingModel(profile As EffectiveProfile, purpose As AiPurpose, level As Integer, required As ICollection(Of ModelCapability)) As Boolean
            Return CandidatesAt(profile, purpose, level, required).Any(Function(c) c.Model.Breaker.State <> CircuitState.Open)
        End Function

        ''' <summary>
        ''' Another level of the ladder with a working model, for when the current level's models are out (provider out of
        ''' credit, down): the nearest cheaper level first, down to the bottom (the free local model), then higher ones.
        ''' </summary>
        Public Function OtherWorkingLevel(profile As EffectiveProfile, purpose As AiPurpose, current As Integer, required As ICollection(Of ModelCapability)) As Integer?
            Dim count = GetLadder(purpose).Count
            For i = current - 1 To 0 Step -1
                If LevelHasWorkingModel(profile, purpose, i, required) Then Return i
            Next
            For i = current + 1 To count - 1
                If LevelHasWorkingModel(profile, purpose, i, required) Then Return i
            Next
            Return Nothing
        End Function

        Public Function IsSpilloverSource(modelKey As String) As Boolean
            Return _routing.Spillover.Enabled AndAlso String.Equals(_routing.Spillover.FromModel, modelKey, StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function SpillAfter(priority As PriorityLevel) As TimeSpan
            Dim sp = _routing.Spillover
            Select Case priority
                Case PriorityLevel.Critical : Return TimeSpan.FromSeconds(sp.CriticalAfterSeconds)
                Case PriorityLevel.High : Return TimeSpan.FromSeconds(sp.HighAfterSeconds)
                Case PriorityLevel.Low : Return TimeSpan.FromSeconds(sp.LowAfterSeconds)
                Case Else : Return TimeSpan.FromSeconds(sp.NormalAfterSeconds)
            End Select
        End Function

        Public ReadOnly Property QueueTimeout As TimeSpan
            Get
                Return TimeSpan.FromSeconds(Math.Max(10, _routing.Spillover.QueueTimeoutSeconds))
            End Get
        End Property
    End Class

End Namespace
