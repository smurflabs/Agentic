Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Monitor events (the monitor tool of the desktop app and VS Code): output lines of a command the model is watching. When
    ' the chat is not busy, the server answers them by itself (an automatic answer, like a scheduled prompt), so the model
    ' reacts to a log, a build or a server as things happen - also while no window has the chat open.
    Partial Friend NotInheritable Class AdminApi

        Private Const MonitorMaxLines As Integer = 300
        ''' <summary>At most one automatic answer to monitor events this often per chat (seconds); lines arriving meanwhile wait for it.</summary>
        Private Const MonitorMinGapSeconds As Integer = 15

        Private NotInheritable Class MonitorInbox
            Public ReadOnly Lines As New List(Of String)
            Public Dropped As Integer
            Public Pending As Boolean
            Public LastDelivery As DateTime = DateTime.MinValue
        End Class

        Private ReadOnly _monitorInboxes As New ConcurrentDictionary(Of String, MonitorInbox)(StringComparer.Ordinal)

        Friend Sub RegisterChatMonitor(r As Router)
            r.Map("POST", "/api/chat/conversations/{chatId}/monitor", AuthLevel.SubmitTask, AddressOf ChatMonitorEvents)
        End Sub

        ''' <summary>Body {monitor, description, lines: [..], status?}: new output lines of a watched command; status when it ended.</summary>
        Private Async Function ChatMonitorEvents(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim id = TextUtil.Truncate(If(JsonUtil.GetString(b, "monitor"), "m"), 40)
            Dim description = TextUtil.Truncate(If(JsonUtil.GetString(b, "description"), ""), 60)
            Dim lines = JsonUtil.GetStringList(b, "lines").Take(200).Select(Function(l) TextUtil.Truncate(Regex.Replace(If(l, ""), "\s+", " ").Trim(), 400)).
                                                                  Where(Function(l) l.Length > 0).ToList()
            Dim status = TextUtil.Truncate(JsonUtil.GetString(b, "status"), 200)
            Dim inbox = _monitorInboxes.GetOrAdd(c.Id, Function(k) New MonitorInbox())
            Dim start = False
            SyncLock inbox
                Dim tag = $"[monitor {id}{If(description.Length > 0, " " & description, "")}] "
                For Each l In lines
                    If inbox.Lines.Count >= MonitorMaxLines Then
                        inbox.Dropped += 1
                    Else
                        inbox.Lines.Add(tag & l)
                    End If
                Next
                If Not String.IsNullOrWhiteSpace(status) Then inbox.Lines.Add(tag & status)
                If inbox.Lines.Count > 0 AndAlso Not inbox.Pending Then
                    inbox.Pending = True
                    start = True
                End If
            End SyncLock
            Dim user = rc.User
            If start Then
                Dim unused = Task.Run(Function() DeliverMonitorEventsAsync(user, c.Id, inbox))
            End If
            Await rc.OkAsync(New With {.queued = lines.Count}).ConfigureAwait(False)
        End Function

        ''' <summary>Waits for a pause (a burst of lines together, the chat not busy, the minimum gap), then answers the events.</summary>
        Private Async Function DeliverMonitorEventsAsync(user As AuthUser, chatId As String, inbox As MonitorInbox) As Task
            Try
                Await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(False)
                Dim waited = 0
                While True
                    Dim gap = MonitorMinGapSeconds - (DateTime.UtcNow - inbox.LastDelivery).TotalSeconds
                    If gap > 0 Then Await Task.Delay(TimeSpan.FromSeconds(gap)).ConfigureAwait(False)
                    If Not _h.Chat.Live.IsLive(chatId) Then Exit While
                    ' An answer is being written: the events go next (an hour at most).
                    Await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(False)
                    waited += 3
                    If waited > 3600 Then Exit While
                End While
                Dim text As String
                SyncLock inbox
                    text = "(Monitor events: output of commands you are watching with the monitor tool, as it happened. React when something needs it - " &
                           "an error, a finished build, a server that is ready - otherwise acknowledge it in a few words.)" & vbLf & String.Join(vbLf, inbox.Lines) &
                           If(inbox.Dropped > 0, vbLf & $"({inbox.Dropped} more line(s) were left out.)", "")
                    inbox.Lines.Clear()
                    inbox.Dropped = 0
                    inbox.Pending = False
                    inbox.LastDelivery = DateTime.UtcNow
                End SyncLock
                Dim status = Await RunAutomaticAnswerAsync(user, chatId, text, "monitor", CancellationToken.None).ConfigureAwait(False)
                If status <> "ok" Then _h.Log.Warn("Chat", $"Answering monitor events in chat {chatId}: {status}")
            Catch ex As Exception
                _h.Log.Warn("Chat", $"Monitor events for chat {chatId} were not answered: {ex.Message}")
                SyncLock inbox
                    inbox.Pending = False
                End SyncLock
            End Try
        End Function

        ''' <summary>
        ''' An answer the server starts by itself (a scheduled prompt, monitor events) as the user, with their access and limits as
        ''' they are now. No window asked for it: its events are kept for windows that open the chat, and open ones follow it live.
        ''' Returns "ok", "stopped", "skipped: …" or "failed: …".
        ''' </summary>
        Friend Async Function RunAutomaticAnswerAsync(user As AuthUser, chatId As String, content As String, kind As String, ct As CancellationToken) As Task(Of String)
            Dim c = Await _h.Chat.GetAsync(user.Name, chatId, False).ConfigureAwait(False)
            If c Is Nothing Then Return "stopped: the chat no longer exists"
            Dim model = c.Model
            If Not user.CanUseModel(model) Then Return $"skipped: no access to the model '{model}'"
            Dim blocked = Await _h.Accounts.BlockReasonAsync(user.Name, user.IsAdmin, model, Nothing).ConfigureAwait(False)
            If blocked Is Nothing AndAlso user.IsRestrictedUser Then blocked = Await LimitProblemAsync(user.Name, 0, user.DailyTokenLimit, False).ConfigureAwait(False)
            If blocked IsNot Nothing Then Return "skipped: " & blocked
            If _h.Chat.Live.IsLive(c.Id) Then Return "skipped: an answer was being written"
            Dim errorText As String = Nothing
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                _h.Chat.Live.SetStopper(c.Id, cts)
                Dim sink As New ChatSink(c.Id, user.Name, Nothing)
                _h.Chat.Live.Sinks.Add(sink)
                Dim emit As Func(Of String, Object, Task) =
                    Function(type, payload)
                        If cts.IsCancellationRequested Then Return Task.CompletedTask
                        If type = "error" Then errorText = JsonUtil.ToNode(payload)?("message")?.ToString()
                        Return sink.WriteAsync($"event: {type}" & vbLf & "data: " & JsonUtil.ToJson(payload) & vbLf & vbLf)
                    End Function
                Try
                    Await _h.Chat.SendAsync(user.Name, user.IsAdmin, user.HasPermission("chat.tools"), c.Id, content, model, New List(Of String), c.Settings, False, emit, cts.Token,
                                            New ChatClientContext With {.ClientId = kind, .Automatic = kind}, Nothing, False, Function(m) user.CanUseModel(m),
                                            ChatAccessFor(user)).ConfigureAwait(False)
                Catch ex As Exception When Not cts.IsCancellationRequested
                    errorText = If(TypeOf ex Is ArgumentException OrElse TypeOf ex Is KeyNotFoundException, ex.Message, ModelErrors.ForUser(ex))
                End Try
                _h.Chat.Live.ClearStopper(c.Id, cts)
                sink.Complete()
                Return If(cts.IsCancellationRequested, "stopped", If(errorText Is Nothing, "ok", "failed: " & errorText))
            End Using
        End Function

    End Class

End Namespace
