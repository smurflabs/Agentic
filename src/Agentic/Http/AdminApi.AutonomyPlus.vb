Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ''' <summary>
    ''' Admin API for fully autonomous agents: an agent's autonomy at a glance, its evaluation suite (cases, runs, rollback of
    ''' behaviour changes), the secrets vault and the mailboxes that give agents tasks.
    ''' </summary>
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterAutonomyPlus(r As Router)
            r.Map("GET", "/api/agents/{id}/autonomy", AuthLevel.Read, AddressOf AgentAutonomy)
            r.Map("POST", "/api/autonomy/sweep", AuthLevel.Admin, AddressOf AutonomySweep)

            r.Map("GET", "/api/agents/{id}/evals", AuthLevel.Read, AddressOf ListEvals)
            r.Map("POST", "/api/agents/{id}/evals", AuthLevel.Admin, AddressOf AddEvalCase)
            r.Map("POST", "/api/agents/{id}/evals/run", AuthLevel.Admin, AddressOf RunEvals)
            r.Map("GET", "/api/agents/{id}/evals/runs/{runId}", AuthLevel.Read, AddressOf GetEvalRun)
            r.Map("PATCH", "/api/agents/{id}/evals/{caseId}", AuthLevel.Admin, AddressOf PatchEvalCase)
            r.Map("DELETE", "/api/agents/{id}/evals/{caseId}", AuthLevel.Admin, AddressOf DeleteEvalCase)

            r.Map("POST", "/api/proposals/{proposalId}/rollback", AuthLevel.Admin, AddressOf RollBackProposal)

            r.Map("GET", "/api/secrets", AuthLevel.Admin, AddressOf ListSecrets)
            r.Map("PUT", "/api/secrets/{name}", AuthLevel.Admin, AddressOf PutSecret)
            r.Map("DELETE", "/api/secrets/{name}", AuthLevel.Admin, AddressOf DeleteSecret)

            r.Map("GET", "/api/mailboxes", AuthLevel.Admin, AddressOf ListMailboxes)
            r.Map("POST", "/api/mailboxes", AuthLevel.Admin, AddressOf SaveMailbox)
            r.Map("PUT", "/api/mailboxes/{mailboxId}", AuthLevel.Admin, AddressOf SaveMailbox)
            r.Map("DELETE", "/api/mailboxes/{mailboxId}", AuthLevel.Admin, AddressOf DeleteMailbox)
            r.Map("POST", "/api/mailboxes/{mailboxId}/check", AuthLevel.Admin, AddressOf CheckMailbox)
        End Sub

        ' -------------------------------------------------------------- overview

        Private Async Function AgentAutonomy(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            RequireAgentAccess(rc, id)
            Dim o = Await _h.Autonomy.OverviewAsync(id).ConfigureAwait(False)
            If o Is Nothing Then Throw ApiException.NotFound("AGENT_NOT_FOUND", "Agent does not exist.")
            o("evalCases") = (Await _h.Evals.ListCasesAsync(id).ConfigureAwait(False)).Count
            Dim lastRun = (Await _h.Evals.ListRunsAsync(id, 1).ConfigureAwait(False)).FirstOrDefault()
            o("lastEval") = lastRun
            ' Mailboxes are shown to everyone who sees the agent (never their passwords); only administrators change them.
            o("mailboxes") = (Await _h.Inboxes.ListAsync(id).ConfigureAwait(False)).Select(Function(m) MailboxDto(m)).ToList()
            o("options") = New With {.policies = _h.Options.Autonomy.PoliciesEnabled, .initiative = _h.Options.Autonomy.InitiativeEnabled,
                                     .bounties = _h.Options.Autonomy.BountiesEnabled, .maxBountyUsd = _h.Options.Autonomy.MaxBountyUsd,
                                     .hiring = _h.Options.Autonomy.HiringEnabled, .evals = _h.Options.Autonomy.EvalsEnabled, .email = _h.Options.Autonomy.EmailEnabled}
            Await rc.OkAsync(o).ConfigureAwait(False)
        End Function

        Private Async Function AutonomySweep(rc As RequestContext) As Task
            Await _h.Autonomy.SweepAsync(DateTime.UtcNow, rc.CancellationToken).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------- evaluations

        Private Async Function ListEvals(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            RequireAgentAccess(rc, id)
            Await rc.OkAsync(New With {
                .cases = Await _h.Evals.ListCasesAsync(id).ConfigureAwait(False),
                .runs = Await _h.Evals.ListRunsAsync(id, rc.QueryInt("limit", 20, 1, 200)).ConfigureAwait(False)}).ConfigureAwait(False)
        End Function

        Private Async Function AddEvalCase(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            RequireAgentAccess(rc, id)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Try
                Dim c = Await _h.Evals.AddCaseAsync(id, JsonUtil.GetString(b, "name"), JsonUtil.GetString(b, "instructions"), JsonUtil.GetString(b, "criteria"), rc.User.Name).ConfigureAwait(False)
                Await AuditAsync(rc, "add-eval-case", "agent", id, New With {.caseId = c.Id, .name = c.Name}).ConfigureAwait(False)
                Await rc.WriteJsonAsync(201, c).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw ApiException.BadRequest(ex.Message)
            End Try
        End Function

        Private Async Function CaseOfAgentAsync(rc As RequestContext) As Task(Of EvalCase)
            Dim id = AgentIdOrThrow(rc)
            RequireAgentAccess(rc, id)
            Dim c = Await _h.Evals.GetCaseAsync(rc.Route("caseId")).ConfigureAwait(False)
            If c Is Nothing OrElse Not String.Equals(c.AgentId, id, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.NotFound("EVAL_CASE_NOT_FOUND", "No such test case.")
            Return c
        End Function

        Private Async Function PatchEvalCase(rc As RequestContext) As Task
            Dim c = Await CaseOfAgentAsync(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim enabled As Boolean? = Nothing
            Dim e As JsonElement
            If b.TryGetProperty("enabled", e) AndAlso (e.ValueKind = JsonValueKind.True OrElse e.ValueKind = JsonValueKind.False) Then enabled = e.GetBoolean()
            Dim criteria As String = Nothing
            If b.TryGetProperty("criteria", e) AndAlso e.ValueKind = JsonValueKind.String Then criteria = e.GetString()
            Await _h.Evals.UpdateCaseAsync(c.Id, JsonUtil.GetString(b, "name"), JsonUtil.GetString(b, "instructions"), criteria, enabled).ConfigureAwait(False)
            Await rc.OkAsync(Await _h.Evals.GetCaseAsync(c.Id).ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        Private Async Function DeleteEvalCase(rc As RequestContext) As Task
            Dim c = Await CaseOfAgentAsync(rc).ConfigureAwait(False)
            Await _h.Evals.DeleteCaseAsync(c.Id).ConfigureAwait(False)
            Await AuditAsync(rc, "delete-eval-case", "agent", c.AgentId, New With {.caseId = c.Id}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        Private Async Function RunEvals(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            RequireAgentAccess(rc, id)
            Dim run = Await _h.Evals.StartAsync(id, "manual", Nothing, rc.User.Name).ConfigureAwait(False)
            If run Is Nothing Then Throw New ApiException(409, "EVAL_NOT_STARTED", "Nothing to run: the agent has no enabled test cases, a run is still going, or evaluations are off.")
            Await AuditAsync(rc, "run-evals", "agent", id, New With {.runId = run.Id}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, run).ConfigureAwait(False)
        End Function

        Private Async Function GetEvalRun(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            RequireAgentAccess(rc, id)
            Dim run = Await _h.Evals.GetRunAsync(rc.Route("runId")).ConfigureAwait(False)
            If run Is Nothing OrElse Not String.Equals(run.AgentId, id, StringComparison.OrdinalIgnoreCase) Then Throw ApiException.NotFound("EVAL_RUN_NOT_FOUND", "No such evaluation run.")
            Await rc.OkAsync(run).ConfigureAwait(False)
        End Function

        Private Async Function RollBackProposal(rc As RequestContext) As Task
            Dim id = rc.Route("proposalId")
            Dim p = Await _h.Proposals.GetAsync(id).ConfigureAwait(False)
            If p Is Nothing Then Throw ApiException.NotFound("PROPOSAL_NOT_FOUND", "No such behaviour change.")
            RequireAgentAccess(rc, p.AgentId)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ok = Await _h.Proposals.RollBackAsync(id, "Rolled back by " & rc.User.Name & If(String.IsNullOrWhiteSpace(JsonUtil.GetString(b, "reason")), "", ": " & JsonUtil.GetString(b, "reason"))).ConfigureAwait(False)
            If Not ok Then Throw New ApiException(409, "NOT_ROLLED_BACK", "Only an applied change whose text is still in the agent's instructions can be rolled back.")
            Await AuditAsync(rc, "rollback-proposal", "agent", p.AgentId, New With {.proposalId = id}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.rolledBack = True}).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------- secrets

        Private Async Function ListSecrets(rc As RequestContext) As Task
            Await rc.OkAsync(Await _h.Secrets.ListAsync().ConfigureAwait(False)).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Body: {"value": "…" (omit to keep), "description": "…", "agents": ["id", "*"], "hosts": ["api.example.com", "*.example.com"]
        ''' (omit to keep; empty = any host the agent may call)}. The value is never returned.
        ''' </summary>
        Private Async Function PutSecret(rc As RequestContext) As Task
            Dim name = rc.Route("name")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim agents = JsonUtil.GetStringList(b, "agents")
            For Each a In agents
                If a <> "*" AndAlso Not _h.Agents.Exists(a) Then Throw ApiException.BadRequest($"Unknown agent '{a}'.")
            Next
            Dim hostsEl As JsonElement
            Dim hosts As List(Of String)
            If b.TryGetProperty("hosts", hostsEl) AndAlso hostsEl.ValueKind = JsonValueKind.Array Then
                hosts = JsonUtil.GetStringList(b, "hosts")
            Else
                hosts = If((Await _h.Secrets.GetInfoAsync(name).ConfigureAwait(False))?.Hosts, New List(Of String))
            End If
            Try
                Dim info = Await _h.Secrets.SetAsync(name, JsonUtil.GetString(b, "value"), JsonUtil.GetString(b, "description"), agents, rc.User.Name, hosts).ConfigureAwait(False)
                Await AuditAsync(rc, "set-secret", "secret", name, New With {.agents = agents, .valueChanged = Not String.IsNullOrEmpty(JsonUtil.GetString(b, "value"))}).ConfigureAwait(False)
                Await rc.OkAsync(info).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
        End Function

        Private Async Function DeleteSecret(rc As RequestContext) As Task
            Dim name = rc.Route("name")
            If Not Await _h.Secrets.DeleteAsync(name).ConfigureAwait(False) Then Throw ApiException.NotFound("SECRET_NOT_FOUND", "No such secret.")
            Await AuditAsync(rc, "delete-secret", "secret", name).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------- mailboxes

        Private Shared Function MailboxDto(m As EmailInbox) As Object
            Return New With {m.Id, m.AgentId, m.Host, m.Port, m.UseSsl, m.Username, m.PasswordSecret, m.Folder, m.PollSeconds, m.AllowedSenders, m.TaskName,
                             m.Template, m.Priority, m.Enabled, m.LastPollUtc, m.LastError, m.Received, m.CreatedBy, m.CreatedUtc}
        End Function

        Private Async Function ListMailboxes(rc As RequestContext) As Task
            Dim list = Await _h.Inboxes.ListAsync(rc.QueryString("agent")).ConfigureAwait(False)
            Await rc.OkAsync(list.Select(Function(m) MailboxDto(m)).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function SaveMailbox(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim id = rc.Route("mailboxId")
            Dim existing As EmailInbox = Nothing
            If id IsNot Nothing Then
                existing = Await _h.Inboxes.GetAsync(id).ConfigureAwait(False)
                If existing Is Nothing Then Throw ApiException.NotFound("MAILBOX_NOT_FOUND", "No such mailbox.")
            End If
            Dim m = If(existing, New EmailInbox())
            Dim e As JsonElement
            m.AgentId = If(JsonUtil.GetString(b, "agentId"), m.AgentId)
            m.Host = If(JsonUtil.GetString(b, "host"), m.Host)
            If b.TryGetProperty("port", e) AndAlso e.ValueKind = JsonValueKind.Number Then m.Port = e.GetInt32()
            If b.TryGetProperty("useSsl", e) AndAlso (e.ValueKind = JsonValueKind.True OrElse e.ValueKind = JsonValueKind.False) Then m.UseSsl = e.GetBoolean()
            m.Username = If(JsonUtil.GetString(b, "username"), m.Username)
            m.PasswordSecret = If(JsonUtil.GetString(b, "passwordSecret"), m.PasswordSecret)
            m.Folder = If(JsonUtil.GetString(b, "folder"), m.Folder)
            If b.TryGetProperty("pollSeconds", e) AndAlso e.ValueKind = JsonValueKind.Number Then m.PollSeconds = e.GetInt32()
            If b.TryGetProperty("allowedSenders", e) Then m.AllowedSenders = If(e.ValueKind = JsonValueKind.String, e.GetString(), Nothing)
            If b.TryGetProperty("taskName", e) Then m.TaskName = If(e.ValueKind = JsonValueKind.String, e.GetString(), Nothing)
            If b.TryGetProperty("template", e) Then m.Template = If(e.ValueKind = JsonValueKind.String, e.GetString(), Nothing)
            If b.TryGetProperty("priority", e) Then
                Dim pr As PriorityLevel
                If e.ValueKind = JsonValueKind.Number Then
                    m.Priority = e.GetInt32()
                ElseIf e.ValueKind = JsonValueKind.String AndAlso [Enum].TryParse(e.GetString(), True, pr) Then
                    m.Priority = CInt(pr)
                End If
            End If
            If b.TryGetProperty("enabled", e) AndAlso (e.ValueKind = JsonValueKind.True OrElse e.ValueKind = JsonValueKind.False) Then m.Enabled = e.GetBoolean()
            Try
                Dim saved = Await _h.Inboxes.SaveAsync(m, rc.User.Name).ConfigureAwait(False)
                Await AuditAsync(rc, If(existing Is Nothing, "create-mailbox", "update-mailbox"), "agent", saved.AgentId, New With {.mailboxId = saved.Id, .host = saved.Host, .user = saved.Username}).ConfigureAwait(False)
                Await rc.WriteJsonAsync(If(existing Is Nothing, 201, 200), MailboxDto(saved)).ConfigureAwait(False)
            Catch ex As InvalidOperationException
                Throw ApiException.BadRequest(ex.Message)
            End Try
        End Function

        Private Async Function DeleteMailbox(rc As RequestContext) As Task
            Dim id = rc.Route("mailboxId")
            If Not Await _h.Inboxes.DeleteAsync(id).ConfigureAwait(False) Then Throw ApiException.NotFound("MAILBOX_NOT_FOUND", "No such mailbox.")
            Await AuditAsync(rc, "delete-mailbox", "mailbox", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Reads the mailbox now (also tests the connection): the result says how many tasks it created or why it failed.</summary>
        Private Async Function CheckMailbox(rc As RequestContext) As Task
            Dim m = Await _h.Inboxes.GetAsync(rc.Route("mailboxId")).ConfigureAwait(False)
            If m Is Nothing Then Throw ApiException.NotFound("MAILBOX_NOT_FOUND", "No such mailbox.")
            Dim r = Await _h.Inboxes.PollAsync(m, rc.CancellationToken).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = r.ErrorMessage Is Nothing, .created = r.Created, .error = r.ErrorMessage,
                                       .mailbox = MailboxDto(Await _h.Inboxes.GetAsync(m.Id).ConfigureAwait(False))}).ConfigureAwait(False)
        End Function
    End Class

End Namespace
