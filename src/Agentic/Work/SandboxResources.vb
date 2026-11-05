Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Microsoft.Data.Sqlite

Namespace Work

    ''' <summary>A long-lived sandbox resource the host is tracking (a background process or a Docker container/compose project).</summary>
    Public Class SandboxResourceInfo
        Public Property Id As String
        Public Property ServerId As String
        Public Property Kind As String            ' process | container | compose
        Public Property Ref As String             ' process id | container id | compose project
        Public Property Name As String
        Public Property WorkingDir As String
        Public Property Command As String
        Public Property OwnerUser As String
        Public Property OwnerChatId As String
        Public Property OwnerAgentId As String
        Public Property IsProtected As Boolean
        Public Property State As String           ' running | stopped | reaped | gone | failed
        Public Property IdleMinutes As Integer?
        Public Property MaxLifeMinutes As Integer?
        Public Property CreatedUtc As DateTime?
        Public Property FirstSeenUtc As DateTime
        Public Property LastSeenUtc As DateTime
        Public Property StoppedUtc As DateTime?
        Public Property Reason As String
        Public Property Notified As Boolean
    End Class

    ''' <summary>Stores the sandbox resources the host tracks. Only resources recorded here are ever stopped by the reaper.</summary>
    Public NotInheritable Class SandboxResourceStore
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Private Shared Function Map(r As SqliteDataReader) As SandboxResourceInfo
            Return New SandboxResourceInfo With {
                .Id = Db.Str(r, "Id"), .ServerId = Db.Str(r, "ServerId"), .Kind = Db.Str(r, "Kind"), .Ref = Db.Str(r, "Ref"),
                .Name = Db.Str(r, "Name"), .WorkingDir = Db.Str(r, "WorkingDir"), .Command = Db.Str(r, "Command"),
                .OwnerUser = Db.Str(r, "OwnerUser"), .OwnerChatId = Db.Str(r, "OwnerChatId"), .OwnerAgentId = Db.Str(r, "OwnerAgentId"),
                .IsProtected = Db.Bool(r, "Protected"), .State = Db.Str(r, "State"),
                .IdleMinutes = NullableInt(r, "IdleMinutes"), .MaxLifeMinutes = NullableInt(r, "MaxLifeMinutes"),
                .CreatedUtc = Db.DtN(r, "CreatedUtc"), .FirstSeenUtc = Db.Dt(r, "FirstSeenUtc"), .LastSeenUtc = Db.Dt(r, "LastSeenUtc"),
                .StoppedUtc = Db.DtN(r, "StoppedUtc"), .Reason = Db.Str(r, "Reason"), .Notified = Db.Bool(r, "Notified")}
        End Function

        Private Shared Function NullableInt(r As SqliteDataReader, col As String) As Integer?
            Dim l = Db.LngN(r, col)
            Return If(l.HasValue, CType(CInt(l.Value), Integer?), Nothing)
        End Function

        ''' <summary>
        ''' Records (or refreshes) a resource keyed by (server, kind, ref). A new resource is inserted as running; an existing
        ''' one keeps its state, protection and owner but its LastSeenUtc (and any newly learned name/created time/dir) is updated.
        ''' Returns the stored row.
        ''' </summary>
        Public Async Function UpsertAsync(res As SandboxResourceInfo, now As DateTime) As Task(Of SandboxResourceInfo)
            Dim existing = Await GetByRefAsync(res.ServerId, res.Kind, res.Ref).ConfigureAwait(False)
            If existing IsNot Nothing Then
                Await _db.ExecuteAsync(
                    "UPDATE SandboxResources SET LastSeenUtc=@now, " &
                    "Name=COALESCE(NULLIF(@name,''),Name), WorkingDir=COALESCE(NULLIF(@wd,''),WorkingDir), " &
                    "Command=COALESCE(NULLIF(@cmd,''),Command), CreatedUtc=COALESCE(CreatedUtc,@created), " &
                    "OwnerUser=COALESCE(OwnerUser,NULLIF(@user,'')), OwnerChatId=COALESCE(OwnerChatId,NULLIF(@chat,'')), OwnerAgentId=COALESCE(OwnerAgentId,NULLIF(@agent,'')) " &
                    "WHERE Id=@id",
                    ("@now", CObj(Db.Ts(now))), ("@name", If(res.Name, "")), ("@wd", If(res.WorkingDir, "")), ("@cmd", If(res.Command, "")),
                    ("@created", Db.Ts(res.CreatedUtc)), ("@user", If(res.OwnerUser, "")), ("@chat", If(res.OwnerChatId, "")),
                    ("@agent", If(res.OwnerAgentId, "")), ("@id", existing.Id)).ConfigureAwait(False)
                Return Await GetAsync(existing.Id).ConfigureAwait(False)
            End If
            Dim id = Ids.NewId("sbx")
            Await _db.ExecuteAsync(
                "INSERT INTO SandboxResources(Id, ServerId, Kind, Ref, Name, WorkingDir, Command, OwnerUser, OwnerChatId, OwnerAgentId, " &
                "Protected, State, IdleMinutes, MaxLifeMinutes, CreatedUtc, FirstSeenUtc, LastSeenUtc, Notified) " &
                "VALUES (@id,@srv,@kind,@ref,@name,@wd,@cmd,@user,@chat,@agent,@prot,@state,@idle,@max,@created,@first,@last,0)",
                ("@id", CObj(id)), ("@srv", res.ServerId), ("@kind", res.Kind), ("@ref", res.Ref), ("@name", If(res.Name, CObj(DBNull.Value))),
                ("@wd", If(res.WorkingDir, CObj(DBNull.Value))), ("@cmd", If(res.Command, CObj(DBNull.Value))), ("@user", If(res.OwnerUser, CObj(DBNull.Value))),
                ("@chat", If(res.OwnerChatId, CObj(DBNull.Value))), ("@agent", If(res.OwnerAgentId, CObj(DBNull.Value))),
                ("@prot", If(res.IsProtected, 1, 0)), ("@state", If(res.State, "running")),
                ("@idle", If(res.IdleMinutes.HasValue, CObj(res.IdleMinutes.Value), DBNull.Value)),
                ("@max", If(res.MaxLifeMinutes.HasValue, CObj(res.MaxLifeMinutes.Value), DBNull.Value)),
                ("@created", Db.Ts(res.CreatedUtc)), ("@first", Db.Ts(now)), ("@last", Db.Ts(now))).ConfigureAwait(False)
            Return Await GetAsync(id).ConfigureAwait(False)
        End Function

        Public Function GetAsync(id As String) As Task(Of SandboxResourceInfo)
            Return _db.QuerySingleAsync("SELECT * FROM SandboxResources WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Public Function GetByRefAsync(serverId As String, kind As String, ref As String) As Task(Of SandboxResourceInfo)
            Return _db.QuerySingleAsync("SELECT * FROM SandboxResources WHERE ServerId=@s AND Kind=@k AND Ref=@r",
                                        AddressOf Map, ("@s", CObj(serverId)), ("@k", CObj(kind)), ("@r", CObj(ref)))
        End Function

        ''' <summary>Resources in the given states (default: running), most recently seen first.</summary>
        Public Function ListAsync(Optional states As String() = Nothing, Optional limit As Integer = 500) As Task(Of List(Of SandboxResourceInfo))
            If states Is Nothing OrElse states.Length = 0 Then states = {"running"}
            Dim inClause = String.Join(",", states.Select(Function(s, i) "@s" & i))
            Dim ps = states.Select(Function(s, i) ("@s" & i, CObj(s))).Append(("@lim", CObj(limit))).ToArray()
            Return _db.QueryAsync("SELECT * FROM SandboxResources WHERE State IN (" & inClause & ") ORDER BY LastSeenUtc DESC LIMIT @lim", AddressOf Map, ps)
        End Function

        Public Function MarkStateAsync(id As String, state As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxResources SET State=@st WHERE Id=@id", ("@st", CObj(state)), ("@id", CObj(id)))
        End Function

        Public Function MarkStoppedAsync(id As String, state As String, reason As String, now As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxResources SET State=@st, Reason=@r, StoppedUtc=@now WHERE Id=@id",
                                    ("@st", CObj(state)), ("@r", If(reason, CObj(DBNull.Value))), ("@now", CObj(Db.Ts(now))), ("@id", CObj(id)))
        End Function

        Public Function SetProtectedAsync(id As String, protectedFlag As Boolean) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxResources SET Protected=@p WHERE Id=@id", ("@p", If(protectedFlag, 1, 0)), ("@id", CObj(id)))
        End Function

        Public Function SetNotifiedAsync(id As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxResources SET Notified=1 WHERE Id=@id", ("@id", CObj(id)))
        End Function
    End Class

End Namespace
