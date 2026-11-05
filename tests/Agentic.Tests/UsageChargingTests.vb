Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Economy
Imports Xunit

''' <summary>Token charging: cached input at its own price, usage counted once per model call, and the balance during tool loops.</summary>
Public Class UsageChargingTests

    ''' <summary>One stream chunk that carries only a usage block (as providers send it, sometimes several times per call).</summary>
    Private Shared Function UsageChunk(prompt As Long, completion As Long, cached As Long) As String
        Dim u As New JsonObject From {{"prompt_tokens", prompt}, {"completion_tokens", completion}, {"total_tokens", prompt + completion}}
        If cached > 0 Then u("prompt_tokens_details") = New JsonObject From {{"cached_tokens", cached}}
        Return "data: " & New JsonObject From {{"choices", New JsonArray()}, {"usage", u}}.ToJsonString() & vbLf & vbLf
    End Function

    Private Shared Function WithUsage(stream As String, prompt As Long, completion As Long, cached As Long, Optional times As Integer = 1) As String
        Dim usage = String.Concat(Enumerable.Repeat(UsageChunk(prompt, completion, cached), times))
        Return stream.Replace("data: [DONE]", usage & "data: [DONE]")
    End Function

    <Fact>
    Public Sub CachedInputCostsItsOwnPriceOrATenth()
        Dim p As New SquarePrice With {.ModelId = "m", .InputPerMillion = 3D, .OutputPerMillion = 5D}
        ' 1,000 input tokens of which 800 cached, 100 output: 200 × $3 + 800 × $0.30 + 100 × $5 per 1M.
        Assert.Equal(600L + 240L + 500L, AccountService.CostMicros(p, 1000, 100, 800))
        Assert.Equal(3000L + 500L, AccountService.CostMicros(p, 1000, 100))
        p.CachedInputPerMillion = 1D
        Assert.Equal(600L + 800L + 500L, AccountService.CostMicros(p, 1000, 100, 800))
        ' Cached tokens never exceed the prompt.
        Assert.Equal(1000L, AccountService.CostMicros(p, 1000, 0, 5000))

        Assert.Equal(800L, AccountService.CachedTokensOf(JsonNode.Parse("{""prompt_tokens"":1000,""prompt_tokens_details"":{""cached_tokens"":800}}")))
        Assert.Equal(700L, AccountService.CachedTokensOf(JsonNode.Parse("{""input_tokens"":1000,""cache_read_input_tokens"":700}")))
        Assert.Equal(600L, AccountService.CachedTokensOf(JsonNode.Parse("{""prompt_tokens"":1000,""prompt_cache_hit_tokens"":600}")))
        Assert.Equal(0L, AccountService.CachedTokensOf(JsonNode.Parse("{""prompt_tokens"":1000}")))

        ' Agents' model calls: cached input at a tenth of the input price as well.
        Dim agentPrice As New ModelPrice With {.ModelKey = "cloud-a", .InputPerMillion = 3D, .OutputPerMillion = 5D}
        Assert.Equal(600L + 240L + 500L, PricingService.CostMicros(agentPrice, 1000, 100, 800))
        Assert.Equal(3000L + 500L, PricingService.CostMicros(agentPrice, 1000, 100))
    End Sub

    <Fact>
    Public Async Function ChatChargesCachedInputCheaplyAndCountsRepeatedUsageOnce() As Task
        Using up As New FakeUpstream()
            ' The provider repeats the call's usage in three chunks; 800 of the 1,000 prompt tokens came from its cache.
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), WithUsage(ChatFeatureTests.TextStream("Hello there.", "stop"), 1000, 100, 800, 3), Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "payer", .password = "payer-pass-123", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/payer/balance", New With {.amount = 5}, admin.Csrf)
                Dim payer = Await PlatformApiTests.LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim id = Await ChatFeatureTests.NewChatAsync(payer, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(payer, id, New With {.content = "hi"})
                Dim message = ChatFeatureTests.Done(sse)("message")
                Assert.Equal(1000L, message("promptTokens").GetValue(Of Long)())
                Assert.Equal(100L, message("completionTokens").GetValue(Of Long)())
                ' 200 × $1000 + 800 × $100 (10%) + 100 × $2000 per 1M = $0.48.
                Assert.Equal(480000L, message("costMicros").GetValue(Of Long)())
                Assert.Equal(4520000L, (Await env.Host.H.Accounts.GetAccountAsync("payer")).BalanceMicros)
                Dim entry = (Await env.Host.H.Accounts.LedgerAsync("payer", 1)).First()
                Assert.Contains("800 of 1,000 input tokens cached", entry.Description)

                ' A cached price of its own (Model Square), kept when only the input/output prices change later.
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000, .cachedInputPerMillion = 500}}}, admin.Csrf)).IsSuccessStatusCode)
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal(500D, env.Host.H.Accounts.PriceOf("fast/a").CachedInputPerMillion)
                Dim row = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/model-square")).RootElement.GetProperty("models").EnumerateArray().First(Function(m) m.GetProperty("id").GetString() = "fast/a")
                Assert.Equal(500D, row.GetProperty("cachedInputPerMillion").GetDecimal())

                ' The API charges cached input the same way.
                Dim hi = New With {.role = "user", .content = "hi"}
                up.TextOverride = Function(req) "ok"
                Dim before = (Await env.Host.H.Accounts.GetAccountAsync("payer")).BalanceMicros
                Assert.True((Await PlatformApiTests.Send(payer.Client, "POST", "/v1/chat/completions", New With {.model = "fast/a", .messages = {hi}, .stream = True}, payer.Csrf)).IsSuccessStatusCode)
                ' 200 × $1000 + 800 × $500 + 100 × $2000 per 1M = $0.80.
                Assert.Equal(before - 800000L, (Await env.Host.H.Accounts.GetAccountAsync("payer")).BalanceMicros)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AToolLoopStopsOnceTheAnswerHasUsedUpTheBalance() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            ' Every round calls a tool again (different arguments, so the loop guard does not step in) and uses $1.
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Dim n = Interlocked.Increment(calls)
                                    Return WithUsage(ChatFeatureTests.ToolCallStream("remember", $"{{""fact"":""Fact number {n}""}}"), 1000, 0, 0)
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "payer", .password = "payer-pass-123", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/payer/balance", New With {.amount = 1.5}, admin.Csrf)
                Dim payer = Await PlatformApiTests.LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim id = Await ChatFeatureTests.NewChatAsync(payer, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(payer, id, New With {.content = "remember lots of things"})
                Dim message = ChatFeatureTests.Done(sse)("message")
                ' Round 1 ($1) leaves money; round 2 ($2 in all) uses it up; no third model call.
                Assert.Equal(2, calls)
                Assert.Equal("balance", message("finishReason").ToString())
                Assert.Contains("balance ran out", message("content").ToString())
                Assert.Equal(-500000L, (Await env.Host.H.Accounts.GetAccountAsync("payer")).BalanceMicros)
            End Using
        End Using
    End Function
End Class
