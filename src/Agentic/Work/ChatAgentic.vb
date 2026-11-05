Option Strict On
Option Explicit On
Option Infer On

Imports System.Collections.Concurrent
Imports System.Net.Http
Imports System.Text
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ' Chat features like Claude Code's: a task list the model keeps up to date (update_todos), subagents (task) that work on
    ' a part of the job in their own context, messages the user adds while an answer is running (steering), permission
    ' rules (allow / ask / deny per tool and argument) and output styles.
    Partial Public NotInheritable Class ChatService

        Public Const TodosTool As String = "update_todos"
        Public Const SubagentTool As String = "task"
        ''' <summary>A message the user added while the answer was running (kept with the answer where it arrived).</summary>
        Public Const UserNoteTool As String = "user_note"

        ' ------------------------------------------------------------------ task list

        Private Function TodosBinding() As ToolBinding
            Return New ToolBinding With {.Name = TodosTool, .Server = "todos", .Tool = TodosTool, .Risk = "read", .Definition = FunctionDef(TodosTool,
                "Keep a visible task list for work with several steps (3 or more), like a checklist the user watches. Call it at the start with the steps, " &
                "then again whenever a step starts or is done (send the whole list each time). Exactly one item is in_progress while you work. " &
                "Do not use it for a simple question.",
                "{""type"":""object"",""properties"":{""todos"":{""type"":""array"",""items"":{""type"":""object"",""properties"":{" &
                """content"":{""type"":""string"",""description"":""The step, in a few words""},""status"":{""type"":""string"",""enum"":[""pending"",""in_progress"",""completed""]}}," &
                """required"":[""content"",""status""]}}},""required"":[""todos""]}")}
        End Function

        ''' <summary>The task list from an update_todos call, cleaned up (at most 40 items, known statuses).</summary>
        Friend Shared Function ParseTodos(argsJson As String) As JsonArray
            Dim result As New JsonArray()
            Dim root As JsonNode = Nothing
            Try
                root = JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
            Catch ex As JsonException
            End Try
            For Each t In If(TryCast(root?("todos"), JsonArray), New JsonArray()).Take(40)
                Dim content = TextUtil.Truncate(t?("content")?.ToString(), 200)
                If String.IsNullOrWhiteSpace(content) Then Continue For
                Dim status = If(t?("status")?.ToString(), "pending").ToLowerInvariant()
                If status <> "in_progress" AndAlso status <> "completed" Then status = "pending"
                result.Add(New JsonObject From {{"content", content}, {"status", status}})
            Next
            Return result
        End Function

        ''' <summary>The task list the model last set in these messages (update_todos, or a client's TodoWrite), or Nothing.</summary>
        Friend Shared Function LastTaskList(messages As IEnumerable(Of ChatMessageRecord)) As JsonArray
            For Each m In messages.Reverse()
                If m.Role <> "assistant" OrElse m.ToolCalls Is Nothing Then Continue For
                Dim c = m.ToolCalls.LastOrDefault(Function(t) t.Ok AndAlso {"update_todos", "TodoWrite", "todo_write"}.Contains(t.Name, StringComparer.Ordinal))
                If c Is Nothing Then Continue For
                Dim list = ParseTodos(c.Arguments)
                If list.Count > 0 Then Return list
            Next
            Return Nothing
        End Function

        ''' <summary>The answer just before the user's latest message ended with a model failure (after its retries) and had done work.</summary>
        Friend Shared Function PreviousAnswerFailed(messages As List(Of ChatMessageRecord), lastUser As ChatMessageRecord) As Boolean
            Dim i = messages.IndexOf(lastUser)
            If i <= 0 Then Return False
            Dim prev = messages(i - 1)
            Return prev.Role = "assistant" AndAlso prev.FinishReason = "error" AndAlso
                   (Not String.IsNullOrWhiteSpace(prev.Content) OrElse (prev.ToolCalls IsNot Nothing AndAlso prev.ToolCalls.Count > 0))
        End Function

        ''' <summary>What the model hears when its previous answer failed part-way: carry on with the plan (and which).</summary>
        Friend Shared Function FailedAnswerNote(openPlan As JsonArray) As String
            Dim sb As New StringBuilder("Your previous answer was cut off by a connection problem with the model before it finished; what it did (its text, " &
                                        "tool calls and their results) is in the conversation above.")
            If openPlan IsNot Nothing Then
                sb.Append(" Your task list at that point:")
                For Each t In openPlan
                    Dim status = t("status").ToString()
                    sb.Append(vbLf).Append(If(status = "completed", "[x] ", If(status = "in_progress", "[>] ", "[ ] "))).Append(t("content").ToString())
                Next
            End If
            sb.Append(vbLf).Append("Unless the user's new message says otherwise, carry on with that work from where it stopped - do not start over or redo steps that are done.")
            Return sb.ToString()
        End Function

        Friend Shared Function TodosSummary(list As JsonArray) As String
            Dim done = list.Where(Function(t) t("status").ToString() = "completed").Count()
            Dim current = list.FirstOrDefault(Function(t) t("status").ToString() = "in_progress")?("content")?.ToString()
            Return $"Task list updated: {done} of {list.Count} done." & If(current Is Nothing, "", $" Working on: {current}.") &
                   If(done = list.Count AndAlso list.Count > 0, " All steps are done: give the user your final answer.", " Keep going; update the list as steps finish.")
        End Function

        ' ------------------------------------------------------------------ subagents

        Private Function SubagentBinding(Optional parallel As Boolean = True, Optional nested As Boolean = False, Optional agents As List(Of ClientAgent) = Nothing) As ToolBinding
            Dim types = String.Join(",", {"general", "explore"}.Concat(If(agents, New List(Of ClientAgent)).Select(Function(a) a.Name)).Select(Function(n) """" & n & """"))
            Return New ToolBinding With {.Name = SubagentTool, .Server = "subagent", .Tool = SubagentTool, .Risk = "read", .Definition = FunctionDef(SubagentTool,
                If(nested, "Hand a self-contained part of YOUR sub-task to a subagent of your own (only when it really splits into independent parts): ", "Hand a self-contained part of the work to a subagent: ") &
                "it works in its own context with the same tools (reading and searching files, running commands, the web…) and returns only its report. " &
                "Use it for broad searches across many files, research, reviews, and independent sub-tasks, so your own context stays small. " &
                If(parallel, "Subagents started in the SAME response run AT THE SAME TIME: when the work splits into independent parts, call task several times in one " &
                             "response (one per part) instead of doing the parts one after another — it is much faster. ",
                             "Subagents run one after another. ") &
                "Give each everything it needs in the prompt: it does not see this chat, unless you set fork to true (then it starts with the whole " &
                "conversation so far — for side tasks that need its context). subagent_type explore = read-only investigation (search, read, web; " &
                "no edits or commands) — safe to run many at once; general = may also change files and run commands. " &
                "Each report gives the subagent's id: when it ran out of steps or stopped (or you have a follow-up), call task with resume set to that id — " &
                "it continues where it stopped with everything it already read and did, which is much cheaper than starting over." & ClientAgentsNote(agents),
                "{""type"":""object"",""properties"":{""description"":{""type"":""string"",""description"":""A short name for the sub-task (3-6 words)""}," &
                """subagent_type"":{""type"":""string"",""enum"":[" & types & "],""description"":""explore: read-only investigation; general (default): may also edit and run commands" &
                If(agents Is Nothing OrElse agents.Count = 0, "", "; or one of the user's agent types") & """}," &
                """fork"":{""type"":""boolean"",""description"":""true: the subagent starts with this whole conversation (its context); default false: a fresh context with only your prompt""}," &
                """resume"":{""type"":""string"",""description"":""The id of an earlier subagent (from its report) to continue with its whole context instead of starting a new one; the prompt then says what to do next""}," &
                """prompt"":{""type"":""string"",""description"":""The complete task for the subagent, with all context it needs and what to report back (with resume: what to do next)""}},""required"":[""description"",""prompt""]}")}
        End Function

        ''' <summary>
        ''' The conversation a forked subagent starts from: a copy of the messages so far, without the newest assistant turn whose tool
        ''' calls are still running (a provider refuses tool calls without their results).
        ''' </summary>
        Friend Shared Function ConversationForFork(messages As JsonArray) As JsonArray
            Dim copy = messages.DeepClone().AsArray()
            For i = copy.Count - 1 To 0 Step -1
                Dim m = TryCast(copy(i), JsonObject)
                If m Is Nothing Then Continue For
                Dim role = m("role")?.ToString()
                If role = "assistant" AndAlso m("tool_calls") IsNot Nothing Then
                    While copy.Count > i
                        copy.RemoveAt(copy.Count - 1)
                    End While
                    Exit For
                End If
                If role <> "tool" Then Exit For
            Next
            Return copy
        End Function

        ''' <summary>
        ''' Only reads: web search and pages, tools on the user's computer marked read, and MCP tools the server lists as read-only
        ''' or idempotent. Explore subagents get only these, and a round's calls of this kind may run at the same time.
        ''' </summary>
        Private Function IsReadOnlyBinding(t As ToolBinding) As Boolean
            If t Is Nothing Then Return False
            Select Case t.Server
                Case "web", "page" : Return True
                Case "client" : Return String.Equals(t.Risk, "read", StringComparison.OrdinalIgnoreCase)
                Case "chat", "todos", "subagent", "goal", "memory", "image", "video", "sd", "music", "code", "share" : Return False
            End Select
            If String.Equals(t.Risk, "change", StringComparison.OrdinalIgnoreCase) Then Return False
            Dim info = _mcp.GetTools(t.Server).FirstOrDefault(Function(x) String.Equals(x.Name, t.Tool, StringComparison.OrdinalIgnoreCase))
            Return info IsNot Nothing AndAlso (info.IsReadOnly OrElse info.IsIdempotent)
        End Function

        ''' <summary>A call that starts a subagent: the server's task tool, or a client's own (the CLI's Task).</summary>
        Friend Shared Function IsSubagentCall(serverName As String, toolName As String) As Boolean
            Return serverName = "subagent" OrElse (serverName = "client" AndAlso
                   {"Task", "task", "Agent", "agent"}.Contains(If(toolName, ""), StringComparer.Ordinal))
        End Function

        ''' <summary>
        ''' The orchestration note added to the system prompt when subagents can run in parallel: models rarely split work on their
        ''' own, so they are told when and how to fan it out.
        ''' </summary>
        Friend Shared Function ParallelAgentsNote(toolName As String, maxAgents As Integer) As String
            Return "Parallel agents: you are an orchestrator. When the work splits into independent parts — exploring different areas of a codebase, " &
                   "researching several questions or sources, reviewing several files, components or services, checking several hypotheses, or changes in " &
                   $"separate files that do not depend on each other — start one {toolName} subagent per part, ALL IN THE SAME RESPONSE (up to {maxAgents} at once): " &
                   "they run at the same time, so the whole job finishes far sooner than doing the parts one by one. Give each a complete, self-contained brief " &
                   "(goal, where to look, what to report back; it sees nothing of this chat unless you fork it). Use read-only explore subagents for investigation. " &
                   "Do quick single steps (one file, one search) yourself instead of delegating them. When the reports come back, check them, " &
                   "resolve any conflicts, and combine them into one result. Independent read-only calls (reading or searching several files) can also be " &
                   "made together in one response."
        End Function

        ''' <summary>
        ''' Runs a subagent: the same model and tools (not ask_user, task lists, goals or further subagents) in a fresh context,
        ''' up to Chat.SubagentMaxRounds steps; returns its final report (or, when it stopped before one, what it did), always with
        ''' its id so it can be continued (resume). Its tokens count toward the answer that started it.
        ''' Tool calls follow the same approvals and permission rules as the answer's own.
        ''' </summary>
        Private Async Function RunSubagentAsync(argsJson As String, tools As List(Of ToolBinding), model As String, username As String, chatId As String, isAdmin As Boolean,
                                                settings As ChatSettings, rules As PermissionRules, deviceRoute As String, client As ChatClientContext, answer As ChatMessageRecord,
                                                emit As Func(Of String, Object, Task), ct As CancellationToken, Optional callId As String = Nothing,
                                                Optional parentMessages As JsonArray = Nothing, Optional depth As Integer = 1) As Task(Of (Ok As Boolean, Text As String))
            ' Its card streams what it writes, thinks and does (several subagents at once each have their own).
            Dim args As JsonNode = Nothing
            Try
                args = JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
            Catch ex As JsonException
            End Try
            Dim stream As New SubagentStream(If(callId, Ids.NewId("sub")), emit)
            ' Only the answer's own subagents keep their transcript with their saved call; a nested one shows inside its parent's card.
            If callId IsNot Nothing AndAlso depth = 1 Then _subagentStreams(callId) = stream
            Dim typeName = args?("subagent_type")?.ToString()
            Await stream.StartAsync(TextUtil.Truncate(If(args?("description")?.ToString(), "sub-task"), 60),
                                    If(String.Equals(typeName, "explore", StringComparison.OrdinalIgnoreCase), "explore",
                                       If(FindClientAgent(client, typeName)?.Name, "general"))).ConfigureAwait(False)
            Dim result As (Ok As Boolean, Text As String)
            Try
                result = Await RunSubagentCoreAsync(argsJson, tools, model, username, chatId, isAdmin, settings, rules, deviceRoute, client, answer, emit, stream, ct, callId,
                                                    parentMessages, depth).ConfigureAwait(False)
            Catch ex As OperationCanceledException
                ' Stopped with its answer: its tool call is never saved, so nothing would ever take (and free) its transcript.
                If callId IsNot Nothing AndAlso depth = 1 Then _subagentStreams.TryRemove(callId, Nothing)
                Throw
            End Try
            Await stream.EndAsync(result.Ok).ConfigureAwait(False)
            Return result
        End Function

        Private Async Function RunSubagentCoreAsync(argsJson As String, tools As List(Of ToolBinding), model As String, username As String, chatId As String, isAdmin As Boolean,
                                                    settings As ChatSettings, rules As PermissionRules, deviceRoute As String, client As ChatClientContext, answer As ChatMessageRecord,
                                                    emit As Func(Of String, Object, Task), stream As SubagentStream, ct As CancellationToken, callId As String,
                                                    parentMessages As JsonArray, depth As Integer) As Task(Of (Ok As Boolean, Text As String))
            Dim args As JsonNode = Nothing
            Try
                args = JsonNode.Parse(If(String.IsNullOrWhiteSpace(argsJson), "{}", argsJson))
            Catch ex As JsonException
            End Try
            Dim prompt = args?("prompt")?.ToString()
            ' resume: an earlier subagent of this chat continues with its whole context (what it read and did), so nothing is done twice.
            Dim resumeId = args?("resume")?.ToString()?.Trim()
            Dim resumed As KeptSubagent = Nothing
            If Not String.IsNullOrEmpty(resumeId) AndAlso Not _keptSubagents.TryRemove(SubagentKey(username, chatId, resumeId), resumed) Then
                Return (False, $"No subagent with id ""{resumeId}"" can be continued in this chat (they are kept for {SubagentKeepHours} hours, and one that is " &
                               "still running cannot be continued). Start a new one with the complete task.")
            End If
            Dim subId = If(resumed IsNot Nothing, resumeId, Ids.NewId("sub"))
            Dim label = TextUtil.Truncate(If(args?("description")?.ToString(), If(resumed?.Label, "sub-task")), 60)
            Dim explore = If(resumed IsNot Nothing, resumed.Explore, String.Equals(args?("subagent_type")?.ToString(), "explore", StringComparison.OrdinalIgnoreCase))
            ' One of the user's own agent types: its instructions and only its tools (a resumed one keeps what it had).
            Dim custom = If(resumed Is Nothing, FindClientAgent(client, args?("subagent_type")?.ToString()), Nothing)
            ' fork: it starts with the conversation so far (for side tasks that need its context) instead of only the prompt.
            Dim fork = resumed Is Nothing AndAlso String.Equals(args?("fork")?.ToString(), "true", StringComparison.OrdinalIgnoreCase) AndAlso parentMessages IsNot Nothing
            If String.IsNullOrWhiteSpace(prompt) AndAlso resumed Is Nothing Then Return (False, "Give the subagent a prompt: the complete task and what to report back.")
            Dim own = tools.Where(Function(t) t.Name <> AskUserTool AndAlso t.Name <> TodosTool AndAlso t.Name <> SubagentTool AndAlso t.Name <> GoalTool AndAlso
                                               t.Server <> "memory" AndAlso t.Server <> "image" AndAlso t.Server <> "video" AndAlso t.Server <> "sd" AndAlso t.Server <> "music").ToList()
            ' An explore subagent only reads: it can run alongside many others without stepping on their changes.
            If explore Then own = own.Where(Function(t) IsReadOnlyBinding(t)).ToList()
            If custom IsNot Nothing AndAlso custom.Tools.Count > 0 Then
                Dim allowed = own.Where(Function(t) AgentMayUse(custom.Tools, t.Name, t.Server) OrElse AgentMayUse(custom.Tools, t.Tool, t.Server)).ToList()
                own = If(allowed.Count > 0, allowed, own.Where(Function(t) IsReadOnlyBinding(t)).ToList())
            End If
            ' It may start subagents of its own, up to Chat.MaxSubagentDepth levels below the answer (an explore one only explore ones).
            Dim nestedOn = depth < Math.Clamp(_opt.MaxSubagentDepth, 1, 5)
            If nestedOn Then own.Add(SubagentBinding(If(settings?.Parallel, True), nested:=True, agents:=client?.Agents))
            Dim forkNote = ""
            Dim inherited As New List(Of JsonNode)
            If fork Then
                For Each m In parentMessages
                    Dim o = TryCast(m, JsonObject)
                    If o Is Nothing Then Continue For
                    ' The answer's own instructions (its first system message) go into this subagent's instructions, shortened.
                    If inherited.Count = 0 AndAlso forkNote.Length = 0 AndAlso o("role")?.ToString() = "system" Then
                        forkNote = vbLf & vbLf & "You are a fork: the conversation so far (between the user and the assistant that started you) comes first, " &
                                   "then your task. The assistant's instructions, for context:" & vbLf & TextUtil.Truncate(If(o("content")?.ToString(), ""), 6000)
                        Continue For
                    End If
                    inherited.Add(o.DeepClone())
                Next
            End If
            ' Its steps are checked for progress every stretch (not a stop): one still getting things done gets another stretch; one
            ' that brought nothing new for a stretch is told to finish. Only Chat.SubagentMaxTotalRounds is a fixed total.
            Dim stretch = Math.Clamp(_opt.SubagentMaxRounds, 3, 100)
            Dim roundCap = If(_opt.SubagentMaxTotalRounds > 0, Math.Max(3, _opt.SubagentMaxTotalRounds), Integer.MaxValue)
            Dim maxRounds = Math.Min(stretch, roundCap)
            Dim stretchProgress = 0
            Dim seenResults As New HashSet(Of String)(StringComparer.Ordinal)
            Dim messages As JsonArray
            If resumed IsNot Nothing Then
                messages = resumed.Messages
                AddUserNote(messages, $"(Note from the app: you are continued where you stopped, with {maxRounds} more steps.)" &
                                      If(String.IsNullOrWhiteSpace(prompt), " Finish your task, then write your report.", vbLf & vbLf & prompt))
            Else
                messages = New JsonArray(
                    New JsonObject From {{"role", "system"}, {"content", "You are a subagent: you work on one part of a larger task for another assistant, with tools. " &
                        "Other subagents may be working on other parts at the same time: stay within your part. " &
                        If(explore, "You only investigate: read, search and look things up; you cannot change files or run commands. ", "") &
                        "Work on your own (nobody can answer questions), be thorough but efficient - make independent read-only calls together in one response - " &
                        "and finish with a concise report of what you found or did - exact file paths, commands, values and conclusions - because only that report is passed back. " &
                        $"Each of your responses is one step. The app checks your progress every {stretch} steps: while you keep getting things done you go on; " &
                        "when you stop making progress it tells you a few steps ahead, and your last step is then for your report. " &
                        $"Current date and time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}." &
                        If(CavemanInstructions(settings?.Caveman) Is Nothing, "", vbLf & vbLf & CavemanInstructions(settings?.Caveman)) & forkNote &
                        If(custom Is Nothing, "", vbLf & vbLf & $"You are the user's ""{custom.Name}"" agent. Its instructions (from the user's agent file):" & vbLf & custom.Prompt)}})
                For Each m In inherited
                    messages.Add(m)
                Next
                messages.Add(New JsonObject From {{"role", "user"}, {"content", If(fork, "(Note from the app: you are a forked subagent. The conversation above is your context; " &
                                                                                       "do only this task, then write your report.)" & vbLf & vbLf & prompt, prompt)}})
            End If
            ' Where this run's own work starts: if it stops before its report, what it did from here goes back instead.
            Dim workStart = messages.Count
            ' A failing model call (offline, overloaded, its stream cut off, no answer in time) is asked again like the answer's own:
            ' up to Chat.Retries times (GoalRetries when the chat has a goal), RetrySeconds apart, for each step.
            Dim goalOn = Not String.IsNullOrWhiteSpace(settings?.Goal)
            Dim retryLimit = Math.Max(0, If(goalOn, _opt.GoalRetries, _opt.Retries))
            Dim retryWait = Math.Max(1, If(goalOn, _opt.GoalRetrySeconds, _opt.RetrySeconds))
            Dim window = Await ContextWindowAsync(model).ConfigureAwait(False)
            Dim toolChars = own.Sum(Function(t) CLng(t.Definition.ToJsonString().Length))
            ' Progress on the subagent's own card (several run at once, so one shared status line is not enough).
            Dim progress = Async Function(note As String) As Task
                               Await emit("status", New With {.text = $"Subagent '{label}': {note}"}).ConfigureAwait(False)
                               If callId IsNot Nothing Then Await emit("tool_progress", New With {.id = callId, .text = note}).ConfigureAwait(False)
                           End Function
            ' Calls failing one after another (the same guard as the answer's own): change approach, then report.
            Dim failStreak = 0, toolsOff = False, reportAsked = False
            ' One step beyond the limit, only to ask again for a report its last step did not write.
            Dim extraReport = False
            ' What it last thought: some models answer only in their thinking, and that is better than nothing.
            Dim lastThoughts As String = Nothing
            ' It ends before writing a report (its model failed, or its last step gave nothing): what it did so far still goes back,
            ' and it is kept so it can be continued instead of started over. stopPlain: the answer when it had not done anything yet.
            Dim stoppedEarly = Function(stopWhy As String, stopPlain As String) As (Boolean, String)
                                   Dim did = SubagentSteps(messages, workStart)
                                   If did.Length = 0 AndAlso resumed Is Nothing AndAlso String.IsNullOrWhiteSpace(lastThoughts) Then Return (False, stopPlain)
                                   KeepSubagent(username, chatId, subId, messages, label, explore)
                                   Return (False, $"Subagent '{label}' (id {subId}) stopped before writing its report: {stopWhy}." &
                                                  If(did.Length = 0, "", vbLf & vbLf & "What it did, from its steps (newest last):" & vbLf & did) &
                                                  If(String.IsNullOrWhiteSpace(lastThoughts), "", vbLf & vbLf & "Its last thoughts:" & vbLf & TailOf(lastThoughts, 3000)) &
                                                  vbLf & vbLf & ResumeHint(subId))
                               End Function
            ' maxRounds grows while it makes progress (see the check before its steps run low), so the end is looked at every step.
            Dim round = 0, lastFold = -10
            While True
                round += 1
                If round > maxRounds + 1 OrElse (round > maxRounds AndAlso Not extraReport) Then Exit While
                ' A long run filling the context: its earlier steps fold into a summary (as in the answer itself), then old output is cut.
                If round > 3 AndAlso round - lastFold >= 3 AndAlso EstimateRequestTokens(messages, 3.0, toolChars) > PromptBudget(window) * 3 \ 4 Then
                    If Await FoldOldRoundsAsync(messages, workStart, model, 2, ct).ConfigureAwait(False) Then lastFold = round
                End If
                FitToBudget(messages, PromptBudget(window), 3.0, toolChars)
                Await progress(If(round > maxRounds, "writing its report…", $"step {round}…")).ConfigureAwait(False)
                Dim body As New JsonObject From {{"model", model}, {"messages", messages.DeepClone()}, {"stream", True}}
                body("stream_options") = New JsonObject From {{"include_usage", True}}
                ' Its last step (and every step after too many failed calls) has no tools: that response is its report.
                Dim offerTools = own.Count > 0 AndAlso round < maxRounds AndAlso Not toolsOff
                If offerTools Then body("tools") = New JsonArray(own.Select(Function(t) CType(t.Definition.DeepClone(), JsonNode)).ToArray())
                Dim json As JsonNode = Nothing
                Dim text As String = Nothing
                Dim status = 0
                Dim failures = 0
                Do
                    json = Nothing
                    text = Nothing
                    status = 0
                    Dim failure As Exception = Nothing
                    ' The local model's slots are shared with agents and other chats: parallel subagents queue for them fairly.
                    Dim slot As IDisposable = Nothing
                    Using limit = CancellationTokenSource.CreateLinkedTokenSource(ct)
                        limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _opt.ModelResponseSeconds)))
                        Try
                            If Gateway IsNot Nothing Then slot = Await Gateway.TakeLocalSlotAsync(model, Nothing, ct).ConfigureAwait(False)
                            Using resp = Await _hub.SendAsync("/chat/completions", body.ToJsonString(), limit.Token).ConfigureAwait(False)
                                status = CInt(resp.StatusCode)
                                ' Streamed: what it writes and thinks shows on its card as it comes.
                                If resp.IsSuccessStatusCode AndAlso If(resp.Content.Headers.ContentType?.MediaType, "").Contains("event-stream") Then
                                    json = Await ReadSubagentStreamAsync(resp, stream, limit.Token, Math.Max(30, _opt.StreamIdleSeconds)).ConfigureAwait(False)
                                Else
                                    text = Await resp.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(False)
                                    ' A refused call is a failure like a broken connection: an outage (5xx, 429, "temporarily unavailable") is retried.
                                    If Not resp.IsSuccessStatusCode Then failure = New HttpRequestException($"{model}: {status} {ProviderMessage(text)}")
                                End If
                            End Using
                        Catch ex As Exception When Not ct.IsCancellationRequested AndAlso (TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is IO.IOException)
                            failure = If(TypeOf ex Is OperationCanceledException, New IO.IOException($"{model}: no answer within {Math.Max(60, _opt.ModelResponseSeconds)} s", ex), ex)
                        Finally
                            slot?.Dispose()
                        End Try
                    End Using
                    If failure Is Nothing Then Exit Do
                    Dim why = TextUtil.Truncate(Regex.Replace(failure.Message, "\s+", " "), 600)
                    If failures >= retryLimit OrElse Not IsTransientFailure(failure, ct) Then
                        _log.Warn("Chat", $"Subagent model call failed in chat {chatId}{If(failures > 0, $" after {failures + 1} attempts", "")}: {why}")
                        Dim gaveUp = $"{ModelErrors.ForUser(failure)}{If(failures > 0, $" (gave up after {failures + 1} attempts)", "")}"
                        Return stoppedEarly($"its model call failed: {gaveUp.TrimEnd("."c)}", $"The subagent's model call failed: {gaveUp} Continue without it, or do the sub-task yourself.")
                    End If
                    failures += 1
                    ' Soon at first (most failures pass in seconds), then longer, up to RetrySeconds.
                    Dim wait = RetryDelay(failures, retryWait)
                    _log.Warn("Chat", $"Subagent model call failed in chat {chatId}, retry {failures} of {retryLimit} in {wait}s: {why}")
                    ' The provider's full text stays in the log: the card says what happened in a few words, and that it retries.
                    Dim shortWhy = RetryReasonText(failure.Message)
                    Await stream.NoteAsync($"{shortWhy} Retrying in {wait}s · attempt {failures + 1} of {retryLimit + 1}").ConfigureAwait(False)
                    Dim waitUntil = DateTime.UtcNow.AddSeconds(wait)
                    While DateTime.UtcNow < waitUntil
                        Dim left = CInt(Math.Ceiling((waitUntil - DateTime.UtcNow).TotalSeconds))
                        Await progress($"{shortWhy.TrimEnd("."c).ToLowerInvariant()}, retrying in {left}s (attempt {failures + 1} of {retryLimit + 1})").ConfigureAwait(False)
                        Await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, left)), ct).ConfigureAwait(False)
                    End While
                Loop
                If json Is Nothing Then
                    ' A proxy's error page with a 200 status is not an answer: report it instead of failing the whole chat answer.
                    Try
                        json = JsonNode.Parse(If(text, ""))
                    Catch ex As JsonException
                        Dim junk = TextUtil.Truncate(Regex.Replace(If(text, ""), "\s+", " "), 120)
                        Return stoppedEarly($"its model returned something that is not an answer ({junk})",
                                            $"The subagent's model returned something that is not an answer ({junk}). Continue without it, or do the sub-task yourself.")
                    End Try
                    ' Answered in one piece (the provider does not stream): its card gets the whole step at once.
                    Dim whole = JsonUtil.FirstChoice(json)?("message")
                    Await stream.WriteAsync("reasoning", If(whole?("reasoning_content")?.ToString(), whole?("reasoning")?.ToString())).ConfigureAwait(False)
                    Await stream.WriteAsync("text", Regex.Replace(If(whole?("content")?.ToString(), ""), "<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase)).ConfigureAwait(False)
                    Await stream.FlushAsync().ConfigureAwait(False)
                End If
                Dim u = json?("usage")
                Dim p As Long, c As Long
                ' Several subagents may finish a step at the same moment: their tokens add up under a lock.
                SyncLock answer
                    If Long.TryParse(u?("prompt_tokens")?.ToString(), p) Then answer.PromptTokens += p : answer.CachedTokens += Math.Min(p, Economy.AccountService.CachedTokensOf(u))
                    If Long.TryParse(u?("completion_tokens")?.ToString(), c) Then answer.CompletionTokens += c
                End SyncLock
                Dim msg = JsonUtil.FirstChoice(json)?("message")
                Dim content = Regex.Replace(If(msg?("content")?.ToString(), ""), "<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase).Trim()
                Dim calls = TryCast(msg?("tool_calls"), JsonArray)
                Dim thought = If(msg?("reasoning_content")?.ToString(), msg?("reasoning")?.ToString())
                If Not String.IsNullOrWhiteSpace(thought) Then lastThoughts = thought
                If Not offerTools Then
                    ' No tools this step: what it wrote is its report. Calls it makes anyway (or writes as text) do not run - their
                    ' results would come after its last step, unseen - and the call text is left out of the report.
                    calls = Nothing
                    If own.Count > 0 Then content = TextToolCalls.Extract(content, own.Select(Function(t) t.Name)).Before.Trim()
                ElseIf (calls Is Nothing OrElse calls.Count = 0) AndAlso own.Count > 0 Then
                    ' Tool calls written as text instead of made (some models do): they run like real calls.
                    Dim written = TextToolCalls.Extract(content, own.Select(Function(t) t.Name))
                    If written.Calls.Count > 0 Then
                        content = written.Before
                        calls = New JsonArray(written.Calls.Select(Function(wc) CType(New JsonObject From {
                            {"id", Ids.NewId("call")}, {"type", "function"},
                            {"function", New JsonObject From {{"name", wc.Name}, {"arguments", wc.Args}}}}, JsonNode)).ToArray())
                    End If
                End If
                If calls Is Nothing OrElse calls.Count = 0 Then
                    If content.Length = 0 AndAlso Not reportAsked AndAlso round <= maxRounds Then
                        ' It stopped without a word (some models answer only in their thinking): ask once for its report instead of
                        ' returning nothing - at its last step too, with one extra step just for that.
                        reportAsked = True
                        If round = maxRounds Then extraReport = True
                        AddUserNote(messages, "(Note from the app: you stopped without a report. Write your final report now, as plain text in your reply " &
                                              "(not only in your thinking, and no tool calls): what you found or did, with exact file paths, commands and values.)")
                        Continue While
                    End If
                    ' Its last step: it ran out of steps (it was told so), and its report may not cover everything.
                    Dim outOfSteps = round >= maxRounds AndAlso own.Count > 0 AndAlso Not toolsOff
                    If content.Length = 0 Then
                        Return stoppedEarly(If(outOfSteps, $"it used all {maxRounds} steps and wrote nothing at its last step", "it ended without writing anything"),
                                            "The subagent finished without a report.")
                    End If
                    ' Kept with its report, so a follow-up (resume) continues with everything it saw.
                    messages.Add(New JsonObject From {{"role", "assistant"}, {"content", content}})
                    KeepSubagent(username, chatId, subId, messages, label, explore)
                    If outOfSteps Then
                        Return (True, $"Subagent '{label}' (id {subId}) used all its {maxRounds} steps, so its work may not be finished. Its report:" & vbLf &
                                      TextUtil.Truncate(content, 20000) & vbLf & vbLf & ResumeHint(subId))
                    End If
                    Return (True, $"Subagent '{label}' (id {subId}) report:" & vbLf & TextUtil.Truncate(content, 20000))
                End If
                messages.Add(New JsonObject From {{"role", "assistant"}, {"content", If(content.Length > 0, CType(JsonValue.Create(content), JsonNode), Nothing)}, {"tool_calls", calls.DeepClone()}})
                ' What a forked subagent of this one starts from (taken now: results are added to messages while they run).
                Dim forkBase = If(nestedOn, ConversationForFork(messages), Nothing)
                Dim steps = calls.Select(Function(tc) (Id:=If(tc?("id")?.ToString(), Ids.NewId("call")), Name:=If(tc?("function")?("name")?.ToString(), ""),
                                                       Args:=If(tc?("function")?("arguments")?.ToString(), "{}"))).ToList()
                Dim runTool = Async Function(s As (Id As String, Name As String, Args As String)) As Task(Of (Boolean, String))
                                 Dim binding = own.FirstOrDefault(Function(t) t.Name = s.Name)
                                 If binding Is Nothing Then Return (False, UnknownToolText(s.Name, own.Select(Function(t) t.Name)))
                                 Await progress($"{s.Name} {TextUtil.Truncate(ToolTarget(s.Args), 60, "…")}").ConfigureAwait(False)
                                 Dim rule = rules.Decide(s.Name, s.Args)
                                 Dim why = If(rule = "allow", Nothing, If(rule = "ask", "your permission rule asks first", ApprovalReason(chatId, settings, binding.Risk, binding.Server, s.Name, s.Args)))
                                 Dim choice = If(rule = "deny", "rule", If(why Is Nothing, "auto", Await RequestApprovalAsync(username, chatId, s.Id, s.Name, s.Args, why, binding.Server, emit, ct).ConfigureAwait(False)))
                                 ' The user allowed only some of an edit's changes: the call runs with what they accepted.
                                 Dim edited = If(choice = "once" OrElse choice = "always", Live.TakeApprovedArguments(username, chatId, s.Id), Nothing)
                                 If edited IsNot Nothing Then s = (s.Id, s.Name, edited)
                                 If choice = "rule" Then Return (False, $"Blocked by the user's permission rule ({rules.Matching(s.Name, s.Args, "deny")}). Do not try it again.")
                                 If choice = "deny" Then Return (False, "The user did not allow this. Continue without it.")
                                 If choice = "timeout" Then Return (False, "The user did not answer the approval request in time (they may be away), so this did not run. " &
                                                                           "Continue with what you can do without it, and say in your report which step still needs their approval.")
                                 If binding.Server = "subagent" Then
                                     ' A subagent of this subagent, one level deeper (an explore subagent only starts explore ones).
                                     Dim nestedArgs = s.Args
                                     If explore Then
                                         Try
                                             Dim n = TryCast(JsonNode.Parse(If(String.IsNullOrWhiteSpace(s.Args), "{}", s.Args)), JsonObject)
                                             If n IsNot Nothing Then
                                                 n("subagent_type") = "explore"
                                                 nestedArgs = n.ToJsonString()
                                             End If
                                         Catch ex As JsonException
                                         End Try
                                     End If
                                     Return Await RunSubagentAsync(nestedArgs, tools, model, username, chatId, isAdmin, settings, rules, deviceRoute, client, answer, emit, ct, s.Id,
                                                                   forkBase, depth + 1).ConfigureAwait(False)
                                 End If
                                 If binding.Server = "client" Then
                                     ' The same approval and permission mode as the answer's own calls: without them the user's computer
                                     ' asks again (or refuses) for a step the user already allowed, even in Auto mode.
                                     Dim r = Await RunClientToolAsync(username, chatId, s.Id, s.Name, s.Args, deviceRoute, emit, ct, client,
                                                                      approved:=choice = "once" OrElse choice = "always",
                                                                      mode:=NormalizeMode(If(settings?.PermissionMode, _opt.DefaultPermissionMode))).ConfigureAwait(False)
                                     ' A subagent gets no pictures (its report is text): it hears that there was one.
                                     If TakeClientImages(username, s.Id).Count > 0 Then r = (r.Ok, r.Text & vbLf & "(It returned a picture, which subagents cannot look at: say so in your report if it matters.)")
                                     Return If(r.Ok, (True, r.Text), (False, WithShellHint(r.Text)))
                                 End If
                                 Return Await RunToolAsync(binding, s.Args, isAdmin, answer.Sources, ct, username:=username).ConfigureAwait(False)
                             End Function
                ' Each step shows on the card as it starts and ends (with the start of its result).
                Dim runOne = Async Function(s As (Id As String, Name As String, Args As String)) As Task(Of (Boolean, String))
                                 Dim target = ToolTarget(s.Args)
                                 Await stream.ToolStartAsync(s.Id, s.Name, target).ConfigureAwait(False)
                                 Dim r = Await runTool(s).ConfigureAwait(False)
                                 Await stream.ToolEndAsync(s.Id, s.Name, target, r.Item1, r.Item2).ConfigureAwait(False)
                                 Return r
                             End Function
                ' Independent reads of this step (several files, several searches) run together; anything that changes something runs
                ' alone and in order.
                Dim early As New Dictionary(Of String, Task(Of (Boolean, String)))(StringComparer.Ordinal)
                If If(settings?.Parallel, True) Then
                    Dim reads = steps.Where(Function(s) ParallelReadable(own.FirstOrDefault(Function(t) t.Name = s.Name), s.Name, s.Args, chatId, settings, rules, allowWeb:=True)).ToList()
                    ' Subagents this one starts in the same step run at the same time too (each queues for its own model slot).
                    Dim subs = steps.Where(Function(s) s.Name = SubagentTool AndAlso nestedOn).ToList()
                    If subs.Count >= 2 Then
                        Dim subGate As New SemaphoreSlim(Math.Clamp(If(settings?.MaxParallelAgents, _opt.MaxParallelSubagents), 1, 16))
                        For Each s In subs
                            Dim one = s
                            early(one.Id) = Task.Run(Async Function()
                                                         Await subGate.WaitAsync(ct).ConfigureAwait(False)
                                                         Try
                                                             Return Await runOne(one).ConfigureAwait(False)
                                                         Finally
                                                             subGate.Release()
                                                         End Try
                                                     End Function)
                        Next
                    End If
                    If reads.Count >= 2 Then
                        Dim gate As New SemaphoreSlim(Math.Max(1, _opt.MaxParallelTools))
                        For Each s In reads.Where(Function(x) Not early.ContainsKey(x.Id))
                            Dim one = s
                            early(one.Id) = Task.Run(Async Function()
                                                         Await gate.WaitAsync(ct).ConfigureAwait(False)
                                                         Try
                                                             Return Await runOne(one).ConfigureAwait(False)
                                                         Finally
                                                             gate.Release()
                                                         End Try
                                                     End Function)
                        Next
                    End If
                End If
                For Each s In steps
                    Dim result As (Boolean, String)
                    Dim started As Task(Of (Boolean, String)) = Nothing
                    If early.TryGetValue(s.Id, started) Then
                        result = Await started.ConfigureAwait(False)
                    Else
                        result = Await runOne(s).ConfigureAwait(False)
                    End If
                    Dim id = s.Id
                    Dim note = ""
                    If result.Item1 Then
                        failStreak = 0
                        ' Progress for the check before its steps run low: a result it had not seen before.
                        If seenResults.Add(Convert.ToHexString(Global.System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(If(result.Item2, ""))))) Then stretchProgress += 1
                    Else
                        failStreak += 1
                        If failStreak = FailStreakNudge Then
                            note = vbLf & vbLf & $"({failStreak} tool calls have failed in a row. Stop trying small variations: work out the cause from the errors, change your approach, or report what is blocking you.)"
                        ElseIf failStreak >= FailStreakStop AndAlso Not toolsOff Then
                            toolsOff = True
                            note = vbLf & vbLf & "(Too many tool calls have failed in a row. Tools are now off: write your report - what you found, what failed and why.)"
                        End If
                    End If
                    messages.Add(New JsonObject From {{"role", "tool"}, {"tool_call_id", id}, {"content", ToolResultForModel(result.Item2, 12000) & note}})
                Next
                ' Steps running out: first a progress check - still getting things done (new results in this stretch), it gets another
                ' stretch. Otherwise it hears so ahead, so it finishes the essential part and keeps its last step for the report.
                If Not toolsOff AndAlso own.Count > 0 Then
                    Dim toolStepsLeft = maxRounds - 1 - round
                    If toolStepsLeft = 3 AndAlso stretchProgress > 0 AndAlso maxRounds < roundCap Then
                        maxRounds = CInt(Math.Min(CLng(maxRounds) + stretch, CLng(roundCap)))
                        stretchProgress = 0
                        toolStepsLeft = maxRounds - 1 - round
                        Await progress($"still getting things done after {round} steps: carrying on…").ConfigureAwait(False)
                    End If
                    If toolStepsLeft = 3 Then
                        AddUserNote(messages, "(Note from the app: 3 more steps with tools, then one last step without tools for your report. Finish the essential part now.)")
                    ElseIf toolStepsLeft = 0 Then
                        AddUserNote(messages, "(Note from the app: you have used all your steps with tools; tools are now off. Write your final report now: what you found or did, " &
                                              "with exact file paths, commands and values; what is not finished yet; and what is left to do.)")
                    End If
                End If
            End While
            ' Not reached (its last step has no tools, so it ends there), but a report goes back whatever happens.
            Return stoppedEarly($"it used all {maxRounds} steps", $"The subagent '{label}' used all {maxRounds} steps without finishing.")
        End Function

        ' ------------------------------------------------------------------ subagents that can be continued (resume)

        ''' <summary>A subagent's conversation after it ended, so the answer (or a later one in the same chat) can continue it.</summary>
        Private NotInheritable Class KeptSubagent
            Public Property Messages As JsonArray
            Public Property Label As String
            Public Property Explore As Boolean
            Public Property At As DateTime
        End Class

        Friend Const SubagentKeepHours As Integer = 2
        ''' <summary>At most this many are kept (each holds a whole context); the oldest go first.</summary>
        Private Const SubagentKeepMax As Integer = 24
        Private ReadOnly _keptSubagents As New ConcurrentDictionary(Of String, KeptSubagent)(StringComparer.Ordinal)

        Private Shared Function SubagentKey(username As String, chatId As String, id As String) As String
            Return username & "|" & chatId & "|" & id
        End Function

        Private Sub KeepSubagent(username As String, chatId As String, id As String, messages As JsonArray, label As String, explore As Boolean)
            Dim now = DateTime.UtcNow
            For Each kv In _keptSubagents
                If now - kv.Value.At > TimeSpan.FromHours(SubagentKeepHours) Then _keptSubagents.TryRemove(kv.Key, Nothing)
            Next
            While _keptSubagents.Count >= SubagentKeepMax
                Dim oldest = _keptSubagents.OrderBy(Function(x) x.Value.At).FirstOrDefault()
                If oldest.Key Is Nothing OrElse Not _keptSubagents.TryRemove(oldest.Key, Nothing) Then Exit While
            End While
            _keptSubagents(SubagentKey(username, chatId, id)) = New KeptSubagent With {.Messages = messages, .Label = label, .Explore = explore, .At = now}
        End Sub

        ''' <summary>Whether a subagent with this id can be continued in this chat (tests).</summary>
        Friend Function CanResumeSubagent(username As String, chatId As String, id As String) As Boolean
            Return _keptSubagents.ContainsKey(SubagentKey(username, chatId, id))
        End Function

        Private Shared Function ResumeHint(id As String) As String
            Return $"To continue it where it stopped (it keeps everything it read and did, so nothing is redone), call {SubagentTool} with resume ""{id}"" " &
                   "and a prompt saying what is left."
        End Function

        ''' <summary>A note from the app as the next user message (added to the last one when that is already the app's or the user's).</summary>
        Friend Shared Sub AddUserNote(messages As JsonArray, note As String)
            Dim last = If(messages.Count > 0, TryCast(messages(messages.Count - 1), JsonObject), Nothing)
            If last IsNot Nothing AndAlso last("role")?.ToString() = "user" AndAlso TypeOf last("content") Is JsonValue Then
                last("content") = last("content").ToString() & vbLf & vbLf & note
            Else
                messages.Add(New JsonObject From {{"role", "user"}, {"content", note}})
            End If
        End Sub

        ''' <summary>
        ''' What a subagent did, from its conversation after start: each tool call with the start of its result, and what it wrote,
        ''' newest last, within maxChars (its first steps and as many of the latest as fit). Empty when it did nothing.
        ''' </summary>
        ''' <summary>The end of a long text (its latest part), marked when cut.</summary>
        Friend Shared Function TailOf(text As String, maxChars As Integer) As String
            Dim t = Regex.Replace(If(text, ""), "</?think>", "", RegexOptions.IgnoreCase).Trim()
            If t.Length <= maxChars Then Return t
            Return "…" & t.Substring(t.Length - maxChars)
        End Function

        Friend Shared Function SubagentSteps(messages As JsonArray, start As Integer, Optional maxChars As Integer = 8000) As String
            Dim lines As New List(Of String)
            Dim calledAs As New Dictionary(Of String, String)(StringComparer.Ordinal)
            For i = Math.Max(0, start) To messages.Count - 1
                Dim m = TryCast(messages(i), JsonObject)
                If m Is Nothing Then Continue For
                Select Case m("role")?.ToString()
                    Case "assistant"
                        Dim said = Regex.Replace(If(m("content")?.ToString(), ""), "\s+", " ").Trim()
                        If said.Length > 0 Then lines.Add("- It wrote: " & TextUtil.Truncate(said, 600))
                        Dim made = TryCast(m("tool_calls"), JsonArray)
                        If made Is Nothing Then Continue For
                        For Each tc In made
                            Dim target = ToolTarget(tc?("function")?("arguments")?.ToString())
                            calledAs(If(tc?("id")?.ToString(), "")) = If(tc?("function")?("name")?.ToString(), "tool") &
                                                                     If(String.IsNullOrEmpty(target), "", " " & TextUtil.Truncate(target, 120))
                        Next
                    Case "tool"
                        Dim callName As String = Nothing
                        If Not calledAs.TryGetValue(If(m("tool_call_id")?.ToString(), ""), callName) Then callName = "tool"
                        lines.Add($"- {callName}: {TextUtil.Truncate(Regex.Replace(If(m("content")?.ToString(), ""), "\s+", " ").Trim(), 300)}")
                End Select
            Next
            If lines.Count = 0 Then Return ""
            If lines.Sum(Function(l) l.Length + 1) <= maxChars Then Return String.Join(vbLf, lines)
            Dim head = lines.Take(3).ToList()
            Dim tail As New List(Of String)
            Dim size = head.Sum(Function(l) l.Length + 1) + 40
            For i = lines.Count - 1 To head.Count Step -1
                If size + lines(i).Length + 1 > maxChars Then Exit For
                tail.Insert(0, lines(i))
                size += lines(i).Length + 1
            Next
            Return String.Join(vbLf, head) & vbLf & $"- … ({lines.Count - head.Count - tail.Count} steps left out) …" & vbLf & String.Join(vbLf, tail)
        End Function

        ' ------------------------------------------------------------------ subagent streams (their cards)

        ''' <summary>Running subagents' streams by the call id of their card; the transcript goes into the saved tool call.</summary>
        Private ReadOnly _subagentStreams As New ConcurrentDictionary(Of String, SubagentStream)(StringComparer.Ordinal)

        ''' <summary>What a subagent did (its card's transcript), once, when its tool call is saved; Nothing for other calls.</summary>
        Friend Function TakeSubagentTranscript(callId As String) As SubagentTranscript
            Dim s As SubagentStream = Nothing
            If String.IsNullOrEmpty(callId) OrElse Not _subagentStreams.TryRemove(callId, s) Then Return Nothing
            SyncLock s.Transcript
                Return s.Transcript.Copy()
            End SyncLock
        End Function

        Private NotInheritable Class SubagentLink
            Public Username As String
            Public ParentChatId As String
            Public ParentCallId As String
            Public Label As String
            Public Type As String
            Public AtUtc As DateTime
        End Class

        ''' <summary>A client's subagent conversations (the CLI's Task makes one per subagent) and the card that started each.</summary>
        Private ReadOnly _subagentLinks As New ConcurrentDictionary(Of String, SubagentLink)(StringComparer.Ordinal)

        ''' <summary>
        ''' Links a client's subagent conversation to its Task card in the parent chat: its answers stream into that card in every
        ''' window and are saved with that call. Links are forgotten after a day.
        ''' </summary>
        Public Sub LinkSubagent(username As String, chatId As String, parentChatId As String, parentCallId As String, label As String, type As String)
            If String.IsNullOrWhiteSpace(chatId) OrElse String.IsNullOrWhiteSpace(parentChatId) OrElse String.IsNullOrWhiteSpace(parentCallId) Then Return
            For Each old In _subagentLinks.Where(Function(kv) kv.Value.AtUtc < DateTime.UtcNow.AddDays(-1)).Select(Function(kv) kv.Key).ToList()
                Dim gone As SubagentLink = Nothing
                _subagentLinks.TryRemove(old, gone)
            Next
            _subagentLinks(chatId) = New SubagentLink With {.Username = username, .ParentChatId = parentChatId, .ParentCallId = TextUtil.Truncate(parentCallId, 120),
                                                            .Label = TextUtil.Truncate(If(label, "subagent"), 80), .Type = TextUtil.Truncate(type, 40), .AtUtc = DateTime.UtcNow}
        End Sub

        ''' <summary>The parent card's stream for an answer in a linked subagent conversation (Nothing for other chats).</summary>
        Private Function SubagentFeedFor(username As String, chatId As String) As SubagentStream
            Dim link As SubagentLink = Nothing
            If Not _subagentLinks.TryGetValue(chatId, link) OrElse link.Username <> username Then Return Nothing
            Dim feed As New SubagentStream(link.ParentCallId, Function(type, data)
                                                                  ' Relayed through the live hub only: even the window that asked takes it from there.
                                                                  Dim e = TryCast(data, JsonObject)
                                                                  If e IsNot Nothing Then e("relayed") = True
                                                                  Live.RecordChat(link.Username, link.ParentChatId, type, data)
                                                                  Return Task.CompletedTask
                                                              End Function)
            _subagentStreams(link.ParentCallId) = feed
            Dim unused = feed.StartAsync(link.Label, link.Type)
            Return feed
        End Function

        ''' <summary>One event of a linked subagent's answer, for its card in the parent chat.</summary>
        Private Async Function RelayToParentAsync(feed As SubagentStream, type As String, payload As Object) As Task
            Select Case type
                Case "delta", "reasoning"
                    Await feed.WriteAsync(If(type = "delta", "text", "reasoning"), JsonUtil.ToNode(payload)?("text")?.ToString()).ConfigureAwait(False)
                Case "tool"
                    Dim n = JsonUtil.ToNode(payload)
                    Dim c = n?("call")
                    Dim id = c?("id")?.ToString()
                    If String.IsNullOrEmpty(id) Then Return
                    Dim name = c("name")?.ToString()
                    Dim target = ToolTarget(If(c("arguments")?.ToString(), "{}"))
                    If n("phase")?.ToString() = "end" Then
                        Await feed.ToolEndAsync(id, name, target, String.Equals(c("ok")?.ToString(), "true", StringComparison.OrdinalIgnoreCase), c("result")?.ToString()).ConfigureAwait(False)
                    Else
                        Await feed.ToolStartAsync(id, name, target).ConfigureAwait(False)
                    End If
            End Select
        End Function

        ''' <summary>
        ''' Reads a subagent's streamed step: what it writes and thinks goes to its card as it comes. Returns the step in the
        ''' non-streamed form (choices[0].message with content and tool_calls, and usage) for the rest of the subagent loop.
        ''' </summary>
        ''' <remarks>
        ''' A stream that goes quiet for idleSeconds, or ends without [DONE] or a finish reason (the connection dropped mid-answer), is a
        ''' failure (IOException), so the step is asked again instead of taking a cut-off answer as the whole one.
        ''' </remarks>
        Private Shared Async Function ReadSubagentStreamAsync(resp As HttpResponseMessage, stream As SubagentStream, ct As CancellationToken,
                                                              Optional idleSeconds As Integer = 300) As Task(Of JsonNode)
            Dim content As New StringBuilder()
            ' What it thought: some models answer only there, so a step without text can still give back its thoughts.
            Dim thinking As New StringBuilder()
            Dim calls As New SortedDictionary(Of Integer, (Id As String, Name As StringBuilder, Args As StringBuilder))
            Dim usage As JsonNode = Nothing
            Dim finish As String = Nothing
            Dim tags As New ThinkTagSplitter()
            Dim done = False
            Using s = Await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(False), reader As New IO.StreamReader(s, Encoding.UTF8),
                  idle = CancellationTokenSource.CreateLinkedTokenSource(ct)
                While True
                    idle.CancelAfter(TimeSpan.FromSeconds(idleSeconds))
                    Dim line As String
                    Try
                        line = Await reader.ReadLineAsync(idle.Token).ConfigureAwait(False)
                    Catch ex As OperationCanceledException When Not ct.IsCancellationRequested
                        Throw New IO.IOException($"the model sent nothing for {idleSeconds} s")
                    End Try
                    If line Is Nothing Then Exit While
                    If Not line.StartsWith("data:", StringComparison.Ordinal) Then Continue While
                    Dim payload = line.Substring(5).Trim()
                    If payload = "[DONE]" Then
                        done = True
                        Exit While
                    End If
                    Dim chunk As JsonNode = Nothing
                    Try
                        chunk = JsonNode.Parse(payload)
                    Catch ex As JsonException
                        Continue While
                    End Try
                    Dim errNode = chunk?("error")
                    If errNode IsNot Nothing Then Throw New HttpRequestException(If(errNode("message")?.ToString(), errNode.ToJsonString()))
                    If TypeOf chunk?("usage") Is JsonObject Then usage = chunk("usage").DeepClone()
                    Dim choice = JsonUtil.FirstChoice(chunk)
                    Dim fr = choice?("finish_reason")?.ToString()
                    If Not String.IsNullOrEmpty(fr) Then finish = fr
                    Dim delta = TryCast(choice?("delta"), JsonObject)
                    If delta Is Nothing Then Continue While
                    Dim thought = If(delta("reasoning_content")?.ToString(), delta("reasoning")?.ToString())
                    thinking.Append(thought)
                    Await stream.WriteAsync("reasoning", thought).ConfigureAwait(False)
                    Dim c = delta("content")?.ToString()
                    If Not String.IsNullOrEmpty(c) Then
                        For Each part In tags.Feed(c)
                            If part.Think Then
                                thinking.Append(part.Text)
                                Await stream.WriteAsync("reasoning", part.Text).ConfigureAwait(False)
                            Else
                                content.Append(part.Text)
                                Await stream.WriteAsync("text", part.Text).ConfigureAwait(False)
                            End If
                        Next
                    End If
                    Dim tcs = TryCast(delta("tool_calls"), JsonArray)
                    If tcs Is Nothing Then Continue While
                    For Each tc In tcs
                        Dim idx = calls.Count
                        If tc?("index") IsNot Nothing Then Integer.TryParse(tc("index").ToString(), idx)
                        Dim entry As (Id As String, Name As StringBuilder, Args As StringBuilder) = Nothing
                        If Not calls.TryGetValue(idx, entry) Then entry = (Nothing, New StringBuilder(), New StringBuilder())
                        Dim id = tc?("id")?.ToString()
                        If Not String.IsNullOrEmpty(id) Then entry.Id = id
                        Dim n = tc?("function")?("name")?.ToString()
                        ' Some providers send the whole name in every chunk, others in pieces.
                        If Not String.IsNullOrEmpty(n) AndAlso Not entry.Name.ToString().EndsWith(n, StringComparison.Ordinal) Then entry.Name.Append(n)
                        Dim a = tc?("function")?("arguments")?.ToString()
                        If a IsNot Nothing Then entry.Args.Append(a)
                        calls(idx) = entry
                    Next
                End While
            End Using
            ' The connection dropped mid-answer: no [DONE] and no finish reason. Not an answer: the step is asked again.
            If Not done AndAlso String.IsNullOrEmpty(finish) Then Throw New IO.IOException("the model's stream ended before the answer was complete")
            Dim rest = tags.Finish()
            If rest.Text.Length > 0 Then
                If rest.Think Then
                    thinking.Append(rest.Text)
                    Await stream.WriteAsync("reasoning", rest.Text).ConfigureAwait(False)
                Else
                    content.Append(rest.Text)
                    Await stream.WriteAsync("text", rest.Text).ConfigureAwait(False)
                End If
            End If
            Await stream.FlushAsync().ConfigureAwait(False)
            Dim message As New JsonObject From {{"role", "assistant"}, {"content", content.ToString()}}
            If thinking.Length > 0 Then message("reasoning_content") = thinking.ToString()
            Dim made = calls.Values.Where(Function(x) x.Name.Length > 0).ToList()
            If made.Count > 0 Then
                message("tool_calls") = New JsonArray(made.Select(Function(x) CType(New JsonObject From {
                    {"id", If(x.Id, Ids.NewId("call"))}, {"type", "function"},
                    {"function", New JsonObject From {{"name", x.Name.ToString()}, {"arguments", If(x.Args.Length = 0, "{}", x.Args.ToString())}}}}, JsonNode)).ToArray())
            End If
            Dim result As New JsonObject From {{"choices", New JsonArray(New JsonObject From {{"index", 0}, {"message", message}, {"finish_reason", finish}})}}
            If usage IsNot Nothing Then result("usage") = usage
            Return result
        End Function

        ''' <summary>
        ''' A call that may run at the same time as the other calls of its step: it only reads, the user's rules neither forbid nor
        ''' ask about it, and the permission mode would not ask either (an approval must stay a question asked one at a time).
        ''' </summary>
        Private Function ParallelReadable(binding As ToolBinding, name As String, args As String, chatId As String, settings As ChatSettings, rules As PermissionRules,
                                          Optional allowWeb As Boolean = False) As Boolean
            If binding Is Nothing OrElse Not IsReadOnlyBinding(binding) Then Return False
            ' Web search and page reads count against per-answer limits in the answer's own loop (not in a subagent's); screenshots come back as pictures.
            If Not allowWeb AndAlso (binding.Server = "web" OrElse binding.Server = "page") Then Return False
            If name.IndexOf("screenshot", StringComparison.OrdinalIgnoreCase) >= 0 Then Return False
            Dim rule = rules.Decide(name, args)
            If rule = "deny" OrElse rule = "ask" Then Return False
            Return rule = "allow" OrElse ApprovalReason(chatId, settings, binding.Risk, binding.Server, name, args) Is Nothing
        End Function

        ' ------------------------------------------------------------------ steering: messages added while an answer runs

        ''' <summary>A message waiting for the model's next step; its id lets the user take it back until then.</summary>
        Public NotInheritable Class PendingSteer
            Public Property Id As String
            Public Property Text As String
            Public Property AtUtc As DateTime
        End Class

        Private ReadOnly _steers As New ConcurrentDictionary(Of String, List(Of PendingSteer))(StringComparer.Ordinal)

        ''' <summary>A message the user sends while the answer is running: it reaches the model before its next step.</summary>
        Public Function Steer(chatId As String, text As String) As Boolean
            Return AddSteer(chatId, text) IsNot Nothing
        End Function

        ''' <summary>Like Steer, returning the waiting message (Nothing when no answer is running).</summary>
        Public Function AddSteer(chatId As String, text As String) As PendingSteer
            If String.IsNullOrWhiteSpace(text) OrElse Not Live.IsLive(chatId) Then Return Nothing
            Dim s As New PendingSteer With {.Id = Ids.NewId("steer"), .Text = TextUtil.Truncate(text.Trim(), 8000), .AtUtc = DateTime.UtcNow}
            Dim list = _steers.GetOrAdd(chatId, Function(k) New List(Of PendingSteer)())
            SyncLock list
                list.Add(s)
            End SyncLock
            PublishSteers(chatId)
            Return s
        End Function

        ''' <summary>The messages the model has not read yet, oldest first.</summary>
        Public Function PendingSteers(chatId As String) As List(Of PendingSteer)
            Dim list As List(Of PendingSteer) = Nothing
            If Not _steers.TryGetValue(chatId, list) Then Return New List(Of PendingSteer)
            SyncLock list
                Return list.ToList()
            End SyncLock
        End Function

        ''' <summary>Takes back a message the model has not read yet; False when it already did (or there is no such message).</summary>
        Public Function CancelSteer(chatId As String, steerId As String) As Boolean
            Dim list As List(Of PendingSteer) = Nothing
            If String.IsNullOrEmpty(steerId) OrElse Not _steers.TryGetValue(chatId, list) Then Return False
            Dim removed As Boolean
            SyncLock list
                removed = list.RemoveAll(Function(s) s.Id = steerId) > 0
            End SyncLock
            If removed Then PublishSteers(chatId)
            Return removed
        End Function

        Private Function TakeSteers(chatId As String) As List(Of PendingSteer)
            Dim list As List(Of PendingSteer) = Nothing
            If Not _steers.TryGetValue(chatId, list) Then Return New List(Of PendingSteer)
            Dim taken As List(Of PendingSteer)
            SyncLock list
                taken = list.ToList()
                list.Clear()
            End SyncLock
            If taken.Count > 0 Then PublishSteers(chatId)
            Return taken
        End Function

        ''' <summary>Every window of the user shows the messages waiting for the model (and can take one back).</summary>
        Private Sub PublishSteers(chatId As String)
            Dim user = Live.RunUser(chatId)
            If user Is Nothing Then Return
            Live.Publish(user, chatId, "steers", New With {.items = PendingSteers(chatId).Select(Function(s) New With {.id = s.Id, .text = s.Text}).ToList()})
        End Sub

        ' ------------------------------------------------------------------ output styles

        Friend Shared Function NormalizeStyle(style As String) As String
            Select Case If(style, "").Trim().ToLowerInvariant()
                Case "explanatory", "learning", "concise" : Return style.Trim().ToLowerInvariant()
                Case Else : Return Nothing
            End Select
        End Function

        Friend Shared Function StyleInstructions(style As String) As String
            Select Case NormalizeStyle(style)
                Case "explanatory"
                    Return "Output style: explanatory. While you work, add short insights about why you choose an approach, the code base's patterns and trade-offs, so the user learns as you go."
                Case "learning"
                    Return "Output style: learning. Teach while you work: explain your reasoning, and for small, well-defined parts leave a clearly marked TODO(human) for the user to write themselves, with a hint, instead of writing it all."
                Case "concise"
                    Return "Output style: concise. Answer as briefly as possible: no preamble, no recap, short sentences and lists; give code and facts, skip explanations unless asked."
                Case Else : Return Nothing
            End Select
        End Function

        ' ------------------------------------------------------------------ permission rules

        Public Const PermissionRulesKey As String = "chat.permissionRules"

        Public Async Function GetPermissionRulesAsync(username As String) As Task(Of PermissionRules)
            Return PermissionRules.Parse(Await GetPreferenceAsync(username, PermissionRulesKey).ConfigureAwait(False))
        End Function

        Public Function SetPermissionRulesAsync(username As String, rules As PermissionRules) As Task
            Return SetPreferenceAsync(username, PermissionRulesKey, rules.ToJson())
        End Function
    End Class

    ''' <summary>
    ''' The user's permission rules, like Claude Code's: patterns in deny / ask / allow lists, checked in that order. A pattern
    ''' is a tool name (wildcards *) or tool(argument): the argument pattern is matched against the call's file, command, URL or
    ''' query, e.g. "run_command(npm test*)", "write_file(*.md)", "*__delete*". deny blocks the call, ask asks first (Auto
    ''' mode too), allow never asks (Manual mode too).
    ''' </summary>
    Public NotInheritable Class PermissionRules
        Public Property Allow As New List(Of String)
        Public Property Ask As New List(Of String)
        Public Property Deny As New List(Of String)

        Public Shared Function Parse(json As String) As PermissionRules
            Dim r = If(String.IsNullOrWhiteSpace(json), Nothing, JsonUtil.FromJson(Of PermissionRules)(json))
            r = If(r, New PermissionRules())
            r.Allow = Clean(r.Allow) : r.Ask = Clean(r.Ask) : r.Deny = Clean(r.Deny)
            Return r
        End Function

        Private Shared Function Clean(list As List(Of String)) As List(Of String)
            Return If(list, New List(Of String)).Where(Function(x) Not String.IsNullOrWhiteSpace(x)).Select(Function(x) TextUtil.Truncate(x.Trim(), 300)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200).ToList()
        End Function

        Public Function ToJson() As String
            Return JsonUtil.ToJson(New With {.allow = Allow, .ask = Ask, .deny = Deny})
        End Function

        ''' <summary>These rules and another set's (the computer's settings files): each list has both.</summary>
        Public Function MergedWith(other As PermissionRules) As PermissionRules
            If other Is Nothing Then Return Me
            Return New PermissionRules With {.Allow = Clean(Allow.Concat(other.Allow).ToList()), .Ask = Clean(Ask.Concat(other.Ask).ToList()), .Deny = Clean(Deny.Concat(other.Deny).ToList())}
        End Function

        ''' <summary>"deny", "ask", "allow", or Nothing when no rule matches.</summary>
        Public Function Decide(tool As String, argsJson As String) As String
            If Matching(tool, argsJson, "deny") IsNot Nothing Then Return "deny"
            If Matching(tool, argsJson, "ask") IsNot Nothing Then Return "ask"
            If Matching(tool, argsJson, "allow") IsNot Nothing Then Return "allow"
            Return Nothing
        End Function

        ''' <summary>The first rule of that list that matches the call, or Nothing.</summary>
        Public Function Matching(tool As String, argsJson As String, list As String) As String
            Dim rules = If(list = "deny", Deny, If(list = "ask", Ask, Allow))
            Dim target = ChatService.ToolTarget(argsJson)
            For Each rule In rules
                Dim m = Regex.Match(rule, "^\s*([^()\s]+)\s*(?:\((.*)\))?\s*$")
                If Not m.Success Then Continue For
                If Not Glob(m.Groups(1).Value, If(tool, "")) Then Continue For
                If m.Groups(2).Success AndAlso m.Groups(2).Value.Length > 0 Then
                    If target Is Nothing OrElse Not Glob(m.Groups(2).Value, target) Then Continue For
                End If
                Return rule
            Next
            Return Nothing
        End Function

        Private Shared Function Glob(pattern As String, value As String) As Boolean
            Dim re = "^" & Regex.Escape(pattern.Trim()).Replace("\*", ".*").Replace("\?", ".") & "$"
            Return Regex.IsMatch(value, re, RegexOptions.IgnoreCase Or RegexOptions.Singleline)
        End Function
    End Class

End Namespace
