Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Pictures sent to a model that cannot read them (the local Qwen) are answered by a vision model; image links are read
''' safely. (Making pictures and videos: MediaGenerationTests.)
''' </summary>
Public Class ImageAndVisionRoutingTests

    Friend Shared Async Function UploadAsync(user As (Client As HttpClient, Csrf As String), name As String, data As Byte()) As Task(Of String)
        Dim req As New HttpRequestMessage(HttpMethod.Post, "/api/uploads?name=" & Uri.EscapeDataString(name)) With {.Content = New ByteArrayContent(data)}
        req.Headers.Add("X-CSRF-Token", user.Csrf)
        Return (Await ChatFeatureTests.JsonOf(Await user.Client.SendAsync(req)))("id").ToString()
    End Function

    Private Shared Function StreamedModels(up As FakeUpstream) As String()
        Return up.Requests.ToArray().Where(AddressOf ChatFeatureTests.Streamed).Select(Function(r) r("model").ToString()).ToArray()
    End Function

    ' ------------------------------------------------------------------ pictures for a model that cannot read them

    <Fact>
    Public Async Function PicturesForAModelThatCannotReadThemGoToAVisionModel() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                env.Host.H.Chat.NoVisionModels.Add("fast/a")
                env.Host.H.Chat.VisionModels = New List(Of String) From {"code/b"}
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim png = Await UploadAsync(admin, "red.png", ModelProbe.RedBluePng(8, 8))
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "what colour is this?", .attachments = {png}})
                Assert.Contains("event: route", sse)
                Dim message = ChatFeatureTests.Done(sse)("message")
                Assert.Equal("code/b", message("routedModel").ToString())
                Assert.Contains("cannot read images", message("routeInfo").ToString())
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)
                Assert.Equal("code/b", sent("model").ToString())
                Assert.Contains("image_url", sent("messages").ToJsonString())

                ' The next message without pictures goes to the chosen model again.
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "thanks"})
                Assert.Equal("fast/a", ChatFeatureTests.LastStreamedRequest(up)("model").ToString())

                ' An account that may use no vision model: the picture goes as its description, to the chosen model.
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "seer", .password = "seer-pass-1234", .role = "User",
                                                                                                                             .permissions = PlatformApiTests.ChatUser, .allowedModels = {"fast/a"}}, admin.Csrf)).StatusCode)
                Dim seer = Await PlatformApiTests.LoginAsync(env.Port, "seer", "seer-pass-1234")
                Dim png2 = Await UploadAsync(seer, "blue.png", ModelProbe.RedBluePng(8, 8))
                Dim chat2 = Await ChatFeatureTests.NewChatAsync(seer, New With {.model = "fast/a"})
                Dim sse2 = Await ChatFeatureTests.AskAsync(seer, chat2, New With {.content = "what is this?", .attachments = {png2}})
                Assert.Contains("event: done", sse2)
                Dim sent2 = ChatFeatureTests.LastStreamedRequest(up)
                Assert.Equal("fast/a", sent2("model").ToString())
                Assert.DoesNotContain("image_url", sent2("messages").ToJsonString())
                Assert.Contains("[Attached image: blue.png", sent2("messages").ToJsonString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AModelThatTurnsPicturesAwayIsRememberedAndAVisionModelAnswers() As Task
        Using up As New FakeUpstream()
            ' Like the local Qwen: "started without the vision encoder".
            up.ErrorOverride = Function(req) If(req("model").ToString() = "fast/a" AndAlso req("messages").ToJsonString().Contains("image_url"),
                                                "litellm.BadRequestError: OpenAIException - this server was started without the vision encoder (run setup again and choose 'vision'), so it cannot read images", Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                env.Host.H.Chat.VisionModels = New List(Of String) From {"code/b"}
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim png = Await UploadAsync(admin, "red.png", ModelProbe.RedBluePng(8, 8))
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "what colour is this?", .attachments = {png}})
                Assert.DoesNotContain("event: error", sse)
                Assert.Equal("code/b", ChatFeatureTests.Done(sse)("message")("routedModel").ToString())
                Assert.Equal({"fast/a", "code/b"}, StreamedModels(up))
                ' Remembered: the next picture goes straight to the vision model.
                Dim png2 = Await UploadAsync(admin, "red2.png", ModelProbe.RedBluePng(8, 8))
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "and this one?", .attachments = {png2}})
                Assert.Equal({"fast/a", "code/b", "code/b"}, StreamedModels(up))
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ units

    <Fact>
    Public Sub ImageAnswersAndLinksAreReadSafely()
        Dim openRouter = JsonNode.Parse("{""choices"":[{""message"":{""content"":""Here"",""images"":[{""type"":""image_url"",""image_url"":{""url"":""data:image/png;base64,iVBORw0KGgo=""}}]}}]}")
        Assert.Equal("data:image/png;base64,iVBORw0KGgo=", OpenAICompatibleProvider.ExtractImageRefs(openRouter)(0))
        Dim markdown = JsonNode.Parse("{""choices"":[{""message"":{""content"":""![lion](https://cdn.example.com/a/lion.png)""}}]}")
        Assert.Equal("https://cdn.example.com/a/lion.png", OpenAICompatibleProvider.ExtractImageRefs(markdown)(0))
        Dim parts = JsonNode.Parse("{""choices"":[{""message"":{""content"":[{""type"":""text"",""text"":""ok""},{""type"":""image_url"",""image_url"":{""url"":""https://x.example/i.webp""}}]}}]}")
        Assert.Equal("https://x.example/i.webp", OpenAICompatibleProvider.ExtractImageRefs(parts)(0))
        Assert.Equal("QUJD", OpenAICompatibleProvider.ExtractImageRefs(JsonNode.Parse("{""data"":[{""b64_json"":""QUJD""}]}"))(0))
        Assert.Empty(OpenAICompatibleProvider.ExtractImageRefs(JsonNode.Parse("{""choices"":[{""message"":{""content"":""I cannot draw.""}}]}")))

        For Each blocked In {"127.0.0.1", "10.0.0.5", "172.16.3.4", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "::1", "fd00::1", "fe80::1", "::ffff:10.0.0.1"}
            Assert.False(OpenAICompatibleProvider.IsPublicAddress(IPAddress.Parse(blocked)), blocked)
        Next
        Assert.True(OpenAICompatibleProvider.IsPublicAddress(IPAddress.Parse("93.184.216.34")))

        Assert.True(ImageRouter.IsImageModel("lmzh/dall-e-3"))
        Assert.True(ImageRouter.IsImageModel("dddai2/gpt-image-2"))
        Assert.True(ImageRouter.IsImageModel("google/gemini-2.5-flash-image"))
        Assert.False(ImageRouter.IsImageModel("yqy/gemini-3.8-flash"))
        Assert.False(ImageRouter.IsImageModel("qwen3.8-flash-MAX"))
        Assert.Equal("当前分组上游负载已饱和，请稍后再试",
                     ImageRouter.ShortReason("litellm.RateLimitError: RateLimitError: OpenAIException - 当前分组上游负载已饱和，请稍后再试 (request id: 2026)No fallback model group found"))

        Assert.True(ChatService.IsNoVisionError("this server was started without the vision encoder (run setup again and choose 'vision'), so it cannot read images"))
        Assert.True(ChatService.IsNoVisionError("Image input is not supported for this model"))
        ' The exact litellm BadRequestError the audit found from ablit/abliterated-model-large-v2 (it arrives as a non-HTTP exception now too).
        Assert.True(ChatService.IsNoVisionError("litellm.BadRequestError: OpenAIException - This model does not accept image inputs. Send this request without images."))
        Assert.False(ChatService.IsNoVisionError("rate limit exceeded"))
    End Sub

    <Fact>
    Public Async Function ImageLinksToPrivateAddressesOrPlainHttpAreRefused() As Task
        Dim ex = Await Assert.ThrowsAsync(Of AiException)(Function() OpenAICompatibleProvider.DownloadExternalImageAsync("https://127.0.0.1/x.png", 1000, CancellationToken.None))
        Assert.Contains("private address", ex.Message)
        ex = Await Assert.ThrowsAsync(Of AiException)(Function() OpenAICompatibleProvider.DownloadExternalImageAsync("http://example.com/x.png", 1000, CancellationToken.None))
        Assert.Contains("https", ex.Message)
    End Function
End Class
