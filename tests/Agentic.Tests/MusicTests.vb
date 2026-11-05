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

''' <summary>A stand-in for the Suno API wrapper (server.py): its key, /health, /credits, /generate, /clips/status, audio, /lyrics and the feed.</summary>
Friend NotInheritable Class FakeSuno
    Implements IDisposable
    Private ReadOnly _listener As New HttpListener()
    Private ReadOnly _cts As New CancellationTokenSource()
    Private ReadOnly _clips As New Concurrent.ConcurrentDictionary(Of String, (Body As JsonNode, Polls As Integer, Final As String))
    Private _next As Integer
    Public ReadOnly Property Port As Integer
    Public Property Key As String = "suno-key"
    Public ReadOnly Property Generated As New Concurrent.ConcurrentQueue(Of JsonNode)
    ''' <summary>The second song of the next request fails; or both.</summary>
    Public Property FailOne As Boolean
    Public Property FailAll As Boolean
    ''' <summary>Songs stay "streaming".</summary>
    Public Property Hold As Boolean
    ''' <summary>The Suno account's downloads left (each first download of a song uses one).</summary>
    Public Property DownloadsLeft As Integer = 7
    ''' <summary>The songs downloaded (official downloads, in order).</summary>
    Public ReadOnly Property Downloads As New Concurrent.ConcurrentQueue(Of String)
    Private ReadOnly _unlocked As New Concurrent.ConcurrentDictionary(Of String, Boolean)

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

    ''' <summary>A song's official download: an MP3 (ID3 tag first).</summary>
    Public Shared Function Audio(id As String) As Byte()
        Return Encoding.ASCII.GetBytes("ID3" & ChrW(4) & ChrW(0)).Concat(Enumerable.Repeat(CByte(Math.Abs(id.GetHashCode()) Mod 251), 4000)).ToArray()
    End Function

    ''' <summary>A song of the Suno library that was made elsewhere (for the import).</summary>
    Public Sub AddOld(id As String)
        _clips(id) = (JsonNode.Parse("{""title"":""Old Song"",""tags"":""jazz""}"), 99, "complete")
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

    Private Function Status(id As String, Optional advance As Boolean = False) As String
        Dim c As (Body As JsonNode, Polls As Integer, Final As String) = Nothing
        If Not _clips.TryGetValue(id, c) Then Return "missing"
        If advance Then
            c = (c.Body, c.Polls + 1, c.Final)
            _clips(id) = c
        End If
        If c.Polls < 1 Then Return "submitted"
        If c.Polls < 2 OrElse (Hold AndAlso c.Final <> "complete" AndAlso c.Polls < 99) OrElse (Hold AndAlso c.Polls < 99) Then Return "streaming"
        Return c.Final
    End Function

    Private Function Info(id As String, st As String) As JsonObject
        Dim c As (Body As JsonNode, Polls As Integer, Final As String) = Nothing
        _clips.TryGetValue(id, c)
        Dim b = c.Body
        Return New JsonObject From {
            {"id", id}, {"title", If(b?("title")?.ToString(), "")}, {"status", st}, {"audio_url", "https://cdn1.suno.ai/" & id & ".m4a"},
            {"image_url", "http://127.0.0.1/" & id & ".jpeg"}, {"duration", 123.4}, {"tags", If(b?("tags")?.ToString(), "")},
            {"lyrics", If(String.IsNullOrEmpty(b?("lyrics")?.ToString()), "[Verse]" & vbLf & "made by Suno", b("lyrics").ToString())}, {"instrumental", If(b?("instrumental")?.DeepClone(), False)},
            {"model", "chirp-goose"}, {"error", If(st = "error", "Content moderation", Nothing)}}
    End Function

    Private Async Function HandleAsync(ctx As HttpListenerContext) As Task
        Dim req = ctx.Request, res = ctx.Response
        Dim path = req.Url.AbsolutePath
        If path = "/health" Then
            Await Json(res, 200, "{""ok"":true,""token_expires_in"":3000}")
            Return
        End If
        If req.Headers("X-API-Key") <> Key Then
            Await Json(res, 401, "{""detail"":""Invalid or missing API key""}")
            Return
        End If
        Dim body As String
        Using sr As New IO.StreamReader(req.InputStream)
            body = Await sr.ReadToEndAsync()
        End Using
        If path = "/credits" Then
            Await Json(res, 200, $"{{""available_credits"":50,""monthly_usage"":10,""monthly_limit"":50,""downloads_left"":{DownloadsLeft},""pending_batches"":[]}}")
        ElseIf path = "/generate" Then
            Dim b = JsonNode.Parse(body)
            Generated.Enqueue(b)
            Dim n = Interlocked.Increment(_next)
            Dim ids = {$"clip{n}a", $"clip{n}b"}
            _clips(ids(0)) = (b, 0, If(FailAll, "error", "complete"))
            _clips(ids(1)) = (b, 0, If(FailAll OrElse FailOne, "error", "complete"))
            Await Json(res, 200, $"{{""batch_id"":""batch{n}"",""clip_ids"":[""{ids(0)}"",""{ids(1)}""],""status"":[""submitted"",""submitted""]}}")
        ElseIf path = "/clips/status" Then
            Dim arr As New JsonArray()
            For Each id In req.QueryString("ids").Split(","c)
                Dim st = Status(id, advance:=True)
                arr.Add(If(st = "missing", New JsonObject From {{"id", id}, {"status", "missing"}}, Info(id, st)))
            Next
            Await Json(res, 200, arr.ToJsonString())
        ElseIf path.StartsWith("/clip/", StringComparison.Ordinal) AndAlso path.EndsWith("/audio", StringComparison.Ordinal) Then
            ' Suno scrambles the stream its own player plays.
            Await Json(res, 409, "{""detail"":""Suno's stream of this song is scrambled for its own player: use GET /clip/{id}/download""}")
        ElseIf path.StartsWith("/clip/", StringComparison.Ordinal) AndAlso path.EndsWith("/download", StringComparison.Ordinal) Then
            Dim id = path.Split("/"c)(2)
            If Status(id) <> "complete" Then
                Await Json(res, 409, "{""detail"":""not complete""}")
            ElseIf Not _unlocked.ContainsKey(id) AndAlso DownloadsLeft <= 0 Then
                Await Json(res, 402, "{""detail"":""Suno refused the download (402): No downloads remaining""}")
            Else
                If _unlocked.TryAdd(id, True) Then DownloadsLeft -= 1
                Downloads.Enqueue(id)
                Await WriteAsync(res, 200, "audio/mpeg", Audio(id))
            End If
        ElseIf path = "/lyrics" Then
            Dim instruction = JsonNode.Parse(body)("instruction").ToString()
            If instruction.Length < 10 Then
                Await Json(res, 502, "{""detail"":""lyrics failed 422: {\""detail\"": \""Prompt likely malformed\""}""}")
            Else
                Await Json(res, 200, "{""lyrics_request_id"":""r1"",""lyrics_id"":""l1"",""edited_lyrics"":""[Verse]\nRain on the window\n\n[Chorus]\nMidnight city""}")
            End If
        ElseIf path = "/clips" Then
            Dim arr As New JsonArray()
            For Each kv In _clips
                Dim st = Status(kv.Key)
                arr.Add(New JsonObject From {{"id", kv.Key}, {"title", If(kv.Value.Body?("title")?.ToString(), "")}, {"status", st},
                                             {"media_urls", New JsonArray(New JsonObject From {{"url", "https://cdn1.suno.ai/" & kv.Key & ".m4a"}})},
                                             {"model_name", "chirp-goose"}, {"metadata", New JsonObject From {{"tags", If(kv.Value.Body?("tags")?.ToString(), "")}, {"duration", 99.5}, {"prompt", "old lyrics"}}}})
            Next
            Await Json(res, 200, arr.ToJsonString())
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
''' Songs through the Suno wrapper: its key stays here, a request reaches Suno as asked, it is followed here until both
''' songs are in the library and paid for (per song; AI lyrics per lyrics), failed songs are not charged, and the Suno
''' account's own library can be taken in by an administrator.
''' </summary>
Public Class MusicTests

    Private Shared Async Function WaitJobAsync(user As (Client As HttpClient, Csrf As String), id As String, Optional status As String = Nothing) As Task(Of JsonNode)
        For i = 0 To 200
            Dim j = Await ChatFeatureTests.JsonOf(Await user.Client.GetAsync("/api/music/jobs/" & id))
            Dim st = j("status").ToString()
            If If(status Is Nothing, st = "done" OrElse st = "failed", st = status) Then Return j
            Await Task.Delay(100)
        Next
        Throw New TimeoutException("The request did not end.")
    End Function

    Private Shared Function JobId(res As HttpResponseMessage) As String
        Return JsonNode.Parse(res.Content.ReadAsStringAsync().Result)("id").ToString()
    End Function

    <Fact>
    Public Async Function SongsAreMadeKeptAndPaidFor() As Task
        Using up As New FakeUpstream(), suno As New FakeSuno()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Music.BaseUrl = suno.BaseUrl
                                                                o.Music.PollSeconds = 1
                                                            End Sub)
            Using env.Host
                Dim h = env.Host.H
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' Without the wrapper's key it says so; the administrator sets it (vault, no agent may use it).
                Dim info = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/music"))
                Assert.False(info("ready").GetValue(Of Boolean)())
                Assert.Contains("API key", info("problem").ToString())
                Assert.True((Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/music/key", New With {.apiKey = "suno-key"}, admin.Csrf)))("ok").GetValue(Of Boolean)())
                Assert.Empty((Await h.Secrets.GetInfoAsync("suno-api-key")).AgentIds)
                Dim infoText = Await admin.Client.GetStringAsync("/api/music")
                Assert.DoesNotContain("suno-key", infoText)
                info = JsonNode.Parse(infoText)
                Assert.True(info("ready").GetValue(Of Boolean)())
                Assert.Equal(50, info("credits")("available_credits").GetValue(Of Integer)())
                Assert.Equal("chirp-goose", info("models")(0)("id").ToString())

                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/music/pricing", New With {.perSong = 0.05, .perLyrics = 0.01}, admin.Csrf))
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users",
                    New With {.username = "singer", .password = "singer-pass-123", .role = "User", .permissions = PlatformApiTests.ChatUser.Concat({"music.suno"}).ToArray()}, admin.Csrf)).StatusCode)
                Await PlatformApiTests.Send(admin.Client, "POST", "/api/users/singer/balance", New With {.amount = 5}, admin.Csrf)
                Dim singer = Await PlatformApiTests.LoginAsync(env.Port, "singer", "singer-pass-123")
                ' The Suno account's credits and library are the administrator's.
                Assert.Null((Await ChatFeatureTests.JsonOf(Await singer.Client.GetAsync("/api/music")))("credits"))
                Assert.Equal(HttpStatusCode.Forbidden, (Await singer.Client.GetAsync("/api/music/suno")).StatusCode)

                ' AI lyrics: charged per lyrics; Suno's terse refusal explained.
                Dim lyrics = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/lyrics", New With {.instruction = "a lo-fi song about rain at midnight"}, singer.Csrf))
                Assert.StartsWith("[Verse]", lyrics("lyrics").ToString())
                Dim lyricsCost = Await h.Accounts.WithSurchargeAsync("singer", 10000)
                Assert.Equal(lyricsCost, lyrics("costMicros").GetValue(Of Long)())
                Dim refused = Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/lyrics", New With {.instruction = "rain"}, singer.Csrf)
                Assert.Equal(HttpStatusCode.BadGateway, refused.StatusCode)
                Assert.Contains("describe it in more words", Await refused.Content.ReadAsStringAsync())

                ' A custom song: exactly what was asked reaches Suno; both songs come back into the library.
                Dim started = Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/jobs",
                    New With {.title = "Neon Ghosts", .tags = "synthwave, dreamy", .lyrics = lyrics("lyrics").ToString(), .model = "chirp-goose"}, singer.Csrf)
                Assert.Equal(HttpStatusCode.Accepted, started.StatusCode)
                Dim sent = suno.Generated.Last()
                Assert.Equal("Neon Ghosts", sent("title").ToString())
                Assert.Equal("synthwave, dreamy", sent("tags").ToString())
                Assert.StartsWith("[Verse]", sent("lyrics").ToString())
                Assert.False(sent("instrumental").GetValue(Of Boolean)())
                Dim job = Await WaitJobAsync(singer, JobId(started))
                Assert.Equal("done", job("status").ToString())
                Assert.Equal(2, job("songIds").AsArray().Count)
                Assert.All(job("clips").AsArray(), Sub(c) Assert.Equal("complete", c("status").ToString()))
                Dim songCost = Await h.Accounts.WithSurchargeAsync("singer", 100000)
                Assert.Equal(songCost, job("costMicros").GetValue(Of Long)())
                Assert.Equal(5000000L - lyricsCost - songCost, (Await h.Accounts.GetAccountAsync("singer")).BalanceMicros)
                Dim songs = (Await ChatFeatureTests.JsonOf(Await singer.Client.GetAsync("/api/music/songs?job=" & job("id").ToString()))).AsArray()
                Assert.Equal(2, songs.Count)
                Dim song = songs(0)
                Assert.Equal("Neon Ghosts", song("title").ToString())
                Assert.Equal(123400, song("durationMs").GetValue(Of Long)())
                Assert.False(song("hasCover").GetValue(Of Boolean)())   ' covers come only from Suno's own https hosts
                Dim songId = song("id").ToString()
                ' Kept without audio: Suno's official download (one of its few) happens at the first play, once.
                Assert.False(song("hasAudio").GetValue(Of Boolean)())
                Assert.Equal(0, song("bytes").GetValue(Of Long)())
                Assert.Empty(suno.Downloads)
                Dim plays = Await Task.WhenAll(singer.Client.GetByteArrayAsync($"/api/music/songs/{songId}/audio"), singer.Client.GetByteArrayAsync($"/api/music/songs/{songId}/audio?v=0"))
                Assert.All(plays, Sub(a) Assert.Equal(FakeSuno.Audio(song("clipId").ToString()), a))
                Assert.Single(suno.Downloads)
                Dim played = Await ChatFeatureTests.JsonOf(Await singer.Client.GetAsync($"/api/music/songs/{songId}"))
                Assert.True(played("hasAudio").GetValue(Of Boolean)())
                Assert.Equal("audio/mpeg", played("mime").ToString())
                Dim ranged As New HttpRequestMessage(HttpMethod.Get, $"/api/music/songs/{songId}/audio")
                ranged.Headers.Range = New Headers.RangeHeaderValue(0, 99)
                Dim part = Await singer.Client.SendAsync(ranged)
                Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode)
                Assert.Equal(100, (Await part.Content.ReadAsByteArrayAsync()).Length)
                Assert.Single(suno.Downloads)
                Dim asFile = Await singer.Client.GetAsync($"/api/music/songs/{songId}/audio?download=1")
                Assert.EndsWith(".mp3", asFile.Content.Headers.ContentDisposition.FileName.Trim(""""c))
                ' The other song by POST fetch; with no downloads left, Suno's refusal is said plainly.
                Dim other = songs(1)("id").ToString()
                suno.DownloadsLeft = 0
                Dim refusedDownload = Await PlatformApiTests.Send(singer.Client, "POST", $"/api/music/songs/{other}/fetch", Nothing, singer.Csrf)
                Assert.Equal(CType(402, HttpStatusCode), refusedDownload.StatusCode)
                Dim why = Await refusedDownload.Content.ReadAsStringAsync()
                Assert.Contains("SUNO_NO_DOWNLOADS", why)
                Assert.Contains("no downloads left", why)
                suno.DownloadsLeft = 1
                Dim fetched = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(singer.Client, "POST", $"/api/music/songs/{other}/fetch", Nothing, singer.Csrf))
                Assert.True(fetched("hasAudio").GetValue(Of Boolean)())
                Assert.Equal(2, suno.Downloads.Count)
                Dim ledger = Await h.Database.QueryAsync("SELECT EntryType, Model FROM AccountLedger WHERE Username='singer' AND EntryType<>'Credit' ORDER BY Id", Function(r) (r.GetString(0), r.GetString(1)))
                Assert.Equal({("Music", "suno-lyrics"), ("Music", "chirp-goose")}, ledger.ToArray())

                ' Rename, star, clear the unstarred.
                Dim renamed = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(singer.Client, "PATCH", "/api/music/songs/" & songId, New With {.title = "Neon Ghosts (take 1)", .starred = True}, singer.Csrf))
                Assert.Equal("Neon Ghosts (take 1)", renamed("title").ToString())
                Assert.True(renamed("starred").GetValue(Of Boolean)())
                Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(singer.Client, "DELETE", "/api/music/songs", Nothing, singer.Csrf))
                Assert.Equal(songId, (Await ChatFeatureTests.JsonOf(Await singer.Client.GetAsync("/api/music/songs"))).AsArray().Single()("id").ToString())

                ' An instrumental with only a style: the style describes it, and there are no lyrics.
                Dim inst = Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/jobs", New With {.tags = "ambient piano", .lyrics = "ignored", .instrumental = True}, singer.Csrf)
                sent = suno.Generated.Last()
                Assert.Equal("ambient piano", sent("prompt").ToString())
                Assert.Equal("", sent("lyrics").ToString())
                Assert.True(sent("instrumental").GetValue(Of Boolean)())
                Await WaitJobAsync(singer, JobId(inst))

                ' One song failing: the other is kept and only it is paid for; all failing: nothing is paid.
                Dim before = (Await h.Accounts.GetAccountAsync("singer")).BalanceMicros
                suno.FailOne = True
                Dim half = Await WaitJobAsync(singer, JobId(Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/jobs", New With {.prompt = "a happy song"}, singer.Csrf)))
                suno.FailOne = False
                Assert.Equal("done", half("status").ToString())
                Assert.Single(half("songIds").AsArray())
                Assert.Contains("Content moderation", half("error").ToString())
                Dim one = Await h.Accounts.WithSurchargeAsync("singer", 50000)
                Assert.Equal(before - one, (Await h.Accounts.GetAccountAsync("singer")).BalanceMicros)
                suno.FailAll = True
                Dim none = Await WaitJobAsync(singer, JobId(Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/jobs", New With {.prompt = "a sad song"}, singer.Csrf)))
                suno.FailAll = False
                Assert.Equal("failed", none("status").ToString())
                Assert.Equal(before - one, (Await h.Accounts.GetAccountAsync("singer")).BalanceMicros)

                ' What Suno does not take is refused here.
                For Each bad In New Object() {New With {.prompt = ""}, New With {.prompt = "x", .model = "bad id!"}, New With {.prompt = "x", .title = New String("t"c, 201)},
                                              New With {.lyrics = New String("l"c, 6001)}}
                    Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/jobs", bad, singer.Csrf)).StatusCode)
                Next
                ' A balance that does not cover two songs is refused before Suno is asked.
                Await h.Accounts.AdjustAsync("singer", 1000 - (Await h.Accounts.GetAccountAsync("singer")).BalanceMicros, "test", "admin")
                Dim asked = suno.Generated.Count
                Assert.Equal(CType(402, HttpStatusCode), (Await PlatformApiTests.Send(singer.Client, "POST", "/api/music/jobs", New With {.prompt = "z"}, singer.Csrf)).StatusCode)
                Assert.Equal(asked, suno.Generated.Count)
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync("/api/music/jobs/" & job("id").ToString())).StatusCode)

                ' The administrator takes a song of the Suno account's own library into theirs.
                suno.AddOld("oldclip1")
                Dim library = (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/music/suno"))).AsArray()
                Dim old = library.Single(Function(c) c("id").ToString() = "oldclip1")
                Assert.False(old("inLibrary").GetValue(Of Boolean)())
                Assert.Equal("jazz", old("tags").ToString())
                Dim imported = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/music/import", New With {.clipIds = {"oldclip1"}}, admin.Csrf))
                Assert.Equal("Old Song", imported("added")(0)("title").ToString())
                Assert.True((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/music/suno"))).AsArray().Single(Function(c) c("id").ToString() = "oldclip1")("inLibrary").GetValue(Of Boolean)())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function RequestsAreFollowedAgainAfterARestartAndTheirLimitHolds() As Task
        Using up As New FakeUpstream(), suno As New FakeSuno()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Music.BaseUrl = suno.BaseUrl
                                                                o.Music.ApiKey = "suno-key"
                                                                o.Music.PollSeconds = 1
                                                                o.Music.MaxActiveJobsPerUser = 1
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                suno.Hold = True
                Dim first = JobId(Await PlatformApiTests.Send(admin.Client, "POST", "/api/music/jobs", New With {.prompt = "one"}, admin.Csrf))
                Assert.Equal(CType(429, HttpStatusCode), (Await PlatformApiTests.Send(admin.Client, "POST", "/api/music/jobs", New With {.prompt = "two"}, admin.Csrf)).StatusCode)
                suno.Hold = False
                Assert.Equal("done", (Await WaitJobAsync(admin, first))("status").ToString())

                ' A request the host was following when it stopped is followed again when it starts.
                Await env.Host.H.Database.ExecuteAsync("UPDATE MusicJobs SET Status='making', SongIdsJson=NULL, FinishedUtc=NULL, CreatedUtc=@t WHERE Id=@id",
                                                       ("@t", CObj(Global.Agentic.Persistence.Db.Ts(DateTime.UtcNow))), ("@id", first))
                Await env.Host.H.Database.ExecuteAsync("DELETE FROM MusicSongs WHERE JobId=@id", ("@id", CObj(first)))
                env.Host.H.MusicJobs.Start(CancellationToken.None)
                Await WaitJobAsync(admin, first, "making")
                Dim again = Await WaitJobAsync(admin, first)
                Assert.Equal("done", again("status").ToString())
                Dim kept = (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/music/songs?job=" & first))).AsArray()
                Assert.Equal(2, kept.Count)

                ' Audio kept before 2.0.8 (Suno's scrambled stream) goes at the start, so its next play downloads it properly;
                ' a playable one stays.
                Dim good = kept(0)("id").ToString(), bad = kept(1)("id").ToString()
                Await admin.Client.GetByteArrayAsync($"/api/music/songs/{good}/audio")
                Dim badSong = Await env.Host.H.MusicStore.SongAsync("admin", bad)
                Await env.Host.H.MusicStore.SetAudioAsync(badSong, Enumerable.Repeat(CByte(&H16), 5000).ToArray(), "audio/mp4")
                Assert.Equal(1, Await env.Host.H.MusicStore.DropUnplayableAudioAsync())
                Assert.False((Await env.Host.H.MusicStore.SongAsync("admin", bad)).HasAudio)
                Assert.True((Await env.Host.H.MusicStore.SongAsync("admin", good)).HasAudio)
                Assert.True(MusicService.Playable(FakeSuno.Audio("x"), 12))
                Assert.False(MusicService.Playable(Enumerable.Repeat(CByte(&H16), 16).ToArray(), 16))
            End Using
        End Using
    End Function

    <Fact>
    Public Sub RequestsAndClipsAreReadAndChecked()
        Dim simple = MusicService.BuildRequest(JsonDocument.Parse("{""prompt"":"" a song "",""instrumental"":false}").RootElement, "chirp-goose")
        Assert.Equal("a song", simple("prompt").ToString())
        Assert.Equal("chirp-goose", simple("model").ToString())
        Assert.Throws(Of MusicException)(Function() MusicService.BuildRequest(JsonDocument.Parse("{}").RootElement, "m"))
        Assert.Equal("done", MusicService.Stage("complete"))
        Assert.Equal("failed", MusicService.Stage("error"))
        Assert.Equal("pending", MusicService.Stage("streaming"))
        Assert.Equal("pending", MusicService.Stage("missing"))
        Dim raw = JsonNode.Parse("{""id"":""c1"",""title"":""T"",""status"":""complete"",""media_urls"":[{""url"":""https://x/a.m4a""}],""image_url"":""https://cdn2.suno.ai/i.jpeg"",""model_name"":""chirp-goose"",""metadata"":{""tags"":""pop"",""duration"":12.5,""prompt"":""la la""}}").AsObject()
        Dim v = MusicService.ClipView(raw)
        Assert.Equal("https://x/a.m4a", v("audio_url").ToString())
        Assert.Equal("la la", v("lyrics").ToString())
        Assert.Equal(12.5, MusicService.SecondsOf(v))
        Assert.Null(MusicService.CoverAsync("http://cdn2.suno.ai/i.jpeg", CancellationToken.None).Result)
        Assert.Null(MusicService.CoverAsync("https://evil.example.com/i.jpeg", CancellationToken.None).Result)
        Dim p As New MusicPrice With {.PerSong = 0.05D, .PerLyrics = 0.01D}
        Assert.Equal(100000L, p.SongsMicros(2))
        Assert.Equal(10000L, p.LyricsMicros())
        Assert.Equal("music.suno", Permissions.ForRoute("POST", "/api/music/jobs"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("GET", "/api/music/suno"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("POST", "/api/music/import"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("PUT", "/api/music/key"))
        Assert.Equal("finance.manage", Permissions.ForRoute("PUT", "/api/music/pricing"))
    End Sub

End Class
