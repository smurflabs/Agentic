Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports Agentic.Persistence
Imports Agentic.Work
Imports Xunit

''' <summary>
''' A flapping endpoint (DOWN, UP, DOWN within minutes) starts one investigation, not one per flip; and a minute-by-minute
''' health check no longer fills the database for 90 days.
''' </summary>
Public Class HealthCheckAndRetentionTests

    ''' <summary>An endpoint whose status the test flips.</summary>
    Private NotInheritable Class FlipServer
        Implements IDisposable
        Private ReadOnly _listener As New HttpListener()
        Public Property Status As Integer = 200
        Public ReadOnly Property Port As Integer

        Public Sub New()
            Port = HttpApiTests.FreePort()
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/")
            _listener.Start()
            Task.Run(AddressOf LoopAsync)
        End Sub

        Private Async Function LoopAsync() As Task
            While _listener.IsListening
                Try
                    Dim c = Await _listener.GetContextAsync()
                    c.Response.StatusCode = Status
                    c.Response.Close()
                Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                    Return
                End Try
            End While
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _listener.Stop()
                _listener.Close()
            Catch
            End Try
        End Sub
    End Class

    <Fact>
    Public Async Function AFlappingEndpointStartsOneInvestigation() As Task
        Using srv As New FlipServer(), h = Await TestHost.StartAsync()
            Dim payload = $"{{""Url"":""http://127.0.0.1:{srv.Port}/health"",""OnChange"":""reasoning""}}"
            Dim check = Async Function() As Task(Of AgentTask)
                            Dim r = Await h.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Health check", .Handler = "http-check", .Payload = payload})
                            Assert.True(r.Success, r.Message)
                            Return Await h.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed)
                        End Function
            Await check()                                   ' first look: UP
            srv.Status = 500
            Dim down = Await check()                        ' UP → UNEXPECTED_500: an investigation starts
            srv.Status = 200
            Dim up = Await check()                          ' back UP minutes later: recorded, no second investigation
            Assert.Contains("changed from UP", down.GetResult().Summary)
            Assert.Contains("changed from UNEXPECTED_500", up.GetResult().Summary)
            Assert.Contains("no new investigation", up.GetResult().Summary)
            Dim investigations = (Await h.H.Tasks.ListAsync("A", Nothing, 100)).Where(Function(t) t.Name.StartsWith("Investigate state change", StringComparison.Ordinal)).Count()
            Assert.Equal(1, investigations)
        End Using
    End Function

    <Fact>
    Public Async Function FinishedPollingTasksAreKeptOnlyAFewDays() As Task
        Using h = Await TestHost.StartAsync()
            Dim finish = Async Function(kind As TaskKind) As Task(Of String)
                             Dim r = Await h.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = kind.ToString(), .Handler = "noop", .Kind = kind})
                             Await h.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed)
                             Return r.TaskId
                         End Function
            Dim oldPoll = Await finish(TaskKind.Polling)
            Dim newPoll = Await finish(TaskKind.Polling)
            Dim oldWork = Await finish(TaskKind.OneTime)
            Dim fiveDaysAgo = Db.Ts(DateTime.UtcNow.AddDays(-5))
            For Each id In {oldPoll, oldWork}
                Await h.H.Database.ExecuteAsync("UPDATE AgentTasks SET CompletedUtc=@c, UpdatedUtc=@c WHERE Id=@id", ("@c", CObj(fiveDaysAgo)), ("@id", CObj(id)))
            Next
            Dim retention As New RetentionService(h.H.Database, h.Options.Retention, h.H.MemoryStore, h.H.Artifacts, h.H.Log)
            Dim purged = Await retention.RunOnceAsync(DateTime.UtcNow)
            Assert.Equal(1, purged("pollingTasks"))
            Assert.Null(Await h.H.Tasks.GetForAgentAsync("A", oldPoll))        ' a 5-day-old health check is gone
            Assert.NotNull(Await h.H.Tasks.GetForAgentAsync("A", newPoll))     ' a recent one stays
            Assert.NotNull(Await h.H.Tasks.GetForAgentAsync("A", oldWork))     ' real work keeps the 90 days
            Assert.Empty(Await h.H.Tasks.ListHistoryAsync(oldPoll))            ' and the history of the purged task went with it
        End Using
    End Function

End Class
