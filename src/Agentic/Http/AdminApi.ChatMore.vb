Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Agentic.Mcp
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' The Chat tab's Mermaid diagrams (```mermaid blocks drawn as pictures, with mermaid-cli like the data server's
    ' render_mermaid) and chats imported from a file (this app's JSON export, ChatGPT's and Claude.ai's exports).
    Partial Friend NotInheritable Class AdminApi

        Private Const MermaidMaxChars As Integer = 20000
        Private Const MermaidCacheSize As Integer = 300
        Private _mermaid As MermaidCli
        Private _mermaidChecked As Boolean
        ''' <summary>Diagrams drawn before (by theme and code): the SVG, or why it could not be drawn.</summary>
        Private ReadOnly _mermaidCache As New ConcurrentDictionary(Of String, (Svg As Byte(), [Error] As String))(StringComparer.Ordinal)
        ''' <summary>Each diagram starts a browser: at most two at a time.</summary>
        Private ReadOnly _mermaidGate As New SemaphoreSlim(2, 2)

        Friend Sub RegisterChatMore(r As Router)
            r.Map("POST", "/api/chat/mermaid", AuthLevel.Read, AddressOf ChatMermaid)
            r.Map("POST", "/api/chat/import", AuthLevel.SubmitTask, AddressOf ChatImport)
        End Sub

        ''' <summary>mermaid-cli as the data server is set up with (Nothing when it is not).</summary>
        Friend ReadOnly Property Mermaid As MermaidCli
            Get
                If Not _mermaidChecked Then
                    Dim found = MermaidCli.FromMcpServers(_h.Options.McpServers)
                    _mermaid = If(found IsNot Nothing AndAlso found.Available, found, Nothing)
                    _mermaidChecked = True
                End If
                Return _mermaid
            End Get
        End Property

        ''' <summary>Body {code, theme: default | dark}: the diagram as SVG (image/svg+xml); 400 with the parse error when it cannot be drawn.</summary>
        Private Async Function ChatMermaid(rc As RequestContext) As Task
            RequireChat()
            Dim m = Mermaid
            If m Is Nothing Then Throw New ApiException(409, "MERMAID_OFF", "Mermaid diagrams are not set up on this server (the data server's --mermaid-cli).")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim code = If(JsonUtil.GetString(b, "code"), "").Trim()
            If code.Length = 0 Then Throw ApiException.BadRequest("code is required.")
            If code.Length > MermaidMaxChars Then Throw ApiException.BadRequest($"The diagram is longer than {MermaidMaxChars:N0} characters.")
            Dim theme = If(String.Equals(JsonUtil.GetString(b, "theme"), "dark", StringComparison.OrdinalIgnoreCase), "dark", "default")
            Dim key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(theme & vbLf & code)))
            Dim hit As (Svg As Byte(), [Error] As String)
            If Not _mermaidCache.TryGetValue(key, hit) Then
                Await _mermaidGate.WaitAsync(rc.CancellationToken).ConfigureAwait(False)
                Try
                    If Not _mermaidCache.TryGetValue(key, hit) Then
                        Try
                            hit = (Await m.RenderAsync(code, ".svg", theme, "transparent", rc.CancellationToken).ConfigureAwait(False), Nothing)
                        Catch ex As MermaidException
                            hit = (Nothing, ex.Message)
                        End Try
                        If _mermaidCache.Count >= MermaidCacheSize Then _mermaidCache.Clear()
                        _mermaidCache(key) = hit
                    End If
                Finally
                    _mermaidGate.Release()
                End Try
            End If
            If hit.Svg Is Nothing Then Throw New ApiException(400, "MERMAID_ERROR", hit.Error)
            Await rc.WriteBytesAsync(200, "image/svg+xml", hit.Svg, New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=86400"}}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body: an export file's JSON (this app's chat export, ChatGPT's or Claude.ai's conversations.json); ?model= for the new
        ''' chats. Answers {chats: [{id, title, messages}]}.
        ''' </summary>
        Private Async Function ChatImport(rc As RequestContext) As Task
            RequireChat()
            Dim data = Await rc.ReadBytesAsync(64L * 1024 * 1024).ConfigureAwait(False)
            Dim found = ChatService.ParseImport(Encoding.UTF8.GetString(data))
            If found.Count = 0 Then
                Throw ApiException.BadRequest("No conversation was found in this file. Import a chat exported as JSON here, or ChatGPT's or Claude.ai's conversations.json.", "NOTHING_TO_IMPORT")
            End If
            Dim model = rc.QueryString("model")
            If String.IsNullOrWhiteSpace(model) Then model = _h.Chat.Options.DefaultModel
            Dim made As New List(Of Object)
            For Each c In found
                Dim chat = Await _h.Chat.ImportAsync(rc.User.Name, c, model).ConfigureAwait(False)
                made.Add(New With {.id = chat.Id, .title = chat.Title, .messages = chat.Messages.Count})
            Next
            Await rc.WriteJsonAsync(201, New With {.chats = made}).ConfigureAwait(False)
        End Function

    End Class

End Namespace
