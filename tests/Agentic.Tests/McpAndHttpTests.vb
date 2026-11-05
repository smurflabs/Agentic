Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Agentic
Imports Agentic.Configuration
Imports Agentic.Mcp
Imports Agentic.Security
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

Public Class McpIntegrationTests
    Friend Shared Function EchoServerOptions() As McpServerOptions
        ' tests/Agentic.Tests/bin/<cfg>/net10.0 -> samples/EchoMcpServer/bin/<cfg>/net10.0
        Dim baseDir = New DirectoryInfo(AppContext.BaseDirectory)
        Dim cfg = baseDir.Parent.Name
        Dim root = baseDir.Parent.Parent.Parent.Parent.Parent.FullName
        Dim dll = Path.Combine(root, "samples", "EchoMcpServer", "bin", cfg, "net10.0", "EchoMcpServer.dll")
        Assert.True(File.Exists(dll), "Sample MCP server not built: " & dll)
        Return New McpServerOptions With {.Id = "echo", .Name = "Echo", .Enabled = True, .Transport = McpTransportKind.Stdio, .Command = "dotnet",
                                          .Arguments = New List(Of String) From {dll}, .TimeoutSeconds = 30, .ConnectTimeoutSeconds = 30}
    End Function

    <Fact>
    Public Async Function SdkClientTalksToRealStdioServer() As Task
        Dim client As New SdkMcpClient(EchoServerOptions())
        Dim failure As Exception = Nothing
        Try
            Await client.ConnectAsync(CancellationToken.None)
            Dim tools = Await client.ListToolsAsync(CancellationToken.None)
            Assert.Contains(tools, Function(t) t.Name = "echo" AndAlso t.IsIdempotent)
            Assert.Contains(tools, Function(t) t.Name = "add")
            Dim args As JsonElement
            JsonUtil.TryParseElement("{""a"":2,""b"":3}", args)
            Dim r = Await client.CallToolAsync("add", args, CancellationToken.None)
            Assert.False(r.IsError)
            Assert.Equal("5", r.Text.Trim())
            JsonUtil.TryParseElement("{""message"":""boom""}", args)
            Dim f = Await client.CallToolAsync("fail", args, CancellationToken.None)
            Assert.True(f.IsError)
        Catch ex As Exception
            failure = ex
        End Try
        Await client.DisposeAsync()
        If failure IsNot Nothing Then Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw()
    End Function

    <Fact>
    Public Async Function AgentsUseOnlyPermittedMcpTools() As Task
        Dim p As New FakeAIProvider()
        Dim seenTools As New Concurrent.ConcurrentBag(Of String)
        p.Responder = Function(r)
                          If r.Tools IsNot Nothing Then
                              For Each t In r.Tools
                                  seenTools.Add(FakeAIProvider.AgentOf(r) & ":" & t.Name)
                              Next
                          End If
                          If FakeAIProvider.ToolResultCount(r) = 0 Then Return FakeAIProvider.Tool("mcp__echo__echo", "{""text"":""hello""}")
                          Return FakeAIProvider.Complete(FakeAIProvider.LastToolResult(r))
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.McpServers.Add(EchoServerOptions())
                                                   TestHost.Agent(o, "A").AllowedMcpServers = New List(Of String) From {"echo"}
                                                   TestHost.Agent(o, "A").DeniedMcpTools = New List(Of String) From {"echo:fail"}
                                               End Sub, p)
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Mcp.GetStatus().First().State = McpConnectionState.Connected), 30))
            Dim id = Await host.SubmitAsync("A", "Echo something")
            Dim t = Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed, 30)
            Assert.Contains("Echo: hello", t.GetResult().Summary)
            Assert.Contains("A:mcp__echo__echo", seenTools)
            Assert.DoesNotContain("A:mcp__echo__fail", seenTools)
            ' B has no MCP permission at all.
            Dim regB = host.H.ToolCatalog.BuildForAgent(host.H.Profiles.TryGetProfile("B"))
            Assert.DoesNotContain(regB.Names(), Function(n) n.StartsWith("mcp__", StringComparison.Ordinal))
        End Using
    End Function

    <Fact>
    Public Async Function FailingMcpServerDoesNotAffectHost() As Task
        Using host = Await TestHost.StartAsync(Sub(o) o.McpServers.Add(New McpServerOptions With {
            .Id = "broken", .Enabled = True, .Transport = McpTransportKind.Stdio, .Command = "this-command-does-not-exist-xyz", .ConnectTimeoutSeconds = 5}))
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(host.H.Mcp.GetStatus().First().State = McpConnectionState.Faulted), 20))
            Dim id = Await host.SubmitAsync("A", "noop", handler:="noop")
            Await host.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Completed)
        End Using
    End Function
End Class

Public Class HttpApiTests
    Friend Shared Function FreePort() As Integer
        Dim l As New TcpListener(IPAddress.Loopback, 0)
        l.Start()
        Dim port = CType(l.LocalEndpoint, IPEndPoint).Port
        l.Stop()
        Return port
    End Function

    Friend Shared Async Function StartWithHttpAsync() As Task(Of (Host As TestHost, Client As HttpClient, Port As Integer))
        Dim port = FreePort()
        Dim host = Await TestHost.StartAsync(Sub(o)
                                                 o.Server.Enabled = True
                                                 o.Server.Port = port
                                                 o.Admin.Users.Add(New AdminUserOptions With {.Username = "admin", .PasswordHash = PasswordHasher.Hash("pw-123456789")})
                                                 o.Admin.ApiTokens.Add(New ApiTokenOptions With {.Name = "reader", .TokenSha256 = TextUtil.Sha256Hex("reader-token-0123456789"), .Scopes = New List(Of String) From {"read"}})
                                                 o.Admin.ApiTokens.Add(New ApiTokenOptions With {.Name = "submitter", .TokenSha256 = TextUtil.Sha256Hex("submit-token-0123456789"),
                                                                                                 .Scopes = New List(Of String) From {"submit-task"}, .AllowedAgents = New List(Of String) From {"A"}})
                                             End Sub)
        Dim handler As New HttpClientHandler With {.CookieContainer = New CookieContainer(), .UseCookies = True}
        Dim client As New HttpClient(handler) With {.BaseAddress = New Uri($"http://127.0.0.1:{port}")}
        Return (host, client, port)
    End Function

    Friend Shared Function JsonBody(o As Object) As StringContent
        Return New StringContent(JsonUtil.ToJson(o), Encoding.UTF8, "application/json")
    End Function

    <Fact>
    Public Async Function AuthCsrfErrorsAndEndpoints() As Task
        Dim env = Await StartWithHttpAsync()
        Using env.Host
            Using c = env.Client
                ' Health is anonymous; API requires auth with structured errors.
                Assert.Equal(HttpStatusCode.OK, (Await c.GetAsync("/healthz")).StatusCode)
                Dim unauth = Await c.GetAsync("/api/agents")
                Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode)
                Dim err = JsonDocument.Parse(Await unauth.Content.ReadAsStringAsync()).RootElement.GetProperty("error")
                Assert.Equal("UNAUTHORIZED", err.GetProperty("code").GetString())

                Dim bad = Await c.PostAsync("/api/auth/login", JsonBody(New With {.username = "admin", .password = "wrong"}))
                Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode)
                Dim ok = Await c.PostAsync("/api/auth/login", JsonBody(New With {.username = "admin", .password = "pw-123456789"}))
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode)
                Dim csrf = JsonDocument.Parse(Await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString()

                Assert.Equal(HttpStatusCode.OK, (Await c.GetAsync("/api/status")).StatusCode)
                Dim agents = JsonDocument.Parse(Await c.GetStringAsync("/api/agents")).RootElement
                Assert.Equal(3, agents.GetArrayLength())

                ' State change without CSRF token is refused; with it, accepted.
                Dim noCsrf = Await c.PostAsync("/api/agents/A/pause", JsonBody(New With {.x = 1}))
                Assert.Equal(HttpStatusCode.Forbidden, noCsrf.StatusCode)
                Dim req As New HttpRequestMessage(HttpMethod.Post, "/api/agents/A/task") With {.Content = JsonBody(New With {.name = "via api", .handler = "noop"})}
                req.Headers.Add("X-CSRF-Token", csrf)
                Dim created = Await c.SendAsync(req)
                Assert.Equal(HttpStatusCode.Created, created.StatusCode)

                Assert.Equal(HttpStatusCode.NotFound, (Await c.GetAsync("/api/agents/Nobody")).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await c.GetAsync("/api/does-not-exist")).StatusCode)
                Dim page = Await c.GetAsync("/agents/A")
                Assert.Equal(HttpStatusCode.OK, page.StatusCode)
                Assert.Contains("text/html", page.Content.Headers.ContentType.ToString())
                Assert.True(page.Headers.Contains("Content-Security-Policy"))

                ' Audit trail recorded the task submission.
                Dim audit = Await c.GetStringAsync("/api/audit")
                Assert.Contains("submit-task", audit)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ApiTokenScopesAreEnforced() As Task
        Dim env = Await StartWithHttpAsync()
        Using env.Host
            Using c As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                c.DefaultRequestHeaders.Authorization = New Headers.AuthenticationHeaderValue("Bearer", "reader-token-0123456789")
                Assert.Equal(HttpStatusCode.OK, (Await c.GetAsync("/api/agents")).StatusCode)
                Assert.Equal(HttpStatusCode.Forbidden, (Await c.PostAsync("/api/agents/A/pause", JsonBody(New With {.x = 1}))).StatusCode)

                c.DefaultRequestHeaders.Authorization = New Headers.AuthenticationHeaderValue("Bearer", "submit-token-0123456789")
                Assert.Equal(HttpStatusCode.Created, (Await c.PostAsync("/api/agents/A/task", JsonBody(New With {.name = "t", .handler = "noop"}))).StatusCode)
                Assert.Equal(HttpStatusCode.Forbidden, (Await c.PostAsync("/api/agents/B/task", JsonBody(New With {.name = "t", .handler = "noop"}))).StatusCode)

                c.DefaultRequestHeaders.Authorization = New Headers.AuthenticationHeaderValue("Bearer", "not-a-valid-token-xxxxxx")
                Assert.Equal(HttpStatusCode.Unauthorized, (Await c.GetAsync("/api/agents")).StatusCode)
            End Using
            env.Client.Dispose()
        End Using
    End Function

    <Fact>
    Public Async Function ProfitEndpointIsIdempotent() As Task
        Dim env = Await StartWithHttpAsync()
        Using env.Host
            Using c = env.Client
                Dim ok = Await c.PostAsync("/api/auth/login", JsonBody(New With {.username = "admin", .password = "pw-123456789"}))
                Dim csrf = JsonDocument.Parse(Await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("csrfToken").GetString()
                Dim send = Async Function() As Task(Of JsonElement)
                               Dim req As New HttpRequestMessage(HttpMethod.Post, "/api/agents/A/profit") With {
                                   .Content = JsonBody(New With {.amount = 12.5, .reference = "INV-9", .idempotencyKey = "form-123"})}
                               req.Headers.Add("X-CSRF-Token", csrf)
                               Dim resp = Await c.SendAsync(req)
                               Assert.Equal(HttpStatusCode.OK, resp.StatusCode)
                               Return JsonDocument.Parse(Await resp.Content.ReadAsStringAsync()).RootElement
                           End Function
                Dim first = Await send()
                Dim second = Await send()
                Assert.True(first.GetProperty("applied").GetBoolean())
                Assert.True(second.GetProperty("duplicate").GetBoolean())
                Assert.Equal(17500000L, (Await env.Host.H.Wallets.GetAsync("A")).BalanceMicros)
            End Using
        End Using
    End Function
End Class
