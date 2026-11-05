Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Http
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' A stand-in for the media API (yuanqiaoyun, a new-api relay): gpt-image models on /v1/images/generations and
''' Seedance-style video tasks on /v1/video/generations, with the finished video served from /files/.
''' </summary>
Friend NotInheritable Class FakeMediaApi
    Implements IDisposable
    Private ReadOnly _listener As New HttpListener()
    Private ReadOnly _cts As New CancellationTokenSource()
    Private ReadOnly _polls As New ConcurrentDictionary(Of String, Integer)
    Private ReadOnly _taskModels As New ConcurrentDictionary(Of String, String)
    Private _tasks As Integer
    Public ReadOnly Property Port As Integer
    ''' <summary>Models that answer 503 "Service is temporarily unavailable" (like gpt-image-2.5-flare did when tested).</summary>
    Public ReadOnly Property Down As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    ''' <summary>Video models whose task ends in FAILURE.</summary>
    Public ReadOnly Property FailingVideo As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    ''' <summary>Every call in order: "image gpt-image-2", "video seedance-2.0-fast", "poll task_1", "file task_1.mp4".</summary>
    Public ReadOnly Property Calls As New ConcurrentQueue(Of String)
    ''' <summary>The bodies of the image and video requests.</summary>
    Public ReadOnly Property Bodies As New ConcurrentQueue(Of JsonNode)
    ''' <summary>The Authorization header of every call.</summary>
    Public ReadOnly Property Keys As New ConcurrentQueue(Of String)
    ''' <summary>The raw multipart bodies of picture edits.</summary>
    Public ReadOnly Property EditBodies As New ConcurrentQueue(Of Byte())

    ''' <summary>What every edit returns (a different picture from the generated one).</summary>
    Public Shared ReadOnly EditedPng As Byte() = ModelProbe.RedBluePng(16, 4)

    ''' <summary>The video every task makes: an MP4 header ("ftyp") and some bytes.</summary>
    Public Shared ReadOnly Mp4 As Byte() = New Byte() {0, 0, 0, &H18}.Concat(Encoding.ASCII.GetBytes("ftypisom")).Concat(New Byte() {0, 0, 2, 0}).
        Concat(Encoding.ASCII.GetBytes("isomiso2")).Concat(Enumerable.Range(0, 4000).Select(Function(i) CByte(i Mod 251))).ToArray()

    Public Sub New()
        Port = HttpApiTests.FreePort()
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/")
        _listener.Start()
        Task.Run(AddressOf LoopAsync)
    End Sub

    Public ReadOnly Property BaseUrl As String
        Get
            Return $"http://127.0.0.1:{Port}/v1"
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
        Keys.Enqueue(If(ctx.Request.Headers("Authorization"), ""))
        Dim body As JsonNode = Nothing
        Dim raw As Byte() = Nothing
        If ctx.Request.HttpMethod = "POST" Then
            Using ms As New IO.MemoryStream()
                Await ctx.Request.InputStream.CopyToAsync(ms)
                raw = ms.ToArray()
            End Using
            If If(ctx.Request.ContentType, "").StartsWith("application/json", StringComparison.OrdinalIgnoreCase) Then
                body = JsonNode.Parse(raw)
                Bodies.Enqueue(body)
            End If
        End If
        Dim unavailable = "{""error"":{""message"":""Service is temporarily unavailable"",""type"":""new_api_error""}}"
        If path = "/v1/models" Then
            Await WriteAsync(res, 200, "application/json", "{""data"":[{""id"":""gpt-image-2""},{""id"":""gpt-image-2.5-flare""},{""id"":""gpt-image-2.5-sunburst""}]}")
            Return
        End If
        ' Picture edits: multipart/form-data with model, prompt, one image file and an optional size.
        If path = "/v1/images/edits" Then
            EditBodies.Enqueue(raw)
            Dim form = Encoding.Latin1.GetString(raw)
            Dim model = Text.RegularExpressions.Regex.Match(form, "name=""?model""?\r\n(?:[^\r\n]+\r\n)*\r\n([^\r\n]*)").Groups(1).Value
            Calls.Enqueue("edit " & model)
            If Down.Contains(model) Then
                Await WriteAsync(res, 503, "application/json", unavailable)
                Return
            End If
            If Not form.Contains("name=image") AndAlso Not form.Contains("name=""image""") Then
                Await WriteAsync(res, 500, "application/json", "{""error"":{""message"":""image is required"",""type"":""new_api_error"",""code"":""convert_request_failed""}}")
                Return
            End If
            Await WriteAsync(res, 200, "application/json", JsonUtil.ToJson(New With {.created = 1, .data = {New With {.b64_json = Convert.ToBase64String(EditedPng)}}}))
            Return
        End If
        If path = "/v1/images/generations" Then
            Dim model = body("model")?.ToString()
            Calls.Enqueue("image " & model)
            If Down.Contains(model) Then
                Await WriteAsync(res, 503, "application/json", unavailable)
                Return
            End If
            Await WriteAsync(res, 200, "application/json", JsonUtil.ToJson(New With {.created = 1, .data = {New With {.b64_json = Convert.ToBase64String(ModelProbe.RedBluePng(8, 8))}}}))
            Return
        End If
        If path = "/v1/video/generations" Then
            Dim model = body("model")?.ToString()
            Calls.Enqueue("video " & model)
            If Down.Contains(model) Then
                Await WriteAsync(res, 503, "application/json", unavailable)
                Return
            End If
            Dim id = "task_" & Interlocked.Increment(_tasks)
            _taskModels(id) = model
            Await WriteAsync(res, 200, "application/json", JsonUtil.ToJson(New With {.id = id, .task_id = id, .object = "video", .model = model, .status = "queued", .progress = 0}))
            Return
        End If
        If path.StartsWith("/v1/video/generations/", StringComparison.Ordinal) Then
            Dim id = path.Substring("/v1/video/generations/".Length)
            Calls.Enqueue("poll " & id)
            Dim n = _polls.AddOrUpdate(id, 1, Function(k, v) v + 1)
            Dim model As String = Nothing
            _taskModels.TryGetValue(id, model)
            Dim data As Object
            If model IsNot Nothing AndAlso FailingVideo.Contains(model) Then
                data = New With {.task_id = id, .status = "FAILURE", .fail_reason = "The prompt was rejected by the content filter", .progress = "100%"}
            ElseIf n = 1 Then
                data = New With {.task_id = id, .status = "IN_PROGRESS", .progress = "40%"}
            Else
                data = New With {.task_id = id, .status = "SUCCESS", .progress = "100%", .result_url = $"http://127.0.0.1:{Port}/files/{id}.mp4"}
            End If
            Await WriteAsync(res, 200, "application/json", JsonUtil.ToJson(New With {.code = "success", .message = "", .data = data}))
            Return
        End If
        If path.StartsWith("/files/", StringComparison.Ordinal) Then
            Calls.Enqueue("file " & path.Substring("/files/".Length))
            Await WriteBytesAsync(res, 200, "video/mp4", Mp4)
            Return
        End If
        Await WriteAsync(res, 404, "application/json", "{}")
    End Function

    Private Shared Function WriteAsync(res As HttpListenerResponse, status As Integer, type As String, text As String) As Task
        Return WriteBytesAsync(res, status, type, Encoding.UTF8.GetBytes(text))
    End Function

    Private Shared Async Function WriteBytesAsync(res As HttpListenerResponse, status As Integer, type As String, bytes As Byte()) As Task
        res.StatusCode = status
        res.ContentType = type
        res.ContentLength64 = bytes.Length
        Await res.OutputStream.WriteAsync(bytes)
        res.Close()
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _listener.Close()
    End Sub
End Class

''' <summary>
''' Pictures and videos come only from their own APIs (ImageGeneration / VideoGeneration, e.g. yuanqiaoyun's gpt-image and
''' Seedance models), never from the chat provider (OpenWebUI); the next configured model steps in when one cannot.
''' </summary>
Public Class MediaGenerationTests

    ''' <summary>The host makes pictures and videos with the fake media API (gpt-image-2, then gpt-image-2.5-flare; Seedance fast, then mini).</summary>
    Friend Shared Sub UseMediaApi(o As HostOptions, media As FakeMediaApi)
        o.ImageGeneration.BaseUrl = media.BaseUrl
        o.ImageGeneration.ApiKey = "img-key-123"
        o.ImageGeneration.Model = "gpt-image-2"
        o.ImageGeneration.FallbackModels = New List(Of String) From {"gpt-image-2.5-flare"}
        o.ImageGeneration.AutoFallback = False
        o.VideoGeneration.BaseUrl = media.BaseUrl
        o.VideoGeneration.ApiKey = "vid-key-456"
        o.VideoGeneration.Model = "seedance-2.0-fast"
        o.VideoGeneration.FallbackModels = New List(Of String) From {"seedance-2.0-mini"}
        o.VideoGeneration.PollSeconds = 1
    End Sub

    ' ------------------------------------------------------------------ images

    <Fact>
    Public Async Function TheNextImageModelOfTheImagesApiMakesThePictureWhenTheFirstCannot() As Task
        Dim provider As New FakeAIProvider()
        provider.WorkingImageModels.Add("gpt-image-2.5-flare")
        Dim opts As New ImageGenerationOptions With {.Model = "gpt-image-2", .BaseUrl = "https://media.example/v1", .ApiKey = "k", .AutoFallback = False}
        opts.FallbackModels.Add("gpt-image-2.5-flare")
        opts.FallbackModels.Add("gpt-image-2.5-sunburst")
        Dim router As New ImageRouter(Function() provider, opts, Nothing)
        Assert.True(router.Enabled)
        Dim attempts As New List(Of String)
        Dim made = Await router.GenerateAsync("a lion", "1024x1024", CancellationToken.None,
                                              Function(m)
                                                  attempts.Add(m)
                                                  Return Task.CompletedTask
                                              End Function)
        Assert.Equal("gpt-image-2.5-flare", made.Model)
        Assert.Equal({"gpt-image-2", "gpt-image-2.5-flare"}, attempts.ToArray())
        Assert.Equal("gpt-image-2: No available channel for model gpt-image-2", Assert.Single(made.Failures))

        ' A model that just failed waits its turn: the next picture goes straight to the one that worked.
        Dim junk As String = Nothing
        While provider.ImageCalls.TryDequeue(junk)
        End While
        made = Await router.GenerateAsync("a tiger", "1024x1024", CancellationToken.None)
        Assert.Equal("gpt-image-2.5-flare", made.Model)
        Assert.Equal({"gpt-image-2.5-flare"}, provider.ImageCalls.ToArray())

        ' A model asked for by name goes first; a model that is not one of them is refused (no other source is used).
        provider.WorkingImageModels.Add("gpt-image-2.5-sunburst")
        While provider.ImageCalls.TryDequeue(junk)
        End While
        made = Await router.GenerateAsync("a cat", Nothing, CancellationToken.None, preferredModel:="GPT-IMAGE-2.5-SUNBURST")
        Assert.Equal("gpt-image-2.5-sunburst", made.Model)
        Dim refused = Await Assert.ThrowsAsync(Of AiException)(Function() router.GenerateAsync("a cat", Nothing, CancellationToken.None, preferredModel:="dddai2/gpt-image-2"))
        Assert.Equal(AiErrorKind.InvalidRequest, refused.Kind)

        ' When no model can, the error names each one and why; asked again right after, the answer comes at once.
        provider.WorkingImageModels.Clear()
        Dim ex = Await Assert.ThrowsAsync(Of AiException)(Function() router.GenerateAsync("a bear", Nothing, CancellationToken.None))
        Assert.Contains("gpt-image-2: No available channel", ex.Message)
        Assert.Contains("gpt-image-2.5-sunburst: No available channel", ex.Message)
        While provider.ImageCalls.TryDequeue(junk)
        End While
        ex = Await Assert.ThrowsAsync(Of AiException)(Function() router.GenerateAsync("a bear", Nothing, CancellationToken.None))
        Assert.Contains("s ago)", ex.Message)
        Assert.Empty(provider.ImageCalls)

        ' Without the images API (its address and key) there is no image generation at all.
        Assert.False(New ImageRouter(Function() provider, New ImageGenerationOptions With {.Model = "gpt-image-2", .ApiKey = "k"}, Nothing).Enabled)
        Assert.False(New ImageRouter(Function() provider, New ImageGenerationOptions With {.Model = "gpt-image-2", .BaseUrl = "https://media.example/v1"}, Nothing).Enabled)
        Assert.False(New ImageRouter(Nothing, New ImageGenerationOptions With {.Model = "gpt-image-2", .BaseUrl = "https://media.example/v1", .ApiKey = "k"}, Nothing).Enabled)

        ' AutoFallback adds the API's other image models after the configured ones (never its chat or video models).
        Dim auto As New ImageGenerationOptions With {.Model = "gpt-image-2", .BaseUrl = "https://media.example/v1", .ApiKey = "k"}
        Dim routes = Await New ImageRouter(Function() provider, auto,
                                           Function(ct) Task.FromResult(New List(Of String) From {"gpt-image-2", "seedance-2.0-fast", "gpt-image-2.5-flare", "gpt-4o"})).RoutesAsync(CancellationToken.None)
        Assert.Equal({"gpt-image-2", "gpt-image-2.5-flare"}, routes.ToArray())
    End Function

    <Fact>
    Public Async Function ChatPicturesComeOnlyFromTheImagesApi() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            media.Down.Add("gpt-image-2")
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(stepNo) = 1, ChatFeatureTests.ToolCallStream("generate_image", "{""prompt"":""a lion""}"),
                                              ChatFeatureTests.TextStream("Here is your lion.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.imageGeneration = True}})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "make an image of a lion"})
                Assert.Contains("Creating the image with gpt-image-2.5-flare", sse)
                Dim message = ChatFeatureTests.Done(sse)("message")
                Dim image = Assert.Single(message("attachments").AsArray())
                Assert.EndsWith("· gpt-image-2.5-flare", image("summary").ToString())
                Dim made = message("toolCalls").AsArray().Single(Function(t) t("name").ToString() = "generate_image")
                Assert.True(made("ok").GetValue(Of Boolean)())
                Assert.Contains("made by gpt-image-2.5-flare", made("result").ToString())
                Assert.Contains("first choice was unavailable", made("result").ToString())
                Assert.Equal({"image gpt-image-2", "image gpt-image-2.5-flare"}, media.Calls.ToArray())
                Assert.All(media.Keys, Sub(k) Assert.Equal("Bearer img-key-123", k))
                ' The chat provider (OpenWebUI) is never asked for a picture.
                Assert.Empty(env.Host.Provider.ImageCalls)
                Assert.Equal("image/png", (Await admin.Client.GetAsync($"/api/chat/files/{image("uploadId")}")).Content.Headers.ContentType.MediaType)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheRouterChangesAPictureWithTheNextModelThatCan() As Task
        Dim provider As New FakeAIProvider()
        provider.WorkingImageModels.Add("gpt-image-2.5-flare")
        Dim opts As New ImageGenerationOptions With {.Model = "gpt-image-2", .BaseUrl = "https://media.example/v1", .ApiKey = "k", .AutoFallback = False}
        opts.FallbackModels.Add("gpt-image-2.5-flare")
        Dim router As New ImageRouter(Function() provider, opts, Nothing)
        Dim made = Await router.EditAsync("make it blue", Nothing, ModelProbe.RedBluePng(8, 8), "image/png", "red.png", CancellationToken.None)
        Assert.Equal("gpt-image-2.5-flare", made.Model)
        Assert.Equal({"edit:gpt-image-2", "edit:gpt-image-2.5-flare"}, provider.ImageCalls.ToArray())
        ' A model that could not change a picture is still asked first for new pictures.
        provider.WorkingImageModels.Add("gpt-image-2")
        Dim junk As String = Nothing
        While provider.ImageCalls.TryDequeue(junk)
        End While
        made = Await router.GenerateAsync("a cat", Nothing, CancellationToken.None)
        Assert.Equal("gpt-image-2", made.Model)
        Assert.Equal({"gpt-image-2"}, provider.ImageCalls.ToArray())
        Await Assert.ThrowsAsync(Of AiException)(Function() router.EditAsync("x", Nothing, Array.Empty(Of Byte)(), "image/png", "x.png", CancellationToken.None))
    End Function

    <Fact>
    Public Async Function APictureTheUserAttachedIsChangedThroughTheImagesApi() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("generate_image", "{""prompt"":""make the red half blue"",""reference_image"":""latest""}")
                                        Case 2 : Return ChatFeatureTests.TextStream("Here it is, all blue.", "stop")
                                        Case 3 : Return ChatFeatureTests.ToolCallStream("generate_image", "{""prompt"":""add a sun"",""reference_image"":""nope.png""}")
                                        Case Else : Return ChatFeatureTests.TextStream("I could not find that picture.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim png = ModelProbe.RedBluePng(8, 8)
                Dim upload = Await ImageAndVisionRoutingTests.UploadAsync(admin, "red.png", png)
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.imageGeneration = True}})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "make the red half blue", .attachments = {upload}})
                Assert.Contains("Changing red.png with gpt-image-2", sse)
                ' The model was told which pictures it can change, and how.
                Dim first = up.Requests.ToArray().First(AddressOf ChatFeatureTests.Streamed)
                Assert.Contains("red.png (attached by the user)", first("messages")(0)("content").ToString())
                Assert.Contains("reference_image", first("tools").ToJsonString())
                ' The picture and the prompt went to the edits endpoint; no size was asked, so the picture keeps its shape.
                Assert.Equal({"edit gpt-image-2"}, media.Calls.ToArray())
                Dim sent = Encoding.Latin1.GetString(Assert.Single(media.EditBodies))
                Assert.Contains("make the red half blue", sent)
                Assert.Contains("red.png", sent)
                Assert.Contains(Encoding.Latin1.GetString(png), sent)
                Assert.DoesNotContain("name=size", sent)
                Assert.All(media.Keys, Sub(k) Assert.Equal("Bearer img-key-123", k))
                Dim message = ChatFeatureTests.Done(sse)("message")
                Dim image = Assert.Single(message("attachments").AsArray())
                Assert.EndsWith("· gpt-image-2 · from red.png", image("summary").ToString())
                Assert.Equal(FakeMediaApi.EditedPng, Await (Await admin.Client.GetAsync($"/api/chat/files/{image("uploadId")}")).Content.ReadAsByteArrayAsync())
                Dim changed = message("toolCalls").AsArray().Single(Function(t) t("name").ToString() = "generate_image")
                Assert.True(changed("ok").GetValue(Of Boolean)())
                Assert.Contains("red.png was changed by gpt-image-2", changed("result").ToString())
                Assert.Empty(env.Host.Provider.ImageCalls)

                ' A picture that is not in the chat: the model hears which ones are, the one it made included.
                Dim sse2 = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "add a sun to nope.png"})
                Dim missing = ChatFeatureTests.Done(sse2)("message")("toolCalls").AsArray().Single(Function(t) t("name").ToString() = "generate_image")
                Assert.False(missing("ok").GetValue(Of Boolean)())
                Assert.Contains("no picture called 'nope.png'", missing("result").ToString())
                Assert.Contains("red.png", missing("result").ToString())
                Assert.Contains(image("name").ToString(), missing("result").ToString())
                Assert.Contains(image("name").ToString() & " (made by you)", ChatFeatureTests.LastStreamedRequest(up)("messages")(0)("content").ToString())
                Assert.Single(media.EditBodies)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function OnlyYourOwnFilesCanBeAttached() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim adminsFile = Await ImageAndVisionRoutingTests.UploadAsync(admin, "secret.png", ModelProbe.RedBluePng(8, 8))
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "mallory", .password = "mallory-pass-123", .role = "User",
                                                                                                                             .permissions = PlatformApiTests.ChatUser, .allowedModels = {"fast/a"}}, admin.Csrf)).StatusCode)
                Dim mallory = Await PlatformApiTests.LoginAsync(env.Port, "mallory", "mallory-pass-123")
                Dim own = Await ImageAndVisionRoutingTests.UploadAsync(mallory, "mine.png", ModelProbe.RedBluePng(8, 8))
                Dim id = Await ChatFeatureTests.NewChatAsync(mallory, New With {.model = "fast/a"})
                Assert.Contains("event: done", Await ChatFeatureTests.AskAsync(mallory, id, New With {.content = "what is in these?", .attachments = {adminsFile, own}}))
                Dim names = (Await ChatFeatureTests.GetChatAsync(mallory, id))("messages").AsArray().
                    SelectMany(Function(m) If(m("attachments")?.AsArray(), New JsonArray())).Select(Function(a) a("name").ToString()).ToArray()
                Assert.Equal({"mine.png"}, names)
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)("messages").ToJsonString()
                Assert.Contains("mine.png", sent)
                Assert.DoesNotContain("secret.png", sent)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function WithoutTheMediaApisChatsOfferNeitherImagesNorVideos() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                Assert.False(meta("imageGeneration").GetValue(Of Boolean)())
                Assert.False(meta("videoGeneration").GetValue(Of Boolean)())
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.imageGeneration = True, .videoGeneration = True}})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "draw a lion"})
                Dim tools = ChatFeatureTests.ToolNames(ChatFeatureTests.LastStreamedRequest(up))
                Assert.DoesNotContain("generate_image", tools)
                Assert.DoesNotContain("generate_video", tools)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheImagesEndpointUsesOnlyTheImageModels() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim key = Await PlatformApiTests.NewKeyAsync(admin.Client, admin.Csrf)
                Using api As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    ' A client's default model ("dall-e-3") is not one of ours: the configured model makes the pictures.
                    Dim res = Await PlatformApiTests.Send(api, "POST", "/v1/images/generations", New With {.model = "dall-e-3", .prompt = "a red dot", .n = 2}, key:=key)
                    Dim json = JsonNode.Parse(Await res.Content.ReadAsStringAsync())
                    Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                    Assert.Equal("gpt-image-2", json("model").ToString())
                    Assert.Equal(2, json("data").AsArray().Count)
                    Assert.Equal(ModelProbe.RedBluePng(8, 8), Convert.FromBase64String(json("data")(0)("b64_json").ToString()))
                    ' A model of ours asked for by name.
                    res = Await PlatformApiTests.Send(api, "POST", "/v1/images/generations", New With {.model = "gpt-image-2.5-flare", .prompt = "a blue dot"}, key:=key)
                    Assert.Equal("gpt-image-2.5-flare", JsonNode.Parse(Await res.Content.ReadAsStringAsync())("model").ToString())
                    Assert.Equal({"image gpt-image-2", "image gpt-image-2", "image gpt-image-2.5-flare"}, media.Calls.ToArray())
                    ' The chat provider's image models are not used for chat either.
                    res = Await PlatformApiTests.Send(api, "POST", "/v1/chat/completions", New With {.model = "dddai2/gpt-image-2", .messages = {New With {.role = "user", .content = "draw"}}}, key:=key)
                    Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode)
                    Assert.Contains("/v1/images/generations", Await res.Content.ReadAsStringAsync())
                End Using
                Assert.Empty(env.Host.Provider.ImageCalls)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ videos

    <Fact>
    Public Async Function ChatVideosComeFromTheVideoApiAndPlayInThePage() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            ' The first model's task fails: the next model makes the video.
            media.FailingVideo.Add("seedance-2.0-fast")
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(stepNo) = 1, ChatFeatureTests.ToolCallStream("generate_video", "{""prompt"":""a lion running"",""seconds"":5,""ratio"":""9:16""}"),
                                              ChatFeatureTests.TextStream("Here is your video.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta")))("videoGeneration").GetValue(Of Boolean)())
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.videoGeneration = True}})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "make a video of a lion running"})
                Assert.Contains("generate_video", ChatFeatureTests.ToolNames(up.Requests.ToArray().First(AddressOf ChatFeatureTests.Streamed)))
                Assert.Contains("event: video", sse)
                Assert.Contains("Creating the video with seedance-2.0-mini", sse)
                Dim message = ChatFeatureTests.Done(sse)("message")
                Assert.Equal("Here is your video.", message("content").ToString())
                Dim video = Assert.Single(message("attachments").AsArray())
                Assert.Equal("video", video("kind").ToString())
                Assert.EndsWith("· 5 s · seedance-2.0-mini", video("summary").ToString())
                Dim made = message("toolCalls").AsArray().Single(Function(t) t("name").ToString() = "generate_video")
                Assert.True(made("ok").GetValue(Of Boolean)())
                Assert.Contains("made by seedance-2.0-mini", made("result").ToString())
                Assert.Contains("rejected by the content filter", made("result").ToString())
                ' What the model asked for reached the video API, with the video key.
                Dim sent = media.Bodies.ToArray().Last(Function(b) b("model")?.ToString() = "seedance-2.0-mini")
                Assert.Equal(5, sent("duration").GetValue(Of Integer)())
                Assert.Equal("9:16", sent("metadata")("ratio").ToString())
                Assert.Equal("a lion running", sent("prompt").ToString())
                Assert.All(media.Keys, Sub(k) Assert.Equal("Bearer vid-key-456", k))
                Assert.Contains("file task_2.mp4", media.Calls)

                ' The video plays: whole, and in pieces (phones and Safari ask for byte ranges).
                Dim url = $"/api/chat/files/{video("uploadId")}"
                Dim whole = Await admin.Client.GetAsync(url)
                Assert.Equal(HttpStatusCode.OK, whole.StatusCode)
                Assert.Equal("video/mp4", whole.Content.Headers.ContentType.MediaType)
                Assert.Equal("bytes", String.Join(",", whole.Headers.AcceptRanges))
                Assert.Equal(FakeMediaApi.Mp4, Await whole.Content.ReadAsByteArrayAsync())
                Dim first As New HttpRequestMessage(HttpMethod.Get, url)
                first.Headers.Range = New Headers.RangeHeaderValue(0, 99)
                Dim part = Await admin.Client.SendAsync(first)
                Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode)
                Assert.Equal($"bytes 0-99/{FakeMediaApi.Mp4.Length}", part.Content.Headers.ContentRange.ToString())
                Assert.Equal(FakeMediaApi.Mp4.Take(100).ToArray(), Await part.Content.ReadAsByteArrayAsync())
                Dim tail As New HttpRequestMessage(HttpMethod.Get, url)
                tail.Headers.TryAddWithoutValidation("Range", "bytes=-10")
                Assert.Equal(FakeMediaApi.Mp4.Skip(FakeMediaApi.Mp4.Length - 10).ToArray(), Await (Await admin.Client.SendAsync(tail)).Content.ReadAsByteArrayAsync())

                ' Shared chats show the video too.
                Dim share = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/share", "{}", admin.Csrf))
                Dim token = share("token").ToString()
                Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim view = Await ChatFeatureTests.JsonOf(Await anon.GetAsync($"/api/share/{token}"))
                    Dim sharedVideo = view("messages").AsArray().Last()("attachments").AsArray().Single()
                    Assert.Equal("video", sharedVideo("kind").ToString())
                    Dim sharedFile = Await anon.GetAsync($"/api/share/{token}/files/{sharedVideo("uploadId")}")
                    Assert.Equal(HttpStatusCode.OK, sharedFile.StatusCode)
                    Assert.Equal("video/mp4", sharedFile.Content.Headers.ContentType.MediaType)
                End Using
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AChatHasImageOrVideoOnNeverBoth() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim settingsOf = Async Function(chatId As String) As Task(Of (Image As Boolean, Video As Boolean))
                                     Dim s = (Await ChatFeatureTests.GetChatAsync(admin, chatId))("settings")
                                     Return (s("imageGeneration").GetValue(Of Boolean)(), s("videoGeneration").GetValue(Of Boolean)())
                                 End Function
                Dim both = New With {.imageGeneration = True, .videoGeneration = True}
                ' A new chat asked for both keeps Image.
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = both})
                Assert.Equal((True, False), Await settingsOf(id))
                ' Switching Video on switches Image off, and the other way round.
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{id}", New With {.settings = both}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal((False, True), Await settingsOf(id))
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{id}", New With {.settings = both}, admin.Csrf)).IsSuccessStatusCode)
                Assert.Equal((True, False), Await settingsOf(id))
                ' A message sent with both on gets one tool: the one just switched on.
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi", .settings = both})
                Dim tools = ChatFeatureTests.ToolNames(ChatFeatureTests.LastStreamedRequest(up))
                Assert.Contains("generate_video", tools)
                Assert.DoesNotContain("generate_image", tools)
                ' A chat saved with both on before they were exclusive offers the image tool only.
                Await env.Host.H.Chat.UpdateAsync("admin", id, Nothing, Nothing, New ChatSettings With {.ImageGeneration = True, .VideoGeneration = True}, Nothing, Nothing)
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi again"})
                tools = ChatFeatureTests.ToolNames(ChatFeatureTests.LastStreamedRequest(up))
                Assert.Contains("generate_image", tools)
                Assert.DoesNotContain("generate_video", tools)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function WhenNoVideoModelCanTheModelIsToldWhy() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            media.Down.Add("seedance-2.0-fast")
            media.Down.Add("seedance-2.0-mini")
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(stepNo) = 1, ChatFeatureTests.ToolCallStream("generate_video", "{""prompt"":""a lion""}"),
                                              ChatFeatureTests.TextStream("Sorry, no video right now.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.videoGeneration = True}})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "make a video of a lion"}))("message")
                Assert.Empty(If(message("attachments")?.AsArray(), New JsonArray()))
                Dim failed = message("toolCalls").AsArray().Single(Function(t) t("name").ToString() = "generate_video")
                Assert.False(failed("ok").GetValue(Of Boolean)())
                Assert.Contains("seedance-2.0-fast: HTTP 503: Service is temporarily unavailable", failed("result").ToString())
                Assert.Contains("seedance-2.0-mini: HTTP 503: Service is temporarily unavailable", failed("result").ToString())
                Assert.Equal({"video seedance-2.0-fast", "video seedance-2.0-mini"}, media.Calls.ToArray())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub VideoTasksAreReadInEveryShape()
        ' yuanqiaoyun (new-api) task answers.
        Dim s = VideoService.ParseTask(JsonNode.Parse("{""code"":""success"",""data"":{""status"":""NOT_START"",""progress"":""0%""}}"))
        Assert.Equal("queued", s.Status)
        s = VideoService.ParseTask(JsonNode.Parse("{""code"":""success"",""data"":{""status"":""IN_PROGRESS"",""progress"":""40%""}}"))
        Assert.Equal("running", s.Status)
        Assert.Equal(40, s.Progress)
        s = VideoService.ParseTask(JsonNode.Parse("{""code"":""success"",""data"":{""status"":""SUCCESS"",""progress"":""100%"",""result_url"":""https://cdn.example.com/v/1.mp4""}}"))
        Assert.Equal("done", s.Status)
        Assert.Equal("https://cdn.example.com/v/1.mp4", s.Url)
        s = VideoService.ParseTask(JsonNode.Parse("{""code"":""success"",""data"":{""status"":""FAILURE"",""fail_reason"":""bad prompt""}}"))
        Assert.Equal("failed", s.Status)
        Assert.Equal("bad prompt", s.Error)
        ' OpenAI / Volcengine-style answers.
        s = VideoService.ParseTask(JsonNode.Parse("{""id"":""t1"",""status"":""queued"",""progress"":0}"))
        Assert.Equal("queued", s.Status)
        s = VideoService.ParseTask(JsonNode.Parse("{""id"":""t1"",""status"":""succeeded"",""content"":{""video_url"":""https://cdn.example.com/2.mp4""}}"))
        Assert.Equal("done", s.Status)
        Assert.Equal("https://cdn.example.com/2.mp4", s.Url)
        Assert.Null(VideoService.VideoUrlOf(JsonNode.Parse("{""data"":{""result_url"":""javascript:alert(1)""}}")))

        Dim opts As New VideoGenerationOptions With {.Model = "seedance-2.0-fast", .BaseUrl = "https://media.example/v1", .ApiKey = "k", .PricePerSecond = 0.05D}
        opts.FallbackModels.Add("seedance-2.0-mini")
        opts.FallbackModels.Add("SEEDANCE-2.0-FAST")
        opts.ModelPricePerSecond("seedance-2.0-mini") = 0.02D
        Dim videos As New VideoService(opts)
        Assert.True(videos.Enabled)
        Assert.Equal({"seedance-2.0-fast", "seedance-2.0-mini"}, videos.Models().ToArray())
        Assert.Equal(50000L, videos.PricePerSecondMicros("seedance-2.0-fast"))
        Assert.Equal(20000L, videos.PricePerSecondMicros("seedance-2.0-mini"))
        Assert.Equal("9:16", videos.NormalizeRatio("9:16"))
        Assert.Equal("16:9", videos.NormalizeRatio("2:1"))
        Assert.False(New VideoService(New VideoGenerationOptions With {.BaseUrl = "https://media.example/v1"}).Enabled)
        Assert.False(New VideoService(New VideoGenerationOptions With {.ApiKey = "k"}).Enabled)
    End Sub

    <Fact>
    Public Sub ByteRangesAreReadLikeBrowsersSendThem()
        Assert.Equal((0L, 99L), AdminApi.ParseRange("bytes=0-99", 1000).Value)
        Assert.Equal((500L, 999L), AdminApi.ParseRange("bytes=500-", 1000).Value)
        Assert.Equal((900L, 999L), AdminApi.ParseRange("bytes=-100", 1000).Value)
        Assert.Equal((0L, 999L), AdminApi.ParseRange("bytes=0-5000", 1000).Value)
        Assert.Equal((0L, 999L), AdminApi.ParseRange("bytes=-5000", 1000).Value)
        Assert.Null(AdminApi.ParseRange("bytes=1000-", 1000))
        Assert.Null(AdminApi.ParseRange("bytes=5-2", 1000))
        Assert.Null(AdminApi.ParseRange("bytes=0-1,5-6", 1000))
        Assert.Null(AdminApi.ParseRange("items=0-1", 1000))
        Assert.Null(AdminApi.ParseRange("bytes=-", 1000))
        Assert.Null(AdminApi.ParseRange(Nothing, 1000))
    End Sub
End Class
