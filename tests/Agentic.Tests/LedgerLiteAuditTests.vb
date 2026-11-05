Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Immutable
Imports System.Text
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Mcp
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' What the second LedgerLite audit (2026-10-31) found: steps never saw the request (only the coordinator's summary), the
''' final answer cut the request at 4000 characters, a revision lost the earlier revision's work, an agent that reported it
''' could not finish counted as done, one agent's second step never ran, base64 file contents reached the models, an agent
''' was locked out for a day after a restart, and nobody was told when steps waited on a usage limit.
''' </summary>
Public Class LedgerLiteAuditTests

    Private Shared Function KindOf(r As ChatRequest) As String
        Dim sys = If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
        If sys.Contains("coordinator of a team") Then Return "plan"
        If sys.Contains("final answer to an administrator's request") Then Return "combine"
        Return "agent"
    End Function

    Private Shared Function AllText(r As ChatRequest) As String
        Return String.Join(vbLf, r.Messages.Select(Function(m) If(m.Content, "")))
    End Function

    <Fact>
    Public Async Function StepsGetTheWholeRequestAndARevisionKeepsEarlierWork() As Task
        ' The data a step needs sits past 4000 characters of the request (LedgerLite's E1-E17 scenario).
        Dim request = "Build and verify the ledger." & vbLf & New String("."c, 5000) & vbLf & "E8 2026-01-20 key k8: Dr 5100 300000, Cr 1000 300000"
        Dim reviews = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Select Case KindOf(r)
                              Case "plan"
                                  If AllText(r).Contains("rejected the result") Then
                                      Return FakeAIProvider.Text("{""understanding"": ""Finish the audit."", ""approach"": ""single"", ""steps"": [" &
                                                                 "{""id"": ""s1"", ""agent"": ""B"", ""title"": ""Audit again"", ""instructions"": ""Audit the code."", ""dependsOn"": []}]}")
                                  End If
                                  Return FakeAIProvider.Text("{""understanding"": ""Research and audit."", ""approach"": ""parallel"", ""steps"": [" &
                                                             "{""id"": ""s1"", ""agent"": ""A"", ""title"": ""Research"", ""instructions"": ""Research it."", ""dependsOn"": []}," &
                                                             "{""id"": ""s2"", ""agent"": ""B"", ""title"": ""Audit"", ""instructions"": ""Audit the code."", ""dependsOn"": []}]}")
                              Case "combine" : Return FakeAIProvider.Text("FINAL ANSWER")
                          End Select
                          Select Case FakeAIProvider.AgentOf(r)
                              Case "C"
                                  If Interlocked.Increment(reviews) = 1 Then
                                      Return FakeAIProvider.Tool("complete_task", JsonUtil.ToJson(New With {.success = True, .summary = "Audit missing.",
                                                                                                             .data = New With {.verdict = "revise", .score = 3, .feedback = "Do the audit."}}))
                                  End If
                                  Return FakeAIProvider.Tool("complete_task", JsonUtil.ToJson(New With {.success = True, .summary = "Fine.",
                                                                                                         .data = New With {.verdict = "approve", .score = 8, .feedback = "Good."}}))
                              Case "B" : Return FakeAIProvider.Complete("Blocked: cannot read the files.", success:=False)
                              Case Else : Return FakeAIProvider.Complete("RESEARCH-DONE with five sources")
                          End Select
                      End Function
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim g = Await host.H.GlobalTasks.CreateAsync("Ledger", request, New GlobalTaskOptions With {.ReviewerAgentId = "C", .MaxRevisions = 1}, "admin")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          g = Await host.H.GlobalTasks.GetAsync(g.Id)
                                                          Return g.Status = "Completed" OrElse g.Status = "Failed"
                                                      End Function, 90), $"global task did not finish; it is {g?.Status} ({g?.Error})")
            Assert.True(g.Status = "Completed", $"{g.Status}: {g.Error} (revision {g.Revision}, reviews {reviews})")
            Assert.Equal(1, g.Revision)
            Assert.Equal(2, reviews)
            ' Revision 0's research is kept, and so is what the blocked audit reported.
            Assert.Contains("RESEARCH-DONE", g.PriorResults)
            Assert.Contains("Blocked: cannot read the files.", g.PriorResults)
            ' An agent that said it could not finish did not do its step.
            Dim audit = Assert.Single(g.Steps)
            Assert.Equal("Failed", audit.Status)
            Assert.Contains("reported it could not finish", audit.Error)
            ' Revision 1 was planned knowing what was done, and its step got the whole request and that work.
            Assert.Contains(p.Requests, Function(r) KindOf(r) = "plan" AndAlso AllText(r).Contains("RESEARCH-DONE") AndAlso AllText(r).Contains("do not plan this work again"))
            Dim auditTask = Await host.H.Tasks.GetAsync(audit.TaskId)
            Assert.Contains("E8 2026-01-20 key k8", auditTask.Description)
            Assert.Contains("RESEARCH-DONE", auditTask.Description)
            ' The final answer was written from the whole request and the earlier work (nothing completed in revision 1).
            Assert.Contains(p.Requests, Function(r) KindOf(r) = "combine" AndAlso AllText(r).Contains("E8 2026-01-20 key k8") AndAlso AllText(r).Contains("RESEARCH-DONE"))
            Assert.Equal("FINAL ANSWER", g.FinalResult)
        End Using
    End Function

    <Fact>
    Public Sub ParallelStepsOfOneAgentAreSentBackWhileOthersAreIdle()
        Dim stepOf = Function(id As String, agent As String, deps As String()) New GlobalTaskStep With {.StepId = id, .AgentId = agent, .Title = id, .Instructions = "x", .DependsOn = deps.ToList()}
        Dim plan As New GlobalTaskService.PlanResult()
        plan.Steps.AddRange({stepOf("s1", "A", {}), stepOf("s2", "A", {}), stepOf("s3", "B", {})})
        Dim problem = GlobalTaskService.SameAgentProblem(plan, available:=3, maxAgents:=12)
        Assert.NotNull(problem)
        Assert.Contains("s1 and s2", problem)
        ' Every listed agent already works, or the limit allows no more: left alone.
        Assert.Null(GlobalTaskService.SameAgentProblem(plan, available:=2, maxAgents:=12))
        Assert.Null(GlobalTaskService.SameAgentProblem(plan, available:=3, maxAgents:=2))
        ' One step builds on the other: they run in order anyway.
        Dim chained As New GlobalTaskService.PlanResult()
        chained.Steps.AddRange({stepOf("s1", "A", {}), stepOf("s2", "B", {}), stepOf("s3", "A", {"s2", "s1"})})
        Assert.Null(GlobalTaskService.SameAgentProblem(chained, available:=3, maxAgents:=12))
    End Sub

    <Fact>
    Public Sub Base64FileContentsReachTheModelAsText()
        Dim b64 = Function(s As String) Convert.ToBase64String(Encoding.UTF8.GetBytes(s))
        Dim decoded = McpFileContent.DecodeText($"{{""name"":""pyproject.toml"",""sha"":""90e2e6"",""content"":""{b64("[project]" & vbLf & "name = ""ledgerlite""" & vbLf)}"",""encoding"":""base64""}}")
        Assert.Contains("name = \""ledgerlite\""", decoded)
        Assert.Contains("decoded from base64", decoded)
        Assert.Contains("90e2e6", decoded)
        ' A list of files, each decoded.
        Dim list = McpFileContent.DecodeText($"[{{""path"":""a.py"",""content"":""{b64("print(1)")}"",""encoding"":""base64""}},{{""path"":""b.py"",""content"":""{b64("print(2)")}"",""encoding"":""base64""}}]")
        Assert.Contains("print(1)", list)
        Assert.Contains("print(2)", list)
        ' A binary file stays as it is, and so does anything that is not such JSON.
        Dim binary = $"{{""content"":""{Convert.ToBase64String(New Byte() {0, 1, 2, 255})}"",""encoding"":""base64""}}"
        Assert.Equal(binary, McpFileContent.DecodeText(binary))
        Assert.Equal("the word base64 in plain text", McpFileContent.DecodeText("the word base64 in plain text"))
        Assert.Equal("{""encoding"":""base64""}", McpFileContent.DecodeText("{""encoding"":""base64""}"))
    End Sub

    <Fact>
    Public Async Function ATaskThatGivesWayLetsItsAgentsOtherTaskRun() As Task
        ' No agents running, so nothing claims the tasks out from under the assertions.
        Using host = Await TestHost.StartAsync(Sub(o) o.Agents.Clear())
            Dim tasks = host.H.Tasks
            Dim now = DateTime.UtcNow
            For Each id In {"first", "second"}
                Assert.True(Await tasks.InsertAsync(New AgentTask With {.Id = id, .AgentId = "A", .Name = id, .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = "cor_" & id}))
                now = now.AddSeconds(1)
            Next
            Dim claim = Function() tasks.ClaimNextAsync("A", DateTime.UtcNow, "test", DateTime.UtcNow.AddHours(1))
            Dim giveWay = Async Function(t As AgentTask) As Task
                              t.Status = AgentTaskStatus.Pending : t.LeaseOwner = Nothing : t.NextRunUtc = DateTime.UtcNow
                              Await tasks.UpdateAsync(t)
                              Await tasks.ReRankAsync(t.Id)
                          End Function
            Dim t1 = Await claim()
            Assert.Equal("first", t1.Id)
            Await giveWay(t1)
            Dim t2 = Await claim()
            Assert.Equal("second", t2.Id)       ' its turn now, not "first" again
            Await giveWay(t2)
            Assert.Equal("first", (Await claim()).Id)
        End Using
    End Function

    <Fact>
    Public Sub AfterARestartUsageFreesUpWhenItTurnsADayOld()
        Dim clock As New FakeClock()
        Dim limiter As New UsageLimiter(clock)
        Dim profile As New EffectiveProfile With {.Id = "A", .MaxTokensPerDay = 1000}
        ' Most of the day's use was 23 hours ago, a little one hour ago.
        limiter.Seed("A", New List(Of (DateTime, Long, Long)) From {(clock.Now.AddHours(-23), 5L, 900L), (clock.Now.AddHours(-1), 1L, 200L)})
        Assert.Contains("daily token limit", limiter.Check(profile))
        clock.Advance(TimeSpan.FromMinutes(90))
        Assert.Null(limiter.Check(profile))
    End Sub

    <Fact>
    Public Sub StepsWaitingOnAUsageLimitAreRecognized()
        Dim waiting As New AgentTask With {.Status = AgentTaskStatus.Retrying, .ErrorMessage = "AI usage limit: daily token limit (20000000) reached"}
        Assert.NotNull(GlobalTaskService.BlockedReason(waiting))
        Assert.Null(GlobalTaskService.BlockedReason(New AgentTask With {.Status = AgentTaskStatus.Retrying, .ErrorMessage = "AI unavailable: timed out"}))
        Assert.Null(GlobalTaskService.BlockedReason(New AgentTask With {.Status = AgentTaskStatus.Running, .ErrorMessage = "AI usage limit: daily token limit (1) reached"}))
    End Sub

    ' ------------------------------------------------------------------ re-reading loops

    <Fact>
    Public Sub TheSameCommandOutputIsTheSameResultWhateverItsProcessId()
        Dim run = Function(id As String, output As String) $"{{""id"":""{id}"",""command"":""cat api.py | sed -n '100,260p'"",""status"":""done"",""exit_code"":0," &
                                                         $"""output"":[{{""type"":""output"",""data"":""{output}""}}],""log_path"":""/home/user/.local/state/open-terminal/logs/processes/{id}.jsonl""}}"
        Dim a = ChatService.ToolResultFingerprint(True, run("20261031-003110-a1b2c3", "def post_entry(...)"))
        Dim b = ChatService.ToolResultFingerprint(True, run("20261031-004417-9f8e7d", "def post_entry(...)"))
        Assert.Equal(a, b)
        Assert.NotEqual(a, ChatService.ToolResultFingerprint(True, run("20261031-004417-9f8e7d", "def reverse_entry(...)")))
        Assert.NotEqual(a, ChatService.ToolResultFingerprint(False, run("20261031-003110-a1b2c3", "def post_entry(...)")))
    End Sub

    <Fact>
    Public Sub CompactionGoesWellBelowTheLimitSoTheStartStaysTheSame()
        ' On a 262k window the message limit is 131 (MaxContextMessages 60 raised for the window; audit 3).
        Dim transcript As New List(Of ChatMessage) From {ChatMessage.ForSystem("system"), ChatMessage.ForUser("the task")}
        For i = 1 To 70
            Dim id = "c" & i
            transcript.Add(ChatMessage.ForAssistant("", New List(Of ToolCallRequest) From {New ToolCallRequest With {.Id = id, .Name = "read_file", .ArgumentsJson = "{}"}}))
            transcript.Add(ChatMessage.ForTool(id, "read_file", New String("x"c, 5000)))
        Next
        Assert.True(ContextCompactor.Compact(transcript, 262144, 60))
        Assert.True(transcript.Count <= 80, transcript.Count.ToString())                ' 60 % of 131, not just under 131
        Assert.Equal(5000, transcript.Last().Content.Length)                            ' the latest results stay whole
        ' The next turns add messages without compacting again: the start stays the same.
        Dim startBefore = String.Join("|", transcript.Take(6).Select(Function(m) m.Role & ":" & If(m.Content, "").Length))
        For i = 71 To 78
            transcript.Add(ChatMessage.ForAssistant("", New List(Of ToolCallRequest) From {New ToolCallRequest With {.Id = "c" & i, .Name = "read_file", .ArgumentsJson = "{}"}}))
            transcript.Add(ChatMessage.ForTool("c" & i, "read_file", "short"))
            Assert.False(ContextCompactor.Compact(transcript, 262144, 60))
        Next
        Assert.Equal(startBefore, String.Join("|", transcript.Take(6).Select(Function(m) m.Role & ":" & If(m.Content, "").Length)))
    End Sub

    <Fact>
    Public Sub AfterCompactionTheLoopGuardLetsAGoneResultBeReadAgain()
        Dim cp As New TaskCheckpoint()
        Dim seen As New Dictionary(Of String, (Count As Integer, LastHash As String, SameResults As Integer)) From {{"k", (3, "h", 2)}, {"other", (1, "h2", 0)}}
        ReasoningTaskHandler.ForgetCompactedResults(cp, seen)
        Assert.Equal(0, seen("k").SameResults)
        Assert.Equal(3, seen("k").Count)
        Assert.Equal(0, cp.SeenCalls("k").SameResults)
        ' The guard's memory survives a time slice (the checkpoint is saved and read back).
        cp.SeenResults.Add("abcdef0123456789")
        Dim back = TaskCheckpoint.Parse(cp.ToJson())
        Assert.Equal(3, back.SeenCalls("k").Count)
        Assert.Contains("abcdef0123456789", back.SeenResults)
        Assert.Equal(ReasoningTaskHandler.GuardKey("read_file", "{""path"":""a"",""n"":1}"), ReasoningTaskHandler.GuardKey("read_file", "{""n"":1,  ""path"":""a""}"))
    End Sub

    <Fact>
    Public Sub ARepeatedArgumentKeyKeepsItsLastValueInsteadOfCrashing()
        Dim dup = "{""path"": ""a.py"", ""content"": ""x &lt; y"", ""path"": ""b.py"", ""opts"": {""n"": 1, ""n"": 2}}"
        Dim fixedJson = JsonUtil.CollapseDuplicateKeys(dup)
        Dim el = JsonUtil.ParseElementOrNull(fixedJson).Value
        Assert.Equal("b.py", JsonUtil.GetString(el, "path"))
        Assert.Equal(2, el.GetProperty("opts").GetProperty("n").GetInt32())
        ' What crashed BackendDev's task (JsonObject reads a repeated key) now works on the collapsed arguments.
        Assert.Contains("x < y", CodeEntities.FixToolArguments(fixedJson))
        Assert.Equal("{""path"": ""a.py""}", JsonUtil.CollapseDuplicateKeys("{""path"": ""a.py""}"))   ' untouched when nothing repeats
        Assert.Equal("not json", JsonUtil.CollapseDuplicateKeys("not json"))
    End Sub

    <Fact>
    Public Sub ARejectedResultIsNotASuccess()
        Assert.True(ResultService.Rejected(New ReviewOutcome With {.Reviewed = True, .Approved = False}))
        Assert.False(ResultService.Rejected(New ReviewOutcome With {.Reviewed = True, .Approved = True}))
        Assert.False(ResultService.Rejected(ReviewOutcome.NotReviewed()))
        Assert.False(ResultService.Rejected(Nothing))
    End Sub

    <Fact>
    Public Sub AFileWrittenWithACutOffArgumentStillShowsItsPath()
        Dim cut = "{""path"": ""/tmp/ll_accept/runner.py"", ""content"": ""#!/usr/bin/env python3\n\""\""\""LedgerLite E1-E17 acceptance scenario runner.\n" & New String("x"c, 50)
        Assert.Equal("/tmp/ll_accept/runner.py", GlobalTaskService.ArgumentFromCutJson(cut, "path", "file_path"))
        Assert.Equal("a b/c.txt", GlobalTaskService.ArgumentFromCutJson("{""content"": ""zz"", ""file_path"": ""a b/c.txt"", ""more"": ""cut off he", "path", "file_path"))
        Assert.Null(GlobalTaskService.ArgumentFromCutJson("{""content"": ""zz", "path"))
    End Sub

End Class
