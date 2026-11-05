Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The third global-task audit (2026-11-02): agents re-read what compaction dropped, paused every 23 minutes with nothing else
''' queued, did not know their deadline, and the final step never got time; a planned report step duplicated the final step;
''' a revision's steps were lost; a relay's "exceed your balance" was retried; subagents failed on an hourly limit; the
''' sandbox watchdog's own checks flooded the log.
''' </summary>
Public Class GlobalTaskAudit3Tests

    Private Shared Function KindOf(r As ChatRequest) As String
        Dim sys = If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
        If sys.Contains("coordinator of a team") Then Return "plan"
        If sys.Contains("final answer to an administrator's request") Then Return "combine"
        Return "agent"
    End Function

    Private Shared Function AllText(r As ChatRequest) As String
        Return String.Join(vbLf, r.Messages.Select(Function(m) If(m.Content, "")))
    End Function

    ' ------------------------------------------------------------------ compaction keeps a digest of what was dropped

    Private Shared Function ReadCall(path As String, a As Integer, b As Integer) As ToolCallRequest
        Return New ToolCallRequest With {.Id = "c" & Guid.NewGuid().ToString("N").Substring(0, 6), .Name = "mcp__code-exec__read_file",
                                         .ArgumentsJson = $"{{""path"":""{path}"",""start_line"":{a},""end_line"":{b}}}"}
    End Function

    Private Shared Function Transcript(turns As Integer, Optional first As Integer = 0) As List(Of ChatMessage)
        Dim list As New List(Of ChatMessage) From {ChatMessage.ForSystem("system"), ChatMessage.ForUser("the task")}
        For i = first To first + turns - 1
            Dim c = ReadCall($"/src/file{i}.py", 1, 50)
            list.Add(ChatMessage.ForAssistant(Nothing, New List(Of ToolCallRequest) From {c}))
            list.Add(New ChatMessage With {.Role = "tool", .ToolCallId = c.Id, .Name = c.Name, .Content = "content of file " & i})
        Next
        Return list
    End Function

    <Fact>
    Public Sub CompactionKeepsWhatTheRemovedTurnsDid()
        ' A large window raises the message limit (60 used to drop work every dozen turns on a 262k window).
        Assert.Equal(131, ContextCompactor.MessageLimit(262144, 60))
        Assert.Equal(60, ContextCompactor.MessageLimit(32000, 60))
        Assert.Equal(160, ContextCompactor.MessageLimit(1000000, 60))
        Assert.Equal(200, ContextCompactor.MessageLimit(262144, 200))

        ' 40 read turns on a small window: the oldest go, and the note says what they read.
        Dim t = Transcript(40)
        Assert.True(ContextCompactor.Compact(t, 32000, 60))
        Dim note = t(2).Content
        Assert.StartsWith("[host] ", note)
        Assert.Contains("What you already did", note)
        Assert.Contains("- read_file /src/file0.py lines 1-50", note)
        Assert.DoesNotContain("re-run tools", note)
        Assert.Contains("Re-read a file only when your next step needs its exact text", note)
        ' A later compaction keeps the earlier lines and adds the new ones (the newest MaxDigestLines).
        Dim firstLines = ContextCompactor.DigestOf(note)
        Assert.Equal("- read_file /src/file0.py lines 1-50", firstLines(0))
        Dim more = Transcript(30, first:=40)
        t.AddRange(more.Skip(2))
        Assert.True(ContextCompactor.Compact(t, 32000, 60))
        Dim lines = ContextCompactor.DigestOf(t(2).Content)
        Assert.Contains(firstLines.Last(), lines)
        Assert.Contains(lines, Function(l) l.Contains("/src/file4"))
        Assert.Equal(ContextCompactor.MaxDigestLines, lines.Count)
        Assert.DoesNotContain("- read_file /src/file0.py lines 1-50", lines)   ' the oldest go first
        Assert.Equal(lines.Count, lines.Distinct().Count())

        ' A command, a failed call, an MCP name without a server prefix.
        Dim cmd As New ToolCallRequest With {.Id = "x", .Name = "mcp__code-exec__run_command", .ArgumentsJson = "{""command"":""pytest -q\ncat log""}"}
        Assert.Equal("- run_command `pytest -q cat log` (failed)", ContextCompactor.DigestLine(cmd, New ChatMessage With {.Role = "tool", .Content = "ERROR: timed out"}))
        Assert.Equal("- complete_task", ContextCompactor.DigestLine(New ToolCallRequest With {.Name = "complete_task", .ArgumentsJson = "{}"}, Nothing))
    End Sub

    ' ------------------------------------------------------------------ deadlines

    <Fact>
    Public Sub WorkStepsStopEarlySoTheFinalStepHasTime()
        Dim start = New DateTime(2026, 11, 2, 8, 0, 0, DateTimeKind.Utc)
        Dim g As New GlobalTask With {.StartedUtc = start, .Options = New GlobalTaskOptions With {.DeadlineMinutes = 600}}
        g.Steps.Add(New GlobalTaskStep With {.StepId = "s1"})
        ' No final step: the work runs to the deadline.
        Dim d = GlobalTaskService.DeadlinesOf(g, start, 240)
        Assert.Equal(start.AddMinutes(600), d.Hard)
        Assert.Equal(d.Hard, d.WorkCutoff)
        ' With a final step: 12% of the time, 15 to 60 minutes.
        g.Steps.Add(New GlobalTaskStep With {.StepId = "final"})
        Assert.Equal(start.AddMinutes(540), GlobalTaskService.DeadlinesOf(g, start, 240).WorkCutoff)
        g.Options.DeadlineMinutes = 120
        Assert.Equal(start.AddMinutes(105), GlobalTaskService.DeadlinesOf(g, start, 240).WorkCutoff)
        ' A short task never gives more than a third to the final step.
        g.Options.DeadlineMinutes = 30
        Assert.Equal(start.AddMinutes(20), GlobalTaskService.DeadlinesOf(g, start, 240).WorkCutoff)
    End Sub

    <Fact>
    Public Sub AgentsAreWarnedOnceWhenLittleTimeIsLeft()
        Dim now = New DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc)
        Dim t As New AgentTask With {.StartedUtc = now.AddHours(-4), .CreatedUtc = now.AddHours(-4), .DeadlineUtc = now.AddMinutes(40)}
        Dim cp As New TaskCheckpoint()
        ' A fifth of 4 h 40 min is 56 minutes: 40 minutes left is inside it.
        Dim warning = ReasoningTaskHandler.DeadlineWarning(t, cp, now)
        Assert.NotNull(warning)
        Assert.Contains("about 40 min from now", warning)
        Assert.Contains("complete_task before the deadline", warning)
        cp.DeadlineWarnedFor = t.DeadlineUtc
        Assert.Null(ReasoningTaskHandler.DeadlineWarning(t, cp, now))
        ' A new deadline is warned about again; far from it, nothing; no deadline, nothing.
        t.DeadlineUtc = now.AddMinutes(10)
        Assert.NotNull(ReasoningTaskHandler.DeadlineWarning(t, cp, now))
        t.DeadlineUtc = now.AddHours(5)
        Assert.Null(ReasoningTaskHandler.DeadlineWarning(t, cp, now))
        t.DeadlineUtc = Nothing
        Assert.Null(ReasoningTaskHandler.DeadlineWarning(t, cp, now))
        ' The prompt's line.
        Assert.Equal("about 2 h 5 min from now", PromptBuilder.LeftText(TimeSpan.FromMinutes(125)))
        Assert.Equal("already passed", PromptBuilder.LeftText(TimeSpan.FromMinutes(-1)))
        Assert.Contains("Deadline: 2026-11-02 14:00 UTC", PromptBuilder.DeadlineLine(now.AddHours(2), now))
    End Sub

    ''' <summary>
    ''' A step that is still running when the work time ends (the deadline minus the final step's share) is stopped with what it
    ''' got done, and the final step writes the answer.
    ''' </summary>
    <Fact>
    Public Async Function AtTheWorkCutoffTheFinalStepStillWritesTheAnswer() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Select Case KindOf(r)
                              Case "plan"
                                  Return FakeAIProvider.Text("{""understanding"": ""Build and document."", ""approach"": ""parallel"", ""steps"": [" &
                                                             "{""id"": ""s1"", ""agent"": ""A"", ""title"": ""Build"", ""instructions"": ""Build it."", ""dependsOn"": []}," &
                                                             "{""id"": ""s2"", ""agent"": ""B"", ""title"": ""Document"", ""instructions"": ""Write docs."", ""dependsOn"": []}]}")
                              Case "combine" : Return FakeAIProvider.Text("HOST ANSWER")
                          End Select
                          Select Case FakeAIProvider.AgentOf(r)
                              Case "A"
                                  ' Slow and never done.
                                  Thread.Sleep(400)
                                  Return FakeAIProvider.Tool("update_plan", "{""steps"":[{""step"":""Build"",""status"":""in_progress""}]}")
                              Case "C" : Return FakeAIProvider.Complete("FINAL BY C")
                              Case Else : Return FakeAIProvider.Complete("DOCS DONE")
                          End Select
                      End Function
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim g = Await host.H.GlobalTasks.CreateAsync("Build", "Build and document it.", New GlobalTaskOptions With {.SynthesisAgentId = "C", .DeadlineMinutes = 60}, "admin")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          g = Await host.H.GlobalTasks.GetAsync(g.Id)
                                                          Return g.Steps.Any(Function(s) s.StepId = "s1" AndAlso s.Status = "Running") AndAlso
                                                                 g.Steps.Any(Function(s) s.StepId = "s2" AndAlso s.IsFinished)
                                                      End Function, 60), "the steps did not start")
            ' Every step's task knows its deadline: work steps the cutoff (15 minutes before), the final step the deadline.
            Dim buildTask = Await host.H.Tasks.GetAsync(g.Steps.First(Function(s) s.StepId = "s1").TaskId)
            Assert.Equal(g.StartedUtc.Value.AddMinutes(45), buildTask.DeadlineUtc.Value, TimeSpan.FromSeconds(2))
            Assert.False(buildTask.DeadlineEnforced)
            Assert.Contains(p.Requests, Function(r) KindOf(r) = "agent" AndAlso FakeAIProvider.AgentOf(r) = "A" AndAlso AllText(r).Contains("Deadline: "))
            ' 50 minutes later (the work time is over, the deadline is not).
            Await host.H.Database.ExecuteAsync("UPDATE GlobalTasks SET StartedUtc=@s WHERE Id=@id", ("@s", CObj(Persistence.Db.Ts(DateTime.UtcNow.AddMinutes(-50)))), ("@id", g.Id))
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          g = Await host.H.GlobalTasks.GetAsync(g.Id)
                                                          Return g.Status = "Completed" OrElse g.Status = "Failed"
                                                      End Function, 60), $"did not finish: {g.Status}")
            Assert.Equal("Completed", g.Status)
            Dim build = g.Steps.First(Function(s) s.StepId = "s1")
            Assert.Equal("Cancelled", build.Status)
            Assert.Contains("before the deadline so the final answer has time", build.Error)
            Dim final = g.Steps.First(Function(s) s.StepId = "final")
            Assert.Equal("Completed", final.Status)
            Assert.Equal("FINAL BY C", g.FinalResult)
            Dim finalTask = Await host.H.Tasks.GetAsync(final.TaskId)
            Assert.True(finalTask.DeadlineUtc.Value > DateTime.UtcNow.AddMinutes(5))
        End Using
    End Function

    ' ------------------------------------------------------------------ planning

    <Fact>
    Public Sub AReportStepThatWaitsOnEverythingFoldsIntoTheFinalStep()
        Dim stepOf = Function(id As String, title As String, deps As String()) New GlobalTaskStep With {.StepId = id, .AgentId = "A", .Title = title, .Instructions = "Do " & title & ".", .DependsOn = deps.ToList()}
        Dim plan As New GlobalTaskService.PlanResult With {.Synthesis = "Combine."}
        plan.Steps.AddRange({stepOf("s1", "Build", {}), stepOf("s2", "Test", {}), stepOf("s3", "Docs", {}),
                             stepOf("s4", "Compile final delivery report", {"s1", "s2", "s3"})})
        Dim removed = GlobalTaskService.DropReportSteps(plan)
        Assert.Equal({"Compile final delivery report"}, removed.ToArray())
        Assert.Equal({"s1", "s2", "s3"}, plan.Steps.Select(Function(s) s.StepId).ToArray())
        Assert.Contains("Do Compile final delivery report.", plan.Synthesis)
        Assert.Equal({1, 2, 3}, plan.Steps.Select(Function(s) s.Ordinal).ToArray())
        ' Work that builds on everything stays (it is not a report), and so does a report that waits on only some steps.
        Dim work As New GlobalTaskService.PlanResult()
        work.Steps.AddRange({stepOf("s1", "Build", {}), stepOf("s2", "Test", {}), stepOf("s3", "Run benchmarks", {"s1", "s2"}), stepOf("s4", "Summary of tests", {"s2"})})
        Assert.Empty(GlobalTaskService.DropReportSteps(work))
        Assert.Equal(4, work.Steps.Count)
    End Sub

    ''' <summary>A revision keeps the earlier attempt's steps (with what each task did) for the page.</summary>
    <Fact>
    Public Async Function ARevisionKeepsTheEarlierStepsAndTheirStats() As Task
        Dim reviews = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Select Case KindOf(r)
                              Case "plan"
                                  Return FakeAIProvider.Text("{""understanding"": ""Research."", ""approach"": ""single"", ""steps"": [" &
                                                             "{""id"": ""s1"", ""agent"": ""A"", ""title"": ""Research"", ""instructions"": ""Research it."", ""dependsOn"": []}]}")
                              Case "combine" : Return FakeAIProvider.Text("ANSWER")
                          End Select
                          If FakeAIProvider.AgentOf(r) = "C" Then
                              Dim verdict = If(Interlocked.Increment(reviews) = 1, "revise", "approve")
                              Return FakeAIProvider.Tool("complete_task", JsonUtil.ToJson(New With {.success = True, .summary = "Review.",
                                                                                                     .data = New With {.verdict = verdict, .score = 5, .feedback = "More sources."}}))
                          End If
                          Return FakeAIProvider.Complete("RESEARCH " & reviews)
                      End Function
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim g = Await host.H.GlobalTasks.CreateAsync("Research", "Research it.", New GlobalTaskOptions With {.ReviewerAgentId = "C", .MaxRevisions = 1, .DeadlineMinutes = 600}, "admin")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          g = Await host.H.GlobalTasks.GetAsync(g.Id)
                                                          Return g.Status = "Completed" OrElse g.Status = "Failed"
                                                      End Function, 90), $"did not finish: {g?.Status}")
            Assert.Equal(1, g.Revision)
            ' The revision kept the first start, so its deadline is the first one (audit 4), not 10 hours from its own start.
            Assert.True(g.FirstStartedUtc.HasValue AndAlso g.FirstStartedUtc.Value < g.StartedUtc.Value, $"first {g.FirstStartedUtc:o}, current {g.StartedUtc:o}")
            Assert.Equal(g.FirstStartedUtc.Value.AddMinutes(600), host.H.GlobalTasks.DeadlineOf(g).Value)
            Dim history = Await host.H.GlobalTasks.HistoryAsync(g.Id)
            Dim r0 = Assert.Single(history)
            Assert.Equal(0, r0.Revision)
            Dim old = Assert.Single(r0.Steps)
            Assert.Equal("Completed", old.Status)
            Assert.Contains("RESEARCH", old.Result)
            Assert.NotEqual(old.TaskId, g.Steps.Single().TaskId)
            Dim stats = Await host.H.GlobalTasks.StepStatsAsync({old.TaskId})
            Assert.True(stats(old.TaskId).ModelCalls >= 1)
            Assert.True(stats(old.TaskId).ToolCalls >= 1)
        End Using
    End Function

    ' ------------------------------------------------------------------ models, subagents, queue, sandbox

    <Fact>
    Public Sub ARelayRefusingForBalanceIsOutOfCredit()
        Dim text = "litellm.APIError: APIError: OpenAIException - Request may exceed your balance. Top up via the Telegram bot or lower max_tokens."
        Assert.True(AiErrorClassifier.IsOutOfCredit(text, text.ToLowerInvariant()))
        Assert.False(AiErrorClassifier.IsOutOfCredit("Bad request: messages missing", "bad request: messages missing"))
    End Sub

    <Fact>
    Public Async Function ASubagentWaitsOutAnHourlyLimitButNotASpentCap() As Task
        Dim ctx As New TaskExecutionContext With {.Profile = New EffectiveProfile With {.Id = "A.sub-1", .IsSubagent = True},
                                                 .SaveCheckpointAsync = Function(c) Task.CompletedTask}
        Dim hourly = Await ReasoningTaskHandler.WaitForAiAsync(ctx, New TaskCheckpoint(), "AI usage limit: hourly AI call limit (300) reached", TimeSpan.FromMinutes(15))
        Assert.True(hourly.WaitingForAi)
        Dim daily = Await ReasoningTaskHandler.WaitForAiAsync(ctx, New TaskCheckpoint(), "AI usage limit: daily token limit reached", TimeSpan.FromMinutes(15))
        Assert.False(daily.WaitingForAi)
        ' After an hour of waiting even an hourly limit fails over to the parent.
        Dim waitedLong = New TaskCheckpoint With {.WaitingForAiSinceUtc = DateTime.UtcNow.AddMinutes(-61)}
        Assert.False((Await ReasoningTaskHandler.WaitForAiAsync(ctx, waitedLong, "hourly AI call limit (300) reached", Nothing)).WaitingForAi)
    End Function

    <Fact>
    Public Async Function OnlyAnotherReadyTaskMakesATaskGiveWay() As Task
        Using host = Await TestHost.StartAsync(Sub(o) o.Agents.Clear())
            Dim tasks = host.H.Tasks
            Dim now = DateTime.UtcNow
            Assert.True(Await tasks.InsertAsync(New AgentTask With {.Id = "mine", .AgentId = "A", .Name = "mine", .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = "c1", .Status = AgentTaskStatus.Running}))
            Assert.False(Await tasks.HasOtherReadyAsync("A", "mine", now))
            Assert.True(Await tasks.InsertAsync(New AgentTask With {.Id = "later", .AgentId = "A", .Name = "later", .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = "c2",
                                                                    .NextRunUtc = now.AddHours(1)}))
            Assert.False(Await tasks.HasOtherReadyAsync("A", "mine", now))      ' not due yet
            Assert.True(Await tasks.InsertAsync(New AgentTask With {.Id = "other", .AgentId = "B", .Name = "other", .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = "c3"}))
            Assert.False(Await tasks.HasOtherReadyAsync("A", "mine", now))      ' another agent's
            Assert.True(Await tasks.InsertAsync(New AgentTask With {.Id = "next", .AgentId = "A", .Name = "next", .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = "c4"}))
            Assert.True(Await tasks.HasOtherReadyAsync("A", "mine", now))
        End Using
    End Function

    <Fact>
    Public Sub TheWatchdogsOwnChecksAreNotSandboxWork()
        Dim reaper As New SandboxReaper(Nothing, Nothing, Nothing, "code-exec", Nothing, Nothing, Nothing, Nothing, Nothing)
        Assert.True(reaper.IsHealthCheck("echo ok"))
        Assert.False(reaper.IsHealthCheck("python app.py"))
        Assert.False(reaper.IsHealthCheck(""))
        reaper.HealthCheckCommand = "echo alive"
        Assert.True(reaper.IsHealthCheck(" echo alive "))
        Assert.False(reaper.IsHealthCheck("echo alive && rm -rf x"))
    End Sub

End Class
