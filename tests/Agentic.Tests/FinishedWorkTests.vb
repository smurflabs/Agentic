Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.AI
Imports Agentic.Communication
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Work that is over stays over: a global task closed by its final review is named as closed to the agents that worked on it
''' (so a routine review does not delegate "follow-up" on it), and a delegated task stops when its delegation expires, also
''' while it runs.
''' </summary>
Public Class FinishedWorkTests

    Private Shared Async Function AddClosedGlobalTaskAsync(h As TestHost, id As String, name As String, reviewJson As String, stepAgent As String) As Task
        Dim now = Global.Agentic.Persistence.Db.Ts(DateTime.UtcNow)
        Await h.H.Database.ExecuteAsync("INSERT INTO GlobalTasks(Id, Name, Description, Status, OptionsJson, CorrelationId, CreatedBy, CreatedUtc, CompletedUtc, ReviewJson) " &
                                        "VALUES (@id, @n, 'd', 'Completed', '{}', @c, 'admin', @t, @t, @r)",
                                        ("@id", CObj(id)), ("@n", name), ("@c", "cor_" & id), ("@t", now), ("@r", reviewJson))
        Await h.H.Database.ExecuteAsync("INSERT INTO GlobalTaskSteps(GlobalTaskId, StepId, Ordinal, AgentId, Title, Instructions, Status) VALUES (@g, 's1', 1, @a, 'Build it', 'i', 'Cancelled')",
                                        ("@g", CObj(id)), ("@a", stepAgent))
    End Function

    <Fact>
    Public Async Function AClosedGlobalTaskIsNamedOnWorkTheAgentTakesUpItself() As Task
        Dim prompts As New Global.System.Collections.Concurrent.ConcurrentDictionary(Of String, String)()
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) = "A" Then
                              Dim sys = If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")
                              Dim said = String.Join(vbLf, r.Messages.Where(Function(m) m.Role = "user").Select(Function(m) m.Content))
                              prompts(If(said.Contains("ADMIN-ASK"), "admin", If(said.Contains("ROUTINE-CHECK"), "scheduled", "other"))) = sys
                          End If
                          Return FakeAIProvider.Complete("ok")
                      End Function
        Using h = Await TestHost.StartAsync(provider:=p)
            Await AddClosedGlobalTaskAsync(h, "gt_ledger", "LedgerLite: build a ledger", "{""verdict"":""Rejected"",""score"":3}", "A")
            Await AddClosedGlobalTaskAsync(h, "gt_good", "Approved work", "{""verdict"":""Approved"",""score"":9}", "A")
            Dim section = Await h.H.GlobalTasks.ClosedSectionAsync("A")
            Assert.Contains("## Closed global tasks", section)
            Assert.Contains("'LedgerLite: build a ledger' (gt_ledger)", section)
            Assert.Contains("final verdict Rejected (3/10)", section)
            Assert.DoesNotContain("gt_good", section)
            ' Only the agents that worked on it are told.
            Assert.Null(Await h.H.GlobalTasks.ClosedSectionAsync("B"))

            ' A routine (scheduled) task carries it; a task the administrator gave does not.
            Dim r = Await h.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Daily review", .Description = "ROUTINE-CHECK of the team", .Kind = TaskKind.Scheduled})
            Assert.True(r.Success, r.Message)
            Await h.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed)
            Dim adminId = Await h.SubmitAsync("A", "Build the ledger", "ADMIN-ASK: build the ledger again")
            Await h.WaitForTaskStatusAsync("A", adminId, AgentTaskStatus.Completed)
            Assert.Contains("## Closed global tasks", prompts("scheduled"))
            Assert.Contains("Do not start, delegate or continue follow-up work", prompts("scheduled"))
            Assert.DoesNotContain("## Closed global tasks", prompts("admin"))
        End Using
    End Function

    <Fact>
    Public Async Function ARunningDelegatedTaskStopsWhenItsDelegationExpires() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Delay = Function(r) If(FakeAIProvider.AgentOf(r) = "B", TimeSpan.FromMilliseconds(300), TimeSpan.Zero)
        ' B is busy and useful, never done.
        p.Responder = Function(r) If(FakeAIProvider.AgentOf(r) = "B", FakeAIProvider.Tool("remember", "{""content"":""note " & Interlocked.Increment(n) & """}"), FakeAIProvider.Text("ok"))
        Using h = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "A").Communication.AllowedRecipients.Add("B"), p)
            Dim ctx As New SendContext With {.TaskId = "tsk_review", .CorrelationId = "cor_review"}
            Dim sent = Await h.H.Comms.DelegateTaskAsync("A", ctx, New OutgoingMessage With {.RecipientAgentId = "B", .TaskName = "Follow-up", .Content = "Keep at it"}, CancellationToken.None)
            Assert.True(sent.Success, sent.ErrorMessage)
            Dim delegatedId = sent.TaskId
            Await h.WaitForTaskStatusAsync("B", delegatedId, AgentTaskStatus.Running, 30)
            ' The delegator stops waiting: the request expires while B is still at work.
            Await h.H.Database.ExecuteAsync("UPDATE AgentMessages SET ExpiresUtc=@t WHERE RelatedTaskId=@id",
                                            ("@t", CObj(Global.Agentic.Persistence.Db.Ts(DateTime.UtcNow.AddMinutes(-1)))), ("@id", delegatedId))
            Dim t = Await h.WaitForTaskStatusAsync("B", delegatedId, AgentTaskStatus.Cancelled, 60)
            Assert.Contains("Delegation expired: A stopped waiting for it", t.ErrorMessage)
        End Using
    End Function

End Class
