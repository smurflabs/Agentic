Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading

Imports System.Text.Json
Imports Agentic.Utilities

Namespace Work

    ' share_file: pulling a file (or a zipped folder) out of the code sandbox and attaching it to the answer as a download.
    ' preview_app: opening a web app that runs in a sandbox container at /container/{id}/ on this host's own port.
    Partial Public NotInheritable Class ChatService

        Public Const PreviewTool As String = "preview_app"

        ''' <summary>App previews and the sandbox's containers (set by the host).</summary>
        Public Property SandboxReaper As SandboxReaper

        Private Shared Function ShareFileBinding() As ToolBinding
            Return New ToolBinding With {.Name = "share_file", .Server = "share", .Tool = "share_file", .Risk = "read", .Definition = FunctionDef("share_file",
                "Give the user a file from the sandbox as a download in the chat. Use this whenever the user wants to download, save, export or get a file that was made in the sandbox " &
                "(by run_code or the terminal): a script, a document, a build, data, an archive. Give the full sandbox path, e.g. /home/user/report.pdf or /tmp/out.csv. " &
                "To send a folder, or several files at once, set as_zip to true and give the folder's path: it is zipped and shared as one .zip. The file appears in the chat with a Download button. " &
                "Share only files that exist (list or create them first).",
                "{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Full path in the sandbox, e.g. /home/user/out.csv""}," &
                """as_zip"":{""type"":""boolean"",""description"":""Zip the path (a folder, or to bundle) and share the .zip (default false)""}},""required"":[""path""]}")}
        End Function

        Private Shared Function PreviewAppBinding() As ToolBinding
            Return New ToolBinding With {.Name = PreviewTool, .Server = "preview", .Tool = PreviewTool, .Risk = "read", .Definition = FunctionDef(PreviewTool,
                "Let the user open a web app you are running in the sandbox, in their browser: it gives a link served by this site (/container/<id>/) that shows the " &
                "app live. The app must run in a Docker container with a published port (sudo docker run -d --name <name> -p <host port>:<app port> <image>) and " &
                "listen on 0.0.0.0. Call it once the app answers; the user gets an Open button. Name the container; give port only when it publishes several.",
                "{""type"":""object"",""properties"":{""container"":{""type"":""string"",""description"":""The container's name (or id)""}," &
                """port"":{""type"":""integer"",""description"":""The published host port or the app's port, when the container publishes several""}},""required"":[""container""]}")}
        End Function

        ''' <summary>
        ''' How to build something the user can open, told to the model when preview_app is offered: which host ports to publish
        ''' on (the range forwarded for previews, with some free right now), and how the app is served.
        ''' </summary>
        Friend Shared Function PreviewNoteFor(range As (Low As Integer, High As Integer)?, free As List(Of Integer)) As String
            Dim port = If(range.HasValue,
                $"a host port in {range.Value.Low}-{range.Value.High} (forwarded for previews: full speed and live updates" &
                    If(free IsNot Nothing AndAlso free.Count > 0, $"; free now: {String.Join(", ", free)}", "") & ")",
                "a free host port")
            Return "Running web apps: when you build a web app, API or site the user may want to see, run it in a Docker container in the sandbox with a published port " &
                   $"(write a Dockerfile, sudo docker build -t <name> ., then sudo docker run -d --name <name> -p <host port>:<app port> <name>, using {port}; " &
                   "if Docker says the port is already allocated, take another; make the app listen on 0.0.0.0), check that it answers (curl), then call preview_app " &
                   "so the user gets a link. The app is served under a path prefix (/container/<id>/): prefer relative URLs. Streamlit needs " &
                   "--server.enableCORS false --server.enableXsrfProtection false. Containers are stopped automatically after a while; tell the user how to start it again."
        End Function

        ''' <summary>Runs preview_app: a link to the container's published port, shown to the user as an Open button.</summary>
        Private Async Function RunPreviewToolAsync(username As String, chat As ChatConversation, argsJson As String, ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
            If SandboxReaper Is Nothing Then Return (False, "App previews are not available on this server.")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            Dim container = JsonUtil.GetString(args, "container")
            Dim port As Integer? = Nothing
            Dim portEl As JsonElement
            If args.ValueKind = JsonValueKind.Object AndAlso args.TryGetProperty("port", portEl) Then
                Dim n As Integer
                If portEl.ValueKind = JsonValueKind.Number AndAlso portEl.TryGetInt32(n) Then port = n
                If portEl.ValueKind = JsonValueKind.String AndAlso Integer.TryParse(portEl.GetString(), n) Then port = n
            End If
            SandboxActivity?.Note(username, chat.Id)
            Dim r = Await SandboxReaper.CreatePreviewAsync(username, chat.Id, container, port, ct).ConfigureAwait(False)
            If r.ErrorText IsNot Nothing Then Return (False, r.ErrorText)
            Dim p = r.Preview
            Dim viaSandbox = String.Equals(p.Transport, "mcp", StringComparison.OrdinalIgnoreCase)
            Dim range = SandboxReaper.PreviewRange
            Dim outside = range.HasValue AndAlso (p.TargetPort < range.Value.Low OrElse p.TargetPort > range.Value.High)
            Return (True, $"Preview ready: {p.Path} (container {p.ContainerName}, host port {p.TargetPort}" &
                          If(p.ContainerPort.HasValue, $" → app port {p.ContainerPort.Value}", "") & $"; the link works until {p.ExpiresUtc:yyyy-MM-dd HH:mm} UTC). " &
                          "The user sees an Open app button for it in the chat. Tell them what they will see there." &
                          If(viaSandbox AndAlso outside,
                             $" Its port is outside the preview range {range.Value.Low}-{range.Value.High}, so the preview goes through the sandbox connection " &
                             "(slower, no live WebSocket updates): for the full experience, run it again on a free port in that range and call preview_app again.",
                             If(viaSandbox, " This server cannot reach the sandbox host's ports, so the preview goes through the sandbox connection: pages, " &
                                            "forms and APIs work, but live connections (WebSockets: Streamlit, Gradio, hot reload) do not; prefer a plain " &
                                            "web framework (Flask, FastAPI, Express, a static site) for what the user should try here.", "")))
        End Function

        ''' <summary>The download limit for files pulled out of the sandbox, in bytes.</summary>
        Friend Function SandboxDownloadLimit() As Long
            Return CLng(Math.Max(1, _opt.Sandbox.MaxDownloadMb)) * 1024L * 1024L
        End Function

        ''' <summary>
        ''' Runs the share_file tool: reads the given sandbox path (zipping it first when as_zip), stores it as an upload and
        ''' attaches it to the answer so the user gets a Download button. Returns (ok, text) for the tool result.
        ''' </summary>
        Private Async Function RunShareFileToolAsync(username As String, chat As ChatConversation, argsJson As String, answer As ChatMessageRecord,
                                                     emit As Func(Of String, Object, Task), ct As CancellationToken) As Task(Of (Ok As Boolean, Text As String))
            If Sandbox Is Nothing OrElse Not _opt.Sandbox.AllowDownloads Then Return (False, "Sharing files from the sandbox is turned off.")
            Dim serverId = _opt.CodeServerId
            If Not Sandbox.Available(serverId) Then Return (False, "The code sandbox is not connected, so there is no file to share.")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson), args) Then Return (False, "The arguments are not valid JSON.")
            Dim path = JsonUtil.GetString(args, "path")
            If String.IsNullOrWhiteSpace(path) Then Return (False, "Give the full path of the file to share, e.g. /home/user/out.csv.")
            Dim asZip = JsonUtil.GetBool(args, "as_zip", False)
            SandboxActivity?.Note(username, chat.Id)
            Dim limit = SandboxDownloadLimit()

            Await emit("status", New With {.text = If(asZip, "Zipping and fetching from the sandbox…", "Fetching the file from the sandbox…")}).ConfigureAwait(False)
            Dim dl As SandboxDownload
            Try
                dl = If(asZip, Await Sandbox.DownloadZipAsync(serverId, path, limit, ct).ConfigureAwait(False),
                               Await Sandbox.DownloadFileAsync(serverId, path, limit, ct).ConfigureAwait(False))
            Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                Return (False, "Could not read the file from the sandbox: " & ex.Message)
            End Try
            If dl Is Nothing Then Return (False, "Could not read the file from the sandbox.")
            If dl.ErrorText IsNot Nothing Then Return (False, dl.ErrorText)
            If dl.Data Is Nothing OrElse dl.Data.Length = 0 Then Return (False, "The file is empty, so there is nothing to download.")

            Dim info As Artifacts.UploadInfo
            Try
                info = Await _uploads.SaveAsync(dl.Name, dl.Data, username, maxBytes:=limit).ConfigureAwait(False)
            Catch ex As ArgumentException
                Return (False, ex.Message)
            End Try
            Dim att As New ChatAttachmentRef With {.UploadId = info.Id, .Name = info.Name, .Kind = info.Kind, .SizeBytes = info.SizeBytes,
                .Summary = If(dl.Zipped, "Zipped from the sandbox: " & path, "From the sandbox: " & path)}
            answer.Attachments.Add(att)
            Await emit("file", New With {.messageId = answer.Id, .attachment = att}).ConfigureAwait(False)
            Return (True, $"Shared {info.Name} ({SandboxGateway.FormatSize(info.SizeBytes)}) with the user as a download in the chat.")
        End Function

    End Class

End Namespace
