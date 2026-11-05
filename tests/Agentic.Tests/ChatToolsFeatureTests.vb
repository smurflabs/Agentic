Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>Bookmarks, rewind with the question given back, thinking off, and smarter compaction.</summary>
Public Class ChatToolsFeatureTests

    <Fact>
    Public Async Function BookmarkedChatsCanBeListedOnTheirOwn() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim a = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .title = "A"})
                Dim b = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a", .title = "B"})
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{b}", New With {.bookmarked = True}, admin.Csrf)).IsSuccessStatusCode)
                Dim all = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/conversations")).RootElement
                Assert.Equal(2, all.GetArrayLength())
                Assert.True(all.EnumerateArray().First(Function(c) c.GetProperty("id").GetString() = b).GetProperty("bookmarked").GetBoolean())
                Dim only = JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/conversations?bookmarked=1")).RootElement
                Assert.Equal({b}, only.EnumerateArray().Select(Function(c) c.GetProperty("id").GetString()).ToArray())
                ' Un-bookmarked: gone from the bookmarked list.
                Await PlatformApiTests.Send(admin.Client, "PATCH", $"/api/chat/conversations/{b}", New With {.bookmarked = False}, admin.Csrf)
                Assert.Equal(0, JsonDocument.Parse(Await admin.Client.GetStringAsync("/api/chat/conversations?bookmarked=1")).RootElement.GetArrayLength())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function RewindGivesBackTheQuestionItTookBack() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "first question"})
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "second question"})
                Dim conv = JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement
                Dim second = conv.GetProperty("messages").EnumerateArray().First(Function(m) m.GetProperty("content").GetString() = "second question").GetProperty("id").GetString()
                Dim r = JsonDocument.Parse(Await (Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/rewind", New With {.messageId = second}, admin.Csrf)).Content.ReadAsStringAsync()).RootElement
                Assert.Equal(2, r.GetProperty("removed").GetInt32())
                Assert.Equal("second question", r.GetProperty("prompt").GetProperty("content").GetString())
                Assert.Equal(2, JsonDocument.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{id}")).RootElement.GetProperty("messages").GetArrayLength())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ThinkingOffAsksTheModelNotToThinkAndCopesWithModelsThatRefuseTheSwitch() As Task
        Using up As New FakeUpstream()
            ' The first model call refuses the switches (as LiteLLM does for an unsupported parameter); the retry has none.
            Dim refused = 0
            up.ErrorOverride = Function(req)
                                   If req("chat_template_kwargs") IsNot Nothing AndAlso Interlocked.Increment(refused) = 1 Then Return "litellm.UnsupportedParamsError: does not support parameters: ['chat_template_kwargs', 'thinking']"
                                   Return Nothing
                               End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "hi", .settings = New With {.thinking = False}})
                Assert.Contains("event: done", sse)
                Dim streamed = up.Requests.ToArray().Where(AddressOf ChatFeatureTests.Streamed).ToList()
                ' First try carried the switches; after the refusal the model is remembered and asked without them.
                Assert.Equal(False, streamed(0)("chat_template_kwargs")("enable_thinking").GetValue(Of Boolean)())
                Assert.Equal("disabled", streamed(0)("thinking")("type").ToString())
                Assert.Null(streamed.Last()("chat_template_kwargs"))
                Assert.Contains("Reasoning effort: none", streamed.Last()("messages")(0)("content").ToString())

                ' Thinking on (the default): no switches.
                Dim before = streamed.Count
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "again", .settings = New With {.thinking = True}})
                Dim later = up.Requests.ToArray().Where(AddressOf ChatFeatureTests.Streamed).Skip(before).ToList()
                Assert.All(later, Sub(r) Assert.Null(r("thinking")))
            End Using
        End Using
    End Function

    <Fact>
    Public Sub ToolTargetsKeepWhatEachStepWorkedOn()
        Assert.Equal("src/app.ts", ChatService.ToolTarget("{""path"":""src/app.ts"",""content"":""x""}"))
        Assert.Equal("dotnet test", ChatService.ToolTarget("{""command"":""dotnet  test""}"))
        Assert.Equal("https://example.com", ChatService.ToolTarget("{""url"":""https://example.com""}"))
        ' A search: what it looks for, and where.
        Assert.Equal("""class \w+Controller"" in src/Api", ChatService.ToolTarget("{""pattern"":""class \\w+Controller"",""path"":""src/Api""}"))
        Assert.Equal("""*.cs""", ChatService.ToolTarget("{""pattern"":""*.cs""}"))
        Assert.Null(ChatService.ToolTarget("{""x"":1}"))
        Assert.Null(ChatService.ToolTarget("not json"))
    End Sub

    <Fact>
    Public Async Function ALongAnswerFoldsItsEarlierToolRoundsIntoANote() As Task
        Using up As New FakeUpstream()
            up.TextOverride = Function(req) "## Progress" & vbLf & "- Read src/app.ts and found the bug in parse()." & vbLf & "- Ran the tests: 3 failed."
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim messages As New JsonArray(New JsonObject From {{"role", "system"}, {"content", "sys"}}, New JsonObject From {{"role", "user"}, {"content", "fix it"}})
                Dim answerStart = messages.Count
                For i = 1 To 5
                    messages.Add(New JsonObject From {{"role", "assistant"}, {"content", "step " & i}, {"tool_calls", New JsonArray(New JsonObject From {
                        {"id", "c" & i}, {"type", "function"}, {"function", New JsonObject From {{"name", "read_file"}, {"arguments", "{""path"":""f" & i & ".ts""}"}}}})}})
                    messages.Add(New JsonObject From {{"role", "tool"}, {"tool_call_id", "c" & i}, {"content", New String("x"c, 2000)}})
                Next
                Dim folded = Await env.Host.H.Chat.FoldOldRoundsAsync(messages, answerStart, "fast/a", 2, CancellationToken.None)
                Assert.True(folded)
                ' The question stays; three rounds became one note; the last two rounds stay whole (each call with its result).
                Assert.Equal("fix it", messages(1)("content").ToString())
                Assert.Contains("earlier steps in it were folded", messages(2)("content").ToString())
                Assert.Contains("src/app.ts", messages(2)("content").ToString())
                Assert.Equal(2 + 1 + 4, messages.Count)
                Assert.Equal("c4", messages(3)("tool_calls")(0)("id").ToString())
                Assert.Equal("c4", messages(4)("tool_call_id").ToString())
            End Using
        End Using
    End Function
End Class
