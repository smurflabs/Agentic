Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>Conversation branches (the CLI's /branch, --fork-session) and compaction focus instructions (/compact focus).</summary>
Public Class ChatForkTests

    Private Shared Async Function NewChatWithTwoExchangesAsync(admin As (Client As Net.Http.HttpClient, Csrf As String)) As Task(Of String)
        Dim id = JsonDocument.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/conversations", New With {.model = "fast/a", .title = "Plan A"}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetString()
        For Each question In {"first question", "second question"}
            Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/messages", New With {.content = question, .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
        Next
        Return id
    End Function

    Private Shared Async Function MessagesAsync(admin As (Client As Net.Http.HttpClient, Csrf As String), id As String) As Task(Of JsonElement)
        Return JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement
    End Function

    <Fact>
    Public Async Function ABranchCopiesTheConversationAndGoesItsOwnWay() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatWithTwoExchangesAsync(admin)
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/fork", "{}", admin.Csrf)
                Assert.Equal(HttpStatusCode.OK, res.StatusCode)
                Dim fork = JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement
                Dim forkId = fork.GetProperty("id").GetString()
                Assert.NotEqual(id, forkId)
                Assert.Equal("Plan A (branch)", fork.GetProperty("title").GetString())
                Dim original = Await MessagesAsync(admin, id)
                Dim copy = Await MessagesAsync(admin, forkId)
                Assert.Equal(4, copy.GetProperty("messages").GetArrayLength())
                For i = 0 To 3
                    Assert.Equal(original.GetProperty("messages")(i).GetProperty("content").GetString(), copy.GetProperty("messages")(i).GetProperty("content").GetString())
                    Assert.NotEqual(original.GetProperty("messages")(i).GetProperty("id").GetString(), copy.GetProperty("messages")(i).GetProperty("id").GetString())
                Next
                ' The branch goes on by itself; the original stays as it was.
                Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{forkId}/messages", New With {.content = "try plan B", .model = "fast/a"}, admin.Csrf)).Content.ReadAsStringAsync()
                Assert.Equal(6, (Await MessagesAsync(admin, forkId)).GetProperty("messages").GetArrayLength())
                Assert.Equal(4, (Await MessagesAsync(admin, id)).GetProperty("messages").GetArrayLength())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ABranchCanStopAtAMessage() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatWithTwoExchangesAsync(admin)
                Dim secondId = (Await MessagesAsync(admin, id)).GetProperty("messages")(1).GetProperty("id").GetString()
                Dim fork = JsonDocument.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/fork",
                                                                                 New With {.title = "Plan B", .messageId = secondId}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Assert.Equal("Plan B", fork.GetProperty("title").GetString())
                Assert.Equal(2, fork.GetProperty("messages").GetInt32())
                Dim missing = Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/fork", New With {.messageId = "msg_nope"}, admin.Csrf)
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function CompactFocusReachesTheSummarizer() As Task
        Using up As New FakeUpstream()
            Dim summarizerInput As String = Nothing
            up.TextOverride = Function(req)
                                  If Not req("messages")(0)("content").ToString().Contains("compress conversations") Then Return Nothing
                                  summarizerInput = req("messages")(1)("content").ToString()
                                  Return "## Goal" & vbLf & "Testing focused compaction of the questions." & vbLf & "## Open items" & vbLf & "None."
                              End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await NewChatWithTwoExchangesAsync(admin)
                Dim body = Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/compact", New With {.instructions = "the database schema"}, admin.Csrf)).Content.ReadAsStringAsync()
                Assert.Contains("event: done", body)
                Assert.Contains("""compacted"":true", body)
                Assert.Contains("Focus the summary on: the database schema", summarizerInput)
            End Using
        End Using
    End Function

End Class
