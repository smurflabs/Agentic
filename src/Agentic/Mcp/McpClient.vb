Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic.Configuration
Imports ModelContextProtocol.Client
Imports ModelContextProtocol.Protocol

Namespace Mcp

    Public Class McpToolInfo
        Public Property ServerId As String
        Public Property Name As String
        Public Property Description As String
        Public Property InputSchemaJson As String
        Public Property IsIdempotent As Boolean
        Public Property IsReadOnly As Boolean
    End Class

    Public Class McpImage
        Public Property Data As Byte()
        Public Property MimeType As String
    End Class

    Public Class McpCallResult
        Public Property IsError As Boolean
        Public Property Text As String
        Public Property StructuredJson As String
        Public Property Images As New List(Of McpImage)
    End Class

    ''' <summary>Transport-agnostic MCP client abstraction (the SDK is an implementation detail behind it).</summary>
    Public Interface IMcpClient
        Inherits IAsyncDisposable
        Function ConnectAsync(ct As CancellationToken) As Task
        Function ListToolsAsync(ct As CancellationToken) As Task(Of List(Of McpToolInfo))
        Function CallToolAsync(name As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult)
        Function PingAsync(ct As CancellationToken) As Task
    End Interface

    ''' <summary>Factory so tests can substitute fake MCP clients.</summary>
    Public Interface IMcpClientFactory
        Function Create(server As McpServerOptions) As IMcpClient
    End Interface

    Public NotInheritable Class SdkMcpClientFactory
        Implements IMcpClientFactory

        Public Function Create(server As McpServerOptions) As IMcpClient Implements IMcpClientFactory.Create
            Return New SdkMcpClient(server)
        End Function
    End Class

    ''' <summary>
    ''' MCP client built on the official ModelContextProtocol.Core SDK (v2.2.0): stdio (child process) and
    ''' HTTP (Streamable HTTP / SSE) transports. Secrets come from environment variables only.
    ''' </summary>
    Public NotInheritable Class SdkMcpClient
        Implements IMcpClient

        Private ReadOnly _server As McpServerOptions
        Private _client As McpClient

        Public Sub New(server As McpServerOptions)
            _server = server
        End Sub

        Private Function BuildTransport(Optional oauthToken As String = Nothing) As IClientTransport
            Dim secret As String = Nothing
            Dim authType = If(_server.Auth?.Type, "None")
            If Not authType.Equals("None", StringComparison.OrdinalIgnoreCase) AndAlso _server.Auth IsNot Nothing Then
                secret = _server.Auth.ResolveSecret()
            End If

            If _server.Transport = McpTransportKind.Stdio Then
                Dim env As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                ' %NAME% takes the host's environment variable, so a key need not be written into appsettings.json.
                For Each kv In _server.EnvironmentVariables
                    env(kv.Key) = If(kv.Value Is Nothing, Nothing, Environment.ExpandEnvironmentVariables(kv.Value))
                Next
                If secret IsNot Nothing Then env(If(_server.Auth.HeaderName, "MCP_TOKEN").Replace("-", "_").ToUpperInvariant()) = secret
                Dim so As New StdioClientTransportOptions With {
                    .Name = _server.Id,
                    .Command = _server.Command,
                    .Arguments = New List(Of String)(_server.Arguments),
                    .EnvironmentVariables = env
                }
                If Not String.IsNullOrEmpty(_server.WorkingDirectory) Then so.WorkingDirectory = _server.WorkingDirectory
                Return New StdioClientTransport(so, Nothing)
            End If

            Dim headers As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each kv In If(_server.Headers, New Dictionary(Of String, String))
                If Not String.IsNullOrWhiteSpace(kv.Value) Then headers(kv.Key) = kv.Value
            Next
            ' A server signed in to with OAuth (its own Authorization header wins).
            If oauthToken IsNot Nothing AndAlso Not headers.ContainsKey("Authorization") Then headers("Authorization") = "Bearer " & oauthToken
            If secret IsNot Nothing Then
                If authType.Equals("Bearer", StringComparison.OrdinalIgnoreCase) Then
                    headers("Authorization") = "Bearer " & secret
                Else
                    headers(If(_server.Auth.HeaderName, "Authorization")) = secret
                End If
            End If
            Dim mode = HttpTransportMode.AutoDetect
            If String.Equals(_server.HttpMode, "StreamableHttp", StringComparison.OrdinalIgnoreCase) Then mode = HttpTransportMode.StreamableHttp
            If String.Equals(_server.HttpMode, "Sse", StringComparison.OrdinalIgnoreCase) Then mode = HttpTransportMode.Sse
            Dim ho As New HttpClientTransportOptions With {
                .Endpoint = New Uri(_server.Endpoint),
                .Name = _server.Id,
                .TransportMode = mode,
                .AdditionalHeaders = headers,
                .ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(5, _server.ConnectTimeoutSeconds))
            }
            Return New HttpClientTransport(ho, CType(Nothing, Microsoft.Extensions.Logging.ILoggerFactory))
        End Function

        Public Async Function ConnectAsync(ct As CancellationToken) As Task Implements IMcpClient.ConnectAsync
            Dim token = If(_server.TokenProvider Is Nothing OrElse _server.Transport = McpTransportKind.Stdio, Nothing, Await _server.TokenProvider.Invoke().ConfigureAwait(False))
            Dim transport = BuildTransport(token)
            Dim options As New McpClientOptions With {
                .ClientInfo = New Implementation With {.Name = "Agentic", .Version = "1.0.0"},
                .InitializationTimeout = TimeSpan.FromSeconds(Math.Max(5, _server.ConnectTimeoutSeconds))
            }
            _client = Await McpClient.CreateAsync(transport, options, Nothing, ct).ConfigureAwait(False)
        End Function

        Private Function RequireClient() As McpClient
            If _client Is Nothing Then Throw New InvalidOperationException($"MCP server '{_server.Id}' is not connected.")
            Return _client
        End Function

        Public Async Function ListToolsAsync(ct As CancellationToken) As Task(Of List(Of McpToolInfo)) Implements IMcpClient.ListToolsAsync
            Dim tools = Await RequireClient().ListToolsAsync(CType(Nothing, ModelContextProtocol.RequestOptions), ct).ConfigureAwait(False)
            Dim result As New List(Of McpToolInfo)
            For Each t In tools
                Dim ann = t.ProtocolTool?.Annotations
                result.Add(New McpToolInfo With {
                    .ServerId = _server.Id,
                    .Name = t.Name,
                    .Description = If(t.Description, String.Empty),
                    .InputSchemaJson = t.JsonSchema.GetRawText(),
                    .IsIdempotent = ann IsNot Nothing AndAlso Object.Equals(CObj(ann.IdempotentHint), True),
                    .IsReadOnly = ann IsNot Nothing AndAlso Object.Equals(CObj(ann.ReadOnlyHint), True)
                })
            Next
            Return result
        End Function

        Public Async Function CallToolAsync(name As String, arguments As JsonElement, ct As CancellationToken) As Task(Of McpCallResult) Implements IMcpClient.CallToolAsync
            Dim args As New Dictionary(Of String, Object)(StringComparer.Ordinal)
            If arguments.ValueKind = JsonValueKind.Object Then
                For Each p In arguments.EnumerateObject()
                    args(p.Name) = p.Value.Clone()
                Next
            End If
            Dim result = Await RequireClient().CallToolAsync(name, args, Nothing, Nothing, ct).ConfigureAwait(False)
            Dim outcome As New McpCallResult With {.IsError = Object.Equals(CObj(result.IsError), True)}
            Dim sb As New Text.StringBuilder()
            If result.Content IsNot Nothing Then
                For Each block In result.Content
                    Dim tb = TryCast(block, TextContentBlock)
                    If tb IsNot Nothing Then
                        If sb.Length > 0 Then sb.AppendLine()
                        sb.Append(tb.Text)
                        Continue For
                    End If
                    Dim ib = TryCast(block, ImageContentBlock)
                    If ib IsNot Nothing Then
                        Try
                            outcome.Images.Add(New McpImage With {.Data = ib.DecodedData.ToArray(), .MimeType = If(ib.MimeType, "image/png")})
                        Catch
                        End Try
                        Continue For
                    End If
                    Dim rb = TryCast(block, EmbeddedResourceBlock)
                    If rb IsNot Nothing Then
                        If sb.Length > 0 Then sb.AppendLine()
                        sb.Append(JsonSerializer.Serialize(CObj(rb.Resource)))
                    End If
                Next
            End If
            outcome.Text = sb.ToString()
            Dim sc As Object = result.StructuredContent
            If sc IsNot Nothing Then outcome.StructuredJson = JsonSerializer.Serialize(sc)
            Return outcome
        End Function

        Public Async Function PingAsync(ct As CancellationToken) As Task Implements IMcpClient.PingAsync
            Await RequireClient().PingAsync(CType(Nothing, ModelContextProtocol.RequestOptions), ct).ConfigureAwait(False)
        End Function

        Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
            Return New ValueTask(DisposeCoreAsync())
        End Function

        Private Async Function DisposeCoreAsync() As Task
            Dim c = _client
            _client = Nothing
            If c IsNot Nothing Then
                Try
                    Await c.DisposeAsync().ConfigureAwait(False)
                Catch
                End Try
            End If
        End Function
    End Class

End Namespace
