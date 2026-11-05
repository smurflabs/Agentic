Option Strict On
Option Explicit On
Option Infer On

Imports System.ComponentModel
Imports System.IO
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Threading
Imports ModelContextProtocol
Imports ModelContextProtocol.Client
Imports ModelContextProtocol.Protocol
Imports ModelContextProtocol.Server

Namespace Mcp

    ''' <summary>
    ''' Built-in "gitea" MCP server ("Command": "{self}", "Arguments": ["--mcp-gitea", "--gitea-mcp", "...\gitea-mcp.exe"]).
    ''' It runs the official gitea-mcp server (repositories, files, branches, commits, tags, releases, issues, pull requests
    ''' and reviews, labels, milestones, Actions, wiki, packages, projects, notifications, search) and passes its tools
    ''' through unchanged. It adds api_* tools that reach every other Gitea REST endpoint (repository settings, webhooks,
    ''' branch protection, collaborators, teams, organizations, deploy keys, admin): api_find_endpoint looks endpoints up
    ''' in the instance's own API spec, and api_get / api_write / api_delete call them.
    ''' The instance and the bot account's token come from GITEA_HOST and GITEA_ACCESS_TOKEN; requests never leave that host.
    ''' </summary>
    Public NotInheritable Class GiteaServer
        Implements IDisposable

        Private ReadOnly _host As String
        Private ReadOnly _http As HttpClient
        Private ReadOnly _maxOutputChars As Integer
        Private ReadOnly _upstreamPath As String
        Private ReadOnly _upstreamLock As New SemaphoreSlim(1, 1)
        Private ReadOnly _specLock As New SemaphoreSlim(1, 1)
        Private _upstream As McpClient
        Private _spec As List(Of ApiOperation)

        Friend NotInheritable Class ApiOperation
            Public Property Method As String
            Public Property Path As String
            Public Property Summary As String
            Public Property OperationId As String
            Public Property Tag As String
            Public Property Parameters As New List(Of String)
            Public Property BodyFields As New List(Of String)
            Public Property Haystack As String
        End Class

        Public Sub New(host As String, token As String, Optional upstreamPath As String = Nothing, Optional maxOutputKb As Integer = 64,
                       Optional insecure As Boolean = False, Optional handler As HttpMessageHandler = Nothing)
            _host = NormalizeHost(host)
            _upstreamPath = upstreamPath
            _maxOutputChars = Math.Clamp(maxOutputKb, 8, 1024) * 1024
            If handler Is Nothing Then
                Dim h As New HttpClientHandler()
                If insecure Then h.ServerCertificateCustomValidationCallback = Function(m, c, ch, e) True
                handler = h
            End If
            _http = New HttpClient(handler) With {.Timeout = TimeSpan.FromSeconds(100)}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic-Gitea/" & HostApplication.Version)
            If Not String.IsNullOrWhiteSpace(token) Then _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "token " & token.Trim())
        End Sub

        Public ReadOnly Property Host As String
            Get
                Return _host
            End Get
        End Property

        Friend Shared Function NormalizeHost(host As String) As String
            Dim h = If(host, "").Trim().TrimEnd("/"c)
            If h.EndsWith("/api/v1", StringComparison.OrdinalIgnoreCase) Then h = h.Substring(0, h.Length - 7)
            Dim u As Uri = Nothing
            If Not Uri.TryCreate(h, UriKind.Absolute, u) OrElse (u.Scheme <> Uri.UriSchemeHttps AndAlso u.Scheme <> Uri.UriSchemeHttp) Then
                Throw New ArgumentException("GITEA_HOST must be the instance's http(s) URL, e.g. https://git.example.com.")
            End If
            Return h
        End Function

        Public Shared Function RunStdio(args As String()) As Integer
            Dim upstream As String = Nothing, maxKb = 64
            Dim i = 0
            While i < args.Length
                Select Case args(i).ToLowerInvariant()
                    Case "--gitea-mcp" : If i + 1 < args.Length Then i += 1 : upstream = args(i)
                    Case "--max-output-kb" : If i + 1 < args.Length Then i += 1 : Integer.TryParse(args(i), maxKb)
                End Select
                i += 1
            End While
            Dim hostUrl = Environment.GetEnvironmentVariable("GITEA_HOST")
            Dim token = Environment.GetEnvironmentVariable("GITEA_ACCESS_TOKEN")
            If String.IsNullOrWhiteSpace(hostUrl) OrElse String.IsNullOrWhiteSpace(token) Then
                Console.Error.WriteLine("gitea: set GITEA_HOST (EnvironmentVariables) and the access token (Auth.Secret with HeaderName GITEA_ACCESS_TOKEN).")
                Return 2
            End If
            upstream = ResolveUpstream(upstream)
            If upstream Is Nothing Then Console.Error.WriteLine("gitea: gitea-mcp.exe not found; only the api_* tools are available.")
            Dim insecure = String.Equals(Environment.GetEnvironmentVariable("GITEA_INSECURE"), "true", StringComparison.OrdinalIgnoreCase)

            Using server As New GiteaServer(hostUrl, token, upstream, maxKb, insecure)
                Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
                tools.Add(McpServerTool.Create(New Func(Of String, Integer, Task(Of String))(AddressOf server.FindEndpointAsync), New McpServerToolCreateOptions With {
                    .Name = "api_find_endpoint", .ReadOnly = True, .Idempotent = True,
                    .Description = "Find Gitea REST API endpoints for anything the other Gitea tools do not cover (repository settings, webhooks, " &
                                   "branch protection, collaborators, teams, organizations, deploy keys, topics, mirrors, admin). Searches this " &
                                   "instance's API spec and returns method, path, parameters and body fields. Then call api_get, api_write or api_delete."}))
                tools.Add(McpServerTool.Create(New Func(Of String, String, Task(Of String))(AddressOf server.ApiGetAsync), New McpServerToolCreateOptions With {
                    .Name = "api_get", .ReadOnly = True, .Idempotent = True,
                    .Description = "GET any Gitea REST API path (relative to /api/v1, e.g. /repos/{owner}/{repo}/hooks). Use page and limit in query for lists."}))
                tools.Add(McpServerTool.Create(New Func(Of String, String, String, String, Task(Of String))(AddressOf server.ApiWriteAsync), New McpServerToolCreateOptions With {
                    .Name = "api_write", .ReadOnly = False, .Destructive = False,
                    .Description = "POST, PUT or PATCH any Gitea REST API path (relative to /api/v1) with a JSON body. Use api_find_endpoint first to get the body fields."}))
                tools.Add(McpServerTool.Create(New Func(Of String, String, String, Task(Of String))(AddressOf server.ApiDeleteAsync), New McpServerToolCreateOptions With {
                    .Name = "api_delete", .ReadOnly = False, .Destructive = True,
                    .Description = "DELETE any Gitea REST API path (relative to /api/v1). Permanent: deletes repositories, branches, webhooks, keys, users and so on."}))
                Return BuiltInServer.Run("agentic-gitea", tools, Sub(o)
                                                                     If upstream Is Nothing Then Return
                                                                     o.Handlers.ListToolsHandler = Function(ctx, ct) New ValueTask(Of ListToolsResult)(server.ListUpstreamToolsAsync(ctx.Params, ct))
                                                                     o.Handlers.CallToolHandler = Function(ctx, ct) New ValueTask(Of CallToolResult)(server.CallUpstreamToolAsync(ctx.Params, ct))
                                                                 End Sub)
            End Using
        End Function

        Friend Shared Function ResolveUpstream(configured As String) As String
            If Not String.IsNullOrWhiteSpace(configured) Then Return If(File.Exists(configured), Path.GetFullPath(configured), Nothing)
            Dim baseDir = AppContext.BaseDirectory
            For Each candidate In {Path.Combine(baseDir, "gitea-mcp.exe"), Path.Combine(baseDir, "tools", "gitea-mcp", "gitea-mcp.exe"),
                                   Path.Combine(baseDir, "..", "tools", "gitea-mcp", "gitea-mcp.exe")}
                If File.Exists(candidate) Then Return Path.GetFullPath(candidate)
            Next
            Return Nothing
        End Function

        ' ------------------------------------------------------------------ gitea-mcp pass-through

        Private Async Function GetUpstreamAsync(ct As CancellationToken) As Task(Of McpClient)
            Dim c = _upstream
            If c IsNot Nothing Then Return c
            Await _upstreamLock.WaitAsync(ct).ConfigureAwait(False)
            Try
                If _upstream Is Nothing Then
                    ' gitea-mcp inherits GITEA_HOST / GITEA_ACCESS_TOKEN (and GITEA_READONLY, GITEA_TOOLS, ...) from this process.
                    Dim transport As New StdioClientTransport(New StdioClientTransportOptions With {
                        .Name = "gitea-mcp", .Command = _upstreamPath, .Arguments = New List(Of String) From {"-t", "stdio"}}, Nothing)
                    _upstream = Await McpClient.CreateAsync(transport, New McpClientOptions With {
                        .ClientInfo = New Implementation With {.Name = "agentic-gitea", .Version = HostApplication.Version}}, Nothing, ct).ConfigureAwait(False)
                End If
                Return _upstream
            Finally
                _upstreamLock.Release()
            End Try
        End Function

        ''' <summary>Drops a broken connection so the next call starts gitea-mcp again.</summary>
        Private Sub ResetUpstream(broken As McpClient)
            If broken Is Nothing OrElse Not ReferenceEquals(Interlocked.CompareExchange(Of McpClient)(_upstream, Nothing, broken), broken) Then Return
            Try
                broken.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))
            Catch
            End Try
        End Sub

        Friend Async Function ListUpstreamToolsAsync(p As ListToolsRequestParams, ct As CancellationToken) As Task(Of ListToolsResult)
            Dim c = Await GetUpstreamAsync(ct).ConfigureAwait(False)
            Try
                Return Await c.ListToolsAsync(If(p, New ListToolsRequestParams()), ct).ConfigureAwait(False)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException AndAlso Not TypeOf ex Is McpProtocolException
                ResetUpstream(c)
                Throw
            End Try
        End Function

        Friend Async Function CallUpstreamToolAsync(p As CallToolRequestParams, ct As CancellationToken) As Task(Of CallToolResult)
            Dim c = Await GetUpstreamAsync(ct).ConfigureAwait(False)
            Try
                Return Await c.CallToolAsync(p, ct).ConfigureAwait(False)
            Catch ex As Exception When Not TypeOf ex Is OperationCanceledException AndAlso Not TypeOf ex Is McpProtocolException
                ResetUpstream(c)
                Throw
            End Try
        End Function

        ' ------------------------------------------------------------------ REST API tools

        Friend Function ApiGetAsync(<Description("API path relative to /api/v1, e.g. /repos/{owner}/{repo}/branch_protections")> path As String,
                                    <Description("Query string: a JSON object such as {""page"":1,""limit"":50} or a=1&b=2 (optional)")> Optional query As String = "") As Task(Of String)
            Return SendAsync(HttpMethod.Get, path, query, Nothing)
        End Function

        Friend Function ApiWriteAsync(<Description("POST, PUT or PATCH")> method As String,
                                      <Description("API path relative to /api/v1, e.g. /repos/{owner}/{repo}/hooks")> path As String,
                                      <Description("JSON request body (optional for some endpoints)")> Optional body As String = "",
                                      <Description("Query string: a JSON object or a=1&b=2 (optional)")> Optional query As String = "") As Task(Of String)
            Dim m = If(method, "").Trim().ToUpperInvariant()
            If m <> "POST" AndAlso m <> "PUT" AndAlso m <> "PATCH" Then Throw New McpException("method must be POST, PUT or PATCH (use api_get or api_delete for the others).")
            Return SendAsync(New HttpMethod(m), path, query, body)
        End Function

        Friend Function ApiDeleteAsync(<Description("API path relative to /api/v1, e.g. /repos/{owner}/{repo}/hooks/{id}")> path As String,
                                       <Description("Query string: a JSON object or a=1&b=2 (optional)")> Optional query As String = "",
                                       <Description("JSON request body, for the few DELETE endpoints that take one (optional)")> Optional body As String = "") As Task(Of String)
            Return SendAsync(HttpMethod.Delete, path, query, body)
        End Function

        ''' <summary>Builds the request URL, keeping every request on the configured instance's /api/v1.</summary>
        Friend Function BuildUrl(apiPath As String, query As String) As String
            Dim p = If(apiPath, "").Trim()
            If p.Length = 0 Then Throw New McpException("path is required, e.g. /repos/{owner}/{repo}.")
            If p.Contains("://") OrElse p.StartsWith("//", StringComparison.Ordinal) OrElse p.Contains("\"c) OrElse p.Contains("#"c) OrElse
               p.Split("/"c).Any(Function(seg) seg = ".." OrElse seg = "." OrElse Uri.UnescapeDataString(seg) = "..") OrElse p.Any(Function(ch) Char.IsControl(ch) OrElse ch = " "c) Then
                Throw New McpException("path must be an API path relative to /api/v1 (no host, '..', spaces or '#').")
            End If
            If Not p.StartsWith("/"c) Then p = "/" & p
            If p.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase) Then p = p.Substring(7)
            Dim q = p.IndexOf("?"c)
            Dim inlineQuery = ""
            If q >= 0 Then inlineQuery = p.Substring(q + 1) : p = p.Substring(0, q)
            Dim parts As New List(Of String)
            If inlineQuery.Length > 0 Then parts.Add(inlineQuery)
            Dim extra = BuildQuery(query)
            If extra.Length > 0 Then parts.Add(extra)
            Return _host & "/api/v1" & p & If(parts.Count > 0, "?" & String.Join("&", parts), "")
        End Function

        Friend Shared Function BuildQuery(query As String) As String
            Dim s = If(query, "").Trim()
            If s.Length = 0 Then Return ""
            If s.StartsWith("?"c) Then s = s.Substring(1)
            If Not s.StartsWith("{"c) Then
                If s.Contains("#"c) OrElse s.Any(Function(ch) Char.IsControl(ch)) Then Throw New McpException("query must not contain '#' or control characters.")
                Return s.Replace(" ", "%20")
            End If
            Dim doc As JsonDocument
            Try
                doc = JsonDocument.Parse(s)
            Catch ex As JsonException
                Throw New McpException("query is not valid JSON: " & ex.Message)
            End Try
            Using doc
                If doc.RootElement.ValueKind <> JsonValueKind.Object Then Throw New McpException("query JSON must be an object.")
                Dim parts As New List(Of String)
                For Each prop In doc.RootElement.EnumerateObject()
                    Dim values = If(prop.Value.ValueKind = JsonValueKind.Array, prop.Value.EnumerateArray().ToList(), New List(Of JsonElement) From {prop.Value})
                    For Each v In values
                        If v.ValueKind = JsonValueKind.Null Then Continue For
                        Dim text = If(v.ValueKind = JsonValueKind.String, v.GetString(), v.GetRawText())
                        parts.Add(Uri.EscapeDataString(prop.Name) & "=" & Uri.EscapeDataString(text))
                    Next
                Next
                Return String.Join("&", parts)
            End Using
        End Function

        Private Async Function SendAsync(method As HttpMethod, apiPath As String, query As String, body As String) As Task(Of String)
            Dim url = BuildUrl(apiPath, query)
            Using req As New HttpRequestMessage(method, url)
                If Not String.IsNullOrWhiteSpace(body) Then
                    Try
                        Using JsonDocument.Parse(body)
                        End Using
                    Catch ex As JsonException
                        Throw New McpException("body is not valid JSON: " & ex.Message)
                    End Try
                    req.Content = New StringContent(body, Encoding.UTF8, "application/json")
                End If
                req.Headers.Accept.ParseAdd("application/json")
                Dim resp As HttpResponseMessage
                Try
                    resp = Await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead).ConfigureAwait(False)
                Catch ex As HttpRequestException
                    Throw New McpException($"Could not reach {_host}: {ex.Message}")
                Catch ex As TaskCanceledException
                    Throw New McpException($"{method} {url} timed out.")
                End Try
                Using resp
                    Dim text = Await DescribeResponseAsync(resp).ConfigureAwait(False)
                    Dim head = $"{CInt(resp.StatusCode)} {resp.ReasonPhrase} · {method} {url.Substring(_host.Length)}"
                    If Not resp.IsSuccessStatusCode Then Throw New McpException(head & vbLf & text)
                    Return head & vbLf & text
                End Using
            End Using
        End Function

        Private Async Function DescribeResponseAsync(resp As HttpResponseMessage) As Task(Of String)
            Dim sb As New StringBuilder()
            For Each h In {"X-Total-Count", "Link", "Location"}
                Dim values As IEnumerable(Of String) = Nothing
                If resp.Headers.TryGetValues(h, values) Then sb.Append(h).Append(": ").Append(String.Join(", ", values)).Append(vbLf)
            Next
            Dim bytes = Await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(False)
            If bytes.Length = 0 Then Return sb.Append("(no content)").ToString()
            Dim mediaType = If(resp.Content.Headers.ContentType?.MediaType, "")
            Dim isText = mediaType.Length = 0 OrElse mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) OrElse
                         mediaType.Contains("json", StringComparison.OrdinalIgnoreCase) OrElse mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) OrElse
                         mediaType.Contains("diff", StringComparison.OrdinalIgnoreCase) OrElse mediaType.Contains("patch", StringComparison.OrdinalIgnoreCase)
            If Not isText Then Return sb.Append($"(binary {mediaType}, {bytes.Length:N0} bytes; use get_file_contents or attachment_read for file content)").ToString()
            Dim content = Encoding.UTF8.GetString(bytes)
            If content.Length > _maxOutputChars Then
                content = content.Substring(0, _maxOutputChars) & vbLf & $"... (truncated at {_maxOutputChars \ 1024} KB of {content.Length \ 1024} KB; request fewer items with page/limit)"
            End If
            Return sb.Append(content).ToString()
        End Function

        ' ------------------------------------------------------------------ endpoint finder

        Friend Async Function FindEndpointAsync(<Description("What you want to do, e.g. 'create webhook', 'branch protection', 'add collaborator', 'transfer repository'")> query As String,
                                                <Description("Maximum endpoints to return (default 8)")> Optional limit As Integer = 8) As Task(Of String)
            If String.IsNullOrWhiteSpace(query) Then Throw New McpException("query is required.")
            Dim ops = Await LoadSpecAsync().ConfigureAwait(False)
            Return FormatMatches(Search(ops, query, Math.Clamp(limit, 1, 25)), query)
        End Function

        Private Async Function LoadSpecAsync() As Task(Of List(Of ApiOperation))
            If _spec IsNot Nothing Then Return _spec
            Await _specLock.WaitAsync().ConfigureAwait(False)
            Try
                If _spec Is Nothing Then
                    Dim json As String
                    Try
                        Using resp = Await _http.GetAsync(_host & "/swagger.v1.json").ConfigureAwait(False)
                            resp.EnsureSuccessStatusCode()
                            json = Await resp.Content.ReadAsStringAsync().ConfigureAwait(False)
                        End Using
                    Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is TaskCanceledException
                        Throw New McpException($"Could not load {_host}/swagger.v1.json ({ex.Message}). If the instance sets [api] ENABLE_SWAGGER=false, " &
                                               "use the endpoint list at https://docs.gitea.com/api/ and call api_get/api_write/api_delete directly.")
                    End Try
                    _spec = ParseSpec(json)
                End If
                Return _spec
            Finally
                _specLock.Release()
            End Try
        End Function

        Friend Shared Function ParseSpec(json As String) As List(Of ApiOperation)
            Dim result As New List(Of ApiOperation)
            Using doc = JsonDocument.Parse(json)
                Dim root = doc.RootElement
                Dim definitions As JsonElement = Nothing
                root.TryGetProperty("definitions", definitions)
                Dim paths As JsonElement = Nothing
                If Not root.TryGetProperty("paths", paths) Then Return result
                For Each pathProp In paths.EnumerateObject()
                    For Each opProp In pathProp.Value.EnumerateObject()
                        If opProp.Name = "parameters" OrElse opProp.Value.ValueKind <> JsonValueKind.Object Then Continue For
                        Dim op = opProp.Value
                        Dim o As New ApiOperation With {
                            .Method = opProp.Name.ToUpperInvariant(), .Path = pathProp.Name,
                            .Summary = Str(op, "summary"), .OperationId = Str(op, "operationId")}
                        Dim tags As JsonElement = Nothing
                        If op.TryGetProperty("tags", tags) AndAlso tags.ValueKind = JsonValueKind.Array AndAlso tags.GetArrayLength() > 0 Then o.Tag = tags(0).GetString()
                        Dim prms As JsonElement = Nothing
                        If op.TryGetProperty("parameters", prms) AndAlso prms.ValueKind = JsonValueKind.Array Then
                            For Each prm In prms.EnumerateArray()
                                Dim name = Str(prm, "name"), where = Str(prm, "in")
                                Dim required = prm.TryGetProperty("required", Nothing) AndAlso prm.GetProperty("required").ValueKind = JsonValueKind.True
                                If where = "body" Then
                                    Dim schema As JsonElement = Nothing
                                    If prm.TryGetProperty("schema", schema) Then o.BodyFields.AddRange(DescribeSchema(schema, definitions))
                                Else
                                    Dim type = Str(prm, "type")
                                    Dim desc = Str(prm, "description")
                                    o.Parameters.Add($"{name} ({where}{If(type.Length > 0, ", " & type, "")}{If(required, ", required", "")}){If(desc.Length > 0, ": " & Clip(desc, 90), "")}")
                                End If
                            Next
                        End If
                        o.Haystack = $"{o.Method} {o.Path} {o.Summary} {SplitCamel(o.OperationId)} {o.Tag}".ToLowerInvariant().Replace("_"c, " "c)
                        result.Add(o)
                    Next
                Next
            End Using
            Return result
        End Function

        Private Shared Function DescribeSchema(schema As JsonElement, definitions As JsonElement) As List(Of String)
            Dim fields As New List(Of String)
            Dim target = schema
            Dim refEl As JsonElement = Nothing
            If schema.TryGetProperty("$ref", refEl) Then
                Dim name = refEl.GetString().Split("/"c).Last()
                If definitions.ValueKind <> JsonValueKind.Object OrElse Not definitions.TryGetProperty(name, target) Then Return fields
                fields.Add("body: " & name)
            ElseIf Str(schema, "type") = "array" Then
                fields.Add("body: JSON array")
                Return fields
            End If
            Dim requiredNames As New HashSet(Of String)
            Dim req As JsonElement = Nothing
            If target.TryGetProperty("required", req) AndAlso req.ValueKind = JsonValueKind.Array Then
                For Each r In req.EnumerateArray()
                    requiredNames.Add(r.GetString())
                Next
            End If
            Dim props As JsonElement = Nothing
            If target.TryGetProperty("properties", props) AndAlso props.ValueKind = JsonValueKind.Object Then
                For Each p In props.EnumerateObject().Take(30)
                    Dim type = Str(p.Value, "type")
                    If type.Length = 0 AndAlso p.Value.TryGetProperty("$ref", Nothing) Then type = p.Value.GetProperty("$ref").GetString().Split("/"c).Last()
                    Dim enumEl As JsonElement = Nothing
                    Dim enumText = If(p.Value.TryGetProperty("enum", enumEl) AndAlso enumEl.ValueKind = JsonValueKind.Array,
                                      " [" & String.Join("|", enumEl.EnumerateArray().Select(Function(x) x.ToString())) & "]", "")
                    Dim desc = Str(p.Value, "description")
                    fields.Add($"  {p.Name} ({type}{If(requiredNames.Contains(p.Name), ", required", "")}){enumText}{If(desc.Length > 0, ": " & Clip(desc, 80), "")}")
                Next
            End If
            Return fields
        End Function

        Friend Shared Function Search(ops As List(Of ApiOperation), query As String, limit As Integer) As List(Of ApiOperation)
            Dim words = query.ToLowerInvariant().Split({" "c, ","c, "/"c, "_"c, "-"c, "."c}, StringSplitOptions.RemoveEmptyEntries).
                        Where(Function(w) w.Length > 1 AndAlso Not StopWords.Contains(w)).
                        Select(Function(w) If(Synonyms.ContainsKey(w), Synonyms(w), w)).Distinct().ToList()
            If words.Count = 0 Then Return New List(Of ApiOperation)
            Dim verbHint = VerbFor(words)
            Return ops.Select(Function(o) New With {.Op = o, .Score = Score(o, words, verbHint)}).
                       Where(Function(x) x.Score > 0).
                       OrderByDescending(Function(x) x.Score).ThenBy(Function(x) x.Op.Path.Length).
                       Take(limit).Select(Function(x) x.Op).ToList()
        End Function

        Private Shared ReadOnly StopWords As New HashSet(Of String) From {"a", "an", "the", "to", "of", "for", "in", "on", "my", "and", "or", "with", "by", "from", "how", "do", "i", "api", "gitea"}

        ''' <summary>Words people use for things Gitea's API names differently.</summary>
        Private Shared ReadOnly Synonyms As New Dictionary(Of String, String)(StringComparer.Ordinal) From {
            {"webhook", "hook"}, {"webhooks", "hook"}, {"pr", "pull"}, {"prs", "pull"}, {"repository", "repo"}, {"repositories", "repo"},
            {"organization", "org"}, {"organisation", "org"}, {"organizations", "org"}, {"protect", "protection"}, {"protected", "protection"},
            {"ssh", "key"}, {"gpg", "gpg"}, {"permissions", "permission"}, {"member", "member"}, {"secret", "secret"}, {"ci", "action"}, {"workflow", "action"}}

        Private Shared Function VerbFor(words As List(Of String)) As String
            For Each w In words
                Select Case w
                    Case "create", "add", "new", "post", "open", "fork", "transfer", "migrate", "mirror", "sync", "generate", "merge" : Return "POST"
                    Case "delete", "remove", "unprotect", "revoke" : Return "DELETE"
                    Case "edit", "update", "change", "rename", "modify", "set", "patch", "archive" : Return "PATCH"
                    Case "list", "get", "show", "read", "find", "search", "view", "check" : Return "GET"
                    Case "star", "watch", "follow", "subscribe", "lock", "pin", "block" : Return "PUT"
                End Select
            Next
            Return Nothing
        End Function

        Private Shared Function Score(o As ApiOperation, words As List(Of String), verbHint As String) As Integer
            Dim s = 0, matched = 0
            For Each w In words
                Dim stem = If(w.Length > 4 AndAlso w.EndsWith("s"c), w.Substring(0, w.Length - 1), w)
                If o.Haystack.Contains(stem) Then
                    matched += 1
                    s += If(o.Path.ToLowerInvariant().Contains(stem), 3, 2)
                    If If(o.Summary, "").ToLowerInvariant().Contains(stem) Then s += 1
                End If
            Next
            If matched = 0 Then Return 0
            If matched = words.Count Then s += 4
            ' Prefer the plain endpoint over deeply nested ones (DELETE /repos/{owner}/{repo} over .../stopwatch/delete).
            s -= Math.Max(0, o.Path.Count(Function(ch) ch = "/"c) - 4)
            ' Most requests are about a repository; break ties toward /repos/{owner}/{repo} unless the user/org/admin is named.
            If (o.Path = "/repos/{owner}/{repo}" OrElse o.Path.StartsWith("/repos/{owner}/{repo}/", StringComparison.Ordinal)) AndAlso
               Not words.Any(Function(w) w = "user" OrElse w = "org" OrElse w = "admin" OrElse w = "team") Then s += 1
            If verbHint IsNot Nothing Then
                If o.Method = verbHint OrElse (verbHint = "PATCH" AndAlso o.Method = "PUT") OrElse (verbHint = "POST" AndAlso o.Method = "PUT") Then s += 2
            End If
            Return s
        End Function

        Private Function FormatMatches(matches As List(Of ApiOperation), query As String) As String
            If matches.Count = 0 Then Return $"No Gitea API endpoint matches '{query}'. Try other words (e.g. 'hook', 'protection', 'collaborator', 'team', 'key')."
            Dim sb As New StringBuilder()
            sb.Append($"Gitea API on {_host} (paths are relative to /api/v1; fill in the {{placeholders}}):").Append(vbLf)
            For Each o In matches
                sb.Append(vbLf).Append($"{o.Method} {o.Path} — {o.Summary}").Append(If(String.IsNullOrEmpty(o.OperationId), "", $" [{o.OperationId}]")).Append(vbLf)
                For Each p In o.Parameters.Where(Function(x) Not x.Contains("(path"))
                    sb.Append("  param ").Append(p).Append(vbLf)
                Next
                For Each f In o.BodyFields
                    sb.Append("  ").Append(f).Append(vbLf)
                Next
            Next
            sb.Append(vbLf).Append("Call with api_get (GET), api_write (POST/PUT/PATCH) or api_delete (DELETE).")
            Return sb.ToString()
        End Function

        Private Shared Function Str(e As JsonElement, name As String) As String
            Dim v As JsonElement = Nothing
            If e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String Then Return v.GetString()
            Return ""
        End Function

        Private Shared Function Clip(s As String, n As Integer) As String
            Dim t = s.Replace(vbCr, " ").Replace(vbLf, " ").Trim()
            Return If(t.Length <= n, t, t.Substring(0, n - 1) & "…")
        End Function

        Private Shared Function SplitCamel(s As String) As String
            If String.IsNullOrEmpty(s) Then Return ""
            Dim sb As New StringBuilder()
            For Each ch In s
                If Char.IsUpper(ch) AndAlso sb.Length > 0 Then sb.Append(" "c)
                sb.Append(Char.ToLowerInvariant(ch))
            Next
            Return s.ToLowerInvariant() & " " & sb.ToString()
        End Function

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim c = Interlocked.Exchange(Of McpClient)(_upstream, Nothing)
            If c IsNot Nothing Then
                Try
                    c.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5))
                Catch
                End Try
            End If
            _http.Dispose()
        End Sub
    End Class

End Namespace
