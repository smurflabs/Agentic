Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: speech through the XTTS server (TextToSpeech). The Chat tab reads answers aloud and speaks Dictation mode's
    ' short summaries with it; the Audio tab uses all of it: every speech setting, streamed or whole files, kept clips,
    ' cloned voices (with their clips kept for renewing) and each voice's personality. The server's key never leaves here.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterAudio(r As Router)
            r.Map("GET", "/api/audio", AuthLevel.Read, AddressOf AudioInfo)
            r.Map("GET", "/api/audio/queue", AuthLevel.Read, AddressOf AudioQueue)
            r.Map("PUT", "/api/audio/prefs", AuthLevel.SubmitTask, AddressOf AudioPrefsPut)
            r.Map("PUT", "/api/audio/profiles", AuthLevel.SubmitTask, AddressOf AudioProfilePut)
            r.Map("DELETE", "/api/audio/profiles", AuthLevel.SubmitTask, AddressOf AudioProfileDelete)
            r.Map("POST", "/api/audio/tts", AuthLevel.SubmitTask, AddressOf AudioTts)
            r.Map("POST", "/api/audio/stream", AuthLevel.SubmitTask, AddressOf AudioStream)
            r.Map("POST", "/api/audio/summary", AuthLevel.SubmitTask, AddressOf AudioSummary)
            r.Map("GET", "/api/audio/clips", AuthLevel.Read, AddressOf AudioClips)
            r.Map("DELETE", "/api/audio/clips", AuthLevel.SubmitTask, AddressOf AudioClipsClear)
            r.Map("GET", "/api/audio/clips/{clipId}/audio", AuthLevel.Read, AddressOf AudioClipAudio)
            r.Map("DELETE", "/api/audio/clips/{clipId}", AuthLevel.SubmitTask, AddressOf AudioClipDelete)
            r.Map("POST", "/api/audio/voice-clips", AuthLevel.SubmitTask, AddressOf AudioStageClip)
            r.Map("POST", "/api/audio/voices", AuthLevel.SubmitTask, AddressOf AudioClone)
            r.Map("GET", "/api/audio/voices/{name}", AuthLevel.Read, AddressOf AudioVoice)
            r.Map("POST", "/api/audio/voices/{name}/renew", AuthLevel.SubmitTask, AddressOf AudioRenew)
            r.Map("DELETE", "/api/audio/voices/{name}", AuthLevel.SubmitTask, AddressOf AudioVoiceDelete)
            r.Map("PUT", "/api/audio/key", AuthLevel.Admin, AddressOf AudioKeyPut)
            r.Map("GET", "/api/audio/pricing", AuthLevel.Read, AddressOf AudioPricing)
            r.Map("PUT", "/api/audio/pricing", AuthLevel.Admin, AddressOf AudioPricingPut)
        End Sub

        ' ------------------------------------------------------------------ prices

        ''' <summary>About how long speech of this many characters plays (about 14 characters a second at speed 1).</summary>
        Friend Shared Function EstimatedAudioMs(chars As Integer, speed As Double) As Long
            Return CLng(chars * 1000.0 / 14.0 / Math.Max(0.1, speed))
        End Function

        Private Function SpeechPriceAsync() As Task(Of SpeechPrice)
            Return _h.Audio.PricingAsync(_h.Options.TextToSpeech)
        End Function

        ''' <summary>A User account whose balance does not cover what this will about cost is refused (402) before the work is done.</summary>
        Private Async Function RequireBalanceAsync(rc As RequestContext, estimateMicros As Long, what As String) As Task
            If estimateMicros <= 0 OrElse rc.User.IsAdmin OrElse Not (rc.User.ViaSession OrElse rc.User.ViaApiKey) Then Return
            Dim acc = Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False)
            If acc Is Nothing OrElse Not acc.Charged Then Return
            Dim needed = Await _h.Accounts.WithSurchargeAsync(rc.User.Name, estimateMicros).ConfigureAwait(False)
            If acc.BalanceMicros < needed Then
                Throw New ApiException(402, "BALANCE_EMPTY", $"Your balance ({TextUtil.FormatMicros(acc.BalanceMicros)}) does not cover {what} (about {TextUtil.FormatMicros(needed)}). Ask an administrator to add money to your account.")
            End If
        End Function

        ''' <summary>Charges speech to the account; returns what it paid (surcharge included), or Nothing when nothing was charged.</summary>
        Private Async Function ChargeSpeechAsync(rc As RequestContext, micros As Long, description As String) As Task(Of Long?)
            If micros <= 0 OrElse rc.User.IsAdmin Then Return Nothing
            Dim after = Await _h.Accounts.ChargeAsync(rc.User.Name, micros, Global.Agentic.Economy.AccountService.EntrySpeech, "xtts", Nothing, Nothing, Nothing, description).ConfigureAwait(False)
            If Not after.HasValue Then Return Nothing
            Return Await _h.Accounts.WithSurchargeAsync(rc.User.Name, micros).ConfigureAwait(False)
        End Function

        Private Shared Function PriceDto(p As SpeechPrice) As Object
            Return New With {.perMillionChars = p.PerMillionChars, .perMinute = p.PerMinute, .perVoiceUpload = p.PerVoiceUpload, .source = p.Source, .effectiveFromUtc = p.EffectiveFromUtc}
        End Function

        Private Async Function AudioPricing(rc As RequestContext) As Task
            Await rc.OkAsync(PriceDto(Await SpeechPriceAsync().ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>Body {perMillionChars, perMinute, perVoiceUpload} in USD (null = free): what speech costs User accounts from now on.</summary>
        Private Async Function AudioPricingPut(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim chars = ParseMoney(b, "perMillionChars", False), minute = ParseMoney(b, "perMinute", False), upload = ParseMoney(b, "perVoiceUpload", False)
            If chars.GetValueOrDefault() > 10000D OrElse minute.GetValueOrDefault() > 1000D OrElse upload.GetValueOrDefault() > 1000D Then
                Throw ApiException.BadRequest("That price looks like a typo: at most $10,000 per 1M characters, $1,000 per minute and $1,000 per voice upload.")
            End If
            Try
                Await _h.Audio.SetPricingAsync(chars, minute, upload, rc.User.Name).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "set-speech-pricing", "pricing", "text-to-speech", JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await rc.OkAsync(PriceDto(Await SpeechPriceAsync().ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        Private Sub RequireSpeech()
            If _h.Speech Is Nothing OrElse Not _h.Speech.Configured Then Throw New ApiException(409, "TTS_NOT_SET_UP", "No speech server is set up (TextToSpeech.BaseUrl in the configuration).")
        End Sub

        Private Shared Function SpeechError(ex As SpeechException) As ApiException
            Dim code = If(ex.Status = 400, "TTS_BAD_REQUEST", If(ex.Status = 404, "VOICE_NOT_FOUND", If(ex.Status = 409, "TTS_NOT_SET_UP", If(ex.Status = 504, "TTS_TIMEOUT", "TTS_FAILED"))))
            Return New ApiException(ex.Status, code, ex.Message)
        End Function

        Private Function SourceDto(s As VoiceSource, rc As RequestContext) As Object
            Return New With {.name = s.Name, .createdBy = s.CreatedBy, .clips = s.Clips, .settings = s.Settings, .createdUtc = s.CreatedUtc,
                             .uploadedUtc = s.UploadedUtc, .expiresUtc = s.ExpiresUtc, .mine = MayManageVoice(s, rc)}
        End Function

        Private Shared Function MayManageVoice(s As VoiceSource, rc As RequestContext) As Boolean
            Return s Is Nothing OrElse rc.User.IsAdmin OrElse String.Equals(s.CreatedBy, rc.User.Name, StringComparison.OrdinalIgnoreCase)
        End Function

        Private Shared Function ClipDto(c As AudioClip) As Object
            Return New With {.id = c.Id, .text = c.Text, .voice = c.Voice, .settings = c.Settings, .mode = c.Mode, .bytes = c.Bytes, .durationMs = c.DurationMs,
                             .generateMs = c.GenerateMs, .firstAudioMs = c.FirstAudioMs, .costMicros = c.CostMicros, .createdUtc = c.CreatedUtc}
        End Function

        ''' <summary>Everything the pages need at once: whether speech works, the voices and languages, defaults, the user's choices and personalities.</summary>
        Private Async Function AudioInfo(rc As RequestContext) As Task
            Dim sp = _h.Speech
            Dim opt = _h.Options.TextToSpeech
            Dim configured = sp IsNot Nothing AndAlso sp.Configured
            Dim key = If(configured, Await sp.KeyAsync().ConfigureAwait(False), Nothing)
            Dim speakers As JsonObject = Nothing
            Dim problem As String = Nothing
            Dim languages = SpeechService.KnownLanguages.ToList()
            If configured AndAlso key IsNot Nothing Then
                Try
                    speakers = Await sp.SpeakersAsync(rc.QueryString("refresh") = "1", rc.CancellationToken).ConfigureAwait(False)
                    languages = Await sp.LanguagesAsync(rc.CancellationToken).ConfigureAwait(False)
                Catch ex As SpeechException
                    problem = ex.Message
                End Try
            ElseIf configured Then
                problem = "The speech server's API key is not set" & If(rc.User.IsAdmin, ": set it under Settings.", ": an administrator sets it on the Audio tab.")
            Else
                problem = "No speech server is set up (TextToSpeech.BaseUrl in the configuration)."
            End If
            Dim sources = If(configured, Await _h.Audio.SourcesAsync().ConfigureAwait(False), New List(Of VoiceSource))
            ' What speech costs this account (for the estimates on the page): administrators never pay.
            Dim acc = If(rc.User.ViaSession OrElse rc.User.ViaApiKey, Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False), Nothing)
            Dim surcharged = acc IsNot Nothing AndAlso acc.Charged AndAlso Await _h.Accounts.HasSurchargeAsync(rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .pricing = PriceDto(Await SpeechPriceAsync().ConfigureAwait(False)),
                .account = New With {.charged = acc IsNot Nothing AndAlso acc.Charged, .balanceMicros = If(acc?.BalanceMicros, 0L),
                                     .surchargePercent = If(surcharged, Global.Agentic.Economy.AccountService.SurchargePercent, 0)},
                .canSetPrices = rc.User.IsAdmin OrElse rc.User.HasPermission("finance.manage"),
                .available = configured, .keySet = key IsNot Nothing, .ready = speakers IsNot Nothing, .problem = problem, .isAdmin = rc.User.IsAdmin,
                .baseUrl = If(rc.User.IsAdmin AndAlso configured, sp.BaseUrl, Nothing), .keyInConfig = rc.User.IsAdmin AndAlso configured AndAlso sp.KeyInConfig,
                .keySecret = If(rc.User.IsAdmin, opt.ApiKeySecret, Nothing),
                .defaults = New With {.speaker = opt.DefaultSpeaker, .language = opt.DefaultLanguage, .maxTextChars = opt.MaxTextChars, .summaryWords = opt.SummaryWords,
                                      .summaryPrompt = opt.DefaultSummaryPrompt, .summaryModel = opt.SummaryModel, .maxCloneClipMb = opt.MaxCloneClipMb,
                                      .voiceDays = SpeechService.VoiceDays, .maxClips = opt.MaxClipsPerUser},
                .speakers = speakers, .languages = languages,
                .prefs = Await _h.Audio.PrefsAsync(rc.User.Name).ConfigureAwait(False),
                .profiles = Await _h.Audio.ProfilesAsync(rc.User.Name).ConfigureAwait(False),
                .sources = sources.Select(Function(s) SourceDto(s, rc)).ToList(),
                .queue = If(configured, sp.Queue.StateFor(rc.User.Name), Nothing)}).ConfigureAwait(False)
        End Function

        ''' <summary>The speech server's line: its slots, requests being made and waiting, and where the caller's own requests are.</summary>
        Private Async Function AudioQueue(rc As RequestContext) As Task
            RequireSpeech()
            Await rc.OkAsync(_h.Speech.Queue.StateFor(rc.User.Name)).ConfigureAwait(False)
        End Function

        ''' <summary>Dictation mode's settings and the chosen voice, each in its range.</summary>
        Private Shared ReadOnly PrefRanges As New Dictionary(Of String, (Min As Integer, Max As Integer)) From {
            {"silenceMs", (400, 6000)}, {"maxTurnSeconds", (10, 600)}, {"noSpeechSeconds", (5, 300)}, {"summaryWords", (10, 300)}, {"sensitivity", (1, 10)}}
        Private Shared ReadOnly PrefFlags As String() = {"autoListen", "speakErrors", "showTranscript"}

        ''' <summary>Body: the settings to change (null removes one); unknown settings are ignored.</summary>
        Private Async Function AudioPrefsPut(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim prefs = Await _h.Audio.PrefsAsync(rc.User.Name).ConfigureAwait(False)
            For Each p In b.EnumerateObject()
                If p.Value.ValueKind = JsonValueKind.Null Then
                    prefs.Remove(p.Name)
                ElseIf p.Name = "voice" Then
                    Dim v = If(p.Value.ValueKind = JsonValueKind.String, p.Value.GetString().Trim(), "")
                    If v.Length = 0 OrElse v.Length > 200 Then Throw ApiException.BadRequest("voice is a voice key (speaker:Name or voice:name).")
                    prefs("voice") = v
                ElseIf PrefRanges.ContainsKey(p.Name) Then
                    Dim range = PrefRanges(p.Name)
                    Dim n As Integer
                    If p.Value.ValueKind <> JsonValueKind.Number OrElse Not p.Value.TryGetInt32(n) OrElse n < range.Min OrElse n > range.Max Then
                        Throw ApiException.BadRequest($"{p.Name} is a whole number from {range.Min} to {range.Max}.")
                    End If
                    prefs(p.Name) = n
                ElseIf PrefFlags.Contains(p.Name) Then
                    If p.Value.ValueKind <> JsonValueKind.True AndAlso p.Value.ValueKind <> JsonValueKind.False Then Throw ApiException.BadRequest($"{p.Name} is true or false.")
                    prefs(p.Name) = p.Value.ValueKind = JsonValueKind.True
                End If
            Next
            Await _h.Audio.SavePrefsAsync(rc.User.Name, prefs).ConfigureAwait(False)
            Await rc.OkAsync(prefs).ConfigureAwait(False)
        End Function

        ''' <summary>Body {voice, summaryPrompt, summaryModel, settings}: the voice's personality for this user.</summary>
        Private Async Function AudioProfilePut(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim voice = If(JsonUtil.GetString(b, "voice"), "").Trim()
            If voice.Length = 0 OrElse voice.Length > 200 Then Throw ApiException.BadRequest("voice is a voice key (speaker:Name or voice:name).")
            Dim prompt = JsonUtil.GetString(b, "summaryPrompt")
            If prompt IsNot Nothing AndAlso prompt.Length > 8000 Then Throw ApiException.BadRequest("The personality can have at most 8,000 characters.")
            Dim model = JsonUtil.GetString(b, "summaryModel")
            If String.IsNullOrWhiteSpace(model) Then model = Nothing
            If model IsNot Nothing AndAlso Not rc.User.CanUseModel(model.Trim()) Then Throw New ApiException(403, "MODEL_NOT_ALLOWED", "Your account may not use that model.")
            Dim settingsEl As JsonElement
            Dim settings As New JsonObject()
            If b.TryGetProperty("settings", settingsEl) Then
                Try
                    Dim langs = If(_h.Speech IsNot Nothing AndAlso _h.Speech.Configured, Await _h.Speech.LanguagesAsync(rc.CancellationToken).ConfigureAwait(False), SpeechService.KnownLanguages.ToList())
                    settings = SpeechService.CleanSettings(settingsEl, _h.Options.TextToSpeech, langs)
                Catch ex As SpeechException
                    Throw SpeechError(ex)
                End Try
            End If
            Dim profile As New VoiceProfile With {.Voice = voice, .SummaryPrompt = If(String.IsNullOrWhiteSpace(prompt), Nothing, prompt.Trim()), .SummaryModel = model?.Trim(), .Settings = settings}
            Await _h.Audio.SaveProfileAsync(rc.User.Name, profile).ConfigureAwait(False)
            Await rc.OkAsync(Await _h.Audio.ProfileAsync(rc.User.Name, voice).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function AudioProfileDelete(rc As RequestContext) As Task
            Dim voice = If(rc.QueryString("voice"), "").Trim()
            If Not Await _h.Audio.DeleteProfileAsync(rc.User.Name, voice).ConfigureAwait(False) Then Throw ApiException.NotFound("PROFILE_NOT_FOUND", "That voice has no personality.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>The /tts JSON for a page's request (the voice's personality fills in the settings it did not send) and the voice's key.</summary>
        Private Async Function SpeechRequestAsync(rc As RequestContext, b As JsonElement, streaming As Boolean) As Task(Of (Body As JsonObject, Voice As String))
            Dim opt = _h.Options.TextToSpeech
            Dim prefs = Await _h.Audio.PrefsAsync(rc.User.Name).ConfigureAwait(False)
            Dim voice = SpeechService.RequestedVoice(b, prefs("voice")?.ToString(), opt)
            Dim profile = If(JsonUtil.GetBool(b, "useProfile", True), Await _h.Audio.ProfileAsync(rc.User.Name, voice).ConfigureAwait(False), Nothing)
            Dim langs = Await _h.Speech.LanguagesAsync(rc.CancellationToken).ConfigureAwait(False)
            Return (SpeechService.BuildRequest(b, voice, profile?.Settings, opt, langs, streaming), voice)
        End Function

        Private Shared Function SettingsOf(body As JsonObject) As JsonObject
            Dim s = CType(body.DeepClone(), JsonObject)
            s.Remove("text")
            Return s
        End Function

        Private Function KeepClipAsync(rc As RequestContext, id As String, request As (Body As JsonObject, Voice As String), mode As String, wav As Byte(), generateMs As Long, firstMs As Long?,
                                       costMicros As Long?) As Task
            Dim clip As New AudioClip With {.Id = id, .Username = rc.User.Name, .Text = request.Body("text").ToString(), .Voice = request.Voice, .Settings = SettingsOf(request.Body),
                                           .Mode = mode, .DurationMs = SpeechService.PcmDurationMs(SpeechService.WavToPcm(wav).LongLength), .GenerateMs = generateMs,
                                           .FirstAudioMs = firstMs, .CostMicros = costMicros, .CreatedUtc = DateTime.UtcNow}
            Return _h.Audio.AddClipAsync(clip, wav, _h.Options.TextToSpeech.MaxClipsPerUser)
        End Function

        ''' <summary>The speech's price: refused (402) up front when the balance does not cover its estimate (text in, audio about as long as the text needs).</summary>
        Private Async Function PriceSpeechAsync(rc As RequestContext, body As JsonObject) As Task(Of SpeechPrice)
            Dim price = Await SpeechPriceAsync().ConfigureAwait(False)
            Dim chars = body("text").ToString().Length
            Dim speed As Double = 1
            Dim s = TryCast(body("speed"), JsonValue)
            If s IsNot Nothing Then s.TryGetValue(speed)
            Await RequireBalanceAsync(rc, price.SpeechMicros(chars, EstimatedAudioMs(chars, speed)), "this speech").ConfigureAwait(False)
            Return price
        End Function

        ''' <summary>Charges speech that was made: its characters (text in) and the audio's length (audio out).</summary>
        Private Function ChargeSpokenAsync(rc As RequestContext, price As SpeechPrice, body As JsonObject, audioMs As Long, voice As String) As Task(Of Long?)
            Dim text = body("text").ToString()
            Return ChargeSpeechAsync(rc, price.SpeechMicros(text.Length, audioMs),
                                     $"Speech ({text.Length:N0} characters, {audioMs / 1000.0:0.0} s, {SpeechService.ParseVoiceKey(voice).Name}): {TextUtil.Truncate(text, 70, "…")}")
        End Function

        ''' <summary>Body: the text, voice and settings (and save: keep it on the Audio tab). Answers the whole WAV file; what it cost is in X-Cost-Micros.</summary>
        Private Async Function AudioTts(rc As RequestContext) As Task
            RequireSpeech()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim request As (Body As JsonObject, Voice As String)
            Dim price As SpeechPrice
            Dim wav As Byte()
            Dim sw = Stopwatch.StartNew()
            Try
                request = Await SpeechRequestAsync(rc, b, False).ConfigureAwait(False)
                price = Await PriceSpeechAsync(rc, request.Body).ConfigureAwait(False)
                wav = Await _h.Speech.SynthesizeAsync(request.Body, rc.CancellationToken, rc.User.Name).ConfigureAwait(False)
            Catch ex As SpeechException
                Throw SpeechError(ex)
            End Try
            Dim durationMs = SpeechService.PcmDurationMs(SpeechService.WavToPcm(wav).LongLength)
            Dim cost = Await ChargeSpokenAsync(rc, price, request.Body, durationMs, request.Voice).ConfigureAwait(False)
            Dim inv = Globalization.CultureInfo.InvariantCulture
            Dim headers As New Dictionary(Of String, String) From {
                {"Cache-Control", "no-store"}, {"X-Generate-Ms", sw.ElapsedMilliseconds.ToString(inv)}, {"X-Duration-Ms", durationMs.ToString(inv)},
                {"X-Voice", Uri.EscapeDataString(request.Voice)}, {"X-Cost-Micros", If(cost, 0L).ToString(inv)}}
            If JsonUtil.GetBool(b, "save", False) Then
                Dim id = Ids.NewId("clip")
                Await KeepClipAsync(rc, id, request, "file", wav, sw.ElapsedMilliseconds, Nothing, cost).ConfigureAwait(False)
                headers("X-Clip-Id") = id
            End If
            Await rc.WriteBytesAsync(200, "audio/wav", wav, headers).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body as /tts (plus stream_chunk_size), ?format=pcm (raw 16-bit mono 24 kHz, the default) or wav: the speech as it is
        ''' made. When nothing at all comes from the server's stream, the whole file is made instead (the server ends a stream
        ''' that failed after it started with less audio and status 200). A kept clip's id is in X-Clip-Id. The text and the
        ''' audio that reached the page are charged when it ends (nothing when no audio did).
        ''' </summary>
        Private Async Function AudioStream(rc As RequestContext) As Task
            RequireSpeech()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim format = If(rc.QueryString("format"), If(JsonUtil.GetString(b, "format"), "pcm")).Trim().ToLowerInvariant()
            If format <> "pcm" AndAlso format <> "wav" Then Throw ApiException.BadRequest("format is pcm or wav.")
            Dim request As (Body As JsonObject, Voice As String)
            Dim price As SpeechPrice
            Dim stream As SpeechStream
            Dim sw = Stopwatch.StartNew()
            Try
                request = Await SpeechRequestAsync(rc, b, True).ConfigureAwait(False)
                price = Await PriceSpeechAsync(rc, request.Body).ConfigureAwait(False)
                stream = Await _h.Speech.OpenStreamAsync(request.Body, format, rc.CancellationToken, rc.User.Name).ConfigureAwait(False)
            Catch ex As SpeechException
                Throw SpeechError(ex)
            End Try
            Dim clipId = If(JsonUtil.GetBool(b, "save", False), Ids.NewId("clip"), Nothing)
            Dim kept = If(clipId Is Nothing, Nothing, New MemoryStream())
            Dim total = 0L
            Dim first As Long? = Nothing
            Dim cut = False
            Dim gone = False
            Using stream
                rc.BeginStream(If(format = "wav", "audio/wav", "audio/L16; rate=24000; channels=1"))
                rc.Http.Response.Headers("X-Voice") = Uri.EscapeDataString(request.Voice)
                If clipId IsNot Nothing Then rc.Http.Response.Headers("X-Clip-Id") = clipId
                Dim output = rc.Http.Response.OutputStream
                Dim buf(16383) As Byte
                While True
                    Dim n As Integer
                    Try
                        n = Await stream.Body.ReadAsync(buf, 0, buf.Length, stream.Token).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpRequestException OrElse
                                               (TypeOf ex Is OperationCanceledException AndAlso Not rc.CancellationToken.IsCancellationRequested)
                        cut = True
                        _h.Log.Warn("Speech", "The speech stream broke off: " & ex.Message)
                        Exit While
                    End Try
                    If n <= 0 Then Exit While
                    If Not first.HasValue Then first = sw.ElapsedMilliseconds
                    total += n
                    kept?.Write(buf, 0, n)
                    If Not Await WriteAudioAsync(rc, output, buf, n).ConfigureAwait(False) Then
                        gone = True
                        Exit While
                    End If
                End While
                If total = 0 AndAlso Not gone AndAlso Not rc.CancellationToken.IsCancellationRequested Then
                    ' Nothing came: the whole file instead (the stream's slot is given back first).
                    stream.Dispose()
                    Dim wav As Byte() = Nothing
                    Try
                        wav = Await _h.Speech.SynthesizeAsync(request.Body, rc.CancellationToken, rc.User.Name).ConfigureAwait(False)
                    Catch ex As SpeechException
                        _h.Log.Warn("Speech", "No speech came from the stream or the whole file: " & ex.Message)
                    End Try
                    If wav IsNot Nothing Then
                        Dim data = If(format = "wav", wav, SpeechService.WavToPcm(wav))
                        first = sw.ElapsedMilliseconds
                        cut = False
                        If Await WriteAudioAsync(rc, output, data, data.Length).ConfigureAwait(False) Then
                            total = data.Length
                            kept?.Write(data, 0, data.Length)
                        Else
                            gone = True
                        End If
                    End If
                End If
            End Using
            ' What reached the page is paid for, also when the page went away part way; nothing when no audio did.
            Dim audioMs = SpeechService.PcmDurationMs(Math.Max(0L, total - If(format = "wav", 44L, 0L)))
            Dim cost As Long? = Nothing
            If audioMs > 0 Then cost = Await ChargeSpokenAsync(rc, price, request.Body, audioMs, request.Voice).ConfigureAwait(False)
            If kept IsNot Nothing AndAlso total > 0 AndAlso Not cut AndAlso Not gone Then
                Dim raw = kept.ToArray()
                Dim wav = SpeechService.PcmToWav(If(format = "wav", SpeechService.WavToPcm(raw), raw))
                Await KeepClipAsync(rc, clipId, request, "stream", wav, sw.ElapsedMilliseconds, first, cost).ConfigureAwait(False)
            End If
        End Function

        ''' <summary>Writes audio to the page; False when it went away (generation then stops with the server's request).</summary>
        Private Shared Async Function WriteAudioAsync(rc As RequestContext, output As Stream, data As Byte(), count As Integer) As Task(Of Boolean)
            Try
                Await output.WriteAsync(data, 0, count, rc.CancellationToken).ConfigureAwait(False)
                Await output.FlushAsync(rc.CancellationToken).ConfigureAwait(False)
                Return True
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is ObjectDisposedException
                Return False
            End Try
        End Function

        ''' <summary>
        ''' Body {chatId, messageId} (saved with that answer) or {text} (a test), with voice (its key; the user's voice otherwise),
        ''' words, and optionally summaryPrompt / summaryModel / language to try before saving them, and fallbackModel (the
        ''' page's chat model, for a test without an answer): the spoken summary. Writing it is charged like a chat answer: its
        ''' tokens at the model's Model Square price (costMicros).
        ''' </summary>
        Private Async Function AudioSummary(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim opt = _h.Options.TextToSpeech
            Dim prefs = Await _h.Audio.PrefsAsync(rc.User.Name).ConfigureAwait(False)
            Dim voice = SpeechService.RequestedVoice(b, prefs("voice")?.ToString(), opt)
            Dim profile = Await _h.Audio.ProfileAsync(rc.User.Name, voice).ConfigureAwait(False)
            Dim prefWords = TryCast(prefs("summaryWords"), JsonValue)
            Dim defaultWords As Integer = opt.SummaryWords
            If prefWords IsNot Nothing Then prefWords.TryGetValue(defaultWords)
            Dim words = JsonUtil.GetInt(b, "words", defaultWords)
            Dim persona = If(JsonUtil.GetString(b, "summaryPrompt"), If(profile?.SummaryPrompt, opt.DefaultSummaryPrompt))
            Dim models = New String() {JsonUtil.GetString(b, "summaryModel"), profile?.SummaryModel, opt.SummaryModel, JsonUtil.GetString(b, "fallbackModel")}
            Dim language = If(JsonUtil.GetString(b, "language"), If(profile?.Settings?("language")?.ToString(), opt.DefaultLanguage))
            Dim chatId = JsonUtil.GetString(b, "chatId")
            Dim text = JsonUtil.GetString(b, "text")
            If String.IsNullOrEmpty(chatId) AndAlso String.IsNullOrWhiteSpace(text) Then Throw ApiException.BadRequest("Send chatId and messageId, or a text.")
            Dim r As SpokenSummary
            Try
                r = Await _h.Chat.SpokenSummaryAsync(rc.User.Name, chatId, JsonUtil.GetString(b, "messageId"), text, voice, persona, models, words, language,
                                                     Function(m) ModelUseProblemAsync(rc, m), rc.CancellationToken,
                                                     TimeSpan.FromSeconds(Math.Clamp(opt.SummaryTimeoutSeconds, 5, 85))).ConfigureAwait(False)
            Catch ex As SpokenSummaryBlockedException
                Dim balance = ex.Message.Contains("balance", StringComparison.OrdinalIgnoreCase)
                Throw New ApiException(If(balance, 402, 403), If(balance, "BALANCE_EMPTY", "MODEL_NOT_ALLOWED"), ex.Message)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("MESSAGE_NOT_FOUND", ex.Message)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As InvalidOperationException
                Throw New ApiException(502, "SUMMARY_FAILED", ex.Message)
            End Try
            Dim cost = 0L
            ' The answer's first sentences (no model wrote one in time) cost nothing.
            If Not r.Fallback AndAlso (rc.User.ViaSession OrElse rc.User.ViaApiKey) Then
                cost = Await _h.Accounts.ChargeUsageAsync(rc.User.Name, rc.User.IsAdmin, Global.Agentic.Economy.AccountService.EntryChat, r.Model, r.RoutedModel,
                                                          r.PromptTokens, r.CompletionTokens, "Spoken summary (TL;DR)", r.CachedTokens).ConfigureAwait(False)
            End If
            Await rc.OkAsync(New With {.text = r.Text, .model = r.Model, .voice = voice, .costMicros = cost, .fallback = r.Fallback}).ConfigureAwait(False)
        End Function

        Private Async Function AudioClips(rc As RequestContext) As Task
            Dim list = Await _h.Audio.ListClipsAsync(rc.User.Name, rc.QueryString("q"), rc.QueryInt("limit", 200, 1, 1000)).ConfigureAwait(False)
            Await rc.OkAsync(list.Select(AddressOf ClipDto).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function AudioClipsClear(rc As RequestContext) As Task
            Await rc.OkAsync(New With {.deleted = Await _h.Audio.ClearClipsAsync(rc.User.Name).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>A kept clip's audio: WAV (the default) or ?format=pcm (raw samples); ?download=1 saves it as a file. Byte ranges work.</summary>
        Private Async Function AudioClipAudio(rc As RequestContext) As Task
            Dim clip = Await _h.Audio.GetClipAsync(rc.User.Name, rc.Route("clipId")).ConfigureAwait(False)
            Dim wav = If(clip Is Nothing, Nothing, Await _h.Audio.ReadClipAsync(clip.Id).ConfigureAwait(False))
            If wav Is Nothing Then Throw ApiException.NotFound("CLIP_NOT_FOUND", "That clip is gone.")
            Dim pcm = String.Equals(rc.QueryString("format"), "pcm", StringComparison.OrdinalIgnoreCase)
            Dim data = If(pcm, SpeechService.WavToPcm(wav), wav)
            Dim type = If(pcm, "application/octet-stream", "audio/wav")
            Dim headers As New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}, {"Accept-Ranges", "bytes"}}
            If rc.QueryString("download") = "1" Then headers("Content-Disposition") = $"attachment; filename=""speech-{clip.Id}.{If(pcm, "pcm", "wav")}"""
            Dim range = ParseRange(rc.Http.Request.Headers("Range"), data.LongLength)
            If range.HasValue Then
                Dim part(CInt(range.Value.Last - range.Value.First)) As Byte
                Array.Copy(data, range.Value.First, part, 0, part.Length)
                headers("Content-Range") = $"bytes {range.Value.First}-{range.Value.Last}/{data.LongLength}"
                Await rc.WriteBytesAsync(206, type, part, headers).ConfigureAwait(False)
                Return
            End If
            Await rc.WriteBytesAsync(200, type, data, headers).ConfigureAwait(False)
        End Function

        Private Async Function AudioClipDelete(rc As RequestContext) As Task
            If Not Await _h.Audio.DeleteClipAsync(rc.User.Name, rc.Route("clipId")).ConfigureAwait(False) Then Throw ApiException.NotFound("CLIP_NOT_FOUND", "That clip is gone.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Body: one clip of a voice to clone (?name=clip.wav); its id goes in POST /api/audio/voices. Kept for a day.</summary>
        Private Async Function AudioStageClip(rc As RequestContext) As Task
            RequireSpeech()
            Dim max = CLng(Math.Max(1, _h.Options.TextToSpeech.MaxCloneClipMb)) * 1024L * 1024L
            Dim data = Await rc.ReadBytesAsync(max).ConfigureAwait(False)
            If data Is Nothing OrElse data.Length < 1000 Then Throw ApiException.BadRequest("The clip is empty or too short.")
            Dim name = If(rc.QueryString("name"), "clip.wav")
            Dim id = Await _h.Audio.StageClipAsync(rc.User.Name, name, data).ConfigureAwait(False)
            Await rc.OkAsync(New With {.id = id, .name = Artifacts.UploadStore.SafeFileName(name), .bytes = data.LongLength}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {name, clips: [ids of staged clips], trimSilenceDb, normalize, gptCondLen, gptCondChunkLen, maxRefLength, consent}:
        ''' clones the voice (a name in use is replaced) and keeps its clips for renewing. consent must be true: the voice is the
        ''' user's own, or its speaker agreed.
        ''' </summary>
        Private Async Function AudioClone(rc As RequestContext) As Task
            RequireSpeech()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim name = If(JsonUtil.GetString(b, "name"), "").Trim()
            If Not SpeechService.IsVoiceName(name) Then Throw ApiException.BadRequest("A voice name has 1 to 64 letters, digits, _ or -.")
            If Not JsonUtil.GetBool(b, "consent", False) Then Throw ApiException.BadRequest("Confirm that this is your own voice, or that its speaker agreed to it being cloned.", "CONSENT_REQUIRED")
            Dim existing = Await _h.Audio.SourceAsync(name).ConfigureAwait(False)
            If Not MayManageVoice(existing, rc) Then Throw New ApiException(403, "VOICE_NOT_YOURS", $"The voice '{name}' was cloned by {existing.CreatedBy}: choose another name.")
            Dim ids = JsonUtil.GetStringList(b, "clips")
            If ids.Count = 0 Then Throw ApiException.BadRequest("Add at least one clip of the voice.")
            If ids.Count > 12 Then Throw ApiException.BadRequest("Use at most 12 clips.")
            Dim settings As New VoiceCloneSettings With {
                .Normalize = JsonUtil.GetBool(b, "normalize", False), .GptCondLen = JsonUtil.GetInt(b, "gptCondLen", 30),
                .GptCondChunkLen = JsonUtil.GetInt(b, "gptCondChunkLen", 4), .MaxRefLength = JsonUtil.GetInt(b, "maxRefLength", 30)}
            Dim trim As JsonElement
            If b.TryGetProperty("trimSilenceDb", trim) AndAlso trim.ValueKind = JsonValueKind.Number Then settings.TrimSilenceDb = trim.GetDouble()
            Dim clips As List(Of (FileName As String, Data As Byte()))
            Try
                clips = Await _h.Audio.StagedClipsAsync(rc.User.Name, ids).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message, "CLIP_NOT_FOUND")
            End Try
            Dim price = Await SpeechPriceAsync().ConfigureAwait(False)
            Await RequireBalanceAsync(rc, price.VoiceUploadMicros(), "cloning a voice").ConfigureAwait(False)
            Dim meta As JsonNode
            Try
                meta = Await _h.Speech.CloneAsync(name, clips, settings, rc.CancellationToken, rc.User.Name).ConfigureAwait(False)
            Catch ex As SpeechException
                Throw SpeechError(ex)
            End Try
            Dim cost = Await ChargeSpeechAsync(rc, price.VoiceUploadMicros(), $"Voice cloned: {name} ({clips.Count} clip{If(clips.Count = 1, "", "s")})").ConfigureAwait(False)
            Dim src = Await _h.Audio.SaveSourceAsync(name, rc.User.Name, clips, settings).ConfigureAwait(False)
            _h.Audio.DropStaged(ids)
            Await rc.OkAsync(New With {.voice = meta, .source = SourceDto(src, rc), .key = SpeechService.VoiceKey(True, name), .costMicros = If(cost, 0L)}).ConfigureAwait(False)
        End Function

        ''' <summary>A cloned voice: what the speech server has (clips, settings, expiry) and the clips kept here.</summary>
        Private Async Function AudioVoice(rc As RequestContext) As Task
            RequireSpeech()
            Dim name = rc.Route("name")
            Dim src = Await _h.Audio.SourceAsync(name).ConfigureAwait(False)
            Dim meta As JsonNode = Nothing
            Dim problem As String = Nothing
            Try
                meta = Await _h.Speech.VoiceInfoAsync(name, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As SpeechException
                If src Is Nothing Then Throw SpeechError(ex)
                problem = ex.Message
            End Try
            Await rc.OkAsync(New With {.name = name, .server = meta, .problem = problem, .source = If(src Is Nothing, Nothing, SourceDto(src, rc))}).ConfigureAwait(False)
        End Function

        ''' <summary>Uploads the voice's kept clips again with its settings: the speech server's 30 days start over.</summary>
        Private Async Function AudioRenew(rc As RequestContext) As Task
            RequireSpeech()
            Dim name = rc.Route("name")
            Dim src = Await _h.Audio.SourceAsync(name).ConfigureAwait(False)
            If src Is Nothing Then Throw ApiException.NotFound("NO_CLIPS", "This voice was not cloned here, so its clips are not kept: clone it again from its clips.")
            If Not MayManageVoice(src, rc) Then Throw New ApiException(403, "VOICE_NOT_YOURS", $"The voice '{name}' was cloned by {src.CreatedBy}.")
            Dim clips = Await _h.Audio.SourceClipsAsync(src).ConfigureAwait(False)
            If clips.Count = 0 Then Throw ApiException.NotFound("NO_CLIPS", "The clips this voice was made from are gone: clone it again.")
            Dim price = Await SpeechPriceAsync().ConfigureAwait(False)
            Await RequireBalanceAsync(rc, price.VoiceUploadMicros(), "renewing a voice").ConfigureAwait(False)
            Dim meta As JsonNode
            Try
                meta = Await _h.Speech.CloneAsync(name, clips, src.Settings, rc.CancellationToken, rc.User.Name).ConfigureAwait(False)
            Catch ex As SpeechException
                Throw SpeechError(ex)
            End Try
            Dim cost = Await ChargeSpeechAsync(rc, price.VoiceUploadMicros(), $"Voice renewed: {name}").ConfigureAwait(False)
            Await _h.Audio.MarkUploadedAsync(name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.voice = meta, .source = SourceDto(Await _h.Audio.SourceAsync(name).ConfigureAwait(False), rc), .costMicros = If(cost, 0L)}).ConfigureAwait(False)
        End Function

        Private Async Function AudioVoiceDelete(rc As RequestContext) As Task
            RequireSpeech()
            Dim name = rc.Route("name")
            Dim src = Await _h.Audio.SourceAsync(name).ConfigureAwait(False)
            If Not MayManageVoice(src, rc) Then Throw New ApiException(403, "VOICE_NOT_YOURS", $"The voice '{name}' was cloned by {src.CreatedBy}.")
            Try
                Await _h.Speech.DeleteVoiceAsync(name, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As SpeechException When ex.Status = 404 AndAlso src IsNot Nothing
                ' Already gone from the speech server (it expired): its clips here go too.
            Catch ex As SpeechException
                Throw SpeechError(ex)
            End Try
            Await _h.Audio.DeleteSourceAsync(name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {apiKey}: the speech server's key, kept in the vault (empty removes it). Answers whether the server takes it.</summary>
        Private Async Function AudioKeyPut(rc As RequestContext) As Task
            RequireSpeech()
            If _h.Secrets Is Nothing Then Throw New ApiException(409, "NO_VAULT", "The vault is not available.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim key = If(JsonUtil.GetString(b, "apiKey"), "").Trim()
            Dim secretName = _h.Options.TextToSpeech.ApiKeySecret
            If String.IsNullOrWhiteSpace(secretName) Then Throw New ApiException(409, "NO_SECRET_NAME", "TextToSpeech.ApiKeySecret is empty: put the key in TextToSpeech.ApiKey instead.")
            If key.Length = 0 Then
                Await _h.Secrets.DeleteAsync(secretName).ConfigureAwait(False)
            Else
                Dim hosts = If(_h.Speech.Host Is Nothing OrElse _h.Speech.Host.Contains(":"c), New List(Of String), New List(Of String) From {_h.Speech.Host})
                Try
                    Await _h.Secrets.SetAsync(secretName, key, "API key of the XTTS speech server (the Audio tab sets it; no agent may use it)", New List(Of String), rc.User.Name, hosts).ConfigureAwait(False)
                Catch ex As ArgumentException
                    Throw ApiException.BadRequest(ex.Message)
                End Try
            End If
            _h.Speech.ForgetKey()
            Await AuditAsync(rc, If(key.Length = 0, "remove-tts-key", "set-tts-key"), "secret", secretName).ConfigureAwait(False)
            Dim problem As String = Nothing
            If key.Length > 0 Then
                Try
                    Await _h.Speech.SpeakersAsync(True, rc.CancellationToken).ConfigureAwait(False)
                Catch ex As SpeechException
                    problem = ex.Message
                End Try
            End If
            Await rc.OkAsync(New With {.keySet = key.Length > 0, .ok = key.Length > 0 AndAlso problem Is Nothing, .problem = problem, .keyInConfig = _h.Speech.KeyInConfig}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
