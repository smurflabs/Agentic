Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Collections.Specialized
Imports System.IO
Imports System.Net
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities

Namespace Http

    ''' <summary>Expected API failure mapped to a structured JSON error ({"error":{"code","message"}}).</summary>
    Public Class ApiException
        Inherits Exception

        Public ReadOnly Property StatusCode As Integer
        Public ReadOnly Property Code As String

        Public Sub New(statusCode As Integer, code As String, message As String)
            MyBase.New(message)
            Me.StatusCode = statusCode
            Me.Code = code
        End Sub

        Public Shared Function NotFound(code As String, message As String) As ApiException
            Return New ApiException(404, code, message)
        End Function

        Public Shared Function BadRequest(message As String, Optional code As String = "BAD_REQUEST") As ApiException
            Return New ApiException(400, code, message)
        End Function
    End Class

    Public Enum AuthLevel
        Anonymous
        Read
        SubmitTask
        Admin
    End Enum

    Public NotInheritable Class AuthUser
        Public Property Name As String
        Public Property DisplayName As String
        Public Property Scopes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Public Property AllowedAgents As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Dashboard permissions of a User-role account (admins have all).</summary>
        Public Property Permissions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Model ids a User-role account may use (Chat, the API, agents); empty = every model.</summary>
        Public Property AllowedModels As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>MCP server ids a User-role account's chats (and the agents it creates) may use; empty = every server.</summary>
        Public Property AllowedMcpServers As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Chat and API tokens per day (UTC) for a User-role account; 0 = no limit.</summary>
        Public Property DailyTokenLimit As Long
        ''' <summary>Chat messages and API calls per minute for a User-role account; 0 = no limit.</summary>
        Public Property RequestsPerMinute As Integer
        ''' <summary>Admin | User for dashboard sessions; Token for API tokens.</summary>
        Public Property Role As String = "Token"
        Public Property ViaSession As Boolean
        ''' <summary>Authenticated with the account's personal API key (same role and permissions as its sessions).</summary>
        Public Property ViaApiKey As Boolean
        Public Property SessionId As String
        Public Property CsrfToken As String

        Public ReadOnly Property IsAdmin As Boolean
            Get
                Return Scopes.Contains("admin")
            End Get
        End Property

        ''' <summary>A dashboard account with the User role: governed by its permission list instead of token scopes.</summary>
        Public ReadOnly Property IsRestrictedUser As Boolean
            Get
                Return (ViaSession OrElse ViaApiKey) AndAlso String.Equals(Role, "User", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property

        Public Function HasPermission(key As String) As Boolean
            If IsAdmin Then Return True
            If key = Http.Permissions.ViewWorkforce Then Return SeesWorkforce
            Return Permissions.Contains(key)
        End Function

        ''' <summary>
        ''' Whether this account sees the Workforce (agents and their work) at all: administrators and API tokens do; a User-role
        ''' account needs "View workforce" or a permission that acts on agents. Without it the user sees no agent anywhere.
        ''' </summary>
        Public ReadOnly Property SeesWorkforce As Boolean
            Get
                Return Not IsRestrictedUser OrElse Http.Permissions.WorkforcePermissions.Any(Function(p) Permissions.Contains(p))
            End Get
        End Property

        ''' <summary>Every agent, including new ones (no agent list, and the Workforce is visible).</summary>
        Public ReadOnly Property SeesAllAgents As Boolean
            Get
                Return AllowedAgents.Count = 0 AndAlso SeesWorkforce
            End Get
        End Property

        ''' <summary>Whether this user may call a route (its token scope level, or its dashboard role and permissions).</summary>
        Public Function MayCall(route As RouteDefinition) As Boolean
            Dim perm = route.Permission
            If perm = Http.Permissions.Self Then Return True
            If IsRestrictedUser Then
                If perm Is Nothing Then Return route.Method = "GET"
                If perm = Http.Permissions.AdminOnly Then Return False
                ' Creating agents is reserved for users who are not limited to specific agents.
                If perm = "agents.manage" AndAlso route.Method = "POST" AndAlso route.Segments.Length = 2 AndAlso AllowedAgents.Count > 0 Then Return False
                Return perm.Split("|"c).Any(Function(p) HasPermission(p)) ' "a|b" = either permission
            End If
            If perm = Http.Permissions.AdminOnly AndAlso Not IsAdmin Then Return False
            Return Has(route.Level)
        End Function

        Public Function Has(level As AuthLevel) As Boolean
            If Scopes.Contains("admin") Then Return True
            Select Case level
                Case AuthLevel.Anonymous : Return True
                Case AuthLevel.Read : Return Scopes.Contains("read") OrElse Scopes.Contains("submit-task")
                Case AuthLevel.SubmitTask : Return Scopes.Contains("submit-task")
                Case Else : Return False
            End Select
        End Function

        ''' <summary>Whether this account may use a model (a provider model id or a custom model id).</summary>
        Public Function CanUseModel(modelId As String) As Boolean
            If Not IsRestrictedUser OrElse AllowedModels.Count = 0 Then Return True
            Return modelId IsNot Nothing AndAlso AllowedModels.Contains(modelId)
        End Function

        ''' <summary>Whether this account's chats (and the agents it creates) may use an MCP server.</summary>
        Public Function CanUseMcp(serverId As String) As Boolean
            If Not IsRestrictedUser OrElse AllowedMcpServers.Count = 0 Then Return True
            Return serverId IsNot Nothing AndAlso AllowedMcpServers.Contains(serverId)
        End Function

        Public Function CanAccessAgent(agentId As String) As Boolean
            If Not SeesWorkforce Then Return False
            Return AllowedAgents.Count = 0 OrElse (agentId IsNot Nothing AndAlso AllowedAgents.Contains(agentId))
        End Function
    End Class

    ''' <summary>Per-request state and helpers (JSON in/out with size limits, safe errors).</summary>
    Public NotInheritable Class RequestContext
        Public ReadOnly Property Http As HttpListenerContext
        Public ReadOnly Property RequestId As String = Ids.NewId("req")
        Public Property RouteValues As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property User As AuthUser
        Public Property CancellationToken As CancellationToken
        Private ReadOnly _maxBody As Integer
        Private _responded As Boolean

        Public Sub New(http As HttpListenerContext, maxBody As Integer, ct As CancellationToken)
            Me.Http = http
            _maxBody = maxBody
            Me.CancellationToken = ct
        End Sub

        Public ReadOnly Property Method As String
            Get
                Return Http.Request.HttpMethod.ToUpperInvariant()
            End Get
        End Property

        Public ReadOnly Property Path As String
            Get
                Dim p = Http.Request.Url.AbsolutePath
                If p.Length > 1 AndAlso p.EndsWith("/", StringComparison.Ordinal) Then p = p.TrimEnd("/"c)
                Return p
            End Get
        End Property

        Public ReadOnly Property Query As NameValueCollection
            Get
                Return Http.Request.QueryString
            End Get
        End Property

        Public ReadOnly Property RemoteAddress As String
            Get
                Return Http.Request.RemoteEndPoint?.Address?.ToString()
            End Get
        End Property

        Public ReadOnly Property Responded As Boolean
            Get
                Return _responded
            End Get
        End Property

        Public Function Route(name As String) As String
            Dim v As String = Nothing
            If RouteValues.TryGetValue(name, v) Then Return v
            Return Nothing
        End Function

        Public Function QueryString(name As String, Optional defaultValue As String = Nothing) As String
            Dim v = Query(name)
            Return If(String.IsNullOrEmpty(v), defaultValue, v)
        End Function

        Public Function QueryInt(name As String, defaultValue As Integer, Optional min As Integer = Integer.MinValue, Optional max As Integer = Integer.MaxValue) As Integer
            Dim v As Integer
            If Integer.TryParse(Query(name), v) Then Return Math.Clamp(v, min, max)
            Return defaultValue
        End Function

        Public Function QueryDate(name As String) As DateTime?
            Dim v = Query(name)
            If String.IsNullOrEmpty(v) Then Return Nothing
            Dim d As DateTime
            If DateTime.TryParse(v, Globalization.CultureInfo.InvariantCulture, Globalization.DateTimeStyles.AdjustToUniversal Or Globalization.DateTimeStyles.AssumeUniversal, d) Then Return d
            Throw ApiException.BadRequest($"Invalid date for '{name}'.")
        End Function

        Public Async Function ReadBodyAsync() As Task(Of String)
            Dim req = Http.Request
            If Not req.HasEntityBody Then Return String.Empty
            If req.ContentLength64 > _maxBody Then Throw New ApiException(413, "PAYLOAD_TOO_LARGE", $"Request body exceeds {_maxBody} bytes.")
            Using ms As New MemoryStream()
                Dim buf(16383) As Byte
                While True
                    Dim n = Await req.InputStream.ReadAsync(buf, 0, buf.Length, CancellationToken).ConfigureAwait(False)
                    If n <= 0 Then Exit While
                    ms.Write(buf, 0, n)
                    If ms.Length > _maxBody Then Throw New ApiException(413, "PAYLOAD_TOO_LARGE", $"Request body exceeds {_maxBody} bytes.")
                End While
                Return Encoding.UTF8.GetString(ms.ToArray())
            End Using
        End Function

        ''' <summary>Raw request body for file uploads (its own size limit instead of Server.MaxRequestBodyBytes).</summary>
        Public Async Function ReadBytesAsync(maxBytes As Long) As Task(Of Byte())
            Dim req = Http.Request
            If Not req.HasEntityBody Then Return Array.Empty(Of Byte)()
            If req.ContentLength64 > maxBytes Then Throw New ApiException(413, "PAYLOAD_TOO_LARGE", $"The file is larger than {maxBytes \ (1024 * 1024)} MB.")
            Using ms As New MemoryStream()
                Dim buf(65535) As Byte
                While True
                    Dim n = Await req.InputStream.ReadAsync(buf, 0, buf.Length, CancellationToken).ConfigureAwait(False)
                    If n <= 0 Then Exit While
                    ms.Write(buf, 0, n)
                    If ms.Length > maxBytes Then Throw New ApiException(413, "PAYLOAD_TOO_LARGE", $"The file is larger than {maxBytes \ (1024 * 1024)} MB.")
                End While
                Return ms.ToArray()
            End Using
        End Function

        Public Async Function ReadJsonAsync() As Task(Of JsonElement)
            Dim body = Await ReadBodyAsync().ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(body) Then body = "{}"
            Dim el As JsonElement
            If Not JsonUtil.TryParseElement(body, el) Then Throw ApiException.BadRequest("Request body must be valid JSON.", "INVALID_JSON")
            If el.ValueKind <> JsonValueKind.Object Then Throw ApiException.BadRequest("Request body must be a JSON object.", "INVALID_JSON")
            Return el
        End Function

        Public Async Function ReadJsonAsync(Of T)() As Task(Of T)
            Dim body = Await ReadBodyAsync().ConfigureAwait(False)
            Try
                Return JsonSerializer.Deserialize(Of T)(If(String.IsNullOrWhiteSpace(body), "{}", body), JsonUtil.Options)
            Catch ex As JsonException
                Throw ApiException.BadRequest("Invalid JSON: " & ex.Message, "INVALID_JSON")
            End Try
        End Function

        ''' <summary>Starts a chunked response (server-sent events or a proxied stream); write with WriteStreamAsync.</summary>
        Public Sub BeginStream(contentType As String, Optional status As Integer = 200)
            _responded = True
            Dim resp = Http.Response
            resp.StatusCode = status
            resp.ContentType = contentType
            resp.SendChunked = True
            resp.Headers("Cache-Control") = "no-cache"
            resp.Headers("X-Accel-Buffering") = "no"
        End Sub

        ''' <summary>Writes and flushes part of a streamed response. Fails (IOException) when the client went away.</summary>
        Public Async Function WriteStreamAsync(text As String) As Task
            Dim bytes = Encoding.UTF8.GetBytes(text)
            Dim output = Http.Response.OutputStream
            Await output.WriteAsync(bytes, 0, bytes.Length, CancellationToken).ConfigureAwait(False)
            Await output.FlushAsync(CancellationToken).ConfigureAwait(False)
        End Function

        ''' <summary>Request body as raw bytes within the normal size limit (proxied JSON).</summary>
        Public Async Function ReadBodyBytesAsync() As Task(Of Byte())
            Return Await ReadBytesAsync(_maxBody).ConfigureAwait(False)
        End Function

        Public Async Function WriteBytesAsync(status As Integer, contentType As String, data As Byte(), Optional extraHeaders As Dictionary(Of String, String) = Nothing) As Task
            If _responded Then Return
            _responded = True
            Dim resp = Http.Response
            resp.StatusCode = status
            resp.ContentType = contentType
            If extraHeaders IsNot Nothing Then
                For Each kv In extraHeaders
                    resp.Headers(kv.Key) = kv.Value
                Next
            End If
            resp.ContentLength64 = data.LongLength
            ' HEAD: the headers only (writing the body throws "Bytes to be written to the stream exceed the Content-Length").
            If String.Equals(Http.Request.HttpMethod, "HEAD", StringComparison.OrdinalIgnoreCase) Then Return
            Await resp.OutputStream.WriteAsync(data, 0, data.Length, CancellationToken).ConfigureAwait(False)
        End Function

        Public Function WriteJsonAsync(status As Integer, value As Object) As Task
            Dim json = JsonUtil.ToJson(value)
            Http.Response.Headers("Cache-Control") = "no-store"
            Return WriteBytesAsync(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json))
        End Function

        Public Function OkAsync(value As Object) As Task
            Return WriteJsonAsync(200, value)
        End Function

        Public Function WriteErrorAsync(status As Integer, code As String, message As String) As Task
            Return WriteJsonAsync(status, New With {.error = New With {.code = code, .message = message, .requestId = RequestId}})
        End Function

        Friend Sub MarkResponded()
            _responded = True
        End Sub
    End Class

    Public NotInheritable Class RouteDefinition
        Public Property Method As String
        Public Property Segments As String()
        Public Property Handler As Func(Of RequestContext, Task)
        Public Property Level As AuthLevel
        ''' <summary>Dashboard permission the route needs (see Permissions.ForRoute); Nothing = viewing.</summary>
        Public Property Permission As String
        ''' <summary>Name of the route value holding an agent id that token agent allowlists apply to.</summary>
        Public Property AgentParam As String
    End Class

    ''' <summary>Minimal method + template router ("/api/agents/{id}/tasks").</summary>
    Public NotInheritable Class Router
        Private ReadOnly _routes As New List(Of RouteDefinition)

        Public Sub Map(method As String, template As String, level As AuthLevel, handler As Func(Of RequestContext, Task), Optional agentParam As String = Nothing)
            _routes.Add(New RouteDefinition With {
                .Method = method.ToUpperInvariant(), .Segments = template.Trim("/"c).Split("/"c), .Handler = handler, .Level = level,
                .Permission = Http.Permissions.ForRoute(method, template),
                .AgentParam = If(agentParam, If(template.Contains("/agents/{id}", StringComparison.OrdinalIgnoreCase), "id", Nothing))})
        End Sub

        ''' <summary>Every registered route (for the OpenAPI document).</summary>
        Public ReadOnly Property Routes As IReadOnlyList(Of RouteDefinition)
            Get
                Return _routes
            End Get
        End Property

        Public Function Match(method As String, path As String, values As Dictionary(Of String, String), ByRef methodMismatch As Boolean) As RouteDefinition
            Dim parts = path.Trim("/"c).Split("/"c)
            methodMismatch = False
            For Each r In _routes
                ' A last segment "{*name}" captures the rest of the path (model ids contain "/").
                Dim catchAll = r.Segments.Length > 0 AndAlso r.Segments(r.Segments.Length - 1).StartsWith("{*", StringComparison.Ordinal)
                If catchAll Then
                    If parts.Length < r.Segments.Length Then Continue For
                ElseIf r.Segments.Length <> parts.Length Then
                    Continue For
                End If
                Dim ok = True
                Dim captured As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                For i = 0 To r.Segments.Length - 1
                    Dim seg = r.Segments(i)
                    If catchAll AndAlso i = r.Segments.Length - 1 Then
                        Dim rest = Uri.UnescapeDataString(String.Join("/", parts.Skip(i)))
                        If rest.Length = 0 OrElse rest.Length > 300 Then ok = False : Exit For
                        captured(seg.Substring(2, seg.Length - 3)) = rest
                    ElseIf seg.StartsWith("{", StringComparison.Ordinal) AndAlso seg.EndsWith("}", StringComparison.Ordinal) Then
                        Dim value = Uri.UnescapeDataString(parts(i))
                        If value.Length = 0 OrElse value.Length > 200 Then ok = False : Exit For
                        captured(seg.Substring(1, seg.Length - 2)) = value
                    ElseIf Not String.Equals(seg, parts(i), StringComparison.OrdinalIgnoreCase) Then
                        ok = False
                        Exit For
                    End If
                Next
                If Not ok Then Continue For
                If r.Method <> method Then
                    methodMismatch = True
                    Continue For
                End If
                For Each kv In captured
                    values(kv.Key) = kv.Value
                Next
                Return r
            Next
            Return Nothing
        End Function
    End Class

    Public NotInheritable Class Session
        Public Property Id As String
        Public Property User As AuthUser
        Public Property CsrfToken As String
        Public Property ExpiresUtc As DateTime
        ''' <summary>The expiry last written to the WebSessions table (it is refreshed there only now and then).</summary>
        Public Property StoredExpiresUtc As DateTime
    End Class

    ''' <summary>
    ''' Authentication: admin users (PBKDF2 hashes in the AdminUsers table) with server-side sessions (HttpOnly,
    ''' SameSite=Strict cookie + CSRF token for state-changing requests), or bearer API tokens (SHA-256 compared,
    ''' scoped, optional agent allowlist). Login attempts are throttled per remote address.
    ''' </summary>
    Public NotInheritable Class AuthService
        Public Const CookieName As String = "agentic_session"

        Private ReadOnly _sessions As New ConcurrentDictionary(Of String, Session)(StringComparer.Ordinal)
        Private ReadOnly _agents As AgentRepository
        Private ReadOnly _admin As AdminOptions
        Private ReadOnly _sessionLifetime As TimeSpan
        Private ReadOnly _loginLimiter As Communication.SlidingWindowRateLimiter
        Private ReadOnly _tokens As New List(Of (Hash As String, Token As ApiTokenOptions))
        Private _lastStoredCleanup As DateTime = DateTime.MinValue

        Public Sub New(agents As AgentRepository, admin As AdminOptions, sessionMinutes As Integer, clock As IClock, redactor As SecretRedactor)
            _agents = agents
            _admin = admin
            _sessionLifetime = TimeSpan.FromMinutes(Math.Max(5, sessionMinutes))
            _loginLimiter = New Communication.SlidingWindowRateLimiter(clock)
            For Each t In admin.ApiTokens
                Dim hash = t.TokenSha256
                If String.IsNullOrEmpty(hash) AndAlso Not String.IsNullOrEmpty(t.TokenEnvVar) Then
                    Dim raw = Environment.GetEnvironmentVariable(t.TokenEnvVar)
                    If Not String.IsNullOrEmpty(raw) Then
                        hash = TextUtil.Sha256Hex(raw)
                        redactor.AddSecret(raw)
                    End If
                End If
                If Not String.IsNullOrEmpty(hash) Then _tokens.Add((hash.ToLowerInvariant(), t))
            Next
        End Sub

        Public Async Function LoginAsync(username As String, password As String, remote As String) As Task(Of Session)
            If Not _loginLimiter.TryAcquire("login:" & If(remote, "?"), Math.Max(1, _admin.MaxLoginAttemptsPerMinute), TimeSpan.FromMinutes(1)) Then
                Throw New ApiException(429, "TOO_MANY_ATTEMPTS", "Too many login attempts. Wait a minute and try again.")
            End If
            If String.IsNullOrWhiteSpace(username) OrElse String.IsNullOrEmpty(password) OrElse username.Length > 100 OrElse password.Length > 500 Then
                Throw New ApiException(401, "INVALID_CREDENTIALS", "Invalid username or password.")
            End If
            Dim hash = Await _agents.GetAdminPasswordHashAsync(username).ConfigureAwait(False)
            ' Verify against a dummy hash when the user does not exist to keep timing similar.
            Dim ok = PasswordHasher.Verify(password, If(hash, "pbkdf2$210000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")) AndAlso hash IsNot Nothing
            If Not ok Then Throw New ApiException(401, "INVALID_CREDENTIALS", "Invalid username or password.")
            Dim account = Await _agents.GetUserAsync(username).ConfigureAwait(False)
            If account Is Nothing OrElse account.Disabled Then Throw New ApiException(401, "ACCOUNT_DISABLED", "This account is disabled. Ask an administrator.")
            Await _agents.TouchAdminLoginAsync(username).ConfigureAwait(False)
            Dim s As New Session With {
                .Id = Ids.NewToken(32), .CsrfToken = Ids.NewToken(24), .ExpiresUtc = DateTime.UtcNow.Add(_sessionLifetime),
                .User = BuildSessionUser(account)}
            s.User.SessionId = s.Id
            s.User.CsrfToken = s.CsrfToken
            s.StoredExpiresUtc = s.ExpiresUtc
            _sessions(s.Id) = s
            ' Stored too (by hash), so a restart of the host does not sign the user out.
            Await _agents.SaveWebSessionAsync(TextUtil.Sha256Hex(s.Id), account.Username, s.CsrfToken, s.ExpiresUtc).ConfigureAwait(False)
            CleanupExpired()
            Return s
        End Function

        ''' <summary>The signed-in identity for a dashboard account (Admins get every permission; Users their assigned ones).</summary>
        Friend Shared Function BuildSessionUser(account As DashboardUser) As AuthUser
            Dim u As New AuthUser With {.Name = account.Username, .DisplayName = If(account.DisplayName, account.Username), .ViaSession = True,
                                        .Role = If(account.IsAdmin, "Admin", "User")}
            If account.IsAdmin Then
                u.Scopes.Add("admin")
                For Each p In Http.Permissions.Catalog
                    u.Permissions.Add(p.Key)
                Next
            Else
                u.Scopes.Add("read")
                For Each p In account.Permissions.Where(AddressOf Http.Permissions.IsKnown)
                    u.Permissions.Add(p)
                Next
                For Each a In account.AllowedAgents
                    u.AllowedAgents.Add(a)
                Next
                For Each m In account.AllowedModels
                    u.AllowedModels.Add(m)
                Next
                For Each s In account.AllowedMcpServers
                    u.AllowedMcpServers.Add(s)
                Next
                u.DailyTokenLimit = Math.Max(0, account.DailyTokenLimit)
                u.RequestsPerMinute = Math.Max(0, account.RequestsPerMinute)
            End If
            Return u
        End Function

        Public Sub Logout(sessionId As String)
            Dim s As Session = Nothing
            If sessionId Is Nothing Then Return
            _sessions.TryRemove(sessionId, s)
            _agents.DeleteWebSessionAsync(TextUtil.Sha256Hex(sessionId)).GetAwaiter().GetResult()
        End Sub

        ''' <summary>Ends every session of a user (after a password reset, role or permission change, disabling or deletion).</summary>
        Public Function LogoutUser(username As String, Optional exceptSessionId As String = Nothing) As Integer
            Dim n = 0
            For Each kv In _sessions
                If String.Equals(kv.Value.User.Name, username, StringComparison.OrdinalIgnoreCase) AndAlso kv.Key <> exceptSessionId Then
                    Dim removed As Session = Nothing
                    If _sessions.TryRemove(kv.Key, removed) Then n += 1
                End If
            Next
            ' Sign-ins kept from before a restart are not all in memory: the stored count is the full one.
            Dim stored = _agents.DeleteWebSessionsOfUserAsync(username, If(exceptSessionId Is Nothing, Nothing, TextUtil.Sha256Hex(exceptSessionId))).GetAwaiter().GetResult()
            Return Math.Max(n, stored)
        End Function

        ''' <summary>Checks a user's current password (for self-service password changes).</summary>
        Public Async Function VerifyPasswordAsync(username As String, password As String) As Task(Of Boolean)
            Dim hash = Await _agents.GetAdminPasswordHashAsync(username).ConfigureAwait(False)
            Return hash IsNot Nothing AndAlso Not String.IsNullOrEmpty(password) AndAlso PasswordHasher.Verify(password, hash)
        End Function

        Private Sub CleanupExpired()
            Dim now = DateTime.UtcNow
            If (now - _lastStoredCleanup).TotalMinutes >= 10 Then
                _lastStoredCleanup = now
                Dim unused = _agents.DeleteExpiredWebSessionsAsync()
            End If
            For Each kv In _sessions
                If kv.Value.ExpiresUtc < now Then
                    Dim removed As Session = Nothing
                    _sessions.TryRemove(kv.Key, removed)
                End If
            Next
        End Sub

        ''' <summary>Prefix of personal API keys (dashboard accounts).</summary>
        Public Const ApiKeyPrefix As String = "agk_"
        Private ReadOnly _keyTouched As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>
        ''' Session cookie, configured API token, or a dashboard account's personal API key ("agk_…", sent as
        ''' "Authorization: Bearer" or "x-api-key"). A personal key acts exactly like its account: same role, permissions
        ''' and agent scope; it needs no CSRF token.
        ''' </summary>
        Public Async Function AuthenticateAsync(req As HttpListenerRequest) As Task(Of AuthUser)
            Dim key = req.Headers("x-api-key")
            Dim authHeader = req.Headers("Authorization")
            If String.IsNullOrEmpty(key) AndAlso Not String.IsNullOrEmpty(authHeader) AndAlso authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) Then
                Dim bearer = authHeader.Substring(7).Trim()
                If bearer.StartsWith(ApiKeyPrefix, StringComparison.Ordinal) Then key = bearer
            End If
            If Not String.IsNullOrEmpty(key) Then
                key = key.Trim()
                If Not key.StartsWith(ApiKeyPrefix, StringComparison.Ordinal) OrElse key.Length < 24 OrElse key.Length > 200 Then Return Nothing
                Dim account = Await _agents.FindUserByApiKeyAsync(TextUtil.Sha256Hex(key)).ConfigureAwait(False)
                If account Is Nothing OrElse account.Disabled Then Return Nothing
                ' Last use is recorded at most once a minute per account.
                Dim last As DateTime
                If Not _keyTouched.TryGetValue(account.Username, last) OrElse (DateTime.UtcNow - last).TotalSeconds > 60 Then
                    _keyTouched(account.Username) = DateTime.UtcNow
                    Await _agents.TouchApiKeyAsync(account.Username).ConfigureAwait(False)
                End If
                Dim u = BuildSessionUser(account)
                u.ViaSession = False
                u.ViaApiKey = True
                ' A User account without "Personal API key" cannot sign in with its key (the desktop app, VS Code, the CLI, programs).
                If u.IsRestrictedUser AndAlso Not u.HasPermission("account.apikey") Then Return Nothing
                Return u
            End If
            Dim user = Authenticate(req)
            If user IsNot Nothing Then Return user
            ' A sign-in from before a restart: restored from the WebSessions table, with the account as it is now.
            Dim cookie = req.Cookies(CookieName)
            If cookie Is Nothing OrElse String.IsNullOrEmpty(cookie.Value) OrElse cookie.Value.Length > 200 Then Return Nothing
            Dim restored = Await RestoreSessionAsync(cookie.Value).ConfigureAwait(False)
            Return restored?.User
        End Function

        ''' <summary>Loads a stored sign-in into memory; Nothing when it is unknown, expired, or its account is gone or disabled.</summary>
        Private Async Function RestoreSessionAsync(sessionId As String) As Task(Of Session)
            Dim idHash = TextUtil.Sha256Hex(sessionId)
            Dim row = Await _agents.GetWebSessionAsync(idHash).ConfigureAwait(False)
            If Not row.HasValue Then Return Nothing
            Dim now = DateTime.UtcNow
            Dim account = If(row.Value.ExpiresUtc < now, Nothing, Await _agents.GetUserAsync(row.Value.Username).ConfigureAwait(False))
            If account Is Nothing OrElse account.Disabled Then
                Await _agents.DeleteWebSessionAsync(idHash).ConfigureAwait(False)
                Return Nothing
            End If
            Dim s As New Session With {.Id = sessionId, .CsrfToken = row.Value.CsrfToken, .ExpiresUtc = now.Add(_sessionLifetime), .User = BuildSessionUser(account)}
            s.User.SessionId = s.Id
            s.User.CsrfToken = s.CsrfToken
            s.StoredExpiresUtc = s.ExpiresUtc
            _sessions(s.Id) = s
            Await _agents.TouchWebSessionAsync(idHash, s.ExpiresUtc).ConfigureAwait(False)
            Return s
        End Function

        Public Function Authenticate(req As HttpListenerRequest) As AuthUser
            Dim authHeader = req.Headers("Authorization")
            If Not String.IsNullOrEmpty(authHeader) AndAlso authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) Then
                Dim token = authHeader.Substring(7).Trim()
                If token.Length < 16 OrElse token.Length > 500 Then Return Nothing
                Dim hash = TextUtil.Sha256Hex(token)
                ' Iterate all tokens to prevent early-exit timing leak
                Dim matched As AuthUser = Nothing
                For Each t In _tokens
                    If TextUtil.FixedTimeEquals(t.Hash, hash) Then
                        Dim u As New AuthUser With {.Name = "token:" & t.Token.Name}
                        For Each s In t.Token.Scopes
                            u.Scopes.Add(s)
                        Next
                        For Each a In t.Token.AllowedAgents
                            u.AllowedAgents.Add(a)
                        Next
                        matched = u
                    End If
                Next
                Return matched
            End If
            Dim cookie = req.Cookies(CookieName)
            If cookie Is Nothing OrElse String.IsNullOrEmpty(cookie.Value) Then Return Nothing
            Dim session As Session = Nothing
            If Not _sessions.TryGetValue(cookie.Value, session) Then Return Nothing
            If session.ExpiresUtc < DateTime.UtcNow Then
                Logout(session.Id)
                Return Nothing
            End If
            session.ExpiresUtc = DateTime.UtcNow.Add(_sessionLifetime)
            ' The stored expiry follows the sliding one, written at most every few minutes.
            If (session.ExpiresUtc - session.StoredExpiresUtc).TotalMinutes >= Math.Min(5, _sessionLifetime.TotalMinutes / 4) Then
                session.StoredExpiresUtc = session.ExpiresUtc
                Dim unused = _agents.TouchWebSessionAsync(TextUtil.Sha256Hex(session.Id), session.ExpiresUtc)
            End If
            Return session.User
        End Function

        Public ReadOnly Property SessionLifetime As TimeSpan
            Get
                Return _sessionLifetime
            End Get
        End Property
    End Class

    ''' <summary>Serves the embedded dashboard files.</summary>
    Public NotInheritable Class StaticFiles
        Private ReadOnly _files As New Dictionary(Of String, (Data As Byte(), ContentType As String, ETag As String))(StringComparer.OrdinalIgnoreCase)

        Public Sub New()
            Dim asm = GetType(StaticFiles).Assembly
            For Each name In asm.GetManifestResourceNames().Where(Function(n) n.StartsWith("Dashboard/", StringComparison.Ordinal))
                Using s = asm.GetManifestResourceStream(name)
                    Using ms As New MemoryStream()
                        s.CopyTo(ms)
                        Dim data = ms.ToArray()
                        Dim rel = name.Substring("Dashboard/".Length).Replace("\"c, "/"c)
                        _files(rel) = (data, ContentTypeFor(rel), """" & TextUtil.Sha256Hex(data).Substring(0, 16) & """")
                    End Using
                End Using
            Next
        End Sub

        Private Shared Function ContentTypeFor(file As String) As String
            Select Case IO.Path.GetExtension(file).ToLowerInvariant()
                Case ".html" : Return "text/html; charset=utf-8"
                Case ".css" : Return "text/css; charset=utf-8"
                Case ".js" : Return "application/javascript; charset=utf-8"
                Case ".svg" : Return "image/svg+xml"
                Case ".png" : Return "image/png"
                Case ".ico" : Return "image/x-icon"
                Case ".json" : Return "application/json"
                Case Else : Return "application/octet-stream"
            End Select
        End Function

        Public Function TryGet(relativePath As String, ByRef file As (Data As Byte(), ContentType As String, ETag As String)) As Boolean
            Return _files.TryGetValue(relativePath.TrimStart("/"c), file)
        End Function

        ''' <summary>
        ''' Identifies this version of the dashboard (a hash of all its files). It changes only when a page or script changes, so an
        ''' open page can tell it is older than the server and offer a reload (old scripts against a newer server fail oddly).
        ''' </summary>
        Public ReadOnly Property BuildId As String
            Get
                If _buildId Is Nothing Then
                    Dim all = String.Join("|", _files.OrderBy(Function(kv) kv.Key, StringComparer.Ordinal).Select(Function(kv) kv.Key & "=" & kv.Value.ETag))
                    _buildId = TextUtil.Sha256Hex(Encoding.UTF8.GetBytes(all)).Substring(0, 12)
                End If
                Return _buildId
            End Get
        End Property
        Private _buildId As String
    End Class

    ''' <summary>
    ''' Embedded asynchronous admin web server on System.Net.HttpListener (no IIS/ASP.NET/Kestrel). Each request is
    ''' handled concurrently up to MaxConcurrentRequests; errors never leak stack traces; security headers on every response.
    ''' </summary>
    Public NotInheritable Class HttpAdminServer
        Private ReadOnly _options As ServerOptions
        Private ReadOnly _router As Router
        Private ReadOnly _auth As AuthService
        Private ReadOnly _static As StaticFiles
        Private ReadOnly _log As ILog
        Private ReadOnly _listener As New HttpListener()
        Private ReadOnly _limiter As SemaphoreSlim
        Private ReadOnly _inFlight As New ConcurrentDictionary(Of Long, Task)
        Private _requestSeq As Long
        Private _acceptLoop As Task
        Private _cts As CancellationTokenSource
        Private _accepting As Boolean

        Public Sub New(options As ServerOptions, router As Router, auth As AuthService, staticFiles As StaticFiles, log As ILog)
            _options = options
            _router = router
            _auth = auth
            _static = staticFiles
            _log = log
            _limiter = New SemaphoreSlim(Math.Max(4, options.MaxConcurrentRequests))
            If staticFiles IsNot Nothing Then BuildId = staticFiles.BuildId
        End Sub

        Public ReadOnly Property Prefix As String
            Get
                Dim h = _options.Host
                If h = "0.0.0.0" Then h = "+"
                Return $"{If(_options.UseHttps, "https", "http")}://{h}:{_options.Port}/"
            End Get
        End Property

        Public ReadOnly Property DisplayUrl As String
            Get
                Dim h = _options.Host
                If h = "+" OrElse h = "*" OrElse h = "0.0.0.0" Then h = "localhost"
                Return $"{If(_options.UseHttps, "https", "http")}://{h}:{_options.Port}"
            End Get
        End Property

        Public Sub Start(ct As CancellationToken)
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
            _listener.Prefixes.Add(Prefix)
            _listener.IgnoreWriteExceptions = True
            _listener.Start()
            _accepting = True
            _acceptLoop = AcceptLoopAsync()
        End Sub

        ''' <summary>Stops accepting new commands immediately (used early in graceful shutdown).</summary>
        Public Sub StopAcceptingCommands()
            _accepting = False
        End Sub

        Private Async Function AcceptLoopAsync() As Task
            While _listener.IsListening AndAlso Not _cts.IsCancellationRequested
                Dim ctx As HttpListenerContext
                Try
                    ctx = Await _listener.GetContextAsync().ConfigureAwait(False)
                Catch ex As HttpListenerException
                    Exit While
                Catch ex As ObjectDisposedException
                    Exit While
                Catch ex As InvalidOperationException
                    Exit While
                End Try
                ' Live event streams stay open for hours (one or two per open window): they do not count against the limit.
                Dim streamPath = ctx.Request.Url.AbsolutePath
                Dim longLived = streamPath.Equals("/api/events/stream", StringComparison.OrdinalIgnoreCase) OrElse streamPath.Equals("/api/chat/stream", StringComparison.OrdinalIgnoreCase) OrElse
                                (ctx.Request.IsWebSocketRequest AndAlso streamPath.StartsWith(PreviewProxy.PathPrefix, StringComparison.OrdinalIgnoreCase))
                If Not longLived AndAlso Not Await _limiter.WaitAsync(0).ConfigureAwait(False) Then
                    Try
                        ctx.Response.StatusCode = 503
                        ctx.Response.Close()
                    Catch
                    End Try
                    Continue While
                End If
                Dim id = Interlocked.Increment(_requestSeq)
                Dim t = HandleAsync(ctx)
                _inFlight(id) = t
                Dim ignored = t.ContinueWith(Sub(x)
                                                 Dim removed As Task = Nothing
                                                 _inFlight.TryRemove(id, removed)
                                                 If Not longLived Then _limiter.Release()
                                             End Sub, TaskScheduler.Default)
            End While
        End Function

        ''' <summary>Other origins the pages may frame (the BTCPay checkout for deposits), space-separated; set at start.</summary>
        Public Shared Property ExtraFrameSources As String = ""

        ''' <summary>The dashboard's version (StaticFiles.BuildId), sent with every response as X-Agentic-Build.</summary>
        Public Shared Property BuildId As String = ""

        ''' <summary>App previews (/container/{id}/…): set at start when they are on; Nothing = 404.</summary>
        Public Property ContainerProxy As Func(Of RequestContext, Task)

        ''' <summary>Shared artifacts' contents (/a/{token}/run, /a/{token}/site/…); their page /a/{token} is a dashboard file.</summary>
        Public Property SharedArtifacts As Func(Of RequestContext, Task)

        Private Shared Sub ApplySecurityHeaders(resp As HttpListenerResponse)
            If Not String.IsNullOrEmpty(BuildId) Then resp.Headers("X-Agentic-Build") = BuildId
            resp.Headers("X-Content-Type-Options") = "nosniff"
            resp.Headers("X-Frame-Options") = "SAMEORIGIN"
            resp.Headers("Referrer-Policy") = "no-referrer"
            resp.Headers("Content-Security-Policy") = "default-src 'self'; img-src 'self' data: blob:; style-src 'self'; script-src 'self'; connect-src 'self'; media-src 'self' blob: https://dl.smurflabs.xyz; frame-src 'self'" & If(String.IsNullOrEmpty(ExtraFrameSources), "", " " & ExtraFrameSources) & "; frame-ancestors 'self'; base-uri 'none'; form-action 'self'"
        End Sub

        Private Async Function HandleAsync(http As HttpListenerContext) As Task
            Await Task.Yield()
            Dim streaming = False
            Using reqCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token)
                Dim rc As New RequestContext(http, _options.MaxRequestBodyBytes, reqCts.Token)
                Dim failure As Exception = Nothing
                Try
                    Dim path = rc.Path
                    ' An app preview gets its own headers (an isolated origin), not the dashboard's.
                    Dim preview = http.Request.Url.AbsolutePath.StartsWith(PreviewProxy.PathPrefix, StringComparison.OrdinalIgnoreCase)
                    If Not preview Then ApplySecurityHeaders(http.Response)
                    ' Live streams and model calls (OpenAI API, chat, code runs) are long-lived: no request timeout.
                    streaming = preview OrElse path.Equals("/api/events/stream", StringComparison.OrdinalIgnoreCase) OrElse path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) OrElse
                                path.StartsWith("/api/chat/", StringComparison.OrdinalIgnoreCase) OrElse path.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) OrElse
                                path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase) OrElse path.StartsWith("/api/v2/", StringComparison.OrdinalIgnoreCase) OrElse
                                path.StartsWith("/api/updates/", StringComparison.OrdinalIgnoreCase) OrElse path.StartsWith("/api/audio/", StringComparison.OrdinalIgnoreCase) OrElse
                                path.StartsWith("/api/images/", StringComparison.OrdinalIgnoreCase) OrElse path.StartsWith("/api/music", StringComparison.OrdinalIgnoreCase)
                    If Not streaming Then reqCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _options.RequestTimeoutSeconds)))

                    If preview Then
                        If ContainerProxy Is Nothing Then Throw ApiException.NotFound("NOT_FOUND", "App previews are turned off.")
                        Await ContainerProxy(rc).ConfigureAwait(False)
                    ElseIf path.Equals("/sandbox/frame", StringComparison.OrdinalIgnoreCase) Then
                        Await SandboxFrame.ServeAsync(rc).ConfigureAwait(False)
                    ElseIf path.StartsWith("/a/", StringComparison.Ordinal) AndAlso path.IndexOf("/"c, 3) > 0 Then
                        ' A shared artifact's contents (its page, /a/{token}, is the dashboard's artifact.html).
                        If SharedArtifacts Is Nothing Then Throw ApiException.NotFound("NOT_FOUND", "This link does not exist.")
                        Await SharedArtifacts(rc).ConfigureAwait(False)
                    ElseIf Not path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) AndAlso Not path.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) AndAlso
                       Not path.StartsWith("/v2/", StringComparison.OrdinalIgnoreCase) AndAlso Not path.Equals("/healthz", StringComparison.OrdinalIgnoreCase) AndAlso
                       Not path.Equals("/install.ps1", StringComparison.OrdinalIgnoreCase) AndAlso Not path.Equals("/install.sh", StringComparison.OrdinalIgnoreCase) AndAlso
                       Not path.StartsWith("/install/", StringComparison.OrdinalIgnoreCase) Then
                        Await ServeStaticAsync(rc).ConfigureAwait(False)
                    Else
                        Dim methodMismatch As Boolean
                        Dim route = _router.Match(rc.Method, path, rc.RouteValues, methodMismatch)
                        If route Is Nothing Then
                            If methodMismatch Then Throw New ApiException(405, "METHOD_NOT_ALLOWED", "Method not allowed.")
                            Throw ApiException.NotFound("NOT_FOUND", "Endpoint does not exist.")
                        End If
                        If route.Level <> AuthLevel.Anonymous Then
                            rc.User = Await _auth.AuthenticateAsync(http.Request).ConfigureAwait(False)
                            If rc.User Is Nothing Then Throw New ApiException(401, "UNAUTHORIZED", "Authentication required.")
                            If Not rc.User.MayCall(route) Then
                                Dim label = Global.Agentic.Http.Permissions.Catalog.FirstOrDefault(Function(c) c.Key = route.Permission)?.Label
                                Throw New ApiException(403, "FORBIDDEN", If(rc.User.IsRestrictedUser,
                                    If(label Is Nothing, "Only administrators can do this.", $"Your account does not have the ""{label}"" permission."), "Insufficient permissions."))
                            End If
                            If route.AgentParam IsNot Nothing AndAlso Not rc.User.CanAccessAgent(rc.Route(route.AgentParam)) Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to this agent.")
                            If rc.Method <> "GET" AndAlso rc.User.ViaSession Then
                                Dim csrf = http.Request.Headers("X-CSRF-Token")
                                If Not TextUtil.FixedTimeEquals(csrf, rc.User.CsrfToken) Then Throw New ApiException(403, "CSRF_FAILED", "Missing or invalid CSRF token.")
                            End If
                            If rc.Method <> "GET" AndAlso Not _accepting Then Throw New ApiException(503, "SHUTTING_DOWN", "The host is shutting down.")
                        End If
                        Await route.Handler(rc).ConfigureAwait(False)
                        If Not rc.Responded Then Await rc.WriteJsonAsync(204, New With {.ok = True}).ConfigureAwait(False)
                    End If
                Catch ex As Exception
                    failure = ex
                End Try

                If failure IsNot Nothing AndAlso Not rc.Responded Then
                    Dim api = TryCast(failure, ApiException)
                    Try
                        If api IsNot Nothing Then
                            Await rc.WriteErrorAsync(api.StatusCode, api.Code, api.Message).ConfigureAwait(False)
                        ElseIf ExceptionUtil.IsCancellation(failure) Then
                            Await rc.WriteErrorAsync(503, "REQUEST_TIMEOUT", "The request timed out or the host is shutting down.").ConfigureAwait(False)
                        Else
                            _log.Err("Http", $"Unhandled error for {rc.Method} {rc.Path} (request {rc.RequestId})", failure)
                            Dim msg = If(_options.ExposeErrorDetails, failure.Message, "An internal error occurred.")
                            Await rc.WriteErrorAsync(500, "INTERNAL_ERROR", msg).ConfigureAwait(False)
                        End If
                    Catch
                    End Try
                ElseIf failure IsNot Nothing AndAlso Not ExceptionUtil.IsCancellation(failure) AndAlso Not TypeOf failure Is HttpListenerException AndAlso Not TypeOf failure Is IOException Then
                    _log.Warn("Http", $"Error after response started ({rc.Path}): {failure.Message}")
                End If
                Try
                    http.Response.Close()
                Catch
                End Try
            End Using
        End Function

        Private Async Function ServeStaticAsync(rc As RequestContext) As Task
            If rc.Method <> "GET" AndAlso rc.Method <> "HEAD" Then Throw New ApiException(405, "METHOD_NOT_ALLOWED", "Method not allowed.")
            Dim path = rc.Path
            Dim file As (Data As Byte(), ContentType As String, ETag As String) = Nothing
            Dim found = False
            If path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase) Then
                found = _static.TryGet(path.Substring("/assets/".Length), file)
                If Not found Then Throw ApiException.NotFound("NOT_FOUND", "Asset not found.")
            ElseIf path.StartsWith("/share/", StringComparison.OrdinalIgnoreCase) Then
                ' A shared chat's public, read-only page (no sign-in).
                found = _static.TryGet("share.html", file)
                If Not found Then Throw ApiException.NotFound("NOT_FOUND", "Dashboard not embedded.")
            ElseIf path.StartsWith("/a/", StringComparison.Ordinal) Then
                ' A shared artifact's public page (no sign-in): it runs the artifact in a sandboxed frame.
                found = _static.TryGet("artifact.html", file)
                If Not found Then Throw ApiException.NotFound("NOT_FOUND", "Dashboard not embedded.")
            Else
                ' Single-page app: one shell for every device. Narrow screens get the mobile layout from css/mobile.css and
                ' js/mobile.js (every page and every Chat control, like the desktop). A phone that asked for the desktop site
                ' gets it at desktop width.
                found = _static.TryGet("index.html", file)
                If Not found Then Throw ApiException.NotFound("NOT_FOUND", "Dashboard not embedded.")
                If PhoneWantsDesktop(rc) Then file = DesktopOnPhone(file)
            End If
            Dim inm = rc.Http.Request.Headers("If-None-Match")
            If inm IsNot Nothing AndAlso inm = file.ETag Then
                rc.Http.Response.StatusCode = 304
                rc.MarkResponded()
                Return
            End If
            Await rc.WriteBytesAsync(200, file.ContentType, file.Data, New Dictionary(Of String, String) From {
                {"ETag", file.ETag}, {"Cache-Control", If(path.StartsWith("/assets/", StringComparison.OrdinalIgnoreCase), "no-cache", "no-store")}}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' A phone or tablet whose user chose the desktop site: ?desktop=1 remembers that in a cookie, ?mobile=1 or ?auto=1
        ''' go back to the layout that fits the screen.
        ''' </summary>
        Private Shared Function PhoneWantsDesktop(rc As RequestContext) As Boolean
            Dim req = rc.Http.Request
            Dim forced As String = Nothing
            If rc.QueryString("desktop") = "1" Then
                forced = "desktop"
            ElseIf rc.QueryString("mobile") = "1" OrElse rc.QueryString("auto") = "1" Then
                rc.Http.Response.AppendHeader("Set-Cookie", $"{DeviceDetector.CookieName}=; Path=/; Max-Age=0; SameSite=Lax")
                Return False
            End If
            If forced IsNot Nothing Then
                rc.Http.Response.AppendHeader("Set-Cookie", $"{DeviceDetector.CookieName}={forced}; Path=/; Max-Age=31536000; SameSite=Lax")
            Else
                forced = req.Cookies(DeviceDetector.CookieName)?.Value
            End If
            Return forced = "desktop" AndAlso DeviceDetector.Classify(req.Headers("User-Agent"), req.Headers("Sec-CH-UA-Mobile")) <> DeviceKind.Desktop
        End Function

        Private _desktopOnPhone As (Data As Byte(), ContentType As String, ETag As String)

        ''' <summary>index.html laid out 1280 px wide (the phone zooms out), marked so the user menu offers the mobile layout again.</summary>
        Private Function DesktopOnPhone(file As (Data As Byte(), ContentType As String, ETag As String)) As (Data As Byte(), ContentType As String, ETag As String)
            Dim cached = _desktopOnPhone
            If cached.Data IsNot Nothing AndAlso cached.ETag.StartsWith(file.ETag.TrimEnd(""""c), StringComparison.Ordinal) Then Return cached
            Dim html = Encoding.UTF8.GetString(file.Data)
            html = Text.RegularExpressions.Regex.Replace(html, "<meta name=""viewport""[^>]*>", "<meta name=""viewport"" content=""width=1280"">")
            html = html.Replace("<html lang=""en"">", "<html lang=""en"" class=""forced-desktop"">")
            cached = (Encoding.UTF8.GetBytes(html), file.ContentType, file.ETag.TrimEnd(""""c) & "-desktop""")
            _desktopOnPhone = cached
            Return cached
        End Function

        Public Async Function StopAsync(timeout As TimeSpan) As Task
            _accepting = False
            Try
                _listener.Stop()
            Catch
            End Try
            _cts?.Cancel()
            Try
                Await Task.WhenAll(_inFlight.Values.ToArray()).WaitAsync(timeout).ConfigureAwait(False)
            Catch
            End Try
            Try
                _listener.Close()
            Catch
            End Try
        End Function
    End Class

End Namespace
