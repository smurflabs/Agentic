Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
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

    ''' <summary>A failure of the Suno wrapper (or Suno behind it), or a request refused here: the status to answer with and why.</summary>
    Public NotInheritable Class MusicException
        Inherits Exception

        Public ReadOnly Property Status As Integer

        Public Sub New(status As Integer, message As String)
            MyBase.New(message)
            Me.Status = status
        End Sub
    End Class

    ''' <summary>
    ''' The Suno API wrapper (Music): it signs in to Suno with the saved session and makes songs (POST /generate: two per
    ''' request), reports them (GET /clips/status), hands over their audio (GET /clip/{id}/audio), writes lyrics (POST /lyrics)
    ''' and tells the credits left (GET /credits). Its key stays on this server.
    ''' </summary>
    Public NotInheritable Class MusicService
        Private Shared ReadOnly SafeName As New Regex("^[A-Za-z0-9._-]{1,100}$", RegexOptions.Compiled)
        ''' <summary>Where covers may be fetched from (Suno's CDNs).</summary>
        Private Shared ReadOnly CoverHosts As String() = {".suno.ai", ".suno.com", ".cloudfront.net"}
        Private Shared ReadOnly CoverClient As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(30)}

        Private ReadOnly _opt As MusicOptions
        Private ReadOnly _vault As SecretVault
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _lock As New Object()
        Private _key As String
        Private _keyAt As DateTime

        Public Sub New(options As MusicOptions, vault As SecretVault, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _opt = If(options, New MusicOptions())
            _vault = vault
            _log = log
            _http = If(handler Is Nothing, New HttpClient(), New HttpClient(handler))
            _http.Timeout = Timeout.InfiniteTimeSpan
        End Sub

        Public ReadOnly Property Options As MusicOptions
            Get
                Return _opt
            End Get
        End Property

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

        ''' <summary>The wrapper's key: Music.ApiKey, else the vault entry ApiKeySecret (Nothing: sent without one).</summary>
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
                _log?.Warn("Music", "Could not read the Suno wrapper's key from the vault: " & ex.Message)
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
            End SyncLock
        End Sub

        ' ------------------------------------------------------------------ requests

        Private Async Function SendAsync(method As HttpMethod, path As String, body As JsonObject, ct As CancellationToken, seconds As Integer) As Task(Of (Data As Byte(), ContentType As String))
            If Not Configured Then Throw New MusicException(409, "No Suno wrapper is set up (Music.BaseUrl).")
            Dim key = Await KeyAsync().ConfigureAwait(False)
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                cts.CancelAfter(TimeSpan.FromSeconds(seconds))
                Using req As New HttpRequestMessage(method, BaseUrl & path)
                    If body IsNot Nothing Then req.Content = New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                    If key IsNot Nothing Then req.Headers.Add("X-API-Key", key)
                    Dim resp As HttpResponseMessage
                    Try
                        resp = Await _http.SendAsync(req, cts.Token).ConfigureAwait(False)
                    Catch ex As HttpRequestException
                        Throw New MusicException(502, $"The Suno wrapper ({BaseUrl}) cannot be reached: {ex.Message} (is the SunoAPI scheduled task running?)")
                    Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                        Throw New MusicException(504, $"The Suno wrapper did not answer within {seconds} s.")
                    End Try
                    Using resp
                        Dim data = Await resp.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(False)
                        If resp.IsSuccessStatusCode Then Return (data, If(resp.Content.Headers.ContentType?.MediaType, "application/json"))
                        Dim status = CInt(resp.StatusCode)
                        Dim detail = SpeechService.DetailOf(Encoding.UTF8.GetString(data))
                        If status = 401 OrElse status = 403 Then Throw New MusicException(502, "The Suno wrapper refused the API key" & If(detail Is Nothing, ".", ": " & detail) & " (its key is in api_key.txt next to server.py).")
                        If status = 404 Then Throw New MusicException(404, If(detail, "The Suno wrapper does not have that."))
                        If status = 400 OrElse status = 422 Then Throw New MusicException(400, If(detail, "Suno refused the request."))
                        If status = 409 Then Throw New MusicException(409, If(detail, "Not ready yet."))
                        If status = 402 Then Throw New MusicException(402, "Suno refused the download: the Suno account has no downloads left (its free plan has a few; more come with a paid plan or download packs on suno.com)." &
                                                                       If(detail Is Nothing, "", " Suno said: " & detail))
                        If status = 504 Then Throw New MusicException(504, If(detail, "Suno took too long."))
                        Throw New MusicException(502, "Suno failed" & If(detail Is Nothing, $" ({status}).", ": " & Friendly(detail)))
                    End Using
                End Using
            End Using
        End Function

        ''' <summary>Suno's terse refusals in words a person can act on.</summary>
        Private Shared Function Friendly(detail As String) As String
            If detail.Contains("Prompt likely malformed", StringComparison.OrdinalIgnoreCase) Then Return detail & " (describe it in more words, or change words Suno may not allow)"
            If detail.Contains("credits", StringComparison.OrdinalIgnoreCase) OrElse detail.Contains("insufficient", StringComparison.OrdinalIgnoreCase) Then Return detail & " (the Suno account is out of credits)"
            If detail.Contains("run login.py", StringComparison.OrdinalIgnoreCase) Then Return detail & " (the Suno sign-in has to be renewed on the server: run login.py in the suno folder)"
            Return detail
        End Function

        Private Async Function JsonAsync(method As HttpMethod, path As String, body As JsonObject, ct As CancellationToken, seconds As Integer) As Task(Of JsonNode)
            Dim r = Await SendAsync(method, path, body, ct, seconds).ConfigureAwait(False)
            Try
                Return If(r.Data.Length = 0, New JsonObject(), JsonNode.Parse(r.Data))
            Catch ex As JsonException
                Throw New MusicException(502, "The Suno wrapper's answer could not be read.")
            End Try
        End Function

        ''' <summary>Whether the wrapper's Suno session works (ok, token_expires_in).</summary>
        Public Async Function HealthAsync(ct As CancellationToken) As Task(Of JsonNode)
            Try
                Return Await JsonAsync(HttpMethod.Get, "/health", Nothing, ct, 60).ConfigureAwait(False)
            Catch ex As MusicException When ex.Status = 502 AndAlso ex.Message.StartsWith("Suno failed", StringComparison.Ordinal)
                Return New JsonObject From {{"ok", False}, {"error", ex.Message}}
            End Try
        End Function

        ''' <summary>The Suno account's credits (available_credits, monthly_usage, monthly_limit, pending_batches).</summary>
        Public Function CreditsAsync(ct As CancellationToken) As Task(Of JsonNode)
            Return JsonAsync(HttpMethod.Get, "/credits", Nothing, ct, 60)
        End Function

        ''' <summary>Starts a request (two songs): {batch_id, clip_ids, status}.</summary>
        Public Async Function GenerateAsync(body As JsonObject, ct As CancellationToken) As Task(Of (BatchId As String, ClipIds As List(Of String)))
            Dim j = Await JsonAsync(HttpMethod.Post, "/generate", body, ct, 180).ConfigureAwait(False)
            Dim ids = If(TryCast(j?("clip_ids"), JsonArray), New JsonArray()).Select(Function(x) x?.ToString()).Where(Function(x) Not String.IsNullOrWhiteSpace(x)).ToList()
            If ids.Count = 0 Then Throw New MusicException(502, "Suno took the request but made no songs.")
            Return (j?("batch_id")?.ToString(), ids)
        End Function

        ''' <summary>Several songs at once (GET /clips/status; an older wrapper is asked one by one): status, audio, cover, lyrics.</summary>
        Public Async Function ClipsAsync(ids As IEnumerable(Of String), ct As CancellationToken) As Task(Of List(Of JsonObject))
            Dim list = ids.ToList()
            Try
                Dim j = Await JsonAsync(HttpMethod.Get, "/clips/status?ids=" & Uri.EscapeDataString(String.Join(",", list)), Nothing, ct, 90).ConfigureAwait(False)
                Return If(TryCast(j, JsonArray), New JsonArray()).OfType(Of JsonObject)().ToList()
            Catch ex As MusicException When ex.Status = 404 OrElse ex.Status = 405
            End Try
            Dim result As New List(Of JsonObject)
            For Each id In list
                Try
                    result.Add(TryCast(Await JsonAsync(HttpMethod.Get, "/clip/" & Uri.EscapeDataString(id), Nothing, ct, 90).ConfigureAwait(False), JsonObject))
                Catch ex As MusicException When ex.Status = 404
                    result.Add(New JsonObject From {{"id", id}, {"status", "missing"}})
                End Try
            Next
            Return result
        End Function

        ''' <summary>
        ''' A song as a playable MP3, through Suno's official download (GET /clip/{id}/download). Suno scrambles the stream its
        ''' own player plays, so this is the only way to a file; its first download of a song uses one of the Suno account's
        ''' downloads (402 when none are left), later ones are free.
        ''' </summary>
        Public Async Function DownloadAsync(clipId As String, ct As CancellationToken) As Task(Of (Data As Byte(), ContentType As String))
            Dim r = Await SendAsync(HttpMethod.Get, $"/clip/{Uri.EscapeDataString(clipId)}/download?format=mp3", Nothing, ct, 300).ConfigureAwait(False)
            If r.Data.Length < 1000 OrElse Not Playable(r.Data, r.Data.Length) Then Throw New MusicException(502, "Suno's download is not a playable audio file.")
            Return (r.Data, If(r.ContentType IsNot Nothing AndAlso r.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase), r.ContentType, "audio/mpeg"))
        End Function

        ''' <summary>Whether the bytes start like an audio file players know: MP3 (ID3, or a frame), MP4/M4A, Ogg, WAV or FLAC.</summary>
        Public Shared Function Playable(data As Byte(), count As Integer) As Boolean
            If data Is Nothing OrElse count < 12 Then Return False
            Dim ascii = Function(at As Integer, s As String) Encoding.ASCII.GetString(data, at, s.Length) = s
            If ascii(0, "ID3") OrElse ascii(0, "OggS") OrElse ascii(0, "RIFF") OrElse ascii(0, "fLaC") OrElse ascii(4, "ftyp") Then Return True
            Return data(0) = &HFF AndAlso (data(1) And &HE0) = &HE0
        End Function

        ''' <summary>Lyrics written by Suno for an instruction (a description of the song).</summary>
        Public Async Function LyricsAsync(instruction As String, ct As CancellationToken) As Task(Of String)
            Dim j = Await JsonAsync(HttpMethod.Post, "/lyrics", New JsonObject From {{"instruction", instruction}}, ct, 180).ConfigureAwait(False)
            Dim text = If(j?("edited_lyrics"), If(j?("lyrics"), If(j?("text"), j?("content"))))?.ToString()
            If String.IsNullOrWhiteSpace(text) Then Throw New MusicException(502, "Suno wrote no lyrics.")
            Return text.Trim()
        End Function

        ''' <summary>The newest songs of the Suno account (its library), as ClipView shows them.</summary>
        Public Async Function RecentAsync(limit As Integer, ct As CancellationToken) As Task(Of List(Of JsonObject))
            Dim j = Await JsonAsync(HttpMethod.Get, "/clips?limit=" & Math.Clamp(limit, 1, 100), Nothing, ct, 90).ConfigureAwait(False)
            Dim list = If(TryCast(j, JsonArray), TryCast(j?("clips"), JsonArray))
            Return If(list, New JsonArray()).OfType(Of JsonObject)().Select(AddressOf ClipView).ToList()
        End Function

        ''' <summary>A song's cover from Suno's CDN (https, Suno's hosts only, at most 5 MB); Nothing when there is none.</summary>
        Public Shared Async Function CoverAsync(url As String, ct As CancellationToken) As Task(Of Byte())
            Dim u As Uri = Nothing
            If String.IsNullOrWhiteSpace(url) OrElse Not Uri.TryCreate(url, UriKind.Absolute, u) OrElse u.Scheme <> Uri.UriSchemeHttps OrElse
               Not CoverHosts.Any(Function(h) u.Host.EndsWith(h, StringComparison.OrdinalIgnoreCase)) Then Return Nothing
            Try
                Using resp = Await CoverClient.GetAsync(u, ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode OrElse resp.Content.Headers.ContentLength.GetValueOrDefault() > 5 * 1024 * 1024 Then Return Nothing
                    Dim data = Await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(False)
                    Return If(data.Length > 100 AndAlso data.Length <= 5 * 1024 * 1024, data, Nothing)
                End Using
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                Return Nothing
            End Try
        End Function

        ' ------------------------------------------------------------------ requests from pages and Suno's answers

        ''' <summary>
        ''' The JSON for /generate from what a page sent: a description (prompt), or custom lyrics with a style (tags) and a
        ''' title; instrumental; the model. Each part checked against what Suno takes.
        ''' </summary>
        Public Shared Function BuildRequest(input As JsonElement, defaultModel As String) As JsonObject
            If input.ValueKind <> JsonValueKind.Object Then Throw New MusicException(400, "Send the song as a JSON object.")
            Dim text = Function(name As String, max As Integer) As String
                           Dim v = If(JsonUtil.GetString(input, name), "").Trim()
                           If v.Length > max Then Throw New MusicException(400, $"{name} has at most {max:N0} characters.")
                           Return v
                       End Function
            Dim prompt = text("prompt", 3000), tags = text("tags", 1000), title = text("title", 200), lyrics = text("lyrics", 6000)
            Dim instrumental = JsonUtil.GetBool(input, "instrumental", False)
            If instrumental Then lyrics = ""
            ' An instrumental with only a style: the style is its description.
            If prompt.Length = 0 AndAlso lyrics.Length = 0 Then prompt = tags
            If prompt.Length = 0 AndAlso lyrics.Length = 0 Then Throw New MusicException(400, "Describe the song, or write its lyrics.")
            Dim model = If(JsonUtil.GetString(input, "model"), "").Trim()
            If model.Length = 0 Then model = If(defaultModel, "chirp-goose")
            If Not SafeName.IsMatch(model) Then Throw New MusicException(400, "model is a Suno model id, like chirp-goose.")
            Return New JsonObject From {{"prompt", prompt}, {"tags", tags}, {"title", title}, {"lyrics", lyrics}, {"instrumental", instrumental}, {"model", model}}
        End Function

        ''' <summary>A song as the wrapper's /clips/status describes it, also from a raw Suno feed clip.</summary>
        Public Shared Function ClipView(c As JsonObject) As JsonObject
            If c Is Nothing Then Return New JsonObject()
            Dim md = TryCast(c("metadata"), JsonObject)
            If md Is Nothing Then Return c
            Dim media = TryCast(c("media_urls"), JsonArray)
            Dim audio = If(media IsNot Nothing AndAlso media.Count > 0, media(0)?("url")?.ToString(), c("audio_url")?.ToString())
            Return New JsonObject From {
                {"id", c("id")?.ToString()}, {"title", c("title")?.ToString()}, {"status", c("status")?.ToString()}, {"audio_url", audio},
                {"image_url", c("image_url")?.ToString()}, {"image_large_url", c("image_large_url")?.ToString()}, {"duration", md("duration")?.DeepClone()},
                {"tags", md("tags")?.ToString()}, {"lyrics", md("prompt")?.ToString()}, {"description", md("gpt_description_prompt")?.ToString()},
                {"instrumental", md("make_instrumental")?.DeepClone()}, {"model", c("model_name")?.ToString()}, {"created_at", c("created_at")?.ToString()},
                {"error", If(md("error_message"), md("error_type"))?.ToString()}}
        End Function

        ''' <summary>done (complete), failed (error), or pending (submitted, queued, streaming, missing for now…).</summary>
        Public Shared Function Stage(status As String) As String
            Select Case If(status, "").ToLowerInvariant()
                Case "complete", "completed", "done" : Return "done"
                Case "error", "failed", "failure" : Return "failed"
            End Select
            Return "pending"
        End Function

        Public Shared Function SecondsOf(clip As JsonObject) As Double
            Dim d As Double
            Return If(clip IsNot Nothing AndAlso Double.TryParse(clip("duration")?.ToString(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, d), d, 0)
        End Function
    End Class

End Namespace
