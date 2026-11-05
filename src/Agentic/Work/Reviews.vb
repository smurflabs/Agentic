Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports Agentic.Agents
Imports Agentic.AI
Imports Agentic.Artifacts
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' What happens after an agent finishes a task the administrator wants checked or delivered:
    ''' 1. A reviewer agent (usually the manager) gets a review task with the instructions, the result, the files and the
    '''    administrator's expected outcome - which only the reviewer ever sees.
    ''' 2. It approves, or sends the work back with feedback; the worker then gets a revision task (up to MaxRevisions).
    ''' 3. The accepted (or finally rejected) result goes to the Results page when delivery was requested.
    ''' Global tasks use the same review for their final answer (GlobalTaskService), re-planning on a revision.
    ''' </summary>
    Public NotInheritable Class OutcomeService
        Private ReadOnly _db As Database
        Private ReadOnly _agents As AgentManager
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _results As ResultService
        Private ReadOnly _attachments As AttachmentService
        Private ReadOnly _artifacts As ArtifactStore
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog

        Public Sub New(db As Database, agents As AgentManager, tasks As TaskRepository, results As ResultService, attachments As AttachmentService,
                       artifacts As ArtifactStore, events As IAgentEventBus, log As ILog)
            _db = db
            _agents = agents
            _tasks = tasks
            _results = results
            _attachments = attachments
            _artifacts = artifacts
            _events = events
            _log = log
        End Sub

        ''' <summary>Agents that cannot take a review right now.</summary>
        Public Function CanReview(agentId As String) As Boolean
            If String.IsNullOrEmpty(agentId) OrElse Not _agents.Exists(agentId) OrElse _agents.IsSubagent(agentId) Then Return False
            Dim s = _agents.GetSnapshot(agentId)
            Return s IsNot Nothing AndAlso Not {"Dead", "Stopped", "Disabled", "Unhealthy"}.Contains(s.State)
        End Function

        ''' <summary>Called (off the agent loop) whenever a task ends.</summary>
        Public Sub OnTaskFinished(t As AgentTask)
            If t Is Nothing OrElse t.Delivery Is Nothing Then Return
            If t.CreatedBy IsNot Nothing AndAlso t.CreatedBy.StartsWith("global:", StringComparison.Ordinal) Then Return ' the global task engine owns its steps
            If Not t.Delivery.IsReview AndAlso Not t.Delivery.Deliver AndAlso String.IsNullOrEmpty(t.Delivery.ReviewerAgentId) AndAlso Not t.Delivery.HasBounty Then Return
            If Not String.IsNullOrEmpty(t.Delivery.ReviewOfGlobalTaskId) Then Return ' read by the global task engine
            Dim ignored = Task.Run(Async Function()
                                       Try
                                           If t.Delivery.IsReview Then
                                               Await HandleReviewFinishedAsync(t).ConfigureAwait(False)
                                           Else
                                               Await HandleWorkFinishedAsync(t).ConfigureAwait(False)
                                           End If
                                       Catch ex As Exception
                                           _log.Warn("Outcomes", $"Post-task handling of {t.Id} failed: {ex.Message}", t.AgentId, t.Id)
                                       End Try
                                   End Function)
        End Sub

        Private Async Function RootOfAsync(t As AgentTask) As Task(Of AgentTask)
            If String.IsNullOrEmpty(t.Delivery?.RootTaskId) OrElse t.Delivery.RootTaskId = t.Id Then Return t
            Return If(Await _tasks.GetAsync(t.Delivery.RootTaskId).ConfigureAwait(False), t)
        End Function

        Private Async Function HandleWorkFinishedAsync(t As AgentTask) As Task
            Dim root = Await RootOfAsync(t).ConfigureAwait(False)
            Dim d = t.Delivery
            Dim result = t.GetResult()
            Dim succeeded = t.Status = AgentTaskStatus.Completed AndAlso result IsNot Nothing AndAlso result.Success
            Dim reviewer = d.ReviewerAgentId
            If Not succeeded OrElse String.IsNullOrEmpty(reviewer) Then
                Await FinalizeAsync(t, root, ReviewOutcome.NotReviewed(If(String.IsNullOrEmpty(reviewer), Nothing, "Not reviewed: the work did not finish successfully."))).ConfigureAwait(False)
                Return
            End If
            If String.Equals(reviewer, t.AgentId, StringComparison.OrdinalIgnoreCase) Then
                Await FinalizeAsync(t, root, ReviewOutcome.NotReviewed($"Not reviewed: {reviewer} did the work itself.")).ConfigureAwait(False)
                Return
            End If
            If Not CanReview(reviewer) Then
                Await FinalizeAsync(t, root, ReviewOutcome.NotReviewed($"Not reviewed: {reviewer} is not available.")).ConfigureAwait(False)
                Return
            End If
            Dim files = Await FilesForReviewAsync(t.CorrelationId, reviewer).ConfigureAwait(False)
            Await _attachments.GrantAsync(d.Attachments, t.AgentId, reviewer).ConfigureAwait(False)
            Dim data = If(result.Data.HasValue AndAlso result.Data.Value.ValueKind <> JsonValueKind.Undefined AndAlso result.Data.Value.ValueKind <> JsonValueKind.Null,
                          result.Data.Value.GetRawText(), Nothing)
            Dim instructions = ReviewInstructions(t.AgentId, root.Name, root.Description, result.Summary, data, files, d.Attachments, root.ReviewCriteria,
                                                  t.CorrelationId, d.Revision, Math.Max(0, d.MaxRevisions))
            Dim r = Await _agents.SubmitTaskAsync(reviewer, New NewTaskRequest With {
                .Name = TextUtil.Truncate("Review: " & root.Name, 200), .Description = instructions, .Priority = root.Priority, .Purpose = AiPurpose.ComplexAnalysis,
                .CorrelationId = t.CorrelationId, .CreatedBy = "review:" & t.Id, .IdempotencyKey = "review:" & t.Id,
                .Delivery = New TaskDelivery With {.ReviewOfTaskId = t.Id, .RootTaskId = root.Id, .Attachments = d.Attachments}}).ConfigureAwait(False)
            If Not r.Success Then
                Await FinalizeAsync(t, root, ReviewOutcome.NotReviewed($"Not reviewed: {r.Message}")).ConfigureAwait(False)
                Return
            End If
            _events.Emit(AgentEventType.ReviewRequested, $"{reviewer} is reviewing '{root.Name}' by {t.AgentId}" & If(d.Revision > 0, $" (revision {d.Revision})", ""),
                         reviewer, r.TaskId, correlationId:=t.CorrelationId)
        End Function

        Private Async Function HandleReviewFinishedAsync(review As AgentTask) As Task
            Dim worker = Await _tasks.GetAsync(review.Delivery.ReviewOfTaskId).ConfigureAwait(False)
            If worker Is Nothing Then Return
            Dim root = Await RootOfAsync(worker).ConfigureAwait(False)
            Dim verdict = ParseVerdict(review)
            _events.Emit(AgentEventType.ReviewCompleted, $"{review.AgentId} {If(Not verdict.Reviewed, "could not review", If(verdict.Approved, "approved", "sent back"))} '{root.Name}'" &
                         If(verdict.Score.HasValue, $" (score {verdict.Score}/10)", ""), review.AgentId, review.Id, correlationId:=review.CorrelationId,
                         level:=If(verdict.Approved, EventLevel.Info, EventLevel.Warning),
                         data:=New Dictionary(Of String, Object) From {{"verdict", verdict.Verdict}, {"score", verdict.Score}, {"workerTaskId", worker.Id}})
            Dim d = worker.Delivery
            If verdict.Reviewed AndAlso Not verdict.Approved AndAlso d.Revision < d.MaxRevisions AndAlso _agents.Exists(worker.AgentId) Then
                Dim nextRevision = d.Revision + 1
                Dim prev = worker.GetResult()
                Dim sb As New StringBuilder()
                sb.Append(root.Description).Append(vbLf).Append(vbLf)
                sb.Append($"## Revision {nextRevision} of {d.MaxRevisions}").Append(vbLf)
                sb.Append($"Your previous result was reviewed by {review.AgentId} and sent back. Fix every point below and keep what was good; ").
                   Append("deliver a complete result again (not only the changes).").Append(vbLf).Append(vbLf)
                sb.Append("Reviewer feedback:").Append(vbLf).Append(Untrusted.Wrap("agent:" & review.AgentId, TextUtil.Truncate(verdict.Feedback, 6000))).Append(vbLf).Append(vbLf)
                sb.Append("Your previous result:").Append(vbLf).Append(TextUtil.Truncate(prev?.Summary, 6000))
                Dim nd = d.CloneSettings()
                nd.RootTaskId = root.Id
                nd.Revision = nextRevision
                Dim r = Await _agents.SubmitTaskAsync(worker.AgentId, New NewTaskRequest With {
                    .Name = TextUtil.Truncate($"{root.Name} (revision {nextRevision})", 200), .Description = sb.ToString(), .Priority = root.Priority,
                    .Purpose = root.Purpose, .StartTier = root.StartTier, .ModelKey = root.ModelKey, .CorrelationId = worker.CorrelationId,
                    .CreatedBy = root.CreatedBy, .IdempotencyKey = $"revision:{root.Id}:{nextRevision}", .Delivery = nd, .ReviewCriteria = root.ReviewCriteria}).ConfigureAwait(False)
                If r.Success Then Return
                verdict.Note = "Could not start the revision: " & r.Message
            End If
            If verdict.Reviewed AndAlso Not verdict.Approved AndAlso d.Revision >= d.MaxRevisions AndAlso d.MaxRevisions > 0 Then
                verdict.Note = $"Rejected after {d.Revision} revision(s); this is the last attempt."
            End If
            Await FinalizeAsync(worker, root, verdict).ConfigureAwait(False)
        End Function

        Private Async Function FinalizeAsync(worker As AgentTask, root As AgentTask, verdict As ReviewOutcome) As Task
            If verdict.Reviewed Then
                ' The worker learns from the verdict either way.
                Try
                    Await _agents.RememberAsync(worker.AgentId, $"{verdict.ReviewerAgentId} {If(verdict.Approved, "approved", "rejected")} your work on '{root.Name}'" &
                        If(verdict.Score.HasValue, $" (score {verdict.Score}/10)", "") & ": " & TextUtil.Truncate(verdict.Feedback, 1200), If(verdict.Approved, 3, 5), root.Id).ConfigureAwait(False)
                Catch ex As Exception
                End Try
            End If
            ' Accepted work earns its bounty (before delivery, so the Results page shows the payment).
            If worker.Delivery.HasBounty AndAlso PayBounty IsNot Nothing Then
                Try
                    Await PayBounty(worker, root, verdict).ConfigureAwait(False)
                Catch ex As Exception
                    _log.Warn("Outcomes", $"Bounty for {worker.Id} not paid: {ex.Message}", worker.AgentId, worker.Id)
                End Try
            End If
            If worker.Delivery.Deliver Then
                Await _results.DeliverTaskAsync(worker, root, verdict, worker.Delivery.Revision).ConfigureAwait(False)
            End If
            If WorkFinalized IsNot Nothing Then
                Try
                    Await WorkFinalized(worker, root, verdict).ConfigureAwait(False)
                Catch ex As Exception
                    _log.Warn("Outcomes", $"After-finalize handling of {worker.Id} failed: {ex.Message}", worker.AgentId, worker.Id)
                End Try
            End If
        End Function

        ''' <summary>
        ''' Called once reviewed or delivered work is final (approved, finally rejected, or not reviewed): the worker's last task,
        ''' the first task of its revision chain and the verdict. Used to post the outcome back to the chat the work came from.
        ''' </summary>
        Public Property WorkFinalized As Func(Of AgentTask, AgentTask, ReviewOutcome, Task)

        ''' <summary>Pays the bounty of finished work when the verdict accepts it (worker's last task, first task of the chain, verdict).</summary>
        Public Property PayBounty As Func(Of AgentTask, AgentTask, ReviewOutcome, Task)

        ''' <summary>Whether this finished task's outcome is reported later, by FinalizeAsync (it has a review, a delivery or a bounty).</summary>
        Public Shared Function FinalizesLater(t As AgentTask) As Boolean
            If t?.Delivery Is Nothing OrElse t.Delivery.IsReview Then Return False
            If t.CreatedBy IsNot Nothing AndAlso t.CreatedBy.StartsWith("global:", StringComparison.Ordinal) Then Return False
            Return t.Delivery.Deliver OrElse Not String.IsNullOrEmpty(t.Delivery.ReviewerAgentId) OrElse t.Delivery.HasBounty
        End Function

        ''' <summary>Artifacts the workflow produced, shared with the reviewer so it can open them.</summary>
        Public Async Function FilesForReviewAsync(correlationId As String, reviewer As String) As Task(Of List(Of String))
            Dim arts = Await _db.QueryAsync("SELECT Id, Name, OwnerAgentId, MimeType, SizeBytes FROM Artifacts WHERE SourceTaskId IN (SELECT Id FROM AgentTasks WHERE CorrelationId=@c) ORDER BY CreatedUtc LIMIT 60",
                                            Function(r) (Id:=Db.Str(r, "Id"), Name:=Db.Str(r, "Name"), Owner:=Db.Str(r, "OwnerAgentId"), Mime:=Db.Str(r, "MimeType"), Size:=Db.Lng(r, "SizeBytes")),
                                            ("@c", CObj(correlationId))).ConfigureAwait(False)
            Dim lines As New List(Of String)
            For Each a In arts
                If Not String.Equals(a.Owner, reviewer, StringComparison.OrdinalIgnoreCase) Then
                    Try
                        Await _artifacts.GrantAsync(a.Owner, a.Id, reviewer, Nothing).ConfigureAwait(False)
                    Catch ex As ArtifactAccessException
                        Continue For
                    End Try
                End If
                lines.Add($"- artifact {a.Id}: {a.Name} ({a.Mime}, {FileExtractor.SizeText(a.Size)}, by {a.Owner})")
            Next
            Return lines
        End Function

        ''' <summary>The reviewer's task. The expected outcome appears here and nowhere the worker can see.</summary>
        Public Shared Function ReviewInstructions(workerAgentId As String, title As String, instructions As String, resultSummary As String, dataJson As String,
                                                  files As List(Of String), attachments As List(Of TaskAttachment), criteria As String, correlationId As String,
                                                  revision As Integer, maxRevisions As Integer) As String
            Dim sb As New StringBuilder()
            sb.Append($"Review the work {workerAgentId} did for the administrator on ""{title}"" and decide whether it is good enough to accept.").Append(vbLf).Append(vbLf)
            sb.Append("## The task that was given").Append(vbLf).Append(TextUtil.Truncate(If(instructions, title), 8000)).Append(vbLf).Append(vbLf)
            sb.Append($"## The result to review{If(revision > 0, $" (revision {revision} of {maxRevisions})", "")}").Append(vbLf)
            sb.Append(Untrusted.Wrap("agent:" & workerAgentId, TextUtil.Truncate(resultSummary, 16000))).Append(vbLf)
            If Not String.IsNullOrWhiteSpace(dataJson) Then sb.Append("Structured data:").Append(vbLf).Append(Untrusted.Wrap("agent:" & workerAgentId, TextUtil.Truncate(dataJson, 8000))).Append(vbLf)
            sb.Append(vbLf).Append("## Files").Append(vbLf)
            If files IsNot Nothing AndAlso files.Count > 0 Then
                sb.Append("Produced during the work (shared with you; open them with read_artifact or analyze_image):").Append(vbLf)
                For Each f In files
                    sb.Append(f).Append(vbLf)
                Next
            Else
                sb.Append("No artifacts were produced.").Append(vbLf)
            End If
            sb.Append($"Deliverable files may also be in the shared storage folder {ResultService.DeliverablesFolder(correlationId)}/ (storage tools).").Append(vbLf)
            If attachments IsNot Nothing AndAlso attachments.Count > 0 Then
                sb.Append("The administrator's input files are listed under ""Attached files"".").Append(vbLf)
            End If
            sb.Append(vbLf).Append("## Expected outcome — CONFIDENTIAL").Append(vbLf)
            sb.Append("Only you can see this. The administrator wrote it so you can judge the result; the agent that did the work never saw it. ").
               Append("Do not reveal or quote it to any agent.").Append(vbLf)
            sb.Append(If(String.IsNullOrWhiteSpace(criteria), "(No expected outcome was given: judge the result against the task itself — correct, complete, well done and actually usable.)",
                         criteria.Trim())).Append(vbLf).Append(vbLf)
            sb.Append("## How to review").Append(vbLf)
            sb.Append("- Check the actual work: open the files, verify facts, figures, links and code with your tools where you can. Do not accept claims you have not checked.").Append(vbLf)
            sb.Append("- Approve only if the result meets the task and the expected outcome. Small polish issues are not a reason to reject; missing or wrong substance is.").Append(vbLf)
            If revision < maxRevisions Then
                sb.Append("- If it falls short, send it back: give specific, actionable feedback (what is wrong or missing and what to change). Describe the gap in your own words; never quote the confidential expected outcome.").Append(vbLf)
            Else
                sb.Append("- No revisions are left, so your verdict is final. If it falls short, explain the shortfalls clearly for the administrator.").Append(vbLf)
            End If
            sb.Append("- Finish by calling complete_task with success=true, your assessment as the summary, and data = {""verdict"": ""approve"" or ""revise"", ""score"": 1-10, ""feedback"": ""...""}.")
            Return sb.ToString()
        End Function

        ''' <summary>Reads a review task's verdict (data.verdict, otherwise the wording of the summary).</summary>
        Public Shared Function ParseVerdict(review As AgentTask) As ReviewOutcome
            If review Is Nothing OrElse review.Status <> AgentTaskStatus.Completed Then
                Return ReviewOutcome.NotReviewed($"The review did not finish ({If(review?.Status.ToString(), "missing")}).")
            End If
            Dim r = review.GetResult()
            If r Is Nothing Then Return ReviewOutcome.NotReviewed("The review returned no result.")
            Dim o As New ReviewOutcome With {.Reviewed = True, .ReviewerAgentId = review.AgentId, .Feedback = r.Summary}
            Dim verdictText As String = Nothing
            If r.Data.HasValue AndAlso r.Data.Value.ValueKind = JsonValueKind.Object Then
                Dim d = r.Data.Value
                verdictText = JsonUtil.GetString(d, "verdict")
                Dim fb = JsonUtil.GetString(d, "feedback")
                If Not String.IsNullOrWhiteSpace(fb) Then o.Feedback = fb & If(String.IsNullOrWhiteSpace(r.Summary) OrElse fb.Contains(r.Summary), "", vbLf & vbLf & r.Summary)
                Dim scoreEl As JsonElement
                If d.TryGetProperty("score", scoreEl) Then
                    Dim sc As Double
                    If scoreEl.ValueKind = JsonValueKind.Number AndAlso scoreEl.TryGetDouble(sc) Then o.Score = CInt(Math.Clamp(Math.Round(sc), 1, 10))
                    If scoreEl.ValueKind = JsonValueKind.String AndAlso Double.TryParse(scoreEl.GetString(), Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture, sc) Then o.Score = CInt(Math.Clamp(Math.Round(sc), 1, 10))
                End If
            End If
            Dim v = If(verdictText, "").Trim().ToLowerInvariant()
            If Regex.IsMatch(v, "^(approve|approved|accept|accepted|pass|passed|ok|yes|good)") Then
                o.Approved = True
            ElseIf Regex.IsMatch(v, "^(revise|revision|reject|rejected|fail|failed|no|redo|insufficient)") Then
                o.Approved = False
            Else
                o.Approved = r.Success AndAlso Not Regex.IsMatch(If(r.Summary, ""), "\b(revise|reject(ed)?|not approved|does not meet|doesn't meet|insufficient|send(ing)? it back)\b", RegexOptions.IgnoreCase)
            End If
            Return o
        End Function
    End Class

End Namespace
