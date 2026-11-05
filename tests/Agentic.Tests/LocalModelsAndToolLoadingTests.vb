Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' After the LedgerLite audit (2026-10-31): agent work is shared between both local models all the time; agents load MCP
''' tool descriptions only when they need them; token limits count cloud tokens only and never stop the local models.
''' </summary>
Public Class LocalModelsAndToolLoadingTests

    ' ------------------------------------------------------------------ sharing the local models

    Private Shared Function Choices(mainTasksInUse As Integer, otherInUse As Integer) As List(Of LocalBalancer.Choice)
        Return New List(Of LocalBalancer.Choice) From {
            New LocalBalancer.Choice With {.Key = "local-main", .Capacity = 4, .InUse = mainTasksInUse},
            New LocalBalancer.Choice With {.Key = "local-vision", .Capacity = 3, .InUse = otherInUse}}
    End Function

    <Fact>
    Public Sub FourTasksAreSharedTwoAndTwoAndStayWhereTheyStarted()
        Dim clock As New FakeClock()
        Dim b As New LocalBalancer(clock)
        Dim window = TimeSpan.FromMinutes(10)
        Dim placed = {"t1", "t2", "t3", "t4"}.ToDictionary(Function(t) t, Function(t) b.Pick(t, Choices(0, 0), window))
        Assert.Equal(2, placed.Values.Where(Function(m) m = "local-main").Count())
        Assert.Equal(2, placed.Values.Where(Function(m) m = "local-vision").Count())
        ' Their next calls stay where their prompts are cached.
        For Each kv In placed
            Assert.Equal(kv.Value, b.Pick(kv.Key, Choices(1, 1), window))
        Next
        ' Its model full while the other has room: the task moves.
        Dim onVision = placed.First(Function(kv) kv.Value = "local-vision").Key
        Assert.Equal("local-main", b.Pick(onVision, Choices(1, 3), window))
        ' Tasks that stopped calling no longer count.
        clock.Advance(TimeSpan.FromMinutes(11))
        Assert.Empty(b.TasksPerModel(window))
        Assert.Equal("local-main", b.Pick("new", Choices(0, 0), window))
    End Sub

    <Fact>
    Public Async Function TheGatewaySharesAgentCallsBetweenTheLocalModels() As Task
        Dim p As New FakeAIProvider()
        p.Models.Add("local-2-model")
        p.Delay = Function(r) TimeSpan.FromMilliseconds(600)
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.Models.Add(New ModelOptions With {.Key = "local-2", .ModelId = "local-2-model", .Tier = 1, .IsLocal = True, .MaxConcurrency = 3,
                                                       .Capabilities = New List(Of ModelCapability) From {ModelCapability.ToolCalling}, .MaxTokens = 1000})
                                                   o.Routing.Spillover.LocalCandidates = New List(Of String) From {"local-2"}
                                               End Sub, p)
            ' One profile; a caller per task, so the agent's own concurrency limit does not line the calls up.
            Dim profile = host.H.Profiles.TryGetProfile("A")
            Dim ask = Function(task As String) host.H.Gateway.ChatAsync(New AiCallContext With {.AgentId = "W-" & task, .TaskId = task, .Profile = profile, .LadderLevel = 0},
                                                                        New List(Of ChatMessage) From {ChatMessage.ForUser("hi")}, Nothing, CancellationToken.None)
            Dim first = Await Task.WhenAll({"t1", "t2", "t3", "t4"}.Select(Function(t) ask(t)))
            Assert.Equal(2, first.Count(Function(o) o.ModelKey = "local"))
            Assert.Equal(2, first.Count(Function(o) o.ModelKey = "local-2"))
            ' One at a time now: every task stays on its model.
            For i = 0 To 3
                Assert.Equal(first(i).ModelKey, (Await ask("t" & (i + 1))).ModelKey)
            Next
        End Using
    End Function

    ' ------------------------------------------------------------------ token limits: cloud only

    <Fact>
    Public Async Function TokenLimitsCountCloudTokensOnlyAndNeverStopTheLocalModel() As Task
        Using host = Await TestHost.StartAsync(Sub(o) o.Agents.First(Function(a) a.Id = "A").Ai = New AiPolicyOptions With {.MaxTokensPerDay = 1000})
            Dim profile = host.H.Profiles.TryGetProfile("A")
            Dim ask = Function(level As Integer) host.H.Gateway.ChatAsync(New AiCallContext With {.AgentId = "A", .Profile = profile, .LadderLevel = level},
                                                                          New List(Of ChatMessage) From {ChatMessage.ForUser("hi")}, Nothing, CancellationToken.None)
            ' Each call is 1100 tokens: three local calls would be over the limit if they counted.
            For i = 1 To 3
                Assert.Equal("local", (Await ask(0)).ModelKey)
            Next
            Assert.Equal("cloud-a", (Await ask(1)).ModelKey)
            ' The cloud tokens are used up: cloud calls stop, the local model goes on.
            Dim ex = Await Assert.ThrowsAsync(Of AiException)(Function() ask(1))
            Assert.Equal(AiErrorKind.QuotaExceeded, ex.Kind)
            Assert.Contains("cloud models", ex.Message)
            Assert.Equal("local", (Await ask(0)).ModelKey)
        End Using
    End Function

    ' ------------------------------------------------------------------ MCP tools loaded on demand

    Private NotInheritable Class FakeMcpTool
        Implements IAgentTool
        Public Sub New(server As String, tool As String, description As String)
            Name = $"mcp__{server}__{tool}"
            Me.Description = description
        End Sub
        Public ReadOnly Property Name As String Implements IAgentTool.Name
        Public ReadOnly Property Description As String Implements IAgentTool.Description
        Public ReadOnly Property Parameters As JsonObject = Schema.Obj({"path"}, Schema.Prop("path", Schema.Str("Where."))) Implements IAgentTool.Parameters
        Public ReadOnly Property Source As ToolSource = ToolSource.Mcp Implements IAgentTool.Source
        Public ReadOnly Property IsIdempotent As Boolean = True Implements IAgentTool.IsIdempotent
        Public ReadOnly Property RequiresApproval As Boolean = False Implements IAgentTool.RequiresApproval
        Public ReadOnly Property Timeout As TimeSpan = TimeSpan.FromSeconds(10) Implements IAgentTool.Timeout
        Public Function ExecuteAsync(ctx As ToolInvocationContext, args As JsonElement) As Task(Of ToolResult) Implements IAgentTool.ExecuteAsync
            Return Task.FromResult(ToolResult.Ok("ran " & Name))
        End Function
    End Class

    Private Shared Function Registry(Optional giteaTools As Integer = 40) As AgentToolRegistry
        Dim reg As New AgentToolRegistry()
        reg.Add(New CompleteTaskTool())
        reg.Add(New FakeMcpTool("code-exec", "run_command", "Run a shell command."))
        reg.Add(New FakeMcpTool("code-exec", "read_file", "Read a file."))
        reg.Add(New FakeMcpTool("browser", "browser_navigate", "Open a web page in the browser."))
        For i = 1 To giteaTools
            reg.Add(New FakeMcpTool("gitea", "tool_" & i.ToString("00"), "A Gitea operation. " & New String("x"c, 300)))
        Next
        reg.Add(New FakeMcpTool("gitea", "list_pull_requests", "List the pull requests of a repository, open or closed."))
        Return reg
    End Function

    <Fact>
    Public Sub ATaskStartsWithItsCoreToolsAndTheNamesOfTheRest()
        Dim reg = Registry()
        Dim opts As New ToolLoadingOptions()
        Dim core = ToolLoading.CoreTools(reg, opts, {"mcp__browser__browser_navigate"})
        Assert.Equal({"mcp__browser__browser_navigate", "mcp__code-exec__read_file", "mcp__code-exec__run_command"}, core.OrderBy(Function(n) n).ToArray())
        Dim cp As New TaskCheckpoint With {.ToolLoading = True, .LoadedTools = core}
        Dim offered = ReasoningTaskHandler.ToolsForCall(reg, cp).Select(Function(d) d.Name).ToList()
        Assert.Contains("complete_task", offered)                 ' host tools always
        Assert.Contains("mcp__code-exec__run_command", offered)
        Assert.DoesNotContain("mcp__gitea__tool_01", offered)      ' named only
        ' The catalog names the rest by server, in short form.
        Dim catalog = ToolLoading.Catalog(reg, core)
        Assert.Contains("- gitea: list_pull_requests, tool_01", catalog)
        Assert.DoesNotContain("run_command", catalog)
        ' A task from before ToolLoading, or one without much to defer, keeps every tool.
        Assert.Equal(reg.Count, ReasoningTaskHandler.ToolsForCall(reg, New TaskCheckpoint()).Count)
        Assert.Null(ToolLoading.CoreTools(Registry(giteaTools:=2), opts, Nothing))
        Assert.Null(ToolLoading.CoreTools(reg, New ToolLoadingOptions With {.Enabled = False}, Nothing))
    End Sub

    ''' <summary>An MCP server with many tools (long descriptions) and the one the agent needs.</summary>
    Private NotInheritable Class ManyToolsMcpFactory
        Implements Mcp.IMcpClientFactory
        Public ReadOnly Calls As New Concurrent.ConcurrentQueue(Of String)
        Public Function Create(server As McpServerOptions) As Mcp.IMcpClient Implements Mcp.IMcpClientFactory.Create
            Return New Client(Me)
        End Function
        Private NotInheritable Class Client
            Implements Mcp.IMcpClient
            Private ReadOnly _f As ManyToolsMcpFactory
            Public Sub New(f As ManyToolsMcpFactory)
                _f = f
            End Sub
            Public Function ConnectAsync(ct As CancellationToken) As Task Implements Mcp.IMcpClient.ConnectAsync
                Return Task.CompletedTask
            End Function
            Public Function ListToolsAsync(ct As CancellationToken) As Task(Of List(Of Mcp.McpToolInfo)) Implements Mcp.IMcpClient.ListToolsAsync
                Dim schema = "{""type"":""object"",""properties"":{""path"":{""type"":""string""}}}"
                Dim list = Enumerable.Range(1, 40).Select(Function(i) New Mcp.McpToolInfo With {.ServerId = "big", .Name = "tool_" & i.ToString("00"), .Description = "Something. " & New String("x"c, 300), .InputSchemaJson = schema}).ToList()
                list.Add(New Mcp.McpToolInfo With {.ServerId = "big", .Name = "wanted_tool", .Description = "The tool this task needs.", .InputSchemaJson = schema})
                Return Task.FromResult(list)
            End Function
            Public Function CallToolAsync(name As String, arguments As JsonElement, ct As CancellationToken) As Task(Of Mcp.McpCallResult) Implements Mcp.IMcpClient.CallToolAsync
                _f.Calls.Enqueue(name)
                Return Task.FromResult(New Mcp.McpCallResult With {.Text = "wanted result"})
            End Function
            Public Function PingAsync(ct As CancellationToken) As Task Implements Mcp.IMcpClient.PingAsync
                Return Task.CompletedTask
            End Function
            Public Function DisposeAsync() As ValueTask Implements IAsyncDisposable.DisposeAsync
                Return ValueTask.CompletedTask
            End Function
        End Class
    End Class

    <Fact>
    Public Async Function AnAgentLoadsTheToolItNeedsAndThenUsesIt() As Task
        Dim f As New ManyToolsMcpFactory()
        Dim p As New FakeAIProvider()
        Dim seen As New Concurrent.ConcurrentQueue(Of (Tools As List(Of String), System As String))
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Complete("ok")
                          Dim tools = If(r.Tools, New List(Of ToolDefinition)).Select(Function(d) d.Name).ToList()
                          seen.Enqueue((tools, If(r.Messages.FirstOrDefault(Function(m) m.Role = "system")?.Content, "")))
                          If tools.Contains("mcp__big__wanted_tool") Then
                              If r.Messages.Any(Function(m) m.Role = "tool" AndAlso If(m.Content, "").Contains("wanted result")) Then Return FakeAIProvider.Complete("done with the wanted tool")
                              Return FakeAIProvider.Tool("mcp__big__wanted_tool", "{""path"":""x""}")
                          End If
                          Return FakeAIProvider.Tool("load_tools", "{""names"":[""big:wanted_tool""]}")
                      End Function
        Using host = Await TestHost.StartAsync(Sub(o)
                                                   o.McpServers.Add(New McpServerOptions With {.Id = "big", .Name = "Big", .Transport = McpTransportKind.Http, .Endpoint = "http://big.local/mcp"})
                                                   o.Agents.First(Function(a) a.Id = "A").AllowedMcpServers = New List(Of String) From {"big"}
                                               End Sub, p, mcpFactory:=f)
            Dim mcpTools = Function() host.H.ToolCatalog.BuildForAgent(host.H.Agents.GetEffectiveProfile("A")).All().Where(Function(t) t.Source = ToolSource.Mcp).Count()
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(Of Boolean)(mcpTools() >= 41), 30), "the MCP tools never appeared")
            Dim r = Await host.H.Agents.SubmitTaskAsync("A", New NewTaskRequest With {.Name = "Use the tool", .Description = "Use wanted_tool on x."})
            Assert.True(r.Success, r.Message)
            Dim done = Await TestHost.WaitUntilAsync(Async Function() (Await host.H.Tasks.GetAsync(r.TaskId)).Status = AgentTaskStatus.Completed, 60)
            Dim tk = Await host.H.Tasks.GetAsync(r.TaskId)
            Assert.True(done, $"task {tk.Status}: {tk.ErrorMessage}")
            Dim calls = seen.ToList()
            ' First call: the host tools and load_tools, not the 41 MCP tools; the system prompt names them.
            Assert.Contains("load_tools", calls(0).Tools)
            Assert.DoesNotContain(calls(0).Tools, Function(n) n.StartsWith("mcp__big__"))
            Assert.Contains("More tools (not loaded yet)", calls(0).System)
            Assert.Contains("- big: tool_01, tool_02", calls(0).System)
            ' After load_tools it is offered - and only it - and the agent used it.
            Assert.Contains(calls, Function(c) c.Tools.Contains("mcp__big__wanted_tool") AndAlso Not c.Tools.Contains("mcp__big__tool_01"))
            Assert.Contains("wanted_tool", f.Calls)
        End Using
    End Function

    <Fact>
    Public Sub AgentsReadTheNotesOfTheirToolServers()
        Dim pb As New PromptBuilder(Nothing, Nothing) With {.McpServerNotes = New List(Of (String, String, String)) From {
            ("code-exec", "Code execution", "git is signed in to Gitea: clone and push."), ("netops", "Network", "Never scan outside the LAN.")}}
        Dim section = pb.ToolServerSection(Registry())
        Assert.Contains("- Code execution (code-exec): git is signed in to Gitea: clone and push.", section)
        Assert.DoesNotContain("netops", section)                                  ' the agent has none of its tools
        Assert.Null(New PromptBuilder(Nothing, Nothing).ToolServerSection(Registry()))
    End Sub

    <Fact>
    Public Async Function LoadToolsFindsToolsByNameOrQuery() As Task
        Dim reg = Registry()
        Dim tool As New LoadToolsTool(reg)
        Dim run = Function(json As String) tool.ExecuteAsync(Nothing, JsonDocument.Parse(json).RootElement)
        Dim byName = Await run("{""names"":[""gitea:tool_05"",""mcp__gitea__tool_06"",""list_pull_requests"",""gitea:nope""]}")
        Assert.True(byName.Success)
        Assert.Equal({"mcp__gitea__tool_05", "mcp__gitea__tool_06", "mcp__gitea__list_pull_requests"}, byName.LoadTools.ToArray())
        Assert.Contains("(arguments: path)", byName.Content)
        Assert.Contains("Not found: gitea:nope", byName.Content)
        Dim byQuery = Await run("{""query"":""pull requests""}")
        Assert.Equal("mcp__gitea__list_pull_requests", byQuery.LoadTools(0))
        Dim none = Await run("{""query"":""zzz""}")
        Assert.False(none.Success)
        Assert.Null(none.LoadTools)
    End Function

End Class
