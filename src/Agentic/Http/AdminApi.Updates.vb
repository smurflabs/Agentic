Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports Agentic.Work

Namespace Http

    ' Admin API: updates for the client apps (CLI, desktop app; Windows and Linux builds, each its own channel such as
    ' "cli-linux-x64"). Any signed-in account or API key may check and download; the list of installs (who runs which
    ' version) needs "View system". New installs download the app as a zip (.tar.gz for Linux); installed copies update
    ' from the exe (downloadUrl) and the single files.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterUpdates(r As Router)
            r.Map("GET", "/api/updates", AuthLevel.Read, AddressOf UpdatesList)
            r.Map("GET", "/api/updates/installs", AuthLevel.Read, AddressOf UpdatesInstalls)
            r.Map("GET", "/api/updates/{app}", AuthLevel.Read, AddressOf UpdatesCheck)
            r.Map("GET", "/api/updates/{app}/download", AuthLevel.Read, AddressOf UpdatesDownload)
            r.Map("GET", "/api/updates/{app}/zip", AuthLevel.Read, AddressOf UpdatesZip)
            r.Map("GET", "/api/updates/{app}/files/{*path}", AuthLevel.Read, AddressOf UpdatesFile)
        End Sub

        Private Function UpdatesPackage(rc As RequestContext) As UpdatePackage
            Dim app = If(rc.Route("app"), "").ToLowerInvariant()
            If Not _h.Updates.Enabled Then Throw New ApiException(409, "UPDATES_DISABLED", "Client updates are turned off on this server (Updates.Enabled).")
            Dim p = _h.Updates.GetPackage(app)
            If p Is Nothing Then Throw ApiException.NotFound("NO_UPDATE", $"No build of '{app}' is published on this server.")
            Return p
        End Function

        Private Shared Function PackageJson(p As UpdatePackage) As Object
            Return New With {
                .app = p.App, .baseApp = p.BaseApp, .platform = p.Platform, .archiveType = UpdateService.ArchiveType(p),
                .version = p.Version, .fileName = p.FileName, .sha256 = p.Sha256, .size = p.Size, .publishedUtc = p.PublishedUtc,
                .notes = p.Notes, .minimumVersion = p.MinimumVersion, .downloadUrl = $"/api/updates/{p.App}/download",
                .zipUrl = $"/api/updates/{p.App}/zip", .zipName = UpdateService.ZipName(p),
                .files = p.Files.Select(Function(f) New With {.path = f.Path, .sha256 = f.Sha256, .size = f.Size,
                                                              .url = $"/api/updates/{p.App}/files/" & String.Join("/", f.Path.Split("/"c).Select(AddressOf Uri.EscapeDataString))}).ToList(),
                .managedFolders = p.ManagedFolders, .filesHash = p.FilesHash}
        End Function

        ''' <summary>Every published app with its newest build.</summary>
        Private Async Function UpdatesList(rc As RequestContext) As Task
            Dim apps = If(_h.Updates.Enabled, _h.Updates.ListPackages(), New List(Of UpdatePackage))
            ' The page offers each app as a zip: have it ready before the first click.
            For Each p In apps
                _h.Updates.WarmZip(p)
            Next
            RecordReleases(apps)
            Await rc.OkAsync(New With {.enabled = _h.Updates.Enabled, .checkIntervalMinutes = _h.Updates.CheckIntervalMinutes,
                                       .directory = If(rc.User.IsAdmin, _h.Updates.Directory, Nothing),
                                       .server = New With {.version = _h.Version, .startedUtc = _h.StartedUtc, .build = HttpAdminServer.BuildId},
                                       .origin = PublicOrigin(rc), .publicInstall = _h.Updates.Enabled AndAlso _h.Updates.PublicInstall,
                                       .installers = _h.Updates.Installers().Select(Function(i) New With {
                                           .app = i.App, .platform = i.Platform, .version = i.Version, .fileName = i.FileName, .size = i.Size, .sha256 = i.Sha256,
                                           .publishedUtc = i.PublishedUtc,
                                           .url = If(_h.Updates.PublicInstall, "/install/desktop.msi", "/api/updates/installers/desktop") & If(i.Platform = "win-arm64", "?arch=arm64", ""),
                                           .isPublic = _h.Updates.PublicInstall}).ToList(),
                                       .apps = apps.Select(AddressOf PackageJson).ToList(),
                                       .changelog = _h.Updates.Changelog.Entries().Select(Function(e) New With {
                                           .app = e.App, .version = e.Version, .date = e.DateUtc, .title = e.Title, .features = e.Features,
                                           .fixes = e.Fixes, .notes = e.Notes, .build = e.Build}).ToList()}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Versions not in the changelog yet get an entry from their release notes: one per app (its Windows or any-OS
        ''' build, else the first platform), and one for this server's own version.
        ''' </summary>
        Private Sub RecordReleases(apps As List(Of UpdatePackage))
            Dim log = _h.Updates.Changelog
            For Each g In apps.GroupBy(Function(p) If(p.BaseApp, p.App), StringComparer.OrdinalIgnoreCase)
                Dim p = g.OrderBy(Function(x) If(x.Platform = "any" OrElse x.Platform.StartsWith("win", StringComparison.Ordinal), 0, 1)).First()
                If p.Version <> "0.0.0" Then log.Ensure(g.Key, p.Version, p.PublishedUtc, p.Notes)
            Next
            If _h.Updates.Enabled Then log.Ensure("server", _h.Version, _h.StartedUtc, Nothing, HttpAdminServer.BuildId)
        End Sub

        ''' <summary>
        ''' GET /api/updates/{app}?version=&amp;sha256=&amp;installId=&amp;machine= : is this install outdated? Records the install
        ''' (with installId) so admins can see who runs which version.
        ''' </summary>
        Private Async Function UpdatesCheck(rc As RequestContext) As Task
            Dim p = UpdatesPackage(rc)
            Dim version = rc.QueryString("version")
            Dim sha = rc.QueryString("sha256")
            Dim verdict = UpdateService.Evaluate(p, version, sha)
            Await _h.Updates.RecordInstallAsync(p.App, rc.QueryString("installId"), rc.User.Name, rc.QueryString("machine"), version, sha, rc.RemoteAddress).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .app = p.App, .current = New With {.version = version, .sha256 = sha}, .updateAvailable = verdict.UpdateAvailable,
                .mandatory = verdict.Mandatory, .reason = verdict.Reason, .checkIntervalMinutes = _h.Updates.CheckIntervalMinutes,
                .latest = PackageJson(p)}).ConfigureAwait(False)
        End Function

        Private Async Function UpdatesDownload(rc As RequestContext) As Task
            Dim p = UpdatesPackage(rc)
            Await SendFileAsync(rc, p.BinaryPath, "application/octet-stream", p.FileName, p.Sha256).ConfigureAwait(False)
        End Function

        ''' <summary>The whole app for a new install (the exe and its folders): a zip, or a .tar.gz for Linux; built once per build.</summary>
        Private Async Function UpdatesZip(rc As RequestContext) As Task
            Dim p = UpdatesPackage(rc)
            Dim z = Await _h.Updates.GetZipAsync(p, rc.CancellationToken).ConfigureAwait(False)
            Dim type = UpdateService.ArchiveType(p)
            Await SendFileAsync(rc, z.FullPath, If(type = "zip", "application/zip", If(type = "file", "application/octet-stream", "application/gzip")), UpdateService.ZipName(p), z.Sha256).ConfigureAwait(False)
        End Function

        Private Async Function UpdatesFile(rc As RequestContext) As Task
            Dim p = UpdatesPackage(rc)
            Dim rel = rc.Route("path")
            Dim full = _h.Updates.ResolveFile(p, rel)
            If full Is Nothing Then Throw ApiException.NotFound("NO_FILE", "The package has no such file.")
            Dim entry = p.Files.First(Function(f) f.Path.Equals(rel.Replace("\"c, "/"c).TrimStart("/"c), StringComparison.OrdinalIgnoreCase))
            ' Content files are small text and images; the browser may show them, but never run them as this site.
            rc.Http.Response.Headers("X-Content-Type-Options") = "nosniff"
            Await SendFileAsync(rc, full, "application/octet-stream", Path.GetFileName(full), entry.Sha256).ConfigureAwait(False)
        End Function

        ''' <summary>Streams a file (the response is not held in memory); X-Sha256 lets clients check what they got.</summary>
        Private Shared Async Function SendFileAsync(rc As RequestContext, file As String, contentType As String, downloadName As String, sha As String) As Task
            Using s As New FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 1 << 16, useAsync:=True)
                rc.MarkResponded()
                Dim resp = rc.Http.Response
                resp.StatusCode = 200
                resp.ContentType = contentType
                resp.ContentLength64 = s.Length
                resp.Headers("Cache-Control") = "no-store"
                resp.Headers("X-Sha256") = sha
                resp.Headers("ETag") = """" & sha & """"
                resp.Headers("Content-Disposition") = "attachment; filename=""" & downloadName.Replace("""", "") & """"
                Await s.CopyToAsync(resp.OutputStream, 1 << 16, rc.CancellationToken).ConfigureAwait(False)
            End Using
        End Function

        ''' <summary>Installs seen in the last ?days= (default 90), each marked current or outdated against the published build.</summary>
        Private Async Function UpdatesInstalls(rc As RequestContext) As Task
            Dim days = rc.QueryInt("days", 90, 1, 3650)
            Dim installs = Await _h.Updates.ListInstallsAsync(DateTime.UtcNow.AddDays(-days)).ConfigureAwait(False)
            Dim packages = If(_h.Updates.Enabled, _h.Updates.ListPackages(), New List(Of UpdatePackage)).ToDictionary(Function(x) x.App, StringComparer.OrdinalIgnoreCase)
            Dim rows = installs.Select(Function(i)
                                           Dim p As UpdatePackage = Nothing
                                           Dim state = "unknown"
                                           Dim latest As String = Nothing
                                           If packages.TryGetValue(i.App, p) Then
                                               latest = p.Version
                                               state = If(UpdateService.Evaluate(p, i.Version, i.Sha256).UpdateAvailable, "outdated", "current")
                                           End If
                                           Return New With {.app = i.App, .installId = i.InstallId, .username = i.Username, .machine = i.Machine, .version = i.Version,
                                                            .latestVersion = latest, .state = state, .remoteAddress = i.RemoteAddress,
                                                            .firstSeenUtc = i.FirstSeenUtc, .lastSeenUtc = i.LastSeenUtc}
                                       End Function).ToList()
            Await rc.OkAsync(New With {.days = days, .installs = rows, .outdated = rows.Where(Function(x) x.state = "outdated").Count()}).ConfigureAwait(False)
        End Function

    End Class

End Namespace
