Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Approvals
Imports Agentic.Artifacts
Imports Agentic.Communication
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Memory
Imports Agentic.Persistence
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Agents

    ''' <summary>Immutable, point-in-time view of an agent for the dashboard/API. Contains no references to runtime objects.</summary>
    Public NotInheritable Class AgentStatusSnapshot
        Public Property Id As String
        Public Property Name As String
        Public Property Role As String
        Public Property Description As String
        Public Property Source As String
        Public Property Enabled As Boolean
        Public Property State As String
        Public Property StateSinceUtc As DateTime
        Public Property StartedUtc As DateTime?
        Public Property UptimeSeconds As Long
        Public Property CurrentTaskId As String
        Public Property CurrentTaskName As String
        Public Property CurrentOperation As String
        Public Property CurrentOperationStartedUtc As DateTime?
        Public Property LastHeartbeatUtc As DateTime?
        Public Property LastProgressUtc As DateTime?
        Public Property LastActivityUtc As DateTime?
        Public Property LastActivity As String
        Public Property LastError As String
        Public Property LastErrorUtc As DateTime?
        Public Property Unhealthy As Boolean
        Public Property RecentRestarts As Integer
        Public Property TasksCompleted As Long
        Public Property TasksFailed As Long
        Public Property TasksRetried As Long
        Public Property AiCalls As Long
        Public Property AiFailures As Long
        Public Property ToolCalls As Long
        Public Property ToolFailures As Long
        Public Property MessagesSent As Long
        Public Property MessagesReceived As Long
        Public Property Delegations As Long
        Public Property AvgTaskMs As Long
        Public Property AvgAiLatencyMs As Long
        Public Property AvgToolLatencyMs As Long
        Public Property Tokens As Long
        Public Property CostMicros As Long
        Public Property LastAiCallUtc As DateTime?
        Public Property LastToolCallUtc As DateTime?
        Public Property LastSuccessfulTaskUtc As DateTime?
        Public Property IsSubagent As Boolean
        Public Property ParentAgentId As String
        Public Property MaxSubagents As Integer
        ''' <summary>The account that pays for the agent (Nothing = the administrators).</summary>
        Public Property Owner As String
        ''' <summary>Stopped because its owner's account balance is empty; it continues once money is added.</summary>
        Public Property Unfunded As Boolean
        ' Optional details (filled by the API on request)
        Public Property QueuedTasks As Long?
        Public Property WaitingTasks As Long?
        Public Property HeldTasks As Long?
        Public Property AvailableMicros As Long?
        Public Property SpentTodayMicros As Long?
        ''' <summary>Days the balance lasts at the recent burn rate (Nothing while nothing is being spent).</summary>
        Public Property RunwayDays As Double?
        Public Property LowRunway As Boolean
        Public Property ActiveSubagents As Integer?
        Public Property GrantedModels As Integer?
    End Class

    ''' <summary>Thread-safe registry of effective profiles (host infrastructure).</summary>
    Public NotInheritable Class ProfileRegistry
        Implements IProfileProvider

        Private ReadOnly _profiles As New ConcurrentDictionary(Of String, EffectiveProfile)(StringComparer.OrdinalIgnoreCase)

        Public Sub [Set](p As EffectiveProfile)
            _profiles(p.Id) = p
        End Sub

        Public Sub Remove(id As String)
            Dim p As EffectiveProfile = Nothing
            _profiles.TryRemove(id, p)
        End Sub

        Public Function TryGetProfile(agentId As String) As EffectiveProfile Implements IProfileProvider.TryGetProfile
            Dim p As EffectiveProfile = Nothing
            If agentId IsNot Nothing AndAlso _profiles.TryGetValue(agentId, p) Then Return p
            Return Nothing
        End Function

        Public Function AllProfiles() As IReadOnlyList(Of EffectiveProfile) Implements IProfileProvider.AllProfiles
            Return _profiles.Values.OrderBy(Function(p) p.Id).ToList()
        End Function
    End Class

    ''' <summary>Builds the agent-bound service facades handed to an agent's tools.</summary>
    Friend NotInheritable Class AgentScopeFactory
        Public Property MemoryStore As IMemoryStore
        Public Property Comms As AgentCommunicationService
        Public Property Artifacts As ArtifactStore
        Public Property Wallets As WalletService
        Public Property Pricing As PricingService
        Public Property Registry As ModelRegistry
        Public Property Tasks As TaskRepository
        Public Property Approvals As ApprovalService
        Public Property Vision As VisionService
        Public Property Images As ImageGenerationService
        Public Property Events As IAgentEventBus
        Public Property Gateway As ModelGateway
        Public Property ModelAccess As ModelAccessService
        Public Property SpendCaps As SpendCapTracker
        Public Property SubagentHost As ISubagentHost

        Public Function Create(agentId As String, profile As Func(Of EffectiveProfile), wake As Action) As AgentScope
            Dim ev = Events
            Dim memory As New ScopedMemoryStore(MemoryStore, agentId, Function() profile().MaxMemories, Function() profile().MaxMemoryChars,
                                                Sub(item) ev.Emit(AgentEventType.MemoryCreated, $"Memory saved [{item.Category}]: {TextUtil.Truncate(item.Content, 80)}", agentId, item.SourceTaskId))
            Return New AgentScope(agentId, memory, New AgentCommunicationClient(Comms, agentId), New ScopedArtifactAccess(Artifacts, agentId),
                                  New AgentBudgetView(Wallets, Pricing, Registry, agentId, profile, ModelAccess, SpendCaps), New AgentTaskQueue(Tasks, agentId, wake),
                                  New ScopedApprovals(Approvals, agentId), New ScopedVision(Vision, agentId), New ScopedImageGeneration(Images, agentId),
                                  If(SubagentHost Is Nothing, Nothing, New AgentSubagents(SubagentHost, agentId)),
                                  If(Gateway Is Nothing, Nothing, New ScopedModelAccess(Gateway, ModelAccess, Pricing, agentId)))
        End Function
    End Class

    Public Class AgentOperationResult
        Public Property Success As Boolean
        Public Property Code As String
        Public Property Message As String
        Public Property TaskId As String

        Public Shared Function Ok(Optional message As String = "OK", Optional taskId As String = Nothing) As AgentOperationResult
            Return New AgentOperationResult With {.Success = True, .Code = "OK", .Message = message, .TaskId = taskId}
        End Function

        Public Shared Function Fail(code As String, message As String) As AgentOperationResult
            Return New AgentOperationResult With {.Success = False, .Code = code, .Message = message}
        End Function
    End Class

    ''' <summary>
    ''' Owns all agent runtimes (privately). Exposes lifecycle commands and immutable snapshots only — there is no
    ''' method that returns an agent's runtime, memory or task queue object. Implements the waker so the message bus
    ''' can nudge recipients without holding references to them.
    ''' </summary>
    Partial Public NotInheritable Class AgentManager
        Implements IAgentWaker

        Private ReadOnly _runtimes As New ConcurrentDictionary(Of String, AgentRuntime)(StringComparer.OrdinalIgnoreCase)
        Private ReadOnly _options As HostOptions
        Private ReadOnly _profiles As ProfileRegistry
        Private ReadOnly _resolver As ProfileResolver
        Private ReadOnly _svc As RuntimeServices
        Private ReadOnly _scopes As AgentScopeFactory
        Private ReadOnly _agentsRepo As AgentRepository
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _wallets As WalletService
        Private ReadOnly _stats As StatisticsAggregator
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private _scheduler As WorkScheduler
        Private _hostToken As CancellationToken
        Private _watchdog As Task
        Private _acceptingWork As Boolean = True

        Friend Sub New(options As HostOptions, profiles As ProfileRegistry, svc As RuntimeServices, scopes As AgentScopeFactory, agentsRepo As AgentRepository,
                       tasks As TaskRepository, wallets As WalletService, stats As StatisticsAggregator, events As IAgentEventBus, log As ILog)
            _options = options
            _profiles = profiles
            _resolver = New ProfileResolver(options.BaseProfiles, options.Communication, options.Subagents, options.Autonomy)
            _svc = svc
            _scopes = scopes
            _scopes.SubagentHost = Me
            _agentsRepo = agentsRepo
            _tasks = tasks
            _wallets = wallets
            _stats = stats
            _events = events
            _log = log
        End Sub

        Friend Sub AttachScheduler(scheduler As WorkScheduler)
            _scheduler = scheduler
        End Sub

        Public ReadOnly Property Resolver As ProfileResolver
            Get
                Return _resolver
            End Get
        End Property

        ' ------------------------------------------------------------------------------------------
        ' Initialization
        ' ------------------------------------------------------------------------------------------

        ''' <summary>
        ''' Agents in appsettings.json are only a starting point: each is copied into the database the first time it appears
        ''' and from then on is an ordinary agent the administrator edits (or deletes) in the dashboard - later changes to
        ''' the file do not overwrite it. Then every stored agent gets its runtime, wallet and schedules.
        ''' </summary>
        Public Async Function InitializeAsync() As Task
            For Each a In _options.Agents
                Dim row = Await _agentsRepo.FindAgentRowAsync(a.Id).ConfigureAwait(False)
                ' Seed new agents. Agents kept read-only by earlier versions (Source=Config) take the file's version one last time.
                If row Is Nothing OrElse (row.Value.Source = "Config" AndAlso Not row.Value.Deleted) Then
                    Await _agentsRepo.UpsertAgentAsync(a, "Admin", If(a.Enabled, True)).ConfigureAwait(False)
                    _log.Info("Agents", $"Agent {a.Id} {If(row Is Nothing, "created", "converted")} from appsettings.json; edit it in the dashboard from now on.")
                End If
            Next
            Dim stored = Await _agentsRepo.ListAgentsAsync().ConfigureAwait(False)
            For Each s In stored
                If s.Profile Is Nothing Then Continue For
                Dim profile = _resolver.Resolve(s.Profile, "Admin")
                Await RegisterAsync(profile, s.DesiredState).ConfigureAwait(False)
                If Not String.IsNullOrWhiteSpace(s.Owner) Then _owners(s.Id) = s.Owner
            Next
            Await _agentsRepo.ReplaceCommunicationTopologyAsync(_profiles.AllProfiles()).ConfigureAwait(False)
            Await RestoreSubagentsAsync().ConfigureAwait(False)
        End Function

        Private _desiredStates As New ConcurrentDictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

        Private Async Function RegisterAsync(profile As EffectiveProfile, desiredState As String) As Task
            _profiles.Set(profile)
            _desiredStates(profile.Id) = If(desiredState, "Running")
            Dim initial = If(profile.InitialBalance >= 0, profile.InitialBalance, _options.Economy.DefaultInitialBalance)
            Await _wallets.EnsureWalletAsync(profile.Id, TextUtil.DollarsToMicros(initial),
                                             If(profile.DailySpendLimit.HasValue, TextUtil.DollarsToMicros(profile.DailySpendLimit.Value), CType(Nothing, Long?))).ConfigureAwait(False)
            Dim existing As AgentRuntime = Nothing
            If _runtimes.TryGetValue(profile.Id, existing) Then
                existing.Context.Profile = profile
            Else
                Dim agentId = profile.Id
                Dim ctxHolder As AgentRuntimeContext = Nothing
                Dim scope = _scopes.Create(agentId, Function() If(ctxHolder?.Profile, profile), Sub() Wake(agentId))
                Dim ctx As New AgentRuntimeContext(agentId, profile, scope)
                ctxHolder = ctx
                _runtimes(agentId) = New AgentRuntime(ctx, _svc)
            End If
            If _scheduler IsNot Nothing Then Await _scheduler.SyncAgentSchedulesAsync(profile).ConfigureAwait(False)
        End Function

        Public Sub StartAll(hostToken As CancellationToken)
            _hostToken = hostToken
            For Each r In _runtimes.Values
                Dim desired As String = Nothing
                _desiredStates.TryGetValue(r.Context.AgentId, desired)
                If Not r.Context.Profile.Enabled Then
                    r.Context.SetState(AgentState.Disabled)
                    Continue For
                End If
                If String.Equals(desired, "Stopped", StringComparison.OrdinalIgnoreCase) Then Continue For
                If String.Equals(desired, "Dead", StringComparison.OrdinalIgnoreCase) Then r.MarkDead("out of money") : Continue For
                r.Start(hostToken)
                If String.Equals(desired, "Paused", StringComparison.OrdinalIgnoreCase) OrElse IsUnfunded(r.Context.AgentId) Then r.Pause()
            Next
            _watchdog = WatchdogLoopAsync(hostToken)
        End Sub

        ''' <summary>Shutdown: stop accepting work and stop every agent loop (bounded wait).</summary>
        Public Async Function StopAllAsync(timeout As TimeSpan) As Task
            _acceptingWork = False
            Dim stops = _runtimes.Values.Select(Function(r) r.StopAsync(timeout)).ToList()
            Try
                Await Task.WhenAll(stops).WaitAsync(timeout + TimeSpan.FromSeconds(2)).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Agents", "Not all agents stopped cleanly: " & ex.Message)
            End Try
        End Function

        ' ------------------------------------------------------------------------------------------
        ' IAgentWaker
        ' ------------------------------------------------------------------------------------------

        Public Sub Wake(agentId As String) Implements IAgentWaker.Wake
            Dim r As AgentRuntime = Nothing
            If agentId IsNot Nothing AndAlso _runtimes.TryGetValue(agentId, r) Then r.Wake()
        End Sub

        Public Function IsRunning(agentId As String) As Boolean Implements IAgentWaker.IsRunning
            Dim r As AgentRuntime = Nothing
            Return agentId IsNot Nothing AndAlso _runtimes.TryGetValue(agentId, r) AndAlso r.IsLoopRunning
        End Function

        Public Function DescribeStatus(agentId As String) As String Implements IAgentWaker.DescribeStatus
            Dim r As AgentRuntime = Nothing
            If agentId Is Nothing OrElse Not _runtimes.TryGetValue(agentId, r) Then Return "unknown"
            Return r.Context.State.ToString().ToLowerInvariant()
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Snapshots (read-only views)
        ' ------------------------------------------------------------------------------------------

        Public Function GetSnapshots(Optional includeSubagents As Boolean = False) As List(Of AgentStatusSnapshot)
            Return _runtimes.Values.Where(Function(r) includeSubagents OrElse Not r.Context.Profile.IsSubagent).
                Select(Function(r) Snapshot(r)).OrderBy(Function(s) s.Id).ToList()
        End Function

        Public Function IsSubagent(agentId As String) As Boolean
            Dim p = _profiles.TryGetProfile(agentId)
            Return p IsNot Nothing AndAlso p.IsSubagent
        End Function

        Public Function GetSnapshot(agentId As String) As AgentStatusSnapshot
            Dim r As AgentRuntime = Nothing
            If agentId Is Nothing OrElse Not _runtimes.TryGetValue(agentId, r) Then Return Nothing
            Return Snapshot(r)
        End Function

        Public Function Exists(agentId As String) As Boolean
            Return agentId IsNot Nothing AndAlso _runtimes.ContainsKey(agentId)
        End Function

        ''' <summary>Admin-only view of the effective profile (permissions etc.). Never exposed to agents.</summary>
        Public Function GetEffectiveProfile(agentId As String) As EffectiveProfile
            Return _profiles.TryGetProfile(agentId)
        End Function

        Private Function Snapshot(r As AgentRuntime) As AgentStatusSnapshot
            Dim c = r.Context
            Dim p = c.Profile
            Dim s = _stats.Get(c.AgentId)
            Dim snap As New AgentStatusSnapshot With {
                .Id = c.AgentId, .Name = p.Name, .Role = p.Role, .Description = p.Description, .Source = p.Source, .Enabled = p.Enabled,
                .State = c.State.ToString(), .StateSinceUtc = c.StateSinceUtc, .StartedUtc = c.StartedUtc,
                .UptimeSeconds = If(c.StartedUtc.HasValue AndAlso r.IsLoopRunning, CLng((DateTime.UtcNow - c.StartedUtc.Value).TotalSeconds), 0L),
                .CurrentTaskId = c.CurrentTaskId, .CurrentTaskName = c.CurrentTaskName, .CurrentOperation = c.CurrentOperation,
                .CurrentOperationStartedUtc = c.CurrentOperationStartedUtc, .LastHeartbeatUtc = c.LastHeartbeatUtc, .LastProgressUtc = c.LastProgressUtc,
                .LastError = If(c.LastErrorMessage, s.LastError), .LastErrorUtc = If(c.LastErrorUtc, s.LastErrorUtc), .Unhealthy = c.Unhealthy,
                .RecentRestarts = c.RestartTimestamps.Count, .IsSubagent = p.IsSubagent, .ParentAgentId = p.OwnerAgentId, .MaxSubagents = p.MaxSubagents,
                .Owner = OwnerOf(c.AgentId), .Unfunded = IsUnfunded(If(p.IsSubagent, p.OwnerAgentId, c.AgentId))}
            SyncLock s
                snap.LastActivityUtc = s.LastActivityUtc
                snap.LastActivity = s.LastActivity
                snap.TasksCompleted = s.TasksCompleted
                snap.TasksFailed = s.TasksFailed
                snap.TasksRetried = s.TasksRetried
                snap.AiCalls = s.AiCalls
                snap.AiFailures = s.AiFailures
                snap.ToolCalls = s.ToolCalls
                snap.ToolFailures = s.ToolFailures
                snap.MessagesSent = s.MessagesSent
                snap.MessagesReceived = s.MessagesReceived
                snap.Delegations = s.Delegations
                snap.AvgTaskMs = If(s.TaskDurationSamples = 0, 0L, s.TaskDurationMs \ s.TaskDurationSamples)
                snap.AvgAiLatencyMs = If(s.AiCalls = 0, 0L, s.AiLatencyMs \ s.AiCalls)
                snap.AvgToolLatencyMs = If(s.ToolCalls = 0, 0L, s.ToolLatencyMs \ s.ToolCalls)
                snap.Tokens = s.Tokens
                snap.CostMicros = s.CostMicros
                snap.LastAiCallUtc = s.LastAiCallUtc
                snap.LastToolCallUtc = s.LastToolCallUtc
                snap.LastSuccessfulTaskUtc = s.LastSuccessfulTaskUtc
            End SyncLock
            Return snap
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Admin commands
        ' ------------------------------------------------------------------------------------------

        Private Function TryRuntime(agentId As String, ByRef r As AgentRuntime) As Boolean
            Return agentId IsNot Nothing AndAlso _runtimes.TryGetValue(agentId, r)
        End Function

        Public Async Function StartAgentAsync(agentId As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If r.Context.Profile.IsSubagent Then
                If r.IsLoopRunning Then Return AgentOperationResult.Fail("SUBAGENT", "Subagents run until their task ends; use Resume or Cancel.")
                r.Start(_hostToken)
                Return AgentOperationResult.Ok("Started.")
            End If
            If Not r.Context.Profile.Enabled Then Return AgentOperationResult.Fail("AGENT_DISABLED", "Agent is disabled in its profile.")
            If Not _acceptingWork Then Return AgentOperationResult.Fail("HOST_SHUTTING_DOWN", "Host is shutting down.")
            If IsDead(agentId) Then Return Await ReviveAsync(agentId, "started by an administrator").ConfigureAwait(False)
            If IsUnfunded(agentId) Then Return UnfundedResult(agentId)
            r.Context.RestartTimestamps.Clear()
            r.Context.Unhealthy = False
            r.Context.PauseGate.Open()
            r.Start(_hostToken)
            _desiredStates(agentId) = "Running"
            Await _agentsRepo.SetDesiredStateAsync(agentId, "Running").ConfigureAwait(False)
            Return AgentOperationResult.Ok("Started.")
        End Function

        Public Async Function StopAgentAsync(agentId As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            ' Stopping a subagent means ending it (its parent receives a Cancelled result).
            If r.Context.Profile.IsSubagent Then Return Await CancelSubagentAsync(agentId, "Stopped by administrator").ConfigureAwait(False)
            Await r.StopAsync(TimeSpan.FromSeconds(_options.Host.ShutdownTimeoutSeconds)).ConfigureAwait(False)
            _desiredStates(agentId) = "Stopped"
            Await _agentsRepo.SetDesiredStateAsync(agentId, "Stopped").ConfigureAwait(False)
            Return AgentOperationResult.Ok("Stopped.")
        End Function

        Public Async Function PauseAgentAsync(agentId As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If Not r.IsLoopRunning Then Return AgentOperationResult.Fail("AGENT_NOT_RUNNING", "Agent is not running.")
            r.Pause()
            _desiredStates(agentId) = "Paused"
            Await _agentsRepo.SetDesiredStateAsync(agentId, "Paused").ConfigureAwait(False)
            Return AgentOperationResult.Ok("Paused (the current task finishes first).")
        End Function

        Public Async Function ResumeAgentAsync(agentId As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If IsDead(agentId) Then Return Await ReviveAsync(agentId, "resumed by an administrator").ConfigureAwait(False)
            If IsUnfunded(agentId) Then Return UnfundedResult(agentId)
            r.Resume()
            _desiredStates(agentId) = "Running"
            Await _agentsRepo.SetDesiredStateAsync(agentId, "Running").ConfigureAwait(False)
            Return AgentOperationResult.Ok("Resumed.")
        End Function

        Public Async Function RestartAgentAsync(agentId As String) As Task(Of AgentOperationResult)
            If IsSubagent(agentId) Then Return AgentOperationResult.Fail("SUBAGENT", "Subagents cannot be restarted; cancel it and let its parent spawn a new one.")
            Dim stopResult = Await StopAgentAsync(agentId).ConfigureAwait(False)
            If Not stopResult.Success Then Return stopResult
            Return Await StartAgentAsync(agentId).ConfigureAwait(False)
        End Function

        ''' <summary>"Run Now": triggers the agent's schedules immediately (if any) and wakes it.</summary>
        Public Async Function RunNowAsync(agentId As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            Dim n = If(_scheduler Is Nothing, 0, Await _scheduler.TriggerAgentNowAsync(agentId).ConfigureAwait(False))
            r.Wake()
            Return AgentOperationResult.Ok(If(n > 0, $"Triggered {n} schedule(s).", "Agent woken."))
        End Function

        ''' <summary>Checks that a model can be pinned for this agent (permitted, enabled, priced).</summary>
        Public Function CanPinModel(agentId As String, modelKey As String) As String
            Dim profile = _profiles.TryGetProfile(agentId)
            If profile Is Nothing Then Return "Agent does not exist."
            Dim m = _scopes.Registry?.TryGet(modelKey)
            If m Is Nothing Then Return $"Model '{modelKey}' does not exist."
            If _selection IsNot Nothing AndAlso Not _selection.IsUsableBy(m, profile, Nothing) Then Return $"{agentId} may not use '{modelKey}' (not permitted, disabled or unpriced)."
            Return Nothing
        End Function

        Public Async Function SubmitTaskAsync(agentId As String, req As NewTaskRequest) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If r.Context.Profile.IsSubagent Then Return AgentOperationResult.Fail("SUBAGENT", "Tasks cannot be added to a subagent.")
            If Not _acceptingWork Then Return AgentOperationResult.Fail("HOST_SHUTTING_DOWN", "Host is shutting down.")
            If String.IsNullOrWhiteSpace(req.Name) Then Return AgentOperationResult.Fail("INVALID_TASK", "Task name is required.")
            If Not ConfigValidator.KnownHandlers.Contains(If(req.Handler, "reasoning"), StringComparer.OrdinalIgnoreCase) Then
                Return AgentOperationResult.Fail("INVALID_TASK", $"Unknown handler '{req.Handler}'.")
            End If
            If Not String.IsNullOrEmpty(req.ModelKey) Then
                Dim err = CanPinModel(agentId, req.ModelKey)
                If err IsNot Nothing Then Return AgentOperationResult.Fail("MODEL_NOT_ALLOWED", err)
            End If
            Dim profile = r.Context.Profile
            Dim now = DateTime.UtcNow
            Dim t As New AgentTask With {
                .Id = Ids.NewId("tsk"), .AgentId = agentId, .Name = TextUtil.Truncate(req.Name, 200), .Description = TextUtil.Truncate(req.Description, 20000),
                .Kind = req.Kind, .Handler = If(req.Handler, "reasoning"), .Purpose = req.Purpose, .Priority = req.Priority, .Payload = req.Payload,
                .MaxRetries = If(req.MaxRetries, profile.MaxRetries), .NextRunUtc = req.NextRunUtc, .StartTier = req.StartTier, .CreatedUtc = now, .UpdatedUtc = now,
                .CorrelationId = If(String.IsNullOrEmpty(req.CorrelationId), Ids.NewId("cor"), req.CorrelationId), .CreatedBy = If(req.CreatedBy, "admin"),
                .IdempotencyKey = req.IdempotencyKey, .ModelKey = req.ModelKey, .Delivery = req.Delivery,
                .ReviewCriteria = If(String.IsNullOrWhiteSpace(req.ReviewCriteria), Nothing, TextUtil.Truncate(req.ReviewCriteria.Trim(), 20000)),
                .DeadlineUtc = req.DeadlineUtc, .AutoApproveTools = req.AutoApproveTools,
                .DeadlineEnforced = req.DeadlineEnforced AndAlso req.DeadlineUtc.HasValue, .RunPastDeadline = req.RunPastDeadline}
            If t.Delivery IsNot Nothing AndAlso Not String.IsNullOrEmpty(t.Delivery.ReviewerAgentId) AndAlso Not Exists(t.Delivery.ReviewerAgentId) Then
                Return AgentOperationResult.Fail("INVALID_TASK", $"Reviewer '{t.Delivery.ReviewerAgentId}' does not exist.")
            End If
            If Not Await _tasks.InsertAsync(t, req.AddToFront).ConfigureAwait(False) Then Return AgentOperationResult.Fail("DUPLICATE_TASK", "A task with this idempotency key already exists.")
            Await _tasks.AddHistoryAsync(t.Id, agentId, Nothing, AgentTaskStatus.Pending, "Submitted by " & t.CreatedBy).ConfigureAwait(False)
            _events.Emit(AgentEventType.TaskCreated, $"Task submitted: {t.Name}", agentId, t.Id, correlationId:=t.CorrelationId)
            r.Wake()
            Return AgentOperationResult.Ok("Task queued.", t.Id)
        End Function

        ''' <param name="reason">Why (shown as the task's error); Nothing = cancelled by an administrator.</param>
        Public Async Function CancelTaskAsync(agentId As String, taskId As String, Optional reason As String = Nothing) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If String.IsNullOrEmpty(taskId) Then taskId = r.Context.CurrentTaskId
            If String.IsNullOrEmpty(taskId) Then Return AgentOperationResult.Fail("NO_TASK", "No task specified and none is running.")
            Dim t = Await _tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
            If t Is Nothing Then Return AgentOperationResult.Fail("TASK_NOT_FOUND", "Task does not exist for this agent.")
            If t.Status = AgentTaskStatus.Running AndAlso r.CancelCurrentTask(taskId, If(reason Is Nothing, "admin", "cancel:" & reason)) Then Return AgentOperationResult.Ok("Cancellation requested.", taskId)
            Dim why = If(reason, "Cancelled by administrator")
            Dim ok = Await _tasks.TryTransitionAsync(taskId, {AgentTaskStatus.Pending, AgentTaskStatus.Retrying, AgentTaskStatus.Waiting, AgentTaskStatus.Blocked},
                                                     AgentTaskStatus.Cancelled, why, why).ConfigureAwait(False)
            If Not ok Then Return AgentOperationResult.Fail("TASK_NOT_CANCELLABLE", $"Task is {t.Status}.")
            _events.Emit(AgentEventType.TaskCancelled, $"Cancelled: {t.Name}", agentId, taskId, correlationId:=t.CorrelationId)
            t.Status = AgentTaskStatus.Cancelled
            OnTaskFinished(t)
            Return AgentOperationResult.Ok("Cancelled.", taskId)
        End Function

        ''' <summary>Re-queues a Blocked/Failed/Cancelled task (admin decision) at its default queue position.</summary>
        Public Async Function RetryTaskAsync(agentId As String, taskId As String) As Task(Of AgentOperationResult)
            Dim r As AgentRuntime = Nothing
            If Not TryRuntime(agentId, r) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If r.Context.Profile.IsSubagent Then Return AgentOperationResult.Fail("SUBAGENT", "Subagent tasks cannot be re-queued.")
            Dim t = Await _tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
            If t Is Nothing Then Return AgentOperationResult.Fail("TASK_NOT_FOUND", "Task does not exist for this agent.")
            Dim ok = Await _tasks.TryTransitionAsync(taskId, {AgentTaskStatus.Failed, AgentTaskStatus.Blocked, AgentTaskStatus.Cancelled}, AgentTaskStatus.Pending, "Re-queued by administrator", "").ConfigureAwait(False)
            If Not ok Then Return AgentOperationResult.Fail("TASK_NOT_RETRYABLE", "Task is not in a retryable state.")
            If t.Status <> AgentTaskStatus.Blocked Then Await _tasks.ReRankAsync(taskId).ConfigureAwait(False)
            _events.Emit(AgentEventType.TaskUpdated, $"Re-queued: {t.Name}", agentId, taskId, correlationId:=t.CorrelationId)
            r.Wake()
            Return AgentOperationResult.Ok("Re-queued.", taskId)
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Queue management (order, edit, hold/release, clone, bulk)
        ' ------------------------------------------------------------------------------------------

        Private Function QueueOpResult(agentId As String, taskId As String, err As String, summary As String) As AgentOperationResult
            If err IsNot Nothing Then Return AgentOperationResult.Fail("TASK_NOT_MODIFIABLE", err)
            _events.Emit(AgentEventType.TaskUpdated, summary, agentId, taskId)
            Wake(agentId)
            Return AgentOperationResult.Ok(summary, taskId)
        End Function

        ''' <summary>Moves a queued task to the front/back or before/after another queued task.</summary>
        Public Async Function MoveTaskAsync(agentId As String, taskId As String, beforeTaskId As String, afterTaskId As String, toFront As Boolean, toBack As Boolean) As Task(Of AgentOperationResult)
            If Not Exists(agentId) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            Dim err = Await _tasks.MoveAsync(agentId, taskId, beforeTaskId, afterTaskId, toFront, toBack).ConfigureAwait(False)
            Return QueueOpResult(agentId, taskId, err, "Queue order changed.")
        End Function

        Public Async Function EditTaskAsync(agentId As String, taskId As String, edit As TaskEdit) As Task(Of AgentOperationResult)
            If Not Exists(agentId) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If Not String.IsNullOrEmpty(edit.ModelKey) Then
                Dim pinErr = CanPinModel(agentId, edit.ModelKey)
                If pinErr IsNot Nothing Then Return AgentOperationResult.Fail("MODEL_NOT_ALLOWED", pinErr)
            End If
            Dim err = Await _tasks.EditQueuedAsync(agentId, taskId, edit).ConfigureAwait(False)
            Return QueueOpResult(agentId, taskId, err, "Task updated.")
        End Function

        ''' <summary>Holds a queued task (it stays in the queue but is not started until released).</summary>
        Public Async Function HoldTaskAsync(agentId As String, taskId As String) As Task(Of AgentOperationResult)
            If Not Exists(agentId) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            Dim t = Await _tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
            If t Is Nothing Then Return AgentOperationResult.Fail("TASK_NOT_FOUND", "Task does not exist for this agent.")
            Dim ok = Await _tasks.TryTransitionAsync(taskId, {AgentTaskStatus.Pending, AgentTaskStatus.Retrying}, AgentTaskStatus.Blocked, "Held by administrator", "Held by administrator").ConfigureAwait(False)
            Return QueueOpResult(agentId, taskId, If(ok, Nothing, $"Only queued tasks can be held (task is {t.Status})."), $"Held: {t.Name}")
        End Function

        Public Async Function ReleaseTaskAsync(agentId As String, taskId As String) As Task(Of AgentOperationResult)
            If Not Exists(agentId) Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            Dim t = Await _tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
            If t Is Nothing Then Return AgentOperationResult.Fail("TASK_NOT_FOUND", "Task does not exist for this agent.")
            Dim ok = Await _tasks.TryTransitionAsync(taskId, {AgentTaskStatus.Blocked}, AgentTaskStatus.Pending, "Released by administrator", "").ConfigureAwait(False)
            Return QueueOpResult(agentId, taskId, If(ok, Nothing, $"Only held tasks can be released (task is {t.Status})."), $"Released: {t.Name}")
        End Function

        ''' <summary>Queues a copy of an existing task (same instructions, payload and settings).</summary>
        Public Async Function CloneTaskAsync(agentId As String, taskId As String, actor As String) As Task(Of AgentOperationResult)
            Dim t = Await _tasks.GetForAgentAsync(agentId, taskId).ConfigureAwait(False)
            If t Is Nothing Then Return AgentOperationResult.Fail("TASK_NOT_FOUND", "Task does not exist for this agent.")
            Return Await SubmitTaskAsync(agentId, New NewTaskRequest With {
                .Name = t.Name, .Description = t.Description, .Kind = If(t.Kind = TaskKind.Delegated OrElse t.Kind = TaskKind.MessageTriggered, TaskKind.OneTime, t.Kind),
                .Handler = t.Handler, .Purpose = t.Purpose, .Priority = t.Priority, .Payload = t.Payload, .MaxRetries = t.MaxRetries, .StartTier = t.StartTier,
                .ModelKey = If(t.ModelKey IsNot Nothing AndAlso CanPinModel(agentId, t.ModelKey) Is Nothing, t.ModelKey, Nothing), .CreatedBy = actor}).ConfigureAwait(False)
        End Function

        ''' <summary>Applies one action (cancel, hold, release, top, bottom, retry) to several tasks. Returns per-task results.</summary>
        Public Async Function BulkTaskActionAsync(agentId As String, action As String, taskIds As IEnumerable(Of String), actor As String) As Task(Of List(Of AgentOperationResult))
            Dim results As New List(Of AgentOperationResult)
            Dim ids = taskIds.Distinct().Take(500).ToList()
            ' "top" keeps the selected tasks' relative order.
            If action = "top" Then ids.Reverse()
            For Each id In ids
                Dim r As AgentOperationResult
                Select Case action
                    Case "cancel" : r = Await CancelTaskAsync(agentId, id).ConfigureAwait(False)
                    Case "hold" : r = Await HoldTaskAsync(agentId, id).ConfigureAwait(False)
                    Case "release" : r = Await ReleaseTaskAsync(agentId, id).ConfigureAwait(False)
                    Case "retry" : r = Await RetryTaskAsync(agentId, id).ConfigureAwait(False)
                    Case "top" : r = Await MoveTaskAsync(agentId, id, Nothing, Nothing, True, False).ConfigureAwait(False)
                    Case "bottom" : r = Await MoveTaskAsync(agentId, id, Nothing, Nothing, False, True).ConfigureAwait(False)
                    Case Else : r = AgentOperationResult.Fail("INVALID_ACTION", $"Unknown action '{action}'.")
                End Select
                r.TaskId = id
                results.Add(r)
            Next
            Return results
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Host-wide controls
        ' ------------------------------------------------------------------------------------------

        ''' <summary>Pauses every running agent and subagent (current tasks finish first). Returns how many were paused.</summary>
        Public Async Function PauseAllAsync() As Task(Of Integer)
            Dim n = 0
            For Each r In _runtimes.Values.ToList()
                If Not r.IsLoopRunning OrElse r.Context.State = AgentState.Paused Then Continue For
                r.Pause()
                _desiredStates(r.Context.AgentId) = "Paused"
                If Not r.Context.Profile.IsSubagent Then Await _agentsRepo.SetDesiredStateAsync(r.Context.AgentId, "Paused").ConfigureAwait(False)
                n += 1
            Next
            Return n
        End Function

        Public Async Function ResumeAllAsync() As Task(Of Integer)
            Dim n = 0
            For Each r In _runtimes.Values.ToList()
                Dim desired As String = Nothing
                _desiredStates.TryGetValue(r.Context.AgentId, desired)
                If Not String.Equals(desired, "Paused", StringComparison.OrdinalIgnoreCase) AndAlso r.Context.PauseGate.IsOpen Then Continue For
                ' An agent whose owner has no money stays stopped; it continues when money is added.
                If IsUnfunded(r.Context.AgentId) Then
                    _desiredStates(r.Context.AgentId) = "Running"
                    If Not r.Context.Profile.IsSubagent Then Await _agentsRepo.SetDesiredStateAsync(r.Context.AgentId, "Running").ConfigureAwait(False)
                    Continue For
                End If
                r.Resume()
                _desiredStates(r.Context.AgentId) = "Running"
                If Not r.Context.Profile.IsSubagent Then Await _agentsRepo.SetDesiredStateAsync(r.Context.AgentId, "Running").ConfigureAwait(False)
                n += 1
            Next
            Return n
        End Function

        ''' <summary>Emergency stop: pause everything, cancel every running task and end all subagents. Queued work stays queued.</summary>
        Public Async Function EmergencyStopAsync(actor As String) As Task(Of (Paused As Integer, CancelledTasks As Integer, CancelledSubagents As Integer))
            Dim paused = Await PauseAllAsync().ConfigureAwait(False)
            Dim cancelled = 0
            For Each r In _runtimes.Values.ToList()
                Dim taskId = r.Context.CurrentTaskId
                If taskId IsNot Nothing AndAlso Not r.Context.Profile.IsSubagent AndAlso r.CancelCurrentTask(taskId, "cancel:Emergency stop by " & actor) Then cancelled += 1
            Next
            Dim subs = 0
            For Each s In Await ListSubagentsAsync(Nothing, Nothing, SubagentStatus.Running.ToString(), 500).ConfigureAwait(False)
                If (Await CancelSubagentAsync(s.Id, "Emergency stop by " & actor).ConfigureAwait(False)).Success Then subs += 1
            Next
            _events.Emit(AgentEventType.AdminAction, $"EMERGENCY STOP by {actor}: {paused} agents paused, {cancelled} running tasks and {subs} subagents cancelled",
                         level:=EventLevel.Warning)
            Return (paused, cancelled, subs)
        End Function

        ''' <summary>Raw definition (overrides as stored) of an agent, for the dashboard editor.</summary>
        Public Async Function GetDefinitionAsync(agentId As String) As Task(Of (Profile As ProfileOptions, Source As String))
            Dim p = _profiles.TryGetProfile(agentId)
            If p Is Nothing OrElse p.IsSubagent Then Return (Nothing, Nothing)
            Dim stored = Await _agentsRepo.ListAgentsAsync().ConfigureAwait(False)
            Return (stored.FirstOrDefault(Function(s) String.Equals(s.Id, agentId, StringComparison.OrdinalIgnoreCase))?.Profile, p.Source)
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Admin-created agents (dashboard). Config agents are read-only here.
        ' ------------------------------------------------------------------------------------------

        Public Async Function SaveAdminAgentAsync(p As ProfileOptions, mcpIds As ICollection(Of String), modelKeys As ICollection(Of String)) As Task(Of ValidationReport)
            Dim report As New ValidationReport()
            If Not Security.InputValidation.IsValidId(p.Id) Then report.AddError("Invalid agent id (1-64 characters: letters, digits, _ - .).") : Return report
            If p.Id.Contains(".sub-", StringComparison.OrdinalIgnoreCase) OrElse p.Id.Equals("admin", StringComparison.OrdinalIgnoreCase) OrElse p.Id.Equals("host", StringComparison.OrdinalIgnoreCase) Then
                report.AddError($"Agent id '{p.Id}' is reserved.") : Return report
            End If
            Dim existing = _profiles.TryGetProfile(p.Id)
            If existing IsNot Nothing AndAlso existing.IsSubagent Then report.AddError("Subagents cannot be edited.") : Return report
            If existing Is Nothing AndAlso AgentCount >= _options.Host.MaxAgents Then report.AddError($"Maximum of {_options.Host.MaxAgents} agents reached.") : Return report
            If p.Subagents IsNot Nothing AndAlso p.Subagents.MaxConcurrent.HasValue AndAlso (p.Subagents.MaxConcurrent.Value < 0 OrElse p.Subagents.MaxConcurrent.Value > 20) Then
                report.AddError("Subagents.MaxConcurrent must be 0-20.") : Return report
            End If
            If p.BaseProfileId IsNot Nothing AndAlso Not _resolver.HasBase(p.BaseProfileId) Then report.AddError($"Unknown base profile '{p.BaseProfileId}'.") : Return report
            ConfigValidator.ValidateAutonomyOptions(p.Autonomy, report, $"Agent '{p.Id}'")
            If Not report.IsValid Then Return report
            Dim resolved = _resolver.Resolve(p, "Admin")
            Dim ids = New HashSet(Of String)(_profiles.AllProfiles().Select(Function(x) x.Id), StringComparer.OrdinalIgnoreCase)
            ids.Add(p.Id)
            ConfigValidator.ValidateProfile(_options, report, resolved, modelKeys, mcpIds, ids, $"Agent '{p.Id}'")
            If Not report.IsValid Then Return report
            Await _agentsRepo.UpsertAgentAsync(p, "Admin", resolved.Enabled).ConfigureAwait(False)
            Await RegisterAsync(resolved, "Running").ConfigureAwait(False)
            Await _agentsRepo.ReplaceCommunicationTopologyAsync(_profiles.AllProfiles()).ConfigureAwait(False)
            Dim r As AgentRuntime = Nothing
            If _runtimes.TryGetValue(p.Id, r) AndAlso resolved.Enabled AndAlso Not r.IsLoopRunning AndAlso _acceptingWork Then r.Start(_hostToken)
            r?.Wake()
            _events.Emit(AgentEventType.ConfigChanged, $"Agent {p.Id} {If(existing Is Nothing, "created", "updated")} by administrator", p.Id)
            Return report
        End Function

        Public Async Function DeleteAdminAgentAsync(agentId As String) As Task(Of AgentOperationResult)
            Dim p = _profiles.TryGetProfile(agentId)
            If p Is Nothing Then Return AgentOperationResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If p.IsSubagent Then Return Await CancelSubagentAsync(agentId, "Deleted by administrator").ConfigureAwait(False)
            Await CancelAllSubagentsOfAsync(agentId, "Parent agent deleted").ConfigureAwait(False)
            If _access IsNot Nothing Then Await _access.RevokeAllForAgentAsync(agentId, "host").ConfigureAwait(False)
            Dim r As AgentRuntime = Nothing
            If _runtimes.TryRemove(agentId, r) Then Await r.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(False)
            _profiles.Remove(agentId)
            Await _agentsRepo.MarkDeletedAsync(agentId).ConfigureAwait(False)
            Await _agentsRepo.ReplaceCommunicationTopologyAsync(_profiles.AllProfiles()).ConfigureAwait(False)
            If _scheduler IsNot Nothing Then Await _scheduler.RemoveAgentSchedulesAsync(agentId).ConfigureAwait(False)
            Return AgentOperationResult.Ok("Deleted.")
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Watchdog: stuck task / hung loop detection (never terminates the host)
        ' ------------------------------------------------------------------------------------------

        Private Async Function WatchdogLoopAsync(ct As CancellationToken) As Task
            Dim sup = _options.Supervision
            While Not ct.IsCancellationRequested
                Try
                    Await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, sup.WatchdogIntervalSeconds)), ct).ConfigureAwait(False)
                Catch ex As OperationCanceledException
                    Exit While
                End Try
                Dim now = DateTime.UtcNow
                SweepSubagentDeadlines(now)
                For Each r In _runtimes.Values
                    Try
                        Dim c = r.Context
                        If Not r.IsLoopRunning Then Continue For
                        If c.State = AgentState.Running AndAlso c.LastProgressUtc.HasValue AndAlso (now - c.LastProgressUtc.Value).TotalSeconds > sup.StuckAfterSeconds Then
                            If r.CancelCurrentTask(Nothing, "stuck") Then
                                c.SetState(AgentState.Stuck)
                                _events.Emit(AgentEventType.AgentStuck, $"Agent {c.AgentId} made no progress for {sup.StuckAfterSeconds}s on '{c.CurrentTaskName}'; task cancelled.",
                                             c.AgentId, c.CurrentTaskId, level:=EventLevel.Warning)
                                c.LastProgressUtc = now
                            End If
                        End If
                        ' Loop hung (no heartbeat for a long time while not legitimately waiting).
                        Dim idleAllowance = c.Profile.IdleDelaySeconds + sup.StuckAfterSeconds + c.Profile.MaxTaskDurationSeconds
                        If c.LastHeartbeatUtc.HasValue AndAlso (now - c.LastHeartbeatUtc.Value).TotalSeconds > idleAllowance AndAlso
                           c.State <> AgentState.Paused AndAlso sup.RestartStuckAgents Then
                            _events.Emit(AgentEventType.AgentStuck, $"Agent {c.AgentId} loop unresponsive; restarting it.", c.AgentId, level:=EventLevel.Warning)
                            Dim agentId = c.AgentId
                            Dim restartTask = Task.Run(Async Function()
                                                           Await r.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(False)
                                                           r.Start(ct)
                                                       End Function)
                            c.Heartbeat()
                        End If
                    Catch ex As Exception
                        _log.Warn("Watchdog", "Watchdog check failed: " & ex.Message)
                    End Try
                Next
            End While
        End Function

        ''' <summary>Agents (not subagents) per state.</summary>
        Public Function CountByState() As Dictionary(Of String, Integer)
            Return _runtimes.Values.Where(Function(r) Not r.Context.Profile.IsSubagent).GroupBy(Function(r) r.Context.State.ToString()).ToDictionary(Function(g) g.Key, Function(g) g.Count())
        End Function

        ''' <summary>Number of agents, excluding temporary subagents.</summary>
        Public ReadOnly Property AgentCount As Integer
            Get
                Return _runtimes.Values.Where(Function(r) Not r.Context.Profile.IsSubagent).Count()
            End Get
        End Property

        ''' <summary>Agents currently executing a task: (agent id, task id, task name, operation, started).</summary>
        Public Function RunningWork() As List(Of AgentStatusSnapshot)
            Return _runtimes.Values.Where(Function(r) r.Context.CurrentTaskId IsNot Nothing).Select(Function(r) Snapshot(r)).OrderBy(Function(s) s.CurrentOperationStartedUtc).ToList()
        End Function
    End Class

End Namespace
