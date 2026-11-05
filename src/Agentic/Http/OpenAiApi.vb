Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports Agentic.AI
Imports Agentic.Economy
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Http

    ' OpenAI-compatible API (/v1/…, also /api/v1/…): every model at the provider (OpenWebUI) plus the administrator's custom
    ' models. Requests are forwarded with every OpenAI parameter intact (tools, response_format, streaming …); a custom model
    ' first picks the model that answers. Authentication is the caller's dashboard account (API key or session).
    Partial Friend NotInheritable Class AdminApi

        Private Const MaxModelRequestBytes As Long = 32L * 1024 * 1024

        Private Sub RegisterOpenAi(r As Router)
            For Each prefix In {"/v1", "/api/v1"}
                r.Map("GET", prefix & "/models", AuthLevel.Read, AddressOf OaiListModels)
                r.Map("GET", prefix & "/models/{*model}", AuthLevel.Read, AddressOf OaiGetModel)
                r.Map("POST", prefix & "/chat/completions", AuthLevel.SubmitTask, Function(rc) OaiChat(rc, False))
                r.Map("POST", prefix & "/completions", AuthLevel.SubmitTask, Function(rc) OaiChat(rc, True))
                r.Map("POST", prefix & "/embeddings", AuthLevel.SubmitTask, AddressOf OaiEmbeddings)
                r.Map("POST", prefix & "/images/generations", AuthLevel.SubmitTask, AddressOf OaiImages)
            Next
            ' Feeds the "Use Agentic from Claude Code" card: the model-picker list, built live so it tracks models coming and going.
            r.Map("GET", "/api/claude-code/models", AuthLevel.Read, AddressOf ClaudeCodeModels)
        End Sub

        ''' <summary>
        ''' {models:[{model,label}]} for the Claude Code setup card, built from the catalog the caller can use, so the
        ''' generated ~/.claude/settings.json modelPicker tracks the models as they are added and removed.
        ''' </summary>
        Private Async Function ClaudeCodeModels(rc As RequestContext) As Task
            Dim models = Await AllModelsAsync(rc).ConfigureAwait(False)
            Dim opts = models.Select(Function(m) CType(New JsonObject From {
                {"model", m("id")?.DeepClone()}, {"label", If(m("name")?.DeepClone(), m("id")?.DeepClone())}}, JsonNode)).ToArray()
            Await rc.WriteBytesAsync(200, "application/json", Encoding.UTF8.GetBytes(New JsonObject From {{"models", New JsonArray(opts)}}.ToJsonString())).ConfigureAwait(False)
        End Function

        Private Shared Function OaiError(status As Integer, message As String, Optional type As String = "invalid_request_error", Optional code As String = Nothing) As ApiException
            Return New ApiException(status, If(code, type), message)
        End Function

        Private Async Function AllModelsAsync(rc As RequestContext) As Task(Of List(Of JsonObject))
            Dim list As New List(Of JsonObject)
            ' The account's own models (My models).
            For Each b In If(_h.Byo?.ListFor(rc.User.Name), New List(Of Global.Agentic.AI.ByoModel)).Where(Function(x) x.Enabled)
                list.Add(New JsonObject From {
                    {"id", b.Id}, {"object", "model"}, {"created", New DateTimeOffset(b.CreatedUtc).ToUnixTimeSeconds()}, {"owned_by", "you"},
                    {"name", b.Name}, {"provider", Global.Agentic.AI.ByoService.ProviderLabel(b.Provider)}, {"byo", True}})
            Next
            For Each c In (Await _h.ModelHub.ListCustomAsync().ConfigureAwait(False)).Where(Function(x) x.Enabled AndAlso Not _h.ModelHub.IsDisabled(x.Id) AndAlso rc.User.CanUseModel(x.Id))
                list.Add(New JsonObject From {
                    {"id", c.Id}, {"object", "model"}, {"created", New DateTimeOffset(c.CreatedUtc).ToUnixTimeSeconds()}, {"owned_by", "agentic"},
                    {"name", c.Name}, {"description", c.Description}, {"custom", True},
                    {"routes", New JsonArray(c.Definition.Routes.Select(Function(rt) CType(New JsonObject From {{"category", rt.Category}, {"model", rt.Model}}, JsonNode)).ToArray())},
                    {"default_model", c.Definition.DefaultModel}})
            Next
            Try
                For Each m In (Await _h.ModelHub.ListUpstreamAsync(rc.CancellationToken).ConfigureAwait(False)).Where(Function(x) rc.User.CanUseModel(x.Id) AndAlso Not Global.Agentic.AI.ImageRouter.IsImageModel(x.Id))
                    list.Add(New JsonObject From {{"id", m.Id}, {"object", "model"}, {"created", m.Created}, {"owned_by", m.OwnedBy}, {"name", m.Name}})
                Next
            Catch ex As HttpRequestException
                _h.Log.Warn("Api", "Provider model list failed: " & ex.Message)
                Throw OaiError(502, ModelErrors.ForUser(ex.Message), "api_error")
            End Try
            Return list
        End Function

        Private Async Function OaiListModels(rc As RequestContext) As Task
            Dim list = Await AllModelsAsync(rc).ConfigureAwait(False)
            Await rc.OkAsync(New JsonObject From {{"object", "list"}, {"data", New JsonArray(list.Select(Function(x) CType(x, JsonNode)).ToArray())}}).ConfigureAwait(False)
        End Function

        Private Async Function OaiGetModel(rc As RequestContext) As Task
            Dim id = rc.Route("model")
            Dim m = (Await AllModelsAsync(rc).ConfigureAwait(False)).FirstOrDefault(Function(x) String.Equals(x("id")?.ToString(), id, StringComparison.OrdinalIgnoreCase))
            If m Is Nothing Then Throw OaiError(404, $"The model '{id}' does not exist.", "invalid_request_error", "model_not_found")
            Await rc.OkAsync(m).ConfigureAwait(False)
        End Function

        ''' <summary>403 for a model the account may not use; 402 when its balance is empty and the model has a price.</summary>
        Private Async Function RequireModelUseAsync(rc As RequestContext, model As String) As Task
            ' The account's limits (Users & access): calls per minute, tokens per day.
            Dim limit = Await LimitProblemAsync(rc).ConfigureAwait(False)
            If limit IsNot Nothing Then Throw OaiError(429, limit, "rate_limit_exceeded", "rate_limited")
            ' A user's own model (My models): only its owner, and it is free.
            If Global.Agentic.AI.ByoService.IsByoId(model) Then
                If _h.Byo Is Nothing OrElse Not _h.Byo.CanUse(rc.User.Name, model) Then Throw OaiError(403, $"'{model}' is not one of your models.", "invalid_request_error", "model_not_allowed")
                Return
            End If
            If Not rc.User.CanUseModel(model) Then Throw OaiError(403, $"You do not have access to the model '{model}'.", "invalid_request_error", "model_not_allowed")
            Dim problem = Await ModelUseProblemAsync(rc, model).ConfigureAwait(False)
            If problem IsNot Nothing Then Throw OaiError(402, problem, "insufficient_quota", "insufficient_balance")
        End Function

        ''' <summary>Charges the account for an API call (Model Square prices; administrators and API tokens are not charged).</summary>
        Private Function ChargeApiAsync(rc As RequestContext, endpoint As String, requested As String, usedModel As String, promptTokens As Long, completionTokens As Long,
                                        Optional cachedTokens As Long = 0) As Task(Of Long)
            Dim exempt = rc.User.IsAdmin OrElse Not (rc.User.ViaSession OrElse rc.User.ViaApiKey)
            Return _h.Accounts.ChargeUsageAsync(rc.User.Name, exempt, AccountService.EntryApi, requested, usedModel, promptTokens, completionTokens, "API: " & endpoint, cachedTokens)
        End Function

        Private Async Function ReadModelRequestAsync(rc As RequestContext) As Task(Of JsonNode)
            Dim bytes = Await rc.ReadBytesAsync(MaxModelRequestBytes).ConfigureAwait(False)
            Dim node As JsonNode = Nothing
            Try
                node = JsonNode.Parse(bytes)
            Catch ex As JsonException
            End Try
            If node Is Nothing OrElse TypeOf node IsNot JsonObject Then Throw OaiError(400, "The request body must be a JSON object.")
            If String.IsNullOrWhiteSpace(node("model")?.ToString()) Then Throw OaiError(400, "'model' is required.")
            Return node
        End Function

        ''' <summary>/v1/completions (legacy text) is served by the chat endpoint: prompt in, text out.</summary>
        Private Shared Function CompletionToChat(req As JsonNode) As JsonNode
            Dim chat = req.DeepClone()
            Dim prompt = req("prompt")
            Dim text = If(prompt Is Nothing, "", If(TypeOf prompt Is JsonArray, String.Join(vbLf, DirectCast(prompt, JsonArray).Select(Function(p) p?.ToString())), prompt.ToString()))
            DirectCast(chat, JsonObject).Remove("prompt")
            DirectCast(chat, JsonObject).Remove("echo")
            DirectCast(chat, JsonObject).Remove("suffix")
            DirectCast(chat, JsonObject).Remove("best_of")
            chat("messages") = New JsonArray(New JsonObject From {{"role", "user"}, {"content", text}})
            Return chat
        End Function

        Private Async Function OaiChat(rc As RequestContext, legacy As Boolean) As Task
            Dim started = Diagnostics.Stopwatch.StartNew()
            Dim req = Await ReadModelRequestAsync(rc).ConfigureAwait(False)
            Dim requested = req("model").ToString()
            Dim stream = req("stream") IsNot Nothing AndAlso req("stream").GetValueKind() = JsonValueKind.True
            Dim body = If(legacy, CompletionToChat(req), req)
            Dim endpoint = If(legacy, "completions", "chat/completions")
            Dim custom = Await _h.ModelHub.GetCustomAsync(requested).ConfigureAwait(False)
            If _h.ModelHub.IsDisabled(requested) Then Throw OaiError(404, "The model '" & requested & "' is turned off by an administrator.", "invalid_request_error", "model_not_found")
            If custom Is Nothing AndAlso _h.ModelHub.IsHidden(requested) Then Throw OaiError(404, $"The model '{requested}' does not exist.", "invalid_request_error", "model_not_found")
            If custom IsNot Nothing AndAlso Not custom.Enabled Then Throw OaiError(404, $"The custom model '{requested}' is disabled.", "invalid_request_error", "model_not_found")
            If custom Is Nothing AndAlso Global.Agentic.AI.ImageRouter.IsImageModel(requested) Then
                Throw OaiError(400, $"'{requested}' is an image model: pictures are made with POST /v1/images/generations.", "invalid_request_error", "model_not_supported")
            End If
            Await RequireModelUseAsync(rc, requested).ConfigureAwait(False)
            Using cancelWork = Threading.CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken)
            ' Routing, a free local slot and the provider's first byte can take minutes; past the grace period the response
            ' starts and is kept alive so Cloudflare does not cut it off (HTTP 524). See ModelKeepAlive.vb.
            Dim work = OpenUpstreamAsync(custom, requested, body, Not stream, cancelWork.Token)
            Dim committed = False
            Dim u As UpstreamCall = Nothing
            Dim failure As Exception = Nothing
            Try
                If Await FinishesWithinAsync(work, KeepAliveAfter, rc.CancellationToken).ConfigureAwait(False) Then
                    u = Await work.ConfigureAwait(False)
                Else
                    rc.BeginStream(If(stream, "text/event-stream; charset=utf-8", "application/json; charset=utf-8"))
                    committed = True
                    u = Await KeepAliveUntilDoneAsync(rc, work, If(stream, SseKeepAlive, JsonKeepAlive), cancelWork).ConfigureAwait(False)
                End If
            Catch ex As Exception When committed AndAlso Not ClientGone(ex)
                failure = ex
            End Try
            If failure IsNot Nothing Then
                Await WriteOaiLateErrorAsync(rc, stream, StatusOfFailure(failure), MessageOfFailure(failure)).ConfigureAwait(False)
                Return
            End If
            Using u
                Dim resp = u.Response
                Dim decision = u.Decision
                If Not committed Then
                    Dim headers = rc.Http.Response.Headers
                    headers("X-Agentic-Model") = u.Model
                    If decision IsNot Nothing Then
                        headers("X-Agentic-Route") = decision.Category
                        headers("X-Agentic-Route-Reason") = Uri.EscapeDataString(TextUtil.Truncate(decision.Reason, 200))
                    End If
                End If
                Dim usage = (Prompt:=0L, Completion:=0L, Cached:=0L)
                If Not resp.IsSuccessStatusCode Then
                    Dim errText = If(u.Text, Await resp.Content.ReadAsStringAsync(rc.CancellationToken).ConfigureAwait(False))
                    Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", endpoint, requested, u.Model, decision?.Category, stream, CInt(resp.StatusCode), 0, 0, started.ElapsedMilliseconds, errText).ConfigureAwait(False)
                    ' The caller gets the kind of failure in a few words, not the provider's own error (it stays in the usage log).
                    Dim shortError = ModelErrors.ForUser(errText, CInt(resp.StatusCode))
                    If committed Then
                        Await WriteOaiLateErrorAsync(rc, stream, CInt(resp.StatusCode), shortError).ConfigureAwait(False)
                    Else
                        Await rc.WriteBytesAsync(CInt(resp.StatusCode), "application/json", Encoding.UTF8.GetBytes(OaiErrorJson(CInt(resp.StatusCode), shortError))).ConfigureAwait(False)
                    End If
                    Return
                End If
                Dim mediaType = If(resp.Content.Headers.ContentType?.MediaType, "")
                Dim renameTo = If(custom IsNot Nothing, custom.Id, Nothing)
                Dim streamedChars As Long = 0
                Dim firstOutputMs As Long = -1
                Dim text As String = Nothing
                ' A stream that has started must stay one, even if the provider answered in one piece.
                Dim replayAsChunk = stream AndAlso committed AndAlso Not mediaType.Contains("event-stream")
                If stream AndAlso mediaType.Contains("event-stream") Then
                    If Not committed Then
                        rc.BeginStream("text/event-stream; charset=utf-8")
                        committed = True
                    End If
                    Using s = Await resp.Content.ReadAsStreamAsync(rc.CancellationToken).ConfigureAwait(False), reader As New StreamReader(s, Encoding.UTF8)
                        While True
                            Dim line = Await ReadLineKeepingAliveAsync(rc, reader, SseKeepAlive).ConfigureAwait(False)
                            If line Is Nothing Then Exit While
                            If line.StartsWith("data:", StringComparison.Ordinal) AndAlso Not line.Contains("[DONE]") Then
                                Dim chunk = TryParse(line.Substring(5).Trim())
                                If chunk IsNot Nothing Then
                                    If firstOutputMs < 0 AndAlso Global.Agentic.AI.ModelHealth.ChunkHasOutput(chunk) Then firstOutputMs = u.Sent.ElapsedMilliseconds
                                    ReadUsage(chunk, usage)
                                    streamedChars += If(JsonUtil.FirstChoice(chunk)?("delta")?("content")?.ToString()?.Length, 0)
                                    If renameTo IsNot Nothing Then chunk("model") = renameTo
                                    If legacy Then chunk = ChatChunkToCompletion(chunk)
                                    line = "data: " & chunk.ToJsonString()
                                End If
                            End If
                            Await rc.WriteStreamAsync(line & vbLf).ConfigureAwait(False)
                        End While
                    End Using
                Else
                    text = If(u.Text, Await resp.Content.ReadAsStringAsync(rc.CancellationToken).ConfigureAwait(False))
                    Dim json = TryParse(text)
                    If json IsNot Nothing Then
                        ReadUsage(json, usage)
                        If renameTo IsNot Nothing Then json("model") = renameTo
                        If replayAsChunk Then
                            Dim chunk As JsonNode = AsStreamChunk(json)
                            If legacy Then chunk = ChatChunkToCompletion(chunk)
                            text = "data: " & chunk.ToJsonString() & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf
                        Else
                            If legacy Then json = ChatToCompletion(json)
                            text = json.ToJsonString()
                        End If
                    End If
                End If
                ' Streams without usage (the client did not ask for it): about four characters per token.
                If usage.Prompt = 0 Then usage.Prompt = Math.Max(1L, CLng(body.ToJsonString().Length \ 4))
                If usage.Completion = 0 AndAlso streamedChars > 0 Then usage.Completion = Math.Max(1L, streamedChars \ 4)
                ' Recorded and charged before the answer goes out, so the account's balance is right once the caller has it.
                Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", endpoint, requested, u.Model, decision?.Category, stream, 200, usage.Prompt, usage.Completion, started.ElapsedMilliseconds, Nothing).ConfigureAwait(False)
                RecordApiSpeed(u, If(custom IsNot Nothing, requested, Nothing), firstOutputMs, usage.Completion)
                Await ChargeApiAsync(rc, endpoint, requested, u.Model, usage.Prompt, usage.Completion, usage.Cached).ConfigureAwait(False)
                If Not (stream AndAlso mediaType.Contains("event-stream")) Then
                    If committed Then
                        Await rc.WriteStreamAsync(text).ConfigureAwait(False)
                    Else
                        Await rc.WriteBytesAsync(200, "application/json", Encoding.UTF8.GetBytes(text)).ConfigureAwait(False)
                    End If
                End If
            End Using
            End Using
        End Function

        Private Shared Function TryParse(text As String) As JsonNode
            Try
                Return JsonNode.Parse(text)
            Catch ex As JsonException
                Return Nothing
            End Try
        End Function

        Private Shared Sub ReadUsage(json As JsonNode, ByRef usage As (Prompt As Long, Completion As Long, Cached As Long))
            Dim u = json?("usage")
            If u Is Nothing Then Return
            Dim p As Long, c As Long
            If Long.TryParse(u("prompt_tokens")?.ToString(), p) Then usage.Prompt = p : usage.Cached = Math.Min(p, AccountService.CachedTokensOf(u))
            If Long.TryParse(u("completion_tokens")?.ToString(), c) Then usage.Completion = c
        End Sub

        Private Shared Function ChatToCompletion(json As JsonNode) As JsonNode
            Dim choices = TryCast(json("choices"), JsonArray)
            Dim out As New JsonArray()
            If choices IsNot Nothing Then
                For Each ch In choices
                    out.Add(New JsonObject From {{"text", If(ch?("message")?("content")?.ToString(), "")}, {"index", ch?("index")?.DeepClone()}, {"logprobs", Nothing},
                                                 {"finish_reason", ch?("finish_reason")?.DeepClone()}})
                Next
            End If
            json("object") = "text_completion"
            json("choices") = out
            Return json
        End Function

        Private Shared Function ChatChunkToCompletion(json As JsonNode) As JsonNode
            Dim choices = TryCast(json("choices"), JsonArray)
            Dim out As New JsonArray()
            If choices IsNot Nothing Then
                For Each ch In choices
                    out.Add(New JsonObject From {{"text", If(ch?("delta")?("content")?.ToString(), "")}, {"index", ch?("index")?.DeepClone()}, {"logprobs", Nothing},
                                                 {"finish_reason", ch?("finish_reason")?.DeepClone()}})
                Next
            End If
            json("object") = "text_completion"
            json("choices") = out
            Return json
        End Function

        Private Async Function OaiEmbeddings(rc As RequestContext) As Task
            Dim started = Diagnostics.Stopwatch.StartNew()
            Dim req = Await ReadModelRequestAsync(rc).ConfigureAwait(False)
            Dim requested = req("model").ToString()
            Await RequireModelUseAsync(rc, requested).ConfigureAwait(False)
            Dim custom = Await _h.ModelHub.GetCustomAsync(requested).ConfigureAwait(False)
            If custom IsNot Nothing Then
                If String.IsNullOrWhiteSpace(custom.Definition.EmbeddingModel) Then Throw OaiError(400, $"The custom model '{requested}' has no embedding model.")
                req("model") = custom.Definition.EmbeddingModel
            End If
            Await ProxyJsonAsync(rc, "/embeddings", req, If(custom?.Id, Nothing), "embeddings", requested, started).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Images come only from the images API in ImageGeneration (never the chat provider). "model" picks one of its models
        ''' (the others are asked when it cannot make the picture); the picture is returned as b64_json. User accounts pay the
        ''' per-image price.
        ''' </summary>
        Private Async Function OaiImages(rc As RequestContext) As Task
            If rc.User.IsRestrictedUser AndAlso Not rc.User.HasPermission("chat.images") Then Throw OaiError(403, "This account may not create images.", "invalid_request_error", "permission_denied")
            Dim limit = Await LimitProblemAsync(rc).ConfigureAwait(False)
            If limit IsNot Nothing Then Throw OaiError(429, limit, "rate_limit_exceeded", "rate_limited")
            Dim started = Diagnostics.Stopwatch.StartNew()
            Dim bytes = Await rc.ReadBytesAsync(MaxModelRequestBytes).ConfigureAwait(False)
            Dim req = TryParse(Encoding.UTF8.GetString(bytes))
            Dim prompt = req?("prompt")?.ToString()
            If String.IsNullOrWhiteSpace(prompt) Then Throw OaiError(400, "'prompt' is required.")
            Dim router = _h.ImageRouter
            If router Is Nothing OrElse Not router.Enabled Then Throw OaiError(503, "Image generation is not set up on this server.")
            Dim requested = req("model")?.ToString()
            Dim known = Await router.RoutesAsync(rc.CancellationToken).ConfigureAwait(False)
            Dim preferred = known.FirstOrDefault(Function(r) String.Equals(r, If(requested, "").Trim(), StringComparison.OrdinalIgnoreCase))
            Dim n = 1
            Integer.TryParse(req("n")?.ToString(), n)
            n = Math.Clamp(n, 1, 4)
            Dim price = Global.Agentic.Economy.PricingService.ImageCostMicros(_h.Pricing.GetPrice(Global.Agentic.Economy.PricingService.ImageGenerationKey), 1)
            If price > 0 AndAlso Not rc.User.IsAdmin Then
                Dim acc = Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False)
                If acc IsNot Nothing AndAlso acc.Charged AndAlso acc.BalanceMicros < price * n Then Throw OaiError(402, $"The balance ({TextUtil.FormatMicros(acc.BalanceMicros)}) does not cover {n} image(s).")
            End If
            Dim data As New JsonArray()
            Dim usedModel As String = Nothing
            Dim failure As AiException = Nothing
            Try
                For i = 1 To n
                    Dim made = Await router.GenerateAsync(prompt, req("size")?.ToString(), rc.CancellationToken, preferredModel:=preferred).ConfigureAwait(False)
                    usedModel = made.Model
                    data.Add(New JsonObject From {{"b64_json", Convert.ToBase64String(made.Result.Data)}, {"revised_prompt", made.Result.RevisedPrompt}})
                    If price > 0 AndAlso Not rc.User.IsAdmin Then
                        Await _h.Accounts.ChargeAsync(rc.User.Name, price, "Image", made.Model, Nothing, Nothing, Nothing, "API image: " & TextUtil.Truncate(prompt, 80, "…")).ConfigureAwait(False)
                    End If
                Next
            Catch ex As AiException
                failure = ex
            End Try
            If failure IsNot Nothing AndAlso data.Count = 0 Then
                If failure.Kind = AiErrorKind.InvalidRequest Then Throw OaiError(400, ModelErrors.ForUser(failure))
                Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", "images/generations", If(requested, "image"), Nothing, Nothing, False, 503, 0, 0, started.ElapsedMilliseconds, failure.Message).ConfigureAwait(False)
                Throw OaiError(503, ModelErrors.ForUser(failure))
            End If
            Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", "images/generations", If(requested, "image"), usedModel, Nothing, False, 200, 0, 0, started.ElapsedMilliseconds, Nothing).ConfigureAwait(False)
            Await rc.WriteJsonAsync(200, New JsonObject From {{"created", DateTimeOffset.UtcNow.ToUnixTimeSeconds()}, {"model", usedModel}, {"data", data}}).ConfigureAwait(False)
        End Function

        Private Async Function ProxyJsonAsync(rc As RequestContext, path As String, req As JsonNode, renameTo As String, endpoint As String, requested As String, started As Diagnostics.Stopwatch) As Task
            Using resp = Await _h.ModelHub.SendAsync(path, req.ToJsonString(), rc.CancellationToken).ConfigureAwait(False)
                Dim text = Await resp.Content.ReadAsStringAsync(rc.CancellationToken).ConfigureAwait(False)
                Dim json = TryParse(text)
                Dim usage = (Prompt:=0L, Completion:=0L, Cached:=0L)
                If json IsNot Nothing Then
                    ReadUsage(json, usage)
                    If renameTo IsNot Nothing AndAlso resp.IsSuccessStatusCode Then json("model") = renameTo
                    text = json.ToJsonString()
                End If
                Dim logged = text
                If Not resp.IsSuccessStatusCode Then text = OaiErrorJson(CInt(resp.StatusCode), ModelErrors.ForUser(text, CInt(resp.StatusCode)))
                Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", endpoint, requested, req("model")?.ToString(), Nothing, False, CInt(resp.StatusCode), usage.Prompt, usage.Completion,
                                                started.ElapsedMilliseconds, If(resp.IsSuccessStatusCode, Nothing, logged)).ConfigureAwait(False)
                If resp.IsSuccessStatusCode AndAlso endpoint = "embeddings" Then Await ChargeApiAsync(rc, endpoint, requested, req("model")?.ToString(), usage.Prompt, usage.Completion).ConfigureAwait(False)
                Await rc.WriteBytesAsync(CInt(resp.StatusCode), "application/json", Encoding.UTF8.GetBytes(text)).ConfigureAwait(False)
            End Using
        End Function
    End Class

End Namespace
