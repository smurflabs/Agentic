Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>A goal is worked on until it is met: an answer that stops early is followed by the next one, by itself.</summary>
Public Class GoalAutoResumeTests

    Private Shared Function LastUserText(req As JsonNode) As String
        Dim msgs = req("messages").AsArray()
        Return msgs.Last(Function(m) m("role").ToString() = "user")("content").ToString()
    End Function

    Private Shared Async Function WaitForAsync(check As Func(Of Task(Of Boolean)), seconds As Integer) As Task(Of Boolean)
        Dim until = DateTime.UtcNow.AddSeconds(seconds)
        While DateTime.UtcNow < until
            If Await check() Then Return True
            Await Task.Delay(250)
        End While
        Return Await check()
    End Function

    <Fact>
    Public Async Function AnAnswerThatStopsBeforeTheGoalIsMetIsContinuedByItself() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Dim all = req("messages").ToJsonString()
                                    ' After the goal is recorded met: the result.
                                    If all.Contains("Recorded: the goal is met") Then Return ChatFeatureTests.TextStream("The full result.", "stop")
                                    ' The automatic continuation: the model finishes the work.
                                    If LastUserText(req).Contains("Keep working on the goal until it is met") Then
                                        Return ChatFeatureTests.ToolCallStream("goal_complete", "{""achieved"":true,""summary"":""All done""}")
                                    End If
                                    ' The first answer: the model stops to summarize (no tool call), even after the nudges.
                                    Return ChatFeatureTests.TextStream("Here is where things stand; more to do later.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.GoalResumeDelaySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "Build the report"}})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go"}))
                ' No window asks for it: the server continues and the goal gets met.
                Dim met = Await WaitForAsync(Async Function()
                                                 Dim c = Await ChatFeatureTests.GetChatAsync(admin, id)
                                                 Return c("settings")("goal") Is Nothing AndAlso c("settings")("goalMet")?.ToString() = "Build the report"
                                             End Function, 30)
                Assert.True(met, "the goal was not met by the automatic continuation")
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim msgs = chat("messages").AsArray()
                Assert.Contains(msgs, Function(m) m("role").ToString() = "user" AndAlso m("content").ToString().Contains("automatic continuation 1 of"))
                ' The continuation is marked as the server's own (windows show it as such, not as a message from another window);
                ' the user's own message is not.
                Dim auto = msgs.First(Function(m) m("role").ToString() = "user" AndAlso m("content").ToString().Contains("automatic continuation 1 of"))
                Assert.Equal(ChatService.AutoGoalFinish, auto("finishReason")?.ToString())
                Assert.Null(msgs.First(Function(m) m("role").ToString() = "user")("finishReason"))
                Assert.Contains("The full result.", msgs.Last()("content").ToString())
                ' Met: nothing more starts.
                Await Task.Delay(2500)
                Assert.Equal(msgs.Count, (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray().Count)
            End Using
        End Using
    End Function

    <Fact>
    Public Sub AnAutomaticRunSaysSoToEveryWindow()
        Dim hub As New ChatLiveHub()
        Dim s = hub.Subscribe("ann", Nothing, Nothing)
        Dim r = hub.BeginRun("c1", "ann", "goal-resume", Nothing, "m", auto:="goal")
        Dim item As String = Nothing
        Assert.True(s.Channel.Reader.TryRead(item))
        Assert.Contains("""auto"":""goal""", item)
        Assert.Contains("""auto"":""goal""", JsonSerializer.Serialize(hub.ActiveRuns("ann")))
        hub.EndRun(r)
    End Sub

    <Fact>
    Public Async Function TwoAutomaticAnswersWithoutProgressPauseTheGoal() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return ChatFeatureTests.TextStream("Still thinking about it.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.GoalResumeDelaySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "Do it"}})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go"}))
                ' The first answer and its continuation make no progress (no tool call): it stops there, not forever.
                Await WaitForAsync(Async Function() (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray().Count >= 4, 20)
                Await Task.Delay(4000)
                Dim count = (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray().Count
                Assert.Equal(4, count)
                Assert.Equal("Do it", (Await ChatFeatureTests.GetChatAsync(admin, id))("settings")("goal").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub WaitingAndOutputToolsArePolling()
        Assert.True(ChatService.IsPollingTool("browser__browser_wait_for"))
        Assert.True(ChatService.IsPollingTool("command_output"))
        Assert.True(ChatService.IsPollingTool("BashOutput"))
        Assert.False(ChatService.IsPollingTool("run_command"))
        Assert.False(ChatService.IsPollingTool("edit_file"))
    End Sub

End Class
