Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Text
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Protocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "shell" MCP server: runs PowerShell or cmd commands on the machine that hosts Agentic, starting in a work
    ''' folder ("Command": "{self}", "Arguments": ["--mcp-shell", "{data}\workspace"]). Commands run as the host's Windows
    ''' account, so grant it only to trusted agents (the shipped config also requires an administrator's approval per command).
    ''' Secrets in the host's environment (AGENTIC_*, AGENTHOST_*, *KEY*, *TOKEN*, *SECRET*, *PASSWORD*) are removed from the commands'
    ''' environment and output is capped. A command is never stopped for taking long: run_command waits up to its wait and then
    ''' lets it go on in the background (run_command_background starts one there), where command_output audits it and
    ''' kill_command stops it with everything it started.
    ''' </summary>
    Public NotInheritable Class ShellServer
        Private ReadOnly _workdir As String
        Private ReadOnly _defaultTimeout As Integer
        Private ReadOnly _maxOutputChars As Integer
        Private Shared ReadOnly SecretMarkers As String() = {"KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL"}

        Public Sub New(workdir As String, Optional defaultTimeoutSeconds As Integer = 120, Optional maxOutputKb As Integer = 64)
            _workdir = Path.GetFullPath(workdir).TrimEnd(Path.DirectorySeparatorChar)
            Directory.CreateDirectory(_workdir)
            _defaultTimeout = Math.Clamp(defaultTimeoutSeconds, 5, 3600)
            _maxOutputChars = Math.Clamp(maxOutputKb, 4, 4096) * 1024
        End Sub

        Public ReadOnly Property WorkDirectory As String
            Get
                Return _workdir
            End Get
        End Property

        Public Shared Function RunStdio(args As String()) As Integer
            Dim dir As String = Nothing, timeout = 120, maxKb = 64
            Dim i = 0
            While i < args.Length
                Select Case args(i).ToLowerInvariant()
                    Case "--mcp-shell" : If i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("--", StringComparison.Ordinal) Then i += 1 : dir = args(i)
                    Case "--timeout" : If i + 1 < args.Length Then i += 1 : Integer.TryParse(args(i), timeout)
                    Case "--max-output-kb" : If i + 1 < args.Length Then i += 1 : Integer.TryParse(args(i), maxKb)
                End Select
                i += 1
            End While
            Dim server As New ShellServer(If(dir, Path.Combine(AppContext.BaseDirectory, "data", "workspace")), timeout, maxKb)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            tools.Add(McpServerTool.Create(New Func(Of String, String, String, Integer, Integer, String)(AddressOf server.RunCommand), New McpServerToolCreateOptions With {
                .Name = "run_command", .Destructive = True,
                .Description = "Run a command on the Agentic host (Windows) and return its exit code and output. shell: powershell (default) or cmd. " &
                               $"folder: working folder relative to the shell workspace. It waits up to wait_seconds (default {timeout}, at most {MaxWaitSeconds}) for " &
                               "the command; one still running then is not stopped: it goes on in the background and you get its id and output so far - check on it " &
                               "with command_output, stop it with kill_command. For servers, watchers and long jobs use run_command_background. There is no keyboard input."}))
            tools.Add(McpServerTool.Create(New Func(Of String, String, String, String)(AddressOf server.RunCommandBackground), New McpServerToolCreateOptions With {
                .Name = "run_command_background", .Destructive = True,
                .Description = "Start a command in the background on the Agentic host and return its id at once; it runs until it ends or you stop it. Use it for " &
                               "servers, watchers, long builds and anything that may wait or hang - then audit it with command_output (still running? for how long? " &
                               "when did it last print something? its new output; warnings when it seems stuck or waits for input) and stop it with kill_command."}))
            tools.Add(McpServerTool.Create(New Func(Of String, Integer, String)(AddressOf server.CommandOutput), New McpServerToolCreateOptions With {
                .Name = "command_output", .ReadOnly = True,
                .Description = "Audit a background command (from run_command_background, or a run_command still running after its wait): running or finished " &
                               "(exit code), for how long, how long since it last printed something, its new output since you last looked, and a warning when it " &
                               $"seems to wait for input or to be stuck. wait_seconds (at most {MaxWaitSeconds}): first wait up to that long for it to finish or print more. " &
                               "Without id: all background commands."}))
            tools.Add(McpServerTool.Create(New Func(Of String, String)(AddressOf server.KillCommand), New McpServerToolCreateOptions With {
                .Name = "kill_command", .Destructive = True, .Description = "Stop a background command and everything it started."}))
            tools.Add(McpServerTool.Create(New Func(Of String)(AddressOf server.ShellInfo), New McpServerToolCreateOptions With {
                .Name = "shell_info", .ReadOnly = True, .Idempotent = True, .Description = "Show the host OS, user, available shells, the workspace folder and limits."}))
            Return BuiltInServer.Run("agentic-shell", tools)
        End Function

        Friend Function ResolveFolder(folder As String) As String
            Dim rel = If(folder, "").Trim().Replace("\"c, "/"c).Trim("/"c)
            If rel.Length = 0 Then Return _workdir
            If Path.IsPathRooted(folder.Trim()) OrElse rel.Contains(":"c) OrElse rel.Split("/"c).Contains("..") Then
                Throw New McpException("folder must be relative to the shell workspace (no drive letters or '..').")
            End If
            Dim full = Path.GetFullPath(Path.Combine(_workdir, rel.Replace("/"c, Path.DirectorySeparatorChar)))
            If Not full.StartsWith(_workdir & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then Throw New McpException("folder is outside the shell workspace.")
            Directory.CreateDirectory(full)
            Return full
        End Function

        Friend Shared Function IsSecretVariable(name As String) As Boolean
            Dim n = name.ToUpperInvariant()
            Return n.StartsWith(Global.Agentic.Configuration.ConfigLoader.EnvPrefix, StringComparison.Ordinal) OrElse
                   n.StartsWith(Global.Agentic.Configuration.ConfigLoader.LegacyEnvPrefix, StringComparison.Ordinal) OrElse n.StartsWith("MCP_", StringComparison.Ordinal) OrElse SecretMarkers.Any(Function(m) n.Contains(m))
        End Function

        ''' <summary>Longest wait of one call (run_command, command_output): below the MCP call timeout, so a call never times out.</summary>
        Public Const MaxWaitSeconds As Integer = 600
        ''' <summary>A shell that ended this long ago while its output is still open left something running that holds it (a server it started).</summary>
        Private Shared ReadOnly ExitGrace As TimeSpan = TimeSpan.FromSeconds(2)
        ''' <summary>Finished commands kept for reading, at most.</summary>
        Private Const KeepFinished As Integer = 20
        ''' <summary>A last line that asks a question and waits for an answer.</summary>
        Private Shared ReadOnly PromptLine As New Global.System.Text.RegularExpressions.Regex(
            "(\(y/n\)|\[y/n\]|\(yes/no\)|\[yes/no\]|press any key|press enter|password\s*:?\s*$|passphrase|are you sure|do you want to (continue|proceed)|continue\?\s*$|\?\s*$)",
            Global.System.Text.RegularExpressions.RegexOptions.IgnoreCase)

        ''' <summary>A command started here: run_command's (until it is done) or one in the background.</summary>
        Private NotInheritable Class ShellJob
            Public Id As String
            Public Command As String
            Public UseCmd As Boolean
            Public Folder As String
            Public Proc As Process
            Public ReadOnly Stdout As New StringBuilder()
            Public ReadOnly Stderr As New StringBuilder()
            ''' <summary>Output already returned, and decoded error text already returned.</summary>
            Public OutRead As Integer
            Public ErrShown As Integer
            ''' <summary>Output and error characters (raw) there were when it was last reported.</summary>
            Public SeenRaw As Integer
            Public Started As DateTime
            ''' <summary>When its shell ended (something it started may still hold its output open); Nothing while it runs.</summary>
            Public Ended As DateTime?
            Public ExitCode As Integer = -1
            Public OutDone As Boolean
            Public ErrDone As Boolean
            Public LastOutput As DateTime?
            ''' <summary>Windows: its own job (everything it starts), so stopping it stops what outlived its shell too.</summary>
            Public Group As IntPtr

            Public ReadOnly Property Closed As Boolean
                Get
                    Return OutDone AndAlso ErrDone
                End Get
            End Property
        End Class

        Private ReadOnly _jobs As New ConcurrentDictionary(Of String, ShellJob)(StringComparer.OrdinalIgnoreCase)
        Private _jobSeq As Integer

        Friend Function RunCommand(<Description("The command line to run")> command As String,
                                   <Description("powershell (default) or cmd")> Optional shell As String = "powershell",
                                   <Description("Working folder relative to the shell workspace (optional)")> Optional folder As String = "",
                                   <Description("How long to wait for the command before it goes on in the background (default from the server configuration)")> Optional wait_seconds As Integer = 0,
                                   <Description("Older name of wait_seconds")> Optional timeout_seconds As Integer = 0) As String
            Dim wait = Math.Clamp(If(wait_seconds > 0, wait_seconds, If(timeout_seconds > 0, timeout_seconds, _defaultTimeout)), 1, MaxWaitSeconds)
            Dim job = StartJob(command, shell, folder)
            Dim how = WaitFor(job, TimeSpan.FromSeconds(wait))
            Dim where = $" · {(DateTime.UtcNow - job.Started).TotalSeconds:0.0} s · {If(job.UseCmd, "cmd", "powershell")} in {RelativeFolder(job.Folder)}"
            Dim sb As New StringBuilder()
            If how = "running" Then
                ' Not stopped: the agent gets control back and decides (wait more, look at it, stop it).
                sb.Append($"Still running after {wait} s, so it goes on in the background as {job.Id} (it was not stopped){where}").Append(vbLf)
                AppendNewOutput(sb, job, "--- output so far ---")
                sb.Append($"Check on it with command_output (id {job.Id}; wait_seconds to wait for it to finish), or stop it with kill_command.")
                Return sb.ToString().TrimEnd()
            End If
            sb.Append($"Exit code {job.ExitCode}{where}").Append(vbLf)
            AppendNewOutput(sb, job, "--- output ---")
            If how = "shell-ended" Then
                sb.Append($"(Something this command started is still running and printing: it goes on in the background as {job.Id}. See what it prints " &
                          "with command_output; stop it with kill_command.)")
            Else
                Forget(job)
                ' Still a normal result: the agent needs the output to understand what went wrong.
                If job.ExitCode <> 0 Then sb.Append("(the command reported failure)")
            End If
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function RunCommandBackground(<Description("The command line to run")> command As String,
                                             <Description("powershell (default) or cmd")> Optional shell As String = "powershell",
                                             <Description("Working folder relative to the shell workspace (optional)")> Optional folder As String = "") As String
            Dim job = StartJob(command, shell, folder)
            Return $"Started in the background as {job.Id} ({If(job.UseCmd, "cmd", "powershell")} in {RelativeFolder(job.Folder)}); it runs until it ends or you " &
                   $"stop it. Check on it with command_output (id {job.Id}; wait_seconds to wait for it) and stop it with kill_command."
        End Function

        ''' <summary>
        ''' command_output: how a command is doing - running for how long, or its exit code; how long since it last printed
        ''' something; its new output; and a warning when it seems to wait for input or to be stuck.
        ''' </summary>
        Friend Function CommandOutput(<Description("The background command's id (bg1, …); empty lists them all")> Optional id As String = "",
                                      <Description("First wait up to this long for it to finish or print more")> Optional wait_seconds As Integer = 0) As String
            If String.IsNullOrWhiteSpace(id) Then Return If(_jobs.IsEmpty, "There are no background commands.", "Background commands:" & vbLf & ListJobs(vbLf))
            Dim job As ShellJob = Nothing
            If Not _jobs.TryGetValue(id.Trim(), job) Then Return $"No background command {id}. " & If(_jobs.IsEmpty, "There are none.", $"There are: {ListJobs("; ")}.")
            ' Waits only while there is nothing new to report.
            If wait_seconds > 0 AndAlso Not JobDone(job) AndAlso OutputLength(job) = job.SeenRaw Then
                Dim from = OutputLength(job)
                Dim deadline = DateTime.UtcNow.AddSeconds(Math.Min(MaxWaitSeconds, wait_seconds))
                While Not JobDone(job) AndAlso OutputLength(job) = from AndAlso DateTime.UtcNow < deadline
                    Thread.Sleep(200)
                End While
                ' Output comes in pieces: a moment more for the rest of it.
                If Not JobDone(job) AndAlso OutputLength(job) > from Then Thread.Sleep(500)
            End If
            Dim now = DateTime.UtcNow
            Dim state = If(Not ShellEnded(job), $"running for {Span(now - job.Started)}",
                           $"finished with exit code {job.ExitCode} after {Span(job.Ended.Value - job.Started)}" & If(job.Closed, "", "; something it started is still running and printing"))
            Dim quiet = If(job.LastOutput.HasValue, $"last output {Span(now - job.LastOutput.Value)} ago", "no output yet")
            Dim sb As New StringBuilder($"{job.Id} ({ShortCommand(job.Command)}): {state}; {quiet}.")
            sb.Append(vbLf)
            Dim warning = JobWarning(job, now)
            If warning.Length > 0 Then sb.Append(warning).Append(vbLf)
            AppendNewOutput(sb, job, "--- new output ---")
            Return sb.ToString().TrimEnd()
        End Function

        Friend Function KillCommand(<Description("The background command's id (bg1, …)")> id As String) As String
            Dim job As ShellJob = Nothing
            If String.IsNullOrWhiteSpace(id) OrElse Not _jobs.TryRemove(id.Trim(), job) Then
                Return $"No background command {id}." & If(_jobs.IsEmpty, "", $" There are: {ListJobs("; ")}.")
            End If
            If job.Group <> IntPtr.Zero Then
                ' Its own job: everything it started, even what outlived its shell.
                ProcessGroups.Terminate(job.Group)
                job.Group = IntPtr.Zero
            Else
                Try
                    If Not job.Proc.HasExited Then job.Proc.Kill(entireProcessTree:=True)
                Catch
                End Try
            End If
            job.Proc.Dispose()
            Return $"Stopped {job.Id} ({ShortCommand(job.Command)})."
        End Function

        Private Function StartJob(command As String, shell As String, folder As String) As ShellJob
            If String.IsNullOrWhiteSpace(command) Then Throw New McpException("command is required.")
            Dim useCmd = String.Equals(shell, "cmd", StringComparison.OrdinalIgnoreCase)
            If Not useCmd AndAlso Not String.IsNullOrEmpty(shell) AndAlso Not String.Equals(shell, "powershell", StringComparison.OrdinalIgnoreCase) Then
                Throw New McpException("shell must be powershell or cmd.")
            End If
            Dim cwd = ResolveFolder(folder)
            Dim psi As New ProcessStartInfo With {
                .UseShellExecute = False, .CreateNoWindow = True, .WorkingDirectory = cwd,
                .RedirectStandardOutput = True, .RedirectStandardError = True, .RedirectStandardInput = True,
                .StandardOutputEncoding = Encoding.UTF8, .StandardErrorEncoding = Encoding.UTF8}
            If useCmd Then
                psi.FileName = "cmd.exe"
                psi.Arguments = "/d /s /c ""chcp 65001 >nul & " & command & """"
            Else
                psi.FileName = "powershell.exe"
                Dim script = "[Console]::OutputEncoding = [Text.Encoding]::UTF8; $ProgressPreference = 'SilentlyContinue'; " & AdaptForWindowsPowerShell(command)
                psi.Arguments = "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " & Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
            End If
            For Each key In psi.Environment.Keys.ToList()
                If IsSecretVariable(key) Then psi.Environment.Remove(key)
            Next
            Dim job As New ShellJob With {.Id = "bg" & Interlocked.Increment(_jobSeq), .Command = command, .UseCmd = useCmd, .Folder = cwd,
                                          .Started = DateTime.UtcNow, .Proc = New Process With {.StartInfo = psi}}
            AddHandler job.Proc.OutputDataReceived, Sub(s, e) Collect(job, job.Stdout, e.Data, False)
            AddHandler job.Proc.ErrorDataReceived, Sub(s, e) Collect(job, job.Stderr, e.Data, True)
            job.Proc.Start()
            job.Proc.StandardInput.Close()
            job.Group = ProcessGroups.ForCommand(job.Proc)
            job.Proc.BeginOutputReadLine()
            job.Proc.BeginErrorReadLine()
            _jobs(job.Id) = job
            PruneJobs()
            Return job
        End Function

        Private Shared Sub Collect(job As ShellJob, buffer As StringBuilder, line As String, isError As Boolean)
            If line Is Nothing Then
                ' The end of that stream: nothing writes to it any more.
                If isError Then job.ErrDone = True Else job.OutDone = True
                Return
            End If
            SyncLock buffer
                buffer.Append(line).Append(vbLf)
                If buffer.Length > 1000000 Then
                    Dim cut = buffer.Length - 500000
                    buffer.Remove(0, cut)
                    If isError Then job.ErrShown = 0 Else job.OutRead = Math.Max(0, job.OutRead - cut)
                End If
            End SyncLock
            job.LastOutput = DateTime.UtcNow
        End Sub

        ''' <summary>Whether its shell has ended (noted the first time it is seen).</summary>
        Private Shared Function ShellEnded(job As ShellJob) As Boolean
            If job.Ended.HasValue Then Return True
            Try
                If Not job.Proc.HasExited Then Return False
                job.ExitCode = job.Proc.ExitCode
            Catch ex As InvalidOperationException
            End Try
            job.Ended = DateTime.UtcNow
            Return True
        End Function

        ''' <summary>Done: it ended and its output too, or its shell ended a moment ago (what still holds the output is not waited for).</summary>
        Private Shared Function JobDone(job As ShellJob) As Boolean
            If Not ShellEnded(job) Then Return False
            Return job.Closed OrElse DateTime.UtcNow - job.Ended.Value >= ExitGrace
        End Function

        ''' <summary>Waits up to wait: "finished", "shell-ended" (something it started still holds its output) or "running". Nothing is stopped.</summary>
        Private Shared Function WaitFor(job As ShellJob, wait As TimeSpan) As String
            Dim deadline = DateTime.UtcNow + wait
            Do
                If ShellEnded(job) Then
                    If job.Closed Then Return "finished"
                    If DateTime.UtcNow - job.Ended.Value >= ExitGrace Then Return "shell-ended"
                End If
                If DateTime.UtcNow >= deadline Then Return "running"
                Thread.Sleep(100)
            Loop
        End Function

        Private Shared Function OutputLength(job As ShellJob) As Integer
            Dim n As Integer
            SyncLock job.Stdout
                n = job.Stdout.Length
            End SyncLock
            SyncLock job.Stderr
                n += job.Stderr.Length
            End SyncLock
            Return n
        End Function

        ''' <summary>The output and errors not returned yet (both count as returned now), capped.</summary>
        Private Sub AppendNewOutput(sb As StringBuilder, job As ShellJob, title As String)
            Dim out As String, errText As String
            SyncLock job.Stdout
                out = job.Stdout.ToString(job.OutRead, job.Stdout.Length - job.OutRead)
                job.OutRead = job.Stdout.Length
            End SyncLock
            SyncLock job.Stderr
                Dim all = If(job.UseCmd, job.Stderr.ToString(), DecodeClixml(job.Stderr.ToString())).TrimEnd()
                errText = If(all.Length > job.ErrShown, all.Substring(job.ErrShown), "")
                job.ErrShown = Math.Max(job.ErrShown, all.Length)
            End SyncLock
            job.SeenRaw = OutputLength(job)
            sb.Append(title).Append(vbLf).Append(If(out.Trim().Length = 0, "(none)", Cap(out.TrimEnd()))).Append(vbLf)
            If errText.Trim().Length > 0 Then sb.Append("--- errors ---").Append(vbLf).Append(Cap(errText.Trim())).Append(vbLf)
        End Sub

        ''' <summary>At most the configured output size: its start and, mostly, its end (where errors and results usually are).</summary>
        Private Function Cap(text As String) As String
            If text.Length <= _maxOutputChars Then Return text
            Dim head = _maxOutputChars \ 4
            Return text.Substring(0, head) & vbLf & $"... ({(text.Length - _maxOutputChars) \ 1024} KB left out) ..." & vbLf & text.Substring(text.Length - (_maxOutputChars - head))
        End Function

        ''' <summary>Something looks wrong with a command that is still running: a question waiting for an answer, or a long silence.</summary>
        Private Shared Function JobWarning(job As ShellJob, now As DateTime) As String
            If ShellEnded(job) Then Return ""
            Dim idle = now - If(job.LastOutput, job.Started)
            Dim last As String
            SyncLock job.Stdout
                last = job.Stdout.ToString().Split({vbCr, vbLf}, StringSplitOptions.RemoveEmptyEntries).Select(Function(l) l.Trim()).LastOrDefault(Function(l) l.Length > 0)
            End SyncLock
            If idle.TotalSeconds >= 5 AndAlso Not String.IsNullOrEmpty(last) AndAlso PromptLine.IsMatch(last) Then
                Return $"Warning: its last line looks like a question waiting for an answer (""{If(last.Length > 160, last.Substring(last.Length - 160), last)}""). " &
                       "Commands get no keyboard input here: stop it with kill_command and run it so it does not ask (for example -y, --yes, -Force, -Confirm:$false, --no-input)."
            End If
            If idle.TotalMinutes >= 2 Then
                Return $"Warning: it has printed nothing for {Span(idle)}. It may be waiting for input, stuck, or busy without printing: if it should have " &
                       "printed something by now, stop it with kill_command and find out why."
            End If
            Return ""
        End Function

        ''' <summary>Drops a finished command whose output was returned (what it may have left running is not stopped).</summary>
        Private Sub Forget(job As ShellJob)
            Dim gone As ShellJob = Nothing
            If Not _jobs.TryRemove(job.Id, gone) Then Return
            ProcessGroups.Release(job.Group)
            job.Group = IntPtr.Zero
            job.Proc.Dispose()
        End Sub

        ''' <summary>Only the newest finished commands are kept.</summary>
        Private Sub PruneJobs()
            Dim finished = _jobs.Values.Where(Function(j) j.Closed AndAlso j.Ended.HasValue).OrderBy(Function(j) j.Started).ToList()
            For Each j In finished.Take(Math.Max(0, finished.Count - KeepFinished))
                Forget(j)
            Next
        End Sub

        Private Function ListJobs(separator As String) As String
            Dim now = DateTime.UtcNow
            Return String.Join(separator, _jobs.Values.OrderBy(Function(j) j.Started).Select(Function(j) $"{j.Id}: {ShortCommand(j.Command)} (" &
                If(Not ShellEnded(j), $"running for {Span(now - j.Started)}", If(j.Closed, $"exit {j.ExitCode}", $"exit {j.ExitCode}, something it started still runs")) & ")"))
        End Function

        ''' <summary>45 s, 3 min 20 s, 1 h 5 min.</summary>
        Friend Shared Function Span(t As TimeSpan) As String
            Dim s = CLng(Math.Max(0, Math.Round(t.TotalSeconds)))
            If s < 60 Then Return $"{s} s"
            If s < 3600 Then Return $"{s \ 60} min" & If(s Mod 60 > 0, $" {s Mod 60} s", "")
            Dim m = (s \ 60) Mod 60
            Return $"{s \ 3600} h" & If(m > 0, $" {m} min", "")
        End Function

        Private Shared Function ShortCommand(command As String) As String
            Dim c = If(command, "").Trim().Split({vbCr, vbLf}, StringSplitOptions.None)(0)
            Return If(c.Length > 80, c.Substring(0, 80) & "…", c)
        End Function

        ''' <summary>
        ''' Windows PowerShell 5.1 has no &amp;&amp; or || (models write them out of habit, and the whole command then fails to parse).
        ''' A chain of only &amp;&amp; becomes "a; if (-not $?) { exit … }; b"; a chain of only || becomes "a; if ($?) { exit 0 }; b".
        ''' Operators inside quotes, (), {} or [] are left alone, and so are mixed chains.
        ''' </summary>
        Friend Shared Function AdaptForWindowsPowerShell(command As String) As String
            Dim parts As New List(Of String)
            Dim ops As New List(Of String)
            Dim depth = 0
            Dim quote As Char = Nothing
            Dim start = 0
            Dim i = 0
            While i < command.Length
                Dim c = command(i)
                If c = "`"c Then
                    i += 2
                    Continue While
                End If
                If quote <> Nothing Then
                    If c = quote Then quote = Nothing
                ElseIf c = "'"c OrElse c = """"c Then
                    quote = c
                ElseIf c = "("c OrElse c = "{"c OrElse c = "["c Then
                    depth += 1
                ElseIf (c = ")"c OrElse c = "}"c OrElse c = "]"c) AndAlso depth > 0 Then
                    depth -= 1
                ElseIf depth = 0 AndAlso i + 1 < command.Length AndAlso ((c = "&"c AndAlso command(i + 1) = "&"c) OrElse (c = "|"c AndAlso command(i + 1) = "|"c)) Then
                    parts.Add(command.Substring(start, i - start).Trim())
                    ops.Add(command.Substring(i, 2))
                    i += 2
                    start = i
                    Continue While
                End If
                i += 1
            End While
            If ops.Count = 0 Then Return command
            parts.Add(command.Substring(start).Trim())
            If parts.Any(Function(p) p.Length = 0) OrElse ops.Distinct().Count() > 1 Then Return command
            Dim guard = If(ops(0) = "&&", "; if (-not $?) { exit $(if ($LASTEXITCODE) { $LASTEXITCODE } else { 1 }) }; ", "; if ($?) { exit 0 }; ")
            Return String.Join(guard, parts)
        End Function

        ''' <summary>
        ''' powershell -EncodedCommand writes its error stream as CLIXML ("#&lt; CLIXML" and serialized objects); this turns it
        ''' back into the error text and drops progress records.
        ''' </summary>
        Friend Shared Function DecodeClixml(text As String) As String
            If String.IsNullOrEmpty(text) OrElse Not text.TrimStart().StartsWith("#< CLIXML", StringComparison.Ordinal) Then Return If(text, "")
            Dim sb As New StringBuilder()
            For Each m As Global.System.Text.RegularExpressions.Match In Global.System.Text.RegularExpressions.Regex.Matches(text, "<S S=""(?:Error|Warning|Verbose|Information|debug)"">(.*?)</S>", Global.System.Text.RegularExpressions.RegexOptions.Singleline)
                sb.Append(m.Groups(1).Value)
            Next
            Dim decoded = Net.WebUtility.HtmlDecode(sb.ToString())
            decoded = Global.System.Text.RegularExpressions.Regex.Replace(decoded, "_x([0-9A-Fa-f]{4})_", Function(m) ChrW(Convert.ToInt32(m.Groups(1).Value, 16)).ToString())
            Return decoded.Replace(vbCrLf, vbLf).Trim()
        End Function


        Private Function RelativeFolder(full As String) As String
            Dim rel = Path.GetRelativePath(_workdir, full)
            Return If(rel = ".", "workspace", "workspace/" & rel.Replace("\"c, "/"c))
        End Function

        Friend Function ShellInfo() As String
            Return $"Host: {Environment.MachineName} ({Runtime.InteropServices.RuntimeInformation.OSDescription}), user {Environment.UserDomainName}\{Environment.UserName}." & vbLf &
                   $"Shells: powershell (Windows PowerShell 5.1: use ; between commands; a plain a && b chain is run step by step), cmd. Workspace: {_workdir}." & vbLf &
                   $"run_command waits {_defaultTimeout} s by default (at most {MaxWaitSeconds}), then lets a command go on in the background - nothing is stopped for " &
                   $"taking long (command_output audits it, kill_command stops it); output capped at {_maxOutputChars \ 1024} KB per result; no interactive input. Secrets are removed from the environment."
        End Function
    End Class

    ''' <summary>Shared startup for the built-in stdio MCP servers.</summary>
    Friend Module BuiltInServer
        Public Function Run(name As String, tools As McpServerPrimitiveCollection(Of McpServerTool), Optional configure As Action(Of McpServerOptions) = Nothing) As Integer
            Dim options As New McpServerOptions With {
                .ServerInfo = New Implementation With {.Name = name, .Version = HostApplication.Version}, .ToolCollection = tools}
            configure?.Invoke(options)
            Dim transport As New StdioServerTransport(name, Nothing)
            Dim server = McpServer.Create(transport, options, Nothing, Nothing)
            server.RunAsync(CancellationToken.None).GetAwaiter().GetResult()
            Return 0
        End Function
    End Module

End Namespace
