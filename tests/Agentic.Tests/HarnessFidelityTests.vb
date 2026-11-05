Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The chat harness against Claude Code's behaviour: Accept edits mode, "Always allow" for a command covering that command's
''' program and subcommand (not every command), and a picture file a tool read going to the model without being added to the
''' answer.
''' </summary>
Public Class HarnessFidelityTests

    Private Const EditTool As String = "{""name"":""edit_file"",""description"":""Edit a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""change""}"
    Private Const RunTool As String = "{""name"":""run_command"",""description"":""Run a command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""change""}"
    Private Const ReadTool As String = "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""read""}"
    Private Const Png As String = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="

    <Fact>
    Public Sub ModesAndCommandScopes()
        Assert.Equal("acceptEdits", ChatService.NormalizeMode("acceptEdits"))
        Assert.Equal("acceptEdits", ChatService.NormalizeMode("accept-edits"))
        Assert.Equal("manual", ChatService.NormalizeMode("Manual"))
        Assert.Equal("auto", ChatService.NormalizeMode("whatever"))
        Assert.Equal({"npm run", "git status", "grep x"}, ChatService.CommandPrefixes("npm run test && git status 2>&1 | grep x").ToArray())
        Assert.Equal({"run_command npm test"}, ChatService.AllowKeys("run_command", "{""command"":""npm test -- --watch""}").ToArray())
        Assert.Equal({"edit_file"}, ChatService.AllowKeys("edit_file", "{""path"":""a.js""}").ToArray())
        Assert.Equal("npm test, rm", ChatService.AlwaysScope("run_command", "{""command"":""npm test; rm -rf x""}"))
    End Sub

    ''' <summary>One message whose model calls these tools in turn, then answers; every approval gets choice(call). Gives what was asked and the approvals' scopes.</summary>
    Private Shared Async Function RunAsync(mode As String, calls As (Name As String, Args As String)(), choice As Func(Of String, String, String),
                                           Optional toolResult As Func(Of String, Object) = Nothing) As Task(Of (Asked As List(Of String), Scopes As List(Of String), Events As List(Of String), Requests As List(Of JsonNode)))
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim requests As New List(Of JsonNode)
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    SyncLock requests
                                        requests.Add(req.DeepClone())
                                    End SyncLock
                                    Dim n = Interlocked.Increment(stepNo)
                                    If n <= calls.Length Then Return ChatFeatureTests.ToolCallStream(calls(n - 1).Name, calls(n - 1).Args)
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim asked As New List(Of String), scopes As New List(Of String), events As New List(Of String)
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "go", .model = "fast/a", .settings = New With {.permissionMode = mode},
                                                               .clientTools = {JsonNode.Parse(EditTool), JsonNode.Parse(RunTool), JsonNode.Parse(ReadTool)}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                    Async Function(evt, data)
                        events.Add(evt)
                        Select Case evt
                            Case "approval"
                                Dim name = data.GetProperty("name").GetString(), args = data.GetProperty("arguments").GetString()
                                asked.Add(name & " " & args)
                                Dim scope As Text.Json.JsonElement
                                scopes.Add(If(data.TryGetProperty("alwaysScope", scope) AndAlso scope.ValueKind = Text.Json.JsonValueKind.String, scope.GetString(), ""))
                                Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals",
                                                            New With {.id = data.GetProperty("id").GetString(), .choice = choice(name, args)}, admin.Csrf)
                            Case "client_tool"
                                Dim body = If(toolResult?.Invoke(data.GetProperty("name").GetString()), New With {.ok = True, .result = "ok"})
                                Dim payload = JsonNode.Parse(Text.Json.JsonSerializer.Serialize(body)).AsObject()
                                payload("id") = data.GetProperty("id").GetString()
                                Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", payload, admin.Csrf)
                        End Select
                    End Function)
                Return (asked, scopes, events, requests)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AlwaysAllowCoversTheCommandNotEveryCommand() As Task
        Dim r = Await RunAsync("manual", {("run_command", "{""command"":""npm test""}"), ("run_command", "{""command"":""npm test -- --watch""}"), ("run_command", "{""command"":""rm -rf build""}")},
                               Function(name, args) If(args.Contains("npm test"), "always", "once"))
        Assert.Equal(2, r.Asked.Count)
        Assert.Contains("npm test", r.Asked(0))
        Assert.Contains("rm -rf build", r.Asked(1))
        Assert.Equal("npm test", r.Scopes(0))
    End Function

    <Fact>
    Public Async Function AcceptEditsRunsEditsAndAsksBeforeCommands() As Task
        Dim r = Await RunAsync("acceptEdits", {("edit_file", "{""path"":""a.js""}"), ("run_command", "{""command"":""npm test""}")}, Function(name, args) "once")
        Assert.Single(r.Asked)
        Assert.StartsWith("run_command", r.Asked(0))
    End Function

    <Fact>
    Public Async Function APictureFileGoesToTheModelNotToTheAnswer() As Task
        Dim r = Await RunAsync("auto", {("read_file", "{""path"":""p.png""}")}, Function(name, args) "once",
                               Function(name) New With {.ok = True, .result = "p.png is a picture; it follows as an image.",
                                                         .image = New With {.mediaType = "image/png", .data = Png, .name = "p.png", .show = False}})
        Assert.DoesNotContain("image", r.Events)
        Dim second = r.Requests(1).ToJsonString()
        Assert.Contains("p.png", second)
        Assert.DoesNotContain("from your take_screenshot", second)
    End Function

End Class
