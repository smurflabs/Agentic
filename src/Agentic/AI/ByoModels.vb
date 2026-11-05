Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports System.Threading.Channels
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities

Namespace AI

    ''' <summary>A model a user added with their own API key (My models).</summary>
    Public NotInheritable Class ByoModel
        Public Property Id As String
        Public Property Owner As String
        Public Property Name As String
        ''' <summary>openai | anthropic | bedrock</summary>
        Public Property Provider As String
        Public Property ModelName As String
        Public Property BaseUrl As String
        Public Property Region As String
        Public Property KeyHint As String
        Public Property ContextWindow As Integer = 128000
        Public Property MaxTokens As Integer = 8192
        Public Property Vision As Boolean
        Public Property Enabled As Boolean = True
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime?
        ''' <summary>Decrypted credentials; never sent to a client.</summary>
        <Text.Json.Serialization.JsonIgnore>
        Public Property Secret As ByoSecret
    End Class

    Public NotInheritable Class ByoSecret
        ''' <summary>OpenAI or Anthropic key, or an Amazon Bedrock API key (bearer token).</summary>
        Public Property ApiKey As String
        ''' <summary>Amazon Bedrock with IAM credentials (signed with SigV4) instead of an API key.</summary>
        Public Property AccessKeyId As String
        Public Property SecretAccessKey As String
        Public Property SessionToken As String
    End Class

    ''' <summary>What a user sends to add or change one of their models (the key is optional on a change: kept when empty).</summary>
    Public NotInheritable Class ByoModelInput
        Public Property Name As String
        Public Property Provider As String
        Public Property ModelName As String
        Public Property BaseUrl As String
        Public Property Region As String
        Public Property ApiKey As String
        Public Property AccessKeyId As String
        Public Property SecretAccessKey As String
        Public Property SessionToken As String
        Public Property ContextWindow As Integer?
        Public Property MaxTokens As Integer?
        Public Property Vision As Boolean?
        Public Property Enabled As Boolean?
    End Class

    Public NotInheritable Class ByoException
        Inherits Exception
        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' My models (bring your own API key). Users add OpenAI (or OpenAI-compatible), Anthropic and Amazon Bedrock models
    ''' with their own keys. Only the owner sees and uses them: in Chat and the OpenAI-compatible API (so the desktop app, the
    ''' CLI and VS Code too), and agents the owner owns when the model is in the agent's AllowedModels. They are free here:
    ''' the user pays the provider. Keys are stored encrypted (SecretBox) and never returned to a client.
    ''' Every call goes through ByoHandler, which speaks the OpenAI chat-completions format to the rest of Agentic and each
    ''' provider's own API to the provider.
    ''' </summary>
    Public NotInheritable Class ByoService
        Public Const IdPrefix As String = "byo-"
        Public Const ProviderName As String = "byo"
        Public Const MaxPerUser As Integer = 50
        Public Shared ReadOnly Providers As String() = {"openai", "anthropic", "bedrock"}

        Private ReadOnly _db As Database
        Private ReadOnly _box As SecretBox
        Private ReadOnly _registry As ModelRegistry
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _log As ILog
        Private ReadOnly _models As New ConcurrentDictionary(Of String, ByoModel)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _client As HttpClient

        Public ReadOnly Property Handler As ByoHandler

        Public Sub New(db As Database, box As SecretBox, registry As ModelRegistry, pricing As PricingService, log As ILog, Optional upstream As HttpMessageHandler = Nothing)
            _db = db
            _box = box
            _registry = registry
            _pricing = pricing
            _log = log
            Handler = New ByoHandler(Me, upstream)
            _client = New HttpClient(Handler, disposeHandler:=False) With {.Timeout = Timeout.InfiniteTimeSpan}
        End Sub

        Public Shared Function IsByoId(id As String) As Boolean
            Return id IsNot Nothing AndAlso id.StartsWith(IdPrefix, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Loads every user's models and makes them usable (chat, agents).</summary>
        Public Async Function LoadAsync() As Task
            Dim rows = Await _db.QueryAsync("SELECT * FROM UserModels", AddressOf Map).ConfigureAwait(False)
            For Each m In rows
                _models(m.Id) = m
                Register(m)
            Next
        End Function

        Private Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ByoModel
            Dim m As New ByoModel With {
                .Id = Db.Str(r, "Id"), .Owner = Db.Str(r, "Owner"), .Name = Db.Str(r, "Name"), .Provider = Db.Str(r, "Provider"),
                .ModelName = Db.Str(r, "ModelName"), .BaseUrl = Db.Str(r, "BaseUrl"), .Region = Db.Str(r, "Region"), .KeyHint = Db.Str(r, "KeyHint"),
                .ContextWindow = Db.Int(r, "ContextWindow"), .MaxTokens = Db.Int(r, "MaxTokens"), .Vision = Db.Bool(r, "Vision"),
                .Enabled = Db.Bool(r, "Enabled"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.DtN(r, "UpdatedUtc")}
            Dim plain = _box.Unprotect(Db.Str(r, "SecretJson"))
            If plain Is Nothing Then
                _log.Warn("MyModels", $"The key of {m.Id} ({m.Owner}) could not be decrypted (data\secrets.key changed?); the user must enter it again.")
                m.Secret = New ByoSecret()
            Else
                m.Secret = If(JsonSerializer.Deserialize(Of ByoSecret)(plain), New ByoSecret())
            End If
            Return m
        End Function

        Public Function [Get](id As String) As ByoModel
            Dim m As ByoModel = Nothing
            Return If(id IsNot Nothing AndAlso _models.TryGetValue(id, m), m, Nothing)
        End Function

        ''' <summary>Tests: a model in memory only (no database, not registered for agents).</summary>
        Friend Sub PutForTests(m As ByoModel)
            _models(m.Id) = m
        End Sub

        Public Function OwnerOf(id As String) As String
            Return [Get](id)?.Owner
        End Function

        ''' <summary>Whether this account may use the model: only its owner, and only while it is on.</summary>
        Public Function CanUse(username As String, id As String) As Boolean
            Dim m = [Get](id)
            Return m IsNot Nothing AndAlso m.Enabled AndAlso String.Equals(m.Owner, username, StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function ListFor(owner As String) As List(Of ByoModel)
            Return _models.Values.Where(Function(m) String.Equals(m.Owner, owner, StringComparison.OrdinalIgnoreCase)).
                OrderBy(Function(m) m.Name, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Adds (id Nothing) or changes one of the owner's models.</summary>
        Public Async Function SaveAsync(owner As String, id As String, input As ByoModelInput) As Task(Of ByoModel)
            If input Is Nothing Then Throw New ByoException("The model is required.")
            Dim existing As ByoModel = Nothing
            If id IsNot Nothing Then
                existing = [Get](id)
                If existing Is Nothing OrElse Not String.Equals(existing.Owner, owner, StringComparison.OrdinalIgnoreCase) Then Throw New ByoException("No such model.")
            ElseIf ListFor(owner).Count >= MaxPerUser Then
                Throw New ByoException($"You can add at most {MaxPerUser} models.")
            End If
            Dim provider = If(input.Provider, If(existing?.Provider, "")).Trim().ToLowerInvariant()
            If Not Providers.Contains(provider) Then Throw New ByoException("Provider must be openai, anthropic or bedrock.")
            Dim name = TextUtil.Truncate(If(input.Name, If(existing?.Name, "")).Trim(), 80)
            Dim modelName = If(input.ModelName, If(existing?.ModelName, "")).Trim()
            If modelName.Length = 0 OrElse modelName.Length > 200 Then Throw New ByoException("The provider's model id is required (e.g. gpt-4o, claude-sonnet-4-5, anthropic.claude-3-5-sonnet-20240620-v1:0).")
            If name.Length = 0 Then name = modelName
            Dim baseUrl = If(input.BaseUrl IsNot Nothing, input.BaseUrl, existing?.BaseUrl)
            baseUrl = If(String.IsNullOrWhiteSpace(baseUrl), Nothing, baseUrl.Trim().TrimEnd("/"c))
            If baseUrl IsNot Nothing Then
                Dim u As Uri = Nothing
                If Not Uri.TryCreate(baseUrl, UriKind.Absolute, u) OrElse (u.Scheme <> "https" AndAlso u.Scheme <> "http") Then Throw New ByoException("The API address must be an http(s) URL.")
            End If
            Dim region = If(input.Region IsNot Nothing, input.Region, existing?.Region)
            region = If(String.IsNullOrWhiteSpace(region), Nothing, region.Trim().ToLowerInvariant())
            If provider = "bedrock" AndAlso region Is Nothing AndAlso baseUrl Is Nothing Then Throw New ByoException("Amazon Bedrock needs a region (e.g. us-east-1).")
            If region IsNot Nothing AndAlso Not Text.RegularExpressions.Regex.IsMatch(region, "^[a-z]{2}(-[a-z]+)+-\d$") Then Throw New ByoException("The region looks wrong (e.g. us-east-1).")

            ' Credentials: kept when a change leaves them empty.
            Dim secret = If(existing?.Secret, New ByoSecret())
            Dim newSecret As New ByoSecret With {.ApiKey = secret.ApiKey, .AccessKeyId = secret.AccessKeyId, .SecretAccessKey = secret.SecretAccessKey, .SessionToken = secret.SessionToken}
            If Not String.IsNullOrWhiteSpace(input.ApiKey) Then
                newSecret.ApiKey = input.ApiKey.Trim() : newSecret.AccessKeyId = Nothing : newSecret.SecretAccessKey = Nothing : newSecret.SessionToken = Nothing
            End If
            If Not String.IsNullOrWhiteSpace(input.AccessKeyId) OrElse Not String.IsNullOrWhiteSpace(input.SecretAccessKey) Then
                If provider <> "bedrock" Then Throw New ByoException("Access keys are for Amazon Bedrock; OpenAI and Anthropic use an API key.")
                If String.IsNullOrWhiteSpace(input.AccessKeyId) OrElse String.IsNullOrWhiteSpace(input.SecretAccessKey) Then Throw New ByoException("Enter both the access key id and the secret access key.")
                newSecret.ApiKey = Nothing
                newSecret.AccessKeyId = input.AccessKeyId.Trim() : newSecret.SecretAccessKey = input.SecretAccessKey.Trim()
                newSecret.SessionToken = If(String.IsNullOrWhiteSpace(input.SessionToken), Nothing, input.SessionToken.Trim())
            End If
            Dim hasKey = Not String.IsNullOrEmpty(newSecret.ApiKey) OrElse (Not String.IsNullOrEmpty(newSecret.AccessKeyId) AndAlso Not String.IsNullOrEmpty(newSecret.SecretAccessKey))
            ' A key is required, except for an OpenAI-compatible server of the user's own (a local LM Studio, Ollama …).
            If Not hasKey AndAlso Not (provider = "openai" AndAlso baseUrl IsNot Nothing) Then Throw New ByoException("Enter the API key.")
            Dim hint = If(Not String.IsNullOrEmpty(newSecret.ApiKey), Last4(newSecret.ApiKey), If(Not String.IsNullOrEmpty(newSecret.AccessKeyId), Last4(newSecret.AccessKeyId), Nothing))

            Dim ctx = Math.Clamp(If(input.ContextWindow, If(existing?.ContextWindow, 128000)), 1024, 10_000_000)
            Dim maxTok = Math.Clamp(If(input.MaxTokens, If(existing?.MaxTokens, 8192)), 16, 1_000_000)
            Dim m As New ByoModel With {
                .Id = If(existing?.Id, IdPrefix & RandomNumberGenerator.GetHexString(12, lowercase:=True)), .Owner = If(existing?.Owner, owner),
                .Name = name, .Provider = provider, .ModelName = modelName, .BaseUrl = baseUrl, .Region = region, .KeyHint = hint,
                .ContextWindow = ctx, .MaxTokens = maxTok, .Vision = If(input.Vision, If(existing?.Vision, False)),
                .Enabled = If(input.Enabled, If(existing?.Enabled, True)), .CreatedUtc = If(existing?.CreatedUtc, DateTime.UtcNow),
                .UpdatedUtc = If(existing Is Nothing, CType(Nothing, DateTime?), DateTime.UtcNow), .Secret = newSecret}
            Await _db.ExecuteAsync(
                "INSERT INTO UserModels(Id, Owner, Name, Provider, ModelName, BaseUrl, Region, SecretJson, KeyHint, ContextWindow, MaxTokens, Vision, Enabled, CreatedUtc, UpdatedUtc) " &
                "VALUES (@id, @o, @n, @p, @mn, @b, @r, @s, @h, @c, @mt, @v, @e, @cu, @uu) " &
                "ON CONFLICT(Id) DO UPDATE SET Name=@n, Provider=@p, ModelName=@mn, BaseUrl=@b, Region=@r, SecretJson=@s, KeyHint=@h, ContextWindow=@c, MaxTokens=@mt, Vision=@v, Enabled=@e, UpdatedUtc=@uu",
                ("@id", CObj(m.Id)), ("@o", m.Owner), ("@n", m.Name), ("@p", m.Provider), ("@mn", m.ModelName), ("@b", m.BaseUrl), ("@r", m.Region),
                ("@s", _box.Protect(JsonSerializer.Serialize(newSecret))), ("@h", hint), ("@c", m.ContextWindow), ("@mt", m.MaxTokens),
                ("@v", If(m.Vision, 1, 0)), ("@e", If(m.Enabled, 1, 0)), ("@cu", Db.Ts(m.CreatedUtc)), ("@uu", Db.Ts(m.UpdatedUtc))).ConfigureAwait(False)
            _models(m.Id) = m
            Register(m)
            Return m
        End Function

        Public Async Function DeleteAsync(owner As String, id As String) As Task(Of Boolean)
            Dim m = [Get](id)
            If m Is Nothing OrElse Not String.Equals(m.Owner, owner, StringComparison.OrdinalIgnoreCase) Then Return False
            Await _db.ExecuteAsync("DELETE FROM UserModels WHERE Id=@id", ("@id", CObj(id))).ConfigureAwait(False)
            Dim gone As ByoModel = Nothing
            _models.TryRemove(id, gone)
            _registry.Remove(id)
            _pricing.Unregister(id)
            Return True
        End Function

        ''' <summary>Agents use it through the model registry (provider "byo"); free, so it never needs a price.</summary>
        Private Sub Register(m As ByoModel)
            Dim caps As New List(Of ModelCapability) From {ModelCapability.ToolCalling, ModelCapability.Streaming}
            If m.Vision Then caps.Add(ModelCapability.Vision)
            _registry.AddOrReplace(New ModelOptions With {
                .Key = m.Id, .ModelId = m.Id, .Provider = ProviderName, .Enabled = m.Enabled, .Tier = 1, .IsLocal = False,
                .Description = $"{m.Name} ({m.Owner}'s own {ProviderLabel(m.Provider)} key)", .Capabilities = caps,
                .AllowedPurposes = New List(Of AiPurpose), .MaxTokens = m.MaxTokens, .Temperature = Nothing, .TimeoutSeconds = 600,
                .ContextWindowTokens = m.ContextWindow, .MaxConcurrency = 8, .ByoOwner = m.Owner, .CreatedBy = m.Owner,
                .UseFor = $"{m.Owner}'s own model: free to use."})
            ' Free: the user pays the provider directly.
            _pricing.RegisterModel(m.Id, True, 0D, 0D, Nothing)
        End Sub

        Public Shared Function ProviderLabel(p As String) As String
            Select Case p
                Case "anthropic" : Return "Anthropic"
                Case "bedrock" : Return "Amazon Bedrock"
                Case Else : Return "OpenAI"
            End Select
        End Function

        Private Shared Function Last4(s As String) As String
            Return If(s.Length <= 4, "…", "…" & s.Substring(s.Length - 4))
        End Function

        ''' <summary>An OpenAI chat-completions call (body with "model" = a byo- id), answered by that model's provider.</summary>
        Public Function SendAsync(body As String, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim req As New HttpRequestMessage(HttpMethod.Post, "http://byo.invalid/chat/completions") With {.Content = New StringContent(body, Encoding.UTF8, "application/json")}
            Return _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
        End Function

        ''' <summary>Checks a model answers: a one-word request. Returns (ok, the answer or the provider's error).</summary>
        Public Async Function TestAsync(id As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Message As String))
            Dim body As New JsonObject From {
                {"model", id}, {"stream", False}, {"max_tokens", 32},
                {"messages", New JsonArray(New JsonObject From {{"role", "user"}, {"content", "Reply with exactly: OK"}})}}
            Try
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(TimeSpan.FromSeconds(60))
                    Using resp = Await SendAsync(body.ToJsonString(), cts.Token).ConfigureAwait(False)
                        Dim text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                        Dim j As JsonNode = Nothing
                        Try
                            j = JsonNode.Parse(text)
                        Catch ex As JsonException
                        End Try
                        If Not resp.IsSuccessStatusCode Then
                            Return (False, $"HTTP {CInt(resp.StatusCode)}: {If(j?("error")?("message")?.ToString(), TextUtil.Truncate(text, 300))}")
                        End If
                        Dim answer = JsonUtil.FirstChoice(j)?("message")?("content")?.ToString()
                        Return (True, If(String.IsNullOrWhiteSpace(answer), "(the model answered with no text)", TextUtil.Truncate(answer.Trim(), 200)))
                    End Using
                End Using
            Catch ex As OperationCanceledException
                Return (False, "No answer within 60 seconds.")
            Catch ex As HttpRequestException
                Return (False, ex.Message)
            End Try
        End Function
    End Class

    ''' <summary>
    ''' Speaks the OpenAI chat-completions format (what Chat, the API and the agents send) and calls each user model's own
    ''' provider: OpenAI or an OpenAI-compatible server as is; Anthropic's Messages API (answers streamed, translated event
    ''' by event); Amazon Bedrock's Converse API (with a Bedrock API key or IAM keys signed with SigV4; the answer arrives
    ''' whole). Errors come back as {"error":{"message"}} with the provider's status.
    ''' </summary>
    Public NotInheritable Class ByoHandler
        Inherits HttpMessageHandler

        Private ReadOnly _service As ByoService
        Private ReadOnly _http As HttpClient

        Public Sub New(service As ByoService, Optional upstream As HttpMessageHandler = Nothing)
            _service = service
            _http = New HttpClient(If(upstream, New SocketsHttpHandler With {
                .ConnectTimeout = TimeSpan.FromSeconds(20), .PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate Or DecompressionMethods.Brotli})) With {.Timeout = Timeout.InfiniteTimeSpan}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic/1.0")
        End Sub

        Protected Overrides Async Function SendAsync(request As HttpRequestMessage, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim bodyText = If(request.Content Is Nothing, "{}", Await request.Content.ReadAsStringAsync(ct).ConfigureAwait(False))
            Dim body As JsonObject
            Try
                body = TryCast(JsonNode.Parse(bodyText), JsonObject)
            Catch ex As JsonException
                body = Nothing
            End Try
            If body Is Nothing Then Return ErrorResponse(HttpStatusCode.BadRequest, "The request is not JSON.")
            Dim id = body("model")?.ToString()
            Dim m = _service.Get(id)
            If m Is Nothing Then Return ErrorResponse(HttpStatusCode.NotFound, $"The model '{id}' does not exist (it was removed from My models).")
            If Not m.Enabled Then Return ErrorResponse(HttpStatusCode.Forbidden, $"{m.Name} is switched off in My models.")
            Try
                Select Case m.Provider
                    Case "anthropic" : Return Await AnthropicAsync(m, body, ct).ConfigureAwait(False)
                    Case "bedrock" : Return Await BedrockAsync(m, body, ct).ConfigureAwait(False)
                    Case Else : Return Await OpenAiAsync(m, body, ct).ConfigureAwait(False)
                End Select
            Catch ex As HttpRequestException
                Return ErrorResponse(HttpStatusCode.BadGateway, $"{ByoService.ProviderLabel(m.Provider)}: {ex.Message}")
            End Try
        End Function

        ' ------------------------------------------------------------------ OpenAI (and OpenAI-compatible servers)

        Private Async Function OpenAiAsync(m As ByoModel, body As JsonObject, ct As CancellationToken) As Task(Of HttpResponseMessage)
            body("model") = m.ModelName
            Dim base = If(m.BaseUrl, "https://api.openai.com/v1")
            Dim req As New HttpRequestMessage(HttpMethod.Post, base & "/chat/completions") With {.Content = New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")}
            If Not String.IsNullOrEmpty(m.Secret?.ApiKey) Then req.Headers.Authorization = New AuthenticationHeaderValue("Bearer", m.Secret.ApiKey)
            req.Headers.Accept.ParseAdd("application/json")
            req.Headers.Accept.ParseAdd("text/event-stream")
            Dim resp = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
            If resp.IsSuccessStatusCode Then Return resp
            Return Await ProviderError(resp, "OpenAI", ct).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ the conversation in a provider-neutral shape

        Private NotInheritable Class Block
            Public Kind As String          ' text | image | tool_use | tool_result
            Public Text As String
            Public MediaType As String
            Public Data As String          ' base64 (image)
            Public Url As String           ' image by address
            Public ToolId As String
            Public ToolName As String
            Public Input As JsonNode
        End Class

        Private NotInheritable Class Turn
            Public Role As String          ' user | assistant
            Public ReadOnly Blocks As New List(Of Block)
        End Class

        ''' <summary>OpenAI messages as alternating user/assistant turns (tool results belong to the user), plus the system text.</summary>
        Private Shared Function ToTurns(messages As JsonArray, ByRef system As String) As List(Of Turn)
            Dim sys As New StringBuilder()
            Dim turns As New List(Of Turn)
            Dim add = Sub(role As String, b As Block)
                          If turns.Count = 0 OrElse turns(turns.Count - 1).Role <> role Then turns.Add(New Turn With {.Role = role})
                          turns(turns.Count - 1).Blocks.Add(b)
                      End Sub
            For Each n In If(messages, New JsonArray())
                Dim msg = TryCast(n, JsonObject)
                If msg Is Nothing Then Continue For
                Dim role = msg("role")?.ToString()
                Select Case role
                    Case "system", "developer"
                        Dim t = ContentText(msg("content"))
                        If t.Length > 0 Then sys.Append(If(sys.Length > 0, vbLf & vbLf, "")).Append(t)
                    Case "tool"
                        add("user", New Block With {.Kind = "tool_result", .ToolId = msg("tool_call_id")?.ToString(), .Text = ContentText(msg("content"))})
                    Case "assistant"
                        Dim t = ContentText(msg("content"))
                        If t.Trim().Length > 0 Then add("assistant", New Block With {.Kind = "text", .Text = t})
                        For Each tc In If(TryCast(msg("tool_calls"), JsonArray), New JsonArray())
                            Dim fn = tc?("function")
                            Dim input As JsonNode = Nothing
                            Try
                                input = JsonNode.Parse(If(fn?("arguments")?.ToString(), "{}"))
                            Catch ex As JsonException
                            End Try
                            If TypeOf input IsNot JsonObject Then input = New JsonObject()
                            add("assistant", New Block With {.Kind = "tool_use", .ToolId = tc?("id")?.ToString(), .ToolName = fn?("name")?.ToString(), .Input = input})
                        Next
                    Case Else ' user
                        Dim content = msg("content")
                        Dim parts = TryCast(content, JsonArray)
                        If parts Is Nothing Then
                            Dim t = ContentText(content)
                            If t.Length > 0 Then add("user", New Block With {.Kind = "text", .Text = t})
                        Else
                            For Each p In parts
                                Dim type = p?("type")?.ToString()
                                If type = "image_url" Then
                                    Dim url = If(p?("image_url")?("url")?.ToString(), p?("image_url")?.ToString())
                                    Dim b = ImageBlock(url)
                                    If b IsNot Nothing Then add("user", b)
                                ElseIf p?("text") IsNot Nothing Then
                                    Dim t = p("text").ToString()
                                    If t.Length > 0 Then add("user", New Block With {.Kind = "text", .Text = t})
                                End If
                            Next
                        End If
                End Select
            Next
            ' Both APIs want the conversation to start with the user and no empty turns.
            If turns.Count = 0 OrElse turns(0).Role <> "user" Then
                Dim first As New Turn With {.Role = "user"}
                first.Blocks.Add(New Block With {.Kind = "text", .Text = "(continue)"})
                turns.Insert(0, first)
            End If
            system = sys.ToString()
            Return turns
        End Function

        Private Shared Function ContentText(content As JsonNode) As String
            If content Is Nothing Then Return ""
            Dim parts = TryCast(content, JsonArray)
            If parts Is Nothing Then Return content.ToString()
            Return String.Join(vbLf, parts.Select(Function(p) If(p?("text")?.ToString(), "")).Where(Function(s) s.Length > 0))
        End Function

        Private Shared Function ImageBlock(url As String) As Block
            If String.IsNullOrEmpty(url) Then Return Nothing
            Dim m = Text.RegularExpressions.Regex.Match(url, "^data:(?<mt>[\w/+.-]+);base64,(?<d>.+)$", Text.RegularExpressions.RegexOptions.Singleline)
            If m.Success Then Return New Block With {.Kind = "image", .MediaType = m.Groups("mt").Value, .Data = m.Groups("d").Value}
            Return New Block With {.Kind = "image", .Url = url}
        End Function

        Private Shared Function MaxTokensOf(m As ByoModel, body As JsonObject) As Integer
            Dim n As Integer
            Dim v = If(body("max_tokens"), body("max_completion_tokens"))
            If v IsNot Nothing AndAlso Integer.TryParse(v.ToString(), n) AndAlso n > 0 Then Return Math.Min(n, m.MaxTokens)
            Return m.MaxTokens
        End Function

        ' ------------------------------------------------------------------ Anthropic (Messages API)

        Private Async Function AnthropicAsync(m As ByoModel, body As JsonObject, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim system As String = Nothing
            Dim turns = ToTurns(TryCast(body("messages"), JsonArray), system)
            Dim msgs As New JsonArray()
            For Each t In turns
                Dim content As New JsonArray()
                ' Tool results lead the user's turn.
                For Each b In t.Blocks.OrderBy(Function(x) If(x.Kind = "tool_result", 0, 1))
                    Select Case b.Kind
                        Case "text" : content.Add(New JsonObject From {{"type", "text"}, {"text", b.Text}})
                        Case "image"
                            content.Add(New JsonObject From {{"type", "image"}, {"source", If(b.Data IsNot Nothing,
                                New JsonObject From {{"type", "base64"}, {"media_type", b.MediaType}, {"data", b.Data}},
                                New JsonObject From {{"type", "url"}, {"url", b.Url}})}})
                        Case "tool_use" : content.Add(New JsonObject From {{"type", "tool_use"}, {"id", b.ToolId}, {"name", b.ToolName}, {"input", b.Input.DeepClone()}})
                        Case "tool_result" : content.Add(New JsonObject From {{"type", "tool_result"}, {"tool_use_id", b.ToolId}, {"content", If(b.Text.Length > 0, b.Text, "(no output)")}})
                    End Select
                Next
                If content.Count = 0 Then content.Add(New JsonObject From {{"type", "text"}, {"text", "(empty)"}})
                msgs.Add(New JsonObject From {{"role", t.Role}, {"content", content}})
            Next
            Dim stream = body("stream") IsNot Nothing AndAlso body("stream").GetValueKind() = JsonValueKind.True
            Dim req As New JsonObject From {{"model", m.ModelName}, {"max_tokens", MaxTokensOf(m, body)}, {"messages", msgs}, {"stream", stream}}
            If system.Length > 0 Then req("system") = system
            Dim temp = body("temperature")
            If temp IsNot Nothing Then req("temperature") = temp.DeepClone()
            Dim tools = TryCast(body("tools"), JsonArray)
            If tools IsNot Nothing AndAlso tools.Count > 0 Then
                Dim list As New JsonArray()
                For Each t In tools
                    Dim fn = t?("function")
                    If fn Is Nothing Then Continue For
                    list.Add(New JsonObject From {{"name", fn("name")?.ToString()}, {"description", If(fn("description")?.ToString(), "")},
                                                  {"input_schema", If(fn("parameters")?.DeepClone(), New JsonObject From {{"type", "object"}, {"properties", New JsonObject()}})}})
                Next
                req("tools") = list
                Dim choice = body("tool_choice")?.ToString()
                If choice = "required" Then req("tool_choice") = New JsonObject From {{"type", "any"}}
            End If
            Dim base = If(m.BaseUrl, "https://api.anthropic.com")
            Dim msg As New HttpRequestMessage(HttpMethod.Post, base & "/v1/messages") With {.Content = New StringContent(req.ToJsonString(), Encoding.UTF8, "application/json")}
            msg.Headers.TryAddWithoutValidation("x-api-key", If(m.Secret?.ApiKey, ""))
            msg.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01")
            Dim resp = Await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
            If Not resp.IsSuccessStatusCode Then Return Await ProviderError(resp, "Anthropic", ct).ConfigureAwait(False)
            If stream Then Return StreamResponse(Function(ch) TranslateAnthropicStreamAsync(resp, ch, ct))
            Using resp
                Dim j = JsonNode.Parse(Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False))
                Dim text As New StringBuilder(), reasoning As New StringBuilder()
                Dim calls As New JsonArray()
                For Each b In If(TryCast(j?("content"), JsonArray), New JsonArray())
                    Select Case b?("type")?.ToString()
                        Case "text" : text.Append(b("text")?.ToString())
                        Case "thinking" : reasoning.Append(b("thinking")?.ToString())
                        Case "tool_use"
                            calls.Add(New JsonObject From {{"id", b("id")?.ToString()}, {"type", "function"},
                                                           {"function", New JsonObject From {{"name", b("name")?.ToString()}, {"arguments", If(b("input")?.ToJsonString(), "{}")}}}})
                    End Select
                Next
                Return CompletionResponse(text.ToString(), reasoning.ToString(), calls, AnthropicStop(j?("stop_reason")?.ToString()),
                                          IntOf(j?("usage")?("input_tokens")) + IntOf(j?("usage")?("cache_read_input_tokens")) + IntOf(j?("usage")?("cache_creation_input_tokens")),
                                          IntOf(j?("usage")?("output_tokens")))
            End Using
        End Function

        Private Shared Function AnthropicStop(reason As String) As String
            Select Case reason
                Case "tool_use" : Return "tool_calls"
                Case "max_tokens" : Return "length"
                Case Else : Return "stop"
            End Select
        End Function

        ''' <summary>Anthropic's events as OpenAI chunks: text, thinking (reasoning_content), tool calls, usage and the end.</summary>
        Private Shared Async Function TranslateAnthropicStreamAsync(resp As HttpResponseMessage, ch As ChannelWriter(Of String), ct As CancellationToken) As Task
            Using resp
                Using s = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False), reader As New StreamReader(s, Encoding.UTF8)
                    Dim toolIndex As New Dictionary(Of Integer, Integer)
                    Dim promptTokens = 0, completionTokens = 0
                    Dim finish = "stop"
                    While True
                        Dim line = Await reader.ReadLineAsync(ct).ConfigureAwait(False)
                        If line Is Nothing Then Exit While
                        If Not line.StartsWith("data:", StringComparison.Ordinal) Then Continue While
                        Dim ev As JsonNode
                        Try
                            ev = JsonNode.Parse(line.Substring(5).Trim())
                        Catch ex As JsonException
                            Continue While
                        End Try
                        Select Case ev?("type")?.ToString()
                            Case "message_start"
                                Dim u = ev("message")?("usage")
                                promptTokens = IntOf(u?("input_tokens")) + IntOf(u?("cache_read_input_tokens")) + IntOf(u?("cache_creation_input_tokens"))
                            Case "content_block_start"
                                Dim cb = ev("content_block")
                                If cb?("type")?.ToString() = "tool_use" Then
                                    Dim i = toolIndex.Count
                                    toolIndex(IntOf(ev("index"))) = i
                                    Await ch.WriteAsync(Chunk(New JsonObject From {{"tool_calls", New JsonArray(New JsonObject From {
                                        {"index", i}, {"id", cb("id")?.ToString()}, {"type", "function"},
                                        {"function", New JsonObject From {{"name", cb("name")?.ToString()}, {"arguments", ""}}}})}}), ct).ConfigureAwait(False)
                                End If
                            Case "content_block_delta"
                                Dim d = ev("delta")
                                Select Case d?("type")?.ToString()
                                    Case "text_delta" : Await ch.WriteAsync(Chunk(New JsonObject From {{"content", d("text")?.ToString()}}), ct).ConfigureAwait(False)
                                    Case "thinking_delta" : Await ch.WriteAsync(Chunk(New JsonObject From {{"reasoning_content", d("thinking")?.ToString()}}), ct).ConfigureAwait(False)
                                    Case "input_json_delta"
                                        Dim i As Integer
                                        If toolIndex.TryGetValue(IntOf(ev("index")), i) Then
                                            Await ch.WriteAsync(Chunk(New JsonObject From {{"tool_calls", New JsonArray(New JsonObject From {
                                                {"index", i}, {"function", New JsonObject From {{"arguments", d("partial_json")?.ToString()}}}})}}), ct).ConfigureAwait(False)
                                        End If
                                End Select
                            Case "message_delta"
                                finish = AnthropicStop(ev("delta")?("stop_reason")?.ToString())
                                completionTokens = IntOf(ev("usage")?("output_tokens"))
                            Case "error"
                                Await ch.WriteAsync("data: " & New JsonObject From {{"error", New JsonObject From {{"message", "Anthropic: " & If(ev("error")?("message")?.ToString(), "error")}}}}.ToJsonString() & vbLf & vbLf, ct).ConfigureAwait(False)
                                Return
                        End Select
                    End While
                    Await ch.WriteAsync(Chunk(New JsonObject(), finish), ct).ConfigureAwait(False)
                    Await ch.WriteAsync(UsageChunk(promptTokens, completionTokens), ct).ConfigureAwait(False)
                    Await ch.WriteAsync("data: [DONE]" & vbLf & vbLf, ct).ConfigureAwait(False)
                End Using
            End Using
        End Function

        ' ------------------------------------------------------------------ Amazon Bedrock (Converse API)

        Private Async Function BedrockAsync(m As ByoModel, body As JsonObject, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim system As String = Nothing
            Dim turns = ToTurns(TryCast(body("messages"), JsonArray), system)
            Dim tools = TryCast(body("tools"), JsonArray)
            Dim hasTools = tools IsNot Nothing AndAlso tools.Count > 0
            Dim msgs As New JsonArray()
            For Each t In turns
                Dim content As New JsonArray()
                For Each b In t.Blocks.OrderBy(Function(x) If(x.Kind = "tool_result", 0, 1))
                    Select Case b.Kind
                        Case "text" : content.Add(New JsonObject From {{"text", b.Text}})
                        Case "image"
                            If b.Data IsNot Nothing Then
                                Dim fmt = b.MediaType.Substring(b.MediaType.IndexOf("/"c) + 1).Replace("jpg", "jpeg")
                                content.Add(New JsonObject From {{"image", New JsonObject From {{"format", fmt}, {"source", New JsonObject From {{"bytes", b.Data}}}}}})
                            Else
                                content.Add(New JsonObject From {{"text", "[image: " & b.Url & "]"}})
                            End If
                        Case "tool_use"
                            ' Without tools in this request Bedrock refuses tool blocks: they go as text then.
                            If hasTools Then
                                content.Add(New JsonObject From {{"toolUse", New JsonObject From {{"toolUseId", b.ToolId}, {"name", b.ToolName}, {"input", b.Input.DeepClone()}}}})
                            Else
                                content.Add(New JsonObject From {{"text", $"[called {b.ToolName} with {b.Input.ToJsonString()}]"}})
                            End If
                        Case "tool_result"
                            If hasTools Then
                                content.Add(New JsonObject From {{"toolResult", New JsonObject From {{"toolUseId", b.ToolId},
                                    {"content", New JsonArray(New JsonObject From {{"text", If(b.Text.Length > 0, b.Text, "(no output)")}})}}}})
                            Else
                                content.Add(New JsonObject From {{"text", "[tool result] " & b.Text}})
                            End If
                    End Select
                Next
                If content.Count = 0 Then content.Add(New JsonObject From {{"text", "(empty)"}})
                msgs.Add(New JsonObject From {{"role", t.Role}, {"content", content}})
            Next
            Dim req As New JsonObject From {{"messages", msgs}}
            If system.Length > 0 Then req("system") = New JsonArray(New JsonObject From {{"text", system}})
            Dim inference As New JsonObject From {{"maxTokens", MaxTokensOf(m, body)}}
            If body("temperature") IsNot Nothing Then inference("temperature") = body("temperature").DeepClone()
            req("inferenceConfig") = inference
            If hasTools Then
                Dim list As New JsonArray()
                For Each t In tools
                    Dim fn = t?("function")
                    If fn Is Nothing Then Continue For
                    list.Add(New JsonObject From {{"toolSpec", New JsonObject From {
                        {"name", fn("name")?.ToString()}, {"description", If(fn("description")?.ToString(), fn("name")?.ToString())},
                        {"inputSchema", New JsonObject From {{"json", If(fn("parameters")?.DeepClone(), New JsonObject From {{"type", "object"}, {"properties", New JsonObject()}})}}}}}})
                Next
                req("toolConfig") = New JsonObject From {{"tools", list}}
            End If
            Dim host = If(m.BaseUrl, $"https://bedrock-runtime.{m.Region}.amazonaws.com")
            Dim path = "/model/" & Uri.EscapeDataString(m.ModelName) & "/converse"
            Dim payload = req.ToJsonString()
            Dim msg As New HttpRequestMessage(HttpMethod.Post, New Uri(host & path)) With {.Content = New StringContent(payload, Encoding.UTF8, "application/json")}
            If Not String.IsNullOrEmpty(m.Secret?.ApiKey) Then
                msg.Headers.Authorization = New AuthenticationHeaderValue("Bearer", m.Secret.ApiKey)
            Else
                SigV4.Sign(msg, payload, If(m.Region, RegionFromHost(host)), "bedrock", m.Secret?.AccessKeyId, m.Secret?.SecretAccessKey, m.Secret?.SessionToken, DateTime.UtcNow)
            End If
            Using resp = Await _http.SendAsync(msg, ct).ConfigureAwait(False)
                If Not resp.IsSuccessStatusCode Then Return Await ProviderError(resp, "Amazon Bedrock", ct).ConfigureAwait(False)
                Dim j = JsonNode.Parse(Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False))
                Dim text As New StringBuilder(), reasoning As New StringBuilder()
                Dim calls As New JsonArray()
                For Each b In If(TryCast(j?("output")?("message")?("content"), JsonArray), New JsonArray())
                    If b?("text") IsNot Nothing Then text.Append(b("text").ToString())
                    Dim rc = b?("reasoningContent")?("reasoningText")?("text")
                    If rc IsNot Nothing Then reasoning.Append(rc.ToString())
                    Dim tu = b?("toolUse")
                    If tu IsNot Nothing Then
                        calls.Add(New JsonObject From {{"id", tu("toolUseId")?.ToString()}, {"type", "function"},
                                                       {"function", New JsonObject From {{"name", tu("name")?.ToString()}, {"arguments", If(tu("input")?.ToJsonString(), "{}")}}}})
                    End If
                Next
                Dim finish = Select_(j?("stopReason")?.ToString())
                Dim pt = IntOf(j?("usage")?("inputTokens")), ctk = IntOf(j?("usage")?("outputTokens"))
                Dim stream = body("stream") IsNot Nothing AndAlso body("stream").GetValueKind() = JsonValueKind.True
                If Not stream Then Return CompletionResponse(text.ToString(), reasoning.ToString(), calls, finish, pt, ctk)
                ' Asked for a stream: the whole answer as one, in the stream's format.
                Dim sb As New StringBuilder()
                If reasoning.Length > 0 Then sb.Append(Chunk(New JsonObject From {{"reasoning_content", reasoning.ToString()}}))
                If text.Length > 0 Then sb.Append(Chunk(New JsonObject From {{"content", text.ToString()}}))
                For i = 0 To calls.Count - 1
                    Dim c = calls(i).DeepClone().AsObject()
                    c("index") = i
                    sb.Append(Chunk(New JsonObject From {{"tool_calls", New JsonArray(c)}}))
                Next
                sb.Append(Chunk(New JsonObject(), finish)).Append(UsageChunk(pt, ctk)).Append("data: [DONE]" & vbLf & vbLf)
                Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(sb.ToString(), Encoding.UTF8, "text/event-stream")}
            End Using
        End Function

        Private Shared Function Select_(stopReason As String) As String
            Select Case stopReason
                Case "tool_use" : Return "tool_calls"
                Case "max_tokens" : Return "length"
                Case Else : Return "stop"
            End Select
        End Function

        Private Shared Function RegionFromHost(host As String) As String
            Dim m = Text.RegularExpressions.Regex.Match(host, "bedrock-runtime(?:-fips)?\.([a-z0-9-]+)\.amazonaws")
            Return If(m.Success, m.Groups(1).Value, "us-east-1")
        End Function

        ' ------------------------------------------------------------------ OpenAI-shaped answers

        Private Shared Function IntOf(n As JsonNode) As Integer
            Dim v As Integer
            Return If(n IsNot Nothing AndAlso Integer.TryParse(n.ToString(), v), v, 0)
        End Function

        Private Shared Function Chunk(delta As JsonObject, Optional finish As String = Nothing) As String
            Dim choice As New JsonObject From {{"index", 0}, {"delta", delta}, {"finish_reason", If(finish Is Nothing, Nothing, CType(JsonValue.Create(finish), JsonNode))}}
            Return "data: " & New JsonObject From {{"object", "chat.completion.chunk"}, {"choices", New JsonArray(choice)}}.ToJsonString() & vbLf & vbLf
        End Function

        Private Shared Function UsageChunk(prompt As Integer, completion As Integer) As String
            Return "data: " & New JsonObject From {{"object", "chat.completion.chunk"}, {"choices", New JsonArray()},
                {"usage", New JsonObject From {{"prompt_tokens", prompt}, {"completion_tokens", completion}, {"total_tokens", prompt + completion}}}}.ToJsonString() & vbLf & vbLf
        End Function

        Private Shared Function CompletionResponse(text As String, reasoning As String, calls As JsonArray, finish As String, prompt As Integer, completion As Integer) As HttpResponseMessage
            Dim message As New JsonObject From {{"role", "assistant"}, {"content", text}}
            If reasoning.Length > 0 Then message("reasoning_content") = reasoning
            If calls.Count > 0 Then message("tool_calls") = calls
            Dim j As New JsonObject From {{"object", "chat.completion"},
                {"choices", New JsonArray(New JsonObject From {{"index", 0}, {"message", message}, {"finish_reason", finish}})},
                {"usage", New JsonObject From {{"prompt_tokens", prompt}, {"completion_tokens", completion}, {"total_tokens", prompt + completion}}}}
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = New StringContent(j.ToJsonString(), Encoding.UTF8, "application/json")}
        End Function

        Private Shared Function ErrorResponse(status As HttpStatusCode, message As String) As HttpResponseMessage
            Return New HttpResponseMessage(status) With {
                .Content = New StringContent(New JsonObject From {{"error", New JsonObject From {{"message", message}}}}.ToJsonString(), Encoding.UTF8, "application/json")}
        End Function

        ''' <summary>The provider's refusal as {"error":{"message"}} with its status (so context-size and vision errors are recognised).</summary>
        Private Shared Async Function ProviderError(resp As HttpResponseMessage, label As String, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Using resp
                Dim text = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Dim message As String = Nothing
                Try
                    Dim j = JsonNode.Parse(text)
                    message = If(j?("error")?("message")?.ToString(), If(j?("message")?.ToString(), j?("Message")?.ToString()))
                Catch ex As JsonException
                End Try
                If String.IsNullOrWhiteSpace(message) Then message = If(text.Length > 0, TextUtil.Truncate(text, 300), resp.ReasonPhrase)
                Return ErrorResponse(resp.StatusCode, $"{label}: {message}")
            End Using
        End Function

        ''' <summary>An event-stream response filled by a translator running alongside.</summary>
        Private Shared Function StreamResponse(produce As Func(Of ChannelWriter(Of String), Task)) As HttpResponseMessage
            Dim ch = Channel.CreateBounded(Of String)(New BoundedChannelOptions(256) With {.SingleReader = True, .SingleWriter = True})
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Await produce(ch.Writer).ConfigureAwait(False)
                                      Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException
                                          ch.Writer.TryWrite("data: " & New JsonObject From {{"error", New JsonObject From {{"message", "The provider's stream broke: " & ex.Message}}}}.ToJsonString() & vbLf & vbLf)
                                      Finally
                                          ch.Writer.TryComplete()
                                      End Try
                                  End Function)
            Dim content As New StreamContent(New ChannelReadStream(ch.Reader))
            content.Headers.ContentType = New MediaTypeHeaderValue("text/event-stream")
            Return New HttpResponseMessage(HttpStatusCode.OK) With {.Content = content}
        End Function

        Protected Overrides Sub Dispose(disposing As Boolean)
            If disposing Then _http.Dispose()
            MyBase.Dispose(disposing)
        End Sub
    End Class

    ''' <summary>A read-only stream over text pieces written to a channel (UTF-8).</summary>
    Friend NotInheritable Class ChannelReadStream
        Inherits Stream

        Private ReadOnly _reader As ChannelReader(Of String)
        Private _pending As Byte() = Array.Empty(Of Byte)()
        Private _offset As Integer

        Public Sub New(reader As ChannelReader(Of String))
            _reader = reader
        End Sub

        Public Overrides Async Function ReadAsync(buffer As Byte(), offset As Integer, count As Integer, ct As CancellationToken) As Task(Of Integer)
            While _offset >= _pending.Length
                If Not Await _reader.WaitToReadAsync(ct).ConfigureAwait(False) Then Return 0
                Dim s As String = Nothing
                If _reader.TryRead(s) Then
                    _pending = Encoding.UTF8.GetBytes(s)
                    _offset = 0
                End If
            End While
            Dim n = Math.Min(count, _pending.Length - _offset)
            System.Buffer.BlockCopy(_pending, _offset, buffer, offset, n)
            _offset += n
            Return n
        End Function

        Public Overrides Function Read(buffer As Byte(), offset As Integer, count As Integer) As Integer
            Return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult()
        End Function

        Public Overrides ReadOnly Property CanRead As Boolean = True
        Public Overrides ReadOnly Property CanSeek As Boolean = False
        Public Overrides ReadOnly Property CanWrite As Boolean = False
        Public Overrides ReadOnly Property Length As Long
            Get
                Throw New NotSupportedException()
            End Get
        End Property
        Public Overrides Property Position As Long
            Get
                Throw New NotSupportedException()
            End Get
            Set(value As Long)
                Throw New NotSupportedException()
            End Set
        End Property
        Public Overrides Sub Flush()
        End Sub
        Public Overrides Function Seek(offset As Long, origin As SeekOrigin) As Long
            Throw New NotSupportedException()
        End Function
        Public Overrides Sub SetLength(value As Long)
            Throw New NotSupportedException()
        End Sub
        Public Overrides Sub Write(buffer As Byte(), offset As Integer, count As Integer)
            Throw New NotSupportedException()
        End Sub
    End Class

    ''' <summary>AWS Signature Version 4 for one request (Amazon Bedrock with IAM keys).</summary>
    Public NotInheritable Class SigV4
        Private Sub New()
        End Sub

        Public Shared Sub Sign(req As HttpRequestMessage, payload As String, region As String, service As String, accessKey As String, secretKey As String, sessionToken As String, now As DateTime)
            If String.IsNullOrEmpty(accessKey) OrElse String.IsNullOrEmpty(secretKey) Then Throw New HttpRequestException("Amazon Bedrock needs an API key or an access key id and secret access key.")
            Dim amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", Globalization.CultureInfo.InvariantCulture)
            Dim day = now.ToString("yyyyMMdd", Globalization.CultureInfo.InvariantCulture)
            Dim uri = req.RequestUri
            Dim payloadHash = Hex(SHA256.HashData(Encoding.UTF8.GetBytes(If(payload, ""))))
            req.Headers.TryAddWithoutValidation("x-amz-date", amzDate)
            req.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash)
            If Not String.IsNullOrEmpty(sessionToken) Then req.Headers.TryAddWithoutValidation("x-amz-security-token", sessionToken)
            Dim host = If(uri.IsDefaultPort, uri.Host, uri.Host & ":" & uri.Port)
            ' Every path segment is encoded once more for the signature (all services but S3).
            Dim canonicalUri = String.Join("/", uri.AbsolutePath.Split("/"c).Select(Function(seg) Uri.EscapeDataString(seg)))
            Dim headers As New SortedDictionary(Of String, String)(StringComparer.Ordinal) From {
                {"host", host}, {"x-amz-content-sha256", payloadHash}, {"x-amz-date", amzDate}}
            If Not String.IsNullOrEmpty(sessionToken) Then headers("x-amz-security-token") = sessionToken
            Dim canonicalHeaders = String.Concat(headers.Select(Function(kv) kv.Key & ":" & kv.Value.Trim() & vbLf))
            Dim signedHeaders = String.Join(";", headers.Keys)
            Dim query = If(uri.Query.Length > 1, uri.Query.Substring(1), "")
            Dim canonical = String.Join(vbLf, req.Method.Method, canonicalUri, query, canonicalHeaders, signedHeaders, payloadHash)
            Dim scope = $"{day}/{region}/{service}/aws4_request"
            Dim toSign = String.Join(vbLf, "AWS4-HMAC-SHA256", amzDate, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))))
            Dim kDate = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" & secretKey), Encoding.UTF8.GetBytes(day))
            Dim kRegion = HMACSHA256.HashData(kDate, Encoding.UTF8.GetBytes(region))
            Dim kService = HMACSHA256.HashData(kRegion, Encoding.UTF8.GetBytes(service))
            Dim kSigning = HMACSHA256.HashData(kService, Encoding.UTF8.GetBytes("aws4_request"))
            Dim signature = Hex(HMACSHA256.HashData(kSigning, Encoding.UTF8.GetBytes(toSign)))
            req.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={accessKey}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}")
        End Sub

        Private Shared Function Hex(bytes As Byte()) As String
            Return Convert.ToHexString(bytes).ToLowerInvariant()
        End Function
    End Class

End Namespace
