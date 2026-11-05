Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports Agentic.Mcp
Imports Agentic.Utilities

Namespace Http

    ''' <summary>
    ''' MCP servers added from the web app (no appsettings.json edit, no restart): everyone with chat tools adds HTTP servers for
    ''' their own chats; administrators also add servers for everyone and programs that run on this host. Hosted servers that
    ''' need a sign-in sign in through the browser (OAuth), coming back to /api/chat/mcp-servers/oauth/callback.
    ''' </summary>
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterMcpServers(r As Router)
            r.Map("GET", "/api/chat/mcp-servers", AuthLevel.Read, AddressOf McpServersList)
            r.Map("POST", "/api/chat/mcp-servers", AuthLevel.SubmitTask, AddressOf McpServersSave)
            r.Map("GET", "/api/chat/mcp-servers/oauth/callback", AuthLevel.Read, AddressOf McpServersCallback)
            r.Map("DELETE", "/api/chat/mcp-servers/{serverId}", AuthLevel.SubmitTask, AddressOf McpServersDelete)
            r.Map("POST", "/api/chat/mcp-servers/{serverId}/enabled", AuthLevel.SubmitTask, AddressOf McpServersEnable)
            r.Map("POST", "/api/chat/mcp-servers/{serverId}/signin", AuthLevel.SubmitTask, AddressOf McpServersSignIn)
            r.Map("POST", "/api/chat/mcp-servers/{serverId}/signout", AuthLevel.SubmitTask, AddressOf McpServersSignOut)
            r.Map("POST", "/api/chat/mcp-servers/install", AuthLevel.SubmitTask, AddressOf McpServersInstall)
        End Sub

        Private Sub RequireMcpServers(rc As RequestContext)
            RequireChat()
            If _h.McpUser Is Nothing Then Throw New ApiException(503, "MCP_UNAVAILABLE", "Adding MCP servers is not available on this server.")
            If Not rc.User.HasPermission("chat.tools") Then Throw New ApiException(403, "FORBIDDEN", "Your account may not use MCP tools in chat.")
        End Sub

        Private Function McpServerOrThrow(rc As RequestContext) As String
            Dim id = rc.Route("serverId")
            If Not _h.McpUser.MayManage(id, rc.User.Name, rc.User.IsAdmin) Then Throw ApiException.NotFound("MCP_NOT_FOUND", "No such MCP server of yours.")
            Return id
        End Function

        ''' <summary>The servers this account added (administrators: every runtime server), with their state.</summary>
        Private Async Function McpServersList(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim status = _h.Mcp.GetStatus().ToDictionary(Function(s) s.Id, StringComparer.OrdinalIgnoreCase)
            Dim list = _h.McpUser.All().Where(Function(e) rc.User.IsAdmin OrElse e.Owner = rc.User.Name OrElse e.Owner = "*").Select(Function(e)
                Dim s As Global.Agentic.Mcp.McpServerStatus = Nothing
                status.TryGetValue(e.Id, s)
                Dim err = s?.LastError
                Return CObj(New With {
                    .id = e.Id, .name = e.Name, .description = e.Description, .owner = e.Owner, .shared = e.Owner = "*", .transport = e.Transport,
                    .url = e.Url, .command = e.Command, .args = e.Args, .env = If(e.Env, New Dictionary(Of String, String)).Keys.ToList(),
                    .headers = If(e.Headers, New Dictionary(Of String, String)).Keys.ToList(), .enabled = e.Enabled, .askFirst = e.AskFirst,
                    .mine = e.Owner = rc.User.Name OrElse rc.User.IsAdmin, .signedIn = Not String.IsNullOrEmpty(e.OAuth),
                    .state = If(s Is Nothing, "Disabled", s.State.ToString()), .error = err, .tools = If(s?.Tools, New List(Of String)),
                    .needsSignIn = err IsNot Nothing AndAlso (err.Contains("401") OrElse err.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0)})
            End Function).ToList()
            Await rc.OkAsync(New With {.servers = list, .isAdmin = rc.User.IsAdmin, .callback = PublicOrigin(rc) & "/api/chat/mcp-servers/oauth/callback"}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {id?, name, transport (http | sse | stdio), url, command, args[], env{}, headers{}, askFirst, shared, description}.</summary>
        Private Async Function McpServersSave(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim strMap = Function(name As String) As Dictionary(Of String, String)
                             Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                             Dim el As JsonElement
                             If b.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.Object Then
                                 For Each p In el.EnumerateObject().Take(40)
                                     If p.Value.ValueKind = JsonValueKind.String Then d(TextUtil.Truncate(p.Name, 100)) = TextUtil.Truncate(p.Value.GetString(), 8000)
                                 Next
                             End If
                             Return d
                         End Function
            Dim args As New List(Of String)
            Dim argsEl As JsonElement
            If b.TryGetProperty("args", argsEl) AndAlso argsEl.ValueKind = JsonValueKind.Array Then args = argsEl.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString()).Take(60).ToList()
            Dim flag = Function(name As String) As Boolean
                           Dim el As JsonElement
                           Return b.TryGetProperty(name, el) AndAlso el.ValueKind = JsonValueKind.True
                       End Function
            Dim existing = JsonUtil.GetString(b, "id")
            If Not String.IsNullOrEmpty(existing) AndAlso Not _h.McpUser.MayManage(existing, rc.User.Name, rc.User.IsAdmin) Then Throw ApiException.NotFound("MCP_NOT_FOUND", "No such MCP server of yours.")
            Dim e As McpUserServers.Entry
            Try
                e = _h.McpUser.Save(rc.User.Name, rc.User.IsAdmin, flag("shared"), JsonUtil.GetString(b, "name"), JsonUtil.GetString(b, "transport"), JsonUtil.GetString(b, "url"),
                                    JsonUtil.GetString(b, "command"), args, strMap("env"), strMap("headers"), flag("askFirst"), JsonUtil.GetString(b, "description"),
                                    If(String.IsNullOrEmpty(existing), Nothing, existing))
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As UnauthorizedAccessException
                Throw New ApiException(403, "FORBIDDEN", ex.Message)
            End Try
            Await AuditAsync(rc, "mcp-server-save", "mcp", e.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.id = e.Id}).ConfigureAwait(False)
        End Function

        Private Async Function McpServersDelete(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim id = McpServerOrThrow(rc)
            _h.McpUser.Remove(id)
            Await AuditAsync(rc, "mcp-server-remove", "mcp", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.removed = True}).ConfigureAwait(False)
        End Function

        Private Async Function McpServersEnable(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim id = McpServerOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim onEl As JsonElement
            _h.McpUser.SetEnabled(id, Not (b.TryGetProperty("on", onEl) AndAlso onEl.ValueKind = JsonValueKind.False))
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Starts the browser sign-in: {url} to open (the browser comes back to the callback).</summary>
        Private Async Function McpServersSignIn(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim id = McpServerOrThrow(rc)
            Dim url As String
            Try
                url = Await _h.McpUser.StartSignInAsync(id, rc.User.Name, PublicOrigin(rc) & "/api/chat/mcp-servers/oauth/callback").ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is UriFormatException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await rc.OkAsync(New With {.url = url}).ConfigureAwait(False)
        End Function

        Private Async Function McpServersSignOut(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim id = McpServerOrThrow(rc)
            _h.McpUser.SignOut(id)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {device, name, entry}: asks a connected desktop app or VS Code window to add an MCP server that runs on that
        ''' computer (entry in Claude Code's format). The app shows its add form filled in; the user confirms there.
        ''' </summary>
        Private Async Function McpServersInstall(rc As RequestContext) As Task
            RequireMcpServers(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim device = JsonUtil.GetString(b, "device")
            Dim name = TextUtil.Truncate(If(JsonUtil.GetString(b, "name"), "").Trim(), 60)
            Dim entryEl As JsonElement
            If String.IsNullOrEmpty(device) OrElse name.Length = 0 OrElse Not b.TryGetProperty("entry", entryEl) OrElse entryEl.ValueKind <> JsonValueKind.Object Then
                Throw ApiException.BadRequest("Give device, name and entry.")
            End If
            If entryEl.GetRawText().Length > 20000 Then Throw ApiException.BadRequest("The entry is too large.")
            Dim target = _h.Chat.Live.PickSubscriber(rc.User.Name, device)
            If target Is Nothing Then Throw New ApiException(409, "DEVICE_OFFLINE", "That computer is not connected now: open the desktop app or VS Code there.")
            _h.Chat.Live.PublishTo(rc.User.Name, Nothing, "mcp_install", New With {.id = Global.Agentic.Utilities.Ids.NewToken(8), .device = device, .name = name,
                                                                                    .entry = Text.Json.Nodes.JsonNode.Parse(entryEl.GetRawText())}, target)
            Await rc.OkAsync(New With {.sent = True}).ConfigureAwait(False)
        End Function

        ''' <summary>The sign-in server sends the browser back here: a small page that says how it went and closes itself.</summary>
        Private Async Function McpServersCallback(rc As RequestContext) As Task
            Dim q = rc.Http.Request.QueryString
            Dim message As String
            Dim ok = False
            If Not String.IsNullOrEmpty(q("error")) Then
                message = "Sign-in refused: " & If(q("error_description"), q("error"))
            ElseIf _h.McpUser Is Nothing Then
                message = "Adding MCP servers is not available on this server."
            Else
                Try
                    Dim id = Await _h.McpUser.FinishSignInAsync(q("state"), q("code")).ConfigureAwait(False)
                    message = "Signed in to " & id & ". It connects now; you can close this tab."
                    ok = True
                Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is TaskCanceledException
                    message = ex.Message
                End Try
            End If
            Dim html = "<!doctype html><meta charset=""utf-8""><title>Agentic</title><body style=""font:16px system-ui;padding:40px""><p><b>" &
                       If(ok, "Signed in.", "Sign-in failed.") & "</b> " & WebUtility.HtmlEncode(message) & "</p>" &
                       If(ok, "<script>setTimeout(function(){window.close()},1500)</script>", "") & "</body>"
            Await rc.WriteBytesAsync(If(ok, 200, 400), "text/html; charset=utf-8", Text.Encoding.UTF8.GetBytes(html)).ConfigureAwait(False)
        End Function

    End Class

End Namespace
