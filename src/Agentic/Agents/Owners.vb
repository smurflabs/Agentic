Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports Agentic.Events
Imports Agentic.Logging

Namespace Agents

    ''' <summary>
    ''' Who pays for an agent. An agent belongs to an account (Nothing = the administrators). Its cloud model use is charged
    ''' to its own wallet, which works as its spending limit, and to its owner's account balance. When a User account's
    ''' balance runs out, all of its agents stop: the running task is cancelled, subagents end and the agent waits, paused,
    ''' until money is added. Administrators' agents never stop for money.
    ''' </summary>
    Partial Public NotInheritable Class AgentManager

        Private ReadOnly _owners As New ConcurrentDictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _unfunded As New ConcurrentDictionary(Of String, Byte)(StringComparer.OrdinalIgnoreCase)

        ''' <summary>The account that pays for the agent (a subagent's is its parent's), or Nothing for the administrators.</summary>
        Public Function OwnerOf(agentId As String) As String
            If agentId Is Nothing Then Return Nothing
            Dim p = _profiles.TryGetProfile(agentId)
            If p IsNot Nothing AndAlso p.IsSubagent AndAlso p.OwnerAgentId IsNot Nothing Then agentId = p.OwnerAgentId
            Dim owner As String = Nothing
            Return If(_owners.TryGetValue(agentId, owner), owner, Nothing)
        End Function

        ''' <summary>Every account that owns an agent.</summary>
        Public Function Owners() As List(Of String)
            Return _owners.Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        End Function

        ''' <summary>Gives an agent to an account (Nothing = the administrators).</summary>
        Public Async Function SetOwnerAsync(agentId As String, owner As String) As Task
            owner = If(String.IsNullOrWhiteSpace(owner), Nothing, owner.Trim())
            Await _agentsRepo.SetAgentOwnerAsync(agentId, owner).ConfigureAwait(False)
            Dim previous As String = Nothing
            If owner Is Nothing Then _owners.TryRemove(agentId, previous) Else _owners(agentId) = owner
            ' A new owner (or the administrators) may have money: the agent's funding is decided again by the caller.
            If owner Is Nothing AndAlso _unfunded.ContainsKey(agentId) Then Await ResumeFundedAsync(agentId).ConfigureAwait(False)
        End Function

        Public Function IsUnfunded(agentId As String) As Boolean
            Return agentId IsNot Nothing AndAlso _unfunded.ContainsKey(agentId)
        End Function

        Private Function UnfundedResult(agentId As String) As AgentOperationResult
            Return AgentOperationResult.Fail("OWNER_OUT_OF_MONEY", $"{agentId} is stopped: the balance of its owner {OwnerOf(agentId)} is empty. It continues by itself once money is added to that account.")
        End Function

        ''' <summary>Before the agents start: the agents of these accounts wait (their owners have no money).</summary>
        Public Sub MarkUnfundedOwner(username As String)
            For Each kv In _owners.Where(Function(x) String.Equals(x.Value, username, StringComparison.OrdinalIgnoreCase)).ToList()
                _unfunded.TryAdd(kv.Key, 0)
            Next
        End Sub

        ''' <summary>
        ''' An account's balance changed (charged = False for administrators): its agents stop at 0 or below and continue when
        ''' there is money again. Runs off the caller's thread.
        ''' </summary>
        Public Sub OnAccountBalance(username As String, balanceMicros As Long, charged As Boolean)
            Dim empty = charged AndAlso balanceMicros <= 0
            For Each kv In _owners.Where(Function(x) String.Equals(x.Value, username, StringComparison.OrdinalIgnoreCase)).ToList()
                Dim agentId = kv.Key
                If empty Then
                    If Not _unfunded.ContainsKey(agentId) Then
                        Dim ignored = Task.Run(Function() SuspendUnfundedAsync(agentId, username))
                    End If
                ElseIf _unfunded.ContainsKey(agentId) Then
                    Dim ignored = Task.Run(Function() ResumeFundedAsync(agentId))
                End If
            Next
        End Sub

        Private Async Function SuspendUnfundedAsync(agentId As String, owner As String) As Task
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) OrElse r.Context.Profile.IsSubagent Then Return
            If Not _unfunded.TryAdd(agentId, 0) Then Return
            r.Pause()
            Dim current = r.Context.CurrentTaskId
            If current IsNot Nothing Then r.CancelCurrentTask(current, "cancel:Stopped: the owner's account balance is empty.")
            Try
                Await CancelAllSubagentsOfAsync(agentId, "The owner's account balance is empty").ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Agents", "Could not end the subagents of an agent whose owner has no money: " & ex.Message, agentId)
            End Try
            _events.Emit(AgentEventType.BudgetInsufficient, $"{agentId} stopped: the balance of its owner {owner} is empty. It continues when money is added to that account.",
                         agentId, level:=EventLevel.Warning, data:=New Dictionary(Of String, Object) From {{"owner", owner}, {"result", "OwnerOutOfMoney"}})
        End Function

        Private Async Function ResumeFundedAsync(agentId As String) As Task
            Dim removed As Byte
            If Not _unfunded.TryRemove(agentId, removed) Then Return
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return
            Dim desired As String = Nothing
            _desiredStates.TryGetValue(agentId, desired)
            ' Only an agent that should be running continues: one an administrator paused or stopped stays so.
            If Not IsDead(agentId) AndAlso (desired Is Nothing OrElse String.Equals(desired, "Running", StringComparison.OrdinalIgnoreCase)) AndAlso r.IsLoopRunning Then r.Resume()
            _events.Emit(AgentEventType.AgentStateChanged, $"{agentId} continues: its owner has money again.", agentId)
            Await Task.CompletedTask.ConfigureAwait(False)
        End Function
    End Class

End Namespace
