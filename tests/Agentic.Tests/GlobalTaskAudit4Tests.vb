Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.AI
Imports Agentic.Configuration
Imports Agentic.Utilities
Imports Agentic.Work
Imports Xunit

''' <summary>
''' The rest of the global-task audit (2026-11-03): the same long output again (an unchanged file read again) is not
''' repeated while it is still in the transcript; shell commands that only read count as looking around; a step whose
''' models have not worked for half an hour is reported to the administrators.
''' </summary>
Public Class GlobalTaskAudit4Tests

    Private Shared Function Call_(id As String, name As String, args As String) As ToolCallRequest
        Return New ToolCallRequest With {.Id = id, .Name = name, .ArgumentsJson = args}
    End Function

    Private Shared Sub AddRead(t As List(Of ChatMessage), id As String, content As String)
        Dim c = Call_(id, "mcp__code-exec__run_command", "{""command"":""sed -n '1,200p' service.py""}")
        t.Add(ChatMessage.ForAssistant(Nothing, New List(Of ToolCallRequest) From {c}))
        t.Add(ChatMessage.ForTool(c.Id, c.Name, ContextCompactor.RepeatOrOutput(t, c.Name, content, c.Id)))
    End Sub

    Private Shared ReadOnly FileText As String = String.Join(vbLf, Enumerable.Range(1, 200).Select(Function(i) $"line {i} of service.py"))

    <Fact>
    Public Sub TheSameLongOutputIsNotRepeatedWhileItIsStillThere()
        Dim t As New List(Of ChatMessage) From {ChatMessage.ForSystem("system"), ChatMessage.ForUser("the task")}
        AddRead(t, "c1", FileText)
        AddRead(t, "c2", FileText)
        Assert.Equal(FileText, t(3).Content)
        Assert.StartsWith("[host] Same output as the run_command call c1 earlier in this conversation", t(5).Content)
        Assert.True(t(5).Content.Length < 300)
        ' A short output is always repeated; a different one too.
        Assert.Equal("ok", ContextCompactor.RepeatOrOutput(t, "x", "ok"))
        Assert.Equal(FileText & "!", ContextCompactor.RepeatOrOutput(t, "x", FileText & "!"))
        ' A server that reuses call ids: no note, because "call c1" would not name one output.
        AddRead(t, "c1", FileText)
        Assert.Equal(FileText, t(7).Content)
    End Sub

    <Fact>
    Public Sub WhenTheFirstCopyIsDroppedTheNoteGetsTheOutput()
        ' The first read goes when the context is compacted; the note that pointed at it now holds the whole output.
        Dim t As New List(Of ChatMessage) From {ChatMessage.ForSystem("system"), ChatMessage.ForUser("the task")}
        AddRead(t, "c1", FileText)
        AddRead(t, "c2", FileText)
        AddRead(t, "c3", FileText)
        For i = 0 To 70
            Dim c = Call_("p" & i, "update_plan", "{}")
            t.Add(ChatMessage.ForAssistant(Nothing, New List(Of ToolCallRequest) From {c}))
            t.Add(ChatMessage.ForTool(c.Id, c.Name, "plan " & i))
        Next
        Assert.True(ContextCompactor.Compact(t, 32000, 60))
        Assert.DoesNotContain(t, Function(m) m.ToolCallId = "c1")
        ' Every copy that is left is either the output or points at a message that is still there.
        For Each m In t.Where(Function(x) x.Role = "tool" AndAlso x.ToolCallId IsNot Nothing AndAlso x.ToolCallId.StartsWith("c"))
            If m.Content.StartsWith("[host] Same output", StringComparison.Ordinal) Then
                Dim target = Text.RegularExpressions.Regex.Match(m.Content, "call (\S+) earlier").Groups(1).Value
                Assert.Contains(t, Function(x) x.ToolCallId = target AndAlso Not x.Content.StartsWith("[host] Same output", StringComparison.Ordinal))
            End If
        Next
    End Sub

    <Fact>
    Public Sub WhenTheFirstCopyIsShortenedTheNoteGetsTheWholeOutput()
        ' Shortening (older outputs cut to OldOutputChars) also hands the whole output to the newest note first.
        Dim big = String.Join(vbLf, Enumerable.Range(1, 2000).Select(Function(i) $"line {i} of service.py"))
        Dim t As New List(Of ChatMessage) From {ChatMessage.ForSystem("system"), ChatMessage.ForUser("the task")}
        AddRead(t, "c1", big)
        AddRead(t, "c2", big)
        For i = 0 To 6
            Dim c = Call_("p" & i, "update_plan", "{}")
            t.Add(ChatMessage.ForAssistant(Nothing, New List(Of ToolCallRequest) From {c}))
            t.Add(ChatMessage.ForTool(c.Id, c.Name, "plan " & i))
        Next
        ' Over the token limit of an 8k window; shortening the old outputs is enough, so nothing is removed.
        Dim before = t.Count
        Assert.True(ContextCompactor.Compact(t, 8000, 60))
        Assert.Equal(before, t.Count)
        Dim c2 = t.First(Function(m) m.ToolCallId = "c2")
        Assert.DoesNotContain("Same output", c2.Content)
        Assert.Contains("older output shortened", c2.Content)   ' it is old as well, so it is shortened in turn
        Assert.Contains("older output shortened", t.First(Function(m) m.ToolCallId = "c1").Content)
    End Sub

    <Theory>
    <InlineData("sed -n '337,400p' /home/user/ledgerlite/src/ledgerlite/service.py", True)>
    <InlineData("cat /home/user/a.py | sed -n '100,260p'; echo ""=====""; cat /home/user/b.py", True)>
    <InlineData("grep -n ""def \|class "" /home/user/api.py | head -60", True)>
    <InlineData("cd /home/user/ledgerlite && git status --short && git log --oneline -5 && git tag -l && git branch -a", True)>
    <InlineData("find / -maxdepth 4 -name ""ledgerlite"" -type d 2>/dev/null; ls -la /home/user", True)>
    <InlineData("git --no-pager diff HEAD~1 -- src", True)>
    <InlineData("echo ok", True)>
    <InlineData("cd /home/user/ledgerlite && python3 -m pytest tests/ -q 2>&1 | tail -40", False)>
    <InlineData("sed -i 's/a/b/' service.py", False)>
    <InlineData("cat > notes.md", False)>
    <InlineData("echo hi >> notes.md", False)>
    <InlineData("python3 << 'EOF'" & vbLf & "print(1)" & vbLf & "EOF", False)>
    <InlineData("find . -name '*.pyc' -delete", False)>
    <InlineData("git commit -am fix", False)>
    <InlineData("git branch new-feature", False)>
    <InlineData("git tag v1.0", False)>
    <InlineData("pip install pytest 2>&1 | tail -3", False)>
    <InlineData("cat $(ls *.py)", False)>
    <InlineData("", False)>
    Public Sub ShellCommandsThatOnlyRead(command As String, reads As Boolean)
        Assert.Equal(reads, ReasoningTaskHandler.ShellOnlyReads(command))
    End Sub

    <Fact>
    Public Sub AShellReadIsLookingAndAShellChangeIsNot()
        Assert.True(ReasoningTaskHandler.OnlyLooks("mcp__code-exec__run_command", "{""command"":""sed -n '1,50p' a.py""}"))
        Assert.False(ReasoningTaskHandler.OnlyLooks("mcp__code-exec__run_command", "{""command"":""python3 -m pytest""}"))
        Assert.False(ReasoningTaskHandler.OnlyLooks("mcp__code-exec__run_command"))   ' arguments unknown: counts as a change, as before
        Assert.True(ReasoningTaskHandler.OnlyLooks("mcp__code-exec__read_file", "{""path"":""a.py""}"))
        Assert.False(ReasoningTaskHandler.OnlyLooks("mcp__code-exec__write_file", "{""path"":""a.py""}"))
    End Sub

    ''' <summary>A shell server whose run_command answers with the command it ran.</summary>
    Private NotInheritable Class ShellMcpFactory
        Implements Mcp.IMcpClientFactory
        Public Function Create(server As McpServerOptions) As Mcp.IMcpClient Implements Mcp.IMcpClientFactory.Create
            Return New Client()
        End Function
        Private NotInheritable Class Client
            Implements Mcp.IMcpClient
            Public Function ConnectAsync(ct As CancellationToken) As Task Implements Mcp.IMcpClient.ConnectAsync
                Return Task.CompletedTask
            End Function
            Public Function ListToolsAsync(ct As CancellationToken) As Task(Of List(Of Mcp.McpToolInfo)) Implements Mcp.IMcpClient.ListToolsAsync
                Return Task.FromResult(New List(Of Mcp.McpToolInfo) From {
                    New Mcp.McpToolInfo With {.ServerId = "sh", .Name = "run_command", .Description = "Run a shell command.",
                                              .InputSchemaJson = "{""type"":""object"",""properties"":{""command"":{""type"":""string""}}}"}})
            End Function
            Public Function CallToolAsync(name As String, arguments As Text.Json.JsonElement, ct As CancellationToken) As Task(Of Mcp.McpCallResult) Implements Mcp.IMcpClient.CallToolAsync
                Return Task.FromResult(New Mcp.McpCallResult With {.Text = "output of " & arguments.GetProperty("command").GetString()})
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
    Public Async Function AnAgentThatOnlyReadsThroughTheShellIsStuckAfterAFewChecks() As Task
        ' LedgerLite's CI step: hundreds of cat/sed -n commands, each a "new result" and each counted as a change.
        Dim n = 0
        Dim p As New FakeAIProvider()
        p.Responder = Function(r)
                          If FakeAIProvider.AgentOf(r) <> "A" Then Return FakeAIProvider.Text("ok")
                          If If(r.Messages.LastOrDefault(Function(m) m.Role = "user")?.Content, "").Contains("This task stops here") Then
                              Return FakeAIProvider.Complete("Read the files, changed nothing.", success:=False)
                          End If
                          Dim k = Interlocked.Increment(n)
                          Return FakeAIProvider.Tool("mcp__sh__run_command", "{""command"":""sed -n '" & k & ",200p' service.py""}")
                      End Function
        Using h = Await TestHost.StartAsync(Sub(o)
                                                o.Routing.Escalation.OnTurnLimit = False
                                                o.McpServers.Add(New McpServerOptions With {.Id = "sh", .Name = "Shell", .Transport = McpTransportKind.Http, .Endpoint = "http://sh.local/mcp"})
                                                Dim a = TestHost.Agent(o, "A")
                                                a.AllowedMcpServers = New List(Of String) From {"sh"}
                                                a.Tasks = New TaskPolicyOptions With {.MaxReasoningTurns = 3, .MaxToolCalls = 40, .MaxRetries = 0}
                                            End Sub, p, mcpFactory:=New ShellMcpFactory())
            Dim shellReady = Function() h.H.ToolCatalog.BuildForAgent(h.H.Agents.GetEffectiveProfile("A")).All().Any(Function(x) x.Name = "mcp__sh__run_command")
            Assert.True(Await TestHost.WaitUntilAsync(Function() Task.FromResult(shellReady()), 30), "the shell tool never appeared")
            Dim id = Await h.SubmitAsync("A", "Fix the CI")
            Dim t = Await h.WaitForTaskStatusAsync("A", id, AgentTaskStatus.Failed)
            Dim result = t.GetResult()
            Assert.Contains($"it has only been reading for {ReasoningTaskHandler.MaxLookingStretches} progress checks in a row", result.Summary)
            Assert.Equal(3 * ReasoningTaskHandler.MaxLookingStretches, n)
        End Using
    End Function

    <Fact>
    Public Sub ARevisionHasWhatIsLeftOfTheFirstDeadlineAndAtLeastAQuarter()
        Dim t0 = New DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc)
        Dim g As New GlobalTask With {.Options = New GlobalTaskOptions With {.DeadlineMinutes = 600}, .StartedUtc = t0, .FirstStartedUtc = t0}
        Assert.Equal(t0.AddHours(10), GlobalTaskService.DeadlinesOf(g, t0, 240).Hard)
        ' Sent back after 4 hours: the revision has the 6 hours that are left, not 10 more.
        g.Revision = 1 : g.StartedUtc = t0.AddHours(4)
        Assert.Equal(t0.AddHours(10), GlobalTaskService.DeadlinesOf(g, t0.AddHours(4), 240).Hard)
        ' Sent back after 9 hours: at least a quarter of the time (2.5 hours).
        g.StartedUtc = t0.AddHours(9)
        Assert.Equal(t0.AddHours(11.5), GlobalTaskService.DeadlinesOf(g, t0.AddHours(9), 240).Hard)
        ' With a final step its work cutoff comes from the revision's own span (12% of 150 min = 18 min).
        g.Steps.Add(New GlobalTaskStep With {.StepId = "final"})
        Dim times = GlobalTaskService.DeadlinesOf(g, t0.AddHours(9), 240)
        Assert.Equal(t0.AddHours(11.5).AddMinutes(-18), times.WorkCutoff)
        ' A task from before FirstStartedUtc existed keeps its own clock.
        g.FirstStartedUtc = Nothing
        Assert.Equal(t0.AddHours(19), GlobalTaskService.DeadlinesOf(g, t0.AddHours(9), 240).Hard)
    End Sub


    <Fact>
    Public Sub AStepWhoseModelsHaveNotWorkedForHalfAnHourIsReported()
        Dim now = New DateTime(2026, 11, 3, 3, 0, 0, DateTimeKind.Utc)
        Dim cp As New TaskCheckpoint With {.WaitingForAiSinceUtc = now.AddMinutes(-45)}
        Dim t As New AgentTask With {.Status = AgentTaskStatus.Retrying, .ErrorMessage = "AI unavailable: the provider is out of credit.", .Checkpoint = cp.ToJson()}
        Dim why = GlobalTaskService.BlockedReason(t, now)
        Assert.NotNull(why)
        Assert.StartsWith("no model it may use has worked for 45 min", why)
        ' Not yet after 10 minutes: a short outage sorts itself out.
        cp.WaitingForAiSinceUtc = now.AddMinutes(-10)
        t.Checkpoint = cp.ToJson()
        Assert.Null(GlobalTaskService.BlockedReason(t, now))
        ' Limits only an administrator can change are reported at once, as before.
        Dim limited As New AgentTask With {.Status = AgentTaskStatus.Retrying, .ErrorMessage = "AI usage limit: daily token limit reached"}
        Assert.NotNull(GlobalTaskService.BlockedReason(limited, now))
    End Sub

End Class
