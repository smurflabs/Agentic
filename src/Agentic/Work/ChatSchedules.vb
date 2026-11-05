Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' When a scheduled prompt runs: every N minutes, daily or on weekdays at a time, at a time once, or once at a date and
    ''' time. Times are in the user's time zone (its id from the browser or the CLI), else UTC.
    ''' </summary>
    Public NotInheritable Class ScheduleSpec
        Public Const MinEveryMinutes As Integer = 5
        Public Property Kind As String
        Public Property EveryMinutes As Integer
        Public Property TimeOfDay As TimeSpan
        ''' <summary>once: the moment (UTC).</summary>
        Public Property OnceUtc As DateTime
        Public Property Zone As TimeZoneInfo = TimeZoneInfo.Utc
        ''' <summary>The normalized "when": "every 1h", "daily 09:00", "weekdays 08:30", "once 2026-11-01 09:00".</summary>
        Public Property Text As String

        ''' <summary>
        ''' Parses "every 30m | 2h | 1d" (also "hourly"), "daily 09:00", "weekdays 08:30", "at 14:00" (once, the next time it is
        ''' 14:00) and "once 2026-11-01 09:00". Nothing, with why, when it is none of these.
        ''' </summary>
        Public Shared Function Parse(text As String, timeZone As String, nowUtc As DateTime, ByRef problem As String) As ScheduleSpec
            problem = Nothing
            Dim t = Regex.Replace(If(text, "").Trim().ToLowerInvariant(), "\s+", " ")
            Dim zone = FindZone(timeZone)
            Dim m = Regex.Match(t, "^(?:every (\d+) ?(m|min|mins|minute|minutes|h|hr|hrs|hour|hours|d|day|days)|(hourly))$")
            If m.Success Then
                Dim minutes = If(m.Groups(3).Success, 60, Integer.Parse(m.Groups(1).Value, CultureInfo.InvariantCulture) * (If(m.Groups(2).Value.StartsWith("h"), 60, If(m.Groups(2).Value.StartsWith("d"), 1440, 1))))
                If minutes < MinEveryMinutes Then
                    problem = $"Every {MinEveryMinutes} minutes at most."
                    Return Nothing
                End If
                If minutes > 31 * 1440 Then
                    problem = "At least once a month."
                    Return Nothing
                End If
                Return New ScheduleSpec With {.Kind = "every", .EveryMinutes = minutes, .Zone = zone, .Text = "every " & EveryText(minutes)}
            End If
            m = Regex.Match(t, "^(daily|weekdays|at) (\d{1,2}):(\d{2})$")
            If m.Success Then
                Dim tod As TimeSpan
                If Not TryTime(m.Groups(2).Value, m.Groups(3).Value, tod) Then
                    problem = "Give the time as HH:MM (24-hour clock)."
                    Return Nothing
                End If
                Dim kind = If(m.Groups(1).Value = "at", "once", m.Groups(1).Value)
                Dim spec As New ScheduleSpec With {.Kind = kind, .TimeOfDay = tod, .Zone = zone}
                If kind = "once" Then
                    ' "at 14:00": the next time it is 14:00 there.
                    spec.OnceUtc = NextLocal(nowUtc, tod, zone, weekdaysOnly:=False)
                    spec.Text = "once " & LocalText(spec.OnceUtc, zone)
                Else
                    spec.Text = $"{kind} {tod:hh\:mm}"
                End If
                Return spec
            End If
            m = Regex.Match(t, "^once (\d{4})-(\d{2})-(\d{2}) (\d{1,2}):(\d{2})$")
            If m.Success Then
                Dim tod As TimeSpan
                Dim day As DateTime
                If Not TryTime(m.Groups(4).Value, m.Groups(5).Value, tod) OrElse
                   Not DateTime.TryParseExact($"{m.Groups(1).Value}-{m.Groups(2).Value}-{m.Groups(3).Value}", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, day) Then
                    problem = "Give the date as YYYY-MM-DD and the time as HH:MM."
                    Return Nothing
                End If
                Dim utc = ToUtc(day.Date.Add(tod), zone)
                If utc <= nowUtc Then
                    problem = "That time has passed."
                    Return Nothing
                End If
                Return New ScheduleSpec With {.Kind = "once", .OnceUtc = utc, .TimeOfDay = tod, .Zone = zone, .Text = "once " & LocalText(utc, zone)}
            End If
            problem = "Say when: every 30m, every 2h, hourly, daily 09:00, weekdays 08:30, at 14:00, or once 2026-11-01 09:00."
            Return Nothing
        End Function

        ''' <summary>The next run after this moment, or Nothing when there is none (a one-time prompt that has run).</summary>
        Public Function NextAfter(afterUtc As DateTime) As DateTime?
            Select Case Kind
                Case "every" : Return afterUtc.AddMinutes(EveryMinutes)
                Case "daily" : Return NextLocal(afterUtc, TimeOfDay, Zone, weekdaysOnly:=False)
                Case "weekdays" : Return NextLocal(afterUtc, TimeOfDay, Zone, weekdaysOnly:=True)
                Case Else : Return If(OnceUtc > afterUtc, OnceUtc, CType(Nothing, DateTime?))
            End Select
        End Function

        ''' <summary>The first run: the next time it is due from now ("every" starts one interval from now).</summary>
        Public Function FirstRun(nowUtc As DateTime) As DateTime
            Return If(NextAfter(nowUtc), nowUtc)
        End Function

        Private Shared Function EveryText(minutes As Integer) As String
            If minutes Mod 1440 = 0 Then Return $"{minutes \ 1440}d"
            If minutes Mod 60 = 0 Then Return $"{minutes \ 60}h"
            Return $"{minutes}m"
        End Function

        Private Shared Function TryTime(h As String, m As String, ByRef tod As TimeSpan) As Boolean
            Dim hh = Integer.Parse(h, CultureInfo.InvariantCulture), mm = Integer.Parse(m, CultureInfo.InvariantCulture)
            If hh > 23 OrElse mm > 59 Then Return False
            tod = New TimeSpan(hh, mm, 0)
            Return True
        End Function

        ''' <summary>
        ''' The time zone by its IANA or Windows id; UTC when it is unknown or not given. On Windows (invariant globalization) an
        ''' IANA id is found through its Windows id (IanaZones).
        ''' </summary>
        Public Shared Function FindZone(id As String) As TimeZoneInfo
            If String.IsNullOrWhiteSpace(id) Then Return TimeZoneInfo.Utc
            For Each candidate In {id.Trim(), IanaZones.ToWindows(id)}
                If String.IsNullOrEmpty(candidate) Then Continue For
                Try
                    Return TimeZoneInfo.FindSystemTimeZoneById(candidate)
                Catch ex As Exception When TypeOf ex Is TimeZoneNotFoundException OrElse TypeOf ex Is InvalidTimeZoneException OrElse TypeOf ex Is ArgumentException
                End Try
            Next
            Return TimeZoneInfo.Utc
        End Function

        ''' <summary>The next moment after afterUtc when it is tod in the zone (Monday to Friday only, when asked).</summary>
        Private Shared Function NextLocal(afterUtc As DateTime, tod As TimeSpan, zone As TimeZoneInfo, weekdaysOnly As Boolean) As DateTime
            Dim local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone)
            Dim day = local.Date
            For i = 0 To 8
                Dim candidate = day.AddDays(i).Add(tod)
                If weekdaysOnly AndAlso (candidate.DayOfWeek = DayOfWeek.Saturday OrElse candidate.DayOfWeek = DayOfWeek.Sunday) Then Continue For
                Dim utc = ToUtc(candidate, zone)
                If utc > afterUtc Then Return utc
            Next
            Return afterUtc.AddDays(1)
        End Function

        ''' <summary>A local time in the zone as UTC; a time skipped by a clock change moves on by an hour.</summary>
        Private Shared Function ToUtc(local As DateTime, zone As TimeZoneInfo) As DateTime
            Dim unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified)
            If zone.IsInvalidTime(unspecified) Then unspecified = unspecified.AddHours(1)
            Return TimeZoneInfo.ConvertTimeToUtc(unspecified, zone)
        End Function

        Private Shared Function LocalText(utc As DateTime, zone As TimeZoneInfo) As String
            Return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
        End Function
    End Class

    ''' <summary>A scheduled prompt (/schedule).</summary>
    Public NotInheritable Class ChatSchedule
        Public Property Id As String
        Public Property Username As String
        Public Property ChatId As String
        Public Property Prompt As String
        Public Property [When] As String
        Public Property TimeZone As String
        Public Property NextRunUtc As DateTime
        Public Property LastRunUtc As DateTime?
        Public Property LastStatus As String
        Public Property Runs As Integer
        Public Property Enabled As Boolean
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>Scheduled prompts, stored in ChatSchedules.</summary>
    Public NotInheritable Class ChatScheduleStore
        Public Const MaxPerUser As Integer = 25
        ''' <summary>How a heartbeat's prompt starts (/heartbeat in the Chat tab: a checklist looked at on a schedule).</summary>
        Public Const HeartbeatPrefix As String = "Heartbeat check."
        ''' <summary>What a heartbeat answers when nothing needs attention (the CLI's /heartbeat says the same).</summary>
        Public Const HeartbeatOk As String = "HEARTBEAT_OK"
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Public Shared Function IsHeartbeat(prompt As String) As Boolean
            Return If(prompt, "").TrimStart().StartsWith(HeartbeatPrefix, StringComparison.Ordinal)
        End Function

        ''' <summary>An answer that is only HEARTBEAT_OK (give or take punctuation, a code span or bold).</summary>
        Public Shared Function IsHeartbeatOk(answer As String) As Boolean
            Return Text.RegularExpressions.Regex.IsMatch(If(answer, ""), "^[\s`*_.!""']*" & HeartbeatOk & "[\s`*_.!""']*$")
        End Function

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ChatSchedule
            Return New ChatSchedule With {
                .Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .ChatId = Db.Str(r, "ChatId"), .Prompt = Db.Str(r, "Prompt"),
                .When = Db.Str(r, "WhenText"), .TimeZone = Db.Str(r, "TimeZone"), .NextRunUtc = Db.Dt(r, "NextRunUtc"), .LastRunUtc = Db.DtN(r, "LastRunUtc"),
                .LastStatus = Db.Str(r, "LastStatus"), .Runs = Db.Int(r, "Runs"), .Enabled = Db.Bool(r, "Enabled"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}
        End Function

        Public Function ListAsync(username As String, Optional chatId As String = Nothing) As Task(Of List(Of ChatSchedule))
            Return _db.QueryAsync("SELECT * FROM ChatSchedules WHERE Username=@u AND (@c IS NULL OR ChatId=@c) ORDER BY CreatedUtc", AddressOf Map,
                                  ("@u", CObj(username)), ("@c", Db.Val(chatId)))
        End Function

        Public Function GetAsync(username As String, id As String) As Task(Of ChatSchedule)
            Return _db.QuerySingleAsync("SELECT * FROM ChatSchedules WHERE Id=@id AND Username=@u", AddressOf Map, ("@id", CObj(id)), ("@u", CObj(username)))
        End Function

        Public Async Function CountAsync(username As String) As Task(Of Long)
            Return Await _db.ScalarLongAsync("SELECT COUNT(*) FROM ChatSchedules WHERE Username=@u", ("@u", CObj(username))).ConfigureAwait(False)
        End Function

        Public Function AddAsync(s As ChatSchedule) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO ChatSchedules(Id, Username, ChatId, Prompt, WhenText, TimeZone, NextRunUtc, Runs, Enabled, CreatedUtc) " &
                                    "VALUES(@id, @u, @c, @p, @w, @tz, @n, 0, 1, @cr)",
                                    ("@id", CObj(s.Id)), ("@u", s.Username), ("@c", s.ChatId), ("@p", s.Prompt), ("@w", s.When), ("@tz", Db.Val(s.TimeZone)),
                                    ("@n", Db.Ts(s.NextRunUtc)), ("@cr", Db.Ts(s.CreatedUtc)))
        End Function

        Public Function SetEnabledAsync(username As String, id As String, enabled As Boolean, nextRunUtc As DateTime?) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE ChatSchedules SET Enabled=@e, NextRunUtc=COALESCE(@n, NextRunUtc) WHERE Id=@id AND Username=@u",
                                    ("@e", CObj(If(enabled, 1, 0))), ("@n", Db.Ts(nextRunUtc)), ("@id", id), ("@u", username))
        End Function

        Public Async Function DeleteAsync(username As String, id As String) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("DELETE FROM ChatSchedules WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", CObj(username))).ConfigureAwait(False) > 0
        End Function

        Public Function DueAsync(nowUtc As DateTime, Optional limit As Integer = 20) As Task(Of List(Of ChatSchedule))
            Return _db.QueryAsync($"SELECT * FROM ChatSchedules WHERE Enabled=1 AND NextRunUtc<=@n ORDER BY NextRunUtc LIMIT {Math.Clamp(limit, 1, 200)}", AddressOf Map,
                                  ("@n", CObj(Db.Ts(nowUtc))))
        End Function

        ''' <summary>A run starts: counted, and the next one set first (Nothing: a one-time prompt, which is then switched off).</summary>
        Public Function MarkRunAsync(id As String, startedUtc As DateTime, nextRunUtc As DateTime?) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE ChatSchedules SET Runs=Runs+1, LastRunUtc=@s, NextRunUtc=COALESCE(@n, NextRunUtc), Enabled=CASE WHEN @n IS NULL THEN 0 ELSE Enabled END, " &
                                    "LastStatus='running' WHERE Id=@id", ("@s", CObj(Db.Ts(startedUtc))), ("@n", Db.Ts(nextRunUtc)), ("@id", id))
        End Function

        Public Function SetStatusAsync(id As String, status As String, Optional nextRunUtc As DateTime? = Nothing, Optional disable As Boolean = False) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE ChatSchedules SET LastStatus=@st, NextRunUtc=COALESCE(@n, NextRunUtc), Enabled=CASE WHEN @d=1 THEN 0 ELSE Enabled END WHERE Id=@id",
                                    ("@st", CObj(TextUtil.Truncate(status, 300))), ("@n", Db.Ts(nextRunUtc)), ("@d", CObj(If(disable, 1, 0))), ("@id", id))
        End Function
    End Class

End Namespace
