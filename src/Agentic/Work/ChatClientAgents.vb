Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' The user's own subagent types (like Claude Code's): Markdown files in .claude/agents or .agentic/agents (the project's or
    ''' the user's) with a name, a description of when to use it, the tools it may use and its instructions. The desktop app and
    ''' VS Code read them and send them with each message; the task tool then offers them as subagent_type, and a subagent of
    ''' that type works by those instructions with only those tools.
    ''' </summary>
    Partial Public NotInheritable Class ChatService

        Public Const MaxClientAgents As Integer = 30

        ''' <summary>clientAgents of a message: [{name, description, prompt, tools: [..]}], checked and shortened.</summary>
        Friend Shared Function ReadClientAgents(b As JsonElement) As List(Of ClientAgent)
            Dim list As New List(Of ClientAgent)
            Dim el As JsonElement
            If b.ValueKind <> JsonValueKind.Object OrElse Not b.TryGetProperty("clientAgents", el) OrElse el.ValueKind <> JsonValueKind.Array Then Return list
            For Each a In el.EnumerateArray()
                If list.Count >= MaxClientAgents Then Exit For
                If a.ValueKind <> JsonValueKind.Object Then Continue For
                Dim name = Text.RegularExpressions.Regex.Replace(If(JsonUtil.GetString(a, "name"), "").Trim(), "[^A-Za-z0-9_.-]+", "-").Trim("-"c)
                Dim prompt = If(JsonUtil.GetString(a, "prompt"), "").Trim()
                If name.Length = 0 OrElse name.Length > 64 OrElse prompt.Length = 0 Then Continue For
                If {"general", "explore"}.Contains(name.ToLowerInvariant()) OrElse list.Any(Function(x) String.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) Then Continue For
                list.Add(New ClientAgent With {.Name = name, .Description = TextUtil.Truncate(If(JsonUtil.GetString(a, "description"), "").Trim(), 400, "…"),
                                               .Prompt = TextUtil.Truncate(prompt, 20000, "…"),
                                               .Tools = JsonUtil.GetStringList(a, "tools").Select(Function(t) t.Trim()).Where(Function(t) t.Length > 0 AndAlso t.Length <= 128).Take(60).ToList()})
            Next
            Return list
        End Function

        ''' <summary>The user's agent type of this name, or Nothing.</summary>
        Friend Shared Function FindClientAgent(client As ChatClientContext, type As String) As ClientAgent
            If client?.Agents Is Nothing OrElse String.IsNullOrWhiteSpace(type) Then Return Nothing
            Return client.Agents.FirstOrDefault(Function(a) String.Equals(a.Name, type.Trim(), StringComparison.OrdinalIgnoreCase))
        End Function

        ''' <summary>What the task tool says about the user's agent types (empty without any).</summary>
        Friend Shared Function ClientAgentsNote(agents As List(Of ClientAgent)) As String
            If agents Is Nothing OrElse agents.Count = 0 Then Return ""
            Dim sb As New StringBuilder(" The user also defined these agent types (set subagent_type to the name when a task fits one): ")
            sb.Append(String.Join("; ", agents.Select(Function(a) a.Name & If(String.IsNullOrWhiteSpace(a.Description), "", " — " & a.Description))))
            Return sb.Append(".").ToString()
        End Function

        ''' <summary>Claude Code's tool names (as agent files list them) and the tools here that do the same.</summary>
        Private Shared ReadOnly AgentToolAliases As New Dictionary(Of String, String())(StringComparer.OrdinalIgnoreCase) From {
            {"Read", {"read_file"}}, {"Write", {"write_file"}}, {"Edit", {"edit_file"}}, {"MultiEdit", {"edit_file"}}, {"NotebookEdit", {"edit_notebook"}},
            {"Glob", {"glob_files"}}, {"Grep", {"grep"}}, {"LS", {"list_dir"}},
            {"Bash", {"run_command", "run_command_background", "command_output", "kill_command"}},
            {"PowerShell", {"run_command", "run_command_background", "command_output", "kill_command"}},
            {"BashOutput", {"command_output"}}, {"KillShell", {"kill_command"}}, {"KillBash", {"kill_command"}}, {"Monitor", {"monitor"}},
            {"WebSearch", {"search_web"}}, {"WebFetch", {"read_page"}}, {"Skill", {"Skill", "skill_file"}}}

        ''' <summary>Whether an agent file's tool list (empty = every tool) lets its subagents use this tool.</summary>
        Friend Shared Function AgentMayUse(agentTools As List(Of String), toolName As String, toolServer As String) As Boolean
            If agentTools Is Nothing OrElse agentTools.Count = 0 Then Return True
            For Each wanted In agentTools
                If String.Equals(wanted, toolName, StringComparison.OrdinalIgnoreCase) Then Return True
                Dim same As String() = Nothing
                If AgentToolAliases.TryGetValue(wanted, same) AndAlso same.Contains(toolName, StringComparer.OrdinalIgnoreCase) Then Return True
                ' mcp__server__tool (or mcp__server for all of its tools), Claude Code's names for MCP tools.
                If wanted.StartsWith("mcp__", StringComparison.OrdinalIgnoreCase) Then
                    Dim rest = wanted.Substring(5)
                    If String.Equals(rest, toolName, StringComparison.OrdinalIgnoreCase) Then Return True
                    If Not rest.Contains("__") AndAlso String.Equals(rest, toolServer, StringComparison.OrdinalIgnoreCase) Then Return True
                End If
            Next
            Return False
        End Function
    End Class

End Namespace
