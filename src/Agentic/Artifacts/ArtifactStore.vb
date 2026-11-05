Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports Agentic.Events
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Artifacts

    Public Class ArtifactInfo
        Public Property Id As String
        Public Property OwnerAgentId As String
        Public Property Name As String
        Public Property MimeType As String
        Public Property SizeBytes As Long
        Public Property Sha256 As String
        Public Property Description As String
        Public Property SourceTaskId As String
        Public Property CreatedUtc As DateTime
        Public Property ExpiresUtc As DateTime?
        Friend Property StorageFile As String
    End Class

    Public Class ArtifactAccessException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' Host-managed artifact store (files + metadata) used for references instead of copying large data
    ''' between agents. Every artifact has an owner; other agents can read it only after the owner explicitly
    ''' attaches it to a message (which records a grant). There is no shared mutable folder.
    ''' </summary>
    Public NotInheritable Class ArtifactStore
        Private ReadOnly _db As Database
        Private ReadOnly _root As String
        Private ReadOnly _maxBytes As Long
        Private ReadOnly _quotaBytes As Long
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _retentionDays As Integer

        Public Sub New(db As Database, rootDirectory As String, maxArtifactBytes As Long, quotaBytesPerAgent As Long, retentionDays As Integer, events As IAgentEventBus)
            _db = db
            _root = rootDirectory
            _maxBytes = maxArtifactBytes
            _quotaBytes = quotaBytesPerAgent
            _retentionDays = retentionDays
            _events = events
            Directory.CreateDirectory(_root)
        End Sub

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ArtifactInfo
            Return New ArtifactInfo With {
                .Id = Db.Str(r, "Id"), .OwnerAgentId = Db.Str(r, "OwnerAgentId"), .Name = Db.Str(r, "Name"), .MimeType = Db.Str(r, "MimeType"),
                .SizeBytes = Db.Lng(r, "SizeBytes"), .Sha256 = Db.Str(r, "Sha256"), .Description = Db.Str(r, "Description"),
                .SourceTaskId = Db.Str(r, "SourceTaskId"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .ExpiresUtc = Db.DtN(r, "ExpiresUtc"),
                .StorageFile = Db.Str(r, "StorageFile")}
        End Function

        Friend Async Function SaveAsync(ownerAgentId As String, name As String, mimeType As String, data As Byte(), description As String, sourceTaskId As String) As Task(Of ArtifactInfo)
            If data Is Nothing OrElse data.Length = 0 Then Throw New ArgumentException("Artifact is empty.")
            If data.LongLength > _maxBytes Then Throw New ArtifactAccessException($"Artifact exceeds the maximum size of {_maxBytes \ 1024} KB.")
            Dim used = Await _db.ScalarLongAsync("SELECT COALESCE(SUM(SizeBytes),0) FROM Artifacts WHERE OwnerAgentId=@a", ("@a", CObj(ownerAgentId))).ConfigureAwait(False)
            If used + data.LongLength > _quotaBytes Then Throw New ArtifactAccessException("Artifact storage quota for this agent is exhausted.")
            Dim id = Ids.NewId("art")
            Dim fileName = id & ".bin"
            Dim full = Path.Combine(_root, fileName)
            Await File.WriteAllBytesAsync(full, data).ConfigureAwait(False)
            Dim now = DateTime.UtcNow
            Dim info As New ArtifactInfo With {
                .Id = id, .OwnerAgentId = ownerAgentId, .Name = TextUtil.Truncate(If(name, id), 200), .MimeType = If(mimeType, "application/octet-stream"),
                .SizeBytes = data.LongLength, .Sha256 = TextUtil.Sha256Hex(data), .Description = TextUtil.Truncate(description, 1000),
                .SourceTaskId = sourceTaskId, .CreatedUtc = now, .ExpiresUtc = If(_retentionDays > 0, now.AddDays(_retentionDays), CType(Nothing, DateTime?)),
                .StorageFile = fileName}
            Await _db.ExecuteAsync("INSERT INTO Artifacts(Id, OwnerAgentId, Name, MimeType, SizeBytes, Sha256, StorageFile, Description, SourceTaskId, CreatedUtc, ExpiresUtc) " &
                                   "VALUES (@id, @o, @n, @m, @s, @h, @f, @d, @t, @c, @e)",
                                   ("@id", CObj(id)), ("@o", ownerAgentId), ("@n", info.Name), ("@m", info.MimeType), ("@s", info.SizeBytes), ("@h", info.Sha256),
                                   ("@f", fileName), ("@d", info.Description), ("@t", sourceTaskId), ("@c", Db.Ts(now)), ("@e", Db.Ts(info.ExpiresUtc))).ConfigureAwait(False)
            _events.Emit(AgentEventType.ArtifactCreated, $"Artifact {info.Name} ({info.SizeBytes} bytes) saved", ownerAgentId, sourceTaskId,
                         data:=New Dictionary(Of String, Object) From {{"artifactId", id}, {"mimeType", info.MimeType}})
            Return info
        End Function

        Friend Function GetInfoAsync(id As String) As Task(Of ArtifactInfo)
            Return _db.QuerySingleAsync("SELECT * FROM Artifacts WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Friend Async Function CanReadAsync(agentId As String, artifactId As String) As Task(Of Boolean)
            Dim n = Await _db.ScalarLongAsync("SELECT COUNT(*) FROM Artifacts a WHERE a.Id=@id AND (a.OwnerAgentId=@a OR EXISTS (SELECT 1 FROM ArtifactGrants g WHERE g.ArtifactId=a.Id AND g.GranteeAgentId=@a))",
                                              ("@id", CObj(artifactId)), ("@a", agentId)).ConfigureAwait(False)
            Return n > 0
        End Function

        ''' <summary>Reads an artifact on behalf of an agent; throws if the agent is neither owner nor grantee.</summary>
        Friend Async Function ReadForAgentAsync(agentId As String, artifactId As String) As Task(Of (Info As ArtifactInfo, Data As Byte()))
            If Not Await CanReadAsync(agentId, artifactId).ConfigureAwait(False) Then Throw New ArtifactAccessException($"Access to artifact '{artifactId}' denied.")
            Return Await ReadAnyAsync(artifactId).ConfigureAwait(False)
        End Function

        ''' <summary>Admin/host read without ACL (dashboard downloads).</summary>
        Friend Async Function ReadAnyAsync(artifactId As String) As Task(Of (Info As ArtifactInfo, Data As Byte()))
            Dim info = Await GetInfoAsync(artifactId).ConfigureAwait(False)
            If info Is Nothing Then Throw New ArtifactAccessException($"Artifact '{artifactId}' not found.")
            Dim data = Await File.ReadAllBytesAsync(Path.Combine(_root, info.StorageFile)).ConfigureAwait(False)
            Return (info, data)
        End Function

        ''' <summary>Grants read access. Only the owner may share (grantees cannot re-share).</summary>
        Friend Async Function GrantAsync(ownerAgentId As String, artifactId As String, granteeAgentId As String, messageId As String) As Task
            Dim info = Await GetInfoAsync(artifactId).ConfigureAwait(False)
            If info Is Nothing Then Throw New ArtifactAccessException($"Artifact '{artifactId}' not found.")
            If Not String.Equals(info.OwnerAgentId, ownerAgentId, StringComparison.OrdinalIgnoreCase) Then
                Throw New ArtifactAccessException($"Only the owner can share artifact '{artifactId}'.")
            End If
            Await _db.ExecuteAsync("INSERT OR IGNORE INTO ArtifactGrants(ArtifactId, GranteeAgentId, GrantedByAgentId, MessageId, CreatedUtc) VALUES (@id, @g, @o, @m, @c)",
                                   ("@id", CObj(artifactId)), ("@g", granteeAgentId), ("@o", ownerAgentId), ("@m", messageId), ("@c", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
        End Function

        ''' <summary>Moves ownership of every artifact of one agent to another (a finished subagent's work goes to its parent).</summary>
        Friend Async Function TransferOwnershipAsync(fromAgentId As String, toAgentId As String) As Task(Of List(Of String))
            Dim ids = Await _db.QueryAsync("SELECT Id FROM Artifacts WHERE OwnerAgentId=@f", Function(r) Db.Str(r, "Id"), ("@f", CObj(fromAgentId))).ConfigureAwait(False)
            Await _db.ExecuteAsync("UPDATE Artifacts SET OwnerAgentId=@t WHERE OwnerAgentId=@f; DELETE FROM ArtifactGrants WHERE GranteeAgentId=@f",
                                   ("@t", CObj(toAgentId)), ("@f", fromAgentId)).ConfigureAwait(False)
            Return ids
        End Function

        Friend Function ListForAgentAsync(agentId As String, limit As Integer) As Task(Of List(Of ArtifactInfo))
            Return _db.QueryAsync("SELECT a.* FROM Artifacts a WHERE a.OwnerAgentId=@a OR EXISTS (SELECT 1 FROM ArtifactGrants g WHERE g.ArtifactId=a.Id AND g.GranteeAgentId=@a) " &
                                  "ORDER BY a.CreatedUtc DESC LIMIT @l", AddressOf Map, ("@a", CObj(agentId)), ("@l", Math.Clamp(limit, 1, 200)))
        End Function

        Public Function ListAllAsync(limit As Integer, Optional ownerAgentId As String = Nothing) As Task(Of List(Of ArtifactInfo))
            Dim where = If(String.IsNullOrEmpty(ownerAgentId), "", "WHERE OwnerAgentId=@o")
            Return _db.QueryAsync($"SELECT * FROM Artifacts {where} ORDER BY CreatedUtc DESC LIMIT @l", AddressOf Map, ("@o", CObj(ownerAgentId)), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Public Async Function DeleteExpiredAsync(now As DateTime) As Task(Of Integer)
            Dim expired = Await _db.QueryAsync("SELECT * FROM Artifacts WHERE ExpiresUtc IS NOT NULL AND ExpiresUtc <= @now LIMIT 500", AddressOf Map, ("@now", CObj(Db.Ts(now)))).ConfigureAwait(False)
            For Each a In expired
                Try
                    File.Delete(Path.Combine(_root, a.StorageFile))
                Catch
                End Try
                Await _db.ExecuteAsync("DELETE FROM ArtifactGrants WHERE ArtifactId=@id; DELETE FROM Artifacts WHERE Id=@id", ("@id", CObj(a.Id))).ConfigureAwait(False)
            Next
            Return expired.Count
        End Function
    End Class

    ''' <summary>Agent-bound artifact access. The agent id is fixed; the agent can only see owned or granted artifacts.</summary>
    Public NotInheritable Class ScopedArtifactAccess
        Private ReadOnly _store As ArtifactStore
        Private ReadOnly _agentId As String

        Friend Sub New(store As ArtifactStore, agentId As String)
            _store = store
            _agentId = agentId
        End Sub

        Public Function SaveAsync(name As String, mimeType As String, data As Byte(), Optional description As String = Nothing, Optional sourceTaskId As String = Nothing) As Task(Of ArtifactInfo)
            Return _store.SaveAsync(_agentId, name, mimeType, data, description, sourceTaskId)
        End Function

        Public Function ReadAsync(artifactId As String) As Task(Of (Info As ArtifactInfo, Data As Byte()))
            Return _store.ReadForAgentAsync(_agentId, artifactId)
        End Function

        Public Function CanReadAsync(artifactId As String) As Task(Of Boolean)
            Return _store.CanReadAsync(_agentId, artifactId)
        End Function

        Public Function ListAsync(Optional limit As Integer = 50) As Task(Of List(Of ArtifactInfo))
            Return _store.ListForAgentAsync(_agentId, limit)
        End Function
    End Class

End Namespace
