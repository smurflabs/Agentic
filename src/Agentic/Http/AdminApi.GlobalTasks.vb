Option Strict On
Option Explicit On
Option Infer On

Imports System.Globalization
Imports System.Text.Json
Imports Agentic.Configuration
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: recurring tasks (per agent), global tasks (planned and distributed by the host) and their schedules.
    Partial Friend NotInheritable Class AdminApi

        Friend Sub RegisterGlobalTasks(r As Router)
            r.Map("POST", "/api/agents/{id}/schedules", AuthLevel.Admin, AddressOf CreateAgentSchedule)
            r.Map("PATCH", "/api/agents/{id}/schedules/{scheduleId}", AuthLevel.Admin, AddressOf UpdateAgentSchedule)
            r.Map("POST", "/api/agents/{id}/schedules/{scheduleId}/run", AuthLevel.Admin, AddressOf RunAgentSchedule)
            r.Map("DELETE", "/api/agents/{id}/schedules/{scheduleId}", AuthLevel.Admin, AddressOf DeleteAgentSchedule)

            r.Map("GET", "/api/global-tasks", AuthLevel.Read, AddressOf ListGlobalTasks)
            r.Map("POST", "/api/global-tasks", AuthLevel.Admin, AddressOf CreateGlobalTask)
            r.Map("GET", "/api/global-tasks/{globalId}", AuthLevel.Read, AddressOf GetGlobalTask)
            r.Map("POST", "/api/global-tasks/{globalId}/approve", AuthLevel.Admin, AddressOf ApproveGlobalTask)
            r.Map("POST", "/api/global-tasks/{globalId}/replan", AuthLevel.Admin, AddressOf ReplanGlobalTask)
            r.Map("POST", "/api/global-tasks/{globalId}/cancel", AuthLevel.Admin, AddressOf CancelGlobalTask)
            r.Map("PATCH", "/api/global-schedules/{scheduleId}", AuthLevel.Admin, AddressOf UpdateGlobalSchedule)
            r.Map("POST", "/api/global-schedules/{scheduleId}/run", AuthLevel.Admin, AddressOf RunGlobalSchedule)
            r.Map("DELETE", "/api/global-schedules/{scheduleId}", AuthLevel.Admin, AddressOf DeleteGlobalSchedule)
        End Sub

        ' ------------------------------------------------------------------------------------------ shared request parts

        ''' <summary>First run from {delaySeconds} or {runAtUtc}; Nothing = as soon as possible.</summary>
        Private Shared Function ParseStart(b As JsonElement) As DateTime?
            Dim delay = JsonUtil.GetInt(b, "delaySeconds", 0)
            If delay > 0 Then Return DateTime.UtcNow.AddSeconds(delay)
            Dim runAt = JsonUtil.GetString(b, "runAtUtc")
            If String.IsNullOrWhiteSpace(runAt) Then Return Nothing
            Dim v As DateTime
            If Not DateTime.TryParse(runAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal Or DateTimeStyles.AssumeUniversal, v) Then
                Throw ApiException.BadRequest("runAtUtc must be an ISO-8601 date/time.")
            End If
            Return v
        End Function

        ''' <summary>Recurrence from {"repeat": {...}}; Nothing when the request does not repeat.</summary>
        Private Shared Function ParseRepeat(b As JsonElement) As ScheduleOptions
            Dim el As JsonElement
            If Not b.TryGetProperty("repeat", el) OrElse el.ValueKind <> JsonValueKind.Object Then Return Nothing
            If String.Equals(JsonUtil.GetString(el, "mode"), "none", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim s As New ScheduleOptions()
            Try
                Recurrence.Apply(el, s)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Return s
        End Function

        Private Shared Function ParsePriority(b As JsonElement) As PriorityLevel
            Dim pr As PriorityLevel
            If Not [Enum].TryParse(JsonUtil.GetString(b, "priority", "Normal"), True, pr) Then Throw ApiException.BadRequest("Invalid priority.")
            Return pr
        End Function

        ''' <summary>
        ''' Run options of a task or global task: {autoApproveTools, runPastDeadline, deadlineMinutes?}. Approving every tool
        ''' call needs the right to answer approvals ("Answer the inbox"), since it decides them in advance.
        ''' </summary>
        Private Shared Function ParseRunOptions(rc As RequestContext, b As JsonElement) As (AutoApproveTools As Boolean, RunPastDeadline As Boolean, DeadlineMinutes As Integer?)
            Dim el As JsonElement
            Dim auto = b.TryGetProperty("autoApproveTools", el) AndAlso el.ValueKind = JsonValueKind.True
            Dim past = b.TryGetProperty("runPastDeadline", el) AndAlso el.ValueKind = JsonValueKind.True
            If auto AndAlso Not rc.User.HasPermission("approvals") Then
                Throw New ApiException(403, "FORBIDDEN", "Approving every tool call automatically needs the ""Answer the inbox"" permission.")
            End If
            Dim minutes = JsonUtil.GetInt(b, "deadlineMinutes", 0)
            Return (auto, past, If(minutes > 0, Math.Clamp(minutes, 1, 60 * 24 * 30), CType(Nothing, Integer?)))
        End Function

        Private Shared Function ParsePurpose(b As JsonElement) As AiPurpose?
            Dim text = JsonUtil.GetString(b, "purpose")
            If String.IsNullOrWhiteSpace(text) Then Return Nothing
            Dim p As AiPurpose
            If Not [Enum].TryParse(text, True, p) Then Throw ApiException.BadRequest("Invalid purpose.")
            Return p
        End Function

        ' ------------------------------------------------------------------------------------------ recurring agent tasks

        ''' <summary>Body: the "Add task" fields plus {"repeat": {...}} and optionally {delaySeconds | runAtUtc} for the first run.</summary>
        Private Async Function CreateAgentSchedule(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim s = ParseRepeat(b)
            If s Is Nothing Then Throw ApiException.BadRequest("repeat is required for a recurring task.")
            s.Name = TextUtil.Truncate(JsonUtil.GetString(b, "name"), 200)
            If String.IsNullOrWhiteSpace(s.Name) Then Throw ApiException.BadRequest("Task name is required.")
            s.Description = TextUtil.Truncate(JsonUtil.GetString(b, "description"), 20000)
            s.Handler = JsonUtil.GetString(b, "handler", "reasoning")
            If Not ConfigValidator.KnownHandlers.Contains(s.Handler, StringComparer.OrdinalIgnoreCase) Then Throw ApiException.BadRequest($"Unknown handler '{s.Handler}'.")
            s.Priority = ParsePriority(b)
            s.Purpose = ParsePurpose(b)
            s.Kind = TaskKind.Recurring
            Dim modelKey = JsonUtil.GetString(b, "modelKey")
            If Not String.IsNullOrWhiteSpace(modelKey) Then
                Dim err = _h.Agents.CanPinModel(id, modelKey)
                If err IsNot Nothing Then Throw ApiException.BadRequest(err)
                s.ModelKey = modelKey
            End If
            Dim tier = JsonUtil.GetInt(b, "startTier", 0)
            If tier > 0 Then s.StartTier = tier
            Dim retries = JsonUtil.GetInt(b, "maxRetries", -1)
            If retries >= 0 Then s.MaxRetries = Math.Min(retries, 10)
            Dim payload As JsonElement
            If b.TryGetProperty("payload", payload) AndAlso payload.ValueKind = JsonValueKind.Object Then
                For Each p In payload.EnumerateObject()
                    s.Parameters(p.Name) = If(p.Value.ValueKind = JsonValueKind.String, p.Value.GetString(), p.Value.GetRawText())
                Next
            End If
            Dim delivery = Await ParseDeliveryAsync(rc, b, id).ConfigureAwait(False)
            s.Delivery = delivery.Delivery
            s.ReviewCriteria = delivery.Criteria
            Dim run = ParseRunOptions(rc, b)
            s.AutoApproveTools = run.AutoApproveTools
            s.RunPastDeadline = run.RunPastDeadline
            s.DeadlineMinutes = If(run.DeadlineMinutes, 0)
            Dim first = ParseStart(b)
            If first.HasValue Then s.StartUtc = first
            ' Interval schedules run first right away (or at the chosen start); calendar schedules at their first occurrence.
            Dim firstRun = If(s.IntervalSeconds.HasValue, If(first, DateTime.UtcNow), CType(Nothing, DateTime?))
            Dim created = Await _h.Scheduler.CreateAdminScheduleAsync(id, s, rc.User.Name, firstRun).ConfigureAwait(False)
            Await AuditAsync(rc, "create-schedule", "agent", id, New With {.scheduleId = created.Id, .name = s.Name, .summary = s.Summary}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, New With {.scheduleId = created.Id, .nextRunUtc = created.NextRunUtc, .summary = s.Summary}).ConfigureAwait(False)
        End Function

        Private Function ScheduleKey(rc As RequestContext) As String
            Return AgentIdOrThrow(rc) & ":" & rc.Route("scheduleId")
        End Function

        ''' <summary>Body: {"enabled": true|false}.</summary>
        Private Async Function UpdateAgentSchedule(rc As RequestContext) As Task
            Dim key = ScheduleKey(rc)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            If Not b.TryGetProperty("enabled", el) OrElse (el.ValueKind <> JsonValueKind.True AndAlso el.ValueKind <> JsonValueKind.False) Then Throw ApiException.BadRequest("enabled (true/false) is required.")
            If Not Await _h.Scheduler.SetEnabledAsync(key, el.GetBoolean()).ConfigureAwait(False) Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "Schedule does not exist.")
            Await AuditAsync(rc, If(el.GetBoolean(), "resume-schedule", "pause-schedule"), "schedule", key).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function RunAgentSchedule(rc As RequestContext) As Task
            Dim key = ScheduleKey(rc)
            If Not Await _h.Scheduler.RunScheduleNowAsync(key).ConfigureAwait(False) Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "Schedule does not exist.")
            Await AuditAsync(rc, "run-schedule", "schedule", key).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True, .message = "The task will be queued within a few seconds."}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteAgentSchedule(rc As RequestContext) As Task
            Dim key = ScheduleKey(rc)
            Dim problem = Await _h.Scheduler.DeleteAdminScheduleAsync(key).ConfigureAwait(False)
            If problem = "not-found" Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "Schedule does not exist.")
            If problem = "config" Then Throw ApiException.BadRequest("This schedule comes from the agent's configuration; pause it, or remove it in the agent editor or appsettings.json.")
            Await AuditAsync(rc, "delete-schedule", "schedule", key).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------------------------------ global tasks

        Private Function GlobalVisible(rc As RequestContext, g As GlobalTask) As Boolean
            If rc.User.SeesAllAgents Then Return True
            If String.Equals(g.CreatedBy, rc.User.Name, StringComparison.OrdinalIgnoreCase) Then Return True
            Return g.Steps.All(Function(s) Sees(rc, s.AgentId)) AndAlso g.Options.AgentIds.Count > 0 AndAlso g.Options.AgentIds.All(Function(a) Sees(rc, a))
        End Function

        Private Function GlobalDto(g As GlobalTask, Optional stats As Object = Nothing, Optional history As Object = Nothing) As Object
            Return New With {
                .stats = stats, .history = history, .deadlineUtc = _h.GlobalTasks.DeadlineOf(g), .firstStartedUtc = g.FirstStartedUtc,
                .id = g.Id, .name = g.Name, .description = g.Description, .status = g.Status, .options = g.Options, .understanding = g.Understanding,
                .approach = g.Approach, .plannerModel = g.PlannerModel, .feedback = g.Feedback, .correlationId = g.CorrelationId, .finalResult = g.FinalResult,
                .error = g.Error, .scheduleId = g.ScheduleId, .createdBy = g.CreatedBy, .createdUtc = g.CreatedUtc, .notBeforeUtc = g.NotBeforeUtc,
                .startedUtc = g.StartedUtc, .completedUtc = g.CompletedUtc, .deadlinePassedUtc = g.DeadlinePassedUtc,
                .revision = g.Revision, .reviewTaskId = g.ReviewTaskId, .review = JsonUtil.ParseElementOrNull(g.ReviewJson), .resultId = g.ResultId,
                .stepsTotal = g.Steps.Count, .stepsDone = g.Steps.Where(Function(s) s.IsFinished).Count(),
                .agents = g.Steps.Select(Function(s) s.AgentId).Distinct().ToList(),
                .steps = g.Steps.Select(Function(s) New With {
                    .stepId = s.StepId, .agentId = s.AgentId, .title = s.Title, .instructions = s.Instructions, .dependsOn = s.DependsOn, .taskId = s.TaskId,
                    .status = s.Status, .result = s.Result, .error = s.Error, .startedUtc = s.StartedUtc, .completedUtc = s.CompletedUtc}).ToList()}
        End Function

        Private Async Function ListGlobalTasks(rc As RequestContext) As Task
            Dim list = (Await _h.GlobalTasks.ListAsync(rc.QueryInt("limit", 100, 1, 500), rc.QueryString("status")).ConfigureAwait(False)).Where(Function(g) GlobalVisible(rc, g)).ToList()
            Dim schedules = (Await _h.GlobalTasks.ListSchedulesAsync().ConfigureAwait(False)).
                Where(Function(s) rc.User.SeesAllAgents OrElse String.Equals(s.CreatedBy, rc.User.Name, StringComparison.OrdinalIgnoreCase)).ToList()
            Await rc.OkAsync(New With {
                .enabled = _h.GlobalTasks.Enabled,
                .tasks = list.Select(Function(g) GlobalDto(g)).ToList(),
                .schedules = schedules.Select(Function(s) New With {
                    .id = s.Id, .name = s.Name, .description = s.Description, .options = s.Options, .summary = s.Summary, .enabled = s.Enabled,
                    .nextRunUtc = s.NextRunUtc, .lastRunUtc = s.LastRunUtc, .lastGlobalTaskId = s.LastGlobalTaskId, .runCount = s.RunCount, .createdBy = s.CreatedBy}).ToList(),
                .serverTimeZone = TimeZoneInfo.Local.DisplayName}).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body: {name, description, priority, purpose, startTier, maxRetries, delaySeconds | runAtUtc, repeat: {...},
        ''' agentIds: [] (empty = any), maxAgents, requirePlanApproval, synthesisAgentId, deadlineMinutes}.
        ''' </summary>
        Private Async Function CreateGlobalTask(rc As RequestContext) As Task
            If Not _h.GlobalTasks.Enabled Then Throw New ApiException(409, "GLOBAL_TASKS_DISABLED", "Global tasks are disabled in the configuration (GlobalTasks.Enabled).")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim name = JsonUtil.GetString(b, "name")
            If String.IsNullOrWhiteSpace(name) Then Throw ApiException.BadRequest("A name is required.")
            Dim description = JsonUtil.GetString(b, "description")
            If String.IsNullOrWhiteSpace(description) Then Throw ApiException.BadRequest("Describe what you want done (instructions).")
            Dim o As New GlobalTaskOptions With {
                .Priority = ParsePriority(b), .Purpose = ParsePurpose(b), .MaxAgents = Math.Clamp(JsonUtil.GetInt(b, "maxAgents", 4), 1, 20),
                .DeadlineMinutes = Math.Clamp(JsonUtil.GetInt(b, "deadlineMinutes", 240), 5, 60 * 24 * 7)}
            Dim run = ParseRunOptions(rc, b)
            o.AutoApproveTools = run.AutoApproveTools
            o.RunPastDeadline = run.RunPastDeadline
            Dim tier = JsonUtil.GetInt(b, "startTier", 0)
            If tier > 0 Then o.StartTier = tier
            Dim retries = JsonUtil.GetInt(b, "maxRetries", -1)
            If retries >= 0 Then o.MaxRetries = Math.Min(retries, 10)
            Dim approval As JsonElement
            o.RequirePlanApproval = b.TryGetProperty("requirePlanApproval", approval) AndAlso approval.ValueKind = JsonValueKind.True
            o.AgentIds = JsonUtil.GetStringList(b, "agentIds").Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            Dim unknown = o.AgentIds.Where(Function(a) Not _h.Agents.Exists(a)).ToList()
            If unknown.Count > 0 Then Throw ApiException.BadRequest("Unknown agent(s): " & String.Join(", ", unknown))
            ' Users limited to some agents can only distribute work among those agents.
            If rc.User.AllowedAgents.Count > 0 Then
                o.AgentIds = If(o.AgentIds.Count = 0, rc.User.AllowedAgents.ToList(), o.AgentIds.Where(Function(a) Sees(rc, a)).ToList())
                If o.AgentIds.Count = 0 Then Throw New ApiException(403, "FORBIDDEN", "You do not have access to any of the chosen agents.")
            End If
            Dim synth = JsonUtil.GetString(b, "synthesisAgentId")
            If Not String.IsNullOrWhiteSpace(synth) Then
                If Not _h.Agents.Exists(synth) Then Throw ApiException.BadRequest("Unknown agent for the final answer.")
                RequireAgentAccess(rc, synth)
                o.SynthesisAgentId = synth
            End If
            ' Review, delivery and attachments: files are copied to each step's agent when its step starts.
            Dim delivery = Await ParseDeliveryAsync(rc, b, Nothing).ConfigureAwait(False)
            If delivery.Delivery IsNot Nothing Then
                o.Deliver = delivery.Delivery.Deliver
                o.ReviewerAgentId = delivery.Delivery.ReviewerAgentId
                o.MaxRevisions = delivery.Delivery.MaxRevisions
                o.Attachments = delivery.Delivery.Attachments
                o.ReviewCriteria = delivery.Criteria
                o.ChatId = delivery.Delivery.ChatId
                o.ChatUser = delivery.Delivery.ChatUser
                o.BountyMicros = delivery.Delivery.BountyMicros
            End If
            Dim first = ParseStart(b)
            Dim repeat = ParseRepeat(b)
            If repeat IsNot Nothing Then
                If first.HasValue Then repeat.StartUtc = first
                Dim firstRun = If(repeat.IntervalSeconds.HasValue, If(first, DateTime.UtcNow), CType(Nothing, DateTime?))
                Dim s = Await _h.GlobalTasks.CreateScheduleAsync(name, description, o, repeat, rc.User.Name, firstRun).ConfigureAwait(False)
                Await AuditAsync(rc, "create-global-schedule", "global-schedule", s.Id, New With {.name = s.Name, .summary = s.Summary}).ConfigureAwait(False)
                Await rc.WriteJsonAsync(201, New With {.scheduleId = s.Id, .summary = s.Summary, .nextRunUtc = s.NextRunUtc}).ConfigureAwait(False)
                Return
            End If
            Dim g = Await _h.GlobalTasks.CreateAsync(name, description, o, rc.User.Name, first).ConfigureAwait(False)
            Await AuditAsync(rc, "create-global-task", "global-task", g.Id, New With {.name = g.Name}).ConfigureAwait(False)
            ' Handed over from a chat: the chat notes it now, and gets the final answer when the global task ends.
            If o.ChatId IsNot Nothing AndAlso _h.ChatReports IsNot Nothing Then
                Await _h.ChatReports.PostHandOverAsync(rc.User.Name, o.ChatId, Nothing, g.Name, "/global-tasks/" & Uri.EscapeDataString(g.Id), True).ConfigureAwait(False)
            End If
            Await rc.WriteJsonAsync(201, GlobalDto(g)).ConfigureAwait(False)
        End Function

        Private Async Function GlobalOrThrow(rc As RequestContext) As Task(Of GlobalTask)
            Dim g = Await _h.GlobalTasks.GetAsync(rc.Route("globalId")).ConfigureAwait(False)
            If g Is Nothing OrElse Not GlobalVisible(rc, g) Then Throw ApiException.NotFound("GLOBAL_TASK_NOT_FOUND", "Global task does not exist.")
            Return g
        End Function

        ''' <summary>One global task with its steps, what each step's task did (stats) and the steps of earlier attempts (history).</summary>
        Private Async Function GetGlobalTask(rc As RequestContext) As Task
            Dim g = Await GlobalOrThrow(rc).ConfigureAwait(False)
            Dim history = Await _h.GlobalTasks.HistoryAsync(g.Id).ConfigureAwait(False)
            Dim stats = Await _h.GlobalTasks.StepStatsAsync(g.Steps.Select(Function(s) s.TaskId).Concat(history.SelectMany(Function(h) h.Steps.Select(Function(s) s.TaskId)))).ConfigureAwait(False)
            Dim statOf = Function(taskId As String) As StepStats
                             Dim st As StepStats = Nothing
                             Return If(taskId IsNot Nothing AndAlso stats.TryGetValue(taskId, st), st, Nothing)
                         End Function
            Await rc.OkAsync(GlobalDto(g,
                g.Steps.Where(Function(s) s.TaskId IsNot Nothing).ToDictionary(Function(s) s.StepId, Function(s) statOf(s.TaskId)),
                history.Select(Function(h) New With {
                    .revision = h.Revision,
                    .steps = h.Steps.Select(Function(s) New With {.stepId = s.StepId, .agentId = s.AgentId, .title = s.Title, .taskId = s.TaskId, .status = s.Status,
                                                                  .result = s.Result, .error = s.Error, .startedUtc = s.StartedUtc, .completedUtc = s.CompletedUtc,
                                                                  .stats = statOf(s.TaskId)}).ToList()}).ToList())).ConfigureAwait(False)
        End Function

        Private Async Function ApproveGlobalTask(rc As RequestContext) As Task
            Dim g = Await GlobalOrThrow(rc).ConfigureAwait(False)
            If Not Await _h.GlobalTasks.ApproveAsync(g.Id).ConfigureAwait(False) Then Throw New ApiException(409, "NOT_AWAITING_APPROVAL", "This plan is not waiting for approval.")
            Await AuditAsync(rc, "approve-global-task", "global-task", g.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {"feedback": "..."} (optional).</summary>
        Private Async Function ReplanGlobalTask(rc As RequestContext) As Task
            Dim g = Await GlobalOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            If Not Await _h.GlobalTasks.ReplanAsync(g.Id, JsonUtil.GetString(b, "feedback")).ConfigureAwait(False) Then
                Throw New ApiException(409, "CANNOT_REPLAN", "Only a plan waiting for approval, or a failed global task, can be planned again.")
            End If
            Await AuditAsync(rc, "replan-global-task", "global-task", g.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function CancelGlobalTask(rc As RequestContext) As Task
            Dim g = Await GlobalOrThrow(rc).ConfigureAwait(False)
            If Not Await _h.GlobalTasks.CancelAsync(g.Id, rc.User.Name).ConfigureAwait(False) Then Throw New ApiException(409, "NOT_CANCELLABLE", "This global task has already finished.")
            Await AuditAsync(rc, "cancel-global-task", "global-task", g.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function UpdateGlobalSchedule(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim el As JsonElement
            If Not b.TryGetProperty("enabled", el) OrElse (el.ValueKind <> JsonValueKind.True AndAlso el.ValueKind <> JsonValueKind.False) Then Throw ApiException.BadRequest("enabled (true/false) is required.")
            If Not Await _h.GlobalTasks.SetScheduleEnabledAsync(rc.Route("scheduleId"), el.GetBoolean()).ConfigureAwait(False) Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "Schedule does not exist.")
            Await AuditAsync(rc, If(el.GetBoolean(), "resume-global-schedule", "pause-global-schedule"), "global-schedule", rc.Route("scheduleId")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function RunGlobalSchedule(rc As RequestContext) As Task
            If Not Await _h.GlobalTasks.RunScheduleNowAsync(rc.Route("scheduleId")).ConfigureAwait(False) Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "Schedule does not exist.")
            Await AuditAsync(rc, "run-global-schedule", "global-schedule", rc.Route("scheduleId")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteGlobalSchedule(rc As RequestContext) As Task
            If Not Await _h.GlobalTasks.DeleteScheduleAsync(rc.Route("scheduleId")).ConfigureAwait(False) Then Throw ApiException.NotFound("SCHEDULE_NOT_FOUND", "Schedule does not exist.")
            Await AuditAsync(rc, "delete-global-schedule", "global-schedule", rc.Route("scheduleId")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------------------------------ workflow timeline

        ''' <summary>Everything that happened in a workflow, in order: tasks, tool calls, model calls, messages and subagents.</summary>
        Private Async Function WorkflowTimelineAsync(rc As RequestContext, correlationId As String, taskIds As List(Of String)) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim items As New List(Of Dictionary(Of String, Object))
            Dim inList = If(taskIds.Count = 0, "''", String.Join(",", taskIds.Select(Function(x, i) "@t" & i)))
            Dim ps = taskIds.Select(Function(x, i) ("@t" & i, CObj(x))).ToArray()
            For Each toolRow In Await _h.Database.QueryAsync($"SELECT AgentId, TaskId, ToolName, Arguments, Result, Error, Success, DurationMs, StartedUtc FROM ToolCalls WHERE TaskId IN ({inList}) ORDER BY StartedUtc LIMIT 1000",
                                                       Function(r) New Dictionary(Of String, Object) From {
                                                           {"kind", "tool"}, {"ts", Db.Str(r, "StartedUtc")}, {"agentId", Db.Str(r, "AgentId")}, {"taskId", Db.Str(r, "TaskId")},
                                                           {"title", Db.Str(r, "ToolName")}, {"input", TextUtil.Truncate(Db.Str(r, "Arguments"), 1500)},
                                                           {"output", TextUtil.Truncate(If(Db.Str(r, "Result"), Db.Str(r, "Error")), 1500)},
                                                           {"success", Db.BoolN(r, "Success")}, {"durationMs", Db.LngN(r, "DurationMs")}}, ps).ConfigureAwait(False)
                items.Add(toolRow)
            Next
            For Each aiRow In Await _h.Database.QueryAsync("SELECT AgentId, TaskId, ModelKey, Reason, Purpose, Success, ErrorKind, DurationMs, PromptTokens, CompletionTokens, CostMicros, CreatedUtc " &
                                                       "FROM AiCalls WHERE CorrelationId=@c ORDER BY CreatedUtc LIMIT 1000",
                                                       Function(r) New Dictionary(Of String, Object) From {
                                                           {"kind", "ai"}, {"ts", Db.Str(r, "CreatedUtc")}, {"agentId", Db.Str(r, "AgentId")}, {"taskId", Db.Str(r, "TaskId")},
                                                           {"title", Db.Str(r, "ModelKey")}, {"reason", Db.Str(r, "Reason")}, {"purpose", Db.Str(r, "Purpose")},
                                                           {"success", Db.Bool(r, "Success")}, {"error", Db.Str(r, "ErrorKind")}, {"durationMs", Db.Lng(r, "DurationMs")},
                                                           {"tokens", Db.Lng(r, "PromptTokens") + Db.Lng(r, "CompletionTokens")}, {"costMicros", Db.Lng(r, "CostMicros")}},
                                                       ("@c", CObj(correlationId))).ConfigureAwait(False)
                items.Add(aiRow)
            Next
            For Each subRow In Await _h.Database.QueryAsync($"SELECT Id, ParentAgentId, ParentTaskId, Name, Status, CreatedUtc FROM Subagents WHERE ParentTaskId IN ({inList}) ORDER BY CreatedUtc",
                                                       Function(r) New Dictionary(Of String, Object) From {
                                                           {"kind", "subagent"}, {"ts", Db.Str(r, "CreatedUtc")}, {"agentId", Db.Str(r, "ParentAgentId")}, {"taskId", Db.Str(r, "ParentTaskId")},
                                                           {"title", Db.Str(r, "Name")}, {"subagentId", Db.Str(r, "Id")}, {"status", Db.Str(r, "Status")}}, ps).ConfigureAwait(False)
                items.Add(subRow)
            Next
            Return items.Where(Function(x) Sees(rc, CStr(x("agentId")))).OrderBy(Function(x) CStr(x("ts")), StringComparer.Ordinal).ToList()
        End Function
    End Class

End Namespace
