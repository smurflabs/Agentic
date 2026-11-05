Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A past message the user typed (Ctrl+R in the message box): its text, its chat and when.</summary>
    Public Class PromptHistoryItem
        Public Property Text As String
        Public Property ChatId As String
        Public Property ChatTitle As String
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>
    ''' What the message box and the conversation view need from the server beyond sending: a shell command the user ran
    ''' with ! (kept in the chat so the model sees it with the next message), the user's earlier prompts across chats
    ''' (Ctrl+R) and an earlier version of an answer, read without switching to it (compare side by side).
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        ''' <summary>What the model hears when the user accepted only some of an edit's changes (per-change review).</summary>
        Friend Const PartialEditNote As String = "Note: the user accepted only some of the changes in this edit and left the rest out; the file now has exactly what they accepted. " &
                                                 "Do not redo the changes they left out unless they ask for them."

        ''' <summary>Marks a user message that holds a shell command the user ran (! in the message box) and its output.</summary>
        Public Const ShellFinish As String = "shell"
        Private Const ShellOutputChars As Integer = 30000

        ''' <summary>
        ''' The message a ! command becomes: Claude Code's bash-input / bash-stdout form, which models know as "the user ran
        ''' this themselves". Long output keeps its start and end.
        ''' </summary>
        Friend Shared Function ShellContent(command As String, output As String, exitCode As Integer?, place As String) As String
            Dim out = If(output, "")
            If out.Length > ShellOutputChars Then
                out = out.Substring(0, ShellOutputChars \ 2) & vbLf & $"… {out.Length - ShellOutputChars:N0} characters left out …" & vbLf & out.Substring(out.Length - ShellOutputChars \ 2)
            End If
            Dim sb As New StringBuilder()
            sb.Append("<bash-input>").Append(If(command, "").Trim()).Append("</bash-input>").Append(vbLf)
            sb.Append("<bash-stdout>").Append(out.TrimEnd()).Append("</bash-stdout>")
            If exitCode.HasValue AndAlso exitCode.Value <> 0 Then sb.Append(vbLf).Append("<bash-exit-code>").Append(exitCode.Value).Append("</bash-exit-code>")
            If Not String.IsNullOrWhiteSpace(place) Then sb.Append(vbLf).Append("<bash-where>").Append(TextUtil.Truncate(place.Trim(), 120)).Append("</bash-where>")
            Return sb.ToString()
        End Function

        ''' <summary>Saves a command the user ran (! in the message box) and its output as a message of the chat; the model sees it next time.</summary>
        Public Async Function SaveShellAsync(username As String, chatId As String, command As String, output As String, exitCode As Integer?, place As String) As Task(Of ChatMessageRecord)
            If String.IsNullOrWhiteSpace(command) Then Throw New ArgumentException("command is required.")
            Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            If Live.IsLive(chatId) Then Throw New InvalidOperationException("Wait until the answer is finished.")
            Dim m As New ChatMessageRecord With {
                .Id = Ids.NewId("msg"), .ChatId = chat.Id, .Role = "user", .Content = ShellContent(command, output, exitCode, place),
                .Model = chat.Model, .CreatedUtc = DateTime.UtcNow, .FinishReason = ShellFinish}
            Await SaveMessageAsync(m).ConfigureAwait(False)
            Await _db.ExecuteAsync("UPDATE Chats SET UpdatedUtc=@n WHERE Id=@id", ("@n", CObj(Db.Ts(DateTime.UtcNow))), ("@id", chat.Id)).ConfigureAwait(False)
            Live.Publish(username, chat.Id, "changed", New With {.shell = True})
            Return m
        End Function

        ''' <summary>
        ''' The user's own earlier messages across all chats, newest first, each text once (Ctrl+R): typed questions only, not
        ''' automatic goal or monitor turns or ! commands.
        ''' </summary>
        Public Async Function PromptHistoryAsync(username As String, query As String, limit As Integer) As Task(Of List(Of PromptHistoryItem))
            Dim q = If(query, "").Trim()
            Dim pattern = "%" & q.Replace("\", "\\").Replace("%", "\%").Replace("_", "\_") & "%"
            Dim rows = Await _db.QueryAsync(
                "SELECT m.Content AS Content, m.ChatId AS ChatId, m.CreatedUtc AS CreatedUtc, c.Title AS Title FROM ChatMessages m JOIN Chats c ON c.Id = m.ChatId " &
                "WHERE c.Username=@u AND m.Role='user' AND (m.FinishReason IS NULL OR m.FinishReason NOT IN ('auto_goal', 'auto_monitor', 'shell')) " &
                "AND m.Content <> '' AND (@q = '' OR m.Content LIKE @like ESCAPE '\') ORDER BY m.CreatedUtc DESC LIMIT 600",
                Function(r) New PromptHistoryItem With {.Text = Db.Str(r, "Content"), .ChatId = Db.Str(r, "ChatId"), .ChatTitle = Db.Str(r, "Title"), .CreatedUtc = Db.Dt(r, "CreatedUtc")},
                ("@u", CObj(username)), ("@q", q), ("@like", pattern)).ConfigureAwait(False)
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            Dim result As New List(Of PromptHistoryItem)
            For Each r In rows
                Dim key = r.Text.Trim()
                If key.Length = 0 OrElse key.StartsWith("⏰ Heartbeat check.", StringComparison.Ordinal) OrElse Not seen.Add(key) Then Continue For
                r.Text = TextUtil.Truncate(r.Text, 4000)
                result.Add(r)
                If result.Count >= Math.Clamp(limit, 1, 200) Then Exit For
            Next
            Return result
        End Function

        ''' <summary>An earlier version at a point of the chat (its messages from that point on), read without switching to it.</summary>
        Public Async Function ReadVersionAsync(username As String, chatId As String, branchId As String) As Task(Of List(Of ChatMessageRecord))
            Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            Dim target = (Await BranchesOfAsync(chatId).ConfigureAwait(False)).FirstOrDefault(Function(b) b.Id = branchId)
            If target Is Nothing Then Throw New KeyNotFoundException("That version does not exist.")
            Dim payload = JsonUtil.FromJson(Of BranchPayload)(target.PayloadJson)
            Return If(payload?.Messages, New List(Of ChatMessageRecord))
        End Function

    End Class

End Namespace
