Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Text
Imports System.Text.Json
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: what the chat in every app (web, desktop app, VS Code, CLI) needs around the conversation - a shell command
    ' the user ran with ! kept in the chat, the user's earlier prompts (Ctrl+R), an earlier version of an answer read to
    ' compare it side by side, the account's usage (/usage) and feedback for the administrators (/feedback).
    Partial Friend NotInheritable Class AdminApi

        Private Const FeedbackMaxLogChars As Integer = 200_000
        Private Const FeedbackMaxChatChars As Integer = 400_000

        Private Sub RegisterChatNav(r As Router)
            r.Map("POST", "/api/chat/conversations/{chatId}/shell", AuthLevel.SubmitTask, AddressOf ChatShellRecord)
            r.Map("GET", "/api/chat/prompt-history", AuthLevel.Read, AddressOf ChatPromptHistory)
            r.Map("GET", "/api/chat/conversations/{chatId}/versions/{branchId}", AuthLevel.Read, AddressOf ChatReadVersion)
            r.Map("GET", "/api/chat/usage", AuthLevel.Read, AddressOf ChatUsage)
            r.Map("POST", "/api/chat/feedback", AuthLevel.SubmitTask, AddressOf ChatFeedbackReport)
            r.Map("GET", "/api/chat/feedback", AuthLevel.Read, AddressOf ChatFeedbackList)
        End Sub

        ''' <summary>Body {command, output, exitCode, where}: a command the user ran (! in the message box), kept as a message of the chat.</summary>
        Private Async Function ChatShellRecord(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim command = JsonUtil.GetString(b, "command")
            If String.IsNullOrWhiteSpace(command) Then Throw ApiException.BadRequest("command is required.")
            Dim exitEl As JsonElement
            Dim exitCode As Integer? = Nothing
            If b.TryGetProperty("exitCode", exitEl) AndAlso exitEl.ValueKind = JsonValueKind.Number Then exitCode = exitEl.GetInt32()
            Dim m As ChatMessageRecord
            Try
                m = Await _h.Chat.SaveShellAsync(rc.User.Name, c.Id, TextUtil.Truncate(command, 4000), JsonUtil.GetString(b, "output"), exitCode, JsonUtil.GetString(b, "where")).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw New ApiException(409, "CHAT_BUSY", ex.Message)
            End Try
            Await rc.OkAsync(m).ConfigureAwait(False)
        End Function

        ''' <summary>?q=&amp;limit=: the user's earlier prompts across all chats, newest first, each text once.</summary>
        Private Async Function ChatPromptHistory(rc As RequestContext) As Task
            RequireChat()
            Await rc.OkAsync(Await _h.Chat.PromptHistoryAsync(rc.User.Name, rc.QueryString("q"), rc.QueryInt("limit", 50, 1, 200)).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>An earlier version's messages (from its point on), to show beside the current one.</summary>
        Private Async Function ChatReadVersion(rc As RequestContext) As Task
            RequireChat()
            Dim c = Await ChatOrThrow(rc).ConfigureAwait(False)
            Try
                Await rc.OkAsync(Await _h.Chat.ReadVersionAsync(rc.User.Name, c.Id, rc.Route("branchId")).ConfigureAwait(False)).ConfigureAwait(False)
            Catch ex As KeyNotFoundException
                Throw ApiException.NotFound("VERSION_NOT_FOUND", ex.Message)
            End Try
        End Function

        ' ------------------------------------------------------------------ /usage

        Private Class UsageRow
            Public Property Model As String
            Public Property Input As Long
            Public Property Output As Long
            Public Property Calls As Long
        End Class

        Private Async Function UsageByModelAsync(username As String, sinceUtc As DateTime) As Task(Of List(Of UsageRow))
            Return Await _h.Database.QueryAsync(
                "SELECT COALESCE(NULLIF(RoutedModel, ''), Model) AS M, SUM(PromptTokens) AS P, SUM(CompletionTokens) AS C, COUNT(*) AS N " &
                "FROM ModelApiUsage WHERE Username=@u AND CreatedUtc>=@s GROUP BY M ORDER BY SUM(PromptTokens + CompletionTokens) DESC",
                Function(r) New UsageRow With {.Model = Db.Str(r, "M"), .Input = Db.Lng(r, "P"), .Output = Db.Lng(r, "C"), .Calls = Db.Lng(r, "N")},
                ("@u", CObj(username)), ("@s", Db.Ts(sinceUtc))).ConfigureAwait(False)
        End Function

        Private Shared Function Totals(rows As List(Of UsageRow), Optional costMicros As Long = 0) As Object
            Return New With {.input = rows.Sum(Function(x) x.Input), .output = rows.Sum(Function(x) x.Output), .calls = rows.Sum(Function(x) x.Calls), .costMicros = costMicros}
        End Function

        ''' <summary>
        ''' This account's use of the models (/usage in every app): tokens and calls today, this month and over 30 days, by
        ''' model and by day; what it cost and the balance (accounts that pay); and the limits an administrator set, with how
        ''' much of today's is used and when it resets.
        ''' </summary>
        Private Async Function ChatUsage(rc As RequestContext) As Task
            Dim name = rc.User.Name
            Dim now = DateTime.UtcNow
            Dim today = now.Date
            Dim month = New DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            Dim days30 = today.AddDays(-29)
            Dim todayRows = Await UsageByModelAsync(name, today).ConfigureAwait(False)
            Dim monthRows = Await UsageByModelAsync(name, month).ConfigureAwait(False)
            Dim rows30 = Await UsageByModelAsync(name, days30).ConfigureAwait(False)
            Dim daily = Await _h.Database.QueryAsync(
                "SELECT substr(CreatedUtc, 1, 10) AS D, SUM(PromptTokens) AS P, SUM(CompletionTokens) AS C, COUNT(*) AS N FROM ModelApiUsage " &
                "WHERE Username=@u AND CreatedUtc>=@s GROUP BY D ORDER BY D",
                Function(r) New With {.day = Db.Str(r, "D"), .input = Db.Lng(r, "P"), .output = Db.Lng(r, "C"), .calls = Db.Lng(r, "N")},
                ("@u", CObj(name)), ("@s", Db.Ts(days30))).ConfigureAwait(False)
            ' Money: accounts that pay for their use have a balance and a ledger by model.
            Dim acc = Await _h.Accounts.GetAccountAsync(name).ConfigureAwait(False)
            Dim spent = Function(since As DateTime) As Task(Of Long)
                            Return SpentByAsync(name, since)
                        End Function
            Dim costByModel = If(acc Is Nothing, New List(Of Dictionary(Of String, Object)), Await _h.Accounts.SpendByModelAsync(name, days30).ConfigureAwait(False))
            Dim costOf = Function(model As String) As Long
                             Dim hit = costByModel.FirstOrDefault(Function(d) String.Equals(Convert.ToString(d("Model"), CultureInfo.InvariantCulture), model, StringComparison.OrdinalIgnoreCase))
                             Return If(hit Is Nothing, 0L, Convert.ToInt64(If(hit("Micros"), 0L), CultureInfo.InvariantCulture))
                         End Function
            Dim usedToday = todayRows.Sum(Function(x) x.Input + x.Output)
            Dim u = rc.User
            Await rc.OkAsync(New With {
                .username = name, .nowUtc = now,
                .today = Totals(todayRows, If(acc Is Nothing, 0L, Await spent(today).ConfigureAwait(False))),
                .month = Totals(monthRows, If(acc Is Nothing, 0L, Await spent(month).ConfigureAwait(False))),
                .days30 = Totals(rows30, If(acc Is Nothing, 0L, Await spent(days30).ConfigureAwait(False))),
                .byModel = rows30.Select(Function(x) New With {.model = x.Model, .input = x.Input, .output = x.Output, .calls = x.Calls, .costMicros = costOf(x.Model),
                                                              .todayTokens = todayRows.Where(Function(t) t.Model = x.Model).Sum(Function(t) t.Input + t.Output)}).ToList(),
                .daily = daily,
                .charged = acc IsNot Nothing AndAlso acc.Charged, .balanceMicros = If(acc?.BalanceMicros, 0L), .hasAccount = acc IsNot Nothing,
                .limits = New With {
                    .restricted = u.IsRestrictedUser, .dailyTokens = If(u.IsRestrictedUser, u.DailyTokenLimit, 0L), .requestsPerMinute = If(u.IsRestrictedUser, u.RequestsPerMinute, 0),
                    .usedToday = usedToday, .resetsUtc = today.AddDays(1)}}).ConfigureAwait(False)
        End Function

        Private Async Function SpentByAsync(name As String, since As DateTime) As Task(Of Long)
            Dim all = Await _h.Accounts.SpentSinceAsync(since).ConfigureAwait(False)
            Dim v As Long
            Return If(all.TryGetValue(name, v), v, 0L)
        End Function

        ' ------------------------------------------------------------------ /feedback

        ''' <summary>
        ''' Body {text, chatId, includeChat, logs, app, version, platform}: a report for the administrators. It goes to the
        ''' Inbox (kind Feedback) with a Markdown file attached: what the user wrote, the chat's transcript (when included) and
        ''' the app's logs.
        ''' </summary>
        Private Async Function ChatFeedbackReport(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim text = If(JsonUtil.GetString(b, "text"), "").Trim()
            If text.Length = 0 Then Throw ApiException.BadRequest("Say what happened (text is required).")
            text = TextUtil.Truncate(text, 4000)
            Dim app = TextUtil.Truncate(If(JsonUtil.GetString(b, "app"), "web"), 40)
            Dim version = TextUtil.Truncate(If(JsonUtil.GetString(b, "version"), ""), 40)
            Dim platform = TextUtil.Truncate(If(JsonUtil.GetString(b, "platform"), ""), 80)
            Dim logs = If(JsonUtil.GetString(b, "logs"), "")
            If logs.Length > FeedbackMaxLogChars Then logs = "… (earlier lines left out)" & vbLf & logs.Substring(logs.Length - FeedbackMaxLogChars)
            Dim chatId = JsonUtil.GetString(b, "chatId")
            Dim chat As ChatConversation = Nothing
            If Not String.IsNullOrWhiteSpace(chatId) AndAlso JsonUtil.GetBool(b, "includeChat", True) Then chat = Await _h.Chat.GetAsync(rc.User.Name, chatId).ConfigureAwait(False)
            Dim now = DateTime.UtcNow
            Dim md As New StringBuilder()
            md.Append("# Feedback from ").Append(rc.User.Name).Append(vbLf).Append(vbLf)
            md.Append("- When: ").Append(now.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture)).Append(vbLf)
            md.Append("- App: ").Append(app).Append(If(version.Length > 0, " " & version, "")).Append(If(platform.Length > 0, " (" & platform & ")", "")).Append(vbLf)
            md.Append("- Server: ").Append(GetType(AdminApi).Assembly.GetName().Version?.ToString()).Append(vbLf)
            If chat IsNot Nothing Then md.Append("- Chat: ").Append(chat.Title).Append(" (").Append(chat.Id).Append(", ").Append(chat.Messages.Count).Append(" messages, model ").Append(chat.Model).Append(")").Append(vbLf)
            md.Append(vbLf).Append("## What happened").Append(vbLf).Append(vbLf).Append(text).Append(vbLf)
            If chat IsNot Nothing Then
                Dim transcript = ChatService.ToMarkdown(chat)
                If transcript.Length > FeedbackMaxChatChars Then transcript = "… (the start of the chat is left out)" & vbLf & transcript.Substring(transcript.Length - FeedbackMaxChatChars)
                md.Append(vbLf).Append("## Chat transcript").Append(vbLf).Append(vbLf).Append(transcript).Append(vbLf)
            End If
            If logs.Trim().Length > 0 Then md.Append(vbLf).Append("## App logs").Append(vbLf).Append(vbLf).Append("```").Append(vbLf).Append(logs.Replace("```", "'''")).Append(vbLf).Append("```").Append(vbLf)
            Dim fileName = $"feedback-{System.Text.RegularExpressions.Regex.Replace(rc.User.Name, "[^\w\-]+", "_")}-{now:yyyyMMdd-HHmmss}.md"
            Dim info = Await _h.Uploads.SaveAsync(fileName, Encoding.UTF8.GetBytes(md.ToString()), rc.User.Name).ConfigureAwait(False)
            Dim context = JsonUtil.ToJson(New With {.from = rc.User.Name, .app = app, .version = version, .platform = platform,
                                                    .chatId = chat?.Id, .chatTitle = chat?.Title, .fileId = info.Id, .fileName = fileName, .bytes = info.SizeBytes})
            Dim firstLine = text.Split({ChrW(10)}, 2)(0).Trim()
            Dim rec = Await _h.Approvals.CreateAsync(FeedbackAgentId(rc.User.Name), Nothing, Nothing, ApprovalKind.Feedback,
                                                     "Feedback from " & rc.User.Name & ": " & TextUtil.Truncate(firstLine, 120, "…"), text, context, Nothing).ConfigureAwait(False)
            Await rc.OkAsync(New With {.id = rec.Id, .fileId = info.Id, .fileName = fileName}).ConfigureAwait(False)
        End Function

        Private Shared Function FeedbackAgentId(username As String) As String
            Return "user:" & username
        End Function

        ''' <summary>The user's own reports and the administrators' replies (newest first).</summary>
        Private Async Function ChatFeedbackList(rc As RequestContext) As Task
            Dim mine = Await _h.Approvals.ListAsync(Nothing, 50, FeedbackAgentId(rc.User.Name)).ConfigureAwait(False)
            Await rc.OkAsync(mine.Where(Function(a) a.Kind = ApprovalKind.Feedback).Select(Function(a) New With {
                .id = a.Id, .text = a.Question, .status = a.Status.ToString(), .createdUtc = a.CreatedUtc, .resolvedUtc = a.ResolvedUtc,
                .reply = a.Response, .by = a.ResolvedBy}).ToList()).ConfigureAwait(False)
        End Function

    End Class

End Namespace
