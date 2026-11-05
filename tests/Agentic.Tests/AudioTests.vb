Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Http
Imports Agentic.Work
Imports Xunit

''' <summary>A stand-in for the XTTS-v2 speech server: its key check, voices, languages, /tts, /tts/stream and cloning.</summary>
Friend NotInheritable Class FakeXtts
    Implements IDisposable
    Private ReadOnly _listener As New HttpListener()
    Private ReadOnly _cts As New CancellationTokenSource()
    Public ReadOnly Property Port As Integer
    Public Property Key As String = "test-key"
    ''' <summary>Every request's path and the key it came with.</summary>
    Public ReadOnly Property Calls As New Concurrent.ConcurrentQueue(Of (Path As String, Key As String))
    ''' <summary>The JSON bodies of /tts and /tts/stream.</summary>
    Public ReadOnly Property Speech As New Concurrent.ConcurrentQueue(Of JsonNode)
    ''' <summary>The form fields of each POST /voices (files: how many clips).</summary>
    Public ReadOnly Property Clones As New Concurrent.ConcurrentQueue(Of Dictionary(Of String, String))
    Public ReadOnly Property Voices As New List(Of String)
    ''' <summary>/tts/stream answers 200 with no audio (as the server does when it fails right after starting).</summary>
    Public Property EmptyStream As Boolean
    ''' <summary>Samples of speech per request (24 kHz).</summary>
    Public Property Samples As Integer = 12000
    ''' <summary>How long making speech takes.</summary>
    Public Property DelayMs As Integer
    ''' <summary>The most speech requests it had at once.</summary>
    Public ReadOnly Property MostAtOnce As Integer
        Get
            Return _most
        End Get
    End Property
    Private _active As Integer
    Private _most As Integer

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

    Public Function Pcm() As Byte()
        Dim data(Samples * 2 - 1) As Byte
        For i = 0 To Samples - 1
            Dim v = CShort(Math.Round(Math.Sin(i / 10.0) * 8000))
            BitConverter.GetBytes(v).CopyTo(data, i * 2)
        Next
        Return data
    End Function

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
        Calls.Enqueue((path, req.Headers("X-API-Key")))
        If req.Headers("X-API-Key") <> Key Then
            Await Json(res, 401, "{""detail"":""Invalid or missing API key""}")
            Return
        End If
        Dim body As Byte()
        Using ms As New IO.MemoryStream()
            Await req.InputStream.CopyToAsync(ms)
            body = ms.ToArray()
        End Using
        If path = "/speakers" Then
            Dim cloned As JsonNode()
            SyncLock Voices
                cloned = Voices.Select(Function(v) CType(JsonValue.Create(v), JsonNode)).ToArray()
            End SyncLock
            Await Json(res, 200, New JsonObject From {{"builtin", New JsonArray("Ana Florence", "Damien Black", "Claribel Dervla")}, {"voices", New JsonArray(cloned)}}.ToJsonString())
        ElseIf path = "/languages" Then
            Await Json(res, 200, "[""en"",""es"",""fr"",""zh-cn""]")
        ElseIf path = "/tts" OrElse path = "/tts/stream" Then
            Dim now = Interlocked.Increment(_active)
            Dim seen = _most
            While now > seen AndAlso Interlocked.CompareExchange(_most, now, seen) <> seen
                seen = _most
            End While
            Try
                Await SpeakAsync(req, res, path, body)
            Finally
                Interlocked.Decrement(_active)
            End Try
        ElseIf path = "/voices" AndAlso req.HttpMethod = "POST" Then
            Dim text = Encoding.Latin1.GetString(body)
            Dim fields As New Dictionary(Of String, String)
            For Each m As Match In Regex.Matches(text, "name=""?([a-z_]+)""?\r\n(?:Content-Type: text/plain; charset=utf-8\r\n)?\r\n([^\r]*)\r\n")
                fields(m.Groups(1).Value) = m.Groups(2).Value
            Next
            fields("files") = Regex.Matches(text, "name=""?files""?; filename=").Count.ToString()
            Clones.Enqueue(fields)
            SyncLock Voices
                If Not Voices.Contains(fields("name")) Then Voices.Add(fields("name"))
            End SyncLock
            Await Json(res, 200, $"{{""name"":""{fields("name")}"",""clips"":{fields("files")},""expires_at"":""2026-12-01T00:00:00Z""}}")
        ElseIf path.StartsWith("/voices/", StringComparison.Ordinal) Then
            Dim name = Uri.UnescapeDataString(path.Substring(8))
            Dim known As Boolean
            SyncLock Voices
                known = Voices.Contains(name)
                If known AndAlso req.HttpMethod = "DELETE" Then Voices.Remove(name)
            End SyncLock
            If Not known Then
                Await Json(res, 404, "{""detail"":""Voice not found""}")
            ElseIf req.HttpMethod = "DELETE" Then
                Await Json(res, 200, "{""deleted"":true}")
            Else
                Await Json(res, 200, $"{{""name"":""{name}"",""clips"":[""a.wav""],""settings"":{{""gpt_cond_len"":30}},""expires_at"":""2026-12-01T00:00:00Z""}}")
            End If
        Else
            Await Json(res, 404, "{""detail"":""Not Found""}")
        End If
    End Function

    Private Async Function SpeakAsync(req As HttpListenerRequest, res As HttpListenerResponse, path As String, body As Byte()) As Task
        If DelayMs > 0 Then Await Task.Delay(DelayMs)
        Dim j = JsonNode.Parse(Encoding.UTF8.GetString(body))
        Speech.Enqueue(j)
        If String.IsNullOrWhiteSpace(j("text")?.ToString()) Then
            Await Json(res, 400, "{""detail"":""Empty text""}")
            Return
        End If
        If path = "/tts" Then
            Await WriteAsync(res, 200, "audio/wav", SpeechService.PcmToWav(Pcm()))
            Return
        End If
        res.StatusCode = 200
        res.SendChunked = True
        res.ContentType = If(req.QueryString("format") = "wav", "audio/wav", "application/octet-stream")
        If Not EmptyStream Then
            Dim data = If(req.QueryString("format") = "wav", SpeechService.PcmToWav(Pcm()), Pcm())
            ' A streamed WAV's header has a placeholder length.
            If req.QueryString("format") = "wav" Then BitConverter.GetBytes(UInteger.MaxValue).CopyTo(data, 40)
            For at = 0 To data.Length - 1 Step 4001
                Dim n = Math.Min(4001, data.Length - at)
                Await res.OutputStream.WriteAsync(data, at, n)
                Await res.OutputStream.FlushAsync()
            Next
        End If
        res.Close()
    End Function

    Public Sub Dispose() Implements IDisposable.Dispose
        _cts.Cancel()
        _listener.Close()
    End Sub
End Class

''' <summary>
''' Speech through the XTTS server: the key stays on the server (config or vault), every speech setting reaches it checked,
''' streamed speech falls back to the whole file, clips are kept, voices are cloned (with consent) and renewed, and Dictation
''' mode's spoken summary is written in the voice's personality and kept with the answer.
''' </summary>
Public Class AudioTests

    Private Shared Async Function BytesOf(res As HttpResponseMessage) As Task(Of Byte())
        Dim data = Await res.Content.ReadAsByteArrayAsync()
        Assert.True(res.IsSuccessStatusCode, Encoding.UTF8.GetString(data))
        Return data
    End Function

    <Fact>
    Public Async Function SpeechGoesThroughTheServerWithItsKeyKeptHere() As Task
        Using up As New FakeUpstream(), tts As New FakeXtts()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.TextToSpeech.BaseUrl = tts.BaseUrl)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' No key yet: speech is set up but cannot work, and says why.
                Dim info = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio"))
                Assert.True(info("available").GetValue(Of Boolean)())
                Assert.False(info("ready").GetValue(Of Boolean)())
                Assert.Contains("API key", info("problem").ToString())
                Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                Assert.True(meta("voice").GetValue(Of Boolean)())

                ' The administrator sets the key: it goes to the vault (no agent may use it) and the server takes it.
                Dim set1 = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/key", New With {.apiKey = "test-key"}, admin.Csrf))
                Assert.True(set1("ok").GetValue(Of Boolean)())
                Dim secret = Await env.Host.H.Secrets.GetInfoAsync("xtts-api-key")
                Assert.NotNull(secret)
                Assert.Empty(secret.AgentIds)
                Assert.Equal({"127.0.0.1"}, secret.Hosts.ToArray())
                Dim infoText = Await admin.Client.GetStringAsync("/api/audio")
                Assert.DoesNotContain("test-key", infoText)
                info = JsonNode.Parse(infoText)
                Assert.True(info("ready").GetValue(Of Boolean)())
                Assert.Contains("Damien Black", info("speakers")("builtin").AsArray().Select(Function(x) x.ToString()))
                Assert.Contains("zh-cn", info("languages").AsArray().Select(Function(x) x.ToString()))

                ' The chosen voice and its personality's settings fill in what a request leaves out.
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/prefs", New With {.voice = "speaker:Damien Black", .silenceMs = 1200}, admin.Csrf))
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/prefs", New With {.silenceMs = 50}, admin.Csrf)).StatusCode)
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/profiles",
                    New With {.voice = "speaker:Damien Black", .summaryPrompt = "You are rude.", .settings = New With {.temperature = 0.6, .repetition_penalty = 7, .language = "es"}}, admin.Csrf))
                Dim wavRes = Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "Hola", .save = True}, admin.Csrf)
                Dim wav = Await BytesOf(wavRes)
                Assert.Equal("audio/wav", wavRes.Content.Headers.ContentType.MediaType)
                Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4))
                Dim fileClip = wavRes.Headers.GetValues("X-Clip-Id").Single()
                Dim sent = tts.Speech.Last()
                Assert.Equal("Damien Black", sent("speaker").ToString())
                Assert.Equal(0.6, sent("temperature").GetValue(Of Double)())
                Assert.Equal(7.0, sent("repetition_penalty").GetValue(Of Double)())
                Assert.Equal("es", sent("language").ToString())
                Assert.Null(sent("stream_chunk_size"))
                Assert.Equal("test-key", tts.Calls.Last().Key)

                ' Settings out of range are refused here, with the field named.
                Dim bad = Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "x", .temperature = 5}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode)
                Assert.Contains("temperature", Await bad.Content.ReadAsStringAsync())
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "x", .language = "xx"}, admin.Csrf)).StatusCode)

                ' Streamed: every setting the page sends reaches the server; the PCM arrives whole and is kept as a WAV clip.
                Dim pcmRes = Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/stream?format=pcm",
                    New With {.text = "Hello there", .voiceKey = "speaker:Ana Florence", .useProfile = False, .speed = 1.2, .top_k = 30, .top_p = 0.9,
                              .length_penalty = 1.5, .enable_text_splitting = False, .stream_chunk_size = 10, .save = True}, admin.Csrf)
                Dim pcm = Await BytesOf(pcmRes)
                Assert.Equal(tts.Pcm(), pcm)
                sent = tts.Speech.Last()
                Assert.Equal("Ana Florence", sent("speaker").ToString())
                Assert.Equal(10, sent("stream_chunk_size").GetValue(Of Integer)())
                Assert.Equal(30, sent("top_k").GetValue(Of Integer)())
                Assert.False(sent("enable_text_splitting").GetValue(Of Boolean)())
                Assert.Null(sent("temperature"))
                Dim streamClip = pcmRes.Headers.GetValues("X-Clip-Id").Single()

                ' A streamed WAV (header with a placeholder length) is kept as a proper WAV too.
                Dim wavStream = Await BytesOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/stream?format=wav", New With {.text = "Hi", .save = True}, admin.Csrf))
                Assert.Equal(tts.Pcm(), SpeechService.WavToPcm(wavStream))

                ' Nothing from the stream: the whole file is made instead.
                tts.EmptyStream = True
                Dim fallback = Await BytesOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/stream", New With {.text = "Fallback please"}, admin.Csrf))
                Assert.Equal(tts.Pcm(), fallback)
                tts.EmptyStream = False

                Dim clips = (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio/clips"))).AsArray()
                Assert.Equal(3, clips.Count)
                Dim kept = clips.First(Function(c) c("id").ToString() = streamClip)
                Assert.Equal("stream", kept("mode").ToString())
                Assert.Equal(500, kept("durationMs").GetValue(Of Long)())
                Assert.Equal(1.2, kept("settings")("speed").GetValue(Of Double)())
                Dim keptWav = Await BytesOf(Await admin.Client.GetAsync($"/api/audio/clips/{streamClip}/audio"))
                Assert.Equal(tts.Pcm(), SpeechService.WavToPcm(keptWav))
                Assert.Equal(tts.Pcm(), Await BytesOf(Await admin.Client.GetAsync($"/api/audio/clips/{streamClip}/audio?format=pcm")))
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "DELETE", $"/api/audio/clips/{fileClip}", Nothing, admin.Csrf))
                Assert.Equal(2, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio/clips"))).AsArray().Count)

                ' A wrong key: the server's refusal is not passed on as the user's own 401.
                tts.Key = "other"
                Dim refused = Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "x"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode)
                Assert.Contains("refused the API key", Await refused.Content.ReadAsStringAsync())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function VoicesAreClonedWithConsentAndCanBeRenewed() As Task
        Using up As New FakeUpstream(), tts As New FakeXtts()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.TextToSpeech.BaseUrl = tts.BaseUrl
                                                                o.TextToSpeech.ApiKey = "test-key"
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim clipBody As New ByteArrayContent(SpeechService.PcmToWav(tts.Pcm()))
                clipBody.Headers.ContentType = New Headers.MediaTypeHeaderValue("audio/wav")
                Dim stage As New HttpRequestMessage(HttpMethod.Post, "/api/audio/voice-clips?name=me%20talking.wav") With {.Content = clipBody}
                stage.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim staged = Await ChatFeatureTests.JsonOf(Await admin.Client.SendAsync(stage))
                Dim clipId = staged("id").ToString()

                Dim noConsent = Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/voices", New With {.name = "me", .clips = {clipId}}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, noConsent.StatusCode)
                Assert.Contains("CONSENT_REQUIRED", Await noConsent.Content.ReadAsStringAsync())
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/voices",
                    New With {.name = "me", .clips = {clipId}, .consent = True, .gptCondLen = 10, .gptCondChunkLen = 20}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/voices",
                    New With {.name = "bad name!", .clips = {clipId}, .consent = True}, admin.Csrf)).StatusCode)
                Assert.Empty(tts.Clones)

                Dim made = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/voices",
                    New With {.name = "me", .clips = {clipId}, .consent = True, .trimSilenceDb = 30, .normalize = True, .gptCondLen = 20, .gptCondChunkLen = 5, .maxRefLength = 25}, admin.Csrf))
                Assert.Equal("voice:me", made("key").ToString())
                Dim form = tts.Clones.Single()
                Assert.Equal("me", form("name"))
                Assert.Equal("1", form("files"))
                Assert.Equal("30", form("trim_silence_db"))
                Assert.Equal("true", form("normalize"))
                Assert.Equal("20", form("gpt_cond_len"))
                Assert.Equal("5", form("gpt_cond_chunk_len"))
                Assert.Equal("25", form("max_ref_length"))

                Dim info = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio?refresh=1"))
                Assert.Contains("me", info("speakers")("voices").AsArray().Select(Function(x) x.ToString()))
                Dim source = info("sources").AsArray().Single()
                Assert.Equal("me", source("name").ToString())
                Assert.True(source("mine").GetValue(Of Boolean)())
                Assert.Equal("01-me talking.wav", source("clips")(0).ToString())

                ' A cloned voice speaks as "voice", not "speaker".
                Await BytesOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "It's me", .voiceKey = "voice:me"}, admin.Csrf))
                Assert.Equal("me", tts.Speech.Last()("voice").ToString())
                Assert.Null(tts.Speech.Last()("speaker"))

                ' Renewing uploads the kept clip again with the same settings.
                Dim details = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio/voices/me"))
                Assert.Equal("2026-12-01T00:00:00Z", details("server")("expires_at").ToString())
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/voices/me/renew", New With {.x = 1}, admin.Csrf))
                Assert.Equal(2, tts.Clones.Count)
                Assert.Equal("20", tts.Clones.Last()("gpt_cond_len"))
                Assert.Equal("1", tts.Clones.Last()("files"))

                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "DELETE", "/api/audio/voices/me", Nothing, admin.Csrf))
                Assert.DoesNotContain("me", tts.Voices)
                Assert.Null(Await env.Host.H.Audio.SourceAsync("me"))
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/audio/voices/me")).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheSpokenSummaryIsWrittenInTheVoicesPersonalityAndKeptWithTheAnswer() As Task
        Using up As New FakeUpstream(), tts As New FakeXtts()
            Dim summaryRequests As New Concurrent.ConcurrentQueue(Of JsonNode)
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), ChatFeatureTests.TextStream("## Done" & vbLf & "I changed `auth.js` and all 48 tests pass. Deploy now?", "stop"), Nothing)
            Dim slow = False
            up.TextOverride = Function(req)
                                  Dim sys = req("messages")(0)("content").ToString()
                                  If Not sys.Contains("tell the user out loud") Then Return Nothing
                                  summaryRequests.Enqueue(req)
                                  ' A model that takes far too long (a busy local GPU).
                                  If slow Then Thread.Sleep(12000)
                                  Return "<think>keep it short</think>TL;DR: ""**Fine.** It works, genius. Deploy it?"""
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.TextToSpeech.BaseUrl = tts.BaseUrl
                                                                o.TextToSpeech.ApiKey = "test-key"
                                                                o.TextToSpeech.SummaryTimeoutSeconds = 5
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Fix the login bug"})
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim answerId = chat("messages").AsArray().Last(Function(m) m("role").ToString() = "assistant")("id").ToString()

                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/profiles",
                    New With {.voice = "speaker:Damien Black", .summaryPrompt = "You are rude and impatient.", .settings = New With {.language = "fr"}}, admin.Csrf))
                Dim r = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/summary",
                    New With {.chatId = id, .messageId = answerId, .voice = "speaker:Damien Black", .words = 40}, admin.Csrf))
                Assert.Equal("Fine. It works, genius. Deploy it?", r("text").ToString())
                Assert.Equal("fast/a", r("model").ToString())
                Dim asked = summaryRequests.Single()
                Dim system = asked("messages")(0)("content").ToString()
                Assert.Contains("You are rude and impatient." & vbLf & vbLf & "In that voice", system)
                Assert.Contains("at most 40 words", system)
                Assert.Contains("Speak French", system)
                Dim user = asked("messages")(1)("content").ToString()
                Assert.Contains("Fix the login bug", user)
                Assert.Contains("all 48 tests pass", user)

                ' Kept with the answer: the chat shows it when it is opened again.
                chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim answer = chat("messages").AsArray().Single(Function(m) m("id").ToString() = answerId)
                Assert.Equal("Fine. It works, genius. Deploy it?", answer("spoken").ToString())
                Assert.Equal("speaker:Damien Black", answer("spokenVoice").ToString())

                ' Trying a personality before saving it: a text instead of an answer, and an unsaved prompt.
                Dim tryOut = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/summary",
                    New With {.text = "Everything is deployed.", .voice = "speaker:Ana Florence", .summaryPrompt = "You are a pirate.", .fallbackModel = "fast/a"}, admin.Csrf))
                Assert.Equal("Fine. It works, genius. Deploy it?", tryOut("text").ToString())
                Assert.Contains("You are a pirate." & vbLf & vbLf & "In that voice", summaryRequests.Last()("messages")(0)("content").ToString())
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/summary",
                    New With {.chatId = id, .messageId = "msg_nope"}, admin.Csrf)).StatusCode)

                ' No model writes it in time (SummaryTimeoutSeconds, all of them together): the answer's first sentences are
                ' spoken instead, at once, not charged and not kept, so a proxy never cuts the request off and Dictation mode
                ' always has something to say.
                slow = True
                Dim clock = Diagnostics.Stopwatch.StartNew()
                Dim late = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/summary",
                    New With {.chatId = id, .messageId = answerId, .voice = "speaker:Damien Black", .words = 40}, admin.Csrf))
                Assert.True(clock.Elapsed.TotalSeconds < 10, clock.Elapsed.TotalSeconds.ToString())
                Assert.True(late("fallback").GetValue(Of Boolean)())
                Assert.Equal("Done I changed auth.js and all 48 tests pass. Deploy now?", late("text").ToString())
                Assert.Equal(0, late("costMicros").GetValue(Of Long)())
                chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Assert.Equal("Fine. It works, genius. Deploy it?", chat("messages").AsArray().Single(Function(m) m("id").ToString() = answerId)("spoken").ToString())
                Assert.Equal("Done I changed auth.js and all 48 tests pass. Deploy now?", Global.Agentic.Work.ChatService.FirstSentences("## Done" & vbLf & "I changed `auth.js` and all 48 tests pass. Deploy now?", 60))
                Assert.Equal("One two three four five six seven eight nine ten.", Global.Agentic.Work.ChatService.FirstSentences("One two three four five six seven eight nine ten. Eleven twelve thirteen.", 10))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SpeechIsChargedAtThePricesAdministratorsSet() As Task
        Using up As New FakeUpstream(), tts As New FakeXtts()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.TextToSpeech.BaseUrl = tts.BaseUrl
                                                                o.TextToSpeech.ApiKey = "test-key"
                                                            End Sub)
            Using env.Host
                Dim h = env.Host.H
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' Free until an administrator sets prices; out-of-range prices are refused.
                Dim free = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/audio/pricing"))
                Assert.Equal("config", free("source").ToString())
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/pricing", New With {.perMinute = 5000}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/pricing", New With {.perMillionChars = -1}, admin.Csrf)).StatusCode)
                Dim set1 = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/audio/pricing",
                    New With {.perMillionChars = 1000, .perMinute = 6, .perVoiceUpload = 0.5}, admin.Csrf))
                Assert.Equal("admin:admin", set1("source").ToString())
                Assert.Equal(6D, set1("perMinute").GetValue(Of Decimal)())
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {New With {.model = "fast/a", .inputPerMillion = 1000, .outputPerMillion = 2000}}}, admin.Csrf)).IsSuccessStatusCode)

                ' An administrator is never charged.
                Dim adminRes = Await PlatformApiTests.Send(admin.Client, "POST", "/api/audio/tts", New With {.text = "Hello world"}, admin.Csrf)
                Await BytesOf(adminRes)
                Assert.Equal("0", adminRes.Headers.GetValues("X-Cost-Micros").Single())

                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users",
                    New With {.username = "payer", .password = "payer-pass-123", .role = "User", .permissions = PlatformApiTests.ChatUser.Concat({"chat.voice"}).ToArray()}, admin.Csrf)).StatusCode)
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/payer/balance", New With {.amount = 5}, admin.Csrf)
                Dim payer = Await PlatformApiTests.LoginAsync(env.Port, "payer", "payer-pass-123")
                Dim info = Await ChatFeatureTests.JsonOf(Await payer.Client.GetAsync("/api/audio"))
                Assert.True(info("account")("charged").GetValue(Of Boolean)())
                Assert.Equal(1000D, info("pricing")("perMillionChars").GetValue(Of Decimal)())
                Assert.False(info("canSetPrices").GetValue(Of Boolean)())
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(payer.Client, "PUT", "/api/audio/pricing", New With {.perMinute = 0}, payer.Csrf)).StatusCode)

                ' Text in and audio out: 11 characters × $1000 per 1M + 0.5 s × $6 per minute = 11,000 + 50,000 micro-USD.
                Dim speech = Await h.Accounts.WithSurchargeAsync("payer", 61000)
                Dim res = Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/tts", New With {.text = "Hello world", .save = True}, payer.Csrf)
                Await BytesOf(res)
                Assert.Equal(speech.ToString(), res.Headers.GetValues("X-Cost-Micros").Single())
                Assert.Equal(5000000L - speech, (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros)
                ' Streamed speech is charged when it ends, and the kept clip says what it cost.
                Await BytesOf(Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/stream", New With {.text = "Hello world", .save = True}, payer.Csrf))
                Assert.Equal(5000000L - 2 * speech, (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros)
                Dim clips = (Await ChatFeatureTests.JsonOf(Await payer.Client.GetAsync("/api/audio/clips"))).AsArray()
                Assert.All(clips, Sub(c) Assert.Equal(speech, c("costMicros").GetValue(Of Long)()))
                Dim ledger = Await h.Database.QueryAsync("SELECT EntryType, Model, Description FROM AccountLedger WHERE Username='payer' AND EntryType<>'Credit' ORDER BY Id",
                                                         Function(r) (Type:=r.GetString(0), Model:=r.GetString(1), Text:=r.GetString(2)))
                Assert.Equal(2, ledger.Count)
                Assert.All(ledger, Sub(l)
                                       Assert.Equal("Speech", l.Type)
                                       Assert.Equal("xtts", l.Model)
                                       Assert.Contains("11 characters, 0.5 s", l.Text)
                                   End Sub)

                ' Cloning a voice costs one voice upload.
                Dim clipBody As New ByteArrayContent(SpeechService.PcmToWav(tts.Pcm()))
                Dim stage As New HttpRequestMessage(HttpMethod.Post, "/api/audio/voice-clips?name=me.wav") With {.Content = clipBody}
                stage.Headers.Add("X-CSRF-Token", payer.Csrf)
                Dim clipId = (Await ChatFeatureTests.JsonOf(Await payer.Client.SendAsync(stage)))("id").ToString()
                Dim before = (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros
                Dim made = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/voices", New With {.name = "payer-voice", .clips = {clipId}, .consent = True}, payer.Csrf))
                Dim upload = Await h.Accounts.WithSurchargeAsync("payer", 500000)
                Assert.Equal(upload, made("costMicros").GetValue(Of Long)())
                Assert.Equal(before - upload, (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros)

                ' The spoken summary is a model call: its tokens at the model's price (5 in × $1000 + 3 out × $2000 per 1M).
                Dim chatId = Await ChatFeatureTests.NewChatAsync(payer, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(payer, chatId, New With {.content = "Hi"})
                Dim answerId = (Await ChatFeatureTests.GetChatAsync(payer, chatId))("messages").AsArray().Last(Function(m) m("role").ToString() = "assistant")("id").ToString()
                before = (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros
                Dim summary = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/summary", New With {.chatId = chatId, .messageId = answerId}, payer.Csrf))
                Dim tokens = Await h.Accounts.WithSurchargeAsync("payer", 11000)
                Assert.Equal(tokens, summary("costMicros").GetValue(Of Long)())
                Assert.Equal(before - tokens, (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros)

                ' A balance that does not cover the speech is refused before anything is made.
                Dim made1 = tts.Speech.Count
                Await h.Accounts.AdjustAsync("payer", 50000 - (Await h.Accounts.GetAccountAsync("payer")).BalanceMicros, "test", "admin")
                Dim refused = Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/tts", New With {.text = "Hello world"}, payer.Csrf)
                Assert.Equal(CType(402, HttpStatusCode), refused.StatusCode)
                Assert.Contains("BALANCE_EMPTY", Await refused.Content.ReadAsStringAsync())
                Assert.Equal(CType(402, HttpStatusCode), (Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/stream", New With {.text = "Hello world"}, payer.Csrf)).StatusCode)
                Assert.Equal(made1, tts.Speech.Count)
                ' An empty balance also stops the summary when its only model has a price.
                Await h.Accounts.AdjustAsync("payer", -50000, "test", "admin")
                Dim blocked = Await PlatformApiTests.Send(payer.Client, "POST", "/api/audio/summary", New With {.chatId = chatId, .messageId = answerId}, payer.Csrf)
                Assert.Equal(CType(402, HttpStatusCode), blocked.StatusCode)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ units

    <Fact>
    Public Sub SpeechRequestsAreCheckedSettingBySetting()
        Dim opt As New TextToSpeechOptions With {.MaxTextChars = 50}
        Dim langs = New List(Of String) From {"en", "es"}
        Dim input = JsonDocument.Parse("{""text"":"" Hi "",""speed"":""1.5"",""top_k"":40,""enable_text_splitting"":false}").RootElement
        Dim defaults As New JsonObject From {{"temperature", 0.65}, {"top_k", 10}, {"language", "es"}}
        Dim body = SpeechService.BuildRequest(input, "voice:me", defaults, opt, langs, streaming:=False)
        Assert.Equal("Hi", body("text").ToString())
        Assert.Equal("me", body("voice").ToString())
        Assert.Null(body("speaker"))
        Assert.Equal(1.5, body("speed").GetValue(Of Double)())
        Assert.Equal(40, body("top_k").GetValue(Of Integer)())
        Assert.Equal(0.65, body("temperature").GetValue(Of Double)())
        Assert.Equal("es", body("language").ToString())
        Assert.False(body("enable_text_splitting").GetValue(Of Boolean)())
        For Each bad In {"{""text"":""x"",""speed"":0}", "{""text"":""x"",""top_p"":1.5}", "{""text"":""x"",""top_k"":2.5}", "{""text"":""x"",""repetition_penalty"":0.5}",
                         "{""text"":""""}", "{""text"":""" & New String("a"c, 51) & """}", "{""text"":""x"",""language"":""de""}", "{""text"":""x"",""temperature"":""warm""}"}
            Assert.Throws(Of SpeechException)(Function() SpeechService.BuildRequest(JsonDocument.Parse(bad).RootElement, "speaker:A", Nothing, opt, langs, False))
        Next
        Assert.Throws(Of SpeechException)(Function() SpeechService.BuildRequest(JsonDocument.Parse("{""text"":""x"",""stream_chunk_size"":2}").RootElement, "speaker:A", Nothing, opt, langs, True))
        Assert.Equal("voice:me", SpeechService.RequestedVoice(JsonDocument.Parse("{""voice"":""me""}").RootElement, "speaker:B", opt))
        Assert.Equal("speaker:Damien Black", SpeechService.RequestedVoice(JsonDocument.Parse("{""voice"":""speaker:Damien Black""}").RootElement, Nothing, opt))
        Assert.Equal("speaker:B", SpeechService.RequestedVoice(JsonDocument.Parse("{}").RootElement, "speaker:B", opt))
        Assert.Equal("speaker:Ana Florence", SpeechService.RequestedVoice(JsonDocument.Parse("{}").RootElement, Nothing, New TextToSpeechOptions()))
    End Sub

    <Fact>
    Public Sub WavFilesRoundTripAndErrorsReadWell()
        Dim pcm = Enumerable.Range(0, 1000).Select(Function(i) CByte(i Mod 256)).ToArray()
        Dim wav = SpeechService.PcmToWav(pcm)
        Assert.Equal(44 + 1000, wav.Length)
        Assert.Equal(pcm, SpeechService.WavToPcm(wav))
        BitConverter.GetBytes(UInteger.MaxValue).CopyTo(wav, 40)
        Assert.Equal(pcm, SpeechService.WavToPcm(wav))
        Assert.Equal(500, SpeechService.PcmDurationMs(24000))
        Assert.Equal("temperature: Input should be less than or equal to 2", SpeechService.DetailOf("{""detail"":[{""loc"":[""body"",""temperature""],""msg"":""Input should be less than or equal to 2""}]}"))
        Assert.Equal("Unknown speaker", SpeechService.DetailOf("{""detail"":""Unknown speaker""}"))
        Assert.Equal("Hello there", ChatService.CleanSpoken("<think>x</think>TL;DR: ""Hello *there*""", 60))
        Assert.Equal("one two…", ChatService.CleanSpoken("one two three four five", 1))
    End Sub

    <Fact>
    Public Sub SpeechNeedsTheVoicePermissionAndTheKeyIsTheAdministrators()
        Assert.Equal("chat.voice", Permissions.ForRoute("POST", "/api/audio/tts"))
        Assert.Equal("chat.voice", Permissions.ForRoute("GET", "/api/audio"))
        Assert.Equal("chat.voice", Permissions.ForRoute("POST", "/api/audio/voices/{name}/renew"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("PUT", "/api/audio/key"))
        Assert.Equal("finance.manage", Permissions.ForRoute("PUT", "/api/audio/pricing"))
        Assert.Equal("finance.view|chat.voice", Permissions.ForRoute("GET", "/api/audio/pricing"))
        Dim p As New SpeechPrice With {.PerMillionChars = 15, .PerMinute = 0.6D, .PerVoiceUpload = 0.25D}
        Assert.Equal(15000L + 600000L, p.SpeechMicros(1000, 60000))
        Assert.Equal(250000L, p.VoiceUploadMicros())
        Assert.True(New SpeechPrice().IsFree)
        Assert.True(Permissions.IsKnown("chat.voice"))
    End Sub

End Class
