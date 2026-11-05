Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net.Http
Imports System.Text.Json
Imports System.Threading
Imports Agentic.Approvals
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Mcp
Imports Agentic.Persistence
Imports Agentic.Tools
Imports Agentic.Utilities

Namespace Work

    Public Enum OutcomeKind
        Completed
        Failed
        Retry
        Park
        Blocked
        Cancelled
        Interrupted
    End Enum

    Public NotInheritable Class TaskOutcome
        Public Property Kind As OutcomeKind
        Public Property Result As TaskResultContract
        Public Property ErrorMessage As String
        Public Property RetryAfter As TimeSpan?
        Public Property WaitKeys As New List(Of String)
        Public Property WaitDeadlineUtc As DateTime?
        Public Property EscalateOnRetry As Boolean
        ''' <summary>Whether a TaskResult memory should be written for this outcome.</summary>
        Public Property RecordMemory As Boolean = True

        Public Shared Function Done(result As TaskResultContract, Optional recordMemory As Boolean = True) As TaskOutcome
            Return New TaskOutcome With {.Kind = OutcomeKind.Completed, .Result = result, .RecordMemory = recordMemory}
        End Function

        Public Shared Function Fail(message As String) As TaskOutcome
            Return New TaskOutcome With {.Kind = OutcomeKind.Failed, .ErrorMessage = message, .Result = TaskResultContract.Fail(message)}
        End Function

        Public Shared Function RetryLater(message As String, Optional after As TimeSpan? = Nothing, Optional escalate As Boolean = False) As TaskOutcome
            Return New TaskOutcome With {.Kind = OutcomeKind.Retry, .ErrorMessage = message, .RetryAfter = after, .EscalateOnRetry = escalate}
        End Function

        ''' <summary>A long task that is making progress gives way after its time slice; it goes on from its checkpoint (not a failed attempt).</summary>
        Public Property Yielded As Boolean

        Public Shared Function GiveWay(message As String) As TaskOutcome
            Return New TaskOutcome With {.Kind = OutcomeKind.Interrupted, .ErrorMessage = message, .Yielded = True}
        End Function

        ''' <summary>The AI cannot be used right now (down, busy, quota or budget used up): try again later without using up a retry.</summary>
        Public Property WaitingForAi As Boolean

        Public Shared Function WaitForAi(message As String, after As TimeSpan) As TaskOutcome
            Return New TaskOutcome With {.Kind = OutcomeKind.Retry, .ErrorMessage = message, .RetryAfter = after, .WaitingForAi = True}
        End Function
    End Class

    ''' <summary>Everything a handler needs for one task execution (all agent-scoped).</summary>
    Friend NotInheritable Class TaskExecutionContext
        Public Property AgentId As String
        Public Property Profile As EffectiveProfile
        Public Property Scope As AgentScope
        Public Property CurrentTask As AgentTask
        Public Property CancellationToken As CancellationToken
        Public Property Progress As Action(Of String)
        Public Property SaveCheckpointAsync As Func(Of TaskCheckpoint, Task)
        ''' <summary>Whether another task of the agent is ready now (Nothing: assume so, and give way as before).</summary>
        Public Property OthersWaitingAsync As Func(Of Task(Of Boolean))
        ''' <summary>Starts the time slice over (Tasks.MaxTaskDurationSeconds from now) when nothing else waits for the agent.</summary>
        Public Property ExtendSlice As Action
    End Class

    Friend Interface ITaskHandler
        ReadOnly Property Name As String
        Function ExecuteAsync(ctx As TaskExecutionContext) As Task(Of TaskOutcome)
    End Interface

    ''' <summary>Host hooks around task lifecycles (subagent completion/cascade, waits that resolved before the task parked).</summary>
    Friend Interface ITaskObserver
        ''' <summary>Called once a task reached Completed, Failed or Cancelled (must not block).</summary>
        Sub OnTaskFinished(t As AgentTask)
        ''' <summary>Returns a resolution for a wait key whose result already exists (e.g. a subagent that already finished), or Nothing.</summary>
        Function TryLateResolveAsync(agentId As String, waitKey As String) As Task(Of String)
    End Interface

    ' ------------------------------------------------------------------------------------------------
    ' Deterministic handlers (no AI)
    ' ------------------------------------------------------------------------------------------------

    Friend NotInheritable Class NoopTaskHandler
        Implements ITaskHandler

        Public ReadOnly Property Name As String = "noop" Implements ITaskHandler.Name

        Public Function ExecuteAsync(ctx As TaskExecutionContext) As Task(Of TaskOutcome) Implements ITaskHandler.ExecuteAsync
            Return Task.FromResult(TaskOutcome.Done(TaskResultContract.Ok("No operation."), recordMemory:=False))
        End Function
    End Class

    ''' <summary>
    ''' Polls a URL deterministically. Only when the observed state changes does it record an observation and
    ''' (optionally) create a reasoning follow-up task — AI is used only when something actually happened.
    ''' Parameters: Url, ExpectStatus (default 200), TimeoutSeconds, OnChange ("reasoning" to create a follow-up).
    ''' </summary>
    Friend NotInheritable Class HttpCheckTaskHandler
        Implements ITaskHandler

        Private Shared ReadOnly Http As New HttpClient(New SocketsHttpHandler With {.PooledConnectionLifetime = TimeSpan.FromMinutes(5)}) With {.Timeout = Timeout.InfiniteTimeSpan}
        Private ReadOnly _lastState As New ConcurrentDictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        ''' <summary>When each check last started an investigation (a flapping endpoint must not start one per flip).</summary>
        Private ReadOnly _lastFollowUp As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)

        Public ReadOnly Property Name As String = "http-check" Implements ITaskHandler.Name

        Public Async Function ExecuteAsync(ctx As TaskExecutionContext) As Task(Of TaskOutcome) Implements ITaskHandler.ExecuteAsync
            Dim p = JsonUtil.ParseElementOrNull(ctx.CurrentTask.Payload)
            If Not p.HasValue Then Return TaskOutcome.Fail("http-check requires a payload with Url.")
            Dim url = JsonUtil.GetString(p.Value, "Url")
            Dim expect = JsonUtil.GetInt(p.Value, "ExpectStatus", 200)
            Dim timeoutSec = JsonUtil.GetInt(p.Value, "TimeoutSeconds", 15)
            Dim onChange = JsonUtil.GetString(p.Value, "OnChange")
            ' OnChangeCooldownMinutes: after an investigation starts, state changes within this time only get recorded (default 30).
            Dim cooldown = JsonUtil.GetInt(p.Value, "OnChangeCooldownMinutes", 30)
            Dim u As Uri = Nothing
            If Not Uri.TryCreate(url, UriKind.Absolute, u) Then Return TaskOutcome.Fail("Invalid Url.")
            If u.Scheme <> Uri.UriSchemeHttp AndAlso u.Scheme <> Uri.UriSchemeHttps Then Return TaskOutcome.Fail("Url must start with http:// or https://.")
            ctx.Progress?.Invoke("GET " & u.Host)
            Dim state As String
            Dim detail As String
            Dim sw = Stopwatch.StartNew()
            Try
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken)
                    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec))
                    Using resp = Await Http.GetAsync(u, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(False)
                        Dim code = CInt(resp.StatusCode)
                        state = If(code = expect, "UP", "UNEXPECTED_" & code.ToString())
                        detail = $"HTTP {code} in {sw.ElapsedMilliseconds}ms"
                    End Using
                End Using
            Catch ex As OperationCanceledException When Not ctx.CancellationToken.IsCancellationRequested
                state = "TIMEOUT"
                detail = $"No response within {timeoutSec}s"
            Catch ex As HttpRequestException
                state = "DOWN"
                detail = ex.Message
            End Try
            Dim key = If(ctx.CurrentTask.ScheduleId, ctx.AgentId & ":" & url)
            Dim previous As String = Nothing
            _lastState.TryGetValue(key, previous)
            _lastState(key) = state
            Dim changed = previous IsNot Nothing AndAlso previous <> state
            Dim firstRun = previous Is Nothing
            Dim summary = $"{u.Host}: {state} ({detail})" & If(changed, $" — changed from {previous}", "")
            If changed Then
                Await ctx.Scope.Memory.SaveMemoryAsync(MemoryCategory.Observation, $"{url} changed state {previous} → {state} at {DateTime.UtcNow:u}. {detail}",
                                                       "http-check", 3, ctx.CurrentTask.Id, TimeSpan.FromDays(30)).ConfigureAwait(False)
                If String.Equals(onChange, "reasoning", StringComparison.OrdinalIgnoreCase) Then
                    ' A flapping endpoint (DOWN, UP, DOWN within minutes) gets one investigation, not one per flip: each re-checks
                    ' the same endpoint and finds it up. The changes in between are still kept as observations.
                    Dim lastStarted As DateTime
                    If cooldown > 0 AndAlso _lastFollowUp.TryGetValue(key, lastStarted) AndAlso DateTime.UtcNow - lastStarted < TimeSpan.FromMinutes(cooldown) Then
                        summary &= $" (no new investigation: one started {Math.Max(1, CInt((DateTime.UtcNow - lastStarted).TotalMinutes))} min ago)"
                    Else
                        _lastFollowUp(key) = DateTime.UtcNow
                        Await ctx.Scope.Queue.CreateFollowUpAsync(ctx.CurrentTask, New NewTaskRequest With {
                            .Name = $"Investigate state change of {u.Host}", .Priority = PriorityLevel.High,
                            .Description = $"The monitored endpoint {url} changed from {previous} to {state} ({detail}). Assess the impact, inform the relevant agents if needed, and record what you found.",
                            .IdempotencyKey = $"httpchg:{ctx.CurrentTask.Id}"}, 10).ConfigureAwait(False)
                    End If
                End If
            End If
            Dim contract = TaskResultContract.Ok(summary, JsonUtil.ParseElementOrNull(JsonUtil.ToJson(New With {.state = state, .previous = previous, .changed = changed, .detail = detail})))
            Return TaskOutcome.Done(contract, recordMemory:=False)
        End Function
    End Class

    ''' <summary>Calls one permitted MCP tool with fixed arguments (no AI). Parameters: Server, Tool, Arguments (JSON).</summary>
    Friend NotInheritable Class McpToolTaskHandler
        Implements ITaskHandler

        Private ReadOnly _mcp As IMcpServerManager

        Public Sub New(mcp As IMcpServerManager)
            _mcp = mcp
        End Sub

        Public ReadOnly Property Name As String = "mcp-tool" Implements ITaskHandler.Name

        Public Async Function ExecuteAsync(ctx As TaskExecutionContext) As Task(Of TaskOutcome) Implements ITaskHandler.ExecuteAsync
            Dim p = JsonUtil.ParseElementOrNull(ctx.CurrentTask.Payload)
            If Not p.HasValue Then Return TaskOutcome.Fail("mcp-tool requires a payload.")
            Dim server = JsonUtil.GetString(p.Value, "Server")
            Dim tool = JsonUtil.GetString(p.Value, "Tool")
            If Not McpToolRegistry.IsAllowed(ctx.Profile, server, tool) Then Return TaskOutcome.Fail($"MCP tool {server}:{tool} is not permitted for this agent.")
            Dim argsText = If(JsonUtil.GetString(p.Value, "Arguments"), "{}")
            Dim args As JsonElement
            If Not JsonUtil.TryParseElement(argsText, args) Then Return TaskOutcome.Fail("Arguments must be JSON.")
            ctx.Progress?.Invoke($"MCP {server}:{tool}")
            Try
                Dim r = Await _mcp.CallToolAsync(server, tool, args, ctx.CancellationToken).ConfigureAwait(False)
                If r.IsError Then Return TaskOutcome.RetryLater("MCP tool returned an error: " & TextUtil.Truncate(r.Text, 500))
                Dim record = String.Equals(JsonUtil.GetString(p.Value, "RecordResult"), "true", StringComparison.OrdinalIgnoreCase)
                Return TaskOutcome.Done(TaskResultContract.Ok(TextUtil.Truncate(If(r.Text, r.StructuredJson), 4000)), recordMemory:=record)
            Catch ex As McpUnavailableException
                Return TaskOutcome.RetryLater(ex.Message, TimeSpan.FromMinutes(2))
            Catch ex As TimeoutException
                Return TaskOutcome.RetryLater(ex.Message)
            End Try
        End Function
    End Class

    ' ------------------------------------------------------------------------------------------------
    ' Executor
    ' ------------------------------------------------------------------------------------------------

    ''' <summary>Runtime hooks the executor needs from the owning agent (no access to the agent object itself).</summary>
    Friend NotInheritable Class ExecutionHooks
        Public Property AgentId As String
        Public Property Profile As Func(Of EffectiveProfile)
        Public Property Scope As AgentScope
        Public Property BeginTask As Action(Of AgentTask, CancellationTokenSource)
        Public Property EndTask As Action
        Public Property Progress As Action(Of String)
        Public Property CancelReason As Func(Of String)
    End Class

    ''' <summary>
    ''' Runs one task with timeout/cancellation, applies the outcome (complete, retry with backoff, park, fail),
    ''' records history/events/memory and sends replies for message-triggered and delegated tasks.
    ''' </summary>
    Friend NotInheritable Class TaskExecutor
        Private ReadOnly _handlers As Dictionary(Of String, ITaskHandler)
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _comms As AgentCommunicationService
        Private ReadOnly _approvals As ApprovalService
        Private ReadOnly _resolver As WaitResolver
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _clock As IClock

        Public Sub New(handlers As IEnumerable(Of ITaskHandler), tasks As TaskRepository, comms As AgentCommunicationService, approvals As ApprovalService,
                       resolver As WaitResolver, events As IAgentEventBus, log As ILog, clock As IClock)
            _handlers = handlers.ToDictionary(Function(h) h.Name, StringComparer.OrdinalIgnoreCase)
            _tasks = tasks
            _comms = comms
            _approvals = approvals
            _resolver = resolver
            _events = events
            _log = log
            _clock = clock
        End Sub

        Public Property Observer As ITaskObserver

        Private Sub NotifyFinished(t As AgentTask)
            Try
                Observer?.OnTaskFinished(t)
            Catch ex As Exception
                _log.Warn("Executor", "Task observer failed: " & ex.Message, t.AgentId, t.Id)
            End Try
        End Sub

        Public Async Function ExecuteAsync(hooks As ExecutionHooks, t As AgentTask, hostCt As CancellationToken) As Task
            Dim profile = hooks.Profile.Invoke()
            Dim handler As ITaskHandler = Nothing
            If Not _handlers.TryGetValue(If(t.Handler, "reasoning"), handler) Then
                Await ApplyAsync(hooks, t, TaskOutcome.Fail($"Unknown task handler '{t.Handler}'."), 0).ConfigureAwait(False)
                Return
            End If

            Dim resumed = t.HasCheckpoint
            _events.Emit(AgentEventType.TaskStarted, If(resumed, "Resumed task: ", "Started task: ") & t.Name, t.AgentId, t.Id, correlationId:=t.CorrelationId,
                         data:=New Dictionary(Of String, Object) From {{"handler", t.Handler}, {"kind", t.Kind.ToString()}, {"attempt", t.RetryCount + 1}})
            Dim sw = Stopwatch.StartNew()
            Dim outcome As TaskOutcome = Nothing
            ' Useful results so far: an attempt cut off at MaxTaskDuration that added some was working, not stuck.
            Dim progressBefore = If(TaskCheckpoint.Parse(t.Checkpoint)?.Progress, 0)
            Using taskCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt)
                taskCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, profile.MaxTaskDurationSeconds)))
                hooks.BeginTask?.Invoke(t, taskCts)
                Dim execCtx As New TaskExecutionContext With {
                    .AgentId = t.AgentId, .Profile = profile, .Scope = hooks.Scope, .CurrentTask = t, .CancellationToken = taskCts.Token,
                    .Progress = hooks.Progress,
                    .SaveCheckpointAsync = Async Function(cp)
                                               t.Checkpoint = cp.ToJson()
                                               Await _tasks.UpdateAsync(t).ConfigureAwait(False)
                                           End Function,
                    .OthersWaitingAsync = Function() _tasks.HasOtherReadyAsync(t.AgentId, t.Id, DateTime.UtcNow),
                    .ExtendSlice = Sub()
                                       Try
                                           taskCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, profile.MaxTaskDurationSeconds)))
                                       Catch ex As ObjectDisposedException
                                       End Try
                                   End Sub}
                Try
                    outcome = Await handler.ExecuteAsync(execCtx).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    If hostCt.IsCancellationRequested Then
                        outcome = New TaskOutcome With {.Kind = OutcomeKind.Interrupted, .ErrorMessage = "Host shutting down; task will resume after restart."}
                    Else
                        Dim reason = hooks.CancelReason?.Invoke()
                        If reason = "admin" Then
                            outcome = New TaskOutcome With {.Kind = OutcomeKind.Cancelled, .ErrorMessage = "Cancelled by administrator."}
                        ElseIf reason IsNot Nothing AndAlso reason.StartsWith("cancel:", StringComparison.Ordinal) Then
                            outcome = New TaskOutcome With {.Kind = OutcomeKind.Cancelled, .ErrorMessage = reason.Substring(7)}
                        ElseIf reason = "stuck" Then
                            outcome = TaskOutcome.RetryLater("Task made no progress and was cancelled as stuck.", escalate:=True)
                        ElseIf If(TaskCheckpoint.Parse(t.Checkpoint)?.Progress, 0) > progressBefore Then
                            ' Its time slice ended in the middle of a step, but it was getting things done: it goes on from its
                            ' checkpoint (the interrupted step is looked at again), without using up a retry or a stronger model.
                            outcome = TaskOutcome.GiveWay($"Paused after its {profile.MaxTaskDurationSeconds}s time slice (Tasks.MaxTaskDurationSeconds) while still making progress; it goes on from its checkpoint.")
                        Else
                            outcome = TaskOutcome.RetryLater($"Task exceeded MaxTaskDuration ({profile.MaxTaskDurationSeconds}s).", escalate:=True)
                        End If
                    End If
                Catch ex As Exception
                    _log.Err("Executor", $"Task {t.Id} handler threw", ex, t.AgentId, t.Id)
                    outcome = TaskOutcome.RetryLater("Unexpected error: " & ex.Message)
                Finally
                    hooks.EndTask?.Invoke()
                End Try
            End Using
            If outcome Is Nothing Then outcome = TaskOutcome.Fail("Handler returned no outcome.")
            Await ApplyAsync(hooks, t, outcome, sw.ElapsedMilliseconds).ConfigureAwait(False)
        End Function

        Private Async Function ApplyAsync(hooks As ExecutionHooks, t As AgentTask, o As TaskOutcome, durationMs As Long) As Task
            Dim profile = hooks.Profile.Invoke()
            Dim now = _clock.UtcNow
            Dim fromStatus = t.Status
            Select Case o.Kind
                Case OutcomeKind.Completed
                    Dim result = If(o.Result, TaskResultContract.Ok("Done."))
                    Await KeepTranscriptAsync(t).ConfigureAwait(False)
                    t.Status = AgentTaskStatus.Completed
                    t.Result = result.ToJson()
                    t.ErrorMessage = Nothing
                    t.CompletedUtc = now
                    t.Checkpoint = Nothing
                    t.WaitingOn.Clear()
                    t.WaitDeadlineUtc = Nothing
                    t.LeaseOwner = Nothing
                    Await _tasks.UpdateAsync(t).ConfigureAwait(False)
                    Await _tasks.AddHistoryAsync(t.Id, t.AgentId, fromStatus, t.Status, TextUtil.Truncate(result.Summary, 500)).ConfigureAwait(False)
                    Try
                        Dim freed = Await _tasks.UnblockDependentsAsync(t.AgentId, t.Id).ConfigureAwait(False)
                        If freed.Count > 0 Then _events.Emit(AgentEventType.TaskProgress, $"Unblocked {freed.Count} dependent task(s).", t.AgentId, t.Id, correlationId:=t.CorrelationId)
                    Catch ex As Exception
                        _log.Warn("Executor", "Unblock dependents failed: " & ex.Message, t.AgentId, t.Id)
                    End Try
                    _events.Emit(If(result.Success, AgentEventType.TaskCompleted, AgentEventType.TaskFailed),
                                 $"{If(result.Success, "Completed", "Finished unsuccessfully")}: {t.Name} — {TextUtil.Truncate(result.Summary, 160)}",
                                 t.AgentId, t.Id, correlationId:=t.CorrelationId, level:=If(result.Success, EventLevel.Info, EventLevel.Warning),
                                 data:=New Dictionary(Of String, Object) From {{"durationMs", durationMs}, {"success", result.Success}, {"costMicros", t.CostMicros}, {"kind", t.Kind.ToString()}})
                    ' Evaluation runs are tests, not the agent's work: they leave no memory behind.
                    Dim isEval = t.CreatedBy IsNot Nothing AndAlso t.CreatedBy.StartsWith("eval:", StringComparison.Ordinal)
                    If o.RecordMemory AndAlso profile.AutoRecordTaskResults AndAlso Not isEval Then
                        Try
                            Await hooks.Scope.Memory.SaveMemoryAsync(MemoryCategory.TaskResult,
                                $"Task '{t.Name}' {If(result.Success, "succeeded", "failed")}: {TextUtil.Truncate(result.Summary, 1500)}",
                                "task-result", If(result.Success, 2, 3), t.Id, TimeSpan.FromDays(90)).ConfigureAwait(False)
                        Catch ex As Exception
                            _log.Warn("Executor", "Failed to record task memory", t.AgentId, t.Id, ex)
                        End Try
                    End If
                    Await ReplyIfNeededAsync(t, result).ConfigureAwait(False)
                    NotifyFinished(t)

                Case OutcomeKind.Failed, OutcomeKind.Cancelled, OutcomeKind.Blocked
                    Dim status = If(o.Kind = OutcomeKind.Cancelled, AgentTaskStatus.Cancelled, If(o.Kind = OutcomeKind.Blocked, AgentTaskStatus.Blocked, AgentTaskStatus.Failed))
                    If status <> AgentTaskStatus.Blocked Then Await KeepTranscriptAsync(t).ConfigureAwait(False)
                    t.Status = status
                    t.ErrorMessage = TextUtil.Truncate(o.ErrorMessage, 4000)
                    t.Result = If(o.Result, TaskResultContract.Fail(If(o.ErrorMessage, status.ToString()))).ToJson()
                    If status <> AgentTaskStatus.Blocked Then t.CompletedUtc = now
                    t.LeaseOwner = Nothing
                    If status <> AgentTaskStatus.Blocked Then t.Checkpoint = Nothing
                    Await _tasks.UpdateAsync(t).ConfigureAwait(False)
                    Await _tasks.AddHistoryAsync(t.Id, t.AgentId, fromStatus, status, o.ErrorMessage).ConfigureAwait(False)
                    _events.Emit(If(status = AgentTaskStatus.Cancelled, AgentEventType.TaskCancelled, AgentEventType.TaskFailed), $"{status}: {t.Name} — {TextUtil.Truncate(o.ErrorMessage, 200)}",
                                 t.AgentId, t.Id, correlationId:=t.CorrelationId, level:=EventLevel.Warning,
                                 data:=New Dictionary(Of String, Object) From {{"durationMs", durationMs}, {"success", False}})
                    If status <> AgentTaskStatus.Blocked Then
                        ' A failure that carries a report (the work done before a turn limit) passes it on to whoever delegated the task.
                        Await ReplyIfNeededAsync(t, If(o.Result IsNot Nothing AndAlso Not o.Result.Success, o.Result, TaskResultContract.Fail(If(o.ErrorMessage, status.ToString())))).ConfigureAwait(False)
                        NotifyFinished(t)
                    End If

                Case OutcomeKind.Retry
                    ' Waiting for the AI (down, busy, its quota or budget used up) is not a failed attempt: no retry is used up.
                    If o.WaitingForAi OrElse t.RetryCount < t.MaxRetries Then
                        If Not o.WaitingForAi Then t.RetryCount += 1
                        t.Status = AgentTaskStatus.Retrying
                        t.ErrorMessage = TextUtil.Truncate(o.ErrorMessage, 4000)
                        Dim delay = If(o.RetryAfter, Backoff.Compute(Math.Max(0, t.RetryCount - 1), TimeSpan.FromSeconds(profile.RetryBaseDelaySeconds), TimeSpan.FromHours(1)))
                        t.NextRunUtc = now.Add(delay)
                        t.LeaseOwner = Nothing
                        If o.EscalateOnRetry AndAlso profile.EscalateOnRetry Then
                            Dim cp = TaskCheckpoint.Parse(t.Checkpoint)
                            If cp IsNot Nothing Then
                                cp.EscalationRequested = True
                                cp.EscalationReason = "Previous attempt failed: " & TextUtil.Truncate(o.ErrorMessage, 300)
                                t.Checkpoint = cp.ToJson()
                            End If
                        End If
                        Await _tasks.UpdateAsync(t).ConfigureAwait(False)
                        Dim attempt = If(o.WaitingForAi, "Waiting for the AI, trying again", $"Retry {t.RetryCount}/{t.MaxRetries}")
                        Await _tasks.AddHistoryAsync(t.Id, t.AgentId, fromStatus, t.Status, $"{attempt} in {delay.TotalSeconds:0}s: {o.ErrorMessage}").ConfigureAwait(False)
                        _events.Emit(AgentEventType.TaskRetrying, $"{attempt} for '{t.Name}' in {delay.TotalSeconds:0}s: {TextUtil.Truncate(o.ErrorMessage, 160)}",
                                     t.AgentId, t.Id, correlationId:=t.CorrelationId, level:=EventLevel.Warning)
                    Else
                        Await ApplyAsync(hooks, t, TaskOutcome.Fail($"Retries exhausted ({t.MaxRetries}): {o.ErrorMessage}"), durationMs).ConfigureAwait(False)
                    End If

                Case OutcomeKind.Park
                    t.Status = AgentTaskStatus.Waiting
                    t.WaitingOn = o.WaitKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                    t.WaitDeadlineUtc = If(o.WaitDeadlineUtc, now.AddHours(1))
                    t.LeaseOwner = Nothing
                    Await _tasks.UpdateAsync(t).ConfigureAwait(False)
                    Await _tasks.AddHistoryAsync(t.Id, t.AgentId, fromStatus, t.Status, "Waiting on " & String.Join(", ", t.WaitingOn)).ConfigureAwait(False)
                    _events.Emit(AgentEventType.TaskWaiting, $"Waiting: {t.Name} ({String.Join(", ", t.WaitingOn)})", t.AgentId, t.Id, correlationId:=t.CorrelationId)
                    ' Close the race where an approval was resolved (or a subagent finished) before the task reached Waiting.
                    For Each key In t.WaitingOn.ToList()
                        If key.StartsWith("approval:", StringComparison.OrdinalIgnoreCase) Then
                            Dim rec = Await _approvals.GetAsync(key.Substring(9)).ConfigureAwait(False)
                            If rec IsNot Nothing AndAlso rec.Status <> ApprovalStatus.Pending Then
                                Await _resolver.ResolveAsync(t.AgentId, key, ResolutionFor(rec)).ConfigureAwait(False)
                            End If
                        ElseIf Observer IsNot Nothing Then
                            Dim late = Await Observer.TryLateResolveAsync(t.AgentId, key).ConfigureAwait(False)
                            If late IsNot Nothing Then Await _resolver.ResolveAsync(t.AgentId, key, late, "Result was already available").ConfigureAwait(False)
                        End If
                    Next

                Case OutcomeKind.Interrupted
                    t.Status = AgentTaskStatus.Pending
                    t.LeaseOwner = Nothing
                    t.ErrorMessage = If(o.Yielded, Nothing, o.ErrorMessage)
                    ' A task that gave way after its time slice is due again at once, behind its agent's other waiting tasks of the
                    ' same priority: it kept its queue place before, so it was picked again at once and they never ran (two
                    ' LedgerLite steps of one agent: the second started only when the first stopped for good, 4 hours later).
                    If o.Yielded Then t.NextRunUtc = now
                    Await _tasks.UpdateAsync(t).ConfigureAwait(False)
                    If o.Yielded Then Await _tasks.ReRankAsync(t.Id).ConfigureAwait(False)
                    Await _tasks.AddHistoryAsync(t.Id, t.AgentId, fromStatus, t.Status, o.ErrorMessage).ConfigureAwait(False)
                    If o.Yielded Then
                        _events.Emit(AgentEventType.TaskProgress, $"Paused '{t.Name}' after {TimeSpan.FromMilliseconds(durationMs).TotalMinutes:0} min of work; it goes on from its checkpoint.",
                                     t.AgentId, t.Id, correlationId:=t.CorrelationId)
                    End If
            End Select
        End Function

        ''' <summary>Reviewed, delivered and review tasks keep their conversation for the reviewer and the Results page.</summary>
        Private Async Function KeepTranscriptAsync(t As AgentTask) As Task
            ' Every finished reasoning task keeps its conversation (shortened), not only reviewed and delivered ones: without it
            ' nobody can see why a task failed once its checkpoint is cleared. Polling tasks have nothing worth keeping.
            If String.IsNullOrEmpty(t.Checkpoint) OrElse t.Kind = TaskKind.Polling Then Return
            Try
                Await _tasks.SaveTranscriptAsync(t.Id, t.AgentId, t.Checkpoint).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Executor", "Could not keep the task transcript: " & ex.Message, t.AgentId, t.Id)
            End Try
        End Function

        Friend Shared Function ResolutionFor(rec As ApprovalRecord) As String
            If rec.Kind = ApprovalKind.ToolApproval Then Return Resolutions.Approval(rec.Status.ToString(), rec.Response, rec.DecidedBy)
            Return Resolutions.HumanAnswer(rec.Status.ToString(), rec.Response, rec.DecidedBy)
        End Function

        ''' <summary>Message-triggered and delegated tasks answer the originating message with the structured result.</summary>
        Private Async Function ReplyIfNeededAsync(t As AgentTask, result As TaskResultContract) As Task
            If String.IsNullOrEmpty(t.SourceMessageId) Then Return
            Try
                Dim r = Await _comms.ReplyAsync(t.AgentId, t.SourceMessageId, result, CancellationToken.None, "reply:" & t.SourceMessageId).ConfigureAwait(False)
                If Not r.Success Then _log.Warn("Executor", $"Could not reply to {t.SourceMessageId}: {r.ErrorCode} {r.ErrorMessage}", t.AgentId, t.Id)
            Catch ex As Exception
                _log.Warn("Executor", "Reply failed", t.AgentId, t.Id, ex)
            End Try
        End Function

        ''' <summary>Resumes the agent's waiting tasks whose deadline passed (each pending wait gets a timeout resolution).</summary>
        Public Async Function ResumeExpiredWaitsAsync(agentId As String) As Task(Of Integer)
            Dim due = Await _tasks.ListWaitingDueAsync(agentId, _clock.UtcNow).ConfigureAwait(False)
            Dim n = 0
            For Each t In due
                For Each key In t.WaitingOn.ToList()
                    If key.StartsWith("msg:", StringComparison.OrdinalIgnoreCase) Then
                        Await _comms.CancelMessageAsync(AgentCommunicationService.AdminSender, key.Substring(4), "Requester stopped waiting (timeout)").ConfigureAwait(False)
                    End If
                    Await _resolver.ResolveAsync(agentId, key, Resolutions.Timeout($"No result for {key} before the deadline."), "Wait deadline passed").ConfigureAwait(False)
                Next
                If t.WaitingOn.Count = 0 Then
                    Await _tasks.TryTransitionAsync(t.Id, {AgentTaskStatus.Waiting}, AgentTaskStatus.Pending, "Wait deadline passed").ConfigureAwait(False)
                End If
                n += 1
            Next
            Return n
        End Function
    End Class

End Namespace
