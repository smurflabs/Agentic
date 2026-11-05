Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports Agentic.Utilities

Namespace Communication

    ''' <summary>
    ''' Sliding-window rate limiter keyed by an arbitrary string (e.g. "agent:messages").
    ''' In-memory only; bounded by the configured maximum per key. No I/O happens under its locks.
    ''' </summary>
    Public NotInheritable Class SlidingWindowRateLimiter
        Private ReadOnly _windows As New ConcurrentDictionary(Of String, Queue(Of DateTime))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _clock As IClock

        Public Sub New(clock As IClock)
            _clock = clock
        End Sub

        ''' <summary>Records a hit if under the limit. Returns False (and records nothing) when the limit is reached.</summary>
        Public Function TryAcquire(key As String, maxHits As Integer, window As TimeSpan) As Boolean
            If maxHits <= 0 Then Return False
            Dim q = _windows.GetOrAdd(key, Function(k) New Queue(Of DateTime)())
            Dim now = _clock.UtcNow
            SyncLock q
                While q.Count > 0 AndAlso now - q.Peek() >= window
                    q.Dequeue()
                End While
                If q.Count >= maxHits Then Return False
                q.Enqueue(now)
                Return True
            End SyncLock
        End Function

        Public Function CurrentCount(key As String, window As TimeSpan) As Integer
            Dim q As Queue(Of DateTime) = Nothing
            If Not _windows.TryGetValue(key, q) Then Return 0
            Dim now = _clock.UtcNow
            SyncLock q
                Return Enumerable.Count(q, Function(t) now - t < window)
            End SyncLock
        End Function
    End Class

    ''' <summary>Result of loop/limit checks for a prospective message or delegation.</summary>
    Public NotInheritable Class LoopCheckResult
        Public Property Allowed As Boolean
        Public Property Code As String
        Public Property Reason As String

        Public Shared ReadOnly Ok As New LoopCheckResult With {.Allowed = True}

        Public Shared Function Reject(code As String, reason As String) As LoopCheckResult
            Return New LoopCheckResult With {.Allowed = False, .Code = code, .Reason = reason}
        End Function
    End Class

    ''' <summary>
    ''' Detects communication cycles and runaway chains using message ancestry (the path of agent IDs that led
    ''' to the current work), delegation depth and chain length.
    ''' </summary>
    Public NotInheritable Class LoopDetector
        Public Shared Function Check(senderId As String, recipientId As String, messageType As MessageType, ctx As SendContext,
                                     maxDepth As Integer, maxChain As Integer, allowRevisit As Boolean) As LoopCheckResult
            If String.Equals(senderId, recipientId, StringComparison.OrdinalIgnoreCase) Then
                Return LoopCheckResult.Reject("SELF_MESSAGE", "An agent cannot send messages to itself.")
            End If
            ' Replies travel back up the chain and never create new hops.
            If AgentMessage.IsReplyType(messageType) Then Return LoopCheckResult.Ok

            Dim newChain = ctx.ChainLength + 1
            If newChain > maxChain Then
                Return LoopCheckResult.Reject("CHAIN_TOO_LONG", $"Communication chain length {newChain} exceeds the limit of {maxChain}.")
            End If
            If AgentMessage.IsRequestType(messageType) Then
                Dim newDepth = ctx.DelegationDepth + 1
                If newDepth > maxDepth Then
                    Return LoopCheckResult.Reject("DELEGATION_DEPTH_EXCEEDED", $"Delegation depth {newDepth} exceeds the limit of {maxDepth}.")
                End If
                If Not allowRevisit AndAlso ctx.Ancestry IsNot Nothing AndAlso ctx.Ancestry.Contains(recipientId, StringComparer.OrdinalIgnoreCase) Then
                    Return LoopCheckResult.Reject("LOOP_DETECTED", $"'{recipientId}' is already part of this request chain ({String.Join(" → ", ctx.Ancestry)} → {senderId}). Circular requests are not allowed.")
                End If
            End If
            Return LoopCheckResult.Ok
        End Function
    End Class

End Namespace
