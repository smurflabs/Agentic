Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace AI

    Public NotInheritable Class UpstreamModel
        Public Property Id As String
        Public Property Name As String
        Public Property OwnedBy As String
        Public Property Created As Long
    End Class

    ''' <summary>A kind of request a custom model can route: detected from the request's shape (signals) or its words.</summary>
    Public NotInheritable Class RouteCategory
        Public Property Key As String
        Public Property Label As String
        Public Property Group As String
        Public Property Description As String
        ''' <summary>Detected from the request itself (images, tools, JSON output, length), not from its words.</summary>
        Public Property IsSignal As Boolean
        Public Property Keywords As String()
    End Class

    Public NotInheritable Class RouterRoute
        Public Property Category As String
        ''' <summary>Model id at the provider (OpenWebUI), e.g. "dddai2/deepseek-v4-pro".</summary>
        Public Property Model As String
        ''' <summary>Tried in order when the model is unavailable.</summary>
        Public Property Fallbacks As New List(Of String)
        ''' <summary>Extra words that select this route (added to the category's built-in keywords).</summary>
        Public Property Keywords As New List(Of String)
        ''' <summary>Added to the system prompt when this route is used.</summary>
        Public Property SystemPrompt As String
        ''' <summary>Used when the request does not set temperature.</summary>
        Public Property Temperature As Double?
        ''' <summary>Used when the request does not set max_tokens.</summary>
        Public Property MaxTokens As Integer?
        Public Property Notes As String
    End Class

    Public NotInheritable Class RouterDefinition
        ''' <summary>Answers anything no route claims.</summary>
        Public Property DefaultModel As String
        Public Property DefaultFallbacks As New List(Of String)
        Public Property Routes As New List(Of RouterRoute)
        ''' <summary>rules (keywords and signals, instant) | classifier (a model picks the category) | hybrid (rules when clear, otherwise the classifier)</summary>
        Public Property Mode As String = "hybrid"
        ''' <summary>A fast model that classifies requests (classifier and hybrid modes).</summary>
        Public Property ClassifierModel As String
        ''' <summary>Prompts longer than this (estimated tokens) count as long context.</summary>
        Public Property LongContextTokens As Integer = 24000
        ''' <summary>Prepended to every request's system prompt.</summary>
        Public Property SystemPrompt As String
        ''' <summary>Model used for /v1/embeddings with this custom model's name.</summary>
        Public Property EmbeddingModel As String
    End Class

    Public NotInheritable Class RouterModel
        Public Property Id As String
        Public Property Name As String
        Public Property Description As String
        Public Property Enabled As Boolean = True
        Public Property Definition As New RouterDefinition()
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedBy As String
        Public Property UpdatedUtc As DateTime?
    End Class

    Public NotInheritable Class RouteDecision
        Public Property Model As String
        Public Property Category As String
        Public Property Reason As String
        Public Property Route As RouterRoute
        Public Property Fallbacks As New List(Of String)
    End Class

    ''' <summary>
    ''' Gateway to the provider (OpenWebUI) for people rather than agents: lists its models, forwards OpenAI-format requests
    ''' unchanged (streaming included), and runs custom models - one model name that sends each request to the model the
    ''' administrator chose for that kind of work (research, coding, vision, tools, JSON, translation …), with fallbacks.
    ''' </summary>
    Public NotInheritable Class ModelHub
        Private ReadOnly _http As HttpClient
        Private ReadOnly _baseUrl As String
        Private ReadOnly _db As Database
        Private ReadOnly _log As ILog
        Private _models As List(Of UpstreamModel)
        Private _hidden As New List(Of String) From {"arena-model"}

        ''' <summary>
        ''' Provider models that are never offered (Chat tab, /v1/models, the API): ids, or prefixes ending in *.
        ''' Default: OpenWebUI's "arena-model" (its model-comparison pseudo model).
        ''' </summary>
        Public Sub SetHiddenModels(spec As String)
            _hidden = If(spec, "").Split({","c, ";"c, " "c}, StringSplitOptions.RemoveEmptyEntries).Select(Function(s) s.Trim()).Where(Function(s) s.Length > 0).ToList()
            _models = Nothing
        End Sub

        Public Function IsHidden(id As String) As Boolean
            If String.IsNullOrWhiteSpace(id) Then Return False
            Return IsConfigHidden(id) OrElse IsDisabled(id)
        End Function

        ''' <summary>Hidden by configuration (Chat.HiddenModels): never listed, not even in Model control.</summary>
        Public Function IsConfigHidden(id As String) As Boolean
            If String.IsNullOrWhiteSpace(id) Then Return False
            Return _hidden.Any(Function(h) If(h.EndsWith("*"), id.StartsWith(h.TrimEnd("*"c), StringComparison.OrdinalIgnoreCase), id.Equals(h, StringComparison.OrdinalIgnoreCase)))
        End Function

        ' ------------------------------------------------------------------ providers that are one (Chat.ProviderMerges)

        ''' <summary>Member provider (id prefix) → the provider it belongs to: {"dddai2": "dddai"}.</summary>
        Private _merges As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        Public Sub SetProviderMerges(merges As IDictionary(Of String, String))
            _merges = New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            For Each kv In If(merges, New Dictionary(Of String, String)())
                If String.IsNullOrWhiteSpace(kv.Key) OrElse String.IsNullOrWhiteSpace(kv.Value) OrElse String.Equals(kv.Key.Trim(), kv.Value.Trim(), StringComparison.OrdinalIgnoreCase) Then Continue For
                _merges(kv.Key.Trim()) = kv.Value.Trim()
            Next
        End Sub

        ''' <summary>A model's provider (its id prefix before "/"), as merged: "dddai2/x" → "dddai". "" for a model without one.</summary>
        Public Function ProviderGroup(id As String) As String
            Dim slash = If(id, "").IndexOf("/"c)
            If slash <= 0 Then Return ""
            Dim prefix = id.Substring(0, slash)
            Dim main As String = Nothing
            Return If(_merges.TryGetValue(prefix, main), main, prefix)
        End Function

        ''' <summary>The same model at the other connections of its merged provider ("dddai/x" → "dddai2/x"), as listed now.</summary>
        Public Function MergedCopies(id As String, listed As IEnumerable(Of String)) As List(Of String)
            Dim slash = If(id, "").IndexOf("/"c)
            If slash <= 0 OrElse _merges.Count = 0 Then Return New List(Of String)
            Dim group = ProviderGroup(id), rest = id.Substring(slash + 1)
            Dim prefixes = _merges.Where(Function(kv) String.Equals(kv.Value, group, StringComparison.OrdinalIgnoreCase)).Select(Function(kv) kv.Key).Concat({group})
            Dim known = New HashSet(Of String)(listed, StringComparer.OrdinalIgnoreCase)
            Return prefixes.Select(Function(p) p & "/" & rest).Where(Function(x) Not String.Equals(x, id, StringComparison.OrdinalIgnoreCase) AndAlso known.Contains(x)).ToList()
        End Function

        ''' <summary>
        ''' The models to offer once each: a member provider's model is left out when its main provider has the same model
        ''' ("dddai2/x" when "dddai/x" is listed); the rest are kept with their own ids.
        ''' </summary>
        Public Function WithoutMergedDuplicates(Of T)(models As IEnumerable(Of T), idOf As Func(Of T, String)) As List(Of T)
            Dim list = models.ToList()
            If _merges.Count = 0 Then Return list
            Dim ids = New HashSet(Of String)(list.Select(idOf), StringComparer.OrdinalIgnoreCase)
            Return list.Where(Function(m)
                                  Dim id = idOf(m)
                                  Dim slash = id.IndexOf("/"c)
                                  If slash <= 0 Then Return True
                                  Dim main As String = Nothing
                                  If Not _merges.TryGetValue(id.Substring(0, slash), main) Then Return True
                                  Return Not ids.Contains(main & "/" & id.Substring(slash + 1))
                              End Function).ToList()
        End Function
        Private _modelsAt As DateTime

        ' ------------------------------------------------------------------ Model control (models turned off by an administrator)

        Private ReadOnly _disabled As New Concurrent.ConcurrentDictionary(Of String, (By As String, At As DateTime))(StringComparer.OrdinalIgnoreCase)

        ''' <summary>Whether an administrator turned this model off for everyone (Chat, the API, custom-model routes, agents, images, videos).</summary>
        Public Function IsDisabled(id As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(id) AndAlso _disabled.ContainsKey(id.Trim())
        End Function

        ''' <summary>The turned-off models: id, who turned it off and when.</summary>
        Public Function DisabledModels() As List(Of (Id As String, By As String, At As DateTime))
            Return _disabled.Select(Function(kv) (kv.Key, kv.Value.By, kv.Value.At)).OrderBy(Function(x) x.Item1, StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        Public Async Function LoadDisabledAsync() As Task
            Dim rows = Await _db.QueryAsync("SELECT ModelId, DisabledBy, DisabledUtc FROM DisabledModels",
                                            Function(r) (Db.Str(r, "ModelId"), Db.Str(r, "DisabledBy"), Db.Dt(r, "DisabledUtc"))).ConfigureAwait(False)
            _disabled.Clear()
            For Each x In rows
                _disabled(x.Item1) = (x.Item2, x.Item3)
            Next
            Await LoadModelSnapshotAsync().ConfigureAwait(False)
        End Function

        ''' <summary>Turns a model off (enabled = False) or back on, for everyone.</summary>
        Public Async Function SetEnabledAsync(id As String, enabled As Boolean, actor As String) As Task
            If String.IsNullOrWhiteSpace(id) Then Throw New ArgumentException("model is required.")
            id = id.Trim()
            If enabled Then
                Await _db.ExecuteAsync("DELETE FROM DisabledModels WHERE ModelId=@m", ("@m", CObj(id))).ConfigureAwait(False)
                _disabled.TryRemove(id, Nothing)
            Else
                Dim now = DateTime.UtcNow
                Await _db.ExecuteAsync("INSERT OR REPLACE INTO DisabledModels(ModelId, DisabledBy, DisabledUtc) VALUES (@m, @by, @n)",
                                       ("@m", CObj(id)), ("@by", actor), ("@n", Db.Ts(now))).ConfigureAwait(False)
                _disabled(id) = (actor, now)
            End If
        End Function
        Private _custom As List(Of RouterModel)

        Public Sub New(provider As AIProviderOptions, db As Database, log As ILog, Optional handler As HttpMessageHandler = Nothing)
            _baseUrl = If(provider?.BaseUrl, "").TrimEnd("/"c)
            _db = db
            _log = log
            _http = New HttpClient(If(handler, New SocketsHttpHandler With {.ConnectTimeout = TimeSpan.FromSeconds(Math.Max(5, If(provider?.ConnectTimeoutSeconds, 20))),
                                                                           .PooledConnectionLifetime = TimeSpan.FromMinutes(10)})) With {.Timeout = Timeout.InfiniteTimeSpan}
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Agentic/1.0")
            If Not String.IsNullOrEmpty(provider?.ApiKey) Then _http.DefaultRequestHeaders.Authorization = New AuthenticationHeaderValue("Bearer", provider.ApiKey)
        End Sub

        Public ReadOnly Property Configured As Boolean
            Get
                Return _baseUrl.Length > 0
            End Get
        End Property

        ' ------------------------------------------------------------------ category catalog

        Public Shared ReadOnly Catalog As RouteCategory() = {
            Sig("vision", "Images & vision", "The request contains images (screenshots, photos, diagrams, scans). Needs a vision-capable model."),
            Sig("tools", "Tool & function calling", "The request offers tools/functions the model may call (agents, MCP, plugins). Needs a model with reliable tool calling."),
            Sig("structured", "Structured output (JSON)", "The request asks for JSON output (response_format json_object or json_schema)."),
            Sig("long_context", "Long context", "The prompt is very long (big documents, codebases, transcripts). Needs a large context window."),
            Cat("coding", "Code", "Writing code", "Write, generate or complete code in any language; implement functions, classes, scripts, APIs and apps.",
                "code", "function", "class", "implement", "script", "program", "snippet", "method", "algorithm", "python", "javascript", "typescript", "c#", "csharp", ".net",
                "vb.net", "visual basic", "java", "golang", "rust", "c++", "kotlin", "swift", "php", "ruby", "html", "css", "react", "vue", "angular", "node", "npm",
                "api", "endpoint", "regex", "unit test", "compile", "library", "framework", "```"),
            Cat("debugging", "Code", "Debugging & errors", "Find and fix bugs, explain error messages, exceptions and stack traces, things that crash or do not work.",
                "error", "exception", "stack trace", "traceback", "bug", "crash", "fails", "failing", "broken", "not working", "doesn't work", "fix", "segfault", "undefined",
                "null reference", "nullreferenceexception", "debug", "warning:"),
            Cat("code_review", "Code", "Code review & security audit", "Review code for correctness, style, performance and security; audits, vulnerabilities, best practices.",
                "review", "audit", "vulnerability", "security", "best practice", "code smell", "refactor", "clean up", "optimize", "performance", "pull request", "pr "),
            Cat("devops", "Code", "DevOps, shell & infrastructure", "Servers, shells and infrastructure: Docker, Kubernetes, Linux, PowerShell, networking, CI/CD, cloud, deployment.",
                "docker", "kubernetes", "k8s", "bash", "powershell", "shell", "linux", "ubuntu", "windows server", "nginx", "apache", "terraform", "ansible", "ci/cd", "pipeline",
                "deploy", "server", "dns", "firewall", "ssh", "systemd", "cron", "aws", "azure", "gcp", "proxy", "ssl", "certificate"),
            Cat("data_sql", "Data", "Data, SQL & spreadsheets", "Databases, SQL queries, spreadsheets, CSV, data cleaning and transformation, pandas, charts from data.",
                "sql", "query", "database", "table", "join", "excel", "csv", "spreadsheet", "pandas", "dataframe", "pivot", "dataset", "etl", "postgres", "mysql", "sqlite", "chart"),
            Cat("math", "Reasoning", "Math & calculation", "Calculations, equations, proofs, statistics, probability, units and numeric problems.",
                "calculate", "equation", "solve", "proof", "prove", "integral", "derivative", "probability", "statistics", "math", "algebra", "geometry", "percentage", "formula"),
            Cat("reasoning", "Reasoning", "Complex reasoning & planning", "Multi-step thinking: plans, strategies, decisions, trade-offs, puzzles, root-cause analysis.",
                "plan", "strategy", "step by step", "trade-off", "tradeoff", "pros and cons", "decide", "decision", "think through", "analyze", "analyse", "evaluate", "puzzle", "logic"),
            Cat("research", "Knowledge", "Research & fact-finding", "Find, compare and explain facts: current events, companies, products, people, history, sources and evidence.",
                "research", "find out", "look up", "sources", "latest", "news", "who is", "what is", "history of", "compare", "market", "competitor", "evidence", "study", "statistics on",
                "according to", "report on", "investigate"),
            Cat("summarization", "Language", "Summarizing", "Condense documents, articles, meetings and threads into summaries, key points and TL;DRs.",
                "summarize", "summarise", "summary", "tl;dr", "tldr", "key points", "condense", "recap", "main points", "shorten", "digest"),
            Cat("writing", "Language", "Writing & editing", "Write or improve prose: emails, letters, articles, reports, documentation; edit, proofread, change tone.",
                "write", "rewrite", "essay", "article", "blog", "email", "letter", "edit", "proofread", "grammar", "tone", "paragraph", "documentation", "readme", "report"),
            Cat("creative", "Language", "Creative writing", "Stories, poems, lyrics, scripts, characters and other imaginative writing.",
                "story", "poem", "poetry", "lyrics", "song", "fiction", "character", "novel", "screenplay", "fantasy", "joke", "haiku", "fairy tale"),
            Cat("marketing", "Language", "Marketing & copywriting", "Ads, slogans, landing pages, product descriptions, SEO and social media posts.",
                "ad copy", "slogan", "landing page", "seo", "campaign", "product description", "tagline", "social media", "marketing", "headline", "newsletter", "brand"),
            Cat("translation", "Language", "Translation & languages", "Translate between languages, localize text, explain grammar of other languages.",
                "translate", "translation", "in spanish", "in french", "in german", "in chinese", "in japanese", "in italian", "in portuguese", "in russian", "to english",
                "localize", "localise", "übersetze", "traduire", "traducir"),
            Cat("business", "Knowledge", "Business, finance & legal", "Contracts, legal questions, finance, accounting, tax, pricing, budgets, compliance and policies.",
                "contract", "legal", "law", "lawyer", "invoice", "finance", "financial", "accounting", "tax", "revenue", "budget", "investment", "compliance", "policy", "gdpr",
                "pricing", "profit", "business plan"),
            Cat("science", "Knowledge", "Science & medicine", "Biology, chemistry, physics, medicine and health questions.",
                "biology", "chemistry", "physics", "medical", "medicine", "symptom", "diagnosis", "clinical", "drug", "dna", "molecule", "quantum", "astronomy"),
            Cat("education", "Knowledge", "Teaching & explanations", "Explain concepts simply, tutor, create lessons, exercises and quizzes.",
                "explain", "teach", "learn", "eli5", "how does", "tutorial", "lesson", "quiz", "exercise", "beginner", "understand"),
            Cat("extraction", "Data", "Extraction & classification", "Pull structured facts out of text, classify, label, tag, parse entities.",
                "extract", "classify", "categorize", "categorise", "label", "parse", "entities", "tag ", "sentiment", "detect"),
            Cat("brainstorming", "Language", "Brainstorming & ideas", "Generate ideas, names, options and alternatives.",
                "ideas", "brainstorm", "suggestions", "names for", "alternatives", "options for", "come up with", "inspire"),
            Cat("roleplay", "Conversation", "Role-play & personas", "Play a character or persona, simulate interviews and conversations.",
                "roleplay", "role-play", "pretend", "act as", "you are a", "in character", "simulate"),
            Cat("conversation", "Conversation", "Chat & small talk", "Greetings, thanks and casual conversation.",
                "hi", "hello", "hey", "thanks", "thank you", "how are you", "good morning", "good night")}

        Private Shared Function Sig(key As String, label As String, description As String) As RouteCategory
            Return New RouteCategory With {.Key = key, .Label = label, .Group = "Request type", .Description = description, .IsSignal = True, .Keywords = Array.Empty(Of String)()}
        End Function

        Private Shared Function Cat(key As String, group As String, label As String, description As String, ParamArray keywords As String()) As RouteCategory
            Return New RouteCategory With {.Key = key, .Label = label, .Group = group, .Description = description, .Keywords = keywords}
        End Function

        ' ------------------------------------------------------------------ provider models

        ''' <summary>The provider's models people may use: without hidden models and models turned off in Model control.</summary>
        Public Async Function ListUpstreamAsync(ct As CancellationToken, Optional force As Boolean = False) As Task(Of List(Of UpstreamModel))
            Dim all = Await ListAllUpstreamAsync(ct, force).ConfigureAwait(False)
            If _disabled.IsEmpty Then Return all
            Return all.Where(Function(m) Not IsDisabled(m.Id)).ToList()
        End Function

        ''' <summary>
        ''' The provider's models, including those turned off in Model control (not those hidden by configuration). Nobody waits
        ''' on a slow provider once a list is known: an older list (over 60 s) is returned at once while a fresh one loads in the
        ''' background, and the last good list is kept (and saved, for the next start) when the provider fails or hangs. Only the
        ''' very first load waits, and at most ModelListTimeout.
        ''' </summary>
        Public Async Function ListAllUpstreamAsync(ct As CancellationToken, Optional force As Boolean = False) As Task(Of List(Of UpstreamModel))
            Dim cached = _models
            If Not force AndAlso cached IsNot Nothing Then
                If (DateTime.UtcNow - _modelsAt).TotalSeconds >= 60 Then StartModelRefresh(False)
                Return cached
            End If
            Return Await StartModelRefresh(force).WaitAsync(ct).ConfigureAwait(False)
        End Function

        ''' <summary>How long one request for the provider's model list may take (it used to wait for as long as the provider hung).</summary>
        Public Property ModelListTimeout As TimeSpan = TimeSpan.FromSeconds(20)
        ''' <summary>After a failed load, how long before the provider is asked again (a hung provider is not asked on every page).</summary>
        Public Property ModelListRetryPause As TimeSpan = TimeSpan.FromSeconds(30)
        Private ReadOnly _refreshGate As New Object()
        Private _refresh As Task(Of List(Of UpstreamModel))
        Private _refreshFailedAt As DateTime = DateTime.MinValue
        Private _lastFailure As HttpRequestException
        Private _savedIds As String

        ''' <summary>Tests: the list counts as old, as if a minute had passed.</summary>
        Friend Sub ExpireModelListForTests()
            _modelsAt = DateTime.MinValue
        End Sub

        ''' <summary>The one load in flight (every caller shares it); after a failure the next try waits ModelListRetryPause.</summary>
        Private Function StartModelRefresh(force As Boolean) As Task(Of List(Of UpstreamModel))
            SyncLock _refreshGate
                If _refresh IsNot Nothing AndAlso Not _refresh.IsCompleted Then Return _refresh
                If Not force AndAlso DateTime.UtcNow - _refreshFailedAt < ModelListRetryPause Then
                    ' Failed a moment ago: the last list, or (none yet) the same failure at once instead of another wait.
                    If _models IsNot Nothing Then Return Task.FromResult(_models)
                    If _lastFailure IsNot Nothing Then Return Task.FromException(Of List(Of UpstreamModel))(_lastFailure)
                End If
                _refresh = Task.Run(Function() FetchModelsAsync())
                Return _refresh
            End SyncLock
        End Function

        Private Async Function FetchModelsAsync() As Task(Of List(Of UpstreamModel))
            Dim list As New List(Of UpstreamModel)
            Try
                Using cts As New CancellationTokenSource(ModelListTimeout)
                    Using resp = Await _http.GetAsync(_baseUrl & "/models", cts.Token).ConfigureAwait(False)
                        Dim text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then Throw New HttpRequestException($"Provider model list failed: {CInt(resp.StatusCode)} {TextUtil.Truncate(text, 200)}")
                        Using doc = JsonDocument.Parse(text)
                            Dim data As JsonElement
                            If doc.RootElement.TryGetProperty("data", data) AndAlso data.ValueKind = JsonValueKind.Array Then
                                For Each m In data.EnumerateArray()
                                    Dim id = JsonUtil.GetString(m, "id")
                                    If String.IsNullOrEmpty(id) OrElse IsConfigHidden(id) Then Continue For
                                    Dim created As Long = 0
                                    Dim c As JsonElement
                                    If m.TryGetProperty("created", c) AndAlso c.ValueKind = JsonValueKind.Number Then c.TryGetInt64(created)
                                    list.Add(New UpstreamModel With {.Id = id, .Name = If(JsonUtil.GetString(m, "name"), id), .OwnedBy = If(JsonUtil.GetString(m, "owned_by"), "openwebui"), .Created = created})
                                Next
                            End If
                        End Using
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is OperationCanceledException
                _refreshFailedAt = DateTime.UtcNow
                Dim why = If(TypeOf ex Is OperationCanceledException, $"no answer within {ModelListTimeout.TotalSeconds:0} s", ex.Message)
                _log.Warn("ModelHub", $"Provider model list failed ({TextUtil.Truncate(why, 200)}){If(_models IsNot Nothing, "; keeping the last list", "")}")
                If _models IsNot Nothing Then Return _models
                _lastFailure = New HttpRequestException("The provider's model list is not available: " & why, ex)
                Throw _lastFailure
            End Try
            _lastFailure = Nothing
            Dim sorted = list.OrderBy(Function(x) x.Id, StringComparer.OrdinalIgnoreCase).ToList()
            _models = sorted
            _modelsAt = DateTime.UtcNow
            Await SaveModelSnapshotAsync(sorted).ConfigureAwait(False)
            Return sorted
        End Function

        Private Const ModelSnapshotKey As String = "upstream.models"

        ''' <summary>The last good list, saved when it changes, so the first page after a restart does not wait on the provider.</summary>
        Private Async Function SaveModelSnapshotAsync(list As List(Of UpstreamModel)) As Task
            Dim ids = String.Join(",", list.Select(Function(m) m.Id & "|" & m.Name))
            If ids = _savedIds OrElse _db Is Nothing Then Return
            Try
                Await _db.ExecuteAsync("INSERT INTO HostState(Key, Value) VALUES (@k, @v) ON CONFLICT(Key) DO UPDATE SET Value=@v",
                                       ("@k", CObj(ModelSnapshotKey)), ("@v", JsonUtil.ToJson(list))).ConfigureAwait(False)
                _savedIds = ids
            Catch ex As Exception When TypeOf ex Is Microsoft.Data.Sqlite.SqliteException OrElse TypeOf ex Is InvalidOperationException
                _log.Warn("ModelHub", "Could not save the provider's model list: " & ex.Message)
            End Try
        End Function

        ''' <summary>Startup: the list saved last time (counted as old, so a fresh one loads in the background at first use).</summary>
        Private Async Function LoadModelSnapshotAsync() As Task
            If _db Is Nothing OrElse _models IsNot Nothing Then Return
            Try
                Dim json = TryCast(Await _db.ScalarAsync("SELECT Value FROM HostState WHERE Key=@k", ("@k", CObj(ModelSnapshotKey))).ConfigureAwait(False), String)
                If String.IsNullOrWhiteSpace(json) Then Return
                Dim list = JsonUtil.FromJson(Of List(Of UpstreamModel))(json)
                If list Is Nothing OrElse list.Count = 0 Then Return
                _models = list.Where(Function(m) m IsNot Nothing AndAlso Not String.IsNullOrEmpty(m.Id) AndAlso Not IsConfigHidden(m.Id)).ToList()
                _modelsAt = DateTime.MinValue
            Catch ex As Exception When TypeOf ex Is JsonException OrElse TypeOf ex Is Microsoft.Data.Sqlite.SqliteException
                _log.Warn("ModelHub", "Could not read the saved model list: " & ex.Message)
            End Try
        End Function

        ''' <summary>Forwards a request body to the provider (path like "/chat/completions"). The caller disposes the response.</summary>
        Private _byo As ByoService

        ''' <summary>Users' own models (My models): their chat calls go to their own provider, not the shared upstream.</summary>
        Public Sub AttachByo(byo As ByoService)
            _byo = byo
        End Sub

        Public Async Function SendAsync(path As String, body As String, ct As CancellationToken) As Task(Of HttpResponseMessage)
            If _byo IsNot Nothing AndAlso path = "/chat/completions" AndAlso body IsNot Nothing AndAlso body.Contains("""" & ByoService.IdPrefix, StringComparison.OrdinalIgnoreCase) Then
                Dim model As String = Nothing
                Try
                    model = JsonNode.Parse(body)?("model")?.ToString()
                Catch ex As JsonException
                End Try
                If ByoService.IsByoId(model) Then Return Await _byo.SendAsync(body, ct).ConfigureAwait(False)
            End If
            Dim resp = Await PostAsync(path, body, ct).ConfigureAwait(False)
            If resp.IsSuccessStatusCode OrElse path <> "/chat/completions" OrElse _merges.Count = 0 Then Return resp
            If _models Is Nothing Then
                ' Which copies exist: the provider's model list (not loaded yet only right after a start).
                Try
                    Await ListAllUpstreamAsync(ct).ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is OperationCanceledException
                End Try
                If _models Is Nothing Then Return resp
            End If
            ' A merged provider (Chat.ProviderMerges): the same model at its other connection answers instead.
            Dim asked As JsonNode = Nothing
            Try
                asked = JsonNode.Parse(body)
            Catch ex As JsonException
            End Try
            Dim id = asked?("model")?.ToString()
            Dim copies = If(id Is Nothing, New List(Of String), MergedCopies(id, _models.Select(Function(m) m.Id)))
            If copies.Count = 0 Then Return resp
            Dim status = CInt(resp.StatusCode)
            Dim errText = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
            If Not CopyMayHelp(status, errText) Then Return WithBody(resp, errText)
            For Each copy In copies
                asked("model") = copy
                Dim again = Await PostAsync(path, asked.ToJsonString(), ct).ConfigureAwait(False)
                If again.IsSuccessStatusCode Then
                    _log.Info("ModelHub", $"{id} failed ({status} {TextUtil.Truncate(errText, 120)}): {copy} answered instead")
                    resp.Dispose()
                    Return again
                End If
                again.Dispose()
            Next
            Return WithBody(resp, errText)
        End Function

        Private Async Function PostAsync(path As String, body As String, ct As CancellationToken) As Task(Of HttpResponseMessage)
            Dim req As New HttpRequestMessage(HttpMethod.Post, _baseUrl & path) With {.Content = New StringContent(body, Encoding.UTF8, "application/json")}
            req.Headers.Accept.ParseAdd("application/json")
            req.Headers.Accept.ParseAdd("text/event-stream")
            Return Await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(False)
        End Function

        ''' <summary>A failure another connection of the same provider may not have: its account (key, credit, limits), or its copy of the model.</summary>
        Friend Shared Function CopyMayHelp(status As Integer, errorText As String) As Boolean
            If status = 401 OrElse status = 402 OrElse status = 403 OrElse status = 404 OrElse status = 408 OrElse status = 429 OrElse status >= 500 Then Return True
            Return status = 400 AndAlso Text.RegularExpressions.Regex.IsMatch(If(errorText, ""),
                "quota|insufficient|billing|credit|balance|no deployments|not ?found|does not exist|unavailable|no available channel|overloaded",
                Text.RegularExpressions.RegexOptions.IgnoreCase)
        End Function

        ''' <summary>The failed response again, with the body that was read from it (for the caller to read).</summary>
        Private Shared Function WithBody(resp As HttpResponseMessage, text As String) As HttpResponseMessage
            Dim copy As New HttpResponseMessage(resp.StatusCode) With {.ReasonPhrase = resp.ReasonPhrase,
                .Content = New StringContent(If(text, ""), Encoding.UTF8, If(resp.Content.Headers.ContentType?.MediaType, "application/json"))}
            resp.Dispose()
            Return copy
        End Function

        ''' <summary>
        ''' Speech to text through the provider (OpenAI's /audio/transcriptions, which OpenWebUI serves at /api/v1/audio/transcriptions).
        ''' Returns the text, or throws HttpRequestException with the provider's message.
        ''' </summary>
        Public Async Function TranscribeAsync(audio As Byte(), fileName As String, mimeType As String, language As String, ct As CancellationToken) As Task(Of String)
            Dim lastError As String = Nothing
            For Each path In {"/audio/transcriptions", "/v1/audio/transcriptions"}
                Using form As New MultipartFormDataContent()
                    Dim file As New ByteArrayContent(audio)
                    file.Headers.ContentType = New MediaTypeHeaderValue(If(String.IsNullOrWhiteSpace(mimeType), "audio/webm", mimeType))
                    form.Add(file, "file", If(String.IsNullOrWhiteSpace(fileName), "speech.webm", fileName))
                    form.Add(New StringContent("whisper-1"), "model")
                    If Not String.IsNullOrWhiteSpace(language) Then form.Add(New StringContent(language), "language")
                    Using req As New HttpRequestMessage(HttpMethod.Post, _baseUrl & path) With {.Content = form}
                        Using resp = Await _http.SendAsync(req, ct).ConfigureAwait(False)
                            Dim body = Await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(False)
                            If resp.IsSuccessStatusCode Then
                                Try
                                    Dim j = JsonNode.Parse(body)
                                    Return If(j?("text")?.ToString(), "").Trim()
                                Catch ex As JsonException
                                    Return body.Trim()
                                End Try
                            End If
                            lastError = $"{CInt(resp.StatusCode)} {TextUtil.Truncate(body, 300)}"
                            If CInt(resp.StatusCode) <> 404 AndAlso CInt(resp.StatusCode) <> 405 Then Exit For
                        End Using
                    End Using
                End Using
            Next
            Throw New HttpRequestException("Speech to text failed: " & lastError)
        End Function

        ''' <summary>Records a call through the OpenAI-compatible API or the Chat tab.</summary>
        Public Async Function LogUsageAsync(username As String, source As String, endpoint As String, model As String, routedModel As String, category As String,
                                            stream As Boolean, status As Integer, promptTokens As Long, completionTokens As Long, durationMs As Long, errorText As String) As Task
            Try
                Await _db.ExecuteAsync("INSERT INTO ModelApiUsage(Username, Source, Endpoint, Model, RoutedModel, Category, Stream, Status, PromptTokens, CompletionTokens, DurationMs, Error, CreatedUtc) " &
                                       "VALUES (@u, @s, @e, @m, @r, @c, @st, @code, @p, @o, @d, @err, @now)",
                                       ("@u", CObj(username)), ("@s", source), ("@e", endpoint), ("@m", If(model, "?")), ("@r", routedModel), ("@c", category), ("@st", stream),
                                       ("@code", status), ("@p", promptTokens), ("@o", completionTokens), ("@d", durationMs), ("@err", TextUtil.Truncate(errorText, 1000)),
                                       ("@now", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("ModelHub", "Could not record model API usage: " & ex.Message)
            End Try
        End Function

        ' ------------------------------------------------------------------ custom models (storage)

        Private Shared Function MapCustom(r As Microsoft.Data.Sqlite.SqliteDataReader) As RouterModel
            Return New RouterModel With {
                .Id = Db.Str(r, "Id"), .Name = Db.Str(r, "Name"), .Description = Db.Str(r, "Description"), .Enabled = Db.Bool(r, "Enabled"),
                .Definition = If(JsonUtil.FromJson(Of RouterDefinition)(Db.Str(r, "DefinitionJson")), New RouterDefinition()),
                .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedBy = Db.Str(r, "UpdatedBy"), .UpdatedUtc = Db.DtN(r, "UpdatedUtc")}
        End Function

        Public Async Function ListCustomAsync() As Task(Of List(Of RouterModel))
            Dim c = _custom
            If c IsNot Nothing Then Return c
            c = Await _db.QueryAsync("SELECT * FROM CustomRouterModels ORDER BY Name", AddressOf MapCustom).ConfigureAwait(False)
            _custom = c
            Return c
        End Function

        Public Async Function GetCustomAsync(id As String) As Task(Of RouterModel)
            Return (Await ListCustomAsync().ConfigureAwait(False)).FirstOrDefault(Function(m) String.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        End Function

        Public Shared Function ValidId(id As String) As Boolean
            Return Not String.IsNullOrWhiteSpace(id) AndAlso id.Length <= 80 AndAlso Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9._:/-]*$")
        End Function

        ''' <summary>Creates or replaces a custom model. Returns problems (empty = saved).</summary>
        Public Async Function SaveCustomAsync(m As RouterModel, actor As String, isNew As Boolean) As Task(Of List(Of String))
            Dim problems As New List(Of String)
            If Not ValidId(m.Id) Then problems.Add("Model id: 1-80 characters, letters, digits and . _ : / - (clients use it as the model name).")
            If String.IsNullOrWhiteSpace(m.Name) Then problems.Add("A name is required.")
            Dim d = If(m.Definition, New RouterDefinition())
            m.Definition = d
            If String.IsNullOrWhiteSpace(d.DefaultModel) Then problems.Add("Choose a default model (it answers anything no route claims).")
            If Not {"rules", "classifier", "hybrid"}.Contains(If(d.Mode, "")) Then problems.Add("Mode must be rules, classifier or hybrid.")
            If d.Mode <> "rules" AndAlso String.IsNullOrWhiteSpace(d.ClassifierModel) Then problems.Add("Choose a classifier model, or use the rules mode.")
            d.Routes = If(d.Routes, New List(Of RouterRoute)).Where(Function(r) r IsNot Nothing AndAlso Not String.IsNullOrWhiteSpace(r.Category)).ToList()
            For Each r In d.Routes
                If Not Catalog.Any(Function(c) c.Key = r.Category) Then problems.Add($"Unknown category '{r.Category}'.")
                If String.IsNullOrWhiteSpace(r.Model) Then problems.Add($"Route '{r.Category}' has no model.")
                r.Fallbacks = If(r.Fallbacks, New List(Of String)).Where(Function(x) Not String.IsNullOrWhiteSpace(x)).ToList()
                r.Keywords = If(r.Keywords, New List(Of String)).Select(Function(x) x.Trim().ToLowerInvariant()).Where(Function(x) x.Length > 0).Distinct().ToList()
            Next
            Dim dup = d.Routes.GroupBy(Function(r) r.Category).FirstOrDefault(Function(g) g.Count() > 1)
            If dup IsNot Nothing Then problems.Add($"Category '{dup.Key}' has more than one route.")
            Dim upstream As List(Of UpstreamModel) = Nothing
            Try
                upstream = Await ListUpstreamAsync(CancellationToken.None).ConfigureAwait(False)
            Catch ex As Exception
            End Try
            If upstream IsNot Nothing AndAlso upstream.Count > 0 Then
                Dim known = upstream.Select(Function(x) x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                Dim used = {d.DefaultModel, d.ClassifierModel, d.EmbeddingModel}.Concat(d.DefaultFallbacks).Concat(d.Routes.SelectMany(Function(r) {r.Model}.Concat(r.Fallbacks)))
                For Each u In used.Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Distinct()
                    If Not known.Contains(u) Then problems.Add($"Model '{u}' is not available at the provider.")
                Next
            End If
            If upstream IsNot Nothing AndAlso upstream.Any(Function(x) String.Equals(x.Id, m.Id, StringComparison.OrdinalIgnoreCase)) Then problems.Add($"'{m.Id}' is already a provider model id; choose another.")
            Dim existing = Await GetCustomAsync(m.Id).ConfigureAwait(False)
            If isNew AndAlso existing IsNot Nothing Then problems.Add($"A custom model '{m.Id}' already exists.")
            If problems.Count > 0 Then Return problems
            Dim now = Db.Ts(DateTime.UtcNow)
            Await _db.ExecuteAsync("INSERT INTO CustomRouterModels(Id, Name, Description, DefinitionJson, Enabled, CreatedBy, CreatedUtc) VALUES (@id, @n, @d, @j, @e, @by, @now) " &
                                   "ON CONFLICT(Id) DO UPDATE SET Name=@n, Description=@d, DefinitionJson=@j, Enabled=@e, UpdatedBy=@by, UpdatedUtc=@now",
                                   ("@id", CObj(m.Id.Trim())), ("@n", m.Name.Trim()), ("@d", m.Description), ("@j", JsonUtil.ToJson(d)), ("@e", m.Enabled), ("@by", actor), ("@now", now)).ConfigureAwait(False)
            _custom = Nothing
            Return problems
        End Function

        Public Async Function DeleteCustomAsync(id As String) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("DELETE FROM CustomRouterModels WHERE Id=@id", ("@id", CObj(id))).ConfigureAwait(False)
            _custom = Nothing
            Return n > 0
        End Function

        ' ------------------------------------------------------------------ routing

        ''' <summary>Text of the latest user message (string content or text parts).</summary>
        Public Shared Function LastUserText(request As JsonNode) As String
            Dim msgs = TryCast(request?("messages"), JsonArray)
            If msgs IsNot Nothing Then
                For i = msgs.Count - 1 To 0 Step -1
                    Dim m = msgs(i)
                    If m Is Nothing OrElse CStr(m("role")?.GetValue(Of String)()) <> "user" Then Continue For
                    Return ContentText(m("content"))
                Next
            End If
            Dim prompt = request?("prompt")
            If prompt IsNot Nothing Then Return If(TryCast(prompt, JsonValue)?.ToString(), prompt.ToJsonString())
            Return ""
        End Function

        Public Shared Function ContentText(content As JsonNode) As String
            If content Is Nothing Then Return ""
            If TypeOf content Is JsonValue Then Return content.ToString()
            Dim arr = TryCast(content, JsonArray)
            If arr Is Nothing Then Return ""
            Return String.Join(vbLf, arr.Where(Function(p) p IsNot Nothing AndAlso CStr(p("type")?.ToString()) = "text").Select(Function(p) CStr(p("text")?.ToString())))
        End Function

        Private Shared Function HasImages(request As JsonNode) As Boolean
            Dim msgs = TryCast(request?("messages"), JsonArray)
            If msgs Is Nothing Then Return False
            Return msgs.Any(Function(m) TypeOf m?("content") Is JsonArray AndAlso DirectCast(m("content"), JsonArray).Any(Function(p) p IsNot Nothing AndAlso CStr(p("type")?.ToString()) = "image_url"))
        End Function

        Private Shared Function EstimateTokens(request As JsonNode) As Integer
            Dim msgs = TryCast(request?("messages"), JsonArray)
            If msgs Is Nothing Then Return CInt(LastUserText(request).Length / 3.5)
            Return CInt(msgs.Sum(Function(m) ContentText(m?("content")).Length) / 3.5)
        End Function

        ''' <summary>Keyword scores for every routed content category (higher = better match).</summary>
        Public Shared Function ScoreCategories(text As String, routes As IEnumerable(Of RouterRoute)) As List(Of (Category As String, Score As Integer))
            Dim t = " " & If(text, "").ToLowerInvariant() & " "
            Dim scores As New List(Of (Category As String, Score As Integer))
            For Each r In routes
                Dim c = Catalog.FirstOrDefault(Function(x) x.Key = r.Category)
                If c Is Nothing OrElse c.IsSignal Then Continue For
                Dim score = 0
                For Each k In c.Keywords.Concat(If(r.Keywords, New List(Of String)))
                    If String.IsNullOrWhiteSpace(k) Then Continue For
                    If k = "```" Then
                        If t.Contains("```") Then score += 3
                        Continue For
                    End If
                    Dim pattern = If(Char.IsLetterOrDigit(k(0)), "(?<![\p{L}\p{N}])", "") & Regex.Escape(k.Trim()) & If(Char.IsLetterOrDigit(k(k.Length - 1)), "(?![\p{L}\p{N}])", "")
                    Dim hits = Regex.Matches(t, pattern).Count
                    If hits > 0 Then score += If(k.Contains(" "c), 3, 2) + Math.Min(2, hits - 1)
                Next
                ' A stack trace or exception text is a strong debugging signal.
                If c.Key = "debugging" AndAlso Regex.IsMatch(t, "(exception|error)[:\s]|at [\w.]+\(.*\)|traceback \(most recent call last\)|line \d+") Then score += 3
                If score > 0 Then scores.Add((c.Key, score))
            Next
            Return scores.OrderByDescending(Function(s) s.Score).ToList()
        End Function

        ''' <summary>Which model answers this request, and why.</summary>
        Public Async Function DecideAsync(m As RouterModel, request As JsonNode, ct As CancellationToken) As Task(Of RouteDecision)
            Dim d = m.Definition
            Dim routes = If(d.Routes, New List(Of RouterRoute))
            Dim pick = Function(r As RouterRoute, reason As String) New RouteDecision With {.Model = r.Model, .Category = r.Category, .Reason = reason, .Route = r, .Fallbacks = If(r.Fallbacks, New List(Of String))}
            ' 1. Signals: what the request needs from the model, regardless of its words.
            Dim byCat = routes.ToDictionary(Function(r) r.Category, StringComparer.OrdinalIgnoreCase)
            If byCat.ContainsKey("vision") AndAlso HasImages(request) Then Return pick(byCat("vision"), "the request contains images")
            Dim rf = request?("response_format")?("type")?.ToString()
            If byCat.ContainsKey("structured") AndAlso (rf = "json_object" OrElse rf = "json_schema") Then Return pick(byCat("structured"), "JSON output requested (" & rf & ")")
            Dim tools = TryCast(request?("tools"), JsonArray)
            If byCat.ContainsKey("tools") AndAlso ((tools IsNot Nothing AndAlso tools.Count > 0) OrElse request?("functions") IsNot Nothing) Then Return pick(byCat("tools"), "the request offers tools")
            Dim est = EstimateTokens(request)
            If byCat.ContainsKey("long_context") AndAlso est > Math.Max(1000, d.LongContextTokens) Then Return pick(byCat("long_context"), $"long prompt (~{est:N0} tokens)")
            ' 2. Content: keywords, a classifier model, or both.
            Dim text = LastUserText(request)
            Dim scores = ScoreCategories(text, routes)
            Dim mode = If(d.Mode, "hybrid")
            Dim clear = scores.Count > 0 AndAlso scores(0).Score >= 3 AndAlso (scores.Count = 1 OrElse scores(0).Score >= scores(1).Score + 2)
            If mode = "rules" OrElse (mode = "hybrid" AndAlso clear) Then
                If scores.Count > 0 Then Return pick(byCat(scores(0).Category), $"keywords matched {Label(scores(0).Category)} (score {scores(0).Score})")
            ElseIf Not String.IsNullOrWhiteSpace(d.ClassifierModel) Then
                Dim chosen = Await ClassifyAsync(d.ClassifierModel, text, routes, ct).ConfigureAwait(False)
                If chosen IsNot Nothing AndAlso byCat.ContainsKey(chosen) Then Return pick(byCat(chosen), $"classifier ({d.ClassifierModel}) chose {Label(chosen)}")
                If scores.Count > 0 Then Return pick(byCat(scores(0).Category), $"keywords matched {Label(scores(0).Category)} (classifier undecided)")
            End If
            Return New RouteDecision With {.Model = d.DefaultModel, .Category = "default", .Reason = "no route matched; default model", .Fallbacks = If(d.DefaultFallbacks, New List(Of String))}
        End Function

        Public Shared Function Label(category As String) As String
            Return If(Catalog.FirstOrDefault(Function(c) c.Key = category)?.Label, category)
        End Function

        Private Async Function ClassifyAsync(model As String, text As String, routes As List(Of RouterRoute), ct As CancellationToken) As Task(Of String)
            Dim options = routes.Select(Function(r) Catalog.FirstOrDefault(Function(c) c.Key = r.Category)).Where(Function(c) c IsNot Nothing AndAlso Not c.IsSignal).ToList()
            If options.Count = 0 Then Return Nothing
            Dim list = String.Join(vbLf, options.Select(Function(c) $"- {c.Key}: {c.Description}")) & vbLf & "- none: anything else"
            Dim body As New JsonObject From {
                {"model", model}, {"stream", False}, {"temperature", 0}, {"max_tokens", 400},
                {"messages", New JsonArray(
                    New JsonObject From {{"role", "system"}, {"content", "Classify the user's request into exactly one category. Reply with only the category key." & vbLf & list}},
                    New JsonObject From {{"role", "user"}, {"content", TextUtil.Truncate(text, 4000)}})}}
            Try
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(TimeSpan.FromSeconds(25))
                    Using resp = Await SendAsync("/chat/completions", body.ToJsonString(), cts.Token).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then Return Nothing
                        Dim json = JsonNode.Parse(Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False))
                        Dim answer = If(JsonUtil.FirstChoice(json)?("message")?("content")?.ToString(), "").ToLowerInvariant()
                        Return options.Select(Function(c) c.Key).OrderByDescending(Function(k) k.Length).FirstOrDefault(Function(k) answer.Contains(k))
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException
                Return Nothing
            End Try
        End Function

        ''' <summary>A short non-streamed completion (titles, classification helpers). Nothing when the model fails or times out.</summary>
        Public Async Function CompleteTextAsync(model As String, system As String, user As String, maxTokens As Integer, timeout As TimeSpan, ct As CancellationToken, Optional suppressThinking As Boolean = False) As Task(Of String)
            Return (Await CompleteTextWithUsageAsync(model, system, user, maxTokens, timeout, ct, suppressThinking).ConfigureAwait(False)).Text
        End Function

        ''' <summary>CompleteTextAsync with the call's token use (for charging it): Text is Nothing when nothing came back.</summary>
        Public Async Function CompleteTextWithUsageAsync(model As String, system As String, user As String, maxTokens As Integer, timeout As TimeSpan, ct As CancellationToken,
                                                         Optional suppressThinking As Boolean = False) As Task(Of (Text As String, PromptTokens As Long, CompletionTokens As Long, CachedTokens As Long))
            Dim t = Await CompleteTextOnceAsync(model, system, user, maxTokens, timeout, ct, suppressThinking).ConfigureAwait(False)
            ' The thinking-off hints can be refused by strict providers (a 400); if nothing came back, try once more without them.
            If t.Text Is Nothing AndAlso suppressThinking Then t = Await CompleteTextOnceAsync(model, system, user, maxTokens, timeout, ct, False).ConfigureAwait(False)
            Return t
        End Function

        Private Async Function CompleteTextOnceAsync(model As String, system As String, user As String, maxTokens As Integer, timeout As TimeSpan, ct As CancellationToken,
                                                     suppressThinking As Boolean) As Task(Of (Text As String, PromptTokens As Long, CompletionTokens As Long, CachedTokens As Long))
            Dim body As New JsonObject From {
                {"model", model}, {"stream", False}, {"temperature", 0.2}, {"max_tokens", maxTokens},
                {"messages", New JsonArray(
                    New JsonObject From {{"role", "system"}, {"content", system}},
                    New JsonObject From {{"role", "user"}, {"content", user}})}}
            ' Ask reasoning models not to think so the token budget goes to the answer, not a <think> block that leaves no text.
            If suppressThinking Then
                body("reasoning_effort") = "none"
                body("chat_template_kwargs") = New JsonObject From {{"enable_thinking", False}}
            End If
            Try
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(timeout)
                    Using resp = Await SendAsync("/chat/completions", body.ToJsonString(), cts.Token).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then Return (Nothing, 0, 0, 0)
                        Dim json = JsonNode.Parse(Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False))
                        Dim text = JsonUtil.FirstChoice(json)?("message")?("content")?.ToString()
                        Dim usage = json?("usage")
                        Dim count = Function(n As JsonNode) As Long
                                        Dim v As Long
                                        Return If(n IsNot Nothing AndAlso Long.TryParse(n.ToString(), v), v, 0L)
                                    End Function
                        Return (If(String.IsNullOrWhiteSpace(text), Nothing, text.Trim()), count(usage?("prompt_tokens")), count(usage?("completion_tokens")),
                                Global.Agentic.Economy.AccountService.CachedTokensOf(usage))
                    End Using
                End Using
            Catch ex As Exception When TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException
                Return (Nothing, 0, 0, 0)
            End Try
        End Function

        ''' <summary>The request as sent to the chosen model: its id, the route's system prompt and defaults.</summary>
        Public Shared Function ApplyRoute(request As JsonNode, d As RouterDefinition, decision As RouteDecision, model As String) As JsonNode
            Dim body = request.DeepClone()
            body("model") = model
            Dim extra = String.Join(vbLf & vbLf, {d.SystemPrompt, decision.Route?.SystemPrompt}.Where(Function(s) Not String.IsNullOrWhiteSpace(s)))
            Dim msgs = TryCast(body("messages"), JsonArray)
            If extra.Length > 0 AndAlso msgs IsNot Nothing Then
                Dim first = If(msgs.Count > 0, msgs(0), Nothing)
                If first IsNot Nothing AndAlso first("role")?.ToString() = "system" AndAlso TypeOf first("content") Is JsonValue Then
                    first("content") = extra & vbLf & vbLf & first("content").ToString()
                Else
                    msgs.Insert(0, New JsonObject From {{"role", "system"}, {"content", extra}})
                End If
            End If
            If decision.Route IsNot Nothing Then
                If decision.Route.Temperature.HasValue AndAlso body("temperature") Is Nothing Then body("temperature") = decision.Route.Temperature.Value
                If decision.Route.MaxTokens.HasValue AndAlso body("max_tokens") Is Nothing AndAlso body("max_completion_tokens") Is Nothing Then body("max_tokens") = decision.Route.MaxTokens.Value
            End If
            Return body
        End Function

        ''' <summary>
        ''' Sends to the first model that answers (the decision's model, then its fallbacks). A model counts as unavailable
        ''' when the provider refuses before any output (404, 429, 5xx, "no available channel").
        ''' </summary>
        Public Async Function SendWithFallbacksAsync(path As String, request As JsonNode, d As RouterDefinition, decision As RouteDecision, ct As CancellationToken) As Task(Of (Response As HttpResponseMessage, Model As String, Tried As List(Of String)))
            Dim candidates = {decision.Model}.Concat(decision.Fallbacks).Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            ' Models an administrator turned off are skipped (Model control).
            Dim off = candidates.Where(AddressOf IsDisabled).ToList()
            candidates = candidates.Where(Function(x) Not IsDisabled(x)).ToList()
            If candidates.Count = 0 Then Throw New ModelRefusedException($"The models this custom model routes to are turned off by an administrator ({String.Join(", ", off)}).")
            Dim tried As New List(Of String)
            Dim last As HttpResponseMessage = Nothing
            For i = 0 To candidates.Count - 1
                Dim model = candidates(i)
                tried.Add(model)
                Dim body = If(d Is Nothing, request, ApplyRoute(request, d, decision, model))
                If d Is Nothing Then body("model") = model
                Dim resp = Await SendAsync(path, body.ToJsonString(), ct).ConfigureAwait(False)
                Dim code = CInt(resp.StatusCode)
                Dim retryable = code = 404 OrElse code = 429 OrElse code >= 500
                If resp.IsSuccessStatusCode OrElse Not retryable OrElse i = candidates.Count - 1 Then
                    last?.Dispose()
                    Return (resp, model, tried)
                End If
                last?.Dispose()
                last = resp
                _log.Warn("ModelHub", $"{model} unavailable ({code}); trying {candidates(i + 1)}.")
            Next
            Return (last, candidates.LastOrDefault(), tried)
        End Function
    End Class

End Namespace
