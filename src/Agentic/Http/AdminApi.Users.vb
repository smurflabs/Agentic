Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities

Namespace Http

    ' Admin API: dashboard user accounts (admins manage users; everyone can change their own password).
    Partial Friend NotInheritable Class AdminApi

        Private Shared ReadOnly UsernamePattern As New Regex("^[A-Za-z0-9][A-Za-z0-9._-]{2,49}$", RegexOptions.Compiled)
        Private Const MinPasswordLength As Integer = 10

        Friend Sub RegisterUsers(r As Router)
            r.Map("POST", "/api/auth/password", AuthLevel.Read, AddressOf ChangeOwnPassword)
            r.Map("GET", "/api/users", AuthLevel.Admin, AddressOf ListUsers)
            r.Map("POST", "/api/users", AuthLevel.Admin, AddressOf CreateUser)
            r.Map("PATCH", "/api/users/{username}", AuthLevel.Admin, AddressOf UpdateUser)
            r.Map("POST", "/api/users/{username}/password", AuthLevel.Admin, AddressOf ResetUserPassword)
            r.Map("DELETE", "/api/users/{username}", AuthLevel.Admin, AddressOf DeleteUser)
        End Sub

        ''' <summary>
        ''' Agent scoping for users limited to specific agents (and token allowlists), or without the Workforce (no agents at
        ''' all). A subagent counts as its parent; records without an agent (host-wide) stay visible.
        ''' </summary>
        Friend Shared Function Sees(rc As RequestContext, agentId As String) As Boolean
            If String.IsNullOrEmpty(agentId) Then Return True
            Dim i = agentId.IndexOf(".sub-", StringComparison.Ordinal)
            Return rc.User.CanAccessAgent(If(i > 0, agentId.Substring(0, i), agentId))
        End Function

        ''' <summary>A message is visible when its sender or recipient is one of the user's agents.</summary>
        Friend Shared Function MessageVisible(rc As RequestContext, m As Communication.AgentMessage) As Boolean
            If rc.User.SeesAllAgents Then Return True
            Return (Not String.IsNullOrEmpty(m.SenderAgentId) AndAlso rc.User.CanAccessAgent(ParentId(m.SenderAgentId))) OrElse
                   (Not String.IsNullOrEmpty(m.RecipientAgentId) AndAlso rc.User.CanAccessAgent(ParentId(m.RecipientAgentId)))
        End Function

        Private Shared Function ParentId(agentId As String) As String
            Dim i = agentId.IndexOf(".sub-", StringComparison.Ordinal)
            Return If(i > 0, agentId.Substring(0, i), agentId)
        End Function

        Private Shared Sub RequireAgentAccess(rc As RequestContext, agentId As String)
            If Not String.IsNullOrEmpty(agentId) AndAlso Not Sees(rc, agentId) Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to this agent.")
        End Sub

        Private Shared Sub CheckPassword(password As String, username As String)
            If String.IsNullOrEmpty(password) OrElse password.Length < MinPasswordLength Then Throw ApiException.BadRequest($"The password must have at least {MinPasswordLength} characters.")
            If password.Length > 200 Then Throw ApiException.BadRequest("The password is too long.")
            If String.Equals(password, username, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.BadRequest("The password must not be the username.")
        End Sub

        Private Shared Function UserDto(u As DashboardUser) As Object
            Return New With {
                .username = u.Username, .displayName = u.DisplayName, .role = u.Role, .permissions = If(u.IsAdmin, Permissions.Catalog.Select(Function(p) p.Key).ToList(), u.Permissions),
                .allowedAgents = u.AllowedAgents, .allAgents = u.IsAdmin OrElse u.AllowedAgents.Count = 0, .disabled = u.Disabled, .source = u.Source,
                .allowedModels = u.AllowedModels, .allModels = u.IsAdmin OrElse u.AllowedModels.Count = 0, .balanceMicros = u.BalanceMicros, .charged = Not u.IsAdmin, .surcharge = u.Surcharge AndAlso Not u.IsAdmin,
                .allowedMcpServers = u.AllowedMcpServers, .allMcp = u.IsAdmin OrElse u.AllowedMcpServers.Count = 0,
                .dailyTokenLimit = If(u.IsAdmin, 0, u.DailyTokenLimit), .requestsPerMinute = If(u.IsAdmin, 0, u.RequestsPerMinute),
                .createdUtc = u.CreatedUtc, .lastLoginUtc = u.LastLoginUtc, .passwordChangedUtc = u.PasswordChangedUtc, .updatedBy = u.UpdatedBy, .updatedUtc = u.UpdatedUtc,
                .apiKeyPrefix = u.ApiKeyPrefix, .apiKeyCreatedUtc = u.ApiKeyCreatedUtc, .apiKeyLastUsedUtc = u.ApiKeyLastUsedUtc}
        End Function

        Private Async Function EnabledAdminCountAsync(Optional excluding As String = Nothing) As Task(Of Integer)
            Return (Await _h.AgentsRepo.ListUsersAsync().ConfigureAwait(False)).
                Where(Function(u) u.IsAdmin AndAlso Not u.Disabled AndAlso Not String.Equals(u.Username, excluding, StringComparison.OrdinalIgnoreCase)).Count()
        End Function

        ''' <summary>Reads role, permissions and agent scope from a request body into a user record.</summary>
        Private Sub ApplyAccess(u As DashboardUser, b As JsonElement)
            Dim role = JsonUtil.GetString(b, "role")
            If role IsNot Nothing Then
                If Not {"Admin", "User"}.Contains(role, StringComparer.OrdinalIgnoreCase) Then Throw ApiException.BadRequest("role must be Admin or User.")
                u.Role = If(role.Equals("Admin", StringComparison.OrdinalIgnoreCase), "Admin", "User")
            End If
            Dim el As JsonElement
            If b.TryGetProperty("permissions", el) AndAlso el.ValueKind = JsonValueKind.Array Then
                Dim list = JsonUtil.GetStringList(b, "permissions")
                Dim unknown = list.Where(Function(p) Not Permissions.IsKnown(p)).ToList()
                If unknown.Count > 0 Then Throw ApiException.BadRequest("Unknown permission(s): " & String.Join(", ", unknown))
                u.Permissions = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            End If
            If b.TryGetProperty("allowedAgents", el) AndAlso (el.ValueKind = JsonValueKind.Array OrElse el.ValueKind = JsonValueKind.Null) Then
                Dim list = If(el.ValueKind = JsonValueKind.Null, New List(Of String), JsonUtil.GetStringList(b, "allowedAgents"))
                Dim unknown = list.Where(Function(a) Not _h.Agents.Exists(a)).ToList()
                If unknown.Count > 0 Then Throw ApiException.BadRequest("Unknown agent(s): " & String.Join(", ", unknown))
                u.AllowedAgents = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            End If
            ' The models (provider model ids, custom model ids) the account may use; null or [] = every model.
            If b.TryGetProperty("allowedModels", el) AndAlso (el.ValueKind = JsonValueKind.Array OrElse el.ValueKind = JsonValueKind.Null) Then
                Dim list = If(el.ValueKind = JsonValueKind.Null, New List(Of String), JsonUtil.GetStringList(b, "allowedModels"))
                If list.Any(Function(m) String.IsNullOrWhiteSpace(m) OrElse m.Length > 200) Then Throw ApiException.BadRequest("allowedModels: model ids must be 1-200 characters.")
                If list.Count > 2000 Then Throw ApiException.BadRequest("allowedModels: too many models.")
                u.AllowedModels = list.Select(Function(m) m.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            End If
            ' The MCP servers the account's chats (and the agents it creates) may use; null or [] = every server.
            If b.TryGetProperty("allowedMcpServers", el) AndAlso (el.ValueKind = JsonValueKind.Array OrElse el.ValueKind = JsonValueKind.Null) Then
                Dim list = If(el.ValueKind = JsonValueKind.Null, New List(Of String), JsonUtil.GetStringList(b, "allowedMcpServers"))
                Dim unknown = list.Where(Function(s) Not _h.Options.McpServers.Any(Function(m) String.Equals(m.Id, s, StringComparison.OrdinalIgnoreCase))).ToList()
                If unknown.Count > 0 Then Throw ApiException.BadRequest("Unknown MCP server(s): " & String.Join(", ", unknown))
                u.AllowedMcpServers = list.Select(Function(s) _h.Options.McpServers.First(Function(m) String.Equals(m.Id, s, StringComparison.OrdinalIgnoreCase)).Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            End If
            ' Limits: chat and API tokens per day, and chat messages and API calls per minute (0 or null = no limit).
            If b.TryGetProperty("dailyTokenLimit", el) Then u.DailyTokenLimit = LimitValue(el, "dailyTokenLimit", 1_000_000_000_000L)
            If b.TryGetProperty("requestsPerMinute", el) Then u.RequestsPerMinute = CInt(LimitValue(el, "requestsPerMinute", 100_000L))
            ' "Tack on 15% surcharge": every fee the account pays is raised by AccountService.SurchargePercent.
            If b.TryGetProperty("surcharge", el) AndAlso (el.ValueKind = JsonValueKind.True OrElse el.ValueKind = JsonValueKind.False) Then u.Surcharge = el.GetBoolean()
            Dim display = JsonUtil.GetString(b, "displayName")
            If display IsNot Nothing Then u.DisplayName = If(String.IsNullOrWhiteSpace(display), Nothing, TextUtil.Truncate(display.Trim(), 80))
            If u.IsAdmin Then
                u.Permissions = New List(Of String)
                u.AllowedAgents = New List(Of String)
                u.AllowedModels = New List(Of String)
                u.AllowedMcpServers = New List(Of String)
                u.DailyTokenLimit = 0
                u.RequestsPerMinute = 0
            End If
        End Sub

        ''' <summary>A limit from the request: a whole number from 0 (no limit) to max; null = no limit.</summary>
        Private Shared Function LimitValue(el As JsonElement, name As String, max As Long) As Long
            If el.ValueKind = JsonValueKind.Null Then Return 0
            Dim v As Long
            If el.ValueKind = JsonValueKind.Number AndAlso el.TryGetInt64(v) AndAlso v >= 0 AndAlso v <= max Then Return v
            If el.ValueKind = JsonValueKind.String AndAlso (el.GetString().Trim() = "" OrElse Long.TryParse(el.GetString().Trim(), v) AndAlso v >= 0 AndAlso v <= max) Then Return v
            Throw ApiException.BadRequest($"{name} must be a whole number from 0 (no limit) to {max:N0}.")
        End Function

        Private Async Function ListUsers(rc As RequestContext) As Task
            Dim users = Await _h.AgentsRepo.ListUsersAsync().ConfigureAwait(False)
            Await rc.OkAsync(New With {.users = users.Select(AddressOf UserDto).ToList(), .permissions = Permissions.Catalog, .minPasswordLength = MinPasswordLength,
                                       .mcpServers = _h.Options.McpServers.Select(Function(m) New With {.id = m.Id, .name = If(m.Name, m.Id), .enabled = m.Enabled, .description = m.Description}).ToList()}).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {username, password, displayName?, role: Admin|User, permissions: [..], allowedAgents: [..] (empty = all)}</summary>
        Private Async Function CreateUser(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim username = If(JsonUtil.GetString(b, "username"), "").Trim()
            If Not UsernamePattern.IsMatch(username) Then Throw ApiException.BadRequest("Username: 3-50 letters, digits, dots, dashes or underscores, starting with a letter or digit.")
            If Await _h.AgentsRepo.GetUserAsync(username).ConfigureAwait(False) IsNot Nothing Then Throw New ApiException(409, "USER_EXISTS", $"User '{username}' already exists.")
            Dim password = JsonUtil.GetString(b, "password")
            CheckPassword(password, username)
            Dim u As New DashboardUser With {.Username = username, .Role = "User"}
            ApplyAccess(u, b)
            Await _h.AgentsRepo.CreateUserAsync(u, PasswordHasher.Hash(password), rc.User.Name).ConfigureAwait(False)
            Await AuditAsync(rc, "create-user", "user", username, New With {.role = u.Role, .permissions = u.Permissions, .allowedAgents = u.AllowedAgents, .allowedModels = u.AllowedModels, .allowedMcpServers = u.AllowedMcpServers, .dailyTokenLimit = u.DailyTokenLimit, .requestsPerMinute = u.RequestsPerMinute, .surcharge = u.Surcharge}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, UserDto(Await _h.AgentsRepo.GetUserAsync(username).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>Body: any of {displayName, role, permissions, allowedAgents, disabled}. The user is signed out so the change applies at once.</summary>
        Private Async Function UpdateUser(rc As RequestContext) As Task
            Dim username = rc.Route("username")
            Dim u = Await _h.AgentsRepo.GetUserAsync(username).ConfigureAwait(False)
            If u Is Nothing Then Throw ApiException.NotFound("USER_NOT_FOUND", "User does not exist.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim wasAdmin = u.IsAdmin AndAlso Not u.Disabled
            ApplyAccess(u, b)
            Dim disabledEl As JsonElement
            If b.TryGetProperty("disabled", disabledEl) AndAlso (disabledEl.ValueKind = JsonValueKind.True OrElse disabledEl.ValueKind = JsonValueKind.False) Then u.Disabled = disabledEl.GetBoolean()
            Dim isSelf = String.Equals(username, rc.User.Name, StringComparison.OrdinalIgnoreCase)
            If isSelf AndAlso (Not u.IsAdmin OrElse u.Disabled) Then Throw ApiException.BadRequest("You cannot remove your own administrator role or disable your own account.")
            If wasAdmin AndAlso (Not u.IsAdmin OrElse u.Disabled) AndAlso Await EnabledAdminCountAsync(username).ConfigureAwait(False) = 0 Then
                Throw ApiException.BadRequest("At least one enabled administrator must remain.")
            End If
            Await _h.AgentsRepo.UpdateUserAsync(u, rc.User.Name).ConfigureAwait(False)
            _h.Accounts.ForgetSurcharge(username)
            Dim signedOut = If(isSelf, 0, _auth.LogoutUser(username))
            ' Administrators' agents never stop for money; a User account's agents run only while it has money.
            If wasAdmin <> (u.IsAdmin AndAlso Not u.Disabled) Then Await _h.Accounts.AnnounceAsync(username).ConfigureAwait(False)
            Await AuditAsync(rc, "update-user", "user", username, New With {.role = u.Role, .permissions = u.Permissions, .allowedAgents = u.AllowedAgents, .allowedModels = u.AllowedModels, .allowedMcpServers = u.AllowedMcpServers, .dailyTokenLimit = u.DailyTokenLimit, .requestsPerMinute = u.RequestsPerMinute, .disabled = u.Disabled, .surcharge = u.Surcharge}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.user = UserDto(Await _h.AgentsRepo.GetUserAsync(username).ConfigureAwait(False)), .sessionsEnded = signedOut}).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {password}. Admin reset of another user's password; their sessions end.</summary>
        Private Async Function ResetUserPassword(rc As RequestContext) As Task
            Dim username = rc.Route("username")
            If Await _h.AgentsRepo.GetUserAsync(username).ConfigureAwait(False) Is Nothing Then Throw ApiException.NotFound("USER_NOT_FOUND", "User does not exist.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim password = JsonUtil.GetString(b, "password")
            CheckPassword(password, username)
            Await _h.AgentsRepo.SetPasswordAsync(username, PasswordHasher.Hash(password), rc.User.Name).ConfigureAwait(False)
            _auth.LogoutUser(username, rc.User.SessionId)
            Await AuditAsync(rc, "reset-password", "user", username).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteUser(rc As RequestContext) As Task
            Dim username = rc.Route("username")
            Dim u = Await _h.AgentsRepo.GetUserAsync(username).ConfigureAwait(False)
            If u Is Nothing Then Throw ApiException.NotFound("USER_NOT_FOUND", "User does not exist.")
            If String.Equals(username, rc.User.Name, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.BadRequest("You cannot delete your own account.")
            If u.IsAdmin AndAlso Not u.Disabled AndAlso Await EnabledAdminCountAsync(username).ConfigureAwait(False) = 0 Then Throw ApiException.BadRequest("At least one enabled administrator must remain.")
            If String.Equals(u.Source, "Config", StringComparison.OrdinalIgnoreCase) Then Throw ApiException.BadRequest("This user is defined in appsettings.json (Admin.Users); remove it there.")
            Await _h.AgentsRepo.DeleteUserAsync(username).ConfigureAwait(False)
            _auth.LogoutUser(username)
            Await AuditAsync(rc, "delete-user", "user", username).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {currentPassword, newPassword}. Any signed-in dashboard user; other sessions of the user end.</summary>
        Private Async Function ChangeOwnPassword(rc As RequestContext) As Task
            If Not rc.User.ViaSession Then Throw New ApiException(403, "FORBIDDEN", "Only dashboard sessions can change a password.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim current = JsonUtil.GetString(b, "currentPassword"), newPassword = JsonUtil.GetString(b, "newPassword")
            If Not Await _auth.VerifyPasswordAsync(rc.User.Name, current).ConfigureAwait(False) Then Throw ApiException.BadRequest("The current password is wrong.")
            CheckPassword(newPassword, rc.User.Name)
            If newPassword = current Then Throw ApiException.BadRequest("The new password must differ from the current one.")
            Await _h.AgentsRepo.SetPasswordAsync(rc.User.Name, PasswordHasher.Hash(newPassword), rc.User.Name).ConfigureAwait(False)
            Dim ended = _auth.LogoutUser(rc.User.Name, rc.User.SessionId)
            Await AuditAsync(rc, "change-own-password", "user", rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True, .otherSessionsEnded = ended}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
