Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports Agentic.Persistence

Namespace Http

    ' Admin API: one read for the dashboard's charts (overview, operations, live events): throughput and spend over time,
    ' per-agent activity, model latency and reliability, tool use, task and message mix, event rates, wallets, results.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterInsights(r As Router)
            r.Map("GET", "/api/insights", AuthLevel.Read, AddressOf Insights)
        End Sub

        Private Shared Function HourKey(d As DateTime) As String
            Return d.ToString("yyyy-MM-ddTHH:00:00Z", CultureInfo.InvariantCulture)
        End Function

        Private Shared Function Pct(sorted As List(Of Long), p As Double) As Long
            If sorted.Count = 0 Then Return 0
            Return sorted(Math.Clamp(CInt(Math.Ceiling(p * sorted.Count)) - 1, 0, sorted.Count - 1))
        End Function

        Private Async Function Insights(rc As RequestContext) As Task
            Dim hours = rc.QueryInt("hours", 24, 1, 24 * 14)
            Dim now = DateTime.UtcNow
            Dim start = New DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-(hours - 1))
            Dim since = Db.Ts(start)
            Dim store = _h.Database
            Dim visible = Function(agentId As String) Sees(rc, agentId)

            ' ---- hourly totals (Statistics) and per-agent activity
            Dim stats = Await store.QueryAsync(
                "SELECT AgentId, BucketUtc, TasksCompleted, TasksFailed, TasksRetried, AiCalls, AiFailures, AiLatencyMsTotal, ToolCalls, ToolFailures, MessagesSent, " &
                "Delegations, TaskDurationMsTotal, Tokens, CostMicros FROM Statistics WHERE BucketUtc>=@s",
                Function(r) New With {.Agent = Db.Str(r, "AgentId"), .Bucket = Db.Str(r, "BucketUtc"), .Done = Db.Lng(r, "TasksCompleted"), .Failed = Db.Lng(r, "TasksFailed"),
                                      .Retried = Db.Lng(r, "TasksRetried"), .Ai = Db.Lng(r, "AiCalls"), .AiFail = Db.Lng(r, "AiFailures"), .AiMs = Db.Lng(r, "AiLatencyMsTotal"),
                                      .Tools = Db.Lng(r, "ToolCalls"), .ToolFail = Db.Lng(r, "ToolFailures"), .Msgs = Db.Lng(r, "MessagesSent"), .Deleg = Db.Lng(r, "Delegations"),
                                      .TaskMs = Db.Lng(r, "TaskDurationMsTotal"), .Tokens = Db.Lng(r, "Tokens"), .Cost = Db.Lng(r, "CostMicros")},
                ("@s", CObj(since))).ConfigureAwait(False)
            stats = stats.Where(Function(x) visible(x.Agent)).ToList()
            Dim keys = Enumerable.Range(0, hours).Select(Function(i) HourKey(start.AddHours(i))).ToList()
            Dim byHour = stats.GroupBy(Function(x) x.Bucket).ToDictionary(Function(g) g.Key, Function(g) g.ToList(), StringComparer.Ordinal)
            Dim hourly = keys.Select(Function(k)
                                         Dim rows = If(byHour.ContainsKey(k), byHour(k), Nothing)
                                         Dim ai = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Ai))
                                         Dim done = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Done))
                                         Return New With {
                                             .bucketUtc = k, .tasksCompleted = done, .tasksFailed = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Failed)),
                                             .tasksRetried = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Retried)),
                                             .aiCalls = ai, .aiFailures = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.AiFail)),
                                             .avgAiMs = If(ai > 0, rows.Sum(Function(x) x.AiMs) \ ai, 0L),
                                             .toolCalls = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Tools)), .toolFailures = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.ToolFail)),
                                             .messagesSent = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Msgs)), .delegations = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Deleg)),
                                             .avgTaskMs = If(done > 0, rows.Sum(Function(x) x.TaskMs) \ done, 0L),
                                             .tokens = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Tokens)), .costMicros = If(rows Is Nothing, 0L, rows.Sum(Function(x) x.Cost))}
                                     End Function).ToList()
            Dim agentNames = _h.Agents.GetSnapshots(False).ToDictionary(Function(a) a.Id, Function(a) a, StringComparer.OrdinalIgnoreCase)
            Dim byAgent = stats.GroupBy(Function(x) OwnerId(x.Agent), StringComparer.OrdinalIgnoreCase).Select(Function(g)
                                                                                                        Dim perHour = g.GroupBy(Function(x) x.Bucket).ToDictionary(Function(h) h.Key, Function(h) h.Sum(Function(x) x.Ai + x.Done + x.Tools), StringComparer.Ordinal)
                                                                                                        Return New With {
                                                                                                            .agentId = g.Key, .name = If(agentNames.ContainsKey(g.Key), agentNames(g.Key).Name, g.Key),
                                                                                                            .state = If(agentNames.ContainsKey(g.Key), agentNames(g.Key).State, Nothing),
                                                                                                            .tasksCompleted = g.Sum(Function(x) x.Done), .tasksFailed = g.Sum(Function(x) x.Failed),
                                                                                                            .aiCalls = g.Sum(Function(x) x.Ai), .toolCalls = g.Sum(Function(x) x.Tools), .messagesSent = g.Sum(Function(x) x.Msgs),
                                                                                                            .tokens = g.Sum(Function(x) x.Tokens), .costMicros = g.Sum(Function(x) x.Cost),
                                                                                                            .activity = keys.Select(Function(k) If(perHour.ContainsKey(k), perHour(k), 0L)).ToList()}
                                                                                                    End Function).OrderByDescending(Function(a) a.aiCalls + a.tasksCompleted).ToList()
            ' Agents with no activity still appear (an idle row in the heat map).
            For Each a In agentNames.Values.Where(Function(x) visible(x.Id) AndAlso Not byAgent.Any(Function(b) String.Equals(b.agentId, x.Id, StringComparison.OrdinalIgnoreCase)))
                byAgent.Add(New With {.agentId = a.Id, .name = a.Name, .state = a.State, .tasksCompleted = 0L, .tasksFailed = 0L, .aiCalls = 0L, .toolCalls = 0L,
                                      .messagesSent = 0L, .tokens = 0L, .costMicros = 0L, .activity = keys.Select(Function(k) 0L).ToList()})
            Next

            ' ---- daily totals (14 days)
            Dim dayStart = now.Date.AddDays(-13)
            Dim daily = Await store.QueryAsync(
                "SELECT substr(BucketUtc,1,10) AS D, SUM(TasksCompleted) TC, SUM(TasksFailed) TF, SUM(AiCalls) AI, SUM(Tokens) TOK, SUM(CostMicros) COST " &
                "FROM Statistics WHERE BucketUtc>=@s GROUP BY substr(BucketUtc,1,10) ORDER BY D",
                Function(r) New With {.day = Db.Str(r, "D"), .tasksCompleted = Db.Lng(r, "TC"), .tasksFailed = Db.Lng(r, "TF"), .aiCalls = Db.Lng(r, "AI"),
                                      .tokens = Db.Lng(r, "TOK"), .costMicros = Db.Lng(r, "COST")}, ("@s", CObj(Db.Ts(dayStart)))).ConfigureAwait(False)
            Dim dailyFull = Enumerable.Range(0, 14).Select(Function(i)
                                                               Dim d = dayStart.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                                               Dim hit = daily.FirstOrDefault(Function(x) x.day = d)
                                                               Return If(hit, New With {.day = d, .tasksCompleted = 0L, .tasksFailed = 0L, .aiCalls = 0L, .tokens = 0L, .costMicros = 0L})
                                                           End Function).ToList()

            ' ---- models: calls, failures, latency percentiles, queue wait, spend
            Dim calls = Await store.QueryAsync("SELECT AgentId, ModelKey, Reason, Purpose, Success, DurationMs, QueueWaitMs, PromptTokens, CompletionTokens, CostMicros FROM AiCalls WHERE CreatedUtc>=@s LIMIT 50000",
                Function(r) New With {.Agent = Db.Str(r, "AgentId"), .Model = Db.Str(r, "ModelKey"), .Reason = Db.Str(r, "Reason"), .Purpose = Db.Str(r, "Purpose"),
                                      .Ok = Db.Bool(r, "Success"), .Ms = Db.Lng(r, "DurationMs"), .Wait = Db.Lng(r, "QueueWaitMs"),
                                      .Tok = Db.Lng(r, "PromptTokens") + Db.Lng(r, "CompletionTokens"), .Cost = Db.Lng(r, "CostMicros")}, ("@s", CObj(since))).ConfigureAwait(False)
            calls = calls.Where(Function(c) visible(c.Agent)).ToList()
            Dim models = calls.GroupBy(Function(c) c.Model).Select(Function(g)
                                                                       Dim ms = g.Select(Function(c) c.Ms).OrderBy(Function(x) x).ToList()
                                                                       Dim waits = g.Select(Function(c) c.Wait).OrderBy(Function(x) x).ToList()
                                                                       Return New With {.model = g.Key, .calls = g.Count(), .failures = g.Where(Function(c) Not c.Ok).Count(),
                                                                                        .p50Ms = Pct(ms, 0.5), .p95Ms = Pct(ms, 0.95), .avgWaitMs = CLng(waits.DefaultIfEmpty(0).Average()),
                                                                                        .p95WaitMs = Pct(waits, 0.95), .tokens = g.Sum(Function(c) c.Tok), .costMicros = g.Sum(Function(c) c.Cost)}
                                                                   End Function).OrderByDescending(Function(m) m.calls).ToList()
            Dim reasons = calls.GroupBy(Function(c) c.Reason).Select(Function(g) New With {.key = g.Key, .count = g.Count()}).OrderByDescending(Function(x) x.count).ToList()
            Dim purposes = calls.GroupBy(Function(c) c.Purpose).Select(Function(g) New With {.key = g.Key, .count = g.Count()}).OrderByDescending(Function(x) x.count).ToList()

            ' ---- tools
            Dim tools = Await store.QueryAsync("SELECT AgentId, ToolName, Source, Success, DurationMs FROM ToolCalls WHERE StartedUtc>=@s LIMIT 50000",
                Function(r) New With {.Agent = Db.Str(r, "AgentId"), .Tool = Db.Str(r, "ToolName"), .Source = Db.Str(r, "Source"), .Ok = Db.Str(r, "Success"), .Ms = Db.LngN(r, "DurationMs")},
                ("@s", CObj(since))).ConfigureAwait(False)
            Dim toolStats = tools.Where(Function(t) visible(t.Agent)).GroupBy(Function(t) t.Tool).Select(Function(g) New With {
                .tool = g.Key, .source = g.First().Source, .calls = g.Count(), .failures = g.Where(Function(t) t.Ok = "0").Count(),
                .avgMs = CLng(g.Where(Function(t) t.Ms.HasValue).Select(Function(t) CDbl(t.Ms.Value)).DefaultIfEmpty(0).Average())}).
                OrderByDescending(Function(t) t.calls).Take(20).ToList()

            ' ---- tasks, messages, results, global tasks
            Dim taskStatus = Await store.QueryAsync("SELECT AgentId, Status, COUNT(*) AS N FROM AgentTasks WHERE CreatedUtc>=@s OR Status IN ('Pending','Running','Waiting','Retrying','Blocked') GROUP BY AgentId, Status",
                Function(r) New With {.Agent = Db.Str(r, "AgentId"), .Status = Db.Str(r, "Status"), .N = Db.Lng(r, "N")}, ("@s", CObj(since))).ConfigureAwait(False)
            Dim taskMix = taskStatus.Where(Function(x) visible(x.Agent)).GroupBy(Function(x) x.Status).Select(Function(g) New With {.key = g.Key, .count = g.Sum(Function(x) x.N)}).
                OrderByDescending(Function(x) x.count).ToList()
            Dim messages = Await store.QueryAsync("SELECT MessageType, Status, COUNT(*) AS N FROM AgentMessages WHERE CreatedUtc>=@s GROUP BY MessageType, Status",
                Function(r) New With {.type = Db.Str(r, "MessageType"), .status = Db.Str(r, "Status"), .count = Db.Lng(r, "N")}, ("@s", CObj(since))).ConfigureAwait(False)
            Dim messageMix = messages.GroupBy(Function(m) m.type).Select(Function(g) New With {.key = g.Key, .count = g.Sum(Function(x) x.count)}).OrderByDescending(Function(x) x.count).ToList()
            Dim results = Await store.QueryAsync("SELECT Status, COUNT(*) AS N, AVG(Rating) AS R, SUM(AwardedMicros) AS A FROM Results GROUP BY Status",
                Function(r) New With {.status = Db.Str(r, "Status"), .count = Db.Lng(r, "N"), .avgRating = If(r.IsDBNull(r.GetOrdinal("R")), CType(Nothing, Double?), r.GetDouble(r.GetOrdinal("R"))),
                                      .awardedMicros = Db.Lng(r, "A")}).ConfigureAwait(False)
            Dim verdicts = Await store.QueryAsync("SELECT ReviewVerdict, COUNT(*) AS N FROM Results GROUP BY ReviewVerdict",
                Function(r) New With {.key = Db.Str(r, "ReviewVerdict"), .count = Db.Lng(r, "N")}).ConfigureAwait(False)
            Dim globals = Await store.QueryAsync("SELECT Status, COUNT(*) AS N FROM GlobalTasks GROUP BY Status",
                Function(r) New With {.key = Db.Str(r, "Status"), .count = Db.Lng(r, "N")}).ConfigureAwait(False)

            ' ---- events: per minute (last hour), by type and by level in the window
            Dim minuteStart = now.AddMinutes(-59)
            Dim perMinute = Await store.QueryAsync("SELECT substr(CreatedUtc,1,16) AS M, Level, COUNT(*) AS N FROM ActivityEvents WHERE CreatedUtc>=@s GROUP BY substr(CreatedUtc,1,16), Level",
                Function(r) New With {.Minute = Db.Str(r, "M"), .Level = Db.Str(r, "Level"), .N = Db.Lng(r, "N")}, ("@s", CObj(Db.Ts(New DateTime(minuteStart.Year, minuteStart.Month, minuteStart.Day, minuteStart.Hour, minuteStart.Minute, 0, DateTimeKind.Utc))))).ConfigureAwait(False)
            Dim minutes = Enumerable.Range(0, 60).Select(Function(i)
                                                             Dim m = minuteStart.AddMinutes(i)
                                                             Dim key = m.ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture)
                                                             Return New With {.minuteUtc = key & ":00Z",
                                                                              .info = perMinute.Where(Function(x) x.Minute = key AndAlso x.Level = "Info").Sum(Function(x) x.N),
                                                                              .warning = perMinute.Where(Function(x) x.Minute = key AndAlso x.Level = "Warning").Sum(Function(x) x.N),
                                                                              .error = perMinute.Where(Function(x) x.Minute = key AndAlso x.Level = "Error").Sum(Function(x) x.N)}
                                                         End Function).ToList()
            Dim eventTypes = Await store.QueryAsync("SELECT EventType, Level, COUNT(*) AS N FROM ActivityEvents WHERE CreatedUtc>=@s GROUP BY EventType, Level",
                Function(r) New With {.type = Db.Str(r, "EventType"), .level = Db.Str(r, "Level"), .count = Db.Lng(r, "N")}, ("@s", CObj(since))).ConfigureAwait(False)
            Dim eventAgents = Await store.QueryAsync("SELECT AgentId, COUNT(*) AS N FROM ActivityEvents WHERE CreatedUtc>=@s AND AgentId IS NOT NULL GROUP BY AgentId ORDER BY N DESC LIMIT 30",
                Function(r) New With {.agentId = Db.Str(r, "AgentId"), .count = Db.Lng(r, "N")}, ("@s", CObj(since))).ConfigureAwait(False)

            ' ---- wallets
            Dim wallets = (Await _h.Wallets.ListAsync().ConfigureAwait(False)).Where(Function(w) visible(w.AgentId) AndAlso agentNames.ContainsKey(w.AgentId)).Select(Function(w) New With {
                .agentId = w.AgentId, .availableMicros = w.AvailableMicros, .burnRateMicrosPerDay = w.BurnRateMicrosPerDay, .runwayDays = w.RunwayDays,
                .spentTodayMicros = w.SpentTodayMicros, .lifetimeSpendMicros = w.LifetimeSpendMicros, .lifetimeProfitMicros = w.LifetimeProfitMicros,
                .lowRunway = _h.Wallets.IsLowRunway(w), .dead = _h.Agents.IsDead(w.AgentId)}).OrderBy(Function(w) w.availableMicros).ToList()

            Await rc.OkAsync(New With {
                .hours = hours, .startUtc = start, .hourly = hourly, .daily = dailyFull, .agents = byAgent, .models = models, .reasons = reasons, .purposes = purposes,
                .tools = toolStats, .taskMix = taskMix, .messageMix = messageMix, .messages = messages, .results = results, .verdicts = verdicts, .globalTasks = globals,
                .eventsPerMinute = minutes, .eventTypes = eventTypes.GroupBy(Function(e) e.type).Select(Function(g) New With {
                    .key = g.Key, .count = g.Sum(Function(x) x.count), .warnings = g.Where(Function(x) x.level <> "Info").Sum(Function(x) x.count)}).OrderByDescending(Function(x) x.count).ToList(),
                .eventLevels = eventTypes.GroupBy(Function(e) e.level).Select(Function(g) New With {.key = g.Key, .count = g.Sum(Function(x) x.count)}).ToList(),
                .eventAgents = eventAgents.Where(Function(e) visible(e.agentId)).ToList(), .wallets = wallets,
                .threshold = New With {.bankruptcyMicros = _h.Wallets.BankruptcyThresholdMicros, .lowRunwayDays = _h.Wallets.LowRunwayDays}}).ConfigureAwait(False)
        End Function

        Private Shared Function OwnerId(agentId As String) As String
            Dim i = If(agentId, "").IndexOf(".sub-", StringComparison.Ordinal)
            Return If(i > 0, agentId.Substring(0, i), agentId)
        End Function
    End Class

End Namespace
