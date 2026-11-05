Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Immutable
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Security
Imports Xunit

''' <summary>My models (bring your own API key): encryption, the provider translators, privacy and agent access.</summary>
Public Class ByoModelTests

    ''' <summary>A transport that records requests and answers with a canned response.</summary>
    Private NotInheritable Class CaptureHandler
        Inherits HttpMessageHandler
        Public ReadOnly Requests As New List(Of (Request As HttpRequestMessage, Body As String))
        Public Property Reply As Func(Of HttpRequestMessage, String, HttpResponseMessage)
        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim body = If(request.Content Is Nothing, "", Await request.Content.ReadAsStringAsync(ct))
            Requests.Add((request, body))
            Return Reply(request, body)
        End Function
    End Class

    Private Shared Function Json(status As HttpStatusCode, text As String, Optional media As String = "application/json") As HttpResponseMessage
        Return New HttpResponseMessage(status) With {.Content = New StringContent(text, Encoding.UTF8, media)}
    End Function

    Private Shared Function Service(capture As CaptureHandler, m As ByoModel) As ByoService
        Dim s As New ByoService(Nothing, New SecretBox(New Byte(31) {}), Nothing, Nothing, Nothing, capture)
        s.PutForTests(m)
        Return s
    End Function

    Private Shared ReadOnly ToolTurn As String =
        "{""model"":""byo-test"",""stream"":false,""max_tokens"":100000,""temperature"":0.3," &
        """messages"":[{""role"":""system"",""content"":""Be brief.""},{""role"":""user"",""content"":""Weather?""}," &
        "{""role"":""assistant"",""content"":null,""tool_calls"":[{""id"":""call_1"",""type"":""function"",""function"":{""name"":""weather"",""arguments"":""{\""city\"":\""Oslo\""}""}}]}," &
        "{""role"":""tool"",""tool_call_id"":""call_1"",""content"":""12 C""}]," &
        """tools"":[{""type"":""function"",""function"":{""name"":""weather"",""description"":""Weather now"",""parameters"":{""type"":""object"",""properties"":{""city"":{""type"":""string""}}}}}]}"

    <Fact>
    Public Sub SecretsAreEncryptedAndTamperingIsRefused()
        Dim key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)
        Dim box As New SecretBox(key)
        Dim sealed = box.Protect("sk-secret-1234")
        Assert.StartsWith("v1:", sealed)
        Assert.DoesNotContain("sk-secret", sealed)
        Assert.Equal("sk-secret-1234", box.Unprotect(sealed))
        ' Another key, or a changed byte, cannot read it.
        Assert.Null(New SecretBox(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).Unprotect(sealed))
        Dim raw = Convert.FromBase64String(sealed.Substring(3))
        raw(raw.Length - 1) = raw(raw.Length - 1) Xor CByte(1)
        Assert.Null(box.Unprotect("v1:" & Convert.ToBase64String(raw)))
    End Sub

    <Fact>
    Public Async Function AnthropicRequestsAndAnswersAreTranslated() As Task
        Dim capture As New CaptureHandler With {.Reply = Function(req, body) Json(HttpStatusCode.OK,
            "{""id"":""msg_1"",""type"":""message"",""role"":""assistant"",""stop_reason"":""tool_use"",""usage"":{""input_tokens"":30,""output_tokens"":9}," &
            """content"":[{""type"":""thinking"",""thinking"":""Need the tool.""},{""type"":""text"",""text"":""Checking.""},{""type"":""tool_use"",""id"":""toolu_9"",""name"":""weather"",""input"":{""city"":""Bergen""}}]}")}
        Dim svc = Service(capture, New ByoModel With {.Id = "byo-test", .Owner = "alice", .Name = "Claude", .Provider = "anthropic", .ModelName = "claude-sonnet-4-5",
                                                       .MaxTokens = 4096, .Enabled = True, .Secret = New ByoSecret With {.ApiKey = "sk-ant-xyz"}})
        Using resp = Await svc.SendAsync(ToolTurn, CancellationToken.None)
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
            Dim sent = capture.Requests.Single()
            Assert.Equal("https://api.anthropic.com/v1/messages", sent.Request.RequestUri.ToString())
            Assert.Equal("sk-ant-xyz", sent.Request.Headers.GetValues("x-api-key").Single())
            Assert.Equal("2023-06-01", sent.Request.Headers.GetValues("anthropic-version").Single())
            Dim a = JsonNode.Parse(sent.Body)
            Assert.Equal("claude-sonnet-4-5", a("model").ToString())
            Assert.Equal("Be brief.", a("system").ToString())
            Assert.Equal(4096, a("max_tokens").GetValue(Of Integer)())      ' capped at the model's maximum
            Dim msgs = a("messages").AsArray()
            Assert.Equal({"user", "assistant", "user"}, msgs.Select(Function(m) m("role").ToString()).ToArray())
            Assert.Equal("tool_use", msgs(1)("content")(0)("type").ToString())
            Assert.Equal("Oslo", msgs(1)("content")(0)("input")("city").ToString())
            Assert.Equal("tool_result", msgs(2)("content")(0)("type").ToString())
            Assert.Equal("call_1", msgs(2)("content")(0)("tool_use_id").ToString())
            Assert.Equal("object", a("tools")(0)("input_schema")("type").ToString())

            Dim o = JsonNode.Parse(Await resp.Content.ReadAsStringAsync())
            Dim msg = o("choices")(0)("message")
            Assert.Equal("Checking.", msg("content").ToString())
            Assert.Equal("Need the tool.", msg("reasoning_content").ToString())
            Assert.Equal("toolu_9", msg("tool_calls")(0)("id").ToString())
            Assert.Equal("Bergen", JsonNode.Parse(msg("tool_calls")(0)("function")("arguments").ToString())("city").ToString())
            Assert.Equal("tool_calls", o("choices")(0)("finish_reason").ToString())
            Assert.Equal(30, o("usage")("prompt_tokens").GetValue(Of Integer)())
        End Using
    End Function

    <Fact>
    Public Async Function AnthropicStreamsBecomeOpenAiChunks() As Task
        Dim events = String.Join(vbLf & vbLf, {
            "event: message_start" & vbLf & "data: {""type"":""message_start"",""message"":{""usage"":{""input_tokens"":12}}}",
            "data: {""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""text"",""text"":""""}}",
            "data: {""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""text_delta"",""text"":""Hel""}}",
            "data: {""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""text_delta"",""text"":""lo""}}",
            "data: {""type"":""content_block_start"",""index"":1,""content_block"":{""type"":""tool_use"",""id"":""toolu_1"",""name"":""weather""}}",
            "data: {""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""input_json_delta"",""partial_json"":""{\""city\"":""}}",
            "data: {""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""input_json_delta"",""partial_json"":""\""Oslo\""}""}}",
            "data: {""type"":""message_delta"",""delta"":{""stop_reason"":""tool_use""},""usage"":{""output_tokens"":7}}",
            "data: {""type"":""message_stop""}"}) & vbLf & vbLf
        Dim capture As New CaptureHandler With {.Reply = Function(req, body) Json(HttpStatusCode.OK, events, "text/event-stream")}
        Dim svc = Service(capture, New ByoModel With {.Id = "byo-test", .Owner = "alice", .Name = "Claude", .Provider = "anthropic", .ModelName = "claude-x",
                                                       .MaxTokens = 4096, .Enabled = True, .Secret = New ByoSecret With {.ApiKey = "k"}})
        Using resp = Await svc.SendAsync(ToolTurn.Replace("""stream"":false", """stream"":true"), CancellationToken.None)
            Dim text = Await resp.Content.ReadAsStringAsync()
            Dim chunks = text.Split({vbLf & vbLf}, StringSplitOptions.RemoveEmptyEntries).Select(Function(l) l.Substring("data: ".Length)).ToList()
            Assert.Equal("[DONE]", chunks.Last())
            Dim parsed = chunks.Take(chunks.Count - 1).Select(Function(c) JsonNode.Parse(c)).ToList()
            Dim content = String.Concat(parsed.Select(Function(c) c("choices")?.AsArray().FirstOrDefault()?("delta")?("content")?.ToString()))
            Assert.Equal("Hello", content)
            Dim calls = parsed.Select(Function(c) c("choices")?.AsArray().FirstOrDefault()?("delta")?("tool_calls")).Where(Function(x) x IsNot Nothing).ToList()
            Assert.Equal("toolu_1", calls(0)(0)("id").ToString())
            Assert.Equal("weather", calls(0)(0)("function")("name").ToString())
            Assert.Equal("{""city"":""Oslo""}", String.Concat(calls.Select(Function(c) c(0)("function")("arguments")?.ToString())))
            Assert.Contains(parsed, Function(c) c("choices")?.AsArray().FirstOrDefault()?("finish_reason")?.ToString() = "tool_calls")
            Dim usage = parsed.Last()("usage")
            Assert.Equal(12, usage("prompt_tokens").GetValue(Of Integer)())
            Assert.Equal(7, usage("completion_tokens").GetValue(Of Integer)())
        End Using
    End Function

    <Fact>
    Public Async Function BedrockUsesConverseWithAnApiKeyOrSignedIamKeys() As Task
        Dim capture As New CaptureHandler With {.Reply = Function(req, body) Json(HttpStatusCode.OK,
            "{""output"":{""message"":{""role"":""assistant"",""content"":[{""text"":""Sunny.""}]}},""stopReason"":""end_turn"",""usage"":{""inputTokens"":20,""outputTokens"":2}}")}
        Dim m As New ByoModel With {.Id = "byo-test", .Owner = "alice", .Name = "Bedrock", .Provider = "bedrock", .ModelName = "anthropic.claude-3-5-sonnet-20240620-v1:0",
                                    .Region = "us-west-2", .MaxTokens = 2048, .Enabled = True, .Secret = New ByoSecret With {.ApiKey = "bedrock-key"}}
        Dim svc = Service(capture, m)
        Using resp = Await svc.SendAsync(ToolTurn, CancellationToken.None)
            Dim sent = capture.Requests.Single()
            Assert.Equal("https://bedrock-runtime.us-west-2.amazonaws.com/model/anthropic.claude-3-5-sonnet-20240620-v1%3A0/converse", sent.Request.RequestUri.AbsoluteUri)
            Assert.Equal("Bearer bedrock-key", sent.Request.Headers.Authorization.ToString())
            Dim b = JsonNode.Parse(sent.Body)
            Assert.Equal("Be brief.", b("system")(0)("text").ToString())
            Assert.Equal(2048, b("inferenceConfig")("maxTokens").GetValue(Of Integer)())
            Assert.Equal("weather", b("toolConfig")("tools")(0)("toolSpec")("name").ToString())
            Assert.Equal("call_1", b("messages")(1)("content")(0)("toolUse")("toolUseId").ToString())
            Assert.Equal("12 C", b("messages")(2)("content")(0)("toolResult")("content")(0)("text").ToString())
            Dim o = JsonNode.Parse(Await resp.Content.ReadAsStringAsync())
            Assert.Equal("Sunny.", o("choices")(0)("message")("content").ToString())
            Assert.Equal("stop", o("choices")(0)("finish_reason").ToString())
        End Using

        ' IAM keys: SigV4, scoped to the region and the bedrock service, with the session token signed too.
        m.Secret = New ByoSecret With {.AccessKeyId = "AKIDEXAMPLE", .SecretAccessKey = "secret", .SessionToken = "tok"}
        capture.Requests.Clear()
        Using resp = Await svc.SendAsync(ToolTurn, CancellationToken.None)
            Dim auth = capture.Requests.Single().Request.Headers.GetValues("Authorization").Single()
            Assert.Matches("^AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/\d{8}/us-west-2/bedrock/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date;x-amz-security-token, Signature=[0-9a-f]{64}$", auth)
            Assert.Equal("tok", capture.Requests.Single().Request.Headers.GetValues("x-amz-security-token").Single())
        End Using
    End Function

    <Fact>
    Public Async Function ProviderErrorsKeepTheirMessageSoContextOverflowIsRecognised() As Task
        Dim capture As New CaptureHandler With {.Reply = Function(req, body) Json(HttpStatusCode.BadRequest,
            "{""type"":""error"",""error"":{""type"":""invalid_request_error"",""message"":""prompt is too long: 210000 tokens > 200000 maximum""}}")}
        Dim svc = Service(capture, New ByoModel With {.Id = "byo-test", .Owner = "alice", .Name = "C", .Provider = "anthropic", .ModelName = "c", .MaxTokens = 100, .Enabled = True, .Secret = New ByoSecret With {.ApiKey = "k"}})
        Using resp = Await svc.SendAsync(ToolTurn, CancellationToken.None)
            Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode)
            Dim message = JsonNode.Parse(Await resp.Content.ReadAsStringAsync())("error")("message").ToString()
            Assert.Equal("Anthropic: prompt is too long: 210000 tokens > 200000 maximum", message)
            Assert.True(Work.ChatService.IsContextOverflow(message))
        End Using
    End Function

    <Fact>
    Public Async Function OnlyTheOwnerSeesAndUsesTheirModelsAndTheyAreFree() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                For Each name In {"alice", "bob"}
                    Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users",
                        New With {.username = name, .password = name & "-pass-1234", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Next
                Dim alice = Await PlatformApiTests.LoginAsync(env.Port, "alice", "alice-pass-1234")
                Dim bob = Await PlatformApiTests.LoginAsync(env.Port, "bob", "bob-pass-1234")

                ' Alice adds an OpenAI-compatible model (the fake upstream stands in for her provider).
                Dim res = Await PlatformApiTests.Send(alice.Client, "POST", "/api/chat/my-models",
                    New With {.provider = "openai", .name = "Alice GPT", .modelName = "alice-upstream", .baseUrl = up.BaseUrl, .apiKey = "sk-alice-secret-9876"}, alice.Csrf)
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Dim created = JsonNode.Parse(Await res.Content.ReadAsStringAsync())
                Dim id = created("id").ToString()
                Assert.StartsWith("byo-", id)
                Assert.Equal("…9876", created("keyHint").ToString())
                Dim listText = Await alice.Client.GetStringAsync("/api/chat/my-models")
                Assert.Contains(id, listText)
                Assert.DoesNotContain("sk-alice-secret", listText)     ' keys are write-only
                ' Stored encrypted.
                Dim stored = Await env.Host.H.Database.QueryAsync("SELECT SecretJson FROM UserModels", Function(r) Persistence.Db.Str(r, "SecretJson"))
                Assert.DoesNotContain("sk-alice-secret", stored.Single())

                ' Alice sees it in Chat and in the API; Bob does not, nor do administrators' model lists.
                Assert.Contains(id, Await alice.Client.GetStringAsync("/api/chat/meta"))
                Assert.Contains(id, Await alice.Client.GetStringAsync("/v1/models"))
                Assert.DoesNotContain(id, Await bob.Client.GetStringAsync("/api/chat/meta"))
                Assert.DoesNotContain(id, Await bob.Client.GetStringAsync("/v1/models"))
                Assert.DoesNotContain(id, Await bob.Client.GetStringAsync("/api/chat/my-models"))
                Assert.DoesNotContain(id, Await admin.Client.GetStringAsync("/api/models"))

                ' Test, then chat with it: answered by her provider, and free (her balance is $0 and nothing is blocked).
                Dim test = JsonNode.Parse(Await (Await PlatformApiTests.Send(alice.Client, "POST", $"/api/chat/my-models/{id}/test", New With {.x = 1}, alice.Csrf)).Content.ReadAsStringAsync())
                Assert.True(test("ok").GetValue(Of Boolean)(), test.ToJsonString())
                Dim chatId = Await ChatFeatureTests.NewChatAsync(alice, New With {.model = id})
                Dim sse = Await ChatFeatureTests.AskAsync(alice, chatId, New With {.content = "hi"})
                Assert.Equal("answer from alice-upstream", ChatFeatureTests.Done(sse)("message")("content").ToString())
                Assert.Contains(up.Requests, Function(r) r("model").ToString() = "alice-upstream")
                Dim ledger = Await env.Host.H.Database.QueryAsync("SELECT Model FROM AccountLedger WHERE Username='alice'", Function(r) Persistence.Db.Str(r, "Model"))
                Assert.DoesNotContain(id, ledger)

                ' Bob cannot use, change, test or delete it.
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(bob.Client, "POST", "/api/chat/conversations", New With {.model = id}, bob.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(bob.Client, "PUT", $"/api/chat/my-models/{id}", New With {.name = "mine"}, bob.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(bob.Client, "POST", $"/api/chat/my-models/{id}/test", New With {.x = 1}, bob.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(bob.Client, "DELETE", $"/api/chat/my-models/{id}", Nothing, bob.Csrf)).StatusCode)

                ' Agents: only an agent Alice owns, with the model in its allowed models.
                Dim h = env.Host.H
                h.ModelAccess.AttachOwners(Function(agentId) If(agentId = "alice-agent", "alice", If(agentId = "bob-agent", "bob", Nothing)))
                Dim m = h.ModelRegistry.TryGet(id)
                Assert.NotNull(m)
                Dim profile = Function(agentId As String, models As String()) New EffectiveProfile With {.Id = agentId, .MaxTier = 9, .AllowedModels = ImmutableHashSet.CreateRange(StringComparer.OrdinalIgnoreCase, models)}
                Assert.Equal(ModelAccessKind.Profile, h.ModelAccess.AccessFor(profile("alice-agent", {id}), m))
                Assert.True(h.Selection.IsUsableBy(m, profile("alice-agent", {id}), Nothing))
                Assert.Equal(ModelAccessKind.None, h.ModelAccess.AccessFor(profile("alice-agent", {"fast/a"}), m))
                Assert.Equal(ModelAccessKind.None, h.ModelAccess.AccessFor(profile("bob-agent", {id}), m))
                Assert.True(h.Pricing.GetPrice(id).IsPriced)                     ' free: never refused for want of a price

                ' Deleting removes it everywhere.
                Assert.Equal(HttpStatusCode.OK, (Await PlatformApiTests.Send(alice.Client, "DELETE", $"/api/chat/my-models/{id}", Nothing, alice.Csrf)).StatusCode)
                Assert.DoesNotContain(id, Await alice.Client.GetStringAsync("/api/chat/meta"))
                Assert.Null(h.ModelRegistry.TryGet(id))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function InvalidModelsAreRefusedWithAReason() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim bad = Async Function(body As Object) As Task(Of String)
                              Dim r = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/my-models", body, admin.Csrf)
                              Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode)
                              Return Await r.Content.ReadAsStringAsync()
                          End Function
                Assert.Contains("Provider must be", Await bad(New With {.provider = "gemini", .modelName = "x", .apiKey = "k"}))
                Assert.Contains("API key", Await bad(New With {.provider = "anthropic", .modelName = "claude"}))
                Assert.Contains("region", Await bad(New With {.provider = "bedrock", .modelName = "m", .apiKey = "k"}))
                Assert.Contains("http(s) URL", Await bad(New With {.provider = "openai", .modelName = "m", .apiKey = "k", .baseUrl = "ftp://x"}))
                ' A local OpenAI-compatible server needs no key.
                Assert.Equal(HttpStatusCode.OK, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/my-models",
                    New With {.provider = "openai", .modelName = "llama", .baseUrl = "http://127.0.0.1:1234/v1"}, admin.Csrf)).StatusCode)
            End Using
        End Using
    End Function

End Class

''' <summary>Live event streams: a per-account limit that replaces that account's oldest stream, never refusing others.</summary>
Public Class EventStreamLimitTests

    <Fact>
    Public Async Function AnAccountsNewestWindowReplacesItsOldestStreamAndOthersStillConnect() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Server.MaxSseClientsPerUser = 2
                                                                o.Server.MaxSseClients = 3
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users",
                    New With {.username = "dana", .password = "dana-pass-1234", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Dim dana = Await PlatformApiTests.LoginAsync(env.Port, "dana", "dana-pass-1234")
                Dim open = Async Function(c As HttpClient) As Task(Of (Status As HttpStatusCode, Body As IO.Stream))
                               Dim r = Await c.SendAsync(New HttpRequestMessage(HttpMethod.Get, "/api/events/stream"), HttpCompletionOption.ResponseHeadersRead)
                               Return (r.StatusCode, If(r.IsSuccessStatusCode, Await r.Content.ReadAsStreamAsync(), Nothing))
                           End Function
                Dim first = Await open(admin.Client)
                Dim second = Await open(admin.Client)
                Dim third = Await open(admin.Client)      ' one more than the account may hold: replaces the first
                Assert.Equal(HttpStatusCode.OK, third.Status)
                ' The first stream ends (it read ": connected" and then the server closed it).
                Dim buf(4095) As Byte
                Using cts As New CancellationTokenSource(TimeSpan.FromSeconds(10))
                    Dim total = 0
                    While True
                        Dim n = Await first.Body.ReadAsync(buf, 0, buf.Length, cts.Token)
                        If n = 0 Then Exit While
                        total += n
                    End While
                    Assert.True(total > 0)
                End Using
                ' Another account still connects (the server-wide limit counts the replaced stream as gone).
                Dim other = Await open(dana.Client)
                Assert.Equal(HttpStatusCode.OK, other.Status)
                ' The server-wide safety net still holds: 3 streams are open now.
                Dim over = Await open(dana.Client)
                Assert.Equal(HttpStatusCode.ServiceUnavailable, over.Status)
                second.Body.Dispose() : third.Body.Dispose() : other.Body.Dispose()
            End Using
        End Using
    End Function

End Class
