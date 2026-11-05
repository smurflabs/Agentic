Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Xunit

''' <summary>
''' Fallbacks on other providers stand in for a model without grants of their own; a second local model takes text work
''' while the main local model is full; a catalog key is billed at the Model Square price of the model it calls.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class ModelRoutingTests

    Private Shared Function Msgs() As List(Of ChatMessage)
        Return New List(Of ChatMessage) From {ChatMessage.ForUser("hello")}
    End Function

    <Fact>
    Public Async Function FallbackOnAnotherProviderNeedsNoGrantOfItsOwn() As Task
        Dim p As New FakeAIProvider()
        ' The primary (cloud-a) is down; its fallback on another provider answers.
        p.Responder = Function(r)
                          If r.ModelId = "cloud-a-model" Then Throw New AiException(AiErrorKind.ModelUnavailable, "no channel")
                          Return FakeAIProvider.Text("hi from " & r.ModelId)
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Models.Add(New ModelOptions With {.Key = "cloud-a-other", .ModelId = "other-provider/model", .Tier = 2, .MaxConcurrency = 4,
                                                       .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000,
                                                       .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 1D}})
                                                   o.Models.First(Function(m) m.Key = "cloud-a").Fallbacks = New List(Of String) From {"cloud-a-other"}
                                               End Sub, p)
            ' "A" may use cloud-a (base profile) but was never given cloud-a-other.
            Dim profile = host.H.Profiles.TryGetProfile("A")
            Assert.DoesNotContain("cloud-a-other", profile.AllowedModels)
            Dim cands = host.H.Selection.CandidatesAt(profile, AiPurpose.GeneralReasoning, 1, Nothing)
            Assert.Equal({"cloud-a", "cloud-a-other"}, cands.Select(Function(c) c.Model.Key).ToArray())
            Dim outcome = Await host.H.Gateway.ChatAsync(New AiCallContext With {.AgentId = "A", .Profile = profile, .LadderLevel = 1}, Msgs(), Nothing, CancellationToken.None)
            Assert.Equal("cloud-a-other", outcome.ModelKey)
            ' On its own (not as a stand-in) it is still not this agent's model.
            Assert.False(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("cloud-a-other"), profile, Nothing, AiPurpose.GeneralReasoning))
        End Using
    End Function

    <Fact>
    Public Async Function SecondLocalModelTakesTextWorkWhileTheMainOneIsFull() As Task
        Dim p As New FakeAIProvider()
        p.Delay = Function(r) If(r.ModelId = "local-model" AndAlso FakeAIProvider.AgentOf(r) Is Nothing, TimeSpan.FromSeconds(3), TimeSpan.Zero)
        p.Responder = Function(r) FakeAIProvider.Text("hi from " & r.ModelId)
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Models.First(Function(m) m.Key = "local").MaxConcurrency = 1
                                                   o.Models.Add(New ModelOptions With {.Key = "local-vision", .ModelId = "local-vision-model", .Tier = 1, .IsLocal = True, .MaxConcurrency = 2,
                                                       .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling, ModelCapability.Vision}, .MaxTokens = 1000})
                                                   o.Routing.Spillover.LocalCandidates = New List(Of String) From {"local-vision"}
                                                   o.Routing.Spillover.LocalKeepFreeSlots = 1
                                                   o.Routing.Spillover.NormalAfterSeconds = 30
                                               End Sub, p)
            Dim gw = host.H.Gateway
            Dim ctxOf = Function(id As String) New AiCallContext With {.AgentId = id, .Profile = host.H.Profiles.TryGetProfile(id)}
            ' C holds the only main local slot.
            Dim blocker = gw.ChatAsync(ctxOf("C"), Msgs(), Nothing, CancellationToken.None)
            Await Task.Delay(300)
            ' A is answered at once by the second local model: free, no 30 s wait, no cloud.
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim oa = Await gw.ChatAsync(ctxOf("A"), Msgs(), Nothing, CancellationToken.None)
            Assert.Equal("local-vision", oa.ModelKey)
            Assert.Equal(AiCallReason.Spillover, oa.Reason)
            Assert.Equal(0L, oa.CostMicros)
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), sw.Elapsed.ToString())
            ' With its one spare slot held (it keeps one free for vision and chat), B waits for the main local model.
            Dim held = host.H.ModelRegistry.TryGet("local-vision").Gate.TryEnterIfFree(PriorityLevel.Normal, 1)
            Assert.NotNull(held)
            Try
                Dim ob = Await gw.ChatAsync(ctxOf("B"), Msgs(), Nothing, CancellationToken.None)
                Assert.Equal("local", ob.ModelKey)
            Finally
                held.Dispose()
            End Try
            Await blocker
        End Using
    End Function

    <Fact>
    Public Sub GateTryEnterIfFreeNeverQueues()
        Dim gate As New PriorityConcurrencyGate(2, 0, Agentic.Utilities.SystemClock.Instance)
        Dim a = gate.TryEnterIfFree(PriorityLevel.Normal, 1)
        Assert.NotNull(a)
        Assert.Null(gate.TryEnterIfFree(PriorityLevel.Normal, 1))   ' one kept free
        Dim b = gate.TryEnterIfFree(PriorityLevel.Normal, 0)
        Assert.NotNull(b)
        Assert.Equal(2, gate.InUse)
        Assert.Equal(0, gate.QueueLength)
        a.Dispose()
        b.Dispose()
        Assert.Equal(0, gate.InUse)
    End Sub

    <Fact>
    Public Async Function KeyIsBilledAtTheModelSquarePriceOfItsModel() As Task
        Dim dir As String = Nothing
        ' First start: an admin price for the key (its old model) and a Model Square price for the model it now calls.
        Using host = Await TestHost.StartAsync()
            dir = host.DataDir
            host.KeepData = True
            Await host.H.Pricing.SetPriceAsync("cloud-a", 0.78D, 3.23D, Nothing, "admin")
            Await host.H.Accounts.SetPriceAsync("cloud-a-model", 0.02D, 0.03D, "admin", Nothing, keepCached:=True)
            Assert.Equal(0.78D, host.H.Pricing.GetPrice("cloud-a").InputPerMillion)
        End Using
        ' Next start: the key follows the Model Square price.
        Using host = Await TestHost.StartAsync(dataDir:=dir)
            Dim price = host.H.Pricing.GetPrice("cloud-a")
            Assert.Equal(0.02D, price.InputPerMillion)
            Assert.Equal(0.03D, price.OutputPerMillion)
            ' A key whose model has no Model Square price keeps its own.
            Assert.Equal(5D, host.H.Pricing.GetPrice("cloud-b").InputPerMillion)
        End Using
    End Function

End Class
