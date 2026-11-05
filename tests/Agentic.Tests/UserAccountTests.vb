Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports Agentic
Imports Agentic.Http
Imports Agentic.Mcp
Imports Agentic.Utilities
Imports Xunit

Public Class UserAccountTests

    ''' <summary>A signed-in dashboard session (its own cookie jar) that sends the CSRF token with every change.</summary>
    Private NotInheritable Class Session
        Implements IDisposable
        Public Client As HttpClient
        Public Csrf As String

        Public Shared Async Function LoginAsync(port As Integer, user As String, password As String) As Task(Of (Session As Session, Status As HttpStatusCode))
            Dim handler As New HttpClientHandler With {.CookieContainer = New CookieContainer(), .UseCookies = True}
            Dim s As New Session With {.Client = New HttpClient(handler) With {.BaseAddress = New Uri($"http://127.0.0.1:{port}")}}
            Dim res = Await s.Client.PostAsync("/api/auth/login", HttpApiTests.JsonBody(New With {.username = user, .password = password}))
            If res.IsSuccessStatusCode Then s.Csrf = JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString()
            Return (s, res.StatusCode)
        End Function

        Public Async Function SendAsync(method As String, url As String, Optional body As Object = Nothing) As Task(Of HttpResponseMessage)
            Dim req As New HttpRequestMessage(New HttpMethod(method), url)
            If body IsNot Nothing Then req.Content = HttpApiTests.JsonBody(body)
            If Csrf IsNot Nothing Then req.Headers.Add("X-CSRF-Token", Csrf)
            Return Await Client.SendAsync(req)
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Client.Dispose()
        End Sub
    End Class

    <Fact>
    Public Sub EveryChangingRouteNeedsAPermission()
        ' Unmapped changes are admin-only; viewing needs nothing; known actions map to their permission.
        Assert.Equal("tasks.manage", Permissions.ForRoute("POST", "/api/agents/{id}/task"))
        Assert.Equal("tasks.manage", Permissions.ForRoute("PATCH", "/api/agents/{id}/tasks/{taskId}"))
        Assert.Equal("agents.control", Permissions.ForRoute("POST", "/api/agents/{id}/pause"))
        Assert.Equal("finance.manage", Permissions.ForRoute("POST", "/api/agents/{id}/profit"))
        Assert.Equal("finance.view", Permissions.ForRoute("GET", "/api/finance"))
        Assert.Equal("operations", Permissions.ForRoute("POST", "/api/operations/emergency-stop"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("GET", "/api/users"))
        Assert.Equal(Permissions.AdminOnly, Permissions.ForRoute("POST", "/api/something/new"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("POST", "/api/auth/password"))
        ' Reading the Workforce and its dashboards needs "View workforce"; other pages need nothing to view.
        Assert.Equal(Permissions.ViewWorkforce, Permissions.ForRoute("GET", "/api/agents/{id}/tasks"))
        Assert.Equal(Permissions.ViewWorkforce, Permissions.ForRoute("GET", "/api/agents"))
        Assert.Equal(Permissions.ViewWorkforce, Permissions.ForRoute("GET", "/api/insights"))
        Assert.Equal("agents.memory", Permissions.ForRoute("GET", "/api/agents/{id}/memory"))
        Assert.Null(Permissions.ForRoute("GET", "/api/models"))
        Assert.Null(Permissions.ForRoute("GET", "/api/status"))
    End Sub

    <Fact>
    Public Async Function UsersWithoutWorkforcePermissionSeeNoAgents() As Task
        Dim env = Await HttpApiTests.StartWithHttpAsync()
        Using env.Host
            Dim admin = (Await Session.LoginAsync(env.Port, "admin", "pw-123456789")).Session
            ' Chat only; read-only Workforce; and a permission that acts on agents (which includes viewing them).
            For Each u In {("chatty", "models.use"), ("viewer", "agents.view"), ("tasker", "tasks.manage")}
                Dim created = Await admin.SendAsync("POST", "/api/users", New With {.username = u.Item1, .password = u.Item1 & "-pass-123", .role = "User", .permissions = {u.Item2}})
                Assert.Equal(HttpStatusCode.Created, created.StatusCode)
            Next

            Dim chatty = (Await Session.LoginAsync(env.Port, "chatty", "chatty-pass-123")).Session
            Dim whoami = JsonDocument.Parse(Await chatty.Client.GetStringAsync("/api/auth/me")).RootElement
            Assert.False(whoami.GetProperty("workforce").GetBoolean())
            Assert.Equal({"models.use"}, whoami.GetProperty("permissions").EnumerateArray().Select(Function(p) p.GetString()).ToArray())
            For Each url In {"/api/agents", "/api/agents/A", "/api/tasks", "/api/subagents", "/api/approvals", "/api/communications", "/api/insights",
                             "/api/operations", "/api/events/recent", "/api/global-tasks", "/api/learning/lessons"}
                Assert.Equal(HttpStatusCode.Forbidden, (Await chatty.Client.GetAsync(url)).StatusCode)
            Next
            ' Pages outside the Workforce leave agents out.
            Dim status = JsonDocument.Parse(Await chatty.Client.GetStringAsync("/api/status")).RootElement
            Dim unused As JsonElement
            Assert.False(status.TryGetProperty("agents", unused))
            Assert.False(status.TryGetProperty("pendingApprovals", unused))
            Dim models = JsonDocument.Parse(Await chatty.Client.GetStringAsync("/api/models")).RootElement.GetProperty("models")
            Assert.All(models.EnumerateArray(), Sub(m) Assert.Equal(0, m.GetProperty("profileAgents").GetArrayLength()))

            For Each name In {"viewer", "tasker"}
                Dim s = (Await Session.LoginAsync(env.Port, name, name & "-pass-123")).Session
                Assert.True(JsonDocument.Parse(Await s.Client.GetStringAsync("/api/agents")).RootElement.GetArrayLength() > 0)
                Dim perms = JsonDocument.Parse(Await s.Client.GetStringAsync("/api/auth/me")).RootElement.GetProperty("permissions").EnumerateArray().Select(Function(p) p.GetString()).ToList()
                Assert.Contains(Permissions.ViewWorkforce, perms)
            Next
            env.Client.Dispose()
        End Using
    End Function

    <Fact>
    Public Async Function RestrictedUsersOnlySeeAndDoWhatTheyWereGranted() As Task
        Dim env = Await HttpApiTests.StartWithHttpAsync()
        Using env.Host
            Dim admin = (Await Session.LoginAsync(env.Port, "admin", "pw-123456789")).Session
            ' Validation: short password, unknown permission, unknown agent.
            Assert.Equal(HttpStatusCode.BadRequest, (Await admin.SendAsync("POST", "/api/users", New With {.username = "alice", .password = "short"})).StatusCode)
            Assert.Equal(HttpStatusCode.BadRequest, (Await admin.SendAsync("POST", "/api/users", New With {.username = "alice", .password = "alice-pass-123", .permissions = {"fly"}})).StatusCode)
            Assert.Equal(HttpStatusCode.BadRequest, (Await admin.SendAsync("POST", "/api/users", New With {.username = "alice", .password = "alice-pass-123", .allowedAgents = {"Ghost"}})).StatusCode)
            ' Alice may manage tasks of agent A only.
            Dim created = Await admin.SendAsync("POST", "/api/users", New With {.username = "alice", .password = "alice-pass-123", .displayName = "Alice",
                                                                               .role = "User", .permissions = {"tasks.manage"}, .allowedAgents = {"A"}})
            Assert.Equal(HttpStatusCode.Created, created.StatusCode)
            Assert.Equal(HttpStatusCode.Conflict, (Await admin.SendAsync("POST", "/api/users", New With {.username = "alice", .password = "alice-pass-123"})).StatusCode)

            Dim alice = (Await Session.LoginAsync(env.Port, "alice", "alice-pass-123")).Session
            Dim whoami = JsonDocument.Parse(Await alice.Client.GetStringAsync("/api/auth/me")).RootElement
            Assert.Equal("User", whoami.GetProperty("role").GetString())
            Assert.False(whoami.GetProperty("isAdmin").GetBoolean())
            ' Sees only agent A.
            Dim agents = JsonDocument.Parse(Await alice.Client.GetStringAsync("/api/agents")).RootElement
            Assert.Equal({"A"}, agents.EnumerateArray().Select(Function(a) a.GetProperty("id").GetString()).ToArray())
            Assert.Equal(HttpStatusCode.Forbidden, (Await alice.Client.GetAsync("/api/agents/B")).StatusCode)
            ' May add a task to A, not to B; may not control agents, see finance or manage users.
            Dim task = Await alice.SendAsync("POST", "/api/agents/A/task", New With {.name = "Alice's task", .description = "x", .handler = "noop"})
            Assert.True(task.IsSuccessStatusCode, Await task.Content.ReadAsStringAsync())
            Assert.Equal(HttpStatusCode.Forbidden, (Await alice.SendAsync("POST", "/api/agents/B/task", New With {.name = "x", .description = "x"})).StatusCode)
            Dim pause = Await alice.SendAsync("POST", "/api/agents/A/pause")
            Assert.Equal(HttpStatusCode.Forbidden, pause.StatusCode)
            Assert.Contains("Control agents", Await pause.Content.ReadAsStringAsync())
            Assert.Equal(HttpStatusCode.Forbidden, (Await alice.Client.GetAsync("/api/finance")).StatusCode)
            Assert.Equal(HttpStatusCode.Forbidden, (Await alice.Client.GetAsync("/api/users")).StatusCode)
            Assert.Equal(HttpStatusCode.Forbidden, (Await alice.SendAsync("POST", "/api/operations/pause-all")).StatusCode)
            ' Tasks list is scoped to A.
            Dim tasks = JsonDocument.Parse(Await alice.Client.GetStringAsync("/api/tasks")).RootElement
            Assert.All(tasks.EnumerateArray(), Sub(t) Assert.Equal("A", t.GetProperty("agentId").GetString()))

            ' Granting a permission takes effect after the forced sign-out.
            Dim upd = Await admin.SendAsync("PATCH", "/api/users/alice", New With {.permissions = {"tasks.manage", "agents.control"}})
            Assert.Equal(1, JsonDocument.Parse(Await upd.Content.ReadAsStringAsync()).RootElement.GetProperty("sessionsEnded").GetInt32())
            Assert.Equal(HttpStatusCode.Unauthorized, (Await alice.Client.GetAsync("/api/agents")).StatusCode)
            alice = (Await Session.LoginAsync(env.Port, "alice", "alice-pass-123")).Session
            Assert.True((Await alice.SendAsync("POST", "/api/agents/A/pause")).IsSuccessStatusCode)

            ' Alice changes her own password: the current one is required, then the old one stops working.
            Assert.Equal(HttpStatusCode.BadRequest, (Await alice.SendAsync("POST", "/api/auth/password", New With {.currentPassword = "wrong", .newPassword = "alice-new-pass-1"})).StatusCode)
            Assert.True((Await alice.SendAsync("POST", "/api/auth/password", New With {.currentPassword = "alice-pass-123", .newPassword = "alice-new-pass-1"})).IsSuccessStatusCode)
            Assert.Equal(HttpStatusCode.Unauthorized, (Await Session.LoginAsync(env.Port, "alice", "alice-pass-123")).Status)
            Assert.Equal(HttpStatusCode.OK, (Await Session.LoginAsync(env.Port, "alice", "alice-new-pass-1")).Status)

            ' Disabling signs her out and blocks sign-in; the last administrator is protected.
            Assert.True((Await admin.SendAsync("PATCH", "/api/users/alice", New With {.disabled = True})).IsSuccessStatusCode)
            Assert.Equal(HttpStatusCode.Unauthorized, (Await alice.Client.GetAsync("/api/agents")).StatusCode)
            Assert.Equal(HttpStatusCode.Unauthorized, (Await Session.LoginAsync(env.Port, "alice", "alice-new-pass-1")).Status)
            Assert.Equal(HttpStatusCode.BadRequest, (Await admin.SendAsync("PATCH", "/api/users/admin", New With {.role = "User"})).StatusCode)
            Assert.Equal(HttpStatusCode.BadRequest, (Await admin.SendAsync("DELETE", "/api/users/admin")).StatusCode)
            Assert.True((Await admin.SendAsync("DELETE", "/api/users/alice")).IsSuccessStatusCode)

            ' Admin's own password change keeps the current session, and survives a config re-apply.
            Assert.True((Await admin.SendAsync("POST", "/api/auth/password", New With {.currentPassword = "pw-123456789", .newPassword = "admin-new-pass-1"})).IsSuccessStatusCode)
            Assert.True((Await admin.Client.GetAsync("/api/users")).IsSuccessStatusCode)
            Dim configHash = env.Host.Options.Admin.Users(0).PasswordHash
            Await env.Host.H.AgentsRepo.ApplyConfigUserAsync("admin", configHash)
            Assert.Equal(HttpStatusCode.OK, (Await Session.LoginAsync(env.Port, "admin", "admin-new-pass-1")).Status)
            ' ...until the hash in the config file itself changes.
            Await env.Host.H.AgentsRepo.ApplyConfigUserAsync("admin", Security.PasswordHasher.Hash("from-config-99"))
            Assert.Equal(HttpStatusCode.OK, (Await Session.LoginAsync(env.Port, "admin", "from-config-99")).Status)
            env.Client.Dispose()
        End Using
    End Function
End Class

Public Class BuiltInMcpServerTests
    Private Shared Function TempDir() As String
        Return Path.Combine(Path.GetTempPath(), "agentic-builtin-tests", Guid.NewGuid().ToString("N"))
    End Function

    <Fact>
    Public Sub ShellRunsCommandsWithoutSecretsAndWithinLimits()
        Dim s As New ShellServer(TempDir(), defaultTimeoutSeconds:=20)
        Dim varName = "AGENTIC_TEST_SECRET_" & Guid.NewGuid().ToString("N").Substring(0, 6).ToUpperInvariant()
        Environment.SetEnvironmentVariable(varName, "do-not-leak")
        Try
            Dim r = s.RunCommand($"Write-Output (6*7); Write-Output ""[$env:{varName}]""; Get-Location | Select-Object -ExpandProperty Path")
            Assert.StartsWith("Exit code 0", r)
            Assert.Contains("42", r)
            Assert.Contains("[]", r) ' the secret variable is not passed to commands
            Assert.Contains(s.WorkDirectory, r)
        Finally
            Environment.SetEnvironmentVariable(varName, Nothing)
        End Try
        Assert.Contains("hello", s.RunCommand("echo hello", "cmd"))
        Assert.Contains("Exit code 3", s.RunCommand("exit 3", "cmd"))
        Assert.Contains("sub", s.RunCommand("(Get-Location).Path", folder:="sub/dir"))
        Dim slow = s.RunCommand("Start-Sleep -Seconds 30", timeout_seconds:=2)
        Assert.Contains("Still running after 2 s", slow)
        Assert.StartsWith("Stopped bg", s.KillCommand(Text.RegularExpressions.Regex.Match(slow, "as (bg\d+)").Groups(1).Value))
        Assert.Throws(Of ModelContextProtocol.McpException)(Function() s.RunCommand("dir", folder:="..\.."))
        Assert.True(ShellServer.IsSecretVariable("OPENAI_API_KEY"))
        Assert.False(ShellServer.IsSecretVariable("PATH"))
        ' Windows PowerShell 5.1 has no && / ||: chains run step by step and stop like bash; errors come back as text, not CLIXML.
        Dim chained = s.RunCommand("cmd /c echo first && cmd /c echo second")
        Assert.StartsWith("Exit code 0", chained)
        Assert.Contains("first", chained)
        Assert.Contains("second", chained)
        Dim stopped = s.RunCommand("cmd /c exit 4 && cmd /c echo never")
        Assert.StartsWith("Exit code 4", stopped)
        Assert.DoesNotContain("never", stopped)
        Assert.Contains("fallback", s.RunCommand("cmd /c exit 2 || cmd /c echo fallback"))
        Dim failed = s.RunCommand("Get-Item C:\no-such-file-here")
        Assert.Contains("Cannot find path", failed)
        Assert.DoesNotContain("CLIXML", failed)
        Assert.Equal("echo 'a && b'", ShellServer.AdaptForWindowsPowerShell("echo 'a && b'"))
        Assert.Equal("if ($x) { a && b }", ShellServer.AdaptForWindowsPowerShell("if ($x) { a && b }"))
        Assert.Equal("a && b || c", ShellServer.AdaptForWindowsPowerShell("a && b || c"))   ' mixed chains are left alone
    End Sub

    <Fact>
    Public Sub CodeReviewFindsProblemsAndOnlyNewOnesInDiffs()
        Dim code = "import os, pickle" & vbLf &
                   "API_KEY = ""abcd1234secretvalue""" & vbLf &
                   "def load(user):" & vbLf &
                   "    q = ""SELECT * FROM users WHERE name = '"" + user + ""'""" & vbLf &
                   "    os.system('ls ' + user)" & vbLf &
                   "    try:" & vbLf &
                   "        return pickle.loads(data)" & vbLf &
                   "    except:" & vbLf &
                   "        pass" & vbLf &
                   "    # TODO handle errors"
        Dim result = CodeReviewServer.Analyze(code, "python")
        Dim rules = result.Findings.Select(Function(f) f.Rule).ToList()
        For Each expected In {"SEC001", "SEC010", "SEC021", "SEC030", "REL002", "REL010"}
            Assert.Contains(expected, rules)
        Next
        Assert.Equal("high", result.Findings.First().Severity)
        Dim server As New CodeReviewServer()
        Assert.Contains("[HIGH] SEC001 line 2", server.ReviewCode(code, "app.py"))
        ' The diff reports only what the change introduces.
        Dim diff = server.DiffCode("x = 1" & vbLf & "y = 2", "x = 1" & vbLf & "y = eval(input())" & vbLf & "z = 3", "a.py")
        Assert.Contains("+2 / -1", diff)
        Assert.Contains("SEC020", diff)
        Assert.Contains("The change introduces no findings.", server.DiffCode("a = 1", "a = 2"))
        Assert.Contains("CancellationToken", server.ReviewChecklist("csharp"))
        Assert.Equal("python", CodeReviewServer.DetectLanguage("x/y/main.py", Nothing))
    End Sub

    <Fact>
    Public Async Function BuiltInServersStartOverStdio() As Task
        For Each args In {New List(Of String) From {"--mcp-code-review", TempDir()}, New List(Of String) From {"--mcp-shell", TempDir()}}
            Dim client As New SdkMcpClient(New Configuration.McpServerOptions With {
                .Id = "x", .Enabled = True, .Transport = McpTransportKind.Stdio, .Command = Path.Combine(AppContext.BaseDirectory, "Agentic.exe"),
                .Arguments = args, .TimeoutSeconds = 30, .ConnectTimeoutSeconds = 30})
            Dim failure As Exception = Nothing
            Try
                Await client.ConnectAsync(Threading.CancellationToken.None)
                Dim tools = (Await client.ListToolsAsync(Threading.CancellationToken.None)).Select(Function(t) t.Name).ToList()
                Assert.Contains(If(args(0) = "--mcp-shell", "run_command", "review_code"), tools)
            Catch ex As Exception
                failure = ex
            End Try
            Await client.DisposeAsync()
            If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
        Next
    End Function
End Class
