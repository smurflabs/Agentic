Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Parallel agents: subagents the model starts in one response run at the same time (and only one at a time when the chat
''' turns it off). Tool routing: a tool call for an app is sent to ONE of its windows — two VS Code windows of the same
''' workspace, or the side bar and an editor tab, never both run it — and each VS Code workspace is its own device.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class ParallelAgentsTests

    ''' <summary>One streamed round that calls the task tool several times at once.</summary>
    Private Shared Function TaskCallsStream(count As Integer) As String
        Dim calls As New JsonArray()
        For i = 0 To count - 1
            calls.Add(New JsonObject From {{"index", i}, {"id", "call_sub" & i}, {"type", "function"},
                {"function", New JsonObject From {{"name", "task"}, {"arguments", $"{{""description"":""Part {i}"",""subagent_type"":""explore"",""prompt"":""Look into part {i} and report.""}}"}}}})
        Next
        Return ChatFeatureTests.Chunk(New JsonObject From {{"tool_calls", calls}}) & ChatFeatureTests.Chunk(New JsonObject(), "tool_calls") & "data: [DONE]" & vbLf & vbLf
    End Function

    ''' <summary>Runs an answer that starts three subagents; returns how far apart their model calls were answered (and the message).</summary>
    Private Shared Async Function RunThreeSubagentsAsync(parallel As Boolean?) As Task(Of (Spread As TimeSpan, Message As JsonNode, Sse As String))
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim gate As New Object()
            Dim answered As New List(Of DateTime)
            ' Every model call takes 700 ms (waited asynchronously, so calls that arrive together are answered together).
            up.Delay = TimeSpan.FromMilliseconds(700)
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return TaskCallsStream(3)
                                    Return ChatFeatureTests.TextStream("All three parts are done.", "stop")
                                End Function
            ' The subagents' model calls (not streamed): when each was answered.
            up.TextOverride = Function(req)
                                  If ChatFeatureTests.Streamed(req) Then Return Nothing
                                  ' Only the subagents' own calls (the chat's title is asked for without streaming too).
                                  If Not (req("messages")?(0)?("content")?.ToString()).Contains("You are a subagent") Then Return Nothing
                                  SyncLock gate
                                      answered.Add(DateTime.UtcNow)
                                  End SyncLock
                                  Return "Report: this part is fine."
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' Web search on gives the chat a tool, so the task tool is offered.
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim settings As New JsonObject From {{"permissionMode", "auto"}, {"webSearch", True}}
                If parallel.HasValue Then settings("parallel") = parallel.Value
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New JsonObject From {{"content", "check all three parts"}, {"model", "fast/a"}, {"settings", settings}})
                Dim spread As TimeSpan
                SyncLock gate
                    spread = If(answered.Count < 2, TimeSpan.Zero, answered.Max() - answered.Min())
                End SyncLock
                Return (spread, ChatFeatureTests.Done(sse)("message"), sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SubagentsStartedTogetherRunAtTheSameTime() As Task
        Dim r = Await RunThreeSubagentsAsync(Nothing)
        ' Together: all three answered within a fraction of one call's 700 ms (one after another would take 1.4 s or more).
        Assert.True(r.Spread < TimeSpan.FromMilliseconds(500), $"the subagents should run together; their calls were {r.Spread.TotalMilliseconds:0} ms apart. Events: {String.Join(" | ", r.Sse.Split(ChrW(10)).Where(Function(l) l.Contains("status") OrElse l.StartsWith("event: tool")).Take(30))}")
        Dim subs = r.Message("toolCalls").AsArray().Where(Function(c) c("name").ToString() = "task").ToList()
        Assert.Equal(3, subs.Count)
        Assert.All(subs, Sub(c) Assert.Contains("this part is fine", c("result").ToString()))
        ' The results stay in the order the model asked for them.
        Assert.Equal({"call_sub0", "call_sub1", "call_sub2"}, subs.Select(Function(c) c("id").ToString()).ToArray())
        Assert.Equal("All three parts are done.", r.Message("content").ToString().Trim())
    End Function

    <Fact>
    Public Async Function WithParallelOffSubagentsRunOneAtATime() As Task
        Dim r = Await RunThreeSubagentsAsync(False)
        Assert.True(r.Spread >= TimeSpan.FromMilliseconds(1300), $"one at a time, the calls should be 700 ms apart each; they were {r.Spread.TotalMilliseconds:0} ms apart")
        Assert.Equal(3, r.Message("toolCalls").AsArray().Where(Function(c) c("name").ToString() = "task").Count())
    End Function

    <Fact>
    Public Sub ATaskCallGoesToOneWindowOfADevice()
        Dim hub As New ChatLiveHub()
        Dim older = hub.Subscribe("alice", "vscode-a-ws1", "PC · VS Code · ws1", "page1")
        Thread.Sleep(20)
        Dim newer = hub.Subscribe("alice", "vscode-a-ws1", "PC · VS Code · ws1", "page2")
        Dim other = hub.Subscribe("alice", "vscode-a-ws2", "PC · VS Code · ws2", "page3")
        ' The newest window of that device, never another device.
        Dim pick = hub.PickSubscriber("alice", "vscode-a-ws1")
        Assert.Equal(newer.Id, pick)
        ' Subscribing tells the user's other windows the device is online: clear that first.
        Dim drained As String = Nothing
        For Each s In {older, newer, other}
            While s.Channel.Reader.TryRead(drained)
            End While
        Next
        hub.PublishTo("alice", "c1", "client_tool", New With {.id = "t1"}, pick)
        Dim item As String = Nothing
        Assert.True(newer.Channel.Reader.TryRead(item))
        Assert.False(older.Channel.Reader.TryRead(item))
        Assert.False(other.Channel.Reader.TryRead(item))
        ' A page that reconnected its stream is preferred (it skips calls it already runs); onlyPage finds nothing else.
        Assert.Equal(older.Id, hub.PickSubscriber("alice", "vscode-a-ws1", except:=newer.Id, preferPage:="page1"))
        Assert.Equal(older.Id, hub.PickSubscriber("alice", "vscode-a-ws1", preferPage:="page1"))
        Assert.Null(hub.PickSubscriber("alice", "vscode-a-ws1", preferPage:="gone-page", onlyPage:=True))
        hub.Unsubscribe(newer)
        Assert.False(hub.SubscriberOnline(newer.Id))
        Assert.Equal(older.Id, hub.PickSubscriber("alice", "vscode-a-ws1"))
    End Sub

    ''' <summary>Reads one window's live stream in the background: counts the tool calls it gets and answers them.</summary>
    Private NotInheritable Class HubWindow
        Implements IDisposable
        Private ReadOnly _cts As New CancellationTokenSource()
        Public Calls As Integer
        Public ReadOnly Ready As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)

        Public Sub New(user As (Client As HttpClient, Csrf As String), device As String, page As String, chatId As String)
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Dim req As New HttpRequestMessage(HttpMethod.Get, $"/api/chat/stream?device={device}&name=test&page={page}")
                                          Using res = Await user.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, _cts.Token)
                                              Using reader As New StreamReader(Await res.Content.ReadAsStreamAsync(_cts.Token))
                                                  Dim evt As String = Nothing
                                                  While Not _cts.IsCancellationRequested
                                                      Dim line = Await reader.ReadLineAsync(_cts.Token)
                                                      If line Is Nothing Then Exit While
                                                      If line.StartsWith("event: ") Then evt = line.Substring(7)
                                                      If Not line.StartsWith("data: ") Then Continue While
                                                      If evt = "hello" Then Ready.TrySetResult(True)
                                                      If evt <> "chat" Then Continue While
                                                      Dim m = JsonNode.Parse(line.Substring(6))
                                                      If m("type")?.ToString() <> "client_tool" Then Continue While
                                                      Interlocked.Increment(Calls)
                                                      Await PlatformApiTests.Send(user.Client, "POST", $"/api/chat/conversations/{chatId}/tool-results",
                                                                                  New With {.id = m("data")("id").ToString(), .ok = True, .result = "ran in " & page}, user.Csrf)
                                                  End While
                                              End Using
                                          End Using
                                      Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException
                                      End Try
                                  End Function)
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            _cts.Cancel()
        End Sub
    End Class

    <Fact>
    Public Async Function AWebMessageUsingADeviceRunsItsToolInOneWindowOnly() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("Read", "{""file_path"":""notes.txt""}")
                                    Return ChatFeatureTests.TextStream("Read it.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim device = "vscode-test-ws1"
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' Two windows of the same workspace (the side bar and an editor tab) are online.
                Using w1 As New HubWindow(admin, device, "page1", id), w2 As New HubWindow(admin, device, "page2", id)
                    Await Task.WhenAll(w1.Ready.Task, w2.Ready.Task).WaitAsync(TimeSpan.FromSeconds(10))
                    Dim catalog = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/devices/{device}/catalog",
                        New JsonObject From {{"name", "PC · VS Code · ws1"}, {"clientTools", New JsonArray(JsonNode.Parse("{""name"":""Read"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""file_path"":{""type"":""string""}}},""x-risk"":""read""}"))}}, admin.Csrf)
                    Assert.True(catalog.IsSuccessStatusCode, Await catalog.Content.ReadAsStringAsync())
                    ' A message from the web app (no tools of its own) in a chat that uses this device.
                    Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New JsonObject From {{"content", "read my notes"}, {"model", "fast/a"},
                        {"settings", New JsonObject From {{"permissionMode", "auto"}, {"device", device}}}})
                    Dim message = ChatFeatureTests.Done(sse)("message")
                    Assert.Equal(1, w1.Calls + w2.Calls)
                    Dim read = message("toolCalls").AsArray().First(Function(c) c("name").ToString() = "Read")
                    Assert.True(read("ok").GetValue(Of Boolean)(), read("result")?.ToString())
                End Using
            End Using
        End Using
    End Function

    <Fact>
    Public Sub OfflineDevicesNotSeenForAWeekLeaveTheList()
        Dim hub As New ChatLiveHub()
        hub.SetCatalog("bob", "vscode-x-old", "PC · VS Code · old", New List(Of JsonObject), "")
        hub.GetDevice("bob", "vscode-x-old").UpdatedUtc = DateTime.UtcNow.AddDays(-8)
        hub.SetCatalog("bob", "vscode-x-new", "PC · VS Code · new", New List(Of JsonObject), "")
        Dim ids = hub.Devices("bob").Select(Function(d) JsonSerializer.SerializeToNode(d)("id").ToString()).ToList()
        Assert.Contains("vscode-x-new", ids)
        Assert.DoesNotContain("vscode-x-old", ids)
    End Sub

End Class
