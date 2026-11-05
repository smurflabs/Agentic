Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.RegularExpressions
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>
    ''' Brings agents' work back to the chat it was handed over from. The hand-over is noted in the chat at once; when the task
    ''' ends (after its review, when it has one) its result is posted there as a message from the agent, and a global task posts
    ''' its final answer. Work an agent did can also be opened in a new chat to talk about it.
    ''' </summary>
    Public NotInheritable Class ChatReportService
        Private ReadOnly _chat As ChatService
        Private ReadOnly _agents As Agents.AgentManager
        Private ReadOnly _log As ILog

        Public Sub New(chat As ChatService, agents As Agents.AgentManager, log As ILog)
            _chat = chat
            _agents = agents
            _log = log
        End Sub

        ''' <summary>Text as Markdown shows it literally (task names may hold *, _, [ or `).</summary>
        Friend Shared Function MdText(s As String) As String
            Return Regex.Replace(If(s, ""), "([\\`*_\[\]])", "\$1")
        End Function

        Private Function AgentName(agentId As String) As String
            Dim snap = _agents?.GetSnapshot(agentId)
            Return If(String.IsNullOrWhiteSpace(snap?.Name), agentId, snap.Name)
        End Function

        Private Shared Function TookText(fromUtc As DateTime?, toUtc As DateTime?) As String
            If Not fromUtc.HasValue OrElse Not toUtc.HasValue OrElse toUtc.Value < fromUtc.Value Then Return Nothing
            Dim s = (toUtc.Value - fromUtc.Value).TotalSeconds
            Return If(s < 90, $"{Math.Max(1, CInt(s))} s", If(s < 5400, $"{CInt(s / 60)} min", $"{s / 3600:0.#} h"))
        End Function

        ''' <summary>Notes in the chat that the work was handed over, so the conversation shows what is pending.</summary>
        Public Async Function PostHandOverAsync(username As String, chatId As String, agentId As String, title As String, link As String, isGlobal As Boolean) As Task
            Try
                Dim who = If(isGlobal, "your agents", $"**{MdText(AgentName(agentId))}**")
                Dim text = $"↪ Handed to {who}: **{MdText(title)}**. The result will be posted here when it is done." & vbLf & vbLf & $"[Follow the progress]({link})"
                Await _chat.PostAgentReportAsync(username, chatId, If(isGlobal, "global", agentId), text).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("ChatReports", $"Could not note the hand-over in chat {chatId}: {ex.Message}")
            End Try
        End Function

        ''' <summary>A task ended (the agents' task observer). Work with a review or a delivery is reported once it is final.</summary>
        Public Sub OnTaskFinished(t As AgentTask)
            Dim d = t?.Delivery
            If d Is Nothing OrElse String.IsNullOrEmpty(d.ChatId) OrElse d.IsReview OrElse OutcomeService.FinalizesLater(t) Then Return
            If t.CreatedBy IsNot Nothing AndAlso t.CreatedBy.StartsWith("global:", StringComparison.Ordinal) Then Return
            Dim ignored = Task.Run(Function() ReportTaskAsync(t, t, Nothing))
        End Sub

        ''' <summary>Reviewed or delivered work is final (OutcomeService.WorkFinalized).</summary>
        Public Function OnWorkFinalizedAsync(worker As AgentTask, root As AgentTask, verdict As ReviewOutcome) As Task
            Dim d = If(root?.Delivery, worker?.Delivery)
            If d Is Nothing OrElse String.IsNullOrEmpty(d.ChatId) Then Return Task.CompletedTask
            Return ReportTaskAsync(worker, If(root, worker), verdict)
        End Function

        Private Async Function ReportTaskAsync(worker As AgentTask, root As AgentTask, verdict As ReviewOutcome) As Task
            Try
                Dim d = If(root.Delivery, worker.Delivery)
                Dim result = worker.GetResult()
                Dim reviewed = verdict IsNot Nothing AndAlso verdict.Reviewed
                Dim ok = worker.Status = AgentTaskStatus.Completed AndAlso result IsNot Nothing AndAlso result.Success AndAlso (Not reviewed OrElse verdict.Approved)
                Dim verb = If(worker.Status = AgentTaskStatus.Cancelled, "stopped working on", If(ok, "finished", "could not finish"))
                Dim sb As New StringBuilder()
                sb.Append(If(ok, "✅ ", "⚠️ ")).Append($"**{MdText(AgentName(worker.AgentId))}** {verb} **{MdText(root.Name)}**")
                If reviewed Then
                    sb.Append($" — {MdText(AgentName(verdict.ReviewerAgentId))} {If(verdict.Approved, "approved it", "did not approve it")}")
                    If verdict.Score.HasValue Then sb.Append($" ({verdict.Score}/10)")
                End If
                sb.Append(vbLf).Append(vbLf)
                Dim body = If(result?.Summary, worker.ErrorMessage)
                sb.Append(If(String.IsNullOrWhiteSpace(body), "_No summary was given._", TextUtil.Truncate(body.Trim(), 20000)))
                If reviewed AndAlso Not String.IsNullOrWhiteSpace(verdict.Feedback) Then
                    sb.Append(vbLf).Append(vbLf).Append("> **Reviewer:** ").Append(TextUtil.Truncate(verdict.Feedback.Trim().Replace(vbLf, " "), 1200))
                End If
                Dim facts As New List(Of String) From {$"[Open the task](/agents/{Uri.EscapeDataString(worker.AgentId)}?task={Uri.EscapeDataString(worker.Id)})"}
                Dim took = TookText(root.CreatedUtc, worker.CompletedUtc)
                If took IsNot Nothing Then facts.Add("took " & took)
                If worker.CostMicros > 0 Then facts.Add(TextUtil.FormatMicros(worker.CostMicros) & " cloud spend")
                sb.Append(vbLf).Append(vbLf).Append(String.Join(" · ", facts))
                Await _chat.PostAgentReportAsync(d.ChatUser, d.ChatId, worker.AgentId, sb.ToString()).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("ChatReports", $"Could not post the result of {worker?.Id} to its chat: {ex.Message}", worker?.AgentId, worker?.Id)
            End Try
        End Function

        ' ------------------------------------------------------------------ work opened in a new chat to talk about it

        ''' <summary>The first message of a chat opened to discuss an agent's task: what was asked, what came back.</summary>
        Public Function DiscussTaskText(t As AgentTask) As String
            Dim result = t.GetResult()
            Dim sb As New StringBuilder()
            sb.Append($"**{MdText(AgentName(t.AgentId))}** worked on **{MdText(t.Name)}** ({t.Status.ToString().ToLowerInvariant()}). Ask me about it.").Append(vbLf).Append(vbLf)
            If Not String.IsNullOrWhiteSpace(t.Description) Then sb.Append("**What was asked**").Append(vbLf).Append(TextUtil.Truncate(t.Description.Trim(), 6000)).Append(vbLf).Append(vbLf)
            sb.Append("**Result**").Append(vbLf)
            Dim body = If(result?.Summary, t.ErrorMessage)
            sb.Append(If(String.IsNullOrWhiteSpace(body), "_No result yet._", TextUtil.Truncate(body.Trim(), 24000)))
            sb.Append(vbLf).Append(vbLf).Append($"[Open the task](/agents/{Uri.EscapeDataString(t.AgentId)}?task={Uri.EscapeDataString(t.Id)})")
            Return sb.ToString()
        End Function

        ''' <summary>The first message of a chat opened to discuss a global task.</summary>
        Public Function DiscussGlobalText(g As GlobalTask) As String
            Dim sb As New StringBuilder()
            sb.Append($"The global task **{MdText(g.Name)}** ({g.Status.ToLowerInvariant()}). Ask me about it.").Append(vbLf).Append(vbLf)
            If Not String.IsNullOrWhiteSpace(g.Description) Then sb.Append("**What was asked**").Append(vbLf).Append(TextUtil.Truncate(g.Description.Trim(), 6000)).Append(vbLf).Append(vbLf)
            For Each s In g.Steps.Where(Function(x) x.Status = "Completed" AndAlso Not String.IsNullOrWhiteSpace(x.Result)).Take(12)
                sb.Append($"**Step: {MdText(s.Title)}** ({MdText(AgentName(s.AgentId))})").Append(vbLf).Append(TextUtil.Truncate(s.Result.Trim(), 3000)).Append(vbLf).Append(vbLf)
            Next
            sb.Append("**Final answer**").Append(vbLf)
            sb.Append(If(String.IsNullOrWhiteSpace(g.FinalResult), If(String.IsNullOrWhiteSpace(g.Error), "_Not finished yet._", MdText(g.Error)), TextUtil.Truncate(g.FinalResult.Trim(), 24000)))
            sb.Append(vbLf).Append(vbLf).Append($"[Open the global task](/global-tasks/{Uri.EscapeDataString(g.Id)})")
            Return sb.ToString()
        End Function

        ''' <summary>The first message of a chat opened to discuss a delivered result.</summary>
        Public Shared Function DiscussResultText(r As ResultRecord) As String
            Dim sb As New StringBuilder()
            sb.Append($"The result **{MdText(r.Title)}**{If(r.Success, "", " (failed)")} from {String.Join(", ", r.Participants.Select(AddressOf MdText))}. Ask me about it.").Append(vbLf).Append(vbLf)
            If Not String.IsNullOrWhiteSpace(r.Instructions) Then sb.Append("**What was asked**").Append(vbLf).Append(TextUtil.Truncate(r.Instructions.Trim(), 6000)).Append(vbLf).Append(vbLf)
            sb.Append("**Answer**").Append(vbLf).Append(If(String.IsNullOrWhiteSpace(r.Summary), "_No answer._", TextUtil.Truncate(r.Summary.Trim(), 24000)))
            If Not String.IsNullOrWhiteSpace(r.ReviewFeedback) Then
                sb.Append(vbLf).Append(vbLf).Append($"> **Reviewer** ({r.ReviewVerdict}{If(r.ReviewScore.HasValue, $", {r.ReviewScore}/10", "")}): ").Append(TextUtil.Truncate(r.ReviewFeedback.Trim().Replace(vbLf, " "), 1500))
            End If
            sb.Append(vbLf).Append(vbLf).Append($"[Open the result](/results/{Uri.EscapeDataString(r.Id)})")
            Return sb.ToString()
        End Function

        ''' <summary>A global task ended: its final answer (or why it stopped) goes to the chat it came from.</summary>
        Public Async Function OnGlobalTaskFinishedAsync(g As GlobalTask) As Task
            Dim o = g?.Options
            If o Is Nothing OrElse String.IsNullOrEmpty(o.ChatId) Then Return
            Try
                Dim ok = g.Status = "Completed"
                Dim sb As New StringBuilder()
                sb.Append(If(ok, "✅ ", "⚠️ ")).Append($"The global task **{MdText(g.Name)}** ").Append(If(ok, "is done", If(g.Status = "Cancelled", "was cancelled", "failed")))
                Dim agents = g.Steps.Select(Function(s) s.AgentId).Where(Function(a) Not String.IsNullOrEmpty(a)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                If agents.Count > 0 Then sb.Append(" — ").Append(String.Join(", ", agents.Select(Function(a) MdText(AgentName(a)))))
                sb.Append(vbLf).Append(vbLf)
                If ok AndAlso Not String.IsNullOrWhiteSpace(g.FinalResult) Then
                    sb.Append(TextUtil.Truncate(g.FinalResult.Trim(), 30000))
                Else
                    sb.Append(If(String.IsNullOrWhiteSpace(g.Error), "_No answer was produced._", MdText(TextUtil.Truncate(g.Error, 2000))))
                End If
                Dim facts As New List(Of String) From {$"[Open the global task](/global-tasks/{Uri.EscapeDataString(g.Id)})"}
                Dim took = TookText(g.CreatedUtc, If(g.CompletedUtc, DateTime.UtcNow))
                If took IsNot Nothing Then facts.Add("took " & took)
                sb.Append(vbLf).Append(vbLf).Append(String.Join(" · ", facts))
                Await _chat.PostAgentReportAsync(o.ChatUser, o.ChatId, If(String.IsNullOrEmpty(o.SynthesisAgentId), "global", o.SynthesisAgentId), sb.ToString()).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("ChatReports", $"Could not post global task {g?.Id} to its chat: {ex.Message}")
            End Try
        End Function

    End Class

End Namespace
