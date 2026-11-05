Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace AI

    ''' <summary>Abstraction over an OpenAI-compatible endpoint (OpenWebUI, LiteLLM, vLLM, ...).</summary>
    Public Interface IAIProvider
        ReadOnly Property Name As String
        Function ChatAsync(request As ChatRequest, ct As CancellationToken) As Task(Of ChatResponse)
        Function ListModelsAsync(ct As CancellationToken) As Task(Of List(Of String))
        Function GenerateImageAsync(prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult)
        ''' <summary>
        ''' An image from one particular model: through the image engine with that model (viaEngine), or by asking an image
        ''' model through chat completions (relays answer with the picture as a URL, data URL or image part).
        ''' </summary>
        Function GenerateImageWithModelAsync(model As String, viaEngine As Boolean, prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult)
        ''' <summary>A picture changed (or built on) by one particular model: the images edits endpoint with that picture.</summary>
        Function EditImageWithModelAsync(model As String, prompt As String, size As String, image As Byte(), imageMime As String, imageName As String,
                                         timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult)
        Function DownloadAsync(url As String, maxBytes As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String))
    End Interface

    ''' <summary>
    ''' OpenAI-compatible chat client. Streams by default (keeps Cloudflare/proxies from timing out long reasoning
    ''' responses), aggregates streamed tool-call deltas, tolerates reasoning_content / null content, and maps HTTP
    ''' and LiteLLM/OpenWebUI error bodies to <see cref="AiErrorKind"/>.
    ''' </summary>
    Public NotInheritable Class OpenAICompatibleProvider
        Implements IAIProvider, IDisposable

        Private ReadOnly _http As HttpClient
        Private ReadOnly _opt As AIProviderOptions
        Private ReadOnly _baseUrl As String

        Public ReadOnly Property Name As String Implements IAIProvider.Name

        ''' <param name="transport">Another transport (users' own models: ByoHandler); Nothing = HTTP to BaseUrl.</param>
        Public Sub New(name As String, options As AIProviderOptions, Optional transport As HttpMessageHandler = Nothing)
            Me.Name = name
            _opt = options
            _baseUrl = options.BaseUrl.TrimEnd("/"c)
            If transport IsNot Nothing Then
                _http = New HttpClient(transport, disposeHandler:=False) With {.Timeout = Timeout.InfiniteTimeSpan}
            Else
                Dim handler As New SocketsHttpHandler With {
                    .PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                    .PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    .ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, options.ConnectTimeoutSeconds)),
                    .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate,
                    .MaxConnectionsPerServer = 64
                }
                _http = New HttpClient(handler) With {.Timeout = Timeout.InfiniteTimeSpan}
            End If
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic/1.0")
            If Not String.IsNullOrEmpty(options.ApiKey) Then
                _http.DefaultRequestHeaders.Authorization = New AuthenticationHeaderValue("Bearer", options.ApiKey)
            End If
        End Sub

        Private Function BuildUrl(pathPart As String) As String
            Return _baseUrl & "/" & pathPart.TrimStart("/"c)
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Chat
        ' ------------------------------------------------------------------------------------------

        Public Async Function ChatAsync(request As ChatRequest, ct As CancellationToken) As Task(Of ChatResponse) Implements IAIProvider.ChatAsync
            Dim body = BuildBody(request)
            Dim sw = Stopwatch.StartNew()
            Using timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeoutCts.CancelAfter(request.Timeout)
                Dim response As HttpResponseMessage = Nothing
                Try
                    Using msg As New HttpRequestMessage(HttpMethod.Post, BuildUrl(_opt.ChatPath))
                        msg.Content = New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                        If request.Stream Then msg.Headers.Accept.Add(New MediaTypeWithQualityHeaderValue("text/event-stream"))
                        response = Await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(False)
                    End Using
                    If Not response.IsSuccessStatusCode Then
                        Dim errBody = Await SafeReadAsync(response, timeoutCts.Token).ConfigureAwait(False)
                        Throw AiErrorClassifier.FromHttp(CInt(response.StatusCode), errBody)
                    End If
                    Dim mediaType = response.Content.Headers.ContentType?.MediaType
                    Dim result As ChatResponse
                    If request.Stream AndAlso String.Equals(mediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase) Then
                        result = Await ReadStreamAsync(response, timeoutCts.Token, sw).ConfigureAwait(False)
                    Else
                        Dim text = Await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(False)
                        result = ParseCompletion(text)
                    End If
                    result.DurationMs = sw.ElapsedMilliseconds
                    Return result
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New AiException(AiErrorKind.Timeout, $"AI request timed out after {request.Timeout.TotalSeconds:0}s.", 0, ex)
                Catch ex As HttpRequestException
                    Throw New AiException(AiErrorKind.Transient, "AI provider connection failed: " & ex.Message, 0, ex)
                Catch ex As IOException
                    Throw New AiException(AiErrorKind.Transient, "AI provider stream failed: " & ex.Message, 0, ex)
                Finally
                    response?.Dispose()
                End Try
            End Using
        End Function

        Private Shared Async Function SafeReadAsync(response As HttpResponseMessage, ct As CancellationToken) As Task(Of String)
            Try
                Dim s = Await response.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Return TextUtil.Truncate(s, 4000)
            Catch
                Return String.Empty
            End Try
        End Function

        Friend Shared Function BuildBody(request As ChatRequest) As JsonObject
            Dim body As New JsonObject()
            body("model") = JsonValue.Create(request.ModelId)
            Dim msgs As New JsonArray()
            For Each m In request.Messages
                msgs.Add(ToJsonMessage(m))
            Next
            body("messages") = msgs
            body("max_tokens") = JsonValue.Create(request.MaxTokens)
            If request.Temperature.HasValue Then body("temperature") = JsonValue.Create(request.Temperature.Value)
            body("stream") = JsonValue.Create(request.Stream)
            If request.Stream Then
                Dim so As New JsonObject()
                so("include_usage") = JsonValue.Create(True)
                body("stream_options") = so
            End If
            If request.Tools IsNot Nothing AndAlso request.Tools.Count > 0 Then
                Dim tools As New JsonArray()
                For Each t In request.Tools
                    Dim fn As New JsonObject()
                    fn("name") = JsonValue.Create(t.Name)
                    fn("description") = JsonValue.Create(t.Description)
                    fn("parameters") = If(t.Parameters Is Nothing, New JsonObject(), t.Parameters.DeepClone())
                    Dim tool As New JsonObject()
                    tool("type") = JsonValue.Create("function")
                    tool("function") = fn
                    tools.Add(tool)
                Next
                body("tools") = tools
                body("tool_choice") = JsonValue.Create("auto")
            End If
            Return body
        End Function

        Private Shared Function ToJsonMessage(m As ChatMessage) As JsonObject
            Dim o As New JsonObject()
            o("role") = JsonValue.Create(m.Role)
            If m.ImageUrls IsNot Nothing AndAlso m.ImageUrls.Count > 0 Then
                Dim parts As New JsonArray()
                If Not String.IsNullOrEmpty(m.Content) Then
                    Dim tp As New JsonObject()
                    tp("type") = JsonValue.Create("text")
                    tp("text") = JsonValue.Create(m.Content)
                    parts.Add(tp)
                End If
                For Each u In m.ImageUrls
                    Dim ip As New JsonObject()
                    ip("type") = JsonValue.Create("image_url")
                    Dim iu As New JsonObject()
                    iu("url") = JsonValue.Create(u)
                    ip("image_url") = iu
                    parts.Add(ip)
                Next
                o("content") = parts
            Else
                ' Some upstreams reject null content on assistant tool-call messages; use empty string.
                o("content") = JsonValue.Create(If(m.Content, String.Empty))
            End If
            If m.ToolCalls IsNot Nothing AndAlso m.ToolCalls.Count > 0 Then
                Dim calls As New JsonArray()
                For Each tc In m.ToolCalls
                    Dim fn As New JsonObject()
                    fn("name") = JsonValue.Create(tc.Name)
                    ' A model cut off mid-call leaves arguments that are not JSON; replaying them makes the provider refuse every
                    ' later request of the task ("Unterminated string starting at…"), so retries never recover. Close or wrap them.
                    fn("arguments") = JsonValue.Create(Global.Agentic.Work.ChatService.ValidToolArguments(tc.ArgumentsJson))
                    Dim c As New JsonObject()
                    c("id") = JsonValue.Create(tc.Id)
                    c("type") = JsonValue.Create("function")
                    c("function") = fn
                    calls.Add(c)
                Next
                o("tool_calls") = calls
            End If
            If m.Role = "tool" Then
                o("tool_call_id") = JsonValue.Create(m.ToolCallId)
                If Not String.IsNullOrEmpty(m.Name) Then o("name") = JsonValue.Create(m.Name)
            End If
            Return o
        End Function

        Friend Shared Function ParseCompletion(text As String) As ChatResponse
            Dim result As New ChatResponse()
            Dim doc As JsonDocument
            Try
                doc = JsonDocument.Parse(text)
            Catch ex As JsonException
                Throw New AiException(AiErrorKind.InvalidResponse, "AI provider returned invalid JSON: " & TextUtil.Truncate(text, 200))
            End Try
            Using doc
                Dim root = doc.RootElement
                Dim errEl As JsonElement
                If root.TryGetProperty("error", errEl) OrElse root.TryGetProperty("detail", errEl) Then
                    Dim hasChoices As JsonElement
                    If Not root.TryGetProperty("choices", hasChoices) Then Throw AiErrorClassifier.FromHttp(200, text)
                End If
                Dim choices As JsonElement
                If Not root.TryGetProperty("choices", choices) OrElse choices.ValueKind <> JsonValueKind.Array OrElse choices.GetArrayLength() = 0 Then
                    Throw New AiException(AiErrorKind.InvalidResponse, "AI response contained no choices.")
                End If
                Dim choice = choices(0)
                result.FinishReason = JsonUtil.GetString(choice, "finish_reason")
                Dim message As JsonElement
                If choice.TryGetProperty("message", message) Then
                    result.Content = JsonUtil.GetString(message, "content")
                    result.ReasoningContent = If(JsonUtil.GetString(message, "reasoning_content"), JsonUtil.GetString(message, "reasoning"))
                    Dim tcs As JsonElement
                    If message.TryGetProperty("tool_calls", tcs) AndAlso tcs.ValueKind = JsonValueKind.Array Then
                        For Each tc In tcs.EnumerateArray()
                            Dim fn As JsonElement
                            If tc.TryGetProperty("function", fn) Then
                                result.ToolCalls.Add(New ToolCallRequest With {
                                    .Id = If(JsonUtil.GetString(tc, "id"), Ids.NewId("call")),
                                    .Name = JsonUtil.GetString(fn, "name"),
                                    .ArgumentsJson = If(JsonUtil.GetString(fn, "arguments"), "{}")})
                            End If
                        Next
                    End If
                End If
                result.Model = JsonUtil.GetString(root, "model")
                ParseUsage(root, result.Usage)
            End Using
            Return result
        End Function

        Private Shared Sub ParseUsage(root As JsonElement, usage As ChatUsage)
            Dim u As JsonElement
            If root.TryGetProperty("usage", u) AndAlso u.ValueKind = JsonValueKind.Object Then
                Dim v As Long
                Dim p As JsonElement
                If u.TryGetProperty("prompt_tokens", p) AndAlso p.TryGetInt64(v) Then usage.PromptTokens = v
                If u.TryGetProperty("completion_tokens", p) AndAlso p.TryGetInt64(v) Then usage.CompletionTokens = v
                Dim det As JsonElement
                If u.TryGetProperty("completion_tokens_details", det) AndAlso det.ValueKind = JsonValueKind.Object AndAlso det.TryGetProperty("reasoning_tokens", p) AndAlso p.TryGetInt64(v) Then
                    usage.ReasoningTokens = v
                End If
                ' Cached prompt tokens: OpenAI (prompt_tokens_details), Responses API (input_tokens_details), Anthropic, DeepSeek.
                For Each cachePath In {({"prompt_tokens_details", "cached_tokens"}), ({"input_tokens_details", "cached_tokens"}), ({"cache_read_input_tokens"}), ({"prompt_cache_hit_tokens"})}
                    Dim cur = u
                    Dim found = True
                    For Each part In cachePath
                        If cur.ValueKind <> JsonValueKind.Object OrElse Not cur.TryGetProperty(part, cur) Then found = False : Exit For
                    Next
                    If found AndAlso cur.ValueKind = JsonValueKind.Number AndAlso cur.TryGetInt64(v) AndAlso v > 0 Then usage.CachedTokens = v : Exit For
                Next
            End If
        End Sub

        Private Class StreamToolCall
            Public Id As String
            Public Name As String
            Public Args As New StringBuilder()
        End Class

        ''' <param name="sent">Started when the request was sent: the first output's time is the wait for the model (its speed).</param>
        Private Shared Async Function ReadStreamAsync(response As HttpResponseMessage, ct As CancellationToken, sent As Stopwatch) As Task(Of ChatResponse)
            Dim result As New ChatResponse()
            Dim content As New StringBuilder()
            Dim reasoning As New StringBuilder()
            Dim calls As New SortedDictionary(Of Integer, StreamToolCall)
            Using s = Await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(False)
                Using reader As New StreamReader(s, Encoding.UTF8)
                    While True
                        Dim line = Await reader.ReadLineAsync(ct).ConfigureAwait(False)
                        If line Is Nothing Then Exit While
                        If line.Length = 0 OrElse line.StartsWith(":", StringComparison.Ordinal) Then Continue While
                        If Not line.StartsWith("data:", StringComparison.Ordinal) Then Continue While
                        Dim data = line.Substring(5).Trim()
                        If data = "[DONE]" Then Exit While
                        Dim doc As JsonDocument = Nothing
                        Try
                            doc = JsonDocument.Parse(data)
                        Catch ex As JsonException
                            Continue While
                        End Try
                        Using doc
                            Dim root = doc.RootElement
                            Dim errEl As JsonElement
                            If root.TryGetProperty("error", errEl) Then Throw AiErrorClassifier.FromHttp(200, data)
                            If result.Model Is Nothing Then result.Model = JsonUtil.GetString(root, "model")
                            ParseUsage(root, result.Usage)
                            Dim choices As JsonElement
                            If root.TryGetProperty("choices", choices) AndAlso choices.ValueKind = JsonValueKind.Array AndAlso choices.GetArrayLength() > 0 Then
                                Dim choice = choices(0)
                                Dim fr = JsonUtil.GetString(choice, "finish_reason")
                                If Not String.IsNullOrEmpty(fr) Then result.FinishReason = fr
                                Dim delta As JsonElement
                                If choice.TryGetProperty("delta", delta) AndAlso delta.ValueKind = JsonValueKind.Object Then
                                    Dim c = JsonUtil.GetString(delta, "content")
                                    If c IsNot Nothing Then content.Append(c)
                                    Dim rc = If(JsonUtil.GetString(delta, "reasoning_content"), JsonUtil.GetString(delta, "reasoning"))
                                    If rc IsNot Nothing AndAlso reasoning.Length < 200000 Then reasoning.Append(rc)
                                    Dim tcs As JsonElement
                                    If result.FirstTokenMs Is Nothing AndAlso sent IsNot Nothing AndAlso (Not String.IsNullOrEmpty(c) OrElse Not String.IsNullOrEmpty(rc) OrElse
                                       (delta.TryGetProperty("tool_calls", tcs) AndAlso tcs.ValueKind = JsonValueKind.Array AndAlso tcs.GetArrayLength() > 0)) Then
                                        result.FirstTokenMs = sent.ElapsedMilliseconds
                                    End If
                                    If delta.TryGetProperty("tool_calls", tcs) AndAlso tcs.ValueKind = JsonValueKind.Array Then
                                        For Each tc In tcs.EnumerateArray()
                                            Dim idx = JsonUtil.GetInt(tc, "index", calls.Count)
                                            Dim entry As StreamToolCall = Nothing
                                            If Not calls.TryGetValue(idx, entry) Then
                                                entry = New StreamToolCall()
                                                calls(idx) = entry
                                            End If
                                            Dim id = JsonUtil.GetString(tc, "id")
                                            If Not String.IsNullOrEmpty(id) Then entry.Id = id
                                            Dim fn As JsonElement
                                            If tc.TryGetProperty("function", fn) Then
                                                Dim n = JsonUtil.GetString(fn, "name")
                                                If Not String.IsNullOrEmpty(n) Then entry.Name = If(entry.Name Is Nothing, n, If(entry.Name.EndsWith(n, StringComparison.Ordinal), entry.Name, entry.Name & n))
                                                Dim a = JsonUtil.GetString(fn, "arguments")
                                                If a IsNot Nothing Then entry.Args.Append(a)
                                            End If
                                        Next
                                    End If
                                End If
                            End If
                        End Using
                    End While
                End Using
            End Using
            result.Content = If(content.Length > 0, content.ToString(), Nothing)
            result.ReasoningContent = If(reasoning.Length > 0, reasoning.ToString(), Nothing)
            For Each kv In calls
                If String.IsNullOrEmpty(kv.Value.Name) Then Continue For
                result.ToolCalls.Add(New ToolCallRequest With {
                    .Id = If(kv.Value.Id, Ids.NewId("call")), .Name = kv.Value.Name,
                    .ArgumentsJson = If(kv.Value.Args.Length = 0, "{}", kv.Value.Args.ToString())})
            Next
            If result.FinishReason Is Nothing AndAlso result.ToolCalls.Count = 0 AndAlso content.Length = 0 Then
                Throw New AiException(AiErrorKind.InvalidResponse, "AI stream ended without any content.")
            End If
            Return result
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Models / images / downloads
        ' ------------------------------------------------------------------------------------------

        Public Async Function ListModelsAsync(ct As CancellationToken) As Task(Of List(Of String)) Implements IAIProvider.ListModelsAsync
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(TimeSpan.FromSeconds(30))
                Using response = Await _http.GetAsync(BuildUrl(_opt.ModelsPath), cts.Token).ConfigureAwait(False)
                    Dim text = Await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                    If Not response.IsSuccessStatusCode Then Throw AiErrorClassifier.FromHttp(CInt(response.StatusCode), text)
                    Dim ids As New List(Of String)
                    Using doc = JsonDocument.Parse(text)
                        Dim data As JsonElement
                        If doc.RootElement.TryGetProperty("data", data) AndAlso data.ValueKind = JsonValueKind.Array Then
                            For Each m In data.EnumerateArray()
                                Dim id = JsonUtil.GetString(m, "id")
                                If id IsNot Nothing Then ids.Add(id)
                            Next
                        End If
                    End Using
                    Return ids
                End Using
            End Using
        End Function

        ''' <summary>
        ''' OpenWebUI image generation (POST /api/v1/images/generations) using whatever engine OpenWebUI is configured
        ''' with. Accepts responses containing url (relative to the OpenWebUI origin) or b64_json.
        ''' </summary>
        Public Function GenerateImageAsync(prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.GenerateImageAsync
            Return EngineImageAsync(Nothing, prompt, size, timeout, ct)
        End Function

        Public Function GenerateImageWithModelAsync(model As String, viaEngine As Boolean, prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.GenerateImageWithModelAsync
            Return If(viaEngine, EngineImageAsync(model, prompt, size, timeout, ct), ChatImageAsync(model, prompt, timeout, ct))
        End Function

        ''' <summary>The images endpoint (POST {ImageGenerationPath}); model Nothing = the endpoint's own model.</summary>
        Private Function EngineImageAsync(model As String, prompt As String, size As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult)
            Dim body As New JsonObject()
            body("prompt") = JsonValue.Create(prompt)
            body("n") = JsonValue.Create(1)
            If Not String.IsNullOrEmpty(size) Then body("size") = JsonValue.Create(size)
            If Not String.IsNullOrEmpty(model) Then body("model") = JsonValue.Create(model)
            Return SendImageRequestAsync(Function() New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), _opt.ImageGenerationPath, "Image generation", timeout, ct)
        End Function

        ''' <summary>
        ''' Changes a picture (or builds on it): POST {ImageEditPath} as multipart/form-data with model, prompt, one image file
        ''' and size, the OpenAI images/edits form.
        ''' </summary>
        Public Function EditImageWithModelAsync(model As String, prompt As String, size As String, image As Byte(), imageMime As String, imageName As String,
                                                timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult) Implements IAIProvider.EditImageWithModelAsync
            Dim path = If(String.IsNullOrWhiteSpace(_opt.ImageEditPath), If(_opt.ImageGenerationPath, "/images/generations").Replace("generations", "edits"), _opt.ImageEditPath)
            Dim build = Function() As HttpContent
                            Dim form As New MultipartFormDataContent()
                            If Not String.IsNullOrEmpty(model) Then form.Add(New StringContent(model), "model")
                            form.Add(New StringContent(prompt), "prompt")
                            Dim file As New ByteArrayContent(image)
                            file.Headers.ContentType = New Headers.MediaTypeHeaderValue(If(String.IsNullOrWhiteSpace(imageMime), "image/png", imageMime))
                            form.Add(file, "image", If(String.IsNullOrWhiteSpace(imageName), "image.png", imageName))
                            If Not String.IsNullOrEmpty(size) Then form.Add(New StringContent(size), "size")
                            form.Add(New StringContent("1"), "n")
                            Return form
                        End Function
            Return SendImageRequestAsync(build, path, "Image editing", timeout, ct)
        End Function

        ''' <summary>Posts an images request and reads the picture from the answer: b64_json, a data URL or a link to download.</summary>
        Private Async Function SendImageRequestAsync(content As Func(Of HttpContent), path As String, what As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult)
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(timeout)
                Dim text As String
                Try
                    Using msg As New HttpRequestMessage(HttpMethod.Post, BuildUrl(path))
                        msg.Content = content()
                        Using response = Await _http.SendAsync(msg, cts.Token).ConfigureAwait(False)
                            text = Await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                            If Not response.IsSuccessStatusCode Then Throw AiErrorClassifier.FromHttp(CInt(response.StatusCode), text)
                        End Using
                    End Using
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New AiException(AiErrorKind.Timeout, what & " timed out.", 0, ex)
                Catch ex As HttpRequestException
                    Throw New AiException(AiErrorKind.Transient, what & " connection failed: " & ex.Message, 0, ex)
                End Try

                Dim url As String = Nothing
                Dim b64 As String = Nothing
                Dim revised As String = Nothing
                Using doc = JsonDocument.Parse(text)
                    Dim items As JsonElement = doc.RootElement
                    Dim data As JsonElement
                    If items.ValueKind = JsonValueKind.Object AndAlso items.TryGetProperty("data", data) Then items = data
                    If items.ValueKind = JsonValueKind.Array AndAlso items.GetArrayLength() > 0 Then
                        Dim first = items(0)
                        url = JsonUtil.GetString(first, "url")
                        b64 = JsonUtil.GetString(first, "b64_json")
                        revised = JsonUtil.GetString(first, "revised_prompt")
                    End If
                End Using
                If Not String.IsNullOrEmpty(b64) Then
                    Dim bytes = Convert.FromBase64String(b64)
                    Dim sniffed = SniffMime(bytes)
                    Return New ImageGenerationResult With {.Data = bytes, .MimeType = If(sniffed IsNot Nothing AndAlso sniffed.StartsWith("image/", StringComparison.Ordinal), sniffed, "image/png"), .RevisedPrompt = revised}
                End If
                If String.IsNullOrEmpty(url) Then Throw New AiException(AiErrorKind.InvalidResponse, what & " returned no image: " & TextUtil.Truncate(text, 300))
                If url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) Then
                    Dim comma = url.IndexOf(","c)
                    Dim mime = url.Substring(5, url.IndexOf(";"c) - 5)
                    Return New ImageGenerationResult With {.Data = Convert.FromBase64String(url.Substring(comma + 1)), .MimeType = mime, .RevisedPrompt = revised}
                End If
                Dim dl = Await FetchImageAsync(url, cts.Token).ConfigureAwait(False)
                Return New ImageGenerationResult With {.Data = dl.Data, .MimeType = dl.MimeType, .RevisedPrompt = revised}
            End Using
        End Function

        ''' <summary>Asks an image model through chat completions and takes the first picture in its answer.</summary>
        Private Async Function ChatImageAsync(model As String, prompt As String, timeout As TimeSpan, ct As CancellationToken) As Task(Of ImageGenerationResult)
            Dim body As New JsonObject From {
                {"model", model}, {"stream", False},
                {"messages", New JsonArray(New JsonObject From {{"role", "user"}, {"content", prompt}})}}
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(timeout)
                Dim text As String
                Try
                    Using msg As New HttpRequestMessage(HttpMethod.Post, BuildUrl(_opt.ChatPath))
                        msg.Content = New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                        Using response = Await _http.SendAsync(msg, cts.Token).ConfigureAwait(False)
                            text = Await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                            If Not response.IsSuccessStatusCode Then Throw AiErrorClassifier.FromHttp(CInt(response.StatusCode), text)
                        End Using
                    End Using
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New AiException(AiErrorKind.Timeout, "Image generation timed out.", 0, ex)
                Catch ex As HttpRequestException
                    Throw New AiException(AiErrorKind.Transient, "Image generation connection failed: " & ex.Message, 0, ex)
                End Try
                Dim node As JsonNode = Nothing
                Try
                    node = JsonNode.Parse(text)
                Catch ex As JsonException
                End Try
                Dim found = ExtractImageRefs(node)
                If found.Count = 0 Then Throw New AiException(AiErrorKind.InvalidResponse, $"{model} answered without an image: " & TextUtil.Truncate(If(JsonUtil.FirstChoice(node)?("message")?("content")?.ToString(), text), 200))
                Dim first = found(0)
                If first.StartsWith("data:", StringComparison.OrdinalIgnoreCase) Then
                    Dim comma = first.IndexOf(","c)
                    Dim semi = first.IndexOf(";"c)
                    Return New ImageGenerationResult With {.Data = Convert.FromBase64String(first.Substring(comma + 1)), .MimeType = If(semi > 5, first.Substring(5, semi - 5), "image/png")}
                End If
                If Not first.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then Return New ImageGenerationResult With {.Data = Convert.FromBase64String(first), .MimeType = "image/png"}
                Dim dl = Await FetchImageAsync(first, cts.Token).ConfigureAwait(False)
                Return New ImageGenerationResult With {.Data = dl.Data, .MimeType = dl.MimeType}
            End Using
        End Function

        ''' <summary>
        ''' Pictures in an image model's chat answer, best first: message.images (OpenRouter / LiteLLM), image parts of the
        ''' content, Markdown images and data URLs in the text, then an images-API style data[] list. Each is a data URL, an
        ''' http(s) URL, or bare base64.
        ''' </summary>
        Friend Shared Function ExtractImageRefs(root As JsonNode) As List(Of String)
            Dim found As New List(Of String)
            If root Is Nothing Then Return found
            Dim add = Sub(s As String)
                          If Not String.IsNullOrWhiteSpace(s) AndAlso Not found.Contains(s) Then found.Add(s.Trim())
                      End Sub
            Dim msg = JsonUtil.FirstChoice(root)?("message")
            For Each img In If(TryCast(msg?("images"), JsonArray), New JsonArray())
                add(If(img?("image_url")?("url")?.ToString(), If(img?("url")?.ToString(), img?("b64_json")?.ToString())))
            Next
            Dim content = msg?("content")
            If TypeOf content Is JsonArray Then
                For Each part In DirectCast(content, JsonArray)
                    Dim kind = part?("type")?.ToString()
                    If kind = "image_url" Then add(part("image_url")?("url")?.ToString())
                    If kind = "output_image" OrElse kind = "image" Then add(If(part?("image_url")?.ToString(), If(part?("url")?.ToString(), part?("b64_json")?.ToString())))
                    If kind = "text" Then AddImagesInText(part?("text")?.ToString(), add)
                Next
            ElseIf content IsNot Nothing Then
                AddImagesInText(content.ToString(), add)
            End If
            For Each item In If(TryCast(root("data"), JsonArray), New JsonArray())
                add(If(item?("b64_json")?.ToString(), item?("url")?.ToString()))
            Next
            Return found
        End Function

        Private Shared Sub AddImagesInText(text As String, add As Action(Of String))
            If String.IsNullOrEmpty(text) Then Return
            For Each m As RegularExpressions.Match In RegularExpressions.Regex.Matches(text, "!\[[^\]]*\]\(\s*(data:image/[\w.+-]+;base64,[A-Za-z0-9+/=]+|https?://[^\s)]+)\s*\)")
                add(m.Groups(1).Value)
            Next
            For Each m As RegularExpressions.Match In RegularExpressions.Regex.Matches(text, "data:image/[\w.+-]+;base64,[A-Za-z0-9+/=]{100,}")
                add(m.Value)
            Next
            For Each m As RegularExpressions.Match In RegularExpressions.Regex.Matches(text, "https?://[^\s)""'<>]+\.(?:png|jpe?g|webp|gif)(?:\?[^\s)""'<>]*)?", RegularExpressions.RegexOptions.IgnoreCase)
                add(m.Value)
            Next
        End Sub

        ''' <summary>A generated image: from the provider itself (with its credentials), or from an image host it linked to.</summary>
        Private Async Function FetchImageAsync(url As String, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String))
            Dim baseUri As New Uri(_baseUrl & "/")
            Dim target As Uri = Nothing
            If url.StartsWith("/", StringComparison.Ordinal) OrElse (Uri.TryCreate(url, UriKind.Absolute, target) AndAlso
                                                                    String.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)) Then
                Return Await DownloadAsync(url, 30 * 1024 * 1024, ct).ConfigureAwait(False)
            End If
            Return Await DownloadExternalImageAsync(url, 30 * 1024 * 1024, ct).ConfigureAwait(False)
        End Function

        Private Shared ReadOnly ExternalHttp As New HttpClient(New SocketsHttpHandler With {.AllowAutoRedirect = False, .ConnectTimeout = TimeSpan.FromSeconds(15)}) With {.Timeout = TimeSpan.FromSeconds(90)}

        ''' <summary>
        ''' An image from the host an image model linked to (CDNs of image services). Guarded against SSRF: https only, public
        ''' addresses only (checked again for every redirect), no provider credentials, and the answer must be an image.
        ''' </summary>
        Friend Shared Function DownloadExternalImageAsync(url As String, maxBytes As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String))
            Return DownloadExternalMediaAsync(url, maxBytes, "image/", ct)
        End Function

        ''' <summary>
        ''' A generated picture or video from where the service linked it (its CDN), with the same guards: https, public
        ''' addresses (every redirect too), no credentials, and the answer must be that kind of media (by type or by content).
        ''' </summary>
        Friend Shared Async Function DownloadExternalMediaAsync(url As String, maxBytes As Integer, mediaPrefix As String, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String))
            Dim what = If(mediaPrefix = "video/", "video", "image")
            Dim target As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, target) Then Throw New AiException(AiErrorKind.InvalidResponse, $"The {what} link is not a URL.")
            For hop = 0 To 3
                If target.Scheme <> Uri.UriSchemeHttps Then Throw New AiException(AiErrorKind.InvalidResponse, $"Only https {what} links are downloaded.")
                Await EnsurePublicHostAsync(target, ct).ConfigureAwait(False)
                Dim response = Await ExternalHttp.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                Dim status = CInt(response.StatusCode)
                If status >= 300 AndAlso status < 400 AndAlso response.Headers.Location IsNot Nothing Then
                    target = If(response.Headers.Location.IsAbsoluteUri, response.Headers.Location, New Uri(target, response.Headers.Location))
                    response.Dispose()
                    Continue For
                End If
                Dim media = If(response.Content.Headers.ContentType?.MediaType, "")
                Dim generic = media.Length = 0 OrElse media.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase) OrElse media.Equals("binary/octet-stream", StringComparison.OrdinalIgnoreCase)
                If response.IsSuccessStatusCode AndAlso Not media.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase) AndAlso Not generic Then
                    response.Dispose()
                    Throw New AiException(AiErrorKind.InvalidResponse, $"The {what} link did not return a {what}.")
                End If
                Dim got = Await ReadLimitedAsync(response, maxBytes, ct).ConfigureAwait(False)
                Dim sniffed = SniffMime(got.Data)
                If sniffed Is Nothing OrElse Not sniffed.StartsWith(mediaPrefix, StringComparison.OrdinalIgnoreCase) Then
                    If generic Then Throw New AiException(AiErrorKind.InvalidResponse, $"The {what} link did not return a {what}.")
                    Return got
                End If
                Return (got.Data, sniffed)
            Next
            Throw New AiException(AiErrorKind.InvalidResponse, $"The {what} link redirects too often.")
        End Function

        ''' <summary>The media type from a file's first bytes (PNG, JPEG, WebP, GIF, MP4/MOV, WebM); Nothing when unknown.</summary>
        Friend Shared Function SniffMime(data As Byte()) As String
            If data Is Nothing OrElse data.Length < 12 Then Return Nothing
            If data(0) = &H89 AndAlso data(1) = &H50 AndAlso data(2) = &H4E AndAlso data(3) = &H47 Then Return "image/png"
            If data(0) = &HFF AndAlso data(1) = &HD8 Then Return "image/jpeg"
            If Encoding.ASCII.GetString(data, 0, 4) = "RIFF" AndAlso Encoding.ASCII.GetString(data, 8, 4) = "WEBP" Then Return "image/webp"
            If Encoding.ASCII.GetString(data, 0, 3) = "GIF" Then Return "image/gif"
            If Encoding.ASCII.GetString(data, 4, 4) = "ftyp" Then Return If(Encoding.ASCII.GetString(data, 8, 2) = "qt", "video/quicktime", "video/mp4")
            If data(0) = &H1A AndAlso data(1) = &H45 AndAlso data(2) = &HDF AndAlso data(3) = &HA3 Then Return "video/webm"
            Return Nothing
        End Function

        Private Shared Async Function EnsurePublicHostAsync(target As Uri, ct As CancellationToken) As Task
            Dim addresses As Net.IPAddress()
            Dim literal As Net.IPAddress = Nothing
            If Net.IPAddress.TryParse(target.DnsSafeHost, literal) Then
                addresses = {literal}
            Else
                addresses = Await Net.Dns.GetHostAddressesAsync(target.DnsSafeHost, ct).ConfigureAwait(False)
            End If
            If addresses.Length = 0 OrElse addresses.Any(Function(a) Not IsPublicAddress(a)) Then
                Throw New AiException(AiErrorKind.InvalidResponse, "The link points to a private address.")
            End If
        End Function

        Friend Shared Function IsPublicAddress(address As Net.IPAddress) As Boolean
            If address.IsIPv4MappedToIPv6 Then address = address.MapToIPv4()
            If Net.IPAddress.IsLoopback(address) Then Return False
            Dim b = address.GetAddressBytes()
            If address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork Then
                If b(0) = 10 OrElse b(0) = 0 OrElse b(0) = 127 OrElse b(0) >= 224 Then Return False
                If b(0) = 172 AndAlso b(1) >= 16 AndAlso b(1) <= 31 Then Return False
                If b(0) = 192 AndAlso b(1) = 168 Then Return False
                If b(0) = 169 AndAlso b(1) = 254 Then Return False
                If b(0) = 100 AndAlso b(1) >= 64 AndAlso b(1) <= 127 Then Return False
                Return True
            End If
            If address.IsIPv6LinkLocal OrElse address.IsIPv6SiteLocal OrElse address.IsIPv6Multicast Then Return False
            If (b(0) And &HFE) = &HFC Then Return False ' fc00::/7 unique local
            Return Not address.Equals(Net.IPAddress.IPv6Any)
        End Function

        ''' <summary>
        ''' Downloads a file served by the AI provider itself (e.g. a generated image). Only same-origin URLs are
        ''' allowed: the host never fetches arbitrary URLs on behalf of agents (prevents SSRF and key leakage).
        ''' </summary>
        Public Async Function DownloadAsync(url As String, maxBytes As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String)) Implements IAIProvider.DownloadAsync
            Dim baseUri As New Uri(_baseUrl & "/")
            Dim target As Uri = Nothing
            If url.StartsWith("/", StringComparison.Ordinal) Then
                target = New Uri(New Uri(baseUri.GetLeftPart(UriPartial.Authority)), url)
            ElseIf Not Uri.TryCreate(url, UriKind.Absolute, target) Then
                Throw New AiException(AiErrorKind.InvalidRequest, "Invalid download URL.")
            End If
            If Not String.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) OrElse target.Port <> baseUri.Port OrElse target.Scheme <> baseUri.Scheme Then
                Throw New AiException(AiErrorKind.InvalidRequest, "Only files served by the configured AI provider can be downloaded.")
            End If
            Using msg As New HttpRequestMessage(HttpMethod.Get, target)
                Dim response = Await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                Return Await ReadLimitedAsync(response, maxBytes, ct).ConfigureAwait(False)
            End Using
        End Function

        Private Shared Async Function ReadLimitedAsync(response As HttpResponseMessage, maxBytes As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String))
            Using response
                If Not response.IsSuccessStatusCode Then Throw New AiException(AiErrorKind.InvalidRequest, $"Download failed: HTTP {CInt(response.StatusCode)}", CInt(response.StatusCode))
                Dim len = response.Content.Headers.ContentLength
                If len.HasValue AndAlso len.Value > maxBytes Then Throw New AiException(AiErrorKind.InvalidRequest, $"Download exceeds {maxBytes} bytes.")
                Using s = Await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(False)
                    Using ms As New MemoryStream()
                        Dim buf(81919) As Byte
                        While True
                            Dim n = Await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
                            If n <= 0 Then Exit While
                            ms.Write(buf, 0, n)
                            If ms.Length > maxBytes Then Throw New AiException(AiErrorKind.InvalidRequest, $"Download exceeds {maxBytes} bytes.")
                        End While
                        Return (ms.ToArray(), If(response.Content.Headers.ContentType?.MediaType, "application/octet-stream"))
                    End Using
                End Using
            End Using
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            _http.Dispose()
        End Sub
    End Class

    ''' <summary>Maps HTTP status codes and provider error bodies (OpenAI, LiteLLM, OpenWebUI) to error kinds.</summary>
    Public Module AiErrorClassifier
        Public Function FromHttp(status As Integer, body As String) As AiException
            Dim text = If(body, String.Empty)
            Dim lower = text.ToLowerInvariant()
            Dim msg = ExtractMessage(text)
            Dim kind As AiErrorKind
            If lower.Contains("vision encoder") OrElse lower.Contains("does not support image") OrElse lower.Contains("image input") AndAlso lower.Contains("not support") OrElse
               lower.Contains("does not support tools") OrElse lower.Contains("tool use is not supported") OrElse lower.Contains("does not support function") Then
                kind = AiErrorKind.CapabilityUnsupported
            ElseIf lower.Contains("context length") OrElse lower.Contains("context_length") OrElse lower.Contains("maximum context") OrElse
                   lower.Contains("too many tokens") OrElse lower.Contains("prompt is too long") OrElse lower.Contains("input is too long") OrElse
                   lower.Contains("leaves no room") OrElse lower.Contains("context window") Then
                kind = AiErrorKind.ContextOverflow
            ElseIf lower.Contains("no available channel") OrElse lower.Contains("model not found") OrElse lower.Contains("model_not_found") OrElse
                   lower.Contains("serviceunavailable") OrElse lower.Contains("does not exist") AndAlso lower.Contains("model") OrElse
                   lower.Contains("no deployments available") OrElse lower.Contains("model is currently overloaded") OrElse
                   IsOutOfCredit(text, lower) Then
                ' An upstream account out of credit cannot serve this model now (retrying soon will not help): another model may.
                kind = AiErrorKind.ModelUnavailable
            ElseIf status = 429 OrElse lower.Contains("rate limit") OrElse lower.Contains("ratelimit") Then
                kind = AiErrorKind.RateLimited
            ElseIf status = 401 OrElse status = 403 Then
                kind = AiErrorKind.Auth
            ElseIf status = 404 Then
                kind = AiErrorKind.ModelUnavailable
            ElseIf status = 408 OrElse status = 524 OrElse status = 504 Then
                kind = AiErrorKind.Timeout
            ElseIf status >= 500 OrElse status = 0 Then
                kind = AiErrorKind.Transient
            ElseIf Global.Agentic.Work.ChatService.IsOutageText(text) Then
                ' Proxies (LiteLLM, OpenWebUI) report a model server that is down as 400 with the real cause in the text
                ' ("InternalServerError ... Connection error"): an outage to wait out, not a bad request.
                kind = AiErrorKind.Transient
            ElseIf status = 400 OrElse status = 422 Then
                kind = AiErrorKind.InvalidRequest
            Else
                kind = AiErrorKind.Transient
            End If
            Return New AiException(kind, $"HTTP {status}: {TextUtil.Truncate(msg, 500)}", status) With {.OutOfCredit = IsOutOfCredit(text, lower)}
        End Function

        ''' <summary>
        ''' The provider (or a relay in front of it) has no credit left for the request: OpenAI's insufficient_quota, "credit
        ''' balance is too low", the Chinese relays' 额度 (quota) / 余额 (balance) messages such as "预扣费额度失败", and relays
        ''' that refuse a request costing more than the balance ("Request may exceed your balance. Top up …": coding-t2 answered
        ''' it 153 times on 2026-11-01 as a bad request, so it was retried instead of paused).
        ''' </summary>
        Friend Function IsOutOfCredit(text As String, lower As String) As Boolean
            Return lower.Contains("insufficient_quota") OrElse lower.Contains("insufficient quota") OrElse lower.Contains("insufficient balance") OrElse
                   lower.Contains("insufficient credit") OrElse lower.Contains("credit balance is too low") OrElse lower.Contains("exceeded your current quota") OrElse
                   lower.Contains("exceed your balance") OrElse lower.Contains("exceeds your balance") OrElse lower.Contains("top up") OrElse
                   text.Contains("额度") OrElse text.Contains("余额")
        End Function

        Private Function ExtractMessage(body As String) As String
            Try
                Using doc = JsonDocument.Parse(body)
                    Dim root = doc.RootElement
                    Dim e As JsonElement
                    If root.TryGetProperty("detail", e) Then Return If(e.ValueKind = JsonValueKind.String, e.GetString(), e.GetRawText())
                    If root.TryGetProperty("error", e) Then
                        If e.ValueKind = JsonValueKind.String Then Return e.GetString()
                        Dim m = JsonUtil.GetString(e, "message")
                        If m IsNot Nothing Then Return m
                        Return e.GetRawText()
                    End If
                    If root.TryGetProperty("message", e) AndAlso e.ValueKind = JsonValueKind.String Then Return e.GetString()
                End Using
            Catch
            End Try
            Return body
        End Function
    End Module

End Namespace
