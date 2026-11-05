Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>
''' When a device's tools are connected (CLI, desktop, VS Code) the model is told to carry out the task with them, not to
''' hand the user commands to run and end the turn. The audit found many answers that narrated steps and stopped (A1 and
''' FLINT: "let me know what X shows after you make this change"). A plain web chat with no tools keeps the shorter prompt.
''' </summary>
Public Class ActWithToolsTests

    Private Const BashTool As String = "{""name"":""Bash"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"

    Private Shared Function SystemOf(up As FakeUpstream) As String
        Return ChatFeatureTests.LastStreamedRequest(up)("messages")(0)("content").ToString()
    End Function

    <Fact>
    Public Async Function WithDeviceToolsTheModelIsToldToActNotNarrate() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), ChatFeatureTests.TextStream("Done.", "stop"), Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' With the device's tools attached.
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "set up docker", .model = "fast/a",
                    .settings = New With {.permissionMode = "auto"}, .clientTools = {JsonNode.Parse(BashTool)}}))
                Dim withTools = SystemOf(up)
                Assert.Contains("Tools on the user's computer are connected", withTools)
                Assert.Contains("do not describe steps or hand the user", withTools)
                Assert.Contains("BatchMode=yes", withTools)

                ' A later message with no tools (a plain web chat) does not get the note.
                Dim web = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, web, New With {.content = "hello", .model = "fast/a"}))
                Assert.DoesNotContain("Tools on the user's computer are connected", SystemOf(up))
            End Using
        End Using
    End Function

End Class
