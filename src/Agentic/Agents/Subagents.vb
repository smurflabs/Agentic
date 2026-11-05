Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Threading
Imports Microsoft.Data.Sqlite
Imports Agentic.AI
Imports Agentic.Artifacts
Imports Agentic.Configuration
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Agents

    ''' <summary>Read model of a subagent (dashboard/API and the parent's tools).</summary>
    Public NotInheritable Class SubagentInfo
        Public Property Id As String
        Public Property ParentAgentId As String
        Public Property ParentTaskId As String
        Public Property TaskId As String
        Public Property Name As String
        Public Property Status As String
        Public Property Models As New List(Of String)
        Public Property Purpose As String
        Public Property SpendCapMicros As Long?
        Public Property SpentMicros As Long
        Public Property ResultJson As String
        Public Property Summary As String
        Public Property Success As Boolean?
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        Public Property DeadlineUtc As DateTime?
        Public Property EndedUtc As DateTime?
        ' Live fields (running subagents only)
        Public Property State As String
        Public Property CurrentOperation As String
        Public Property TaskStatus As String
    End Class

    Public NotInheritable Class SpawnRequest
        Public Property Name As String
        Public Property Instructions As String
        Public Property PayloadJson As String
        Public Property Models As New List(Of String)
        Public Property Purpose As AiPurpose?
        Public Property SpendCapUsd As Decimal?
        Public Property TimeoutMinutes As Integer
        Public Property Attachments As New List(Of String)
        Public Property Priority As PriorityLevel?
        Public Property StartTier As Integer?
        Public Property IdempotencyKey As String
    End Class

    Public NotInheritable Class SpawnResult
        Public Property Success As Boolean
        Public Property Code As String
        Public Property Message As String
        Public Property SubagentId As String
        Public Property TaskId As String
        Public Property DeadlineUtc As DateTime
        Public Property SpendCapMicros As Long
        Public Property Duplicate As Boolean

        Public Shared Function Fail(code As String, message As String) As SpawnResult
            Return New SpawnResult With {.Success = False, .Code = code, .Message = message}
        End Function
    End Class

    Friend Interface ISubagentHost
        Function SpawnAsync(parentId As String, parentTask As AgentTask, req As SpawnRequest) As Task(Of SpawnResult)
        Function ListForParentAsync(parentId As String, parentTaskId As String, limit As Integer) As Task(Of List(Of SubagentInfo))
        Function GetForParentAsync(parentId As String, subagentId As String) As Task(Of SubagentInfo)
        Function CancelForParentAsync(parentId As String, subagentId As String, reason As String) As Task(Of AgentOperationResult)
    End Interface

    ''' <summary>The calling agent's own subagents. Bound to one parent id; it can never touch another agent's subagents.</summary>
    Public NotInheritable Class AgentSubagents
        Private ReadOnly _host As ISubagentHost
        Private ReadOnly _agentId As String

        Friend Sub New(host As ISubagentHost, agentId As String)
            _host = host
            _agentId = agentId
        End Sub

        Public Function SpawnAsync(parentTask As AgentTask, req As SpawnRequest) As Task(Of SpawnResult)
            Return _host.SpawnAsync(_agentId, parentTask, req)
        End Function

        ''' <summary>Recent subagents of this agent (optionally only those started by one task).</summary>
        Public Function ListAsync(Optional parentTaskId As String = Nothing, Optional limit As Integer = 25) As Task(Of List(Of SubagentInfo))
            Return _host.ListForParentAsync(_agentId, parentTaskId, limit)
        End Function

        Public Function GetAsync(subagentId As String) As Task(Of SubagentInfo)
            Return _host.GetForParentAsync(_agentId, subagentId)
        End Function

        Public Function CancelAsync(subagentId As String, reason As String) As Task(Of AgentOperationResult)
            Return _host.CancelForParentAsync(_agentId, subagentId, reason)
        End Function
    End Class

    ' =============================================================================================
    ' AgentManager: subagent lifecycle
    ' =============================================================================================

    Partial Public NotInheritable Class AgentManager
        Implements ISubagentHost, ITaskObserver

        Private _db As Database
        Private _access As ModelAccessService
        Private _caps As SpendCapTracker
        Private _waitResolver As WaitResolver
        Private _artifactStore As ArtifactStore
        Private _selection As ModelSelectionService
        Private ReadOnly _spawnLock As New SemaphoreSlim(1, 1)
        Private ReadOnly _subDeadlines As New ConcurrentDictionary(Of String, DateTime)(StringComparer.OrdinalIgnoreCase)

        Friend Sub AttachServices(db As Database, access As ModelAccessService, caps As SpendCapTracker, waitResolver As WaitResolver,
                                  artifacts As ArtifactStore, selection As ModelSelectionService)
            _db = db
            _access = access
            _caps = caps
            _waitResolver = waitResolver
            _artifactStore = artifacts
            _selection = selection
        End Sub

        Private Shared Function MapSubagent(r As SqliteDataReader) As SubagentInfo
            Dim spec = JsonUtil.FromJson(Of SubagentSpec)(Db.Str(r, "SpecJson"))
            Dim info As New SubagentInfo With {
                .Id = Db.Str(r, "Id"), .ParentAgentId = Db.Str(r, "ParentAgentId"), .ParentTaskId = Db.Str(r, "ParentTaskId"), .TaskId = Db.Str(r, "TaskId"),
                .Name = Db.Str(r, "Name"), .Status = Db.Str(r, "Status"), .Models = If(spec?.Models, New List(Of String)), .Purpose = spec?.Purpose?.ToString(),
                .SpendCapMicros = Db.LngN(r, "SpendCapMicros"), .SpentMicros = Db.Lng(r, "SpentMicros"), .ResultJson = Db.Str(r, "ResultJson"),
                .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .DeadlineUtc = Db.DtN(r, "DeadlineUtc"), .EndedUtc = Db.DtN(r, "EndedUtc")}
            Dim el As System.Text.Json.JsonElement
            If info.ResultJson IsNot Nothing AndAlso JsonUtil.TryParseElement(info.ResultJson, el) Then
                info.Summary = JsonUtil.GetString(el, "summary")
                info.Success = JsonUtil.GetBool(el, "success", False)
            End If
            Return info
        End Function

        Private Function Decorate(info As SubagentInfo) As SubagentInfo
            If info Is Nothing OrElse info.Status <> SubagentStatus.Running.ToString() Then Return info
            If _caps IsNot Nothing AndAlso _caps.IsTracked(info.Id) Then info.SpentMicros = _caps.GetSpent(info.Id)
            Dim r As AgentRuntime = Nothing
            If _runtimes.TryGetValue(info.Id, r) Then
                info.State = r.Context.State.ToString()
                info.CurrentOperation = r.Context.CurrentOperation
            End If
            Return info
        End Function

        Public Async Function GetSubagentAsync(subagentId As String) As Task(Of SubagentInfo)
            If _db Is Nothing OrElse String.IsNullOrEmpty(subagentId) Then Return Nothing
            Dim s = Await _db.QuerySingleAsync("SELECT * FROM Subagents WHERE Id=@id", AddressOf MapSubagent, ("@id", CObj(subagentId))).ConfigureAwait(False)
            Return Decorate(s)
        End Function

        ''' <summary>Subagents filtered by parent, parent task and/or status (newest first).</summary>
        Public Async Function ListSubagentsAsync(Optional parentId As String = Nothing, Optional parentTaskId As String = Nothing, Optional status As String = Nothing,
                                                 Optional limit As Integer = 100) As Task(Of List(Of SubagentInfo))
            If _db Is Nothing Then Return New List(Of SubagentInfo)
            Dim where As New List(Of String)
            If Not String.IsNullOrEmpty(parentId) Then where.Add("ParentAgentId=@p")
            If Not String.IsNullOrEmpty(parentTaskId) Then where.Add("ParentTaskId=@t")
            If Not String.IsNullOrEmpty(status) Then where.Add("Status=@s")
            Dim list = Await _db.QueryAsync("SELECT * FROM Subagents" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY CreatedUtc DESC LIMIT @l",
                                            AddressOf MapSubagent, ("@p", CObj(parentId)), ("@t", parentTaskId), ("@s", status), ("@l", Math.Clamp(limit, 1, 500))).ConfigureAwait(False)
            Return list.Select(AddressOf Decorate).ToList()
        End Function

        Public ReadOnly Property ActiveSubagentCount As Integer
            Get
                Return _runtimes.Values.Where(Function(r) r.Context.Profile.IsSubagent).Count()
            End Get
        End Property

        ' ------------------------------------------------------------------------------------------
        ' Spawn
        ' ------------------------------------------------------------------------------------------

        Private Shared Function MakeSubagentId(parentId As String) As String
            Dim prefix = If(parentId.Length > 48, parentId.Substring(0, 48), parentId)
            Return prefix & ".sub-" & Guid.NewGuid().ToString("N").Substring(0, 6)
        End Function

        Private Function SpawnableFor(parent As EffectiveProfile) As List(Of String)
            If _access IsNot Nothing Then Return _access.SpawnableModels(parent)
            Return parent.AllowedModels.ToList()
        End Function

        Friend Function SpawnSubagentAsync(parentId As String, parentTask As AgentTask, req As SpawnRequest) As Task(Of SpawnResult) Implements ISubagentHost.SpawnAsync
            Return SpawnCoreAsync(parentId, parentTask, req)
        End Function

        Private Async Function SpawnCoreAsync(parentId As String, parentTask As AgentTask, req As SpawnRequest) As Task(Of SpawnResult)
            Dim o = _options.Subagents
            If _db Is Nothing OrElse Not o.Enabled Then Return SpawnResult.Fail("SUBAGENTS_DISABLED", "Subagents are disabled on this host.")
            If Not _acceptingWork Then Return SpawnResult.Fail("HOST_SHUTTING_DOWN", "The host is shutting down.")
            Dim parent = _profiles.TryGetProfile(parentId)
            If parent Is Nothing Then Return SpawnResult.Fail("AGENT_NOT_FOUND", "Agent does not exist.")
            If parent.IsSubagent Then Return SpawnResult.Fail("NOT_ALLOWED", "Subagents cannot spawn further subagents.")
            If parent.MaxSubagents <= 0 Then Return SpawnResult.Fail("NOT_ALLOWED", "This agent is not permitted to spawn subagents.")
            Dim name = TextUtil.Truncate(If(req.Name, "").Trim(), 100)
            If name.Length = 0 Then Return SpawnResult.Fail("INVALID_REQUEST", "A subagent name is required.")
            If String.IsNullOrWhiteSpace(req.Instructions) Then Return SpawnResult.Fail("INVALID_REQUEST", "Instructions are required.")

            If Not String.IsNullOrEmpty(req.IdempotencyKey) Then
                Dim existing = Await _db.QuerySingleAsync("SELECT * FROM Subagents WHERE ParentAgentId=@p AND IdempotencyKey=@k", AddressOf MapSubagent,
                                                          ("@p", CObj(parentId)), ("@k", req.IdempotencyKey)).ConfigureAwait(False)
                If existing IsNot Nothing Then
                    Return New SpawnResult With {.Success = True, .Duplicate = True, .SubagentId = existing.Id, .TaskId = existing.TaskId,
                                                 .DeadlineUtc = If(existing.DeadlineUtc, DateTime.UtcNow), .SpendCapMicros = If(existing.SpendCapMicros, 0L)}
                End If
            End If

            Await _spawnLock.WaitAsync().ConfigureAwait(False)
            Try
                Dim mine = Await _db.ScalarLongAsync("SELECT COUNT(*) FROM Subagents WHERE ParentAgentId=@p AND Status='Running'", ("@p", CObj(parentId))).ConfigureAwait(False)
                If mine >= parent.MaxSubagents Then
                    Return SpawnResult.Fail("SUBAGENT_LIMIT", $"You already have {mine} running subagent(s) (limit {parent.MaxSubagents}). Wait for them (wait_for_subagents) first.")
                End If
                Dim total = Await _db.ScalarLongAsync("SELECT COUNT(*) FROM Subagents WHERE Status='Running'").ConfigureAwait(False)
                If total >= o.MaxTotal Then Return SpawnResult.Fail("HOST_SUBAGENT_LIMIT", $"The host is running its maximum of {o.MaxTotal} subagents. Try again later or do the work yourself.")
                If parentTask IsNot Nothing Then
                    Dim perTask = Await _db.ScalarLongAsync("SELECT COUNT(*) FROM Subagents WHERE ParentTaskId=@t", ("@t", CObj(parentTask.Id))).ConfigureAwait(False)
                    If perTask >= o.MaxPerTask Then Return SpawnResult.Fail("TASK_SUBAGENT_LIMIT", $"This task already spawned {perTask} subagents (limit {o.MaxPerTask}).")
                End If

                ' Models: a subset of what the parent may hand on (profile models + grants that allow subagents).
                Dim spawnable = SpawnableFor(parent)
                Dim models = If(req.Models IsNot Nothing AndAlso req.Models.Count > 0, req.Models.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), spawnable)
                Dim notAllowed = models.Where(Function(m) Not spawnable.Contains(m, StringComparer.OrdinalIgnoreCase)).ToList()
                If notAllowed.Count > 0 Then Return SpawnResult.Fail("MODEL_NOT_ALLOWED", $"You cannot give a subagent: {String.Join(", ", notAllowed)}. Allowed: {String.Join(", ", spawnable)}.")
                If models.Count = 0 Then Return SpawnResult.Fail("NO_MODELS", "You have no models that subagents may use.")

                ' Cloud spend cap, paid from the parent's wallet.
                Dim capUsd = If(req.SpendCapUsd, If(parent.SubagentDefaultSpendCap, o.DefaultSpendCap))
                If capUsd < 0 Then capUsd = 0
                If o.MaxSpendCap.HasValue AndAlso capUsd > o.MaxSpendCap.Value Then
                    Return SpawnResult.Fail("CAP_TOO_HIGH", $"budget_limit_usd may not exceed ${o.MaxSpendCap.Value:0.00}.")
                End If
                Dim capMicros = TextUtil.DollarsToMicros(capUsd)
                Dim timeout = If(req.TimeoutMinutes > 0, Math.Min(req.TimeoutMinutes, o.MaxTimeoutMinutes), o.DefaultTimeoutMinutes)

                Dim attachments = If(req.Attachments, New List(Of String)).Distinct(StringComparer.Ordinal).ToList()
                For Each art In attachments
                    If Not Await _artifactStore.CanReadAsync(parentId, art).ConfigureAwait(False) Then Return SpawnResult.Fail("ARTIFACT_NOT_FOUND", $"You cannot share artifact '{art}'.")
                Next

                Dim id = MakeSubagentId(parentId)
                Dim spec As New SubagentSpec With {
                    .Name = name, .Models = models, .Purpose = req.Purpose, .SpendCapMicros = capMicros, .TimeoutMinutes = timeout,
                    .Priority = If(req.Priority, If(parentTask?.Priority, PriorityLevel.Normal))}
                If spec.Priority > PriorityLevel.High Then spec.Priority = PriorityLevel.High
                Dim profile = ProfileResolver.DeriveSubagent(parent, id, spec)
                Dim now = DateTime.UtcNow
                Dim deadline = now.AddMinutes(timeout)
                Dim description = $"Assigned by your parent agent {parentId}:" & vbLf & req.Instructions.Trim()
                If attachments.Count > 0 Then description &= vbLf & vbLf & "Shared artifacts you can read with read_artifact: " & String.Join(", ", attachments)
                Dim ancestry = New List(Of String)(If(parentTask?.Ancestry, New List(Of String)))
                ancestry.Add(parentId)
                Dim t As New AgentTask With {
                    .Id = Ids.NewId("tsk"), .AgentId = id, .Name = name, .Description = TextUtil.Truncate(description, 20000), .Kind = TaskKind.Delegated,
                    .Handler = "reasoning", .Purpose = req.Purpose, .Priority = spec.Priority, .Payload = req.PayloadJson, .MaxRetries = profile.MaxRetries,
                    .CreatedUtc = now, .UpdatedUtc = now, .ParentTaskId = parentTask?.Id, .ParentAgentId = parentId, .DelegatingAgentId = parentId,
                    .CorrelationId = If(parentTask?.CorrelationId, Ids.NewId("cor")), .DelegationDepth = If(parentTask?.DelegationDepth, 0) + 1,
                    .ChainLength = If(parentTask?.ChainLength, 0) + 1, .Ancestry = ancestry, .CreatedBy = parentId, .StartTier = req.StartTier}

                Dim inserted = Await _db.InTransactionAsync(Of Boolean)(
                    Async Function(conn, tx)
                        Dim n = Await Database.TxExecAsync(conn, tx,
                            "INSERT INTO Subagents(Id, ParentAgentId, ParentTaskId, TaskId, Name, SpecJson, SpendCapMicros, SpentMicros, Status, IdempotencyKey, CreatedBy, CreatedUtc, DeadlineUtc) " &
                            "VALUES (@id, @p, @pt, @t, @n, @spec, @cap, 0, 'Running', @k, @by, @now, @dl) ON CONFLICT DO NOTHING",
                            ("@id", CObj(id)), ("@p", parentId), ("@pt", parentTask?.Id), ("@t", t.Id), ("@n", name), ("@spec", JsonUtil.ToJson(spec)),
                            ("@cap", capMicros), ("@k", req.IdempotencyKey), ("@by", parentId), ("@now", Db.Ts(now)), ("@dl", Db.Ts(deadline))).ConfigureAwait(False)
                        If n = 0 Then Return False
                        Await TaskRepository.TxInsertAsync(conn, tx, t).ConfigureAwait(False)
                        Return True
                    End Function).ConfigureAwait(False)
                If Not inserted Then Return SpawnResult.Fail("DUPLICATE", "This subagent was already started.")
                Await _tasks.AddHistoryAsync(t.Id, id, Nothing, AgentTaskStatus.Pending, $"Subagent task from {parentId}").ConfigureAwait(False)

                RegisterSubagentRuntime(profile, capMicros, 0, start:=True)
                For Each art In attachments
                    Try
                        Await _artifactStore.GrantAsync(parentId, art, id, Nothing).ConfigureAwait(False)
                    Catch ex As ArtifactAccessException
                        _log.Warn("Subagents", $"Could not share {art} with {id}: {ex.Message}", parentId)
                    End Try
                Next
                _subDeadlines(id) = deadline
                _events.Emit(AgentEventType.SubagentSpawned, $"{parentId} spawned subagent {id}: {name}", parentId, parentTask?.Id, correlationId:=t.CorrelationId,
                             data:=New Dictionary(Of String, Object) From {{"subagent", id}, {"taskId", t.Id}, {"models", models}, {"capMicros", capMicros}, {"timeoutMinutes", timeout}})
                _events.Emit(AgentEventType.TaskCreated, $"Subagent task: {name}", id, t.Id, correlationId:=t.CorrelationId)
                Return New SpawnResult With {.Success = True, .SubagentId = id, .TaskId = t.Id, .DeadlineUtc = deadline, .SpendCapMicros = capMicros}
            Finally
                _spawnLock.Release()
            End Try
        End Function

        Private Sub RegisterSubagentRuntime(profile As EffectiveProfile, capMicros As Long?, spentMicros As Long, start As Boolean)
            _profiles.Set(profile)
            _desiredStates(profile.Id) = "Running"
            _caps?.Register(profile.Id, capMicros, spentMicros)
            Dim agentId = profile.Id
            Dim ctxHolder As AgentRuntimeContext = Nothing
            Dim scope = _scopes.Create(agentId, Function() If(ctxHolder?.Profile, profile), Sub() Wake(agentId))
            Dim ctx As New AgentRuntimeContext(agentId, profile, scope)
            ctxHolder = ctx
            Dim r As New AgentRuntime(ctx, _svc)
            _runtimes(agentId) = r
            If start AndAlso _acceptingWork Then r.Start(_hostToken)
        End Sub

        ' ------------------------------------------------------------------------------------------
        ' Finish / cancel / retire
        ' ------------------------------------------------------------------------------------------

        ''' <summary>Records the subagent's outcome, hands its artifacts and result to the parent, resumes the parent and retires the runtime.</summary>
        Private Async Function FinishSubagentAsync(subagentId As String, t As AgentTask) As Task
            Dim rec = Await _db.QuerySingleAsync("SELECT * FROM Subagents WHERE Id=@id", AddressOf MapSubagent, ("@id", CObj(subagentId))).ConfigureAwait(False)
            If rec Is Nothing OrElse rec.Status <> SubagentStatus.Running.ToString() Then
                Await RetireSubagentAsync(subagentId).ConfigureAwait(False)
                Return
            End If
            Dim status As SubagentStatus
            Select Case If(t?.Status, AgentTaskStatus.Cancelled)
                Case AgentTaskStatus.Completed : status = SubagentStatus.Completed
                Case AgentTaskStatus.Failed : status = SubagentStatus.Failed
                Case Else : status = SubagentStatus.Cancelled
            End Select
            Dim result = t?.GetResult()
            If result Is Nothing Then result = TaskResultContract.Fail(If(t?.ErrorMessage, "Subagent was cancelled."))
            Dim spent = Await _wallets.SpentByAsync(subagentId).ConfigureAwait(False)
            Dim artifacts = Await _artifactStore.TransferOwnershipAsync(subagentId, rec.ParentAgentId).ConfigureAwait(False)
            Dim resolution = Resolutions.SubagentResult(subagentId, rec.Name, status.ToString(), result, artifacts, spent)
            Dim n = Await _db.ExecuteAsync("UPDATE Subagents SET Status=@s, ResultJson=@r, SpentMicros=@sp, EndedUtc=@now WHERE Id=@id AND Status='Running'",
                                           ("@s", CObj(status.ToString())), ("@r", resolution), ("@sp", spent), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", subagentId)).ConfigureAwait(False)
            If n > 0 Then
                Await _waitResolver.ResolveAsync(rec.ParentAgentId, "sub:" & subagentId, resolution, $"Subagent {subagentId} {status}").ConfigureAwait(False)
                Wake(rec.ParentAgentId)
                _events.Emit(AgentEventType.SubagentFinished, $"Subagent {subagentId} ({rec.Name}) {status}: {TextUtil.Truncate(result.Summary, 140)}" &
                             If(spent > 0, $" — cloud cost {TextUtil.FormatMicros(spent)} charged to {rec.ParentAgentId}", ""),
                             rec.ParentAgentId, rec.ParentTaskId, level:=If(status = SubagentStatus.Completed, EventLevel.Info, EventLevel.Warning),
                             data:=New Dictionary(Of String, Object) From {{"subagent", subagentId}, {"status", status.ToString()}, {"spentMicros", spent}, {"artifacts", artifacts}})
            End If
            Await RetireSubagentAsync(subagentId).ConfigureAwait(False)
        End Function

        Private Async Function RetireSubagentAsync(subagentId As String) As Task
            Dim r As AgentRuntime = Nothing
            If _runtimes.TryRemove(subagentId, r) Then
                Try
                    Await r.StopAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(False)
                Catch ex As Exception
                    _log.Warn("Subagents", "Subagent did not stop cleanly: " & ex.Message, subagentId)
                End Try
            End If
            _profiles.Remove(subagentId)
            Dim ignored As String = Nothing
            _desiredStates.TryRemove(subagentId, ignored)
            _caps?.Remove(subagentId)
            Dim ignoredDl As DateTime
            _subDeadlines.TryRemove(subagentId, ignoredDl)
        End Function

        ''' <summary>Stops a running subagent (admin, parent, timeout or parent-task end). Its parent receives a Cancelled result.</summary>
        Public Async Function CancelSubagentAsync(subagentId As String, reason As String) As Task(Of AgentOperationResult)
            Dim rec = Await GetSubagentAsync(subagentId).ConfigureAwait(False)
            If rec Is Nothing Then Return AgentOperationResult.Fail("SUBAGENT_NOT_FOUND", "Subagent does not exist.")
            If rec.Status <> SubagentStatus.Running.ToString() Then Return AgentOperationResult.Fail("SUBAGENT_NOT_RUNNING", $"Subagent is already {rec.Status}.")
            reason = If(String.IsNullOrWhiteSpace(reason), "Cancelled", reason)
            For attempt = 1 To 15
                Dim t = Await _tasks.GetAsync(rec.TaskId).ConfigureAwait(False)
                If t Is Nothing OrElse t.IsTerminal Then
                    Await FinishSubagentAsync(subagentId, t).ConfigureAwait(False)
                    Return AgentOperationResult.Ok("Subagent finished.")
                End If
                If t.Status = AgentTaskStatus.Running Then
                    Dim r As AgentRuntime = Nothing
                    If _runtimes.TryGetValue(subagentId, r) AndAlso r.CancelCurrentTask(t.Id, "cancel:" & reason) Then Return AgentOperationResult.Ok("Cancellation requested.")
                    Await Task.Delay(200).ConfigureAwait(False)
                    Continue For
                End If
                If Await _tasks.TryTransitionAsync(t.Id, {AgentTaskStatus.Pending, AgentTaskStatus.Retrying, AgentTaskStatus.Waiting, AgentTaskStatus.Blocked},
                                                   AgentTaskStatus.Cancelled, reason, reason).ConfigureAwait(False) Then
                    Await FinishSubagentAsync(subagentId, Await _tasks.GetAsync(t.Id).ConfigureAwait(False)).ConfigureAwait(False)
                    Return AgentOperationResult.Ok("Subagent cancelled.")
                End If
            Next
            ' The runtime could not be reached (e.g. its loop was stopped while running): close the record anyway.
            Await FinishSubagentAsync(subagentId, Nothing).ConfigureAwait(False)
            Return AgentOperationResult.Ok("Subagent closed.")
        End Function

        ' ------------------------------------------------------------------------------------------
        ' ISubagentHost (parent-scoped)
        ' ------------------------------------------------------------------------------------------

        Friend Function ListForParentAsync(parentId As String, parentTaskId As String, limit As Integer) As Task(Of List(Of SubagentInfo)) Implements ISubagentHost.ListForParentAsync
            Return ListSubagentsAsync(parentId, parentTaskId, Nothing, limit)
        End Function

        Friend Async Function GetForParentAsync(parentId As String, subagentId As String) As Task(Of SubagentInfo) Implements ISubagentHost.GetForParentAsync
            Dim s = Await GetSubagentAsync(subagentId).ConfigureAwait(False)
            If s Is Nothing OrElse Not String.Equals(s.ParentAgentId, parentId, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Return s
        End Function

        Friend Async Function CancelForParentAsync(parentId As String, subagentId As String, reason As String) As Task(Of AgentOperationResult) Implements ISubagentHost.CancelForParentAsync
            Dim s = Await GetForParentAsync(parentId, subagentId).ConfigureAwait(False)
            If s Is Nothing Then Return AgentOperationResult.Fail("SUBAGENT_NOT_FOUND", $"'{subagentId}' is not one of your subagents.")
            Return Await CancelSubagentAsync(subagentId, $"Cancelled by {parentId}: {reason}").ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------------------------------
        ' ITaskObserver
        ' ------------------------------------------------------------------------------------------

        Friend Sub OnTaskFinished(t As AgentTask) Implements ITaskObserver.OnTaskFinished
            If t Is Nothing Then Return
            Try
                Outcomes?.OnTaskFinished(t)
            Catch ex As Exception
                _log.Warn("Outcomes", "Post-task review/delivery failed: " & ex.Message, t.AgentId, t.Id)
            End Try
            Try
                ChatReports?.OnTaskFinished(t)
            Catch ex As Exception
                _log.Warn("ChatReports", "Posting the result to its chat failed: " & ex.Message, t.AgentId, t.Id)
            End Try
            Try
                Autonomy?.OnTaskFinished(t)
            Catch ex As Exception
                _log.Warn("Autonomy", "After-task autonomy handling failed: " & ex.Message, t.AgentId, t.Id)
            End Try
            If _db Is Nothing Then Return
            Dim ignored = Task.Run(Function() HandleTaskFinishedAsync(t))
        End Sub

        Private Async Function HandleTaskFinishedAsync(t As AgentTask) As Task
            Try
                Dim p = _profiles.TryGetProfile(t.AgentId)
                If p IsNot Nothing AndAlso p.IsSubagent Then
                    Await FinishSubagentAsync(t.AgentId, t).ConfigureAwait(False)
                    Return
                End If
                ' Subagents are bound to the task that spawned them: stop any that are still running.
                Dim children = Await ListSubagentsAsync(Nothing, t.Id, SubagentStatus.Running.ToString(), 100).ConfigureAwait(False)
                For Each c In children
                    Await CancelSubagentAsync(c.Id, $"Parent task ended ({t.Status}) before this subagent finished").ConfigureAwait(False)
                Next
            Catch ex As Exception
                _log.Warn("Subagents", "Post-task subagent handling failed: " & ex.Message, t.AgentId, t.Id)
            End Try
        End Function

        Friend Async Function TryLateResolveAsync(agentId As String, waitKey As String) As Task(Of String) Implements ITaskObserver.TryLateResolveAsync
            ' A task this agent assigned (assign_task) that finished before the agent started waiting for it.
            If waitKey IsNot Nothing AndAlso waitKey.StartsWith("task:", StringComparison.OrdinalIgnoreCase) Then
                Dim assigned = Await _tasks.GetAsync(waitKey.Substring(5)).ConfigureAwait(False)
                If assigned Is Nothing Then Return Resolutions.Timeout($"Task {waitKey.Substring(5)} does not exist.")
                If Not String.Equals(assigned.CreatedBy, agentId, StringComparison.OrdinalIgnoreCase) OrElse Not assigned.IsTerminal Then Return Nothing
                Return Resolutions.TaskResult(assigned)
            End If
            If _db Is Nothing OrElse waitKey Is Nothing OrElse Not waitKey.StartsWith("sub:", StringComparison.OrdinalIgnoreCase) Then Return Nothing
            Dim s = Await GetSubagentAsync(waitKey.Substring(4)).ConfigureAwait(False)
            If s Is Nothing Then Return Resolutions.Timeout($"Subagent {waitKey.Substring(4)} does not exist.")
            If Not String.Equals(s.ParentAgentId, agentId, StringComparison.OrdinalIgnoreCase) Then Return Nothing
            If s.Status = SubagentStatus.Running.ToString() Then Return Nothing
            Return s.ResultJson
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Startup recovery, deadlines, retention
        ' ------------------------------------------------------------------------------------------

        ''' <summary>Rebuilds running subagents after a restart (their tasks resume from checkpoints) and closes orphans.</summary>
        Private Async Function RestoreSubagentsAsync() As Task
            If _db Is Nothing Then Return
            Dim running = Await ListSubagentsAsync(Nothing, Nothing, SubagentStatus.Running.ToString(), 500).ConfigureAwait(False)
            For Each rec In running
                Try
                    Dim parent = _profiles.TryGetProfile(rec.ParentAgentId)
                    Dim t = Await _tasks.GetAsync(rec.TaskId).ConfigureAwait(False)
                    If parent Is Nothing OrElse parent.IsSubagent OrElse t Is Nothing Then
                        If t IsNot Nothing AndAlso Not t.IsTerminal Then
                            Await _tasks.TryTransitionAsync(t.Id, {AgentTaskStatus.Pending, AgentTaskStatus.Retrying, AgentTaskStatus.Waiting, AgentTaskStatus.Blocked, AgentTaskStatus.Running},
                                                            AgentTaskStatus.Cancelled, "Parent agent no longer exists", "Parent agent no longer exists").ConfigureAwait(False)
                            t = Await _tasks.GetAsync(t.Id).ConfigureAwait(False)
                        End If
                        Await FinishSubagentAsync(rec.Id, t).ConfigureAwait(False)
                        Continue For
                    End If
                    If t.IsTerminal Then
                        Await FinishSubagentAsync(rec.Id, t).ConfigureAwait(False)
                        Continue For
                    End If
                    Dim spec = Await _db.ScalarAsync("SELECT SpecJson FROM Subagents WHERE Id=@id", ("@id", CObj(rec.Id))).ConfigureAwait(False)
                    Dim s = If(JsonUtil.FromJson(Of SubagentSpec)(CStr(spec)), New SubagentSpec With {.Name = rec.Name, .Models = rec.Models, .TimeoutMinutes = 30})
                    Dim profile = ProfileResolver.DeriveSubagent(parent, rec.Id, s)
                    Dim spent = Await _wallets.SpentByAsync(rec.Id).ConfigureAwait(False)
                    RegisterSubagentRuntime(profile, s.SpendCapMicros, spent, start:=False)
                    _subDeadlines(rec.Id) = If(rec.DeadlineUtc, DateTime.UtcNow.AddMinutes(Math.Max(5, s.TimeoutMinutes)))
                Catch ex As Exception
                    _log.Warn("Subagents", $"Could not restore subagent {rec.Id}: {ex.Message}", rec.ParentAgentId)
                End Try
            Next
        End Function

        ''' <summary>Watchdog: subagents past their deadline are cancelled (their parent receives the cancellation as the result).</summary>
        Private Sub SweepSubagentDeadlines(now As DateTime)
            For Each kv In _subDeadlines.ToArray()
                If now <= kv.Value.AddMinutes(1) Then Continue For
                Dim removed As DateTime
                If Not _subDeadlines.TryRemove(kv.Key, removed) Then Continue For
                Dim id = kv.Key
                Dim ignored = Task.Run(Async Function()
                                           Try
                                               Await CancelSubagentAsync(id, "Subagent exceeded its time limit").ConfigureAwait(False)
                                           Catch ex As Exception
                                               _log.Warn("Subagents", "Deadline cancellation failed: " & ex.Message, id)
                                           End Try
                                       End Function)
            Next
        End Sub

        Public Function PurgeFinishedSubagentsAsync(olderThanUtc As DateTime) As Task(Of Integer)
            If _db Is Nothing Then Return Task.FromResult(0)
            Return _db.ExecuteAsync("DELETE FROM Subagents WHERE Status<>'Running' AND EndedUtc IS NOT NULL AND EndedUtc < @t", ("@t", CObj(Db.Ts(olderThanUtc))))
        End Function

        Private Async Function CancelAllSubagentsOfAsync(parentId As String, reason As String) As Task(Of Integer)
            Dim n = 0
            For Each s In Await ListSubagentsAsync(parentId, Nothing, SubagentStatus.Running.ToString(), 500).ConfigureAwait(False)
                If (Await CancelSubagentAsync(s.Id, reason).ConfigureAwait(False)).Success Then n += 1
            Next
            Return n
        End Function
    End Class

End Namespace
