Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Collections.Generic
Imports System.IO
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Mcp
Imports Agentic.Persistence
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The code sandbox: pulling files out of it (base64 over MCP), and the reaper that stops long-lived processes/containers the
''' host started while never touching pre-existing infrastructure or Compose stacks.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class SandboxTests

    ' ---------------------------------------------------------------- a scripted fake code server

    Private NotInheritable Class FakeMcp
        Implements IMcpServerManager
        Public ReadOnly Calls As New List(Of (Tool As String, Args As String))
        ''' <summary>toolName -> queue of canned JSON results (dequeued per call; last one repeats).</summary>
        Public ReadOnly Scripts As New Dictionary(Of String, Queue(Of String))(StringComparer.Ordinal)
        Public ToolNames As New List(Of String) From {"run_command", "write_file", "list_files", "get_process_status", "list_processes", "kill_process"}

        Public Sub Script(tool As String, ParamArray results As String())
            Dim q As New Queue(Of String)
            For Each r In results : q.Enqueue(r) : Next
            Scripts(tool) = q
        End Sub

        Public Function GetTools(serverId As String) As List(Of McpToolInfo) Implements IMcpServerManager.GetTools
            Return ToolNames.Select(Function(n) New McpToolInfo With {.ServerId = serverId, .Name = n, .InputSchemaJson = "{}"}).ToList()
        End Function

        ''' <summary>Every call hangs until it is cancelled (a sandbox that accepts connections but never answers).</summary>
        Public Hang As Boolean

        ''' <summary>Tools whose calls come back as errors (IsError) with their scripted text.</summary>
        Public ReadOnly Failing As New HashSet(Of String)(StringComparer.Ordinal)

        Public Function CallToolAsync(serverId As String, toolName As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult) Implements IMcpServerManager.CallToolAsync
            If Hang Then Return HangAsync(ct)
            Calls.Add((toolName, arguments.GetRawText()))
            Dim text = "{}"
            Dim q As Queue(Of String) = Nothing
            If Scripts.TryGetValue(toolName, q) AndAlso q.Count > 0 Then
                text = If(q.Count = 1, q.Peek(), q.Dequeue())
            End If
            Return Task.FromResult(New McpCallResult With {.Text = text, .IsError = Failing.Contains(toolName)})
        End Function

        Private Shared Async Function HangAsync(ct As CancellationToken) As Task(Of McpCallResult)
            Await Task.Delay(Timeout.Infinite, ct)
            Return Nothing
        End Function

        Public Function StartAsync(ct As CancellationToken) As Task Implements IMcpServerManager.StartAsync
            Return Task.CompletedTask
        End Function
        Public Function StopAsync() As Task Implements IMcpServerManager.StopAsync
            Return Task.CompletedTask
        End Function
        Public Function GetStatus() As List(Of McpServerStatus) Implements IMcpServerManager.GetStatus
            Return New List(Of McpServerStatus)
        End Function
        Public Function GetServerOptions(serverId As String) As McpServerOptions Implements IMcpServerManager.GetServerOptions
            Return New McpServerOptions With {.Id = serverId}
        End Function
        Public Sub RequestReconnect(serverId As String) Implements IMcpServerManager.RequestReconnect
        End Sub
    End Class

    Private Shared Function RunResult(id As String, status As String, exitCode As Integer?, ParamArray chunks As String()) As String
        Dim out As New StringBuilder("[")
        out.Append(String.Join(",", chunks.Select(Function(c) "{""type"":""output"",""data"":" & JsonSerializer.Serialize(c) & "}")))
        out.Append("]")
        Dim e = If(exitCode.HasValue, exitCode.Value.ToString(), "null")
        Return $"{{""id"":""{id}"",""status"":""{status}"",""exit_code"":{e},""output"":{out},""truncated"":false,""next_offset"":{chunks.Length}}}"
    End Function

    ' ---------------------------------------------------------------- download

    <Fact>
    Public Async Function DownloadReassemblesBase64() As Task
        Dim mcp As New FakeMcp()
        Dim payload = Encoding.UTF8.GetBytes("hello sandbox file")
        Dim b64 = Convert.ToBase64String(payload)
        ' One run_command response with the size line and the bracketed base64.
        mcp.Script("run_command", RunResult("p1", "done", 0, "__AGENTIC_B64_START__" & vbLf & b64 & vbLf & "__AGENTIC_B64_END__" & vbLf))
        Dim gw As New SandboxGateway(mcp, New NullLog())
        Dim dl = Await gw.DownloadFileAsync("code-exec", "/home/user/a.txt", 10L * 1024 * 1024, CancellationToken.None)
        Assert.Null(dl.ErrorText)
        Assert.Equal("a.txt", dl.Name)
        Assert.Equal("hello sandbox file", Encoding.UTF8.GetString(dl.Data))
    End Function

    <Fact>
    Public Async Function DownloadRejectsDirectoryAndMissing() As Task
        Dim mcp As New FakeMcp()
        mcp.Script("run_command", RunResult("p1", "done", 0, "__ISDIR__" & vbLf))
        Dim gw As New SandboxGateway(mcp, New NullLog())
        Dim dir = Await gw.DownloadFileAsync("code-exec", "/home/user/folder", 1024 * 1024, CancellationToken.None)
        Assert.Null(dir.Data)
        Assert.Contains("folder", dir.ErrorText)

        mcp.Script("run_command", RunResult("p1", "done", 0, "__NOFILE__" & vbLf))
        Dim miss = Await gw.DownloadFileAsync("code-exec", "/nope", 1024 * 1024, CancellationToken.None)
        Assert.Null(miss.Data)
        Assert.Contains("No such file", miss.ErrorText)
    End Function

    <Fact>
    Public Async Function DownloadRefusesOversizeFile() As Task
        Dim mcp As New FakeMcp()
        mcp.Script("run_command", RunResult("p1", "done", 0, "__TOOBIG__=99999999" & vbLf))
        Dim gw As New SandboxGateway(mcp, New NullLog())
        Dim dl = Await gw.DownloadFileAsync("code-exec", "/home/user/big.bin", 1024 * 1024, CancellationToken.None)
        Assert.Null(dl.Data)
        Assert.Contains("limit", dl.ErrorText)
    End Function

    <Fact>
    Public Async Function RunPagesUntilDone() As Task
        Dim mcp As New FakeMcp()
        ' run_command returns "running" with the first half; get_process_status returns the rest then done.
        mcp.Script("run_command", "{""id"":""p9"",""status"":""running"",""exit_code"":null,""output"":[{""type"":""output"",""data"":""AA""}],""truncated"":false,""next_offset"":1}")
        mcp.Script("get_process_status", "{""id"":""p9"",""status"":""done"",""exit_code"":0,""output"":[{""type"":""output"",""data"":""BB""}],""truncated"":false,""next_offset"":2}",
                                         "{""id"":""p9"",""status"":""done"",""exit_code"":0,""output"":[],""truncated"":false,""next_offset"":2}")
        Dim gw As New SandboxGateway(mcp, New NullLog())
        Dim run = Await gw.RunAsync("code-exec", "echo", Nothing, 5, 100000, CancellationToken.None)
        Assert.Equal(0, run.ExitCode)
        Assert.Equal("AABB", run.Output)
    End Function

    ' ---------------------------------------------------------------- reaper decisions

    Private Shared Function Res(kind As String, Optional created As DateTime? = Nothing, Optional lastSeen As DateTime? = Nothing,
                                Optional protectedFlag As Boolean = False, Optional state As String = "running") As SandboxResourceInfo
        Dim now = DateTime.UtcNow
        Return New SandboxResourceInfo With {.Kind = kind, .State = state, .IsProtected = protectedFlag,
            .CreatedUtc = If(created, now), .FirstSeenUtc = If(created, now), .LastSeenUtc = If(lastSeen, now)}
    End Function

    <Fact>
    Public Sub ProcessStoppedOnMaxLifeAndIdle()
        Dim now = DateTime.UtcNow
        Dim opt As New SandboxOptions With {.ProcessMaxLifeMinutes = 60, .ProcessIdleMinutes = 30}
        ' Young and busy: not due.
        Assert.Null(SandboxReaper.DueReason(Res("process", now.AddMinutes(-5), now.AddMinutes(-1)), opt, now))
        ' Old: due by max lifetime.
        Assert.Contains("maximum lifetime", SandboxReaper.DueReason(Res("process", now.AddMinutes(-90), now.AddMinutes(-1)), opt, now))
        ' Young but idle: due by idle.
        Assert.Contains("idle", SandboxReaper.DueReason(Res("process", now.AddMinutes(-10), now.AddMinutes(-45)), opt, now))
    End Sub

    <Fact>
    Public Sub ContainerUsesMaxLifeOnlyAndRespectsProtection()
        Dim now = DateTime.UtcNow
        Dim opt As New SandboxOptions With {.ContainerMaxLifeMinutes = 60, .ProcessIdleMinutes = 1}
        ' Idle does not apply to containers: an old-but-only-idle container (young) is not due.
        Assert.Null(SandboxReaper.DueReason(Res("container", now.AddMinutes(-10), now.AddMinutes(-9)), opt, now))
        Assert.Contains("maximum lifetime", SandboxReaper.DueReason(Res("container", now.AddMinutes(-120), now), opt, now))
        ' Protected is never due even when ancient.
        Assert.Null(SandboxReaper.DueReason(Res("container", now.AddMinutes(-9000), now, protectedFlag:=True), opt, now))
        ' Not running is never due.
        Assert.Null(SandboxReaper.DueReason(Res("container", now.AddMinutes(-9000), now, state:="stopped"), opt, now))
    End Sub

    <Fact>
    Public Sub ZeroLimitsMeanNever()
        Dim now = DateTime.UtcNow
        Dim opt As New SandboxOptions With {.ProcessMaxLifeMinutes = 0, .ProcessIdleMinutes = 0}
        Assert.Null(SandboxReaper.DueReason(Res("process", now.AddDays(-30), now.AddDays(-30)), opt, now))
    End Sub

    <Fact>
    Public Sub ManageableOnlyAdHocContainers()
        Dim opt As New SandboxOptions With {.ManagedComposeProjects = New List(Of String) From {"myapp"}, .NeverTouch = New List(Of String) From {"keepme"}}
        Dim protectedRefs As New HashSet(Of String)(StringComparer.Ordinal) From {"abc123", "infra-db"}
        ' Ad-hoc container (no compose project): managed.
        Assert.True(SandboxReaper.IsManageable(New SandboxContainer With {.Id = "x1", .Name = "scratch"}, opt, protectedRefs))
        ' Compose stack not in the managed list: left alone.
        Assert.False(SandboxReaper.IsManageable(New SandboxContainer With {.Id = "x2", .Name = "open-webui", .ComposeProject = "chat-gpt"}, opt, protectedRefs))
        ' Compose stack explicitly managed: managed.
        Assert.True(SandboxReaper.IsManageable(New SandboxContainer With {.Id = "x3", .Name = "myapp-web", .ComposeProject = "myapp"}, opt, protectedRefs))
        ' In the startup protected snapshot: never.
        Assert.False(SandboxReaper.IsManageable(New SandboxContainer With {.Id = "abc123", .Name = "whatever"}, opt, protectedRefs))
        ' On the never-touch list by name: never.
        Assert.False(SandboxReaper.IsManageable(New SandboxContainer With {.Id = "x4", .Name = "keepme"}, opt, protectedRefs))
    End Sub

    <Fact>
    Public Sub ParsesOpenTerminalProcessIdTimestamp()
        Dim dt = SandboxReaper.ProcessStartFromId("20261025-043759-6afa15")
        Assert.True(dt.HasValue)
        Assert.Equal(2026, dt.Value.Year)
        Assert.Equal(4, dt.Value.Hour)
        Assert.Null(SandboxReaper.ProcessStartFromId("short"))
    End Sub

    <Fact>
    Public Sub ParsesDockerCreatedAt()
        Dim dt = SandboxGateway.ParseDockerTime("2026-10-22 04:56:00 +0000 UTC")
        Assert.True(dt.HasValue)
        Assert.Equal(2026, dt.Value.Year)
        Assert.Equal(10, dt.Value.Month)
        Assert.Null(SandboxGateway.ParseDockerTime(""))
    End Sub

    ' ---------------------------------------------------------------- owner attribution

    <Fact>
    Public Sub AttributesSingleActiveChatOnly()
        Dim act As New SandboxActivity()
        Dim now = DateTime.UtcNow
        act.Note("alice", "chat1", Nothing, now.AddMinutes(-2))
        Dim one = act.Attribute(now, TimeSpan.FromMinutes(15))
        Assert.True(one.HasValue)
        Assert.Equal("alice", one.Value.User)
        ' A second, different chat in the window makes it ambiguous: no attribution.
        act.Note("bob", "chat2", Nothing, now.AddMinutes(-1))
        Assert.False(act.Attribute(now, TimeSpan.FromMinutes(15)).HasValue)
        ' Outside the window: none.
        Dim act2 As New SandboxActivity()
        act2.Note("carol", "chat9", Nothing, now.AddMinutes(-40))
        Assert.False(act2.Attribute(now, TimeSpan.FromMinutes(15)).HasValue)
    End Sub

    ' ---------------------------------------------------------------- store round-trip

    <Fact>
    Public Async Function StoreUpsertsListsAndStops() As Task
        Dim dir = Path.Combine(Path.GetTempPath(), "sbx-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Try
            Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
            Await New SchemaMigrator(db).MigrateAsync()
            Dim store As New SandboxResourceStore(db)
            Dim now = DateTime.UtcNow

            Dim a = Await store.UpsertAsync(New SandboxResourceInfo With {.ServerId = "code-exec", .Kind = "process", .Ref = "p1",
                .Name = "sleep 999", .Command = "sleep 999", .State = "running", .OwnerUser = "alice", .OwnerChatId = "c1"}, now)
            Assert.Equal("running", a.State)
            Assert.Equal("alice", a.OwnerUser)

            ' Upsert with the same ref keeps the row (no duplicate), refreshes LastSeen, keeps the owner.
            Dim later = now.AddMinutes(5)
            Dim a2 = Await store.UpsertAsync(New SandboxResourceInfo With {.ServerId = "code-exec", .Kind = "process", .Ref = "p1", .State = "running"}, later)
            Assert.Equal(a.Id, a2.Id)
            Assert.Equal("alice", a2.OwnerUser)
            Assert.True(a2.LastSeenUtc >= a.LastSeenUtc)

            Dim running = Await store.ListAsync({"running"})
            Assert.Single(running)

            Await store.MarkStoppedAsync(a.Id, "stopped", "max lifetime", later)
            Assert.Empty(Await store.ListAsync({"running"}))
            Dim stopped = Await store.ListAsync({"stopped"})
            Assert.Single(stopped)
            Assert.Equal("max lifetime", stopped(0).Reason)
        Finally
            Try : Directory.Delete(dir, True) : Catch : End Try
        End Try
    End Function

    ' ---------------------------------------------------------------- HTTP endpoints (in-process host)

    <Fact>
    Public Async Function EndpointsRouteAndGateWithoutACodeServer() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' No code-exec MCP server is connected in the test host, so downloads are advertised as off in chat meta.
                Dim meta = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/meta")).RootElement
                Assert.False(meta.GetProperty("sandboxDownloads").GetBoolean())
                ' The resources endpoint is reachable and reports the reaper flags with an empty running list.
                Dim resResp = Await PlatformApiTests.Send(admin.Client, "GET", "/api/sandbox/resources", Nothing, admin.Csrf)
                Assert.Equal(HttpStatusCode.OK, resResp.StatusCode)
                Dim res = JsonDocument.Parse(Await resResp.Content.ReadAsStringAsync()).RootElement
                Assert.True(res.GetProperty("reaperEnabled").GetBoolean())
                Assert.Empty(res.GetProperty("resources").EnumerateArray())
                ' Listing sandbox files with no connected code server is a clean 409, not a crash.
                Dim files = Await PlatformApiTests.Send(admin.Client, "GET", "/api/chat/sandbox/files?path=/home/user", Nothing, admin.Csrf)
                Assert.Equal(HttpStatusCode.Conflict, files.StatusCode)
                ' Stopping a resource that does not exist is a clean 404.
                Dim stop404 = Await PlatformApiTests.Send(admin.Client, "POST", "/api/sandbox/resources/sbx_nope/stop", "{}", admin.Csrf)
                Assert.Equal(HttpStatusCode.NotFound, stop404.StatusCode)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' Production 2026-11-02: processes the sandbox could not kill were tried again on every pass and logged each time (893
    ''' warnings in a day). Now a failed stop waits 15 min, then 30, 60… before the next try.
    ''' </summary>
    <Fact>
    Public Async Function AProcessThatCannotBeStoppedIsRetriedLessOften() As Task
        Dim dir = Path.Combine(Path.GetTempPath(), "sbx-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Try
            Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
            Await New SchemaMigrator(db).MigrateAsync()
            Dim mcp As New FakeMcp()
            ' Started long ago (past the maximum lifetime), still "running"; kill_process fails.
            mcp.Script("list_processes", "{""result"":[{""id"":""20261025-043759-6afa15"",""command"":""git log"",""status"":""running"",""exit_code"":null}]}")
            mcp.Script("kill_process", "Error calling tool 'kill_process': [Errno 24] Too many open files")
            mcp.Failing.Add("kill_process")
            Dim state As New Dictionary(Of String, String)
            Dim reaper As New SandboxReaper(New SandboxGateway(mcp, New NullLog()), New SandboxResourceStore(db), New SandboxOptions() With {.ManageContainers = False}, "code-exec", Nothing, Nothing,
                                            Function(k) Task.FromResult(If(state.ContainsKey(k), state(k), Nothing)),
                                            Function(k, v)
                                                state(k) = v
                                                Return Task.CompletedTask
                                            End Function, New NullLog())
            Dim kills = Function() mcp.Calls.Where(Function(c) c.Tool = "kill_process").Count()
            Dim now = New DateTime(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc)
            Await reaper.RunOnceAsync(now, CancellationToken.None)
            Assert.Equal(1, kills())
            ' The next passes (every 10 min) leave it alone until 15 min have passed.
            Await reaper.RunOnceAsync(now.AddMinutes(10), CancellationToken.None)
            Assert.Equal(1, kills())
            Await reaper.RunOnceAsync(now.AddMinutes(16), CancellationToken.None)
            Assert.Equal(2, kills())
            ' Then 30 min.
            Await reaper.RunOnceAsync(now.AddMinutes(40), CancellationToken.None)
            Assert.Equal(2, kills())
            Await reaper.RunOnceAsync(now.AddMinutes(47), CancellationToken.None)
            Assert.Equal(3, kills())
        Finally
            Try : Directory.Delete(dir, True) : Catch : End Try
        End Try
    End Function

    ''' <summary>
    ''' Production 2026-11-03: after the sandbox was restarted, about 1000 tracked processes no longer existed. Only 500 were
    ''' marked gone (the store's default page), and the reaper tried to stop the others: 497 warnings of "Process not found".
    ''' </summary>
    <Fact>
    Public Async Function ProcessesThatNoLongerExistAreGoneNotFailed() As Task
        Dim dir = Path.Combine(Path.GetTempPath(), "sbx-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Try
            Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
            Await New SchemaMigrator(db).MigrateAsync()
            Dim store As New SandboxResourceStore(db)
            Dim mcp As New FakeMcp()
            ' 600 old processes tracked as running; the restarted sandbox lists none of them.
            Dim old = Enumerable.Range(0, 600).Select(Function(i) $"{{""id"":""20261025-0437{i:00}-{i:x6}"",""command"":""git log {i}"",""status"":""running"",""exit_code"":null}}")
            mcp.Script("list_processes", "{""result"":[" & String.Join(",", old) & "]}", "{""result"":[]}")
            mcp.Script("kill_process", "Error calling tool 'kill_process': HTTP error 404: Not Found - {'detail': 'Process not found'}")
            mcp.Failing.Add("kill_process")
            Dim state As New Dictionary(Of String, String)
            Dim reaper As New SandboxReaper(New SandboxGateway(mcp, New NullLog()), store, New SandboxOptions() With {.ManageContainers = False, .ProcessMaxLifeMinutes = 0}, "code-exec", Nothing, Nothing,
                                            Function(k) Task.FromResult(If(state.ContainsKey(k), state(k), Nothing)),
                                            Function(k, v)
                                                state(k) = v
                                                Return Task.CompletedTask
                                            End Function, New NullLog())
            Dim kills = Function() mcp.Calls.Where(Function(c) c.Tool = "kill_process").Count()
            Dim now = New DateTime(2026, 11, 3, 2, 50, 0, DateTimeKind.Utc)
            Await reaper.RunOnceAsync(now, CancellationToken.None)
            Assert.Equal(600, (Await store.ListAsync({"running"}, 100000)).Count)
            ' The restart: none of them is listed any more, so every one is gone and none is "stopped".
            Await reaper.RunOnceAsync(now.AddMinutes(3), CancellationToken.None)
            Assert.Empty(Await store.ListAsync({"running", "failed"}, 100000))
            Assert.Equal(0, kills())

            ' A stop answered with "Process not found" is gone too, not a failure tried again later.
            Assert.True(SandboxReaper.AlreadyGone("Error calling tool 'kill_process': HTTP error 404: Not Found - {'detail': 'Process not found'}"))
            Assert.False(SandboxReaper.AlreadyGone("Error calling tool 'kill_process': [Errno 24] Too many open files"))
            mcp.Script("list_processes", "{""result"":[{""id"":""20261025-043759-6afa15"",""command"":""sleep 9999"",""status"":""running"",""exit_code"":null}]}")
            Dim due As New SandboxReaper(New SandboxGateway(mcp, New NullLog()), store, New SandboxOptions() With {.ManageContainers = False}, "code-exec", Nothing, Nothing,
                                         Function(k) Task.FromResult(If(state.ContainsKey(k), state(k), Nothing)),
                                         Function(k, v)
                                             state(k) = v
                                             Return Task.CompletedTask
                                         End Function, New NullLog())
            Await due.RunOnceAsync(now.AddMinutes(10), CancellationToken.None)
            Assert.Equal(1, kills())
            Assert.Empty(Await store.ListAsync({"running", "failed"}, 100000))
            Assert.Contains(Await store.ListAsync({"gone"}, 100000), Function(r) r.Ref = "20261025-043759-6afa15")
        Finally
            Try : Directory.Delete(dir, True) : Catch : End Try
        End Try
    End Function

    ''' <summary>Production 2026-10-30: the sandbox accepted connections but never answered, and /api/sandbox/apps never replied.</summary>
    <Fact>
    Public Async Function AHungSandboxDoesNotHoldTheAppsPage() As Task
        Dim dir = Path.Combine(Path.GetTempPath(), "sbx-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(dir)
        Try
            Dim db As New Database(Path.Combine(dir, "agentic.db"), 2)
            Await New SchemaMigrator(db).MigrateAsync()
            Dim mcp As New FakeMcp()
            mcp.Script("run_command", RunResult("p1", "done", 0, "__DOCKER_OK__" & vbLf),
                       RunResult("p2", "done", 0, "__C__abc123" & vbTab & "shop-web" & vbTab & "" & vbTab & "2026-10-30 01:00:00 +0000 UTC" & vbTab & "0.0.0.0:20001->80/tcp" & vbTab & "nginx:1.27" & vbLf))
            mcp.Script("get_process_status", RunResult("p2", "done", 0))
            Dim state As New Dictionary(Of String, String) From {{"SandboxContainerSnapshotDone", "2026-10-01"}}
            Dim reaper As New SandboxReaper(New SandboxGateway(mcp, New NullLog()), New SandboxResourceStore(db), New SandboxOptions(), "code-exec", Nothing, Nothing,
                                            Function(k) Task.FromResult(If(state.ContainsKey(k), state(k), Nothing)),
                                            Function(k, v)
                                                state(k) = v
                                                Return Task.CompletedTask
                                            End Function, New NullLog()) With {
                .SandboxCallTimeout = TimeSpan.FromSeconds(1), .SandboxRetryPause = TimeSpan.FromMinutes(5)}

            Dim ok = Await reaper.PreviewableForPageAsync(CancellationToken.None, TimeSpan.FromSeconds(5))
            Assert.Null(ok.Warning)
            Assert.Equal("shop-web", Assert.Single(ok.Apps).Name)

            ' The sandbox hangs: the page answers within its budget with the last apps and says why.
            mcp.Hang = True
            reaper.ExpireContainerListForTests()
            Dim sw = Diagnostics.Stopwatch.StartNew()
            Dim slow = Await reaper.PreviewableForPageAsync(CancellationToken.None, TimeSpan.FromMilliseconds(300))
            Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms")
            Assert.Contains("slow to answer", slow.Warning)
            Assert.Equal("shop-web", Assert.Single(slow.Apps).Name)

            ' The hung look gives up after SandboxCallTimeout; until the retry pause ends, pages get the failure at once.
            Await Task.Delay(1500)
            sw.Restart()
            Dim failed = Await reaper.PreviewableForPageAsync(CancellationToken.None, TimeSpan.FromSeconds(5))
            Assert.True(sw.ElapsedMilliseconds < 1000, $"took {sw.ElapsedMilliseconds} ms")
            Assert.Contains("did not answer within 1 s", failed.Warning)
            Assert.Single(failed.Apps)
        Finally
            Try : Directory.Delete(dir, True) : Catch : End Try
        End Try
    End Function

End Class
