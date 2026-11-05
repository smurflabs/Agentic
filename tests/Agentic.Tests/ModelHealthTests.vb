Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Xunit

''' <summary>
''' The dot beside each model in the model picker: green, yellow or red from its recent calls; gray without any. Beside it,
''' the model's speed: average response time and tokens per second.
''' </summary>
Public Class ModelHealthTests

    Private Shared ReadOnly Now As New DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc)

    ''' <summary>Calls of one model, newest first, one minute apart; True = it worked.</summary>
    Private Shared Function CallsOf(model As String, ParamArray results As Boolean()) As List(Of ModelCallRecord)
        Return results.Select(Function(ok, i) New ModelCallRecord With {
            .Model = model, .Source = "chat", .Ok = ok, .DurationMs = 800, .CreatedUtc = Now.AddMinutes(-1 - i), .ErrorText = If(ok, Nothing, "insufficient_quota")}).ToList()
    End Function

    Private Shared Function StateOf(ParamArray results As Boolean()) As String
        Return ModelHealth.Evaluate(CallsOf("m", results), Now)("m").State
    End Function

    <Fact>
    Public Sub HealthComesFromTheLatestCalls()
        Assert.Equal("green", StateOf(True, True, True, True, True, True))
        Assert.Equal("green", StateOf(True))
        ' A failure among the latest five, or 15% or more failing: yellow.
        Assert.Equal("yellow", StateOf(True, True, False, True, True, True, True, True))
        Assert.Equal("green", StateOf(True, True, True, True, True, True, False))   ' 1 in 7 (14%), not among the latest five
        Assert.Equal("red", StateOf(False, False, False, True, True, True))   ' the latest three failed
        Assert.Equal("red", StateOf(False, False))
        Assert.Equal("red", StateOf(True, False, True, False, False, True, False, True))   ' half failed
        Assert.Equal("yellow", StateOf(False))   ' one failed call alone is not yet red
        ' Recovered: the latest five worked and fewer than 15% failed.
        Assert.Equal("green", StateOf(True, True, True, True, True, True, True, True, True, True, False))
    End Sub

    <Fact>
    Public Sub TheLastHourDecidesWhenItHasEnoughCalls()
        ' Six good calls in the last hour; older failures (earlier today) do not count.
        Dim calls = CallsOf("m", True, True, True, True, True, True)
        For i = 0 To 9
            calls.Add(New ModelCallRecord With {.Model = "m", .Source = "api", .Ok = False, .CreatedUtc = Now.AddHours(-3).AddMinutes(-i), .ErrorText = "model not found"})
        Next
        Dim h = ModelHealth.Evaluate(calls, Now)("m")
        Assert.Equal("green", h.State)
        Assert.Equal("1h", h.Window)
        Assert.Equal(6, h.Calls)
        Assert.Equal("model not available", h.LastErrorKind)   ' the latest failure is still reported
        ' Few calls lately: the latest ten of the day decide.
        Dim quiet = CallsOf("q", True, True)
        For i = 0 To 9
            quiet.Add(New ModelCallRecord With {.Model = "q", .Source = "agents", .Ok = False, .CreatedUtc = Now.AddHours(-5).AddMinutes(-i), .ErrorText = "timed out"})
        Next
        Dim q = ModelHealth.Evaluate(quiet, Now)("q")
        Assert.Equal("recent", q.Window)
        Assert.Equal(10, q.Calls)
        Assert.Equal("red", q.State)   ' 8 of the latest 10 failed
        Assert.Equal(2, q.Chat) : Assert.Equal(8, q.Agents)
        ' Calls older than a day: no entry (a gray dot).
        Dim old = {New ModelCallRecord With {.Model = "o", .Source = "chat", .Ok = True, .CreatedUtc = Now.AddDays(-2)}}
        Assert.False(ModelHealth.Evaluate(old, Now).ContainsKey("o"))
    End Sub

    <Fact>
    Public Sub StoppingIsNotAFailureAndFailuresAreNamed()
        Assert.True(ModelHealth.IsStopped("Stopped."))
        Assert.True(ModelHealth.IsStopped("The operation was canceled."))
        Assert.False(ModelHealth.IsStopped("Upstream error 500"))
        Assert.False(ModelHealth.IsStopped(Nothing))
        Assert.Equal("quota or billing", ModelHealth.ErrorKind("You exceeded your current quota, please check your plan and billing details."))
        Assert.Equal("model not available", ModelHealth.ErrorKind("No deployments available for selected model"))
        Assert.Equal("timed out", ModelHealth.ErrorKind("The request timed out after 120 s"))
        Assert.Equal("overloaded", ModelHealth.ErrorKind("HTTP 429 Too Many Requests"))
        Assert.Equal("overloaded", ModelHealth.ErrorKind("Rate limit reached for requests"))
        Assert.Equal("error", ModelHealth.ErrorKind("Failed to generate a reply"))   ' "generate" is not a rate limit
        Assert.Equal("timed out", ModelHealth.ErrorKind("Gateway timeout"))
        ' A provider's JSON error reads as its message, after the status.
        Dim channel = ModelHealth.Readable(503, "{""error"":{""message"":""No available channel""}}")
        Assert.Equal("HTTP 503: No available channel", channel)
        Assert.Equal("model not available", ModelHealth.ErrorKind(channel))
        Assert.Equal("HTTP 400: Bad model", ModelHealth.Readable(400, "{""detail"":""Bad model""}"))
        Assert.Equal("HTTP 500: busy", ModelHealth.Readable(500, "{""error"":""busy""}"))
        Assert.Equal("HTTP 502", ModelHealth.Readable(502, Nothing))
        Assert.Equal("HTTP 500: {not json", ModelHealth.Readable(500, "{not json"))
        Assert.Equal("unreachable", ModelHealth.ErrorKind("Connection refused"))
        Assert.Equal("error", ModelHealth.ErrorKind("Something odd"))
    End Sub

    <Fact>
    Public Async Function ThePickerShowsHowEachModelIsDoing() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hello"}))
                Dim s = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/model-status"))
                Dim fast = s("models")("fast/a")
                Assert.NotNull(fast)
                Assert.Equal("green", fast("state").ToString())
                Assert.True(fast("chat").GetValue(Of Integer)() >= 1)
                Assert.Null(s("models")("cloud-b"))   ' no calls: no entry (gray)
                ' The chat page gets the same when it opens.
                Dim meta = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/meta"))
                Assert.Equal("green", meta("modelStatus")("fast/a")("state").ToString())
                ' A user sees only the models they may use.
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "picky", .password = "picky-pass-123", .role = "User",
                    .permissions = {"models.use"}, .allowedModels = {"cloud-a"}}, admin.Csrf)).StatusCode)
                Dim picky = Await PlatformApiTests.LoginAsync(env.Port, "picky", "picky-pass-123")
                Dim mine = JsonNode.Parse(Await picky.Client.GetStringAsync("/api/chat/model-status"))
                Assert.Null(mine("models")("fast/a"))
            End Using
        End Using
    End Function

    Private Shared Function Sample(model As String, firstTokenMs As Long?, durationMs As Long, tokens As Long, Optional via As String = Nothing,
                                   Optional minutesAgo As Integer = 1) As ModelSpeedSample
        Return New ModelSpeedSample With {.Model = model, .Via = via, .Source = "chat", .FirstTokenMs = firstTokenMs, .DurationMs = durationMs,
                                          .OutputTokens = tokens, .CreatedUtc = Now.AddMinutes(-minutesAgo)}
    End Function

    <Fact>
    Public Sub SpeedIsTheAverageResponseTimeAndTokensPerSecondWhileWriting()
        Dim m = ModelHealth.Speed({Sample("m", 500, 2500, 400), Sample("m", 300, 1300, 200), Sample("m", Nothing, 4000, 300)}, Now)("m")
        Assert.Equal(3, m.Calls)
        Assert.Equal(2600, m.ResponseMs)                    ' (2500 + 1300 + 4000) / 3
        Assert.Equal(400L, m.FirstTokenMs)                  ' streamed calls only
        Assert.Equal(Math.Round(900 / 7.0, 1), m.Tps)       ' 900 tokens over 2 + 1 + 4 s of writing (not streamed: the whole call)
        ' An answer that came in one piece (first token at the end) is timed over the whole call.
        Assert.Equal(100.0, ModelHealth.Speed({Sample("q", 995, 1000, 100)}, Now)("q").Tps)
        ' A call through a custom router model counts for both.
        Dim routed = ModelHealth.Speed({Sample("fast", 100, 1100, 300, via:="router")}, Now)
        Assert.Equal(300.0, routed("fast").Tps)
        Assert.Equal(300.0, routed("router").Tps)
        ' Only the last day, only calls that wrote something, and only the latest SpeedCalls.
        Dim old = ModelHealth.Speed({Sample("o", 100, 1100, 300, minutesAgo:=25 * 60), Sample("e", 100, 1100, 0)}, Now)
        Assert.Empty(old)
        Dim many = Enumerable.Range(0, ModelHealth.SpeedCalls).Select(Function(i) Sample("n", 100, 1100, 100, minutesAgo:=i + 1)).
                   Concat(Enumerable.Range(0, 10).Select(Function(i) Sample("n", 100, 10100, 100, minutesAgo:=600 + i))).ToList()
        Dim n = ModelHealth.Speed(many, Now)("n")
        Assert.Equal(ModelHealth.SpeedCalls, n.Calls)
        Assert.Equal(1100, n.ResponseMs)
        Assert.Equal(100.0, n.Tps)
    End Sub

    <Fact>
    Public Async Function TheProviderTimesTheFirstToken() As Task
        Using up As New FakeUpstream() With {.ChunkDelay = TimeSpan.FromMilliseconds(120)}
            Using p As New OpenAICompatibleProvider("test", New AIProviderOptions With {.BaseUrl = up.BaseUrl, .TimeoutSeconds = 30})
                Dim r = Await p.ChatAsync(New ChatRequest With {.ModelId = "fast/a", .Messages = New List(Of ChatMessage) From {ChatMessage.ForUser("hi")},
                                                                .Stream = True, .Timeout = TimeSpan.FromSeconds(30)}, CancellationToken.None)
                Assert.NotNull(r.FirstTokenMs)
                ' The first part comes at once, the other four 120 ms apart.
                Assert.True(r.DurationMs - r.FirstTokenMs.Value >= 400, $"first {r.FirstTokenMs} ms, whole {r.DurationMs} ms")
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ChatApiAndAgentsCallsAllCountForSpeed() As Task
        Using up As New FakeUpstream() With {.ChunkDelay = TimeSpan.FromMilliseconds(100)}
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hello"}))
                Dim key = Await PlatformApiTests.NewKeyAsync(admin.Client, admin.Csrf)
                Using anon As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim r = Await PlatformApiTests.Send(anon, "POST", "/v1/chat/completions", New With {.model = "fast/a", .stream = True, .messages = {New With {.role = "user", .content = "hi"}}}, key:=key)
                    Assert.Equal(HttpStatusCode.OK, r.StatusCode)
                    Assert.Contains("[DONE]", Await r.Content.ReadAsStringAsync())
                End Using
                ' Written in the background (an answer never waits for it): one streamed row each, about 400 ms, 3 tokens.
                Dim db = env.Host.H.Database
                Dim rowsOf = Async Function(source As String) As Task(Of Long)
                                 Return CLng(Await db.ScalarAsync("SELECT COUNT(*) FROM ModelSpeed WHERE Model='fast/a' AND Source=@s AND FirstTokenMs IS NOT NULL AND DurationMs - FirstTokenMs >= 250 AND OutputTokens = 3",
                                                                  ("@s", CObj(source))))
                             End Function
                For i = 0 To 150
                    If Await rowsOf("chat") = 1 AndAlso Await rowsOf("api") = 1 Then Exit For
                    Await Task.Delay(100)
                Next
                Assert.Equal(1L, Await rowsOf("chat"))
                Assert.Equal(1L, Await rowsOf("api"))
                Dim s = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/chat/model-status"))("models")("fast/a")
                Assert.Equal(2, s("speedCalls").GetValue(Of Integer)())
                Assert.True(s("responseMs").GetValue(Of Long)() >= 300, s.ToJsonString())
                Assert.True(s("firstTokenMs").GetValue(Of Long)() < s("responseMs").GetValue(Of Long)())
                Dim tps = s("tps").GetValue(Of Double)()
                Assert.True(tps > 0 AndAlso tps < 20, s.ToJsonString())   ' 3 tokens over about 0.4 s of writing (longer on a busy machine)
            End Using
        End Using
        ' Agents: their calls through the model gateway.
        Dim p As New FakeAIProvider() With {.Delay = Function(r) TimeSpan.FromMilliseconds(250), .Responder = Function(r) FakeAIProvider.Text("hi")}
        Using host = Await TestHost.StartAsync(provider:=p)
            Dim outcome = Await host.H.Gateway.ChatAsync(New AiCallContext With {.AgentId = "A", .Profile = host.H.Profiles.TryGetProfile("A")},
                                                         New List(Of ChatMessage) From {ChatMessage.ForUser("hello")}, Nothing, CancellationToken.None)
            For i = 0 To 150
                If CLng(Await host.H.Database.ScalarAsync("SELECT COUNT(*) FROM ModelSpeed WHERE Source='agents'")) > 0 Then Exit For
                Await Task.Delay(100)
            Next
            Dim speed = (Await host.H.ModelHealth.SnapshotAsync())(outcome.ModelId).Speed
            Assert.NotNull(speed)
            Assert.Null(speed.FirstTokenMs)   ' the fake provider does not stream
            Assert.True(speed.ResponseMs >= 200, $"{speed.ResponseMs}")   ' a 250 ms wait (timers can end a little early)
            Assert.True(speed.Tps > 0 AndAlso speed.Tps <= 600, $"{speed.Tps}")   ' 100 tokens in about 0.25 s (timers can end a little early)
        End Using
    End Function

End Class
