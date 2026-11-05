Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Persistence
Imports Agentic.Security
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Chat features known from ChatGPT and Claude: versions of edited questions and regenerated answers, projects, memory and
''' personal instructions, temporary chats, share links, export, feedback, saved prompts, continuing a cut-off answer,
''' images made by the model and dictation.
''' </summary>
Public Class ChatFeatureTests

    ' ------------------------------------------------------------------ helpers

    Friend Shared Function Chunk(delta As JsonObject, Optional finish As String = Nothing) As String
        Dim choice As New JsonObject From {{"index", 0}, {"delta", delta}}
        If finish IsNot Nothing Then choice("finish_reason") = finish
        Return "data: " & New JsonObject From {{"choices", New JsonArray(choice)}}.ToJsonString() & vbLf & vbLf
    End Function

    ''' <summary>A streamed answer with this text and finish reason.</summary>
    Friend Shared Function TextStream(text As String, finish As String) As String
        Return Chunk(New JsonObject From {{"content", text}}) & Chunk(New JsonObject(), finish) & "data: [DONE]" & vbLf & vbLf
    End Function

    ''' <summary>A streamed answer that calls one tool.</summary>
    Friend Shared Function ToolCallStream(name As String, args As String) As String
        Dim call1 As New JsonObject From {{"index", 0}, {"id", "call_" & name}, {"type", "function"}, {"function", New JsonObject From {{"name", name}, {"arguments", args}}}}
        Return Chunk(New JsonObject From {{"tool_calls", New JsonArray(call1)}}) & Chunk(New JsonObject(), "tool_calls") & "data: [DONE]" & vbLf & vbLf
    End Function

    ''' <summary>The answer's own streamed request (a subagent's request streams too, and is told apart by its system prompt).</summary>
    Friend Shared Function Streamed(req As JsonNode) As Boolean
        Return req("stream")?.ToString() = "true" AndAlso Not IsSubagent(req)
    End Function

    ''' <summary>A subagent's model request: its system prompt says so.</summary>
    Friend Shared Function IsSubagent(req As JsonNode) As Boolean
        Dim first = TryCast(req("messages"), JsonArray)?.FirstOrDefault()
        Return first IsNot Nothing AndAlso first("role")?.ToString() = "system" AndAlso If(first("content")?.ToString(), "").StartsWith("You are a subagent", StringComparison.Ordinal)
    End Function

    Friend Shared Async Function JsonOf(res As HttpResponseMessage) As Task(Of JsonNode)
        Dim text = Await res.Content.ReadAsStringAsync()
        Assert.True(res.IsSuccessStatusCode, text)
        Return JsonNode.Parse(text)
    End Function

    Friend Shared Async Function NewChatAsync(user As (Client As HttpClient, Csrf As String), body As Object) As Task(Of String)
        Return (Await JsonOf(Await PlatformApiTests.Send(user.Client, "POST", "/api/chat/conversations", body, user.Csrf)))("id").ToString()
    End Function

    Friend Shared Async Function AskAsync(user As (Client As HttpClient, Csrf As String), chatId As String, body As Object) As Task(Of String)
        Return Await (Await PlatformApiTests.Send(user.Client, "POST", $"/api/chat/conversations/{chatId}/messages", body, user.Csrf)).Content.ReadAsStringAsync()
    End Function

    Friend Shared Async Function GetChatAsync(user As (Client As HttpClient, Csrf As String), chatId As String) As Task(Of JsonNode)
        Return JsonNode.Parse(Await user.Client.GetStringAsync($"/api/chat/conversations/{chatId}"))
    End Function

    ''' <summary>The data of the answer's "done" event.</summary>
    Friend Shared Function Done(sse As String) As JsonNode
        Dim at = sse.IndexOf("event: done" & vbLf, StringComparison.Ordinal)
        Assert.True(at >= 0, sse)
        Dim line = sse.Substring(at).Split(vbLf)(1)
        Return JsonNode.Parse(line.Substring("data: ".Length))
    End Function

    Friend Shared Function LastStreamedRequest(up As FakeUpstream) As JsonNode
        Return up.Requests.ToArray().Last(AddressOf Streamed)
    End Function

    Friend Shared Function ToolNames(req As JsonNode) As String()
        Dim tools = TryCast(req("tools"), JsonArray)
        If tools Is Nothing Then Return Array.Empty(Of String)()
        Return tools.Select(Function(t) t("function")("name").ToString()).ToArray()
    End Function

    ' ------------------------------------------------------------------ versions

    <Fact>
    Public Async Function EditingAndRegeneratingKeepEarlierVersions() As Task
        Using up As New FakeUpstream()
            Dim n = 0
            up.StreamOverride = Function(req) If(Streamed(req), TextStream("answer " & Interlocked.Increment(n), "stop"), Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskAsync(admin, id, New With {.content = "first question"})

                ' Regenerating keeps the first answer as a version of that point.
                Dim regen = Await AskAsync(admin, id, New With {.regenerate = True})
                Assert.Contains("event: truncate", regen)
                Assert.Equal(1, Done(regen)("versions")(0)("anchorIndex").GetValue(Of Integer)())
                Dim chat = Await GetChatAsync(admin, id)
                Assert.Equal(2, chat("messages").AsArray().Count)
                Assert.Equal("answer 2", chat("messages")(1)("content").ToString())
                Dim point = chat("versions")(0)
                Assert.Equal(1, point("anchorIndex").GetValue(Of Integer)())
                Assert.Equal(2, point("total").GetValue(Of Integer)())
                Assert.Equal(2, point("position").GetValue(Of Integer)())
                Assert.Equal("answer 1", point("versions")(0)("preview").ToString())

                ' Editing the question keeps the original question with its answers (and their versions) as a version.
                Dim firstId = chat("messages")(0)("id").ToString()
                Await AskAsync(admin, id, New With {.content = "edited question", .editMessageId = firstId})
                chat = Await GetChatAsync(admin, id)
                Assert.Equal({"edited question", "answer 3"}, chat("messages").AsArray().Select(Function(m) m("content").ToString()).ToArray())
                Assert.Single(chat("versions").AsArray())
                Assert.Equal(0, chat("versions")(0)("anchorIndex").GetValue(Of Integer)())

                ' Going back brings the original question, its last answer and that answer's earlier version.
                Dim older = chat("versions")(0)("versions")(0)("branchId").ToString()
                chat = Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/versions", New With {.branchId = older}, admin.Csrf))
                Assert.Equal({"first question", "answer 2"}, chat("messages").AsArray().Select(Function(m) m("content").ToString()).ToArray())
                Assert.Equal({0, 1}, chat("versions").AsArray().Select(Function(v) v("anchorIndex").GetValue(Of Integer)()).ToArray())
                Dim answerVersion = chat("versions").AsArray().First(Function(v) v("anchorIndex").GetValue(Of Integer)() = 1)("versions")(0)("branchId").ToString()
                chat = Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/versions", New With {.branchId = answerVersion}, admin.Csrf))
                Assert.Equal("answer 1", chat("messages")(1)("content").ToString())
                Assert.Equal(HttpStatusCode.NotFound, (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/versions", New With {.branchId = answerVersion}, admin.Csrf)).StatusCode)

                ' The model is never shown earlier versions: only the conversation as it is now.
                Await AskAsync(admin, id, New With {.content = "next"})
                Dim sent = LastStreamedRequest(up)("messages").AsArray().Select(Function(m) m("content")?.ToString()).ToList()
                Assert.Contains("answer 1", sent)
                Assert.DoesNotContain("answer 2", sent)
                Assert.DoesNotContain("edited question", sent)

                ' Rewinding to the first question drops the versions from there on.
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/rewind", New With {.messageId = chat("messages")(0)("id").ToString()}, admin.Csrf))
                Assert.Empty((Await GetChatAsync(admin, id))("versions").AsArray())
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ projects, personalization, memory, temporary chats

    <Fact>
    Public Async Function ProjectsPersonalInstructionsAndMemoryReachTheModel() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim up1 As New HttpRequestMessage(HttpMethod.Post, "/api/uploads?name=notes.txt") With {.Content = New ByteArrayContent(Encoding.UTF8.GetBytes("The greenhouse is 12 metres long."))}
                up1.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim upload = Await JsonOf(Await admin.Client.SendAsync(up1))
                Dim proj = Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/projects",
                                                                    New With {.name = "Garden", .instructions = "Always mention tomatoes.", .files = {upload("id").ToString()}}, admin.Csrf))
                Dim projectId = proj("id").ToString()
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/personalization", New With {.aboutMe = "I live in Oslo.", .howToRespond = "Answer in haiku."}, admin.Csrf))
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/memories", New With {.content = "Has two cats"}, admin.Csrf))
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/memories", New With {.content = "my api_key=abcdef0123456789xyz"}, admin.Csrf)).StatusCode)

                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .projectId = projectId})
                Assert.Contains("event: done", Await AskAsync(admin, id, New With {.content = "hi"}))
                Dim req = LastStreamedRequest(up)
                Dim system = req("messages")(0)("content").ToString()
                For Each expected In {"Always mention tomatoes.", "The greenhouse is 12 metres long.", "I live in Oslo.", "Answer in haiku.", "Has two cats", "Garden"}
                    Assert.Contains(expected, system)
                Next
                Assert.Contains("remember", ToolNames(req))
                Assert.Contains("forget", ToolNames(req))

                ' The project lists its chat; the list can show one project's chats.
                Assert.Equal(1, (Await JsonOf(Await admin.Client.GetAsync("/api/chat/projects"))).AsArray()(0)("chats").GetValue(Of Integer)())
                Dim inProject = (Await JsonOf(Await admin.Client.GetAsync($"/api/chat/conversations?projectId={projectId}"))).AsArray()
                Assert.Equal(id, Assert.Single(inProject)("id").ToString())

                ' With memory off the model neither sees nor saves memories.
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/personalization", New With {.memory = False}, admin.Csrf))
                Await AskAsync(admin, id, New With {.content = "again"})
                Assert.DoesNotContain("Has two cats", LastStreamedRequest(up)("messages")(0)("content").ToString())
                Assert.DoesNotContain("remember", ToolNames(LastStreamedRequest(up)))
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", "/api/chat/personalization", New With {.memory = True}, admin.Csrf))

                ' A temporary chat: personal instructions yes, memory no, and never in the list.
                Dim tmp = Await NewChatAsync(admin, New With {.model = "fast/a", .temporary = True})
                Await AskAsync(admin, tmp, New With {.content = "hello"})
                system = LastStreamedRequest(up)("messages")(0)("content").ToString()
                Assert.Contains("I live in Oslo.", system)
                Assert.DoesNotContain("Has two cats", system)
                Assert.DoesNotContain("remember", ToolNames(LastStreamedRequest(up)))
                Assert.DoesNotContain(tmp, Await admin.Client.GetStringAsync("/api/chat/conversations"))
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{tmp}/share", "{}", admin.Csrf)).StatusCode)
                ' It is deleted a day after its last message.
                Await env.Host.H.Database.ExecuteAsync("UPDATE Chats SET UpdatedUtc=@t WHERE Id=@id", ("@t", CObj(Db.Ts(DateTime.UtcNow.AddDays(-2)))), ("@id", tmp))
                Assert.Equal(1, Await env.Host.H.Chat.PurgeTemporaryAsync())
                Assert.Equal(HttpStatusCode.NotFound, (Await admin.Client.GetAsync($"/api/chat/conversations/{tmp}")).StatusCode)

                ' Deleting the project keeps its chats.
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "DELETE", $"/api/chat/projects/{projectId}", Nothing, admin.Csrf))
                Assert.Null((Await GetChatAsync(admin, id))("projectId"))
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ tools: memory and images; files; share links

    <Fact>
    Public Async Function TheModelSavesMemoriesAndMakesImagesThatCanBeShared() As Task
        Using up As New FakeUpstream(), media As New FakeMediaApi()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ToolCallStream("remember", "{""fact"":""Likes green tea""}")
                                        Case 2 : Return ToolCallStream("generate_image", "{""prompt"":""a teapot""}")
                                        Case Else : Return TextStream("Here is your teapot.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) MediaGenerationTests.UseMediaApi(o, media))
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await JsonOf(Await admin.Client.GetAsync("/api/chat/meta")))("imageGeneration").GetValue(Of Boolean)())
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.imageGeneration = True}})
                Dim sse = Await AskAsync(admin, id, New With {.content = "remember that I like green tea, then draw a teapot"})
                Assert.Contains("event: memory", sse)
                Assert.Contains("event: image", sse)
                Dim message = Done(sse)("message")
                Assert.Equal("Here is your teapot.", message("content").ToString())
                Assert.Equal({"remember", "generate_image"}, message("toolCalls").AsArray().Select(Function(t) t("name").ToString()).ToArray())
                Dim image = Assert.Single(message("attachments").AsArray())
                Assert.Equal("image", image("kind").ToString())
                Dim uploadId = image("uploadId").ToString()
                Dim memories = (Await JsonOf(Await admin.Client.GetAsync("/api/chat/personalization")))("memories").AsArray()
                Assert.Contains(memories, Function(m) m("content").ToString() = "Likes green tea" AndAlso m("source").ToString() = "chat")

                ' The image is shown to its owner, and to no other account.
                Dim file = Await admin.Client.GetAsync($"/api/chat/files/{uploadId}")
                Assert.Equal(HttpStatusCode.OK, file.StatusCode)
                Assert.Equal("image/png", file.Content.Headers.ContentType.MediaType)
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "other", .password = "other-pass-123", .role = "User", .permissions = {"models.use"}}, admin.Csrf)).StatusCode)
                Dim other = Await PlatformApiTests.LoginAsync(env.Port, "other", "other-pass-123")
                Assert.Equal(HttpStatusCode.NotFound, (Await other.Client.GetAsync($"/api/chat/files/{uploadId}")).StatusCode)
                Assert.Equal(HttpStatusCode.NotFound, (Await other.Client.GetAsync($"/api/chat/conversations/{id}")).StatusCode)

                ' A share link: anyone can read the chat and see its images; nothing else, and nothing after it is withdrawn.
                Dim token = (Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/share", "{}", admin.Csrf)))("token").ToString()
                Assert.True(token.Length >= 32)
                Assert.True((Await JsonOf(Await admin.Client.GetAsync("/api/chat/conversations"))).AsArray().First(Function(c) c("id").ToString() = id)("isShared").GetValue(Of Boolean)())
                Using anon As New HttpClient() With {.BaseAddress = New Uri($"http://127.0.0.1:{env.Port}")}
                    Dim shared1 = Await JsonOf(Await anon.GetAsync($"/api/share/{token}"))
                    Assert.Equal(2, shared1("messages").AsArray().Count)
                    Assert.Null(shared1("messages")(1)("reasoning"))
                    Assert.Equal(uploadId, shared1("messages")(1)("attachments")(0)("uploadId").ToString())
                    Assert.Equal(HttpStatusCode.OK, (Await anon.GetAsync($"/api/share/{token}/files/{uploadId}")).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/api/share/{token}/files/upl_0000")).StatusCode)
                    Assert.Equal(HttpStatusCode.Unauthorized, (Await anon.GetAsync($"/api/chat/files/{uploadId}")).StatusCode)
                    Dim page = Await anon.GetAsync($"/share/{token}")
                    Assert.Equal(HttpStatusCode.OK, page.StatusCode)
                    Assert.Contains("share.js", Await page.Content.ReadAsStringAsync())
                    Await JsonOf(Await PlatformApiTests.Send(admin.Client, "DELETE", $"/api/chat/conversations/{id}/share", Nothing, admin.Csrf))
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/api/share/{token}")).StatusCode)
                    Assert.Equal(HttpStatusCode.NotFound, (Await anon.GetAsync($"/api/share/{token}/files/{uploadId}")).StatusCode)
                End Using
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ continue, feedback, export, saved prompts, dictation

    <Fact>
    Public Async Function ACutOffAnswerIsContinuedInPlace() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(stepNo) = 1, TextStream("1 One" & vbLf & "2 Two" & vbLf, "length"), TextStream("3 Three", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.MaxLengthContinues = 0)   ' the Continue button (not the automatic continuation)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a"})
                Dim first = Done(Await AskAsync(admin, id, New With {.content = "count to three"}))("message")
                Assert.Equal("length", first("finishReason").ToString())
                Dim sse = Await AskAsync(admin, id, New With {.continue = True})
                Assert.Contains("""continues"":true", sse)
                Assert.DoesNotContain("event: user", sse)
                Dim chat = Await GetChatAsync(admin, id)
                Assert.Equal(2, chat("messages").AsArray().Count)
                Assert.Equal(first("id").ToString(), chat("messages")(1)("id").ToString())
                Assert.Equal("1 One" & vbLf & "2 Two" & vbLf & "3 Three", chat("messages")(1)("content").ToString())
                Assert.Equal("stop", chat("messages")(1)("finishReason").ToString())
                ' The model saw what it had written and was asked to go on.
                Dim sent = LastStreamedRequest(up)("messages").AsArray()
                Assert.Equal("1 One" & vbLf & "2 Two", sent(sent.Count - 2)("content").ToString())
                Assert.Contains("Continue your previous answer", sent(sent.Count - 1)("content").ToString())
                Assert.Contains("event: error", Await AskAsync(admin, Await NewChatAsync(admin, New With {.model = "fast/a"}), New With {.continue = True}))

                ' Feedback on the answer, and the chat as Markdown.
                Dim answerId = first("id").ToString()
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages/{answerId}/feedback", New With {.value = -1, .note = "too short"}, admin.Csrf))
                chat = Await GetChatAsync(admin, id)
                Assert.Equal(-1, chat("messages")(1)("feedback").GetValue(Of Integer)())
                Assert.Equal("too short", chat("messages")(1)("feedbackNote").ToString())
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages/{answerId}/feedback", New With {.value = 0}, admin.Csrf))
                Assert.Null((Await GetChatAsync(admin, id))("messages")(1)("feedback"))
                Dim md = Await admin.Client.GetAsync($"/api/chat/conversations/{id}/export?format=md")
                Assert.Contains("attachment", md.Content.Headers.ContentDisposition.ToString())
                Dim mdText = Await md.Content.ReadAsStringAsync()
                Assert.Contains("## You", mdText)
                Assert.Contains("3 Three", mdText)
                Assert.Equal(id, JsonNode.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}/export?format=json"))("id").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SavedPromptsAndDictation() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim p = Await JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/prompts", New With {.command = "/Summarize", .title = "Summary", .content = "Summarize in 3 bullets:"}, admin.Csrf))
                Assert.Equal("summarize", p("command").ToString())
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/prompts", New With {.command = "summarize", .content = "again"}, admin.Csrf)).StatusCode)
                Assert.Equal(HttpStatusCode.BadRequest, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/prompts", New With {.command = "two words", .content = "x"}, admin.Csrf)).StatusCode)
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "PUT", $"/api/chat/prompts/{p("id")}", New With {.command = "sum", .content = "Summarize briefly:"}, admin.Csrf))
                Dim list = (Await JsonOf(Await admin.Client.GetAsync("/api/chat/prompts"))).AsArray()
                Assert.Equal("sum", Assert.Single(list)("command").ToString())
                Await JsonOf(Await PlatformApiTests.Send(admin.Client, "DELETE", $"/api/chat/prompts/{p("id")}", Nothing, admin.Csrf))
                Assert.Empty((Await JsonOf(Await admin.Client.GetAsync("/api/chat/prompts"))).AsArray())

                ' Dictation: recorded speech goes to the provider's speech-to-text and comes back as text.
                Dim audio As New ByteArrayContent(Enumerable.Repeat(CByte(7), 4000).ToArray())
                audio.Headers.ContentType = New Headers.MediaTypeHeaderValue("audio/webm")
                Dim req As New HttpRequestMessage(HttpMethod.Post, "/api/chat/transcribe?name=speech.webm") With {.Content = audio}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim text = Await JsonOf(Await admin.Client.SendAsync(req))
                Assert.Equal("hello from speech", text("text").ToString())
                Assert.True(up.TranscribedBytes > 4000)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ units

    <Fact>
    Public Sub ContinuationsJoinOnTheRightLine()
        Assert.Equal("1 One" & vbLf & "2 Two", ChatService.JoinContinuation("1 One", "2 Two"))
        Assert.Equal("- a" & vbLf & "- b", ChatService.JoinContinuation("- a", "- b"))
        Assert.Equal("Twenty-one", ChatService.JoinContinuation("Twenty-", "one"))
        Assert.Equal("The quick brown fox", ChatService.JoinContinuation("The quick bro", "wn fox"))
        Assert.Equal("Hello world", ChatService.JoinContinuation("Hello", " world"))
        Assert.Equal("```js" & vbLf & "a();" & vbLf & "b();", ChatService.JoinContinuation("```js" & vbLf & "a();", "b();"))
        Assert.Equal("Done." & vbLf & vbLf & "## Next", ChatService.JoinContinuation("Done.", "## Next"))
    End Sub

    <Fact>
    Public Sub SecretsAreNotKeptAsMemories()
        Assert.True(SecretRedactor.LooksLikeSecret("my key is sk-abcdefghijklmnop1234"))
        Assert.True(SecretRedactor.LooksLikeSecret("password: hunter2hunter2"))
        Assert.True(SecretRedactor.LooksLikeSecret("use token 9f8e7d6c5b4a39281706f5e4d3c2b1a0ff"))
        Assert.False(SecretRedactor.LooksLikeSecret("Prefers metric units and lives in Oslo"))
        Assert.False(SecretRedactor.LooksLikeSecret("Works on internationalization and accessibility"))
    End Sub

    <Fact>
    Public Sub ConversationsExportAsMarkdown()
        Dim c As New ChatConversation With {.Title = "Trip", .Model = "m", .CreatedUtc = New DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc),
                                            .Messages = New List(Of ChatMessageRecord) From {
                                                New ChatMessageRecord With {.Role = "user", .Content = "Where to?"},
                                                New ChatMessageRecord With {.Role = "assistant", .Model = "m", .Content = "Oslo [1]",
                                                                            .Sources = New List(Of ChatSource) From {New ChatSource With {.Index = 1, .Title = "Visit", .Url = "https://example.org"}}}}}
        Dim md = ChatService.ToMarkdown(c)
        Assert.StartsWith("# Trip", md)
        Assert.Contains("## You" & vbLf & vbLf & "Where to?", md)
        Assert.Contains("[1] Visit — https://example.org", md)
    End Sub

    ' ------------------------------------------------------------------ goals (/goal) and retries

    <Fact>
    Public Async Function GoalKeepsWorkingThroughFailuresAndEarlyStopsUntilMet() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(calls)
                                        Case 1 : Return "data: {""error"":{""message"":""upstream overloaded""}}" & vbLf & vbLf  ' the model fails: retried
                                        Case 2 : Return TextStream("Working on it.", "stop")                                        ' stops early: told to go on
                                        Case 3 : Return ToolCallStream("goal_complete", "{""achieved"":true,""summary"":""checked""}")
                                        ' Held for a final check against the goal: it checks and reports again.
                                        Case 4 : Return ToolCallStream("goal_complete", "{""achieved"":true,""summary"":""Report finished; re-read every section against the goal.""}")
                                        Case Else : Return TextStream("All done.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.GoalRetrySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "Finish the report"}})
                Dim sse = Await AskAsync(admin, id, New With {.content = "go"})
                Assert.Contains("Trying again", sse)
                Assert.Contains("Goal not met yet", sse)
                Assert.Contains("Checking that the goal is really met", sse)
                Assert.Contains("""status"":""met""", sse)
                Assert.Equal(5, calls)
                ' The goal tool is offered, the model is told about the goal, and it is told to keep going after stopping early.
                Dim sent = LastStreamedRequest(up)
                Assert.Contains(sent("tools").AsArray(), Function(t) t("function")("name").ToString() = "goal_complete")
                Assert.Contains("Finish the report", sent("messages")(0)("content").ToString())
                Assert.Contains(sent("messages").AsArray(), Function(m) m("role").ToString() = "user" AndAlso If(m("content")?.ToString(), "").Contains("goal is not reported met"))
                Dim chat = Await GetChatAsync(admin, id)
                Assert.Null(chat("settings")("goal"))
                Assert.Equal("Finish the report", chat("settings")("goalMet").ToString())
                Dim answer = chat("messages")(1)("content").ToString()
                Assert.Contains("Working on it.", answer)
                Assert.Contains("All done.", answer)
                ' A window that missed the end still sends the old goal: it stays finished.
                Await AskAsync(admin, id, New With {.content = "thanks", .settings = New With {.goal = "Finish the report"}})
                Assert.Null((Await GetChatAsync(admin, id))("settings")("goal"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AUserStopClearsTheGoalWithoutNagging() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Interlocked.Increment(calls)
                                    Return TextStream("Understood — holding here.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "Keep REAP and A1 serving"}})
                Dim sse = Await AskAsync(admin, id, New With {.content = "stop", .settings = New With {.goal = "Keep REAP and A1 serving"}})
                ' The goal is cleared, the model is not nudged to keep going, and it only answered once.
                Assert.Contains("""status"":""stopped""", sse)
                Assert.DoesNotContain("goal is not reported met", sse)
                Assert.DoesNotContain("Goal not met yet", sse)
                Assert.Equal(1, calls)
                Assert.Null((Await GetChatAsync(admin, id))("settings")("goal"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function RepeatedHoldingPausesTheGoalInsteadOfNaggingForever() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            ' The model never calls a tool and never reports the goal met: it just keeps writing text.
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Return TextStream("Still holding. " & Interlocked.Increment(calls), "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.GoalMaxStalls = 2)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "Make it perfect"}})
                Dim sse = Await AskAsync(admin, id, New With {.content = "go", .settings = New With {.goal = "Make it perfect"}})
                ' Nudged at most GoalMaxStalls (2) times, not up to GoalMaxContinues (8): 1 first answer + 2 nudges = 3 calls.
                Assert.Equal(3, calls)
                Assert.Contains("""status"":""paused""", sse)
                ' The goal is not met, so it stays set for the user to act on.
                Assert.Equal("Make it perfect", (Await GetChatAsync(admin, id))("settings")("goal").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AGoalReportedImpossibleIsPushedBackOnceThenAccepted() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(calls)
                                        Case 1, 2 : Return ToolCallStream("goal_complete", "{""achieved"":false,""summary"":""I have no live access""}")
                                        Case Else : Return TextStream("Sorry, it cannot be done.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "List today's news", .webSearch = True}})
                Dim sse = Await AskAsync(admin, id, New With {.content = "go"})
                ' The first "impossible" is not accepted: the model is told what it can do instead (news search, read_page).
                Assert.Contains("Not giving up yet", sse)
                Dim second = LastStreamedRequest(up)("messages").AsArray().Where(Function(m) m("role").ToString() = "tool").Select(Function(m) m("content").ToString()).ToList()
                Assert.Contains(second, Function(c) c.Contains("Not accepted yet"))
                ' The second is: the goal stays (it was not met) and the answer says it failed.
                Assert.Contains("""status"":""failed""", sse)
                Assert.Equal("List today's news", (Await GetChatAsync(admin, id))("settings")("goal").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SettingsChangedWhileAnAnswerIsWrittenAreKept() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Threading.Thread.Sleep(1500)   ' a slow answer
                                    Return TextStream("slow answer", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.reasoningEffort = "low", .webSearch = False}})
                Dim answering = AskAsync(admin, id, New With {.content = "hi", .settings = New With {.reasoningEffort = "low", .webSearch = False}})
                Await Task.Delay(500)
                ' Another window changes the chat's settings while the answer is being written.
                Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{id}", New With {.settings = New With {.reasoningEffort = "high", .webSearch = True}}, admin.Csrf)
                Await answering
                Dim chat = Await GetChatAsync(admin, id)
                Assert.Equal("high", chat("settings")("reasoningEffort").ToString())
                Assert.True(chat("settings")("webSearch").GetValue(Of Boolean)())
                Assert.Equal("slow answer", chat("messages")(1)("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ModelFailuresAreRetriedThenReportedWithoutAGoal() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            Dim failFirst = True
            up.StreamOverride = Function(req)
                                    If Not Streamed(req) Then Return Nothing
                                    Dim k = Interlocked.Increment(calls)
                                    If failFirst AndAlso k = 1 Then Return "data: {""error"":{""message"":""connection reset""}}" & vbLf & vbLf
                                    If Not failFirst Then Return "data: {""error"":{""message"":""still down""}}" & vbLf & vbLf
                                    Return TextStream("Recovered answer", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.RetrySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await AskAsync(admin, id, New With {.content = "hi"})
                Assert.Equal("Recovered answer", Done(sse)("message")("content").ToString())
                ' While it waits, people see that it is retrying, not the provider's error.
                Assert.Contains("There was an issue with the upstream connection. Retrying in", sse)
                Assert.Contains("attempt 2 of 6", sse)
                Assert.DoesNotContain("connection reset", sse)
                ' Still failing after the retry: the answer ends with the error, never silently.
                failFirst = False : calls = 0
                sse = Await AskAsync(admin, id, New With {.content = "again"})
                Assert.Contains("event: error", sse)
                Assert.Contains("gave up after 6 attempts", sse)
                Assert.DoesNotContain("still down", sse)
                Assert.Equal(6, calls)
            End Using
        End Using
    End Function
End Class
