Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Xunit

''' <summary>
''' The 2026-10-30 chat and provider fixes: a stopped answer is saved and the next message waits for it; messages typed while
''' an answer runs can be taken back; real context windows are learned from providers; a provider out of credit is listed
''' for administrators and can be turned back on.
''' </summary>
<Collection(TimingSensitive.Name)>
Public Class ChatQueueAndLimitsTests

    ' ------------------------------------------------------------------ stop

    <Fact>
    Public Async Function AStoppedAnswerIsSavedAndTheNextMessageComesAfterIt() As Task
        Using up As New FakeUpstream()
            up.ChunkDelay = TimeSpan.FromMilliseconds(1500)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim asking = ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "tell me something long"})
                Dim deadline = DateTime.UtcNow.AddSeconds(10)
                While Not env.Host.H.Chat.Live.IsLive(chatId) AndAlso DateTime.UtcNow < deadline
                    Await Task.Delay(50)
                End While
                Await Task.Delay(600)   ' the first part ("answer ") is written
                Dim stopped = JsonNode.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/stop", Nothing, admin.Csrf)).Content.ReadAsStringAsync())
                Assert.True(stopped("stopped").GetValue(Of Boolean)())
                ' A message sent right away waits until the stopped answer is saved, so it comes after it.
                up.ChunkDelay = TimeSpan.Zero
                Dim second = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "and now?"}))
                Await asking
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, chatId)
                Dim msgs = chat("messages").AsArray()
                Assert.Equal({"user", "assistant", "user", "assistant"}, msgs.Select(Function(m) m("role").ToString()).ToArray())
                Assert.Equal("Stopped.", msgs(1)("error").ToString())
                Assert.Contains("answer", msgs(1)("content").ToString())
                Assert.Equal("and now?", msgs(2)("content").ToString())
                ' The second answer's model saw the stopped one.
                Assert.Contains("answer", ChatFeatureTests.LastStreamedRequest(up)("messages").ToJsonString())
                Assert.NotNull(second("message"))
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ one answer at a time

    <Fact>
    Public Async Function ASecondAnswerIsNotStartedWhileOneIsWritten() As Task
        Using up As New FakeUpstream()
            up.ChunkDelay = TimeSpan.FromMilliseconds(800)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim asking = ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "first"})
                Dim deadline = DateTime.UtcNow.AddSeconds(10)
                While Not env.Host.H.Chat.Live.IsLive(chatId) AndAlso DateTime.UtcNow < deadline
                    Await Task.Delay(50)
                End While
                Dim second = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/messages", New With {.content = "second"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Conflict, second.StatusCode)
                Assert.Contains("ANSWERING", Await second.Content.ReadAsStringAsync())
                ChatFeatureTests.Done(Await asking)
                Dim msgs = (Await ChatFeatureTests.GetChatAsync(admin, chatId))("messages").AsArray()
                Assert.Equal({"user", "assistant"}, msgs.Select(Function(m) m("role").ToString()).ToArray())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AGoalSetWhileItAnswersIsWorkedOnWhenItEnds() As Task
        Using up As New FakeUpstream()
            up.ChunkDelay = TimeSpan.FromMilliseconds(700)
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.GoalResumeDelaySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim asking = ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "hello"})
                Dim deadline = DateTime.UtcNow.AddSeconds(10)
                While Not env.Host.H.Chat.Live.IsLive(chatId) AndAlso DateTime.UtcNow < deadline
                    Await Task.Delay(50)
                End While
                Dim saved = Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{chatId}", New With {.settings = New With {.goal = "write the report"}}, admin.Csrf)
                Assert.True(saved.IsSuccessStatusCode, Await saved.Content.ReadAsStringAsync())
                ChatFeatureTests.Done(Await asking)
                up.ChunkDelay = TimeSpan.Zero
                ' It ends without a word about the new goal: the next answer starts by itself, for the new goal.
                deadline = DateTime.UtcNow.AddSeconds(20)
                Dim auto As JsonNode = Nothing
                While auto Is Nothing AndAlso DateTime.UtcNow < deadline
                    Await Task.Delay(300)
                    auto = (Await ChatFeatureTests.GetChatAsync(admin, chatId))("messages").AsArray().FirstOrDefault(Function(m) m("role").ToString() = "user" AndAlso m("finishReason")?.ToString() = "auto_goal")
                End While
                Assert.NotNull(auto)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ messages typed while it answers

    <Fact>
    Public Async Function AMessageTypedWhileItAnswersCanBeTakenBack() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim chatId As String = Nothing
            Dim host As TestHost = Nothing
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        Dim dropped = host.H.Chat.AddSteer(chatId, "drop me")
                                        Assert.NotNull(host.H.Chat.AddSteer(chatId, "keep me"))
                                        Assert.Equal(2, host.H.Chat.PendingSteers(chatId).Count)
                                        Assert.True(host.H.Chat.CancelSteer(chatId, dropped.Id))
                                        Assert.False(host.H.Chat.CancelSteer(chatId, dropped.Id))
                                        Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                    End If
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            host = env.Host
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "go"}))("message")
                Dim seen = ChatFeatureTests.LastStreamedRequest(up)("messages").ToJsonString()
                Assert.Contains("keep me", seen)
                Assert.DoesNotContain("drop me", seen)
                Dim note = message("toolCalls").AsArray().Single(Function(c) c("name").ToString() = "user_note")
                Assert.Contains("steer_", note("arguments").ToString())
                ' Read: nothing waits any more, and taking it back now says so.
                Dim list = JsonNode.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{chatId}/steers"))
                Assert.Empty(list("items").AsArray())
                Dim late = JsonNode.Parse(Await (Await PlatformApiTests.Send(admin.Client, "DELETE", $"/api/chat/conversations/{chatId}/steers/steer_x", Nothing, admin.Csrf)).Content.ReadAsStringAsync())
                Assert.False(late("removed").GetValue(Of Boolean)())
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ real context windows

    <Theory>
    <InlineData("This model's maximum context length is 65536 tokens. However, you requested 70000 tokens (60000 in the messages, 10000 in the completion).", 65536, 70000)>
    <InlineData("litellm.BadRequestError: AnthropicException - prompt is too long: 210345 tokens > 200000 maximum", 200000, 210345)>
    <InlineData("The input token count (1234567) exceeds the maximum number of tokens allowed (1048576).", 1048576, 1234567)>
    <InlineData("Prompt contains 140000 tokens and 0 draft tokens, too large for model with 131072 maximum context length", 131072, 140000)>
    <InlineData("request (9000 tokens) exceeds the available context size (8192 tokens), try increasing it", 8192, 9000)>
    <InlineData("Range of input length should be [1, 129024]", 129024, 0)>
    <InlineData("Your input exceeds the context window of this model. Please adjust your input and try again.", 0, 0)>
    <InlineData("max_tokens: 64000 > 32000, which is the maximum allowed number of output tokens", 0, 0)>
    Public Sub ProvidersRefusalsNameTheirWindow(text As String, limit As Integer, requested As Integer)
        Dim r = ContextLimits.ParseLimit(text)
        Assert.Equal(limit, r.Limit)
        Assert.Equal(requested, r.Requested)
    End Sub

    <Fact>
    Public Async Function WindowsAreLearnedFromAnswersAndRefusals() As Task
        Using host = Await TestHost.StartAsync()
            Dim limits As New ContextLimits(host.H.Database, New NullLog())
            ' Answered prompts only prove a lower bound: bigger than configured raises it, smaller changes nothing.
            limits.RecordAccepted("p/m", 50000, "chat")
            Assert.Equal(128000, limits.Effective("p/m", 128000))
            Assert.Equal(50000, limits.Effective("p/m", 32000))
            ' A refusal without a number: a little under that size, never under what it answered.
            limits.RecordRejected("p/m", 100000, "context length exceeded", "agents")
            Assert.Equal(90000, limits.Effective("p/m", 128000))
            ' It later answers a prompt as big as the "refused" one: that estimate was wrong and is dropped.
            limits.RecordAccepted("p/m", 100000, "chat")
            Assert.Equal(128000, limits.Effective("p/m", 128000))
            ' The provider names its window: that wins.
            Assert.Equal(100000, limits.RecordRejected("p/m", 70000, "This model's maximum context length is 65536 tokens. However, you requested 70000 tokens.", "chat"))
            Assert.Equal(100000, limits.Effective("p/m", 128000))   ' it did answer 100k: never below that
            limits.RecordRejected("p/n", 70000, "This model's maximum context length is 65536 tokens.", "chat")
            Assert.Equal(65536, limits.Effective("p/n", 128000))
            ' Kept across restarts.
            Await Task.Delay(300)
            Dim again As New ContextLimits(host.H.Database, New NullLog())
            Await again.LoadAsync()
            Assert.Equal(65536, again.Effective("p/n", 128000))
            Assert.True(Await again.ForgetAsync("p/n"))
            Assert.Equal(128000, again.Effective("p/n", 128000))
        End Using
    End Function

    <Fact>
    Public Async Function AChatRefusedAsTooLongTeachesTheModelsRealWindow() As Task
        Using up As New FakeUpstream()
            Dim refused = 0
            up.ErrorOverride = Function(req)
                                   If Not ChatFeatureTests.Streamed(req) OrElse Interlocked.Increment(refused) > 1 Then Return Nothing
                                   Return "litellm.ContextWindowExceededError: This model's maximum context length is 65536 tokens. However, you requested 70000 tokens."
                               End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim done = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "hello"}))
                Assert.Equal(65536, env.Host.H.ContextLimits.Get("fast/a").Reported)
                Assert.Equal(65536, env.Host.H.Chat.WindowFor("fast/a"))
                Assert.Equal(65536, done("context")("max").GetValue(Of Integer)())
                Dim list = JsonNode.Parse(Await admin.Client.GetStringAsync("/api/models/context-limits"))
                Assert.Contains(list("items").AsArray(), Function(x) x("modelId").ToString() = "fast/a" AndAlso x("effective").GetValue(Of Integer)() = 65536)
            End Using
        End Using
    End Function

    ' ------------------------------------------------------------------ providers out of credit

    <Fact>
    Public Async Function AProviderOutOfCreditIsListedAndCanBeTurnedBackOn() As Task
        Dim p As New FakeAIProvider()
        Dim broke = True
        p.Responder = Function(r)
                          If broke AndAlso r.ModelId.StartsWith("broke/", StringComparison.Ordinal) Then
                              Throw New AiException(AiErrorKind.ModelUnavailable, "HTTP 400: insufficient balance", 400) With {.OutOfCredit = True}
                          End If
                          Return FakeAIProvider.Text("hi from " & r.ModelId)
                      End Function
        Dim notified As New List(Of String)
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   Dim a = o.Models.First(Function(m) m.Key = "cloud-a")
                                                   a.ModelId = "broke/a"
                                                   o.Models.Add(New ModelOptions With {.Key = "cloud-a-other", .ModelId = "fine/a", .Tier = 2, .MaxConcurrency = 4,
                                                       .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000,
                                                       .Pricing = New PricingOptions With {.InputPerMillion = 1D, .OutputPerMillion = 1D}})
                                                   a.Fallbacks = New List(Of String) From {"cloud-a-other"}
                                               End Sub, p)
            host.H.Gateway.OnProviderPaused = Sub(x)
                                                  SyncLock notified
                                                      notified.Add(x.Provider)
                                                  End SyncLock
                                              End Sub
            Dim profile = host.H.Profiles.TryGetProfile("A")
            Dim ask = Function() host.H.Gateway.ChatAsync(New AiCallContext With {.AgentId = "A", .Profile = profile, .LadderLevel = 1},
                                                          New List(Of ChatMessage) From {ChatMessage.ForUser("hi")}, Nothing, CancellationToken.None)
            Assert.Equal("cloud-a-other", (Await ask()).ModelKey)
            Dim paused = host.H.Gateway.PausedProviders()
            Assert.Equal("broke", Assert.Single(paused).Provider)
            Assert.Contains("cloud-a", paused(0).Models)
            Assert.Equal({"broke"}, notified.ToArray())
            ' Balance added: the administrator turns it back on, and the next call uses it again.
            broke = False
            Assert.Equal({"cloud-a"}, host.H.Gateway.ResumeProvider("broke", "admin").ToArray())
            Assert.Empty(host.H.Gateway.PausedProviders())
            Assert.Equal(CircuitState.Closed, host.H.ModelRegistry.TryGet("cloud-a").Breaker.State)
            Assert.Equal("cloud-a", (Await ask()).ModelKey)
            Assert.Empty(host.H.Gateway.ResumeProvider("nobody"))
        End Using
    End Function

    ' ------------------------------------------------------------------ Try again after the model's connection gave out

    <Fact>
    Public Async Function TryAgainOnAFailedAnswerKeepsItsWork() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim down = False
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If down Then Return "data: {""error"":{""message"":""connection reset""}}" & vbLf & vbLf
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        ' One tool round done, then the model goes away for good.
                                        down = True
                                        Return ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                    End If
                                    Return ChatFeatureTests.TextStream("Finished the rest.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Chat.RetrySeconds = 1
                                                                o.Chat.Retries = 1 : o.Chat.RetriesWithWork = 1
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "remember I like tea, then finish"})
                Assert.Contains("gave up after 2 attempts", sse)
                Dim failed = (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray().Last()
                Assert.Equal("error", failed("finishReason").ToString())
                Dim callId = failed("toolCalls").AsArray().Single(Function(c) c("name").ToString() = "remember")("id").ToString()

                ' Try again (the Continue request): the tool round stays, the model is told to carry on, and the answer is finished in place.
                down = False
                sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.continue = True})
                Assert.Contains("""continues"":true", sse)
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray()
                Assert.Contains(sent, Function(m) m("role").ToString() = "tool" AndAlso m("tool_call_id")?.ToString() = callId)
                Assert.Contains("cut off by a connection problem", sent(sent.Count - 1)("content").ToString())
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim msgs = chat("messages").AsArray()
                Assert.Equal(2, msgs.Count)
                Dim done = msgs(1)
                Assert.Equal(failed("id").ToString(), done("id").ToString())
                Assert.Contains(done("toolCalls").AsArray(), Function(c) c("id").ToString() = callId)
                Assert.Contains("Finished the rest.", done("content").ToString())
                Assert.Null(done("error"))
                Assert.Equal("stop", done("finishReason").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function WorkInProgressIsRetriedLongerAndTheNextMessageCarriesOnWithThePlan() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim down = False, attempts = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If down Then
                                        Interlocked.Increment(attempts)
                                        Return "data: {""error"":{""message"":""connection reset""}}" & vbLf & vbLf
                                    End If
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        ' A plan, then the model's provider goes away.
                                        down = True
                                        Return ChatFeatureTests.ToolCallStream("update_todos", "{""todos"":[{""content"":""Read the code"",""status"":""completed""}," &
                                                                                               "{""content"":""Fix the parser"",""status"":""in_progress""},{""content"":""Run the tests"",""status"":""pending""}]}")
                                    End If
                                    Return ChatFeatureTests.TextStream("Carrying on with the parser.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Chat.RetrySeconds = 1
                                                                o.Chat.Retries = 1
                                                                o.Chat.RetriesWithWork = 3
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "fix the parser"})
                ' With a plan under way it tried 4 times, not 2.
                Assert.Contains("gave up after 4 attempts", sse)
                Assert.Equal(4, attempts)
                ' A new message (here not one that continues the answer itself) carries on with that plan.
                down = False
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "ok, the provider is back, go"}))
                Dim system = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray()(0)("content").ToString()
                Assert.Contains("cut off by a connection problem", system)
                Assert.Contains("[>] Fix the parser", system)
                Assert.Contains("[ ] Run the tests", system)
                Assert.Contains("do not start over", system)
            End Using
        End Using
    End Function
End Class
