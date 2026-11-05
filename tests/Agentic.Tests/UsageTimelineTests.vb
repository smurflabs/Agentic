Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>The token usage graph: tokens per user (or model) over time; administrators see everyone, users themselves.</summary>
Public Class UsageTimelineTests

    <Fact>
    Public Async Function UsageIsGroupedByUserAndModelOverTime() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hello"}))
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "viewer", .password = "viewer-pass-123", .role = "User", .permissions = {"models.use"}}, admin.Csrf)).StatusCode)

                For Each range In {"24h", "7d", "30d"}
                    Dim d = JsonNode.Parse(Await admin.Client.GetStringAsync($"/api/usage/timeline?range={range}&by=user&tz=-120"))
                    Assert.Equal("all", d("scope").ToString())
                    Assert.Equal(If(range = "24h", 24, If(range = "7d", 56, 30)), d("buckets").AsArray().Count)
                    Dim adminLine = d("series").AsArray().First(Function(s) s("label").ToString() = "admin")
                    Dim inputs = adminLine("input").AsArray().Select(Function(v) v.GetValue(Of Long)()).ToList()
                    Assert.Equal(d("buckets").AsArray().Count, inputs.Count)
                    ' This hour's use is in the last bucket.
                    Assert.True(inputs.Last() > 0, $"{range}: {String.Join(",", inputs)}")
                    Assert.True(d("totals")("input").GetValue(Of Long)() > 0)
                Next
                Dim byModel = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/usage/timeline?range=24h&by=model"))
                Assert.Contains(byModel("series").AsArray(), Function(s) s("kind").ToString() = "model" AndAlso s("inputTotal").GetValue(Of Long)() > 0)
                ' Who used each model, and which models each user used.
                Dim modelLine = byModel("series").AsArray().First(Function(s) s("kind").ToString() = "model" AndAlso s("inputTotal").GetValue(Of Long)() > 0)
                Assert.Contains(modelLine("parts").AsArray(), Function(p) p("label").ToString() = "admin" AndAlso p("kind").ToString() = "user" AndAlso
                                                                         p("input").GetValue(Of Long)() > 0 AndAlso p("calls").GetValue(Of Long)() >= 1)
                Assert.Equal(0, modelLine("moreParts").GetValue(Of Integer)())
                Dim byUser = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/usage/timeline?range=24h&by=user"))
                Dim adminParts = byUser("series").AsArray().First(Function(s) s("label").ToString() = "admin")("parts").AsArray()
                Assert.Contains(adminParts, Function(p) p("kind").ToString() = "model" AndAlso p("label").ToString() = modelLine("label").ToString())

                ' Another user sees only their own use (none yet).
                Dim viewer = Await PlatformApiTests.LoginAsync(env.Port, "viewer", "viewer-pass-123")
                Dim own = JsonNode.Parse(Await viewer.Client.GetStringAsync("/api/usage/timeline?range=24h"))
                Assert.Equal("mine", own("scope").ToString())
                Assert.Empty(own("series").AsArray())
            End Using
        End Using
    End Function

End Class
