Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Channels
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' Live state of a user's chats, shared by every window they have open (the web app, the desktop app, several tabs):
    ''' who is answering where, the answer as it streams, tool approvals waiting for the user, and the desktop apps that are
    ''' connected (with the tools they offer). One event stream per window carries it all (GET /api/chat/stream).
    ''' </summary>
    Public NotInheritable Class ChatLiveHub

        Public NotInheritable Class Subscriber
            Public ReadOnly Channel As Channel(Of String) = Threading.Channels.Channel.CreateBounded(Of String)(
                New BoundedChannelOptions(2000) With {.FullMode = BoundedChannelFullMode.DropOldest, .SingleReader = True})
            ''' <summary>This connection (one window's event stream); a tool call for a device is addressed to one of these.</summary>
            Public ReadOnly Property Id As String = "sub_" & Ids.NewToken(12)
            Public ReadOnly Property ConnectedUtc As DateTime = DateTime.UtcNow
            ''' <summary>The page behind this connection (stays the same when the page reconnects its event stream).</summary>
            Public Property PageId As String
            Public Property Username As String
            Public Property DeviceId As String
            Public Property DeviceName As String
        End Class

        ''' <summary>A desktop app that can run tools on its computer for this user's chats.</summary>
        Public NotInheritable Class Device
            Public Property Id As String
            Public Property Name As String
            Public Property Username As String
            Public Property Tools As New List(Of JsonObject)
            Public Property Instructions As String
            Public Property UpdatedUtc As DateTime
        End Class

        ''' <summary>An answer being written right now.</summary>
        Public NotInheritable Class Run
            Public Property ChatId As String
            Public Property Username As String
            ''' <summary>The window that asked (its random client id), so it can ignore its own echoes.</summary>
            Public Property ClientId As String
            ''' <summary>"goal": the server started this answer itself (a goal's automatic continuation), no window did.</summary>
            Public Property Auto As String
            Public Property DeviceId As String
            Public Property Model As String
            Public Property MessageId As String
            Public Property StartedUtc As DateTime
            ''' <summary>The stop button was pressed: it is winding down and saving what it wrote.</summary>
            Public Property Stopping As Boolean
            Public ReadOnly Content As New StringBuilder()
            Public ReadOnly Reasoning As New StringBuilder()
            ''' <summary>Where each thinking phase starts (like ChatMessageRecord.ReasoningParts).</summary>
            Public ReadOnly ReasoningParts As New List(Of ChatReasoningPart)
            Public Property LastKind As String
            Public Property Status As String
            Public Property Route As Object
            Public Property Sources As Object
            Public ReadOnly Tools As New List(Of Object)
            Public ReadOnly Approvals As New Dictionary(Of String, Object)
            ''' <summary>Subagents' transcripts by their card's call id (what each wrote, thought and did so far).</summary>
            Public ReadOnly Subagents As New Dictionary(Of String, SubagentTranscript)(StringComparer.Ordinal)
            Public ReadOnly Gate As New Object()
        End Class

        Private NotInheritable Class PendingApproval
            Public Waiter As New TaskCompletionSource(Of String)(TaskCreationOptions.RunContinuationsAsynchronously)
            Public Info As Object
        End Class

        Private ReadOnly _subs As New ConcurrentDictionary(Of Subscriber, Byte)()
        Private ReadOnly _runs As New ConcurrentDictionary(Of String, Run)(StringComparer.Ordinal)
        Private ReadOnly _devices As New ConcurrentDictionary(Of String, Device)(StringComparer.Ordinal)
        Private ReadOnly _approvals As New ConcurrentDictionary(Of String, PendingApproval)(StringComparer.Ordinal)
        ''' <summary>Arguments the user changed while allowing a call (only some of an edit's changes accepted), by approval key.</summary>
        Private ReadOnly _approvedArgs As New ConcurrentDictionary(Of String, String)(StringComparer.Ordinal)
        Private ReadOnly _allowedTools As New ConcurrentDictionary(Of String, ConcurrentDictionary(Of String, Byte))(StringComparer.Ordinal)
        Private ReadOnly _allowedHosts As New ConcurrentDictionary(Of String, ConcurrentDictionary(Of String, Byte))(StringComparer.Ordinal)
        Private ReadOnly _stoppers As New ConcurrentDictionary(Of String, CancellationTokenSource)(StringComparer.Ordinal)

        ' ------------------------------------------------------------------ stopping an answer from anywhere

        ''' <summary>Where each running answer's events go; a window that lost its connection reconnects to it (GET …/attach).</summary>
        Public ReadOnly Property Sinks As New ChatSinks()

        Public Sub SetStopper(chatId As String, cts As CancellationTokenSource)
            _stoppers(chatId) = cts
        End Sub

        Public Sub ClearStopper(chatId As String, cts As CancellationTokenSource)
            DirectCast(_stoppers, ICollection(Of KeyValuePair(Of String, CancellationTokenSource))).Remove(New KeyValuePair(Of String, CancellationTokenSource)(chatId, cts))
        End Sub

        ''' <summary>How long a stopped answer may take to wind down before the chat is released anyway.</summary>
        Public Property StopGrace As TimeSpan = TimeSpan.FromSeconds(8)

        ''' <summary>
        ''' Cancels the answer being written in the chat; False when none is. Approvals it waits for are refused. If it has not
        ''' ended after StopGrace (stuck in something that ignores cancellation, or its window is gone), the chat is released
        ''' anyway, so a new message can be sent.
        ''' </summary>
        Public Function [Stop](chatId As String) As Boolean
            Dim stopped = False
            Dim cts As CancellationTokenSource = Nothing
            If _stoppers.TryGetValue(chatId, cts) Then
                Try
                    cts.Cancel()
                    stopped = True
                Catch ex As ObjectDisposedException
                End Try
            End If
            For Each kv In _approvals.Where(Function(a) a.Key.Contains("|" & chatId & "|", StringComparison.Ordinal)).ToList()
                kv.Value.Waiter.TrySetResult("deny")
            Next
            Dim r As Run = Nothing
            If _runs.TryGetValue(chatId, r) Then
                stopped = True
                r.Stopping = True
                Dim grace = StopGrace
                Dim unused = Task.Run(Async Function()
                                          Await Task.Delay(grace).ConfigureAwait(False)
                                          EndRun(r)
                                      End Function)
            End If
            Return stopped
        End Function

        ''' <summary>
        ''' A message sent right after the stop button waits (up to StopGrace) for the stopped answer to be saved: otherwise the
        ''' new answer would not see it, and Continue or Answer again would work on the message before it.
        ''' </summary>
        Public Async Function WaitForStoppedAsync(chatId As String) As Task
            Dim deadline = DateTime.UtcNow + StopGrace
            Dim r As Run = Nothing
            While _runs.TryGetValue(chatId, r) AndAlso r.Stopping AndAlso DateTime.UtcNow < deadline
                Await Task.Delay(50).ConfigureAwait(False)
            End While
        End Function

        ' ------------------------------------------------------------------ windows (subscribers)

        Public Function Subscribe(username As String, deviceId As String, deviceName As String, Optional pageId As String = Nothing) As Subscriber
            Dim s As New Subscriber With {.Username = username, .DeviceId = Clean(deviceId), .DeviceName = TextUtil.Truncate(deviceName, 80), .PageId = Clean(pageId)}
            _subs(s) = 0
            If s.DeviceId IsNot Nothing Then
                Dim d = _devices.GetOrAdd(Key(username, s.DeviceId), Function(k) New Device With {.Id = s.DeviceId, .Username = username})
                If Not String.IsNullOrWhiteSpace(s.DeviceName) Then d.Name = s.DeviceName
                d.UpdatedUtc = DateTime.UtcNow
                Publish(username, Nothing, "device", New With {.id = s.DeviceId, .name = d.Name, .online = True})
            End If
            Return s
        End Function

        Public Sub Unsubscribe(s As Subscriber)
            Dim unused As Byte
            _subs.TryRemove(s, unused)
            s.Channel.Writer.TryComplete()
            If s.DeviceId IsNot Nothing AndAlso Not DeviceOnline(s.Username, s.DeviceId) Then
                Publish(s.Username, Nothing, "device", New With {.id = s.DeviceId, .online = False})
            End If
        End Sub

        Private Shared Function Key(username As String, id As String) As String
            Return username & "|" & id
        End Function

        Private Shared Function Clean(id As String) As String
            If String.IsNullOrWhiteSpace(id) Then Return Nothing
            Dim c = New String(id.Where(Function(ch) Char.IsLetterOrDigit(ch) OrElse ch = "-"c OrElse ch = "_"c).ToArray())
            Return If(c.Length = 0, Nothing, TextUtil.Truncate(c, 64))
        End Function

        ''' <summary>Sends one event to every window of the user (deviceId: only to that desktop app).</summary>
        Public Sub Publish(username As String, chatId As String, type As String, data As Object, Optional onlyDevice As String = Nothing, Optional clientId As String = Nothing)
            Dim text = "event: chat" & vbLf & "data: " & JsonUtil.ToJson(New With {.chatId = chatId, .type = type, .data = data, .clientId = clientId}) & vbLf & vbLf
            For Each s In _subs.Keys
                If s.Username <> username Then Continue For
                If onlyDevice IsNot Nothing AndAlso s.DeviceId <> onlyDevice Then Continue For
                s.Channel.Writer.TryWrite(text)
            Next
        End Sub

        ''' <summary>A window of the user is open (web app, desktop app, VS Code, CLI): someone can answer a question.</summary>
        Public Function UserOnline(username As String) As Boolean
            Return _subs.Keys.Any(Function(s) s.Username = username)
        End Function

        ' ------------------------------------------------------------------ desktop apps

        Public Function DeviceOnline(username As String, deviceId As String) As Boolean
            If String.IsNullOrEmpty(deviceId) Then Return False
            Return _subs.Keys.Any(Function(s) s.Username = username AndAlso s.DeviceId = deviceId)
        End Function

        ''' <summary>
        ''' The one window of a device that should run a tool call: the most recently connected (a page that reconnected, or the
        ''' window opened last). Nothing when the device is offline. Never several: two windows of the same app must not both run it.
        ''' </summary>
        Public Function PickSubscriber(username As String, deviceId As String, Optional except As String = Nothing, Optional preferPage As String = Nothing,
                                       Optional onlyPage As Boolean = False) As String
            If String.IsNullOrEmpty(deviceId) Then Return Nothing
            Dim candidates = _subs.Keys.Where(Function(s) s.Username = username AndAlso s.DeviceId = deviceId AndAlso s.Id <> except).ToList()
            ' The same page first (it reconnected its stream): it knows which calls it already runs.
            Dim page = If(String.IsNullOrEmpty(preferPage), Nothing, candidates.Where(Function(s) s.PageId = preferPage).OrderByDescending(Function(s) s.ConnectedUtc).FirstOrDefault())
            If page IsNot Nothing Then Return page.Id
            If onlyPage Then Return Nothing
            Return candidates.OrderByDescending(Function(s) s.ConnectedUtc).Select(Function(s) s.Id).FirstOrDefault()
        End Function

        Public Function SubscriberOnline(subscriberId As String) As Boolean
            If String.IsNullOrEmpty(subscriberId) Then Return False
            Return _subs.Keys.Any(Function(s) s.Id = subscriberId)
        End Function

        ''' <summary>The page of a subscriber (Nothing when unknown or gone).</summary>
        Public Function PageOf(subscriberId As String) As String
            Return _subs.Keys.FirstOrDefault(Function(s) s.Id = subscriberId)?.PageId
        End Function

        ''' <summary>Sends one event to a single window (subscriber) of the user.</summary>
        Public Sub PublishTo(username As String, chatId As String, type As String, data As Object, subscriberId As String)
            Dim text = "event: chat" & vbLf & "data: " & JsonUtil.ToJson(New With {.chatId = chatId, .type = type, .data = data, .clientId = CStr(Nothing)}) & vbLf & vbLf
            For Each s In _subs.Keys
                If s.Username = username AndAlso s.Id = subscriberId Then s.Channel.Writer.TryWrite(text)
            Next
        End Sub

        Public Sub SetCatalog(username As String, deviceId As String, name As String, tools As List(Of JsonObject), instructions As String)
            Dim id = Clean(deviceId)
            If id Is Nothing Then Throw New ArgumentException("A device id is required.")
            Dim d = _devices.GetOrAdd(Key(username, id), Function(k) New Device With {.Id = id, .Username = username})
            If Not String.IsNullOrWhiteSpace(name) Then d.Name = TextUtil.Truncate(name, 80)
            d.Tools = If(tools, New List(Of JsonObject)).Take(96).ToList()
            d.Instructions = TextUtil.Truncate(instructions, 12000)
            d.UpdatedUtc = DateTime.UtcNow
        End Sub

        Public Function GetDevice(username As String, deviceId As String) As Device
            Dim d As Device = Nothing
            If String.IsNullOrEmpty(deviceId) OrElse Not _devices.TryGetValue(Key(username, deviceId), d) Then Return Nothing
            Return d
        End Function

        Public Function Devices(username As String) As List(Of Object)
            ' Each VS Code workspace and CLI folder is its own device: offline ones not seen for a week drop out of the list.
            Dim cutoff = DateTime.UtcNow.AddDays(-7)
            Return _devices.Values.Where(Function(d) d.Username = username AndAlso (DeviceOnline(username, d.Id) OrElse d.UpdatedUtc >= cutoff)).
                Select(Function(d) CObj(New With {.id = d.Id, .name = d.Name, .online = DeviceOnline(username, d.Id), .tools = d.Tools.Count, .updatedUtc = d.UpdatedUtc})).ToList()
        End Function

        ' ------------------------------------------------------------------ runs

        ''' <param name="auto">"goal" when the server started the answer itself (a goal's automatic continuation), not a window.</param>
        Public Function BeginRun(chatId As String, username As String, clientId As String, deviceId As String, model As String, Optional auto As String = Nothing) As Run
            Dim r As New Run With {.ChatId = chatId, .Username = username, .ClientId = clientId, .DeviceId = deviceId, .Model = model, .StartedUtc = DateTime.UtcNow, .Auto = auto}
            _runs(chatId) = r
            Publish(username, chatId, "run", New With {.phase = "start", .model = model, .device = deviceId, .auto = auto}, clientId:=clientId)
            Return r
        End Function

        ''' <summary>Ends the run (once: a run already released by Stop, or replaced by a newer one, is left alone).</summary>
        Public Sub EndRun(r As Run)
            If Not DirectCast(_runs, ICollection(Of KeyValuePair(Of String, Run))).Remove(New KeyValuePair(Of String, Run)(r.ChatId, r)) Then Return
            Publish(r.Username, r.ChatId, "run", New With {.phase = "end"}, clientId:=r.ClientId)
        End Sub

        Private Function IsCurrent(r As Run) As Boolean
            Dim current As Run = Nothing
            Return _runs.TryGetValue(r.ChatId, current) AndAlso current Is r
        End Function

        Public Function IsLive(chatId As String) As Boolean
            Return _runs.ContainsKey(chatId)
        End Function

        ''' <summary>An answer is being written in the chat and was not stopped (a stopped one is only saving what it wrote).</summary>
        Public Function IsBusy(chatId As String) As Boolean
            Dim r As Run = Nothing
            Return _runs.TryGetValue(chatId, r) AndAlso Not r.Stopping
        End Function

        ''' <summary>Whose answer is being written in the chat (Nothing when none is).</summary>
        Public Function RunUser(chatId As String) As String
            Dim r As Run = Nothing
            Return If(_runs.TryGetValue(chatId, r), r.Username, Nothing)
        End Function

        ''' <summary>Remembers what was streamed (for windows that open the chat mid-answer) and passes it to every window.</summary>
        Public Sub Record(r As Run, type As String, data As Object)
            ' A run that was released (stopped and not ending) no longer shows anywhere.
            If Not IsCurrent(r) Then Return
            SyncLock r.Gate
                Select Case type
                    Case "start" : r.MessageId = TryCast(JsonUtil.ToNode(data)?("messageId"), JsonValue)?.ToString()
                    Case "delta"
                        Dim t = JsonUtil.ToNode(data)?("text")?.ToString()
                        r.Content.Append(t)
                        If Not String.IsNullOrWhiteSpace(t) Then r.LastKind = "delta"
                    Case "reasoning"
                        If r.LastKind <> "reasoning" Then r.ReasoningParts.Add(New ChatReasoningPart With {.Start = r.Reasoning.Length, .At = r.Content.Length, .Tools = r.Tools.Count})
                        r.LastKind = "reasoning"
                        r.Reasoning.Append(JsonUtil.ToNode(data)?("text")?.ToString())
                    Case "rollback"
                        ' A failed round is retried: what it wrote is taken back.
                        Dim n = JsonUtil.ToNode(data)
                        Dim drop = 0, dropR = 0
                        Integer.TryParse(n?("drop")?.ToString(), drop)
                        Integer.TryParse(n?("dropReasoning")?.ToString(), dropR)
                        r.Content.Length = Math.Max(0, r.Content.Length - Math.Max(0, drop))
                        r.Reasoning.Length = Math.Max(0, r.Reasoning.Length - Math.Max(0, dropR))
                        r.ReasoningParts.RemoveAll(Function(p) p.Start >= r.Reasoning.Length)
                        r.LastKind = Nothing
                    Case "status" : r.Status = JsonUtil.ToNode(data)?("text")?.ToString()
                    Case "route" : r.Route = data
                    Case "sources" : r.Sources = data
                    Case "tool"
                        Dim n = JsonUtil.ToNode(data)
                        r.LastKind = "tool"
                        If n?("phase")?.ToString() = "end" Then
                            Dim id = n("call")?("id")?.ToString()
                            r.Tools.RemoveAll(Function(t) JsonUtil.ToNode(t)?("id")?.ToString() = id)
                        End If
                        r.Tools.Add(n?("call"))
                        If n?("phase")?.ToString() = "start" AndAlso r.Content.Length > 0 AndAlso Not r.Content.ToString().EndsWith(vbLf) Then r.Content.Append(vbLf & vbLf)
                    Case "approval" : r.Approvals(If(JsonUtil.ToNode(data)?("id")?.ToString(), "")) = data
                    Case "approval_resolved" : r.Approvals.Remove(If(JsonUtil.ToNode(data)?("id")?.ToString(), ""))
                    Case "subagent"
                        Dim n = JsonUtil.ToNode(data)
                        Dim id = n?("id")?.ToString()
                        If id IsNot Nothing Then
                            Dim t As SubagentTranscript = Nothing
                            If Not r.Subagents.TryGetValue(id, t) Then t = New SubagentTranscript() : r.Subagents(id) = t
                            t.Apply(n)
                        End If
                End Select
            End SyncLock
            Publish(r.Username, r.ChatId, type, data, clientId:=r.ClientId)
        End Sub

        ''' <summary>Records an event for the chat's running answer (so windows that open it later see it), or just passes it on.</summary>
        Public Sub RecordChat(username As String, chatId As String, type As String, data As Object)
            Dim r As Run = Nothing
            If _runs.TryGetValue(chatId, r) AndAlso r.Username = username Then
                Record(r, type, data)
            Else
                Publish(username, chatId, type, data)
            End If
        End Sub

        ''' <summary>The answer so far, for a window that opens the chat while it is being written.</summary>
        Public Function Snapshot(username As String, chatId As String) As Object
            Dim r As Run = Nothing
            If Not _runs.TryGetValue(chatId, r) OrElse r.Username <> username Then Return New With {.active = False}
            SyncLock r.Gate
                Return New With {.active = True, .clientId = r.ClientId, .device = r.DeviceId, .auto = r.Auto, .model = r.Model, .messageId = r.MessageId, .startedUtc = r.StartedUtc,
                                 .content = r.Content.ToString(), .reasoning = r.Reasoning.ToString(), .reasoningParts = r.ReasoningParts.ToList(), .status = r.Status, .route = r.Route, .sources = r.Sources,
                                 .tools = r.Tools.ToList(), .approvals = r.Approvals.Values.ToList(),
                                 .subagents = r.Subagents.ToDictionary(Function(kv) kv.Key, Function(kv) kv.Value.Copy())}
            End SyncLock
        End Function

        Public Function ActiveRuns(username As String) As List(Of Object)
            Return _runs.Values.Where(Function(r) r.Username = username).
                Select(Function(r)
                           SyncLock r.Gate
                               Return CObj(New With {.chatId = r.ChatId, .device = r.DeviceId, .clientId = r.ClientId, .auto = r.Auto, .approvals = r.Approvals.Count})
                           End SyncLock
                       End Function).ToList()
        End Function

        ' ------------------------------------------------------------------ approvals

        ''' <summary>Waits for any of the user's windows to answer (once | always | deny); "deny" after the timeout.</summary>
        Public Function WaitForApproval(username As String, chatId As String, callId As String, info As Object) As Task(Of String)
            Dim p As New PendingApproval With {.Info = info}
            _approvals(Key(username, chatId & "|" & callId)) = p
            Return p.Waiter.Task
        End Function

        ''' <summary>
        ''' Answers a waiting approval. arguments (optional, JSON): the call as the user changed it before allowing it - an edit
        ''' with only some of its changes accepted; the caller takes them with TakeApprovedArguments.
        ''' </summary>
        Public Function ResolveApproval(username As String, chatId As String, callId As String, choice As String, Optional arguments As String = Nothing) As Boolean
            Dim p As PendingApproval = Nothing
            Dim k = Key(username, chatId & "|" & callId)
            If Not _approvals.TryRemove(k, p) Then Return False
            Dim allowed = choice = "always" OrElse choice = "once"
            Dim dropped As String = Nothing
            If allowed AndAlso Not String.IsNullOrWhiteSpace(arguments) Then _approvedArgs(k) = arguments Else _approvedArgs.TryRemove(k, dropped)
            Return p.Waiter.TrySetResult(If(allowed, choice, "deny"))
        End Function

        ''' <summary>The arguments the user changed while allowing this call (Nothing when they allowed it as it was).</summary>
        Public Function TakeApprovedArguments(username As String, chatId As String, callId As String) As String
            Dim v As String = Nothing
            Return If(_approvedArgs.TryRemove(Key(username, chatId & "|" & callId), v), v, Nothing)
        End Function

        Public Sub ForgetApproval(username As String, chatId As String, callId As String)
            Dim p As PendingApproval = Nothing
            _approvals.TryRemove(Key(username, chatId & "|" & callId), p)
        End Sub

        Public Function AllowedTools(chatId As String) As ConcurrentDictionary(Of String, Byte)
            Return _allowedTools.GetOrAdd(chatId, Function(k) New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase))
        End Function

        Public Function AllowedHosts(chatId As String) As ConcurrentDictionary(Of String, Byte)
            Return _allowedHosts.GetOrAdd(chatId, Function(k) New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase))
        End Function

    End Class

End Namespace
