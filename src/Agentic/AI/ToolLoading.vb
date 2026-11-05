Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Configuration
Imports Agentic.Tools
Imports Agentic.Utilities

Namespace AI

    ''' <summary>
    ''' Agents get the full descriptions of their MCP tools only when they need them (ToolLoadingOptions). A task starts with
    ''' its host tools, a core set of MCP tools and, in its system prompt, the names of the rest; load_tools loads more, and
    ''' a loaded tool stays offered for the rest of the task (TaskCheckpoint.LoadedTools).
    ''' </summary>
    Public Module ToolLoading

        ''' <summary>"mcp__code-exec__read_file" → ("code-exec", "read_file"); a name that is not an MCP tool has no server.</summary>
        Public Function SplitMcpName(name As String) As (Server As String, Tool As String)
            If name Is Nothing OrElse Not name.StartsWith("mcp__", StringComparison.Ordinal) Then Return (Nothing, name)
            Dim rest = name.Substring(5)
            Dim i = rest.IndexOf("__", StringComparison.Ordinal)
            If i < 0 Then Return (Nothing, rest)
            Return (rest.Substring(0, i), rest.Substring(i + 2))
        End Function

        ''' <summary>"server:tool" → "mcp__server__tool"; full names stay as they are.</summary>
        Public Function FullName(spec As String) As String
            Dim s = If(spec, "").Trim()
            If s.StartsWith("mcp__", StringComparison.Ordinal) Then Return s
            Dim i = s.IndexOf(":"c)
            Return If(i > 0, $"mcp__{s.Substring(0, i)}__{s.Substring(i + 1)}", s)
        End Function

        ''' <summary>Characters a tool adds to every model call while it is loaded.</summary>
        Public Function SizeOf(t As IAgentTool) As Integer
            Return t.Name.Length + If(t.Description, "").Length + If(t.Parameters?.ToJsonString(), "").Length
        End Function

        ''' <summary>
        ''' The MCP tools a new task starts with (CoreTools and the agent's most used ones that it has), or Nothing when every
        ''' tool is loaded at once: loading off, no MCP tools, or too little left over to be worth deferring.
        ''' </summary>
        Public Function CoreTools(registry As AgentToolRegistry, opts As ToolLoadingOptions, recentlyUsed As IEnumerable(Of String)) As List(Of String)
            If registry Is Nothing OrElse opts Is Nothing OrElse Not opts.Enabled Then Return Nothing
            Dim mcp = registry.All().Where(Function(t) t.Source = ToolSource.Mcp).ToList()
            If mcp.Count = 0 Then Return Nothing
            Dim wanted As New HashSet(Of String)(StringComparer.Ordinal)
            For Each c In If(opts.CoreTools, New List(Of String))
                wanted.Add(FullName(c))
            Next
            For Each r In If(recentlyUsed, Enumerable.Empty(Of String)()).Take(Math.Max(0, opts.RecentlyUsedTools))
                wanted.Add(r)
            Next
            Dim deferredChars = mcp.Where(Function(t) Not wanted.Contains(t.Name)).Sum(Function(t) SizeOf(t))
            If deferredChars < Math.Max(0, opts.MinDeferredChars) Then Return Nothing
            Return mcp.Where(Function(t) wanted.Contains(t.Name)).Select(Function(t) t.Name).ToList()
        End Function

        ''' <summary>The system-prompt section that names the tools not loaded yet, by server (Nothing when there are none).</summary>
        Public Function Catalog(registry As AgentToolRegistry, loaded As ICollection(Of String)) As String
            Dim deferred = registry.All().Where(Function(t) t.Source = ToolSource.Mcp AndAlso Not loaded.Contains(t.Name)).ToList()
            If deferred.Count = 0 Then Return Nothing
            Dim sb As New StringBuilder()
            sb.Append("## More tools (not loaded yet)").Append(vbLf)
            sb.Append($"Besides the tools you have, {deferred.Count} more are available. Their descriptions are left out to keep your context small. " &
                      "Before you use one, call load_tools with its name as server:tool (several at once is fine), or with a query of keywords to find " &
                      "the right ones; they then stay loaded for the rest of this task.").Append(vbLf)
            For Each g In deferred.GroupBy(Function(t) If(SplitMcpName(t.Name).Server, "other")).OrderBy(Function(x) x.Key, StringComparer.Ordinal)
                sb.Append($"- {g.Key}: ").Append(String.Join(", ", g.Select(Function(t) SplitMcpName(t.Name).Tool).OrderBy(Function(n) n, StringComparer.Ordinal))).Append(vbLf)
            Next
            Return sb.ToString().TrimEnd()
        End Function

        ''' <summary>An MCP tool of the registry by its name as an agent writes it: full, server:tool, or a tool name only one server has.</summary>
        Public Function Resolve(registry As AgentToolRegistry, name As String) As IAgentTool
            Dim n = If(name, "").Trim()
            If n.Length = 0 Then Return Nothing
            Dim t = registry.TryGet(FullName(n))
            If t IsNot Nothing AndAlso t.Source = ToolSource.Mcp Then Return t
            Dim matches = registry.All().Where(Function(x) x.Source = ToolSource.Mcp AndAlso String.Equals(SplitMcpName(x.Name).Tool, n, StringComparison.OrdinalIgnoreCase)).ToList()
            Return If(matches.Count = 1, matches(0), Nothing)
        End Function

        ''' <summary>The MCP tools that best match a keyword query (name words count most), best first.</summary>
        Public Function Search(registry As AgentToolRegistry, query As String, limit As Integer) As List(Of IAgentTool)
            Dim words = Text.RegularExpressions.Regex.Split(If(query, "").ToLowerInvariant(), "[^a-z0-9]+").Where(Function(w) w.Length >= 2).Distinct().ToList()
            If words.Count = 0 Then Return New List(Of IAgentTool)
            Return registry.All().Where(Function(t) t.Source = ToolSource.Mcp).
                Select(Function(t)
                           Dim name = t.Name.ToLowerInvariant()
                           Dim desc = If(t.Description, "").ToLowerInvariant()
                           Dim score = words.Sum(Function(w) If(name.Contains(w), 3, 0) + If(desc.Contains(w), 1, 0))
                           Return (Tool:=t, Score:=score)
                       End Function).
                Where(Function(x) x.Score > 0).OrderByDescending(Function(x) x.Score).ThenBy(Function(x) x.Tool.Name, StringComparer.Ordinal).
                Take(Math.Max(1, limit)).Select(Function(x) x.Tool).ToList()
        End Function
    End Module

    ''' <summary>Loads MCP tools the task does not have yet (ToolLoading): by name, or the best matches for a query.</summary>
    Public NotInheritable Class LoadToolsTool
        Inherits InternalTool

        Private Const MaxPerCall As Integer = 25
        Private ReadOnly _registry As AgentToolRegistry

        Public Sub New(registry As AgentToolRegistry)
            _registry = registry
        End Sub

        Public Overrides ReadOnly Property Name As String = "load_tools"
        Public Overrides ReadOnly Property Description As String =
            "Load tools listed under ""More tools (not loaded yet)"" so you can call them: give their names (server:tool), or a query of keywords to " &
            "find the right ones. Loaded tools stay available for the rest of this task."
        Public Overrides ReadOnly Property Parameters As JsonObject = Schema.Obj({},
            Schema.Prop("names", Schema.StrArray("Tools to load, as server:tool (for example gitea:list_issues).")),
            Schema.Prop("query", Schema.Str("Keywords describing what you need (for example ""pull request review comments""); the best matches are loaded.", 300)))

        Public Overrides Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult)
            Dim found As New List(Of IAgentTool)
            Dim missing As New List(Of String)
            For Each n In JsonUtil.GetStringList(args, "names")
                Dim t = ToolLoading.Resolve(_registry, n)
                If t Is Nothing Then missing.Add(n) Else If Not found.Contains(t) Then found.Add(t)
            Next
            Dim query = JsonUtil.GetString(args, "query", "")
            If Not String.IsNullOrWhiteSpace(query) Then
                For Each t In ToolLoading.Search(_registry, query, 8)
                    If Not found.Contains(t) Then found.Add(t)
                Next
            End If
            found = found.Take(MaxPerCall).ToList()
            Dim sb As New StringBuilder()
            If found.Count = 0 Then
                sb.Append("No tool matched. ")
                If missing.Count > 0 Then sb.Append("Unknown: ").Append(String.Join(", ", missing)).Append(". ")
                sb.Append("Use the exact server:tool names from ""More tools (not loaded yet)"", or a query with other keywords.")
                Return Task.FromResult(ToolResult.Fail(sb.ToString()))
            End If
            sb.Append($"Loaded {found.Count} tool(s); you can call them from your next step:").Append(vbLf)
            For Each t In found
                Dim params = If(t.Parameters?("properties"), Nothing)
                Dim names = If(TryCast(params, JsonObject)?.Select(Function(p) p.Key), Enumerable.Empty(Of String)())
                sb.Append($"- {t.Name}: {TextUtil.Truncate(If(t.Description, "").Replace(vbLf, " "), 240)}")
                If names.Any() Then sb.Append(" (arguments: ").Append(String.Join(", ", names)).Append(")")
                sb.Append(vbLf)
            Next
            If missing.Count > 0 Then sb.Append("Not found: ").Append(String.Join(", ", missing)).Append(vbLf)
            Return Task.FromResult(New ToolResult With {.Success = True, .Content = sb.ToString().TrimEnd(), .LoadTools = found.Select(Function(t) t.Name).ToList()})
        End Function
    End Class

End Namespace
