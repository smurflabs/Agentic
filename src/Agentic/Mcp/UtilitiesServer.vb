Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "utilities" MCP server ("Command": "{self}", "Arguments": ["--mcp-utilities"]): the small exact things models get
    ''' wrong in their heads - the current time in any time zone, date arithmetic (business days too), exact decimal math, unit
    ''' conversion, hashes, encodings (base64, URL, hex, HTML, JWT payloads), UUIDs and regular-expression tests. No network, no files.
    ''' </summary>
    Partial Public NotInheritable Class UtilitiesServer
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Public Shared Function RunStdio(args As String()) As Integer
            Return BuiltInServer.Run("agentic-utilities", New UtilitiesServer().CreateTools())
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf CurrentTime), "current_time",
                "The current date and time (UTC and the given time zones: IANA like Europe/Berlin, Windows names, abbreviations like PST, or offsets like +05:30).")
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf ConvertTime), "convert_time",
                "Convert a date/time from one time zone to others (daylight saving included).")
            ToolKit.Add(tools, New Func(Of String, Integer, Integer, Integer, Integer, Integer, Integer, String)(AddressOf DateAdd), "date_add",
                "Add (or subtract, with negative numbers) years, months, days, business days (Mon-Fri), hours and minutes to a date.")
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf DateDiff), "date_diff",
                "The time between two dates: days, weeks, hours, business days (Mon-Fri) and calendar months.")
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf Calculate), "calculate",
                "Evaluate a math expression exactly (decimal arithmetic, no floating-point drift): + - * / ^, 'mod', parentheses, 15% (= 0.15), n!, " &
                "sqrt, cbrt, abs, round(x, digits), floor, ceil, trunc, min, max, sum, avg, pow, exp, ln, log(x, base), log10, log2, sin, cos, tan (radians), " &
                "asin, acos, atan, deg, rad, pi, e. Use it for any arithmetic that matters (money, totals, percentages).")
            ToolKit.Add(tools, New Func(Of Double, String, String, String)(AddressOf ConvertUnits), "convert_units",
                "Convert a value between units: length, mass, time, data sizes (KB/KiB), volume, area, speed, temperature, energy, pressure.")
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf HashText), "hash_text",
                "Hash text (UTF-8): md5, sha1, sha256 (default), sha384, sha512; with a key: HMAC of that algorithm. Hex and base64.")
            ToolKit.Add(tools, New Func(Of String, String, Boolean, String)(AddressOf EncodeText), "encode_text",
                "Encode or decode text: base64, base64url, url, hex, html; jwt decodes a token's header and payload (it does not check the signature).")
            ToolKit.Add(tools, New Func(Of Integer, Integer, String)(AddressOf NewUuids), "new_uuid", "New random UUIDs (version 4, or 7 for time-ordered ids).")
            ToolKit.Add(tools, New Func(Of String, String, String, String, String)(AddressOf RegexTest), "regex_test",
                "Test a .NET regular expression on text: the matches with their groups, or the text after a replacement ($1, ${name}). flags: i m s x.")
            AddMoreTools(tools)
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ time

        Private Shared ReadOnly ZoneAliases As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"America/New_York", "Eastern Standard Time"}, {"America/Toronto", "Eastern Standard Time"}, {"America/Detroit", "Eastern Standard Time"},
            {"America/Chicago", "Central Standard Time"}, {"America/Denver", "Mountain Standard Time"}, {"America/Phoenix", "US Mountain Standard Time"},
            {"America/Los_Angeles", "Pacific Standard Time"}, {"America/Vancouver", "Pacific Standard Time"}, {"America/Anchorage", "Alaskan Standard Time"},
            {"Pacific/Honolulu", "Hawaiian Standard Time"}, {"America/Mexico_City", "Central Standard Time (Mexico)"}, {"America/Sao_Paulo", "E. South America Standard Time"},
            {"America/Argentina/Buenos_Aires", "Argentina Standard Time"}, {"America/Bogota", "SA Pacific Standard Time"}, {"America/Halifax", "Atlantic Standard Time"},
            {"Europe/London", "GMT Standard Time"}, {"Europe/Dublin", "GMT Standard Time"}, {"Europe/Lisbon", "GMT Standard Time"},
            {"Europe/Paris", "Romance Standard Time"}, {"Europe/Madrid", "Romance Standard Time"}, {"Europe/Brussels", "Romance Standard Time"},
            {"Europe/Berlin", "W. Europe Standard Time"}, {"Europe/Amsterdam", "W. Europe Standard Time"}, {"Europe/Rome", "W. Europe Standard Time"},
            {"Europe/Stockholm", "W. Europe Standard Time"}, {"Europe/Zurich", "W. Europe Standard Time"}, {"Europe/Vienna", "W. Europe Standard Time"},
            {"Europe/Oslo", "W. Europe Standard Time"}, {"Europe/Warsaw", "Central European Standard Time"}, {"Europe/Prague", "Central Europe Standard Time"},
            {"Europe/Budapest", "Central Europe Standard Time"}, {"Europe/Athens", "GTB Standard Time"}, {"Europe/Bucharest", "GTB Standard Time"},
            {"Europe/Helsinki", "FLE Standard Time"}, {"Europe/Kyiv", "FLE Standard Time"}, {"Europe/Kiev", "FLE Standard Time"},
            {"Europe/Istanbul", "Turkey Standard Time"}, {"Europe/Moscow", "Russian Standard Time"}, {"Asia/Dubai", "Arabian Standard Time"},
            {"Asia/Riyadh", "Arab Standard Time"}, {"Asia/Tehran", "Iran Standard Time"}, {"Asia/Jerusalem", "Israel Standard Time"},
            {"Asia/Karachi", "Pakistan Standard Time"}, {"Asia/Kolkata", "India Standard Time"}, {"Asia/Calcutta", "India Standard Time"},
            {"Asia/Dhaka", "Bangladesh Standard Time"}, {"Asia/Bangkok", "SE Asia Standard Time"}, {"Asia/Jakarta", "SE Asia Standard Time"},
            {"Asia/Ho_Chi_Minh", "SE Asia Standard Time"}, {"Asia/Singapore", "Singapore Standard Time"}, {"Asia/Kuala_Lumpur", "Singapore Standard Time"},
            {"Asia/Manila", "Singapore Standard Time"}, {"Asia/Hong_Kong", "China Standard Time"}, {"Asia/Shanghai", "China Standard Time"},
            {"Asia/Taipei", "Taipei Standard Time"}, {"Asia/Tokyo", "Tokyo Standard Time"}, {"Asia/Seoul", "Korea Standard Time"},
            {"Australia/Sydney", "AUS Eastern Standard Time"}, {"Australia/Melbourne", "AUS Eastern Standard Time"}, {"Australia/Brisbane", "E. Australia Standard Time"},
            {"Australia/Adelaide", "Cen. Australia Standard Time"}, {"Australia/Perth", "W. Australia Standard Time"}, {"Pacific/Auckland", "New Zealand Standard Time"},
            {"Africa/Johannesburg", "South Africa Standard Time"}, {"Africa/Cairo", "Egypt Standard Time"}, {"Africa/Lagos", "W. Central Africa Standard Time"},
            {"Africa/Nairobi", "E. Africa Standard Time"},
            {"EST", "Eastern Standard Time"}, {"EDT", "Eastern Standard Time"}, {"ET", "Eastern Standard Time"},
            {"CST", "Central Standard Time"}, {"CDT", "Central Standard Time"}, {"CT", "Central Standard Time"},
            {"MST", "Mountain Standard Time"}, {"MDT", "Mountain Standard Time"}, {"MT", "Mountain Standard Time"},
            {"PST", "Pacific Standard Time"}, {"PDT", "Pacific Standard Time"}, {"PT", "Pacific Standard Time"},
            {"AKST", "Alaskan Standard Time"}, {"HST", "Hawaiian Standard Time"}, {"BST", "GMT Standard Time"},
            {"CET", "W. Europe Standard Time"}, {"CEST", "W. Europe Standard Time"}, {"EET", "FLE Standard Time"}, {"EEST", "FLE Standard Time"},
            {"MSK", "Russian Standard Time"}, {"IST", "India Standard Time"}, {"SGT", "Singapore Standard Time"}, {"HKT", "China Standard Time"},
            {"JST", "Tokyo Standard Time"}, {"KST", "Korea Standard Time"}, {"AEST", "AUS Eastern Standard Time"}, {"AEDT", "AUS Eastern Standard Time"},
            {"NZST", "New Zealand Standard Time"}, {"NZDT", "New Zealand Standard Time"}}

        Private Shared ReadOnly OffsetZone As New Regex("^(?:UTC|GMT)?\s*([+-])(\d{1,2})(?::?(\d{2}))?$", RegexOptions.IgnoreCase Or RegexOptions.Compiled)

        ''' <summary>A time zone from an IANA id, a Windows id, an abbreviation or a fixed offset; an McpException when unknown.</summary>
        Friend Shared Function FindZone(id As String) As TimeZoneInfo
            Dim z = If(id, "").Trim()
            If z.Length = 0 OrElse z.Equals("UTC", StringComparison.OrdinalIgnoreCase) OrElse z.Equals("Z", StringComparison.OrdinalIgnoreCase) OrElse
               z.Equals("GMT", StringComparison.OrdinalIgnoreCase) OrElse z.Equals("Etc/UTC", StringComparison.OrdinalIgnoreCase) Then Return TimeZoneInfo.Utc
            Dim m = OffsetZone.Match(z)
            If m.Success Then
                Dim minutes = Integer.Parse(m.Groups(2).Value, Inv) * 60 + If(m.Groups(3).Success, Integer.Parse(m.Groups(3).Value, Inv), 0)
                If minutes > 14 * 60 Then Throw New McpException($"'{id}' is not a valid offset.")
                Dim offset = TimeSpan.FromMinutes(If(m.Groups(1).Value = "-", -minutes, minutes))
                Dim label = "UTC" & If(offset < TimeSpan.Zero, "-", "+") & offset.Duration().ToString("hh\:mm", Inv)
                Return TimeZoneInfo.CreateCustomTimeZone(label, offset, label, label)
            End If
            Dim winName As String = Nothing
            If ZoneAliases.TryGetValue(z, winName) Then
                Try
                    Return TimeZoneInfo.FindSystemTimeZoneById(winName)
                Catch ex As TimeZoneNotFoundException
                End Try
            End If
            Try
                Return TimeZoneInfo.FindSystemTimeZoneById(z)
            Catch ex As Exception When TypeOf ex Is TimeZoneNotFoundException OrElse TypeOf ex Is InvalidTimeZoneException
            End Try
            Dim win As String = Nothing
            Try
                If TimeZoneInfo.TryConvertIanaIdToWindowsId(z, win) Then Return TimeZoneInfo.FindSystemTimeZoneById(win)
            Catch ex As Exception When TypeOf ex Is TimeZoneNotFoundException OrElse TypeOf ex Is InvalidTimeZoneException OrElse TypeOf ex Is PlatformNotSupportedException
            End Try
            Throw New McpException($"Unknown time zone '{id}'. Use an IANA name (Europe/Berlin), a Windows name (Pacific Standard Time), an abbreviation (PST) or an offset (+05:30).")
        End Function

        Private Shared Function Describe(t As DateTimeOffset, zone As TimeZoneInfo, label As String) As String
            Dim local = TimeZoneInfo.ConvertTime(t, zone)
            Dim off = local.Offset
            Dim offText = "UTC" & If(off < TimeSpan.Zero, "-", "+") & off.Duration().ToString("hh\:mm", Inv)
            Dim dst = If(zone.IsDaylightSavingTime(local), ", daylight saving", "")
            Return $"{label}: {local.ToString("yyyy-MM-dd HH:mm:ss", Inv)} {local.DayOfWeek} ({offText}{dst})"
        End Function

        ''' <summary>A date/time from text ("now", ISO 8601, "2026-03-01 14:00"); text without an offset is a time in the given zone.</summary>
        Friend Shared Function ParseMoment(text As String, zone As TimeZoneInfo) As DateTimeOffset
            Dim s = If(text, "").Trim()
            If s.Length = 0 OrElse s.Equals("now", StringComparison.OrdinalIgnoreCase) Then Return DateTimeOffset.UtcNow
            If s.Equals("today", StringComparison.OrdinalIgnoreCase) Then
                Dim today = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date
                Return New DateTimeOffset(today, zone.GetUtcOffset(today))
            End If
            Dim hasOffset = Regex.IsMatch(s, "(Z|[+-]\d{2}:?\d{2})$", RegexOptions.IgnoreCase)
            If hasOffset Then
                Dim dto As DateTimeOffset
                If DateTimeOffset.TryParse(s, Inv, DateTimeStyles.AllowWhiteSpaces, dto) Then Return dto
            End If
            Dim dt As DateTime
            If Not DateTime.TryParse(s, Inv, DateTimeStyles.AllowWhiteSpaces, dt) Then Throw New McpException($"Could not read the date '{text}'. Use ISO 8601, e.g. 2026-03-01T14:00 or 2026-03-01.")
            dt = DateTime.SpecifyKind(dt, DateTimeKind.Unspecified)
            If zone.IsInvalidTime(dt) Then Throw New McpException($"{dt:yyyy-MM-dd HH:mm} does not exist in {zone.Id} (the clocks skip it).")
            Return New DateTimeOffset(dt, zone.GetUtcOffset(dt))
        End Function

        Friend Function CurrentTime(<Description("Time zones, comma-separated (optional), e.g. 'America/New_York, Europe/London, +05:30'")> Optional time_zones As String = "") As String
            Dim t = DateTimeOffset.UtcNow
            Dim sb As New StringBuilder()
            sb.AppendLine($"UTC: {t.ToString("yyyy-MM-dd HH:mm:ss", Inv)} {t.DayOfWeek} (ISO {t.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)}, Unix {t.ToUnixTimeSeconds()}, ISO week {ISOWeek.GetWeekOfYear(t.UtcDateTime)})")
            For Each z In ToolKit.SplitList(time_zones)
                sb.AppendLine(Describe(t, FindZone(z), z))
            Next
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function ConvertTime(<Description("Date/time, e.g. '2026-03-01 14:00' or 'now'")> time As String,
                                    <Description("Zone the time is in (ignored when the time has an offset)")> from_zone As String,
                                    <Description("Zones to convert to, comma-separated")> to_zones As String) As String
            Dim src = FindZone(from_zone)
            Dim t = ParseMoment(time, src)
            Dim sb As New StringBuilder()
            sb.AppendLine(Describe(t, src, If(String.IsNullOrWhiteSpace(from_zone), "UTC", from_zone.Trim())))
            Dim targets = ToolKit.SplitList(to_zones)
            If targets.Count = 0 Then targets.Add("UTC")
            For Each z In targets
                sb.AppendLine(Describe(t, FindZone(z), z))
            Next
            Return sb.ToString().TrimEnd()
        End Function

        Friend Shared Function AddBusinessDays(d As DateTime, n As Integer) As DateTime
            Dim stepDir = Math.Sign(n)
            Dim left = Math.Abs(n)
            While left > 0
                d = d.AddDays(stepDir)
                If d.DayOfWeek <> DayOfWeek.Saturday AndAlso d.DayOfWeek <> DayOfWeek.Sunday Then left -= 1
            End While
            Return d
        End Function

        Friend Shared Function BusinessDaysBetween(a As DateTime, b As DateTime) As Integer
            Dim sign = 1
            If b < a Then
                Dim t = a : a = b : b = t : sign = -1
            End If
            Dim days = CInt((b.Date - a.Date).TotalDays)
            Dim weeks = days \ 7
            Dim count = weeks * 5
            Dim d = a.Date.AddDays(weeks * 7)
            While d < b.Date
                d = d.AddDays(1)
                If d.DayOfWeek <> DayOfWeek.Saturday AndAlso d.DayOfWeek <> DayOfWeek.Sunday Then count += 1
            End While
            Return sign * count
        End Function

        Friend Function DateAdd(<Description("Start date/time (ISO 8601) or 'now'/'today'")> start As String,
                                <Description("Years to add")> Optional years As Integer = 0,
                                <Description("Months to add")> Optional months As Integer = 0,
                                <Description("Days to add")> Optional days As Integer = 0,
                                <Description("Business days (Mon-Fri) to add")> Optional business_days As Integer = 0,
                                <Description("Hours to add")> Optional hours As Integer = 0,
                                <Description("Minutes to add")> Optional minutes As Integer = 0) As String
            Dim t = ParseMoment(start, TimeZoneInfo.Utc)
            Dim d = t.DateTime.AddYears(years).AddMonths(months).AddDays(days)
            If business_days <> 0 Then d = AddBusinessDays(d, business_days)
            d = d.AddHours(hours).AddMinutes(minutes)
            Dim r = New DateTimeOffset(d, t.Offset)
            Dim hasTime = r.TimeOfDay <> TimeSpan.Zero OrElse hours <> 0 OrElse minutes <> 0
            Return $"{If(hasTime, r.ToString("yyyy-MM-dd HH:mm:ss zzz", Inv), r.ToString("yyyy-MM-dd", Inv))} ({r.DayOfWeek}, ISO week {ISOWeek.GetWeekOfYear(r.DateTime)})"
        End Function

        Friend Function DateDiff(<Description("Start date/time (ISO 8601) or 'now'/'today'")> start As String,
                                 <Description("End date/time (ISO 8601) or 'now'/'today'")> [end] As String) As String
            Dim a = ParseMoment(start, TimeZoneInfo.Utc), b = ParseMoment([end], TimeZoneInfo.Utc)
            Dim span = b - a
            Dim months = (b.Year - a.Year) * 12 + b.Month - a.Month
            If months > 0 AndAlso a.AddMonths(months) > b Then months -= 1
            If months < 0 AndAlso a.AddMonths(months) < b Then months += 1
            Dim sb As New StringBuilder()
            sb.AppendLine($"From {a.ToString("yyyy-MM-dd HH:mm", Inv)} ({a.DayOfWeek}) to {b.ToString("yyyy-MM-dd HH:mm", Inv)} ({b.DayOfWeek}):")
            sb.AppendLine($"{span.TotalDays.ToString("0.####", Inv)} days ({Math.Truncate(span.TotalDays / 7)} weeks and {Math.Abs(CInt(Math.Truncate(span.TotalDays)) Mod 7)} days)")
            sb.AppendLine($"{span.TotalHours.ToString("0.##", Inv)} hours, {span.TotalMinutes.ToString("0.##", Inv)} minutes, {CLng(span.TotalSeconds)} seconds")
            sb.AppendLine($"{months} whole calendar months")
            sb.Append($"{BusinessDaysBetween(a.DateTime, b.DateTime)} business days (Mon-Fri, end date counted, start date not; holidays not known)")
            Return sb.ToString()
        End Function

        ' ------------------------------------------------------------------------------------------ math

        Friend Function Calculate(<Description("The expression, e.g. '(1299.99 * 3) * (1 - 15%)' or 'round(sqrt(2), 6)'")> expression As String) As String
            Dim v = Calculator.Evaluate(expression)
            Dim plain = Calculator.Format(v)
            Dim grouped = If(Math.Abs(v) >= 10000D, $" ({v.ToString("#,##0.############################", Inv)})", "")
            Return $"{expression.Trim()} = {plain}{grouped}"
        End Function

        ' ------------------------------------------------------------------------------------------ units

        Private Shared ReadOnly Units As New Dictionary(Of String, (Kind As String, Factor As Double))(StringComparer.Ordinal)

        Private Shared Sub Def(kind As String, factor As Double, ParamArray names As String())
            For Each n In names
                Units(n) = (kind, factor)
            Next
        End Sub

        Shared Sub New()
            def("length", 1, "m", "meter", "meters", "metre", "metres")
            def("length", 1000, "km", "kilometer", "kilometers")
            def("length", 0.01, "cm", "centimeter", "centimeters")
            def("length", 0.001, "mm", "millimeter", "millimeters")
            def("length", 0.000001, "um", "µm", "micrometer")
            def("length", 0.000000001, "nm", "nanometer")
            def("length", 1609.344, "mi", "mile", "miles")
            def("length", 0.9144, "yd", "yard", "yards")
            def("length", 0.3048, "ft", "foot", "feet")
            def("length", 0.0254, "in", "inch", "inches")
            def("length", 1852, "nmi", "nautical mile")
            def("mass", 1, "kg", "kilogram", "kilograms")
            def("mass", 0.001, "g", "gram", "grams")
            def("mass", 0.000001, "mg", "milligram")
            def("mass", 1000, "t", "tonne", "tonnes", "metric ton")
            def("mass", 0.45359237, "lb", "lbs", "pound", "pounds")
            def("mass", 0.028349523125, "oz", "ounce", "ounces")
            def("mass", 6.35029318, "st", "stone")
            def("time", 0.001, "ms", "millisecond", "milliseconds")
            def("time", 1, "s", "sec", "second", "seconds")
            def("time", 60, "min", "minute", "minutes")
            def("time", 3600, "h", "hr", "hour", "hours")
            def("time", 86400, "d", "day", "days")
            def("time", 604800, "wk", "week", "weeks")
            def("time", 31557600, "yr", "year", "years")
            def("data", 1, "B", "byte", "bytes")
            def("data", 0.125, "bit", "bits", "b")
            def("data", 1000.0, "KB", "kB")
            def("data", 1000.0 ^ 2, "MB")
            def("data", 1000.0 ^ 3, "GB")
            def("data", 1000.0 ^ 4, "TB")
            def("data", 1000.0 ^ 5, "PB")
            def("data", 1024.0, "KiB")
            def("data", 1024.0 ^ 2, "MiB")
            def("data", 1024.0 ^ 3, "GiB")
            def("data", 1024.0 ^ 4, "TiB")
            def("data", 1024.0 ^ 5, "PiB")
            def("data", 125.0, "Kb", "kbit")
            def("data", 125000.0, "Mb", "Mbit")
            def("data", 125000000.0, "Gb", "Gbit")
            def("volume", 1, "l", "L", "liter", "liters", "litre", "litres")
            def("volume", 0.001, "ml", "mL", "milliliter")
            def("volume", 1000, "m3", "m³", "cubic meter")
            def("volume", 3.785411784, "gal", "gallon", "gallons")
            def("volume", 0.946352946, "qt", "quart")
            def("volume", 0.473176473, "pt", "pint")
            def("volume", 0.2365882365, "cup", "cups")
            def("volume", 0.0295735295625, "floz", "fl oz")
            def("volume", 0.01478676478125, "tbsp")
            def("volume", 0.00492892159375, "tsp")
            def("area", 1, "m2", "m²", "sq m")
            def("area", 1000000, "km2", "km²")
            def("area", 0.0001, "cm2", "cm²")
            def("area", 10000, "ha", "hectare", "hectares")
            def("area", 4046.8564224, "acre", "acres")
            def("area", 0.09290304, "ft2", "ft²", "sq ft")
            def("area", 0.00064516, "in2", "in²", "sq in")
            def("area", 2589988.110336, "mi2", "mi²", "sq mi")
            def("speed", 1, "m/s", "mps")
            def("speed", 1 / 3.6, "km/h", "kph", "kmh")
            def("speed", 0.44704, "mph")
            def("speed", 0.514444444444, "knot", "knots", "kn")
            def("speed", 0.3048, "ft/s", "fps")
            def("energy", 1, "J", "joule", "joules")
            def("energy", 1000, "kJ")
            def("energy", 4.184, "cal")
            def("energy", 4184, "kcal")
            def("energy", 3600, "Wh")
            def("energy", 3600000, "kWh")
            def("pressure", 1, "Pa")
            def("pressure", 1000, "kPa")
            def("pressure", 100000, "bar")
            def("pressure", 6894.757293168, "psi")
            def("pressure", 101325, "atm")
        End Sub

        Private Shared Function FindUnit(name As String) As (Kind As String, Factor As Double, Name As String)
            Dim n = If(name, "").Trim()
            Dim u As (Kind As String, Factor As Double) = Nothing
            If Units.TryGetValue(n, u) Then Return (u.Kind, u.Factor, n)
            Dim hit = Units.FirstOrDefault(Function(kv) kv.Key.Equals(n, StringComparison.OrdinalIgnoreCase))
            If hit.Key IsNot Nothing Then Return (hit.Value.Kind, hit.Value.Factor, hit.Key)
            Return (Nothing, 0, n)
        End Function

        Private Shared Function TemperatureUnit(name As String) As String
            Select Case If(name, "").Trim().ToLowerInvariant().Replace("°", "").Replace("degrees ", "")
                Case "c", "celsius" : Return "C"
                Case "f", "fahrenheit" : Return "F"
                Case "k", "kelvin" : Return "K"
            End Select
            Return Nothing
        End Function

        Friend Function ConvertUnits(<Description("The value")> value As Double,
                                     <Description("Unit to convert from, e.g. 'mi', 'GiB', 'F', 'kWh'")> from_unit As String,
                                     <Description("Unit to convert to")> to_unit As String) As String
            Dim tf = TemperatureUnit(from_unit), tt = TemperatureUnit(to_unit)
            If tf IsNot Nothing OrElse tt IsNot Nothing Then
                If tf Is Nothing OrElse tt Is Nothing Then Throw New McpException("Temperatures convert only to temperatures (C, F, K).")
                Dim k = If(tf = "C", value + 273.15, If(tf = "F", (value - 32) * 5 / 9 + 273.15, value))
                Dim r = If(tt = "C", k - 273.15, If(tt = "F", (k - 273.15) * 9 / 5 + 32, k))
                Return $"{Num(value)} °{tf} = {Num(r)} {If(tt = "K", "K", "°" & tt)}"
            End If
            Dim a = FindUnit(from_unit), b = FindUnit(to_unit)
            If a.Kind Is Nothing Then Throw New McpException($"Unknown unit '{from_unit}'.")
            If b.Kind Is Nothing Then Throw New McpException($"Unknown unit '{to_unit}'.")
            If a.Kind <> b.Kind Then Throw New McpException($"'{from_unit}' is {a.Kind} and '{to_unit}' is {b.Kind}; they do not convert.")
            Return $"{Num(value)} {a.Name} = {Num(value * a.Factor / b.Factor)} {b.Name}"
        End Function

        Private Shared Function Num(v As Double) As String
            If Math.Abs(v) >= 1.0E+15 OrElse (v <> 0 AndAlso Math.Abs(v) < 0.000001) Then Return v.ToString("G10", Inv)
            Return Math.Round(v, 10).ToString("0.##########", Inv)
        End Function

        ' ------------------------------------------------------------------------------------------ hashes, encodings, ids, regex

        Friend Function HashText(<Description("Text to hash (UTF-8)")> text As String,
                                 <Description("md5, sha1, sha256 (default), sha384 or sha512")> Optional algorithm As String = "sha256",
                                 <Description("Key for an HMAC (optional)")> Optional key As String = "") As String
            Dim data = Encoding.UTF8.GetBytes(If(text, ""))
            Dim alg = If(String.IsNullOrWhiteSpace(algorithm), "sha256", algorithm.Trim().ToLowerInvariant().Replace("-", ""))
            Dim k = If(String.IsNullOrEmpty(key), Nothing, Encoding.UTF8.GetBytes(key))
            Dim h As Byte()
            Select Case alg
                Case "md5" : h = If(k Is Nothing, MD5.HashData(data), HMACMD5.HashData(k, data))
                Case "sha1" : h = If(k Is Nothing, SHA1.HashData(data), HMACSHA1.HashData(k, data))
                Case "sha256" : h = If(k Is Nothing, SHA256.HashData(data), HMACSHA256.HashData(k, data))
                Case "sha384" : h = If(k Is Nothing, SHA384.HashData(data), HMACSHA384.HashData(k, data))
                Case "sha512" : h = If(k Is Nothing, SHA512.HashData(data), HMACSHA512.HashData(k, data))
                Case Else : Throw New McpException($"Unknown algorithm '{algorithm}'. Use md5, sha1, sha256, sha384 or sha512.")
            End Select
            Return $"{If(k Is Nothing, "", "HMAC-")}{alg.ToUpperInvariant()} of {data.Length:N0} bytes{vbLf}hex: {Convert.ToHexString(h).ToLowerInvariant()}{vbLf}base64: {Convert.ToBase64String(h)}"
        End Function

        Friend Function EncodeText(<Description("The text")> text As String,
                                   <Description("base64, base64url, url, hex, html or jwt")> format As String,
                                   <Description("Decode instead of encode")> Optional decode As Boolean = False) As String
            Dim s = If(text, "")
            Try
                Select Case If(format, "").Trim().ToLowerInvariant()
                    Case "base64"
                        Return If(decode, Encoding.UTF8.GetString(Convert.FromBase64String(s.Trim())), Convert.ToBase64String(Encoding.UTF8.GetBytes(s)))
                    Case "base64url"
                        Return If(decode, Encoding.UTF8.GetString(FromBase64Url(s.Trim())), Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd("="c).Replace("+"c, "-"c).Replace("/"c, "_"c))
                    Case "url"
                        Return If(decode, Uri.UnescapeDataString(s.Replace("+", " ")), Uri.EscapeDataString(s))
                    Case "hex"
                        Return If(decode, Encoding.UTF8.GetString(Convert.FromHexString(Regex.Replace(s, "[\s:-]|0x", ""))), Convert.ToHexString(Encoding.UTF8.GetBytes(s)).ToLowerInvariant())
                    Case "html"
                        Return If(decode, Net.WebUtility.HtmlDecode(s), Net.WebUtility.HtmlEncode(s))
                    Case "jwt"
                        Dim parts = s.Trim().Split("."c)
                        If parts.Length < 2 Then Throw New McpException("That is not a JWT (header.payload.signature).")
                        Dim sb As New StringBuilder("JWT (signature not checked)" & vbLf)
                        sb.AppendLine("header: " & Pretty(Encoding.UTF8.GetString(FromBase64Url(parts(0)))))
                        Dim payload = Encoding.UTF8.GetString(FromBase64Url(parts(1)))
                        sb.AppendLine("payload: " & Pretty(payload))
                        Try
                            Using doc = JsonDocument.Parse(payload)
                                For Each claim In {"iat", "nbf", "exp"}
                                    Dim v As JsonElement
                                    If doc.RootElement.TryGetProperty(claim, v) AndAlso v.ValueKind = JsonValueKind.Number Then
                                        Dim at = DateTimeOffset.FromUnixTimeSeconds(v.GetInt64())
                                        sb.AppendLine($"{claim}: {at.ToString("yyyy-MM-dd HH:mm:ss", Inv)} UTC{If(claim = "exp", If(at < DateTimeOffset.UtcNow, " (expired)", " (not expired)"), "")}")
                                    End If
                                Next
                            End Using
                        Catch ex As JsonException
                        End Try
                        Return sb.ToString().TrimEnd()
                    Case Else
                        Throw New McpException($"Unknown format '{format}'. Use base64, base64url, url, hex, html or jwt.")
                End Select
            Catch ex As FormatException
                Throw New McpException($"That is not valid {format}: {ex.Message}")
            Catch ex As DecoderFallbackException
                Throw New McpException("The decoded bytes are not UTF-8 text.")
            End Try
        End Function

        Private Shared Function FromBase64Url(s As String) As Byte()
            Dim b = s.Replace("-"c, "+"c).Replace("_"c, "/"c)
            Select Case b.Length Mod 4
                Case 2 : b &= "=="
                Case 3 : b &= "="
            End Select
            Return Convert.FromBase64String(b)
        End Function

        Private Shared Function Pretty(json As String) As String
            Try
                Using doc = JsonDocument.Parse(json)
                    Return JsonSerializer.Serialize(doc.RootElement, New JsonSerializerOptions With {.WriteIndented = True})
                End Using
            Catch ex As JsonException
                Return json
            End Try
        End Function

        Friend Function NewUuids(<Description("How many (1-50, default 1)")> Optional count As Integer = 1,
                                 <Description("4 (random, default) or 7 (time-ordered)")> Optional version As Integer = 4) As String
            Dim n = Math.Clamp(count, 1, 50)
            Return String.Join(vbLf, Enumerable.Range(0, n).Select(Function(i) If(version = 7, Guid.CreateVersion7(), Guid.NewGuid()).ToString()))
        End Function

        Friend Function RegexTest(<Description(".NET regular expression")> pattern As String,
                                  <Description("Text to test")> text As String,
                                  <Description("Replacement (optional): returns the replaced text instead of the matches")> Optional replacement As String = Nothing,
                                  <Description("Flags: i (ignore case), m (multiline), s (dot matches newline), x (ignore whitespace)")> Optional flags As String = "") As String
            Dim opts = RegexOptions.None
            For Each c In If(flags, "").ToLowerInvariant()
                Select Case c
                    Case "i"c : opts = opts Or RegexOptions.IgnoreCase
                    Case "m"c : opts = opts Or RegexOptions.Multiline
                    Case "s"c : opts = opts Or RegexOptions.Singleline
                    Case "x"c : opts = opts Or RegexOptions.IgnorePatternWhitespace
                End Select
            Next
            Dim re As Regex
            Try
                re = New Regex(pattern, opts, TimeSpan.FromSeconds(2))
            Catch ex As ArgumentException
                Throw New McpException("Invalid pattern: " & ex.Message)
            End Try
            Try
                If replacement IsNot Nothing Then Return ToolKit.Clip(re.Replace(If(text, ""), replacement), 20000)
                Dim ms = re.Matches(If(text, ""))
                If ms.Count = 0 Then Return "No match."
                Dim sb As New StringBuilder($"{ms.Count} match(es):" & vbLf)
                For Each m As Match In ms.Take(50)
                    sb.AppendLine($"[{m.Index}] {JsonSerializer.Serialize(m.Value)}")
                    For g = 1 To m.Groups.Count - 1
                        Dim grp = m.Groups(g)
                        If grp.Success Then sb.AppendLine($"    {re.GroupNameFromNumber(g)}: {JsonSerializer.Serialize(grp.Value)}")
                    Next
                Next
                If ms.Count > 50 Then sb.AppendLine("(first 50 shown)")
                Return sb.ToString().TrimEnd()
            Catch ex As RegexMatchTimeoutException
                Throw New McpException("The pattern took too long (over 2 s) on this text.")
            End Try
        End Function
    End Class

    ''' <summary>Exact decimal expression evaluator for the calculate tool (functions use double precision).</summary>
    Friend NotInheritable Class Calculator
        Private ReadOnly _s As String
        Private _i As Integer
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Private Sub New(s As String)
            _s = s
        End Sub

        Public Shared Function Evaluate(expression As String) As Decimal
            If String.IsNullOrWhiteSpace(expression) Then Throw New McpException("expression is required.")
            Dim c As New Calculator(expression.Replace("×", "*").Replace("÷", "/").Replace("−", "-"))
            Try
                Dim v = c.ParseSum()
                c.Skip()
                If c._i < c._s.Length Then Throw New McpException($"Unexpected '{c._s(c._i)}' at position {c._i + 1}.")
                Return v
            Catch ex As OverflowException
                Throw New McpException("The result is too large for exact arithmetic (about 7.9e28 is the limit).")
            Catch ex As DivideByZeroException
                Throw New McpException("Division by zero.")
            End Try
        End Function

        Public Shared Function Format(v As Decimal) As String
            Return v.ToString("G29", Inv)
        End Function

        Private Sub Skip()
            While _i < _s.Length AndAlso Char.IsWhiteSpace(_s(_i))
                _i += 1
            End While
        End Sub

        Private Function Peek() As Char
            Skip()
            Return If(_i < _s.Length, _s(_i), ChrW(0))
        End Function

        Private Function TryWord(word As String) As Boolean
            Skip()
            If _i + word.Length <= _s.Length AndAlso String.Compare(_s, _i, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) = 0 AndAlso
               (_i + word.Length = _s.Length OrElse Not Char.IsLetterOrDigit(_s(_i + word.Length))) Then
                _i += word.Length
                Return True
            End If
            Return False
        End Function

        Private Function ParseSum() As Decimal
            Dim v = ParseProduct()
            While True
                Dim c = Peek()
                If c = "+"c Then
                    _i += 1 : v += ParseProduct()
                ElseIf c = "-"c Then
                    _i += 1 : v -= ParseProduct()
                Else
                    Return v
                End If
            End While
            Return v
        End Function

        Private Function ParseProduct() As Decimal
            Dim v = ParsePower()
            While True
                Dim c = Peek()
                If c = "*"c Then
                    _i += 1 : v *= ParsePower()
                ElseIf c = "/"c Then
                    _i += 1
                    Dim d = ParsePower()
                    If d = 0D Then Throw New DivideByZeroException()
                    v /= d
                ElseIf TryWord("mod") Then
                    Dim d = ParsePower()
                    If d = 0D Then Throw New DivideByZeroException()
                    v = v Mod d
                Else
                    Return v
                End If
            End While
            Return v
        End Function

        Private Function ParsePower() As Decimal
            Dim b = ParseUnary()
            If Peek() = "^"c Then
                _i += 1
                Return Pow(b, ParsePowerRight())
            End If
            If Peek() = "*"c AndAlso _i + 1 < _s.Length AndAlso _s(_i + 1) = "*"c Then
                _i += 2
                Return Pow(b, ParsePowerRight())
            End If
            Return b
        End Function

        Private Function ParsePowerRight() As Decimal
            Return ParsePower()
        End Function

        Private Function ParseUnary() As Decimal
            Dim c = Peek()
            If c = "-"c Then _i += 1 : Return -ParseUnary()
            If c = "+"c Then _i += 1 : Return ParseUnary()
            Return ParsePostfix()
        End Function

        Private Function ParsePostfix() As Decimal
            Dim v = ParsePrimary()
            While True
                Dim c = Peek()
                If c = "%"c Then
                    _i += 1 : v /= 100D
                ElseIf c = "!"c AndAlso Not (_i + 1 < _s.Length AndAlso _s(_i + 1) = "="c) Then
                    _i += 1
                    If v < 0 OrElse v <> Decimal.Truncate(v) OrElse v > 27 Then Throw New McpException("n! needs a whole number from 0 to 27.")
                    Dim f = 1D
                    For k = 2 To CInt(v)
                        f *= k
                    Next
                    v = f
                Else
                    Return v
                End If
            End While
            Return v
        End Function

        Private Function ParsePrimary() As Decimal
            Dim c = Peek()
            If c = "("c Then
                _i += 1
                Dim v = ParseSum()
                If Peek() <> ")"c Then Throw New McpException("Missing ')'.")
                _i += 1
                Return v
            End If
            If Char.IsDigit(c) OrElse c = "."c Then Return ParseNumber()
            If Char.IsLetter(c) Then
                Dim start = _i
                While _i < _s.Length AndAlso (Char.IsLetterOrDigit(_s(_i)) OrElse _s(_i) = "_"c)
                    _i += 1
                End While
                Dim name = _s.Substring(start, _i - start).ToLowerInvariant()
                If name = "pi" Then Return CDec(Math.PI)
                If name = "e" Then Return CDec(Math.E)
                If Peek() <> "("c Then Throw New McpException($"Unknown name '{name}'.")
                _i += 1
                Dim args As New List(Of Decimal)
                If Peek() <> ")"c Then
                    args.Add(ParseSum())
                    While Peek() = ","c
                        _i += 1
                        args.Add(ParseSum())
                    End While
                End If
                If Peek() <> ")"c Then Throw New McpException($"Missing ')' after {name}(...).")
                _i += 1
                Return CallFunction(name, args)
            End If
            If c = ChrW(0) Then Throw New McpException("The expression ends too early.")
            Throw New McpException($"Unexpected '{c}' at position {_i + 1}.")
        End Function

        Private Function ParseNumber() As Decimal
            Dim start = _i
            While _i < _s.Length AndAlso (Char.IsDigit(_s(_i)) OrElse _s(_i) = "."c OrElse _s(_i) = "_"c)
                _i += 1
            End While
            Dim mantissa = _s.Substring(start, _i - start).Replace("_", "")
            Dim exponent = 0
            If _i < _s.Length AndAlso (_s(_i) = "e"c OrElse _s(_i) = "E"c) Then
                Dim j = _i + 1
                If j < _s.Length AndAlso (_s(j) = "+"c OrElse _s(j) = "-"c) Then j += 1
                If j < _s.Length AndAlso Char.IsDigit(_s(j)) Then
                    Dim expStart = _i + 1
                    _i = j
                    While _i < _s.Length AndAlso Char.IsDigit(_s(_i))
                        _i += 1
                    End While
                    exponent = Integer.Parse(_s.Substring(expStart, _i - expStart), NumberStyles.AllowLeadingSign, Inv)
                End If
            End If
            Dim v As Decimal
            If Not Decimal.TryParse(mantissa, NumberStyles.AllowDecimalPoint, Inv, v) Then Throw New McpException($"'{mantissa}' is not a number.")
            If exponent <> 0 Then v *= Pow(10D, exponent)
            Return v
        End Function

        Private Shared Function Pow(b As Decimal, e As Decimal) As Decimal
            If e = Decimal.Truncate(e) AndAlso Math.Abs(e) <= 1000 Then
                Dim n = CInt(Math.Abs(e))
                Dim r = 1D, x = b
                While n > 0
                    If (n And 1) = 1 Then r *= x
                    n >>= 1
                    If n > 0 Then x *= x
                End While
                If e < 0 Then
                    If r = 0D Then Throw New DivideByZeroException()
                    r = 1D / r
                End If
                Return r
            End If
            Return ToDec(Math.Pow(CDbl(b), CDbl(e)))
        End Function

        Private Shared Function ToDec(d As Double) As Decimal
            If Double.IsNaN(d) OrElse Double.IsInfinity(d) Then Throw New McpException("The result is not a real number.")
            Return CDec(d)
        End Function

        Private Shared Function CallFunction(name As String, a As List(Of Decimal)) As Decimal
            Dim need = Sub(n As Integer)
                           If a.Count <> n Then Throw New McpException($"{name}() takes {n} argument(s).")
                       End Sub
            Dim d = Function(i As Integer) CDbl(a(i))
            Select Case name
                Case "sqrt"
                    need(1)
                    If a(0) < 0 Then Throw New McpException("sqrt of a negative number.")
                    Return ToDec(Math.Sqrt(d(0)))
                Case "cbrt" : need(1) : Return ToDec(Math.Cbrt(d(0)))
                Case "abs" : need(1) : Return Math.Abs(a(0))
                Case "round"
                    If a.Count = 1 Then Return Math.Round(a(0), 0, MidpointRounding.AwayFromZero)
                    need(2) : Return Math.Round(a(0), CInt(Math.Clamp(a(1), 0D, 28D)), MidpointRounding.AwayFromZero)
                Case "floor" : need(1) : Return Math.Floor(a(0))
                Case "ceil", "ceiling" : need(1) : Return Math.Ceiling(a(0))
                Case "trunc", "truncate" : need(1) : Return Decimal.Truncate(a(0))
                Case "min" : If a.Count = 0 Then need(1)
                    Return a.Min()
                Case "max" : If a.Count = 0 Then need(1)
                    Return a.Max()
                Case "sum" : Return a.Aggregate(0D, Function(x, y) x + y)
                Case "avg", "mean" : If a.Count = 0 Then need(1)
                    Return a.Aggregate(0D, Function(x, y) x + y) / a.Count
                Case "pow" : need(2) : Return Pow(a(0), a(1))
                Case "exp" : need(1) : Return ToDec(Math.Exp(d(0)))
                Case "ln" : need(1) : Return ToDec(Math.Log(d(0)))
                Case "log"
                    If a.Count = 1 Then Return ToDec(Math.Log10(d(0)))
                    need(2) : Return ToDec(Math.Log(d(0), d(1)))
                Case "log10" : need(1) : Return ToDec(Math.Log10(d(0)))
                Case "log2" : need(1) : Return ToDec(Math.Log2(d(0)))
                Case "sin" : need(1) : Return ToDec(Math.Round(Math.Sin(d(0)), 15))
                Case "cos" : need(1) : Return ToDec(Math.Round(Math.Cos(d(0)), 15))
                Case "tan" : need(1) : Return ToDec(Math.Round(Math.Tan(d(0)), 15))
                Case "asin" : need(1) : Return ToDec(Math.Asin(d(0)))
                Case "acos" : need(1) : Return ToDec(Math.Acos(d(0)))
                Case "atan" : need(1) : Return ToDec(Math.Atan(d(0)))
                Case "deg" : need(1) : Return a(0) * 180D / CDec(Math.PI)
                Case "rad" : need(1) : Return a(0) * CDec(Math.PI) / 180D
            End Select
            Throw New McpException($"Unknown function '{name}'.")
        End Function
    End Class

End Namespace
