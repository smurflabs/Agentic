Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.RegularExpressions
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A vault entry as people and agents see it: never its value.</summary>
    Public NotInheritable Class SecretInfo
        Public Property Name As String
        Public Property Description As String
        ''' <summary>Agents that may use it ("*" = every agent).</summary>
        Public Property AgentIds As New List(Of String)
        ''' <summary>Hosts it may be sent to (example.com, *.example.com); empty = any host the agent may call.</summary>
        Public Property Hosts As New List(Of String)
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
        Public Property LastUsedUtc As DateTime?
        Public Property Uses As Long

        Public Function MayUse(agentId As String) As Boolean
            Return AgentIds IsNot Nothing AndAlso (AgentIds.Contains("*") OrElse AgentIds.Contains(agentId, StringComparer.OrdinalIgnoreCase))
        End Function

        ''' <summary>Whether it may go to this host (always, when no hosts are set).</summary>
        Public Function MayGoTo(host As String) As Boolean
            If Hosts Is Nothing OrElse Hosts.Count = 0 Then Return True
            Return SecretVault.HostMatches(Hosts, host)
        End Function
    End Class

    ''' <summary>
    ''' Values tools use without the model ever seeing them: an API key in http_request ({{secret:NAME}}), a mailbox password.
    ''' Values are encrypted with the host's secrets.key, never returned by the API, and kept out of logs and tool output.
    ''' A secret bound to hosts is only ever sent to them, so an agent tricked by a web page cannot send it elsewhere.
    ''' </summary>
    Public NotInheritable Class SecretVault
        Private ReadOnly _db As Database
        Private ReadOnly _box As SecretBox
        Private ReadOnly _redactor As SecretRedactor

        Public Sub New(db As Database, box As SecretBox, Optional redactor As SecretRedactor = Nothing)
            _db = db
            _box = box
            _redactor = redactor
        End Sub

        Private Shared ReadOnly NamePattern As New Regex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$", RegexOptions.Compiled)
        Private Const InfoColumns As String = "Name, Description, AgentIds, Hosts, CreatedBy, CreatedUtc, UpdatedUtc, LastUsedUtc, Uses"

        Public Shared Function IsValidName(name As String) As Boolean
            Return name IsNot Nothing AndAlso NamePattern.IsMatch(name)
        End Function

        ''' <summary>example.com matches itself; *.example.com matches its subdomains and example.com; * matches everything.</summary>
        Public Shared Function HostMatches(patterns As IEnumerable(Of String), host As String) As Boolean
            Dim h = If(host, "").Trim().ToLowerInvariant()
            If h.Length = 0 Then Return False
            For Each raw In patterns
                Dim p = If(raw, "").Trim().ToLowerInvariant()
                If p = "*" OrElse p = h Then Return True
                If p.StartsWith("*.", StringComparison.Ordinal) AndAlso (h.EndsWith(p.Substring(1), StringComparison.Ordinal) OrElse h = p.Substring(2)) Then Return True
            Next
            Return False
        End Function

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As SecretInfo
            Return New SecretInfo With {
                .Name = Db.Str(r, "Name"), .Description = Db.Str(r, "Description"),
                .AgentIds = If(JsonUtil.FromJson(Of List(Of String))(If(Db.Str(r, "AgentIds"), "[]")), New List(Of String)),
                .Hosts = If(JsonUtil.FromJson(Of List(Of String))(If(Db.Str(r, "Hosts"), "[]")), New List(Of String)),
                .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc"),
                .LastUsedUtc = Db.DtN(r, "LastUsedUtc"), .Uses = Db.Lng(r, "Uses")}
        End Function

        ''' <summary>Startup: every value is registered with the log redactor, so none can reach a log file.</summary>
        Public Async Function LoadAsync() As Task
            If _redactor Is Nothing Then Return
            For Each boxed In Await _db.QueryAsync("SELECT Value FROM AgentSecrets", Function(r) Db.Str(r, "Value")).ConfigureAwait(False)
                Try
                    _redactor.AddSecret(_box.Unprotect(boxed))
                Catch ex As Exception
                End Try
            Next
        End Function

        ''' <summary>The vault (or only the entries one agent may use).</summary>
        Public Async Function ListAsync(Optional agentId As String = Nothing) As Task(Of List(Of SecretInfo))
            Dim all = Await _db.QueryAsync($"SELECT {InfoColumns} FROM AgentSecrets ORDER BY Name", AddressOf Map).ConfigureAwait(False)
            Return If(agentId Is Nothing, all, all.Where(Function(s) s.MayUse(agentId)).ToList())
        End Function

        Public Function GetInfoAsync(name As String) As Task(Of SecretInfo)
            Return _db.QuerySingleAsync($"SELECT {InfoColumns} FROM AgentSecrets WHERE Name=@n", AddressOf Map, ("@n", CObj(name)))
        End Function

        Private Shared Function CleanList(items As IEnumerable(Of String), lower As Boolean) As List(Of String)
            Return If(items, Enumerable.Empty(Of String)()).Where(Function(x) Not String.IsNullOrWhiteSpace(x)).
                Select(Function(x) If(lower, x.Trim().ToLowerInvariant(), x.Trim())).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Adds or changes an entry. value Nothing keeps the stored value (a new entry needs one).</summary>
        Public Async Function SetAsync(name As String, value As String, description As String, agentIds As IEnumerable(Of String), actor As String,
                                       Optional hosts As IEnumerable(Of String) = Nothing) As Task(Of SecretInfo)
            If Not IsValidName(name) Then Throw New ArgumentException("A secret name starts with a letter and has up to 64 letters, digits, _ . or -.")
            Dim existing = Await GetInfoAsync(name).ConfigureAwait(False)
            If existing Is Nothing AndAlso String.IsNullOrEmpty(value) Then Throw New ArgumentException("A new secret needs a value.")
            If value IsNot Nothing AndAlso value.Length > 16000 Then Throw New ArgumentException("A secret value can have at most 16,000 characters.")
            Dim ids = CleanList(agentIds, False)
            Dim hostList = CleanList(hosts, True)
            For Each h In hostList
                If h.Contains("/") OrElse h.Contains(" ") OrElse h.Contains(":") Then Throw New ArgumentException($"'{h}' is not a host name (api.example.com or *.example.com).")
            Next
            Dim now = Db.Ts(DateTime.UtcNow)
            If existing Is Nothing Then
                Await _db.ExecuteAsync("INSERT INTO AgentSecrets(Name, Value, Description, AgentIds, Hosts, CreatedBy, CreatedUtc, UpdatedUtc, Uses) VALUES (@n, @v, @d, @a, @h, @by, @now, @now, 0)",
                                       ("@n", CObj(name)), ("@v", _box.Protect(value)), ("@d", TextUtil.Truncate(description, 500)), ("@a", JsonUtil.ToJson(ids)),
                                       ("@h", JsonUtil.ToJson(hostList)), ("@by", actor), ("@now", now)).ConfigureAwait(False)
            Else
                Await _db.ExecuteAsync("UPDATE AgentSecrets SET Value=COALESCE(@v, Value), Description=@d, AgentIds=@a, Hosts=@h, UpdatedUtc=@now WHERE Name=@n",
                                       ("@n", CObj(name)), ("@v", If(String.IsNullOrEmpty(value), Nothing, CObj(_box.Protect(value)))),
                                       ("@d", TextUtil.Truncate(description, 500)), ("@a", JsonUtil.ToJson(ids)), ("@h", JsonUtil.ToJson(hostList)), ("@now", now)).ConfigureAwait(False)
            End If
            If Not String.IsNullOrEmpty(value) Then _redactor?.AddSecret(value)
            Return Await GetInfoAsync(name).ConfigureAwait(False)
        End Function

        Public Async Function DeleteAsync(name As String) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("DELETE FROM AgentSecrets WHERE Name=@n", ("@n", CObj(name))).ConfigureAwait(False)) > 0
        End Function

        ''' <summary>
        ''' The value of a secret an agent may use, for a request to this host (Nothing when it may not, it is bound to other
        ''' hosts, or there is none). Counts the use.
        ''' </summary>
        Public Async Function ResolveForAgentAsync(agentId As String, name As String, Optional host As String = Nothing) As Task(Of String)
            If Not IsValidName(name) Then Return Nothing
            Dim row = Await _db.QuerySingleAsync("SELECT * FROM AgentSecrets WHERE Name=@n",
                                                 Function(r) New With {.Info = Map(r), .Value = Db.Str(r, "Value")}, ("@n", CObj(name))).ConfigureAwait(False)
            If row Is Nothing OrElse Not row.Info.MayUse(agentId) Then Return Nothing
            If host IsNot Nothing AndAlso Not row.Info.MayGoTo(host) Then Return Nothing
            Await _db.ExecuteAsync("UPDATE AgentSecrets SET Uses=Uses+1, LastUsedUtc=@now WHERE Name=@n", ("@now", CObj(Db.Ts(DateTime.UtcNow))), ("@n", name)).ConfigureAwait(False)
            Return _box.Unprotect(row.Value)
        End Function

        ''' <summary>The host's own use (a mailbox password).</summary>
        Friend Async Function GetValueAsync(name As String) As Task(Of String)
            Dim boxed = Await _db.ScalarAsync("SELECT Value FROM AgentSecrets WHERE Name=@n", ("@n", CObj(name))).ConfigureAwait(False)
            If boxed Is Nothing OrElse TypeOf boxed Is DBNull Then Return Nothing
            Return _box.Unprotect(Convert.ToString(boxed))
        End Function
    End Class

End Namespace
