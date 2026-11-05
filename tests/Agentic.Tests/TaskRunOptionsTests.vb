Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports Agentic
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Configuration
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Run options set when a task or global task is created: approve tool use automatically (for the task and the work it
''' hands on), and keep working past the deadline (marked late instead of cancelled).
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class TaskRunOptionsTests

    Private Const Command As String = "{""command"": ""Remove-Item C:\\temp\\x.txt""}"

    Private Shared Async Function SubmitAsync(host As TestHost, agentId As String, req As NewTaskRequest) As Task(Of String)
        If req.Name Is Nothing Then req.Name = "job"
        If req.Handler Is Nothing Then req.Handler = "noop"
        Dim r = Await host.H.Agents.SubmitTaskAsync(agentId, req)
        Assert.True(r.Success, r.Message)
        Return r.TaskId
    End Function

    Private Shared Function AskAsync(host As TestHost, agentId As String, taskId As String) As Task(Of ApprovalRecord)
        Return host.H.Approvals.CreateAsync(agentId, taskId, Nothing, ApprovalKind.ToolApproval, "Approve mcp__shell__run_command?", "x", Command, Nothing)
    End Function

    <Fact>
    Public Async Function ToolCallsOfAnAutoApprovedTaskAndItsWorkAreApprovedAtOnce() As Task
        Using host = Await TestHost.StartAsync()
            Dim later = DateTime.UtcNow.AddDays(1)
            Dim auto = await SubmitAsync(host, "A", New NewTaskRequest With {.AutoApproveTools = True, .NextRunUtc = later})
            Dim own = Await AskAsync(host, "A", auto)
            Assert.Equal(ApprovalStatus.Approved, own.Status)
            Assert.Equal("policy", own.DecidedBy)
            Assert.Contains("approve tool use automatically", own.Reason)

            ' Work it handed to another agent (same workflow) is covered too.
            Dim correlation = (Await host.H.Tasks.GetAsync(auto)).CorrelationId
            Dim handedOn = Await SubmitAsync(host, "B", New NewTaskRequest With {.CorrelationId = correlation, .NextRunUtc = later})
            Dim delegated = Await AskAsync(host, "B", handedOn)
            Assert.Equal(ApprovalStatus.Approved, delegated.Status)
            Assert.Contains($"part of task {auto}", delegated.Reason)

            ' Any other task still asks.
            Dim plain = Await SubmitAsync(host, "A", New NewTaskRequest With {.NextRunUtc = later})
            Assert.Equal(ApprovalStatus.Pending, (Await AskAsync(host, "A", plain)).Status)
            Assert.Equal(ApprovalStatus.Pending, (Await AskAsync(host, "A", Nothing)).Status)
        End Using
    End Function

    <Fact>
    Public Async Function DeadlinesCancelOrMarkLateAsAsked() As Task
        Using host = Await TestHost.StartAsync()
            Dim later = DateTime.UtcNow.AddDays(1)
            Dim passed = DateTime.UtcNow.AddMinutes(-1)
            ' Queued for tomorrow, so they are still unfinished when their deadline passes.
            Dim strict = Await SubmitAsync(host, "A", New NewTaskRequest With {.NextRunUtc = later, .DeadlineUtc = passed, .DeadlineEnforced = True})
            Dim keepsGoing = Await SubmitAsync(host, "A", New NewTaskRequest With {.NextRunUtc = later, .DeadlineUtc = passed, .DeadlineEnforced = True, .RunPastDeadline = True})
            Dim hintOnly = Await SubmitAsync(host, "A", New NewTaskRequest With {.NextRunUtc = later, .DeadlineUtc = passed})

            Await host.App.EnforceTaskDeadlinesAsync()

            Dim s = Await host.H.Tasks.GetAsync(strict)
            Assert.Equal(AgentTaskStatus.Cancelled, s.Status)
            Assert.Contains("Deadline reached", s.ErrorMessage)
            Assert.NotNull(s.DeadlinePassedUtc)
            Dim k = Await host.H.Tasks.GetAsync(keepsGoing)
            Assert.Equal(AgentTaskStatus.Pending, k.Status)
            Assert.NotNull(k.DeadlinePassedUtc)
            Dim h = Await host.H.Tasks.GetAsync(hintOnly)
            Assert.Equal(AgentTaskStatus.Pending, h.Status)
            Assert.Null(h.DeadlinePassedUtc)
            ' Handled once.
            Dim firstMark = k.DeadlinePassedUtc
            Await host.App.EnforceTaskDeadlinesAsync()
            Assert.Equal(firstMark, (Await host.H.Tasks.GetAsync(keepsGoing)).DeadlinePassedUtc)
        End Using
    End Function

    <Theory>
    <InlineData(True)>
    <InlineData(False)>
    Public Async Function GlobalTaskPastItsDeadlineKeepsWorkingOnlyWhenAsked(runPast As Boolean) As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Dim sys = If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
                          If sys.Contains("coordinator of a team") Then
                              Return FakeAIProvider.Text("{""understanding"": ""x"", ""approach"": ""single"", ""steps"": [{""id"": ""s1"", ""agent"": ""A"", ""title"": ""Work"", ""instructions"": ""Do it."", ""dependsOn"": []}]}")
                          End If
                          Return FakeAIProvider.Complete("done")
                      End Function
        p.Delay = Function(r) If(FakeAIProvider.AgentOf(r) = "A", TimeSpan.FromSeconds(30), TimeSpan.Zero)
        Using host = Await TestHost.StartAsync(Nothing, p)
            Dim g = Await host.H.GlobalTasks.CreateAsync("Long job", "Do it.", New GlobalTaskOptions With {.DeadlineMinutes = 5, .RunPastDeadline = runPast, .AutoApproveTools = True}, "admin")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.GlobalTasks.GetAsync(g.Id)).Steps.Any(Function(s) s.TaskId IsNot Nothing)))
            g = Await host.H.GlobalTasks.GetAsync(g.Id)
            ' Its steps carry the run options.
            Assert.True((Await host.H.Tasks.GetAsync(g.Steps(0).TaskId)).AutoApproveTools)
            ' Started 10 minutes ago: past its 5-minute deadline.
            Await host.H.Database.ExecuteAsync("UPDATE GlobalTasks SET StartedUtc=@s WHERE Id=@id", ("@s", CObj(Persistence.Db.Ts(DateTime.UtcNow.AddMinutes(-10)))), ("@id", g.Id))
            host.H.GlobalTasks.Wake()
            If runPast Then
                Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.GlobalTasks.GetAsync(g.Id)).DeadlinePassedUtc.HasValue, 20))
                g = Await host.H.GlobalTasks.GetAsync(g.Id)
                Assert.Equal("Running", g.Steps(0).Status)
                Assert.Null(g.Error)
            Else
                Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.GlobalTasks.GetAsync(g.Id)).Steps(0).Status = "Cancelled", 20))
                g = Await host.H.GlobalTasks.GetAsync(g.Id)
                Assert.Equal("Deadline reached before it finished", g.Steps(0).Error)
                Assert.Null(g.DeadlinePassedUtc)
                ' Its task says why it stopped (not "Cancelled by administrator").
                Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Tasks.GetAsync(g.Steps(0).TaskId)).Status = AgentTaskStatus.Cancelled, 20))
                Assert.StartsWith("Deadline reached", (Await host.H.Tasks.GetAsync(g.Steps(0).TaskId)).ErrorMessage)
            End If
        End Using
    End Function

    <Fact>
    Public Async Function OnlyPeopleWhoAnswerTheInboxMaySkipIt() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "worker", .password = "worker-pass-123", .role = "User",
                                                                                                                         .permissions = {"agents.view", "tasks.manage"}}, admin.Csrf)).StatusCode)
                Dim worker = Await PlatformApiTests.LoginAsync(env.Port, "worker", "worker-pass-123")
                Dim refused = Await PlatformApiTests.Send(worker.Client, "POST", "/api/agents/A/task", New With {.name = "x", .handler = "noop", .autoApproveTools = True}, worker.Csrf)
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode)
                Dim fine = Await PlatformApiTests.Send(worker.Client, "POST", "/api/agents/A/task", New With {.name = "x", .handler = "noop", .delaySeconds = 3600, .deadlineMinutes = 30, .runPastDeadline = True}, worker.Csrf)
                Assert.Equal(HttpStatusCode.Created, fine.StatusCode)
                Dim id = JsonDocument.Parse(Await fine.Content.ReadAsStringAsync()).RootElement.GetProperty("taskId").GetString()
                Dim t = Await env.Host.H.Tasks.GetAsync(id)
                Assert.True(t.DeadlineEnforced)
                Assert.True(t.RunPastDeadline)
                Assert.False(t.AutoApproveTools)
                ' The deadline counts from its start (an hour from now), not from when it was queued.
                Assert.InRange((t.DeadlineUtc.Value - DateTime.UtcNow).TotalMinutes, 85, 95)
                Dim byAdmin = Await PlatformApiTests.Send(admin.Client, "POST", "/api/agents/A/task", New With {.name = "y", .handler = "noop", .delaySeconds = 3600, .autoApproveTools = True}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, byAdmin.StatusCode)
                Assert.True((Await env.Host.H.Tasks.GetAsync(JsonDocument.Parse(Await byAdmin.Content.ReadAsStringAsync()).RootElement.GetProperty("taskId").GetString())).AutoApproveTools)
            End Using
        End Using
    End Function

End Class
