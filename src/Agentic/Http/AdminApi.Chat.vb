Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Text.Json
Imports System.Threading
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: the Chat tab. Conversations are private to the account that owns them; answers stream as server-sent events.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterChat(r As Router)
            r.Map("GET", "/api/chat/meta", AuthLevel.Read, AddressOf ChatMeta)
            r.Map("GET", "/api/chat/conversations", AuthLevel.Read, AddressOf ChatList)
            r.Map("POST", "/api/chat/conversations", AuthLevel.SubmitTask, AddressOf ChatCreate)
            r.Map("POST", "/api/chat/discuss", AuthLevel.SubmitTask, AddressOf ChatDiscussWork)
            r.Map("GET", "/api/chat/conversations/{chatId}", AuthLevel.Read, AddressOf ChatGet)
            r.Map("PATCH", "/api/chat/conversations/{chatId}", AuthLevel.SubmitTask, AddressOf ChatUpdate)
            r.Map("DELETE", "/api/chat/conversations/{chatId}", AuthLevel.SubmitTask, AddressOf ChatDelete)
            r.Map("POST", "/api/chat/conversations/delete", AuthLevel.SubmitTask, AddressOf ChatDeleteMany)
            r.Map("POST", "/api/chat/conversations/{chatId}/messages", AuthLevel.SubmitTask, AddressOf ChatSend)
            r.Map("GET", "/api/chat/conversations/{chatId}/attach", AuthLevel.Read, AddressOf ChatAttach)
            r.Map("POST", "/api/chat/conversations/{chatId}/steer", AuthLevel.SubmitTask, AddressOf ChatSteer)
            r.Map("GET", "/api/chat/conversations/{chatId}/steers", AuthLevel.SubmitTask, AddressOf ChatSteers)
            r.Map("DELETE", "/api/chat/conversations/{chatId}/steers/{steerId}", AuthLevel.SubmitTask, AddressOf ChatSteerCancel)
            r.Map("GET", "/api/chat/permission-rules", AuthLevel.Read, AddressOf ChatRulesGet)
            r.Map("PUT", "/api/chat/permission-rules", AuthLevel.SubmitTask, AddressOf ChatRulesPut)
            r.Map("POST", "/api/chat/conversations/{chatId}/external", AuthLevel.SubmitTask, AddressOf ChatExternal)
            r.Map("POST", "/api/chat/run", AuthLevel.SubmitTask, AddressOf ChatRunCode)
            r.Map("POST", "/api/chat/conversations/{chatId}/tool-results", AuthLevel.SubmitTask, AddressOf ChatToolResult)
            r.Map("GET", "/api/favicon/{host}", AuthLevel.Read, AddressOf Favicon)
            r.Map("GET", "/api/chat/stream", AuthLevel.Read, AddressOf ChatStream)
            r.Map("GET", "/api/chat/devices", AuthLevel.Read, AddressOf ChatDevices)
            r.Map("POST", "/api/chat/devices/{device}/catalog", AuthLevel.SubmitTask, AddressOf ChatDeviceCatalog)
            r.Map("GET", "/api/chat/devices/{device}/catalog", AuthLevel.Read, AddressOf ChatDeviceCatalogGet)
            r.Map("GET", "/api/chat/conversations/{chatId}/live", AuthLevel.Read, AddressOf ChatLiveSnapshot)
            r.Map("POST", "/api/chat/conversations/{chatId}/approvals", AuthLevel.SubmitTask, AddressOf ChatApprove)
            r.Map("POST", "/api/chat/conversations/{chatId}/approvals/request", AuthLevel.SubmitTask, AddressOf ChatApprovalRequest)
            r.Map("GET", "/api/chat/conversations/{chatId}/context", AuthLevel.Read, AddressOf ChatContext)
            r.Map("POST", "/api/chat/conversations/{chatId}/compact", AuthLevel.SubmitTask, AddressOf ChatCompact)
            r.Map("POST", "/api/chat/conversations/{chatId}/stop", AuthLevel.SubmitTask, AddressOf ChatStop)
            r.Map("GET", "/api/chat/preferences", AuthLevel.Read, AddressOf ChatPreferencesGet)
            r.Map("PUT", "/api/chat/preferences", AuthLevel.SubmitTask, AddressOf ChatPreferencesPut)
            r.Map("GET", "/api/chat/model-status", AuthLevel.Read, AddressOf ChatModelStatus)
            r.Map("POST", "/api/chat/conversations/{chatId}/rewind", AuthLevel.SubmitTask, AddressOf ChatRewind)
            r.Map("POST", "/api/chat/conversations/{chatId}/fork", AuthLevel.SubmitTask, AddressOf ChatFork)
            r.Map("POST", "/api/chat/conversations/{chatId}/aside", AuthLevel.SubmitTask, AddressOf ChatAside)
            RegisterChatFeatures(r)
            RegisterMyModels(r)
        End Sub

        Private Sub RequireModelAccess(rc As RequestContext, model As String)
            ' Claude Code runs on the user's computer with their own Claude account: no server model is used.
            If ChatService.IsExternalModel(model) Then Return
            ' A user's own model (My models): only its owner, whatever other model limits their account has.
            If Global.Agentic.AI.ByoService.IsByoId(model) Then
                If _h.Byo Is Nothing OrElse Not _h.Byo.CanUse(rc.User.Name, model) Then
                    Throw New ApiException(403, "MODEL_NOT_ALLOWED", $"'{model}' is not one of your models (My models), or it is switched off.")
                End If
                Return
            End If
            If Not String.IsNullOrWhiteSpace(model) AndAlso Not rc.User.CanUseModel(model) Then
                Throw New ApiException(403, "MODEL_NOT_ALLOWED", $"You do not have access to the model '{model}'. Ask an administrator to add it to your account.")
            End If
        End Sub

        Private Sub RequireChat()
            If Not _h.Chat.Options.Enabled Then Throw New ApiException(409, "CHAT_DISABLED", "The Chat tab is turned off (Chat.Enabled).")
        End Sub

        Private Shared Function CanUseTools(rc As RequestContext) As Boolean
            Return rc.User.HasPermission("chat.tools")
        End Function

        ''' <summary>
        ''' What this account may use in chat (Users &amp; access). MCP tools and the code interpreter need their permissions; the
        ''' other features need theirs only for User accounts (administrators have every permission).
        ''' </summary>
        Friend Shared Function ChatAccessFor(u As AuthUser) As ChatAccess
            Dim may = Function(key As String) Not u.IsRestrictedUser OrElse u.HasPermission(key)
            Return New ChatAccess With {
                .Mcp = u.HasPermission("chat.tools"), .Username = u.Name, .Code = u.HasPermission(If(u.IsRestrictedUser, "chat.code", "chat.tools")),
                .McpServers = If(u.IsRestrictedUser AndAlso u.AllowedMcpServers.Count > 0, New HashSet(Of String)(u.AllowedMcpServers, StringComparer.OrdinalIgnoreCase), Nothing),
                .Web = may("chat.web"), .Images = may("chat.images"), .Video = may("chat.video"), .Knowledge = may("chat.knowledge"),
                .Memory = may("chat.memory"), .Devices = may("chat.devices"), .Diffusion = u.HasPermission("images.sd"), .Music = u.HasPermission("music.suno")}
        End Function

        Private Async Function ChatMeta(rc As RequestContext) As Task
            RequireChat()
            Dim custom = (Await _h.ModelHub.ListCustomAsync().ConfigureAwait(False)).Where(Function(m) m.Enabled AndAlso Not _h.ModelHub.IsDisabled(m.Id) AndAlso rc.User.CanUseModel(m.Id)).
                Select(Function(m) New With {.id = m.Id, .name = m.Name, .description = m.Description, .custom = True,
                                             .routes = m.Definition.Routes.Select(Function(rt) Global.Agentic.AI.ModelHub.Label(rt.Category)).ToList()}).ToList()
            Dim provider As List(Of Object) = Nothing
            Dim providerError As String = Nothing
            Try
                ' Each model once (merged providers: Chat.ProviderMerges), with its provider for the model picker's tabs.
                Dim upstream = (Await _h.ModelHub.ListUpstreamAsync(rc.CancellationToken).ConfigureAwait(False)).Where(Function(m) rc.User.CanUseModel(m.Id) AndAlso Not Global.Agentic.AI.ImageRouter.IsImageModel(m.Id))
                provider = _h.ModelHub.WithoutMergedDuplicates(upstream, Function(m) m.Id).
                    Select(Function(m) CObj(New With {.id = m.Id, .name = m.Name, .ownedBy = m.OwnedBy, .group = _h.ModelHub.ProviderGroup(m.Id)})).ToList()
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is JsonException
                providerError = ex.Message
            End Try
            ' The account's own models (My models), first: listed with the provider models so every app (web, desktop, CLI,
            ' VS Code) offers them, even while the shared provider is unreachable.
            Dim mine = If(_h.Byo?.ListFor(rc.User.Name), New List(Of Global.Agentic.AI.ByoModel)).Where(Function(b) b.Enabled).ToList()
            If mine.Count > 0 Then
                provider = mine.Select(Function(b) CObj(New With {.id = b.Id, .name = b.Name, .ownedBy = "you", .byo = True,
                                                                  .provider = Global.Agentic.AI.ByoService.ProviderLabel(b.Provider), .vision = b.Vision})).
                    Concat(If(provider, New List(Of Object))).ToList()
            End If
            Dim access = ChatAccessFor(rc.User)
            Dim tools = access.Mcp
            Dim code = access.Code
            Dim may = Function(key As String) Not rc.User.IsRestrictedUser OrElse rc.User.HasPermission(key)
            Dim codeServer = _h.Chat.Options.CodeServerId
            ' Context window per model (custom models count with their default model).
            Dim windows As New Dictionary(Of String, Integer)(StringComparer.Ordinal)
            For Each m In (Await _h.ModelHub.ListCustomAsync().ConfigureAwait(False))
                windows(m.Id) = _h.Chat.CustomWindow(m)
            Next
            If providerError Is Nothing Then
                For Each m In (Await _h.ModelHub.ListUpstreamAsync(rc.CancellationToken).ConfigureAwait(False)).Where(Function(x) rc.User.CanUseModel(x.Id))
                    windows(m.Id) = _h.Chat.WindowFor(m.Id)
                Next
            End If
            For Each b In mine
                windows(b.Id) = b.ContextWindow
            Next
            ' Model Square prices (USD per 1M tokens) of the models offered here, and the account's balance.
            Dim prices = (Await PricesForAsync(rc).ConfigureAwait(False)).Where(Function(kv) windows.ContainsKey(kv.Key)).
                ToDictionary(Function(kv) kv.Key, Function(kv) New With {.input = kv.Value.InputPerMillion, .output = kv.Value.OutputPerMillion})
            Dim acc = If(rc.User.ViaSession OrElse rc.User.ViaApiKey, Await _h.Accounts.GetAccountAsync(rc.User.Name).ConfigureAwait(False), Nothing)
            Dim defaultModel = If(_h.Chat.Options.DefaultModel, custom.FirstOrDefault()?.id)
            If defaultModel IsNot Nothing AndAlso Not windows.ContainsKey(defaultModel) Then defaultModel = windows.Keys.FirstOrDefault()
            ' RAG: whether knowledge bases can be used (needs an embedding model), and the account's bases for the chip.
            Dim ragOn = _h.Rag IsNot Nothing AndAlso Await _h.Rag.AvailableAsync(rc.CancellationToken).ConfigureAwait(False)
            Dim ragBases = If(ragOn,
                              (Await _h.Rag.ListBasesAsync(rc.User.Name).ConfigureAwait(False)).Select(Function(b) CObj(New With {.id = b.Id, .name = b.Name, .docs = b.Docs, .chunks = b.Chunks})).ToList(),
                              New List(Of Object))
            Dim modelStatus = Await ModelStatusFor(rc).ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .customModels = custom, .providerModels = provider, .providerError = providerError, .modelStatus = modelStatus, .favoriteModels = Await FavoriteModelsAsync(rc.User.Name).ConfigureAwait(False),
                .defaultModel = defaultModel, .prices = prices,
                .publicOrigin = PublicOrigin(rc),
                .account = New With {.balanceMicros = If(acc?.BalanceMicros, 0L), .charged = acc IsNot Nothing AndAlso acc.Charged},
                .webSearch = _h.Chat.WebSearchAvailable AndAlso access.Web, .canTools = tools, .canCode = code, .isAdmin = rc.User.IsAdmin,
                .imageGeneration = _h.Chat.ImagesAvailable AndAlso access.Images, .videoGeneration = _h.Chat.VideosAvailable AndAlso access.Video,
                .markets = _h.Markets IsNot Nothing AndAlso _h.Markets.Enabled, .news = _h.News IsNot Nothing AndAlso _h.News.Enabled,
                .stocks = _h.Stocks IsNot Nothing AndAlso _h.Stocks.Enabled, .polymarket = _h.Polymarket IsNot Nothing AndAlso _h.Polymarket.Enabled,
                .knowledge = ragOn AndAlso access.Knowledge, .knowledgeBases = If(access.Knowledge, ragBases, New List(Of Object)),
                .canShare = may("chat.share"), .canUpload = may("chat.uploads"), .canMemory = access.Memory, .canDevices = access.Devices,
                .canWebpage = access.Web, .canOwnModels = may("models.own"),
                .diffusion = _h.Chat.DiffusionAvailable AndAlso access.Diffusion, .music = _h.Chat.MusicAvailable AndAlso access.Music, .mermaid = Mermaid IsNot Nothing,
                .voice = may("chat.voice") AndAlso _h.Speech IsNot Nothing AndAlso _h.Speech.Configured,
                .codeInterpreter = code AndAlso (_h.Mcp.GetTools(If(codeServer, "")).Any(Function(t) t.Name = "run_command") OrElse (_h.Chat.Options.AllowLocalCodeExecution AndAlso rc.User.IsAdmin)),
                .sandboxDownloads = code AndAlso _h.Chat.Options.Sandbox.AllowDownloads AndAlso _h.Sandbox IsNot Nothing AndAlso _h.Sandbox.Available(If(codeServer, "")),
                .sandboxPreviews = code AndAlso _h.Chat.Options.Sandbox.Previews AndAlso _h.Chat.Options.Sandbox.ManageContainers AndAlso _h.SandboxReaper IsNot Nothing AndAlso _h.Sandbox IsNot Nothing AndAlso _h.Sandbox.Available(If(codeServer, "")),
                .servers = If(tools, _h.Chat.AvailableServers(rc.User.Name).Where(Function(s) access.AllowsServer(s.Id) OrElse Global.Agentic.Mcp.McpUserServers.IsPersonalOf(s.Id, rc.User.Name)).Select(Function(s) New With {.id = s.Id, .name = s.Name, .description = s.Description, .tools = s.Tools, .connected = s.Connected}).Cast(Of Object)().ToList(),
                              New List(Of Object)),
                .contextWindows = windows, .defaultPermissionMode = Global.Agentic.Work.ChatService.NormalizeMode(_h.Chat.Options.DefaultPermissionMode),
                .autoCompactPercent = _h.Chat.Options.AutoCompactPercent, .devices = _h.Chat.Live.Devices(rc.User.Name),
                .defaultMaxToolRounds = _h.Chat.Options.MaxToolRounds, .defaultMaxTotalToolRounds = _h.Chat.Options.MaxTotalToolRounds,
                .defaultMaxWebSearches = _h.Chat.Options.MaxWebSearches, .defaultMaxParallelAgents = _h.Chat.Options.MaxParallelSubagents,
                .lastModel = Await AvailableLastModelAsync(rc, custom.Select(Function(c) c.id).ToList(), windows.Keys.ToList()).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>The model this account last chatted with, if it is still offered.</summary>
        Private Async Function AvailableLastModelAsync(rc As RequestContext, customIds As List(Of String), providerIds As List(Of String)) As Task(Of String)
            Dim last = Await _h.Chat.GetPreferenceAsync(rc.User.Name, ChatService.LastModelKey).ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(last) Then Return Nothing
            If customIds.Contains(last, StringComparer.OrdinalIgnoreCase) OrElse providerIds.Contains(last, StringComparer.OrdinalIgnoreCase) Then Return last
            Return Nothing
        End Function

        ''' <summary>{lastModel}: what this account's clients start with.</summary>
        ''' <summary>The health of the models (the dots in the model picker; it asks again every 30 s).</summary>
        Private Async Function ChatModelStatus(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(New With {.models = Await ModelStatusFor(rc).ConfigureAwait(False), .updatedUtc = Global.Agentic.Persistence.Db.Ts(DateTime.UtcNow)}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' How each model this account may use has been doing (chat, API and agents' calls): green, yellow or red, with its
        ''' speed (average response time, first token, tokens per second); a model with no calls in the last day is missing
        ''' (gray). The text of a failure is for administrators only; a user's own models (My models) are theirs alone.
        ''' </summary>
        Private Async Function ModelStatusFor(rc As RequestContext) As Task(Of Dictionary(Of String, Object))
            Dim result As New Dictionary(Of String, Object)(StringComparer.OrdinalIgnoreCase)
            If _h.ModelHealth Is Nothing Then Return result
            Dim snap = Await _h.ModelHealth.SnapshotAsync().ConfigureAwait(False)
            Dim admin = rc.User.IsAdmin
            For Each kv In snap
                If Not admin AndAlso Not rc.User.CanUseModel(kv.Key) Then Continue For
                If Global.Agentic.AI.ByoService.IsByoId(kv.Key) AndAlso Not (_h.Byo IsNot Nothing AndAlso _h.Byo.CanUse(rc.User.Name, kv.Key)) Then Continue For
                Dim m = kv.Value
                result(kv.Key) = New With {.state = m.State, .calls = m.Calls, .failures = m.Failures, .window = m.Window,
                                           .lastUtc = Global.Agentic.Persistence.Db.Ts(m.LastUtc), .lastOk = m.LastOk, .lastFailureUtc = Global.Agentic.Persistence.Db.Ts(m.LastFailureUtc), .lastErrorKind = m.LastErrorKind,
                                           .lastError = If(admin, m.LastError, Nothing), .chat = m.Chat, .api = m.Api, .agents = m.Agents,
                                           .responseMs = m.Speed?.ResponseMs, .firstTokenMs = m.Speed?.FirstTokenMs, .tps = m.Speed?.Tps, .speedCalls = If(m.Speed?.Calls, 0)}
            Next
            Return result
        End Function

        Private Async Function ChatPreferencesGet(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(New With {.lastModel = Await _h.Chat.GetPreferenceAsync(rc.User.Name, ChatService.LastModelKey).ConfigureAwait(False),
                                       .favorites = Await FavoriteModelsAsync(rc.User.Name).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {lastModel?, favorites?}: remembers a model chosen in a client before any message was sent with it, and the
        ''' account's favorite models (the model picker's Favorites tab, the same in every app).
        ''' </summary>
        Private Async Function ChatPreferencesPut(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim model = JsonUtil.GetString(b, "lastModel")
            If Not String.IsNullOrWhiteSpace(model) Then
                If _h.ModelHub.IsHidden(model) Then Throw ApiException.BadRequest("That model is not available.")
                RequireModelAccess(rc, model)
                Await _h.Chat.SetPreferenceAsync(rc.User.Name, ChatService.LastModelKey, model.Trim()).ConfigureAwait(False)
            End If
            Dim favEl As JsonElement
            If b.TryGetProperty("favorites", favEl) AndAlso favEl.ValueKind = JsonValueKind.Array Then
                Dim favs = favEl.EnumerateArray().Where(Function(x) x.ValueKind = JsonValueKind.String).Select(Function(x) x.GetString().Trim()).
                    Where(Function(x) x.Length > 0 AndAlso x.Length <= 200).Distinct(StringComparer.Ordinal).ToList()
                If favs.Count > 200 Then Throw ApiException.BadRequest("favorites: at most 200 models.")
                Await _h.Chat.SetPreferenceAsync(rc.User.Name, ChatService.FavoriteModelsKey, JsonUtil.ToJson(favs)).ConfigureAwait(False)
            End If
            Await rc.OkAsync(New With {.lastModel = Await _h.Chat.GetPreferenceAsync(rc.User.Name, ChatService.LastModelKey).ConfigureAwait(False),
                                       .favorites = Await FavoriteModelsAsync(rc.User.Name).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>The account's favorite models, in the order they were added.</summary>
        Private Async Function FavoriteModelsAsync(username As String) As Task(Of List(Of String))
            Dim raw = Await _h.Chat.GetPreferenceAsync(username, ChatService.FavoriteModelsKey).ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(raw) Then Return New List(Of String)
            Try
                Return If(JsonSerializer.Deserialize(Of List(Of String))(raw), New List(Of String))
            Catch ex As JsonException
                Return New List(Of String)
            End Try
        End Function

        Private Async Function ChatList(rc As RequestContext) As Task
            RequireChat()
            Dim list = Await _h.Chat.ListAsync(rc.User.Name, rc.QueryString("q"), rc.QueryString("archived") = "1", rc.QueryInt("limit", 200, 1, 500), rc.QueryString("projectId"),
                                              rc.QueryString("bookmarked") = "1").ConfigureAwait(False)
            Dim runs = _h.Chat.Live.ActiveRuns(rc.User.Name).Select(Function(x) JsonUtil.ToNode(x)).ToDictionary(Function(n) n("chatId").ToString(), Function(n) n)
            Await rc.OkAsync(list.Select(Function(c) New With {c.Id, c.Title, c.Model, c.Pinned, c.Bookmarked, c.Archived, c.CreatedUtc, c.UpdatedUtc, c.Preview, c.ProjectId, .isShared = c.ShareToken IsNot Nothing,
                .live = runs.ContainsKey(c.Id), .needsYou = runs.ContainsKey(c.Id) AndAlso CInt(runs(c.Id)("approvals").GetValue(Of Integer)()) > 0}).ToList()).ConfigureAwait(False)
        End Function

        ''' <param name="previous">The chat's settings before this change (decides which of Image and Video stays on).</param>
        Private Function ReadSettings(b As JsonElement, rc As RequestContext, Optional previous As ChatSettings = Nothing) As ChatSettings
            Dim el As JsonElement
            If Not b.TryGetProperty("settings", el) OrElse el.ValueKind <> JsonValueKind.Object Then Return Nothing
            ' A setting the window does not send keeps its value (the CLI knows nothing of the device the chat uses, a web tab
            ' opened earlier nothing of options set since); one sent as null is cleared.
            Dim merged = If(previous Is Nothing, New Text.Json.Nodes.JsonObject(), TryCast(Text.Json.Nodes.JsonNode.Parse(JsonUtil.ToJson(previous)), Text.Json.Nodes.JsonObject))
            For Each p In el.EnumerateObject()
                For Each old In merged.Select(Function(kv) kv.Key).Where(Function(k) String.Equals(k, p.Name, StringComparison.OrdinalIgnoreCase)).ToList()
                    merged.Remove(old)
                Next
                merged(p.Name) = Text.Json.Nodes.JsonNode.Parse(p.Value.GetRawText())
            Next
            Dim s = JsonUtil.FromJson(Of ChatSettings)(merged.ToJsonString())
            If s Is Nothing Then Return Nothing
            ChatAccessFor(rc.User).Apply(s)   ' what the account may not use stays off (Users & access)
            If Not rc.User.IsAdmin Then s.AllowApprovalTools = False
            s.SystemPrompt = TextUtil.Truncate(s.SystemPrompt, 8000)
            s.Goal = If(String.IsNullOrWhiteSpace(s.Goal), Nothing, TextUtil.Truncate(s.Goal.Trim(), 4000))
            If s.Claude IsNot Nothing AndAlso s.Claude.ToJsonString().Length > 20000 Then s.Claude = previous?.Claude
            ' The goal met last is the server's to record; a window that missed it still sends the old goal, which stays finished.
            s.GoalMet = previous?.GoalMet
            ' Where the research notes are written is the server's too: a window can neither move them nor point them elsewhere.
            s.ResearchNotesPath = previous?.ResearchNotesPath
            If s.Goal IsNot Nothing AndAlso previous IsNot Nothing AndAlso previous.Goal Is Nothing AndAlso s.Goal = previous.GoalMet Then s.Goal = Nothing
            s.ReasoningEffort = ChatService.NormalizeEffort(s.ReasoningEffort)
            s.OutputStyle = ChatService.NormalizeStyle(s.OutputStyle)
            s.Caveman = ChatService.NormalizeCaveman(s.Caveman)
            If s.MaxToolRounds.HasValue Then s.MaxToolRounds = Math.Clamp(s.MaxToolRounds.Value, 0, 10000)
            If s.MaxWebSearches.HasValue Then s.MaxWebSearches = Math.Clamp(s.MaxWebSearches.Value, 0, 1000)
            s.PermissionMode = ChatService.NormalizeMode(If(s.PermissionMode, _h.Chat.Options.DefaultPermissionMode))
            s.DeviceToolsOff = If(s.DeviceToolsOff, New List(Of String)).Where(Function(n) Not String.IsNullOrWhiteSpace(n) AndAlso n.Length <= 64).Distinct().Take(200).ToList()
            s.Knowledge = If(s.Knowledge, New List(Of String)).Where(Function(n) Not String.IsNullOrWhiteSpace(n) AndAlso n.Length <= 64).Distinct().Take(20).ToList()
            If s.PinnedMessages IsNot Nothing Then
                s.PinnedMessages = s.PinnedMessages.Where(Function(n) Not String.IsNullOrWhiteSpace(n) AndAlso n.Length <= 64).Distinct().Take(50).ToList()
                If s.PinnedMessages.Count = 0 Then s.PinnedMessages = Nothing
            End If
            s.KeepOneMediaTool(previous)
            Return s
        End Function

        Private Async Function ChatCreate(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            RequireModelAccess(rc, JsonUtil.GetString(b, "model"))
            Dim c As ChatConversation
            Try
                c = Await _h.Chat.CreateAsync(rc.User.Name, JsonUtil.GetString(b, "title"), JsonUtil.GetString(b, "model"), If(ReadSettings(b, rc), New ChatSettings()),
                                              JsonUtil.GetString(b, "projectId"), JsonUtil.GetBool(b, "temporary", False)).ConfigureAwait(False)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("PROJECT_NOT_FOUND", ex.Message)
            End Try
            ' A client's subagent (the CLI's Task): {parent: {chatId, callId, label?, type?}} streams its answers into that Task card.
            Dim parentEl As JsonElement
            If b.TryGetProperty("parent", parentEl) AndAlso parentEl.ValueKind = JsonValueKind.Object Then
                Dim parentChat = JsonUtil.GetString(parentEl, "chatId")
                If Not String.IsNullOrWhiteSpace(parentChat) AndAlso Await _h.Chat.GetAsync(rc.User.Name, parentChat).ConfigureAwait(False) IsNot Nothing Then
                    _h.Chat.LinkSubagent(rc.User.Name, c.Id, parentChat, JsonUtil.GetString(parentEl, "callId"), JsonUtil.GetString(parentEl, "label"), JsonUtil.GetString(parentEl, "type"))
                End If
            End If
            Await rc.WriteJsonAsync(201, c).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {agentId, taskId} | {globalTaskId} | {resultId}: opens a new chat to talk about work the agents did, with that work
        ''' (what was asked and what came back) posted into it as the agent's first message. Returns {id} of the new chat.
        ''' </summary>
        Private Async Function ChatDiscussWork(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim agentId = JsonUtil.GetString(b, "agentId"), taskId = JsonUtil.GetString(b, "taskId")
            Dim globalId = JsonUtil.GetString(b, "globalTaskId"), resultId = JsonUtil.GetString(b, "resultId")
            Dim title As String, content As String, reporter As String
            If Not String.IsNullOrWhiteSpace(resultId) Then
                If Not (rc.User.IsAdmin OrElse rc.User.HasPermission("results")) Then Throw New ApiException(403, "FORBIDDEN", "You cannot open results.")
                Dim r = Await _h.Results.GetAsync(resultId).ConfigureAwait(False)
                If r Is Nothing OrElse Not r.Participants.All(Function(p) Sees(rc, p)) Then Throw ApiException.NotFound("RESULT_NOT_FOUND", "Result does not exist.")
                title = r.Title : reporter = If(r.AgentId, "global") : content = ChatReportService.DiscussResultText(r)
            ElseIf Not String.IsNullOrWhiteSpace(globalId) Then
                Dim g = Await _h.GlobalTasks.GetAsync(globalId).ConfigureAwait(False)
                If g Is Nothing OrElse Not GlobalVisible(rc, g) Then Throw ApiException.NotFound("GLOBAL_TASK_NOT_FOUND", "Global task does not exist.")
                title = g.Name : reporter = If(String.IsNullOrEmpty(g.Options.SynthesisAgentId), "global", g.Options.SynthesisAgentId) : content = _h.ChatReports.DiscussGlobalText(g)
            Else
                If String.IsNullOrWhiteSpace(agentId) OrElse String.IsNullOrWhiteSpace(taskId) Then Throw ApiException.BadRequest("Give agentId and taskId, globalTaskId or resultId.")
                RequireAgentAccess(rc, agentId)
                Dim t = Await _h.Tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
                If t Is Nothing Then Throw ApiException.NotFound("TASK_NOT_FOUND", "Task does not exist.")
                title = t.Name : reporter = t.AgentId : content = _h.ChatReports.DiscussTaskText(t)
            End If
            ' The model the user chatted with last, when they may still use it; otherwise the chat picks its default.
            Dim model = Await _h.Chat.GetPreferenceAsync(rc.User.Name, ChatService.LastModelKey).ConfigureAwait(False)
            If String.IsNullOrWhiteSpace(model) OrElse Not (ChatService.IsExternalModel(model) OrElse rc.User.CanUseModel(model)) Then model = Nothing
            Dim c = Await _h.Chat.CreateAsync(rc.User.Name, "Discuss: " & title, model, New ChatSettings()).ConfigureAwait(False)
            Await _h.Chat.PostAgentReportAsync(rc.User.Name, c.Id, reporter, content).ConfigureAwait(False)
            Await AuditAsync(rc, "discuss-in-chat", "chat", c.Id, New With {.agentId = agentId, .taskId = taskId, .globalTaskId = globalId, .resultId = resultId}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, New With {.id = c.Id, .title = c.Title}).ConfigureAwait(False)
        End Function

        Private Async Function ChatOrThrow(rc As RequestContext) As Task(Of ChatConversation)
            Dim c = Await _h.Chat.GetAsync(rc.User.Name, rc.Route("chatId")).ConfigureAwait(False)
            If c Is Nothing Then Throw ApiException.NotFound("CHAT_NOT_FOUND", "Conversation does not exist.")
            Return c
        End Function

        Private Async Function ChatGet(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(Await ConversationWithVersionsAsync(Await ChatOrThrow(rc).ConfigureAwait(False)).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function ChatUpdate(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            RequireModelAccess(rc, JsonUtil.GetString(b, "model"))
            Dim boolOf = Function(name As String) As Boolean?
                             Dim e As JsonElement
                             If b.TryGetProperty(name, e) AndAlso (e.ValueKind = JsonValueKind.True OrElse e.ValueKind = JsonValueKind.False) Then Return e.GetBoolean()
                             Return Nothing
                         End Function
            Await _h.Chat.UpdateAsync(rc.User.Name, c.Id, JsonUtil.GetString(b, "title"), JsonUtil.GetString(b, "model"), ReadSettings(b, rc, c.Settings), boolOf("pinned"), boolOf("archived"), boolOf("bookmarked")).ConfigureAwait(False)
            ' {projectId}: moves the chat into a project ("" or null takes it out).
            Dim projectEl As JsonElement
            If b.TryGetProperty("projectId", projectEl) Then
                Try
                    Await _h.Chat.MoveToProjectAsync(rc.User.Name, c.Id, JsonUtil.GetString(b, "projectId")).ConfigureAwait(False)
                Catch ex As KeyNotFoundException
                    Throw ApiException.NotFound("PROJECT_NOT_FOUND", ex.Message)
                End Try
            End If
            Dim pickedModel = JsonUtil.GetString(b, "model")
            If Not String.IsNullOrWhiteSpace(pickedModel) AndAlso Not _h.ModelHub.IsHidden(pickedModel) Then Await _h.Chat.SetPreferenceAsync(rc.User.Name, ChatService.LastModelKey, pickedModel).ConfigureAwait(False)
            Await rc.OkAsync(Await _h.Chat.GetAsync(rc.User.Name, c.Id, False).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function ChatDelete(rc As RequestContext) As Task
            RequireChat()
            If Not Await _h.Chat.DeleteAsync(rc.User.Name, rc.Route("chatId")).ConfigureAwait(False) Then Throw ApiException.NotFound("CHAT_NOT_FOUND", "Conversation does not exist.")
            _h.Chat.Live.Publish(rc.User.Name, Nothing, "deleted", New With {.ids = {rc.Route("chatId")}})
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {ids: [chatId]}: deletes those conversations. Chats being answered right now are skipped.</summary>
        Private Async Function ChatDeleteMany(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ids As New List(Of String)
            Dim el As JsonElement
            If b.TryGetProperty("ids", el) AndAlso el.ValueKind = JsonValueKind.Array Then
                For Each x In el.EnumerateArray()
                    If x.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrWhiteSpace(x.GetString()) Then ids.Add(x.GetString())
                Next
            End If
            ids = ids.Distinct(StringComparer.Ordinal).ToList()
            If ids.Count = 0 Then Throw ApiException.BadRequest("Give the ids of the chats to delete.")
            If ids.Count > 1000 Then Throw ApiException.BadRequest("Delete at most 1000 chats at a time.")
            Dim deleted As New List(Of String), skipped As New List(Of String)
            For Each id In ids
                If _h.Chat.Live.IsLive(id) Then
                    skipped.Add(id)
                ElseIf Await _h.Chat.DeleteAsync(rc.User.Name, id).ConfigureAwait(False) Then
                    deleted.Add(id)
                End If
            Next
            If deleted.Count > 0 Then _h.Chat.Live.Publish(rc.User.Name, Nothing, "deleted", New With {.ids = deleted})
            Await rc.OkAsync(New With {.deleted = deleted, .skipped = skipped}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {content, model, attachments: [uploadId], settings, regenerate, editMessageId, continue}. editMessageId asks an edited
        ''' version of that question (attachments left out keep its files); continue adds to the last answer. Responds with server-sent events.
        ''' </summary>
        Private Async Function ChatSend(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            ' One answer at a time per chat: two at once would write over each other (and every window would show both). A
            ' message for the running answer goes to it (POST …/steer); a stopped one is only saving, and the new one waits for it.
            If _h.Chat.Live.IsBusy(c.Id) Then Throw New ApiException(409, "ANSWERING", "An answer is being written in this chat: add your message to it, or stop it first.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim regenEl As JsonElement
            Dim regenerate = b.TryGetProperty("regenerate", regenEl) AndAlso regenEl.ValueKind = JsonValueKind.True
            Dim continueAnswer = JsonUtil.GetBool(b, "continue", False)
            Dim editMessageId = JsonUtil.GetString(b, "editMessageId")
            Dim attachEl As JsonElement
            Dim attachments = If(b.TryGetProperty("attachments", attachEl) AndAlso attachEl.ValueKind = JsonValueKind.Array, JsonUtil.GetStringList(b, "attachments"),
                                 If(String.IsNullOrWhiteSpace(editMessageId), New List(Of String), Nothing))
            Dim settings = If(ReadSettings(b, rc, c.Settings), c.Settings)
            Dim access = ChatAccessFor(rc.User)
            access.Apply(settings)   ' also for settings saved before a permission was taken away
            ' The account must be allowed this model, and have money when the model has a price.
            Dim blocked = Await ModelUseProblemAsync(rc, If(JsonUtil.GetString(b, "model"), c.Model)).ConfigureAwait(False)
            ' The account's limits (Users & access): messages per minute, tokens per day.
            If blocked Is Nothing Then blocked = Await LimitProblemAsync(rc).ConfigureAwait(False)
            ' The user writes: an automatic goal continuation waiting to start gives way to this message.
            NextGoalGeneration(c.Id)
            Dim goalWatch As New GoalWatch()
            rc.BeginStream("text/event-stream; charset=utf-8")
            Using cts = CancellationTokenSource.CreateLinkedTokenSource(rc.CancellationToken)
                _h.Chat.Live.SetStopper(c.Id, cts)
                ' Every answer keeps working when this window's connection drops (a network blip, a proxy reconnect, a sleeping
                ' laptop): its events are kept and the window reconnects to them (GET …/attach); other windows follow it too.
                ' Only the stop button (POST …/stop) ends an answer.
                Dim sink As New ChatSink(c.Id, rc.User.Name, Function(text) rc.WriteStreamAsync(text))
                _h.Chat.Live.Sinks.Add(sink)
                Dim emit As Func(Of String, Object, Task) =
                    Function(type, payload)
                        If cts.IsCancellationRequested Then Return Task.CompletedTask
                        goalWatch.Note(type, payload)
                        Return sink.WriteAsync($"event: {type}" & vbLf & "data: " & JsonUtil.ToJson(payload) & vbLf & vbLf)
                    End Function
                ' A comment line every 15 s while nothing else is sent: proxies such as Cloudflare close a response that is silent
                ' for 100 s, and an answer can be quiet longer than that (a slow model, an approval, a tool on the user's computer).
                Dim keepAlive = Task.Run(Async Function()
                                             While Not cts.IsCancellationRequested
                                                 Try
                                                     Await Task.Delay(TimeSpan.FromSeconds(15), cts.Token).ConfigureAwait(False)
                                                 Catch ex As OperationCanceledException
                                                     Exit While
                                                 End Try
                                                 Await sink.PingAsync().ConfigureAwait(False)
                                             End While
                                         End Function)
                Dim problem As String = blocked
            Dim goalAtStart = settings?.Goal
                Dim clientCtx = ReadClientContext(b)
                clientCtx.IsDetached = Function() sink.Detached
                Try
                    If blocked Is Nothing Then Await _h.Chat.SendAsync(rc.User.Name, rc.User.IsAdmin, CanUseTools(rc), c.Id, JsonUtil.GetString(b, "content"), JsonUtil.GetString(b, "model"),
                                            attachments, settings, regenerate, emit, cts.Token, clientCtx, editMessageId, continueAnswer,
                                            Function(m) rc.User.CanUseModel(m), access).ConfigureAwait(False)
                Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is KeyNotFoundException OrElse TypeOf ex Is InvalidOperationException
                    problem = ex.Message
                Catch ex As Exception When Not cts.IsCancellationRequested
                    ' Never end the stream silently: the window would keep showing an answer that is no longer being written.
                    problem = "The answer failed: " & ex.Message
                End Try
                If problem IsNot Nothing AndAlso blocked Is Nothing Then Await emit("error", New With {.message = problem}).ConfigureAwait(False)
                If blocked IsNot Nothing Then Await sink.WriteAsync("event: error" & vbLf & "data: " & JsonUtil.ToJson(New With {.message = blocked}) & vbLf & vbLf).ConfigureAwait(False)
                ' Stopped with the stop button: the goal waits. Otherwise a goal still open goes on by itself.
                Dim stoppedByUser = cts.IsCancellationRequested
                _h.Chat.Live.ClearStopper(c.Id, cts)
                If Not stoppedByUser AndAlso blocked Is Nothing Then AfterGoalAnswer(rc.User, c.Id, goalWatch, 1, 0, 0, goalAtStart)
                ' The answer is over: stop the pings, and release a window that reconnected (its request ends with this).
                sink.Complete()
                cts.Cancel()
                Await keepAlive.ConfigureAwait(False)
            End Using
        End Function

        ''' <summary>Body {content}: a message for the answer being written (it reads it before its next step). 409 when none is running.</summary>
        Private Async Function ChatSteer(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim content = JsonUtil.GetString(b, "content")
            If String.IsNullOrWhiteSpace(content) Then Throw ApiException.BadRequest("content is required.")
            Dim s = _h.Chat.AddSteer(c.Id, content)
            If s Is Nothing Then Throw New ApiException(409, "NOT_ANSWERING", "No answer is being written in this chat: send it as a message.")
            Await rc.OkAsync(New With {.ok = True, .id = s.Id, .items = SteerItems(c.Id)}).ConfigureAwait(False)
        End Function

        ''' <summary>The messages added while the answer runs that the model has not read yet: {items: [{id, text}]}.</summary>
        Private Async Function ChatSteers(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Await rc.OkAsync(New With {.items = SteerItems(c.Id)}).ConfigureAwait(False)
        End Function

        ''' <summary>Takes back a message the model has not read yet. {removed: false} when it already read it.</summary>
        Private Async Function ChatSteerCancel(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim removed = _h.Chat.CancelSteer(c.Id, rc.Route("steerId"))
            Await rc.OkAsync(New With {.removed = removed, .items = SteerItems(c.Id)}).ConfigureAwait(False)
        End Function

        Private Function SteerItems(chatId As String) As List(Of Object)
            Return _h.Chat.PendingSteers(chatId).Select(Function(s) CObj(New With {.id = s.Id, .text = s.Text})).ToList()
        End Function

        Private Async Function ChatRulesGet(rc As RequestContext) As Task
            RequireChat()
            Dim r = Await _h.Chat.GetPermissionRulesAsync(rc.User.Name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.allow = r.Allow, .ask = r.Ask, .deny = r.Deny}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {allow: [..], ask: [..], deny: [..]}: patterns like "run_command(npm test*)", "write_file(*.md)", "*__delete*".</summary>
        Private Async Function ChatRulesPut(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim r = Global.Agentic.Work.PermissionRules.Parse(b.GetRawText())
            Await _h.Chat.SetPermissionRulesAsync(rc.User.Name, r).ConfigureAwait(False)
            Await rc.OkAsync(New With {.allow = r.Allow, .ask = r.Ask, .deny = r.Deny}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' A window whose connection dropped while an answer was being written reconnects to it: the events it missed, then
        ''' the rest live, in the same server-sent events as POST …/messages. When there is no answer to reconnect to (it
        ''' ended long ago, or the server restarted), the stream says "ended" and the window reloads the conversation.
        ''' </summary>
        Private Async Function ChatAttach(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim sink = _h.Chat.Live.Sinks.Get(c.Id, rc.User.Name)
            rc.BeginStream("text/event-stream; charset=utf-8")
            If sink Is Nothing Then
                Await rc.WriteStreamAsync("event: ended" & vbLf & "data: {""live"":" & If(_h.Chat.Live.IsLive(c.Id), "true", "false") & "}" & vbLf & vbLf).ConfigureAwait(False)
                Return
            End If
            Dim ended As Task
            Try
                ended = Await sink.AttachAsync(Function(text) rc.WriteStreamAsync(text)).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException
                Return
            End Try
            ' Pings keep this response open (proxies close silent ones) until the answer ends or this window goes away.
            While Not ended.IsCompleted
                Await Task.WhenAny(ended, Task.Delay(TimeSpan.FromSeconds(15))).ConfigureAwait(False)
                If Not ended.IsCompleted Then Await sink.PingAsync().ConfigureAwait(False)
            End While
        End Function

        ''' <summary>
        ''' Body {model, content, attachments, settings, answer: {content, reasoning, toolCalls, routedModel, durationMs, promptTokens,
        ''' completionTokens, error, finishReason}}: one exchange answered outside the server (Claude Code through the desktop app).
        ''' </summary>
        Private Async Function ChatExternal(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim model = If(JsonUtil.GetString(b, "model"), c.Model)
            If Not ChatService.IsExternalModel(model) Then Throw ApiException.BadRequest("Only answers from Claude Code (model claude-code…) can be saved this way.")
            Dim answerEl As JsonElement
            If Not b.TryGetProperty("answer", answerEl) OrElse answerEl.ValueKind <> JsonValueKind.Object Then Throw ApiException.BadRequest("answer is required.")
            Dim a = If(JsonUtil.FromJson(Of ChatMessageRecord)(answerEl.GetRawText()), New ChatMessageRecord())
            a.Model = TextUtil.Truncate(model, 120)
            a.RoutedModel = TextUtil.Truncate(a.RoutedModel, 120)
            a.RouteInfo = TextUtil.Truncate(a.RouteInfo, 300)
            a.Content = TextUtil.Truncate(If(a.Content, ""), 400000)
            a.Reasoning = TextUtil.Truncate(a.Reasoning, 200000)
            a.Error = TextUtil.Truncate(a.Error, 4000)
            a.FinishReason = TextUtil.Truncate(a.FinishReason, 40)
            a.Attachments = New List(Of ChatAttachmentRef)
            a.Sources = New List(Of ChatSource)
            a.ToolCalls = If(a.ToolCalls, New List(Of ChatToolCall)).Take(300).ToList()
            For Each t In a.ToolCalls
                t.Name = TextUtil.Truncate(t.Name, 120) : t.Arguments = TextUtil.Truncate(t.Arguments, 8000) : t.Result = TextUtil.Truncate(t.Result, 12000)
                t.Server = If(String.IsNullOrEmpty(t.Server), "claude-code", TextUtil.Truncate(t.Server, 60))
            Next
            Dim settings = If(ReadSettings(b, rc, c.Settings), c.Settings)
            Dim saved = Await _h.Chat.SaveExternalTurnAsync(rc.User.Name, rc.User.IsAdmin, c.Id, TextUtil.Truncate(JsonUtil.GetString(b, "content"), 200000),
                                                            JsonUtil.GetStringList(b, "attachments"), a, settings).ConfigureAwait(False)
            Await rc.OkAsync(New With {.user = saved.User, .message = saved.Answer, .title = saved.Title}).ConfigureAwait(False)
        End Function

        ''' <summary>Stops the answer being written in this chat (from any window, the CLI or the desktop app).</summary>
        Private Async Function ChatStop(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Await rc.OkAsync(New With {.stopped = _h.Chat.Live.Stop(c.Id)}).ConfigureAwait(False)
        End Function

        ''' <summary>Body {title?, messageId?}: a copy of this conversation (up to messageId) as a new one; returns {id, title}.</summary>
        Private Async Function ChatFork(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim copy As ChatConversation
            Try
                copy = Await _h.Chat.ForkAsync(rc.User.Name, c.Id, JsonUtil.GetString(b, "title"), JsonUtil.GetString(b, "messageId")).ConfigureAwait(False)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("MESSAGE_NOT_FOUND", ex.Message)
            End Try
            If copy Is Nothing Then Throw ApiException.NotFound("CHAT_NOT_FOUND", "Conversation does not exist.")
            ' A client's forked subagent (the CLI's Task with fork): {parent: {chatId, callId, label?, type?}} streams into that Task card.
            Dim parentEl As JsonElement
            If b.TryGetProperty("parent", parentEl) AndAlso parentEl.ValueKind = JsonValueKind.Object Then
                Dim parentChat = JsonUtil.GetString(parentEl, "chatId")
                If Not String.IsNullOrWhiteSpace(parentChat) AndAlso Await _h.Chat.GetAsync(rc.User.Name, parentChat).ConfigureAwait(False) IsNot Nothing Then
                    _h.Chat.LinkSubagent(rc.User.Name, copy.Id, parentChat, JsonUtil.GetString(parentEl, "callId"), JsonUtil.GetString(parentEl, "label"), JsonUtil.GetString(parentEl, "type"))
                End If
            End If
            Await rc.OkAsync(New With {.id = copy.Id, .title = copy.Title, .messages = copy.Messages.Count}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body {question, model}: /btw - a side question answered from the conversation so far, without tools; nothing is added
        ''' to the chat. The account must be allowed the model and be within its limits, as for a message.
        ''' </summary>
        Private Async Function ChatAside(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim model = If(JsonUtil.GetString(b, "model"), c.Model)
            Dim blocked = Await ModelUseProblemAsync(rc, model).ConfigureAwait(False)
            If blocked Is Nothing Then blocked = Await LimitProblemAsync(rc).ConfigureAwait(False)
            If blocked IsNot Nothing Then Throw New ApiException(403, "CHAT_BLOCKED", blocked)
            Dim answer As ChatAsideAnswer
            Try
                answer = Await _h.Chat.AsideAsync(rc.User.Name, rc.User.IsAdmin, c.Id, JsonUtil.GetString(b, "question"), model, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            Catch ex As Net.Http.HttpRequestException
                Throw New ApiException(502, "MODEL_FAILED", ex.Message)
            End Try
            Await rc.OkAsync(answer).ConfigureAwait(False)
        End Function

        ''' <summary>Body {messageId}: removes that message and everything after it (to go back to an earlier point).</summary>
        Private Async Function ChatRewind(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            If _h.Chat.Live.IsLive(c.Id) Then Throw New ApiException(409, "CHAT_BUSY", "Wait until the answer is finished.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim messageId = JsonUtil.GetString(b, "messageId")
            If String.IsNullOrWhiteSpace(messageId) Then Throw ApiException.BadRequest("messageId is required.")
            Dim full = Await _h.Chat.GetAsync(rc.User.Name, c.Id).ConfigureAwait(False)
            Dim taken = full?.Messages?.FirstOrDefault(Function(m) m.Id = messageId)
            Dim removed = Await _h.Chat.RewindAsync(rc.User.Name, c.Id, messageId).ConfigureAwait(False)
            If removed < 0 Then Throw ApiException.NotFound("MESSAGE_NOT_FOUND", "That message is not in this conversation.")
            ' The question taken back (when the rewind starts at a question): the window puts it into the message box again.
            Dim prompt = If(taken IsNot Nothing AndAlso taken.Role = "user", New With {.content = taken.Content, .attachments = taken.Attachments}, Nothing)
            Await rc.OkAsync(New With {.removed = removed, .prompt = prompt, .conversation = Await _h.Chat.GetAsync(rc.User.Name, c.Id, False).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        ''' <summary>Tools and instructions from the client (the desktop app's local tools); Nothing when the request has none.</summary>
        Private Shared Function ReadClientContext(b As JsonElement) As ChatClientContext
            Dim toolsEl As JsonElement
            Dim hasTools = b.TryGetProperty("clientTools", toolsEl) AndAlso toolsEl.ValueKind = JsonValueKind.Array
            Dim instructions = JsonUtil.GetString(b, "clientInstructions")
            Dim ctx As New ChatClientContext With {.Instructions = instructions, .ClientId = TextUtil.Truncate(JsonUtil.GetString(b, "clientId"), 64),
                                                   .DeviceId = TextUtil.Truncate(JsonUtil.GetString(b, "device"), 64), .PlanMode = JsonUtil.GetBool(b, "plan", False),
                                                   .Research = JsonUtil.GetBool(b, "research", False)}
            Dim roundsEl As JsonElement
            If b.TryGetProperty("maxToolRounds", roundsEl) AndAlso roundsEl.ValueKind = JsonValueKind.Number Then ctx.MaxToolRounds = Math.Max(0, Math.Min(1000, roundsEl.GetInt32()))
            ctx.MaxToolRoundsHard = JsonUtil.GetBool(b, "maxToolRoundsHard", False)
            If hasTools Then
                For Each t In toolsEl.EnumerateArray()
                    If t.ValueKind <> JsonValueKind.Object Then Continue For
                    Dim node = TryCast(Text.Json.Nodes.JsonNode.Parse(t.GetRawText()), Text.Json.Nodes.JsonObject)
                    If node IsNot Nothing Then ctx.Tools.Add(node)
                Next
            End If
            ctx.Agents = ChatService.ReadClientAgents(b)
            Dim rulesEl As JsonElement
            If b.TryGetProperty("clientRules", rulesEl) AndAlso rulesEl.ValueKind = JsonValueKind.Object Then ctx.Rules = Global.Agentic.Work.PermissionRules.Parse(rulesEl.GetRawText())
            Return ctx
        End Function

        ' ------------------------------------------------------------------ live: every window of the user, desktop apps, approvals

        ''' <summary>
        ''' One event stream per window (?device=&amp;name= for a desktop app): runs starting and ending in any window, the
        ''' answer as it streams, approvals to answer, desktop apps coming and going, and tool calls for this desktop app.
        ''' </summary>
        Private Async Function ChatStream(rc As RequestContext) As Task
            RequireChat()
            Dim live = _h.Chat.Live
            Dim user = rc.User.Name
            Dim s = live.Subscribe(user, rc.QueryString("device"), rc.QueryString("name"), rc.QueryString("page"))
            Try
                rc.BeginStream("text/event-stream; charset=utf-8")
                ' subscriber: this connection's id. A tool call for a device goes to one window of it only (data.target), so two
                ' windows of the same app (VS Code's side bar and an editor tab, two CLIs in one folder) never both run it.
                Await rc.WriteStreamAsync("event: hello" & vbLf & "data: " & JsonUtil.ToJson(New With {.runs = live.ActiveRuns(user), .devices = live.Devices(user), .subscriber = s.Id}) & vbLf & vbLf).ConfigureAwait(False)
                Dim reader = s.Channel.Reader
                While Not rc.CancellationToken.IsCancellationRequested
                    Dim waiting = reader.WaitToReadAsync(rc.CancellationToken).AsTask()
                    Dim first = Await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromSeconds(20))).ConfigureAwait(False)
                    If first Is waiting Then
                        If Not Await waiting.ConfigureAwait(False) Then Exit While
                        Dim item As String = Nothing
                        While reader.TryRead(item)
                            Await rc.WriteStreamAsync(item).ConfigureAwait(False)
                        End While
                    Else
                        Await rc.WriteStreamAsync(": ping" & vbLf & vbLf).ConfigureAwait(False)
                    End If
                End While
            Catch ex As Exception When TypeOf ex Is IOException OrElse TypeOf ex Is HttpListenerException OrElse TypeOf ex Is ObjectDisposedException OrElse TypeOf ex Is OperationCanceledException
                ' the window closed
            Finally
                live.Unsubscribe(s)
            End Try
        End Function

        Private Async Function ChatDevices(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(_h.Chat.Live.Devices(rc.User.Name)).ConfigureAwait(False)
        End Function

        ''' <summary>A desktop app tells which tools it offers (so chats from other windows can use them while it is connected).</summary>
        Private Async Function ChatDeviceCatalog(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ctx = ReadClientContext(b)
            _h.Chat.Live.SetCatalog(rc.User.Name, rc.Route("device"), JsonUtil.GetString(b, "name"), ctx.Tools, ctx.Instructions)
            Await rc.OkAsync(New With {.tools = ctx.Tools.Count}).ConfigureAwait(False)
        End Function

        ''' <summary>The tools a desktop app offers (for the Tools menu in other windows).</summary>
        Private Async Function ChatDeviceCatalogGet(rc As RequestContext) As Task
            RequireChat()
            Dim catalog = _h.Chat.DeviceCatalog(rc.User.Name, rc.Route("device"))
            If catalog Is Nothing Then Throw ApiException.NotFound("DEVICE_NOT_FOUND", "This desktop app has not connected since the server started.")
            Await rc.OkAsync(catalog).ConfigureAwait(False)
        End Function

        Private Async Function ChatLiveSnapshot(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Await rc.OkAsync(_h.Chat.Live.Snapshot(rc.User.Name, c.Id)).ConfigureAwait(False)
        End Function

        ''' <summary>Answers a tool approval: {id, choice: once | always | deny}. Any of the user's windows may answer.</summary>
        Private Async Function ChatApprove(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            ' arguments: the call as the user changed it while allowing it (an edit with only some of its changes accepted).
            Dim edited = JsonUtil.GetString(b, "arguments")
            If edited IsNot Nothing Then
                Dim parsed As JsonElement
                If edited.Length > 2_000_000 OrElse Not JsonUtil.TryParseElement(edited, parsed) OrElse parsed.ValueKind <> JsonValueKind.Object Then Throw ApiException.BadRequest("arguments must be a JSON object.")
            End If
            If Not _h.Chat.Live.ResolveApproval(rc.User.Name, c.Id, JsonUtil.GetString(b, "id"), JsonUtil.GetString(b, "choice"), edited) Then
                Throw ApiException.NotFound("NO_PENDING_APPROVAL", "This approval was already answered or has expired.")
            End If
            Await rc.OkAsync(New With {.accepted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' A client asks the user before running one of its own tools (the CLI's permission prompt): {id, name, arguments, reason,
        ''' where}. Every window of the user shows it like the server's own approvals; the response waits for the first answer
        ''' (from a window, or from the client itself through POST …/approvals) and returns {choice: once | always | deny | timeout}.
        ''' </summary>
        Private Async Function ChatApprovalRequest(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim id = TextUtil.Truncate(JsonUtil.GetString(b, "id"), 80)
            If String.IsNullOrWhiteSpace(id) Then Throw ApiException.BadRequest("id is required.")
            Dim user = rc.User.Name
            Dim live = _h.Chat.Live
            Dim info = New With {.id = id, .chatId = c.Id, .name = TextUtil.Truncate(JsonUtil.GetString(b, "name"), 120),
                                 .arguments = TextUtil.Truncate(JsonUtil.GetString(b, "arguments"), 20000), .reason = TextUtil.Truncate(JsonUtil.GetString(b, "reason"), 300),
                                 .server = "client", .where = TextUtil.Truncate(JsonUtil.GetString(b, "where"), 80), .requestedUtc = DateTime.UtcNow}
            Dim waiter = live.WaitForApproval(user, c.Id, id, info)
            Dim choice = "timeout"
            Try
                live.RecordChat(user, c.Id, "approval", info)
                Dim wait = Math.Clamp(JsonUtil.GetInt(b, "timeoutSeconds", 600), 10, 3600)
                Dim finished = Await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(wait), rc.CancellationToken)).ConfigureAwait(False)
                If finished Is waiter Then choice = Await waiter.ConfigureAwait(False)
            Catch ex As OperationCanceledException
                ' The client went away (it answered by itself, or closed).
            Finally
                live.ForgetApproval(user, c.Id, id)
                live.RecordChat(user, c.Id, "approval_resolved", New With {.id = id, .choice = choice})
            End Try
            ' arguments: the user accepted only some of an edit's changes; the client runs the call with these instead.
            Dim changed = live.TakeApprovedArguments(user, c.Id, id)
            If Not rc.CancellationToken.IsCancellationRequested Then Await rc.OkAsync(New With {.choice = choice, .arguments = changed}).ConfigureAwait(False)
        End Function

        Private Async Function ChatContext(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Await rc.OkAsync(Await _h.Chat.ContextStatsAsync(rc.User.Name, c.Id, rc.QueryString("model"), c.LastPromptTokens).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Summarizes the older part of the conversation (the last exchange stays). A big summary can take minutes, so the reply
        ''' is a server-sent-event stream with a comment ping every 10 s (Cloudflare and other proxies close a response silent for
        ''' ~100 s); the result comes as a final "done" event, or "error". Clients read it like the message stream.
        ''' </summary>
        Private Async Function ChatCompact(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            If _h.Chat.Live.IsLive(c.Id) Then Throw New ApiException(409, "CHAT_BUSY", "Wait until the answer is finished.")
            ' {instructions}: what the summary should focus on (the CLI's /compact <instructions>).
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim focus = TextUtil.Truncate(JsonUtil.GetString(b, "instructions"), 2000)
            rc.BeginStream("text/event-stream; charset=utf-8")
            Await rc.WriteStreamAsync(": start" & vbLf & vbLf).ConfigureAwait(False)
            Dim work = _h.Chat.CompactAsync(rc.User.Name, c.Id, 2, rc.CancellationToken, keepTokens:=1, focus:=focus)
            Dim failure As String = Nothing
            Dim payload As Object = Nothing
            Try
                While Not work.IsCompleted
                    If Await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(10), rc.CancellationToken)).ConfigureAwait(False) IsNot work Then
                        Await rc.WriteStreamAsync(": ping" & vbLf & vbLf).ConfigureAwait(False)
                    End If
                End While
                Dim r = Await work.ConfigureAwait(False)
                Dim cstats = New With {.beforeTokens = r.BeforeTokens, .afterTokens = r.AfterTokens, .beforeMessages = r.BeforeMessages, .afterMessages = r.AfterMessages}
                payload = New With {.compacted = r.Ok, .message = r.Message, .stats = cstats,
                                    .context = Await _h.Chat.ContextStatsAsync(rc.User.Name, c.Id, Nothing, 0, cstats).ConfigureAwait(False),
                                    .conversation = Await _h.Chat.GetAsync(rc.User.Name, c.Id, False).ConfigureAwait(False)}
            Catch ex As Exception When Not rc.CancellationToken.IsCancellationRequested
                failure = "Compaction failed: " & ex.Message
            End Try
            If payload IsNot Nothing Then
                Await rc.WriteStreamAsync("event: done" & vbLf & "data: " & JsonUtil.ToJson(payload) & vbLf & vbLf).ConfigureAwait(False)
            ElseIf failure IsNot Nothing Then
                Await rc.WriteStreamAsync("event: error" & vbLf & "data: " & JsonUtil.ToJson(New With {.message = failure}) & vbLf & vbLf).ConfigureAwait(False)
            End If
        End Function

        ''' <summary>The result of a tool that ran on the user's computer, for an answer that is waiting for it.</summary>
        ''' <summary>A tool result's body may carry five pictures of 8 MB (base64) and the text.</summary>
        Private Const MaxToolResultBytes As Long = 60L * 1024 * 1024

        Private Async Function ChatToolResult(rc As RequestContext) As Task
            RequireChat()
            Await ChatOrThrow(rc).ConfigureAwait(False)
            ' Pictures make this body larger than other requests (a screenshot is often over 1 MB as base64): its own limit.
            Dim raw = Await rc.ReadBytesAsync(MaxToolResultBytes).ConfigureAwait(False)
            Dim b As JsonElement
            If Not JsonUtil.TryParseElement(If(raw.Length = 0, "{}", Text.Encoding.UTF8.GetString(raw)), b) OrElse b.ValueKind <> JsonValueKind.Object Then
                Throw ApiException.BadRequest("Request body must be a JSON object.", "INVALID_JSON")
            End If
            Dim okEl As JsonElement
            Dim ok = b.TryGetProperty("ok", okEl) AndAlso okEl.ValueKind = JsonValueKind.True
            ' {image: {mediaType, data}}: a picture the tool made (a screenshot from the desktop app), at most 8 MB.
            ' With name and show false it is a picture file the tool read: given to the model, not added to the answer.
            ' {images: [{mediaType, data, show}]}: pictures an MCP tool on the user's computer returned (at most 4).
            Dim readPicture = Function(el As JsonElement, what As String) As (Mime As String, Data As Byte(), Name As String, Show As Boolean)
                                  Dim m = JsonUtil.GetString(el, "mediaType")
                                  Dim n = IO.Path.GetFileName(If(JsonUtil.GetString(el, "name"), "").Trim())
                                  n = If(String.IsNullOrWhiteSpace(n), Nothing, TextUtil.Truncate(n, 120))
                                  Dim showEl As JsonElement
                                  Dim show = Not (el.TryGetProperty("show", showEl) AndAlso showEl.ValueKind = JsonValueKind.False)
                                  If m Is Nothing OrElse Not {"image/png", "image/jpeg", "image/webp", "image/gif"}.Contains(m.ToLowerInvariant()) Then Throw ApiException.BadRequest(what & ".mediaType must be image/png, image/jpeg, image/webp or image/gif.")
                                  Dim data As Byte()
                                  Try
                                      data = Convert.FromBase64String(If(JsonUtil.GetString(el, "data"), ""))
                                  Catch ex As FormatException
                                      Throw ApiException.BadRequest(what & ".data is not base64.")
                                  End Try
                                  If data.Length > 8 * 1024 * 1024 Then Throw ApiException.BadRequest("The picture is larger than 8 MB.")
                                  Return (m.ToLowerInvariant(), data, n, show)
                              End Function
            Dim imageEl As JsonElement, image As Byte() = Nothing, mime As String = Nothing, imageName As String = Nothing, showImage = True
            If b.TryGetProperty("image", imageEl) AndAlso imageEl.ValueKind = JsonValueKind.Object Then
                Dim p = readPicture(imageEl, "image")
                mime = p.Mime : image = p.Data : imageName = p.Name : showImage = p.Show
            End If
            Dim more As New List(Of (Mime As String, Data As Byte(), Name As String, Show As Boolean))
            Dim imagesEl As JsonElement
            If b.TryGetProperty("images", imagesEl) AndAlso imagesEl.ValueKind = JsonValueKind.Array Then
                For Each el In imagesEl.EnumerateArray().Take(4)
                    If el.ValueKind = JsonValueKind.Object Then more.Add(readPicture(el, "images[]"))
                Next
            End If
            If Not _h.Chat.CompleteClientTool(rc.User.Name, JsonUtil.GetString(b, "id"), ok, JsonUtil.GetString(b, "result"), mime, image, imageName, showImage, more) Then
                Throw ApiException.NotFound("NO_PENDING_CALL", "No answer is waiting for this tool call.")
            End If
            Await rc.OkAsync(New With {.accepted = True}).ConfigureAwait(False)
        End Function

        Private Async Function ChatRunCode(rc As RequestContext) As Task
            RequireChat()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim result As CodeRunResult = Nothing
            Dim problem As String = Nothing
            Try
                result = Await _h.Chat.RunCodeAsync(JsonUtil.GetString(b, "language"), JsonUtil.GetString(b, "code"), rc.User.IsAdmin, rc.CancellationToken).ConfigureAwait(False)
            Catch ex As Exception When TypeOf ex Is ArgumentException OrElse TypeOf ex Is InvalidOperationException OrElse TypeOf ex Is Mcp.McpUnavailableException
                problem = ex.Message
            End Try
            If problem IsNot Nothing Then Throw ApiException.BadRequest(problem)
            Await AuditAsync(rc, "run-code", "chat", result.Language, New With {result.Runner, result.ExitCode}).ConfigureAwait(False)
            Await rc.OkAsync(result).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ favicons for web sources
        Private Shared ReadOnly FaviconHttp As New Net.Http.HttpClient With {.Timeout = TimeSpan.FromSeconds(6)}
        Private Shared ReadOnly FaviconCache As New Concurrent.ConcurrentDictionary(Of String, (Data As Byte(), Type As String))(StringComparer.OrdinalIgnoreCase)

        ''' <summary>
        ''' A site's icon for the Chat tab's source list (the console's CSP only allows its own images). Icons come from one fixed
        ''' service (Google's favicon service), never from the site itself, so this cannot be used to reach arbitrary addresses.
        ''' </summary>
        Private Async Function Favicon(rc As RequestContext) As Task
            Dim host = If(rc.Route("host"), "").Trim().ToLowerInvariant()
            If Not Text.RegularExpressions.Regex.IsMatch(host, "^(?=.{3,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$") Then Throw ApiException.BadRequest("Not a host name.")
            Dim hit As (Data As Byte(), Type As String) = (Nothing, Nothing)
            If Not FaviconCache.TryGetValue(host, hit) Then
                Try
                    Using resp = Await FaviconHttp.GetAsync("https://www.google.com/s2/favicons?sz=64&domain=" & Uri.EscapeDataString(host), rc.CancellationToken).ConfigureAwait(False)
                        Dim type = If(resp.Content.Headers.ContentType?.MediaType, "")
                        If resp.IsSuccessStatusCode AndAlso type.StartsWith("image/", StringComparison.Ordinal) Then
                            Dim data = Await resp.Content.ReadAsByteArrayAsync(rc.CancellationToken).ConfigureAwait(False)
                            If data.Length > 0 AndAlso data.Length < 200000 Then hit = (data, type)
                        End If
                    End Using
                Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                End Try
                If FaviconCache.Count > 5000 Then FaviconCache.Clear()
                FaviconCache(host) = hit
            End If
            If hit.Data Is Nothing Then Throw ApiException.NotFound("NO_ICON", "No icon for this site.")
            Await rc.WriteBytesAsync(200, hit.Type, hit.Data, New Dictionary(Of String, String) From {{"Cache-Control", "private, max-age=604800"}}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
