Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Configuration
Imports Agentic.Http
Imports Agentic.Work
Imports Microsoft.Data.Sqlite
Imports Xunit

''' <summary>
''' Finer control per User account (Users &amp; access): a permission per chat feature, the model API and the personal API
''' key; the MCP servers it may use; tokens per day and requests per minute. Existing accounts keep what they could do.
''' </summary>
Public Class UserAccessTests

    <Fact>
    Public Sub EachFeatureHasItsOwnPermission()
        Assert.Equal("api.use", Permissions.ForRoute("POST", "/v1/chat/completions"))
        Assert.Equal("api.use", Permissions.ForRoute("POST", "/api/v1/messages"))
        Assert.Equal("chat.code", Permissions.ForRoute("POST", "/api/chat/run"))
        Assert.Equal("chat.code", Permissions.ForRoute("GET", "/api/chat/sandbox/files"))
        Assert.Equal("chat.code", Permissions.ForRoute("POST", "/api/sandbox/apps/preview"))
        Assert.Equal("chat.knowledge", Permissions.ForRoute("POST", "/api/chat/knowledge"))
        Assert.Equal("chat.memory", Permissions.ForRoute("POST", "/api/chat/memories"))
        Assert.Equal("chat.web", Permissions.ForRoute("POST", "/api/chat/fetch-page"))
        Assert.Equal("chat.share", Permissions.ForRoute("POST", "/api/chat/conversations/{chatId}/share"))
        Assert.Equal("models.use", Permissions.ForRoute("DELETE", "/api/chat/conversations/{chatId}/share"))   ' stopping a share is always allowed
        Assert.Equal("models.own", Permissions.ForRoute("POST", "/api/chat/my-models"))
        Assert.Equal("tasks.manage|chat.uploads", Permissions.ForRoute("POST", "/api/uploads"))
        Assert.Equal("account.apikey", Permissions.ForRoute("POST", "/api/auth/api-key"))
        Assert.Equal(Permissions.Self, Permissions.ForRoute("DELETE", "/api/auth/api-key"))
        Assert.Equal("models.use", Permissions.ForRoute("POST", "/api/chat/conversations/{chatId}/messages"))
        ' Every permission is in a group the users editor shows.
        Assert.All(Permissions.Catalog, Sub(p) Assert.Contains(p.Group, {"Chat", "API & apps", "Workforce", "Money", "Administration"}))
    End Sub

    <Fact>
    Public Sub TheMigrationKeepsWhatEachAccountCouldDo()
        Dim dbFile = Path.Combine(Path.GetTempPath(), "agentic-access-" & Guid.NewGuid().ToString("N") & ".db")
        Try
            Using db As New SqliteConnection("Data Source=" & dbFile)
                db.Open()
                Dim exec = Sub(sql As String)
                               Using cmd = db.CreateCommand()
                                   cmd.CommandText = sql
                                   cmd.ExecuteNonQuery()
                               End Using
                           End Sub
                exec("CREATE TABLE AdminUsers (Username TEXT PRIMARY KEY, Role TEXT, Permissions TEXT)")
                exec("INSERT INTO AdminUsers VALUES ('chatter', 'User', '[""models.use""]'), ('coder', 'User', '[""models.use"",""chat.tools""]'), " &
                     "('viewer', 'User', '[""agents.view""]'), ('nothing', 'User', NULL), ('boss', 'Admin', NULL)")
                Dim asm = GetType(AdminApi).Assembly
                Dim name = asm.GetManifestResourceNames().First(Function(n) n.EndsWith("0024_user_access.sql", StringComparison.OrdinalIgnoreCase))
                Using s = asm.GetManifestResourceStream(name), r As New StreamReader(s)
                    exec(r.ReadToEnd())
                End Using
                Dim perms = Function(user As String) As List(Of String)
                                Using cmd = db.CreateCommand()
                                    cmd.CommandText = "SELECT Permissions FROM AdminUsers WHERE Username=$u"
                                    cmd.Parameters.AddWithValue("$u", user)
                                    Dim v = cmd.ExecuteScalar()
                                    If v Is Nothing OrElse TypeOf v Is DBNull Then Return Nothing
                                    Return JsonSerializer.Deserialize(Of List(Of String))(CStr(v))
                                End Using
                            End Function
                Dim chatter = perms("chatter")
                For Each p In {"models.use", "api.use", "chat.web", "chat.uploads", "chat.images", "chat.video", "chat.knowledge", "chat.memory", "chat.share", "chat.devices", "account.apikey", "models.own"}
                    Assert.Contains(p, chatter)
                Next
                Assert.DoesNotContain("chat.code", chatter)   ' no tools before: no code interpreter now
                Assert.Contains("chat.code", perms("coder"))
                Assert.Equal({"agents.view", "account.apikey", "models.own"}, perms("viewer").ToArray())
                Assert.Equal({"account.apikey", "models.own"}, perms("nothing").ToArray())
                Assert.Null(perms("boss"))   ' administrators have every permission anyway
            End Using
        Finally
            SqliteConnection.ClearAllPools()
            Try : File.Delete(dbFile) : Catch ex As IOException : End Try
        End Try
    End Sub

    <Fact>
    Public Sub ChatAccessTurnsOffWhatTheAccountMayNotUse()
        Dim u As New AuthUser With {.Name = "u", .Role = "User", .ViaSession = True}
        u.Scopes.Add("read")
        For Each p In {"models.use", "chat.tools", "chat.web"}
            u.Permissions.Add(p)
        Next
        u.AllowedMcpServers.Add("git")
        Dim a = AdminApi.ChatAccessFor(u)
        Assert.True(a.Mcp) : Assert.False(a.Code) : Assert.True(a.Web) : Assert.False(a.Images) : Assert.False(a.Memory) : Assert.False(a.Devices)
        Assert.True(a.AllowsServer("git")) : Assert.False(a.AllowsServer("shell"))
        Dim s As New ChatSettings With {.WebSearch = True, .CodeInterpreter = True, .ImageGeneration = True, .VideoGeneration = True, .Device = "pc-1",
                                        .McpServers = New List(Of String) From {"git", "shell"}, .Knowledge = New List(Of String) From {"kb1"}}
        a.Apply(s)
        Assert.True(s.WebSearch) : Assert.False(s.CodeInterpreter) : Assert.False(s.ImageGeneration) : Assert.False(s.VideoGeneration)
        Assert.Equal({"git"}, s.McpServers.ToArray()) : Assert.Empty(s.Knowledge) : Assert.Null(s.Device)
        ' Administrators may use everything.
        Dim admin As New AuthUser With {.Name = "a", .Role = "Admin", .ViaSession = True}
        admin.Scopes.Add("admin")
        Dim full = AdminApi.ChatAccessFor(admin)
        Assert.True(full.Mcp AndAlso full.Code AndAlso full.Web AndAlso full.Images AndAlso full.Video AndAlso full.Knowledge AndAlso full.Memory AndAlso full.Devices)
        Assert.True(full.AllowsServer("anything"))
    End Sub

    <Fact>
    Public Async Function ChatMetaShowsOnlyWhatTheAccountMayUse() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "basic", .password = "basic-pass-123", .role = "User", .permissions = {"models.use"}}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "full", .password = "full-pass-1234", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Dim basic = Await PlatformApiTests.LoginAsync(env.Port, "basic", "basic-pass-123")
                Dim m = JsonNode.Parse(Await basic.Client.GetStringAsync("/api/chat/meta"))
                For Each flag In {"canShare", "canUpload", "canMemory", "canDevices", "canWebpage", "canCode", "canTools", "canOwnModels"}
                    Assert.False(m(flag).GetValue(Of Boolean)(), flag)
                Next
                ' What the user may not do is refused by the server too.
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(basic.Client, "POST", "/api/chat/memories", New With {.content = "x"}, basic.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(basic.Client, "POST", "/api/chat/fetch-page", New With {.url = "http://example.com"}, basic.Csrf)).StatusCode)
                Dim full = Await PlatformApiTests.LoginAsync(env.Port, "full", "full-pass-1234")
                Dim f = JsonNode.Parse(Await full.Client.GetStringAsync("/api/chat/meta"))
                For Each flag In {"canShare", "canUpload", "canMemory", "canDevices", "canWebpage", "canOwnModels"}
                    Assert.True(f(flag).GetValue(Of Boolean)(), flag)
                Next
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function LimitsRefuseCallsOverTheAccountsLimits() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "limited", .password = "limited-pass-1", .role = "User",
                    .permissions = PlatformApiTests.ChatUser, .requestsPerMinute = 2}, admin.Csrf)).StatusCode)
                Dim users = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/users"))("users").AsArray()
                Assert.Equal(2, users.First(Function(x) x("username").ToString() = "limited")("requestsPerMinute").GetValue(Of Integer)())
                Dim limited = Await PlatformApiTests.LoginAsync(env.Port, "limited", "limited-pass-1")
                Dim key = Await PlatformApiTests.NewKeyAsync(limited.Client, limited.Csrf)
                Using anon As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim ask = Function() PlatformApiTests.Send(anon, "POST", "/v1/chat/completions", New With {.model = "fast/a", .messages = {New With {.role = "user", .content = "hi"}}}, key:=key)
                    Assert.Equal(HttpStatusCode.OK, (Await ask()).StatusCode)
                    Assert.Equal(HttpStatusCode.OK, (Await ask()).StatusCode)
                    Dim third = Await ask()
                    Assert.Equal(CType(429, HttpStatusCode), third.StatusCode)
                    Assert.Contains("per minute", Await third.Content.ReadAsStringAsync())
                    ' A daily token limit below what was used today stops the next call (no per-minute limit now).
                    Assert.True((Await PlatformApiTests.Send(admin.Client, "PATCH", "/api/users/limited", New With {.requestsPerMinute = 0, .dailyTokenLimit = 1}, admin.Csrf)).IsSuccessStatusCode)
                    Await Task.Delay(2200)
                    Dim over = Await ask()
                    Assert.Equal(CType(429, HttpStatusCode), over.StatusCode)
                    Assert.Contains("tokens for today", Await over.Content.ReadAsStringAsync())
                    Assert.True((Await PlatformApiTests.Send(admin.Client, "PATCH", "/api/users/limited", New With {.dailyTokenLimit = Nothing}, admin.Csrf)).IsSuccessStatusCode)
                    Assert.Equal(HttpStatusCode.OK, (Await ask()).StatusCode)
                End Using
                ' A limit must be a whole number from 0.
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "PATCH", "/api/users/limited", New With {.requestsPerMinute = -1}, admin.Csrf)).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function UsersGetOnlyTheirMcpServersForAgents() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                               o.McpServers.Add(New McpServerOptions With {.Id = "git", .Name = "Git", .Enabled = False, .Transport = McpTransportKind.Http, .Endpoint = "http://git.local/mcp"})
                                                               o.McpServers.Add(New McpServerOptions With {.Id = "shell", .Name = "Shell", .Enabled = False, .Transport = McpTransportKind.Http, .Endpoint = "http://shell.local/mcp"})
                                                           End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "maker", .password = "maker-pass-123", .role = "User",
                    .permissions = {"agents.manage", "chat.tools"}, .allowedMcpServers = {"nope"}}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "maker", .password = "maker-pass-123", .role = "User",
                    .permissions = {"agents.manage", "chat.tools"}, .allowedMcpServers = {"GIT"}}, admin.Csrf)).StatusCode)
                Dim listed = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/users"))
                Dim row = listed("users").AsArray().First(Function(x) x("username").ToString() = "maker")
                Assert.Equal("git", row("allowedMcpServers").AsArray().Single().ToString())   ' stored with the server's own id
                Assert.False(row("allMcp").GetValue(Of Boolean)())
                Assert.Equal(2, listed("mcpServers").AsArray().Count)
                Dim maker = Await PlatformApiTests.LoginAsync(env.Port, "maker", "maker-pass-123")
                Dim form = JsonNode.Parse(Await maker.Client.GetStringAsync("/api/meta/agent-form"))
                Assert.Equal("git", form("mcpServers").AsArray().Single()("id").ToString())
                Dim agent = Function(servers As String()) PlatformApiTests.Send(maker.Client, "POST", "/api/agents", New With {
                    .profile = New With {.id = "Helper", .name = "Helper", .baseProfileId = "default", .allowedModels = {"local"}, .allowedMcpServers = servers}}, maker.Csrf)
                Dim refused = Await agent({"shell"})
                Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode)
                Assert.Contains("MCP_NOT_ALLOWED", Await refused.Content.ReadAsStringAsync())
                Assert.Equal(HttpStatusCode.Forbidden, (Await agent({"*"})).StatusCode)
                Assert.Equal(HttpStatusCode.OK, (Await agent({"git"})).StatusCode)
            End Using
        End Using
    End Function

End Class
