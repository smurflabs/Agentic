Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.Utilities

Namespace Http

    ''' <summary>
    ''' Admin API for the autonomy features: inbound hooks (plus the public fire endpoint), file watches, standing goals,
    ''' self-modification proposals and authored skills.
    ''' </summary>
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterAutonomy(r As Router)
            ' Public: fire a hook. The token in the URL is the authorisation; knowing it is enough.
            r.Map("POST", "/api/hooks/{token}", AuthLevel.Anonymous, AddressOf FireHook)

            r.Map("GET", "/api/hooks", AuthLevel.Admin, AddressOf ListHooks)
            r.Map("POST", "/api/hooks", AuthLevel.Admin, AddressOf CreateHook)
            r.Map("PATCH", "/api/hooks/{id}", AuthLevel.Admin, AddressOf PatchHook)
            r.Map("DELETE", "/api/hooks/{id}", AuthLevel.Admin, AddressOf DeleteHook)

            r.Map("GET", "/api/file-watches", AuthLevel.Admin, AddressOf ListWatches)
            r.Map("POST", "/api/file-watches", AuthLevel.Admin, AddressOf CreateWatch)
            r.Map("DELETE", "/api/file-watches/{id}", AuthLevel.Admin, AddressOf DeleteWatch)

            r.Map("GET", "/api/goals", AuthLevel.Admin, AddressOf ListGoals)
            r.Map("POST", "/api/goals", AuthLevel.Admin, AddressOf CreateGoal)
            r.Map("PATCH", "/api/goals/{id}", AuthLevel.Admin, AddressOf PatchGoal)
            r.Map("DELETE", "/api/goals/{id}", AuthLevel.Admin, AddressOf DeleteGoal)

            r.Map("GET", "/api/proposals", AuthLevel.Admin, AddressOf ListProposals)
            r.Map("GET", "/api/skills", AuthLevel.Admin, AddressOf ListSkills)
        End Sub

        Private Shared Function ParsePriority(b As JsonElement, defaultValue As PriorityLevel) As PriorityLevel
            Dim pr As PriorityLevel = defaultValue
            [Enum].TryParse(JsonUtil.GetString(b, "priority", defaultValue.ToString()), True, pr)
            Return pr
        End Function

        ' -------------------------------------------------------------- hooks

        Private Async Function FireHook(rc As RequestContext) As Task
            Dim token = rc.Route("token")
            Dim payload = Await rc.ReadBodyAsync().ConfigureAwait(False)
            Dim res = Await _h.Triggers.FireAsync(token, payload).ConfigureAwait(False)
            If res.TaskId Is Nothing Then
                Dim code = If(res.ErrorMessage = "Unknown hook.", 404, If(res.ErrorMessage = "Rate limited; fired too recently.", 429, 409))
                Await rc.WriteJsonAsync(code, New With {.error = New With {.code = "HOOK_NOT_FIRED", .message = res.ErrorMessage}}).ConfigureAwait(False)
                Return
            End If
            Await rc.OkAsync(New With {.ok = True, .taskId = res.TaskId}).ConfigureAwait(False)
        End Function

        Private Async Function ListHooks(rc As RequestContext) As Task
            Dim hooks = Await _h.Triggers.ListHooksAsync(rc.QueryString("agent")).ConfigureAwait(False)
            Await rc.OkAsync(hooks.Select(Function(h) New With {h.Id, h.AgentId, h.Name, h.TaskName, h.Token, h.Priority, h.Enabled,
                                                                h.MinIntervalSeconds, h.FireCount, h.LastFiredUtc, h.CreatedUtc, h.CreatedBy,
                                                                .url = "/api/hooks/" & h.Token}).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function CreateHook(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim agent = JsonUtil.GetString(b, "agent")
            If String.IsNullOrWhiteSpace(agent) Then Throw ApiException.BadRequest("agent is required.")
            Try
                Dim h = Await _h.Triggers.CreateHookAsync(agent, JsonUtil.GetString(b, "name"), JsonUtil.GetString(b, "taskName"),
                                                          JsonUtil.GetString(b, "template"), ParsePriority(b, PriorityLevel.Normal),
                                                          JsonUtil.GetInt(b, "minIntervalSeconds", 0), rc.User.Name).ConfigureAwait(False)
                Await AuditAsync(rc, "create-hook", "hook", h.Id, New With {h.AgentId, h.Name}).ConfigureAwait(False)
                Await rc.WriteJsonAsync(201, New With {h.Id, h.AgentId, h.Name, h.Token, .url = "/api/hooks/" & h.Token}).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw ApiException.BadRequest(ex.Message)
            End Try
        End Function

        Private Async Function PatchHook(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ok = Await _h.Triggers.SetHookEnabledAsync(rc.Route("id"), JsonUtil.GetBool(b, "enabled", True)).ConfigureAwait(False)
            If Not ok Then Throw ApiException.NotFound("HOOK_NOT_FOUND", "No such hook.")
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteHook(rc As RequestContext) As Task
            Dim ok = Await _h.Triggers.DeleteHookAsync(rc.Route("id")).ConfigureAwait(False)
            If Not ok Then Throw ApiException.NotFound("HOOK_NOT_FOUND", "No such hook.")
            Await AuditAsync(rc, "delete-hook", "hook", rc.Route("id")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------- file watches

        Private Async Function ListWatches(rc As RequestContext) As Task
            Dim ws = Await _h.Triggers.ListWatchesAsync(rc.QueryString("agent")).ConfigureAwait(False)
            Await rc.OkAsync(ws.Select(Function(w) New With {w.Id, w.AgentId, w.Path, w.Filter, w.TaskName, w.Priority, w.Enabled,
                                                             w.DebounceSeconds, w.FireCount, w.LastFiredUtc, w.CreatedUtc}).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function CreateWatch(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim agent = JsonUtil.GetString(b, "agent")
            If String.IsNullOrWhiteSpace(agent) Then Throw ApiException.BadRequest("agent is required.")
            Try
                Dim w = Await _h.Triggers.CreateWatchAsync(agent, JsonUtil.GetString(b, "path"), JsonUtil.GetString(b, "filter"),
                                                           JsonUtil.GetString(b, "taskName"), JsonUtil.GetString(b, "template"),
                                                           ParsePriority(b, PriorityLevel.Normal), JsonUtil.GetInt(b, "debounceSeconds", 5), rc.User.Name).ConfigureAwait(False)
                Await AuditAsync(rc, "create-file-watch", "file-watch", w.Id, New With {w.AgentId, w.Path}).ConfigureAwait(False)
                Await rc.WriteJsonAsync(201, New With {w.Id, w.AgentId, w.Path, w.Filter}).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw ApiException.BadRequest(ex.Message)
            End Try
        End Function

        Private Async Function DeleteWatch(rc As RequestContext) As Task
            Dim ok = Await _h.Triggers.DeleteWatchAsync(rc.Route("id")).ConfigureAwait(False)
            If Not ok Then Throw ApiException.NotFound("WATCH_NOT_FOUND", "No such file watch.")
            Await AuditAsync(rc, "delete-file-watch", "file-watch", rc.Route("id")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------- goals

        Private Async Function ListGoals(rc As RequestContext) As Task
            Dim goals = Await _h.Goals.ListGoalsAsync(rc.QueryString("agent"), rc.QueryString("status")).ConfigureAwait(False)
            Await rc.OkAsync(goals.Select(Function(g) New With {g.Id, g.AgentId, g.Title, g.Detail, g.Metric, g.Status, g.Progress,
                                                                g.ProgressPct, g.CadenceMinutes, g.Reviews, g.LastReviewUtc, g.NextReviewUtc, g.CreatedUtc, g.CreatedBy}).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function CreateGoal(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim agent = JsonUtil.GetString(b, "agent")
            If String.IsNullOrWhiteSpace(agent) Then Throw ApiException.BadRequest("agent is required.")
            Try
                Dim g = Await _h.Goals.CreateGoalAsync(agent, JsonUtil.GetString(b, "title"), JsonUtil.GetString(b, "detail"),
                                                       JsonUtil.GetString(b, "metric"), JsonUtil.GetInt(b, "cadenceMinutes", 1440), rc.User.Name).ConfigureAwait(False)
                Await AuditAsync(rc, "create-goal", "goal", g.Id, New With {g.AgentId, g.Title}).ConfigureAwait(False)
                Await rc.WriteJsonAsync(201, New With {g.Id, g.AgentId, g.Title, g.CadenceMinutes}).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw ApiException.BadRequest(ex.Message)
            End Try
        End Function

        Private Async Function PatchGoal(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim cadence As Integer? = Nothing
            Dim ce As JsonElement
            If b.TryGetProperty("cadenceMinutes", ce) AndAlso ce.ValueKind = JsonValueKind.Number Then cadence = ce.GetInt32()
            Dim ok = Await _h.Goals.UpdateGoalAsync(rc.Route("id"), JsonUtil.GetString(b, "status"), cadence,
                                                    JsonUtil.GetString(b, "detail"), JsonUtil.GetString(b, "metric")).ConfigureAwait(False)
            If Not ok Then Throw ApiException.NotFound("GOAL_NOT_FOUND", "No such goal.")
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function DeleteGoal(rc As RequestContext) As Task
            Dim ok = Await _h.Goals.DeleteGoalAsync(rc.Route("id")).ConfigureAwait(False)
            If Not ok Then Throw ApiException.NotFound("GOAL_NOT_FOUND", "No such goal.")
            Await AuditAsync(rc, "delete-goal", "goal", rc.Route("id")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------- proposals & skills (read-only visibility)

        Private Async Function ListProposals(rc As RequestContext) As Task
            Dim props = Await _h.Proposals.ListAsync(rc.QueryString("agent"), rc.QueryString("status")).ConfigureAwait(False)
            Await rc.OkAsync(props.Select(Function(p) New With {p.Id, p.AgentId, p.Kind, p.Proposed, p.Rationale, p.Status,
                                                                p.ApprovalId, p.CreatedUtc, p.DecidedBy, p.DecidedUtc, p.RolledBackUtc, p.RollbackReason}).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function ListSkills(rc As RequestContext) As Task
            Dim agent = rc.QueryString("agent")
            If String.IsNullOrWhiteSpace(agent) Then Throw ApiException.BadRequest("agent is required.")
            Dim skills = Await _h.Skills.ListAsync(agent).ConfigureAwait(False)
            Await rc.OkAsync(skills.Select(Function(s) New With {s.Id, s.Name, s.Description, s.Uses, s.CreatedUtc, s.UpdatedUtc}).ToList()).ConfigureAwait(False)
        End Function

    End Class

End Namespace
