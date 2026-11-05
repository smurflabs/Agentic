Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.AI

Namespace Http

    ' My models (bring your own API key): each account adds OpenAI (or OpenAI-compatible), Anthropic and Amazon Bedrock
    ' models with its own keys. Under /api/chat/, so the "Chat & model API" permission (models.use) covers them. Every call
    ' only sees and changes the caller's own models; keys are write-only (only their last characters come back).
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterMyModels(r As Router)
            r.Map("GET", "/api/chat/my-models", AuthLevel.Read, AddressOf MyModelsList)
            r.Map("POST", "/api/chat/my-models", AuthLevel.SubmitTask, AddressOf MyModelsCreate)
            r.Map("PUT", "/api/chat/my-models/{id}", AuthLevel.SubmitTask, AddressOf MyModelsUpdate)
            r.Map("DELETE", "/api/chat/my-models/{id}", AuthLevel.SubmitTask, AddressOf MyModelsDelete)
            r.Map("POST", "/api/chat/my-models/{id}/test", AuthLevel.SubmitTask, AddressOf MyModelsTest)
        End Sub

        ''' <summary>Registry models this account may see: everything but other accounts' own models (My models are private,
        ''' administrators included).</summary>
        Private Function VisibleModels(rc As RequestContext) As IEnumerable(Of ModelRuntime)
            Return _h.ModelRegistry.All().Where(Function(m) m.Options.ByoOwner Is Nothing OrElse String.Equals(m.Options.ByoOwner, rc.User.Name, StringComparison.OrdinalIgnoreCase))
        End Function

        Private Function RequireByo() As ByoService
            If _h.Byo Is Nothing Then Throw New ApiException(409, "BYO_UNAVAILABLE", "My models are not available on this server.")
            Return _h.Byo
        End Function

        Private Shared Function ByoJson(m As ByoModel) As Object
            Return New With {.id = m.Id, .name = m.Name, .provider = m.Provider, .providerLabel = ByoService.ProviderLabel(m.Provider),
                             .modelName = m.ModelName, .baseUrl = m.BaseUrl, .region = m.Region, .keyHint = m.KeyHint,
                             .auth = If(Not String.IsNullOrEmpty(m.Secret?.AccessKeyId), "iam", If(Not String.IsNullOrEmpty(m.Secret?.ApiKey), "key", "none")),
                             .contextWindow = m.ContextWindow, .maxTokens = m.MaxTokens, .vision = m.Vision, .enabled = m.Enabled,
                             .createdUtc = m.CreatedUtc, .updatedUtc = m.UpdatedUtc}
        End Function

        Private Shared Function ByoInput(b As JsonElement) As ByoModelInput
            Dim str = Function(name As String) As String
                          Dim v As JsonElement
                          Return If(b.ValueKind = JsonValueKind.Object AndAlso b.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.String, v.GetString(), Nothing)
                      End Function
            Dim int = Function(name As String) As Integer?
                          Dim v As JsonElement
                          Dim n As Integer
                          If b.ValueKind = JsonValueKind.Object AndAlso b.TryGetProperty(name, v) AndAlso v.ValueKind = JsonValueKind.Number AndAlso v.TryGetInt32(n) Then Return n
                          Return Nothing
                      End Function
            Dim bool = Function(name As String) As Boolean?
                           Dim v As JsonElement
                           If b.ValueKind = JsonValueKind.Object AndAlso b.TryGetProperty(name, v) AndAlso (v.ValueKind = JsonValueKind.True OrElse v.ValueKind = JsonValueKind.False) Then Return v.GetBoolean()
                           Return Nothing
                       End Function
            Return New ByoModelInput With {
                .Name = str("name"), .Provider = str("provider"), .ModelName = str("modelName"), .BaseUrl = str("baseUrl"), .Region = str("region"),
                .ApiKey = str("apiKey"), .AccessKeyId = str("accessKeyId"), .SecretAccessKey = str("secretAccessKey"), .SessionToken = str("sessionToken"),
                .ContextWindow = int("contextWindow"), .MaxTokens = int("maxTokens"), .Vision = bool("vision"), .Enabled = bool("enabled")}
        End Function

        Private Async Function MyModelsList(rc As RequestContext) As Task
            Dim byo = RequireByo()
            Await rc.OkAsync(New With {
                .models = byo.ListFor(rc.User.Name).Select(Function(m) ByoJson(m)).ToList(),
                .providers = ByoService.Providers.Select(Function(p) New With {.id = p, .label = ByoService.ProviderLabel(p)}).ToList(),
                .maxModels = ByoService.MaxPerUser}).ConfigureAwait(False)
        End Function

        Private Async Function MyModelsCreate(rc As RequestContext) As Task
            Dim byo = RequireByo()
            Dim input = ByoInput(Await rc.ReadJsonAsync().ConfigureAwait(False))
            Dim m As ByoModel
            Try
                m = Await byo.SaveAsync(rc.User.Name, Nothing, input).ConfigureAwait(False)
            Catch ex As ByoException
                Throw New ApiException(400, "INVALID_MODEL", ex.Message)
            End Try
            Await AuditAsync(rc, "mymodels.create", "user-model", m.Id, New With {m.Name, m.Provider, m.ModelName}).ConfigureAwait(False)
            Await rc.OkAsync(ByoJson(m)).ConfigureAwait(False)
        End Function

        Private Async Function MyModelsUpdate(rc As RequestContext) As Task
            Dim byo = RequireByo()
            Dim id = rc.Route("id")
            Dim input = ByoInput(Await rc.ReadJsonAsync().ConfigureAwait(False))
            Dim m As ByoModel
            Try
                m = Await byo.SaveAsync(rc.User.Name, id, input).ConfigureAwait(False)
            Catch ex As ByoException When ex.Message = "No such model."
                Throw ApiException.NotFound("MODEL_NOT_FOUND", "No such model in My models.")
            Catch ex As ByoException
                Throw New ApiException(400, "INVALID_MODEL", ex.Message)
            End Try
            Await AuditAsync(rc, "mymodels.update", "user-model", m.Id, New With {m.Name, m.Provider, m.ModelName}).ConfigureAwait(False)
            Await rc.OkAsync(ByoJson(m)).ConfigureAwait(False)
        End Function

        Private Async Function MyModelsDelete(rc As RequestContext) As Task
            Dim byo = RequireByo()
            Dim id = rc.Route("id")
            If Not Await byo.DeleteAsync(rc.User.Name, id).ConfigureAwait(False) Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "No such model in My models.")
            Await AuditAsync(rc, "mymodels.delete", "user-model", id, Nothing).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function MyModelsTest(rc As RequestContext) As Task
            Dim byo = RequireByo()
            Dim id = rc.Route("id")
            Dim m = byo.Get(id)
            If m Is Nothing OrElse Not String.Equals(m.Owner, rc.User.Name, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.NotFound("MODEL_NOT_FOUND", "No such model in My models.")
            Dim r = Await byo.TestAsync(id, rc.CancellationToken).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = r.Ok, .message = r.Message}).ConfigureAwait(False)
        End Function

    End Class

End Namespace
