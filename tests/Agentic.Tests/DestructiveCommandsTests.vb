Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports Agentic.Security
Imports Xunit

''' <summary>
''' The shared code sandbox is refused commands that would destroy it or take it down (2026-11-04: a user ran
''' sudo rm -rf / --no-preserve-root from a chat's Run button and wiped it for everyone), while everyday commands still run.
''' </summary>
Public Class DestructiveCommandsTests

    Public Shared ReadOnly Blocked As TheoryData(Of String) = New TheoryData(Of String) From {
        "sudo rm -rf / --no-preserve-root",
        "rm -rf /",
        "rm -rf /*",
        "sudo rm -fr /usr",
        "rm -r -f ~",
        "rm -rf $HOME/*",
        "rm -rf ""$HOME""",
        "rm -rf /home/user",
        "rm -rf /home/user/",
        "cd / && rm -rf *",
        "bash -c ""rm -rf /""",
        "echo hi; sudo rm -Rf /etc",
        "import os" & vbLf & "os.system('rm -rf /')",
        "import shutil; shutil.rmtree('/')",
        "subprocess.run([""rm"", ""-rf"", ""/""])",
        "require('fs').rmSync('/', { recursive: true, force: true })",
        ":(){ :|:& };:",
        "bomb() { bomb | bomb & }; bomb",
        "mkfs.ext4 /dev/sda1",
        "dd if=/dev/zero of=/dev/sda bs=1M",
        "kill -9 -1",
        "sudo shutdown now",
        "reboot",
        "systemctl poweroff",
        "docker rm -f open-terminal",
        "docker stop open-terminal",
        "sudo docker exec -u root open-terminal sh",
        "docker kill $(docker ps -q)",
        "docker run --privileged -it ubuntu bash",
        "docker run -v /:/host alpine",
        "docker run --mount type=bind,source=/,target=/h alpine",
        "docker system prune -af",
        "docker volume rm open-terminal_home",
        "echo cm0gLXJmIC8= | base64 -d | sh",
        "find / -delete",
        "find ~ -exec rm -rf {} +",
        "chmod -R 000 /",
        "sudo chown -R nobody /usr",
        "mv /usr /tmp/usr",
        "Remove-Item -Recurse -Force C:\",
        "Remove-Item -Recurse -Force 'C:\Windows'",
        "rd /s /q C:\",
        "format C: /q",
        "Stop-Computer -Force"}

    Public Shared ReadOnly Allowed As TheoryData(Of String) = New TheoryData(Of String) From {
        "rm -rf node_modules",
        "rm -rf /home/user/myproj/build",
        "rm -rf ./dist /tmp/cache",
        "rm -f /tmp/x.txt",
        "ls -la /",
        "git log --oneline | head",
        "docker run -d -p 20001:3000 --name preview-1 myapp",
        "docker compose down",
        "docker rm -f preview-1",
        "docker run -v /home/user/app:/app node:22 npm test",
        "find ~ -name '*.pyc' -delete",
        "find /home/user/proj -name node_modules -prune",
        "chown -R user:user /home/user/proj",
        "chmod -R u+w ~",
        "kill -9 12345",
        "echo ""reboot needed"" > notes.txt",
        "python -c ""print('shutdown complete')""",
        "mv build/out.js dist/",
        "cat /etc/os-release",
        "Remove-Item -Recurse -Force C:\work\old",
        "pip install --user requests"}

    <Theory>
    <MemberData(NameOf(Blocked))>
    Public Sub DestructiveCommandsAreRefused(command As String)
        Assert.NotNull(DestructiveCommands.CheckText(command))
    End Sub

    <Theory>
    <MemberData(NameOf(Allowed))>
    Public Sub EverydayCommandsRun(command As String)
        Assert.Null(DestructiveCommands.CheckText(command))
    End Sub

    <Fact>
    Public Sub EveryStringOfACallIsChecked()
        ' A script written to a file and run later, input typed into a terminal, an argument list.
        Assert.NotNull(DestructiveCommands.Check(JsonDocument.Parse("{""path"":""x.sh"",""content"":""#!/bin/bash\necho start\nsudo rm -rf / --no-preserve-root\n""}").RootElement))
        Assert.NotNull(DestructiveCommands.Check(JsonDocument.Parse("{""process_id"":""p1"",""input"":""rm -rf ~\n""}").RootElement))
        Assert.NotNull(DestructiveCommands.Check(JsonDocument.Parse("{""argv"":[""rm"",""-rf"",""/""]}").RootElement))
        Assert.Null(DestructiveCommands.Check(JsonDocument.Parse("{""command"":""npm test"",""wait"":60}").RootElement))
        Assert.Contains("every user", DestructiveCommands.BlockedMessage("it deletes /"))
    End Sub

    ''' <summary>A fake sandbox that records what reaches it.</summary>
    Private NotInheritable Class RecordingFactory
        Implements Global.Agentic.Mcp.IMcpClientFactory
        Public ReadOnly Calls As New Concurrent.ConcurrentQueue(Of String)
        Public Function Create(server As Global.Agentic.Configuration.McpServerOptions) As Global.Agentic.Mcp.IMcpClient Implements Global.Agentic.Mcp.IMcpClientFactory.Create
            Return New Client(Me)
        End Function
        Private NotInheritable Class Client
            Implements Global.Agentic.Mcp.IMcpClient
            Private ReadOnly _f As RecordingFactory
            Public Sub New(f As RecordingFactory)
                _f = f
            End Sub
            Public Function ConnectAsync(ct As Threading.CancellationToken) As Task Implements Global.Agentic.Mcp.IMcpClient.ConnectAsync
                Return Task.CompletedTask
            End Function
            Public Function ListToolsAsync(ct As Threading.CancellationToken) As Task(Of List(Of Global.Agentic.Mcp.McpToolInfo)) Implements Global.Agentic.Mcp.IMcpClient.ListToolsAsync
                Return Task.FromResult(New List(Of Global.Agentic.Mcp.McpToolInfo) From {
                    New Global.Agentic.Mcp.McpToolInfo With {.Name = "run_command", .InputSchemaJson = "{""type"":""object""}"},
                    New Global.Agentic.Mcp.McpToolInfo With {.Name = "write_file", .InputSchemaJson = "{""type"":""object""}"}})
            End Function
            Public Function CallToolAsync(name As String, arguments As JsonElement, ct As Threading.CancellationToken) As Task(Of Global.Agentic.Mcp.McpCallResult) Implements Global.Agentic.Mcp.IMcpClient.CallToolAsync
                _f.Calls.Enqueue(name & " " & arguments.GetRawText())
                Return Task.FromResult(New Global.Agentic.Mcp.McpCallResult With {.Text = "ok"})
            End Function
            Public Function PingAsync(ct As Threading.CancellationToken) As Task Implements Global.Agentic.Mcp.IMcpClient.PingAsync
                Return Task.CompletedTask
            End Function
            Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
                Return ValueTask.CompletedTask
            End Function
        End Class
    End Class

    <Fact>
    Public Async Function TheSandboxNeverReceivesIt() As Task
        Dim f As New RecordingFactory()
        Using host = Await TestHost.StartAsync(Sub(o) o.McpServers.Add(New Global.Agentic.Configuration.McpServerOptions With {
                                                   .Id = "sbx", .Name = "Sandbox", .Transport = Global.Agentic.McpTransportKind.Http, .Endpoint = "http://sandbox.local/mcp"}),
                                               mcpFactory:=f)
            Dim mcp = host.H.Mcp
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(mcp.GetTools("sbx").Count = 2), 10))
            Dim bad = Await mcp.CallToolAsync("sbx", "run_command", JsonDocument.Parse("{""command"":""sudo rm -rf / --no-preserve-root""}").RootElement, Threading.CancellationToken.None)
            Assert.True(bad.IsError)
            Assert.StartsWith("Blocked", bad.Text)
            Dim script = Await mcp.CallToolAsync("sbx", "write_file", JsonDocument.Parse("{""path"":""a.sh"",""content"":""rm -rf ~/*""}").RootElement, Threading.CancellationToken.None)
            Assert.True(script.IsError)
            Dim good = Await mcp.CallToolAsync("sbx", "run_command", JsonDocument.Parse("{""command"":""npm test""}").RootElement, Threading.CancellationToken.None)
            Assert.Equal("ok", good.Text)
            Assert.Equal({"run_command {""command"":""npm test""}"}, f.Calls.ToArray())
        End Using
    End Function

    <Fact>
    Public Async Function TheRunButtonRefusesIt() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/run", New With {.language = "bash", .code = "echo hi" & vbLf & "sudo rm -rf / --no-preserve-root"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode)
                Assert.Contains("Blocked", Await res.Content.ReadAsStringAsync())
            End Using
        End Using
    End Function

End Class
