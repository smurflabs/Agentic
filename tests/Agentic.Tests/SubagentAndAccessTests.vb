Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports Agentic
Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

Public Class SubagentTests
    Private Shared Function IsSub(r As ChatRequest) As Boolean
        Return If(FakeAIProvider.AgentOf(r), "").Contains(".sub-")
    End Function

    Private Shared Async Function OnlySubagentOfAsync(host As TestHost, parentId As String) As Task(Of SubagentInfo)
        Dim found As SubagentInfo = Nothing
        Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                      Dim list = Await host.H.Agents.ListSubagentsAsync(parentId)
                                                      found = list.FirstOrDefault()
                                                      Return found IsNot Nothing
                                                  End Function), "subagent was not spawned")
        Return found
    End Function

    <Fact>
    Public Async Function SubagentRunsOnLocalModelForFreeAndReturnsResultToParent() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If IsSub(r) Then Return FakeAIProvider.Complete("The answer is 42")
                          Dim last = FakeAIProvider.LastToolResult(r)
                          If last IsNot Nothing AndAlso last.Contains("subagent_result") Then Return FakeAIProvider.Complete("Parent got " & If(last.Contains("42"), "42", "nothing"))
                          Return FakeAIProvider.Tool("spawn_subagent", "{""name"":""calc"",""instructions"":""Compute the answer.""}")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Use a helper")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Equal("Parent got 42", t.GetResult().Summary)
            Dim s = (Await host.H.Agents.ListSubagentsAsync("A")).Single()
            Assert.Equal("Completed", s.Status)
            Assert.Equal(id, s.ParentTaskId)
            Assert.Equal(0, s.SpentMicros)
            ' Local model only: nothing charged to the parent.
            Assert.All(p.Requests, Sub(r) Assert.Equal("local-model", r.ModelId))
            Assert.Equal(0, (Await host.H.Wallets.GetAsync("A")).LifetimeSpendMicros)
            ' The subagent's task is part of the parent's workflow and the subagent is retired afterwards.
            Dim child = Await host.H.Tasks.GetAsync(s.TaskId)
            Assert.Equal(t.CorrelationId, child.CorrelationId)
            Assert.Equal(1, child.DelegationDepth)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(Not host.H.Agents.Exists(s.Id))))
        End Using
    End Function

    <Fact>
    Public Async Function SubagentCloudUseIsChargedToParentWallet() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If IsSub(r) Then Return FakeAIProvider.Complete("done in the cloud")
                          If If(FakeAIProvider.LastToolResult(r), "").Contains("subagent_result") Then Return FakeAIProvider.Complete("ok")
                          Return FakeAIProvider.Tool("spawn_subagent", "{""name"":""cloudy"",""instructions"":""x"",""models"":[""cloud-a""],""budget_limit_usd"":0.05}")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Use a cloud helper")
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Dim s = (Await host.H.Agents.ListSubagentsAsync("A")).Single()
            Assert.Contains(p.Requests, Function(r) IsSub(r) AndAlso r.ModelId = "cloud-a-model")
            ' 1000 prompt tokens x $1/M + 100 completion tokens x $2/M = 1200 micro-USD, from A's wallet.
            Assert.Equal(1200, s.SpentMicros)
            Assert.Equal(1200, (Await host.H.Wallets.GetAsync("A")).LifetimeSpendMicros)
            Dim entry = (Await host.H.Wallets.ListLedgerAsync("A", 20, "AiCharge")).Single()
            Assert.Equal(s.Id, entry.SpentByAgentId)
            Assert.Null(Await host.H.Wallets.GetAsync(s.Id))
        End Using
    End Function

    <Fact>
    Public Async Function SubagentSpendCapBlocksCloudUseAndCancelResumesParent() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If IsSub(r) Then Return FakeAIProvider.Complete("should not happen")
                          Dim last = If(FakeAIProvider.LastToolResult(r), "")
                          If last.Contains("subagent_result") Then Return FakeAIProvider.Complete(If(last.Contains("Cancelled"), "helper was cancelled", "unexpected"))
                          Return FakeAIProvider.Tool("spawn_subagent", "{""name"":""capped"",""instructions"":""x"",""models"":[""cloud-a""],""budget_limit_usd"":0.000001}")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Capped helper")
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Waiting)
            Dim s = Await OnlySubagentOfAsync(host, "A")
            Await host.WaitForTaskStatusAsync(s.Id, s.TaskId, AgentTaskStatus.Retrying)
            Assert.DoesNotContain(p.Requests, Function(r) IsSub(r))
            Assert.Equal(0, (Await host.H.Wallets.GetAsync("A")).LifetimeSpendMicros)
            Assert.True((Await host.H.Agents.CancelSubagentAsync(s.Id, "test")).Success)
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Equal("helper was cancelled", t.GetResult().Summary)
        End Using
    End Function

    <Fact>
    Public Async Function ParentTaskCancellationCancelsItsSubagents() As Task
        Dim p As New FakeAIProvider With {.Delay = Function(r) If(IsSub(r), TimeSpan.FromSeconds(30), TimeSpan.Zero)}
        p.Responder = Function(r) If(IsSub(r), FakeAIProvider.Complete("late"), FakeAIProvider.Tool("spawn_subagent", "{""name"":""slow"",""instructions"":""x""}"))
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim id = Await host.SubmitAsync("A", "Parent")
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Waiting)
            Dim s = Await OnlySubagentOfAsync(host, "A")
            Assert.True((Await host.H.Agents.CancelTaskAsync("A", id)).Success)
            Assert.True(Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Agents.GetSubagentAsync(s.Id)).Status = "Cancelled"), "subagent should be cancelled with its parent task")
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(Not host.H.Agents.Exists(s.Id))))
        End Using
    End Function

    <Fact>
    Public Async Function SubagentsGetRestrictedToolsAndCannotBeMessaged() As Task
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "A").Communication.AllowedRecipients = New List(Of String) From {"B"})
            Dim parent = host.H.Agents.GetEffectiveProfile("A")
            Assert.Contains("spawn_subagent", host.H.ToolCatalog.BuildForAgent(parent).Names())
            Dim sp = ProfileResolver.DeriveSubagent(parent, "A.sub-test01", New SubagentSpec With {.Name = "x", .Models = New List(Of String) From {"local"}, .TimeoutMinutes = 5})
            Dim names = host.H.ToolCatalog.BuildForAgent(sp).Names()
            Assert.Contains("complete_task", names)
            For Each forbidden In {"spawn_subagent", "send_agent_message", "delegate_task", "request_agent_help", "remember", "ask_human", "create_followup_task"}
                Assert.DoesNotContain(forbidden, names)
            Next
            Assert.Equal("A", sp.BillingAgentId)
            ' Models are limited to what the parent handed over.
            Assert.True(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("local"), sp, Nothing))
            Assert.False(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("cloud-a"), sp, Nothing))
        End Using
    End Function

    <Fact>
    Public Async Function SpawnLimitsAreEnforced() As Task
        ' Subagents stay busy so the concurrency limit is observable.
        Dim p As New FakeAIProvider With {.Delay = Function(r) If(IsSub(r), TimeSpan.FromSeconds(30), TimeSpan.Zero)}
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   TestHost.Agent(o, "A").Subagents = New SubagentPolicyOptions With {.MaxConcurrent = 1}
                                                   TestHost.Agent(o, "B").Subagents = New SubagentPolicyOptions With {.MaxConcurrent = 0}
                                               End Sub, p)
            Await host.H.Agents.StopAgentAsync("A")
            Dim req = Function(n As String) New SpawnRequest With {.Name = n, .Instructions = "x"}
            Dim r1 = Await host.H.Agents.SpawnSubagentAsync("A", Nothing, req("one"))
            Assert.True(r1.Success, r1.Message)
            Dim r2 = Await host.H.Agents.SpawnSubagentAsync("A", Nothing, req("two"))
            Assert.Equal("SUBAGENT_LIMIT", r2.Code)
            Assert.Equal("NOT_ALLOWED", (Await host.H.Agents.SpawnSubagentAsync("B", Nothing, req("b"))).Code)
            Assert.Equal("NOT_ALLOWED", (Await host.H.Agents.SpawnSubagentAsync(r1.SubagentId, Nothing, req("nested"))).Code)
            Dim bad = Await host.H.Agents.SpawnSubagentAsync("A", Nothing, New SpawnRequest With {.Name = "m", .Instructions = "x", .Models = New List(Of String) From {"not-a-model"}})
            Assert.False(bad.Success)
            Assert.DoesNotContain(r1.SubagentId, host.H.Agents.GetSnapshots().Select(Function(s) s.Id))
            Assert.Contains(r1.SubagentId, host.H.Agents.GetSnapshots(includeSubagents:=True).Select(Function(s) s.Id))
            Assert.Equal(3, host.H.Agents.AgentCount)
        End Using
    End Function
End Class

Public Class ModelGrantTests
    Private Shared Function SpecialModel(Optional tools As Boolean = False) As ModelOptions
        Return New ModelOptions With {
            .Key = "special", .ModelId = "special-model", .Tier = 5, .MaxTokens = 1000, .MaxConcurrency = 2, .UseFor = "contracts",
            .Capabilities = If(tools, New List(Of ModelCapability) From {ModelCapability.ToolCalling}, New List(Of ModelCapability)),
            .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 1D}}
    End Function

    <Fact>
    Public Async Function CustomModelIsOnlyUsableByGrantedAgentsAndRevocable() As Task
        Dim p As New FakeAIProvider()
        Dim sawGuidance = False
        p.Responder = Function(r)
                          If r.ModelId = "special-model" Then Return FakeAIProvider.Text("Drafted contract text")
                          If r.Messages(0).Content.Contains("legal drafting") Then sawGuidance = True
                          Dim last = If(FakeAIProvider.LastToolResult(r), "")
                          If last.Contains("Drafted contract") Then Return FakeAIProvider.Complete("got the draft")
                          Return FakeAIProvider.Tool("consult_model", "{""model"":""special"",""prompt"":""Draft a contract""}")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim access = host.H.ModelAccess
            Await access.SaveCustomModelAsync(SpecialModel(), "tester", True)
            Dim special = host.H.ModelRegistry.TryGet("special")
            Dim a = host.H.Agents.GetEffectiveProfile("A")
            Dim b = host.H.Agents.GetEffectiveProfile("B")
            Assert.False(host.H.Selection.IsUsableBy(special, a, Nothing), "custom models need an explicit grant")

            Await access.GrantAsync("A", "special", "legal drafting", False, "tester")
            Assert.True(host.H.Selection.IsUsableBy(special, a, Nothing))
            Assert.False(host.H.Selection.IsUsableBy(special, b, Nothing))
            Assert.Contains("consult_model", host.H.ToolCatalog.BuildForAgent(a).Names())
            Assert.DoesNotContain("consult_model", host.H.ToolCatalog.BuildForAgent(b).Names())
            ' Grants are not inherited by subagents unless allowed.
            Assert.DoesNotContain("special", access.SpawnableModels(a))

            Dim id = Await host.SubmitAsync("A", "Draft")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Equal("got the draft", t.GetResult().Summary)
            Assert.True(sawGuidance, "the agent's prompt must say what the granted model is for")
            Dim charge = (Await host.H.Wallets.ListLedgerAsync("A", 20, "AiCharge")).Single()
            Assert.Equal("special", charge.ModelKey)
            Assert.Equal(1100, -charge.AmountMicros)

            Assert.True(Await access.RevokeAsync("A", "special", "tester"))
            Assert.False(host.H.Selection.IsUsableBy(special, a, Nothing))
            Assert.DoesNotContain("consult_model", host.H.ToolCatalog.BuildForAgent(a).Names())
            Assert.Single(Await access.ListHistoryAsync("A"))
        End Using
    End Function

    <Fact>
    Public Async Function SwitchModelContinuesTaskOnGrantedModel() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If r.ModelId = "special-model" Then Return FakeAIProvider.Complete("finished on special")
                          Return FakeAIProvider.Tool("switch_model", "{""model"":""special"",""reason"":""contract work""}")
                      End Function
        Using host = Await TestHost.StartAsync(provider:=p)
            Await host.H.ModelAccess.SaveCustomModelAsync(SpecialModel(tools:=True), "tester", True)
            Await host.H.ModelAccess.GrantAsync("A", "special", "contract work", False, "tester")
            Dim id = Await host.SubmitAsync("A", "Contract")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
            Assert.Equal("finished on special", t.GetResult().Summary)
            Assert.Contains(p.Requests, Function(r) r.ModelId = "special-model" AndAlso r.Tools IsNot Nothing AndAlso r.Tools.Count > 0)
        End Using
    End Function

    <Fact>
    Public Async Function GrantValidationAndSubagentInheritance() As Task
        Using host = Await TestHost.StartAsync(Sub(o) TestHost.Agent(o, "B").AllowedModels = New List(Of String) From {"local"})
            Dim access = host.H.ModelAccess
            Await Assert.ThrowsAsync(Of ModelAccessException)(Function() access.GrantAsync("A", "local", "free stuff", False, "t"))
            Await Assert.ThrowsAsync(Of ModelAccessException)(Function() access.GrantAsync("A", "cloud-a", "already allowed", False, "t"))
            Await Assert.ThrowsAsync(Of ModelAccessException)(Function() access.GrantAsync("B", "cloud-a", "", False, "t"))
            ' A standard cloud model granted to B beyond its profile, inheritable by its subagents.
            Await access.GrantAsync("B", "cloud-b", "hard analysis", True, "t")
            Dim b = host.H.Agents.GetEffectiveProfile("B")
            Assert.True(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("cloud-b"), b, Nothing))
            Assert.Contains("cloud-b", access.SpawnableModels(b))
            Dim sp = ProfileResolver.DeriveSubagent(b, "B.sub-abc123", New SubagentSpec With {.Name = "x", .Models = New List(Of String) From {"local", "cloud-b"}, .TimeoutMinutes = 5})
            Assert.True(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("cloud-b"), sp, Nothing))
            ' Revoking the parent's grant immediately cuts off the subagent too.
            Await access.RevokeAsync("B", "cloud-b", "t")
            Assert.False(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("cloud-b"), sp, Nothing))
        End Using
    End Function
End Class

Public Class QueueOrderTests
    Private Shared Async Function AddAsync(host As TestHost, name As String, pr As PriorityLevel) As Task(Of String)
        Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = name, .Handler = "noop", .Priority = pr})
        Assert.True(r.Success, r.Message)
        Return r.TaskId
    End Function

    Private Shared Async Function OrderAsync(host As TestHost) As Task(Of List(Of String))
        Return (Await host.H.Tasks.ListQueueAsync("A", 100)).Where(Function(t) t.Status = AgentTaskStatus.Pending OrElse t.Status = AgentTaskStatus.Blocked).Select(Function(t) t.Name).ToList()
    End Function

    <Fact>
    Public Async Function QueueFollowsPriorityAndManualOrder() As Task
        Using host = Await TestHost.StartAsync()
            Await host.H.Agents.StopAgentAsync("A")
            Dim t1 = Await AddAsync(host, "n1", PriorityLevel.Normal)
            Dim t2 = Await AddAsync(host, "n2", PriorityLevel.Normal)
            Dim t3 = Await AddAsync(host, "h3", PriorityLevel.High)
            Dim t4 = Await AddAsync(host, "l4", PriorityLevel.Low)
            Assert.Equal({"h3", "n1", "n2", "l4"}, Await OrderAsync(host))

            Assert.True((Await host.H.Agents.MoveTaskAsync("A", t4, Nothing, Nothing, True, False)).Success)
            Assert.Equal({"l4", "h3", "n1", "n2"}, Await OrderAsync(host))
            Assert.True((Await host.H.Agents.MoveTaskAsync("A", t1, t3, Nothing, False, False)).Success)
            Assert.Equal({"l4", "n1", "h3", "n2"}, Await OrderAsync(host))
            Assert.True((Await host.H.Agents.MoveTaskAsync("A", t4, Nothing, t2, False, False)).Success)
            Assert.Equal({"n1", "h3", "n2", "l4"}, Await OrderAsync(host))

            ' A new High task goes after the last task of equal/higher priority in the current order.
            Await AddAsync(host, "h5", PriorityLevel.High)
            Assert.Equal({"n1", "h3", "h5", "n2", "l4"}, Await OrderAsync(host))
            ' Front insertion.
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "urgent", .Handler = "noop", .AddToFront = True})
            Assert.Equal("urgent", (Await OrderAsync(host)).First())

            ' Held tasks keep their place but are skipped.
            Assert.True((Await host.H.Agents.HoldTaskAsync("A", r.TaskId)).Success)
            Assert.Equal("urgent", (Await OrderAsync(host)).First())
            Dim claimed = Await host.H.Tasks.ClaimNextAsync("A", DateTime.UtcNow, "test", DateTime.UtcNow.AddMinutes(5))
            Assert.Equal("n1", claimed.Name)
            Assert.True((Await host.H.Agents.ReleaseTaskAsync("A", r.TaskId)).Success)
            Assert.Equal(AgentTaskStatus.Pending, (Await host.H.Tasks.GetAsync(r.TaskId)).Status)

            ' Editing a queued task; running tasks cannot be moved.
            Assert.True((Await host.H.Agents.EditTaskAsync("A", t2, New TaskEdit With {.Name = "n2-renamed", .Priority = PriorityLevel.Critical})).Success)
            Assert.Equal("n2-renamed", (Await OrderAsync(host)).First())
            Assert.False((Await host.H.Agents.MoveTaskAsync("A", claimed.Id, Nothing, Nothing, True, False)).Success)
            Assert.False((Await host.H.Agents.EditTaskAsync("A", t3, New TaskEdit With {.ModelKey = "nope"})).Success)
        End Using
    End Function

    <Fact>
    Public Async Function BulkActionsAndClone() As Task
        Using host = Await TestHost.StartAsync()
            Await host.H.Agents.StopAgentAsync("A")
            Dim ids As New List(Of String)
            For i = 1 To 3
                ids.Add(Await AddAsync(host, "t" & i, PriorityLevel.Normal))
            Next
            Dim res = Await host.H.Agents.BulkTaskActionAsync("A", "top", {ids(1), ids(2)}, "tester")
            Assert.All(res, Sub(x) Assert.True(x.Success))
            Assert.Equal({"t2", "t3", "t1"}, Await OrderAsync(host))
            Dim cancelled = Await host.H.Agents.BulkTaskActionAsync("A", "cancel", ids, "tester")
            Assert.Equal(3, cancelled.Where(Function(x) x.Success).Count())
            Dim clone = Await host.H.Agents.CloneTaskAsync("A", ids(0), "tester")
            Assert.True(clone.Success)
            Assert.Equal({"t1"}, Await OrderAsync(host))
        End Using
    End Function
End Class

Public Class ManagementApiTests
    Private Shared Async Function SendAsync(c As HttpClient, method As HttpMethod, url As String, csrf As String, Optional body As Object = Nothing) As Task(Of HttpResponseMessage)
        Dim req As New HttpRequestMessage(method, url)
        If body IsNot Nothing Then req.Content = HttpApiTests.JsonBody(body)
        req.Headers.Add("X-CSRF-Token", csrf)
        Return Await c.SendAsync(req)
    End Function

    <Fact>
    Public Async Function CreateAgentWithGrantsQueueAndOperations() As Task
        Dim env = Await HttpApiTests.StartWithHttpAsync()
        Using env.Host
            Using c = env.Client
                Dim login = Await c.PostAsync("/api/auth/login", HttpApiTests.JsonBody(New With {.username = "admin", .password = "pw-123456789"}))
                Dim csrf = JsonDocument.Parse(Await login.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString()

                ' Register a custom model, then create an agent that is granted it with a stated purpose.
                Dim reg = Await SendAsync(c, HttpMethod.Post, "/api/models/custom", csrf, New With {
                    .key = "special", .modelId = "special-model", .tier = 4, .maxTokens = 2000, .useFor = "contracts",
                    .capabilities = {"ToolCalling"}, .pricing = New With {.inputPerMillion = 2, .outputPerMillion = 4}})
                Assert.Equal(HttpStatusCode.Created, reg.StatusCode)
                Dim noPrice = Await SendAsync(c, HttpMethod.Post, "/api/models/custom", csrf, New With {.key = "free", .modelId = "x"})
                Assert.Equal(HttpStatusCode.BadRequest, noPrice.StatusCode)

                Dim save = Await SendAsync(c, HttpMethod.Post, "/api/agents", csrf, New With {
                    .profile = New With {.id = "Drafter", .name = "Drafter", .baseProfileId = "default", .allowedModels = {"local"},
                                         .subagents = New With {.maxConcurrent = 2}},
                    .grants = {New With {.modelKey = "special", .useFor = "legal drafting", .allowSubagents = False}}})
                Assert.Equal(HttpStatusCode.OK, save.StatusCode)
                Dim accessJson = Await c.GetStringAsync("/api/agents/Drafter/model-access")
                Assert.Contains("legal drafting", accessJson)
                Assert.True(env.Host.H.Selection.IsUsableBy(env.Host.H.ModelRegistry.TryGet("special"), env.Host.H.Agents.GetEffectiveProfile("Drafter"), Nothing))
                Assert.False(env.Host.H.Selection.IsUsableBy(env.Host.H.ModelRegistry.TryGet("special"), env.Host.H.Agents.GetEffectiveProfile("A"), Nothing))

                ' Grant/revoke through the per-agent endpoints (works for config-defined agents too).
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Put, "/api/agents/A/model-grants/special", csrf, New With {.useFor = "reviews"})).StatusCode)
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Delete, "/api/agents/A/model-grants/special", csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await SendAsync(c, HttpMethod.Delete, "/api/agents/A/model-grants/special", csrf)).StatusCode)

                ' Definition, editor metadata and detailed agent list.
                Assert.Contains("Drafter", Await c.GetStringAsync("/api/agents/Drafter/definition"))
                Assert.Contains("spawn_subagent", Await c.GetStringAsync("/api/meta/agent-form"))
                Dim list = JsonDocument.Parse(Await c.GetStringAsync("/api/agents?details=1")).RootElement
                Assert.Equal(4, list.GetArrayLength())

                ' Queue management.
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Post, "/api/agents/B/pause", csrf, New With {.x = 1})).StatusCode)
                Await env.Host.H.Agents.StopAgentAsync("B")
                Dim first = JsonDocument.Parse(Await (Await SendAsync(c, HttpMethod.Post, "/api/agents/B/task", csrf, New With {.name = "first", .handler = "noop"})).Content.ReadAsStringAsync()).RootElement.GetProperty("taskId").GetString()
                Dim second = JsonDocument.Parse(Await (Await SendAsync(c, HttpMethod.Post, "/api/agents/B/task", csrf, New With {.name = "second", .handler = "noop"})).Content.ReadAsStringAsync()).RootElement.GetProperty("taskId").GetString()
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Post, $"/api/agents/B/tasks/{second}/move", csrf, New With {.before = first})).StatusCode)
                Dim queue = JsonDocument.Parse(Await c.GetStringAsync("/api/agents/B/tasks?status=queue")).RootElement
                Assert.Equal("second", queue(0).GetProperty("name").GetString())
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, New HttpMethod("PATCH"), $"/api/agents/B/tasks/{first}", csrf, New With {.name = "first-edited", .priority = "High"})).StatusCode)
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Post, $"/api/agents/B/tasks/{first}/hold", csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Conflict, (Await SendAsync(c, HttpMethod.Post, $"/api/agents/B/tasks/{first}/hold", csrf)).StatusCode)
                Dim bulk = Await SendAsync(c, HttpMethod.Post, "/api/agents/B/tasks/bulk", csrf, New With {.action = "cancel", .taskIds = {first, second}})
                Assert.Equal(HttpStatusCode.OK, bulk.StatusCode)
                Assert.Contains("""succeeded"":2", Await bulk.Content.ReadAsStringAsync())
                Assert.Contains("first-edited", Await c.GetStringAsync("/api/tasks?agent=B&q=edited"))

                ' Operations center and host-wide controls.
                Dim ops = JsonDocument.Parse(Await c.GetStringAsync("/api/operations")).RootElement
                Assert.True(ops.TryGetProperty("totals", Nothing))
                Assert.Equal(24, JsonDocument.Parse(Await c.GetStringAsync("/api/stats/hourly?hours=24")).RootElement.GetArrayLength())
                Dim paused = Await SendAsync(c, HttpMethod.Post, "/api/operations/pause-all", csrf)
                Assert.Equal(HttpStatusCode.OK, paused.StatusCode)
                Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(env.Host.H.Agents.GetSnapshot("A").State = "Paused")))
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Post, "/api/operations/resume-all", csrf)).StatusCode)
                Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(env.Host.H.Agents.GetSnapshot("A").State <> "Paused")))
                Assert.Equal(HttpStatusCode.OK, (Await SendAsync(c, HttpMethod.Post, "/api/operations/emergency-stop", csrf)).StatusCode)
                Assert.Contains("emergency-stop", Await c.GetStringAsync("/api/audit"))
            End Using
        End Using
    End Function
End Class
