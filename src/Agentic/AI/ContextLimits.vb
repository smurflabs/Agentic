Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Globalization
Imports System.Text.RegularExpressions
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Persistence

Namespace AI

    ''' <summary>
    ''' Learns each cloud model's real context window from what its provider accepts and refuses, as agents and chat users
    ''' talk to it. Configured sizes (Models, Chat.ContextWindows, the built-in list) are often guesses: a refusal "too long"
    ''' proves the window is smaller (the provider usually names the real number), and a prompt the provider counted and
    ''' answered proves it is at least that big. The learned size is kept in ModelContextLimits and used everywhere the
    ''' window is budgeted (agents' context compaction, chat rounds, the context meter).
    ''' </summary>
    Public NotInheritable Class ContextLimits

        Public NotInheritable Class Entry
            Public Property ModelId As String
            ''' <summary>The largest prompt the provider counted and answered (tokens).</summary>
            Public Property MaxAccepted As Integer
            ''' <summary>The smallest prompt refused as too long when the refusal named no limit (estimated tokens); 0 = none.</summary>
            Public Property MinRejected As Integer
            ''' <summary>The limit the provider itself named in a refusal; 0 = none.</summary>
            Public Property Reported As Integer
            Public Property Accepted As Integer
            Public Property Rejected As Integer
            Public Property LastError As String
            ''' <summary>Who taught it last: "agents" or "chat".</summary>
            Public Property Source As String
            Public Property UpdatedUtc As DateTime

            ''' <summary>
            ''' The learned window, 0 when only a lower bound is known (nothing was refused): what the provider said, else a
            ''' little under the smallest refused prompt (that size is an estimate) - never less than a prompt it accepted.
            ''' </summary>
            Public ReadOnly Property Limit As Integer
                Get
                    Dim upper = If(Reported > 0, Reported, If(MinRejected > 0, CInt(MinRejected * 0.9), 0))
                    If upper = 0 Then Return 0
                    Return Math.Max(upper, MaxAccepted)
                End Get
            End Property
        End Class

        ''' <summary>Prompts smaller than this teach nothing about the window (and are cheap to count wrong).</summary>
        Public Const MinUsefulTokens As Integer = 2000

        Private ReadOnly _db As Database
        Private ReadOnly _log As ILog
        Private ReadOnly _map As New ConcurrentDictionary(Of String, Entry)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>A model's learned window changed (old effective size, new).</summary>
        Public Property Changed As Action(Of Entry)

        Public Sub New(db As Database, log As ILog)
            _db = db
            _log = log
        End Sub

        Public Async Function LoadAsync() As Task
            Dim rows = Await _db.QueryAsync("SELECT * FROM ModelContextLimits",
                Function(r) New Entry With {.ModelId = Db.Str(r, "ModelId"), .MaxAccepted = Db.Int(r, "MaxAccepted"), .MinRejected = Db.Int(r, "MinRejected"),
                                            .Reported = Db.Int(r, "Reported"), .Accepted = Db.Int(r, "Accepted"), .Rejected = Db.Int(r, "Rejected"),
                                            .LastError = Db.Str(r, "LastError"), .Source = Db.Str(r, "Source"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}).ConfigureAwait(False)
            For Each e In rows
                If Not String.IsNullOrEmpty(e.ModelId) Then _map(e.ModelId) = e
            Next
        End Function

        Public Function [Get](modelId As String) As Entry
            If String.IsNullOrWhiteSpace(modelId) Then Return Nothing
            Dim e As Entry = Nothing
            Return If(_map.TryGetValue(modelId.Trim(), e), e, Nothing)
        End Function

        Public Function All() As List(Of Entry)
            Return _map.Values.OrderBy(Function(e) e.ModelId, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>
        ''' The window to budget with: the configured size, unless the provider showed otherwise - a refusal (or the limit it
        ''' named) wins, and a model that answered a bigger prompt than configured has at least that much.
        ''' </summary>
        Public Function Effective(modelId As String, configured As Integer) As Integer
            Dim e = [Get](modelId)
            If e Is Nothing Then Return configured
            Dim lim = e.Limit
            If lim > 0 Then Return lim
            Return Math.Max(configured, e.MaxAccepted)
        End Function

        ''' <summary>The provider counted this prompt and answered it.</summary>
        Public Sub RecordAccepted(modelId As String, promptTokens As Long, source As String)
            If String.IsNullOrWhiteSpace(modelId) OrElse promptTokens < MinUsefulTokens OrElse promptTokens > 50_000_000 Then Return
            Dim tokens = CInt(promptTokens)
            Dim e = _map.GetOrAdd(modelId.Trim(), Function(k) New Entry With {.ModelId = k})
            Dim save = False, before As Integer
            SyncLock e
                before = e.Limit
                e.Accepted += 1
                If tokens > e.MaxAccepted Then
                    ' Saved when it grows noticeably (not at every call).
                    save = tokens > e.MaxAccepted * 1.05 OrElse e.MaxAccepted = 0
                    e.MaxAccepted = tokens
                End If
                ' It took a prompt as big as one "refused" earlier: that refusal's size was a bad estimate - forget it.
                If e.MinRejected > 0 AndAlso tokens >= e.MinRejected Then e.MinRejected = 0 : save = True
                If save Then e.Source = source : e.UpdatedUtc = DateTime.UtcNow
            End SyncLock
            If save Then Persist(e, before)
        End Sub

        ''' <summary>
        ''' The provider refused a prompt as too long. Returns the learned window (0 when nothing could be learned): the limit
        ''' it named, else a little under this prompt's size.
        ''' </summary>
        Public Function RecordRejected(modelId As String, attemptedTokens As Long, errorText As String, source As String) As Integer
            If String.IsNullOrWhiteSpace(modelId) Then Return 0
            Dim parsed = ParseLimit(errorText)
            If parsed.Requested > 0 Then attemptedTokens = parsed.Requested
            If parsed.Limit = 0 AndAlso attemptedTokens < MinUsefulTokens Then Return 0
            Dim e = _map.GetOrAdd(modelId.Trim(), Function(k) New Entry With {.ModelId = k})
            Dim before As Integer
            SyncLock e
                before = e.Limit
                e.Rejected += 1
                If parsed.Limit > 0 Then
                    e.Reported = parsed.Limit
                ElseIf attemptedTokens > 0 Then
                    e.MinRejected = If(e.MinRejected = 0, CInt(Math.Min(Integer.MaxValue, attemptedTokens)), Math.Min(e.MinRejected, CInt(Math.Min(Integer.MaxValue, attemptedTokens))))
                End If
                e.LastError = TextUtil.Truncate(Regex.Replace(If(errorText, ""), "\s+", " ").Trim(), 300)
                e.Source = source
                e.UpdatedUtc = DateTime.UtcNow
            End SyncLock
            Persist(e, before)
            Return e.Limit
        End Function

        ''' <summary>Forgets what was learned about a model (its configured size applies again).</summary>
        Public Async Function ForgetAsync(modelId As String) As Task(Of Boolean)
            Dim e As Entry = Nothing
            If String.IsNullOrWhiteSpace(modelId) OrElse Not _map.TryRemove(modelId.Trim(), e) Then Return False
            Await _db.ExecuteAsync("DELETE FROM ModelContextLimits WHERE ModelId=@m", ("@m", CObj(e.ModelId))).ConfigureAwait(False)
            Changed?.Invoke(e)
            Return True
        End Function

        Private Sub Persist(e As Entry, beforeLimit As Integer)
            Dim snapshot As Entry
            SyncLock e
                snapshot = e.MemberwiseCloneEntry()
            End SyncLock
            If snapshot.Limit <> beforeLimit Then
                _log.Info("ContextLimits", $"{snapshot.ModelId}: context window learned as {snapshot.Limit:N0} tokens (accepted up to {snapshot.MaxAccepted:N0}" &
                                           If(snapshot.Reported > 0, $", provider says {snapshot.Reported:N0}", If(snapshot.MinRejected > 0, $", refused at {snapshot.MinRejected:N0}", "")) & $", from {snapshot.Source}).")
            End If
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Await _db.ExecuteAsync(
                                              "INSERT INTO ModelContextLimits (ModelId, MaxAccepted, MinRejected, Reported, Accepted, Rejected, LastError, Source, UpdatedUtc) " &
                                              "VALUES (@m, @a, @r, @p, @ac, @rj, @e, @s, @u) ON CONFLICT(ModelId) DO UPDATE SET MaxAccepted=@a, MinRejected=@r, Reported=@p, " &
                                              "Accepted=@ac, Rejected=@rj, LastError=@e, Source=@s, UpdatedUtc=@u",
                                              ("@m", CObj(snapshot.ModelId)), ("@a", snapshot.MaxAccepted), ("@r", snapshot.MinRejected), ("@p", snapshot.Reported),
                                              ("@ac", snapshot.Accepted), ("@rj", snapshot.Rejected), ("@e", CObj(snapshot.LastError)), ("@s", CObj(snapshot.Source)),
                                              ("@u", Db.Ts(snapshot.UpdatedUtc))).ConfigureAwait(False)
                                      Catch ex As Exception
                                          _log.Warn("ContextLimits", $"Could not save the context limit of {snapshot.ModelId}: {ex.Message}")
                                      End Try
                                  End Function)
            If snapshot.Limit <> beforeLimit Then Changed?.Invoke(snapshot)
        End Sub

        ' ------------------------------------------------------------------ reading providers' refusals

        Private Shared ReadOnly Num As String = "(\d{1,3}(?:[,.\s]\d{3})+|\d{3,9})"

        ''' <summary>(limit named, prompt size named) in the patterns providers use (both are tokens; 0 when not said).</summary>
        Private Shared ReadOnly Patterns As (Rx As Regex, LimitGroup As Integer, RequestedGroup As Integer)() = {
            (New Regex("maximum context length is " & Num & " tokens.*?(?:requested|resulted in|have|has|contains?|sent)\D{0,20}?" & Num, RegexOptions.IgnoreCase Or RegexOptions.Singleline), 1, 2),
            (New Regex("prompt is too long:\s*" & Num & " tokens\s*>\s*" & Num, RegexOptions.IgnoreCase), 2, 1),
            (New Regex("input token count\D{0,10}" & Num & "\D{0,60}?maximum number of tokens allowed\D{0,10}" & Num, RegexOptions.IgnoreCase), 2, 1),
            (New Regex("prompt contains " & Num & " tokens.*?model with " & Num & " maximum context", RegexOptions.IgnoreCase Or RegexOptions.Singleline), 2, 1),
            (New Regex("request \(" & Num & " tokens\) exceeds the available context size \(" & Num & " tokens\)", RegexOptions.IgnoreCase), 2, 1),
            (New Regex("(?:requested|request has|input has|input of|prompt has|you sent)\D{0,15}" & Num & " (?:input )?tokens\D{0,80}?(?:limit|maximum|max|context(?: length| window| size)?)\D{0,25}" & Num, RegexOptions.IgnoreCase), 2, 1),
            (New Regex("maximum context length is " & Num, RegexOptions.IgnoreCase), 1, 0),
            (New Regex("context (?:length|window|size) (?:of|is|limit(?: is)?|=|:)\s*" & Num, RegexOptions.IgnoreCase), 1, 0),
            (New Regex("(?:max(?:imum)?[ _]?(?:input|prompt|context)[ _]?(?:tokens|length)|n_ctx(?:_slot|_train)?)\s*(?:is|of|=|:)?\s*" & Num, RegexOptions.IgnoreCase), 1, 0),
            (New Regex("range of input length should be \[\d+,\s*" & Num & "\]", RegexOptions.IgnoreCase), 1, 0),
            (New Regex("exceeds? (?:the )?(?:model'?s? )?(?:context|token) (?:limit|window|length) of " & Num, RegexOptions.IgnoreCase), 1, 0)}

        Friend Shared Function ParseLimit(text As String) As (Limit As Integer, Requested As Integer)
            Dim t = If(text, "")
            ' A limit on the answer's length (max_tokens, output tokens) says nothing about the context window.
            If Regex.IsMatch(t, "output tokens|max_tokens|max_completion_tokens|completion tokens? (?:limit|maximum)", RegexOptions.IgnoreCase) AndAlso
               Not Regex.IsMatch(t, "context|prompt is too long|input", RegexOptions.IgnoreCase) Then Return (0, 0)
            For Each p In Patterns
                Dim m = p.Rx.Match(t)
                If Not m.Success Then Continue For
                Dim limit = ToInt(m.Groups(p.LimitGroup).Value)
                Dim requested = If(p.RequestedGroup > 0, ToInt(m.Groups(p.RequestedGroup).Value), 0)
                If limit < 1024 OrElse limit > 20_000_000 Then Continue For
                ' A refusal is for a prompt over the limit: two numbers the other way round were read wrong.
                If requested > 0 AndAlso requested <= limit Then requested = 0
                Return (limit, requested)
            Next
            Return (0, 0)
        End Function

        Private Shared Function ToInt(s As String) As Integer
            Dim digits = Regex.Replace(If(s, ""), "[^\d]", "")
            Dim v As Long
            If digits.Length = 0 OrElse digits.Length > 10 OrElse Not Long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, v) Then Return 0
            Return CInt(Math.Min(Integer.MaxValue, v))
        End Function
    End Class

    Friend Module ContextLimitEntryExtensions
        <System.Runtime.CompilerServices.Extension>
        Friend Function MemberwiseCloneEntry(e As ContextLimits.Entry) As ContextLimits.Entry
            Return New ContextLimits.Entry With {.ModelId = e.ModelId, .MaxAccepted = e.MaxAccepted, .MinRejected = e.MinRejected, .Reported = e.Reported,
                                                 .Accepted = e.Accepted, .Rejected = e.Rejected, .LastError = e.LastError, .Source = e.Source, .UpdatedUtc = e.UpdatedUtc}
        End Function
    End Module

End Namespace
