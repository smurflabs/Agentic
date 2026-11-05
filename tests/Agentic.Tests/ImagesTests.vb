Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Http
Imports Agentic.Work
Imports Xunit

''' <summary>A stand-in for the Stable Diffusion 1.5 server: its key, /options, /health, jobs (queued, running, done), pictures, preview, cancel and /preprocess.</summary>
Friend NotInheritable Class FakeSd
    Implements IDisposable
    Private ReadOnly _listener As New HttpListener()
    Private ReadOnly _cts As New CancellationTokenSource()
    Private ReadOnly _state As New Concurrent.ConcurrentDictionary(Of String, (Body As JsonNode, Polls As Integer, State As String))
    Private _next As Integer
    Public ReadOnly Property Port As Integer
    Public Property Key As String = "sd-key"
    ''' <summary>The bodies of POST /jobs and /preprocess.</summary>
    Public ReadOnly Property Jobs As New Concurrent.ConcurrentQueue(Of JsonNode)
    Public ReadOnly Property Preprocessed As New Concurrent.ConcurrentQueue(Of JsonNode)
    ''' <summary>Jobs stay queued (to test limits and cancelling).</summary>
    Public Property Hold As Boolean
    ''' <summary>The next job fails.</summary>
    Public Property FailNext As Boolean
    ''' <summary>Pictures are numbered from 1 instead of 0.</summary>
    Public Property FromOne As Boolean

    Public Sub New()
        Port = HttpApiTests.FreePort()
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/")
        _listener.Start()
        Task.Run(AddressOf LoopAsync)
    End Sub

    Public ReadOnly Property BaseUrl As String
        Get
            Return $"http://127.0.0.1:{Port}"
        End Get
    End Property

    Private Shared ReadOnly CrcTable As UInteger() = Enumerable.Range(0, 256).Select(Function(n)
                                                                                         Dim c = CUInt(n)
                                                                                         For k = 0 To 7
                                                                                             c = If((c And 1UI) <> 0, &HEDB88320UI Xor (c >> 1), c >> 1)
                                                                                         Next
                                                                                         Return c
                                                                                     End Function).ToArray()

    Private Shared Function Chunk(type As String, data As Byte()) As Byte()
        Dim body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray()
        Dim crc = &HFFFFFFFFUI
        For Each b In body
            crc = CrcTable(CInt((crc Xor b) And &HFFUI)) Xor (crc >> 8)
        Next
        crc = crc Xor &HFFFFFFFFUI
        Dim be = Function(v As UInteger) New Byte() {CByte(v >> 24), CByte((v >> 16) And &HFFUI), CByte((v >> 8) And &HFFUI), CByte(v And &HFFUI)}
        Return be(CUInt(data.Length)).Concat(body).Concat(be(crc)).ToArray()
    End Function

    ''' <summary>A tiny real PNG (8×8 grey) with an A1111 "parameters" text chunk.</summary>
    Public Shared Function Png(parameters As String, Optional size As Integer = 8) As Byte()
        Dim ihdr = New Byte() {0, 0, 0, CByte(size), 0, 0, 0, CByte(size), 8, 0, 0, 0, 0}
        Dim raw As New List(Of Byte)
        For y = 0 To size - 1
            raw.Add(0)
            raw.AddRange(Enumerable.Repeat(CByte(128), size))
        Next
        Dim idat As Byte()
        Using ms As New IO.MemoryStream()
            Using z As New IO.Compression.ZLibStream(ms, IO.Compression.CompressionLevel.Optimal, leaveOpen:=True)
                z.Write(raw.ToArray(), 0, raw.Count)
            End Using
            idat = ms.ToArray()
        End Using
        Dim parts As New List(Of Byte)(New Byte() {&H89, &H50, &H4E, &H47, &HD, &HA, &H1A, &HA})
        parts.AddRange(Chunk("IHDR", ihdr))
        If parameters IsNot Nothing Then parts.AddRange(Chunk("tEXt", Encoding.Latin1.GetBytes("parameters" & ChrW(0) & parameters)))
        parts.AddRange(Chunk("IDAT", idat))
        parts.AddRange(Chunk("IEND", Array.Empty(Of Byte)()))
        Return parts.ToArray()
    End Function

    Public Sub SetState(id As String, state As String)
        Dim j As (Body As JsonNode, Polls As Integer, State As String) = Nothing
        If _state.TryGetValue(id, j) Then _state(id) = (j.Body, j.Polls, state)
    End Sub

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

    Private Shared Async Function WriteAsync(res As HttpListenerResponse, status As Integer, type As String, data As Byte()) As Task
        res.StatusCode = status
        res.ContentType = type
        res.ContentLength64 = data.Length
        Await res.OutputStream.WriteAsync(data, 0, data.Length)
        res.Close()
    End Function

    Private Shared Function Json(res As HttpListenerResponse, status As Integer, body As String) As Task
        Return WriteAsync(res, status, "application/json", Encoding.UTF8.GetBytes(body))
    End Function

    Private Async Function HandleAsync(ctx As HttpListenerContext) As Task
        Dim req = ctx.Request, res = ctx.Response
        Dim path = req.Url.AbsolutePath
        If req.Headers("X-API-Key") <> Key Then
            Await Json(res, 401, "{""detail"":""Invalid API key""}")
            Return
        End If
        Dim body As String
        Using sr As New IO.StreamReader(req.InputStream)
            body = Await sr.ReadToEndAsync()
        End Using
        Dim parts = path.Trim("/"c).Split("/"c)
        If path = "/options" Then
            Await Json(res, 200, "{""models"":[""sd15"",""dreamshaper""],""vaes"":[""default"",""ft-mse""],""loras"":[""detail""],""embeddings"":[""easynegative""],""controlnets"":[""canny""],""preprocessors"":[""canny"",""depth""],""schedulers"":[""dpmpp_2m"",""euler_a""]}")
        ElseIf path = "/health" Then
            Await Json(res, 200, "{""gpu"":""GTX 1660 Ti"",""model"":""sd15"",""queue"":0,""vram_used_mb"":3000}")
        ElseIf path = "/jobs" AndAlso req.HttpMethod = "POST" Then
            Dim j = JsonNode.Parse(body)
            Jobs.Enqueue(j)
            Dim id = "job" & Interlocked.Increment(_next)
            _state(id) = (j, 0, If(FailNext, "fail", "queued"))
            FailNext = False
            Await Json(res, 202, $"{{""id"":""{id}"",""status"":""queued"",""position"":1}}")
        ElseIf parts.Length >= 2 AndAlso parts(0) = "jobs" Then
            Dim id = parts(1)
            Dim j As (Body As JsonNode, Polls As Integer, State As String) = Nothing
            If Not _state.TryGetValue(id, j) Then
                Await Json(res, 404, "{""detail"":""Unknown job""}")
                Return
            End If
            Dim n = If(j.Body("num_images") Is Nothing, 1, j.Body("num_images").GetValue(Of Integer)())
            Dim seed = If(j.Body("seed") Is Nothing, 1000L, j.Body("seed").GetValue(Of Long)())
            If parts.Length = 2 AndAlso req.HttpMethod = "DELETE" Then
                _state(id) = (j.Body, j.Polls, "cancelled")
                Await Json(res, 200, "{""cancelled"":true}")
            ElseIf parts.Length = 2 Then
                Dim polls = j.Polls + 1
                Dim state = j.State
                If state = "queued" AndAlso Not Hold AndAlso polls >= 2 Then state = "running"
                If state = "running" AndAlso polls >= 3 Then state = "done"
                _state(id) = (j.Body, polls, state)
                Select Case state
                    Case "queued" : Await Json(res, 200, $"{{""id"":""{id}"",""status"":""queued"",""position"":2}}")
                    Case "running" : Await Json(res, 200, $"{{""id"":""{id}"",""status"":""running"",""progress"":0.5,""step"":12,""total_steps"":25,""image_index"":1}}")
                    Case "fail" : Await Json(res, 200, $"{{""id"":""{id}"",""status"":""error"",""error"":""CUDA out of memory""}}")
                    Case "cancelled" : Await Json(res, 200, $"{{""id"":""{id}"",""status"":""cancelled""}}")
                    Case Else
                        Dim seeds = String.Join(",", Enumerable.Range(0, n).Select(Function(i) (seed + i).ToString()))
                        Await Json(res, 200, $"{{""id"":""{id}"",""status"":""done"",""progress"":1,""images"":{n},""seeds"":[{seeds}],""elapsed"":3.5}}")
                End Select
            ElseIf parts.Length = 3 AndAlso parts(2) = "preview" Then
                Await WriteAsync(res, 200, "image/png", Png(Nothing))
            ElseIf parts.Length = 4 AndAlso parts(2) = "images" Then
                Dim k = Integer.Parse(parts(3)) - If(FromOne, 1, 0)
                If k < 0 OrElse k >= n OrElse j.State <> "done" Then
                    Await Json(res, 404, "{""detail"":""No such image""}")
                Else
                    Await WriteAsync(res, 200, "image/png", Png($"{j.Body("prompt")}{vbLf}Steps: 25, Sampler: dpmpp_2m, Seed: {seed + k}, Size: 8x8"))
                End If
            Else
                Await Json(res, 404, "{""detail"":""Not Found""}")
            End If
        ElseIf path = "/preprocess" Then
            Preprocessed.Enqueue(JsonNode.Parse(body))
            Await WriteAsync(res, 200, "image/png", Png(Nothing))
        Else
            Await Json(res, 404, "{""detail"":""Not Found""}")
        End If
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _listener.Close()
    End Sub
End Class

''' <summary>
''' Pictures through the Stable Diffusion server: its key stays here, every setting of the spec reaches it checked, jobs are
''' followed here until their pictures are in the gallery and paid for (per picture and per GPU minute), gallery pictures
''' feed img2img, inpainting and ControlNet, and cancelled or failed jobs are not charged.
''' </summary>
Public Class ImagesTests

    Private Shared Async Function WaitJobAsync(user As (Client As HttpClient, Csrf As String), id As String, Optional status As String = Nothing) As Task(Of JsonNode)
        For i = 0 To 150
            Dim j = Await ChatFeatureTests.JsonOf(Await user.Client.GetAsync("/api/images/jobs/" & id))
            Dim st = j("status").ToString()
            If If(status Is Nothing, st = "done" OrElse st = "failed" OrElse st = "cancelled", st = status) Then Return j
            Await Task.Delay(100)
        Next
        Throw New TimeoutException("The job did not end.")
    End Function

    <Fact>
    Public Async Function PicturesAreMadeKeptAndPaidFor() As Task
        Using up As New FakeUpstream(), sd As New FakeSd()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.StableDiffusion.BaseUrl = sd.BaseUrl
                                                                o.StableDiffusion.PollMilliseconds = 100
                                                            End Sub)
            Using env.Host
                Dim h = env.Host.H
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim info = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images"))
                Assert.False(info("ready").GetValue(Of Boolean)())
                Assert.Contains("API key", info("problem").ToString())
                ' The administrator sets the key: the vault (no agent may use it), bound to the server's host.
                Assert.True((Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/images/key", New With {.apiKey = "sd-key"}, admin.Csrf)))("ok").GetValue(Of Boolean)())
                Dim secret = Await h.Secrets.GetInfoAsync("sd-api-key")
                Assert.Empty(secret.AgentIds)
                Assert.Equal({"127.0.0.1"}, secret.Hosts.ToArray())
                Dim infoText = Await admin.Client.GetStringAsync("/api/images")
                Assert.DoesNotContain("sd-key", infoText)
                info = JsonNode.Parse(infoText)
                Assert.True(info("ready").GetValue(Of Boolean)())
                Assert.Contains("dreamshaper", info("options")("models").AsArray().Select(Function(x) x.ToString()))
                Assert.Equal("GTX 1660 Ti", info("health")("gpu").ToString())
                Assert.Contains("openpose", info("known")("controlnets").AsArray().Select(Function(x) x.ToString()))

                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/images/pricing", New With {.perImage = 0.02, .perGpuMinute = 0.6}, admin.Csrf))
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users",
                    New With {.username = "painter", .password = "painter-pass-123", .role = "User", .permissions = PlatformApiTests.ChatUser.Concat({"images.sd"}).ToArray()}, admin.Csrf)).StatusCode)
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/painter/balance", New With {.amount = 5}, admin.Csrf)
                Dim painter = Await PlatformApiTests.LoginAsync(env.Port, "painter", "painter-pass-123")
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(painter.Client, "PUT", "/api/images/key", New With {.apiKey = "x"}, painter.Csrf)).StatusCode)

                ' Text to image with every setting: each reaches the server as the spec names it.
                Dim started = Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", New With {
                    .prompt = "a (cozy:1.2) cabin", .negative_prompt = "blurry", .model = "dreamshaper", .vae = "ft-mse", .scheduler = "euler_a", .width = 515, .height = 768,
                    .steps = 20, .guidance_scale = 7, .guidance_rescale = 0.2, .eta = 0.1, .clip_skip = 2, .seed = 42, .num_images = 2, .freeu = True,
                    .loras = {New With {.name = "detail", .weight = 0.8}}, .hires = New With {.scale = 1.5, .upscaler = "latent", .steps = 10, .denoise = 0.4}}, painter.Csrf)
                Assert.Equal(HttpStatusCode.Accepted, started.StatusCode)
                Dim jobId = JsonNode.Parse(Await started.Content.ReadAsStringAsync())("id").ToString()
                Dim sent = sd.Jobs.Last()
                Assert.Equal(512, sent("width").GetValue(Of Integer)())
                Assert.Equal(768, sent("height").GetValue(Of Integer)())
                For Each kv In {("model", "dreamshaper"), ("vae", "ft-mse"), ("scheduler", "euler_a"), ("negative_prompt", "blurry")}
                    Assert.Equal(kv.Item2, sent(kv.Item1).ToString())
                Next
                Assert.Equal(2, sent("clip_skip").GetValue(Of Integer)())
                Assert.Equal(0.2, sent("guidance_rescale").GetValue(Of Double)())
                Assert.True(sent("freeu").GetValue(Of Boolean)())
                Assert.Equal("detail", sent("loras")(0)("name").ToString())
                Assert.Equal("latent", sent("hires")("upscaler").ToString())
                Dim job = Await WaitJobAsync(painter, jobId)
                Assert.Equal("done", job("status").ToString())
                Assert.Equal(2, job("imageIds").AsArray().Count)

                ' Kept in the gallery with their seeds, size and PNG info.
                Dim gallery = (Await ChatFeatureTests.JsonOf(Await painter.Client.GetAsync("/api/images/gallery?job=" & jobId))).AsArray()
                Assert.Equal(2, gallery.Count)
                Assert.Equal({42L, 43L}, gallery.Select(Function(g) g("seed").GetValue(Of Long)()).OrderBy(Function(x) x).ToArray())
                Assert.All(gallery, Sub(g)
                                        Assert.Equal(8, g("width").GetValue(Of Integer)())
                                        Assert.Equal("txt2img", g("mode").ToString())
                                        Assert.Contains("Steps: 25", g("info").ToString())
                                        Assert.Equal("dreamshaper", g("model").ToString())
                                    End Sub)
                Dim first = gallery(0)("id").ToString()
                Dim png = Await (Await painter.Client.GetAsync($"/api/images/gallery/{first}/file")).Content.ReadAsByteArrayAsync()
                Assert.NotNull(DiffusionService.PngParameters(png))

                ' Paid per picture and per GPU minute: 2 × $0.02 + 3.5 s × $0.6 per minute = 40,000 + 35,000 micro-USD.
                Dim cost = Await h.Accounts.WithSurchargeAsync("painter", 75000)
                Assert.Equal(cost, job("costMicros").GetValue(Of Long)())
                Assert.Equal(5000000L - cost, (Await h.Accounts.GetAccountAsync("painter")).BalanceMicros)
                Dim ledger = Await h.Database.QueryAsync("SELECT EntryType, Model FROM AccountLedger WHERE Username='painter' AND EntryType<>'Credit'", Function(r) (r.GetString(0), r.GetString(1)))
                Assert.Equal(("Image", "dreamshaper"), ledger.Single())

                ' A gallery picture feeds img2img, inpainting and ControlNet (the server gets the picture itself).
                Dim mask = Convert.ToBase64String(FakeSd.Png(Nothing))
                Dim inpaint = await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", New With {
                    .prompt = "a red door", .init_image = "gallery:" & first, .strength = 0.5, .mask_image = "data:image/png;base64," & mask, .mask_blur = 8, .invert_mask = True,
                    .inpaint_padding = 32, .guess_mode = True,
                    .controlnets = {New With {.model = "canny", .preprocessor = "canny", .preprocessor_params = New With {.low_threshold = 50}, .scale = 0.8, .start = 0, .end = 0.9}}}, painter.Csrf)
                Assert.Equal(HttpStatusCode.Accepted, inpaint.StatusCode)
                sent = sd.Jobs.Last()
                Assert.Equal(Convert.ToBase64String(png), sent("init_image").ToString())
                Assert.StartsWith("data:image/png;base64,", sent("mask_image").ToString())
                Assert.Equal(50, sent("controlnets")(0)("preprocessor_params")("low_threshold").GetValue(Of Integer)())
                Assert.Null(sent("controlnets")(0)("image"))
                Assert.True(sent("invert_mask").GetValue(Of Boolean)())
                Dim inpaintJob = Await WaitJobAsync(painter, JsonNode.Parse(Await inpaint.Content.ReadAsStringAsync())("id").ToString())
                Dim inpainted = (Await ChatFeatureTests.JsonOf(Await painter.Client.GetAsync("/api/images/gallery?job=" & inpaintJob("id").ToString()))).AsArray().Single()
                Assert.Equal("inpaint", inpainted("mode").ToString())
                Assert.Equal(True, inpainted("params")("init_image").GetValue(Of Boolean)())
                Assert.Equal(True, inpainted("params")("mask_image").GetValue(Of Boolean)())

                ' Settings out of the spec's ranges are refused here.
                For Each bad In New Object() {
                    New With {.prompt = ""}, New With {.prompt = "x", .steps = 0}, New With {.prompt = "x", .width = 10}, New With {.prompt = "x", .guidance_scale = 31},
                    New With {.prompt = "x", .num_images = 17}, New With {.prompt = "x", .loras = {New With {.name = "a", .weight = 5}}},
                    New With {.prompt = "x", .init_image = "gallery:" & first, .strength = 0}, New With {.prompt = "x", .init_image = "gallery:" & first, .hires = New With {.scale = 2}},
                    New With {.prompt = "x", .mask_image = mask}, New With {.prompt = "x", .hires = New With {.upscaler = "bicubic"}},
                    New With {.prompt = "x", .controlnets = {New With {.model = "canny", .image = mask, .start = 0.8, .end = 0.2}}},
                    New With {.prompt = "x", .controlnets = {New With {.model = "canny"}}}, New With {.prompt = "x", .init_image = "not base64!"}}
                    Dim r = Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", bad, painter.Csrf)
                    Assert.True(r.StatusCode = HttpStatusCode.BadRequest, JsonSerializer.Serialize(bad) & " → " & r.StatusCode.ToString())
                Next
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", New With {.prompt = "x", .init_image = "gallery:sdimg_nope"}, painter.Csrf)).StatusCode)

                ' The preprocessor makes a control map from a gallery picture.
                Dim map = Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/preprocess", New With {.preprocessor = "canny", .image = "gallery:" & first, .params = New With {.low_threshold = 40}}, painter.Csrf)
                Assert.Equal("image/png", map.Content.Headers.ContentType.MediaType)
                Assert.Equal(40, sd.Preprocessed.Single()("params")("low_threshold").GetValue(Of Integer)())

                ' Star, delete, clear the unstarred.
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(painter.Client, "PATCH", "/api/images/gallery/" & first, New With {.starred = True}, painter.Csrf))
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(painter.Client, "DELETE", "/api/images/gallery", Nothing, painter.Csrf))
                Dim left = (Await ChatFeatureTests.JsonOf(Await painter.Client.GetAsync("/api/images/gallery"))).AsArray()
                Assert.Equal(first, left.Single()("id").ToString())
                Assert.True(left.Single()("starred").GetValue(Of Boolean)())

                ' A failed job is not charged; a cancelled one neither.
                Dim before = (Await h.Accounts.GetAccountAsync("painter")).BalanceMicros
                sd.FailNext = True
                Dim failedId = JsonNode.Parse(Await (Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", New With {.prompt = "x"}, painter.Csrf)).Content.ReadAsStringAsync())("id").ToString()
                Dim failed = Await WaitJobAsync(painter, failedId)
                Assert.Equal("failed", failed("status").ToString())
                Assert.Equal("CUDA out of memory", failed("error").ToString())
                sd.Hold = True
                Dim heldId = JsonNode.Parse(Await (Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", New With {.prompt = "y"}, painter.Csrf)).Content.ReadAsStringAsync())("id").ToString()
                Await WaitJobAsync(painter, heldId, "queued")
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(painter.Client, "DELETE", "/api/images/jobs/" & heldId, Nothing, painter.Csrf))
                Assert.Equal("cancelled", (Await WaitJobAsync(painter, heldId))("status").ToString())
                Assert.Equal(before, (Await h.Accounts.GetAccountAsync("painter")).BalanceMicros)

                ' A balance that does not cover the estimate is refused before anything is queued.
                Await h.Accounts.AdjustAsync("painter", 1000 - before, "test", "admin")
                Dim queued = sd.Jobs.Count
                Dim refused = Await PlatformApiTests.Send(painter.Client, "POST", "/api/images/jobs", New With {.prompt = "z", .num_images = 4}, painter.Csrf)
                Assert.Equal(CType(402, HttpStatusCode), refused.StatusCode)
                Assert.Equal(queued, sd.Jobs.Count)
                ' Another user sees none of this.
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/images/jobs/" & jobId)).StatusCode)
                Assert.Empty((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/gallery"))).AsArray())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function JobsAreFollowedAgainAfterARestartAndTheirLimitHolds() As Task
        Using up As New FakeUpstream(), sd As New FakeSd()
            sd.FromOne = True
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.StableDiffusion.BaseUrl = sd.BaseUrl
                                                                o.StableDiffusion.ApiKey = "sd-key"
                                                                o.StableDiffusion.PollMilliseconds = 100
                                                                o.StableDiffusion.MaxActiveJobsPerUser = 1
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                sd.Hold = True
                Dim first = JsonNode.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", "/api/images/jobs", New With {.prompt = "one", .num_images = 3}, admin.Csrf)).Content.ReadAsStringAsync())
                Dim second = Await PlatformApiTests.Send(admin.Client, "POST", "/api/images/jobs", New With {.prompt = "two"}, admin.Csrf)
                Assert.Equal(CType(429, HttpStatusCode), second.StatusCode)
                sd.Hold = False
                ' Pictures numbered from 1 are found too.
                Dim job = Await WaitJobAsync(admin, first("id").ToString())
                Assert.Equal("done", job("status").ToString())
                Assert.Equal(3, job("imageIds").AsArray().Count)

                ' A job the host was following when it stopped is followed again when it starts (here: the first job, as if it had
                ' been running on the server, which knows it as job1, when the host stopped).
                Dim firstId = first("id").ToString()
                Assert.NotEqual("job1", firstId)
                sd.SetState("job1", "running")
                Await env.Host.H.Database.ExecuteAsync("UPDATE SdJobs SET Status='running', ImageIdsJson=NULL, CostMicros=NULL, FinishedUtc=NULL, SubmittedUtc=@t WHERE Id=@id",
                                                       ("@t", CObj(Global.Agentic.Persistence.Db.Ts(DateTime.UtcNow))), ("@id", firstId))
                Await env.Host.H.Database.ExecuteAsync("DELETE FROM SdImages WHERE JobId=@id", ("@id", CObj(firstId)))
                env.Host.H.DiffusionJobs.Start(CancellationToken.None)
                Await WaitJobAsync(admin, firstId, "running")
                Dim resumed = Await WaitJobAsync(admin, firstId)
                Assert.Equal("done", resumed("status").ToString())
                Assert.Equal(3, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/gallery?job=" & firstId))).AsArray().Count)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ units

    <Fact>
    Public Sub JobAnswersAreReadWhateverTheirFieldNames()
        Dim a = DiffusionService.ReadJob(JsonNode.Parse("{""state"":""processing"",""progress"":45,""current_step"":9,""steps_total"":20}"))
        Assert.Equal("running", a.Status)
        Assert.Equal(0.45, a.Progress.Value, 3)
        Assert.Equal(9, a.Step)
        Assert.Equal(20, a.TotalSteps)
        Dim b = DiffusionService.ReadJob(JsonNode.Parse("{""status"":""completed"",""images"":[""" & New String("A"c, 300) & """],""seeds"":[5,6],""infos"":[""Seed: 5""],""elapsed_ms"":1200}"))
        Assert.Equal("done", b.Status)
        Assert.True(b.Finished)
        Assert.Single(b.InlineImages)
        Assert.Equal({5L, 6L}, b.Seeds.ToArray())
        Assert.Equal(1200L, b.ElapsedMs)
        Dim c = DiffusionService.ReadJob(JsonNode.Parse("{""status"":""failed"",""detail"":""out of memory""}"))
        Assert.Equal("failed", c.Status)
        Assert.Equal("out of memory", c.Error)
        Assert.Equal("cancelled", DiffusionService.ReadJob(JsonNode.Parse("{""status"":""canceled""}")).Status)
        Assert.Equal(2, DiffusionService.ReadJob(JsonNode.Parse("{""status"":""pending"",""queue_position"":2}")).QueuePosition)
        ' Progress in an object of its own, or only as step of steps: the bar still moves.
        Dim nested = DiffusionService.ReadJob(JsonNode.Parse("{""status"":""running"",""progress"":{""step"":5,""total"":20}}"))
        Assert.Equal(5, nested.Step)
        Assert.Equal(20, nested.TotalSteps)
        Assert.Equal(0.25, nested.Progress.Value, 3)
        Dim percent = DiffusionService.ReadJob(JsonNode.Parse("{""status"":""running"",""progress"":{""percent"":60}}"))
        Assert.Equal(0.6, percent.Progress.Value, 3)
        Dim stepsOnly = DiffusionService.ReadJob(JsonNode.Parse("{""status"":""running"",""step"":3,""num_inference_steps"":30}"))
        Assert.Equal(0.1, stepsOnly.Progress.Value, 3)
        Assert.False(DiffusionService.ReadJob(JsonNode.Parse("{""status"":""running""}")).Progress.HasValue)
    End Sub

    <Fact>
    Public Sub PngInfoSeedsAndEstimates()
        Dim png = FakeSd.Png("a cat" & vbLf & "Steps: 25, Seed: 987654, Size: 8x8")
        Assert.Equal("a cat" & vbLf & "Steps: 25, Seed: 987654, Size: 8x8", DiffusionService.PngParameters(png))
        Assert.Equal(987654L, DiffusionService.SeedOf(DiffusionService.PngParameters(png)))
        Assert.Equal((8, 8), DiffusionService.PngSize(png).Value)
        Assert.Null(DiffusionService.PngParameters(FakeSd.Png(Nothing)))
        Assert.Equal(10000L, DiffusionService.EstimateGpuMs(New JsonObject From {{"steps", 25}}))
        Assert.Equal(20000L, DiffusionService.EstimateGpuMs(New JsonObject From {{"steps", 25}, {"num_images", 2}}))
        Assert.Equal(5000L, DiffusionService.EstimateGpuMs(New JsonObject From {{"steps", 25}, {"init_image", "x"}, {"strength", 0.5}}))
        Dim p As New SdPrice With {.PerImage = 0.01D, .PerGpuMinute = 0.6D}
        Assert.Equal(30000L + 600000L, p.Micros(3, 60000))
        Assert.Equal("images.sd", Permissions.ForRoute("POST", "/api/images/jobs"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("PUT", "/api/images/key"))
        Assert.Equal("finance.manage", Permissions.ForRoute("PUT", "/api/images/pricing"))
        Assert.Equal("finance.view|images.sd", Permissions.ForRoute("GET", "/api/images/pricing"))
    End Sub

End Class
