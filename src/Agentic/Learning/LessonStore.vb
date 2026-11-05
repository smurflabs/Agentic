Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Memory
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Learning

    Public NotInheritable Class CaseStudy
        Public Property Id As String
        Public Property AgentId As String
        ''' <summary>Failure | Success | Review</summary>
        Public Property Outcome As String
        ''' <summary>Broke | LowBalance | Profit | RoiMilestone | Manual</summary>
        Public Property TriggerKind As String
        Public Property TriggerDetail As String
        ''' <summary>Pending | Analyzing | Published | AwaitingReview | Failed</summary>
        Public Property Status As String
        Public Property WindowStartUtc As DateTime
        Public Property WindowEndUtc As DateTime
        Public Property EvidenceJson As String
        Public Property ReportJson As String
        Public Property Headline As String
        Public Property AnalysisModel As String
        Public Property [Error] As String
        Public Property Note As String
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        Public Property CompletedUtc As DateTime?
        Public Property LessonCount As Integer
    End Class

    Public NotInheritable Class Lesson
        Public Property Id As String
        Public Property CaseStudyId As String
        Public Property SourceAgentId As String
        Public Property Outcome As String
        ''' <summary>Do | Avoid</summary>
        Public Property Kind As String
        Public Property Title As String
        Public Property Body As String
        Public Property Tags As String
        ''' <summary>Active | PendingReview | Retracted</summary>
        Public Property Status As String
        Public Property Pinned As Boolean
        Public Property UseCount As Long
        Public Property LastUsedUtc As DateTime?
        ''' <summary>Case studies that support this lesson: its origin plus later studies that confirmed it.</summary>
        Public Property Confirmations As Integer = 1
        Public Property LastConfirmedUtc As DateTime?
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
        Public Property UpdatedBy As String
    End Class

    ''' <summary>Persistence for case studies and the shared lesson library (FTS5 search, usage counting).</summary>
    Public NotInheritable Class LessonStore
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Private Shared Function MapStudy(r As Microsoft.Data.Sqlite.SqliteDataReader) As CaseStudy
            Return New CaseStudy With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .Outcome = Db.Str(r, "Outcome"), .TriggerKind = Db.Str(r, "TriggerKind"),
                .TriggerDetail = Db.Str(r, "TriggerDetail"), .Status = Db.Str(r, "Status"), .WindowStartUtc = Db.Dt(r, "WindowStartUtc"),
                .WindowEndUtc = Db.Dt(r, "WindowEndUtc"), .EvidenceJson = Db.Str(r, "EvidenceJson"), .ReportJson = Db.Str(r, "ReportJson"),
                .Headline = Db.Str(r, "Headline"), .AnalysisModel = Db.Str(r, "AnalysisModel"), .Error = Db.Str(r, "Error"), .Note = Db.Str(r, "Note"),
                .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .CompletedUtc = Db.DtN(r, "CompletedUtc"),
                .LessonCount = If(Db.HasColumn(r, "LessonCount"), Db.Int(r, "LessonCount"), 0)}
        End Function

        Private Shared Function MapLesson(r As Microsoft.Data.Sqlite.SqliteDataReader) As Lesson
            Return New Lesson With {
                .Id = Db.Str(r, "Id"), .CaseStudyId = Db.Str(r, "CaseStudyId"), .SourceAgentId = Db.Str(r, "SourceAgentId"), .Outcome = Db.Str(r, "Outcome"),
                .Kind = Db.Str(r, "Kind"), .Title = Db.Str(r, "Title"), .Body = Db.Str(r, "Body"), .Tags = Db.Str(r, "Tags"), .Status = Db.Str(r, "Status"),
                .Pinned = Db.Bool(r, "Pinned"), .UseCount = Db.Lng(r, "UseCount"), .LastUsedUtc = Db.DtN(r, "LastUsedUtc"), .CreatedUtc = Db.Dt(r, "CreatedUtc"),
                .UpdatedUtc = Db.Dt(r, "UpdatedUtc"), .UpdatedBy = Db.Str(r, "UpdatedBy"),
                .Confirmations = Math.Max(1, Db.Int(r, "Confirmations")), .LastConfirmedUtc = Db.DtN(r, "LastConfirmedUtc")}
        End Function

        ' ---------------------------------------------------------------- case studies

        Public Function InsertStudyAsync(s As CaseStudy) As Task(Of Integer)
            Return _db.ExecuteAsync(
                "INSERT INTO CaseStudies(Id, AgentId, Outcome, TriggerKind, TriggerDetail, Status, WindowStartUtc, WindowEndUtc, Note, CreatedBy, CreatedUtc) " &
                "VALUES (@id, @a, @o, @t, @d, @s, @ws, @we, @n, @by, @c)",
                ("@id", CObj(s.Id)), ("@a", s.AgentId), ("@o", s.Outcome), ("@t", s.TriggerKind), ("@d", TextUtil.Truncate(s.TriggerDetail, 500)), ("@s", s.Status),
                ("@ws", Db.Ts(s.WindowStartUtc)), ("@we", Db.Ts(s.WindowEndUtc)), ("@n", TextUtil.Truncate(s.Note, 2000)), ("@by", s.CreatedBy), ("@c", Db.Ts(s.CreatedUtc)))
        End Function

        Public Function SetStudyStatusAsync(id As String, status As String, Optional errorMessage As String = Nothing) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE CaseStudies SET Status=@s, Error=@e WHERE Id=@id", ("@s", CObj(status)), ("@e", TextUtil.Truncate(errorMessage, 2000)), ("@id", id))
        End Function

        Public Function CompleteStudyAsync(id As String, status As String, evidenceJson As String, reportJson As String, headline As String, model As String, Optional errorMessage As String = Nothing) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE CaseStudies SET Status=@s, EvidenceJson=@ev, ReportJson=@r, Headline=@h, AnalysisModel=@m, Error=@e, CompletedUtc=@c WHERE Id=@id",
                                    ("@s", CObj(status)), ("@ev", evidenceJson), ("@r", reportJson), ("@h", TextUtil.Truncate(headline, 300)), ("@m", model),
                                    ("@e", TextUtil.Truncate(errorMessage, 2000)), ("@c", Db.Ts(DateTime.UtcNow)), ("@id", id))
        End Function

        Public Function GetStudyAsync(id As String) As Task(Of CaseStudy)
            Return _db.QuerySingleAsync("SELECT s.*, (SELECT COUNT(*) FROM Lessons l WHERE l.CaseStudyId=s.Id) AS LessonCount FROM CaseStudies s WHERE s.Id=@id",
                                        AddressOf MapStudy, ("@id", CObj(id)))
        End Function

        ''' <summary>Studies (newest first) without the large evidence/report blobs unless requested.</summary>
        Public Function ListStudiesAsync(Optional agentId As String = Nothing, Optional outcome As String = Nothing, Optional status As String = Nothing, Optional limit As Integer = 100) As Task(Of List(Of CaseStudy))
            Dim where As New List(Of String)
            If Not String.IsNullOrEmpty(agentId) Then where.Add("s.AgentId=@a")
            If Not String.IsNullOrEmpty(outcome) Then where.Add("s.Outcome=@o")
            If Not String.IsNullOrEmpty(status) Then where.Add("s.Status=@s")
            Return _db.QueryAsync("SELECT s.Id, s.AgentId, s.Outcome, s.TriggerKind, s.TriggerDetail, s.Status, s.WindowStartUtc, s.WindowEndUtc, NULL AS EvidenceJson, NULL AS ReportJson, " &
                                  "s.Headline, s.AnalysisModel, s.Error, s.Note, s.CreatedBy, s.CreatedUtc, s.CompletedUtc, (SELECT COUNT(*) FROM Lessons l WHERE l.CaseStudyId=s.Id) AS LessonCount " &
                                  "FROM CaseStudies s" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY s.CreatedUtc DESC LIMIT @l",
                                  AddressOf MapStudy, ("@a", CObj(agentId)), ("@o", outcome), ("@s", status), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        ''' <summary>Most recent study of an agent with the given outcome that did not fail, or Nothing.</summary>
        Public Async Function LastStudyUtcAsync(agentId As String, outcome As String) As Task(Of DateTime?)
            Dim v = Await _db.ScalarAsync("SELECT MAX(CreatedUtc) FROM CaseStudies WHERE AgentId=@a AND Outcome=@o AND Status<>'Failed'", ("@a", CObj(agentId)), ("@o", outcome)).ConfigureAwait(False)
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return Nothing
            Return Db.ParseTs(CStr(v))
        End Function

        ''' <summary>End of the agent's latest completed study (start of the next study's window).</summary>
        Public Async Function LastCompletedWindowEndAsync(agentId As String) As Task(Of DateTime?)
            Dim v = Await _db.ScalarAsync("SELECT MAX(WindowEndUtc) FROM CaseStudies WHERE AgentId=@a AND Status IN ('Published','AwaitingReview')", ("@a", CObj(agentId))).ConfigureAwait(False)
            If v Is Nothing OrElse TypeOf v Is DBNull Then Return Nothing
            Return Db.ParseTs(CStr(v))
        End Function

        Public Function ListUnfinishedStudyIdsAsync() As Task(Of List(Of String))
            Return _db.QueryAsync("SELECT Id FROM CaseStudies WHERE Status IN ('Pending','Analyzing') ORDER BY CreatedUtc", Function(r) Db.Str(r, "Id"))
        End Function

        ' ---------------------------------------------------------------- lessons

        Public Async Function InsertLessonsAsync(lessons As IEnumerable(Of Lesson)) As Task
            Dim list = lessons.ToList()
            If list.Count = 0 Then Return
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    For Each l In list
                        Await Database.TxExecAsync(conn, tx,
                            "INSERT INTO Lessons(Id, CaseStudyId, SourceAgentId, Outcome, Kind, Title, Body, Tags, Status, Pinned, UseCount, CreatedUtc, UpdatedUtc) " &
                            "VALUES (@id, @cs, @a, @o, @k, @t, @b, @tags, @s, 0, 0, @c, @c)",
                            ("@id", CObj(l.Id)), ("@cs", l.CaseStudyId), ("@a", l.SourceAgentId), ("@o", l.Outcome), ("@k", l.Kind), ("@t", l.Title), ("@b", l.Body),
                            ("@tags", l.Tags), ("@s", l.Status), ("@c", Db.Ts(l.CreatedUtc))).ConfigureAwait(False)
                    Next
                    Return True
                End Function).ConfigureAwait(False)
        End Function

        Public Function RetractLessonsOfStudyAsync(studyId As String, actor As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE Lessons SET Status='Retracted', UpdatedUtc=@n, UpdatedBy=@by WHERE CaseStudyId=@s AND Status<>'Retracted'",
                                    ("@n", CObj(Db.Ts(DateTime.UtcNow))), ("@by", actor), ("@s", studyId))
        End Function

        Private Const RecountSql As String =
            "UPDATE Lessons SET Confirmations = 1 + (SELECT COUNT(*) FROM LessonConfirmations c WHERE c.LessonId=Lessons.Id), " &
            "LastConfirmedUtc = (SELECT MAX(c.CreatedUtc) FROM LessonConfirmations c WHERE c.LessonId=Lessons.Id) WHERE Id IN "

        ''' <summary>Records that a case study reached the same conclusion as existing lessons (idempotent per study).</summary>
        Public Async Function ConfirmLessonsAsync(lessonIds As IEnumerable(Of String), studyId As String, agentId As String, outcome As String) As Task
            Dim ids = lessonIds.Distinct(StringComparer.Ordinal).ToList()
            If ids.Count = 0 Then Return
            Dim now = Db.Ts(DateTime.UtcNow)
            Await _db.InTransactionAsync(Of Boolean)(
                Async Function(conn, tx)
                    For Each id In ids
                        Await Database.TxExecAsync(conn, tx,
                            "INSERT OR IGNORE INTO LessonConfirmations(LessonId, CaseStudyId, AgentId, Outcome, CreatedUtc) VALUES (@l, @s, @a, @o, @c)",
                            ("@l", CObj(id)), ("@s", studyId), ("@a", agentId), ("@o", outcome), ("@c", now)).ConfigureAwait(False)
                    Next
                    Await Database.TxExecAsync(conn, tx, RecountSql & "(" & String.Join(",", ids.Select(Function(x, i) "@p" & i)) & ")",
                                               ids.Select(Function(x, i) ("@p" & i, CObj(x))).ToArray()).ConfigureAwait(False)
                    Return True
                End Function).ConfigureAwait(False)
        End Function

        ''' <summary>Undoes a study's confirmations (before it is re-analyzed).</summary>
        Public Async Function RemoveConfirmationsOfStudyAsync(studyId As String) As Task
            Dim ids = Await _db.QueryAsync("SELECT LessonId FROM LessonConfirmations WHERE CaseStudyId=@s", Function(r) Db.Str(r, "LessonId"), ("@s", CObj(studyId))).ConfigureAwait(False)
            If ids.Count = 0 Then Return
            Await _db.ExecuteAsync("DELETE FROM LessonConfirmations WHERE CaseStudyId=@s", ("@s", CObj(studyId))).ConfigureAwait(False)
            Await _db.ExecuteAsync(RecountSql & "(" & String.Join(",", ids.Select(Function(x, i) "@p" & i)) & ")",
                                   ids.Select(Function(x, i) ("@p" & i, CObj(x))).ToArray()).ConfigureAwait(False)
        End Function

        ''' <summary>Existing lessons a study confirmed.</summary>
        Public Function ListConfirmedByStudyAsync(studyId As String) As Task(Of List(Of Lesson))
            Return _db.QueryAsync("SELECT l.* FROM LessonConfirmations c JOIN Lessons l ON l.Id=c.LessonId WHERE c.CaseStudyId=@s ORDER BY l.Title",
                                  AddressOf MapLesson, ("@s", CObj(studyId)))
        End Function

        ''' <summary>Lessons a new study is compared against (not retracted): best-established first.</summary>
        Public Function LibraryAsync(limit As Integer) As Task(Of List(Of Lesson))
            Return _db.QueryAsync("SELECT * FROM Lessons WHERE Status<>'Retracted' ORDER BY Pinned DESC, Confirmations DESC, UseCount DESC, CreatedUtc DESC LIMIT @l",
                                  AddressOf MapLesson, ("@l", CObj(Math.Clamp(limit, 1, 2000))))
        End Function

        Public Function GetLessonAsync(id As String) As Task(Of Lesson)
            Return _db.QuerySingleAsync("SELECT * FROM Lessons WHERE Id=@id", AddressOf MapLesson, ("@id", CObj(id)))
        End Function

        Public Function ListLessonsAsync(Optional status As String = Nothing, Optional outcome As String = Nothing, Optional agentId As String = Nothing,
                                         Optional studyId As String = Nothing, Optional limit As Integer = 200) As Task(Of List(Of Lesson))
            Dim where As New List(Of String)
            If Not String.IsNullOrEmpty(status) Then where.Add("Status=@s")
            If Not String.IsNullOrEmpty(outcome) Then where.Add("Outcome=@o")
            If Not String.IsNullOrEmpty(agentId) Then where.Add("SourceAgentId=@a")
            If Not String.IsNullOrEmpty(studyId) Then where.Add("CaseStudyId=@cs")
            Return _db.QueryAsync("SELECT * FROM Lessons" & If(where.Count > 0, " WHERE " & String.Join(" AND ", where), "") & " ORDER BY Pinned DESC, CreatedUtc DESC LIMIT @l",
                                  AddressOf MapLesson, ("@s", CObj(status)), ("@o", outcome), ("@a", agentId), ("@cs", studyId), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        ''' <summary>Full-text search over lessons (Active only unless includeInactive).</summary>
        Public Async Function SearchAsync(query As String, limit As Integer, Optional includeInactive As Boolean = False) As Task(Of List(Of Lesson))
            Dim fts = SqliteMemoryStore.BuildFtsQuery(query)
            Dim statusFilter = If(includeInactive, "", " AND l.Status='Active'")
            If fts Is Nothing Then
                Return Await _db.QueryAsync($"SELECT l.* FROM Lessons l WHERE 1=1{statusFilter} ORDER BY l.Pinned DESC, l.CreatedUtc DESC LIMIT @l", AddressOf MapLesson,
                                            ("@l", CObj(Math.Clamp(limit, 1, 200)))).ConfigureAwait(False)
            End If
            Dim results As List(Of Lesson) = Nothing
            Try
                results = Await _db.QueryAsync("SELECT l.* FROM LessonsFts f JOIN Lessons l ON l.rowid=f.rowid " &
                                               $"WHERE LessonsFts MATCH @q{statusFilter} ORDER BY bm25(LessonsFts) - (l.Pinned * 2.0) - (MIN(l.Confirmations, 6) - 1) * 0.5 LIMIT @l", AddressOf MapLesson,
                                               ("@q", CObj(fts)), ("@l", Math.Clamp(limit, 1, 200))).ConfigureAwait(False)
            Catch ex As Microsoft.Data.Sqlite.SqliteException
                results = Nothing
            End Try
            If results IsNot Nothing Then Return results
            Return Await _db.QueryAsync($"SELECT l.* FROM Lessons l WHERE (l.Title LIKE @like OR l.Body LIKE @like){statusFilter} ORDER BY l.CreatedUtc DESC LIMIT @l",
                                        AddressOf MapLesson, ("@like", CObj("%" & query & "%")), ("@l", Math.Clamp(limit, 1, 200))).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' Lessons to show an agent for a task: pinned lessons first, then the best text matches, then recent ones; bounded
        ''' by count and characters. Usage is counted so administrators can see which lessons circulate.
        ''' </summary>
        Public Async Function RelevantAsync(query As String, limit As Integer, maxChars As Integer) As Task(Of List(Of Lesson))
            Dim result As New List(Of Lesson)
            If limit <= 0 Then Return result
            Dim seen As New HashSet(Of String)
            Dim budget = maxChars
            Dim add = Sub(l As Lesson)
                          If result.Count >= limit OrElse Not seen.Add(l.Id) Then Return
                          Dim size = l.Title.Length + l.Body.Length + 40
                          If size > budget Then Return
                          budget -= size
                          result.Add(l)
                      End Sub
            For Each l In Await _db.QueryAsync("SELECT * FROM Lessons WHERE Status='Active' AND Pinned=1 ORDER BY UpdatedUtc DESC LIMIT @l", AddressOf MapLesson, ("@l", CObj(limit))).ConfigureAwait(False)
                add(l)
            Next
            If result.Count < limit AndAlso Not String.IsNullOrWhiteSpace(query) Then
                For Each l In Await SearchAsync(query, limit * 2).ConfigureAwait(False)
                    add(l)
                Next
            End If
            If result.Count < limit Then
                ' Recent lessons, plus older ones that several studies have confirmed.
                For Each l In Await _db.QueryAsync("SELECT * FROM Lessons WHERE Status='Active' AND (CreatedUtc>=@s OR Confirmations>=2) " &
                                                   "ORDER BY Confirmations DESC, COALESCE(LastConfirmedUtc, CreatedUtc) DESC LIMIT @l", AddressOf MapLesson,
                                                   ("@s", CObj(Db.Ts(DateTime.UtcNow.AddDays(-30)))), ("@l", limit)).ConfigureAwait(False)
                    add(l)
                Next
            End If
            If result.Count > 0 Then
                Dim now = Db.Ts(DateTime.UtcNow)
                Await _db.ExecuteAsync($"UPDATE Lessons SET UseCount=UseCount+1, LastUsedUtc=@n WHERE Id IN ({String.Join(",", result.Select(Function(x, i) "@p" & i))})",
                                       {("@n", CObj(now))}.Concat(result.Select(Function(x, i) ("@p" & i, CObj(x.Id)))).ToArray()).ConfigureAwait(False)
            End If
            Return result
        End Function

        ''' <summary>Admin edit: any argument left Nothing is unchanged.</summary>
        Public Async Function UpdateLessonAsync(id As String, title As String, body As String, tags As String, status As String, pinned As Boolean?, actor As String) As Task(Of Lesson)
            Dim l = Await GetLessonAsync(id).ConfigureAwait(False)
            If l Is Nothing Then Return Nothing
            If title IsNot Nothing Then l.Title = Sanitize(title, 120)
            If body IsNot Nothing Then l.Body = Sanitize(body, 800)
            If tags IsNot Nothing Then l.Tags = Sanitize(tags, 200)
            If status IsNot Nothing Then l.Status = status
            If pinned.HasValue Then l.Pinned = pinned.Value
            If String.IsNullOrWhiteSpace(l.Title) OrElse String.IsNullOrWhiteSpace(l.Body) Then Throw New ArgumentException("Title and text are required.")
            Await _db.ExecuteAsync("UPDATE Lessons SET Title=@t, Body=@b, Tags=@tags, Status=@s, Pinned=@p, UpdatedUtc=@n, UpdatedBy=@by WHERE Id=@id",
                                   ("@t", CObj(l.Title)), ("@b", l.Body), ("@tags", l.Tags), ("@s", l.Status), ("@p", l.Pinned), ("@n", Db.Ts(DateTime.UtcNow)),
                                   ("@by", actor), ("@id", id)).ConfigureAwait(False)
            Return Await GetLessonAsync(id).ConfigureAwait(False)
        End Function

        Public Async Function SummaryAsync() As Task(Of Dictionary(Of String, Object))
            Dim studies = Await _db.QueryAsync("SELECT Outcome, Status, COUNT(*) AS C FROM CaseStudies GROUP BY Outcome, Status",
                                               Function(r) (Db.Str(r, "Outcome"), Db.Str(r, "Status"), Db.Lng(r, "C"))).ConfigureAwait(False)
            Dim lessons = Await _db.QueryAsync("SELECT Status, COUNT(*) AS C, COALESCE(SUM(UseCount),0) AS U, COALESCE(SUM(Confirmations-1),0) AS K FROM Lessons GROUP BY Status",
                                               Function(r) (Db.Str(r, "Status"), Db.Lng(r, "C"), Db.Lng(r, "U"), Db.Lng(r, "K"))).ConfigureAwait(False)
            Dim last = Await _db.ScalarAsync("SELECT MAX(CreatedUtc) FROM CaseStudies").ConfigureAwait(False)
            Return New Dictionary(Of String, Object) From {
                {"failureStudies", studies.Where(Function(x) x.Item1 = "Failure").Sum(Function(x) x.Item3)},
                {"successStudies", studies.Where(Function(x) x.Item1 = "Success").Sum(Function(x) x.Item3)},
                {"reviewStudies", studies.Where(Function(x) x.Item1 = "Review").Sum(Function(x) x.Item3)},
                {"studiesInProgress", studies.Where(Function(x) x.Item2 = "Pending" OrElse x.Item2 = "Analyzing").Sum(Function(x) x.Item3)},
                {"activeLessons", lessons.Where(Function(x) x.Item1 = "Active").Sum(Function(x) x.Item2)},
                {"pendingLessons", lessons.Where(Function(x) x.Item1 = "PendingReview").Sum(Function(x) x.Item2)},
                {"retractedLessons", lessons.Where(Function(x) x.Item1 = "Retracted").Sum(Function(x) x.Item2)},
                {"lessonUses", lessons.Sum(Function(x) x.Item3)},
                {"confirmations", lessons.Where(Function(x) x.Item1 <> "Retracted").Sum(Function(x) x.Item4)},
                {"lastStudyUtc", If(last Is Nothing OrElse TypeOf last Is DBNull, Nothing, CObj(Db.ParseTs(CStr(last))))}}
        End Function

        ''' <summary>Lesson text is shown to many agents: strip markup-like characters and bound the length.</summary>
        Public Shared Function Sanitize(text As String, max As Integer) As String
            If text Is Nothing Then Return Nothing
            Dim t = text.Replace("<", "(").Replace(">", ")").Replace(vbCr, " ").Replace(vbLf, " ").Trim()
            While t.Contains("  ")
                t = t.Replace("  ", " ")
            End While
            Return TextUtil.Truncate(t, max)
        End Function
    End Class

End Namespace
