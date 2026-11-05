Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>Runaway answers: a model repeating the same text, or the same tool call, is noticed and steered.</summary>
Public Class LoopGuardTests

    Private Const Story As String = "# The Cosmic Race" & vbLf & vbLf & "The ship drifted past the last beacon, and the crew watched the stars bend around the hull. " &
        "Nobody spoke; the silence was its own kind of answer. Captain Ilse checked the readings twice before she trusted them." & vbLf & vbLf

    Private Shared Function Times(unit As String, count As Integer) As String
        Return String.Concat(Enumerable.Repeat(unit, count))
    End Function

    <Fact>
    Public Sub TheGoldenSpiralLoopIsFoundAndCutToOneCopy()
        Dim unit = "It was *the Golden Spiral*." & vbLf & vbLf
        Dim s = Story & Times(unit, 40)
        Dim keep = ChatService.FindRepetition(s, 0)
        Assert.True(keep > 0)
        Assert.Equal(Story & unit, s.Substring(0, keep))
    End Sub

    <Fact>
    Public Sub ALoopOfAParagraphOrOfLinesWithChangingSpacingIsFound()
        Dim para = "The engines roared as the fleet turned toward the burning moon, and every pilot knew that the next hour would decide the war for good." & vbLf & vbLf
        Assert.True(ChatService.FindRepetition(Story & Times(para, 4), 0) > 0)
        ' The same sentence, but the spacing between copies varies (not an exact period).
        Dim sb As New StringBuilder(Story)
        For i = 1 To 12
            sb.Append("She looked at the Golden Spiral again.").Append(New String(" "c, i Mod 3)).Append(vbLf & vbLf)
        Next
        Dim keep = ChatService.FindRepetition(sb.ToString(), 0)
        Assert.True(keep > 0)
        Assert.EndsWith("She looked at the Golden Spiral again.", sb.ToString(0, keep).TrimEnd())
    End Sub

    <Fact>
    Public Sub NormalTextCodeAndDataAreNotMistakenForALoop()
        ' Prose that varies.
        Dim prose As New StringBuilder()
        For i = 1 To 60
            prose.Append($"Chapter {i}: the crew reached station {i * 7} and logged {i * 13} new signals before moving on.").Append(vbLf)
        Next
        Assert.Equal(-1, ChatService.FindRepetition(prose.ToString(), 0))
        ' A long run of zeros in data, short repeated code lines, and a markdown table.
        Assert.Equal(-1, ChatService.FindRepetition("[" & Times("0, ", 300) & "0]", 0))
        Dim code As New StringBuilder("```js" & vbLf)
        For i = 1 To 40
            code.Append("  }" & vbLf & "  console.log(value);" & vbLf)
        Next
        code.Append("```" & vbLf)
        Assert.Equal(-1, ChatService.FindRepetition(code.ToString(), 0))
        Dim table As New StringBuilder("| id | name |" & vbLf & "|---|---|" & vbLf)
        For i = 1 To 50
            table.Append($"| {i} | item {i} |").Append(vbLf)
        Next
        Assert.Equal(-1, ChatService.FindRepetition(table.ToString(), 0))
    End Sub

    <Fact>
    Public Sub ToolCallsAreTheSameWhateverTheirSpacingOrKeyOrder()
        Assert.Equal(ChatService.ToolCallKey("read_file", "{""path"":""a.txt"",""offset"":1}"), ChatService.ToolCallKey("read_file", "{ ""offset"": 1,  ""path"": ""a.txt"" }"))
        Assert.NotEqual(ChatService.ToolCallKey("read_file", "{""path"":""a.txt""}"), ChatService.ToolCallKey("read_file", "{""path"":""b.txt""}"))
        Assert.NotEqual(ChatService.ToolCallKey("read_file", "{""path"":""a.txt""}"), ChatService.ToolCallKey("list_dir", "{""path"":""a.txt""}"))
        ' A key sent twice (seen from a model in production: it crashed the agent's task) counts once, with its last value.
        Assert.Equal(ChatService.ToolCallKey("read_file", "{""path"":""b.txt""}"), ChatService.ToolCallKey("read_file", "{""path"":""a.txt"",""path"":""b.txt""}"))
        Assert.Equal(ChatService.ToolCallKey("x", "{""a"":[1,{""p"":""A""}]}"), ChatService.ToolCallKey("x", "{ ""a"": [1, {""p"": ""A""}] }"))
    End Sub

    <Fact>
    Public Async Function ARepeatingAnswerIsCutBackAndContinuedWithoutTheRepeats() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            Dim unit = "It was *the Golden Spiral*." & vbLf & vbLf
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(calls)
                                        Case 1 : Return ChatFeatureTests.TextStream(Story & Times(unit, 300), "length")
                                        Case Else : Return ChatFeatureTests.TextStream("The spiral opened, and they flew through. The end.", "stop")
                                    End Select
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "write a long story"})
                Assert.Contains("event: rollback", sse)
                Assert.Contains("started repeating itself", sse)
                Dim content = ChatFeatureTests.Done(sse)("message")("content").ToString()
                ' One copy of the repeated line stays; the rest is gone; the answer goes on.
                Assert.Equal(1, (content.Length - content.Replace("It was *the Golden Spiral*.", "").Length) \ "It was *the Golden Spiral*.".Length)
                Assert.StartsWith(Story.TrimEnd(), content)
                Assert.EndsWith("The end.", content)
                ' The second request tells the model what happened and discourages repeating.
                Dim second = up.Requests.ToArray().Last(Function(r) ChatFeatureTests.Streamed(r))
                Assert.Equal(0.5, second("frequency_penalty").GetValue(Of Double)())
                Assert.Contains(second("messages").AsArray(), Function(m) If(m("content")?.ToString(), "").Contains("started repeating the same text"))
                Assert.Equal(2, calls)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AModelThatKeepsRepeatingIsStoppedNotLeftRunning() As Task
        Using up As New FakeUpstream()
            Dim calls = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Interlocked.Increment(calls)
                                    Return ChatFeatureTests.TextStream(Times("Again and again the spiral turned. ", 200), "length")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go"})
                Dim content = ChatFeatureTests.Done(sse)("message")("content").ToString()
                Assert.Contains("Stopped: the model kept repeating the same text.", content)
                Assert.True(content.Length < 400, $"kept {content.Length} characters")
                Assert.Equal(ChatService.MaxRepetitionRetries + 1, calls)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheSameToolCallWithTheSameResultIsStoppedAndToolsGoAwayIfTheModelInsists() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    ' The model calls forget on a memory that does not exist, forever - until it has no tools.
                                    If req("tools") Is Nothing Then Return ChatFeatureTests.TextStream("I could not find that memory.", "stop")
                                    Return ChatFeatureTests.ToolCallStream("forget", "{""id"":""mem_nope""}")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "forget mem_nope"})
                Assert.Contains("repeated the same forget call", sse)
                Dim message = ChatFeatureTests.Done(sse)("message")
                Assert.Equal("I could not find that memory.", message("content").ToString().Trim())
                Dim results = message("toolCalls").AsArray().Select(Function(t) t("result").ToString()).ToList()
                ' Run three times (the same result each time), then refused three times, then no more tools.
                Assert.Equal(3, results.Where(Function(r) r.StartsWith("No memory matched")).Count())
                Assert.Equal(3, results.Where(Function(r) r.StartsWith("Not run:")).Count())
                Assert.Contains("Tools are now switched off", results.Last())
                Assert.Null(up.Requests.ToArray().Last(Function(r) ChatFeatureTests.Streamed(r))("tools"))
            End Using
        End Using
    End Function

End Class
