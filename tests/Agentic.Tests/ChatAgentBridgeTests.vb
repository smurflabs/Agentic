Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Chat and agents: work handed to an agent from a chat comes back into that chat (a hand-over note at once, the result when
''' the task ends), agents' work can be opened in a new chat to talk about it, and a report in the history does not break
''' the conversation (roles stay alternating, regenerating still works).
''' </summary>
Public Class ChatAgentBridgeTests

    Private Shared Async Function MessagesAsync(admin As (Client As HttpClient, Csrf As String), chatId As String) As Task(Of JsonArray)
        Return (Await ChatFeatureTests.GetChatAsync(admin, chatId))("messages").AsArray()
    End Function

    Private Shared Function Reports(messages As JsonArray) As List(Of JsonNode)
        Return messages.Where(Function(m) m("finishReason")?.ToString() = "agent_report").ToList()
    End Function

    <Fact>
    Public Async Function ATaskHandedOverFromAChatPostsItsResultBackIntoThatChat() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", "/api/agents/A/task",
                                                      New With {.name = "Check the logs", .description = "Look for errors.", .handler = "reasoning", .chatId = chatId}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, res.StatusCode)
                ' The hand-over is noted at once; the result follows when the task ends.
                Dim got As List(Of JsonNode) = Nothing
                Assert.True(Await TestHost.WaitUntilAsync(Async Function()
                                                              got = Reports(Await MessagesAsync(admin, chatId))
                                                              Return got.Count >= 2
                                                          End Function, 30), "the result never reached the chat")
                Assert.Contains("Handed to", got(0)("content").ToString())
                Assert.Contains("Check the logs", got(1)("content").ToString())
                Assert.Contains("finished", got(1)("content").ToString())
                Assert.Contains("(/agents/A?task=", got(1)("content").ToString())
                Assert.Equal("agent:A", got(1)("model").ToString())

                ' Asking about it next: the reports are one assistant turn in the history (roles alternate) and say who posted them.
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "what did it find?"}))
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray()
                For i = 1 To sent.Count - 1
                    Assert.False(sent(i)("role").ToString() = "assistant" AndAlso sent(i - 1)("role").ToString() = "assistant", "two assistant turns in a row")
                Next
                Assert.Contains(sent, Function(m) m("role").ToString() = "assistant" AndAlso m("content").ToString().Contains("Posted into this chat by the agent A"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function DeliveredWorkIsReportedOnceWhenItIsFinal() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", "/api/agents/A/task",
                                                      New With {.name = "Write the brief", .description = "One page.", .handler = "reasoning", .deliver = True, .chatId = chatId}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, res.StatusCode)
                Assert.True(Await TestHost.WaitUntilAsync(Async Function() Reports(Await MessagesAsync(admin, chatId)).Count >= 2, 30), "the result never reached the chat")
                ' The result went to Results too, and the chat got it once (the finished task and the final delivery are one report).
                Await Task.Delay(1500)
                Dim got = Reports(Await MessagesAsync(admin, chatId))
                Assert.Equal(2, got.Count)
                Assert.Contains("Write the brief", got(1)("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AChatThatIsNotYoursCannotReceiveResults() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", "/api/agents/A/task",
                                                      New With {.name = "x", .description = "y", .handler = "noop", .chatId = "chat_does_not_exist"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AnAgentsTaskCanBeOpenedInANewChatToTalkAboutIt() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim taskId = Await env.Host.SubmitAsync("A", "Summarize the quarter", "Write the summary.")
                Await env.Host.WaitForTaskStatusAsync("A", taskId, AgentTaskStatus.Completed)
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/discuss", New With {.agentId = "A", .taskId = taskId}, admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, res.StatusCode)
                Dim chatId = JsonNode.Parse(Await res.Content.ReadAsStringAsync())("id").ToString()
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, chatId)
                Assert.StartsWith("Discuss: Summarize the quarter", conv("title").ToString())
                Dim first = conv("messages").AsArray().Single()
                Assert.Equal("agent_report", first("finishReason").ToString())
                Assert.Contains("worked on **Summarize the quarter**", first("content").ToString())
                Assert.Contains("Write the summary.", first("content").ToString())
                ' Unknown work is a clean 404.
                Dim missing = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/discuss", New With {.agentId = "A", .taskId = "tsk_missing"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function RegeneratingAfterAnAgentsReportStillWorks() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim chatId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, chatId, New With {.content = "hello"}))
                Await env.Host.H.Chat.PostAgentReportAsync("admin", chatId, "A", "A report that arrived after the answer.")
                ' Regenerating the answer to "hello" must not stop at the report ("There is no question to answer again").
                Dim sse = Await ChatFeatureTests.AskAsync(admin, chatId, New With {.regenerate = True})
                ChatFeatureTests.Done(sse)
                Dim messages = Await MessagesAsync(admin, chatId)
                Assert.Equal("user", messages(0)("role").ToString())
                Assert.Equal("assistant", messages.Last()("role").ToString())
                Assert.NotEqual("agent_report", messages.Last()("finishReason")?.ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AGlobalTaskFromAChatPostsItsAnswerBack() As Task
        Using h = Await TestHost.StartAsync()
            Dim chat = Await h.H.Chat.CreateAsync("admin", "Planning", "fast/a", New ChatSettings())
            Dim g As New GlobalTask With {.Id = "gt_test", .Name = "Competitor report", .Status = "Completed", .FinalResult = "## Findings" & vbLf & "Three products compared.",
                                          .CreatedUtc = DateTime.UtcNow.AddMinutes(-12), .CompletedUtc = DateTime.UtcNow,
                                          .Options = New GlobalTaskOptions With {.ChatId = chat.Id, .ChatUser = "admin"}}
            g.Steps.Add(New GlobalTaskStep With {.StepId = "s1", .AgentId = "A", .Title = "Research", .Status = "Completed"})
            Await h.H.ChatReports.OnGlobalTaskFinishedAsync(g)
            Dim c = Await h.H.Chat.GetAsync("admin", chat.Id)
            Dim m = c.Messages.Single()
            Assert.True(ChatService.IsAgentReport(m))
            Assert.Contains("The global task **Competitor report** is done", m.Content)
            Assert.Contains("Three products compared.", m.Content)
            Assert.Contains("(/global-tasks/gt_test)", m.Content)
            Assert.Contains("took 12 min", m.Content)
        End Using
    End Function

    <Fact>
    Public Sub TaskNamesAreShownLiterallyInReports()
        Assert.Equal("fix \*all\* \_the\_ \[bugs\] in \`main\`", ChatReportService.MdText("fix *all* _the_ [bugs] in `main`"))
    End Sub

    <Fact>
    Public Async Function OneSubagentCanBeReadById() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await admin.Client.GetAsync("/api/subagents/A.sub-missing")
                Assert.Equal(HttpStatusCode.NotFound, res.StatusCode)
            End Using
        End Using
    End Function

End Class
