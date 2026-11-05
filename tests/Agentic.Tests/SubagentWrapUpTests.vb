Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' A chat subagent that runs out of steps is told ahead, writes its report at its last step (without tools) and can be continued
''' with its whole context (resume); one whose model fails midway still reports what it did. A tool on the user's computer has no
''' 15-minute limit any more: it is waited for while that computer stays connected, up to Chat.ClientToolTimeoutSeconds.
''' </summary>
Public Class SubagentWrapUpTests

    Private Const BashTool As String = "{""name"":""Bash"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"
    Private Const BashAsText As String = "<tool_call>{""name"": ""Bash"", ""arguments"": {""command"": ""echo 42""}}</tool_call>"

    ''' <summary>Asks in Auto mode with a Bash tool on "the user's computer"; onClientTool answers (or not) each call.</summary>
    Private Shared Async Function AskWithBashAsync(admin As (Client As HttpClient, Csrf As String), chatId As String, onClientTool As Func(Of Text.Json.JsonElement, Task)) As Task
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = "count them", .model = "fast/a", .settings = New With {.permissionMode = "auto"}, .clientTools = {JsonNode.Parse(BashTool)}})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
            Async Function(evt, data)
                If evt = "client_tool" Then Await onClientTool(data)
            End Function)
    End Function

    Private Shared Function Answer42(admin As (Client As HttpClient, Csrf As String), chatId As String) As Func(Of Text.Json.JsonElement, Task)
        Return Async Function(data)
                   Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/tool-results",
                                               New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "42"}, admin.Csrf)
               End Function
    End Function

    Private Shared Function Said(req As JsonNode, text As String) As Boolean
        Return req("messages").AsArray().Any(Function(m) If(m("content")?.ToString(), "").Contains(text, StringComparison.Ordinal))
    End Function

    <Fact>
    Public Async Function ASubagentOutOfStepsReportsAtItsLastStepAndCanBeContinued() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1
                                            Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Count files"",""prompt"":""Count the files and report.""}")
                                        Case 2
                                            ' The report names the subagent's id: continue it instead of starting over.
                                            Dim report = req("messages").AsArray().Last(Function(m) m("role")?.ToString() = "tool")("content").ToString()
                                            Dim id = Regex.Match(report, "\(id (sub_[0-9a-f]+)\)").Groups(1).Value
                                            Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Finish counting"",""prompt"":""Finish counting."",""resume"":""" & id & """}")
                                        Case Else
                                            Return ChatFeatureTests.TextStream("All done.", "stop")
                                    End Select
                                End Function
            ' The subagent calls Bash while it has tools; its last step has none, and that answer is its report.
            up.TextOverride = Function(req)
                                  If req("tools") IsNot Nothing Then Return BashAsText
                                  Return If(Said(req, "you are continued where you stopped"), "Resumed report: 42 files in all.", "Partial report: 21 files so far.")
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.SubagentMaxRounds = 5)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskWithBashAsync(admin, id, Answer42(admin, id))
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim tasks = conv("messages").AsArray().Last()("toolCalls").AsArray().Where(Function(c) c("name").ToString() = "task").ToList()
                Assert.Equal(2, tasks.Count)
                ' Out of steps: its report still comes back (not a bare failure), with how to continue it.
                Dim first = tasks(0)("result").ToString()
                Assert.True(tasks(0)("ok").GetValue(Of Boolean)(), first)
                ' Its first step brought something new ("42"), so the check gave it a second stretch; the same "42" again and again
                ' is no progress, so it was told to finish: 10 steps in all.
                Assert.Contains("used all its 10 steps", first)
                Assert.Contains("Partial report: 21 files so far.", first)
                Assert.Contains("resume", first)
                ' Continued with its whole context: the second run finished the work.
                Assert.True(tasks(1)("ok").GetValue(Of Boolean)(), tasks(1)("result").ToString())
                Assert.Contains("Resumed report: 42 files in all.", tasks(1)("result").ToString())

                Dim subRequests = up.Requests.Where(Function(r) ChatFeatureTests.IsSubagent(r)).ToList()
                ' It hears how its progress is checked, a warning 3 steps ahead, and that tools are off at its last step.
                Assert.Contains("checks your progress every 5 steps", subRequests(0)("messages")(0)("content").ToString())
                Assert.Contains(subRequests, Function(r) Said(r, "3 more steps with tools"))
                Dim lastStep = subRequests.First(Function(r) r("tools") Is Nothing)
                Assert.True(Said(lastStep, "tools are now off"))
                ' The continued run starts from everything the first one saw, including its report.
                Dim resumed = subRequests.First(Function(r) Said(r, "you are continued where you stopped"))
                Assert.True(Said(resumed, "Partial report: 21 files so far."))
                Assert.True(Said(resumed, "Count the files and report."))
                ' Each run: 9 steps with tools, then the report.
                Assert.Equal(20, subRequests.Count)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ASubagentStillGettingThingsDoneGoesPastItsStretch() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Count files"",""prompt"":""Count the files.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            ' Twelve steps of work, each with a new result, then its report: far past its stretch of 5, without being cut short.
            ' (Counted from the request itself: the fake may ask for a text more than once per request.)
            up.TextOverride = Function(req)
                                  Dim done = req("messages").AsArray().Where(Function(m) m("role")?.ToString() = "tool").Count()
                                  If req("tools") IsNot Nothing AndAlso done < 12 Then Return BashAsText
                                  Return "Report: 12 folders counted."
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.SubagentMaxRounds = 5)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim n = 0
                Await AskWithBashAsync(admin, id, Async Function(data)
                                                      Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                                                  New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "folder " & Interlocked.Increment(n)}, admin.Csrf)
                                                  End Function)
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim task1 = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")
                Dim result = task1("result").ToString()
                Assert.True(task1("ok").GetValue(Of Boolean)(), result)
                Assert.Contains("Report: 12 folders counted.", result)
                Assert.DoesNotContain("used all", result)
                Assert.Equal(12, n)
                Dim subRequests = up.Requests.Where(Function(r) ChatFeatureTests.IsSubagent(r)).ToList()
                Assert.DoesNotContain(subRequests, Function(r) Said(r, "3 more steps with tools"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ASubagentWhoseModelFailsMidwayReportsWhatItDid() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Check the box"",""prompt"":""Run echo 42 and report.""}")
                                    Return ChatFeatureTests.TextStream("Continuing myself.", "stop")
                                End Function
            up.TextOverride = Function(req) BashAsText
            ' Its second step is refused (not an outage, so it is not retried).
            up.ErrorOverride = Function(req) If(ChatFeatureTests.IsSubagent(req) AndAlso req("messages").AsArray().Any(Function(m) m("role")?.ToString() = "tool"),
                                                "the prompt is too long for this model", Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskWithBashAsync(admin, id, Answer42(admin, id))
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim task1 = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")
                Dim result = task1("result").ToString()
                Assert.False(task1("ok").GetValue(Of Boolean)())
                Assert.Contains("stopped before writing its report: its model call failed", result)
                Assert.Contains("What it did", result)
                Assert.Contains("- Bash echo 42: 42", result)
                Assert.Contains("resume", result)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AToolOnTheUsersComputerIsWaitedForUpToTheLongestWait() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("Bash", "{""command"":""make all""}")
                                    Return ChatFeatureTests.TextStream("It is still building.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.ClientToolTimeoutSeconds = 2)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' The computer never answers (the build is still running there).
                Dim watch = Diagnostics.Stopwatch.StartNew()
                Await AskWithBashAsync(admin, id, Function(data) Task.CompletedTask)
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim bash = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "Bash")
                Assert.False(bash("ok").GetValue(Of Boolean)())
                Assert.Contains("still running on the user's computer after 2 s", bash("result").ToString())
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ASubagentThatWritesNothingAtItsLastStepIsAskedOnceMore() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Count files"",""prompt"":""Count the files.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            ' Its last step (no tools) gives nothing at first; asked once more, it writes the report.
            up.TextOverride = Function(req)
                                  If req("tools") IsNot Nothing Then Return BashAsText
                                  Return If(Said(req, "not only in your thinking"), "Report: 42 files.", "")
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.SubagentMaxRounds = 3)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskWithBashAsync(admin, id, Answer42(admin, id))
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim task1 = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")
                Assert.True(task1("ok").GetValue(Of Boolean)(), task1("result").ToString())
                Assert.Contains("Report: 42 files.", task1("result").ToString())
                Assert.Contains("used all its 3 steps", task1("result").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ASubagentThatOnlyThinksGivesBackItsLastThoughts() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Count files"",""prompt"":""Count the files.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            up.TextOverride = Function(req) BashAsText
            ' Without tools it answers only in its thinking, every time.
            up.RawNonStreamOverride = Function(req)
                                          If Not ChatFeatureTests.IsSubagent(req) OrElse req("tools") IsNot Nothing Then Return Nothing
                                          Return "{""choices"":[{""index"":0,""message"":{""role"":""assistant"",""content"":"""",""reasoning_content"":""The command printed 42, so there are 42 files.""},""finish_reason"":""stop""}]}"
                                      End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.SubagentMaxRounds = 3)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskWithBashAsync(admin, id, Answer42(admin, id))
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim result = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")("result").ToString()
                Assert.Contains("used all 3 steps and wrote nothing at its last step", result)
                Assert.Contains("Its last thoughts:" & vbLf & "The command printed 42, so there are 42 files.", result)
                Assert.Contains("- Bash echo 42: 42", result)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub WhatASubagentDidIsListedWithinItsLimit()
        Dim messages As New JsonArray(New JsonObject From {{"role", "system"}, {"content", "You are a subagent"}}, New JsonObject From {{"role", "user"}, {"content", "Count."}})
        For i = 1 To 60
            Dim callId = "c" & i
            messages.Add(New JsonObject From {{"role", "assistant"}, {"content", If(i = 1, "Looking around first.", "")},
                                              {"tool_calls", New JsonArray(New JsonObject From {{"id", callId}, {"type", "function"},
                                                  {"function", New JsonObject From {{"name", "read_file"}, {"arguments", "{""path"":""src/f" & i & ".vb""}"}}}})}})
            messages.Add(New JsonObject From {{"role", "tool"}, {"tool_call_id", callId}, {"content", "line one" & vbLf & "of file " & i}})
        Next
        Dim all = ChatService.SubagentSteps(messages, 2, 100000)
        Assert.StartsWith("- It wrote: Looking around first.", all)
        Assert.Contains("- read_file src/f1.vb: line one of file 1", all)
        Assert.Contains("- read_file src/f60.vb: line one of file 60", all)

        Dim cut = ChatService.SubagentSteps(messages, 2, 1500)
        Assert.True(cut.Length <= 1500, cut.Length.ToString())
        Assert.Contains("- read_file src/f1.vb", cut)
        Assert.Contains("- read_file src/f60.vb", cut)
        Assert.Contains("steps left out", cut)
        Assert.DoesNotContain("src/f30.vb", cut)
        ' Nothing done yet: nothing to list.
        Assert.Equal("", ChatService.SubagentSteps(messages, messages.Count))
    End Sub

    <Fact>
    Public Sub WaitsAreWrittenShortly()
        Assert.Equal("45 s", ChatService.ShortSpan(45))
        Assert.Equal("20 min", ChatService.ShortSpan(1200))
        Assert.Equal("90 min", ChatService.ShortSpan(5400))
        Assert.Equal("4 h", ChatService.ShortSpan(14400))
        Assert.Equal("2 h 30 min", ChatService.ShortSpan(9000))
    End Sub

End Class
