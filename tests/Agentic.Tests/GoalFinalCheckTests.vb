Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' A goal is not taken as met on the model's first word: it first does a final check against the goal as the user wrote it,
''' verifying each part with its tools, and confirms what it had left "not confirmed".
''' </summary>
Public Class GoalFinalCheckTests

    Private Shared Function LastToolText(req As JsonNode) As String
        Return If(req("messages").AsArray().LastOrDefault(Function(m) m("role")?.ToString() = "tool")?("content")?.ToString(), "")
    End Function

    <Fact>
    Public Async Function AGoalReportedMetIsCheckedAgainstTheGoalFirst() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            Dim notes As New List(Of String)
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("goal_complete", "{""achieved"":true,""summary"":""Built the parser. Not confirmed: the tests pass on Windows.""}")
                                        Case 2
                                            SyncLock notes : notes.Add(LastToolText(req)) : End SyncLock
                                            ' Still one part it could not check: held once more.
                                            Return ChatFeatureTests.ToolCallStream("goal_complete", "{""achieved"":true,""summary"":""Ran the tests: 42 passed. The Linux build is unverified.""}")
                                        Case 3
                                            SyncLock notes : notes.Add(LastToolText(req)) : End SyncLock
                                            Return ChatFeatureTests.ToolCallStream("goal_complete", "{""achieved"":true,""summary"":""Ran the tests on Windows (42 passed) and built on Linux (ok).""}")
                                        Case Else : Return ChatFeatureTests.TextStream("The parser is done; tests pass on both.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .settings = New With {.goal = "Write a CSV parser with passing tests"}})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go"})
                Assert.Contains("""status"":""met""", sse)
                Assert.Equal(4, stepNo)
                ' The first hold: a final check against the goal as written, with what it left unconfirmed.
                Assert.Contains("Write a CSV parser with passing tests", notes(0))
                Assert.Contains("final check", notes(0))
                Assert.Contains("the tests pass on Windows", notes(0))
                ' The second: what is still unverified.
                Assert.Contains("The Linux build is unverified", notes(1))
                Dim chat = await ChatFeatureTests.GetChatAsync(admin, id)
                Assert.Equal("Write a CSV parser with passing tests", chat("settings")("goalMet").ToString())
                Dim goalCalls = chat("messages")(1)("toolCalls").AsArray().Where(Function(c) c("name").ToString() = "goal_complete").ToList()
                Assert.Equal(3, goalCalls.Count)
                Assert.False(goalCalls(0)("ok").GetValue(Of Boolean)())
                Assert.True(goalCalls(2)("ok").GetValue(Of Boolean)())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub UnconfirmedPartsAreFound()
        Assert.Equal("Not confirmed: the tests pass.", ChatService.UnconfirmedParts("Built it. Not confirmed: the tests pass."))
        Assert.Contains("could not verify", ChatService.UnconfirmedParts("Everything works. I could not verify the email step; it should work."))
        Assert.Contains("untested", ChatService.UnconfirmedParts("Done." & vbLf & "- export: untested"))
        Assert.Null(ChatService.UnconfirmedParts("Ran the tests (42 passed) and opened the page: it shows the new table."))
        Assert.Null(ChatService.UnconfirmedParts(Nothing))
    End Sub

End Class
