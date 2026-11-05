Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>The data server's statistics and finance tools (money sums in decimal, so cents stay exact).</summary>
    Partial Public NotInheritable Class DataServer

        Private Sub AddMathTools(tools As McpServerPrimitiveCollection(Of McpServerTool))
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf Statistics), "statistics",
                "Descriptive statistics: count, sum, mean, median, mode, min, max, range, standard deviation and variance (sample and population), " &
                "percentiles (5-99), quartiles and outliers. values: numbers separated by spaces, new lines or semicolons; or source: a CSV/JSON table " &
                "(text or file) with column (default: every numeric column).")
            ToolKit.Add(tools, New Func(Of String, String, String, String, String, String)(AddressOf Aggregate), "aggregate",
                "Group a table (CSV/JSON/YAML, text or file) by a column and summarise another: count, sum, avg, min, max (like a pivot / SQL GROUP BY).")
            ToolKit.Add(tools, New Func(Of Decimal, Decimal, Double, String)(AddressOf PercentChange), "percent_change",
                "Change from an old to a new value: difference, percent change, the ratio, and the yearly growth rate (CAGR) when years is given.")
            ToolKit.Add(tools, New Func(Of Decimal, Double, Double, Integer, Decimal, String)(AddressOf CompoundInterest), "compound_interest",
                "Growth with compound interest: principal, yearly rate in %, years, compounding per year (12 = monthly), optional monthly contribution; year-by-year table.")
            ToolKit.Add(tools, New Func(Of Decimal, Double, Integer, Integer, String)(AddressOf LoanSchedule), "loan_schedule",
                "A fixed-rate loan: monthly payment, total paid, total interest and the amortization schedule (principal, yearly rate in %, months).")
            ToolKit.Add(tools, New Func(Of Double, String, String)(AddressOf CashFlows), "cash_flows",
                "Net present value (NPV) at a discount rate in %, internal rate of return (IRR) and payback period for cash flows per period (first one = now, usually negative).")
        End Sub

        ' ------------------------------------------------------------------------------------------ number parsing

        ''' <summary>Numbers from free text: "1 2 3", one per line, "1;2;3", "1,2,3" or "1,234.50, 99" ($ and % ignored).</summary>
        Friend Shared Function ParseNumbers(values As String) As List(Of Decimal)
            Dim result As New List(Of Decimal)
            For Each token In Regex.Split(If(values, ""), "[\s;|]+").Where(Function(t) t.Length > 0)
                Dim t = token.Replace("$", "").Replace("€", "").Replace("£", "").Replace("%", "").Trim("("c, ")"c, "["c, "]"c)
                Dim parts = t.Split(","c, StringSplitOptions.RemoveEmptyEntries)
                ' "1,234,567.5" is one number (thousands); "1,2,3" are three.
                Dim pieces = If(parts.Length > 1 AndAlso parts.Skip(1).All(Function(p) Regex.IsMatch(p, "^\d{3}(\.\d+)?$")) AndAlso Regex.IsMatch(parts(0), "^-?\d{1,3}$"),
                                {String.Concat(parts)}, parts)
                For Each p In pieces
                    Dim d As Decimal
                    If Decimal.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, d) Then result.Add(d)
                Next
            Next
            Return result
        End Function

        Private Function TableFrom(source As String) As List(Of JsonObject)
            Dim src = ReadSource(source)
            Dim node = Parse(src.Text, DetectFormat(src.Text, src.FileName, ""))
            Dim arr = TryCast(node, JsonArray)
            If arr Is Nothing AndAlso TypeOf node Is JsonObject AndAlso DirectCast(node, JsonObject).Count = 1 Then arr = TryCast(DirectCast(node, JsonObject).First().Value, JsonArray)
            If arr Is Nothing OrElse Not arr.All(Function(x) TypeOf x Is JsonObject) Then Throw New McpException("The source must be a table: CSV, or a list of objects.")
            Return arr.Cast(Of JsonObject)().ToList()
        End Function

        Private Shared Function NumberOf(n As JsonNode) As Decimal?
            If n Is Nothing Then Return Nothing
            Dim nums = ParseNumbers(Plain(n))
            Return If(nums.Count = 1, nums(0), CType(Nothing, Decimal?))
        End Function

        ' ------------------------------------------------------------------------------------------ statistics

        Friend Shared Function Percentile(sorted As List(Of Decimal), p As Double) As Decimal
            If sorted.Count = 1 Then Return sorted(0)
            Dim rank = p / 100.0 * (sorted.Count - 1)
            Dim lo = CInt(Math.Floor(rank)), hi = CInt(Math.Ceiling(rank))
            Return sorted(lo) + (sorted(hi) - sorted(lo)) * CDec(rank - lo)
        End Function

        Private Shared Function Fmt(d As Decimal) As String
            Return Math.Round(d, 10).ToString("G29", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function FmtD(d As Double) As String
            If Double.IsNaN(d) OrElse Double.IsInfinity(d) Then Return "n/a"
            Return Math.Round(d, 8).ToString("0.########", CultureInfo.InvariantCulture)
        End Function

        Friend Shared Function Describe(values As List(Of Decimal), Optional label As String = Nothing) As String
            If values.Count = 0 Then Return If(label Is Nothing, "", label & ": ") & "no numbers."
            Dim s = values.OrderBy(Function(v) v).ToList()
            Dim n = s.Count
            Dim sum = s.Aggregate(0D, Function(a, b) a + b)
            Dim mean = sum / n
            Dim sq = s.Sum(Function(v) Math.Pow(CDbl(v - mean), 2))
            Dim varP = sq / n, varS = If(n > 1, sq / (n - 1), Double.NaN)
            Dim counts = s.GroupBy(Function(v) v).Select(Function(g) (Value:=g.Key, Count:=g.Count())).OrderByDescending(Function(g) g.Count).ToList()
            Dim modeText = If(counts(0).Count = 1, "none (all values differ)", String.Join(", ", counts.Where(Function(g) g.Count = counts(0).Count).Take(5).Select(Function(g) Fmt(g.Value))) & $" ({counts(0).Count}x)")
            Dim q1 = Percentile(s, 25), q3 = Percentile(s, 75)
            Dim iqr = q3 - q1
            Dim outliers = s.Where(Function(v) v < q1 - 1.5D * iqr OrElse v > q3 + 1.5D * iqr).ToList()
            Dim sb As New StringBuilder()
            If label IsNot Nothing Then sb.AppendLine($"{label}:")
            sb.AppendLine($"count {n}, sum {Fmt(sum)}, mean {Fmt(mean)}, median {Fmt(Percentile(s, 50))}, mode {modeText}")
            sb.AppendLine($"min {Fmt(s(0))}, max {Fmt(s(n - 1))}, range {Fmt(s(n - 1) - s(0))}")
            sb.AppendLine($"std dev: sample {FmtD(Math.Sqrt(varS))}, population {FmtD(Math.Sqrt(varP))}; variance: sample {FmtD(varS)}, population {FmtD(varP)}")
            sb.AppendLine($"percentiles: p5 {Fmt(Percentile(s, 5))}, p10 {Fmt(Percentile(s, 10))}, p25 {Fmt(q1)}, p50 {Fmt(Percentile(s, 50))}, p75 {Fmt(q3)}, p90 {Fmt(Percentile(s, 90))}, p95 {Fmt(Percentile(s, 95))}, p99 {Fmt(Percentile(s, 99))}")
            sb.Append($"IQR {Fmt(iqr)}; outliers (beyond 1.5 IQR): {If(outliers.Count = 0, "none", String.Join(", ", outliers.Take(20).Select(Function(v) Fmt(v))) & If(outliers.Count > 20, $" (+{outliers.Count - 20})", ""))}")
            Return sb.ToString()
        End Function

        Friend Function Statistics(<Description("Numbers (spaces, new lines or semicolons between them); or leave empty and give source")> Optional values As String = "",
                                   <Description("A table: CSV/JSON text or a file path (instead of values)")> Optional source As String = "",
                                   <Description("The table's column (default: every numeric column)")> Optional column As String = "") As String
            If Not String.IsNullOrWhiteSpace(values) Then Return Describe(ParseNumbers(values))
            If String.IsNullOrWhiteSpace(source) Then Throw New McpException("Give values, or a source table.")
            Dim rows = TableFrom(source)
            If rows.Count = 0 Then Return "The table has no rows."
            Dim cols = If(String.IsNullOrWhiteSpace(column), rows.SelectMany(Function(r) r.Select(Function(kv) kv.Key)).Distinct().ToList(), New List(Of String) From {column.Trim()})
            Dim sb As New StringBuilder($"{rows.Count} row(s)." & vbLf)
            Dim shown = 0
            For Each c In cols
                Dim nums = rows.Select(Function(r) If(r.ContainsKey(c), NumberOf(r(c)), Nothing)).Where(Function(v) v.HasValue).Select(Function(v) v.Value).ToList()
                If nums.Count = 0 Then
                    If Not String.IsNullOrWhiteSpace(column) Then Throw New McpException($"Column '{column}' has no numbers (columns: {String.Join(", ", rows(0).Select(Function(kv) kv.Key))}).")
                    Continue For
                End If
                sb.AppendLine(Describe(nums, $"{c} ({nums.Count} numeric of {rows.Count})")).AppendLine()
                shown += 1
            Next
            If shown = 0 Then Return "No numeric columns."
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function Aggregate(<Description("A table: CSV/JSON/YAML text or a file path")> source As String,
                                  <Description("Column to group by (several: comma-separated)")> group_by As String,
                                  <Description("Column to summarise (optional for count)")> Optional value_column As String = "",
                                  <Description("count, sum, avg, min, max (comma-separated; default count,sum,avg)")> Optional operations As String = "count,sum,avg",
                                  <Description("Sort by this result column, e.g. sum (descending; optional)")> Optional sort_by As String = "") As String
            Dim rows = TableFrom(source)
            Dim keys = ToolKit.SplitList(group_by)
            If keys.Count = 0 Then Throw New McpException("group_by is required.")
            Dim ops = ToolKit.SplitList(If(String.IsNullOrWhiteSpace(operations), "count,sum,avg", operations)).Select(Function(o) o.ToLowerInvariant()).ToList()
            For Each o In ops
                If Not {"count", "sum", "avg", "mean", "min", "max"}.Contains(o) Then Throw New McpException($"Unknown operation '{o}'.")
            Next
            If ops.Any(Function(o) o <> "count") AndAlso String.IsNullOrWhiteSpace(value_column) Then Throw New McpException("value_column is required for sum, avg, min and max.")
            Dim groups = rows.GroupBy(Function(r) String.Join(" | ", keys.Select(Function(k) If(r.ContainsKey(k), Plain(r(k)), "")))).ToList()
            Dim header = keys.Concat(ops.Select(Function(o) If(o = "count", "count", $"{o}({value_column.Trim()})"))).ToList()
            Dim table As New List(Of (Cells As List(Of String), Sort As Decimal))
            For Each g In groups
                Dim nums = If(String.IsNullOrWhiteSpace(value_column), New List(Of Decimal),
                              g.Select(Function(r) If(r.ContainsKey(value_column.Trim()), NumberOf(r(value_column.Trim())), Nothing)).Where(Function(v) v.HasValue).Select(Function(v) v.Value).ToList())
                Dim cells = g.Key.Split(" | ").ToList()
                Dim sortValue = 0D
                For Each o In ops
                    Dim v As Decimal? = Nothing
                    Select Case o
                        Case "count" : v = g.Count()
                        Case "sum" : v = nums.Aggregate(0D, Function(a, b) a + b)
                        Case "avg", "mean" : If nums.Count > 0 Then v = nums.Aggregate(0D, Function(a, b) a + b) / nums.Count
                        Case "min" : If nums.Count > 0 Then v = nums.Min()
                        Case "max" : If nums.Count > 0 Then v = nums.Max()
                    End Select
                    cells.Add(If(v.HasValue, Fmt(Math.Round(v.Value, 6)), ""))
                    If v.HasValue AndAlso (sort_by.Trim().ToLowerInvariant() = o OrElse (sort_by.Trim() = "" AndAlso o = ops(0))) Then sortValue = v.Value
                Next
                table.Add((cells, sortValue))
            Next
            Dim ordered = If(String.IsNullOrWhiteSpace(sort_by), table.OrderBy(Function(t) t.Cells(0), StringComparer.OrdinalIgnoreCase).ToList(), table.OrderByDescending(Function(t) t.Sort).ToList())
            Dim rowsOut = New List(Of List(Of String)) From {header}
            rowsOut.AddRange(ordered.Select(Function(t) t.Cells))
            Return $"{groups.Count} group(s) from {rows.Count} row(s):{vbLf}" & TableText.ToMarkdown(rowsOut, 1000)
        End Function

        ' ------------------------------------------------------------------------------------------ finance

        Private Shared Function Money(d As Decimal) As String
            Return Math.Round(d, 2, MidpointRounding.AwayFromZero).ToString("#,##0.00", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function PowDec(b As Decimal, n As Integer) As Decimal
            Dim r = 1D
            For i = 1 To n
                r *= b
            Next
            Return r
        End Function

        Friend Function PercentChange(<Description("Old (starting) value")> old_value As Decimal,
                                      <Description("New value")> new_value As Decimal,
                                      <Description("Years between them, for the yearly growth rate (optional)")> Optional years As Double = 0) As String
            Dim diff = new_value - old_value
            Dim sb As New StringBuilder($"Change: {Fmt(diff)}")
            If old_value = 0D Then
                sb.Append(" (percent change is undefined from 0)")
            Else
                sb.Append($" = {Fmt(Math.Round(diff / Math.Abs(old_value) * 100D, 6))}%; new is {Fmt(Math.Round(new_value / old_value, 6))}x the old ({Fmt(Math.Round(new_value / old_value * 100D, 4))}% of it)")
                If years > 0 Then
                    If old_value < 0 OrElse new_value < 0 Then
                        sb.Append("; CAGR needs positive values")
                    Else
                        Dim cagr = Math.Pow(CDbl(new_value / old_value), 1 / years) - 1
                        sb.Append($"; CAGR over {years.ToString("0.##", CultureInfo.InvariantCulture)} years: {FmtD(Math.Round(cagr * 100, 4))}% a year")
                    End If
                End If
            End If
            If new_value <> 0D Then sb.Append($". Reverse: from new back to old is {Fmt(Math.Round((old_value - new_value) / Math.Abs(new_value) * 100D, 6))}%")
            Return sb.ToString() & "."
        End Function

        Friend Function CompoundInterest(<Description("Starting amount")> principal As Decimal,
                                         <Description("Yearly interest rate in percent, e.g. 5 for 5%")> annual_rate As Double,
                                         <Description("Years")> years As Double,
                                         <Description("Compounding periods per year (default 12 = monthly; 1 = yearly, 365 = daily)")> Optional periods_per_year As Integer = 12,
                                         <Description("Added every month (optional)")> Optional monthly_contribution As Decimal = 0D) As String
            If years <= 0 OrElse years > 200 Then Throw New McpException("years must be between 0 and 200.")
            Dim n = Math.Clamp(If(periods_per_year <= 0, 12, periods_per_year), 1, 365)
            Dim rate = CDec(annual_rate) / 100D / n
            Dim periods = CInt(Math.Round(years * n))
            Dim perPeriodContribution = monthly_contribution * 12D / n
            Dim balance = principal, contributed = principal
            Dim sb As New StringBuilder()
            Dim rows As New List(Of List(Of String)) From {New List(Of String) From {"year", "contributed", "interest so far", "balance"}}
            For p = 1 To periods
                balance += balance * rate + perPeriodContribution
                contributed += perPeriodContribution
                If p Mod n = 0 OrElse p = periods Then rows.Add(New List(Of String) From {(CDbl(p) / n).ToString("0.##", CultureInfo.InvariantCulture), Money(contributed), Money(balance - contributed), Money(balance)})
            Next
            Dim effective = Math.Pow(1 + annual_rate / 100 / n, n) - 1
            sb.AppendLine($"Final balance: {Money(balance)} after {years.ToString("0.##", CultureInfo.InvariantCulture)} years at {annual_rate.ToString("0.####", CultureInfo.InvariantCulture)}% compounded {n}x a year (effective {FmtD(Math.Round(effective * 100, 4))}% a year).")
            sb.AppendLine($"Contributed {Money(contributed)}; interest earned {Money(balance - contributed)}.")
            sb.Append(TableText.ToMarkdown(rows, 200))
            Return sb.ToString()
        End Function

        Friend Function LoanSchedule(<Description("Amount borrowed")> principal As Decimal,
                                     <Description("Yearly interest rate in percent, e.g. 6.5")> annual_rate As Double,
                                     <Description("Number of monthly payments, e.g. 360 for 30 years")> months As Integer,
                                     <Description("Schedule rows to show (default 12; the last row is always shown)")> Optional show_rows As Integer = 12) As String
            If principal <= 0 Then Throw New McpException("principal must be more than 0.")
            If months < 1 OrElse months > 1200 Then Throw New McpException("months must be between 1 and 1200.")
            Dim r = CDec(annual_rate) / 1200D
            Dim payment As Decimal
            If r = 0D Then
                payment = principal / months
            Else
                Dim f = PowDec(1D + r, months)
                payment = principal * r * f / (f - 1D)
            End If
            payment = Math.Round(payment, 2, MidpointRounding.AwayFromZero)
            Dim balance = principal, totalInterest = 0D, totalPaid = 0D
            Dim rows As New List(Of List(Of String)) From {New List(Of String) From {"month", "payment", "interest", "principal", "balance"}}
            Dim show = Math.Clamp(If(show_rows <= 0, 12, show_rows), 1, 600)
            For m = 1 To months
                Dim interest = Math.Round(balance * r, 2, MidpointRounding.AwayFromZero)
                Dim pay = If(m = months, balance + interest, Math.Min(payment, balance + interest))
                Dim toPrincipal = pay - interest
                balance -= toPrincipal
                totalInterest += interest
                totalPaid += pay
                If m <= show OrElse m = months Then
                    If m = months AndAlso m > show + 1 Then rows.Add(New List(Of String) From {"...", "", "", "", ""})
                    rows.Add(New List(Of String) From {m.ToString(CultureInfo.InvariantCulture), Money(pay), Money(interest), Money(toPrincipal), Money(Math.Max(0D, balance))})
                End If
            Next
            Return $"Monthly payment: {Money(payment)} for {months} months ({(months / 12.0).ToString("0.##", CultureInfo.InvariantCulture)} years) at {annual_rate.ToString("0.####", CultureInfo.InvariantCulture)}% a year." & vbLf &
                   $"Total paid {Money(totalPaid)}: principal {Money(principal)} + interest {Money(totalInterest)} ({Fmt(Math.Round(totalInterest / principal * 100D, 2))}% of the principal)." & vbLf &
                   TableText.ToMarkdown(rows, 700)
        End Function

        Friend Function CashFlows(<Description("Discount rate per period in percent, e.g. 8")> rate As Double,
                                  <Description("Cash flows per period, first = now (e.g. '-10000, 3000, 4200, 6800')")> flows As String) As String
            Dim cf = ParseNumbers(If(flows, "").Replace(",", " ")).Select(Function(d) CDbl(d)).ToList()
            If cf.Count < 2 Then Throw New McpException("Give at least two cash flows, e.g. '-1000 300 400 500'.")
            Dim npvAt = Function(r As Double) cf.Select(Function(c, t) c / Math.Pow(1 + r, t)).Sum()
            Dim npv = npvAt(rate / 100)
            Dim sb As New StringBuilder($"NPV at {rate.ToString("0.####", CultureInfo.InvariantCulture)}%: {Money(CDec(npv))}" & vbLf)
            ' IRR: the rate where NPV is 0, by bisection over a wide bracket (needs a sign change in the flows).
            Dim irrText As String
            If Not (cf.Any(Function(c) c < 0) AndAlso cf.Any(Function(c) c > 0)) Then
                irrText = "undefined (the flows need both a negative and a positive value)"
            Else
                Dim lo = -0.9999, hi = 10.0
                Dim fLo = npvAt(lo), fHi = npvAt(hi)
                If Math.Sign(fLo) = Math.Sign(fHi) Then
                    irrText = "not found between -99.99% and 1000%"
                Else
                    For i = 1 To 300
                        Dim mid = (lo + hi) / 2
                        Dim fMid = npvAt(mid)
                        If Math.Sign(fMid) = Math.Sign(fLo) Then lo = mid : fLo = fMid Else hi = mid
                    Next
                    irrText = FmtD(Math.Round((lo + hi) / 2 * 100, 4)) & "% per period"
                    Dim signChanges = Enumerable.Range(1, cf.Count - 1).Count(Function(i) Math.Sign(cf(i)) <> Math.Sign(cf(i - 1)) AndAlso cf(i) <> 0)
                    If signChanges > 1 Then irrText &= " (the flows change sign more than once: other IRRs may exist)"
                End If
            End If
            sb.AppendLine("IRR: " & irrText)
            Dim cumulative = 0.0, payback As String = "never (the flows do not pay back)"
            For t = 0 To cf.Count - 1
                Dim before = cumulative
                cumulative += cf(t)
                If before < 0 AndAlso cumulative >= 0 Then
                    payback = $"{FmtD(Math.Round(t - 1 + (-before / cf(t)), 2))} periods"
                    Exit For
                End If
            Next
            sb.AppendLine("Payback: " & payback)
            sb.Append($"Total of the flows (undiscounted): {Money(CDec(cf.Sum()))}")
            Return sb.ToString()
        End Function
    End Class

End Namespace
