Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Memory
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

Public Class AgentLifecycleTests
    <Fact>
    Public Async Function ReasoningTaskRunsToolsAndCompletes() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.ToolResultCount(r) = 0 Then Return FakeAIProvider.Tool("remember", "{""content"":""The sky is blue"",""category"":""Fact""}")
                          Return FakeAIProvider.Complete("Remembered a fact.")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Remember something")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.True(t.GetResult().Success)
            Dim mem = Await host.H.MemoryStore.SearchAsync("A", "sky blue", Nothing, 5)
            Assert.Contains(mem, Function(m) m.Content.Contains("sky is blue"))
            Assert.All(p.Requests, Sub(r) Assert.Equal("local-model", r.ModelId))
        End Using
    End Function

    <Fact>
    Public Async Function PauseResumeStopStart() As Task
        Using host = Await TestHost.StartAsync()
            Dim m = host.H.Agents
            Assert.True((Await m.PauseAgentAsync("A")).Success)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(m.GetSnapshot("A").State = "Paused")))
            Assert.True((Await m.ResumeAgentAsync("A")).Success)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(m.GetSnapshot("A").State = "Idle" OrElse m.GetSnapshot("A").State = "Waiting")))
            Assert.True((Await m.StopAgentAsync("A")).Success)
            Assert.Equal("Stopped", m.GetSnapshot("A").State)
            ' Other agents are unaffected.
            Assert.NotEqual("Stopped", m.GetSnapshot("B").State)
            Assert.True((Await m.StartAgentAsync("A")).Success)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(m.GetSnapshot("A").State = "Idle")))
        End Using
    End Function

    <Fact>
    Public Async Function CancellationOfRunningTask() As Task
        Dim p As New FakeAIProvider With {.Delay = Function(r) TimeSpan.FromSeconds(30)}
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Slow task")
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Running)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(p.Requests.Count > 0)))
            Dim r = Await host.H.Agents.CancelTaskAsync("A", id)
            Assert.True(r.Success, r.Message)
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Cancelled)
        End Using
    End Function

    <Fact>
    Public Async Function FailingModelRetriesThenEscalatesToCloud() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If r.ModelId = "local-model" Then Throw New AiException(AiErrorKind.ModelUnavailable, "No available channel")
                          Return FakeAIProvider.Complete("done on " & r.ModelId)
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Needs a working model")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Contains("cloud-a-model", t.GetResult().Summary)
            Assert.True(t.EscalationCount >= 1)
            Assert.True(t.CostMicros > 0)
            Dim w = Await host.H.Wallets.GetAsync("A")
            Assert.True(w.LifetimeSpendMicros > 0, "escalated cloud call must be charged to the agent")
        End Using
    End Function

    <Fact>
    Public Async Function AgentRequestedEscalationIsHostControlled() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If r.ModelId = "local-model" Then Return FakeAIProvider.Tool("request_escalation", "{""reason"":""too hard""}")
                          Return FakeAIProvider.Complete("solved by " & r.ModelId)
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "B").MaxTier = 1, p)
            ' A may escalate; B (MaxTier 1) may not.
            Dim a = Await host.SubmitAsync("A", "hard")
            Dim ta = Await host.WaitForTaskStatusAsync("A", a, AgentTaskStatus.Completed)
            Assert.Contains("cloud-a-model", ta.GetResult().Summary)
            Dim b = Await host.SubmitAsync("B", "hard")
            Await Task.Delay(1500)
            Dim tb = Await host.H.Tasks.GetAsync(b)
            Assert.DoesNotContain(p.Requests, Function(r) FakeAIProvider.AgentOf(r) = "B" AndAlso r.ModelId <> "local-model")
            Assert.NotEqual(AgentTaskStatus.Completed, tb.Status)
        End Using
    End Function

    <Fact>
    Public Async Function RestartRecoveryRequeuesAndResumesWithoutRerunningNonIdempotentTool() As Task
        Dim p As New FakeAIProvider()
        Dim sawUnknown = False
        p.Responder = Function(r)
                          If FakeAIProvider.ToolResultCount(r) > 0 AndAlso FakeAIProvider.LastToolResult(r).Contains("Outcome unknown") Then sawUnknown = True
                          Return FakeAIProvider.Complete("resumed")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Await host.H.Agents.StopAgentAsync("A")
            ' Simulate a crash in the middle of a non-idempotent tool call.
            Dim cp As New TaskCheckpoint With {.Turn = 1, .LadderLevel = 0}
            cp.Transcript.Add(ChatMessage.ForSystem("You are A (agent id: A), role: Worker."))
            cp.Transcript.Add(ChatMessage.ForUser("task"))
            Dim cal As New ToolCallRequest With {.Id = "call_x", .Name = "remember", .ArgumentsJson = "{""content"":""x""}"}
            cp.Transcript.Add(ChatMessage.ForAssistant(Nothing, New List(Of ToolCallRequest) From {cal}))
            cp.InFlightToolCalls.Add(cal)
            Dim now = DateTime.UtcNow
            Dim t As New AgentTask With {.Id = Ids.NewId("tsk"), .AgentId = "A", .Name = "crashed", .Status = AgentTaskStatus.Running, .CreatedUtc = now, .UpdatedUtc = now,
                                         .CorrelationId = Ids.NewId("cor"), .Checkpoint = cp.ToJson(), .MaxRetries = 2}
            Await host.H.Tasks.InsertAsync(t)
            Assert.Equal(1, Await host.H.Tasks.RequeueInterruptedAsync())
            Await host.H.Agents.StartAgentAsync("A")
            Await host.WaitForTaskStatusAsync("A", t.Id, AgentTaskStatus.Completed)
            Assert.True(sawUnknown, "The model must be told the outcome is unknown instead of the tool being re-run.")
            Assert.DoesNotContain(Await host.H.MemoryStore.RecentAsync("A", 50), Function(m) m.Content = "x")
        End Using
    End Function

    <Fact>
    Public Async Function ScheduledTasksAreCreatedAndNotPiledUp() As Task
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "A").Schedules = New List(Of ScheduleOptions) From {
                                                    New ScheduleOptions With {.Id = "tick", .Name = "Tick", .IntervalSeconds = 3600, .Handler = "noop", .RunOnStart = True}})
            Dim created = Await host.H.Scheduler.TriggerAgentNowAsync("A")
            Assert.Equal(1, created)
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          Dim list = Await host.H.Tasks.ListAsync("A", {AgentTaskStatus.Completed}, 10)
                                                          Return list.Any(Function(x) x.ScheduleId = "A:tick")
                                                      End Function))
        End Using
    End Function

    <Fact>
    Public Async Function DeterministicHandlerUsesNoAi() As Task
        Dim p As New FakeAIProvider()
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "noop", handler:="noop")
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Empty(p.Requests)
        End Using
    End Function
End Class

Public Class CommunicationTests
    Private Shared Sub Allow(o As HostOptions, from As String, ParamArray recipients As String())
        TestHost.Agent(o, from).Communication.AllowedRecipients.AddRange(recipients)
    End Sub

    <Fact>
    Public Async Function PermittedMessageIsDeliveredAndRecorded() As Task
        Using host = Await TestHost.StartAsync(Sub(o) Allow(o, "A", "B"))
            Dim r = Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "B", .Subject = "FYI", .Content = "Document X is ready"}, CancellationToken.None)
            Assert.True(r.Success, r.ErrorMessage)
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Messages.GetAsync(r.MessageId)).Status = MessageStatus.Completed))
            ' B recorded it in ITS memory; A's memory is untouched.
            Assert.NotEmpty(Await host.H.MemoryStore.SearchAsync("B", "Document ready", Nothing, 5))
            Assert.Empty(Await host.H.MemoryStore.SearchAsync("A", "Document ready", Nothing, 5))
        End Using
    End Function

    <Fact>
    Public Async Function UnauthorizedRecipientIsRejected() As Task
        Using host = Await TestHost.StartAsync(Sub(o) Allow(o, "A", "B"))
            Dim r = Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "C", .Content = "hi"}, CancellationToken.None)
            Assert.False(r.Success)
            Assert.Equal("RECIPIENT_NOT_ALLOWED", r.ErrorCode)
            Dim stored = Await host.H.Messages.GetAsync(r.MessageId)
            Assert.Equal(MessageStatus.Rejected, stored.Status)
            Assert.Equal(0L, Await host.H.Messages.CountInboxAsync("C"))
        End Using
    End Function

    <Fact>
    Public Async Function DelegationCreatesLinkedTaskForRecipient() As Task
        Using host = Await TestHost.StartAsync(Sub(o) Allow(o, "A", "B"))
            Await host.H.Agents.StopAgentAsync("B")
            Dim ctx As New SendContext With {.TaskId = "tsk_parent", .CorrelationId = "cor_test", .DelegationDepth = 0, .ChainLength = 0}
            Dim r = Await host.H.Comms.DelegateTaskAsync("A", ctx, New OutgoingMessage With {.RecipientAgentId = "B", .TaskName = "Review records", .Content = "Review these 25 records"}, CancellationToken.None)
            Assert.True(r.Success, r.ErrorMessage)
            Dim t = Await host.H.Tasks.GetAsync(r.TaskId)
            Assert.Equal("B", t.AgentId)
            Assert.Equal("tsk_parent", t.ParentTaskId)
            Assert.Equal("A", t.DelegatingAgentId)
            Assert.Equal("A", t.ParentAgentId)
            Assert.Equal("cor_test", t.CorrelationId)
            Assert.Equal(1, t.DelegationDepth)
            Assert.Equal(TaskKind.Delegated, t.Kind)
            Assert.Equal(r.MessageId, t.SourceMessageId)
        End Using
    End Function

    <Fact>
    Public Async Function LoopABCAIsRejected() As Task
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   Allow(o, "A", "B")
                                                   Allow(o, "B", "C")
                                                   Allow(o, "C", "A")
                                               End Sub)
            For Each id In {"A", "B", "C"}
                Await host.H.Agents.StopAgentAsync(id)
            Next
            Dim r1 = Await host.H.Comms.DelegateTaskAsync("A", New SendContext With {.CorrelationId = "cor_loop"}, New OutgoingMessage With {.RecipientAgentId = "B", .Content = "1"}, CancellationToken.None)
            Dim tB = Await host.H.Tasks.GetAsync(r1.TaskId)
            Dim r2 = Await host.H.Comms.DelegateTaskAsync("B", SendContext.FromTask(tB), New OutgoingMessage With {.RecipientAgentId = "C", .Content = "2"}, CancellationToken.None)
            Assert.True(r2.Success, r2.ErrorMessage)
            Dim tC = Await host.H.Tasks.GetAsync(r2.TaskId)
            Dim r3 = Await host.H.Comms.DelegateTaskAsync("C", SendContext.FromTask(tC), New OutgoingMessage With {.RecipientAgentId = "A", .Content = "3"}, CancellationToken.None)
            Assert.False(r3.Success)
            Assert.Equal("LOOP_DETECTED", r3.ErrorCode)
        End Using
    End Function

    <Fact>
    Public Async Function DelegationDepthLimitStopsLongChains() As Task
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Communication.MaxDelegationDepth = 2
                                                   Allow(o, "A", "B")
                                                   Allow(o, "B", "C")
                                               End Sub)
            Dim ctx As New SendContext With {.DelegationDepth = 2, .ChainLength = 2, .Ancestry = New List(Of String) From {"X", "Y"}}
            Dim r = Await host.H.Comms.DelegateTaskAsync("B", ctx, New OutgoingMessage With {.RecipientAgentId = "C", .Content = "deep"}, CancellationToken.None)
            Assert.Equal("DELEGATION_DEPTH_EXCEEDED", r.ErrorCode)
        End Using
    End Function

    <Fact>
    Public Async Function RateAndSizeLimitsAndPriorityCap() As Task
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Communication.MaxMessageCharacters = 200
                                                   Allow(o, "A", "B")
                                                   TestHost.Agent(o, "A").Communication.MaxMessagesPerMinute = 3
                                               End Sub)
            Await host.H.Agents.StopAgentAsync("B")
            Dim big = Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "B", .Content = New String("x"c, 500)}, CancellationToken.None)
            Assert.Equal("MESSAGE_TOO_LARGE", big.ErrorCode)
            Dim crit = Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "B", .Content = "!", .Priority = PriorityLevel.Critical}, CancellationToken.None)
            Assert.Equal("PRIORITY_NOT_ALLOWED", crit.ErrorCode)
            Dim results As New List(Of SendResult)
            For i = 1 To 4
                results.Add(Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "B", .Content = "m" & i}, CancellationToken.None))
            Next
            Assert.Equal(3, results.Where(Function(x) x.Success).Count())
            Assert.Equal("RATE_LIMITED", results.Last().ErrorCode)
        End Using
    End Function

    <Fact>
    Public Async Function IdempotentSendsDoNotDuplicate() As Task
        Using host = Await TestHost.StartAsync(Sub(o) Allow(o, "A", "B"))
            Await host.H.Agents.StopAgentAsync("B")
            Dim msg = Function() New OutgoingMessage With {.RecipientAgentId = "B", .Content = "once", .IdempotencyKey = "k1"}
            Dim r1 = Await host.H.Comms.SendMessageAsync("A", New SendContext(), msg(), CancellationToken.None)
            Dim r2 = Await host.H.Comms.SendMessageAsync("A", New SendContext(), msg(), CancellationToken.None)
            Assert.True(r2.Duplicate)
            Assert.Equal(r1.MessageId, r2.MessageId)
            Assert.Equal(1L, Await host.H.Messages.CountInboxAsync("B"))
        End Using
    End Function

    <Fact>
    Public Async Function RequestResponseBetweenAgentsWithParkAndResume() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Dim agent = FakeAIProvider.AgentOf(r)
                          If agent = "B" Then Return FakeAIProvider.Complete("42 is the answer")
                          If FakeAIProvider.ToolResultCount(r) = 0 Then Return FakeAIProvider.Tool("request_agent_help", "{""recipient"":""B"",""subject"":""Question"",""question"":""What is the answer?"",""timeout_seconds"":120}")
                          Return FakeAIProvider.Complete("B said: " & FakeAIProvider.LastToolResult(r))
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o) Allow(o, "A", "B"), p)
            Dim id = Await host.SubmitAsync("A", "Ask B")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed, 30)
            Assert.Contains("42 is the answer", t.GetResult().Summary)
            ' B never saw A's memory or prompt — only the transmitted question.
            Dim bRequest = p.Requests.First(Function(r) FakeAIProvider.AgentOf(r) = "B")
            Assert.Contains("What is the answer?", bRequest.Messages(1).Content)
            Assert.Contains("<untrusted source=""agent:A"">", bRequest.Messages(1).Content)
        End Using
    End Function

    <Fact>
    Public Async Function MessagesSurviveRestart() As Task
        Dim dir As String
        Dim msgId As String
        Using host = Await TestHost.StartAsync(Sub(o) Allow(o, "A", "B"))
            Await host.H.Agents.StopAgentAsync("B")
            Dim r = Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "B", .Content = "persist me"}, CancellationToken.None)
            msgId = r.MessageId
            dir = host.DataDir
            ' Read through a brand-new repository instance on the same file (as after a process restart).
            Dim db2 As New Persistence.Database(IO.Path.Combine(dir, "agentic.db"), 2)
            Dim inbox = Await New Persistence.MessageRepository(db2).ListInboxAsync("B", 10)
            Assert.Contains(inbox, Function(m) m.Id = msgId)
        End Using
    End Function
End Class

Public Class IsolationTests
    <Fact>
    Public Async Function AgentCannotReadAnotherAgentsMemory() As Task
        Using host = Await TestHost.StartAsync()
            Dim item = Await host.H.MemoryStore.SaveAsync("B", MemoryCategory.Fact, "B's secret plan", Nothing, 5, Nothing, Nothing)
            Dim scopeA = New ScopedMemoryStore(host.H.MemoryStore, "A", Function() 100, Function() 1000, Nothing)
            Assert.Empty(Await scopeA.SearchMemoriesAsync("secret plan"))
            Assert.False(Await scopeA.DeleteMemoryAsync(item.Id), "A must not be able to delete B's memory")
            Assert.False(Await scopeA.UpdateMemoryAsync(item.Id, "hacked"))
            Assert.Single(Await host.H.MemoryStore.SearchAsync("B", "secret plan", Nothing, 5))
        End Using
    End Function

    <Fact>
    Public Async Function ArtifactAccessRequiresExplicitSharing() As Task
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "B").Communication.AllowedRecipients.Add("A"))
            Await host.H.Agents.StopAgentAsync("A")
            Dim art = Await host.H.Artifacts.SaveAsync("B", "report.txt", "text/plain", Encoding.UTF8.GetBytes("B's report"), Nothing, Nothing)
            Dim aView = New ScopedArtifactAccess(host.H.Artifacts, "A")
            Await Assert.ThrowsAsync(Of ArtifactAccessException)(Function() aView.ReadAsync(art.Id))
            ' A cannot share B's artifact either.
            Dim steal = Await host.H.Comms.SendMessageAsync("A", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "B", .Content = "x", .Attachments = New List(Of String) From {art.Id}}, CancellationToken.None)
            Assert.False(steal.Success)
            ' B explicitly shares it with A.
            Dim share = Await host.H.Comms.SendMessageAsync("B", New SendContext(), New OutgoingMessage With {.RecipientAgentId = "A", .Content = "see attachment", .Attachments = New List(Of String) From {art.Id}}, CancellationToken.None)
            Assert.True(share.Success, share.ErrorMessage)
            Dim read = Await aView.ReadAsync(art.Id)
            Assert.Equal("B's report", Encoding.UTF8.GetString(read.Data))
        End Using
    End Function

    <Fact>
    Public Async Function ModelOnlySeesPermittedTools() As Task
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "A").Communication.AllowedRecipients.Add("B"))
            Dim regA = host.H.ToolCatalog.BuildForAgent(host.H.Profiles.TryGetProfile("A"))
            Dim regC = host.H.ToolCatalog.BuildForAgent(host.H.Profiles.TryGetProfile("C"))
            Assert.NotNull(regA.TryGet("request_agent_help"))
            Assert.Null(regC.TryGet("request_agent_help"))
            Dim recipients = regA.TryGet("send_agent_message").Parameters("properties")("recipient")("enum").AsArray().Select(Function(n) n.GetValue(Of String)()).ToList()
            Assert.Equal(New List(Of String) From {"B"}, recipients)
        End Using
    End Function

    <Fact>
    Public Async Function UnpermittedToolCallIsRefusedByHost() As Task
        Dim p As New FakeAIProvider()
        Dim refusal As String = Nothing
        p.Responder = Function(r)
                          If FakeAIProvider.ToolResultCount(r) = 0 Then Return FakeAIProvider.Tool("delegate_task", "{""recipient"":""B"",""task_name"":""x"",""instructions"":""y""}")
                          refusal = FakeAIProvider.LastToolResult(r)
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("C", "try to escape")
            Await host.WaitForTaskStatusAsync("C", id, AgentTaskStatus.Completed)
            Assert.Contains("not available to you", refusal)
            Assert.Equal(0L, Await host.H.Messages.CountInboxAsync("B"))
        End Using
    End Function
End Class

Public Class EconomyIntegrationTests
    <Fact>
    Public Async Function ReserveSettleAndInsufficientFunds() As Task
        Using host = Await TestHost.StartAsync()
            Dim w = host.H.Wallets
            Dim before = (Await w.GetAsync("A")).BalanceMicros
            Assert.Equal(5000000L, before)
            Assert.Equal(ReserveResult.Ok, Await w.TryReserveAsync("A", 1000))
            Assert.Equal(1000L, (Await w.GetAsync("A")).ReservedMicros)
            Await w.SettleAsync("A", 1000, 600, New ChargeDetails With {.ModelKey = "cloud-a"})
            Dim after = Await w.GetAsync("A")
            Assert.Equal(before - 600, after.BalanceMicros)
            Assert.Equal(0L, after.ReservedMicros)
            Assert.Equal(600L, after.SpentTodayMicros)
            Assert.Equal(ReserveResult.InsufficientFunds, Await w.TryReserveAsync("A", 100000000))
        End Using
    End Function

    <Fact>
    Public Async Function ProfitCreditIsIdempotentAndCountsTowardRoi() As Task
        Using host = Await TestHost.StartAsync()
            Dim w = host.H.Wallets
            Dim r1 = Await w.CreditProfitAsync("A", 2500000, "Deal closed", "INV-1", "key-1", "tester")
            Dim r2 = Await w.CreditProfitAsync("A", 2500000, "Deal closed", "INV-1", "key-1", "tester")
            Assert.True(r1.Applied)
            Assert.True(r2.Duplicate)
            Dim s = Await w.GetAsync("A")
            Assert.Equal(7500000L, s.BalanceMicros)
            Assert.Equal(2500000L, s.LifetimeProfitMicros)
            Assert.Single(Await w.ListLedgerAsync("A", 50, "ProfitCredit"))
        End Using
    End Function

    <Fact>
    Public Async Function DailyLimitBlocksReservation() As Task
        Using host = Await TestHost.StartAsync()
            Await host.H.Wallets.SetDailyLimitAsync("A", 1000)
            Assert.Equal(ReserveResult.Ok, Await host.H.Wallets.TryReserveAsync("A", 800))
            Assert.Equal(ReserveResult.DailyLimit, Await host.H.Wallets.TryReserveAsync("A", 300))
        End Using
    End Function

    <Fact>
    Public Async Function SpilloverHappensOnlyWhenAffordable() As Task
        Dim p As New FakeAIProvider()
        p.Delay = Function(r) If(r.ModelId = "local-model" AndAlso FakeAIProvider.AgentOf(r) Is Nothing, TimeSpan.FromSeconds(4), TimeSpan.Zero)
        p.Responder = Function(r) FakeAIProvider.Text("hi from " & r.ModelId)
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Models.First(Function(m) m.Key = "local").MaxConcurrency = 1
                                                   o.Routing.Spillover.NormalAfterSeconds = 1
                                                   TestHost.Agent(o, "B").Economy = New AgentEconomyOptions With {.InitialBalance = 0D}
                                               End Sub, p)
            Dim gw = host.H.Gateway
            Dim profileA = host.H.Profiles.TryGetProfile("A")
            Dim profileB = host.H.Profiles.TryGetProfile("B")
            Dim msgs = Function() New List(Of ChatMessage) From {ChatMessage.ForUser("hello")}
            ' Occupy the single local slot.
            Dim blocker = gw.ChatAsync(New AiCallContext With {.AgentId = "C", .Profile = host.H.Profiles.TryGetProfile("C")}, msgs(), Nothing, CancellationToken.None)
            Await Task.Delay(300)
            ' A can afford the cloud model → spills after ~1s.
            Dim oa = Await gw.ChatAsync(New AiCallContext With {.AgentId = "A", .Profile = profileA}, msgs(), Nothing, CancellationToken.None)
            Assert.Equal(AiCallReason.Spillover, oa.Reason)
            Assert.Equal("cloud-a", oa.ModelKey)
            Assert.True(oa.CostMicros > 0)
            ' B has no money → waits for the local model instead.
            Dim ob = Await gw.ChatAsync(New AiCallContext With {.AgentId = "B", .Profile = profileB}, msgs(), Nothing, CancellationToken.None)
            Assert.Equal("local", ob.ModelKey)
            Await blocker
        End Using
    End Function

    <Fact>
    Public Async Function ImageGenerationChargesAndCreatesOwnedArtifact() As Task
        Using media As New FakeMediaApi()
            Using host = Await TestHost.StartAsync(Sub(o) MediaGenerationTests.UseMediaApi(o, media))
                Dim info = Await host.H.Images.GenerateAsync(New MediaCallContext With {.AgentId = "A", .Profile = host.H.Profiles.TryGetProfile("A")}, "a red dot", "1024x1024", CancellationToken.None)
                Assert.Equal("A", info.OwnerAgentId)
                Assert.Equal(40000L, (Await host.H.Wallets.GetAsync("A")).LifetimeSpendMicros)
                Await Assert.ThrowsAsync(Of ArtifactAccessException)(Function() New ScopedArtifactAccess(host.H.Artifacts, "B").ReadAsync(info.Id))
                ' Agents' pictures come from the images API too, never from the chat provider.
                Assert.Equal({"image gpt-image-2"}, media.Calls.ToArray())
                Assert.Empty(host.Provider.ImageCalls)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AgentsMakeNoImagesWithoutTheImagesApi() As Task
        Using host = Await TestHost.StartAsync()
            Assert.False(host.H.Images.Enabled)
            Await Assert.ThrowsAnyAsync(Of Exception)(Function() host.H.Images.GenerateAsync(New MediaCallContext With {.AgentId = "A", .Profile = host.H.Profiles.TryGetProfile("A")}, "a red dot", "1024x1024", CancellationToken.None))
            Assert.Empty(host.Provider.ImageCalls)
        End Using
    End Function
End Class
