Option Strict On
Option Explicit On
Option Infer On

Imports System.IO
Imports System.Net
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Mcp
Imports Agentic.Work
Imports Xunit

''' <summary>
''' What the Chat tab gained from the CLI, the desktop app and VS Code: chats imported from a file, a quiet /heartbeat,
''' Stable Diffusion and Suno songs in chat, the user's own subagent types, and Mermaid diagrams drawn by the server.
''' </summary>
Public Class ChatGapsTests

    Private Const OwnExport As String = "{""Title"":""Trip plan"",""Model"":""fast/a"",""Messages"":[" &
        "{""Role"":""user"",""Content"":""Plan a trip"",""CreatedUtc"":""2026-10-01T10:00:00Z""}," &
        "{""Role"":""assistant"",""Content"":""Day 1: Rome"",""CreatedUtc"":""2026-10-01T10:00:05Z""}," &
        "{""Role"":""tool"",""Content"":""(left out)""}]}"

    Private Const ChatGptExport As String = "[{""title"":""Soup"",""current_node"":""c"",""mapping"":{" &
        """a"":{""message"":null,""parent"":null,""children"":[""b""]}," &
        """b"":{""message"":{""author"":{""role"":""user""},""content"":{""parts"":[""Best soup?""]},""create_time"":1760000000},""parent"":""a"",""children"":[""c"",""x""]}," &
        """x"":{""message"":{""author"":{""role"":""assistant""},""content"":{""parts"":[""An answer that was regenerated""]}},""parent"":""b"",""children"":[]}," &
        """c"":{""message"":{""author"":{""role"":""assistant""},""content"":{""parts"":[""Pho.""]},""create_time"":1760000010},""parent"":""b"",""children"":[]}}}," &
        "{""name"":""Code"",""chat_messages"":[{""sender"":""human"",""text"":""Fix my loop""},{""sender"":""assistant"",""content"":[{""type"":""text"",""text"":""Use break.""}]}]}]"

    <Fact>
    Public Sub ExportsOfThisAppChatGptAndClaudeAiAreRead()
        Dim own = ChatService.ParseImport(OwnExport)
        Assert.Single(own)
        Assert.Equal("Trip plan", own(0).Title)
        Assert.Equal({"user", "assistant"}, own(0).Messages.Select(Function(m) m.Role).ToArray())
        Assert.Equal(New DateTime(2026, 10, 1, 10, 0, 5, DateTimeKind.Utc), own(0).Messages(1).CreatedUtc.Value.ToUniversalTime())

        Dim two = ChatService.ParseImport(ChatGptExport)
        Assert.Equal(2, two.Count)
        ' ChatGPT: the branch that ends at current_node, not the regenerated answer.
        Assert.Equal({"Best soup?", "Pho."}, two(0).Messages.Select(Function(m) m.Content).ToArray())
        Assert.Equal("Soup", two(0).Title)
        ' Claude.ai: human and assistant, text in parts too.
        Assert.Equal({"user", "assistant"}, two(1).Messages.Select(Function(m) m.Role).ToArray())
        Assert.Equal("Use break.", two(1).Messages(1).Content)

        Assert.Empty(ChatService.ParseImport("not json"))
        Assert.Empty(ChatService.ParseImport("{""hello"":1}"))
    End Sub

    <Fact>
    Public Async Function AnImportedFileBecomesChats() As Task
        Using up As New FakeUpstream()
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim res = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/import?model=fast/a", JsonNode.Parse(ChatGptExport), admin.Csrf)
                Assert.Equal(HttpStatusCode.Created, res.StatusCode)
                Dim made = (Await ChatFeatureTests.JsonOf(res))("chats").AsArray()
                Assert.Equal(2, made.Count)
                Dim chat = Await ChatFeatureTests.GetChatAsync(admin, made(0)("id").ToString())
                Assert.Equal("Soup", chat("title").ToString())
                Assert.Equal({"Best soup?", "Pho."}, chat("messages").AsArray().Select(Function(m) m("content").ToString()).ToArray())
                Assert.Equal("fast/a", chat("model").ToString())
                Dim bad = Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/import", New With {.x = 1}, admin.Csrf)
                Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode)
                ' Archived chats leave the list and come back.
                Dim id = made(1)("id").ToString()
                Await PlatformApiTests.Send(admin.Client, "PATCH", "/api/chat/conversations/" & id, New With {.archived = True}, admin.Csrf)
                Assert.DoesNotContain(id, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/conversations"))).AsArray().Select(Function(c) c("id").ToString()))
                Assert.Contains(id, (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/conversations?archived=1"))).AsArray().Select(Function(c) c("id").ToString()))
            End Using
        End Using
    End Function

    <Theory>
    <InlineData("HEARTBEAT_OK", True)>
    <InlineData("  `HEARTBEAT_OK`. ", True)>
    <InlineData("**HEARTBEAT_OK**", True)>
    <InlineData("HEARTBEAT_OK, but the disk is nearly full", False)>
    <InlineData("All good", False)>
    Public Sub OnlyAnAnswerThatIsJustHeartbeatOkIsQuiet(answer As String, quiet As Boolean)
        Assert.Equal(quiet, ChatScheduleStore.IsHeartbeatOk(answer))
    End Sub

    <Fact>
    Public Async Function AHeartbeatThatFindsNothingLeavesTheChat() As Task
        Using up As New FakeUpstream()
            Dim asked = 0
            up.StreamOverride = Function(req)
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return ChatFeatureTests.TextStream(If(Interlocked.Increment(asked) = 1, "HEARTBEAT_OK", "The backup failed last night."), "stop")
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim prompt = ChatScheduleStore.HeartbeatPrefix & " Go through the checklist below. If nothing needs attention, reply with exactly HEARTBEAT_OK." & vbLf & vbLf & "<heartbeat>" & vbLf & "- backups" & vbLf & "</heartbeat>"
                Dim s = Await ChatFeatureTests.JsonOf(Await PlatformApiTests.Send(admin.Client, "POST", "/api/chat/schedules", New With {.chatId = id, .when = "every 30m", .prompt = prompt}, admin.Csrf))
                Dim sid = s("id").ToString()
                ' Nothing needs attention: the check and its HEARTBEAT_OK leave the chat.
                Assert.True((Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/schedules/{sid}/run", New With {.x = 1}, admin.Csrf)).IsSuccessStatusCode)
                Dim first As JsonNode = Nothing
                For i = 1 To 150
                    first = JsonNode.Parse(Await admin.Client.GetStringAsync($"/api/chat/schedules?chatId={id}")).AsArray().Single()
                    If If(first("lastStatus")?.ToString(), "").StartsWith("ok") Then Exit For
                    Await Task.Delay(100)
                Next
                Assert.Equal("ok: nothing needed attention", first("lastStatus").ToString())
                Assert.Empty((Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray())
                ' Something does: it stays.
                Assert.True((Await PlatformApiTests.Send(admin.Client, "POST", $"/api/chat/schedules/{sid}/run", New With {.x = 1}, admin.Csrf)).IsSuccessStatusCode)
                Dim msgs As JsonArray = Nothing
                For i = 1 To 150
                    msgs = (Await ChatFeatureTests.GetChatAsync(admin, id))("messages").AsArray()
                    If msgs.Count >= 2 Then Exit For
                    Await Task.Delay(100)
                Next
                Assert.Equal(2, msgs.Count)
                Assert.Equal("The backup failed last night.", msgs(1)("content").ToString())
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function StableDiffusionPicturesAndSunoSongsAreMadeInChat() As Task
        Dim poll = ChatService.MediaPollInterval
        ChatService.MediaPollInterval = TimeSpan.FromMilliseconds(100)
        Try
            Using up As New FakeUpstream(), sd As New FakeSd(), suno As New FakeSuno()
                Dim asked = 0
                up.StreamOverride = Function(req)
                                        If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                        Select Case Interlocked.Increment(asked)
                                            Case 1 : Return ChatFeatureTests.ToolCallStream("stable_diffusion", "{""prompt"":""a red fox in snow"",""num_images"":1}")
                                            Case 2 : Return ChatFeatureTests.TextStream("Here is your fox.", "stop")
                                            Case 3 : Return ChatFeatureTests.ToolCallStream("make_song", "{""prompt"":""a song about foxes""}")
                                            Case Else : Return ChatFeatureTests.TextStream("Here is your song.", "stop")
                                        End Select
                                    End Function
                Dim env = Await PlatformApiTests.StartAsync(up, Sub(o)
                                                                    o.StableDiffusion.BaseUrl = sd.BaseUrl
                                                                    o.StableDiffusion.ApiKey = "sd-key"
                                                                    o.StableDiffusion.PollMilliseconds = 100
                                                                    o.Music.BaseUrl = suno.BaseUrl
                                                                    o.Music.ApiKey = "suno-key"
                                                                    o.Music.PollSeconds = 1
                                                                End Sub)
                Using env.Host
                    Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                    Dim meta = Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/chat/meta"))
                    Assert.True(meta("diffusion").GetValue(Of Boolean)())
                    Assert.True(meta("music").GetValue(Of Boolean)())
                    Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                    Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Draw a fox", .model = "fast/a", .settings = New With {.diffusion = True}})
                    Dim requests = up.Requests.Where(Function(r) ChatFeatureTests.Streamed(r)).ToList()
                    Assert.Contains("stable_diffusion", requests(0)("tools").AsArray().Select(Function(t) t("function")("name").ToString()))
                    Assert.DoesNotContain("make_song", requests(0)("tools").AsArray().Select(Function(t) t("function")("name").ToString()))
                    Dim result = requests(1)("messages").AsArray().Last(Function(m) m("role").ToString() = "tool")("content").ToString()
                    Assert.Contains("Stable Diffusion made 1 picture", result)
                    Assert.Equal("a red fox in snow", sd.Jobs.Last()("prompt").ToString())
                    Dim chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                    Dim att = chat("messages").AsArray().Last()("attachments").AsArray().Single()
                    Assert.Equal("image", att("kind").ToString())
                    Assert.Contains("Stable Diffusion", att("summary").ToString())
                    Dim file = Await admin.Client.GetAsync("/api/chat/files/" & att("uploadId").ToString())
                    Assert.Equal(HttpStatusCode.OK, file.StatusCode)
                    ' The picture is in the Images gallery too.
                    Assert.NotEmpty((Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/images/gallery"))).AsArray())

                    Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Now a song", .model = "fast/a", .settings = New With {.music = True}})
                    chat = Await ChatFeatureTests.GetChatAsync(admin, id)
                    Dim songs = chat("messages").AsArray().Last()("attachments").AsArray()
                    Assert.NotEmpty(songs)
                    Assert.All(songs, Sub(s) Assert.Equal("song", s("kind").ToString()))
                    Dim library = (Await ChatFeatureTests.JsonOf(Await admin.Client.GetAsync("/api/music/songs"))).AsArray()
                    Assert.Contains(songs(0)("uploadId").ToString(), library.Select(Function(s) s("id").ToString()))
                End Using
            End Using
        Finally
            ChatService.MediaPollInterval = poll
        End Try
    End Function

    <Fact>
    Public Sub AgentFilesAreReadAndTheirToolNamesMatchOurs()
        Dim b = JsonDocument.Parse("{""clientAgents"":[{""name"":""code reviewer"",""description"":""Reviews"",""prompt"":""You review."",""tools"":[""Read"",""Grep""]}," &
                                   "{""name"":""explore"",""prompt"":""clash""},{""name"":""Code-Reviewer"",""prompt"":""twice""},{""name"":""empty""}]}").RootElement
        Dim agents = ChatService.ReadClientAgents(b)
        Assert.Single(agents)
        Assert.Equal("code-reviewer", agents(0).Name)
        Assert.True(ChatService.AgentMayUse(agents(0).Tools, "read_file", "client"))
        Assert.True(ChatService.AgentMayUse(agents(0).Tools, "grep", "client"))
        Assert.False(ChatService.AgentMayUse(agents(0).Tools, "write_file", "client"))
        Assert.True(ChatService.AgentMayUse(New List(Of String) From {"Bash"}, "kill_command", "client"))
        Assert.True(ChatService.AgentMayUse(New List(Of String) From {"mcp__gitea"}, "gitea__list_repos", "gitea"))
        Assert.True(ChatService.AgentMayUse(New List(Of String)(), "anything", "x"))
    End Sub

    <Fact>
    Public Async Function TheUsersOwnAgentTypeWorksByItsFileWithOnlyItsTools() As Task
        Using up As New FakeUpstream()
            Dim asked = 0
            Dim subRequests As New Concurrent.ConcurrentQueue(Of JsonNode)
            up.StreamOverride = Function(req)
                                    If ChatFeatureTests.IsSubagent(req) Then
                                        subRequests.Enqueue(req)
                                        Return ChatFeatureTests.TextStream("Report: no bugs found.", "stop")
                                    End If
                                    If Not ChatFeatureTests.Streamed(req) Then Return Nothing
                                    Return If(Interlocked.Increment(asked) = 1,
                                              ChatFeatureTests.ToolCallStream("task", "{""description"":""review"",""subagent_type"":""reviewer"",""prompt"":""Check app.js""}"),
                                              ChatFeatureTests.TextStream("The reviewer found nothing.", "stop"))
                                End Function
            Dim env = Await PlatformApiTests.StartAsync(up)
            Using env.Host
                Dim admin = Await PlatformApiTests.LoginAsync(env.Port, "admin", "pw-123456789")
                Dim id = Await ChatFeatureTests.NewChatAsync(admin, New With {.model = "fast/a"})
                Dim tool = Function(name As String) New With {.name = name, .description = name, .parameters = New With {.type = "object", .properties = New With {.path = New With {.type = "string"}}}}
                Await ChatFeatureTests.AskAsync(admin, id, New With {.content = "Review my code", .model = "fast/a",
                    .clientTools = {tool("read_file"), tool("write_file")},
                    .clientAgents = {New With {.name = "reviewer", .description = "Reviews code for bugs", .prompt = "You review code for bugs and report them.", .tools = {"Read"}}}})
                Dim first = up.Requests.First(Function(r) ChatFeatureTests.Streamed(r))
                Dim task = first("tools").AsArray().Single(Function(t) t("function")("name").ToString() = "task")("function")
                Assert.Contains("reviewer", task("parameters")("properties")("subagent_type")("enum").AsArray().Select(Function(x) x.ToString()))
                Assert.Contains("reviewer — Reviews code for bugs", task("description").ToString())
                Dim sub1 = subRequests.First()
                Assert.Contains("You are the user's ""reviewer"" agent", sub1("messages")(0)("content").ToString())
                Assert.Contains("You review code for bugs and report them.", sub1("messages")(0)("content").ToString())
                Dim names = sub1("tools").AsArray().Select(Function(t) t("function")("name").ToString()).ToList()
                Assert.Contains("read_file", names)
                Assert.DoesNotContain("write_file", names)
            End Using
        End Using
    End Function

    <Fact>
    Public Async Function MermaidIsFoundFromTheDataServerAndDrawnAsSvg() As Task
        Dim servers As New List(Of McpServerOptions) From {
            New McpServerOptions With {.Id = "other", .Arguments = New List(Of String) From {"--x"}},
            New McpServerOptions With {.Id = "data", .Arguments = New List(Of String) From {"--mcp-data", "--node", "n.exe", "--mermaid-cli", "cli.js", "--chrome", "c.exe"}}}
        Dim m = MermaidCli.FromMcpServers(servers)
        Assert.Equal("n.exe", m.Node)
        Assert.Equal("cli.js", m.Cli)
        Assert.Equal("c.exe", m.Chrome)
        Assert.False(m.Available)
        Assert.Null(MermaidCli.FromMcpServers(New List(Of McpServerOptions)))
        ' On a computer with the portable Node and mermaid-cli of this repository (and Chrome), a diagram is really drawn.
        Dim root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."))
        Dim real As New MermaidCli(Path.Combine(root, "tools", "node", "node.exe"), Path.Combine(root, "tools", "mermaid-cli", "node_modules", "@mermaid-js", "mermaid-cli", "src", "cli.js"),
                                   "C:\Program Files\Google\Chrome\Application\chrome.exe")
        If Not real.Available OrElse Not File.Exists(real.Chrome) Then Return
        Dim svg = Text.Encoding.UTF8.GetString(Await real.RenderAsync("graph TD; A[Start]-->B[End]", ".svg", "dark", "transparent", CancellationToken.None))
        Assert.Contains("<svg", svg)
        Assert.Contains("Start", svg)
        Await Assert.ThrowsAsync(Of MermaidException)(Function() real.RenderAsync("graph TD; A-->", ".svg", "default", "white", CancellationToken.None))
    End Function

End Class
