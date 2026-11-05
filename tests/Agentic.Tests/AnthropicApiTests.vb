Option Strict On
Option Explicit On
Option Infer On

Imports System.Net
Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports Agentic.Http
Imports Xunit

''' <summary>The Anthropic-compatible Messages API at /v2 (translated to the OpenAI path and back).</summary>
Public Class AnthropicApiTests

    Private Shared Async Function PostAsync(user As (Client As HttpClient, Csrf As String), body As Object) As Task(Of HttpResponseMessage)
        Dim req As New HttpRequestMessage(HttpMethod.Post, "/v2/messages") With {.Content = HttpApiTests.JsonBody(body)}
        req.Headers.Add("X-CSRF-Token", user.Csrf)
        Return Await user.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead)
    End Function

    <Fact>
    Public Async Function NonStreamTranslatesBothWays() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 100, .system = "Be brief.",
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.True(res.IsSuccessStatusCode, Await res.Content.ReadAsStringAsync())
                Dim m = JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement
                Assert.Equal("message", m.GetProperty("type").GetString())
                Assert.Equal("assistant", m.GetProperty("role").GetString())
                Assert.Equal("fast/a", m.GetProperty("model").GetString())
                Assert.Equal("text", m.GetProperty("content")(0).GetProperty("type").GetString())
                Assert.Contains("answer from fast/a", m.GetProperty("content")(0).GetProperty("text").GetString())
                Assert.Equal("end_turn", m.GetProperty("stop_reason").GetString())
                Assert.True(m.GetProperty("usage").GetProperty("input_tokens").GetInt64() > 0)
                Assert.Equal(3, m.GetProperty("usage").GetProperty("output_tokens").GetInt64())
                ' The upstream got an OpenAI body: a system message then the user message, max_tokens carried.
                Dim sent = up.Requests.Last()
                Assert.Equal("system", sent("messages")(0)("role").ToString())
                Assert.Equal("Be brief.", sent("messages")(0)("content").ToString())
                Assert.Equal("user", sent("messages")(1)("role").ToString())
                Assert.Equal("100", sent("max_tokens").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ToolResultAndToolDefinitionsAreTranslated() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 50,
                    .tools = {New With {.name = "get_weather", .description = "Weather", .input_schema = New With {.type = "object", .properties = New With {.city = New With {.type = "string"}}}}},
                    .tool_choice = New With {.type = "auto"},
                    .messages = New Object() {
                        New With {.role = "user", .content = "weather?"},
                        New With {.role = "assistant", .content = {New With {.type = "tool_use", .id = "toolu_1", .name = "get_weather", .input = New With {.city = "NYC"}}}},
                        New With {.role = "user", .content = {New With {.type = "tool_result", .tool_use_id = "toolu_1", .content = "sunny"}}}}})
                Dim sent = up.Requests.Last()
                ' tools -> OpenAI function tools; tool_choice auto; assistant tool_use -> tool_calls; tool_result -> role tool.
                Assert.Equal("function", sent("tools")(0)("type").ToString())
                Assert.Equal("get_weather", sent("tools")(0)("function")("name").ToString())
                Assert.Equal("auto", sent("tool_choice").ToString())
                Dim msgs = sent("messages").AsArray()
                Dim asst = msgs.First(Function(x) x("role").ToString() = "assistant")
                Assert.Equal("toolu_1", asst("tool_calls")(0)("id").ToString())
                Assert.Equal("get_weather", asst("tool_calls")(0)("function")("name").ToString())
                Assert.Contains("NYC", asst("tool_calls")(0)("function")("arguments").ToString())
                Dim toolMsg = msgs.First(Function(x) x("role").ToString() = "tool")
                Assert.Equal("toolu_1", toolMsg("tool_call_id").ToString())
                Assert.Contains("sunny", toolMsg("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function StreamingToolCallEmitsAnthropicEvents() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req) ChatFeatureTests.ToolCallStream("get_weather", "{""city"":""NYC""}")
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 50, .stream = True,
                    .tools = {New With {.name = "get_weather", .description = "w", .input_schema = New With {.type = "object"}}},
                    .messages = {New With {.role = "user", .content = "weather in NYC?"}}})
                Dim sse = Await res.Content.ReadAsStringAsync()
                Assert.Contains("event: message_start", sse)
                Assert.Contains("event: content_block_start", sse)
                Assert.Contains("""type"":""tool_use""", sse)
                Assert.Contains("""name"":""get_weather""", sse)
                Assert.Contains("input_json_delta", sse)
                Assert.Contains("NYC", sse)
                Assert.Contains("event: content_block_stop", sse)
                Assert.Contains("""stop_reason"":""tool_use""", sse)
                Assert.Contains("event: message_stop", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function StreamingTextEmitsTextDeltas() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 50, .stream = True,
                    .messages = {New With {.role = "user", .content = "hi"}}})
                Dim sse = Await res.Content.ReadAsStringAsync()
                Assert.Contains("event: content_block_delta", sse)
                Assert.Contains("text_delta", sse)
                Assert.Contains("answer ", sse)
                Assert.Contains("""stop_reason"":""end_turn""", sse)
                Assert.Contains("event: message_stop", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ClaudeIdsAreServedByAProvidersCopy() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.AddRange({"prov/claude-sonnet-5", "prov/claude-sonnet-5-5"})
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                ' What Claude Code's auto-mode safety classifier asks for, and the dated and [1m] forms of the id.
                For Each asked In {"claude-sonnet-5", "claude-sonnet-5-20260514", "claude-sonnet-5[1m]"}
                    Dim res = Await PostAsync(admin, New With {.model = asked, .max_tokens = 50,
                        .messages = {New With {.role = "user", .content = "Is this command safe?"}}})
                    Assert.True(res.IsSuccessStatusCode, Await res.Content.ReadAsStringAsync())
                    Dim m = JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement
                    Assert.Equal(asked, m.GetProperty("model").GetString())                        ' the answer names the model asked for
                    Assert.Equal("prov/claude-sonnet-5", up.Requests.Last()("model").ToString())     ' the provider's copy answered
                Next
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ClaudeModelAliasesOverrideAndCatchAll() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.Add("prov/claude-sonnet-5")
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.ClaudeModelAliases("claude-sonnet-5") = "code/b"
                                                                o.ClaudeModelAliases("*") = "fast/a"
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Await PostAsync(admin, New With {.model = "claude-sonnet-5", .max_tokens = 20, .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal("code/b", up.Requests.Last()("model").ToString())       ' an alias beats a provider's copy
                Await PostAsync(admin, New With {.model = "claude-nonexistent-9", .max_tokens = 20, .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal("fast/a", up.Requests.Last()("model").ToString())       ' "*" answers a Claude id no provider has
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheCheapestPricedCopyAnswers() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.AddRange({"none/claude-sonnet-5", "pricey/claude-sonnet-5", "cheap/claude-sonnet-5"})
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {
                    New With {.model = "pricey/claude-sonnet-5", .inputPerMillion = 5, .outputPerMillion = 10},
                    New With {.model = "cheap/claude-sonnet-5", .inputPerMillion = 1, .outputPerMillion = 2}}}, admin.Csrf)).IsSuccessStatusCode)
                Await PostAsync(admin, New With {.model = "claude-sonnet-5", .max_tokens = 20, .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal("cheap/claude-sonnet-5", up.Requests.Last()("model").ToString())    ' priced (Model Square) before unpriced, cheapest first
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ABrokenCopyHandsOverToTheNext() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.AddRange({"broken/claude-sonnet-5", "good/claude-sonnet-5"})
            up.Down.Add("broken/claude-sonnet-5")                                             ' the provider refuses (503)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Assert.True((Await PlatformApiTests.Send(admin.Client, "PUT", "/api/model-square", New With {.prices = {
                    New With {.model = "broken/claude-sonnet-5", .inputPerMillion = 1, .outputPerMillion = 1},
                    New With {.model = "good/claude-sonnet-5", .inputPerMillion = 5, .outputPerMillion = 5}}}, admin.Csrf)).IsSuccessStatusCode)
                Dim res = Await PostAsync(admin, New With {.model = "claude-sonnet-5", .max_tokens = 20, .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.True(res.IsSuccessStatusCode, Await res.Content.ReadAsStringAsync())
                Dim tried = up.Requests.Select(Function(q) q("model").ToString()).ToList()
                Assert.Equal({"broken/claude-sonnet-5", "good/claude-sonnet-5"}, tried.TakeLast(2).ToArray())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheSafetyClassifierIsAnsweredByAClaudeModel() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.Add("prov/claude-sonnet-5")
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim classifierSystem = "Decide whether the action must be blocked. Output <block>yes</block><reason>…</reason> or <block>no</block>. " &
                                       "Err on the side of blocking. Your ENTIRE response MUST begin with <block>."
                ' Claude Code fell back to its (non-Claude) session model for the classifier: a Claude copy answers instead.
                Dim res = Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 64, .system = classifierSystem, .stop_sequences = {"</block>"},
                    .messages = {New With {.role = "user", .content = "<action>Bash: ls -la</action>"}}})
                Assert.True(res.IsSuccessStatusCode, Await res.Content.ReadAsStringAsync())
                Assert.Equal("prov/claude-sonnet-5", up.Requests.Last()("model").ToString())
                Assert.Equal("fast/a", JsonDocument.Parse(Await res.Content.ReadAsStringAsync()).RootElement.GetProperty("model").GetString())
                ' Other requests to the session model are untouched.
                Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 20, .system = "Be brief.", .messages = {New With {.role = "user", .content = "hi"}}})
                Assert.Equal("fast/a", up.Requests.Last()("model").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function TheClassifierAliasPicksItsModels() As Task
        Using up As New FakeUpstream()
            up.ExtraModels.AddRange({"prov/claude-sonnet-5", "sev/claude-opus-5-5"})
            up.Down.Add("code/b")
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.ClaudeModelAliases(AdminApi.ClassifierAlias) = "code/b, sev/claude-opus-5-5")
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Await PostAsync(admin, New With {.model = "fast/a", .max_tokens = 64, .system = "Output <block>no</block> or <block>yes</block>. Err on the side of blocking.",
                    .messages = {New With {.role = "user", .content = "<action>Bash: pwd</action>"}}})
                ' The first listed refuses, the second answers.
                Assert.Equal("sev/claude-opus-5-5", up.Requests.Last()("model").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub BareClaudeIds()
        Assert.Equal("claude-sonnet-5", AdminApi.BareClaudeId("claude-sonnet-5-20260514"))
        Assert.Equal("claude-sonnet-5", AdminApi.BareClaudeId("Claude-Sonnet-5[1m]"))
        Assert.Equal("claude-opus-4-6", AdminApi.BareClaudeId("claude-opus-4-6@20260101"))
        Assert.Equal("claude-3-5-sonnet", AdminApi.BareClaudeId("claude-3-5-sonnet-latest"))
        Assert.Equal("claude-opus-4-5-20251101-thinking", AdminApi.BareClaudeId("claude-opus-4-5-20251101-thinking"))
    End Sub

    <Fact>
    Public Sub UnitTranslations()
        ' Request: image block -> image_url data URL; stop_sequences -> stop.
        Dim req = JsonNode.Parse("{""model"":""m"",""max_tokens"":10,""stop_sequences"":[""END""],""messages"":[{""role"":""user"",""content"":[" &
            "{""type"":""text"",""text"":""look""},{""type"":""image"",""source"":{""type"":""base64"",""media_type"":""image/png"",""data"":""QUJD""}}]}]}")
        Dim oai = AdminApi.AnthropicToOpenAi(DirectCast(req, JsonObject))
        Assert.Equal("END", oai("stop")(0).ToString())
        Dim content = oai("messages")(0)("content").AsArray()
        Assert.Equal("image_url", content(1)("type").ToString())
        Assert.Equal("data:image/png;base64,QUJD", content(1)("image_url")("url").ToString())

        ' Response: OpenAI tool_calls -> Anthropic tool_use block; length -> max_tokens.
        Dim comp = JsonNode.Parse("{""choices"":[{""message"":{""role"":""assistant"",""content"":""ok"",""tool_calls"":[" &
            "{""id"":""call_1"",""function"":{""name"":""f"",""arguments"":""{\""a\"":1}""}}]},""finish_reason"":""length""}]}")
        Dim an = AdminApi.OpenAiToAnthropic(comp, "msg_1", "m", 7, 9)
        Assert.Equal("text", an("content")(0)("type").ToString())
        Assert.Equal("tool_use", an("content")(1)("type").ToString())
        Assert.Equal("f", an("content")(1)("name").ToString())
        Assert.Equal("1", an("content")(1)("input")("a").ToString())
        Assert.Equal("max_tokens", an("stop_reason").ToString())
        Assert.Equal(7, an("usage")("input_tokens").GetValue(Of Long)())
    End Sub

End Class
