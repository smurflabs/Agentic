Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: Chat features people know from ChatGPT and Claude - earlier versions of answers and edited questions,
    ' projects, memory and personal instructions, read-only share links, export, feedback on answers and saved prompts.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterChatFeatures(r As Router)
            r.Map("POST", "/api/chat/conversations/{chatId}/versions", AuthLevel.SubmitTask, AddressOf ChatSwitchVersion)
            r.Map("POST", "/api/chat/conversations/{chatId}/share", AuthLevel.SubmitTask, AddressOf ChatShare)
            r.Map("DELETE", "/api/chat/conversations/{chatId}/share", AuthLevel.SubmitTask, AddressOf ChatUnshare)
            r.Map("GET", "/api/chat/conversations/{chatId}/export", AuthLevel.Read, AddressOf ChatExport)
            r.Map("POST", "/api/chat/conversations/{chatId}/messages/{messageId}/feedback", AuthLevel.SubmitTask, AddressOf ChatFeedback)
            r.Map("GET", "/api/chat/projects", AuthLevel.Read, AddressOf ChatProjects)
            r.Map("POST", "/api/chat/projects", AuthLevel.SubmitTask, AddressOf ChatProjectSave)
            r.Map("PUT", "/api/chat/projects/{projectId}", AuthLevel.SubmitTask, AddressOf ChatProjectSave)
            r.Map("DELETE", "/api/chat/projects/{projectId}", AuthLevel.SubmitTask, AddressOf ChatProjectDelete)
            r.Map("GET", "/api/chat/personalization", AuthLevel.Read, AddressOf ChatPersonalization)
            r.Map("PUT", "/api/chat/personalization", AuthLevel.SubmitTask, AddressOf ChatPersonalizationPut)
            r.Map("POST", "/api/chat/memories", AuthLevel.SubmitTask, AddressOf ChatMemoryAdd)
            r.Map("POST", "/api/chat/memories/clear", AuthLevel.SubmitTask, AddressOf ChatMemoryClear)
            r.Map("DELETE", "/api/chat/memories/{memoryId}", AuthLevel.SubmitTask, AddressOf ChatMemoryDelete)
            r.Map("GET", "/api/chat/prompts", AuthLevel.Read, AddressOf ChatPrompts)
            r.Map("POST", "/api/chat/prompts", AuthLevel.SubmitTask, AddressOf ChatPromptSave)
            r.Map("PUT", "/api/chat/prompts/{promptId}", AuthLevel.SubmitTask, AddressOf ChatPromptSave)
            r.Map("DELETE", "/api/chat/prompts/{promptId}", AuthLevel.SubmitTask, AddressOf ChatPromptDelete)
            r.Map("GET", "/api/chat/files/{uploadId}", AuthLevel.Read, AddressOf ChatFile)
            r.Map("POST", "/api/chat/transcribe", AuthLevel.SubmitTask, AddressOf ChatTranscribe)
            ' Attach sources to a message: a webpage's text, or other conversations as context (saved as uploads).
            r.Map("POST", "/api/chat/fetch-page", AuthLevel.SubmitTask, AddressOf ChatFetchPage)
            r.Map("POST", "/api/chat/reference-chats", AuthLevel.SubmitTask, AddressOf ChatReferenceChats)
            ' RAG knowledge bases (Retrieval-Augmented Generation): build from uploaded files, attach to a chat.
            r.Map("GET", "/api/chat/knowledge", AuthLevel.Read, AddressOf KnowledgeList)
            r.Map("POST", "/api/chat/knowledge", AuthLevel.SubmitTask, AddressOf KnowledgeCreate)
            r.Map("GET", "/api/chat/knowledge/{baseId}", AuthLevel.Read, AddressOf KnowledgeGet)
            r.Map("PATCH", "/api/chat/knowledge/{baseId}", AuthLevel.SubmitTask, AddressOf KnowledgeUpdate)
            r.Map("DELETE", "/api/chat/knowledge/{baseId}", AuthLevel.SubmitTask, AddressOf KnowledgeDelete)
            r.Map("POST", "/api/chat/knowledge/{baseId}/docs", AuthLevel.SubmitTask, AddressOf KnowledgeAddDocs)
            r.Map("DELETE", "/api/chat/knowledge/{baseId}/docs/{docId}", AuthLevel.SubmitTask, AddressOf KnowledgeRemoveDoc)
            ' The public page of a shared chat (/share/{token}) reads these without signing in.
            r.Map("GET", "/api/share/{token}", AuthLevel.Anonymous, AddressOf SharedChat)
            r.Map("GET", "/api/share/{token}/files/{uploadId}", AuthLevel.Anonymous, AddressOf SharedChatFile)
        End Sub

        ''' <summary>The chat page's conversation: its messages and the points that have earlier versions.</summary>
        Private Async Function ConversationWithVersionsAsync(c As ChatConversation) As Task(Of ChatConversation)
            c.Versions = Await _h.Chat.VersionPointsAsync(c).ConfigureAwait(False)
            Return c
        End Function

        ''' <summary>Body {branchId}: shows that earlier version (the one shown now is kept as a version).</summary>
        Private Async Function ChatSwitchVersion(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim branchId = JsonUtil.GetString(b, "branchId")
            If String.IsNullOrWhiteSpace(branchId) Then Throw ApiException.BadRequest("branchId is required.")
            Dim updated As ChatConversation
            Try
                updated = Await _h.Chat.SwitchVersionAsync(rc.User.Name, c.Id, branchId).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw New ApiException(409, "CHAT_BUSY", ex.Message)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("VERSION_NOT_FOUND", ex.Message)
            End Try
            _h.Chat.Live.Publish(rc.User.Name, c.Id, "changed", New With {.chatId = c.Id})
            Await rc.OkAsync(Await ConversationWithVersionsAsync(updated).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>Makes (or returns) the chat's read-only public link.</summary>
        Private Async Function ChatShare(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim token As String
            Try
                token = Await _h.Chat.ShareAsync(rc.User.Name, c.Id).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "chat.share", "chat", c.Id, New With {c.Title}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.token = token, .path = "/share/" & token, .url = PublicOrigin(rc) & "/share/" & token}).ConfigureAwait(False)
        End Function

        Private Async Function ChatUnshare(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Await _h.Chat.UnshareAsync(rc.User.Name, c.Id).ConfigureAwait(False)
            Await AuditAsync(rc, "chat.unshare", "chat", c.Id, Nothing).ConfigureAwait(False)
            Await rc.OkAsync(New With {.isShared = False}).ConfigureAwait(False)
        End Function

        ''' <summary>?format=md (default) | json: the conversation as a file.</summary>
        Private Async Function ChatExport(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim json = String.Equals(rc.QueryString("format"), "json", StringComparison.OrdinalIgnoreCase)
            Dim baseName = Text.RegularExpressions.Regex.Replace(If(c.Title, "chat"), "[^\w\- ]+", "").Trim()
            If baseName.Length = 0 Then baseName = "chat"
            If baseName.Length > 60 Then baseName = baseName.Substring(0, 60).Trim()
            Dim data = Encoding.UTF8.GetBytes(If(json, JsonUtil.ToJson(c, indented:=True), ChatService.ToMarkdown(c)))
            Await rc.WriteBytesAsync(200, If(json, "application/json; charset=utf-8", "text/markdown; charset=utf-8"), data, New Dictionary(Of String, String) From {
                {"Content-Disposition", $"attachment; filename=""{baseName}{If(json, ".json", ".md")}"""}, {"Cache-Control", "no-store"}}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {value: 1 | -1 | 0, note}: the user's rating of an answer.</summary>
        Private Async Function ChatFeedback(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim value = JsonUtil.GetInt(b, "value", 0)
            If Not Await _h.Chat.SetFeedbackAsync(rc.User.Name, c.Id, rc.Route("messageId"), value, JsonUtil.GetString(b, "note")).ConfigureAwait(False) Then
                Throw ApiException.NotFound("MESSAGE_NOT_FOUND", "That answer is not in this conversation.")
            End If
            Await rc.OkAsync(New With {.feedback = Math.Sign(value)}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ projects

        Private Async Function ChatProjects(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(Await _h.Chat.ListProjectsAsync(rc.User.Name).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>Body {name, description, instructions, files: [uploadId]} (files left out = unchanged).</summary>
        Private Async Function ChatProjectSave(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim filesEl As JsonElement
            Dim files = If(b.TryGetProperty("files", filesEl) AndAlso filesEl.ValueKind = JsonValueKind.Array, JsonUtil.GetStringList(b, "files"), Nothing)
            Dim p As ChatProject
            Try
                p = Await _h.Chat.SaveProjectAsync(rc.User.Name, rc.Route("projectId"), JsonUtil.GetString(b, "name"), JsonUtil.GetString(b, "description"),
                                                   JsonUtil.GetString(b, "instructions"), files).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("PROJECT_NOT_FOUND", ex.Message)
            End Try
            Await rc.WriteJsonAsync(If(rc.Route("projectId") Is Nothing, 201, 200), p).ConfigureAwait(False)
        End Function

        Private Async Function ChatProjectDelete(rc As RequestContext) As Task
            RequireChat()
            If Not Await _h.Chat.DeleteProjectAsync(rc.User.Name, rc.Route("projectId")).ConfigureAwait(False) Then Throw ApiException.NotFound("PROJECT_NOT_FOUND", "Project does not exist.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ personalization and memory

        Private Async Function PersonalizationJsonAsync(user As String) As Task(Of Object)
            Return New With {
                .aboutMe = Await _h.Chat.GetPreferenceAsync(user, ChatService.AboutMeKey).ConfigureAwait(False),
                .howToRespond = Await _h.Chat.GetPreferenceAsync(user, ChatService.HowToRespondKey).ConfigureAwait(False),
                .memory = Await _h.Chat.MemoryEnabledAsync(user).ConfigureAwait(False),
                .memories = Await _h.Chat.ListMemoriesAsync(user).ConfigureAwait(False)}
        End Function

        Private Async Function ChatPersonalization(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(Await PersonalizationJsonAsync(rc.User.Name).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>Body {aboutMe, howToRespond, memory: bool}; properties left out stay as they are.</summary>
        Private Async Function ChatPersonalizationPut(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            If b.TryGetProperty("aboutMe", el) Then Await _h.Chat.SetPreferenceAsync(rc.User.Name, ChatService.AboutMeKey, If(JsonUtil.GetString(b, "aboutMe"), "").Trim()).ConfigureAwait(False)
            If b.TryGetProperty("howToRespond", el) Then Await _h.Chat.SetPreferenceAsync(rc.User.Name, ChatService.HowToRespondKey, If(JsonUtil.GetString(b, "howToRespond"), "").Trim()).ConfigureAwait(False)
            If b.TryGetProperty("memory", el) AndAlso (el.ValueKind = JsonValueKind.True OrElse el.ValueKind = JsonValueKind.False) Then
                Await _h.Chat.SetPreferenceAsync(rc.User.Name, ChatService.MemoryOnKey, If(el.GetBoolean(), "on", "off")).ConfigureAwait(False)
            End If
            Await rc.OkAsync(Await PersonalizationJsonAsync(rc.User.Name).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>Body {content}: a memory the user typed.</summary>
        Private Async Function ChatMemoryAdd(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim content = JsonUtil.GetString(b, "content")
            If Global.Agentic.Security.SecretRedactor.LooksLikeSecret(content) Then Throw ApiException.BadRequest("That looks like a password or key: keep secrets out of memory.")
            Dim m As UserMemory
            Try
                m = Await _h.Chat.AddMemoryAsync(rc.User.Name, content, "user", Nothing).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await rc.WriteJsonAsync(201, m).ConfigureAwait(False)
        End Function

        Private Async Function ChatMemoryDelete(rc As RequestContext) As Task
            RequireChat()
            If Not Await _h.Chat.DeleteMemoryAsync(rc.User.Name, rc.Route("memoryId")).ConfigureAwait(False) Then Throw ApiException.NotFound("MEMORY_NOT_FOUND", "That memory does not exist.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        Private Async Function ChatMemoryClear(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(New With {.deleted = Await _h.Chat.ClearMemoriesAsync(rc.User.Name).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ saved prompts

        Private Async Function ChatPrompts(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(Await _h.Chat.ListPromptsAsync(rc.User.Name).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>Body {command, title, content}.</summary>
        Private Async Function ChatPromptSave(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim id = rc.Route("promptId")
            If id IsNot Nothing AndAlso Not (Await _h.Chat.ListPromptsAsync(rc.User.Name).ConfigureAwait(False)).Any(Function(p) p.Id = id) Then
                Throw ApiException.NotFound("PROMPT_NOT_FOUND", "That prompt does not exist.")
            End If
            Dim saved As ChatPrompt
            Try
                saved = Await _h.Chat.SavePromptAsync(rc.User.Name, id, JsonUtil.GetString(b, "command"), JsonUtil.GetString(b, "title"), JsonUtil.GetString(b, "content")).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await rc.WriteJsonAsync(If(id Is Nothing, 201, 200), saved).ConfigureAwait(False)
        End Function

        Private Async Function ChatPromptDelete(rc As RequestContext) As Task
            RequireChat()
            If Not Await _h.Chat.DeletePromptAsync(rc.User.Name, rc.Route("promptId")).ConfigureAwait(False) Then Throw ApiException.NotFound("PROMPT_NOT_FOUND", "That prompt does not exist.")
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ files shown in chats

        ''' <summary>Pictures and videos shown in the page; everything else downloads (an SVG or HTML file could run script on this site).</summary>
        Private Shared ReadOnly InlineTypes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp", "video/mp4", "video/webm", "video/quicktime"}

        Private Async Function WriteUploadAsync(rc As RequestContext, uploadId As String) As Task
            Dim r = Await _h.Uploads.ReadAsync(uploadId).ConfigureAwait(False)
            If r.Info Is Nothing OrElse r.Data Is Nothing Then Throw ApiException.NotFound("UPLOAD_NOT_FOUND", "File does not exist.")
            Dim inline = InlineTypes.Contains(If(r.Info.MimeType, ""))
            Dim safeName = Text.RegularExpressions.Regex.Replace(r.Info.Name, "[^\w\-. ]+", "_")
            Dim headers As New Dictionary(Of String, String) From {
                {"Content-Disposition", $"{If(inline, "inline", "attachment")}; filename=""{safeName}"""},
                {"Cache-Control", "private, max-age=86400"}, {"Content-Security-Policy", "default-src 'none'; sandbox"}, {"Accept-Ranges", "bytes"}}
            Dim type = If(inline, r.Info.MimeType, "application/octet-stream")
            Dim range = ParseRange(rc.Http.Request.Headers("Range"), r.Data.LongLength)
            If range.HasValue Then
                Dim part(CInt(range.Value.Last - range.Value.First)) As Byte
                Array.Copy(r.Data, range.Value.First, part, 0, part.Length)
                headers("Content-Range") = $"bytes {range.Value.First}-{range.Value.Last}/{r.Data.LongLength}"
                Await rc.WriteBytesAsync(206, type, part, headers).ConfigureAwait(False)
                Return
            End If
            Await rc.WriteBytesAsync(200, type, r.Data, headers).ConfigureAwait(False)
        End Function

        ''' <summary>A single byte range ("bytes=a-b", "bytes=a-", "bytes=-n"); Nothing for none or one that cannot be served.</summary>
        Friend Shared Function ParseRange(header As String, length As Long) As (First As Long, Last As Long)?
            If String.IsNullOrWhiteSpace(header) OrElse length <= 0 Then Return Nothing
            Dim m = Text.RegularExpressions.Regex.Match(header.Trim(), "^bytes=(\d*)-(\d*)$")
            If Not m.Success OrElse (m.Groups(1).Value.Length = 0 AndAlso m.Groups(2).Value.Length = 0) Then Return Nothing
            Dim first As Long, last As Long
            If m.Groups(1).Value.Length = 0 Then
                Dim suffix = Long.Parse(m.Groups(2).Value)
                If suffix <= 0 Then Return Nothing
                first = Math.Max(0, length - suffix)
                last = length - 1
            Else
                first = Long.Parse(m.Groups(1).Value)
                last = If(m.Groups(2).Value.Length = 0, length - 1, Math.Min(length - 1, Long.Parse(m.Groups(2).Value)))
            End If
            If first >= length OrElse last < first Then Return Nothing
            Return (first, last)
        End Function

        ''' <summary>A file of the user's (an attachment or an image the model created) for showing in the chat.</summary>
        Private Async Function ChatFile(rc As RequestContext) As Task
            Dim u = Await _h.Uploads.GetAsync(rc.Route("uploadId")).ConfigureAwait(False)
            If u Is Nothing OrElse Not (rc.User.IsAdmin OrElse String.Equals(u.CreatedBy, rc.User.Name, StringComparison.OrdinalIgnoreCase)) Then
                Throw ApiException.NotFound("UPLOAD_NOT_FOUND", "File does not exist.")
            End If
            Await WriteUploadAsync(rc, u.Id).ConfigureAwait(False)
        End Function

        ''' <summary>Body {url}: fetches a public web page's readable text and saves it as an upload the message can attach.</summary>
        Private Async Function ChatFetchPage(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim url = JsonUtil.GetString(b, "url")
            If String.IsNullOrWhiteSpace(url) Then Throw ApiException.BadRequest("url is required.")
            Dim page As (Title As String, Text As String, Url As String)
            Try
                page = Await Global.Agentic.Tools.PageReader.ReadAsync(url, 60000, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As Net.Http.HttpRequestException
                Throw New ApiException(502, "FETCH_FAILED", "Could not read the page: " & ex.Message)
            End Try
            If String.IsNullOrWhiteSpace(page.Text) Then Throw New ApiException(502, "EMPTY_PAGE", "The page has no readable text (it may need JavaScript).")
            Dim title = If(String.IsNullOrWhiteSpace(page.Title), New Uri(page.Url).Host, page.Title)
            Dim md = "# " & title & vbLf & page.Url & vbLf & vbLf & page.Text
            Dim info = Await _h.Uploads.SaveAsync(SafeSourceName(title) & ".md", Encoding.UTF8.GetBytes(md), rc.User.Name).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, UploadDto(info, 600)).ConfigureAwait(False)
        End Function

        ''' <summary>Body {chatIds:[…]}: saves each of the user's other conversations as an upload, to attach as context.</summary>
        Private Async Function ChatReferenceChats(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ids = JsonUtil.GetStringList(b, "chatIds")
            If ids Is Nothing OrElse ids.Count = 0 Then Throw ApiException.BadRequest("chatIds is required.")
            Dim uploads As New List(Of Object)
            For Each id In ids.Distinct().Take(10)
                Dim chat = Await _h.Chat.GetAsync(rc.User.Name, id).ConfigureAwait(False)
                If chat Is Nothing Then Continue For
                Dim sb As New StringBuilder("# " & chat.Title & vbLf & vbLf)
                For Each m In chat.Messages
                    If String.IsNullOrWhiteSpace(m.Content) Then Continue For
                    sb.Append("**").Append(If(m.Role = "user", "User", "Assistant")).Append(":** ").Append(m.Content).Append(vbLf & vbLf)
                Next
                Dim info = Await _h.Uploads.SaveAsync(SafeSourceName("chat " & chat.Title) & ".md", Encoding.UTF8.GetBytes(sb.ToString()), rc.User.Name).ConfigureAwait(False)
                uploads.Add(UploadDto(info, 300))
            Next
            If uploads.Count = 0 Then Throw ApiException.NotFound("NO_CHATS", "None of those conversations were found.")
            Await rc.OkAsync(New With {.uploads = uploads}).ConfigureAwait(False)
        End Function

        ''' <summary>A short, file-name-safe label from a title (letters, digits, space, dash, underscore; trimmed).</summary>
        Private Shared Function SafeSourceName(title As String) As String
            Dim cleaned = Text.RegularExpressions.Regex.Replace(If(title, ""), "[^\w \-]", "").Trim()
            cleaned = Text.RegularExpressions.Regex.Replace(cleaned, "\s+", " ")
            If cleaned.Length = 0 Then cleaned = "source"
            Return If(cleaned.Length > 60, cleaned.Substring(0, 60).Trim(), cleaned)
        End Function

        ''' <summary>Body: recorded speech (?name=speech.webm, ?language=en): its text, for dictating a message.</summary>
        Private Async Function ChatTranscribe(rc As RequestContext) As Task
            RequireChat()
            Dim audio = Await rc.ReadBytesAsync(25L * 1024 * 1024).ConfigureAwait(False)
            If audio Is Nothing OrElse audio.Length < 100 Then Throw ApiException.BadRequest("No audio was recorded.")
            Dim name = If(rc.QueryString("name"), "speech.webm")
            Dim mime = If(rc.Http.Request.ContentType, "audio/webm").Split(";"c)(0).Trim()
            If mime = "application/octet-stream" Then mime = If(name.EndsWith(".wav", StringComparison.OrdinalIgnoreCase), "audio/wav", If(name.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase), "audio/ogg", "audio/webm"))
            Dim text As String
            Try
                text = Await _h.ModelHub.TranscribeAsync(audio, name, mime, rc.QueryString("language"), rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Net.Http.HttpRequestException
                Throw New ApiException(502, "TRANSCRIPTION_FAILED", ex.Message)
            End Try
            Await rc.OkAsync(New With {.text = text}).ConfigureAwait(False)
        End Function

        Private Async Function SharedChat(rc As RequestContext) As Task
            Dim view = Await _h.Chat.GetSharedAsync(rc.Route("token")).ConfigureAwait(False)
            If view Is Nothing Then Throw ApiException.NotFound("SHARE_NOT_FOUND", "This link does not exist or is no longer shared.")
            rc.Http.Response.Headers("Cache-Control") = "no-store"
            Await rc.OkAsync(view).ConfigureAwait(False)
        End Function

        Private Async Function SharedChatFile(rc As RequestContext) As Task
            If Not Await _h.Chat.SharedImageAsync(rc.Route("token"), rc.Route("uploadId")).ConfigureAwait(False) Then Throw ApiException.NotFound("UPLOAD_NOT_FOUND", "File does not exist.")
            Await WriteUploadAsync(rc, rc.Route("uploadId")).ConfigureAwait(False)
        End Function
    End Class

End Namespace
