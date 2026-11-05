Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions

Namespace Http

    ''' <summary>
    ''' Builds the OpenAPI 3 document from the live route table, so every endpoint the dashboard uses is documented with the
    ''' permission it needs. Hand-written summaries and request bodies cover the important ones (the Audio, Images and Music
    ''' APIs fully: OpenApi.Media.vb); the rest are named from their handlers.
    ''' </summary>
    Partial Friend NotInheritable Class OpenApi

        Private Shared ReadOnly Summaries As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"POST /api/auth/login", "Sign in (sets the session cookie; returns the CSRF token)"},
            {"GET /api/auth/me", "The signed-in account, its role, permissions and CSRF token"},
            {"GET /api/auth/api-key", "Your API key's status"},
            {"POST /api/auth/api-key", "Create or replace your API key (shown once)"},
            {"DELETE /api/auth/api-key", "Revoke your API key"},
            {"POST /api/agents/{id}/start", "Start an agent (revives a dead agent that has money again)"},
            {"POST /api/agents/{id}/stop", "Stop an agent"}, {"POST /api/agents/{id}/pause", "Pause an agent"}, {"POST /api/agents/{id}/resume", "Resume an agent"},
            {"POST /api/agents/{id}/restart", "Restart an agent"}, {"POST /api/agents/{id}/run-now", "Run an agent's schedules now"},
            {"POST /api/agents/{id}/task", "Add a task to an agent (delivery, reviewer, expected outcome and attachments optional)"},
            {"POST /api/agents/{id}/tasks/{taskId}/hold", "Hold a queued task"}, {"POST /api/agents/{id}/tasks/{taskId}/release", "Release a held task"},
            {"POST /api/agents/{id}/tasks/{taskId}/clone", "Clone a task"}, {"POST /api/agents/{id}/tasks/{taskId}/cancel", "Cancel a task"},
            {"POST /api/operations/pause-all", "Pause every agent"}, {"POST /api/operations/resume-all", "Resume every agent"},
            {"POST /api/operations/emergency-stop", "Emergency stop: pause all agents and cancel running work"},
            {"POST /api/uploads", "Upload a file to attach to tasks (raw body, ?name=file.ext); returns its id and extracted text"},
            {"POST /api/global-tasks", "Create a global task the host plans and distributes"},
            {"POST /api/custom-models", "Create a custom model (admin)"}, {"PUT /api/custom-models/{id}", "Replace a custom model (admin)"},
            {"POST /api/custom-models/test", "Show which model a custom model picks for a prompt"},
            {"GET /api/chat/conversations", "Your chat conversations"}, {"POST /api/chat/conversations", "Start a chat conversation"},
            {"POST /api/chat/conversations/{id}/messages", "Send a chat message; the answer streams back as server-sent events"},
            {"POST /api/chat/run", "Run Python, Node or shell code in the code sandbox"},
            {"GET /v1/models", "OpenAI: list models (provider models and custom models)"}, {"GET /v1/models/{model}", "OpenAI: one model"},
            {"POST /v1/chat/completions", "OpenAI: chat completion (streaming with stream=true; custom models route automatically)"},
            {"POST /v1/completions", "OpenAI: legacy text completion"}, {"POST /v1/embeddings", "OpenAI: embeddings"},
            {"POST /v1/images/generations", "OpenAI: generate images"}}

        Private Shared Function Humanize(name As String) As String
            Dim s = Regex.Replace(name, "([a-z0-9])([A-Z])", "$1 $2")
            Return If(s.Length = 0, s, Char.ToUpperInvariant(s(0)) & s.Substring(1).ToLowerInvariant())
        End Function

        Private Shared Function TagFor(path As String) As String
            If path.StartsWith("/v1/") OrElse path.StartsWith("/api/v1/") Then Return "OpenAI-compatible API"
            Dim parts = path.Split("/"c, StringSplitOptions.RemoveEmptyEntries)
            If parts.Length < 2 Then Return "General"
            Select Case parts(1)
                Case "agents" : Return If(parts.Length > 3 AndAlso parts(3) = "tasks" OrElse parts.Length > 3 AndAlso parts(3) = "task", "Tasks", "Agents")
                Case "tasks", "global-tasks", "global-schedules" : Return If(parts(1) = "tasks", "Tasks", "Global tasks")
                Case "auth", "users" : Return "Accounts & API keys"
                Case "chat" : Return "Chat"
                Case "custom-models" : Return "Custom models"
                Case "results", "uploads" : Return "Results & files"
                Case "finance", "pricing" : Return "Finance"
                Case "models", "model-grants", "ai" : Return "Models"
                Case "audio" : Return AudioTag
                Case "images" : Return ImagesTag
                Case "music" : Return MusicTag
                Case "markets" : Return MarketsTag
                Case "news" : Return NewsTag
                Case "stocks" : Return StocksTag
                Case "polymarket" : Return PolymarketTag
                Case Else : Return Humanize(parts(1).Replace("-", " "))
            End Select
        End Function

        Private Shared Function ChatCompletionSchema() As JsonObject
            Dim msg As New JsonObject From {{"type", "object"}, {"properties", New JsonObject From {
                {"role", New JsonObject From {{"type", "string"}, {"enum", New JsonArray("system", "user", "assistant", "tool")}}},
                {"content", New JsonObject From {{"description", "Text, or an array of parts: {type:'text', text} and {type:'image_url', image_url:{url}}"}}},
                {"tool_calls", New JsonObject From {{"type", "array"}, {"items", New JsonObject From {{"type", "object"}}}}},
                {"tool_call_id", New JsonObject From {{"type", "string"}}}}}}
            Return New JsonObject From {{"type", "object"}, {"required", New JsonArray("model", "messages")}, {"properties", New JsonObject From {
                {"model", New JsonObject From {{"type", "string"}, {"description", "A provider model id (GET /v1/models) or a custom model id"}, {"example", "lmzh/gpt-4.1-mini"}}},
                {"messages", New JsonObject From {{"type", "array"}, {"items", msg}}},
                {"stream", New JsonObject From {{"type", "boolean"}, {"description", "Server-sent events, like OpenAI"}}},
                {"temperature", New JsonObject From {{"type", "number"}}}, {"top_p", New JsonObject From {{"type", "number"}}},
                {"max_tokens", New JsonObject From {{"type", "integer"}}}, {"stop", New JsonObject From {{"description", "string or array"}}},
                {"tools", New JsonObject From {{"type", "array"}, {"items", New JsonObject From {{"type", "object"}}}}},
                {"tool_choice", New JsonObject From {{"description", "auto | none | required | {type:'function', function:{name}}"}}},
                {"response_format", New JsonObject From {{"type", "object"}, {"description", "{type:'json_object'} or {type:'json_schema', json_schema:{…}}"}}},
                {"seed", New JsonObject From {{"type", "integer"}}}, {"n", New JsonObject From {{"type", "integer"}}},
                {"presence_penalty", New JsonObject From {{"type", "number"}}}, {"frequency_penalty", New JsonObject From {{"type", "number"}}},
                {"user", New JsonObject From {{"type", "string"}}}}},
                {"example", JsonNode.Parse("{""model"":""lmzh/gpt-4.1-mini"",""messages"":[{""role"":""user"",""content"":""Say hello""}],""stream"":false}")}}
        End Function

        Public Shared Function Build(router As Router, version As String) As JsonObject
            Dim paths As New JsonObject()
            Dim tagOrder As New List(Of String)
            For Each r In router.Routes
                Dim template = "/" & String.Join("/", r.Segments.Select(Function(s) If(s.StartsWith("{*"), "{" & s.Substring(2), s)))
                If template.StartsWith("/api/v1/") OrElse template = "/healthz" Then Continue For ' alias of /v1, and the health probe
                Dim key = r.Method & " " & template
                Dim summary As String = Nothing
                If Not Summaries.TryGetValue(key, summary) Then
                    Dim name = r.Handler.Method.Name
                    summary = If(name.Contains("Lambda") OrElse name.StartsWith("_"), Humanize(r.Method.ToLowerInvariant()) & " " & template, Humanize(name))
                End If
                Dim perm = If(r.Permission, "view")
                Dim permText = If(perm = Permissions.Self, "any signed-in account", If(perm = Permissions.AdminOnly, "administrators only", If(perm = "view", "viewing (any signed-in account, limited to its agents)",
                                  $"permission ""{perm}"" ({Permissions.Catalog.FirstOrDefault(Function(c) c.Key = perm)?.Label})")))
                Dim op As New JsonObject From {
                    {"tags", New JsonArray(TagFor(template))}, {"summary", summary},
                    {"description", $"Requires {permText}. API tokens need level {r.Level}."},
                    {"operationId", Regex.Replace(r.Method.ToLowerInvariant() & template, "[^A-Za-z0-9]+", "_").Trim("_"c)},
                    {"x-permission", perm},
                    {"responses", New JsonObject From {{"200", New JsonObject From {{"description", "OK"}}}, {"400", New JsonObject From {{"description", "Invalid request"}}},
                                                     {"401", New JsonObject From {{"description", "Not signed in / bad API key"}}}, {"403", New JsonObject From {{"description", "Missing permission"}}}}}}
                Dim prms As New JsonArray()
                For Each m As Match In Regex.Matches(template, "\{([^}]+)\}")
                    prms.Add(New JsonObject From {{"name", m.Groups(1).Value}, {"in", "path"}, {"required", True}, {"schema", New JsonObject From {{"type", "string"}}}})
                Next
                If template = "/api/uploads" Then prms.Add(New JsonObject From {{"name", "name"}, {"in", "query"}, {"required", True}, {"schema", New JsonObject From {{"type", "string"}}}})
                If r.Method = "GET" AndAlso Not template.Contains("{") Then
                    For Each q In {"limit", "status", "q"}
                        prms.Add(New JsonObject From {{"name", q}, {"in", "query"}, {"required", False}, {"schema", New JsonObject From {{"type", "string"}}}})
                    Next
                End If
                If prms.Count > 0 Then op("parameters") = prms
                If r.Method <> "GET" AndAlso r.Method <> "DELETE" Then
                    Dim schema As JsonNode = If(template.EndsWith("/chat/completions") OrElse template.EndsWith("/completions"), ChatCompletionSchema(), New JsonObject From {{"type", "object"}})
                    Dim media = If(template = "/api/uploads", "application/octet-stream", "application/json")
                    If media <> "application/json" Then schema = New JsonObject From {{"type", "string"}, {"format", "binary"}}
                    op("requestBody") = New JsonObject From {{"required", False}, {"content", New JsonObject From {{media, New JsonObject From {{"schema", schema}}}}}}
                End If
                If template.StartsWith("/v1/") AndAlso r.Method = "POST" Then
                    op("responses")("200") = New JsonObject From {{"description", "OpenAI-format response; text/event-stream when stream=true. Headers X-Agentic-Model and X-Agentic-Route tell which model answered."}}
                End If
                Dim doc As RouteDoc = Nothing
                If MediaDocs.TryGetValue(key, doc) Then
                    Dim pathParams As New JsonArray()
                    For Each m As Match In Regex.Matches(template, "\{([^}]+)\}")
                        pathParams.Add(New JsonObject From {{"name", m.Groups(1).Value}, {"in", "path"}, {"required", True}, {"schema", New JsonObject From {{"type", "string"}}}})
                    Next
                    ApplyDoc(op, doc, pathParams)
                End If
                If paths(template) Is Nothing Then paths(template) = New JsonObject()
                paths(template)(r.Method.ToLowerInvariant()) = op
                Dim tag = op("tags")(0).ToString()
                If Not tagOrder.Contains(tag) Then tagOrder.Add(tag)
            Next
            ' Tags in the order they first appear, with what the feature is and how it is used where there is more to say.
            Dim tags As New JsonArray()
            For Each t In tagOrder
                Dim tagDoc As New JsonObject From {{"name", t}}
                Dim text As String = Nothing
                If TagDescriptions.TryGetValue(t, text) Then tagDoc("description") = text
                tags.Add(tagDoc)
            Next
            Return New JsonObject From {
                {"openapi", "3.0.3"},
                {"info", New JsonObject From {{"title", "Agentic API"}, {"version", version},
                    {"description", "Everything the dashboard does, as an API. Authenticate with your personal API key (Accounts → API key) as " &
                                    "'Authorization: Bearer agk_…' or 'x-api-key: agk_…'. A key acts with its account's role and permissions; state-changing " &
                                    "calls with a key need no CSRF token. The OpenAI-compatible API is under /v1 (also /api/v1)."}}},
                {"servers", New JsonArray(New JsonObject From {{"url", "/"}})},
                {"components", New JsonObject From {{"securitySchemes", New JsonObject From {
                    {"apiKey", New JsonObject From {{"type", "http"}, {"scheme", "bearer"}, {"description", "Personal API key (agk_…)"}}},
                    {"apiKeyHeader", New JsonObject From {{"type", "apiKey"}, {"in", "header"}, {"name", "x-api-key"}}},
                    {"session", New JsonObject From {{"type", "apiKey"}, {"in", "cookie"}, {"name", "agentic_session"}}}}}}},
                {"security", New JsonArray(New JsonObject From {{"apiKey", New JsonArray()}}, New JsonObject From {{"apiKeyHeader", New JsonArray()}}, New JsonObject From {{"session", New JsonArray()}})},
                {"tags", tags},
                {"paths", paths}}
        End Function
    End Class

    Partial Friend NotInheritable Class AdminApi
        Private _router As Router

        Private Function OpenApiDocument(rc As RequestContext) As Task
            Return rc.OkAsync(OpenApi.Build(_router, _h.Version))
        End Function
    End Class

End Namespace
