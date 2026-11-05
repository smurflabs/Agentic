Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Xunit

''' <summary>
''' Across turns the model remembers what its tools read and did (the bug in "Adding Outlook Graph support": it re-read the
''' same files and re-planned every turn because tool results were dropped from history).
''' </summary>
Public Class HistoryToolReplayTests

    Private Const ReadTool As String = "{""name"":""read_file"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""path"":{""type"":""string""}}},""x-risk"":""read""}"

    Private Shared Async Function AskRunningToolAsync(admin As (Client As HttpClient, Csrf As String), chatId As String, content As String, result As String, toolRounds As Integer) As Task
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = content, .model = "fast/a", .settings = New With {.permissionMode = "auto"}, .clientTools = {JsonNode.Parse(ReadTool)}})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Dim seen = 0
        Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead), Async Function(evt, data)
                                                                                                                              If evt = "client_tool" Then
                                                                                                                                  seen += 1
                                                                                                                                  Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{chatId}/tool-results", New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = result}, admin.Csrf)
                                                                                                                              End If
                                                                                                                          End Function)
    End Function

    <Fact>
    Public Async Function AFollowUpTurnSeesTheEarlierTurnsToolResults() As Task
        Using up As New FakeUpstream()
            Dim turn = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    ' Turn 1: read a file, then stop. Turn 2: just answer (no tools).
                                    Dim hasResult = req("messages").ToJsonString().Contains("SECRET_MARKER_42")
                                    If Not hasResult AndAlso req("messages").ToJsonString().Contains("read the config") AndAlso Not req("messages").ToJsonString().Contains("role"":""tool") Then
                                        Return ChatFeatureTests.ToolCallStream("read_file", "{""path"":""config.vb""}")
                                    End If
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Await AskRunningToolAsync(admin, id, "read the config", "PORT=8080 SECRET_MARKER_42 TIMEOUT=30", 1)
                ' Second turn: the request history must carry the earlier read's result and the tool_call that produced it.
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "what port?"}))
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray()
                Assert.Contains(sent, Function(m) m("role").ToString() = "tool" AndAlso m("content").ToString().Contains("SECRET_MARKER_42"))
                Dim toolMsg = sent.First(Function(m) m("role").ToString() = "tool")
                Dim callId = toolMsg("tool_call_id").ToString()
                ' The assistant tool_call that the tool result answers is present and comes before it (valid OpenAI order).
                Dim asstIdx = -1, toolIdx = -1
                For i = 0 To sent.Count - 1
                    If sent(i)("tool_calls") IsNot Nothing AndAlso sent(i)("tool_calls").ToJsonString().Contains(callId) Then asstIdx = i
                    If sent(i)("role").ToString() = "tool" AndAlso sent(i)("tool_call_id").ToString() = callId Then toolIdx = i
                Next
                Assert.True(asstIdx >= 0 AndAlso toolIdx = asstIdx + 1, $"asst={asstIdx} tool={toolIdx}")
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function UserNoteAndAskUserAreNotReplayedAsDanglingToolResults() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If req("messages").ToJsonString().Contains("user_note") Then Return Nothing   ' never: user_note must not be a tool in history
                                    Return ChatFeatureTests.TextStream("Second answer.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                ' First answer steers mid-stream (creates a user_note tool call in the saved message).
                Dim steered = False
                up.StreamOverride = Function(req)
                                        If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                        If Not steered Then steered = True : Assert.True(env.Host.H.Chat.Steer(id, "also check the footer"))
                                        Return ChatFeatureTests.TextStream("First answer.", "stop")
                                    End Function
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "start"}))
                up.StreamOverride = Function(req)
                                        If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                        Return ChatFeatureTests.TextStream("Second answer.", "stop")
                                    End Function
                ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "go on"}))
                Dim sent = ChatFeatureTests.LastStreamedRequest(up)("messages").AsArray()
                ' No tool message in history refers to user_note, and there are no tool messages without a matching tool_call.
                Dim callIds As New HashSet(Of String)
                For Each m In sent
                    Dim tc = TryCast(m("tool_calls"), JsonArray)
                    If tc IsNot Nothing Then For Each c In tc : callIds.Add(c("id").ToString()) : Next
                Next
                For Each m In sent
                    If m("role").ToString() = "tool" Then Assert.Contains(m("tool_call_id").ToString(), callIds)
                Next
                Assert.DoesNotContain("""name"":""user_note""", sent.ToJsonString())
            End Using
        End Using
    End Function

End Class
