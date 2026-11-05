Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>Task list, subagents, steering, permission rules and output styles.</summary>
Public Class ClaudeCodeParityTests

    Private Shared Function Streamed(up As FakeUpstream) As List(Of JsonNode)
        Return up.Requests.ToArray().Where(AddressOf ChatFeatureTests.Streamed).ToList()
    End Function

    <Fact>
    Public Async Function TheModelKeepsATaskListAndCanHandWorkToASubagent() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("update_todos", "{""todos"":[{""content"":""Look around"",""status"":""in_progress""},{""content"":""Report"",""status"":""weird""}]}")
                                        Case 2 : Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Find the config"",""prompt"":""Find where the port is set and report it.""}")
                                        Case 3 : Return ChatFeatureTests.TextStream("All done.", "stop")
                                        ' Its list still showed open steps: asked once, it marks them done (and says nothing more).
                                        Case 4 : Return ChatFeatureTests.ToolCallStream("update_todos", "{""todos"":[{""content"":""Look around"",""status"":""completed""},{""content"":""Report"",""status"":""completed""}]}")
                                        Case Else : Return ChatFeatureTests.TextStream("", "stop")
                                    End Select
                                End Function
            ' The subagent's own (not streamed) call answers right away with its report.
            up.TextOverride = Function(req) "The port is set in config.json (8022)."
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.webSearch = True}})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "check the port"}))("message")
                Dim calls = message("toolCalls").AsArray()
                Dim todos = calls.First(Function(c) c("name").ToString() = "update_todos")
                Dim list = JsonNode.Parse(todos("arguments").ToString())("todos").AsArray()
                Assert.Equal("pending", list(1)("status").ToString())          ' unknown status cleaned up
                Assert.Contains("0 of 2 done", todos("result").ToString())
                Dim sub1 = calls.First(Function(c) c("name").ToString() = "task")
                Assert.True(sub1("ok").GetValue(Of Boolean)())
                Assert.Contains("config.json (8022)", sub1("result").ToString())
                ' The subagent got a fresh context: its own system prompt and the task, not the chat.
                Dim subReq = up.Requests.ToArray().First(Function(r) Not ChatFeatureTests.Streamed(r) AndAlso r("messages")(0)("content").ToString().Contains("You are a subagent"))
                Assert.Equal("Find where the port is set and report it.", subReq("messages")(1)("content").ToString())
                Assert.Equal("All done.", message("content").ToString().Trim())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AMessageAddedWhileTheAnswerRunsReachesTheModel() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim chatId As String = Nothing
            Dim host As TestHost = Nothing
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        ' The user types while the first step runs.
                                        Assert.True(host.H.Chat.Steer(chatId, "use port 9000 instead"))
                                        Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                    End If
                                    Return ChatFeatureTests.TextStream("Using 9000.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            host = env.Host
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' No answer running: steering is refused (the window sends it as a message instead).
                Assert.Equal(HttpStatusCode.Conflict, (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/steer", New With {.content = "x"}, admin.Csrf)).StatusCode)
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "set up the server"}))("message")
                Assert.Contains("use port 9000 instead", Streamed(up).Last()("messages").ToJsonString())
                Dim note = message("toolCalls").AsArray().First(Function(c) c("name").ToString() = "user_note")
                Assert.Contains("use port 9000 instead", note("arguments").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub PermissionRulesMatchToolsAndArguments()
        Dim r = PermissionRules.Parse("{""allow"":[""run_command(npm test*)"",""read_*""],""ask"":[""write_file(*.json)""],""deny"":[""run_command(rm *)"",""*__delete*""]}")
        Assert.Equal("allow", r.Decide("run_command", "{""command"":""npm test -- --watch""}"))
        Assert.Equal("deny", r.Decide("run_command", "{""command"":""rm -rf /""}"))
        Assert.Null(r.Decide("run_command", "{""command"":""git status""}"))
        Assert.Equal("ask", r.Decide("write_file", "{""path"":""package.json"",""content"":""{}""}"))
        Assert.Null(r.Decide("write_file", "{""path"":""a.md""}"))
        Assert.Equal("allow", r.Decide("read_file", "{""path"":""x""}"))
        Assert.Equal("deny", r.Decide("gitea__delete_repo", "{}"))
        Assert.Equal("run_command(rm *)", r.Matching("run_command", "{""command"":""rm x""}", "deny"))
    End Sub

    <Fact>
    Public Async Function ADenyRuleBlocksTheCallAndRulesAreSavedPerAccount() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                    Return ChatFeatureTests.TextStream("ok", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/permission-rules", New With {.deny = {"remember"}, .allow = New String() {}, .ask = New String() {}}, admin.Csrf)).IsSuccessStatusCode)
                Dim saved = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/permission-rules")).RootElement
                Assert.Equal("remember", saved.GetProperty("deny")(0).GetString())
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "remember I like tea"}))("message")
                Dim call1 = message("toolCalls").AsArray().First(Function(c) c("name").ToString() = "remember")
                Assert.False(call1("ok").GetValue(Of Boolean)())
                Assert.Contains("Blocked by the user's permission rule (remember)", call1("result").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AnOutputStyleShapesTheInstructions() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi", .settings = New With {.outputStyle = "Learning"}})
                Assert.Contains("Output style: learning", Streamed(up).Last()("messages")(0)("content").ToString())
                Assert.Null(ChatService.NormalizeStyle("fancy"))
            End Using
        End Using
    End Function
End Class
