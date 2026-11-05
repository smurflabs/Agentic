Option Strict On
Option Explicit On
Option Infer On

Imports System.ServiceProcess
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Security

''' <summary>
''' Windows Service wrapper (Agentic.exe --service). Install with:
'''   sc.exe create Agentic binPath= "C:\Agentic\Agentic.exe --service --config C:\Agentic\appsettings.json" start= delayed-auto
'''   sc.exe failure Agentic reset= 86400 actions= restart/60000/restart/60000/restart/300000
''' </summary>
<System.Runtime.Versioning.SupportedOSPlatform("windows")>
Public NotInheritable Class WindowsServiceHost
    Inherits ServiceBase

    Private ReadOnly _loaded As ConfigLoadResult
    Private _app As HostApplication
    Private _log As StructuredLogger

    Public Sub New(loaded As ConfigLoadResult)
        _loaded = loaded
        ServiceName = "Agentic"
        CanStop = True
        CanShutdown = True
        CanPauseAndContinue = False
    End Sub

    Public Shared Function RunService(loaded As ConfigLoadResult) As Integer
        If Not OperatingSystem.IsWindows() Then
            Console.Error.WriteLine("--service is only supported on Windows.")
            Return 6
        End If
        ServiceBase.Run(New WindowsServiceHost(loaded))
        Return 0
    End Function

    Protected Overrides Sub OnStart(args As String())
        Dim redactor As New SecretRedactor()
        _log = Program.CreateLogger(_loaded.Options, redactor, consoleEnabled:=False)
        _app = New HostApplication(_loaded.Options, _loaded.ConfigPath, _log, redactor)
        RequestAdditionalTime(120000)
        Try
            _app.BuildAsync().GetAwaiter().GetResult()
            _app.StartAsync().GetAwaiter().GetResult()
            _log.Write(LogSeverity.Info, "Service", "Agentic service started; admin UI at " & _app.AdminUrl)
        Catch ex As Exception
            _log.Write(LogSeverity.Critical, "Service", "Service start failed: " & ex.Message, ex:=ex)
            _log.Flush(TimeSpan.FromSeconds(5))
            Throw
        End Try
    End Sub

    Protected Overrides Sub OnStop()
        RequestAdditionalTime((_loaded.Options.Host.ShutdownTimeoutSeconds + 30) * 1000)
        Try
            _app?.StopAsync().GetAwaiter().GetResult()
        Catch ex As Exception
            _log?.Write(LogSeverity.Error, "Service", "Error during stop: " & ex.Message, ex:=ex)
        End Try
        _log?.Flush(TimeSpan.FromSeconds(5))
    End Sub

    Protected Overrides Sub OnShutdown()
        OnStop()
    End Sub
End Class
