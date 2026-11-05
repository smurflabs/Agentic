Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Immutable
Imports System.Threading
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities

Namespace AI

    Public Enum ModelAccessKind
        None
        ''' <summary>Allowed by the agent's profile (AllowedModels, subject to MaxTier).</summary>
        Profile
        ''' <summary>Explicitly granted by an administrator (with a stated purpose).</summary>
        Grant
    End Enum

    Public NotInheritable Class ModelGrant
        Public Property Id As Long
        Public Property AgentId As String
        Public Property ModelKey As String
        ''' <summary>What the agent should use this model for (shown to the agent).</summary>
        Public Property UseFor As String
        ''' <summary>The agent's subagents may also use this model (paid from the agent's wallet).</summary>
        Public Property AllowSubagents As Boolean
        Public Property GrantedBy As String
        Public Property GrantedUtc As DateTime
        Public Property RevokedBy As String
        Public Property RevokedUtc As DateTime?
    End Class

    Public Class ModelAccessException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ''' <summary>
    ''' Administrator-controlled model access beyond agent profiles:
    ''' - custom models: additional cloud models registered at runtime (stored in the database, priced, never on routing
    '''   ladders) that only explicitly granted agents can use;
    ''' - grants: per-agent access to an additional cloud model with a stated purpose, revocable at any time (takes effect
    '''   on the next AI call).
    ''' Every check is made by the host; agents cannot influence it.
    ''' </summary>
    Public NotInheritable Class ModelAccessService
        Private ReadOnly _db As Database
        Private ReadOnly _registry As ModelRegistry
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _configKeys As HashSet(Of String)
        Private ReadOnly _providers As HashSet(Of String)
        Private ReadOnly _clock As IClock
        Private _profiles As IProfileProvider
        Private ReadOnly _lock As New Object()
        ' agent id → (model key → active grant)
        Private _grants As ImmutableDictionary(Of String, ImmutableDictionary(Of String, ModelGrant)) =
            ImmutableDictionary.Create(Of String, ImmutableDictionary(Of String, ModelGrant))(StringComparer.OrdinalIgnoreCase)

        Public Sub New(db As Database, registry As ModelRegistry, pricing As PricingService, events As IAgentEventBus, configModels As IEnumerable(Of ModelOptions),
                       providerNames As IEnumerable(Of String), clock As IClock)
            _db = db
            _registry = registry
            _pricing = pricing
            _events = events
            _configKeys = New HashSet(Of String)(configModels.Select(Function(m) m.Key), StringComparer.OrdinalIgnoreCase)
            _providers = New HashSet(Of String)(providerNames, StringComparer.OrdinalIgnoreCase)
            _clock = clock
        End Sub

        Friend Sub AttachProfiles(profiles As IProfileProvider)
            _profiles = profiles
        End Sub

        Private _ownerOf As Func(Of String, String)

        ''' <summary>Who owns an agent (users' own models are usable only by agents their owner owns).</summary>
        Friend Sub AttachOwners(ownerOf As Func(Of String, String))
            _ownerOf = ownerOf
        End Sub

        ''' <summary>
        ''' A user's own model (My models): an agent may use it when the model is in its AllowedModels and the agent belongs
        ''' to the model's owner (a subagent: when its parent does). Never by grant, never for anyone else's agents.
        ''' </summary>
        Private Function ByoAccess(profile As EffectiveProfile, m As ModelRuntime) As ModelAccessKind
            If Not profile.AllowedModels.Contains(m.Key) OrElse _ownerOf Is Nothing Then Return ModelAccessKind.None
            Dim agentId = If(profile.IsSubagent, profile.OwnerAgentId, profile.Id)
            Dim owner = _ownerOf(agentId)
            Return If(owner IsNot Nothing AndAlso String.Equals(owner, m.Options.ByoOwner, StringComparison.OrdinalIgnoreCase), ModelAccessKind.Profile, ModelAccessKind.None)
        End Function

        ''' <summary>Startup: registers custom models (registry + pricing) and loads active grants.</summary>
        Public Async Function LoadAsync() As Task
            Dim defs = Await _db.QueryAsync("SELECT DefinitionJson, CreatedBy FROM CustomModels WHERE DeletedUtc IS NULL",
                                            Function(r) (Db.Str(r, "DefinitionJson"), Db.Str(r, "CreatedBy"))).ConfigureAwait(False)
            For Each d In defs
                Dim m = JsonUtil.FromJson(Of ModelOptions)(d.Item1)
                If m Is Nothing OrElse String.IsNullOrEmpty(m.Key) OrElse _configKeys.Contains(m.Key) Then Continue For
                m.IsCustom = True
                m.CreatedBy = d.Item2
                _registry.AddOrReplace(m)
                _pricing.RegisterModel(m.Key, False, m.Pricing?.InputPerMillion, m.Pricing?.OutputPerMillion, m.Pricing?.PerImage)
            Next
            Dim rows = Await _db.QueryAsync("SELECT * FROM AgentModelGrants WHERE RevokedUtc IS NULL", AddressOf MapGrant).ConfigureAwait(False)
            Dim builder = ImmutableDictionary.CreateBuilder(Of String, ImmutableDictionary(Of String, ModelGrant))(StringComparer.OrdinalIgnoreCase)
            For Each g In rows.GroupBy(Function(x) x.AgentId, StringComparer.OrdinalIgnoreCase)
                builder(g.Key) = g.ToImmutableDictionary(Function(x) x.ModelKey, Function(x) x, StringComparer.OrdinalIgnoreCase)
            Next
            SyncLock _lock
                _grants = builder.ToImmutable()
            End SyncLock
        End Function

        Private Shared Function MapGrant(r As Microsoft.Data.Sqlite.SqliteDataReader) As ModelGrant
            Return New ModelGrant With {
                .Id = Db.Lng(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .ModelKey = Db.Str(r, "ModelKey"), .UseFor = Db.Str(r, "UseFor"),
                .AllowSubagents = Db.Bool(r, "AllowSubagents"), .GrantedBy = Db.Str(r, "GrantedBy"), .GrantedUtc = Db.Dt(r, "GrantedUtc"),
                .RevokedBy = Db.Str(r, "RevokedBy"), .RevokedUtc = Db.DtN(r, "RevokedUtc")}
        End Function

        ' -----------------------------------------------------------------------------------------
        ' Access checks
        ' -----------------------------------------------------------------------------------------

        Public Function GetGrant(agentId As String, modelKey As String) As ModelGrant
            If agentId Is Nothing OrElse modelKey Is Nothing Then Return Nothing
            Dim byModel As ImmutableDictionary(Of String, ModelGrant) = Nothing
            Dim g As ModelGrant = Nothing
            Dim snapshot = Volatile.Read(_grants)
            If snapshot.TryGetValue(agentId, byModel) AndAlso byModel.TryGetValue(modelKey, g) Then Return g
            Return Nothing
        End Function

        Public Function GrantsFor(agentId As String) As IReadOnlyList(Of ModelGrant)
            Dim byModel As ImmutableDictionary(Of String, ModelGrant) = Nothing
            If agentId IsNot Nothing AndAlso Volatile.Read(_grants).TryGetValue(agentId, byModel) Then
                Return byModel.Values.OrderBy(Function(g) g.ModelKey, StringComparer.OrdinalIgnoreCase).ToList()
            End If
            Return Array.Empty(Of ModelGrant)()
        End Function

        Public Function AllGrants() As IReadOnlyList(Of ModelGrant)
            Return Volatile.Read(_grants).Values.SelectMany(Function(d) d.Values).OrderBy(Function(g) g.ModelKey).ThenBy(Function(g) g.AgentId).ToList()
        End Function

        ''' <summary>
        ''' How (if at all) a profile may use a model. Custom models are reachable only through grants. A subagent may use a
        ''' model only if it was given that model at spawn time AND its parent still may use it (grants must allow subagents),
        ''' so revoking the parent's access also cuts off its subagents immediately.
        ''' </summary>
        Public Function AccessFor(profile As EffectiveProfile, m As ModelRuntime) As ModelAccessKind
            If profile Is Nothing OrElse m Is Nothing Then Return ModelAccessKind.None
            If m.Options.ByoOwner IsNot Nothing Then Return ByoAccess(profile, m)
            If profile.IsSubagent Then
                If Not profile.AllowedModels.Contains(m.Key) Then Return ModelAccessKind.None
                Dim owner = _profiles?.TryGetProfile(profile.OwnerAgentId)
                If owner Is Nothing OrElse owner.IsSubagent Then Return ModelAccessKind.None
                Dim ownerKind = AccessFor(owner, m)
                If ownerKind = ModelAccessKind.Grant Then
                    Dim g = GetGrant(owner.Id, m.Key)
                    Return If(g IsNot Nothing AndAlso g.AllowSubagents, ModelAccessKind.Grant, ModelAccessKind.None)
                End If
                Return ownerKind
            End If
            If Not m.Options.IsCustom AndAlso profile.AllowedModels.Contains(m.Key) Then Return ModelAccessKind.Profile
            Return If(GetGrant(profile.Id, m.Key) IsNot Nothing, ModelAccessKind.Grant, ModelAccessKind.None)
        End Function

        ''' <summary>Models explicitly granted to this agent (for a subagent: its parent's grants it inherited).</summary>
        Public Function GrantedModelsFor(profile As EffectiveProfile) As List(Of (Model As ModelRuntime, Grant As ModelGrant))
            Dim result As New List(Of (ModelRuntime, ModelGrant))
            If profile Is Nothing Then Return result
            Dim ownerId = If(profile.IsSubagent, profile.OwnerAgentId, profile.Id)
            For Each g In GrantsFor(ownerId)
                Dim m = _registry.TryGet(g.ModelKey)
                If m Is Nothing OrElse Not m.Options.Enabled Then Continue For
                If profile.IsSubagent AndAlso (Not g.AllowSubagents OrElse Not profile.AllowedModels.Contains(g.ModelKey)) Then Continue For
                result.Add((m, g))
            Next
            Return result
        End Function

        ''' <summary>Models a parent may hand to a subagent: its profile models plus grants that allow subagents.</summary>
        Public Function SpawnableModels(parent As EffectiveProfile) As List(Of String)
            Dim result As New List(Of String)
            If parent Is Nothing OrElse parent.IsSubagent Then Return result
            For Each m In _registry.All()
                If Not m.Options.Enabled Then Continue For
                Select Case AccessFor(parent, m)
                    Case ModelAccessKind.Profile
                        If m.Options.Tier <= parent.MaxTier Then result.Add(m.Key)
                    Case ModelAccessKind.Grant
                        Dim g = GetGrant(parent.Id, m.Key)
                        If g IsNot Nothing AndAlso g.AllowSubagents Then result.Add(m.Key)
                End Select
            Next
            Return result
        End Function

        ' -----------------------------------------------------------------------------------------
        ' Grants
        ' -----------------------------------------------------------------------------------------

        Private Sub Publish(agentId As String, mutate As Func(Of ImmutableDictionary(Of String, ModelGrant), ImmutableDictionary(Of String, ModelGrant)))
            SyncLock _lock
                Dim current As ImmutableDictionary(Of String, ModelGrant) = Nothing
                If Not _grants.TryGetValue(agentId, current) Then current = ImmutableDictionary.Create(Of String, ModelGrant)(StringComparer.OrdinalIgnoreCase)
                Dim updated = mutate(current)
                _grants = If(updated.Count = 0, _grants.Remove(agentId), _grants.SetItem(agentId, updated))
            End SyncLock
        End Sub

        ''' <summary>Grants (or updates) access to an additional cloud model. UseFor tells the agent what the model is for.</summary>
        Public Async Function GrantAsync(agentId As String, modelKey As String, useFor As String, allowSubagents As Boolean, actor As String) As Task(Of ModelGrant)
            Dim profile = _profiles?.TryGetProfile(agentId)
            If profile Is Nothing OrElse profile.IsSubagent Then Throw New ModelAccessException($"Agent '{agentId}' does not exist.")
            Dim m = _registry.TryGet(modelKey)
            If m Is Nothing Then Throw New ModelAccessException($"Model '{modelKey}' does not exist.")
            If m.Options.IsLocal Then Throw New ModelAccessException($"'{modelKey}' is a local model; grants are for additional cloud models.")
            If m.Options.ByoOwner IsNot Nothing Then Throw New ModelAccessException($"'{modelKey}' is {m.Options.ByoOwner}'s own model: only their agents can use it (through the agent's allowed models).")
            If Not m.Options.IsCustom AndAlso profile.AllowedModels.Contains(m.Key) Then
                Throw New ModelAccessException($"'{modelKey}' is already allowed by {agentId}'s profile.")
            End If
            useFor = If(useFor, "").Trim()
            If useFor.Length < 3 Then Throw New ModelAccessException("Describe what the agent should use this model for.")
            useFor = TextUtil.Truncate(useFor, 1000)
            Dim now = _clock.UtcNow
            Dim existing = GetGrant(agentId, m.Key)
            Dim grant As ModelGrant
            If existing IsNot Nothing Then
                Await _db.ExecuteAsync("UPDATE AgentModelGrants SET UseFor=@u, AllowSubagents=@s WHERE Id=@id",
                                       ("@u", CObj(useFor)), ("@s", allowSubagents), ("@id", existing.Id)).ConfigureAwait(False)
                grant = New ModelGrant With {.Id = existing.Id, .AgentId = existing.AgentId, .ModelKey = existing.ModelKey, .UseFor = useFor,
                                             .AllowSubagents = allowSubagents, .GrantedBy = existing.GrantedBy, .GrantedUtc = existing.GrantedUtc}
            Else
                Dim id = Await _db.InTransactionAsync(Of Long)(
                    Async Function(conn, tx)
                        Await Database.TxExecAsync(conn, tx,
                            "INSERT INTO AgentModelGrants(AgentId, ModelKey, UseFor, AllowSubagents, GrantedBy, GrantedUtc) VALUES (@a, @m, @u, @s, @by, @t)",
                            ("@a", CObj(profile.Id)), ("@m", m.Key), ("@u", useFor), ("@s", allowSubagents), ("@by", actor), ("@t", Db.Ts(now))).ConfigureAwait(False)
                        Return Convert.ToInt64(Await Database.TxScalarAsync(conn, tx, "SELECT last_insert_rowid()").ConfigureAwait(False))
                    End Function).ConfigureAwait(False)
                grant = New ModelGrant With {.Id = id, .AgentId = profile.Id, .ModelKey = m.Key, .UseFor = useFor, .AllowSubagents = allowSubagents,
                                             .GrantedBy = actor, .GrantedUtc = now}
            End If
            Publish(profile.Id, Function(d) d.SetItem(m.Key, grant))
            _events.Emit(AgentEventType.ModelAccessChanged, $"{actor} {If(existing Is Nothing, "granted", "updated")} {m.Key} for {profile.Id}: {TextUtil.Truncate(useFor, 120)}",
                         profile.Id, data:=New Dictionary(Of String, Object) From {{"model", m.Key}, {"action", If(existing Is Nothing, "grant", "update")}, {"allowSubagents", allowSubagents}})
            Return grant
        End Function

        Public Async Function RevokeAsync(agentId As String, modelKey As String, actor As String) As Task(Of Boolean)
            Dim g = GetGrant(agentId, modelKey)
            If g Is Nothing Then Return False
            Await _db.ExecuteAsync("UPDATE AgentModelGrants SET RevokedBy=@by, RevokedUtc=@t WHERE Id=@id AND RevokedUtc IS NULL",
                                   ("@by", CObj(actor)), ("@t", Db.Ts(_clock.UtcNow)), ("@id", g.Id)).ConfigureAwait(False)
            Publish(g.AgentId, Function(d) d.Remove(g.ModelKey))
            _events.Emit(AgentEventType.ModelAccessChanged, $"{actor} revoked {g.ModelKey} from {g.AgentId}", g.AgentId,
                         data:=New Dictionary(Of String, Object) From {{"model", g.ModelKey}, {"action", "revoke"}})
            Return True
        End Function

        ''' <summary>Makes the agent's grants exactly match the given list (agent editor). Returns validation messages.</summary>
        Public Async Function SyncGrantsAsync(agentId As String, desired As IEnumerable(Of ModelGrant), actor As String) As Task(Of List(Of String))
            Dim errors As New List(Of String)
            Dim wanted = desired.Where(Function(d) Not String.IsNullOrWhiteSpace(d?.ModelKey)).GroupBy(Function(d) d.ModelKey, StringComparer.OrdinalIgnoreCase).Select(Function(g) g.Last()).ToList()
            For Each g In GrantsFor(agentId)
                If Not wanted.Any(Function(w) String.Equals(w.ModelKey, g.ModelKey, StringComparison.OrdinalIgnoreCase)) Then Await RevokeAsync(agentId, g.ModelKey, actor).ConfigureAwait(False)
            Next
            For Each w In wanted
                Dim current = GetGrant(agentId, w.ModelKey)
                If current IsNot Nothing AndAlso current.UseFor = If(w.UseFor, "").Trim() AndAlso current.AllowSubagents = w.AllowSubagents Then Continue For
                Try
                    Await GrantAsync(agentId, w.ModelKey, w.UseFor, w.AllowSubagents, actor).ConfigureAwait(False)
                Catch ex As ModelAccessException
                    errors.Add($"{w.ModelKey}: {ex.Message}")
                End Try
            Next
            Return errors
        End Function

        Public Async Function RevokeAllForAgentAsync(agentId As String, actor As String) As Task
            For Each g In GrantsFor(agentId)
                Await RevokeAsync(agentId, g.ModelKey, actor).ConfigureAwait(False)
            Next
        End Function

        Public Function ListHistoryAsync(Optional agentId As String = Nothing, Optional modelKey As String = Nothing, Optional limit As Integer = 200) As Task(Of List(Of ModelGrant))
            Dim where As New List(Of String)
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a")
            If Not String.IsNullOrEmpty(modelKey) Then where.Add("ModelKey=@m")
            Return _db.QueryAsync("SELECT * FROM AgentModelGrants" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY Id DESC LIMIT @l",
                                  AddressOf MapGrant, ("@a", CObj(agentId)), ("@m", modelKey), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        ' -----------------------------------------------------------------------------------------
        ' Custom models
        ' -----------------------------------------------------------------------------------------

        Public Function CustomModels() As List(Of ModelOptions)
            Return _registry.All().Where(Function(m) m.Options.IsCustom).Select(Function(m) m.Options).ToList()
        End Function

        Public Function IsCustom(key As String) As Boolean
            Dim m = _registry.TryGet(key)
            Return m IsNot Nothing AndAlso m.Options.IsCustom
        End Function

        ''' <summary>Validates and stores a custom model; it becomes usable immediately by agents that are granted access.</summary>
        Public Async Function SaveCustomModelAsync(def As ModelOptions, actor As String, isNew As Boolean) As Task(Of ModelOptions)
            If def Is Nothing Then Throw New ModelAccessException("Model definition is required.")
            If Not InputValidation.IsValidId(def.Key) Then Throw New ModelAccessException("Key must be 1-64 characters: letters, digits, _ - .")
            If _configKeys.Contains(def.Key) Then Throw New ModelAccessException($"'{def.Key}' is defined in appsettings.json; choose another key.")
            Dim existing = _registry.TryGet(def.Key)
            If isNew AndAlso existing IsNot Nothing Then Throw New ModelAccessException($"A model with key '{def.Key}' already exists.")
            If Not isNew AndAlso (existing Is Nothing OrElse Not existing.Options.IsCustom) Then Throw New ModelAccessException($"Custom model '{def.Key}' does not exist.")
            If String.IsNullOrWhiteSpace(def.ModelId) Then Throw New ModelAccessException("Provider model id is required.")
            If String.IsNullOrWhiteSpace(def.Provider) Then def.Provider = _providers.FirstOrDefault()
            If Not _providers.Contains(def.Provider) Then Throw New ModelAccessException($"Unknown provider '{def.Provider}'.")
            Dim pr = If(def.Pricing, New PricingOptions())
            If Not pr.InputPerMillion.HasValue OrElse Not pr.OutputPerMillion.HasValue Then Throw New ModelAccessException("Input and output prices (USD per 1M tokens) are required for cloud models.")
            If pr.InputPerMillion.Value < 0 OrElse pr.OutputPerMillion.Value < 0 OrElse (pr.PerImage.HasValue AndAlso pr.PerImage.Value < 0) Then Throw New ModelAccessException("Prices must be >= 0.")
            If def.MaxTokens < 16 OrElse def.MaxTokens > 1000000 Then Throw New ModelAccessException("Max output tokens must be 16 - 1,000,000.")
            If def.ContextWindowTokens < 1024 Then Throw New ModelAccessException("Context window must be >= 1024 tokens.")
            If def.MaxConcurrency < 1 OrElse def.MaxConcurrency > 256 Then Throw New ModelAccessException("Max concurrency must be 1 - 256.")
            If def.Tier < 1 OrElse def.Tier > 9 Then Throw New ModelAccessException("Tier must be 1 - 9.")
            If def.Temperature.HasValue AndAlso (def.Temperature.Value < 0 OrElse def.Temperature.Value > 2) Then Throw New ModelAccessException("Temperature must be 0 - 2.")
            If def.TimeoutSeconds < 10 Then def.TimeoutSeconds = 300
            def.Key = def.Key.Trim()
            def.ModelId = def.ModelId.Trim()
            def.IsLocal = False
            def.IsCustom = True
            def.Description = TextUtil.Truncate(def.Description, 500)
            def.UseFor = TextUtil.Truncate(def.UseFor, 1000)
            def.AllowedPurposes = New List(Of AiPurpose)
            def.Fallbacks = New List(Of String)
            def.Capabilities = If(def.Capabilities, New List(Of ModelCapability)).Distinct().ToList()
            def.CreatedBy = If(existing?.Options.CreatedBy, actor)
            def.Pricing = pr
            Dim now = Db.Ts(_clock.UtcNow)
            Await _db.ExecuteAsync(
                "INSERT INTO CustomModels(Key, DefinitionJson, CreatedBy, CreatedUtc, ModifiedUtc) VALUES (@k, @d, @by, @t, @t) " &
                "ON CONFLICT(Key) DO UPDATE SET DefinitionJson=@d, ModifiedUtc=@t, DeletedUtc=NULL",
                ("@k", CObj(def.Key)), ("@d", JsonUtil.ToJson(def)), ("@by", actor), ("@t", now)).ConfigureAwait(False)
            _registry.AddOrReplace(def)
            _pricing.RegisterModel(def.Key, False, pr.InputPerMillion, pr.OutputPerMillion, pr.PerImage)
            Await _pricing.SetPriceAsync(def.Key, pr.InputPerMillion, pr.OutputPerMillion, pr.PerImage, actor).ConfigureAwait(False)
            _events.Emit(AgentEventType.ConfigChanged, $"Custom model {def.Key} ({def.ModelId}) {If(isNew, "registered", "updated")} by {actor}",
                         data:=New Dictionary(Of String, Object) From {{"model", def.Key}})
            Return def
        End Function

        ''' <summary>Removes a custom model and revokes every grant for it.</summary>
        Public Async Function DeleteCustomModelAsync(key As String, actor As String) As Task(Of Boolean)
            If Not IsCustom(key) Then Return False
            Await _db.ExecuteAsync("UPDATE CustomModels SET DeletedUtc=@t WHERE Key=@k", ("@t", CObj(Db.Ts(_clock.UtcNow))), ("@k", key)).ConfigureAwait(False)
            For Each g In AllGrants().Where(Function(x) String.Equals(x.ModelKey, key, StringComparison.OrdinalIgnoreCase)).ToList()
                Await RevokeAsync(g.AgentId, g.ModelKey, actor).ConfigureAwait(False)
            Next
            _registry.Remove(key)
            _pricing.Unregister(key)
            _events.Emit(AgentEventType.ConfigChanged, $"Custom model {key} removed by {actor}", data:=New Dictionary(Of String, Object) From {{"model", key}})
            Return True
        End Function
    End Class

End Namespace
