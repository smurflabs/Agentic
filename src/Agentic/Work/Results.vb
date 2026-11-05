Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.IO.Compression
Imports System.Text.Json
Imports Agentic.Artifacts
Imports Agentic.Economy
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Memory
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A reviewer's decision on a piece of work.</summary>
    Public NotInheritable Class ReviewOutcome
        Public Property Reviewed As Boolean
        Public Property Approved As Boolean
        Public Property Score As Integer?
        Public Property Feedback As String
        Public Property ReviewerAgentId As String
        Public Property Note As String

        Public ReadOnly Property Verdict As String
            Get
                Return If(Not Reviewed, "NotReviewed", If(Approved, "Approved", "Rejected"))
            End Get
        End Property

        Public Shared Function NotReviewed(Optional note As String = Nothing) As ReviewOutcome
            Return New ReviewOutcome With {.Reviewed = False, .Approved = True, .Note = note}
        End Function
    End Class

    Public NotInheritable Class ResultFile
        ''' <summary>Path inside the result folder ("files/report.docx", "artifacts/chart.png").</summary>
        Public Property Path As String
        Public Property Name As String
        Public Property SizeBytes As Long
        Public Property MimeType As String
        Public Property Kind As String
        ''' <summary>shared (the workflow's results folder) | artifact</summary>
        Public Property Source As String
        Public Property AgentId As String
    End Class

    Public NotInheritable Class ResultAward
        Public Property AgentId As String
        Public Property AmountMicros As Long
        Public Property Note As String
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
    End Class

    Public NotInheritable Class ResultRecord
        Public Property Id As String
        Public Property SourceType As String
        Public Property SourceId As String
        Public Property CorrelationId As String
        Public Property Title As String
        Public Property AgentId As String
        Public Property Participants As New List(Of String)
        Public Property Success As Boolean
        Public Property Summary As String
        Public Property DataJson As String
        Public Property Instructions As String
        Public Property ReviewerAgentId As String
        Public Property ReviewVerdict As String
        Public Property ReviewScore As Integer?
        Public Property ReviewFeedback As String
        Public Property Revisions As Integer
        Public Property Files As New List(Of ResultFile)
        Public Property CostMicros As Long
        Public Property Status As String
        Public Property Rating As Integer?
        Public Property RatingComment As String
        Public Property RatedBy As String
        Public Property RatedUtc As DateTime?
        Public Property AwardedMicros As Long
        Public Property CreatedBy As String
        Public Property CreatedUtc As DateTime
        <Text.Json.Serialization.JsonIgnore>
        Public Property ConversationJson As String
        <Text.Json.Serialization.JsonIgnore>
        Public Property FolderPath As String
    End Class

    ''' <summary>
    ''' The Results inbox. When a task (or global task) that should be delivered is finished and accepted, the host
    ''' takes a snapshot: the answer, the reviewer's verdict, every file the agents produced (artifacts from the workflow's
    ''' tasks and the shared storage folder results/{workflow}/), the agents' conversation and the cost. Administrators
    ''' view and download it, rate it, and award money to the agents that took part (awards count as profit).
    ''' </summary>
    Public NotInheritable Class ResultService
        Private ReadOnly _db As Database
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _messages As MessageRepository
        Private ReadOnly _artifacts As ArtifactStore
        Private ReadOnly _wallets As WalletService
        Private ReadOnly _memory As IMemoryStore
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _sharedRoot As String
        Private ReadOnly _root As String
        Private Const MaxFileBytes As Long = 200L * 1024 * 1024
        Private Const MaxTotalBytes As Long = 1024L * 1024 * 1024

        Public Sub New(db As Database, tasks As TaskRepository, messages As MessageRepository, artifacts As ArtifactStore, wallets As WalletService,
                       memory As IMemoryStore, events As IAgentEventBus, log As ILog, sharedRoot As String, resultsRoot As String)
            _db = db
            _tasks = tasks
            _messages = messages
            _artifacts = artifacts
            _wallets = wallets
            _memory = memory
            _events = events
            _log = log
            _sharedRoot = Path.GetFullPath(sharedRoot)
            _root = Path.GetFullPath(resultsRoot)
            Directory.CreateDirectory(_root)
        End Sub

        ''' <summary>Folder (relative to the shared storage) where agents put files for a workflow's result.</summary>
        Public Shared Function DeliverablesFolder(correlationId As String) As String
            Return "results/" & correlationId
        End Function

        ' ------------------------------------------------------------------ delivery

        Public Async Function DeliverTaskAsync(worker As AgentTask, root As AgentTask, review As ReviewOutcome, revisions As Integer) As Task(Of String)
            Dim r = worker.GetResult()
            Dim ok = worker.Status = AgentTaskStatus.Completed AndAlso (r Is Nothing OrElse r.Success) AndAlso Not Rejected(review)
            Dim summary = If(r?.Summary, If(worker.ErrorMessage, worker.Status.ToString()))
            If worker.Status <> AgentTaskStatus.Completed Then summary = $"The task ended {worker.Status.ToString().ToLowerInvariant()}: {If(worker.ErrorMessage, "no result")}"
            Dim data = If(r IsNot Nothing AndAlso r.Data.HasValue AndAlso r.Data.Value.ValueKind <> JsonValueKind.Undefined AndAlso r.Data.Value.ValueKind <> JsonValueKind.Null,
                          r.Data.Value.GetRawText(), Nothing)
            If r IsNot Nothing AndAlso r.Warnings IsNot Nothing AndAlso r.Warnings.Count > 0 Then summary &= vbLf & vbLf & "Warnings: " & String.Join("; ", r.Warnings)
            Return Await CreateAsync("Task", root.Id, worker.CorrelationId, root.Name, worker.AgentId, ok, summary, data, root.Description, review, revisions,
                                     root.CreatedBy).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' The reviewer read the work and did not approve it: the result is not a success, whatever the task's status (LedgerLite
        ''' was delivered as a success with the reviewer's verdict "Rejected, 3/10").
        ''' </summary>
        Friend Shared Function Rejected(review As ReviewOutcome) As Boolean
            Return review IsNot Nothing AndAlso review.Reviewed AndAlso Not review.Approved
        End Function

        Public Function DeliverGlobalAsync(g As GlobalTask, review As ReviewOutcome) As Task(Of String)
            Dim ok = g.Status = "Completed" AndAlso Not String.IsNullOrWhiteSpace(g.FinalResult) AndAlso Not Rejected(review)
            Return CreateAsync("GlobalTask", g.Id, g.CorrelationId, g.Name, Nothing, ok, If(g.FinalResult, If(g.Error, g.Status)), Nothing, g.Description, review,
                               g.Revision, g.CreatedBy)
        End Function

        Private Async Function CreateAsync(sourceType As String, sourceId As String, correlationId As String, title As String, agentId As String, success As Boolean,
                                           summary As String, dataJson As String, instructions As String, review As ReviewOutcome, revisions As Integer,
                                           createdBy As String) As Task(Of String)
            Dim existing = Await _db.ScalarAsync("SELECT Id FROM Results WHERE SourceType=@t AND SourceId=@s", ("@t", CObj(sourceType)), ("@s", sourceId)).ConfigureAwait(False)
            If existing IsNot Nothing AndAlso Not TypeOf existing Is DBNull Then Return CStr(existing)
            review = If(review, ReviewOutcome.NotReviewed())
            Dim id = Ids.NewId("res")
            Dim folder = Path.Combine(_root, id)
            Directory.CreateDirectory(folder)
            Dim tasks = If(String.IsNullOrEmpty(correlationId), New List(Of AgentTask), Await _tasks.ListByCorrelationAsync(correlationId).ConfigureAwait(False))
            Dim participants = tasks.Select(Function(t) OwnerOf(t.AgentId)).Where(Function(a) Not a.StartsWith("host", StringComparison.OrdinalIgnoreCase)).
                Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            If agentId IsNot Nothing AndAlso Not participants.Contains(agentId, StringComparer.OrdinalIgnoreCase) Then participants.Insert(0, agentId)
            Dim files As New List(Of ResultFile)
            Try
                files = Await SnapshotFilesAsync(folder, correlationId).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Results", $"Could not copy every file for {title}: {ex.Message}")
            End Try
            Dim conversation = Await ConversationAsync(tasks, correlationId).ConfigureAwait(False)
            Dim cost = If(String.IsNullOrEmpty(correlationId), 0L, Await _wallets.CostByCorrelationAsync(correlationId).ConfigureAwait(False))
            Dim feedback = review.Feedback
            If Not String.IsNullOrEmpty(review.Note) Then feedback = If(String.IsNullOrEmpty(feedback), review.Note, feedback & vbLf & review.Note)
            Dim n = Await _db.ExecuteAsync(
                "INSERT INTO Results(Id, SourceType, SourceId, CorrelationId, Title, AgentId, Participants, Success, Summary, DataJson, Instructions, ReviewerAgentId, " &
                "ReviewVerdict, ReviewScore, ReviewFeedback, Revisions, FolderPath, FilesJson, ConversationJson, CostMicros, Status, CreatedBy, CreatedUtc) " &
                "VALUES (@id, @st, @sid, @c, @t, @a, @p, @ok, @sum, @d, @ins, @rv, @ver, @sc, @fb, @rev, @f, @files, @conv, @cost, 'New', @by, @now) ON CONFLICT DO NOTHING",
                ("@id", CObj(id)), ("@st", sourceType), ("@sid", sourceId), ("@c", correlationId), ("@t", TextUtil.Truncate(title, 200)), ("@a", agentId),
                ("@p", JsonUtil.ToJson(participants)), ("@ok", success), ("@sum", TextUtil.Truncate(summary, 100000)), ("@d", TextUtil.Truncate(dataJson, 200000)),
                ("@ins", TextUtil.Truncate(instructions, 20000)), ("@rv", review.ReviewerAgentId), ("@ver", review.Verdict),
                ("@sc", If(review.Score.HasValue, CObj(review.Score.Value), Nothing)), ("@fb", TextUtil.Truncate(feedback, 8000)), ("@rev", revisions),
                ("@f", folder), ("@files", JsonUtil.ToJson(files)), ("@conv", conversation), ("@cost", cost), ("@by", createdBy), ("@now", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
            If n = 0 Then Return CStr(Await _db.ScalarAsync("SELECT Id FROM Results WHERE SourceType=@t AND SourceId=@s", ("@t", CObj(sourceType)), ("@s", sourceId)).ConfigureAwait(False))
            _events.Emit(AgentEventType.ResultDelivered, $"Result ready: {title} ({review.Verdict.ToLowerInvariant()}{If(files.Count > 0, $", {files.Count} file(s)", "")})",
                         agentId, correlationId:=correlationId, level:=If(success, EventLevel.Info, EventLevel.Warning),
                         data:=New Dictionary(Of String, Object) From {{"resultId", id}, {"verdict", review.Verdict}, {"files", files.Count}})
            Return id
        End Function

        ''' <summary>Subagents belong to their parent ("Agent.sub-xyz" → "Agent").</summary>
        Private Shared Function OwnerOf(agentId As String) As String
            Dim i = If(agentId, "").IndexOf(".sub-", StringComparison.OrdinalIgnoreCase)
            Return If(i > 0, agentId.Substring(0, i), agentId)
        End Function

        Private Async Function SnapshotFilesAsync(folder As String, correlationId As String) As Task(Of List(Of ResultFile))
            Dim files As New List(Of ResultFile)
            Dim total = 0L
            If String.IsNullOrEmpty(correlationId) Then Return files
            ' 1. The workflow's deliverables folder in the shared storage.
            Dim src = Path.Combine(_sharedRoot, DeliverablesFolder(correlationId).Replace("/"c, Path.DirectorySeparatorChar))
            If Directory.Exists(src) Then
                For Each f In Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).Take(2000)
                    Dim fi As New FileInfo(f)
                    If fi.Length > MaxFileBytes OrElse total + fi.Length > MaxTotalBytes Then Continue For
                    If (fi.Attributes And FileAttributes.ReparsePoint) <> 0 Then Continue For
                    Dim rel = Path.GetRelativePath(src, f).Replace("\"c, "/"c)
                    Dim dest = Path.Combine(folder, "files", rel.Replace("/"c, Path.DirectorySeparatorChar))
                    Directory.CreateDirectory(Path.GetDirectoryName(dest))
                    File.Copy(f, dest, True)
                    total += fi.Length
                    files.Add(Describe("files/" & rel, fi.Length, "shared", Nothing))
                Next
            End If
            ' 2. Artifacts the workflow's tasks created (reports, generated images, data files).
            Dim arts = Await _db.QueryAsync("SELECT Id, Name, OwnerAgentId, SizeBytes FROM Artifacts WHERE SourceTaskId IN (SELECT Id FROM AgentTasks WHERE CorrelationId=@c) ORDER BY CreatedUtc",
                                            Function(r) (Id:=Db.Str(r, "Id"), Name:=Db.Str(r, "Name"), Owner:=Db.Str(r, "OwnerAgentId"), Size:=Db.Lng(r, "SizeBytes")),
                                            ("@c", CObj(correlationId))).ConfigureAwait(False)
            Dim used As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            For Each a In arts
                If a.Size > MaxFileBytes OrElse total + a.Size > MaxTotalBytes Then Continue For
                Try
                    Dim r = Await _artifacts.ReadAnyAsync(a.Id).ConfigureAwait(False)
                    Dim name = UploadStore.SafeFileName(a.Name)
                    Dim baseName = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name)
                    Dim k = 1
                    While Not used.Add(name)
                        k += 1 : name = $"{baseName} ({k}){ext}"
                    End While
                    Dim dest = Path.Combine(folder, "artifacts", name)
                    Directory.CreateDirectory(Path.GetDirectoryName(dest))
                    Await File.WriteAllBytesAsync(dest, r.Data).ConfigureAwait(False)
                    total += r.Data.LongLength
                    files.Add(Describe("artifacts/" & name, r.Data.LongLength, "artifact", OwnerOf(a.Owner)))
                Catch ex As ArtifactAccessException
                End Try
            Next
            Return files
        End Function

        Private Shared Function Describe(rel As String, size As Long, source As String, agentId As String) As ResultFile
            Dim name = Path.GetFileName(rel)
            Dim kind = If(FileExtractor.IsImage(name), "image", If(FileExtractor.IsTextFile(name), "text", Path.GetExtension(name).TrimStart("."c).ToLowerInvariant()))
            Return New ResultFile With {.Path = rel, .Name = name, .SizeBytes = size, .MimeType = FileExtractor.MimeTypeFor(name), .Kind = kind, .Source = source, .AgentId = agentId}
        End Function

        ''' <summary>The workflow's conversation: each task's reasoning transcript plus the messages between agents.</summary>
        Private Async Function ConversationAsync(tasks As List(Of AgentTask), correlationId As String) As Task(Of String)
            Dim threads As New List(Of Object)
            For Each t In tasks.Take(60)
                Dim transcript = Await _tasks.GetTranscriptAsync(t.Id).ConfigureAwait(False)
                Dim r = t.GetResult()
                Dim role = If(t.Delivery?.IsReview = True, "review", If(t.Delivery IsNot Nothing AndAlso t.Delivery.Revision > 0, "revision", If(t.CreatedBy?.StartsWith("global:") = True, "step", "work")))
                threads.Add(New With {
                    .taskId = t.Id, .agentId = t.AgentId, .name = t.Name, .role = role, .status = t.Status.ToString(), .summary = TextUtil.Truncate(r?.Summary, 4000),
                    .startedUtc = t.StartedUtc, .completedUtc = t.CompletedUtc, .costMicros = t.CostMicros,
                    .transcript = If(transcript Is Nothing, CType(Nothing, JsonElement?), JsonUtil.ParseElementOrNull(transcript))})
            Next
            Dim msgs = If(String.IsNullOrEmpty(correlationId), New List(Of Communication.AgentMessage),
                          Await _messages.QueryAsync(New MessageQuery With {.CorrelationId = correlationId, .Limit = 500}).ConfigureAwait(False))
            Dim messages = msgs.OrderBy(Function(m) m.CreatedUtc).Select(Function(m) New With {
                .createdUtc = m.CreatedUtc, .from = m.SenderAgentId, .to = m.RecipientAgentId, .type = m.MessageType.ToString(), .subject = m.Subject,
                .content = TextUtil.Truncate(m.Content, 8000), .status = m.Status.ToString()}).ToList()
            Return JsonUtil.ToJson(New With {.tasks = threads, .messages = messages})
        End Function

        ' ------------------------------------------------------------------ queries

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As ResultRecord
            Return New ResultRecord With {
                .Id = Db.Str(r, "Id"), .SourceType = Db.Str(r, "SourceType"), .SourceId = Db.Str(r, "SourceId"), .CorrelationId = Db.Str(r, "CorrelationId"),
                .Title = Db.Str(r, "Title"), .AgentId = Db.Str(r, "AgentId"), .Participants = JsonUtil.ParseStringList(Db.Str(r, "Participants")),
                .Success = Db.Bool(r, "Success"), .Summary = Db.Str(r, "Summary"), .DataJson = Db.Str(r, "DataJson"), .Instructions = Db.Str(r, "Instructions"),
                .ReviewerAgentId = Db.Str(r, "ReviewerAgentId"), .ReviewVerdict = Db.Str(r, "ReviewVerdict"), .ReviewScore = Db.IntN(r, "ReviewScore"),
                .ReviewFeedback = Db.Str(r, "ReviewFeedback"), .Revisions = Db.Int(r, "Revisions"),
                .Files = If(JsonUtil.FromJson(Of List(Of ResultFile))(Db.Str(r, "FilesJson")), New List(Of ResultFile)), .CostMicros = Db.Lng(r, "CostMicros"),
                .Status = Db.Str(r, "Status"), .Rating = Db.IntN(r, "Rating"), .RatingComment = Db.Str(r, "RatingComment"), .RatedBy = Db.Str(r, "RatedBy"),
                .RatedUtc = Db.DtN(r, "RatedUtc"), .AwardedMicros = Db.Lng(r, "AwardedMicros"), .CreatedBy = Db.Str(r, "CreatedBy"), .CreatedUtc = Db.Dt(r, "CreatedUtc"),
                .ConversationJson = Db.Str(r, "ConversationJson"), .FolderPath = Db.Str(r, "FolderPath")}
        End Function

        Public Function ListAsync(status As String, limit As Integer) As Task(Of List(Of ResultRecord))
            Dim where = If(String.IsNullOrEmpty(status), "WHERE Status<>'Archived'", If(status = "all", "", "WHERE Status=@s"))
            Return _db.QueryAsync($"SELECT * FROM Results {where} ORDER BY CreatedUtc DESC LIMIT @l", AddressOf Map, ("@s", CObj(status)), ("@l", Math.Clamp(limit, 1, 1000)))
        End Function

        Public Function GetAsync(id As String) As Task(Of ResultRecord)
            Return _db.QuerySingleAsync("SELECT * FROM Results WHERE Id=@id", AddressOf Map, ("@id", CObj(id)))
        End Function

        Public Function FindBySourceAsync(sourceType As String, sourceId As String) As Task(Of ResultRecord)
            Return _db.QuerySingleAsync("SELECT * FROM Results WHERE SourceType=@t AND SourceId=@s", AddressOf Map, ("@t", CObj(sourceType)), ("@s", sourceId))
        End Function

        Public Function CountNewAsync() As Task(Of Long)
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM Results WHERE Status='New'")
        End Function

        Public Function AwardsAsync(id As String) As Task(Of List(Of ResultAward))
            Return _db.QueryAsync("SELECT * FROM ResultAwards WHERE ResultId=@id ORDER BY Id", Function(r) New ResultAward With {
                .AgentId = Db.Str(r, "AgentId"), .AmountMicros = Db.Lng(r, "AmountMicros"), .Note = Db.Str(r, "Note"), .CreatedBy = Db.Str(r, "CreatedBy"),
                .CreatedUtc = Db.Dt(r, "CreatedUtc")}, ("@id", CObj(id)))
        End Function

        Public Function MarkViewedAsync(id As String) As Task(Of Integer)
            Return _db.ExecuteAsync("UPDATE Results SET Status='Viewed' WHERE Id=@id AND Status='New'", ("@id", CObj(id)))
        End Function

        Public Async Function SetArchivedAsync(id As String, archived As Boolean) As Task(Of Boolean)
            Dim n = Await _db.ExecuteAsync("UPDATE Results SET Status=CASE WHEN @a=1 THEN 'Archived' WHEN Rating IS NOT NULL THEN 'Rated' ELSE 'Viewed' END WHERE Id=@id",
                                           ("@a", CObj(If(archived, 1, 0))), ("@id", id)).ConfigureAwait(False)
            Return n > 0
        End Function

        ' ------------------------------------------------------------------ feedback and money

        Public Async Function RateAsync(id As String, rating As Integer, comment As String, actor As String) As Task(Of ResultRecord)
            If rating < 1 OrElse rating > 5 Then Throw New ArgumentException("Rating must be 1-5.")
            Dim r = Await GetAsync(id).ConfigureAwait(False)
            If r Is Nothing Then Return Nothing
            Await _db.ExecuteAsync("UPDATE Results SET Rating=@r, RatingComment=@c, RatedBy=@by, RatedUtc=@now, Status=CASE WHEN Status='Archived' THEN Status ELSE 'Rated' END WHERE Id=@id",
                                   ("@r", CObj(rating)), ("@c", TextUtil.Truncate(comment, 4000)), ("@by", actor), ("@now", Db.Ts(DateTime.UtcNow)), ("@id", id)).ConfigureAwait(False)
            ' Every agent that took part learns how the administrator judged the result.
            Dim stars = New String("★"c, rating) & New String("☆"c, 5 - rating)
            For Each a In r.Participants
                Await RememberAsync(a, $"The administrator rated the result of '{r.Title}' {rating}/5 {stars}" &
                                       If(String.IsNullOrWhiteSpace(comment), ".", ": " & TextUtil.Truncate(comment, 1000)), If(rating <= 2, 5, 4), r.SourceId).ConfigureAwait(False)
            Next
            _events.Emit(AgentEventType.AdminAction, $"{actor} rated result '{r.Title}' {rating}/5", correlationId:=r.CorrelationId)
            Return Await GetAsync(id).ConfigureAwait(False)
        End Function

        ''' <summary>Pays agents for a result; each award is a profit credit (counts toward ROI and learning). Idempotent per key.</summary>
        Public Async Function AwardAsync(id As String, awards As IEnumerable(Of (AgentId As String, Micros As Long)), note As String, actor As String, key As String) As Task(Of List(Of String))
            Dim r = Await GetAsync(id).ConfigureAwait(False)
            If r Is Nothing Then Throw New KeyNotFoundException("Result does not exist.")
            Dim done As New List(Of String)
            For Each a In awards.Where(Function(x) x.Micros > 0)
                Dim desc = $"Award for result: {r.Title}" & If(String.IsNullOrWhiteSpace(note), "", " — " & note)
                Dim lr = Await _wallets.CreditProfitAsync(a.AgentId, a.Micros, desc, r.Id, $"award:{r.Id}:{a.AgentId}:{If(key, Ids.NewId("k"))}", actor, r.SourceId).ConfigureAwait(False)
                If Not lr.Applied Then Continue For
                Await _db.ExecuteAsync("INSERT INTO ResultAwards(ResultId, AgentId, AmountMicros, LedgerEntryId, Note, CreatedBy, CreatedUtc) VALUES (@r, @a, @m, @l, @n, @by, @now)",
                                       ("@r", CObj(r.Id)), ("@a", a.AgentId), ("@m", a.Micros), ("@l", lr.EntryId), ("@n", TextUtil.Truncate(note, 1000)), ("@by", actor),
                                       ("@now", Db.Ts(DateTime.UtcNow))).ConfigureAwait(False)
                Await _db.ExecuteAsync("UPDATE Results SET AwardedMicros = AwardedMicros + @m WHERE Id=@id", ("@m", CObj(a.Micros)), ("@id", r.Id)).ConfigureAwait(False)
                Await RememberAsync(a.AgentId, $"You were awarded {TextUtil.FormatMicros(a.Micros)} for your part in '{r.Title}'" &
                                               If(String.IsNullOrWhiteSpace(note), ".", ": " & TextUtil.Truncate(note, 600)) & " Good, verified results earn money.", 4, r.SourceId).ConfigureAwait(False)
                done.Add(a.AgentId)
            Next
            Return done
        End Function

        Private Async Function RememberAsync(agentId As String, text As String, importance As Integer, sourceTaskId As String) As Task
            Try
                Await _memory.SaveAsync(agentId, MemoryCategory.Lesson, text, "admin-feedback", importance, sourceTaskId, TimeSpan.FromDays(365)).ConfigureAwait(False)
            Catch ex As Exception
                _log.Warn("Results", $"Could not record feedback for {agentId}: {ex.Message}")
            End Try
        End Function

        ' ------------------------------------------------------------------ files

        ''' <summary>Full path of a file in a result's folder, or Nothing (never outside the folder).</summary>
        Public Function ResolveFile(r As ResultRecord, relPath As String) As String
            If r Is Nothing OrElse String.IsNullOrEmpty(r.FolderPath) OrElse String.IsNullOrWhiteSpace(relPath) Then Return Nothing
            If r.Files.All(Function(f) Not String.Equals(f.Path, relPath, StringComparison.Ordinal)) Then Return Nothing
            Dim root = Path.GetFullPath(r.FolderPath).TrimEnd(Path.DirectorySeparatorChar) & Path.DirectorySeparatorChar
            Dim full = Path.GetFullPath(Path.Combine(root, relPath.Replace("/"c, Path.DirectorySeparatorChar)))
            If Not full.StartsWith(root, StringComparison.OrdinalIgnoreCase) OrElse Not File.Exists(full) Then Return Nothing
            Return full
        End Function

        ''' <summary>The answer, the review and every file as one ZIP.</summary>
        Public Function ZipBytes(r As ResultRecord) As Byte()
            Using ms As New MemoryStream()
                Using zip As New ZipArchive(ms, ZipArchiveMode.Create, True)
                    Dim summary = zip.CreateEntry("RESULT.md")
                    Using w As New StreamWriter(summary.Open())
                        w.WriteLine("# " & r.Title)
                        w.WriteLine()
                        w.WriteLine($"Status: {If(r.Success, "succeeded", "failed")} · review: {r.ReviewVerdict}{If(r.ReviewScore.HasValue, $" ({r.ReviewScore}/10)", "")} · cost {TextUtil.FormatMicros(r.CostMicros)}")
                        w.WriteLine($"Agents: {String.Join(", ", r.Participants)} · delivered {r.CreatedUtc:u}")
                        w.WriteLine()
                        w.WriteLine(r.Summary)
                        If Not String.IsNullOrWhiteSpace(r.DataJson) Then w.WriteLine() : w.WriteLine("## Data") : w.WriteLine("```json") : w.WriteLine(r.DataJson) : w.WriteLine("```")
                        If Not String.IsNullOrWhiteSpace(r.ReviewFeedback) Then w.WriteLine() : w.WriteLine("## Review") : w.WriteLine(r.ReviewFeedback)
                    End Using
                    For Each f In r.Files
                        Dim full = ResolveFile(r, f.Path)
                        If full IsNot Nothing Then zip.CreateEntryFromFile(full, f.Path, CompressionLevel.Fastest)
                    Next
                End Using
                Return ms.ToArray()
            End Using
        End Function
    End Class

End Namespace
