Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.IO
Imports System.Text.RegularExpressions
Imports Agentic.Configuration
Imports Agentic.Mcp
Imports Xunit

''' <summary>
''' The built-in shell server's commands for agents: run_command never stops a command for taking long (it goes on in the
''' background), returns when the shell ends even if something it started holds the output, run_command_background starts
''' one there, command_output audits it (status, silence, a question waiting for an answer, waiting for it) and kill_command
''' stops everything it started. The background form of a tool that needs approval needs it too.
''' </summary>
<Collection("ShellCommands")>
Public Class ShellCommandTests

    Private Shared Function NewServer() As ShellServer
        Dim dir = Path.Combine(Path.GetTempPath(), "agentic-shell-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Return New ShellServer(dir, defaultTimeoutSeconds:=20)
    End Function

    Private Shared Function IdOf(text As String) As String
        Return Regex.Match(If(text, ""), "as (bg\d+)").Groups(1).Value
    End Function

    Private Shared Function Pings() As Integer
        Return Process.GetProcessesByName("PING").Length
    End Function

    <Fact>
    Public Sub ALongCommandGoesOnInTheBackgroundAndIsAuditedThenStopped()
        Dim s = NewServer()
        Dim watch = Stopwatch.StartNew()
        Dim r = s.RunCommand("Write-Output slow-start; Start-Sleep -Seconds 60", wait_seconds:=3)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString())
        Assert.Contains("Still running after 3 s", r)
        Dim id = IdOf(r)
        Assert.NotEmpty(id)
        ' A busy machine may start PowerShell slowly: its first line comes with the hand-over or, waited for, with the audit.
        Dim audit = If(r.Contains("slow-start"), s.CommandOutput(id), s.CommandOutput(id, wait_seconds:=30))
        Assert.Contains(id & " (Write-Output slow-start; Start-Sleep -Seconds 60): running for", audit)
        Assert.Contains("slow-start", r & audit)
        Assert.Contains("last output", audit)
        Assert.Contains(id & ": ", s.CommandOutput())
        Assert.StartsWith("Stopped " & id, s.KillCommand(id))
        Assert.StartsWith("No background command " & id, s.CommandOutput(id))
    End Sub

    <Fact>
    Public Sub ACommandThatStartsSomethingHoldingItsOutputReturnsWhenItsShellEnds()
        If Not OperatingSystem.IsWindows() Then Return
        Dim s = NewServer()
        For Each p In Process.GetProcessesByName("PING")
            p.Kill()
        Next
        Dim watch = Stopwatch.StartNew()
        ' Started straight from the shell, and through cmd that ends at once (no process tree leads to it afterwards).
        Dim direct = s.RunCommand("Start-Process -NoNewWindow ping -ArgumentList '-n','120','127.0.0.1'", wait_seconds:=60)
        Dim through = s.RunCommand("cmd /c 'start /b ping -n 120 127.0.0.1'", wait_seconds:=60)
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), watch.Elapsed.ToString())
        Assert.Contains("Exit code 0", direct)
        Assert.Contains("something this command started is still running", direct.ToLowerInvariant())
        Assert.Contains("it goes on in the background as", through)
        Assert.True(Pings() >= 2, $"{Pings()} ping(s)")
        s.KillCommand(IdOf(direct))
        s.KillCommand(IdOf(through))
        Assert.True(TestHost.WaitUntilAsync(Function() Task.FromResult(Pings() = 0), 10).GetAwaiter().GetResult(), $"{Pings()} ping(s) left")
    End Sub

    <Fact>
    Public Sub CommandOutputWarnsAboutAQuestionAndCanWaitForTheEnd()
        Dim s = NewServer()
        Dim asks = IdOf(s.RunCommandBackground("Write-Output 'Overwrite the file? (y/n)'; Start-Sleep -Seconds 60"))
        ' Once the question is out (waited for, however slowly PowerShell starts), 5 s of silence make it a warning.
        Assert.Contains("Overwrite the file?", s.CommandOutput(asks, wait_seconds:=30))
        Threading.Thread.Sleep(6000)
        Assert.Contains("Warning: its last line looks like a question waiting for an answer", s.CommandOutput(asks))
        s.KillCommand(asks)

        Dim quick = IdOf(s.RunCommandBackground("Start-Sleep -Seconds 2; Write-Output all-done"))
        Dim report = s.CommandOutput(quick, wait_seconds:=30)
        Assert.Contains("finished with exit code 0", report)
        Assert.Contains("all-done", report)
    End Sub

    <Fact>
    Public Sub TheBackgroundFormOfAToolThatNeedsApprovalNeedsItToo()
        Dim o As New McpServerOptions With {.Id = "shell", .ApprovalRequiredTools = New List(Of String) From {"run_command"}}
        Assert.True(o.RequiresApproval("run_command"))
        Assert.True(o.RequiresApproval("RUN_COMMAND_BACKGROUND"))
        Assert.False(o.RequiresApproval("command_output"))
        Assert.False(o.RequiresApproval("kill_command"))
        Assert.False(New McpServerOptions().RequiresApproval("run_command_background"))
    End Sub

End Class
