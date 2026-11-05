Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net.Http
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Security

Namespace Mcp

    ''' <summary>
    ''' MCP servers added while the server runs, without editing appsettings.json:
    '''   - by an administrator on the MCP page, for everyone (owner "*"): stdio programs on this host or HTTP servers
    '''   - by any user with chat tools, for their own chats (owner = their name): HTTP servers only, with their own headers or
    '''     an OAuth sign-in; ids start with u-&lt;user&gt;- and other users never see them
    ''' They are kept in data\mcp-servers.json; header values and sign-in tokens are encrypted (data\secrets.key).
    ''' Signing in: the server's metadata names its authorization server; Agentic registers itself, sends the browser there
    ''' with PKCE, and the redirect comes back to /api/mcp/oauth/callback.
    ''' </summary>
    Public NotInheritable Class McpUserServers

        Public Class Entry
            Public Property Id As String
            Public Property Owner As String
            Public Property Name As String
            Public Property Description As String
            ''' <summary>http | sse | stdio (administrators only)</summary>
            Public Property Transport As String = "http"
            Public Property Url As String
            Public Property Command As String
            Public Property Args As New List(Of String)
            Public Property Env As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            ''' <summary>Header name → encrypted value.</summary>
            Public Property Headers As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            Public Property Enabled As Boolean = True
            ''' <summary>Ask before each call of its tools (in every permission mode).</summary>
            Public Property AskFirst As Boolean
            ''' <summary>Encrypted OAuth state: tokens, client, token endpoint.</summary>
            Public Property OAuth As String
            Public Property CreatedUtc As DateTime = DateTime.UtcNow
        End Class

        Private ReadOnly _file As String
        Private ReadOnly _box As SecretBox
        Private ReadOnly _manager As McpServerManager
        Private ReadOnly _log As ILog
        Private ReadOnly _lock As New Object()
        Private ReadOnly _http As New HttpClient() With {.Timeout = TimeSpan.FromSeconds(30)}
        Private ReadOnly _pending As New Concurrent.ConcurrentDictionary(Of String, (Id As String, User As String, Verifier As String, ClientId As String, ClientSecret As String,
                                                                                    TokenEndpoint As String, Resource As String, Redirect As String, Started As DateTime))(StringComparer.Ordinal)

        Public Sub New(dataDir As String, box As SecretBox, manager As McpServerManager, log As ILog)
            _file = Path.Combine(dataDir, "mcp-servers.json")
            _box = box
            _manager = manager
            _log = log
        End Sub

        ' ------------------------------------------------------------------ ids

        Private Shared Function Clean(s As String, max As Integer) As String
            Dim c = Regex.Replace(If(s, "").Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim("-"c)
            Return If(c.Length > max, c.Substring(0, max), c)
        End Function

        ''' <summary>A user's own servers' ids start with this.</summary>
        Public Shared Function PersonalPrefix(username As String) As String
            Return "u-" & Clean(username, 16) & "-"
        End Function

        Public Shared Function IsPersonalOf(serverId As String, username As String) As Boolean
            Return Not String.IsNullOrEmpty(username) AndAlso serverId IsNot Nothing AndAlso serverId.StartsWith(PersonalPrefix(username), StringComparison.OrdinalIgnoreCase)
        End Function

        Public Shared Function IsPersonal(serverId As String) As Boolean
            Return serverId IsNot Nothing AndAlso serverId.StartsWith("u-", StringComparison.OrdinalIgnoreCase)
        End Function

        ' ------------------------------------------------------------------ the file

        Public Function All() As List(Of Entry)
            SyncLock _lock
                Try
                    If Not File.Exists(_file) Then Return New List(Of Entry)
                    Return If(JsonSerializer.Deserialize(Of List(Of Entry))(File.ReadAllText(_file)), New List(Of Entry))
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UnauthorizedAccessException
                    _log.Warn("MCP", "mcp-servers.json could not be read: " & ex.Message)
                    Return New List(Of Entry)
                End Try
            End SyncLock
        End Function

        Private Sub SaveAll(list As List(Of Entry))
            SyncLock _lock
                Dim tmp = _file & ".tmp"
                File.WriteAllText(tmp, JsonSerializer.Serialize(list, New JsonSerializerOptions With {.WriteIndented = True}))
                File.Move(tmp, _file, True)
            End SyncLock
        End Sub

        Private Function Update(id As String, change As Action(Of Entry)) As Entry
            SyncLock _lock
                Dim list = All()
                Dim e = list.FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                If e Is Nothing Then Return Nothing
                change(e)
                SaveAll(list)
                Return e
            End SyncLock
        End Function

        ''' <summary>Starts every enabled server from the file (at start-up).</summary>
        Public Sub LoadAll()
            For Each e In All()
                Apply(e)
            Next
        End Sub

        ''' <summary>The entry as a running server (or stops it when switched off).</summary>
        Private Sub Apply(e As Entry)
            Dim o As New McpServerOptions With {
                .Id = e.Id, .Name = If(e.Name, e.Id), .Description = e.Description, .Enabled = e.Enabled, .Owner = e.Owner,
                .Transport = If(e.Transport = "stdio", McpTransportKind.Stdio, McpTransportKind.Http),
                .HttpMode = If(e.Transport = "sse", "Sse", "AutoDetect"), .Endpoint = e.Url, .Command = e.Command,
                .Arguments = New List(Of String)(If(e.Args, New List(Of String))), .ConnectTimeoutSeconds = 30, .TimeoutSeconds = 120}
            For Each kv In If(e.Env, New Dictionary(Of String, String))
                o.EnvironmentVariables(kv.Key) = kv.Value
            Next
            For Each kv In If(e.Headers, New Dictionary(Of String, String))
                Dim v = _box.Unprotect(kv.Value)
                If v IsNot Nothing Then o.Headers(kv.Key) = v
            Next
            ' Its tools ask first (also in Auto mode) when the owner wants that.
            o.AskFirst = e.AskFirst
            Dim id = e.Id
            o.TokenProvider = Function() TokenAsync(id)
            _manager.AddOrReplace(o)
        End Sub

        ''' <summary>Adds or changes a server. Users add HTTP servers for themselves; administrators also stdio ones and shared ones.</summary>
        Public Function Save(user As String, isAdmin As Boolean, forEveryone As Boolean, name As String, transport As String, url As String, command As String,
                             args As List(Of String), env As Dictionary(Of String, String), headers As Dictionary(Of String, String),
                             askFirst As Boolean, description As String, Optional existingId As String = Nothing) As Entry
            transport = If(transport, "http").Trim().ToLowerInvariant()
            If transport <> "http" AndAlso transport <> "sse" AndAlso transport <> "stdio" Then Throw New ArgumentException("transport must be http, sse or stdio.")
            If transport = "stdio" AndAlso Not isAdmin Then Throw New UnauthorizedAccessException("Only administrators add servers that run programs on the Agentic server; add an HTTP server, or one on your computer in the desktop app or VS Code.")
            If forEveryone AndAlso Not isAdmin Then Throw New UnauthorizedAccessException("Only administrators add servers for everyone.")
            If transport = "stdio" AndAlso String.IsNullOrWhiteSpace(command) Then Throw New ArgumentException("Give the command that starts the server.")
            If transport <> "stdio" Then
                Dim u As Uri = Nothing
                If Not Uri.TryCreate(If(url, "").Trim(), UriKind.Absolute, u) OrElse (u.Scheme <> "https" AndAlso u.Scheme <> "http") Then Throw New ArgumentException("Give the server's http(s) URL.")
            End If
            Dim slug = Clean(name, 24)
            If slug.Length = 0 Then Throw New ArgumentException("Give the server a name.")
            Dim owner = If(forEveryone, "*", user)
            Dim id = If(existingId, If(forEveryone, slug, PersonalPrefix(user) & slug))
            If id.Length > 40 Then id = id.Substring(0, 40)
            SyncLock _lock
                Dim list = All()
                Dim before = list.FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                If before IsNot Nothing AndAlso Not isAdmin AndAlso before.Owner <> user Then Throw New UnauthorizedAccessException("That server is not yours.")
                If before Is Nothing AndAlso _manager.GetServerOptions(id) IsNot Nothing Then Throw New ArgumentException($"There is a server called {id} already; choose another name.")
                If Not isAdmin AndAlso before Is Nothing AndAlso list.Where(Function(x) x.Owner = user).Count() >= 20 Then Throw New ArgumentException("You have 20 MCP servers already.")
                Dim e = If(before, New Entry With {.Id = id, .Owner = owner})
                e.Name = If(String.IsNullOrWhiteSpace(name), id, name.Trim())
                e.Description = description
                e.Transport = transport
                e.Url = If(transport = "stdio", Nothing, url.Trim())
                e.Command = If(transport = "stdio", command.Trim(), Nothing)
                e.Args = If(transport = "stdio", If(args, New List(Of String)), New List(Of String))
                e.Env = If(transport = "stdio", If(env, New Dictionary(Of String, String)), New Dictionary(Of String, String))
                e.AskFirst = askFirst
                ' Header values: new ones replace, a value left as "•••" keeps the old one, an empty value removes the header.
                Dim old = If(e.Headers, New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase))
                Dim fresh As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                For Each kv In If(headers, New Dictionary(Of String, String))
                    If String.IsNullOrWhiteSpace(kv.Key) OrElse String.IsNullOrEmpty(kv.Value) Then Continue For
                    If kv.Value = "•••" Then
                        If old.ContainsKey(kv.Key) Then fresh(kv.Key) = old(kv.Key)
                    Else
                        fresh(kv.Key.Trim()) = _box.Protect(kv.Value)
                    End If
                Next
                e.Headers = fresh
                If before Is Nothing Then list.Add(e)
                SaveAll(list)
                Apply(e)
                Return e
            End SyncLock
        End Function

        Public Function MayManage(id As String, user As String, isAdmin As Boolean) As Boolean
            Dim e = All().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            Return e IsNot Nothing AndAlso (isAdmin OrElse e.Owner = user)
        End Function

        Public Function Remove(id As String) As Boolean
            SyncLock _lock
                Dim list = All()
                Dim n = list.RemoveAll(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                If n = 0 Then Return False
                SaveAll(list)
            End SyncLock
            _manager.RemoveServer(id)
            Return True
        End Function

        Public Sub SetEnabled(id As String, enabled As Boolean)
            Dim e = Update(id, Sub(x) x.Enabled = enabled)
            If e IsNot Nothing Then Apply(e)
        End Sub

        ' ------------------------------------------------------------------ OAuth

        Private Shared Function S(o As JsonNode, name As String) As String
            Dim n = TryCast(o, JsonObject)?(name)
            If n Is Nothing Then Return Nothing
            Return If(n.GetValueKind() = JsonValueKind.String, n.GetValue(Of String)(), n.ToJsonString())
        End Function

        Private Shared Function B64Url(b As Byte()) As String
            Return Convert.ToBase64String(b).Replace("+"c, "-"c).Replace("/"c, "_"c).TrimEnd("="c)
        End Function

        Private Async Function GetJsonAsync(url As String) As Task(Of JsonObject)
            Try
                Using req As New HttpRequestMessage(HttpMethod.Get, url)
                    req.Headers.TryAddWithoutValidation("Accept", "application/json")
                    req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", "2025-06-18")
                    Using res = Await _http.SendAsync(req).ConfigureAwait(False)
                        If Not res.IsSuccessStatusCode Then Return Nothing
                        Return TryCast(JsonNode.Parse(Await res.Content.ReadAsStringAsync().ConfigureAwait(False)), JsonObject)
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is UriFormatException
                Return Nothing
            End Try
        End Function

        Private Async Function PostFormAsync(url As String, form As Dictionary(Of String, String)) As Task(Of JsonObject)
            Using content As New FormUrlEncodedContent(form)
                Using res = Await _http.PostAsync(url, content).ConfigureAwait(False)
                    Dim text = Await res.Content.ReadAsStringAsync().ConfigureAwait(False)
                    Dim j As JsonObject = Nothing
                    Try
                        j = TryCast(JsonNode.Parse(text), JsonObject)
                    Catch ex As JsonException
                    End Try
                    If Not res.IsSuccessStatusCode OrElse j Is Nothing OrElse j("error") IsNot Nothing Then
                        Throw New InvalidOperationException("The sign-in server refused: " & If(S(j, "error_description"), If(S(j, "error"), "HTTP " & CInt(res.StatusCode))))
                    End If
                    Return j
                End Using
            End Using
        End Function

        ''' <summary>
        ''' Starts a sign-in: where to send the browser. redirectUri is this server's /api/mcp/oauth/callback as the browser sees it.
        ''' </summary>
        Public Async Function StartSignInAsync(id As String, user As String, redirectUri As String) As Task(Of String)
            Dim e = All().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            If e Is Nothing OrElse e.Transport = "stdio" Then Throw New ArgumentException("Only HTTP servers sign in.")
            Dim u As New Uri(e.Url)
            Dim origin = u.GetLeftPart(UriPartial.Authority)
            Dim p = u.AbsolutePath.TrimEnd("/"c)
            ' The server's 401 names its metadata.
            Dim www As String = Nothing
            Try
                Using req As New HttpRequestMessage(HttpMethod.Post, e.Url) With {.Content = New StringContent("{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2025-06-18"",""capabilities"":{},""clientInfo"":{""name"":""Agentic"",""version"":""1""}}}", Encoding.UTF8, "application/json")}
                    req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream")
                    Using res = Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(False)
                        If res.Headers.WwwAuthenticate.Count > 0 Then www = String.Join(", ", res.Headers.WwwAuthenticate.Select(Function(h) h.ToString()))
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is TaskCanceledException
            End Try
            Dim named = Regex.Match(If(www, ""), "resource_metadata=""([^""]+)""", RegexOptions.IgnoreCase)
            Dim scopeHint = Regex.Match(If(www, ""), "scope=""([^""]+)""", RegexOptions.IgnoreCase)
            Dim prm As JsonObject = Nothing
            For Each c In {If(named.Success, New Uri(u, named.Groups(1).Value).ToString(), ""), If(p.Length > 0, origin & "/.well-known/oauth-protected-resource" & p, ""), origin & "/.well-known/oauth-protected-resource"}
                If c.Length = 0 Then Continue For
                prm = Await GetJsonAsync(c).ConfigureAwait(False)
                If prm IsNot Nothing Then Exit For
            Next
            Dim issuer = origin
            Dim servers = TryCast(prm?("authorization_servers"), JsonArray)
            If servers IsNot Nothing AndAlso servers.Count > 0 Then issuer = servers(0).ToString().TrimEnd("/"c)
            Dim iu As New Uri(issuer)
            Dim iorigin = iu.GetLeftPart(UriPartial.Authority)
            Dim ipath = iu.AbsolutePath.TrimEnd("/"c)
            Dim meta As JsonObject = Nothing
            For Each c In If(ipath.Length > 0, {iorigin & "/.well-known/oauth-authorization-server" & ipath, iorigin & "/.well-known/openid-configuration" & ipath},
                                               {iorigin & "/.well-known/oauth-authorization-server", iorigin & "/.well-known/openid-configuration"})
                meta = Await GetJsonAsync(c).ConfigureAwait(False)
                If meta IsNot Nothing AndAlso S(meta, "authorization_endpoint") IsNot Nothing Then Exit For
                meta = Nothing
            Next
            Dim authorize = If(S(meta, "authorization_endpoint"), iorigin & "/authorize")
            Dim tokenEndpoint = If(S(meta, "token_endpoint"), iorigin & "/token")
            Dim register = If(meta Is Nothing, iorigin & "/register", S(meta, "registration_endpoint"))
            Dim resource = If(S(prm, "resource"), e.Url.Split("?"c)(0))
            If String.IsNullOrEmpty(register) Then Throw New InvalidOperationException("This server does not let apps register themselves, so Agentic cannot sign in to it. Give it a token header instead.")
            Dim reg As New JsonObject From {{"client_name", "Agentic"}, {"redirect_uris", New JsonArray(JsonValue.Create(redirectUri))},
                                            {"grant_types", New JsonArray(JsonValue.Create("authorization_code"), JsonValue.Create("refresh_token"))},
                                            {"response_types", New JsonArray(JsonValue.Create("code"))}, {"token_endpoint_auth_method", "none"}}
            Dim clientId As String, clientSecret As String = Nothing
            Using content As New StringContent(reg.ToJsonString(), Encoding.UTF8, "application/json")
                Using res = Await _http.PostAsync(register, content).ConfigureAwait(False)
                    Dim j As JsonObject = Nothing
                    Try
                        j = TryCast(JsonNode.Parse(Await res.Content.ReadAsStringAsync().ConfigureAwait(False)), JsonObject)
                    Catch ex As JsonException
                    End Try
                    If Not res.IsSuccessStatusCode OrElse S(j, "client_id") Is Nothing Then Throw New InvalidOperationException("The server refused to register Agentic: " & If(S(j, "error_description"), If(S(j, "error"), "HTTP " & CInt(res.StatusCode))))
                    clientId = S(j, "client_id")
                    clientSecret = S(j, "client_secret")
                End Using
            End Using
            Dim verifier = B64Url(RandomNumberGenerator.GetBytes(48))
            Dim challenge = B64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            Dim state = B64Url(RandomNumberGenerator.GetBytes(18))
            ' Sign-ins not finished within 15 minutes are forgotten.
            For Each old In _pending.Where(Function(kv) DateTime.UtcNow - kv.Value.Started > TimeSpan.FromMinutes(15)).Select(Function(kv) kv.Key).ToList()
                Dim gone As (String, String, String, String, String, String, String, String, DateTime) = Nothing
                _pending.TryRemove(old, gone)
            Next
            _pending(state) = (e.Id, user, verifier, clientId, clientSecret, tokenEndpoint, resource, redirectUri, DateTime.UtcNow)
            Dim q As New List(Of String) From {"response_type=code", "client_id=" & Uri.EscapeDataString(clientId), "redirect_uri=" & Uri.EscapeDataString(redirectUri),
                                               "code_challenge=" & challenge, "code_challenge_method=S256", "state=" & state, "resource=" & Uri.EscapeDataString(resource)}
            Dim scope = If(scopeHint.Success, scopeHint.Groups(1).Value, If(TypeOf prm?("scopes_supported") Is JsonArray, String.Join(" ", DirectCast(prm("scopes_supported"), JsonArray).Select(Function(x) x.ToString())), Nothing))
            If Not String.IsNullOrEmpty(scope) Then q.Add("scope=" & Uri.EscapeDataString(scope))
            Return authorize & If(authorize.Contains("?"c), "&", "?") & String.Join("&", q)
        End Function

        ''' <summary>The browser came back: the code becomes tokens, kept encrypted with the server. Gives the server's id.</summary>
        Public Async Function FinishSignInAsync(state As String, code As String) As Task(Of String)
            Dim p As (Id As String, User As String, Verifier As String, ClientId As String, ClientSecret As String, TokenEndpoint As String, Resource As String, Redirect As String, Started As DateTime) = Nothing
            If String.IsNullOrEmpty(state) OrElse Not _pending.TryRemove(state, p) Then Throw New ArgumentException("This sign-in is unknown or expired; start it again from the MCP page.")
            Dim form As New Dictionary(Of String, String) From {{"grant_type", "authorization_code"}, {"code", code}, {"redirect_uri", p.Redirect},
                                                                {"client_id", p.ClientId}, {"code_verifier", p.Verifier}, {"resource", p.Resource}}
            If Not String.IsNullOrEmpty(p.ClientSecret) Then form("client_secret") = p.ClientSecret
            Dim tok = Await PostFormAsync(p.TokenEndpoint, form).ConfigureAwait(False)
            If S(tok, "access_token") Is Nothing Then Throw New InvalidOperationException("The sign-in server sent no access token.")
            Dim saved As New JsonObject From {{"access_token", S(tok, "access_token")}, {"refresh_token", S(tok, "refresh_token")}, {"expires_at", ExpiresAt(tok)},
                                              {"client_id", p.ClientId}, {"client_secret", p.ClientSecret}, {"token_endpoint", p.TokenEndpoint}, {"resource", p.Resource}}
            Dim e = Update(p.Id, Sub(x) x.OAuth = _box.Protect(saved.ToJsonString()))
            If e IsNot Nothing Then Apply(e)
            Return p.Id
        End Function

        Private Shared Function ExpiresAt(j As JsonObject) As Long
            Dim secs As Long = 0
            If Long.TryParse(If(S(j, "expires_in"), ""), secs) AndAlso secs > 0 Then Return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + secs * 1000
            Return 0
        End Function

        Public Function SignedIn(id As String) As Boolean
            Return Not String.IsNullOrEmpty(All().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))?.OAuth)
        End Function

        Public Sub SignOut(id As String)
            Dim e = Update(id, Sub(x) x.OAuth = Nothing)
            If e IsNot Nothing Then Apply(e)
        End Sub

        ''' <summary>The access token for a server (refreshed when it runs out within a minute); Nothing when not signed in.</summary>
        Public Async Function TokenAsync(id As String) As Task(Of String)
            Dim e = All().FirstOrDefault(Function(x) x.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            If e Is Nothing OrElse String.IsNullOrEmpty(e.OAuth) Then Return Nothing
            Dim plain = _box.Unprotect(e.OAuth)
            If plain Is Nothing Then Return Nothing
            Dim j = TryCast(JsonNode.Parse(plain), JsonObject)
            Dim expires As Long = 0
            Long.TryParse(If(S(j, "expires_at"), "0"), expires)
            If expires = 0 OrElse expires > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000 OrElse String.IsNullOrEmpty(S(j, "refresh_token")) Then Return S(j, "access_token")
            Try
                Dim form As New Dictionary(Of String, String) From {{"grant_type", "refresh_token"}, {"refresh_token", S(j, "refresh_token")}, {"client_id", S(j, "client_id")}, {"resource", S(j, "resource")}}
                If Not String.IsNullOrEmpty(S(j, "client_secret")) Then form("client_secret") = S(j, "client_secret")
                Dim tok = Await PostFormAsync(S(j, "token_endpoint"), form).ConfigureAwait(False)
                j("access_token") = S(tok, "access_token")
                If S(tok, "refresh_token") IsNot Nothing Then j("refresh_token") = S(tok, "refresh_token")
                j("expires_at") = ExpiresAt(tok)
                Dim boxed = _box.Protect(j.ToJsonString())
                Update(id, Sub(x) x.OAuth = boxed)
                Return S(j, "access_token")
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is TaskCanceledException OrElse TypeOf ex Is JsonException
                _log.Warn("MCP", $"Refreshing the sign-in of {id} failed: {ex.Message}")
                Return S(j, "access_token")
            End Try
        End Function

    End Class

End Namespace
