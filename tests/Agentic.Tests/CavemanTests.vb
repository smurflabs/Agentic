Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>Caveman mode (/cavemode): the rules go with every reply while it is on, and stop when it is off.</summary>
Public Class CavemanTests

    <Fact>
    Public Async Function CavemanRulesReachTheModelOnlyWhileOn() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim systemOf = Function() ChatFeatureTests.LastStreamedRequest(up)("messages")(0)("content").ToString()

                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi", .settings = New With {.caveman = "ultracave"}}))
                Assert.Contains("ULTRA CAVEMAN MODE", systemOf())
                Assert.Contains("Payload verbatim", systemOf())
                ' Kept with the chat: the next message (settings not sent) is still caveman.
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "again"}))
                Assert.Contains("ULTRA CAVEMAN MODE", systemOf())
                Assert.Equal("ultra", (Await ChatFeatureTests.GetChatAsync(admin, id))("settings")("caveman").ToString())

                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "wenyan", .settings = New With {.caveman = "megacave"}}))
                Assert.Contains("文言文", systemOf())

                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "normal", .settings = New With {.caveman = CType(Nothing, String)}}))
                Assert.DoesNotContain("CAVEMAN MODE", systemOf())
            End Using
        End Using
    End Function

End Class
