Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports ModelContextProtocol.Protocol
Imports ModelContextProtocol.Server

''' <summary>
''' Minimal stdio MCP server used by the Agentic integration tests and as a template for your own servers.
''' Tools: echo(text), add(a, b), utc_now(), fail(message).
''' Run by Agentic as a child process:  "Transport": "Stdio", "Command": "dotnet", "Arguments": [ "path\EchoMcpServer.dll" ]
''' </summary>
Module Program
    Function Main(args As String()) As Integer
        Return RunAsync().GetAwaiter().GetResult()
    End Function

    Private Async Function RunAsync() As Task(Of Integer)
        Dim tools As New McpServerPrimitiveCollection(Of McpServerTool)()
        tools.Add(McpServerTool.Create(New Func(Of String, String)(AddressOf Echo),
                                       New McpServerToolCreateOptions With {.Name = "echo", .Description = "Returns the given text prefixed with 'Echo: '.", .ReadOnly = True, .Idempotent = True}))
        tools.Add(McpServerTool.Create(New Func(Of Integer, Integer, Integer)(AddressOf Add),
                                       New McpServerToolCreateOptions With {.Name = "add", .Description = "Adds two integers.", .ReadOnly = True, .Idempotent = True}))
        tools.Add(McpServerTool.Create(New Func(Of String)(AddressOf UtcNow),
                                       New McpServerToolCreateOptions With {.Name = "utc_now", .Description = "Current UTC time (ISO 8601).", .ReadOnly = True}))
        tools.Add(McpServerTool.Create(New Func(Of String, String)(AddressOf Fail),
                                       New McpServerToolCreateOptions With {.Name = "fail", .Description = "Always fails with the given message (for testing error handling)."}))
        Dim options As New McpServerOptions With {
            .ServerInfo = New Implementation With {.Name = "echo-sample", .Version = "1.0.0"},
            .ToolCollection = tools
        }
        Dim transport As New StdioServerTransport("echo-sample", Nothing)
        Dim server = McpServer.Create(transport, options, Nothing, Nothing)
        Await server.RunAsync(CancellationToken.None)
        Return 0
    End Function

    Private Function Echo(text As String) As String
        Return "Echo: " & text
    End Function

    Private Function Add(a As Integer, b As Integer) As Integer
        Return a + b
    End Function

    Private Function UtcNow() As String
        Return DateTime.UtcNow.ToString("o")
    End Function

    Private Function Fail(message As String) As String
        Throw New InvalidOperationException(If(message, "Requested failure"))
    End Function
End Module
