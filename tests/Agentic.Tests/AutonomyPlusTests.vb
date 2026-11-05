Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Configuration
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Fully autonomous agents: autonomy levels and their tools, approval policies, supervisor agents, unanswered questions,
''' initiative, hiring and retiring, bounties, assigned work, plans, parallel read-only tools, evaluations with rollback,
''' the secrets vault with http_request, mailboxes and learning from failure.
''' </summary>
Public Class AutonomyPlusTests

    Private Shared Function CompleteWith(summary As String, data As Object) As ChatResponse
        Return FakeAIProvider.Tool("complete_task", JsonUtil.ToJson(New With {.success = True, .summary = summary, .data = data}))
    End Function

    Private Shared Function UserText(r As ChatRequest) As String
        Return If(r.Messages.FirstOrDefault(Function(m) m.Role = "user")?.Content, "")
    End Function

    Private Shared Function SystemText(r As ChatRequest) As String
        Return If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
    End Function

    Private Shared Sub SetAutonomy(o As HostOptions, agentId As String, a As AgentAutonomyOptions)
        TestHost.Agent(o, agentId).Autonomy = a
    End Sub

    Private Shared Function BudgetContext(micros As Long) As String
        Return JsonUtil.ToJson(New With {.micros = micros, .amountUsd = micros / 1000000D, .justification = "test"})
    End Function

    Private Shared Async Function BalanceAsync(host As TestHost, agentId As String) As Task(Of Long)
        Return (Await host.H.Wallets.GetAsync(agentId)).BalanceMicros
    End Function

    ' ------------------------------------------------------------------------------------------ levels

    <Fact>
    Public Sub LevelsGrantToolsAndPoliciesAndSubagentsStaySupervised()
        Dim o = TestHost.DefaultOptions(Path.GetTempPath())
        Dim resolver As New ProfileResolver(o.BaseProfiles, o.Communication, o.Subagents, o.Autonomy)

        Dim sup = resolver.Resolve(New ProfileOptions With {.Id = "S", .BaseProfileId = "default"})
        Assert.Equal(AutonomyLevels.Supervised, sup.AutonomyLevel)
        Assert.True(sup.HasInternalTool("update_plan"))
        Assert.True(sup.HasInternalTool("read_web_page"))   ' it may search the web, so it may read pages
        Assert.False(sup.HasInternalTool("set_goal"))
        Assert.False(sup.HasInternalTool("run_code"))
        Assert.Equal(0L, sup.AutoApproveBudgetMicros)
        Assert.Equal(0, sup.InitiativeMinutes)
        Assert.Null(sup.VerifyPasses)

        Dim assisted = resolver.Resolve(New ProfileOptions With {.Id = "As", .BaseProfileId = "default", .Autonomy = New AgentAutonomyOptions With {.Level = "assisted"}})
        Assert.True(assisted.HasInternalTool("set_goal"))
        Assert.True(assisted.HasInternalTool("schedule_task"))
        Assert.True(assisted.HasInternalTool("run_code"))
        Assert.False(assisted.HasInternalTool("hire_agent"))
        Assert.Equal(2_000_000L, assisted.AutoApproveBudgetMicros)
        Assert.Equal(180, assisted.InitiativeMinutes)
        Assert.Empty(assisted.AutoApproveTools)
        Assert.False(assisted.AutoApplyBehaviorChanges)

        Dim auto = resolver.Resolve(New ProfileOptions With {.Id = "Au", .BaseProfileId = "default", .Autonomy = New AgentAutonomyOptions With {
            .Level = "autonomous", .SupervisorAgentId = "B", .CanHire = True, .DeniedTools = New List(Of String) From {"run_code"},
            .AllowedHttpHosts = New List(Of String) From {"API.example.com"}}})
        Assert.True(auto.IsAutonomous)
        Assert.Equal("B", auto.SupervisorAgentId)
        Assert.Empty(auto.AutoApproveTools)                  ' its supervisor checks held tools
        Dim alone = resolver.Resolve(New ProfileOptions With {.Id = "Al", .BaseProfileId = "default", .Autonomy = New AgentAutonomyOptions With {.Level = "autonomous"}})
        Assert.Contains("*", alone.AutoApproveTools)         ' without a supervisor it approves them itself
        Assert.True(auto.AutoApplyBehaviorChanges)
        Assert.True(auto.AutoApproveHires)
        Assert.Equal(240, auto.AskHumanTimeoutMinutes)
        Assert.Equal(1, auto.VerifyPasses)
        Assert.True(auto.HasInternalTool("hire_agent"))
        Assert.True(auto.HasInternalTool("assign_task"))
        Assert.True(auto.HasInternalTool("http_request"))
        Assert.Contains("api.example.com", auto.AllowedHttpHosts)
        Assert.False(auto.HasInternalTool("run_code"))       ' denied

        ' A subagent of an autonomous agent does one job: supervised, no hiring, managing or self-direction.
        Dim subagent = ProfileResolver.DeriveSubagent(auto, "Au.sub-1", New SubagentSpec With {.Name = "helper", .Models = New List(Of String) From {"local"}, .TimeoutMinutes = 5})
        Assert.Equal(AutonomyLevels.Supervised, subagent.AutonomyLevel)
        Assert.Null(subagent.SupervisorAgentId)
        Assert.False(subagent.HasInternalTool("hire_agent"))
        Assert.False(subagent.HasInternalTool("set_goal"))
        Assert.False(subagent.HasInternalTool("http_request"))
        Assert.True(subagent.HasInternalTool("update_plan"))
        Assert.Equal(0, subagent.InitiativeMinutes)

        ' A level that does not exist is a configuration error.
        Dim report As New ValidationReport()
        ConfigValidator.ValidateAutonomyOptions(New AgentAutonomyOptions With {.Level = "godmode"}, report, "Agent 'Z'")
        Assert.False(report.IsValid)
    End Sub

    ' ------------------------------------------------------------------------------------------ policies

    <Fact>
    Public Async Function BudgetRequestsWithinThePolicyAreApprovedAtOnceAndOthersWaitForAPerson() As Task
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "assisted", .AutoApproveBudgetUsd = 1D, .AutoApproveBudgetPerDayUsd = 1.5D}))
            Dim before = Await BalanceAsync(host, "A")
            Dim small = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.BudgetRequest, "Budget request", "Need 0.80", BudgetContext(800_000L), Nothing)
            Assert.Equal(ApprovalStatus.Approved, small.Status)
            Assert.Equal("policy", small.DecidedBy)
            Assert.Equal(before + 800_000L, Await BalanceAsync(host, "A"))

            ' The daily limit: another 0.80 would make 1.60 of 1.50.
            Dim second = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.BudgetRequest, "Budget request", "Need 0.80 more", BudgetContext(800_000L), Nothing)
            Assert.Equal(ApprovalStatus.Pending, second.Status)
            Assert.Null(second.Stage)
            Assert.Contains("daily", second.Reason)

            ' More than the per-request limit waits for a person; whoever decides, the credit follows.
            Dim big = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.BudgetRequest, "Budget request", "Need 5", BudgetContext(5_000_000L), Nothing)
            Assert.Equal(ApprovalStatus.Pending, big.Status)
            Dim decided = Await host.H.Approvals.ResolveAsync(big.Id, ApprovalStatus.Approved, "ok", "admin")
            Assert.Equal("human", decided.DecidedBy)
            Assert.Equal(before + 800_000L + 5_000_000L, Await BalanceAsync(host, "A"))

            ' A supervised agent's requests always wait for a person.
            Dim supervised = Await host.H.Approvals.CreateAsync("B", Nothing, Nothing, ApprovalKind.BudgetRequest, "Budget request", "Need 0.10", BudgetContext(100_000L), Nothing)
            Assert.Equal(ApprovalStatus.Pending, supervised.Status)
        End Using
    End Function

    <Fact>
    Public Async Function ToolApprovalsAndBehaviourChangesFollowTheAutonomousPolicy() As Task
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "autonomous"}))
            Dim tool = Await host.H.Approvals.CreateAsync("A", Nothing, "call_1", ApprovalKind.ToolApproval, "Approve deploy_site?", "Agent A wants to call deploy_site.", "{}", Nothing)
            Assert.Equal(ApprovalStatus.Approved, tool.Status)
            Assert.Equal("policy", tool.DecidedBy)

            ' A behaviour change is applied at once (and kept: there are no test cases to check it against).
            Dim approvalId = Ids.NewId("apr")
            Await host.H.Proposals.CreateAsync("A", "behavior", "Always state your sources.", "Fewer mistakes", approvalId)
            Dim change = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.ProfileChange, "Self-modification", "Agent A proposes a behaviour change.",
                                                            "Always state your sources.", Nothing, approvalId)
            Assert.Equal(ApprovalStatus.Approved, change.Status)
            Assert.Contains("Always state your sources.", Await host.H.Proposals.GetPatchAsync("A"))

            ' The same tool approval for a supervised agent waits for a person.
            Dim held = Await host.H.Approvals.CreateAsync("B", Nothing, "call_2", ApprovalKind.ToolApproval, "Approve deploy_site?", "Agent B wants to call deploy_site.", "{}", Nothing)
            Assert.Equal(ApprovalStatus.Pending, held.Status)
        End Using
    End Function

    <Fact>
    Public Async Function ASupervisorAgentDecidesWhatThePolicyDoesNotAndCanHandItToAPerson() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) = "B" AndAlso UserText(r).Contains("You supervise agent A") Then
                              If UserText(r).Contains("Need 9") Then Return CompleteWith("A person must decide this.", New With {.decision = "escalate", .answer = "Too much."})
                              Return CompleteWith("Worth it.", New With {.decision = "approve", .answer = "Approved: spend it on the report."})
                          End If
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "autonomous", .SupervisorAgentId = "B", .AutoApproveBudgetUsd = 0.5D}), p)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.IsRunning("B"))))
            Dim before = Await BalanceAsync(host, "A")
            Dim rec = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.BudgetRequest, "Budget request", "Need 2", BudgetContext(2_000_000L), Nothing)
            Assert.Equal(ApprovalStatus.Pending, rec.Status)
            Assert.Equal("supervisor:B", rec.Stage)
            Assert.NotNull(rec.StageTaskId)
            Dim final As ApprovalRecord = Nothing
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          final = Await host.H.Approvals.GetAsync(rec.Id)
                                                          Return final.Status <> ApprovalStatus.Pending
                                                      End Function, 60), "the supervisor did not decide")
            Assert.Equal(ApprovalStatus.Approved, final.Status)
            Assert.Equal("agent:B", final.DecidedBy)
            Assert.Contains("spend it on the report", final.Response)
            Assert.Equal(before + 2_000_000L, Await BalanceAsync(host, "A"))

            ' The supervisor hands a request it should not decide to people.
            Dim escalated = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.BudgetRequest, "Budget request", "Need 9", BudgetContext(9_000_000L), Nothing)
            Assert.Equal("supervisor:B", escalated.Stage)
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          final = Await host.H.Approvals.GetAsync(escalated.Id)
                                                          Return final.Stage Is Nothing
                                                      End Function, 60), "the request did not come back to people")
            Assert.Equal(ApprovalStatus.Pending, final.Status)
            Assert.Contains("handed it to a person", final.Reason)
            Assert.True(Await host.H.Approvals.CountPendingAsync() >= 1)
        End Using
    End Function

    <Fact>
    Public Async Function AQuestionNobodyAnswersIsAnsweredWithDecideYourself() As Task
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "autonomous", .AskHumanTimeoutMinutes = 20}))
            Dim q = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.AskHuman, "Which colour?", "Which colour should the logo be?", Nothing, Nothing)
            Assert.Equal(ApprovalStatus.Pending, q.Status)
            Await host.H.Autonomy.QuestionSweepAsync(DateTime.UtcNow.AddMinutes(10))
            Assert.Equal(ApprovalStatus.Pending, (Await host.H.Approvals.GetAsync(q.Id)).Status)
            Await host.H.Autonomy.QuestionSweepAsync(DateTime.UtcNow.AddMinutes(30))
            Dim answered = Await host.H.Approvals.GetAsync(q.Id)
            Assert.Equal(ApprovalStatus.Answered, answered.Status)
            Assert.Equal("timeout", answered.DecidedBy)
            Assert.Contains("Decide yourself", answered.Response)
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ initiative

    <Fact>
    Public Async Function AnIdleAutonomousAgentGetsAnInitiativeTaskWithinItsDailyLimit() As Task
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "autonomous", .InitiativeMinutes = 10})
                                                   o.Autonomy.MaxInitiativesPerDay = 1
                                               End Sub)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.IsRunning("A") AndAlso host.H.Agents.GetSnapshot("A").State = "Idle")))
            Dim created = Await host.H.Autonomy.InitiativeSweepAsync(DateTime.UtcNow.AddMinutes(30))
            Assert.Equal(1, created)
            Dim tasks = Await host.H.Tasks.ListAsync("A", {AgentTaskStatus.Pending, AgentTaskStatus.Running, AgentTaskStatus.Completed}, 20)
            Dim initiative = tasks.Single(Function(t) t.CreatedBy = AutonomyService.InitiativeCreator)
            Assert.Contains("most valuable", initiative.Description)
            Await host.WaitForTaskStatusAsync("A", initiative.Id, AgentTaskStatus.Completed)
            ' Supervised agents never get one, and the daily limit holds: 20 minutes on it is due again but for the limit. The limit
            ' counts from midnight UTC, so it is checked only while that is still today (after 23:40 a new day rightly starts again;
            ' 90 minutes on failed every evening after 22:30).
            Dim later = DateTime.UtcNow.AddMinutes(20)
            If later.Date = DateTime.UtcNow.Date Then Assert.Equal(0, Await host.H.Autonomy.InitiativeSweepAsync(later))
            Assert.DoesNotContain(Await host.H.Tasks.ListAsync("B", {AgentTaskStatus.Pending, AgentTaskStatus.Completed}, 20), Function(t) t.CreatedBy = AutonomyService.InitiativeCreator)
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ hiring

    <Fact>
    Public Async Function AnAgentHiresAHelperWithinItsLimitsAndRetiresIt() As Task
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "assisted", .CanHire = True, .MaxHires = 1, .AutoApproveHires = True}))
            Dim spec As New HireSpec With {.Name = "Data Helper", .Role = "Analyst", .Instructions = "Summarise data sets.", .Reason = "Too much data",
                                           .StartingBudgetUsd = 1D, .Level = "autonomous", .Tools = New List(Of String) From {"hire_agent", "remember", "recall"}}
            Dim aBefore = Await BalanceAsync(host, "A")
            Dim rec = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.HireAgent, "Hire Data Helper (Analyst)", "A wants a helper", JsonUtil.ToJson(spec), Nothing)
            Assert.Equal(ApprovalStatus.Approved, rec.Status)
            Dim hire = host.H.Autonomy.ActiveHires("A").Single()
            Assert.StartsWith("A-data-helper-", hire.AgentId)
            Assert.Contains(hire.AgentId, rec.Response)
            Dim hp = host.H.Agents.GetEffectiveProfile(hire.AgentId)
            Assert.Equal("A", hp.SupervisorAgentId)
            Assert.False(hp.CanHire)
            Assert.Equal(AutonomyLevels.Assisted, hp.AutonomyLevel)      ' never above its hirer
            Assert.False(hp.HasInternalTool("hire_agent"))
            Assert.True(hp.HasInternalTool("recall"))
            Assert.Contains(hire.AgentId, host.H.Autonomy.ManagedAgents("A"))
            Assert.Equal(1_000_000L, Await BalanceAsync(host, hire.AgentId))
            Assert.Equal(aBefore - 1_000_000L, Await BalanceAsync(host, "A"))

            ' The hiring limit holds.
            Dim second = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.HireAgent, "Hire Another", "again", JsonUtil.ToJson(New HireSpec With {.Name = "Another"}), Nothing)
            Assert.Equal(ApprovalStatus.Rejected, second.Status)
            Assert.Contains("limit", second.Response)

            ' Retiring returns its money and removes it; only the hirer can.
            Assert.False((Await host.H.Autonomy.RetireAsync("B", hire.AgentId, "not mine")).Ok)
            Dim r = Await host.H.Autonomy.RetireAsync("A", hire.AgentId, "done")
            Assert.True(r.Ok, r.Message)
            Assert.False(host.H.Agents.Exists(hire.AgentId))
            Assert.Equal(aBefore, Await BalanceAsync(host, "A"))
            Assert.Empty(host.H.Autonomy.ActiveHires("A"))
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ bounties and teams

    <Fact>
    Public Async Function AcceptedWorkEarnsItsBountyAndRejectedWorkDoesNot() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If UserText(r).Contains("Fail on purpose") Then Return FakeAIProvider.Complete("Could not do it.", success:=False)
                          Return FakeAIProvider.Complete("Done well.")
                      End Function
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim before = Await host.H.Wallets.GetAsync("A")
            Dim ok = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Paid work", .Description = "Do the paid work.",
                                                                                         .Delivery = New TaskDelivery With {.BountyMicros = 2_000_000L}})
            Dim failed = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Doomed work", .Description = "Fail on purpose.",
                                                                                             .Delivery = New TaskDelivery With {.BountyMicros = 3_000_000L}})
            Await host.WaitForTaskStatusAsync("A", ok.TaskId, AgentTaskStatus.Completed)
            Await host.WaitForTaskStatusAsync("A", failed.TaskId, AgentTaskStatus.Completed)
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          Dim ledger = Await host.H.Wallets.ListLedgerAsync("A", 50, "Bounty")
                                                          Return ledger.Count >= 1
                                                      End Function, 30), "no bounty was paid")
            Await Task.Delay(500)
            Dim bounties = Await host.H.Wallets.ListLedgerAsync("A", 50, "Bounty")
            Assert.Single(bounties)
            Assert.Equal(2_000_000L, bounties(0).AmountMicros)
            Dim after = Await host.H.Wallets.GetAsync("A")
            Assert.Equal(before.LifetimeProfitMicros + 2_000_000L, after.LifetimeProfitMicros)
        End Using
    End Function

    <Fact>
    Public Async Function AManagerAssignsWorkWithABountyAndWaitsForTheResult() As Task
        Dim p As New FakeAIProvider()
        Dim managerSaw As String = Nothing
        p.Responder = Function(r)
                          Dim agent = FakeAIProvider.AgentOf(r)
                          If agent = "B" AndAlso UserText(r).Contains("Get the count") Then
                              If FakeAIProvider.ToolResultCount(r) = 0 Then
                                  Return FakeAIProvider.Tool("assign_task", JsonUtil.ToJson(New With {.agent = "A", .task_name = "Count", .instructions = "Count to three.",
                                                                                                      .bounty_usd = 1, .wait_for_result = True}))
                              End If
                              managerSaw = FakeAIProvider.LastToolResult(r)
                              Return FakeAIProvider.Complete("A counted.")
                          End If
                          If agent = "A" AndAlso UserText(r).Contains("Count to three") Then Return FakeAIProvider.Complete("1 2 3")
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "B", New AgentAutonomyOptions With {.Level = "assisted", .Manages = New List(Of String) From {"A"}}), p)
            Dim aBefore = Await BalanceAsync(host, "A")
            Dim r = Await host.H.Agents.SubmitTaskAsync("B", New NewTaskRequest With {.Name = "Manage", .Description = "Get the count from your team."})
            Await host.WaitForTaskStatusAsync("B", r.TaskId, AgentTaskStatus.Completed, 90)
            Assert.NotNull(managerSaw)
            Assert.Contains("1 2 3", managerSaw)
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Wallets.ListLedgerAsync("A", 20, "Bounty")).Count = 1, 30), "the bounty was not paid")
            Dim paid = (Await host.H.Wallets.ListLedgerAsync("B", 20, "Bounty")).Single()
            Assert.Equal(-1_000_000L, paid.AmountMicros)
            Assert.True(Await BalanceAsync(host, "A") >= aBefore + 1_000_000L - 100_000L)
            ' A manager cannot give work to agents outside its team.
            Dim refused = Await host.H.Autonomy.AssignAsync("B", Nothing, "C", "x", "y", PriorityLevel.Normal, 0, False)
            Assert.False(refused.Success)
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ plans and tools

    <Fact>
    Public Async Function AnAgentKeepsAPlanRunsReadOnlyToolsTogetherAndDirectsItself() As Task
        Dim p As New FakeAIProvider()
        Dim sawPlan As String = Nothing
        Dim sawTwoResults As Boolean = False
        p.Responder = Function(r)
                          Dim agent = FakeAIProvider.AgentOf(r)
                          If agent = "A" AndAlso UserText(r).Contains("Organise yourself") Then
                              Select Case FakeAIProvider.ToolResultCount(r)
                                  Case 0
                                      Return FakeAIProvider.Tool("update_plan", JsonUtil.ToJson(New With {.steps = {New With {.step = "Look around", .status = "in_progress"},
                                                                                                                     New With {.step = "Plan the week", .status = "pending"}}}))
                                  Case 1
                                      sawPlan = FakeAIProvider.LastToolResult(r)
                                      Return New ChatResponse With {.FinishReason = "tool_calls", .ToolCalls = New List(Of ToolCallRequest) From {
                                          New ToolCallRequest With {.Id = "call_b1", .Name = "get_my_budget", .ArgumentsJson = "{}"},
                                          New ToolCallRequest With {.Id = "call_g1", .Name = "list_goals", .ArgumentsJson = "{}"}}}
                                  Case 3
                                      sawTwoResults = r.Messages.Where(Function(m) m.Role = "tool" AndAlso (m.ToolCallId = "call_b1" OrElse m.ToolCallId = "call_g1")).Count() = 2
                                      Return FakeAIProvider.Tool("set_goal", JsonUtil.ToJson(New With {.title = "Grow the newsletter", .review_every_minutes = 1440}))
                                  Case 4
                                      Return FakeAIProvider.Tool("schedule_task", JsonUtil.ToJson(New With {.name = "Weekly digest", .instructions = "Write the digest.", .every_minutes = 10080}))
                                  Case 5
                                      Return FakeAIProvider.Tool("create_plan", JsonUtil.ToJson(New With {.goal = "Launch", .steps = New Object() {
                                          New With {.name = "Draft", .instructions = "Draft the launch post."},
                                          New With {.name = "Publish", .instructions = "Publish the post.", .after = {1}}}}))
                                  Case Else
                                      Return FakeAIProvider.Complete("Organised.")
                              End Select
                          End If
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "assisted", .InitiativeMinutes = 0}), p)
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Organise", .Description = "Organise yourself."})
            Await host.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed, 90)
            Assert.NotNull(sawPlan)
            Assert.Contains("Plan updated", sawPlan)
            Assert.Contains("[>] 1. Look around", sawPlan)
            Assert.True(sawTwoResults, "both read-only calls should have answered")
            Dim goals = Await host.H.Goals.ListGoalsAsync("A", "active")
            Assert.Contains(goals, Function(g) g.Title = "Grow the newsletter" AndAlso g.CreatedBy = "agent:A")
            Dim schedules = Await host.H.Scheduler.ListAsync("A")
            Assert.Contains(schedules, Function(s) CStr(s("createdBy")) = "agent:A" AndAlso CStr(s("name")) = "Weekly digest")
            Dim planned = (Await host.H.Tasks.ListByCorrelationAsync((Await host.H.Tasks.GetAsync(r.TaskId)).CorrelationId)).Where(Function(t) t.Id <> r.TaskId).ToList()
            Dim publish = planned.Single(Function(t) t.Name = "Publish")
            Dim draft = planned.Single(Function(t) t.Name = "Draft")
            Assert.Contains(draft.Id, publish.DependsOn)
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ evaluations

    <Fact>
    Public Async Function ABehaviourChangeThatMakesTheAgentWorseIsRolledBack() As Task
        Dim judgeScore As Integer = 9
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If SystemText(r).Contains("You grade an AI agent's result") Then
                              Return FakeAIProvider.Text($"{{""score"": {Volatile.Read(judgeScore)}, ""pass"": true, ""feedback"": ""graded""}}")
                          End If
                          If UserText(r).Contains("Name the capital of France") Then Return FakeAIProvider.Complete("Paris")
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.Level = "autonomous", .InitiativeMinutes = 0}), p)
            Await host.H.Evals.AddCaseAsync("A", "Capital", "Name the capital of France.", "Paris", "admin")
            Dim baseline = Await host.H.Evals.StartAsync("A", "manual", Nothing, "admin")
            Assert.NotNull(baseline)
            Dim done As EvalRun = Nothing
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          done = Await host.H.Evals.GetRunAsync(baseline.Id)
                                                          Return done.Status = "done"
                                                      End Function, 90), "the baseline run did not finish")
            Assert.Equal("baseline", done.Verdict)
            Assert.Equal(1, done.Passed)
            Assert.Equal(9.0, done.AvgScore)

            ' The agent changes its behaviour (applied at once: it is autonomous); the check that follows scores worse.
            Volatile.Write(judgeScore, 3)
            Dim approvalId = Ids.NewId("apr")
            Dim prop = Await host.H.Proposals.CreateAsync("A", "behavior", "Answer every question with a poem.", "More charm", approvalId)
            Dim change = Await host.H.Approvals.CreateAsync("A", Nothing, Nothing, ApprovalKind.ProfileChange, "Self-modification", "Agent A proposes a behaviour change.",
                                                            "Answer every question with a poem.", Nothing, approvalId)
            Assert.Equal(ApprovalStatus.Approved, change.Status)
            Assert.Contains("poem", Await host.H.Proposals.GetPatchAsync("A"))
            Dim check As EvalRun = Nothing
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          check = (Await host.H.Evals.ListRunsAsync("A")).FirstOrDefault(Function(x) x.TriggerKind = "change")
                                                          Return check IsNot Nothing AndAlso check.Status = "done"
                                                      End Function, 90), "the change was not checked")
            Assert.Equal("rolled-back", check.Verdict)
            Assert.Equal(baseline.Id, check.BaselineRunId)
            Assert.DoesNotContain("poem", If(Await host.H.Proposals.GetPatchAsync("A"), ""))
            Assert.Equal("rolled-back", (Await host.H.Proposals.GetAsync(prop.Id)).Status)
        End Using
    End Function

    <Fact>
    Public Sub JudgeRepliesAndPatchEntriesAreReadExactly()
        Dim g = EvalService.ParseGrade("<think>hmm</think> Sure: {""score"": 8.4, ""pass"": true, ""feedback"": ""good""}")
        Assert.True(g.HasValue)
        Assert.Equal(8, g.Value.Score)
        Assert.Equal("passed", g.Value.Status)
        Assert.Equal("failed", EvalService.ParseGrade("{""score"": 9, ""pass"": false}").Value.Status)
        Assert.Null(EvalService.ParseGrade("no grade here"))

        Assert.Equal("", ProfileProposalService.RemoveFromPatch("Be brief.", "Be brief."))
        Assert.Equal("Cite sources.", ProfileProposalService.RemoveFromPatch("Be brief." & vbLf & "- Cite sources.", "Be brief."))
        Assert.Equal("Be brief.", ProfileProposalService.RemoveFromPatch("Be brief." & vbLf & "- Cite sources.", "Cite sources."))
        Assert.Null(ProfileProposalService.RemoveFromPatch("Be brief." & vbLf & "- Cite sources.", "Cite"))
    End Sub

    ' ------------------------------------------------------------------------------------------ vault and http_request

    <Fact>
    Public Async Function HttpRequestUsesVaultSecretsTheModelNeverSees() As Task
        Const secret As String = "sk-test-SECRET-98765"
        Using server As New FakeHttpServer()
            Dim p As New FakeAIProvider()
            Dim toolSaw As String = Nothing
            Dim boundSaw As String = Nothing
            Dim call_ = Function(secretName As String) FakeAIProvider.Tool("http_request", JsonUtil.ToJson(New Dictionary(Of String, Object) From {
                                                         {"method", "GET"}, {"url", $"http://127.0.0.1:{server.Port}/echo"},
                                                         {"headers", New Dictionary(Of String, String) From {{"Authorization", "Bearer {{secret:" & secretName & "}}"}}}}))
            p.Responder = Function(r)
                              If FakeAIProvider.AgentOf(r) = "A" AndAlso UserText(r).Contains("Call the API") Then
                                  Select Case FakeAIProvider.ToolResultCount(r)
                                      Case 0 : Return call_("API_KEY")
                                      Case 1
                                          toolSaw = FakeAIProvider.LastToolResult(r)
                                          Return call_("STRIPE_KEY")       ' bound to another host
                                      Case Else
                                          boundSaw = FakeAIProvider.LastToolResult(r)
                                          Return FakeAIProvider.Complete("Called.")
                                  End Select
                              End If
                              Return FakeAIProvider.Complete("ok")
                          End Function
            Using host = Await TestHost.StartAsync(Sub(o) SetAutonomy(o, "A", New AgentAutonomyOptions With {.AllowedHttpHosts = New List(Of String) From {"127.0.0.1"}}), p)
                Await host.H.Secrets.SetAsync("API_KEY", secret, "Test API", {"A"}, "admin")
                Await host.H.Secrets.SetAsync("STRIPE_KEY", "sk-stripe-55555", "Payments", {"A"}, "admin", {"api.stripe.com"})
                Assert.Null(Await host.H.Secrets.ResolveForAgentAsync("B", "API_KEY"))
                Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Call", .Description = "Call the API."})
                Await host.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed, 60)
                Assert.NotNull(toolSaw)
                Assert.Contains("HTTP 200", toolSaw)
                Assert.Contains("auth=Bearer [secret]", toolSaw)
                ' A secret bound to another host is never sent here (no request went out).
                Assert.NotNull(boundSaw)
                Assert.Contains("You may not use these secrets for 127.0.0.1", boundSaw)
                Assert.Equal("Bearer " & secret, server.LastAuthorization)
                Assert.Equal(0L, (Await host.H.Secrets.GetInfoAsync("STRIPE_KEY")).Uses)
                Assert.True(SecretVault.HostMatches({"*.stripe.com"}, "api.stripe.com"))
                Assert.False(SecretVault.HostMatches({"api.stripe.com"}, "evil.test"))
                Assert.DoesNotContain(p.Requests, Function(x) x.Messages.Any(Function(m) If(m.Content, "").Contains(secret)))
                Assert.Equal(1L, (Await host.H.Secrets.GetInfoAsync("API_KEY")).Uses)
                ' The list never shows values.
                Assert.DoesNotContain(secret, JsonUtil.ToJson(Await host.H.Secrets.ListAsync()))
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ email

    <Fact>
    Public Sub MailIsParsedFromMime()
        Dim raw = "From: =?UTF-8?B?w4lsw6hu?= <elen@example.com>" & vbCrLf &
                  "Subject: =?UTF-8?Q?Caf=C3=A9_order?=" & vbCrLf &
                  "Date: Tue, 27 Oct 2026 10:00:00 +0000" & vbCrLf &
                  "MIME-Version: 1.0" & vbCrLf &
                  "Content-Type: multipart/mixed; boundary=""outer""" & vbCrLf & vbCrLf &
                  "--outer" & vbCrLf &
                  "Content-Type: multipart/alternative; boundary=inner" & vbCrLf & vbCrLf &
                  "--inner" & vbCrLf &
                  "Content-Type: text/plain; charset=utf-8" & vbCrLf &
                  "Content-Transfer-Encoding: quoted-printable" & vbCrLf & vbCrLf &
                  "Two caf=C3=A9s, please. Long line that wraps=" & vbCrLf & " here." & vbCrLf &
                  "--inner" & vbCrLf &
                  "Content-Type: text/html; charset=utf-8" & vbCrLf &
                  "Content-Transfer-Encoding: base64" & vbCrLf & vbCrLf &
                  Convert.ToBase64String(Encoding.UTF8.GetBytes("<p>Two cafés</p>")) & vbCrLf &
                  "--inner--" & vbCrLf &
                  "--outer" & vbCrLf &
                  "Content-Type: application/pdf; name=""menu.pdf""" & vbCrLf &
                  "Content-Disposition: attachment; filename=""menu.pdf""" & vbCrLf &
                  "Content-Transfer-Encoding: base64" & vbCrLf & vbCrLf &
                  "JVBERi0xLjQK" & vbCrLf &
                  "--outer--" & vbCrLf
        Dim mail = MailParser.Parse(Encoding.Latin1.GetBytes(raw))
        Assert.Equal("elen@example.com", mail.FromAddress)
        Assert.Equal("Élèn", mail.FromName)
        Assert.Equal("Café order", mail.Subject)
        Assert.Equal("Two cafés, please. Long line that wraps here.", mail.Body)
        Assert.Contains("menu.pdf", mail.Attachments)
        Assert.True(EmailInboxService.SenderAllowed("@example.com, boss@corp.test", "Elen@Example.com"))
        Assert.False(EmailInboxService.SenderAllowed("@example.com", "x@evil.test"))
        Assert.True(EmailInboxService.SenderAllowed("", "anyone@anywhere.test"))
    End Sub

    Private Shared Function Mail(from As String, subject As String, body As String) As String
        Return $"From: {from}" & vbCrLf & $"Subject: {subject}" & vbCrLf & "Content-Type: text/plain; charset=utf-8" & vbCrLf & vbCrLf & body & vbCrLf
    End Function

    <Fact>
    Public Async Function NewMailFromAllowedSendersBecomesTasks() As Task
        Using imap As New FakeImapServer("pw-123")
            Using host = Await TestHost.StartAsync()
                Await host.H.Secrets.SetAsync("MAIL_PW", "pw-123", "Mailbox password", {"A"}, "admin")
                Dim box = Await host.H.Inboxes.SaveAsync(New EmailInbox With {
                    .AgentId = "A", .Host = "127.0.0.1", .Port = imap.Port, .UseSsl = False, .Username = "agent@corp.test", .PasswordSecret = "MAIL_PW",
                    .AllowedSenders = "@example.com"}, "admin")
                imap.Add(1, Mail("old@example.com", "Old", "Already handled."))
                ' The first check only notes where the mailbox stands.
                Dim first = Await host.H.Inboxes.PollAsync(box, CancellationToken.None)
                Assert.Null(first.ErrorMessage)
                Assert.Equal(0, first.Created)
                imap.Add(2, Mail("Customer <buyer@example.com>", "Order 42", "Please ship order 42 today."))
                imap.Add(3, Mail("spam@evil.test", "Win", "Click here."))
                Dim second = Await host.H.Inboxes.PollAsync(Await host.H.Inboxes.GetAsync(box.Id), CancellationToken.None)
                Assert.Null(second.ErrorMessage)
                Assert.Equal(1, second.Created)
                Dim tasks = Await host.H.Tasks.ListAsync("A", {AgentTaskStatus.Pending, AgentTaskStatus.Running, AgentTaskStatus.Completed}, 20)
                Dim t = tasks.Single(Function(x) x.CreatedBy = "email:" & box.Id)
                Assert.Equal("Email: Order 42", t.Name)
                Assert.Contains("Please ship order 42 today.", t.Description)
                Assert.Contains("untrusted", t.Description)
                ' Nothing is imported twice.
                Dim third = Await host.H.Inboxes.PollAsync(Await host.H.Inboxes.GetAsync(box.Id), CancellationToken.None)
                Assert.Equal(0, third.Created)
                ' A wrong password is reported, not thrown.
                Await host.H.Secrets.SetAsync("MAIL_PW", "wrong", "Mailbox password", {"A"}, "admin")
                Dim bad = Await host.H.Inboxes.PollAsync(Await host.H.Inboxes.GetAsync(box.Id), CancellationToken.None)
                Assert.NotNull(bad.ErrorMessage)
                Assert.Contains("LOGIN", bad.ErrorMessage)
                Assert.NotNull((Await host.H.Inboxes.GetAsync(box.Id)).LastError)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------------------------------ learning

    <Fact>
    Public Async Function RepeatedFailuresOpenACaseStudy() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If UserText(r).Contains("Impossible job") Then Return FakeAIProvider.Complete("Could not do it.", success:=False)
                          Return FakeAIProvider.Text("{""headline"": ""x"", ""lessons"": []}")
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Learning.Enabled = True
                                                   o.Autonomy.FailureStudyThreshold = 2
                                               End Sub, p)
            For i = 1 To 2
                Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Impossible " & i, .Description = "Impossible job."})
                Await host.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed)
            Next
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          Dim studies = Await host.H.Lessons.ListStudiesAsync("A", "Failure")
                                                          Return studies.Any(Function(s) s.TriggerKind = "TaskFailures")
                                                      End Function, 30), "no case study was opened")
        End Using
    End Function
End Class

''' <summary>A one-endpoint HTTP server that answers with the Authorization header it received.</summary>
Friend NotInheritable Class FakeHttpServer
    Implements IDisposable

    Private ReadOnly _listener As TcpListener
    Private ReadOnly _cts As New CancellationTokenSource()
    Public ReadOnly Property Port As Integer
    Public Property LastAuthorization As String

    Public Sub New()
        _listener = New TcpListener(IPAddress.Loopback, 0)
        _listener.Start()
        Port = CType(_listener.LocalEndpoint, IPEndPoint).Port
        Dim ignored = Task.Run(AddressOf AcceptLoopAsync)
    End Sub

    Private Async Function AcceptLoopAsync() As Task
        While Not _cts.IsCancellationRequested
            Dim client As TcpClient
            Try
                client = Await _listener.AcceptTcpClientAsync(_cts.Token)
            Catch
                Return
            End Try
            Dim ignored = Task.Run(Function() ServeAsync(client))
        End While
    End Function

    Private Async Function ServeAsync(client As TcpClient) As Task
        Using client
            Dim stream = client.GetStream()
            Dim reader As New StreamReader(stream, Encoding.ASCII, False, 1024, leaveOpen:=True)
            Dim auth As String = Nothing
            While True
                Dim line = Await reader.ReadLineAsync()
                If String.IsNullOrEmpty(line) Then Exit While
                If line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase) Then auth = line.Substring(14).Trim()
            End While
            LastAuthorization = auth
            Dim body = Encoding.UTF8.GetBytes("auth=" & auth)
            Dim head = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK{vbCrLf}Content-Type: text/plain{vbCrLf}Content-Length: {body.Length}{vbCrLf}Connection: close{vbCrLf}{vbCrLf}")
            Await stream.WriteAsync(head)
            Await stream.WriteAsync(body)
            Await stream.FlushAsync()
        End Using
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _listener.Stop()
    End Sub
End Class

''' <summary>Just enough IMAP4rev1 for the mailbox reader: LOGIN, SELECT, UID SEARCH, UID FETCH, LOGOUT.</summary>
Friend NotInheritable Class FakeImapServer
    Implements IDisposable

    Private ReadOnly _listener As TcpListener
    Private ReadOnly _cts As New CancellationTokenSource()
    Private ReadOnly _password As String
    Private ReadOnly _messages As New SortedDictionary(Of Long, String)
    Public ReadOnly Property Port As Integer

    Public Sub New(password As String)
        _password = password
        _listener = New TcpListener(IPAddress.Loopback, 0)
        _listener.Start()
        Port = CType(_listener.LocalEndpoint, IPEndPoint).Port
        Dim ignored = Task.Run(AddressOf AcceptLoopAsync)
    End Sub

    Public Sub Add(uid As Long, raw As String)
        SyncLock _messages
            _messages(uid) = raw
        End SyncLock
    End Sub

    Private Async Function AcceptLoopAsync() As Task
        While Not _cts.IsCancellationRequested
            Dim client As TcpClient
            Try
                client = Await _listener.AcceptTcpClientAsync(_cts.Token)
            Catch
                Return
            End Try
            Dim ignored = Task.Run(Function() ServeAsync(client))
        End While
    End Function

    Private Async Function ServeAsync(client As TcpClient) As Task
        Using client
            Dim stream = client.GetStream()
            Dim reader As New StreamReader(stream, Encoding.Latin1, False, 1024, leaveOpen:=True)
            Dim send = Async Function(text As String) As Task
                           Dim bytes = Encoding.Latin1.GetBytes(text)
                           Await stream.WriteAsync(bytes)
                           Await stream.FlushAsync()
                       End Function
            Await send("* OK fake IMAP ready" & vbCrLf)
            While True
                Dim line = Await reader.ReadLineAsync()
                If line Is Nothing Then Return
                Dim parts = line.Split(" "c, 3)
                Dim tag = parts(0)
                Dim cmd = If(parts.Length > 1, parts(1).ToUpperInvariant(), "")
                Dim rest = If(parts.Length > 2, parts(2), "")
                Dim snapshot As List(Of KeyValuePair(Of Long, String))
                SyncLock _messages
                    snapshot = _messages.ToList()
                End SyncLock
                Select Case cmd
                    Case "LOGIN"
                        If rest.EndsWith("""" & _password & """", StringComparison.Ordinal) Then
                            Await send(tag & " OK LOGIN completed" & vbCrLf)
                        Else
                            Await send(tag & " NO [AUTHENTICATIONFAILED] Invalid credentials" & vbCrLf)
                        End If
                    Case "SELECT"
                        Await send($"* {snapshot.Count} EXISTS{vbCrLf}* OK [UIDVALIDITY 77] UIDs valid{vbCrLf}{tag} OK [READ-WRITE] SELECT completed{vbCrLf}")
                    Case "UID"
                        If rest.StartsWith("SEARCH", StringComparison.OrdinalIgnoreCase) Then
                            Dim from = Long.Parse(Text.RegularExpressions.Regex.Match(rest, "UID (\d+):\*").Groups(1).Value)
                            Dim uids = snapshot.Select(Function(kv) kv.Key).Where(Function(u) u >= from).ToList()
                            ' "n:*" always includes the highest UID.
                            If uids.Count = 0 AndAlso snapshot.Count > 0 Then uids.Add(snapshot.Last().Key)
                            Await send("* SEARCH" & String.Concat(uids.Select(Function(u) " " & u.ToString())) & vbCrLf & tag & " OK SEARCH completed" & vbCrLf)
                        ElseIf rest.StartsWith("FETCH", StringComparison.OrdinalIgnoreCase) Then
                            Dim uid = Long.Parse(Text.RegularExpressions.Regex.Match(rest, "FETCH (\d+)").Groups(1).Value)
                            Dim raw = snapshot.FirstOrDefault(Function(kv) kv.Key = uid).Value
                            If raw IsNot Nothing Then
                                Dim size = Encoding.Latin1.GetByteCount(raw)
                                Await send($"* 1 FETCH (UID {uid} BODY[] {{{size}}}{vbCrLf}{raw}){vbCrLf}")
                            End If
                            Await send(tag & " OK FETCH completed" & vbCrLf)
                        Else
                            Await send(tag & " BAD unknown" & vbCrLf)
                        End If
                    Case "LOGOUT"
                        Await send("* BYE" & vbCrLf & tag & " OK LOGOUT completed" & vbCrLf)
                        Return
                    Case Else
                        Await send(tag & " BAD unknown command" & vbCrLf)
                End Select
            End While
        End Using
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _listener.Stop()
    End Sub
End Class
