Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Formats.Tar
Imports System.Globalization
Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "files" MCP server ("Command": "{self}", "Arguments": ["--mcp-files", "--roots", "{data}\shared;{data}\workspace"]):
    ''' archives (zip, tar, tar.gz: create, list, extract safely), file checksums, a file's real type from its bytes, image facts and
    ''' changes (resize, crop, rotate, convert) and OCR of images and scanned PDFs through Tesseract when it is installed.
    ''' </summary>
    Public NotInheritable Class FilesServer
        Private ReadOnly _roots As PathRoots
        Private ReadOnly _tesseract As String
        Private Const MaxExtractBytes As Long = 2L * 1024 * 1024 * 1024
        Private Const MaxEntries As Integer = 50000
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture

        Friend Sub New(roots As PathRoots, Optional tesseract As String = Nothing)
            _roots = roots
            _tesseract = If(String.IsNullOrWhiteSpace(tesseract), FindTesseract(), tesseract.Trim())
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim roots = ToolKit.SplitList(If(ToolKit.Arg(args, "--roots"), Path.Combine(AppContext.BaseDirectory, "data", "shared")))
            Dim server As New FilesServer(New PathRoots(roots), If(ToolKit.Arg(args, "--tesseract"), ToolKit.Env("TESSERACT_PATH")))
            Return BuiltInServer.Run("agentic-files", server.CreateTools())
        End Function

        Friend Shared Function FindTesseract() As String
            For Each c In {"C:\Program Files\Tesseract-OCR\tesseract.exe", "C:\Program Files (x86)\Tesseract-OCR\tesseract.exe", "/usr/bin/tesseract", "/usr/local/bin/tesseract"}
                If File.Exists(c) Then Return c
            Next
            Return Nothing
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim where = $" Paths are relative to {_roots.Roots(0)} (or absolute inside {_roots.Describe()})."
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf ListArchive), "archive_list", "The files in a .zip, .tar, .tar.gz/.tgz or .gz: names, sizes and totals." & where)
            ToolKit.Add(tools, New Func(Of String, String, Boolean, String)(AddressOf ExtractArchive), "archive_extract",
                "Unpack a .zip, .tar, .tar.gz/.tgz or .gz into a folder (default: next to it, named after it). Entries that would land outside the folder, links and archives over 2 GB unpacked are refused." & where, False, True)
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf CreateArchive), "archive_create",
                "Pack files and folders (comma-separated) into a .zip, .tar or .tar.gz." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, String, String)(AddressOf HashFile), "file_hash",
                "A file's checksum (sha256 by default; sha1, sha512, md5); with expected, says whether it matches." & where)
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf FileInfoText), "file_info",
                "What a file really is (from its first bytes, not its name): type, size, dates, and for text the encoding and line count; warns when the extension does not match." & where)
            ToolKit.Add(tools, New Func(Of String, String)(AddressOf ImageInfo), "image_info", "An image's format, width and height, color type, orientation and size." & where)
            ToolKit.Add(tools, New Func(Of String, String, Integer, Integer, String, String, Integer, Integer, String)(AddressOf TransformImage), "image_transform",
                "Change an image and save it (format from output_path: .png, .jpg, .webp): resize (width/height; fit contain, cover or stretch), crop 'x,y,width,height', rotate 90/180/270, quality for JPEG/WebP." & where, False)
            ToolKit.Add(tools, New Func(Of String, String, String, CancellationToken, Task(Of String))(AddressOf OcrAsync), "ocr",
                "Read the text in an image (.png, .jpg, .tif, ...) or a scanned PDF with Tesseract OCR. language: eng (default), or e.g. deu, fra, eng+spa when installed." & where)
            Return tools
        End Function

        Private Function Existing(p As String) As String
            Dim full = _roots.Resolve(p)
            If Not File.Exists(full) Then Throw New McpException($"File '{p}' does not exist.")
            Return full
        End Function

        ' ------------------------------------------------------------------------------------------ archives

        Private Shared Function KindOf(path As String) As String
            Dim n = path.ToLowerInvariant()
            If n.EndsWith(".zip") OrElse n.EndsWith(".jar") OrElse n.EndsWith(".nupkg") OrElse n.EndsWith(".vsix") Then Return "zip"
            If n.EndsWith(".tar.gz") OrElse n.EndsWith(".tgz") Then Return "tgz"
            If n.EndsWith(".tar") Then Return "tar"
            If n.EndsWith(".gz") Then Return "gz"
            Return Nothing
        End Function

        Friend Function ListArchive(<Description("Archive path")> path As String) As String
            Dim full = Existing(path)
            Dim rows As New List(Of List(Of String)) From {New List(Of String) From {"entry", "size", "packed", "modified"}}
            Dim total = 0L, count = 0
            Select Case KindOf(full)
                Case "zip"
                    Using z = OpenZip(full)
                        For Each e In z.Entries
                            count += 1 : total += e.Length
                            If rows.Count <= 500 Then rows.Add(New List(Of String) From {e.FullName, Size(e.Length), If(e.FullName.EndsWith("/"), "", Size(e.CompressedLength)), e.LastWriteTime.ToString("yyyy-MM-dd HH:mm", Inv)})
                        Next
                    End Using
                Case "tar", "tgz"
                    Using fs = File.OpenRead(full)
                        Using s As Stream = If(KindOf(full) = "tgz", New GZipStream(fs, CompressionMode.Decompress), CType(fs, Stream))
                            Using reader As New TarReader(s)
                                Dim e = reader.GetNextEntry(copyData:=False)
                                While e IsNot Nothing
                                    count += 1 : total += e.Length
                                    If rows.Count <= 500 Then rows.Add(New List(Of String) From {e.Name & If(e.EntryType = TarEntryType.Directory, "/", "") & If(IsLink(e), $" -> {e.LinkName} (link)", ""), Size(e.Length), "", e.ModificationTime.ToString("yyyy-MM-dd HH:mm", Inv)})
                                    e = reader.GetNextEntry(copyData:=False)
                                End While
                            End Using
                        End Using
                    End Using
                Case "gz"
                    Return $"{IO.Path.GetFileName(full)} is a single gzip-compressed file ({Size(New FileInfo(full).Length)} packed); archive_extract unpacks it."
                Case Else
                    Throw New McpException("Only .zip, .tar, .tar.gz/.tgz and .gz archives are supported.")
            End Select
            Return $"{count:N0} entr{If(count = 1, "y", "ies")}, {Size(total)} unpacked ({Size(New FileInfo(full).Length)} archive){If(count > 500, ", first 500 shown", "")}:" & vbLf & TableText.ToMarkdown(rows, 500)
        End Function

        Private Shared Function OpenZip(full As String) As ZipArchive
            Try
                Return ZipFile.OpenRead(full)
            Catch ex As InvalidDataException
                Throw New McpException("This is not a valid zip file: " & ex.Message)
            End Try
        End Function

        Private Shared Function IsLink(e As TarEntry) As Boolean
            Return e.EntryType = TarEntryType.SymbolicLink OrElse e.EntryType = TarEntryType.HardLink
        End Function

        ''' <summary>The full path for an archive entry, or Nothing when it would leave the destination (zip slip).</summary>
        Friend Shared Function SafeTarget(destination As String, entryName As String) As String
            Dim name = If(entryName, "").Replace("\"c, "/"c).TrimStart("/"c)
            If name.Length = 0 OrElse name.Split("/"c).Any(Function(p) p = "..") OrElse name.Contains(":"c) Then Return Nothing
            Dim full = IO.Path.GetFullPath(IO.Path.Combine(destination, name.Replace("/"c, IO.Path.DirectorySeparatorChar)))
            Dim root = destination.TrimEnd(IO.Path.DirectorySeparatorChar) & IO.Path.DirectorySeparatorChar
            Return If(full.StartsWith(root, StringComparison.OrdinalIgnoreCase), full, Nothing)
        End Function

        Friend Function ExtractArchive(<Description("Archive path")> path As String,
                                       <Description("Folder to unpack into (optional)")> Optional destination As String = "",
                                       <Description("Replace files that already exist")> Optional overwrite As Boolean = False) As String
            Dim full = Existing(path)
            Dim kind = KindOf(full)
            If kind Is Nothing Then Throw New McpException("Only .zip, .tar, .tar.gz/.tgz and .gz archives are supported.")
            Dim baseName = IO.Path.GetFileName(full)
            For Each ext In {".tar.gz", ".tgz", ".zip", ".tar", ".gz"}
                If baseName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) Then baseName = baseName.Substring(0, baseName.Length - ext.Length) : Exit For
            Next
            Dim dest = If(String.IsNullOrWhiteSpace(destination), IO.Path.Combine(IO.Path.GetDirectoryName(full), baseName), _roots.Resolve(destination))
            _roots.Resolve(dest)
            Directory.CreateDirectory(dest)
            Dim files = 0, skipped As New List(Of String), total = 0L
            Dim write = Sub(target As String, data As Stream, length As Long)
                            If File.Exists(target) AndAlso Not overwrite Then skipped.Add(_roots.Show(target) & " (exists)") : Return
                            total += length
                            If total > MaxExtractBytes Then Throw New McpException("The archive unpacks to more than 2 GB; stopped.")
                            Directory.CreateDirectory(IO.Path.GetDirectoryName(target))
                            Using outFile = File.Create(target)
                                data.CopyTo(outFile)
                            End Using
                            files += 1
                        End Sub
            Select Case kind
                Case "zip"
                    Using z = OpenZip(full)
                        If z.Entries.Count > MaxEntries Then Throw New McpException($"The archive has more than {MaxEntries:N0} entries.")
                        If z.Entries.Sum(Function(e) e.Length) > MaxExtractBytes Then Throw New McpException("The archive unpacks to more than 2 GB.")
                        For Each e In z.Entries
                            Dim target = SafeTarget(dest, e.FullName)
                            If target Is Nothing Then skipped.Add(e.FullName & " (outside the folder)") : Continue For
                            If e.FullName.EndsWith("/") Then Directory.CreateDirectory(target) : Continue For
                            Using s = e.Open()
                                write(target, s, e.Length)
                            End Using
                        Next
                    End Using
                Case "gz"
                    Dim target = IO.Path.Combine(IO.Path.GetDirectoryName(full), baseName)
                    _roots.Resolve(target)
                    Using fs = File.OpenRead(full)
                        Using gz As New GZipStream(fs, CompressionMode.Decompress)
                            If File.Exists(target) AndAlso Not overwrite Then Throw New McpException($"{_roots.Show(target)} already exists (overwrite: true to replace it).")
                            Using outFile = File.Create(target)
                                Dim buf(81919) As Byte
                                Dim n = gz.Read(buf, 0, buf.Length)
                                While n > 0
                                    total += n
                                    If total > MaxExtractBytes Then Throw New McpException("The file unpacks to more than 2 GB; stopped.")
                                    outFile.Write(buf, 0, n)
                                    n = gz.Read(buf, 0, buf.Length)
                                End While
                            End Using
                        End Using
                    End Using
                    Return $"Unpacked to {_roots.Show(target)} ({Size(total)})."
                Case Else
                    Using fs = File.OpenRead(full)
                        Using s As Stream = If(kind = "tgz", New GZipStream(fs, CompressionMode.Decompress), CType(fs, Stream))
                            Using reader As New TarReader(s)
                                Dim e = reader.GetNextEntry()
                                Dim n = 0
                                While e IsNot Nothing
                                    n += 1
                                    If n > MaxEntries Then Throw New McpException($"The archive has more than {MaxEntries:N0} entries.")
                                    Dim target = SafeTarget(dest, e.Name)
                                    If target Is Nothing Then
                                        skipped.Add(e.Name & " (outside the folder)")
                                    ElseIf IsLink(e) Then
                                        skipped.Add(e.Name & " (link)")
                                    ElseIf e.EntryType = TarEntryType.Directory Then
                                        Directory.CreateDirectory(target)
                                    ElseIf e.DataStream IsNot Nothing Then
                                        write(target, e.DataStream, e.Length)
                                    End If
                                    e = reader.GetNextEntry()
                                End While
                            End Using
                        End Using
                    End Using
            End Select
            Return $"Unpacked {files:N0} file(s), {Size(total)}, into {_roots.Show(dest)}/." &
                   If(skipped.Count > 0, $"{vbLf}Skipped {skipped.Count}: {String.Join("; ", skipped.Take(20))}{If(skipped.Count > 20, "; ...", "")}", "")
        End Function

        Friend Function CreateArchive(<Description("Files and folders to pack, comma-separated")> paths As String,
                                      <Description("The archive to write: .zip, .tar or .tar.gz")> output_path As String) As String
            Dim items = ToolKit.SplitList(paths).Select(Function(p) _roots.Resolve(p)).ToList()
            If items.Count = 0 Then Throw New McpException("paths is required.")
            Dim outFull = _roots.Resolve(output_path)
            Dim kind = KindOf(outFull)
            If kind Is Nothing OrElse kind = "gz" Then Throw New McpException("output_path must end in .zip, .tar or .tar.gz.")
            Dim entries As New List(Of (Full As String, Name As String))
            For Each it In items
                If File.Exists(it) Then
                    entries.Add((it, IO.Path.GetFileName(it)))
                ElseIf Directory.Exists(it) Then
                    Dim parent = IO.Path.GetDirectoryName(it.TrimEnd(IO.Path.DirectorySeparatorChar))
                    For Each f In Directory.EnumerateFiles(it, "*", SearchOption.AllDirectories)
                        If String.Equals(IO.Path.GetFullPath(f), outFull, StringComparison.OrdinalIgnoreCase) Then Continue For
                        entries.Add((f, IO.Path.GetRelativePath(parent, f).Replace("\"c, "/"c)))
                    Next
                Else
                    Throw New McpException($"'{_roots.Show(it)}' does not exist.")
                End If
            Next
            If entries.Count = 0 Then Throw New McpException("Nothing to pack (the folders are empty).")
            Directory.CreateDirectory(IO.Path.GetDirectoryName(outFull))
            Dim tmp = outFull & ".tmp-" & Guid.NewGuid().ToString("N").Substring(0, 8)
            Try
                Using fs = File.Create(tmp)
                    If kind = "zip" Then
                        Using z As New ZipArchive(fs, ZipArchiveMode.Create)
                            For Each e In entries
                                z.CreateEntryFromFile(e.Full, e.Name, CompressionLevel.Optimal)
                            Next
                        End Using
                    Else
                        Using s As Stream = If(kind = "tgz", New GZipStream(fs, CompressionLevel.Optimal), CType(fs, Stream))
                            Using w As New TarWriter(s, TarEntryFormat.Pax, leaveOpen:=False)
                                For Each e In entries
                                    w.WriteEntry(e.Full, e.Name)
                                Next
                            End Using
                        End Using
                    End If
                End Using
                File.Move(tmp, outFull, overwrite:=True)
            Finally
                If File.Exists(tmp) Then File.Delete(tmp)
            End Try
            Return $"Packed {entries.Count:N0} file(s), {Size(entries.Sum(Function(e) New FileInfo(e.Full).Length))}, into {_roots.Show(outFull)} ({Size(New FileInfo(outFull).Length)})."
        End Function

        ' ------------------------------------------------------------------------------------------ checksums, file types

        Friend Shared Function Size(bytes As Long) As String
            Return DockerServer.Size(bytes)
        End Function

        Friend Function HashFile(<Description("File path")> path As String,
                                 <Description("sha256 (default), sha1, sha512 or md5")> Optional algorithm As String = "sha256",
                                 <Description("Expected checksum to compare with (optional)")> Optional expected As String = "") As String
            Dim full = Existing(path)
            Dim alg = If(String.IsNullOrWhiteSpace(algorithm), "sha256", algorithm.Trim().ToLowerInvariant().Replace("-", ""))
            Dim hash As Byte()
            Using fs = File.OpenRead(full)
                Select Case alg
                    Case "sha256" : hash = SHA256.HashData(fs)
                    Case "sha1" : hash = SHA1.HashData(fs)
                    Case "sha512" : hash = SHA512.HashData(fs)
                    Case "md5" : hash = MD5.HashData(fs)
                    Case Else : Throw New McpException("algorithm must be sha256, sha1, sha512 or md5.")
                End Select
            End Using
            Dim hex = Convert.ToHexString(hash).ToLowerInvariant()
            Dim text = $"{alg.ToUpperInvariant()} of {_roots.Show(full)} ({Size(New FileInfo(full).Length)}): {hex}"
            If Not String.IsNullOrWhiteSpace(expected) Then
                Dim want = expected.Trim().ToLowerInvariant().Replace(" ", "")
                text &= If(want = hex OrElse want = Convert.ToBase64String(hash).ToLowerInvariant(), vbLf & "MATCHES the expected checksum.", vbLf & "DOES NOT MATCH the expected checksum " & expected.Trim() & ".")
            End If
            Return text
        End Function

        ''' <summary>Whether the bytes start with the signature (-1 = any byte).</summary>
        Private Shared Function HeadIs(head As Byte(), ParamArray sig As Integer()) As Boolean
            If head.Length < sig.Length Then Return False
            For i = 0 To sig.Length - 1
                If sig(i) >= 0 AndAlso head(i) <> sig(i) Then Return False
            Next
            Return True
        End Function

        ''' <summary>The type from a file's first bytes: (description, usual extensions).</summary>
        Friend Shared Function Sniff(head As Byte(), fullPath As String) As (Kind As String, Exts As String())
            Dim ascii = Encoding.ASCII.GetString(head, 0, Math.Min(head.Length, 512))
            Select Case True
                Case HeadIs(head, &H25, &H50, &H44, &H46) : Return ("PDF document", {".pdf"})
                Case HeadIs(head, &H89, &H50, &H4E, &H47) : Return ("PNG image", {".png"})
                Case HeadIs(head, &HFF, &HD8, &HFF) : Return ("JPEG image", {".jpg", ".jpeg"})
                Case HeadIs(head, &H47, &H49, &H46, &H38) : Return ("GIF image", {".gif"})
                Case HeadIs(head, &H52, &H49, &H46, &H46, -1, -1, -1, -1, &H57, &H45, &H42, &H50) : Return ("WebP image", {".webp"})
                Case HeadIs(head, &H52, &H49, &H46, &H46, -1, -1, -1, -1, &H57, &H41, &H56, &H45) : Return ("WAV audio", {".wav"})
                Case HeadIs(head, &H42, &H4D) AndAlso head.Length > 14 : Return ("BMP image", {".bmp"})
                Case HeadIs(head, &H49, &H49, &H2A, 0) OrElse HeadIs(head, &H4D, &H4D, 0, &H2A) : Return ("TIFF image", {".tif", ".tiff"})
                Case HeadIs(head, 0, 0, 1, 0) : Return ("ICO icon", {".ico"})
                Case HeadIs(head, &H50, &H4B, 3, 4) OrElse HeadIs(head, &H50, &H4B, 5, 6)
                    Return ZipKind(fullPath)
                Case HeadIs(head, &H1F, &H8B) : Return ("gzip compressed data", {".gz", ".tgz"})
                Case HeadIs(head, &H37, &H7A, &HBC, &HAF, &H27, &H1C) : Return ("7-Zip archive", {".7z"})
                Case HeadIs(head, &H52, &H61, &H72, &H21, &H1A, &H7) : Return ("RAR archive", {".rar"})
                Case head.Length > 262 AndAlso Encoding.ASCII.GetString(head, 257, 5) = "ustar" : Return ("tar archive", {".tar"})
                Case HeadIs(head, &H4D, &H5A) : Return ("Windows executable or DLL (PE)", {".exe", ".dll", ".sys"})
                Case HeadIs(head, &H7F, &H45, &H4C, &H46) : Return ("Linux executable (ELF)", {"", ".so", ".bin"})
                Case ascii.StartsWith("SQLite format 3") : Return ("SQLite database", {".db", ".sqlite", ".sqlite3"})
                Case HeadIs(head, &H49, &H44, &H33) OrElse HeadIs(head, &HFF, &HFB) : Return ("MP3 audio", {".mp3"})
                Case HeadIs(head, -1, -1, -1, -1, &H66, &H74, &H79, &H70) : Return ("MP4/MOV video", {".mp4", ".m4a", ".mov"})
                Case HeadIs(head, &H4F, &H67, &H67, &H53) : Return ("Ogg media", {".ogg", ".opus"})
                Case HeadIs(head, &H1A, &H45, &HDF, &HA3) : Return ("Matroska/WebM video", {".mkv", ".webm"})
                Case HeadIs(head, &HD0, &HCF, &H11, &HE0) : Return ("old Office document (OLE: .doc/.xls/.ppt/.msi)", {".doc", ".xls", ".ppt", ".msi"})
                Case HeadIs(head, &HCA, &HFE, &HBA, &HBE) : Return ("Java class or Mach-O fat binary", {".class"})
                Case HeadIs(head, &H0, &H61, &H73, &H6D) : Return ("WebAssembly module", {".wasm"})
            End Select
            If head.Take(8000).Any(Function(b) b = 0) Then Return ("binary data", Array.Empty(Of String)())
            Dim t = ascii.TrimStart(ChrW(&HFEFF)).TrimStart()
            If t.StartsWith("<?xml") Then Return ("XML text", {".xml", ".svg", ".csproj", ".config"})
            If t.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) OrElse t.StartsWith("<html", StringComparison.OrdinalIgnoreCase) Then Return ("HTML text", {".html", ".htm"})
            If t.StartsWith("<svg") Then Return ("SVG image (XML)", {".svg"})
            If t.StartsWith("{") OrElse t.StartsWith("[") Then Return ("JSON text (probably)", {".json"})
            If t.StartsWith("#!") Then Return ("script with a #! line", {".sh", ".py", ""})
            If t.StartsWith("%!PS") Then Return ("PostScript", {".ps", ".eps"})
            If t.StartsWith("From ") OrElse t.StartsWith("Received:") OrElse t.StartsWith("Return-Path:") Then Return ("email message", {".eml"})
            Return ("text", {".txt", ".md", ".csv", ".log", ".yaml", ".yml", ".toml", ".ini", ".cs", ".vb", ".js", ".ts", ".py", ".sql", ".ps1"})
        End Function

        Private Shared Function ZipKind(fullPath As String) As (Kind As String, Exts As String())
            Try
                Using z = ZipFile.OpenRead(fullPath)
                    Dim names = z.Entries.Select(Function(e) e.FullName).ToList()
                    If names.Contains("word/document.xml") Then Return ("Word document (.docx)", {".docx", ".docm"})
                    If names.Contains("xl/workbook.xml") Then Return ("Excel workbook (.xlsx)", {".xlsx", ".xlsm"})
                    If names.Contains("ppt/presentation.xml") Then Return ("PowerPoint presentation (.pptx)", {".pptx", ".pptm"})
                    If names.Contains("META-INF/MANIFEST.MF") Then Return ("Java archive (.jar)", {".jar", ".war"})
                    If names.Contains("AndroidManifest.xml") Then Return ("Android package (.apk)", {".apk"})
                    If names.Any(Function(n) n.EndsWith(".nuspec")) Then Return ("NuGet package", {".nupkg"})
                    If names.Contains("extension.vsixmanifest") Then Return ("VS Code / Visual Studio extension (.vsix)", {".vsix"})
                    If names.Contains("mimetype") Then Return ("OpenDocument or EPUB (zip with mimetype)", {".odt", ".ods", ".odp", ".epub"})
                End Using
            Catch ex As InvalidDataException
                Return ("zip archive (damaged)", {".zip"})
            End Try
            Return ("zip archive", {".zip"})
        End Function

        Friend Function FileInfoText(<Description("File path")> path As String) As String
            Dim full = Existing(path)
            Dim fi As New FileInfo(full)
            Dim head(Math.Min(CInt(Math.Min(fi.Length, 65536)), 65536) - 1) As Byte
            Using fs = File.OpenRead(full)
                Dim got = 0
                While got < head.Length
                    Dim n = fs.Read(head, got, head.Length - got)
                    If n = 0 Then Exit While
                    got += n
                End While
            End Using
            Dim kind = Sniff(head, full)
            Dim ext = fi.Extension.ToLowerInvariant()
            Dim sb As New StringBuilder()
            sb.AppendLine($"{_roots.Show(full)}: {kind.Kind}, {Size(fi.Length)} ({fi.Length:N0} bytes)")
            sb.AppendLine($"Created {fi.CreationTimeUtc:yyyy-MM-dd HH:mm} UTC, modified {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC{If(fi.IsReadOnly, ", read-only", "")}")
            If kind.Exts.Length > 0 AndAlso Not kind.Exts.Contains(ext) AndAlso kind.Kind <> "text" Then sb.AppendLine($"WARNING: the extension '{ext}' does not match the content ({kind.Kind}: {String.Join(", ", kind.Exts.Where(Function(e) e <> ""))}).")
            If kind.Kind = "text" OrElse kind.Kind.Contains("text") OrElse kind.Kind.Contains("JSON") Then
                Dim bom = If(head.Length >= 3 AndAlso head(0) = &HEF AndAlso head(1) = &HBB AndAlso head(2) = &HBF, "UTF-8 with BOM", If(head.Length >= 2 AndAlso head(0) = &HFF AndAlso head(1) = &HFE, "UTF-16 LE", Nothing))
                Dim enc = If(bom, If(IsUtf8(head), "UTF-8 (or ASCII)", "not UTF-8 (probably Windows-1252/Latin-1)"))
                Dim lines = 0L
                Dim crlf = False
                Using sr As New StreamReader(full, detectEncodingFromByteOrderMarks:=True)
                    Dim text = sr.ReadToEnd()
                    lines = text.Count(Function(c) c = ControlChars.Lf) + If(text.Length > 0 AndAlso Not text.EndsWith(vbLf), 1, 0)
                    crlf = text.Contains(vbCrLf)
                End Using
                sb.AppendLine($"Encoding: {enc}; {lines:N0} lines; line endings: {If(crlf, "CRLF (Windows)", "LF (Unix)")}")
            End If
            Return sb.ToString().TrimEnd()
        End Function

        Private Shared Function IsUtf8(bytes As Byte()) As Boolean
            Try
                Dim strict As New UTF8Encoding(False, True)
                ' The sample may end inside a character: ignore the last 3 bytes.
                strict.GetString(bytes, 0, Math.Max(0, bytes.Length - 3))
                Return True
            Catch ex As DecoderFallbackException
                Return False
            End Try
        End Function

        ' ------------------------------------------------------------------------------------------ images

        Friend Function ImageInfo(<Description("Image path")> path As String) As String
            Dim full = Existing(path)
            Using codec = SkiaSharp.SKCodec.Create(full)
                If codec Is Nothing Then Throw New McpException($"'{path}' is not an image this tool can read (PNG, JPEG, GIF, WebP, BMP, ICO, WBMP, HEIF where supported).")
                Dim info = codec.Info
                Dim sb As New StringBuilder($"{_roots.Show(full)}: {codec.EncodedFormat} {info.Width} x {info.Height} px, {Size(New FileInfo(full).Length)}")
                sb.Append($"; color {info.ColorType}, alpha {info.AlphaType}")
                If codec.EncodedOrigin <> SkiaSharp.SKEncodedOrigin.TopLeft Then sb.Append($"; orientation {codec.EncodedOrigin} (shown rotated)")
                If codec.FrameCount > 1 Then sb.Append($"; {codec.FrameCount} frames (animated)")
                Dim mp = info.Width * CLng(info.Height) / 1000000.0
                sb.Append($"; {mp.ToString("0.##", Inv)} megapixels")
                Return sb.ToString()
            End Using
        End Function

        Friend Function TransformImage(<Description("Image path")> path As String,
                                       <Description("Output path: .png, .jpg or .webp")> output_path As String,
                                       <Description("New width in pixels (optional)")> Optional width As Integer = 0,
                                       <Description("New height in pixels (optional)")> Optional height As Integer = 0,
                                       <Description("contain (default: fit inside), cover (fill and crop) or stretch")> Optional fit As String = "contain",
                                       <Description("Crop first: 'x,y,width,height' in pixels (optional)")> Optional crop As String = "",
                                       <Description("Rotate 90, 180 or 270 degrees clockwise (optional)")> Optional rotate As Integer = 0,
                                       <Description("JPEG/WebP quality 1-100 (default 88)")> Optional quality As Integer = 88) As String
            Dim full = Existing(path)
            Dim outFull = _roots.Resolve(output_path)
            Dim format As SkiaSharp.SKEncodedImageFormat
            Select Case IO.Path.GetExtension(outFull).ToLowerInvariant()
                Case ".png" : format = SkiaSharp.SKEncodedImageFormat.Png
                Case ".jpg", ".jpeg" : format = SkiaSharp.SKEncodedImageFormat.Jpeg
                Case ".webp" : format = SkiaSharp.SKEncodedImageFormat.Webp
                Case Else : Throw New McpException("output_path must end in .png, .jpg or .webp.")
            End Select
            Dim bmp = SkiaSharp.SKBitmap.Decode(full)
            If bmp Is Nothing Then Throw New McpException($"'{path}' is not an image this tool can read.")
            Dim steps As New List(Of String)
            Try
                If Not String.IsNullOrWhiteSpace(crop) Then
                    Dim p = crop.Split(","c).Select(Function(s) Integer.Parse(s.Trim(), Inv)).ToArray()
                    If p.Length <> 4 OrElse p(2) <= 0 OrElse p(3) <= 0 OrElse p(0) < 0 OrElse p(1) < 0 OrElse p(0) + p(2) > bmp.Width OrElse p(1) + p(3) > bmp.Height Then
                        Throw New McpException($"crop must be 'x,y,width,height' inside the {bmp.Width} x {bmp.Height} image.")
                    End If
                    Dim cropped As New SkiaSharp.SKBitmap(p(2), p(3))
                    If Not bmp.ExtractSubset(cropped, New SkiaSharp.SKRectI(p(0), p(1), p(0) + p(2), p(1) + p(3))) Then Throw New McpException("The crop failed.")
                    Dim copy = cropped.Copy()
                    cropped.Dispose()
                    bmp.Dispose() : bmp = copy
                    steps.Add($"cropped to {p(2)} x {p(3)}")
                End If
                If rotate <> 0 Then
                    Dim r = ((rotate Mod 360) + 360) Mod 360
                    If r <> 90 AndAlso r <> 180 AndAlso r <> 270 Then Throw New McpException("rotate must be 90, 180 or 270.")
                    Dim rw = If(r = 180, bmp.Width, bmp.Height), rh = If(r = 180, bmp.Height, bmp.Width)
                    Dim rotated As New SkiaSharp.SKBitmap(rw, rh)
                    Using c As New SkiaSharp.SKCanvas(rotated)
                        c.Translate(rw / 2.0F, rh / 2.0F)
                        c.RotateDegrees(r)
                        c.Translate(-bmp.Width / 2.0F, -bmp.Height / 2.0F)
                        c.DrawBitmap(bmp, 0, 0)
                    End Using
                    bmp.Dispose() : bmp = rotated
                    steps.Add($"rotated {r} degrees")
                End If
                If width > 0 OrElse height > 0 Then
                    Dim mode = If(fit, "contain").Trim().ToLowerInvariant()
                    Dim w = If(width > 0, width, CInt(Math.Round(bmp.Width * height / CDbl(bmp.Height))))
                    Dim h = If(height > 0, height, CInt(Math.Round(bmp.Height * width / CDbl(bmp.Width))))
                    If w > 16000 OrElse h > 16000 Then Throw New McpException("At most 16000 pixels on a side.")
                    Dim srcRect = New SkiaSharp.SKRect(0, 0, bmp.Width, bmp.Height)
                    If width > 0 AndAlso height > 0 AndAlso mode = "contain" Then
                        Dim s = Math.Min(width / CDbl(bmp.Width), height / CDbl(bmp.Height))
                        w = Math.Max(1, CInt(Math.Round(bmp.Width * s))) : h = Math.Max(1, CInt(Math.Round(bmp.Height * s)))
                    ElseIf width > 0 AndAlso height > 0 AndAlso mode = "cover" Then
                        Dim s = Math.Max(width / CDbl(bmp.Width), height / CDbl(bmp.Height))
                        Dim cw = width / s, ch = height / s
                        srcRect = New SkiaSharp.SKRect(CSng((bmp.Width - cw) / 2), CSng((bmp.Height - ch) / 2), CSng((bmp.Width + cw) / 2), CSng((bmp.Height + ch) / 2))
                    End If
                    Dim resized As New SkiaSharp.SKBitmap(Math.Max(1, w), Math.Max(1, h))
                    Using c As New SkiaSharp.SKCanvas(resized)
                        Using paint As New SkiaSharp.SKPaint With {.IsAntialias = True}
                            Using img = SkiaSharp.SKImage.FromBitmap(bmp)
                                c.DrawImage(img, srcRect, New SkiaSharp.SKRect(0, 0, resized.Width, resized.Height), New SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell), paint)
                            End Using
                        End Using
                    End Using
                    bmp.Dispose() : bmp = resized
                    steps.Add($"resized to {resized.Width} x {resized.Height} ({mode})")
                End If
                Directory.CreateDirectory(IO.Path.GetDirectoryName(outFull))
                Using img = SkiaSharp.SKImage.FromBitmap(bmp)
                    Using data = img.Encode(format, Math.Clamp(If(quality <= 0, 88, quality), 1, 100))
                        If data Is Nothing Then Throw New McpException($"This server cannot write {format} images.")
                        File.WriteAllBytes(outFull, data.ToArray())
                    End Using
                End Using
                Return $"Saved {_roots.Show(outFull)}: {bmp.Width} x {bmp.Height} {format}, {Size(New FileInfo(outFull).Length)}{If(steps.Count > 0, " (" & String.Join(", ", steps) & ")", "")}."
            Finally
                bmp?.Dispose()
            End Try
        End Function

        ' ------------------------------------------------------------------------------------------ OCR

        Friend Async Function OcrAsync(<Description("Image or PDF path")> path As String,
                                       <Description("Tesseract language(s), e.g. eng (default), deu, eng+fra")> Optional language As String = "eng",
                                       <Description("PDF pages, e.g. '1-3' (optional)")> Optional pages As String = "",
                                       Optional ct As CancellationToken = Nothing) As Task(Of String)
            If String.IsNullOrWhiteSpace(_tesseract) OrElse Not File.Exists(_tesseract) Then
                Throw New McpException("OCR needs Tesseract, which is not installed on this server (an administrator installs it, e.g. to C:\Program Files\Tesseract-OCR).")
            End If
            Dim full = Existing(path)
            Dim lang = If(String.IsNullOrWhiteSpace(language), "eng", language.Trim())
            If Not Text.RegularExpressions.Regex.IsMatch(lang, "^[a-z_]{3,10}(\+[a-z_]{3,10})*$") Then Throw New McpException($"'{language}' is not a Tesseract language code (e.g. eng, deu, eng+fra).")
            Dim work = IO.Path.Combine(IO.Path.GetTempPath(), "agentic-ocr-" & Guid.NewGuid().ToString("N").Substring(0, 10))
            Directory.CreateDirectory(work)
            Try
                Dim images As New List(Of (Label As String, File As String))
                If full.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) Then
                    Using doc = UglyToad.PdfPig.PdfDocument.Open(full)
                        Dim pick = DocumentsServer.ParseRanges(pages, doc.NumberOfPages)
                        For Each page In doc.GetPages()
                            If pick IsNot Nothing AndAlso Not pick.Contains(page.Number) Then Continue For
                            If images.Count >= 100 Then Exit For
                            Dim k = 0
                            For Each img In page.GetImages()
                                k += 1
                                Dim raw = img.RawMemory.ToArray()
                                Dim file As String = Nothing
                                If raw.Length > 3 AndAlso raw(0) = &HFF AndAlso raw(1) = &HD8 Then
                                    file = IO.Path.Combine(work, $"p{page.Number}-{k}.jpg") : IO.File.WriteAllBytes(file, raw)
                                Else
                                    Dim png As Byte() = Nothing
                                    If img.TryGetPng(png) AndAlso png IsNot Nothing Then file = IO.Path.Combine(work, $"p{page.Number}-{k}.png") : IO.File.WriteAllBytes(file, png)
                                End If
                                If file IsNot Nothing Then images.Add(($"Page {page.Number}" & If(k > 1, $" image {k}", ""), file))
                            Next
                        Next
                    End Using
                    If images.Count = 0 Then Return "No page images could be taken from this PDF (it may have a text layer: use read_document, or images in a format this tool cannot decode)."
                Else
                    images.Add((IO.Path.GetFileName(full), full))
                End If
                Dim sb As New StringBuilder()
                For Each im In images
                    ct.ThrowIfCancellationRequested()
                    Dim r = Await ToolKit.RunProcessAsync(_tesseract, {im.File, "stdout", "-l", lang, "--psm", "3"}, work, TimeSpan.FromSeconds(120), ct).ConfigureAwait(False)
                    If r.TimedOut Then Throw New McpException("OCR took longer than 120 s for one image and was stopped.")
                    If r.ExitCode <> 0 Then
                        Dim err = r.Error.Trim()
                        If err.Contains("Failed loading language", StringComparison.OrdinalIgnoreCase) Then Throw New McpException($"The Tesseract language '{lang}' is not installed.")
                        Throw New McpException("Tesseract failed: " & ToolKit.Clip(err, 500))
                    End If
                    If images.Count > 1 Then sb.AppendLine($"=== {im.Label} ===")
                    sb.AppendLine(r.Output.Trim()).AppendLine()
                Next
                Dim text = sb.ToString().Trim()
                Return If(text.Length = 0, "No text was recognized.", ToolKit.Clip(text, 150000))
            Finally
                Try
                    Directory.Delete(work, True)
                Catch ex As IOException
                End Try
            End Try
        End Function
    End Class

End Namespace
