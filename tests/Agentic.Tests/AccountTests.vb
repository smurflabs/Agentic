Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic
Imports Agentic.Persistence
Imports Agentic.Security
Imports Xunit

''' <summary>Accounts that own agents pay for them; people share the local model's slots with the agents.</summary>
Public Class AccountTests

    <Fact>
    Public Async Function AgentsStopWhenTheirOwnersMoneyRunsOutAndContinueWhenMoneyIsAdded() As Task
        Using host = Await TestHost.StartAsync()
            Await host.H.AgentsRepo.CreateUserAsync(New DashboardUser With {.Username = "payer", .Role = "User"}, PasswordHasher.Hash("payer-pass-123"), "test")
            Await host.H.AgentsRepo.CreateUserAsync(New DashboardUser With {.Username = "boss", .Role = "Admin"}, PasswordHasher.Hash("boss-pass-1234"), "test")

            ' A belongs to a user with $0.00: it stops and its work waits.
            Await host.H.Agents.SetOwnerAsync("A", "payer")
            Await host.H.Accounts.AnnounceAsync("payer")
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Agents.GetSnapshot("A").Unfunded), 10), "A did not stop")
            Assert.Equal("payer", host.H.Agents.GetSnapshot("A").Owner)
            Dim t = Await host.SubmitAsync("A", "While its owner has no money")
            Await Task.Delay(1500)
            Assert.Equal(AgentTaskStatus.Pending, (Await host.H.Tasks.GetAsync(t)).Status)
            Assert.Equal("OWNER_OUT_OF_MONEY", (Await host.H.Agents.ResumeAgentAsync("A")).Code)

            ' Money comes in: A continues and does the waiting work.
            Assert.Equal(2000000L, Await host.H.Accounts.AdjustAsync("payer", 2000000, "top-up", "test"))
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(Not host.H.Agents.GetSnapshot("A").Unfunded), 10), "A did not continue")
            Await host.WaitForTaskStatusAsync("A", t, AgentTaskStatus.Completed)

            ' The agent's cloud use is charged to its owner as well.
            Await host.H.Gateway.AgentCharged.Invoke("A", 500, "cloud-a", "test call")
            Assert.Equal(1999500L, (Await host.H.Accounts.GetAccountAsync("payer")).BalanceMicros)
            Dim entry = (Await host.H.Accounts.LedgerAsync("payer", 5)).First()
            Assert.Equal("Agent", entry.EntryType)
            Assert.Equal("A", entry.AgentId)

            ' Administrators' agents never stop for money.
            Await host.H.Agents.SetOwnerAsync("B", "boss")
            Await host.H.Accounts.AnnounceAsync("boss")
            Await Task.Delay(500)
            Assert.False(host.H.Agents.GetSnapshot("B").Unfunded)
            Assert.Null(Await host.H.Accounts.ChargeAsync("boss", 1000, "Agent", "cloud-a", Nothing, Nothing, "B", "never charged"))
        End Using
    End Function

    <Fact>
    Public Async Function PeopleShareTheLocalModelsSlotsWithAgentsAndQueueForThem() As Task
        Using host = Await TestHost.StartAsync()
            Dim gw = host.H.Gateway
            Assert.Null(Await gw.TakeLocalSlotAsync("cloud-a-model", Nothing, CancellationToken.None))   ' only the local model's slots are shared
            Dim m = gw.LocalModelFor("local-model")
            Assert.Equal(2, m.Gate.Capacity)
            Dim first = Await gw.TakeLocalSlotAsync("local-model", Nothing, CancellationToken.None)
            Dim second = Await gw.TakeLocalSlotAsync("local-model", Nothing, CancellationToken.None)
            Assert.Equal(2, m.ChatSlots)
            Assert.Contains(gw.ModelStatus(), Function(s) CStr(s("key")) = "local" AndAlso CInt(s("chatSlots")) = 2)

            ' A third person waits in the queue (and hears where they are) until a slot frees up.
            Dim heard As New Concurrent.ConcurrentQueue(Of Integer)
            Dim third = gw.TakeLocalSlotAsync("local-model", Function(ahead As Integer, capacity As Integer)
                                                                  heard.Enqueue(ahead)
                                                                  Return Task.CompletedTask
                                                              End Function, CancellationToken.None)
            Await Task.Delay(300)
            Assert.False(third.IsCompleted)
            Assert.Equal(0, heard.First())   ' next in line
            first.Dispose()
            Dim got = Await third.WaitAsync(TimeSpan.FromSeconds(10))
            Assert.Equal(2, m.ChatSlots)
            second.Dispose()
            got.Dispose()
            got.Dispose()   ' releasing twice is harmless
            Assert.Equal(0, m.ChatSlots)
        End Using
    End Function

    <Fact>
    Public Sub ModelSquarePricesWorkInMicroDollars()
        Dim p As New Economy.SquarePrice With {.ModelId = "m", .InputPerMillion = 0.15D, .OutputPerMillion = 0.6D}
        ' 1M input tokens at $0.15 = 150,000 micro-USD; 10 output tokens at $0.60/1M = 6 micro-USD.
        Assert.Equal(150006L, Economy.AccountService.CostMicros(p, 1000000, 10))
        Assert.Equal(1L, Economy.AccountService.CostMicros(p, 1, 0))   ' rounds up: never free when priced
        Assert.Equal(0L, Economy.AccountService.CostMicros(Nothing, 1000, 1000))
    End Sub
End Class
