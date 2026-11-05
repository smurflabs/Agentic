Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

Public Class RecurrenceTests
    Private Shared Function Spec(json As String) As ScheduleOptions
        Dim el As JsonElement
        JsonUtil.TryParseElement(json, el)
        Dim s As New ScheduleOptions()
        Recurrence.Apply(el, s)
        Return s
    End Function

    <Fact>
    Public Sub RepeatSettingsBecomeSchedules()
        Dim s = Spec("{""mode"":""interval"",""every"":2,""unit"":""hours""}")
        Assert.Equal(7200, s.IntervalSeconds)
        Assert.Equal("Every 2 hours", s.Summary)
        s = Spec("{""mode"":""weekly"",""time"":""09:30"",""days"":[""mon"",""fri""]}")
        Assert.Equal("30 9 * * 1,5", s.Cron)
        Assert.Equal("Local", s.TimeZone)
        Assert.StartsWith("Weekly on Mon, Fri at 09:30", s.Summary)
        Assert.Equal("0 18 15 * *", Spec("{""mode"":""monthly"",""time"":""18:00"",""day"":15}").Cron)
        Assert.Equal("5 7 * * *", Spec("{""mode"":""daily"",""time"":""07:05"",""maxRuns"":3}").Cron)
        Assert.Equal(3, Spec("{""mode"":""daily"",""time"":""07:05"",""maxRuns"":3}").MaxRuns)
        For Each bad In {"{""mode"":""interval"",""every"":0}", "{""mode"":""interval"",""every"":30,""unit"":""seconds""}", "{""mode"":""weekly"",""time"":""09:00"",""days"":[]}",
                         "{""mode"":""daily"",""time"":""25:00""}", "{""mode"":""monthly"",""time"":""09:00"",""day"":31}", "{""mode"":""cron"",""cron"":""not a cron""}", "{""mode"":""yearly""}"}
            Assert.Throws(Of ArgumentException)(Sub() Spec(bad))
        Next
    End Sub

    <Fact>
    Public Sub NextOccurrenceRespectsStartAndEnd()
        Dim now = New DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
        Dim s As New ScheduleOptions With {.IntervalSeconds = 3600}
        Assert.Equal(now.AddHours(1), WorkScheduler.NextOccurrence(s, now))
        s.StartUtc = now.AddDays(2)
        Assert.Equal(now.AddDays(2), WorkScheduler.NextOccurrence(s, now)) ' first interval run at the start time
        s.StartUtc = Nothing
        s.EndUtc = now.AddMinutes(30)
        Assert.Null(WorkScheduler.NextOccurrence(s, now))                   ' next run would be after the end
        Dim cron As New ScheduleOptions With {.Cron = "0 9 * * *"}
        Assert.Equal(New DateTime(2026, 1, 2, 9, 0, 0, DateTimeKind.Utc), WorkScheduler.NextOccurrence(cron, now))
    End Sub

    <Fact>
    Public Async Function DashboardSchedulesRunUntilTheirLimitAndSurviveConfigSync() As Task
        Using host = Await TestHost.StartAsync()
            Dim s As New ScheduleOptions With {.Name = "Ping", .Description = "x", .Handler = "noop", .IntervalSeconds = 60, .MaxRuns = 2, .Summary = "Every minute, 2 runs max"}
            Dim created = Await host.H.Scheduler.CreateAdminScheduleAsync("A", s, "admin", DateTime.UtcNow)
            ' Re-syncing the agent's configured schedules keeps it.
            Await host.H.Scheduler.SyncAgentSchedulesAsync(host.H.Agents.GetEffectiveProfile("A"))
            Assert.Contains(Await host.H.Scheduler.ListAsync("A"), Function(x) CStr(x("id")) = created.Id AndAlso CStr(x("source")) = "Admin")
            ' The scheduler loop runs it once now; "Run now" makes it due again before its next interval.
            Dim runs = Async Function() CInt((Await host.H.Scheduler.ListAsync("A")).Single(Function(x) CStr(x("id")) = created.Id)("runCount"))
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() Await runs() = 1, 30))
            Assert.True(Await host.H.Scheduler.RunScheduleNowAsync(created.Id))
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() Await runs() = 2, 30))
            Dim row = (Await host.H.Scheduler.ListAsync("A")).Single(Function(x) CStr(x("id")) = created.Id)
            Assert.Equal(2, CInt(row("runCount")))
            Assert.False(CBool(row("enabled")))                      ' stopped after its maximum number of runs
            Assert.Null(Await host.H.Scheduler.DeleteAdminScheduleAsync(created.Id))
        End Using
    End Function
End Class

Public Class GlobalTaskTests
    Private Const PlanJson As String =
        "{""understanding"": ""Compare two products and recommend one."", ""approach"": ""pipeline"", ""steps"": [" &
        "{""id"": ""s1"", ""agent"": ""A"", ""title"": ""Research product X"", ""instructions"": ""Research X."", ""dependsOn"": []}," &
        "{""id"": ""s2"", ""agent"": ""b"", ""title"": ""Research product Y"", ""instructions"": ""Research Y."", ""dependsOn"": []}," &
        "{""id"": ""s3"", ""agent"": ""C"", ""title"": ""Compare"", ""instructions"": ""Compare X and Y."", ""dependsOn"": [""s1"", ""s2""]}], " &
        """synthesis"": ""Lead with the recommendation.""}"

    Private Shared Function Kind(r As ChatRequest) As String
        Dim sys = If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
        If sys.Contains("coordinator of a team") Then Return "plan"
        If sys.Contains("final answer to an administrator's request") Then Return "combine"
        Return "agent"
    End Function

    Private Shared Function Provider(Optional planReply As String = PlanJson) As FakeAIProvider
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Select Case Kind(r)
                              Case "plan" : Return FakeAIProvider.Text(planReply)
                              Case "combine" : Return FakeAIProvider.Text("FINAL: buy Y.")
                              Case Else : Return FakeAIProvider.Complete($"result from {FakeAIProvider.AgentOf(r)}")
                          End Select
                      End Function
        Return p
    End Function

    Private Shared Async Function WaitStatusAsync(host As TestHost, id As String, status As String) As Task(Of GlobalTask)
        Dim g As GlobalTask = Nothing
        Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                      g = Await host.H.GlobalTasks.GetAsync(id)
                                                      Return g.Status = status
                                                  End Function, 60), $"global task did not reach {status}; it is {g?.Status} ({g?.Error})")
        Return g
    End Function

    <Fact>
    Public Async Function PlansDistributesRunsDependentStepsAndCombines() As Task
        Dim p = Provider()
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim g = Await host.H.GlobalTasks.CreateAsync("Pick a product", "Compare X and Y and tell me which to buy.", New GlobalTaskOptions(), "admin")
            g = Await WaitStatusAsync(host, g.Id, "Completed")
            Assert.Equal("FINAL: buy Y.", g.FinalResult)
            Assert.Equal("local", g.PlannerModel)
            Assert.Equal("Compare two products and recommend one.", g.Understanding)
            Assert.Equal({"A", "B", "C"}, g.Steps.Select(Function(s) s.AgentId).ToArray()) ' agent ids are matched case-insensitively
            Assert.All(g.Steps, Sub(s) Assert.Equal("Completed", s.Status))
            ' Every step is a normal task in the global task's workflow.
            For Each s In g.Steps
                Dim t = Await host.H.Tasks.GetAsync(s.TaskId)
                Assert.Equal(g.CorrelationId, t.CorrelationId)
                Assert.Equal("global:" & g.Id, t.CreatedBy)
            Next
            ' The dependent step started after both others and received their results.
            Dim compareTask = Await host.H.Tasks.GetAsync(g.Steps(2).TaskId)
            Assert.Contains("result from A", compareTask.Description)
            Assert.Contains("result from B", compareTask.Description)
            Assert.True(g.Steps(2).StartedUtc >= g.Steps(0).CompletedUtc AndAlso g.Steps(2).StartedUtc >= g.Steps(1).CompletedUtc)
            ' The combine step saw every step result and the synthesis hint.
            Assert.Contains(p.Requests, Function(r) Kind(r) = "combine" AndAlso r.Messages.Any(Function(m) If(m.Content, "").Contains("result from C") AndAlso m.Content.Contains("Lead with the recommendation")))
        End Using
    End Function

    <Fact>
    Public Async Function PlansCanRequireApprovalAndFallBackToRules() As Task
        ' The planner answers with an unknown agent twice: the rule-based plan sends everything to one agent.
        Using host = Await TestHost.StartAsync(Nothing, Provider("{""understanding"": ""x"", ""steps"": [{""agent"": ""Ghost"", ""instructions"": ""y""}]}"))
            Dim o As New GlobalTaskOptions With {.RequirePlanApproval = True, .AgentIds = New List(Of String) From {"B", "C"}}
            Dim g = Await host.H.GlobalTasks.CreateAsync("Summarize the notes", "Summarize the meeting notes.", o, "admin")
            g = Await WaitStatusAsync(host, g.Id, "AwaitingApproval")
            Assert.Equal("rules", g.PlannerModel)
            Dim only = Assert.Single(g.Steps)
            Assert.Contains(only.AgentId, {"B", "C"})              ' only the chosen agents are used
            Assert.Null(only.TaskId)                                ' nothing starts before approval
            Assert.True(Await host.H.GlobalTasks.ApproveAsync(g.Id))
            g = Await WaitStatusAsync(host, g.Id, "Completed")
            Assert.Equal($"result from {only.AgentId}", g.FinalResult) ' a single step's result is the final answer
        End Using
    End Function

    <Fact>
    Public Async Function TheReviewerNeverDoesTheWorkItReviews() As Task
        ' The planner keeps giving a step to A, the reviewer: A is not offered, so those plans are rejected and the work goes to others.
        Dim p = Provider()
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim o As New GlobalTaskOptions With {.RequirePlanApproval = True, .ReviewerAgentId = "A"}
            Dim g = Await host.H.GlobalTasks.CreateAsync("Pick a product", "Compare X and Y and tell me which to buy.", o, "admin")
            g = Await WaitStatusAsync(host, g.Id, "AwaitingApproval")
            Assert.NotEmpty(g.Steps)
            Assert.DoesNotContain(g.Steps, Function(s) s.AgentId = "A")
            Dim planRequest = p.Requests.First(Function(r) Kind(r) = "plan")
            Dim userText = planRequest.Messages.First(Function(m) m.Role = "user").Content
            Assert.Contains("A reviews the final answer afterwards", userText)
            Assert.DoesNotContain("""id"":""A""", userText)
        End Using
        Assert.True(GlobalTaskService.IsCoordinator(New EffectiveProfile With {.Name = "ManagerAgent", .Role = "Team manager"}))
        Assert.False(GlobalTaskService.IsCoordinator(New EffectiveProfile With {.Name = "ResearchAgent", .Role = "Researcher"}))
    End Function

    <Fact>
    Public Async Function CancellingStopsQueuedSteps() As Task
        Dim p = Provider()
        p.Delay = Function(r) If(Kind(r) = "agent", TimeSpan.FromSeconds(20), TimeSpan.Zero)
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim g = Await host.H.GlobalTasks.CreateAsync("Slow job", "Do it.", New GlobalTaskOptions(), "admin")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.GlobalTasks.GetAsync(g.Id)).Steps.Any(Function(s) s.TaskId IsNot Nothing)))
            Assert.True(Await host.H.GlobalTasks.CancelAsync(g.Id, "admin"))
            g = Await host.H.GlobalTasks.GetAsync(g.Id)
            Assert.Equal("Cancelled", g.Status)
            Assert.All(g.Steps, Sub(s) Assert.Equal("Cancelled", s.Status))
        End Using
    End Function

    <Fact>
    Public Sub PlanValidationRejectsBadPlans()
        Dim agents = {"A", "B", "C"}
        Assert.NotNull(GlobalTaskService.ParsePlan(PlanJson, agents, 8, 4).Plan)
        Assert.Contains("not in the list", GlobalTaskService.ParsePlan(PlanJson.Replace("""agent"": ""C""", """agent"": ""Z"""), agents, 8, 4).Problem)
        Assert.Contains("at most 2 steps", GlobalTaskService.ParsePlan(PlanJson, agents, 2, 4).Problem)
        Assert.Contains("at most 2 different agents", GlobalTaskService.ParsePlan(PlanJson, agents, 8, 2).Problem)
        ' A step may only depend on earlier steps (no cycles).
        Dim forward = PlanJson.Replace("""dependsOn"": []}," & "{""id"": ""s2""", """dependsOn"": [""s3""]}," & "{""id"": ""s2""")
        Assert.Contains("not an earlier step", GlobalTaskService.ParsePlan(forward, agents, 8, 4).Problem)
        Assert.Contains("No JSON", GlobalTaskService.ParsePlan("I would ask the researcher.", agents, 8, 4).Problem)
    End Sub
End Class
