Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>The data server's charts (SVG drawn here; PNG through Svg.Skia) and Mermaid diagrams (mermaid-cli with the installed Chrome).</summary>
    Partial Public NotInheritable Class DataServer

        Private Sub AddChartTools(tools As McpServerPrimitiveCollection(Of McpServerTool))
            ToolKit.Add(tools, New Func(Of String, String, String, String, String, String, Integer, Integer, String)(AddressOf MakeChart), "make_chart",
                "Draw a chart from a table (CSV/JSON/YAML text or file) and save it as .svg or .png: type bar, hbar, line, area, scatter or pie; " &
                "x: the label column; y: value columns (comma-separated; default every numeric column).", False)
            ToolKit.Add(tools, New Func(Of String, String, String, String, CancellationToken, Task(Of String))(AddressOf RenderMermaidAsync), "render_mermaid",
                "Render a Mermaid diagram (flowchart, sequence, class, state, ER, gantt, pie, mindmap, timeline, ...) to .svg or .png.", False)
        End Sub

        Private Shared ReadOnly Palette As String() = {"#4F46E5", "#0EA5E9", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6", "#EC4899", "#14B8A6", "#84CC16", "#F97316"}

        Private Shared Function SvgText(s As String) As String
            Return Net.WebUtility.HtmlEncode(If(s, ""))
        End Function

        Private Shared Function N(d As Double) As String
            Return Math.Round(d, 2).ToString("0.##", CultureInfo.InvariantCulture)
        End Function

        ''' <summary>A "nice" step for about count ticks between lo and hi (1, 2, 5 x 10^n).</summary>
        Friend Shared Function NiceStep(lo As Double, hi As Double, count As Integer) As Double
            Dim range = Math.Max(hi - lo, 1.0E-9)
            Dim raw = range / Math.Max(1, count)
            Dim mag = Math.Pow(10, Math.Floor(Math.Log10(raw)))
            Dim f = raw / mag
            Return If(f <= 1, 1, If(f <= 2, 2, If(f <= 5, 5, 10))) * mag
        End Function

        Private Shared Function TickLabel(v As Double) As String
            Dim a = Math.Abs(v)
            If a >= 1000000000.0 Then Return N(v / 1000000000.0) & "B"
            If a >= 1000000.0 Then Return N(v / 1000000.0) & "M"
            If a >= 10000.0 Then Return N(v / 1000.0) & "k"
            Return v.ToString("0.###", CultureInfo.InvariantCulture)
        End Function

        ''' <summary>The chart as SVG text.</summary>
        Friend Shared Function ChartSvg(type As String, title As String, labels As List(Of String), series As List(Of (Name As String, Values As List(Of Double?))),
                                        width As Integer, height As Integer, xValues As List(Of Double?)) As String
            Dim sb As New StringBuilder()
            sb.Append($"<svg xmlns=""http://www.w3.org/2000/svg"" width=""{width}"" height=""{height}"" viewBox=""0 0 {width} {height}"" font-family=""Segoe UI, Helvetica, Arial, sans-serif"">")
            sb.Append($"<rect width=""{width}"" height=""{height}"" fill=""#ffffff""/>")
            Dim top = 24.0
            If Not String.IsNullOrWhiteSpace(title) Then
                sb.Append($"<text x=""{width / 2}"" y=""30"" text-anchor=""middle"" font-size=""18"" font-weight=""600"" fill=""#111827"">{SvgText(title)}</text>")
                top = 50
            End If
            Dim showLegend = series.Count > 1 OrElse type = "pie"
            If type = "pie" Then
                Dim vals = series(0).Values.Select(Function(v) Math.Max(0, If(v, 0))).ToList()
                Dim total = vals.Sum()
                If total <= 0 Then Throw New McpException("A pie chart needs positive values.")
                Dim cx = width * 0.36, cy = top + (height - top) / 2, r = Math.Min(width * 0.3, (height - top) / 2 - 16)
                Dim angle = -Math.PI / 2
                For i = 0 To vals.Count - 1
                    Dim sweep = vals(i) / total * 2 * Math.PI
                    Dim color = Palette(i Mod Palette.Length)
                    If vals(i) >= total Then
                        sb.Append($"<circle cx=""{N(cx)}"" cy=""{N(cy)}"" r=""{N(r)}"" fill=""{color}""/>")
                    ElseIf vals(i) > 0 Then
                        Dim x1 = cx + r * Math.Cos(angle), y1 = cy + r * Math.Sin(angle)
                        Dim x2 = cx + r * Math.Cos(angle + sweep), y2 = cy + r * Math.Sin(angle + sweep)
                        sb.Append($"<path d=""M{N(cx)},{N(cy)} L{N(x1)},{N(y1)} A{N(r)},{N(r)} 0 {If(sweep > Math.PI, 1, 0)} 1 {N(x2)},{N(y2)} Z"" fill=""{color}"" stroke=""#fff"" stroke-width=""2""/>")
                        If sweep > 0.25 Then
                            Dim mid = angle + sweep / 2
                            sb.Append($"<text x=""{N(cx + r * 0.65 * Math.Cos(mid))}"" y=""{N(cy + r * 0.65 * Math.Sin(mid) + 4)}"" text-anchor=""middle"" font-size=""12"" font-weight=""600"" fill=""#fff"">{N(vals(i) / total * 100)}%</text>")
                        End If
                    End If
                    angle += sweep
                Next
                Dim ly = top + 10
                For i = 0 To labels.Count - 1
                    If ly > height - 14 Then Exit For
                    sb.Append($"<rect x=""{N(width * 0.7)}"" y=""{N(ly)}"" width=""12"" height=""12"" rx=""2"" fill=""{Palette(i Mod Palette.Length)}""/>")
                    sb.Append($"<text x=""{N(width * 0.7 + 18)}"" y=""{N(ly + 10)}"" font-size=""12"" fill=""#374151"">{SvgText(ToolKit.Clip(labels(i), 28))} ({N(vals(i) / total * 100)}%)</text>")
                    ly += 20
                Next
                Return sb.Append("</svg>").ToString()
            End If

            Dim horizontal = type = "hbar"
            Dim allValues = series.SelectMany(Function(s) s.Values).Where(Function(v) v.HasValue).Select(Function(v) v.Value).ToList()
            If allValues.Count = 0 Then Throw New McpException("No numbers to chart.")
            Dim lo = Math.Min(0, allValues.Min()), hi = Math.Max(0, allValues.Max())
            If type = "line" OrElse type = "scatter" Then lo = allValues.Min() : hi = allValues.Max()
            If hi = lo Then hi = lo + 1
            Dim stepV = NiceStep(lo, hi, 6)
            lo = Math.Floor(lo / stepV) * stepV : hi = Math.Ceiling(hi / stepV) * stepV
            Dim longest = labels.DefaultIfEmpty("").Max(Function(l) l.Length)
            Dim rotate = Not horizontal AndAlso (labels.Count > 8 OrElse longest > 10)
            Dim left = If(horizontal, Math.Min(200, 14 + Math.Min(28, longest) * 6.5), 64.0)
            Dim right = If(showLegend, 150.0, 24.0)
            Dim bottom = If(horizontal, 40.0, If(rotate, 30 + Math.Min(28, longest) * 5.2, 44.0))
            Dim plotW = width - left - right, plotH = height - top - bottom
            If plotW < 100 OrElse plotH < 80 Then Throw New McpException("The chart is too small for its labels; make it larger.")
            Dim scaleV = Function(v As Double) If(horizontal, left + (v - lo) / (hi - lo) * plotW, top + plotH - (v - lo) / (hi - lo) * plotH)
            ' Grid and value axis.
            Dim t = lo
            While t <= hi + stepV / 2
                Dim p = scaleV(t)
                If horizontal Then
                    sb.Append($"<line x1=""{N(p)}"" y1=""{N(top)}"" x2=""{N(p)}"" y2=""{N(top + plotH)}"" stroke=""#e5e7eb""/>")
                    sb.Append($"<text x=""{N(p)}"" y=""{N(top + plotH + 18)}"" text-anchor=""middle"" font-size=""11"" fill=""#6b7280"">{TickLabel(t)}</text>")
                Else
                    sb.Append($"<line x1=""{N(left)}"" y1=""{N(p)}"" x2=""{N(left + plotW)}"" y2=""{N(p)}"" stroke=""#e5e7eb""/>")
                    sb.Append($"<text x=""{N(left - 8)}"" y=""{N(p + 4)}"" text-anchor=""end"" font-size=""11"" fill=""#6b7280"">{TickLabel(t)}</text>")
                End If
                t += stepV
            End While
            Dim zero = scaleV(Math.Max(lo, Math.Min(hi, 0)))
            sb.Append(If(horizontal, $"<line x1=""{N(zero)}"" y1=""{N(top)}"" x2=""{N(zero)}"" y2=""{N(top + plotH)}"" stroke=""#9ca3af""/>",
                                     $"<line x1=""{N(left)}"" y1=""{N(zero)}"" x2=""{N(left + plotW)}"" y2=""{N(zero)}"" stroke=""#9ca3af""/>"))
            Dim count = labels.Count
            If type = "scatter" AndAlso xValues IsNot Nothing Then
                Dim xs = xValues.Where(Function(v) v.HasValue).Select(Function(v) v.Value).ToList()
                Dim xlo = xs.Min(), xhi = xs.Max()
                If xhi = xlo Then xhi = xlo + 1
                Dim xstep = NiceStep(xlo, xhi, 6)
                xlo = Math.Floor(xlo / xstep) * xstep : xhi = Math.Ceiling(xhi / xstep) * xstep
                Dim sx = Function(v As Double) left + (v - xlo) / (xhi - xlo) * plotW
                Dim xt = xlo
                While xt <= xhi + xstep / 2
                    sb.Append($"<text x=""{N(sx(xt))}"" y=""{N(top + plotH + 18)}"" text-anchor=""middle"" font-size=""11"" fill=""#6b7280"">{TickLabel(xt)}</text>")
                    xt += xstep
                End While
                For si = 0 To series.Count - 1
                    For i = 0 To count - 1
                        If Not series(si).Values(i).HasValue OrElse Not xValues(i).HasValue Then Continue For
                        sb.Append($"<circle cx=""{N(sx(xValues(i).Value))}"" cy=""{N(scaleV(series(si).Values(i).Value))}"" r=""4.5"" fill=""{Palette(si Mod Palette.Length)}"" fill-opacity=""0.8""/>")
                    Next
                Next
            Else
                Dim band = If(horizontal, plotH, plotW) / Math.Max(1, count)
                For i = 0 To count - 1
                    Dim center = If(horizontal, top, left) + band * (i + 0.5)
                    If horizontal Then
                        sb.Append($"<text x=""{N(left - 8)}"" y=""{N(center + 4)}"" text-anchor=""end"" font-size=""11"" fill=""#374151"">{SvgText(ToolKit.Clip(labels(i), 28))}</text>")
                    ElseIf count <= 40 OrElse i Mod CInt(Math.Ceiling(count / 40.0)) = 0 Then
                        If rotate Then
                            sb.Append($"<text transform=""translate({N(center)},{N(top + plotH + 12)}) rotate(-40)"" text-anchor=""end"" font-size=""11"" fill=""#374151"">{SvgText(ToolKit.Clip(labels(i), 28))}</text>")
                        Else
                            sb.Append($"<text x=""{N(center)}"" y=""{N(top + plotH + 18)}"" text-anchor=""middle"" font-size=""11"" fill=""#374151"">{SvgText(labels(i))}</text>")
                        End If
                    End If
                Next
                If type = "bar" OrElse type = "hbar" Then
                    Dim groupW = band * 0.78
                    Dim barW = groupW / series.Count
                    For si = 0 To series.Count - 1
                        For i = 0 To count - 1
                            Dim v = series(si).Values(i)
                            If Not v.HasValue Then Continue For
                            Dim start = If(horizontal, top, left) + band * i + (band - groupW) / 2 + barW * si
                            Dim p = scaleV(v.Value)
                            Dim a = Math.Min(p, zero), len = Math.Abs(p - zero)
                            If horizontal Then
                                sb.Append($"<rect x=""{N(a)}"" y=""{N(start)}"" width=""{N(len)}"" height=""{N(barW - 1)}"" rx=""2"" fill=""{Palette(si Mod Palette.Length)}""/>")
                            Else
                                sb.Append($"<rect x=""{N(start)}"" y=""{N(a)}"" width=""{N(barW - 1)}"" height=""{N(len)}"" rx=""2"" fill=""{Palette(si Mod Palette.Length)}""/>")
                            End If
                            If count * series.Count <= 24 Then
                                If horizontal Then
                                    sb.Append($"<text x=""{N(If(v.Value >= 0, a + len + 4, a - 4))}"" y=""{N(start + barW / 2 + 4)}"" text-anchor=""{If(v.Value >= 0, "start", "end")}"" font-size=""10.5"" fill=""#374151"">{TickLabel(v.Value)}</text>")
                                Else
                                    sb.Append($"<text x=""{N(start + barW / 2)}"" y=""{N(If(v.Value >= 0, a - 4, a + len + 12))}"" text-anchor=""middle"" font-size=""10.5"" fill=""#374151"">{TickLabel(v.Value)}</text>")
                                End If
                            End If
                        Next
                    Next
                Else
                    For si = 0 To series.Count - 1
                        Dim pts = Enumerable.Range(0, count).Where(Function(i) series(si).Values(i).HasValue).
                            Select(Function(i) (X:=left + band * (i + 0.5), Y:=scaleV(series(si).Values(i).Value))).ToList()
                        If pts.Count = 0 Then Continue For
                        Dim color = Palette(si Mod Palette.Length)
                        Dim d = "M" & String.Join(" L", pts.Select(Function(p) $"{N(p.X)},{N(p.Y)}"))
                        If type = "area" Then sb.Append($"<path d=""{d} L{N(pts.Last().X)},{N(zero)} L{N(pts(0).X)},{N(zero)} Z"" fill=""{color}"" fill-opacity=""0.18""/>")
                        sb.Append($"<path d=""{d}"" fill=""none"" stroke=""{color}"" stroke-width=""2.5"" stroke-linejoin=""round""/>")
                        If pts.Count <= 60 Then
                            For Each p In pts
                                sb.Append($"<circle cx=""{N(p.X)}"" cy=""{N(p.Y)}"" r=""3.2"" fill=""#fff"" stroke=""{color}"" stroke-width=""2""/>")
                            Next
                        End If
                    Next
                End If
            End If
            If showLegend Then
                Dim ly = top + 4
                For si = 0 To series.Count - 1
                    sb.Append($"<rect x=""{N(width - right + 16)}"" y=""{N(ly)}"" width=""12"" height=""12"" rx=""2"" fill=""{Palette(si Mod Palette.Length)}""/>")
                    sb.Append($"<text x=""{N(width - right + 34)}"" y=""{N(ly + 10)}"" font-size=""12"" fill=""#374151"">{SvgText(ToolKit.Clip(series(si).Name, 18))}</text>")
                    ly += 20
                Next
            End If
            Return sb.Append("</svg>").ToString()
        End Function

        ''' <summary>An SVG drawn into a PNG file at twice its size (sharp on high-density screens).</summary>
        Friend Shared Sub SvgToPng(svgText As String, outputPath As String, Optional scale As Single = 2.0F)
            Using svg As New Svg.Skia.SKSvg()
                Dim picture = svg.FromSvg(svgText)
                If picture Is Nothing Then Throw New McpException("The drawing could not be rendered to PNG.")
                Dim bounds = picture.CullRect
                Dim w = Math.Max(1, CInt(Math.Ceiling(bounds.Width * scale))), h = Math.Max(1, CInt(Math.Ceiling(bounds.Height * scale)))
                Using bmp As New SkiaSharp.SKBitmap(w, h)
                    Using canvas As New SkiaSharp.SKCanvas(bmp)
                        canvas.Clear(SkiaSharp.SKColors.White)
                        canvas.Scale(scale)
                        canvas.DrawPicture(picture)
                    End Using
                    Using img = SkiaSharp.SKImage.FromBitmap(bmp)
                        Using data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100)
                            File.WriteAllBytes(outputPath, data.ToArray())
                        End Using
                    End Using
                End Using
            End Using
        End Sub

        Friend Function MakeChart(<Description("A table: CSV/JSON/YAML text or a file path")> source As String,
                                  <Description("Output file: .svg or .png")> output_path As String,
                                  <Description("bar (default), hbar, line, area, scatter or pie")> Optional type As String = "bar",
                                  <Description("Label column (default: the first column)")> Optional x As String = "",
                                  <Description("Value columns, comma-separated (default: every numeric column)")> Optional y As String = "",
                                  <Description("Chart title (optional)")> Optional title As String = "",
                                  <Description("Width in pixels (default 800)")> Optional width As Integer = 800,
                                  <Description("Height in pixels (default 480)")> Optional height As Integer = 480) As String
            Dim kind = If(String.IsNullOrWhiteSpace(type), "bar", type.Trim().ToLowerInvariant())
            If Not {"bar", "hbar", "line", "area", "scatter", "pie"}.Contains(kind) Then Throw New McpException("type must be bar, hbar, line, area, scatter or pie.")
            Dim rows = TableFrom(source)
            If rows.Count = 0 Then Throw New McpException("The table has no rows.")
            If rows.Count > 2000 Then Throw New McpException("At most 2000 rows can be charted; aggregate them first.")
            Dim columns = rows.SelectMany(Function(r) r.Select(Function(kv) kv.Key)).Distinct().ToList()
            Dim xCol = If(String.IsNullOrWhiteSpace(x), columns(0), x.Trim())
            If Not columns.Contains(xCol) Then Throw New McpException($"No column '{xCol}'. Columns: {String.Join(", ", columns)}.")
            Dim yCols = If(String.IsNullOrWhiteSpace(y), columns.Where(Function(c) c <> xCol AndAlso rows.Any(Function(r) r.ContainsKey(c) AndAlso NumberOf(r(c)).HasValue)).ToList(), ToolKit.SplitList(y))
            If yCols.Count = 0 Then Throw New McpException("No numeric columns to chart; name them in y.")
            For Each c In yCols
                If Not columns.Contains(c) Then Throw New McpException($"No column '{c}'. Columns: {String.Join(", ", columns)}.")
            Next
            If kind = "pie" Then yCols = yCols.Take(1).ToList()
            Dim labels = rows.Select(Function(r) If(r.ContainsKey(xCol), Plain(r(xCol)), "")).ToList()
            Dim series = yCols.Select(Function(c) (Name:=c, Values:=rows.Select(Function(r) If(r.ContainsKey(c), CType(If(NumberOf(r(c)).HasValue, CDbl(NumberOf(r(c)).Value), CType(Nothing, Double?)), Double?), Nothing)).ToList())).ToList()
            Dim xValues = If(kind = "scatter", rows.Select(Function(r) If(r.ContainsKey(xCol) AndAlso NumberOf(r(xCol)).HasValue, CDbl(NumberOf(r(xCol)).Value), CType(Nothing, Double?))).ToList(), Nothing)
            If kind = "scatter" AndAlso xValues.All(Function(v) Not v.HasValue) Then Throw New McpException($"A scatter chart needs numbers in the x column '{xCol}'.")
            Dim svgText = ChartSvg(kind, title, labels, series, Math.Clamp(width, 300, 4000), Math.Clamp(height, 200, 3000), xValues)
            Dim full = _roots.Resolve(output_path)
            Directory.CreateDirectory(Path.GetDirectoryName(full))
            Select Case Path.GetExtension(full).ToLowerInvariant()
                Case ".svg" : File.WriteAllText(full, svgText, New UTF8Encoding(False))
                Case ".png" : SvgToPng(svgText, full)
                Case Else : Throw New McpException("output_path must end in .svg or .png.")
            End Select
            Return $"Saved a {kind} chart of {String.Join(", ", yCols)} by {xCol} ({rows.Count} rows) to {_roots.Show(full)} ({New FileInfo(full).Length:N0} bytes)."
        End Function

        Friend Async Function RenderMermaidAsync(<Description("Mermaid diagram code, e.g. 'graph TD; A-->B'")> code As String,
                                                 <Description("Output file: .svg or .png")> output_path As String,
                                                 <Description("default, dark, forest or neutral")> Optional theme As String = "default",
                                                 <Description("Background: white (default) or transparent")> Optional background As String = "white",
                                                 Optional ct As CancellationToken = Nothing) As Task(Of String)
            If String.IsNullOrWhiteSpace(code) Then Throw New McpException("code is required.")
            Dim full = _roots.Resolve(output_path)
            Dim ext = Path.GetExtension(full).ToLowerInvariant()
            If ext <> ".svg" AndAlso ext <> ".png" Then Throw New McpException("output_path must end in .svg or .png.")
            Dim data As Byte()
            Try
                data = Await New MermaidCli(_node, _mermaidCli, _chrome).RenderAsync(code, ext, theme, background, ct).ConfigureAwait(False)
            Catch ex As MermaidException
                Throw New McpException(ex.Message)
            End Try
            Directory.CreateDirectory(Path.GetDirectoryName(full))
            File.WriteAllBytes(full, data)
            Return $"Saved the diagram to {_roots.Show(full)} ({New FileInfo(full).Length:N0} bytes)."
        End Function
    End Class

End Namespace
