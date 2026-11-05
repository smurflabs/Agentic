Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>The agent tool servers built into Agentic.exe, by their switch ("Command": "{self}", "Arguments": ["--mcp-git", ...]).</summary>
    Friend NotInheritable Class BuiltInServers
        Private Sub New()
        End Sub

        Public Shared ReadOnly Switches As New Dictionary(Of String, Func(Of String(), Integer))(StringComparer.OrdinalIgnoreCase) From {
            {"--mcp-utilities", AddressOf UtilitiesServer.RunStdio}, {"--mcp-git", AddressOf GitServer.RunStdio}, {"--mcp-documents", AddressOf DocumentsServer.RunStdio}, {"--mcp-database", AddressOf DatabaseServer.RunStdio}, {"--mcp-email", AddressOf EmailServer.RunStdio}, {"--mcp-netops", AddressOf NetOpsServer.RunStdio}, {"--mcp-deps", AddressOf DepsServer.RunStdio}, {"--mcp-docker", AddressOf DockerServer.RunStdio}, {"--mcp-windows", AddressOf WindowsServer.RunStdio}, {"--mcp-data", AddressOf DataServer.RunStdio}, {"--mcp-files", AddressOf FilesServer.RunStdio}, {"--mcp-lookups", AddressOf LookupsServer.RunStdio}}

        ''' <summary>The server a command line asks for (stdout then carries the protocol), or Nothing.</summary>
        Public Shared Function Find(args As String()) As Func(Of String(), Integer)
            For Each a In args
                Dim run As Func(Of String(), Integer) = Nothing
                If Switches.TryGetValue(a, run) Then Return run
            Next
            Return Nothing
        End Function
    End Class

    ''' <summary>Helpers the built-in stdio MCP servers share: tool registration, arguments, output limits.</summary>
    Friend NotInheritable Class ToolKit
        Private Sub New()
        End Sub

        ''' <summary>Adds a tool made from a method (its parameters and their Description attributes become the input schema).</summary>
        Public Shared Sub Add(tools As McpServerPrimitiveCollection(Of McpServerTool), d As [Delegate], name As String, description As String,
                              Optional readOnlyTool As Boolean = True, Optional destructive As Boolean = False)
            tools.Add(McpServerTool.Create(d, New McpServerToolCreateOptions With {
                .Name = name, .Description = description, .ReadOnly = readOnlyTool, .Idempotent = readOnlyTool, .Destructive = destructive}))
        End Sub

        ''' <summary>The value after a command-line switch ("--roots a;b"), or Nothing.</summary>
        Public Shared Function Arg(args As String(), name As String) As String
            For i = 0 To args.Length - 2
                If String.Equals(args(i), name, StringComparison.OrdinalIgnoreCase) AndAlso Not args(i + 1).StartsWith("--", StringComparison.Ordinal) Then Return args(i + 1)
            Next
            Return Nothing
        End Function

        Public Shared Function IntArg(args As String(), name As String, fallback As Integer) As Integer
            Dim v As Integer
            Return If(Integer.TryParse(Arg(args, name), v), v, fallback)
        End Function

        ''' <summary>A list from an environment variable or switch: separated by commas, semicolons or line breaks.</summary>
        Public Shared Function SplitList(value As String) As List(Of String)
            Return If(value, "").Split({","c, ";"c, ControlChars.Lf, ControlChars.Cr}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(s) s.Trim()).Where(Function(s) s.Length > 0).ToList()
        End Function

        Public Shared Function Env(name As String) As String
            Dim v = Environment.GetEnvironmentVariable(name)
            Return If(String.IsNullOrWhiteSpace(v), Nothing, v.Trim())
        End Function

        ''' <summary>Text cut to maxChars with a note saying how much was left out.</summary>
        Public Shared Function Clip(text As String, maxChars As Integer) As String
            If text Is Nothing Then Return ""
            If text.Length <= maxChars Then Return text
            Return text.Substring(0, maxChars) & $"{vbLf}... ({text.Length - maxChars:N0} more characters not shown)"
        End Function

        ''' <summary>Runs a program (no shell) and returns its exit code and output, stopping it after the timeout.</summary>
        Public Shared Async Function RunProcessAsync(fileName As String, arguments As IEnumerable(Of String), workingDirectory As String, timeout As TimeSpan,
                                                    ct As CancellationToken, Optional env As IDictionary(Of String, String) = Nothing,
                                                    Optional stdin As String = Nothing) As Task(Of (ExitCode As Integer, Output As String, [Error] As String, TimedOut As Boolean))
            Dim psi As New ProcessStartInfo(fileName) With {
                .UseShellExecute = False, .RedirectStandardOutput = True, .RedirectStandardError = True, .RedirectStandardInput = True,
                .CreateNoWindow = True, .StandardOutputEncoding = Encoding.UTF8, .StandardErrorEncoding = Encoding.UTF8}
            If Not String.IsNullOrEmpty(workingDirectory) Then psi.WorkingDirectory = workingDirectory
            For Each a In arguments
                psi.ArgumentList.Add(a)
            Next
            If env IsNot Nothing Then
                For Each kv In env
                    psi.Environment(kv.Key) = kv.Value
                Next
            End If
            Using p As New Process With {.StartInfo = psi}
                p.Start()
                Dim outTask = p.StandardOutput.ReadToEndAsync()
                Dim errTask = p.StandardError.ReadToEndAsync()
                Try
                    If stdin IsNot Nothing Then Await p.StandardInput.WriteAsync(stdin).ConfigureAwait(False)
                    p.StandardInput.Close()
                Catch ex As IOException
                End Try
                Dim timedOut = False
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(timeout)
                    Try
                        Await p.WaitForExitAsync(cts.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException
                        timedOut = Not ct.IsCancellationRequested
                        Try
                            p.Kill(entireProcessTree:=True)
                        Catch kex As Exception When TypeOf kex Is InvalidOperationException OrElse TypeOf kex Is ComponentModel.Win32Exception
                        End Try
                        If Not timedOut Then Throw
                    End Try
                End Using
                Dim output = Await outTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(False)
                Dim err = Await errTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(False)
                Return (If(timedOut, -1, p.ExitCode), output, err, timedOut)
            End Using
        End Function
    End Class

    ''' <summary>
    ''' The folders a built-in server may touch. A relative path is taken from the first folder; an absolute path must be inside
    ''' one of them. "..", other drives and links that lead outside are refused.
    ''' </summary>
    Friend NotInheritable Class PathRoots
        Private ReadOnly _roots As List(Of String)

        Public Sub New(roots As IEnumerable(Of String))
            _roots = roots.Where(Function(r) Not String.IsNullOrWhiteSpace(r)).
                Select(Function(r) Path.GetFullPath(Environment.ExpandEnvironmentVariables(r.Trim())).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).
                Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If _roots.Count = 0 Then Throw New ArgumentException("At least one folder is required.")
            For Each r In _roots
                Directory.CreateDirectory(r)
            Next
        End Sub

        Public ReadOnly Property Roots As IReadOnlyList(Of String)
            Get
                Return _roots
            End Get
        End Property

        Public Function Describe() As String
            Return String.Join(", ", _roots)
        End Function

        ''' <summary>Full path for a path given by a model, or an McpException saying which folders are allowed.</summary>
        Public Function Resolve(p As String) As String
            Dim raw = If(p, "").Trim().Trim(""""c)
            Dim full As String
            If raw.Length = 0 Then
                full = _roots(0)
            ElseIf Path.IsPathRooted(raw) Then
                full = Path.GetFullPath(raw)
            Else
                If raw.Replace("\"c, "/"c).Split("/"c).Any(Function(s) s = "..") Then Throw New McpException("'..' is not allowed in paths.")
                full = Path.GetFullPath(Path.Combine(_roots(0), raw))
            End If
            full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            Dim root = _roots.FirstOrDefault(Function(r) full.Equals(r, StringComparison.OrdinalIgnoreCase) OrElse full.StartsWith(r & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            If root Is Nothing Then Throw New McpException($"'{p}' is outside the folders this tool may use: {Describe()}.")
            Dim probe = full
            While probe IsNot Nothing AndAlso probe.Length > root.Length
                If (File.Exists(probe) OrElse Directory.Exists(probe)) AndAlso File.GetAttributes(probe).HasFlag(FileAttributes.ReparsePoint) Then
                    Throw New McpException("Links are not allowed here.")
                End If
                probe = Path.GetDirectoryName(probe)
            End While
            Return full
        End Function

        ''' <summary>A path as the model should see it: relative to the first folder when inside it.</summary>
        Public Function Show(full As String) As String
            Dim first = _roots(0)
            If full.StartsWith(first & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Return Path.GetRelativePath(first, full).Replace("\"c, "/"c)
            Return full
        End Function
    End Class

End Namespace
