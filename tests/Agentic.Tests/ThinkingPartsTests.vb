Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Threading
Imports Xunit

''' <summary>Each thinking phase of an answer is kept with its place, so the chat shows one thinking block per phase.</summary>
Public Class ThinkingPartsTests

    <Fact>
    Public Async Function ThinkingAgainAfterActingStartsANewPart() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        ' Thinks, says a word, calls a tool.
                                        Return ChatFeatureTests.Chunk(New JsonObject From {{"reasoning_content", "First I plan. "}}) &
                                               ChatFeatureTests.Chunk(New JsonObject From {{"reasoning_content", "Then I look."}}) &
                                               ChatFeatureTests.Chunk(New JsonObject From {{"content", "Checking."}}) &
                                               ChatFeatureTests.ToolCallStream("remember", "{""fact"":""Likes tea""}")
                                    End If
                                    ' Thinks again after the tool, then answers.
                                    Return ChatFeatureTests.Chunk(New JsonObject From {{"reasoning_content", "Now I know."}}) &
                                           ChatFeatureTests.Chunk(New JsonObject From {{"content", vbLf}}) &
                                           ChatFeatureTests.Chunk(New JsonObject From {{"reasoning_content", " Still thinking."}}) &
                                           ChatFeatureTests.TextStream(" Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go"}))("message")
                Assert.Equal("First I plan. Then I look.Now I know. Still thinking.", message("reasoning").ToString())
                Dim parts = message("reasoningParts").AsArray()
                ' Two phases: before the tool (at the start), and after it - a line break between thoughts is still the same phase.
                Assert.Equal(2, parts.Count)
                Assert.Equal(0, parts(0)("start").GetValue(Of Integer)())
                Assert.Equal(0, parts(0)("at").GetValue(Of Integer)())
                Assert.Equal(0, parts(0)("tools").GetValue(Of Integer)())
                Assert.Equal("First I plan. Then I look.".Length, parts(1)("start").GetValue(Of Integer)())
                ' After the text before the tool (and the paragraph break the server puts after a tool), before the answer.
                Assert.InRange(parts(1)("at").GetValue(Of Integer)(), "Checking.".Length, message("content").ToString().IndexOf("Done"))
                Assert.Equal(1, parts(1)("tools").GetValue(Of Integer)())
                ' Saved with the message (the chat shows them after a reload too).
                Dim saved = (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray().Last()
                Assert.Equal(2, saved("reasoningParts").AsArray().Count)
            End Using
        End Using
    End Function

End Class
