Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration

Namespace AI

    ''' <summary>
    ''' Turns text into unit-normalised vectors with the provider's embedding model (OpenAI-compatible POST /embeddings),
    ''' so similarity is a dot product. Used by the RAG knowledge bases.
    ''' </summary>
    Public NotInheritable Class Embedder

        Private ReadOnly _hub As ModelHub
        Private ReadOnly _opt As RagOptions

        Public Sub New(hub As ModelHub, options As RagOptions)
            _hub = hub
            _opt = If(options, New RagOptions())
        End Sub

        ''' <summary>
        ''' Embeds each text, in batches, with the given model. Returns one unit vector per text, in order. Throws
        ''' AiException when the provider will not embed (so ingest can mark the document failed and the chat can fall back).
        ''' </summary>
        Public Async Function EmbedAsync(model As String, texts As IList(Of String), ct As CancellationToken) As Task(Of List(Of Single()))
            Dim result As New List(Of Single())
            If texts Is Nothing OrElse texts.Count = 0 Then Return result
            If String.IsNullOrWhiteSpace(model) Then Throw New AiException(AiErrorKind.CapabilityUnsupported, "No embedding model is configured for RAG.")
            Dim batch = Math.Max(1, _opt.EmbedBatch)
            Dim i = 0
            While i < texts.Count
                ct.ThrowIfCancellationRequested()
                Dim slice = texts.Skip(i).Take(batch).ToList()
                Dim input As New JsonArray()
                For Each t In slice
                    input.Add(If(String.IsNullOrEmpty(t), " ", t))
                Next
                Dim body As New JsonObject From {{"model", model}, {"input", input}, {"encoding_format", "float"}}
                Using cts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                    cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(15, _opt.TimeoutSeconds)))
                    Dim text As String
                    Try
                        Using resp = Await _hub.SendAsync("/embeddings", body.ToJsonString(), cts.Token).ConfigureAwait(False)
                            text = Await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(False)
                            If Not resp.IsSuccessStatusCode Then Throw AiErrorClassifier.FromHttp(CInt(resp.StatusCode), text)
                        End Using
                    Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                        Throw New AiException(AiErrorKind.Timeout, "Embedding timed out.", 0, ex)
                    Catch ex As Net.Http.HttpRequestException
                        Throw New AiException(AiErrorKind.Transient, "Embedding connection failed: " & ex.Message, 0, ex)
                    End Try
                    Dim vecs = ParseVectors(text)
                    If vecs.Count <> slice.Count Then Throw New AiException(AiErrorKind.InvalidResponse, $"The embedding model returned {vecs.Count} vectors for {slice.Count} inputs.")
                    result.AddRange(vecs)
                End Using
                i += batch
            End While
            Return result
        End Function

        ''' <summary>Embeds one text (a search query).</summary>
        Public Async Function EmbedOneAsync(model As String, text As String, ct As CancellationToken) As Task(Of Single())
            Dim v = Await EmbedAsync(model, {text}, ct).ConfigureAwait(False)
            Return If(v.Count > 0, v(0), Nothing)
        End Function

        ''' <summary>Reads the data[].embedding arrays in order and unit-normalises each (indices may be out of order).</summary>
        Private Shared Function ParseVectors(json As String) As List(Of Single())
            Dim root = JsonNode.Parse(json)
            Dim data = TryCast(root?("data"), JsonArray)
            If data Is Nothing Then Throw New AiException(AiErrorKind.InvalidResponse, "The embedding response has no data array.")
            Dim rows As New List(Of (Index As Integer, Vec As Single()))
            Dim n = 0
            For Each item In data
                Dim arr = TryCast(item?("embedding"), JsonArray)
                If arr Is Nothing Then Throw New AiException(AiErrorKind.InvalidResponse, "An embedding was missing.")
                Dim v(arr.Count - 1) As Single
                For k = 0 To arr.Count - 1
                    v(k) = arr(k).GetValue(Of Single)()
                Next
                Normalize(v)
                Dim idx = If(item?("index")?.GetValue(Of Integer)(), n)
                rows.Add((idx, v))
                n += 1
            Next
            Return rows.OrderBy(Function(r) r.Index).Select(Function(r) r.Vec).ToList()
        End Function

        ''' <summary>Scales a vector to unit length in place (a zero vector is left as is).</summary>
        Public Shared Sub Normalize(v As Single())
            Dim sum As Double = 0
            For Each x In v
                sum += x * x
            Next
            If sum <= 0 Then Return
            Dim inv = CSng(1.0 / Math.Sqrt(sum))
            For k = 0 To v.Length - 1
                v(k) *= inv
            Next
        End Sub

        ''' <summary>Dot product (cosine for unit vectors); 0 when the lengths differ.</summary>
        Public Shared Function Dot(a As Single(), b As Single()) As Single
            If a Is Nothing OrElse b Is Nothing OrElse a.Length <> b.Length Then Return 0
            Dim s As Single = 0
            For k = 0 To a.Length - 1
                s += a(k) * b(k)
            Next
            Return s
        End Function

        Public Shared Function ToBlob(v As Single()) As Byte()
            Dim bytes(v.Length * 4 - 1) As Byte
            Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length)
            Return bytes
        End Function

        Public Shared Function FromBlob(bytes As Byte()) As Single()
            If bytes Is Nothing OrElse bytes.Length Mod 4 <> 0 Then Return Array.Empty(Of Single)()
            Dim v(bytes.Length \ 4 - 1) As Single
            Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length)
            Return v
        End Function

    End Class

End Namespace
