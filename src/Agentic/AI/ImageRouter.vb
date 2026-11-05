Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace AI

    ''' <summary>A picture and the model that made it.</summary>
    Public NotInheritable Class RoutedImage
        Public Property Result As ImageGenerationResult
        ''' <summary>The model that made it ("gpt-image-2" …).</summary>
        Public Property Model As String
        ''' <summary>Models that failed first, with why.</summary>
        Public Property Failures As New List(Of String)
    End Class

    ''' <summary>
    ''' All image generation (chats, agents, /v1/images): only the images API in ImageGeneration (BaseUrl + ApiKey, e.g.
    ''' yuanqiaoyun's gpt-image models) - never the chat provider. ImageGeneration.Model is asked first; when it cannot make
    ''' the picture, the next model of the same API is asked for that request: FallbackModels, then (AutoFallback) the API's
    ''' other image models. A model that just failed is skipped for a few minutes, so one outage does not slow every request.
    ''' </summary>
    Public NotInheritable Class ImageRouter
        Private ReadOnly _provider As Func(Of IAIProvider)
        Private ReadOnly _opt As ImageGenerationOptions
        Private ReadOnly _listModels As Func(Of CancellationToken, Task(Of List(Of String)))
        ''' <summary>Models that failed a moment ago, per kind of request ("make:gpt-image-2", "edit:gpt-image-2").</summary>
        Private ReadOnly _pausedUntil As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>When every model failed last, and why, per kind (a request right after it gets that answer at once).</summary>
        Private ReadOnly _allFailed As New ConcurrentDictionary(Of String, (At As DateTime, Message As String))(StringComparer.Ordinal)
        ''' <summary>How long "every image model just failed" is answered without asking them all again.</summary>
        Private Shared ReadOnly AllFailedMemory As TimeSpan = TimeSpan.FromSeconds(60)

        ''' <summary>Model ids that make images, as they appear in model lists.</summary>
        Private Shared ReadOnly ImageModelPattern As New Regex("(gpt-image|dall-e|imagen|flux|stable-diffusion|sdxl|sd3|seedream|midjourney|ideogram|recraft|cogview|hidream|kolors|playground-v|qwen-image|nano-banana|-image(-preview|-generation)?$|image-gen)",
                                                               RegexOptions.IgnoreCase Or RegexOptions.Compiled)

        ''' <param name="provider">The images API (OpenAI-compatible, at ImageGeneration.BaseUrl).</param>
        ''' <param name="listModels">That API's model ids (for AutoFallback); Nothing = only the configured models.</param>
        Public Sub New(provider As Func(Of IAIProvider), options As ImageGenerationOptions, listModels As Func(Of CancellationToken, Task(Of List(Of String))))
            _provider = provider
            _opt = If(options, New ImageGenerationOptions())
            _listModels = listModels
        End Sub

        ''' <summary>On, with an images API and its key.</summary>
        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled AndAlso _provider IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(_opt.BaseUrl) AndAlso Not String.IsNullOrWhiteSpace(_opt.ApiKey)
            End Get
        End Property

        ''' <summary>The client for ImageGeneration's images API (Nothing without a BaseUrl).</summary>
        Public Shared Function CreateApi(opt As ImageGenerationOptions) As OpenAICompatibleProvider
            If opt Is Nothing OrElse String.IsNullOrWhiteSpace(opt.BaseUrl) Then Return Nothing
            Return New OpenAICompatibleProvider("images", New AIProviderOptions With {
                .BaseUrl = opt.BaseUrl.Trim(), .ApiKey = opt.ApiKey, .ImageGenerationPath = "/images/generations", .ImageEditPath = "/images/edits", .ModelsPath = "/models",
                .TimeoutSeconds = Math.Max(60, opt.TimeoutSeconds), .UseStreaming = False})
        End Function

        ''' <summary>A router over that API (not enabled when api is Nothing).</summary>
        Public Shared Function ForApi(opt As ImageGenerationOptions, api As OpenAICompatibleProvider) As ImageRouter
            Return New ImageRouter(If(api Is Nothing, Nothing, New Func(Of IAIProvider)(Function() api)), opt,
                                   If(api Is Nothing, Nothing, New Func(Of CancellationToken, Task(Of List(Of String)))(Function(ct) api.ListModelsAsync(ct))))
        End Function

        Public Shared Function IsImageModel(id As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(id) AndAlso ImageModelPattern.IsMatch(id)
        End Function

        ''' <summary>Models an administrator turned off for everyone (Model control).</summary>
        Public Property TurnedOff As Func(Of String, Boolean)

        ''' <summary>The image models, in the order they are asked.</summary>
        Public Async Function RoutesAsync(ct As CancellationToken) As Task(Of List(Of String))
            Dim routes As New List(Of String)
            For Each m In {_opt.Model}.Concat(_opt.FallbackModels).Where(Function(x) Not String.IsNullOrWhiteSpace(x))
                If Not routes.Contains(m.Trim(), StringComparer.OrdinalIgnoreCase) Then routes.Add(m.Trim())
            Next
            If _opt.AutoFallback AndAlso _listModels IsNot Nothing Then
                Dim ids As List(Of String) = Nothing
                Try
                    ids = Await _listModels(ct).ConfigureAwait(False)
                Catch ex As Exception When Not ct.IsCancellationRequested
                    ' The model list is unavailable: the configured models still work.
                End Try
                For Each id In If(ids, New List(Of String)).Where(AddressOf IsImageModel).OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)
                    If Not routes.Contains(id, StringComparer.OrdinalIgnoreCase) Then routes.Add(id)
                Next
            End If
            ' Models an administrator turned off (Model control) are not asked.
            If TurnedOff IsNot Nothing Then routes = routes.Where(Function(r) Not TurnedOff(r)).ToList()
            Return routes
        End Function

        ''' <summary>
        ''' Makes the picture with the first model that can (preferredModel first when it is one of them). onAttempt hears which
        ''' model is being asked (for status lines). Throws AiException with every model's reason when none could.
        ''' </summary>
        Public Function GenerateAsync(prompt As String, size As String, ct As CancellationToken, Optional onAttempt As Func(Of String, Task) = Nothing,
                                      Optional preferredModel As String = Nothing) As Task(Of RoutedImage)
            Dim timeout = TimeSpan.FromSeconds(Math.Max(30, _opt.TimeoutSeconds))
            Return RunAsync("make", "make the picture", Function(model) _provider().GenerateImageWithModelAsync(model, True, prompt, size, timeout, ct), ct, onAttempt, preferredModel)
        End Function

        ''' <summary>
        ''' Changes a picture, or builds a new one on it, with the first model that can: POST {BaseUrl}/images/edits with the
        ''' picture (one) and the prompt saying what to change. Same order and fallbacks as GenerateAsync.
        ''' </summary>
        Public Function EditAsync(prompt As String, size As String, image As Byte(), imageMime As String, imageName As String, ct As CancellationToken,
                                  Optional onAttempt As Func(Of String, Task) = Nothing, Optional preferredModel As String = Nothing) As Task(Of RoutedImage)
            If image Is Nothing OrElse image.Length = 0 Then Throw New AiException(AiErrorKind.InvalidRequest, "There is no picture to change.")
            Dim timeout = TimeSpan.FromSeconds(Math.Max(30, _opt.TimeoutSeconds))
            Return RunAsync("edit", "change the picture",
                            Function(model) _provider().EditImageWithModelAsync(model, prompt, size, image, imageMime, imageName, timeout, ct), ct, onAttempt, preferredModel)
        End Function

        ''' <summary>Asks the image models in order (paused ones last) until one returns a picture.</summary>
        Private Async Function RunAsync(kind As String, what As String, attempt As Func(Of String, Task(Of ImageGenerationResult)), ct As CancellationToken,
                                        onAttempt As Func(Of String, Task), preferredModel As String) As Task(Of RoutedImage)
            If Not Enabled Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "Image generation is not set up (ImageGeneration.BaseUrl and ApiKey).")
            Dim routes = Await RoutesAsync(ct).ConfigureAwait(False)
            If routes.Count = 0 Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "No image model is configured (ImageGeneration.Model).")
            If Not String.IsNullOrWhiteSpace(preferredModel) Then
                Dim match = routes.FirstOrDefault(Function(r) String.Equals(r, preferredModel.Trim(), StringComparison.OrdinalIgnoreCase))
                If match Is Nothing Then Throw New AiException(AiErrorKind.InvalidRequest, $"'{preferredModel}' is not one of the image models: {String.Join(", ", routes)}.")
                routes.Remove(match)
                routes.Insert(0, match)
            End If
            Dim now = DateTime.UtcNow
            ' Every model failed a moment ago: say so at once instead of asking them all again (an outage takes a while).
            Dim last As (At As DateTime, Message As String) = Nothing
            If _allFailed.TryGetValue(kind, last) AndAlso now - last.At < AllFailedMemory AndAlso routes.All(Function(r) IsPaused(kind, r, now)) Then
                Throw New AiException(AiErrorKind.ModelUnavailable, $"{last.Message} (checked {CInt((now - last.At).TotalSeconds)} s ago)")
            End If
            ' Models that failed a moment ago go last (they are still tried when nothing else works).
            Dim ordered = routes.Where(Function(r) Not IsPaused(kind, r, now)).Concat(routes.Where(Function(r) IsPaused(kind, r, now))).ToList()
            Dim failures As New List(Of String)
            For Each model In ordered
                ct.ThrowIfCancellationRequested()
                If onAttempt IsNot Nothing Then Await onAttempt(model).ConfigureAwait(False)
                Dim failure As String = Nothing
                Try
                    Dim result = Await attempt(model).ConfigureAwait(False)
                    If result?.Data IsNot Nothing AndAlso result.Data.Length > 0 Then
                        _pausedUntil.TryRemove(kind & ":" & model, Nothing)
                        Return New RoutedImage With {.Result = result, .Model = model, .Failures = failures}
                    End If
                    failure = "no image came back"
                Catch ex As OperationCanceledException When ct.IsCancellationRequested
                    Throw
                Catch ex As Exception When TypeOf ex Is AiException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is FormatException OrElse
                                           TypeOf ex Is Text.Json.JsonException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is IO.IOException OrElse
                                           TypeOf ex Is Net.Sockets.SocketException
                    failure = ShortReason(ex.Message)
                End Try
                failures.Add($"{model}: {failure}")
                _pausedUntil(kind & ":" & model) = DateTime.UtcNow.AddSeconds(Math.Max(0, _opt.FailedModelPauseSeconds))
            Next
            Dim message = $"No image model could {what} right now — " & String.Join("; ", failures)
            _allFailed(kind) = (DateTime.UtcNow, message)
            Throw New AiException(AiErrorKind.ModelUnavailable, message)
        End Function

        Private Function IsPaused(kind As String, route As String, now As DateTime) As Boolean
            Dim until As DateTime
            Return _pausedUntil.TryGetValue(kind & ":" & route, until) AndAlso until > now
        End Function

        ''' <summary>The useful part of a provider error (relays wrap the upstream's reason in a lot of text).</summary>
        Friend Shared Function ShortReason(message As String) As String
            Dim m = If(message, "").Replace(vbCr, " ").Replace(vbLf, " ")
            Dim upstream = Regex.Match(m, "OpenAIException - (.+?)(?:\(request id|No fallback model group|$)")
            If upstream.Success Then m = upstream.Groups(1).Value
            m = Regex.Replace(m, "\s*\(request id: [^)]*\)", "")
            m = Regex.Replace(m, "^(litellm\.\w+:\s*)+", "").Trim()
            Return TextUtil.Truncate(m, 160, "…")
        End Function
    End Class

End Namespace
