Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Security
Imports Agentic.Tools
Imports Agentic.Utilities

Namespace Mcp

    Public Enum McpConnectionState
        Disabled
        Disconnected
        Connecting
        Connected
        Faulted
    End Enum

    Public Class McpServerStatus
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property Transport As String
        Public Property Enabled As Boolean
        Public Property State As McpConnectionState
        Public Property LastError As String
        Public Property LastConnectedUtc As DateTime?
        Public Property ToolCount As Integer
        Public Property Tools As List(Of String)
        Public Property Calls As Long
        Public Property Failures As Long
        Public Property ReconnectAttempts As Integer
        ''' <summary>Set while the watchdog finds it not responding (its calls fail at once).</summary>
        Public Property DownSinceUtc As DateTime?
    End Class

    Public Interface IMcpServerManager
        Function StartAsync(ct As CancellationToken) As Task
        Function StopAsync() As Task
        Function GetStatus() As List(Of McpServerStatus)
        Function GetTools(serverId As String) As List(Of McpToolInfo)
        Function CallToolAsync(serverId As String, toolName As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult)
        Function GetServerOptions(serverId As String) As McpServerOptions
        Sub RequestReconnect(serverId As String)
    End Interface

    Public Class McpUnavailableException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' Owns one connection per configured MCP server with an independent lifecycle:
    ''' Disconnected → Connecting → Connected ↔ Faulted (backoff reconnect). A failing server never affects
    ''' other servers or agents; its tools simply disappear from agents' tool lists until it recovers.
    ''' </summary>
    Public NotInheritable Class McpServerManager
        Implements IMcpServerManager

        Private Class Connection
            Public Options As McpServerOptions
            Public Client As IMcpClient
            Public State As McpConnectionState
            Public LastError As String
            Public LastConnectedUtc As DateTime?
            Public Tools As New List(Of McpToolInfo)
            Public ToolsRefreshedUtc As DateTime
            Public Gate As SemaphoreSlim
            Public Attempts As Integer
            Public Calls As Long
            Public Failures As Long
            Public ReconnectSignal As New AsyncSignal()
            Public ReadOnly SyncRoot As New Object()
            ''' <summary>Stops this server's loops alone (a server removed or replaced at run time).</summary>
            Public Cts As CancellationTokenSource
            ' Watchdog state.
            Public HealthSignal As New AsyncSignal()
            Public HealthFailures As Integer
            Public ConsecutiveTimeouts As Integer
            Public DownSinceUtc As DateTime?
            Public RestartsTried As Integer
            ''' <summary>The last fault reported, and when; Cycling: the same fault came back within FaultRepeatQuiet and was not reported.</summary>
            Public LastFaultText As String
            Public LastFaultUtc As DateTime
            Public Cycling As Boolean
        End Class

        ''' <summary>
        ''' A server that keeps faulting with the same error is reported once per this long, not on every reconnect (code-exec's
        ''' "Errno 24 Too many open files" was logged 1751 times on 2026-11-02, burying everything else).
        ''' </summary>
        Friend Shared FaultRepeatQuiet As TimeSpan = TimeSpan.FromMinutes(30)

        ''' <summary>
        ''' Whether this state change is reported (log and activity feed): a new fault is, the same fault again within
        ''' FaultRepeatQuiet is not, and neither are the reconnects of a server cycling through that fault.
        ''' </summary>
        Private Shared Function ShouldReport(c As Connection, state As McpConnectionState, errorMessage As String, now As DateTime) As Boolean
            SyncLock c.SyncRoot
                If state = McpConnectionState.Faulted Then
                    Dim text = If(errorMessage, "")
                    c.Cycling = String.Equals(c.LastFaultText, text, StringComparison.Ordinal) AndAlso now - c.LastFaultUtc < FaultRepeatQuiet
                    If c.Cycling Then Return False
                    c.LastFaultText = text
                    c.LastFaultUtc = now
                    Return True
                End If
                ' Back up after a quiet stretch of the same fault: said once, and the next fault is reported again.
                If c.Cycling AndAlso now - c.LastFaultUtc < FaultRepeatQuiet Then Return False
                c.Cycling = False
                Return True
            End SyncLock
        End Function

        Private ReadOnly _connections As New ConcurrentDictionary(Of String, Connection)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _factory As IMcpClientFactory
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _global As SemaphoreSlim
        Private ReadOnly _loops As New List(Of Task)
        Private _cts As CancellationTokenSource

        Public Sub New(servers As IEnumerable(Of McpServerOptions), factory As IMcpClientFactory, events As IAgentEventBus, log As ILog, limits As LimitsOptions)
            _factory = factory
            _events = events
            _log = log
            _global = New SemaphoreSlim(Math.Max(1, limits.MaxConcurrentMcpCalls))
            For Each s In servers
                _connections(s.Id) = New Connection With {
                    .Options = s, .State = If(s.Enabled, McpConnectionState.Disconnected, McpConnectionState.Disabled),
                    .Gate = New SemaphoreSlim(Math.Max(1, s.MaxConcurrency))}
            Next
        End Sub

        Public Function StartAsync(ct As CancellationToken) As Task Implements IMcpServerManager.StartAsync
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            For Each c In _connections.Values
                StartLoops(c)
            Next
            Return Task.CompletedTask
        End Function

        Private Sub StartLoops(c As Connection)
            c.Cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
            SyncLock _loops
                If c.Options.Enabled Then _loops.Add(ConnectionLoopAsync(c, c.Cts.Token))
                If c.Options.Enabled AndAlso c.Options.HealthCheck IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(c.Options.HealthCheck.Tool) Then
                    _loops.Add(WatchdogLoopAsync(c, c.Cts.Token))
                End If
            End SyncLock
        End Sub

        ''' <summary>
        ''' Adds a server while running, or replaces the one with the same id (its connection is closed and made again): servers
        ''' added on the MCP page and users' own servers.
        ''' </summary>
        Public Sub AddOrReplace(options As McpServerOptions)
            RemoveServer(options.Id)
            Dim c As New Connection With {
                .Options = options, .State = If(options.Enabled, McpConnectionState.Disconnected, McpConnectionState.Disabled),
                .Gate = New SemaphoreSlim(Math.Max(1, options.MaxConcurrency))}
            _connections(options.Id) = c
            If _cts IsNot Nothing Then StartLoops(c)
        End Sub

        ''' <summary>Stops and forgets a server added at run time (or any server, until the next start).</summary>
        Public Function RemoveServer(serverId As String) As Boolean
            Dim c As Connection = Nothing
            If Not _connections.TryRemove(serverId, c) Then Return False
            Try
                c.Cts?.Cancel()
            Catch ex As ObjectDisposedException
            End Try
            Dim client = c.Client
            c.Client = Nothing
            If client IsNot Nothing Then
                Dim unused = SafeDisposeAsync(client)
            End If
            Return True
        End Function

        ''' <summary>Every server's options as they are now (appsettings.json and those added at run time).</summary>
        Public Function ServerOptions() As List(Of McpServerOptions)
            Return _connections.Values.Select(Function(c) c.Options).OrderBy(Function(o) o.Id, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Waits (bounded) for enabled servers to finish their first connection attempt.</summary>
        Public Async Function WaitForInitialConnectAsync(timeout As TimeSpan) As Task
            Dim sw = Stopwatch.StartNew()
            While sw.Elapsed < timeout
                If _connections.Values.All(Function(c) c.State <> McpConnectionState.Connecting AndAlso c.State <> McpConnectionState.Disconnected) Then Return
                Await Task.Delay(100).ConfigureAwait(False)
            End While
        End Function

        Private Sub SetState(c As Connection, state As McpConnectionState, errorMessage As String)
            Dim changed As Boolean
            SyncLock c.SyncRoot
                changed = c.State <> state
                c.State = state
                If errorMessage IsNot Nothing Then c.LastError = errorMessage
                If state = McpConnectionState.Connected Then
                    c.LastConnectedUtc = DateTime.UtcNow
                    c.LastError = Nothing
                End If
            End SyncLock
            If changed AndAlso ShouldReport(c, state, errorMessage, DateTime.UtcNow) Then
                Dim lvl = If(state = McpConnectionState.Faulted, EventLevel.Warning, EventLevel.Info)
                _events.Emit(AgentEventType.McpServerStateChanged, $"MCP server {c.Options.Id}: {state}" & If(errorMessage Is Nothing, "", $" ({TextUtil.Truncate(errorMessage, 160)})"),
                             level:=lvl, data:=New Dictionary(Of String, Object) From {{"serverId", c.Options.Id}, {"state", state.ToString()}})
                If state = McpConnectionState.Faulted Then
                    _log.Warn("MCP", $"Server {c.Options.Id} faulted: {errorMessage}")
                Else
                    _log.Info("MCP", $"Server {c.Options.Id} {state}")
                End If
            End If
        End Sub

        Private Async Function ConnectionLoopAsync(c As Connection, ct As CancellationToken) As Task
            Dim opt = c.Options
            While Not ct.IsCancellationRequested
                If c.State <> McpConnectionState.Connected Then
                    SetState(c, McpConnectionState.Connecting, Nothing)
                    Dim client As IMcpClient = Nothing
                    Dim failure As Exception = Nothing
                    Try
                        client = _factory.Create(opt)
                        Using connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                            connectCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, opt.ConnectTimeoutSeconds)))
                            Await client.ConnectAsync(connectCts.Token).ConfigureAwait(False)
                            Dim tools = Await client.ListToolsAsync(connectCts.Token).ConfigureAwait(False)
                            Dim old = c.Client
                            c.Client = client
                            c.Tools = FilterTools(opt, tools)
                            c.ToolsRefreshedUtc = DateTime.UtcNow
                            c.Attempts = 0
                            If old IsNot Nothing Then Await SafeDisposeAsync(old).ConfigureAwait(False)
                        End Using
                        SetState(c, McpConnectionState.Connected, Nothing)
                    Catch ex As OperationCanceledException When ct.IsCancellationRequested
                        Exit While
                    Catch ex As Exception
                        failure = ex
                    End Try
                    If failure IsNot Nothing Then
                        If client IsNot Nothing Then Await SafeDisposeAsync(client).ConfigureAwait(False)
                        c.Attempts += 1
                        SetState(c, McpConnectionState.Faulted, failure.Message)
                        Dim delay = Backoff.Compute(c.Attempts - 1, TimeSpan.FromSeconds(opt.ReconnectBaseSeconds), TimeSpan.FromSeconds(opt.ReconnectMaxSeconds))
                        Try
                            Await c.ReconnectSignal.WaitAsync(delay, ct).ConfigureAwait(False)
                        Catch ex As OperationCanceledException
                            Exit While
                        End Try
                        Continue While
                    End If
                End If

                ' Connected: periodically refresh the tool list (also serves as a health check).
                Try
                    Await c.ReconnectSignal.WaitAsync(TimeSpan.FromMinutes(Math.Max(1, opt.ToolRefreshMinutes)), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                If c.State <> McpConnectionState.Connected Then Continue While
                Dim refreshError As Exception = Nothing
                Try
                    Using refreshCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        refreshCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, opt.TimeoutSeconds)))
                        Dim tools = Await c.Client.ListToolsAsync(refreshCts.Token).ConfigureAwait(False)
                        c.Tools = FilterTools(opt, tools)
                        c.ToolsRefreshedUtc = DateTime.UtcNow
                    End Using
                Catch ex As OperationCanceledException When ct.IsCancellationRequested
                    Exit While
                Catch ex As Exception
                    refreshError = ex
                End Try
                If refreshError IsNot Nothing Then MarkFaulted(c, "Tool refresh failed: " & refreshError.Message)
            End While
            If c.Client IsNot Nothing Then Await SafeDisposeAsync(c.Client).ConfigureAwait(False)
            c.Client = Nothing
        End Function

        ''' <summary>
        ''' The watchdog: checks the server with its HealthCheck tool every IntervalSeconds (and at once after a run of call
        ''' timeouts). After FailuresBeforeRestart failures it is "not responding": calls fail fast, the connection is reset
        ''' (a new session), and when that did not help RestartCommand runs. One passing check ends it.
        ''' </summary>
        Private Async Function WatchdogLoopAsync(c As Connection, ct As CancellationToken) As Task
            Dim hc = c.Options.HealthCheck
            While Not ct.IsCancellationRequested
                Dim wait = TimeSpan.FromSeconds(If(c.DownSinceUtc.HasValue, DownCheckSeconds(hc, c.RestartsTried), Math.Max(1, hc.IntervalSeconds)))
                Try
                    Await c.HealthSignal.WaitAsync(wait, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                If c.State <> McpConnectionState.Connected OrElse c.Client Is Nothing Then Continue While
                Dim problem = Await CheckHealthAsync(c, ct).ConfigureAwait(False)
                If ct.IsCancellationRequested Then Exit While
                If problem Is Nothing Then
                    c.HealthFailures = 0
                    Interlocked.Exchange(c.ConsecutiveTimeouts, 0)
                    If c.DownSinceUtc.HasValue Then
                        Dim downFor = DateTime.UtcNow - c.DownSinceUtc.Value
                        c.DownSinceUtc = Nothing
                        c.RestartsTried = 0
                        _events.Emit(AgentEventType.McpServerStateChanged, $"MCP server {c.Options.Id} is responding again (after {downFor.TotalMinutes:0} min)", level:=EventLevel.Info,
                                     data:=New Dictionary(Of String, Object) From {{"serverId", c.Options.Id}, {"state", "Responding"}})
                        _log.Info("MCP", $"Server {c.Options.Id} responds again after {downFor.TotalMinutes:0} min")
                    End If
                    Continue While
                End If
                c.HealthFailures += 1
                If c.HealthFailures < Math.Max(1, hc.FailuresBeforeRestart) Then
                    c.HealthSignal.Set() ' check again soon
                    Await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, Math.Max(1, hc.DownIntervalSeconds))), ct).ConfigureAwait(False)
                    Continue While
                End If
                If Not c.DownSinceUtc.HasValue Then
                    c.DownSinceUtc = DateTime.UtcNow
                    _events.Emit(AgentEventType.McpServerStateChanged, $"MCP server {c.Options.Id} is not responding ({problem}); resetting it. Calls to it fail at once until it answers.",
                                 level:=EventLevel.Warning, data:=New Dictionary(Of String, Object) From {{"serverId", c.Options.Id}, {"state", "NotResponding"}})
                    _log.Warn("MCP", $"Server {c.Options.Id} is not responding: {problem}")
                    If Text.RegularExpressions.Regex.IsMatch(If(problem, ""), "out of pty devices|No such file or directory: '/bin|/bin/(ba)?sh: not found|exec format error|Read-only file system", Text.RegularExpressions.RegexOptions.IgnoreCase) Then
                        _log.Err("MCP", $"Server {c.Options.Id} looks damaged ({problem}): its container has to be recreated from its image (docker compose up -d --force-recreate); a restart does not bring deleted files back.")
                    End If
                End If
                ' First a new connection (session); when that did not help, the restart command.
                c.RestartsTried += 1
                If c.RestartsTried = 1 OrElse String.IsNullOrWhiteSpace(hc.RestartCommand) Then
                    MarkFaulted(c, "Watchdog: " & problem)
                Else
                    Await RunRestartCommandAsync(c, ct).ConfigureAwait(False)
                    MarkFaulted(c, "Watchdog: restarted after " & problem)
                End If
            End While
        End Function

        ''' <summary>
        ''' Seconds to the next check while a server is not responding: DownIntervalSeconds, doubling with each failed reset up to
        ''' DownMaxIntervalSeconds. Every check of a sandbox may start a process there, so a long outage must not pile them up.
        ''' </summary>
        Friend Shared Function DownCheckSeconds(hc As McpHealthCheckOptions, resetsTried As Integer) As Integer
            Dim first = Math.Max(1, hc.DownIntervalSeconds)
            Dim most = Math.Max(first, hc.DownMaxIntervalSeconds)
            Dim n = Math.Min(Math.Max(0, resetsTried - 1), 20)
            Return CInt(Math.Min(CLng(most), CLng(first) << n))
        End Function

        ''' <summary>
        ''' The process a check started when it is still running ({"id": …, "status": "running"} from Open Terminal's run_command):
        ''' its id, else Nothing.
        ''' </summary>
        Friend Shared Function RunningProcessId(text As String) As String
            If String.IsNullOrWhiteSpace(text) OrElse Not text.TrimStart().StartsWith("{") Then Return Nothing
            Try
                Dim node = TryCast(JsonNode.Parse(text), JsonObject)
                Dim status = node?("status")?.ToString()
                Dim id = node?("id")?.ToString()
                If String.Equals(status, "running", StringComparison.OrdinalIgnoreCase) AndAlso Not String.IsNullOrWhiteSpace(id) Then Return id
            Catch ex As JsonException
            End Try
            Return Nothing
        End Function

        ''' <summary>Runs the health check tool directly (not behind the server's call slots). Nothing = healthy, else the problem.</summary>
        Private Async Function CheckHealthAsync(c As Connection, ct As CancellationToken) As Task(Of String)
            Dim hc = c.Options.HealthCheck
            Dim args As JsonElement
            Dim obj As New JsonObject()
            For Each kv In If(hc.Arguments, New Dictionary(Of String, String))
                If Not String.IsNullOrWhiteSpace(kv.Key) AndAlso kv.Value IsNot Nothing Then obj(kv.Key) = ConfigValue(kv.Value)
            Next
            Using doc = JsonDocument.Parse(obj.ToJsonString())
                args = doc.RootElement.Clone()
            End Using
            Using checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                checkCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, hc.TimeoutSeconds)))
                Try
                    Dim r = Await c.Client.CallToolAsync(hc.Tool, args, checkCts.Token).ConfigureAwait(False)
                    Dim text = If(r?.Text, "")
                    If r IsNot Nothing AndAlso r.IsError Then Return "the check returned an error: " & TextUtil.Truncate(text, 160)
                    If Not String.IsNullOrEmpty(hc.Expect) AndAlso text.IndexOf(hc.Expect, StringComparison.OrdinalIgnoreCase) < 0 Then
                        ' A check command that never finished is itself a stuck process: stop it, so checks do not pile up there.
                        Dim stuck = RunningProcessId(text)
                        If stuck IsNot Nothing Then
                            Await StopCheckProcessAsync(c, stuck, ct).ConfigureAwait(False)
                            Return "the check command did not finish: commands are not completing on the server (stuck processes, or it ran out of files or processes)"
                        End If
                        Return "the check did not return """ & hc.Expect & """"
                    End If
                    Return Nothing
                Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                    Return $"{hc.Tool} did not answer within {hc.TimeoutSeconds}s"
                Catch ex As OperationCanceledException
                    Return Nothing
                Catch ex As Exception
                    Return $"{hc.Tool} failed: {TextUtil.Truncate(ex.Message, 160)}"
                End Try
            End Using
        End Function

        ''' <summary>Stops a check's process that is still running, with HealthCheck.StopTool (best effort, 15 s at most).</summary>
        Private Async Function StopCheckProcessAsync(c As Connection, processId As String, ct As CancellationToken) As Task
            Dim tool = c.Options.HealthCheck?.StopTool
            If String.IsNullOrWhiteSpace(tool) OrElse c.Client Is Nothing Then Return
            Using stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                stopCts.CancelAfter(TimeSpan.FromSeconds(15))
                Try
                    Using doc = JsonDocument.Parse(New JsonObject From {{"process_id", processId}, {"force", True}}.ToJsonString())
                        Await c.Client.CallToolAsync(tool, doc.RootElement.Clone(), stopCts.Token).ConfigureAwait(False)
                    End Using
                Catch ex As Exception When Not ct.IsCancellationRequested
                End Try
            End Using
        End Function

        ''' <summary>Runs HealthCheck.RestartCommand on this host (cmd.exe /c on Windows, /bin/sh -c elsewhere), bounded in time.</summary>
        Private Async Function RunRestartCommandAsync(c As Connection, ct As CancellationToken) As Task
            Dim hc = c.Options.HealthCheck
            Dim psi As New ProcessStartInfo With {.UseShellExecute = False, .RedirectStandardOutput = True, .RedirectStandardError = True, .CreateNoWindow = True}
            If OperatingSystem.IsWindows() Then
                psi.FileName = "cmd.exe" : psi.ArgumentList.Add("/c") : psi.ArgumentList.Add(hc.RestartCommand)
            Else
                psi.FileName = "/bin/sh" : psi.ArgumentList.Add("-c") : psi.ArgumentList.Add(hc.RestartCommand)
            End If
            _log.Warn("MCP", $"Server {c.Options.Id}: running its restart command")
            Try
                Using p = Process.Start(psi)
                    Dim output = p.StandardOutput.ReadToEndAsync()
                    Dim errors = p.StandardError.ReadToEndAsync()
                    Using waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        waitCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, hc.RestartCommandTimeoutSeconds)))
                        Try
                            Await p.WaitForExitAsync(waitCts.Token).ConfigureAwait(False)
                        Catch ex As OperationCanceledException
                            Try : p.Kill(True) : Catch : End Try
                            _log.Warn("MCP", $"Server {c.Options.Id}: the restart command did not finish within {hc.RestartCommandTimeoutSeconds}s")
                            Return
                        End Try
                    End Using
                    _events.Emit(AgentEventType.McpServerStateChanged, $"MCP server {c.Options.Id}: restart command finished (exit {p.ExitCode})", level:=EventLevel.Warning,
                                 data:=New Dictionary(Of String, Object) From {{"serverId", c.Options.Id}, {"state", "Restarted"}, {"exitCode", p.ExitCode}})
                    _log.Warn("MCP", $"Server {c.Options.Id}: restart command exit {p.ExitCode}: {TextUtil.Truncate((Await output.ConfigureAwait(False)) & " " & (Await errors.ConfigureAwait(False)), 300)}")
                End Using
            Catch ex As Exception When TypeOf ex Is ComponentModel.Win32Exception OrElse TypeOf ex Is InvalidOperationException
                _log.Warn("MCP", $"Server {c.Options.Id}: the restart command could not run: {ex.Message}")
            End Try
        End Function

        Private Sub MarkFaulted(c As Connection, message As String)
            SetState(c, McpConnectionState.Faulted, message)
            c.ReconnectSignal.Set()
        End Sub

        Private Shared Async Function SafeDisposeAsync(client As IMcpClient) As Task
            Try
                Await client.DisposeAsync().ConfigureAwait(False)
            Catch
            End Try
        End Function

        Private Shared Function FilterTools(opt As McpServerOptions, tools As List(Of McpToolInfo)) As List(Of McpToolInfo)
            Dim result = tools
            If opt.IncludeTools.Count > 0 Then result = result.Where(Function(t) opt.IncludeTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            If opt.ExcludeTools.Count > 0 Then result = result.Where(Function(t) Not opt.ExcludeTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            For Each t In result
                If opt.IdempotentTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase) Then t.IsIdempotent = True
            Next
            Return result
        End Function

        Public Function GetTools(serverId As String) As List(Of McpToolInfo) Implements IMcpServerManager.GetTools
            Dim c As Connection = Nothing
            If Not _connections.TryGetValue(serverId, c) OrElse c.State <> McpConnectionState.Connected Then Return New List(Of McpToolInfo)
            Return c.Tools
        End Function

        Public Function GetServerOptions(serverId As String) As McpServerOptions Implements IMcpServerManager.GetServerOptions
            Dim c As Connection = Nothing
            If _connections.TryGetValue(serverId, c) Then Return c.Options
            Return Nothing
        End Function

        Public Sub RequestReconnect(serverId As String) Implements IMcpServerManager.RequestReconnect
            Dim c As Connection = Nothing
            If _connections.TryGetValue(serverId, c) Then
                c.Attempts = 0
                c.ReconnectSignal.Set()
            End If
        End Sub

        ''' <summary>Whether a server runs commands (a tool like run_command, execute, shell, terminal): its calls are checked for destructive ones.</summary>
        Private Shared Function RunsCommands(c As Connection) As Boolean
            Return c.Tools.Any(Function(t) Text.RegularExpressions.Regex.IsMatch(If(t.Name, ""), "(^|_)(run|exec|execute|shell|bash|terminal|command|process)(_|$)", Text.RegularExpressions.RegexOptions.IgnoreCase))
        End Function

        Public Async Function CallToolAsync(serverId As String, toolName As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult) Implements IMcpServerManager.CallToolAsync
            Dim c As Connection = Nothing
            If Not _connections.TryGetValue(serverId, c) Then Throw New McpUnavailableException($"MCP server '{serverId}' is not configured.")
            If c.State <> McpConnectionState.Connected OrElse c.Client Is Nothing Then Throw New McpUnavailableException($"MCP server '{serverId}' is currently {c.State}.")
            ' The watchdog found it not responding: fail at once instead of waiting out the timeout on every call.
            Dim downSince = c.DownSinceUtc
            If downSince.HasValue Then
                Dim resetting = c.Options.HealthCheck IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(c.Options.HealthCheck.RestartCommand)
                Throw New McpUnavailableException($"MCP server '{serverId}' is not responding (since {downSince.Value:HH:mm} UTC)" &
                                                  If(resetting, "; the host is resetting it. Do other parts of your work and try it again in a few minutes.",
                                                     ". An administrator has to restart it; do other parts of your work, and tell the user if you need it."))
            End If
            arguments = WithDefaultArguments(c.Options, c.Tools.FirstOrDefault(Function(t) String.Equals(t.Name, toolName, StringComparison.OrdinalIgnoreCase)), toolName, arguments)
            ' A server that runs commands (the shared code sandbox, a shell): what would destroy it or take it down is refused.
            If Not c.Options.AllowDestructiveCommands AndAlso RunsCommands(c) Then
                Dim why = Global.Agentic.Security.DestructiveCommands.Check(arguments)
                If why IsNot Nothing Then
                    _log.Warn("MCP", $"Refused a call to {serverId}.{toolName}: {why}")
                    _events.Emit(AgentEventType.McpServerStateChanged, $"Refused a destructive call to {serverId}.{toolName}: {why}", level:=EventLevel.Warning,
                                 data:=New Dictionary(Of String, Object) From {{"serverId", serverId}, {"tool", toolName}})
                    Return New McpCallResult With {.IsError = True, .Text = Global.Agentic.Security.DestructiveCommands.BlockedMessage(why)}
                End If
            End If
            Await _global.WaitAsync(ct).ConfigureAwait(False)
            Try
                Await c.Gate.WaitAsync(ct).ConfigureAwait(False)
                Try
                    Interlocked.Increment(c.Calls)
                    Using callCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        callCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, c.Options.TimeoutSeconds)))
                        Dim failure As Exception = Nothing
                        Try
                            Dim result = Await c.Client.CallToolAsync(toolName, arguments, callCts.Token).ConfigureAwait(False)
                            Interlocked.Exchange(c.ConsecutiveTimeouts, 0)
                            ' Base64 file contents (Gitea's get_file_contents) reach agents and chats as text.
                            McpFileContent.DecodeInPlace(result)
                            ' Errors models keep making come back with the call that works.
                            If result IsNot Nothing AndAlso result.IsError Then
                                Dim hint = McpErrorHints.HintFor(toolName, result.Text)
                                If hint IsNot Nothing Then result.Text = If(result.Text, "").TrimEnd() & vbLf & "Hint: " & hint
                            End If
                            Return result
                        Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                            Interlocked.Increment(c.Failures)
                            ' A run of timeouts: let the watchdog check the server now.
                            Dim hc = c.Options.HealthCheck
                            If Interlocked.Increment(c.ConsecutiveTimeouts) >= Math.Max(1, If(hc?.TimeoutsBeforeCheck, Integer.MaxValue)) Then c.HealthSignal.Set()
                            Throw New TimeoutException($"MCP tool '{serverId}:{toolName}' timed out after {c.Options.TimeoutSeconds}s.")
                        Catch ex As OperationCanceledException
                            Throw
                        Catch ex As Exception
                            failure = ex
                        End Try
                        Interlocked.Increment(c.Failures)
                        ' Transport-level failures fault the connection so it reconnects; tool errors don't.
                        If TypeOf failure Is IO.IOException OrElse TypeOf failure Is Net.Http.HttpRequestException OrElse TypeOf failure Is ObjectDisposedException OrElse
                           failure.GetType().Name.Contains("Transport") Then
                            MarkFaulted(c, failure.Message)
                        End If
                        Throw New McpUnavailableException($"MCP tool '{serverId}:{toolName}' failed: {failure.Message}")
                    End Using
                Finally
                    c.Gate.Release()
                End Try
            Finally
                _global.Release()
            End Try
        End Function

        ''' <summary>
        ''' The call's arguments plus the server's DefaultArguments for this tool ("*" first, then the tool's own) that the model left
        ''' out or sent as null, and its DefaultEnv merged into the "env" argument (the model's own variables win). A default is only
        ''' added when the tool's schema declares that argument (or declares none); env only when the schema declares env.
        ''' </summary>
        Friend Shared Function WithDefaultArguments(opt As McpServerOptions, tool As McpToolInfo, toolName As String, arguments As JsonElement) As JsonElement
            Dim defaults As New List(Of KeyValuePair(Of String, String))
            Dim envDefaults As New List(Of KeyValuePair(Of String, String))
            Dim d As Dictionary(Of String, String) = Nothing
            If opt?.DefaultArguments IsNot Nothing Then
                If opt.DefaultArguments.TryGetValue("*", d) AndAlso d IsNot Nothing Then defaults.AddRange(d)
                If opt.DefaultArguments.TryGetValue(toolName, d) AndAlso d IsNot Nothing Then defaults.AddRange(d)
            End If
            If opt?.DefaultEnv IsNot Nothing Then
                If opt.DefaultEnv.TryGetValue("*", d) AndAlso d IsNot Nothing Then envDefaults.AddRange(d)
                If opt.DefaultEnv.TryGetValue(toolName, d) AndAlso d IsNot Nothing Then envDefaults.AddRange(d)
            End If
            If defaults.Count = 0 AndAlso envDefaults.Count = 0 Then Return arguments
            Dim declared As JsonObject = Nothing
            Try
                declared = TryCast(TryCast(JsonNode.Parse(If(tool?.InputSchemaJson, "{}")), JsonObject)?("properties"), JsonObject)
            Catch ex As JsonException
            End Try
            Dim obj As JsonObject = Nothing
            If arguments.ValueKind = JsonValueKind.Object Then obj = TryCast(JsonNode.Parse(arguments.GetRawText()), JsonObject)
            If obj Is Nothing Then obj = New JsonObject()
            Dim changed = False
            For Each kv In defaults
                If String.IsNullOrWhiteSpace(kv.Key) OrElse kv.Value Is Nothing Then Continue For
                If declared IsNot Nothing AndAlso declared.Count > 0 AndAlso Not declared.ContainsKey(kv.Key) Then Continue For
                If obj.ContainsKey(kv.Key) AndAlso obj(kv.Key) IsNot Nothing Then Continue For
                obj(kv.Key) = ConfigValue(kv.Value)
                changed = True
            Next
            If envDefaults.Count > 0 AndAlso declared IsNot Nothing AndAlso declared.ContainsKey("env") Then
                ' The model's env (an object) keeps its own values; a missing or null env starts empty.
                Dim env = TryCast(obj("env"), JsonObject)
                If env Is Nothing Then
                    env = New JsonObject()
                    obj("env") = env
                End If
                For Each kv In envDefaults
                    If String.IsNullOrWhiteSpace(kv.Key) OrElse kv.Value Is Nothing OrElse env.ContainsKey(kv.Key) Then Continue For
                    env(kv.Key) = kv.Value
                    changed = True
                Next
            End If
            If Not changed Then Return arguments
            Using doc = JsonDocument.Parse(obj.ToJsonString())
                Return doc.RootElement.Clone()
            End Using
        End Function

        ''' <summary>A configuration value as JSON: numbers and true/false keep their type (configuration hands everything over as text).</summary>
        Private Shared Function ConfigValue(v As String) As JsonNode
            Dim l As Long, dbl As Double
            If Long.TryParse(v, Globalization.NumberStyles.Integer, Globalization.CultureInfo.InvariantCulture, l) Then Return JsonValue.Create(l)
            If Double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, dbl) Then Return JsonValue.Create(dbl)
            If String.Equals(v, "true", StringComparison.OrdinalIgnoreCase) Then Return JsonValue.Create(True)
            If String.Equals(v, "false", StringComparison.OrdinalIgnoreCase) Then Return JsonValue.Create(False)
            Return JsonValue.Create(v)
        End Function

        Public Function GetStatus() As List(Of McpServerStatus) Implements IMcpServerManager.GetStatus
            Return _connections.Values.OrderBy(Function(c) c.Options.Id).Select(Function(c) New McpServerStatus With {
                .Id = c.Options.Id, .Name = If(c.Options.Name, c.Options.Id), .Description = c.Options.Description, .Transport = c.Options.Transport.ToString(),
                .Enabled = c.Options.Enabled, .State = c.State, .LastError = c.LastError, .LastConnectedUtc = c.LastConnectedUtc,
                .ToolCount = If(c.State = McpConnectionState.Connected, c.Tools.Count, 0),
                .Tools = If(c.State = McpConnectionState.Connected, c.Tools.Select(Function(t) t.Name).ToList(), New List(Of String)),
                .Calls = Interlocked.Read(c.Calls), .Failures = Interlocked.Read(c.Failures), .ReconnectAttempts = c.Attempts, .DownSinceUtc = c.DownSinceUtc}).ToList()
        End Function

        Public Async Function StopAsync() As Task Implements IMcpServerManager.StopAsync
            _cts?.Cancel()
            Try
                Dim loops As Task()
                SyncLock _loops
                    loops = _loops.ToArray()
                End SyncLock
                Await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(False)
            Catch
            End Try
            For Each c In _connections.Values
                If c.Client IsNot Nothing Then
                    Await SafeDisposeAsync(c.Client).ConfigureAwait(False)
                    c.Client = Nothing
                End If
            Next
        End Function
    End Class

    ''' <summary>An MCP tool exposed to an agent, after permission filtering.</summary>
    Public NotInheritable Class McpAgentTool
        Implements IAgentTool

        Private ReadOnly _manager As IMcpServerManager
        Private ReadOnly _info As McpToolInfo
        Private ReadOnly _parameters As JsonObject
        Private ReadOnly _requiresApproval As Boolean
        Private ReadOnly _timeout As TimeSpan

        Public ReadOnly Property Name As String Implements IAgentTool.Name
        Public ReadOnly Property ServerId As String
        Public ReadOnly Property ToolName As String

        Friend Sub New(manager As IMcpServerManager, info As McpToolInfo, exposedName As String, requiresApproval As Boolean, timeout As TimeSpan)
            _manager = manager
            _info = info
            Name = exposedName
            ServerId = info.ServerId
            ToolName = info.Name
            _requiresApproval = requiresApproval
            _timeout = timeout
            Dim parsed As JsonObject = Nothing
            Try
                parsed = TryCast(JsonNode.Parse(If(info.InputSchemaJson, "{}")), JsonObject)
            Catch
            End Try
            If parsed Is Nothing Then parsed = New JsonObject From {{"type", JsonValue.Create("object")}, {"properties", New JsonObject()}}
            If parsed("type") Is Nothing Then parsed("type") = JsonValue.Create("object")
            If parsed("properties") Is Nothing Then parsed("properties") = New JsonObject()
            _parameters = parsed
        End Sub

        Public ReadOnly Property Description As String Implements IAgentTool.Description
            Get
                Return $"[MCP {_info.ServerId}] " & TextUtil.Truncate(If(_info.Description, _info.Name), 1000)
            End Get
        End Property

        Public ReadOnly Property Parameters As JsonObject Implements IAgentTool.Parameters
            Get
                Return _parameters
            End Get
        End Property

        Public ReadOnly Property Source As ToolSource Implements IAgentTool.Source
            Get
                Return ToolSource.Mcp
            End Get
        End Property

        Public ReadOnly Property IsIdempotent As Boolean Implements IAgentTool.IsIdempotent
            Get
                Return _info.IsIdempotent OrElse _info.IsReadOnly
            End Get
        End Property

        Public ReadOnly Property RequiresApproval As Boolean Implements IAgentTool.RequiresApproval
            Get
                Return _requiresApproval
            End Get
        End Property

        Public ReadOnly Property Timeout As TimeSpan Implements IAgentTool.Timeout
            Get
                Return _timeout
            End Get
        End Property

        Public Async Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult) Implements IAgentTool.ExecuteAsync
            ' Re-check permission at call time (profile may have changed since the registry was built).
            If Not McpToolRegistry.IsAllowed(ctx.Profile, ServerId, ToolName) Then Return ToolResult.Fail("This MCP tool is no longer permitted for you.")
            Dim result As McpCallResult
            Try
                result = Await _manager.CallToolAsync(ServerId, ToolName, args, ctx.CancellationToken).ConfigureAwait(False)
            Catch ex As McpUnavailableException
                Return ToolResult.Fail(ex.Message)
            Catch ex As TimeoutException
                Return ToolResult.Fail(ex.Message)
            End Try
            Dim sb As New Text.StringBuilder()
            If Not String.IsNullOrEmpty(result.Text) Then sb.Append(result.Text)
            If Not String.IsNullOrEmpty(result.StructuredJson) AndAlso String.IsNullOrEmpty(result.Text) Then sb.Append(result.StructuredJson)
            For Each img In result.Images
                Try
                    Dim art = Await ctx.Scope.Artifacts.SaveAsync($"{ToolName}-image", img.MimeType, img.Data, $"Image returned by MCP tool {ServerId}:{ToolName}", ctx.CurrentTask?.Id).ConfigureAwait(False)
                    sb.AppendLine().Append($"[image saved as artifact {art.Id} ({img.MimeType}); use analyze_image to inspect it]")
                Catch ex As Exception
                    sb.AppendLine().Append("[an image was returned but could not be stored: " & ex.Message & "]")
                End Try
            Next
            Return New ToolResult With {
                .Success = Not result.IsError, .Content = If(result.IsError, "ERROR: ", "") & sb.ToString(),
                .Untrusted = True, .UntrustedSource = $"mcp:{ServerId}/{ToolName}"}
        End Function
    End Class

    Public Interface IMcpToolRegistry
        Function GetToolsForAgent(profile As EffectiveProfile) As List(Of IAgentTool)
    End Interface

    ''' <summary>Turns connected servers' tools into the permission-filtered tool list for one agent.</summary>
    Public NotInheritable Class McpToolRegistry
        Implements IMcpToolRegistry

        Private ReadOnly _manager As IMcpServerManager
        Private ReadOnly _servers As List(Of McpServerOptions)

        Public Sub New(manager As IMcpServerManager, servers As IEnumerable(Of McpServerOptions))
            _manager = manager
            _servers = servers.ToList()
        End Sub

        ''' <summary>Deny rules win; allow rules are "server:*" or "server:tool"; an allowed server with no tool rules allows all its tools.</summary>
        Public Shared Function IsAllowed(profile As EffectiveProfile, serverId As String, toolName As String) As Boolean
            If Not (profile.AllowedMcpServers.Contains(serverId) OrElse profile.AllowedMcpServers.Contains("*")) Then Return False
            For Each rule In profile.DeniedMcpTools
                If RuleMatches(rule, serverId, toolName) Then Return False
            Next
            Dim serverRules = profile.AllowedMcpTools.Where(Function(r) r.StartsWith(serverId & ":", StringComparison.OrdinalIgnoreCase) OrElse r.StartsWith("*:", StringComparison.Ordinal)).ToList()
            If serverRules.Count = 0 Then Return True
            Return serverRules.Any(Function(r) RuleMatches(r, serverId, toolName))
        End Function

        Private Shared Function RuleMatches(rule As String, serverId As String, toolName As String) As Boolean
            Dim parts = rule.Split(":"c, 2)
            If parts.Length <> 2 Then Return False
            Dim serverOk = parts(0) = "*" OrElse String.Equals(parts(0), serverId, StringComparison.OrdinalIgnoreCase)
            Dim toolOk = parts(1) = "*" OrElse String.Equals(parts(1), toolName, StringComparison.OrdinalIgnoreCase)
            Return serverOk AndAlso toolOk
        End Function

        Public Function GetToolsForAgent(profile As EffectiveProfile) As List(Of IAgentTool) Implements IMcpToolRegistry.GetToolsForAgent
            Dim result As New List(Of IAgentTool)
            ' Servers added at run time count too; a user's own server is for their chats, never for agents.
            Dim live = TryCast(_manager, McpServerManager)
            For Each s In If(live IsNot Nothing, live.ServerOptions(), _servers)
                If Not s.Enabled OrElse (s.Owner IsNot Nothing AndAlso s.Owner <> "*") Then Continue For
                For Each t In _manager.GetTools(s.Id)
                    If Not IsAllowed(profile, s.Id, t.Name) Then Continue For
                    Dim exposed = InputValidation.SanitizeToolName($"mcp__{s.Id}__{t.Name}")
                    Dim approval = s.RequiresApproval(t.Name)
                    result.Add(New McpAgentTool(_manager, t, exposed, approval, TimeSpan.FromSeconds(s.TimeoutSeconds + 5)))
                Next
            Next
            Return result
        End Function
    End Class

End Namespace
