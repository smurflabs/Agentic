Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports System.Threading.Channels
Imports Agentic.Logging

Namespace Events

    Public Enum AgentEventType
        AgentStateChanged
        TaskCreated
        TaskStarted
        TaskCompleted
        TaskFailed
        TaskRetrying
        TaskWaiting
        TaskCancelled
        TaskProgress
        MessageSent
        MessageReceived
        MessageCompleted
        MessageRejected
        MessageExpired
        DelegationCreated
        DelegationCompleted
        ToolStarted
        ToolCompleted
        AiCallStarted
        AiCallCompleted
        AiSpillover
        AiEscalation
        ModelHealthChanged
        BudgetInsufficient
        LedgerEntry
        MemoryCreated
        ApprovalRequested
        ApprovalResolved
        McpServerStateChanged
        ArtifactCreated
        AgentUnhealthy
        AgentStuck
        ConfigChanged
        AdminAction
        ErrorOccurred
        HostLifecycle
        TaskUpdated
        SubagentSpawned
        SubagentFinished
        ModelAccessChanged
        CaseStudyStarted
        CaseStudyPublished
        GlobalTaskUpdated
        ReviewRequested
        ReviewCompleted
        ResultDelivered
        AgentDied
        AgentRevived
    End Enum

    Public Enum EventLevel
        Info
        Warning
        [Error]
    End Enum

    ''' <summary>Immutable host event. Summary is human readable; Data holds small structured details (never secrets or full AI content).</summary>
    Public NotInheritable Class AgentEvent
        Public Property Sequence As Long
        Public Property EventType As AgentEventType
        Public Property Level As EventLevel
        Public Property TimestampUtc As DateTime
        Public Property AgentId As String
        Public Property TaskId As String
        Public Property MessageId As String
        Public Property CorrelationId As String
        Public Property Summary As String
        Public Property Data As Dictionary(Of String, Object)
    End Class

    Public Interface IAgentEventBus
        Sub Publish(evt As AgentEvent)
        Function Subscribe(name As String, capacity As Integer, Optional filter As Func(Of AgentEvent, Boolean) = Nothing) As EventSubscription
        Sub Unsubscribe(subscription As EventSubscription)
    End Interface

    Public Module EventBusExtensions
        <System.Runtime.CompilerServices.Extension>
        Public Sub Emit(bus As IAgentEventBus, eventType As AgentEventType, summary As String,
                        Optional agentId As String = Nothing, Optional taskId As String = Nothing,
                        Optional messageId As String = Nothing, Optional correlationId As String = Nothing,
                        Optional level As EventLevel = EventLevel.Info, Optional data As Dictionary(Of String, Object) = Nothing)
            bus.Publish(New AgentEvent With {
                .EventType = eventType, .Level = level, .TimestampUtc = DateTime.UtcNow, .AgentId = agentId, .TaskId = taskId,
                .MessageId = messageId, .CorrelationId = correlationId, .Summary = summary, .Data = data
            })
        End Sub
    End Module

    ''' <summary>A subscriber's bounded queue. Slow consumers lose oldest events (counted) instead of blocking producers.</summary>
    Public NotInheritable Class EventSubscription
        Friend ReadOnly Queue As Channel(Of AgentEvent)
        Friend ReadOnly Filter As Func(Of AgentEvent, Boolean)
        Private _dropped As Long

        Public ReadOnly Property Name As String

        Friend Sub New(name As String, capacity As Integer, filter As Func(Of AgentEvent, Boolean))
            Me.Name = name
            Me.Filter = filter
            Queue = Channel.CreateBounded(Of AgentEvent)(New BoundedChannelOptions(Math.Max(16, capacity)) With {
                .FullMode = BoundedChannelFullMode.DropOldest, .SingleReader = True, .SingleWriter = True})
        End Sub

        Public ReadOnly Property Reader As ChannelReader(Of AgentEvent)
            Get
                Return Queue.Reader
            End Get
        End Property

        Public ReadOnly Property Dropped As Long
            Get
                Return Interlocked.Read(_dropped)
            End Get
        End Property

        Friend Sub Offer(evt As AgentEvent)
            If Filter IsNot Nothing Then
                Try
                    If Not Filter(evt) Then Return
                Catch
                    Return
                End Try
            End If
            If Not Queue.Writer.TryWrite(evt) Then Interlocked.Increment(_dropped)
        End Sub

        Friend Sub Complete()
            Queue.Writer.TryComplete()
        End Sub
    End Class

    ''' <summary>
    ''' In-process event bus. Publish never blocks (bounded inbound channel, drop-oldest). A single dispatcher
    ''' fans events out to subscribers (activity persister, statistics, SSE clients, alerts, logger).
    ''' Producers (agent code) are fully decoupled from consumers.
    ''' </summary>
    Public NotInheritable Class AgentEventBus
        Implements IAgentEventBus, IDisposable

        Private ReadOnly _inbound As Channel(Of AgentEvent)
        Private ReadOnly _subscribers As New List(Of EventSubscription)
        Private ReadOnly _subLock As New Object()
        Private _snapshot As EventSubscription() = Array.Empty(Of EventSubscription)()
        Private _sequence As Long
        Private ReadOnly _dispatcher As Task
        Private ReadOnly _log As ILog
        Private _droppedInbound As Long

        Public Sub New(log As ILog)
            _log = log
            _inbound = Channel.CreateBounded(Of AgentEvent)(New BoundedChannelOptions(20000) With {
                .FullMode = BoundedChannelFullMode.DropOldest, .SingleReader = True, .SingleWriter = False})
            _dispatcher = DispatchLoopAsync()
        End Sub

        Public ReadOnly Property DroppedInbound As Long
            Get
                Return Interlocked.Read(_droppedInbound)
            End Get
        End Property

        Public Sub Publish(evt As AgentEvent) Implements IAgentEventBus.Publish
            If evt Is Nothing Then Return
            evt.Sequence = Interlocked.Increment(_sequence)
            If evt.TimestampUtc = DateTime.MinValue Then evt.TimestampUtc = DateTime.UtcNow
            If Not _inbound.Writer.TryWrite(evt) Then Interlocked.Increment(_droppedInbound)
        End Sub

        Public Function Subscribe(name As String, capacity As Integer, Optional filter As Func(Of AgentEvent, Boolean) = Nothing) As EventSubscription Implements IAgentEventBus.Subscribe
            Dim s As New EventSubscription(name, capacity, filter)
            SyncLock _subLock
                _subscribers.Add(s)
                _snapshot = _subscribers.ToArray()
            End SyncLock
            Return s
        End Function

        Public Sub Unsubscribe(subscription As EventSubscription) Implements IAgentEventBus.Unsubscribe
            If subscription Is Nothing Then Return
            SyncLock _subLock
                _subscribers.Remove(subscription)
                _snapshot = _subscribers.ToArray()
            End SyncLock
            subscription.Complete()
        End Sub

        Private Async Function DispatchLoopAsync() As Task
            Try
                While Await _inbound.Reader.WaitToReadAsync().ConfigureAwait(False)
                    Dim evt As AgentEvent = Nothing
                    While _inbound.Reader.TryRead(evt)
                        Dim subs = Volatile.Read(_snapshot)
                        For Each s In subs
                            s.Offer(evt)
                        Next
                    End While
                End While
            Catch ex As Exception
                _log.Err("EventBus", "Event dispatcher stopped unexpectedly", ex)
            End Try
        End Function

        ''' <summary>Stops accepting events and drains the dispatcher (bounded wait).</summary>
        Public Sub Complete(timeout As TimeSpan)
            _inbound.Writer.TryComplete()
            Try
                _dispatcher.Wait(timeout)
            Catch
            End Try
            SyncLock _subLock
                For Each s In _subscribers
                    s.Complete()
                Next
            End SyncLock
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Complete(TimeSpan.FromSeconds(2))
        End Sub
    End Class

End Namespace
