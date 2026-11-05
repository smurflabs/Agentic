Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A failure of the speech server: the status to answer with and the reason (the server's "detail").</summary>
    Public NotInheritable Class SpeechException
        Inherits Exception

        Public ReadOnly Property Status As Integer

        Public Sub New(status As Integer, message As String)
            MyBase.New(message)
            Me.Status = status
        End Sub
    End Class

    ''' <summary>How a cloned voice is made from its clips (the speech server's POST /voices fields).</summary>
    Public NotInheritable Class VoiceCloneSettings
        ''' <summary>Trims leading and trailing silence below this many dB (10-80); Nothing = off.</summary>
        Public Property TrimSilenceDb As Double?
        ''' <summary>Evens out the loudness of the clips.</summary>
        Public Property Normalize As Boolean
        ''' <summary>Seconds of audio used to capture the speaking style (1-60).</summary>
        Public Property GptCondLen As Integer = 30
        ''' <summary>Chunk size for the style (1-60, at most GptCondLen).</summary>
        Public Property GptCondChunkLen As Integer = 4
        ''' <summary>The most seconds used from each clip (1-120).</summary>
        Public Property MaxRefLength As Integer = 30
    End Class

    ''' <summary>Speech coming from the server as it is made; holds one of the request slots until it is disposed.</summary>
    Public NotInheritable Class SpeechStream
        Implements IDisposable

        Private ReadOnly _resp As HttpResponseMessage
        Private ReadOnly _release As Action
        Private _done As Integer

        ''' <summary>The audio (raw PCM, or a WAV header and then PCM).</summary>
        Public ReadOnly Property Body As Stream
        ''' <summary>Cancelled when the request's time is up or its caller went away: read Body with it.</summary>
        Public ReadOnly Property Token As CancellationToken

        Friend Sub New(resp As HttpResponseMessage, body As Stream, token As CancellationToken, release As Action)
            _resp = resp
            Me.Body = body
            Me.Token = token
            _release = release
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            If Interlocked.Exchange(_done, 1) = 1 Then Return
            Try
                Body.Dispose()
                _resp.Dispose()
            Finally
                _release()
            End Try
        End Sub
    End Class

    ''' <summary>
    ''' The XTTS-v2 speech server (TextToSpeech): its built-in speakers and cloned voices, its languages, speech as a whole WAV
    ''' file (/tts) or as it is made (/tts/stream), and cloning voices. Its API key stays on this server. The server works on
    ''' MaxConcurrency (1) request at a time: every request that makes speech or clones a voice (the Audio tab, Read aloud,
    ''' Dictation mode, the API) waits in one first-come, first-served line (Queue) until the one being made ends.
    ''' </summary>
    Public NotInheritable Class SpeechService
        ''' <summary>The server's languages, used until it has answered /languages.</summary>
        Public Shared ReadOnly KnownLanguages As String() = {"en", "es", "fr", "de", "it", "pt", "pl", "tr", "ru", "nl", "cs", "ar", "zh-cn", "hu", "ko", "ja", "hi"}
        ''' <summary>Cloned voices are deleted by the server this many days after they were uploaded.</summary>
        Public Const VoiceDays As Integer = 30
        ''' <summary>The server's audio: 24 kHz, mono, 16-bit.</summary>
        Public Const SampleRate As Integer = 24000
        Private Shared ReadOnly VoiceNamePattern As New Regex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled)

        Private ReadOnly _opt As TextToSpeechOptions
        Private ReadOnly _vault As SecretVault
        Private ReadOnly _log As StructuredLogger
        Private ReadOnly _http As HttpClient
        Private ReadOnly _lock As New Object()
        Private _speakers As JsonObject
        Private _speakersAt As DateTime
        Private _languages As List(Of String)
        Private _key As String
        Private _keyAt As DateTime

        Public Sub New(options As TextToSpeechOptions, vault As SecretVault, log As StructuredLogger, Optional handler As HttpMessageHandler = Nothing)
            _opt = If(options, New TextToSpeechOptions())
            _vault = vault
            _log = log
            _http = If(handler Is Nothing, New HttpClient(), New HttpClient(handler))
            _http.Timeout = Timeout.InfiniteTimeSpan
            Queue = New WorkQueue(_opt.MaxConcurrency)
        End Sub

        ''' <summary>The line for the server's MaxConcurrency slots.</summary>
        Public ReadOnly Property Queue As WorkQueue

        Public ReadOnly Property Options As TextToSpeechOptions
            Get
                Return _opt
            End Get
        End Property

        ''' <summary>Whether a speech server is set up (TextToSpeech.Enabled with a BaseUrl).</summary>
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

        ''' <summary>The host of BaseUrl: the only host the vault entry may be sent to.</summary>
        Public ReadOnly Property Host As String
            Get
                Dim u As Uri = Nothing
                Return If(Uri.TryCreate(BaseUrl, UriKind.Absolute, u), u.Host, Nothing)
            End Get
        End Property

        ''' <summary>TextToSpeech.ApiKey is set: it wins over the vault entry.</summary>
        Public ReadOnly Property KeyInConfig As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(_opt.ApiKey)
            End Get
        End Property

        ''' <summary>The API key: TextToSpeech.ApiKey, else the vault entry ApiKeySecret (read again after 30 seconds, or after ForgetKey).</summary>
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
                _log?.Warn("Speech", "Could not read the speech server's key from the vault: " & ex.Message)
            End Try
            SyncLock _lock
                _key = If(String.IsNullOrWhiteSpace(v), Nothing, v.Trim())
                _keyAt = DateTime.UtcNow
                Return _key
            End SyncLock
        End Function

        ''' <summary>The key changed: read it again (and the voices it can see).</summary>
        Public Sub ForgetKey()
            SyncLock _lock
                _keyAt = DateTime.MinValue
                _speakers = Nothing
            End SyncLock
        End Sub

        Private Sub ForgetSpeakers()
            SyncLock _lock
                _speakers = Nothing
            End SyncLock
        End Sub

        ' ------------------------------------------------------------------ requests

        ''' <summary>A request to the server with its key; a failure becomes a SpeechException with the server's reason.</summary>
        Private Async Function SendAsync(method As HttpMethod, path As String, content As HttpContent, ct As CancellationToken,
                                         Optional headersOnly As Boolean = False) As Task(Of HttpResponseMessage)
            If Not Configured Then Throw New SpeechException(409, "No speech server is set up (TextToSpeech.BaseUrl).")
            Dim key = Await KeyAsync().ConfigureAwait(False)
            If String.IsNullOrEmpty(key) Then Throw New SpeechException(409, "The speech server's API key is not set: an administrator sets it on the Audio tab (Settings).")
            Dim req As New HttpRequestMessage(method, BaseUrl & path) With {.Content = content}
            req.Headers.Add("X-API-Key", key)
            Dim resp As HttpResponseMessage
            Try
                resp = Await _http.SendAsync(req, If(headersOnly, HttpCompletionOption.ResponseHeadersRead, HttpCompletionOption.ResponseContentRead), ct).ConfigureAwait(False)
            Catch ex As HttpRequestException
                Throw New SpeechException(502, $"The speech server ({BaseUrl}) cannot be reached: {ex.Message}")
            End Try
            If resp.IsSuccessStatusCode Then Return resp
            Dim body = ""
            Try
                body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IOException
            End Try
            Dim status = CInt(resp.StatusCode)
            resp.Dispose()
            Dim detail = DetailOf(body)
            If status = 401 OrElse status = 403 Then Throw New SpeechException(502, "The speech server refused the API key" & If(detail Is Nothing, ".", ": " & detail))
            If status = 404 Then Throw New SpeechException(404, If(detail, "The speech server does not know that voice."))
            If status = 400 OrElse status = 422 Then Throw New SpeechException(400, If(detail, $"The speech server refused the request ({status})."))
            Throw New SpeechException(502, $"The speech server failed ({status})" & If(detail Is Nothing, ".", ": " & detail))
        End Function

        ''' <summary>The reason in an error body: {"detail": "..."}, or a validation list [{"loc": [...], "msg": "..."}].</summary>
        Public Shared Function DetailOf(body As String) As String
            If String.IsNullOrWhiteSpace(body) Then Return Nothing
            Try
                Dim d = JsonNode.Parse(body)?("detail")
                Dim list = TryCast(d, JsonArray)
                If list IsNot Nothing Then Return TextUtil.Truncate(String.Join("; ", list.Select(Function(x) FieldOf(x?("loc")) & x?("msg")?.ToString())), 500)
                If d IsNot Nothing Then Return TextUtil.Truncate(d.ToString(), 500)
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is InvalidOperationException
            End Try
            Return TextUtil.Truncate(body.Trim(), 300)
        End Function

        Private Shared Function FieldOf(loc As JsonNode) As String
            Dim a = TryCast(loc, JsonArray)
            If a Is Nothing OrElse a.Count = 0 Then Return ""
            Return a(a.Count - 1)?.ToString() & ": "
        End Function

        Private Function Deadline(ct As CancellationToken, Optional seconds As Integer = 0) As CancellationTokenSource
            Dim cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            cts.CancelAfter(TimeSpan.FromSeconds(If(seconds > 0, seconds, Math.Max(10, _opt.TimeoutSeconds))))
            Return cts
        End Function

        ''' <summary>Waits in line for one of the server's slots (at most TimeoutSeconds); dispose the result to free it.</summary>
        Private Async Function EnterAsync(owner As String, label As String, ct As CancellationToken) As Task(Of IDisposable)
            Using wait = Deadline(ct)
                Try
                    Return Await Queue.EnterAsync(If(owner, ""), label, wait.Token).ConfigureAwait(False)
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New SpeechException(503, $"The speech server was busy with other requests for {Math.Max(10, _opt.TimeoutSeconds)} s.")
                End Try
            End Using
        End Function

        ''' <summary>Work that makes speech: it waits its turn in line, then has at most TimeoutSeconds.</summary>
        Private Async Function WithSlotAsync(Of T)(owner As String, label As String, work As Func(Of CancellationToken, Task(Of T)), ct As CancellationToken) As Task(Of T)
            Using slot = Await EnterAsync(owner, label, ct).ConfigureAwait(False)
                Using cts = Deadline(ct)
                    Try
                        Return Await work(cts.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                        Throw New SpeechException(504, $"The speech server did not finish within {Math.Max(10, _opt.TimeoutSeconds)} s.")
                    End Try
                End Using
            End Using
        End Function

        Private Shared Function JsonBody(body As JsonObject) As HttpContent
            Return New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        End Function

        ' ------------------------------------------------------------------ voices and languages

        ''' <summary>{"builtin": [...], "voices": [...]}: the built-in speakers and the cloned voices (kept for 60 seconds).</summary>
        Public Async Function SpeakersAsync(refresh As Boolean, ct As CancellationToken) As Task(Of JsonObject)
            SyncLock _lock
                If Not refresh AndAlso _speakers IsNot Nothing AndAlso _speakersAt > DateTime.UtcNow.AddSeconds(-60) Then Return CType(_speakers.DeepClone(), JsonObject)
            End SyncLock
            Using cts = Deadline(ct, 20)
                Try
                    Using resp = Await SendAsync(HttpMethod.Get, "/speakers", Nothing, cts.Token).ConfigureAwait(False)
                        Dim j = JsonNode.Parse(Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False))
                        Dim result As New JsonObject From {{"builtin", Names(j?("builtin"))}, {"voices", Names(j?("voices"))}}
                        SyncLock _lock
                            _speakers = result
                            _speakersAt = DateTime.UtcNow
                        End SyncLock
                        Return CType(result.DeepClone(), JsonObject)
                    End Using
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Throw New SpeechException(504, "The speech server did not answer within 20 s.")
                Catch ex As JsonException
                    Throw New SpeechException(502, "The speech server's list of voices could not be read.")
                End Try
            End Using
        End Function

        ''' <summary>Names from a list of names (or of objects with a name).</summary>
        Private Shared Function Names(n As JsonNode) As JsonArray
            Dim result As New JsonArray()
            Dim list = TryCast(n, JsonArray)
            If list Is Nothing Then Return result
            For Each x In list
                Dim o = TryCast(x, JsonObject)
                Dim s = If(o IsNot Nothing, If(o("name"), o("id"))?.ToString(), x?.ToString())
                If Not String.IsNullOrWhiteSpace(s) Then result.Add(JsonValue.Create(s))
            Next
            Return result
        End Function

        ''' <summary>The languages the server speaks (asked once; the known list while it cannot be asked).</summary>
        Public Async Function LanguagesAsync(ct As CancellationToken) As Task(Of List(Of String))
            SyncLock _lock
                If _languages IsNot Nothing Then Return New List(Of String)(_languages)
            End SyncLock
            Try
                Using cts = Deadline(ct, 20)
                    Using resp = Await SendAsync(HttpMethod.Get, "/languages", Nothing, cts.Token).ConfigureAwait(False)
                        Dim j = JsonNode.Parse(Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False))
                        Dim list = Names(If(TypeOf j Is JsonObject, j("languages"), j)).Select(Function(x) x.ToString().ToLowerInvariant()).ToList()
                        If list.Count > 0 Then
                            SyncLock _lock
                                _languages = list
                            End SyncLock
                            Return New List(Of String)(list)
                        End If
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is SpeechException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is JsonException
            End Try
            Return KnownLanguages.ToList()
        End Function

        Public Shared Function IsVoiceName(name As String) As Boolean
            Return Not String.IsNullOrEmpty(name) AndAlso VoiceNamePattern.IsMatch(name)
        End Function

        ''' <summary>A voice's key: "speaker:Name" for a built-in speaker, "voice:name" for a cloned voice.</summary>
        Public Shared Function VoiceKey(cloned As Boolean, name As String) As String
            Return If(cloned, "voice:", "speaker:") & name
        End Function

        Public Shared Function ParseVoiceKey(key As String) As (Cloned As Boolean, Name As String)
            Dim k = If(key, "").Trim()
            If k.StartsWith("voice:", StringComparison.OrdinalIgnoreCase) Then Return (True, k.Substring(6).Trim())
            If k.StartsWith("speaker:", StringComparison.OrdinalIgnoreCase) Then Return (False, k.Substring(8).Trim())
            Return (False, k)
        End Function

        ' ------------------------------------------------------------------ speech

        Private Shared Function IsVoiceKey(s As String) As Boolean
            Return s.StartsWith("speaker:", StringComparison.OrdinalIgnoreCase) OrElse s.StartsWith("voice:", StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>
        ''' The voice a request asks for, as a key: "voiceKey", else the server's own "voice" (a cloned voice's name, or a key)
        ''' or "speaker" field, else fallback (the user's chosen voice), else the default speaker.
        ''' </summary>
        Public Shared Function RequestedVoice(input As JsonElement, fallback As String, opt As TextToSpeechOptions) As String
            Dim key = JsonUtil.GetString(input, "voiceKey")
            If Not String.IsNullOrWhiteSpace(key) Then Return key.Trim()
            Dim cloned = If(JsonUtil.GetString(input, "voice"), "").Trim()
            If IsVoiceKey(cloned) Then Return cloned
            If cloned.Length > 0 Then Return VoiceKey(True, cloned)
            Dim speaker = JsonUtil.GetString(input, "speaker")
            If Not String.IsNullOrWhiteSpace(speaker) Then Return VoiceKey(False, speaker.Trim())
            If Not String.IsNullOrWhiteSpace(fallback) Then Return fallback.Trim()
            Return VoiceKey(False, opt.DefaultSpeaker)
        End Function

        ''' <summary>
        ''' The JSON for /tts from what a page sent: the text, the voice (its key) and any of the speech settings, each checked
        ''' against its range. A setting the page did not send comes from defaults (the voice's personality), else the server's
        ''' own default.
        ''' </summary>
        Public Shared Function BuildRequest(input As JsonElement, voice As String, defaults As JsonObject, opt As TextToSpeechOptions, languages As ICollection(Of String), streaming As Boolean) As JsonObject
            Dim text = If(JsonUtil.GetString(input, "text"), "").Trim()
            If text.Length = 0 Then Throw New SpeechException(400, "There is no text to speak.")
            If text.Length > opt.MaxTextChars Then Throw New SpeechException(400, $"The text is longer than {opt.MaxTextChars:N0} characters.")
            Dim body As New JsonObject From {{"text", text}}
            Dim v = ParseVoiceKey(voice)
            If String.IsNullOrWhiteSpace(v.Name) Then v = (False, opt.DefaultSpeaker)
            If v.Cloned Then
                If Not IsVoiceName(v.Name) Then Throw New SpeechException(400, $"'{v.Name}' is not a voice name.")
                body("voice") = v.Name
            Else
                body("speaker") = v.Name
            End If
            Dim language = If(JsonUtil.GetString(input, "language"), If(defaults?("language")?.ToString(), opt.DefaultLanguage))
            language = If(language, "en").Trim().ToLowerInvariant()
            If languages IsNot Nothing AndAlso languages.Count > 0 AndAlso Not languages.Contains(language) Then
                Throw New SpeechException(400, $"The speech server does not speak '{language}' (it speaks {String.Join(", ", languages)}).")
            End If
            body("language") = language
            Num(body, input, defaults, "speed", 0, 3, True)
            Num(body, input, defaults, "temperature", 0, 2, True)
            Whole(body, input, defaults, "top_k", 1, 1000)
            Num(body, input, defaults, "top_p", 0, 1, True)
            Num(body, input, defaults, "repetition_penalty", 1, 50, False)
            Num(body, input, defaults, "length_penalty", -100, 100, False)
            Flag(body, input, defaults, "enable_text_splitting")
            If streaming Then Whole(body, input, defaults, "stream_chunk_size", 5, 200)
            Return body
        End Function

        ''' <summary>The speech settings a voice's personality may keep (the /tts fields other than the text and the voice).</summary>
        Public Shared ReadOnly SettingNames As String() = {"language", "speed", "temperature", "top_k", "top_p", "repetition_penalty", "length_penalty", "enable_text_splitting", "stream_chunk_size"}

        ''' <summary>A personality's speech settings, checked like a request's: only the known settings, each in its range.</summary>
        Public Shared Function CleanSettings(input As JsonElement, opt As TextToSpeechOptions, languages As ICollection(Of String)) As JsonObject
            Dim result As New JsonObject()
            If input.ValueKind <> JsonValueKind.Object Then Return result
            Dim lang = JsonUtil.GetString(input, "language")
            If Not String.IsNullOrWhiteSpace(lang) Then
                lang = lang.Trim().ToLowerInvariant()
                If languages IsNot Nothing AndAlso languages.Count > 0 AndAlso Not languages.Contains(lang) Then Throw New SpeechException(400, $"The speech server does not speak '{lang}'.")
                result("language") = lang
            End If
            Num(result, input, Nothing, "speed", 0, 3, True)
            Num(result, input, Nothing, "temperature", 0, 2, True)
            Whole(result, input, Nothing, "top_k", 1, 1000)
            Num(result, input, Nothing, "top_p", 0, 1, True)
            Num(result, input, Nothing, "repetition_penalty", 1, 50, False)
            Num(result, input, Nothing, "length_penalty", -100, 100, False)
            Flag(result, input, Nothing, "enable_text_splitting")
            Whole(result, input, Nothing, "stream_chunk_size", 5, 200)
            Return result
        End Function

        Private Shared Sub Num(body As JsonObject, input As JsonElement, defaults As JsonObject, name As String, min As Double, max As Double, exclusiveMin As Boolean)
            Dim v = If(NumberOf(input, name), NumberOf(defaults, name))
            If Not v.HasValue Then Return
            Dim x = v.Value
            If Double.IsNaN(x) OrElse Double.IsInfinity(x) OrElse x > max OrElse x < min OrElse (exclusiveMin AndAlso x = min) Then
                Throw New SpeechException(400, $"{name} must be {If(exclusiveMin, "above", "at least")} {min.ToString(CultureInfo.InvariantCulture)} and at most {max.ToString(CultureInfo.InvariantCulture)}.")
            End If
            body(name) = x
        End Sub

        Private Shared Sub Whole(body As JsonObject, input As JsonElement, defaults As JsonObject, name As String, min As Integer, max As Integer)
            Dim v = If(NumberOf(input, name), NumberOf(defaults, name))
            If Not v.HasValue Then Return
            Dim x = v.Value
            If x <> Math.Floor(x) OrElse x < min OrElse x > max Then Throw New SpeechException(400, $"{name} must be a whole number from {min} to {max}.")
            body(name) = CInt(x)
        End Sub

        Private Shared Sub Flag(body As JsonObject, input As JsonElement, defaults As JsonObject, name As String)
            Dim p As JsonElement
            If input.ValueKind = JsonValueKind.Object AndAlso input.TryGetProperty(name, p) AndAlso (p.ValueKind = JsonValueKind.True OrElse p.ValueKind = JsonValueKind.False) Then
                body(name) = p.ValueKind = JsonValueKind.True
                Return
            End If
            Dim d = TryCast(If(defaults Is Nothing, Nothing, defaults(name)), JsonValue)
            Dim b As Boolean
            If d IsNot Nothing AndAlso d.TryGetValue(b) Then body(name) = b
        End Sub

        Private Shared Function NumberOf(el As JsonElement, name As String) As Double?
            Dim p As JsonElement
            If el.ValueKind <> JsonValueKind.Object OrElse Not el.TryGetProperty(name, p) Then Return Nothing
            Select Case p.ValueKind
                Case JsonValueKind.Number
                    Return p.GetDouble()
                Case JsonValueKind.Null, JsonValueKind.Undefined
                    Return Nothing
                Case JsonValueKind.String
                    Dim s = p.GetString()
                    If String.IsNullOrWhiteSpace(s) Then Return Nothing
                    Dim d As Double
                    If Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then Return d
            End Select
            Throw New SpeechException(400, $"{name} must be a number.")
        End Function

        Private Shared Function NumberOf(o As JsonObject, name As String) As Double?
            Dim n = TryCast(If(o Is Nothing, Nothing, o(name)), JsonValue)
            If n Is Nothing Then Return Nothing
            Dim d As Double
            If n.TryGetValue(d) Then Return d
            Dim i As Integer
            If n.TryGetValue(i) Then Return i
            Return Nothing
        End Function

        ''' <summary>The whole speech as a WAV file (/tts).</summary>
        Public Function SynthesizeAsync(body As JsonObject, ct As CancellationToken, Optional owner As String = Nothing) As Task(Of Byte())
            Dim request = CType(body.DeepClone(), JsonObject)
            request.Remove("stream_chunk_size")
            Return WithSlotAsync(Of Byte())(owner, "speech",
                Async Function(t)
                    Using resp = Await SendAsync(HttpMethod.Post, "/tts", JsonBody(request), t).ConfigureAwait(False)
                        Dim data = Await resp.Content.ReadAsByteArrayAsync(t).ConfigureAwait(False)
                        If data.Length <= 44 Then Throw New SpeechException(502, "The speech server sent no audio.")
                        Return data
                    End Using
                End Function, ct)
        End Function

        ''' <summary>Speech as it is made (/tts/stream; format wav or pcm). The first audio comes after about 1.5 s.</summary>
        Public Async Function OpenStreamAsync(body As JsonObject, format As String, ct As CancellationToken, Optional owner As String = Nothing) As Task(Of SpeechStream)
            Dim slot = Await EnterAsync(owner, "speech", ct).ConfigureAwait(False)
            Dim cts = Deadline(ct)
            Try
                Dim resp = Await SendAsync(HttpMethod.Post, "/tts/stream?format=" & If(format = "wav", "wav", "pcm"), JsonBody(body), cts.Token, headersOnly:=True).ConfigureAwait(False)
                Dim s = Await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(False)
                Return New SpeechStream(resp, s, cts.Token, Sub()
                                                                 slot.Dispose()
                                                                 cts.Dispose()
                                                             End Sub)
            Catch
                slot.Dispose()
                cts.Dispose()
                Throw
            End Try
        End Function

        ' ------------------------------------------------------------------ cloned voices

        Public Shared Sub CheckClone(s As VoiceCloneSettings)
            If s.GptCondLen < 1 OrElse s.GptCondLen > 60 Then Throw New SpeechException(400, "gpt_cond_len is 1 to 60 seconds.")
            If s.GptCondChunkLen < 1 OrElse s.GptCondChunkLen > 60 Then Throw New SpeechException(400, "gpt_cond_chunk_len is 1 to 60 seconds.")
            If s.GptCondChunkLen > s.GptCondLen Then Throw New SpeechException(400, "gpt_cond_chunk_len must not be larger than gpt_cond_len.")
            If s.MaxRefLength < 1 OrElse s.MaxRefLength > 120 Then Throw New SpeechException(400, "max_ref_length is 1 to 120 seconds.")
            If s.TrimSilenceDb.HasValue AndAlso (s.TrimSilenceDb.Value < 10 OrElse s.TrimSilenceDb.Value > 80) Then Throw New SpeechException(400, "trim_silence_db is 10 to 80 dB (or off).")
        End Sub

        Private Shared Function MimeOf(fileName As String) As String
            Select Case Path.GetExtension(If(fileName, "")).ToLowerInvariant()
                Case ".wav" : Return "audio/wav"
                Case ".mp3" : Return "audio/mpeg"
                Case ".flac" : Return "audio/flac"
                Case ".ogg", ".oga", ".opus" : Return "audio/ogg"
                Case ".webm" : Return "audio/webm"
                Case ".m4a", ".mp4", ".aac" : Return "audio/mp4"
            End Select
            Return "application/octet-stream"
        End Function

        ''' <summary>Clones a voice from clips of one speaker (POST /voices); a name in use is replaced. Returns the voice's metadata.</summary>
        Public Function CloneAsync(name As String, clips As IList(Of (FileName As String, Data As Byte())), settings As VoiceCloneSettings, ct As CancellationToken,
                                   Optional owner As String = Nothing) As Task(Of JsonNode)
            If Not IsVoiceName(name) Then Throw New SpeechException(400, "A voice name has 1 to 64 letters, digits, _ or -.")
            If clips Is Nothing OrElse clips.Count = 0 Then Throw New SpeechException(400, "Add at least one clip of the voice.")
            Dim s = If(settings, New VoiceCloneSettings())
            CheckClone(s)
            Dim inv = CultureInfo.InvariantCulture
            Return WithSlotAsync(Of JsonNode)(owner, "voice clone",
                Async Function(t)
                    Using form As New MultipartFormDataContent()
                        form.Add(New StringContent(name), "name")
                        For Each c In clips
                            Dim f As New ByteArrayContent(c.Data)
                            f.Headers.ContentType = New MediaTypeHeaderValue(MimeOf(c.FileName))
                            form.Add(f, "files", c.FileName)
                        Next
                        If s.TrimSilenceDb.HasValue Then form.Add(New StringContent(s.TrimSilenceDb.Value.ToString(inv)), "trim_silence_db")
                        form.Add(New StringContent(If(s.Normalize, "true", "false")), "normalize")
                        form.Add(New StringContent(s.GptCondLen.ToString(inv)), "gpt_cond_len")
                        form.Add(New StringContent(s.GptCondChunkLen.ToString(inv)), "gpt_cond_chunk_len")
                        form.Add(New StringContent(s.MaxRefLength.ToString(inv)), "max_ref_length")
                        Using resp = Await SendAsync(HttpMethod.Post, "/voices", form, t).ConfigureAwait(False)
                            Dim text = Await resp.Content.ReadAsStringAsync(t).ConfigureAwait(False)
                            ForgetSpeakers()
                            Return ParseOrText(text)
                        End Using
                    End Using
                End Function, ct)
        End Function

        ''' <summary>A cloned voice's clips, settings and expiry date (GET /voices/{name}).</summary>
        Public Async Function VoiceInfoAsync(name As String, ct As CancellationToken) As Task(Of JsonNode)
            If Not IsVoiceName(name) Then Throw New SpeechException(404, "There is no voice with that name.")
            Using cts = Deadline(ct, 20)
                Using resp = Await SendAsync(HttpMethod.Get, "/voices/" & Uri.EscapeDataString(name), Nothing, cts.Token).ConfigureAwait(False)
                    Return ParseOrText(Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False))
                End Using
            End Using
        End Function

        Public Async Function DeleteVoiceAsync(name As String, ct As CancellationToken) As Task
            If Not IsVoiceName(name) Then Throw New SpeechException(404, "There is no voice with that name.")
            Using cts = Deadline(ct, 30)
                Using resp = Await SendAsync(HttpMethod.Delete, "/voices/" & Uri.EscapeDataString(name), Nothing, cts.Token).ConfigureAwait(False)
                End Using
            End Using
            ForgetSpeakers()
        End Function

        Private Shared Function ParseOrText(text As String) As JsonNode
            If String.IsNullOrWhiteSpace(text) Then Return New JsonObject()
            Try
                Return JsonNode.Parse(text)
            Catch ex As JsonException
                Return New JsonObject From {{"message", TextUtil.Truncate(text, 2000)}}
            End Try
        End Function

        ' ------------------------------------------------------------------ WAV

        ''' <summary>16-bit mono PCM samples as a WAV file.</summary>
        Public Shared Function PcmToWav(pcm As Byte(), Optional rate As Integer = SampleRate) As Byte()
            Using ms As New MemoryStream(44 + pcm.Length)
                Using w As New BinaryWriter(ms, Encoding.ASCII, leaveOpen:=True)
                    w.Write(Encoding.ASCII.GetBytes("RIFF"))
                    w.Write(36 + pcm.Length)
                    w.Write(Encoding.ASCII.GetBytes("WAVEfmt "))
                    w.Write(16)
                    w.Write(CShort(1))
                    w.Write(CShort(1))
                    w.Write(rate)
                    w.Write(rate * 2)
                    w.Write(CShort(2))
                    w.Write(CShort(16))
                    w.Write(Encoding.ASCII.GetBytes("data"))
                    w.Write(pcm.Length)
                    w.Write(pcm)
                End Using
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>The samples of a WAV file: its data chunk (a streamed file's placeholder length means "to the end").</summary>
        Public Shared Function WavToPcm(wav As Byte()) As Byte()
            If wav Is Nothing OrElse wav.Length < 12 OrElse Encoding.ASCII.GetString(wav, 0, 4) <> "RIFF" Then Return If(wav, Array.Empty(Of Byte)())
            Dim at = 12
            While at + 8 <= wav.Length
                Dim id = Encoding.ASCII.GetString(wav, at, 4)
                Dim size = CLng(BitConverter.ToUInt32(wav, at + 4))
                Dim start = at + 8
                If id = "data" Then
                    Dim len = If(size = 0 OrElse size > wav.Length - start, wav.Length - start, CInt(size))
                    len -= len Mod 2
                    Dim pcm(Math.Max(0, len) - 1) As Byte
                    If len > 0 Then Array.Copy(wav, start, pcm, 0, len)
                    Return pcm
                End If
                at = CInt(Math.Min(CLng(wav.Length), start + size + (size Mod 2)))
            End While
            Return Array.Empty(Of Byte)()
        End Function

        ''' <summary>How long 16-bit mono PCM at the server's rate plays.</summary>
        Public Shared Function PcmDurationMs(pcmBytes As Long) As Long
            Return pcmBytes * 1000 \ (SampleRate * 2)
        End Function
    End Class

End Namespace
