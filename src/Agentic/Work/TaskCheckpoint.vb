Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.AI
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A tool call whose result arrives later (agent reply, human answer).</summary>
    Public Class PendingToolCall
        Public Property ToolCallId As String
        Public Property ToolName As String
        Public Property WaitKey As String
        ''' <summary>Further keys the same call waits on (e.g. wait_for_subagents on several subagents); all must resolve.</summary>
        Public Property ExtraWaitKeys As New List(Of String)

        Public Function AllWaitKeys() As List(Of String)
            Dim keys As New List(Of String) From {WaitKey}
            If ExtraWaitKeys IsNot Nothing Then keys.AddRange(ExtraWaitKeys)
            Return keys
        End Function
    End Class

    ''' <summary>The loop guard's record of one tool call (same tool, same arguments).</summary>
    Public Class SeenCall
        Public Property Count As Integer
        Public Property LastHash As String
        Public Property SameResults As Integer
    End Class

    ''' <summary>One step of the plan an agent keeps for its current task (update_plan).</summary>
    Public Class PlanStep
        Public Property [Step] As String
        ''' <summary>pending | in_progress | done | skipped</summary>
        Public Property Status As String = "pending"

        ''' <summary>The plan as the agent and people read it ("[x] step", "[>] step", "[ ] step").</summary>
        Public Shared Function Render(plan As IEnumerable(Of PlanStep)) As String
            Dim lines = If(plan, Enumerable.Empty(Of PlanStep)()).Select(Function(p, i)
                                                                            Dim mark = If(p.Status = "done", "[x]", If(p.Status = "in_progress", "[>]", If(p.Status = "skipped", "[-]", "[ ]")))
                                                                            Return $"{mark} {i + 1}. {p.Step}"
                                                                        End Function)
            Return String.Join(vbLf, lines)
        End Function
    End Class

    ''' <summary>
    ''' Persisted reasoning state for a task. Allows a task to park (Waiting) and resume later, and to resume after
    ''' a host restart instead of starting over.
    ''' </summary>
    Public Class TaskCheckpoint
        Public Property Version As Integer = 1
        Public Property Transcript As New List(Of ChatMessage)
        Public Property Turn As Integer
        Public Property ToolCallsUsed As Integer
        Public Property LadderLevel As Integer
        Public Property PendingToolCalls As New List(Of PendingToolCall)
        ''' <summary>Tool calls that were executing when the checkpoint was written (outcome unknown after a crash).</summary>
        Public Property InFlightToolCalls As New List(Of ToolCallRequest)
        ''' <summary>Wait key → JSON resolution (reply, timeout, human answer).</summary>
        Public Property Resolutions As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        Public Property EscalationRequested As Boolean
        Public Property EscalationReason As String
        Public Property TruncationRetries As Integer
        Public Property TurnLimitEscalations As Integer
        ''' <summary>Self-critique verification passes already demanded of a successful complete_task.</summary>
        Public Property Verifications As Integer
        ''' <summary>Model the task continues on after switch_model (or an admin pin); Nothing = routing ladder.</summary>
        Public Property ModelOverride As String
        ''' <summary>Times a reply that announced a next step without calling a tool was sent back ("call the tool now").</summary>
        Public Property DanglingNudges As Integer
        ''' <summary>Unused since tool calls are counted per stretch (kept so older checkpoints still load).</summary>
        Public Property ToolBudgetWarned As Boolean
        ''' <summary>Tool calls that failed one after another (any arguments); reset by a call that works.</summary>
        Public Property FailStreak As Integer
        ''' <summary>Only complete_task is offered for the rest of the task (after too many failures in a row).</summary>
        Public Property ToolsOff As Boolean
        ''' <summary>The agent's own plan for this task (update_plan), kept across context compaction and restarts.</summary>
        Public Property Plan As List(Of PlanStep)
        ''' <summary>Turns in all (Turn counts only the current stretch, between progress checks).</summary>
        Public Property TotalTurns As Integer
        ''' <summary>Tool calls in the current stretch.</summary>
        Public Property StretchToolCalls As Integer
        ''' <summary>Useful results in the current stretch: calls that worked and gave something new (not a repeat).</summary>
        Public Property StretchProgress As Integer
        ''' <summary>Useful results in all; a long task that gives way after its time slice only counts as stuck when this did not grow.</summary>
        Public Property Progress As Integer
        ''' <summary>Progress checks passed (the task went on because it was still making progress).</summary>
        Public Property Stretches As Integer
        ''' <summary>Calls in the current stretch that the loop guard refused (the same call and result again).</summary>
        Public Property StretchRefusals As Integer
        ''' <summary>New results in the current stretch from calls that do more than look (a write, a command, a plan step done).</summary>
        Public Property StretchChanges As Integer
        ''' <summary>Progress checks in a row at which the agent had only looked (ReasoningTaskHandler.MaxLookingStretches).</summary>
        Public Property LookingStretches As Integer
        ''' <summary>The most plan steps marked done so far in this task.</summary>
        Public Property PlanDoneHigh As Integer
        ''' <summary>Since when the task has been waiting for the AI (down, busy, quota or budget used up); Nothing once a call works.</summary>
        Public Property WaitingForAiSinceUtc As DateTime?
        ''' <summary>The deadline the agent was last warned about (it is warned once per deadline, when little time is left).</summary>
        Public Property DeadlineWarnedFor As DateTime?
        ''' <summary>
        ''' MCP tools are loaded on demand for this task (ToolLoading): only the host tools and LoadedTools are offered to the
        ''' model, and its system prompt names the rest. False for tasks started before it existed (they keep every tool).
        ''' </summary>
        Public Property ToolLoading As Boolean
        ''' <summary>The MCP tools this task has loaded (its core set, then what load_tools added).</summary>
        Public Property LoadedTools As New List(Of String)
        ''' <summary>
        ''' The loop guard's memory (call key → how often, last result, same results in a row) and the results seen so far
        ''' (short fingerprints, for the progress check). Kept across time slices: it used to start empty after every 23-minute
        ''' slice, so an agent could repeat the same reads slice after slice and each repeat counted as progress.
        ''' </summary>
        Public Property SeenCalls As New Dictionary(Of String, SeenCall)(StringComparer.Ordinal)
        Public Property SeenResults As New List(Of String)

        Public Function ToJson() As String
            Return JsonUtil.ToJson(Me)
        End Function

        Public Shared Function Parse(json As String) As TaskCheckpoint
            If String.IsNullOrEmpty(json) Then Return Nothing
            Try
                Return JsonUtil.FromJson(Of TaskCheckpoint)(json)
            Catch
                Return Nothing
            End Try
        End Function
    End Class

    ''' <summary>
    ''' Delivers the outcome of something a task was waiting for (agent reply, expiry, human answer) into the
    ''' task's checkpoint, and makes the task runnable again once nothing else is outstanding.
    ''' Uses a single IMMEDIATE transaction per resolution so concurrent resolvers cannot lose updates.
    ''' </summary>
    Public NotInheritable Class WaitResolver
        Private ReadOnly _db As Database
        Private ReadOnly _tasks As TaskRepository

        Public Sub New(db As Database, tasks As TaskRepository)
            _db = db
            _tasks = tasks
        End Sub

        ''' <summary>Returns the task id that was updated, or Nothing if no task was waiting on the key.</summary>
        Public Async Function ResolveAsync(agentId As String, waitKey As String, resolutionJson As String, Optional note As String = Nothing) As Task(Of String)
            Dim waiting = Await _tasks.FindWaitingForAsync(agentId, waitKey).ConfigureAwait(False)
            If waiting Is Nothing Then Return Nothing
            Dim becameRunnable = False
            Dim updated = Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    Dim rows = Await Database.TxQueryAsync(conn, tx, "SELECT Status, Checkpoint, WaitingOn FROM AgentTasks WHERE Id=@id",
                                                           Function(r) (Db.Str(r, "Status"), Db.Str(r, "Checkpoint"), Db.Str(r, "WaitingOn")), ("@id", CObj(waiting.Id))).ConfigureAwait(False)
                    If rows.Count = 0 Then Return False
                    Dim status = rows(0).Item1
                    Dim cp = If(TaskCheckpoint.Parse(rows(0).Item2), New TaskCheckpoint())
                    Dim waitingOn = If(JsonUtil.FromJson(Of List(Of String))(rows(0).Item3), New List(Of String))
                    If Not waitingOn.Contains(waitKey, StringComparer.OrdinalIgnoreCase) Then Return False
                    cp.Resolutions(waitKey) = resolutionJson
                    waitingOn.RemoveAll(Function(k) String.Equals(k, waitKey, StringComparison.OrdinalIgnoreCase))
                    Dim newStatus = status
                    If waitingOn.Count = 0 AndAlso status = AgentTaskStatus.Waiting.ToString() Then
                        newStatus = AgentTaskStatus.Pending.ToString()
                        becameRunnable = True
                    End If
                    Await Database.TxExecAsync(conn, tx,
                        "UPDATE AgentTasks SET Checkpoint=@cp, WaitingOn=@w, Status=@s, NextRunUtc=NULL, WaitDeadlineUtc=CASE WHEN @s='Pending' THEN NULL ELSE WaitDeadlineUtc END, UpdatedUtc=@now WHERE Id=@id",
                        ("@cp", CObj(cp.ToJson())), ("@w", JsonUtil.ToJson(waitingOn)), ("@s", newStatus), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", waiting.Id)).ConfigureAwait(False)
                    Return True
                End Function).ConfigureAwait(False)
            If Not updated Then Return Nothing
            If becameRunnable Then Await _tasks.AddHistoryAsync(waiting.Id, agentId, AgentTaskStatus.Waiting, AgentTaskStatus.Pending, If(note, "Resumed: " & waitKey)).ConfigureAwait(False)
            Return waiting.Id
        End Function
    End Class

End Namespace
