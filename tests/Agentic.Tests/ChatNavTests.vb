Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' What the chat in every app needs around the conversation: a ! command kept in the chat (the model sees it with the next
''' message), the user's earlier prompts (Ctrl+R), an earlier version read without switching to it, /usage, /feedback to the
''' Inbox, pinned messages, and an edit allowed with some of its changes left out.
''' </summary>
Public Class ChatNavTests

    Private Const WriteTool As String = "{""name"":""write_file"",""description"":""Write a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""},""content"":{""type"":""string""}}},""x-risk"":""change""}"

    <Fact>
    Public Sub ShellMessagesUseClaudeCodesForm()
        Dim c = ChatService.ShellContent("ls -la", "a.txt" & vbLf & "b.txt" & vbLf, 2, "my-pc")
        Assert.Equal("<bash-input>ls -la</bash-input>" & vbLf & "<bash-stdout>a.txt" & vbLf & "b.txt</bash-stdout>" & vbLf & "<bash-exit-code>2</bash-exit-code>" & vbLf & "<bash-where>my-pc</bash-where>", c)
        Assert.DoesNotContain("bash-exit-code", ChatService.ShellContent("pwd", "/x", 0, Nothing))
        Dim huge = ChatService.ShellContent("cat big", New String("x"c, 50000), 0, Nothing)
        Assert.Contains("characters left out", huge)
        Assert.True(huge.Length < 32000)
    End Sub

    Private Shared Async Function PostJson(user As (Client As HttpClient, Csrf As String), path As String, body As Object) As Task(Of JsonNode)
        Dim res = Await PlatformApiTests.Send(user.Client, "POST", path, body, user.Csrf)
        Assert.True(res.IsSuccessStatusCode, path & ": " & CInt(res.StatusCode) & " " & Await res.Content.ReadAsStringAsync())
        Return Await ChatFeatureTests.JsonOf(res)
    End Function

    Private Shared Async Function GetJson(user As (Client As HttpClient, Csrf As String), path As String) As Task(Of JsonNode)
        Dim res = Await user.Client.GetAsync(path)
        Assert.True(res.IsSuccessStatusCode, path & ": " & CInt(res.StatusCode) & " " & Await res.Content.ReadAsStringAsync())
        Return JsonNode.Parse(Await res.Content.ReadAsStringAsync())
    End Function

    Private Shared Async Function SendAsync(user As (Client As HttpClient, Csrf As String), chatId As String, body As Object,
                                            Optional onEvent As Func(Of String, JsonElement, Task) = Nothing) As Task
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {.Content = HttpApiTests.JsonBody(body)}
        req.Headers.Add("X-CSRF-Token", user.Csrf)
        Await PlatformApiTests.ReadEventsAsync(Await user.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                                               Async Function(evt, data)
                                                   If onEvent IsNot Nothing Then Await onEvent(evt, data)
                                               End Function)
    End Function

    ''' <summary>A ! command is a message of the chat; with the next question the model gets both in one user turn.</summary>
    <Fact>
    Public Async Function ShellCommandsGoIntoTheChatAndTheHistory() As Task
        Using up As New FakeUpstream()
            Dim seen As New List(Of JsonNode)
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    SyncLock seen
                                        seen.Add(req.DeepClone())
                                    End SyncLock
                                    Return ChatFeatureTests.TextStream("Two files.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim m = Await PostJson(admin, $"/api/chat/conversations/{id}/shell", New With {.command = "ls", .output = "a.txt" & vbLf & "b.txt", .exitCode = 0, .where = "laptop"})
                Assert.Equal("shell", m("finishReason")?.ToString())
                Assert.Equal("user", m("role")?.ToString())
                Await SendAsync(admin, id, New With {.content = "what files are there?", .model = "fast/a"})
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim msgs = chat("messages").AsArray()
                Assert.Equal(3, msgs.Count)
                Assert.Equal("shell", msgs(0)("finishReason")?.ToString())
                ' The model got one user turn with the command, its output and the question.
                Dim sent = seen.Last()("messages").AsArray().Where(Function(x) x("role")?.ToString() <> "system").ToList()
                Assert.Single(sent)
                Dim text = sent(0)("content").ToString()
                Assert.Contains("<bash-input>ls</bash-input>", text)
                Assert.Contains("a.txt", text)
                Assert.Contains("what files are there?", text)
                ' Ctrl+R: typed questions only (not the ! command), newest first, each once.
                Await SendAsync(admin, id, New With {.content = "what files are there?", .model = "fast/a"})
                Dim other = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await SendAsync(admin, other, New With {.content = "hello there", .model = "fast/a"})
                Dim hist = (Await GetJson(admin, "/api/chat/prompt-history?limit=10")).AsArray()
                Assert.Equal({"hello there", "what files are there?"}, hist.Select(Function(h) h("text").ToString()).ToArray())
                Dim filtered = (Await GetJson(admin, "/api/chat/prompt-history?q=FILES")).AsArray()
                Assert.Single(filtered)
                Assert.Equal(id, filtered(0)("chatId").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AnEarlierVersionIsReadWithoutSwitching() As Task
        Using up As New FakeUpstream()
            Dim n = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return ChatFeatureTests.TextStream("Answer " & Interlocked.Increment(n), "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await SendAsync(admin, id, New With {.content = "question", .model = "fast/a"})
                Await SendAsync(admin, id, New With {.content = "", .model = "fast/a", .regenerate = True})
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Assert.Equal("Answer 2", chat("messages").AsArray().Last()("content").ToString())
                Dim point = chat("versions").AsArray().Single()
                Dim branchId = point("versions").AsArray().First(Function(v) v("branchId") IsNot Nothing).Item("branchId").ToString()
                Dim old = (Await GetJson(admin, $"/api/chat/conversations/{id}/versions/{branchId}")).AsArray()
                Assert.Equal("Answer 1", old(0)("content").ToString())
                ' Still on the newest version.
                Assert.Equal("Answer 2", (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray().Last()("content").ToString())
                Dim missing = Await admin.Client.GetAsync($"/api/chat/conversations/{id}/versions/ver_nope")
                Assert.Equal(404, CInt(missing.StatusCode))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function UsageFeedbackAndPins() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), ChatFeatureTests.TextStream("Fine.", "stop"), Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await SendAsync(admin, id, New With {.content = "hi", .model = "fast/a"})

                ' /usage: this account's calls today, by model.
                Dim u = Await GetJson(admin, "/api/chat/usage")
                Assert.True(u("today")("calls").GetValue(Of Long)() >= 1, u.ToJsonString())
                Assert.Contains(u("byModel").AsArray(), Function(r) r("model").ToString() = "fast/a")
                Assert.NotNull(u("limits"))
                Assert.True(u("daily").AsArray().Count >= 1)

                ' /feedback: an Inbox item with a report file (the words and the chat); the reply comes back to the user.
                Dim fb = Await PostJson(admin, "/api/chat/feedback", New With {.text = "The answer was cut short." & vbLf & "Details here.", .chatId = id, .includeChat = True, .logs = "line one", .app = "CLI", .version = "2.3.26"})
                Dim apr = (Await GetJson(admin, "/api/approvals?status=Pending&limit=50")).AsArray().Single(Function(a) a("kind").ToString() = "Feedback")
                Assert.Equal(fb("id").ToString(), apr("id").ToString())
                Assert.StartsWith("Feedback from admin: The answer was cut short.", apr("title").ToString())
                Dim ctx = JsonNode.Parse(apr("context").ToString())
                Assert.Equal(id, ctx("chatId").ToString())
                Dim file = Await admin.Client.GetStringAsync($"/api/chat/files/{ctx("fileId")}")
                Assert.Contains("## What happened", file)
                Assert.Contains("## Chat transcript", file)
                Assert.Contains("hi", file)
                Assert.Contains("line one", file)
                Assert.Contains("CLI 2.3.26", file)
                Dim resolved = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/approvals/{apr("id")}/resolve", New With {.status = "Answered", .response = "Fixed in 2.0.23"}, admin.Csrf)
                Assert.True(resolved.IsSuccessStatusCode, Await resolved.Content.ReadAsStringAsync())
                Dim mine = (Await GetJson(admin, "/api/chat/feedback")).AsArray()
                Assert.Equal("Fixed in 2.0.23", mine.Single()("reply").ToString())

                ' Pins: kept with the chat's settings; a branch does not take them.
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim answerId = chat("messages").AsArray().Last()("id").ToString()
                Dim patched = Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{id}", New With {.settings = New With {.pinnedMessages = {answerId, answerId, ""}}}, admin.Csrf)
                Assert.True(patched.IsSuccessStatusCode, Await patched.Content.ReadAsStringAsync())
                Dim pins = (Await ChatFeatureTests.GetChatAsync(admin, id))("settings")("pinnedMessages").AsArray()
                Assert.Equal({answerId}, pins.Select(Function(p) p.ToString()).ToArray())
                Dim branch = (Await PostJson(admin, $"/api/chat/conversations/{id}/fork", New With {.title = "b"}))("id").ToString()
                Assert.Null((Await ChatFeatureTests.GetChatAsync(admin, branch))("settings")("pinnedMessages"))
            End Using
        End Using
    End Function

    ''' <summary>Allowed with some changes left out: the tool runs with the arguments the user kept, and the model hears it.</summary>
    <Fact>
    Public Async Function AnEditAllowedInPartRunsWithWhatTheUserKept() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim lastRequest As JsonNode = Nothing
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    lastRequest = req.DeepClone()
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("write_file", "{""path"":""a.txt"",""content"":""one\nTWO\nthree\nFOUR\n""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim ran As String = Nothing
                Await SendAsync(admin, id, New With {.content = "edit it", .model = "fast/a", .settings = New With {.permissionMode = "manual"}, .clientTools = {JsonNode.Parse(WriteTool)}},
                                Async Function(evt, data)
                                    Select Case evt
                                        Case "approval"
                                            Await PostJson(admin, $"/api/chat/conversations/{id}/approvals",
                                                           New With {.id = data.GetProperty("id").GetString(), .choice = "once", .arguments = "{""path"":""a.txt"",""content"":""one\nTWO\nthree\nfour\n""}"})
                                        Case "client_tool"
                                            ran = data.GetProperty("arguments").GetString()
                                            Await PostJson(admin, $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "Overwrote a.txt."})
                                    End Select
                                End Function)
                Assert.NotNull(ran)
                Assert.Contains("four", ran)
                Assert.DoesNotContain("FOUR", ran)
                Dim tool = lastRequest("messages").AsArray().Last(Function(m) m("role")?.ToString() = "tool")("content").ToString()
                Assert.Contains("accepted only some of the changes", tool)
                ' The saved call shows what ran.
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Assert.Contains("four", chat("messages").AsArray().Last()("toolCalls")(0)("arguments").ToString())
                ' Arguments that are not a JSON object are refused.
                Dim bad = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals", New With {.id = "x", .choice = "once", .arguments = "[1]"}, admin.Csrf)
                Assert.Equal(400, CInt(bad.StatusCode))
            End Using
        End Using
    End Function

    ''' <summary>A client's own question (the CLI's) answered in a window with some changes left out: the client gets the arguments.</summary>
    <Fact>
    Public Async Function AClientsQuestionComesBackWithTheKeptArguments() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim ask = PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals/request",
                                                New With {.id = "cli-1", .name = "Edit", .arguments = "{""file_path"":""a"",""old_string"":""x"",""new_string"":""y\nz""}", .reason = "it edits", .where = "CLI"}, admin.Csrf)
                Dim answered = False
                For i = 1 To 50
                    Dim r = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals",
                                                        New With {.id = "cli-1", .choice = "once", .arguments = "{""file_path"":""a"",""old_string"":""x"",""new_string"":""y""}"}, admin.Csrf)
                    If r.IsSuccessStatusCode Then answered = True : Exit For
                    Await Task.Delay(100)
                Next
                Assert.True(answered)
                Dim back = Await ChatFeatureTests.JsonOf(Await ask)
                Assert.Equal("once", back("choice").ToString())
                Assert.Contains("""new_string"":""y""", back("arguments").ToString())
            End Using
        End Using
    End Function

End Class
