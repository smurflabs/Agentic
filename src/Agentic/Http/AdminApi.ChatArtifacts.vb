Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' The Artifacts page: what the user made that runs - HTML and SVG from chat answers, and (in the desktop app, VS Code and
    ' the CLI) the projects on their computer - and public links that let other people open them. A link runs as an isolated
    ' origin (Content-Security-Policy sandbox): its scripts cannot use the viewer's Agentic session, cookies or API.
    '   GET    /api/chat/artifacts[?chat=]            the user's artifacts from chats and their shared links
    '   GET    /api/chat/artifacts/code?chat&message&block   one artifact's code
    '   POST   /api/chat/artifacts/share              {chatId, messageId, block, title?}: a link to a chat artifact
    '   POST   /api/chat/artifacts/share-site?title&source&key   body: a zip with index.html: a link to a website (again: same link)
    '   DELETE /api/chat/artifacts/shared/{token}     stops sharing
    '   GET    /api/public/artifacts/{token}          (no sign-in) what the link shows; /a/{token} is its page
    Partial Friend NotInheritable Class AdminApi

        Private _sharedArtifacts As SharedArtifactStore
        ''' <summary>Answers scanned for artifacts (newest first), and the most the page lists.</summary>
        Private Const ArtifactScanAnswers As Integer = 600
        Private Const ArtifactListMax As Integer = 300

        Friend ReadOnly Property SharedArtifacts As SharedArtifactStore
            Get
                If _sharedArtifacts Is Nothing Then _sharedArtifacts = New SharedArtifactStore(_h.Database, Path.Combine(_h.Options.Host.DataDirectory, "shared-artifacts"))
                Return _sharedArtifacts
            End Get
        End Property

        Friend Sub RegisterChatArtifacts(r As Router)
            r.Map("GET", "/api/chat/artifacts", AuthLevel.Read, AddressOf ArtifactsList)
            r.Map("GET", "/api/chat/artifacts/code", AuthLevel.Read, AddressOf ArtifactCode)
            r.Map("POST", "/api/chat/artifacts/share", AuthLevel.SubmitTask, AddressOf ArtifactShare)
            r.Map("POST", "/api/chat/artifacts/share-site", AuthLevel.SubmitTask, AddressOf ArtifactShareSite)
            r.Map("DELETE", "/api/chat/artifacts/shared/{token}", AuthLevel.SubmitTask, AddressOf ArtifactUnshare)
            r.Map("GET", "/api/public/artifacts/{token}", AuthLevel.Anonymous, AddressOf PublicArtifact)
        End Sub

        Private Function SharedDto(rc As RequestContext, a As SharedArtifact) As Object
            Return New With {.token = a.Token, .kind = a.Kind, .title = a.Title, .lang = a.Lang, .source = a.Source, .key = a.SourceKey, .chatId = a.ChatId, .messageId = a.MessageId,
                             .block = a.BlockIndex, .files = a.Files, .bytes = a.Bytes, .views = a.Views, .createdUtc = a.CreatedUtc, .updatedUtc = a.UpdatedUtc,
                             .path = "/a/" & a.Token, .url = PublicOrigin(rc) & "/a/" & a.Token}
        End Function

        Private Function MaySharePublicly(rc As RequestContext) As Boolean
            Return _h.Options.Chat.ArtifactSharing AndAlso rc.User.HasPermission("chat.share")
        End Function

        Private Async Function ArtifactsList(rc As RequestContext) As Task
            RequireChat()
            Dim chatId = rc.QueryString("chat")
            Dim items = Await ChatArtifacts.ScanAsync(_h.Database, rc.User.Name, If(String.IsNullOrWhiteSpace(chatId), Nothing, chatId), ArtifactScanAnswers, ArtifactListMax).ConfigureAwait(False)
            Dim links = Await SharedArtifacts.ListAsync(rc.User.Name).ConfigureAwait(False)
            Dim byBlock = links.Where(Function(l) l.Kind = "code").GroupBy(Function(l) $"{l.ChatId}:{l.MessageId}:{l.BlockIndex}").ToDictionary(Function(g) g.Key, Function(g) g.First().Token)
            Await rc.OkAsync(New With {
                .items = items.Select(Function(a)
                                          Dim token As String = Nothing
                                          byBlock.TryGetValue($"{a.ChatId}:{a.MessageId}:{a.Block}", token)
                                          Return New With {.id = $"{a.ChatId}:{a.MessageId}:{a.Block}", .chatId = a.ChatId, .chatTitle = a.ChatTitle, .messageId = a.MessageId,
                                                           .block = a.Block, .lang = a.Lang, .title = a.Title, .lines = a.Lines, .bytes = a.Bytes, .createdUtc = a.CreatedUtc,
                                                           .sharedToken = token}
                                      End Function).ToList(),
                .[shared] = links.Select(Function(l) SharedDto(rc, l)).ToList(),
                .sharing = _h.Options.Chat.ArtifactSharing, .canShare = MaySharePublicly(rc),
                .maxShared = _h.Options.Chat.MaxSharedArtifacts, .maxSiteMb = _h.Options.Chat.MaxSharedSiteMb}).ConfigureAwait(False)
        End Function

        ''' <summary>The code block an artifact is (the user's own chats only).</summary>
        Private Async Function ArtifactBlockAsync(rc As RequestContext, chatId As String, messageId As String, block As Integer) As Task(Of (Lang As String, Code As String, Title As String))
            If String.IsNullOrWhiteSpace(chatId) OrElse String.IsNullOrWhiteSpace(messageId) Then Throw ApiException.BadRequest("chat and message are required.")
            Dim c = Await _h.Chat.GetAsync(rc.User.Name, chatId).ConfigureAwait(False)
            If c Is Nothing Then Throw ApiException.NotFound("CHAT_NOT_FOUND", "Conversation does not exist.")
            Dim m = c.Messages.FirstOrDefault(Function(x) x.Id = messageId AndAlso x.Role = "assistant")
            If m Is Nothing Then Throw ApiException.NotFound("MESSAGE_NOT_FOUND", "That answer is not in this conversation.")
            Dim b = ChatArtifacts.Blocks(m.Content).FirstOrDefault(Function(x) x.Index = block)
            If b.Code Is Nothing OrElse Not ChatArtifacts.IsArtifact(b.Lang, b.Code) Then Throw ApiException.NotFound("ARTIFACT_NOT_FOUND", "That answer has no page or drawing there.")
            Return (b.Lang, b.Code, ChatArtifacts.TitleOf(b.Lang, b.Code, c.Title))
        End Function

        Private Async Function ArtifactCode(rc As RequestContext) As Task
            RequireChat()
            Dim block As Integer
            If Not Integer.TryParse(rc.QueryString("block"), block) Then Throw ApiException.BadRequest("block is required.")
            Dim a = Await ArtifactBlockAsync(rc, rc.QueryString("chat"), rc.QueryString("message"), block).ConfigureAwait(False)
            rc.Http.Response.Headers("Cache-Control") = "no-store"
            Await rc.OkAsync(New With {.lang = a.Lang, .title = a.Title, .code = a.Code}).ConfigureAwait(False)
        End Function

        Private Sub RequireArtifactSharing()
            If Not _h.Options.Chat.ArtifactSharing Then Throw New ApiException(409, "SHARING_OFF", "Sharing artifacts is turned off on this server (Chat.ArtifactSharing).")
        End Sub

        Private Async Function RequireRoomToShareAsync(rc As RequestContext) As Task
            Dim limit = Math.Max(1, _h.Options.Chat.MaxSharedArtifacts)
            If Await SharedArtifacts.CountAsync(rc.User.Name).ConfigureAwait(False) >= limit Then
                Throw New ApiException(409, "TOO_MANY_LINKS", $"You share {limit} artifacts already: stop sharing one on the Artifacts page first.")
            End If
        End Function

        ''' <summary>Body {chatId, messageId, block, title?}: a public link to that artifact (the same link when it is shared already).</summary>
        Private Async Function ArtifactShare(rc As RequestContext) As Task
            RequireChat()
            RequireArtifactSharing()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim chatId = JsonUtil.GetString(b, "chatId"), messageId = JsonUtil.GetString(b, "messageId")
            Dim block = JsonUtil.GetInt(b, "block", -1)
            Dim art = Await ArtifactBlockAsync(rc, chatId, messageId, block).ConfigureAwait(False)
            Dim existing = Await SharedArtifacts.FindCodeAsync(rc.User.Name, chatId, messageId, block).ConfigureAwait(False)
            If existing IsNot Nothing Then
                Await rc.OkAsync(SharedDto(rc, existing)).ConfigureAwait(False)
                Return
            End If
            Await RequireRoomToShareAsync(rc).ConfigureAwait(False)
            Dim title = CleanTitle(JsonUtil.GetString(b, "title"), art.Title)
            Dim a = Await SharedArtifacts.AddCodeAsync(rc.User.Name, title, art.Lang, art.Code, chatId, messageId, block).ConfigureAwait(False)
            Await AuditAsync(rc, "artifact.share", "artifact", a.Token, New With {a.Title, .chatId = chatId}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, SharedDto(rc, a)).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' A website from the user's computer (the desktop app, VS Code and the CLI zip a project's static files): body = the zip;
        ''' ?title, ?source (the project's name) and ?key (its id there: sharing it again replaces the files under the same link).
        ''' </summary>
        Private Async Function ArtifactShareSite(rc As RequestContext) As Task
            RequireChat()
            RequireArtifactSharing()
            Dim key = Trimmed(rc.QueryString("key"), 120)
            Dim source = Trimmed(rc.QueryString("source"), 120)
            Dim known = If(key Is Nothing, Nothing, Await SharedArtifacts.FindByKeyAsync(rc.User.Name, key).ConfigureAwait(False))
            If known Is Nothing Then Await RequireRoomToShareAsync(rc).ConfigureAwait(False)
            Dim maxBytes = CLng(Math.Clamp(_h.Options.Chat.MaxSharedSiteMb, 1, 1024)) * 1024L * 1024L
            Dim zip = Await rc.ReadBytesAsync(maxBytes).ConfigureAwait(False)
            If zip Is Nothing OrElse zip.Length = 0 Then Throw ApiException.BadRequest("Send the website as a zip.")
            Dim title = CleanTitle(rc.QueryString("title"), If(source, "Website"))
            Dim saved As (Artifact As SharedArtifact, Updated As Boolean)
            Try
                saved = Await SharedArtifacts.SaveSiteAsync(rc.User.Name, title, source, key, zip, maxBytes).ConfigureAwait(False)
            Catch ex As InvalidDataException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, If(saved.Updated, "artifact.update", "artifact.share"), "artifact", saved.Artifact.Token,
                             New With {saved.Artifact.Title, saved.Artifact.Files, saved.Artifact.Bytes}).ConfigureAwait(False)
            Dim dto = SharedDto(rc, saved.Artifact)
            Await rc.WriteJsonAsync(If(saved.Updated, 200, 201), New With {.link = dto, .updated = saved.Updated}).ConfigureAwait(False)
        End Function

        Private Async Function ArtifactUnshare(rc As RequestContext) As Task
            Dim token = rc.Route("token")
            If Not Await SharedArtifacts.DeleteAsync(token, If(rc.User.IsAdmin, Nothing, rc.User.Name)).ConfigureAwait(False) Then
                Throw ApiException.NotFound("LINK_NOT_FOUND", "That link does not exist.")
            End If
            Await AuditAsync(rc, "artifact.unshare", "artifact", token).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>(No sign-in) what a link shows, for its page /a/{token}; each call counts as a view.</summary>
        Private Async Function PublicArtifact(rc As RequestContext) As Task
            Dim a = If(_h.Options.Chat.ArtifactSharing, Await SharedArtifacts.GetAsync(rc.Route("token")).ConfigureAwait(False), Nothing)
            If a Is Nothing Then Throw ApiException.NotFound("LINK_NOT_FOUND", "This link does not exist or is no longer shared.")
            Await SharedArtifacts.AddViewAsync(a.Token).ConfigureAwait(False)
            rc.Http.Response.Headers("Cache-Control") = "no-store"
            Await rc.OkAsync(New With {.title = a.Title, .kind = a.Kind, .lang = a.Lang, .files = a.Files, .bytes = a.Bytes, .createdUtc = a.CreatedUtc,
                                       .updatedUtc = a.UpdatedUtc, .views = a.Views + 1,
                                       .run = If(a.Kind = "site", $"/a/{a.Token}/site/", $"/a/{a.Token}/run")}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' /a/{token}/run (a chat artifact as a document) and /a/{token}/site/… (a website's files), for anyone with the link. Served
        ''' with a CSP sandbox: the page runs as an opaque origin, so it cannot use the viewer's session; its own requests may read
        ''' the site's files (CORS *, as the files are public anyway).
        ''' </summary>
        Friend Async Function ServeSharedArtifactAsync(rc As RequestContext) As Task
            If rc.Method <> "GET" AndAlso rc.Method <> "HEAD" Then Throw New ApiException(405, "METHOD_NOT_ALLOWED", "Method not allowed.")
            Dim parts = rc.Http.Request.Url.AbsolutePath.Split("/"c, 4)
            Dim token = If(parts.Length > 2, Uri.UnescapeDataString(parts(2)), "")
            Dim rest = If(parts.Length > 3, parts(3), "")
            Dim a = If(_h.Options.Chat.ArtifactSharing, Await SharedArtifacts.GetAsync(token).ConfigureAwait(False), Nothing)
            Dim resp = rc.Http.Response
            resp.Headers("Content-Security-Policy") = ArtifactPolicy
            resp.Headers("Cache-Control") = "no-store"
            If a IsNot Nothing AndAlso a.Kind = "code" AndAlso rest = "run" Then
                Await rc.WriteBytesAsync(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(ChatArtifacts.DocumentOf(a.Lang, a.Content))).ConfigureAwait(False)
                Return
            End If
            If a IsNot Nothing AndAlso a.Kind = "site" AndAlso (rest = "site" OrElse rest.StartsWith("site/", StringComparison.Ordinal)) Then
                Dim rel = If(rest.Length > 5, Uri.UnescapeDataString(rest.Substring(5)), "")
                Dim file = SharedArtifacts.SiteFile(a.Token, rel)
                If file IsNot Nothing Then
                    resp.Headers("Access-Control-Allow-Origin") = "*"
                    Await rc.WriteBytesAsync(200, SiteContentType(file), Await IO.File.ReadAllBytesAsync(file).ConfigureAwait(False)).ConfigureAwait(False)
                    Return
                End If
            End If
            Await rc.WriteBytesAsync(404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("This link does not exist or is no longer shared.")).ConfigureAwait(False)
        End Function

        ''' <summary>Shared pages may load anything a website loads, but only as an isolated origin, and only framed by this server.</summary>
        Private Const ArtifactPolicy As String = "default-src * data: blob: 'unsafe-inline' 'unsafe-eval'; frame-ancestors 'self'; " &
                                                 "sandbox allow-scripts allow-forms allow-modals allow-popups allow-downloads allow-pointer-lock allow-presentation"

        Private Shared ReadOnly SiteTypes As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {".html", "text/html; charset=utf-8"}, {".htm", "text/html; charset=utf-8"}, {".js", "text/javascript; charset=utf-8"}, {".mjs", "text/javascript; charset=utf-8"},
            {".css", "text/css; charset=utf-8"}, {".json", "application/json; charset=utf-8"}, {".map", "application/json"}, {".txt", "text/plain; charset=utf-8"},
            {".md", "text/plain; charset=utf-8"}, {".csv", "text/csv; charset=utf-8"}, {".xml", "application/xml"}, {".svg", "image/svg+xml"}, {".png", "image/png"},
            {".jpg", "image/jpeg"}, {".jpeg", "image/jpeg"}, {".gif", "image/gif"}, {".webp", "image/webp"}, {".avif", "image/avif"}, {".ico", "image/x-icon"},
            {".bmp", "image/bmp"}, {".wav", "audio/wav"}, {".mp3", "audio/mpeg"}, {".ogg", "audio/ogg"}, {".m4a", "audio/mp4"}, {".mp4", "video/mp4"},
            {".webm", "video/webm"}, {".woff", "font/woff"}, {".woff2", "font/woff2"}, {".ttf", "font/ttf"}, {".otf", "font/otf"}, {".wasm", "application/wasm"},
            {".pdf", "application/pdf"}, {".glb", "model/gltf-binary"}, {".gltf", "model/gltf+json"}}

        Private Shared Function SiteContentType(file As String) As String
            Dim t As String = Nothing
            Return If(SiteTypes.TryGetValue(Path.GetExtension(file), t), t, "application/octet-stream")
        End Function

        Private Shared Function Trimmed(value As String, max As Integer) As String
            If String.IsNullOrWhiteSpace(value) Then Return Nothing
            Dim v = value.Trim()
            Return If(v.Length > max, v.Substring(0, max), v)
        End Function

        Private Shared Function CleanTitle(value As String, fallback As String) As String
            Dim t = If(Trimmed(value, 100), Trimmed(fallback, 100))
            Return If(t, "Artifact")
        End Function
    End Class

End Namespace
