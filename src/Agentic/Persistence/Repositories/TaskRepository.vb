Option Strict On
Option Explicit On
Option Infer On

Imports Microsoft.Data.Sqlite
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Persistence

    Public NotInheritable Class TaskRepository
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        ' Safe: Columns is a compile-time constant, never interpolate runtime values this way
        Private Const Columns As String =
            "Id, AgentId, Name, Description, Kind, Handler, Purpose, Status, Priority, Payload, Result, Error, RetryCount, MaxRetries, NextRunUtc, " &
            "CreatedUtc, StartedUtc, CompletedUtc, UpdatedUtc, ParentTaskId, ParentAgentId, DelegatingAgentId, CorrelationId, DelegationDepth, " &
            "ChainLength, Ancestry, SourceMessageId, ScheduleId, StartTier, CurrentLadderLevel, EscalationCount, Checkpoint, WaitingOn, " &
            "WaitDeadlineUtc, LeaseOwner, LeaseExpiresUtc, CostMicros, CreatedBy, IdempotencyKey, QueueRank, ModelKey, OptionsJson, ReviewCriteria, " &
            "DependsOn, DeadlineUtc, OffPeak, Weight, AutoApproveTools, DeadlineEnforced, RunPastDeadline, DeadlinePassedUtc"

        ''' <summary>Statuses that hold a position in an agent's queue.</summary>
        Private Const RankedStatuses As String = "'Pending','Retrying','Waiting','Blocked'"
        Private Const RankStep As Double = 1000.0
        Private Const MinRankGap As Double = 0.000001

        Public Shared Function Map(r As SqliteDataReader) As AgentTask
            Dim rankOrd = r.GetOrdinal("QueueRank")
            Return New AgentTask With {
                .Id = Db.Str(r, "Id"),
                .AgentId = Db.Str(r, "AgentId"),
                .Name = Db.Str(r, "Name"),
                .Description = Db.Str(r, "Description"),
                .Kind = Db.EnumOf(r, "Kind", TaskKind.OneTime),
                .Handler = Db.Str(r, "Handler"),
                .Purpose = ParsePurpose(Db.Str(r, "Purpose")),
                .Status = Db.EnumOf(r, "Status", AgentTaskStatus.Pending),
                .Priority = CType(Db.Int(r, "Priority"), PriorityLevel),
                .Payload = Db.Str(r, "Payload"),
                .Result = Db.Str(r, "Result"),
                .ErrorMessage = Db.Str(r, "Error"),
                .RetryCount = Db.Int(r, "RetryCount"),
                .MaxRetries = Db.Int(r, "MaxRetries"),
                .NextRunUtc = Db.DtN(r, "NextRunUtc"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"),
                .StartedUtc = Db.DtN(r, "StartedUtc"),
                .CompletedUtc = Db.DtN(r, "CompletedUtc"),
                .UpdatedUtc = Db.Dt(r, "UpdatedUtc"),
                .ParentTaskId = Db.Str(r, "ParentTaskId"),
                .ParentAgentId = Db.Str(r, "ParentAgentId"),
                .DelegatingAgentId = Db.Str(r, "DelegatingAgentId"),
                .CorrelationId = Db.Str(r, "CorrelationId"),
                .DelegationDepth = Db.Int(r, "DelegationDepth"),
                .ChainLength = Db.Int(r, "ChainLength"),
                .Ancestry = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "Ancestry")), New List(Of String)),
                .SourceMessageId = Db.Str(r, "SourceMessageId"),
                .ScheduleId = Db.Str(r, "ScheduleId"),
                .StartTier = Db.IntN(r, "StartTier"),
                .CurrentLadderLevel = Db.Int(r, "CurrentLadderLevel"),
                .EscalationCount = Db.Int(r, "EscalationCount"),
                .Checkpoint = Db.Str(r, "Checkpoint"),
                .WaitingOn = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "WaitingOn")), New List(Of String)),
                .WaitDeadlineUtc = Db.DtN(r, "WaitDeadlineUtc"),
                .LeaseOwner = Db.Str(r, "LeaseOwner"),
                .LeaseExpiresUtc = Db.DtN(r, "LeaseExpiresUtc"),
                .CostMicros = Db.Lng(r, "CostMicros"),
                .CreatedBy = Db.Str(r, "CreatedBy"),
                .IdempotencyKey = Db.Str(r, "IdempotencyKey"),
                .QueueRank = If(r.IsDBNull(rankOrd), CType(Nothing, Double?), r.GetDouble(rankOrd)),
                .ModelKey = Db.Str(r, "ModelKey"),
                .Delivery = JsonUtil.FromJson(Of TaskDelivery)(Db.Str(r, "OptionsJson")),
                .ReviewCriteria = Db.Str(r, "ReviewCriteria"),
                .DependsOn = If(JsonUtil.FromJson(Of List(Of String))(Db.Str(r, "DependsOn")), New List(Of String)),
                .DeadlineUtc = Db.DtN(r, "DeadlineUtc"),
                .OffPeak = Db.Int(r, "OffPeak") <> 0,
                .Weight = Db.Int(r, "Weight"),
                .AutoApproveTools = Db.Int(r, "AutoApproveTools") <> 0,
                .DeadlineEnforced = Db.Int(r, "DeadlineEnforced") <> 0,
                .RunPastDeadline = Db.Int(r, "RunPastDeadline") <> 0,
                .DeadlinePassedUtc = Db.DtN(r, "DeadlinePassedUtc")
            }
        End Function

        Private Shared Function ParsePurpose(s As String) As AiPurpose?
            Dim p As AiPurpose
            If s IsNot Nothing AndAlso [Enum].TryParse(s, True, p) Then Return p
            Return Nothing
        End Function

        Private Shared Function Params(t As AgentTask) As (String, Object)()
            Return {
                ("@Id", CObj(t.Id)), ("@AgentId", t.AgentId), ("@Name", t.Name), ("@Description", t.Description), ("@Kind", t.Kind),
                ("@Handler", t.Handler), ("@Purpose", If(t.Purpose.HasValue, CObj(t.Purpose.Value.ToString()), Nothing)), ("@Status", t.Status),
                ("@Priority", CInt(t.Priority)), ("@Payload", t.Payload), ("@Result", t.Result), ("@Error", t.ErrorMessage),
                ("@RetryCount", t.RetryCount), ("@MaxRetries", t.MaxRetries), ("@NextRunUtc", Db.Ts(t.NextRunUtc)),
                ("@CreatedUtc", Db.Ts(t.CreatedUtc)), ("@StartedUtc", Db.Ts(t.StartedUtc)), ("@CompletedUtc", Db.Ts(t.CompletedUtc)),
                ("@UpdatedUtc", Db.Ts(t.UpdatedUtc)), ("@ParentTaskId", t.ParentTaskId), ("@ParentAgentId", t.ParentAgentId),
                ("@DelegatingAgentId", t.DelegatingAgentId), ("@CorrelationId", t.CorrelationId), ("@DelegationDepth", t.DelegationDepth),
                ("@ChainLength", t.ChainLength), ("@Ancestry", JsonUtil.ToJson(If(t.Ancestry, New List(Of String)))),
                ("@SourceMessageId", t.SourceMessageId), ("@ScheduleId", t.ScheduleId),
                ("@StartTier", If(t.StartTier.HasValue, CObj(t.StartTier.Value), Nothing)), ("@CurrentLadderLevel", t.CurrentLadderLevel),
                ("@EscalationCount", t.EscalationCount), ("@Checkpoint", t.Checkpoint),
                ("@WaitingOn", JsonUtil.ToJson(If(t.WaitingOn, New List(Of String)))), ("@WaitDeadlineUtc", Db.Ts(t.WaitDeadlineUtc)),
                ("@LeaseOwner", t.LeaseOwner), ("@LeaseExpiresUtc", Db.Ts(t.LeaseExpiresUtc)), ("@CostMicros", t.CostMicros),
                ("@CreatedBy", t.CreatedBy), ("@IdempotencyKey", t.IdempotencyKey),
                ("@QueueRank", If(t.QueueRank.HasValue, CObj(t.QueueRank.Value), Nothing)), ("@ModelKey", t.ModelKey),
                ("@OptionsJson", If(t.Delivery Is Nothing, Nothing, JsonUtil.ToJson(t.Delivery))), ("@ReviewCriteria", t.ReviewCriteria),
                ("@DependsOn", If(t.DependsOn Is Nothing OrElse t.DependsOn.Count = 0, Nothing, CObj(JsonUtil.ToJson(t.DependsOn)))),
                ("@DeadlineUtc", Db.Ts(t.DeadlineUtc)), ("@OffPeak", If(t.OffPeak, 1, 0)), ("@Weight", t.Weight),
                ("@AutoApproveTools", If(t.AutoApproveTools, 1, 0)), ("@DeadlineEnforced", If(t.DeadlineEnforced, 1, 0)),
                ("@RunPastDeadline", If(t.RunPastDeadline, 1, 0)), ("@DeadlinePassedUtc", Db.Ts(t.DeadlinePassedUtc))
            }
        End Function

        Private Shared ReadOnly InsertSql As String =
            $"INSERT INTO AgentTasks ({Columns}) VALUES (@Id, @AgentId, @Name, @Description, @Kind, @Handler, @Purpose, @Status, @Priority, @Payload, " &
            "@Result, @Error, @RetryCount, @MaxRetries, @NextRunUtc, @CreatedUtc, @StartedUtc, @CompletedUtc, @UpdatedUtc, @ParentTaskId, @ParentAgentId, " &
            "@DelegatingAgentId, @CorrelationId, @DelegationDepth, @ChainLength, @Ancestry, @SourceMessageId, @ScheduleId, @StartTier, @CurrentLadderLevel, " &
            "@EscalationCount, @Checkpoint, @WaitingOn, @WaitDeadlineUtc, @LeaseOwner, @LeaseExpiresUtc, @CostMicros, @CreatedBy, @IdempotencyKey, @QueueRank, @ModelKey, @OptionsJson, @ReviewCriteria, " &
            "@DependsOn, @DeadlineUtc, @OffPeak, @Weight, @AutoApproveTools, @DeadlineEnforced, @RunPastDeadline, @DeadlinePassedUtc) " &
            "ON CONFLICT DO NOTHING"

        ' QueueRank and ModelKey are deliberately not written here: the agent loop holds stale copies of running tasks,
        ' and queue order / model pins are only changed through the dedicated admin operations below.
        Private Const UpdateSql As String =
            "UPDATE AgentTasks SET Name=@Name, Description=@Description, Status=@Status, Priority=@Priority, Payload=@Payload, Result=@Result, Error=@Error, " &
            "RetryCount=@RetryCount, MaxRetries=@MaxRetries, NextRunUtc=@NextRunUtc, StartedUtc=@StartedUtc, CompletedUtc=@CompletedUtc, UpdatedUtc=@UpdatedUtc, " &
            "CurrentLadderLevel=@CurrentLadderLevel, EscalationCount=@EscalationCount, Checkpoint=@Checkpoint, WaitingOn=@WaitingOn, " &
            "WaitDeadlineUtc=@WaitDeadlineUtc, LeaseOwner=@LeaseOwner, LeaseExpiresUtc=@LeaseExpiresUtc, CostMicros=@CostMicros WHERE Id=@Id"

        ''' <summary>Inserts a task and gives it a queue position. Returns False if the SourceMessageId/IdempotencyKey already exists.</summary>
        Public Function InsertAsync(t As AgentTask, Optional addToFront As Boolean = False) As Task(Of Boolean)
            Return _db.InTransactionAsync(Of Boolean)(Function(conn, tx) TxInsertAsync(conn, tx, t, addToFront))
        End Function

        Public Shared Async Function TxInsertAsync(conn As SqliteConnection, tx As SqliteTransaction, t As AgentTask, Optional addToFront As Boolean = False) As Task(Of Boolean)
            ' Dependency DAG: a task waits as Blocked until every task it depends on has Completed.
            If Not t.IsTerminal AndAlso t.DependsOn IsNot Nothing AndAlso t.DependsOn.Count > 0 Then
                If Not Await TxAllDepsDoneAsync(conn, tx, t.DependsOn).ConfigureAwait(False) Then t.Status = AgentTaskStatus.Blocked
            End If
            If (Await Database.TxExecAsync(conn, tx, InsertSql, Params(t)).ConfigureAwait(False)) = 0 Then Return False
            If Not t.QueueRank.HasValue Then t.QueueRank = Await TxAssignRankAsync(conn, tx, t.AgentId, t.Id, t.Priority, addToFront).ConfigureAwait(False)
            Return True
        End Function

        ''' <summary>True only if every dependency id exists and has Completed.</summary>
        Private Shared Async Function TxAllDepsDoneAsync(conn As SqliteConnection, tx As SqliteTransaction, deps As IEnumerable(Of String)) As Task(Of Boolean)
            For Each d In deps
                If String.IsNullOrWhiteSpace(d) Then Continue For
                Dim st = Await Database.TxScalarAsync(conn, tx, "SELECT Status FROM AgentTasks WHERE Id=@id", ("@id", CObj(d))).ConfigureAwait(False)
                If st Is Nothing OrElse TypeOf st Is DBNull OrElse st.ToString() <> "Completed" Then Return False
            Next
            Return True
        End Function

        ''' <summary>
        ''' After a task completes, moves any of its dependents from Blocked back to Pending once all of their dependencies are
        ''' done, and returns the ids that became runnable (so the caller can wake the agent).
        ''' </summary>
        Public Async Function UnblockDependentsAsync(agentId As String, completedTaskId As String) As Task(Of List(Of String))
            Dim unblocked As New List(Of String)
            If String.IsNullOrWhiteSpace(completedTaskId) Then Return unblocked
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Dim rows = Await Database.TxQueryAsync(conn, tx,
                        "SELECT Id, DependsOn FROM AgentTasks WHERE AgentId=@a AND Status='Blocked' AND DependsOn LIKE @like",
                        Function(r) (Id:=Db.Str(r, "Id"), Dep:=Db.Str(r, "DependsOn")),
                        ("@a", CObj(agentId)), ("@like", CObj("%" & completedTaskId & "%"))).ConfigureAwait(False)
                    For Each row In rows
                        Dim deps = JsonUtil.FromJson(Of List(Of String))(row.Dep)
                        If deps Is Nothing OrElse Not deps.Contains(completedTaskId) Then Continue For
                        If Await TxAllDepsDoneAsync(conn, tx, deps).ConfigureAwait(False) Then
                            Await Database.TxExecAsync(conn, tx, "UPDATE AgentTasks SET Status='Pending', UpdatedUtc=@now WHERE Id=@id AND Status='Blocked'",
                                                       ("@now", CObj(Db.Ts(DateTime.UtcNow))), ("@id", CObj(row.Id))).ConfigureAwait(False)
                            unblocked.Add(row.Id)
                        End If
                    Next
                    Return True
                End Function).ConfigureAwait(False)
            Return unblocked
        End Function

        ' -----------------------------------------------------------------------------------------
        ' Queue order
        ' -----------------------------------------------------------------------------------------

        Private Shared Function ToDbl(v As Object) As Double?
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return Nothing
            Return Convert.ToDouble(v, Globalization.CultureInfo.InvariantCulture)
        End Function

        Private Shared Async Function TxRankedAsync(conn As SqliteConnection, tx As SqliteTransaction, agentId As String, excludeId As String) As Task(Of List(Of (Id As String, Rank As Double, Priority As Integer)))
            Return Await Database.TxQueryAsync(conn, tx,
                $"SELECT Id, QueueRank, Priority FROM AgentTasks WHERE AgentId=@a AND Id<>@x AND QueueRank IS NOT NULL AND Status IN ({RankedStatuses}) ORDER BY QueueRank, CreatedUtc",
                Function(r) (Id:=Db.Str(r, "Id"), Rank:=r.GetDouble(r.GetOrdinal("QueueRank")), Priority:=Db.Int(r, "Priority")), ("@a", CObj(agentId)), ("@x", If(excludeId, ""))).ConfigureAwait(False)
        End Function

        ''' <summary>Rewrites the agent's ranks as 1000, 2000, … in their current order (when gaps become too small).</summary>
        Private Shared Async Function TxRenumberAsync(conn As SqliteConnection, tx As SqliteTransaction, list As List(Of (Id As String, Rank As Double, Priority As Integer))) As Task(Of List(Of (Id As String, Rank As Double, Priority As Integer)))
            Dim result As New List(Of (Id As String, Rank As Double, Priority As Integer))
            For i = 0 To list.Count - 1
                Dim rank = (i + 1) * RankStep
                Await Database.TxExecAsync(conn, tx, "UPDATE AgentTasks SET QueueRank=@r WHERE Id=@id", ("@r", CObj(rank)), ("@id", list(i).Id)).ConfigureAwait(False)
                result.Add((list(i).Id, rank, list(i).Priority))
            Next
            Return result
        End Function

        ''' <summary>Rank between two neighbours (either may be missing). Renumbers the queue when the gap is exhausted.</summary>
        Private Shared Async Function TxRankBetweenAsync(conn As SqliteConnection, tx As SqliteTransaction, list As List(Of (Id As String, Rank As Double, Priority As Integer)), insertIndex As Integer) As Task(Of Double)
            For attempt = 0 To 1
                Dim prev As Double? = If(insertIndex > 0, list(insertIndex - 1).Rank, CType(Nothing, Double?))
                Dim nxt As Double? = If(insertIndex < list.Count, list(insertIndex).Rank, CType(Nothing, Double?))
                If Not prev.HasValue AndAlso Not nxt.HasValue Then Return RankStep
                If Not prev.HasValue Then Return nxt.Value - RankStep
                If Not nxt.HasValue Then Return prev.Value + RankStep
                If nxt.Value - prev.Value > MinRankGap Then Return (prev.Value + nxt.Value) / 2.0
                list = Await TxRenumberAsync(conn, tx, list).ConfigureAwait(False)
            Next
            Return (list(insertIndex - 1).Rank + list(insertIndex).Rank) / 2.0
        End Function

        ''' <summary>
        ''' Default placement: after the last queued task of equal or higher priority (so priority order holds by default,
        ''' while respecting any manual reordering), or at the very front.
        ''' </summary>
        Private Shared Async Function TxAssignRankAsync(conn As SqliteConnection, tx As SqliteTransaction, agentId As String, taskId As String, priority As PriorityLevel, toFront As Boolean) As Task(Of Double)
            Dim list = Await TxRankedAsync(conn, tx, agentId, taskId).ConfigureAwait(False)
            Dim index = 0
            If Not toFront Then
                For i = list.Count - 1 To 0 Step -1
                    If list(i).Priority >= CInt(priority) Then
                        index = i + 1
                        Exit For
                    End If
                Next
            End If
            Dim rank = Await TxRankBetweenAsync(conn, tx, list, index).ConfigureAwait(False)
            Await Database.TxExecAsync(conn, tx, "UPDATE AgentTasks SET QueueRank=@r WHERE Id=@id", ("@r", CObj(rank)), ("@id", taskId)).ConfigureAwait(False)
            Return rank
        End Function

        ''' <summary>Gives a task a fresh default queue position (after re-queue or a priority change).</summary>
        Public Function ReRankAsync(taskId As String, Optional toFront As Boolean = False) As Task(Of Boolean)
            Return _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Dim rows = Await Database.TxQueryAsync(conn, tx, "SELECT AgentId, Priority FROM AgentTasks WHERE Id=@id",
                                                           Function(r) (Db.Str(r, "AgentId"), Db.Int(r, "Priority")), ("@id", CObj(taskId))).ConfigureAwait(False)
                    If rows.Count = 0 Then Return False
                    Await TxAssignRankAsync(conn, tx, rows(0).Item1, taskId, CType(rows(0).Item2, PriorityLevel), toFront).ConfigureAwait(False)
                    Return True
                End Function)
        End Function

        ''' <summary>
        ''' Moves a queued task (Pending, Retrying or held) to the front, the back, or directly before/after another queued
        ''' task of the same agent. Returns an error message, or Nothing on success.
        ''' </summary>
        Public Function MoveAsync(agentId As String, taskId As String, Optional beforeTaskId As String = Nothing, Optional afterTaskId As String = Nothing,
                                  Optional toFront As Boolean = False, Optional toBack As Boolean = False) As Task(Of String)
            Return _db.InTransactionAsync(Of String)(
                Async Function(conn, tx)
                    Dim st = Await Database.TxScalarAsync(conn, tx, "SELECT Status FROM AgentTasks WHERE Id=@id AND AgentId=@a", ("@id", CObj(taskId)), ("@a", agentId)).ConfigureAwait(False)
                    If st Is Nothing Then Return "Task does not exist for this agent."
                    Dim status = CStr(st)
                    If status <> "Pending" AndAlso status <> "Retrying" AndAlso status <> "Blocked" Then Return $"Only queued tasks can be moved (task is {status})."
                    Dim list = Await TxRankedAsync(conn, tx, agentId, taskId).ConfigureAwait(False)
                    Dim index As Integer
                    If toFront Then
                        index = 0
                    ElseIf toBack Then
                        index = list.Count
                    Else
                        Dim anchor = If(beforeTaskId, afterTaskId)
                        If String.IsNullOrEmpty(anchor) Then Return "Specify before, after, top or bottom."
                        Dim pos = list.FindIndex(Function(x) String.Equals(x.Id, anchor, StringComparison.Ordinal))
                        If pos < 0 Then Return "The reference task is not in this agent's queue."
                        index = If(beforeTaskId IsNot Nothing, pos, pos + 1)
                    End If
                    Dim rank = Await TxRankBetweenAsync(conn, tx, list, index).ConfigureAwait(False)
                    Await Database.TxExecAsync(conn, tx, "UPDATE AgentTasks SET QueueRank=@r, UpdatedUtc=@now WHERE Id=@id",
                                               ("@r", CObj(rank)), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", taskId)).ConfigureAwait(False)
                    Return Nothing
                End Function)
        End Function

        ''' <summary>Edits a queued task (not running, waiting or finished). Returns an error message or Nothing.</summary>
        Public Function EditQueuedAsync(agentId As String, taskId As String, e As TaskEdit) As Task(Of String)
            Return _db.InTransactionAsync(Of String)(
                Async Function(conn, tx)
                    Dim rows = Await Database.TxQueryAsync(conn, tx, $"SELECT {Columns} FROM AgentTasks WHERE Id=@id AND AgentId=@a", AddressOf Map, ("@id", CObj(taskId)), ("@a", agentId)).ConfigureAwait(False)
                    If rows.Count = 0 Then Return "Task does not exist for this agent."
                    Dim t = rows(0)
                    If t.Status <> AgentTaskStatus.Pending AndAlso t.Status <> AgentTaskStatus.Retrying AndAlso t.Status <> AgentTaskStatus.Blocked Then
                        Return $"Only queued tasks can be edited (task is {t.Status})."
                    End If
                    Dim priorityChanged = e.Priority.HasValue AndAlso e.Priority.Value <> t.Priority
                    If e.Name IsNot Nothing Then t.Name = TextUtil.Truncate(e.Name.Trim(), 200)
                    If String.IsNullOrWhiteSpace(t.Name) Then Return "Task name is required."
                    If e.Description IsNot Nothing Then t.Description = TextUtil.Truncate(e.Description, 20000)
                    If e.Priority.HasValue Then t.Priority = e.Priority.Value
                    If e.ClearPurpose Then t.Purpose = Nothing
                    If e.Purpose.HasValue Then t.Purpose = e.Purpose
                    If e.RunNow Then t.NextRunUtc = Nothing
                    If e.NextRunUtc.HasValue Then t.NextRunUtc = e.NextRunUtc
                    If e.MaxRetries.HasValue Then t.MaxRetries = Math.Clamp(e.MaxRetries.Value, 0, 10)
                    If e.ClearModel Then t.ModelKey = Nothing
                    If Not String.IsNullOrEmpty(e.ModelKey) Then t.ModelKey = e.ModelKey
                    Await Database.TxExecAsync(conn, tx,
                        "UPDATE AgentTasks SET Name=@n, Description=@d, Priority=@p, Purpose=@pu, NextRunUtc=@nr, MaxRetries=@mr, ModelKey=@mk, UpdatedUtc=@now WHERE Id=@id",
                        ("@n", CObj(t.Name)), ("@d", t.Description), ("@p", CInt(t.Priority)), ("@pu", If(t.Purpose.HasValue, CObj(t.Purpose.Value.ToString()), Nothing)),
                        ("@nr", Db.Ts(t.NextRunUtc)), ("@mr", t.MaxRetries), ("@mk", t.ModelKey), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", t.Id)).ConfigureAwait(False)
                    If priorityChanged Then Await TxAssignRankAsync(conn, tx, agentId, t.Id, t.Priority, False).ConfigureAwait(False)
                    Return Nothing
                End Function)
        End Function

        ' -----------------------------------------------------------------------------------------
        ' Reads / transitions
        ' -----------------------------------------------------------------------------------------

        Public Async Function UpdateAsync(t As AgentTask) As Task
            t.UpdatedUtc = DateTime.UtcNow
            Await _db.ExecuteAsync(UpdateSql, Params(t)).ConfigureAwait(False)
        End Function

        Public Function GetAsync(id As String) As Task(Of AgentTask)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM AgentTasks WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Public Function GetForAgentAsync(agentId As String, id As String) As Task(Of AgentTask)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM AgentTasks WHERE Id=@id AND AgentId=@a", AddressOf Map, ("@id", CObj(id)), ("@a", agentId))
        End Function

        Public Function GetBySourceMessageAsync(messageId As String) As Task(Of AgentTask)
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM AgentTasks WHERE SourceMessageId=@m", AddressOf Map, ("@m", CObj(messageId)))
        End Function

        ''' <summary>
        ''' The task whose "approve tool use automatically" covers this one: itself, or a task of the same workflow
        ''' (CorrelationId: the work it handed to other agents and subagents). Nothing when none does.
        ''' </summary>
        Public Async Function AutoApproveSourceAsync(taskId As String) As Task(Of String)
            If String.IsNullOrEmpty(taskId) Then Return Nothing
            Dim rows = Await _db.QueryAsync("SELECT AutoApproveTools, CorrelationId FROM AgentTasks WHERE Id=@id",
                                            Function(r) (Auto:=Db.Int(r, "AutoApproveTools") <> 0, Correlation:=Db.Str(r, "CorrelationId")), ("@id", CObj(taskId))).ConfigureAwait(False)
            If rows.Count = 0 Then Return Nothing
            If rows(0).Auto Then Return taskId
            If String.IsNullOrEmpty(rows(0).Correlation) Then Return Nothing
            Dim other = Await _db.ScalarAsync("SELECT Id FROM AgentTasks WHERE CorrelationId=@c AND AutoApproveTools=1 LIMIT 1", ("@c", CObj(rows(0).Correlation))).ConfigureAwait(False)
            Return If(other Is Nothing OrElse TypeOf other Is DBNull, Nothing, CStr(other))
        End Function

        ''' <summary>Unfinished tasks whose enforced deadline has passed and was not handled yet.</summary>
        Public Function ListDeadlineDueAsync(nowUtc As DateTime) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE DeadlineEnforced=1 AND DeadlinePassedUtc IS NULL AND DeadlineUtc IS NOT NULL AND DeadlineUtc <= @now " &
                                  "AND Status NOT IN ('Completed','Failed','Cancelled') ORDER BY DeadlineUtc LIMIT 100", AddressOf Map, ("@now", CObj(Db.Ts(nowUtc))))
        End Function

        ''' <summary>Records that a task's deadline passed (once).</summary>
        Public Async Function MarkDeadlinePassedAsync(taskId As String, nowUtc As DateTime) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("UPDATE AgentTasks SET DeadlinePassedUtc=@n WHERE Id=@id AND DeadlinePassedUtc IS NULL",
                                           ("@n", CObj(Db.Ts(nowUtc))), ("@id", taskId)).ConfigureAwait(False)) > 0
        End Function

        ''' <summary>
        ''' Atomically claims the agent's next due task in queue order (Pending/Retrying → Running). Off-peak tasks are
        ''' skipped while <paramref name="offPeakActive"/> is False unless their deadline is within <paramref name="deadlineWindow"/>,
        ''' and any task whose deadline is that near jumps ahead of the ordinary queue order (SLA-aware).
        ''' </summary>
        Public Function ClaimNextAsync(agentId As String, now As DateTime, leaseOwner As String, leaseUntil As DateTime,
                                       Optional offPeakActive As Boolean = True, Optional deadlineWindow As TimeSpan? = Nothing) As Task(Of AgentTask)
            Dim soon = Db.Ts(now.Add(If(deadlineWindow, TimeSpan.FromMinutes(30))))
            Dim sql = "UPDATE AgentTasks SET Status='Running', StartedUtc=COALESCE(StartedUtc, @now), UpdatedUtc=@now, LeaseOwner=@lo, LeaseExpiresUtc=@lu " &
                      "WHERE Id = (SELECT Id FROM AgentTasks WHERE AgentId=@a AND Status IN ('Pending','Retrying') AND (NextRunUtc IS NULL OR NextRunUtc <= @now) " &
                      "AND (OffPeak=0 OR @inwin=1 OR (DeadlineUtc IS NOT NULL AND DeadlineUtc <= @soon)) " &
                      "ORDER BY CASE WHEN DeadlineUtc IS NOT NULL AND DeadlineUtc <= @soon THEN 0 ELSE 1 END ASC, " &
                      "CASE WHEN QueueRank IS NULL THEN 1 ELSE 0 END, QueueRank ASC, Priority DESC, CreatedUtc ASC LIMIT 1) " &
                      $"RETURNING {Columns}"
            Return _db.QuerySingleAsync(sql, AddressOf Map, ("@a", CObj(agentId)), ("@now", Db.Ts(now)), ("@lo", leaseOwner), ("@lu", Db.Ts(leaseUntil)),
                                        ("@inwin", If(offPeakActive, 1, 0)), ("@soon", CObj(soon)))
        End Function

        ''' <summary>Conditional status transition (used by admin cancel and other actors outside the agent loop).</summary>
        Public Async Function TryTransitionAsync(id As String, fromStatuses As AgentTaskStatus(), toStatus As AgentTaskStatus, note As String, Optional errorMessage As String = Nothing) As Task(Of Boolean)
            Dim inList = String.Join(",", fromStatuses.Select(Function(s) "'" & s.ToString() & "'"))
            Dim now = DateTime.UtcNow
            Dim terminal = toStatus = AgentTaskStatus.Completed OrElse toStatus = AgentTaskStatus.Failed OrElse toStatus = AgentTaskStatus.Cancelled
            Dim n = Await _db.ExecuteAsync(
                $"UPDATE AgentTasks SET Status=@to, UpdatedUtc=@now, Error=COALESCE(@err, Error), CompletedUtc=CASE WHEN @term=1 THEN @now ELSE CompletedUtc END " &
                $"WHERE Id=@id AND Status IN ({inList})",
                ("@to", CObj(toStatus.ToString())), ("@now", Db.Ts(now)), ("@err", errorMessage), ("@term", If(terminal, 1, 0)), ("@id", id)).ConfigureAwait(False)
            If n > 0 Then
                Dim t = Await GetAsync(id).ConfigureAwait(False)
                If t IsNot Nothing Then Await AddHistoryAsync(id, t.AgentId, Nothing, toStatus, note).ConfigureAwait(False)
            End If
            Return n > 0
        End Function

        ''' <summary>Keeps the conversation of a reviewed or delivered task (the checkpoint is cleared when the task ends).</summary>
        Public Function SaveTranscriptAsync(taskId As String, agentId As String, checkpointJson As String) As Task(Of Integer)
            Dim cp = TaskCheckpoint.Parse(checkpointJson)
            If cp Is Nothing OrElse cp.Transcript.Count = 0 Then Return Task.FromResult(0)
            Dim compact = cp.Transcript.Select(Function(m) New With {
                .role = m.Role, .content = TextUtil.Truncate(m.Content, If(m.Role = "system", 6000, 12000)), .name = m.Name,
                .toolCalls = m.ToolCalls?.Select(Function(c) New With {.name = c.Name, .arguments = TextUtil.Truncate(c.ArgumentsJson, 2000)}).ToList()}).ToList()
            Return _db.ExecuteAsync("INSERT INTO TaskTranscripts(TaskId, AgentId, TranscriptJson, CreatedUtc) VALUES (@t, @a, @j, @c) " &
                                    "ON CONFLICT(TaskId) DO UPDATE SET TranscriptJson=@j, CreatedUtc=@c",
                                    ("@t", CObj(taskId)), ("@a", agentId), ("@j", JsonUtil.ToJson(compact)), ("@c", Db.Ts(DateTime.UtcNow)))
        End Function

        Public Async Function GetTranscriptAsync(taskId As String) As Task(Of String)
            Dim v = Await _db.ScalarAsync("SELECT TranscriptJson FROM TaskTranscripts WHERE TaskId=@t", ("@t", CObj(taskId))).ConfigureAwait(False)
            Return If(v Is Nothing OrElse TypeOf v Is DBNull, Nothing, CStr(v))
        End Function

        ''' <summary>All tasks of a workflow (oldest first).</summary>
        Public Function ListByCorrelationAsync(correlationId As String, Optional limit As Integer = 500) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE CorrelationId=@c ORDER BY CreatedUtc LIMIT @l", AddressOf Map,
                                  ("@c", CObj(correlationId)), ("@l", Math.Clamp(limit, 1, 5000)))
        End Function

        Public Function AddHistoryAsync(taskId As String, agentId As String, fromStatus As AgentTaskStatus?, toStatus As AgentTaskStatus, note As String) As Task(Of Integer)
            Return _db.ExecuteAsync("INSERT INTO TaskHistory(TaskId, AgentId, FromStatus, ToStatus, Note, CreatedUtc) VALUES (@t, @a, @f, @to, @n, @c)",
                                    ("@t", CObj(taskId)), ("@a", agentId), ("@f", If(fromStatus.HasValue, CObj(fromStatus.Value.ToString()), Nothing)),
                                    ("@to", toStatus.ToString()), ("@n", TextUtil.Truncate(note, 2000)), ("@c", Db.Ts(DateTime.UtcNow)))
        End Function

        Public Function ListAsync(agentId As String, statuses As AgentTaskStatus(), limit As Integer, Optional offset As Integer = 0) As Task(Of List(Of AgentTask))
            Dim where = "WHERE AgentId=@a"
            If statuses IsNot Nothing AndAlso statuses.Length > 0 Then where &= $" AND Status IN ({String.Join(",", statuses.Select(Function(s) "'" & s.ToString() & "'"))})"
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks {where} ORDER BY CreatedUtc DESC LIMIT @l OFFSET @o", AddressOf Map,
                                  ("@a", CObj(agentId)), ("@l", Math.Clamp(limit, 1, 500)), ("@o", Math.Max(0, offset)))
        End Function

        ''' <summary>
        ''' The agent's open work in execution order: running first, then the queue in its explicit order (held tasks keep
        ''' their place there and are skipped until released), then tasks waiting on something.
        ''' </summary>
        Public Function ListQueueAsync(agentId As String, limit As Integer) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE AgentId=@a AND Status IN ('Pending','Retrying','Waiting','Blocked','Running') " &
                                  "ORDER BY CASE Status WHEN 'Running' THEN 0 WHEN 'Waiting' THEN 2 ELSE 1 END, " &
                                  "CASE WHEN QueueRank IS NULL THEN 1 ELSE 0 END, QueueRank, Priority DESC, CreatedUtc ASC LIMIT @l", AddressOf Map,
                                  ("@a", CObj(agentId)), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        ''' <summary>Filtered task search across agents (dashboard).</summary>
        Public Function SearchAsync(agentId As String, statuses As IEnumerable(Of String), text As String, limit As Integer, offset As Integer) As Task(Of List(Of AgentTask))
            Dim where As New List(Of String)
            Dim ps As New List(Of (String, Object))
            If Not String.IsNullOrEmpty(agentId) Then where.Add("AgentId=@a") : ps.Add(("@a", agentId))
            Dim st = If(statuses, Enumerable.Empty(Of String)()).Where(Function(s) [Enum].IsDefined(GetType(AgentTaskStatus), s)).ToList()
            If st.Count > 0 Then where.Add($"Status IN ({String.Join(",", st.Select(Function(s) "'" & s & "'"))})")
            If Not String.IsNullOrWhiteSpace(text) Then
                where.Add("(Id=@id OR CorrelationId=@id OR Name LIKE @q ESCAPE '\' OR Description LIKE @q ESCAPE '\')")
                ps.Add(("@id", text.Trim()))
                ps.Add(("@q", "%" & text.Trim().Replace("\", "\\").Replace("%", "\%").Replace("_", "\_") & "%"))
            End If
            ps.Add(("@l", Math.Clamp(limit, 1, 500)))
            ps.Add(("@o", Math.Max(0, offset)))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") &
                                  " ORDER BY CreatedUtc DESC LIMIT @l OFFSET @o", AddressOf Map, ps.ToArray())
        End Function

        ''' <summary>Open-work counts per agent and status (Pending, Retrying, Waiting, Blocked, Running).</summary>
        Public Async Function OpenCountsAsync() As Task(Of Dictionary(Of String, Dictionary(Of String, Long)))
            Dim rows = Await _db.QueryAsync("SELECT AgentId, Status, COUNT(*) AS C FROM AgentTasks WHERE Status IN ('Pending','Retrying','Waiting','Blocked','Running') GROUP BY AgentId, Status",
                                            Function(r) (Db.Str(r, "AgentId"), Db.Str(r, "Status"), Db.Lng(r, "C"))).ConfigureAwait(False)
            Dim result As New Dictionary(Of String, Dictionary(Of String, Long))(StringComparer.OrdinalIgnoreCase)
            For Each row In rows
                Dim d As Dictionary(Of String, Long) = Nothing
                If Not result.TryGetValue(row.Item1, d) Then
                    d = New Dictionary(Of String, Long)
                    result(row.Item1) = d
                End If
                d(row.Item2) = row.Item3
            Next
            Return result
        End Function

        Public Function ListByStatusAllAsync(status As AgentTaskStatus, limit As Integer) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE Status=@s ORDER BY UpdatedUtc DESC LIMIT @l", AddressOf Map,
                                  ("@s", CObj(status.ToString())), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Public Function ListFailedSinceAsync(sinceUtc As DateTime, limit As Integer) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE Status='Failed' AND CompletedUtc>=@s ORDER BY CompletedUtc DESC LIMIT @l", AddressOf Map,
                                  ("@s", CObj(Db.Ts(sinceUtc))), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Public Function ListWaitingDueAsync(agentId As String, now As DateTime) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE AgentId=@a AND Status='Waiting' AND WaitDeadlineUtc IS NOT NULL AND WaitDeadlineUtc <= @now LIMIT 20",
                                  AddressOf Map, ("@a", CObj(agentId)), ("@now", Db.Ts(now)))
        End Function

        ''' <summary>Finds the agent's waiting task that is waiting on the given key (e.g. "msg:{id}" or "approval:{id}").</summary>
        Public Function FindWaitingForAsync(agentId As String, waitKey As String) As Task(Of AgentTask)
            ' Escape LIKE wildcards to prevent pattern injection
            Dim escaped = waitKey.Replace("\", "\\").Replace("%", "\%").Replace("_", "\_")
            Return _db.QuerySingleAsync($"SELECT {Columns} FROM AgentTasks WHERE AgentId=@a AND Status IN ('Waiting','Pending','Running','Retrying') AND WaitingOn LIKE @k ESCAPE '\' LIMIT 1",
                                        AddressOf Map, ("@a", CObj(agentId)), ("@k", "%""" & escaped & """%"))
        End Function

        ''' <summary>Earliest scheduled run among the agent's pending/retrying tasks (to wake the loop on time).</summary>
        Public Async Function NextDueUtcAsync(agentId As String) As Task(Of DateTime?)
            Dim v = Await _db.ScalarAsync("SELECT MIN(NextRunUtc) FROM AgentTasks WHERE AgentId=@a AND Status IN ('Pending','Retrying') AND NextRunUtc IS NOT NULL",
                                          ("@a", CObj(agentId))).ConfigureAwait(False)
            If v Is Nothing Then Return Nothing
            Return Db.ParseTs(CStr(v))
        End Function

        Public Function CountChildrenAsync(parentTaskId As String) As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentTasks WHERE ParentTaskId=@p", ("@p", CObj(parentTaskId)))
        End Function

        Public Function ListByCorrelationAsync(correlationId As String) As Task(Of List(Of AgentTask))
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks WHERE CorrelationId=@c ORDER BY CreatedUtc LIMIT 500", AddressOf Map, ("@c", CObj(correlationId)))
        End Function

        ''' <summary>Another task of the agent is ready to run now (what a task giving way at the end of its time slice makes room for).</summary>
        Public Async Function HasOtherReadyAsync(agentId As String, exceptTaskId As String, now As DateTime) As Task(Of Boolean)
            Return (Await _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentTasks WHERE AgentId=@a AND Id<>@id AND Status IN ('Pending','Retrying') " &
                                              "AND (NextRunUtc IS NULL OR NextRunUtc <= @now)",
                                              ("@a", CObj(agentId)), ("@id", exceptTaskId), ("@now", Db.Ts(now))).ConfigureAwait(False)) > 0
        End Function

        Public Async Function HasActiveForScheduleAsync(scheduleId As String) As Task(Of Boolean)
            Return (Await _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentTasks WHERE ScheduleId=@s AND Status IN ('Pending','Running','Retrying','Waiting')",
                                              ("@s", CObj(scheduleId))).ConfigureAwait(False)) > 0
        End Function

        ''' <summary>Startup recovery: tasks left Running by a previous process are re-queued (they resume from checkpoint).</summary>
        Public Function RequeueInterruptedAsync() As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE AgentTasks SET Status='Pending', LeaseOwner=NULL, LeaseExpiresUtc=NULL, UpdatedUtc=@now, " &
                                    "Error='Interrupted by host restart; resuming.' WHERE Status='Running'", ("@now", CObj(Db.Ts(DateTime.UtcNow))))
        End Function

        Public Async Function CountsByStatusAsync(agentId As String) As Task(Of Dictionary(Of String, Long))
            Dim rows = Await _db.QueryAsync("SELECT Status, COUNT(*) AS C FROM AgentTasks WHERE AgentId=@a GROUP BY Status",
                                            Function(r) New KeyValuePair(Of String, Long)(Db.Str(r, "Status"), Db.Lng(r, "C")), ("@a", CObj(agentId))).ConfigureAwait(False)
            Return rows.ToDictionary(Function(k) k.Key, Function(k) k.Value)
        End Function

        Public Function ListHistoryAsync(taskId As String) As Task(Of List(Of Dictionary(Of String, Object)))
            Return _db.QueryAsync("SELECT FromStatus, ToStatus, Note, CreatedUtc FROM TaskHistory WHERE TaskId=@t ORDER BY Id",
                                  Function(r) New Dictionary(Of String, Object) From {
                                      {"from", Db.Str(r, "FromStatus")}, {"to", Db.Str(r, "ToStatus")}, {"note", Db.Str(r, "Note")}, {"createdUtc", Db.Str(r, "CreatedUtc")}},
                                  ("@t", CObj(taskId)))
        End Function

        Private Shared ReadOnly ValidStatuses As HashSet(Of String) = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "Pending", "Running", "Completed", "Failed", "Cancelled", "Retrying", "Waiting", "Blocked", "Parked"
        }

        Public Function ListRecentAllAsync(limit As Integer, Optional status As String = Nothing) As Task(Of List(Of AgentTask))
            If Not String.IsNullOrEmpty(status) AndAlso Not ValidStatuses.Contains(status) Then
                Throw New ArgumentException($"Invalid status value: {status}", NameOf(status))
            End If
            Dim where = If(String.IsNullOrEmpty(status), String.Empty, "WHERE Status=@s")
            Return _db.QueryAsync($"SELECT {Columns} FROM AgentTasks {where} ORDER BY CreatedUtc DESC LIMIT @l", AddressOf Map, ("@s", CObj(status)), ("@l", Math.Clamp(limit, 1, 500)))
        End Function
    End Class

End Namespace
