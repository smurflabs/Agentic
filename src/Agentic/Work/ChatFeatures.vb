Option Strict On
Option Explicit On
Option Infer On

Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Persistence
Imports Agentic.AI
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A project: chats grouped with shared instructions and knowledge files (like Claude and ChatGPT projects).</summary>
    Public Class ChatProject
        Public Property Id As String
        Public Property Username As String
        Public Property Name As String
        Public Property Description As String
        Public Property Instructions As String
        Public Property Files As New List(Of ChatAttachmentRef)
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
        ''' <summary>Chats in the project (filled by lists).</summary>
        Public Property Chats As Integer
    End Class

    Public Class UserMemory
        Public Property Id As String
        Public Property Content As String
        Public Property Source As String
        Public Property ChatId As String
        Public Property CreatedUtc As DateTime
    End Class

    Public Class ChatPrompt
        Public Property Id As String
        Public Property Command As String
        Public Property Title As String
        Public Property Content As String
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>The versions of a conversation at one point: earlier edits of a question or earlier answers, plus the current one.</summary>
    Public Class ChatVersionPoint
        ''' <summary>Index in the conversation where the versions differ.</summary>
        Public Property AnchorIndex As Integer
        Public Property Total As Integer
        ''' <summary>1-based position of the version shown now.</summary>
        Public Property Position As Integer
        ''' <summary>Every version in the order they were written; BranchId is Nothing for the one shown now.</summary>
        Public Property Versions As New List(Of ChatVersionRef)
    End Class

    Public Class ChatVersionRef
        Public Property BranchId As String
        Public Property CreatedUtc As DateTime
        Public Property Preview As String
    End Class

    ''' <summary>A stored earlier version: the messages from its anchor on, and the versions that were inside it.</summary>
    Public Class BranchPayload
        Public Property Messages As New List(Of ChatMessageRecord)
        Public Property Branches As New List(Of StoredBranch)
    End Class

    Public Class StoredBranch
        Public Property Id As String
        Public Property AnchorIndex As Integer
        Public Property PayloadJson As String
        Public Property CreatedUtc As DateTime
    End Class

    ' Chat features people know from ChatGPT and Claude: versions of edited questions and regenerated answers, projects,
    ' memory and personal instructions, read-only share links, export, feedback, temporary chats and saved prompts.
    Partial Public NotInheritable Class ChatService

        ' ------------------------------------------------------------------ versions (edit and regenerate keep what they replace)

        Private Async Function BranchesOfAsync(chatId As String) As Task(Of List(Of StoredBranch))
            Return Await _db.QueryAsync("SELECT * FROM ChatBranches WHERE ChatId=@c ORDER BY AnchorIndex, CreatedUtc",
                                        Function(r) New StoredBranch With {.Id = Db.Str(r, "Id"), .AnchorIndex = CInt(Db.Lng(r, "AnchorIndex")),
                                                                           .PayloadJson = Db.Str(r, "PayloadJson"), .CreatedUtc = Db.Dt(r, "CreatedUtc")},
                                        ("@c", CObj(chatId))).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Moves the conversation from message <paramref name="fromIndex"/> on into a stored version (with the versions inside
        ''' it), so that point can be written again. Returns the number of messages moved.
        ''' </summary>
        Friend Async Function ArchiveTailAsync(chat As ChatConversation, fromIndex As Integer) As Task(Of Integer)
            If fromIndex < 0 OrElse fromIndex >= chat.Messages.Count Then Return 0
            Dim tail = chat.Messages.Skip(fromIndex).ToList()
            Dim inner = (Await BranchesOfAsync(chat.Id).ConfigureAwait(False)).Where(Function(b) b.AnchorIndex > fromIndex).ToList()
            Dim payload As New BranchPayload With {.Messages = tail, .Branches = inner}
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Await Database.TxExecAsync(conn, tx, "INSERT INTO ChatBranches(Id, ChatId, AnchorIndex, PayloadJson, CreatedUtc) VALUES (@id, @c, @a, @p, @t)",
                                               ("@id", CObj(Ids.NewId("ver"))), ("@c", chat.Id), ("@a", fromIndex), ("@p", JsonUtil.ToJson(payload)), ("@t", Db.Ts(tail(0).CreatedUtc))).ConfigureAwait(False)
                    For Each b In inner
                        Await Database.TxExecAsync(conn, tx, "DELETE FROM ChatBranches WHERE Id=@id", ("@id", CObj(b.Id))).ConfigureAwait(False)
                    Next
                    For Each m In tail
                        Await Database.TxExecAsync(conn, tx, "DELETE FROM ChatMessages WHERE Id=@id AND ChatId=@c", ("@id", CObj(m.Id)), ("@c", chat.Id)).ConfigureAwait(False)
                    Next
                    Return True
                End Function).ConfigureAwait(False)
            Await DropSummaryIfCoveredAsync(chat, tail(0)).ConfigureAwait(False)
            chat.Messages.RemoveRange(fromIndex, tail.Count)
            Return tail.Count
        End Function

        Private Async Function DropSummaryIfCoveredAsync(chat As ChatConversation, firstChanged As ChatMessageRecord) As Task
            If chat.SummaryThroughUtc.HasValue AndAlso firstChanged.CreatedUtc <= chat.SummaryThroughUtc.Value Then
                Await _db.ExecuteAsync("UPDATE Chats SET Summary=NULL, SummaryThroughUtc=NULL, SummaryTokens=NULL, CompactedUtc=NULL WHERE Id=@id", ("@id", CObj(chat.Id))).ConfigureAwait(False)
                chat.Summary = Nothing
                chat.SummaryThroughUtc = Nothing
            End If
        End Function

        ''' <summary>Shows another version at its point: the version shown now is stored, the chosen one comes back.</summary>
        Public Async Function SwitchVersionAsync(username As String, chatId As String, branchId As String) As Task(Of ChatConversation)
            Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            If Live.IsLive(chatId) Then Throw New InvalidOperationException("Wait until the answer is finished.")
            Dim target = (Await BranchesOfAsync(chatId).ConfigureAwait(False)).FirstOrDefault(Function(b) b.Id = branchId)
            If target Is Nothing Then Throw New KeyNotFoundException("That version does not exist.")
            Dim payload = JsonUtil.FromJson(Of BranchPayload)(target.PayloadJson)
            If payload Is Nothing OrElse payload.Messages.Count = 0 Then Throw New KeyNotFoundException("That version is empty.")
            Dim anchor = Math.Min(target.AnchorIndex, chat.Messages.Count)
            If anchor < chat.Messages.Count Then Await ArchiveTailAsync(chat, anchor).ConfigureAwait(False)
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Await Database.TxExecAsync(conn, tx, "DELETE FROM ChatBranches WHERE Id=@id", ("@id", CObj(target.Id))).ConfigureAwait(False)
                    For Each b In payload.Branches
                        Await Database.TxExecAsync(conn, tx, "INSERT OR REPLACE INTO ChatBranches(Id, ChatId, AnchorIndex, PayloadJson, CreatedUtc) VALUES (@id, @c, @a, @p, @t)",
                                                   ("@id", CObj(b.Id)), ("@c", chatId), ("@a", b.AnchorIndex), ("@p", b.PayloadJson), ("@t", Db.Ts(b.CreatedUtc))).ConfigureAwait(False)
                    Next
                    Return True
                End Function).ConfigureAwait(False)
            For Each m In payload.Messages
                m.ChatId = chatId
                Await SaveMessageAsync(m).ConfigureAwait(False)
            Next
            Await _db.ExecuteAsync("UPDATE Chats SET UpdatedUtc=@n, LastPromptTokens=0 WHERE Id=@id", ("@n", CObj(Db.Ts(DateTime.UtcNow))), ("@id", chatId)).ConfigureAwait(False)
            Return Await GetAsync(username, chatId).ConfigureAwait(False)
        End Function

        ''' <summary>Every point of the conversation that has more than one version.</summary>
        Friend Async Function VersionPointsAsync(chat As ChatConversation) As Task(Of List(Of ChatVersionPoint))
            Dim result As New List(Of ChatVersionPoint)
            If chat.Messages Is Nothing Then Return result
            For Each g In (Await BranchesOfAsync(chat.Id).ConfigureAwait(False)).GroupBy(Function(b) b.AnchorIndex)
                If g.Key >= chat.Messages.Count Then Continue For
                Dim current = chat.Messages(g.Key)
                Dim refs = g.Select(Function(b)
                                        Dim p = JsonUtil.FromJson(Of BranchPayload)(b.PayloadJson)
                                        Return New ChatVersionRef With {.BranchId = b.Id, .CreatedUtc = b.CreatedUtc,
                                                                        .Preview = TextUtil.Truncate(If(p?.Messages.FirstOrDefault()?.Content, ""), 120, "…")}
                                    End Function).ToList()
                refs.Add(New ChatVersionRef With {.BranchId = Nothing, .CreatedUtc = current.CreatedUtc, .Preview = TextUtil.Truncate(If(current.Content, ""), 120, "…")})
                refs = refs.OrderBy(Function(r) r.CreatedUtc).ToList()
                result.Add(New ChatVersionPoint With {.AnchorIndex = g.Key, .Total = refs.Count, .Position = refs.FindIndex(Function(r) r.BranchId Is Nothing) + 1, .Versions = refs})
            Next
            Return result
        End Function

        ''' <summary>
        ''' Joins a continued answer to the text before it. Saved answers lose their trailing line break, so a continuation that
        ''' starts a new item of the same kind (a numbered or bulleted line, a table row, a heading, a quote) or a new line of
        ''' code gets one back; anything else (a word cut in half) joins directly.
        ''' </summary>
        Friend Shared Function JoinContinuation(before As String, after As String) As String
            If String.IsNullOrEmpty(before) Then Return after
            If String.IsNullOrEmpty(after) Then Return before
            If Char.IsWhiteSpace(before(before.Length - 1)) OrElse Char.IsWhiteSpace(after(0)) Then Return before & after
            Dim lastLine = before.Substring(before.LastIndexOf(ChrW(10)) + 1)
            Dim firstLine = after.Split(ChrW(10))(0)
            Const lineStart As String = "^\s*(\d+[.)]?\s|[-*+]\s|\||#{1,6}\s|>)"
            If Regex.IsMatch(firstLine, lineStart) AndAlso Regex.IsMatch(lastLine, lineStart) Then Return before & vbLf & after
            Dim fences = Regex.Matches(before, "^\s*```", RegexOptions.Multiline).Count
            If fences Mod 2 = 1 AndAlso (Regex.IsMatch(lastLine, "[;{}()\[\],:]\s*$") OrElse after.StartsWith("```", StringComparison.Ordinal)) Then Return before & vbLf & after
            If after.StartsWith("```", StringComparison.Ordinal) OrElse Regex.IsMatch(firstLine, "^#{1,6}\s") Then Return before & vbLf & vbLf & after
            Return before & after
        End Function

        ' ------------------------------------------------------------------ personalization and memory

        Public Const AboutMeKey As String = "chat.aboutMe"
        Public Const HowToRespondKey As String = "chat.howToRespond"
        Public Const MemoryOnKey As String = "chat.memory"
        Public Const RememberTool As String = "remember"
        Public Const ForgetTool As String = "forget"
        Private Const MaxMemories As Integer = 200

        Public Async Function MemoryEnabledAsync(username As String) As Task(Of Boolean)
            Return Not String.Equals(Await GetPreferenceAsync(username, MemoryOnKey).ConfigureAwait(False), "off", StringComparison.OrdinalIgnoreCase)
        End Function

        Public Function ListMemoriesAsync(username As String) As Task(Of List(Of UserMemory))
            Return _db.QueryAsync("SELECT * FROM UserMemories WHERE Username=@u ORDER BY CreatedUtc DESC",
                                  Function(r) New UserMemory With {.Id = Db.Str(r, "Id"), .Content = Db.Str(r, "Content"), .Source = Db.Str(r, "Source"),
                                                                   .ChatId = Db.Str(r, "ChatId"), .CreatedUtc = Db.Dt(r, "CreatedUtc")},
                                  ("@u", CObj(username)))
        End Function

        Public Async Function AddMemoryAsync(username As String, content As String, source As String, chatId As String) As Task(Of UserMemory)
            Dim text = TextUtil.Truncate(If(content, "").Trim(), 500)
            If text.Length < 3 Then Throw New ArgumentException("A memory needs some text.")
            Dim existing = Await ListMemoriesAsync(username).ConfigureAwait(False)
            Dim same = existing.FirstOrDefault(Function(x) String.Equals(x.Content, text, StringComparison.OrdinalIgnoreCase))
            If same IsNot Nothing Then Return same
            ' The oldest memories make room for new ones.
            For Each old In existing.Skip(MaxMemories - 1)
                Await DeleteMemoryAsync(username, old.Id).ConfigureAwait(False)
            Next
            Dim m As New UserMemory With {.Id = Ids.NewId("mem"), .Content = text, .Source = source, .ChatId = chatId, .CreatedUtc = DateTime.UtcNow}
            Await _db.ExecuteAsync("INSERT INTO UserMemories(Id, Username, Content, Source, ChatId, CreatedUtc) VALUES (@id, @u, @t, @s, @c, @n)",
                                   ("@id", CObj(m.Id)), ("@u", username), ("@t", m.Content), ("@s", source), ("@c", chatId), ("@n", Db.Ts(m.CreatedUtc))).ConfigureAwait(False)
            Return m
        End Function

        Public Async Function DeleteMemoryAsync(username As String, id As String) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("DELETE FROM UserMemories WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False) > 0
        End Function

        Public Function ClearMemoriesAsync(username As String) As Task(Of Integer)
            Return _db.ExecuteAsync("DELETE FROM UserMemories WHERE Username=@u", ("@u", CObj(username)))
        End Function

        ''' <summary>
        ''' What the model is told about the person and the chat's project: their personal instructions, what it remembers about
        ''' them, and the project's instructions and knowledge files. Nothing when there is nothing to tell.
        ''' </summary>
        Friend Async Function PersonalContextAsync(chat As ChatConversation, memoryTools As Boolean) As Task(Of String)
            Dim parts As New List(Of String)
            Dim about = Await GetPreferenceAsync(chat.Username, AboutMeKey).ConfigureAwait(False)
            Dim how = Await GetPreferenceAsync(chat.Username, HowToRespondKey).ConfigureAwait(False)
            If Not String.IsNullOrWhiteSpace(about) Then parts.Add("What the user wants you to know about them:" & vbLf & about.Trim())
            If Not String.IsNullOrWhiteSpace(how) Then parts.Add("How the user wants you to respond:" & vbLf & how.Trim())
            If memoryTools Then
                Dim memories = Await ListMemoriesAsync(chat.Username).ConfigureAwait(False)
                Dim sb As New StringBuilder("Memory: what you remember about the user from earlier chats (use it when relevant; never mention this list unless asked). ")
                sb.Append($"When the user asks you to remember something, or shares a lasting preference or fact about themselves, save it with {RememberTool}; ")
                sb.Append($"when they ask you to forget something, use {ForgetTool}.")
                If memories.Count = 0 Then
                    sb.Append(vbLf & "(nothing yet)")
                Else
                    Dim used = 0
                    For Each m In memories.AsEnumerable().Reverse()
                        If used > 6000 Then Exit For
                        sb.Append(vbLf & "- [" & m.Id & "] " & m.Content)
                        used += m.Content.Length
                    Next
                End If
                parts.Add(sb.ToString())
            End If
            If Not String.IsNullOrWhiteSpace(chat.ProjectId) Then
                Dim p = Await GetProjectAsync(chat.Username, chat.ProjectId).ConfigureAwait(False)
                If p IsNot Nothing Then
                    Dim sb As New StringBuilder($"This chat belongs to the project ""{p.Name}"".")
                    If Not String.IsNullOrWhiteSpace(p.Description) Then sb.Append(" " & p.Description.Trim())
                    If Not String.IsNullOrWhiteSpace(p.Instructions) Then sb.Append(vbLf & "Project instructions:" & vbLf & p.Instructions.Trim())
                    Dim budget = 60000
                    For Each f In p.Files
                        Dim u = Await _uploads.GetAsync(f.UploadId).ConfigureAwait(False)
                        If u Is Nothing Then Continue For
                        Dim text = If(u.ExtractedText, "")
                        If text.Length = 0 Then
                            sb.Append(vbLf & $"Project file {u.Name} ({u.Kind}): {u.Summary}")
                            Continue For
                        End If
                        Dim take = Math.Min(text.Length, Math.Max(0, budget))
                        If take <= 0 Then sb.Append(vbLf & $"Project file {u.Name}: (left out: the project's files are too long)") : Continue For
                        sb.Append(vbLf & $"<project_file name=""{u.Name}"">" & vbLf & text.Substring(0, take) & If(take < text.Length, vbLf & "[…]", "") & vbLf & "</project_file>")
                        budget -= take
                    Next
                    parts.Add(sb.ToString())
                End If
            End If
            Return If(parts.Count = 0, Nothing, String.Join(vbLf & vbLf, parts))
        End Function

        ''' <summary>
        ''' RAG: the chunks of the chat's selected knowledge bases that best match the question, as a cited context block
        ''' ([n] markers the answer can reference), and the same chunks added to the answer's sources. Nothing when no base
        ''' is selected, RAG is off, it is a continued answer, or nothing relevant was found.
        ''' </summary>
        Friend Async Function KnowledgeContextAsync(username As String, settings As ChatSettings, lastUser As ChatMessageRecord, continuing As ChatMessageRecord,
                                                    answer As ChatMessageRecord, emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of String)
            If Rag Is Nothing OrElse continuing IsNot Nothing OrElse settings?.Knowledge Is Nothing OrElse settings.Knowledge.Count = 0 Then Return Nothing
            If Not Rag.Enabled OrElse String.IsNullOrWhiteSpace(lastUser?.Content) Then Return Nothing
            Await emit("status", New With {.text = "Searching your knowledge…"}).ConfigureAwait(False)
            Dim hits = Await Rag.RetrieveAsync(username, settings.Knowledge, lastUser.Content, Rag.Options.TopK, ct).ConfigureAwait(False)
            If hits.Count = 0 Then Return Nothing
            Dim sb As New StringBuilder("Knowledge retrieved from the user's selected knowledge bases for this question. Treat it as the authoritative source, " &
                                        "cite the passages you use as [n], and if it does not contain the answer, say so instead of guessing.")
            Dim budget = Rag.Options.MaxContextChars
            For Each h In hits
                Dim idx = answer.Sources.Count + 1
                Dim snippet = TextUtil.Truncate(h.Text, Math.Max(200, budget))
                sb.Append(vbLf & vbLf & $"[{idx}] {h.DocName} (relevance {h.Score:0.00}):" & vbLf & snippet)
                answer.Sources.Add(New ChatSource With {.Index = idx, .Title = h.DocName, .Internal = True,
                                                        .Url = If(String.IsNullOrEmpty(h.UploadId), Nothing, "/api/chat/files/" & h.UploadId),
                                                        .Snippet = TextUtil.Truncate(h.Text, 400)})
                budget -= snippet.Length
                If budget <= 200 Then Exit For
            Next
            Await emit("sources", answer.Sources).ConfigureAwait(False)
            Return sb.ToString()
        End Function

        ' ------------------------------------------------------------------ projects

        Private Shared Function MapProject(r As Microsoft.Data.Sqlite.SqliteDataReader) As ChatProject
            Return New ChatProject With {
                .Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .Name = Db.Str(r, "Name"), .Description = Db.Str(r, "Description"),
                .Instructions = Db.Str(r, "Instructions"), .Files = If(JsonUtil.FromJson(Of List(Of ChatAttachmentRef))(Db.Str(r, "FilesJson")), New List(Of ChatAttachmentRef)),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
        End Function

        Public Async Function ListProjectsAsync(username As String) As Task(Of List(Of ChatProject))
            Dim list = Await _db.QueryAsync("SELECT p.*, (SELECT COUNT(*) FROM Chats c WHERE c.ProjectId=p.Id AND c.Temporary=0) AS ChatCount FROM ChatProjects p WHERE p.Username=@u ORDER BY p.UpdatedUtc DESC",
                                            Function(r)
                                                Dim p = MapProject(r)
                                                p.Chats = CInt(Db.Lng(r, "ChatCount"))
                                                Return p
                                            End Function, ("@u", CObj(username))).ConfigureAwait(False)
            Return list
        End Function

        Public Function GetProjectAsync(username As String, id As String) As Task(Of ChatProject)
            Return _db.QuerySingleAsync("SELECT * FROM ChatProjects WHERE Id=@id AND Username=@u", AddressOf MapProject, ("@id", CObj(id)), ("@u", username))
        End Function

        ''' <summary>Creates a project, or (with an id) changes one. Files are upload ids; Nothing leaves them as they are.</summary>
        Public Async Function SaveProjectAsync(username As String, id As String, name As String, description As String, instructions As String,
                                               fileIds As List(Of String)) As Task(Of ChatProject)
            Dim now = DateTime.UtcNow
            Dim p As ChatProject
            If String.IsNullOrWhiteSpace(id) Then
                If String.IsNullOrWhiteSpace(name) Then Throw New ArgumentException("A project needs a name.")
                p = New ChatProject With {.Id = Ids.NewId("prj"), .Username = username, .CreatedUtc = now}
            Else
                p = Await GetProjectAsync(username, id).ConfigureAwait(False)
                If p Is Nothing Then Throw New KeyNotFoundException("Project does not exist.")
            End If
            If name IsNot Nothing Then p.Name = TextUtil.Truncate(name.Trim(), 80)
            If description IsNot Nothing Then p.Description = TextUtil.Truncate(description.Trim(), 500)
            If instructions IsNot Nothing Then p.Instructions = TextUtil.Truncate(instructions.Trim(), 20000)
            If fileIds IsNot Nothing Then
                Dim files As New List(Of ChatAttachmentRef)
                For Each fid In fileIds.Distinct().Take(30)
                    Dim u = Await _uploads.GetAsync(fid).ConfigureAwait(False)
                    If u IsNot Nothing Then files.Add(New ChatAttachmentRef With {.UploadId = u.Id, .Name = u.Name, .Kind = u.Kind, .Summary = u.Summary, .SizeBytes = u.SizeBytes})
                Next
                p.Files = files
            End If
            If String.IsNullOrWhiteSpace(p.Name) Then Throw New ArgumentException("A project needs a name.")
            p.UpdatedUtc = now
            Await _db.ExecuteAsync("INSERT INTO ChatProjects(Id, Username, Name, Description, Instructions, FilesJson, CreatedUtc, UpdatedUtc) VALUES (@id, @u, @n, @d, @i, @f, @c, @m) " &
                                   "ON CONFLICT(Id) DO UPDATE SET Name=@n, Description=@d, Instructions=@i, FilesJson=@f, UpdatedUtc=@m",
                                   ("@id", CObj(p.Id)), ("@u", username), ("@n", p.Name), ("@d", p.Description), ("@i", p.Instructions),
                                   ("@f", If(p.Files.Count = 0, Nothing, JsonUtil.ToJson(p.Files))), ("@c", Db.Ts(p.CreatedUtc)), ("@m", Db.Ts(now))).ConfigureAwait(False)
            Return p
        End Function

        ''' <summary>Deletes a project; its chats stay (outside any project).</summary>
        Public Async Function DeleteProjectAsync(username As String, id As String) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("DELETE FROM ChatProjects WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False)
            If n > 0 Then Await _db.ExecuteAsync("UPDATE Chats SET ProjectId=NULL WHERE ProjectId=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False)
            Return n > 0
        End Function

        ''' <summary>Puts a chat in a project (Nothing or "" takes it out).</summary>
        Public Async Function MoveToProjectAsync(username As String, chatId As String, projectId As String) As Task
            Dim pid = If(String.IsNullOrWhiteSpace(projectId), Nothing, projectId)
            If pid IsNot Nothing AndAlso Await GetProjectAsync(username, pid).ConfigureAwait(False) Is Nothing Then Throw New KeyNotFoundException("Project does not exist.")
            Await _db.ExecuteAsync("UPDATE Chats SET ProjectId=@p WHERE Id=@id AND Username=@u", ("@p", CObj(pid)), ("@id", chatId), ("@u", username)).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ share links and export

        ''' <summary>Makes a read-only link to the conversation as it is now and later (anyone with the link can read it).</summary>
        Public Async Function ShareAsync(username As String, chatId As String) As Task(Of String)
            Dim chat = Await GetAsync(username, chatId, False).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            If chat.Temporary Then Throw New InvalidOperationException("Temporary chats cannot be shared.")
            If Not String.IsNullOrEmpty(chat.ShareToken) Then Return chat.ShareToken
            Dim token = Convert.ToHexString(RandomNumberGenerator.GetBytes(18)).ToLowerInvariant()
            Await _db.ExecuteAsync("UPDATE Chats SET ShareToken=@t, SharedUtc=@n WHERE Id=@id AND Username=@u",
                                   ("@t", CObj(token)), ("@n", Db.Ts(DateTime.UtcNow)), ("@id", chatId), ("@u", username)).ConfigureAwait(False)
            Return token
        End Function

        Public Async Function UnshareAsync(username As String, chatId As String) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("UPDATE Chats SET ShareToken=NULL, SharedUtc=NULL WHERE Id=@id AND Username=@u", ("@id", CObj(chatId)), ("@u", username)).ConfigureAwait(False) > 0
        End Function

        ''' <summary>A shared conversation for its public page: what was said, without reasoning, tool output or costs.</summary>
        Public Async Function GetSharedAsync(token As String) As Task(Of Object)
            If String.IsNullOrWhiteSpace(token) OrElse token.Length < 20 OrElse token.Length > 64 Then Return Nothing
            Dim chat = Await _db.QuerySingleAsync("SELECT * FROM Chats WHERE ShareToken=@t", AddressOf MapChat, ("@t", CObj(token))).ConfigureAwait(False)
            If chat Is Nothing Then Return Nothing
            Dim messages = Await _db.QueryAsync("SELECT * FROM ChatMessages WHERE ChatId=@c ORDER BY CreatedUtc, rowid", AddressOf MapMessage, ("@c", CObj(chat.Id))).ConfigureAwait(False)
            Return New With {
                .title = chat.Title, .model = chat.Model, .sharedUtc = chat.SharedUtc, .createdUtc = chat.CreatedUtc,
                .messages = messages.Where(Function(m) Not String.IsNullOrWhiteSpace(m.Content) OrElse m.Attachments.Count > 0).Select(Function(m) New With {
                    .role = m.Role, .content = m.Content, .model = If(m.RoutedModel, m.Model), .createdUtc = m.CreatedUtc,
                    .attachments = m.Attachments.Select(Function(a) New With {a.Name, a.Kind, .uploadId = If(a.Kind = "image" OrElse a.Kind = "video", a.UploadId, Nothing)}).ToList(),
                    .sources = m.Sources.Where(Function(s) Not s.Internal).Select(Function(s) New With {s.Index, s.Title, s.Url}).ToList(),
                    .tools = m.ToolCalls.Select(Function(t) t.Name).Distinct().ToList()}).ToList()}
        End Function

        ''' <summary>True when the upload is an image or a video shown in the shared conversation (its public page may load it).</summary>
        Public Async Function SharedImageAsync(token As String, uploadId As String) As Task(Of Boolean)
            If String.IsNullOrWhiteSpace(token) OrElse String.IsNullOrWhiteSpace(uploadId) Then Return False
            Dim chatId = Await _db.ScalarAsync("SELECT Id FROM Chats WHERE ShareToken=@t", ("@t", CObj(token))).ConfigureAwait(False)
            If chatId Is Nothing OrElse TypeOf chatId Is DBNull Then Return False
            Dim messages = Await _db.QueryAsync("SELECT * FROM ChatMessages WHERE ChatId=@c AND AttachmentsJson LIKE @l", AddressOf MapMessage,
                                                ("@c", CObj(chatId.ToString())), ("@l", "%" & uploadId & "%")).ConfigureAwait(False)
            Return messages.Any(Function(m) m.Attachments.Any(Function(a) a.UploadId = uploadId AndAlso (a.Kind = "image" OrElse a.Kind = "video")))
        End Function

        ''' <summary>The conversation as Markdown (what was said, with sources and the tools used).</summary>
        Public Shared Function ToMarkdown(chat As ChatConversation) As String
            Dim sb As New StringBuilder()
            sb.Append("# ").Append(chat.Title).Append(vbLf & vbLf)
            sb.Append($"*{chat.Model} · {chat.CreatedUtc:yyyy-MM-dd HH:mm} UTC*").Append(vbLf & vbLf)
            For Each m In chat.Messages
                sb.Append(If(m.Role = "user", "## You", "## " & If(m.RoutedModel, m.Model))).Append(vbLf & vbLf)
                If m.Attachments.Count > 0 Then sb.Append("Attached: ").Append(String.Join(", ", m.Attachments.Select(Function(a) a.Name))).Append(vbLf & vbLf)
                If m.ToolCalls.Count > 0 Then sb.Append("*Tools: ").Append(String.Join(", ", m.ToolCalls.Select(Function(t) t.Name).Distinct())).Append("*" & vbLf & vbLf)
                sb.Append(If(m.Content, "").Trim()).Append(vbLf & vbLf)
                If m.Sources.Count > 0 Then
                    For Each s In m.Sources
                        sb.Append($"[{s.Index}] {s.Title} — {s.Url}").Append(vbLf)
                    Next
                    sb.Append(vbLf)
                End If
                If Not String.IsNullOrEmpty(m.Error) Then sb.Append("> " & m.Error).Append(vbLf & vbLf)
            Next
            Return sb.ToString()
        End Function

        ' ------------------------------------------------------------------ feedback

        ''' <summary>Thumbs up (1), down (-1) or nothing (0) on an answer, with an optional note.</summary>
        Public Async Function SetFeedbackAsync(username As String, chatId As String, messageId As String, value As Integer, note As String) As Task(Of Boolean)
            If Await GetAsync(username, chatId, False).ConfigureAwait(False) Is Nothing Then Return False
            Dim v = Math.Sign(value)
            Return Await _db.ExecuteAsync("UPDATE ChatMessages SET Feedback=@v, FeedbackNote=@n WHERE Id=@id AND ChatId=@c AND Role='assistant'",
                                          ("@v", If(v = 0, Nothing, CObj(v))), ("@n", If(v = 0, Nothing, TextUtil.Truncate(note, 1000))),
                                          ("@id", messageId), ("@c", chatId)).ConfigureAwait(False) > 0
        End Function

        ' ------------------------------------------------------------------ temporary chats

        ''' <summary>Deletes temporary chats older than a day (they are never in the history).</summary>
        Public Async Function PurgeTemporaryAsync() As Task(Of Integer)
            Dim cutoff = Db.Ts(DateTime.UtcNow.AddDays(-1))
            Dim ids = Await _db.QueryAsync("SELECT Id FROM Chats WHERE Temporary=1 AND UpdatedUtc<@t", Function(r) Db.Str(r, "Id"), ("@t", CObj(cutoff))).ConfigureAwait(False)
            For Each id In ids
                If Live.IsLive(id) Then Continue For
                Await _db.ExecuteAsync("DELETE FROM ChatMessages WHERE ChatId=@id", ("@id", CObj(id))).ConfigureAwait(False)
                Await _db.ExecuteAsync("DELETE FROM ChatBranches WHERE ChatId=@id", ("@id", CObj(id))).ConfigureAwait(False)
                Await _db.ExecuteAsync("DELETE FROM Chats WHERE Id=@id", ("@id", CObj(id))).ConfigureAwait(False)
            Next
            Return ids.Count
        End Function

        ' ------------------------------------------------------------------ saved prompts

        Public Function ListPromptsAsync(username As String) As Task(Of List(Of ChatPrompt))
            Return _db.QueryAsync("SELECT * FROM ChatPrompts WHERE Username=@u ORDER BY Command",
                                  Function(r) New ChatPrompt With {.Id = Db.Str(r, "Id"), .Command = Db.Str(r, "Command"), .Title = Db.Str(r, "Title"),
                                                                   .Content = Db.Str(r, "Content"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")},
                                  ("@u", CObj(username)))
        End Function

        ''' <summary>Saves a prompt under /command (a new one, or a change to the one with that id).</summary>
        Public Async Function SavePromptAsync(username As String, id As String, command As String, title As String, content As String) As Task(Of ChatPrompt)
            Dim cmd = If(command, "").Trim().TrimStart("/"c).ToLowerInvariant()
            If Not Regex.IsMatch(cmd, "^[a-z0-9][a-z0-9_-]{0,39}$") Then Throw New ArgumentException("The command: 1-40 letters, digits, - or _ (it is typed as /command).")
            If String.IsNullOrWhiteSpace(content) Then Throw New ArgumentException("The prompt needs some text.")
            Dim now = DateTime.UtcNow
            Dim p As New ChatPrompt With {.Id = If(String.IsNullOrWhiteSpace(id), Ids.NewId("prm"), id), .Command = cmd, .Title = TextUtil.Truncate(title?.Trim(), 100),
                                          .Content = TextUtil.Truncate(content, 20000), .UpdatedUtc = now}
            Dim clash = (Await ListPromptsAsync(username).ConfigureAwait(False)).FirstOrDefault(Function(x) x.Command = cmd AndAlso x.Id <> p.Id)
            If clash IsNot Nothing Then Throw New ArgumentException($"You already have a prompt /{cmd}.")
            Await _db.ExecuteAsync("INSERT INTO ChatPrompts(Id, Username, Command, Title, Content, CreatedUtc, UpdatedUtc) VALUES (@id, @u, @c, @t, @x, @n, @n) " &
                                   "ON CONFLICT(Id) DO UPDATE SET Command=@c, Title=@t, Content=@x, UpdatedUtc=@n",
                                   ("@id", CObj(p.Id)), ("@u", username), ("@c", cmd), ("@t", p.Title), ("@x", p.Content), ("@n", Db.Ts(now))).ConfigureAwait(False)
            Return p
        End Function

        Public Async Function DeletePromptAsync(username As String, id As String) As Task(Of Boolean)
            Return Await _db.ExecuteAsync("DELETE FROM ChatPrompts WHERE Id=@id AND Username=@u", ("@id", CObj(id)), ("@u", username)).ConfigureAwait(False) > 0
        End Function

        ' ------------------------------------------------------------------ memory and image tools

        ''' <summary>remember / forget, called by the model.</summary>
        Private Async Function RunMemoryToolAsync(username As String, chatId As String, name As String, argsJson As String,
                                                  emit As Func(Of String, Object, Task)) As Task(Of (Ok As Boolean, Text As String))
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            If name = RememberTool Then
                Dim fact = If(JsonUtil.GetString(args, "fact"), JsonUtil.GetString(args, "content"))
                If String.IsNullOrWhiteSpace(fact) Then Return (False, "fact is required.")
                If Global.Agentic.Security.SecretRedactor.LooksLikeSecret(fact) Then Return (False, "That looks like a password or key: secrets are never saved to memory.")
                Dim m = Await AddMemoryAsync(username, fact, "chat", chatId).ConfigureAwait(False)
                Await emit("memory", New With {.action = "saved", .memory = m}).ConfigureAwait(False)
                Return (True, $"Saved to memory ({m.Id}).")
            End If
            Dim id = JsonUtil.GetString(args, "id")
            Dim contains = JsonUtil.GetString(args, "contains")
            Dim gone As New List(Of UserMemory)
            For Each m In Await ListMemoriesAsync(username).ConfigureAwait(False)
                Dim hit = (Not String.IsNullOrWhiteSpace(id) AndAlso m.Id = id.Trim()) OrElse
                          (Not String.IsNullOrWhiteSpace(contains) AndAlso contains.Trim().Length >= 3 AndAlso m.Content.Contains(contains.Trim(), StringComparison.OrdinalIgnoreCase))
                If hit AndAlso Await DeleteMemoryAsync(username, m.Id).ConfigureAwait(False) Then gone.Add(m)
            Next
            If gone.Count = 0 Then Return (False, "No memory matched; nothing was removed.")
            Await emit("memory", New With {.action = "forgot", .memories = gone}).ConfigureAwait(False)
            Return (True, $"Removed {gone.Count} memor{If(gone.Count = 1, "y", "ies")}: " & String.Join("; ", gone.Select(Function(m) m.Content)))
        End Function

        Public Const ImageTool As String = "generate_image"

        ''' <summary>Image generation for chats (set by the host): sizes and the engine's model name.</summary>
        Public Property ImageOptions As ImageGenerationOptions
        ''' <summary>The image engine plus fallback image models, shared with the agents (set by the host).</summary>
        Public Property ImageRouter As Global.Agentic.AI.ImageRouter
        ''' <summary>What one image costs a User account (Finance → Pricing → image-generation); 0 = free.</summary>
        Public Property ImagePriceMicros As Func(Of Long)

        Public ReadOnly Property ImagesAvailable As Boolean
            Get
                Return ImageOptions IsNot Nothing AndAlso ImageRouter IsNot Nothing AndAlso ImageRouter.Enabled
            End Get
        End Property

        ' ------------------------------------------------------------------ pictures for models that cannot read them

        ''' <summary>Models known to read images, best first (Vision.ModelKeys first); set by the host.</summary>
        Public Property VisionModels As New List(Of String)
        ''' <summary>Models known not to read images (configured without the Vision capability, like the local Qwen); set by the host.</summary>
        Public Property NoVisionModels As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>Models that turned pictures away since the start (their error said they cannot read images).</summary>
        Private ReadOnly _learnedNoVision As New Concurrent.ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)

        Friend Function CanSeeImages(model As String) As Boolean
            ' A user's own model sees pictures when they said it does (My models → Vision).
            If ByoService.IsByoId(model) Then
                Dim b = Byo?.Get(model)
                If b IsNot Nothing AndAlso Not b.Vision Then Return False
            End If
            Return Not (NoVisionModels.Contains(model) OrElse _learnedNoVision.ContainsKey(model))
        End Function

        ''' <summary>The vision model that answers a message with pictures for this model (one the account may use); Nothing when none.</summary>
        Private Function PickVisionModel(model As String, canUseModel As Func(Of String, Boolean)) As String
            Return VisionModels.FirstOrDefault(Function(v) Not String.Equals(v, model, StringComparison.OrdinalIgnoreCase) AndAlso Not _hub.IsHidden(v) AndAlso
                                                           CanSeeImages(v) AndAlso (canUseModel Is Nothing OrElse canUseModel(v)))
        End Function

        ''' <summary>A provider error saying the model cannot take images ("started without the vision encoder", "image input is not supported" …).</summary>
        Friend Shared Function IsNoVisionError(message As String) As Boolean
            Return Regex.IsMatch(If(message, ""), "vision encoder|no vision|" &
                                                   "(does not|doesn'?t|do not|not|cannot|can'?t|won'?t|unable to) (accept|take|handle|read|see|process|support)(s|ed)? (an? )?(image|images|vision|multimodal|picture|photo)|" &
                                                   "image (input|content)s? (is |are )?(not supported|unsupported|not accepted)|" &
                                                   "image_url.{0,40}(not supported|unsupported)|multimodal.{0,30}not (supported|enabled)", RegexOptions.IgnoreCase)
        End Function

        ''' <summary>generate_image: creates the image, keeps it as a file of the user and shows it with the answer.</summary>
        Private Async Function RunImageToolAsync(username As String, isAdmin As Boolean, chat As ChatConversation, argsJson As String, answer As ChatMessageRecord,
                                                 emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
            If Not ImagesAvailable Then Return (False, "Image generation is turned off on this server.")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            Dim prompt = JsonUtil.GetString(args, "prompt")
            If String.IsNullOrWhiteSpace(prompt) Then Return (False, "prompt is required.")
            Dim asked = JsonUtil.GetString(args, "size")
            Dim size = asked
            If String.IsNullOrWhiteSpace(size) OrElse Not Regex.IsMatch(size, "^\d{3,4}x\d{3,4}$") Then size = ImageOptions.DefaultSize
            If ImageOptions.AllowedSizes.Count > 0 AndAlso Not ImageOptions.AllowedSizes.Contains(size, StringComparer.OrdinalIgnoreCase) Then size = ImageOptions.DefaultSize
            ' A picture from this chat to change or build on (POST /images/edits): one the user attached or one made earlier.
            Dim source As ChatAttachmentRef = Nothing
            Dim sourceData As Byte() = Nothing
            Dim sourceMime As String = Nothing
            Dim reference = JsonUtil.GetString(args, "reference_image")
            If Not String.IsNullOrWhiteSpace(reference) Then
                Dim pictures = ChatPictures(chat, answer)
                If pictures.Count = 0 Then Return (False, "There is no picture in this chat to change. Leave reference_image out to draw a new picture, or ask the user to attach one.")
                Dim wanted = reference.Trim()
                Dim found = If(LatestPictureWords.Contains(wanted), pictures.Last(),
                               pictures.LastOrDefault(Function(p) String.Equals(p.Ref.Name, wanted, StringComparison.OrdinalIgnoreCase) OrElse String.Equals(p.Ref.UploadId, wanted, StringComparison.Ordinal)))
                If found.Ref Is Nothing Then
                    Return (False, $"There is no picture called '{wanted}' in this chat. Pictures: {String.Join(", ", pictures.Select(Function(p) p.Ref.Name).Distinct(StringComparer.OrdinalIgnoreCase).TakeLast(12))} (or ""latest"").")
                End If
                source = found.Ref
                Dim file = Await _uploads.ReadAsync(source.UploadId).ConfigureAwait(False)
                If file.Info Is Nothing OrElse file.Data Is Nothing Then Return (False, $"The picture '{source.Name}' is no longer available.")
                sourceData = file.Data
                If source.Name.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) Then sourceData = If(FileExtractor.IcoLargestPng(sourceData), sourceData)
                sourceMime = Global.Agentic.AI.OpenAICompatibleProvider.SniffMime(sourceData)
                If sourceMime Is Nothing OrElse Not EditableMimes.Contains(sourceMime) Then Return (False, $"'{source.Name}' cannot be changed: the picture must be PNG, JPEG or WebP.")
                If sourceData.LongLength > MaxReferenceBytes Then Return (False, $"'{source.Name}' is larger than {MaxReferenceBytes \ (1024 * 1024)} MB, too large to change.")
                ' Without an asked size the result keeps the picture's shape.
                If String.IsNullOrWhiteSpace(asked) Then size = Nothing
            End If
            Dim price = If(ImagePriceMicros Is Nothing, 0L, ImagePriceMicros.Invoke())
            If price > 0 AndAlso Not isAdmin AndAlso Accounts IsNot Nothing Then
                Dim acc = Await Accounts.GetAccountAsync(username).ConfigureAwait(False)
                If acc IsNot Nothing AndAlso acc.Charged AndAlso acc.BalanceMicros < price Then
                    Return (False, $"The user's balance ({TextUtil.FormatMicros(acc.BalanceMicros)}) does not cover an image ({TextUtil.FormatMicros(price)}). Tell them to ask an administrator to add money.")
                End If
            End If
            ' ImageGeneration.Model first; when it cannot make the picture, the next image model is asked (ImageRouter).
            Dim made As Global.Agentic.AI.RoutedImage
            If source Is Nothing Then
                made = Await ImageRouter.GenerateAsync(prompt, size, ct,
                    Function(model) emit("status", New With {.text = $"Creating the image with {model}…"})).ConfigureAwait(False)
            Else
                Dim sourceName = source.Name
                made = Await ImageRouter.EditAsync(prompt, size, sourceData, sourceMime, sourceName, ct,
                    Function(model) emit("status", New With {.text = $"Changing {sourceName} with {model}…"})).ConfigureAwait(False)
            End If
            Dim result = made.Result
            Dim ext = If(If(result.MimeType, "").Contains("jpeg"), ".jpg", If(If(result.MimeType, "").Contains("webp"), ".webp", ".png"))
            Dim u = Await _uploads.SaveAsync($"image-{DateTime.UtcNow:yyyyMMdd-HHmmss}{ext}", result.Data, username).ConfigureAwait(False)
            Dim att As New ChatAttachmentRef With {.UploadId = u.Id, .Name = u.Name, .Kind = "image", .SizeBytes = u.SizeBytes,
                                                   .Summary = TextUtil.Truncate(prompt, 260, "…") & " · " & made.Model & If(source Is Nothing, "", " · from " & source.Name)}
            answer.Attachments.Add(att)
            If price > 0 AndAlso Not isAdmin AndAlso Accounts IsNot Nothing Then
                Dim after = Await Accounts.ChargeAsync(username, price, "Image", made.Model, Nothing, Nothing, Nothing, If(source Is Nothing, "Chat image: ", "Chat image edit: ") & TextUtil.Truncate(prompt, 80, "…")).ConfigureAwait(False)
                If after.HasValue Then answer.CostMicros = If(answer.CostMicros, 0L) + Await Accounts.WithSurchargeAsync(username, price).ConfigureAwait(False)
            End If
            Await emit("image", New With {.messageId = answer.Id, .attachment = att}).ConfigureAwait(False)
            Dim note = If(made.Failures.Count = 0, "", $" (the first choice was unavailable: {String.Join("; ", made.Failures)})")
            If source IsNot Nothing Then
                Return (True, $"The picture {source.Name} was changed by {made.Model}{note}; the result ({u.Name}) is shown to the user with your answer. Do not add a link or Markdown image for it; describe the change briefly if useful.")
            End If
            Return (True, $"The image ({size}) was made by {made.Model}{note} and is shown to the user with your answer. Do not add a link or Markdown image for it; describe it briefly if useful.")
        End Function

        ''' <summary>Words that mean "the most recent picture" for reference_image.</summary>
        Private Shared ReadOnly LatestPictureWords As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"latest", "last", "previous", "current", "this", "it"}
        ''' <summary>What the images edits endpoint takes.</summary>
        Private Shared ReadOnly EditableMimes As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"image/png", "image/jpeg", "image/webp"}
        Private Const MaxReferenceBytes As Long = 20L * 1024 * 1024

        ''' <summary>The pictures in this chat, oldest first: attached by the user or made by the model (this answer's own last).</summary>
        Friend Shared Function ChatPictures(chat As ChatConversation, answer As ChatMessageRecord) As List(Of (Ref As ChatAttachmentRef, ByUser As Boolean))
            Dim list As New List(Of (Ref As ChatAttachmentRef, ByUser As Boolean))
            Dim add = Sub(m As ChatMessageRecord)
                          For Each a In If(m.Attachments, New List(Of ChatAttachmentRef))
                              If a IsNot Nothing AndAlso a.Kind = "image" AndAlso Not String.IsNullOrEmpty(a.UploadId) Then list.Add((a, m.Role = "user"))
                          Next
                      End Sub
            For Each m In chat.Messages
                If answer Is Nothing OrElse m IsNot answer Then add(m)
            Next
            If answer IsNot Nothing Then add(answer)
            Return list
        End Function

        ' ------------------------------------------------------------------ videos

        Public Const VideoTool As String = "generate_video"

        ''' <summary>Video generation (set by the host): the video API in VideoGeneration, nothing else.</summary>
        Public Property Videos As Global.Agentic.AI.VideoService

        Public ReadOnly Property VideosAvailable As Boolean
            Get
                Return Videos IsNot Nothing AndAlso Videos.Enabled
            End Get
        End Property

        ''' <summary>generate_video: makes the clip (one to three minutes, with progress), keeps it as a file of the user and shows it with the answer.</summary>
        Private Async Function RunVideoToolAsync(username As String, isAdmin As Boolean, chat As ChatConversation, argsJson As String, answer As ChatMessageRecord,
                                                 emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
            If Not VideosAvailable Then Return (False, "Video generation is turned off on this server.")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            Dim prompt = JsonUtil.GetString(args, "prompt")
            If String.IsNullOrWhiteSpace(prompt) Then Return (False, "prompt is required.")
            Dim vo = Videos.Options
            Dim seconds = Math.Clamp(JsonUtil.GetInt(args, "seconds", vo.DefaultSeconds), 1, Math.Max(1, vo.MaxSeconds))
            Dim ratio = Videos.NormalizeRatio(JsonUtil.GetString(args, "ratio"))
            ' User accounts pay per second of video; the price of the first model is checked before anything is made.
            Dim estimate = Videos.PricePerSecondMicros(Videos.Models().FirstOrDefault()) * seconds
            If estimate > 0 AndAlso Not isAdmin AndAlso Accounts IsNot Nothing Then
                Dim acc = Await Accounts.GetAccountAsync(username).ConfigureAwait(False)
                If acc IsNot Nothing AndAlso acc.Charged AndAlso acc.BalanceMicros < estimate Then
                    Return (False, $"The user's balance ({TextUtil.FormatMicros(acc.BalanceMicros)}) does not cover a {seconds}-second video ({TextUtil.FormatMicros(estimate)}). Tell them to ask an administrator to add money.")
                End If
            End If
            Dim made = Await Videos.GenerateAsync(prompt, seconds, ratio, ct,
                Function(model, stage) emit("status", New With {.text = $"Creating the video with {model}…" & If(stage = "starting", "", " " & stage)})).ConfigureAwait(False)
            Await emit("status", New With {.text = "Saving the video…"}).ConfigureAwait(False)
            Dim ext = If(If(made.MimeType, "").Contains("webm"), ".webm", If(If(made.MimeType, "").Contains("quicktime"), ".mov", ".mp4"))
            Dim u = Await _uploads.SaveAsync($"video-{DateTime.UtcNow:yyyyMMdd-HHmmss}{ext}", made.Data, username, CLng(Math.Max(10, vo.MaxVideoMb)) * 1024L * 1024L).ConfigureAwait(False)
            Dim att As New ChatAttachmentRef With {.UploadId = u.Id, .Name = u.Name, .Kind = "video", .SizeBytes = u.SizeBytes,
                                                   .Summary = TextUtil.Truncate(prompt, 240, "…") & $" · {made.Seconds} s · {made.Model}"}
            answer.Attachments.Add(att)
            Dim price = Videos.PricePerSecondMicros(made.Model) * made.Seconds
            If price > 0 AndAlso Not isAdmin AndAlso Accounts IsNot Nothing Then
                Dim after = Await Accounts.ChargeAsync(username, price, "Video", made.Model, Nothing, Nothing, Nothing, $"Chat video ({made.Seconds} s): " & TextUtil.Truncate(prompt, 70, "…")).ConfigureAwait(False)
                If after.HasValue Then answer.CostMicros = If(answer.CostMicros, 0L) + Await Accounts.WithSurchargeAsync(username, price).ConfigureAwait(False)
            End If
            Await emit("video", New With {.messageId = answer.Id, .attachment = att}).ConfigureAwait(False)
            Dim note = If(made.Failures.Count = 0, "", $" (the first choice was unavailable: {String.Join("; ", made.Failures)})")
            Return (True, $"The {made.Seconds}-second {ratio} video was made by {made.Model}{note} and is shown to the user with your answer, with a player. " &
                          "Do not add a link to it; describe it briefly if useful.")
        End Function
    End Class

End Namespace
