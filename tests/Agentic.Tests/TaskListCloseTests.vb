Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Threading
Imports Xunit

''' <summary>
''' A model that finishes its work and answers without marking the last steps of its task list (the list stayed at "5 of 8")
''' is asked once to bring the list up to date, so the last list the user sees is how things really stand.
''' </summary>
Public Class TaskListCloseTests

    Private Shared Function Todos(ParamArray steps As (Content As String, Status As String)()) As String
        Return New JsonObject From {{"todos", New JsonArray(steps.Select(Function(s) CType(New JsonObject From {{"content", s.Content}, {"status", s.Status}}, JsonNode)).ToArray())}}.ToJsonString()
    End Function

    Private Shared Function LastUser(req As JsonNode) As String
        Return If(req("messages").AsArray().LastOrDefault(Function(m) m("role")?.ToString() = "user")?("content")?.ToString(), "")
    End Function

    <Fact>
    Public Async Function AnAnswerThatLeavesStepsOpenIsAskedOnceToUpdateTheList() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("update_todos", Todos(("Read the file", "completed"), ("Fix the bug", "in_progress"), ("Run the tests", "pending")))
                                        Case 2 : Return ChatFeatureTests.TextStream("Fixed it and the tests pass.", "stop")
                                        Case 3
                                            ' The app's note names the open steps.
                                            Assert.Contains("task list still shows 2 steps not completed", LastUser(req))
                                            Assert.Contains("""Fix the bug""", LastUser(req))
                                            Return ChatFeatureTests.ToolCallStream("update_todos", Todos(("Read the file", "completed"), ("Fix the bug", "completed"), ("Run the tests", "completed")))
                                        Case Else : Return ChatFeatureTests.TextStream("", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "fix the bug"}))("message")
                Dim lists = message("toolCalls").AsArray().Where(Function(c) c("name").ToString() = "update_todos").ToList()
                Assert.Equal(2, lists.Count)
                ' The last list shown is the finished one.
                Assert.Contains("3 of 3 done", lists.Last()("result").ToString())
                ' The answer is not repeated.
                Assert.Equal("Fixed it and the tests pass.", message("content").ToString().Trim())
                Assert.Equal(4, stepNo)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AFinishedListOrOneLeftOpenAfterTheNoteEndsTheAnswer() As Task
        ' Already complete: no extra round.
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("update_todos", Todos(("Look", "completed"), ("Report", "completed")))
                                        Case Else : Return ChatFeatureTests.TextStream("Done.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go"}))
                Assert.Equal(2, stepNo)
            End Using
        End Using
        ' Left open on purpose after the note (a step could not be done): asked only once, the answer ends.
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(stepNo)
                                        Case 1 : Return ChatFeatureTests.ToolCallStream("update_todos", Todos(("Deploy", "pending")))
                                        Case 2 : Return ChatFeatureTests.TextStream("I could not deploy: no access.", "stop")
                                        Case Else : Return ChatFeatureTests.TextStream("Deploy stays open: no access to the server.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "deploy it"}))("message")
                Assert.Equal(3, stepNo)
                Assert.Contains("Deploy stays open", message("content").ToString())
            End Using
        End Using
    End Function

End Class
