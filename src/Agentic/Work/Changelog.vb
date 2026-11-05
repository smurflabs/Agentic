Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports Agentic.Logging

Namespace Work

    ''' <summary>One release of one app in the changelog: what it added and what it fixed.</summary>
    Public NotInheritable Class ChangelogEntry
        ''' <summary>"desktop", "cli", "vscode" or "server".</summary>
        Public Property App As String
        Public Property Version As String
        <JsonPropertyName("date")>
        Public Property DateUtc As DateTime
        Public Property Title As String
        Public Property Features As New List(Of String)
        Public Property Fixes As New List(Of String)
        ''' <summary>The release notes as published (release.json), when no features were listed.</summary>
        Public Property Notes As String
        ''' <summary>The server's dashboard build id, for server releases.</summary>
        Public Property Build As String
    End Class

    ''' <summary>
    ''' The apps' changelog: changelog.json in the updates folder. tools\publish-clients.ps1 and tools\publish-server.ps1
    ''' add an entry for each version they publish (with its features and fixes); a version that reaches the folder some
    ''' other way gets an entry from its release notes the first time the server sees it, so nothing goes unrecorded.
    ''' Entries are only ever added: a hand-edited file is kept as it is, and one that does not parse is never overwritten.
    ''' </summary>
    Public NotInheritable Class ReleaseChangelog

        Private Shared ReadOnly Json As New JsonSerializerOptions With {
            .PropertyNamingPolicy = JsonNamingPolicy.CamelCase, .PropertyNameCaseInsensitive = True, .WriteIndented = True,
            .ReadCommentHandling = JsonCommentHandling.Skip, .AllowTrailingCommas = True,
            .DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull}

        Private ReadOnly _path As String
        Private ReadOnly _log As ILog
        Private ReadOnly _gate As New Object()
        Private _stamp As DateTime = DateTime.MinValue
        Private _entries As List(Of ChangelogEntry)
        Private _broken As Boolean

        Private NotInheritable Class FileShape
            Public Property Entries As New List(Of ChangelogEntry)
        End Class

        Public Sub New(path As String, log As ILog)
            _path = path
            _log = log
        End Sub

        Public ReadOnly Property FilePath As String
            Get
                Return _path
            End Get
        End Property

        ''' <summary>Every entry, newest first.</summary>
        Public Function Entries() As List(Of ChangelogEntry)
            SyncLock _gate
                Load()
                Return _entries.OrderByDescending(Function(e) e.DateUtc).
                    ThenByDescending(Function(e) UpdateService.ParseVersion(e.Version)).ToList()
            End SyncLock
        End Function

        ''' <summary>Adds an entry for this version of the app unless it has one. Returns whether it added one.</summary>
        Public Function Ensure(app As String, version As String, dateUtc As DateTime, notes As String, Optional build As String = Nothing) As Boolean
            If String.IsNullOrWhiteSpace(app) OrElse String.IsNullOrWhiteSpace(version) Then Return False
            SyncLock _gate
                Load()
                If _broken Then Return False
                If _entries.Any(Function(e) String.Equals(e.App, app, StringComparison.OrdinalIgnoreCase) AndAlso
                                    String.Equals(e.Version, version, StringComparison.OrdinalIgnoreCase)) Then Return False
                Dim dir = Path.GetDirectoryName(_path)
                If String.IsNullOrEmpty(dir) OrElse Not Directory.Exists(dir) Then Return False
                _entries.Add(New ChangelogEntry With {
                    .App = app.ToLowerInvariant(), .Version = version, .DateUtc = DateTime.SpecifyKind(dateUtc, DateTimeKind.Utc),
                    .Notes = If(String.IsNullOrWhiteSpace(notes), Nothing, notes.Trim()), .Build = If(String.IsNullOrWhiteSpace(build), Nothing, build)})
                Try
                    Dim temp = _path & ".partial"
                    File.WriteAllText(temp, JsonSerializer.Serialize(New FileShape With {.Entries = _entries}, Json))
                    File.Move(temp, _path, overwrite:=True)
                    _stamp = File.GetLastWriteTimeUtc(_path)
                Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                    _log?.Warn("Updates", $"The changelog {_path} could not be written: {ex.Message}")
                    Return False
                End Try
                Return True
            End SyncLock
        End Function

        ''' <summary>Reads the file again when it changed (a publish script added to it).</summary>
        Private Sub Load()
            Dim stamp = If(File.Exists(_path), File.GetLastWriteTimeUtc(_path), DateTime.MinValue)
            If _entries IsNot Nothing AndAlso stamp = _stamp Then Return
            _stamp = stamp
            _broken = False
            _entries = New List(Of ChangelogEntry)
            If stamp = DateTime.MinValue Then Return
            Try
                Dim shape = JsonSerializer.Deserialize(Of FileShape)(File.ReadAllText(_path), Json)
                If shape?.Entries IsNot Nothing Then
                    _entries = shape.Entries.Where(Function(e) e IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(e.App) AndAlso Not String.IsNullOrWhiteSpace(e.Version)).ToList()
                    For Each e In _entries
                        e.App = e.App.ToLowerInvariant()
                        If e.Features Is Nothing Then e.Features = New List(Of String)
                        If e.Fixes Is Nothing Then e.Fixes = New List(Of String)
                    Next
                End If
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is IOException OrElse TypeOf ex Is UnauthorizedAccessException
                ' Keep the file as it is: someone edits it by hand and will fix it.
                _broken = True
                _log?.Warn("Updates", $"The changelog {_path} does not parse; it is left as it is: {ex.Message}")
            End Try
        End Sub

    End Class

End Namespace
