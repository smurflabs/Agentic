Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Http

    ' Admin API: RAG knowledge bases for the Chat tab. A base is built from the user's uploaded files; its documents' text
    ' is chunked and embedded, and at chat time the chunks most like the question are given to the model as cited context.
    ' All of these are per-account (a user sees only their own) and need "Use models" (the /api/chat/ prefix maps to it).
    Partial Friend NotInheritable Class AdminApi

        Private Sub RequireRag()
            If _h.Rag Is Nothing OrElse Not _h.Rag.Enabled Then Throw New ApiException(409, "RAG_DISABLED", "Knowledge bases are turned off on this server (Rag.Enabled).")
        End Sub

        Private Shared Function BaseDto(b As KnowledgeBase) As Object
            Return New With {.id = b.Id, .name = b.Name, .description = b.Description, .embeddingModel = b.EmbeddingModel,
                             .dimensions = b.Dimensions, .docs = b.Docs, .chunks = b.Chunks, .createdUtc = b.CreatedUtc, .updatedUtc = b.UpdatedUtc}
        End Function

        Private Shared Function DocDto(d As KnowledgeDoc) As Object
            Return New With {.id = d.Id, .uploadId = d.UploadId, .name = d.Name, .chunks = d.Chunks, .chars = d.Chars, .status = d.Status, .error = d.[Error], .createdUtc = d.CreatedUtc}
        End Function

        Private Async Function KnowledgeList(rc As RequestContext) As Task
            Dim bases = If(_h.Rag Is Nothing, New List(Of KnowledgeBase), Await _h.Rag.ListBasesAsync(rc.User.Name).ConfigureAwait(False))
            Await rc.OkAsync(New With {
                .enabled = _h.Rag IsNot Nothing AndAlso _h.Rag.Enabled,
                .available = _h.Rag IsNot Nothing AndAlso Await _h.Rag.AvailableAsync(rc.CancellationToken).ConfigureAwait(False),
                .embeddingModel = If(_h.Rag Is Nothing, Nothing, Await _h.Rag.ResolveModelAsync(rc.CancellationToken).ConfigureAwait(False)),
                .bases = bases.Select(AddressOf BaseDto).ToList()}).ConfigureAwait(False)
        End Function

        Private Async Function KnowledgeCreate(rc As RequestContext) As Task
            RequireRag()
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim name = JsonUtil.GetString(b, "name")
            If String.IsNullOrWhiteSpace(name) Then Throw ApiException.BadRequest("A name is required.")
            Dim created = Await _h.Rag.CreateBaseAsync(rc.User.Name, name, JsonUtil.GetString(b, "description"), rc.CancellationToken).ConfigureAwait(False)
            Await AuditAsync(rc, "chat.knowledge.create", "knowledge", created.Id, New With {created.Name}).ConfigureAwait(False)
            Await rc.WriteJsonAsync(201, BaseDto(created)).ConfigureAwait(False)
        End Function

        Private Async Function KnowledgeGet(rc As RequestContext) As Task
            Dim b = Await _h.Rag.GetBaseAsync(rc.User.Name, rc.Route("baseId")).ConfigureAwait(False)
            If b Is Nothing Then Throw ApiException.NotFound("NO_KNOWLEDGE", "That knowledge base does not exist.")
            Dim docs = Await _h.Rag.ListDocsAsync(b.Id).ConfigureAwait(False)
            Await rc.OkAsync(New With {.base = BaseDto(b), .docs = docs.Select(AddressOf DocDto).ToList()}).ConfigureAwait(False)
        End Function

        Private Async Function KnowledgeUpdate(rc As RequestContext) As Task
            RequireRag()
            Dim existing = Await _h.Rag.GetBaseAsync(rc.User.Name, rc.Route("baseId")).ConfigureAwait(False)
            If existing Is Nothing Then Throw ApiException.NotFound("NO_KNOWLEDGE", "That knowledge base does not exist.")
            Dim b = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Await _h.Rag.RenameBaseAsync(rc.User.Name, existing.Id, JsonUtil.GetString(b, "name", existing.Name), JsonUtil.GetString(b, "description", existing.Description)).ConfigureAwait(False)
            Await rc.OkAsync(BaseDto(Await _h.Rag.GetBaseAsync(rc.User.Name, existing.Id).ConfigureAwait(False))).ConfigureAwait(False)
        End Function

        Private Async Function KnowledgeDelete(rc As RequestContext) As Task
            RequireRag()
            Await _h.Rag.DeleteBaseAsync(rc.User.Name, rc.Route("baseId")).ConfigureAwait(False)
            Await AuditAsync(rc, "chat.knowledge.delete", "knowledge", rc.Route("baseId"), Nothing).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

        ''' <summary>Adds uploaded files to a base: each is chunked, embedded and indexed. Returns every document with its status.</summary>
        Private Async Function KnowledgeAddDocs(rc As RequestContext) As Task
            RequireRag()
            Dim baseId = rc.Route("baseId")
            Dim b = Await _h.Rag.GetBaseAsync(rc.User.Name, baseId).ConfigureAwait(False)
            If b Is Nothing Then Throw ApiException.NotFound("NO_KNOWLEDGE", "That knowledge base does not exist.")
            Dim body = Await rc.ReadJsonAsync().ConfigureAwait(False)
            Dim ids = JsonUtil.GetStringList(body, "uploadIds").Distinct().Take(100).ToList()
            If ids.Count = 0 Then Throw ApiException.BadRequest("No files to add (uploadIds).")
            Dim added As New List(Of Object)
            Dim failed As String = Nothing
            For Each id In ids
                Try
                    Dim doc = Await _h.Rag.AddDocumentAsync(rc.User.Name, baseId, id, rc.User.IsAdmin, rc.CancellationToken).ConfigureAwait(False)
                    added.Add(DocDto(doc))
                Catch ex As ArgumentException
                    failed = ex.Message
                End Try
            Next
            Await AuditAsync(rc, "chat.knowledge.add-docs", "knowledge", baseId, New With {.count = added.Count}).ConfigureAwait(False)
            Await rc.OkAsync(New With {.base = BaseDto(Await _h.Rag.GetBaseAsync(rc.User.Name, baseId).ConfigureAwait(False)), .docs = added, .error = failed}).ConfigureAwait(False)
        End Function

        Private Async Function KnowledgeRemoveDoc(rc As RequestContext) As Task
            RequireRag()
            Await _h.Rag.RemoveDocumentAsync(rc.User.Name, rc.Route("baseId"), rc.Route("docId")).ConfigureAwait(False)
            Await rc.OkAsync(New With {.deleted = True}).ConfigureAwait(False)
        End Function

    End Class

End Namespace
