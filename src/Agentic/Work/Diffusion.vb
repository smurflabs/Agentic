Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A failure of the Stable Diffusion server, or a request refused here: the status to answer with and why.</summary>
    Public NotInheritable Class DiffusionException
        Inherits Exception

        Public ReadOnly Property Status As Integer

        Public Sub New(status As Integer, message As String)
            MyBase.New(message)
            Me.Status = status
        End Sub
    End Class

    ''' <summary>A job as the Stable Diffusion server describes it (GET /jobs/{id}), read the same whatever its field names.</summary>
    Public NotInheritable Class DiffusionJobView
        ''' <summary>queued | running | done | failed | cancelled (or the server's own word).</summary>
        Public Property Status As String
        ''' <summary>0 to 1, when the server says.</summary>
        Public Property Progress As Double?
        Public Property [Step] As Integer?
        Public Property TotalSteps As Integer?
        ''' <summary>Which picture of the job is being made (from 1), when the server says.</summary>
        Public Property ImageIndex As Integer?
        Public Property QueuePosition As Integer?
        Public Property [Error] As String
        ''' <summary>How long the server worked on it, when it says.</summary>
        Public Property ElapsedMs As Long?
        Public Property Seeds As New List(Of Long)
        Public Property Infos As New List(Of String)
        ''' <summary>Pictures the job carries inline (base64), when the server sends them with the job.</summary>
        Public Property InlineImages As New List(Of String)
        ''' <summary>How many pictures the job has made, when the server says.</summary>
        Public Property ImagesReady As Integer?

        Public ReadOnly Property Finished As Boolean
            Get
                Return Status = "done" OrElse Status = "failed" OrElse Status = "cancelled"
            End Get
        End Property
    End Class

    ''' <summary>
    ''' The Stable Diffusion 1.5 server (StableDiffusion): jobs (POST /jobs, GET /jobs/{id}, its pictures and live preview,
    ''' DELETE to cancel), preprocessing for ControlNet, the lists of models, VAEs, LoRAs, embeddings, ControlNets,
    ''' preprocessors and schedulers (/options) and its health. Its API key stays on this server.
    ''' </summary>
    Public NotInheritable Class DiffusionService
        ''' <summary>The ControlNet shorthands the server knows (lllyasviel v1.1); /options may list more.</summary>
        Public Shared ReadOnly ControlNetModels As String() = {"canny", "depth", "openpose", "lineart", "lineart_anime", "softedge", "scribble", "mlsd", "normalbae", "seg", "inpaint", "tile", "shuffle", "ip2p"}
        ''' <summary>The preprocessors the server has (there is no seg preprocessor); /options may list more.</summary>
        Public Shared ReadOnly Preprocessors As String() = {"canny", "depth", "depth_zoe", "openpose", "openpose_full", "lineart", "lineart_coarse", "lineart_anime",
                                                            "softedge_hed", "softedge_pidi", "scribble_hed", "scribble_pidi", "mlsd", "normalbae", "shuffle", "inpaint"}
        Public Shared ReadOnly Upscalers As String() = {"lanczos", "latent"}
        Private Shared ReadOnly SafeName As New Regex("^[^\x00-\x1F]{1,300}$", RegexOptions.Compiled)

        Private ReadOnly _opt As StableDiffusionOptions
        Private ReadOnly _vault As SecretVault
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _lock As New Object()
        Private _key As String
        Private _keyAt As DateTime
        Private _options As JsonNode
        Private _optionsAt As DateTime

        Public Sub New(options As StableDiffusionOptions, vault As SecretVault, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _opt = If(options, New StableDiffusionOptions())
            _vault = vault
            _log = log
            _http = If(handler Is Nothing, New HttpClient(), New HttpClient(handler))
            _http.Timeout = Timeout.InfiniteTimeSpan
            Queue = New WorkQueue(_opt.MaxConcurrency)
        End Sub

        Public ReadOnly Property Options As StableDiffusionOptions
            Get
                Return _opt
            End Get
        End Property

        ''' <summary>
        ''' The line for the server's MaxConcurrency (1) slots: a job holds its slot from when it is sent until it ends, and
        ''' preprocessor runs wait their turn the same way.
        ''' </summary>
        Public ReadOnly Property Queue As WorkQueue

        Public ReadOnly Property Configured As Boolean
            Get
                Return _opt.Enabled AndAlso Not String.IsNullOrWhiteSpace(_opt.BaseUrl)
            End Get
        End Property

        Public ReadOnly Property BaseUrl As String
            Get
                Return If(_opt.BaseUrl, "").Trim().TrimEnd("/"c)
            End Get
        End Property

        Public ReadOnly Property Host As String
            Get
                Dim u As Uri = Nothing
                Return If(Uri.TryCreate(BaseUrl, UriKind.Absolute, u), u.Host, Nothing)
            End Get
        End Property

        Public ReadOnly Property KeyInConfig As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_opt.ApiKey)
            End Get
        End Property

        ''' <summary>The API key: StableDiffusion.ApiKey, else the vault entry ApiKeySecret (read again after 30 seconds, or after ForgetKey).</summary>
        Public Async Function KeyAsync() As Task(Of String)
            If KeyInConfig Then Return _opt.ApiKey.Trim()
            If _vault Is Nothing OrElse String.IsNullOrWhiteSpace(_opt.ApiKeySecret) Then Return Nothing
            SyncLock _lock
                If _keyAt > DateTime.UtcNow.AddSeconds(-30) Then Return _key
            End SyncLock
            Dim v As String = Nothing
            Try
                v = Await _vault.GetValueAsync(_opt.ApiKeySecret.Trim()).ConfigureAwait(False)
            Catch ex As Exception
                _log?.Warn("Images", "Could not read the Stable Diffusion key from the vault: " & ex.Message)
            End Try
            SyncLock _lock
                _key = If(String.IsNullOrWhiteSpace(v), Nothing, v.Trim())
                _keyAt = DateTime.UtcNow
                Return _key
            End SyncLock
        End Function

        Public Sub ForgetKey()
            SyncLock _lock
                _keyAt = DateTime.MinValue
                _options = Nothing
            End SyncLock
        End Sub

        ' ------------------------------------------------------------------ requests

        Private Async Function SendAsync(method As HttpMethod, path As String, content As HttpContent, ct As CancellationToken) As Task(Of HttpResponseMessage)
            If Not Configured Then Throw New DiffusionException(409, "No Stable Diffusion server is set up (StableDiffusion.BaseUrl).")
            Dim key = Await KeyAsync().ConfigureAwait(False)
            If String.IsNullOrEmpty(key) Then Throw New DiffusionException(409, "The Stable Diffusion server's API key is not set: an administrator sets it on the Images tab (Settings).")
            Dim req As New HttpRequestMessage(method, BaseUrl & path) With {.Content = content}
            req.Headers.Add("X-API-Key", key)
            Dim resp As HttpResponseMessage
            Try
                resp = Await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(False)
            Catch ex As HttpRequestException
                Throw New DiffusionException(502, $"The Stable Diffusion server ({BaseUrl}) cannot be reached: {ex.Message}")
            End Try
            If resp.IsSuccessStatusCode Then Return resp
            Dim body = ""
            Try
                body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IOException
            End Try
            Dim status = CInt(resp.StatusCode)
            resp.Dispose()
            Dim detail = SpeechService.DetailOf(body)
            If status = 401 OrElse status = 403 Then Throw New DiffusionException(502, "The Stable Diffusion server refused the API key" & If(detail Is Nothing, ".", ": " & detail))
            If status = 404 Then Throw New DiffusionException(404, If(detail, "The Stable Diffusion server does not have that."))
            If status = 400 OrElse status = 422 Then Throw New DiffusionException(400, If(detail, $"The Stable Diffusion server refused the request ({status})."))
            If status = 409 OrElse status = 429 OrElse status = 503 Then Throw New DiffusionException(503, If(detail, "The Stable Diffusion server is busy."))
            Throw New DiffusionException(502, $"The Stable Diffusion server failed ({status})" & If(detail Is Nothing, ".", ": " & detail))
        End Function

        Private Function Deadline(ct As CancellationToken, seconds As Integer) As CancellationTokenSource
            Dim cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            cts.CancelAfter(TimeSpan.FromSeconds(seconds))
            Return cts
        End Function

        Private Async Function JsonAsync(method As HttpMethod, path As String, body As JsonObject, ct As CancellationToken, Optional seconds As Integer = 30) As Task(Of JsonNode)
            Using cts = Deadline(ct, seconds)
                Try
                    Dim content = If(body Is Nothing, Nothing, New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"))
                    Using resp = Await SendAsync(method, path, content, cts.Token).ConfigureAwait(False)
                        Dim text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                        If String.IsNullOrWhiteSpace(text) Then Return New JsonObject()
                        Return JsonNode.Parse(text)
                    End Using
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New DiffusionException(504, $"The Stable Diffusion server did not answer within {seconds} s.")
                Catch ex As JsonException
                    Throw New DiffusionException(502, "The Stable Diffusion server's answer could not be read.")
                End Try
            End Using
        End Function

        ''' <summary>A picture from the server (Nothing when it has none: 404 or no content).</summary>
        Private Async Function BytesAsync(path As String, ct As CancellationToken) As Task(Of (Data As Byte(), ContentType As String))
            Using cts = Deadline(ct, 60)
                Try
                    Using resp = Await SendAsync(HttpMethod.Get, path, Nothing, cts.Token).ConfigureAwait(False)
                        Dim data = Await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(False)
                        If data.Length = 0 Then Return (Nothing, Nothing)
                        Return (data, If(resp.Content.Headers.ContentType?.MediaType, "image/png"))
                    End Using
                Catch ex As DiffusionException When ex.Status = 404
                    Return (Nothing, Nothing)
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New DiffusionException(504, "The Stable Diffusion server did not send the picture within 60 s.")
                End Try
            End Using
        End Function

        ''' <summary>Models, VAEs, LoRAs, embeddings, ControlNets, preprocessors and schedulers (kept for 30 seconds).</summary>
        Public Async Function OptionsAsync(refresh As Boolean, ct As CancellationToken) As Task(Of JsonNode)
            SyncLock _lock
                If Not refresh AndAlso _options IsNot Nothing AndAlso _optionsAt > DateTime.UtcNow.AddSeconds(-30) Then Return _options.DeepClone()
            End SyncLock
            Dim j = Await JsonAsync(HttpMethod.Get, "/options", Nothing, ct).ConfigureAwait(False)
            SyncLock _lock
                _options = j
                _optionsAt = DateTime.UtcNow
            End SyncLock
            Return j.DeepClone()
        End Function

        ''' <summary>The GPU, the model loaded, the queue's length and VRAM.</summary>
        Public Function HealthAsync(ct As CancellationToken) As Task(Of JsonNode)
            Return JsonAsync(HttpMethod.Get, "/health", Nothing, ct, 15)
        End Function

        ''' <summary>Queues a job (POST /jobs); returns the server's answer, with its id.</summary>
        Public Async Function SubmitAsync(body As JsonObject, ct As CancellationToken) As Task(Of (Id As String, Raw As JsonNode))
            Dim j = Await JsonAsync(HttpMethod.Post, "/jobs", body, ct, 120).ConfigureAwait(False)
            Dim id = If(j?("id"), If(j?("job_id"), j?("jobId")))?.ToString()
            If String.IsNullOrWhiteSpace(id) Then Throw New DiffusionException(502, "The Stable Diffusion server queued the job without an id.")
            Return (id, j)
        End Function

        Public Function JobAsync(id As String, ct As CancellationToken) As Task(Of JsonNode)
            Return JsonAsync(HttpMethod.Get, "/jobs/" & Uri.EscapeDataString(id), Nothing, ct, 20)
        End Function

        ''' <summary>Picture n of a finished job (Nothing when there is no such picture).</summary>
        Public Function ImageAsync(id As String, n As Integer, ct As CancellationToken) As Task(Of (Data As Byte(), ContentType As String))
            Return BytesAsync($"/jobs/{Uri.EscapeDataString(id)}/images/{n}", ct)
        End Function

        ''' <summary>The rough live preview of the picture being made (Nothing when there is none yet).</summary>
        Public Function PreviewAsync(id As String, ct As CancellationToken) As Task(Of (Data As Byte(), ContentType As String))
            Return BytesAsync($"/jobs/{Uri.EscapeDataString(id)}/preview", ct)
        End Function

        Public Function CancelAsync(id As String, ct As CancellationToken) As Task(Of JsonNode)
            Return JsonAsync(HttpMethod.Delete, "/jobs/" & Uri.EscapeDataString(id), Nothing, ct, 20)
        End Function

        ''' <summary>{preprocessor, image, params}: the control map (a PNG). It waits its turn in line behind running jobs.</summary>
        Public Async Function PreprocessAsync(body As JsonObject, ct As CancellationToken, Optional owner As String = Nothing) As Task(Of (Data As Byte(), ContentType As String))
            Using slot = Await Queue.EnterAsync(If(owner, ""), "preprocess", ct).ConfigureAwait(False)
                Using cts = Deadline(ct, 300)
                    Try
                        Using resp = Await SendAsync(HttpMethod.Post, "/preprocess", New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), cts.Token).ConfigureAwait(False)
                            Dim data = Await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(False)
                            If data.Length = 0 Then Throw New DiffusionException(502, "The Stable Diffusion server sent no control map.")
                            Return (data, If(resp.Content.Headers.ContentType?.MediaType, "image/png"))
                        End Using
                    Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                        Throw New DiffusionException(504, "The preprocessor did not finish within 300 s (the first use of one downloads it).")
                    End Try
                End Using
            End Using
        End Function

        ' ------------------------------------------------------------------ requests from pages

        ''' <summary>
        ''' The JSON for /jobs from what a page sent, every field checked against the server's ranges (GenerateRequest). Pictures
        ''' (init_image, mask_image, a ControlNet's image) go through resolveImage, which turns "gallery:{id}" into the picture
        ''' and checks the size of base64 ones.
        ''' </summary>
        Public Shared Function BuildRequest(input As JsonElement, resolveImage As Func(Of String, String)) As JsonObject
            If input.ValueKind <> JsonValueKind.Object Then Throw New DiffusionException(400, "Send the settings as a JSON object.")
            Dim body As New JsonObject()
            Dim prompt = If(JsonUtil.GetString(input, "prompt"), "").Trim()
            If prompt.Length = 0 Then Throw New DiffusionException(400, "Write a prompt.")
            If prompt.Length > 20000 Then Throw New DiffusionException(400, "The prompt is longer than 20,000 characters.")
            body("prompt") = prompt
            Dim negative = JsonUtil.GetString(input, "negative_prompt")
            If negative IsNot Nothing Then
                If negative.Length > 20000 Then Throw New DiffusionException(400, "The negative prompt is longer than 20,000 characters.")
                body("negative_prompt") = negative
            End If
            For Each n In {"model", "vae", "scheduler"}
                Dim v = JsonUtil.GetString(input, n)
                If String.IsNullOrWhiteSpace(v) Then Continue For
                If Not SafeName.IsMatch(v.Trim()) Then Throw New DiffusionException(400, $"{n} is a name (up to 300 characters).")
                body(n) = v.Trim()
            Next
            For Each n In {"width", "height"}
                Dim v = WholeOf(input, n, 64, 2048)
                If v.HasValue Then body(n) = v.Value - v.Value Mod 8
            Next
            Put(body, "steps", WholeOf(input, "steps", 1, 150))
            Put(body, "guidance_scale", NumberOf(input, "guidance_scale", 0, 30, False))
            Put(body, "guidance_rescale", NumberOf(input, "guidance_rescale", 0, 1, False))
            Put(body, "eta", NumberOf(input, "eta", 0, 1, False))
            Put(body, "clip_skip", WholeOf(input, "clip_skip", 1, 12))
            Dim seed = LongOf(input, "seed")
            If seed.HasValue AndAlso seed.Value >= 0 Then body("seed") = seed.Value
            Put(body, "num_images", WholeOf(input, "num_images", 1, 16))
            For Each n In {"freeu", "invert_mask", "guess_mode"}
                Dim b = FlagOf(input, n)
                If b.HasValue Then body(n) = b.Value
            Next

            Dim loras = ArrayOf(input, "loras", 20)
            If loras.Count > 0 Then
                Dim list As New JsonArray()
                For Each l In loras
                    Dim name = If(JsonUtil.GetString(l, "name"), "").Trim()
                    If name.Length = 0 OrElse Not SafeName.IsMatch(name) Then Throw New DiffusionException(400, "Each LoRA needs a name (a file in loras/ or a Hugging Face repo id).")
                    Dim o As New JsonObject From {{"name", name}}
                    Put(o, "weight", NumberOf(l, "weight", -4, 4, False))
                    list.Add(o)
                Next
                body("loras") = list
            End If

            Dim init = JsonUtil.GetString(input, "init_image")
            If Not String.IsNullOrWhiteSpace(init) Then
                body("init_image") = resolveImage(init)
                Put(body, "strength", NumberOf(input, "strength", 0, 1, True))
            End If
            Dim mask = JsonUtil.GetString(input, "mask_image")
            If Not String.IsNullOrWhiteSpace(mask) Then
                If body("init_image") Is Nothing Then Throw New DiffusionException(400, "Inpainting needs the picture to change (init_image) with its mask.")
                body("mask_image") = resolveImage(mask)
                Put(body, "mask_blur", WholeOf(input, "mask_blur", 0, 64))
                Put(body, "inpaint_padding", WholeOf(input, "inpaint_padding", 0, 512))
            End If

            Dim nets = ArrayOf(input, "controlnets", 8)
            If nets.Count > 0 Then
                Dim list As New JsonArray()
                For Each c In nets
                    Dim model = If(JsonUtil.GetString(c, "model"), "").Trim()
                    If model.Length = 0 OrElse Not SafeName.IsMatch(model) Then Throw New DiffusionException(400, "Each ControlNet needs a model (canny, depth, openpose, …).")
                    Dim o As New JsonObject From {{"model", model}}
                    Dim img = JsonUtil.GetString(c, "image")
                    If Not String.IsNullOrWhiteSpace(img) Then
                        o("image") = resolveImage(img)
                    ElseIf body("init_image") Is Nothing Then
                        Throw New DiffusionException(400, $"The {model} ControlNet needs an image (or an init image to use).")
                    End If
                    Dim pre = JsonUtil.GetString(c, "preprocessor")
                    If Not String.IsNullOrWhiteSpace(pre) Then
                        If Not SafeName.IsMatch(pre.Trim()) Then Throw New DiffusionException(400, "preprocessor is a name.")
                        o("preprocessor") = pre.Trim()
                    End If
                    Dim p As JsonElement
                    If c.TryGetProperty("preprocessor_params", p) AndAlso p.ValueKind <> JsonValueKind.Null Then
                        If p.ValueKind <> JsonValueKind.Object Then Throw New DiffusionException(400, "preprocessor_params is an object, like {""low_threshold"": 50}.")
                        o("preprocessor_params") = JsonNode.Parse(p.GetRawText())
                    End If
                    Dim scale = NumberOf(c, "scale", 0, 2, False), start = NumberOf(c, "start", 0, 1, False), [end] = NumberOf(c, "end", 0, 1, False)
                    If start.GetValueOrDefault(0) > [end].GetValueOrDefault(1) Then Throw New DiffusionException(400, $"The {model} ControlNet starts after it ends.")
                    Put(o, "scale", scale)
                    Put(o, "start", start)
                    Put(o, "end", [end])
                    list.Add(o)
                Next
                body("controlnets") = list
            End If

            Dim h As JsonElement
            If input.TryGetProperty("hires", h) AndAlso h.ValueKind = JsonValueKind.Object Then
                If body("init_image") IsNot Nothing Then Throw New DiffusionException(400, "Hires fix works with text to image only (no init image).")
                Dim o As New JsonObject()
                Put(o, "scale", NumberOf(h, "scale", 1, 4, False))
                Dim up = JsonUtil.GetString(h, "upscaler")
                If Not String.IsNullOrWhiteSpace(up) Then
                    If Not Upscalers.Contains(up) Then Throw New DiffusionException(400, "upscaler is lanczos or latent.")
                    o("upscaler") = up
                End If
                Put(o, "steps", WholeOf(h, "steps", 1, 150))
                Put(o, "denoise", NumberOf(h, "denoise", 0, 1, True))
                body("hires") = o
            End If
            Return body
        End Function

        ''' <summary>The request without its pictures (they are big), for keeping with the job and its pictures.</summary>
        Public Shared Function Summary(body As JsonObject) As JsonObject
            Dim s = CType(body.DeepClone(), JsonObject)
            For Each n In {"init_image", "mask_image"}
                If s(n) IsNot Nothing Then s(n) = True
            Next
            Dim nets = TryCast(s("controlnets"), JsonArray)
            If nets IsNot Nothing Then
                For Each c In nets.OfType(Of JsonObject)()
                    If c("image") IsNot Nothing Then c("image") = True
                Next
            End If
            Return s
        End Function

        ''' <summary>
        ''' About how long the GPU works on a request, for the balance check before it is queued: a GTX 1660 Ti in fp32 makes a
        ''' 512×512 step in about 0.4 s; img2img runs steps × strength, the hires pass its steps × denoise at the larger size.
        ''' </summary>
        Public Shared Function EstimateGpuMs(body As JsonObject) As Long
            ' Values may hold an Integer, a Long or a Double: read them as text.
            Dim d = Function(name As String, fallback As Double) As Double
                        Return If(Num(body(name)), fallback)
                    End Function
            Dim w = d("width", 512), h = d("height", 512), steps = d("steps", 25), n = d("num_images", 1)
            If body("init_image") IsNot Nothing Then steps *= d("strength", 0.75)
            Dim perStep = 400.0 * w * h / (512.0 * 512.0)
            Dim total = steps * perStep
            Dim hires = TryCast(body("hires"), JsonObject)
            If hires IsNot Nothing Then
                Dim hv = Function(name As String, fallback As Double) As Double
                             Return If(Num(hires(name)), fallback)
                         End Function
                Dim scale = hv("scale", 2)
                total += hv("steps", d("steps", 25)) * hv("denoise", 0.5) * perStep * scale * scale
            End If
            Return CLng(total * Math.Max(1, n))
        End Function

        ''' <summary>txt2img, img2img or inpaint, from the inputs.</summary>
        Public Shared Function ModeOf(body As JsonObject) As String
            If body("mask_image") IsNot Nothing Then Return "inpaint"
            If body("init_image") IsNot Nothing Then Return "img2img"
            Return "txt2img"
        End Function

        Private Shared Sub Put(o As JsonObject, name As String, v As Double?)
            If v.HasValue Then o(name) = v.Value
        End Sub

        Private Shared Sub Put(o As JsonObject, name As String, v As Integer?)
            If v.HasValue Then o(name) = v.Value
        End Sub

        Private Shared Function Raw(el As JsonElement, name As String) As JsonElement?
            Dim p As JsonElement
            If el.ValueKind <> JsonValueKind.Object OrElse Not el.TryGetProperty(name, p) OrElse p.ValueKind = JsonValueKind.Null OrElse p.ValueKind = JsonValueKind.Undefined Then Return Nothing
            If p.ValueKind = JsonValueKind.String AndAlso String.IsNullOrWhiteSpace(p.GetString()) Then Return Nothing
            Return p
        End Function

        Private Shared Function DoubleOf(el As JsonElement, name As String) As Double?
            Dim p = Raw(el, name)
            If Not p.HasValue Then Return Nothing
            Dim d As Double
            If p.Value.ValueKind = JsonValueKind.Number Then Return p.Value.GetDouble()
            If p.Value.ValueKind = JsonValueKind.String AndAlso Double.TryParse(p.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
            Throw New DiffusionException(400, $"{name} must be a number.")
        End Function

        Private Shared Function NumberOf(el As JsonElement, name As String, min As Double, max As Double, exclusiveMin As Boolean) As Double?
            Dim v = DoubleOf(el, name)
            If Not v.HasValue Then Return Nothing
            Dim x = v.Value
            If Double.IsNaN(x) OrElse Double.IsInfinity(x) OrElse x < min OrElse x > max OrElse (exclusiveMin AndAlso x = min) Then
                Throw New DiffusionException(400, $"{name} must be {If(exclusiveMin, "above", "at least")} {min.ToString(CultureInfo.InvariantCulture)} and at most {max.ToString(CultureInfo.InvariantCulture)}.")
            End If
            Return x
        End Function

        Private Shared Function WholeOf(el As JsonElement, name As String, min As Integer, max As Integer) As Integer?
            Dim v = DoubleOf(el, name)
            If Not v.HasValue Then Return Nothing
            If v.Value <> Math.Floor(v.Value) OrElse v.Value < min OrElse v.Value > max Then Throw New DiffusionException(400, $"{name} must be a whole number from {min} to {max}.")
            Return CInt(v.Value)
        End Function

        Private Shared Function LongOf(el As JsonElement, name As String) As Long?
            Dim v = DoubleOf(el, name)
            If Not v.HasValue Then Return Nothing
            If v.Value <> Math.Floor(v.Value) OrElse v.Value > 9.2E+18 Then Throw New DiffusionException(400, $"{name} must be a whole number.")
            Return CLng(v.Value)
        End Function

        Private Shared Function FlagOf(el As JsonElement, name As String) As Boolean?
            Dim p = Raw(el, name)
            If Not p.HasValue Then Return Nothing
            If p.Value.ValueKind = JsonValueKind.True Then Return True
            If p.Value.ValueKind = JsonValueKind.False Then Return False
            Throw New DiffusionException(400, $"{name} is true or false.")
        End Function

        Private Shared Function ArrayOf(el As JsonElement, name As String, max As Integer) As List(Of JsonElement)
            Dim p = Raw(el, name)
            If Not p.HasValue Then Return New List(Of JsonElement)
            If p.Value.ValueKind <> JsonValueKind.Array Then Throw New DiffusionException(400, $"{name} is a list.")
            Dim list = p.Value.EnumerateArray().ToList()
            If list.Count > max Then Throw New DiffusionException(400, $"Use at most {max} {name}.")
            If list.Any(Function(x) x.ValueKind <> JsonValueKind.Object) Then Throw New DiffusionException(400, $"Each of {name} is an object.")
            Return list
        End Function

        ' ------------------------------------------------------------------ the server's answers

        ''' <summary>The first of these fields with a plain value, at the top of the answer or in its nested progress object.</summary>
        Private Shared Function FieldOf(o As JsonObject, nested As JsonObject, ParamArray names As String()) As JsonNode
            For Each n In names
                If o(n) IsNot Nothing AndAlso TypeOf o(n) IsNot JsonObject Then Return o(n)
                If nested IsNot Nothing AndAlso nested(n) IsNot Nothing AndAlso TypeOf nested(n) IsNot JsonObject Then Return nested(n)
            Next
            Return Nothing
        End Function

        ''' <summary>A job's state, whatever the server calls its fields (status/state, progress as 0-1 or 0-100, step/total_steps …).</summary>
        Public Shared Function ReadJob(raw As JsonNode) As DiffusionJobView
            Dim v As New DiffusionJobView()
            Dim o = TryCast(raw, JsonObject)
            If o Is Nothing Then
                v.Status = "running"
                Return v
            End If
            Dim word = If(If(o("status"), o("state"))?.ToString(), "").Trim().ToLowerInvariant()
            v.Status = If(word.Length = 0, "running",
                       If({"queued", "pending", "waiting", "submitted"}.Contains(word), "queued",
                       If({"running", "processing", "started", "generating", "working", "in_progress", "active"}.Contains(word), "running",
                       If({"done", "completed", "complete", "finished", "succeeded", "success", "ok"}.Contains(word), "done",
                       If({"error", "failed", "failure", "errored"}.Contains(word), "failed",
                       If({"cancelled", "canceled", "aborted", "stopped"}.Contains(word), "cancelled", word))))))
            ' Progress may be a number, a percent, or an object of its own ({"step": 3, "total": 25}, {"current": …}); the
            ' fields may also sit in a "progress"/"state" object instead of at the top.
            Dim nested = If(TryCast(o("progress"), JsonObject), TryCast(o("state"), JsonObject))
            Dim p = Num(If(TypeOf o("progress") Is JsonObject, Nothing, o("progress")))
            If Not p.HasValue Then p = Num(FieldOf(o, nested, "fraction", "percent", "percentage", "pct", "progress"))
            If p.HasValue Then v.Progress = Math.Clamp(If(p.Value > 1.0001, p.Value / 100, p.Value), 0, 1)
            v.Step = Int(FieldOf(o, nested, "step", "current_step", "steps_done", "current", "step_index", "i"))
            v.TotalSteps = Int(FieldOf(o, nested, "total_steps", "steps_total", "total", "num_inference_steps", "max_steps", "n_steps", "steps"))
            If Not v.Progress.HasValue AndAlso v.Step.HasValue AndAlso v.TotalSteps.HasValue AndAlso v.TotalSteps.Value > 0 Then
                v.Progress = Math.Clamp(v.Step.Value / CDbl(v.TotalSteps.Value), 0, 1)
            End If
            v.ImageIndex = Int(FieldOf(o, nested, "image_index", "current_image", "image"))
            v.QueuePosition = Int(If(o("queue_position"), If(o("position"), o("ahead"))))
            Dim err = If(o("error"), If(o("detail"), o("message")))
            If err IsNot Nothing AndAlso (v.Status = "failed" OrElse o("error") IsNot Nothing) Then v.Error = TextUtil.Truncate(err.ToString(), 1000)
            Dim elapsedMs = Num(o("elapsed_ms"))
            Dim elapsed = Num(If(o("elapsed"), If(o("elapsed_seconds"), o("duration"))))
            If elapsedMs.HasValue Then
                v.ElapsedMs = CLng(elapsedMs.Value)
            ElseIf elapsed.HasValue Then
                v.ElapsedMs = CLng(elapsed.Value * 1000)
            End If
            For Each s In If(TryCast(o("seeds"), JsonArray), New JsonArray())
                Dim n = Num(s)
                If n.HasValue Then v.Seeds.Add(CLng(n.Value))
            Next
            If v.Seeds.Count = 0 Then
                Dim one = Num(o("seed"))
                If one.HasValue Then v.Seeds.Add(CLng(one.Value))
            End If
            For Each s In If(TryCast(o("infos"), JsonArray), New JsonArray())
                If s IsNot Nothing Then v.Infos.Add(s.ToString())
            Next
            Dim images = o("images")
            Dim list = TryCast(images, JsonArray)
            If list IsNot Nothing Then
                For Each s In list
                    Dim t = If(s Is Nothing, Nothing, If(TypeOf s Is JsonObject, If(s("image"), s("data"))?.ToString(), s.ToString()))
                    If t IsNot Nothing AndAlso t.Length > 200 Then v.InlineImages.Add(t)
                Next
                v.ImagesReady = list.Count
            Else
                v.ImagesReady = Int(If(images, If(o("images_done"), If(o("image_count"), o("count")))))
            End If
            Return v
        End Function

        Private Shared Function Num(n As JsonNode) As Double?
            If n Is Nothing Then Return Nothing
            Dim d As Double
            If Double.TryParse(n.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
            Return Nothing
        End Function

        Private Shared Function Int(n As JsonNode) As Integer?
            Dim d = Num(n)
            Return If(d.HasValue AndAlso d.Value >= 0 AndAlso d.Value < Integer.MaxValue, CType(CInt(Math.Floor(d.Value)), Integer?), Nothing)
        End Function

        ''' <summary>Base64 picture data (a data: URL prefix is dropped).</summary>
        Public Shared Function DecodeImage(b64 As String) As Byte()
            Dim s = If(b64, "").Trim()
            Dim comma = s.IndexOf(","c)
            If s.StartsWith("data:", StringComparison.OrdinalIgnoreCase) AndAlso comma > 0 Then s = s.Substring(comma + 1)
            Try
                Return Convert.FromBase64String(s)
            Catch ex As FormatException
                Return Nothing
            End Try
        End Function

        ''' <summary>A PNG's size (from its header), or Nothing when it is not a PNG.</summary>
        Public Shared Function PngSize(png As Byte()) As (Width As Integer, Height As Integer)?
            If png Is Nothing OrElse png.Length < 24 OrElse png(0) <> &H89 OrElse png(1) <> &H50 Then Return Nothing
            Dim be = Function(at As Integer) (CInt(png(at)) << 24) Or (CInt(png(at + 1)) << 16) Or (CInt(png(at + 2)) << 8) Or CInt(png(at + 3))
            Return (be(16), be(20))
        End Function

        ''' <summary>The PNG's A1111-style "parameters" text (tEXt, zTXt or iTXt chunk), or Nothing.</summary>
        Public Shared Function PngParameters(png As Byte()) As String
            If png Is Nothing OrElse png.Length < 16 OrElse png(0) <> &H89 OrElse png(1) <> &H50 Then Return Nothing
            Dim at = 8
            While at + 12 <= png.Length
                Dim len = (CLng(png(at)) << 24) Or (CLng(png(at + 1)) << 16) Or (CLng(png(at + 2)) << 8) Or CLng(png(at + 3))
                Dim type = Encoding.ASCII.GetString(png, at + 4, 4)
                Dim start = at + 8
                If len < 0 OrElse start + len > png.Length Then Exit While
                Dim data = png.Skip(start).Take(CInt(len)).ToArray()
                Dim nul = Array.IndexOf(data, CByte(0))
                If nul > 0 AndAlso Encoding.Latin1.GetString(data, 0, nul) = "parameters" Then
                    Try
                        Select Case type
                            Case "tEXt"
                                Return Encoding.Latin1.GetString(data, nul + 1, data.Length - nul - 1)
                            Case "zTXt"
                                Return Inflate(data.Skip(nul + 2).ToArray())
                            Case "iTXt"
                                ' keyword \0 compression-flag method language \0 translated \0 text
                                Dim flag = data(nul + 1)
                                Dim p = nul + 3
                                For i = 1 To 2
                                    Dim z = Array.IndexOf(data, CByte(0), p)
                                    If z < 0 Then Return Nothing
                                    p = z + 1
                                Next
                                Dim rest = data.Skip(p).ToArray()
                                Return If(flag = 1, Inflate(rest), Encoding.UTF8.GetString(rest))
                        End Select
                    Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is ArgumentException
                        Return Nothing
                    End Try
                End If
                If type = "IEND" Then Exit While
                at = CInt(start + len + 4)
            End While
            Return Nothing
        End Function

        Private Shared Function Inflate(zlib As Byte()) As String
            Using src As New MemoryStream(zlib), z As New ZLibStream(src, CompressionMode.Decompress), dst As New MemoryStream()
                z.CopyTo(dst)
                Return Encoding.UTF8.GetString(dst.ToArray())
            End Using
        End Function

        ''' <summary>The seed in an A1111 "parameters" text ("…, Seed: 1234, …").</summary>
        Public Shared Function SeedOf(info As String) As Long?
            If String.IsNullOrEmpty(info) Then Return Nothing
            Dim m = Regex.Match(info, "\bSeed:\s*(\d+)")
            Dim v As Long
            Return If(m.Success AndAlso Long.TryParse(m.Groups(1).Value, v), CType(v, Long?), Nothing)
        End Function
    End Class

End Namespace
