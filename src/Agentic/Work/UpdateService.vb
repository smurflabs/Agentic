Option Strict On
Option Explicit On
Option Infer On

Imports System.Formats.Tar
Imports System.IO
Imports System.IO.Compression
Imports System.Security.Cryptography
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Logging
Imports Agentic.Persistence

Namespace Work

    ''' <summary>One file an app keeps next to its exe (e.g. www/desktop/desktop.js).</summary>
    Public NotInheritable Class UpdateFile
        Public Property Path As String
        Public Property Sha256 As String
        Public Property Size As Long
    End Class

    ''' <summary>The newest build of one client app for one platform, as published in the updates folder.</summary>
    Public NotInheritable Class UpdatePackage
        ''' <summary>The update channel (the folder): "cli", "desktop", "cli-linux-x64" …</summary>
        Public Property App As String
        ''' <summary>The app without the platform ("cli").</summary>
        Public Property BaseApp As String
        ''' <summary>"win-x64" (folders without a platform), "linux-x64" …</summary>
        Public Property Platform As String = "win-x64"
        Public Property FileName As String
        Public Property Version As String
        Public Property Sha256 As String
        Public Property Size As Long
        Public Property PublishedUtc As DateTime
        Public Property Notes As String
        ''' <summary>Installs older than MinimumVersion must update (clients install it without waiting).</summary>
        Public Property MinimumVersion As String
        Public Property Files As New List(Of UpdateFile)
        ''' <summary>Folders the package owns: clients delete files in them that the package no longer has.</summary>
        Public Property ManagedFolders As New List(Of String)
        ''' <summary>SHA-256 over every file's path and hash: equal means the folders are identical.</summary>
        Public Property FilesHash As String
        Friend Property Root As String
        Friend Property BinaryPath As String
    End Class

    Public NotInheritable Class ClientInstall
        Public Property App As String
        Public Property InstallId As String
        Public Property Username As String
        Public Property Machine As String
        Public Property Version As String
        Public Property Sha256 As String
        Public Property RemoteAddress As String
        Public Property FirstSeenUtc As DateTime
        Public Property LastSeenUtc As DateTime
    End Class

    ''' <summary>An installer in updates\installers, e.g. AgenticDesktop-0.4.13-x64.msi.</summary>
    Public NotInheritable Class InstallerFile
        Public Property App As String
        Public Property Platform As String
        Public Property Version As String
        Public Property FileName As String
        Public Property Size As Long
        Public Property Sha256 As String
        Public Property PublishedUtc As DateTime
        Friend Property FullPath As String
    End Class

    ''' <summary>
    ''' Client app updates. The updates folder has one sub-folder per app and platform:
    '''   updates\cli\agentic.exe                                   Windows (a folder without a platform)
    '''   updates\desktop\AgenticClient.exe + desktop\www\desktop\*  (mirrored into the app's folder)
    '''   updates\cli-linux-x64\agentic, updates\desktop-linux-x64\AgenticClient + www\desktop\*   Linux
    '''   updates\vscode\agentic-vscode.vsix + release.json {file, version}   the VS Code extension (any platform, as is)
    '''   updates\&lt;app&gt;\release.json   optional: {version, notes, minimumVersion, file}; Linux builds need "version"
    ''' Each platform's apps ask for their own folder ("cli-linux-x64").
    ''' A client is outdated when the server's build is a newer version, or the same version with a different SHA-256 (a rebuild).
    ''' Hashes are cached until a file's size or time changes. New installs download the whole app as a zip (built once per
    ''' build; a .tar.gz with the binary executable for Linux); installed copies update from the exe and the single files.
    ''' </summary>
    Public NotInheritable Class UpdateService

        Private Shared ReadOnly AppPattern As New Regex("^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.Compiled)
        Private Shared ReadOnly InstallPattern As New Regex("^[A-Za-z0-9_.:-]{1,64}$", RegexOptions.Compiled)
        ''' <summary>"cli-linux-x64" = app "cli" for platform "linux-x64".</summary>
        Private Shared ReadOnly PlatformSuffix As New Regex("^(?<app>[a-z0-9_-]+?)-(?<rid>(?:linux|osx|win)(?:-musl)?-(?:x64|x86|arm64|arm))$", RegexOptions.Compiled)
        ''' <summary>Never shipped to clients, whatever an admin copies into the folder (they hold keys and per-install settings).</summary>
        Private Shared ReadOnly BlockedNames As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "config.json", "mcp.json", "settings.json", "secrets.bin", "release.json", "desktop.ini", "thumbs.db"}

        Private ReadOnly _db As Database
        Private ReadOnly _log As ILog
        Private ReadOnly _cache As New Dictionary(Of String, (Stamp As String, Package As UpdatePackage))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _hashes As New Dictionary(Of String, (Stamp As String, Sha As String))(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _zipDir As String
        Private ReadOnly _zipLock As New SemaphoreSlim(1, 1)
        Private ReadOnly _warming As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        ''' <param name="zipCache">Where the apps' zips are kept (default: a folder in the temp directory).</param>
        Public Sub New(directory As String, enabled As Boolean, checkIntervalMinutes As Integer, db As Database, log As ILog, Optional zipCache As String = Nothing)
            Me.Directory = directory
            Me.Enabled = enabled
            Me.CheckIntervalMinutes = Math.Max(5, checkIntervalMinutes)
            _db = db
            _log = log
            _zipDir = If(String.IsNullOrWhiteSpace(zipCache), IO.Path.Combine(IO.Path.GetTempPath(), "agentic-update-zips"), zipCache)
            Changelog = New ReleaseChangelog(IO.Path.Combine(If(directory, ""), "changelog.json"), log)
        End Sub

        ''' <summary>What each version of each app added (changelog.json in the updates folder).</summary>
        Public ReadOnly Property Changelog As ReleaseChangelog

        Public ReadOnly Property Directory As String
        Public ReadOnly Property Enabled As Boolean
        Public ReadOnly Property CheckIntervalMinutes As Integer
        ''' <summary>Updates.PublicInstall: the install scripts, the CLI binaries and the installers download without signing in.</summary>
        Public Property PublicInstall As Boolean = True

        Public Shared Function IsValidApp(app As String) As Boolean
            Return app IsNot Nothing AndAlso AppPattern.IsMatch(app) AndAlso app <> "installs"
        End Function

        ''' <summary>Apps that are one file for every platform (the VS Code extension's .vsix): platform "any", handed out as is.</summary>
        Private Shared ReadOnly PortableApps As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {"vscode"}

        ''' <summary>The app and platform of an update channel: "cli-linux-x64" → (cli, linux-x64); "cli" → (cli, win-x64); "vscode" → (vscode, any).</summary>
        Public Shared Function SplitApp(app As String) As (BaseApp As String, Platform As String)
            If PortableApps.Contains(If(app, "")) Then Return (app, "any")
            Dim m = PlatformSuffix.Match(If(app, ""))
            Return If(m.Success, (m.Groups("app").Value, m.Groups("rid").Value), (app, "win-x64"))
        End Function

        ''' <summary>New installs of Windows builds get a zip; other platforms a .tar.gz (it keeps the binary executable); a
        ''' one-file app for any platform ("file") is downloaded as it is.</summary>
        Public Shared Function ArchiveType(p As UpdatePackage) As String
            If p.Platform = "any" Then Return "file"
            Return If(p.Platform.StartsWith("win", StringComparison.Ordinal), "zip", "tar.gz")
        End Function

        Public Function ListPackages() As List(Of UpdatePackage)
            Dim list As New List(Of UpdatePackage)
            If Not Enabled OrElse Not IO.Directory.Exists(Directory) Then Return list
            For Each d In IO.Directory.GetDirectories(Directory).OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)
                Dim p = GetPackage(IO.Path.GetFileName(d).ToLowerInvariant())
                If p IsNot Nothing Then list.Add(p)
            Next
            Return list
        End Function

        Private Shared ReadOnly InstallerName As New Regex("^AgenticDesktop-(?<v>\d+(?:\.\d+){1,3})-(?<arch>x64|arm64)\.msi$", RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        ''' <summary>
        ''' The newest installer of each app and platform in updates\installers (tools\publish-clients.ps1 writes
        ''' AgenticDesktop-&lt;version&gt;-&lt;x64|arm64&gt;.msi there). That folder has no app binary, so it is never an update channel.
        ''' </summary>
        Public Function Installers() As List(Of InstallerFile)
            Dim list As New List(Of InstallerFile)
            Dim dir = IO.Path.Combine(If(Directory, ""), "installers")
            If Not Enabled OrElse Not IO.Directory.Exists(dir) Then Return list
            For Each f In IO.Directory.GetFiles(dir, "*.msi", SearchOption.TopDirectoryOnly)
                Dim m = InstallerName.Match(IO.Path.GetFileName(f))
                If Not m.Success Then Continue For
                Dim info As New FileInfo(f)
                list.Add(New InstallerFile With {.App = "desktop", .Platform = "win-" & m.Groups("arch").Value.ToLowerInvariant(), .Version = m.Groups("v").Value,
                                                 .FileName = info.Name, .Size = info.Length, .PublishedUtc = info.LastWriteTimeUtc, .FullPath = f})
            Next
            Dim newest = list.GroupBy(Function(x) x.App & "|" & x.Platform).
                Select(Function(g) g.OrderByDescending(Function(x) ParseVersion(x.Version)).ThenByDescending(Function(x) x.PublishedUtc).First()).ToList()
            For Each i In newest
                i.Sha256 = HashFile(i.FullPath)
            Next
            Return newest.OrderBy(Function(x) x.App).ThenBy(Function(x) x.Platform).ToList()
        End Function

        ''' <summary>The app's package, or Nothing when there is none (no folder, no exe).</summary>
        Public Function GetPackage(app As String) As UpdatePackage
            If Not Enabled OrElse Not IsValidApp(app) Then Return Nothing
            Dim root = IO.Path.Combine(Directory, app)
            If Not IO.Directory.Exists(root) Then Return Nothing
            Try
                Dim release = ReadRelease(root)
                Dim binary = FindBinary(root, JsonString(release, "file"))
                If binary Is Nothing Then Return Nothing
                Dim contents = ContentFiles(root, binary)
                Dim stamp As New StringBuilder()
                stamp.Append(FileStamp(binary)).Append("|"c).Append(FileStamp(IO.Path.Combine(root, "release.json")))
                For Each f In contents
                    stamp.Append("|"c).Append(f).Append(":"c).Append(FileStamp(f))
                Next
                SyncLock _cache
                    Dim hit As (Stamp As String, Package As UpdatePackage) = Nothing
                    If _cache.TryGetValue(app, hit) AndAlso hit.Stamp = stamp.ToString() Then Return hit.Package
                End SyncLock

                Dim info = New FileInfo(binary)
                Dim split = SplitApp(app)
                Dim p As New UpdatePackage With {
                    .App = app, .BaseApp = split.BaseApp, .Platform = split.Platform,
                    .Root = root, .BinaryPath = binary, .FileName = info.Name, .Size = info.Length, .PublishedUtc = info.LastWriteTimeUtc,
                    .Sha256 = HashFile(binary), .Notes = JsonString(release, "notes"), .MinimumVersion = NormalizeVersionText(JsonString(release, "minimumVersion")),
                    .Version = If(NormalizeVersionText(JsonString(release, "version")), If(BinaryVersion(binary), "0.0.0"))}
                If p.Version = "0.0.0" Then _log.Warn("Updates", $"The build in {root} has no version: add ""version"" to its release.json (Linux binaries carry none the server can read).")
                For Each f In contents
                    Dim rel = IO.Path.GetRelativePath(root, f).Replace("\"c, "/"c)
                    p.Files.Add(New UpdateFile With {.Path = rel, .Size = New FileInfo(f).Length, .Sha256 = HashFile(f)})
                Next
                p.Files = p.Files.OrderBy(Function(x) x.Path, StringComparer.Ordinal).ToList()
                p.ManagedFolders = p.Files.Select(Function(x) If(x.Path.Contains("/"c), x.Path.Substring(0, x.Path.LastIndexOf("/"c)), "")).
                    Where(Function(x) x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Function(x) x, StringComparer.Ordinal).ToList()
                p.FilesHash = FilesHashOf(p.Files.Select(Function(x) (x.Path, x.Sha256)))
                SyncLock _cache
                    _cache(app) = (stamp.ToString(), p)
                End SyncLock
                Return p
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                _log.Warn("Updates", $"The update package '{app}' could not be read: {ex.Message}")
                Return Nothing
            End Try
        End Function

        ''' <summary>A package file (the exe or a content file) by its path in the package, or Nothing.</summary>
        Public Function ResolveFile(p As UpdatePackage, relativePath As String) As String
            If p Is Nothing OrElse String.IsNullOrWhiteSpace(relativePath) Then Return Nothing
            Dim wanted = relativePath.Replace("\"c, "/"c).TrimStart("/"c)
            Dim f = p.Files.FirstOrDefault(Function(x) x.Path.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            If f Is Nothing Then Return Nothing
            Dim full = IO.Path.GetFullPath(IO.Path.Combine(p.Root, f.Path))
            Return If(full.StartsWith(IO.Path.GetFullPath(p.Root) & IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) AndAlso File.Exists(full), full, Nothing)
        End Function

        ''' <summary>Whether a client running clientVersion (with an exe whose SHA-256 is clientSha) should install the package.</summary>
        Public Shared Function Evaluate(p As UpdatePackage, clientVersion As String, clientSha As String) As (UpdateAvailable As Boolean, Mandatory As Boolean, Reason As String)
            Dim client = ParseVersion(clientVersion)
            Dim mandatory = p.MinimumVersion IsNot Nothing AndAlso (client Is Nothing OrElse client < ParseVersion(p.MinimumVersion))
            Dim cmp = If(client Is Nothing, 1, ParseVersion(p.Version).CompareTo(client))
            If cmp > 0 Then Return (True, mandatory, $"version {p.Version} is available")
            If cmp < 0 Then Return (False, False, "this build is newer than the server's")
            If Not String.IsNullOrWhiteSpace(clientSha) AndAlso Not clientSha.Trim().Equals(p.Sha256, StringComparison.OrdinalIgnoreCase) Then
                Return (True, mandatory, $"a new build of {p.Version} is available")
            End If
            Return (False, False, "up to date")
        End Function

        ' ------------------------------------------------------------------ zip (new installs)

        ''' <summary>The app's name in archives: Agentic-Desktop, Agentic-CLI.</summary>
        Private Shared Function DisplayName(p As UpdatePackage) As String
            Dim app = If(p.BaseApp, p.App)
            Return If(app = "desktop", "Agentic-Desktop", If(app = "cli", "Agentic-CLI", IO.Path.GetFileNameWithoutExtension(p.FileName)))
        End Function

        ''' <summary>The archive's download name: Agentic-Desktop-0.4.0.zip, Agentic-CLI-2.2.0-linux-x64.tar.gz; a one-file app
        ''' keeps its own name with the version (agentic-vscode-0.2.0.vsix).</summary>
        Public Shared Function ZipName(p As UpdatePackage) As String
            Select Case ArchiveType(p)
                Case "file" : Return $"{IO.Path.GetFileNameWithoutExtension(p.FileName)}-{p.Version}{IO.Path.GetExtension(p.FileName)}"
                Case "zip" : Return $"{DisplayName(p)}-{p.Version}.zip"
                Case Else : Return $"{DisplayName(p)}-{p.Version}-{p.Platform}.tar.gz"
            End Select
        End Function

        Private Function ZipPath(p As UpdatePackage) As String
            Return IO.Path.Combine(_zipDir, $"{p.App}-{p.Sha256.Substring(0, 16)}-{p.FilesHash.Substring(0, 16)}.{ArchiveType(p)}")
        End Function

        ''' <summary>
        ''' The whole app in one zip for a new install: the exe and its folders (www\desktop …) laid out as the app keeps them.
        ''' Built once per build and kept in the cache folder.
        ''' </summary>
        Public Async Function GetZipAsync(p As UpdatePackage, ct As CancellationToken) As Task(Of (FullPath As String, Sha256 As String))
            ' A one-file app is its own install: no archive to build.
            If ArchiveType(p) = "file" Then Return (p.BinaryPath, p.Sha256)
            Dim target = ZipPath(p)
            If Not File.Exists(target) Then
                Await _zipLock.WaitAsync(ct).ConfigureAwait(False)
                Try
                    If Not File.Exists(target) Then Await Task.Run(Sub() BuildZip(p, target), ct).ConfigureAwait(False)
                Finally
                    _zipLock.Release()
                End Try
            End If
            Return (target, HashFile(target))
        End Function

        ''' <summary>Builds the package's zip in the background unless it exists, so the first download does not wait for it.</summary>
        Public Sub WarmZip(p As UpdatePackage)
            If ArchiveType(p) = "file" Then Return
            Dim target = ZipPath(p)
            If File.Exists(target) Then Return
            SyncLock _warming
                If Not _warming.Add(target) Then Return
            End SyncLock
            Dim unused = Task.Run(Async Function()
                                      Try
                                          Await GetZipAsync(p, CancellationToken.None).ConfigureAwait(False)
                                      Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException OrElse TypeOf ex Is InvalidDataException
                                          _log.Warn("Updates", $"The zip of '{p.App}' could not be built: {ex.Message}")
                                      Finally
                                          SyncLock _warming
                                              _warming.Remove(target)
                                          End SyncLock
                                      End Try
                                  End Function)
        End Sub

        Private Sub BuildZip(p As UpdatePackage, target As String)
            IO.Directory.CreateDirectory(_zipDir)
            Dim temp = target & ".partial"
            If ArchiveType(p) = "zip" Then
                Using fs As New FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None), zip As New ZipArchive(fs, ZipArchiveMode.Create)
                    zip.CreateEntryFromFile(p.BinaryPath, p.FileName, CompressionLevel.Optimal)
                    For Each f In p.Files
                        Dim full = ResolveFile(p, f.Path)
                        If full IsNot Nothing Then zip.CreateEntryFromFile(full, f.Path, CompressionLevel.Optimal)
                    Next
                End Using
            Else
                BuildTarGz(p, temp)
            End If
            File.Move(temp, target, True)
            ' Archives of earlier builds are not needed any more.
            For Each old In IO.Directory.GetFiles(_zipDir, p.App & "-*." & ArchiveType(p))
                If old.Equals(target, StringComparison.OrdinalIgnoreCase) Then Continue For
                Try
                    File.Delete(old)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                End Try
            Next
        End Sub

        ''' <summary>
        ''' A .tar.gz for Linux: Agentic-CLI/agentic (executable) and its folders, so "tar xzf" gives a folder that is ready
        ''' to run (zip files lose the executable bit).
        ''' </summary>
        Private Sub BuildTarGz(p As UpdatePackage, target As String)
            Dim top = DisplayName(p)
            Dim exe = UnixFileMode.UserRead Or UnixFileMode.UserWrite Or UnixFileMode.UserExecute Or UnixFileMode.GroupRead Or
                      UnixFileMode.GroupExecute Or UnixFileMode.OtherRead Or UnixFileMode.OtherExecute
            Dim plain = UnixFileMode.UserRead Or UnixFileMode.UserWrite Or UnixFileMode.GroupRead Or UnixFileMode.OtherRead
            Using fs As New FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None),
                  gz As New GZipStream(fs, CompressionLevel.Optimal),
                  tar As New TarWriter(gz, TarEntryFormat.Pax, leaveOpen:=False)
                Dim folders = New List(Of String) From {top}
                folders.AddRange(p.ManagedFolders.SelectMany(Function(d)
                                                                 Dim parts = d.Split("/"c)
                                                                 Return Enumerable.Range(1, parts.Length).Select(Function(n) top & "/" & String.Join("/", parts.Take(n)))
                                                             End Function))
                For Each d In folders.Distinct(StringComparer.Ordinal)
                    tar.WriteEntry(New PaxTarEntry(TarEntryType.Directory, d & "/") With {.Mode = exe, .ModificationTime = p.PublishedUtc})
                Next
                Dim add = Sub(full As String, name As String, mode As UnixFileMode)
                              Using data = File.OpenRead(full)
                                  tar.WriteEntry(New PaxTarEntry(TarEntryType.RegularFile, top & "/" & name) With {
                                      .Mode = mode, .ModificationTime = File.GetLastWriteTimeUtc(full), .DataStream = data})
                              End Using
                          End Sub
                add(p.BinaryPath, p.FileName, exe)
                For Each f In p.Files
                    Dim full = ResolveFile(p, f.Path)
                    If full IsNot Nothing Then add(full, f.Path, plain)
                Next
            End Using
        End Sub

        ' ------------------------------------------------------------------ installs

        Public Async Function RecordInstallAsync(app As String, installId As String, username As String, machine As String, version As String, sha As String, remote As String) As Task
            If String.IsNullOrWhiteSpace(installId) OrElse Not InstallPattern.IsMatch(installId) Then Return
            Dim now = Db.Ts(DateTime.UtcNow)
            Await _db.ExecuteAsync(
                "INSERT INTO ClientInstalls(App, InstallId, Username, Machine, Version, Sha256, RemoteAddress, FirstSeenUtc, LastSeenUtc) VALUES (@a, @i, @u, @m, @v, @s, @r, @n, @n) " &
                "ON CONFLICT(App, InstallId) DO UPDATE SET Username=excluded.Username, Machine=excluded.Machine, Version=excluded.Version, Sha256=excluded.Sha256, " &
                "RemoteAddress=excluded.RemoteAddress, LastSeenUtc=excluded.LastSeenUtc",
                ("@a", CObj(app)), ("@i", installId), ("@u", Clip(username, 128)), ("@m", Clip(machine, 128)), ("@v", Clip(version, 64)),
                ("@s", Clip(sha, 64)), ("@r", Clip(remote, 64)), ("@n", now)).ConfigureAwait(False)
        End Function

        Public Function ListInstallsAsync(sinceUtc As DateTime) As Task(Of List(Of ClientInstall))
            Return _db.QueryAsync("SELECT * FROM ClientInstalls WHERE LastSeenUtc >= @t ORDER BY App, LastSeenUtc DESC",
                                  Function(r) New ClientInstall With {
                                      .App = Db.Str(r, "App"), .InstallId = Db.Str(r, "InstallId"), .Username = Db.Str(r, "Username"), .Machine = Db.Str(r, "Machine"),
                                      .Version = Db.Str(r, "Version"), .Sha256 = Db.Str(r, "Sha256"), .RemoteAddress = Db.Str(r, "RemoteAddress"),
                                      .FirstSeenUtc = Db.Dt(r, "FirstSeenUtc"), .LastSeenUtc = Db.Dt(r, "LastSeenUtc")},
                                  ("@t", CObj(Db.Ts(sinceUtc))))
        End Function

        ' ------------------------------------------------------------------ versions

        ''' <summary>"1.2" / "1.2.3" / "1.2.3.4" / "v1.2.3-beta+abc" as a four-part version (missing parts are 0), or Nothing.</summary>
        Public Shared Function ParseVersion(text As String) As Version
            Dim t = NormalizeVersionText(text)
            If t Is Nothing Then Return Nothing
            Dim parts = t.Split("."c).Select(Function(x) Integer.Parse(x, Globalization.CultureInfo.InvariantCulture)).ToList()
            While parts.Count < 4
                parts.Add(0)
            End While
            Return New Version(parts(0), parts(1), parts(2), parts(3))
        End Function

        Private Shared Function NormalizeVersionText(text As String) As String
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim m = Regex.Match(text.Trim(), "^v?(\d{1,9}(?:\.\d{1,9}){0,3})")
            Return If(m.Success, m.Groups(1).Value, Nothing)
        End Function

        ''' <summary>The exe's product version (InformationalVersion, without "+build" or text after it), else its file version.</summary>
        Private Shared Function BinaryVersion(file As String) As String
            Try
                Dim fv = FileVersionInfo.GetVersionInfo(file)
                Dim v = If(NormalizeVersionText(fv.ProductVersion), NormalizeVersionText(fv.FileVersion))
                If v IsNot Nothing AndAlso v.Split("."c).Length = 4 AndAlso v.EndsWith(".0", StringComparison.Ordinal) Then v = v.Substring(0, v.Length - 2)
                Return v
            Catch ex As FileNotFoundException
                Return Nothing
            End Try
        End Function

        ' ------------------------------------------------------------------ files

        Private Shared Function ReadRelease(root As String) As JsonElement
            Dim f = IO.Path.Combine(root, "release.json")
            If Not File.Exists(f) Then Return Nothing
            Try
                Using doc = JsonDocument.Parse(File.ReadAllText(f), New JsonDocumentOptions With {.CommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True})
                    Return doc.RootElement.Clone()
                End Using
            Catch ex As JsonException
                Return Nothing
            End Try
        End Function

        Private Shared Function JsonString(e As JsonElement, name As String) As String
            Dim v As JsonElement
            If e.ValueKind = JsonValueKind.Object AndAlso e.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrWhiteSpace(v.GetString()) Then Return v.GetString().Trim()
            Return Nothing
        End Function

        ''' <summary>release.json "file", else the only .exe in the app folder, else (Linux builds) its only file without an extension.</summary>
        Private Function FindBinary(root As String, named As String) As String
            If named IsNot Nothing Then
                Dim f = IO.Path.Combine(root, IO.Path.GetFileName(named))
                Return If(File.Exists(f), f, Nothing)
            End If
            Dim exes = IO.Directory.GetFiles(root, "*.exe", SearchOption.TopDirectoryOnly)
            If exes.Length = 1 Then Return exes(0)
            If exes.Length > 1 Then
                _log.Warn("Updates", $"{root} has {exes.Length} .exe files; name the app's exe in release.json (""file"").")
                Return Nothing
            End If
            Dim plain = IO.Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly).Where(
                Function(f)
                    Dim name = IO.Path.GetFileName(f)
                    Return IO.Path.GetExtension(name).Length = 0 AndAlso Not name.StartsWith("."c) AndAlso Not name.StartsWith("~"c) AndAlso Not BlockedNames.Contains(name)
                End Function).ToArray()
            If plain.Length = 1 Then Return plain(0)
            If plain.Length > 1 Then _log.Warn("Updates", $"{root} has {plain.Length} files that could be the app; name it in release.json (""file"").")
            Return Nothing
        End Function

        ''' <summary>Files in the app folder's sub-folders (the exe and release.json sit at the top), minus temporary and private files.</summary>
        Private Shared Function ContentFiles(root As String, binary As String) As List(Of String)
            Dim list As New List(Of String)
            For Each d In IO.Directory.GetDirectories(root)
                For Each f In IO.Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)
                    Dim name = IO.Path.GetFileName(f)
                    If BlockedNames.Contains(name) OrElse name.StartsWith("."c) OrElse name.StartsWith("~"c) OrElse
                       name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) OrElse name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) Then Continue For
                    If (File.GetAttributes(f) And (FileAttributes.Hidden Or FileAttributes.System)) <> 0 Then Continue For
                    list.Add(f)
                Next
            Next
            list.Sort(StringComparer.OrdinalIgnoreCase)
            Return list
        End Function

        Private Shared Function FileStamp(f As String) As String
            Dim i As New FileInfo(f)
            Return If(i.Exists, i.Length.ToString(Globalization.CultureInfo.InvariantCulture) & "@" & i.LastWriteTimeUtc.Ticks.ToString(Globalization.CultureInfo.InvariantCulture), "-")
        End Function

        Private Function HashFile(f As String) As String
            Dim stamp = FileStamp(f)
            SyncLock _hashes
                Dim hit As (Stamp As String, Sha As String) = Nothing
                If _hashes.TryGetValue(f, hit) AndAlso hit.Stamp = stamp Then Return hit.Sha
            End SyncLock
            Dim sha As String
            Using s As New FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite Or FileShare.Delete, 1 << 16)
                sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant()
            End Using
            SyncLock _hashes
                _hashes(f) = (stamp, sha)
            End SyncLock
            Return sha
        End Function

        ''' <summary>SHA-256 of "path\nsha\n" for every file in path order; clients compute the same over their copies.</summary>
        Public Shared Function FilesHashOf(files As IEnumerable(Of (Path As String, Sha As String))) As String
            Dim sb As New StringBuilder()
            For Each f In files.OrderBy(Function(x) x.Path, StringComparer.Ordinal)
                sb.Append(f.Path).Append(vbLf).Append(f.Sha.ToLowerInvariant()).Append(vbLf)
            Next
            Return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant()
        End Function

        Private Shared Function Clip(s As String, max As Integer) As String
            If String.IsNullOrWhiteSpace(s) Then Return Nothing
            s = s.Trim()
            Return If(s.Length > max, s.Substring(0, max), s)
        End Function

    End Class

End Namespace
