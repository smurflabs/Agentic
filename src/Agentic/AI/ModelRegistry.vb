Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace AI

    Public Enum CircuitState
        Closed
        Open
        HalfOpen
    End Enum

    ''' <summary>Per-model circuit breaker: after N consecutive failures the model is skipped for a cooldown, then probed once.</summary>
    Public NotInheritable Class CircuitBreaker
        Private ReadOnly _lock As New Object()
        Private ReadOnly _threshold As Integer
        Private ReadOnly _openDuration As TimeSpan
        Private ReadOnly _clock As IClock
        Private _state As CircuitState = CircuitState.Closed
        Private _failures As Integer
        Private _openedUtc As DateTime
        ''' <summary>How long the current opening lasts: the configured duration, or longer after TripFor.</summary>
        Private _openFor As TimeSpan
        Private _halfOpenTrialInFlight As Boolean
        Private ReadOnly _onChange As Action(Of CircuitState, CircuitState)

        Public Sub New(threshold As Integer, openDuration As TimeSpan, clock As IClock, Optional onChange As Action(Of CircuitState, CircuitState) = Nothing)
            _threshold = Math.Max(1, threshold)
            _openDuration = openDuration
            _openFor = openDuration
            _clock = clock
            _onChange = onChange
        End Sub

        Public ReadOnly Property State As CircuitState
            Get
                SyncLock _lock
                    If _state = CircuitState.Open AndAlso _clock.UtcNow - _openedUtc >= _openFor Then Return CircuitState.HalfOpen
                    Return _state
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property ConsecutiveFailures As Integer
            Get
                SyncLock _lock
                    Return _failures
                End SyncLock
            End Get
        End Property

        Public Function AllowRequest() As Boolean
            Dim changed As (CircuitState, CircuitState)? = Nothing
            Dim allowed As Boolean
            SyncLock _lock
                Select Case _state
                    Case CircuitState.Closed
                        allowed = True
                    Case CircuitState.Open
                        If _clock.UtcNow - _openedUtc >= _openFor Then
                            _state = CircuitState.HalfOpen
                            _halfOpenTrialInFlight = True
                            changed = (CircuitState.Open, CircuitState.HalfOpen)
                            allowed = True
                        Else
                            allowed = False
                        End If
                    Case Else
                        If _halfOpenTrialInFlight Then
                            allowed = False
                        Else
                            _halfOpenTrialInFlight = True
                            allowed = True
                        End If
                End Select
            End SyncLock
            If changed.HasValue Then _onChange?.Invoke(changed.Value.Item1, changed.Value.Item2)
            Return allowed
        End Function

        Public Sub RecordSuccess()
            Dim prev As CircuitState
            SyncLock _lock
                prev = _state
                _state = CircuitState.Closed
                _failures = 0
                _halfOpenTrialInFlight = False
                _openFor = _openDuration
            End SyncLock
            If prev <> CircuitState.Closed Then _onChange?.Invoke(prev, CircuitState.Closed)
        End Sub

        ''' <summary>
        ''' Opens the circuit now for at least <paramref name="duration"/> (an upstream account out of credit): no requests
        ''' until then; afterwards one trial request decides as usual.
        ''' </summary>
        Public Sub TripFor(duration As TimeSpan)
            Dim prev As CircuitState
            SyncLock _lock
                prev = _state
                _state = CircuitState.Open
                _openedUtc = _clock.UtcNow
                _openFor = If(duration > _openDuration, duration, _openDuration)
                _halfOpenTrialInFlight = False
            End SyncLock
            If prev <> CircuitState.Open Then _onChange?.Invoke(prev, CircuitState.Open)
        End Sub

        Public Sub RecordFailure()
            Dim prev As CircuitState
            Dim opened = False
            SyncLock _lock
                prev = _state
                _failures += 1
                _halfOpenTrialInFlight = False
                If _state = CircuitState.HalfOpen OrElse _failures >= _threshold Then
                    If _state <> CircuitState.Open Then opened = True
                    _state = CircuitState.Open
                    _openedUtc = _clock.UtcNow
                    _openFor = _openDuration
                End If
            End SyncLock
            If opened Then _onChange?.Invoke(prev, CircuitState.Open)
        End Sub

        ''' <summary>Admin reset.</summary>
        Public Sub Reset()
            RecordSuccess()
        End Sub
    End Class

    ''' <summary>
    ''' Bounded concurrency gate with priority ordering and aging. Used for the local model (4 slots shared by up to
    ''' 50 agents) and per-model limits on cloud models. Waiters keep their queue position while the caller decides
    ''' whether to spill over to another model.
    ''' </summary>
    Public NotInheritable Class PriorityConcurrencyGate
        Private ReadOnly _lock As New Object()
        Private ReadOnly _capacity As Integer
        Private _inUse As Integer
        Private ReadOnly _waiters As New List(Of GateTicket)
        Private _sequence As Long
        Private ReadOnly _agingSeconds As Integer
        Private ReadOnly _clock As IClock

        Public Sub New(capacity As Integer, agingSeconds As Integer, clock As IClock)
            _capacity = Math.Max(1, capacity)
            _agingSeconds = Math.Max(0, agingSeconds)
            _clock = clock
        End Sub

        Public ReadOnly Property Capacity As Integer
            Get
                Return _capacity
            End Get
        End Property

        Public ReadOnly Property InUse As Integer
            Get
                SyncLock _lock
                    Return _inUse
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property QueueLength As Integer
            Get
                SyncLock _lock
                    Return _waiters.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>Joins the queue (or takes a free slot immediately). Always dispose the ticket.</summary>
        Public Function Enter(priority As PriorityLevel) As GateTicket
            Dim t As New GateTicket(Me, priority, Interlocked.Increment(_sequence), _clock.UtcNow)
            SyncLock _lock
                If _inUse < _capacity AndAlso _waiters.Count = 0 Then
                    _inUse += 1
                    t.MarkGranted()
                Else
                    _waiters.Add(t)
                End If
            End SyncLock
            Return t
        End Function

        ''' <summary>
        ''' A slot only when one is free right now, leaving <paramref name="keepFree"/> slots for others, and nobody is
        ''' waiting; else Nothing (never queues). Dispose the ticket to release the slot.
        ''' </summary>
        Public Function TryEnterIfFree(priority As PriorityLevel, keepFree As Integer) As GateTicket
            Dim t As New GateTicket(Me, priority, Interlocked.Increment(_sequence), _clock.UtcNow)
            SyncLock _lock
                If _waiters.Count > 0 OrElse _inUse >= _capacity - Math.Max(0, keepFree) Then Return Nothing
                _inUse += 1
            End SyncLock
            t.MarkGranted()
            Return t
        End Function

        Private Function EffectivePriority(t As GateTicket, now As DateTime) As Integer
            Dim p = CInt(t.Priority)
            If _agingSeconds > 0 Then p += CInt(Math.Floor((now - t.EnqueuedUtc).TotalSeconds / _agingSeconds))
            Return Math.Min(p, CInt(PriorityLevel.Critical) + 1)
        End Function

        ''' <summary>Releases a slot and hands it to the best waiter (highest effective priority, then FIFO).</summary>
        Friend Sub Release()
            Dim nextTicket As GateTicket = Nothing
            SyncLock _lock
                If _waiters.Count > 0 Then
                    Dim now = _clock.UtcNow
                    Dim best = _waiters(0)
                    Dim bestP = EffectivePriority(best, now)
                    For i = 1 To _waiters.Count - 1
                        Dim w = _waiters(i)
                        Dim p = EffectivePriority(w, now)
                        If p > bestP OrElse (p = bestP AndAlso w.Sequence < best.Sequence) Then
                            best = w
                            bestP = p
                        End If
                    Next
                    _waiters.Remove(best)
                    nextTicket = best
                Else
                    _inUse = Math.Max(0, _inUse - 1)
                End If
            End SyncLock
            nextTicket?.MarkGranted()
        End Sub

        ''' <summary>How many waiters go before this ticket (0 = it is next), or -1 when it is not waiting.</summary>
        Public Function PositionOf(t As GateTicket) As Integer
            SyncLock _lock
                If Not _waiters.Contains(t) Then Return -1
                Dim now = _clock.UtcNow
                Dim mine = EffectivePriority(t, now)
                Return _waiters.Where(Function(w) w IsNot t AndAlso (EffectivePriority(w, now) > mine OrElse (EffectivePriority(w, now) = mine AndAlso w.Sequence < t.Sequence))).Count()
            End SyncLock
        End Function

        ''' <summary>Removes a waiting ticket. Returns False if it had already been granted (caller then owns a slot).</summary>
        Friend Function TryRemove(t As GateTicket) As Boolean
            SyncLock _lock
                Return _waiters.Remove(t)
            End SyncLock
        End Function
    End Class

    Public NotInheritable Class GateTicket
        Implements IDisposable

        Private ReadOnly _gate As PriorityConcurrencyGate
        Private ReadOnly _tcs As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        Private _released As Integer

        Public ReadOnly Property Priority As PriorityLevel
        Public ReadOnly Property Sequence As Long
        Public ReadOnly Property EnqueuedUtc As DateTime

        Friend Sub New(gate As PriorityConcurrencyGate, priority As PriorityLevel, sequence As Long, enqueuedUtc As DateTime)
            _gate = gate
            Me.Priority = priority
            Me.Sequence = sequence
            Me.EnqueuedUtc = enqueuedUtc
        End Sub

        Friend Sub MarkGranted()
            _tcs.TrySetResult(True)
        End Sub

        Public ReadOnly Property IsGranted As Boolean
            Get
                Return _tcs.Task.IsCompleted
            End Get
        End Property

        ''' <summary>Waits up to timeout for a slot. False on timeout (the ticket stays queued).</summary>
        Public Async Function WaitAsync(timeout As TimeSpan, ct As CancellationToken) As Task(Of Boolean)
            If _tcs.Task.IsCompleted Then Return True
            If timeout <= TimeSpan.Zero Then Return False
            Try
                Await _tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(False)
                Return True
            Catch ex As TimeoutException
                Return _tcs.Task.IsCompleted
            End Try
        End Function

        ''' <summary>Leaves the queue, or releases the slot if one was granted.</summary>
        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_released, 1) = 1 Then Return
            If _gate.TryRemove(Me) Then Return
            ' Not in the queue: either granted or about to be granted. Ensure we release what we got.
            If _tcs.Task.IsCompleted Then
                _gate.Release()
            Else
                ' Granted concurrently after removal failed; release once granted.
                _tcs.Task.ContinueWith(Sub(t) _gate.Release(), TaskScheduler.Default)
            End If
        End Sub
    End Class

    ''' <summary>Runtime state of one configured model: options, circuit breaker, concurrency gate and counters.</summary>
    Public NotInheritable Class ModelRuntime
        Public ReadOnly Property Options As ModelOptions
        Public ReadOnly Property Breaker As CircuitBreaker
        Public ReadOnly Property Gate As PriorityConcurrencyGate
        ''' <summary>What providers showed about real context windows (Nothing in tests without it).</summary>
        Public Property Limits As ContextLimits

        ''' <summary>The context window to budget with: the configured size, or what its provider showed it really is.</summary>
        Public ReadOnly Property ContextWindow As Integer
            Get
                Dim configured = Options.ContextWindowTokens
                Return If(Limits Is Nothing OrElse Options.IsLocal, configured, Limits.Effective(Options.ModelId, configured))
            End Get
        End Property

        Private _calls As Long
        Private _failures As Long
        Private _chatSlots As Integer

        ''' <summary>Slots held right now by people (Chat, the API) rather than agents.</summary>
        Public ReadOnly Property ChatSlots As Integer
            Get
                Return Volatile.Read(_chatSlots)
            End Get
        End Property

        Friend Sub NoteChatSlot(delta As Integer)
            Interlocked.Add(_chatSlots, delta)
        End Sub
        Private _totalLatencyMs As Long
        Public Property LastError As String
        Public Property LastErrorUtc As DateTime?
        Public Property LastSuccessUtc As DateTime?

        Public Sub New(options As ModelOptions, breaker As CircuitBreaker, gate As PriorityConcurrencyGate)
            Me.Options = options
            Me.Breaker = breaker
            Me.Gate = gate
        End Sub

        Public ReadOnly Property Key As String
            Get
                Return Options.Key
            End Get
        End Property

        Public Function Has(cap As ModelCapability) As Boolean
            Return Options.Capabilities.Contains(cap)
        End Function

        Public Sub RecordCall(success As Boolean, latencyMs As Long, errorMessage As String)
            Interlocked.Increment(_calls)
            Interlocked.Add(_totalLatencyMs, latencyMs)
            If success Then
                LastSuccessUtc = DateTime.UtcNow
            Else
                Interlocked.Increment(_failures)
                LastError = errorMessage
                LastErrorUtc = DateTime.UtcNow
            End If
        End Sub

        Public ReadOnly Property Calls As Long
            Get
                Return Interlocked.Read(_calls)
            End Get
        End Property

        Public ReadOnly Property Failures As Long
            Get
                Return Interlocked.Read(_failures)
            End Get
        End Property

        Public ReadOnly Property AverageLatencyMs As Double
            Get
                Dim c = Calls
                Return If(c = 0, 0.0, Interlocked.Read(_totalLatencyMs) / CDbl(c))
            End Get
        End Property
    End Class

    ''' <summary>
    ''' All models the host can call: configured models (appsettings.json) plus custom models registered by administrators
    ''' at runtime. Custom models can be added, replaced and removed while agents run.
    ''' </summary>
    Public NotInheritable Class ModelRegistry
        Private ReadOnly _models As New Concurrent.ConcurrentDictionary(Of String, ModelRuntime)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _agingSeconds As Integer
        Private ReadOnly _clock As IClock
        Private ReadOnly _onCircuitChange As Action(Of String, CircuitState, CircuitState)
        Private _limits As ContextLimits

        ''' <summary>Learned context windows, for every model now and later.</summary>
        Public Property Limits As ContextLimits
            Get
                Return _limits
            End Get
            Set(value As ContextLimits)
                _limits = value
                For Each m In _models.Values
                    m.Limits = value
                Next
            End Set
        End Property

        Public Sub New(models As IEnumerable(Of ModelOptions), agingSeconds As Integer, clock As IClock, onCircuitChange As Action(Of String, CircuitState, CircuitState))
            _agingSeconds = agingSeconds
            _clock = clock
            _onCircuitChange = onCircuitChange
            For Each m In models
                _models(m.Key) = Build(m)
            Next
        End Sub

        Private Function Build(m As ModelOptions) As ModelRuntime
            Dim key = m.Key
            Dim cb = _onCircuitChange
            Dim breaker As New CircuitBreaker(m.CircuitBreakerFailures, TimeSpan.FromSeconds(m.CircuitBreakerSeconds), _clock, Sub(a, b) cb?.Invoke(key, a, b))
            Return New ModelRuntime(m, breaker, New PriorityConcurrencyGate(m.MaxConcurrency, _agingSeconds, _clock)) With {.Limits = _limits}
        End Function

        Public Function TryGet(key As String) As ModelRuntime
            Dim m As ModelRuntime = Nothing
            If key IsNot Nothing AndAlso _models.TryGetValue(key, m) Then Return m
            Return Nothing
        End Function

        Public Function All() As IReadOnlyList(Of ModelRuntime)
            Return _models.Values.OrderBy(Function(m) m.Options.Tier).ThenBy(Function(m) m.Key, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Adds or replaces a model (used for administrator-registered custom models).</summary>
        Public Sub AddOrReplace(m As ModelOptions)
            _models(m.Key) = Build(m)
        End Sub

        Public Function Remove(key As String) As Boolean
            Dim removed As ModelRuntime = Nothing
            Return key IsNot Nothing AndAlso _models.TryRemove(key, removed)
        End Function
    End Class

End Namespace
