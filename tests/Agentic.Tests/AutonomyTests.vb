Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The autonomy features: a dependency gate (a task waits until its predecessor completes), an inbound webhook turning into
''' a task, and a standing goal producing a review task on its cadence.
''' </summary>
Public Class AutonomyTests

    <Fact>
    Public Async Function BlockedTaskRunsOnlyAfterItsDependencyCompletes() As Task
        ' No agents running, so nothing claims the tasks out from under the assertions.
        Using host = Await TestHost.StartAsync(Sub(o) o.Agents.Clear())
            Dim tasks = host.H.Tasks
            Dim now = DateTime.UtcNow
            Dim dep As New AgentTask With {.Id = "dep1", .AgentId = "A", .Name = "dependency", .CreatedUtc = now, .UpdatedUtc = now, .CorrelationId = "cor1"}
            Assert.True(Await tasks.InsertAsync(dep))
            Dim child As New AgentTask With {.Id = "child1", .AgentId = "A", .Name = "dependent", .CreatedUtc = now, .UpdatedUtc = now,
                                             .CorrelationId = "cor1", .DependsOn = New List(Of String) From {"dep1"}}
            Assert.True(Await tasks.InsertAsync(child))

            ' The dependent is held as Blocked while its dependency is unfinished.
            Assert.Equal(AgentTaskStatus.Blocked, (Await tasks.GetAsync("child1")).Status)

            ' Completing the dependency releases the dependent back to Pending.
            Assert.True(Await tasks.TryTransitionAsync("dep1", {AgentTaskStatus.Pending}, AgentTaskStatus.Completed, "done"))
            Dim freed = Await tasks.UnblockDependentsAsync("A", "dep1")
            Assert.Contains("child1", freed)
            Assert.Equal(AgentTaskStatus.Pending, (Await tasks.GetAsync("child1")).Status)
        End Using
    End Function

    <Fact>
    Public Async Function WebhookFireCreatesAnEventTriggeredTask() As Task
        Using host = Await TestHost.StartAsync()
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.IsRunning("A"))))
            Dim hook = Await host.H.Triggers.CreateHookAsync("A", "ci", "CI build finished", Nothing, PriorityLevel.Normal, 0, "admin")
            Dim res = Await host.H.Triggers.FireAsync(hook.Token, "{""build"":""green""}")
            Assert.NotNull(res.TaskId)
            Dim t = Await host.H.Tasks.GetAsync(res.TaskId)
            Assert.NotNull(t)
            Assert.Equal(TaskKind.EventTriggered, t.Kind)
            Assert.Contains("green", t.Description)           ' the payload reached the task
            Assert.Contains("untrusted", t.Description)        ' wrapped as untrusted data

            ' An unknown token fires nothing.
            Dim bad = Await host.H.Triggers.FireAsync("not-a-real-token", "x")
            Assert.Null(bad.TaskId)
        End Using
    End Function

    <Fact>
    Public Async Function AStandingGoalProducesAReviewTask() As Task
        ' Keep the background sweep idle; the test drives ReviewDueAsync itself.
        Using host = Await TestHost.StartAsync(Sub(o) o.Autonomy.GoalSweepSeconds = 3600)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.IsRunning("A"))))
            Dim g = Await host.H.Goals.CreateGoalAsync("A", "Grow the library", "Add useful lessons", "lesson count", 60, "admin")
            Dim created = Await host.H.Goals.ReviewDueAsync(DateTime.UtcNow.AddMinutes(1))
            Assert.True(created >= 1)
            ' The review advances the next-review time so it does not fire again immediately.
            Dim reloaded = Await host.H.Goals.GetGoalAsync("A", g.Id)
            Assert.True(reloaded.NextReviewUtc.Value > DateTime.UtcNow)
        End Using
    End Function
End Class
