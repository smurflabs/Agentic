Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports Xunit

''' <summary>
''' MCP servers added from the web app: a user's own servers (only theirs, only HTTP, header values encrypted), servers for
''' everyone by an administrator, and the browser sign-in (OAuth with PKCE) that comes back to the callback.
''' </summary>
Public Class McpUserServersTests

    Private Shared Function FreePort() As Integer
        Using l As New TcpListener(IPAddress.Loopback, 0)
            l.Start()
            Return CType(l.LocalEndpoint, IPEndPoint).Port
        End Using
    End Function

    <Fact>
    Public Async Function UsersAddTheirOwnServersAndOthersNeverSeeThem() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim perms = PlatformApiTests.ChatUser.Concat({"chat.tools"}).ToArray()
                For Each u In {"alice", "bob"}
                    Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = u, .password = u & "-pass-1234", .role = "User", .permissions = perms}, admin.Csrf)).StatusCode)
                Next
                Dim alice = Await PlatformApiTests.LoginAsync(env.Port, "alice", "alice-pass-1234")
                Dim bob = Await PlatformApiTests.LoginAsync(env.Port, "bob", "bob-pass-1234")
                ' A user adds an HTTP server with a token header; a program on the server is for administrators only.
                Dim saved = Await PlatformApiTests.Send(alice.Client, "POST", "/api/chat/mcp-servers", New With {.name = "Linear", .transport = "http", .url = "http://127.0.0.1:1/mcp",
                                                                                                                  .headers = New Dictionary(Of String, String) From {{"Authorization", "Bearer secret-token-xyz"}}}, alice.Csrf)
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode)
                Dim id = JsonDocument.Parse(Await saved.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
                Assert.Equal("u-alice-linear", id)
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(alice.Client, "POST", "/api/chat/mcp-servers", New With {.name = "shell", .transport = "stdio", .command = "cmd"}, alice.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.Forbidden, (Await PlatformApiTests.Send(alice.Client, "POST", "/api/chat/mcp-servers", New With {.name = "everyone", .url = "http://127.0.0.1:1/x", .shared = True}, alice.Csrf)).StatusCode)
                ' The token is not in the file as text.
                Dim file = Path.Combine(env.Host.DataDir, "mcp-servers.json")
                Assert.True(IO.File.Exists(file))
                Assert.DoesNotContain("secret-token-xyz", IO.File.ReadAllText(file))
                ' Alice sees it (list and the chat's Tools menu); Bob does not, and cannot change it.
                Dim aliceList = Await alice.Client.GetStringAsync("/api/chat/mcp-servers")
                Assert.Contains("u-alice-linear", aliceList)
                Assert.DoesNotContain("secret-token-xyz", aliceList)
                Assert.DoesNotContain("u-alice-linear", Await bob.Client.GetStringAsync("/api/chat/mcp-servers"))
                Assert.Contains("u-alice-linear", Await alice.Client.GetStringAsync("/api/chat/meta"))
                Assert.DoesNotContain("u-alice-linear", Await bob.Client.GetStringAsync("/api/chat/meta"))
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(bob.Client, "DELETE", "/api/chat/mcp-servers/u-alice-linear", Nothing, bob.Csrf)).StatusCode)
                ' An administrator's server for everyone is seen by all; the MCP page lists it as running.
                Assert.Equal(HttpStatusCode.OK, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/mcp-servers", New With {.name = "docs", .url = "http://127.0.0.1:1/docs", .shared = True}, admin.Csrf)).StatusCode)
                Assert.Contains("""docs""", Await bob.Client.GetStringAsync("/api/chat/meta"))
                Assert.Contains("""docs""", Await admin.Client.GetStringAsync("/api/mcp"))
                ' Removing it stops it.
                Assert.Equal(HttpStatusCode.OK, (Await PlatformApiTests.Send(alice.Client, "DELETE", "/api/chat/mcp-servers/u-alice-linear", Nothing, alice.Csrf)).StatusCode)
                Assert.DoesNotContain("u-alice-linear", Await alice.Client.GetStringAsync("/api/chat/meta"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheBrowserSignInStoresATokenForTheServer() As Task
        Dim port = FreePort()
        Dim base = $"http://localhost:{port}"
        Dim listener As New HttpListener()
        listener.Prefixes.Add(base & "/")
        listener.Start()
        Dim codes As New ConcurrentDictionary(Of String, String)()
        Dim serving = Task.Run(Async Function()
                                   While listener.IsListening
                                       Dim ctx As HttpListenerContext
                                       Try
                                           ctx = Await listener.GetContextAsync()
                                       Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                                           Exit While
                                       End Try
                                       Dim body As String
                                       Using r As New StreamReader(ctx.Request.InputStream)
                                           body = Await r.ReadToEndAsync()
                                       End Using
                                       Dim send = Sub(status As Integer, json As String, hdr As String)
                                                      ctx.Response.StatusCode = status
                                                      If hdr IsNot Nothing Then ctx.Response.AddHeader("WWW-Authenticate", hdr)
                                                      Dim b = Encoding.UTF8.GetBytes(json)
                                                      ctx.Response.OutputStream.Write(b, 0, b.Length)
                                                      ctx.Response.Close()
                                                  End Sub
                                       Select Case ctx.Request.Url.AbsolutePath
                                           Case "/mcp" : send(401, "{}", $"Bearer resource_metadata=""{base}/.well-known/oauth-protected-resource/mcp""")
                                           Case "/.well-known/oauth-protected-resource/mcp" : send(200, "{""resource"":""" & base & "/mcp"",""authorization_servers"":[""" & base & """]}", Nothing)
                                           Case "/.well-known/oauth-authorization-server" : send(200, "{""authorization_endpoint"":""" & base & "/authorize"",""token_endpoint"":""" & base & "/token"",""registration_endpoint"":""" & base & "/register""}", Nothing)
                                           Case "/register" : send(201, "{""client_id"":""agentic-1""}", Nothing)
                                           Case "/token"
                                               Dim f = Web.HttpUtility.ParseQueryString(body)
                                               Dim challenge As String = Nothing
                                               Dim hash = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(If(f("code_verifier"), "")))).Replace("+"c, "-"c).Replace("/"c, "_"c).TrimEnd("="c)
                                               If codes.TryGetValue(If(f("code"), ""), challenge) AndAlso challenge = hash Then send(200, "{""access_token"":""tok-777"",""expires_in"":3600}", Nothing) Else send(400, "{""error"":""invalid_grant""}", Nothing)
                                           Case Else : send(404, "{}", Nothing)
                                       End Select
                                   End While
                               End Function)
        Try
            Dim dir = Directory.CreateTempSubdirectory("mcpuser").FullName
            Dim box As New Global.Agentic.Security.SecretBox(RandomNumberGenerator.GetBytes(32))
            Dim manager As New Global.Agentic.Mcp.McpServerManager(New List(Of Global.Agentic.Configuration.McpServerOptions), New Global.Agentic.Mcp.SdkMcpClientFactory(),
                                                                    New Global.Agentic.Events.AgentEventBus(Global.Agentic.Logging.NullLog.Instance), Global.Agentic.Logging.NullLog.Instance, New Global.Agentic.Configuration.LimitsOptions())
            Dim store As New Global.Agentic.Mcp.McpUserServers(dir, box, manager, Global.Agentic.Logging.NullLog.Instance)
            Dim e = store.Save("carol", False, False, "Hosted", "http", base & "/mcp", Nothing, Nothing, Nothing, Nothing, False, Nothing)
            Dim url = Await store.StartSignInAsync(e.Id, "carol", "https://agentic.example/api/chat/mcp-servers/oauth/callback")
            Assert.StartsWith(base & "/authorize?", url)
            Dim q = Web.HttpUtility.ParseQueryString(New Uri(url).Query)
            Assert.Equal("agentic-1", q("client_id"))
            Assert.Equal("S256", q("code_challenge_method"))
            Assert.Equal(base & "/mcp", q("resource"))
            ' The authorization server would send the browser back with a code for this challenge.
            codes("code-1") = q("code_challenge")
            Assert.Equal(e.Id, Await store.FinishSignInAsync(q("state"), "code-1"))
            Assert.True(store.SignedIn(e.Id))
            Assert.Equal("tok-777", Await store.TokenAsync(e.Id))
            Assert.DoesNotContain("tok-777", IO.File.ReadAllText(Path.Combine(dir, "mcp-servers.json")))
            ' The same state cannot be used twice; signing out forgets the token.
            Await Assert.ThrowsAsync(Of ArgumentException)(Function() store.FinishSignInAsync(q("state"), "code-1"))
            store.SignOut(e.Id)
            Assert.Null(Await store.TokenAsync(e.Id))
        Finally
            listener.Stop()
            listener.Close()
        End Try
    End Function

End Class
