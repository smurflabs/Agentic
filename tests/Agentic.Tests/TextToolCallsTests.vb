Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>Tool calls a model writes as text instead of making them run anyway (chat_2610191345059fe1628bc686).</summary>
Public Class TextToolCallsTests

    <Fact>
    Public Sub QwenXmlCallsAreRead()
        ' Exactly what Agents-A1-35B-VISION wrote as its answer and as its subagents' reports.
        Dim text = "<tool_call>" & vbLf & "<function=search_web>" & vbLf & "<parameter=query>" & vbLf & """iPod 4th generation"" ""power adapter"" ""100-240V""" & vbLf &
                   "</parameter>" & vbLf & "<parameter=category>" & vbLf & "general" & vbLf & "</parameter>" & vbLf & "</function>" & vbLf & "</tool_call>"
        Dim r = TextToolCalls.Extract(text, {"search_web", "read_page"})
        Assert.Equal("", r.Before)
        Assert.Single(r.Calls)
        Assert.Equal("search_web", r.Calls(0).Name)
        Dim args = JsonNode.Parse(r.Calls(0).Args)
        Assert.Equal("""iPod 4th generation"" ""power adapter"" ""100-240V""", args("query").ToString())
        Assert.Equal("general", args("category").ToString())

        ' Text before the call stays; two calls; numbers stay numbers; a tool that was not offered is not run.
        Dim two = TextToolCalls.Extract("Let me look." & vbLf & "<tool_call><function=read_page><parameter=url>https://example.com/a</parameter><parameter=max_chars>5000</parameter></function></tool_call>" &
                                        "<tool_call><function=delete_everything><parameter=x>1</parameter></function></tool_call>" &
                                        "<function=search_web><parameter=query>ipod</parameter></function>", {"search_web", "read_page"})
        Assert.Equal("Let me look.", two.Before)
        Assert.Equal({"read_page", "search_web"}, two.Calls.Select(Function(c) c.Name).ToArray())
        Assert.Equal(5000, JsonNode.Parse(two.Calls(0).Args)("max_chars").GetValue(Of Integer)())
    End Sub

    <Fact>
    Public Sub HermesJsonCallsAreReadAndPlainTextIsLeftAlone()
        Dim r = TextToolCalls.Extract("<tool_call>" & vbLf & "{""name"": ""read_page"", ""arguments"": {""url"": ""https://example.com""}}" & vbLf & "</tool_call>", {"read_page"})
        Assert.Single(r.Calls)
        Assert.Equal("https://example.com", JsonNode.Parse(r.Calls(0).Args)("url").ToString())
        ' Talking about tool calls is not a call.
        Dim plain = TextToolCalls.Extract("Use <function> tags in HTML? No, that is not a thing.", {"read_page"})
        Assert.Empty(plain.Calls)
        Assert.Equal("Use <function> tags in HTML? No, that is not a thing.", plain.Before)
    End Sub

    <Fact>
    Public Async Function ACallWrittenAsTextRunsAndIsTakenOutOfTheAnswer() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then
                                        Return ChatFeatureTests.TextStream("Noting that." & vbLf & "<tool_call>" & vbLf & "<function=remember>" & vbLf & "<parameter=fact>" & vbLf & "Likes tea" & vbLf &
                                                                           "</parameter>" & vbLf & "</function>" & vbLf & "</tool_call>", "stop")
                                    End If
                                    Return ChatFeatureTests.TextStream("Saved: you like tea.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "remember I like tea"}))("message")
                Dim content = message("content").ToString()
                Assert.DoesNotContain("<tool_call>", content)
                Assert.Contains("Noting that.", content)
                Assert.Contains("Saved: you like tea.", content)
                Dim call1 = message("toolCalls").AsArray().First(Function(t) t("name").ToString() = "remember")
                Assert.True(call1("ok").GetValue(Of Boolean)())
                Assert.Contains("Likes tea", call1("arguments").ToString())
                ' The model's history shows a real call, so it learns the right way.
                Dim second = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray()
                Assert.Contains(second, Function(m) m("tool_calls") IsNot Nothing AndAlso m("tool_calls").ToJsonString().Contains("remember"))
            End Using
        End Using
    End Function

End Class

''' <summary>An answer cut off by the model's output limit goes on by itself, in the same answer.</summary>
Public Class LengthContinueTests

    <Fact>
    Public Async Function ACutOffAnswerIsContinued() As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(stepNo) = 1 Then Return ChatFeatureTests.TextStream("The first half of a long answer,", "length")
                                    Return ChatFeatureTests.TextStream(" and the second half.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim message = ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "write a lot"}))("message")
                Assert.Equal("The first half of a long answer, and the second half.", message("content").ToString().Trim())
                Assert.Equal("stop", message("finishReason").ToString())
                ' The model was told to go on from where it stopped.
                Assert.Contains("cut off by the output length limit", ChatFeatureTests.LastStreamedRequest(up)("messages").ToJsonString())
            End Using
        End Using
    End Function

End Class
