Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Security.Cryptography
Imports System.Text
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Artifacts
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Microsoft.Data.Sqlite

Namespace Work

    Public NotInheritable Class KnowledgeBase
        Public Property Id As String
        Public Property Username As String
        Public Property Name As String
        Public Property Description As String
        Public Property EmbeddingModel As String
        Public Property Dimensions As Integer
        Public Property Docs As Integer
        Public Property Chunks As Integer
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
    End Class

    Public NotInheritable Class KnowledgeDoc
        Public Property Id As String
        Public Property BaseId As String
        Public Property UploadId As String
        Public Property Name As String
        Public Property Chunks As Integer
        Public Property Chars As Integer
        Public Property Status As String
        Public Property [Error] As String
        Public Property CreatedUtc As DateTime
    End Class

    ''' <summary>A chunk retrieved for a question: where it came from and how well it matched.</summary>
    Public NotInheritable Class RetrievedChunk
        Public Property BaseId As String
        Public Property DocId As String
        Public Property DocName As String
        Public Property UploadId As String
        Public Property Ordinal As Integer
        Public Property Text As String
        Public Property Score As Single
    End Class

    ''' <summary>
    ''' RAG knowledge bases for the Chat tab. A base is built from uploaded files: each file's extracted text is split into
    ''' overlapping chunks and embedded; at chat time the question is embedded and the most similar chunks (cosine) are
    ''' returned as cited context. Search is brute force over the base's vectors, kept in memory for speed.
    ''' </summary>
    Public NotInheritable Class RagService

        Private ReadOnly _db As Database
        Private ReadOnly _hub As ModelHub
        Private ReadOnly _uploads As UploadStore
        Private ReadOnly _embedder As Embedder
        Private ReadOnly _opt As RagOptions
        Private ReadOnly _log As ILog
        ''' <summary>Per base: the loaded chunks and a stamp (chunk count + last chunk id) to know when to reload.</summary>
        Private ReadOnly _cache As New ConcurrentDictionary(Of String, (Stamp As String, Chunks As List(Of Loaded)))(StringComparer.Ordinal)
        Private _model As String
        Private _modelCheckedUtc As DateTime

        Private NotInheritable Class Loaded
            Public DocId As String
            Public DocName As String
            Public UploadId As String
            Public Ordinal As Integer
            Public Text As String
            Public Vec As Single()
        End Class

        Private Shared ReadOnly EmbeddingModelHint As New Text.RegularExpressions.Regex("embed|bge|gte|e5|nomic|mxbai|minilm|instructor|jina|arctic", Text.RegularExpressions.RegexOptions.IgnoreCase Or Text.RegularExpressions.RegexOptions.Compiled)

        Public Sub New(db As Database, hub As ModelHub, uploads As UploadStore, options As RagOptions, log As ILog)
            _db = db
            _hub = hub
            _uploads = uploads
            _opt = If(options, New RagOptions())
            _log = log
            _embedder = New Embedder(hub, _opt)
        End Sub

        Public ReadOnly Property Options As RagOptions
            Get
                Return _opt
            End Get
        End Property

        ''' <summary>The shared embedder, reused for semantic agent memory.</summary>
        Friend ReadOnly Property VectorEmbedder As Embedder
            Get
                Return _embedder
            End Get
        End Property

        ''' <summary>On when enabled; an embedding model is needed to build or search a base (checked lazily).</summary>
        Public ReadOnly Property Enabled As Boolean
            Get
                Return _opt.Enabled
            End Get
        End Property

        ''' <summary>The embedding model to use (configured, else the first provider model that looks like one), or Nothing.</summary>
        Public Async Function ResolveModelAsync(ct As CancellationToken) As Task(Of String)
            If Not String.IsNullOrWhiteSpace(_opt.EmbeddingModel) Then Return _opt.EmbeddingModel.Trim()
            If _model IsNot Nothing AndAlso (DateTime.UtcNow - _modelCheckedUtc).TotalMinutes < 10 Then Return If(_model = "", Nothing, _model)
            Try
                Dim models = Await _hub.ListUpstreamAsync(ct).ConfigureAwait(False)
                Dim pick = models.Select(Function(m) m.Id).FirstOrDefault(Function(id) EmbeddingModelHint.IsMatch(id))
                _model = If(pick, "")
            Catch ex As Exception When TypeOf ex Is Net.Http.HttpRequestException OrElse TypeOf ex Is OperationCanceledException
                Return _model  ' keep the last answer on a provider hiccup
            End Try
            _modelCheckedUtc = DateTime.UtcNow
            Return If(_model = "", Nothing, _model)
        End Function

        ''' <summary>Whether a base can be built or searched right now (enabled and an embedding model is available).</summary>
        Public Async Function AvailableAsync(ct As CancellationToken) As Task(Of Boolean)
            If Not _opt.Enabled Then Return False
            Return (Await ResolveModelAsync(ct).ConfigureAwait(False)) IsNot Nothing
        End Function

        ' ------------------------------------------------------------------ bases

        Public Async Function CreateBaseAsync(username As String, name As String, description As String, ct As CancellationToken) As Task(Of KnowledgeBase)
            If String.IsNullOrWhiteSpace(name) Then Throw New ArgumentException("A name is required.")
            Dim model = Await ResolveModelAsync(ct).ConfigureAwait(False)
            Dim now = DateTime.UtcNow
            Dim b As New KnowledgeBase With {.Id = Ids.NewId("kb"), .Username = username, .Name = TextUtil.Truncate(name.Trim(), 120),
                                             .Description = TextUtil.Truncate(description, 1000), .EmbeddingModel = model, .CreatedUtc = now, .UpdatedUtc = now}
            Await _db.ExecuteAsync("INSERT INTO KnowledgeBases(Id, Username, Name, Description, EmbeddingModel, Dimensions, CreatedUtc, UpdatedUtc) VALUES (@id,@u,@n,@d,@m,0,@c,@c)",
                                   ("@id", CObj(b.Id)), ("@u", username), ("@n", b.Name), ("@d", CObj(b.Description)), ("@m", CObj(b.EmbeddingModel)), ("@c", Db.Ts(now))).ConfigureAwait(False)
            Return b
        End Function

        Public Function ListBasesAsync(username As String) As Task(Of List(Of KnowledgeBase))
            Return _db.QueryAsync(
                "SELECT b.*, (SELECT COUNT(*) FROM KnowledgeDocs d WHERE d.BaseId=b.Id) AS DocN, (SELECT COUNT(*) FROM KnowledgeChunks c WHERE c.BaseId=b.Id) AS ChunkN " &
                "FROM KnowledgeBases b WHERE b.Username=@u ORDER BY b.UpdatedUtc DESC",
                AddressOf MapBase, ("@u", CObj(username)))
        End Function

        ''' <summary>Every knowledge base on the server (for administrators' agents).</summary>
        Public Function ListAllBasesAsync() As Task(Of List(Of KnowledgeBase))
            Return _db.QueryAsync(
                "SELECT b.*, (SELECT COUNT(*) FROM KnowledgeDocs d WHERE d.BaseId=b.Id) AS DocN, (SELECT COUNT(*) FROM KnowledgeChunks c WHERE c.BaseId=b.Id) AS ChunkN " &
                "FROM KnowledgeBases b ORDER BY b.Username, b.UpdatedUtc DESC", AddressOf MapBase)
        End Function

        Public Function GetBaseAsync(username As String, id As String) As Task(Of KnowledgeBase)
            Return _db.QuerySingleAsync(
                "SELECT b.*, (SELECT COUNT(*) FROM KnowledgeDocs d WHERE d.BaseId=b.Id) AS DocN, (SELECT COUNT(*) FROM KnowledgeChunks c WHERE c.BaseId=b.Id) AS ChunkN " &
                "FROM KnowledgeBases b WHERE b.Id=@id AND b.Username=@u",
                AddressOf MapBase, ("@id", CObj(id)), ("@u", CObj(username)))
        End Function

        Private Shared Function MapBase(r As SqliteDataReader) As KnowledgeBase
            Return New KnowledgeBase With {
                .Id = Db.Str(r, "Id"), .Username = Db.Str(r, "Username"), .Name = Db.Str(r, "Name"), .Description = Db.Str(r, "Description"),
                .EmbeddingModel = Db.Str(r, "EmbeddingModel"), .Dimensions = CInt(Db.Lng(r, "Dimensions")),
                .Docs = CInt(Db.Lng(r, "DocN")), .Chunks = CInt(Db.Lng(r, "ChunkN")), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc")}
        End Function

        Public Async Function RenameBaseAsync(username As String, id As String, name As String, description As String) As Task
            Await _db.ExecuteAsync("UPDATE KnowledgeBases SET Name=COALESCE(@n,Name), Description=@d, UpdatedUtc=@t WHERE Id=@id AND Username=@u",
                                   ("@n", CObj(If(String.IsNullOrWhiteSpace(name), Nothing, TextUtil.Truncate(name.Trim(), 120)))),
                                   ("@d", CObj(TextUtil.Truncate(description, 1000))), ("@t", Db.Ts(DateTime.UtcNow)), ("@id", id), ("@u", username)).ConfigureAwait(False)
        End Function

        Public Async Function DeleteBaseAsync(username As String, id As String) As Task
            Dim owned = Await GetBaseAsync(username, id).ConfigureAwait(False)
            If owned Is Nothing Then Return
            ' ON DELETE CASCADE is not on by default in SQLite; remove children explicitly.
            Await _db.ExecuteAsync("DELETE FROM KnowledgeChunks WHERE BaseId=@id", ("@id", CObj(id))).ConfigureAwait(False)
            Await _db.ExecuteAsync("DELETE FROM KnowledgeDocs WHERE BaseId=@id", ("@id", CObj(id))).ConfigureAwait(False)
            Await _db.ExecuteAsync("DELETE FROM KnowledgeBases WHERE Id=@id AND Username=@u", ("@id", id), ("@u", username)).ConfigureAwait(False)
            _cache.TryRemove(id, Nothing)
        End Function

        ' ------------------------------------------------------------------ documents

        Public Function ListDocsAsync(baseId As String) As Task(Of List(Of KnowledgeDoc))
            Return _db.QueryAsync("SELECT * FROM KnowledgeDocs WHERE BaseId=@b ORDER BY CreatedUtc", AddressOf MapDoc, ("@b", CObj(baseId)))
        End Function

        Private Shared Function MapDoc(r As SqliteDataReader) As KnowledgeDoc
            Return New KnowledgeDoc With {
                .Id = Db.Str(r, "Id"), .BaseId = Db.Str(r, "BaseId"), .UploadId = Db.Str(r, "UploadId"), .Name = Db.Str(r, "Name"),
                .Chunks = CInt(Db.Lng(r, "Chunks")), .Chars = CInt(Db.Lng(r, "Chars")), .Status = Db.Str(r, "Status"), .[Error] = Db.Str(r, "Error"), .CreatedUtc = Db.Dt(r, "CreatedUtc")}
        End Function

        ''' <summary>
        ''' Adds an uploaded file to a base: its extracted text is chunked, embedded and stored. Returns the document (with
        ''' Status "ready" or "failed"). Only the owner's own uploads are read.
        ''' </summary>
        Public Async Function AddDocumentAsync(username As String, baseId As String, uploadId As String, isAdmin As Boolean, ct As CancellationToken) As Task(Of KnowledgeDoc)
            Dim b = Await GetBaseAsync(username, baseId).ConfigureAwait(False)
            If b Is Nothing Then Throw New KeyNotFoundException("No such knowledge base.")
            Dim u = Await _uploads.GetAsync(uploadId).ConfigureAwait(False)
            If u Is Nothing OrElse Not (isAdmin OrElse String.Equals(u.CreatedBy, username, StringComparison.OrdinalIgnoreCase)) Then Throw New ArgumentException("That file does not exist.")
            Dim now = DateTime.UtcNow
            Dim doc As New KnowledgeDoc With {.Id = Ids.NewId("kd"), .BaseId = baseId, .UploadId = uploadId, .Name = u.Name, .CreatedUtc = now, .Status = "ready"}
            Dim text = If(u.ExtractedText, "")
            If text.Length > _opt.MaxDocChars Then text = text.Substring(0, _opt.MaxDocChars)
            Dim chunks = Chunk(text, _opt.ChunkChars, _opt.ChunkOverlap)
            If chunks.Count = 0 Then
                doc.Status = "failed"
                doc.Error = "The file has no readable text to index."
                Await SaveDocAsync(doc, 0, 0).ConfigureAwait(False)
                Return doc
            End If
            Dim existing = Convert.ToInt32(Await _db.ScalarAsync("SELECT COUNT(*) FROM KnowledgeChunks WHERE BaseId=@b", ("@b", CObj(baseId))).ConfigureAwait(False))
            If existing + chunks.Count > _opt.MaxChunksPerBase Then Throw New ArgumentException($"This base would exceed {_opt.MaxChunksPerBase:N0} chunks; make another base or add fewer files.")
            Dim model = If(String.IsNullOrWhiteSpace(b.EmbeddingModel), Await ResolveModelAsync(ct).ConfigureAwait(False), b.EmbeddingModel)
            Dim vectors As List(Of Single()) = Nothing
            Dim embedError As String = Nothing
            Try
                vectors = Await _embedder.EmbedAsync(model, chunks, ct).ConfigureAwait(False)
            Catch ex As AiException
                embedError = TextUtil.Truncate(ex.Message, 400)
            End Try
            If embedError IsNot Nothing Then
                doc.Status = "failed"
                doc.Error = embedError
                Await SaveDocAsync(doc, chunks.Count, text.Length).ConfigureAwait(False)
                Return doc
            End If
            ' Store the document and its chunks together, so a base never holds half a file.
            Await _db.InTransactionAsync(Async Function(conn, tx)
                                             Await Database.TxExecAsync(conn, tx,
                                                 "INSERT INTO KnowledgeDocs(Id, BaseId, UploadId, Name, Sha256, Chunks, Chars, Status, Error, CreatedUtc) VALUES (@id,@b,@up,@n,@h,@ch,@cc,'ready',NULL,@t)",
                                                 ("@id", CObj(doc.Id)), ("@b", baseId), ("@up", CObj(uploadId)), ("@n", doc.Name), ("@h", CObj(u.Sha256)),
                                                 ("@ch", chunks.Count), ("@cc", text.Length), ("@t", Db.Ts(now))).ConfigureAwait(False)
                                             For k = 0 To chunks.Count - 1
                                                 Await Database.TxExecAsync(conn, tx,
                                                     "INSERT INTO KnowledgeChunks(Id, BaseId, DocId, Ordinal, Text, Embedding) VALUES (@id,@b,@d,@o,@x,@e)",
                                                     ("@id", CObj(Ids.NewId("kc"))), ("@b", baseId), ("@d", doc.Id), ("@o", k), ("@x", chunks(k)), ("@e", CObj(Embedder.ToBlob(vectors(k))))).ConfigureAwait(False)
                                             Next
                                             Await Database.TxExecAsync(conn, tx, "UPDATE KnowledgeBases SET UpdatedUtc=@t, Dimensions=@dim WHERE Id=@b",
                                                 ("@t", Db.Ts(now)), ("@dim", If(vectors.Count > 0, vectors(0).Length, 0)), ("@b", baseId)).ConfigureAwait(False)
                                             Return 0
                                         End Function).ConfigureAwait(False)
            doc.Chunks = chunks.Count
            doc.Chars = text.Length
            _cache.TryRemove(baseId, Nothing)
            Return doc
        End Function

        Private Function SaveDocAsync(doc As KnowledgeDoc, chunks As Integer, chars As Integer) As Task
            Return _db.ExecuteAsync("INSERT INTO KnowledgeDocs(Id, BaseId, UploadId, Name, Chunks, Chars, Status, Error, CreatedUtc) VALUES (@id,@b,@up,@n,@ch,@cc,@s,@e,@t)",
                                    ("@id", CObj(doc.Id)), ("@b", doc.BaseId), ("@up", CObj(doc.UploadId)), ("@n", doc.Name), ("@ch", chunks), ("@cc", chars),
                                    ("@s", doc.Status), ("@e", CObj(doc.Error)), ("@t", Db.Ts(doc.CreatedUtc)))
        End Function

        Public Async Function RemoveDocumentAsync(username As String, baseId As String, docId As String) As Task
            Dim b = Await GetBaseAsync(username, baseId).ConfigureAwait(False)
            If b Is Nothing Then Return
            Await _db.ExecuteAsync("DELETE FROM KnowledgeChunks WHERE DocId=@d AND BaseId=@b", ("@d", CObj(docId)), ("@b", CObj(baseId))).ConfigureAwait(False)
            Await _db.ExecuteAsync("DELETE FROM KnowledgeDocs WHERE Id=@d AND BaseId=@b", ("@d", CObj(docId)), ("@b", CObj(baseId))).ConfigureAwait(False)
            Await _db.ExecuteAsync("UPDATE KnowledgeBases SET UpdatedUtc=@t WHERE Id=@b", ("@t", Db.Ts(DateTime.UtcNow)), ("@b", CObj(baseId))).ConfigureAwait(False)
            _cache.TryRemove(baseId, Nothing)
        End Function

        ' ------------------------------------------------------------------ retrieval

        ''' <summary>
        ''' The chunks across the given bases most like the question, best first (cosine ≥ MinScore, at most topK). The bases
        ''' must belong to the user. Returns an empty list when nothing is relevant or RAG is unavailable.
        ''' </summary>
        Public Async Function RetrieveAsync(username As String, baseIds As IEnumerable(Of String), query As String, topK As Integer, ct As CancellationToken) As Task(Of List(Of RetrievedChunk))
            Dim empty As New List(Of RetrievedChunk)
            If Not _opt.Enabled OrElse String.IsNullOrWhiteSpace(query) OrElse baseIds Is Nothing Then Return empty
            Dim ids = baseIds.Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList()
            If ids.Count = 0 Then Return empty
            ' Only the user's own bases.
            Dim owned = (Await ListBasesAsync(username).ConfigureAwait(False)).ToDictionary(Function(b) b.Id, StringComparer.Ordinal)
            ids = ids.Where(Function(x) owned.ContainsKey(x)).ToList()
            Return Await RetrieveCoreAsync(ids, query, topK, ct).ConfigureAwait(False)
        End Function

        ''' <summary>
        ''' The same search over bases the caller already checked (an agent's Autonomy.KnowledgeBases, limited to its owner's
        ''' bases by search_knowledge).
        ''' </summary>
        Public Async Function RetrieveAnyAsync(baseIds As IEnumerable(Of String), query As String, topK As Integer, ct As CancellationToken) As Task(Of List(Of RetrievedChunk))
            If Not _opt.Enabled OrElse String.IsNullOrWhiteSpace(query) OrElse baseIds Is Nothing Then Return New List(Of RetrievedChunk)
            Dim ids = baseIds.Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList()
            Return Await RetrieveCoreAsync(ids, query, topK, ct).ConfigureAwait(False)
        End Function

        Private Async Function RetrieveCoreAsync(ids As List(Of String), query As String, topK As Integer, ct As CancellationToken) As Task(Of List(Of RetrievedChunk))
            Dim empty As New List(Of RetrievedChunk)
            If ids.Count = 0 Then Return empty
            Dim model = Await ResolveModelAsync(ct).ConfigureAwait(False)
            If model Is Nothing Then Return empty
            Dim q As Single()
            Try
                q = Await _embedder.EmbedOneAsync(model, query, ct).ConfigureAwait(False)
            Catch ex As AiException
                _log.Warn("Rag", "The question could not be embedded: " & ex.Message)
                Return empty
            End Try
            If q Is Nothing Then Return empty
            Dim k = Math.Clamp(If(topK > 0, topK, _opt.TopK), 1, 50)
            Dim scored As New List(Of RetrievedChunk)
            For Each id In ids
                Dim loaded = Await LoadAsync(id, ct).ConfigureAwait(False)
                For Each c In loaded
                    Dim s = Embedder.Dot(q, c.Vec)
                    If s < _opt.MinScore Then Continue For
                    scored.Add(New RetrievedChunk With {.BaseId = id, .DocId = c.DocId, .DocName = c.DocName, .UploadId = c.UploadId, .Ordinal = c.Ordinal, .Text = c.Text, .Score = s})
                Next
            Next
            Return scored.OrderByDescending(Function(x) x.Score).Take(k).ToList()
        End Function

        ''' <summary>The base's chunks with their vectors, from memory; reloaded when the base changed.</summary>
        Private Async Function LoadAsync(baseId As String, ct As CancellationToken) As Task(Of List(Of Loaded))
            Dim stamp = Convert.ToString(Await _db.ScalarAsync(
                "SELECT COUNT(*) || ':' || COALESCE(MAX(Id),'') FROM KnowledgeChunks WHERE BaseId=@b", ("@b", CObj(baseId))).ConfigureAwait(False))
            Dim hit As (Stamp As String, Chunks As List(Of Loaded)) = Nothing
            If _cache.TryGetValue(baseId, hit) AndAlso hit.Stamp = stamp Then Return hit.Chunks
            Dim rows = Await _db.QueryAsync(
                "SELECT c.DocId, c.Ordinal, c.Text, c.Embedding, d.Name AS DocName, d.UploadId FROM KnowledgeChunks c JOIN KnowledgeDocs d ON c.DocId=d.Id WHERE c.BaseId=@b",
                Function(r) New Loaded With {.DocId = Db.Str(r, "DocId"), .DocName = Db.Str(r, "DocName"), .UploadId = Db.Str(r, "UploadId"),
                                             .Ordinal = CInt(Db.Lng(r, "Ordinal")), .Text = Db.Str(r, "Text"), .Vec = Embedder.FromBlob(CType(r("Embedding"), Byte()))},
                ("@b", CObj(baseId))).ConfigureAwait(False)
            _cache(baseId) = (stamp, rows)
            Return rows
        End Function

        ' ------------------------------------------------------------------ chunking

        ''' <summary>
        ''' Splits text into chunks of about size characters that overlap by overlap, cutting at a paragraph, line, sentence
        ''' or word boundary near the end of each window so sentences are not split mid-word.
        ''' </summary>
        Friend Shared Function Chunk(text As String, size As Integer, overlap As Integer) As List(Of String)
            Dim result As New List(Of String)
            If String.IsNullOrWhiteSpace(text) Then Return result
            text = text.Replace(vbCr, "")
            size = Math.Max(200, size)
            overlap = Math.Clamp(overlap, 0, size \ 2)
            Dim i = 0
            While i < text.Length
                Dim [end] = Math.Min(i + size, text.Length)
                If [end] < text.Length Then
                    Dim windowStart = i + CInt(size * 0.6)
                    Dim cut = BestBoundary(text, windowStart, [end])
                    If cut > i Then [end] = cut
                End If
                Dim piece = text.Substring(i, [end] - i).Trim()
                If piece.Length > 0 Then result.Add(piece)
                If [end] >= text.Length Then Exit While
                i = Math.Max([end] - overlap, i + 1)
            End While
            Return result
        End Function

        ''' <summary>The last paragraph break, newline, sentence end or space in [from, to]; -1 when none.</summary>
        Private Shared Function BestBoundary(text As String, fromIndex As Integer, toIndex As Integer) As Integer
            fromIndex = Math.Max(0, fromIndex)
            For Each sep In {vbLf & vbLf, vbLf, ". ", "? ", "! ", "; ", " "}
                Dim at = text.LastIndexOf(sep, Math.Min(toIndex - 1, text.Length - 1), StringComparison.Ordinal)
                If at >= fromIndex Then Return at + sep.Length
            Next
            Return -1
        End Function

    End Class

End Namespace
