Option Strict On
Option Explicit On
Option Infer On

Namespace Work

    ''' <summary>
    ''' What an account may use in chat (Users &amp; access): MCP servers (all or some), the code interpreter, web search,
    ''' pictures, videos, knowledge bases, memory and tools on its own computers. The server applies it to a chat's settings and
    ''' to every answer, whatever a window sends.
    ''' </summary>
    Public NotInheritable Class ChatAccess
        ''' <summary>MCP servers' tools (chat.tools).</summary>
        Public Property Mcp As Boolean = True
        ''' <summary>The MCP servers allowed; Nothing = every server.</summary>
        Public Property McpServers As HashSet(Of String)
        ''' <summary>The account: its own MCP servers (added on the MCP page) are always allowed with chat.tools.</summary>
        Public Property Username As String
        Public Property Code As Boolean = True
        Public Property Web As Boolean = True
        Public Property Images As Boolean = True
        Public Property Video As Boolean = True
        ''' <summary>The Stable Diffusion server (images.sd, the Images tab's permission).</summary>
        Public Property Diffusion As Boolean = True
        ''' <summary>Suno (music.suno, the Music tab's permission).</summary>
        Public Property Music As Boolean = True
        Public Property Knowledge As Boolean = True
        Public Property Memory As Boolean = True
        ''' <summary>Tools on the user's own computers (the desktop app, VS Code, the CLI).</summary>
        Public Property Devices As Boolean = True

        ''' <summary>Everything allowed.</summary>
        Public Shared Function Full() As ChatAccess
            Return New ChatAccess()
        End Function

        ''' <summary>Before these permissions existed, "tools" decided MCP servers and the code interpreter together.</summary>
        Public Shared Function FromTools(canTools As Boolean) As ChatAccess
            Return New ChatAccess With {.Mcp = canTools, .Code = canTools}
        End Function

        Public Function AllowsServer(serverId As String) As Boolean
            If Not Mcp Then Return False
            Return McpServers Is Nothing OrElse (serverId IsNot Nothing AndAlso McpServers.Contains(serverId)) OrElse Global.Agentic.Mcp.McpUserServers.IsPersonalOf(serverId, Username)
        End Function

        ''' <summary>Turns off in a chat's settings what the account may not use (kept off when it is saved).</summary>
        Public Sub Apply(s As ChatSettings)
            If s Is Nothing Then Return
            s.McpServers = If(s.McpServers, New List(Of String)).Where(Function(id) AllowsServer(id)).ToList()
            If Not Code Then s.CodeInterpreter = False
            If Not Web Then s.WebSearch = False
            If Not Images Then s.ImageGeneration = False
            If Not Video Then s.VideoGeneration = False
            If Not Diffusion Then s.Diffusion = False
            If Not Music Then s.Music = False
            If Not Knowledge Then s.Knowledge = New List(Of String)
            If Not Devices Then s.Device = Nothing
        End Sub
    End Class

End Namespace
