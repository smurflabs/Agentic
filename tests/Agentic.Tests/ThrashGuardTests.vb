Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json.Nodes
Imports Xunit

''' <summary>
''' Tool calls failing one after another, each with different arguments (the "let me try… let me try…" loop the audit found
''' in the Android TV chat), are caught: after a few failures the model is told to change approach, and if it keeps going the
''' tools are switched off so it has to answer. The same-arguments guard did not catch this because the arguments differed.
''' </summary>
Public Class ThrashGuardTests

    Private Const BashTool As String = "{""name"":""Bash"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"

    Private Shared Function ToolCall(n As Integer) As String
        Dim fn As New JsonObject From {{"name", "Bash"}, {"arguments", "{""command"":""attempt-" & n & """}"}}
        Dim call1 As New JsonObject From {{"index", 0}, {"id", "call_" & n}, {"type", "function"}, {"function", fn}}
        Return ChatFeatureTests.Chunk(New JsonObject From {{"tool_calls", New JsonArray(call1)}}) & ChatFeatureTests.Chunk(New JsonObject(), "tool_calls") & "data: [DONE]" & vbLf & vbLf
    End Function

    <Fact>
    Public Async Function ManyFailingCallsInARowAreStopped() As Task
        Using up As New FakeUpstream()
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Dim offersTools = (TryCast(req("tools"), JsonArray)?.Count).GetValueOrDefault() > 0
                                    If Not offersTools Then Return ChatFeatureTests.TextStream("I kept failing, here is what is wrong.", "stop")
                                    ' A different command each round, so the same-arguments guard does not fire.
                                    Dim n = req("messages").AsArray().Where(Function(m) m("role")?.ToString() = "tool").Count()
                                    Return ToolCall(n + 1)
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{id}/messages") With {
                    .Content = HttpApiTests.JsonBody(New With {.content = "fix the thing", .model = "fast/a",
                        .settings = New With {.permissionMode = "auto", .maxToolRounds = 25}, .clientTools = {JsonNode.Parse(BashTool)}})}
                req.Headers.Add("X-CSRF-Token", admin.Csrf)
                Dim calls = 0
                Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                    Async Function(evt, data)
                        If evt = "client_tool" Then
                            calls += 1
                            Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                New With {.id = data.GetProperty("id").GetString(), .ok = False, .result = "boom " & calls}, admin.Csrf)
                        End If
                    End Function)
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim results = conv("messages").AsArray().SelectMany(Function(m) If(TryCast(m("toolCalls"), JsonArray), New JsonArray())).
                                   Select(Function(t) t("result")?.ToString()).Where(Function(s) s IsNot Nothing).ToList()
                ' The model was told to change approach after a run of failures, and tools were cut so it stopped looping.
                Assert.Contains(results, Function(r) r.Contains("failed in a row. Stop trying small variations"))
                Assert.Contains(results, Function(r) r.Contains("Tools are now switched off"))
                Assert.True(calls <= 10, $"the loop should be stopped well before 25 rounds; it made {calls} calls")
                Dim last = conv("messages").AsArray().Last()
                Assert.Equal("assistant", last("role").ToString())
                Assert.Contains("here is what is wrong", last("content").ToString())
            End Using
        End Using
    End Function

End Class
