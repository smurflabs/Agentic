Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.ServiceProcess
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "windows" MCP server ("Command": "{self}", "Arguments": ["--mcp-windows"]): read the state of the Agentic host -
    ''' overview (OS, uptime, CPU, memory, disks), services, event logs, processes, scheduled tasks and network connections - and,
    ''' with approval, start/stop/restart services and end processes. Runs fixed commands only (no shell input from the model).
    ''' </summary>
    Public NotInheritable Class WindowsServer
        Private Shared ReadOnly Inv As CultureInfo = CultureInfo.InvariantCulture
        Private ReadOnly _protected As HashSet(Of String)

        Friend Sub New(Optional protectedNames As IEnumerable(Of String) = Nothing)
            _protected = New HashSet(Of String)(If(protectedNames, Enumerable.Empty(Of String)()), StringComparer.OrdinalIgnoreCase) From {
                "csrss", "wininit", "winlogon", "lsass", "services", "smss", "System", "Idle", "svchost", "Registry", "dwm", "fontdrvhost", "MsMpEng"}
            _protected.Add(Process.GetCurrentProcess().ProcessName)
        End Sub

        Public Shared Function RunStdio(args As String()) As Integer
            Dim server As New WindowsServer(ToolKit.SplitList(ToolKit.Env("WINDOWS_PROTECTED_PROCESSES")))
            Return BuiltInServer.Run("agentic-windows", server.CreateTools())
        End Function

        Public Function CreateTools() As McpServerPrimitiveCollection(Of McpServerTool)
            Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
            ToolKit.Add(tools, New Func(Of CancellationToken, Task(Of String))(AddressOf OverviewAsync), "system_overview",
                "The host at a glance: Windows version, uptime, CPU load, memory, disks (free space) and the busiest processes.")
            ToolKit.Add(tools, New Func(Of String, String, String)(AddressOf Services), "list_services",
                "Windows services: name, display name, status and start type (filter: part of the name; state: running, stopped or all).")
            ToolKit.Add(tools, New Func(Of String, String, CancellationToken, Task(Of String))(AddressOf ControlServiceAsync), "control_service",
                "start, stop or restart a Windows service (waits up to 60 s for it).", False, True)
            ToolKit.Add(tools, New Func(Of String, String, Integer, Integer, String, CancellationToken, Task(Of String))(AddressOf EventsAsync), "event_log",
                "Recent Windows event log entries: log System, Application (default) or another; level error (default), warning or all; from the last hours; optional source.")
            ToolKit.Add(tools, New Func(Of String, String, Integer, CancellationToken, Task(Of String))(AddressOf ProcessesAsync), "list_processes",
                "Running processes with CPU % (sampled over 1 s), memory, start time and PID; sort by cpu (default) or memory; filter by name.")
            ToolKit.Add(tools, New Func(Of Integer, String, String)(AddressOf KillProcess), "end_process",
                "End a process (and its children) by PID, or every process with a name. System processes and Agentic itself are protected.", False, True)
            ToolKit.Add(tools, New Func(Of String, Boolean, CancellationToken, Task(Of String))(AddressOf TasksAsync), "scheduled_tasks",
                "Scheduled tasks: name, status, last run and result, next run, what it runs (Microsoft's own tasks hidden unless include_system).")
            ToolKit.Add(tools, New Func(Of Boolean, Integer, CancellationToken, Task(Of String))(AddressOf ConnectionsAsync), "network_connections",
                "TCP/UDP ports in use with the owning process: listening ports (default) or all connections, optionally only one port.")
            Return tools
        End Function

        Private Shared Sub RequireWindows()
            If Not OperatingSystem.IsWindows() Then Throw New McpException("These tools work on Windows hosts only.")
        End Sub

        Private Shared Async Function PowerShellJsonAsync(script As String, ct As CancellationToken) As Task(Of JsonNode)
            Dim r = Await ToolKit.RunProcessAsync("powershell.exe", {"-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script & " | ConvertTo-Json -Depth 4 -Compress"},
                                                  Nothing, TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
            If r.TimedOut Then Throw New McpException("PowerShell did not answer within 60 s.")
            If r.ExitCode <> 0 OrElse String.IsNullOrWhiteSpace(r.Output) Then Throw New McpException("PowerShell failed: " & ToolKit.Clip(If(r.Error, "").Trim(), 500))
            Return JsonNode.Parse(r.Output)
        End Function

        Private Shared Function S(n As JsonNode) As String
            If n Is Nothing Then Return ""
            Try
                Return n.GetValue(Of String)()
            Catch ex As InvalidOperationException
                Return n.ToJsonString()
            End Try
        End Function

        Private Shared Function D(n As JsonNode) As Double
            Dim v As Double
            Return If(Double.TryParse(S(n), NumberStyles.Float, Inv, v), v, 0)
        End Function

        ' ------------------------------------------------------------------------------------------ overview

        Friend Async Function OverviewAsync(ct As CancellationToken) As Task(Of String)
            RequireWindows()
            Dim os = Await PowerShellJsonAsync("$o = Get-CimInstance Win32_OperatingSystem; $c = Get-CimInstance Win32_Processor; [pscustomobject]@{ Caption = $o.Caption; Version = $o.Version; " &
                                               "Boot = $o.LastBootUpTime.ToUniversalTime().ToString('o'); FreeKb = $o.FreePhysicalMemory; TotalKb = $o.TotalVisibleMemorySize; " &
                                               "Load = ($c | Measure-Object -Property LoadPercentage -Average).Average; Cpu = ($c | Select-Object -First 1).Name; Cores = ($c | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum }", ct).ConfigureAwait(False)
            Dim sb As New StringBuilder()
            Dim boot As DateTimeOffset
            Dim up = If(DateTimeOffset.TryParse(S(os("Boot")), Inv, DateTimeStyles.AssumeUniversal, boot), DateTimeOffset.UtcNow - boot, TimeSpan.Zero)
            sb.AppendLine($"{S(os("Caption"))} {S(os("Version"))} on {Environment.MachineName}; up {CInt(Math.Floor(up.TotalDays))}d {up.Hours}h {up.Minutes}m")
            sb.AppendLine($"CPU: {S(os("Cpu")).Trim()} ({S(os("Cores"))} logical), load {D(os("Load")):0}%")
            Dim total = D(os("TotalKb")) * 1024, free = D(os("FreeKb")) * 1024
            sb.AppendLine($"Memory: {DockerServer.Size(CLng(total - free))} used of {DockerServer.Size(CLng(total))} ({If(total > 0, 100 * (total - free) / total, 0):0}%)")
            For Each drive In DriveInfo.GetDrives().Where(Function(x) x.IsReady AndAlso x.DriveType = DriveType.Fixed)
                Dim pctFree = 100.0 * drive.AvailableFreeSpace / Math.Max(1L, drive.TotalSize)
                sb.AppendLine($"Disk {drive.Name} {DockerServer.Size(drive.AvailableFreeSpace)} free of {DockerServer.Size(drive.TotalSize)} ({pctFree:0}% free){If(pctFree < 10, "  LOW", "")}")
            Next
            sb.AppendLine("Busiest processes:")
            sb.Append(Await ProcessesAsync("cpu", "", 8, ct).ConfigureAwait(False))
            Return sb.ToString()
        End Function

        ' ------------------------------------------------------------------------------------------ services

        Friend Function Services(<Description("Part of the service name or display name (optional)")> Optional filter As String = "",
                                 <Description("running, stopped or all (default all)")> Optional state As String = "all") As String
            RequireWindows()
            Dim st = If(state, "all").Trim().ToLowerInvariant()
            Dim rows As New List(Of List(Of String)) From {New List(Of String) From {"name", "display name", "status", "start"}}
            For Each sc In ServiceController.GetServices().OrderBy(Function(x) x.ServiceName)
                Using sc
                    If Not String.IsNullOrWhiteSpace(filter) AndAlso Not sc.ServiceName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) AndAlso
                       Not sc.DisplayName.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) Then Continue For
                    If st = "running" AndAlso sc.Status <> ServiceControllerStatus.Running Then Continue For
                    If st = "stopped" AndAlso sc.Status <> ServiceControllerStatus.Stopped Then Continue For
                    Dim start As String
                    Try
                        start = sc.StartType.ToString()
                    Catch ex As InvalidOperationException
                        start = "?"
                    End Try
                    rows.Add(New List(Of String) From {sc.ServiceName, sc.DisplayName, sc.Status.ToString(), start})
                End Using
            Next
            Return If(rows.Count = 1, "No matching services.", $"{rows.Count - 1} service(s):{vbLf}" & TableText.ToMarkdown(rows, 400))
        End Function

        Friend Async Function ControlServiceAsync(<Description("Service name")> name As String,
                                                  <Description("start, stop or restart")> action As String,
                                                  Optional ct As CancellationToken = Nothing) As Task(Of String)
            RequireWindows()
            Dim act = If(action, "").Trim().ToLowerInvariant()
            If act <> "start" AndAlso act <> "stop" AndAlso act <> "restart" Then Throw New McpException("action must be start, stop or restart.")
            Using sc As New ServiceController(If(name, "").Trim())
                Try
                    Dim unused = sc.Status
                Catch ex As InvalidOperationException
                    Throw New McpException($"No service named '{name}'.")
                End Try
                Dim waitFor = Async Function(target As ServiceControllerStatus) As Task
                                  Dim deadline = DateTime.UtcNow.AddSeconds(60)
                                  sc.Refresh()
                                  While sc.Status <> target AndAlso DateTime.UtcNow < deadline
                                      Await Task.Delay(500, ct).ConfigureAwait(False)
                                      sc.Refresh()
                                  End While
                              End Function
                Try
                    If (act = "stop" OrElse act = "restart") AndAlso sc.Status <> ServiceControllerStatus.Stopped Then
                        sc.Stop()
                        Await waitFor(ServiceControllerStatus.Stopped).ConfigureAwait(False)
                    End If
                    If (act = "start" OrElse act = "restart") AndAlso sc.Status <> ServiceControllerStatus.Running Then
                        sc.Start()
                        Await waitFor(ServiceControllerStatus.Running).ConfigureAwait(False)
                    End If
                Catch ex As InvalidOperationException
                    Throw New McpException($"Could not {act} {sc.ServiceName}: {If(ex.InnerException?.Message, ex.Message)}")
                End Try
                sc.Refresh()
                Return $"{sc.ServiceName} is now {sc.Status}."
            End Using
        End Function

        ' ------------------------------------------------------------------------------------------ event logs

        Friend Async Function EventsAsync(<Description("Log: Application (default), System, or another log name")> Optional log As String = "Application",
                                          <Description("error (default; includes critical), warning (and worse) or all")> Optional level As String = "error",
                                          <Description("From the last hours (default 24, at most 720)")> Optional hours As Integer = 24,
                                          <Description("Most entries (default 50, at most 300)")> Optional max_results As Integer = 50,
                                          <Description("Only this source/provider (optional)")> Optional source As String = "",
                                          Optional ct As CancellationToken = Nothing) As Task(Of String)
            RequireWindows()
            Dim logName = If(String.IsNullOrWhiteSpace(log), "Application", log.Trim())
            If Not Regex.IsMatch(logName, "^[A-Za-z0-9 \-/_.]+$") Then Throw New McpException($"'{log}' is not a log name.")
            Dim levels = Select_Level(If(level, "error").Trim().ToLowerInvariant())
            Dim ms = CLng(Math.Clamp(If(hours <= 0, 24, hours), 1, 720)) * 3600000L
            Dim cond As New List(Of String) From {$"TimeCreated[timediff(@SystemTime) &lt;= {ms}]"}
            If levels IsNot Nothing Then cond.Add("(" & String.Join(" or ", levels.Select(Function(l) $"Level={l}")) & ")")
            If Not String.IsNullOrWhiteSpace(source) Then
                If Not Regex.IsMatch(source.Trim(), "^[A-Za-z0-9 \-_.]+$") Then Throw New McpException($"'{source}' is not a source name.")
                cond.Add($"Provider[@Name='{source.Trim()}']")
            End If
            Dim query = "*[System[" & String.Join(" and ", cond) & "]]"
            Dim r = Await ToolKit.RunProcessAsync("wevtutil.exe", {"qe", logName, "/q:" & query.Replace("&lt;", "<"), $"/c:{Math.Clamp(If(max_results <= 0, 50, max_results), 1, 300)}", "/rd:true", "/f:text"},
                                                  Nothing, TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
            If r.ExitCode <> 0 Then Throw New McpException("wevtutil failed: " & ToolKit.Clip((r.Error & r.Output).Trim(), 400))
            Dim events = Regex.Split(r.Output, "(?m)^Event\[\d+\]:\s*$").Where(Function(e) e.Trim().Length > 0).ToList()
            If events.Count = 0 Then Return $"No {If(level, "error")} events in {logName} in the last {Math.Clamp(If(hours <= 0, 24, hours), 1, 720)} h."
            Dim sb As New StringBuilder($"{events.Count} event(s) in {logName}, newest first:" & vbLf)
            For Each ev In events
                Dim field = Function(name As String) Regex.Match(ev, $"(?m)^\s*{name}:\s*(.*)$").Groups(1).Value.Trim()
                Dim desc = Regex.Match(ev, "(?s)Description:\s*(.*)$").Groups(1).Value.Trim()
                sb.AppendLine($"- {field("Date")} {field("Level")} {field("Source")} (id {field("Event ID")}): {ToolKit.Clip(Regex.Replace(desc, "\s+", " "), 400)}")
            Next
            Return ToolKit.Clip(sb.ToString().TrimEnd(), 60000)
        End Function

        Private Shared Function Select_Level(level As String) As Integer()
            Select Case level
                Case "all" : Return Nothing
                Case "warning", "warn" : Return {1, 2, 3}
                Case "info", "information" : Return {4, 0}
                Case Else : Return {1, 2}
            End Select
        End Function

        ' ------------------------------------------------------------------------------------------ processes

        Friend Async Function ProcessesAsync(<Description("cpu (default) or memory")> Optional sort As String = "cpu",
                                             <Description("Only names containing this (optional)")> Optional filter As String = "",
                                             <Description("How many (default 25, at most 200)")> Optional top As Integer = 25,
                                             Optional ct As CancellationToken = Nothing) As Task(Of String)
            Dim first = Snapshot()
            Await Task.Delay(1000, ct).ConfigureAwait(False)
            Dim second = Snapshot()
            Dim cores = Environment.ProcessorCount
            Dim rows = second.Select(Function(kv)
                                         Dim before As (Name As String, Cpu As TimeSpan, Mem As Long, Started As DateTime?) = Nothing
                                         Dim cpu = If(first.TryGetValue(kv.Key, before), (kv.Value.Cpu - before.Cpu).TotalMilliseconds / 10.0 / cores, 0.0)
                                         Return (Pid:=kv.Key, kv.Value.Name, Cpu:=Math.Max(0, cpu), kv.Value.Mem, kv.Value.Started)
                                     End Function).
                Where(Function(p) String.IsNullOrWhiteSpace(filter) OrElse p.Name.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase))
            rows = If(String.Equals(sort, "memory", StringComparison.OrdinalIgnoreCase), rows.OrderByDescending(Function(p) p.Mem), rows.OrderByDescending(Function(p) p.Cpu).ThenByDescending(Function(p) p.Mem))
            Dim table As New List(Of List(Of String)) From {New List(Of String) From {"pid", "name", "cpu %", "memory", "started"}}
            For Each p In rows.Take(Math.Clamp(If(top <= 0, 25, top), 1, 200))
                table.Add(New List(Of String) From {p.Pid.ToString(Inv), p.Name, p.Cpu.ToString("0.0", Inv), DockerServer.Size(p.Mem), If(p.Started.HasValue, p.Started.Value.ToString("yyyy-MM-dd HH:mm", Inv), "")})
            Next
            Return TableText.ToMarkdown(table, 200)
        End Function

        Private Shared Function Snapshot() As Dictionary(Of Integer, (Name As String, Cpu As TimeSpan, Mem As Long, Started As DateTime?))
            Dim map As New Dictionary(Of Integer, (Name As String, Cpu As TimeSpan, Mem As Long, Started As DateTime?))
            For Each p In Process.GetProcesses()
                Using p
                    Try
                        Dim started As DateTime? = Nothing
                        Try
                            started = p.StartTime
                        Catch ex As Exception When TypeOf ex Is Win32Exception OrElse TypeOf ex Is InvalidOperationException
                        End Try
                        Dim cpu = TimeSpan.Zero
                        Try
                            cpu = p.TotalProcessorTime
                        Catch ex As Exception When TypeOf ex Is Win32Exception OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is NotSupportedException
                        End Try
                        map(p.Id) = (p.ProcessName, cpu, p.WorkingSet64, started)
                    Catch ex As InvalidOperationException
                    End Try
                End Using
            Next
            Return map
        End Function

        Friend Function KillProcess(<Description("Process id (or 0 when using name)")> Optional pid As Integer = 0,
                                    <Description("Process name (without .exe): ends every process with it")> Optional name As String = "") As String
            Dim targets As New List(Of Process)
            If pid > 0 Then
                Try
                    targets.Add(Process.GetProcessById(pid))
                Catch ex As ArgumentException
                    Throw New McpException($"No process with PID {pid}.")
                End Try
            ElseIf Not String.IsNullOrWhiteSpace(name) Then
                targets.AddRange(Process.GetProcessesByName(name.Trim().Replace(".exe", "", StringComparison.OrdinalIgnoreCase)))
                If targets.Count = 0 Then Throw New McpException($"No process named '{name}'.")
            Else
                Throw New McpException("pid or name is required.")
            End If
            Dim done As New List(Of String)
            For Each p In targets
                Using p
                    If _protected.Contains(p.ProcessName) OrElse p.Id = Environment.ProcessId OrElse p.Id <= 4 Then Throw New McpException($"{p.ProcessName} ({p.Id}) is protected and cannot be ended here.")
                    Try
                        p.Kill(entireProcessTree:=True)
                        p.WaitForExit(5000)
                        done.Add($"{p.ProcessName} ({p.Id})")
                    Catch ex As Exception When TypeOf ex Is Win32Exception OrElse TypeOf ex Is InvalidOperationException
                        Throw New McpException($"Could not end {p.ProcessName} ({p.Id}): {ex.Message}")
                    End Try
                End Using
            Next
            Return "Ended " & String.Join(", ", done) & "."
        End Function

        ' ------------------------------------------------------------------------------------------ scheduled tasks, connections

        Friend Async Function TasksAsync(<Description("Part of the task name (optional)")> Optional filter As String = "",
                                         <Description("Include Microsoft's own tasks")> Optional include_system As Boolean = False,
                                         Optional ct As CancellationToken = Nothing) As Task(Of String)
            RequireWindows()
            Dim r = Await ToolKit.RunProcessAsync("schtasks.exe", {"/query", "/fo", "csv", "/v"}, Nothing, TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
            If r.ExitCode <> 0 Then Throw New McpException("schtasks failed: " & ToolKit.Clip(r.Error.Trim(), 300))
            Dim rows = TableText.ParseCsv(r.Output)
            If rows.Count = 0 Then Return "No scheduled tasks."
            Dim header = rows(0)
            Dim col = Function(n As String) header.FindIndex(Function(h) h.Equals(n, StringComparison.OrdinalIgnoreCase))
            Dim iName = col("TaskName"), iStatus = col("Status"), iNext = col("Next Run Time"), iLast = col("Last Run Time"), iResult = col("Last Result"), iRun = col("Task To Run"), iUser = col("Run As User")
            Dim table As New List(Of List(Of String)) From {New List(Of String) From {"task", "status", "last run", "result", "next run", "runs", "as"}}
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim cell = Function(r2 As List(Of String), i As Integer) If(i >= 0 AndAlso i < r2.Count, r2(i), "")
            For Each row In rows.Skip(1)
                Dim nm = cell(row, iName)
                If nm = "" OrElse nm = "TaskName" OrElse Not seen.Add(nm) Then Continue For
                If Not include_system AndAlso nm.StartsWith("\Microsoft\", StringComparison.OrdinalIgnoreCase) Then Continue For
                If Not String.IsNullOrWhiteSpace(filter) AndAlso Not nm.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim result = cell(row, iResult)
                Dim code As Long
                If Long.TryParse(result, code) AndAlso code <> 0 Then result &= $" (0x{code:X})"
                table.Add(New List(Of String) From {nm, cell(row, iStatus), cell(row, iLast), result, cell(row, iNext), ToolKit.Clip(cell(row, iRun), 120), cell(row, iUser)})
            Next
            Return If(table.Count = 1, "No matching scheduled tasks.", $"{table.Count - 1} task(s):{vbLf}" & TableText.ToMarkdown(table, 300))
        End Function

        Friend Async Function ConnectionsAsync(<Description("Only listening ports (default true)")> Optional listening_only As Boolean = True,
                                               <Description("Only this local or remote port (optional, 0 = all)")> Optional port As Integer = 0,
                                               Optional ct As CancellationToken = Nothing) As Task(Of String)
            RequireWindows()
            Dim r = Await ToolKit.RunProcessAsync("netstat.exe", {"-ano"}, Nothing, TimeSpan.FromSeconds(60), ct).ConfigureAwait(False)
            Dim names As New Dictionary(Of Integer, String)
            For Each p In Process.GetProcesses()
                Using p
                    names(p.Id) = p.ProcessName
                End Using
            Next
            Dim table As New List(Of List(Of String)) From {New List(Of String) From {"proto", "local", "remote", "state", "pid", "process"}}
            For Each line In r.Output.Split(ControlChars.Lf)
                Dim m = Regex.Match(line.Trim(), "^(TCP|UDP)\s+(\S+)\s+(\S+)\s+(?:(\S+)\s+)?(\d+)$")
                If Not m.Success Then Continue For
                Dim state = If(m.Groups(4).Success, m.Groups(4).Value, "")
                Dim proto = m.Groups(1).Value
                If listening_only AndAlso Not (state = "LISTENING" OrElse (proto = "UDP" AndAlso m.Groups(3).Value.StartsWith("*"))) Then Continue For
                If port > 0 AndAlso Not (m.Groups(2).Value.EndsWith(":" & port.ToString(Inv)) OrElse m.Groups(3).Value.EndsWith(":" & port.ToString(Inv))) Then Continue For
                Dim pidValue = Integer.Parse(m.Groups(5).Value, Inv)
                Dim pname As String = Nothing
                table.Add(New List(Of String) From {proto, m.Groups(2).Value, m.Groups(3).Value, state, pidValue.ToString(Inv), If(names.TryGetValue(pidValue, pname), pname, "")})
            Next
            Return If(table.Count = 1, "No matching connections.", $"{table.Count - 1} entr{If(table.Count = 2, "y", "ies")}:{vbLf}" & TableText.ToMarkdown(table, 500))
        End Function
    End Class

End Namespace
