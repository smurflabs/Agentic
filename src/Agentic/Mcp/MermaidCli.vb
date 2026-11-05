Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration

Namespace Mcp

    ''' <summary>Why a Mermaid diagram could not be drawn (its parse error, a timeout, mermaid-cli missing).</summary>
    Public NotInheritable Class MermaidException
        Inherits Exception
        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' Mermaid diagrams drawn by mermaid-cli (Node) with an installed Chrome: the data server's render_mermaid tool and the
    ''' diagrams in Chat answers (```mermaid blocks). The Chat tab finds the paths in the McpServers entry that starts the data
    ''' server with --mermaid-cli, so nothing more is configured.
    ''' </summary>
    Friend NotInheritable Class MermaidCli
        Public ReadOnly Property Node As String
        Public ReadOnly Property Cli As String
        Public ReadOnly Property Chrome As String

        Public Sub New(node As String, cli As String, chrome As String)
            Me.Node = node
            Me.Cli = cli
            Me.Chrome = chrome
        End Sub

        ''' <summary>Node and mermaid-cli are where they were said to be.</summary>
        Public ReadOnly Property Available As Boolean
            Get
                Return Not String.IsNullOrWhiteSpace(Node) AndAlso Not String.IsNullOrWhiteSpace(Cli) AndAlso File.Exists(Node) AndAlso File.Exists(Cli)
            End Get
        End Property

        ''' <summary>The paths from the first enabled MCP server started with --mermaid-cli (the data server); Nothing when there is none.</summary>
        Public Shared Function FromMcpServers(servers As IEnumerable(Of McpServerOptions)) As MermaidCli
            For Each s In If(servers, Enumerable.Empty(Of McpServerOptions)())
                If s Is Nothing OrElse Not s.Enabled OrElse s.Arguments Is Nothing Then Continue For
                Dim args = s.Arguments.ToArray()
                Dim cli = ToolKit.Arg(args, "--mermaid-cli")
                If String.IsNullOrWhiteSpace(cli) Then Continue For
                Return New MermaidCli(ToolKit.Arg(args, "--node"), cli, ToolKit.Arg(args, "--chrome"))
            Next
            Return Nothing
        End Function

        ''' <summary>The diagram as .svg or .png bytes. theme: default, dark, forest or neutral; background: white or transparent.</summary>
        Public Async Function RenderAsync(code As String, ext As String, theme As String, background As String, ct As CancellationToken) As Task(Of Byte())
            If Not Available Then Throw New MermaidException("Mermaid is not set up on this server (--node and --mermaid-cli).")
            If String.IsNullOrWhiteSpace(code) Then Throw New MermaidException("code is required.")
            ext = If(ext, ".svg").ToLowerInvariant()
            If ext <> ".svg" AndAlso ext <> ".png" Then Throw New MermaidException("The diagram is drawn as .svg or .png.")
            Dim th = If(theme, "default").Trim().ToLowerInvariant()
            If Not {"default", "dark", "forest", "neutral"}.Contains(th) Then th = "default"
            Dim work = Path.Combine(Path.GetTempPath(), "agentic-mmd-" & Guid.NewGuid().ToString("N").Substring(0, 10))
            Directory.CreateDirectory(work)
            Try
                Dim input = Path.Combine(work, "diagram.mmd"), output = Path.Combine(work, "diagram" & ext), config = Path.Combine(work, "puppeteer.json")
                File.WriteAllText(input, code, New UTF8Encoding(False))
                Dim pp As New JsonObject From {{"args", New JsonArray("--no-sandbox", "--headless=new", "--disable-gpu")}}
                If Not String.IsNullOrWhiteSpace(Chrome) AndAlso File.Exists(Chrome) Then pp("executablePath") = Chrome
                File.WriteAllText(config, pp.ToJsonString())
                Dim args As New List(Of String) From {Cli, "-i", input, "-o", output, "-p", config, "-t", th, "-b",
                                                      If(String.Equals(background, "transparent", StringComparison.OrdinalIgnoreCase), "transparent", "white"), "-q"}
                If ext = ".png" Then args.AddRange({"-s", "2"})
                Dim r = Await ToolKit.RunProcessAsync(Node, args, work, TimeSpan.FromSeconds(90), ct, New Dictionary(Of String, String) From {{"PUPPETEER_SKIP_DOWNLOAD", "1"}}).ConfigureAwait(False)
                If r.TimedOut Then Throw New MermaidException("Rendering took longer than 90 s and was stopped.")
                If r.ExitCode <> 0 OrElse Not File.Exists(output) Then
                    Dim why = (r.Error & vbLf & r.Output).Trim()
                    Dim parseLine = why.Split(ControlChars.Lf).FirstOrDefault(Function(l) l.Contains("Parse error", StringComparison.OrdinalIgnoreCase) OrElse l.Contains("Error:", StringComparison.Ordinal))
                    Throw New MermaidException("The diagram could not be rendered: " & ToolKit.Clip(If(parseLine, why), 1500))
                End If
                Return File.ReadAllBytes(output)
            Finally
                Try
                    Directory.Delete(work, True)
                Catch ex As IOException
                End Try
            End Try
        End Function
    End Class

End Namespace
