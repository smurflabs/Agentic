Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A request waiting in a WorkQueue, as its owner sees it.</summary>
    Public NotInheritable Class QueuedWork
        Public Property Id As String
        Public Property Label As String
        ''' <summary>1 = next to start.</summary>
        Public Property Position As Integer
        Public Property WaitingSeconds As Integer
    End Class

    ''' <summary>How busy a server is: its slots, the requests it is working on and the requests waiting (the owner's own listed).</summary>
    Public NotInheritable Class QueueState
        Public Property Slots As Integer
        Public Property Running As Integer
        Public Property Waiting As Integer
        ''' <summary>The owner's requests being worked on.</summary>
        Public Property MineRunning As Integer
        ''' <summary>The owner's requests waiting, first in line first.</summary>
        Public Property Mine As New List(Of QueuedWork)
    End Class

    ''' <summary>
    ''' A first-come, first-served line for a server that can only work on so many requests at once (the speech server 2,
    ''' Stable Diffusion 1). EnterAsync gives a slot at once when one is free and nobody is waiting, else waits in line;
    ''' disposing what it returns frees the slot for the next in line. Leaving the line (cancelling) keeps everyone's order.
    ''' </summary>
    Public NotInheritable Class WorkQueue
        Private NotInheritable Class Entry
            Public Id As String
            Public Owner As String
            Public Label As String
            Public Since As DateTime
            Public ReadOnly Turn As New TaskCompletionSource(Of IDisposable)(TaskCreationOptions.RunContinuationsAsynchronously)
        End Class

        Private NotInheritable Class Slot
            Implements IDisposable
            Private ReadOnly _queue As WorkQueue
            Private ReadOnly _entry As Entry
            Private _freed As Integer

            Public Sub New(queue As WorkQueue, entry As Entry)
                _queue = queue
                _entry = entry
            End Sub

            Public Sub Dispose() Implements IDisposable.Dispose
                If Interlocked.Exchange(_freed, 1) = 0 Then _queue.Free(_entry)
            End Sub
        End Class

        Private ReadOnly _lock As New Object()
        Private ReadOnly _line As New LinkedList(Of Entry)()
        Private ReadOnly _running As New List(Of Entry)()

        Public Sub New(slots As Integer)
            Me.Slots = Math.Max(1, slots)
        End Sub

        Public ReadOnly Property Slots As Integer

        ''' <summary>
        ''' Waits for a slot, in the order requests came. The place in line is taken before this returns, so calls made one
        ''' after another keep their order. Cancelling ct leaves the line (OperationCanceledException).
        ''' </summary>
        Public Function EnterAsync(owner As String, label As String, ct As CancellationToken, Optional id As String = Nothing) As Task(Of IDisposable)
            ct.ThrowIfCancellationRequested()
            Dim e As New Entry With {.Id = If(id, Ids.NewId("q")), .Owner = owner, .Label = label, .Since = DateTime.UtcNow}
            Dim node As LinkedListNode(Of Entry)
            SyncLock _lock
                If _line.Count = 0 AndAlso _running.Count < Slots Then
                    _running.Add(e)
                    Return Task.FromResult(Of IDisposable)(New Slot(Me, e))
                End If
                node = _line.AddLast(e)
            End SyncLock
            If ct.CanBeCanceled Then
                Dim reg = ct.Register(Sub() Leave(node, ct))
                Dim unused = e.Turn.Task.ContinueWith(Sub(t) reg.Dispose(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
            End If
            Return e.Turn.Task
        End Function

        Private Sub Leave(node As LinkedListNode(Of Entry), ct As CancellationToken)
            Dim left = False
            SyncLock _lock
                If node.List Is _line Then
                    _line.Remove(node)
                    left = True
                End If
            End SyncLock
            ' Its turn had already come: the slot is handed over and freed by whoever holds it.
            If left Then node.Value.Turn.TrySetCanceled(ct)
        End Sub

        Private Sub Free(e As Entry)
            Dim nextUp As Entry = Nothing
            SyncLock _lock
                _running.Remove(e)
                If _line.Count > 0 AndAlso _running.Count < Slots Then
                    nextUp = _line.First.Value
                    _line.RemoveFirst()
                    _running.Add(nextUp)
                End If
            End SyncLock
            If nextUp IsNot Nothing Then nextUp.Turn.TrySetResult(New Slot(Me, nextUp))
        End Sub

        ''' <summary>Where a waiting request is (1 = next to start); Nothing when it is not waiting.</summary>
        Public Function PositionOf(id As String) As Integer?
            SyncLock _lock
                Dim i = 0
                For Each e In _line
                    i += 1
                    If e.Id = id Then Return i
                Next
            End SyncLock
            Return Nothing
        End Function

        Public ReadOnly Property Waiting As Integer
            Get
                SyncLock _lock
                    Return _line.Count
                End SyncLock
            End Get
        End Property

        Public ReadOnly Property Running As Integer
            Get
                SyncLock _lock
                    Return _running.Count
                End SyncLock
            End Get
        End Property

        ''' <summary>How busy the server is, with the owner's own requests (no one else's are named).</summary>
        Public Function StateFor(owner As String) As QueueState
            Dim now = DateTime.UtcNow
            Dim s As New QueueState With {.Slots = Slots}
            SyncLock _lock
                s.Running = _running.Count
                s.Waiting = _line.Count
                s.MineRunning = _running.Where(Function(e) e.Owner = owner).Count()
                Dim i = 0
                For Each e In _line
                    i += 1
                    If e.Owner = owner Then s.Mine.Add(New QueuedWork With {.Id = e.Id, .Label = e.Label, .Position = i, .WaitingSeconds = CInt((now - e.Since).TotalSeconds)})
                Next
            End SyncLock
            Return s
        End Function
    End Class

End Namespace
