Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace AI

    ''' <summary>A finished video and the model that made it.</summary>
    Public NotInheritable Class GeneratedVideo
        Public Property Data As Byte()
        Public Property MimeType As String = "video/mp4"
        Public Property Model As String
        Public Property Seconds As Integer
        ''' <summary>Models that failed first, with why.</summary>
        Public Property Failures As New List(Of String)
    End Class

    ''' <summary>
    ''' Video generation through one video API only (VideoGeneration: new-api task routes, e.g. Seedance on yuanqiaoyun):
    ''' POST {BaseUrl}/video/generations starts the task, GET {BaseUrl}/video/generations/{task_id} follows it, and the finished
    ''' video is downloaded. VideoGeneration.Model first, then FallbackModels when it cannot make the video.
    ''' </summary>
    Public NotInheritable Class VideoService
        Private ReadOnly _opt As VideoGenerationOptions
        Private ReadOnly _http As HttpClient
        Private ReadOnly _base As String
        Private ReadOnly _gate As SemaphoreSlim
        Private ReadOnly _pausedUntil As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
        Private Shared ReadOnly Ratios As String() = {"16:9", "9:16", "1:1", "4:3", "3:4", "21:9"}

        ''' <param name="handler">Tests pass a fake server here.</param>
        Public Sub New(options As VideoGenerationOptions, Optional handler As HttpMessageHandler = Nothing)
            _opt = If(options, New VideoGenerationOptions())
            _base = If(_opt.BaseUrl, "").Trim().TrimEnd("/"c)
            _http = New HttpClient(If(handler, New SocketsHttpHandler With {.ConnectTimeout = TimeSpan.FromSeconds(20), .AllowAutoRedirect = False}), handler Is Nothing) With {
                .Timeout = TimeSpan.FromSeconds(180)}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic/1.0")
            If Not String.IsNullOrWhiteSpace(_opt.ApiKey) Then _http.DefaultRequestHeaders.Authorization = New AuthenticationHeaderValue("Bearer", _opt.ApiKey.Trim())
            _gate = New SemaphoreSlim(Math.Max(1, _opt.MaxConcurrency))
        End Sub

        Public ReadOnly Property Options As VideoGenerationOptions
            Get
                Return _opt
            End Get
        End Property

        ''' <summary>On, with a video API and its key.</summary>
        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled AndAlso _base.Length > 0 AndAlso Not String.IsNullOrWhiteSpace(_opt.ApiKey)
            End Get
        End Property

        ''' <summary>Models an administrator turned off for everyone (Model control).</summary>
        Public Property TurnedOff As Func(Of String, Boolean)

        ''' <summary>The video models, in the order they are asked.</summary>
        Public Function Models() As List(Of String)
            Return {_opt.Model}.Concat(_opt.FallbackModels).Where(Function(m) Not String.IsNullOrWhiteSpace(m) AndAlso (TurnedOff Is Nothing OrElse Not TurnedOff(m.Trim()))).Select(Function(m) m.Trim()).
                Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>What one second of this model's video costs a User account, in micro-USD (0 = free).</summary>
        Public Function PricePerSecondMicros(model As String) As Long
            Dim p As Decimal
            If model IsNot Nothing AndAlso _opt.ModelPricePerSecond IsNot Nothing AndAlso _opt.ModelPricePerSecond.TryGetValue(model, p) Then Return CLng(Math.Ceiling(p * 1000000D))
            Return If(_opt.PricePerSecond.HasValue, CLng(Math.Ceiling(_opt.PricePerSecond.Value * 1000000D)), 0L)
        End Function

        Public Function NormalizeRatio(ratio As String) As String
            Dim r = If(ratio, "").Trim()
            Return If(Ratios.Contains(r), r, If(Ratios.Contains(_opt.DefaultRatio), _opt.DefaultRatio, "16:9"))
        End Function

        ''' <summary>
        ''' Makes the video with the first model that can. onProgress hears the model and how far it is ("queued", "40%").
        ''' Throws AiException with every model's reason when none could.
        ''' </summary>
        Public Async Function GenerateAsync(prompt As String, seconds As Integer, ratio As String, ct As CancellationToken,
                                            Optional onProgress As Func(Of String, String, Task) = Nothing) As Task(Of GeneratedVideo)
            If Not Enabled Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "Video generation is not set up (VideoGeneration.BaseUrl and ApiKey).")
            If String.IsNullOrWhiteSpace(prompt) Then Throw New AiException(AiErrorKind.InvalidRequest, "A prompt is required.")
            seconds = Math.Clamp(If(seconds > 0, seconds, _opt.DefaultSeconds), 1, Math.Max(1, _opt.MaxSeconds))
            ratio = NormalizeRatio(ratio)
            Await _gate.WaitAsync(ct).ConfigureAwait(False)
            Try
                Dim models = Me.Models()
                If models.Count = 0 Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "No video model is configured (VideoGeneration.Model).")
                Dim now = DateTime.UtcNow
                ' Models that failed a moment ago go last (they are still tried when nothing else works).
                Dim ordered = models.Where(Function(m) Not IsPaused(m, now)).Concat(models.Where(Function(m) IsPaused(m, now))).ToList()
                Dim failures As New List(Of String)
                For Each model In ordered
                    Dim failure As String = Nothing
                    Try
                        If onProgress IsNot Nothing Then Await onProgress(model, "starting").ConfigureAwait(False)
                        Dim taskId = Await CreateAsync(model, prompt, seconds, ratio, ct).ConfigureAwait(False)
                        Dim url = Await WaitForVideoAsync(model, taskId, ct, onProgress).ConfigureAwait(False)
                        Dim file = Await DownloadAsync(url, ct).ConfigureAwait(False)
                        _pausedUntil.TryRemove(model, Nothing)
                        Return New GeneratedVideo With {.Data = file.Data, .MimeType = file.MimeType, .Model = model, .Seconds = seconds, .Failures = failures}
                    Catch ex As OperationCanceledException When ct.IsCancellationRequested
                        Throw
                    Catch ex As Exception When TypeOf ex Is AiException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException OrElse
                                               TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is FormatException
                        failure = ImageRouter.ShortReason(ex.Message)
                    End Try
                    failures.Add($"{model}: {failure}")
                    _pausedUntil(model) = DateTime.UtcNow.AddSeconds(Math.Max(0, _opt.FailedModelPauseSeconds))
                Next
                Throw New AiException(AiErrorKind.ModelUnavailable, "No video model could make the video right now — " & String.Join("; ", failures))
            Finally
                _gate.Release()
            End Try
        End Function

        Private Function IsPaused(model As String, now As DateTime) As Boolean
            Dim until As DateTime
            Return _pausedUntil.TryGetValue(model, until) AndAlso until > now
        End Function

        ''' <summary>Starts the video; returns the task id.</summary>
        Private Async Function CreateAsync(model As String, prompt As String, seconds As Integer, ratio As String, ct As CancellationToken) As Task(Of String)
            Dim body As New JsonObject From {
                {"model", model}, {"prompt", prompt}, {"duration", seconds},
                {"metadata", New JsonObject From {{"resolution", If(_opt.Resolution, "720p")}, {"ratio", ratio}, {"duration", seconds}}}}
            Using content As New StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
                Using resp = Await _http.PostAsync(_base & "/video/generations", content, ct).ConfigureAwait(False)
                    Dim text = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw AiErrorClassifier.FromHttp(CInt(resp.StatusCode), text)
                    Dim j = ParseJson(text)
                    Dim id = If(j?("task_id")?.ToString(), If(j?("id")?.ToString(), j?("data")?("task_id")?.ToString()))
                    If String.IsNullOrWhiteSpace(id) Then Throw New AiException(AiErrorKind.InvalidResponse, "The video service started no task: " & TextUtil.Truncate(text, 200))
                    Return id
                End Using
            End Using
        End Function

        ''' <summary>Follows the task until it has a video; returns its link.</summary>
        Private Async Function WaitForVideoAsync(model As String, taskId As String, ct As CancellationToken, onProgress As Func(Of String, String, Task)) As Task(Of String)
            Dim deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, _opt.TimeoutSeconds))
            Dim lastNote As String = Nothing
            While True
                ct.ThrowIfCancellationRequested()
                If DateTime.UtcNow > deadline Then Throw New AiException(AiErrorKind.Timeout, $"The video was not ready after {Math.Max(60, _opt.TimeoutSeconds) \ 60} minutes.")
                Dim state As (Status As String, Progress As Integer?, Url As String, [Error] As String)
                Using resp = Await _http.GetAsync(_base & "/video/generations/" & Uri.EscapeDataString(taskId), ct).ConfigureAwait(False)
                    Dim text = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                    ' A status check that fails now and then is waited out; the deadline ends a task that is really gone.
                    If Not resp.IsSuccessStatusCode AndAlso CInt(resp.StatusCode) < 500 Then Throw AiErrorClassifier.FromHttp(CInt(resp.StatusCode), text)
                    If resp.IsSuccessStatusCode Then
                        state = ParseTask(ParseJson(text))
                    Else
                        state = ("running", CType(Nothing, Integer?), CType(Nothing, String), CType(Nothing, String))
                    End If
                End Using
                If state.Status = "done" Then
                    If String.IsNullOrWhiteSpace(state.Url) Then Throw New AiException(AiErrorKind.InvalidResponse, "The video finished without a link to it.")
                    Return state.Url
                End If
                If state.Status = "failed" Then Throw New AiException(AiErrorKind.ModelUnavailable, If(String.IsNullOrWhiteSpace(state.Error), "the video failed", state.Error))
                Dim note = If(state.Progress.HasValue AndAlso state.Progress.Value > 0, $"{state.Progress.Value}%", state.Status)
                If onProgress IsNot Nothing AndAlso note <> lastNote Then
                    lastNote = note
                    Await onProgress(model, note).ConfigureAwait(False)
                End If
                Await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _opt.PollSeconds)), ct).ConfigureAwait(False)
            End While
            Return Nothing
        End Function

        Private Shared Function ParseJson(text As String) As JsonNode
            Try
                Return JsonNode.Parse(text)
            Catch ex As JsonException
                Return Nothing
            End Try
        End Function

        ''' <summary>A task's state from the service's answer: queued | running | done | failed, progress (%), the video link and why it failed.</summary>
        Friend Shared Function ParseTask(json As JsonNode) As (Status As String, Progress As Integer?, Url As String, [Error] As String)
            Dim d = If(TryCast(json?("data"), JsonObject), TryCast(json, JsonObject))
            Dim raw = If(d?("status")?.ToString(), If(json?("status")?.ToString(), "")).Trim().ToUpperInvariant()
            Dim status As String
            Select Case raw
                Case "SUCCESS", "SUCCEEDED", "COMPLETED", "COMPLETE", "FINISHED", "DONE" : status = "done"
                Case "FAILURE", "FAILED", "FAIL", "ERROR", "CANCELLED", "CANCELED", "EXPIRED" : status = "failed"
                Case "NOT_START", "QUEUED", "PENDING", "SUBMITTED", "WAITING", "" : status = "queued"
                Case Else : status = "running"
            End Select
            Dim progress As Integer? = Nothing
            Dim p = Regex.Match(If(If(d?("progress")?.ToString(), json?("progress")?.ToString()), ""), "\d+")
            If p.Success Then progress = Math.Min(100, Integer.Parse(p.Value))
            Dim err = If(d?("fail_reason")?.ToString(), If(d?("error")?("message")?.ToString(), d?("error")?.ToString()))
            If String.IsNullOrWhiteSpace(err) Then err = Nothing
            Return (status, progress, VideoUrlOf(json), err)
        End Function

        ''' <summary>The link to the finished video, wherever the service puts it.</summary>
        Friend Shared Function VideoUrlOf(json As JsonNode) As String
            Dim d = json?("data")
            Dim candidates As JsonNode() = {
                d?("result_url"), d?("url"), d?("video_url"), d?("data")?("content")?("video_url"), d?("content")?("video_url"), d?("output")?("video_url"),
                d?("output")?("url"), json?("result_url"), json?("url"), json?("video_url"), json?("content")?("video_url"), json?("metadata")?("url")}
            For Each c In candidates
                Dim s = TryCast(c, JsonValue)?.ToString()
                If s IsNot Nothing AndAlso Regex.IsMatch(s, "^https?://", RegexOptions.IgnoreCase) Then Return s
            Next
            Dim list = TryCast(d, JsonArray)
            If list IsNot Nothing AndAlso list.Count > 0 Then
                Dim s = If(list(0)?("url")?.ToString(), list(0)?("video_url")?.ToString())
                If s IsNot Nothing AndAlso s.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then Return s
            End If
            Return Nothing
        End Function

        ''' <summary>The finished video: from the service itself (with its key), or from where it linked it (guarded download).</summary>
        Private Async Function DownloadAsync(url As String, ct As CancellationToken) As Task(Of (Data As Byte(), MimeType As String))
            Dim maxBytes = CInt(Math.Min(Integer.MaxValue - 1L, CLng(Math.Max(10, _opt.MaxVideoMb)) * 1024L * 1024L))
            Dim target As Uri = Nothing
            Dim baseUri As Uri = Nothing
            If Uri.TryCreate(url, UriKind.Absolute, target) AndAlso Uri.TryCreate(_base, UriKind.Absolute, baseUri) AndAlso
               String.Equals(target.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) Then
                Using resp = Await _http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
                    If Not resp.IsSuccessStatusCode Then Throw New AiException(AiErrorKind.InvalidResponse, $"The video could not be downloaded: HTTP {CInt(resp.StatusCode)}.")
                    Using s = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False), ms As New MemoryStream()
                        Dim buf(81919) As Byte
                        While True
                            Dim n = Await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
                            If n <= 0 Then Exit While
                            ms.Write(buf, 0, n)
                            If ms.Length > maxBytes Then Throw New AiException(AiErrorKind.InvalidResponse, $"The video is larger than {_opt.MaxVideoMb} MB.")
                        End While
                        Dim data = ms.ToArray()
                        Dim sniffed = OpenAICompatibleProvider.SniffMime(data)
                        If sniffed Is Nothing OrElse Not sniffed.StartsWith("video/", StringComparison.Ordinal) Then Throw New AiException(AiErrorKind.InvalidResponse, "The video link did not return a video.")
                        Return (data, sniffed)
                    End Using
                End Using
            End If
            Return Await OpenAICompatibleProvider.DownloadExternalMediaAsync(url, maxBytes, "video/", ct).ConfigureAwait(False)
        End Function
    End Class

End Namespace
