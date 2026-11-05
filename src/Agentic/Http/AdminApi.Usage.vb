Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Persistence

Namespace Http

    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterUsage(r As Router)
            r.Map("GET", "/api/usage/timeline", AuthLevel.Read, AddressOf UsageTimeline)
        End Sub

        ''' <summary>One user, agent or model inside a line of the usage graph (for "Used by" / "Models" in its table).</summary>
        Private NotInheritable Class UsagePart
            Public Label As String
            Public Kind As String
            Public Input As Long
            Public Output As Long
            Public Calls As Long
        End Class

        ''' <summary>Users, agents or models listed per line; the rest are counted.</summary>
        Private Const MaxParts As Integer = 25

        ''' <summary>One hour of one user's (or agent's) use of one model.</summary>
        Private NotInheritable Class UsageHour
            Public Hour As DateTime
            Public Who As String
            Public Kind As String
            Public Model As String
            Public Input As Long
            Public Output As Long
            Public Calls As Long
        End Class

        ''' <summary>
        ''' Token use over time for the usage graph: ?range=24h|7d|30d &amp;by=user|model &amp;tz=(minutes, like JavaScript's
        ''' getTimezoneOffset). Chat and API calls of every user, and agents' calls (administrators see everyone; other users
        ''' their own use). The busiest 8 users or models get a line each; the rest are summed as "Other".
        ''' </summary>
        Private Async Function UsageTimeline(rc As RequestContext) As Task
            Dim range = If(rc.QueryString("range"), "24h").ToLowerInvariant()
            Dim by = If(rc.QueryString("by"), "user").ToLowerInvariant()
            If by <> "model" Then by = "user"
            Dim tz = rc.QueryInt("tz", 0, -14 * 60, 14 * 60)
            Dim bucketHours As Integer, count As Integer
            Select Case range
                Case "7d" : bucketHours = 3 : count = 56
                Case "30d" : bucketHours = 24 : count = 30
                Case Else : range = "24h" : bucketHours = 1 : count = 24
            End Select
            ' Buckets in the viewer's local time: whole hours, 3-hour blocks or days that end with the current one.
            Dim offset = TimeSpan.FromMinutes(-tz)
            Dim nowLocal = DateTime.UtcNow + offset
            Dim lastStartLocal = New DateTime(nowLocal.Year, nowLocal.Month, nowLocal.Day, nowLocal.Hour, 0, 0, DateTimeKind.Unspecified)
            lastStartLocal = lastStartLocal.AddHours(-(lastStartLocal.Hour Mod bucketHours))
            Dim firstStartLocal = lastStartLocal.AddHours(-bucketHours * (count - 1))
            Dim firstUtc = DateTime.SpecifyKind(firstStartLocal - offset, DateTimeKind.Utc)
            Dim since = Db.Ts(firstUtc)
            Dim everyone = rc.User.IsAdmin
            Dim viewer = rc.User.Name

            Dim hours As New List(Of UsageHour)
            hours.AddRange(Await _h.Database.QueryAsync(
                "SELECT substr(CreatedUtc, 1, 13) AS H, Username AS W, COALESCE(NULLIF(RoutedModel, ''), Model) AS M, SUM(PromptTokens) AS P, SUM(CompletionTokens) AS C, COUNT(*) AS N " &
                "FROM ModelApiUsage WHERE CreatedUtc>=@s" & If(everyone, "", " AND Username=@u") & " GROUP BY H, W, M",
                Function(r) New UsageHour With {.Hour = HourOf(Db.Str(r, "H")), .Who = Db.Str(r, "W"), .Kind = "user", .Model = Db.Str(r, "M"),
                                                .Input = Db.Lng(r, "P"), .Output = Db.Lng(r, "C"), .Calls = Db.Lng(r, "N")},
                ("@s", CObj(since)), ("@u", viewer)).ConfigureAwait(False))
            If everyone Then
                hours.AddRange(Await _h.Database.QueryAsync(
                    "SELECT substr(CreatedUtc, 1, 13) AS H, AgentId AS W, ModelId AS M, SUM(PromptTokens) AS P, SUM(CompletionTokens) AS C, COUNT(*) AS N " &
                    "FROM AiCalls WHERE CreatedUtc>=@s GROUP BY H, W, M",
                    Function(r) New UsageHour With {.Hour = HourOf(Db.Str(r, "H")), .Who = Db.Str(r, "W"), .Kind = "agent", .Model = Db.Str(r, "M"),
                                                    .Input = Db.Lng(r, "P"), .Output = Db.Lng(r, "C"), .Calls = Db.Lng(r, "N")},
                    ("@s", CObj(since))).ConfigureAwait(False))
            End If

            Dim keyOf = Function(h As UsageHour) If(by = "model", "m:" & h.Model, If(h.Kind = "agent", "a:", "u:") & h.Who)
            Dim labelOf = Function(key As String) As String
                              If key.StartsWith("a:", StringComparison.Ordinal) Then Return "Agent · " & key.Substring(2)
                              Return key.Substring(2)
                          End Function
            ' The busiest lines by tokens; the rest are one "Other" line.
            Dim ranked = hours.GroupBy(keyOf).Select(Function(g) (Key:=g.Key, Tokens:=g.Sum(Function(x) x.Input + x.Output))).OrderByDescending(Function(x) x.Tokens).ToList()
            Const MaxLines As Integer = 8
            Dim shown = ranked.Take(If(ranked.Count > MaxLines, MaxLines - 1, MaxLines)).Select(Function(x) x.Key).ToList()
            Dim hasOther = ranked.Count > shown.Count

            Dim bucketOf = Function(utcHour As DateTime) As Integer
                               Dim local = utcHour + offset
                               Return CInt(Math.Floor((local - firstStartLocal).TotalHours / bucketHours))
                           End Function
            Dim series As New List(Of Object)
            Dim lines = shown.Select(Function(k) (Key:=k, Label:=labelOf(k), Kind:=If(by = "model", "model", If(k.StartsWith("a:"), "agent", "user")))).ToList()
            If hasOther Then lines.Add((Key:=ChrW(0) & "other", Label:="Other", Kind:="other"))
            For Each line In lines
                Dim input(count - 1) As Long, output(count - 1) As Long
                Dim calls As Long = 0
                ' The other side of the line: who used this model, or which models this user or agent used.
                Dim parts As New Dictionary(Of String, UsagePart)(StringComparer.OrdinalIgnoreCase)
                For Each h In hours
                    Dim k = keyOf(h)
                    If line.Kind = "other" Then
                        If shown.Contains(k) Then Continue For
                    ElseIf k <> line.Key Then
                        Continue For
                    End If
                    Dim b = bucketOf(h.Hour)
                    If b < 0 OrElse b >= count Then Continue For
                    input(b) += h.Input
                    output(b) += h.Output
                    calls += h.Calls
                    Dim pk = If(by = "model", If(h.Kind = "agent", "a:", "u:") & h.Who, "m:" & h.Model)
                    Dim p As UsagePart = Nothing
                    If Not parts.TryGetValue(pk, p) Then
                        p = New UsagePart With {.Label = If(by = "model", h.Who, h.Model), .Kind = If(by = "model", h.Kind, "model")}
                        parts(pk) = p
                    End If
                    p.Input += h.Input
                    p.Output += h.Output
                    p.Calls += h.Calls
                Next
                Dim ranked2 = parts.Values.OrderByDescending(Function(x) x.Input + x.Output).ThenByDescending(Function(x) x.Calls).ToList()
                series.Add(New With {.key = line.Key, .label = line.Label, .kind = line.Kind, .input = input, .output = output, .calls = calls,
                                     .inputTotal = input.Sum(), .outputTotal = output.Sum(),
                                     .parts = ranked2.Take(MaxParts).Select(Function(x) New With {.label = x.Label, .kind = x.Kind, .input = x.Input, .output = x.Output, .calls = x.Calls}).ToList(),
                                     .moreParts = Math.Max(0, ranked2.Count - MaxParts)})
            Next
            Dim inRange = hours.Where(Function(h) bucketOf(h.Hour) >= 0 AndAlso bucketOf(h.Hour) < count).ToList()
            Dim buckets = Enumerable.Range(0, count).Select(Function(i) Db.Ts(DateTime.SpecifyKind(firstStartLocal.AddHours(i * bucketHours) - offset, DateTimeKind.Utc))).ToList()
            Await rc.OkAsync(New With {
                .range = range, .by = by, .bucketMinutes = bucketHours * 60, .buckets = buckets, .series = series,
                .scope = If(everyone, "all", "mine"),
                .totals = New With {.input = inRange.Sum(Function(h) h.Input), .output = inRange.Sum(Function(h) h.Output), .calls = inRange.Sum(Function(h) h.Calls),
                                    .users = inRange.Where(Function(h) h.Kind = "user").Select(Function(h) h.Who).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                                    .agents = inRange.Where(Function(h) h.Kind = "agent").Select(Function(h) h.Who).Distinct().Count(),
                                    .models = inRange.Select(Function(h) h.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count()},
                .updatedUtc = Db.Ts(DateTime.UtcNow)}).ConfigureAwait(False)
        End Function

        ''' <summary>"2026-10-20T05" (the first 13 characters of a stored time) as that hour in UTC.</summary>
        Private Shared Function HourOf(text As String) As DateTime
            Dim d As DateTime
            If DateTime.TryParseExact(If(text, "") & ":00:00Z", "yyyy-MM-ddTHH:mm:ssZ", Globalization.CultureInfo.InvariantCulture,
                                      Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, d) Then Return DateTime.SpecifyKind(d, DateTimeKind.Utc)
            Return DateTime.MinValue
        End Function

    End Class

End Namespace
