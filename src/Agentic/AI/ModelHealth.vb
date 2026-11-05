Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Logging
Imports Agentic.Persistence

Namespace AI

    ''' <summary>One model call as the health check sees it: from the Chat tab, the API (/v1) or an agent.</summary>
    Public NotInheritable Class ModelCallRecord
        Public Property Model As String
        ''' <summary>chat | api | agents</summary>
        Public Property Source As String
        Public Property Ok As Boolean
        Public Property DurationMs As Long
        Public Property CreatedUtc As DateTime
        Public Property ErrorText As String
    End Class

    ''' <summary>How a model has been doing lately (the dot in the model picker).</summary>
    Public NotInheritable Class ModelHealthInfo
        ''' <summary>green | yellow | red. Models without calls in the last day have no entry (gray).</summary>
        Public Property State As String
        Public Property Calls As Integer
        Public Property Failures As Integer
        ''' <summary>"1h" when the last hour had enough calls, else "recent" (the latest calls of the last day).</summary>
        Public Property Window As String
        Public Property LastUtc As DateTime
        Public Property LastOk As Boolean
        Public Property LastFailureUtc As DateTime?
        ''' <summary>What the latest failure was, in a few words (quota, not available, timed out …).</summary>
        Public Property LastErrorKind As String
        ''' <summary>The latest failure's text (shown to administrators only).</summary>
        Public Property LastError As String
        Public Property Chat As Integer
        Public Property Api As Integer
        Public Property Agents As Integer
        ''' <summary>How fast the model answered lately (Nothing: no finished call in the last day).</summary>
        Public Property Speed As ModelSpeedInfo
    End Class

    ''' <summary>One model call's speed: a chat round, an API request or an agent's call.</summary>
    Public NotInheritable Class ModelSpeedSample
        ''' <summary>The model that answered.</summary>
        Public Property Model As String
        ''' <summary>The model asked for when it differs (a custom router model): the call counts for it too.</summary>
        Public Property Via As String
        Public Property Source As String
        ''' <summary>Request sent to the first token streamed back; Nothing when the answer came in one piece.</summary>
        Public Property FirstTokenMs As Long?
        ''' <summary>Request sent to the last token.</summary>
        Public Property DurationMs As Long
        Public Property OutputTokens As Long
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>A model's speed from its latest calls (shown in the model picker).</summary>
    Public NotInheritable Class ModelSpeedInfo
        ''' <summary>Average response time: request sent to the answer complete.</summary>
        Public Property ResponseMs As Long
        ''' <summary>Average wait for the first token (streamed calls; Nothing without any).</summary>
        Public Property FirstTokenMs As Long?
        ''' <summary>Output tokens per second while the model writes (first token to last).</summary>
        Public Property Tps As Double
        Public Property Calls As Integer
    End Class

    ''' <summary>
    ''' The health of every model from its recent calls in the Chat tab, the API and agents: green (working), yellow (some
    ''' failures), red (mostly or lately failing). A call the user stopped is not a failure. Also each model's speed: average
    ''' response time and tokens per second. Recomputed at most every 15 s.
    ''' </summary>
    Public NotInheritable Class ModelHealth

        Public Const HourWindowMinutes As Integer = 60
        ''' <summary>Fewer calls than this in the last hour: the latest calls of the last day decide instead.</summary>
        Public Const MinHourCalls As Integer = 5
        Public Const RecentCalls As Integer = 10
        ''' <summary>A model's speed is the average of its latest calls of the last day, at most this many.</summary>
        Public Const SpeedCalls As Integer = 50
        ''' <summary>Writing shorter than this (an answer sent in one piece) cannot be timed: the whole call counts instead.</summary>
        Public Const MinWritingMs As Long = 50
        Private Const CacheSeconds As Integer = 15

        Private ReadOnly _db As Database
        Private ReadOnly _log As ILog
        Private ReadOnly _gate As New SemaphoreSlim(1, 1)
        Private _cached As Dictionary(Of String, ModelHealthInfo)
        Private _cachedAt As DateTime = DateTime.MinValue

        Public Sub New(db As Database, Optional log As ILog = Nothing)
            _db = db
            _log = log
        End Sub

        ''' <summary>Every model's health and speed (cached for a few seconds: every open chat asks for it).</summary>
        Public Async Function SnapshotAsync() As Task(Of Dictionary(Of String, ModelHealthInfo))
            If _cached IsNot Nothing AndAlso (DateTime.UtcNow - _cachedAt).TotalSeconds < CacheSeconds Then Return _cached
            Await _gate.WaitAsync().ConfigureAwait(False)
            Try
                If _cached IsNot Nothing AndAlso (DateTime.UtcNow - _cachedAt).TotalSeconds < CacheSeconds Then Return _cached
                Dim now = DateTime.UtcNow
                Dim health = Evaluate(Await LoadAsync(now.AddDays(-1)).ConfigureAwait(False), now)
                For Each kv In Speed(Await LoadSpeedAsync(now.AddDays(-1)).ConfigureAwait(False), now)
                    Dim h As ModelHealthInfo = Nothing
                    If health.TryGetValue(kv.Key, h) Then h.Speed = kv.Value
                Next
                _cached = health
                _cachedAt = now
                Return _cached
            Finally
                _gate.Release()
            End Try
        End Function

        ''' <summary>
        ''' Records how fast one model call was (request sent, first token, last token) and the tokens it wrote. Best effort:
        ''' a failure here never touches the answer.
        ''' </summary>
        Public Async Function RecordSpeedAsync(model As String, via As String, source As String, firstTokenMs As Long?, durationMs As Long, outputTokens As Long) As Task
            If String.IsNullOrWhiteSpace(model) OrElse durationMs <= 0 OrElse outputTokens <= 0 Then Return
            If firstTokenMs.HasValue AndAlso (firstTokenMs.Value < 0 OrElse firstTokenMs.Value > durationMs) Then firstTokenMs = Nothing
            If String.IsNullOrWhiteSpace(via) OrElse String.Equals(via, model, StringComparison.OrdinalIgnoreCase) Then via = Nothing
            Try
                Await _db.ExecuteAsync("INSERT INTO ModelSpeed(Model, Via, Source, FirstTokenMs, DurationMs, OutputTokens, CreatedUtc) VALUES (@m, @v, @s, @f, @d, @o, @now)",
                                       ("@m", CObj(model)), ("@v", via), ("@s", source), ("@f", If(firstTokenMs.HasValue, CObj(firstTokenMs.Value), Nothing)),
                                       ("@d", durationMs), ("@o", outputTokens), ("@now", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
            Catch ex As Exception
                _log?.Warn("ModelHealth", $"Could not record the speed of {model}: {ex.Message}")
            End Try
        End Function

        ''' <summary>Whether a streamed chunk carries output (text, thinking or a tool call): the first one ends the wait for the model.</summary>
        Public Shared Function ChunkHasOutput(chunk As JsonNode) As Boolean
            Dim delta = TryCast(Utilities.JsonUtil.FirstChoice(chunk)?("delta"), JsonObject)
            If delta Is Nothing Then Return False
            For Each name In {"content", "reasoning_content", "reasoning"}
                Dim v = TryCast(delta(name), JsonValue)
                Dim s As String = Nothing
                If v IsNot Nothing AndAlso v.TryGetValue(s) AndAlso s.Length > 0 Then Return True
            Next
            Dim calls = TryCast(delta("tool_calls"), JsonArray)
            Return calls IsNot Nothing AndAlso calls.Count > 0
        End Function

        Private Async Function LoadSpeedAsync(sinceUtc As DateTime) As Task(Of List(Of ModelSpeedSample))
            Return Await _db.QueryAsync(
                "SELECT Model, Via, Source, FirstTokenMs, DurationMs, OutputTokens, CreatedUtc FROM ModelSpeed WHERE CreatedUtc>=@s ORDER BY CreatedUtc DESC LIMIT 20000",
                Function(r) New ModelSpeedSample With {.Model = Db.Str(r, "Model"), .Via = Db.Str(r, "Via"), .Source = Db.Str(r, "Source"), .FirstTokenMs = Db.LngN(r, "FirstTokenMs"),
                                                       .DurationMs = Db.Lng(r, "DurationMs"), .OutputTokens = Db.Lng(r, "OutputTokens"), .CreatedUtc = Db.Dt(r, "CreatedUtc")},
                ("@s", CObj(Db.Ts(sinceUtc)))).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Each model's speed from its latest calls of the last day (at most SpeedCalls): the average response time (request
        ''' to the last token), the average wait for the first token, and output tokens per second while it writes (all its
        ''' tokens over all its writing time, so long answers weigh more than one-word ones). A call through a custom router
        ''' model counts for the router model too.
        ''' </summary>
        Public Shared Function Speed(samples As IEnumerable(Of ModelSpeedSample), nowUtc As DateTime) As Dictionary(Of String, ModelSpeedInfo)
            Dim byModel As New Dictionary(Of String, List(Of ModelSpeedSample))(StringComparer.OrdinalIgnoreCase)
            For Each s In samples
                If s.CreatedUtc <= nowUtc.AddDays(-1) OrElse s.DurationMs <= 0 OrElse s.OutputTokens <= 0 OrElse String.IsNullOrWhiteSpace(s.Model) Then Continue For
                Dim keys As New List(Of String) From {s.Model}
                If Not String.IsNullOrWhiteSpace(s.Via) AndAlso Not String.Equals(s.Via, s.Model, StringComparison.OrdinalIgnoreCase) Then keys.Add(s.Via)
                For Each key In keys
                    Dim list As List(Of ModelSpeedSample) = Nothing
                    If Not byModel.TryGetValue(key, list) Then list = New List(Of ModelSpeedSample) : byModel(key) = list
                    list.Add(s)
                Next
            Next
            Dim result As New Dictionary(Of String, ModelSpeedInfo)(StringComparer.OrdinalIgnoreCase)
            For Each kv In byModel
                Dim latest = kv.Value.OrderByDescending(Function(s) s.CreatedUtc).Take(SpeedCalls).ToList()
                Dim tokens = 0L, writingMs = 0L
                For Each s In latest
                    ' Writing: first token to last. Not streamed, or too quick to time (the answer came in one piece): the whole call.
                    Dim writing = If(s.FirstTokenMs.HasValue, s.DurationMs - s.FirstTokenMs.Value, 0L)
                    If writing < MinWritingMs Then writing = s.DurationMs
                    tokens += s.OutputTokens
                    writingMs += writing
                Next
                Dim firsts = latest.Where(Function(s) s.FirstTokenMs.HasValue).ToList()
                result(kv.Key) = New ModelSpeedInfo With {
                    .Calls = latest.Count, .ResponseMs = CLng(Math.Round(latest.Average(Function(s) s.DurationMs))),
                    .FirstTokenMs = If(firsts.Count > 0, CLng(Math.Round(firsts.Average(Function(s) s.FirstTokenMs.Value))), CType(Nothing, Long?)),
                    .Tps = Math.Round(tokens / (writingMs / 1000.0), 1)}
            Next
            Return result
        End Function

        ''' <summary>The calls of the last day: chat and API calls (by the model asked for and the model a router chose), agents' calls.</summary>
        Private Async Function LoadAsync(sinceUtc As DateTime) As Task(Of List(Of ModelCallRecord))
            Dim since = Db.Ts(sinceUtc)
            Dim list As New List(Of ModelCallRecord)
            Dim api = Await _db.QueryAsync(
                "SELECT Source, Model, RoutedModel, Status, DurationMs, Error, CreatedUtc FROM ModelApiUsage WHERE CreatedUtc>=@s ORDER BY CreatedUtc DESC LIMIT 20000",
                Function(r) (Source:=Db.Str(r, "Source"), Model:=Db.Str(r, "Model"), Routed:=Db.Str(r, "RoutedModel"), Status:=Db.Int(r, "Status"),
                             Ms:=Db.Lng(r, "DurationMs"), Err:=Db.Str(r, "Error"), At:=Db.Dt(r, "CreatedUtc")), ("@s", CObj(since))).ConfigureAwait(False)
            For Each c In api
                If IsStopped(c.Err) Then Continue For
                Dim source = If(String.Equals(c.Source, "chat", StringComparison.OrdinalIgnoreCase), "chat", "api")
                Dim ok = c.Status >= 200 AndAlso c.Status < 300
                For Each m In {c.Model, c.Routed}.Where(Function(x) Not String.IsNullOrWhiteSpace(x) AndAlso x <> "?").Distinct(StringComparer.OrdinalIgnoreCase)
                    list.Add(New ModelCallRecord With {.Model = m, .Source = source, .Ok = ok, .DurationMs = c.Ms, .CreatedUtc = c.At, .ErrorText = If(ok, Nothing, Readable(c.Status, c.Err))})
                Next
            Next
            Dim agents = Await _db.QueryAsync(
                "SELECT ModelId, Success, ErrorKind, Error, DurationMs, CreatedUtc FROM AiCalls WHERE CreatedUtc>=@s ORDER BY CreatedUtc DESC LIMIT 20000",
                Function(r) (Model:=Db.Str(r, "ModelId"), Ok:=Db.Bool(r, "Success"), Kind:=Db.Str(r, "ErrorKind"), Err:=Db.Str(r, "Error"),
                             Ms:=Db.Lng(r, "DurationMs"), At:=Db.Dt(r, "CreatedUtc")), ("@s", CObj(since))).ConfigureAwait(False)
            For Each c In agents
                If String.IsNullOrWhiteSpace(c.Model) Then Continue For
                If Not c.Ok AndAlso (String.Equals(c.Kind, "Cancelled", StringComparison.OrdinalIgnoreCase) OrElse String.Equals(c.Kind, "Canceled", StringComparison.OrdinalIgnoreCase)) Then Continue For
                list.Add(New ModelCallRecord With {.Model = c.Model, .Source = "agents", .Ok = c.Ok, .DurationMs = c.Ms, .CreatedUtc = c.At,
                                                   .ErrorText = If(c.Ok, Nothing, If(c.Err, c.Kind))})
            Next
            Return list
        End Function

        ''' <summary>The user pressed stop (or the client went away): not the model's fault.</summary>
        Public Shared Function IsStopped(errorText As String) As Boolean
            If String.IsNullOrWhiteSpace(errorText) Then Return False
            Dim t = errorText.Trim()
            Return t.Equals("Stopped.", StringComparison.OrdinalIgnoreCase) OrElse t.Equals("Stopped", StringComparison.OrdinalIgnoreCase) OrElse
                   Regex.IsMatch(t, "^(the operation was |request )?cancel+ed\.?$", RegexOptions.IgnoreCase)
        End Function

        ''' <summary>A failed call's error for people: the provider's message when it answered with JSON, after the HTTP status.</summary>
        Public Shared Function Readable(status As Integer, errorText As String) As String
            Dim message = If(errorText, "").Trim()
            If message.StartsWith("{", StringComparison.Ordinal) Then
                Try
                    Using doc = JsonDocument.Parse(message)
                        Dim root = doc.RootElement, el As JsonElement
                        If root.ValueKind = JsonValueKind.Object Then
                            If root.TryGetProperty("error", el) AndAlso el.ValueKind = JsonValueKind.Object Then root = el
                            For Each name In {"message", "detail", "error"}
                                If root.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrWhiteSpace(el.GetString()) Then
                                    message = el.GetString().Trim()
                                    Exit For
                                End If
                            Next
                        End If
                    End Using
                Catch ex As JsonException
                    ' Not JSON after all: the text as it is.
                End Try
            End If
            If status <= 0 OrElse (status >= 200 AndAlso status < 300) Then Return message
            Return If(message = "", $"HTTP {status}", $"HTTP {status}: {message}")
        End Function

        ''' <summary>A failure in a few words, without any of its details.</summary>
        Public Shared Function ErrorKind(errorText As String) As String
            Dim t = If(errorText, "")
            If Regex.IsMatch(t, "quota|insufficient|top up|billing|balance|credit|payment", RegexOptions.IgnoreCase) Then Return "quota or billing"
            If Regex.IsMatch(t, "not ?found|no deployments|no available channel|does not exist|unknown model|\b404\b", RegexOptions.IgnoreCase) Then Return "model not available"
            If Regex.IsMatch(t, "timed? ?out|\b524\b|\b504\b|\b408\b", RegexOptions.IgnoreCase) Then Return "timed out"
            If Regex.IsMatch(t, "rate.?limit|\b429\b|in-flight|overload|busy|capacity|too many", RegexOptions.IgnoreCase) Then Return "overloaded"
            If Regex.IsMatch(t, "connection|unavailable|unreachable|refused|\b50[23]\b", RegexOptions.IgnoreCase) Then Return "unreachable"
            Return "error"
        End Function

        ''' <summary>
        ''' The health of each model: its calls of the last hour (or, with fewer than MinHourCalls, its last RecentCalls calls of
        ''' the day). Red: the latest calls (at least two) all failed, or half or more failed. Yellow: a failure among the latest
        ''' five, or 15% or more failed. Green otherwise.
        ''' </summary>
        Public Shared Function Evaluate(calls As IEnumerable(Of ModelCallRecord), nowUtc As DateTime) As Dictionary(Of String, ModelHealthInfo)
            Dim result As New Dictionary(Of String, ModelHealthInfo)(StringComparer.OrdinalIgnoreCase)
            For Each g In calls.Where(Function(c) c.CreatedUtc > nowUtc.AddDays(-1)).GroupBy(Function(c) c.Model, StringComparer.OrdinalIgnoreCase)
                Dim all = g.OrderByDescending(Function(c) c.CreatedUtc).ToList()
                Dim hour = all.Where(Function(c) c.CreatedUtc >= nowUtc.AddMinutes(-HourWindowMinutes)).ToList()
                Dim useHour = hour.Count >= MinHourCalls
                Dim considered = If(useHour, hour, all.Take(RecentCalls).ToList())
                If considered.Count = 0 Then Continue For
                Dim failures = considered.Where(Function(c) Not c.Ok).ToList()
                Dim latest = considered.Take(3).ToList()
                Dim failRate = failures.Count / CDbl(considered.Count)
                Dim state As String
                If latest.Count >= 2 AndAlso latest.All(Function(c) Not c.Ok) Then
                    state = "red"
                ElseIf considered.Count >= 4 AndAlso failRate >= 0.5 Then
                    state = "red"
                ElseIf failRate >= 0.15 OrElse considered.Take(5).Any(Function(c) Not c.Ok) Then
                    state = "yellow"
                Else
                    state = "green"
                End If
                Dim lastFailure = all.FirstOrDefault(Function(c) Not c.Ok)
                result(g.Key) = New ModelHealthInfo With {
                    .State = state, .Calls = considered.Count, .Failures = failures.Count, .Window = If(useHour, "1h", "recent"),
                    .LastUtc = all(0).CreatedUtc, .LastOk = all(0).Ok,
                    .LastFailureUtc = lastFailure?.CreatedUtc, .LastErrorKind = If(lastFailure Is Nothing, Nothing, ErrorKind(lastFailure.ErrorText)),
                    .LastError = If(lastFailure Is Nothing, Nothing, Utilities.TextUtil.Truncate(lastFailure.ErrorText, 300)),
                    .Chat = considered.Where(Function(c) c.Source = "chat").Count(), .Api = considered.Where(Function(c) c.Source = "api").Count(),
                    .Agents = considered.Where(Function(c) c.Source = "agents").Count()}
            Next
            Return result
        End Function

    End Class

End Namespace
