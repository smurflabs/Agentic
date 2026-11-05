Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Text.Json
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>One conversation read from an export file, ready to become a chat.</summary>
    Public NotInheritable Class ImportedChat
        Public Property Title As String
        Public Property Model As String
        Public Property Messages As New List(Of (Role As String, Content As String, CreatedUtc As DateTime?))
    End Class

    ''' <summary>
    ''' Chats brought in from a file (/import in the Chat tab): this app's own JSON export (one chat), ChatGPT's export
    ''' (conversations.json: one conversation or the whole list) and Claude.ai's export (the same). Only the text of the user's
    ''' messages and the answers comes along; files, tool calls and settings stay behind.
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        Public Const MaxImportChats As Integer = 50
        Public Const MaxImportMessages As Integer = 2000
        Private Const MaxImportChars As Integer = 200000

        ''' <summary>The conversations in an export file; an empty list when it holds none this app knows.</summary>
        Public Shared Function ParseImport(json As String) As List(Of ImportedChat)
            Dim result As New List(Of ImportedChat)
            Dim root As JsonElement
            If Not JsonUtil.TryParseElement(If(json, ""), root) Then Return result
            Dim items = If(root.ValueKind = JsonValueKind.Array, root.EnumerateArray().ToList(), New List(Of JsonElement) From {root})
            For Each item In items.Take(MaxImportChats)
                If item.ValueKind <> JsonValueKind.Object Then Continue For
                Dim chat = If(ChatGptChat(item), If(ClaudeAiChat(item), OwnChat(item)))
                If chat IsNot Nothing AndAlso chat.Messages.Count > 0 Then result.Add(chat)
            Next
            Return result
        End Function

        Private Shared Function Prop(o As JsonElement, ParamArray names As String()) As JsonElement
            Dim v As JsonElement
            For Each n In names
                If o.ValueKind = JsonValueKind.Object AndAlso o.TryGetProperty(n, v) AndAlso v.ValueKind <> JsonValueKind.Null Then Return v
            Next
            Return Nothing
        End Function

        Private Shared Function JText(o As JsonElement, ParamArray names As String()) As String
            Dim v = Prop(o, names)
            Return If(v.ValueKind = JsonValueKind.String, v.GetString(), Nothing)
        End Function

        ''' <summary>An ISO date or seconds since 1970 (ChatGPT's create_time).</summary>
        Private Shared Function TimeOf(o As JsonElement, ParamArray names As String()) As DateTime?
            Dim v = Prop(o, names)
            If v.ValueKind = JsonValueKind.Number Then
                Dim secs As Double
                If v.TryGetDouble(secs) AndAlso secs > 0 AndAlso secs < 32503680000 Then Return DateTime.UnixEpoch.AddSeconds(secs)
            ElseIf v.ValueKind = JsonValueKind.String Then
                Dim d As DateTime
                If DateTime.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, d) Then Return d
            End If
            Return Nothing
        End Function

        Private Shared Sub AddMessage(chat As ImportedChat, role As String, content As String, created As DateTime?)
            If chat.Messages.Count >= MaxImportMessages OrElse String.IsNullOrWhiteSpace(content) Then Return
            Dim r = If(role, "").ToLowerInvariant()
            r = If(r = "human" OrElse r = "user", "user", If(r = "assistant" OrElse r = "model" OrElse r = "ai", "assistant", Nothing))
            If r Is Nothing Then Return
            chat.Messages.Add((r, TextUtil.Truncate(content, MaxImportChars, "…"), created))
        End Sub

        ''' <summary>This app's export: {Title, Model, Messages: [{Role, Content, CreatedUtc}]} (also {messages: [{role, content}]}).</summary>
        Private Shared Function OwnChat(o As JsonElement) As ImportedChat
            Dim list = Prop(o, "Messages", "messages")
            If list.ValueKind <> JsonValueKind.Array Then Return Nothing
            Dim chat As New ImportedChat With {.Title = JText(o, "Title", "title"), .Model = JText(o, "Model", "model")}
            For Each m In list.EnumerateArray()
                AddMessage(chat, JText(m, "Role", "role"), JText(m, "Content", "content"), TimeOf(m, "CreatedUtc", "createdUtc", "created_at"))
            Next
            Return chat
        End Function

        ''' <summary>A ChatGPT conversation: a tree of messages (mapping) followed from current_node back to the start.</summary>
        Private Shared Function ChatGptChat(o As JsonElement) As ImportedChat
            Dim mapping = Prop(o, "mapping")
            If mapping.ValueKind <> JsonValueKind.Object Then Return Nothing
            Dim nodes As New Dictionary(Of String, JsonElement)(StringComparer.Ordinal)
            For Each p In mapping.EnumerateObject()
                nodes(p.Name) = p.Value
            Next
            Dim current = JText(o, "current_node")
            ' Without current_node: the newest leaf.
            If current Is Nothing OrElse Not nodes.ContainsKey(current) Then
                current = nodes.Where(Function(kv) Prop(kv.Value, "children").ValueKind <> JsonValueKind.Array OrElse Prop(kv.Value, "children").GetArrayLength() = 0).
                                Select(Function(kv) kv.Key).LastOrDefault()
            End If
            Dim path As New List(Of JsonElement)
            Dim seen As New HashSet(Of String)(StringComparer.Ordinal)
            While current IsNot Nothing AndAlso nodes.ContainsKey(current) AndAlso seen.Add(current)
                path.Add(nodes(current))
                current = JText(nodes(current), "parent")
            End While
            path.Reverse()
            Dim chat As New ImportedChat With {.Title = JText(o, "title"), .Model = Nothing}
            For Each n In path
                Dim msg = Prop(n, "message")
                If msg.ValueKind <> JsonValueKind.Object Then Continue For
                Dim parts = Prop(Prop(msg, "content"), "parts")
                If parts.ValueKind <> JsonValueKind.Array Then Continue For
                Dim body = String.Join(vbLf & vbLf, parts.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString()).Where(Function(x) Not String.IsNullOrWhiteSpace(x)))
                AddMessage(chat, JText(Prop(msg, "author"), "role"), body, TimeOf(msg, "create_time"))
            Next
            Return chat
        End Function

        ''' <summary>A Claude.ai conversation: {name, chat_messages: [{sender: human | assistant, text, created_at}]}.</summary>
        Private Shared Function ClaudeAiChat(o As JsonElement) As ImportedChat
            Dim list = Prop(o, "chat_messages")
            If list.ValueKind <> JsonValueKind.Array Then Return Nothing
            Dim chat As New ImportedChat With {.Title = JText(o, "name"), .Model = Nothing}
            For Each m In list.EnumerateArray()
                Dim body = JText(m, "text")
                If String.IsNullOrWhiteSpace(body) Then
                    Dim content = Prop(m, "content")
                    If content.ValueKind = JsonValueKind.Array Then
                        body = String.Join(vbLf & vbLf, content.EnumerateArray().Select(Function(c) JText(c, "text")).Where(Function(x) Not String.IsNullOrWhiteSpace(x)))
                    End If
                End If
                AddMessage(chat, JText(m, "sender"), body, TimeOf(m, "created_at"))
            Next
            Return chat
        End Function

        ''' <summary>Makes a chat from an imported conversation (its messages keep their order and, when known, their times).</summary>
        Public Async Function ImportAsync(username As String, imported As ImportedChat, model As String) As Task(Of ChatConversation)
            Dim title = TextUtil.Truncate(If(String.IsNullOrWhiteSpace(imported.Title), "Imported chat", imported.Title.Trim()), 100, "…")
            Dim chat = Await CreateAsync(username, title, model, New ChatSettings()).ConfigureAwait(False)
            Dim last = DateTime.MinValue
            Dim start = DateTime.UtcNow.AddMilliseconds(-imported.Messages.Count)
            For i = 0 To imported.Messages.Count - 1
                Dim m = imported.Messages(i)
                ' Times stay in order (a message never before the one above it), so the chat reads as it was written.
                Dim at = If(m.CreatedUtc, start.AddMilliseconds(i))
                If at <= last Then at = last.AddMilliseconds(1)
                last = at
                Dim rec As New ChatMessageRecord With {.Id = Ids.NewId("msg"), .ChatId = chat.Id, .Role = m.Role, .Content = m.Content, .CreatedUtc = at,
                                                       .Model = If(m.Role = "assistant", If(imported.Model, "imported"), Nothing), .FinishReason = If(m.Role = "assistant", "stop", Nothing)}
                Await SaveMessageAsync(rec).ConfigureAwait(False)
                chat.Messages.Add(rec)
            Next
            Await _db.ExecuteAsync("UPDATE Chats SET UpdatedUtc=@t WHERE Id=@id", ("@t", CObj(Db.Ts(If(last = DateTime.MinValue, DateTime.UtcNow, last)))), ("@id", chat.Id)).ConfigureAwait(False)
            Return chat
        End Function
    End Class

End Namespace
