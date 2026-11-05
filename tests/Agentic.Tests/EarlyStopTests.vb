Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>Things that used to end an answer early, though the user would have wanted it to go on.</summary>
Public Class EarlyStopTests

    Private Const ReadTool As String = "{""name"":""Bash"",""description"":""Run a command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""read""}"

    ''' <summary>Sends a message with one client tool; each client_tool event is answered with resultOf(n) (n = 1, 2, …).</summary>
    Private Shared Async Function AskWithToolAsync(admin As (Client As HttpClient, Csrf As String), chatId As String, toolJson As String,
                                                   resultOf As Func(Of Integer, String)) As Task(Of (Calls As Integer, Done As JsonElement))
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = "build it", .model = "fast/a", .settings = New With {.permissionMode = "auto"},
                                                       .clientTools = {JsonNode.Parse(toolJson)}})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Dim calls = 0
        Dim done As JsonElement = Nothing
        Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                              If evt = "client_tool" Then
                                                                                                                                  calls += 1
                                                                                                                                  Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = resultOf(calls)}, admin.Csrf)
                                                                                                                              End If
                                                                                                                              If evt = "done" Then done = data.Clone()
                                                                                                                          End Function)
        Return (calls, done)
    End Function

    <Fact>
    Public Async Function RunningTheSameCommandAgainAfterEachFixIsNotALoop() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    ' The same build command 8 times (the result changes each time), then the answer.
                                    Return If(Interlocked.Increment(stepNo) <= 8, ChatFeatureTests.ToolCallStream("Bash", "{""command"":""dotnet build""}"),
                                              ChatFeatureTests.TextStream("It builds now.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim r = Await AskWithToolAsync(admin, id, ReadTool, Function(n) $"{9 - n} errors left")
                Assert.Equal(8, r.Calls)
                Assert.Contains("It builds now.", r.Done.GetProperty("message").GetProperty("content").GetString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheSameCallWithTheSameResultRightAwayIsStillStopped() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(stepNo) <= 5, ChatFeatureTests.ToolCallStream("Bash", "{""command"":""cat status""}"),
                                              ChatFeatureTests.TextStream("Still pending.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim r = Await AskWithToolAsync(admin, id, ReadTool, Function(n) "pending")
                ' Three identical results in a row, at once: the 4th and 5th are not run.
                Assert.Equal(3, r.Calls)
                Dim notes = r.Done.GetProperty("message").GetProperty("toolCalls").EnumerateArray().Where(Function(t) t.GetProperty("result").GetString().StartsWith("Not run")).Count()
                Assert.Equal(2, notes)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ARefusedToolSchemaIsSentAgainPlainer() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            ' A strict provider: refuses JSON Schema keywords it does not know (format, $schema) in tool parameters.
            up.ErrorOverride = Function(req)
                                   Dim tools = req("tools")?.ToJsonString()
                                   If tools IsNot Nothing AndAlso (tools.Contains("""format""") OrElse tools.Contains("$schema")) Then Return "Invalid schema for function 'Bash': 'format' is not supported"
                                   Return Nothing
                               End Function
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(stepNo) = 1, ChatFeatureTests.ToolCallStream("Bash", "{""command"":""ls""}"), ChatFeatureTests.TextStream("Listed.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' Thinking off as well: the request carries the thinking switches, which used to hide every other 400 cause.
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.thinking = False}})
                Dim tool = "{""name"":""Bash"",""description"":""Run"",""parameters"":{""$schema"":""http://json-schema.org/draft-07/schema#"",""type"":""object""," &
                           """properties"":{""command"":{""type"":""string"",""format"":""text""}},""required"":[""command"",""ghost""]},""x-risk"":""read""}"
                Dim r = Await AskWithToolAsync(admin, id, tool, Function(n) "a.txt")
                Assert.Equal(1, r.Calls)
                Assert.Equal("Listed.", r.Done.GetProperty("message").GetProperty("content").GetString().Trim())
                ' The plain schema kept the parameter and only the required names that exist.
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)("tools").AsArray().First(Function(t) t("function")("name").ToString() = "Bash")("function")("parameters")
                Assert.Equal("string", sent("properties")("command")("type").ToString())
                Assert.Null(sent("properties")("command")("format"))
                Assert.Equal("[""command""]", sent("required").ToJsonString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AnAnswerWithOnlyToolCallsGetsItsWordsWritten() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Dim n = Interlocked.Increment(stepNo)
                                    If n = 1 Then Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                    ' Empty replies, also after being asked twice to go on - until the tools are off.
                                    If req("tools") IsNot Nothing Then Return ChatFeatureTests.TextStream("", "stop")
                                    Return ChatFeatureTests.TextStream("Saved that you like tea.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "remember I like tea"}))("message")
                Assert.Equal("Saved that you like tea.", message("content").ToString().Trim())
                Assert.Contains("you have not written any answer yet", ChatFeatureTests.LastStreamedRequest(up)("messages").ToJsonString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AHugePasteIsShortenedInsteadOfFailing() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.ContextWindows("fast/a") = 16000)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim log = "START OF LOG" & vbLf & String.Join(vbLf, Enumerable.Range(1, 6000).Select(Function(i) $"line {i}: something happened here")) & vbLf & "END OF LOG"
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "what failed? " & log}))("message")
                Assert.True(String.IsNullOrEmpty(message("error")?.ToString()), message("error")?.ToString())
                Dim sentUser = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray().Last(Function(m) m("role").ToString() = "user")("content").ToString()
                Assert.Contains("characters cut to fit", sentUser)
                Assert.Contains("START OF LOG", sentUser)
                Assert.Contains("END OF LOG", sentUser)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub ALongRunInsideACodeBlockIsNotARepetitionLoop()
        Dim code = "Here is the buffer:" & vbLf & "```c" & vbLf & "uint8_t z[] = {" & String.Concat(Enumerable.Repeat("0x00, ", 400)) & "};" & vbLf
        Assert.True(ChatService.FindRepetition(code, 0) < 0)
        ' The same run outside a code block is a loop.
        Dim prose = "Here is the buffer: " & String.Concat(Enumerable.Repeat("0x00, ", 400))
        Assert.True(ChatService.FindRepetition(prose, 0) >= 0)
        ' A model stuck in a loop inside code still gets caught once it runs on.
        Dim stuck = "```c" & vbLf & String.Concat(Enumerable.Repeat("foo(bar); ", 600))
        Assert.True(ChatService.FindRepetition(stuck, 0) >= 0)
    End Sub

End Class

''' <summary>The user's per-chat tool limits (/limits) cap the rounds the model gets.</summary>
Public Class ToolLimitTests
    Private Const ReadTool As String = "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""read""}"

    <Fact>
    Public Async Function MaxToolRoundsSettingCapsTheAnswer() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    ' Always wants another read; only the round cap stops it.
                                    If req("tools") IsNot Nothing Then Return ChatFeatureTests.ToolCallStream("read_file", "{""path"":""x""}")
                                    Return ChatFeatureTests.TextStream("Final.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "read everything", .model = "fast/a", .settings = New With {.permissionMode = "auto", .maxToolRounds = 2},
                        .clientTools = {JsonNode.Parse(ReadTool)}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim toolCalls = 0
                Dim stopReason As String = Nothing
                Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                                     If evt = "client_tool" Then
                                                                                                                                         toolCalls += 1
                                                                                                                                         Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "x"}, admin.Csrf)
                                                                                                                                     End If
                                                                                                                                     If evt = "done" Then stopReason = data.GetProperty("stopReason").GetString()
                                                                                                                                 End Function)
                ' Capped at 2 rounds, then the model is given a no-tools round and answers (no 80-round runaway).
                Assert.Equal(2, toolCalls)
            End Using
        End Using
    End Function
End Class
