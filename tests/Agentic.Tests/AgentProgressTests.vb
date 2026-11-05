Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Autonomous agents are not stopped by a turn count: Tasks.MaxReasoningTurns is how often progress is checked, a task still
''' getting things done goes on, one that is stuck stops with a report, and a long task gives way after its time slice
''' (Tasks.MaxTaskDurationSeconds) and goes on from its checkpoint instead of failing the attempt.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class AgentProgressTests

    Private Shared Function LastUser(r As ChatRequest) As String
        Return If(r.Messages.LastOrDefault(Function(m) m.Role = "user")?.Content, "")
    End Function

    Private Shared Function Said(r As ChatRequest, text As String) As Boolean
        Return r.Messages.Any(Function(m) m.Role = "user" AndAlso If(m.Content, "").Contains(text, StringComparison.Ordinal))
    End Function

    <Fact>
    Public Async Function AnAgentThatKeepsGettingThingsDoneIsNotStoppedByATurnCount() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          ' Ten useful steps (each saves something new), far more than 3 turns, then the result.
                          If Interlocked.Increment(n) <= 10 Then Return FakeAIProvider.Tool("remember", "{""content"":""fact " & n & """}")
                          Return FakeAIProvider.Complete("Saved 10 facts.")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o)
                                                o.Routing.Escalation.OnTurnLimit = False
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 3, .MaxToolCalls = 40, .MaxRetries = 0}
                                            End Sub, p)
            Dim id = Await h.SubmitAsync("A", "Save the facts")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.True(t.GetResult().Success)
            Assert.Equal("Saved 10 facts.", t.GetResult().Summary)
            ' It passed progress checks on the way instead of hitting a limit.
            Dim last = p.Requests.ToArray().Last(Function(r) FakeAIProvider.AgentOf(r) = "A")
            Assert.True(last.Messages.Where(Function(m) m.Role = "user" AndAlso If(m.Content, "").Contains("[host] Progress check after")).Count() >= 3)
        End Using
    End Function

    <Fact>
    Public Async Function AnAgentThatStopsMakingProgressEndsWithAReport() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If LastUser(r).Contains("stopped making progress") Then Return FakeAIProvider.Complete("Searched 8 ways; nothing found.", success:=False)
                          ' Different searches that all find nothing: only the first result is new.
                          Return FakeAIProvider.Tool("recall", "{""query"":""lost key " & Interlocked.Increment(n) & """}")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o)
                                                o.Routing.Escalation.OnTurnLimit = False
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 4, .MaxToolCalls = 40, .MaxRetries = 0}
                                            End Sub, p)
            Dim id = Await h.SubmitAsync("A", "Find the lost key")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Failed)
            Dim result = t.GetResult()
            Assert.False(result.Success)
            Assert.Contains("Stopped after 8 turns without completing the task: no progress in the last 4 turns", result.Summary)
            Assert.Contains("Report of the work so far", result.Summary)
            Assert.Contains("Searched 8 ways; nothing found.", result.Summary)
            Assert.Equal(8, n)
        End Using
    End Function

    <Fact>
    Public Async Function AnAgentThatOnlyLooksAroundIsStuckAfterAFewChecks() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If LastUser(r).Contains("This task stops here") Then Return FakeAIProvider.Complete("Read a lot, changed nothing.", success:=False)
                          ' Every result is new (a re-worded plan), but nothing is ever changed, saved or finished.
                          Return FakeAIProvider.Tool("update_plan", "{""steps"":[{""step"":""look again " & Interlocked.Increment(n) & """,""status"":""in_progress""}]}")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o)
                                                o.Routing.Escalation.OnTurnLimit = False
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 3, .MaxToolCalls = 40, .MaxRetries = 0}
                                            End Sub, p)
            Dim id = Await h.SubmitAsync("A", "Fix the bug")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Failed)
            Dim result = t.GetResult()
            Assert.Contains($"it has only been reading for {ReasoningTaskHandler.MaxLookingStretches} progress checks in a row", result.Summary)
            Assert.Contains("Read a lot, changed nothing.", result.Summary)
            Assert.Equal(3 * ReasoningTaskHandler.MaxLookingStretches, n)
        End Using
    End Function

    <Fact>
    Public Async Function RefusedRepeatsThatOutweighNewResultsAreNotProgress() As Task
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If LastUser(r).Contains("This task stops here") Then Return FakeAIProvider.Complete("Went in circles.", success:=False)
                          ' A new fact, then the same plan again (refused from its third time on), and so on.
                          Dim k = Interlocked.Increment(n)
                          If k Mod 2 = 1 Then Return FakeAIProvider.Tool("remember", "{""content"":""fact " & k & """}")
                          Return FakeAIProvider.Tool("update_plan", "{""steps"":[{""step"":""save the facts"",""status"":""in_progress""}]}")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o)
                                                o.Routing.Escalation.OnTurnLimit = False
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 6, .MaxToolCalls = 40, .MaxRetries = 0}
                                            End Sub, p)
            Dim id = Await h.SubmitAsync("A", "Save the facts")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Failed)
            Dim result = t.GetResult()
            Assert.Contains("3 repeated calls were refused against 3 new results in the last 6 turns", result.Summary)
            Assert.Equal(12, n)
        End Using
    End Function

    ''' <summary>The agent's notes task: a second per step, 11 notes, then done (the "other" task finishes at once).</summary>
    Private Shared Function NotesProvider() As FakeAIProvider
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Delay = Function(r) If(FakeAIProvider.AgentOf(r) = "A", TimeSpan.FromSeconds(1), TimeSpan.Zero)
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If String.Join(" ", r.Messages.Select(Function(m) m.Content)).Contains("Other work") Then Return FakeAIProvider.Complete("Other done.")
                          If Interlocked.Increment(n) <= 11 Then Return FakeAIProvider.Tool("remember", "{""content"":""note " & n & """}")
                          Return FakeAIProvider.Complete("All 11 notes saved.")
                      End Function
        Return p
    End Function

    <Fact>
    Public Async Function ALongTaskGivesWayAfterItsTimeSliceWhenAnotherTaskWaits() As Task
        ' Each step takes a second and the time slice is 10 s; another task of the agent is waiting, so it gives way to it once.
        Using h = Await TestHost.StartAsync(Sub(o)
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 50, .MaxToolCalls = 100, .MaxTaskDurationSeconds = 10, .MaxRetries = 0}
                                            End Sub, NotesProvider())
            Dim id = Await h.SubmitAsync("A", "Save the notes")
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await h.H.Tasks.GetAsync(id)).Status = AgentTaskStatus.Running))
            Dim other = Await h.SubmitAsync("A", "Other work")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed, 90)
            Assert.Equal("All 11 notes saved.", t.GetResult().Summary)
            ' Not a failed attempt: no retry was used (with MaxRetries 0 a timeout would have failed the task).
            Assert.Equal(0, t.RetryCount)
            Dim history = Await h.H.Tasks.ListHistoryAsync(id)
            Assert.Contains(history, Function(x) x.Values.Any(Function(v) If(v?.ToString(), "").Contains("it goes on from its checkpoint")))
            ' The other task ran in the meantime.
            Dim o = Await h.H.Tasks.GetAsync(other)
            Assert.Equal(AgentTaskStatus.Completed, o.Status)
            Assert.True(o.CompletedUtc.Value < t.CompletedUtc.Value)
        End Using
    End Function

    <Fact>
    Public Async Function ALongTaskWithNothingElseQueuedKeepsWorking() As Task
        ' The same task alone: its slice starts over instead of a pause and resume (audit 3).
        Using h = Await TestHost.StartAsync(Sub(o)
                                                TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 50, .MaxToolCalls = 100, .MaxTaskDurationSeconds = 10, .MaxRetries = 0}
                                            End Sub, NotesProvider())
            Dim id = Await h.SubmitAsync("A", "Save the notes")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed, 90)
            Assert.Equal("All 11 notes saved.", t.GetResult().Summary)
            Assert.Equal(0, t.RetryCount)
            Dim history = Await h.H.Tasks.ListHistoryAsync(id)
            Assert.DoesNotContain(history, Function(x) x.Values.Any(Function(v) If(v?.ToString(), "").Contains("it goes on from its checkpoint")))
        End Using
    End Function

    <Fact>
    Public Async Function AnAiOutageIsWaitedOutWithoutUsingUpRetries() As Task
        Dim downSince As DateTime? = Nothing
        Dim gate As New Object()
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          ' Every model is down for half a second from the first call (so the task's first attempt fails on all
                          ' of them, while no model fails often enough to trip its circuit breaker).
                          SyncLock gate
                              downSince = If(downSince, DateTime.UtcNow)
                              If DateTime.UtcNow - downSince.Value < TimeSpan.FromMilliseconds(500) Then Throw New AiException(AiErrorKind.Transient, "Connection error")
                          End SyncLock
                          Return FakeAIProvider.Complete("Done once the model was back.")
                      End Function
        Dim stepBefore = ReasoningTaskHandler.AiWaitStep
        ReasoningTaskHandler.AiWaitStep = TimeSpan.FromSeconds(1)
        Try
            Using h = Await TestHost.StartAsync(Sub(o)
                                                    o.Routing.Escalation.OnModelUnavailable = False
                                                    o.AIProviders("OpenWebUI").MaxRetries = 0
                                                    TestHost.Agent(o, "A").Tasks = New TaskPolicyOptions With {.MaxRetries = 0}
                                                End Sub, p)
                Dim id = Await h.SubmitAsync("A", "Needs the model")
                ' With no retries left a counted failure would end the task; waiting for the AI does not count.
                Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed, 90)
                Assert.Equal("Done once the model was back.", t.GetResult().Summary)
                Assert.Equal(0, t.RetryCount)
                Dim history = Await h.H.Tasks.ListHistoryAsync(id)
                Assert.Contains(history, Function(x) x.Values.Any(Function(v) If(v?.ToString(), "").Contains("Waiting for the AI")))
            End Using
        Finally
            ReasoningTaskHandler.AiWaitStep = stepBefore
        End Try
    End Function

    <Fact>
    Public Sub AProxysConnectionErrorIsAnOutageNotABadRequest()
        Dim body = "{""error"":{""message"":""litellm.InternalServerError: InternalServerError: OpenAIException - Connection error.No fallback model group found for original model_group=FLINT.""}}"
        Assert.Equal(AiErrorKind.Transient, AiErrorClassifier.FromHttp(400, body).Kind)
        ' A real bad request stays one.
        Assert.Equal(AiErrorKind.InvalidRequest, AiErrorClassifier.FromHttp(400, "{""error"":{""message"":""Unterminated string in JSON at position 120""}}").Kind)
    End Sub

End Class
