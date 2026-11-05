Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Logging
Imports Agentic.Mcp
Imports Agentic.Utilities

Namespace Work

    ''' <summary>One file or directory in a sandbox listing.</summary>
    Public Class SandboxEntry
        Public Property Name As String
        Public Property Type As String   ' file | dir | other
        Public Property Size As Long
    End Class

    ''' <summary>A directory listing from the sandbox.</summary>
    Public Class SandboxListing
        Public Property Dir As String
        Public Property Writable As Boolean
        Public Property Entries As New List(Of SandboxEntry)
    End Class

    ''' <summary>Bytes pulled out of the sandbox (or why they could not be).</summary>
    Public Class SandboxDownload
        Public Property Name As String
        Public Property Data As Byte()
        Public Property ErrorText As String       ' Nothing on success
        Public Property Zipped As Boolean
    End Class

    ''' <summary>A container seen on the sandbox host.</summary>
    Public Class SandboxContainer
        Public Property Id As String
        Public Property Name As String
        Public Property ComposeProject As String
        Public Property CreatedUtc As DateTime?
        Public Property Image As String
        ''' <summary>Ports published on the sandbox host (host port → container port), TCP only.</summary>
        Public Property Ports As New List(Of SandboxPort)
    End Class

    ''' <summary>A container port published on the sandbox's Docker host.</summary>
    Public Class SandboxPort
        Public Property HostPort As Integer
        Public Property ContainerPort As Integer
    End Class

    ''' <summary>An HTTP answer fetched from inside the sandbox (an app preview through the MCP connection).</summary>
    Public Class SandboxHttpResponse
        Public Property Status As Integer
        Public Property Headers As New List(Of KeyValuePair(Of String, String))
        Public Property Body As Byte() = Array.Empty(Of Byte)()
        ''' <summary>Why there is no answer (the app did not answer, too large…); Nothing when there is one.</summary>
        Public Property ErrorText As String
    End Class

    ''' <summary>A background process the host started in the sandbox.</summary>
    Public Class SandboxProcess
        Public Property Id As String
        Public Property Command As String
        Public Property Status As String       ' running | done | killed
        Public Property ExitCode As Integer?
    End Class

    ''' <summary>
    ''' Everything the host does against the code sandbox (the Open Terminal MCP server) beyond running one-off code:
    ''' listing and reading files, zipping a folder, pulling bytes out (base64 over MCP, as there is no binary download tool),
    ''' and listing/stopping long-lived processes and Docker containers. All of it funnels through McpServerManager, so it
    ''' honours the same connection, gate and timeouts as any other MCP call.
    ''' </summary>
    Public NotInheritable Class SandboxGateway
        Private ReadOnly _mcp As IMcpServerManager
        Private ReadOnly _log As ILog

        ' base64 of a file is bracketed by these so output from stat/echo is not mistaken for file content.
        Private Const B64Start As String = "__AGENTIC_B64_START__"
        Private Const B64End As String = "__AGENTIC_B64_END__"

        Public Sub New(mcp As IMcpServerManager, log As ILog)
            _mcp = mcp
            _log = log
        End Sub

        ''' <summary>The code server has the tools this gateway needs (run_command). Nothing/empty id or not connected = not available.</summary>
        Public Function Available(serverId As String) As Boolean
            If String.IsNullOrWhiteSpace(serverId) Then Return False
            Dim tools = _mcp.GetTools(serverId)
            Return tools IsNot Nothing AndAlso tools.Any(Function(t) String.Equals(t.Name, "run_command", StringComparison.OrdinalIgnoreCase))
        End Function

        Private Shared Function El(o As Object) As JsonElement
            Return JsonSerializer.SerializeToElement(o)
        End Function

        Private Shared Function EmptyArgs() As JsonElement
            Return JsonSerializer.SerializeToElement(New Dictionary(Of String, Object)())
        End Function

        ''' <summary>
        ''' Runs a shell command and returns its whole output and exit code, paging the sandbox's chunked output until the
        ''' process is done or <paramref name="maxChars"/> is reached. The command text is fixed by the caller; variable data
        ''' (paths) is passed through <paramref name="env"/> so it never needs shell-escaping.
        ''' </summary>
        Public Async Function RunAsync(serverId As String, command As String, env As Dictionary(Of String, String), waitSeconds As Integer, maxChars As Integer, ct As CancellationToken) As Task(Of (ExitCode As Integer?, Output As String, Capped As Boolean))
            Dim args As Object
            If env IsNot Nothing AndAlso env.Count > 0 Then
                args = New With {.command = command, .env = env, .wait = waitSeconds}
            Else
                args = New With {.command = command, .wait = waitSeconds}
            End If
            Dim first = Await _mcp.CallToolAsync(serverId, "run_command", El(args), ct).ConfigureAwait(False)
            Dim sb As New StringBuilder()
            Dim node = ParseResult(first)
            Dim id = node?("id")?.ToString()
            Dim status = If(node?("status")?.ToString(), "done")
            Dim exitCode As Integer? = ReadExit(node)
            Dim offset = AppendOutput(sb, node, maxChars)
            Dim capped = sb.Length >= maxChars
            Dim guard = 0
            While Not capped AndAlso Not String.IsNullOrEmpty(id) AndAlso guard < 400 AndAlso
                  (String.Equals(status, "running", StringComparison.OrdinalIgnoreCase) OrElse guard = 0)
                guard += 1
                ct.ThrowIfCancellationRequested()
                Dim poll = Await _mcp.CallToolAsync(serverId, "get_process_status",
                    El(New With {.process_id = id, .offset = offset, .wait = Math.Min(Math.Max(waitSeconds, 5), 30)}), ct).ConfigureAwait(False)
                Dim pn = ParseResult(poll)
                If pn Is Nothing Then Exit While
                status = If(pn("status")?.ToString(), status)
                exitCode = If(ReadExit(pn), exitCode)
                Dim before = offset
                offset = AppendOutput(sb, pn, maxChars, offset)
                capped = sb.Length >= maxChars
                ' Done and nothing new to drain: stop.
                If offset = before AndAlso Not String.Equals(status, "running", StringComparison.OrdinalIgnoreCase) Then Exit While
            End While
            Return (exitCode, sb.ToString(), capped)
        End Function

        Private Shared Function ParseResult(r As McpCallResult) As JsonObject
            If r Is Nothing Then Return Nothing
            Dim raw = If(r.Text, r.StructuredJson)
            If String.IsNullOrWhiteSpace(raw) Then Return Nothing
            Try
                Return TryCast(JsonNode.Parse(raw), JsonObject)
            Catch ex As JsonException
                Return Nothing
            End Try
        End Function

        Private Shared Function ReadExit(node As JsonObject) As Integer?
            Dim e = node?("exit_code")
            If e Is Nothing Then Return Nothing
            Dim v As Integer
            If Integer.TryParse(e.ToString(), v) Then Return v
            Return Nothing
        End Function

        ''' <summary>Appends the entries of an output array to the buffer and returns the next offset (entries consumed so far).</summary>
        Private Shared Function AppendOutput(sb As StringBuilder, node As JsonObject, maxChars As Integer, Optional baseOffset As Integer = 0) As Integer
            Dim arr = TryCast(node?("output"), JsonArray)
            Dim next_ = baseOffset
            If arr IsNot Nothing Then
                For Each e In arr
                    Dim data = If(e?("data")?.ToString(), If(TypeOf e Is JsonValue, e.ToString(), Nothing))
                    If data IsNot Nothing AndAlso sb.Length < maxChars Then sb.Append(data)
                Next
            End If
            Dim noff = node?("next_offset")
            Dim v As Integer
            If noff IsNot Nothing AndAlso Integer.TryParse(noff.ToString(), v) Then next_ = v Else next_ = baseOffset + If(arr?.Count, 0)
            Return next_
        End Function

        ''' <summary>Lists a directory in the sandbox (via the list_files tool). Returns Nothing if the tool is unavailable or errored.</summary>
        Public Async Function ListAsync(serverId As String, directory As String, ct As CancellationToken) As Task(Of SandboxListing)
            If Not _mcp.GetTools(serverId).Any(Function(t) String.Equals(t.Name, "list_files", StringComparison.OrdinalIgnoreCase)) Then Return Nothing
            Dim r = Await _mcp.CallToolAsync(serverId, "list_files", El(New With {.directory = If(directory, "/home/user")}), ct).ConfigureAwait(False)
            If r.IsError Then Return Nothing
            Dim node = ParseResult(r)
            If node Is Nothing Then Return Nothing
            Dim listing As New SandboxListing With {.Dir = If(node("dir")?.ToString(), directory), .Writable = NodeBool(node("writable"))}
            Dim arr = TryCast(node("entries"), JsonArray)
            If arr IsNot Nothing Then
                For Each e In arr
                    Dim o = TryCast(e, JsonObject)
                    If o Is Nothing Then Continue For
                    Dim sz As Long
                    Long.TryParse(If(o("size")?.ToString(), "0"), sz)
                    listing.Entries.Add(New SandboxEntry With {.Name = If(o("name")?.ToString(), ""), .Type = If(o("type")?.ToString(), "file"), .Size = sz})
                Next
            End If
            Return listing
        End Function

        Private Shared Function NodeBool(n As JsonNode) As Boolean
            If n Is Nothing Then Return False
            Dim b As Boolean
            Return Boolean.TryParse(n.ToString(), b) AndAlso b
        End Function

        ''' <summary>
        ''' Pulls a single file out of the sandbox as bytes (base64 over run_command). Fails cleanly when the path is missing,
        ''' is a directory, or is larger than <paramref name="maxBytes"/>.
        ''' </summary>
        Public Async Function DownloadFileAsync(serverId As String, path As String, maxBytes As Long, ct As CancellationToken) As Task(Of SandboxDownload)
            Dim name = PosixName(path)
            Dim env As New Dictionary(Of String, String) From {{"SBX_PATH", path}, {"SBX_MAX", maxBytes.ToString()}}
            Dim cmd =
                "p=""$SBX_PATH""" & vbLf &
                "if [ ! -e ""$p"" ]; then echo __NOFILE__; exit 0; fi" & vbLf &
                "if [ -d ""$p"" ]; then echo __ISDIR__; exit 0; fi" & vbLf &
                "sz=$(stat -c %s ""$p"" 2>/dev/null || echo -1)" & vbLf &
                "if [ ""$sz"" -gt ""$SBX_MAX"" ]; then echo __TOOBIG__=$sz; exit 0; fi" & vbLf &
                "echo " & B64Start & vbLf &
                "base64 -w0 ""$p""" & vbLf &
                "echo" & vbLf &
                "echo " & B64End
            Dim maxChars = CInt(Math.Min(Integer.MaxValue \ 2, (maxBytes \ 3L + 1L) * 4L + 4096L))
            Dim run = Await RunAsync(serverId, cmd, env, Math.Max(30, CInt(Math.Min(280, maxBytes \ (512L * 1024L) + 30))), maxChars, ct).ConfigureAwait(False)
            Dim text = run.Output
            If text.Contains("__NOFILE__") Then Return New SandboxDownload With {.Name = name, .ErrorText = "No such file in the sandbox: " & path}
            If text.Contains("__ISDIR__") Then Return New SandboxDownload With {.Name = name, .ErrorText = path & " is a folder. Zip it first (download a folder as a .zip)."}
            Dim tooBig = Global.System.Text.RegularExpressions.Regex.Match(text, "__TOOBIG__=(\d+)")
            If tooBig.Success Then Return New SandboxDownload With {.Name = name, .ErrorText = $"The file is {FormatSize(CLng(tooBig.Groups(1).Value))}, over the {FormatSize(maxBytes)} download limit."}
            Dim b64 = ExtractBetween(text, B64Start, B64End)
            If b64 Is Nothing Then Return New SandboxDownload With {.Name = name, .ErrorText = If(run.Capped, "The file is too large to pull out over MCP.", "Could not read the file from the sandbox.")}
            Try
                Dim data = Convert.FromBase64String(New String(b64.Where(Function(c) Not Char.IsWhiteSpace(c)).ToArray()))
                If data.LongLength > maxBytes Then Return New SandboxDownload With {.Name = name, .ErrorText = $"The file is over the {FormatSize(maxBytes)} download limit."}
                Return New SandboxDownload With {.Name = name, .Data = data}
            Catch ex As FormatException
                Return New SandboxDownload With {.Name = name, .ErrorText = "The sandbox returned data that could not be decoded."}
            End Try
        End Function

        ''' <summary>Zips a file or folder in the sandbox to a temp archive, pulls it out, then deletes the archive. Name ends in .zip.</summary>
        Public Async Function DownloadZipAsync(serverId As String, path As String, maxBytes As Long, ct As CancellationToken) As Task(Of SandboxDownload)
            Dim baseName = PosixName(path)
            If baseName.Length = 0 OrElse baseName = "/" Then baseName = "sandbox"
            Dim outPath = "/tmp/agentic_pack_" & Ids.NewToken(10) & ".zip"
            Dim env As New Dictionary(Of String, String) From {{"SBX_PATH", path}, {"SBX_OUT", outPath}}
            Dim cmd =
                "p=""$SBX_PATH""" & vbLf &
                "if [ ! -e ""$p"" ]; then echo __NOFILE__; exit 0; fi" & vbLf &
                "d=$(dirname ""$p""); n=$(basename ""$p"")" & vbLf &
                "( cd ""$d"" && zip -r -q ""$SBX_OUT"" ""$n"" ) || { echo __PACKFAIL__; exit 0; }" & vbLf &
                "echo __OK__"
            Dim pack = Await RunAsync(serverId, cmd, env, 280, 8192, ct).ConfigureAwait(False)
            If pack.Output.Contains("__NOFILE__") Then Return New SandboxDownload With {.Name = baseName & ".zip", .ErrorText = "No such file or folder in the sandbox: " & path}
            If Not pack.Output.Contains("__OK__") OrElse pack.Output.Contains("__PACKFAIL__") Then Return New SandboxDownload With {.Name = baseName & ".zip", .ErrorText = "Could not zip it in the sandbox (is zip installed?)."}
            Dim dl = Await DownloadFileAsync(serverId, outPath, maxBytes, ct).ConfigureAwait(False)
            dl.Name = baseName & ".zip"
            dl.Zipped = True
            ' Remove the temporary archive (best effort; a leftover in /tmp is harmless and the reaper does not touch files).
            Try
                Await RunAsync(serverId, "rm -f ""$SBX_OUT""", New Dictionary(Of String, String) From {{"SBX_OUT", outPath}}, 20, 1024, CancellationToken.None).ConfigureAwait(False)
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
            End Try
            Return dl
        End Function

        ' ----------------------------------------------------------------- processes and containers (for the reaper)

        ''' <summary>The background processes the host has started through this code server (Open Terminal's own tracker).</summary>
        Public Async Function ListProcessesAsync(serverId As String, ct As CancellationToken) As Task(Of List(Of SandboxProcess))
            Dim result As New List(Of SandboxProcess)
            If Not _mcp.GetTools(serverId).Any(Function(t) String.Equals(t.Name, "list_processes", StringComparison.OrdinalIgnoreCase)) Then Return result
            Dim r = Await _mcp.CallToolAsync(serverId, "list_processes", EmptyArgs(), ct).ConfigureAwait(False)
            Dim node = ParseResult(r)
            Dim arr = TryCast(If(node?("result"), node?("processes")), JsonArray)
            If arr Is Nothing AndAlso node IsNot Nothing Then arr = TryCast(node("result"), JsonArray)
            If arr Is Nothing Then Return result
            For Each e In arr
                Dim o = TryCast(e, JsonObject)
                If o Is Nothing Then Continue For
                result.Add(New SandboxProcess With {.Id = o("id")?.ToString(), .Command = o("command")?.ToString(),
                           .Status = If(o("status")?.ToString(), "running"), .ExitCode = ReadExit(o)})
            Next
            Return result
        End Function

        ''' <summary>New output of a process since the given offset (to tell whether it is still doing anything). Returns (length, nextOffset).</summary>
        Public Async Function ProcessOutputSinceAsync(serverId As String, processId As String, offset As Integer, ct As CancellationToken) As Task(Of (Length As Integer, NextOffset As Integer))
            Dim r = Await _mcp.CallToolAsync(serverId, "get_process_status", El(New With {.process_id = processId, .offset = offset, .wait = 0}), ct).ConfigureAwait(False)
            Dim node = ParseResult(r)
            Dim sb As New StringBuilder()
            Dim noff = AppendOutput(sb, node, Integer.MaxValue, offset)
            Return (sb.Length, noff)
        End Function

        ''' <summary>Stops a background process (SIGTERM, then SIGKILL if force). Ok if the tool accepted it; else why not.</summary>
        Public Async Function KillProcessAsync(serverId As String, processId As String, force As Boolean, ct As CancellationToken) As Task(Of (Ok As Boolean, Message As String))
            Try
                Dim r = Await _mcp.CallToolAsync(serverId, "kill_process", El(New With {.process_id = processId, .force = force}), ct).ConfigureAwait(False)
                Return (Not r.IsError, If(r.IsError, TextUtil.Truncate(If(r.Text, "kill_process returned an error"), 200), "process stopped"))
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                Return (False, TextUtil.Truncate(ex.Message, 200))
            End Try
        End Function

        ''' <summary>Whether `sudo -n docker` works in the sandbox (passwordless). False means containers cannot be managed.</summary>
        Public Async Function DockerAvailableAsync(serverId As String, ct As CancellationToken) As Task(Of Boolean)
            Dim run = Await RunAsync(serverId, "sudo -n docker version --format '{{.Server.Version}}' >/dev/null 2>&1 && echo __DOCKER_OK__ || echo __DOCKER_NO__", Nothing, 20, 4096, ct).ConfigureAwait(False)
            Return run.Output.Contains("__DOCKER_OK__")
        End Function

        ''' <summary>Running containers on the sandbox host (id, name, compose project, created time).</summary>
        Public Async Function ListContainersAsync(serverId As String, ct As CancellationToken) As Task(Of List(Of SandboxContainer))
            Dim result As New List(Of SandboxContainer)
            ' A unit separator between fields and a record marker per line survive names with spaces.
            Dim run = Await RunAsync(serverId, "sudo -n docker ps --no-trunc --format '__C__{{.ID}}\t{{.Names}}\t{{.Label ""com.docker.compose.project""}}\t{{.CreatedAt}}\t{{.Ports}}\t{{.Image}}'", Nothing, 30, 262144, ct).ConfigureAwait(False)
            For Each line In run.Output.Split({vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                If Not line.StartsWith("__C__") Then Continue For
                Dim parts = line.Substring(5).Split(ControlChars.Tab)
                If parts.Length < 4 Then Continue For
                result.Add(New SandboxContainer With {.Id = parts(0).Trim(), .Name = parts(1).Trim(),
                           .ComposeProject = parts(2).Trim(), .CreatedUtc = ParseDockerTime(parts(3)),
                           .Ports = If(parts.Length > 4, ParsePorts(parts(4)), New List(Of SandboxPort)),
                           .Image = If(parts.Length > 5, parts(5).Trim(), Nothing)})
            Next
            Return result
        End Function

        ''' <summary>Largest request body sent through the sandbox (it travels in an environment variable).</summary>
        Public Const MaxTunnelBodyBytes As Integer = 48 * 1024

        ''' <summary>
        ''' One HTTP request to a port published on the sandbox's Docker host, made from inside the sandbox (curl to its default
        ''' gateway, which is that host): app previews when the host's ports cannot be reached from here (Docker in WSL2 on
        ''' Windows publishes them only inside its VM). Everything variable travels in environment variables; curl's own output
        ''' is base64 between markers, so binary answers come through intact.
        ''' </summary>
        Public Async Function TunnelHttpAsync(serverId As String, port As Integer, method As String, pathAndQuery As String,
                                              headers As IEnumerable(Of KeyValuePair(Of String, String)), body As Byte(), maxBytes As Long,
                                              ct As CancellationToken) As Task(Of SandboxHttpResponse)
            If port <= 0 OrElse port > 65535 Then Return New SandboxHttpResponse With {.ErrorText = "Invalid port."}
            If Not Global.System.Text.RegularExpressions.Regex.IsMatch(If(method, ""), "^[A-Z]{3,10}$") Then Return New SandboxHttpResponse With {.ErrorText = "Invalid method."}
            If body IsNot Nothing AndAlso body.Length > MaxTunnelBodyBytes Then
                Return New SandboxHttpResponse With {.ErrorText = $"The request body is larger than {FormatSize(MaxTunnelBodyBytes)}, which is all a preview through the sandbox connection can send."}
            End If
            Dim path = If(String.IsNullOrEmpty(pathAndQuery), "/", pathAndQuery)
            If Not path.StartsWith("/", StringComparison.Ordinal) Then path = "/" & path
            Dim headerText As New StringBuilder()
            For Each h In If(headers, Enumerable.Empty(Of KeyValuePair(Of String, String))())
                If String.IsNullOrWhiteSpace(h.Key) OrElse h.Value Is Nothing Then Continue For
                ' One header per line: line breaks in a value would add headers of their own.
                headerText.Append(h.Key.Trim()).Append(": ").Append(h.Value.Replace(vbCr, " ").Replace(vbLf, " ")).Append(vbLf)
            Next
            Dim env As New Dictionary(Of String, String) From {
                {"SBX_PORT", port.ToString()}, {"SBX_METHOD", method}, {"SBX_PATH", path},
                {"SBX_HEADERS", Convert.ToBase64String(Encoding.UTF8.GetBytes(headerText.ToString()))},
                {"SBX_BODY", If(body Is Nothing OrElse body.Length = 0, "", Convert.ToBase64String(body))},
                {"SBX_MAX", maxBytes.ToString()}}
            ' The default gateway of the sandbox's own network is the Docker host, where published ports listen.
            Dim cmd =
                "h=$(awk '$2==""00000000"" {print $3; exit}' /proc/net/route)" & vbLf &
                "gw=$(printf '%d.%d.%d.%d' 0x$(echo $h | cut -c7-8) 0x$(echo $h | cut -c5-6) 0x$(echo $h | cut -c3-4) 0x$(echo $h | cut -c1-2))" & vbLf &
                "hf=$(mktemp); bf=$(mktemp); ef=$(mktemp)" & vbLf &
                "printf '%s' ""$SBX_HEADERS"" | base64 -d > ""$hf""" & vbLf &
                "printf '%s' ""$SBX_BODY"" | base64 -d > ""$bf""" & vbLf &
                "echo " & B64Start & vbLf &
                "if [ -s ""$bf"" ]; then" & vbLf &
                "  curl -sS -g -i --max-time 120 --max-filesize ""$SBX_MAX"" -X ""$SBX_METHOD"" -H @""$hf"" --data-binary @""$bf"" ""http://$gw:$SBX_PORT$SBX_PATH"" 2>""$ef"" | base64 -w0" & vbLf &
                "else" & vbLf &
                "  curl -sS -g -i --max-time 120 --max-filesize ""$SBX_MAX"" -X ""$SBX_METHOD"" -H @""$hf"" ""http://$gw:$SBX_PORT$SBX_PATH"" 2>""$ef"" | base64 -w0" & vbLf &
                "fi" & vbLf &
                "echo" & vbLf &
                "echo " & B64End & vbLf &
                "head -c 300 ""$ef""" & vbLf &
                "rm -f ""$hf"" ""$bf"" ""$ef"""
            Dim maxChars = CInt(Math.Min(Integer.MaxValue \ 2, (maxBytes \ 3L + 1L) * 4L + 16384L))
            Dim run = Await RunAsync(serverId, cmd, env, 130, maxChars, ct).ConfigureAwait(False)
            Dim b64 = ExtractBetween(run.Output, B64Start, B64End)
            Dim raw As Byte() = Array.Empty(Of Byte)()
            If b64 IsNot Nothing Then
                Try
                    raw = Convert.FromBase64String(New String(b64.Where(Function(c) Not Char.IsWhiteSpace(c)).ToArray()))
                Catch ex As FormatException
                End Try
            End If
            If raw.Length = 0 Then
                Dim tail = run.Output.Substring(Math.Max(0, run.Output.LastIndexOf(B64End, StringComparison.Ordinal) + B64End.Length)).Trim()
                Return New SandboxHttpResponse With {.ErrorText = If(tail.Length > 0, "The app did not answer: " & tail, "The app did not answer.")}
            End If
            Return ParseHttpResponse(raw)
        End Function

        ''' <summary>curl -i output: the last status block (skipping 1xx ones like 100 Continue), its headers, and the body bytes after it.</summary>
        Friend Shared Function ParseHttpResponse(raw As Byte()) As SandboxHttpResponse
            Dim r As New SandboxHttpResponse()
            Dim pos = 0
            While True
                Dim headEnd = IndexOf(raw, Encoding.ASCII.GetBytes(vbCrLf & vbCrLf), pos)
                Dim sep = 4
                If headEnd < 0 Then
                    headEnd = IndexOf(raw, Encoding.ASCII.GetBytes(vbLf & vbLf), pos)
                    sep = 2
                End If
                If headEnd < 0 Then Return New SandboxHttpResponse With {.ErrorText = "The app's answer could not be read."}
                Dim head = Encoding.ASCII.GetString(raw, pos, headEnd - pos)
                Dim lines = head.Replace(vbCr, "").Split(ChrW(10))
                Dim m = Global.System.Text.RegularExpressions.Regex.Match(lines(0), "^HTTP/[\d.]+\s+(\d{3})")
                If Not m.Success Then Return New SandboxHttpResponse With {.ErrorText = "The app's answer could not be read."}
                Dim status = CInt(m.Groups(1).Value)
                pos = headEnd + sep
                If status >= 100 AndAlso status < 200 AndAlso pos < raw.Length Then Continue While
                r.Status = status
                For Each line In lines.Skip(1)
                    Dim colon = line.IndexOf(":"c)
                    If colon > 0 Then r.Headers.Add(New KeyValuePair(Of String, String)(line.Substring(0, colon).Trim(), line.Substring(colon + 1).Trim()))
                Next
                Dim body(raw.Length - pos - 1) As Byte
                If body.Length > 0 Then Array.Copy(raw, pos, body, 0, body.Length)
                r.Body = body
                Return r
            End While
            Return r
        End Function

        Private Shared Function IndexOf(data As Byte(), pattern As Byte(), start As Integer) As Integer
            For i = start To data.Length - pattern.Length
                Dim hit = True
                For j = 0 To pattern.Length - 1
                    If data(i + j) <> pattern(j) Then hit = False : Exit For
                Next
                If hit Then Return i
            Next
            Return -1
        End Function

        ''' <summary>
        ''' Published TCP ports from docker's Ports column ("0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 9000/tcp"): each host port
        ''' once; ports that are only exposed (not published) and UDP are left out.
        ''' </summary>
        Friend Shared Function ParsePorts(s As String) As List(Of SandboxPort)
            Dim list As New List(Of SandboxPort)
            For Each part In If(s, "").Split(","c)
                Dim m = Global.System.Text.RegularExpressions.Regex.Match(part.Trim(), "^(?:\[[^\]]*\]|[^:\s]*):(\d+)->(\d+)/tcp$", Global.System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                If Not m.Success Then Continue For
                Dim hp = CInt(m.Groups(1).Value), cp = CInt(m.Groups(2).Value)
                If hp > 0 AndAlso Not list.Any(Function(x) x.HostPort = hp) Then list.Add(New SandboxPort With {.HostPort = hp, .ContainerPort = cp})
            Next
            Return list
        End Function

        ''' <summary>Stops a container (docker stop), and removes it when <paramref name="remove"/>. Returns (ok, message).</summary>
        Public Async Function StopContainerAsync(serverId As String, idOrName As String, remove As Boolean, ct As CancellationToken) As Task(Of (Ok As Boolean, Message As String))
            Dim env As New Dictionary(Of String, String) From {{"SBX_C", idOrName}}
            Dim cmd = "sudo -n docker stop ""$SBX_C"" >/dev/null 2>&1 && echo __STOP_OK__ || echo __STOP_FAIL__"
            If remove Then cmd &= vbLf & "sudo -n docker rm -f ""$SBX_C"" >/dev/null 2>&1 && echo __RM_OK__ || true"
            Dim run = Await RunAsync(serverId, cmd, env, 60, 8192, ct).ConfigureAwait(False)
            If run.Output.Contains("__STOP_OK__") Then Return (True, If(run.Output.Contains("__RM_OK__"), "stopped and removed", "stopped"))
            Return (False, "docker stop failed")
        End Function

        ''' <summary>Stops a compose project (docker compose down) from its working directory. Returns (ok, message).</summary>
        Public Async Function ComposeDownAsync(serverId As String, workingDir As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Message As String))
            If String.IsNullOrWhiteSpace(workingDir) Then Return (False, "no working directory recorded")
            Dim env As New Dictionary(Of String, String) From {{"SBX_WD", workingDir}}
            Dim cmd = "cd ""$SBX_WD"" 2>/dev/null && sudo -n docker compose down >/dev/null 2>&1 && echo __DOWN_OK__ || echo __DOWN_FAIL__"
            Dim run = Await RunAsync(serverId, cmd, env, 120, 8192, ct).ConfigureAwait(False)
            Return (run.Output.Contains("__DOWN_OK__"), If(run.Output.Contains("__DOWN_OK__"), "compose down", "compose down failed"))
        End Function

        ' ----------------------------------------------------------------- helpers

        Friend Shared Function ParseDockerTime(s As String) As DateTime?
            If String.IsNullOrWhiteSpace(s) Then Return Nothing
            ' Docker prints e.g. "2026-10-22 04:56:00 +0000 UTC"; .NET cannot parse the trailing " UTC".
            Dim t = s.Trim()
            If t.EndsWith(" UTC", StringComparison.Ordinal) Then t = t.Substring(0, t.Length - 4).Trim()
            Dim dto As DateTimeOffset
            If DateTimeOffset.TryParse(t, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AssumeUniversal, dto) Then Return dto.UtcDateTime
            Return Nothing
        End Function

        Friend Shared Function ExtractBetween(text As String, startMarker As String, endMarker As String) As String
            Dim i = text.IndexOf(startMarker, StringComparison.Ordinal)
            If i < 0 Then Return Nothing
            i += startMarker.Length
            Dim j = text.IndexOf(endMarker, i, StringComparison.Ordinal)
            If j < 0 Then Return Nothing
            Return text.Substring(i, j - i)
        End Function

        ''' <summary>The last path segment of a POSIX path (the sandbox is Linux; Path.GetFileName would use Windows rules).</summary>
        Friend Shared Function PosixName(path As String) As String
            Dim p = If(path, "").Replace("\"c, "/"c).TrimEnd("/"c)
            Dim i = p.LastIndexOf("/"c)
            Dim n = If(i >= 0, p.Substring(i + 1), p)
            Return If(n.Length = 0, "file", n)
        End Function

        Friend Shared Function FormatSize(bytes As Long) As String
            If bytes < 1024 Then Return bytes & " B"
            If bytes < 1024L * 1024L Then Return (bytes / 1024.0).ToString("0.#") & " KB"
            If bytes < 1024L * 1024L * 1024L Then Return (bytes / 1024.0 / 1024.0).ToString("0.#") & " MB"
            Return (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.##") & " GB"
        End Function

    End Class

End Namespace
