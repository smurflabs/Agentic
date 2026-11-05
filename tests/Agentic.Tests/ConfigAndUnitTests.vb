Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Reflection
Imports Agentic
Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Mcp
Imports Agentic.Security
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Xunit

Public Class ConfigValidationTests
    Private Shared Function Valid() As HostOptions
        Return TestHost.DefaultOptions(Path.GetTempPath())
    End Function

    <Fact>
    Public Sub DefaultTestConfigIsValid()
        Dim r = ConfigValidator.Validate(Valid())
        Assert.True(r.IsValid, String.Join("; ", r.Errors))
    End Sub

    <Fact>
    Public Sub ShippedAppSettingsIsValid()
        Dim path = IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json")
        Assert.True(File.Exists(path), "appsettings.json should be copied to the test output")
        Dim loaded = ConfigLoader.Load(path)
        Dim r = ConfigValidator.Validate(loaded.Options, requireApiKey:=False)
        Assert.True(r.IsValid, String.Join("; ", r.Errors))
        Assert.Equal(8, loaded.Options.Agents.Count)
        ' Every MCP server must be usable by at least two agents so no tool has a single point of failure.
        For Each server In loaded.Options.McpServers.Where(Function(s) s.Enabled)
            Dim users = loaded.Options.Agents.Where(Function(a) a.AllowedMcpServers IsNot Nothing AndAlso a.AllowedMcpServers.Contains(server.Id)).Count()
            Assert.True(users >= 2, $"MCP server '{server.Id}' is only allowed for {users} agent(s).")
        Next
        Assert.Contains(loaded.Options.Models, Function(m) m.ModelId = "REAPER-40B-A3B" AndAlso m.IsLocal AndAlso m.MaxConcurrency = 4)
        ' A1 has 4 slots too: chat requests to it queue for a slot instead of overloading it.
        Assert.Contains(loaded.Options.Models, Function(m) m.ModelId = "Agents-A1-35B-VISION" AndAlso m.IsLocal AndAlso m.MaxConcurrency = 4)
    End Sub

    <Fact>
    Public Sub DetectsDuplicateAgentIds()
        Dim o = Valid()
        o.Agents.Add(New ProfileOptions With {.Id = "A", .BaseProfileId = "default"})
        Assert.Contains(ConfigValidator.Validate(o).Errors, Function(e) e.Contains("Duplicate agent id 'A'"))
    End Sub

    <Fact>
    Public Sub DetectsInvalidRecipient()
        Dim o = Valid()
        TestHost.Agent(o, "A").Communication.AllowedRecipients.Add("Ghost")
        Assert.Contains(ConfigValidator.Validate(o).Errors, Function(e) e.Contains("unknown recipient 'Ghost'"))
    End Sub

    <Fact>
    Public Sub DetectsUnknownModelAndImpossiblePolicy()
        Dim o = Valid()
        TestHost.Agent(o, "A").AllowedModels = New List(Of String) From {"nope"}
        TestHost.Agent(o, "B").AllowedModels = New List(Of String) From {"cloud-b"}
        TestHost.Agent(o, "B").MaxTier = 1
        Dim errors = ConfigValidator.Validate(o).Errors
        Assert.Contains(errors, Function(e) e.Contains("unknown model 'nope'"))
        Assert.Contains(errors, Function(e) e.Contains("Agent 'B'") AndAlso e.Contains("impossible model policy"))
    End Sub

    <Fact>
    Public Sub DetectsInvalidMcpReference()
        Dim o = Valid()
        TestHost.Agent(o, "A").AllowedMcpServers = New List(Of String) From {"missing-server"}
        Assert.Contains(ConfigValidator.Validate(o).Errors, Function(e) e.Contains("unknown MCP server 'missing-server'"))
    End Sub

    <Fact>
    Public Sub MissingApiKeyIsAnError()
        Dim o = Valid()
        o.AIProviders("OpenWebUI").ApiKey = ""
        Assert.Contains(ConfigValidator.Validate(o).Errors, Function(e) e.Contains("ApiKey is not set"))
    End Sub

    <Fact>
    Public Sub UnpricedCloudModelIsWarnedAndUnusable()
        Dim o = Valid()
        o.Models.First(Function(m) m.Key = "cloud-a").Pricing = New PricingOptions()
        Dim r = ConfigValidator.Validate(o)
        Assert.True(r.IsValid)
        Assert.Contains(r.Warnings, Function(w) w.Contains("'cloud-a' has no Pricing"))
    End Sub

    <Fact>
    Public Sub PlaintextPasswordRejected()
        Dim o = Valid()
        o.Server.Enabled = True
        o.Admin.Users.Add(New AdminUserOptions With {.Username = "x", .PasswordHash = "secret"})
        Assert.Contains(ConfigValidator.Validate(o).Errors, Function(e) e.Contains("not a PBKDF2 hash"))
    End Sub

    <Fact>
    Public Sub ProfileInheritanceMergesBaseAndOverrides()
        Dim o = Valid()
        TestHost.Agent(o, "A").MaxTier = 2
        Dim p = New ProfileResolver(o.BaseProfiles, o.Communication).Resolve(TestHost.Agent(o, "A"))
        Assert.Equal(2, p.MaxTier)
        Assert.Contains("local", p.AllowedModels)
        Assert.Equal(1, p.IdleDelaySeconds)
    End Sub
End Class

Public Class SecurityTests
    <Fact>
    Public Sub PasswordHashRoundTrip()
        Dim h = PasswordHasher.Hash("correct horse battery")
        Assert.True(PasswordHasher.Verify("correct horse battery", h))
        Assert.False(PasswordHasher.Verify("wrong", h))
    End Sub

    <Fact>
    Public Sub RedactorMasksSecrets()
        Dim r As New SecretRedactor()
        r.AddSecret("my-super-secret-value")
        Dim s = r.Redact("key=my-super-secret-value and sk-abcdefghijklmnopqrstuvwx and Bearer abcdefghijklmnop123")
        Assert.DoesNotContain("my-super-secret-value", s)
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwx", s)
        Assert.DoesNotContain("abcdefghijklmnop123", s)
    End Sub
End Class

Public Class EconomyUnitTests
    <Fact>
    Public Sub CostMathIsMicroUsd()
        Dim price As New ModelPrice With {.ModelKey = "x", .InputPerMillion = 2D, .OutputPerMillion = 8D}
        ' 1,000,000 input tokens at $2/M = $2 = 2,000,000 micros; 500,000 output at $8/M = $4.
        Assert.Equal(6000000L, PricingService.CostMicros(price, 1000000, 500000))
        Assert.Equal(0L, PricingService.CostMicros(New ModelPrice With {.IsLocal = True}, 1000000, 1000000))
        Assert.Equal(8L, PricingService.CostMicros(price, 0, 1)) ' 1 token × $8/M = 8 micro-USD
    End Sub

    <Fact>
    Public Sub UnpricedCloudModelIsNotPriced()
        Assert.False(New ModelPrice With {.InputPerMillion = 1D}.IsPriced)
        Assert.True(New ModelPrice With {.IsLocal = True}.IsPriced)
    End Sub
End Class

Public Class GateTests
    <Fact>
    Public Async Function HigherPriorityIsServedFirst() As Task
        Dim clock As New FakeClock()
        Dim gate As New PriorityConcurrencyGate(1, 0, clock)
        Dim holder = gate.Enter(PriorityLevel.Normal)
        Assert.True(holder.IsGranted)
        Dim low = gate.Enter(PriorityLevel.Low)
        Dim high = gate.Enter(PriorityLevel.High)
        Assert.False(low.IsGranted)
        Assert.Equal(2, gate.QueueLength)
        holder.Dispose()
        Assert.True(Await high.WaitAsync(TimeSpan.FromSeconds(1), Threading.CancellationToken.None))
        Assert.False(low.IsGranted)
        high.Dispose()
        Assert.True(Await low.WaitAsync(TimeSpan.FromSeconds(1), Threading.CancellationToken.None))
        low.Dispose()
        Assert.Equal(0, gate.InUse)
    End Function

    <Fact>
    Public Sub AgingPreventsStarvation()
        Dim clock As New FakeClock()
        Dim gate As New PriorityConcurrencyGate(1, 60, clock)
        Dim holder = gate.Enter(PriorityLevel.Normal)
        Dim low = gate.Enter(PriorityLevel.Low)
        clock.Advance(TimeSpan.FromMinutes(5)) ' Low ages up by 5 levels.
        Dim high = gate.Enter(PriorityLevel.High)
        holder.Dispose()
        Assert.True(low.IsGranted)
        Assert.False(high.IsGranted)
        low.Dispose()
        high.Dispose()
    End Sub

    <Fact>
    Public Sub TimedOutTicketKeepsQueuePositionAndCanLeave()
        Dim gate As New PriorityConcurrencyGate(1, 0, New FakeClock())
        Dim holder = gate.Enter(PriorityLevel.Normal)
        Dim waiter = gate.Enter(PriorityLevel.Normal)
        Assert.False(waiter.WaitAsync(TimeSpan.FromMilliseconds(50), Threading.CancellationToken.None).Result)
        Assert.Equal(1, gate.QueueLength)
        waiter.Dispose()
        Assert.Equal(0, gate.QueueLength)
        holder.Dispose()
        Assert.Equal(0, gate.InUse)
    End Sub

    <Fact>
    Public Sub CircuitOpensAfterFailuresAndHalfOpensLater()
        Dim clock As New FakeClock()
        Dim cb As New CircuitBreaker(2, TimeSpan.FromSeconds(30), clock)
        cb.RecordFailure()
        Assert.Equal(CircuitState.Closed, cb.State)
        cb.RecordFailure()
        Assert.Equal(CircuitState.Open, cb.State)
        Assert.False(cb.AllowRequest())
        clock.Advance(TimeSpan.FromSeconds(31))
        Assert.True(cb.AllowRequest())
        Assert.False(cb.AllowRequest()) ' only one half-open trial
        cb.RecordSuccess()
        Assert.Equal(CircuitState.Closed, cb.State)
    End Sub
End Class

Public Class LoopDetectorTests
    <Fact>
    Public Sub CycleIsRejected()
        ' Work that came A -> B -> C; C now tries to ask A.
        Dim ctx As New SendContext With {.CorrelationId = "c1", .DelegationDepth = 2, .ChainLength = 2, .Ancestry = New List(Of String) From {"A", "B"}}
        Dim r = LoopDetector.Check("C", "A", MessageType.TaskDelegation, ctx, 5, 12, False)
        Assert.False(r.Allowed)
        Assert.Equal("LOOP_DETECTED", r.Code)
    End Sub

    <Fact>
    Public Sub DepthLimitIsEnforced()
        Dim ctx As New SendContext With {.DelegationDepth = 5, .ChainLength = 5, .Ancestry = New List(Of String) From {"X1", "X2", "X3", "X4", "X5"}}
        Dim r = LoopDetector.Check("X6", "X7", MessageType.Request, ctx, 5, 12, False)
        Assert.Equal("DELEGATION_DEPTH_EXCEEDED", r.Code)
    End Sub

    <Fact>
    Public Sub RepliesAndInformationAreNotLoops()
        Dim ctx As New SendContext With {.Ancestry = New List(Of String) From {"A", "B"}, .ChainLength = 2, .DelegationDepth = 2}
        Assert.True(LoopDetector.Check("C", "A", MessageType.Response, ctx, 5, 12, False).Allowed)
        Assert.True(LoopDetector.Check("C", "A", MessageType.Information, ctx, 5, 12, False).Allowed)
        Assert.Equal("SELF_MESSAGE", LoopDetector.Check("A", "A", MessageType.Information, ctx, 5, 12, False).Code)
    End Sub
End Class

Public Class McpPermissionTests
    Private Shared Function Profile(servers As String(), allow As String(), deny As String()) As EffectiveProfile
        Dim o = TestHost.DefaultOptions(Path.GetTempPath())
        Dim a = TestHost.Agent(o, "A")
        a.AllowedMcpServers = servers.ToList()
        a.AllowedMcpTools = allow.ToList()
        a.DeniedMcpTools = deny.ToList()
        Return New ProfileResolver(o.BaseProfiles, o.Communication).Resolve(a)
    End Function

    <Fact>
    Public Sub ServerMustBeAllowed()
        Assert.False(McpToolRegistry.IsAllowed(Profile({}, {}, {}), "search", "search_web"))
        Assert.True(McpToolRegistry.IsAllowed(Profile({"search"}, {}, {}), "search", "search_web"))
    End Sub

    <Fact>
    Public Sub ToolAllowListAndDenyWins()
        Dim p = Profile({"files"}, {"files:read_file", "files:list"}, {"files:list"})
        Assert.True(McpToolRegistry.IsAllowed(p, "files", "read_file"))
        Assert.False(McpToolRegistry.IsAllowed(p, "files", "write_file"))
        Assert.False(McpToolRegistry.IsAllowed(p, "files", "list"))
    End Sub
End Class

Public Class IsolationApiShapeTests
    ''' <summary>The public surface of AgentManager must never hand out runtime objects of an agent.</summary>
    <Fact>
    Public Sub AgentManagerExposesNoRuntimeObjects()
        Dim forbidden = {GetType(AgentRuntime), GetType(AgentRuntimeContext), GetType(AgentScope), GetType(Memory.IAgentMemoryStore), GetType(Work.AgentTaskQueue)}
        For Each m In GetType(AgentManager).GetMethods(BindingFlags.Public Or BindingFlags.Instance)
            Assert.DoesNotContain(m.ReturnType, forbidden)
            If m.ReturnType.IsGenericType Then
                For Each ga In m.ReturnType.GetGenericArguments()
                    Assert.DoesNotContain(ga, forbidden)
                Next
            End If
        Next
        For Each p In GetType(AgentManager).GetProperties(BindingFlags.Public Or BindingFlags.Instance)
            Assert.DoesNotContain(p.PropertyType, forbidden)
        Next
    End Sub

    ''' <summary>Agent-facing APIs never take an agent id parameter (they are bound to one agent).</summary>
    <Fact>
    Public Sub ScopedServicesTakeNoAgentId()
        For Each t In {GetType(Memory.IAgentMemoryStore), GetType(Artifacts.ScopedArtifactAccess), GetType(AgentCommunicationClient), GetType(Work.AgentTaskQueue), GetType(AgentBudgetView)}
            For Each m In t.GetMethods(BindingFlags.Public Or BindingFlags.Instance Or BindingFlags.DeclaredOnly)
                Assert.DoesNotContain(m.GetParameters(), Function(p) p.Name.Equals("agentId", StringComparison.OrdinalIgnoreCase) OrElse p.Name.Equals("senderId", StringComparison.OrdinalIgnoreCase))
            Next
        Next
    End Sub
End Class

Public Class ProviderParsingTests
    <Fact>
    Public Sub ParsesToolCallsAndReasoningAndNullContent()
        Dim json = "{""choices"":[{""finish_reason"":""tool_calls"",""message"":{""role"":""assistant"",""content"":null,""reasoning_content"":""thinking"",""tool_calls"":[{""id"":""call_1"",""type"":""function"",""function"":{""name"":""get_x"",""arguments"":""{\""a\"":1}""}}]}}],""usage"":{""prompt_tokens"":12,""completion_tokens"":5}}"
        Dim r = OpenAICompatibleProvider.ParseCompletion(json)
        Assert.True(r.HasToolCalls)
        Assert.Equal("get_x", r.ToolCalls(0).Name)
        Assert.Equal("thinking", r.ReasoningContent)
        Assert.Equal(12L, r.Usage.PromptTokens.Value)
    End Sub

    <Fact>
    Public Sub ClassifiesProviderErrors()
        Assert.Equal(AiErrorKind.ModelUnavailable, AiErrorClassifier.FromHttp(503, "{""detail"":""litellm.ServiceUnavailableError: No available channel for model x""}").Kind)
        Assert.Equal(AiErrorKind.CapabilityUnsupported, AiErrorClassifier.FromHttp(400, "{""detail"":""this server was started without the vision encoder""}").Kind)
        Assert.Equal(AiErrorKind.ContextOverflow, AiErrorClassifier.FromHttp(400, "{""error"":{""message"":""maximum context length exceeded""}}").Kind)
        Assert.Equal(AiErrorKind.ContextOverflow, AiErrorClassifier.FromHttp(400, "{""error"":{""message"":""[strata] 400 invalid request: prompt (131159 tokens) leaves no room to answer in the context (131072); requests are never truncated""}}").Kind)
        Assert.Equal(AiErrorKind.RateLimited, AiErrorClassifier.FromHttp(429, "slow down").Kind)
        Assert.Equal(AiErrorKind.Timeout, AiErrorClassifier.FromHttp(524, "cloudflare timeout").Kind)
        Assert.Equal(AiErrorKind.Auth, AiErrorClassifier.FromHttp(401, "bad key").Kind)
    End Sub

    <Fact>
    Public Sub BodyIncludesToolsAndStreamUsage()
        Dim req As New ChatRequest With {.ModelId = "m", .Stream = True, .Tools = New List(Of ToolDefinition) From {New ToolDefinition With {.Name = "t", .Description = "d", .Parameters = Schema.Obj({})}}}
        req.Messages.Add(ChatMessage.ForUser("hi"))
        Dim body = OpenAICompatibleProvider.BuildBody(req).ToJsonString()
        Assert.Contains("""tools""", body)
        Assert.Contains("""include_usage"":true", body)
    End Sub

    <Fact>
    Public Sub TestImageIsValidPng()
        Dim png = ModelProbe.RedBluePng(16, 16)
        Assert.Equal(137, CInt(png(0)))
        Assert.Equal(AscW("P"c), CInt(png(1)))
    End Sub
End Class
