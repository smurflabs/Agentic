Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' Research notes: when an answer researched (it or its subagents searched the web or read pages, or it was a /research
    ''' message), the harness keeps what it found in a Markdown file - the model does not have to remember to. One file per
    ''' chat, rebuilt from every research answer in it: the question, the findings (the answer, with its [n] citations), what
    ''' its subagents reported, the sources, excerpts of the pages read and the searches. It is attached to the answer (a
    ''' download in every app) and, when the chat works on a computer with file tools (the CLI, VS Code, the desktop app with
    ''' a folder), written to research/&lt;chat&gt;.md in that workspace - also while a long research answer is still working,
    ''' so a stop, a failure or a crash keeps what was found.
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        ''' <summary>Tools that look things up on the web, by their name (or an MCP tool's own name after its server's).</summary>
        Private Shared ReadOnly ResearchToolNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "search_web", ReadPageTool, NewsService.ToolName, "WebSearch", "WebFetch", "web_search", "web_fetch", "fetch_url", "fetch",
            "brave_web_search", "tavily_search", "tavily_extract", "firecrawl_search", "firecrawl_scrape", "read_web_page"}
        Private Shared ReadOnly SearchToolNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "search_web", "WebSearch", "web_search", "brave_web_search", "tavily_search", "firecrawl_search", NewsService.ToolName}
        Private Shared ReadOnly PageToolNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            ReadPageTool, "WebFetch", "web_fetch", "fetch_url", "fetch", "tavily_extract", "firecrawl_scrape", "read_web_page"}
        ''' <summary>Research answers kept in one notes file (the latest ones).</summary>
        Friend Const MaxResearchSections As Integer = 40
        ''' <summary>Pages quoted per answer, and how much of each.</summary>
        Private Const MaxPageExcerpts As Integer = 12
        Private Const PageExcerptChars As Integer = 700
        ''' <summary>A subagent's report in the notes, at most.</summary>
        Private Const SubagentReportChars As Integer = 8000
        ''' <summary>While an answer researches, its notes are written again after this many new lookups, or this long after the last.</summary>
        Friend Const CheckpointEveryLookups As Integer = 8
        Friend Shared ReadOnly CheckpointEvery As TimeSpan = TimeSpan.FromMinutes(4)
        ''' <summary>FinishReason of an answer's snapshot written while it still works (never saved on a message).</summary>
        Friend Const InProgressFinish As String = "in_progress"
        Private Const ResearchNoteMark As String = "<!-- agentic-research-notes -->"
        ''' <summary>For the instructions when the chat can research.</summary>
        Friend Const ResearchNotesNote As String =
            "Research notes are kept for you: after an answer in which you (or your subagents) searched the web or read pages, the app saves " &
            "the question, your answer, your subagents' reports and the sources to the chat's research notes (a Markdown file attached to the " &
            "answer, and research/<chat>.md in the user's workspace when there is one). Do not write your own notes file unless the user asks; " &
            "write your findings in the answer, with each fact cited [n] to its source."

        ''' <summary>The tool's own name: mcp__fetch__fetch → fetch.</summary>
        Private Shared Function BareToolName(name As String) As String
            If String.IsNullOrWhiteSpace(name) Then Return ""
            Dim i = name.LastIndexOf("__", StringComparison.Ordinal)
            Return If(i >= 0 AndAlso i + 2 < name.Length, name.Substring(i + 2), name)
        End Function

        ''' <summary>Whether a tool call looks things up on the web: search_web, read_page, WebFetch, mcp__fetch__fetch, …</summary>
        Friend Shared Function IsResearchTool(name As String) As Boolean
            If String.IsNullOrWhiteSpace(name) Then Return False
            Return ResearchToolNames.Contains(name) OrElse ResearchToolNames.Contains(BareToolName(name))
        End Function

        Private Shared Function IsSearchTool(name As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(name) AndAlso (SearchToolNames.Contains(name) OrElse SearchToolNames.Contains(BareToolName(name)))
        End Function

        Private Shared Function IsPageTool(name As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(name) AndAlso (PageToolNames.Contains(name) OrElse PageToolNames.Contains(BareToolName(name)))
        End Function

        ''' <summary>A subagent call whose subagent looked things up on the web.</summary>
        Private Shared Function IsResearchSubagent(tc As ChatToolCall) As Boolean
            Return tc?.Subagent?.Items IsNot Nothing AndAlso tc.Subagent.Items.Any(Function(it) it.Kind = "tool" AndAlso IsResearchTool(it.Name))
        End Function

        ''' <summary>The searches an answer ran (its own and its subagents'), in order, each once.</summary>
        Friend Shared Function ResearchQueries(m As ChatMessageRecord) As List(Of String)
            Dim list As New List(Of String)
            Dim seen As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim add = Sub(q As String)
                          q = Regex.Replace(If(q, ""), "\s+", " ").Trim()
                          If q.Length > 0 AndAlso seen.Add(q) Then list.Add(TextUtil.Truncate(q, 200))
                      End Sub
            For Each tc In If(m?.ToolCalls, New List(Of ChatToolCall))
                If IsSearchTool(tc.Name) Then
                    Dim args As JsonElement
                    If JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(tc.Arguments), "{}", tc.Arguments), args) Then add(If(JsonUtil.GetString(args, "query"), JsonUtil.GetString(args, "q")))
                End If
                For Each it In If(tc.Subagent?.Items, New List(Of SubagentItem))
                    If it.Kind = "tool" AndAlso IsSearchTool(it.Name) Then add(it.Target)
                Next
            Next
            Return list
        End Function

        ''' <summary>How many lookups an answer made so far (its own and its subagents'): when to write its notes again.</summary>
        Friend Shared Function ResearchLookups(m As ChatMessageRecord) As Integer
            Dim n = 0
            For Each tc In If(m?.ToolCalls, New List(Of ChatToolCall))
                If IsResearchTool(tc.Name) Then n += 1
                If tc.Subagent?.Items IsNot Nothing Then n += tc.Subagent.Items.Where(Function(it) it.Kind = "tool" AndAlso IsResearchTool(it.Name)).Count()
            Next
            Return n
        End Function

        ''' <summary>
        ''' Whether an answer researched: it or one of its subagents called a web lookup tool. A chat's automatic search before
        ''' the answer (web search switched on) alone does not count: that is every message, not research.
        ''' </summary>
        Friend Shared Function IsResearchAnswer(m As ChatMessageRecord) As Boolean
            If m Is Nothing OrElse m.Role <> "assistant" Then Return False
            Return If(m.ToolCalls, New List(Of ChatToolCall)).Any(Function(tc) IsResearchTool(tc.Name) OrElse IsResearchSubagent(tc))
        End Function

        ''' <summary>An answer worth a section: it researched and has findings, sources or reports - even when it was stopped or failed.</summary>
        Private Shared Function HasFindings(m As ChatMessageRecord) As Boolean
            Return IsResearchAnswer(m) AndAlso (Not String.IsNullOrWhiteSpace(m.Content) OrElse m.Sources.Count > 0 OrElse
                                                m.ToolCalls.Any(Function(tc) IsResearchSubagent(tc) AndAlso Not String.IsNullOrWhiteSpace(tc.Result)))
        End Function

        ''' <summary>"comparing-python-web-frameworks" from a title: lower case letters, digits and dashes, at most 60 characters.</summary>
        Friend Shared Function NotesSlug(title As String) As String
            Dim s = New String(If(title, "").ToLowerInvariant().Select(Function(c) If(Char.IsLetterOrDigit(c) AndAlso c < ChrW(128), c, "-"c)).ToArray())
            s = Regex.Replace(s, "-{2,}", "-").Trim("-"c)
            If s.Length > 60 Then
                Dim cut = s.LastIndexOf("-"c, 60)
                s = s.Substring(0, If(cut > 20, cut, 60)).TrimEnd("-"c)
            End If
            Return If(s.Length = 0, "research", s)
        End Function

        ''' <summary>Text with its headings moved under a section's (by 2: # → ###), code blocks untouched.</summary>
        Private Shared Function DemoteHeadings(text As String, Optional by As Integer = 2) As String
            Dim sb As New StringBuilder()
            Dim inFence = False
            Dim prefix = New String("#"c, by)
            For Each line In If(text, "").Replace(vbCrLf, vbLf).Split(ChrW(10))
                If line.TrimStart().StartsWith("```", StringComparison.Ordinal) Then inFence = Not inFence
                If Not inFence AndAlso Regex.IsMatch(line, "^#{1,4}\s") Then line = prefix & line
                sb.Append(line).Append(vbLf)
            Next
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>A link's text: one line, no brackets.</summary>
        Private Shared Function LinkText(text As String) As String
            Return Regex.Replace(If(text, ""), "[\[\]\s]+", " ").Trim()
        End Function

        ''' <summary>
        ''' The notes file: a title, then one section per research answer (oldest first): the question, when and which model,
        ''' the findings, its subagents' reports, the sources (numbered as the answer cites them), excerpts of the pages read and
        ''' the searches. A stopped, failed or still running answer says so.
        ''' </summary>
        Friend Shared Function BuildResearchNotes(chatTitle As String, chatId As String, exchanges As IList(Of (Question As ChatMessageRecord, Answer As ChatMessageRecord))) As String
            Dim sb As New StringBuilder()
            Dim title = If(String.IsNullOrWhiteSpace(chatTitle) OrElse chatTitle = "New chat", "Research notes", chatTitle.Trim())
            sb.Append("# Research: ").Append(title).Append(vbLf).Append(vbLf)
            sb.Append(ResearchNoteMark).Append(vbLf)
            sb.Append($"_Findings from the Agentic chat ""{title}"" ({chatId}), saved after each answer that researched. ")
            sb.Append("Agentic rewrites this file as the chat goes on: keep your own notes in another file._").Append(vbLf)
            Dim n = 0
            For Each ex In exchanges.Skip(Math.Max(0, exchanges.Count - MaxResearchSections))
                n += 1
                Dim a = ex.Answer
                Dim question = If(ex.Question?.Content, "").Trim()
                sb.Append(vbLf).Append("## ").Append(n).Append(". ").Append(TitleFrom(If(question.Length = 0, "Research", question))).Append(vbLf).Append(vbLf)
                sb.Append("_").Append(a.CreatedUtc.ToString("yyyy-MM-dd HH:mm", Globalization.CultureInfo.InvariantCulture)).Append(" UTC")
                Dim model = If(a.RoutedModel, a.Model)
                If Not String.IsNullOrWhiteSpace(model) Then sb.Append(" · ").Append(model)
                Dim web = a.Sources.Where(Function(s) Not s.Internal).Count()
                If web > 0 Then sb.Append(" · ").Append(web).Append(If(web = 1, " source", " sources"))
                sb.Append("_").Append(vbLf).Append(vbLf)
                If a.FinishReason = InProgressFinish Then
                    sb.Append("**Still researching:** what was found so far.").Append(vbLf).Append(vbLf)
                ElseIf a.Error = "Stopped." Then
                    sb.Append("**Stopped before it finished:** what was found until then.").Append(vbLf).Append(vbLf)
                ElseIf a.Error IsNot Nothing Then
                    sb.Append("**The answer failed** (").Append(TextUtil.Truncate(a.Error, 200)).Append("): what was found until then.").Append(vbLf).Append(vbLf)
                End If
                If question.Length > 0 Then
                    For Each line In TextUtil.Truncate(question, 1200).Replace(vbCrLf, vbLf).Split(ChrW(10))
                        sb.Append("> ").Append(line).Append(vbLf)
                    Next
                    sb.Append(vbLf)
                End If
                sb.Append("### Findings").Append(vbLf).Append(vbLf)
                sb.Append(If(String.IsNullOrWhiteSpace(a.Content), "_No answer written yet._", DemoteHeadings(a.Content))).Append(vbLf)
                ' What each researching subagent reported: often more than the answer repeats.
                For Each tc In a.ToolCalls.Where(Function(t) IsResearchSubagent(t) AndAlso Not String.IsNullOrWhiteSpace(t.Result))
                    sb.Append(vbLf).Append("### Subagent: ").Append(LinkText(If(tc.Subagent.Label, "subagent"))).Append(vbLf).Append(vbLf)
                    sb.Append(DemoteHeadings(TextUtil.Truncate(tc.Result.Trim(), SubagentReportChars), 3)).Append(vbLf)
                Next
                If a.Sources.Count > 0 Then
                    sb.Append(vbLf).Append("### Sources").Append(vbLf).Append(vbLf)
                    For Each s In a.Sources.OrderBy(Function(x) x.Index)
                        Dim name = LinkText(If(String.IsNullOrWhiteSpace(s.Title), s.Url, s.Title))
                        If s.Internal OrElse String.IsNullOrWhiteSpace(s.Url) Then
                            sb.Append(s.Index).Append(". ").Append(name).Append(If(s.Internal, " (knowledge base)", "")).Append(vbLf)
                        Else
                            sb.Append(s.Index).Append(". [").Append(name).Append("](").Append(s.Url.Replace(" ", "%20").Replace(")", "%29")).Append(")").Append(vbLf)
                        End If
                    Next
                End If
                ' What the pages said, shortly (the model's own reads; a page's first line is its number, title and address).
                Dim pages = a.ToolCalls.Where(Function(t) t.Ok AndAlso IsPageTool(t.Name) AndAlso Not String.IsNullOrWhiteSpace(t.Result)).Take(MaxPageExcerpts).ToList()
                If pages.Count > 0 Then
                    sb.Append(vbLf).Append("### Pages read").Append(vbLf).Append(vbLf)
                    For Each p In pages
                        Dim body = p.Result.Replace(vbCrLf, vbLf).Trim()
                        Dim head As String = Nothing
                        If body.StartsWith("[", StringComparison.Ordinal) Then
                            Dim nl = body.IndexOf(ChrW(10))
                            head = If(nl > 0, body.Substring(0, nl), body)
                            body = If(nl > 0, body.Substring(nl + 1), "")
                        End If
                        If String.IsNullOrWhiteSpace(head) Then
                            Dim args As JsonElement
                            If JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(p.Arguments), "{}", p.Arguments), args) Then head = If(JsonUtil.GetString(args, "url"), JsonUtil.GetString(args, "uri"))
                        End If
                        body = Regex.Replace(Regex.Replace(body, "Cite this page as \[\d+\]\.\s*$", ""), "\s+", " ").Trim()
                        If body.Length = 0 Then Continue For
                        sb.Append("<details><summary>").Append(LinkText(If(head, p.Name)).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")).Append("</summary>").Append(vbLf).Append(vbLf)
                        sb.Append(TextUtil.Truncate(body, PageExcerptChars).Replace("<", "&lt;")).Append(vbLf).Append(vbLf).Append("</details>").Append(vbLf)
                    Next
                End If
                Dim queries = ResearchQueries(a)
                If queries.Count > 0 Then
                    sb.Append(vbLf).Append("### Searches").Append(vbLf).Append(vbLf)
                    For Each q In queries
                        sb.Append("- ").Append(q.Replace("`", "'")).Append(vbLf)
                    Next
                End If
            Next
            Return sb.ToString()
        End Function

        ''' <summary>The name a client tool definition has (OpenAI's {function:{name}} or a bare {name}).</summary>
        Private Shared Function ClientToolName(def As JsonObject) As String
            Return If(If(TryCast(def?("function"), JsonObject), def)?("name")?.ToString(), "")
        End Function

        ''' <summary>
        ''' Whether this chat keeps research notes: on unless the server or the chat (/notes off) turned them off. A client's
        ''' subagent conversation (the CLI's Task) does not: its findings go to the notes of the chat it works for.
        ''' </summary>
        Private Function KeepsResearchNotes(chat As ChatConversation, settings As ChatSettings) As Boolean
            If Not _opt.ResearchNotes OrElse chat Is Nothing Then Return False
            If settings IsNot Nothing AndAlso settings.ResearchNotes.HasValue AndAlso Not settings.ResearchNotes.Value Then Return False
            If _subagentLinks.ContainsKey(chat.Id) OrElse If(chat.Title, "").StartsWith("Subagent: ", StringComparison.Ordinal) Then Return False
            Return True
        End Function

        ''' <summary>The chat's notes with this answer in them: its title, its path in the workspace, the file and how many answers it has.</summary>
        Private Function ComposeResearchNotes(chat As ChatConversation, answer As ChatMessageRecord, settings As ChatSettings) As (Path As String, Notes As String, Sections As Integer)
            Dim exchanges As New List(Of (Question As ChatMessageRecord, Answer As ChatMessageRecord))
            Dim question As ChatMessageRecord = Nothing
            Dim answerQuestion As ChatMessageRecord = Nothing
            For Each m In chat.Messages
                If m.Role = "user" Then question = m : Continue For
                If m.Id = answer.Id Then answerQuestion = question : Continue For
                If HasFindings(m) Then exchanges.Add((question, m))
            Next
            exchanges.Add((If(answerQuestion, chat.Messages.LastOrDefault(Function(m) m.Role = "user")), answer))
            Dim title = If(chat.Title = "New chat", TitleFrom(If(exchanges.Last().Question?.Content, "")), chat.Title)
            Dim folder = If(String.IsNullOrWhiteSpace(_opt.ResearchNotesFolder), "research", _opt.ResearchNotesFolder.Trim().Replace("\"c, "/"c).Trim("/"c))
            Dim notesPath = If(settings?.ResearchNotesPath, folder & "/" & NotesSlug(title) & ".md")
            Return (notesPath, BuildResearchNotes(title, chat.Id, exchanges), exchanges.Count)
        End Function

        ''' <summary>
        ''' Writes the notes to the workspace of the computer the chat works on, through the app's own file tools (the CLI's
        ''' Write, the desktop app's and VS Code's write_file). The harness writes it, so the user is not asked. Returns whether it was written.
        ''' </summary>
        Private Async Function WriteResearchNotesAsync(username As String, chatId As String, notesPath As String, notes As String, client As ChatClientContext,
                                                       deviceRoute As String, emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of Boolean)
            If client Is Nothing OrElse client.Tools.Count = 0 Then Return False
            Dim names = New HashSet(Of String)(client.Tools.Select(AddressOf ClientToolName), StringComparer.Ordinal)
            Dim writeTool = If(names.Contains("Write"), "Write", If(names.Contains("write_file"), "write_file", Nothing))
            If writeTool Is Nothing Then Return False
            Dim readTool = If(writeTool = "Write", If(names.Contains("Read"), "Read", Nothing), If(names.Contains("read_file"), "read_file", Nothing))
            Dim pathKey = If(writeTool = "Write", "file_path", "path")
            Dim callTool = Function(tool As String, args As JsonObject) RunClientToolAsync(username, chatId, Ids.NewId("call"), tool, args.ToJsonString(), deviceRoute, emit, ct, client,
                                                                                           approved:=True, mode:="auto")
            Dim r = Await callTool(writeTool, New JsonObject From {{pathKey, notesPath}, {"content", notes}}).ConfigureAwait(False)
            ' The file is there from an earlier session of the app (or changed since): the apps write a file only after reading it.
            If Not r.Ok AndAlso readTool IsNot Nothing AndAlso Not ct.IsCancellationRequested Then
                Dim read = Await callTool(readTool, New JsonObject From {{pathKey, notesPath}}).ConfigureAwait(False)
                If read.Ok Then r = Await callTool(writeTool, New JsonObject From {{pathKey, notesPath}, {"content", notes}}).ConfigureAwait(False)
            End If
            If Not r.Ok Then _log.Info("Chat", $"Research notes for chat {chatId} were not written to {notesPath}: {TextUtil.Truncate(r.Text, 300)}")
            Return r.Ok
        End Function

        ''' <summary>Keeps how far an answer's notes were written while it works.</summary>
        Friend NotInheritable Class ResearchCheckpoint
            Public Lookups As Integer
            Public AtUtc As DateTime = DateTime.UtcNow
            Public Path As String
        End Class

        ''' <summary>
        ''' While an answer researches: every few lookups (or minutes) its notes so far go to the workspace, so a stop, a failure or
        ''' a crash of a long research keeps what it found. Only where there is a workspace; the attachment comes with the answer.
        ''' </summary>
        Private Async Function CheckpointResearchNotesAsync(username As String, chat As ChatConversation, answer As ChatMessageRecord, continuing As ChatMessageRecord,
                                                            text As String, settings As ChatSettings, client As ChatClientContext, deviceRoute As String,
                                                            planMode As Boolean, state As ResearchCheckpoint, emit As Func(Of String, Object, Task), ct As CancellationToken) As Task
            If planMode OrElse client Is Nothing OrElse client.Tools.Count = 0 OrElse ct.IsCancellationRequested OrElse Not KeepsResearchNotes(chat, settings) Then Return
            Dim snapshot As New ChatMessageRecord With {.Id = answer.Id, .ChatId = chat.Id, .Role = "assistant", .Model = answer.Model, .RoutedModel = answer.RoutedModel,
                .CreatedUtc = If(continuing?.CreatedUtc, answer.CreatedUtc), .FinishReason = InProgressFinish,
                .Content = (If(continuing?.Content, "") & If(String.IsNullOrEmpty(continuing?.Content), "", vbLf & vbLf) & If(text, "")).Trim()}
            snapshot.ToolCalls.AddRange(If(continuing?.ToolCalls, New List(Of ChatToolCall)))
            snapshot.ToolCalls.AddRange(answer.ToolCalls)
            SyncLock answer.Sources
                snapshot.Sources.AddRange(answer.Sources)
            End SyncLock
            Dim lookups = ResearchLookups(snapshot)
            If lookups = 0 OrElse lookups <= state.Lookups Then Return
            If lookups - state.Lookups < CheckpointEveryLookups AndAlso DateTime.UtcNow - state.AtUtc < CheckpointEvery Then Return
            state.Lookups = lookups
            state.AtUtc = DateTime.UtcNow
            Try
                Dim c = ComposeResearchNotes(chat, snapshot, settings)
                If Await WriteResearchNotesAsync(username, chat.Id, c.Path, c.Notes, client, deviceRoute, emit, ct).ConfigureAwait(False) Then state.Path = c.Path
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                _log.Warn("Chat", $"Research notes for chat {chat.Id} were not saved while it worked: {ex.Message}")
            End Try
        End Function

        ''' <summary>
        ''' After an answer that researched (also one stopped or failed on the way): rebuilds the chat's notes, attaches them to the
        ''' answer and writes them to the workspace of the computer the chat works on. Never fails the answer (a note that cannot be
        ''' written is logged). Returns the notes' path in the workspace to remember for the chat (Nothing when not written).
        ''' </summary>
        Private Async Function SaveResearchNotesAsync(username As String, chat As ChatConversation, answer As ChatMessageRecord, settings As ChatSettings,
                                                      client As ChatClientContext, deviceRoute As String, planMode As Boolean,
                                                      emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of String)
            If Not KeepsResearchNotes(chat, settings) Then Return Nothing
            Dim researchMessage = client IsNot Nothing AndAlso client.Research AndAlso answer.Sources.Count > 0
            If Not (HasFindings(answer) OrElse (researchMessage AndAlso Not String.IsNullOrWhiteSpace(answer.Content))) Then Return Nothing
            ' A stopped answer still saves what it found: the user's Stop ended its request, not this.
            Using limit = CancellationTokenSource.CreateLinkedTokenSource(If(ct.IsCancellationRequested, CancellationToken.None, ct))
                limit.CancelAfter(TimeSpan.FromMinutes(2))
                Dim quiet = Async Function(type As String, data As Object) As Task
                                Try
                                    Await emit(type, data).ConfigureAwait(False)
                                Catch ex As Exception When Not TypeOf ex Is OutOfMemoryException
                                End Try
                            End Function
                Dim c = ComposeResearchNotes(chat, answer, settings)
                Dim fileName = IO.Path.GetFileName(c.Path)
                ' In the chat: the notes as they are now, a download on this answer (a continued answer's older copy is replaced).
                Try
                    answer.Attachments.RemoveAll(Function(x) String.Equals(x.Name, fileName, StringComparison.OrdinalIgnoreCase) AndAlso If(x.Summary, "").StartsWith("Research notes", StringComparison.Ordinal))
                    Dim info = Await _uploads.SaveAsync(fileName, Encoding.UTF8.GetBytes(c.Notes), username).ConfigureAwait(False)
                    Dim att As New ChatAttachmentRef With {.UploadId = info.Id, .Name = info.Name, .Kind = info.Kind, .SizeBytes = info.SizeBytes,
                        .Summary = $"Research notes: {c.Sections} research answer{If(c.Sections = 1, "", "s")} in this chat"}
                    answer.Attachments.Add(att)
                    Await quiet("file", New With {.messageId = answer.Id, .attachment = att}).ConfigureAwait(False)
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    _log.Warn("Chat", $"Research notes for chat {chat.Id} were not attached: {ex.Message}")
                End Try
                ' On the user's computer: a plan-only message writes nothing.
                If planMode Then Return Nothing
                Try
                    If client Is Nothing OrElse client.Tools.Count = 0 Then Return Nothing
                    ' After Stop the asking window's request is over: only the app itself (its live link) can still run the write.
                    If ct.IsCancellationRequested AndAlso deviceRoute Is Nothing Then
                        If String.IsNullOrEmpty(client.DeviceId) OrElse Not Live.DeviceOnline(username, client.DeviceId) Then Return Nothing
                        deviceRoute = client.DeviceId
                    End If
                    Await quiet("status", New With {.text = "Saving research notes to " & c.Path & "…"}).ConfigureAwait(False)
                    If Not Await WriteResearchNotesAsync(username, chat.Id, c.Path, c.Notes, client, deviceRoute, quiet, limit.Token).ConfigureAwait(False) Then Return Nothing
                    Await quiet("status", New With {.text = "Research notes saved to " & c.Path}).ConfigureAwait(False)
                    Return c.Path
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex) OrElse limit.IsCancellationRequested
                    _log.Warn("Chat", $"Research notes for chat {chat.Id} were not written: {ex.Message}")
                    Return Nothing
                End Try
            End Using
        End Function

    End Class

End Namespace
