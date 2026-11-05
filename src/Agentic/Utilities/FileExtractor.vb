Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Xml

Namespace Utilities

    ''' <summary>What a file is and the text inside it.</summary>
    Public NotInheritable Class FileExtraction
        ''' <summary>text | code | document | spreadsheet | presentation | pdf | archive | image | binary</summary>
        Public Property Kind As String
        Public Property MimeType As String
        ''' <summary>One line for people and prompts: "Excel workbook: 2 sheets (Sales 120 rows, Costs 14 rows)".</summary>
        Public Property Summary As String
        ''' <summary>Readable text (Nothing for images and unknown binaries).</summary>
        Public Property Text As String
        Public Property Truncated As Boolean
    End Class

    ''' <summary>
    ''' Reads the text out of files administrators attach to tasks so the agents (whose models only read text) know what
    ''' they are dealing with: plain text and source code, Word/Excel/PowerPoint (Office Open XML and OpenDocument), PDF,
    ''' RTF, ZIP archives (listing plus the text of the files inside), legacy .doc/.xls/.ppt (best effort) and images
    ''' (format and size; their content is read with analyze_image). No external libraries.
    ''' </summary>
    Public Module FileExtractor

        Private ReadOnly CodeExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            ".cs", ".vb", ".fs", ".csproj", ".vbproj", ".fsproj", ".sln", ".slnx", ".props", ".targets", ".resx", ".xaml", ".razor", ".cshtml", ".vbhtml",
            ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".css", ".scss", ".less", ".html", ".htm",
            ".py", ".rb", ".php", ".java", ".kt", ".kts", ".scala", ".go", ".rs", ".swift", ".m", ".mm", ".c", ".h", ".cpp", ".cc", ".cxx", ".hpp", ".hh",
            ".sql", ".ps1", ".psm1", ".psd1", ".sh", ".bash", ".zsh", ".bat", ".cmd", ".lua", ".pl", ".r", ".dart", ".groovy", ".gradle",
            ".asm", ".s", ".pas", ".bas", ".vbs", ".frm", ".cls", ".ahk", ".au3", ".dockerfile", ".tf", ".hcl", ".proto", ".graphql", ".gql"}
        Private ReadOnly TextExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            ".txt", ".md", ".markdown", ".rst", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".ini", ".cfg", ".conf", ".config",
            ".log", ".env", ".properties", ".editorconfig", ".gitignore", ".gitattributes", ".svg", ".tex", ".srt", ".vtt", ".nfo", ".diff", ".patch", ".manifest"}
        Private ReadOnly Mimes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {".txt", "text/plain"}, {".md", "text/markdown"}, {".csv", "text/csv"}, {".tsv", "text/tab-separated-values"}, {".json", "application/json"},
            {".xml", "application/xml"}, {".yaml", "text/yaml"}, {".yml", "text/yaml"}, {".html", "text/html"}, {".htm", "text/html"}, {".css", "text/css"},
            {".js", "text/javascript"}, {".svg", "image/svg+xml"}, {".png", "image/png"}, {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".gif", "image/gif"},
            {".bmp", "image/bmp"}, {".webp", "image/webp"}, {".ico", "image/x-icon"}, {".tif", "image/tiff"}, {".tiff", "image/tiff"}, {".pdf", "application/pdf"},
            {".zip", "application/zip"}, {".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document"},
            {".xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"}, {".pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"},
            {".doc", "application/msword"}, {".xls", "application/vnd.ms-excel"}, {".ppt", "application/vnd.ms-powerpoint"}, {".rtf", "application/rtf"},
            {".odt", "application/vnd.oasis.opendocument.text"}, {".ods", "application/vnd.oasis.opendocument.spreadsheet"},
            {".odp", "application/vnd.oasis.opendocument.presentation"}, {".mp3", "audio/mpeg"}, {".wav", "audio/wav"}, {".mp4", "video/mp4"}, {".webm", "video/webm"}, {".mov", "video/quicktime"},
            {".exe", "application/vnd.microsoft.portable-executable"}, {".dll", "application/vnd.microsoft.portable-executable"}}
        Private ReadOnly ImageExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff"}

        Public Function MimeTypeFor(fileName As String) As String
            Dim ext = Path.GetExtension(If(fileName, ""))
            Dim m As String = Nothing
            If Mimes.TryGetValue(ext, m) Then Return m
            If CodeExtensions.Contains(ext) OrElse TextExtensions.Contains(ext) Then Return "text/plain"
            Return "application/octet-stream"
        End Function

        Public Function IsImage(fileName As String) As Boolean
            Return ImageExtensions.Contains(Path.GetExtension(If(fileName, "")))
        End Function

        ''' <summary>True for files whose bytes are readable text (source code, configuration, data files).</summary>
        Public Function IsTextFile(fileName As String) As Boolean
            Dim ext = Path.GetExtension(If(fileName, ""))
            Return CodeExtensions.Contains(ext) OrElse TextExtensions.Contains(ext)
        End Function

        Public Function Extract(fileName As String, data As Byte(), Optional maxChars As Integer = 60000) As FileExtraction
            Return ExtractCore(If(fileName, "file"), If(data, Array.Empty(Of Byte)()), Math.Max(1000, maxChars), 0)
        End Function

        Private Function ExtractCore(fileName As String, data As Byte(), maxChars As Integer, depth As Integer) As FileExtraction
            Dim ext = Path.GetExtension(fileName).ToLowerInvariant()
            Dim result As FileExtraction
            Try
                Select Case ext
                    Case ".docx", ".docm", ".dotx", ".dotm" : result = Docx(data)
                    Case ".xlsx", ".xlsm", ".xltx", ".xltm" : result = Xlsx(data, maxChars)
                    Case ".pptx", ".pptm", ".potx" : result = Pptx(data)
                    Case ".odt", ".ods", ".odp" : result = OpenDocument(data, ext)
                    Case ".pdf" : result = Pdf(data)
                    Case ".rtf" : result = Rtf(data)
                    Case ".zip", ".nupkg", ".jar", ".vsix", ".apk", ".epub" : result = ZipArchiveText(data, maxChars, depth)
                    Case ".doc", ".xls", ".ppt", ".msg" : result = LegacyOffice(data, ext)
                    Case Else
                        If ImageExtensions.Contains(ext) Then
                            result = Image(data, ext)
                        ElseIf CodeExtensions.Contains(ext) OrElse TextExtensions.Contains(ext) OrElse LooksLikeText(data) Then
                            Dim text = DecodeText(data)
                            Dim lines = CountLines(text)
                            Dim isCode = CodeExtensions.Contains(ext)
                            result = New FileExtraction With {.Kind = If(isCode, "code", "text"), .Text = text,
                                .Summary = $"{If(isCode, LanguageName(ext) & " source", If(ext = ".csv", "CSV data", "Text file"))}, {lines:N0} line{If(lines = 1, "", "s")}"}
                        Else
                            result = New FileExtraction With {.Kind = "binary", .Summary = $"Binary file ({SizeText(data.LongLength)}); no readable text"}
                        End If
                End Select
            Catch ex As Exception When TypeOf ex Is InvalidDataException OrElse TypeOf ex Is XmlException OrElse TypeOf ex Is IOException OrElse
                                       TypeOf ex Is IndexOutOfRangeException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is FormatException
                result = New FileExtraction With {.Kind = "binary", .Summary = $"Could not be read as {ext.TrimStart("."c).ToUpperInvariant()} ({ex.Message})"}
            End Try
            result.MimeType = MimeTypeFor(fileName)
            If result.Text IsNot Nothing Then
                result.Text = result.Text.Replace(vbCrLf, vbLf).Replace(vbNullChar, "")
                If result.Text.Length > maxChars Then
                    result.Text = result.Text.Substring(0, maxChars) & vbLf & $"… [truncated: {result.Text.Length:N0} characters in total]"
                    result.Truncated = True
                End If
            End If
            Return result
        End Function

        ' ------------------------------------------------------------------ text

        Public Function DecodeText(data As Byte()) As String
            If data.Length >= 3 AndAlso data(0) = &HEF AndAlso data(1) = &HBB AndAlso data(2) = &HBF Then Return Encoding.UTF8.GetString(data, 3, data.Length - 3)
            If data.Length >= 2 AndAlso data(0) = &HFF AndAlso data(1) = &HFE Then Return Encoding.Unicode.GetString(data, 2, data.Length - 2)
            If data.Length >= 2 AndAlso data(0) = &HFE AndAlso data(1) = &HFF Then Return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2)
            Try
                Return New UTF8Encoding(False, True).GetString(data)
            Catch ex As DecoderFallbackException
                Return Encoding.Latin1.GetString(data)
            End Try
        End Function

        Private Function LooksLikeText(data As Byte()) As Boolean
            If data.Length = 0 Then Return True
            Dim n = Math.Min(data.Length, 8192)
            Dim control = 0
            For i = 0 To n - 1
                Dim b = data(i)
                If b = 0 Then Return False
                If b < 9 OrElse (b > 13 AndAlso b < 32) Then control += 1
            Next
            Return control < n \ 50
        End Function

        Private Function CountLines(s As String) As Integer
            If String.IsNullOrEmpty(s) Then Return 0
            Return s.Count(Function(c) c = vbLf(0)) + If(s.EndsWith(vbLf), 0, 1)
        End Function

        Private Function LanguageName(ext As String) As String
            Select Case ext
                Case ".cs" : Return "C#"
                Case ".vb", ".bas", ".frm", ".cls" : Return "Visual Basic"
                Case ".fs" : Return "F#"
                Case ".csproj", ".vbproj", ".fsproj", ".props", ".targets" : Return "MSBuild project"
                Case ".sln", ".slnx" : Return "Visual Studio solution"
                Case ".js", ".mjs", ".cjs", ".jsx" : Return "JavaScript"
                Case ".ts", ".tsx" : Return "TypeScript"
                Case ".py" : Return "Python"
                Case ".java" : Return "Java"
                Case ".go" : Return "Go"
                Case ".rs" : Return "Rust"
                Case ".c", ".h" : Return "C"
                Case ".cpp", ".cc", ".cxx", ".hpp", ".hh" : Return "C++"
                Case ".sql" : Return "SQL"
                Case ".ps1", ".psm1", ".psd1" : Return "PowerShell"
                Case ".html", ".htm", ".cshtml", ".vbhtml", ".razor" : Return "HTML"
                Case ".css", ".scss", ".less" : Return "Stylesheet"
                Case ".xaml" : Return "XAML"
                Case Else : Return ext.TrimStart("."c).ToUpperInvariant()
            End Select
        End Function

        Public Function SizeText(bytes As Long) As String
            If bytes < 1024 Then Return $"{bytes} B"
            If bytes < 1024L * 1024 Then Return $"{bytes / 1024.0:0.#} KB"
            Return $"{bytes / 1024.0 / 1024.0:0.#} MB"
        End Function

        ' ------------------------------------------------------------------ Office Open XML / OpenDocument

        Private Function ReadEntry(zip As ZipArchive, name As String) As String
            Dim e = zip.GetEntry(name)
            If e Is Nothing Then Return Nothing
            Using s = e.Open(), r As New StreamReader(s, Encoding.UTF8)
                Return r.ReadToEnd()
            End Using
        End Function

        ''' <summary>Walks XML: text of elements named in textNames, a newline after paraNames, a tab after cellNames.</summary>
        Private Function XmlText(xml As String, textNames As String(), paraNames As String(), Optional cellNames As String() = Nothing, Optional tabNames As String() = Nothing) As String
            If String.IsNullOrEmpty(xml) Then Return ""
            Dim sb As New StringBuilder()
            Dim settings As New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing, .IgnoreComments = True}
            Using r = XmlReader.Create(New StringReader(xml), settings)
                Dim inText = 0
                While r.Read()
                    Select Case r.NodeType
                        Case XmlNodeType.Element
                            If textNames.Contains(r.LocalName) AndAlso Not r.IsEmptyElement Then inText += 1
                            If tabNames IsNot Nothing AndAlso tabNames.Contains(r.LocalName) Then sb.Append(vbTab)
                            If r.LocalName = "br" OrElse r.LocalName = "line-break" Then sb.Append(vbLf)
                            If r.LocalName = "s" AndAlso r.Prefix = "text" Then sb.Append(" "c)
                        Case XmlNodeType.Text, XmlNodeType.SignificantWhitespace, XmlNodeType.Whitespace
                            If inText > 0 Then sb.Append(r.Value)
                        Case XmlNodeType.EndElement
                            If textNames.Contains(r.LocalName) Then inText = Math.Max(0, inText - 1)
                            If paraNames.Contains(r.LocalName) Then sb.Append(vbLf)
                            If cellNames IsNot Nothing AndAlso cellNames.Contains(r.LocalName) Then sb.Append(vbTab)
                    End Select
                End While
            End Using
            Return Regex.Replace(sb.ToString(), "\n{3,}", vbLf & vbLf).Trim()
        End Function

        Private Function Docx(data As Byte()) As FileExtraction
            Using zip As New ZipArchive(New MemoryStream(data), ZipArchiveMode.Read)
                Dim body = XmlText(ReadEntry(zip, "word/document.xml"), {"t"}, {"p", "tr"}, {"tc"}, {"tab"})
                Dim parts As New List(Of String) From {body}
                For Each e In zip.Entries.Where(Function(x) Regex.IsMatch(x.FullName, "^word/(header|footer|footnotes|endnotes)\d*\.xml$")).OrderBy(Function(x) x.FullName)
                    Dim t = XmlText(ReadEntry(zip, e.FullName), {"t"}, {"p"})
                    If t.Length > 0 Then parts.Add($"[{Path.GetFileNameWithoutExtension(e.FullName)}]" & vbLf & t)
                Next
                Dim text = String.Join(vbLf & vbLf, parts.Where(Function(p) p.Length > 0))
                Dim words = Regex.Matches(body, "\S+").Count
                Dim images = zip.Entries.Where(Function(x) x.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase)).Count()
                Return New FileExtraction With {.Kind = "document", .Text = text,
                    .Summary = $"Word document, about {words:N0} words" & If(images > 0, $", {images} embedded image{If(images = 1, "", "s")}", "")}
            End Using
        End Function

        Private Function ColumnIndex(cellRef As String) As Integer
            Dim n = 0
            For Each ch In cellRef
                If Not Char.IsLetter(ch) Then Exit For
                n = n * 26 + (AscW(Char.ToUpperInvariant(ch)) - AscW("A"c) + 1)
            Next
            Return Math.Max(0, n - 1)
        End Function

        Private Function Xlsx(data As Byte(), maxChars As Integer) As FileExtraction
            Using zip As New ZipArchive(New MemoryStream(data), ZipArchiveMode.Read)
                ' Shared strings: one entry per <si>, the concatenation of its <t> runs.
                Dim sharedStrings As New List(Of String)
                Dim ssXml = ReadEntry(zip, "xl/sharedStrings.xml")
                If ssXml IsNot Nothing Then
                    Dim settings As New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing}
                    Using r = XmlReader.Create(New StringReader(ssXml), settings)
                        Dim cur As StringBuilder = Nothing
                        Dim inT = False
                        While r.Read()
                            If r.NodeType = XmlNodeType.Element AndAlso r.LocalName = "si" Then cur = New StringBuilder()
                            If r.NodeType = XmlNodeType.Element AndAlso r.LocalName = "t" AndAlso Not r.IsEmptyElement Then inT = True
                            If r.NodeType = XmlNodeType.EndElement AndAlso r.LocalName = "t" Then inT = False
                            If (r.NodeType = XmlNodeType.Text OrElse r.NodeType = XmlNodeType.SignificantWhitespace) AndAlso inT AndAlso cur IsNot Nothing Then cur.Append(r.Value)
                            If r.NodeType = XmlNodeType.EndElement AndAlso r.LocalName = "si" AndAlso cur IsNot Nothing Then sharedStrings.Add(cur.ToString()) : cur = Nothing
                        End While
                    End Using
                End If
                ' Sheet names in workbook order; sheet files are matched through the workbook relationships.
                Dim names As New List(Of (Name As String, File As String))
                Dim wb = ReadEntry(zip, "xl/workbook.xml")
                Dim rels = ReadEntry(zip, "xl/_rels/workbook.xml.rels")
                Dim relMap As New Dictionary(Of String, String)
                If rels IsNot Nothing Then
                    For Each m As Match In Regex.Matches(rels, "<Relationship\b[^>]*>")
                        Dim id = Regex.Match(m.Value, "\bId=""([^""]+)""").Groups(1).Value
                        Dim target = Regex.Match(m.Value, "\bTarget=""([^""]+)""").Groups(1).Value
                        If id.Length > 0 Then relMap(id) = If(target.StartsWith("/"), target.TrimStart("/"c), "xl/" & target)
                    Next
                End If
                If wb IsNot Nothing Then
                    For Each m As Match In Regex.Matches(wb, "<sheet\b[^>]*>")
                        Dim nm = Net.WebUtility.HtmlDecode(Regex.Match(m.Value, "\bname=""([^""]*)""").Groups(1).Value)
                        Dim rid = Regex.Match(m.Value, "\br:id=""([^""]+)""").Groups(1).Value
                        Dim file As String = Nothing
                        If relMap.TryGetValue(rid, file) Then names.Add((nm, file))
                    Next
                End If
                If names.Count = 0 Then
                    For Each e In zip.Entries.Where(Function(x) Regex.IsMatch(x.FullName, "^xl/worksheets/sheet\d+\.xml$")).OrderBy(Function(x) CInt(Regex.Match(x.FullName, "\d+").Value))
                        names.Add((Path.GetFileNameWithoutExtension(e.FullName), e.FullName))
                    Next
                End If
                Dim sb As New StringBuilder()
                Dim sheetInfo As New List(Of String)
                For Each sh In names
                    Dim xml = ReadEntry(zip, sh.File)
                    If xml Is Nothing Then Continue For
                    Dim rows = 0
                    sb.Append("## Sheet: ").Append(sh.Name).Append(vbLf)
                    Dim settings As New XmlReaderSettings With {.DtdProcessing = DtdProcessing.Prohibit, .XmlResolver = Nothing}
                    Using r = XmlReader.Create(New StringReader(xml), settings)
                        Dim row As New SortedDictionary(Of Integer, String)
                        Dim cellCol = 0, cellType = "", inV = False, inIs = False
                        Dim val As New StringBuilder()
                        While r.Read()
                            Select Case r.NodeType
                                Case XmlNodeType.Element
                                    Select Case r.LocalName
                                        Case "row" : row.Clear()
                                        Case "c" : cellCol = ColumnIndex(If(r.GetAttribute("r"), "")) : cellType = If(r.GetAttribute("t"), "") : val.Clear()
                                            If r.GetAttribute("r") Is Nothing Then cellCol = If(row.Count = 0, 0, row.Keys.Last() + 1)
                                        Case "v" : inV = Not r.IsEmptyElement
                                        Case "is" : inIs = True
                                        Case "t" : If inIs AndAlso Not r.IsEmptyElement Then inV = True
                                    End Select
                                Case XmlNodeType.Text, XmlNodeType.SignificantWhitespace
                                    If inV Then val.Append(r.Value)
                                Case XmlNodeType.EndElement
                                    Select Case r.LocalName
                                        Case "v", "t" : inV = False
                                        Case "is" : inIs = False
                                        Case "c"
                                            Dim v = val.ToString()
                                            If cellType = "s" Then
                                                Dim idx As Integer
                                                If Integer.TryParse(v, idx) AndAlso idx >= 0 AndAlso idx < sharedStrings.Count Then v = sharedStrings(idx)
                                            ElseIf cellType = "b" Then
                                                v = If(v = "1", "TRUE", "FALSE")
                                            End If
                                            If v.Length > 0 Then row(cellCol) = v.Replace(vbTab, " ").Replace(vbLf, " ").Replace(vbCr, "")
                                        Case "row"
                                            If row.Count > 0 Then
                                                rows += 1
                                                If sb.Length < maxChars Then
                                                    Dim last = row.Keys.Max()
                                                    Dim cells = Enumerable.Range(0, Math.Min(last + 1, 60)).Select(Function(i) If(row.ContainsKey(i), row(i), ""))
                                                    sb.Append(String.Join(vbTab, cells)).Append(vbLf)
                                                End If
                                            End If
                                    End Select
                            End Select
                        End While
                    End Using
                    sheetInfo.Add($"{sh.Name} {rows:N0} row{If(rows = 1, "", "s")}")
                    sb.Append(vbLf)
                Next
                Return New FileExtraction With {.Kind = "spreadsheet", .Text = sb.ToString().TrimEnd(),
                    .Summary = $"Excel workbook: {names.Count} sheet{If(names.Count = 1, "", "s")} ({String.Join(", ", sheetInfo)}); cells are tab-separated"}
            End Using
        End Function

        Private Function Pptx(data As Byte()) As FileExtraction
            Using zip As New ZipArchive(New MemoryStream(data), ZipArchiveMode.Read)
                Dim slides = zip.Entries.Where(Function(e) Regex.IsMatch(e.FullName, "^ppt/slides/slide\d+\.xml$")).
                    OrderBy(Function(e) CInt(Regex.Match(e.FullName, "slide(\d+)").Groups(1).Value)).ToList()
                Dim sb As New StringBuilder()
                Dim i = 0
                For Each s In slides
                    i += 1
                    sb.Append($"## Slide {i}").Append(vbLf).Append(XmlText(ReadEntry(zip, s.FullName), {"t"}, {"p"})).Append(vbLf)
                    Dim notesName = $"ppt/notesSlides/notesSlide{Regex.Match(s.FullName, "slide(\d+)").Groups(1).Value}.xml"
                    Dim notes = XmlText(ReadEntry(zip, notesName), {"t"}, {"p"})
                    If notes.Length > 0 Then sb.Append("Notes: ").Append(notes).Append(vbLf)
                    sb.Append(vbLf)
                Next
                Return New FileExtraction With {.Kind = "presentation", .Text = sb.ToString().TrimEnd(), .Summary = $"PowerPoint presentation, {slides.Count} slide{If(slides.Count = 1, "", "s")}"}
            End Using
        End Function

        Private Function OpenDocument(data As Byte(), ext As String) As FileExtraction
            Using zip As New ZipArchive(New MemoryStream(data), ZipArchiveMode.Read)
                Dim text = XmlText(ReadEntry(zip, "content.xml"), {"p", "h", "span", "a"}, {"p", "h", "table-row"}, {"table-cell"}, {"tab"})
                Dim kind = If(ext = ".ods", "spreadsheet", If(ext = ".odp", "presentation", "document"))
                Dim label = If(ext = ".ods", "OpenDocument spreadsheet", If(ext = ".odp", "OpenDocument presentation", "OpenDocument text"))
                Return New FileExtraction With {.Kind = kind, .Text = text, .Summary = $"{label}, about {Regex.Matches(text, "\S+").Count:N0} words"}
            End Using
        End Function

        ' ------------------------------------------------------------------ PDF (text operators of the content streams)

        Private Function Pdf(data As Byte()) As FileExtraction
            Dim raw = Encoding.Latin1.GetString(data)
            Dim pages = Regex.Matches(raw, "/Type\s*/Page\b(?!s)").Count
            Dim sb As New StringBuilder()
            Dim pos = 0
            While sb.Length < 400000
                Dim s = raw.IndexOf("stream", pos, StringComparison.Ordinal)
                If s < 0 Then Exit While
                If s >= 3 AndAlso raw.Substring(s - 3, 3) = "end" Then pos = s + 6 : Continue While
                Dim dictStart = raw.LastIndexOf("<<", s, Math.Min(s, 2000), StringComparison.Ordinal)
                Dim dict = If(dictStart >= 0, raw.Substring(dictStart, s - dictStart), "")
                Dim begin = s + 6
                If begin < raw.Length AndAlso raw(begin) = vbCr(0) Then begin += 1
                If begin < raw.Length AndAlso raw(begin) = vbLf(0) Then begin += 1
                Dim [end] = raw.IndexOf("endstream", begin, StringComparison.Ordinal)
                If [end] < 0 Then Exit While
                pos = [end] + 9
                ' Skip images, fonts and other binary streams.
                If Regex.IsMatch(dict, "/Subtype\s*/(Image|Form)|/Length1|/Length2|/Type\s*/(XObject|XRef|ObjStm|EmbeddedFile|Metadata)|/DCTDecode|/JPXDecode|/CCITT") Then Continue While
                Dim bytes = data.AsSpan(begin, Math.Max(0, [end] - begin)).ToArray()
                Dim content As String
                If dict.Contains("/FlateDecode") Then
                    Try
                        Using inflater As New ZLibStream(New MemoryStream(bytes), CompressionMode.Decompress), ms As New MemoryStream()
                            inflater.CopyTo(ms)
                            content = Encoding.Latin1.GetString(ms.ToArray())
                        End Using
                    Catch ex As InvalidDataException
                        Continue While
                    End Try
                ElseIf Regex.IsMatch(dict, "/Filter") Then
                    Continue While
                Else
                    content = Encoding.Latin1.GetString(bytes)
                End If
                If content.Contains("BT") Then PdfContentText(content, sb)
            End While
            Dim text = Regex.Replace(sb.ToString(), "[ \t]+\n", vbLf)
            text = Regex.Replace(text, "\n{3,}", vbLf & vbLf).Trim()
            Dim printable = If(text.Length = 0, 0.0, text.Count(Function(c) Not Char.IsControl(c) OrElse c = vbLf(0)) / CDbl(text.Length))
            Dim letters = text.Count(Function(c) Char.IsLetterOrDigit(c))
            Dim pageText = If(pages > 0, $"{pages} page{If(pages = 1, "", "s")}", "PDF")
            If letters < 20 OrElse printable < 0.85 Then
                Return New FileExtraction With {.Kind = "pdf", .Summary = $"PDF, {pageText}; no extractable text (scanned or uses custom font encodings)", .Text = Nothing}
            End If
            Return New FileExtraction With {.Kind = "pdf", .Text = text, .Summary = $"PDF, {pageText}, about {Regex.Matches(text, "\S+").Count:N0} words (text extracted; layout is approximate)"}
        End Function

        ''' <summary>Text shown by Tj, TJ, ' and " in one content stream, with line breaks at text moves.</summary>
        Private Sub PdfContentText(c As String, sb As StringBuilder)
            Dim i = 0
            Dim operands As New List(Of String)
            Dim arrayParts As List(Of (Text As String, Kern As Double)) = Nothing
            Dim lastArray As List(Of (Text As String, Kern As Double)) = Nothing
            While i < c.Length
                Dim ch = c(i)
                If ch = "("c Then
                    Dim depth = 1, j = i + 1
                    Dim lit As New StringBuilder()
                    While j < c.Length AndAlso depth > 0
                        Dim x = c(j)
                        If x = "\"c AndAlso j + 1 < c.Length Then
                            Dim n = c(j + 1)
                            Select Case n
                                Case "n"c : lit.Append(vbLf) : j += 2
                                Case "r"c : j += 2
                                Case "t"c : lit.Append(vbTab) : j += 2
                                Case "b"c, "f"c : j += 2
                                Case "("c, ")"c, "\"c : lit.Append(n) : j += 2
                                Case Else
                                    If n >= "0"c AndAlso n <= "7"c Then
                                        Dim k = j + 1, oct = 0, cnt = 0
                                        While k < c.Length AndAlso cnt < 3 AndAlso c(k) >= "0"c AndAlso c(k) <= "7"c
                                            oct = oct * 8 + (AscW(c(k)) - 48) : k += 1 : cnt += 1
                                        End While
                                        lit.Append(ChrW(oct And 255)) : j = k
                                    Else
                                        j += 2
                                    End If
                            End Select
                            Continue While
                        End If
                        If x = "("c Then depth += 1
                        If x = ")"c Then depth -= 1 : If depth = 0 Then Exit While
                        lit.Append(x)
                        j += 1
                    End While
                    Dim s = DecodePdfString(lit.ToString())
                    If arrayParts IsNot Nothing Then arrayParts.Add((s, 0)) Else operands.Add(s)
                    i = j + 1
                    Continue While
                End If
                If ch = "<"c AndAlso i + 1 < c.Length AndAlso c(i + 1) <> "<"c Then
                    Dim j = c.IndexOf(">"c, i + 1)
                    If j < 0 Then Exit While
                    Dim s = DecodeHexString(c.Substring(i + 1, j - i - 1))
                    If arrayParts IsNot Nothing Then arrayParts.Add((s, 0)) Else operands.Add(s)
                    i = j + 1
                    Continue While
                End If
                If ch = "["c Then arrayParts = New List(Of (Text As String, Kern As Double)) : i += 1 : Continue While
                If ch = "]"c Then lastArray = arrayParts : arrayParts = Nothing : i += 1 : Continue While
                If Char.IsWhiteSpace(ch) Then i += 1 : Continue While
                ' Number or operator token.
                Dim st = i
                While i < c.Length AndAlso Not Char.IsWhiteSpace(c(i)) AndAlso "()<>[]/%".IndexOf(c(i)) < 0
                    i += 1
                End While
                If i = st Then i += 1 : Continue While
                Dim tok = c.Substring(st, i - st)
                Dim num As Double
                If Double.TryParse(tok, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, num) Then
                    If arrayParts IsNot Nothing Then arrayParts.Add((Nothing, num)) Else operands.Add(tok)
                    Continue While
                End If
                Select Case tok
                    Case "Tj" : If operands.Count > 0 Then sb.Append(operands.Last())
                    Case "'", """" : sb.Append(vbLf) : If operands.Count > 0 Then sb.Append(operands.Last())
                    Case "TJ"
                        If lastArray IsNot Nothing Then
                            For Each p In lastArray
                                If p.Text IsNot Nothing Then
                                    sb.Append(p.Text)
                                ElseIf p.Kern < -180 Then
                                    sb.Append(" "c)
                                End If
                            Next
                        End If
                    Case "T*" : sb.Append(vbLf)
                    Case "Td", "TD"
                        Dim ty As Double
                        If operands.Count >= 1 AndAlso Double.TryParse(operands.Last(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, ty) AndAlso Math.Abs(ty) > 0.01 Then
                            sb.Append(vbLf)
                        ElseIf sb.Length > 0 AndAlso Not Char.IsWhiteSpace(sb(sb.Length - 1)) Then
                            sb.Append(" "c)
                        End If
                    Case "Tm" : If sb.Length > 0 AndAlso sb(sb.Length - 1) <> vbLf(0) Then sb.Append(vbLf)
                    Case "ET" : If sb.Length > 0 AndAlso sb(sb.Length - 1) <> vbLf(0) Then sb.Append(vbLf)
                End Select
                operands.Clear()
                lastArray = Nothing
            End While
        End Sub

        Private Function DecodePdfString(s As String) As String
            ' UTF-16BE with BOM, otherwise the bytes are (approximately) PDFDocEncoding/WinAnsi.
            If s.Length >= 2 AndAlso AscW(s(0)) = &HFE AndAlso AscW(s(1)) = &HFF Then
                Dim bytes = s.Skip(2).Select(Function(c) CByte(AscW(c) And 255)).ToArray()
                Return Encoding.BigEndianUnicode.GetString(bytes)
            End If
            Return s
        End Function

        Private Function DecodeHexString(hex As String) As String
            Dim h = Regex.Replace(hex, "\s", "")
            If h.Length Mod 2 = 1 Then h &= "0"
            Dim bytes As New List(Of Byte)
            For k = 0 To h.Length - 2 Step 2
                Dim b As Byte
                If Byte.TryParse(h.Substring(k, 2), Globalization.NumberStyles.HexNumber, Nothing, b) Then bytes.Add(b)
            Next
            Dim arr = bytes.ToArray()
            If arr.Length >= 2 AndAlso arr(0) = &HFE AndAlso arr(1) = &HFF Then Return Encoding.BigEndianUnicode.GetString(arr, 2, arr.Length - 2)
            ' Two-byte glyph codes that happen to be Unicode (common with Identity-H fonts made from Unicode text).
            If arr.Length >= 2 AndAlso arr.Length Mod 2 = 0 AndAlso Enumerable.Range(0, arr.Length \ 2).All(Function(k) arr(k * 2) = 0) Then
                Return Encoding.BigEndianUnicode.GetString(arr)
            End If
            Return Encoding.Latin1.GetString(arr)
        End Function

        ' ------------------------------------------------------------------ RTF, legacy Office

        Private Function Rtf(data As Byte()) As FileExtraction
            Dim s = Encoding.Latin1.GetString(data)
            s = Regex.Replace(s, "\{\\\*[^{}]*(\{[^{}]*\}[^{}]*)*\}", "")
            s = Regex.Replace(s, "\{\\(fonttbl|colortbl|stylesheet|info)[\s\S]*?\}\s*\}", "")
            s = Regex.Replace(s, "\\'([0-9a-fA-F]{2})", Function(m) ChrW(Convert.ToInt32(m.Groups(1).Value, 16)).ToString())
            s = Regex.Replace(s, "\\u(-?\d+)\??", Function(m) ChrW(CInt(m.Groups(1).Value) And &HFFFF).ToString())
            s = Regex.Replace(s, "\\(par|line)\b ?", vbLf)
            s = Regex.Replace(s, "\\tab\b ?", vbTab)
            s = Regex.Replace(s, "\\[a-zA-Z]+-?\d* ?", "")
            s = s.Replace("{", "").Replace("}", "").Replace("\\", "\")
            s = Regex.Replace(s, "\n{3,}", vbLf & vbLf).Trim()
            Return New FileExtraction With {.Kind = "document", .Text = s, .Summary = $"RTF document, about {Regex.Matches(s, "\S+").Count:N0} words"}
        End Function

        ''' <summary>Old binary Office formats: the readable runs of UTF-16 and ASCII text (approximate).</summary>
        Private Function LegacyOffice(data As Byte(), ext As String) As FileExtraction
            Dim runs As New List(Of String)
            Dim seen As New HashSet(Of String)
            Dim utf16 = Encoding.Unicode.GetString(data, 0, data.Length - (data.Length Mod 2))
            For Each m As Match In Regex.Matches(utf16, "[\p{L}\p{N}\p{P}\p{Zs}]{4,}")
                Dim v = m.Value.Trim()
                If v.Length >= 4 AndAlso v.Count(Function(c) Char.IsLetter(c)) >= 3 AndAlso seen.Add(v) Then runs.Add(v)
            Next
            Dim ascii = Encoding.Latin1.GetString(data)
            For Each m As Match In Regex.Matches(ascii, "[\x20-\x7E]{6,}")
                Dim v = m.Value.Trim()
                If v.Count(Function(c) Char.IsLetter(c)) >= v.Length \ 2 AndAlso seen.Add(v) Then runs.Add(v)
            Next
            Dim label = If(ext = ".xls", "Excel 97-2003 workbook", If(ext = ".ppt", "PowerPoint 97-2003 presentation", If(ext = ".msg", "Outlook message", "Word 97-2003 document")))
            Dim kind = If(ext = ".xls", "spreadsheet", If(ext = ".ppt", "presentation", "document"))
            Return New FileExtraction With {.Kind = kind, .Text = String.Join(vbLf, runs),
                .Summary = $"{label} (old binary format; text recovered approximately, no layout)"}
        End Function

        ' ------------------------------------------------------------------ archives

        Private Function ZipArchiveText(data As Byte(), maxChars As Integer, depth As Integer) As FileExtraction
            Using zip As New ZipArchive(New MemoryStream(data), ZipArchiveMode.Read)
                Dim files = zip.Entries.Where(Function(e) Not e.FullName.EndsWith("/")).ToList()
                Dim total = files.Sum(Function(e) e.Length)
                Dim sb As New StringBuilder()
                sb.Append("Files in the archive:").Append(vbLf)
                For Each e In files.Take(400)
                    sb.Append($"- {e.FullName} ({SizeText(e.Length)})").Append(vbLf)
                Next
                If files.Count > 400 Then sb.Append($"… and {files.Count - 400} more").Append(vbLf)
                Dim budget = 30L * 1024 * 1024
                Dim textFiles = 0
                If depth = 0 Then
                    For Each e In files.OrderBy(Function(x) If(IsTextFile(x.Name), 0, 1)).ThenBy(Function(x) x.FullName)
                        If sb.Length > maxChars Then Exit For
                        Dim ext = Path.GetExtension(e.Name).ToLowerInvariant()
                        Dim readable = IsTextFile(e.Name) OrElse {".docx", ".xlsx", ".pptx", ".pdf", ".rtf", ".odt", ".ods", ".odp"}.Contains(ext)
                        If Not readable OrElse e.Length > 5 * 1024 * 1024 OrElse e.Length > budget Then Continue For
                        ' Refuse implausible compression ratios (zip bombs).
                        If e.CompressedLength > 0 AndAlso e.Length \ Math.Max(1, e.CompressedLength) > 200 Then Continue For
                        budget -= e.Length
                        Dim bytes As Byte()
                        Using s = e.Open(), ms As New MemoryStream()
                            s.CopyTo(ms)
                            bytes = ms.ToArray()
                        End Using
                        Dim inner = ExtractCore(e.Name, bytes, Math.Min(12000, maxChars), depth + 1)
                        If String.IsNullOrWhiteSpace(inner.Text) Then Continue For
                        textFiles += 1
                        sb.Append(vbLf).Append($"===== {e.FullName} ({inner.Summary}) =====").Append(vbLf).Append(inner.Text).Append(vbLf)
                    Next
                End If
                Dim code = files.Where(Function(e) CodeExtensions.Contains(Path.GetExtension(e.Name))).Count()
                Return New FileExtraction With {.Kind = "archive", .Text = sb.ToString().TrimEnd(),
                    .Summary = $"ZIP archive: {files.Count:N0} file{If(files.Count = 1, "", "s")}{If(code > 0, $" ({code} source files)", "")}, {SizeText(total)} uncompressed" &
                               If(textFiles > 0, $"; text of {textFiles} file{If(textFiles = 1, "", "s")} included", "")}
            End Using
        End Function

        ' ------------------------------------------------------------------ images

        Private Function Image(data As Byte(), ext As String) As FileExtraction
            Dim dims = ImageSize(data)
            Dim fmt = ext.TrimStart("."c).ToUpperInvariant().Replace("JPEG", "JPG")
            Dim extra = ""
            If ext = ".ico" AndAlso data.Length >= 6 Then
                Dim count = BitConverter.ToUInt16(data, 4)
                extra = $", {count} size{If(count = 1, "", "s")}"
            End If
            Return New FileExtraction With {.Kind = "image",
                .Summary = $"{fmt} image" & If(dims.HasValue, $", {dims.Value.Width}×{dims.Value.Height} px", "") & extra & $", {SizeText(data.LongLength)}"}
        End Function

        Public Function ImageSize(d As Byte()) As (Width As Integer, Height As Integer)?
            Try
                If d.Length > 24 AndAlso d(0) = &H89 AndAlso d(1) = &H50 Then Return (BE32(d, 16), BE32(d, 20))
                If d.Length > 10 AndAlso d(0) = AscW("G"c) AndAlso d(1) = AscW("I"c) Then Return (BitConverter.ToUInt16(d, 6), BitConverter.ToUInt16(d, 8))
                If d.Length > 26 AndAlso d(0) = AscW("B"c) AndAlso d(1) = AscW("M"c) Then Return (BitConverter.ToInt32(d, 18), Math.Abs(BitConverter.ToInt32(d, 22)))
                If d.Length > 4 AndAlso d(0) = &HFF AndAlso d(1) = &HD8 Then
                    Dim i = 2
                    While i + 9 < d.Length
                        If d(i) <> &HFF Then i += 1 : Continue While
                        Dim marker = d(i + 1)
                        If marker >= &HC0 AndAlso marker <= &HCF AndAlso marker <> &HC4 AndAlso marker <> &HC8 AndAlso marker <> &HCC Then
                            Return ((CInt(d(i + 7)) << 8) Or d(i + 8), (CInt(d(i + 5)) << 8) Or d(i + 6))
                        End If
                        i += 2 + ((CInt(d(i + 2)) << 8) Or d(i + 3))
                    End While
                End If
                If d.Length > 22 AndAlso d(0) = 0 AndAlso d(1) = 0 AndAlso d(2) = 1 Then
                    ' ICO: the largest entry (0 means 256).
                    Dim count = BitConverter.ToUInt16(d, 4)
                    Dim best = 0
                    For k = 0 To count - 1
                        Dim o = 6 + k * 16
                        If o + 1 >= d.Length Then Exit For
                        best = Math.Max(best, If(d(o) = 0, 256, CInt(d(o))))
                    Next
                    If best > 0 Then Return (best, best)
                End If
            Catch ex As ArgumentException
            End Try
            Return Nothing
        End Function

        Private Function BE32(d As Byte(), o As Integer) As Integer
            Return (CInt(d(o)) << 24) Or (CInt(d(o + 1)) << 16) Or (CInt(d(o + 2)) << 8) Or d(o + 3)
        End Function

        ''' <summary>The largest PNG inside an .ico (vision models do not read .ico); Nothing when the icon holds only bitmaps.</summary>
        Public Function IcoLargestPng(d As Byte()) As Byte()
            If d.Length < 6 OrElse d(0) <> 0 OrElse d(2) <> 1 Then Return Nothing
            Dim count = BitConverter.ToUInt16(d, 4)
            Dim best As Byte() = Nothing
            For k = 0 To count - 1
                Dim o = 6 + k * 16
                If o + 16 > d.Length Then Exit For
                Dim size = BitConverter.ToInt32(d, o + 8), offset = BitConverter.ToInt32(d, o + 12)
                If offset < 0 OrElse size <= 8 OrElse offset + size > d.Length Then Continue For
                If d(offset) = &H89 AndAlso d(offset + 1) = &H50 AndAlso (best Is Nothing OrElse size > best.Length) Then best = d.AsSpan(offset, size).ToArray()
            Next
            Return best
        End Function
    End Module

End Namespace
