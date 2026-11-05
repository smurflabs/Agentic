Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.WebSockets
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ''' <summary>
    ''' App previews: /container/{id}/… on the host's own port is a reverse proxy to a web app running in a sandbox container
    ''' (its published port on the sandbox's Docker host). The id is the link's secret. Requests and WebSockets are passed
    ''' through; the host's own session cookie, API key and CSRF token never are. With isolation "sandbox" (the default) every
    ''' response carries Content-Security-Policy: sandbox, so the page runs as an opaque origin: its scripts cannot use the
    ''' signed-in user's Agentic session (the session cookie is SameSite=Strict and is not sent from it), whatever the app does.
    ''' Root-absolute URLs (/static/app.js, fetch('/api'), redirects to /login) are rewritten to stay under the prefix.
    ''' </summary>
    Public NotInheritable Class PreviewProxy
        Private ReadOnly _store As SandboxPreviewStore
        Private ReadOnly _log As ILog
        Private ReadOnly _http As HttpClient

        ''' <summary>sandbox (isolated origin, default) or none (the app runs as this site; trusted apps only).</summary>
        Public Property Isolation As String = "sandbox"
        ''' <summary>Confirms the link's container still runs and still publishes the port: Nothing when fine, else why not.</summary>
        Public Property Validate As Func(Of SandboxPreview, CancellationToken, Task(Of String))
        ''' <summary>Largest HTML or CSS page rewritten (bigger ones pass through unchanged).</summary>
        Public Property MaxRewriteBytes As Integer = 8 * 1024 * 1024

        Public Const PathPrefix As String = "/container/"

        Public Sub New(store As SandboxPreviewStore, log As ILog, Optional handler As HttpMessageHandler = Nothing)
            _store = store
            _log = log
            _http = New HttpClient(If(handler, New SocketsHttpHandler With {
                .AllowAutoRedirect = False, .UseCookies = False, .AutomaticDecompression = DecompressionMethods.None,
                .ConnectTimeout = TimeSpan.FromSeconds(10), .PooledConnectionLifetime = TimeSpan.FromMinutes(2)})) With {.Timeout = Timeout.InfiniteTimeSpan}
        End Sub

        Private ReadOnly Property Isolated As Boolean
            Get
                Return Not String.Equals(Isolation, "none", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        ''' <summary>The link id and the rest of the URL (path and query, starting with "/", or "" when missing) of a /container/ request.</summary>
        Friend Shared Function Split(rawUrl As String) As (Id As String, Tail As String)
            Dim s = If(rawUrl, "")
            If Not s.StartsWith(PathPrefix, StringComparison.OrdinalIgnoreCase) Then Return (Nothing, Nothing)
            s = s.Substring(PathPrefix.Length)
            Dim cut = s.IndexOfAny({"/"c, "?"c})
            If cut < 0 Then Return (s, "")
            Return (s.Substring(0, cut), s.Substring(cut))
        End Function

        Public Async Function HandleAsync(rc As RequestContext) As Task
            Dim req = rc.Http.Request
            Dim resp = rc.Http.Response
            Dim parts = Split(req.RawUrl)
            If String.IsNullOrEmpty(parts.Id) OrElse Not Regex.IsMatch(parts.Id, "^[a-z0-9-]{10,80}$") Then
                Await WritePageAsync(rc, 404, "This app link is not valid.").ConfigureAwait(False)
                Return
            End If
            Dim prefix = PathPrefix & parts.Id
            ' /container/id → /container/id/ so the app's relative links resolve under it.
            If parts.Tail.Length = 0 OrElse parts.Tail.StartsWith("?", StringComparison.Ordinal) Then
                resp.StatusCode = 308
                resp.RedirectLocation = prefix & "/" & parts.Tail
                rc.MarkResponded()
                Return
            End If
            Dim now = DateTime.UtcNow
            Dim preview = Await _store.GetAsync(parts.Id).ConfigureAwait(False)
            If preview Is Nothing OrElse Not preview.IsLive(now) Then
                Await WritePageAsync(rc, 404, "This app link has expired or was closed. Ask the assistant (or open Sandbox → Running apps) for a new one.").ConfigureAwait(False)
                Return
            End If
            If Validate IsNot Nothing Then
                Dim why = Await Validate(preview, rc.CancellationToken).ConfigureAwait(False)
                If why IsNot Nothing Then
                    Await WritePageAsync(rc, 502, why).ConfigureAwait(False)
                    Return
                End If
            End If
            If Not preview.LastUsedUtc.HasValue OrElse now - preview.LastUsedUtc.Value > TimeSpan.FromMinutes(1) Then
                Dim unused = _store.TouchAsync(preview.Id, now)
            End If
            Dim target = $"http://{preview.TargetHost}:{preview.TargetPort}"
            ' mcp: this host cannot reach the port (Docker in WSL2 on Windows publishes ports only inside its VM), so each request
            ' goes through the sandbox connection instead.
            Dim tunnelled = Tunnel IsNot Nothing AndAlso String.Equals(preview.Transport, "mcp", StringComparison.OrdinalIgnoreCase)
            If req.IsWebSocketRequest Then
                If tunnelled Then
                    ' Live connections need the port itself; the page still works without its live updates.
                    rc.Http.Response.StatusCode = 502
                    rc.MarkResponded()
                    Return
                End If
                Await BridgeWebSocketAsync(rc, preview, parts.Tail, prefix).ConfigureAwait(False)
                Return
            End If
            ' Under an isolated origin the app's own fetches are cross-origin: answer their preflights here (the app may not).
            If Isolated AndAlso rc.Method = "OPTIONS" AndAlso Not String.IsNullOrEmpty(req.Headers("Access-Control-Request-Method")) Then
                resp.StatusCode = 204
                ApplyHeaders(resp)
                resp.Headers("Access-Control-Allow-Methods") = req.Headers("Access-Control-Request-Method")
                If Not String.IsNullOrEmpty(req.Headers("Access-Control-Request-Headers")) Then resp.Headers("Access-Control-Allow-Headers") = req.Headers("Access-Control-Request-Headers")
                resp.Headers("Access-Control-Max-Age") = "600"
                rc.MarkResponded()
                Return
            End If

            Dim headers = ForwardHeaders(req, target, prefix, Isolated)
            ' The body is read once, so a request that cannot go straight to the app can still go through the sandbox.
            Dim bodyBytes As Byte() = Nothing
            If req.HasEntityBody AndAlso rc.Method <> "GET" AndAlso rc.Method <> "HEAD" Then
                bodyBytes = Await ReadBodyAsync(req.InputStream, MaxBodyBytes, rc.CancellationToken).ConfigureAwait(False)
                If bodyBytes Is Nothing Then
                    Await WritePageAsync(rc, 413, $"The upload is larger than {MaxBodyBytes \ (1024 * 1024)} MB, more than an app preview passes on.").ConfigureAwait(False)
                    Return
                End If
                If Not String.IsNullOrEmpty(req.ContentType) Then headers.Add(New KeyValuePair(Of String, String)("Content-Type", req.ContentType))
            End If

            If Not tunnelled Then
                Dim answer = Await SendDirectAsync(rc.Method, target & parts.Tail, headers, bodyBytes, rc.CancellationToken).ConfigureAwait(False)
                If answer IsNot Nothing Then
                    Using answer
                        Dim all As New List(Of KeyValuePair(Of String, String))
                        For Each h In answer.Headers
                            For Each v In h.Value
                                all.Add(New KeyValuePair(Of String, String)(h.Key, v))
                            Next
                        Next
                        For Each h In answer.Content.Headers
                            For Each v In h.Value
                                all.Add(New KeyValuePair(Of String, String)(h.Key, v))
                            Next
                        Next
                        Using body = Await answer.Content.ReadAsStreamAsync(rc.CancellationToken).ConfigureAwait(False)
                            Await WriteResponseAsync(rc, CInt(answer.StatusCode), all, body, answer.Content.Headers.ContentLength, target, prefix).ConfigureAwait(False)
                        End Using
                    End Using
                    Return
                End If
                If Tunnel Is Nothing Then
                    Await WritePageAsync(rc, 502, $"The app is not answering on port {preview.TargetPort}. Is it running, and listening on 0.0.0.0 (not only 127.0.0.1) inside its container?").ConfigureAwait(False)
                    Return
                End If
                ' This host cannot reach the port: through the sandbox from now on.
                preview.Transport = "mcp"
                Dim unused = _store.SetTransportAsync(preview.Id, "mcp")
            End If
            Dim tunnelAnswer = Await Tunnel(preview, rc.Method, parts.Tail, headers, bodyBytes, rc.CancellationToken).ConfigureAwait(False)
            If tunnelAnswer Is Nothing OrElse tunnelAnswer.ErrorText IsNot Nothing Then
                Await WritePageAsync(rc, 502, $"The app is not answering on port {preview.TargetPort} ({If(tunnelAnswer?.ErrorText, "no answer")}). Is it running, and listening on 0.0.0.0 inside its container?").ConfigureAwait(False)
                Return
            End If
            Using ms As New MemoryStream(tunnelAnswer.Body)
                Await WriteResponseAsync(rc, tunnelAnswer.Status, tunnelAnswer.Headers, ms, tunnelAnswer.Body.LongLength, target, prefix).ConfigureAwait(False)
            End Using
        End Function

        ''' <summary>Largest request body passed on (uploads to the app).</summary>
        Public Property MaxBodyBytes As Integer = 32 * 1024 * 1024

        ''' <summary>
        ''' Through the sandbox connection: (link, method, path and query, headers, body) → the app's answer. Set by the host; used
        ''' when this host cannot reach the published port.
        ''' </summary>
        Public Property Tunnel As Func(Of SandboxPreview, String, String, List(Of KeyValuePair(Of String, String)), Byte(), CancellationToken, Task(Of SandboxHttpResponse))

        Private Shared Async Function ReadBodyAsync(input As Stream, max As Integer, ct As CancellationToken) As Task(Of Byte())
            Using ms As New MemoryStream()
                Dim buf(81919) As Byte
                While True
                    Dim n = Await input.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(False)
                    If n = 0 Then Exit While
                    ms.Write(buf, 0, n)
                    If ms.Length > max Then Return Nothing
                End While
                Return ms.ToArray()
            End Using
        End Function

        ''' <summary>The request straight to the published port; Nothing when this host cannot reach it.</summary>
        Private Async Function SendDirectAsync(method As String, url As String, headers As List(Of KeyValuePair(Of String, String)), body As Byte(), ct As CancellationToken) As Task(Of HttpResponseMessage)
            Using upstream As New HttpRequestMessage(New HttpMethod(method), url)
                For Each h In headers
                    If String.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase) Then Continue For
                    upstream.Headers.TryAddWithoutValidation(h.Key, h.Value)
                Next
                If body IsNot Nothing Then
                    upstream.Content = New ByteArrayContent(body)
                    Dim type = headers.FirstOrDefault(Function(h) String.Equals(h.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value
                    If type IsNot Nothing Then upstream.Content.Headers.TryAddWithoutValidation("Content-Type", type)
                End If
                Using headerWait = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    headerWait.CancelAfter(TimeSpan.FromSeconds(120))
                    Try
                        Return Await _http.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, headerWait.Token).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse (TypeOf ex Is OperationCanceledException AndAlso Not ct.IsCancellationRequested)
                        Return Nothing
                    End Try
                End Using
            End Using
        End Function

        ' ---------------------------------------------------------------- requests

        Private Shared ReadOnly HopByHop As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
            "Proxy-Connection"}
        ''' <summary>Never passed to the app: the host's own credentials and request plumbing.</summary>
        Private Shared ReadOnly NeverForward As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "Host", "Cookie", "Authorization", "X-CSRF-Token", "X-Vscode-Token", "X-Desktop-Token", "Accept-Encoding", "Origin", "Referer",
            "Content-Length", "Content-Type", "Expect", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-Prefix"}

        ''' <summary>The headers the app gets: the browser's, minus the host's credentials and plumbing, plus where it is served from.</summary>
        Friend Shared Function ForwardHeaders(req As HttpListenerRequest, target As String, prefix As String, isolated As Boolean) As List(Of KeyValuePair(Of String, String))
            Dim list As New List(Of KeyValuePair(Of String, String))
            Dim add = Sub(n As String, v As String)
                          If Not String.IsNullOrEmpty(v) Then list.Add(New KeyValuePair(Of String, String)(n, v))
                      End Sub
            For Each name As String In req.Headers.AllKeys
                If name Is Nothing OrElse HopByHop.Contains(name) OrElse NeverForward.Contains(name) Then Continue For
                If name.StartsWith("X-Agentic", StringComparison.OrdinalIgnoreCase) OrElse name.StartsWith("Sec-WebSocket", StringComparison.OrdinalIgnoreCase) Then Continue For
                add(name, req.Headers(name))
            Next
            ' The app's own cookies (only when it runs as this site); the host's own (agentic_*) never.
            If Not isolated Then
                Dim cookies = AppCookies(req.Headers("Cookie"))
                If cookies.Length > 0 Then add("Cookie", cookies)
            End If
            ' Uncompressed, so pages can be rewritten; the app sees itself as the origin (its CSRF and CORS checks pass).
            add("Accept-Encoding", "identity")
            add("Origin", target)
            Dim referer = req.Headers("Referer")
            If Not String.IsNullOrEmpty(referer) Then
                Dim i = referer.IndexOf(prefix, StringComparison.Ordinal)
                If i >= 0 Then add("Referer", target & referer.Substring(i + prefix.Length))
            End If
            add("X-Forwarded-For", req.RemoteEndPoint?.Address?.ToString())
            add("X-Forwarded-Host", req.Headers("Host"))
            add("X-Forwarded-Proto", If(req.IsSecureConnection, "https", "http"))
            add("X-Forwarded-Prefix", prefix)
            Return list
        End Function

        ''' <summary>The Cookie header without the host's own cookies (names starting with "agentic").</summary>
        Friend Shared Function AppCookies(header As String) As String
            If String.IsNullOrWhiteSpace(header) Then Return ""
            Return String.Join("; ", header.Split(";"c).Select(Function(c) c.Trim()).
                Where(Function(c) c.Length > 0 AndAlso Not c.StartsWith("agentic", StringComparison.OrdinalIgnoreCase)))
        End Function

        ' ---------------------------------------------------------------- answers

        ''' <summary>Headers every preview answer gets (and the error pages): the isolation, no referrer (the link is a secret), CORS.</summary>
        Private Sub ApplyHeaders(resp As HttpListenerResponse)
            resp.Headers("X-Content-Type-Options") = "nosniff"
            ' The link's id must not leak to other sites through the Referer header.
            resp.Headers("Referrer-Policy") = "no-referrer"
            If Isolated Then
                resp.Headers("Content-Security-Policy") = SandboxPolicy
                ' The page's own requests come from an opaque origin ("null"): let it read the answers (never with credentials).
                resp.Headers("Access-Control-Allow-Origin") = "*"
            End If
        End Sub

        Friend Const SandboxPolicy As String = "sandbox allow-scripts allow-forms allow-popups allow-popups-to-escape-sandbox allow-modals allow-downloads allow-pointer-lock allow-presentation"

        ''' <summary>The app's answer to the browser: its headers (filtered and rewritten), then its body (HTML and CSS rewritten).</summary>
        Private Async Function WriteResponseAsync(rc As RequestContext, status As Integer, headers As List(Of KeyValuePair(Of String, String)), body As Stream,
                                                  length As Long?, target As String, prefix As String) As Task
            Dim resp = rc.Http.Response
            resp.StatusCode = status
            Dim contentType As String = Nothing
            Dim encoded = False
            For Each h In headers
                Dim name = h.Key
                If HopByHop.Contains(name) Then Continue For
                Select Case name.ToLowerInvariant()
                    Case "content-type"
                        contentType = h.Value
                        Continue For
                    Case "content-length", "server", "date", "strict-transport-security", "referrer-policy", "access-control-allow-origin",
                         "access-control-allow-credentials", "alt-svc"
                        Continue For
                    Case "content-encoding"
                        encoded = Not String.Equals(h.Value, "identity", StringComparison.OrdinalIgnoreCase)
                    Case "content-security-policy"
                        If Isolated Then Continue For
                    Case "location"
                        resp.RedirectLocation = RewriteLocation(h.Value, prefix, target)
                        Continue For
                    Case "set-cookie"
                        If Isolated Then Continue For
                        Dim rewritten = RewriteSetCookie(h.Value, prefix & "/")
                        If rewritten IsNot Nothing Then resp.AppendHeader("Set-Cookie", rewritten)
                        Continue For
                End Select
                Try
                    resp.AppendHeader(name, h.Value)
                Catch ex As ArgumentException
                End Try
            Next
            ApplyHeaders(resp)
            If contentType IsNot Nothing Then resp.ContentType = contentType
            rc.MarkResponded()
            If rc.Method = "HEAD" OrElse status = 204 OrElse status = 304 Then Return
            Dim kind = If(contentType, "").ToLowerInvariant()
            ' A compressed page cannot be rewritten (the app ignored Accept-Encoding: identity): it passes through as it is.
            Dim rewrite = Not encoded AndAlso (kind.StartsWith("text/html") OrElse kind.StartsWith("application/xhtml") OrElse kind.StartsWith("text/css"))
            If rewrite AndAlso (Not length.HasValue OrElse length.Value <= MaxRewriteBytes) Then
                Dim buffer As New MemoryStream()
                Dim chunk(81919) As Byte
                Dim tooBig = False
                While True
                    Dim n = Await body.ReadAsync(chunk, 0, chunk.Length, rc.CancellationToken).ConfigureAwait(False)
                    If n = 0 Then Exit While
                    buffer.Write(chunk, 0, n)
                    If buffer.Length > MaxRewriteBytes Then tooBig = True : Exit While
                End While
                If Not tooBig Then
                    Dim enc = EncodingOf(contentType)
                    Dim text = enc.GetString(buffer.ToArray())
                    text = If(kind.StartsWith("text/css"), PreviewRewriter.RewriteCss(text, prefix), PreviewRewriter.RewriteHtml(text, prefix))
                    Dim bytes = enc.GetBytes(text)
                    resp.ContentLength64 = bytes.Length
                    Await resp.OutputStream.WriteAsync(bytes, 0, bytes.Length, rc.CancellationToken).ConfigureAwait(False)
                    Return
                End If
                ' Too big to rewrite: what was read, then the rest as it comes.
                resp.SendChunked = True
                buffer.Position = 0
                Await buffer.CopyToAsync(resp.OutputStream, rc.CancellationToken).ConfigureAwait(False)
                Await body.CopyToAsync(resp.OutputStream, rc.CancellationToken).ConfigureAwait(False)
                Return
            End If
            If length.HasValue Then resp.ContentLength64 = length.Value Else resp.SendChunked = True
            Await body.CopyToAsync(resp.OutputStream, 81920, rc.CancellationToken).ConfigureAwait(False)
        End Function

        Private Shared Function EncodingOf(contentType As String) As Encoding
            Dim m = Regex.Match(If(contentType, ""), "charset=([\w\-]+)", RegexOptions.IgnoreCase)
            If m.Success Then
                Try
                    Return Encoding.GetEncoding(m.Groups(1).Value)
                Catch ex As ArgumentException
                End Try
            End If
            Return New UTF8Encoding(False)
        End Function

        ''' <summary>A redirect inside the app stays under the prefix: "/login" and "http://localhost:5000/login" both become prefix + "/login".</summary>
        Friend Shared Function RewriteLocation(location As String, prefix As String, target As String) As String
            If String.IsNullOrEmpty(location) Then Return location
            If location.StartsWith("/", StringComparison.Ordinal) AndAlso Not location.StartsWith("//", StringComparison.Ordinal) Then
                Return If(location.StartsWith(prefix & "/", StringComparison.Ordinal), location, prefix & location)
            End If
            Dim u As Uri = Nothing
            If Uri.TryCreate(location, UriKind.Absolute, u) AndAlso (u.Scheme = "http" OrElse u.Scheme = "https") Then
                Dim t = New Uri(target)
                Dim local = u.Host = t.Host OrElse u.Host = "localhost" OrElse u.Host = "127.0.0.1" OrElse u.Host = "0.0.0.0"
                If local AndAlso (u.Port = t.Port OrElse u.Host <> t.Host) Then Return prefix & u.PathAndQuery & u.Fragment
            End If
            Return location
        End Function

        ''' <summary>
        ''' The app's cookie, kept to its own path under the prefix and to this host (no Domain); the host's own cookie names
        ''' (agentic*) are refused. Nothing = drop it.
        ''' </summary>
        Friend Shared Function RewriteSetCookie(cookie As String, prefixPath As String) As String
            If String.IsNullOrWhiteSpace(cookie) Then Return Nothing
            Dim name = cookie.Split("="c)(0).Trim()
            If name.StartsWith("agentic", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim attrs = cookie.Split(";"c).Select(Function(a) a.Trim()).Where(Function(a) a.Length > 0).ToList()
            Dim result As New List(Of String) From {attrs(0)}
            Dim hasPath = False
            For Each a In attrs.Skip(1)
                If a.StartsWith("domain", StringComparison.OrdinalIgnoreCase) Then Continue For
                If a.StartsWith("path", StringComparison.OrdinalIgnoreCase) Then
                    Dim p = a.Substring(a.IndexOf("="c) + 1).Trim()
                    result.Add("Path=" & prefixPath.TrimEnd("/"c) & If(p.StartsWith("/"), p, "/" & p))
                    hasPath = True
                    Continue For
                End If
                result.Add(a)
            Next
            If Not hasPath Then result.Add("Path=" & prefixPath)
            Return String.Join("; ", result)
        End Function

        Private Async Function WritePageAsync(rc As RequestContext, status As Integer, message As String) As Task
            Dim resp = rc.Http.Response
            ApplyHeaders(resp)
            Dim html = "<!doctype html><html><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1""><title>App preview</title>" &
                       "<style>body{font:15px/1.5 system-ui,sans-serif;margin:0;display:grid;place-items:center;min-height:100vh;background:#f4f6fa;color:#0f172a}" &
                       "div{max-width:520px;padding:28px;border-radius:12px;background:#fff;box-shadow:0 2px 12px rgba(0,0,0,.08)}h1{font-size:18px;margin:0 0 8px}</style></head>" &
                       $"<body><div><h1>App preview</h1><p>{WebUtility.HtmlEncode(message)}</p></div></body></html>"
            Await rc.WriteBytesAsync(status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), New Dictionary(Of String, String) From {{"Cache-Control", "no-store"}}).ConfigureAwait(False)
        End Function

        ' ---------------------------------------------------------------- WebSockets (live reload, Streamlit, Gradio…)

        Private Async Function BridgeWebSocketAsync(rc As RequestContext, preview As SandboxPreview, rest As String, prefix As String) As Task
            Dim req = rc.Http.Request
            Using upstream As New ClientWebSocket()
                Dim protocols = If(req.Headers("Sec-WebSocket-Protocol"), "")
                For Each p In protocols.Split(","c).Select(Function(x) x.Trim()).Where(Function(x) x.Length > 0)
                    upstream.Options.AddSubProtocol(p)
                Next
                upstream.Options.SetRequestHeader("Origin", $"http://{preview.TargetHost}:{preview.TargetPort}")
                upstream.Options.KeepAliveInterval = TimeSpan.FromSeconds(30)
                Try
                    Using connectWait = CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken)
                        connectWait.CancelAfter(TimeSpan.FromSeconds(20))
                        Await upstream.ConnectAsync(New Uri($"ws://{preview.TargetHost}:{preview.TargetPort}{rest}"), connectWait.Token).ConfigureAwait(False)
                    End Using
                Catch ex As Exception When TypeOf ex Is WebSocketException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                    rc.Http.Response.StatusCode = 502
                    rc.MarkResponded()
                    Return
                End Try
                Dim accepted = Await rc.Http.AcceptWebSocketAsync(If(String.IsNullOrEmpty(upstream.SubProtocol), Nothing, upstream.SubProtocol)).ConfigureAwait(False)
                rc.MarkResponded()
                Dim client = accepted.WebSocket
                Using stopBoth = CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken)
                    Dim a = PumpAsync(client, upstream, stopBoth.Token)
                    Dim b = PumpAsync(upstream, client, stopBoth.Token)
                    Await Task.WhenAny(a, b).ConfigureAwait(False)
                    stopBoth.Cancel()
                    Try
                        Await Task.WhenAll(a, b).ConfigureAwait(False)
                    Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is WebSocketException OrElse TypeOf ex Is ObjectDisposedException
                    End Try
                End Using
                For Each ws In New WebSocket() {client, upstream}
                    Try
                        If ws.State = WebSocketState.Open OrElse ws.State = WebSocketState.CloseReceived Then
                            Await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).ConfigureAwait(False)
                        End If
                    Catch ex As Exception When TypeOf ex Is WebSocketException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException
                    End Try
                Next
            End Using
        End Function

        Private Shared Async Function PumpAsync(source As WebSocket, sink As WebSocket, ct As CancellationToken) As Task
            Dim buffer(65535) As Byte
            Try
                While source.State = WebSocketState.Open AndAlso Not ct.IsCancellationRequested
                    Dim r = Await source.ReceiveAsync(New ArraySegment(Of Byte)(buffer), ct).ConfigureAwait(False)
                    If r.MessageType = WebSocketMessageType.Close Then
                        If sink.State = WebSocketState.Open Then Await sink.CloseOutputAsync(If(r.CloseStatus, WebSocketCloseStatus.NormalClosure), r.CloseStatusDescription, ct).ConfigureAwait(False)
                        Exit While
                    End If
                    Await sink.SendAsync(New ArraySegment(Of Byte)(buffer, 0, r.Count), r.MessageType, r.EndOfMessage, ct).ConfigureAwait(False)
                End While
            Catch ex As Exception When TypeOf ex Is WebSocketException OrElse TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is ObjectDisposedException
            End Try
        End Function
    End Class

    ''' <summary>Keeps a previewed app's root-absolute URLs under its /container/{id} prefix.</summary>
    Public NotInheritable Class PreviewRewriter
        Private Sub New()
        End Sub

        Private Shared Function Under(path As String, prefix As String) As String
            If path.StartsWith(prefix & "/", StringComparison.Ordinal) OrElse path = prefix Then Return path
            Return prefix & path
        End Function

        Private Shared ReadOnly AttrUrl As New Regex("(\s(?:href|src|action|formaction|poster|data|background|manifest)\s*=\s*)([""'])(/(?!/)[^""']*)\2",
                                                     RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private Shared ReadOnly SrcSet As New Regex("(\s(?:srcset|imagesrcset)\s*=\s*)([""'])([^""']*)\2", RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private Shared ReadOnly CssUrl As New Regex("url\(\s*([""']?)(/(?!/)[^""')]*)\1\s*\)", RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private Shared ReadOnly CssImport As New Regex("(@import\s+)([""'])(/(?!/)[^""']*)\2", RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private Shared ReadOnly HeadTag As New Regex("<head\b[^>]*>", RegexOptions.IgnoreCase Or RegexOptions.Compiled)
        Private Shared ReadOnly HtmlTag As New Regex("<html\b[^>]*>", RegexOptions.IgnoreCase Or RegexOptions.Compiled)

        ''' <summary>Rewrites root-absolute URLs in attributes, srcset and inline CSS, and puts the runtime shim first in the page.</summary>
        Public Shared Function RewriteHtml(html As String, prefix As String) As String
            If String.IsNullOrEmpty(html) Then Return html
            Dim s = AttrUrl.Replace(html, Function(m) m.Groups(1).Value & m.Groups(2).Value & Under(m.Groups(3).Value, prefix) & m.Groups(2).Value)
            s = SrcSet.Replace(s, Function(m) m.Groups(1).Value & m.Groups(2).Value &
                String.Join(",", m.Groups(3).Value.Split(","c).Select(Function(entry)
                                                                          Dim t = entry.TrimStart()
                                                                          Dim lead = entry.Substring(0, entry.Length - t.Length)
                                                                          Return lead & If(t.StartsWith("/") AndAlso Not t.StartsWith("//"), Under(t, prefix), t)
                                                                      End Function)) & m.Groups(2).Value)
            s = RewriteCss(s, prefix)
            Dim shim = "<script>" & ShimScript(prefix) & "</script>"
            Dim head = HeadTag.Match(s)
            If head.Success Then Return s.Insert(head.Index + head.Length, shim)
            Dim openHtml = HtmlTag.Match(s)
            If openHtml.Success Then Return s.Insert(openHtml.Index + openHtml.Length, shim)
            Return shim & s
        End Function

        Public Shared Function RewriteCss(css As String, prefix As String) As String
            If String.IsNullOrEmpty(css) Then Return css
            Dim s = CssUrl.Replace(css, Function(m) "url(" & m.Groups(1).Value & Under(m.Groups(2).Value, prefix) & m.Groups(1).Value & ")")
            Return CssImport.Replace(s, Function(m) m.Groups(1).Value & m.Groups(2).Value & Under(m.Groups(3).Value, prefix) & m.Groups(2).Value)
        End Function

        ''' <summary>
        ''' Runs before the app's own scripts: root-absolute URLs the app builds at runtime (fetch, XHR, WebSocket, EventSource,
        ''' history, links and forms) get the prefix; and when the page runs as an isolated origin, where the browser refuses
        ''' localStorage and sessionStorage, the app gets working in-memory ones instead of an error.
        ''' </summary>
        Public Shared Function ShimScript(prefix As String) As String
            Return "(function(){var P=" & JsonUtil.ToJson(prefix) & ";" &
                "function fix(u){if(typeof u!=='string'||u.charAt(0)!=='/'||u.charAt(1)==='/'||u===P||u.indexOf(P+'/')===0)return u;return P+u;}" &
                "function fixAbs(u){try{var x=new URL(u,location.href);if(x.host===location.host&&x.pathname!==P&&x.pathname.indexOf(P+'/')!==0){x.pathname=P+x.pathname;return x.toString();}}catch(e){}return u;}" &
                "var F=window.fetch;if(F){window.fetch=function(i,o){if(typeof i==='string')i=fix(i);else if(i&&typeof i.url==='string'&&typeof Request!=='undefined'&&i instanceof Request){var f=fixAbs(i.url);if(f!==i.url)i=new Request(f,i);}return F.call(this,i,o);};}" &
                "var X=XMLHttpRequest.prototype.open;XMLHttpRequest.prototype.open=function(m,u){if(typeof u==='string')arguments[1]=fix(u);return X.apply(this,arguments);};" &
                "var W=window.WebSocket;if(W){var N=function(u,p){u=typeof u==='string'&&u.charAt(0)==='/'?(location.protocol==='https:'?'wss://':'ws://')+location.host+fix(u):fixAbs(u);return p===undefined?new W(u):new W(u,p);};N.prototype=W.prototype;N.CONNECTING=0;N.OPEN=1;N.CLOSING=2;N.CLOSED=3;window.WebSocket=N;}" &
                "var E=window.EventSource;if(E){var S=function(u,c){return new E(typeof u==='string'?fix(u):u,c);};S.prototype=E.prototype;window.EventSource=S;}" &
                "['pushState','replaceState'].forEach(function(n){var h=history[n];history[n]=function(s,t,u){return h.call(this,s,t,typeof u==='string'?fix(u):u);};});" &
                "document.addEventListener('click',function(e){var a=e.target&&e.target.closest&&e.target.closest('a[href]');if(a){var h=a.getAttribute('href'),f=fix(h);if(f!==h)a.setAttribute('href',f);}},true);" &
                "document.addEventListener('submit',function(e){var t=e.target;if(t&&t.getAttribute){var a=t.getAttribute('action'),f=fix(a);if(a&&f!==a)t.setAttribute('action',f);}},true);" &
                "function mem(){var d={};return{getItem:function(k){return Object.prototype.hasOwnProperty.call(d,k)?d[k]:null;},setItem:function(k,v){d[k]=String(v);},removeItem:function(k){delete d[k];},clear:function(){d={};},key:function(i){return Object.keys(d)[i]||null;},get length(){return Object.keys(d).length;}};}" &
                "['localStorage','sessionStorage'].forEach(function(n){try{window[n].getItem('x');}catch(e){try{Object.defineProperty(window,n,{value:mem(),configurable:true});}catch(e2){}}});" &
                "})();"
        End Function
    End Class

End Namespace
