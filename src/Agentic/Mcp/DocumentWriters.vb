Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions
Imports ModelContextProtocol

Namespace Mcp

    ''' <summary>Writes .docx and .xlsx files (Office Open XML) directly: no Office, no SDK.</summary>
    Friend NotInheritable Class OfficeWriter
        Private Sub New()
        End Sub

        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        ''' <summary>Text safe inside XML (characters XML does not allow are dropped).</summary>
        Friend Shared Function X(s As String) As String
            Dim sb As New StringBuilder(If(s, "").Length)
            For Each c In If(s, "")
                Select Case c
                    Case "&"c : sb.Append("&amp;")
                    Case "<"c : sb.Append("&lt;")
                    Case ">"c : sb.Append("&gt;")
                    Case """"c : sb.Append("&quot;")
                    Case Else
                        If c = ControlChars.Tab OrElse c = ControlChars.Lf OrElse c = ControlChars.Cr OrElse (c >= " "c AndAlso c <> ChrW(&HFFFE) AndAlso c <> ChrW(&HFFFF)) Then sb.Append(c)
                End Select
            Next
            Return sb.ToString()
        End Function

        Private Shared Sub Put(zip As ZipArchive, name As String, content As String)
            Dim e = zip.CreateEntry(name, CompressionLevel.Optimal)
            Using w As New StreamWriter(e.Open(), New UTF8Encoding(False))
                w.Write(content)
            End Using
        End Sub

        Private Shared Function CoreProps(title As String, author As String) As String
            Dim now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", Inv)
            Return "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><cp:coreProperties xmlns:cp=""http://schemas.openxmlformats.org/package/2006/metadata/core-properties"" " &
                   "xmlns:dc=""http://purl.org/dc/elements/1.1/"" xmlns:dcterms=""http://purl.org/dc/terms/"" xmlns:xsi=""http://www.w3.org/2001/XMLSchema-instance"">" &
                   $"<dc:title>{X(title)}</dc:title><dc:creator>{X(If(String.IsNullOrWhiteSpace(author), "Agentic", author))}</dc:creator>" &
                   $"<dcterms:created xsi:type=""dcterms:W3CDTF"">{now}</dcterms:created><dcterms:modified xsi:type=""dcterms:W3CDTF"">{now}</dcterms:modified></cp:coreProperties>"
        End Function

        ''' <summary>Writes to a temporary file first, so a failure never leaves half a document behind.</summary>
        Private Shared Sub WriteZip(full As String, fill As Action(Of ZipArchive))
            Dim tmp = full & ".tmp-" & Guid.NewGuid().ToString("N").Substring(0, 8)
            Try
                Using fs As New FileStream(tmp, FileMode.CreateNew)
                    Using zip As New ZipArchive(fs, ZipArchiveMode.Create)
                        fill(zip)
                    End Using
                End Using
                File.Move(tmp, full, overwrite:=True)
            Finally
                If File.Exists(tmp) Then File.Delete(tmp)
            End Try
        End Sub

        ' ---------------------------------------------------------------- Word

        Public Shared Sub WriteDocx(full As String, blocks As List(Of MarkdownBlocks.Block), title As String, author As String)
            Dim body As New StringBuilder()
            If Not String.IsNullOrWhiteSpace(title) Then body.Append($"<w:p><w:pPr><w:pStyle w:val=""Title""/></w:pPr>{Runs(title.Trim(), False)}</w:p>")
            Dim numberedLists = 0
            Dim prevKind As MarkdownBlocks.Kind? = Nothing
            For Each b In blocks
                Select Case b.Kind
                    Case MarkdownBlocks.Kind.Heading
                        body.Append($"<w:p><w:pPr><w:pStyle w:val=""Heading{Math.Min(b.Level, 4)}""/></w:pPr>{Runs(b.Text, False)}</w:p>")
                    Case MarkdownBlocks.Kind.Paragraph
                        body.Append($"<w:p>{Runs(b.Text, False)}</w:p>")
                    Case MarkdownBlocks.Kind.Bullet
                        body.Append($"<w:p><w:pPr><w:pStyle w:val=""ListParagraph""/><w:numPr><w:ilvl w:val=""{Math.Min(b.Level, 2)}""/><w:numId w:val=""1""/></w:numPr></w:pPr>{Runs(b.Text, False)}</w:p>")
                    Case MarkdownBlocks.Kind.Numbered
                        If Not prevKind.HasValue OrElse prevKind.Value <> MarkdownBlocks.Kind.Numbered Then numberedLists += 1
                        body.Append($"<w:p><w:pPr><w:pStyle w:val=""ListParagraph""/><w:numPr><w:ilvl w:val=""{Math.Min(b.Level, 2)}""/><w:numId w:val=""{1 + numberedLists}""/></w:numPr></w:pPr>{Runs(b.Text, False)}</w:p>")
                    Case MarkdownBlocks.Kind.Code
                        For Each line In b.Text.Split(ControlChars.Lf)
                            body.Append($"<w:p><w:pPr><w:pStyle w:val=""Code""/></w:pPr><w:r><w:t xml:space=""preserve"">{X(line)}</w:t></w:r></w:p>")
                        Next
                    Case MarkdownBlocks.Kind.Rule
                        body.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=""single"" w:sz=""6"" w:space=""1"" w:color=""999999""/></w:pBdr></w:pPr></w:p>")
                    Case MarkdownBlocks.Kind.PageBreak
                        body.Append("<w:p><w:r><w:br w:type=""page""/></w:r></w:p>")
                    Case MarkdownBlocks.Kind.Table
                        body.Append(Table(b.Rows))
                End Select
                prevKind = b.Kind
            Next
            body.Append("<w:sectPr><w:pgSz w:w=""11906"" w:h=""16838""/><w:pgMar w:top=""1134"" w:right=""1134"" w:bottom=""1134"" w:left=""1134"" w:header=""567"" w:footer=""567"" w:gutter=""0""/></w:sectPr>")
            Dim document = "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><w:document xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"" " &
                           "xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""><w:body>" & body.ToString() & "</w:body></w:document>"
            WriteZip(full, Sub(zip)
                               Put(zip, "[Content_Types].xml", "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" &
                                   "<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/><Default Extension=""xml"" ContentType=""application/xml""/>" &
                                   "<Override PartName=""/word/document.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml""/>" &
                                   "<Override PartName=""/word/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml""/>" &
                                   "<Override PartName=""/word/numbering.xml"" ContentType=""application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml""/>" &
                                   "<Override PartName=""/docProps/core.xml"" ContentType=""application/vnd.openxmlformats-package.core-properties+xml""/></Types>")
                               Put(zip, "_rels/.rels", "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" &
                                   "<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""word/document.xml""/>" &
                                   "<Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"" Target=""docProps/core.xml""/></Relationships>")
                               Put(zip, "word/_rels/document.xml.rels", "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" &
                                   "<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/>" &
                                   "<Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/numbering"" Target=""numbering.xml""/></Relationships>")
                               Put(zip, "word/document.xml", document)
                               Put(zip, "word/styles.xml", Styles())
                               Put(zip, "word/numbering.xml", Numbering(numberedLists))
                               Put(zip, "docProps/core.xml", CoreProps(title, author))
                           End Sub)
        End Sub

        Private Shared Function Runs(text As String, bold As Boolean) As String
            Dim sb As New StringBuilder()
            For Each s In MarkdownBlocks.Spans(text)
                Dim props = If(bold OrElse s.Bold, "<w:b/>", "") & If(s.Italic, "<w:i/>", "") & If(s.Code, "<w:rFonts w:ascii=""Consolas"" w:hAnsi=""Consolas""/><w:shd w:val=""clear"" w:fill=""F2F2F2""/>", "")
                sb.Append($"<w:r>{If(props.Length > 0, "<w:rPr>" & props & "</w:rPr>", "")}<w:t xml:space=""preserve"">{X(s.Text)}</w:t></w:r>")
            Next
            Return sb.ToString()
        End Function

        Private Shared Function Table(rows As List(Of List(Of String))) As String
            If rows Is Nothing OrElse rows.Count = 0 Then Return ""
            Dim cols = rows.Max(Function(r) r.Count)
            Dim sb As New StringBuilder("<w:tbl><w:tblPr><w:tblStyle w:val=""TableGrid""/><w:tblW w:w=""5000"" w:type=""pct""/><w:tblLook w:val=""04A0"" w:firstRow=""1""/></w:tblPr><w:tblGrid>")
            For c = 1 To cols
                sb.Append("<w:gridCol/>")
            Next
            sb.Append("</w:tblGrid>")
            For r = 0 To rows.Count - 1
                sb.Append("<w:tr>" & If(r = 0, "<w:trPr><w:tblHeader/></w:trPr>", ""))
                For c = 0 To cols - 1
                    Dim text = If(c < rows(r).Count, rows(r)(c), "")
                    sb.Append($"<w:tc><w:tcPr>{If(r = 0, "<w:shd w:val=""clear"" w:fill=""E8E8E8""/>", "")}</w:tcPr><w:p>{Runs(text, r = 0)}</w:p></w:tc>")
                Next
                sb.Append("</w:tr>")
            Next
            Return sb.Append("</w:tbl><w:p/>").ToString()
        End Function

        Private Shared Function Styles() As String
            Dim heading = Function(n As Integer, size As Integer, color As String) $"<w:style w:type=""paragraph"" w:styleId=""Heading{n}""><w:name w:val=""heading {n}""/><w:basedOn w:val=""Normal""/><w:next w:val=""Normal""/><w:qFormat/>" &
                $"<w:pPr><w:keepNext/><w:spacing w:before=""{If(n = 1, 360, 240)}"" w:after=""120""/><w:outlineLvl w:val=""{n - 1}""/></w:pPr><w:rPr><w:b/><w:color w:val=""{color}""/><w:sz w:val=""{size}""/></w:rPr></w:style>"
            Return "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><w:styles xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">" &
                   "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=""Calibri"" w:hAnsi=""Calibri"" w:eastAsia=""Calibri"" w:cs=""Calibri""/><w:sz w:val=""22""/><w:lang w:val=""en-US""/></w:rPr></w:rPrDefault>" &
                   "<w:pPrDefault><w:pPr><w:spacing w:after=""140"" w:line=""276"" w:lineRule=""auto""/></w:pPr></w:pPrDefault></w:docDefaults>" &
                   "<w:style w:type=""paragraph"" w:default=""1"" w:styleId=""Normal""><w:name w:val=""Normal""/><w:qFormat/></w:style>" &
                   "<w:style w:type=""paragraph"" w:styleId=""Title""><w:name w:val=""Title""/><w:basedOn w:val=""Normal""/><w:next w:val=""Normal""/><w:qFormat/><w:pPr><w:spacing w:after=""240""/></w:pPr><w:rPr><w:b/><w:color w:val=""1F3864""/><w:sz w:val=""44""/></w:rPr></w:style>" &
                   heading(1, 32, "1F3864") & heading(2, 28, "2E74B5") & heading(3, 24, "2E74B5") & heading(4, 22, "404040") &
                   "<w:style w:type=""paragraph"" w:styleId=""ListParagraph""><w:name w:val=""List Paragraph""/><w:basedOn w:val=""Normal""/><w:qFormat/><w:pPr><w:spacing w:after=""60""/><w:ind w:left=""720""/><w:contextualSpacing/></w:pPr></w:style>" &
                   "<w:style w:type=""paragraph"" w:styleId=""Code""><w:name w:val=""Code""/><w:basedOn w:val=""Normal""/><w:pPr><w:spacing w:after=""0"" w:line=""240"" w:lineRule=""auto""/><w:shd w:val=""clear"" w:fill=""F2F2F2""/></w:pPr><w:rPr><w:rFonts w:ascii=""Consolas"" w:hAnsi=""Consolas""/><w:sz w:val=""19""/></w:rPr></w:style>" &
                   "<w:style w:type=""table"" w:styleId=""TableGrid""><w:name w:val=""Table Grid""/><w:tblPr><w:tblBorders><w:top w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""A6A6A6""/><w:left w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""A6A6A6""/>" &
                   "<w:bottom w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""A6A6A6""/><w:right w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""A6A6A6""/><w:insideH w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""A6A6A6""/>" &
                   "<w:insideV w:val=""single"" w:sz=""4"" w:space=""0"" w:color=""A6A6A6""/></w:tblBorders><w:tblCellMar><w:left w:w=""100"" w:type=""dxa""/><w:right w:w=""100"" w:type=""dxa""/></w:tblCellMar></w:tblPr>" &
                   "<w:pPr><w:spacing w:after=""0""/></w:pPr></w:style></w:styles>"
        End Function

        Private Shared Function Numbering(numberedLists As Integer) As String
            Dim lvl = Function(i As Integer, fmt As String, text As String) $"<w:lvl w:ilvl=""{i}""><w:start w:val=""1""/><w:numFmt w:val=""{fmt}""/><w:lvlText w:val=""{text}""/><w:lvlJc w:val=""left""/><w:pPr><w:ind w:left=""{720 + 360 * i}"" w:hanging=""360""/></w:pPr></w:lvl>"
            Dim sb As New StringBuilder("<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><w:numbering xmlns:w=""http://schemas.openxmlformats.org/wordprocessingml/2006/main"">")
            sb.Append("<w:abstractNum w:abstractNumId=""0"">" & lvl(0, "bullet", "•") & lvl(1, "bullet", "◦") & lvl(2, "bullet", "▪") & "</w:abstractNum>")
            sb.Append("<w:abstractNum w:abstractNumId=""1"">" & lvl(0, "decimal", "%1.") & lvl(1, "lowerLetter", "%2.") & lvl(2, "lowerRoman", "%3.") & "</w:abstractNum>")
            sb.Append("<w:num w:numId=""1""><w:abstractNumId w:val=""0""/></w:num>")
            For n = 1 To Math.Max(1, numberedLists)
                sb.Append($"<w:num w:numId=""{1 + n}""><w:abstractNumId w:val=""1""/><w:lvlOverride w:ilvl=""0""><w:startOverride w:val=""1""/></w:lvlOverride></w:num>")
            Next
            Return sb.Append("</w:numbering>").ToString()
        End Function

        ' ---------------------------------------------------------------- Excel

        Private Shared ReadOnly NumberCell As New Regex("^-?(0|[1-9]\d{0,14})(\.\d+)?([eE][+-]?\d+)?$", RegexOptions.Compiled)
        Private Shared ReadOnly DateCell As New Regex("^\d{4}-\d{2}-\d{2}([T ]\d{2}:\d{2}(:\d{2})?)?$", RegexOptions.Compiled)

        Friend Shared Function ColumnName(index As Integer) As String
            Dim n = index + 1
            Dim s = ""
            While n > 0
                Dim m = (n - 1) Mod 26
                s = ChrW(AscW("A"c) + m) & s
                n = (n - 1) \ 26
            End While
            Return s
        End Function

        Private Shared Function SheetName(name As String, used As HashSet(Of String)) As String
            Dim n = Regex.Replace(If(name, "Sheet"), "[\[\]:*?/\\]", "_").Trim("'"c).Trim()
            If n.Length = 0 Then n = "Sheet"
            If n.Length > 31 Then n = n.Substring(0, 31)
            Dim candidate = n
            Dim k = 2
            While used.Contains(candidate)
                Dim suffix = $" ({k})"
                candidate = n.Substring(0, Math.Min(n.Length, 31 - suffix.Length)) & suffix
                k += 1
            End While
            used.Add(candidate)
            Return candidate
        End Function

        Public Shared Sub WriteXlsx(full As String, sheets As List(Of (Name As String, Rows As List(Of List(Of String)))))
            Dim used As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim names = sheets.Select(Function(s) SheetName(s.Name, used)).ToList()
            WriteZip(full, Sub(zip)
                               Dim types As New StringBuilder("<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Types xmlns=""http://schemas.openxmlformats.org/package/2006/content-types"">" &
                                   "<Default Extension=""rels"" ContentType=""application/vnd.openxmlformats-package.relationships+xml""/><Default Extension=""xml"" ContentType=""application/xml""/>" &
                                   "<Override PartName=""/xl/workbook.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml""/>" &
                                   "<Override PartName=""/xl/styles.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml""/>" &
                                   "<Override PartName=""/docProps/core.xml"" ContentType=""application/vnd.openxmlformats-package.core-properties+xml""/>")
                               Dim wb As New StringBuilder("<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><workbook xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"" " &
                                   "xmlns:r=""http://schemas.openxmlformats.org/officeDocument/2006/relationships""><sheets>")
                               Dim rels As New StringBuilder("<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">")
                               For i = 0 To sheets.Count - 1
                                   types.Append($"<Override PartName=""/xl/worksheets/sheet{i + 1}.xml"" ContentType=""application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml""/>")
                                   wb.Append($"<sheet name=""{X(names(i))}"" sheetId=""{i + 1}"" r:id=""rId{i + 1}""/>")
                                   rels.Append($"<Relationship Id=""rId{i + 1}"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"" Target=""worksheets/sheet{i + 1}.xml""/>")
                                   Put(zip, $"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets(i).Rows))
                               Next
                               rels.Append($"<Relationship Id=""rId{sheets.Count + 1}"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles"" Target=""styles.xml""/></Relationships>")
                               Put(zip, "[Content_Types].xml", types.Append("</Types>").ToString())
                               Put(zip, "_rels/.rels", "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><Relationships xmlns=""http://schemas.openxmlformats.org/package/2006/relationships"">" &
                                   "<Relationship Id=""rId1"" Type=""http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument"" Target=""xl/workbook.xml""/>" &
                                   "<Relationship Id=""rId2"" Type=""http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties"" Target=""docProps/core.xml""/></Relationships>")
                               Put(zip, "xl/workbook.xml", wb.Append("</sheets></workbook>").ToString())
                               Put(zip, "xl/_rels/workbook.xml.rels", rels.ToString())
                               Put(zip, "xl/styles.xml", "<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">" &
                                   "<numFmts count=""1""><numFmt numFmtId=""164"" formatCode=""yyyy-mm-dd hh:mm""/></numFmts>" &
                                   "<fonts count=""2""><font><sz val=""11""/><name val=""Calibri""/></font><font><b/><sz val=""11""/><name val=""Calibri""/></font></fonts>" &
                                   "<fills count=""3""><fill><patternFill patternType=""none""/></fill><fill><patternFill patternType=""gray125""/></fill><fill><patternFill patternType=""solid""><fgColor rgb=""FFE8E8E8""/><bgColor indexed=""64""/></patternFill></fill></fills>" &
                                   "<borders count=""1""><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>" &
                                   "<cellXfs count=""4""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/><xf numFmtId=""0"" fontId=""1"" fillId=""2"" borderId=""0"" xfId=""0"" applyFont=""1"" applyFill=""1""/>" &
                                   "<xf numFmtId=""14"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0"" applyNumberFormat=""1""/><xf numFmtId=""164"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0"" applyNumberFormat=""1""/></cellXfs>" &
                                   "<cellStyles count=""1""><cellStyle name=""Normal"" xfId=""0"" builtinId=""0""/></cellStyles></styleSheet>")
                               Put(zip, "docProps/core.xml", CoreProps(Path.GetFileNameWithoutExtension(full), Nothing))
                           End Sub)
        End Sub

        Private Shared Function Sheet(rows As List(Of List(Of String))) As String
            Dim cols = If(rows.Count = 0, 0, rows.Max(Function(r) r.Count))
            Dim sb As New StringBuilder("<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?><worksheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">")
            If rows.Count > 1 Then sb.Append("<sheetViews><sheetView workbookViewId=""0""><pane ySplit=""1"" topLeftCell=""A2"" activePane=""bottomLeft"" state=""frozen""/></sheetView></sheetViews>")
            If cols > 0 Then
                sb.Append("<cols>")
                For c = 0 To cols - 1
                    Dim ci = c
                    Dim width = Math.Clamp(rows.Take(500).Max(Function(r) If(ci < r.Count, If(r(ci), "").Length, 0)) + 2, 8, 60)
                    sb.Append($"<col min=""{c + 1}"" max=""{c + 1}"" width=""{width}"" customWidth=""1""/>")
                Next
                sb.Append("</cols>")
            End If
            sb.Append("<sheetData>")
            For r = 0 To rows.Count - 1
                sb.Append($"<row r=""{r + 1}"">")
                For c = 0 To rows(r).Count - 1
                    Dim v = If(rows(r)(c), "")
                    If v.Length = 0 Then Continue For
                    Dim refName = ColumnName(c) & (r + 1).ToString(Inv)
                    Dim header = r = 0 AndAlso rows.Count > 1
                    Dim num As Double
                    If header Then
                        sb.Append($"<c r=""{refName}"" s=""1"" t=""inlineStr""><is><t xml:space=""preserve"">{X(v)}</t></is></c>")
                    ElseIf NumberCell.IsMatch(v.Trim()) AndAlso Double.TryParse(v.Trim(), NumberStyles.Float, Inv, num) Then
                        sb.Append($"<c r=""{refName}""><v>{num.ToString("R", Inv)}</v></c>")
                    ElseIf v.Equals("TRUE", StringComparison.OrdinalIgnoreCase) OrElse v.Equals("FALSE", StringComparison.OrdinalIgnoreCase) Then
                        sb.Append($"<c r=""{refName}"" t=""b""><v>{If(v.Equals("TRUE", StringComparison.OrdinalIgnoreCase), 1, 0)}</v></c>")
                    ElseIf DateCell.IsMatch(v.Trim()) AndAlso DateTime.TryParse(v.Trim(), Inv, DateTimeStyles.None, Nothing) Then
                        Dim d = DateTime.Parse(v.Trim(), Inv)
                        sb.Append($"<c r=""{refName}"" s=""{If(d.TimeOfDay = TimeSpan.Zero, 2, 3)}""><v>{d.ToOADate().ToString("R", Inv)}</v></c>")
                    Else
                        sb.Append($"<c r=""{refName}"" t=""inlineStr""><is><t xml:space=""preserve"">{X(v)}</t></is></c>")
                    End If
                Next
                sb.Append("</row>")
            Next
            sb.Append("</sheetData>")
            If rows.Count > 1 AndAlso cols > 0 Then sb.Append($"<autoFilter ref=""A1:{ColumnName(cols - 1)}{rows.Count}""/>")
            Return sb.Append("</worksheet>").ToString()
        End Function
    End Class

    ''' <summary>
    ''' A small PDF writer for reports: Helvetica and Courier (the standard 14 fonts, so nothing is embedded), headings, wrapped
    ''' paragraphs with bold/italic/code, lists, tables with borders, code blocks, page breaks and page numbers. Text outside
    ''' Windows-1252 is shown as '?'.
    ''' </summary>
    Friend NotInheritable Class PdfWriter
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
        Private Const PageW As Double = 595.28, PageH As Double = 841.89, Margin As Double = 56, Bottom As Double = 60

        ' Helvetica and Helvetica-Bold glyph widths (1/1000 em) for characters 32..126.
        Private Shared ReadOnly Helv As Integer() = {278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            278, 278, 584, 584, 584, 556, 1015, 667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
            278, 278, 278, 469, 556, 333, 556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556, 556, 333, 500, 278, 556, 500, 722, 500, 500, 500, 334, 260, 334, 584}
        Private Shared ReadOnly HelvB As Integer() = {278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278, 556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            333, 333, 584, 584, 584, 611, 975, 722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667, 778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
            333, 278, 333, 584, 556, 333, 556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611, 611, 389, 556, 333, 611, 556, 778, 556, 556, 500, 389, 280, 389, 584}

        Private Enum Font
            Regular = 1
            Bold = 2
            Italic = 3
            Mono = 4
        End Enum

        Private ReadOnly _pages As New List(Of StringBuilder)
        Private _page As StringBuilder
        Private _y As Double
        Private Shared ReadOnly Win1252 As Encoding

        Shared Sub New()
            Try
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)
            Catch ex As Exception
            End Try
            Win1252 = Encoding.GetEncoding(1252, New EncoderReplacementFallback("?"), New DecoderReplacementFallback("?"))
        End Sub

        Private Shared Function Width(text As String, f As Font, size As Double) As Double
            If f = Font.Mono Then Return text.Length * 600 * size / 1000
            Dim table = If(f = Font.Bold, HelvB, Helv)
            Dim w = 0
            For Each c In text
                Dim code = AscW(c)
                w += If(code >= 32 AndAlso code <= 126, table(code - 32), 556)
            Next
            Return w * size / 1000
        End Function

        Private Shared Function Literal(text As String) As String
            Dim bytes = Win1252.GetBytes(text)
            Dim sb As New StringBuilder("(")
            For Each b In bytes
                Select Case b
                    Case 40, 41, 92 : sb.Append("\"c).Append(ChrW(b))
                    Case Is < 32, Is > 126 : sb.Append("\"c).Append(Convert.ToString(b, 8).PadLeft(3, "0"c))
                    Case Else : sb.Append(ChrW(b))
                End Select
            Next
            Return sb.Append(")"c).ToString()
        End Function

        Private Sub NewPage()
            _page = New StringBuilder()
            _pages.Add(_page)
            _y = PageH - Margin
        End Sub

        Private Sub Need(height As Double)
            If _y - height < Bottom Then NewPage()
        End Sub

        Private Sub Text(x As Double, y As Double, s As String, f As Font, size As Double, Optional gray As Double = 0)
            If s.Length = 0 Then Return
            _page.Append($"BT {If(gray > 0, Num(gray) & " g ", "")}/F{CInt(f)} {Num(size)} Tf 1 0 0 1 {Num(x)} {Num(y)} Tm {Literal(s)} Tj ET{If(gray > 0, " 0 g", "")}" & vbLf)
        End Sub

        Private Shared Function Num(v As Double) As String
            Return Math.Round(v, 2).ToString("0.##", Inv)
        End Function

        ''' <summary>Lays out inline spans in lines no wider than maxWidth; each line is a list of (text, font, x offset).</summary>
        Private Shared Function Wrap(spans As List(Of (Text As String, F As Font)), size As Double, maxWidth As Double) As List(Of List(Of (Text As String, F As Font, X As Double)))
            Dim lines As New List(Of List(Of (Text As String, F As Font, X As Double)))
            Dim line As New List(Of (Text As String, F As Font, X As Double))
            Dim x = 0.0
            For Each sp In spans
                For Each word In Regex.Split(sp.Text, "(\s+)")
                    If word.Length = 0 Then Continue For
                    Dim isSpace = String.IsNullOrWhiteSpace(word)
                    Dim piece = If(isSpace, " ", word)
                    Dim w = Width(piece, sp.F, size)
                    If isSpace Then
                        If line.Count > 0 Then x += w
                        Continue For
                    End If
                    If x + w > maxWidth AndAlso line.Count > 0 Then
                        lines.Add(line) : line = New List(Of (Text As String, F As Font, X As Double)) : x = 0
                    End If
                    While w > maxWidth AndAlso piece.Length > 1
                        Dim n = piece.Length
                        While n > 1 AndAlso Width(piece.Substring(0, n), sp.F, size) > maxWidth - x
                            n -= 1
                        End While
                        line.Add((piece.Substring(0, n), sp.F, x))
                        lines.Add(line) : line = New List(Of (Text As String, F As Font, X As Double)) : x = 0
                        piece = piece.Substring(n)
                        w = Width(piece, sp.F, size)
                    End While
                    line.Add((piece, sp.F, x))
                    x += w
                Next
            Next
            If line.Count > 0 Then lines.Add(line)
            Return lines
        End Function

        Private Shared Function SpansOf(text As String, bold As Boolean) As List(Of (Text As String, F As Font))
            Return MarkdownBlocks.Spans(text).Select(Function(s) (s.Text, If(s.Code, Font.Mono, If(bold OrElse s.Bold, Font.Bold, If(s.Italic, Font.Italic, Font.Regular))))).ToList()
        End Function

        Private Sub Paragraph(spans As List(Of (Text As String, F As Font)), size As Double, indent As Double, after As Double, Optional prefix As String = Nothing)
            Dim leading = size * 1.35
            Dim lines = Wrap(spans, size, PageW - 2 * Margin - indent)
            Dim first = True
            For Each line In lines
                Need(leading)
                _y -= leading
                If first AndAlso prefix IsNot Nothing Then Text(Margin + indent - Width(prefix & " ", Font.Regular, size), _y, prefix, Font.Regular, size)
                first = False
                For Each piece In line
                    Text(Margin + indent + piece.X, _y, piece.Text, piece.F, size)
                Next
            Next
            _y -= after
        End Sub

        Private Sub DrawTable(rows As List(Of List(Of String)))
            If rows Is Nothing OrElse rows.Count = 0 Then Return
            Dim cols = rows.Max(Function(r) r.Count)
            Dim size = 9.0, pad = 4.0, leading = size * 1.3
            Dim total = PageW - 2 * Margin
            Dim natural = Enumerable.Range(0, cols).Select(Function(c) Math.Max(30.0, rows.Max(Function(r) If(c < r.Count, Width(MarkdownBlocks.Plain(r(c)), Font.Regular, size), 0)) + 2 * pad)).ToList()
            Dim sum = natural.Sum()
            Dim widths = If(sum <= total, natural, natural.Select(Function(w) Math.Max(36.0, w * total / sum)).ToList())
            Dim scale = Math.Min(1.0, total / widths.Sum())
            widths = widths.Select(Function(w) w * scale).ToList()
            _y -= 4
            For r = 0 To rows.Count - 1
                Dim header = r = 0 AndAlso rows.Count > 1
                Dim cells = Enumerable.Range(0, cols).Select(Function(c) Wrap(SpansOf(If(c < rows(r).Count, rows(r)(c), ""), header), size, widths(c) - 2 * pad)).ToList()
                Dim h = Math.Max(1, cells.Max(Function(l) l.Count)) * leading + 2 * pad
                Need(h)
                Dim x = Margin
                If header Then _page.Append($"0.91 g {Num(Margin)} {Num(_y - h)} {Num(widths.Sum())} {Num(h)} re f 0 g" & vbLf)
                For c = 0 To cols - 1
                    _page.Append($"0.6 G 0.5 w {Num(x)} {Num(_y - h)} {Num(widths(c))} {Num(h)} re S 0 G" & vbLf)
                    Dim ly = _y - pad
                    For Each line In cells(c)
                        ly -= leading
                        For Each piece In line
                            Text(x + pad + piece.X, ly + 2.5, piece.Text, piece.F, size)
                        Next
                    Next
                    x += widths(c)
                Next
                _y -= h
            Next
            _y -= 10
        End Sub

        Public Shared Sub Write(full As String, blocks As List(Of MarkdownBlocks.Block), title As String, author As String)
            Dim w As New PdfWriter()
            w.NewPage()
            If Not String.IsNullOrWhiteSpace(title) Then w.Paragraph(New List(Of (Text As String, F As Font)) From {(title.Trim(), Font.Bold)}, 22, 0, 10)
            Dim number = 0
            Dim prev As MarkdownBlocks.Kind? = Nothing
            For Each b In blocks
                Select Case b.Kind
                    Case MarkdownBlocks.Kind.Heading
                        Dim size = If(b.Level <= 1, 17.0, If(b.Level = 2, 14.0, 12.0))
                        w.Need(size * 3)
                        w._y -= If(b.Level <= 2, 8, 4)
                        w.Paragraph(SpansOf(b.Text, True), size, 0, 4)
                    Case MarkdownBlocks.Kind.Paragraph
                        w.Paragraph(SpansOf(b.Text, False), 10.5, 0, 7)
                    Case MarkdownBlocks.Kind.Bullet
                        w.Paragraph(SpansOf(b.Text, False), 10.5, 16 + 14 * b.Level, 2, ChrW(&H2022))
                    Case MarkdownBlocks.Kind.Numbered
                        If Not prev.HasValue OrElse prev.Value <> MarkdownBlocks.Kind.Numbered Then number = 0
                        number += 1
                        w.Paragraph(SpansOf(b.Text, False), 10.5, 20 + 14 * b.Level, 2, number.ToString(Inv) & ".")
                    Case MarkdownBlocks.Kind.Code
                        For Each line In b.Text.Split(ControlChars.Lf)
                            Dim rest = line.Replace(ControlChars.Tab, "    ")
                            Do
                                Dim chunk = If(rest.Length > 85, rest.Substring(0, 85), rest)
                                rest = If(rest.Length > 85, rest.Substring(85), "")
                                w.Need(12)
                                w._y -= 12
                                w._page.Append($"0.95 g {Num(Margin - 4)} {Num(w._y - 3)} {Num(PageW - 2 * Margin + 8)} 12 re f 0 g" & vbLf)
                                w.Text(Margin, w._y, chunk, Font.Mono, 9)
                            Loop While rest.Length > 0
                        Next
                        w._y -= 8
                    Case MarkdownBlocks.Kind.Rule
                        w.Need(12)
                        w._y -= 6
                        w._page.Append($"0.7 G 0.5 w {Num(Margin)} {Num(w._y)} m {Num(PageW - Margin)} {Num(w._y)} l S 0 G" & vbLf)
                        w._y -= 6
                    Case MarkdownBlocks.Kind.PageBreak
                        w.NewPage()
                    Case MarkdownBlocks.Kind.Table
                        w.DrawTable(b.Rows)
                End Select
                If b.Kind = MarkdownBlocks.Kind.Bullet OrElse b.Kind = MarkdownBlocks.Kind.Numbered Then
                    prev = b.Kind
                Else
                    If (prev = MarkdownBlocks.Kind.Bullet OrElse prev = MarkdownBlocks.Kind.Numbered) Then w._y -= 5
                    prev = b.Kind
                End If
            Next
            For i = 0 To w._pages.Count - 1
                Dim label = $"Page {i + 1} of {w._pages.Count}"
                w._page = w._pages(i)
                w.Text((PageW - Width(label, Font.Regular, 8)) / 2, 30, label, Font.Regular, 8, 0.45)
            Next
            w.Save(full, title, author)
        End Sub

        Private Sub Save(full As String, title As String, author As String)
            Dim objects As New List(Of Byte())
            Dim add = Function(body As Byte()) As Integer
                          objects.Add(body)
                          Return objects.Count
                      End Function
            Dim ascii = Function(s As String) Encoding.Latin1.GetBytes(s)
            Dim catalogId = add(Nothing)
            Dim pagesId = add(Nothing)
            Dim fontIds = {add(ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")),
                           add(ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>")),
                           add(ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Oblique /Encoding /WinAnsiEncoding >>")),
                           add(ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"))}
            Dim fonts = $"<< /F1 {fontIds(0)} 0 R /F2 {fontIds(1)} 0 R /F3 {fontIds(2)} 0 R /F4 {fontIds(3)} 0 R >>"
            Dim pageIds As New List(Of Integer)
            For Each p In _pages
                Dim raw = Encoding.Latin1.GetBytes(p.ToString())
                Dim packed As Byte()
                Using ms As New MemoryStream()
                    Using z As New ZLibStream(ms, CompressionLevel.Optimal, leaveOpen:=True)
                        z.Write(raw, 0, raw.Length)
                    End Using
                    packed = ms.ToArray()
                End Using
                Dim head = ascii($"<< /Length {packed.Length} /Filter /FlateDecode >>{vbLf}stream{vbLf}")
                Dim tail = ascii($"{vbLf}endstream")
                Dim contentId = add(head.Concat(packed).Concat(tail).ToArray())
                pageIds.Add(add(ascii($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {Num(PageW)} {Num(PageH)}] /Resources << /Font {fonts} >> /Contents {contentId} 0 R >>")))
            Next
            objects(pagesId - 1) = ascii($"<< /Type /Pages /Kids [{String.Join(" ", pageIds.Select(Function(i) $"{i} 0 R"))}] /Count {pageIds.Count} >>")
            objects(catalogId - 1) = ascii($"<< /Type /Catalog /Pages {pagesId} 0 R >>")
            Dim infoId = add(Win1252.GetBytes($"<< /Title {Literal(If(title, ""))} /Author {Literal(If(String.IsNullOrWhiteSpace(author), "Agentic", author))} /Producer (Agentic) /CreationDate (D:{DateTime.UtcNow.ToString("yyyyMMddHHmmss", Inv)}Z) >>"))
            Dim tmp = full & ".tmp-" & Guid.NewGuid().ToString("N").Substring(0, 8)
            Try
                Using fs As New FileStream(tmp, FileMode.CreateNew)
                    Dim write = Sub(b As Byte()) fs.Write(b, 0, b.Length)
                    write(ascii("%PDF-1.4" & vbLf & "%" & ChrW(&HE2) & ChrW(&HE3) & ChrW(&HCF) & ChrW(&HD3) & vbLf))
                    Dim offsets As New List(Of Long)
                    For i = 0 To objects.Count - 1
                        offsets.Add(fs.Position)
                        write(ascii($"{i + 1} 0 obj{vbLf}"))
                        write(objects(i))
                        write(ascii($"{vbLf}endobj{vbLf}"))
                    Next
                    Dim xref = fs.Position
                    Dim sb As New StringBuilder($"xref{vbLf}0 {objects.Count + 1}{vbLf}0000000000 65535 f {vbLf}")
                    For Each o In offsets
                        sb.Append(o.ToString("D10", Inv)).Append(" 00000 n ").Append(vbLf)
                    Next
                    sb.Append($"trailer{vbLf}<< /Size {objects.Count + 1} /Root {catalogId} 0 R /Info {infoId} 0 R >>{vbLf}startxref{vbLf}{xref}{vbLf}%%EOF{vbLf}")
                    write(ascii(sb.ToString()))
                End Using
                File.Move(tmp, full, overwrite:=True)
            Finally
                If File.Exists(tmp) Then File.Delete(tmp)
            End Try
        End Sub
    End Class

End Namespace
