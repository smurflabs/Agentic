Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Agents

    ''' <summary>Shared host services used by every agent loop (no agent-specific state lives here).</summary>
    Friend NotInheritable Class RuntimeServices
        Public Property Tasks As TaskRepository
        Public Property Executor As TaskExecutor
        Public Property Mailbox As MailboxProcessor
        Public Property Events As IAgentEventBus
        Public Property Log As ILog
        Public Property Clock As IClock
        Public Property Supervision As SupervisionOptions
        Public Property Scheduling As SchedulingOptions
        Public Property InstanceId As String
    End Class

    ''' <summary>
    ''' Private runtime state of one agent. Never handed to other agents, tools or the HTTP layer; the outside world
    ''' only sees immutable <see cref="AgentStatusSnapshot"/> copies.
    ''' </summary>
    Friend NotInheritable Class AgentRuntimeContext
        Public ReadOnly Property AgentId As String
        Public ReadOnly Property Scope As AgentScope
        Public ReadOnly Property WakeSignal As New AsyncSignal()
        Public ReadOnly Property PauseGate As New AsyncGate(True)

        Private _profile As EffectiveProfile
        Private _state As Integer = AgentState.Stopped
        Private ReadOnly _lock As New Object()

        Public Property StateSinceUtc As DateTime = DateTime.UtcNow
        Public Property StartedUtc As DateTime?
        Public Property LastHeartbeatUtc As DateTime?
        Public Property LastProgressUtc As DateTime?
        Public Property CurrentOperation As String
        Public Property CurrentOperationStartedUtc As DateTime?
        Public Property CurrentTaskId As String
        Public Property CurrentTaskName As String
        Public Property CurrentTaskCts As CancellationTokenSource
        Public Property CancelReason As String
        Public Property LastErrorMessage As String
        Public Property LastErrorUtc As DateTime?
        Public ReadOnly Property RestartTimestamps As New List(Of DateTime)
        Public Property Unhealthy As Boolean

        Public Sub New(agentId As String, profile As EffectiveProfile, scope As AgentScope)
            Me.AgentId = agentId
            _profile = profile
            Me.Scope = scope
        End Sub

        Public Property Profile As EffectiveProfile
            Get
                Return Volatile.Read(_profile)
            End Get
            Set(value As EffectiveProfile)
                Volatile.Write(_profile, value)
            End Set
        End Property

        Public ReadOnly Property State As AgentState
            Get
                Return CType(Volatile.Read(_state), AgentState)
            End Get
        End Property

        ''' <summary>Sets state; returns the previous state.</summary>
        Public Function SetState(value As AgentState) As AgentState
            Dim prev = CType(Interlocked.Exchange(_state, CInt(value)), AgentState)
            If prev <> value Then StateSinceUtc = DateTime.UtcNow
            Return prev
        End Function

        Public Sub Heartbeat()
            LastHeartbeatUtc = DateTime.UtcNow
        End Sub

        Public Sub ReportProgress(operation As String)
            Dim now = DateTime.UtcNow
            SyncLock _lock
                CurrentOperation = operation
                CurrentOperationStartedUtc = now
                LastProgressUtc = now
                LastHeartbeatUtc = now
            End SyncLock
        End Sub
    End Class

    ''' <summary>
    ''' One agent's long-running supervised worker loop: heartbeat → pause gate → mailbox → expired waits → next task
    ''' → execute → repeat, waiting on a wake signal (never spinning, never Thread.Sleep). Faults restart the loop with
    ''' exponential backoff; repeated faults mark the agent Unhealthy without affecting any other agent.
    ''' </summary>
    Friend NotInheritable Class AgentRuntime
        Private ReadOnly _ctx As AgentRuntimeContext
        Private ReadOnly _svc As RuntimeServices
        Private _loopCts As CancellationTokenSource
        Private _loopTask As Task = Task.CompletedTask
        Private ReadOnly _lifecycleLock As New Object()
        ''' <summary>
        ''' Which loop is current. A loop told to stop that does not (stuck in a call that ignores cancellation) is replaced by a
        ''' new one; whatever the old loop does when it finally returns (state changes, task bookkeeping) is ignored.
        ''' </summary>
        Private _generation As Integer

        Public Sub New(ctx As AgentRuntimeContext, services As RuntimeServices)
            _ctx = ctx
            _svc = services
        End Sub

        Public ReadOnly Property Context As AgentRuntimeContext
            Get
                Return _ctx
            End Get
        End Property

        Public ReadOnly Property IsLoopRunning As Boolean
            Get
                Return Not _loopTask.IsCompleted
            End Get
        End Property

        Private Function IsCurrent(gen As Integer) As Boolean
            Return gen = Volatile.Read(_generation)
        End Function

        ''' <summary>Sets the agent's state; a loop passes its generation so a replaced loop cannot change it any more.</summary>
        Private Sub ChangeState(state As AgentState, Optional reason As String = Nothing, Optional gen As Integer = -1)
            If gen >= 0 AndAlso Not IsCurrent(gen) Then Return
            Dim prev = _ctx.SetState(state)
            If prev <> state Then
                _svc.Events.Emit(AgentEventType.AgentStateChanged, $"{_ctx.AgentId}: {prev} → {state}" & If(reason Is Nothing, "", $" ({reason})"), _ctx.AgentId,
                                 level:=If(state = AgentState.Error OrElse state = AgentState.Unhealthy OrElse state = AgentState.Stuck, EventLevel.Warning, EventLevel.Info),
                                 data:=New Dictionary(Of String, Object) From {{"from", prev.ToString()}, {"to", state.ToString()}})
            End If
        End Sub

        Public Sub Start(hostToken As CancellationToken)
            SyncLock _lifecycleLock
                ' A running loop keeps running - unless it was told to stop and is stuck (the watchdog's restart of a hung
                ' loop, or a restart whose stop timed out): then a new loop takes over and the old one is abandoned.
                Dim stuck = IsLoopRunning AndAlso _loopCts IsNot Nothing AndAlso _loopCts.IsCancellationRequested
                If IsLoopRunning AndAlso Not stuck Then Return
                ' The abandoned loop still watches its own token, so it is not disposed under it.
                If Not stuck Then _loopCts?.Dispose()
                _loopCts = CancellationTokenSource.CreateLinkedTokenSource(hostToken)
                Dim gen = Interlocked.Increment(_generation)
                _ctx.Unhealthy = False
                _ctx.StartedUtc = DateTime.UtcNow
                If stuck Then
                    _ctx.CurrentTaskId = Nothing : _ctx.CurrentTaskName = Nothing : _ctx.CurrentTaskCts = Nothing : _ctx.CurrentOperation = Nothing
                    _svc.Log.Warn("Runtime", "The previous loop did not stop; a new loop takes over and the old one is abandoned.", _ctx.AgentId)
                End If
                ChangeState(AgentState.Starting)
                _loopTask = RunSupervisedAsync(_loopCts.Token, gen)
            End SyncLock
        End Sub

        Public Async Function StopAsync(timeout As TimeSpan) As Task
            Dim t As Task
            SyncLock _lifecycleLock
                If Not IsLoopRunning Then
                    If _ctx.State <> AgentState.Unhealthy AndAlso _ctx.State <> AgentState.Disabled AndAlso _ctx.State <> AgentState.Dead Then ChangeState(AgentState.Stopped)
                    Return
                End If
                ChangeState(AgentState.Stopping)
                _loopCts.Cancel()
                _ctx.PauseGate.Open()
                t = _loopTask
            End SyncLock
            Try
                Await t.WaitAsync(timeout).ConfigureAwait(False)
            Catch ex As TimeoutException
                _svc.Log.Warn("Runtime", $"Agent loop did not stop within {timeout.TotalSeconds:0}s", _ctx.AgentId)
            Catch
            End Try
            If _ctx.State <> AgentState.Unhealthy Then ChangeState(AgentState.Stopped)
        End Function

        ''' <summary>After the loop stopped because the agent ran out of money.</summary>
        Public Sub MarkDead(reason As String)
            ChangeState(AgentState.Dead, reason)
        End Sub

        Public Sub Pause()
            _ctx.PauseGate.Close()
            _ctx.WakeSignal.Set()
        End Sub

        Public Sub [Resume]()
            _ctx.PauseGate.Open()
            _ctx.WakeSignal.Set()
        End Sub

        Public Sub Wake()
            _ctx.WakeSignal.Set()
        End Sub

        Public Function CancelCurrentTask(taskId As String, reason As String) As Boolean
            If taskId IsNot Nothing AndAlso Not String.Equals(_ctx.CurrentTaskId, taskId, StringComparison.Ordinal) Then Return False
            Dim cts = _ctx.CurrentTaskCts
            If cts Is Nothing Then Return False
            _ctx.CancelReason = reason
            Try
                cts.Cancel()
            Catch ex As ObjectDisposedException
                Return False
            End Try
            Return True
        End Function

        Private Async Function RunSupervisedAsync(ct As CancellationToken, gen As Integer) As Task
            Await Task.Yield()
            Dim sup = _svc.Supervision
            While Not ct.IsCancellationRequested
                Dim fault As Exception = Nothing
                Try
                    Await RunLoopAsync(ct, gen).ConfigureAwait(False)
                    Exit While
                Catch ex As OperationCanceledException When ct.IsCancellationRequested
                    Exit While
                Catch ex As Exception
                    fault = ex
                End Try
                ' A loop that was replaced while it was stuck just ends.
                If Not IsCurrent(gen) Then Exit While

                ' ---- Fault isolation: only this agent is affected ----
                _ctx.LastErrorMessage = fault.Message
                _ctx.LastErrorUtc = DateTime.UtcNow
                _svc.Log.Err("Runtime", $"Agent loop faulted: {fault.Message}", fault, _ctx.AgentId)
                _svc.Events.Emit(AgentEventType.ErrorOccurred, $"Agent {_ctx.AgentId} faulted: {TextUtil.Truncate(fault.Message, 200)}", _ctx.AgentId, level:=EventLevel.Error)
                ChangeState(AgentState.Error, fault.Message, gen)
                If Not sup.AutoRestart Then Exit While

                Dim now = DateTime.UtcNow
                _ctx.RestartTimestamps.RemoveAll(Function(x) now - x > TimeSpan.FromMinutes(sup.RestartWindowMinutes))
                _ctx.RestartTimestamps.Add(now)
                If _ctx.RestartTimestamps.Count > sup.MaxRestartAttempts Then
                    _ctx.Unhealthy = True
                    ChangeState(AgentState.Unhealthy, $"{_ctx.RestartTimestamps.Count} faults within {sup.RestartWindowMinutes} min", gen)
                    _svc.Events.Emit(AgentEventType.AgentUnhealthy, $"Agent {_ctx.AgentId} marked unhealthy after repeated failures; administrator intervention required.",
                                     _ctx.AgentId, level:=EventLevel.Error)
                    Exit While
                End If
                Dim delay = Backoff.Compute(_ctx.RestartTimestamps.Count - 1, TimeSpan.FromSeconds(sup.RestartDelaySeconds), TimeSpan.FromSeconds(sup.MaxBackoffSeconds), sup.ExponentialBackoff)
                _svc.Log.Info("Runtime", $"Restarting agent loop in {delay.TotalSeconds:0}s (attempt {_ctx.RestartTimestamps.Count}/{sup.MaxRestartAttempts})", _ctx.AgentId)
                Try
                    Await Task.Delay(delay, ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                ChangeState(AgentState.Starting, "restart", gen)
            End While
            If _ctx.State <> AgentState.Unhealthy AndAlso _ctx.State <> AgentState.Error Then ChangeState(AgentState.Stopped, gen:=gen)
        End Function

        Private Function StatusText() As String
            Dim p = _ctx.Profile
            Return $"{p.Name} ({p.Role}) is {_ctx.State}" & If(_ctx.CurrentTaskName Is Nothing, ".", $"; working on '{_ctx.CurrentTaskName}' ({_ctx.CurrentOperation}).")
        End Function

        Private Async Function RunLoopAsync(ct As CancellationToken, gen As Integer) As Task
            ' The task bookkeeping of a loop that was replaced (stuck, then abandoned) must not touch the new loop's task.
            Dim hooks As New ExecutionHooks With {
                .AgentId = _ctx.AgentId,
                .Profile = Function() _ctx.Profile,
                .Scope = _ctx.Scope,
                .BeginTask = Sub(t, cts)
                                 If Not IsCurrent(gen) Then Return
                                 _ctx.CurrentTaskId = t.Id
                                 _ctx.CurrentTaskName = t.Name
                                 _ctx.CurrentTaskCts = cts
                                 _ctx.CancelReason = Nothing
                                 _ctx.ReportProgress("Starting " & t.Name)
                             End Sub,
                .EndTask = Sub()
                               If Not IsCurrent(gen) Then Return
                               _ctx.CurrentTaskId = Nothing
                               _ctx.CurrentTaskName = Nothing
                               _ctx.CurrentTaskCts = Nothing
                               _ctx.CurrentOperation = Nothing
                               _ctx.CurrentOperationStartedUtc = Nothing
                           End Sub,
                .Progress = Sub(op)
                                If IsCurrent(gen) Then _ctx.ReportProgress(op)
                            End Sub,
                .CancelReason = Function() _ctx.CancelReason
            }
            ChangeState(AgentState.Idle, gen:=gen)
            While Not ct.IsCancellationRequested
                _ctx.Heartbeat()
                Dim profile = _ctx.Profile
                If Not profile.Enabled Then
                    ChangeState(AgentState.Disabled, gen:=gen)
                    Await _ctx.WakeSignal.WaitAsync(TimeSpan.FromMinutes(1), ct).ConfigureAwait(False)
                    Continue While
                End If
                If Not _ctx.PauseGate.IsOpen Then
                    ChangeState(AgentState.Paused, gen:=gen)
                    Await _ctx.PauseGate.WaitAsync(ct).ConfigureAwait(False)
                    ChangeState(AgentState.Idle, gen:=gen)
                    Continue While
                End If

                ' 1. Incoming agent messages.
                _ctx.CurrentOperation = "Checking mailbox"
                Await _svc.Mailbox.ProcessAsync(_ctx.AgentId, profile, _ctx.Scope, AddressOf StatusText, ct).ConfigureAwait(False)

                ' 2. Waiting tasks whose deadline passed.
                Await _svc.Executor.ResumeExpiredWaitsAsync(_ctx.AgentId).ConfigureAwait(False)

                ' 3. Next task.
                Dim now = _svc.Clock.UtcNow
                Dim offPeakActive = _svc.Scheduling Is Nothing OrElse _svc.Scheduling.IsOffPeakNow(now)
                Dim deadlineWindow = If(_svc.Scheduling Is Nothing, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(Math.Max(1, _svc.Scheduling.DeadlineWindowMinutes)))
                Dim t = Await _svc.Tasks.ClaimNextAsync(_ctx.AgentId, now, _svc.InstanceId, now.AddSeconds(profile.MaxTaskDurationSeconds + 60), offPeakActive, deadlineWindow).ConfigureAwait(False)
                If t Is Nothing Then
                    _ctx.CurrentOperation = Nothing
                    Dim waiting = Await _svc.Tasks.ListAsync(_ctx.AgentId, {AgentTaskStatus.Waiting}, 1).ConfigureAwait(False)
                    ChangeState(If(waiting.Count > 0, AgentState.Waiting, AgentState.Idle), gen:=gen)
                    Dim idle = TimeSpan.FromSeconds(Math.Max(1, profile.IdleDelaySeconds))
                    Dim nextDue = Await NextDueDelayAsync(now).ConfigureAwait(False)
                    If nextDue.HasValue AndAlso nextDue.Value < idle Then idle = nextDue.Value
                    Await _ctx.WakeSignal.WaitAsync(idle, ct).ConfigureAwait(False)
                    Continue While
                End If

                ChangeState(AgentState.Running, gen:=gen)
                Await _svc.Executor.ExecuteAsync(hooks, t, ct).ConfigureAwait(False)
                ChangeState(AgentState.Idle, gen:=gen)
            End While
        End Function

        Private Async Function NextDueDelayAsync(now As DateTime) As Task(Of TimeSpan?)
            Dim v = Await _svc.Tasks.NextDueUtcAsync(_ctx.AgentId).ConfigureAwait(False)
            If Not v.HasValue Then Return Nothing
            Dim d = v.Value - now
            If d < TimeSpan.FromMilliseconds(200) Then d = TimeSpan.FromMilliseconds(200)
            Return d
        End Function
    End Class

End Namespace
