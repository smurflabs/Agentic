Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Text
Imports System.Text.RegularExpressions
Imports Agentic.Agents
Imports Agentic.Approvals
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>An agent another agent hired (hire_agent), and whether it was retired.</summary>
    Public NotInheritable Class HireRecord
        Public Property AgentId As String
        Public Property HiredBy As String
        Public Property Reason As String
        Public Property ApprovalId As String
        Public Property CreatedUtc As DateTime
        Public Property RetiredUtc As DateTime?
        Public Property RetiredReason As String
    End Class

    ''' <summary>What hire_agent asks for (kept as the approval's context).</summary>
    Public NotInheritable Class HireSpec
        Public Property Name As String
        Public Property Role As String
        Public Property Instructions As String
        Public Property Models As List(Of String)
        Public Property Tools As List(Of String)
        Public Property McpServers As List(Of String)
        Public Property Level As String
        Public Property StartingBudgetUsd As Decimal
        Public Property Reason As String
    End Class

    ''' <summary>One step of a plan an agent made for itself (create_plan).</summary>
    Public NotInheritable Class PlannedStep
        Public Property Name As String
        Public Property Instructions As String
        ''' <summary>Steps (1-based) that must finish first.</summary>
        Public Property After As List(Of Integer)
        Public Property DelayMinutes As Integer
    End Class

    ' The organisation: hires, managed agents, assigned work, bounties and the plans agents make for themselves.
    Partial Public NotInheritable Class AutonomyService
        Private ReadOnly _hires As New ConcurrentDictionary(Of String, HireRecord)(StringComparer.OrdinalIgnoreCase)

        Private Shared Function MapHire(r As Microsoft.Data.Sqlite.SqliteDataReader) As HireRecord
            Return New HireRecord With {
                .AgentId = Db.Str(r, "AgentId"), .HiredBy = Db.Str(r, "HiredBy"), .Reason = Db.Str(r, "Reason"), .ApprovalId = Db.Str(r, "ApprovalId"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"), .RetiredUtc = Db.DtN(r, "RetiredUtc"), .RetiredReason = Db.Str(r, "RetiredReason")}
        End Function

        Private Async Function LoadHiresAsync() As Task
            _hires.Clear()
            For Each h In Await _db.QueryAsync("SELECT * FROM AgentHires", AddressOf MapHire).ConfigureAwait(False)
                _hires(h.AgentId) = h
            Next
        End Function

        ''' <summary>Every agent this agent hired (also retired ones), oldest first.</summary>
        Public Function Hires(hirerId As String) As List(Of HireRecord)
            Return _hires.Values.Where(Function(h) String.Equals(h.HiredBy, hirerId, StringComparison.OrdinalIgnoreCase)).OrderBy(Function(h) h.CreatedUtc).ToList()
        End Function

        Public Function ActiveHires(hirerId As String) As List(Of HireRecord)
            Return Hires(hirerId).Where(Function(h) Not h.RetiredUtc.HasValue AndAlso _agents.Exists(h.AgentId)).ToList()
        End Function

        ''' <summary>Who hired an agent (Nothing when nobody did).</summary>
        Public Function HireOf(agentId As String) As HireRecord
            Dim h As HireRecord = Nothing
            Return If(agentId IsNot Nothing AndAlso _hires.TryGetValue(agentId, h), h, Nothing)
        End Function

        ''' <summary>
        ''' The agents a manager manages: its Autonomy.Manages ("*" = every agent of the same owner) and its active hires.
        ''' </summary>
        Public Function ManagedAgents(managerId As String) As List(Of String)
            Dim result As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim p = _agents.GetEffectiveProfile(managerId)
            If p Is Nothing OrElse p.IsSubagent Then Return New List(Of String)
            If p.Manages.Contains("*") Then
                Dim owner = _agents.OwnerOf(managerId)
                For Each s In _agents.GetSnapshots()
                    If String.Equals(_agents.OwnerOf(s.Id), owner, StringComparison.OrdinalIgnoreCase) Then result.Add(s.Id)
                Next
            Else
                For Each m In p.Manages
                    If _agents.Exists(m) AndAlso Not _agents.IsSubagent(m) Then result.Add(m)
                Next
            End If
            For Each h In ActiveHires(managerId)
                result.Add(h.AgentId)
            Next
            result.Remove(managerId)
            Return result.OrderBy(Function(x) x).ToList()
        End Function

        Public Function IsManagedBy(managerId As String, agentId As String) As Boolean
            Return agentId IsNot Nothing AndAlso ManagedAgents(managerId).Contains(agentId, StringComparer.OrdinalIgnoreCase)
        End Function

        ' -------------------------------------------------------------------------------- hiring

        Private Function NewHireId(hirerId As String, name As String) As String
            Dim slug = Regex.Replace(If(name, "helper").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim("-"c)
            If slug.Length = 0 Then slug = "helper"
            If slug.Length > 20 Then slug = slug.Substring(0, 20).Trim("-"c)
            Dim prefix = If(hirerId.Length > 32, hirerId.Substring(0, 32), hirerId)
            For attempt = 1 To 20
                ' A fresh suffix every time: a retired hire's wallet and history never pass to a new agent.
                Dim suffix = Regex.Replace(Ids.NewToken(6).ToLowerInvariant(), "[^a-z0-9]", "").PadRight(4, "x"c).Substring(0, 4)
                Dim id = $"{prefix}-{slug}-{suffix}"
                If Not _agents.Exists(id) Then Return id
            Next
            Return $"{prefix}-{Ids.NewId("hire").ToLowerInvariant()}"
        End Function

        ''' <summary>An approved hire: creates the agent from the hirer's own limits, gives it the hirer's owner and starting budget.</summary>
        Private Async Function CreateHireAsync(rec As ApprovalRecord) As Task
            If _hires.Values.Any(Function(x) String.Equals(x.ApprovalId, rec.Id, StringComparison.Ordinal)) Then Return
            Dim hirer = _agents.GetEffectiveProfile(rec.AgentId)
            If hirer Is Nothing OrElse hirer.IsSubagent Then Return
            Dim spec As HireSpec = Nothing
            Try
                spec = JsonUtil.FromJson(Of HireSpec)(rec.Context)
            Catch ex As Exception
            End Try
            If spec Is Nothing OrElse String.IsNullOrWhiteSpace(spec.Name) Then
                Await _approvals.AppendResponseAsync(rec, "The hire request was incomplete, so no agent was created.").ConfigureAwait(False)
                Return
            End If
            ' The hire gets no more than the hirer has: its models, MCP servers and tools (never hiring or managing), a level no higher.
            Dim models = If(spec.Models, New List(Of String)).Where(Function(m) hirer.AllowedModels.Contains(m)).ToList()
            If models.Count = 0 Then models = hirer.AllowedModels.ToList()
            Dim excluded = AutonomyLevels.HiringTools.Concat(AutonomyLevels.ManagerTools).ToList()
            Dim hirerTools = ConfigValidator.KnownInternalTools.Where(Function(t) t <> "*" AndAlso hirer.HasInternalTool(t) AndAlso Not excluded.Contains(t)).ToList()
            Dim tools = If(spec.Tools, New List(Of String)).Where(Function(t) hirerTools.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList()
            If tools.Count = 0 Then tools = hirerTools
            If Not tools.Contains("complete_task") Then tools.Add("complete_task")
            Dim mcp = If(spec.McpServers, New List(Of String)).Where(Function(s) hirer.AllowedMcpServers.Contains(s) OrElse hirer.AllowedMcpServers.Contains("*")).ToList()
            If mcp.Count = 0 AndAlso (spec.McpServers Is Nothing OrElse spec.McpServers.Count = 0) Then mcp = hirer.AllowedMcpServers.Where(Function(s) s <> "*").ToList()
            Dim level = If(AutonomyLevels.Normalize(spec.Level), hirer.AutonomyLevel)
            If AutonomyLevels.Rank(level) > AutonomyLevels.Rank(hirer.AutonomyLevel) Then level = hirer.AutonomyLevel
            Dim id = NewHireId(hirer.Id, spec.Name)
            Dim p As New ProfileOptions With {
                .Id = id, .Name = TextUtil.Truncate(spec.Name.Trim(), 100), .Role = TextUtil.Truncate(If(String.IsNullOrWhiteSpace(spec.Role), "Helper", spec.Role.Trim()), 100),
                .Description = TextUtil.Truncate($"Hired by {hirer.Id}: {spec.Reason}", 1000),
                .BaseProfileId = If(_agents.Resolver.HasBase(hirer.BaseProfileId), hirer.BaseProfileId, Nothing),
                .AppendSystemPrompt = If(String.IsNullOrWhiteSpace(spec.Instructions), Nothing, TextUtil.Truncate(spec.Instructions.Trim(), 4000)),
                .AllowedModels = models, .MaxTier = hirer.MaxTier,
                .AllowedMcpServers = mcp, .AllowedMcpTools = hirer.AllowedMcpTools.ToList(), .DeniedMcpTools = hirer.DeniedMcpTools.ToList(),
                .InternalTools = tools,
                .Communication = New CommunicationPolicyOptions With {.AllowedRecipients = New List(Of String) From {hirer.Id}},
                .Economy = New AgentEconomyOptions With {.InitialBalance = 0D, .DailySpendLimit = hirer.DailySpendLimit},
                .Subagents = New SubagentPolicyOptions With {.MaxConcurrent = Math.Min(hirer.MaxSubagents, 2)},
                .Autonomy = New AgentAutonomyOptions With {
                    .Level = level, .SupervisorAgentId = hirer.Id, .CanHire = False,
                    .AllowedHttpHosts = hirer.AllowedHttpHosts.ToList(), .KnowledgeBases = hirer.KnowledgeBases.ToList()}}
            Dim report = Await _agents.SaveAdminAgentAsync(p, _host.McpServers.Select(Function(m) m.Id).ToList(), _host.Models.Select(Function(m) m.Key).ToList()).ConfigureAwait(False)
            If Not report.IsValid Then
                Await _approvals.AppendResponseAsync(rec, "The agent could not be created: " & String.Join(" ", report.Errors)).ConfigureAwait(False)
                Return
            End If
            Dim owner = _agents.OwnerOf(hirer.Id)
            If owner IsNot Nothing Then Await _agents.SetOwnerAsync(id, owner).ConfigureAwait(False)
            Dim h As New HireRecord With {.AgentId = id, .HiredBy = hirer.Id, .Reason = TextUtil.Truncate(spec.Reason, 1000), .ApprovalId = rec.Id, .CreatedUtc = _clock.UtcNow}
            Await _db.ExecuteAsync("INSERT INTO AgentHires(AgentId, HiredBy, Reason, ApprovalId, CreatedUtc) VALUES (@a, @h, @r, @ap, @c)",
                                   ("@a", CObj(id)), ("@h", hirer.Id), ("@r", h.Reason), ("@ap", rec.Id), ("@c", Db.Ts(h.CreatedUtc))).ConfigureAwait(False)
            _hires(id) = h
            Dim funded = ""
            If spec.StartingBudgetUsd > 0D Then
                Dim micros = TextUtil.DollarsToMicros(spec.StartingBudgetUsd)
                Dim t = Await _wallets.TransferAsync(hirer.Id, id, micros, LedgerEntryType.Transfer, $"Starting budget from {hirer.Id}", "hire:" & id, hirer.Id).ConfigureAwait(False)
                funded = If(t.Applied, $" It has {TextUtil.FormatMicros(micros)} from your wallet.", $" Its starting budget was not moved: {t.FailureReason}")
            End If
            _events.Emit(AgentEventType.ConfigChanged, $"{hirer.Id} hired {id} ({p.Role})", hirer.Id,
                         data:=New Dictionary(Of String, Object) From {{"hired", id}, {"approvalId", rec.Id}})
            Await _approvals.AppendResponseAsync(rec, $"Hired agent {id}.{funded} Give it work with assign_task, watch it with team_status; retire_agent ends it.").ConfigureAwait(False)
        End Function

        ''' <summary>Ends one of the hirer's hires: its remaining money goes back to the hirer, then the agent is removed.</summary>
        Public Async Function RetireAsync(hirerId As String, agentId As String, reason As String) As Task(Of (Ok As Boolean, Message As String))
            Dim h = HireOf(agentId)
            If h Is Nothing OrElse Not String.Equals(h.HiredBy, hirerId, StringComparison.OrdinalIgnoreCase) OrElse h.RetiredUtc.HasValue Then
                Return (False, $"{agentId} is not one of your hires.")
            End If
            Dim returned = 0L
            Dim w = Await _wallets.GetAsync(agentId).ConfigureAwait(False)
            If w IsNot Nothing AndAlso w.AvailableMicros > 0 AndAlso _agents.Exists(hirerId) Then
                Dim t = Await _wallets.TransferAsync(agentId, hirerId, w.AvailableMicros, LedgerEntryType.Transfer, $"{agentId} retired: remaining budget returned",
                                                     "retire:" & agentId, hirerId).ConfigureAwait(False)
                If t.Applied Then returned = w.AvailableMicros
            End If
            If _agents.Exists(agentId) Then
                Dim r = Await _agents.DeleteAdminAgentAsync(agentId).ConfigureAwait(False)
                If Not r.Success Then Return (False, r.Message)
            End If
            h.RetiredUtc = _clock.UtcNow
            h.RetiredReason = TextUtil.Truncate(reason, 1000)
            Await _db.ExecuteAsync("UPDATE AgentHires SET RetiredUtc=@t, RetiredReason=@r WHERE AgentId=@a",
                                   ("@t", CObj(Db.Ts(h.RetiredUtc))), ("@r", h.RetiredReason), ("@a", agentId)).ConfigureAwait(False)
            _events.Emit(AgentEventType.ConfigChanged, $"{hirerId} retired {agentId}: {TextUtil.Truncate(reason, 160)}", hirerId,
                         data:=New Dictionary(Of String, Object) From {{"retired", agentId}})
            Return (True, $"{agentId} is retired." & If(returned > 0, $" {TextUtil.FormatMicros(returned)} went back to your wallet.", ""))
        End Function

        ' -------------------------------------------------------------------------------- managing

        ''' <summary>What a manager sees of its team: state, current work, queue, money and recent results.</summary>
        Public Async Function TeamStatusAsync(managerId As String) As Task(Of List(Of Dictionary(Of String, Object)))
            Dim list As New List(Of Dictionary(Of String, Object))
            For Each id In ManagedAgents(managerId)
                Dim s = _agents.GetSnapshot(id)
                Dim w = Await _wallets.GetAsync(id).ConfigureAwait(False)
                Dim counts = Await _tasks.CountsByStatusAsync(id).ConfigureAwait(False)
                Dim recent = Await _db.QueryAsync("SELECT * FROM AgentTasks WHERE AgentId=@a AND Status IN ('Completed','Failed','Cancelled') " &
                                                  "ORDER BY CompletedUtc DESC LIMIT 3", AddressOf TaskRepository.Map, ("@a", CObj(id))).ConfigureAwait(False)
                Dim count = Function(st As String) As Long
                                Dim v As Long = 0
                                Return If(counts IsNot Nothing AndAlso counts.TryGetValue(st, v), v, 0L)
                            End Function
                list.Add(New Dictionary(Of String, Object) From {
                    {"agent", id}, {"name", s?.Name}, {"role", s?.Role}, {"state", s?.State}, {"hired", HireOf(id) IsNot Nothing},
                    {"currentTask", s?.CurrentTaskName}, {"queued", count("Pending") + count("Retrying") + count("Blocked")}, {"waiting", count("Waiting")},
                    {"available", If(w Is Nothing, Nothing, TextUtil.FormatMicros(w.AvailableMicros))},
                    {"recent", recent.Select(Function(t) New Dictionary(Of String, Object) From {
                        {"taskId", t.Id}, {"name", t.Name}, {"status", t.Status.ToString()}, {"success", t.GetResult()?.Success},
                        {"assignedBy", t.CreatedBy}, {"summary", TextUtil.Truncate(If(t.GetResult()?.Summary, t.ErrorMessage), 240)}}).ToList()}})
            Next
            Return list
        End Function

        ''' <summary>assign_task: gives one of the manager's agents a task (optionally with a bounty from the manager, and the manager as reviewer).</summary>
        Public Async Function AssignAsync(managerId As String, parent As AgentTask, agentId As String, name As String, instructions As String,
                                          priority As PriorityLevel, bountyMicros As Long, review As Boolean) As Task(Of AgentOperationResult)
            If Not IsManagedBy(managerId, agentId) Then Return AgentOperationResult.Fail("NOT_MANAGED", $"{agentId} is not on your team (team_status lists your team).")
            Dim check = Await CheckBountyAsync(managerId, bountyMicros).ConfigureAwait(False)
            If check IsNot Nothing Then Return AgentOperationResult.Fail("BOUNTY", check)
            Dim delivery As TaskDelivery = Nothing
            If bountyMicros > 0 OrElse review Then
                delivery = New TaskDelivery With {.BountyMicros = Math.Max(0, bountyMicros), .BountyFrom = If(bountyMicros > 0, managerId, Nothing),
                                                  .ReviewerAgentId = If(review, managerId, Nothing), .MaxRevisions = 1}
            End If
            Return Await _agents.SubmitTaskAsync(agentId, New NewTaskRequest With {
                .Name = TextUtil.Truncate(name, 200), .Description = instructions, .Priority = priority, .CreatedBy = managerId,
                .Delivery = delivery, .CorrelationId = parent?.CorrelationId}).ConfigureAwait(False)
        End Function

        ''' <summary>Nothing when the payer can offer this bounty now, else why not.</summary>
        Public Async Function CheckBountyAsync(payerId As String, micros As Long) As Task(Of String)
            If micros <= 0 Then Return Nothing
            If Not _opt.BountiesEnabled Then Return "Bounties are turned off on this server."
            Dim max = TextUtil.DollarsToMicros(_opt.MaxBountyUsd)
            If micros > max Then Return $"A bounty can be at most {TextUtil.FormatMicros(max)}."
            Dim w = Await _wallets.GetAsync(payerId).ConfigureAwait(False)
            If w Is Nothing OrElse w.AvailableMicros < micros Then
                Return $"You have only {TextUtil.FormatMicros(If(w?.AvailableMicros, 0L))} available, not enough for a {TextUtil.FormatMicros(micros)} bounty."
            End If
            Return Nothing
        End Function

        ''' <summary>cancel_agent_task: a manager cancels work it assigned, or any work of its own hires.</summary>
        Public Async Function CancelForManagerAsync(managerId As String, agentId As String, taskId As String) As Task(Of AgentOperationResult)
            If Not IsManagedBy(managerId, agentId) Then Return AgentOperationResult.Fail("NOT_MANAGED", $"{agentId} is not on your team.")
            Dim t = Await _tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
            If t Is Nothing Then Return AgentOperationResult.Fail("TASK_NOT_FOUND", $"{agentId} has no task {taskId}.")
            Dim h = HireOf(agentId)
            Dim ownHire = h IsNot Nothing AndAlso String.Equals(h.HiredBy, managerId, StringComparison.OrdinalIgnoreCase) AndAlso Not h.RetiredUtc.HasValue
            If Not ownHire AndAlso Not String.Equals(t.CreatedBy, managerId, StringComparison.OrdinalIgnoreCase) Then
                Return AgentOperationResult.Fail("NOT_YOURS", "You can cancel only work you assigned, or any work of agents you hired.")
            End If
            Return Await _agents.CancelTaskAsync(agentId, taskId).ConfigureAwait(False)
        End Function

        ' -------------------------------------------------------------------------------- bounties

        Private Shared Function AddNote(note As String, text As String) As String
            Return If(String.IsNullOrWhiteSpace(note), text, note & " " & text)
        End Function

        ''' <summary>OutcomeService hook: pays an accepted task's bounty (once for its whole revision chain).</summary>
        Friend Async Function PayBountyAsync(worker As AgentTask, root As AgentTask, verdict As ReviewOutcome) As Task
            Dim d = worker.Delivery
            If d Is Nothing OrElse d.BountyMicros <= 0 OrElse Not _opt.BountiesEnabled Then Return
            Dim amount = TextUtil.FormatMicros(d.BountyMicros)
            Dim result = worker.GetResult()
            Dim accepted = worker.Status = AgentTaskStatus.Completed AndAlso result IsNot Nothing AndAlso result.Success AndAlso (Not verdict.Reviewed OrElse verdict.Approved)
            If Not accepted Then
                verdict.Note = AddNote(verdict.Note, $"Bounty {amount} not paid: the work was not accepted.")
                Return
            End If
            If String.Equals(d.BountyFrom, worker.AgentId, StringComparison.OrdinalIgnoreCase) Then Return
            Dim title = If(root?.Name, worker.Name)
            Dim key = "bounty:" & If(root?.Id, worker.Id)
            Dim desc = TextUtil.Truncate($"Bounty for '{title}'", 300)
            Dim r As LedgerResult
            If String.IsNullOrEmpty(d.BountyFrom) Then
                r = Await _wallets.CreditAsync(worker.AgentId, d.BountyMicros, LedgerEntryType.Bounty, desc, key, "host", worker.Id).ConfigureAwait(False)
            Else
                r = Await _wallets.TransferAsync(d.BountyFrom, worker.AgentId, d.BountyMicros, LedgerEntryType.Bounty, desc, key, d.BountyFrom, worker.Id).ConfigureAwait(False)
            End If
            If r.Applied Then
                verdict.Note = AddNote(verdict.Note, $"Bounty {amount} paid to {worker.AgentId}" & If(String.IsNullOrEmpty(d.BountyFrom), ".", $" by {d.BountyFrom}."))
                Try
                    Await _agents.RememberAsync(worker.AgentId, $"You earned a {amount} bounty for accepted work on '{TextUtil.Truncate(title, 160)}'.", 3, root?.Id).ConfigureAwait(False)
                Catch ex As Exception
                End Try
            ElseIf Not r.Duplicate Then
                verdict.Note = AddNote(verdict.Note, $"Bounty {amount} not paid: {r.FailureReason}")
                _log.Warn("Autonomy", $"Bounty for {worker.Id} not paid: {r.FailureReason}", worker.AgentId, worker.Id)
            End If
        End Function

        ''' <summary>GlobalTaskService hook: a completed global task's bounty, shared equally by the agents whose steps succeeded.</summary>
        Friend Async Function PayGlobalBountyAsync(g As GlobalTask, verdict As ReviewOutcome) As Task
            If g Is Nothing OrElse g.Options.BountyMicros <= 0 OrElse Not _opt.BountiesEnabled Then Return
            Dim amount = TextUtil.FormatMicros(g.Options.BountyMicros)
            If verdict IsNot Nothing AndAlso verdict.Reviewed AndAlso Not verdict.Approved Then
                verdict.Note = AddNote(verdict.Note, $"Bounty {amount} not paid: the answer was not accepted.")
                Return
            End If
            Dim workers = g.Steps.Where(Function(s) s.Status = "Completed" AndAlso Not String.IsNullOrEmpty(s.AgentId) AndAlso _agents.Exists(s.AgentId)).
                Select(Function(s) s.AgentId).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If workers.Count = 0 Then Return
            Dim share = g.Options.BountyMicros \ workers.Count
            If share <= 0 Then Return
            For Each a In workers
                Dim r = Await _wallets.CreditAsync(a, share, LedgerEntryType.Bounty, TextUtil.Truncate($"Bounty share for global task '{g.Name}'", 300),
                                                   $"bounty:{g.Id}:{a}", "host").ConfigureAwait(False)
                If r.Applied Then
                    Try
                        Await _agents.RememberAsync(a, $"You earned a {TextUtil.FormatMicros(share)} bounty share for global task '{TextUtil.Truncate(g.Name, 160)}'.", 3, Nothing).ConfigureAwait(False)
                    Catch ex As Exception
                    End Try
                End If
            Next
            If verdict IsNot Nothing Then verdict.Note = AddNote(verdict.Note, $"Bounty {amount} shared by {String.Join(", ", workers)}.")
        End Function

        ' -------------------------------------------------------------------------------- plans

        ''' <summary>create_plan: the agent's own steps as tasks in its queue, each waiting for the steps it depends on.</summary>
        Public Async Function CreatePlanAsync(agentId As String, parent As AgentTask, goal As String, steps As List(Of PlannedStep)) As Task(Of List(Of AgentTask))
            If steps Is Nothing OrElse steps.Count = 0 Then Throw New InvalidOperationException("A plan needs at least one step.")
            If steps.Count > 12 Then Throw New InvalidOperationException("A plan can have at most 12 steps; split bigger work into goals.")
            For i = 0 To steps.Count - 1
                If String.IsNullOrWhiteSpace(steps(i).Name) OrElse String.IsNullOrWhiteSpace(steps(i).Instructions) Then
                    Throw New InvalidOperationException($"Step {i + 1} needs a name and instructions.")
                End If
                For Each a In If(steps(i).After, New List(Of Integer))
                    If a < 1 OrElse a > i Then Throw New InvalidOperationException($"Step {i + 1} can only wait for earlier steps (1-{i}), not {a}.")
                Next
            Next
            Dim now = _clock.UtcNow
            Dim correlation = If(parent?.CorrelationId, Ids.NewId("cor"))
            Dim outline As New StringBuilder()
            For i = 0 To steps.Count - 1
                outline.AppendLine($"{i + 1}. {TextUtil.Truncate(steps(i).Name, 120)}")
            Next
            Dim created As New List(Of AgentTask)
            For i = 0 To steps.Count - 1
                Dim s = steps(i)
                Dim t As New AgentTask With {
                    .Id = Ids.NewId("tsk"), .AgentId = agentId, .Name = TextUtil.Truncate($"{s.Name}", 200),
                    .Description = $"Step {i + 1} of {steps.Count} of your plan: {TextUtil.Truncate(goal, 300)}" & vbLf & "Plan:" & vbLf & outline.ToString() & vbLf &
                                   "This step:" & vbLf & s.Instructions.Trim(),
                    .Kind = TaskKind.OneTime, .Handler = "reasoning", .Priority = PriorityLevel.Normal, .MaxRetries = 2,
                    .NextRunUtc = If(s.DelayMinutes > 0, now.AddMinutes(Math.Min(s.DelayMinutes, 10080)), CType(Nothing, DateTime?)),
                    .CreatedUtc = now, .UpdatedUtc = now, .ParentTaskId = parent?.Id, .ParentAgentId = If(parent Is Nothing, Nothing, agentId),
                    .CorrelationId = correlation, .CreatedBy = agentId,
                    .DependsOn = If(s.After, New List(Of Integer)).Distinct().Select(Function(n) created(n - 1).Id).ToList()}
                If Not Await _tasks.InsertAsync(t).ConfigureAwait(False) Then Throw New InvalidOperationException("The plan could not be saved.")
                Await _tasks.AddHistoryAsync(t.Id, agentId, Nothing, If(t.DependsOn.Count > 0, AgentTaskStatus.Blocked, AgentTaskStatus.Pending), "Planned by the agent").ConfigureAwait(False)
                created.Add(t)
            Next
            _events.Emit(AgentEventType.TaskCreated, $"{agentId} planned {steps.Count} step(s): {TextUtil.Truncate(goal, 120)}", agentId, parent?.Id, correlationId:=correlation)
            _agents.Wake(agentId)
            Return created
        End Function
    End Class

End Namespace
