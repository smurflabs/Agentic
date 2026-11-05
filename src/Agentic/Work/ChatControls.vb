Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ' Chat controls: reasoning effort, permission mode (tool approvals), and the context window (usage, compaction).
    Partial Public NotInheritable Class ChatService

        ''' <summary>Live state shared by all of a user's windows (web app, desktop app): runs, approvals, desktop apps.</summary>
        Public ReadOnly Property Live As New ChatLiveHub()
        Private ReadOnly _noReasoningParam As New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Models (or their proxy) that refused the thinking-off switches (chat_template_kwargs, thinking).</summary>
        Private ReadOnly _noThinkingParam As New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>
        ''' Thinking off, in the forms the common servers understand: chat_template_kwargs.enable_thinking (llama.cpp, vLLM,
        ''' SGLang: Qwen, GLM, DeepSeek templates) and thinking.type "disabled" (GLM's and Anthropic-style APIs).
        ''' </summary>
        Friend Shared Sub ApplyThinkingOff(body As JsonObject)
            body("chat_template_kwargs") = New JsonObject From {{"enable_thinking", False}, {"thinking", False}}
            body("thinking") = New JsonObject From {{"type", "disabled"}}
        End Sub

        Friend Shared Sub RemoveThinkingOff(body As JsonObject)
            body.Remove("chat_template_kwargs")
            body.Remove("thinking")
        End Sub

        ''' <summary>A 400 caused by the thinking switches (an unknown or unsupported parameter).</summary>
        Friend Shared Function IsThinkingParamError(body As String) As Boolean
            Dim b = If(body, "").ToLowerInvariant()
            Return (b.Contains("chat_template_kwargs") OrElse b.Contains("thinking") OrElse b.Contains("enable_thinking")) AndAlso
                   (b.Contains("unsupported") OrElse b.Contains("not support") OrElse b.Contains("unknown") OrElse b.Contains("unrecognized") OrElse
                    b.Contains("invalid") OrElse b.Contains("extra") OrElse b.Contains("unexpected") OrElse b.Contains("not allowed") OrElse b.Contains("not permitted"))
        End Function
        ''' <summary>Models that refused frequency_penalty / presence_penalty (sent only while stopping a repetition loop).</summary>
        Private ReadOnly _noPenaltyParam As New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)

        ' ------------------------------------------------------------------ runaway answers

        ''' <summary>How often an answer that fell into repeating itself is cut back and continued before it is ended.</summary>
        Friend Const MaxRepetitionRetries As Integer = 2
        ''' <summary>
        ''' The same tool call (same tool, same arguments) that keeps giving the same result: stopped after this many in a row
        ''' even when spaced out (polling something that never changes). A call whose result changes is never stopped (running
        ''' the same build or test again after each fix is how work gets done).
        ''' </summary>
        Friend Const MaxSameToolCalls As Integer = 10
        ''' <summary>Identical calls with identical results closer together than this are a rut, not polling: stopped after 3.</summary>
        Friend Const PollGapSeconds As Integer = 20
        ''' <summary>Tool calls failing one after another (different arguments each time, a "let me try… let me try…" loop): after
        ''' this many in a row the model is told to stop guessing and change approach or ask; after FailStreakStop, tools are off.</summary>
        Friend Const FailStreakNudge As Integer = 5
        Friend Const FailStreakStop As Integer = 9

        ''' <summary>
        ''' A model stuck repeating itself ("It was the Golden Spiral." hundreds of times): where the repetition at the end of
        ''' s (from index from on) begins, so it can be cut back to one copy. Two checks: the end of the text is one piece of
        ''' text over and over (an exact period: at least 5 copies and 400 characters; 3 copies of a long piece; a very short
        ''' piece needs a letter and 1,200 characters, so "0, 0, 0" in data is not mistaken for it), or the same line (12+
        ''' characters with letters, outside code blocks) makes up 8 of the last 40 lines. Returns -1 when the text is fine,
        ''' else the length to keep: everything before the loop plus its first copy.
        ''' </summary>
        Friend Shared Function FindRepetition(s As String, from As Integer, Optional inCodeAtStart As Boolean = False) As Integer
            Dim n = s.Length
            If n - from < 300 Then Return -1
            Dim floor = Math.Max(from, n - 12000)
            ' 1. An exact period at the end.
            For p = 1 To Math.Min(800, (n - floor) \ 3)
                Dim i = n - 1
                While i - p >= floor AndAlso s(i) = s(i - p)
                    i -= 1
                End While
                Dim region = (n - 1 - i) + p          ' characters covered by the repeating piece
                If region > n - floor Then region = n - floor
                If region < If(p < 8, 1200, 400) Then Continue For
                Dim reps = region \ p
                ' Inside a code block an exact run is often real data (0x00, 0x00, … or padded rows): only a much longer run
                ' counts there (a model stuck in a loop keeps going far past it).
                If InCodeAt(s, n - region, inCodeAtStart) AndAlso region < 4000 Then Continue For
                Dim unit = s.Substring(n - p, p)
                Dim hasLetter = unit.Any(AddressOf Char.IsLetter)
                Dim enough = If(p < 8, hasLetter AndAlso region >= 1200, region >= 400 AndAlso reps >= If(p < 80, 5, 3))
                If enough AndAlso unit.Trim().Length > 0 Then
                    ' The run may begin mid-copy (text before it can end like a copy does): keep one whole copy that starts
                    ' at a line start in the first copy, else at a sentence start, else where the run starts.
                    Dim start = n - region
                    Dim k = start
                    For j = start To Math.Min(n - 1, start + p - 1)
                        If j > 0 AndAlso s(j - 1) = ControlChars.Lf AndAlso s(j) <> ControlChars.Lf AndAlso s(j) <> ControlChars.Cr Then k = j : Exit For
                    Next
                    If k = start Then
                        For j = start To Math.Min(n - 1, start + p - 1)
                            If j >= 2 AndAlso ".!?".Contains(s(j - 2)) AndAlso s(j - 1) = " "c Then k = j : Exit For
                        Next
                    End If
                    Return Math.Min(n, k + p)
                End If
            Next
            ' 2. The same line again and again (copies that differ in spacing or are separated by other lines).
            Dim lines As New List(Of (Start As Integer, Key As String))
            Dim inCode = False
            Dim pos = from
            For Each raw In s.Substring(from).Split(ControlChars.Lf)
                Dim t = raw.Trim()
                If t.StartsWith("```", StringComparison.Ordinal) Then
                    inCode = Not inCode
                ElseIf Not inCode AndAlso t.Length > 0 Then
                    Dim key = Text.RegularExpressions.Regex.Replace(t.ToLowerInvariant().Trim("*"c, "_"c, "#"c, ">"c, "-"c, " "c), "\s+", " ")
                    lines.Add((pos, key))
                End If
                pos += raw.Length + 1
            Next
            Dim tail = lines.Skip(Math.Max(0, lines.Count - 40)).ToList()
            For Each g In tail.Where(Function(l) l.Key.Length >= 12 AndAlso l.Key.Any(AddressOf Char.IsLetter)).GroupBy(Function(l) l.Key)
                If g.Count() >= 8 Then
                    Dim firstLine = lines.First(Function(l) l.Key = g.Key)
                    Dim second = lines.Where(Function(l) l.Key = g.Key).Skip(1).First()
                    Return Math.Max(firstLine.Start + 1, second.Start)
                End If
            Next
            Return -1
        End Function

        ''' <summary>FindRepetition over the end of a growing answer (from = where this round began): the length to keep, or -1.</summary>
        Friend Shared Function RepetitionKeep(sb As StringBuilder, from As Integer) As Integer
            Dim start = Math.Max(from, sb.Length - 12000)
            If sb.Length - start < 300 Then Return -1
            Dim k = FindRepetition(sb.ToString(start, sb.Length - start), 0, InCodeAt(sb.ToString(0, start), start, False))
            Return If(k < 0, -1, start + k)
        End Function

        ''' <summary>Whether position pos of s is inside a fenced code block (an odd number of fence lines before it).</summary>
        Friend Shared Function InCodeAt(s As String, pos As Integer, inCodeAtStart As Boolean) As Boolean
            Dim inside = inCodeAtStart
            Dim i = 0
            While True
                i = s.IndexOf("```", i, StringComparison.Ordinal)
                If i < 0 OrElse i >= pos Then Exit While
                inside = Not inside
                i += 3
            End While
            Return inside
        End Function

        ' ------------------------------------------------------------------ tool schemas
        ''' <summary>
        ''' Tool parameter schemas as providers take them. Always: keys no provider needs ($schema, $id, $comment) go, and every
        ''' schema is an object with properties. Strict (after a provider refused the tools): only the plain JSON Schema subset
        ''' every provider accepts stays (type, properties, required, items, enum, description, anyOf, nullable).
        ''' </summary>
        Friend Shared Sub CleanToolSchemas(tools As JsonArray, strict As Boolean)
            If tools Is Nothing Then Return
            For Each t In tools
                Dim fn = TryCast(t?("function"), JsonObject)
                If fn Is Nothing Then Continue For
                Dim p = TryCast(fn("parameters"), JsonObject)
                If p Is Nothing Then
                    fn("parameters") = New JsonObject From {{"type", "object"}, {"properties", New JsonObject()}}
                    Continue For
                End If
                CleanSchema(p, strict)
                If p("type") Is Nothing Then p("type") = "object"
                If p("type")?.ToString() = "object" AndAlso p("properties") Is Nothing Then p("properties") = New JsonObject()
            Next
        End Sub

        Private Shared ReadOnly StrictSchemaKeys As New HashSet(Of String)(StringComparer.Ordinal) From {"type", "properties", "required", "items", "enum", "description", "anyOf", "nullable"}

        Private Shared Sub CleanSchema(node As JsonNode, strict As Boolean)
            Dim o = TryCast(node, JsonObject)
            If o Is Nothing Then Return
            For Each key In o.Select(Function(kv) kv.Key).ToList()
                If key = "$schema" OrElse key = "$id" OrElse key = "$comment" OrElse (strict AndAlso Not StrictSchemaKeys.Contains(key)) Then
                    o.Remove(key)
                    Continue For
                End If
                Select Case key
                    Case "properties", "$defs", "definitions"
                        Dim props = TryCast(o(key), JsonObject)
                        If props IsNot Nothing Then
                            For Each child In props.Select(Function(kv) kv.Value).ToList()
                                CleanSchema(child, strict)
                            Next
                        End If
                    Case "items", "additionalProperties", "not"
                        CleanSchema(o(key), strict)
                    Case "anyOf", "oneOf", "allOf"
                        Dim arr = TryCast(o(key), JsonArray)
                        If arr IsNot Nothing Then
                            For Each el In arr
                                CleanSchema(el, strict)
                            Next
                        End If
                End Select
            Next
            If strict Then
                ' A type list (["string", "null"]): one type, nullable.
                Dim types = TryCast(o("type"), JsonArray)
                If types IsNot Nothing Then
                    Dim names = types.Select(Function(x) x?.ToString()).Where(Function(x) Not String.IsNullOrEmpty(x)).ToList()
                    o("type") = If(names.FirstOrDefault(Function(x) x <> "null"), "string")
                    If names.Contains("null") Then o("nullable") = True
                End If
                ' required may only name properties that exist.
                Dim req = TryCast(o("required"), JsonArray), props2 = TryCast(o("properties"), JsonObject)
                If req IsNot Nothing Then
                    Dim keep = req.Select(Function(x) x?.ToString()).Where(Function(x) x IsNot Nothing AndAlso (props2 Is Nothing OrElse props2.ContainsKey(x))).Distinct().ToList()
                    o("required") = New JsonArray(keep.Select(Function(x) CType(JsonValue.Create(x), JsonNode)).ToArray())
                End If
            End If
        End Sub

        ''' <summary>A provider refusing the tool definitions (their schemas): asked again with plainer schemas.</summary>
        Friend Shared Function IsToolSchemaError(body As String) As Boolean
            Dim m = If(body, "").ToLowerInvariant()
            Return m.Contains("schema") OrElse ((m.Contains("tool") OrElse m.Contains("function")) AndAlso
                   (m.Contains("invalid") OrElse m.Contains("unsupported") OrElse m.Contains("not supported") OrElse m.Contains("unknown") OrElse m.Contains("must")))
        End Function

        ''' <summary>
        ''' What a tool result says, for the loop guard and the progress check: success plus the text without the parts that
        ''' change on every run although nothing else did - the sandbox's process ids (20261031-003110-a1b2c3), log paths and
        ''' timestamps. With them, the same "cat file | sed -n …" run 25 times in 13 minutes looked new each time: the loop
        ''' guard never stopped it and every repeat counted as progress (LedgerLite).
        ''' </summary>
        Friend Shared Function ToolResultFingerprint(success As Boolean, content As String) As String
            Dim t = If(content, "")
            t = Text.RegularExpressions.Regex.Replace(t, """log_path""\s*:\s*""[^""]*""", "")
            t = Text.RegularExpressions.Regex.Replace(t, "\b\d{8}-\d{6}-[0-9a-f]{4,}\b", "#")
            t = Text.RegularExpressions.Regex.Replace(t, "\b\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:?\d{2})?\b", "#")
            t = Text.RegularExpressions.Regex.Replace(t, """(duration|elapsed|took)[A-Za-z_]*""\s*:\s*[\d.]+", "")
            Return Convert.ToHexString(Global.System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(success.ToString() & "|" & t)))
        End Function

        ''' <summary>A tool call's identity: its name and its arguments as compact JSON (spacing and key order do not matter).</summary>
        Friend Shared Function ToolCallKey(name As String, argsJson As String) As String
            Dim canon As String
            Try
                Using doc = Text.Json.JsonDocument.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
                    canon = Canonical(doc.RootElement)
                End Using
            Catch ex As Text.Json.JsonException
                canon = Text.RegularExpressions.Regex.Replace(If(argsJson, ""), "\s+", "")
            End Try
            Return name & "|" & canon
        End Function

        ''' <summary>JsonElement, not JsonNode: a model can send a key twice ({"path":"a","path":"b"}), which JsonObject refuses to enumerate.</summary>
        Private Shared Function Canonical(e As Text.Json.JsonElement) As String
            Select Case e.ValueKind
                Case Text.Json.JsonValueKind.Object
                    ' A repeated key counts once, with its last value.
                    Dim props As New Dictionary(Of String, Text.Json.JsonElement)(StringComparer.Ordinal)
                    For Each p In e.EnumerateObject()
                        props(p.Name) = p.Value
                    Next
                    Return "{" & String.Join(",", props.OrderBy(Function(kv) kv.Key, StringComparer.Ordinal).Select(Function(kv) Text.Json.JsonSerializer.Serialize(kv.Key) & ":" & Canonical(kv.Value))) & "}"
                Case Text.Json.JsonValueKind.Array
                    Return "[" & String.Join(",", e.EnumerateArray().Select(Function(x) Canonical(x))) & "]"
                Case Text.Json.JsonValueKind.String
                    Return Text.Json.JsonSerializer.Serialize(e.GetString())
                Case Else
                    Return e.GetRawText()
            End Select
        End Function

        ''' <summary>A provider refusing frequency_penalty / presence_penalty.</summary>
        Friend Shared Function IsPenaltyParamError(body As String) As Boolean
            Dim b = If(body, "").ToLowerInvariant()
            Return b.Contains("penalty") AndAlso (b.Contains("unsupported") OrElse b.Contains("not support") OrElse b.Contains("unknown") OrElse
                                                   b.Contains("unrecognized") OrElse b.Contains("invalid") OrElse b.Contains("extra"))
        End Function

        ' ------------------------------------------------------------------ reasoning effort

        Public Shared ReadOnly Efforts As String() = {"none", "low", "medium", "high", "ultracode"}

        Friend Shared Function NormalizeEffort(effort As String) As String
            Dim e = If(effort, "").Trim().ToLowerInvariant()
            Return If(Efforts.Contains(e), e, "medium")
        End Function

        ''' <summary>The reasoning_effort parameter for the request (Nothing = leave the provider's default, medium).</summary>
        Private Function EffortParam(effort As String, model As String) As String
            If _noReasoningParam.ContainsKey(model) Then Return Nothing
            Select Case effort
                Case "low" : Return "low"
                Case "high", "ultracode" : Return "high"
                Case Else : Return Nothing
            End Select
        End Function

        Friend Shared Function EffortInstructions(effort As String) As String
            Select Case effort
                Case "none"
                    Return "Reasoning effort: none. Answer right away and briefly; do not deliberate at length."
                Case "low"
                    Return "Reasoning effort: low. Keep thinking short and answer efficiently."
                Case "high"
                    Return "Reasoning effort: high. Think the problem through carefully before answering and check your answer."
                Case "ultracode"
                    Return "Reasoning effort: Ultracode. The user wants your most careful, complete engineering work. Understand the whole problem first, " &
                           "plan, then write complete, production-quality code (no placeholders or TODOs), handle edge cases and errors, and verify your " &
                           "result before you answer. Prefer correctness over brevity; explain the key decisions briefly."
            End Select
            Return Nothing
        End Function

        ''' <summary>A 400 caused by a model that does not take reasoning_effort.</summary>
        Friend Shared Function IsReasoningParamError(body As String) As Boolean
            Dim b = If(body, "").ToLowerInvariant()
            Return b.Contains("reasoning") AndAlso (b.Contains("unsupported") OrElse b.Contains("not support") OrElse b.Contains("unknown") OrElse
                                                     b.Contains("unrecognized") OrElse b.Contains("invalid") OrElse b.Contains("extra"))
        End Function

        ' ------------------------------------------------------------------ context window

        ''' <summary>Context window sizes by model name (first match wins; Chat.ContextWindows overrides).</summary>
        Private Shared ReadOnly KnownWindows As (Pattern As String, Tokens As Integer)() = {
            ("gpt-4.1", 1047576), ("gpt-5", 400000), ("gpt-4o", 128000), ("gpt-4-turbo", 128000), ("gpt-4", 8192), ("gpt-3.5", 16385),
            ("o1", 200000), ("o3", 200000), ("o4", 200000), ("claude", 200000), ("gemini", 1048576), ("deepseek", 128000), ("kimi", 262144),
            ("moonshot", 131072), ("glm", 131072), ("qwen", 131072), ("llama-4", 1000000), ("llama", 131072), ("mistral", 131072),
            ("mixtral", 32768), ("grok-4", 256000), ("grok", 131072), ("command", 128000), ("phi", 128000), ("gemma", 131072)}

        ''' <summary>Real context windows learned from providers' answers and refusals (AI.ContextLimits).</summary>
        Public Property Limits As Global.Agentic.AI.ContextLimits

        ''' <summary>The model's context window: as configured or known, unless its provider showed otherwise (Limits).</summary>
        Public Function WindowFor(modelId As String) As Integer
            Dim configured = ConfiguredWindow(modelId)
            Return If(Limits Is Nothing, configured, Limits.Effective(modelId, configured))
        End Function

        Public Function ConfiguredWindow(modelId As String) As Integer
            ' A user's own model: the window they gave it.
            If ByoService.IsByoId(modelId) Then
                Dim b = Byo?.Get(modelId)
                If b IsNot Nothing Then Return b.ContextWindow
            End If
            Dim id = If(modelId, "").ToLowerInvariant()
            For Each kv In _opt.ContextWindows
                If id.Contains(kv.Key.ToLowerInvariant()) AndAlso kv.Value > 0 Then Return kv.Value
            Next
            For Each k In KnownWindows
                If id.Contains(k.Pattern) Then Return k.Tokens
            Next
            Return 128000
        End Function

        ''' <summary>The window of the model that answers. A custom model can route to several models, so it counts with
        ''' the largest window any of them offers: the conversation is only compacted when even that model could not hold it,
        ''' and each round is still trimmed to the model actually chosen (so a route to a smaller local model never overflows).</summary>
        Public Async Function ContextWindowAsync(model As String) As Task(Of Integer)
            Dim custom = Await _hub.GetCustomAsync(model).ConfigureAwait(False)
            Return If(custom Is Nothing, WindowFor(model), CustomWindow(custom))
        End Function

        ''' <summary>The largest context window across every model a custom model could route to (default, routes and their
        ''' fallbacks). Used for compaction and for what clients are told; a single round is budgeted for the chosen model.</summary>
        Public Function CustomWindow(m As RouterModel) As Integer
            If m Is Nothing OrElse m.Definition Is Nothing Then Return 128000
            Dim d = m.Definition
            Dim ids As New List(Of String) From {d.DefaultModel}
            ids.AddRange(If(d.DefaultFallbacks, New List(Of String)))
            For Each r In If(d.Routes, New List(Of RouterRoute))
                If r Is Nothing Then Continue For
                ids.Add(r.Model)
                ids.AddRange(If(r.Fallbacks, New List(Of String)))
            Next
            Dim best = 0
            For Each id In ids.Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase)
                best = Math.Max(best, WindowFor(id))
            Next
            Return If(best > 0, best, 128000)
        End Function

        ''' <summary>About four characters per token; images count as roughly a thousand tokens each.</summary>
        Friend Shared Function EstimateTokens(text As String) As Integer
            Return If(String.IsNullOrEmpty(text), 0, CInt(Math.Ceiling(text.Length / 4.0)))
        End Function

        ' ------------------------------------------------------------------ fitting one request into the window

        ''' <summary>The prompt may use the window minus room for the answer: a tenth of the window, at least 4,096 tokens.</summary>
        Friend Shared Function PromptBudget(window As Integer) As Integer
            Return Math.Max(1024, window - Math.Max(4096, window \ 10))
        End Function

        ''' <summary>Text characters and images in a request's messages (an image's data does not count as text).</summary>
        Friend Shared Function RequestSize(messages As JsonArray) As (Chars As Long, Images As Integer)
            Dim chars As Long = 0
            Dim images = 0
            For Each m In messages
                If m Is Nothing Then Continue For
                Dim content = m("content")
                Dim parts = TryCast(content, JsonArray)
                If parts IsNot Nothing Then
                    For Each part In parts
                        If part?("type")?.ToString() = "image_url" Then images += 1 Else chars += If(part?("text")?.ToString().Length, 0)
                    Next
                ElseIf content IsNot Nothing Then
                    chars += content.ToString().Length
                End If
                Dim calls = m("tool_calls")
                If calls IsNot Nothing Then chars += calls.ToJsonString().Length
                chars += 16
            Next
            Return (chars, images)
        End Function

        ''' <summary>Tokens of a request: its text at charsPerToken, about a thousand per image, plus extraChars (tool definitions).</summary>
        Friend Shared Function EstimateRequestTokens(messages As JsonArray, charsPerToken As Double, extraChars As Long) As Integer
            Dim size = RequestSize(messages)
            Return CInt(Math.Min(Integer.MaxValue, (size.Chars + extraChars) / Math.Max(1.0, charsPerToken) + size.Images * 1000.0))
        End Function

        ''' <summary>
        ''' Shortens a request until it fits budgetTokens: tool output first (oldest first, so the newest is cut last), then
        ''' the conversation before the latest question. The system prompt and the latest question stay whole, and every cut
        ''' says how much was left out. Providers refuse prompts that leave no room to answer; they never truncate them.
        ''' Returns whether anything was cut.
        ''' </summary>
        Friend Shared Function FitToBudget(messages As JsonArray, budgetTokens As Integer, charsPerToken As Double, extraChars As Long) As Boolean
            If EstimateRequestTokens(messages, charsPerToken, extraChars) <= budgetTokens Then Return False
            Dim lastUser = -1
            For i = messages.Count - 1 To 0 Step -1
                If messages(i)?("role")?.ToString() = "user" Then lastUser = i : Exit For
            Next
            Dim cut = False
            For Each pass In {"tool", "history"}
                For Each keep In {6000, 2000, 600}
                    For i = 1 To messages.Count - 1
                        Dim m = TryCast(messages(i), JsonObject)
                        If m Is Nothing Then Continue For
                        Dim role = If(m("role")?.ToString(), "")
                        Dim wanted = If(pass = "tool", role = "tool", role <> "tool" AndAlso role <> "system" AndAlso i <> lastUser)
                        If Not wanted Then Continue For
                        Dim value = TryCast(m("content"), JsonValue)
                        Dim s As String = Nothing
                        If value Is Nothing OrElse Not value.TryGetValue(s) OrElse s.Length <= keep + 200 Then Continue For
                        m("content") = JsonValue.Create(Shorten(s, keep))
                        cut = True
                        If EstimateRequestTokens(messages, charsPerToken, extraChars) <= budgetTokens Then Return True
                    Next
                Next
            Next
            ' Last resort: the newest message itself (a huge paste or file) - its start and end stay, its middle goes, so the
            ' answer can still be written instead of failing as too long.
            Dim newest = If(lastUser >= 0, TryCast(messages(lastUser), JsonObject), Nothing)
            If newest IsNot Nothing Then
                For Each keep In {60000, 20000, 6000, 2000}
                    Dim value = TryCast(newest("content"), JsonValue)
                    Dim s As String = Nothing
                    If value IsNot Nothing AndAlso value.TryGetValue(s) Then
                        If s.Length > keep + 200 Then newest("content") = JsonValue.Create(Shorten(s, keep)) : cut = True
                    Else
                        ' Text and pictures: the longest text part is shortened.
                        Dim parts = TryCast(newest("content"), JsonArray)
                        Dim longest = parts?.OfType(Of JsonObject)().Where(Function(p) p("type")?.ToString() = "text").
                            OrderByDescending(Function(p) If(p("text")?.ToString(), "").Length).FirstOrDefault()
                        Dim t = longest?("text")?.ToString()
                        If t IsNot Nothing AndAlso t.Length > keep + 200 Then longest("text") = Shorten(t, keep) : cut = True
                    End If
                    If EstimateRequestTokens(messages, charsPerToken, extraChars) <= budgetTokens Then Return True
                Next
            End If
            Return cut
        End Function

        Private Shared Function Shorten(s As String, keep As Integer) As String
            Dim head = keep * 2 \ 3
            Dim tail = keep - head
            Return s.Substring(0, head) & vbLf & $"[… {s.Length - keep:N0} characters cut to fit the model's context window …]" & vbLf & s.Substring(s.Length - tail)
        End Function

        ''' <summary>A provider error saying the prompt does not fit the model's context window.</summary>
        Friend Shared Function IsContextOverflow(message As String) As Boolean
            Dim m = If(message, "").ToLowerInvariant()
            Return m.Contains("context length") OrElse m.Contains("context_length") OrElse m.Contains("maximum context") OrElse m.Contains("context window") OrElse
                   m.Contains("context size") OrElse m.Contains("too many tokens") OrElse m.Contains("prompt is too long") OrElse m.Contains("input is too long") OrElse
                   m.Contains("leaves no room") OrElse m.Contains("reduce the length") OrElse (m.Contains("exceed") AndAlso m.Contains("context")) OrElse
                   (m.Contains("exceed") AndAlso m.Contains("token"))
        End Function

        Private Function EstimateMessages(list As IEnumerable(Of ChatMessageRecord)) As Integer
            Return list.Sum(Function(m) EstimateTokens(m.Content) + 6 + m.Attachments.Sum(Function(a) If(a.Kind = "image", 1000, CInt(Math.Min(a.SizeBytes / 4, 30000)))) +
                                        m.ToolCalls.Sum(Function(t) (EstimateTokens(t.Arguments) \ 2) + Math.Min(EstimateTokens(t.Result), 6000 \ 4)))
        End Function

        ''' <summary>The messages the model still gets in full: after the summary, at most HistoryMessages.</summary>
        Friend Function RecentMessages(chat As ChatConversation) As List(Of ChatMessageRecord)
            Dim after = chat.Messages.Where(Function(m) Not chat.SummaryThroughUtc.HasValue OrElse m.CreatedUtc > chat.SummaryThroughUtc.Value).ToList()
            Return after.Skip(Math.Max(0, after.Count - Math.Max(2, _opt.HistoryMessages))).ToList()
        End Function

        Private Function EstimateContext(chat As ChatConversation, settings As ChatSettings) As Integer
            Return EstimateTokens(BaseSystemPrompt) + EstimateTokens(_opt.SystemPrompt) + EstimateTokens(settings?.SystemPrompt) + chat.SummaryTokens + EstimateMessages(RecentMessages(chat)) + 200
        End Function

        ''' <summary>Removes a reasoning model's &lt;think&gt; block (closed, and an unclosed one left when it was cut off) so it is not mistaken for the summary.</summary>
        Private Shared Function StripThinking(s As String) As String
            Dim t = Regex.Replace(If(s, ""), "<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase)
            Dim open = t.IndexOf("<think>", StringComparison.OrdinalIgnoreCase)
            If open >= 0 Then t = t.Substring(0, open)
            Return t.Trim()
        End Function

        ''' <summary>How full the context window is: {used, max, percent, compacted, …}.</summary>
        Public Async Function ContextStatsAsync(username As String, chatId As String, Optional model As String = Nothing, Optional reported As Integer = 0, Optional compactStats As Object = Nothing) As Task(Of Object)
            Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            Dim m = If(String.IsNullOrWhiteSpace(model), chat.Model, model)
            Dim max = Await ContextWindowAsync(m).ConfigureAwait(False)
            Dim used = Math.Max(EstimateContext(chat, chat.Settings), reported)
            Return New With {.used = used, .max = max, .percent = Math.Round(100.0 * used / Math.Max(1, max), 1), .model = m,
                             .compacted = Not String.IsNullOrWhiteSpace(chat.Summary), .summaryTokens = chat.SummaryTokens, .summaryThroughUtc = chat.SummaryThroughUtc,
                             .compactedUtc = chat.CompactedUtc, .messages = chat.Messages.Count, .messagesInFull = RecentMessages(chat).Count,
                             .autoCompactPercent = _opt.AutoCompactPercent, .stats = compactStats}
        End Function

        ''' <summary>
        ''' Summarizes the conversation except its last <paramref name="keep"/> messages; from then on the model gets the
        ''' summary plus the newer messages. Earlier summaries are folded into the new one.
        ''' </summary>
        ''' <summary>
        ''' Folds older messages into the chat's summary. What stays in full is chosen by size, not count: the newest messages
        ''' that fit in keepTokens (at least keep of them), so a few huge tool-heavy answers do not keep the context full and
        ''' many short ones are not summarized for nothing. The transcript given to the summarizer keeps what tools did (the
        ''' file, command or page and the start of the result), so file names, commands and findings survive.
        ''' </summary>
        Public Async Function CompactAsync(username As String, chatId As String, keep As Integer, ct As CancellationToken, Optional keepTokens As Integer = 0, Optional focus As String = Nothing) As Task(Of (Ok As Boolean, Message As String, BeforeTokens As Integer, AfterTokens As Integer, BeforeMessages As Integer, AfterMessages As Integer))
            Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            Dim after = chat.Messages.Where(Function(m) Not chat.SummaryThroughUtc.HasValue OrElse m.CreatedUtc > chat.SummaryThroughUtc.Value).ToList()
            Dim beforeTokens = EstimateContext(chat, chat.Settings)
            Dim beforeMessages = after.Count
            Dim window = Await ContextWindowAsync(chat.Model).ConfigureAwait(False)
            If keepTokens <= 0 Then keepTokens = Math.Max(2000, window \ 4)
            ' Newest first: keep while within keepTokens (and always the minimum count).
            Dim kept = 0, keptTokens = 0
            For i = after.Count - 1 To 0 Step -1
                Dim t = EstimateMessages(New List(Of ChatMessageRecord) From {after(i)})
                If kept >= Math.Max(1, keep) AndAlso keptTokens + t > keepTokens Then Exit For
                kept += 1 : keptTokens += t
            Next
            Dim toSummarize = after.Take(Math.Max(0, after.Count - kept)).ToList()
            If toSummarize.Count < 2 Then Return (False, "There is not enough new conversation to compact yet.", beforeTokens, beforeTokens, beforeMessages, beforeMessages)
            Dim transcript As New StringBuilder()
            Dim files As New SortedSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each m In toSummarize
                Dim body = If(m.Content, "") & QuestionsForHistory(m)
                If m.ToolCalls.Count > 0 Then
                    Dim steps As New StringBuilder()
                    For Each t In m.ToolCalls.Take(80)
                        Dim target = ToolTarget(t.Arguments)
                        If target IsNot Nothing AndAlso Regex.IsMatch(t.Name, "write|edit|create|patch|MultiEdit|apply", RegexOptions.IgnoreCase) Then files.Add(target)
                        steps.Append(vbLf & "- ").Append(t.Name).Append(If(target Is Nothing, "", " " & target)).Append(If(t.Ok, "", " (failed)"))
                        Dim res = Regex.Replace(If(t.Result, ""), "\s+", " ").Trim()
                        If res.Length > 0 Then steps.Append(": ").Append(TextUtil.Truncate(res, 240, "…"))
                    Next
                    body &= vbLf & "(tool steps:" & steps.ToString() & ")"
                End If
                transcript.Append(If(m.Role = "user", "User: ", "Assistant: ")).Append(TextUtil.Truncate(body, 9000)).Append(vbLf & vbLf)
            Next
            If files.Count > 0 Then transcript.Append("Files changed in this part: ").Append(String.Join(", ", files.Take(200))).Append(vbLf)
            Dim text = transcript.ToString()
            If text.Length > 200000 Then text = text.Substring(0, 50000) & vbLf & "[…]" & vbLf & text.Substring(text.Length - 150000)
            Dim custom = Await _hub.GetCustomAsync(chat.Model).ConfigureAwait(False)
            Dim model = If(Not String.IsNullOrWhiteSpace(_opt.TaskModel), _opt.TaskModel, If(custom Is Nothing, chat.Model, custom.Definition.DefaultModel))
            ' Room for the summary grows with the model's window (a long chat in a big window keeps more detail).
            Dim summaryTokens = Math.Clamp(window \ 25, 2500, 8000)
            Const summarySystem As String =
                "You compress conversations so they can continue without the original messages. Write a dense Markdown summary with the sections " &
                "## Goal, ## Key facts and decisions, ## Files and code, ## Work done, ## Current state, ## Open items. Keep the user's goals and preferences, " &
                "decisions, facts and numbers, exact names of files, functions, commands, settings and URLs, errors and how they were solved, short code " &
                "that matters, and what was promised next. Under Files and code list every file that was changed and what changed in it. " &
                "Under Current state say exactly where the work stands now. Do not invent anything. Output only the Markdown summary — no preamble and no <think> reasoning."
            Dim prefix = If(String.IsNullOrWhiteSpace(chat.Summary), "", "Summary of the conversation before this part (merge it in, keep its details):" & vbLf & chat.Summary & vbLf & vbLf)
            ' A model (local reasoning models especially) can return nothing when the transcript overflows its real context
            ' window or when the whole budget goes to thinking. Thinking is suppressed; if the summary is still empty, halve the
            ' transcript and retry so compaction cannot get stuck on a large conversation.
            Dim summary As String = ""
            For attempt = 0 To 3
                summary = StripThinking(Await _hub.CompleteTextAsync(model, summarySystem,
                    prefix & If(String.IsNullOrWhiteSpace(focus), "", "Focus the summary on: " & focus.Trim() & vbLf & vbLf) & "Conversation to summarize:" & vbLf & text, summaryTokens, TimeSpan.FromSeconds(180), ct, suppressThinking:=True).ConfigureAwait(False))
                If summary.Length >= 40 Then Exit For
                If text.Length <= 12000 Then Exit For
                text = "[earlier conversation omitted]" & vbLf & text.Substring(text.Length \ 2)
            Next
            If summary.Length < 40 Then Return (False, "The model could not write a summary; try again.", beforeTokens, beforeTokens, beforeMessages, beforeMessages)
            Dim through = toSummarize.Last().CreatedUtc
            ' The last answer's count was from before the summary: the meter goes by the estimate until the next answer.
            Await _db.ExecuteAsync("UPDATE Chats SET Summary=@s, SummaryThroughUtc=@t, SummaryTokens=@n, CompactedUtc=@c, LastPromptTokens=0 WHERE Id=@id AND Username=@u",
                                   ("@s", CObj(summary)), ("@t", Db.Ts(through)), ("@n", EstimateTokens(summary)), ("@c", Db.Ts(DateTime.UtcNow)),
                                   ("@id", chatId), ("@u", username)).ConfigureAwait(False)
            ' Reflect the update on the in-memory chat so the after-stats match what the model now gets.
            chat.Summary = summary
            chat.SummaryThroughUtc = through
            chat.SummaryTokens = EstimateTokens(summary)
            chat.CompactedUtc = DateTime.UtcNow
            Dim afterTokens = EstimateContext(chat, chat.Settings)
            Live.Publish(username, chatId, "compacted", New With {.summaryThroughUtc = through, .messages = toSummarize.Count})
            Return (True, $"Compacted {toSummarize.Count} messages into a summary.", beforeTokens, afterTokens, beforeMessages, kept)
        End Function

        ''' <summary>The file, command, page or query a tool call was about (from its arguments), or Nothing. A search says what it
        ''' looks for and where: """Timeout"" in src".</summary>
        Friend Shared Function ToolTarget(argsJson As String) As String
            If String.IsNullOrWhiteSpace(argsJson) Then Return Nothing
            Try
                Dim a = JsonNode.Parse(argsJson)
                Dim pattern = a?("pattern")?.ToString()
                If Not String.IsNullOrWhiteSpace(pattern) Then
                    Dim where = If(a?("path")?.ToString(), a?("folder")?.ToString())
                    Return TextUtil.Truncate("""" & Regex.Replace(pattern, "\s+", " ").Trim() & """" & If(String.IsNullOrWhiteSpace(where), "", " in " & where.Trim()), 160, "…")
                End If
                For Each key In {"path", "file_path", "file", "filename", "command", "url", "query", "name"}
                    Dim v = a?(key)?.ToString()
                    If Not String.IsNullOrWhiteSpace(v) Then Return TextUtil.Truncate(Regex.Replace(v, "\s+", " ").Trim(), 160, "…")
                Next
            Catch ex As Text.Json.JsonException
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' A long answer's earlier tool rounds, folded into a short progress note so the answer can go on in its context window
        ''' (like a mid-task compaction): every round except the newest keepRounds, from answerStart on, is summarized and
        ''' replaced. Whole rounds only (an assistant message with its tool results), so no tool result loses its call.
        ''' </summary>
        Friend Async Function FoldOldRoundsAsync(messages As JsonArray, answerStart As Integer, model As String, keepRounds As Integer, ct As CancellationToken) As Task(Of Boolean)
            Dim starts As New List(Of Integer)
            ' The summary of an earlier fold is folded again with the steps after it, so a long answer keeps one summary instead of
            ' piling them up.
            Dim earlierFold = -1
            For i = answerStart To messages.Count - 1
                Dim m = messages(i)
                If m?("role")?.ToString() = "assistant" AndAlso m?("tool_calls") IsNot Nothing Then starts.Add(i)
                If earlierFold < 0 AndAlso starts.Count = 0 AndAlso IsFoldNote(m) Then earlierFold = i
            Next
            If starts.Count <= keepRounds Then Return False
            Dim cutEnd = starts(starts.Count - keepRounds)   ' first message that stays
            Dim cutStart = If(earlierFold >= 0, earlierFold, starts(0))
            If cutEnd - cutStart < 2 Then Return False
            Dim transcript As New StringBuilder()
            For i = cutStart To cutEnd - 1
                Dim m = messages(i)
                Dim role = m?("role")?.ToString()
                Dim content = If(TypeOf m?("content") Is JsonValue, m("content").ToString(), "")
                If IsFoldNote(m) Then
                    transcript.Append("Summary of the steps before: ").Append(TextUtil.Truncate(content.Substring(FoldNotePrefix.Length), 8000)).Append(vbLf)
                ElseIf role = "assistant" Then
                    If content.Trim().Length > 0 Then transcript.Append("Assistant: ").Append(TextUtil.Truncate(content, 3000)).Append(vbLf)
                    For Each tc In If(TryCast(m("tool_calls"), JsonArray), New JsonArray())
                        transcript.Append("Called ").Append(tc?("function")?("name")?.ToString()).Append(" ").Append(TextUtil.Truncate(tc?("function")?("arguments")?.ToString(), 400, "…")).Append(vbLf)
                    Next
                ElseIf role = "tool" Then
                    transcript.Append("Result: ").Append(TextUtil.Truncate(Regex.Replace(content, "\s+", " "), 1500, "…")).Append(vbLf)
                ElseIf content.Trim().Length > 0 Then
                    transcript.Append("Note: ").Append(TextUtil.Truncate(content, 800)).Append(vbLf)
                End If
            Next
            Dim note As String
            Try
                note = Await _hub.CompleteTextAsync(If(Not String.IsNullOrWhiteSpace(_opt.TaskModel), _opt.TaskModel, model),
                    "You summarize the steps an assistant already took in a task, so it can continue without them. Write compact Markdown: what was " &
                    "checked or changed (exact files, commands, URLs, values), what was found, what failed and why, and what remains. Do not invent anything.",
                    transcript.ToString(), 2000, TimeSpan.FromSeconds(120), ct).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is Text.Json.JsonException OrElse (TypeOf ex Is OperationCanceledException AndAlso Not ct.IsCancellationRequested)
                Return False
            End Try
            note = Regex.Replace(If(note, ""), "<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim()
            If note.Length < 40 Then Return False
            For i = cutEnd - 1 To cutStart Step -1
                messages.RemoveAt(i)
            Next
            messages.Insert(cutStart, New JsonObject From {{"role", "user"}, {"content", FoldNotePrefix & note}})
            Return True
        End Function

        Private Const FoldNotePrefix As String = "(Note from the app, not the user: to keep this answer within the context window, your earlier steps in it were folded into this summary. " &
                                                 "Continue from here.)" & vbLf

        Private Shared Function IsFoldNote(m As JsonNode) As Boolean
            Return m?("role")?.ToString() = "user" AndAlso TypeOf m?("content") Is JsonValue AndAlso
                   m("content").ToString().StartsWith(FoldNotePrefix, StringComparison.Ordinal)
        End Function

        ' ------------------------------------------------------------------ permission mode (tool approvals)

        ''' <summary>Tools that never need approval: asking the user and reading skills.</summary>
        Private Shared ReadOnly NoApprovalTools As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {AskUserTool, "Skill", "skill_file", RememberTool, ForgetTool, ImageTool, VideoTool, DiffusionTool, TodosTool, SubagentTool}

        ''' <summary>manual (asks before changes), acceptEdits (file edits run, other changes ask) or auto (never asks).</summary>
        Friend Shared Function NormalizeMode(mode As String) As String
            Select Case If(mode, "").Trim().Replace("-", "").Replace("_", "").ToLowerInvariant()
                Case "manual" : Return "manual"
                Case "acceptedits" : Return "acceptEdits"
                Case Else : Return "auto"
            End Select
        End Function

        ''' <summary>Tools that change files (in the user's apps and Claude Code's names): Accept edits runs them without asking.</summary>
        Private Shared ReadOnly FileEditTools As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "write_file", "edit_file", "edit_notebook", "Write", "Edit", "MultiEdit", "NotebookEdit"}

        ''' <summary>The command a call runs (its "command" argument), or Nothing for a tool that runs no command.</summary>
        Friend Shared Function CommandOf(argsJson As String) As String
            Try
                Dim n = TryCast(JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson)), JsonObject)
                Dim v = TryCast(n?("command"), JsonValue)
                Dim s As String = Nothing
                If v IsNot Nothing AndAlso v.TryGetValue(s) AndAlso Not String.IsNullOrWhiteSpace(s) Then Return s
            Catch ex As Text.Json.JsonException
            End Try
            Return Nothing
        End Function

        ''' <summary>
        ''' What "Always allow" covers for a command, like Claude Code's suggested rules: the program and its subcommand of each
        ''' part ("npm run", "git commit", "dotnet build"), so allowing "npm run test" does not allow "rm -rf".
        ''' </summary>
        Friend Shared Function CommandPrefixes(command As String) As List(Of String)
            Dim parts As New List(Of String)
            Dim cur As New StringBuilder()
            Dim quote = ChrW(0)
            Dim c = If(command, "")
            For i = 0 To c.Length - 1
                Dim ch = c(i)
                If quote <> ChrW(0) Then
                    cur.Append(ch)
                    If ch = quote Then quote = ChrW(0)
                    Continue For
                End If
                If ch = """"c OrElse ch = "'"c Then
                    quote = ch
                    cur.Append(ch)
                    Continue For
                End If
                Dim redirect = ch = "&"c AndAlso ((i > 0 AndAlso c(i - 1) = ">"c) OrElse (i + 1 < c.Length AndAlso c(i + 1) = ">"c))
                If Not redirect AndAlso (ch = ";"c OrElse ch = "|"c OrElse ch = "&"c OrElse ch = ChrW(10) OrElse ch = ChrW(13)) Then
                    parts.Add(cur.ToString())
                    cur.Clear()
                Else
                    cur.Append(ch)
                End If
            Next
            parts.Add(cur.ToString())
            Dim result As New List(Of String)
            For Each part In parts
                Dim tokens = part.Trim().Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
                If tokens.Length = 0 Then Continue For
                Dim prefix = tokens(0).Trim(""""c, "'"c).ToLowerInvariant()
                If tokens.Length > 1 AndAlso Regex.IsMatch(tokens(1), "^[a-zA-Z][\w:-]*$") Then prefix &= " " & tokens(1).ToLowerInvariant()
                If Not result.Contains(prefix) Then result.Add(prefix)
            Next
            Return result
        End Function

        ''' <summary>What "Always allow" stores for a call: the tool, or for a command the tool with each command prefix.</summary>
        Friend Shared Function AllowKeys(name As String, argsJson As String) As List(Of String)
            Dim command = CommandOf(argsJson)
            If command Is Nothing Then Return New List(Of String) From {name}
            Return CommandPrefixes(command).Select(Function(p) name & " " & p).ToList()
        End Function

        ''' <summary>The user chose "Always allow" for this call's tool (or, for a command, for every part's program and subcommand).</summary>
        Private Function ToolAllowed(chatId As String, name As String, argsJson As String) As Boolean
            Dim allowed = Live.AllowedTools(chatId)
            Dim keys = AllowKeys(name, argsJson)
            Return keys.Count > 0 AndAlso keys.All(Function(k) allowed.ContainsKey(k))
        End Function

        ''' <summary>What "Always allow" would cover, for the approval card ("npm run, git status" or Nothing for the whole tool).</summary>
        Friend Shared Function AlwaysScope(name As String, argsJson As String) As String
            Dim command = CommandOf(argsJson)
            If command Is Nothing Then Return Nothing
            Return String.Join(", ", CommandPrefixes(command))
        End Function

        ''' <summary>Web addresses a tool call would open (any argument that is an http(s) URL).</summary>
        Friend Shared Function HostsIn(argsJson As String) As List(Of String)
            Dim hosts As New List(Of String)
            For Each m As Match In Regex.Matches(If(argsJson, ""), "https?://([A-Za-z0-9.-]+)")
                Dim h = m.Groups(1).Value.ToLowerInvariant().TrimEnd("."c)
                If h.StartsWith("www.") Then h = h.Substring(4)
                If Not hosts.Contains(h) Then hosts.Add(h)
            Next
            Return hosts
        End Function

        ''' <summary>
        ''' Why this call must wait for the user (Nothing = it may run). Manual asks before a change the chat has not always
        ''' allowed (a command: its program and subcommand) and before each new site. Accept edits does the same, except that
        ''' file edits run. Auto never asks, except for tools the user set to ask first (x-risk "ask").
        ''' </summary>
        Private Function ApprovalReason(chatId As String, settings As ChatSettings, risk As String, server As String, name As String, argsJson As String) As String
            If NoApprovalTools.Contains(name) Then Return Nothing
            ' A command on the user's computer that runs as administrator asks every time, in every mode.
            If server = "client" AndAlso Regex.IsMatch(If(argsJson, ""), """elevated""\s*:\s*true") Then Return "it runs as administrator on your computer"
            If String.Equals(risk, "ask", StringComparison.OrdinalIgnoreCase) AndAlso Not ToolAllowed(chatId, name, argsJson) Then
                Return If(name.EndsWith("take_screenshot", StringComparison.Ordinal), "a screenshot shows everything on your screen", "you set it to ask first")
            End If
            Dim mode = NormalizeMode(If(settings?.PermissionMode, _opt.DefaultPermissionMode))
            If mode = "auto" Then Return Nothing
            If mode = "acceptEdits" AndAlso FileEditTools.Contains(name) Then Return Nothing
            Dim hosts = Live.AllowedHosts(chatId)
            Dim newHost = HostsIn(argsJson).FirstOrDefault(Function(h) Not hosts.ContainsKey(h))
            ' Reading never asks (like Claude Code): only what changes something does - and a site the chat has not opened yet.
            If String.Equals(risk, "read", StringComparison.OrdinalIgnoreCase) Then Return If(newHost Is Nothing, Nothing, "it opens " & newHost)
            If Not ToolAllowed(chatId, name, argsJson) Then
                Dim scope = AlwaysScope(name, argsJson)
                Return If(scope Is Nothing, "it is the first time this chat uses " & name, "this chat has not allowed " & scope & " commands yet")
            End If
            If newHost IsNot Nothing Then Return "it opens " & newHost
            Return Nothing
        End Function

        ' ------------------------------------------------------------------ desktop app tools (used from other windows)

        ''' <summary>The name of a tool a desktop app offers (OpenAI tool format or a bare function).</summary>
        Public Shared Function DeviceToolName(def As JsonObject) As String
            Dim fn = If(TryCast(def?("function"), JsonObject), def)
            Return Global.Agentic.Security.InputValidation.SanitizeToolName(If(fn?("name")?.ToString(), ""))
        End Function

        ''' <summary>What a desktop app offers, for the Tools menu of other windows: name, description, risk and group of each tool.</summary>
        Public Function DeviceCatalog(username As String, deviceId As String) As Object
            Dim d = Live.GetDevice(username, deviceId)
            If d Is Nothing Then Return Nothing
            Dim tools = d.Tools.Select(Function(def)
                                           Dim fn = If(TryCast(def("function"), JsonObject), def)
                                           Dim name = DeviceToolName(def)
                                           Dim group = If(name.StartsWith("mcp__", StringComparison.Ordinal), "MCP · " & name.Substring(5).Split({"__"}, StringSplitOptions.None)(0),
                                                       If(name = "Skill" OrElse name = "skill_file", "Skills", "Files & commands"))
                                           Return CObj(New With {.name = name, .description = TextUtil.Truncate(If(fn?("description")?.ToString(), ""), 300),
                                                                 .risk = If(def("x-risk")?.ToString(), fn?("x-risk")?.ToString()), .group = group})
                                       End Function).ToList()
            Return New With {.id = d.Id, .name = d.Name, .online = Live.DeviceOnline(username, d.Id), .updatedUtc = d.UpdatedUtc, .tools = tools}
        End Function

        ''' <summary>Asks every window of the user (web app or desktop app) and waits: once | always | deny.</summary>
        Private Async Function RequestApprovalAsync(username As String, chatId As String, callId As String, name As String, argsJson As String, reason As String,
                                                    server As String, emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of String)
            ' alwaysScope: what "Always allow" covers for a command ("npm run, git status"); Nothing = the whole tool.
            Dim info = New With {.id = callId, .chatId = chatId, .name = name, .arguments = argsJson, .reason = reason, .server = server, .requestedUtc = DateTime.UtcNow,
                                 .alwaysScope = AlwaysScope(name, argsJson)}
            Dim waiter = Live.WaitForApproval(username, chatId, callId, info)
            Dim choice = "deny"
            Try
                Await emit("approval", info).ConfigureAwait(False)
                ' Said at once, not only with the first reminder 15 s later.
                Await emit("status", New With {.text = "Waiting for your approval…"}).ConfigureAwait(False)
                ' Like Claude Code, the question waits for an answer while a window of the user is open (up to a day); only when
                ' nobody is there to answer does it time out after ApprovalTimeoutSeconds.
                Dim startedAt = DateTime.UtcNow
                Dim deadline = startedAt.AddSeconds(Math.Max(30, _opt.ApprovalTimeoutSeconds))
                While Not waiter.IsCompleted
                    If DateTime.UtcNow >= deadline Then
                        If Not Live.UserOnline(username) OrElse DateTime.UtcNow - startedAt > TimeSpan.FromHours(24) Then Exit While
                        deadline = DateTime.UtcNow.AddSeconds(60)
                    End If
                    Dim finished = Await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(15), ct)).ConfigureAwait(False)
                    ct.ThrowIfCancellationRequested()
                    If finished IsNot waiter Then Await emit("status", New With {.text = "Waiting for your approval…"}).ConfigureAwait(False)
                End While
                ' Nobody answered in time (the user is away): not a refusal - the model hears that it may ask again later.
                choice = If(waiter.IsCompleted, Await waiter.ConfigureAwait(False), "timeout")
            Finally
                Live.ForgetApproval(username, chatId, callId)
            End Try
            If choice = "always" Then
                For Each k In AllowKeys(name, argsJson)
                    Live.AllowedTools(chatId)(k) = 0
                Next
                For Each h In HostsIn(argsJson)
                    Live.AllowedHosts(chatId)(h) = 0
                Next
            End If
            Await emit("approval_resolved", New With {.id = callId, .choice = choice}).ConfigureAwait(False)
            Return choice
        End Function

    End Class

End Namespace
