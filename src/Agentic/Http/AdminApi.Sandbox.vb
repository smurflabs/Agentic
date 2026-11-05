Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Endpoints for the code sandbox: browsing and downloading files out of it, and (for admins) seeing and stopping the
    ' long-lived processes and containers the host tracks there.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterSandbox(r As Router)
            r.Map("GET", "/api/chat/sandbox/files", AuthLevel.Read, AddressOf SandboxFiles)
            r.Map("GET", "/api/chat/sandbox/download", AuthLevel.Read, AddressOf SandboxDownloadFile)
            r.Map("GET", "/api/sandbox/resources", AuthLevel.Admin, AddressOf SandboxResourcesList)
            r.Map("POST", "/api/sandbox/resources/{id}/stop", AuthLevel.Admin, AddressOf SandboxResourceStop)
            r.Map("GET", "/api/sandbox/apps", AuthLevel.Read, AddressOf SandboxApps)
            r.Map("POST", "/api/sandbox/apps/preview", AuthLevel.SubmitTask, AddressOf SandboxAppPreview)
            r.Map("DELETE", "/api/sandbox/previews/{id}", AuthLevel.SubmitTask, AddressOf SandboxPreviewRevoke)
        End Sub

        Private Shared Function PreviewDto(p As SandboxPreview, origin As String) As Object
            Return New With {.id = p.Id, .path = p.Path, .url = origin & p.Path, .containerId = p.ContainerId, .container = p.ContainerName,
                             .port = p.TargetPort, .containerPort = p.ContainerPort, .transport = p.Transport, .chatId = p.ChatId, .createdUtc = p.CreatedUtc, .expiresUtc = p.ExpiresUtc,
                             .lastUsedUtc = p.LastUsedUtc}
        End Function

        Private Sub RequirePreviews(rc As RequestContext)
            RequireChat()
            If Not ChatAccessFor(rc.User).Code Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to the code interpreter and sandbox.")
            If _h.SandboxReaper Is Nothing OrElse _h.SandboxPreviews Is Nothing OrElse Not _h.Chat.Options.Sandbox.Previews Then
                Throw New ApiException(409, "PREVIEWS_OFF", "App previews are turned off (Chat.Sandbox.Previews).")
            End If
        End Sub

        ''' <summary>GET /api/sandbox/apps — web apps running in sandbox containers (with published ports), and your open preview links.</summary>
        Private Async Function SandboxApps(rc As RequestContext) As Task
            RequirePreviews(rc)
            ' A hung sandbox must not hold the page: answer within 8 s with the last known apps and a warning.
            Dim look = Await _h.SandboxReaper.PreviewableForPageAsync(rc.CancellationToken, TimeSpan.FromSeconds(8)).ConfigureAwait(False)
            Dim apps = look.Apps
            Dim origin = PublicOrigin(rc)
            Dim now = DateTime.UtcNow
            Dim mine = (Await _h.SandboxPreviews.ListForUserAsync(rc.User.Name).ConfigureAwait(False)).Where(Function(p) p.IsLive(now)).ToList()
            Await rc.OkAsync(New With {
                .apps = apps.Select(Function(c) New With {.id = c.Id, .name = c.Name, .image = c.Image, .createdUtc = c.CreatedUtc,
                    .ports = c.Ports.Select(Function(p) New With {.host = p.HostPort, .container = p.ContainerPort}).ToList(),
                    .previews = mine.Where(Function(p) p.ContainerId = c.Id).Select(Function(p) PreviewDto(p, origin)).ToList()}).ToList(),
                .previews = mine.Select(Function(p) PreviewDto(p, origin)).ToList(),
                .previewPorts = _h.Chat.Options.Sandbox.PreviewPorts, .warning = look.Warning}).ConfigureAwait(False)
        End Function

        ''' <summary>POST /api/sandbox/apps/preview {container, port?, chatId?} — a link that opens the app (reuses a live one).</summary>
        Private Async Function SandboxAppPreview(rc As RequestContext) As Task
            RequirePreviews(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim port As Integer? = Nothing
            Dim n = JsonUtil.GetInt(b, "port", 0)
            If n > 0 Then port = n
            Dim r = Await _h.SandboxReaper.CreatePreviewAsync(rc.User.Name, JsonUtil.GetString(b, "chatId"), JsonUtil.GetString(b, "container"), port, rc.CancellationToken).ConfigureAwait(False)
            If r.ErrorText IsNot Nothing Then Throw ApiException.BadRequest(r.ErrorText, "PREVIEW_FAILED")
            Await rc.WriteJsonAsync(201, PreviewDto(r.Preview, PublicOrigin(rc))).ConfigureAwait(False)
        End Function

        ''' <summary>DELETE /api/sandbox/previews/{id} — closes a preview link now (its owner, or an administrator).</summary>
        Private Async Function SandboxPreviewRevoke(rc As RequestContext) As Task
            RequirePreviews(rc)
            Dim p = Await _h.SandboxPreviews.GetAsync(rc.Route("id")).ConfigureAwait(False)
            If p Is Nothing OrElse Not (rc.User.IsAdmin OrElse String.Equals(p.OwnerUser, rc.User.Name, StringComparison.OrdinalIgnoreCase)) Then
                Throw ApiException.NotFound("PREVIEW_NOT_FOUND", "No such preview link.")
            End If
            Await _h.SandboxPreviews.RevokeAsync(p.Id, DateTime.UtcNow).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Sub RequireSandboxDownloads()
            RequireChat()
            If Not _h.Chat.Options.Sandbox.AllowDownloads Then Throw New ApiException(409, "SANDBOX_DOWNLOADS_OFF", "Downloading files from the sandbox is turned off (Chat.Sandbox.AllowDownloads).")
        End Sub

        Private Function SandboxServerId() As String
            Return _h.Chat.Options.CodeServerId
        End Function

        ''' <summary>GET /api/chat/sandbox/files?path=/home/user — a directory listing from the sandbox.</summary>
        Private Async Function SandboxFiles(rc As RequestContext) As Task
            RequireSandboxDownloads()
            If Not ChatAccessFor(rc.User).Code Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to the code interpreter and sandbox.")
            Dim serverId = SandboxServerId()
            If _h.Sandbox Is Nothing OrElse Not _h.Sandbox.Available(serverId) Then Throw New ApiException(409, "SANDBOX_OFFLINE", "The code sandbox is not connected.")
            Dim path = rc.QueryString("path", "/home/user")
            Dim listing = Await _h.Sandbox.ListAsync(serverId, path, rc.CancellationToken).ConfigureAwait(False)
            If listing Is Nothing Then Throw New ApiException(502, "SANDBOX_LIST_FAILED", "Could not list that folder in the sandbox.")
            Await rc.OkAsync(New With {
                .dir = listing.Dir, .writable = listing.Writable,
                .entries = listing.Entries.OrderByDescending(Function(e) e.Type = "dir").ThenBy(Function(e) e.Name, StringComparer.OrdinalIgnoreCase).
                    Select(Function(e) New With {.name = e.Name, .type = e.Type, .size = e.Size}).ToList()}).ConfigureAwait(False)
        End Function

        ''' <summary>GET /api/chat/sandbox/download?path=…&amp;zip=1 — streams a sandbox file (or a zipped folder) as a download.</summary>
        Private Async Function SandboxDownloadFile(rc As RequestContext) As Task
            RequireSandboxDownloads()
            If Not ChatAccessFor(rc.User).Code Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to the code interpreter and sandbox.")
            Dim serverId = SandboxServerId()
            If _h.Sandbox Is Nothing OrElse Not _h.Sandbox.Available(serverId) Then Throw New ApiException(409, "SANDBOX_OFFLINE", "The code sandbox is not connected.")
            Dim path = rc.QueryString("path")
            If String.IsNullOrWhiteSpace(path) Then Throw ApiException.BadRequest("path is required.")
            Dim zip = rc.QueryString("zip") = "1" OrElse String.Equals(rc.QueryString("zip"), "true", StringComparison.OrdinalIgnoreCase)
            Dim limit = CLng(Math.Max(1, _h.Chat.Options.Sandbox.MaxDownloadMb)) * 1024L * 1024L
            _h.SandboxActivity?.Note(rc.User.Name, Nothing)
            Dim dl = If(zip, Await _h.Sandbox.DownloadZipAsync(serverId, path, limit, rc.CancellationToken).ConfigureAwait(False),
                             Await _h.Sandbox.DownloadFileAsync(serverId, path, limit, rc.CancellationToken).ConfigureAwait(False))
            If dl Is Nothing Then Throw New ApiException(502, "SANDBOX_READ_FAILED", "Could not read the file from the sandbox.")
            If dl.ErrorText IsNot Nothing Then Throw ApiException.BadRequest(dl.ErrorText)
            If dl.Data Is Nothing Then Throw ApiException.NotFound("SANDBOX_FILE_NOT_FOUND", "No such file in the sandbox.")
            Dim safeName = Global.System.Text.RegularExpressions.Regex.Replace(dl.Name, "[^\w\-. ]+", "_")
            Await rc.WriteBytesAsync(200, "application/octet-stream", dl.Data, New Dictionary(Of String, String) From {
                {"Content-Disposition", $"attachment; filename=""{safeName}"""},
                {"Content-Security-Policy", "default-src 'none'; sandbox"}, {"Cache-Control", "private, no-store"}}).ConfigureAwait(False)
        End Function

        Private Shared Function SandboxResourceDto(x As SandboxResourceInfo) As Object
            Return New With {
                .id = x.Id, .kind = x.Kind, .ref = x.Ref, .name = x.Name, .workingDir = x.WorkingDir, .command = x.Command,
                .ownerUser = x.OwnerUser, .ownerChatId = x.OwnerChatId, .ownerAgentId = x.OwnerAgentId,
                .isProtected = x.IsProtected, .state = x.State, .createdUtc = x.CreatedUtc, .firstSeenUtc = x.FirstSeenUtc,
                .lastSeenUtc = x.LastSeenUtc, .stoppedUtc = x.StoppedUtc, .reason = x.Reason}
        End Function

        ''' <summary>GET /api/sandbox/resources — the long-lived sandbox resources the host tracks (running by default).</summary>
        Private Async Function SandboxResourcesList(rc As RequestContext) As Task
            RequireChat()
            If _h.SandboxResources Is Nothing Then Throw New ApiException(409, "SANDBOX_OFF", "Sandbox tracking is not available.")
            Dim states = If(rc.QueryString("state") = "all", {"running", "stopped", "reaped", "failed", "gone"}, {"running"})
            Dim list = Await _h.SandboxResources.ListAsync(states, 500).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .reaperEnabled = _h.Chat.Options.Sandbox.ReaperEnabled,
                .manageContainers = _h.Chat.Options.Sandbox.ManageContainers,
                .processMaxLifeMinutes = _h.Chat.Options.Sandbox.ProcessMaxLifeMinutes,
                .processIdleMinutes = _h.Chat.Options.Sandbox.ProcessIdleMinutes,
                .containerMaxLifeMinutes = _h.Chat.Options.Sandbox.ContainerMaxLifeMinutes,
                .resources = list.Select(AddressOf SandboxResourceDto).ToList()}).ConfigureAwait(False)
        End Function

        ''' <summary>POST /api/sandbox/resources/{id}/stop — stop one tracked sandbox resource now (admins).</summary>
        Private Async Function SandboxResourceStop(rc As RequestContext) As Task
            RequireChat()
            If _h.SandboxReaper Is Nothing OrElse _h.SandboxResources Is Nothing Then Throw New ApiException(409, "SANDBOX_OFF", "Sandbox tracking is not available.")
            Dim res = Await _h.SandboxResources.GetAsync(rc.Route("id")).ConfigureAwait(False)
            If res Is Nothing Then Throw ApiException.NotFound("SANDBOX_RESOURCE_NOT_FOUND", "No such sandbox resource.")
            If Not String.Equals(res.State, "running", StringComparison.OrdinalIgnoreCase) Then Throw ApiException.BadRequest("That resource is not running.")
            Dim ok = Await _h.SandboxReaper.StopResourceAsync(res, "stopped from the dashboard by " & rc.User.Name, "manual", DateTime.UtcNow, rc.CancellationToken).ConfigureAwait(False)
            Dim after = Await _h.SandboxResources.GetAsync(res.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = ok, .resource = SandboxResourceDto(after)}).ConfigureAwait(False)
        End Function

    End Class

End Namespace
