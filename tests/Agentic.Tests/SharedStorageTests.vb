Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Threading
Imports Agentic
Imports Agentic.Configuration
Imports Agentic.Mcp
Imports Agentic.Utilities
Imports Agentic.Work
Imports ModelContextProtocol
Imports Xunit

Public Class SharedStorageTests
    Private Shared Function TempRoot() As String
        Return Path.Combine(Path.GetTempPath(), "agentic-storage-tests", Guid.NewGuid().ToString("N"))
    End Function

    ''' <summary>The host executable from the test output, started in storage-server mode (as "{self}" would be).</summary>
    Private Shared Function StorageServerOptions(root As String) As McpServerOptions
        Return New McpServerOptions With {
            .Id = "storage", .Name = "Shared storage", .Enabled = True, .Transport = McpTransportKind.Stdio,
            .Command = Path.Combine(AppContext.BaseDirectory, "Agentic.exe"),
            .Arguments = New List(Of String) From {"--mcp-storage", root, "--max-file-mb", "1", "--quota-mb", "2"},
            .TimeoutSeconds = 30, .ConnectTimeoutSeconds = 30}
    End Function

    <Fact>
    Public Sub PathsAreConfinedToTheRoot()
        Dim s As New SharedStorageServer(TempRoot())
        Assert.Equal(Path.Combine(s.Root, "reports", "a.md"), s.Resolve("reports/a.md"))
        Assert.Equal(Path.Combine(s.Root, "a.md"), s.Resolve("\a.md".TrimStart("\"c)))
        For Each bad In {"../outside.txt", "reports/../../x", "C:\Windows\win.ini", "\\server\share\x", "D:x"}
            Assert.Throws(Of McpException)(Function() s.Resolve(bad))
        Next
        Assert.Throws(Of McpException)(Function() s.Resolve("", allowRoot:=False))
    End Sub

    <Fact>
    Public Sub FileOperationsWorkWithinLimits()
        Dim s As New SharedStorageServer(TempRoot(), maxFileMb:=1, quotaMb:=2)
        Assert.Equal("The shared storage is empty.", s.ListFiles(""))
        Assert.Contains("Wrote reports/q3.md", s.WriteFile("reports/q3.md", "line one" & vbLf & "Revenue up 12%" & vbLf & "line three"))
        s.WriteFile("reports/q3.md", vbLf & "appended", append:=True)
        Assert.Equal("Revenue up 12%", s.ReadFile("reports/q3.md", 2, 2))
        Assert.Contains("appended", s.ReadFile("reports/q3.md"))
        Assert.Contains("[folder] reports/", s.ListFiles(""))
        Assert.Contains("reports/q3.md:2: Revenue up 12%", s.GrepFiles("revenue"))
        Assert.Contains("reports/q3.md", s.SearchFiles("Q3"))
        s.MakeFolder("archive")
        Assert.Contains("Moved", s.MovePath("reports/q3.md", "archive/q3.md"))
        Assert.Throws(Of McpException)(Function() s.ReadFile("reports/q3.md"))
        Assert.Throws(Of McpException)(Function() s.DeletePath("archive")) ' not empty
        s.DeletePath("archive/q3.md")
        s.DeletePath("archive")
        ' Limits: per-file size and total quota.
        Dim mb = New String("x"c, 1024 * 1024)
        Assert.Throws(Of McpException)(Function() s.WriteFile("big.txt", mb & "x"))
        s.WriteFile("one.txt", mb.Substring(0, 1000 * 1024))
        s.WriteFile("two.txt", mb.Substring(0, 1000 * 1024))
        Assert.Contains("quota", Assert.Throws(Of McpException)(Function() s.WriteFile("three.txt", mb.Substring(0, 200 * 1024))).Message)
        ' Binary files are not returned as text.
        File.WriteAllBytes(Path.Combine(s.Root, "img.bin"), {1, 0, 2, 3})
        Assert.Contains("binary", Assert.Throws(Of McpException)(Function() s.ReadFile("img.bin")).Message)
    End Sub

    <Fact>
    Public Sub SelfAndDataPlaceholdersAreExpanded()
        Dim o As New HostOptions()
        o.Host.DataDirectory = "C:\agentic\data"
        o.McpServers.Add(New McpServerOptions With {.Id = "storage", .Transport = McpTransportKind.Stdio, .Command = "{self}",
                                                    .Arguments = New List(Of String) From {"--mcp-storage", "{data}\shared"}})
        ConfigLoader.ExpandMcpPlaceholders(o)
        Assert.Equal(Environment.ProcessPath, o.McpServers(0).Command)
        Assert.Equal("C:\agentic\data\shared", o.McpServers(0).Arguments(1))
    End Sub

    <Fact>
    Public Async Function TheHostExecutableServesStorageOverStdio() As Task
        Dim root = TempRoot()
        Dim client As New SdkMcpClient(StorageServerOptions(root))
        Dim failure As Exception = Nothing
        Try
            Await client.ConnectAsync(CancellationToken.None)
            Dim tools = Await client.ListToolsAsync(CancellationToken.None)
            Assert.Equal({"delete", "grep", "list_files", "make_folder", "move", "read_file", "search_files", "storage_info", "write_file"},
                         tools.Select(Function(t) t.Name).OrderBy(Function(n) n).ToArray())
            Assert.True(tools.Single(Function(t) t.Name = "read_file").IsIdempotent)
            Dim args As JsonElement
            JsonUtil.TryParseElement("{""path"":""notes/hello.txt"",""content"":""hi from A""}", args)
            Assert.False((Await client.CallToolAsync("write_file", args, CancellationToken.None)).IsError)
            Assert.Equal("hi from A", File.ReadAllText(Path.Combine(root, "notes", "hello.txt")))
            JsonUtil.TryParseElement("{""path"":""notes/hello.txt""}", args)
            Assert.Equal("hi from A", (Await client.CallToolAsync("read_file", args, CancellationToken.None)).Text.Trim())
            JsonUtil.TryParseElement("{""path"":""../escape.txt"",""content"":""x""}", args)
            Dim bad = Await client.CallToolAsync("write_file", args, CancellationToken.None)
            Assert.True(bad.IsError)
            Assert.Contains("'..' is not allowed", bad.Text)
        Catch ex As Exception
            failure = ex
        End Try
        Await client.DisposeAsync()
        If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
    End Function

    <Fact>
    Public Async Function AgentsHandFilesToEachOtherThroughSharedStorage() As Task
        Dim root = TempRoot()
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          Dim agent = FakeAIProvider.AgentOf(r)
                          If FakeAIProvider.ToolResultCount(r) = 0 Then
                              Return If(agent = "A", FakeAIProvider.Tool("mcp__storage__write_file", "{""path"":""handoff/brief.md"",""content"":""Customer wants a Q4 report.""}"),
                                                     FakeAIProvider.Tool("mcp__storage__read_file", "{""path"":""handoff/brief.md""}"))
                          End If
                          Return FakeAIProvider.Complete(FakeAIProvider.LastToolResult(r))
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.McpServers.Add(StorageServerOptions(root))
                                                   TestHost.Agent(o, "A").AllowedMcpServers = New List(Of String) From {"storage"}
                                                   TestHost.Agent(o, "B").AllowedMcpServers = New List(Of String) From {"storage"}
                                               End Sub, p)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Mcp.GetStatus().First().State = McpConnectionState.Connected), 30))
            Dim a = Await host.SubmitAsync("A", "Leave the brief for B")
            Await host.WaitForTaskStatusAsync("A", a, AgentTaskStatus.Completed, 30)
            Dim b = Await host.SubmitAsync("B", "Read the brief from A")
            Dim t = Await host.WaitForTaskStatusAsync("B", b, AgentTaskStatus.Completed, 30)
            Assert.Contains("Customer wants a Q4 report.", t.GetResult().Summary)
            ' C has no storage permission.
            Assert.DoesNotContain(host.H.ToolCatalog.BuildForAgent(host.H.Agents.GetEffectiveProfile("C")).Names(), Function(n) n.StartsWith("mcp__storage", StringComparison.Ordinal))
        End Using
    End Function
End Class
