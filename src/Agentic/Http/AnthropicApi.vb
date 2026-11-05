Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.AI
Imports Agentic.Economy
Imports Agentic.Utilities

Namespace Http

    ' Anthropic-compatible Messages API (/v2/…, also /api/v2/…): the same models as the OpenAI API (/v1), spoken in
    ' Anthropic's Messages shape (POST /v2/messages, system/messages/tools, content blocks, x-api-key or Bearer auth). The
    ' request is translated to the OpenAI chat form, sent to the chosen model, and the reply translated back - streaming too.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterAnthropic(r As Router)
            For Each prefix In {"/v2", "/api/v2"}
                r.Map("POST", prefix & "/messages", AuthLevel.SubmitTask, AddressOf AnthroMessages)
                r.Map("GET", prefix & "/models", AuthLevel.Read, AddressOf AnthroModels)
            Next
            ' Claude Code and the Anthropic SDK always call {baseUrl}/v1/messages. /messages is unused by the OpenAI API, so
            ' serving it here lets them point straight at this server without a custom path, while /v1's OpenAI routes stay.
            r.Map("POST", "/v1/messages", AuthLevel.SubmitTask, AddressOf AnthroMessages)
            r.Map("POST", "/api/v1/messages", AuthLevel.SubmitTask, AddressOf AnthroMessages)
        End Sub

        Private Shared Function AnthroError(status As Integer, message As String, Optional type As String = "invalid_request_error") As ApiException
            Return New ApiException(status, type, message)
        End Function

        ''' <summary>GET /v2/models: the Anthropic list shape ({data:[{type:"model", id, display_name}], has_more:false}).</summary>
        Private Async Function AnthroModels(rc As RequestContext) As Task
            Dim models = Await AllModelsAsync(rc).ConfigureAwait(False)
            Dim data = models.Select(Function(m) CType(New JsonObject From {
                {"type", "model"}, {"id", m("id")?.DeepClone()}, {"display_name", If(m("name")?.DeepClone(), m("id")?.DeepClone())},
                {"created_at", Nothing}}, JsonNode)).ToArray()
            Await rc.WriteBytesAsync(200, "application/json", Encoding.UTF8.GetBytes(New JsonObject From {{"data", New JsonArray(data)}, {"has_more", False}}.ToJsonString())).ConfigureAwait(False)
        End Function

        ''' <summary>The ClaudeModelAliases key for the model that answers Claude Code's auto-mode safety classifier.</summary>
        Friend Const ClassifierAlias As String = "auto-mode-classifier"

        ''' <summary>
        ''' The models here that can answer an Anthropic id a client asked for, best first: Claude Code's claude-sonnet-5 for auto
        ''' mode's safety classifier, claude-opus-5-5 for "opus", also dated or [1m] forms. ClaudeModelAliases first (a value may
        ''' list several, comma separated), then the providers' copies ("prefix/claude-sonnet-5": priced ones first, cheapest
        ''' first), then the "*" alias. The first answers; the others take over when a provider refuses (a broken copy). Empty
        ''' when the id is served as it is, is not a Claude id, or has no match (the request then fails as before).
        ''' ClassifierAlias asks for the classifier's models (its alias, else claude-sonnet-5's).
        ''' </summary>
        Private Async Function ResolveClaudeModelAsync(rc As RequestContext, id As String) As Task(Of List(Of String))
            Dim result As New List(Of String)
            Dim classifier = String.Equals(id, ClassifierAlias, StringComparison.OrdinalIgnoreCase)
            If Not classifier AndAlso (String.IsNullOrWhiteSpace(id) OrElse Not id.StartsWith("claude-", StringComparison.OrdinalIgnoreCase)) Then Return result
            Dim ids As List(Of String)
            Try
                ids = (Await AllModelsAsync(rc).ConfigureAwait(False)).Select(Function(m) m("id")?.ToString()).Where(Function(x) Not String.IsNullOrEmpty(x)).ToList()
            Catch ex As ApiException
                Return result
            End Try
            If Not classifier AndAlso ids.Contains(id, StringComparer.OrdinalIgnoreCase) Then Return result
            Dim bare = If(classifier, "claude-sonnet-5", BareClaudeId(id))
            Dim aliases = _h.Options.ClaudeModelAliases
            Dim add = Sub(list As String)
                          For Each part In If(list, "").Split(","c)
                              Dim known = ids.FirstOrDefault(Function(x) String.Equals(x, part.Trim(), StringComparison.OrdinalIgnoreCase))
                              If known IsNot Nothing AndAlso Not result.Contains(known, StringComparer.OrdinalIgnoreCase) Then result.Add(known)
                          Next
                      End Sub
            Dim mapped As String = Nothing
            If aliases IsNot Nothing Then
                If aliases.TryGetValue(id, mapped) Then add(mapped)
                If aliases.TryGetValue(bare, mapped) Then add(mapped)
            End If
            For Each copy In ids.Where(Function(x) BareClaudeId(x.Substring(x.LastIndexOf("/"c) + 1)) = bare).OrderBy(Function(x) SquareCost(x))
                add(copy)
            Next
            If aliases IsNot Nothing AndAlso aliases.TryGetValue("*", mapped) Then add(mapped)
            Return result
        End Function

        ''' <summary>
        ''' Claude Code's auto-mode safety classifier: its system prompt asks for a &lt;block&gt;yes/no&lt;/block&gt; verdict. When
        ''' Sonnet 5 is not reachable Claude Code sends it to the session's model instead, and a reasoning model spends the small
        ''' token budget (64) thinking and gives no verdict, so every checked action is denied.
        ''' </summary>
        Friend Shared Function IsAutoModeClassifierRequest(req As JsonNode) As Boolean
            Dim sys = AnthroTextOf(req?("system"))
            Return sys IsNot Nothing AndAlso sys.Contains("<block>", StringComparison.Ordinal) AndAlso
                   (sys.Contains("Err on the side of blocking", StringComparison.OrdinalIgnoreCase) OrElse sys.Contains("<block>yes</block>", StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>A Claude model id (claude-…) or a provider's copy of one (prefix/claude-…).</summary>
        Friend Shared Function IsClaudeModel(model As String) As Boolean
            Return model IsNot Nothing AndAlso (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase) OrElse model.IndexOf("/claude-", StringComparison.OrdinalIgnoreCase) >= 0)
        End Function

        ''' <summary>A Claude model id without what differs between providers: a [1m] or @version suffix, a -YYYYMMDD date, -latest.</summary>
        Friend Shared Function BareClaudeId(id As String) As String
            Dim s = Text.RegularExpressions.Regex.Replace(If(id, "").Trim().ToLowerInvariant(), "\[[^\]]*\]$", "")
            Dim at = s.IndexOf("@"c)
            If at > 0 Then s = s.Substring(0, at)
            Return Text.RegularExpressions.Regex.Replace(s, "-(\d{8}|latest)$", "")
        End Function

        ''' <summary>A model's Model Square price (what API calls are charged), input + output per million; unpriced ones last.</summary>
        Private Function SquareCost(model As String) As Decimal
            Dim p = _h.Accounts.PriceOf(model)
            Return If(p Is Nothing, Decimal.MaxValue, p.InputPerMillion + p.OutputPerMillion)
        End Function

        Private Async Function AnthroMessages(rc As RequestContext) As Task
            Dim started = Diagnostics.Stopwatch.StartNew()
            Dim req = Await ReadModelRequestAsync(rc).ConfigureAwait(False)
            Dim requested = req("model").ToString()
            Dim stream = req("stream") IsNot Nothing AndAlso req("stream").GetValueKind() = JsonValueKind.True
            If TryCast(req("messages"), JsonArray) Is Nothing Then Throw AnthroError(400, "'messages' is required and must be an array.")

            Dim body As JsonObject
            Try
                body = AnthropicToOpenAi(DirectCast(req, JsonObject))
            Catch ex As ArgumentException
                Throw AnthroError(400, ex.Message)
            End Try

            ' Claude Code asks for Anthropic's own ids (auto mode's safety classifier: claude-sonnet-5); a provider here has them
            ' under its prefix. The answer still names the model that was asked for.
            Dim asked = requested
            ' Its safety classifier sent to a non-Claude session model (Sonnet 5 was unreachable) goes to the classifier model.
            Dim lookup = If(Not IsClaudeModel(requested) AndAlso IsAutoModeClassifierRequest(req), ClassifierAlias, requested)
            Dim candidates = Await ResolveClaudeModelAsync(rc, lookup).ConfigureAwait(False)
            If candidates.Count > 0 Then
                requested = candidates(0)
                body("model") = requested
            End If

            Dim custom = Await _h.ModelHub.GetCustomAsync(requested).ConfigureAwait(False)
            If _h.ModelHub.IsDisabled(requested) Then Throw AnthroError(404, "The model '" & requested & "' is turned off by an administrator.", "not_found_error")
            If custom Is Nothing AndAlso _h.ModelHub.IsHidden(requested) Then Throw AnthroError(404, $"The model '{requested}' does not exist.", "not_found_error")
            If custom IsNot Nothing AndAlso Not custom.Enabled Then Throw AnthroError(404, $"The custom model '{requested}' is disabled.", "not_found_error")
            If custom Is Nothing AndAlso ImageRouter.IsImageModel(requested) Then Throw AnthroError(400, $"'{requested}' is an image model.")
            Await RequireModelUseAsync(rc, requested).ConfigureAwait(False)

            Using cancelWork = Threading.CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken)
                ' Routing, a free local slot and the provider's first byte can take minutes; past the grace period the response
                ' starts and is kept alive so Cloudflare does not cut it off (HTTP 524). See ModelKeepAlive.vb.
                Dim work = OpenUpstreamAsync(custom, requested, body, Not stream, cancelWork.Token, candidates.Skip(1).ToList())
                Dim committed = False
                Dim u As UpstreamCall = Nothing
                Dim failure As Exception = Nothing
                Try
                    If Await FinishesWithinAsync(work, KeepAliveAfter, rc.CancellationToken).ConfigureAwait(False) Then
                        u = Await work.ConfigureAwait(False)
                    Else
                        rc.BeginStream(If(stream, "text/event-stream; charset=utf-8", "application/json; charset=utf-8"))
                        committed = True
                        u = Await KeepAliveUntilDoneAsync(rc, work, If(stream, AnthroPing, JsonKeepAlive), cancelWork).ConfigureAwait(False)
                    End If
                Catch ex As Exception When committed AndAlso Not ClientGone(ex)
                    failure = ex
                End Try
                If failure IsNot Nothing Then
                    Await WriteAnthroLateErrorAsync(rc, stream, StatusOfFailure(failure), MessageOfFailure(failure)).ConfigureAwait(False)
                    Return
                End If
                Using u
                    Dim resp = u.Response
                    Dim usage = (Prompt:=0L, Completion:=0L, Cached:=0L)
                    If Not resp.IsSuccessStatusCode Then
                        Dim errText = If(u.Text, Await resp.Content.ReadAsStringAsync(rc.CancellationToken).ConfigureAwait(False))
                        Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", "messages", asked, u.Model, u.Decision?.Category, stream, CInt(resp.StatusCode), 0, 0, started.ElapsedMilliseconds, errText).ConfigureAwait(False)
                        ' The caller gets the kind of failure in a few words, not the provider's own error (it stays in the usage log).
                        Dim shortError = ModelErrors.ForUser(errText, CInt(resp.StatusCode))
                        If committed Then
                            Await WriteAnthroLateErrorAsync(rc, stream, CInt(resp.StatusCode), shortError).ConfigureAwait(False)
                        Else
                            Await rc.WriteBytesAsync(CInt(resp.StatusCode), "application/json", Encoding.UTF8.GetBytes(AnthroErrorJson(CInt(resp.StatusCode), shortError))).ConfigureAwait(False)
                        End If
                        Return
                    End If
                    Dim mediaType = If(resp.Content.Headers.ContentType?.MediaType, "")
                    If Not committed Then rc.Http.Response.Headers("X-Agentic-Model") = u.Model
                    Dim msgId = Ids.NewId("msg")
                    Dim firstOutputMs As Long = -1
                    If stream Then
                        If Not committed Then
                            rc.BeginStream("text/event-stream; charset=utf-8")
                            committed = True
                        End If
                        Dim st As New AnthroStreamState(msgId, asked)
                        Await WriteAnthroEventAsync(rc, "message_start", st.MessageStart()).ConfigureAwait(False)
                        If mediaType.Contains("event-stream") Then
                            Using s = Await resp.Content.ReadAsStreamAsync(rc.CancellationToken).ConfigureAwait(False), reader As New IO.StreamReader(s, Encoding.UTF8)
                                ' Time since the client last got anything: chunks that make no event (the model's reasoning) are silence too.
                                Dim quiet = Diagnostics.Stopwatch.StartNew()
                                While True
                                    Dim line = Await ReadLineKeepingAliveAsync(rc, reader, AnthroPing).ConfigureAwait(False)
                                    If line Is Nothing Then Exit While
                                    If quiet.Elapsed >= KeepAliveEvery Then
                                        Await rc.WriteStreamAsync(AnthroPing).ConfigureAwait(False)
                                        quiet.Restart()
                                    End If
                                    If Not line.StartsWith("data:", StringComparison.Ordinal) Then Continue While
                                    Dim payload = line.Substring(5).Trim()
                                    If payload = "[DONE]" Then Exit While
                                    Dim chunk = TryParse(payload)
                                    If chunk Is Nothing Then Continue While
                                    If firstOutputMs < 0 AndAlso Global.Agentic.AI.ModelHealth.ChunkHasOutput(chunk) Then firstOutputMs = u.Sent.ElapsedMilliseconds
                                    ReadUsage(chunk, usage)
                                    For Each ev In st.OnChunk(chunk)
                                        Await WriteAnthroEventAsync(rc, ev.Type, ev.Data).ConfigureAwait(False)
                                        quiet.Restart()
                                    Next
                                End While
                            End Using
                        Else
                            ' The provider answered in one piece although a stream was asked for: replay the answer as events.
                            Dim json = TryParse(Await resp.Content.ReadAsStringAsync(rc.CancellationToken).ConfigureAwait(False))
                            If json IsNot Nothing Then
                                ReadUsage(json, usage)
                                For Each ev In st.OnChunk(AsStreamChunk(json))
                                    Await WriteAnthroEventAsync(rc, ev.Type, ev.Data).ConfigureAwait(False)
                                Next
                            End If
                        End If
                        If usage.Completion = 0 AndAlso st.TextChars > 0 Then usage.Completion = Math.Max(1L, st.TextChars \ 4)
                        If usage.Prompt = 0 Then usage.Prompt = Math.Max(1L, CLng(body.ToJsonString().Length \ 4))
                        For Each ev In st.Finish(usage.Prompt, usage.Completion)
                            Await WriteAnthroEventAsync(rc, ev.Type, ev.Data).ConfigureAwait(False)
                        Next
                    Else
                        Dim json = TryParse(u.Text)
                        If json IsNot Nothing Then ReadUsage(json, usage)
                        If usage.Prompt = 0 Then usage.Prompt = Math.Max(1L, CLng(body.ToJsonString().Length \ 4))
                        Dim outJson = OpenAiToAnthropic(json, msgId, asked, usage.Prompt, usage.Completion).ToJsonString()
                        If committed Then
                            Await rc.WriteStreamAsync(outJson).ConfigureAwait(False)
                        Else
                            Await rc.WriteBytesAsync(200, "application/json", Encoding.UTF8.GetBytes(outJson)).ConfigureAwait(False)
                        End If
                    End If
                    Await _h.ModelHub.LogUsageAsync(rc.User.Name, "api", "messages", asked, u.Model, u.Decision?.Category, stream, 200, usage.Prompt, usage.Completion, started.ElapsedMilliseconds, Nothing).ConfigureAwait(False)
                    RecordApiSpeed(u, If(custom IsNot Nothing, requested, Nothing), firstOutputMs, usage.Completion)
                    Await ChargeApiAsync(rc, "messages", requested, u.Model, usage.Prompt, usage.Completion, usage.Cached).ConfigureAwait(False)
                End Using
            End Using
        End Function

        Private Shared Function WriteAnthroEventAsync(rc As RequestContext, type As String, data As JsonObject) As Task
            Return rc.WriteStreamAsync($"event: {type}" & vbLf & "data: " & data.ToJsonString() & vbLf & vbLf)
        End Function

        ' ------------------------------------------------------------------ request: Anthropic -> OpenAI

        ''' <summary>Turns an Anthropic Messages request into an OpenAI chat request.</summary>
        Friend Shared Function AnthropicToOpenAi(req As JsonObject) As JsonObject
            Dim out As New JsonObject From {{"model", req("model")?.DeepClone()}}
            If req("stream")?.GetValueKind() = JsonValueKind.True Then out("stream") = True
            ' max_tokens is required by Anthropic; carry it, and the sampling parameters.
            If req("max_tokens") IsNot Nothing Then out("max_tokens") = req("max_tokens").DeepClone()
            If req("temperature") IsNot Nothing Then out("temperature") = req("temperature").DeepClone()
            If req("top_p") IsNot Nothing Then out("top_p") = req("top_p").DeepClone()
            Dim stops = req("stop_sequences")
            If TypeOf stops Is JsonArray Then out("stop") = stops.DeepClone()

            Dim messages As New JsonArray()
            ' system: a string or an array of text blocks.
            Dim sys = AnthroTextOf(req("system"))
            If Not String.IsNullOrEmpty(sys) Then messages.Add(New JsonObject From {{"role", "system"}, {"content", sys}})

            For Each m In TryCast(req("messages"), JsonArray)
                Dim mo = TryCast(m, JsonObject)
                If mo Is Nothing Then Continue For
                Dim role = If(mo("role")?.ToString(), "user")
                Dim content = mo("content")
                If TypeOf content IsNot JsonArray Then
                    ' Plain string content.
                    messages.Add(New JsonObject From {{"role", role}, {"content", If(content?.ToString(), "")}})
                    Continue For
                End If
                Dim blocks = DirectCast(content, JsonArray)
                If role = "assistant" Then
                    ' Text and tool_use blocks become one assistant message (content + tool_calls).
                    Dim textParts As New List(Of String)
                    Dim toolCalls As New JsonArray()
                    For Each b In blocks
                        Dim bo = TryCast(b, JsonObject)
                        Dim t = bo?("type")?.ToString()
                        If t = "text" Then
                            textParts.Add(If(bo("text")?.ToString(), ""))
                        ElseIf t = "tool_use" Then
                            toolCalls.Add(New JsonObject From {{"id", If(bo("id")?.DeepClone(), Ids.NewId("call"))}, {"type", "function"},
                                {"function", New JsonObject From {{"name", bo("name")?.ToString()}, {"arguments", If(bo("input") Is Nothing, "{}", bo("input").ToJsonString())}}}})
                        End If
                    Next
                    Dim am As New JsonObject From {{"role", "assistant"}, {"content", If(textParts.Count > 0, String.Join(vbLf, textParts), Nothing)}}
                    If toolCalls.Count > 0 Then am("tool_calls") = toolCalls
                    messages.Add(am)
                Else
                    ' user: tool_result blocks become OpenAI tool messages; everything else becomes the user content (text + images).
                    Dim userContent As New JsonArray()
                    For Each b In blocks
                        Dim bo = TryCast(b, JsonObject)
                        Dim t = bo?("type")?.ToString()
                        If t = "tool_result" Then
                            messages.Add(New JsonObject From {{"role", "tool"}, {"tool_call_id", bo("tool_use_id")?.ToString()}, {"content", AnthroTextOf(bo("content"))}})
                        ElseIf t = "text" Then
                            userContent.Add(New JsonObject From {{"type", "text"}, {"text", If(bo("text")?.ToString(), "")}})
                        ElseIf t = "image" Then
                            Dim src = TryCast(bo("source"), JsonObject)
                            If src IsNot Nothing Then
                                Dim u As String
                                If src("type")?.ToString() = "url" Then
                                    u = src("url")?.ToString()
                                Else
                                    u = $"data:{src("media_type")?.ToString()};base64,{src("data")?.ToString()}"
                                End If
                                userContent.Add(New JsonObject From {{"type", "image_url"}, {"image_url", New JsonObject From {{"url", u}}}})
                            End If
                        End If
                    Next
                    If userContent.Count = 1 AndAlso userContent(0)("type")?.ToString() = "text" Then
                        messages.Add(New JsonObject From {{"role", "user"}, {"content", userContent(0)("text")?.DeepClone()}})
                    ElseIf userContent.Count > 0 Then
                        messages.Add(New JsonObject From {{"role", "user"}, {"content", userContent}})
                    End If
                End If
            Next
            out("messages") = messages

            ' tools: {name, description, input_schema} -> OpenAI function tools.
            Dim tools = TryCast(req("tools"), JsonArray)
            If tools IsNot Nothing AndAlso tools.Count > 0 Then
                Dim oaiTools As New JsonArray()
                For Each tl In tools
                    Dim tlo = TryCast(tl, JsonObject)
                    If tlo Is Nothing OrElse String.IsNullOrEmpty(tlo("name")?.ToString()) Then Continue For
                    oaiTools.Add(New JsonObject From {{"type", "function"}, {"function", New JsonObject From {
                        {"name", tlo("name")?.DeepClone()}, {"description", If(tlo("description")?.DeepClone(), "")},
                        {"parameters", If(tlo("input_schema")?.DeepClone(), New JsonObject From {{"type", "object"}, {"properties", New JsonObject()}})}}}})
                Next
                If oaiTools.Count > 0 Then out("tools") = oaiTools
            End If
            Dim tc = TryCast(req("tool_choice"), JsonObject)
            If tc IsNot Nothing Then
                Select Case tc("type")?.ToString()
                    Case "auto" : out("tool_choice") = "auto"
                    Case "any" : out("tool_choice") = "required"
                    Case "none" : out("tool_choice") = "none"
                    Case "tool" : out("tool_choice") = New JsonObject From {{"type", "function"}, {"function", New JsonObject From {{"name", tc("name")?.DeepClone()}}}}
                End Select
            End If
            Return out
        End Function

        ''' <summary>Flattens Anthropic content (a string, or an array of text/tool_result blocks) to plain text.</summary>
        Private Shared Function AnthroTextOf(node As JsonNode) As String
            If node Is Nothing Then Return ""
            If TypeOf node IsNot JsonArray Then Return node.ToString()
            Dim sb As New StringBuilder()
            For Each b In DirectCast(node, JsonArray)
                Dim bo = TryCast(b, JsonObject)
                If bo Is Nothing Then
                    If b IsNot Nothing Then sb.Append(b.ToString())
                    Continue For
                End If
                Dim t = bo("type")?.ToString()
                If t = "text" OrElse t Is Nothing Then
                    sb.Append(If(bo("text")?.ToString(), ""))
                ElseIf t = "tool_result" Then
                    sb.Append(AnthroTextOf(bo("content")))
                End If
                sb.Append(vbLf)
            Next
            Return sb.ToString().Trim()
        End Function

        ' ------------------------------------------------------------------ response: OpenAI -> Anthropic

        Private Shared ReadOnly StopReasons As New Dictionary(Of String, String) From {
            {"stop", "end_turn"}, {"length", "max_tokens"}, {"tool_calls", "tool_use"}, {"function_call", "tool_use"}, {"content_filter", "end_turn"}}

        Private Shared Function MapStop(finish As String) As String
            Dim s As String = Nothing
            Return If(finish IsNot Nothing AndAlso StopReasons.TryGetValue(finish, s), s, "end_turn")
        End Function

        ''' <summary>Turns an OpenAI chat completion into an Anthropic message.</summary>
        Friend Shared Function OpenAiToAnthropic(json As JsonNode, msgId As String, model As String, inputTokens As Long, outputTokens As Long) As JsonObject
            Dim choice = JsonUtil.FirstChoice(json)
            Dim msg = choice?("message")
            Dim content As New JsonArray()
            Dim text = msg?("content")?.ToString()
            If Not String.IsNullOrEmpty(text) Then content.Add(New JsonObject From {{"type", "text"}, {"text", text}})
            Dim calls = TryCast(msg?("tool_calls"), JsonArray)
            If calls IsNot Nothing Then
                For Each c In calls
                    Dim fn = c?("function")
                    If fn Is Nothing Then Continue For
                    content.Add(New JsonObject From {{"type", "tool_use"}, {"id", If(c("id")?.DeepClone(), Ids.NewId("toolu"))},
                        {"name", fn("name")?.DeepClone()}, {"input", ParseArgs(fn("arguments")?.ToString())}})
                Next
            End If
            Dim finish = choice?("finish_reason")?.ToString()
            Return New JsonObject From {
                {"id", msgId}, {"type", "message"}, {"role", "assistant"}, {"model", model},
                {"content", content}, {"stop_reason", MapStop(finish)}, {"stop_sequence", Nothing},
                {"usage", New JsonObject From {{"input_tokens", inputTokens}, {"output_tokens", outputTokens}}}}
        End Function

        Private Shared Function ParseArgs(argsJson As String) As JsonNode
            If String.IsNullOrWhiteSpace(argsJson) Then Return New JsonObject()
            Try
                Return JsonNode.Parse(JsonUtil.CollapseDuplicateKeys(argsJson))
            Catch ex As JsonException
                Return New JsonObject()
            End Try
        End Function

        ' ------------------------------------------------------------------ streaming translator

        Friend Structure AnthroEvent
            Public Type As String
            Public Data As JsonObject
            Public Sub New(t As String, d As JsonObject)
                Type = t : Data = d
            End Sub
        End Structure

        ''' <summary>
        ''' Turns the OpenAI streamed chunks of one answer into the Anthropic event sequence: message_start, then a text
        ''' content block (content_block_start / text_delta / stop) and a tool_use block per tool call (input_json_delta),
        ''' then message_delta (stop_reason, usage) and message_stop.
        ''' </summary>
        Friend NotInheritable Class AnthroStreamState
            Private ReadOnly _id As String
            Private ReadOnly _model As String
            Private _nextIndex As Integer
            Private _textOpen As Boolean
            Private _textIndex As Integer = -1
            Private _finish As String
            ' OpenAI tool-call index -> Anthropic content block index (and whether it is open).
            Private ReadOnly _toolBlocks As New Dictionary(Of Integer, Integer)
            Private _openToolIndex As Integer = -1
            Public Property TextChars As Long

            Public Sub New(id As String, model As String)
                _id = id : _model = model
            End Sub

            Public Function MessageStart() As JsonObject
                Return New JsonObject From {{"type", "message_start"}, {"message", New JsonObject From {
                    {"id", _id}, {"type", "message"}, {"role", "assistant"}, {"model", _model}, {"content", New JsonArray()},
                    {"stop_reason", Nothing}, {"stop_sequence", Nothing}, {"usage", New JsonObject From {{"input_tokens", 0}, {"output_tokens", 0}}}}}}
            End Function

            Public Iterator Function OnChunk(chunk As JsonNode) As IEnumerable(Of AnthroEvent)
                Dim choice = JsonUtil.FirstChoice(chunk)
                If choice Is Nothing Then Return
                Dim fin = choice("finish_reason")?.ToString()
                If Not String.IsNullOrEmpty(fin) Then _finish = fin
                Dim delta = choice("delta")
                If delta Is Nothing Then Return
                Dim text = delta("content")?.ToString()
                If Not String.IsNullOrEmpty(text) Then
                    ' A tool block cannot be open at the same time as text.
                    If _openToolIndex >= 0 Then
                        Yield New AnthroEvent("content_block_stop", New JsonObject From {{"type", "content_block_stop"}, {"index", _openToolIndex}})
                        _openToolIndex = -1
                    End If
                    If Not _textOpen Then
                        _textIndex = _nextIndex : _nextIndex += 1 : _textOpen = True
                        Yield New AnthroEvent("content_block_start", New JsonObject From {{"type", "content_block_start"}, {"index", _textIndex},
                            {"content_block", New JsonObject From {{"type", "text"}, {"text", ""}}}})
                    End If
                    TextChars += text.Length
                    Yield New AnthroEvent("content_block_delta", New JsonObject From {{"type", "content_block_delta"}, {"index", _textIndex},
                        {"delta", New JsonObject From {{"type", "text_delta"}, {"text", text}}}})
                End If
                Dim tcs = TryCast(delta("tool_calls"), JsonArray)
                If tcs IsNot Nothing Then
                    For Each tc In tcs
                        Dim to1 = TryCast(tc, JsonObject)
                        If to1 Is Nothing Then Continue For
                        Dim oaiIdx As Integer
                        If Not Integer.TryParse(to1("index")?.ToString(), oaiIdx) Then oaiIdx = _toolBlocks.Count
                        Dim fn = to1("function")
                        Dim idx As Integer
                        If Not _toolBlocks.TryGetValue(oaiIdx, idx) Then
                            ' A new tool call: close text/other tool, open a tool_use block.
                            If _textOpen Then
                                Yield New AnthroEvent("content_block_stop", New JsonObject From {{"type", "content_block_stop"}, {"index", _textIndex}})
                                _textOpen = False
                            End If
                            If _openToolIndex >= 0 Then
                                Yield New AnthroEvent("content_block_stop", New JsonObject From {{"type", "content_block_stop"}, {"index", _openToolIndex}})
                            End If
                            idx = _nextIndex : _nextIndex += 1
                            _toolBlocks(oaiIdx) = idx
                            _openToolIndex = idx
                            Yield New AnthroEvent("content_block_start", New JsonObject From {{"type", "content_block_start"}, {"index", idx},
                                {"content_block", New JsonObject From {{"type", "tool_use"}, {"id", If(to1("id")?.DeepClone(), Ids.NewId("toolu"))},
                                                                       {"name", If(fn?("name")?.DeepClone(), "")}, {"input", New JsonObject()}}}})
                        End If
                        Dim args = fn?("arguments")?.ToString()
                        If Not String.IsNullOrEmpty(args) Then
                            Yield New AnthroEvent("content_block_delta", New JsonObject From {{"type", "content_block_delta"}, {"index", idx},
                                {"delta", New JsonObject From {{"type", "input_json_delta"}, {"partial_json", args}}}})
                        End If
                    Next
                End If
            End Function

            Public Iterator Function Finish(inputTokens As Long, outputTokens As Long) As IEnumerable(Of AnthroEvent)
                If _textOpen Then
                    Yield New AnthroEvent("content_block_stop", New JsonObject From {{"type", "content_block_stop"}, {"index", _textIndex}})
                    _textOpen = False
                End If
                If _openToolIndex >= 0 Then
                    Yield New AnthroEvent("content_block_stop", New JsonObject From {{"type", "content_block_stop"}, {"index", _openToolIndex}})
                    _openToolIndex = -1
                End If
                Yield New AnthroEvent("message_delta", New JsonObject From {
                    {"type", "message_delta"},
                    {"delta", New JsonObject From {{"stop_reason", MapStop(_finish)}, {"stop_sequence", Nothing}}},
                    {"usage", New JsonObject From {{"input_tokens", inputTokens}, {"output_tokens", outputTokens}}}})
                Yield New AnthroEvent("message_stop", New JsonObject From {{"type", "message_stop"}})
            End Function
        End Class

    End Class

End Namespace
