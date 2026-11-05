Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Learning
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

Public Class LearningTests
    Private Const AnalysisJson As String =
        "Here is my analysis: ```json {""headline"": ""A burned its budget on escalations"", ""keyDecisions"": [""Escalated to cloud-a twice""], " &
        """rootCauses"": [""No local attempt""], ""lessons"": [{""title"": ""Try the local model first"", ""lesson"": ""Escalate only after the free local model has clearly failed."", " &
        """kind"": ""avoid"", ""tags"": [""escalation""]}], ""recommendations"": [""Lower its daily limit""]} ```"

    Private Shared Function IsAnalysis(r As ChatRequest) As Boolean
        Dim sys = r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content
        Return sys IsNot Nothing AndAlso sys.Contains("performance analyst")
    End Function

    Private Shared Sub EnableLearning(o As HostOptions, Optional requireApproval As Boolean = False)
        o.Learning.Enabled = True
        o.Learning.ModelKeys = New List(Of String) From {"local"}
        o.Learning.RequireApproval = requireApproval
        o.Learning.MinRoi = 2
        o.Learning.MinProfit = 1D
        o.Learning.LargeProfit = 100D
    End Sub

    ''' <summary>Analysis requests get the given report; normal work completes immediately.</summary>
    Private Shared Function Provider(Optional analysisReply As String = AnalysisJson) As FakeAIProvider
        Dim p As New FakeAIProvider()
        p.Responder = Function(r) If(IsAnalysis(r), FakeAIProvider.Text(analysisReply), FakeAIProvider.Complete("done"))
        Return p
    End Function

    ''' <summary>A task that starts on the paid cloud tier, so it costs money.</summary>
    Private Shared Async Function PaidTaskAsync(host As TestHost, agentId As String, name As String) As Task(Of String)
        Dim r = Await host.H.Agents.SubmitTaskAsync(agentId, New NewTaskRequest With {.Name = name, .Description = "do it", .StartTier = 2})
        Assert.True(r.Success, r.Message)
        Return r.TaskId
    End Function

    Private Shared Async Function WaitForStudyAsync(host As TestHost, agentId As String, outcome As String, status As String) As Task(Of CaseStudy)
        Dim found As CaseStudy = Nothing
        Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                      found = (Await host.H.Lessons.ListStudiesAsync(agentId, outcome)).FirstOrDefault(Function(s) s.Status = status)
                                                      Return found IsNot Nothing
                                                  End Function), $"no {outcome} study reached {status}")
        Return found
    End Function

    <Fact>
    Public Async Function BrokeAgentIsStudiedAndOtherAgentsLearnFromIt() As Task
        Dim p = Provider()
        Using host = Await TestHost.StartAsync(Sub(o) EnableLearning(o), p)
            ' 1. A pays for a cloud call (local "fails", task escalates), then its wallet is nearly drained.
            Dim t1 = Await PaidTaskAsync(host, "A", "First job")
            Await host.WaitForTaskStatusAsync("A", t1, AgentTaskStatus.Completed)
            Dim w = Await host.H.Wallets.GetAsync("A")
            Assert.True(w.LifetimeSpendMicros > 0)
            Await host.H.Wallets.AdjustAsync("A", -(w.BalanceMicros - 100), "drain", Nothing, "test")
            ' 2. The next paid call is refused for lack of funds: A has gone broke and a failure study opens.
            Await PaidTaskAsync(host, "A", "Second job")
            Dim study = Await WaitForStudyAsync(host, "A", "Failure", "Published")
            Assert.Equal("Broke", study.TriggerKind)
            Assert.Equal("local", study.AnalysisModel)
            Assert.Equal("A burned its budget on escalations", study.Headline)
            Dim lessons = Await host.H.Lessons.ListLessonsAsync(studyId:=study.Id)
            Dim lesson = Assert.Single(lessons)
            Assert.Equal("Avoid", lesson.Kind)
            Assert.Equal("Active", lesson.Status)
            ' The evidence records what the agent did (spend by reason, its tasks).
            Dim full = Await host.H.Lessons.GetStudyAsync(study.Id)
            Assert.Contains("cloud-a", full.EvidenceJson)
            Assert.Contains("First job", full.EvidenceJson)
            ' 3. A keeps the lesson in its private memory; other agents see it in their prompts and via search_lessons.
            Assert.Contains(Await host.H.MemoryStore.RecentAsync("A", 20), Function(m) m.Category = MemoryCategory.Lesson AndAlso m.Content.Contains("Try the local model first"))
            Dim bTask = Await host.SubmitAsync("B", "Plan escalation strategy", "Decide when to use stronger models")
            Await host.WaitForTaskStatusAsync("B", bTask, AgentTaskStatus.Completed)
            Assert.Contains(p.Requests, Function(r) FakeAIProvider.AgentOf(r) = "B" AndAlso r.Messages.Any(Function(m) If(m.Content, "").Contains("Try the local model first")))
            Assert.True((Await host.H.Lessons.GetLessonAsync(lesson.Id)).UseCount >= 1)
            Assert.Contains("search_lessons", host.H.ToolCatalog.BuildForAgent(host.H.Agents.GetEffectiveProfile("B")).Names())
            Assert.Single(Await host.H.Lessons.SearchAsync("escalation", 10))
            ' No second failure study within the cooldown.
            Assert.Null(Await host.H.Learning.RequestStudyAsync("A", "Failure", "Broke", "again", "host", Nothing, force:=False))
        End Using
    End Function

    <Fact>
    Public Async Function ProfitableAgentGetsSuccessStudyWithRuleBasedFallback() As Task
        ' The analysis model answers with unusable text, so the host falls back to rule-based analysis.
        Dim p = Provider("I am not able to produce JSON today.")
        Using host = Await TestHost.StartAsync(Sub(o) EnableLearning(o), p)
            Dim t1 = Await PaidTaskAsync(host, "A", "Write the customer proposal")
            Await host.WaitForTaskStatusAsync("A", t1, AgentTaskStatus.Completed)
            ' $5 profit on ~$0.001 of AI spend: very high ROI, linked to the task that produced it.
            Dim r = Await host.H.Wallets.CreditProfitAsync("A", TextUtil.DollarsToMicros(5D), "Proposal accepted", "INV-1", "k1", "admin", t1)
            Assert.True(r.Applied)
            Dim study = Await WaitForStudyAsync(host, "A", "Success", "Published")
            Assert.Equal("RoiMilestone", study.TriggerKind)
            Assert.Equal("rules", study.AnalysisModel)
            Dim lessons = Await host.H.Lessons.ListLessonsAsync(studyId:=study.Id)
            Assert.NotEmpty(lessons)
            Assert.All(lessons, Sub(l) Assert.Equal("Do", l.Kind))
            Assert.Contains(lessons, Function(l) l.Body.Contains("Write the customer proposal"))
            Dim full = Await host.H.Lessons.GetStudyAsync(study.Id)
            Assert.Contains("""profitCredited"":true", full.EvidenceJson)
        End Using
    End Function

    <Fact>
    Public Async Function ApprovalRetractionAndPinning() As Task
        Dim p = Provider()
        Using host = Await TestHost.StartAsync(Sub(o) EnableLearning(o, requireApproval:=True), p)
            Dim s = Await host.H.Learning.RequestStudyAsync("B", "Review", "Manual", "test", "admin", "Check B", force:=True)
            Assert.NotNull(s)
            Await WaitForStudyAsync(host, "B", "Review", "AwaitingReview")
            Dim lesson = Assert.Single(Await host.H.Lessons.ListLessonsAsync(agentId:="B"))
            Assert.Equal("PendingReview", lesson.Status)
            ' Pending lessons are not shown to agents.
            Assert.Empty(Await host.H.Lessons.RelevantAsync("escalation local model", 5, 2000))
            Await host.H.Lessons.UpdateLessonAsync(lesson.Id, Nothing, Nothing, Nothing, "Active", True, "admin")
            Dim shown = Await host.H.Lessons.RelevantAsync("unrelated words", 5, 2000)
            Assert.Equal(lesson.Id, Assert.Single(shown).Id) ' pinned lessons are always shown
            Await host.H.Lessons.UpdateLessonAsync(lesson.Id, Nothing, Nothing, Nothing, "Retracted", False, "admin")
            Assert.Empty(Await host.H.Lessons.RelevantAsync("escalation local model", 5, 2000))
            ' Re-analysis replaces the study's lessons.
            Assert.True(Await host.H.Learning.RerunAsync(s.Id, "admin"))
            Await WaitForStudyAsync(host, "B", "Review", "AwaitingReview")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Lessons.ListLessonsAsync(agentId:="B")).Count = 2))
        End Using
    End Function

    <Fact>
    Public Async Function RepeatedConclusionsConfirmExistingLessonsInsteadOfDuplicating() As Task
        Dim reply As String = AnalysisJson
        Dim p As New FakeAIProvider()
        p.Responder = Function(r) If(IsAnalysis(r), FakeAIProvider.Text(reply), FakeAIProvider.Complete("done"))
        Using host = Await TestHost.StartAsync(Sub(o) EnableLearning(o), p)
            ' Study 1 (B) creates the lesson.
            Dim s1 = Await host.H.Learning.RequestStudyAsync("B", "Review", "Manual", "t", "admin", Nothing, force:=True)
            Await WaitForStudyAsync(host, "B", "Review", "Published")
            Dim original = Assert.Single(Await host.H.Lessons.ListLessonsAsync(agentId:="B"))
            Assert.Equal(1, original.Confirmations)
            ' Study 2 (C): the analyst confirms the existing lesson by id and adds one genuinely new lesson.
            reply = "{""headline"": ""C wasted money the same way"", ""lessons"": [{""title"": ""Share research results early"", ""lesson"": ""Send partial results to the requester as soon as they are useful."", ""kind"": ""do""}], " &
                    $"""confirms"": [""{original.Id}"", ""lsn_does_not_exist""]}}"
            Dim s2 = Await host.H.Learning.RequestStudyAsync("C", "Review", "Manual", "t", "admin", Nothing, force:=True)
            Await WaitForStudyAsync(host, "C", "Review", "Published")
            ' Study 3 (A): the analyst rewrites the same lesson with other words in the body - it becomes a confirmation, not a duplicate.
            reply = "{""headline"": ""A escalated too early"", ""lessons"": [{""title"": ""Try the local model first!"", ""lesson"": ""Use the free model before paying for a stronger one."", ""kind"": ""avoid""}]}"
            Dim s3 = Await host.H.Learning.RequestStudyAsync("A", "Review", "Manual", "t", "admin", Nothing, force:=True)
            Await WaitForStudyAsync(host, "A", "Review", "Published")

            Dim active = Await host.H.Lessons.ListLessonsAsync(status:="Active")
            Assert.Equal(2, active.Count) ' the original and "Share research results early"
            Dim confirmed = Await host.H.Lessons.GetLessonAsync(original.Id)
            Assert.Equal(3, confirmed.Confirmations)
            Assert.NotNull(confirmed.LastConfirmedUtc)
            Assert.Contains(Await host.H.Lessons.ListConfirmedByStudyAsync(s2.Id), Function(l) l.Id = original.Id)
            Assert.Contains(Await host.H.Lessons.ListConfirmedByStudyAsync(s3.Id), Function(l) l.Id = original.Id)
            Assert.Empty(Await host.H.Lessons.ListLessonsAsync(studyId:=s3.Id))
            ' Re-analysis does not count a study's confirmation twice.
            Assert.True(Await host.H.Learning.RerunAsync(s3.Id, "admin"))
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Lessons.GetStudyAsync(s3.Id)).Status = "Published"))
            Assert.Equal(3, (Await host.H.Lessons.GetLessonAsync(original.Id)).Confirmations)
            ' Agents are told how well supported a lesson is.
            Dim bTask = Await host.SubmitAsync("B", "Plan local model usage", "Decide when to try the local model")
            Await host.WaitForTaskStatusAsync("B", bTask, AgentTaskStatus.Completed)
            Assert.Contains(p.Requests, Function(r) FakeAIProvider.AgentOf(r) = "B" AndAlso r.Messages.Any(Function(m) If(m.Content, "").Contains("confirmed by 3 case studies")))

            ' Retention drops old raw evidence but keeps the report and the lessons.
            Assert.NotNull((Await host.H.Lessons.GetStudyAsync(s1.Id)).EvidenceJson)
            Dim retention As New Persistence.RetentionService(host.H.Database, host.Options.Retention, host.H.MemoryStore, host.H.Artifacts, host.H.Log)
            Dim purged = Await retention.RunOnceAsync(DateTime.UtcNow.AddDays(host.Options.Retention.CaseStudyEvidenceDays + 1))
            Assert.Equal(3, purged("caseStudyEvidence"))
            Dim old = Await host.H.Lessons.GetStudyAsync(s1.Id)
            Assert.Null(old.EvidenceJson)
            Assert.NotNull(old.ReportJson)
            Assert.Equal(2, (Await host.H.Lessons.ListLessonsAsync(status:="Active")).Count)
        End Using
    End Function

    <Fact>
    Public Sub SimilarTitlesAreRecognizedAsTheSameLesson()
        Assert.True(LearningService.SameLesson("Try the local model first", "Try the local model first!"))
        Assert.True(LearningService.SameLesson("Escalating to paid models too early", "Escalate to paid model too early"))
        Assert.False(LearningService.SameLesson("Try the local model first", "Share research results early"))
        Assert.False(LearningService.SameLesson("Check your budget before paid steps", "Check tool availability before execution"))
    End Sub

    <Fact>
    Public Sub ReportParsingToleratesWrappingAndRejectsIncompleteReports()
        Dim r = LearningService.ParseReport(AnalysisJson)
        Assert.NotNull(r)
        Assert.Equal("Try the local model first", r.Lessons.Single().Title)
        Assert.Null(LearningService.ParseReport("no json here"))
        Assert.Null(LearningService.ParseReport("{""headline"": ""x"", ""lessons"": []}"))
        ' A report that only confirms existing lessons is complete.
        Assert.Equal({"lsn_1"}, LearningService.ParseReport("{""headline"": ""x"", ""lessons"": [], ""confirms"": [""lsn_1"", ""lsn_1""]}").Confirms)
        Assert.Equal("a (b) c", LessonStore.Sanitize("a <b> c", 100))
    End Sub

    <Fact>
    Public Async Function LearningApiAndProfitAttribution() As Task
        Dim env = Await HttpApiTests.StartWithHttpAsync()
        Using env.Host
            Using c = env.Client
                Dim login = Await c.PostAsync("/api/auth/login", HttpApiTests.JsonBody(New With {.username = "admin", .password = "pw-123456789"}))
                Dim csrf = JsonDocument.Parse(Await login.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString()
                Dim send = Async Function(method As HttpMethod, url As String, body As Object) As Task(Of HttpResponseMessage)
                               Dim req As New HttpRequestMessage(method, url) With {.Content = HttpApiTests.JsonBody(body)}
                               req.Headers.Add("X-CSRF-Token", csrf)
                               Return Await c.SendAsync(req)
                           End Function
                Assert.Equal(HttpStatusCode.OK, (Await c.GetAsync("/api/learning/summary")).StatusCode)
                ' Learning is off in the default test host.
                Assert.Equal(HttpStatusCode.Conflict, (Await send(HttpMethod.Post, "/api/learning/studies", New With {.agentId = "A", .outcome = "Review"})).StatusCode)
                ' Profit can be attributed to a task of the same agent only.
                Assert.Equal(HttpStatusCode.BadRequest, (Await send(HttpMethod.Post, "/api/agents/A/profit", New With {.amount = 1, .idempotencyKey = "x1", .taskId = "tsk_nope"})).StatusCode)
                Dim t = Await env.Host.SubmitAsync("A", "noop", handler:="noop")
                Await env.Host.WaitForTaskStatusAsync("A", t, AgentTaskStatus.Completed)
                Assert.Equal(HttpStatusCode.OK, (Await send(HttpMethod.Post, "/api/agents/A/profit", New With {.amount = 1, .idempotencyKey = "x2", .taskId = t})).StatusCode)
                Assert.Equal(t, (Await env.Host.H.Wallets.ListLedgerAsync("A", 5, "ProfitCredit")).Single().TaskId)
                Assert.Equal(HttpStatusCode.NotFound, (Await send(New HttpMethod("PATCH"), "/api/learning/lessons/lsn_nope", New With {.status = "Retracted"})).StatusCode)
            End Using
        End Using
    End Function
End Class
