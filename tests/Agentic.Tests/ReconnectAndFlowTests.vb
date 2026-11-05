Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit
Imports Agentic.AI

''' <summary>Answers survive a dropped connection (the window reconnects to them), and tool calls keep their place in the text.</summary>
Public Class ReconnectAndFlowTests

    <Fact>
    Public Async Function ASinkKeepsEventsWhileDetachedAndReplaysThemToTheNextWindow() As Task
        Dim first As New List(Of String)
        Dim broken = False
        Dim sink As New ChatSink("chat1", "u", Function(text)
                                                   If broken Then Throw New IOException("gone")
                                                   first.Add(text)
                                                   Return Task.CompletedTask
                                               End Function)
        Await sink.WriteAsync("event: delta" & vbLf & "data: {""text"":""a""}" & vbLf & vbLf)
        Assert.False(sink.Detached)
        ' The window's connection drops: the answer goes on, its events are kept.
        broken = True
        Await sink.WriteAsync("event: delta" & vbLf & "data: {""text"":""b""}" & vbLf & vbLf)
        Assert.True(sink.Detached)
        Await sink.WriteAsync("event: delta" & vbLf & "data: {""text"":""c""}" & vbLf & vbLf)
        Assert.Single(first)

        ' A window reconnects: what it missed first, then the rest live.
        Dim second As New List(Of String)
        Dim listening = Await sink.AttachAsync(Function(text)
                                                   second.Add(text)
                                                   Return Task.CompletedTask
                                               End Function)
        Assert.False(sink.Detached)
        Assert.Equal(2, second.Count)
        Assert.Contains("""b""", second(0))
        Assert.Contains("""c""", second(1))
        Await sink.WriteAsync("event: done" & vbLf & "data: {}" & vbLf & vbLf)
        Assert.Equal(3, second.Count)
        Assert.False(listening.IsCompleted)
        sink.Complete()
        Assert.True(listening.IsCompleted)

        ' A window reconnecting after the end still gets the end (a write to a dying connection may have "succeeded").
        Dim third As New List(Of String)
        Dim late = Await sink.AttachAsync(Function(text)
                                              third.Add(text)
                                              Return Task.CompletedTask
                                          End Function)
        Assert.True(late.IsCompleted)
        Assert.Single(third)
        Assert.StartsWith("event: done", third(0))
    End Function

    <Fact>
    Public Async Function AttachSaysEndedWhenThereIsNoAnswerAndReplaysAFinishedOne() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim none = Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}/attach")
                Assert.Contains("event: ended", none)

                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi"})
                ' Shortly after the answer: a reconnect gets its end.
                Dim replay = Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}/attach")
                Assert.Contains("event: done", replay)
                ' Another account cannot attach to it.
                Assert.Equal(HttpStatusCode.Created, (Await PlatformApiTests.Send(admin.Client, "POST", "/api/users", New With {.username = "other", .password = "other-pass-123", .role = "User", .permissions = {"models.use"}}, admin.Csrf)).StatusCode)
                Dim other = Await PlatformApiTests.LoginAsync(env.Port, "other", "other-pass-123")
                Assert.False((Await other.Client.GetAsync($"/api/chat/conversations/{id}/attach")).IsSuccessStatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AnEmptyReplyAfterToolsIsAskedForTheAnswer() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            ' A tool call, then an empty reply (only thinking), then the answer once asked for it.
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                        Case 2 : Return ChatFeatureTests.TextStream("", "stop")
                                        Case Else : Return ChatFeatureTests.TextStream("Here is the answer.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "check it"})
                Assert.Contains("empty reply", sse)
                Assert.Equal("Here is the answer.", ChatFeatureTests.Done(sse)("message")("content").ToString().Trim())
                Assert.Equal(3, stepNo)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub OutagesReportedAs400AreRetriedAndUnfinishedRepliesAreRecognised()
        Dim ct = CancellationToken.None
        Dim retried = Function(msg As String) ChatService.IsTransientFailure(New Net.Http.HttpRequestException(msg), ct)
        Assert.True(retried("qwen3.8-flash-MAX: 400 litellm.InternalServerError: InternalServerError: OpenAIException - Connection error.No fallback model group found"))
        Assert.True(retried("dddai2/deepseek-v4-flash: 400 litellm.ServiceUnavailableError: ServiceUnavailableError: OpenAIException - No available channel for model"))
        Assert.True(retried("yqy/glm-5.3-flash: 524 error code: 524"))
        Assert.False(retried("qwen3.8-flash-MAX: 400 Model not found"))
        Assert.False(retried("m: 400 litellm.MidStreamFallbackError: APIConnectionError: OpenAIException - Context size has been exceeded."))
        Assert.False(retried("m: 401 invalid api key"))

        Assert.Equal("empty", ChatService.DanglingReply("  ", True))
        Assert.Equal("empty", ChatService.DanglingReply("", False))
        Assert.Equal("announced", ChatService.DanglingReply("Let me check the shipped folder contents:", True))
        Assert.Null(ChatService.DanglingReply("Let me check the shipped folder contents:", False))
        Assert.Null(ChatService.DanglingReply("The fix is done.", True))
        Assert.Null(ChatService.DanglingReply("Here is the config:" & vbLf & "```" & vbLf & "a: 1" & vbLf & "b:", True))
    End Sub

    <Fact>
    Public Async Function AReplyThatAnnouncesAStepWithoutDoingItIsAskedToGoOn() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.TextStream("Now I will save what you like:", "stop")
                                        Case 2 : Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                        Case Else : Return ChatFeatureTests.TextStream("Saved.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "remember I like tea"})
                Assert.Contains("stopped before its next step", sse)
                Dim message = ChatFeatureTests.Done(sse)("message")
                Assert.Equal({"remember"}, message("toolCalls").AsArray().Select(Function(t) t("name").ToString()).ToArray())
                Assert.EndsWith("Saved.", message("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AModelServerOutOfMemoryMidAnswerIsRetriedNotShortened() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            ' The first try fails mid-answer the way llama.cpp behind LiteLLM does when its shared memory is full.
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        Return ChatFeatureTests.TextStream("Partial", "stop").Replace("data: [DONE]", "") &
                                               "data: {""error"":{""message"":""litellm.MidStreamFallbackError: APIConnectionError: OpenAIException - Context size has been exceeded.""}}" & vbLf & vbLf
                                    End If
                                    Return ChatFeatureTests.TextStream("Complete answer.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.RetrySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi"})
                Assert.Contains("Trying again", sse)
                Assert.DoesNotContain("Shortened", sse)
                Assert.Equal("Complete answer.", ChatFeatureTests.Done(sse)("message")("content").ToString().Trim())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub UnknownToolsListTheRealOnesAndCommonShellSlipsGetAHint()
        Dim text = ChatService.UnknownToolText("mcp__filesystem__read_file", {"read_file", "write_file", "run_command"})
        Assert.Contains("Unknown tool 'mcp__filesystem__read_file'", text)
        Assert.Contains("Did you mean read_file", text)
        Assert.Contains("read_file, write_file, run_command", text)
        Assert.Contains("No tools are available", ChatService.UnknownToolText("web_search", Array.Empty(Of String)()))

        Dim grep = ChatService.WithShellHint("grep : The term 'grep' is not recognized as the name of a cmdlet, function, script file, or operable program.")
        Assert.Contains("Select-String", grep)
        Assert.Contains("unbalanced", ChatService.WithShellHint("The string is missing the terminator: ""."))
        Assert.Contains("unbalanced", ChatService.WithShellHint("bash: -c: line 1: unexpected EOF while looking for matching `""'"))
        Assert.Equal("Exit code 1. Permission denied", ChatService.WithShellHint("Exit code 1. Permission denied"))
    End Sub

    <Fact>
    Public Sub ModelErrorsAreShortForPeople()
        Dim litellm = "litellm.MidStreamFallbackError: litellm.APIConnectionError: APIConnectionError: OpenAIException - The service is temporarily unavailable. Received Model Group=yqy/glm-5.3-flash Available Model Group Fallbacks=None"
        Assert.Equal("There was an issue with the upstream connection.", ModelErrors.ForUser(litellm))
        Assert.Equal("There was an issue with the upstream connection.", ModelErrors.ForUser("fast/a: Connection refused (10.0.0.5:4000)"))
        Assert.Equal("There was an issue with the upstream connection.", ModelErrors.ForUser("fast/a: 502 Bad Gateway"))
        Assert.Equal("The model took too long to answer.", ModelErrors.ForUser("yqy/glm-5.3-flash: 524 error code: 524"))
        Assert.Equal("The model took too long to answer.", ModelErrors.ForUser("timed out: no answer within 300s"))
        Assert.Equal("The model is busy right now (rate limited).", ModelErrors.ForUser("litellm.RateLimitError: RateLimitError: OpenAIException - Rate limit reached for requests", 429))
        Assert.Equal("The model's provider account is out of credit or quota.", ModelErrors.ForUser("You exceeded your current quota, please check your plan and billing details. (insufficient_quota)", 429))
        Assert.Equal("The model's provider account is out of credit or quota.", ModelErrors.ForUser("预扣费额度失败"))
        Assert.Equal("This model is not available right now.", ModelErrors.ForUser("litellm.BadRequestError: There are no healthy deployments for this model", 400))
        Assert.Equal("The conversation is too long for this model.", ModelErrors.ForUser("litellm.ContextWindowExceededError: This model's maximum context length is 131072 tokens", 400))
        Assert.Equal("The model's provider rejected the request (access).", ModelErrors.ForUser("litellm.AuthenticationError: Invalid API key provided", 401))
        Assert.Equal("The model's provider had an error.", ModelErrors.ForUser("litellm.InternalServerError: something odd", 500))
        Assert.Equal("The model's provider had an error.", ModelErrors.ForUser("All candidate models failed: fast/a: Transient HTTP 500: odd"))
        Assert.Equal("The model returned an error.", ModelErrors.ForUser(CStr(Nothing)))
        Assert.Equal("The conversation is too long for this model.", ModelErrors.ForUser("the request exceeds the available context size, try increasing it", 400))
        ' Agentic's own refusals keep their words; an AiException carrying a provider's error does not.
        Assert.Equal("Size must be one of: 1024x1024.", ModelErrors.ForUser(New AiException(AiErrorKind.InvalidRequest, "Size must be one of: 1024x1024.")))
        Assert.Equal("The model's provider could not handle the request.", ModelErrors.ForUser(New AiException(AiErrorKind.InvalidRequest, "HTTP 400: litellm.BadRequestError: bad", 400)))
        Assert.Equal("There was an issue with the upstream connection.",
                     ModelErrors.ForUser(New AiException(AiErrorKind.NoModelAvailable, "All candidate models failed: fast/a: Transient AI provider connection failed: Connection refused")))
        Assert.Equal("x cannot read images.", ModelErrors.ForUser(New ModelRefusedException("x cannot read images.")))
        Assert.False(ChatService.IsTransientFailure(New ModelRefusedException("off"), CancellationToken.None))
        Assert.True(ChatService.IsTransientFailure(New Net.Http.HttpRequestException("fast/a: 503 Service Unavailable"), CancellationToken.None))
        Assert.Equal(503, ModelErrors.StatusOf("fast/a: 503 Service Unavailable"))
        Assert.Equal(0, ModelErrors.StatusOf("maximum context length is 4096 tokens"))
        ' None of the provider's own words (exception names, links, keys) reach people.
        For Each raw In {litellm, "sk-live-abc123 at https://api.example.com/v1 returned 500"}
            Dim shown = ModelErrors.ForUser(raw)
            Assert.DoesNotContain("litellm", shown, StringComparison.OrdinalIgnoreCase)
            Assert.DoesNotContain("http", shown, StringComparison.OrdinalIgnoreCase)
            Assert.DoesNotContain("sk-", shown, StringComparison.OrdinalIgnoreCase)
        Next
    End Sub

    <Fact>
    Public Async Function ToolCallsKeepTheirPlaceInTheAnswersText() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            ' Text, then a tool; more text, then another tool; then the final words.
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.TextStream("Let me remember that.", "stop").Replace("data: [DONE]", "") & ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                        Case 2 : Return ChatFeatureTests.TextStream(" And one more.", "stop").Replace("data: [DONE]", "") & ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes cake""}")
                                        Case Else : Return ChatFeatureTests.TextStream(" All done.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "remember things"}))("message")
                Dim content = message("content").ToString()
                Dim calls = message("toolCalls").AsArray()
                Assert.Equal(2, calls.Count)
                Dim at1 = calls(0)("at").GetValue(Of Integer)(), at2 = calls(1)("at").GetValue(Of Integer)()
                Assert.Equal("Let me remember that.", content.Substring(0, at1).TrimEnd())
                Assert.True(at2 > at1)
                Assert.EndsWith("And one more.", content.Substring(0, at2).TrimEnd())
                Assert.Contains("All done.", content.Substring(at2))
            End Using
        End Using
    End Function
End Class
