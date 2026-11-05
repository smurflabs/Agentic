Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>/btw (a side question answered aside, not saved in the chat) and /research (a message researched in depth).</summary>
Public Class ChatAsideTests

    ''' <summary>Everything a model request says (its messages' text).</summary>
    Private Shared Function TextOf(req As JsonNode) As String
        Dim msgs = TryCast(req("messages"), JsonArray)
        If msgs Is Nothing Then Return ""
        Return String.Join(vbLf, msgs.Select(Function(m) If(m?("content")?.ToString(), "")))
    End Function

    <Fact>
    Public Async Function SideQuestionIsAnsweredAsideAndNotSaved() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), ChatFeatureTests.TextStream("The build uses dotnet 10.", "stop"), Nothing)
            up.TextOverride = Function(req) If(TextOf(req).Contains("Side question:"), "It uses .NET 10.", Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "What does the build use?"})
                Dim r = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/aside",
                                                                                   New With {.question = "Which .NET again?"}, admin.Csrf))
                Assert.Equal("It uses .NET 10.", r("answer").ToString())
                Assert.Equal("fast/a", r("model").ToString())
                ' The model saw the conversation and the side question, and got no tools.
                Dim req = up.Requests.ToArray().Last(Function(q) TextOf(q).Contains("Side question:"))
                Assert.Contains("What does the build use?", TextOf(req))
                Assert.Contains("The build uses dotnet 10.", TextOf(req))
                Assert.Contains("Which .NET again?", TextOf(req))
                Assert.Null(req("tools"))
                ' Nothing was added to the chat.
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                Assert.Equal(2, chat("messages").AsArray().Count)
                ' No question: refused.
                Dim empty = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/aside", New With {.question = " "}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode)
                ' The model down: a short error, not the provider's text.
                up.Down.Add("fast/a")
                Dim failed = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/aside", New With {.question = "Still there?"}, admin.Csrf)
                Dim body = Await failed.Content.ReadAsStringAsync()
                Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode)
                Assert.Contains("This model is not available right now.", body)
                Assert.DoesNotContain("No available channel", body)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ResearchMessagesGetResearchInstructions() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req) If(ChatFeatureTests.Streamed(req), ChatFeatureTests.TextStream("Report.", "stop"), Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Compare the two frameworks", .research = True})
                Dim system = ChatFeatureTests.LastStreamedRequest(up)("messages")(0)("content").ToString()
                Assert.Contains("DEEP RESEARCH", system)
                ' Only that message: the next one is a normal answer.
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Thanks"})
                Assert.DoesNotContain("DEEP RESEARCH", ChatFeatureTests.LastStreamedRequest(up)("messages")(0)("content").ToString())
            End Using
        End Using
    End Function

End Class
