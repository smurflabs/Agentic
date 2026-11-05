Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.IO
Imports System.Text
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Protocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "shared storage" MCP server: a folder that every authorized agent can read and write, so agents can hand
    ''' files to each other. Started by the host itself as a stdio child process ("Command": "{self}",
    ''' "Arguments": ["--mcp-storage", "{data}\shared"]). All paths are confined to the root folder: absolute paths, "..",
    ''' and links that lead outside are refused. Per-file size and total quota limits apply.
    ''' </summary>
    Public NotInheritable Class SharedStorageServer
        Private ReadOnly _root As String
        Private ReadOnly _maxFileBytes As Long
        Private ReadOnly _quotaBytes As Long
        Private Const MaxReadChars As Integer = 200000
        Private Const MaxListEntries As Integer = 500

        Public Sub New(root As String, Optional maxFileMb As Integer = 20, Optional quotaMb As Integer = 2048)
            _root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            Directory.CreateDirectory(_root)
            _maxFileBytes = CLng(Math.Max(1, maxFileMb)) * 1024 * 1024
            _quotaBytes = CLng(Math.Max(1, quotaMb)) * 1024 * 1024
        End Sub

        Public ReadOnly Property Root As String
            Get
                Return _root
            End Get
        End Property

        ''' <summary>Runs the server on stdin/stdout until the host closes the pipe. Nothing else may write to stdout.</summary>
        Public Shared Function RunStdio(args As String()) As Integer
            Dim root As String = Nothing, maxFile = 20, quota = 2048
            Dim i = 0
            While i < args.Length
                Select Case args(i).ToLowerInvariant()
                    Case "--mcp-storage" : If i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("--", StringComparison.Ordinal) Then i += 1 : root = args(i)
                    Case "--max-file-mb" : If i + 1 < args.Length Then i += 1 : Integer.TryParse(args(i), maxFile)
                    Case "--quota-mb" : If i + 1 < args.Length Then i += 1 : Integer.TryParse(args(i), quota)
                End Select
                i += 1
            End While
            If String.IsNullOrWhiteSpace(root) Then root = Path.Combine(AppContext.BaseDirectory, "data", "shared")
            Dim server As New SharedStorageServer(root, maxFile, quota)
            Dim options As New McpServerOptions With {
                .ServerInfo = New Implementation With {.Name = "agentic-shared-storage", .Version = HostApplication.Version},
                .ToolCollection = server.CreateTools()}
            Dim transport As New StdioServerTransport("shared-storage", Nothing)
            Dim mcp = McpServer.Create(transport, options, Nothing, Nothing)
            mcp.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
            Return 0
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            Dim add = Sub(d As [Delegate], name As String, description As String, readOnlyTool As Boolean, destructive As Boolean)
                          tools.Add(McpServerTool.Create(d, New McpServerToolCreateOptions With {
                              .Name = name, .Description = description, .ReadOnly = readOnlyTool, .Idempotent = readOnlyTool, .Destructive = destructive}))
                      End Sub
            add(New Func(Of String, String)(AddressOf ListFiles), "list_files",
                "List the files and folders in a folder of the shared storage (default: the top folder).", True, False)
            add(New Func(Of String, Integer, Integer, String)(AddressOf ReadFile), "read_file",
                "Read a text file from the shared storage. Optionally only lines start_line..end_line (1-based).", True, False)
            add(New Func(Of String, String, Boolean, String)(AddressOf WriteFile), "write_file",
                "Write a text file to the shared storage (creates folders; overwrites unless append is true). Other authorized agents can read it.", False, True)
            add(New Func(Of String, String)(AddressOf MakeFolder), "make_folder", "Create a folder in the shared storage.", False, False)
            add(New Func(Of String, String, String)(AddressOf MovePath), "move", "Move or rename a file or folder within the shared storage.", False, True)
            add(New Func(Of String, String)(AddressOf DeletePath), "delete", "Delete a file or an empty folder from the shared storage.", False, True)
            add(New Func(Of String, String, Integer, String)(AddressOf SearchFiles), "search_files",
                "Find files whose name contains the text (case-insensitive), optionally below a folder.", True, False)
            add(New Func(Of String, String, Integer, String)(AddressOf GrepFiles), "grep",
                "Search the contents of text files for a phrase (case-insensitive). Returns matching lines with file and line number.", True, False)
            add(New Func(Of String)(AddressOf StorageInfo), "storage_info", "Show the shared storage's used space, quota and limits.", True, False)
            Return tools
        End Function

        ' ------------------------------------------------------------------------------------------ path safety

        ''' <summary>Maps a storage-relative path to a full path inside the root, or throws.</summary>
        Friend Function Resolve(relative As String, Optional allowRoot As Boolean = True) As String
            Dim rel = If(relative, "").Trim().Replace("\"c, "/"c).Trim("/"c)
            If rel.Length > 0 AndAlso (Path.IsPathRooted(relative.Trim()) OrElse rel.Contains(":"c)) Then Throw New McpException("Use a path relative to the shared storage, e.g. 'reports/summary.md'.")
            If rel.Split("/"c).Any(Function(p) p = "..") Then Throw New McpException("'..' is not allowed in shared storage paths.")
            Dim full = Path.GetFullPath(Path.Combine(_root, rel.Replace("/"c, Path.DirectorySeparatorChar)))
            If Not (full.Equals(_root, StringComparison.OrdinalIgnoreCase) OrElse full.StartsWith(_root & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Then
                Throw New McpException("Path is outside the shared storage.")
            End If
            If Not allowRoot AndAlso full.Equals(_root, StringComparison.OrdinalIgnoreCase) Then Throw New McpException("A file or folder path is required.")
            ' Refuse links/junctions anywhere on the way (they could lead outside the root).
            Dim probe = full
            While probe.Length > _root.Length
                If (File.Exists(probe) OrElse Directory.Exists(probe)) AndAlso File.GetAttributes(probe).HasFlag(FileAttributes.ReparsePoint) Then
                    Throw New McpException("Links are not allowed in the shared storage.")
                End If
                probe = Path.GetDirectoryName(probe)
            End While
            Return full
        End Function

        Private Function Relative(full As String) As String
            Return Path.GetRelativePath(_root, full).Replace(Path.DirectorySeparatorChar, "/"c)
        End Function

        Private Function UsedBytes() As Long
            Return New DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(Function(f) f.Length)
        End Function

        Private Shared Function LooksBinary(bytes As Byte(), count As Integer) As Boolean
            For i = 0 To Math.Min(count, 8000) - 1
                If bytes(i) = 0 Then Return True
            Next
            Return False
        End Function

        ' ------------------------------------------------------------------------------------------ tools

        Friend Function ListFiles(<Description("Folder, relative to the shared storage (empty = top folder)")> Optional folder As String = "") As String
            Dim dir = Resolve(folder)
            If Not Directory.Exists(dir) Then Throw New McpException($"Folder '{folder}' does not exist.")
            Dim sb As New StringBuilder()
            Dim n = 0
            For Each d In New DirectoryInfo(dir).EnumerateDirectories().OrderBy(Function(x) x.Name)
                If n >= MaxListEntries Then Exit For
                sb.AppendLine($"[folder] {Relative(d.FullName)}/") : n += 1
            Next
            For Each f In New DirectoryInfo(dir).EnumerateFiles().OrderBy(Function(x) x.Name)
                If n >= MaxListEntries Then Exit For
                sb.AppendLine($"{Relative(f.FullName)}  ({f.Length:N0} bytes, modified {f.LastWriteTimeUtc:yyyy-MM-dd HH:mm} UTC)") : n += 1
            Next
            If n = 0 Then Return If(String.IsNullOrEmpty(folder), "The shared storage is empty.", $"Folder '{folder}' is empty.")
            If n >= MaxListEntries Then sb.AppendLine($"(first {MaxListEntries} entries shown)")
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function ReadFile(<Description("File path, relative to the shared storage")> path As String,
                                 <Description("First line to return (1-based, optional)")> Optional start_line As Integer = 0,
                                 <Description("Last line to return (optional)")> Optional end_line As Integer = 0) As String
            Dim full = Resolve(path, allowRoot:=False)
            If Not File.Exists(full) Then Throw New McpException($"File '{path}' does not exist.")
            Dim bytes = File.ReadAllBytes(full)
            If LooksBinary(bytes, bytes.Length) Then Throw New McpException($"'{path}' is a binary file ({bytes.Length:N0} bytes) and cannot be read as text.")
            Dim text = Encoding.UTF8.GetString(bytes)
            If start_line > 0 OrElse end_line > 0 Then
                Dim lines = text.Split(ControlChars.Lf)
                Dim first = Math.Max(1, start_line), last = If(end_line > 0, Math.Min(end_line, lines.Length), lines.Length)
                If first > lines.Length Then Return $"(the file has only {lines.Length} lines)"
                text = String.Join(ControlChars.Lf, lines.Skip(first - 1).Take(Math.Max(0, last - first + 1)))
            End If
            If text.Length > MaxReadChars Then text = text.Substring(0, MaxReadChars) & $"{vbLf}... (truncated; read a line range to see more)"
            Return text
        End Function

        Friend Function WriteFile(<Description("File path, relative to the shared storage, e.g. 'reports/q3.md'")> path As String,
                                  <Description("Text content to write")> content As String,
                                  <Description("Append to the file instead of replacing it")> Optional append As Boolean = False) As String
            Dim full = Resolve(path, allowRoot:=False)
            If Directory.Exists(full) Then Throw New McpException($"'{path}' is a folder.")
            Dim data = Encoding.UTF8.GetBytes(If(content, ""))
            Dim existing = If(File.Exists(full), New FileInfo(full).Length, 0L)
            Dim newSize = If(append, existing + data.Length, CLng(data.Length))
            If newSize > _maxFileBytes Then Throw New McpException($"File would be {newSize:N0} bytes; the limit is {_maxFileBytes:N0} bytes.")
            If UsedBytes() - existing + newSize > _quotaBytes Then Throw New McpException($"The shared storage quota ({_quotaBytes \ (1024 * 1024)} MB) would be exceeded.")
            Directory.CreateDirectory(IO.Path.GetDirectoryName(full))
            If append Then
                Using fs As New FileStream(full, FileMode.Append, FileAccess.Write, FileShare.Read)
                    fs.Write(data, 0, data.Length)
                End Using
            Else
                ' Write to a temp file first so readers never see a half-written file.
                Dim tmp = full & ".tmp-" & Guid.NewGuid().ToString("N").Substring(0, 8)
                File.WriteAllBytes(tmp, data)
                File.Move(tmp, full, overwrite:=True)
            End If
            Return $"{If(append, "Appended to", "Wrote")} {Relative(full)} ({newSize:N0} bytes)."
        End Function

        Friend Function MakeFolder(<Description("Folder path, relative to the shared storage")> path As String) As String
            Dim full = Resolve(path, allowRoot:=False)
            If File.Exists(full) Then Throw New McpException($"'{path}' is a file.")
            Directory.CreateDirectory(full)
            Return $"Folder {Relative(full)}/ is ready."
        End Function

        Friend Function MovePath(<Description("Existing file or folder")> from_path As String, <Description("New path")> to_path As String) As String
            Dim src = Resolve(from_path, allowRoot:=False), dst = Resolve(to_path, allowRoot:=False)
            If File.Exists(dst) OrElse Directory.Exists(dst) Then Throw New McpException($"'{to_path}' already exists.")
            Directory.CreateDirectory(IO.Path.GetDirectoryName(dst))
            If File.Exists(src) Then
                File.Move(src, dst)
            ElseIf Directory.Exists(src) Then
                If dst.StartsWith(src & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Throw New McpException("A folder cannot be moved into itself.")
                Directory.Move(src, dst)
            Else
                Throw New McpException($"'{from_path}' does not exist.")
            End If
            Return $"Moved {from_path} to {Relative(dst)}."
        End Function

        Friend Function DeletePath(<Description("File or empty folder to delete")> path As String) As String
            Dim full = Resolve(path, allowRoot:=False)
            If File.Exists(full) Then
                File.Delete(full)
                Return $"Deleted {Relative(full)}."
            End If
            If Directory.Exists(full) Then
                If Directory.EnumerateFileSystemEntries(full).Any() Then Throw New McpException($"Folder '{path}' is not empty; delete its contents first.")
                Directory.Delete(full)
                Return $"Deleted folder {Relative(full)}/."
            End If
            Throw New McpException($"'{path}' does not exist.")
        End Function

        Friend Function SearchFiles(<Description("Text the file name must contain")> name As String,
                                    <Description("Folder to search below (optional)")> Optional folder As String = "",
                                    <Description("Maximum results (default 50)")> Optional max_results As Integer = 50) As String
            If String.IsNullOrWhiteSpace(name) Then Throw New McpException("name is required.")
            Dim dir = Resolve(folder)
            If Not Directory.Exists(dir) Then Throw New McpException($"Folder '{folder}' does not exist.")
            Dim hits = New DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).
                Where(Function(f) f.Name.Contains(name, StringComparison.OrdinalIgnoreCase)).
                Take(Math.Clamp(max_results, 1, 200)).Select(Function(f) $"{Relative(f.FullName)}  ({f.Length:N0} bytes)").ToList()
            Return If(hits.Count = 0, $"No files named like '{name}'.", String.Join(vbLf, hits))
        End Function

        Friend Function GrepFiles(<Description("Text to look for (case-insensitive)")> text As String,
                                  <Description("Folder to search below (optional)")> Optional folder As String = "",
                                  <Description("Maximum matching lines (default 50)")> Optional max_results As Integer = 50) As String
            If String.IsNullOrWhiteSpace(text) Then Throw New McpException("text is required.")
            Dim dir = Resolve(folder)
            If Not Directory.Exists(dir) Then Throw New McpException($"Folder '{folder}' does not exist.")
            Dim limit = Math.Clamp(max_results, 1, 200)
            Dim hits As New List(Of String)
            For Each f In New DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)
                If hits.Count >= limit Then Exit For
                If f.Length > 2 * 1024 * 1024 Then Continue For
                Dim bytes = File.ReadAllBytes(f.FullName)
                If LooksBinary(bytes, bytes.Length) Then Continue For
                Dim lines = Encoding.UTF8.GetString(bytes).Split(ControlChars.Lf)
                For n = 0 To lines.Length - 1
                    If lines(n).Contains(text, StringComparison.OrdinalIgnoreCase) Then
                        hits.Add($"{Relative(f.FullName)}:{n + 1}: {lines(n).TrimEnd(ControlChars.Cr).Trim()}")
                        If hits.Count >= limit Then Exit For
                    End If
                Next
            Next
            Return If(hits.Count = 0, $"No lines contain '{text}'.", String.Join(vbLf, hits))
        End Function

        Friend Function StorageInfo() As String
            Dim files = New DirectoryInfo(_root).EnumerateFiles("*", SearchOption.AllDirectories).ToList()
            Dim used = files.Sum(Function(f) f.Length)
            Return $"{files.Count:N0} files, {used:N0} bytes used of {_quotaBytes:N0} ({100.0 * used / _quotaBytes:0.0}%). Max file size {_maxFileBytes:N0} bytes. Text files only for reading."
        End Function
    End Class

End Namespace
