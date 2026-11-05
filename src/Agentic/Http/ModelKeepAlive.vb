Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Http

    ' Model calls behind a proxy: Cloudflare cuts off a response that sends nothing for 100-120 seconds (HTTP 524). A local
    ' model can take minutes before its first token (waiting for a free slot, reading a long prompt, thinking), so the model
    ' APIs (/v1, /v2) give a call a grace period (Server.ModelKeepAliveAfterSeconds) to answer as usual - errors keep their
    ' real status. Past it they send the headers and keep the connection alive until the answer comes: a ping event
    ' (Anthropic streams), an SSE comment (OpenAI streams) or JSON's insignificant leading whitespace (plain answers). A
    ' failure after that point is reported in the response itself, as its status has already gone out.
    Partial Friend NotInheritable Class AdminApi

        Private Const AnthroPing As String = "event: ping" & vbLf & "data: {""type"":""ping""}" & vbLf & vbLf
        Private Const SseKeepAlive As String = ": keep-alive" & vbLf & vbLf
        Private Const JsonKeepAlive As String = " "

        Private ReadOnly Property KeepAliveAfter As TimeSpan
            Get
                Return TimeSpan.FromSeconds(Math.Max(0.05, _h.Options.Server.ModelKeepAliveAfterSeconds))
            End Get
        End Property

        Private ReadOnly Property KeepAliveEvery As TimeSpan
            Get
                Return TimeSpan.FromSeconds(Math.Max(0.05, _h.Options.Server.ModelKeepAliveIntervalSeconds))
            End Get
        End Property

        ''' <summary>One upstream model call: the routing decision, the local slot it holds and the provider's response.</summary>
        Private NotInheritable Class UpstreamCall
            Implements IDisposable
            Public Decision As RouteDecision
            Public Slot As IDisposable
            Public Response As HttpResponseMessage
            ''' <summary>The model that answered (a custom model's route, or the model asked for).</summary>
            Public Model As String
            ''' <summary>The whole body, for calls that do not stream.</summary>
            Public Text As String
            ''' <summary>Started when the request went to the model (after any wait for a slot): the call's speed.</summary>
            Public Sent As New Diagnostics.Stopwatch()

            Public Sub Dispose() Implements IDisposable.Dispose
                Response?.Dispose()
                Slot?.Dispose()
            End Sub
        End Class

        ''' <summary>
        ''' Routes a custom model, waits for a local slot and sends the request (reading the whole body when readAll). When the
        ''' provider refuses, each of alternates (other providers' copies of the same Claude model) gets the request in turn.
        ''' </summary>
        Private Async Function OpenUpstreamAsync(custom As RouterModel, requested As String, body As JsonNode, readAll As Boolean, ct As CancellationToken,
                                                 Optional alternates As IReadOnlyList(Of String) = Nothing) As Task(Of UpstreamCall)
            Dim u As New UpstreamCall With {.Model = requested}
            Try
                If custom IsNot Nothing Then u.Decision = Await _h.ModelHub.DecideAsync(custom, body, ct).ConfigureAwait(False)
                ' The local model's slots are shared with the agents (and Chat): wait for one.
                u.Slot = Await _h.Gateway.TakeLocalSlotAsync(If(u.Decision?.Model, requested), Nothing, ct).ConfigureAwait(False)
                u.Sent.Start()
                If custom IsNot Nothing Then
                    Dim sent = Await _h.ModelHub.SendWithFallbacksAsync("/chat/completions", body, custom.Definition, u.Decision, ct).ConfigureAwait(False)
                    u.Response = sent.Response
                    u.Model = sent.Model
                Else
                    u.Response = Await _h.ModelHub.SendAsync("/chat/completions", body.ToJsonString(), ct).ConfigureAwait(False)
                    For Each alt In If(alternates, Array.Empty(Of String)())
                        If u.Response.IsSuccessStatusCode Then Exit For
                        u.Response.Dispose()
                        body("model") = alt
                        u.Model = alt
                        u.Sent.Restart()
                        u.Response = Await _h.ModelHub.SendAsync("/chat/completions", body.ToJsonString(), ct).ConfigureAwait(False)
                    Next
                End If
                If readAll Then u.Text = Await u.Response.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                Return u
            Catch
                u.Dispose()
                Throw
            End Try
        End Function

        ''' <summary>An API call's speed for the model picker (never delays the answer); firstOutputMs -1 when not streamed.</summary>
        Private Sub RecordApiSpeed(u As UpstreamCall, via As String, firstOutputMs As Long, outputTokens As Long)
            If _h.ModelHealth Is Nothing Then Return
            Dim unused = _h.ModelHealth.RecordSpeedAsync(u.Model, via, "api", If(firstOutputMs >= 0, firstOutputMs, CType(Nothing, Long?)), u.Sent.ElapsedMilliseconds, outputTokens)
        End Sub

        ''' <summary>True when work finishes within wait, False when the time runs out first.</summary>
        Private Shared Async Function FinishesWithinAsync(work As Task, wait As TimeSpan, ct As CancellationToken) As Task(Of Boolean)
            If work.IsCompleted Then Return True
            Using timer = CancellationTokenSource.CreateLinkedTokenSource(ct)
                Dim first = Await Task.WhenAny(work, Task.Delay(wait, timer.Token)).ConfigureAwait(False)
                timer.Cancel()
                Return first Is work
            End Using
        End Function

        ''' <summary>
        ''' Waits for work once the response has started: writes keepAlive at once (sending the headers) and again every few
        ''' seconds. When the client has gone (a write fails) the work is cancelled, and what it still returns is disposed so
        ''' its local model slot is not held.
        ''' </summary>
        Private Async Function KeepAliveUntilDoneAsync(Of T As {Class, IDisposable})(rc As RequestContext, work As Task(Of T), keepAlive As String, cancelWork As CancellationTokenSource) As Task(Of T)
            Try
                Await rc.WriteStreamAsync(keepAlive).ConfigureAwait(False)
                While Not Await FinishesWithinAsync(work, KeepAliveEvery, rc.CancellationToken).ConfigureAwait(False)
                    Await rc.WriteStreamAsync(keepAlive).ConfigureAwait(False)
                End While
            Catch
                cancelWork.Cancel()
                Dim unused = work.ContinueWith(Sub(finished)
                                                   If finished.Status = TaskStatus.RanToCompletion Then
                                                       finished.Result?.Dispose()
                                                   ElseIf finished.IsFaulted Then
                                                       Dim observed = finished.Exception
                                                   End If
                                               End Sub, TaskScheduler.Default)
                Throw
            End Try
            Return Await work.ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' The next line of an upstream stream. While the upstream is silent (a model still reading a long prompt, or
        ''' thinking) keepAlive is written every few seconds.
        ''' </summary>
        Private Async Function ReadLineKeepingAliveAsync(rc As RequestContext, reader As StreamReader, keepAlive As String) As Task(Of String)
            Dim pending = reader.ReadLineAsync(rc.CancellationToken).AsTask()
            Try
                While Not Await FinishesWithinAsync(pending, KeepAliveEvery, rc.CancellationToken).ConfigureAwait(False)
                    Await rc.WriteStreamAsync(keepAlive).ConfigureAwait(False)
                End While
            Catch
                ' The client went away: the read still pending fails when the upstream response is disposed.
                Dim unused = pending.ContinueWith(Sub(t)
                                                      Dim observed = t.Exception
                                                  End Sub, TaskContinuationOptions.OnlyOnFaulted)
                Throw
            End Try
            Return Await pending.ConfigureAwait(False)
        End Function

        ''' <summary>A failure while waiting for the model; one that means the client is gone is not written to it.</summary>
        Private Shared Function ClientGone(ex As Exception) As Boolean
            Return TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ObjectDisposedException
        End Function

        Private Shared Function StatusOfFailure(ex As Exception) As Integer
            Dim api = TryCast(ex, ApiException)
            If api IsNot Nothing Then Return api.StatusCode
            If TypeOf ex Is HttpRequestException Then Return 502
            If ExceptionUtil.IsCancellation(ex) Then Return 503
            Return 500
        End Function

        Private Function MessageOfFailure(ex As Exception) As String
            ' Our own refusals keep their words; a provider or connection failure is said in a few words (its text goes to the log).
            If TypeOf ex Is ApiException Then Return ex.Message
            If TypeOf ex Is HttpRequestException Then
                _h.Log.Warn("Http", "A model call failed: " & ex.Message)
                Return ModelErrors.ForUser(ex)
            End If
            If ExceptionUtil.IsCancellation(ex) Then Return "The request timed out or the host is shutting down."
            _h.Log.Warn("Http", "A model call failed after its response had started: " & ex.Message)
            Return If(_h.Options.Server.ExposeErrorDetails, ex.Message, "An internal error occurred.")
        End Function

        ''' <summary>Anthropic's error type for an HTTP status.</summary>
        Private Shared Function AnthroErrorType(status As Integer) As String
            Select Case status
                Case 400, 422 : Return "invalid_request_error"
                Case 401 : Return "authentication_error"
                Case 403 : Return "permission_error"
                Case 404 : Return "not_found_error"
                Case 413 : Return "request_too_large"
                Case 429 : Return "rate_limit_error"
                Case 503, 529 : Return "overloaded_error"
                Case Else : Return "api_error"
            End Select
        End Function

        ''' <summary>An error as an Anthropic error object (callers pass a short message, never the provider's own error).</summary>
        Private Shared Function AnthroErrorJson(status As Integer, message As String) As String
            Return New JsonObject From {{"type", "error"}, {"error", New JsonObject From {{"type", AnthroErrorType(status)}, {"message", If(message, "")}}}}.ToJsonString()
        End Function

        ''' <summary>An error after the response started: an error event in a stream, else the error as the body.</summary>
        Private Shared Async Function WriteAnthroLateErrorAsync(rc As RequestContext, stream As Boolean, status As Integer, message As String) As Task
            Dim json = AnthroErrorJson(status, message)
            Try
                Await rc.WriteStreamAsync(If(stream, "event: error" & vbLf & "data: " & json & vbLf & vbLf, json)).ConfigureAwait(False)
            Catch ex As Exception When ClientGone(ex) OrElse ExceptionUtil.IsCancellation(ex)
            End Try
        End Function

        ''' <summary>An error as an OpenAI error object (callers pass a short message, never the provider's own error).</summary>
        Friend Shared Function OaiErrorJson(status As Integer, message As String) As String
            Return JsonUtil.ToJson(New With {.error = New With {.message = If(message, ""),
                                                                .type = If(status = 429, "rate_limit_error", If(status >= 500, "api_error", "invalid_request_error")), .code = status}})
        End Function

        ''' <summary>An error after the response started: an error chunk in a stream (the OpenAI SDKs raise it), else the body.</summary>
        Private Shared Async Function WriteOaiLateErrorAsync(rc As RequestContext, stream As Boolean, status As Integer, message As String) As Task
            Dim json = OaiErrorJson(status, message)
            Try
                Await rc.WriteStreamAsync(If(stream, "data: " & json & vbLf & vbLf & "data: [DONE]" & vbLf & vbLf, json)).ConfigureAwait(False)
            Catch ex As Exception When ClientGone(ex) OrElse ExceptionUtil.IsCancellation(ex)
            End Try
        End Function

        ''' <summary>A whole OpenAI completion as one stream chunk: its message becomes the delta, tool calls are numbered.</summary>
        Friend Shared Function AsStreamChunk(completion As JsonNode) As JsonObject
            Dim choice = JsonUtil.FirstChoice(completion)
            Dim delta = If(TryCast(choice?("message")?.DeepClone(), JsonObject), New JsonObject())
            Dim calls = TryCast(delta("tool_calls"), JsonArray)
            If calls IsNot Nothing Then
                For i = 0 To calls.Count - 1
                    Dim c = TryCast(calls(i), JsonObject)
                    If c IsNot Nothing AndAlso c("index") Is Nothing Then c("index") = i
                Next
            End If
            Dim whole = TryCast(completion, JsonObject)
            Return New JsonObject From {
                {"id", whole?("id")?.DeepClone()}, {"object", "chat.completion.chunk"}, {"model", whole?("model")?.DeepClone()},
                {"choices", New JsonArray(New JsonObject From {{"index", 0}, {"delta", delta}, {"finish_reason", choice?("finish_reason")?.DeepClone()}})}}
        End Function

    End Class

End Namespace
