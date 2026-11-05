Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Chats whose tools do real work get a coding agent's working practices, are asked once to check changes nobody ran, and
''' see where long tool output was cut. Audit 2026-11-02: chats "fixed" code they never ran, and long output was cut silently.
''' </summary>
Public Class ChatWorkTests

    Private Const WriteTool As String = "{""name"":""write_file"",""description"":""Write a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""},""content"":{""type"":""string""}}},""x-risk"":""change""}"
    Private Const BashTool As String = "{""name"":""run_command"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"
    Private Const ReadTool As String = "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""read""}"

    <Fact>
    Public Sub ToolsAreToldApartByWhatTheyDo()
        For Each n In {"write_file", "edit_file", "Edit", "MultiEdit", "Write", "replace_file_content", "code-exec__write_file", "NotebookEdit"}
            Assert.True(ChatService.IsEditTool(n), n)
        Next
        For Each n In {"read_file", "Read", "grep_search", "TodoWrite", "todo_write", "update_todos", "list_files"}
            Assert.False(ChatService.IsEditTool(n), n)
        Next
        For Each n In {"run_command", "Bash", "code-exec__run_command", "vscode_diagnostics", "run_code", "Monitor"}
            Assert.True(ChatService.IsRunTool(n), n)
        Next
        For Each n In {"read_file", "write_file", "Edit", "grep"}
            Assert.False(ChatService.IsRunTool(n), n)
        Next
    End Sub

    <Fact>
    Public Sub LongToolOutputKeepsItsStartAndEndAndSaysWhatWasCut()
        Assert.Equal("short", ChatService.ToolResultForModel("short"))
        Dim big = "BEGIN" & New String("x"c, 50000) & "END: 3 tests failed"
        Dim cut = ChatService.ToolResultForModel(big, 20000)
        Assert.StartsWith("BEGIN", cut)
        Assert.EndsWith("END: 3 tests failed", cut)
        Assert.Contains("characters left out here", cut)
        Assert.Contains("of 50,024 characters", cut)
        Assert.True(cut.Length < 20400, cut.Length.ToString())
    End Sub

    ''' <summary>Runs one message with the given client tools; the model's rounds come from <paramref name="round"/>.</summary>
    Private Shared Async Function RunAsync(round As Func(Of Integer, JsonNode, String), ParamArray toolsJson As String()) As Task(Of List(Of JsonNode))
        Using up As New FakeUpstream()
            Dim asked = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return round(Interlocked.Increment(asked), req)
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "fix the bug in app.py", .model = "fast/a", .settings = New With {.permissionMode = "auto"},
                                                               .clientTools = toolsJson.Select(Function(t) JsonNode.Parse(t)).ToArray()})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                    Async Function(evt, data)
                        If evt = "client_tool" Then
                            Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                        New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "ok"}, admin.Csrf)
                        End If
                    End Function)
                Return up.Requests.Where(Function(r) ChatFeatureTests.Streamed(r)).ToList()
            End Using
        End Using
    End Function

    Private Shared Function LastUserText(req As JsonNode) As String
        Dim msgs = req("messages").AsArray()
        Return msgs.Last(Function(m) m("role").ToString() = "user")("content").ToString()
    End Function

    <Fact>
    Public Async Function AChangeNobodyRanIsCheckedOnceBeforeTheReport() As Task
        Dim requests = Await RunAsync(Function(n, req)
                                          Select Case n
                                              Case 1 : Return ChatFeatureTests.ToolCallStream("write_file", "{""path"":""app.py"",""content"":""print(1)""}")
                                              Case 2 : Return ChatFeatureTests.TextStream("Fixed it.", "stop")
                                              Case 3 : Return ChatFeatureTests.ToolCallStream("run_command", "{""command"":""python app.py""}")
                                              Case Else : Return ChatFeatureTests.TextStream("Fixed and checked: it prints 1.", "stop")
                                          End Select
                                      End Function, WriteTool, BashTool)
        Assert.Equal(4, requests.Count)
        ' The working practices are in the prompt (tools that change things are connected).
        Dim system = requests(0)("messages")(0)("content").ToString()
        Assert.Contains("# Working with tools", system)
        Assert.Contains("Verify every change", system)
        ' After "Fixed it." with nothing run, the model was asked to check; after it ran the program, not again.
        Assert.Contains("you changed files but have not run anything", LastUserText(requests(2)))
        Dim lastPrompt = requests(3)("messages").ToJsonString()
        Assert.Equal(1, (lastPrompt.Length - lastPrompt.Replace("you changed files but have not run anything", "").Length) \ "you changed files but have not run anything".Length)
    End Function

    <Fact>
    Public Async Function ReadingOnlyOrCheckingAfterTheChangeIsNotNudged() As Task
        ' Edit then run: checked already.
        Dim checkedRequests = Await RunAsync(Function(n, req)
                                                 Select Case n
                                                     Case 1 : Return ChatFeatureTests.ToolCallStream("write_file", "{""path"":""app.py"",""content"":""print(1)""}")
                                                     Case 2 : Return ChatFeatureTests.ToolCallStream("run_command", "{""command"":""python app.py""}")
                                                     Case Else : Return ChatFeatureTests.TextStream("Fixed and checked.", "stop")
                                                 End Select
                                             End Function, WriteTool, BashTool)
        Assert.Equal(3, checkedRequests.Count)
        ' Only reading: nothing to check, and no practices note is needed for read-only tools.
        Dim readRequests = Await RunAsync(Function(n, req) If(n = 1, ChatFeatureTests.ToolCallStream("read_file", "{""path"":""app.py""}"), ChatFeatureTests.TextStream("It prints 1.", "stop")), ReadTool)
        Assert.Equal(2, readRequests.Count)
        Assert.DoesNotContain("# Working with tools", readRequests(0)("messages")(0)("content").ToString())
    End Function

End Class
