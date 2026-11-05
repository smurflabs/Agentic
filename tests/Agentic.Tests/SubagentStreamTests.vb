Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Work
Imports Xunit

''' <summary>
''' Subagents stream what they write, think and do into their card (the Chat tab, the desktop app and VS Code show it live), the
''' saved call keeps that transcript, and a client's subagent (the CLI's Task) streams into its Task card the same way.
''' </summary>
Public Class SubagentStreamTests

    Private Const BashTool As String = "{""name"":""Bash"",""description"":""Run a shell command"",""parameters"":{""type"":""object"",""properties"":{""command"":{""type"":""string""}}},""x-risk"":""run""}"
    Private Const TaskTool As String = "{""name"":""Task"",""description"":""Start a subagent"",""parameters"":{""type"":""object"",""properties"":{""description"":{""type"":""string""},""prompt"":{""type"":""string""}}},""x-risk"":""read""}"

    ''' <summary>Each step on the card says what came of it, from its whole result (the card keeps only the start of it).</summary>
    <Fact>
    Public Async Function StepsSayWhatCameOfThem() As Task
        Assert.Equal("17 lines", SubagentStream.StepMeta("read_file", True, String.Join(vbLf, Enumerable.Range(1, 17).Select(Function(i) $"{i}" & vbTab & "line"))))
        Assert.Equal("3 results", SubagentStream.StepMeta("grep", True, "a.cs:1: x" & vbLf & "b.cs:2: y" & vbLf & "c.cs:3: z"))
        Assert.Equal("nothing found", SubagentStream.StepMeta("Grep", True, "No matches found."))
        Assert.Equal("exit 1", SubagentStream.StepMeta("run_command", True, "Exit code 1." & vbLf & "error CS1002"))
        Assert.Null(SubagentStream.StepMeta("read_file", False, "No such file"))
        Assert.Null(SubagentStream.StepMeta("edit_file", True, "Edited a.cs (1 replacement)."))
        ' The step's end event carries it, and the saved transcript keeps it.
        Dim sent As New List(Of JsonNode)
        Dim stream As New SubagentStream("call_1", Function(evt, data)
                                                       sent.Add(JsonNode.Parse(JsonSerializer.Serialize(data)))
                                                       Return Task.CompletedTask
                                                   End Function)
        Await stream.ToolStartAsync("s1", "read_file", "src/a.cs")
        Await stream.ToolEndAsync("s1", "read_file", "src/a.cs", True, "1" & vbTab & "a" & vbLf & "2" & vbTab & "b")
        Assert.Equal("2 lines", sent.Last()("call")("meta").ToString())
        Assert.Equal("2 lines", stream.Transcript.Items.Single().Meta)
    End Function

    ''' <summary>Sends a message with tools on "the user's computer" and collects every event of the answer.</summary>
    Private Shared Async Function AskAsync(admin As (Client As HttpClient, Csrf As String), chatId As String, content As String, tools As String(),
                                           onClientTool As Func(Of JsonElement, Task)) As Task(Of List(Of (Name As String, Data As JsonElement)))
        Dim events As New List(Of (Name As String, Data As JsonElement))
        Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
            .Content = HttpApiTests.JsonBody(New With {.content = content, .model = "fast/a", .settings = New With {.permissionMode = "auto"},
                                                       .clientTools = tools.Select(Function(t) JsonNode.Parse(t)).ToArray()})}
        req.Headers.Add("X-CSRF-Token", admin.Csrf)
        Await PlatformApiTests.ReadEventsAsync(Await admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
            Async Function(evt, data)
                SyncLock events
                    events.Add((evt, data.Clone()))
                End SyncLock
                If evt = "client_tool" Then Await onClientTool(data)
            End Function)
        Return events
    End Function

    Private Shared Function LastUserText(req As JsonNode) As String
        Return If(req("messages").AsArray().LastOrDefault(Function(m) m("role")?.ToString() = "user")?("content")?.ToString(), "")
    End Function

    <Fact>
    Public Async Function ASubagentsWritingThinkingAndStepsStreamIntoItsCard() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0
            up.StreamOverride = Function(req)
                                    If ChatFeatureTests.IsSubagent(req) Then
                                        ' The subagent thinks, says what it does, runs a command on the user's computer, then reports.
                                        If req("messages").AsArray().Any(Function(m) m("role")?.ToString() = "tool") Then Return ChatFeatureTests.TextStream("The command printed 42.", "stop")
                                        Return ChatFeatureTests.Chunk(New JsonObject From {{"reasoning_content", "Which command shows it? "}}) &
                                               ChatFeatureTests.Chunk(New JsonObject From {{"content", "I will run echo. "}}) &
                                               ChatFeatureTests.ToolCallStream("Bash", "{""command"":""echo 42""}")
                                    End If
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    If Interlocked.Increment(mainStep) = 1 Then Return ChatFeatureTests.ToolCallStream("task", "{""description"":""Check the box"",""prompt"":""Run echo 42 and report the output.""}")
                                    Return ChatFeatureTests.TextStream("Done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim events = Await AskAsync(admin, id, "check it", {BashTool},
                    Async Function(data)
                        Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{id}/tool-results",
                                                    New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = "42"}, admin.Csrf)
                    End Function)
                Dim taskId = events.First(Function(e) e.Name = "tool" AndAlso e.Data.GetProperty("call").GetProperty("name").GetString() = "task").Data.GetProperty("call").GetProperty("id").GetString()
                Dim subs = events.Where(Function(e) e.Name = "subagent").Select(Function(e) e.Data).ToList()
                Assert.All(subs, Sub(s) Assert.Equal(taskId, s.GetProperty("id").GetString()))
                Dim kinds = subs.Select(Function(s) s.GetProperty("kind").GetString()).ToList()
                Assert.Equal("start", kinds.First())
                Assert.Equal("end", kinds.Last())
                Assert.Equal("Check the box", subs(0).GetProperty("label").GetString())
                Assert.Contains(subs, Function(s) s.GetProperty("kind").GetString() = "reasoning" AndAlso s.GetProperty("text").GetString().Contains("Which command"))
                Dim written = String.Concat(subs.Where(Function(s) s.GetProperty("kind").GetString() = "text").Select(Function(s) s.GetProperty("text").GetString()))
                Assert.Contains("I will run echo.", written)
                Assert.Contains("The command printed 42.", written)
                Dim toolEnd = subs.First(Function(s) s.GetProperty("kind").GetString() = "tool" AndAlso s.GetProperty("phase").GetString() = "end")
                Assert.Equal("Bash", toolEnd.GetProperty("call").GetProperty("name").GetString())
                Assert.Contains("echo 42", toolEnd.GetProperty("call").GetProperty("target").GetString())
                Assert.True(toolEnd.GetProperty("call").GetProperty("ok").GetBoolean())
                Assert.True(subs.Last().GetProperty("ok").GetBoolean())
                ' The writing comes before the step, the report after it.
                Assert.True(kinds.IndexOf("tool") > kinds.IndexOf("text") AndAlso kinds.LastIndexOf("text") > kinds.LastIndexOf("tool"))

                ' Saved with the card: the same transcript, for the chat opened later.
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, id)
                Dim saved = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "task")("subagent")
                Assert.NotNull(saved)
                Assert.True(saved("done").GetValue(Of Boolean)())
                Assert.Equal("Check the box", saved("label").ToString())
                Dim items = saved("items").AsArray()
                Assert.Equal({"reasoning", "text", "tool", "text"}, items.Select(Function(i) i("kind").ToString()).ToArray())
                Assert.Equal("Bash", items(2)("name").ToString())
                Assert.True(items(2)("ok").GetValue(Of Boolean)())
                Assert.Equal("The command printed 42.", items(3)("text").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function AClientsSubagentStreamsIntoItsTaskCard() As Task
        Using up As New FakeUpstream()
            Dim mainStep = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    ' The CLI's subagent has a conversation of its own.
                                    If LastUserText(req).Contains("scan the repo") Then Return ChatFeatureTests.TextStream("Found three config files.", "stop")
                                    If Interlocked.Increment(mainStep) = 1 Then Return ChatFeatureTests.ToolCallStream("Task", "{""description"":""Look around"",""prompt"":""scan the repo""}")
                                    Return ChatFeatureTests.TextStream("All done.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim parentId = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim liveSubagent As JsonNode = Nothing
                Await AskAsync(admin, parentId, "look around", {TaskTool},
                    Async Function(data)
                        ' What the CLI does for its Task: a conversation linked to this call, answered, then the call's result.
                        Dim callId = data.GetProperty("id").GetString()
                        Dim created = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/conversations",
                                                                  New With {.title = "Subagent: Look around", .model = "fast/a",
                                                                            .parent = New With {.chatId = parentId, .callId = callId, .label = "Look around", .type = "Explore"}}, admin.Csrf)
                        Dim subId = JsonNode.Parse(Await created.Content.ReadAsStringAsync())("id").ToString()
                        ChatFeatureTests.Done(Await ChatFeatureTests.AskAsync(admin, subId, New With {.content = "scan the repo"}))
                        ' While the parent waits for the result, its live answer has the subagent's work (a window opening it sees it).
                        liveSubagent = JsonNode.Parse(Await admin.Client.GetStringAsync($"/api/chat/conversations/{parentId}/live"))("subagents")?(callId)
                        Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/conversations/{parentId}/tool-results",
                                                    New With {.id = callId, .ok = True, .result = "Found three config files."}, admin.Csrf)
                    End Function)
                Assert.NotNull(liveSubagent)
                Assert.Equal("Look around", liveSubagent("label").ToString())
                Assert.True(liveSubagent("done").GetValue(Of Boolean)())
                Assert.Contains(liveSubagent("items").AsArray(), Function(i) i("kind").ToString() = "text" AndAlso i("text").ToString().Contains("Found three config files."))
                ' Saved with the parent's Task call.
                Dim conv = Await ChatFeatureTests.GetChatAsync(admin, parentId)
                Dim taskCall = conv("messages").AsArray().Last()("toolCalls").AsArray().First(Function(c) c("name").ToString() = "Task")
                Dim saved = taskCall("subagent")
                Assert.NotNull(saved)
                Assert.Equal("Explore", saved("kind").ToString())
                Assert.Contains("Found three config files.", saved("items").AsArray().First(Function(i) i("kind").ToString() = "text")("text").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Sub ThinkTagsSplitAcrossChunks()
        Dim s As New ThinkTagSplitter()
        Dim parts As New List(Of (Think As Boolean, Text As String))
        For Each chunk In {"<thi", "nk>Hmm, ", "let me see</th", "ink>The answer", " is 4."}
            parts.AddRange(s.Feed(chunk))
        Next
        Dim rest = s.Finish()
        If rest.Text.Length > 0 Then parts.Add(rest)
        Assert.Equal("Hmm, let me see", String.Concat(parts.Where(Function(p) p.Think).Select(Function(p) p.Text)))
        Assert.Equal("The answer is 4.", String.Concat(parts.Where(Function(p) Not p.Think).Select(Function(p) p.Text)))
        ' A lone "<" that starts no tag is kept as text.
        Dim t As New ThinkTagSplitter()
        Dim a = t.Feed("x <")
        Dim b = t.Feed(" y")
        Assert.Equal("x < y", String.Concat(a.Concat(b).Select(Function(p) p.Text)) & t.Finish().Text)
    End Sub

    <Fact>
    Public Sub TheTranscriptKeepsItsBeginningWhenALongRunOverflows()
        Dim tr As New SubagentTranscript()
        For i = 0 To 99
            tr.Apply(New JsonObject From {{"kind", "text"}, {"text", New String("x"c, 400)}})
            tr.Apply(New JsonObject From {{"kind", "tool"}, {"phase", "start"}, {"call", New JsonObject From {{"id", "c" & i}, {"name", "Read"}}}})
        Next
        Assert.True(tr.Clipped)
        Assert.True(tr.Items.Where(Function(i) i.Kind = "text").Sum(Function(i) i.Text.Length) <= SubagentTranscript.MaxTextChars)
        Assert.True(tr.Items.Count <= SubagentTranscript.MaxItems)
    End Sub

End Class
