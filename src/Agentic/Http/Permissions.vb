Option Strict On
Option Explicit On
Option Infer On

Namespace Http

    Public NotInheritable Class PermissionInfo
        ''' <summary>Where the users editor lists it: Chat, API &amp; apps, Workforce, Money, Administration.</summary>
        Public Property Group As String
        Public Property Key As String
        Public Property Label As String
        Public Property Description As String
    End Class

    ''' <summary>
    ''' Dashboard permissions. Admins have all of them (plus user management); users have the ones an admin granted, limited
    ''' to their allowed agents. Every API route maps to one permission here (unmapped changes are admin-only).
    ''' </summary>
    Public NotInheritable Class Permissions
        Public Const AdminOnly As String = "admin"
        ''' <summary>Actions on the signed-in user's own account (logout, password change).</summary>
        Public Const Self As String = "self"
        ''' <summary>Seeing the Workforce at all: agents, subagents, tasks, global tasks, learning, communications, the inbox.</summary>
        Public Const ViewWorkforce As String = "agents.view"

        ''' <summary>
        ''' Permissions that include viewing the Workforce: each acts on agents or their work. A user with none of them sees no
        ''' agents anywhere (the Workforce pages are gone, and other pages leave agents out).
        ''' </summary>
        Public Shared ReadOnly WorkforcePermissions As String() = {
            ViewWorkforce, "agents.control", "tasks.manage", "global.tasks", "results", "agents.message", "agents.memory", "agents.manage",
            "approvals", "finance.view", "finance.manage", "learning.manage", "operations"}

        Public Const GroupChat As String = "Chat"
        Public Const GroupApps As String = "API & apps"
        Public Const GroupWorkforce As String = "Workforce"
        Public Const GroupMoney As String = "Money"
        Public Const GroupAdmin As String = "Administration"

        Public Shared ReadOnly Catalog As PermissionInfo() = {
            P(GroupChat, "models.use", "Chat", "Use the Chat tab with the models the account may use (below)."),
            P(GroupChat, "chat.tools", "MCP tools", "Let chat models use MCP servers - shell, Git, browser … - limited to the servers chosen below."),
            P(GroupChat, "chat.code", "Code interpreter & sandbox", "Run Python and Node in the code sandbox from chat, run code blocks, browse and download sandbox files, open running apps (previews)."),
            P(GroupChat, "chat.web", "Web search", "Let chat models search the web and read web pages; attach webpages to messages."),
            P(GroupChat, "chat.uploads", "Attach files", "Upload files, images and documents to chats."),
            P(GroupChat, "chat.images", "Image generation", "Create and edit pictures in chat and through the API (paid per picture)."),
            P(GroupChat, "chat.video", "Video generation", "Create short video clips in chat (paid per clip)."),
            P(GroupChat, "chat.knowledge", "Knowledge bases", "Create knowledge bases and let chat answer from them (RAG)."),
            P(GroupChat, "chat.memory", "Memory", "The assistant remembers facts about the user across chats (and the user can manage them)."),
            P(GroupChat, "chat.share", "Share chats", "Create public read-only links to conversations."),
            P(GroupChat, "images.sd", "Images (Stable Diffusion)", "The Images tab: make pictures with the Stable Diffusion server with every setting (img2img, inpainting, LoRAs, ControlNet, hires fix) and keep them in a gallery."),
            P(GroupChat, "music.suno", "Music (Suno)", "The Music tab: make songs with Suno (described, or with custom lyrics, style and title; instrumentals; AI-written lyrics) and keep them in a library. Uses the server's Suno account."),
            P(GroupChat, "chat.voice", "Voice", "Hear answers in the speech server's voices, talk with the chat in Dictation mode, and use the Audio tab: make speech with every setting and clone voices."),
            P(GroupChat, "chat.devices", "Tools on own computers", "Let chat models read, edit and run things on the user's own computer through the desktop app, VS Code or the CLI."),
            P(GroupChat, "models.own", "Own models", "Add models with their own provider API keys (My models), used only by this account."),
            P(GroupApps, "api.use", "Model API", "Use the OpenAI- and Anthropic-compatible API (/v1) with the models the account may use."),
            P(GroupApps, "account.apikey", "Personal API key", "Create and use a personal API key: needed by the desktop app, VS Code, the CLI and programs using the API."),
            P(GroupWorkforce, ViewWorkforce, "View workforce", "See the Workforce: agents, subagents, learning, global tasks, tasks, communications and the inbox (only the agents chosen below). Every permission that acts on agents includes it; without any of them the user sees no agents."),
            P(GroupWorkforce, "agents.control", "Control agents", "Start, stop, pause, resume, restart and run agents now; cancel running work and subagents."),
            P(GroupWorkforce, "tasks.manage", "Manage tasks", "Add, edit, reorder, hold, release, retry and cancel tasks, and manage recurring tasks."),
            P(GroupWorkforce, "global.tasks", "Global tasks", "Create global tasks the system plans and distributes across agents; approve plans, re-plan, cancel and schedule them."),
            P(GroupWorkforce, "results", "Results", "View and download delivered task results (answers, files, conversations), rate them and archive them."),
            P(GroupWorkforce, "agents.message", "Message agents", "Send messages to agents and cancel pending messages."),
            P(GroupWorkforce, "agents.memory", "Agent memory", "Read and delete agents' private memories."),
            P(GroupWorkforce, "agents.manage", "Create & edit agents", "Create, edit, clone and delete agents and grant them model access (only users with access to all agents can create agents; only the MCP servers allowed below)."),
            P(GroupWorkforce, "approvals", "Answer the inbox", "Answer agents' questions and approve or reject their requests."),
            P(GroupWorkforce, "learning.manage", "Manage learning", "Start and re-run case studies; approve, retract, pin and edit lessons."),
            P(GroupMoney, "finance.view", "View finance", "Wallets, ledgers, spending and model prices."),
            P(GroupMoney, "finance.manage", "Manage money", "Record profit, adjust balances, set daily limits and model prices."),
            P(GroupAdmin, "models.manage", "Manage models", "Register custom models, reset circuit breakers and run AI checks."),
            P(GroupAdmin, "operations", "Global operations", "Pause or resume all agents, emergency stop, reconnect MCP servers, test searches."),
            P(GroupAdmin, "system.view", "View system", "Configuration, audit log, MCP servers and metrics.")}

        Private Shared Function P(group As String, key As String, label As String, description As String) As PermissionInfo
            Return New PermissionInfo With {.Group = group, .Key = key, .Label = label, .Description = description}
        End Function

        Public Shared Function IsKnown(key As String) As Boolean
            Return Catalog.Any(Function(c) c.Key = key)
        End Function

        ''' <summary>The permission a route needs. Nothing = viewing only (allowed to every signed-in user, scoped to their agents).</summary>
        Public Shared Function ForRoute(method As String, template As String) As String
            Dim t = template.ToLowerInvariant().TrimEnd("/"c)
            Dim m = method.ToUpperInvariant()
            If t.StartsWith("/api/users", StringComparison.Ordinal) Then Return AdminOnly
            If t.StartsWith("/api/model-control", StringComparison.Ordinal) Then Return AdminOnly
            ' The vault, mailboxes (they use vault passwords) and the autonomy loop are for administrators only.
            If t.StartsWith("/api/secrets", StringComparison.Ordinal) OrElse t.StartsWith("/api/mailboxes", StringComparison.Ordinal) OrElse
               t.StartsWith("/api/autonomy", StringComparison.Ordinal) Then Return AdminOnly
            ' An agent's evaluation suite: everyone who sees the agent sees it; changing or running it is part of managing agents.
            If t.StartsWith("/api/agents/{id}/evals", StringComparison.Ordinal) AndAlso m <> "GET" Then Return "agents.manage"
            If t = "/api/auth/logout" OrElse t = "/api/auth/password" OrElse t = "/api/auth/me" OrElse t = "/api/auth/login" Then Return Self

            ' A personal API key: creating one needs "Personal API key"; seeing and revoking your own does not.
            If t = "/api/auth/api-key" Then Return If(m = "POST", "account.apikey", Self)
            If t = "/api/openapi.json" Then Return Self
            If t.StartsWith("/api/account", StringComparison.Ordinal) Then Return Self
            ' The usage graph: everyone sees their own use; administrators see every user and agent.
            If t = "/api/usage/timeline" Then Return Self
            ' Client apps check for and download updates with any account; the install list is for admins.
            If t.StartsWith("/api/updates", StringComparison.Ordinal) Then Return If(t = "/api/updates/installs", "system.view", Self)
            If t.StartsWith("/v1/", StringComparison.Ordinal) OrElse t.StartsWith("/api/v1/", StringComparison.Ordinal) Then Return "api.use"
            If t.StartsWith("/v2/", StringComparison.Ordinal) OrElse t.StartsWith("/api/v2/", StringComparison.Ordinal) Then Return "api.use"
            ' Speech: the speech server's key is the administrator's; everything else is "Voice".
            If t = "/api/audio/key" Then Return AdminOnly
            ' Speech prices are set like model prices (Manage money) and seen by everyone who uses speech or sees finance.
            If t = "/api/audio/pricing" Then Return If(m = "GET", "finance.view|chat.voice", "finance.manage")
            If t = "/api/audio" OrElse t.StartsWith("/api/audio/", StringComparison.Ordinal) Then Return "chat.voice"
            ' Pictures (the Images tab), likewise.
            If t = "/api/images/key" Then Return AdminOnly
            If t = "/api/images/pricing" Then Return If(m = "GET", "finance.view|images.sd", "finance.manage")
            If t = "/api/images" OrElse t.StartsWith("/api/images/", StringComparison.Ordinal) Then Return "images.sd"
            ' Songs (the Music tab), likewise; the Suno account's own library is the administrator's.
            If t = "/api/music/key" OrElse t = "/api/music/suno" OrElse t = "/api/music/import" Then Return AdminOnly
            If t = "/api/music/pricing" Then Return If(m = "GET", "finance.view|music.suno", "finance.manage")
            If t = "/api/music" OrElse t.StartsWith("/api/music/", StringComparison.Ordinal) Then Return "music.suno"
            ' Prices and news (the Money, Stocks and News tabs): every account may look. Money's defaults are the administrator's;
            ' each account keeps its own Money and Stocks watch lists.
            If t = "/api/markets/defaults" Then Return AdminOnly
            If t = "/api/markets" OrElse t.StartsWith("/api/markets/", StringComparison.Ordinal) OrElse t = "/api/news" OrElse t.StartsWith("/api/news/", StringComparison.Ordinal) OrElse t = "/api/stocks" OrElse t.StartsWith("/api/stocks/", StringComparison.Ordinal) OrElse t = "/api/polymarket" OrElse t.StartsWith("/api/polymarket/", StringComparison.Ordinal) Then Return Self
            ' Chat features with their own permission (the rest of the Chat tab is "Chat").
            If t = "/api/chat/run" OrElse t.StartsWith("/api/chat/sandbox", StringComparison.Ordinal) OrElse
               t.StartsWith("/api/sandbox/apps", StringComparison.Ordinal) OrElse t.StartsWith("/api/sandbox/previews", StringComparison.Ordinal) Then Return "chat.code"
            If t.StartsWith("/api/chat/knowledge", StringComparison.Ordinal) Then Return "chat.knowledge"
            ' MCP servers added from the web app (their own, or for everyone by an administrator).
            If t.StartsWith("/api/chat/mcp-servers", StringComparison.Ordinal) Then Return "chat.tools"
            If t = "/api/chat/fetch-page" Then Return "chat.web"
            If t.StartsWith("/api/chat/memories", StringComparison.Ordinal) Then Return "chat.memory"
            If t = "/api/chat/conversations/{chatid}/share" Then Return If(m = "POST", "chat.share", "models.use")   ' anyone can stop sharing
            If t.StartsWith("/api/chat/my-models", StringComparison.Ordinal) Then Return "models.own"
            ' Public links to artifacts: making one needs "Share chats"; listing them and stopping one does not.
            If t = "/api/chat/artifacts/share" OrElse t = "/api/chat/artifacts/share-site" Then Return "chat.share"
            If t.StartsWith("/api/chat/", StringComparison.Ordinal) Then Return "models.use"
            If t.StartsWith("/api/custom-models", StringComparison.Ordinal) Then Return If(m = "GET" OrElse t.EndsWith("/test", StringComparison.Ordinal), "models.use", AdminOnly)
            If t.StartsWith("/api/results", StringComparison.Ordinal) Then Return If(t.EndsWith("/award", StringComparison.Ordinal), "finance.manage", "results")
            If t = "/api/uploads" AndAlso m <> "GET" Then Return "tasks.manage|chat.uploads"
            If m = "GET" Then
                Select Case t
                    Case "/api/config/validate", "/api/config/summary", "/api/audit", "/api/metrics", "/api/mcp", "/api/websearch", "/api/ai/available-models"
                        Return "system.view"
                    Case "/api/finance", "/api/finance/chat-usage", "/api/finance/ledger", "/api/pricing", "/api/agents/{id}/ledger"
                        Return "finance.view"
                    Case "/api/agents/{id}/memory"
                        Return "agents.memory"
                    Case "/api/meta/agent-form", "/api/agents/{id}/definition"
                        Return "agents.manage"
                End Select
                Return If(IsWorkforceRead(t), ViewWorkforce, Nothing)
            End If

            Select Case t
                Case "/api/agents/{id}/start", "/api/agents/{id}/stop", "/api/agents/{id}/pause", "/api/agents/{id}/resume", "/api/agents/{id}/restart", "/api/agents/{id}/revive",
                     "/api/agents/{id}/run-now", "/api/agents/{id}/cancel-task", "/api/agents/{id}/subagents/{subagentid}/cancel"
                    Return "agents.control"
                Case "/api/agents/{id}/task"
                    Return "tasks.manage"
                Case "/api/agents/{id}/message", "/api/messages/{messageid}/cancel"
                    Return "agents.message"
                Case "/api/agents/{id}/memory/{memoryid}"
                    Return "agents.memory"
                Case "/api/agents/{id}/profit", "/api/agents/{id}/adjust", "/api/agents/{id}/daily-limit", "/api/pricing/{key}"
                    Return "finance.manage"
                Case "/api/agents", "/api/agents/{id}", "/api/agents/{id}/model-grants/{key}"
                    Return "agents.manage"
                Case "/api/ai/check"
                    Return "models.manage"
                Case "/api/websearch/test"
                    Return "operations"
            End Select
            If t.StartsWith("/api/agents/{id}/tasks", StringComparison.Ordinal) OrElse t.StartsWith("/api/agents/{id}/schedules", StringComparison.Ordinal) Then Return "tasks.manage"
            If t.StartsWith("/api/global-tasks", StringComparison.Ordinal) OrElse t.StartsWith("/api/global-schedules", StringComparison.Ordinal) Then Return "global.tasks"
            If t.StartsWith("/api/models", StringComparison.Ordinal) Then Return "models.manage"
            If t.StartsWith("/api/approvals", StringComparison.Ordinal) OrElse t.StartsWith("/api/approval-rules", StringComparison.Ordinal) Then Return "approvals"
            If t.StartsWith("/api/learning", StringComparison.Ordinal) Then Return "learning.manage"
            If t.StartsWith("/api/operations", StringComparison.Ordinal) OrElse t.StartsWith("/api/mcp", StringComparison.Ordinal) Then Return "operations"
            Return AdminOnly
        End Function

        ''' <summary>
        ''' Reading the Workforce and its dashboards: agents (and everything under an agent), subagents, tasks, global tasks,
        ''' learning, communications, the inbox, and the Monitor pages (overview, operations, events, AI calls).
        ''' </summary>
        Private Shared Function IsWorkforceRead(t As String) As Boolean
            Select Case t
                Case "/api/agents", "/api/subagents", "/api/tasks", "/api/approvals", "/api/insights", "/api/operations", "/api/stats/hourly", "/api/events/recent", "/api/ai/calls"
                    Return True
            End Select
            Return t.StartsWith("/api/agents/", StringComparison.Ordinal) OrElse t.StartsWith("/api/communications", StringComparison.Ordinal) OrElse
                   t.StartsWith("/api/correlations/", StringComparison.Ordinal) OrElse t.StartsWith("/api/global-tasks", StringComparison.Ordinal) OrElse
                   t.StartsWith("/api/learning/", StringComparison.Ordinal) OrElse t.StartsWith("/api/artifacts/", StringComparison.Ordinal)
        End Function
    End Class

End Namespace
