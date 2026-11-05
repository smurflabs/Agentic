Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Http
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The speech and Stable Diffusion servers' lines (2 requests at once and 1, first come first served, from every page and
''' the API), personal API keys reaching speech, pictures and songs, and the three APIs described on /swagger.
''' </summary>
Public Class MediaApiTests

    Private Shared Async Function PollAsync(c As HttpClient, url As String, Optional status As String = Nothing) As Task(Of JsonNode)
        For i = 0 To 300
            Dim j = Await ChatFeatureTests.JsonOf(Await c.GetAsync(url))
            Dim st = j("status").ToString()
            If If(status Is Nothing, st = "done" OrElse st = "failed" OrElse st = "cancelled", st = status) Then Return j
            Await Task.Delay(100)
        Next
        Throw New TimeoutException(url & " did not end.")
    End Function

    Private Shared Function IdOf(res As HttpResponseMessage) As String
        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode)
        Return JsonNode.Parse(res.Content.ReadAsStringAsync().GetAwaiter().GetResult())("id").ToString()
    End Function

    <Fact>
    Public Async Function TheLineIsFirstComeFirstServedAndLeavingItKeepsTheOrder() As Task
        Dim q As New WorkQueue(2)
        Dim a = Await q.EnterAsync("ann", "a", CancellationToken.None)
        Dim b = Await q.EnterAsync("bob", "b", CancellationToken.None)
        Dim leave As New CancellationTokenSource()
        Dim c = q.EnterAsync("ann", "c", CancellationToken.None, "c")
        Dim d = q.EnterAsync("bob", "d", leave.Token, "d")
        Dim e = q.EnterAsync("ann", "e", CancellationToken.None, "e")
        Assert.False(c.IsCompleted)
        Assert.Equal(2, q.Running)
        Assert.Equal(3, q.Waiting)
        Assert.Equal(2, q.PositionOf("d"))
        Dim ann = q.StateFor("ann")
        Assert.Equal({1, 3}, ann.Mine.Select(Function(m) m.Position).ToArray())
        Assert.Equal(1, ann.MineRunning)
        Assert.Equal({"c", "e"}, ann.Mine.Select(Function(m) m.Label).ToArray())
        ' Leaving: the rest keep their order.
        leave.Cancel()
        Await Assert.ThrowsAnyAsync(Of OperationCanceledException)(Function() d)
        Assert.Equal(2, q.PositionOf("e"))
        ' A slot freed goes to the first in line, then the next.
        a.Dispose()
        a.Dispose()
        Dim cSlot = Await c.WaitAsync(TimeSpan.FromSeconds(5))
        Assert.False(e.IsCompleted)
        Assert.Equal(1, q.PositionOf("e"))
        b.Dispose()
        Dim eSlot = Await e.WaitAsync(TimeSpan.FromSeconds(5))
        Assert.Equal(0, q.Waiting)
        cSlot.Dispose()
        eSlot.Dispose()
        Assert.Equal(0, q.Running)
        ' A free slot with nobody waiting is taken at once.
        Assert.True(q.EnterAsync("ann", "f", CancellationToken.None).IsCompleted)
    End Function

    <Fact>
    Public Async Function SpeechWaitsInLineForTheServersOneSlot() As Task
        Using up As New FakeUpstream(), tts As New FakeXtts()
            tts.DelayMs = 700
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.TextToSpeech.BaseUrl = tts.BaseUrl
                                                                o.TextToSpeech.ApiKey = "test-key"
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' Five at once, as from Read aloud, Dictation, the Audio tab and the API: one is made, four wait their turn.
                Dim calls = Enumerable.Range(1, 5).Select(Function(i) If(i = 3,
                    PlatformApiTests.Send(admin.Client, "POST", "/api/audio/stream", New With {.text = "Streamed " & i}, admin.Csrf),
                    PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "Line " & i}, admin.Csrf))).ToList()
                Dim waiting As JsonNode = Nothing
                For i = 0 To 50
                    waiting = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio/queue"))
                    If waiting("waiting").GetValue(Of Integer)() = 4 Then Exit For
                    Await Task.Delay(50)
                Next
                Assert.Equal(1, waiting("slots").GetValue(Of Integer)())
                Assert.Equal(1, waiting("running").GetValue(Of Integer)())
                Assert.Equal(4, waiting("waiting").GetValue(Of Integer)())
                Assert.Equal({1, 2, 3, 4}, waiting("mine").AsArray().Select(Function(m) m("position").GetValue(Of Integer)()).ToArray())
                Assert.Equal(1, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio")))("queue")("running").GetValue(Of Integer)())
                For Each res In Await Task.WhenAll(calls)
                    Assert.True(res.IsSuccessStatusCode, Await res.Content.ReadAsStringAsync())
                    Assert.True((Await res.Content.ReadAsByteArrayAsync()).Length > 44)
                Next
                Assert.Equal(1, tts.MostAtOnce)
                Assert.Equal(5, tts.Speech.Count)
                Dim after As JsonNode = Nothing
                For i = 0 To 40
                    after = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio/queue"))
                    If after("running").GetValue(Of Integer)() = 0 Then Exit For
                    Await Task.Delay(50)
                Next
                Assert.Equal(0, after("running").GetValue(Of Integer)())
                Assert.Equal(0, after("waiting").GetValue(Of Integer)())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function PictureJobsWaitInLineAndAreSentOneAtATime() As Task
        Using up As New FakeUpstream(), sd As New FakeSd()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.StableDiffusion.BaseUrl = sd.BaseUrl
                                                                o.StableDiffusion.ApiKey = "sd-key"
                                                                o.StableDiffusion.PollMilliseconds = 100
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                sd.Hold = True
                Dim one = IdOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/images/jobs", New With {.prompt = "one"}, admin.Csrf))
                Dim twoRes = Await PlatformApiTests.Send(admin.Client, "POST", "/api/images/jobs", New With {.prompt = "two"}, admin.Csrf)
                Dim twoJob = JsonNode.Parse(Await twoRes.Content.ReadAsStringAsync())
                Dim two = IdOf(twoRes)
                Dim three = IdOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/images/jobs", New With {.prompt = "three", .num_images = 2}, admin.Csrf))
                ' The first went to the server; the others wait here, in order.
                Assert.Single(sd.Jobs)
                Assert.True(twoJob("inLine").GetValue(Of Boolean)())
                Assert.Equal(1, twoJob("queuePosition").GetValue(Of Integer)())
                Dim third = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/jobs/" & three))
                Assert.Equal("queued", third("status").ToString())
                Assert.Equal(2, third("queuePosition").GetValue(Of Integer)())
                Assert.False((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/jobs/" & one)))("inLine").GetValue(Of Boolean)())
                Dim line = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/queue"))
                Assert.Equal(1, line("slots").GetValue(Of Integer)())
                Assert.Equal(1, line("running").GetValue(Of Integer)())
                Assert.Equal(2, line("waiting").GetValue(Of Integer)())
                ' No live preview while waiting.
                Assert.Equal(HttpStatusCode.NoContent, (Await admin.Client.GetAsync($"/api/images/jobs/{three}/preview")).StatusCode)

                ' Cancelling a waiting job: it leaves the line and never reaches the server; the next moves up.
                Dim cancelled = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "DELETE", "/api/images/jobs/" & two, Nothing, admin.Csrf))
                Assert.Equal("cancelled", cancelled("status").ToString())
                Assert.Equal(1, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/jobs/" & three)))("queuePosition").GetValue(Of Integer)())

                ' A preprocessor run waits behind the jobs too.
                Dim pre = PlatformApiTests.Send(admin.Client, "POST", "/api/images/preprocess", New With {.preprocessor = "canny", .image = Convert.ToBase64String(FakeSd.Png(Nothing))}, admin.Csrf)
                Await Task.Delay(400)
                Assert.False(pre.IsCompleted)
                Assert.Empty(sd.Preprocessed)

                sd.Hold = False
                Assert.Equal("done", (Await PollAsync(admin.Client, "/api/images/jobs/" & one))("status").ToString())
                Dim done3 = Await PollAsync(admin.Client, "/api/images/jobs/" & three)
                Assert.Equal("done", done3("status").ToString())
                Assert.Equal(2, done3("imageIds").AsArray().Count)
                Dim map = Await pre
                Assert.True(map.IsSuccessStatusCode, Await map.Content.ReadAsStringAsync())
                Assert.Single(sd.Preprocessed)
                Assert.Equal({"one", "three"}, sd.Jobs.Select(Function(j) j("prompt").ToString()).ToArray())
                Assert.Equal("cancelled", (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/jobs/" & two)))("status").ToString())

                ' A job still waiting when the host stopped is sent when it starts again.
                Dim store = env.Host.H.DiffusionStore
                Dim id = "sdjob_waited"
                Await store.SaveWaitingAsync(id, New JsonObject From {{"prompt", "later"}})
                Await store.AddJobAsync(New SdJobRecord With {.Id = id, .Username = "admin", .Status = "queued", .Prompt = "later", .Request = New JsonObject From {{"prompt", "later"}},
                                                              .NumImages = 1, .CreatedUtc = DateTime.UtcNow})
                env.Host.H.DiffusionJobs.Start(CancellationToken.None)
                Dim later = Await PollAsync(admin.Client, "/api/images/jobs/" & id)
                Assert.Equal("done", later("status").ToString())
                Assert.Equal("later", sd.Jobs.Last()("prompt").ToString())
                Assert.Null(Await store.ReadWaitingAsync(id))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function PersonalApiKeysMakeSpeechPicturesAndSongs() As Task
        Using up As New FakeUpstream(), tts As New FakeXtts(), sd As New FakeSd(), suno As New FakeSuno()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.TextToSpeech.BaseUrl = tts.BaseUrl
                                                                o.TextToSpeech.ApiKey = "test-key"
                                                                o.StableDiffusion.BaseUrl = sd.BaseUrl
                                                                o.StableDiffusion.ApiKey = "sd-key"
                                                                o.StableDiffusion.PollMilliseconds = 100
                                                                o.Music.BaseUrl = suno.BaseUrl
                                                                o.Music.ApiKey = "suno-key"
                                                                o.Music.PollSeconds = 1
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "maker", .password = "maker-pass-123", .role = "User",
                    .permissions = {"account.apikey", "chat.voice", "images.sd", "music.suno"}}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "plain", .password = "plain-pass-123", .role = "User",
                    .permissions = {"account.apikey"}}, admin.Csrf)).StatusCode)
                Dim keyOf = Async Function(user As String, password As String) As Task(Of String)
                                Dim s = Await PlatformApiTests.LoginAsync(env.Port, user, password)
                                Return JsonNode.Parse(Await (Await PlatformApiTests.Send(s.Client, "POST", "/api/auth/api-key", "{}", s.Csrf)).Content.ReadAsStringAsync())("key").ToString()
                            End Function
                Dim key = Await keyOf("maker", "maker-pass-123")
                ' A program: no cookie, no CSRF token, only the key.
                Dim api As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                api.DefaultRequestHeaders.Add("x-api-key", key)
                Dim post = Function(url As String, body As Object) api.PostAsync(url, New StringContent(Text.Json.JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"))

                ' Speech.
                Assert.True((Await ChatFeatureTests.JsonOf(Await api.GetAsync("/api/audio")))("ready").GetValue(Of Boolean)())
                Dim wav = Await post("/api/audio/tts", New With {.text = "Hello from a program", .voiceKey = "speaker:Damien Black", .speed = 1.1})
                Assert.True(wav.IsSuccessStatusCode, Await wav.Content.ReadAsStringAsync())
                Assert.Equal("audio/wav", wav.Content.Headers.ContentType.MediaType)
                Assert.Equal("Damien Black", tts.Speech.Last()("speaker").ToString())

                ' Pictures: a job, followed to its end, then its PNG.
                Dim job = Await PollAsync(api, "/api/images/jobs/" & IdOf(Await post("/api/images/jobs", New With {.prompt = "a red fox", .steps = 20})))
                Assert.Equal("done", job("status").ToString())
                Dim png = Await api.GetAsync($"/api/images/gallery/{job("imageIds")(0)}/file")
                Assert.Equal("image/png", png.Content.Headers.ContentType.MediaType)

                ' Songs, with the key as a Bearer token this time.
                Dim bearer As New HttpClient() With {.BaseAddress = api.BaseAddress}
                bearer.DefaultRequestHeaders.Authorization = New Headers.AuthenticationHeaderValue("Bearer", key)
                Dim started = Await bearer.PostAsync("/api/music/jobs", New StringContent("{""prompt"":""a calm piano song""}", Encoding.UTF8, "application/json"))
                Dim songs = Await PollAsync(bearer, "/api/music/jobs/" & IdOf(started))
                Assert.Equal("done", songs("status").ToString())
                Dim audio = Await bearer.GetAsync($"/api/music/songs/{songs("songIds")(0)}/audio")
                Assert.True(audio.IsSuccessStatusCode)
                Assert.True((Await audio.Content.ReadAsByteArrayAsync()).Length > 0)
                Assert.Equal(2, (Await ChatFeatureTests.JsonOf(Await bearer.GetAsync("/api/music/songs"))).AsArray().Count)

                ' A key acts with its account's permissions: none of these without them; a wrong key is not signed in.
                Dim plain As New HttpClient() With {.BaseAddress = api.BaseAddress}
                plain.DefaultRequestHeaders.Add("x-api-key", Await keyOf("plain", "plain-pass-123"))
                For Each url In {"/api/audio", "/api/images", "/api/music"}
                    Assert.Equal(HttpStatusCode.Forbidden, (Await plain.GetAsync(url)).StatusCode)
                Next
                Dim wrong As New HttpClient() With {.BaseAddress = api.BaseAddress}
                wrong.DefaultRequestHeaders.Add("x-api-key", "agk_" & New String("x"c, 40))
                Assert.Equal(HttpStatusCode.Unauthorized, (Await wrong.GetAsync("/api/audio")).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SwaggerDescribesSpeechPicturesAndSongs() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim doc = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/openapi.json"))
                Dim paths = doc("paths").AsObject()
                ' Every route of the three is described by hand.
                For Each p In paths.Where(Function(x) x.Key.StartsWith("/api/audio") OrElse x.Key.StartsWith("/api/images") OrElse x.Key.StartsWith("/api/music"))
                    For Each op In p.Value.AsObject()
                        Assert.True(OpenApi.MediaDocs.ContainsKey(op.Key.ToUpperInvariant() & " " & p.Key), $"{op.Key} {p.Key} has no description")
                        Assert.Contains(op.Value("tags")(0).ToString(), {OpenApi.AudioTag, OpenApi.ImagesTag, OpenApi.MusicTag})
                    Next
                Next
                Dim tts = paths("/api/audio/tts")("post")
                Dim ttsFields = tts("requestBody")("content")("application/json")("schema")("properties").AsObject()
                For Each f In {"text", "voiceKey", "language", "speed", "temperature", "top_k", "top_p", "repetition_penalty", "length_penalty", "enable_text_splitting", "save"}
                    Assert.True(ttsFields.ContainsKey(f), f)
                Next
                Assert.NotNull(tts("responses")("200")("content")("audio/wav"))
                Assert.Contains("chat.voice", tts("description").ToString())
                Dim jobs = paths("/api/images/jobs")("post")
                Assert.NotNull(jobs("responses")("202")("content")("application/json")("schema")("properties")("queuePosition"))
                Assert.True(jobs("requestBody")("content")("application/json")("schema")("properties").AsObject().ContainsKey("controlnets"))
                Assert.NotNull(jobs("requestBody")("content")("application/json")("example"))
                Assert.NotNull(paths("/api/images/gallery/{imageId}/file")("get")("responses")("200")("content")("image/png"))
                Dim song = paths("/api/music/jobs")("post")("requestBody")("content")("application/json")("schema")("properties").AsObject()
                Assert.True(song.ContainsKey("lyrics") AndAlso song.ContainsKey("instrumental") AndAlso song.ContainsKey("tags"))
                Dim tags = doc("tags").AsArray()
                Assert.Contains("one request at a time", tags.First(Function(t) t("name").ToString() = OpenApi.AudioTag)("description").ToString())
                Assert.Contains("market_prices", tags.First(Function(t) t("name").ToString() = OpenApi.MarketsTag)("description").ToString())
                Assert.Contains("news_articles", tags.First(Function(t) t("name").ToString() = OpenApi.NewsTag)("description").ToString())
                Assert.Contains("one job at a time", tags.First(Function(t) t("name").ToString() = OpenApi.ImagesTag)("description").ToString())
                Assert.Contains("x-api-key", doc("info")("description").ToString())
            End Using
        End Using
    End Function
End Class
