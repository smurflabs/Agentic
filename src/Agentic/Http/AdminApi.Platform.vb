Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.AI
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Http

    ' Admin API: personal API keys, custom (routing) models and model usage.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterPlatform(r As Router)
            r.Map("GET", "/api/auth/api-key", AuthLevel.Read, AddressOf GetOwnApiKey)
            r.Map("POST", "/api/auth/api-key", AuthLevel.Read, AddressOf CreateOwnApiKey)
            r.Map("DELETE", "/api/auth/api-key", AuthLevel.Read, AddressOf DeleteOwnApiKey)
            r.Map("DELETE", "/api/users/{username}/api-key", AuthLevel.Admin, AddressOf RevokeUserApiKey)
            ' Fixed paths first: the catch-all {*id} below would otherwise match them.
            r.Map("GET", "/api/custom-models", AuthLevel.Read, AddressOf ListRouterModels)
            r.Map("POST", "/api/custom-models", AuthLevel.Admin, Function(rc) SaveRouterModel(rc, True))
            r.Map("POST", "/api/custom-models/test", AuthLevel.Read, AddressOf TestRouterModel)
            r.Map("GET", "/api/custom-models/usage", AuthLevel.Read, AddressOf ModelUsage)
            r.Map("GET", "/api/custom-models/{*id}", AuthLevel.Read, AddressOf GetRouterModel)
            r.Map("PUT", "/api/custom-models/{*id}", AuthLevel.Admin, Function(rc) SaveRouterModel(rc, False))
            r.Map("DELETE", "/api/custom-models/{*id}", AuthLevel.Admin, AddressOf DeleteRouterModel)
            r.Map("GET", "/api/openapi.json", AuthLevel.Read, AddressOf OpenApiDocument)
        End Sub

        ' ------------------------------------------------------------------ personal API keys

        Private Sub RequireAccount(rc As RequestContext)
            If Not rc.User.ViaSession AndAlso Not rc.User.ViaApiKey Then Throw ApiException.BadRequest("API keys belong to dashboard accounts; sign in to the dashboard to manage yours.")
        End Sub

        Private Shared Function KeyDto(u As DashboardUser) As Object
            Return New With {.hasKey = u?.ApiKeyPrefix IsNot Nothing, .prefix = u?.ApiKeyPrefix, .createdUtc = u?.ApiKeyCreatedUtc, .lastUsedUtc = u?.ApiKeyLastUsedUtc}
        End Function

        Private Async Function GetOwnApiKey(rc As RequestContext) As Task
            RequireAccount(rc)
            Await rc.OkAsync(KeyDto(Await _h.AgentsRepo.GetUserAsync(rc.User.Name).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        ''' <summary>Creates (or replaces) the caller's API key. The key is returned once and only its hash is stored.</summary>
        Private Async Function CreateOwnApiKey(rc As RequestContext) As Task
            RequireAccount(rc)
            Dim key = AuthService.ApiKeyPrefix & Ids.NewToken(36)
            Dim prefix = key.Substring(0, 12)
            Await _h.AgentsRepo.SetApiKeyAsync(rc.User.Name, TextUtil.Sha256Hex(key), prefix).ConfigureAwait(False)
            Await AuditAsync(rc, "api-key-created", "user", rc.User.Name, New With {prefix}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, New With {.key = key, .prefix = prefix, .createdUtc = DateTime.UtcNow,
                                                   .message = "Copy the key now; it is not shown again. It acts with your account's role and permissions."}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteOwnApiKey(rc As RequestContext) As Task
            RequireAccount(rc)
            Await _h.AgentsRepo.ClearApiKeyAsync(rc.User.Name).ConfigureAwait(False)
            Await AuditAsync(rc, "api-key-revoked", "user", rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.revoked = True}).ConfigureAwait(False)
        End Function

        Private Async Function RevokeUserApiKey(rc As RequestContext) As Task
            Dim name = rc.Route("username")
            If Await _h.AgentsRepo.GetUserAsync(name).ConfigureAwait(False) Is Nothing Then Throw ApiException.NotFound("USER_NOT_FOUND", "User does not exist.")
            Await _h.AgentsRepo.ClearApiKeyAsync(name).ConfigureAwait(False)
            Await AuditAsync(rc, "api-key-revoked", "user", name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.revoked = True}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ custom models

        Private Async Function ListRouterModels(rc As RequestContext) As Task
            Dim models = Await _h.ModelHub.ListCustomAsync().ConfigureAwait(False)
            Dim upstream As List(Of UpstreamModel) = Nothing
            Dim upstreamError As String = Nothing
            Try
                upstream = Await _h.ModelHub.ListUpstreamAsync(rc.CancellationToken, rc.QueryString("refresh") = "1").ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is OperationCanceledException
                upstreamError = ex.Message
            End Try
            Await rc.OkAsync(New With {
                .models = models, .canEdit = rc.User.IsAdmin,
                .categories = ModelHub.Catalog.Select(Function(c) New With {c.Key, c.Label, c.Group, c.Description, c.IsSignal, c.Keywords}).ToList(),
                .providerModels = If(upstream, New List(Of UpstreamModel)), .providerError = upstreamError}).ConfigureAwait(False)
        End Function

        Private Async Function GetRouterModel(rc As RequestContext) As Task
            Dim m = Await _h.ModelHub.GetCustomAsync(rc.Route("id")).ConfigureAwait(False)
            If m Is Nothing Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "Custom model does not exist.")
            Await rc.OkAsync(m).ConfigureAwait(False)
        End Function

        Private Async Function SaveRouterModel(rc As RequestContext, isNew As Boolean) As Task
            Dim m = Await rc.ReadJsonAsync(Of RouterModel)().ConfigureAwait(False)
            If m Is Nothing Then Throw ApiException.BadRequest("Send the custom model as JSON.")
            If Not isNew Then
                Dim id = rc.Route("id")
                If Await _h.ModelHub.GetCustomAsync(id).ConfigureAwait(False) Is Nothing Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "Custom model does not exist.")
                m.Id = id
            End If
            Dim problems = Await _h.ModelHub.SaveCustomAsync(m, rc.User.Name, isNew).ConfigureAwait(False)
            If problems.Count > 0 Then
                Await rc.WriteJsonAsync(400, New With {.error = New With {.code = "INVALID_MODEL", .message = problems(0)}, .errors = problems}).ConfigureAwait(False)
                Return
            End If
            Await AuditAsync(rc, If(isNew, "create-custom-model", "update-custom-model"), "custom-model", m.Id, New With {m.Name, .routes = m.Definition.Routes.Count}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(If(isNew, 201, 200), Await _h.ModelHub.GetCustomAsync(m.Id).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function DeleteRouterModel(rc As RequestContext) As Task
            Dim id = rc.Route("id")
            If Not Await _h.ModelHub.DeleteCustomAsync(id).ConfigureAwait(False) Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "Custom model does not exist.")
            Await AuditAsync(rc, "delete-custom-model", "custom-model", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Which model a custom model would pick for a prompt: {model: id | definition: {...}, prompt, images, tools, json}.</summary>
        Private Async Function TestRouterModel(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim m As RouterModel = Nothing
            Dim id = JsonUtil.GetString(b, "model")
            If Not String.IsNullOrEmpty(id) Then m = Await _h.ModelHub.GetCustomAsync(id).ConfigureAwait(False)
            Dim defEl As JsonElement
            If m Is Nothing AndAlso b.TryGetProperty("definition", defEl) Then m = New RouterModel With {.Id = "draft", .Definition = JsonUtil.FromJson(Of RouterDefinition)(defEl.GetRawText())}
            If m Is Nothing OrElse m.Definition Is Nothing Then Throw ApiException.BadRequest("Give a custom model id or a definition.")
            Dim content As JsonNode = JsonUtil.GetString(b, "prompt", "")
            Dim imgEl As JsonElement
            If b.TryGetProperty("images", imgEl) AndAlso imgEl.ValueKind = JsonValueKind.True Then
                content = New JsonArray(New JsonObject From {{"type", "text"}, {"text", JsonUtil.GetString(b, "prompt", "")}},
                                        New JsonObject From {{"type", "image_url"}, {"image_url", New JsonObject From {{"url", "data:image/png;base64,AA=="}}}})
            End If
            Dim req As New JsonObject From {{"model", m.Id}, {"messages", New JsonArray(New JsonObject From {{"role", "user"}, {"content", content}})}}
            Dim toolsEl As JsonElement
            If b.TryGetProperty("tools", toolsEl) AndAlso toolsEl.ValueKind = JsonValueKind.True Then req("tools") = New JsonArray(New JsonObject From {{"type", "function"}, {"function", New JsonObject From {{"name", "noop"}}}})
            Dim jsonEl As JsonElement
            If b.TryGetProperty("json", jsonEl) AndAlso jsonEl.ValueKind = JsonValueKind.True Then req("response_format") = New JsonObject From {{"type", "json_object"}}
            Dim d = Await _h.ModelHub.DecideAsync(m, req, rc.CancellationToken).ConfigureAwait(False)
            Dim scores = ModelHub.ScoreCategories(JsonUtil.GetString(b, "prompt", ""), If(m.Definition.Routes, New List(Of RouterRoute)))
            Await rc.OkAsync(New With {.model = d.Model, .category = d.Category, .label = ModelHub.Label(d.Category), .reason = d.Reason, .fallbacks = d.Fallbacks,
                                       .scores = scores.Select(Function(s) New With {.category = s.Category, .label = ModelHub.Label(s.Category), .score = s.Score}).ToList()}).ConfigureAwait(False)
        End Function

        ''' <summary>Calls through the OpenAI API and Chat, per model and per user (users see their own).</summary>
        Private Async Function ModelUsage(rc As RequestContext) As Task
            Dim hours = rc.QueryInt("hours", 24 * 7, 1, 24 * 90)
            Dim since = Db.Ts(DateTime.UtcNow.AddHours(-hours))
            Dim mine = Not rc.User.IsAdmin
            Dim where = "WHERE CreatedUtc>=@s" & If(mine, " AND Username=@u", "")
            Dim rows = Await _h.Database.QueryAsync($"SELECT Username, Source, Model, RoutedModel, Category, COUNT(*) AS N, SUM(CASE WHEN Status>=400 THEN 1 ELSE 0 END) AS F, " &
                                                     $"SUM(PromptTokens) AS P, SUM(CompletionTokens) AS C, AVG(DurationMs) AS D FROM ModelApiUsage {where} GROUP BY Username, Source, Model, RoutedModel, Category ORDER BY N DESC LIMIT 500",
                Function(r) New With {.username = Db.Str(r, "Username"), .source = Db.Str(r, "Source"), .model = Db.Str(r, "Model"), .routedModel = Db.Str(r, "RoutedModel"),
                                      .category = Db.Str(r, "Category"), .calls = Db.Lng(r, "N"), .failures = Db.Lng(r, "F"), .promptTokens = Db.Lng(r, "P"),
                                      .completionTokens = Db.Lng(r, "C"), .avgMs = CLng(If(r.IsDBNull(r.GetOrdinal("D")), 0, r.GetDouble(r.GetOrdinal("D"))))},
                ("@s", CObj(since)), ("@u", rc.User.Name)).ConfigureAwait(False)
            Await rc.OkAsync(New With {.hours = hours, .rows = rows}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
