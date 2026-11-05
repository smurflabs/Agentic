Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.WebSockets
Imports System.Text
Imports System.Threading
Imports Agentic.Http
Imports Agentic.Work
Imports Xunit

''' <summary>
''' App previews: /container/{id}/ on the host's own port proxies a web app in a sandbox container. Root-absolute URLs stay
''' under the prefix, the page runs isolated (CSP sandbox), and the host's own credentials never reach the app.
''' </summary>
Public Class PreviewProxyTests

    ' ---------------------------------------------------------------- pure parts

    <Fact>
    Public Sub PublishedPortsAreReadFromDockerPs()
        Dim ports = SandboxGateway.ParsePorts("0.0.0.0:8080->80/tcp, [::]:8080->80/tcp, 9000/tcp, 0.0.0.0:53->53/udp, 0.0.0.0:3000->3000/tcp")
        Assert.Equal(2, ports.Count)
        Assert.Equal(8080, ports(0).HostPort)
        Assert.Equal(80, ports(0).ContainerPort)
        Assert.Equal(3000, ports(1).HostPort)
        Assert.Empty(SandboxGateway.ParsePorts("8001/tcp"))
        Assert.Empty(SandboxGateway.ParsePorts(""))
    End Sub

    <Fact>
    Public Sub TheWebPortIsPickedWhenSeveralArePublished()
        Dim ports As New List(Of SandboxPort) From {New SandboxPort With {.HostPort = 9100, .ContainerPort = 5432}, New SandboxPort With {.HostPort = 8100, .ContainerPort = 3000}}
        Assert.Equal(8100, SandboxReaper.PickPort(ports, Nothing).HostPort)
        Assert.Equal(9100, SandboxReaper.PickPort(ports, 5432).HostPort)
        Assert.Equal(9100, SandboxReaper.PickPort(ports, 9100).HostPort)
        Assert.Null(SandboxReaper.PickPort(ports, 1234))
    End Sub

    <Fact>
    Public Sub LinkIdsAreReadableAndUnguessable()
        Dim a = SandboxPreviewStore.NewId("My Shop_API!")
        Dim b = SandboxPreviewStore.NewId("My Shop_API!")
        Assert.Matches("^my-shop-api-[a-z0-9]{22}$", a)
        Assert.NotEqual(a, b)
        Assert.Equal("app", SandboxPreviewStore.Slug("!!!"))
        Assert.Equal(("abc-1", "/x/y?z=1"), PreviewProxy.Split("/container/abc-1/x/y?z=1"))
        Assert.Equal(("abc-1", ""), PreviewProxy.Split("/container/abc-1"))
        Assert.Equal(("abc-1", "?q"), PreviewProxy.Split("/container/abc-1?q"))
    End Sub

    <Fact>
    Public Sub RootAbsoluteUrlsStayUnderThePrefix()
        Const p = "/container/app-x"
        Dim html = "<html><head><title>t</title></head><body>" &
                   "<link rel=""stylesheet"" href=""/static/site.css""><script src=""/static/app.js""></script>" &
                   "<a href=""/about"">a</a><a href=""//cdn.example.com/x"">cdn</a><a href=""https://example.com/"">ext</a><a href=""rel.html"">rel</a>" &
                   "<form action=""/login"" method=""post""></form><img srcset=""/a.png 1x, /b.png 2x"">" &
                   "<div style=""background:url(/bg.png)""></div><a href=""" & p & "/already"">done</a></body></html>"
        Dim s = PreviewRewriter.RewriteHtml(html, p)
        Assert.Contains("href=""" & p & "/static/site.css""", s)
        Assert.Contains("src=""" & p & "/static/app.js""", s)
        Assert.Contains("href=""" & p & "/about""", s)
        Assert.Contains("action=""" & p & "/login""", s)
        Assert.Contains("srcset=""" & p & "/a.png 1x, " & p & "/b.png 2x""", s)
        Assert.Contains("url(" & p & "/bg.png)", s)
        Assert.Contains("href=""//cdn.example.com/x""", s)
        Assert.Contains("href=""https://example.com/""", s)
        Assert.Contains("href=""rel.html""", s)
        Assert.Contains("href=""" & p & "/already""", s)
        Assert.DoesNotContain(p & p, s)
        ' The runtime shim comes first in the head, before the app's own scripts.
        Assert.True(s.IndexOf("<script>(function(){var P=", StringComparison.Ordinal) < s.IndexOf("<title>", StringComparison.Ordinal))
        Assert.Contains("url(" & p & "/f.woff2)", PreviewRewriter.RewriteCss("@font-face{src:url('/f.woff2')}", p).Replace("'", ""))
        Assert.Contains("@import """ & p & "/base.css""", PreviewRewriter.RewriteCss("@import ""/base.css"";", p))
    End Sub

    <Fact>
    Public Sub RedirectsAndCookiesStayWithTheApp()
        Const p = "/container/app-x"
        Assert.Equal(p & "/login?next=1", PreviewProxy.RewriteLocation("/login?next=1", p, "http://10.0.0.167:8100"))
        Assert.Equal(p & "/x", PreviewProxy.RewriteLocation("http://localhost:5000/x", p, "http://10.0.0.167:8100"))
        Assert.Equal(p & "/y", PreviewProxy.RewriteLocation("http://10.0.0.167:8100/y", p, "http://10.0.0.167:8100"))
        Assert.Equal("https://example.com/z", PreviewProxy.RewriteLocation("https://example.com/z", p, "http://10.0.0.167:8100"))
        Assert.Equal("sid=1; Path=" & p & "/; HttpOnly", PreviewProxy.RewriteSetCookie("sid=1; Path=/; Domain=example.com; HttpOnly", p & "/"))
        Assert.Equal("t=2; Path=" & p & "/", PreviewProxy.RewriteSetCookie("t=2", p & "/"))
        Assert.Null(PreviewProxy.RewriteSetCookie("agentic_session=evil; Path=/", p & "/"))
        Assert.Equal("a=1; b=2", PreviewProxy.AppCookies("agentic_session=secret; a=1; agentic_device=x; b=2"))
    End Sub

    ' ---------------------------------------------------------------- through the host

    ''' <summary>A tiny web app: a page with root-absolute links, a stylesheet, a redirect, an echo of what it received, and a WebSocket echo.</summary>
    Private NotInheritable Class FakeApp
        Implements IDisposable
        Private ReadOnly _listener As New HttpListener()
        Public ReadOnly Property Port As Integer
        Public LastHeaders As Specialized.NameValueCollection

        Public Sub New()
            Port = HttpApiTests.FreePort()
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/")
            _listener.Start()
            Dim unused = Task.Run(AddressOf LoopAsync)
        End Sub

        Private Async Function LoopAsync() As Task
            While _listener.IsListening
                Dim ctx As HttpListenerContext
                Try
                    ctx = Await _listener.GetContextAsync()
                Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is InvalidOperationException
                    Return
                End Try
                Dim unused = Task.Run(Function() ServeAsync(ctx))
            End While
        End Function

        Private Async Function ServeAsync(ctx As HttpListenerContext) As Task
            Try
                Dim path = ctx.Request.Url.AbsolutePath
                If ctx.Request.IsWebSocketRequest Then
                    Dim ws = (Await ctx.AcceptWebSocketAsync(Nothing)).WebSocket
                    Dim buf(1023) As Byte
                    Dim r = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buf), CancellationToken.None)
                    Await ws.SendAsync(New ArraySegment(Of Byte)(Encoding.UTF8.GetBytes("echo:" & Encoding.UTF8.GetString(buf, 0, r.Count))), WebSocketMessageType.Text, True, CancellationToken.None)
                    Await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None)
                    Return
                End If
                LastHeaders = New Specialized.NameValueCollection(ctx.Request.Headers)
                Dim body = ""
                Using r As New StreamReader(ctx.Request.InputStream)
                    body = Await r.ReadToEndAsync()
                End Using
                Select Case path
                    Case "/"
                        Await WriteAsync(ctx, 200, "text/html; charset=utf-8", "<html><head><title>Shop</title><link rel=""stylesheet"" href=""/static/site.css""></head><body><a href=""/about"">About</a></body></html>",
                                         {("Set-Cookie", "sid=abc; Path=/"), ("Content-Security-Policy", "default-src 'self'")})
                    Case "/static/site.css"
                        Await WriteAsync(ctx, 200, "text/css", "body{background:url(/img/bg.png)}")
                    Case "/redirect"
                        ctx.Response.RedirectLocation = "/login"
                        Await WriteAsync(ctx, 302, "text/plain", "")
                    Case "/echo"
                        Await WriteAsync(ctx, 200, "text/plain", $"{ctx.Request.HttpMethod} {body}")
                    Case Else
                        Await WriteAsync(ctx, 404, "text/plain", "nope")
                End Select
            Catch ex As Exception When TypeOf ex Is HttpListenerException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is WebSocketException
            End Try
        End Function

        Private Shared Async Function WriteAsync(ctx As HttpListenerContext, status As Integer, type As String, text As String, Optional headers As (String, String)() = Nothing) As Task
            ctx.Response.StatusCode = status
            ctx.Response.ContentType = type
            For Each h In If(headers, Array.Empty(Of (String, String))())
                ctx.Response.AppendHeader(h.Item1, h.Item2)
            Next
            Dim bytes = Encoding.UTF8.GetBytes(text)
            ctx.Response.ContentLength64 = bytes.Length
            Await ctx.Response.OutputStream.WriteAsync(bytes)
            ctx.Response.Close()
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Try
                _listener.Stop()
                _listener.Close()
            Catch
            End Try
        End Sub
    End Class

    <Fact>
    Public Async Function AnAppOpensUnderContainerOnTheHostsOwnPort() As Task
        Using up As New FakeUpstream(), app As New FakeApp()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim h = env.Host.H
                ' In the test the "container" is the fake app on this machine; whether it still runs is checked by the stub.
                Dim why As String = Nothing
                h.PreviewProxy.Validate = Function(p, ct) Task.FromResult(why)
                Dim preview = Await h.SandboxPreviews.CreateAsync("admin", Nothing, "cid-1", "shop", "127.0.0.1", app.Port, 80, 24, DateTime.UtcNow)
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim c = admin.Client
                Dim prefix = "/container/" & preview.Id

                ' Without the trailing slash: sent to it, so relative links resolve.
                Using noRedirect As New HttpClient(New HttpClientHandler With {.AllowAutoRedirect = False}) With {.BaseAddress = c.BaseAddress}
                    Dim bare = Await noRedirect.GetAsync(prefix)
                    Assert.Equal(CType(308, HttpStatusCode), bare.StatusCode)
                    Assert.Equal(prefix & "/", bare.Headers.Location.OriginalString)
                    ' A redirect inside the app stays under the prefix.
                    Dim redirect = Await noRedirect.GetAsync(prefix & "/redirect")
                    Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode)
                    Assert.Equal(prefix & "/login", redirect.Headers.Location.OriginalString)
                End Using

                ' The page: rewritten, isolated, no cookie of the app's (it cannot use one as an isolated page), no referrer.
                Dim page = Await c.GetAsync(prefix & "/")
                Dim html = Await page.Content.ReadAsStringAsync()
                Assert.Equal(HttpStatusCode.OK, page.StatusCode)
                Assert.Contains("href=""" & prefix & "/static/site.css""", html)
                Assert.Contains("href=""" & prefix & "/about""", html)
                Assert.Contains("var P=""" & prefix & """", html)
                Dim csp = page.Headers.GetValues("Content-Security-Policy").Single()
                Assert.StartsWith("sandbox ", csp)
                Assert.DoesNotContain("allow-same-origin", csp)
                Assert.Equal("no-referrer", page.Headers.GetValues("Referrer-Policy").Single())
                Assert.False(page.Headers.Contains("Set-Cookie"))
                Assert.False(page.Headers.Contains("X-Frame-Options"))

                Dim css = Await c.GetStringAsync(prefix & "/static/site.css")
                Assert.Contains("url(" & prefix & "/img/bg.png)", css)

                ' The host's session cookie, API key and CSRF token never reach the app; it learns the prefix.
                Dim echoReq As New HttpRequestMessage(HttpMethod.Post, prefix & "/echo") With {.Content = New StringContent("hello")}
                echoReq.Headers.Add("Authorization", "Bearer agentic-secret-key")
                echoReq.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim echo = Await c.SendAsync(echoReq)
                Assert.Equal("POST hello", Await echo.Content.ReadAsStringAsync())
                Assert.Null(app.LastHeaders("Cookie"))
                Assert.Null(app.LastHeaders("Authorization"))
                Assert.Null(app.LastHeaders("X-CSRF-Token"))
                Assert.Equal(prefix, app.LastHeaders("X-Forwarded-Prefix"))
                Assert.Equal($"http://127.0.0.1:{app.Port}", app.LastHeaders("Origin"))

                ' WebSockets go through too (live reload, Streamlit…).
                Using ws As New ClientWebSocket()
                    Await ws.ConnectAsync(New Uri($"ws://127.0.0.1:{env.Port}{prefix}/ws"), CancellationToken.None)
                    Await ws.SendAsync(New ArraySegment(Of Byte)(Encoding.UTF8.GetBytes("ping")), WebSocketMessageType.Text, True, CancellationToken.None)
                    Dim buf(255) As Byte
                    Dim got = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buf), CancellationToken.None)
                    Assert.Equal("echo:ping", Encoding.UTF8.GetString(buf, 0, got.Count))
                End Using

                ' The app stopped: a clear page, not a hang.
                why = "This app is not running any more."
                Dim gone = Await c.GetAsync(prefix & "/")
                Assert.Equal(HttpStatusCode.BadGateway, gone.StatusCode)
                Assert.Contains("not running any more", Await gone.Content.ReadAsStringAsync())
                why = Nothing

                ' Unknown, revoked and expired links open nothing.
                Assert.Equal(HttpStatusCode.NotFound, (Await c.GetAsync("/container/nope-aaaaaaaaaaaaaaaaaaaaaa/")).StatusCode)
                Await h.SandboxPreviews.RevokeAsync(preview.Id, DateTime.UtcNow)
                Assert.Equal(HttpStatusCode.NotFound, (Await c.GetAsync(prefix & "/")).StatusCode)
                Dim old = Await h.SandboxPreviews.CreateAsync("admin", Nothing, "cid-2", "old", "127.0.0.1", app.Port, 80, 1, DateTime.UtcNow.AddHours(-2))
                Assert.Equal(HttpStatusCode.NotFound, (Await c.GetAsync("/container/" & old.Id & "/")).StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub TheForwardedPortRangeIsKnownAndFreePortsAreSuggested()
        Assert.Equal((20000, 20499), SandboxReaper.ParsePortRange(" 20000 - 20499 ").Value)
        Assert.Equal((8100, 8100), SandboxReaper.ParsePortRange("8100").Value)
        Assert.Null(SandboxReaper.ParsePortRange(""))
        Assert.Null(SandboxReaper.ParsePortRange("20499-20000"))
        Assert.Null(SandboxReaper.ParsePortRange("1-65535"))
        Assert.Null(SandboxReaper.ParsePortRange("abc"))
        Dim free = SandboxReaper.FreePorts((20000, 20499), {20000, 20002, 8080}, 3)
        Assert.Equal({20001, 20003, 20004}, free.ToArray())
        Assert.Empty(SandboxReaper.FreePorts(Nothing, {}, 3))
        Dim note = ChatService.PreviewNoteFor((20000, 20499), free)
        Assert.Contains("a host port in 20000-20499", note)
        Assert.Contains("free now: 20001, 20003, 20004", note)
        Assert.Contains("a free host port", ChatService.PreviewNoteFor(Nothing, New List(Of Integer)))
    End Sub

    <Fact>
    Public Sub CurlOutputIsReadIntoStatusHeadersAndBody()
        Dim head = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue" & vbCrLf & vbCrLf & "HTTP/1.1 201 Created" & vbCrLf & "Content-Type: image/png" & vbCrLf & "X-App: 1" & vbCrLf & vbCrLf)
        Dim png = New Byte() {137, 80, 78, 71, 0, 13, 10, 13, 10, 255}
        Dim raw = head.Concat(png).ToArray()
        Dim r = SandboxGateway.ParseHttpResponse(raw)
        Assert.Null(r.ErrorText)
        Assert.Equal(201, r.Status)
        Assert.Contains(r.Headers, Function(h) h.Key = "X-App" AndAlso h.Value = "1")
        Assert.Equal(png, r.Body)
        Assert.NotNull(SandboxGateway.ParseHttpResponse(Encoding.ASCII.GetBytes("garbage")).ErrorText)
    End Sub

    <Fact>
    Public Async Function AppsTheHostCannotReachGoThroughTheSandboxConnection() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim h = env.Host.H
                h.PreviewProxy.Validate = Function(p, ct) Task.FromResult(Of String)(Nothing)
                ' What went through the "sandbox", and the app's answer from there.
                Dim seen As (Method As String, Path As String, Headers As List(Of KeyValuePair(Of String, String)), Body As Byte()) = Nothing
                h.PreviewProxy.Tunnel = Function(p, method, path, headers, body, ct)
                                            seen = (method, path, headers, body)
                                            Dim r As New SandboxHttpResponse With {.Status = 200, .Body = Encoding.UTF8.GetBytes("<html><head></head><body><a href=""/about"">a</a></body></html>")}
                                            r.Headers.Add(New KeyValuePair(Of String, String)("Content-Type", "text/html; charset=utf-8"))
                                            r.Headers.Add(New KeyValuePair(Of String, String)("Transfer-Encoding", "chunked"))
                                            Return Task.FromResult(r)
                                        End Function
                ' A port nobody listens on: the direct attempt fails and the link switches to the sandbox connection.
                Dim closed = HttpApiTests.FreePort()
                Dim preview = Await h.SandboxPreviews.CreateAsync("admin", Nothing, "cid-9", "wsl", "127.0.0.1", closed, 80, 24, DateTime.UtcNow)
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim prefix = "/container/" & preview.Id
                Dim req As New HttpRequestMessage(HttpMethod.Post, prefix & "/form?x=1") With {.Content = New StringContent("a=1", Encoding.UTF8, "application/x-www-form-urlencoded")}
                req.Headers.Add("Authorization", "Bearer agentic-secret-key")
                Dim page = Await admin.Client.SendAsync(req)
                Dim html = Await page.Content.ReadAsStringAsync()
                Assert.Equal(HttpStatusCode.OK, page.StatusCode)
                Assert.Contains("href=""" & prefix & "/about""", html)
                Assert.StartsWith("sandbox ", page.Headers.GetValues("Content-Security-Policy").Single())
                Assert.Equal("POST", seen.Method)
                Assert.Equal("/form?x=1", seen.Path)
                Assert.Equal("a=1", Encoding.UTF8.GetString(seen.Body))
                Assert.Contains(seen.Headers, Function(x) x.Key = "Content-Type" AndAlso x.Value.StartsWith("application/x-www-form-urlencoded"))
                Assert.DoesNotContain(seen.Headers, Function(x) x.Key = "Cookie" OrElse x.Key = "Authorization")
                Assert.Contains(seen.Headers, Function(x) x.Key = "X-Forwarded-Prefix" AndAlso x.Value = prefix)
                Assert.Equal("mcp", (Await h.SandboxPreviews.GetAsync(preview.Id)).Transport)
                ' Live connections need the port itself: refused cleanly through the sandbox connection.
                Using ws As New ClientWebSocket()
                    Await Assert.ThrowsAnyAsync(Of WebSocketException)(Function() ws.ConnectAsync(New Uri($"ws://127.0.0.1:{env.Port}{prefix}/ws"), CancellationToken.None))
                End Using
                ' The app's answer failed in the sandbox: a page that says so.
                h.PreviewProxy.Tunnel = Function(p, method, path, headers, body, ct) Task.FromResult(New SandboxHttpResponse With {.ErrorText = "connection refused"})
                Dim down = Await admin.Client.GetAsync(prefix & "/")
                Assert.Equal(HttpStatusCode.BadGateway, down.StatusCode)
                Assert.Contains("connection refused", Await down.Content.ReadAsStringAsync())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function PreviewsNeedTheCodeToolsAndAreOffWithoutASandbox() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' No code server in the test host: nothing to list, and asking for a link explains why.
                Dim apps = Await PlatformApiTests.Send(admin.Client, "GET", "/api/sandbox/apps", Nothing, admin.Csrf)
                Assert.Equal(HttpStatusCode.OK, apps.StatusCode)
                Assert.Contains("""apps"":[]", Await apps.Content.ReadAsStringAsync())
                Dim make = Await PlatformApiTests.Send(admin.Client, "POST", "/api/sandbox/apps/preview", New With {.container = "shop"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, make.StatusCode)
            End Using
        End Using
    End Function

End Class
