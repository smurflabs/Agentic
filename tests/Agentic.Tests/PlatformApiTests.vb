Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Http
Imports Agentic.Logging
Imports Agentic.Security
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>A stand-in for OpenWebUI: lists two models and answers chat completions (plain or streamed) naming the model used.</summary>
Friend NotInheritable Class FakeUpstream
    Implements IDisposable
    Private ReadOnly _listener As New HttpListener()
    Private ReadOnly _cts As New CancellationTokenSource()
    Public ReadOnly Property Port As Integer
    Public ReadOnly Property Requests As New Concurrent.ConcurrentQueue(Of JsonNode)
    ''' <summary>Model ids that answer 503 (to exercise fallbacks).</summary>
    Public ReadOnly Property Down As New HashSet(Of String)
    ''' <summary>Optional: a full server-sent-events reply for a streamed chat request (Nothing = the default answer).</summary>
    Public Property StreamOverride As Func(Of JsonNode, String)
    ''' <summary>Optional: the text of a non-streamed answer (Nothing = "answer from model").</summary>
    Public Property TextOverride As Func(Of JsonNode, String)
    ''' <summary>Refuse requests that carry reasoning_effort (like a model without reasoning).</summary>
    Public Property RejectReasoning As Boolean
    ''' <summary>Optional: a 400 error message for a request (Nothing = answer it normally).</summary>
    Public Property ErrorOverride As Func(Of JsonNode, String)
    ''' <summary>Optional: the raw body (status 200, text/html) of a non-streamed answer, e.g. a proxy's error page (Nothing = answer normally).</summary>
    Public Property RawNonStreamOverride As Func(Of JsonNode, String)
    ''' <summary>How long a chat completion waits before answering (a busy or slow local model).</summary>
    Public Property Delay As TimeSpan
    ''' <summary>A pause before each part of the default streamed answer (a model that goes quiet mid-answer).</summary>
    Public Property ChunkDelay As TimeSpan
    ''' <summary>More model ids the provider lists (e.g. a provider's copy of a Claude model, "prov/claude-sonnet-5").</summary>
    Public ReadOnly Property ExtraModels As New List(Of String)

    Public Sub New()
        Port = HttpApiTests.FreePort()
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/")
        _listener.Start()
        Task.Run(AddressOf LoopAsync)
    End Sub

    Public ReadOnly Property BaseUrl As String
        Get
            Return $"http://127.0.0.1:{Port}/api"
        End Get
    End Property

    Private Async Function LoopAsync() As Task
        While Not _cts.IsCancellationRequested
            Dim ctx As HttpListenerContext
            Try
                ctx = Await _listener.GetContextAsync()
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                Return
            End Try
            Dim unused = HandleAsync(ctx)
        End While
    End Function

    Private Async Function HandleAsync(ctx As HttpListenerContext) As Task
        Dim res = ctx.Response
        Dim path = ctx.Request.Url.AbsolutePath
        If path = "/api/models" Then
            Dim extra = String.Concat(ExtraModels.Select(Function(id) ",{""id"":""" & id & """,""name"":""" & id & """,""owned_by"":""extra""}"))
            Await WriteAsync(res, 200, "application/json", "{""data"":[{""id"":""fast/a"",""name"":""Fast A"",""owned_by"":""fast""},{""id"":""code/b"",""name"":""Code B"",""owned_by"":""code""},{""id"":""arena-model"",""name"":""Arena Model"",""owned_by"":""arena""},{""id"":""bge-embed"",""name"":""Embeddings"",""owned_by"":""embed""}" & extra & "]}")
            Return
        End If
        ' Deterministic embeddings for RAG tests: a 64-dim hashed bag of words, so texts that share words are close.
        If path = "/api/embeddings" Then
            Dim body As String
            Using reader As New IO.StreamReader(ctx.Request.InputStream)
                body = Await reader.ReadToEndAsync()
            End Using
            Dim req = JsonNode.Parse(body)
            Dim inputs = TryCast(req("input"), JsonArray)
            Dim data As New JsonArray()
            Dim idx = 0
            For Each t In If(inputs, New JsonArray())
                data.Add(New JsonObject From {{"object", "embedding"}, {"index", idx}, {"embedding", FakeEmbed(t.ToString())}})
                idx += 1
            Next
            Dim payload As New JsonObject From {{"object", "list"}, {"model", "bge-embed"}, {"data", data}, {"usage", New JsonObject From {{"prompt_tokens", 1}, {"total_tokens", 1}}}}
            Await WriteAsync(res, 200, "application/json", payload.ToJsonString()).ConfigureAwait(False)
            Return
        End If
        If path = "/api/chat/completions" Then
            Dim body As String
            Using reader As New IO.StreamReader(ctx.Request.InputStream)
                body = Await reader.ReadToEndAsync()
            End Using
            Dim req = JsonNode.Parse(body)
            Requests.Enqueue(req)
            If Delay > TimeSpan.Zero Then Await Task.Delay(Delay)
            Dim model = req("model").ToString()
            If Down.Contains(model) Then
                Await WriteAsync(res, 503, "application/json", "{""error"":{""message"":""No available channel""}}")
                Return
            End If
            Dim refusal = If(ErrorOverride Is Nothing, Nothing, ErrorOverride(req))
            If refusal IsNot Nothing Then
                Await WriteAsync(res, 400, "application/json", JsonUtil.ToJson(New With {.detail = refusal}))
                Return
            End If
            If RejectReasoning AndAlso req("reasoning_effort") IsNot Nothing Then
                Await WriteAsync(res, 400, "application/json", "{""error"":{""message"":""Unsupported parameter: 'reasoning_effort' is not supported with this model.""}}")
                Return
            End If
            Dim streamed = req("stream") IsNot Nothing AndAlso req("stream").GetValue(Of Boolean)()
            ' A subagent's request streams too: the overrides meant for it (not for the answer's own stream) apply to it.
            Dim subagent = ChatFeatureTests.IsSubagent(req)
            Dim raw = If((streamed AndAlso Not subagent) OrElse RawNonStreamOverride Is Nothing, Nothing, RawNonStreamOverride(req))
            If raw IsNot Nothing Then
                Await WriteAsync(res, 200, "text/html", raw)
                Return
            End If
            Dim answer = If(If(TextOverride Is Nothing, Nothing, TextOverride(req)), "answer from " & model)
            Dim custom = If(StreamOverride Is Nothing, Nothing, StreamOverride(req))
            If custom IsNot Nothing Then
                Await WriteAsync(res, 200, "text/event-stream", custom)
                Return
            End If
            If req("stream") IsNot Nothing AndAlso req("stream").GetValue(Of Boolean)() Then
                Dim frames As New List(Of String)
                Dim scripted = If(subagent AndAlso TextOverride IsNot Nothing, TextOverride(req), Nothing)
                For Each part In If(scripted Is Nothing, {"answer ", "from ", model}, Text.RegularExpressions.Regex.Split(scripted, "(?<= )").Where(Function(p) p.Length > 0).ToArray())
                    frames.Add("data: " & JsonUtil.ToJson(New With {.id = "c1", .model = model, .choices = {New With {.index = 0, .delta = New With {.content = part}}}}) & vbLf & vbLf)
                Next
                frames.Add("data: " & JsonUtil.ToJson(New With {.id = "c1", .model = model, .choices = {New With {.index = 0, .delta = New With {.content = ""}, .finish_reason = "stop"}},
                                                                 .usage = New With {.prompt_tokens = 5, .completion_tokens = 3}}) & vbLf & vbLf)
                frames.Add("data: [DONE]" & vbLf & vbLf)
                If ChunkDelay > TimeSpan.Zero Then
                    ' The first part goes out at once (with the headers), then the model goes quiet before each next part.
                    res.StatusCode = 200
                    res.ContentType = "text/event-stream"
                    res.SendChunked = True
                    For i = 0 To frames.Count - 1
                        If i > 0 Then Await Task.Delay(ChunkDelay)
                        Dim b = Encoding.UTF8.GetBytes(frames(i))
                        Await res.OutputStream.WriteAsync(b)
                        Await res.OutputStream.FlushAsync()
                    Next
                    res.Close()
                Else
                    Await WriteAsync(res, 200, "text/event-stream", String.Concat(frames))
                End If
            Else
                Await WriteAsync(res, 200, "application/json", JsonUtil.ToJson(New With {.id = "c1", .object = "chat.completion", .model = model,
                    .choices = {New With {.index = 0, .message = New With {.role = "assistant", .content = answer}, .finish_reason = "stop"}},
                    .usage = New With {.prompt_tokens = 5, .completion_tokens = 3, .total_tokens = 8}}))
            End If
            Return
        End If
        ' Speech to text the way OpenWebUI serves it (the OpenAI path, /audio/transcriptions, is not found there).
        If path = "/api/v1/audio/transcriptions" Then
            Using ms As New IO.MemoryStream()
                Await ctx.Request.InputStream.CopyToAsync(ms)
                TranscribedBytes = ms.Length
            End Using
            Await WriteAsync(res, 200, "application/json", "{""text"":""hello from speech""}")
            Return
        End If
        Await WriteAsync(res, 404, "application/json", "{}")
    End Function

    ''' <summary>Size of the last multipart body sent for speech to text.</summary>
    Public Property TranscribedBytes As Long

    Private Shared Async Function WriteAsync(res As HttpListenerResponse, status As Integer, type As String, text As String) As Task
        Dim bytes = Encoding.UTF8.GetBytes(text)
        res.StatusCode = status
        res.ContentType = type
        res.ContentLength64 = bytes.Length
        Await res.OutputStream.WriteAsync(bytes)
        res.Close()
    End Function

    ''' <summary>A 64-dim bag-of-words vector (words hashed into buckets): texts that share words point the same way.</summary>
    Private Shared Function FakeEmbed(txt As String) As JsonArray
        Dim v(63) As Double
        For Each w In System.Text.RegularExpressions.Regex.Split(If(txt, "").ToLowerInvariant(), "[^a-z0-9]+")
            If w.Length = 0 Then Continue For
            Dim h As ULong = 2166136261UL
            For Each c In w
                h = ((h Xor CULng(AscW(c))) * 16777619UL) And &HFFFFFFFFUL
            Next
            v(CInt(h Mod 64UL)) += 1
        Next
        Dim arr As New JsonArray()
        For Each x In v
            arr.Add(x)
        Next
        Return arr
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _listener.Close()
    End Sub
End Class

Public Class PlatformApiTests

    ''' <summary>What a chat user could do before the finer permissions: the migration (0024) grants them all to such accounts.</summary>
    Friend Shared ReadOnly ChatUser As String() = {"models.use", "api.use", "chat.uploads", "chat.web", "chat.images", "chat.video", "chat.knowledge",
                                                   "chat.memory", "chat.share", "chat.devices", "account.apikey", "models.own"}

    Friend Shared Async Function StartAsync(upstream As FakeUpstream, Optional configure As Action(Of HostOptions) = Nothing) As Task(Of (Host As TestHost, Port As Integer))
        Dim port = HttpApiTests.FreePort()
        Dim host = Await TestHost.StartAsync(Sub(o)
                                                 o.Server.Enabled = True
                                                 o.Server.Port = port
                                                 o.Admin.Users.Add(New AdminUserOptions With {.Username = "admin", .PasswordHash = PasswordHasher.Hash("pw-123456789")})
                                                 o.AIProviders("OpenWebUI").BaseUrl = upstream.BaseUrl
                                                 configure?.Invoke(o)
                                             End Sub)
        Return (host, port)
    End Function

    Friend Shared Async Function LoginAsync(port As Integer, user As String, password As String) As Task(Of (Client As HttpClient, Csrf As String))
        Dim c As New HttpClient(New HttpClientHandler With {.CookieContainer = New CookieContainer(), .UseCookies = True}) With {.BaseAddress = New Uri($"http://127.0.0.1:{port}")}
        Dim res = Await c.PostAsync("/api/auth/login", HttpApiTests.JsonBody(New With {.username = user, .password = password}))
        Assert.True(res.IsSuccessStatusCode, Await res.Content.ReadAsStringAsync())
        Return (c, JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString())
    End Function

    Friend Shared Function Send(c As HttpClient, method As String, url As String, Optional body As Object = Nothing, Optional csrf As String = Nothing,
                                 Optional key As String = Nothing) As Task(Of HttpResponseMessage)
        Dim req As New HttpRequestMessage(New HttpMethod(method), url)
        If body IsNot Nothing Then req.Content = If(TypeOf body Is String, New StringContent(CStr(body), Encoding.UTF8, "application/json"), HttpApiTests.JsonBody(body))
        If csrf IsNot Nothing Then req.Headers.Add("X-CSRF-Token", csrf)
        If key IsNot Nothing Then req.Headers.Authorization = New Headers.AuthenticationHeaderValue("Bearer", key)
        Return c.SendAsync(req)
    End Function

    Friend Shared Async Function NewKeyAsync(c As HttpClient, csrf As String) As Task(Of String)
        Dim res = Await Send(c, "POST", "/api/auth/api-key", "{}", csrf)
        Assert.Equal(HttpStatusCode.Created, res.StatusCode)
        Return JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()
    End Function

    <Fact>
    Public Async Function AccountsPayModelSquarePricesAndSeeOnlyTheirModels() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                ' fast/a costs $1000 in and $2000 out per 1M tokens (the fake answers with 5 + 3 tokens: $0.011); code/b is free.
                Dim priced = Await Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)
                Assert.True(priced.IsSuccessStatusCode, Await priced.Content.ReadAsStringAsync())
                Dim square = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/model-square")).RootElement
                Assert.True(square.GetProperty("canEdit").GetBoolean())
                Assert.True(square.GetProperty("models").EnumerateArray().First(Function(m) m.GetProperty("id").GetString() = "fast/a").GetProperty("priced").GetBoolean())

                ' A user who may only use fast/a; every account starts at $0.00.
                Assert.Equal(HttpStatusCode.Created, (Await Send(admin.Client, "POST", "/api/users", New With {.username = "payer", .password = "payer-pass-123", .role = "User",
                                                                                                             .permissions = ChatUser, .allowedModels = {"fast/a"}}, admin.Csrf)).StatusCode)
                Dim payer = Await LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim meta = JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/chat/meta")).RootElement
                Assert.Equal({"fast/a"}, meta.GetProperty("providerModels").EnumerateArray().Select(Function(m) m.GetProperty("id").GetString()).ToArray())
                Assert.Equal(1000D, meta.GetProperty("prices").GetProperty("fast/a").GetProperty("input").GetDecimal())
                Assert.Equal(HttpStatusCode.Forbidden, (Await Send(payer.Client, "POST", "/api/chat/conversations", New With {.model = "code/b"}, payer.Csrf)).StatusCode)
                Dim chatId = JsonDocument.Parse(Await (Await Send(payer.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, payer.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim ask = Async Function(model As String) As Task(Of String)
                              Return Await (Await Send(payer.Client, "POST", $"/api/chat/conversations/{chatId}/messages", New With {.content = "hello", .model = model}, payer.Csrf)).Content.ReadAsStringAsync()
                          End Function

                ' An empty balance: a priced model does not answer; a model outside the account's list neither.
                Dim broke = Await ask("fast/a")
                Assert.Contains("event: error", broke)
                Assert.Contains("balance is empty", broke)
                Assert.Contains("do not have access", Await ask("code/b"))

                ' An administrator adds money; the answer is charged at the Model Square price.
                Dim added = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/users/payer/balance", New With {.amount = 1, .note = "first top-up"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Assert.Equal(1000000L, added.GetProperty("balanceMicros").GetInt64())
                Dim paid = Await ask("fast/a")
                Assert.Contains("event: done", paid)
                Assert.Contains("""costMicros"":11000", paid)
                Dim account = JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/account")).RootElement
                Assert.Equal(989000L, account.GetProperty("balanceMicros").GetInt64())
                Assert.Equal({"Chat", "Credit"}, account.GetProperty("ledger").EnumerateArray().Select(Function(e) e.GetProperty("entryType").GetString()).ToArray())

                ' The API offers only the account's models and charges the same way.
                Assert.Equal({"fast/a"}, JsonDocument.Parse(Await payer.Client.GetStringAsync("/v1/models")).RootElement.GetProperty("data").EnumerateArray().
                                             Select(Function(m) m.GetProperty("id").GetString()).ToArray())
                Dim hi = New With {.role = "user", .content = "hi"}
                Assert.Equal(HttpStatusCode.Forbidden, (Await Send(payer.Client, "POST", "/v1/chat/completions", New With {.model = "code/b", .messages = {hi}}, payer.Csrf)).StatusCode)
                Assert.True((Await Send(payer.Client, "POST", "/v1/chat/completions", New With {.model = "fast/a", .messages = {hi}}, payer.Csrf)).IsSuccessStatusCode)
                Assert.Equal(978000L, JsonDocument.Parse(Await payer.Client.GetStringAsync("/api/account")).RootElement.GetProperty("balanceMicros").GetInt64())

                ' Administrators are never charged.
                Dim adminChat = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Assert.Contains("event: done", Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{adminChat}/messages", New With {.content = "hello", .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync())
                Dim mine = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/account")).RootElement
                Assert.False(mine.GetProperty("charged").GetBoolean())
                Assert.Equal(0L, mine.GetProperty("balanceMicros").GetInt64())
                Assert.Contains(mine.GetProperty("accounts").EnumerateArray(), Function(a) a.GetProperty("username").GetString() = "payer" AndAlso a.GetProperty("balanceMicros").GetInt64() = 978000L)
                ' Only administrators set prices and add money.
                Assert.Equal(HttpStatusCode.Forbidden, (Await Send(payer.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 0}}}, payer.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Forbidden, (Await Send(payer.Client, "POST", "/api/users/payer/balance", New With {.amount = 5}, payer.Csrf)).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ApiKeysActWithTheAccountsPermissions() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.False(JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/auth/api-key")).RootElement.GetProperty("hasKey").GetBoolean())
                Dim adminKey = Await NewKeyAsync(admin.Client, admin.Csrf)
                Assert.StartsWith("agk_", adminKey)
                ' Only the prefix is ever shown again.
                Dim status = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/auth/api-key")).RootElement
                Assert.Equal(adminKey.Substring(0, 12), status.GetProperty("prefix").GetString())

                Using anon As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    ' A key needs no CSRF token and works as Bearer or x-api-key; a wrong key is 401.
                    Dim me1 = Await Send(anon, "GET", "/api/auth/me", key:=adminKey)
                    Assert.Equal(HttpStatusCode.OK, me1.StatusCode)
                    Dim viaHeader As New HttpRequestMessage(HttpMethod.Get, "/api/agents")
                    viaHeader.Headers.Add("x-api-key", adminKey)
                    Assert.Equal(HttpStatusCode.OK, (Await anon.SendAsync(viaHeader)).StatusCode)
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await Send(anon, "GET", "/api/agents", key:="agk_" & New String("x"c, 36))).StatusCode)
                    Assert.Equal(HttpStatusCode.OK, (Await Send(anon, "POST", "/api/agents/A/pause", "{}", key:=adminKey)).StatusCode)

                    ' Without "Personal API key" a user cannot make a key.
                    Assert.Equal(HttpStatusCode.Created, (Await Send(admin.Client, "POST", "/api/users", New With {.username = "nokey", .password = "nokey-pass-12345", .role = "User", .permissions = {"models.use"}}, admin.Csrf)).StatusCode)
                    Dim nokey = Await LoginAsync(env.Port, "nokey", "nokey-pass-12345")
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(nokey.Client, "POST", "/api/auth/api-key", "{}", nokey.Csrf)).StatusCode)
                    ' Bob may only have a personal API key: his key sees no agents, cannot use models and cannot change anything.
                    Assert.Equal(HttpStatusCode.Created, (Await Send(admin.Client, "POST", "/api/users", New With {.username = "bob", .password = "bob-pass-12345", .role = "User", .permissions = {"account.apikey"}}, admin.Csrf)).StatusCode)
                    Dim bob = Await LoginAsync(env.Port, "bob", "bob-pass-12345")
                    Dim bobKey = Await NewKeyAsync(bob.Client, bob.Csrf)
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(anon, "GET", "/api/agents", key:=bobKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.OK, (Await Send(anon, "GET", "/api/updates", key:=bobKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(anon, "GET", "/v1/models", key:=bobKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(anon, "POST", "/api/agents/A/pause", "{}", key:=bobKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(anon, "GET", "/api/users", key:=bobKey)).StatusCode)
                    ' Granting the model API applies to the key at once.
                    Assert.True((Await Send(admin.Client, "PATCH", "/api/users/bob", New With {.permissions = {"account.apikey", "api.use"}}, admin.Csrf)).IsSuccessStatusCode)
                    Assert.Equal(HttpStatusCode.OK, (Await Send(anon, "GET", "/v1/models", key:=bobKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(anon, "POST", "/api/custom-models", New With {.id = "x/y"}, key:=bobKey)).StatusCode)
                    Assert.Equal(HttpStatusCode.Forbidden, (Await Send(anon, "POST", "/api/chat/run", New With {.language = "python", .code = "print(1)"}, key:=bobKey)).StatusCode)
                    ' The user list shows the key's prefix; an administrator can revoke it.
                    Dim users = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/users")).RootElement.GetProperty("users")
                    Dim bobRow = users.EnumerateArray().First(Function(u) u.GetProperty("username").GetString() = "bob")
                    Assert.Equal(bobKey.Substring(0, 12), bobRow.GetProperty("apiKeyPrefix").GetString())
                    Assert.True((Await Send(admin.Client, "DELETE", "/api/users/bob/api-key", csrf:=admin.Csrf)).IsSuccessStatusCode)
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await Send(anon, "GET", "/api/agents", key:=bobKey)).StatusCode)
                    ' Disabled accounts' keys stop working too (the permission change ended Bob's session, so he signs in again).
                    bob = Await LoginAsync(env.Port, "bob", "bob-pass-12345")
                    Dim bobKey2 = Await NewKeyAsync(bob.Client, bob.Csrf)
                    ' Taking "Personal API key" away stops the key at once.
                    Assert.True((Await Send(admin.Client, "PATCH", "/api/users/bob", New With {.permissions = {"api.use"}}, admin.Csrf)).IsSuccessStatusCode)
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await Send(anon, "GET", "/v1/models", key:=bobKey2)).StatusCode)
                    Assert.True((Await Send(admin.Client, "PATCH", "/api/users/bob", New With {.permissions = {"account.apikey", "api.use"}}, admin.Csrf)).IsSuccessStatusCode)
                    Assert.Equal(HttpStatusCode.OK, (Await Send(anon, "GET", "/v1/models", key:=bobKey2)).StatusCode)
                    Assert.True((Await Send(admin.Client, "PATCH", "/api/users/bob", New With {.disabled = True}, admin.Csrf)).IsSuccessStatusCode)
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await Send(anon, "GET", "/api/agents", key:=bobKey2)).StatusCode)
                End Using
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function OpenAiApiServesProviderAndCustomModelsWithStreaming() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim key = Await NewKeyAsync(admin.Client, admin.Csrf)
                ' Unknown provider models are rejected when saving a custom model.
                Dim bad = Await Send(admin.Client, "POST", "/api/custom-models", New With {.id = "team/router", .name = "Team router",
                    .definition = New With {.defaultModel = "nope/zzz", .mode = "rules"}}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode)
                Dim created = Await Send(admin.Client, "POST", "/api/custom-models", New With {.id = "team/router", .name = "Team router",
                    .definition = New With {.defaultModel = "fast/a", .mode = "rules",
                        .routes = {New With {.category = "coding", .model = "code/b", .fallbacks = {"fast/a"}}}}}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, created.StatusCode)

                Using c As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim models = JsonDocument.Parse(Await (Await Send(c, "GET", "/v1/models", key:=key)).Content.ReadAsStringAsync()).RootElement
                    Dim ids = models.GetProperty("data").EnumerateArray().Select(Function(m) m.GetProperty("id").GetString()).ToList()
                    Assert.Contains("team/router", ids)
                    Assert.Contains("fast/a", ids)
                    Assert.Contains("code/b", ids)
                    Assert.Equal(HttpStatusCode.OK, (Await Send(c, "GET", "/v1/models/team/router", key:=key)).StatusCode)

                    ' A coding question goes to the coding route; the reply carries the custom model's name.
                    Dim res = Await Send(c, "POST", "/v1/chat/completions", New With {.model = "team/router",
                        .messages = {New With {.role = "user", .content = "Fix this python function, it throws an exception: def f(x): return x[0]"}}}, key:=key)
                    Dim text = Await res.Content.ReadAsStringAsync()
                    Assert.True(res.IsSuccessStatusCode, text)
                    Assert.Equal("code/b", res.Headers.GetValues("X-Agentic-Model").First())
                    Dim reply = JsonDocument.Parse(text).RootElement
                    Assert.Equal("team/router", reply.GetProperty("model").GetString())
                    Assert.Equal("answer from code/b", reply.GetProperty("choices")(0).GetProperty("message").GetProperty("content").GetString())

                    ' Anything else goes to the default; when the route's model is down its fallback answers.
                    Dim plain = Await Send(c, "POST", "/v1/chat/completions", New With {.model = "team/router", .messages = {New With {.role = "user", .content = "hello there"}}}, key:=key)
                    Assert.Equal("fast/a", plain.Headers.GetValues("X-Agentic-Model").First())
                    up.Down.Add("code/b")
                    Dim fb = Await Send(c, "POST", "/v1/chat/completions", New With {.model = "team/router",
                        .messages = {New With {.role = "user", .content = "Refactor this C# class and fix the compile error"}}}, key:=key)
                    Assert.True(fb.IsSuccessStatusCode, Await fb.Content.ReadAsStringAsync())
                    Assert.Equal("fast/a", fb.Headers.GetValues("X-Agentic-Model").First())
                    up.Down.Clear()

                    ' Streaming passes through as server-sent events, renamed to the custom model.
                    Dim sreq As New HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions") With {
                        .Content = HttpApiTests.JsonBody(New With {.model = "team/router", .stream = True, .messages = {New With {.role = "user", .content = "debug this javascript bug"}}})}
                    sreq.Headers.Authorization = New Headers.AuthenticationHeaderValue("Bearer", key)
                    Dim sres = Await c.SendAsync(sreq, HttpCompletionOption.ResponseHeadersRead)
                    Assert.Equal("text/event-stream", sres.Content.Headers.ContentType.MediaType)
                    Dim sse = Await sres.Content.ReadAsStringAsync()
                    Assert.Contains("data: [DONE]", sse)
                    Assert.Contains("""model"":""team/router""", sse)
                    Assert.DoesNotContain("""model"":""code/b""", sse)

                    ' Provider models pass straight through; the legacy completions endpoint is converted.
                    Dim direct = JsonDocument.Parse(Await (Await Send(c, "POST", "/api/v1/chat/completions", New With {.model = "fast/a", .messages = {New With {.role = "user", .content = "hi"}}}, key:=key)).Content.ReadAsStringAsync()).RootElement
                    Assert.Equal("answer from fast/a", direct.GetProperty("choices")(0).GetProperty("message").GetProperty("content").GetString())
                    Dim legacy = JsonDocument.Parse(Await (Await Send(c, "POST", "/v1/completions", New With {.model = "fast/a", .prompt = "Say hi"}, key:=key)).Content.ReadAsStringAsync()).RootElement
                    Assert.Equal("text_completion", legacy.GetProperty("object").GetString())
                    Assert.Equal("answer from fast/a", legacy.GetProperty("choices")(0).GetProperty("text").GetString())
                    ' Usage is recorded per user.
                    Dim usage = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/custom-models/usage?hours=24")).RootElement.ToString()
                    Assert.Contains("team/router", usage)
                End Using
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ChatStreamsAnswersAndKeepsConversations() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim meta = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement
                Assert.Contains("fast/a", meta.GetProperty("providerModels").EnumerateArray().Select(Function(m) m.GetProperty("id").GetString()))
                Dim conv = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Dim id = conv.GetProperty("id").GetString()

                Dim res = Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages", New With {.content = "## Hello **there**", .model = "fast/a"}, admin.Csrf)
                Assert.Equal("text/event-stream", res.Content.Headers.ContentType.MediaType)
                Dim sse = Await res.Content.ReadAsStringAsync()
                Dim events = sse.Split({vbLf & vbLf}, StringSplitOptions.RemoveEmptyEntries).
                    Select(Function(e) e.Split(vbLf(0))(0).Replace("event: ", "")).ToList()
                Assert.Equal("user", events(0))
                Assert.Contains("delta", events)
                Assert.Contains("done", events)
                Assert.DoesNotContain("error", events)
                ' The model was asked for usage, and the conversation keeps both messages and a title.
                Assert.True(up.Requests.Any(Function(r) r("stream_options")?("include_usage")?.ToString() = "true"))
                Dim saved = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement
                Dim msgs = saved.GetProperty("messages").EnumerateArray().ToList()
                Assert.Equal(2, msgs.Count)
                Assert.Equal("answer from fast/a", msgs(1).GetProperty("content").GetString())
                Assert.Equal(3, msgs(1).GetProperty("completionTokens").GetInt64())
                Assert.NotEqual("New chat", saved.GetProperty("title").GetString())

                ' Conversations are private to their owner.
                Assert.Equal(HttpStatusCode.Created, (Await Send(admin.Client, "POST", "/api/users", New With {.username = "carol", .password = "carol-pass-123", .role = "User", .permissions = ChatUser}, admin.Csrf)).StatusCode)
                Dim carol = Await LoginAsync(env.Port, "carol", "carol-pass-123")
                Assert.Equal(HttpStatusCode.NotFound, (Await carol.Client.GetAsync($"/api/chat/conversations/{id}")).StatusCode)
                Assert.Equal(0, JsonDocument.Parse(Await carol.Client.GetStringAsync("/api/chat/conversations")).RootElement.GetArrayLength())
                Assert.Equal(HttpStatusCode.OK, (Await Send(admin.Client, "DELETE", $"/api/chat/conversations/{id}", csrf:=admin.Csrf)).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ClientToolsRunOnTheUsersComputerAndTheAnswerContinues() As Task
        Using up As New FakeUpstream()
            ' First round: the model calls the client's tool; second round (after the tool result): it answers with the result.
            up.StreamOverride = Function(req)
                                    Dim msgs = req("messages").AsArray()
                                    Dim toolMsg = msgs.LastOrDefault(Function(m) m("role")?.ToString() = "tool")
                                    Dim sb As New StringBuilder()
                                    If toolMsg Is Nothing Then
                                        Dim hasTool = req("tools")?.AsArray().Any(Function(t) t("function")("name").ToString() = "local_echo")
                                        If hasTool <> True Then Return Nothing
                                        sb.Append("data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.tool_calls = {New With {.index = 0, .id = "call_1", .type = "function",
                                            .function = New With {.name = "local_echo", .arguments = "{""text"":""ping""}"}}}}}}}) & vbLf & vbLf)
                                    Else
                                        sb.Append("data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "tool said " & toolMsg("content").ToString()}}}}) & vbLf & vbLf)
                                    End If
                                    sb.Append("data: [DONE]" & vbLf & vbLf)
                                    Return sb.ToString()
                                End Function
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "echo please", .model = "fast/a", .clientInstructions = "Skills: none.",
                        .clientTools = {New With {.name = "local_echo", .description = "Echo text", .parameters = New With {.type = "object", .properties = New With {.text = New With {.type = "string"}}}}}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim res = Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                Dim events As New List(Of String)
                Using stream = Await res.Content.ReadAsStreamAsync(), reader As New IO.StreamReader(stream)
                    Dim currentEvent As String = Nothing
                    While True
                        Dim line = Await reader.ReadLineAsync()
                        If line Is Nothing Then Exit While
                        If line.StartsWith("event: ") Then currentEvent = line.Substring(7) : events.Add(currentEvent)
                        If line.StartsWith("data: ") AndAlso currentEvent = "client_tool" Then
                            Dim call1 = JsonDocument.Parse(line.Substring(6)).RootElement
                            Assert.Equal("local_echo", call1.GetProperty("name").GetString())
                            Assert.Contains("ping", call1.GetProperty("arguments").GetString())
                            ' A wrong id is refused; the right one resumes the answer.
                            Assert.Equal(HttpStatusCode.NotFound, (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = "nope", .ok = True, .result = "x"}, admin.Csrf)).StatusCode)
                            Assert.True((Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = call1.GetProperty("id").GetString(), .ok = True, .result = "pong-42"}, admin.Csrf)).IsSuccessStatusCode)
                        End If
                    End While
                End Using
                Assert.Contains("client_tool", events)
                Assert.Contains("done", events)
                Dim saved = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement
                Dim last = saved.GetProperty("messages").EnumerateArray().Last()
                Assert.Contains("pong-42", last.GetProperty("content").GetString())
                Assert.Contains(last.GetProperty("toolCalls").EnumerateArray(), Function(t) t.GetProperty("name").GetString() = "local_echo" AndAlso t.GetProperty("ok").GetBoolean())
                ' The client's instructions reached the model.
                Assert.Contains(up.Requests, Function(r) r("messages")(0)("content").ToString().Contains("Skills: none."))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AScreenshotFromTheDesktopAppReachesTheModelAndTheUser() As Task
        Const Png As String = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNkYAAAAAYAAjCB0C8AAAAASUVORK5CYII="
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If req("stream") Is Nothing OrElse req("stream").ToString() <> "true" Then Return Nothing
                                    Dim sb As New Text.StringBuilder()
                                    Dim sawTool = req("messages").AsArray().Any(Function(m) m("role")?.ToString() = "tool")
                                    If Not sawTool Then
                                        sb.Append("data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.tool_calls = {New With {.index = 0, .id = "call_shot", .type = "function",
                                            .function = New With {.name = "take_screenshot", .arguments = "{""reason"":""to see the error""}"}}}}}}}) & vbLf & vbLf)
                                    Else
                                        sb.Append("data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "I can see your screen."}}}}) & vbLf & vbLf)
                                    End If
                                    sb.Append("data: [DONE]" & vbLf & vbLf)
                                    Return sb.ToString()
                                End Function
            Dim env = Await StartAsync(up, Sub(o) o.Chat.DefaultPermissionMode = "auto")
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "what is on my screen?", .model = "fast/a", .device = "pc-test",
                        .clientTools = {New With {.name = "take_screenshot", .description = "Screenshot", .parameters = New With {.type = "object"}}}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim res = Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                Dim events As New List(Of String)
                Await ReadEventsAsync(res, Async Function(evt, data)
                                               events.Add(evt)
                                               ' The desktop app's tools ask first unless allowed; this test allows the approval the server asks for.
                                               If evt = "approval" Then Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/approvals", New With {.id = data.GetProperty("id").GetString(), .choice = "once"}, admin.Csrf)
                                               If evt = "client_tool" Then
                                                   Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                              New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "Screenshot taken.", .image = New With {.mediaType = "image/png", .data = Png}}, admin.Csrf)
                                               End If
                                           End Function)
                Assert.Contains("image", events)
                ' The model got the picture after the tool result, in a user message with the image.
                Dim last = up.Requests.Last(Function(r) r("stream")?.ToString() = "true")
                Dim msgs = last("messages").AsArray()
                Dim toolAt = msgs.ToList().FindIndex(Function(m) m("role")?.ToString() = "tool")
                Assert.True(toolAt >= 0)
                Dim pic = msgs(toolAt + 1)
                Assert.Equal("user", pic("role").ToString())
                Assert.Contains(pic("content").AsArray(), Function(p) p("type")?.ToString() = "image_url" AndAlso p("image_url")("url").ToString().StartsWith("data:image/png;base64,"))
                ' The user sees the screenshot with the answer.
                Dim saved = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement
                Dim answer = saved.GetProperty("messages").EnumerateArray().Last()
                Assert.Equal("I can see your screen.", answer.GetProperty("content").GetString().Trim())
                Assert.Contains(answer.GetProperty("attachments").EnumerateArray(), Function(a) a.GetProperty("kind").GetString() = "image" AndAlso a.GetProperty("name").GetString().StartsWith("screenshot-"))
                ' Bad pictures are refused.
                Assert.Equal(HttpStatusCode.BadRequest, (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = "x", .ok = True, .result = "x", .image = New With {.mediaType = "text/html", .data = Png}}, admin.Csrf)).StatusCode)
            End Using
        End Using
    End Function

    ''' <summary>Reads an SSE response line by line, handing each (event, data) to the callback until the stream ends.</summary>
    Friend Shared Async Function ReadEventsAsync(res As HttpResponseMessage, onEvent As Func(Of String, JsonElement, Task)) As Task
        Using stream = Await res.Content.ReadAsStreamAsync(), reader As New IO.StreamReader(stream)
            Dim current As String = Nothing
            While True
                Dim line = Await reader.ReadLineAsync()
                If line Is Nothing Then Exit While
                If line.StartsWith("event: ") Then current = line.Substring(7)
                If line.StartsWith("data: ") Then Await onEvent(current, JsonDocument.Parse(line.Substring(6)).RootElement.Clone())
            End While
        End Using
    End Function

    <Fact>
    Public Async Function ManualModeAsksBeforeNewToolsAndAnyWindowCanAnswer() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    Dim msgs = req("messages").AsArray()
                                    If msgs.Any(Function(m) m("role")?.ToString() = "tool") Then
                                        Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "done: " & msgs.Last(Function(m) m("role")?.ToString() = "tool")("content").ToString()}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                    End If
                                    Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.tool_calls = {New With {.index = 0, .id = "call_7", .type = "function",
                                        .function = New With {.name = "local_read", .arguments = "{""url"":""https://example.com/a""}"}}}}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                End Function
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                ' A second window of the same user listens on the live stream.
                Dim second = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim liveReq As New HttpRequestMessage(HttpMethod.Get, "/api/chat/stream")
                Dim liveRes = Await second.Client.SendAsync(liveReq, HttpCompletionOption.ResponseHeadersRead)
                Dim seenOnSecond As New Concurrent.ConcurrentQueue(Of String)
                Dim secondTask = Task.Run(Async Function()
                                              Try
                                                  Using stream = Await liveRes.Content.ReadAsStreamAsync(), reader As New IO.StreamReader(stream)
                                                      While True
                                                          Dim line = Await reader.ReadLineAsync()
                                                          If line Is Nothing Then Exit While
                                                          If line.StartsWith("data: ") Then
                                                              Dim d = JsonDocument.Parse(line.Substring(6)).RootElement
                                                              Dim t As JsonElement
                                                              If d.TryGetProperty("type", t) Then seenOnSecond.Enqueue(t.GetString())
                                                              ' The second window answers the approval.
                                                              If t.ValueKind = JsonValueKind.String AndAlso t.GetString() = "approval" Then
                                                                  Dim callId = d.GetProperty("data").GetProperty("id").GetString()
                                                                  Assert.Contains("first time", d.GetProperty("data").GetProperty("reason").GetString())
                                                                  Dim snap = JsonDocument.Parse(Await second.Client.GetStringAsync($"/api/chat/conversations/{id}/live")).RootElement
                                                                  Assert.True(snap.GetProperty("active").GetBoolean())
                                                                  Assert.Equal(1, snap.GetProperty("approvals").GetArrayLength())
                                                                  Assert.True((Await Send(second.Client, "POST", $"/api/chat/conversations/{id}/approvals", New With {.id = callId, .choice = "always"}, second.Csrf)).IsSuccessStatusCode)
                                                              End If
                                                          End If
                                                      End While
                                                  End Using
                                              Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException
                                              End Try
                                          End Function)
                Await Task.Delay(300)
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "read it", .model = "fast/a", .settings = New With {.permissionMode = "manual"},
                        .clientTools = {New With {.name = "local_read", .description = "Read a page", .parameters = New With {.type = "object"}}}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim res = Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
                Dim events As New List(Of String)
                Await ReadEventsAsync(res, Async Function(evt, data)
                                               events.Add(evt)
                                               If evt = "client_tool" Then
                                                   Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "page text"}, admin.Csrf)
                                               End If
                                           End Function)
                ' approval → (answered in the other window) → resolved → the tool runs → done
                Dim order = events.Where(Function(e) e = "approval" OrElse e = "approval_resolved" OrElse e = "client_tool" OrElse e = "done").ToList()
                Assert.Equal({"approval", "approval_resolved", "client_tool", "done"}, order.ToArray())
                Assert.Contains("approval", seenOnSecond)
                Assert.Contains("delta", seenOnSecond)                      ' the other window follows the answer live
                Dim last = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement.GetProperty("messages").EnumerateArray().Last()
                Assert.Contains("page text", last.GetProperty("content").GetString())
                ' "Always allow in this chat": the same tool and site run without asking the next time.
                Dim req2 As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "again", .model = "fast/a", .settings = New With {.permissionMode = "manual"},
                        .clientTools = {New With {.name = "local_read", .description = "Read a page", .parameters = New With {.type = "object"}}}})}
                req2.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim events2 As New List(Of String)
                Await ReadEventsAsync(Await admin.Client.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                      events2.Add(evt)
                                                                                                                      If evt = "client_tool" Then Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "x"}, admin.Csrf)
                                                                                                                  End Function)
                Assert.DoesNotContain("approval", events2)
                Assert.Contains("done", events2)
                Assert.Contains("approval_resolved", seenOnSecond)             ' every window closes the approval card
                liveRes.Dispose()
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheLastModelIsRememberedAndArenaModelIsHidden() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                ' OpenWebUI's arena pseudo model is not offered anywhere and cannot be used.
                Dim meta = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement
                Dim ids = meta.GetProperty("providerModels").EnumerateArray().Select(Function(m) m.GetProperty("id").GetString()).ToList()
                Assert.Contains("fast/a", ids)
                Assert.DoesNotContain("arena-model", ids)
                Dim v1 = Await admin.Client.GetStringAsync("/v1/models")
                Assert.DoesNotContain("arena-model", v1)
                Assert.Equal(HttpStatusCode.NotFound, (Await Send(admin.Client, "POST", "/v1/chat/completions", New With {.model = "arena-model", .messages = {New With {.role = "user", .content = "hi"}}}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.BadRequest, (Await Send(admin.Client, "PUT", "/api/chat/preferences", New With {.lastModel = "arena-model"}, admin.Csrf)).StatusCode)

                ' Nothing used yet; then a message with code/b makes it the model every client starts with.
                Assert.Equal(JsonValueKind.Null, meta.GetProperty("lastModel").ValueKind)
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "code/b"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {.Content = HttpApiTests.JsonBody(New With {.content = "hi", .model = "code/b"})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Function(evt, data) Task.CompletedTask)
                Assert.Equal("code/b", JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement.GetProperty("lastModel").GetString())
                ' Picking a model before sending (a new chat) is remembered too, as is changing a chat's model.
                Assert.True((Await Send(admin.Client, "PUT", "/api/chat/preferences", New With {.lastModel = "fast/a"}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal("fast/a", JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/preferences")).RootElement.GetProperty("lastModel").GetString())
                Assert.True((Await Send(admin.Client, "PATCH", $"/api/chat/conversations/{id}", New With {.model = "code/b"}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal("code/b", JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement.GetProperty("lastModel").GetString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AgentClientsGetMoreToolRoundsAndCanRewindOrStopAChat() As Task
        Using up As New FakeUpstream()
            ' The model always asks for the client's tool (a different file each time, so it is not a loop), but every file reads
            ' "x": nothing new after the first, so the progress check after the second stretch ends the answer.
            Dim fileNo = 0
            up.StreamOverride = Function(req)
                                    If req("tools") Is Nothing Then Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "final"}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                    Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.tool_calls = {New With {.index = 0, .id = "call_" & Guid.NewGuid().ToString("N").Substring(0, 6), .type = "function",
                                        .function = New With {.name = "Read", .arguments = "{""path"":""file" & Interlocked.Increment(fileNo) & ".txt""}"}}}}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                End Function
            Dim env = Await StartAsync(up, Sub(o) o.Chat.MaxToolRounds = 8)   ' the chat default this test compares with
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "read", .model = "fast/a", .maxToolRounds = 12, .settings = New With {.permissionMode = "auto"},
                        .clientTools = {New With {.name = "Read", .description = "Read a file", .parameters = New With {.type = "object"}}}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim toolCalls = 0
                Dim stopReason As String = Nothing
                Await ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                     If evt = "client_tool" Then
                                                                                                                         toolCalls += 1
                                                                                                                         Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "x"}, admin.Csrf)
                                                                                                                     End If
                                                                                                                     If evt = "done" Then stopReason = data.GetProperty("stopReason").GetString()
                                                                                                                 End Function)
                ' The client's stretch of 12 (not the chat default of 8): the first brought something new, the second nothing new.
                Assert.Equal(24, toolCalls)
                Assert.Equal("max_rounds", stopReason)                       ' then a round without tools: it answers, and the answer offers Continue

                ' A second message, then rewind to it: it and its answer go.
                Dim req2 As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {.Content = HttpApiTests.JsonBody(New With {.content = "second", .model = "fast/a"})}
                req2.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await ReadEventsAsync(Await admin.Client.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead), Function(evt, data) Task.CompletedTask)
                Dim messages = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement.GetProperty("messages").EnumerateArray().ToList()
                Assert.Equal(4, messages.Count)
                Dim secondId = messages(2).GetProperty("id").GetString()
                Dim rewound = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/rewind", New With {.messageId = secondId}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Assert.Equal(2, rewound.GetProperty("removed").GetInt32())
                Assert.Equal(2, JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement.GetProperty("messages").GetArrayLength())
                Assert.Equal(HttpStatusCode.NotFound, (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/rewind", New With {.messageId = "nope"}, admin.Csrf)).StatusCode)
                ' Nothing is being written: stop says so.
                Dim stopped = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/stop", New With {.x = 1}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Assert.False(stopped.GetProperty("stopped").GetBoolean())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function WebWindowsChooseTheDesktopToolsTheyUseAndDeleteChatsInBulk() As Task
        Using up As New FakeUpstream()
            Dim offered As New Concurrent.ConcurrentQueue(Of String)
            up.StreamOverride = Function(req)
                                    For Each t In If(req("tools")?.AsArray(), New JsonArray())
                                        offered.Enqueue(t("function")("name").ToString())
                                    Next
                                    Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "ok"}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                End Function
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                ' The desktop app is connected (its live stream names the device) and tells what it offers.
                Dim desktop = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim liveRes = Await desktop.Client.SendAsync(New HttpRequestMessage(HttpMethod.Get, "/api/chat/stream?device=pc1&name=Office%20PC"), HttpCompletionOption.ResponseHeadersRead)
                Dim seen As New Concurrent.ConcurrentQueue(Of String)
                Dim reading = Task.Run(Async Function()
                                           Try
                                               Using stream = Await liveRes.Content.ReadAsStreamAsync(), reader As New IO.StreamReader(stream)
                                                   While True
                                                       Dim line = Await reader.ReadLineAsync()
                                                       If line Is Nothing Then Exit While
                                                       Dim t As JsonElement
                                                       If line.StartsWith("data: ") AndAlso JsonDocument.Parse(line.Substring(6)).RootElement.TryGetProperty("type", t) Then seen.Enqueue(t.GetString())
                                                   End While
                                               End Using
                                           Catch ex As Exception When TypeOf ex Is IO.IOException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException
                                           End Try
                                       End Function)
                Await Task.Delay(200)
                Dim catalog = "{""name"":""Office PC"",""clientTools"":[" &
                    "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object""},""x-risk"":""read""}," &
                    "{""name"":""write_file"",""description"":""Write a file"",""parameters"":{""type"":""object""},""x-risk"":""change""}," &
                    "{""name"":""mcp__git__log"",""description"":""Git log"",""parameters"":{""type"":""object""}}]}"
                Assert.True((Await Send(desktop.Client, "POST", "/api/chat/devices/pc1/catalog", catalog, desktop.Csrf)).IsSuccessStatusCode)

                ' The web app lists those tools in its Tools menu.
                Dim cat = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/devices/pc1/catalog")).RootElement
                Assert.True(cat.GetProperty("online").GetBoolean())
                Assert.Equal("Office PC", cat.GetProperty("name").GetString())
                Dim tools = cat.GetProperty("tools").EnumerateArray().ToList()
                Assert.Equal(3, tools.Count)
                Assert.Equal("change", tools.First(Function(x) x.GetProperty("name").GetString() = "write_file").GetProperty("risk").GetString())
                Assert.Equal("Files & commands", tools.First(Function(x) x.GetProperty("name").GetString() = "read_file").GetProperty("group").GetString())
                Assert.Equal("MCP · git", tools.First(Function(x) x.GetProperty("name").GetString() = "mcp__git__log").GetProperty("group").GetString())
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/chat/devices/nope/catalog")).StatusCode)

                ' A chat in the web app uses that computer, with write_file switched off: the model is not offered it.
                Dim settings = New With {.device = "pc1", .deviceToolsOff = {"write_file"}}
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a", settings}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "hi", .model = "fast/a", settings})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Await ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Function(evt, data) Task.CompletedTask)
                Assert.Contains("read_file", offered)
                Assert.Contains("mcp__git__log", offered)
                Assert.DoesNotContain("write_file", offered)

                ' Manage → check two chats → delete them at once; every window hears about it.
                Dim other = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim r = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations/delete", New With {.ids = {id, other, "missing"}}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Assert.Equal(2, r.GetProperty("deleted").GetArrayLength())
                Assert.Equal(0, r.GetProperty("skipped").GetArrayLength())
                Dim list = Await admin.Client.GetStringAsync("/api/chat/conversations")
                Assert.DoesNotContain(id, list)
                Assert.DoesNotContain(other, list)
                Assert.Equal(HttpStatusCode.BadRequest, (Await Send(admin.Client, "POST", "/api/chat/conversations/delete", New With {.ids = New String() {}}, admin.Csrf)).StatusCode)
                For i = 1 To 30
                    If seen.Contains("deleted") Then Exit For
                    Await Task.Delay(100)
                Next
                Assert.Contains("deleted", seen)
                liveRes.Dispose()
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ReasoningEffortIsDroppedForModelsWithoutIt() As Task
        Using up As New FakeUpstream() With {.RejectReasoning = True}
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                For Each msgText In {"one", "two"}
                    Dim sse = Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages", New With {.content = msgText, .model = "fast/a", .settings = New With {.reasoningEffort = "ultracode"}}, admin.Csrf)).Content.ReadAsStringAsync()
                    Assert.Contains("event: done", sse)
                    Assert.DoesNotContain("event: error", sse)
                Next
                Dim chats = up.Requests.Where(Function(r) r("stream")?.ToString() = "true").ToList()
                Assert.Equal(3, chats.Count)                                   ' with the parameter (refused), without, and the next message without
                Assert.NotNull(chats(0)("reasoning_effort"))
                Assert.Null(chats(1)("reasoning_effort"))
                Assert.Null(chats(2)("reasoning_effort"))
                Assert.Contains("Ultracode", chats(2)("messages")(0)("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ContextIsMeasuredAndCompactedIntoASummary() As Task
        Using up As New FakeUpstream()
            ' A reasoning model prepends a <think> block; compaction must strip it so it is not stored as the summary.
            up.TextOverride = Function(req) If(req("messages")(0)("content").ToString().Contains("compress conversations"),
                                               "<think>Deciding what to keep.</think>" & vbLf & "## Goal" & vbLf & "The user is testing compaction with numbered messages one to three." & vbLf & "## Open items" & vbLf & "None.", Nothing)
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                For Each msgText In {"message one", "message two", "message three"}
                    Dim sse = Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages", New With {.content = msgText, .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
                    Assert.Contains("""context"":{""used""", sse)                ' every answer reports the context use
                Next
                Dim ctx = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}/context")).RootElement
                Assert.True(ctx.GetProperty("used").GetInt32() > 0)
                Assert.Equal(128000, ctx.GetProperty("max").GetInt32())
                Assert.False(ctx.GetProperty("compacted").GetBoolean())
                ' /compact streams Server-Sent Events (keep-alive pings, then a "done" event) so a long summary never trips
                ' Cloudflare's read timeout. Pull the JSON out of the single data: line.
                Dim compactBody = Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/compact", New With {.x = 1}, admin.Csrf)).Content.ReadAsStringAsync()
                Assert.Contains("event: done", compactBody)
                Dim r = JsonDocument.Parse(compactBody.Split(vbLf).First(Function(l) l.StartsWith("data: ", StringComparison.Ordinal)).Substring(6)).RootElement
                Assert.True(r.GetProperty("compacted").GetBoolean(), compactBody)
                Assert.True(r.GetProperty("context").GetProperty("compacted").GetBoolean())
                Assert.Contains("compaction", r.GetProperty("conversation").GetProperty("summary").GetString())
                Assert.DoesNotContain("<think>", r.GetProperty("conversation").GetProperty("summary").GetString())
                ' The stats dropped into the chat: fewer messages in full, real token counts.
                Dim st = r.GetProperty("stats")
                Assert.True(st.GetProperty("beforeMessages").GetInt32() > st.GetProperty("afterMessages").GetInt32(), st.ToString())
                Assert.True(st.GetProperty("beforeTokens").GetInt32() > 0)
                Assert.True(st.GetProperty("afterTokens").GetInt32() > 0)
                ' The next message carries the summary instead of the compacted messages.
                Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages", New With {.content = "message four", .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
                Dim lastReq = up.Requests.Last(Function(q) q("stream")?.ToString() = "true")
                Dim sent = lastReq("messages").ToJsonString()
                Assert.Contains("Summary of the earlier part", sent)
                Assert.DoesNotContain("message one", sent)
                Assert.Contains("message three", sent)                         ' the last exchange stayed in full
            End Using
        End Using
        Assert.Equal({"example.com", "docs.x.io"}, ChatService.HostsIn("{""url"":""https://www.example.com/a"",""b"":""see http://docs.x.io/""}").ToArray())
        Assert.Equal("medium", ChatService.NormalizeEffort("weird"))
        Assert.Equal("ultracode", ChatService.NormalizeEffort("UltraCode"))
    End Function

    <Fact>
    Public Async Function ChatShortensAndRetriesWhenTheProviderSaysThePromptDoesNotFit() As Task
        Using up As New FakeUpstream()
            Dim streamed = 0
            ' The first streamed request is refused the way the local model's server refuses an over-long prompt.
            up.StreamOverride = Function(req)
                                    If req("stream")?.ToString() <> "true" Then Return Nothing
                                    If Interlocked.Increment(streamed) > 1 Then Return Nothing
                                    Return "data: {""error"":{""message"":""[strata] 400 invalid request: prompt (131159 tokens) leaves no room to answer in the context (131072); requests are never truncated""}}" & vbLf & vbLf
                                End Function
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim sse = Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages", New With {.content = "hello", .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
                Assert.Contains("trying again", sse)
                Assert.Contains("event: done", sse)
                Assert.DoesNotContain("event: error", sse)
                Assert.Equal(2, Volatile.Read(streamed))
            End Using
        End Using
    End Function

    <Fact>
    Public Sub LongToolLoopsAreShortenedToFitTheContextWindow()
        ' A tool loop that grew past the window: system prompt, an older exchange, the question, four big tool results.
        Dim messages As New JsonArray(
            New JsonObject From {{"role", "system"}, {"content", "You are helpful."}},
            New JsonObject From {{"role", "user"}, {"content", "earlier question " & New String("e"c, 30000)}},
            New JsonObject From {{"role", "assistant"}, {"content", "earlier answer " & New String("a"c, 30000)}},
            New JsonObject From {{"role", "user"}, {"content", "Read the four files."}})
        For i = 1 To 4
            messages.Add(New JsonObject From {{"role", "assistant"}, {"content", Nothing}, {"tool_calls", New JsonArray(New JsonObject From {{"id", "c" & i}, {"type", "function"}, {"function", New JsonObject From {{"name", "read"}, {"arguments", "{}"}}}})}})
            messages.Add(New JsonObject From {{"role", "tool"}, {"tool_call_id", "c" & i}, {"content", $"<untrusted source=""tool:read"">file {i} " & New String(ChrW(48 + i), 60000) & "</untrusted>"}})
        Next
        Dim budget = ChatService.PromptBudget(32000)
        Assert.Equal(32000 - 4096, budget)
        Assert.True(ChatService.EstimateRequestTokens(messages, 3.0, 0) > budget)
        Assert.True(ChatService.FitToBudget(messages, budget, 3.0, 0))
        Assert.True(ChatService.EstimateRequestTokens(messages, 3.0, 0) <= budget)
        ' The system prompt and the question stay whole; the oldest tool output is cut first and says so, keeping its ends.
        Assert.Equal("You are helpful.", messages(0)("content").ToString())
        Assert.Equal("Read the four files.", messages(3)("content").ToString())
        Dim first = messages(5)("content").ToString()
        Assert.Contains("characters cut to fit the model's context window", first)
        Assert.StartsWith("<untrusted source=""tool:read"">file 1", first)
        Assert.EndsWith("</untrusted>", first)
        Assert.True(messages(11)("content").ToString().Length >= messages(5)("content").ToString().Length)
        Assert.False(ChatService.FitToBudget(messages, budget, 3.0, 0))   ' fits now: nothing more to cut
        ' The upstream's way of saying the prompt is too long.
        Assert.True(ChatService.IsContextOverflow("yqy/x: 400 [strata] 400 invalid request: prompt (131159 tokens) leaves no room to answer in the context (131072); requests are never truncated"))
        Assert.False(ChatService.IsContextOverflow("yqy/x: 500 upstream timed out"))
    End Sub

    <Fact>
    Public Async Function OpenApiDocumentListsEveryRouteWithItsPermission() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim doc = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/openapi.json"))
                Assert.StartsWith("3.", doc("openapi").ToString())
                Dim paths = doc("paths").AsObject()
                Assert.True(paths.ContainsKey("/v1/chat/completions"))
                Assert.True(paths.ContainsKey("/api/agents/{id}/pause"))
                Assert.True(paths.ContainsKey("/api/chat/conversations"))
                Assert.Equal("agents.control", paths("/api/agents/{id}/pause")("post")("x-permission").ToString())
                Assert.Equal("api.use", paths("/v1/chat/completions")("post")("x-permission").ToString())
                Assert.NotNull(doc("components")("securitySchemes")("apiKey"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function CustomModelRoutingPicksTheRightModel() As Task
        Dim hub As New ModelHub(Nothing, Nothing, New NullLog())
        Dim m As New RouterModel With {.Id = "x/y", .Definition = New RouterDefinition With {
            .DefaultModel = "chat", .Mode = "rules", .LongContextTokens = 2000,
            .Routes = New List(Of RouterRoute) From {
                New RouterRoute With {.Category = "vision", .Model = "eyes"},
                New RouterRoute With {.Category = "structured", .Model = "json"},
                New RouterRoute With {.Category = "tools", .Model = "agent"},
                New RouterRoute With {.Category = "long_context", .Model = "long"},
                New RouterRoute With {.Category = "coding", .Model = "coder"},
                New RouterRoute With {.Category = "research", .Model = "scholar", .Keywords = New List(Of String) From {"zebrafish"}},
                New RouterRoute With {.Category = "translation", .Model = "polyglot"}}}}
        Dim ask = Function(json As String) hub.DecideAsync(m, JsonNode.Parse(json), CancellationToken.None)
        Assert.Equal("eyes", (Await ask("{""messages"":[{""role"":""user"",""content"":[{""type"":""text"",""text"":""what is this""},{""type"":""image_url"",""image_url"":{""url"":""data:image/png;base64,AAA""}}]}]}")).Model)
        Assert.Equal("json", (Await ask("{""response_format"":{""type"":""json_object""},""messages"":[{""role"":""user"",""content"":""list colors""}]}")).Model)
        Assert.Equal("agent", (Await ask("{""tools"":[{""type"":""function"",""function"":{""name"":""f""}}],""messages"":[{""role"":""user"",""content"":""hi""}]}")).Model)
        Assert.Equal("long", (Await ask("{""messages"":[{""role"":""user"",""content"":""" & New String("a"c, 12000) & """}]}")).Model)
        Assert.Equal("coder", (Await ask("{""messages"":[{""role"":""user"",""content"":""Write a Python function and fix the bug in this code""}]}")).Model)
        Assert.Equal("scholar", (Await ask("{""messages"":[{""role"":""user"",""content"":""tell me about zebrafish""}]}")).Model)
        Assert.Equal("polyglot", (Await ask("{""messages"":[{""role"":""user"",""content"":""Translate this into French please""}]}")).Model)
        Dim d = Await ask("{""messages"":[{""role"":""user"",""content"":""good morning!""}]}")
        Assert.Equal("chat", d.Model)
        Assert.Equal("default", d.Category)
        ' Only the last user message decides.
        Assert.Equal("chat", (Await ask("{""messages"":[{""role"":""user"",""content"":""fix my python code""},{""role"":""assistant"",""content"":""ok""},{""role"":""user"",""content"":""thanks!""}]}")).Model)
    End Function

    <Fact>
    Public Sub HelpersParseTitlesRunOutputAndCatchAllRoutes()
        Assert.Equal("Fix the login bug", ChatService.CleanTitle("<think>hmm</think>" & vbLf & "Title: ""Fix the login bug""."))
        Assert.Null(ChatService.CleanTitle("   "))
        Assert.Equal("Explain this code please", ChatService.TitleFrom("**Explain** this ```py" & vbLf & "print(1)" & vbLf & "``` code please"))
        Assert.EndsWith("…", ChatService.TitleFrom(String.Join(" ", Enumerable.Repeat("word", 40))))

        Assert.Equal({"node.js latest lts release", "node 24 release date"}, ChatService.SearchQueries("<think>x</think>1. ""node.js latest LTS release""" & vbLf & "- node 24 release date" & vbLf & "extra one", "q").Select(Function(s) s.ToLowerInvariant()).ToArray())
        Assert.Equal({"What is new?"}, ChatService.SearchQueries(Nothing, "  What is   new? ").ToArray())
        ' The same search reworded only in case, punctuation or word order counts as a repeat.
        Assert.Equal(ChatService.SearchKey("Node.js LTS release"), ChatService.SearchKey("release, node js lts"))
        Assert.NotEqual(ChatService.SearchKey("node 24 release"), ChatService.SearchKey("node 22 release"))
        Assert.Equal("", ChatService.SearchKey("  ?! "))
        ' News questions search news articles of the last day (or week); other questions search as usual.
        Assert.Equal("day", ChatService.NewsRange("gather today's news, every event of the last 24h"))
        Assert.Equal("day", ChatService.NewsRange("What are the latest headlines?"))
        Assert.Equal("week", ChatService.NewsRange("news from this week about AI"))
        Assert.Null(ChatService.NewsRange("write a story about a lion"))
        ' read_page: readable text of the article part, feeds as items, and only public web links.
        Dim page = Global.Agentic.Tools.PageReader.HtmlToText("<html><head><title>Big &amp; News</title><script>x()</script></head><body><nav>Menu Home</nav>" &
            "<article><h1>Storm hits coast</h1><p>Winds of 120 km/h.</p>" & New String("x"c, 1600) & "</article><footer>Contact</footer></body></html>")
        Assert.Equal("Big & News", page.Title)
        Assert.Contains("## Storm hits coast", page.Text)
        Assert.Contains("Winds of 120 km/h.", page.Text)
        Assert.DoesNotContain("Menu Home", page.Text)
        Assert.DoesNotContain("x()", page.Text)
        Dim feed = Global.Agentic.Tools.PageReader.FeedToText("<rss><channel><title>Wire</title><item><title>Quake in Chile</title><link>https://e.org/q</link>" &
            "<pubDate>Tue, 13 Oct 2026 10:00:00 GMT</pubDate><description><![CDATA[<p>A 6.1 quake</p>]]></description></item></channel></rss>")
        Assert.Contains("- Quake in Chile (Tue, 13 Oct 2026 10:00:00 GMT)", feed.Text)
        Assert.Contains("https://e.org/q", feed.Text)
        Assert.Contains("A 6.1 quake", feed.Text)
        Assert.Throws(Of ArgumentException)(Function() Global.Agentic.Tools.PageReader.CheckUrl("file:///c:/windows/win.ini"))
        Assert.Throws(Of ArgumentException)(Function() Global.Agentic.Tools.PageReader.CheckUrl("http://example.org:8080/"))
        Assert.Throws(Of ArgumentException)(Function() Global.Agentic.Tools.PageReader.CheckUrl("https://user:pw@example.org/"))
        Dim local = Assert.ThrowsAny(Of Exception)(Function() Global.Agentic.Tools.PageReader.ReadAsync("http://127.0.0.1/", 1000, Threading.CancellationToken.None).GetAwaiter().GetResult())
        Assert.Contains("public internet", local.ToString())
        ' Model failures worth retrying (goal mode retries them): offline, overloaded, timed out, a broken stream; not refusals.
        Dim live As New Threading.CancellationTokenSource()
        Assert.True(ChatService.IsTransientFailure(New Net.Http.HttpRequestException("qwen: 503 upstream unavailable"), live.Token))
        Assert.True(ChatService.IsTransientFailure(New Net.Http.HttpRequestException("Connection refused (localhost:8080)"), live.Token))
        Assert.True(ChatService.IsTransientFailure(New TaskCanceledException("timeout"), live.Token))
        Assert.True(ChatService.IsTransientFailure(New IO.IOException("reset"), live.Token))
        Assert.False(ChatService.IsTransientFailure(New Net.Http.HttpRequestException("qwen: 400 bad request"), live.Token))
        Assert.False(ChatService.IsTransientFailure(New Net.Http.HttpRequestException("gpt: 401 invalid key"), live.Token))
        live.Cancel()
        Assert.False(ChatService.IsTransientFailure(New TaskCanceledException("stopped"), live.Token))

        ' ask_user questions are cleaned up: 1-4 questions, 2-4 distinct options, or free text when fewer than two options.
        Dim qs = ChatService.ParseQuestions("{""questions"":[{""question"":""Which language?"",""options"":[""C#"",""Python"",""c#"",""Go"",""Rust"",""Java""]},{""question"":""Anything else?"",""options"":[""x""]}]}")
        Assert.Equal(2, qs.Count)
        Assert.Equal({"C#", "Python", "Go", "Rust"}, qs(0)("options").AsArray().Select(Function(o) o.ToString()).ToArray())
        Assert.Empty(qs(1)("options").AsArray())
        Assert.Equal("true", qs(1)("allowOther").ToString())
        Assert.Empty(ChatService.ParseQuestions("not json"))

        Dim parsed = ChatService.ParseRunOutput("hello" & vbLf & "__AGENTIC_EXIT__=3" & vbLf)
        Assert.Equal(3, parsed.ExitCode)
        Assert.DoesNotContain("__AGENTIC_EXIT__", parsed.Output)
        Assert.Contains("hello", parsed.Output)

        Dim r As New Router()
        r.Map("GET", "/api/custom-models/usage", AuthLevel.Read, Function(rc) Task.CompletedTask)
        r.Map("GET", "/api/custom-models/{*id}", AuthLevel.Read, Function(rc) Task.CompletedTask)
        Dim values As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Dim mismatch As Boolean
        Dim hit = r.Match("GET", "/api/custom-models/team/router", values, mismatch)
        Assert.NotNull(hit)
        Assert.Equal("team/router", values("id"))
        values.Clear()
        Assert.Empty(If(r.Match("GET", "/api/custom-models/usage", values, mismatch)?.Segments.Where(Function(s) s.StartsWith("{")), {}))
    End Sub

    <Fact>
    Public Async Function ClientAppsFindOutdatedBuildsAndDownloadUpdates() As Task
        Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-updates-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "cli"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "desktop", "www", "desktop"))
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "agentic.exe"), "cli build 1")
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "release.json"), "{ ""version"": ""2.1.0"", ""notes"": ""Faster"", ""minimumVersion"": ""2.0.5"" }")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop", "AgenticClient.exe"), "desktop build")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop", "www", "desktop", "desktop.js"), "js 1")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop", "www", "desktop", "config.json"), "{""apiKey"":""secret""}")   ' never shipped
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop", "www", "desktop", "copying.tmp"), "half")
        Try
            Using up As New FakeUpstream()
                Dim port = HttpApiTests.FreePort()
                Dim host = Await TestHost.StartAsync(Sub(o)
                                                         o.Server.Enabled = True
                                                         o.Server.Port = port
                                                         o.Admin.Users.Add(New AdminUserOptions With {.Username = "admin", .PasswordHash = PasswordHasher.Hash("pw-123456789")})
                                                         o.AIProviders("OpenWebUI").BaseUrl = up.BaseUrl
                                                         o.Updates.Directory = dir
                                                     End Sub)
                Using host
                    Dim admin = Await LoginAsync(port, "admin", "pw-123456789")
                    Assert.Equal(HttpStatusCode.Created, (Await Send(admin.Client, "POST", "/api/users", New With {.username = "bob", .password = "bob-pass-12345", .role = "User"}, admin.Csrf)).StatusCode)
                    Dim bob = Await LoginAsync(port, "bob", "bob-pass-12345")

                    ' Any account sees the published builds.
                    Dim list = JsonDocument.Parse(Await bob.Client.GetStringAsync("/api/updates")).RootElement
                    Dim apps = list.GetProperty("apps").EnumerateArray().ToDictionary(Function(a) a.GetProperty("app").GetString())
                    Assert.Equal({"cli", "desktop"}, apps.Keys.OrderBy(Function(k) k).ToArray())
                    Assert.Equal("2.1.0", apps("cli").GetProperty("version").GetString())
                    Assert.Equal("agentic.exe", apps("cli").GetProperty("fileName").GetString())
                    Dim files = apps("desktop").GetProperty("files").EnumerateArray().Select(Function(f) f.GetProperty("path").GetString()).ToList()
                    Assert.Equal({"www/desktop/desktop.js"}, files.ToArray())
                    Assert.Equal("www/desktop", apps("desktop").GetProperty("managedFolders")(0).GetString())
                    Dim sha = apps("cli").GetProperty("sha256").GetString()

                    ' Older version: update (mandatory below minimumVersion); same build: none; same version, other build: update; newer: none.
                    Dim check = Async Function(q As String) JsonDocument.Parse(Await bob.Client.GetStringAsync("/api/updates/cli?" & q)).RootElement
                    Dim old = Await check("version=2.0.0&sha256=abc&installId=inst-old&machine=PC1")
                    Assert.True(old.GetProperty("updateAvailable").GetBoolean())
                    Assert.True(old.GetProperty("mandatory").GetBoolean())
                    Assert.False((Await check($"version=2.1.0&sha256={sha}&installId=inst-new&machine=PC2")).GetProperty("updateAvailable").GetBoolean())
                    Dim rebuilt = Await check("version=2.1.0&sha256=0000")
                    Assert.True(rebuilt.GetProperty("updateAvailable").GetBoolean())
                    Assert.False(rebuilt.GetProperty("mandatory").GetBoolean())
                    Assert.False((Await check("version=2.2.0&sha256=0000")).GetProperty("updateAvailable").GetBoolean())

                    ' Downloads carry their SHA-256; private and temporary files are not served.
                    Dim dl = Await bob.Client.GetAsync("/api/updates/cli/download")
                    Assert.Equal("cli build 1", Await dl.Content.ReadAsStringAsync())
                    Assert.Equal(sha, dl.Headers.GetValues("X-Sha256").First())
                    Assert.Equal("js 1", Await bob.Client.GetStringAsync("/api/updates/desktop/files/www/desktop/desktop.js"))
                    Assert.Equal(HttpStatusCode.NotFound, (Await bob.Client.GetAsync("/api/updates/desktop/files/www/desktop/config.json")).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await bob.Client.GetAsync("/api/updates/desktop/files/www/desktop/copying.tmp")).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await bob.Client.GetAsync("/api/updates/nothing")).StatusCode)

                    ' New installs get the whole app as one zip (the exe and its folders, no private or temporary files);
                    ' installed copies keep updating from the exe.
                    Assert.Equal("/api/updates/desktop/zip", apps("desktop").GetProperty("zipUrl").GetString())
                    Assert.Equal("/api/updates/desktop/download", apps("desktop").GetProperty("downloadUrl").GetString())
                    Assert.Equal("Agentic-CLI-2.1.0.zip", apps("cli").GetProperty("zipName").GetString())
                    Dim zipRes = Await bob.Client.GetAsync("/api/updates/desktop/zip")
                    Assert.Equal(HttpStatusCode.OK, zipRes.StatusCode)
                    Assert.Equal("application/zip", zipRes.Content.Headers.ContentType.MediaType)
                    Using z As New IO.Compression.ZipArchive(Await zipRes.Content.ReadAsStreamAsync())
                        Assert.Equal({"AgenticClient.exe", "www/desktop/desktop.js"}, z.Entries.Select(Function(e) e.FullName).OrderBy(Function(n) n, StringComparer.Ordinal).ToArray())
                        Using r As New IO.StreamReader(z.GetEntry("www/desktop/desktop.js").Open())
                            Assert.Equal("js 1", r.ReadToEnd())
                        End Using
                    End Using

                    ' Admins see every install and whether it is outdated; a rebuild makes the current one outdated too.
                    Assert.Equal(HttpStatusCode.Forbidden, (Await bob.Client.GetAsync("/api/updates/installs")).StatusCode)
                    Dim installs = Async Function() JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates/installs")).RootElement.GetProperty("installs").EnumerateArray().
                                                        ToDictionary(Function(i) i.GetProperty("installId").GetString(), Function(i) i.GetProperty("state").GetString())
                    Dim seen = Await installs()
                    Assert.Equal("outdated", seen("inst-old"))
                    Assert.Equal("current", seen("inst-new"))
                    IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "agentic.exe"), "cli build 2 (rebuilt)")
                    Assert.Equal("outdated", (Await installs())("inst-new"))
                End Using
            End Using
        Finally
            Try
                IO.Directory.Delete(dir, True)
            Catch ex As IO.IOException
            End Try
        End Try
    End Function

    <Fact>
    Public Async Function LinuxBuildsHaveTheirOwnChannelsAndTarGzInstalls() As Task
        Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-updates-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "cli"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "cli-linux-x64"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "desktop-linux-x64", "www", "desktop"))
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "agentic.exe"), "windows cli")
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli", "release.json"), "{ ""version"": ""2.2.0"" }")
        ' Linux binaries have no extension and no version resource: release.json carries the version.
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli-linux-x64", "agentic"), "linux cli")
        IO.File.WriteAllText(IO.Path.Combine(dir, "cli-linux-x64", "release.json"), "{ ""version"": ""2.2.0"", ""notes"": ""Linux build"" }")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop-linux-x64", "AgenticClient"), "linux desktop")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop-linux-x64", "release.json"), "{ ""version"": ""0.4.0"" }")
        IO.File.WriteAllText(IO.Path.Combine(dir, "desktop-linux-x64", "www", "desktop", "desktop.js"), "js linux")
        Try
            Using up As New FakeUpstream()
                Dim port = HttpApiTests.FreePort()
                Dim host = Await TestHost.StartAsync(Sub(o)
                                                         o.Server.Enabled = True
                                                         o.Server.Port = port
                                                         o.Admin.Users.Add(New AdminUserOptions With {.Username = "admin", .PasswordHash = PasswordHasher.Hash("pw-123456789")})
                                                         o.AIProviders("OpenWebUI").BaseUrl = up.BaseUrl
                                                         o.Updates.Directory = dir
                                                     End Sub)
                Using host
                    Dim admin = Await LoginAsync(port, "admin", "pw-123456789")
                    Dim apps = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates")).RootElement.GetProperty("apps").EnumerateArray().
                        ToDictionary(Function(a) a.GetProperty("app").GetString())
                    Assert.Equal({"cli", "cli-linux-x64", "desktop-linux-x64"}, apps.Keys.OrderBy(Function(k) k, StringComparer.Ordinal).ToArray())
                    Dim cli = apps("cli-linux-x64")
                    Assert.Equal("cli", cli.GetProperty("baseApp").GetString())
                    Assert.Equal("linux-x64", cli.GetProperty("platform").GetString())
                    Assert.Equal("agentic", cli.GetProperty("fileName").GetString())
                    Assert.Equal("2.2.0", cli.GetProperty("version").GetString())
                    Assert.Equal("tar.gz", cli.GetProperty("archiveType").GetString())
                    Assert.Equal("Agentic-CLI-2.2.0-linux-x64.tar.gz", cli.GetProperty("zipName").GetString())
                    Assert.Equal("win-x64", apps("cli").GetProperty("platform").GetString())
                    Assert.Equal("Agentic-CLI-2.2.0.zip", apps("cli").GetProperty("zipName").GetString())

                    ' A Linux install asks its own channel and gets the Linux binary.
                    Dim check = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates/cli-linux-x64?version=2.1.0&sha256=abc&installId=linux-1&machine=box")).RootElement
                    Assert.True(check.GetProperty("updateAvailable").GetBoolean())
                    Assert.Equal("/api/updates/cli-linux-x64/download", check.GetProperty("latest").GetProperty("downloadUrl").GetString())
                    Assert.Equal("linux cli", Await admin.Client.GetStringAsync("/api/updates/cli-linux-x64/download"))
                    Assert.Equal("js linux", Await admin.Client.GetStringAsync("/api/updates/desktop-linux-x64/files/www/desktop/desktop.js"))

                    ' New Linux installs get a .tar.gz: one folder, the binary executable, its files readable.
                    Dim res = Await admin.Client.GetAsync("/api/updates/desktop-linux-x64/zip")
                    Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                    Assert.Equal("application/gzip", res.Content.Headers.ContentType.MediaType)
                    Dim entries As New Dictionary(Of String, (Type As Formats.Tar.TarEntryType, Mode As IO.UnixFileMode, Text As String))
                    Using gz As New IO.Compression.GZipStream(Await res.Content.ReadAsStreamAsync(), IO.Compression.CompressionMode.Decompress),
                          tar As New Formats.Tar.TarReader(gz)
                        Dim e = tar.GetNextEntry()
                        While e IsNot Nothing
                            Dim text As String = Nothing
                            If e.DataStream IsNot Nothing Then
                                Using r As New IO.StreamReader(e.DataStream)
                                    text = r.ReadToEnd()
                                End Using
                            End If
                            entries(e.Name) = (e.EntryType, e.Mode, text)
                            e = tar.GetNextEntry()
                        End While
                    End Using
                    Assert.Equal({"Agentic-Desktop/", "Agentic-Desktop/AgenticClient", "Agentic-Desktop/www/", "Agentic-Desktop/www/desktop/", "Agentic-Desktop/www/desktop/desktop.js"},
                                 entries.Keys.OrderBy(Function(k) k, StringComparer.Ordinal).ToArray())
                    Assert.Equal("linux desktop", entries("Agentic-Desktop/AgenticClient").Text)
                    Assert.True(entries("Agentic-Desktop/AgenticClient").Mode.HasFlag(IO.UnixFileMode.UserExecute))
                    Assert.False(entries("Agentic-Desktop/www/desktop/desktop.js").Mode.HasFlag(IO.UnixFileMode.UserExecute))
                    Assert.Equal(Formats.Tar.TarEntryType.Directory, entries("Agentic-Desktop/www/desktop/").Type)

                    ' The installs list shows the platform's channel.
                    Dim installs = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates/installs")).RootElement.GetProperty("installs").EnumerateArray().ToList()
                    Dim linux = installs.Single(Function(i) i.GetProperty("installId").GetString() = "linux-1")
                    Assert.Equal("cli-linux-x64", linux.GetProperty("app").GetString())
                    Assert.Equal("outdated", linux.GetProperty("state").GetString())
                End Using
            End Using
        Finally
            Try
                IO.Directory.Delete(dir, True)
            Catch ex As IO.IOException
            End Try
        End Try
    End Function

    <Fact>
    Public Async Function FinanceShowsChatTabSpendByModelAndUser() As Task
        Using up As New FakeUpstream()
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                For Each name In {"alice", "bob"}
                    Assert.Equal(HttpStatusCode.Created, (Await Send(admin.Client, "POST", "/api/users",
                        New With {.username = name, .password = name & "-pass-1234", .role = "User", .permissions = ChatUser}, admin.Csrf)).StatusCode)
                Next
                ' Real account charges, each at the Model Square price in force then (that is what the graphs read back).
                Dim acc = env.Host.H.Accounts
                Await acc.ChargeAsync("alice", 500000, Global.Agentic.Economy.AccountService.EntryChat, "fast/a", 1000L, 200L, Nothing, "msg")
                Await acc.ChargeAsync("alice", 300000, Global.Agentic.Economy.AccountService.EntryChat, "code/b", 400L, 90L, Nothing, "msg")
                Await acc.ChargeAsync("alice", 150000, "Image", "gpt-image-2", Nothing, Nothing, Nothing, "picture")
                Await acc.ChargeAsync("bob", 200000, Global.Agentic.Economy.AccountService.EntryChat, "fast/a", 500L, 100L, Nothing, "msg")
                ' An API charge and a credit must NOT appear (the graphs are the Chat tab only).
                Await acc.ChargeAsync("bob", 999999, Global.Agentic.Economy.AccountService.EntryApi, "fast/a", 10L, 10L, Nothing, "api call")

                Dim u = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/finance/chat-usage?days=30"))
                Assert.Equal(30, u("days").GetValue(Of Integer)())
                ' By model: fast/a is alice 500k + bob 200k = 700k; API's 999999 is excluded.
                Dim byModel = u("byModel").AsArray().ToDictionary(Function(m) m("model").ToString(), Function(m) m("micros").GetValue(Of Long)())
                Assert.Equal(700000L, byModel("fast/a"))
                Assert.Equal(300000L, byModel("code/b"))
                Assert.Equal(150000L, byModel("gpt-image-2"))
                Assert.False(byModel.ContainsKey("?"))
                Assert.Equal(1150000L, u("totalMicros").GetValue(Of Long)())
                ' fast/a's tokens summed across its charges: (1000+200)+(500+100).
                Dim fastRow = u("byModel").AsArray().Single(Function(m) m("model").ToString() = "fast/a")
                Assert.Equal(1500L, fastRow("promptTokens").GetValue(Of Long)())
                Assert.Equal(300L, fastRow("completionTokens").GetValue(Of Long)())
                ' By user: alice (950k) above bob (200k); the API charge is excluded from bob.
                Dim byUser = u("byUser").AsArray()
                Assert.Equal("alice", byUser(0)("username").ToString())
                Assert.Equal(950000L, byUser(0)("micros").GetValue(Of Long)())
                Assert.Equal(200000L, byUser.Single(Function(x) x("username").ToString() = "bob")("micros").GetValue(Of Long)())
                ' Daily: today holds every chat-tab charge (1.15M), grouped by model.
                Dim today = DateTime.UtcNow.ToString("yyyy-MM-dd")
                Assert.Equal(1150000L, u("daily").AsArray().Where(Function(r) r("day").ToString() = today).Sum(Function(r) r("micros").GetValue(Of Long)()))

                ' Only finance viewers see it.
                Dim alice = Await LoginAsync(env.Port, "alice", "alice-pass-1234")
                Assert.Equal(HttpStatusCode.Forbidden, (Await alice.Client.GetAsync("/api/finance/chat-usage")).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub UpdateChannelsNameTheirPlatform()
        Assert.Equal(("cli", "linux-x64"), UpdateService.SplitApp("cli-linux-x64"))
        Assert.Equal(("desktop", "linux-arm64"), UpdateService.SplitApp("desktop-linux-arm64"))
        Assert.Equal(("my-tool", "linux-musl-x64"), UpdateService.SplitApp("my-tool-linux-musl-x64"))
        Assert.Equal(("cli", "win-x64"), UpdateService.SplitApp("cli"))
        Assert.Equal(("some-app", "win-x64"), UpdateService.SplitApp("some-app"))
        Assert.Equal(("vscode", "any"), UpdateService.SplitApp("vscode"))
    End Sub

    <Fact>
    Public Async Function TheVsCodeExtensionIsOneFileForAnyPlatform() As Task
        Dim dir = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-updates-" & Guid.NewGuid().ToString("N"))
        IO.Directory.CreateDirectory(IO.Path.Combine(dir, "vscode"))
        IO.File.WriteAllText(IO.Path.Combine(dir, "vscode", "agentic-vscode.vsix"), "vsix bytes")
        IO.File.WriteAllText(IO.Path.Combine(dir, "vscode", "release.json"), "{ ""file"": ""agentic-vscode.vsix"", ""version"": ""0.2.0"", ""notes"": ""Multitask"" }")
        Try
            Using up As New FakeUpstream()
                Dim port = HttpApiTests.FreePort()
                Dim host = Await TestHost.StartAsync(Sub(o)
                                                         o.Server.Enabled = True
                                                         o.Server.Port = port
                                                         o.Admin.Users.Add(New AdminUserOptions With {.Username = "admin", .PasswordHash = PasswordHasher.Hash("pw-123456789")})
                                                         o.AIProviders("OpenWebUI").BaseUrl = up.BaseUrl
                                                         o.Updates.Directory = dir
                                                     End Sub)
                Using host
                    Dim admin = Await LoginAsync(port, "admin", "pw-123456789")
                    Dim app = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates")).RootElement.GetProperty("apps").EnumerateArray().Single()
                    Assert.Equal("vscode", app.GetProperty("app").GetString())
                    Assert.Equal("any", app.GetProperty("platform").GetString())
                    Assert.Equal("file", app.GetProperty("archiveType").GetString())
                    Assert.Equal("0.2.0", app.GetProperty("version").GetString())
                    Assert.Equal("agentic-vscode.vsix", app.GetProperty("fileName").GetString())
                    Assert.Equal("agentic-vscode-0.2.0.vsix", app.GetProperty("zipName").GetString())

                    ' An older extension is told to update; the same version is up to date.
                    Dim check = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates/vscode?version=0.1.0&installId=vscode-1&machine=pc")).RootElement
                    Assert.True(check.GetProperty("updateAvailable").GetBoolean())
                    Dim same = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/updates/vscode?version=0.2.0")).RootElement
                    Assert.False(same.GetProperty("updateAvailable").GetBoolean())

                    ' New installs and updates both get the .vsix itself, never an archive of it.
                    Dim res = Await admin.Client.GetAsync("/api/updates/vscode/zip")
                    Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                    Assert.Equal("application/octet-stream", res.Content.Headers.ContentType.MediaType)
                    Assert.Equal("agentic-vscode-0.2.0.vsix", res.Content.Headers.ContentDisposition.FileName.Trim(""""c))
                    Assert.Equal("vsix bytes", Await res.Content.ReadAsStringAsync())
                    Assert.Equal("vsix bytes", Await admin.Client.GetStringAsync("/api/updates/vscode/download"))
                End Using
            End Using
        Finally
            Try
                IO.Directory.Delete(dir, True)
            Catch ex As IO.IOException
            End Try
        End Try
    End Function

    <Fact>
    Public Sub UpdateVersionsCompareByPartsAndBuild()
        Assert.Equal(New Version(2, 1, 0, 0), UpdateService.ParseVersion("v2.1"))
        Assert.Equal(New Version(0, 2, 0, 0), UpdateService.ParseVersion("0.2.0 Agentic Desktop"))
        Assert.Equal(New Version(1, 2, 3, 4), UpdateService.ParseVersion("1.2.3.4+abc"))
        Assert.Null(UpdateService.ParseVersion("latest"))
        Dim p As New UpdatePackage With {.Version = "1.0.0", .Sha256 = "aa"}
        Assert.False(UpdateService.Evaluate(p, "1.0.0.0", "AA").UpdateAvailable)   ' hashes compare without case
        Assert.True(UpdateService.Evaluate(p, "0.9", Nothing).UpdateAvailable)
        Assert.False(UpdateService.Evaluate(p, "1.0.0", Nothing).UpdateAvailable)  ' no hash: version only
        Assert.True(UpdateService.Evaluate(p, Nothing, Nothing).UpdateAvailable)   ' unknown version: offer it
    End Sub

    <Fact>
    Public Async Function AutoModeNeverAsksExceptAskFirstToolsAndStopEndsTheWait() As Task
        Using up As New FakeUpstream()
            ' The model calls the first offered tool once, then answers.
            up.StreamOverride = Function(req)
                                    Dim msgs = req("messages").AsArray()
                                    If msgs(msgs.Count - 1)("role")?.ToString() = "tool" OrElse req("tools") Is Nothing Then
                                        Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "finished"}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                    End If
                                    Dim offered = req("tools").AsArray()
                                    Dim toolName = offered(offered.Count - 1)("function")("name").ToString()
                                    Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.tool_calls = {New With {.index = 0, .id = "call_" & Guid.NewGuid().ToString("N").Substring(0, 6), .type = "function",
                                        .function = New With {.name = toolName, .arguments = "{""command"":""rm -rf build""}"}}}}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                End Function
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim run = Async Function(tool As Object, onEvent As Func(Of String, JsonElement, String, Task)) As Task(Of List(Of String))
                              Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                              Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                                  .Content = HttpApiTests.JsonBody(New With {.content = "clean up", .model = "fast/a", .settings = New With {.permissionMode = "auto"}, .clientTools = {tool}})}
                              req.Headers.Add("X-CSRF-Token", admin.Csrf)
                              Dim events As New List(Of String)
                              Await ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                                   events.Add(evt)
                                                                                                                                   Await onEvent(evt, data, id)
                                                                                                                               End Function)
                              Return events
                          End Function
                Dim answerTool = Async Function(evt As String, data As JsonElement, id As String) As Task
                                     If evt = "client_tool" Then Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "done"}, admin.Csrf)
                                 End Function
                ' Auto: a tool that changes things, with a destructive command, runs without a question.
                Dim plain = Await run(JsonNode.Parse("{""name"":""Bash"",""description"":""Run a command"",""parameters"":{""type"":""object""},""x-risk"":""change""}"), answerTool)
                Assert.DoesNotContain("approval", plain)
                Assert.Contains("client_tool", plain)
                Assert.Contains("done", plain)
                ' A tool the user set to ask first still asks in Auto; Stop refuses the approval and ends the answer.
                Dim stopped = False
                Dim askFirst = Await run(JsonNode.Parse("{""name"":""mcp__fs__write"",""description"":""Write"",""parameters"":{""type"":""object""},""x-risk"":""ask""}"),
                                         Async Function(evt, data, id)
                                             If evt = "approval" AndAlso Not stopped Then
                                                 stopped = True
                                                 Dim r = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/stop", "{}", admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                                                 Assert.True(r.GetProperty("stopped").GetBoolean())
                                             End If
                                         End Function)
                Assert.Contains("approval", askFirst)
                Assert.DoesNotContain("client_tool", askFirst)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub HtmlEscapedCodeInFileToolsIsRepaired()
        ' What qwen wrote into WebServer.vb (it then looped trying to fix the build).
        Assert.Equal("While sb.Length < maxBytes AndAlso Date.Now <= deadline", CodeEntities.Decode("While sb.Length &lt; maxBytes AndAlso Date.Now &lt;= deadline", ".vb"))
        ' Strings and comments that use entities on purpose stay as they are.
        Assert.Equal("Return s.Replace(""&"", ""&amp;"").Replace(""<"", ""&lt;"")", CodeEntities.Decode("Return s.Replace(""&"", ""&amp;"").Replace(""<"", ""&lt;"")", ".vb"))
        Assert.Equal("x = a & b ' turns < into &lt;", CodeEntities.Decode("x = a &amp; b ' turns < into &lt;", ".vb"))
        Assert.Equal("if (a < b && c > d) s = ""&lt;""; // &gt; stays", CodeEntities.Decode("if (a &lt; b &amp;&amp; c &gt; d) s = ""&lt;""; // &gt; stays", ".cs"))
        Assert.Equal("/* &lt; */ x => x < 1", CodeEntities.Decode("/* &lt; */ x =&gt; x &lt; 1", ".ts"))
        Assert.Equal("if x < 3:  # &lt; ok" & vbLf & "    y = '&amp;'", CodeEntities.Decode("if x &lt; 3:  # &lt; ok" & vbLf & "    y = '&amp;'", ".py"))
        ' Arguments of file tools (desktop app, CLI, multi-edit); markup files are left alone.
        Dim fixedArgs = CodeEntities.FixToolArguments("{""path"":""C:\\app\\WebServer.vb"",""content"":""If a &lt; b Then""}")
        Assert.Equal("If a < b Then", JsonNode.Parse(fixedArgs)("content").ToString())
        Assert.Contains("a < b", fixedArgs)                                            ' written as < (not \u003C) for the model's history
        Assert.Null(CodeEntities.FixToolArguments("{""path"":""site/index.html"",""content"":""&lt;p&gt;""}"))
        Assert.Null(CodeEntities.FixToolArguments("{""path"":""a.vb"",""content"":""If a < b Then""}"))
        Dim multi = JsonNode.Parse(CodeEntities.FixToolArguments("{""file_path"":""x.cs"",""edits"":[{""old_string"":""a &lt; b"",""new_string"":""a &gt;= b""}]}"))
        Assert.Equal("a < b", multi("edits")(0)("old_string").ToString())
        Assert.Equal("a >= b", multi("edits")(0)("new_string").ToString())
    End Sub

    <Fact>
    Public Async Function ClientFileToolsGetRepairedCode() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    Dim msgs = req("messages").AsArray()
                                    If msgs(msgs.Count - 1)("role")?.ToString() = "tool" OrElse req("tools") Is Nothing Then
                                        Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.content = "written"}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                    End If
                                    Return "data: " & JsonUtil.ToJson(New With {.choices = {New With {.index = 0, .delta = New With {.tool_calls = {New With {.index = 0, .id = "call_w1", .type = "function",
                                        .function = New With {.name = "write_file", .arguments = "{""path"":""C:\\p\\Program.vb"",""content"":""If n &lt; 10 AndAlso s = \""&lt;b&gt;\"" Then""}"}}}}}}}) & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                                End Function
            Dim env = Await StartAsync(up)
            Using env.Host
                Dim admin = Await LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = JsonDocument.Parse(Await (Await Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "write it", .model = "fast/a", .settings = New With {.permissionMode = "auto"},
                        .clientTools = {New With {.name = "write_file", .description = "Write a file", .parameters = New With {.type = "object"}}}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim got As String = Nothing
                Await ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                     If evt = "client_tool" Then
                                                                                                                         got = JsonNode.Parse(data.GetProperty("arguments").GetString())("content").ToString()
                                                                                                                         Await Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "ok"}, admin.Csrf)
                                                                                                                     End If
                                                                                                                 End Function)
                Assert.Equal("If n < 10 AndAlso s = ""&lt;b&gt;"" Then", got)   ' the comparison is repaired, the HTML in the string is not
                ' The model's next request shows its own call repaired.
                Dim second = up.Requests.Last(Function(q) q("stream")?.ToString() = "true")
                Assert.Contains("If n < 10", second("messages").ToJsonString(New JsonSerializerOptions With {.Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping}))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function StopReleasesARunThatDoesNotEnd() As Task
        Dim hub As New ChatLiveHub() With {.StopGrace = TimeSpan.FromMilliseconds(100)}
        Dim r = hub.BeginRun("chat_1", "admin", "client-a", Nothing, "m")
        Assert.True(hub.IsLive("chat_1"))
        Assert.True(hub.Stop("chat_1"))                  ' nothing cancels it (no stopper): it is released after the grace time
        Await Task.Delay(600)
        Assert.False(hub.IsLive("chat_1"))
        Dim newer = hub.BeginRun("chat_1", "admin", "client-b", Nothing, "m")
        hub.EndRun(r)                                    ' the stuck run ending late leaves the new one alone
        Assert.True(hub.IsLive("chat_1"))
        hub.EndRun(newer)
        Assert.False(hub.IsLive("chat_1"))
        Assert.False(hub.Stop("chat_1"))
    End Function
End Class
