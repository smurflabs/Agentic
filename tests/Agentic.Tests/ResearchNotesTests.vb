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
''' Research notes: an answer that researched (web lookups by itself or its subagents) has its findings saved by the harness as
''' Markdown - attached to the answer, and written to research/&lt;chat&gt;.md on the computer the chat works on.
''' </summary>
Public Class ResearchNotesTests

    <Fact>
    Public Sub ResearchToolsAreKnownByNameAndByMcpToolName()
        For Each n In {"search_web", "read_page", "news_articles", "WebSearch", "WebFetch", "mcp__fetch__fetch", "mcp__brave__brave_web_search", "web__fetch_url"}
            Assert.True(ChatService.IsResearchTool(n), n)
        Next
        For Each n In {"Read", "read_file", "run_command", "mcp__github__create_issue", "fetcher", "", Nothing}
            Assert.False(ChatService.IsResearchTool(n), If(n, "(null)"))
        Next
    End Sub

    <Fact>
    Public Sub AnAnswerResearchedWhenItOrItsSubagentLookedThingsUp()
        Dim plain As New ChatMessageRecord With {.Role = "assistant", .Content = "Hi"}
        plain.ToolCalls.Add(New ChatToolCall With {.Name = "read_file", .Arguments = "{""path"":""a""}"})
        Assert.False(ChatService.IsResearchAnswer(plain))
        ' The automatic search before an answer (sources, no tool call) is not research.
        plain.Sources.Add(New ChatSource With {.Index = 1, .Title = "x", .Url = "https://x"})
        Assert.False(ChatService.IsResearchAnswer(plain))

        Dim own As New ChatMessageRecord With {.Role = "assistant", .Content = "Found"}
        own.ToolCalls.Add(New ChatToolCall With {.Name = "search_web", .Arguments = "{""query"":""rust async runtimes""}"})
        own.ToolCalls.Add(New ChatToolCall With {.Name = "search_web", .Arguments = "{""query"":""rust  async runtimes""}"})
        Assert.True(ChatService.IsResearchAnswer(own))
        Assert.Equal({"rust async runtimes"}, ChatService.ResearchQueries(own).ToArray())
        Assert.Equal(2, ChatService.ResearchLookups(own))

        Dim viaAgent As New ChatMessageRecord With {.Role = "assistant", .Content = "Found"}
        Dim t As New SubagentTranscript()
        t.Items.Add(New SubagentItem With {.Kind = "tool", .Name = "WebSearch", .Target = "tokio vs smol"})
        viaAgent.ToolCalls.Add(New ChatToolCall With {.Name = "task", .Arguments = "{}", .Subagent = t})
        Assert.True(ChatService.IsResearchAnswer(viaAgent))
        Assert.Equal({"tokio vs smol"}, ChatService.ResearchQueries(viaAgent).ToArray())
        Assert.Equal(1, ChatService.ResearchLookups(viaAgent))
        Assert.False(ChatService.IsResearchAnswer(New ChatMessageRecord With {.Role = "user", .Content = "x"}))
    End Sub

    <Fact>
    Public Sub NotesFileNamesAreSafeSlugs()
        Assert.Equal("comparing-python-web-frameworks", ChatService.NotesSlug("Comparing Python web frameworks"))
        Assert.Equal("research", ChatService.NotesSlug("../../ ?? "))
        Assert.Equal("c-vs-rust", ChatService.NotesSlug("C++ vs Rust!"))
        Dim longOne = ChatService.NotesSlug(String.Join(" ", Enumerable.Repeat("word", 40)))
        Assert.True(longOne.Length <= 60 AndAlso Not longOne.EndsWith("-"), longOne)
    End Sub

    <Fact>
    Public Sub NotesKeepQuestionFindingsSourcesAndSearches()
        Dim q As New ChatMessageRecord With {.Role = "user", .Content = "Which async runtime should I use?"}
        Dim a As New ChatMessageRecord With {.Role = "assistant", .Model = "fast/a", .CreatedUtc = New DateTime(2026, 11, 4, 15, 32, 0, DateTimeKind.Utc),
            .Content = "# Summary" & vbLf & "Tokio is the default [1]." & vbLf & "```" & vbLf & "# not a heading" & vbLf & "```"}
        a.ToolCalls.Add(New ChatToolCall With {.Name = "search_web", .Arguments = "{""query"":""rust async runtime""}"})
        a.Sources.Add(New ChatSource With {.Index = 1, .Title = "Tokio [docs]", .Url = "https://tokio.rs/a b"})
        a.Sources.Add(New ChatSource With {.Index = 2, .Title = "My notes", .Internal = True})
        Dim md = ChatService.BuildResearchNotes("Async runtimes", "chat_1", {(q, a)})
        Assert.StartsWith("# Research: Async runtimes", md)
        Assert.Contains("## 1. Which async runtime should I use?", md)
        Assert.Contains("_2026-11-04 15:32 UTC · fast/a · 1 source_", md)
        Assert.Contains("> Which async runtime should I use?", md)
        Assert.Contains("### Summary", md)
        Assert.Contains(vbLf & "# not a heading", md)
        Assert.Contains("1. [Tokio docs](https://tokio.rs/a%20b)", md)
        Assert.Contains("2. My notes (knowledge base)", md)
        Assert.Contains("- rust async runtime", md)
        Assert.DoesNotContain("Stopped", md)
    End Sub

    <Fact>
    Public Sub NotesKeepSubagentReportsPagesAndStoppedWork()
        Dim q As New ChatMessageRecord With {.Role = "user", .Content = "Compare runtimes"}
        Dim a As New ChatMessageRecord With {.Role = "assistant", .Model = "fast/a", .Content = "", .Error = "Stopped."}
        Dim t As New SubagentTranscript With {.Label = "Look up [tokio]"}
        t.Items.Add(New SubagentItem With {.Kind = "tool", .Name = "search_web", .Target = "tokio benchmarks"})
        a.ToolCalls.Add(New ChatToolCall With {.Name = "task", .Arguments = "{}", .Subagent = t, .Ok = True, .Result = "# Report" & vbLf & "Tokio wins on throughput."})
        a.ToolCalls.Add(New ChatToolCall With {.Name = "read_page", .Arguments = "{""url"":""https://tokio.rs/""}", .Ok = True,
                                               .Result = "[1] Tokio — https://tokio.rs/" & vbLf & "Tokio is an <async> runtime." & vbLf & vbLf & "Cite this page as [1]."})
        a.ToolCalls.Add(New ChatToolCall With {.Name = "WebFetch", .Arguments = "{""url"":""https://smol.rs/""}", .Ok = False, .Result = "404"})
        Dim md = ChatService.BuildResearchNotes("Runtimes", "chat_1", {(q, a)})
        Assert.Contains("**Stopped before it finished:**", md)
        Assert.Contains("_No answer written yet._", md)
        Assert.Contains("### Subagent: Look up tokio", md)
        Assert.Contains("#### Report", md)
        Assert.Contains("Tokio wins on throughput.", md)
        Assert.Contains("<details><summary>1 Tokio — https://tokio.rs/</summary>", md)
        Assert.Contains("Tokio is an &lt;async> runtime.", md)
        Assert.DoesNotContain("Cite this page", md)
        Assert.DoesNotContain("smol.rs", md)
        Assert.Contains("- tokio benchmarks", md)

        Dim failed As New ChatMessageRecord With {.Role = "assistant", .Content = "Partial", .Error = "The model is busy."}
        failed.ToolCalls.Add(New ChatToolCall With {.Name = "search_web", .Arguments = "{""query"":""x""}"})
        Assert.Contains("**The answer failed** (The model is busy.)", ChatService.BuildResearchNotes("R", "c", {(q, failed)}))
        Dim running As New ChatMessageRecord With {.Role = "assistant", .Content = "So far", .FinishReason = ChatService.InProgressFinish}
        running.ToolCalls.Add(New ChatToolCall With {.Name = "search_web", .Arguments = "{""query"":""x""}"})
        Assert.Contains("**Still researching:**", ChatService.BuildResearchNotes("R", "c", {(q, running)}))
    End Sub

    Private Const FetchTool As String = "{""name"":""WebFetch"",""description"":""Fetch a page"",""parameters"":{""type"":""object"",""properties"":{""url"":{""type"":""string""}}},""x-risk"":""read""}"
    Private Const WriteTool As String = "{""name"":""Write"",""description"":""Write a file"",""parameters"":{""type"":""object"",""properties"":{""file_path"":{""type"":""string""},""content"":{""type"":""string""}}},""x-risk"":""change""}"
    Private Const ReadTool As String = "{""name"":""Read"",""description"":""Read a file"",""parameters"":{""type"":""object"",""properties"":{""file_path"":{""type"":""string""}}},""x-risk"":""read""}"

    Private NotInheritable Class Session
        Public Writes As New List(Of (Path As String, Content As String, Approved As Boolean))
        Public Files As New List(Of String)
        Public Admin As (Client As HttpClient, Csrf As String)
        ''' <summary>Sends a message to a chat with the CLI's tools; researchNotes: the chat's /notes setting (Nothing = not sent).</summary>
        Public Send As Func(Of String, String, Boolean?, Task)
    End Class

    ''' <summary>
    ''' A server whose model fetches fetches pages (WebFetch, a CLI tool) for each question, then answers; a message with "hello"
    ''' is answered without tools. Writes and attachments the harness made are collected.
    ''' </summary>
    Private Shared Async Function WithResearchServerAsync(fetches As Integer, body As Func(Of Session, Task)) As Task
        Using up As New FakeUpstream()
            Dim stepNo = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Dim n = Interlocked.Increment(stepNo)
                                    Dim msgs = req("messages").AsArray()
                                    Dim last = msgs.Last()
                                    If last("role")?.ToString() = "user" AndAlso last("content")?.ToString().Contains("hello") Then Return ChatFeatureTests.TextStream("Hello!", "stop")
                                    Dim lastUser = msgs.Select(Function(m, i) (m, i)).Last(Function(x) x.m("role")?.ToString() = "user").i
                                    Dim done = msgs.Skip(lastUser + 1).Count(Function(m) m("role")?.ToString() = "tool")
                                    If done < fetches Then Return ChatFeatureTests.ToolCallStream("WebFetch", "{""url"":""https://example.com/" & done & """}")
                                    Return ChatFeatureTests.TextStream("Finding number " & n & ": example.com is a test domain.", "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim s As New Session With {.Admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")}
                s.Send = Async Function(chatId As String, text As String, notes As Boolean?) As Task
                             Dim settings As Object = If(notes.HasValue, CObj(New With {.permissionMode = "auto", .researchNotes = notes.Value}), New With {.permissionMode = "auto"})
                             Dim req As New HttpRequestMessage(HttpMethod.Post, $"/api/chat/conversations/{chatId}/messages") With {
                                 .Content = HttpApiTests.JsonBody(New With {.content = text, .model = "fast/a", .settings = settings,
                                                                            .clientTools = {JsonNode.Parse(FetchTool), JsonNode.Parse(WriteTool), JsonNode.Parse(ReadTool)}})}
                             req.Headers.Add("X-CSRF-Token", s.Admin.Csrf)
                             Await PlatformApiTests.ReadEventsAsync(Await s.Admin.Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead),
                                 Async Function(evt, data)
                                     Select Case evt
                                         Case "file"
                                             s.Files.Add(data.GetProperty("attachment").GetProperty("name").GetString())
                                         Case "client_tool"
                                             Dim name = data.GetProperty("name").GetString()
                                             Dim args = JsonNode.Parse(data.GetProperty("arguments").GetString())
                                             Dim approvedProp As JsonElement
                                             If name = "Write" Then s.Writes.Add((args("file_path").ToString(), args("content").ToString(),
                                                                                  data.TryGetProperty("approved", approvedProp) AndAlso approvedProp.ValueKind = JsonValueKind.True))
                                             Await PlatformApiTests.Send(s.Admin.Client, "POST", $"/api/chat/conversations/{chatId}/tool-results",
                                                                         New With {.id = data.GetProperty("id").GetString(), .ok = True, .result = If(name = "WebFetch", "Example Domain. This domain is for use in examples.", "ok")}, s.Admin.Csrf)
                                     End Select
                                 End Function)
                         End Function
                Await body(s)
            End Using
        End Using
    End Function

    ''' <summary>The CLI's tools (Write, Read, WebFetch) on two research messages and a plain one: the harness writes the notes (approved, so the app does not ask) to one path.</summary>
    <Fact>
    Public Async Function ResearchAnswersAreWrittenToTheWorkspaceAndAttached() As Task
        Await WithResearchServerAsync(1, Async Function(s)
                                             Dim id = Await ChatFeatureTests.NewChatAsync(s.Admin, New With {.model = "fast/a"})
                                             Await s.Send(id, "What is example.com for?", Nothing)
                                             Await s.Send(id, "And who runs it?", Nothing)
                                             Await s.Send(id, "hello", Nothing)
                                             Assert.Equal(2, s.Writes.Count)
                                             Assert.True(s.Writes.All(Function(w) w.Approved))
                                             Assert.Equal("research/what-is-example-com-for.md", s.Writes(0).Path)
                                             ' The chat got a better name in between; the notes stay in one file.
                                             Assert.Equal(s.Writes(0).Path, s.Writes(1).Path)
                                             Assert.Contains("## 1. What is example.com for?", s.Writes(1).Content)
                                             Assert.Contains("## 2. And who runs it?", s.Writes(1).Content)
                                             Assert.DoesNotContain("## 3.", s.Writes(1).Content)
                                             Assert.Contains("<summary>https://example.com/0</summary>", s.Writes(1).Content)
                                             Assert.Equal(2, s.Files.Count)
                                             Assert.True(s.Files.All(Function(f) f = "what-is-example-com-for.md"), String.Join(", ", s.Files))
                                             ' The answer keeps its notes as a download.
                                             Dim chat = Await ChatFeatureTests.GetChatAsync(s.Admin, id)
                                             Dim answers = chat("messages").AsArray().Where(Function(m) m("role")?.ToString() = "assistant").ToList()
                                             Assert.Equal(1, answers(0)("attachments").AsArray().Count)
                                             Assert.Equal(0, answers(2)("attachments").AsArray().Count)

                                             ' A branch writes its own notes, never over the original's.
                                             Dim branch = (Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(s.Admin.Client, "POST", $"/api/chat/conversations/{id}/fork",
                                                                                                                    New With {.title = "Other angle"}, s.Admin.Csrf)))("id").ToString()
                                             Await s.Send(branch, "Who else uses it?", Nothing)
                                             Assert.Equal("research/other-angle.md", s.Writes.Last().Path)
                                             Assert.Contains("## 3. Who else uses it?", s.Writes.Last().Content)
                                         End Function)
    End Function

    ''' <summary>A long research answer writes its notes while it works (every 8 lookups), then once more when done.</summary>
    <Fact>
    Public Async Function LongResearchIsSavedWhileItWorks() As Task
        Await WithResearchServerAsync(9, Async Function(s)
                                             Dim id = Await ChatFeatureTests.NewChatAsync(s.Admin, New With {.model = "fast/a"})
                                             Await s.Send(id, "Dig into example.com", Nothing)
                                             Assert.Equal(2, s.Writes.Count)
                                             Assert.Contains("**Still researching:**", s.Writes(0).Content)
                                             Assert.DoesNotContain("**Still researching:**", s.Writes(1).Content)
                                             Assert.Equal(s.Writes(0).Path, s.Writes(1).Path)
                                             Assert.Single(s.Files)
                                         End Function)
    End Function

    ''' <summary>/notes off keeps a chat out of it; a client's subagent conversation (the CLI's Task) never writes its own notes.</summary>
    <Fact>
    Public Async Function NotesOffAndSubagentChatsWriteNothing() As Task
        Await WithResearchServerAsync(1, Async Function(s)
                                             Dim off = Await ChatFeatureTests.NewChatAsync(s.Admin, New With {.model = "fast/a"})
                                             Await s.Send(off, "What is example.com for?", False)
                                             Dim agent = Await ChatFeatureTests.NewChatAsync(s.Admin, New With {.model = "fast/a", .title = "Subagent: look up example.com"})
                                             Await s.Send(agent, "What is example.com for?", Nothing)
                                             Assert.Empty(s.Writes)
                                             Assert.Empty(s.Files)
                                         End Function)
    End Function

End Class
