Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports Agentic
Imports Agentic.AI
Imports Agentic.Work
Imports Xunit

Public Class RunwayAndMetricsTests

    <Fact>
    Public Async Function RunwayIsForecastAndAgentsAreWarnedWhenItRunsLow() As Task
        Dim p As New FakeAIProvider()
        p.Responder = Function(r) FakeAIProvider.Complete("done")
        Using host = Await TestHost.StartAsync(Nothing, p)
            ' Nothing spent yet: no burn rate, no forecast.
            Dim w0 = Await host.H.Wallets.GetAsync("A")
            Assert.Equal(0, w0.BurnRateMicrosPerDay)
            Assert.Null(w0.RunwayDays)

            ' A paid task gives A a burn rate; with $5 the balance lasts a long time.
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Paid job", .Description = "do it", .StartTier = 2})
            Await host.WaitForTaskStatusAsync("A", r.TaskId, AgentTaskStatus.Completed)
            Dim w1 = Await host.H.Wallets.GetAsync("A")
            Assert.True(w1.BurnRateMicrosPerDay > 0)
            ' The wallet is younger than a day, so the whole spend counts as one day's burn.
            Assert.Equal(w1.LifetimeSpendMicros, w1.BurnRateMicrosPerDay)
            Assert.True(w1.RunwayDays.Value > 100)
            Assert.False(host.H.Wallets.IsLowRunway(w1))

            ' Leave two days of runway: the agent is flagged and its prompt tells it to economize.
            Await host.H.Wallets.AdjustAsync("A", -(w1.AvailableMicros - 2 * w1.BurnRateMicrosPerDay), "drain", Nothing, "test")
            Dim w2 = Await host.H.Wallets.GetAsync("A")
            Assert.InRange(w2.RunwayDays.Value, 1.9, 2.1)
            Assert.True(host.H.Wallets.IsLowRunway(w2))
            Dim t = Await host.SubmitAsync("A", "Local job")
            Await host.WaitForTaskStatusAsync("A", t, AgentTaskStatus.Completed)
            Assert.Contains(p.Requests, Function(x) FakeAIProvider.AgentOf(x) = "A" AndAlso
                                                    x.Messages.Any(Function(m) If(m.Content, "").Contains("your balance lasts about 2 days") AndAlso
                                                                               m.Content.Contains("WARNING: your budget is running out")))
            ' B has spent nothing and is not warned.
            Assert.False(host.H.Wallets.IsLowRunway(Await host.H.Wallets.GetAsync("B")))
        End Using
    End Function

    <Fact>
    Public Async Function MetricsEndpointExposesPrometheusText() As Task
        Dim env = Await HttpApiTests.StartWithHttpAsync()
        Using env.Host
            Using c = env.Client
                Assert.Equal(HttpStatusCode.Unauthorized, (Await c.GetAsync("/api/metrics")).StatusCode)
                Dim login = Await c.PostAsync("/api/auth/login", HttpApiTests.JsonBody(New With {.username = "admin", .password = "pw-123456789"}))
                Assert.Equal(HttpStatusCode.OK, login.StatusCode)
                Dim res = Await c.GetAsync("/api/metrics")
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Assert.StartsWith("text/plain", res.Content.Headers.ContentType.MediaType)
                Dim body = Await res.Content.ReadAsStringAsync()
                Assert.Contains("# TYPE agentic_up gauge", body)
                Assert.Contains("agentic_up 1", body)
                Assert.Contains("agentic_wallet_available_usd{agent=""A""} 5", body)
                Assert.Contains("agentic_model_slots_capacity{model=""local"",local=""true""} 2", body)
                Assert.Contains("agentic_lessons_active 0", body)
                ' Each metric is declared once even with many series.
                Assert.Equal(1, body.Split(vbLf).Count(Function(l) l = "# TYPE agentic_wallet_available_usd gauge"))

                ' Wallet DTOs carry the forecast fields.
                Dim fin = JsonDocument.Parse(Await c.GetStringAsync("/api/finance")).RootElement
                Assert.Equal(3.0, fin.GetProperty("lowRunwayDays").GetDouble())
                Dim wa = fin.GetProperty("wallets").EnumerateArray().First(Function(x) x.GetProperty("agentId").GetString() = "A")
                Assert.Equal(JsonValueKind.Null, wa.GetProperty("runwayDays").ValueKind)
                Assert.False(wa.GetProperty("lowRunway").GetBoolean())
            End Using
        End Using
    End Function
End Class
