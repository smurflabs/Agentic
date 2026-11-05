Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' MCP tools on the user's computer (the desktop app's, VS Code's, the CLI's - several servers can bring hundreds) are not
    ''' all sent with every request: past a budget the model sees their names and loads the ones it needs with load_tools, like
    ''' Claude Code's tool search. Calling a waiting tool by its exact name loads it too. What a chat loaded stays loaded.
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        Public Const LoadToolsTool As String = "load_tools"
        ''' <summary>Up to this many MCP tools from the user's computer are sent as they are.</summary>
        Friend Const InlineClientMcpTools As Integer = 24
        ''' <summary>... and only while their definitions stay under this many characters.</summary>
        Friend Const InlineClientMcpChars As Integer = 40000

        ''' <summary>Tools that may wait: a local MCP server's (mcp__server__tool) or another VS Code extension's (vscode_tool__name).</summary>
        Friend Shared Function IsDeferrableClientTool(name As String) As Boolean
            Return name IsNot Nothing AndAlso (name.StartsWith("mcp__", StringComparison.Ordinal) OrElse name.StartsWith("vscode_tool__", StringComparison.Ordinal))
        End Function

        ''' <summary>Whether this many MCP tools (their definitions) wait instead of going with every request.</summary>
        Friend Shared Function ShouldDeferClientTools(defs As IEnumerable(Of JsonObject)) As Boolean
            Dim list = defs.ToList()
            Return list.Count > InlineClientMcpTools OrElse list.Sum(Function(d) d.ToJsonString().Length) > InlineClientMcpChars
        End Function

        Private Shared Function LoadToolsBinding() As ToolBinding
            Return New ToolBinding With {.Name = LoadToolsTool, .Server = "toolsearch", .Tool = LoadToolsTool, .Risk = "read", .Definition = FunctionDef(LoadToolsTool,
                "Load tools of the MCP servers on the user's computer that are listed as waiting (in your instructions), so you can call them: " &
                "give their exact names, a server's name with __* (mcp__github__*) for all of its tools, or words to search their names and descriptions. " &
                "They can be called from your next step on.",
                "{""type"":""object"",""properties"":{""names"":{""type"":""array"",""items"":{""type"":""string""},""description"":""Exact tool names, or mcp__server__* for a server's tools""}," &
                """query"":{""type"":""string"",""description"":""Words to find tools by (e.g. 'create issue', 'screenshot')""}}}")}
        End Function

        ''' <summary>The waiting tools, by server, for the instructions.</summary>
        Private Shared Function DeferredCatalog(deferred As IEnumerable(Of ToolBinding)) As String
            Dim sb As New StringBuilder()
            sb.Append(vbLf & vbLf & $"More tools on the user's computer wait to be loaded (call {LoadToolsTool} with their names or a search first; calling one by its exact name also works):" & vbLf)
            For Each g In deferred.GroupBy(Function(t) ServerOfClientTool(t.Name)).OrderBy(Function(x) x.Key, StringComparer.Ordinal)
                Dim names = g.Select(Function(t) t.Name).OrderBy(Function(n) n, StringComparer.Ordinal).ToList()
                sb.Append("- ").Append(g.Key).Append(": ").Append(String.Join(", ", names.Take(80).Select(Function(n) ShortToolName(n))))
                If names.Count > 80 Then sb.Append($", … ({names.Count - 80} more)")
                sb.Append(vbLf)
            Next
            Return sb.ToString()
        End Function

        ''' <summary>mcp__github__create_issue → mcp__github; vscode_tool__x → vscode_tool.</summary>
        Friend Shared Function ServerOfClientTool(name As String) As String
            If name.StartsWith("mcp__", StringComparison.Ordinal) Then
                Dim i = name.IndexOf("__", 5, StringComparison.Ordinal)
                Return If(i > 0, name.Substring(0, i), name)
            End If
            Return "vscode_tool"
        End Function

        Private Shared Function ShortToolName(name As String) As String
            Dim server = ServerOfClientTool(name)
            Return If(name.Length > server.Length + 2 AndAlso name.StartsWith(server & "__", StringComparison.Ordinal), name.Substring(server.Length + 2), name)
        End Function

        ''' <summary>Whether this account may use the server in chat: every server but other users' own ones.</summary>
        Public Function MayUseServer(serverId As String, username As String) As Boolean
            Dim o = _mcp?.GetServerOptions(serverId)
            If o Is Nothing OrElse o.Owner Is Nothing OrElse o.Owner = "*" Then Return True
            Return username IsNot Nothing AndAlso String.Equals(o.Owner, username, StringComparison.OrdinalIgnoreCase)
        End Function

        ''' <summary>Remembers the MCP tools this chat loaded, so its next answers have them without loading them again.</summary>
        Private Async Function RememberLoadedClientToolsAsync(chat As ChatConversation, settings As ChatSettings, names As IEnumerable(Of String)) As Task
            If settings.LoadedClientTools Is Nothing Then settings.LoadedClientTools = New List(Of String)
            Dim before = settings.LoadedClientTools.Count
            For Each n In names
                If Not settings.LoadedClientTools.Contains(n) Then settings.LoadedClientTools.Add(n)
            Next
            If settings.LoadedClientTools.Count = before Then Return
            ' The newest 200 at most.
            If settings.LoadedClientTools.Count > 200 Then settings.LoadedClientTools.RemoveRange(0, settings.LoadedClientTools.Count - 200)
            Await _db.ExecuteAsync("UPDATE Chats SET SettingsJson=@s WHERE Id=@id", ("@s", CObj(JsonUtil.ToJson(settings))), ("@id", chat.Id)).ConfigureAwait(False)
        End Function

        ''' <summary>The waiting tools a load_tools call asks for: exact names, server__* and a word search (best 8).</summary>
        Friend Shared Function PickDeferredTools(argsJson As String, waiting As IEnumerable(Of (Name As String, Description As String))) As List(Of String)
            Dim all = waiting.ToList()
            Dim picked As New List(Of String)
            Dim names As New List(Of String)
            Dim query As String = Nothing
            Try
                Using doc = JsonDocument.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
                    Dim el As JsonElement
                    If doc.RootElement.TryGetProperty("names", el) Then
                        If el.ValueKind = JsonValueKind.Array Then
                            names.AddRange(el.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString()))
                        ElseIf el.ValueKind = JsonValueKind.String Then
                            names.AddRange(el.GetString().Split({","c, " "c}, StringSplitOptions.RemoveEmptyEntries))
                        End If
                    End If
                    If doc.RootElement.TryGetProperty("query", el) AndAlso el.ValueKind = JsonValueKind.String Then query = el.GetString()
                End Using
            Catch ex As JsonException
            End Try
            For Each n In names.Select(Function(x) x.Trim())
                If n.EndsWith("*", StringComparison.Ordinal) Then
                    Dim prefix = n.TrimEnd("*"c)
                    picked.AddRange(all.Where(Function(t) t.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(Function(t) t.Name))
                Else
                    ' The exact name, or the tool's own name without its server (create_issue).
                    Dim hit = all.FirstOrDefault(Function(t) t.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    If hit.Name Is Nothing Then hit = all.FirstOrDefault(Function(t) ShortToolName(t.Name).Equals(n, StringComparison.OrdinalIgnoreCase))
                    If hit.Name IsNot Nothing Then picked.Add(hit.Name)
                End If
            Next
            If Not String.IsNullOrWhiteSpace(query) Then
                Dim words = query.ToLowerInvariant().Split({" "c, ","c, "_"c, "-"c}, StringSplitOptions.RemoveEmptyEntries).Where(Function(w) w.Length > 1).ToList()
                Dim scored = all.Select(Function(t)
                                            Dim hay = (t.Name.Replace("_"c, " "c) & " " & If(t.Description, "")).ToLowerInvariant()
                                            Dim inName = t.Name.ToLowerInvariant()
                                            Dim score = words.Sum(Function(w) If(inName.Contains(w), 3, 0) + If(hay.Contains(w), 1, 0))
                                            Return (t.Name, score)
                                        End Function).Where(Function(x) x.score > 0).OrderByDescending(Function(x) x.score).Take(8)
                picked.AddRange(scored.Select(Function(x) x.Name))
            End If
            Return picked.Distinct(StringComparer.Ordinal).Take(64).ToList()
        End Function

    End Class

End Namespace
