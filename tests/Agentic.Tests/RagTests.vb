Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' RAG on the Chat tab: a knowledge base is built from an uploaded file, and a chat that selects it is answered with the
''' chunks most like the question as cited context. The fake upstream serves deterministic bag-of-words embeddings, so the
''' matching chunk is predictable. Knowledge sources are shown to the owner but never on a shared link.
''' </summary>
Public Class RagTests

    Private Const Elephants As String = "African elephants roam the savanna in large herds led by a matriarch. Their long trunk and ivory tusks help them forage, and seasonal migration follows the rains across the grassland for water and food."
    Private Const Quantum As String = "A quantum computer stores information in qubits that hold superposition and entanglement. Decoherence and noise limit how long a qubit keeps its state, so error correction is needed for reliable quantum computation."
    Private Const Sourdough As String = "Sourdough bread rises from a wild yeast starter. Flour and water ferment for hours, the dough is folded and proofed, then baked hot so the crust crackles and the crumb stays open and chewy."

    <Fact>
    Public Async Function KnowledgeBaseAnswersFromTheMatchingChunkAndStaysPrivateWhenShared() As Task
        Using up As New FakeUpstream()
            ' The model echoes what it was told, so the test can see the retrieved context reached the prompt.
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), ChatFeatureTests.TextStream("Here is what I found [1].", "stop"), Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Rag.ChunkChars = 300
                                                                o.Rag.ChunkOverlap = 30
                                                                o.Rag.MinScore = 0.0
                                                                o.Rag.TopK = 1
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' RAG is available (the fake provider offers an embedding model, "bge-embed").
                Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                Assert.True(meta("knowledge").GetValue(Of Boolean)())

                ' Build a base from a file with three distinct topics.
                Dim baseId = (Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/knowledge", New With {.name = "Notes"}, admin.Csrf)))("id").ToString()
                Dim text = Elephants & vbLf & vbLf & Quantum & vbLf & vbLf & Sourdough
                Dim upload = Await ImageAndVisionRoutingTests.UploadAsync(admin, "notes.txt", Encoding.UTF8.GetBytes(text))
                Dim add = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/knowledge/{baseId}/docs", New With {.uploadIds = {upload}}, admin.Csrf))
                Assert.Equal("ready", add("docs")(0)("status").ToString())
                Assert.True(add("base")("chunks").GetValue(Of Integer)() >= 3)

                ' A chat that uses the base, asked about one topic, gets that topic's chunk as cited context.
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.knowledge = {baseId}}})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Explain qubits, superposition and entanglement in a quantum computer."})
                Assert.Contains("event: sources", sse)
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)
                Dim system = sent("messages")(0)("content").ToString()
                Assert.Contains("qubits", system)                 ' the quantum chunk was retrieved
                Assert.Contains("[1]", system)
                Assert.DoesNotContain("matriarch", system)          ' the unrelated elephant chunk was not
                Dim message = ChatFeatureTests.Done(sse)("message")
                Dim sources = message("sources").AsArray()
                Assert.Single(sources)
                Assert.Equal("notes.txt", sources(0)("title").ToString())

                ' Shared read-only view: the private knowledge sources are stripped.
                Dim token = (Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/share", "{}", admin.Csrf)))("token").ToString()
                Using anon As New HttpClient With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim view = Await ChatFeatureTests.JsonOf(Await anon.GetAsync($"/api/share/{token}"))
                    Dim answer = view("messages").AsArray().Last()
                    Assert.Empty(If(answer("sources")?.AsArray(), New JsonArray()))
                End Using

                ' A chat that selects no base gets no retrieval.
                Dim plain = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(admin, plain, New With {.content = "Explain qubits and superposition."})
                Assert.DoesNotContain("qubits that hold superposition", ChatFeatureTests.LastStreamedRequest(up)("messages")(0)("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function OnlyTheOwnerSeesAndUsesTheirKnowledgeBases() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Rag.MinScore = 0.0)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim baseId = (Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/knowledge", New With {.name = "Private"}, admin.Csrf)))("id").ToString()
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "mallory", .password = "mallory-pass-123", .role = "User", .permissions = PlatformApiTests.ChatUser}, admin.Csrf)).StatusCode)
                Dim mallory = Await PlatformApiTests.LoginAsync(env.Port, "mallory", "mallory-pass-123")
                ' Mallory sees none of the admin's bases and cannot open or delete one.
                Assert.Empty((Await ChatFeatureTests.JsonOf(Await mallory.Client.GetAsync("/api/chat/knowledge")))("bases").AsArray())
                Assert.Equal(HttpStatusCode.NotFound, (Await mallory.Client.GetAsync($"/api/chat/knowledge/{baseId}")).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(mallory.Client, "POST", $"/api/chat/knowledge/{baseId}/docs", New With {.uploadIds = {"upl_x"}}, mallory.Csrf)).StatusCode)
                ' Retrieving another user's base id returns nothing (not that user's base).
                Dim hits = Await env.Host.H.Rag.RetrieveAsync("mallory", {baseId}, "anything", 5, CancellationToken.None)
                Assert.Empty(hits)
                ' The admin still has it.
                Assert.Single((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/knowledge")))("bases").AsArray())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub ChunkingOverlapsAndCutsAtBoundaries()
        Dim text = String.Join(vbLf & vbLf, Enumerable.Range(1, 20).Select(Function(n) $"Paragraph {n}. " & New String("x"c, 200)))
        Dim chunks = RagService.Chunk(text, 300, 40)
        Assert.True(chunks.Count > 3)
        Assert.All(chunks, Sub(c) Assert.True(c.Length <= 320, $"chunk too long: {c.Length}"))
        ' Consecutive chunks overlap (the tail of one appears at the head area of the next).
        Dim tail = chunks(0).Substring(Math.Max(0, chunks(0).Length - 30))
        Assert.Contains(tail.Trim().Split(" "c).Last(), chunks(1))
        Assert.Empty(RagService.Chunk("   ", 300, 40))
    End Sub
End Class
