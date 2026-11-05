Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: pictures from the Stable Diffusion 1.5 server (StableDiffusion), the Images tab. Jobs are queued there and
    ' followed here (DiffusionJobs), so their pictures reach the gallery and are paid for even when the page was closed.
    ' The server's key never leaves here.
    Partial Friend NotInheritable Class AdminApi

        Private Shared ReadOnly GalleryRef As New Regex("gallery:(sdimg_[A-Za-z0-9_-]+)", RegexOptions.Compiled)

        Private Sub RegisterImages(r As Router)
            r.Map("GET", "/api/images", AuthLevel.Read, AddressOf ImagesInfo)
            r.Map("GET", "/api/images/queue", AuthLevel.Read, AddressOf ImagesQueue)
            r.Map("GET", "/api/images/options", AuthLevel.Read, AddressOf ImagesOptions)
            r.Map("GET", "/api/images/health", AuthLevel.Read, AddressOf ImagesHealth)
            r.Map("POST", "/api/images/jobs", AuthLevel.SubmitTask, AddressOf ImagesJobStart)
            r.Map("GET", "/api/images/jobs", AuthLevel.Read, AddressOf ImagesJobs)
            r.Map("GET", "/api/images/jobs/{jobId}", AuthLevel.Read, AddressOf ImagesJob)
            r.Map("GET", "/api/images/jobs/{jobId}/preview", AuthLevel.Read, AddressOf ImagesJobPreview)
            r.Map("DELETE", "/api/images/jobs/{jobId}", AuthLevel.SubmitTask, AddressOf ImagesJobCancel)
            r.Map("POST", "/api/images/preprocess", AuthLevel.SubmitTask, AddressOf ImagesPreprocess)
            r.Map("GET", "/api/images/gallery", AuthLevel.Read, AddressOf ImagesGallery)
            r.Map("DELETE", "/api/images/gallery", AuthLevel.SubmitTask, AddressOf ImagesGalleryClear)
            r.Map("GET", "/api/images/gallery/{imageId}", AuthLevel.Read, AddressOf ImagesGalleryItem)
            r.Map("GET", "/api/images/gallery/{imageId}/file", AuthLevel.Read, AddressOf ImagesGalleryFile)
            r.Map("PATCH", "/api/images/gallery/{imageId}", AuthLevel.SubmitTask, AddressOf ImagesGalleryPatch)
            r.Map("DELETE", "/api/images/gallery/{imageId}", AuthLevel.SubmitTask, AddressOf ImagesGalleryDelete)
            r.Map("PUT", "/api/images/key", AuthLevel.Admin, AddressOf ImagesKeyPut)
            r.Map("GET", "/api/images/pricing", AuthLevel.Read, AddressOf ImagesPricing)
            r.Map("PUT", "/api/images/pricing", AuthLevel.Admin, AddressOf ImagesPricingPut)
        End Sub

        Private Sub RequireDiffusion()
            If _h.Diffusion Is Nothing OrElse Not _h.Diffusion.Configured Then Throw New ApiException(409, "SD_NOT_SET_UP", "No Stable Diffusion server is set up (StableDiffusion.BaseUrl in the configuration).")
        End Sub

        Private Shared Function DiffusionError(ex As DiffusionException) As ApiException
            Dim code = If(ex.Status = 400, "SD_BAD_REQUEST", If(ex.Status = 404, "SD_NOT_FOUND", If(ex.Status = 409, "SD_NOT_SET_UP", If(ex.Status = 503, "SD_BUSY", If(ex.Status = 504, "SD_TIMEOUT", "SD_FAILED")))))
            Return New ApiException(ex.Status, code, ex.Message)
        End Function

        Private Shared Function SdPriceDto(p As SdPrice) As Object
            Return New With {.perImage = p.PerImage, .perGpuMinute = p.PerGpuMinute, .source = p.Source, .effectiveFromUtc = p.EffectiveFromUtc}
        End Function

        Private Function SdPriceAsync() As Task(Of SdPrice)
            Return _h.DiffusionStore.PricingAsync(_h.Options.StableDiffusion)
        End Function

        Private Shared Function SdImageDto(i As SdImage) As Object
            Return New With {.id = i.Id, .jobId = i.JobId, .idx = i.Idx, .prompt = i.Prompt, .negativePrompt = i.NegativePrompt, .params = i.Params, .info = i.Info, .seed = i.Seed,
                             .width = i.Width, .height = i.Height, .model = i.Model, .mode = i.Mode, .bytes = i.Bytes, .starred = i.Starred, .createdUtc = i.CreatedUtc}
        End Function

        ''' <summary>Everything the tab needs at once: whether the server works, its options and health, the prices and the account.</summary>
        Private Async Function ImagesInfo(rc As RequestContext) As Task
            Dim sd = _h.Diffusion
            Dim opt = _h.Options.StableDiffusion
            Dim configured = sd IsNot Nothing AndAlso sd.Configured
            Dim key = If(configured, Await sd.KeyAsync().ConfigureAwait(False), Nothing)
            Dim options As JsonNode = Nothing, health As JsonNode = Nothing
            Dim problem As String = Nothing
            If configured AndAlso key IsNot Nothing Then
                Dim optionsTask = sd.OptionsAsync(rc.QueryString("refresh") = "1", rc.CancellationToken)
                Dim healthTask = sd.HealthAsync(rc.CancellationToken)
                Try
                    options = Await optionsTask.ConfigureAwait(False)
                Catch ex As DiffusionException
                    problem = ex.Message
                End Try
                Try
                    health = Await healthTask.ConfigureAwait(False)
                Catch ex As DiffusionException
                    problem = If(problem, ex.Message)
                End Try
            ElseIf configured Then
                problem = "The Stable Diffusion server's API key is not set" & If(rc.User.IsAdmin, ": set it under Settings.", ": an administrator sets it on the Images tab.")
            Else
                problem = "No Stable Diffusion server is set up (StableDiffusion.BaseUrl in the configuration)."
            End If
            Dim acc = If(rc.User.ViaSession OrElse rc.User.ViaApiKey, Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False), Nothing)
            Dim surcharged = acc IsNot Nothing AndAlso acc.Charged AndAlso Await _h.Accounts.HasSurchargeAsync(rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .available = configured, .keySet = key IsNot Nothing, .ready = options IsNot Nothing, .problem = problem, .isAdmin = rc.User.IsAdmin,
                .baseUrl = If(rc.User.IsAdmin AndAlso configured, sd.BaseUrl, Nothing), .keyInConfig = rc.User.IsAdmin AndAlso configured AndAlso sd.KeyInConfig,
                .keySecret = If(rc.User.IsAdmin, opt.ApiKeySecret, Nothing),
                .options = options, .health = health,
                .known = New With {.controlnets = DiffusionService.ControlNetModels, .preprocessors = DiffusionService.Preprocessors, .upscalers = DiffusionService.Upscalers},
                .defaults = New With {.maxImages = opt.MaxImagesPerUser, .maxInputImageMb = opt.MaxInputImageMb, .maxActiveJobs = opt.MaxActiveJobsPerUser, .jobTimeoutSeconds = opt.JobTimeoutSeconds},
                .pricing = SdPriceDto(Await SdPriceAsync().ConfigureAwait(False)),
                .account = New With {.charged = acc IsNot Nothing AndAlso acc.Charged, .balanceMicros = If(acc?.BalanceMicros, 0L),
                                     .surchargePercent = If(surcharged, Global.Agentic.Economy.AccountService.SurchargePercent, 0)},
                .canSetPrices = rc.User.IsAdmin OrElse rc.User.HasPermission("finance.manage"),
                .queue = If(configured, sd.Queue.StateFor(rc.User.Name), Nothing)}).ConfigureAwait(False)
        End Function

        ''' <summary>The server's line: its slot, what it is working on, what waits, and where the caller's own jobs are.</summary>
        Private Async Function ImagesQueue(rc As RequestContext) As Task
            RequireDiffusion()
            Await rc.OkAsync(_h.Diffusion.Queue.StateFor(rc.User.Name)).ConfigureAwait(False)
        End Function

        Private Async Function ImagesOptions(rc As RequestContext) As Task
            RequireDiffusion()
            Try
                Await rc.OkAsync(Await _h.Diffusion.OptionsAsync(rc.QueryString("refresh") = "1", rc.CancellationToken).ConfigureAwait(False)).ConfigureAwait(False)
            Catch ex As DiffusionException
                Throw DiffusionError(ex)
            End Try
        End Function

        Private Async Function ImagesHealth(rc As RequestContext) As Task
            RequireDiffusion()
            Try
                Await rc.OkAsync(Await _h.Diffusion.HealthAsync(rc.CancellationToken).ConfigureAwait(False)).ConfigureAwait(False)
            Catch ex As DiffusionException
                Throw DiffusionError(ex)
            End Try
        End Function

        ''' <summary>A request with pictures in it: its own size limit (several pictures as base64), parsed as JSON.</summary>
        Private Async Function ReadImageRequestAsync(rc As RequestContext) As Task(Of JsonElement)
            Dim limit = Math.Max(8L, CLng(_h.Options.StableDiffusion.MaxInputImageMb) * 4L) * 1024L * 1024L
            Dim data = Await rc.ReadBytesAsync(limit).ConfigureAwait(False)
            Dim el As JsonElement
            If data.Length = 0 OrElse Not JsonUtil.TryParseElement(Text.Encoding.UTF8.GetString(data), el) OrElse el.ValueKind <> JsonValueKind.Object Then
                Throw ApiException.BadRequest("Request body must be a JSON object.", "INVALID_JSON")
            End If
            Return el
        End Function

        ''' <summary>
        ''' Turns the pictures of a request into what the server takes: "gallery:{id}" (a picture of this user's gallery) becomes
        ''' its base64; base64 (a data: URL is fine) is checked to decode and to be within MaxInputImageMb.
        ''' </summary>
        Private Async Function ImageResolverAsync(rc As RequestContext, request As JsonElement) As Task(Of Func(Of String, String))
            Dim found As New Dictionary(Of String, String)(StringComparer.Ordinal)
            For Each m As Match In GalleryRef.Matches(request.GetRawText())
                Dim id = m.Groups(1).Value
                If found.ContainsKey(id) Then Continue For
                Dim img = Await _h.DiffusionStore.ImageAsync(rc.User.Name, id).ConfigureAwait(False)
                Dim png = If(img Is Nothing, Nothing, Await _h.DiffusionStore.ReadImageAsync(id).ConfigureAwait(False))
                If png Is Nothing Then Throw ApiException.NotFound("IMAGE_NOT_FOUND", $"The gallery picture {id} is gone.")
                found(id) = Convert.ToBase64String(png)
            Next
            Dim maxBytes = CLng(Math.Max(1, _h.Options.StableDiffusion.MaxInputImageMb)) * 1024L * 1024L
            Return Function(value As String) As String
                       Dim v = value.Trim()
                       Dim g = GalleryRef.Match(v)
                       If g.Success AndAlso g.Index = 0 Then Return found(g.Groups(1).Value)
                       Dim bytes = DiffusionService.DecodeImage(v)
                       If bytes Is Nothing OrElse bytes.Length < 16 Then Throw New DiffusionException(400, "A picture is not valid base64 (or a gallery:{id} reference).")
                       If bytes.LongLength > maxBytes Then Throw New DiffusionException(400, $"A picture is larger than {_h.Options.StableDiffusion.MaxInputImageMb} MB.")
                       Return v
                   End Function
        End Function

        ''' <summary>Body: the generation settings (GenerateRequest). Queues the job and answers 202 with it; GET /api/images/jobs/{id} follows it.</summary>
        Private Async Function ImagesJobStart(rc As RequestContext) As Task
            RequireDiffusion()
            Dim b = Await ReadImageRequestAsync(rc).ConfigureAwait(False)
            Dim opt = _h.Options.StableDiffusion
            If _h.DiffusionJobs.ActiveCount(rc.User.Name) >= Math.Max(1, opt.MaxActiveJobsPerUser) Then
                Throw New ApiException(429, "TOO_MANY_JOBS", $"You already have {opt.MaxActiveJobsPerUser} pictures jobs queued or running: wait for one to end.")
            End If
            Dim body As JsonObject
            Try
                body = DiffusionService.BuildRequest(b, Await ImageResolverAsync(rc, b).ConfigureAwait(False))
            Catch ex As DiffusionException
                Throw DiffusionError(ex)
            End Try
            Dim price = Await SdPriceAsync().ConfigureAwait(False)
            Dim n = 1
            Dim nv = TryCast(body("num_images"), JsonValue)
            If nv IsNot Nothing Then nv.TryGetValue(n)
            Await RequireBalanceAsync(rc, price.Micros(n, DiffusionService.EstimateGpuMs(body)), If(n = 1, "this picture", $"these {n} pictures")).ConfigureAwait(False)
            Dim job As DiffusionJob
            Try
                job = Await _h.DiffusionJobs.StartJobAsync(rc.User.Name, body, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As DiffusionException
                Throw DiffusionError(ex)
            End Try
            Await rc.WriteJsonAsync(202, job).ConfigureAwait(False)
        End Function

        Private Async Function ImagesJobs(rc As RequestContext) As Task
            Await rc.OkAsync(Await _h.DiffusionJobs.ListAsync(rc.User.Name, rc.QueryInt("limit", 30, 1, 200)).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function JobOrThrowAsync(rc As RequestContext) As Task(Of DiffusionJob)
            Dim job = Await _h.DiffusionJobs.GetAsync(rc.User.Name, rc.Route("jobId")).ConfigureAwait(False)
            If job Is Nothing Then Throw ApiException.NotFound("JOB_NOT_FOUND", "That job was not found.")
            Return job
        End Function

        Private Async Function ImagesJob(rc As RequestContext) As Task
            Await rc.OkAsync(Await JobOrThrowAsync(rc).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>The rough live preview of the picture being made (204 when there is none yet).</summary>
        Private Async Function ImagesJobPreview(rc As RequestContext) As Task
            RequireDiffusion()
            Dim job = Await JobOrThrowAsync(rc).ConfigureAwait(False)
            Dim headers As New Dictionary(Of String, String) From {{"Cache-Control", "no-store"}}
            If job.Finished OrElse job.RemoteId Is Nothing Then
                Await rc.WriteBytesAsync(204, "image/png", Array.Empty(Of Byte)(), headers).ConfigureAwait(False)
                Return
            End If
            Dim p As (Data As Byte(), ContentType As String)
            Try
                p = Await _h.Diffusion.PreviewAsync(job.RemoteId, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As DiffusionException
                p = (Nothing, Nothing)
            End Try
            If p.Data Is Nothing Then
                Await rc.WriteBytesAsync(204, "image/png", Array.Empty(Of Byte)(), headers).ConfigureAwait(False)
                Return
            End If
            Await rc.WriteBytesAsync(200, If(p.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase), p.ContentType, "image/png"), p.Data, headers).ConfigureAwait(False)
        End Function

        Private Async Function ImagesJobCancel(rc As RequestContext) As Task
            RequireDiffusion()
            Dim job = Await JobOrThrowAsync(rc).ConfigureAwait(False)
            Try
                job = Await _h.DiffusionJobs.CancelAsync(rc.User.Name, job.Id, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As DiffusionException
                Throw DiffusionError(ex)
            End Try
            Await rc.OkAsync(job).ConfigureAwait(False)
        End Function

        ''' <summary>Body {preprocessor, image (base64 or gallery:{id}), params}: the control map as a PNG.</summary>
        Private Async Function ImagesPreprocess(rc As RequestContext) As Task
            RequireDiffusion()
            Dim b = Await ReadImageRequestAsync(rc).ConfigureAwait(False)
            Dim pre = If(JsonUtil.GetString(b, "preprocessor"), "").Trim()
            If pre.Length = 0 OrElse pre.Length > 100 Then Throw ApiException.BadRequest("Choose a preprocessor.")
            Dim image = JsonUtil.GetString(b, "image")
            If String.IsNullOrWhiteSpace(image) Then Throw ApiException.BadRequest("Add the picture to preprocess.")
            Dim body As New JsonObject From {{"preprocessor", pre}}
            Dim p As JsonElement
            If b.TryGetProperty("params", p) AndAlso p.ValueKind <> JsonValueKind.Null Then
                If p.ValueKind <> JsonValueKind.Object Then Throw ApiException.BadRequest("params is an object, like {""low_threshold"": 50}.")
                body("params") = JsonNode.Parse(p.GetRawText())
            End If
            Dim map As (Data As Byte(), ContentType As String)
            Try
                body("image") = (Await ImageResolverAsync(rc, b).ConfigureAwait(False))(image)
                map = Await _h.Diffusion.PreprocessAsync(body, rc.CancellationToken, rc.User.Name).ConfigureAwait(False)
            Catch ex As DiffusionException
                Throw DiffusionError(ex)
            End Try
            Await rc.WriteBytesAsync(200, If(map.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase), map.ContentType, "image/png"), map.Data,
                                     New Dictionary(Of String, String) From {{"Cache-Control", "no-store"}}).ConfigureAwait(False)
        End Function

        ''' <summary>?q= (prompt, model or seed), starred=1, job=, limit=, before= (paging by time).</summary>
        Private Async Function ImagesGallery(rc As RequestContext) As Task
            Dim list = Await _h.DiffusionStore.ImagesAsync(rc.User.Name, rc.QueryString("q"), rc.QueryString("starred") = "1", rc.QueryString("job"),
                                                           rc.QueryInt("limit", 60, 1, 500), rc.QueryDate("before")).ConfigureAwait(False)
            Await rc.OkAsync(list.Select(AddressOf SdImageDto).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function ImageOrThrowAsync(rc As RequestContext) As Task(Of SdImage)
            Dim img = Await _h.DiffusionStore.ImageAsync(rc.User.Name, rc.Route("imageId")).ConfigureAwait(False)
            If img Is Nothing Then Throw ApiException.NotFound("IMAGE_NOT_FOUND", "That picture is gone.")
            Return img
        End Function

        Private Async Function ImagesGalleryItem(rc As RequestContext) As Task
            Await rc.OkAsync(SdImageDto(Await ImageOrThrowAsync(rc).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>The PNG (with its "parameters" text); ?download=1 saves it as a file.</summary>
        Private Async Function ImagesGalleryFile(rc As RequestContext) As Task
            Dim img = Await ImageOrThrowAsync(rc).ConfigureAwait(False)
            Dim png = Await _h.DiffusionStore.ReadImageAsync(img.Id).ConfigureAwait(False)
            If png Is Nothing Then Throw ApiException.NotFound("IMAGE_NOT_FOUND", "That picture's file is gone.")
            Dim headers As New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}}
            If rc.QueryString("download") = "1" Then headers("Content-Disposition") = $"attachment; filename=""sd-{img.Seed?.ToString(Globalization.CultureInfo.InvariantCulture)}-{img.Id}.png"""
            Await rc.WriteBytesAsync(200, "image/png", png, headers).ConfigureAwait(False)
        End Function

        ''' <summary>Body {starred}: starred pictures are never dropped to make room.</summary>
        Private Async Function ImagesGalleryPatch(rc As RequestContext) As Task
            Dim img = Await ImageOrThrowAsync(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Await _h.DiffusionStore.StarAsync(rc.User.Name, img.Id, JsonUtil.GetBool(b, "starred", img.Starred)).ConfigureAwait(False)
            Await rc.OkAsync(SdImageDto(Await ImageOrThrowAsync(rc).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        Private Async Function ImagesGalleryDelete(rc As RequestContext) As Task
            If Not Await _h.DiffusionStore.DeleteImageAsync(rc.User.Name, rc.Route("imageId")).ConfigureAwait(False) Then Throw ApiException.NotFound("IMAGE_NOT_FOUND", "That picture is gone.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Deletes the user's pictures that are not starred.</summary>
        Private Async Function ImagesGalleryClear(rc As RequestContext) As Task
            Await rc.OkAsync(New With {.deleted = Await _h.DiffusionStore.ClearAsync(rc.User.Name).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {apiKey}: the Stable Diffusion server's key, kept in the vault (empty removes it). Answers whether the server takes it.</summary>
        Private Async Function ImagesKeyPut(rc As RequestContext) As Task
            RequireDiffusion()
            If _h.Secrets Is Nothing Then Throw New ApiException(409, "NO_VAULT", "The vault is not available.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim key = If(JsonUtil.GetString(b, "apiKey"), "").Trim()
            Dim secretName = _h.Options.StableDiffusion.ApiKeySecret
            If String.IsNullOrWhiteSpace(secretName) Then Throw New ApiException(409, "NO_SECRET_NAME", "StableDiffusion.ApiKeySecret is empty: put the key in StableDiffusion.ApiKey instead.")
            If key.Length = 0 Then
                Await _h.Secrets.DeleteAsync(secretName).ConfigureAwait(False)
            Else
                Dim host = _h.Diffusion.Host
                Dim hosts = If(host Is Nothing OrElse host.Contains(":"c), New List(Of String), New List(Of String) From {host})
                Try
                    Await _h.Secrets.SetAsync(secretName, key, "API key of the Stable Diffusion server (the Images tab sets it; no agent may use it)", New List(Of String), rc.User.Name, hosts).ConfigureAwait(False)
                Catch ex As ArgumentException
                    Throw ApiException.BadRequest(ex.Message)
                End Try
            End If
            _h.Diffusion.ForgetKey()
            Await AuditAsync(rc, If(key.Length = 0, "remove-sd-key", "set-sd-key"), "secret", secretName).ConfigureAwait(False)
            Dim problem As String = Nothing
            If key.Length > 0 Then
                Try
                    Await _h.Diffusion.OptionsAsync(True, rc.CancellationToken).ConfigureAwait(False)
                Catch ex As DiffusionException
                    problem = ex.Message
                End Try
            End If
            Await rc.OkAsync(New With {.keySet = key.Length > 0, .ok = key.Length > 0 AndAlso problem Is Nothing, .problem = problem, .keyInConfig = _h.Diffusion.KeyInConfig}).ConfigureAwait(False)
        End Function

        Private Async Function ImagesPricing(rc As RequestContext) As Task
            Await rc.OkAsync(SdPriceDto(Await SdPriceAsync().ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>Body {perImage, perGpuMinute} in USD (null = free): what pictures cost User accounts from now on.</summary>
        Private Async Function ImagesPricingPut(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim perImage = ParseMoney(b, "perImage", False), perMinute = ParseMoney(b, "perGpuMinute", False)
            If perImage.GetValueOrDefault() > 1000D OrElse perMinute.GetValueOrDefault() > 1000D Then
                Throw ApiException.BadRequest("That price looks like a typo: at most $1,000 per picture and $1,000 per GPU minute.")
            End If
            Try
                Await _h.DiffusionStore.SetPricingAsync(perImage, perMinute, rc.User.Name).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "set-sd-pricing", "pricing", "stable-diffusion", JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await rc.OkAsync(SdPriceDto(Await SdPriceAsync().ConfigureAwait(False))).ConfigureAwait(False)
        End Function
    End Class

End Namespace
