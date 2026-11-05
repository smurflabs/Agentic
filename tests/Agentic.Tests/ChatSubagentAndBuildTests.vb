Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Xunit

''' <summary>
''' A chat subagent's calls on the user's computer carry the chat's permission mode (it asked again, or was refused, in Auto
''' mode); a proxy page instead of an answer no longer fails the whole chat answer; every reply names the dashboard version so
''' an open page notices the server was updated.
''' </summary>
Public Class ChatSubagentAndBuildTests

    Private Const BashTool As String = "{""name"":""Bash"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"

    Private Shared Async Function AskWithToolsAsync(admin As (Client As HttpClient, Csrf As String), chatId As String, onClientTool As Func(Of Text.Json.JsonElement, Task)) As Task
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = "check it", .model = "fast/a", .settings = New With {.permissionMode = "auto"}, .clientTools = {JsonNode.Parse(BashTool)}})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
            Async Function(evt, data)
                If evt = "client_tool" Then Await onClientTool(data)
            End Function)
    End Function

    <Fact>
    Public Async Function ASubagentsCallOnTheUsersComputerCarriesTheChatsPermissionMode() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Check the box"",""prompt"":""Run echo 42 and report the output.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            ' The subagent (not streamed) first calls Bash (written as text, which runs like a real call), then reports.
            up.TextOverride = Function(req)
                                  If req("messages").AsArray().Any(Function(m) m("role")?.ToString() = "tool") Then Return "The command printed 42."
                                  Return "<tool_call>{""name"": ""Bash"", ""arguments"": {""command"": ""echo 42""}}</tool_call>"
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim mode As String = Nothing
                Await AskWithToolsAsync(admin, id, Async Function(data)
                                                       mode = data.GetProperty("mode").ToString()
                                                       Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                                                   New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "42"}, admin.Csrf)
                                                   End Function)
                Assert.Equal("auto", mode)
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim task1 = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")
                Assert.Contains("printed 42", task1("result").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AProxyPageInsteadOfASubagentAnswerDoesNotFailTheChatAnswer() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Look it up"",""prompt"":""Find the answer.""}")
                                    Return ChatFeatureTests.TextStream("I did it myself.", "stop")
                                End Function
            up.ErrorOverride = Nothing
            up.RawNonStreamOverride = Function(req) "<html><body>502 Bad Gateway</body></html>"
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "find it"}))("message")
                Assert.Equal("I did it myself.", message("content").ToString().Trim())
                Dim sub1 = message("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")
                Assert.False(sub1("ok").GetValue(Of Boolean)())
                Assert.Contains("not an answer", sub1("result").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function EveryReplyNamesTheDashboardVersion() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await admin.Client.GetAsync("/api/chat/conversations")
                Dim build = res.Headers.GetValues("X-Agentic-Build").Single()
                Assert.Matches("^[0-9a-f]{12}$", build)
                ' The same version on every reply (it changes only when the dashboard's files do).
                Dim again = Await admin.Client.GetAsync("/api/chat/conversations")
                Assert.Equal(build, again.Headers.GetValues("X-Agentic-Build").Single())
            End Using
        End Using
    End Function

End Class
