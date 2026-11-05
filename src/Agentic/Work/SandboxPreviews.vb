Option Strict On
Option Explicit On
Option Infer On

Imports System.Security.Cryptography
Imports Agentic.Persistence
Imports Microsoft.Data.Sqlite

Namespace Work

    ''' <summary>A preview link: /container/{Id}/ opens the app on a sandbox container's published port.</summary>
    Public Class SandboxPreview
        Public Property Id As String
        Public Property OwnerUser As String
        Public Property ChatId As String
        Public Property ContainerId As String
        Public Property ContainerName As String
        Public Property TargetHost As String
        Public Property TargetPort As Integer
        Public Property ContainerPort As Integer?
        ''' <summary>direct: this host reaches the port; mcp: each request goes through the sandbox connection.</summary>
        Public Property Transport As String
        Public Property CreatedUtc As DateTime
        Public Property ExpiresUtc As DateTime
        Public Property LastUsedUtc As DateTime?
        Public Property RevokedUtc As DateTime?

        ''' <summary>The path the app is served under, ending in a slash.</summary>
        Public ReadOnly Property Path As String
            Get
                Return "/container/" & Id & "/"
            End Get
        End Property

        Public Function IsLive(now As DateTime) As Boolean
            Return Not RevokedUtc.HasValue AndAlso ExpiresUtc > now
        End Function
    End Class

    ''' <summary>Preview links. The id is the secret: a readable name and 128 random bits ("shop-a1b2…").</summary>
    Public NotInheritable Class SandboxPreviewStore
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Private Shared Function Map(r As SqliteDataReader) As SandboxPreview
            Dim cp = Db.LngN(r, "ContainerPort")
            Return New SandboxPreview With {
                .Id = Db.Str(r, "Id"), .OwnerUser = Db.Str(r, "OwnerUser"), .ChatId = Db.Str(r, "ChatId"), .ContainerId = Db.Str(r, "ContainerId"),
                .ContainerName = Db.Str(r, "ContainerName"), .TargetHost = Db.Str(r, "TargetHost"), .TargetPort = Db.Int(r, "TargetPort"),
                .ContainerPort = If(cp.HasValue, CType(CInt(cp.Value), Integer?), Nothing), .Transport = Db.Str(r, "Transport"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .ExpiresUtc = Db.Dt(r, "ExpiresUtc"),
                .LastUsedUtc = Db.DtN(r, "LastUsedUtc"), .RevokedUtc = Db.DtN(r, "RevokedUtc")}
        End Function

        ''' <summary>"shop-api" from a container name: lower case letters, digits and dashes, at most 24 characters.</summary>
        Friend Shared Function Slug(name As String) As String
            Dim s = New String(If(name, "").ToLowerInvariant().Select(Function(c) If(Char.IsLetterOrDigit(c) AndAlso c < ChrW(128), c, "-"c)).ToArray())
            While s.Contains("--")
                s = s.Replace("--", "-")
            End While
            s = s.Trim("-"c)
            If s.Length > 24 Then s = s.Substring(0, 24).TrimEnd("-"c)
            Return If(s.Length = 0, "app", s)
        End Function

        ''' <summary>A new unguessable link id: the app's name and 22 random base-32 characters (110 bits).</summary>
        Friend Shared Function NewId(name As String) As String
            Const alphabet = "abcdefghijkmnpqrstuvwxyz23456789"
            Dim bytes = RandomNumberGenerator.GetBytes(22)
            Return Slug(name) & "-" & New String(bytes.Select(Function(b) alphabet(b Mod alphabet.Length)).ToArray())
        End Function

        ''' <summary>A link to this container port for this user: the live one they already have, or a new one.</summary>
        Public Async Function CreateAsync(owner As String, chatId As String, containerId As String, containerName As String, host As String, port As Integer,
                                          containerPort As Integer?, hours As Integer, now As DateTime, Optional transport As String = Nothing) As Task(Of SandboxPreview)
            Dim existing = (Await ListForUserAsync(owner).ConfigureAwait(False)).FirstOrDefault(
                Function(p) p.ContainerId = containerId AndAlso p.TargetPort = port AndAlso p.IsLive(now) AndAlso p.ExpiresUtc > now.AddHours(1))
            If existing IsNot Nothing Then Return existing
            Dim id = NewId(containerName)
            Await _db.ExecuteAsync(
                "INSERT INTO SandboxPreviews(Id, OwnerUser, ChatId, ContainerId, ContainerName, TargetHost, TargetPort, ContainerPort, Transport, CreatedUtc, ExpiresUtc) " &
                "VALUES (@id,@o,@c,@cid,@cn,@h,@p,@cp,@tr,@now,@exp)",
                ("@id", CObj(id)), ("@o", owner), ("@c", If(chatId, CObj(DBNull.Value))), ("@cid", containerId), ("@cn", If(containerName, CObj(DBNull.Value))),
                ("@h", host), ("@p", port), ("@cp", If(containerPort.HasValue, CObj(containerPort.Value), DBNull.Value)), ("@tr", If(transport, CObj(DBNull.Value))),
                ("@now", Db.Ts(now)), ("@exp", Db.Ts(now.AddHours(Math.Clamp(hours, 1, 24 * 30))))).ConfigureAwait(False)
            Return Await GetAsync(id).ConfigureAwait(False)
        End Function

        Public Function GetAsync(id As String) As Task(Of SandboxPreview)
            Return _db.QuerySingleAsync("SELECT * FROM SandboxPreviews WHERE Id=@id", AddressOf Map, ("@id", CObj(If(id, ""))))
        End Function

        Public Function ListForUserAsync(owner As String) As Task(Of List(Of SandboxPreview))
            Return _db.QueryAsync("SELECT * FROM SandboxPreviews WHERE OwnerUser=@o AND RevokedUtc IS NULL ORDER BY CreatedUtc DESC LIMIT 200", AddressOf Map, ("@o", CObj(owner)))
        End Function

        Public Function ListLiveAsync(now As DateTime) As Task(Of List(Of SandboxPreview))
            Return _db.QueryAsync("SELECT * FROM SandboxPreviews WHERE RevokedUtc IS NULL AND ExpiresUtc > @n ORDER BY CreatedUtc DESC LIMIT 500", AddressOf Map, ("@n", CObj(Db.Ts(now))))
        End Function

        Public Function RevokeAsync(id As String, now As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxPreviews SET RevokedUtc=@n WHERE Id=@id AND RevokedUtc IS NULL", ("@n", CObj(Db.Ts(now))), ("@id", CObj(id)))
        End Function

        ''' <summary>Every link to a container ends with it (it was stopped).</summary>
        Public Function RevokeForContainerAsync(containerId As String, now As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxPreviews SET RevokedUtc=@n WHERE ContainerId=@c AND RevokedUtc IS NULL", ("@n", CObj(Db.Ts(now))), ("@c", CObj(containerId)))
        End Function

        Public Function SetTransportAsync(id As String, transport As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxPreviews SET Transport=@t WHERE Id=@id", ("@t", CObj(transport)), ("@id", CObj(id)))
        End Function

        Public Function TouchAsync(id As String, now As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE SandboxPreviews SET LastUsedUtc=@n WHERE Id=@id", ("@n", CObj(Db.Ts(now))), ("@id", CObj(id)))
        End Function
    End Class

End Namespace
