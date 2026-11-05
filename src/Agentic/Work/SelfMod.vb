Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    Public NotInheritable Class SkillDef
        Public Property Id As String
        Public Property AgentId As String
        Public Property Name As String
        Public Property Description As String
        Public Property Instructions As String
        Public Property Uses As Integer
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    ''' <summary>
    ''' Reusable procedures an agent writes for itself (instructions only, never executable code): a checklist or method it
    ''' discovered and wants to follow again. Loading one drops its text into the task so the agent re-uses its own approach.
    ''' </summary>
    Public NotInheritable Class SkillStore
        Private ReadOnly _db As Database

        Public Sub New(db As Database)
            _db = db
        End Sub

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As SkillDef
            Return New SkillDef With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .Name = Db.Str(r, "Name"), .Description = Db.Str(r, "Description"),
                .Instructions = Db.Str(r, "Instructions"), .Uses = Db.Int(r, "Uses"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
        End Function

        Public Async Function SaveAsync(agentId As String, name As String, description As String, instructions As String) As Task(Of SkillDef)
            If String.IsNullOrWhiteSpace(name) Then Throw New InvalidOperationException("A skill name is required.")
            If String.IsNullOrWhiteSpace(instructions) Then Throw New InvalidOperationException("Skill instructions are required.")
            Dim now = DateTime.UtcNow
            Await _db.ExecuteAsync("INSERT INTO AgentSkills(Id, AgentId, Name, Description, Instructions, Uses, CreatedUtc, UpdatedUtc) " &
                                   "VALUES (@id, @a, @n, @d, @i, 0, @now, @now) " &
                                   "ON CONFLICT(AgentId, Name) DO UPDATE SET Description=@d, Instructions=@i, UpdatedUtc=@now",
                                   ("@id", CObj(Ids.NewId("skill"))), ("@a", agentId), ("@n", TextUtil.Truncate(name, 100)),
                                   ("@d", TextUtil.Truncate(description, 500)), ("@i", TextUtil.Truncate(instructions, 8000)), ("@now", Db.Ts(now))).ConfigureAwait(False)
            Return Await GetAsync(agentId, name).ConfigureAwait(False)
        End Function

        Public Function ListAsync(agentId As String) As Task(Of List(Of SkillDef))
            Return _db.QueryAsync("SELECT * FROM AgentSkills WHERE AgentId=@a ORDER BY Uses DESC, UpdatedUtc DESC LIMIT 200", AddressOf Map, ("@a", CObj(agentId)))
        End Function

        Public Function GetAsync(agentId As String, name As String) As Task(Of SkillDef)
            Return _db.QuerySingleAsync("SELECT * FROM AgentSkills WHERE AgentId=@a AND Name=@n", AddressOf Map, ("@a", CObj(agentId)), ("@n", CObj(name)))
        End Function

        Public Async Function UseAsync(agentId As String, name As String) As Task(Of SkillDef)
            Dim s = Await GetAsync(agentId, name).ConfigureAwait(False)
            If s IsNot Nothing Then Await _db.ExecuteAsync("UPDATE AgentSkills SET Uses=Uses+1 WHERE Id=@id", ("@id", CObj(s.Id))).ConfigureAwait(False)
            Return s
        End Function

        Public Async Function DeleteAsync(agentId As String, name As String) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("DELETE FROM AgentSkills WHERE AgentId=@a AND Name=@n", ("@a", CObj(agentId)), ("@n", CObj(name))).ConfigureAwait(False)) > 0
        End Function
    End Class

    Public NotInheritable Class ProposalDef
        Public Property Id As String
        Public Property AgentId As String
        Public Property Kind As String
        Public Property Proposed As String
        Public Property Rationale As String
        Public Property Status As String
        Public Property ApprovalId As String
        Public Property CreatedUtc As DateTime
        Public Property DecidedBy As String
        Public Property DecidedUtc As DateTime?
        Public Property RolledBackUtc As DateTime?
        Public Property RollbackReason As String
    End Class

    ''' <summary>
    ''' Approval-gated self-modification. An agent proposes a change to its own behaviour; it takes effect once approved (by an
    ''' administrator, or by the agent's policy or supervisor when it is autonomous). The only change applied is a behaviour
    ''' addendum appended to the agent's system prompt (AgentPromptPatches), so a self-edit can never widen the agent's
    ''' permissions or model access. An evaluation that shows the agent got worse rolls the change back.
    ''' </summary>
    Public NotInheritable Class ProfileProposalService
        Private ReadOnly _db As Database
        Private ReadOnly _log As ILog
        Private Const MaxPatchChars As Integer = 4000

        Public Sub New(db As Database, log As ILog)
            _db = db
            _log = log
        End Sub

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ProposalDef
            Return New ProposalDef With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .Kind = Db.Str(r, "Kind"), .Proposed = Db.Str(r, "Proposed"),
                .Rationale = Db.Str(r, "Rationale"), .Status = Db.Str(r, "Status"), .ApprovalId = Db.Str(r, "ApprovalId"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc"), .DecidedBy = Db.Str(r, "DecidedBy"), .DecidedUtc = Db.DtN(r, "DecidedUtc"),
                .RolledBackUtc = Db.DtN(r, "RolledBackUtc"), .RollbackReason = Db.Str(r, "RollbackReason")}
        End Function

        Public Async Function CreateAsync(agentId As String, kind As String, proposed As String, rationale As String, approvalId As String) As Task(Of ProposalDef)
            Dim p As New ProposalDef With {
                .Id = Ids.NewId("prop"), .AgentId = agentId, .Kind = If(kind, "behavior"), .Proposed = TextUtil.Truncate(proposed, MaxPatchChars),
                .Rationale = TextUtil.Truncate(rationale, 1000), .Status = "pending", .ApprovalId = approvalId, .CreatedUtc = DateTime.UtcNow}
            Await _db.ExecuteAsync("INSERT INTO ProfileProposals(Id, AgentId, Kind, Proposed, Rationale, Status, ApprovalId, CreatedUtc) " &
                                   "VALUES (@id, @a, @k, @p, @r, 'pending', @ap, @now)",
                                   ("@id", CObj(p.Id)), ("@a", agentId), ("@k", p.Kind), ("@p", p.Proposed), ("@r", p.Rationale), ("@ap", p.ApprovalId), ("@now", Db.Ts(p.CreatedUtc))).ConfigureAwait(False)
            Return p
        End Function

        Public Function ListAsync(Optional agentId As String = Nothing, Optional status As String = Nothing) As Task(Of List(Of ProposalDef))
            Dim clauses As New List(Of String)
            If agentId IsNot Nothing Then clauses.Add("AgentId=@a")
            If status IsNot Nothing Then clauses.Add("Status=@s")
            Dim where = If(clauses.Count > 0, " WHERE " & String.Join(" AND ", clauses), "")
            Return _db.QueryAsync($"SELECT * FROM ProfileProposals{where} ORDER BY CreatedUtc DESC LIMIT 500", AddressOf Map,
                                  ("@a", If(agentId, CObj(DBNull.Value))), ("@s", If(status, CObj(DBNull.Value))))
        End Function

        Public Function GetByApprovalAsync(approvalId As String) As Task(Of ProposalDef)
            Return _db.QuerySingleAsync("SELECT * FROM ProfileProposals WHERE ApprovalId=@ap AND Status='pending'", AddressOf Map, ("@ap", CObj(approvalId)))
        End Function

        ''' <summary>Marks a proposal approved and applies it (appends its text to the agent's prompt patch).</summary>
        Public Async Function ApproveAsync(proposalId As String, decidedBy As String) As Task(Of Boolean)
            Dim p = (Await _db.QueryAsync("SELECT * FROM ProfileProposals WHERE Id=@id", AddressOf Map, ("@id", CObj(proposalId))).ConfigureAwait(False)).FirstOrDefault()
            If p Is Nothing OrElse p.Status <> "pending" Then Return False
            Await _db.ExecuteAsync("UPDATE ProfileProposals SET Status='approved', DecidedBy=@by, DecidedUtc=@now WHERE Id=@id",
                                   ("@by", decidedBy), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", CObj(proposalId))).ConfigureAwait(False)
            ' Apply: append the behaviour addendum to the agent's prompt patch.
            Dim current = Await GetPatchAsync(p.AgentId).ConfigureAwait(False)
            Dim combined = If(String.IsNullOrEmpty(current), p.Proposed, current & vbLf & "- " & p.Proposed)
            combined = TextUtil.Truncate(combined, MaxPatchChars)
            Await _db.ExecuteAsync("INSERT INTO AgentPromptPatches(AgentId, Append, UpdatedUtc) VALUES (@a, @ap, @now) " &
                                   "ON CONFLICT(AgentId) DO UPDATE SET Append=@ap, UpdatedUtc=@now",
                                   ("@a", CObj(p.AgentId)), ("@ap", CObj(combined)), ("@now", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
            Return True
        End Function

        Public Async Function RejectAsync(proposalId As String, decidedBy As String) As Task(Of Boolean)
            Return (Await _db.ExecuteAsync("UPDATE ProfileProposals SET Status='rejected', DecidedBy=@by, DecidedUtc=@now WHERE Id=@id AND Status='pending'",
                                           ("@by", decidedBy), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", CObj(proposalId))).ConfigureAwait(False)) > 0
        End Function

        Public Async Function GetAsync(proposalId As String) As Task(Of ProposalDef)
            Return (Await _db.QueryAsync("SELECT * FROM ProfileProposals WHERE Id=@id", AddressOf Map, ("@id", CObj(proposalId))).ConfigureAwait(False)).FirstOrDefault()
        End Function

        ''' <summary>
        ''' Takes an applied behaviour change back out of the agent's prompt patch (an evaluation showed it made the agent worse,
        ''' or a person decided so). False when it is not applied or its text is no longer in the patch.
        ''' </summary>
        Public Async Function RollBackAsync(proposalId As String, reason As String) As Task(Of Boolean)
            Dim p = Await GetAsync(proposalId).ConfigureAwait(False)
            If p Is Nothing OrElse p.Status <> "approved" Then Return False
            Dim current = Await GetPatchAsync(p.AgentId).ConfigureAwait(False)
            Dim updated = RemoveFromPatch(current, p.Proposed)
            If updated Is Nothing Then Return False
            If updated.Length = 0 Then
                Await _db.ExecuteAsync("DELETE FROM AgentPromptPatches WHERE AgentId=@a", ("@a", CObj(p.AgentId))).ConfigureAwait(False)
            Else
                Await _db.ExecuteAsync("UPDATE AgentPromptPatches SET Append=@ap, UpdatedUtc=@now WHERE AgentId=@a",
                                       ("@ap", CObj(updated)), ("@now", Db.Ts(DateTime.UtcNow)), ("@a", p.AgentId)).ConfigureAwait(False)
            End If
            Await _db.ExecuteAsync("UPDATE ProfileProposals SET Status='rolled-back', RolledBackUtc=@now, RollbackReason=@r WHERE Id=@id",
                                   ("@now", CObj(Db.Ts(DateTime.UtcNow))), ("@r", TextUtil.Truncate(reason, 1000)), ("@id", proposalId)).ConfigureAwait(False)
            _log.Info("SelfMod", $"Behaviour change {proposalId} of {p.AgentId} rolled back: {TextUtil.Truncate(reason, 200)}", p.AgentId)
            Return True
        End Function

        ''' <summary>The patch without one applied change (entries are joined with a newline and "- "); Nothing when it is not there.</summary>
        Friend Shared Function RemoveFromPatch(patch As String, entry As String) As String
            If String.IsNullOrEmpty(patch) OrElse String.IsNullOrEmpty(entry) Then Return Nothing
            If patch = entry Then Return ""
            Dim firstThenMore = entry & vbLf & "- "
            If patch.StartsWith(firstThenMore, StringComparison.Ordinal) Then Return patch.Substring(firstThenMore.Length)
            Dim later = vbLf & "- " & entry
            Dim i = patch.LastIndexOf(later, StringComparison.Ordinal)
            If i < 0 Then Return Nothing
            ' Only a whole entry: the next one starts with a newline and "- ", or the patch ends.
            Dim afterEnd = i + later.Length
            If afterEnd <> patch.Length AndAlso Not patch.Substring(afterEnd).StartsWith(vbLf & "- ", StringComparison.Ordinal) Then Return Nothing
            Return patch.Remove(i, later.Length)
        End Function

        ''' <summary>The approved behaviour addendum for an agent, appended to its system prompt (Nothing when none).</summary>
        Public Async Function GetPatchAsync(agentId As String) As Task(Of String)
            Dim v = Await _db.ScalarAsync("SELECT Append FROM AgentPromptPatches WHERE AgentId=@a", ("@a", CObj(agentId))).ConfigureAwait(False)
            Return If(v Is Nothing OrElse TypeOf v Is DBNull, Nothing, v.ToString())
        End Function
    End Class

End Namespace
