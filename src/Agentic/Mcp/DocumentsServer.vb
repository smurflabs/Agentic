Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Xml.Linq
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "documents" MCP server ("Command": "{self}", "Arguments": ["--mcp-documents", "--roots", "{data}\shared;{data}\workspace"]):
    ''' reads PDF, Word (.docx), Excel (.xlsx), PowerPoint (.pptx), CSV, email (.eml), HTML and text files into text and tables, and
    ''' writes reports as .docx, .pdf, .md or .html and spreadsheets as .xlsx or .csv. Files stay inside the allowed folders.
    ''' </summary>
    Public NotInheritable Class DocumentsServer
        Private ReadOnly _roots As PathRoots
        Private ReadOnly _maxChars As Integer
        Private Const MaxFileBytes As Long = 100L * 1024 * 1024
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(roots As PathRoots, Optional maxOutputKb As Integer = 120)
            _roots = roots
            _maxChars = Math.Max(8, maxOutputKb) * 1024
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim roots = ToolKit.SplitList(If(ToolKit.Arg(args, "--roots"), Path.Combine(AppContext.BaseDirectory, "data", "shared")))
            Dim server As New DocumentsServer(New PathRoots(roots), ToolKit.IntArg(args, "--max-output-kb", 120))
            Return BuiltInServer.Run("agentic-documents", server.CreateTools())
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim where = $" Paths are relative to {_roots.Roots(0)} (or absolute inside {_roots.Describe()})."
            ToolKit.Add(tools, New Func(Of String, String, String, Integer, String)(AddressOf ReadDocument), "read_document",
                "Read a document as text: PDF (per page), Word .docx (headings, lists, tables as Markdown), Excel .xlsx (sheets as tables), " &
                "PowerPoint .pptx (slides and notes), CSV/TSV, .eml, HTML, Markdown, JSON or text. pages: e.g. '1-3,7' (PDF pages / slides); sheet: an Excel sheet name." & where)
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf DocumentInfo), "document_info",
                "A document's type, size, page/sheet/slide count, title, author and dates - without reading it all." & where)
            ToolKit.Add(tools, New Func(Of String, String, Integer, String)(AddressOf SearchDocument), "search_document",
                "Find text in a document (case-insensitive): the page, sheet or slide of each match with the text around it." & where)
            ToolKit.Add(tools, New Func(Of String, String, String, String, String)(AddressOf CreateDocument), "create_document",
                "Write a report from Markdown (# headings, - bullets, 1. lists, **bold**, *italic*, `code`, | tables |, --- page break) as .docx, .pdf, .html or .md " &
                "(by the path's extension). The file is replaced if it exists." & where, False, True)
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf CreateSpreadsheet), "create_spreadsheet",
                "Write an .xlsx (or .csv) spreadsheet. data: CSV text (first row = headers), a JSON array of rows, or a JSON object of sheets " &
                "{""Sheet name"": [[...],[...]]}. Numbers, true/false and ISO dates become real cells; the header row is bold and frozen." & where, False, True)
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf ConvertDocument), "convert_document",
                "Convert a document to another format by the target's extension: .docx/.pdf/.html/.md from any readable document, .xlsx/.csv from a spreadsheet or CSV." & where, False, True)
            Return tools
        End Function

        Private Function Existing(p As String) As String
            Dim full = _roots.Resolve(p)
            If Not File.Exists(full) Then Throw New McpException($"File '{p}' does not exist.")
            If New FileInfo(full).Length > MaxFileBytes Then Throw New McpException($"'{p}' is larger than {MaxFileBytes \ (1024 * 1024)} MB.")
            Return full
        End Function

        ' ------------------------------------------------------------------------------------------ reading

        ''' <summary>A document as labelled parts (pages, sheets, slides): the label and the text of each.</summary>
        Friend Shared Function ReadParts(full As String, Optional sheet As String = Nothing) As List(Of (Label As String, Text As String))
            Select Case Path.GetExtension(full).ToLowerInvariant()
                Case ".pdf" : Return PdfText.Pages(full)
                Case ".docx", ".docm", ".dotx" : Return New List(Of (String, String)) From {("Document", OfficeText.Docx(full))}
                Case ".xlsx", ".xlsm" : Return OfficeText.Xlsx(full, sheet).Select(Function(s) ("Sheet " & s.Name, TableText.ToMarkdown(s.Rows, 500))).ToList()
                Case ".pptx", ".pptm" : Return OfficeText.Pptx(full)
                Case ".csv", ".tsv"
                    Dim rows = TableText.ParseCsv(File.ReadAllText(full), If(full.EndsWith(".tsv", StringComparison.OrdinalIgnoreCase), ControlChars.Tab, ","c))
                    Return New List(Of (String, String)) From {($"Table ({rows.Count:N0} rows)", TableText.ToMarkdown(rows, 500))}
                Case ".eml"
                    Dim m = Global.Agentic.Work.MailParser.Parse(File.ReadAllBytes(full))
                    Dim head = $"From: {m.FromName} <{m.FromAddress}>{vbLf}Subject: {m.Subject}{vbLf}Date: {m.DateText}{If(m.Attachments.Count > 0, vbLf & "Attachments: " & String.Join(", ", m.Attachments), "")}"
                    Return New List(Of (String, String)) From {("Email", head & vbLf & vbLf & m.Body)}
                Case ".html", ".htm"
                    Return New List(Of (String, String)) From {("Page", Global.Agentic.Tools.PageReader.HtmlToText(File.ReadAllText(full)).Text)}
                Case ".doc", ".xls", ".ppt"
                    Throw New McpException("Old binary Office formats (.doc/.xls/.ppt) cannot be read; save them as .docx/.xlsx/.pptx first.")
                Case Else
                    Dim bytes = File.ReadAllBytes(full)
                    If bytes.Take(8000).Any(Function(b) b = 0) Then Throw New McpException($"'{Path.GetFileName(full)}' is a binary file this tool cannot read.")
                    Return New List(Of (String, String)) From {("Text", Encoding.UTF8.GetString(bytes))}
            End Select
        End Function

        ''' <summary>Part numbers from "1-3,7" (1-based); Nothing = all.</summary>
        Friend Shared Function ParseRanges(spec As String, count As Integer) As HashSet(Of Integer)
            If String.IsNullOrWhiteSpace(spec) Then Return Nothing
            Dim result As New HashSet(Of Integer)
            For Each part In spec.Split(","c, StringSplitOptions.RemoveEmptyEntries)
                Dim p = part.Trim()
                Dim m = Regex.Match(p, "^(\d+)\s*(?:-\s*(\d*))?$")
                If Not m.Success Then Throw New McpException($"'{p}' is not a page range (use e.g. 1-3,7).")
                Dim a = Integer.Parse(m.Groups(1).Value, Inv)
                Dim b = If(m.Groups(2).Success, If(m.Groups(2).Value = "", count, Integer.Parse(m.Groups(2).Value, Inv)), a)
                For i = Math.Max(1, a) To Math.Min(count, b)
                    result.Add(i)
                Next
            Next
            Return result
        End Function

        Friend Function ReadDocument(<Description("Document path")> path As String,
                                     <Description("Pages or slides, e.g. '1-3,7' (optional)")> Optional pages As String = "",
                                     <Description("Excel sheet name (optional; default all sheets)")> Optional sheet As String = "",
                                     <Description("Most characters to return (default about 120 000)")> Optional max_chars As Integer = 0) As String
            Dim full = Existing(path)
            Dim parts = ReadParts(full, If(String.IsNullOrWhiteSpace(sheet), Nothing, sheet.Trim()))
            Dim pick = ParseRanges(pages, parts.Count)
            Dim sb As New StringBuilder()
            Dim n = 0
            For i = 0 To parts.Count - 1
                If pick IsNot Nothing AndAlso Not pick.Contains(i + 1) Then Continue For
                If parts.Count > 1 Then sb.AppendLine($"=== {parts(i).Label} ===")
                sb.AppendLine(parts(i).Text.TrimEnd())
                sb.AppendLine()
                n += 1
            Next
            Dim text = sb.ToString().TrimEnd()
            If text.Length = 0 AndAlso full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) Then
                Return "This PDF has no text layer (probably scanned images). Its pages would need OCR."
            End If
            Dim limit = If(max_chars > 0, Math.Min(max_chars, _maxChars * 4), _maxChars)
            Dim header = If(parts.Count > 1, $"{System.IO.Path.GetFileName(full)}: {parts.Count} part(s){If(pick IsNot Nothing, $", {n} shown", "")}{vbLf}{vbLf}", "")
            Return ToolKit.Clip(header & text, limit) & If(text.Length > limit AndAlso parts.Count > 1, vbLf & "(read fewer pages at a time with pages: '...')", "")
        End Function

        Friend Function DocumentInfo(<Description("Document path")> path As String) As String
            Dim full = Existing(path)
            Dim fi As New FileInfo(full)
            Dim sb As New StringBuilder()
            sb.AppendLine($"{fi.Name}: {fi.Length:N0} bytes, modified {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC")
            Select Case fi.Extension.ToLowerInvariant()
                Case ".pdf"
                    sb.Append(PdfText.Info(full))
                Case ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm"
                    sb.Append(OfficeText.Info(full))
                Case Else
                    Dim parts = ReadParts(full)
                    sb.Append($"{parts.Count} part(s), {parts.Sum(Function(p) p.Text.Length):N0} characters of text")
            End Select
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function SearchDocument(<Description("Document path")> path As String,
                                       <Description("Text to find (case-insensitive)")> text As String,
                                       <Description("Most matches (default 30)")> Optional max_results As Integer = 30) As String
            If String.IsNullOrWhiteSpace(text) Then Throw New McpException("text is required.")
            Dim full = Existing(path)
            Dim hits As New List(Of String)
            Dim limit = Math.Clamp(If(max_results <= 0, 30, max_results), 1, 200)
            For Each part In ReadParts(full)
                Dim t = part.Text
                Dim at = t.IndexOf(text, StringComparison.OrdinalIgnoreCase)
                While at >= 0 AndAlso hits.Count < limit
                    Dim s = Math.Max(0, at - 100), e = Math.Min(t.Length, at + text.Length + 100)
                    hits.Add($"[{part.Label}] ...{Regex.Replace(t.Substring(s, e - s), "\s+", " ").Trim()}...")
                    at = t.IndexOf(text, at + text.Length, StringComparison.OrdinalIgnoreCase)
                End While
                If hits.Count >= limit Then Exit For
            Next
            Return If(hits.Count = 0, $"'{text}' was not found.", $"{hits.Count} match(es):{vbLf}" & String.Join(vbLf, hits))
        End Function

        ' ------------------------------------------------------------------------------------------ writing

        Friend Function CreateDocument(<Description("Output path ending in .docx, .pdf, .html or .md")> path As String,
                                       <Description("Content as Markdown")> content As String,
                                       <Description("Document title (optional)")> Optional title As String = "",
                                       <Description("Author (optional)")> Optional author As String = "") As String
            Dim full = _roots.Resolve(path)
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full))
            Dim blocks = MarkdownBlocks.Parse(If(content, ""))
            Select Case System.IO.Path.GetExtension(full).ToLowerInvariant()
                Case ".docx" : OfficeWriter.WriteDocx(full, blocks, title, author)
                Case ".pdf" : PdfWriter.Write(full, blocks, title, author)
                Case ".html", ".htm" : File.WriteAllText(full, MarkdownBlocks.ToHtml(blocks, title), New UTF8Encoding(False))
                Case ".md", ".markdown", ".txt" : File.WriteAllText(full, If(String.IsNullOrWhiteSpace(title), "", "# " & title.Trim() & vbLf & vbLf) & If(content, ""), New UTF8Encoding(False))
                Case Else : Throw New McpException("The path must end in .docx, .pdf, .html or .md.")
            End Select
            Return $"Wrote {_roots.Show(full)} ({New FileInfo(full).Length:N0} bytes, {blocks.Count} blocks)."
        End Function

        Friend Function CreateSpreadsheet(<Description("Output path ending in .xlsx or .csv")> path As String,
                                          <Description("CSV text, a JSON array of rows, or a JSON object of sheet name -> rows")> data As String,
                                          <Description("Sheet name for CSV/array data (default Sheet1)")> Optional sheet_name As String = "") As String
            Dim full = _roots.Resolve(path)
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full))
            Dim sheets = TableText.ParseSheets(If(data, ""), If(String.IsNullOrWhiteSpace(sheet_name), "Sheet1", sheet_name.Trim()))
            Select Case System.IO.Path.GetExtension(full).ToLowerInvariant()
                Case ".xlsx" : OfficeWriter.WriteXlsx(full, sheets)
                Case ".csv" : File.WriteAllText(full, TableText.ToCsv(sheets(0).Rows), New UTF8Encoding(True))
                Case Else : Throw New McpException("The path must end in .xlsx or .csv.")
            End Select
            Return $"Wrote {_roots.Show(full)}: {String.Join(", ", sheets.Select(Function(s) $"{s.Name} ({s.Rows.Count:N0} rows)"))}."
        End Function

        Friend Function ConvertDocument(<Description("Source document")> source As String,
                                        <Description("Target path; its extension sets the format")> target As String) As String
            Dim src = Existing(source)
            Dim ext = System.IO.Path.GetExtension(target).ToLowerInvariant()
            Dim srcExt = System.IO.Path.GetExtension(src).ToLowerInvariant()
            If ext = ".xlsx" OrElse ext = ".csv" Then
                Dim sheets As List(Of (Name As String, Rows As List(Of List(Of String))))
                If srcExt = ".xlsx" OrElse srcExt = ".xlsm" Then
                    sheets = OfficeText.Xlsx(src, Nothing)
                ElseIf srcExt = ".csv" OrElse srcExt = ".tsv" Then
                    sheets = New List(Of (Name As String, Rows As List(Of List(Of String)))) From {("Sheet1", TableText.ParseCsv(File.ReadAllText(src), If(srcExt = ".tsv", ControlChars.Tab, ","c)))}
                Else
                    Throw New McpException("Only spreadsheets and CSV files convert to .xlsx/.csv.")
                End If
                Dim full = _roots.Resolve(target)
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full))
                If ext = ".xlsx" Then OfficeWriter.WriteXlsx(full, sheets) Else File.WriteAllText(full, TableText.ToCsv(sheets(0).Rows), New UTF8Encoding(True))
                Return $"Wrote {_roots.Show(full)}."
            End If
            Dim parts = ReadParts(src)
            Dim md As New StringBuilder()
            For i = 0 To parts.Count - 1
                If i > 0 Then md.AppendLine().AppendLine(If(srcExt = ".pdf" OrElse srcExt = ".pptx", "---", "")).AppendLine()
                If parts.Count > 1 AndAlso srcExt <> ".pdf" Then md.AppendLine("## " & parts(i).Label).AppendLine()
                md.AppendLine(parts(i).Text)
            Next
            Return CreateDocument(target, md.ToString(), System.IO.Path.GetFileNameWithoutExtension(src), "")
        End Function
    End Class

    ' ============================================================================================== PDF text (PdfPig)

    Friend NotInheritable Class PdfText
        Private Sub New()
        End Sub

        Public Shared Function Pages(full As String) As List(Of (Label As String, Text As String))
            Dim result As New List(Of (Label As String, Text As String))
            Try
                Using doc = UglyToad.PdfPig.PdfDocument.Open(full)
                    For Each page In doc.GetPages()
                        Dim text As String
                        Try
                            text = UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor.ContentOrderTextExtractor.GetText(page)
                        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is NullReferenceException
                            text = page.Text
                        End Try
                        result.Add(($"Page {page.Number}", text))
                    Next
                End Using
            Catch ex As Exception When TypeOf ex Is UglyToad.PdfPig.Core.PdfDocumentFormatException OrElse TypeOf ex Is InvalidOperationException
                Throw New McpException("This PDF could not be read: " & ex.Message)
            End Try
            Return result
        End Function

        Public Shared Function Info(full As String) As String
            Using doc = UglyToad.PdfPig.PdfDocument.Open(full)
                Dim i = doc.Information
                Dim sb As New StringBuilder()
                sb.AppendLine($"PDF {doc.Version.ToString(CultureInfo.InvariantCulture)}, {doc.NumberOfPages} page(s){If(doc.IsEncrypted, ", encrypted", "")}")
                If Not String.IsNullOrWhiteSpace(i.Title) Then sb.AppendLine("Title: " & i.Title)
                If Not String.IsNullOrWhiteSpace(i.Author) Then sb.AppendLine("Author: " & i.Author)
                If Not String.IsNullOrWhiteSpace(i.Subject) Then sb.AppendLine("Subject: " & i.Subject)
                If Not String.IsNullOrWhiteSpace(i.Creator) Then sb.AppendLine("Created with: " & i.Creator)
                If Not String.IsNullOrWhiteSpace(i.CreationDate) Then sb.AppendLine("Created: " & i.CreationDate)
                Dim first = doc.GetPage(1)
                sb.Append($"Page size: {first.Width:0} x {first.Height:0} pt; text on page 1: {If(first.Letters.Count > 0, "yes", "no (scanned?)")}")
                Return sb.ToString()
            End Using
        End Function
    End Class

    ' ============================================================================================== Office Open XML text

    Friend NotInheritable Class OfficeText
        Private Sub New()
        End Sub

        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Private Shared ReadOnly W As XNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"
        Private Shared ReadOnly S As XNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
        Private Shared ReadOnly A As XNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main"
        Private Shared ReadOnly P As XNamespace = "http://schemas.openxmlformats.org/presentationml/2006/main"
        Private Shared ReadOnly R As XNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
        Private Shared ReadOnly PR As XNamespace = "http://schemas.openxmlformats.org/package/2006/relationships"

        Private Shared Function Load(zip As ZipArchive, name As String) As XDocument
            Dim e = zip.GetEntry(name)
            If e Is Nothing Then Return Nothing
            Using s = e.Open()
                Return XDocument.Load(s)
            End Using
        End Function

        Private Shared Function Open(full As String) As ZipArchive
            Try
                Return ZipFile.OpenRead(full)
            Catch ex As InvalidDataException
                Throw New McpException($"'{Path.GetFileName(full)}' is not a valid Office file (it may be password-protected or an old .doc/.xls).")
            End Try
        End Function

        ''' <summary>Relationship id -> target part name, for the part at partName.</summary>
        Private Shared Function Rels(zip As ZipArchive, partName As String) As Dictionary(Of String, String)
            Dim dir = partName.Substring(0, partName.LastIndexOf("/"c) + 1)
            Dim relsName = dir & "_rels/" & partName.Substring(dir.Length) & ".rels"
            Dim doc = Load(zip, relsName)
            Dim map As New Dictionary(Of String, String)(StringComparer.Ordinal)
            If doc Is Nothing Then Return map
            For Each rel In doc.Root.Elements(PR + "Relationship")
                Dim target = CStr(rel.Attribute("Target"))
                If String.Equals(CStr(rel.Attribute("TargetMode")), "External", StringComparison.OrdinalIgnoreCase) Then Continue For
                map(CStr(rel.Attribute("Id"))) = If(target.StartsWith("/"), target.TrimStart("/"c), NormalizePart(dir & target))
            Next
            Return map
        End Function

        Private Shared Function NormalizePart(p As String) As String
            Dim stack As New List(Of String)
            For Each seg In p.Split("/"c)
                If seg = ".." Then
                    If stack.Count > 0 Then stack.RemoveAt(stack.Count - 1)
                ElseIf seg <> "." AndAlso seg <> "" Then
                    stack.Add(seg)
                End If
            Next
            Return String.Join("/", stack)
        End Function

        ' ---------------------------------------------------------------- Word

        Public Shared Function Docx(full As String) As String
            Using zip = Open(full)
                Dim doc = Load(zip, "word/document.xml")
                If doc Is Nothing Then Throw New McpException("This is not a Word document (word/document.xml is missing).")
                Dim sb As New StringBuilder()
                For Each el In doc.Root.Element(W + "body").Elements()
                    If el.Name = W + "p" Then
                        Dim line = Paragraph(el)
                        If line IsNot Nothing Then sb.AppendLine(line).AppendLine()
                    ElseIf el.Name = W + "tbl" Then
                        Dim rows = el.Elements(W + "tr").Select(Function(tr) tr.Elements(W + "tc").Select(Function(tc) String.Join(" ", tc.Elements(W + "p").Select(Function(pp) RunText(pp)).Where(Function(x) x.Length > 0))).ToList()).ToList()
                        sb.AppendLine(TableText.ToMarkdown(rows, 1000)).AppendLine()
                    End If
                Next
                Return Regex.Replace(sb.ToString(), "\n{3,}", vbLf & vbLf).Trim()
            End Using
        End Function

        Private Shared Function RunText(p As XElement) As String
            Dim sb As New StringBuilder()
            For Each node In p.Descendants()
                If node.Name = W + "t" Then
                    sb.Append(node.Value)
                ElseIf node.Name = W + "tab" Then
                    sb.Append(ControlChars.Tab)
                ElseIf node.Name = W + "br" OrElse node.Name = W + "cr" Then
                    sb.Append(vbLf)
                End If
            Next
            Return sb.ToString()
        End Function

        Private Shared Function Paragraph(p As XElement) As String
            Dim text = RunText(p)
            If text.Trim().Length = 0 Then Return Nothing
            Dim style = CStr(p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val"))
            Dim m = Regex.Match(If(style, ""), "^(?:Heading|heading)\s*(\d)$")
            If m.Success Then Return New String("#"c, Math.Clamp(Integer.Parse(m.Groups(1).Value, Inv), 1, 6)) & " " & text.Trim()
            If String.Equals(style, "Title", StringComparison.OrdinalIgnoreCase) Then Return "# " & text.Trim()
            Dim numPr = p.Element(W + "pPr")?.Element(W + "numPr")
            If numPr IsNot Nothing OrElse (style IsNot Nothing AndAlso style.StartsWith("List", StringComparison.OrdinalIgnoreCase)) Then
                Dim level = 0
                Integer.TryParse(CStr(numPr?.Element(W + "ilvl")?.Attribute(W + "val")), level)
                Return New String(" "c, level * 2) & "- " & text.Trim()
            End If
            Return text
        End Function

        ' ---------------------------------------------------------------- Excel

        Public Shared Function Xlsx(full As String, onlySheet As String) As List(Of (Name As String, Rows As List(Of List(Of String))))
            Using zip = Open(full)
                Dim wb = Load(zip, "xl/workbook.xml")
                If wb Is Nothing Then Throw New McpException("This is not an Excel workbook (xl/workbook.xml is missing).")
                Dim relMap = Rels(zip, "xl/workbook.xml")
                Dim strings As New List(Of String)
                Dim sst = Load(zip, "xl/sharedStrings.xml")
                If sst IsNot Nothing Then
                    For Each si In sst.Root.Elements(S + "si")
                        strings.Add(String.Concat(si.Descendants(S + "t").Select(Function(t) t.Value)))
                    Next
                End If
                Dim dateIdx = DateStyles(Load(zip, "xl/styles.xml"))
                Dim result As New List(Of (Name As String, Rows As List(Of List(Of String))))
                For Each sh In wb.Root.Element(S + "sheets").Elements(S + "sheet")
                    Dim name = CStr(sh.Attribute("name"))
                    If onlySheet IsNot Nothing AndAlso Not String.Equals(name, onlySheet, StringComparison.OrdinalIgnoreCase) Then Continue For
                    Dim target As String = Nothing
                    If Not relMap.TryGetValue(CStr(sh.Attribute(R + "id")), target) Then Continue For
                    Dim ws = Load(zip, target)
                    If ws Is Nothing Then Continue For
                    result.Add((name, SheetRows(ws, strings, dateIdx)))
                Next
                If onlySheet IsNot Nothing AndAlso result.Count = 0 Then
                    Throw New McpException($"No sheet '{onlySheet}'. Sheets: {String.Join(", ", wb.Root.Element(S + "sheets").Elements(S + "sheet").Select(Function(x) CStr(x.Attribute("name"))))}")
                End If
                Return result
            End Using
        End Function

        ''' <summary>Style indexes (cellXfs) whose number format is a date.</summary>
        Private Shared Function DateStyles(styles As XDocument) As HashSet(Of Integer)
            Dim result As New HashSet(Of Integer)
            If styles Is Nothing Then Return result
            Dim custom As New Dictionary(Of Integer, String)
            For Each nf In If(styles.Root.Element(S + "numFmts")?.Elements(S + "numFmt"), Enumerable.Empty(Of XElement)())
                custom(CInt(nf.Attribute("numFmtId"))) = CStr(nf.Attribute("formatCode"))
            Next
            Dim i = 0
            For Each xf In If(styles.Root.Element(S + "cellXfs")?.Elements(S + "xf"), Enumerable.Empty(Of XElement)())
                Dim id = CInt(If(CStr(xf.Attribute("numFmtId")), "0"))
                Dim code As String = Nothing
                Dim isDate = (id >= 14 AndAlso id <= 22) OrElse (id >= 45 AndAlso id <= 47) OrElse
                             (custom.TryGetValue(id, code) AndAlso Regex.IsMatch(Regex.Replace(code, "\[[^\]]*\]|""[^""]*""", ""), "[dy]|m{1,2}[^a-z]*[ds]|h", RegexOptions.IgnoreCase))
                If isDate Then result.Add(i)
                i += 1
            Next
            Return result
        End Function

        Private Shared Function ColumnIndex(cellRef As String) As Integer
            Dim n = 0
            For Each c In cellRef
                If Not Char.IsLetter(c) Then Exit For
                n = n * 26 + (AscW(Char.ToUpperInvariant(c)) - AscW("A"c) + 1)
            Next
            Return n - 1
        End Function

        Private Shared Function SheetRows(ws As XDocument, strings As List(Of String), dateStyles As HashSet(Of Integer)) As List(Of List(Of String))
            Dim rows As New List(Of List(Of String))
            Dim data = ws.Root.Element(S + "sheetData")
            If data Is Nothing Then Return rows
            Dim lastRow = 0
            For Each row In data.Elements(S + "row")
                Dim rowNum = CInt(If(CStr(row.Attribute("r")), (lastRow + 1).ToString(Inv)))
                While lastRow + 1 < rowNum AndAlso rows.Count < 100000
                    rows.Add(New List(Of String)) : lastRow += 1
                End While
                Dim cells As New List(Of String)
                Dim col = 0
                For Each c In row.Elements(S + "c")
                    Dim r = CStr(c.Attribute("r"))
                    If r IsNot Nothing Then col = ColumnIndex(r)
                    While cells.Count < col
                        cells.Add("")
                    End While
                    cells.Add(CellText(c, strings, dateStyles))
                    col += 1
                Next
                rows.Add(cells)
                lastRow = rowNum
            Next
            While rows.Count > 0 AndAlso rows(rows.Count - 1).All(Function(x) x.Length = 0)
                rows.RemoveAt(rows.Count - 1)
            End While
            Return rows
        End Function

        Private Shared Function CellText(c As XElement, strings As List(Of String), dateStyles As HashSet(Of Integer)) As String
            Dim t = CStr(c.Attribute("t"))
            Dim v = c.Element(S + "v")?.Value
            Select Case t
                Case "s"
                    Dim idx As Integer
                    Return If(Integer.TryParse(v, idx) AndAlso idx >= 0 AndAlso idx < strings.Count, strings(idx), "")
                Case "inlineStr" : Return String.Concat(c.Descendants(S + "t").Select(Function(x) x.Value))
                Case "b" : Return If(v = "1", "TRUE", "FALSE")
                Case "str", "e" : Return If(v, "")
            End Select
            If v Is Nothing Then Return ""
            Dim style = CInt(If(CStr(c.Attribute("s")), "0"))
            Dim num As Double
            If dateStyles.Contains(style) AndAlso Double.TryParse(v, NumberStyles.Float, Inv, num) AndAlso num > 0 AndAlso num < 2958466 Then
                Dim d = DateTime.FromOADate(num)
                Return If(d.TimeOfDay = TimeSpan.Zero, d.ToString("yyyy-MM-dd", Inv), d.ToString("yyyy-MM-dd HH:mm:ss", Inv))
            End If
            Return v
        End Function

        ' ---------------------------------------------------------------- PowerPoint

        Public Shared Function Pptx(full As String) As List(Of (Label As String, Text As String))
            Using zip = Open(full)
                Dim pres = Load(zip, "ppt/presentation.xml")
                If pres Is Nothing Then Throw New McpException("This is not a PowerPoint file (ppt/presentation.xml is missing).")
                Dim relMap = Rels(zip, "ppt/presentation.xml")
                Dim result As New List(Of (Label As String, Text As String))
                Dim n = 0
                For Each sld In If(pres.Root.Element(P + "sldIdLst")?.Elements(P + "sldId"), Enumerable.Empty(Of XElement)())
                    n += 1
                    Dim target As String = Nothing
                    If Not relMap.TryGetValue(CStr(sld.Attribute(R + "id")), target) Then Continue For
                    Dim slide = Load(zip, target)
                    If slide Is Nothing Then Continue For
                    Dim sb As New StringBuilder()
                    For Each para In slide.Descendants(A + "p")
                        Dim line = String.Concat(para.Descendants(A + "t").Select(Function(x) x.Value)).Trim()
                        If line.Length > 0 Then sb.AppendLine(line)
                    Next
                    For Each kv In Rels(zip, target)
                        If kv.Value.Contains("notesSlide", StringComparison.OrdinalIgnoreCase) Then
                            Dim notes = Load(zip, kv.Value)
                            Dim lines = notes?.Descendants(A + "p").Select(Function(x) String.Concat(x.Descendants(A + "t").Select(Function(t) t.Value)).Trim()).
                                Where(Function(x) x.Length > 0 AndAlso Not Regex.IsMatch(x, "^\d+$")).ToList()
                            If lines IsNot Nothing AndAlso lines.Count > 0 Then sb.AppendLine("Notes: " & String.Join(" ", lines))
                        End If
                    Next
                    result.Add(($"Slide {n}", sb.ToString().TrimEnd()))
                Next
                Return result
            End Using
        End Function

        Public Shared Function Info(full As String) As String
            Using zip = Open(full)
                Dim sb As New StringBuilder()
                Dim core = Load(zip, "docProps/core.xml")
                If core IsNot Nothing Then
                    For Each el In core.Root.Elements()
                        Dim v = el.Value.Trim()
                        If v.Length > 0 AndAlso {"title", "subject", "creator", "lastModifiedBy", "created", "modified"}.Contains(el.Name.LocalName) Then sb.AppendLine($"{el.Name.LocalName}: {v}")
                    Next
                End If
                Dim ext = Path.GetExtension(full).ToLowerInvariant()
                If ext.StartsWith(".xls") Then
                    Dim wb = Load(zip, "xl/workbook.xml")
                    Dim names = wb?.Root.Element(S + "sheets")?.Elements(S + "sheet").Select(Function(x) CStr(x.Attribute("name"))).ToList()
                    If names IsNot Nothing Then sb.AppendLine($"Sheets ({names.Count}): {String.Join(", ", names)}")
                ElseIf ext.StartsWith(".ppt") Then
                    Dim pres = Load(zip, "ppt/presentation.xml")
                    sb.AppendLine($"Slides: {pres?.Root.Element(P + "sldIdLst")?.Elements(P + "sldId").Count()}")
                Else
                    Dim app = Load(zip, "docProps/app.xml")
                    Dim ns As XNamespace = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties"
                    Dim pages = app?.Root.Element(ns + "Pages")?.Value
                    Dim words = app?.Root.Element(ns + "Words")?.Value
                    If pages IsNot Nothing Then sb.AppendLine($"Pages: {pages}{If(words IsNot Nothing, $", words: {words}", "")}")
                End If
                Return sb.ToString()
            End Using
        End Function
    End Class

    ' ============================================================================================== tables

    Friend NotInheritable Class TableText
        Private Sub New()
        End Sub

        Public Shared Function ParseCsv(text As String, Optional sep As Char = ","c) As List(Of List(Of String))
            Dim rows As New List(Of List(Of String))
            Dim row As New List(Of String)
            Dim cell As New StringBuilder()
            Dim quoted = False
            Dim i = 0
            Dim s = If(text, "").TrimStart(ChrW(&HFEFF))
            While i < s.Length
                Dim c = s(i)
                If quoted Then
                    If c = """"c Then
                        If i + 1 < s.Length AndAlso s(i + 1) = """"c Then
                            cell.Append(""""c) : i += 1
                        Else
                            quoted = False
                        End If
                    Else
                        cell.Append(c)
                    End If
                ElseIf c = """"c AndAlso cell.Length = 0 Then
                    quoted = True
                ElseIf c = sep Then
                    row.Add(cell.ToString()) : cell.Clear()
                ElseIf c = ControlChars.Lf OrElse c = ControlChars.Cr Then
                    If c = ControlChars.Cr AndAlso i + 1 < s.Length AndAlso s(i + 1) = ControlChars.Lf Then i += 1
                    row.Add(cell.ToString()) : cell.Clear()
                    rows.Add(row) : row = New List(Of String)
                Else
                    cell.Append(c)
                End If
                i += 1
            End While
            If cell.Length > 0 OrElse row.Count > 0 Then
                row.Add(cell.ToString())
                rows.Add(row)
            End If
            Return rows
        End Function

        Public Shared Function ToCsv(rows As List(Of List(Of String))) As String
            Dim sb As New StringBuilder()
            For Each r In rows
                sb.AppendLine(String.Join(",", r.Select(Function(c) If(c.IndexOfAny({","c, """"c, ControlChars.Lf, ControlChars.Cr}) >= 0, """" & c.Replace("""", """""") & """", c))))
            Next
            Return sb.ToString()
        End Function

        Public Shared Function ToMarkdown(rows As List(Of List(Of String)), maxRows As Integer) As String
            If rows Is Nothing OrElse rows.Count = 0 Then Return "(empty)"
            Dim width = rows.Max(Function(r) r.Count)
            If width = 0 Then Return "(empty)"
            Dim esc = Function(c As String) If(c, "").Replace("|", "\|").Replace(vbCr, "").Replace(vbLf, " ").Trim()
            Dim line = Function(r As List(Of String)) "| " & String.Join(" | ", Enumerable.Range(0, width).Select(Function(i) If(i < r.Count, esc(r(i)), ""))) & " |"
            Dim sb As New StringBuilder()
            sb.AppendLine(line(rows(0)))
            sb.AppendLine("|" & String.Concat(Enumerable.Repeat(" --- |", width)))
            For Each r In rows.Skip(1).Take(maxRows)
                sb.AppendLine(line(r))
            Next
            If rows.Count - 1 > maxRows Then sb.AppendLine($"({rows.Count - 1 - maxRows:N0} more rows not shown; {rows.Count - 1:N0} data rows in all)")
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>Sheets from CSV text, a JSON array of rows (arrays or objects), or a JSON object of sheet name -> rows.</summary>
        Public Shared Function ParseSheets(data As String, defaultName As String) As List(Of (Name As String, Rows As List(Of List(Of String))))
            Dim t = data.Trim()
            If t.StartsWith("[") OrElse t.StartsWith("{") Then
                Try
                    Using doc = JsonDocument.Parse(t)
                        If doc.RootElement.ValueKind = JsonValueKind.Array Then Return New List(Of (Name As String, Rows As List(Of List(Of String)))) From {(defaultName, JsonRows(doc.RootElement))}
                        Dim list As New List(Of (Name As String, Rows As List(Of List(Of String))))
                        For Each p In doc.RootElement.EnumerateObject()
                            If p.Value.ValueKind <> JsonValueKind.Array Then Throw New McpException($"Sheet '{p.Name}' must be an array of rows.")
                            list.Add((p.Name, JsonRows(p.Value)))
                        Next
                        If list.Count = 0 Then Throw New McpException("No sheets in the data.")
                        Return list
                    End Using
                Catch ex As JsonException
                    Throw New McpException("data looks like JSON but is not valid: " & ex.Message)
                End Try
            End If
            Return New List(Of (Name As String, Rows As List(Of List(Of String)))) From {(defaultName, ParseCsv(data))}
        End Function

        Private Shared Function JsonRows(arr As JsonElement) As List(Of List(Of String))
            Dim rows As New List(Of List(Of String))
            Dim headers As List(Of String) = Nothing
            For Each item In arr.EnumerateArray()
                If item.ValueKind = JsonValueKind.Object Then
                    If headers Is Nothing Then
                        headers = item.EnumerateObject().Select(Function(p) p.Name).ToList()
                        rows.Add(headers.ToList())
                    End If
                    Dim cells = headers.Select(Function(h)
                                                   Dim v As JsonElement
                                                   Return If(item.TryGetProperty(h, v), Scalar(v), "")
                                               End Function).ToList()
                    rows.Add(cells)
                ElseIf item.ValueKind = JsonValueKind.Array Then
                    rows.Add(item.EnumerateArray().Select(Function(v) Scalar(v)).ToList())
                Else
                    rows.Add(New List(Of String) From {Scalar(item)})
                End If
            Next
            Return rows
        End Function

        Private Shared Function Scalar(v As JsonElement) As String
            Select Case v.ValueKind
                Case JsonValueKind.String : Return v.GetString()
                Case JsonValueKind.Null, JsonValueKind.Undefined : Return ""
                Case JsonValueKind.True : Return "TRUE"
                Case JsonValueKind.False : Return "FALSE"
                Case Else : Return v.GetRawText()
            End Select
        End Function
    End Class

    ' ============================================================================================== Markdown blocks

    ''' <summary>The Markdown subset the writers understand.</summary>
    Friend NotInheritable Class MarkdownBlocks
        Private Sub New()
        End Sub

        Public Enum Kind
            Heading
            Paragraph
            Bullet
            Numbered
            Table
            Code
            Rule
            PageBreak
        End Enum

        Public NotInheritable Class Block
            Public Property Kind As Kind
            Public Property Level As Integer
            Public Property Text As String
            Public Property Rows As List(Of List(Of String))
        End Class

        Public Shared Function Parse(md As String) As List(Of Block)
            Dim blocks As New List(Of Block)
            Dim lines = md.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf).Split(ControlChars.Lf)
            Dim para As New List(Of String)
            Dim flush = Sub()
                            If para.Count > 0 Then blocks.Add(New Block With {.Kind = Kind.Paragraph, .Text = String.Join(" ", para.Select(Function(x) x.Trim()))})
                            para.Clear()
                        End Sub
            Dim i = 0
            While i < lines.Length
                Dim line = lines(i)
                Dim t = line.Trim()
                If t.StartsWith("```") Then
                    flush()
                    Dim code As New List(Of String)
                    i += 1
                    While i < lines.Length AndAlso Not lines(i).Trim().StartsWith("```")
                        code.Add(lines(i)) : i += 1
                    End While
                    blocks.Add(New Block With {.Kind = Kind.Code, .Text = String.Join(vbLf, code)})
                ElseIf t.Length = 0 Then
                    flush()
                ElseIf Regex.IsMatch(t, "^#{1,6}\s") Then
                    flush()
                    Dim level = t.TakeWhile(Function(c) c = "#"c).Count()
                    blocks.Add(New Block With {.Kind = Kind.Heading, .Level = level, .Text = t.Substring(level).Trim()})
                ElseIf t = "---" OrElse t = "***" OrElse t = "___" Then
                    flush()
                    blocks.Add(New Block With {.Kind = If(t = "---", Kind.PageBreak, Kind.Rule)})
                ElseIf t.StartsWith("|") Then
                    flush()
                    Dim rows As New List(Of List(Of String))
                    While i < lines.Length AndAlso lines(i).Trim().StartsWith("|")
                        Dim cells = lines(i).Trim().Trim("|"c).Split("|"c).Select(Function(c) c.Trim()).ToList()
                        If Not cells.All(Function(c) Regex.IsMatch(c, "^:?-{2,}:?$")) Then rows.Add(cells)
                        i += 1
                    End While
                    i -= 1
                    blocks.Add(New Block With {.Kind = Kind.Table, .Rows = rows})
                Else
                    Dim bm = Regex.Match(line, "^(\s*)[-*+]\s+(.*)$")
                    Dim nm = Regex.Match(line, "^(\s*)\d+[.)]\s+(.*)$")
                    If bm.Success Then
                        flush()
                        blocks.Add(New Block With {.Kind = Kind.Bullet, .Level = bm.Groups(1).Value.Length \ 2, .Text = bm.Groups(2).Value.Trim()})
                    ElseIf nm.Success Then
                        flush()
                        blocks.Add(New Block With {.Kind = Kind.Numbered, .Level = nm.Groups(1).Value.Length \ 2, .Text = nm.Groups(2).Value.Trim()})
                    Else
                        para.Add(line)
                    End If
                End If
                i += 1
            End While
            flush()
            Return blocks
        End Function

        ''' <summary>Inline spans: (text, bold, italic, code).</summary>
        Public Shared Function Spans(text As String) As List(Of (Text As String, Bold As Boolean, Italic As Boolean, Code As Boolean))
            Dim result As New List(Of (Text As String, Bold As Boolean, Italic As Boolean, Code As Boolean))
            Dim re As New Regex("(\*\*([^*]+)\*\*|__([^_]+)__|`([^`]+)`|\*([^*]+)\*|\[([^\]]+)\]\(([^)]+)\))")
            Dim pos = 0
            For Each m As Match In re.Matches(If(text, ""))
                If m.Index > pos Then result.Add((text.Substring(pos, m.Index - pos), False, False, False))
                If m.Groups(2).Success Then
                    result.Add((m.Groups(2).Value, True, False, False))
                ElseIf m.Groups(3).Success Then
                    result.Add((m.Groups(3).Value, True, False, False))
                ElseIf m.Groups(4).Success Then
                    result.Add((m.Groups(4).Value, False, False, True))
                ElseIf m.Groups(5).Success Then
                    result.Add((m.Groups(5).Value, False, True, False))
                Else
                    result.Add(($"{m.Groups(6).Value} ({m.Groups(7).Value})", False, False, False))
                End If
                pos = m.Index + m.Length
            Next
            If pos < If(text, "").Length Then result.Add((text.Substring(pos), False, False, False))
            Return result
        End Function

        Public Shared Function Plain(text As String) As String
            Return String.Concat(Spans(text).Select(Function(s) s.Text))
        End Function

        Public Shared Function ToHtml(blocks As List(Of Block), title As String) As String
            Dim enc = Function(s As String) Net.WebUtility.HtmlEncode(s)
            Dim inline = Function(s As String) String.Concat(Spans(s).Select(Function(x) If(x.Bold, $"<strong>{enc(x.Text)}</strong>", If(x.Italic, $"<em>{enc(x.Text)}</em>", If(x.Code, $"<code>{enc(x.Text)}</code>", enc(x.Text))))))
            Dim sb As New StringBuilder()
            sb.AppendLine("<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width, initial-scale=1"">")
            sb.AppendLine($"<title>{enc(If(String.IsNullOrWhiteSpace(title), "Document", title))}</title>")
            sb.AppendLine("<style>body{font-family:system-ui,Segoe UI,Arial,sans-serif;max-width:860px;margin:2rem auto;padding:0 1rem;line-height:1.55;color:#1c1c1c}table{border-collapse:collapse;margin:1rem 0}td,th{border:1px solid #ccc;padding:.35rem .6rem;text-align:left}th{background:#f3f3f3}pre{background:#f6f6f6;padding:.8rem;overflow:auto}code{background:#f3f3f3;padding:0 .2rem}</style></head><body>")
            If Not String.IsNullOrWhiteSpace(title) Then sb.AppendLine($"<h1>{enc(title)}</h1>")
            Dim openList As String = Nothing
            For Each b In blocks
                Dim listTag = If(b.Kind = Kind.Bullet, "ul", If(b.Kind = Kind.Numbered, "ol", Nothing))
                If openList IsNot Nothing AndAlso openList <> listTag Then sb.AppendLine($"</{openList}>") : openList = Nothing
                Select Case b.Kind
                    Case Kind.Heading : sb.AppendLine($"<h{Math.Min(6, b.Level)}>{inline(b.Text)}</h{Math.Min(6, b.Level)}>")
                    Case Kind.Paragraph : sb.AppendLine($"<p>{inline(b.Text)}</p>")
                    Case Kind.Bullet, Kind.Numbered
                        If openList Is Nothing Then sb.AppendLine($"<{listTag}>") : openList = listTag
                        sb.AppendLine($"<li>{inline(b.Text)}</li>")
                    Case Kind.Code : sb.AppendLine($"<pre><code>{enc(b.Text)}</code></pre>")
                    Case Kind.Rule, Kind.PageBreak : sb.AppendLine("<hr>")
                    Case Kind.Table
                        sb.AppendLine("<table>")
                        For r = 0 To b.Rows.Count - 1
                            Dim tag = If(r = 0, "th", "td")
                            sb.AppendLine("<tr>" & String.Concat(b.Rows(r).Select(Function(c) $"<{tag}>{inline(c)}</{tag}>")) & "</tr>")
                        Next
                        sb.AppendLine("</table>")
                End Select
            Next
            If openList IsNot Nothing Then sb.AppendLine($"</{openList}>")
            sb.AppendLine("</body></html>")
            Return sb.ToString()
        End Function
    End Class

End Namespace
