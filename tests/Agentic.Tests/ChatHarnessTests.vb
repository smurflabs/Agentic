Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The chat harness: in Manual mode reading never asks (only changes do, like Claude Code), the wait for an approval is said
''' at once, and a failing model is retried soon at first (5 s, 10 s, 20 s…) with what went wrong in a few words.
''' </summary>
Public Class ChatHarnessTests

    Private Const ReadTool As String = "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""read""}"
    Private Const RunTool As String = "{""name"":""run_command"",""description"":""Run a command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""change""}"

    <Fact>
    Public Sub RetriesStartSoonAndGrow()
        Assert.Equal(5, ChatService.RetryDelay(1, 120))
        Assert.Equal(10, ChatService.RetryDelay(2, 120))
        Assert.Equal(20, ChatService.RetryDelay(3, 120))
        Assert.Equal(120, ChatService.RetryDelay(9, 120))
        Assert.Equal(1, ChatService.RetryDelay(1, 1))
        Assert.Equal("The model is busy right now (rate limited).", ChatService.RetryReasonText("sev/x: 529 overloaded"))
        Assert.Equal("The model is not responding.", ChatService.RetryReasonText("something odd"))
    End Sub

    <Fact>
    Public Async Function ManualModeAsksBeforeChangesButNotBeforeReading() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("read_file", "{""path"":""src/app.js""}")
                                        Case 2 : Return ChatFeatureTests.ToolCallStream("run_command", "{""command"":""npm test""}")
                                        Case Else : Return ChatFeatureTests.TextStream("All good.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim asked As New List(Of String)
                Dim statusLines As New List(Of String)
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "check it", .model = "fast/a", .settings = New With {.permissionMode = "manual"},
                                                               .clientTools = {JsonNode.Parse(ReadTool), JsonNode.Parse(RunTool)}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                    Async Function(evt, data)
                        Select Case evt
                            Case "approval"
                                asked.Add(data.GetProperty("name").GetString())
                                ' The wait is said at once (not only 15 s later), and answered here.
                                Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals",
                                                            New With {.id = data.GetProperty("id").GetString(), .choice = "once"}, admin.Csrf)
                            Case "status"
                                statusLines.Add(data.GetProperty("text").GetString())
                            Case "client_tool"
                                Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                            New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "ok"}, admin.Csrf)
                        End Select
                    End Function)
                Assert.Equal({"run_command"}, asked.ToArray())
                Assert.Contains("Waiting for your approval…", statusLines)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' One message with read_file on "the user's computer": the model reads file1, file2… while it gets tools and wantsReads()
    ''' says so, then answers "Done."; result(n) is what the n-th read returns. Gives the reads, the stop reason, the status lines
    ''' and the requests the model got.
    ''' </summary>
    Private Shared Async Function RunReadsAsync(configure As Action(Of HostOptions), wantsReads As Func(Of Integer, Boolean), result As Func(Of Integer, String)) As Task(Of (Reads As Integer, StopReason As String, Statuses As List(Of String), Requests As List(Of JsonNode)))
        Using up As New FakeUpstream()
            Dim asked = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If req("tools") IsNot Nothing AndAlso wantsReads(asked) Then
                                        Return ChatFeatureTests.ToolCallStream("read_file", "{""path"":""file" & Interlocked.Increment(asked) & ".txt""}")
                                    End If
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, configure)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim reads = 0
                Dim stopReason As String = Nothing
                Dim statuses As New List(Of String)
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "read them all", .model = "fast/a", .settings = New With {.permissionMode = "auto"},
                                                               .clientTools = {JsonNode.Parse(ReadTool)}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                    Async Function(evt, data)
                        Select Case evt
                            Case "client_tool"
                                reads += 1
                                Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                            New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = result(reads)}, admin.Csrf)
                            Case "status"
                                statuses.Add(data.GetProperty("text").GetString())
                            Case "done"
                                stopReason = data.GetProperty("stopReason").GetString()
                        End Select
                    End Function)
                Return (reads, stopReason, statuses, up.Requests.Where(Function(r) ChatFeatureTests.Streamed(r)).ToList())
            End Using
        End Using
    End Function

    ''' <summary>A stretch of 3 rounds is a progress check, not a stop: ten reads that each bring something new all happen.</summary>
    <Fact>
    Public Async Function AnAnswerStillGettingThingsDoneGoesPastItsStretch() As Task
        Dim r = Await RunReadsAsync(Sub(o)
                                        o.Chat.MaxToolRounds = 3
                                        o.Chat.ClientToolRounds = 3
                                    End Sub, Function(n) n < 10, Function(n) "content of file " & n)
        Assert.Equal(10, r.Reads)
        Assert.Equal("end", r.StopReason)
        Assert.Contains(r.Statuses, Function(s) s.StartsWith("Progress check after", StringComparison.Ordinal))
        Assert.DoesNotContain(r.Requests, Function(q) q.ToJsonString().Contains("no more tool calls are possible"))
    End Function

    ''' <summary>A whole stretch with nothing new (every file reads the same) ends the tools: the model is told why and answers.</summary>
    <Fact>
    Public Async Function AStretchWithNothingNewEndsTheTools() As Task
        Dim r = Await RunReadsAsync(Sub(o)
                                        o.Chat.MaxToolRounds = 3
                                        o.Chat.ClientToolRounds = 3
                                    End Sub, Function(n) True, Function(n) "same")
        ' The first stretch brought something new (its first "same"), the second nothing: 6 reads, then an answer without tools.
        Assert.Equal(6, r.Reads)
        Assert.Equal("max_rounds", r.StopReason)
        Assert.Contains(r.Statuses, Function(s) s.StartsWith("No progress in the last 3 steps", StringComparison.Ordinal))
        Assert.Contains("brought nothing new", r.Requests.Last().ToJsonString())
        Assert.Null(r.Requests.Last()("tools"))
    End Function

    ''' <summary>Chat.MaxTotalToolRounds is a fixed total, progress or not.</summary>
    <Fact>
    Public Async Function MaxTotalToolRoundsIsAFixedTotal() As Task
        Dim r = Await RunReadsAsync(Sub(o)
                                        o.Chat.MaxToolRounds = 3
                                        o.Chat.ClientToolRounds = 3
                                        o.Chat.MaxTotalToolRounds = 5
                                    End Sub, Function(n) True, Function(n) "content of file " & n)
        Assert.Equal(5, r.Reads)
        Assert.Equal("max_rounds", r.StopReason)
        Assert.Contains("reached its limit of 5 tool rounds", r.Requests.Last().ToJsonString())
    End Function

End Class
