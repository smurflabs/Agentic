Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Xunit

''' <summary>Model control: a model an administrator turns off is used by nobody (Chat, the API, custom routes, agents).</summary>
Public Class ModelControlTests

    Private Shared Function Ids(list As JsonElement) As String()
        Return list.EnumerateArray().Select(Function(m) m.GetProperty("id").GetString()).ToArray()
    End Function

    <Fact>
    Public Async Function ATurnedOffModelIsNotOfferedOrUsableUntilTurnedOnAgain() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim list = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/model-control")).RootElement
                Assert.True(list.GetProperty("models").EnumerateArray().First(Function(m) m.GetProperty("id").GetString() = "fast/a").GetProperty("enabled").GetBoolean())
                Dim chatId = JsonDocument.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()

                ' Off: gone from the Chat tab and /v1/models, refused by Chat and the API.
                Dim off = Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-control", New With {.models = {"fast/a"}, .enabled = False}, admin.Csrf)
                Assert.True(off.IsSuccessStatusCode, Await off.Content.ReadAsStringAsync())
                Dim meta = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement
                Assert.DoesNotContain("fast/a", Ids(meta.GetProperty("providerModels")))
                Assert.Contains("code/b", Ids(meta.GetProperty("providerModels")))
                Assert.DoesNotContain("fast/a", Ids(JsonDocument.Parse(Await admin.Client.GetStringAsync("/v1/models")).RootElement.GetProperty("data")))
                Dim hi = New With {.role = "user", .content = "hi"}
                Dim api = Await PlatformApiTests.Send(admin.Client, "POST", "/v1/chat/completions", New With {.model = "fast/a", .messages = {hi}}, admin.Csrf)
                Assert.Equal(HttpStatusCode.NotFound, api.StatusCode)
                Assert.Contains("turned off", Await api.Content.ReadAsStringAsync())
                Dim sse = Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/messages", New With {.content = "hello", .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
                Assert.DoesNotContain("event: done", sse)
                Assert.Contains("not available", sse)
                ' Still listed in Model control, so it can be turned on again.
                Dim row = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/model-control")).RootElement.GetProperty("models").EnumerateArray().First(Function(m) m.GetProperty("id").GetString() = "fast/a")
                Assert.False(row.GetProperty("enabled").GetBoolean())
                Assert.Equal("admin", row.GetProperty("disabledBy").GetString())

                ' Kept across a restart (stored), and only administrators change it.
                Await env.Host.H.ModelHub.LoadDisabledAsync()
                Assert.True(env.Host.H.ModelHub.IsDisabled("FAST/A"))
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "plain", .password = "plain-pass-123", .role = "User", .permissions = {"models.use"}}, admin.Csrf)).StatusCode)
                Dim plain = Await PlatformApiTests.LoginAsync(env.Port, "plain", "plain-pass-123")
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(plain.Client, "PUT", "/api/model-control", New With {.models = {"fast/a"}, .enabled = True}, plain.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Forbidden, (Await plain.Client.GetAsync("/api/model-control")).StatusCode)

                ' On again: offered and answering.
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-control", New With {.models = {"fast/a"}, .enabled = True}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Contains("fast/a", Ids(JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement.GetProperty("providerModels")))
                Assert.True((Await PlatformApiTests.Send(admin.Client, "POST", "/v1/chat/completions", New With {.model = "fast/a", .messages = {hi}}, admin.Csrf)).IsSuccessStatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AgentsAndCustomRoutesSkipTurnedOffModels() As Task
        Using host = Await TestHost.StartAsync()
            Dim a = host.H.Agents.GetEffectiveProfile("A")
            Dim cloud = host.H.ModelRegistry.TryGet("cloud-a")
            Assert.True(host.H.Selection.IsUsableBy(cloud, a, Nothing))
            ' Turned off by its model id: agents no longer get it.
            Await host.H.ModelHub.SetEnabledAsync("cloud-a-model", False, "test")
            Assert.False(host.H.Selection.IsUsableBy(cloud, a, Nothing))
            Assert.True(host.H.Selection.IsUsableBy(host.H.ModelRegistry.TryGet("local"), a, Nothing))
            Await host.H.ModelHub.SetEnabledAsync("cloud-a-model", True, "test")
            Assert.True(host.H.Selection.IsUsableBy(cloud, a, Nothing))

            ' A custom model's route skips turned-off models; when all are off it says so.
            Await host.H.ModelHub.SetEnabledAsync("only/model", False, "test")
            Dim decision As New RouteDecision With {.Model = "only/model", .Category = "default", .Reason = "test", .Fallbacks = New List(Of String)}
            Dim ex = Await Assert.ThrowsAsync(Of ModelRefusedException)(
                Function() host.H.ModelHub.SendWithFallbacksAsync("/chat/completions", Nodes.JsonNode.Parse("{""messages"":[]}"), Nothing, decision, CancellationToken.None))
            Assert.Contains("turned off", ex.Message)
        End Using
    End Function
End Class
