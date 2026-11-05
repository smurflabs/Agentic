Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Mcp
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Agent task failures found in the production data: subagents ending with nothing but "turn limit reached", research done but
''' never saved before the tool budget ran out, "Let me check…" accepted as a task's result, "let me try…" loops of failing
''' calls, truncated tool arguments poisoning every retry, a stuck loop the watchdog could not restart, and the noise of a
''' minute-by-minute health check.
''' </summary>
Public Class AgentReliabilityTests

    Private Shared Function LastUser(r As ChatRequest) As String
        Return If(r.Messages.LastOrDefault(Function(m) m.Role = "user")?.Content, "")
    End Function

    <Fact>
    Public Async Function ATotalTurnCapEndsWithAReportOfTheWorkSoFar() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If LastUser(r).Contains("reached its turn limit") Then
                              Return FakeAIProvider.Complete("Found the port in config.json (8022); the restart step is not done yet.", success:=False)
                          End If
                          n += 1
                          Return FakeAIProvider.Tool("recall", "{""query"":""step " & n & """}")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o)
                                                o.Routing.Escalation.OnTurnLimit = False
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 5, .MaxTotalTurns = 5, .MaxRetries = 0, .MaxToolCalls = 40}
                                            End Sub, p)
            Dim id = Await h.SubmitAsync("A", "Find the port")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Failed)
            Dim result = t.GetResult()
            Assert.False(result.Success)
            Assert.Contains("Turn limit (5 turns in all", result.Summary)
            Assert.Contains("Report of the work so far", result.Summary)
            Assert.Contains("config.json (8022)", result.Summary)
            ' It was warned before the end, and the wrap-up offered nothing but complete_task.
            Dim requests = p.Requests.ToArray().Where(Function(r) FakeAIProvider.AgentOf(r) = "A").ToList()
            Assert.Contains(requests, Function(r) r.Messages.Any(Function(m) m.Role = "user" AndAlso If(m.Content, "").Contains("Only 2 turns are left")))
            Dim wrap = requests.Last()
            Assert.Contains("reached its turn limit", LastUser(wrap))
            Assert.True(wrap.Tools Is Nothing OrElse wrap.Tools.All(Function(d) d.Name = "complete_task"))
        End Using
    End Function

    <Fact>
    Public Async Function AReplyThatOnlyAnnouncesItsNextStepIsSentBack() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If r.Messages.Any(Function(m) If(m.Content, "").Contains("[host] Your reply announced a next step")) Then
                              Return FakeAIProvider.Complete("Checked the logs: all clear.")
                          End If
                          Return FakeAIProvider.Text("Let me check the logs.")
                      End Function
        Using h = Await TestHost.StartAsync(Nothing, p)
            Dim id = Await h.SubmitAsync("A", "Check the logs")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Equal("Checked the logs: all clear.", t.GetResult().Summary)
            ' Every finished reasoning task keeps its conversation, so the failed attempt can be read afterwards.
            Dim kept = Await h.H.Tasks.GetTranscriptAsync(id)
            Assert.NotNull(kept)
            Assert.Contains("Let me check the logs.", kept)
        End Using
    End Function

    <Theory>
    <InlineData("Let me check the logs.", True)>
    <InlineData("I found the cause. Next, I'll restart the service:", True)>
    <InlineData("Okay, now I'll run the tests.", True)>
    <InlineData("The service is healthy: HTTP 200 in 120 ms, no errors in the last hour.", False)>
    <InlineData("Done. The port is 8022.", False)>
    <InlineData("", False)>
    Public Sub AnnouncedNextStepsAreRecognised(reply As String, expected As Boolean)
        Assert.Equal(expected, ReasoningTaskHandler.AnnouncesNextStep(reply))
    End Sub

    <Fact>
    Public Sub ALongResultThatEndsWithAPlanIsStillAResult()
        Dim report = String.Concat(Enumerable.Repeat("The endpoint answered with HTTP 200 and valid JSON on every check. ", 12)) & "I'll keep monitoring it."
        Assert.False(ReasoningTaskHandler.AnnouncesNextStep(report))
    End Sub

    <Fact>
    Public Async Function ManyFailingCallsInARowLeaveTheAgentOnlyCompleteTask() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If r.Tools IsNot Nothing AndAlso r.Tools.Count > 0 AndAlso r.Tools.All(Function(d) d.Name = "complete_task") Then
                              Return FakeAIProvider.Complete("Could not delete the memories: none of the ids exist.", success:=False)
                          End If
                          n += 1
                          ' A different (wrong) id every time, so the same-arguments guard never fires.
                          Return FakeAIProvider.Tool("forget", "{""memory_id"":""mem_missing_" & n & """}")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 20, .MaxToolCalls = 40, .MaxRetries = 0}, p)
            Dim id = Await h.SubmitAsync("A", "Clean up memories")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.False(t.GetResult().Success)
            Dim requests = p.Requests.ToArray().Where(Function(r) FakeAIProvider.AgentOf(r) = "A").ToList()
            Assert.Contains(requests, Function(r) r.Messages.Any(Function(m) m.Role = "tool" AndAlso If(m.Content, "").Contains("failed in a row. Stop trying small variations")))
            Assert.Contains(requests, Function(r) r.Tools IsNot Nothing AndAlso r.Tools.Count = 1 AndAlso r.Tools(0).Name = "complete_task")
            Assert.True(n <= 10, $"the agent should be stopped after 9 failures in a row; it made {n} calls")
        End Using
    End Function

    <Fact>
    Public Async Function ARestartReplacesALoopThatIsStuckInACall() As Task
        Using gate As New ManualResetEventSlim(False)
            Dim blocked = 0
            Dim p As New FakeAIProvider()
            p.Responder = Function(r)
                              If FakeAIProvider.AgentOf(r) = "A" AndAlso Interlocked.Increment(blocked) = 1 Then
                                  ' A call that ignores cancellation (it holds the loop until released).
                                  gate.Wait(TimeSpan.FromSeconds(90))
                              End If
                              Return FakeAIProvider.Complete("ok")
                          End Function
            Using h = Await TestHost.StartAsync(Nothing, p)
                Try
                    Dim stuckTask = Await h.SubmitAsync("A", "Stuck")
                    Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(Volatile.Read(blocked) >= 1), 20))
                    ' The stop times out (the loop never notices), then the start must still give the agent a working loop.
                    Dim restarted = Await h.H.Agents.RestartAgentAsync("A")
                    Assert.True(restarted.Success, restarted.Message)
                    Dim quick = Await h.SubmitAsync("A", "Quick", handler:="noop")
                    Await h.WaitForTaskStatusAsync("A", quick, AgentTaskStatus.Completed, 30)
                    Assert.True(h.H.Agents.IsRunning("A"))
                Finally
                    gate.Set()
                End Try
            End Using
        End Using
    End Function

    <Fact>
    Public Sub RoutineEventsAreNotKeptInTheActivityHistory()
        Dim stateEvent = Function(fromState As String, toState As String) New AgentEvent With {
            .EventType = AgentEventType.AgentStateChanged, .Level = EventLevel.Info, .Data = New Dictionary(Of String, Object) From {{"from", fromState}, {"to", toState}}}
        Assert.True(ActivityPersister.IsRoutine(stateEvent("Idle", "Running")))
        Assert.True(ActivityPersister.IsRoutine(stateEvent("Running", "Waiting")))
        Assert.False(ActivityPersister.IsRoutine(stateEvent("Running", "Paused")))
        Assert.False(ActivityPersister.IsRoutine(stateEvent("Idle", "Dead")))
        Dim polling = New AgentEvent With {.EventType = AgentEventType.TaskCompleted, .Level = EventLevel.Info, .Data = New Dictionary(Of String, Object) From {{"kind", "Polling"}}}
        Assert.True(ActivityPersister.IsRoutine(polling))
        polling.Level = EventLevel.Warning   ' a check that failed is kept
        Assert.False(ActivityPersister.IsRoutine(polling))
        Dim work = New AgentEvent With {.EventType = AgentEventType.TaskCompleted, .Level = EventLevel.Info, .Data = New Dictionary(Of String, Object) From {{"kind", "OneTime"}}}
        Assert.False(ActivityPersister.IsRoutine(work))
    End Sub

    <Fact>
    Public Sub McpDefaultArgumentsFillOnlyWhatTheModelLeftOut()
        Dim opt As New McpServerOptions With {.Id = "code-exec"}
        opt.DefaultArguments("run_command") = New Dictionary(Of String, String) From {{"wait", "60"}, {"shell", "bash"}}
        opt.DefaultArguments("*") = New Dictionary(Of String, String) From {{"verbose", "true"}}
        Dim tool As New McpToolInfo With {.Name = "run_command", .InputSchemaJson = "{""type"":""object"",""properties"":{""command"":{""type"":""string""},""wait"":{""type"":""number""},""verbose"":{""type"":""boolean""}}}"}
        Dim parse = Function(s As String) JsonDocument.Parse(s).RootElement.Clone()
        ' Missing: added with their types (number, boolean); "shell" is not in the tool's schema, so it is left out.
        Dim r = McpServerManager.WithDefaultArguments(opt, tool, "run_command", parse("{""command"":""ls""}"))
        Assert.Equal(60, r.GetProperty("wait").GetInt32())
        Assert.True(r.GetProperty("verbose").GetBoolean())
        Assert.False(r.TryGetProperty("shell", Nothing))
        ' Given by the model (even 0): kept. Null counts as left out.
        Assert.Equal(5, McpServerManager.WithDefaultArguments(opt, tool, "run_command", parse("{""command"":""ls"",""wait"":5}")).GetProperty("wait").GetInt32())
        Assert.Equal(60, McpServerManager.WithDefaultArguments(opt, tool, "run_command", parse("{""command"":""ls"",""wait"":null}")).GetProperty("wait").GetInt32())
        ' Other tools get only the "*" defaults.
        Dim other = McpServerManager.WithDefaultArguments(opt, New McpToolInfo With {.Name = "list_files"}, "list_files", parse("{}"))
        Assert.False(other.TryGetProperty("wait", Nothing))
        Assert.True(other.GetProperty("verbose").GetBoolean())
    End Sub

    ''' <summary>Production 2026-11-01: `git branch -a` opened a pager in the sandbox's terminal and never finished.</summary>
    <Fact>
    Public Sub McpDefaultEnvIsMergedIntoTheCommandsEnvironment()
        Dim opt As New McpServerOptions With {.Id = "code-exec"}
        opt.DefaultEnv("run_command") = New Dictionary(Of String, String) From {{"GIT_PAGER", "cat"}, {"GIT_TERMINAL_PROMPT", "0"}}
        Dim tool As New McpToolInfo With {.Name = "run_command", .InputSchemaJson = "{""type"":""object"",""properties"":{""command"":{""type"":""string""},""env"":{""type"":""object""}}}"}
        Dim parse = Function(s As String) JsonDocument.Parse(s).RootElement.Clone()
        ' No env from the model: the defaults are its env.
        Dim r = McpServerManager.WithDefaultArguments(opt, tool, "run_command", parse("{""command"":""git branch -a""}"))
        Assert.Equal("cat", r.GetProperty("env").GetProperty("GIT_PAGER").GetString())
        Assert.Equal("0", r.GetProperty("env").GetProperty("GIT_TERMINAL_PROMPT").GetString())
        ' The model's own variables stay, and win over a default of the same name.
        Dim own = McpServerManager.WithDefaultArguments(opt, tool, "run_command", parse("{""command"":""git log"",""env"":{""GIT_PAGER"":""less"",""FOO"":""1""}}"))
        Assert.Equal("less", own.GetProperty("env").GetProperty("GIT_PAGER").GetString())
        Assert.Equal("1", own.GetProperty("env").GetProperty("FOO").GetString())
        Assert.Equal("0", own.GetProperty("env").GetProperty("GIT_TERMINAL_PROMPT").GetString())
        ' A null env counts as none.
        Assert.Equal("cat", McpServerManager.WithDefaultArguments(opt, tool, "run_command", parse("{""command"":""x"",""env"":null}")).GetProperty("env").GetProperty("GIT_PAGER").GetString())
        ' A tool whose schema has no env gets none.
        Dim noEnv As New McpToolInfo With {.Name = "run_command", .InputSchemaJson = "{""type"":""object"",""properties"":{""command"":{""type"":""string""}}}"}
        Assert.False(McpServerManager.WithDefaultArguments(opt, noEnv, "run_command", parse("{""command"":""x""}")).TryGetProperty("env", Nothing))
    End Sub

    ''' <summary>The tool-call audit of 2026-11-02: the errors models kept repeating now say which call works.</summary>
    <Fact>
    Public Sub CommonToolErrorsComeWithTheCallThatWorks()
        Assert.Contains("get_dir_contents", McpErrorHints.HintFor("get_file_contents", "get file err: object does not exist [id: , rel_path: docs]"))
        Assert.Contains("sha", McpErrorHints.HintFor("create_or_update_file", "change files err: repository file already exists [path: tests/test_service.py]"))
        Assert.Contains("""."" for the repository's top", McpErrorHints.HintFor("get_dir_contents", "path is required"))
        Assert.Contains("glob_search", McpErrorHints.HintFor("read_file", "Error calling tool 'read_file': HTTP error 404: Not Found - {'detail': 'File not found'}"))
        Assert.Contains("cwd", McpErrorHints.HintFor("run_command", "Error calling tool 'run_command': [Errno 2] No such file or directory: '/workspace'"))
        Assert.Contains("Nothing to stop", McpErrorHints.HintFor("kill_process", "HTTP error 404: Not Found - {'detail': 'Process not found'}"))
        Assert.Null(McpErrorHints.HintFor("get_file_contents", "start_line 120 is past the end of the file (98 lines)"))
        Assert.Null(McpErrorHints.HintFor("write_file", "anything"))
    End Sub

    <Fact>
    Public Sub TheWatchdogSlowsDownWhileASandboxStaysDown()
        Dim hc As New McpHealthCheckOptions With {.DownIntervalSeconds = 30, .DownMaxIntervalSeconds = 600}
        Assert.Equal(30, McpServerManager.DownCheckSeconds(hc, 0))
        Assert.Equal(30, McpServerManager.DownCheckSeconds(hc, 1))
        Assert.Equal(60, McpServerManager.DownCheckSeconds(hc, 2))
        Assert.Equal(480, McpServerManager.DownCheckSeconds(hc, 5))
        Assert.Equal(600, McpServerManager.DownCheckSeconds(hc, 6))
        Assert.Equal(600, McpServerManager.DownCheckSeconds(hc, 500))
        ' A check that started a process which is still running is told apart from one that finished.
        Assert.Equal("20261102-0816-ab", McpServerManager.RunningProcessId("{""id"":""20261102-0816-ab"",""status"":""running"",""output"":[]}"))
        Assert.Null(McpServerManager.RunningProcessId("{""id"":""p1"",""status"":""done"",""output"":[""ok""]}"))
        Assert.Null(McpServerManager.RunningProcessId("Error calling tool 'run_command': [Errno 24] Too many open files"))
    End Sub

    <Fact>
    Public Sub CutOffToolArgumentsAreSentAsValidJson()
        Dim req As New ChatRequest With {.ModelId = "m"}
        req.Messages.Add(ChatMessage.ForUser("write it"))
        req.Messages.Add(ChatMessage.ForAssistant("", New List(Of ToolCallRequest) From {
            New ToolCallRequest With {.Id = "call_1", .Name = "write_file", .ArgumentsJson = "{""path"":""a.txt"",""content"":""abc"}}))
        req.Messages.Add(ChatMessage.ForTool("call_1", "write_file", "ERROR: cut off"))
        Dim body = OpenAICompatibleProvider.BuildBody(req)
        Dim args = body("messages")(1)("tool_calls")(0)("function")("arguments").ToString()
        Dim parsed = JsonDocument.Parse(args).RootElement
        Assert.Equal("a.txt", parsed.GetProperty("path").GetString())
    End Sub

    <Theory>
    <InlineData("{""error"":{""message"":""litellm.APIError: APIError: OpenAIException - 预扣费额度失败, 用户剩余额度: ＄0.005136, 需要预扣费额度: ＄0.017364""}}")>
    <InlineData("{""error"":{""message"":""You exceeded your current quota, please check your plan and billing details."",""code"":""insufficient_quota""}}")>
    Public Sub AProviderOutOfCreditMakesThatModelUnavailable(body As String)
        Assert.Equal(AiErrorKind.ModelUnavailable, AiErrorClassifier.FromHttp(400, body).Kind)
    End Sub

End Class
