Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports Agentic.Configuration
Imports Xunit

''' <summary>
''' Slow model calls behind a proxy (Cloudflare drops a response that is silent for 100-120 s: HTTP 524). Past a grace
''' period the model APIs (/v1/messages, /v1/chat/completions) send their headers and keep the connection alive; quick
''' answers and quick errors are unchanged.
''' </summary>
Public Class ModelKeepAliveTests

    ''' <summary>A short grace period and keep-alive gap, so each test runs in about a second.</summary>
    Private Shared Sub QuickKeepAlive(o As HostOptions)
        o.Server.ModelKeepAliveAfterSeconds = 0.2
        o.Server.ModelKeepAliveIntervalSeconds = 0.2
    End Sub

    Private Shared Async Function PostAsync(user As (Client As HttpClient, Csrf As String), path As String, body As Object) As Task(Of HttpResponseMessage)
        Dim req As New HttpRequestMessage(HttpMethod.Post, path) With {.Content = HttpApiTests.JsonBody(body)}
        req.Headers.Add("X-CSRF-Token", user.Csrf)
        Return Await user.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
    End Function

    <Fact>
    Public Async Function SlowAnthropicStreamIsPingedThenAnswers() As Task
        Using up As New FakeUpstream()
            up.Delay = TimeSpan.FromMilliseconds(900)
            Dim env = Await PlatformApiTests.StartAsync(up, AddressOf QuickKeepAlive)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/messages", New With {.model = "fast/a", .max_tokens = 50, .stream = True,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Dim sse = Await res.Content.ReadAsStringAsync()
                ' Pings kept the line busy while the model was slow to start; then the answer as usual.
                Assert.Contains("event: ping", sse)
                Assert.True(sse.IndexOf("event: ping", StringComparison.Ordinal) < sse.IndexOf("event: message_start", StringComparison.Ordinal), sse)
                Assert.Contains("text_delta", sse)
                Assert.Contains("event: message_stop", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SlowAnthropicAnswerIsPaddedAndStillParses() As Task
        Using up As New FakeUpstream()
            up.Delay = TimeSpan.FromMilliseconds(900)
            Dim env = Await PlatformApiTests.StartAsync(up, AddressOf QuickKeepAlive)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/messages", New With {.model = "fast/a", .max_tokens = 50,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Dim raw = Await res.Content.ReadAsStringAsync()
                Assert.StartsWith(" ", raw)                                    ' kept alive with JSON's leading whitespace
                Dim m = JsonDocument.Parse(raw).RootElement
                Assert.Equal("message", m.GetProperty("type").GetString())
                Assert.Contains("answer from fast/a", m.GetProperty("content")(0).GetProperty("text").GetString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SlowFailureBecomesAnErrorEvent() As Task
        Using up As New FakeUpstream()
            up.Delay = TimeSpan.FromMilliseconds(900)
            up.ErrorOverride = Function(req) "the request exceeds the available context size"
            Dim env = Await PlatformApiTests.StartAsync(up, AddressOf QuickKeepAlive)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/messages", New With {.model = "fast/a", .max_tokens = 50, .stream = True,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Dim sse = Await res.Content.ReadAsStringAsync()
                ' The status had gone out, so the failure is an error event (the Anthropic SDKs raise it).
                Assert.Contains("event: error", sse)
                Assert.Contains("invalid_request_error", sse)
                ' The kind of failure in a few words, not the provider's text.
                Assert.Contains("The conversation is too long for this model.", sse)
                Assert.DoesNotContain("exceeds the available context size", sse)
                Assert.DoesNotContain("message_start", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function QuickFailureKeepsItsStatus() As Task
        Using up As New FakeUpstream()
            up.ErrorOverride = Function(req) "the request exceeds the available context size"
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/messages", New With {.model = "fast/a", .max_tokens = 50, .stream = True,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode)
                Dim e = JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement
                Assert.Equal("error", e.GetProperty("type").GetString())
                Assert.Equal("invalid_request_error", e.GetProperty("error").GetProperty("type").GetString())
                Assert.Equal("The conversation is too long for this model.", e.GetProperty("error").GetProperty("message").GetString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function QuietStreamIsPinged() As Task
        Using up As New FakeUpstream()
            up.ChunkDelay = TimeSpan.FromMilliseconds(700)
            Dim env = Await PlatformApiTests.StartAsync(up, AddressOf QuickKeepAlive)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/messages", New With {.model = "fast/a", .max_tokens = 50, .stream = True,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Dim sse = Await res.Content.ReadAsStringAsync()
                ' The model went quiet between parts of its answer: pings in between, and the whole answer.
                Dim start = sse.IndexOf("event: message_start", StringComparison.Ordinal)
                Assert.True(start >= 0, sse)
                Assert.True(sse.IndexOf("event: ping", start, StringComparison.Ordinal) > start, sse)
                Assert.Contains("from ", sse)
                Assert.Contains("event: message_stop", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SlowOpenAiStreamGetsCommentsThenAnswers() As Task
        Using up As New FakeUpstream()
            up.Delay = TimeSpan.FromMilliseconds(900)
            Dim env = Await PlatformApiTests.StartAsync(up, AddressOf QuickKeepAlive)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/chat/completions", New With {.model = "fast/a", .stream = True,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Dim sse = Await res.Content.ReadAsStringAsync()
                Assert.Contains(": keep-alive", sse)
                Assert.True(sse.IndexOf(": keep-alive", StringComparison.Ordinal) < sse.IndexOf("data:", StringComparison.Ordinal), sse)
                Assert.Contains("answer ", sse)
                Assert.Contains("data: [DONE]", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SlowOpenAiAnswerIsPaddedAndStillParses() As Task
        Using up As New FakeUpstream()
            up.Delay = TimeSpan.FromMilliseconds(900)
            Dim env = Await PlatformApiTests.StartAsync(up, AddressOf QuickKeepAlive)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, "/v1/chat/completions", New With {.model = "fast/a",
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Dim raw = Await res.Content.ReadAsStringAsync()
                Assert.StartsWith(" ", raw)
                Dim c = JsonDocument.Parse(raw).RootElement
                Assert.Contains("answer from fast/a", c.GetProperty("choices")(0).GetProperty("message").GetProperty("content").GetString())
            End Using
        End Using
    End Function

End Class
