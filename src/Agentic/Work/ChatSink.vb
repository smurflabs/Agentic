Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Threading

Namespace Work

    ''' <summary>
    ''' Where an answer's events go. The answer keeps running when its window's connection drops (a network blip, a proxy
    ''' reconnect, a sleeping laptop): events are kept while nobody listens, and a window that reconnects
    ''' (GET …/attach) gets them first and then the rest live. Only the stop button ends an answer.
    ''' </summary>
    Public NotInheritable Class ChatSink
        ''' <summary>Events kept while detached; beyond this the oldest go (the saved answer is complete anyway).</summary>
        Public Const MaxBacklog As Integer = 20000

        Private ReadOnly _gate As New SemaphoreSlim(1, 1)
        Private ReadOnly _backlog As New LinkedList(Of String)
        Private _writer As Func(Of String, Task)
        Private _attachment As TaskCompletionSource(Of Boolean)
        Private _lastWrite As Long = Environment.TickCount64
        ''' <summary>The answer's closing events (done, error), always kept: a write to a connection that just died can still
        ''' "succeed", so a window reconnecting after the end gets them again.</summary>
        Private ReadOnly _final As New List(Of String)

        Public Sub New(chatId As String, username As String, writer As Func(Of String, Task))
            Me.ChatId = chatId
            Me.Username = username
            _writer = writer
            _attachment = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        End Sub

        Public ReadOnly Property ChatId As String
        Public ReadOnly Property Username As String
        Public Property CompletedUtc As DateTime?

        ''' <summary>No window is listening right now (its connection dropped and none has reconnected).</summary>
        Public ReadOnly Property Detached As Boolean
            Get
                Return _writer Is Nothing
            End Get
        End Property

        ''' <summary>Milliseconds since the listening window last got something.</summary>
        Public ReadOnly Property IdleMs As Long
            Get
                Return Environment.TickCount64 - _lastWrite
            End Get
        End Property

        ''' <summary>Sends one event (already formatted as SSE text) to the listening window, or keeps it for the next one.</summary>
        Public Async Function WriteAsync(text As String) As Task
            Await _gate.WaitAsync().ConfigureAwait(False)
            Try
                If text.StartsWith("event: done", StringComparison.Ordinal) OrElse text.StartsWith("event: error", StringComparison.Ordinal) Then _final.Add(text)
                If _writer IsNot Nothing Then
                    Try
                        Await _writer(text).ConfigureAwait(False)
                        _lastWrite = Environment.TickCount64
                        Return
                    Catch ex As Exception When IsGone(ex)
                        DetachLocked()
                    End Try
                End If
                Keep(text)
            Finally
                _gate.Release()
            End Try
        End Function

        ''' <summary>A keep-alive comment while the window has had nothing for a while (proxies close silent responses).</summary>
        Public Async Function PingAsync() As Task
            If Not Await _gate.WaitAsync(0).ConfigureAwait(False) Then Return
            Try
                If _writer Is Nothing OrElse IdleMs < 12000 Then Return
                Try
                    Await _writer(": ping" & vbLf & vbLf).ConfigureAwait(False)
                    _lastWrite = Environment.TickCount64
                Catch ex As Exception When IsGone(ex)
                    DetachLocked()
                End Try
            Finally
                _gate.Release()
            End Try
        End Function

        ''' <summary>
        ''' A window (re)connects: it gets what it missed, then the rest live. Returns a task that ends when this window's
        ''' listening ends (the answer finished, its connection dropped, or another window took over).
        ''' </summary>
        Public Async Function AttachAsync(writer As Func(Of String, Task)) As Task(Of Task)
            Await _gate.WaitAsync().ConfigureAwait(False)
            Try
                _attachment.TrySetResult(True)
                _attachment = New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                _writer = Nothing
                Dim replayed As New HashSet(Of String)(StringComparer.Ordinal)
                While _backlog.Count > 0
                    Await writer(_backlog.First.Value).ConfigureAwait(False)
                    replayed.Add(_backlog.First.Value)
                    _backlog.RemoveFirst()
                End While
                For Each closing In _final.Where(Function(t) Not replayed.Contains(t))
                    Await writer(closing).ConfigureAwait(False)
                Next
                _writer = writer
                _lastWrite = Environment.TickCount64
                If CompletedUtc.HasValue Then _attachment.TrySetResult(True)
                Return _attachment.Task
            Finally
                _gate.Release()
            End Try
        End Function

        ''' <summary>The answer is over: the window listening (if any) is released.</summary>
        Public Sub Complete()
            CompletedUtc = DateTime.UtcNow
            _attachment.TrySetResult(True)
        End Sub

        Private Sub DetachLocked()
            _writer = Nothing
            _attachment.TrySetResult(True)
        End Sub

        Private Sub Keep(text As String)
            _backlog.AddLast(text)
            While _backlog.Count > MaxBacklog
                _backlog.RemoveFirst()
            End While
        End Sub

        Private Shared Function IsGone(ex As Exception) As Boolean
            Return TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse
                   TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is InvalidOperationException
        End Function
    End Class

    ''' <summary>The answers' sinks by chat (kept a while after the answer ends, so a late reconnect still gets its end).</summary>
    Public NotInheritable Class ChatSinks
        Private ReadOnly _sinks As New ConcurrentDictionary(Of String, ChatSink)(StringComparer.Ordinal)
        Public Shared ReadOnly KeepAfterEnd As TimeSpan = TimeSpan.FromMinutes(15)

        Public Sub Add(sink As ChatSink)
            Cleanup()
            _sinks(sink.ChatId) = sink
        End Sub

        Public Function [Get](chatId As String, username As String) As ChatSink
            Dim s As ChatSink = Nothing
            If chatId Is Nothing OrElse Not _sinks.TryGetValue(chatId, s) Then Return Nothing
            Return If(String.Equals(s.Username, username, StringComparison.OrdinalIgnoreCase), s, Nothing)
        End Function

        Private Sub Cleanup()
            Dim now = DateTime.UtcNow
            For Each kv In _sinks
                If kv.Value.CompletedUtc.HasValue AndAlso now - kv.Value.CompletedUtc.Value > KeepAfterEnd Then _sinks.TryRemove(kv)
            Next
        End Sub
    End Class

End Namespace
