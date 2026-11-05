Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Text.Json
Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' Turns the dashboard's "Repeat" settings into a schedule (interval or cron in the server's time zone) with a readable
    ''' summary. Spec: {"mode": "interval|daily|weekly|monthly|cron", "every": 2, "unit": "minutes|hours|days",
    ''' "time": "09:30", "days": ["mon","fri"], "day": 15, "cron": "...", "endUtc": "...", "maxRuns": 10, "skipIfRunning": true}.
    ''' </summary>
    Public NotInheritable Class Recurrence
        Private Shared ReadOnly DayNames As String() = {"sun", "mon", "tue", "wed", "thu", "fri", "sat"}

        ''' <summary>Fills the recurrence fields of a schedule; throws ArgumentException with a user-facing message when invalid.</summary>
        Public Shared Sub Apply(spec As JsonElement, s As ScheduleOptions)
            If spec.ValueKind <> JsonValueKind.Object Then Throw New ArgumentException("repeat must be an object.")
            Dim mode = If(JsonUtil.GetString(spec, "mode"), "").Trim().ToLowerInvariant()
            s.TimeZone = "Local"
            s.IntervalSeconds = Nothing
            s.Cron = Nothing
            Select Case mode
                Case "interval"
                    Dim every = JsonUtil.GetInt(spec, "every", 0)
                    Dim unit = If(JsonUtil.GetString(spec, "unit"), "hours").Trim().ToLowerInvariant()
                    Dim factor As Integer
                    Select Case unit
                        Case "minute", "minutes" : factor = 60
                        Case "hour", "hours" : factor = 3600
                        Case "day", "days" : factor = 86400
                        Case "week", "weeks" : factor = 604800
                        Case Else : Throw New ArgumentException("Repeat unit must be minutes, hours, days or weeks.")
                    End Select
                    If every < 1 Then Throw New ArgumentException("Repeat every: enter a number of at least 1.")
                    Dim seconds = CLng(every) * factor
                    If seconds < 60 Then Throw New ArgumentException("Tasks can repeat at most once a minute.")
                    If seconds > 366L * 86400 Then Throw New ArgumentException("The repeat interval is too long (at most a year).")
                    s.IntervalSeconds = CInt(seconds)
                    Dim unitName = If(factor = 60, "minute", If(factor = 86400, "day", If(factor = 604800, "week", "hour")))
                    s.Summary = If(every = 1, $"Every {unitName}", $"Every {every} {unitName}s")
                Case "daily", "weekly", "monthly"
                    Dim time = ParseTime(JsonUtil.GetString(spec, "time"))
                    If mode = "daily" Then
                        s.Cron = $"{time.Minute} {time.Hour} * * *"
                        s.Summary = $"Daily at {time.Text}"
                    ElseIf mode = "weekly" Then
                        Dim days = JsonUtil.GetStringList(spec, "days").Select(Function(d) Array.IndexOf(DayNames, d.Trim().ToLowerInvariant().Substring(0, Math.Min(3, d.Trim().Length)))).
                            Where(Function(i) i >= 0).Distinct().OrderBy(Function(i) i).ToList()
                        If days.Count = 0 Then Throw New ArgumentException("Weekly: choose at least one day.")
                        s.Cron = $"{time.Minute} {time.Hour} * * {String.Join(",", days)}"
                        s.Summary = $"Weekly on {String.Join(", ", days.Select(Function(i) CultureInfo.InvariantCulture.DateTimeFormat.AbbreviatedDayNames(i)))} at {time.Text}"
                    Else
                        Dim day = JsonUtil.GetInt(spec, "day", 1)
                        If day < 1 OrElse day > 28 Then Throw New ArgumentException("Monthly: the day must be 1-28 (so it exists in every month).")
                        s.Cron = $"{time.Minute} {time.Hour} {day} * *"
                        s.Summary = $"Monthly on day {day} at {time.Text}"
                    End If
                Case "cron"
                    Dim cron = If(JsonUtil.GetString(spec, "cron"), "").Trim()
                    Try
                        Dim fields = cron.Split(" "c, StringSplitOptions.RemoveEmptyEntries).Length
                        Cronos.CronExpression.Parse(cron, If(fields = 6, Cronos.CronFormat.IncludeSeconds, Cronos.CronFormat.Standard))
                    Catch ex As Exception
                        Throw New ArgumentException("Cron expression is invalid: " & ex.Message)
                    End Try
                    s.Cron = cron
                    s.Summary = $"Cron {cron}"
                Case Else
                    Throw New ArgumentException("repeat.mode must be interval, daily, weekly, monthly or cron.")
            End Select
            Dim endText = JsonUtil.GetString(spec, "endUtc")
            Dim endValue As DateTime
            If Not String.IsNullOrWhiteSpace(endText) Then
                If Not DateTime.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, endValue) Then Throw New ArgumentException("End date is invalid.")
                s.EndUtc = endValue
                s.Summary &= $", until {endValue.ToLocalTime():yyyy-MM-dd HH:mm}"
            End If
            Dim maxRuns = JsonUtil.GetInt(spec, "maxRuns", 0)
            If maxRuns > 0 Then s.MaxRuns = maxRuns : s.Summary &= $", {maxRuns} run{If(maxRuns = 1, "", "s")} max"
            Dim skip As JsonElement
            If spec.TryGetProperty("skipIfRunning", skip) AndAlso (skip.ValueKind = JsonValueKind.True OrElse skip.ValueKind = JsonValueKind.False) Then s.SkipIfRunning = skip.GetBoolean()
            If s.Cron IsNot Nothing Then s.Summary &= " (server time)"
        End Sub

        Private Shared Function ParseTime(text As String) As (Hour As Integer, Minute As Integer, Text As String)
            Dim parts = If(text, "").Trim().Split(":"c)
            Dim h, m As Integer
            If parts.Length <> 2 OrElse Not Integer.TryParse(parts(0), h) OrElse Not Integer.TryParse(parts(1), m) OrElse h < 0 OrElse h > 23 OrElse m < 0 OrElse m > 59 Then
                Throw New ArgumentException("Time must be HH:mm, e.g. 09:30.")
            End If
            Return (h, m, $"{h:00}:{m:00}")
        End Function

        ''' <summary>Readable summary of any schedule (dashboard ones carry their own; config ones are described here).</summary>
        Public Shared Function Describe(s As ScheduleOptions) As String
            If Not String.IsNullOrEmpty(s.Summary) Then Return s.Summary
            If s.IntervalSeconds.HasValue Then
                Dim sec = s.IntervalSeconds.Value
                Return If(sec Mod 86400 = 0, $"Every {sec \ 86400} day(s)", If(sec Mod 3600 = 0, $"Every {sec \ 3600} hour(s)", If(sec Mod 60 = 0, $"Every {sec \ 60} minute(s)", $"Every {sec} seconds")))
            End If
            Return If(String.IsNullOrEmpty(s.Cron), "Not scheduled", $"Cron {s.Cron} ({If(String.Equals(s.TimeZone, "Local", StringComparison.OrdinalIgnoreCase), "server time", "UTC")})")
        End Function
    End Class

End Namespace
