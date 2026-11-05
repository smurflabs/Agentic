Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Memory

    Public Class MemoryItem
        Public Property Id As String
        Public Property AgentId As String
        Public Property Category As MemoryCategory
        Public Property Content As String
        Public Property Tags As String
        Public Property Importance As Integer
        Public Property SourceTaskId As String
        Public Property CreatedUtc As DateTime
        Public Property UpdatedUtc As DateTime
        Public Property ExpiresUtc As DateTime?
    End Class

    ''' <summary>
    ''' Host-level memory storage keyed by agent. Version 1 uses SQLite FTS5 keyword search; this interface lets a
    ''' vector/semantic implementation be introduced later. Never handed to agents directly (see IAgentMemoryStore).
    ''' </summary>
    Public Interface IMemoryStore
        Function SaveAsync(agentId As String, category As MemoryCategory, content As String, tags As String, importance As Integer, sourceTaskId As String, ttl As TimeSpan?) As Task(Of MemoryItem)
        Function SearchAsync(agentId As String, query As String, category As MemoryCategory?, limit As Integer) As Task(Of List(Of MemoryItem))
        Function RecentAsync(agentId As String, limit As Integer, Optional category As MemoryCategory? = Nothing) As Task(Of List(Of MemoryItem))
        Function UpdateAsync(agentId As String, id As String, content As String, tags As String, importance As Integer?) As Task(Of Boolean)
        Function DeleteAsync(agentId As String, id As String) As Task(Of Boolean)
        Function CountAsync(agentId As String) As Task(Of Long)
        Function PruneAsync(agentId As String, maxMemories As Integer) As Task(Of Integer)
        Function DeleteExpiredAsync(now As DateTime) As Task(Of Integer)
    End Interface

    ''' <summary>
    ''' The memory API an agent (and its tools) receives. It is bound to one agent at construction:
    ''' no method accepts an agent id, so an agent can never address another agent's memory.
    ''' </summary>
    Public Interface IAgentMemoryStore
        Function SaveMemoryAsync(category As MemoryCategory, content As String, Optional tags As String = Nothing, Optional importance As Integer = 3, Optional sourceTaskId As String = Nothing, Optional ttl As TimeSpan? = Nothing) As Task(Of MemoryItem)
        Function SearchMemoriesAsync(query As String, Optional category As MemoryCategory? = Nothing, Optional limit As Integer = 8) As Task(Of List(Of MemoryItem))
        Function GetRecentMemoriesAsync(Optional limit As Integer = 10, Optional category As MemoryCategory? = Nothing) As Task(Of List(Of MemoryItem))
        Function UpdateMemoryAsync(id As String, content As String, Optional tags As String = Nothing, Optional importance As Integer? = Nothing) As Task(Of Boolean)
        Function DeleteMemoryAsync(id As String) As Task(Of Boolean)
    End Interface

    Public NotInheritable Class SqliteMemoryStore
        Implements IMemoryStore

        Private ReadOnly _db As Database
        Private _embedder As Embedder
        Private _resolveModel As Func(Of CancellationToken, Task(Of String))

        Public Sub New(db As Database)
            _db = db
        End Sub

        ''' <summary>
        ''' Turns on semantic recall: new memories are embedded and stored as vectors, and search ranks by cosine similarity
        ''' (merged with keyword hits). Wired after the embedding provider exists. Without it, memory stays keyword-only.
        ''' </summary>
        Friend Sub AttachSemantic(embedder As Embedder, resolveModel As Func(Of CancellationToken, Task(Of String)))
            _embedder = embedder
            _resolveModel = resolveModel
        End Sub

        ''' <summary>Embeds the content and stores the vector on a memory row. Best-effort: a failure leaves the memory keyword-only.</summary>
        Private Async Function EmbedMemoryAsync(id As String, content As String) As Task
            If _embedder Is Nothing OrElse _resolveModel Is Nothing OrElse String.IsNullOrWhiteSpace(content) Then Return
            Try
                Dim model = Await _resolveModel(CancellationToken.None).ConfigureAwait(False)
                If String.IsNullOrWhiteSpace(model) Then Return
                Dim vec = Await _embedder.EmbedOneAsync(model, content, CancellationToken.None).ConfigureAwait(False)
                If vec Is Nothing OrElse vec.Length = 0 Then Return
                Await _db.ExecuteAsync("UPDATE AgentMemory SET Embedding=@e, EmbeddingModel=@m WHERE Id=@id",
                                       ("@e", CObj(Embedder.ToBlob(vec))), ("@m", CObj(model)), ("@id", CObj(id))).ConfigureAwait(False)
            Catch
                ' Semantic recall is an enhancement; never fail a memory write because embedding was unavailable.
            End Try
        End Function

        Private Shared Function Map(r As Microsoft.Data.Sqlite.SqliteDataReader) As MemoryItem
            Return New MemoryItem With {
                .Id = Db.Str(r, "Id"), .AgentId = Db.Str(r, "AgentId"), .Category = Db.EnumOf(r, "Category", MemoryCategory.Fact),
                .Content = Db.Str(r, "Content"), .Tags = Db.Str(r, "Tags"), .Importance = Db.Int(r, "Importance"),
                .SourceTaskId = Db.Str(r, "SourceTaskId"), .CreatedUtc = Db.Dt(r, "CreatedUtc"), .UpdatedUtc = Db.Dt(r, "UpdatedUtc"),
                .ExpiresUtc = Db.DtN(r, "ExpiresUtc")}
        End Function

        Public Async Function SaveAsync(agentId As String, category As MemoryCategory, content As String, tags As String, importance As Integer, sourceTaskId As String, ttl As TimeSpan?) As Task(Of MemoryItem) Implements IMemoryStore.SaveAsync
            If String.IsNullOrWhiteSpace(agentId) Then Throw New ArgumentException("agentId required")
            If String.IsNullOrWhiteSpace(content) Then Throw New ArgumentException("Memory content must not be empty.")
            Dim now = DateTime.UtcNow
            Dim item As New MemoryItem With {
                .Id = Ids.NewId("mem"), .AgentId = agentId, .Category = category, .Content = content, .Tags = tags,
                .Importance = Math.Clamp(importance, 1, 5), .SourceTaskId = sourceTaskId, .CreatedUtc = now, .UpdatedUtc = now,
                .ExpiresUtc = If(ttl.HasValue, now.Add(ttl.Value), CType(Nothing, DateTime?))}
            Await _db.ExecuteAsync("INSERT INTO AgentMemory(Id, AgentId, Category, Content, Tags, Importance, SourceTaskId, CreatedUtc, UpdatedUtc, ExpiresUtc) " &
                                   "VALUES (@id, @a, @c, @content, @t, @i, @s, @now, @now, @e)",
                                   ("@id", CObj(item.Id)), ("@a", agentId), ("@c", category), ("@content", content), ("@t", tags), ("@i", item.Importance),
                                   ("@s", sourceTaskId), ("@now", Db.Ts(now)), ("@e", Db.Ts(item.ExpiresUtc))).ConfigureAwait(False)
            Await EmbedMemoryAsync(item.Id, content).ConfigureAwait(False)
            Return item
        End Function

        ''' <summary>
        ''' Semantic recall: embeds the query and ranks the agent's embedded memories by cosine similarity, then merges in
        ''' keyword hits the vectors missed. Only vectors from the same embedding model are comparable. Returns Nothing when
        ''' semantic search is unavailable, so the caller can use the keyword path.
        ''' </summary>
        Private Async Function SemanticSearchAsync(agentId As String, query As String, category As MemoryCategory?, limit As Integer) As Task(Of List(Of MemoryItem))
            If _embedder Is Nothing OrElse _resolveModel Is Nothing OrElse String.IsNullOrWhiteSpace(query) Then Return Nothing
            Dim model As String
            Dim qv As Single()
            Try
                model = Await _resolveModel(CancellationToken.None).ConfigureAwait(False)
                If String.IsNullOrWhiteSpace(model) Then Return Nothing
                qv = Await _embedder.EmbedOneAsync(model, query, CancellationToken.None).ConfigureAwait(False)
            Catch
                Return Nothing
            End Try
            If qv Is Nothing OrElse qv.Length = 0 Then Return Nothing
            Dim catFilter = If(category.HasValue, " AND Category=@cat", "")
            Dim now = Db.Ts(DateTime.UtcNow)
            Dim rows = Await _db.QueryAsync(
                $"SELECT Id, Embedding FROM AgentMemory WHERE AgentId=@a AND Embedding IS NOT NULL AND EmbeddingModel=@m AND (ExpiresUtc IS NULL OR ExpiresUtc > @now){catFilter} ORDER BY CreatedUtc DESC LIMIT 1000",
                Function(r) (Id:=Db.Str(r, "Id"), Vec:=Embedder.FromBlob(CType(r("Embedding"), Byte()))),
                ("@a", CObj(agentId)), ("@m", CObj(model)), ("@now", now),
                ("@cat", If(category.HasValue, CObj(category.Value.ToString()), Nothing))).ConfigureAwait(False)
            If rows.Count = 0 Then Return Nothing
            Dim ranked = rows.Where(Function(x) x.Vec IsNot Nothing AndAlso x.Vec.Length = qv.Length).
                              Select(Function(x) (x.Id, Score:=Embedder.Dot(qv, x.Vec))).
                              OrderByDescending(Function(x) x.Score).Take(Math.Clamp(limit, 1, 100)).ToList()
            If ranked.Count = 0 Then Return Nothing
            Dim ids = ranked.Select(Function(x) x.Id).ToList()
            Dim inList = String.Join(",", ids.Select(Function(i) "'" & i.Replace("'", "") & "'"))
            Dim items = Await _db.QueryAsync($"SELECT * FROM AgentMemory WHERE AgentId=@a AND Id IN ({inList})", AddressOf Map, ("@a", CObj(agentId))).ConfigureAwait(False)
            ' Preserve cosine order.
            Dim byId = items.ToDictionary(Function(m) m.Id)
            Dim ordered = ids.Where(Function(i) byId.ContainsKey(i)).Select(Function(i) byId(i)).ToList()
            If ordered.Count > 0 Then
                Await _db.ExecuteAsync($"UPDATE AgentMemory SET LastAccessedUtc=@now WHERE AgentId=@a AND Id IN ({inList})", ("@now", CObj(now)), ("@a", agentId)).ConfigureAwait(False)
            End If
            Return ordered
        End Function

        ''' <summary>Builds a safe FTS5 query: each word quoted, OR-combined.</summary>
        Friend Shared Function BuildFtsQuery(query As String) As String
            If String.IsNullOrWhiteSpace(query) Then Return Nothing
            Dim words As New List(Of String)
            Dim sb As New StringBuilder()
            For Each c In query
                If Char.IsLetterOrDigit(c) Then
                    sb.Append(c)
                Else
                    If sb.Length > 1 Then words.Add(sb.ToString())
                    sb.Clear()
                End If
            Next
            If sb.Length > 1 Then words.Add(sb.ToString())
            If words.Count = 0 Then Return Nothing
            Return String.Join(" OR ", words.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).Select(Function(w) """" & w & """"))
        End Function

        Public Async Function SearchAsync(agentId As String, query As String, category As MemoryCategory?, limit As Integer) As Task(Of List(Of MemoryItem)) Implements IMemoryStore.SearchAsync
            Dim semantic = Await SemanticSearchAsync(agentId, query, category, limit).ConfigureAwait(False)
            Dim fts = BuildFtsQuery(query)
            Dim catFilter = If(category.HasValue, " AND m.Category=@cat", "")
            Dim now = Db.Ts(DateTime.UtcNow)
            If fts Is Nothing Then Return Await RecentAsync(agentId, limit, category).ConfigureAwait(False)
            Dim results As List(Of MemoryItem)
            Try
                results = Await _db.QueryAsync(
                    "SELECT m.* FROM AgentMemoryFts f JOIN AgentMemory m ON m.rowid = f.rowid " &
                    $"WHERE AgentMemoryFts MATCH @q AND m.AgentId=@a AND (m.ExpiresUtc IS NULL OR m.ExpiresUtc > @now){catFilter} " &
                    "ORDER BY bm25(AgentMemoryFts) - (m.Importance * 0.5) LIMIT @l",
                    AddressOf Map, ("@q", CObj(fts)), ("@a", agentId), ("@now", now), ("@cat", If(category.HasValue, CObj(category.Value.ToString()), Nothing)),
                    ("@l", Math.Clamp(limit, 1, 100))).ConfigureAwait(False)
            Catch ex As Microsoft.Data.Sqlite.SqliteException
                results = Nothing
            End Try
            If results Is Nothing Then
                ' Fallback: simple LIKE search.
                results = Await _db.QueryAsync(
                    $"SELECT m.* FROM AgentMemory m WHERE m.AgentId=@a AND m.Content LIKE @like AND (m.ExpiresUtc IS NULL OR m.ExpiresUtc > @now){catFilter} ORDER BY m.Importance DESC, m.CreatedUtc DESC LIMIT @l",
                    AddressOf Map, ("@a", CObj(agentId)), ("@like", "%" & query & "%"), ("@now", now),
                    ("@cat", If(category.HasValue, CObj(category.Value.ToString()), Nothing)), ("@l", Math.Clamp(limit, 1, 100))).ConfigureAwait(False)
            End If
            If results.Count > 0 Then
                Dim idList = String.Join(",", results.Select(Function(m) "'" & m.Id.Replace("'", "") & "'"))
                Await _db.ExecuteAsync($"UPDATE AgentMemory SET LastAccessedUtc=@now WHERE AgentId=@a AND Id IN ({idList})", ("@now", CObj(now)), ("@a", agentId)).ConfigureAwait(False)
            End If
            If semantic IsNot Nothing AndAlso semantic.Count > 0 Then
                ' Semantic hits first (ranked by meaning), then any keyword hits they missed, capped at the limit.
                Dim merged As New List(Of MemoryItem)(semantic)
                Dim seen = New HashSet(Of String)(semantic.Select(Function(m) m.Id))
                For Each m In results
                    If merged.Count >= limit Then Exit For
                    If seen.Add(m.Id) Then merged.Add(m)
                Next
                Return merged.Take(Math.Clamp(limit, 1, 100)).ToList()
            End If
            Return results
        End Function

        Public Function RecentAsync(agentId As String, limit As Integer, Optional category As MemoryCategory? = Nothing) As Task(Of List(Of MemoryItem)) Implements IMemoryStore.RecentAsync
            Dim catFilter = If(category.HasValue, " AND Category=@cat", "")
            Return _db.QueryAsync($"SELECT * FROM AgentMemory WHERE AgentId=@a AND (ExpiresUtc IS NULL OR ExpiresUtc > @now){catFilter} ORDER BY CreatedUtc DESC LIMIT @l",
                                  AddressOf Map, ("@a", CObj(agentId)), ("@now", Db.Ts(DateTime.UtcNow)),
                                  ("@cat", If(category.HasValue, CObj(category.Value.ToString()), Nothing)), ("@l", Math.Clamp(limit, 1, 500)))
        End Function

        Public Async Function UpdateAsync(agentId As String, id As String, content As String, tags As String, importance As Integer?) As Task(Of Boolean) Implements IMemoryStore.UpdateAsync
            Dim n = Await _db.ExecuteAsync("UPDATE AgentMemory SET Content=COALESCE(@c, Content), Tags=COALESCE(@t, Tags), Importance=COALESCE(@i, Importance), UpdatedUtc=@now " &
                                           "WHERE Id=@id AND AgentId=@a",
                                           ("@c", CObj(content)), ("@t", tags), ("@i", If(importance.HasValue, CObj(Math.Clamp(importance.Value, 1, 5)), Nothing)),
                                           ("@now", Db.Ts(DateTime.UtcNow)), ("@id", id), ("@a", agentId)).ConfigureAwait(False)
            Return n > 0
        End Function

        Public Async Function DeleteAsync(agentId As String, id As String) As Task(Of Boolean) Implements IMemoryStore.DeleteAsync
            Return (Await _db.ExecuteAsync("DELETE FROM AgentMemory WHERE Id=@id AND AgentId=@a", ("@id", CObj(id)), ("@a", agentId)).ConfigureAwait(False)) > 0
        End Function

        Public Function CountAsync(agentId As String) As Task(Of Long) Implements IMemoryStore.CountAsync
            Return _db.ScalarLongAsync("SELECT COUNT(*) FROM AgentMemory WHERE AgentId=@a", ("@a", CObj(agentId)))
        End Function

        ''' <summary>Keeps at most maxMemories per agent, removing the least important / oldest first.</summary>
        Public Async Function PruneAsync(agentId As String, maxMemories As Integer) As Task(Of Integer) Implements IMemoryStore.PruneAsync
            Dim count = Await CountAsync(agentId).ConfigureAwait(False)
            If count <= maxMemories Then Return 0
            Dim excess = count - maxMemories
            Return Await _db.ExecuteAsync("DELETE FROM AgentMemory WHERE Id IN (SELECT Id FROM AgentMemory WHERE AgentId=@a ORDER BY Importance ASC, COALESCE(LastAccessedUtc, CreatedUtc) ASC LIMIT @n)",
                                          ("@a", CObj(agentId)), ("@n", excess)).ConfigureAwait(False)
        End Function

        Public Function DeleteExpiredAsync(now As DateTime) As Task(Of Integer) Implements IMemoryStore.DeleteExpiredAsync
            Return _db.ExecuteAsync("DELETE FROM AgentMemory WHERE ExpiresUtc IS NOT NULL AND ExpiresUtc <= @now", ("@now", CObj(Db.Ts(now))))
        End Function
    End Class

    ''' <summary>Agent-bound facade over <see cref="IMemoryStore"/>. The agent id is fixed at construction.</summary>
    Public NotInheritable Class ScopedMemoryStore
        Implements IAgentMemoryStore

        Private ReadOnly _store As IMemoryStore
        Private ReadOnly _agentId As String
        Private ReadOnly _maxMemories As Func(Of Integer)
        Private ReadOnly _maxChars As Func(Of Integer)
        Private ReadOnly _onCreated As Action(Of MemoryItem)

        Friend Sub New(store As IMemoryStore, agentId As String, maxMemories As Func(Of Integer), maxChars As Func(Of Integer), onCreated As Action(Of MemoryItem))
            _store = store
            _agentId = agentId
            _maxMemories = maxMemories
            _maxChars = maxChars
            _onCreated = onCreated
        End Sub

        Public Async Function SaveMemoryAsync(category As MemoryCategory, content As String, Optional tags As String = Nothing, Optional importance As Integer = 3, Optional sourceTaskId As String = Nothing, Optional ttl As TimeSpan? = Nothing) As Task(Of MemoryItem) Implements IAgentMemoryStore.SaveMemoryAsync
            Dim item = Await _store.SaveAsync(_agentId, category, TextUtil.Truncate(content, _maxChars()), TextUtil.Truncate(tags, 300), importance, sourceTaskId, ttl).ConfigureAwait(False)
            Await _store.PruneAsync(_agentId, _maxMemories()).ConfigureAwait(False)
            _onCreated?.Invoke(item)
            Return item
        End Function

        Public Function SearchMemoriesAsync(query As String, Optional category As MemoryCategory? = Nothing, Optional limit As Integer = 8) As Task(Of List(Of MemoryItem)) Implements IAgentMemoryStore.SearchMemoriesAsync
            Return _store.SearchAsync(_agentId, query, category, limit)
        End Function

        Public Function GetRecentMemoriesAsync(Optional limit As Integer = 10, Optional category As MemoryCategory? = Nothing) As Task(Of List(Of MemoryItem)) Implements IAgentMemoryStore.GetRecentMemoriesAsync
            Return _store.RecentAsync(_agentId, limit, category)
        End Function

        Public Function UpdateMemoryAsync(id As String, content As String, Optional tags As String = Nothing, Optional importance As Integer? = Nothing) As Task(Of Boolean) Implements IAgentMemoryStore.UpdateMemoryAsync
            Return _store.UpdateAsync(_agentId, id, If(content Is Nothing, Nothing, TextUtil.Truncate(content, _maxChars())), tags, importance)
        End Function

        Public Function DeleteMemoryAsync(id As String) As Task(Of Boolean) Implements IAgentMemoryStore.DeleteMemoryAsync
            Return _store.DeleteAsync(_agentId, id)
        End Function
    End Class

End Namespace
