Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Runtime.CompilerServices
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' MCP tools on the user's computer: many of them wait until the model loads them (load_tools, or calling one by name) and
''' stay loaded for the chat; a tool result can carry several pictures, and more than 1 MB of them.
''' </summary>
Public Class McpClientToolsTests

    Private Const ReadTool As String = "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""read""}"

    Private Shared Function McpTools(count As Integer) As List(Of JsonNode)
        Dim list As New List(Of JsonNode) From {JsonNode.Parse(ReadTool)}
        For i = 1 To count
            list.Add(JsonNode.Parse($"{{""name"":""mcp__big__tool{i}"",""description"":""Big server tool number {i}{If(i = 17, " that creates an issue", "")}"",""parameters"":{{""type"":""object"",""properties"":{{}}}},""x-risk"":""read""}}"))
        Next
        Return list
    End Function

    ''' <summary>One message: the model makes these calls in turn, then answers. Gives the requests the model got and the client calls.</summary>
    Private Shared Async Function MessageAsync(admin As (Client As HttpClient, Csrf As String), id As String, calls As (Name As String, Args As String)(),
                                               requests As List(Of JsonNode), stepNo As StrongBox(Of Integer), plan As List(Of (Name As String, Args As String)),
                                               Optional toolResult As Func(Of String, Object) = Nothing, Optional tools As List(Of JsonNode) = Nothing,
                                               Optional clientRules As Object = Nothing, Optional approvals As List(Of String) = Nothing) As Task(Of List(Of String))
        plan.Clear()
        plan.AddRange(calls)
        stepNo.Value = 0
        Dim ran As New List(Of String)
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = "go", .model = "fast/a", .settings = New With {.permissionMode = "auto"}, .clientTools = If(tools, McpTools(40)), clientRules})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
            Async Function(evt, data)
                If evt = "approval" Then approvals?.Add(data.GetProperty("name").GetString() & ": " & data.GetProperty("reason").GetString())
                If evt = "approval" Then
                    Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals", New With {.id = data.GetProperty("id").GetString(), .choice = "once"}, admin.Csrf)
                ElseIf evt = "client_tool" Then
                    Dim name = data.GetProperty("name").GetString()
                    ran.Add(name)
                    Dim body = If(toolResult?.Invoke(name), New With {.ok = True, .result = "ran " & name})
                    Dim payload = JsonNode.Parse(Text.Json.JsonSerializer.Serialize(body)).AsObject()
                    payload("id") = data.GetProperty("id").GetString()
                    Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", payload, admin.Csrf)
                End If
            End Function)
        Return ran
    End Function

    Private Shared Function ToolNames(req As JsonNode) As List(Of String)
        Return If(req("tools")?.AsArray(), New JsonArray()).Select(Function(t) t("function")("name").ToString()).ToList()
    End Function

    <Fact>
    Public Async Function ManyLocalMcpToolsWaitUntilTheModelLoadsThem() As Task
        Using up As New FakeUpstream()
            Dim requests As New List(Of JsonNode)
            Dim stepNo As New StrongBox(Of Integer)(0)
            Dim plan As New List(Of (Name As String, Args As String))
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    SyncLock requests
                                        requests.Add(req.DeepClone())
                                    End SyncLock
                                    Dim n = Interlocked.Increment(stepNo.Value)
                                    If n <= plan.Count Then Return ChatFeatureTests.ToolCallStream(plan(n - 1).Name, plan(n - 1).Args)
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' A search loads the matching tool; calling a waiting tool by its exact name loads and runs it too.
                Dim ran = Await MessageAsync(admin, id, {("load_tools", "{""query"":""creates an issue""}"), ("mcp__big__tool17", "{}"), ("mcp__big__tool3", "{}")}, requests, stepNo, plan)
                Dim first = ToolNames(requests(0))
                Assert.Contains("read_file", first)
                Assert.Contains("load_tools", first)
                Assert.DoesNotContain("mcp__big__tool17", first)
                Assert.Contains("wait to be loaded", requests(0)("messages")(0)("content").ToString())
                Assert.Contains("tool17", requests(0)("messages")(0)("content").ToString())
                Assert.Contains("mcp__big__tool17", ToolNames(requests(1)))
                Assert.Equal({"mcp__big__tool17", "mcp__big__tool3"}, ran.ToArray())
                ' The next message has them from the start (the chat remembers what it loaded); the others still wait.
                requests.Clear()
                Await MessageAsync(admin, id, Array.Empty(Of (String, String))(), requests, stepNo, plan)
                Dim next1 = ToolNames(requests(0))
                Assert.Contains("mcp__big__tool17", next1)
                Assert.Contains("mcp__big__tool3", next1)
                Assert.DoesNotContain("mcp__big__tool5", next1)
                ' A few MCP tools go as they are.
                requests.Clear()
                Await MessageAsync(admin, id, Array.Empty(Of (String, String))(), requests, stepNo, plan, tools:=McpTools(5))
                Dim few = ToolNames(requests(0))
                Assert.Contains("mcp__big__tool5", few)
                Assert.DoesNotContain("load_tools", few)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AToolResultCanCarrySeveralLargePictures() As Task
        ' Two pictures of 900 KB each: the body is over 2 MB as base64 (the usual request limit is 1 MB).
        Dim big = Convert.ToBase64String(Enumerable.Range(0, 900 * 1024).Select(Function(i) CByte(i Mod 251)).ToArray())
        Using up As New FakeUpstream()
            Dim requests As New List(Of JsonNode)
            Dim stepNo As New StrongBox(Of Integer)(0)
            Dim plan As New List(Of (Name As String, Args As String))
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    SyncLock requests
                                        requests.Add(req.DeepClone())
                                    End SyncLock
                                    Dim n = Interlocked.Increment(stepNo.Value)
                                    If n <= plan.Count Then Return ChatFeatureTests.ToolCallStream(plan(n - 1).Name, plan(n - 1).Args)
                                    Return ChatFeatureTests.TextStream("I see two pages.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await MessageAsync(admin, id, {("mcp__big__tool1", "{}")}, requests, stepNo, plan,
                                   Function(name) New With {.ok = True, .result = "[picture 1] [picture 2]",
                                                             .images = {New With {.mediaType = "image/png", .data = big, .show = True}, New With {.mediaType = "image/jpeg", .data = big, .show = True}}},
                                   McpTools(3))
                Dim msgs = requests.Last()("messages").AsArray()
                Dim toolAt = msgs.ToList().FindIndex(Function(m) m("role")?.ToString() = "tool")
                Assert.True(toolAt >= 0)
                Assert.Contains("Picture 1 follows as a picture.", msgs(toolAt)("content").ToString())
                Dim pics = msgs(toolAt + 1)("content").AsArray().Where(Function(p) p("type")?.ToString() = "image_url").ToList()
                Assert.Equal(2, pics.Count)
                Dim saved = Text.Json.JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement
                Dim answer = saved.GetProperty("messages").EnumerateArray().Last()
                Assert.Equal(2, answer.GetProperty("attachments").EnumerateArray().Count(Function(a) a.GetProperty("kind").GetString() = "image"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheComputersRulesApplyAndAdminCommandsAlwaysAsk() As Task
        Const RunTool As String = "{""name"":""run_command"",""description"":""Run a command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""},""elevated"":{""type"":""boolean""}}},""x-risk"":""change""}"
        Using up As New FakeUpstream()
            Dim requests As New List(Of JsonNode)
            Dim stepNo As New StrongBox(Of Integer)(0)
            Dim plan As New List(Of (Name As String, Args As String))
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    SyncLock requests
                                        requests.Add(req.DeepClone())
                                    End SyncLock
                                    Dim n = Interlocked.Increment(stepNo.Value)
                                    If n <= plan.Count Then Return ChatFeatureTests.ToolCallStream(plan(n - 1).Name, plan(n - 1).Args)
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim tools As New List(Of JsonNode) From {JsonNode.Parse(RunTool), JsonNode.Parse(ReadTool)}
                ' A deny rule from the computer's settings files blocks the call (Auto mode); other calls run.
                Dim ran = Await MessageAsync(admin, id, {("run_command", "{""command"":""rm -rf build""}"), ("read_file", "{""path"":""a.txt""}")}, requests, stepNo, plan,
                                             tools:=tools, clientRules:=New With {.deny = {"run_command(rm*)"}})
                Assert.Equal({"read_file"}, ran.ToArray())
                Assert.Contains("permission rule (run_command(rm*))", requests.Last().ToJsonString())
                ' A command as administrator asks first, in Auto mode too; the same command without it does not.
                Dim asked As New List(Of String)
                ran = Await MessageAsync(admin, id, {("run_command", "{""command"":""net stop spooler"",""elevated"":true}"), ("run_command", "{""command"":""dir""}")}, requests, stepNo, plan,
                                         tools:=tools, approvals:=asked)
                Assert.Single(asked)
                Assert.Contains("administrator", asked(0))
                Assert.Equal(2, ran.Count)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub LoadToolsFindsByNameServerAndWords()
        Dim waiting = {("mcp__github__create_issue", "Create a new issue in a repository"), ("mcp__github__list_issues", "List issues"),
                       ("mcp__browser__screenshot", "Take a screenshot of the page"), ("vscode_tool__gitlens_blame", "Who changed this line")}
        Assert.Equal({"mcp__github__create_issue"}, ChatService.PickDeferredTools("{""names"":[""create_issue""]}", waiting).ToArray())
        Assert.Equal({"mcp__github__create_issue", "mcp__github__list_issues"}, ChatService.PickDeferredTools("{""names"":[""mcp__github__*""]}", waiting).ToArray())
        Assert.Equal("mcp__browser__screenshot", ChatService.PickDeferredTools("{""query"":""screenshot""}", waiting).First())
        Assert.Empty(ChatService.PickDeferredTools("{""names"":[""nope""]}", waiting))
        Assert.Equal("mcp__github", ChatService.ServerOfClientTool("mcp__github__create_issue"))
        Assert.True(ChatService.ShouldDeferClientTools(Enumerable.Range(0, 30).Select(Function(i) New JsonObject From {{"name", "mcp__x__t" & i}})))
        Assert.False(ChatService.ShouldDeferClientTools(Enumerable.Range(0, 5).Select(Function(i) New JsonObject From {{"name", "mcp__x__t" & i}})))
    End Sub

End Class
