Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.Learning
Imports Agentic.Utilities

Namespace Http

    ' Admin API: organizational learning (case studies of agents that went broke or became very profitable, and the
    ' shared lesson library all agents learn from).
    Partial Friend NotInheritable Class AdminApi

        Friend Sub RegisterLearning(r As Router)
            r.Map("GET", "/api/learning/summary", AuthLevel.Read, AddressOf LearningSummary)
            r.Map("GET", "/api/learning/studies", AuthLevel.Read, AddressOf ListStudies)
            r.Map("POST", "/api/learning/studies", AuthLevel.Admin, AddressOf StartStudy)
            r.Map("GET", "/api/learning/studies/{studyId}", AuthLevel.Read, AddressOf GetStudy)
            r.Map("POST", "/api/learning/studies/{studyId}/rerun", AuthLevel.Admin, AddressOf RerunStudy)
            r.Map("GET", "/api/learning/lessons", AuthLevel.Read, AddressOf ListLessons)
            r.Map("PATCH", "/api/learning/lessons/{lessonId}", AuthLevel.Admin, AddressOf EditLesson)
        End Sub

        Private Async Function LearningSummary(rc As RequestContext) As Task
            Dim s = Await _h.Lessons.SummaryAsync().ConfigureAwait(False)
            Dim o = _h.Options.Learning
            s("settings") = New With {o.Enabled, o.StudyFailures, o.StudySuccesses, o.BrokeThreshold, o.MinRoi, o.MinProfit, o.LargeProfit,
                                      o.RequireApproval, o.ModelKeys, o.PromptLessons, o.FailureCooldownHours, o.SuccessCooldownHours, o.WindowDays}
            Await rc.OkAsync(s).ConfigureAwait(False)
        End Function

        Private Async Function ListStudies(rc As RequestContext) As Task
            Dim list = Await _h.Lessons.ListStudiesAsync(rc.QueryString("agent"), rc.QueryString("outcome"), rc.QueryString("status"), rc.QueryInt("limit", 100, 1, 500)).ConfigureAwait(False)
            Await rc.OkAsync(list.Where(Function(s) rc.User.CanAccessAgent(s.AgentId)).ToList()).ConfigureAwait(False)
        End Function

        Private Async Function GetStudy(rc As RequestContext) As Task
            Dim s = Await _h.Lessons.GetStudyAsync(rc.Route("studyId")).ConfigureAwait(False)
            If s Is Nothing OrElse Not rc.User.CanAccessAgent(s.AgentId) Then Throw ApiException.NotFound("STUDY_NOT_FOUND", "Case study does not exist.")
            Dim lessons = Await _h.Lessons.ListLessonsAsync(studyId:=s.Id).ConfigureAwait(False)
            Dim confirmed = Await _h.Lessons.ListConfirmedByStudyAsync(s.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.study = s, .report = JsonUtil.ParseElementOrNull(s.ReportJson), .evidence = JsonUtil.ParseElementOrNull(s.EvidenceJson),
                                       .lessons = lessons, .confirmedLessons = confirmed}).ConfigureAwait(False)
        End Function

        ''' <summary>Body: {"agentId": "...", "outcome": "Failure|Success|Review", "note": "..."} - studies an agent now (no cooldown).</summary>
        Private Async Function StartStudy(rc As RequestContext) As Task
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            If Not _h.Options.Learning.Enabled Then Throw New ApiException(409, "LEARNING_DISABLED", "Learning is disabled in the configuration.")
            Dim agentId = JsonUtil.GetString(b, "agentId")
            If Not _h.Agents.Exists(agentId) OrElse _h.Agents.IsSubagent(agentId) Then Throw ApiException.NotFound("AGENT_NOT_FOUND", "Agent does not exist.")
            Dim s As CaseStudy
            Try
                s = Await _h.Learning.RequestStudyAsync(agentId, JsonUtil.GetString(b, "outcome", "Review"), "Manual", $"Requested by {rc.User.Name}.", rc.User.Name,
                                                        JsonUtil.GetString(b, "note"), force:=True).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "start-case-study", "agent", agentId, New With {.studyId = s.Id, .outcome = s.Outcome}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, s).ConfigureAwait(False)
        End Function

        Private Async Function RerunStudy(rc As RequestContext) As Task
            Dim id = rc.Route("studyId")
            If Not Await _h.Learning.RerunAsync(id, rc.User.Name).ConfigureAwait(False) Then Throw New ApiException(409, "STUDY_NOT_RERUNNABLE", "Study not found or still in progress.")
            Await AuditAsync(rc, "rerun-case-study", "study", id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.ok = True}).ConfigureAwait(False)
        End Function

        Private Async Function ListLessons(rc As RequestContext) As Task
            Dim q = rc.QueryString("q")
            Dim list = If(String.IsNullOrWhiteSpace(q),
                          Await _h.Lessons.ListLessonsAsync(rc.QueryString("status"), rc.QueryString("outcome"), rc.QueryString("agent"), rc.QueryString("study"), rc.QueryInt("limit", 200, 1, 1000)).ConfigureAwait(False),
                          Await _h.Lessons.SearchAsync(q, rc.QueryInt("limit", 100, 1, 200), includeInactive:=True).ConfigureAwait(False))
            Await rc.OkAsync(list).ConfigureAwait(False)
        End Function

        ''' <summary>Body: any of {"title", "body", "tags", "status": "Active|Retracted|PendingReview", "pinned": true}.</summary>
        Private Async Function EditLesson(rc As RequestContext) As Task
            Dim id = rc.Route("lessonId")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim status = JsonUtil.GetString(b, "status")
            If status IsNot Nothing AndAlso Not {"Active", "Retracted", "PendingReview"}.Contains(status) Then Throw ApiException.BadRequest("status must be Active, Retracted or PendingReview.")
            Dim pinnedEl As JsonElement
            Dim pinned As Boolean? = Nothing
            If b.TryGetProperty("pinned", pinnedEl) AndAlso (pinnedEl.ValueKind = JsonValueKind.True OrElse pinnedEl.ValueKind = JsonValueKind.False) Then pinned = pinnedEl.GetBoolean()
            Dim l As Lesson
            Try
                l = Await _h.Lessons.UpdateLessonAsync(id, JsonUtil.GetString(b, "title"), JsonUtil.GetString(b, "body"), JsonUtil.GetString(b, "tags"), status, pinned, rc.User.Name).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            If l Is Nothing Then Throw ApiException.NotFound("LESSON_NOT_FOUND", "Lesson does not exist.")
            Await AuditAsync(rc, "edit-lesson", "lesson", id, JsonUtil.ParseElementOrNull(b.GetRawText())).ConfigureAwait(False)
            Await rc.OkAsync(l).ConfigureAwait(False)
        End Function
    End Class

End Namespace
