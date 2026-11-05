Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Agents

    ''' <summary>
    ''' Agent life and death. An agent whose money runs out dies: its loop stops, its running task is abandoned, its
    ''' subagents end, and it stays dead (also across restarts) until an administrator credits its wallet - a credit that
    ''' lifts the balance above the threshold revives it. Agents that never had money are exempt.
    ''' </summary>
    Partial Public NotInheritable Class AgentManager

        ''' <summary>Review and delivery of finished tasks (set by the host).</summary>
        Public Property Outcomes As OutcomeService
        ''' <summary>Posts results back to the chat a task was handed over from (set by the host).</summary>
        Public Property ChatReports As ChatReportService
        ''' <summary>Supervisor decisions, evaluations, assigned work and learning from failure (set by the host).</summary>
        Public Property Autonomy As AutonomyService

        Private ReadOnly Property ThresholdMicros As Long
            Get
                Return TextUtil.DollarsToMicros(_options.Economy.BankruptcyThreshold)
            End Get
        End Property

        Public Function IsDead(agentId As String) As Boolean
            Dim d As String = Nothing
            Return agentId IsNot Nothing AndAlso _desiredStates.TryGetValue(agentId, d) AndAlso d = "Dead"
        End Function

        ''' <summary>Wallet hook: every balance change decides life or death (runs off the caller's thread).</summary>
        Friend Sub OnBalanceChanged(agentId As String, balanceMicros As Long)
            If agentId Is Nothing OrElse Not _runtimes.ContainsKey(agentId) Then Return
            If balanceMicros < ThresholdMicros Then
                If Not _options.Economy.BankruptcyEndsAgent OrElse IsDead(agentId) Then Return
                Dim ignored = Task.Run(Function() KillAsync(agentId))
            ElseIf IsDead(agentId) Then
                Dim ignored = Task.Run(Function() ReviveAsync(agentId, "its wallet was funded"))
            End If
        End Sub

        Private ReadOnly _deathLock As New Threading.SemaphoreSlim(1, 1)

        ''' <summary>Ends an agent that ran out of money. Safe to call repeatedly.</summary>
        Public Async Function KillAsync(agentId As String) As Task(Of Boolean)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) OrElse r.Context.Profile.IsSubagent Then Return False
            Await _deathLock.WaitAsync().ConfigureAwait(False)
            Try
                If IsDead(agentId) Then Return False
                Dim w = Await _wallets.GetAsync(agentId).ConfigureAwait(False)
                If w Is Nothing OrElse w.LifetimeCreditMicros <= 0 OrElse w.BalanceMicros >= ThresholdMicros Then Return False
                _desiredStates(agentId) = "Dead"
                Await _agentsRepo.SetDesiredStateAsync(agentId, "Dead").ConfigureAwait(False)
            Finally
                _deathLock.Release()
            End Try
            Dim current = r.Context.CurrentTaskId
            If current IsNot Nothing Then r.CancelCurrentTask(current, "cancel:The agent died: its money ran out.")
            Try
                Await CancelAllSubagentsOfAsync(agentId, "Parent agent died (out of money)").ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Agents", "Could not end the subagents of a dead agent: " & ex.Message, agentId)
            End Try
            Await r.StopAsync(TimeSpan.FromSeconds(Math.Max(5, _options.Host.ShutdownTimeoutSeconds))).ConfigureAwait(False)
            r.MarkDead("out of money")
            _events.Emit(AgentEventType.AgentDied, $"{agentId} died: its balance fell below {TextUtil.FormatMicros(ThresholdMicros)}. Credit its wallet to revive it.",
                         agentId, level:=EventLevel.Error)
            Return True
        End Function

        ''' <summary>Brings a dead agent back once it has money again.</summary>
        Public Async Function ReviveAsync(agentId As String, reason As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If Not IsDead(agentId) Then Return AgentOperationResult.Ok("The agent is alive.")
            Dim w = Await _wallets.GetAsync(agentId).ConfigureAwait(False)
            If w Is Nothing OrElse w.BalanceMicros < ThresholdMicros Then
                Return AgentOperationResult.Fail("OUT_OF_MONEY", $"The agent is dead: its balance is below {TextUtil.FormatMicros(ThresholdMicros)}. Credit its wallet first.")
            End If
            _desiredStates(agentId) = "Running"
            Await _agentsRepo.SetDesiredStateAsync(agentId, "Running").ConfigureAwait(False)
            If Not r.Context.Profile.Enabled OrElse Not _acceptingWork Then Return AgentOperationResult.Ok("Revived (it starts when it is enabled).")
            r.Context.RestartTimestamps.Clear()
            r.Context.Unhealthy = False
            r.Context.PauseGate.Open()
            r.Start(_hostToken)
            _events.Emit(AgentEventType.AgentRevived, $"{agentId} is alive again: {reason} ({TextUtil.FormatMicros(w.BalanceMicros)}).", agentId)
            Return AgentOperationResult.Ok("Revived.")
        End Function

        ''' <summary>Host-written memory for an agent (feedback from reviews, ratings and awards).</summary>
        Public Async Function RememberAsync(agentId As String, text As String, importance As Integer, sourceTaskId As String) As Task
            If _scopes.MemoryStore Is Nothing OrElse Not Exists(agentId) Then Return
            Await _scopes.MemoryStore.SaveAsync(agentId, MemoryCategory.Lesson, TextUtil.Truncate(text, 2000), "feedback", Math.Clamp(importance, 1, 5), sourceTaskId,
                                                TimeSpan.FromDays(365)).ConfigureAwait(False)
        End Function
    End Class

End Namespace
