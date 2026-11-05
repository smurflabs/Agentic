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

    ''' <summary>The utilities server's secure random, cron and text tools.</summary>
    Partial Public NotInheritable Class UtilitiesServer

        Private Sub AddMoreTools(tools As McpServerPrimitiveCollection(Of McpServerTool))
            ToolKit.Add(tools, New Func(Of Integer, Integer, Boolean, Boolean, String)(AddressOf GeneratePassword), "generate_password",
                "Strong random passwords (cryptographic random): length, how many, with symbols, without look-alike characters (0/O, 1/l/I).", False)
            ToolKit.Add(tools, New Func(Of Integer, String, String, String)(AddressOf RandomSecret), "random_secret",
                "A random secret or API key: bytes of cryptographic randomness as hex, base64 or base64url, with an optional prefix (e.g. sk_live_).", False)
            ToolKit.Add(tools, New Func(Of Long, Long, Integer, Boolean, String)(AddressOf RandomNumbers), "random_number",
                "Random whole numbers from min to max (inclusive), cryptographically random; unique: no repeats.", False)
            ToolKit.Add(tools, New Func(Of String, Integer, String)(AddressOf RandomPick), "random_pick",
                "Pick items at random from a list (separated by line breaks or commas), or shuffle the whole list (count 0).", False)
            ToolKit.Add(tools, New Func(Of String, String, Integer, String, String)(AddressOf ExplainCron), "explain_cron",
                "Explain a cron expression in plain words (5 fields, 6 with seconds, or @daily etc.) and list its next run times in a time zone.")
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf TextStats), "text_stats",
                "Count a text: characters, words, lines, sentences, paragraphs, estimated tokens and reading time, and its most frequent words.")
            ToolKit.Add(tools, New Func(Of String, String, Integer, String)(AddressOf TextDiff), "text_diff",
                "A unified diff (like git diff) of two texts, with the number of lines added and removed.")
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf TransformLines), "transform_lines",
                "Apply line operations in order (comma-separated): sort, sort_desc, sort_numeric, unique, unique_ci, reverse, trim, remove_empty, shuffle, number.")
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf ExtractPatterns), "extract_patterns",
                "Pull things out of text, each listed once: emails, urls, domains, ips, phones, hashtags, mentions, numbers, dates, uuids (default: all found).")
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf ConvertCase), "convert_case",
                "Change text case: slug, kebab, snake, camel, pascal, constant, title, sentence, upper or lower.")
        End Sub

        ' ------------------------------------------------------------------------------------------ secure random

        Private Const Upper As String = "ABCDEFGHIJKLMNOPQRSTUVWXYZ", Lower As String = "abcdefghijklmnopqrstuvwxyz", Digits As String = "0123456789", SymbolChars As String = "!@#$%^&*()-_=+[]{};:,.?/~"

        Friend Function GeneratePassword(<Description("Length (default 20, 8-128)")> Optional length As Integer = 20,
                                         <Description("How many (default 1, at most 20)")> Optional count As Integer = 1,
                                         <Description("Include symbols (default true)")> Optional symbols As Boolean = True,
                                         <Description("Leave out look-alike characters (default true)")> Optional exclude_ambiguous As Boolean = True) As String
            Dim n = Math.Clamp(If(length <= 0, 20, length), 8, 128)
            Dim strip = Function(s As String) If(exclude_ambiguous, New String(s.Where(Function(c) "0O1lI|".IndexOf(c) < 0).ToArray()), s)
            Dim classes = New List(Of String) From {strip(Upper), strip(Lower), strip(Digits)}
            If symbols Then classes.Add(SymbolChars)
            Dim all = String.Concat(classes)
            Dim results As New List(Of String)
            For k = 1 To Math.Clamp(If(count <= 0, 1, count), 1, 20)
                ' One of each class, the rest from all of them, then shuffled.
                Dim chars = classes.Select(Function(c) c(RandomNumberGenerator.GetInt32(c.Length))).ToList()
                While chars.Count < n
                    chars.Add(all(RandomNumberGenerator.GetInt32(all.Length)))
                End While
                Shuffle(chars)
                results.Add(New String(chars.ToArray()))
            Next
            Dim bits = n * Math.Log2(all.Length)
            Return String.Join(vbLf, results) & vbLf & $"({n} characters from a set of {all.Length}: about {bits:0} bits of entropy)"
        End Function

        Private Shared Sub Shuffle(Of T)(list As IList(Of T))
            For i = list.Count - 1 To 1 Step -1
                Dim j = RandomNumberGenerator.GetInt32(i + 1)
                Dim tmp = list(i) : list(i) = list(j) : list(j) = tmp
            Next
        End Sub

        Friend Function RandomSecret(<Description("Bytes of randomness (default 32 = 256 bits, 16-128)")> Optional bytes As Integer = 32,
                                     <Description("hex (default), base64 or base64url")> Optional format As String = "hex",
                                     <Description("Text in front, e.g. sk_live_ (optional)")> Optional prefix As String = "") As String
            Dim data = RandomNumberGenerator.GetBytes(Math.Clamp(If(bytes <= 0, 32, bytes), 16, 128))
            Dim body As String
            Select Case If(format, "hex").Trim().ToLowerInvariant()
                Case "base64" : body = Convert.ToBase64String(data)
                Case "base64url" : body = Convert.ToBase64String(data).TrimEnd("="c).Replace("+"c, "-"c).Replace("/"c, "_"c)
                Case Else : body = Convert.ToHexString(data).ToLowerInvariant()
            End Select
            Return If(prefix, "").Trim() & body
        End Function

        Friend Function RandomNumbers(<Description("Smallest value")> min As Long,
                                      <Description("Largest value")> max As Long,
                                      <Description("How many (default 1, at most 1000)")> Optional count As Integer = 1,
                                      <Description("No repeats")> Optional unique As Boolean = False) As String
            If max < min Then Throw New McpException("max must not be less than min.")
            Dim span = CDec(max) - CDec(min) + 1D
            Dim n = Math.Clamp(If(count <= 0, 1, count), 1, 1000)
            If unique AndAlso n > span Then Throw New McpException($"Only {span} different numbers exist from {min} to {max}.")
            Dim seen As New HashSet(Of Long)
            Dim list As New List(Of Long)
            While list.Count < n
                Dim v = min + CLng(RandomBelow(CULng(span)))
                If unique AndAlso Not seen.Add(v) Then Continue While
                list.Add(v)
            End While
            Return String.Join(", ", list)
        End Function

        ''' <summary>A uniform random number in [0, bound) without modulo bias.</summary>
        Private Shared Function RandomBelow(bound As ULong) As ULong
            If bound <= Integer.MaxValue Then Return CULng(RandomNumberGenerator.GetInt32(CInt(bound)))
            Dim limit = ULong.MaxValue - (ULong.MaxValue Mod bound)
            Dim buf(7) As Byte
            While True
                RandomNumberGenerator.Fill(buf)
                Dim v = BitConverter.ToUInt64(buf, 0)
                If v < limit Then Return v Mod bound
            End While
            Return 0
        End Function

        Friend Function RandomPick(<Description("The items, one per line or separated by commas")> items As String,
                                   <Description("How many to pick (default 1; 0 = shuffle all)")> Optional count As Integer = 1) As String
            Dim list = If(items, "").Split(If(items.Contains(ControlChars.Lf), {ControlChars.Lf}, {","c}), StringSplitOptions.RemoveEmptyEntries).
                Select(Function(s) s.Trim()).Where(Function(s) s.Length > 0).ToList()
            If list.Count = 0 Then Throw New McpException("items is required.")
            Shuffle(list)
            If count <= 0 Then Return String.Join(vbLf, list)
            Return String.Join(vbLf, list.Take(Math.Min(count, list.Count)))
        End Function

        ' ------------------------------------------------------------------------------------------ cron

        Private Shared ReadOnly DayNames As String() = {"Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"}
        Private Shared ReadOnly MonthNames As String() = {"", "January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"}

        Friend Function ExplainCron(<Description("Cron expression, e.g. '0 9 * * 1-5' or '@daily'")> expression As String,
                                    <Description("Time zone for the run times (default UTC)")> Optional time_zone As String = "UTC",
                                    <Description("How many next runs (default 5, at most 50)")> Optional count As Integer = 5,
                                    <Description("List runs after this date/time (default now)")> Optional from As String = "") As String
            Dim expr = If(expression, "").Trim()
            If expr.Length = 0 Then Throw New McpException("expression is required.")
            Dim fields = expr.Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries)
            Dim withSeconds = fields.Length = 6
            Dim cron As Cronos.CronExpression
            Try
                cron = Cronos.CronExpression.Parse(expr, If(withSeconds, Cronos.CronFormat.IncludeSeconds, Cronos.CronFormat.Standard))
            Catch ex As Cronos.CronFormatException
                Throw New McpException("Not a valid cron expression: " & ex.Message)
            End Try
            Dim zone = FindZone(time_zone)
            Dim start = ParseMoment(from, zone).UtcDateTime
            Dim sb As New StringBuilder()
            sb.AppendLine(DescribeCron(expr) & If(zone Is TimeZoneInfo.Utc, " (UTC)", $" ({time_zone.Trim()})"))
            Dim n = Math.Clamp(If(count <= 0, 5, count), 1, 50)
            sb.AppendLine($"Next {n} run(s):")
            Dim t = start
            For i = 1 To n
                Dim nxt = cron.GetNextOccurrence(DateTime.SpecifyKind(t, DateTimeKind.Utc), zone)
                If Not nxt.HasValue Then sb.AppendLine("(no more runs)") : Exit For
                Dim local = TimeZoneInfo.ConvertTimeFromUtc(nxt.Value, zone)
                sb.AppendLine($"  {local.ToString(If(withSeconds, "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm"), Inv)} {local.DayOfWeek}")
                t = nxt.Value
            Next
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>Plain words for a cron expression ("At 09:00, Monday through Friday").</summary>
        Friend Shared Function DescribeCron(expression As String) As String
            Dim e = expression.Trim().ToLowerInvariant()
            Select Case e
                Case "@yearly", "@annually" : Return "Once a year, at 00:00 on January 1"
                Case "@monthly" : Return "Once a month, at 00:00 on day 1"
                Case "@weekly" : Return "Once a week, at 00:00 on Sunday"
                Case "@daily", "@midnight" : Return "Every day at 00:00"
                Case "@hourly" : Return "Every hour, at minute 0"
                Case "@every_minute" : Return "Every minute"
                Case "@every_second" : Return "Every second"
            End Select
            Dim f = expression.Trim().Split({" "c, ControlChars.Tab}, StringSplitOptions.RemoveEmptyEntries).ToList()
            Dim sec As String = Nothing
            If f.Count = 6 Then sec = f(0) : f.RemoveAt(0)
            If f.Count <> 5 Then Return "A cron schedule"
            Dim minute = f(0), hour = f(1), dom = f(2), month = f(3), dow = f(4)
            Dim parts As New List(Of String)
            Dim isNum = Function(s As String) Regex.IsMatch(s, "^\d+$")
            If isNum(minute) AndAlso isNum(hour) Then
                parts.Add($"At {Integer.Parse(hour, Inv):00}:{Integer.Parse(minute, Inv):00}{If(sec IsNot Nothing AndAlso isNum(sec), ":" & Integer.Parse(sec, Inv).ToString("00", Inv), "")}")
            ElseIf isNum(minute) AndAlso hour.Contains(","c) AndAlso hour.Split(","c).All(Function(h) isNum(h)) Then
                parts.Add("At " & String.Join(", ", hour.Split(","c).Select(Function(h) $"{Integer.Parse(h, Inv):00}:{Integer.Parse(minute, Inv):00}")))
            Else
                If sec IsNot Nothing AndAlso sec <> "0" Then parts.Add(FieldWords(sec, "second", Nothing))
                parts.Add(If(minute = "*", "every minute", If(minute.StartsWith("*/"), $"every {minute.Substring(2)} minutes", "at minute " & FieldWords(minute, "minute", Nothing, True))))
                If hour <> "*" Then parts.Add(If(hour.StartsWith("*/"), $"every {hour.Substring(2)} hours", "during hour " & FieldWords(hour, "hour", Nothing, True)))
            End If
            If dom <> "*" AndAlso dom <> "?" Then
                If dom.StartsWith("*/") Then
                    parts.Add($"every {dom.Substring(2)} days")
                ElseIf dom.ToUpperInvariant() = "L" Then
                    parts.Add("on the last day of the month")
                Else
                    parts.Add("on day " & FieldWords(dom, "day", Nothing, True) & " of the month")
                End If
            End If
            If dow <> "*" AndAlso dow <> "?" Then parts.Add(If(dow.StartsWith("*/"), $"every {dow.Substring(2)} days of the week", "on " & FieldWords(dow, "weekday", DayNames, True)))
            If month <> "*" Then parts.Add(If(month.StartsWith("*/"), $"every {month.Substring(2)} months", "in " & FieldWords(month, "month", MonthNames, True)))
            Dim text = String.Join(", ", parts)
            Return Char.ToUpperInvariant(text(0)) & text.Substring(1)
        End Function

        Private Shared Function FieldWords(field As String, unit As String, names As String(), Optional bare As Boolean = False) As String
            Dim label = Function(token As String) As String
                             Dim t = token.Trim().ToUpperInvariant()
                             If names Is Nothing Then Return token
                             Dim n As Integer
                             If Integer.TryParse(t, n) Then
                                 If names Is DayNames Then n = n Mod 7
                                 Return If(n >= 0 AndAlso n < names.Length AndAlso names(n) <> "", names(n), token)
                             End If
                             Dim hit = names.FirstOrDefault(Function(x) x.Length >= 3 AndAlso x.Substring(0, 3).ToUpperInvariant() = t.Substring(0, Math.Min(3, t.Length)))
                             Return If(hit, token)
                         End Function
            Dim items = field.Split(","c).Select(Function(part)
                                                      Dim stepText = ""
                                                      Dim p = part
                                                      If p.Contains("/"c) Then stepText = $" every {p.Split("/"c)(1)}" : p = p.Split("/"c)(0)
                                                      If p.Contains("-"c) Then
                                                          Dim ab = p.Split("-"c)
                                                          Return $"{label(ab(0))} through {label(ab(1))}{stepText}"
                                                      End If
                                                      Return label(p) & stepText
                                                  End Function).ToList()
            Dim joined = If(items.Count <= 2, String.Join(" and ", items), String.Join(", ", items.Take(items.Count - 1)) & " and " & items.Last())
            Return If(bare, joined, $"{unit} {joined}")
        End Function

        ' ------------------------------------------------------------------------------------------ text

        Friend Function TextStats(<Description("The text")> text As String) As String
            Dim t = If(text, "")
            Dim words = Regex.Matches(t, "[\p{L}\p{N}][\p{L}\p{N}'’\-]*").Select(Function(m) m.Value).ToList()
            Dim sentences = Regex.Matches(t, "[^.!?\n]+[.!?]+(\s|$)").Count
            Dim paragraphs = Regex.Split(t.Trim(), "\n\s*\n").Where(Function(p) p.Trim().Length > 0).Count()
            Dim lines = If(t.Length = 0, 0, t.Split(ControlChars.Lf).Length)
            Dim tokens = CInt(Math.Ceiling(Math.Max(t.Length / 4.0, words.Count * 1.3)))
            Dim minutes = words.Count / 230.0
            Dim stopWords = New HashSet(Of String)({"the", "a", "an", "and", "or", "of", "to", "in", "is", "it", "for", "on", "that", "this", "with", "as", "be", "are", "was", "at", "by", "i", "you", "we", "not"})
            Dim top = words.Select(Function(w) w.ToLowerInvariant()).Where(Function(w) w.Length > 2 AndAlso Not stopWords.Contains(w)).
                GroupBy(Function(w) w).OrderByDescending(Function(g) g.Count()).ThenBy(Function(g) g.Key).Take(10).Select(Function(g) $"{g.Key} ({g.Count()})")
            Return $"Characters: {t.Length:N0} ({t.Count(Function(c) Not Char.IsWhiteSpace(c)):N0} without spaces), bytes (UTF-8): {Encoding.UTF8.GetByteCount(t):N0}" & vbLf &
                   $"Words: {words.Count:N0}; unique: {words.Select(Function(w) w.ToLowerInvariant()).Distinct().Count():N0}" & vbLf &
                   $"Lines: {lines:N0}; sentences: {sentences:N0}; paragraphs: {paragraphs:N0}" & vbLf &
                   $"Estimated tokens: about {tokens:N0} (English text; code and other languages run higher)" & vbLf &
                   $"Reading time: about {If(minutes < 1, "under a minute", $"{Math.Ceiling(minutes):0} min")} at 230 words a minute" &
                   If(top.Any(), vbLf & "Most frequent words: " & String.Join(", ", top), "")
        End Function

        ''' <summary>A unified diff (hunks with context lines) of two texts; lines compared exactly.</summary>
        Friend Shared Function UnifiedDiff(original As String, revised As String, context As Integer, Optional labelA As String = "a", Optional labelB As String = "b") As (Text As String, Added As Integer, Removed As Integer)
            Dim a = If(original, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            Dim b = If(revised, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf)
            If CLng(a.Length) * b.Length > 25_000_000 Then Throw New McpException("The texts are too long to compare here (at most about 5,000 lines each).")
            Dim lcs(a.Length, b.Length) As Integer
            For i = a.Length - 1 To 0 Step -1
                For j = b.Length - 1 To 0 Step -1
                    lcs(i, j) = If(a(i) = b(j), lcs(i + 1, j + 1) + 1, Math.Max(lcs(i + 1, j), lcs(i, j + 1)))
                Next
            Next
            Dim ops As New List(Of (Kind As Char, Text As String, A As Integer, B As Integer))
            Dim x = 0, y = 0
            While x < a.Length OrElse y < b.Length
                If x < a.Length AndAlso y < b.Length AndAlso a(x) = b(y) Then
                    ops.Add((" "c, a(x), x, y)) : x += 1 : y += 1
                ElseIf y < b.Length AndAlso (x >= a.Length OrElse lcs(x, y + 1) >= lcs(x + 1, y)) Then
                    ops.Add(("+"c, b(y), x, y)) : y += 1
                Else
                    ops.Add(("-"c, a(x), x, y)) : x += 1
                End If
            End While
            Dim added = ops.Where(Function(o) o.Kind = "+"c).Count(), removed = ops.Where(Function(o) o.Kind = "-"c).Count()
            If added = 0 AndAlso removed = 0 Then Return ("The texts are identical.", 0, 0)
            Dim sb As New StringBuilder($"--- {labelA}{vbLf}+++ {labelB}{vbLf}")
            Dim k = 0
            While k < ops.Count
                If ops(k).Kind = " "c Then k += 1 : Continue While
                Dim startIdx = Math.Max(0, k - context)
                Dim endIdx = k
                ' Extend the hunk while changes are within 2*context lines of each other.
                Dim lastChange = k
                While endIdx < ops.Count AndAlso endIdx - lastChange <= 2 * context
                    If ops(endIdx).Kind <> " "c Then lastChange = endIdx
                    endIdx += 1
                End While
                endIdx = Math.Min(ops.Count, lastChange + context + 1)
                Dim hunk = ops.Skip(startIdx).Take(endIdx - startIdx).ToList()
                Dim aStart = hunk.First().A + 1, bStart = hunk.First().B + 1
                Dim aLen = hunk.Where(Function(o) o.Kind <> "+"c).Count(), bLen = hunk.Where(Function(o) o.Kind <> "-"c).Count()
                sb.Append($"@@ -{aStart},{aLen} +{bStart},{bLen} @@{vbLf}")
                For Each o In hunk
                    sb.Append(o.Kind).Append(o.Text).Append(vbLf)
                Next
                k = endIdx
            End While
            Return (sb.ToString().TrimEnd(), added, removed)
        End Function

        Friend Function TextDiff(<Description("The original text")> original As String,
                                 <Description("The changed text")> revised As String,
                                 <Description("Unchanged lines shown around each change (default 3)")> Optional context As Integer = 3) As String
            Dim r = UnifiedDiff(original, revised, Math.Clamp(If(context < 0, 3, context), 0, 20), "original", "revised")
            If r.Added = 0 AndAlso r.Removed = 0 Then Return r.Text
            Return $"+{r.Added} / -{r.Removed} lines{vbLf}{ToolKit.Clip(r.Text, 100000)}"
        End Function

        Friend Function TransformLines(<Description("The text")> text As String,
                                       <Description("Operations in order, comma-separated: sort, sort_desc, sort_numeric, unique, unique_ci, reverse, trim, remove_empty, shuffle, number")> operations As String) As String
            Dim lines = If(text, "").Replace(vbCrLf, vbLf).Split(ControlChars.Lf).ToList()
            If lines.Count > 0 AndAlso lines(lines.Count - 1) = "" Then lines.RemoveAt(lines.Count - 1)
            For Each op In ToolKit.SplitList(operations).Select(Function(o) o.ToLowerInvariant())
                Select Case op
                    Case "sort" : lines = lines.OrderBy(Function(l) l, StringComparer.OrdinalIgnoreCase).ThenBy(Function(l) l, StringComparer.Ordinal).ToList()
                    Case "sort_desc" : lines = lines.OrderByDescending(Function(l) l, StringComparer.OrdinalIgnoreCase).ToList()
                    Case "sort_numeric"
                        lines = lines.OrderBy(Function(l)
                                                  Dim m = Regex.Match(l, "-?\d+(\.\d+)?")
                                                  Dim v As Double
                                                  Return If(m.Success AndAlso Double.TryParse(m.Value, NumberStyles.Float, Inv, v), v, Double.MaxValue)
                                              End Function).ToList()
                    Case "unique" : lines = lines.Distinct(StringComparer.Ordinal).ToList()
                    Case "unique_ci" : lines = lines.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    Case "reverse" : lines.Reverse()
                    Case "trim" : lines = lines.Select(Function(l) l.Trim()).ToList()
                    Case "remove_empty" : lines = lines.Where(Function(l) l.Trim().Length > 0).ToList()
                    Case "shuffle" : Shuffle(lines)
                    Case "number" : lines = lines.Select(Function(l, i) $"{i + 1}. {l}").ToList()
                    Case Else : Throw New McpException($"Unknown operation '{op}'.")
                End Select
            Next
            Return ToolKit.Clip(String.Join(vbLf, lines), 200000)
        End Function

        Private Shared ReadOnly PatternTable As (Name As String, Re As Regex)() = {
            ("emails", New Regex("[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.Compiled)),
            ("urls", New Regex("\bhttps?://[^\s<>""'()\[\]]+[^\s<>""'()\[\].,;:!?]", RegexOptions.Compiled Or RegexOptions.IgnoreCase)),
            ("domains", New Regex("\b(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+(?:com|net|org|io|dev|ai|app|co|edu|gov|xyz|info|biz|me|us|uk|de|fr|ca|au|nl|eu|ru|cn|jp|in|br)\b", RegexOptions.Compiled Or RegexOptions.IgnoreCase)),
            ("ips", New Regex("\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.Compiled)),
            ("phones", New Regex("(?<!\w)\+?\d{1,3}?[\s.\-]?\(?\d{2,4}\)?[\s.\-]\d{3,4}[\s.\-]?\d{3,4}(?!\w)", RegexOptions.Compiled)),
            ("hashtags", New Regex("(?<![\w#])#[A-Za-z][\w]{1,60}", RegexOptions.Compiled)),
            ("mentions", New Regex("(?<![\w@])@[A-Za-z0-9_]{2,40}\b(?!\.)", RegexOptions.Compiled)),
            ("dates", New Regex("\b(?:\d{4}-\d{2}-\d{2}|\d{1,2}/\d{1,2}/\d{2,4}|(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\.? \d{1,2},? \d{4})\b", RegexOptions.Compiled)),
            ("uuids", New Regex("\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b", RegexOptions.Compiled)),
            ("numbers", New Regex("(?<![\w.])-?\d{1,3}(?:,\d{3})+(?:\.\d+)?(?!\w|\.\d)|(?<![\w.])-?\d+(?:\.\d+)?(?!\w|\.\d)", RegexOptions.Compiled))}

        Friend Function ExtractPatterns(<Description("The text")> text As String,
                                        <Description("What to find, comma-separated (default: everything found)")> Optional kinds As String = "") As String
            Dim want = ToolKit.SplitList(kinds).Select(Function(k) k.ToLowerInvariant()).ToList()
            Dim unknown = want.Where(Function(w) Not PatternTable.Any(Function(p) p.Name = w)).ToList()
            If unknown.Count > 0 Then Throw New McpException($"Unknown kind(s): {String.Join(", ", unknown)}. Use {String.Join(", ", PatternTable.Select(Function(p) p.Name))}.")
            Dim sb As New StringBuilder()
            For Each p In PatternTable.Where(Function(x) want.Count = 0 OrElse want.Contains(x.Name))
                If want.Count = 0 AndAlso p.Name = "numbers" Then Continue For
                Dim found = p.Re.Matches(If(text, "")).Select(Function(m) m.Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                If p.Name = "domains" Then found = found.Where(Function(d) Not Regex.IsMatch(If(text, ""), "@" & Regex.Escape(d))).ToList()
                If found.Count = 0 AndAlso want.Count = 0 Then Continue For
                sb.AppendLine($"{p.Name} ({found.Count}):")
                For Each f In found.Take(500)
                    sb.AppendLine("  " & f)
                Next
            Next
            Return If(sb.Length = 0, "Nothing found.", sb.ToString().TrimEnd())
        End Function

        ' Latin letters with accents and their plain forms (Unicode normalization is not available: the host runs in invariant
        ' globalization mode).
        Private Const AccentFrom As String = "ÀÁÂÃÄÅàáâãäåÇçÈÉÊËèéêëÌÍÎÏìíîïÑñÒÓÔÕÖØòóôõöøÙÚÛÜùúûüÝýÿŠšŽžČčĆćĐđŁłŃńŚśŹźŻżĘęĄąŘřŤťŮůŇňĎďĞğŞşİı"
        Private Const AccentTo As String = "AAAAAAaaaaaaCcEEEEeeeeIIIIiiiiNnOOOOOOooooooUUUUuuuuYyySsZzCcCcDdLlNnSsZzZzEeAaRrTtUuNnDdGgSsIi"

        Friend Shared Function FoldAccents(text As String) As String
            Dim sb As New StringBuilder(If(text, "").Length)
            For Each c In If(text, "")
                Dim i = AccentFrom.IndexOf(c)
                If i >= 0 Then
                    sb.Append(AccentTo(i))
                ElseIf c = "ß"c Then
                    sb.Append("ss")
                ElseIf c = "æ"c OrElse c = "Æ"c Then
                    sb.Append("ae")
                ElseIf c = "œ"c OrElse c = "Œ"c Then
                    sb.Append("oe")
                Else
                    sb.Append(c)
                End If
            Next
            Return sb.ToString()
        End Function

        Friend Shared Function Words(text As String) As List(Of String)
            ' Splits on separators and on lower-to-upper and letter-digit boundaries (helloWorld2 -> hello, World, 2).
            Dim spaced = Regex.Replace(If(text, ""), "([a-z0-9])([A-Z])", "$1 $2")
            spaced = Regex.Replace(spaced, "([A-Z]+)([A-Z][a-z])", "$1 $2")
            Return Regex.Split(spaced, "[^\p{L}\p{N}]+").Where(Function(w) w.Length > 0).ToList()
        End Function

        Friend Function ConvertCase(<Description("The text")> text As String,
                                    <Description("slug, kebab, snake, camel, pascal, constant, title, sentence, upper or lower")> [to] As String) As String
            Dim t = If(text, "")
            Dim w = Words(t)
            Dim cap = Function(s As String) If(s.Length = 0, s, Char.ToUpperInvariant(s(0)) & s.Substring(1).ToLowerInvariant())
            Select Case If([to], "").Trim().ToLowerInvariant()
                Case "slug"
                    Return String.Join("-", Words(FoldAccents(t).ToLowerInvariant()).Select(Function(x) x.ToLowerInvariant()))
                Case "kebab" : Return String.Join("-", w.Select(Function(x) x.ToLowerInvariant()))
                Case "snake" : Return String.Join("_", w.Select(Function(x) x.ToLowerInvariant()))
                Case "constant" : Return String.Join("_", w.Select(Function(x) x.ToUpperInvariant()))
                Case "camel" : Return String.Concat(w.Select(Function(x, i) If(i = 0, x.ToLowerInvariant(), cap(x))))
                Case "pascal" : Return String.Concat(w.Select(cap))
                Case "title"
                    Dim small = New HashSet(Of String)({"a", "an", "the", "and", "but", "or", "for", "nor", "on", "at", "to", "by", "of", "in", "with"})
                    Dim parts = t.Split(" "c)
                    Return String.Join(" ", parts.Select(Function(p, i) If(i > 0 AndAlso i < parts.Length - 1 AndAlso small.Contains(p.ToLowerInvariant()), p.ToLowerInvariant(), cap(p))))
                Case "sentence"
                    Dim lower = t.ToLowerInvariant()
                    Return Regex.Replace(lower, "(^\s*\p{L})|([.!?]\s+\p{L})", Function(m) m.Value.ToUpperInvariant())
                Case "upper" : Return t.ToUpperInvariant()
                Case "lower" : Return t.ToLowerInvariant()
            End Select
            Throw New McpException("to must be slug, kebab, snake, camel, pascal, constant, title, sentence, upper or lower.")
        End Function
    End Class

End Namespace
