Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Xunit

''' <summary>
''' Chat subagents: a failing model call is retried like the answer's own (an outage, a stream cut off), a fork starts with the
''' conversation, and subagents start their own up to Chat.MaxSubagentDepth.
''' </summary>
Public Class SubagentRetryForkTests

    Private Const BashTool As String = "{""name"":""Bash"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"
    Private Const Outage As String = "litellm.MidStreamFallbackError: litellm.APIConnectionError: APIConnectionError: OpenAIException - The service is temporarily unavailable. Please retry shortly."

    ''' <summary>Sends a message with a tool on the user's computer (so the chat offers subagents); returns the whole event stream.</summary>
    Private Shared Async Function AskAsync(admin As (Client As HttpClient, Csrf As String), chatId As String, content As String) As Task(Of String)
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = content, .model = "fast/a", .settings = New With {.permissionMode = "auto"}, .clientTools = {JsonNode.Parse(BashTool)}})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Using res = Await admin.Client.SendAsync(req)
            Return Await res.Content.ReadAsStringAsync()
        End Using
    End Function

    Private Shared Function LastUser(req As JsonNode) As String
        Return If(req("messages").AsArray().LastOrDefault(Function(m) m("role")?.ToString() = "user")?("content")?.ToString(), "")
    End Function

    Private Shared Function ToolNames(req As JsonNode) As List(Of String)
        Return If(TryCast(req("tools"), JsonArray), New JsonArray()).Select(Function(t) t?("function")?("name")?.ToString()).ToList()
    End Function

    Private Shared Async Function TaskResultAsync(admin As (Client As HttpClient, Csrf As String), chatId As String) As Task(Of JsonNode)
        Dim conv = Await ChatFeatureTests.GetChatAsync(admin, chatId)
        Return conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")
    End Function

    <Fact>
    Public Async Function ASubagentRetriesAnOutageAndACutOffStreamLikeTheAnswer() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0, subCalls = 0
            up.ErrorOverride = Function(req)
                                   If Not ChatFeatureTests.IsSubagent(req) Then Return Nothing
                                   ' 1st call: the provider's outage (a 400 whose text says it is temporary).
                                   Return If(Interlocked.Increment(subCalls) = 1, Outage, Nothing)
                               End Function
            up.StreamOverride = Function(req)
                                    If ChatFeatureTests.IsSubagent(req) Then
                                        ' 2nd call: the stream breaks off mid-answer (no finish reason, no [DONE]).
                                        If Volatile.Read(subCalls) = 2 Then Return "data: {""choices"":[{""index"":0,""delta"":{""content"":""half an ans""}}]}" & vbLf & vbLf
                                        Return Nothing
                                    End If
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(mainStep) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Look it up"",""prompt"":""Find the answer.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            ' 3rd call: it answers.
            up.TextOverride = Function(req) If(ChatFeatureTests.IsSubagent(req), "Found it: 42.", Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.RetrySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim sse = Await AskAsync(admin, id, "look it up")
                Dim task1 = Await TaskResultAsync(admin, id)
                Assert.True(task1("ok").GetValue(Of Boolean)(), task1.ToJsonString())
                Assert.Contains("Found it: 42.", task1("result").ToString())
                Assert.Equal(3, Volatile.Read(subCalls))
                ' Its card said it was retrying, without the provider's own text.
                Assert.Contains("Retrying in 1s · attempt 2 of 6", sse)
                Assert.Contains("attempt 3 of 6", sse)
                Assert.DoesNotContain("MidStreamFallbackError", sse)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ASubagentGivesUpAfterTheRetriesWithAShortError() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0, subCalls = 0
            up.ErrorOverride = Function(req)
                                   If Not ChatFeatureTests.IsSubagent(req) Then Return Nothing
                                   Interlocked.Increment(subCalls)
                                   Return Outage
                               End Function
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(mainStep) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Look it up"",""prompt"":""Find the answer.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                o.Chat.RetrySeconds = 1
                                                                o.Chat.Retries = 2
                                                            End Sub)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskAsync(admin, id, "look it up")
                Dim task1 = Await TaskResultAsync(admin, id)
                Assert.False(task1("ok").GetValue(Of Boolean)())
                Dim result = task1("result").ToString()
                Assert.Equal(3, Volatile.Read(subCalls))
                Assert.Contains("There was an issue with the upstream connection.", result)
                Assert.Contains("gave up after 3 attempts", result)
                Assert.DoesNotContain("litellm", result)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function ARefusedSubagentCallIsNotRetried() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0, subCalls = 0
            up.ErrorOverride = Function(req)
                                   If Not ChatFeatureTests.IsSubagent(req) Then Return Nothing
                                   Interlocked.Increment(subCalls)
                                   Return "Invalid parameter: tools[0].function.name"
                               End Function
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(mainStep) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Look it up"",""prompt"":""Find the answer.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.RetrySeconds = 1)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskAsync(admin, id, "look it up")
                Dim task1 = Await TaskResultAsync(admin, id)
                Assert.False(task1("ok").GetValue(Of Boolean)())
                Assert.Equal(1, Volatile.Read(subCalls))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AForkedSubagentStartsWithTheConversation() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0
            Dim subRequests As New Concurrent.ConcurrentQueue(Of JsonNode)
            up.StreamOverride = Function(req)
                                    If ChatFeatureTests.IsSubagent(req) Then
                                        subRequests.Enqueue(req)
                                        Return Nothing
                                    End If
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Select Case Interlocked.Increment(mainStep)
                                        Case 1 : Return ChatFeatureTests.TextStream("Noted.", "stop")
                                        Case 2 : Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Recall"",""prompt"":""What is the user's favourite colour?"",""fork"":true}")
                                        Case 3 : Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Fresh"",""prompt"":""What is the user's favourite colour?""}")
                                        Case Else : Return ChatFeatureTests.TextStream("Done.", "stop")
                                    End Select
                                End Function
            up.TextOverride = Function(req) If(ChatFeatureTests.IsSubagent(req), "Teal.", Nothing)
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskAsync(admin, id, "My favourite colour is teal.")
                Await AskAsync(admin, id, "Check what you know about me.")
                Dim forked = subRequests.First()
                Dim said = String.Join(vbLf, forked("messages").AsArray().Select(Function(m) If(m?("content")?.ToString(), "")))
                ' The fork sees the conversation (and is told it is a fork), but not the answer's calls that are still running.
                Assert.Contains("My favourite colour is teal.", said)
                Assert.Contains("forked subagent", said)
                Assert.DoesNotContain(forked("messages").AsArray(), Function(m) m?("role")?.ToString() = "assistant" AndAlso m?("tool_calls") IsNot Nothing)
                Assert.True(forked("messages")(0)("content").ToString().StartsWith("You are a subagent", StringComparison.Ordinal))
                ' A subagent without fork sees only its prompt.
                Dim fresh = subRequests.Skip(1).First()
                Assert.DoesNotContain(fresh("messages").AsArray(), Function(m) If(m?("content")?.ToString(), "").Contains("favourite colour is teal"))
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function SubagentsStartTheirOwnUpToTheDepthLimit() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0, outerStep = 0
            Dim inner As JsonNode = Nothing, outer As JsonNode = Nothing
            up.StreamOverride = Function(req)
                                    If ChatFeatureTests.IsSubagent(req) Then
                                        If LastUser(req).Contains("Nested job") Then
                                            inner = req
                                            Return ChatFeatureTests.TextStream("inner said 7", "stop")
                                        End If
                                        If outer Is Nothing Then outer = req
                                        If Interlocked.Increment(outerStep) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Inner"",""prompt"":""Nested job""}")
                                        Return ChatFeatureTests.TextStream("Outer report: the inner subagent said 7.", "stop")
                                    End If
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(mainStep) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Outer"",""prompt"":""Outer job""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up, Sub(o) o.Chat.MaxSubagentDepth = 2)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskAsync(admin, id, "do the job")
                Dim task1 = Await TaskResultAsync(admin, id)
                Assert.True(task1("ok").GetValue(Of Boolean)(), task1.ToJsonString())
                Assert.Contains("Outer report: the inner subagent said 7.", task1("result").ToString())
                ' The first level may start subagents; the second (the depth limit) may not.
                Assert.Contains("task", ToolNames(outer))
                Assert.NotNull(inner)
                Assert.DoesNotContain("task", ToolNames(inner))
                ' The inner report went back to the outer subagent as its tool result.
                Dim outerLast = up.Requests.ToArray().Where(Function(r) ChatFeatureTests.IsSubagent(r) AndAlso Not LastUser(r).Contains("Nested job")).Last()
                Assert.Contains(outerLast("messages").AsArray(), Function(m) m?("role")?.ToString() = "tool" AndAlso If(m?("content")?.ToString(), "").Contains("inner said 7"))
            End Using
        End Using
    End Function

End Class
