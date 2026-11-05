Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Configuration
Imports Agentic.Utilities

Namespace Persistence

    ''' <summary>A dashboard login. Admins can do everything; Users only what their permissions and agent scope allow.</summary>
    Public Class DashboardUser
        Public Property Username As String
        ''' <summary>Admin | User</summary>
        Public Property Role As String = "User"
        Public Property DisplayName As String
        Public Property Permissions As New List(Of String)
        ''' <summary>Agent ids the user may see and act on; empty = all agents.</summary>
        Public Property AllowedAgents As New List(Of String)
        ''' <summary>Model ids (provider models and custom models) the user may use in Chat, the API and for agents; empty = every model.</summary>
        Public Property AllowedModels As New List(Of String)
        ''' <summary>MCP server ids the user's chats (and the agents it creates) may use; empty = every server.</summary>
        Public Property AllowedMcpServers As New List(Of String)
        ''' <summary>Chat and API tokens per day (UTC); 0 = no limit.</summary>
        Public Property DailyTokenLimit As Long
        ''' <summary>Chat messages and API calls per minute; 0 = no limit.</summary>
        Public Property RequestsPerMinute As Integer
        ''' <summary>The account's balance in micro-USD (User accounts pay for what they use from it).</summary>
        Public Property BalanceMicros As Long
        ''' <summary>"Tack on 15% surcharge": every fee this account pays is raised by <see cref="Economy.AccountService.SurchargePercent"/>.</summary>
        Public Property Surcharge As Boolean
        Public Property Disabled As Boolean
        ''' <summary>Bootstrap | Config | Dashboard</summary>
        Public Property Source As String
        Public Property CreatedUtc As DateTime
        Public Property LastLoginUtc As DateTime?
        Public Property PasswordChangedUtc As DateTime?
        Public Property UpdatedBy As String
        Public Property UpdatedUtc As DateTime?
        ''' <summary>First characters of the account's API key (Nothing = no key).</summary>
        Public Property ApiKeyPrefix As String
        Public Property ApiKeyCreatedUtc As DateTime?
        Public Property ApiKeyLastUsedUtc As DateTime?

        Public ReadOnly Property IsAdmin As Boolean
            Get
                Return String.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase)
            End Get
        End Property
    End Class

    Public Class StoredAgent
        Public Property Id As String
        Public Property Name As String
        Public Property Source As String
        Public Property Profile As ProfileOptions
        Public Property Enabled As Boolean
        Public Property DesiredState As String
        ''' <summary>The account that pays for the agent (Nothing = the administrators).</summary>
        Public Property Owner As String
        Public Property CreatedUtc As DateTime
        Public Property ModifiedUtc As DateTime
    End Class

    Public Class AuditRecord
        Public Property Id As Long
        Public Property Actor As String
        Public Property Action As String
        Public Property TargetType As String
        Public Property TargetId As String
        Public Property DetailsJson As String
        Public Property RemoteAddress As String
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>Agent definitions, communication topology, admin users, audit log and host key/value state.</summary>
    Public NotInheritable Class AgentRepository
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Public Function UpsertAgentAsync(profile As ProfileOptions, source As String, enabled As Boolean) As Task(Of Integer)
            Dim now = Db.Ts(DateTime.UtcNow)
            Return _db.ExecuteAsync(
                "INSERT INTO Agents(Id, Name, Source, ProfileJson, Enabled, CreatedUtc, ModifiedUtc) VALUES (@id, @n, @s, @p, @e, @now, @now) " &
                "ON CONFLICT(Id) DO UPDATE SET Name=@n, Source=@s, ProfileJson=@p, Enabled=@e, ModifiedUtc=@now, DeletedUtc=NULL",
                ("@id", CObj(profile.Id)), ("@n", If(profile.Name, profile.Id)), ("@s", source), ("@p", JsonUtil.ToJson(profile)), ("@e", enabled), ("@now", now))
        End Function

        Public Function SetDesiredStateAsync(agentId As String, state As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE Agents SET DesiredState=@s, ModifiedUtc=@now WHERE Id=@id", ("@s", CObj(state)), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", agentId))
        End Function

        Public Function MarkDeletedAsync(agentId As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE Agents SET DeletedUtc=@now, Enabled=0 WHERE Id=@id", ("@now", CObj(Db.Ts(DateTime.UtcNow))), ("@id", agentId))
        End Function

        ''' <summary>Source and deletion state of a stored agent, including deleted ones (Nothing when it was never stored).</summary>
        Public Async Function FindAgentRowAsync(id As String) As Task(Of (Source As String, Deleted As Boolean)?)
            Dim rows = Await _db.QueryAsync("SELECT Source, DeletedUtc FROM Agents WHERE Id=@id",
                                            Function(r) (Source:=Db.Str(r, "Source"), Deleted:=Db.Str(r, "DeletedUtc") IsNot Nothing), ("@id", CObj(id))).ConfigureAwait(False)
            If rows.Count = 0 Then Return Nothing
            Return rows(0)
        End Function

        Public Function ListAgentsAsync() As Task(Of List(Of StoredAgent))
            Return _db.QueryAsync("SELECT * FROM Agents WHERE DeletedUtc IS NULL ORDER BY Id",
                                  Function(r) New StoredAgent With {
                                      .Id = Db.Str(r, "Id"), .Name = Db.Str(r, "Name"), .Source = Db.Str(r, "Source"),
                                      .Profile = JsonUtil.FromJson(Of ProfileOptions)(Db.Str(r, "ProfileJson")), .Enabled = Db.Bool(r, "Enabled"),
                                      .DesiredState = Db.Str(r, "DesiredState"), .Owner = Db.Str(r, "Owner"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .ModifiedUtc = Db.Dt(r, "ModifiedUtc")})
        End Function

        ''' <summary>Sets the account that pays for an agent (Nothing = the administrators).</summary>
        Public Function SetAgentOwnerAsync(agentId As String, owner As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE Agents SET Owner=@o, ModifiedUtc=@now WHERE Id=@id", ("@o", CObj(owner)), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", agentId))
        End Function

        Public Async Function ReplaceCommunicationTopologyAsync(profiles As IEnumerable(Of EffectiveProfile)) As Task
            Dim list = profiles.ToList()
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Await Database.TxExecAsync(conn, tx, "DELETE FROM AgentCommunicationPermissions").ConfigureAwait(False)
                    For Each p In list
                        For Each rcp In p.AllowedRecipients
                            Await Database.TxExecAsync(conn, tx,
                                "INSERT OR REPLACE INTO AgentCommunicationPermissions(AgentId, RecipientAgentId, AllowedMessageTypes, MaxPriority) VALUES (@a, @r, @t, @p)",
                                ("@a", CObj(p.Id)), ("@r", rcp), ("@t", String.Join(",", p.AllowedSendMessageTypes.Select(Function(x) x.ToString()))),
                                ("@p", p.MaxSendPriority.ToString())).ConfigureAwait(False)
                        Next
                    Next
                    Return True
                End Function).ConfigureAwait(False)
        End Function

        ' ---------------- Admin users ----------------

        Public Function CountAdminUsersAsync() As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM AdminUsers")
        End Function

        Public Function UpsertAdminUserAsync(username As String, passwordHash As String, source As String) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO AdminUsers(Username, PasswordHash, Source, CreatedUtc, Role) VALUES (@u, @h, @s, @c, 'Admin') " &
                                    "ON CONFLICT(Username) DO UPDATE SET PasswordHash=@h, Source=@s",
                                    ("@u", CObj(username)), ("@h", passwordHash), ("@s", source), ("@c", Db.Ts(DateTime.UtcNow)))
        End Function

        ''' <summary>
        ''' Applies a user from appsettings.json: created as Admin if missing; its password is only replaced when the hash in the
        ''' config file changed since it was last applied (so a password changed in the dashboard survives restarts).
        ''' </summary>
        Public Function ApplyConfigUserAsync(username As String, passwordHash As String) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO AdminUsers(Username, PasswordHash, Source, CreatedUtc, Role, ConfigPasswordHash) VALUES (@u, @h, 'Config', @c, 'Admin', @h) " &
                                    "ON CONFLICT(Username) DO UPDATE SET Source='Config', " &
                                    "PasswordHash = CASE WHEN ConfigPasswordHash IS @h THEN PasswordHash ELSE @h END, ConfigPasswordHash=@h",
                                    ("@u", CObj(username)), ("@h", passwordHash), ("@c", Db.Ts(DateTime.UtcNow)))
        End Function

        Public Async Function GetAdminPasswordHashAsync(username As String) As Task(Of String)
            Return CStr(Await _db.ScalarAsync("SELECT PasswordHash FROM AdminUsers WHERE Username=@u", ("@u", CObj(username))).ConfigureAwait(False))
        End Function

        Private Shared Function MapUser(r As Microsoft.Data.Sqlite.SqliteDataReader) As DashboardUser
            Return New DashboardUser With {
                .Username = Db.Str(r, "Username"), .Role = If(Db.Str(r, "Role"), "Admin"), .DisplayName = Db.Str(r, "DisplayName"),
                .Permissions = JsonUtil.ParseStringList(Db.Str(r, "Permissions")), .AllowedAgents = JsonUtil.ParseStringList(Db.Str(r, "AllowedAgents")),
                .AllowedModels = JsonUtil.ParseStringList(Db.Str(r, "AllowedModels")), .BalanceMicros = Db.Lng(r, "BalanceMicros"),
                .AllowedMcpServers = JsonUtil.ParseStringList(Db.Str(r, "AllowedMcpServers")), .DailyTokenLimit = Db.Lng(r, "DailyTokenLimit"),
                .RequestsPerMinute = Db.Int(r, "RequestsPerMinute"),
                .Disabled = Db.Bool(r, "Disabled"), .Surcharge = Db.Bool(r, "Surcharge"), .Source = Db.Str(r, "Source"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .LastLoginUtc = Db.DtN(r, "LastLoginUtc"),
                .PasswordChangedUtc = Db.DtN(r, "PasswordChangedUtc"), .UpdatedBy = Db.Str(r, "UpdatedBy"), .UpdatedUtc = Db.DtN(r, "UpdatedUtc"),
                .ApiKeyPrefix = Db.Str(r, "ApiKeyPrefix"), .ApiKeyCreatedUtc = Db.DtN(r, "ApiKeyCreatedUtc"), .ApiKeyLastUsedUtc = Db.DtN(r, "ApiKeyLastUsedUtc")}
        End Function

        ''' <summary>Stores a new API key for the account (only its hash and a short prefix to recognise it).</summary>
        Public Function SetApiKeyAsync(username As String, keyHash As String, prefix As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AdminUsers SET ApiKeyHash=@h, ApiKeyPrefix=@p, ApiKeyCreatedUtc=@n, ApiKeyLastUsedUtc=NULL WHERE Username=@u",
                                    ("@h", CObj(keyHash)), ("@p", prefix), ("@n", Db.Ts(DateTime.UtcNow)), ("@u", username))
        End Function

        Public Function ClearApiKeyAsync(username As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AdminUsers SET ApiKeyHash=NULL, ApiKeyPrefix=NULL, ApiKeyCreatedUtc=NULL, ApiKeyLastUsedUtc=NULL WHERE Username=@u", ("@u", CObj(username)))
        End Function

        Public Function FindUserByApiKeyAsync(keyHash As String) As Task(Of DashboardUser)
            Return _db.QuerySingleAsync("SELECT * FROM AdminUsers WHERE ApiKeyHash=@h", AddressOf MapUser, ("@h", CObj(keyHash)))
        End Function

        Public Function TouchApiKeyAsync(username As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AdminUsers SET ApiKeyLastUsedUtc=@n WHERE Username=@u", ("@n", CObj(Db.Ts(DateTime.UtcNow))), ("@u", username))
        End Function

        Public Function ListUsersAsync() As Task(Of List(Of DashboardUser))
            Return _db.QueryAsync("SELECT * FROM AdminUsers ORDER BY CASE Role WHEN 'Admin' THEN 0 ELSE 1 END, Username", AddressOf MapUser)
        End Function

        Public Function GetUserAsync(username As String) As Task(Of DashboardUser)
            Return _db.QuerySingleAsync("SELECT * FROM AdminUsers WHERE Username=@u", AddressOf MapUser, ("@u", CObj(username)))
        End Function

        Public Function CreateUserAsync(u As DashboardUser, passwordHash As String, actor As String) As Task(Of Integer)
            Dim now = Db.Ts(DateTime.UtcNow)
            Return _db.ExecuteAsync("INSERT INTO AdminUsers(Username, PasswordHash, Source, CreatedUtc, Role, Permissions, AllowedAgents, AllowedModels, AllowedMcpServers, DailyTokenLimit, RequestsPerMinute, DisplayName, Disabled, Surcharge, PasswordChangedUtc, UpdatedBy, UpdatedUtc) " &
                                    "VALUES (@u, @h, 'Dashboard', @n, @r, @p, @a, @am, @mcp, @tok, @rpm, @d, 0, @s, @n, @by, @n)",
                                    ("@u", CObj(u.Username)), ("@h", passwordHash), ("@n", now), ("@r", u.Role), ("@p", JsonUtil.ToJson(u.Permissions)),
                                    ("@a", JsonUtil.ToJson(u.AllowedAgents)), ("@am", ModelsJson(u)), ("@mcp", McpJson(u)), ("@tok", LimitOrNull(u.DailyTokenLimit)),
                                    ("@rpm", LimitOrNull(u.RequestsPerMinute)), ("@d", u.DisplayName), ("@by", actor), ("@s", u.Surcharge))
        End Function

        Public Function UpdateUserAsync(u As DashboardUser, actor As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AdminUsers SET Role=@r, Permissions=@p, AllowedAgents=@a, AllowedModels=@am, AllowedMcpServers=@mcp, DailyTokenLimit=@tok, RequestsPerMinute=@rpm, " &
                                    "DisplayName=@d, Disabled=@x, Surcharge=@s, UpdatedBy=@by, UpdatedUtc=@n WHERE Username=@u",
                                    ("@r", CObj(u.Role)), ("@p", JsonUtil.ToJson(u.Permissions)), ("@a", JsonUtil.ToJson(u.AllowedAgents)), ("@am", ModelsJson(u)), ("@mcp", McpJson(u)),
                                    ("@tok", LimitOrNull(u.DailyTokenLimit)), ("@rpm", LimitOrNull(u.RequestsPerMinute)), ("@d", u.DisplayName),
                                    ("@x", u.Disabled), ("@s", u.Surcharge), ("@by", actor), ("@n", Db.Ts(DateTime.UtcNow)), ("@u", u.Username))
        End Function

        ''' <summary>NULL = every MCP server.</summary>
        Private Shared Function McpJson(u As DashboardUser) As String
            Return If(u.AllowedMcpServers Is Nothing OrElse u.AllowedMcpServers.Count = 0, Nothing, JsonUtil.ToJson(u.AllowedMcpServers))
        End Function

        ''' <summary>NULL = no limit.</summary>
        Private Shared Function LimitOrNull(value As Long) As Object
            Return If(value > 0, CObj(value), Nothing)
        End Function

        ''' <summary>NULL = every model.</summary>
        Private Shared Function ModelsJson(u As DashboardUser) As String
            Return If(u.AllowedModels Is Nothing OrElse u.AllowedModels.Count = 0, Nothing, JsonUtil.ToJson(u.AllowedModels))
        End Function

        ' ------------------------------------------------------------------ web sign-ins (kept across restarts)

        ''' <summary>Stores a web sign-in under the SHA-256 of its session id.</summary>
        Public Function SaveWebSessionAsync(idHash As String, username As String, csrfToken As String, expiresUtc As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT OR REPLACE INTO WebSessions(IdHash, Username, CsrfToken, ExpiresUtc, CreatedUtc) VALUES (@h, @u, @c, @e, @n)",
                                    ("@h", CObj(idHash)), ("@u", username), ("@c", csrfToken), ("@e", Db.Ts(expiresUtc)), ("@n", Db.Ts(DateTime.UtcNow)))
        End Function

        ''' <summary>A stored web sign-in: (username, CSRF token, expiry), or Nothing.</summary>
        Public Async Function GetWebSessionAsync(idHash As String) As Task(Of (Username As String, CsrfToken As String, ExpiresUtc As DateTime)?)
            Dim rows = Await _db.QueryAsync("SELECT Username, CsrfToken, ExpiresUtc FROM WebSessions WHERE IdHash=@h",
                                            Function(r) (Db.Str(r, "Username"), Db.Str(r, "CsrfToken"), Db.Dt(r, "ExpiresUtc")), ("@h", CObj(idHash))).ConfigureAwait(False)
            If rows.Count = 0 Then Return Nothing
            Return rows(0)
        End Function

        Public Function TouchWebSessionAsync(idHash As String, expiresUtc As DateTime) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE WebSessions SET ExpiresUtc=@e WHERE IdHash=@h", ("@e", CObj(Db.Ts(expiresUtc))), ("@h", idHash))
        End Function

        Public Function DeleteWebSessionAsync(idHash As String) As Task(Of Integer)
            Return _db.ExecuteAsync("DELETE FROM WebSessions WHERE IdHash=@h", ("@h", CObj(idHash)))
        End Function

        ''' <summary>Ends a user's stored sign-ins, except one (by hash). Returns how many ended.</summary>
        Public Function DeleteWebSessionsOfUserAsync(username As String, Optional exceptIdHash As String = Nothing) As Task(Of Integer)
            Return _db.ExecuteAsync("DELETE FROM WebSessions WHERE Username=@u COLLATE NOCASE AND IdHash<>@x", ("@u", CObj(username)), ("@x", If(exceptIdHash, "")))
        End Function

        Public Function DeleteExpiredWebSessionsAsync() As Task(Of Integer)
            Return _db.ExecuteAsync("DELETE FROM WebSessions WHERE ExpiresUtc<@n", ("@n", CObj(Db.Ts(DateTime.UtcNow))))
        End Function

        Public Function SetPasswordAsync(username As String, passwordHash As String, actor As String) As Task(Of Integer)
            Dim now = Db.Ts(DateTime.UtcNow)
            Return _db.ExecuteAsync("UPDATE AdminUsers SET PasswordHash=@h, PasswordChangedUtc=@n, UpdatedBy=@by, UpdatedUtc=@n WHERE Username=@u",
                                    ("@h", CObj(passwordHash)), ("@n", now), ("@by", actor), ("@u", username))
        End Function

        Public Function DeleteUserAsync(username As String) As Task(Of Integer)
            Return _db.ExecuteAsync("DELETE FROM AdminUsers WHERE Username=@u", ("@u", CObj(username)))
        End Function

        Public Function TouchAdminLoginAsync(username As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AdminUsers SET LastLoginUtc=@c WHERE Username=@u", ("@c", CObj(Db.Ts(DateTime.UtcNow))), ("@u", username))
        End Function

        ' ---------------- Audit ----------------

        Public Function AuditAsync(actor As String, action As String, targetType As String, targetId As String, details As Object, remoteAddress As String) As Task(Of Integer)
            Dim detailsJson As String = Nothing
            If details IsNot Nothing Then
                Dim full = JsonUtil.ToJson(details)
                detailsJson = TextUtil.Truncate(full, 8000)
                ' Flag truncation in the JSON itself for forensic clarity
                If detailsJson.Length < full.Length Then
                    detailsJson = detailsJson.TrimEnd("}"c) & ",""_truncated"":true}"
                End If
            End If
            Return _db.ExecuteAsync("INSERT INTO AuditLog(Actor, Action, TargetType, TargetId, DetailsJson, RemoteAddress, CreatedUtc) VALUES (@a, @ac, @tt, @ti, @d, @r, @c)",
                                    ("@a", CObj(actor)), ("@ac", action), ("@tt", targetType), ("@ti", targetId),
                                    ("@d", detailsJson), ("@r", remoteAddress),
                                    ("@c", Db.Ts(DateTime.UtcNow)))
        End Function

        Public Function ListAuditAsync(limit As Integer, Optional targetId As String = Nothing) As Task(Of List(Of AuditRecord))
            Dim where = If(String.IsNullOrEmpty(targetId), "", "WHERE TargetId=@t")
            Return _db.QueryAsync($"SELECT * FROM AuditLog {where} ORDER BY Id DESC LIMIT @l",
                                  Function(r) New AuditRecord With {
                                      .Id = Db.Lng(r, "Id"), .Actor = Db.Str(r, "Actor"), .Action = Db.Str(r, "Action"), .TargetType = Db.Str(r, "TargetType"),
                                      .TargetId = Db.Str(r, "TargetId"), .DetailsJson = Db.Str(r, "DetailsJson"), .RemoteAddress = Db.Str(r, "RemoteAddress"),
                                      .CreatedUtc = Db.Dt(r, "CreatedUtc")},
                                  ("@t", CObj(targetId)), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        ' ---------------- Host state ----------------

        Public Async Function GetStateAsync(key As String) As Task(Of String)
            Dim result = Await _db.ScalarAsync("SELECT Value FROM HostState WHERE Key=@k", ("@k", CObj(key))).ConfigureAwait(False)
            ' Return Nothing when key does not exist, not an empty string
            If result Is Nothing Then Return Nothing
            Return CStr(result)
        End Function

        Public Function SetStateAsync(key As String, value As String) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO HostState(Key, Value) VALUES (@k, @v) ON CONFLICT(Key) DO UPDATE SET Value=@v", ("@k", CObj(key)), ("@v", value))
        End Function
    End Class

End Namespace
