Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text
Imports System.Text.RegularExpressions
Imports Agentic.Work

Namespace Http

    ' One-line installs (Updates.PublicInstall, on by default), without signing in:
    '   irm <server>/install.ps1 | iex          the CLI on Windows (into %LOCALAPPDATA%\Programs\Agentic, added to PATH)
    '   curl -fsSL <server>/install.sh | bash   the CLI on Linux and macOS (into ~/.local/share/agentic, ~/.local/bin)
    '   <server>/install/bin/{channel}          the CLI binary the scripts download ("cli", "cli-linux-x64", ...)
    '   <server>/install/desktop.msi            the desktop app's Windows installer (?arch=arm64 for ARM)
    ' The scripts are written for the build published now: its version and SHA-256 are in them, and they check the download.
    Partial Friend NotInheritable Class AdminApi

        Private Shared ReadOnly CliChannel As New Regex("^cli(?:-(?:linux|osx|win)-(?:x64|arm64))?$", RegexOptions.Compiled)
        Private Shared ReadOnly SafeOrigin As New Regex("^https?://(?:[A-Za-z0-9.-]+|\[[0-9A-Fa-f:.]+\])(?::\d{1,5})?$", RegexOptions.Compiled)
        Private Shared ReadOnly SafeVersion As New Regex("^[0-9A-Za-z.+-]{1,64}$", RegexOptions.Compiled)

        Private Sub RegisterInstall(r As Router)
            r.Map("GET", "/install.ps1", AuthLevel.Anonymous, AddressOf InstallScriptPs1)
            r.Map("GET", "/install.sh", AuthLevel.Anonymous, AddressOf InstallScriptSh)
            r.Map("GET", "/install/bin/{app}", AuthLevel.Anonymous, AddressOf InstallBinary)
            r.Map("GET", "/install/desktop.msi", AuthLevel.Anonymous, AddressOf InstallDesktopMsi)
            r.Map("GET", "/api/updates/installers/desktop", AuthLevel.Read, AddressOf InstallDesktopMsiSignedIn)
        End Sub

        Private Sub RequirePublicInstall()
            If Not _h.Updates.Enabled OrElse Not _h.Updates.PublicInstall Then
                Throw ApiException.NotFound("NO_PUBLIC_INSTALL", "One-line installs are turned off on this server (Updates.PublicInstall).")
            End If
        End Sub

        ''' <summary>The address the scripts download from: PublicUrl, else the one this request came to; only a plain origin is ever put in a script.</summary>
        Private Function InstallOrigin(rc As RequestContext) As String
            Dim origin = PublicOrigin(rc)
            If SafeOrigin.IsMatch(origin) Then Return origin
            Dim fallback = rc.Http.Request.Url.Scheme & "://" & rc.Http.Request.Url.Authority
            If SafeOrigin.IsMatch(fallback) Then Return fallback
            Throw ApiException.BadRequest("This server's address cannot be put in an install script.")
        End Function

        ''' <summary>The published CLI builds the scripts offer, by channel.</summary>
        Private Function CliBuilds() As List(Of UpdatePackage)
            Return _h.Updates.ListPackages().Where(Function(p) CliChannel.IsMatch(p.App) AndAlso p.Version <> "0.0.0" AndAlso SafeVersion.IsMatch(p.Version)).ToList()
        End Function

        Private Shared Function Template(name As String) As String
            Dim asm = GetType(AdminApi).Assembly
            Using s = asm.GetManifestResourceStream("Install/" & name)
                If s Is Nothing Then Throw New InvalidOperationException("Missing embedded install script " & name)
                Using reader As New StreamReader(s, Encoding.UTF8)
                    Return reader.ReadToEnd()
                End Using
            End Using
        End Function

        ''' <summary>The Windows install script, for the builds published now.</summary>
        Friend Function BuildInstallPs1(origin As String, builds As List(Of UpdatePackage)) As String
            Dim lines As New StringBuilder()
            For Each p In builds
                Dim key = If(p.App = "cli" OrElse p.App = "cli-win-x64", "x64", If(p.App = "cli-win-arm64", "arm64", Nothing))
                If key Is Nothing Then Continue For
                lines.Append("        '").Append(key).Append("' = @{ version = '").Append(p.Version).Append("'; url = '/install/bin/").Append(p.App).
                    Append("'; sha256 = '").Append(p.Sha256.ToLowerInvariant()).Append("' }").Append(vbCrLf)
            Next
            Return Template("install.ps1").Replace(vbCrLf, vbLf).Replace(vbLf, vbCrLf).Replace("__SERVER__", origin).Replace("__BUILDS__" & vbCrLf, lines.ToString())
        End Function

        ''' <summary>The Linux and macOS install script, for the builds published now.</summary>
        Friend Function BuildInstallSh(origin As String, builds As List(Of UpdatePackage)) As String
            Dim lines As New StringBuilder()
            For Each p In builds.Where(Function(x) x.App.StartsWith("cli-linux-", StringComparison.Ordinal) OrElse x.App.StartsWith("cli-osx-", StringComparison.Ordinal))
                lines.Append("  ").Append(p.App).Append(") version='").Append(p.Version).Append("'; sha='").Append(p.Sha256.ToLowerInvariant()).Append("' ;;").Append(vbLf)
            Next
            Return Template("install.sh").Replace(vbCrLf, vbLf).Replace("__SERVER__", origin).Replace("__BUILDS__" & vbLf, lines.ToString())
        End Function

        Private Async Function InstallScriptPs1(rc As RequestContext) As Task
            RequirePublicInstall()
            Dim script = BuildInstallPs1(InstallOrigin(rc), CliBuilds())
            rc.Http.Response.Headers("Cache-Control") = "no-store"
            Await rc.WriteBytesAsync(200, "text/plain; charset=utf-8", New UTF8Encoding(False).GetBytes(script)).ConfigureAwait(False)
        End Function

        Private Async Function InstallScriptSh(rc As RequestContext) As Task
            RequirePublicInstall()
            Dim script = BuildInstallSh(InstallOrigin(rc), CliBuilds())
            rc.Http.Response.Headers("Cache-Control") = "no-store"
            Await rc.WriteBytesAsync(200, "text/plain; charset=utf-8", New UTF8Encoding(False).GetBytes(script)).ConfigureAwait(False)
        End Function

        ''' <summary>A CLI build for the install scripts (only CLI channels: the other apps install from their archives or the MSI).</summary>
        Private Async Function InstallBinary(rc As RequestContext) As Task
            RequirePublicInstall()
            Dim app = If(rc.Route("app"), "").ToLowerInvariant()
            If Not CliChannel.IsMatch(app) Then Throw ApiException.NotFound("NO_BUILD", "Only the CLI installs this way.")
            Dim p = _h.Updates.GetPackage(app)
            If p Is Nothing Then Throw ApiException.NotFound("NO_BUILD", $"No build of '{app}' is published on this server.")
            Await SendFileAsync(rc, p.BinaryPath, "application/octet-stream", p.FileName, p.Sha256).ConfigureAwait(False)
        End Function

        Private Function InstallDesktopMsi(rc As RequestContext) As Task
            RequirePublicInstall()
            Return SendDesktopMsiAsync(rc)
        End Function

        ''' <summary>The same installer for signed-in users when one-line installs are off.</summary>
        Private Function InstallDesktopMsiSignedIn(rc As RequestContext) As Task
            If Not _h.Updates.Enabled Then Throw New ApiException(409, "UPDATES_DISABLED", "Client updates are turned off on this server (Updates.Enabled).")
            Return SendDesktopMsiAsync(rc)
        End Function

        Private Async Function SendDesktopMsiAsync(rc As RequestContext) As Task
            Dim platform = If(String.Equals(rc.QueryString("arch"), "arm64", StringComparison.OrdinalIgnoreCase), "win-arm64", "win-x64")
            Dim msi = _h.Updates.Installers().FirstOrDefault(Function(i) i.App = "desktop" AndAlso i.Platform = platform)
            If msi Is Nothing Then Throw ApiException.NotFound("NO_INSTALLER", "No installer of the desktop app is published on this server.")
            Await SendFileAsync(rc, msi.FullPath, "application/x-msi", msi.FileName, msi.Sha256).ConfigureAwait(False)
        End Function

    End Class

End Namespace
