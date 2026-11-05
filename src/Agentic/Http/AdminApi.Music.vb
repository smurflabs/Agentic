Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: songs from Suno through the local Suno API wrapper (Music), the Music tab. Requests are sent to Suno and
    ' followed here (MusicJobs), so their songs reach the library and are paid for even when the page was closed. The
    ' wrapper's key never leaves here.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterMusic(r As Router)
            r.Map("GET", "/api/music", AuthLevel.Read, AddressOf MusicInfo)
            r.Map("POST", "/api/music/jobs", AuthLevel.SubmitTask, AddressOf MusicJobStart)
            r.Map("GET", "/api/music/jobs", AuthLevel.Read, AddressOf MusicJobsList)
            r.Map("GET", "/api/music/jobs/{jobId}", AuthLevel.Read, AddressOf MusicJob)
            r.Map("POST", "/api/music/lyrics", AuthLevel.SubmitTask, AddressOf MusicLyrics)
            r.Map("GET", "/api/music/songs", AuthLevel.Read, AddressOf MusicSongs)
            r.Map("DELETE", "/api/music/songs", AuthLevel.SubmitTask, AddressOf MusicSongsClear)
            r.Map("GET", "/api/music/songs/{songId}", AuthLevel.Read, AddressOf MusicSong)
            r.Map("GET", "/api/music/songs/{songId}/audio", AuthLevel.Read, AddressOf MusicSongAudio)
            r.Map("POST", "/api/music/songs/{songId}/fetch", AuthLevel.SubmitTask, AddressOf MusicSongFetch)
            r.Map("GET", "/api/music/songs/{songId}/cover", AuthLevel.Read, AddressOf MusicSongCover)
            r.Map("PATCH", "/api/music/songs/{songId}", AuthLevel.SubmitTask, AddressOf MusicSongPatch)
            r.Map("DELETE", "/api/music/songs/{songId}", AuthLevel.SubmitTask, AddressOf MusicSongDelete)
            r.Map("GET", "/api/music/suno", AuthLevel.Admin, AddressOf MusicSunoLibrary)
            r.Map("POST", "/api/music/import", AuthLevel.Admin, AddressOf MusicImport)
            r.Map("PUT", "/api/music/key", AuthLevel.Admin, AddressOf MusicKeyPut)
            r.Map("GET", "/api/music/pricing", AuthLevel.Read, AddressOf MusicPricing)
            r.Map("PUT", "/api/music/pricing", AuthLevel.Admin, AddressOf MusicPricingPut)
        End Sub

        Private Sub RequireMusic()
            If _h.Music Is Nothing OrElse Not _h.Music.Configured Then Throw New ApiException(409, "MUSIC_NOT_SET_UP", "No Suno wrapper is set up (Music.BaseUrl in the configuration).")
        End Sub

        Private Shared Function MusicError(ex As MusicException) As ApiException
            Dim code = If(ex.Status = 400, "SUNO_BAD_REQUEST", If(ex.Status = 404, "SUNO_NOT_FOUND", If(ex.Status = 409, "MUSIC_NOT_SET_UP", If(ex.Status = 504, "SUNO_TIMEOUT",
                          If(ex.Status = 402, "SUNO_NO_DOWNLOADS", "SUNO_FAILED")))))
            Return New ApiException(ex.Status, code, ex.Message)
        End Function

        Private Function MusicPriceAsync() As Task(Of MusicPrice)
            Return _h.MusicStore.PricingAsync(_h.Options.Music)
        End Function

        Private Shared Function MusicPriceDto(p As MusicPrice) As Object
            Return New With {.perSong = p.PerSong, .perLyrics = p.PerLyrics, .source = p.Source, .effectiveFromUtc = p.EffectiveFromUtc}
        End Function

        Private Shared Function SongDto(s As MusicSong) As Object
            Return New With {.id = s.Id, .jobId = s.JobId, .clipId = s.ClipId, .title = s.Title, .tags = s.Tags, .lyrics = s.Lyrics, .description = s.Description,
                             .instrumental = s.Instrumental, .model = s.Model, .durationMs = s.DurationMs, .mime = s.Mime, .bytes = s.Bytes, .hasCover = s.HasCover,
                             .hasAudio = s.HasAudio, .starred = s.Starred, .createdUtc = s.CreatedUtc}
        End Function

        ''' <summary>The song with its audio: downloaded from Suno the first time (one of the Suno account's downloads).</summary>
        Private Async Function SongWithAudioAsync(rc As RequestContext, s As MusicSong) As Task(Of MusicSong)
            If s.HasAudio Then Return s
            RequireMusic()
            Try
                Return Await _h.MusicJobs.EnsureAudioAsync(s, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As MusicException
                Throw MusicError(ex)
            End Try
        End Function

        ''' <summary>Gets a song's audio from Suno now (its first play): answers the song.</summary>
        Private Async Function MusicSongFetch(rc As RequestContext) As Task
            Dim s = Await SongWithAudioAsync(rc, Await SongOrThrowAsync(rc).ConfigureAwait(False)).ConfigureAwait(False)
            Await rc.OkAsync(SongDto(s)).ConfigureAwait(False)
        End Function

        ''' <summary>Everything the tab needs at once: whether Suno works, the models, prices and the account (administrators: the Suno credits too).</summary>
        Private Async Function MusicInfo(rc As RequestContext) As Task
            Dim m = _h.Music
            Dim opt = _h.Options.Music
            Dim configured = m IsNot Nothing AndAlso m.Configured
            Dim health As JsonNode = Nothing, credits As JsonNode = Nothing
            Dim problem As String = Nothing
            If configured Then
                Try
                    health = Await m.HealthAsync(rc.CancellationToken).ConfigureAwait(False)
                    If health?("ok")?.ToString() <> "true" Then problem = "Suno's sign-in does not work: " & If(health?("error")?.ToString(), "unknown") & " (run login.py in the suno folder on the server)."
                    If problem Is Nothing AndAlso rc.User.IsAdmin Then credits = Await m.CreditsAsync(rc.CancellationToken).ConfigureAwait(False)
                Catch ex As MusicException
                    problem = ex.Message
                End Try
            Else
                problem = "No Suno wrapper is set up (Music.BaseUrl in the configuration)."
            End If
            Dim acc = If(rc.User.ViaSession OrElse rc.User.ViaApiKey, Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False), Nothing)
            Dim surcharged = acc IsNot Nothing AndAlso acc.Charged AndAlso Await _h.Accounts.HasSurchargeAsync(rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .available = configured, .ready = configured AndAlso problem Is Nothing, .problem = problem, .isAdmin = rc.User.IsAdmin,
                .baseUrl = If(rc.User.IsAdmin AndAlso configured, m.BaseUrl, Nothing), .keyInConfig = rc.User.IsAdmin AndAlso configured AndAlso m.KeyInConfig,
                .keySet = configured AndAlso rc.User.IsAdmin AndAlso (Await m.KeyAsync().ConfigureAwait(False)) IsNot Nothing,
                .keySecret = If(rc.User.IsAdmin, opt.ApiKeySecret, Nothing),
                .credits = credits,
                .models = opt.Models.Select(Function(kv) New With {.id = kv.Key, .name = kv.Value}).ToList(), .defaultModel = opt.DefaultModel,
                .defaults = New With {.maxSongs = opt.MaxSongsPerUser, .maxActiveJobs = opt.MaxActiveJobsPerUser, .jobTimeoutSeconds = opt.JobTimeoutSeconds},
                .pricing = MusicPriceDto(Await MusicPriceAsync().ConfigureAwait(False)),
                .account = New With {.charged = acc IsNot Nothing AndAlso acc.Charged, .balanceMicros = If(acc?.BalanceMicros, 0L),
                                     .surchargePercent = If(surcharged, Global.Agentic.Economy.AccountService.SurchargePercent, 0)},
                .canSetPrices = rc.User.IsAdmin OrElse rc.User.HasPermission("finance.manage")}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {prompt, tags, title, lyrics, instrumental, model}: Suno makes two songs; answers 202 with the request (follow it at /api/music/jobs/{id}).</summary>
        Private Async Function MusicJobStart(rc As RequestContext) As Task
            RequireMusic()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim opt = _h.Options.Music
            If _h.MusicJobs.ActiveCount(rc.User.Name) >= Math.Max(1, opt.MaxActiveJobsPerUser) Then
                Throw New ApiException(429, "TOO_MANY_JOBS", $"You already have {opt.MaxActiveJobsPerUser} songs being made: wait for one to finish.")
            End If
            Dim body As JsonObject
            Try
                body = MusicService.BuildRequest(b, opt.DefaultModel)
            Catch ex As MusicException
                Throw MusicError(ex)
            End Try
            ' Suno makes two songs per request.
            Await RequireBalanceAsync(rc, (Await MusicPriceAsync().ConfigureAwait(False)).SongsMicros(2), "two songs").ConfigureAwait(False)
            Dim job As MusicJob
            Try
                job = Await _h.MusicJobs.StartJobAsync(rc.User.Name, body, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As MusicException
                Throw MusicError(ex)
            End Try
            Await rc.WriteJsonAsync(202, job).ConfigureAwait(False)
        End Function

        Private Async Function MusicJobsList(rc As RequestContext) As Task
            Await rc.OkAsync(Await _h.MusicJobs.ListAsync(rc.User.Name, rc.QueryInt("limit", 20, 1, 200)).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function MusicJob(rc As RequestContext) As Task
            Dim job = Await _h.MusicJobs.GetAsync(rc.User.Name, rc.Route("jobId")).ConfigureAwait(False)
            If job Is Nothing Then Throw ApiException.NotFound("JOB_NOT_FOUND", "That request was not found.")
            Await rc.OkAsync(job).ConfigureAwait(False)
        End Function

        ''' <summary>Body {instruction}: lyrics written by Suno (charged per lyrics).</summary>
        Private Async Function MusicLyrics(rc As RequestContext) As Task
            RequireMusic()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim instruction = If(JsonUtil.GetString(b, "instruction"), "").Trim()
            If instruction.Length < 3 Then Throw ApiException.BadRequest("Say what the song is about.")
            If instruction.Length > 3000 Then Throw ApiException.BadRequest("The description has at most 3,000 characters.")
            Dim price = Await MusicPriceAsync().ConfigureAwait(False)
            Await RequireBalanceAsync(rc, price.LyricsMicros(), "AI lyrics").ConfigureAwait(False)
            Dim text As String
            Try
                text = Await _h.Music.LyricsAsync(instruction, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As MusicException
                Throw MusicError(ex)
            End Try
            Dim cost As Long? = Nothing
            If price.LyricsMicros() > 0 AndAlso Not rc.User.IsAdmin Then
                Dim after = Await _h.Accounts.ChargeAsync(rc.User.Name, price.LyricsMicros(), Global.Agentic.Economy.AccountService.EntryMusic, "suno-lyrics", Nothing, Nothing, Nothing,
                                                          "Suno lyrics: " & TextUtil.Truncate(instruction, 70, "…")).ConfigureAwait(False)
                If after.HasValue Then cost = Await _h.Accounts.WithSurchargeAsync(rc.User.Name, price.LyricsMicros()).ConfigureAwait(False)
            End If
            Await rc.OkAsync(New With {.lyrics = text, .costMicros = If(cost, 0L)}).ConfigureAwait(False)
        End Function

        ''' <summary>?q= (title, style, lyrics), starred=1, job=, limit=, before= (paging by time).</summary>
        Private Async Function MusicSongs(rc As RequestContext) As Task
            Dim list = Await _h.MusicStore.SongsAsync(rc.User.Name, rc.QueryString("q"), rc.QueryString("starred") = "1", rc.QueryString("job"),
                                                      rc.QueryInt("limit", 60, 1, 500), rc.QueryDate("before")).ConfigureAwait(False)
            Await rc.OkAsync(list.Select(AddressOf SongDto).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function SongOrThrowAsync(rc As RequestContext) As Task(Of MusicSong)
            Dim s = Await _h.MusicStore.SongAsync(rc.User.Name, rc.Route("songId")).ConfigureAwait(False)
            If s Is Nothing Then Throw ApiException.NotFound("SONG_NOT_FOUND", "That song is gone.")
            Return s
        End Function

        Private Async Function MusicSong(rc As RequestContext) As Task
            Await rc.OkAsync(SongDto(Await SongOrThrowAsync(rc).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>The song's audio (byte ranges work, so the player can seek); ?download=1 saves it as a file.</summary>
        Private Async Function MusicSongAudio(rc As RequestContext) As Task
            Dim s = Await SongWithAudioAsync(rc, Await SongOrThrowAsync(rc).ConfigureAwait(False)).ConfigureAwait(False)
            Dim data = Await _h.MusicStore.ReadAudioAsync(s.Id).ConfigureAwait(False)
            If data Is Nothing Then Throw ApiException.NotFound("SONG_NOT_FOUND", "That song's audio is gone.")
            Dim headers As New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}, {"Accept-Ranges", "bytes"}}
            If rc.QueryString("download") = "1" Then
                Dim safe = Text.RegularExpressions.Regex.Replace(If(s.Title, "song"), "[^\w\- ]+", "").Trim()
                Dim ext = If(s.Mime = "audio/mpeg", "mp3", If(s.Mime = "audio/wav", "wav", "m4a"))
                headers("Content-Disposition") = $"attachment; filename=""{If(safe.Length = 0, "song", safe)}.{ext}"""
            End If
            Dim range = ParseRange(rc.Http.Request.Headers("Range"), data.LongLength)
            If range.HasValue Then
                Dim part(CInt(range.Value.Last - range.Value.First)) As Byte
                Array.Copy(data, range.Value.First, part, 0, part.Length)
                headers("Content-Range") = $"bytes {range.Value.First}-{range.Value.Last}/{data.LongLength}"
                Await rc.WriteBytesAsync(206, s.Mime, part, headers).ConfigureAwait(False)
                Return
            End If
            Await rc.WriteBytesAsync(200, s.Mime, data, headers).ConfigureAwait(False)
        End Function

        Private Async Function MusicSongCover(rc As RequestContext) As Task
            Dim s = Await SongOrThrowAsync(rc).ConfigureAwait(False)
            Dim data = If(s.HasCover, Await _h.MusicStore.ReadCoverAsync(s.Id).ConfigureAwait(False), Nothing)
            If data Is Nothing Then Throw ApiException.NotFound("NO_COVER", "This song has no cover.")
            Dim type = If(data.Length > 3 AndAlso data(0) = &H89 AndAlso data(1) = &H50, "image/png", If(data.Length > 12 AndAlso data(8) = AscW("W"c) AndAlso data(9) = AscW("E"c), "image/webp", "image/jpeg"))
            Await rc.WriteBytesAsync(200, type, data, New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {title, starred}: a new title, or the star (starred songs are never dropped to make room).</summary>
        Private Async Function MusicSongPatch(rc As RequestContext) As Task
            Dim s = Await SongOrThrowAsync(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim title = JsonUtil.GetString(b, "title")
            If title IsNot Nothing Then
                title = title.Trim()
                If title.Length = 0 OrElse title.Length > 200 Then Throw ApiException.BadRequest("A title has 1 to 200 characters.")
            End If
            Dim starred As Boolean? = Nothing
            Dim p As JsonElement
            If b.TryGetProperty("starred", p) AndAlso (p.ValueKind = JsonValueKind.True OrElse p.ValueKind = JsonValueKind.False) Then starred = p.ValueKind = JsonValueKind.True
            Await _h.MusicStore.UpdateSongAsync(rc.User.Name, s.Id, title, starred).ConfigureAwait(False)
            Await rc.OkAsync(SongDto(Await SongOrThrowAsync(rc).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        Private Async Function MusicSongDelete(rc As RequestContext) As Task
            If Not Await _h.MusicStore.DeleteSongAsync(rc.User.Name, rc.Route("songId")).ConfigureAwait(False) Then Throw ApiException.NotFound("SONG_NOT_FOUND", "That song is gone.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        Private Async Function MusicSongsClear(rc As RequestContext) As Task
            Await rc.OkAsync(New With {.deleted = Await _h.MusicStore.ClearAsync(rc.User.Name).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>The Suno account's newest songs (administrators: the account is theirs), marked when already in their library.</summary>
        Private Async Function MusicSunoLibrary(rc As RequestContext) As Task
            RequireMusic()
            Dim mine = New HashSet(Of String)(Await _h.MusicStore.ClipIdsAsync(rc.User.Name).ConfigureAwait(False), StringComparer.Ordinal)
            Dim list As List(Of JsonObject)
            Try
                list = Await _h.Music.RecentAsync(rc.QueryInt("limit", 30, 1, 100), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As MusicException
                Throw MusicError(ex)
            End Try
            For Each c In list
                c("inLibrary") = mine.Contains(If(c("id")?.ToString(), ""))
            Next
            Await rc.OkAsync(list).ConfigureAwait(False)
        End Function

        ''' <summary>Body {clipIds}: finished songs of the Suno account go into the administrator's library (not charged).</summary>
        Private Async Function MusicImport(rc As RequestContext) As Task
            RequireMusic()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ids = JsonUtil.GetStringList(b, "clipIds").Distinct().ToList()
            If ids.Count = 0 OrElse ids.Count > 20 Then Throw ApiException.BadRequest("Choose 1 to 20 songs.")
            Dim mine = New HashSet(Of String)(Await _h.MusicStore.ClipIdsAsync(rc.User.Name).ConfigureAwait(False), StringComparer.Ordinal)
            Dim added As New List(Of Object), skipped As New List(Of String)
            Try
                For Each v In Await _h.Music.ClipsAsync(ids, rc.CancellationToken).ConfigureAwait(False)
                    Dim id = v("id")?.ToString()
                    If id Is Nothing OrElse mine.Contains(id) OrElse MusicService.Stage(v("status")?.ToString()) <> "done" Then
                        skipped.Add(id)
                        Continue For
                    End If
                    Dim song = Await _h.MusicJobs.KeepAsync(rc.User.Name, "import", id, v, Nothing).ConfigureAwait(False)
                    If song Is Nothing Then skipped.Add(id) Else added.Add(SongDto(song))
                Next
            Catch ex As MusicException
                Throw MusicError(ex)
            End Try
            Await rc.OkAsync(New With {.added = added, .skipped = skipped}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {apiKey}: the wrapper's key (its api_key.txt), kept in the vault (empty removes it). Answers whether the wrapper takes it.</summary>
        Private Async Function MusicKeyPut(rc As RequestContext) As Task
            RequireMusic()
            If _h.Secrets Is Nothing Then Throw New ApiException(409, "NO_VAULT", "The vault is not available.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim key = If(JsonUtil.GetString(b, "apiKey"), "").Trim()
            Dim secretName = _h.Options.Music.ApiKeySecret
            If String.IsNullOrWhiteSpace(secretName) Then Throw New ApiException(409, "NO_SECRET_NAME", "Music.ApiKeySecret is empty: put the key in Music.ApiKey instead.")
            If key.Length = 0 Then
                Await _h.Secrets.DeleteAsync(secretName).ConfigureAwait(False)
            Else
                Dim host = _h.Music.Host
                Dim hosts = If(host Is Nothing OrElse host.Contains(":"c), New List(Of String), New List(Of String) From {host})
                Try
                    Await _h.Secrets.SetAsync(secretName, key, "API key of the Suno wrapper (the Music tab sets it; no agent may use it)", New List(Of String), rc.User.Name, hosts).ConfigureAwait(False)
                Catch ex As ArgumentException
                    Throw ApiException.BadRequest(ex.Message)
                End Try
            End If
            _h.Music.ForgetKey()
            Await AuditAsync(rc, If(key.Length = 0, "remove-music-key", "set-music-key"), "secret", secretName).ConfigureAwait(False)
            Dim problem As String = Nothing
            Try
                Await _h.Music.CreditsAsync(rc.CancellationToken).ConfigureAwait(False)
            Catch ex As MusicException
                problem = ex.Message
            End Try
            Await rc.OkAsync(New With {.keySet = key.Length > 0, .ok = problem Is Nothing, .problem = problem, .keyInConfig = _h.Music.KeyInConfig}).ConfigureAwait(False)
        End Function

        Private Async Function MusicPricing(rc As RequestContext) As Task
            Await rc.OkAsync(MusicPriceDto(Await MusicPriceAsync().ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>Body {perSong, perLyrics} in USD (null = free): what music costs User accounts from now on.</summary>
        Private Async Function MusicPricingPut(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim perSong = ParseMoney(b, "perSong", False), perLyrics = ParseMoney(b, "perLyrics", False)
            If perSong.GetValueOrDefault() > 1000D OrElse perLyrics.GetValueOrDefault() > 1000D Then
                Throw ApiException.BadRequest("That price looks like a typo: at most $1,000 per song and per lyrics.")
            End If
            Try
                Await _h.MusicStore.SetPricingAsync(perSong, perLyrics, rc.User.Name).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "set-music-pricing", "pricing", "suno", JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await rc.OkAsync(MusicPriceDto(Await MusicPriceAsync().ConfigureAwait(False))).ConfigureAwait(False)
        End Function
    End Class

End Namespace
