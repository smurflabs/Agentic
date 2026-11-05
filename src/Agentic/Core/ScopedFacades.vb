Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Agentic.Work

' Agent-bound facades over host services. Each is constructed with a fixed agent id and exposes no way to
' address another agent. They are the only service objects handed to an agent's tools.

Namespace Economy

    Public NotInheritable Class AgentBudgetView
        Private ReadOnly _wallets As WalletService
        Private ReadOnly _pricing As PricingService
        Private ReadOnly _registry As ModelRegistry
        Private ReadOnly _agentId As String
        Private ReadOnly _profile As Func(Of EffectiveProfile)
        Private ReadOnly _access As ModelAccessService
        Private ReadOnly _caps As SpendCapTracker

        Friend Sub New(wallets As WalletService, pricing As PricingService, registry As ModelRegistry, agentId As String, profile As Func(Of EffectiveProfile),
                       Optional access As ModelAccessService = Nothing, Optional caps As SpendCapTracker = Nothing)
            _wallets = wallets
            _pricing = pricing
            _registry = registry
            _agentId = agentId
            _profile = profile
            _access = access
            _caps = caps
        End Sub

        ''' <summary>The wallet that pays for this agent (a subagent's parent).</summary>
        Public Function GetAsync() As Task(Of WalletSnapshot)
            Return _wallets.GetAsync(_profile().BillingAgentId)
        End Function

        Private Shared Function FormatDays(days As Double) As String
            If days < 1 Then Return $"{Math.Max(1, CInt(Math.Round(days * 24)))} hours"
            If days >= 365 Then Return "more than a year"
            Return $"{days.ToString("0.#", Globalization.CultureInfo.InvariantCulture)} days"
        End Function

        ''' <summary>
        ''' What the agent must know to stay alive: money left, how fast it goes, when it runs out, what happens then and
        ''' how money is earned. Shown in every prompt.
        ''' </summary>
        Public Async Function SurvivalAsync() As Task(Of String)
            If Not _wallets.Enabled Then Return Nothing
            Dim p = _profile()
            Dim w = Await _wallets.GetAsync(p.BillingAgentId).ConfigureAwait(False)
            If w Is Nothing Then Return Nothing
            Dim money = TextUtil.FormatMicros(w.AvailableMicros)
            Dim threshold = TextUtil.FormatMicros(_wallets.BankruptcyThresholdMicros)
            Dim mortal = _wallets.BankruptcyEndsAgent AndAlso w.LifetimeCreditMicros > 0
            Dim sb As New StringBuilder()
            Dim whose = If(p.IsSubagent, $"{p.OwnerAgentId}'s wallet (which pays for you)", "Your wallet")
            sb.AppendLine($"- {whose} holds {money}.")
            If w.RunwayDays.HasValue Then
                Dim eta = DateTime.UtcNow.AddDays(Math.Min(w.RunwayDays.Value, 3650))
                sb.AppendLine($"- Recent spending: {TextUtil.FormatMicros(w.BurnRateMicrosPerDay)} per day. At that rate the money runs out in about {FormatDays(w.RunwayDays.Value)}" &
                              If(w.RunwayDays.Value < 365, $" (around {eta:yyyy-MM-dd HH:mm} UTC).", "."))
            Else
                sb.AppendLine("- You have not spent money recently, so the balance is not shrinking right now. Every paid model call reduces it.")
            End If
            If w.AverageChargeMicros > 0 Then
                Dim calls = If(w.AverageChargeMicros > 0, Math.Max(0L, w.AvailableMicros - _wallets.BankruptcyThresholdMicros) \ w.AverageChargeMicros, 0L)
                sb.AppendLine($"- Your paid calls cost about {TextUtil.FormatMicros(w.AverageChargeMicros)} each, so you can afford roughly {calls:N0} more.")
            End If
            If mortal Then
                If p.IsSubagent Then
                    sb.AppendLine($"- If that wallet falls below {threshold}, {p.OwnerAgentId} dies and you end with it.")
                Else
                    sb.AppendLine($"- If your balance falls below {threshold} you DIE: the host shuts you down at once, abandons your current work, and you stay dead " &
                                  "unless an administrator decides to pay to revive you. Nobody is obliged to.")
                End If
            End If
            sb.AppendLine("- The local model is free. Spend money only where it clearly improves the result, and never on retries of the same mistake.")
            sb.Append("- Money is earned, not given: administrators award money for good, verified results and record profit you generate. Sloppy, unverified or padded work earns nothing.")
            If _wallets.IsLowRunway(w) OrElse (mortal AndAlso w.AvailableMicros < _wallets.BankruptcyThresholdMicros * 25) Then
                sb.AppendLine().Append("- DANGER: you are close to running out. Use only the free local model unless a paid call is the only way to finish, and do not spawn paid subagents.")
            End If
            Return sb.ToString()
        End Function

        ''' <summary>Compact, model-readable budget summary including prices of the agent's usable cloud models.</summary>
        Public Async Function DescribeAsync(Optional includePrices As Boolean = True) As Task(Of String)
            If Not _wallets.Enabled Then Return "Budget tracking is disabled."
            Dim p = _profile()
            Dim w = Await _wallets.GetAsync(p.BillingAgentId).ConfigureAwait(False)
            If w Is Nothing Then Return "No wallet."
            Dim sb As New StringBuilder()
            If p.IsSubagent Then
                sb.Append($"You spend from {p.OwnerAgentId}'s wallet ({TextUtil.FormatMicros(w.AvailableMicros)} available). ")
                Dim spent = If(_caps Is Nothing, 0L, _caps.GetSpent(_agentId))
                sb.Append($"Your cloud spend so far: {TextUtil.FormatMicros(spent)}")
                If p.SpendCapMicros.HasValue Then sb.Append($" of your {TextUtil.FormatMicros(p.SpendCapMicros.Value)} cap")
                sb.Append(". Local models are free.")
            Else
                sb.Append($"Balance {TextUtil.FormatMicros(w.AvailableMicros)} available (reserved {TextUtil.FormatMicros(w.ReservedMicros)}); ")
                sb.Append($"spent today {TextUtil.FormatMicros(w.SpentTodayMicros)}")
                If w.DailyLimitMicros.HasValue Then sb.Append($" of {TextUtil.FormatMicros(w.DailyLimitMicros.Value)} daily limit")
                sb.Append($"; lifetime spend {TextUtil.FormatMicros(w.LifetimeSpendMicros)}; profit credited {TextUtil.FormatMicros(w.LifetimeProfitMicros)}.")
                If w.RunwayDays.HasValue Then
                    sb.Append($" Recent spend averages {TextUtil.FormatMicros(w.BurnRateMicrosPerDay)}/day, so your balance lasts about {FormatDays(w.RunwayDays.Value)}.")
                    If _wallets.IsLowRunway(w) Then
                        sb.Append(" WARNING: your budget is running out. Prefer the free local model, avoid escalating to paid models unless the task " &
                                  "clearly fails without it, and do not spawn subagents on paid models.")
                    End If
                End If
            End If
            If includePrices Then
                Dim lines As New List(Of String)
                For Each m In _registry.All().Where(Function(x) x.Options.Enabled).OrderBy(Function(x) x.Options.Tier)
                    Dim kind = If(_access Is Nothing,
                                  If(Not m.Options.IsCustom AndAlso p.AllowedModels.Contains(m.Key), ModelAccessKind.Profile, ModelAccessKind.None),
                                  _access.AccessFor(p, m))
                    If kind = ModelAccessKind.None Then Continue For
                    If kind = ModelAccessKind.Profile AndAlso m.Options.Tier > p.MaxTier Then Continue For
                    Dim tag = If(kind = ModelAccessKind.Grant, "granted, ", "")
                    If m.Options.IsLocal Then
                        lines.Add($"{m.Key} ({tag}tier {m.Options.Tier}, local, free)")
                    Else
                        Dim pr = _pricing.GetPrice(m.Key)
                        lines.Add(If(pr.IsPriced, $"{m.Key} ({tag}tier {m.Options.Tier}, ${pr.InputPerMillion}/M in, ${pr.OutputPerMillion}/M out)", $"{m.Key} (unpriced - unavailable)"))
                    End If
                Next
                If lines.Count > 0 Then sb.Append(" Models: ").Append(String.Join("; ", lines)).Append(".")
            End If
            Return sb.ToString()
        End Function
    End Class

End Namespace

Namespace Work

    ''' <summary>The agent's own task queue. Follow-up tasks always land in the same agent's queue.</summary>
    Public NotInheritable Class AgentTaskQueue
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _agentId As String
        Private ReadOnly _wake As Action

        Friend Sub New(tasks As TaskRepository, agentId As String, wake As Action)
            _tasks = tasks
            _agentId = agentId
            _wake = wake
        End Sub

        Public Async Function CreateFollowUpAsync(parent As AgentTask, req As NewTaskRequest, maxChildren As Integer) As Task(Of AgentTask)
            If parent IsNot Nothing Then
                Dim children = Await _tasks.CountChildrenAsync(parent.Id).ConfigureAwait(False)
                If children >= maxChildren Then Throw New InvalidOperationException($"This task already created {children} child tasks (limit {maxChildren}).")
            End If
            Dim now = DateTime.UtcNow
            Dim t As New AgentTask With {
                .Id = Ids.NewId("tsk"), .AgentId = _agentId, .Name = TextUtil.Truncate(req.Name, 200), .Description = req.Description, .Kind = req.Kind,
                .Handler = If(req.Handler, "reasoning"), .Purpose = req.Purpose, .Priority = req.Priority, .Payload = req.Payload,
                .MaxRetries = If(req.MaxRetries, 2), .NextRunUtc = req.NextRunUtc, .CreatedUtc = now, .UpdatedUtc = now,
                .ParentTaskId = parent?.Id, .ParentAgentId = If(parent Is Nothing, Nothing, _agentId), .CorrelationId = If(parent?.CorrelationId, Ids.NewId("cor")),
                .DelegationDepth = If(parent Is Nothing, 0, parent.DelegationDepth), .ChainLength = If(parent Is Nothing, 0, parent.ChainLength),
                .Ancestry = If(parent Is Nothing, New List(Of String), New List(Of String)(parent.Ancestry)), .CreatedBy = _agentId,
                .IdempotencyKey = req.IdempotencyKey}
            If Not Await _tasks.InsertAsync(t).ConfigureAwait(False) Then Throw New InvalidOperationException("A task with the same idempotency key already exists.")
            Await _tasks.AddHistoryAsync(t.Id, _agentId, Nothing, AgentTaskStatus.Pending, "Follow-up created by agent").ConfigureAwait(False)
            _wake?.Invoke()
            Return t
        End Function

        Public Function GetAsync(taskId As String) As Task(Of AgentTask)
            Return _tasks.GetForAgentAsync(_agentId, taskId)
        End Function
    End Class

End Namespace

Namespace Approvals

    Public NotInheritable Class ScopedApprovals
        Private ReadOnly _service As ApprovalService
        Private ReadOnly _agentId As String

        Friend Sub New(service As ApprovalService, agentId As String)
            _service = service
            _agentId = agentId
        End Sub

        ''' <summary>
        ''' Asks for a decision. The agent's policy may decide at once (the returned record is then no longer Pending) or send it
        ''' to its supervisor agent.
        ''' </summary>
        Public Function AskAsync(taskId As String, toolCallId As String, kind As ApprovalKind, title As String, question As String, context As String, expiresUtc As DateTime?,
                                 Optional id As String = Nothing) As Task(Of ApprovalRecord)
            Return _service.CreateAsync(_agentId, taskId, toolCallId, kind, title, question, context, expiresUtc, id)
        End Function
    End Class

End Namespace
