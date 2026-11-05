Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Security

''' <summary>Parsed command line. Kept deliberately simple.</summary>
Public NotInheritable Class CommandLine
    Public Property ConfigPath As String
    Public Property Port As Integer?
    Public Property Bind As String
    Public Property ValidateConfig As Boolean
    Public Property ListAgents As Boolean
    Public Property ShowVersion As Boolean
    Public Property HashPassword As Boolean
    Public Property PasswordArg As String
    Public Property ProbeModels As Boolean
    Public Property ProbeImage As Boolean
    Public Property RunAsService As Boolean
    Public Property ShowHelp As Boolean
    Public Property Errors As New List(Of String)

    Public Shared Function Parse(args As String()) As CommandLine
        Dim c As New CommandLine()
        Dim i = 0
        While i < args.Length
            Dim a = args(i).ToLowerInvariant()
            Select Case a
                Case "--config", "-c"
                    If i + 1 >= args.Length Then c.Errors.Add("--config requires a path") Else i += 1 : c.ConfigPath = args(i)
                Case "--port", "-p"
                    Dim p As Integer
                    If i + 1 >= args.Length OrElse Not Integer.TryParse(args(i + 1), p) OrElse p < 1 OrElse p > 65535 Then
                        c.Errors.Add("--port requires a number 1-65535")
                    Else
                        c.Port = p
                    End If
                    i += 1
                Case "--bind", "-b"
                    If i + 1 >= args.Length Then c.Errors.Add("--bind requires a host/address") Else i += 1 : c.Bind = args(i)
                Case "--validate-config" : c.ValidateConfig = True
                Case "--list-agents" : c.ListAgents = True
                Case "--version", "-v" : c.ShowVersion = True
                Case "--hash-password"
                    c.HashPassword = True
                    If i + 1 < args.Length AndAlso Not args(i + 1).StartsWith("--", StringComparison.Ordinal) Then i += 1 : c.PasswordArg = args(i)
                Case "--probe-models" : c.ProbeModels = True
                Case "--probe-image" : c.ProbeModels = True : c.ProbeImage = True
                Case "--service" : c.RunAsService = True
                Case "--help", "-h", "/?" : c.ShowHelp = True
                Case Else : c.Errors.Add("Unknown argument: " & args(i))
            End Select
            i += 1
        End While
        Return c
    End Function

    Public Shared ReadOnly HelpText As String =
        "Agentic " & HostApplication.Version & " - multi-agent AI worker host" & vbLf & vbLf &
        "Usage: " & IO.Path.GetFileName(If(Environment.ProcessPath, "Agentic.exe")) & " [options]" & vbLf &
        "  --config <path>       Configuration file (default: ./appsettings.json)" & vbLf &
        "  --port <n>            Override Server.Port" & vbLf &
        "  --bind <host>         Override Server.Host (e.g. 127.0.0.1, 0.0.0.0)" & vbLf &
        "  --validate-config     Validate configuration and exit" & vbLf &
        "  --list-agents         Print configured agents and exit" & vbLf &
        "  --probe-models        Check every configured model (chat, tool calls, vision) and exit" & vbLf &
        "  --probe-image         Like --probe-models plus one image generation (costs money)" & vbLf &
        "  --hash-password [pw]  Print a PBKDF2 hash for Admin.Users[].PasswordHash" & vbLf &
        "  --service             Run as a Windows Service (used by sc.exe)" & vbLf &
        "  --mcp-storage <dir>   Run the built-in shared storage MCP server on stdio (started by the host)" & vbLf &
        "  --mcp-shell <dir>     Run the built-in shell commands MCP server on stdio (started by the host)" & vbLf &
        "  --mcp-code-review <dir>  Run the built-in code review MCP server on stdio (started by the host)" & vbLf &
        "  --mcp-gitea [--gitea-mcp <exe>]  Run the built-in Gitea MCP server on stdio (GITEA_HOST, GITEA_ACCESS_TOKEN)" & vbLf &
        "  --version             Print version" & vbLf
End Class

Module Program

    Function Main(args As String()) As Integer
        ' Built-in shared storage MCP server (started by the host as a stdio child process). stdout carries the protocol,
        ' so this must run before anything else is written.
        If args.Any(Function(a) String.Equals(a, "--mcp-storage", StringComparison.OrdinalIgnoreCase)) Then Return Mcp.SharedStorageServer.RunStdio(args)
        If args.Any(Function(a) String.Equals(a, "--mcp-shell", StringComparison.OrdinalIgnoreCase)) Then Return Mcp.ShellServer.RunStdio(args)
        If args.Any(Function(a) String.Equals(a, "--mcp-code-review", StringComparison.OrdinalIgnoreCase)) Then Return Mcp.CodeReviewServer.RunStdio(args)
        If args.Any(Function(a) String.Equals(a, "--mcp-gitea", StringComparison.OrdinalIgnoreCase)) Then Return Mcp.GiteaServer.RunStdio(args)
        Dim builtIn = Mcp.BuiltInServers.Find(args)
        If builtIn IsNot Nothing Then Return builtIn(args)
        Try
            Console.OutputEncoding = Text.Encoding.UTF8
        Catch
        End Try
        Dim cl = CommandLine.Parse(args)
        If cl.Errors.Count > 0 Then
            For Each e In cl.Errors
                Console.Error.WriteLine("error: " & e)
            Next
            Console.Error.WriteLine("Use --help for usage.")
            Return 2
        End If
        If cl.ShowHelp Then Console.WriteLine(CommandLine.HelpText) : Return 0
        If cl.ShowVersion Then Console.WriteLine("Agentic " & HostApplication.Version) : Return 0
        If cl.HashPassword Then Return RunHashPassword(cl.PasswordArg)

        ' Services start with the system directory as working directory; use the exe folder instead.
        If cl.RunAsService Then Directory.SetCurrentDirectory(AppContext.BaseDirectory)

        Dim loaded As ConfigLoadResult
        Try
            loaded = ConfigLoader.Load(cl.ConfigPath, cl.Port, cl.Bind)
        Catch ex As Exception
            Console.Error.WriteLine("Configuration error: " & ex.Message)
            Return 3
        End Try

        If cl.ListAgents Then Return RunListAgents(loaded.Options)

        Dim report = ConfigValidator.Validate(loaded.Options)
        If cl.ValidateConfig Then Return PrintValidation(report, loaded.ConfigPath)
        If Not report.IsValid Then
            PrintValidation(report, loaded.ConfigPath)
            Console.Error.WriteLine("Refusing to start with an invalid configuration (nothing was started).")
            Return 4
        End If

        If cl.ProbeModels Then Return AI.ModelProbe.RunAsync(loaded.Options, cl.ProbeImage).GetAwaiter().GetResult()

        If cl.RunAsService Then
            If Not OperatingSystem.IsWindows() Then Console.Error.WriteLine("--service is only supported on Windows.") : Return 6
            Return WindowsServiceHost.RunService(loaded)
        End If
        Return RunConsoleAsync(loaded, report).GetAwaiter().GetResult()
    End Function

    Private Function RunHashPassword(arg As String) As Integer
        Dim pw = arg
        If String.IsNullOrEmpty(pw) Then
            Console.Write("Password: ")
            pw = ReadHidden()
            Console.Write("Repeat:   ")
            Dim again = ReadHidden()
            If pw <> again Then Console.Error.WriteLine("Passwords do not match.") : Return 1
        End If
        If pw.Length < 10 Then Console.Error.WriteLine("Use at least 10 characters.") : Return 1
        Console.WriteLine(PasswordHasher.Hash(pw))
        Return 0
    End Function

    Private Function ReadHidden() As String
        Dim sb As New Text.StringBuilder()
        If Console.IsInputRedirected Then
            Dim line = Console.ReadLine()
            Return If(line, "")
        End If
        While True
            Dim k = Console.ReadKey(True)
            If k.Key = ConsoleKey.Enter Then Exit While
            If k.Key = ConsoleKey.Backspace Then
                If sb.Length > 0 Then sb.Length -= 1
            ElseIf Not Char.IsControl(k.KeyChar) Then
                sb.Append(k.KeyChar)
            End If
        End While
        Console.WriteLine()
        Return sb.ToString()
    End Function

    Private Function RunListAgents(o As HostOptions) As Integer
        Dim resolver As New ProfileResolver(o.BaseProfiles, o.Communication, o.Subagents, o.Autonomy)
        Console.WriteLine($"{o.Agents.Count} configured agent(s):")
        For Each a In o.Agents
            Dim p = resolver.Resolve(a)
            Console.WriteLine($"  {p.Id,-20} {If(p.Enabled, "enabled ", "disabled")}  role={p.Role}")
            Console.WriteLine($"      models:     {String.Join(", ", p.AllowedModels)} (max tier {p.MaxTier})")
            Console.WriteLine($"      recipients: {If(p.AllowedRecipients.Count = 0, "(none)", String.Join(", ", p.AllowedRecipients))}")
            Console.WriteLine($"      mcp:        {If(p.AllowedMcpServers.Count = 0, "(none)", String.Join(", ", p.AllowedMcpServers))}")
            If p.Schedules.Length > 0 Then Console.WriteLine($"      schedules:  {String.Join(", ", p.Schedules.Select(Function(s) $"{s.Id}[{s.Handler}, {If(s.IntervalSeconds.HasValue, s.IntervalSeconds.Value.ToString() & "s", s.Cron)}]"))}")
        Next
        Return 0
    End Function

    Private Function PrintValidation(report As ValidationReport, path As String) As Integer
        Console.WriteLine("Configuration: " & path)
        For Each w In report.Warnings
            Console.WriteLine("  warning: " & w)
        Next
        For Each e In report.Errors
            Console.WriteLine("  ERROR:   " & e)
        Next
        Console.WriteLine(If(report.IsValid, $"Configuration is valid ({report.Warnings.Count} warning(s)).", $"Configuration is INVALID ({report.Errors.Count} error(s))."))
        Return If(report.IsValid, 0, 1)
    End Function

    Friend Function CreateLogger(o As HostOptions, redactor As SecretRedactor, consoleEnabled As Boolean) As StructuredLogger
        For Each kv In o.AIProviders
            redactor.AddSecret(kv.Value.ApiKey)
        Next
        For Each s In o.McpServers
            If s.Auth IsNot Nothing Then redactor.AddSecret(s.Auth.ResolveSecret())
        Next
        Return New StructuredLogger(o.Logging.Directory, o.Logging.MinimumLevel, o.Logging.ConsoleLevel, o.Logging.RetainDays, o.Logging.MaxFileMB, redactor, consoleEnabled)
    End Function

    Private Async Function RunConsoleAsync(loaded As ConfigLoadResult, report As ValidationReport) As Task(Of Integer)
        Dim redactor As New SecretRedactor()
        Using log = CreateLogger(loaded.Options, redactor, True)
            For Each w In report.Warnings
                log.Write(LogSeverity.Warn, "Config", w)
            Next
            Dim app As New HostApplication(loaded.Options, loaded.ConfigPath, log, redactor)
            Dim shutdown As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
            Dim stopped As New ManualResetEventSlim(False)
            AddHandler Console.CancelKeyPress, Sub(s, e)
                                                   e.Cancel = True
                                                   Console.WriteLine("Shutdown requested (Ctrl+C)...")
                                                   shutdown.TrySetResult(True)
                                               End Sub
            ' Console window closed / system shutdown: run the graceful path and block exit until it finishes (bounded).
            AddHandler AppDomain.CurrentDomain.ProcessExit, Sub(s, e)
                                                                shutdown.TrySetResult(True)
                                                                stopped.Wait(TimeSpan.FromSeconds(loaded.Options.Host.ShutdownTimeoutSeconds + 10))
                                                            End Sub
            AddHandler AppDomain.CurrentDomain.UnhandledException, Sub(s, e) log.Write(LogSeverity.Critical, "Host", "Unhandled exception", ex:=TryCast(e.ExceptionObject, Exception))
            AddHandler TaskScheduler.UnobservedTaskException, Sub(s, e)
                                                                  log.Write(LogSeverity.Error, "Host", "Unobserved task exception", ex:=e.Exception)
                                                                  e.SetObserved()
                                                              End Sub
            Dim startFailure As Exception = Nothing
            Try
                Await app.BuildAsync().ConfigureAwait(False)
                Await app.StartAsync().ConfigureAwait(False)
            Catch ex As Exception
                startFailure = ex
            End Try
            If startFailure IsNot Nothing Then
                log.Write(LogSeverity.Critical, "Host", "Startup failed: " & startFailure.Message, ex:=startFailure)
                Console.Error.WriteLine("Startup failed: " & startFailure.Message)
                If TypeOf startFailure Is Net.HttpListenerException Then
                    Console.Error.WriteLine("Hint: binding to non-localhost addresses requires running as Administrator or a URL ACL: netsh http add urlacl url=http://+:PORT/ user=DOMAIN\user")
                End If
                Await app.StopAsync().ConfigureAwait(False)
                stopped.Set()
                Return 5
            End If
            app.PrintBanner()
            Await shutdown.Task.ConfigureAwait(False)
            Await app.StopAsync().ConfigureAwait(False)
            log.Flush(TimeSpan.FromSeconds(5))
            stopped.Set()
        End Using
        Return 0
    End Function

End Module
