Option Strict On
Option Explicit On
Option Infer On

Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Security.Cryptography
Imports System.Text

''' <summary>
''' Builds a per-user MSI from a folder: every file in it (sub-folders included) goes to
''' %LOCALAPPDATA%\Programs\&lt;folder&gt;, with Start menu and desktop shortcuts to the app's exe. Per-user because the app
''' updates itself in place, so its folder must stay writable; no administrator rights or UAC prompt are needed.
''' A newer MSI (same upgrade code) replaces an older one; an older one refuses to install over a newer one.
''' Uninstalling removes the files, also those the app's own updates added to the known folders.
'''
''' Usage: MsiBuilder --source &lt;dir&gt; --exe AgenticClient.exe --name "Agentic Desktop" --manufacturer Agentic
'''          --version 0.4.13 --upgrade-code {GUID} --out AgenticDesktop-0.4.13-x64.msi [--folder "Agentic Desktop"] [--arch x64|arm64]
''' </summary>
Module Program

    Private Const MSIDBOPEN_CREATE As Integer = 3

    <DllImport("msi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function MsiOpenDatabaseW(path As String, persist As IntPtr, ByRef handle As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function MsiDatabaseOpenViewW(db As IntPtr, query As String, ByRef view As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiViewExecute(view As IntPtr, record As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiViewClose(view As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiCloseHandle(handle As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiCreateRecord(fields As UInteger) As IntPtr
    End Function
    <DllImport("msi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function MsiRecordSetStringW(record As IntPtr, field As UInteger, value As String) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiRecordSetInteger(record As IntPtr, field As UInteger, value As Integer) As UInteger
    End Function
    <DllImport("msi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function MsiRecordSetStreamW(record As IntPtr, field As UInteger, filePath As String) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiDatabaseCommit(db As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function MsiGetSummaryInformationW(db As IntPtr, path As String, updateCount As UInteger, ByRef summary As IntPtr) As UInteger
    End Function
    <DllImport("msi.dll", CharSet:=CharSet.Unicode, ExactSpelling:=True)>
    Private Function MsiSummaryInfoSetPropertyW(summary As IntPtr, prop As UInteger, dataType As UInteger, intValue As Integer, ByRef time As ComTypes.FILETIME, text As String) As UInteger
    End Function
    <DllImport("msi.dll", ExactSpelling:=True)>
    Private Function MsiSummaryInfoPersist(summary As IntPtr) As UInteger
    End Function

    Private _db As IntPtr

    Private Sub Check(code As UInteger, what As String)
        If code <> 0 Then Throw New InvalidOperationException($"{what} failed (Windows Installer error {code}).")
    End Sub

    Private Sub Exec(sql As String)
        Dim view As IntPtr
        Check(MsiDatabaseOpenViewW(_db, sql, view), sql)
        Try
            Check(MsiViewExecute(view, IntPtr.Zero), sql)
        Finally
            MsiViewClose(view)
            MsiCloseHandle(view)
        End Try
    End Sub

    ''' <summary>Inserts a row: Nothing = null, Integer = a number, a String starting with "@stream:" = that file's bytes.</summary>
    Private Sub Insert(table As String, columns As String(), ParamArray values As Object())
        Dim sql = $"INSERT INTO `{table}` ({String.Join(", ", columns.Select(Function(c) "`" & c & "`"))}) VALUES ({String.Join(", ", columns.Select(Function(c) "?"))})"
        Dim rec = MsiCreateRecord(CUInt(values.Length))
        Try
            For i = 0 To values.Length - 1
                Dim field = CUInt(i + 1)
                Dim v = values(i)
                If v Is Nothing Then Continue For
                If TypeOf v Is Integer Then
                    Check(MsiRecordSetInteger(rec, field, CInt(v)), $"{table}.{columns(i)}")
                ElseIf CStr(v).StartsWith("@stream:", StringComparison.Ordinal) Then
                    Check(MsiRecordSetStreamW(rec, field, CStr(v).Substring(8)), $"{table}.{columns(i)} stream")
                Else
                    Check(MsiRecordSetStringW(rec, field, CStr(v)), $"{table}.{columns(i)}")
                End If
            Next
            Dim view As IntPtr
            Check(MsiDatabaseOpenViewW(_db, sql, view), sql)
            Try
                Check(MsiViewExecute(view, rec), $"insert into {table}")
            Finally
                MsiViewClose(view)
                MsiCloseHandle(view)
            End Try
        Finally
            MsiCloseHandle(rec)
        End Try
    End Sub

    Private ReadOnly Schema As String() = {
        "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR NOT NULL LOCALIZABLE PRIMARY KEY `Property`)",
        "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)",
        "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)",
        "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)",
        "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)",
        "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` LONG NOT NULL PRIMARY KEY `File`)",
        "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)",
        "CREATE TABLE `Registry` (`Registry` CHAR(72) NOT NULL, `Root` SHORT NOT NULL, `Key` CHAR(255) NOT NULL LOCALIZABLE, `Name` CHAR(255) LOCALIZABLE, `Value` LONGCHAR LOCALIZABLE, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Registry`)",
        "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)",
        "CREATE TABLE `RemoveFile` (`FileKey` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) LOCALIZABLE, `DirProperty` CHAR(72) NOT NULL, `InstallMode` SHORT NOT NULL PRIMARY KEY `FileKey`)",
        "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)",
        "CREATE TABLE `LaunchCondition` (`Condition` CHAR(255) NOT NULL, `Description` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Condition`)",
        "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
        "CREATE TABLE `InstallUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
        "CREATE TABLE `AdminExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)",
        "CREATE TABLE `AdminUISequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)"}

    Private ReadOnly ExecuteSequence As (String, Integer)() = {
        ("FindRelatedProducts", 25), ("LaunchConditions", 100), ("ValidateProductID", 700), ("CostInitialize", 800), ("FileCost", 900),
        ("CostFinalize", 1000), ("MigrateFeatureStates", 1200), ("InstallValidate", 1400), ("RemoveExistingProducts", 1401),
        ("InstallInitialize", 1500), ("ProcessComponents", 1600), ("UnpublishFeatures", 1800), ("RemoveRegistryValues", 2600),
        ("RemoveShortcuts", 3200), ("RemoveFiles", 3500), ("RemoveFolders", 3600), ("CreateFolders", 3700), ("InstallFiles", 4000),
        ("CreateShortcuts", 4500), ("WriteRegistryValues", 5000), ("RegisterUser", 6000), ("RegisterProduct", 6100),
        ("PublishFeatures", 6300), ("PublishProduct", 6400), ("InstallFinalize", 6600)}
    Private ReadOnly UiSequence As (String, Integer)() = {
        ("FindRelatedProducts", 25), ("LaunchConditions", 100), ("ValidateProductID", 700), ("CostInitialize", 800), ("FileCost", 900),
        ("CostFinalize", 1000), ("MigrateFeatureStates", 1200), ("ExecuteAction", 1300)}
    Private ReadOnly AdminExecute As (String, Integer)() = {
        ("CostInitialize", 800), ("FileCost", 900), ("CostFinalize", 1000), ("InstallValidate", 1400), ("InstallInitialize", 1500),
        ("InstallAdminPackage", 3900), ("InstallFiles", 4000), ("InstallFinalize", 6600)}
    Private ReadOnly AdminUi As (String, Integer)() = {("CostInitialize", 800), ("FileCost", 900), ("CostFinalize", 1000), ("ExecuteAction", 1300)}

    Function Main(args As String()) As Integer
        Try
            Dim a = ParseArgs(args)
            Build(a("source"), a("exe"), a("name"), a("manufacturer"), a("version"), a("upgrade-code"), a("out"),
                  If(a.ContainsKey("folder"), a("folder"), a("name")), If(a.ContainsKey("arch"), a("arch"), "x64"))
            Return 0
        Catch ex As Exception When TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is ArgumentException OrElse TypeOf ex Is KeyNotFoundException
            Console.Error.WriteLine("MsiBuilder: " & ex.Message)
            Return 1
        End Try
    End Function

    Private Function ParseArgs(args As String()) As Dictionary(Of String, String)
        Dim d As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        For i = 0 To args.Length - 2 Step 2
            If Not args(i).StartsWith("--", StringComparison.Ordinal) Then Throw New ArgumentException("Unexpected argument " & args(i))
            d(args(i).Substring(2)) = args(i + 1)
        Next
        For Each k In {"source", "exe", "name", "manufacturer", "version", "upgrade-code", "out"}
            If Not d.ContainsKey(k) Then Throw New ArgumentException("--" & k & " is required.")
        Next
        Return d
    End Function

    ''' <summary>A stable component GUID for a folder of the app, so later versions keep the same components.</summary>
    Private Function StableGuid(seed As String) As String
        Dim bytes = MD5.HashData(Encoding.UTF8.GetBytes(seed))
        bytes(7) = CByte((bytes(7) And &HF) Or &H30)   ' version 3 (name-based)
        bytes(8) = CByte((bytes(8) And &H3F) Or &H80)
        Return "{" & New Guid(bytes).ToString().ToUpperInvariant() & "}"
    End Function

    Private Function IsShortName(name As String) As Boolean
        Dim parts = name.Split("."c)
        If parts.Length > 2 OrElse parts(0).Length = 0 OrElse parts(0).Length > 8 OrElse (parts.Length = 2 AndAlso parts(1).Length > 3) Then Return False
        Return name.All(Function(c) Char.IsAsciiLetterOrDigit(c) OrElse c = "_"c OrElse c = "-"c OrElse c = "."c)
    End Function

    ''' <summary>"SHORT~1.EXT|long name" (Windows Installer wants an 8.3 name next to a long one), unique in its folder.</summary>
    Private Function MsiName(name As String, used As HashSet(Of String)) As String
        If IsShortName(name) AndAlso used.Add(name.ToUpperInvariant()) Then Return name
        Dim baseName = New String(IO.Path.GetFileNameWithoutExtension(name).Where(Function(c) Char.IsAsciiLetterOrDigit(c)).Take(6).ToArray()).ToUpperInvariant()
        If baseName.Length = 0 Then baseName = "F"
        Dim ext = New String(IO.Path.GetExtension(name).Where(Function(c) Char.IsAsciiLetterOrDigit(c)).Take(3).ToArray()).ToUpperInvariant()
        For n = 1 To 99999
            Dim suffix = "~" & n
            Dim candidate = baseName.Substring(0, Math.Min(baseName.Length, 8 - suffix.Length)) & suffix & If(ext.Length > 0, "." & ext, "")
            If used.Add(candidate) Then Return candidate & "|" & name
        Next
        Throw New InvalidOperationException("No short name for " & name)
    End Function

    Private Sub Build(source As String, exeName As String, productName As String, manufacturer As String, versionText As String, upgradeCode As String,
                      outPath As String, folderName As String, arch As String)
        source = IO.Path.GetFullPath(source)
        Dim exePath = IO.Path.Combine(source, exeName)
        If Not File.Exists(exePath) Then Throw New IOException("The app's exe is not in the source folder: " & exePath)
        Dim v = Version.Parse(versionText)
        Dim productVersion = $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}"
        upgradeCode = "{" & Guid.Parse(upgradeCode).ToString().ToUpperInvariant() & "}"
        Dim x64 = Not String.Equals(arch, "arm64", StringComparison.OrdinalIgnoreCase)
        Dim compBits = 4 Or 256   ' registry key path, 64-bit

        Dim work = IO.Path.Combine(IO.Path.GetTempPath(), "msibuilder-" & Guid.NewGuid().ToString("N").Substring(0, 8))
        Directory.CreateDirectory(work)
        Try
            outPath = IO.Path.GetFullPath(outPath)
            Dim staging = outPath & ".partial"
            If File.Exists(staging) Then File.Delete(staging)
            Dim seq = 0
            Check(MsiOpenDatabaseW(staging, New IntPtr(MSIDBOPEN_CREATE), _db), "Creating " & staging)
            Try
                For Each sql In Schema
                    Exec(sql)
                Next

                ' Folders: TARGETDIR\LocalAppData\Programs\<folder>\..., plus the Start menu and the desktop.
                Dim dirCols = {"Directory", "Directory_Parent", "DefaultDir"}
                Insert("Directory", dirCols, "TARGETDIR", Nothing, "SourceDir")
                Insert("Directory", dirCols, "LocalAppDataFolder", "TARGETDIR", ".")
                Insert("Directory", dirCols, "ProgramMenuFolder", "TARGETDIR", ".")
                Insert("Directory", dirCols, "DesktopFolder", "TARGETDIR", ".")
                Insert("Directory", dirCols, "ProgramsFolder", "LocalAppDataFolder", MsiName("Programs", New HashSet(Of String)))
                Insert("Directory", dirCols, "INSTALLFOLDER", "ProgramsFolder", MsiName(folderName, New HashSet(Of String)))
                Dim dirKeys As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {{"", "INSTALLFOLDER"}}
                Dim usedInDir As New Dictionary(Of String, HashSet(Of String))(StringComparer.OrdinalIgnoreCase) From {{"", New HashSet(Of String)}}
                Dim n = 0
                For Each d In Directory.GetDirectories(source, "*", SearchOption.AllDirectories).OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)
                    Dim rel = IO.Path.GetRelativePath(source, d)
                    Dim parentRel = If(IO.Path.GetDirectoryName(rel), "")
                    n += 1
                    Dim key = "dir_" & n
                    Insert("Directory", dirCols, key, dirKeys(parentRel), MsiName(IO.Path.GetFileName(d), usedInDir(parentRel)))
                    dirKeys(rel) = key
                    usedInDir(rel) = New HashSet(Of String)
                Next

                ' One component per folder (its key path a value under HKCU: required for per-user folders), plus one for the
                ' shortcuts and the clean-up on uninstall.
                Dim compCols = {"Component", "ComponentId", "Directory_", "Attributes", "Condition", "KeyPath"}
                Dim regCols = {"Registry", "Root", "Key", "Name", "Value", "Component_"}
                Dim regKey = "Software\" & manufacturer & "\" & productName.Replace("\", "")
                Dim comps As New List(Of String)
                Dim compOfDir As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
                Dim ci = 0
                For Each kv In dirKeys.OrderBy(Function(x) x.Key, StringComparer.OrdinalIgnoreCase)
                    ci += 1
                    Dim comp = "c_" & ci
                    Insert("Component", compCols, comp, StableGuid(upgradeCode & "|dir|" & kv.Key.ToLowerInvariant()), kv.Value, compBits, Nothing, "r_" & ci)
                    Insert("Registry", regCols, "r_" & ci, 1, regKey & "\Components", comp, "1", comp)
                    comps.Add(comp)
                    compOfDir(kv.Key) = comp
                Next
                Insert("Component", compCols, "c_main", StableGuid(upgradeCode & "|main"), "INSTALLFOLDER", compBits, Nothing, "r_main")
                Insert("Registry", regCols, "r_main", 1, regKey, "InstallFolder", "[INSTALLFOLDER]", "c_main")
                comps.Add("c_main")

                ' Files, in the order they go into the cabinet.
                Dim fileCols = {"File", "Component_", "FileName", "FileSize", "Version", "Language", "Attributes", "Sequence"}
                Dim ddf As New StringBuilder()
                Dim cab = IO.Path.Combine(work, "app.cab")
                ddf.AppendLine(".OPTION EXPLICIT").AppendLine(".Set CabinetNameTemplate=app.cab").AppendLine($".Set DiskDirectoryTemplate=""{work}""").
                    AppendLine(".Set Cabinet=on").AppendLine(".Set Compress=on").AppendLine(".Set CompressionType=LZX").AppendLine(".Set CompressionMemory=21").
                    AppendLine(".Set MaxDiskSize=0").AppendLine(".Set CabinetFileCountThreshold=0").AppendLine(".Set FolderFileCountThreshold=0").
                    AppendLine(".Set FolderSizeThreshold=0").AppendLine(".Set MaxCabinetSize=0").AppendLine(".Set UniqueFiles=on").
                    AppendLine($".Set RptFileName=""{IO.Path.Combine(work, "cab.rpt")}""").AppendLine($".Set InfFileName=""{IO.Path.Combine(work, "cab.inf")}""")

                Dim exeKey As String = Nothing
                For Each f In Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(Function(x) x, StringComparer.OrdinalIgnoreCase)
                    Dim rel = IO.Path.GetRelativePath(source, f)
                    Dim dirRel = If(IO.Path.GetDirectoryName(rel), "")
                    seq += 1
                    Dim key = "f_" & seq
                    Dim info As New FileInfo(f)
                    If info.Length > Integer.MaxValue Then Throw New IOException("Too large for an MSI: " & rel)
                    Dim fv = FileVersionInfo.GetVersionInfo(f)
                    Dim ver As String = Nothing
                    If fv.FileMajorPart + fv.FileMinorPart + fv.FileBuildPart + fv.FilePrivatePart > 0 Then ver = $"{fv.FileMajorPart}.{fv.FileMinorPart}.{fv.FileBuildPart}.{fv.FilePrivatePart}"
                    Insert("File", fileCols, key, compOfDir(dirRel), MsiName(IO.Path.GetFileName(f), usedInDir(dirRel)), CInt(info.Length), ver, If(ver Is Nothing, Nothing, "0"), 0, seq)
                    If String.Equals(rel, exeName, StringComparison.OrdinalIgnoreCase) Then exeKey = key
                    ddf.Append(""""c).Append(f).Append(""" ").AppendLine(key)
                Next
                If exeKey Is Nothing Then Throw New IOException("The exe must sit at the top of the source folder.")
                Dim ddfPath = IO.Path.Combine(work, "app.ddf")
                File.WriteAllText(ddfPath, ddf.ToString(), New UTF8Encoding(False))
                Dim psi As New ProcessStartInfo(IO.Path.Combine(Environment.SystemDirectory, "makecab.exe"), $"/F ""{ddfPath}""") With {
                    .WorkingDirectory = work, .UseShellExecute = False, .RedirectStandardOutput = True, .RedirectStandardError = True, .CreateNoWindow = True}
                Using p = Process.Start(psi)
                    Dim output = p.StandardOutput.ReadToEndAsync()
                    Dim errors = p.StandardError.ReadToEnd()
                    p.WaitForExit()
                    If p.ExitCode <> 0 OrElse Not File.Exists(cab) Then Throw New InvalidOperationException("makecab failed: " & output.Result & errors)
                End Using

                Insert("Media", {"DiskId", "LastSequence", "DiskPrompt", "Cabinet", "VolumeLabel", "Source"}, 1, seq, Nothing, "#app.cab", Nothing, Nothing)
                Insert("_Streams", {"Name", "Data"}, "app.cab", "@stream:" & cab)

                ' Shortcuts (not advertised: they point at the exe itself and show its icon).
                Dim scCols = {"Shortcut", "Directory_", "Name", "Component_", "Target", "Arguments", "Description", "Hotkey", "Icon_", "IconIndex", "ShowCmd", "WkDir"}
                Dim scName = MsiName(productName, New HashSet(Of String))
                Insert("Shortcut", scCols, "sc_menu", "ProgramMenuFolder", scName, "c_main", "[#" & exeKey & "]", Nothing, productName, Nothing, Nothing, Nothing, Nothing, "INSTALLFOLDER")
                Insert("Shortcut", scCols, "sc_desktop", "DesktopFolder", scName, "c_main", "[#" & exeKey & "]", Nothing, productName, Nothing, Nothing, Nothing, Nothing, "INSTALLFOLDER")

                ' Uninstall also removes what the app's own updates put in its folders, then the folders.
                Dim rmCols = {"FileKey", "Component_", "FileName", "DirProperty", "InstallMode"}
                Dim ri = 0
                For Each kv In dirKeys
                    ri += 1
                    Insert("RemoveFile", rmCols, "rmf_" & ri, "c_main", "*.*", kv.Value, 2)
                    Insert("RemoveFile", rmCols, "rmd_" & ri, "c_main", Nothing, kv.Value, 2)
                Next

                Insert("Feature", {"Feature", "Feature_Parent", "Title", "Description", "Display", "Level", "Directory_", "Attributes"}, "Main", Nothing, productName, productName, 1, 1, Nothing, 0)
                For Each c In comps
                    Insert("FeatureComponents", {"Feature_", "Component_"}, "Main", c)
                Next

                ' Upgrades: an older (or the same) version is removed first; a newer one installed stops this one.
                Dim upCols = {"UpgradeCode", "VersionMin", "VersionMax", "Language", "Attributes", "Remove", "ActionProperty"}
                Insert("Upgrade", upCols, upgradeCode, "0.0.0", productVersion, Nothing, 256 Or 512, Nothing, "PREVIOUSFOUND")
                Insert("Upgrade", upCols, upgradeCode, productVersion, Nothing, Nothing, 2, Nothing, "NEWERFOUND")
                Insert("LaunchCondition", {"Condition", "Description"}, "NOT NEWERFOUND", $"A newer version of {productName} is already installed.")

                Dim propCols = {"Property", "Value"}
                For Each kv In New Dictionary(Of String, String) From {
                    {"ProductCode", "{" & Guid.NewGuid().ToString().ToUpperInvariant() & "}"}, {"UpgradeCode", upgradeCode}, {"ProductName", productName},
                    {"ProductVersion", productVersion}, {"Manufacturer", manufacturer}, {"ProductLanguage", "1033"},
                    {"SecureCustomProperties", "PREVIOUSFOUND;NEWERFOUND"}, {"ARPNOMODIFY", "1"}, {"ARPNOREPAIR", "1"},
                    {"ARPCOMMENTS", productName & " (per-user install; it updates itself)"}}
                    Insert("Property", propCols, kv.Key, kv.Value)
                Next

                Dim seqCols = {"Action", "Condition", "Sequence"}
                For Each s In ExecuteSequence
                    Insert("InstallExecuteSequence", seqCols, s.Item1, Nothing, s.Item2)
                Next
                For Each s In UiSequence
                    Insert("InstallUISequence", seqCols, s.Item1, Nothing, s.Item2)
                Next
                For Each s In AdminExecute
                    Insert("AdminExecuteSequence", seqCols, s.Item1, Nothing, s.Item2)
                Next
                For Each s In AdminUi
                    Insert("AdminUISequence", seqCols, s.Item1, Nothing, s.Item2)
                Next

                ' Summary information: 64-bit (or Arm64) package, schema 200/500, compressed, no elevation needed.
                Dim summary As IntPtr
                Check(MsiGetSummaryInformationW(_db, Nothing, 20, summary), "summary information")
                Try
                    Dim none As New ComTypes.FILETIME()
                    Dim setText = Sub(pid As UInteger, text As String) Check(MsiSummaryInfoSetPropertyW(summary, pid, 30, 0, none, text), "summary " & pid)
                    Dim setInt = Sub(pid As UInteger, value As Integer) Check(MsiSummaryInfoSetPropertyW(summary, pid, 3, value, none, Nothing), "summary " & pid)
                    Check(MsiSummaryInfoSetPropertyW(summary, 1, 2, 1252, none, Nothing), "summary codepage")
                    setText(2, "Installation Database")
                    setText(3, productName)
                    setText(4, manufacturer)
                    setText(5, "Installer")
                    setText(6, $"{productName} {productVersion}")
                    setText(7, If(x64, "x64", "Arm64") & ";1033")
                    setText(9, "{" & Guid.NewGuid().ToString().ToUpperInvariant() & "}")
                    setInt(14, If(x64, 200, 500))
                    setInt(15, 2 Or 8)
                    setText(18, "Agentic MsiBuilder")
                    setInt(19, 2)
                    Check(MsiSummaryInfoPersist(summary), "saving summary information")
                Finally
                    MsiCloseHandle(summary)
                End Try
                Check(MsiDatabaseCommit(_db), "commit")
            Finally
                MsiCloseHandle(_db)
                _db = IntPtr.Zero
            End Try
            File.Move(staging, outPath, overwrite:=True)
            Console.WriteLine($"{outPath}: {productName} {productVersion}, {seq} files, {New FileInfo(outPath).Length \ 1024 \ 1024} MB")
        Finally
            Try : Directory.Delete(work, True) : Catch ex As IOException : End Try
        End Try
    End Sub

End Module
