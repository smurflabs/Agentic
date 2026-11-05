Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Text.Json
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: files attached to tasks, delivery/review options of tasks, the Results inbox (view, download, rate, award)
    ' and reviving agents that died when their money ran out.
    Partial Friend NotInheritable Class AdminApi

        Private Sub RegisterResults(r As Router)
            r.Map("POST", "/api/uploads", AuthLevel.Admin, AddressOf Upload)
            r.Map("GET", "/api/uploads/{uploadId}", AuthLevel.Read, AddressOf GetUpload)
            r.Map("GET", "/api/results", AuthLevel.Read, AddressOf ListResults)
            r.Map("GET", "/api/results/{resultId}", AuthLevel.Read, AddressOf GetResult)
            r.Map("GET", "/api/results/{resultId}/file", AuthLevel.Read, AddressOf ResultFile)
            r.Map("GET", "/api/results/{resultId}/preview", AuthLevel.Read, AddressOf ResultPreview)
            r.Map("GET", "/api/results/{resultId}/zip", AuthLevel.Read, AddressOf ResultZip)
            r.Map("POST", "/api/results/{resultId}/rate", AuthLevel.Admin, AddressOf RateResult)
            r.Map("POST", "/api/results/{resultId}/award", AuthLevel.Admin, AddressOf AwardResult)
            r.Map("POST", "/api/results/{resultId}/archive", AuthLevel.Admin, AddressOf ArchiveResult)
            r.Map("POST", "/api/agents/{id}/revive", AuthLevel.Admin, AddressOf ReviveAgent)
        End Sub

        ' ------------------------------------------------------------------ delivery options (task forms)

        ''' <summary>
        ''' Reads deliver / reviewerAgentId / maxRevisions / reviewCriteria / attachments from a task form. Attachments are
        ''' copied to agentId when given (agent tasks), or only checked (global tasks copy them to each step's agent).
        ''' </summary>
        Private Async Function ParseDeliveryAsync(rc As RequestContext, b As JsonElement, agentId As String) As Task(Of (Delivery As TaskDelivery, Criteria As String))
            Dim deliverEl As JsonElement
            Dim deliver = b.TryGetProperty("deliver", deliverEl) AndAlso deliverEl.ValueKind = JsonValueKind.True
            Dim reviewer = JsonUtil.GetString(b, "reviewerAgentId")
            If String.IsNullOrWhiteSpace(reviewer) Then reviewer = Nothing
            If reviewer IsNot Nothing Then
                If Not _h.Agents.Exists(reviewer) OrElse _h.Agents.IsSubagent(reviewer) Then Throw ApiException.BadRequest($"Unknown reviewer '{reviewer}'.")
                RequireAgentAccess(rc, reviewer)
            End If
            Dim criteria = JsonUtil.GetString(b, "reviewCriteria")
            If String.IsNullOrWhiteSpace(criteria) Then criteria = Nothing
            If criteria IsNot Nothing AndAlso reviewer Is Nothing Then Throw ApiException.BadRequest("The expected outcome is only shown to a reviewer: choose who reviews the work.")
            Dim uploadIds = JsonUtil.GetStringList(b, "attachments")
            ' {chatId}: the chat the work is handed over from; its result is posted back there. Only the caller's own chats.
            Dim chatId = JsonUtil.GetString(b, "chatId")
            If String.IsNullOrWhiteSpace(chatId) Then chatId = Nothing
            If chatId IsNot Nothing AndAlso (_h.Chat Is Nothing OrElse Await _h.Chat.GetAsync(rc.User.Name, chatId, False).ConfigureAwait(False) Is Nothing) Then
                Throw ApiException.BadRequest("That chat does not exist (the result could not be posted back to it).")
            End If
            ' {bountyUsd}: a reward paid to the agent when the work is accepted. It adds money, so it needs "Manage money".
            Dim bountyUsd = JsonUtil.GetDecimal(b, "bountyUsd", 0D)
            If bountyUsd < 0D Then Throw ApiException.BadRequest("bountyUsd cannot be negative.")
            If bountyUsd > 0D Then
                If Not _h.Options.Autonomy.BountiesEnabled Then Throw ApiException.BadRequest("Bounties are turned off on this server (Autonomy.BountiesEnabled).")
                If bountyUsd > _h.Options.Autonomy.MaxBountyUsd Then Throw ApiException.BadRequest($"A bounty can be at most {_h.Options.Autonomy.MaxBountyUsd} USD (Autonomy.MaxBountyUsd).")
                If Not rc.User.HasPermission("finance.manage") Then Throw New ApiException(403, "FORBIDDEN", "Offering a bounty needs the Manage money permission.")
            End If
            If Not deliver AndAlso reviewer Is Nothing AndAlso uploadIds.Count = 0 AndAlso chatId Is Nothing AndAlso bountyUsd <= 0D Then Return (Nothing, Nothing)
            Dim atts As List(Of TaskAttachment)
            Try
                atts = Await _h.Attachments.DescribeUploadsAsync(uploadIds).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            If agentId IsNot Nothing AndAlso atts.Count > 0 Then
                atts = Await _h.Attachments.MaterializeAsync(atts, agentId, Ids.NewId("att")).ConfigureAwait(False)
            End If
            Dim d As New TaskDelivery With {.Deliver = deliver, .ReviewerAgentId = reviewer, .MaxRevisions = Math.Clamp(JsonUtil.GetInt(b, "maxRevisions", 1), 0, 5),
                                            .Attachments = atts, .ChatId = chatId, .ChatUser = If(chatId Is Nothing, Nothing, rc.User.Name),
                                            .BountyMicros = TextUtil.DollarsToMicros(bountyUsd)}
            Return (d, If(criteria Is Nothing, Nothing, TextUtil.Truncate(criteria.Trim(), 20000)))
        End Function

        ' ------------------------------------------------------------------ uploads

        Private Async Function Upload(rc As RequestContext) As Task
            Dim name = rc.QueryString("name")
            If String.IsNullOrWhiteSpace(name) Then Throw ApiException.BadRequest("name is required (?name=file.ext).")
            Dim data = Await rc.ReadBytesAsync(_h.Uploads.MaxBytes).ConfigureAwait(False)
            Dim info As Artifacts.UploadInfo
            Try
                info = Await _h.Uploads.SaveAsync(name, data, rc.User.Name).ConfigureAwait(False)
            Catch ex As ArgumentException
                Throw ApiException.BadRequest(ex.Message)
            End Try
            Await AuditAsync(rc, "upload", "upload", info.Id, New With {info.Name, info.SizeBytes, info.Kind}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, UploadDto(info, 600)).ConfigureAwait(False)
        End Function

        Private Shared Function UploadDto(u As Artifacts.UploadInfo, previewChars As Integer) As Object
            Return New With {.id = u.Id, .name = u.Name, .mimeType = u.MimeType, .sizeBytes = u.SizeBytes, .kind = u.Kind, .summary = u.Summary,
                             .textChars = If(u.ExtractedText?.Length, 0), .preview = TextUtil.Truncate(u.ExtractedText, previewChars), .createdUtc = u.CreatedUtc}
        End Function

        Private Async Function GetUpload(rc As RequestContext) As Task
            Dim u = Await _h.Uploads.GetAsync(rc.Route("uploadId")).ConfigureAwait(False)
            If u Is Nothing Then Throw ApiException.NotFound("UPLOAD_NOT_FOUND", "Upload does not exist.")
            Await rc.OkAsync(UploadDto(u, 20000)).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ results

        Private Shared Function ResultVisible(rc As RequestContext, r As ResultRecord) As Boolean
            If rc.User.SeesAllAgents Then Return True
            Return r.Participants.Any(Function(a) Sees(rc, a))
        End Function

        Private Async Function ResultOrThrow(rc As RequestContext) As Task(Of ResultRecord)
            Dim r = Await _h.Results.GetAsync(rc.Route("resultId")).ConfigureAwait(False)
            If r Is Nothing OrElse Not ResultVisible(rc, r) Then Throw ApiException.NotFound("RESULT_NOT_FOUND", "Result does not exist.")
            Return r
        End Function

        Private Async Function ListResults(rc As RequestContext) As Task
            Dim list = (Await _h.Results.ListAsync(rc.QueryString("status"), rc.QueryInt("limit", 200, 1, 1000)).ConfigureAwait(False)).Where(Function(x) ResultVisible(rc, x)).ToList()
            Dim counts = Await _db_CountsAsync().ConfigureAwait(False)
            Await rc.OkAsync(New With {
                .results = list.Select(Function(x) New With {
                    .id = x.Id, .title = x.Title, .sourceType = x.SourceType, .sourceId = x.SourceId, .correlationId = x.CorrelationId, .agentId = x.AgentId,
                    .participants = x.Participants, .success = x.Success, .summary = TextUtil.Truncate(x.Summary, 280), .reviewVerdict = x.ReviewVerdict,
                    .reviewScore = x.ReviewScore, .reviewerAgentId = x.ReviewerAgentId, .revisions = x.Revisions, .files = x.Files.Count,
                    .images = x.Files.Where(Function(f) f.Kind = "image").Count(), .costMicros = x.CostMicros, .status = x.Status, .rating = x.Rating,
                    .awardedMicros = x.AwardedMicros, .createdUtc = x.CreatedUtc, .createdBy = x.CreatedBy}).ToList(),
                .counts = counts}).ConfigureAwait(False)
        End Function

        Private Async Function _db_CountsAsync() As Task(Of Dictionary(Of String, Long))
            Dim rows = Await _h.Database.QueryAsync("SELECT Status, COUNT(*) AS N FROM Results GROUP BY Status", Function(r) (Db.Str(r, "Status"), Db.Lng(r, "N"))).ConfigureAwait(False)
            Return rows.ToDictionary(Function(x) x.Item1, Function(x) x.Item2)
        End Function

        Private Async Function GetResult(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            If r.Status = "New" AndAlso rc.User.HasPermission("results") Then Await _h.Results.MarkViewedAsync(r.Id).ConfigureAwait(False)
            Dim awards = Await _h.Results.AwardsAsync(r.Id).ConfigureAwait(False)
            Dim wallets = (Await _h.Wallets.ListAsync().ConfigureAwait(False)).ToDictionary(Function(w) w.AgentId, StringComparer.OrdinalIgnoreCase)
            Dim people = r.Participants.Select(Function(a)
                                                   Dim w As Economy.WalletSnapshot = Nothing
                                                   wallets.TryGetValue(a, w)
                                                   Dim snap = _h.Agents.GetSnapshot(a)
                                                   Return New With {.agentId = a, .name = If(snap?.Name, a), .role = snap?.Role, .state = snap?.State,
                                                                    .balanceMicros = If(w Is Nothing, CType(Nothing, Long?), w.AvailableMicros),
                                                                    .awardedMicros = awards.Where(Function(x) String.Equals(x.AgentId, a, StringComparison.OrdinalIgnoreCase)).Sum(Function(x) x.AmountMicros)}
                                               End Function).ToList()
            Await rc.OkAsync(New With {
                .result = r, .awards = awards, .participants = people,
                .conversation = JsonUtil.ParseElementOrNull(r.ConversationJson),
                .data = JsonUtil.ParseElementOrNull(r.DataJson)}).ConfigureAwait(False)
        End Function

        Private Async Function ResultFile(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            Dim rel = rc.QueryString("path")
            Dim full = _h.Results.ResolveFile(r, rel)
            If full Is Nothing Then Throw ApiException.NotFound("FILE_NOT_FOUND", "File does not exist.")
            Dim f = r.Files.First(Function(x) x.Path = rel)
            Dim download = rc.QueryString("download") = "1"
            ' Only images and PDFs are shown inline; everything else downloads (no HTML or scripts rendered from agent output).
            Dim inlineOk = f.Kind = "image" AndAlso Not f.Name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) OrElse f.MimeType = "application/pdf"
            Dim mime = If(inlineOk, f.MimeType, If(f.Kind = "text" AndAlso Not download, "text/plain; charset=utf-8", "application/octet-stream"))
            Dim disposition = If(download OrElse Not (inlineOk OrElse f.Kind = "text"), "attachment", "inline") & "; filename*=UTF-8''" & Uri.EscapeDataString(f.Name)
            Await rc.WriteBytesAsync(200, mime, Await File.ReadAllBytesAsync(full).ConfigureAwait(False),
                                     New Dictionary(Of String, String) From {{"Content-Disposition", disposition}, {"Cache-Control", "private, max-age=300"},
                                                                             {"Content-Security-Policy", "default-src 'none'; img-src 'self' data:; style-src 'unsafe-inline'; sandbox"}}).ConfigureAwait(False)
        End Function

        Private Async Function ResultPreview(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            Dim rel = rc.QueryString("path")
            Dim full = _h.Results.ResolveFile(r, rel)
            If full Is Nothing Then Throw ApiException.NotFound("FILE_NOT_FOUND", "File does not exist.")
            Dim size = New FileInfo(full).Length
            If size > 50L * 1024 * 1024 Then
                Await rc.OkAsync(New With {.kind = "binary", .summary = $"Too large to preview ({FileExtractor.SizeText(size)}); download it.", .text = CStr(Nothing)}).ConfigureAwait(False)
                Return
            End If
            Dim ex = FileExtractor.Extract(Path.GetFileName(full), Await File.ReadAllBytesAsync(full).ConfigureAwait(False), 400000)
            Await rc.OkAsync(New With {.kind = ex.Kind, .summary = ex.Summary, .text = ex.Text, .truncated = ex.Truncated}).ConfigureAwait(False)
        End Function

        Private Async Function ResultZip(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            Dim bytes = Await Task.Run(Function() _h.Results.ZipBytes(r)).ConfigureAwait(False)
            Dim name = UploadSafe(r.Title) & ".zip"
            Await rc.WriteBytesAsync(200, "application/zip", bytes,
                                     New Dictionary(Of String, String) From {{"Content-Disposition", "attachment; filename*=UTF-8''" & Uri.EscapeDataString(name)}}).ConfigureAwait(False)
        End Function

        Private Shared Function UploadSafe(s As String) As String
            Return Artifacts.UploadStore.SafeFileName(If(String.IsNullOrWhiteSpace(s), "result", s))
        End Function

        Private Async Function RateResult(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim rating = JsonUtil.GetInt(b, "rating", 0)
            If rating < 1 OrElse rating > 5 Then Throw ApiException.BadRequest("rating must be 1-5.")
            Dim updated = Await _h.Results.RateAsync(r.Id, rating, JsonUtil.GetString(b, "comment"), rc.User.Name).ConfigureAwait(False)
            Await AuditAsync(rc, "rate-result", "result", r.Id, New With {rating}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.rating = updated.Rating, .status = updated.Status}).ConfigureAwait(False)
        End Function

        Private Async Function AwardResult(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim list As New List(Of (AgentId As String, Micros As Long))
            Dim arr As JsonElement
            If Not b.TryGetProperty("awards", arr) OrElse arr.ValueKind <> JsonValueKind.Array Then Throw ApiException.BadRequest("awards is required: [{agentId, amount}].")
            For Each a In arr.EnumerateArray()
                Dim agent = JsonUtil.GetString(a, "agentId")
                Dim amountEl As JsonElement
                Dim amount As Decimal
                If Not a.TryGetProperty("amount", amountEl) OrElse Not amountEl.TryGetDecimal(amount) Then Throw ApiException.BadRequest("Each award needs an amount in USD.")
                If amount <= 0 Then Continue For
                If amount > 10000D Then Throw ApiException.BadRequest("An award may be at most $10,000.")
                If agent Is Nothing OrElse Not _h.Agents.Exists(agent) Then Throw ApiException.BadRequest($"Unknown agent '{agent}'.")
                RequireAgentAccess(rc, agent)
                list.Add((agent, TextUtil.DollarsToMicros(amount)))
            Next
            If list.Count = 0 Then Throw ApiException.BadRequest("Enter an amount for at least one agent.")
            Dim paid = Await _h.Results.AwardAsync(r.Id, list, JsonUtil.GetString(b, "note"), rc.User.Name, JsonUtil.GetString(b, "idempotencyKey")).ConfigureAwait(False)
            Await AuditAsync(rc, "award-result", "result", r.Id, New With {.awards = list.Select(Function(x) New With {x.AgentId, .usd = x.Micros / 1000000D})}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.paid = paid, .message = If(paid.Count = 0, "Already awarded (duplicate submit).", $"Awarded {paid.Count} agent(s).")}).ConfigureAwait(False)
        End Function

        Private Async Function ArchiveResult(rc As RequestContext) As Task
            Dim r = Await ResultOrThrow(rc).ConfigureAwait(False)
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim archivedEl As JsonElement
            Dim archived = Not b.TryGetProperty("archived", archivedEl) OrElse archivedEl.ValueKind <> JsonValueKind.False
            Await _h.Results.SetArchivedAsync(r.Id, archived).ConfigureAwait(False)
            Await rc.OkAsync(New With {.archived = archived}).ConfigureAwait(False)
        End Function

        ' ------------------------------------------------------------------ revival

        Private Async Function ReviveAgent(rc As RequestContext) As Task
            Dim id = AgentIdOrThrow(rc)
            Dim r = Await _h.Agents.ReviveAsync(id, "revived by " & rc.User.Name).ConfigureAwait(False)
            If r.Success Then Await AuditAsync(rc, "revive", "agent", id).ConfigureAwait(False)
            Await ResultAsync(rc, r).ConfigureAwait(False)
        End Function
    End Class

End Namespace
