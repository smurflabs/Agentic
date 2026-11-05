Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Mcp
Imports Agentic.Work
Imports Xunit

''' <summary>
''' What the LedgerLite audit (2026-10-30) asked for: a provider out of credit is paused and work moves to other models and
''' tiers; deadline cancellations say so and keep what was done; agent requests wait long enough; a sandbox that stops
''' answering is reset and fails fast meanwhile; plans where one step holds up everything are sent back.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class ResilienceTests

    Private Shared Function OutOfCredit(model As String) As AiException
        Return New AiException(AiErrorKind.ModelUnavailable, $"HTTP 400: 预扣费额度失败, 用户剩余额度: $0.02 ({model})", 400) With {.OutOfCredit = True}
    End Function

    <Fact>
    Public Sub OutOfCreditErrorsAreRecognized()
        Dim e = AiErrorClassifier.FromHttp(400, "{""detail"":""litellm.APIError: OpenAIException - 预扣费额度失败, 用户剩余额度: ＄0.021100, 需要预扣费额度: ＄0.031764""}")
        Assert.True(e.OutOfCredit)
        Assert.Equal(AiErrorKind.ModelUnavailable, e.Kind)
        Assert.True(AiErrorClassifier.FromHttp(429, "{""error"":{""code"":""insufficient_quota"",""message"":""You exceeded your current quota""}}").OutOfCredit)
        Assert.False(AiErrorClassifier.FromHttp(503, "upstream connect error").OutOfCredit)
    End Sub

    <Fact>
    Public Async Function AProviderOutOfCreditIsPausedAndItsFallbackOnAnotherProviderAnswers() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If r.ModelId.StartsWith("broke/", StringComparison.Ordinal) Then Throw OutOfCredit(r.ModelId)
                          Return FakeAIProvider.Text("hi from " & r.ModelId)
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   Dim a = o.Models.First(Function(m) m.Key = "cloud-a")
                                                   a.ModelId = "broke/a"
                                                   o.Models.Add(New ModelOptions With {.Key = "cloud-a-same", .ModelId = "broke/a2", .Tier = 2, .MaxConcurrency = 4,
                                                       .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000,
                                                       .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 1D}})
                                                   o.Models.Add(New ModelOptions With {.Key = "cloud-a-other", .ModelId = "fine/a", .Tier = 2, .MaxConcurrency = 4,
                                                       .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000,
                                                       .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 1D}})
                                                   a.Fallbacks = New List(Of String) From {"cloud-a-same", "cloud-a-other"}
                                               End Sub, p)
            Dim profile = host.H.Profiles.TryGetProfile("A")
            Dim outcome = Await host.H.Gateway.ChatAsync(New AiCallContext With {.AgentId = "A", .Profile = profile, .LadderLevel = 1},
                                                         New List(Of ChatMessage) From {ChatMessage.ForUser("hi")}, Nothing, CancellationToken.None)
            Assert.Equal("cloud-a-other", outcome.ModelKey)
            ' The whole provider is paused: its other model was never called, and both circuits are open for the pause.
            Assert.DoesNotContain(p.Requests, Function(r) r.ModelId = "broke/a2")
            Assert.Equal(CircuitState.Open, host.H.ModelRegistry.TryGet("cloud-a").Breaker.State)
            Assert.Equal(CircuitState.Open, host.H.ModelRegistry.TryGet("cloud-a-same").Breaker.State)
            Assert.Equal(CircuitState.Closed, host.H.ModelRegistry.TryGet("cloud-a-other").Breaker.State)
        End Using
    End Function

    <Fact>
    Public Async Function ATaskStuckOnAProviderOutOfCreditMovesToAnotherTier() As Task
        Dim p As New FakeAIProvider()
        ' The top tier's provider has no credit; the others work.
        p.Responder = Function(r)
                          If r.ModelId = "cloud-b-model" Then Throw OutOfCredit(r.ModelId)
                          Return FakeAIProvider.Complete("done on " & r.ModelId)
                      End Function
        Using host = Await TestHost.StartAsync(Nothing, p)
            ' Start it at the top tier, as an escalated task would be.
            Dim r = Await host.H.Agents.SubmitTaskAsync("B", New NewTaskRequest With {.Name = "escalated job", .Description = "do it", .Handler = "reasoning", .StartTier = 3})
            Assert.True(r.Success, r.Message)
            Dim done = Await host.WaitForTaskStatusAsync("B", r.TaskId, AgentTaskStatus.Completed, 60)
            Assert.True(done.CurrentLadderLevel < 2, $"ladder level {done.CurrentLadderLevel}")
            Assert.Contains(p.Requests, Function(x) x.ModelId = "cloud-b-model")
            Assert.Equal(CircuitState.Open, host.H.ModelRegistry.TryGet("cloud-b").Breaker.State)
        End Using
    End Function

    <Fact>
    Public Sub UnfinishedStepsKeepWhatTheyDid()
        Dim done As New GlobalTaskStep With {.StepId = "s1", .Status = "Completed", .Result = "all good"}
        Dim partialStep As New GlobalTaskStep With {.StepId = "s2", .Status = "Cancelled", .Error = "Deadline reached before it finished", .Result = "Files it wrote (2): a.py, b.py"}
        Dim never As New GlobalTaskStep With {.StepId = "s3", .Status = "Cancelled", .Error = "Deadline reached before it could start"}
        Assert.Equal("all good", GlobalTaskService.StepOutcomeText(done, "No result"))
        Assert.StartsWith("UNFINISHED (Deadline reached before it finished)", GlobalTaskService.StepOutcomeText(partialStep, "No result"))
        Assert.Contains("a.py, b.py", GlobalTaskService.StepOutcomeText(partialStep, "No result"))
        Assert.Equal("No result: Deadline reached before it could start", GlobalTaskService.StepOutcomeText(never, "No result"))
    End Sub

    <Fact>
    Public Async Function AgentRequestsWaitAtLeastAnHour() As Task
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "A").Communication.AllowedRecipients = New List(Of String) From {"B"})
            Dim before = DateTime.UtcNow
            Dim r = Await host.H.Comms.SendRequestAsync("A", New SendContext(), New OutgoingMessage With {
                .RecipientAgentId = "B", .MessageType = MessageType.Request, .Subject = "git access?", .Content = "How do I push?", .TimeoutSeconds = 180}, CancellationToken.None)
            Assert.True(r.Success, r.ErrorMessage)
            Assert.True(r.ExpiresUtc.Value >= before.AddMinutes(59), $"expires {r.ExpiresUtc}")
        End Using
    End Function

    ''' <summary>A fake MCP server whose calls hang while Hang is set.</summary>
    Private NotInheritable Class HangingMcpFactory
        Implements IMcpClientFactory
        Public Hang As Boolean
        Public Created As Integer
        Public Function Create(server As McpServerOptions) As IMcpClient Implements IMcpClientFactory.Create
            Interlocked.Increment(Created)
            Return New Client(Me)
        End Function
        Private NotInheritable Class Client
            Implements IMcpClient
            Private ReadOnly _f As HangingMcpFactory
            Public Sub New(f As HangingMcpFactory)
                _f = f
            End Sub
            Public Function ConnectAsync(ct As CancellationToken) As Task Implements IMcpClient.ConnectAsync
                Return Task.CompletedTask
            End Function
            Public Function ListToolsAsync(ct As CancellationToken) As Task(Of List(Of McpToolInfo)) Implements IMcpClient.ListToolsAsync
                Return Task.FromResult(New List(Of McpToolInfo) From {New McpToolInfo With {.Name = "run_command", .Description = "run", .InputSchemaJson = "{""type"":""object""}"}})
            End Function
            Public Async Function CallToolAsync(name As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult) Implements IMcpClient.CallToolAsync
                If _f.Hang Then Await Task.Delay(Timeout.Infinite, ct)
                Return New McpCallResult With {.Text = "ok"}
            End Function
            Public Function PingAsync(ct As CancellationToken) As Task Implements IMcpClient.PingAsync
                Return Task.CompletedTask
            End Function
            Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
                Return ValueTask.CompletedTask
            End Function
        End Class
    End Class

    <Fact>
    Public Async Function ASandboxThatStopsAnsweringIsResetAndFailsFastMeanwhile() As Task
        Dim f As New HangingMcpFactory()
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.McpServers.Add(New McpServerOptions With {
                                                       .Id = "sbx", .Name = "Sandbox", .Transport = McpTransportKind.Http, .Endpoint = "http://sandbox.local/mcp", .TimeoutSeconds = 1,
                                                       .ReconnectBaseSeconds = 1, .ReconnectMaxSeconds = 1,
                                                       .HealthCheck = New McpHealthCheckOptions With {
                                                           .Tool = "run_command", .Arguments = New Dictionary(Of String, String) From {{"command", "echo ok"}}, .Expect = "ok",
                                                           .IntervalSeconds = 60, .DownIntervalSeconds = 1, .TimeoutSeconds = 1, .FailuresBeforeRestart = 2, .TimeoutsBeforeCheck = 1}})
                                               End Sub, mcpFactory:=f)
            Dim mcp = host.H.Mcp
            Dim args = JsonDocument.Parse("{""command"":""echo hi""}").RootElement
            Assert.Equal("ok", (Await mcp.CallToolAsync("sbx", "run_command", args, CancellationToken.None)).Text)
            Dim createdBefore = f.Created

            f.Hang = True
            Await Assert.ThrowsAsync(Of TimeoutException)(Function() mcp.CallToolAsync("sbx", "run_command", args, CancellationToken.None))
            ' The watchdog notices, resets the connection, and calls fail at once (not after the 1 s timeout) meanwhile.
            Dim fastFailure As Exception = Nothing
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          Dim sw = Diagnostics.Stopwatch.StartNew()
                                                          Try
                                                              Await mcp.CallToolAsync("sbx", "run_command", args, CancellationToken.None)
                                                          Catch ex As McpUnavailableException When ex.Message.Contains("not responding")
                                                              fastFailure = ex
                                                              Return sw.ElapsedMilliseconds < 500
                                                          Catch ex As Exception
                                                          End Try
                                                          Return False
                                                      End Function, 30), "calls never failed fast")
            Assert.True(f.Created > createdBefore, "the connection was not reset")

            ' It answers again: the watchdog sees it and calls work.
            f.Hang = False
            Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                          Try
                                                              Return (Await mcp.CallToolAsync("sbx", "run_command", args, CancellationToken.None)).Text = "ok"
                                                          Catch ex As Exception
                                                              Return False
                                                          End Try
                                                      End Function, 30), "calls did not work again")
        End Using
    End Function

    ''' <summary>A fake sandbox whose commands never finish (run_command answers "running") while Stuck is set; it records kills.</summary>
    Private NotInheritable Class StuckSandboxFactory
        Implements IMcpClientFactory
        Public Stuck As Boolean
        Public ReadOnly Killed As New Concurrent.ConcurrentBag(Of String)
        Private _n As Integer
        Public Function Create(server As McpServerOptions) As IMcpClient Implements IMcpClientFactory.Create
            Return New Client(Me)
        End Function
        Private NotInheritable Class Client
            Implements IMcpClient
            Private ReadOnly _f As StuckSandboxFactory
            Public Sub New(f As StuckSandboxFactory)
                _f = f
            End Sub
            Public Function ConnectAsync(ct As CancellationToken) As Task Implements IMcpClient.ConnectAsync
                Return Task.CompletedTask
            End Function
            Public Function ListToolsAsync(ct As CancellationToken) As Task(Of List(Of McpToolInfo)) Implements IMcpClient.ListToolsAsync
                Return Task.FromResult(New List(Of McpToolInfo) From {
                    New McpToolInfo With {.Name = "run_command", .InputSchemaJson = "{""type"":""object"",""properties"":{""command"":{""type"":""string""},""wait"":{""type"":""number""},""env"":{""type"":""object""}}}"},
                    New McpToolInfo With {.Name = "kill_process", .InputSchemaJson = "{""type"":""object"",""properties"":{""process_id"":{""type"":""string""},""force"":{""type"":""boolean""}}}"}})
            End Function
            Public Function CallToolAsync(name As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult) Implements IMcpClient.CallToolAsync
                If name = "kill_process" Then
                    _f.Killed.Add(arguments.GetProperty("process_id").GetString())
                    Return Task.FromResult(New McpCallResult With {.Text = "{""status"":""killed""}"})
                End If
                Dim id = "20261102-0816-" & Interlocked.Increment(_f._n).ToString("000000")
                If _f.Stuck Then Return Task.FromResult(New McpCallResult With {.Text = $"{{""id"":""{id}"",""status"":""running"",""exit_code"":null,""output"":[]}}"})
                Return Task.FromResult(New McpCallResult With {.Text = $"{{""id"":""{id}"",""status"":""done"",""exit_code"":0,""output"":[{{""type"":""output"",""data"":""ok\n""}}]}}"})
            End Function
            Public Function PingAsync(ct As CancellationToken) As Task Implements IMcpClient.PingAsync
                Return Task.CompletedTask
            End Function
            Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
                Return ValueTask.CompletedTask
            End Function
        End Class
    End Class

    ''' <summary>
    ''' Production 2026-11-01/02: git commands stuck in a pager stopped Open Terminal from finishing any command; every "echo ok"
    ''' check then stayed running too (997 of them) until the sandbox ran out of files. A check that never finished is stopped,
    ''' the server counts as not responding, and its status says since when (chats leave its tools out meanwhile).
    ''' </summary>
    <Fact>
    Public Async Function ACheckThatNeverFinishesIsStoppedAndTheSandboxCountsAsDown() As Task
        Dim f As New StuckSandboxFactory()
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.McpServers.Add(New McpServerOptions With {
                                                       .Id = "sbx", .Name = "Sandbox", .Transport = McpTransportKind.Http, .Endpoint = "http://sandbox.local/mcp", .TimeoutSeconds = 5,
                                                       .ReconnectBaseSeconds = 1, .ReconnectMaxSeconds = 1,
                                                       .HealthCheck = New McpHealthCheckOptions With {
                                                           .Tool = "run_command", .Arguments = New Dictionary(Of String, String) From {{"command", "echo ok"}, {"wait", "1"}}, .Expect = "ok",
                                                           .StopTool = "kill_process", .IntervalSeconds = 1, .DownIntervalSeconds = 1, .DownMaxIntervalSeconds = 2,
                                                           .TimeoutSeconds = 2, .FailuresBeforeRestart = 2, .TimeoutsBeforeCheck = 1}})
                                               End Sub, mcpFactory:=f)
            Dim mcp = host.H.Mcp
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(mcp.GetTools("sbx").Count = 2), 10), "never connected")
            f.Stuck = True
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(mcp.GetStatus().Single(Function(s) s.Id = "sbx").DownSinceUtc.HasValue), 30), "never counted as down")
            Assert.True(f.Killed.Count >= 2, $"stuck checks were not stopped ({f.Killed.Count})")
            Assert.All(f.Killed, Sub(id) Assert.StartsWith("20261102-0816-", id))
            Await Assert.ThrowsAsync(Of McpUnavailableException)(Function() mcp.CallToolAsync("sbx", "run_command", JsonDocument.Parse("{""command"":""ls""}").RootElement, CancellationToken.None))

            ' Commands finish again: it is back.
            f.Stuck = False
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(Not mcp.GetStatus().Single(Function(s) s.Id = "sbx").DownSinceUtc.HasValue), 30), "never came back")
        End Using
    End Function

    <Fact>
    Public Sub PlansWhereOneStepHoldsUpEverythingAreSentBack()
        Dim stepOf = Function(id As String, deps As String()) New GlobalTaskStep With {.StepId = id, .AgentId = "A", .Title = id, .Instructions = "x", .DependsOn = deps.ToList()}
        ' The LedgerLite plan: research, then one big implementation step every other step waits for.
        Dim gated As New GlobalTaskService.PlanResult()
        gated.Steps.AddRange({stepOf("s1", {}), stepOf("s2", {"s1"}), stepOf("s3", {"s2"}), stepOf("s4", {"s2"}), stepOf("s5", {"s2"}), stepOf("s6", {"s3", "s4", "s5"})})
        Dim problem = GlobalTaskService.GatingProblem(gated)
        Assert.NotNull(problem)
        Assert.Contains("step s1", problem) ' s1 holds up all five others (through s2)
        ' Independent work starts at once; only the integration step waits.
        Dim parallel As New GlobalTaskService.PlanResult()
        parallel.Steps.AddRange({stepOf("s1", {}), stepOf("s2", {}), stepOf("s3", {}), stepOf("s4", {}), stepOf("s5", {"s2"}), stepOf("s6", {"s1", "s5"})})
        Assert.Null(GlobalTaskService.GatingProblem(parallel))
        ' Small plans are left alone.
        Dim small As New GlobalTaskService.PlanResult()
        small.Steps.AddRange({stepOf("s1", {}), stepOf("s2", {"s1"}), stepOf("s3", {"s2"})})
        Assert.Null(GlobalTaskService.GatingProblem(small))
    End Sub

End Class
