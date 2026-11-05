Option Strict On
Option Explicit On
Option Infer On

Imports System.Net.Http
Imports System.Text.Json
Imports System.Text.Json.Nodes
Imports System.Threading
Imports Agentic.AI
Imports Agentic.Economy
Imports Agentic.Logging
Imports Agentic.Utilities

Namespace Work

    ''' <summary>The answer to a side question (/btw, /recap), which is not added to the chat.</summary>
    Public NotInheritable Class ChatAsideAnswer
        Public Property Answer As String
        Public Property Model As String
        Public Property PromptTokens As Long
        Public Property CompletionTokens As Long
        Public Property CostMicros As Long
    End Class

    ' Side questions (/btw, /recap) and deep research (/research).
    Partial Public NotInheritable Class ChatService

        ''' <summary>The most of the conversation a side question gets (characters; the newest messages are kept).</summary>
        Private Const AsideTranscriptChars As Integer = 60000

        Private Const AsideSystemPrompt As String =
            "You answer a side question about the conversation below. Answer only the side question, briefly and directly, from what the " &
            "conversation says (and your own knowledge where it says nothing). Do not continue the conversation's work, do not call tools, " &
            "and do not repeat the conversation back. If the conversation does not contain what is needed, say so."

        ''' <summary>/research: this message is researched in depth (sub-questions, several searches, sources read and cross-checked).</summary>
        Friend Shared ReadOnly ResearchInstructions As String =
            "DEEP RESEARCH (this message only): research the user's question thoroughly before you answer. " &
            "1. Split it into 3 to 6 specific sub-questions. " &
            "2. Search the web for each one (search_web) with focused queries. Run independent searches together in one response, or give each " &
            "sub-question to a subagent (spawn_subagent) when you have it, so they run at the same time. " &
            "3. Open the best sources with read_page for the facts, numbers and dates that snippets leave out; prefer primary, official and recent sources. " &
            "4. Check every important fact in at least two independent sources, and say where they disagree. " &
            "5. Then write a structured report: the short answer first, then a section for each sub-question, citing every fact with its source " &
            "as [n], and last what you could not confirm. Search more than once: do not answer from the first results alone."

        ''' <summary>
        ''' /btw: answers a side question from the conversation so far (the summary of older messages and the recent ones), with no
        ''' tools. Nothing is saved in the chat; the account pays for the tokens as for any answer, and the call shows in Usage.
        ''' </summary>
        Public Async Function AsideAsync(username As String, isAdmin As Boolean, chatId As String, question As String, model As String,
                                         ct As CancellationToken) As Task(Of ChatAsideAnswer)
            question = If(question, "").Trim()
            If question.Length = 0 Then Throw New ArgumentException("Ask a question: /btw <question>.")
            Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
            If chat Is Nothing Then Throw New KeyNotFoundException("Conversation does not exist.")
            model = If(String.IsNullOrWhiteSpace(model), chat.Model, model)
            If String.IsNullOrWhiteSpace(model) Then Throw New ArgumentException("Choose a model.")
            If IsExternalModel(model) Then Throw New ArgumentException("This chat is answered by Claude Code on a computer: ask your side question there.")
            If _hub.IsHidden(model) Then Throw New ArgumentException($"The model '{model}' is not available.")
            ' A custom model answers through its default model: a side question needs no routing.
            Dim custom = Await _hub.GetCustomAsync(model).ConfigureAwait(False)
            Dim used = If(custom Is Nothing OrElse String.IsNullOrWhiteSpace(custom.Definition.DefaultModel), model, custom.Definition.DefaultModel)
            Dim body As New JsonObject From {
                {"model", used}, {"stream", False}, {"max_tokens", 2000},
                {"messages", New JsonArray(
                    New JsonObject From {{"role", "system"}, {"content", AsideSystemPrompt & vbLf & vbLf & $"Current date and time (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm}."}},
                    New JsonObject From {{"role", "user"}, {"content", "<conversation>" & vbLf & AsideTranscript(chat) & vbLf & "</conversation>" & vbLf & vbLf &
                                                                        "Side question: " & TextUtil.Truncate(question, 4000)}})}}
            Dim started = Diagnostics.Stopwatch.StartNew()
            Dim text As String = Nothing, failure As String = Nothing, shown As String = Nothing
            Dim status = 0
            Dim prompt As Long = 0, completion As Long = 0, cached As Long = 0
            Using limit = CancellationTokenSource.CreateLinkedTokenSource(ct)
                limit.CancelAfter(TimeSpan.FromSeconds(Math.Max(60, _opt.ModelResponseSeconds)))
                Try
                    Using resp = Await _hub.SendAsync("/chat/completions", body.ToJsonString(), limit.Token).ConfigureAwait(False)
                        status = CInt(resp.StatusCode)
                        Dim raw = Await resp.Content.ReadAsStringAsync(limit.Token).ConfigureAwait(False)
                        If Not resp.IsSuccessStatusCode Then
                            failure = $"{status} {ProviderMessage(raw)}"
                            shown = ModelErrors.ForUser(ProviderMessage(raw), status)
                        Else
                            Dim json = JsonNode.Parse(raw)
                            text = StripThinking(JsonUtil.FirstChoice(json)?("message")?("content")?.ToString())?.Trim()
                            Dim u = json?("usage")
                            If u IsNot Nothing Then
                                Long.TryParse(u("prompt_tokens")?.ToString(), prompt)
                                Long.TryParse(u("completion_tokens")?.ToString(), completion)
                                cached = Math.Min(prompt, AccountService.CachedTokensOf(u))
                            End If
                        End If
                    End Using
                Catch ex As Exception When Not ct.IsCancellationRequested AndAlso
                                           (TypeOf ex Is OperationCanceledException OrElse TypeOf ex Is HttpRequestException OrElse TypeOf ex Is JsonException OrElse TypeOf ex Is IO.IOException)
                    failure = If(TypeOf ex Is OperationCanceledException, "timed out", ex.Message)
                    shown = If(TypeOf ex Is OperationCanceledException, "The model took too long to answer.", ModelErrors.ForUser(ex))
                End Try
            End Using
            If failure Is Nothing AndAlso String.IsNullOrWhiteSpace(text) Then
                failure = "the model returned no answer"
                shown = "The model returned an empty answer."
            End If
            If prompt = 0 Then prompt = Math.Max(1, body.ToJsonString().Length \ 4)
            If failure Is Nothing AndAlso completion = 0 Then completion = Math.Max(1, text.Length \ 4)
            Await _hub.LogUsageAsync(username, "chat", "chat/aside", model, used, Nothing, False, If(failure Is Nothing, 200, If(status >= 400, status, 502)),
                                     If(failure Is Nothing, prompt, 0), If(failure Is Nothing, completion, 0), started.ElapsedMilliseconds, failure).ConfigureAwait(False)
            If failure IsNot Nothing Then
                ' The provider's own text stays in the log; people get the kind of failure in a few words.
                _log.Warn("Chat", $"Side question failed in chat {chatId} ({used}): {TextUtil.Truncate(failure, 600)}")
                Throw New ModelRefusedException(shown)
            End If
            Dim cost As Long = 0
            If Accounts IsNot Nothing Then
                cost = Await Accounts.ChargeUsageAsync(username, isAdmin, AccountService.EntryChat, model, used, prompt, completion,
                                                       "Chat side question: " & chat.Title, cached).ConfigureAwait(False)
            End If
            Return New ChatAsideAnswer With {.Answer = text, .Model = used, .PromptTokens = prompt, .CompletionTokens = completion, .CostMicros = cost}
        End Function

        ''' <summary>
        ''' The conversation for a side question: the summary of older messages, then the recent messages (the newest kept when it is
        ''' long), with the tool results of the last answers so questions about what a command printed can be answered.
        ''' </summary>
        Friend Function AsideTranscript(chat As ChatConversation) As String
            Dim recent = RecentMessages(chat)
            Dim lastAnswers = recent.Where(Function(m) m.Role = "assistant").Reverse().Take(2).ToList()
            Dim parts As New List(Of String)
            Dim total = 0
            For Each m In Enumerable.Reverse(recent)
                Dim said = If(m.Role = "user", m.Content, If(IsAgentReport(m), ReportForHistory(m), If(m.Content, "") & QuestionsForHistory(m)))
                Dim calls = ReplayableCalls(m)
                If calls.Count > 0 Then
                    If lastAnswers.Contains(m) Then
                        For Each c In calls.Take(12)
                            said &= vbLf & $"(Tool {c.Name} {TextUtil.Truncate(c.Arguments, 200)} -> {TextUtil.Truncate(c.Result, 1500)})"
                        Next
                    Else
                        said &= vbLf & "(Used: " & String.Join(", ", calls.Select(Function(c) c.Name).Distinct().Take(12)) & ")"
                    End If
                End If
                If String.IsNullOrWhiteSpace(said) Then Continue For
                Dim line = If(m.Role = "user", "User: ", "Assistant: ") & TextUtil.Truncate(said.Trim(), 8000)
                If total + line.Length > AsideTranscriptChars Then Exit For
                parts.Insert(0, line)
                total += line.Length
            Next
            If Not String.IsNullOrWhiteSpace(chat.Summary) Then parts.Insert(0, "Summary of the earlier conversation: " & TextUtil.Truncate(chat.Summary, 8000))
            Return If(parts.Count = 0, "(nothing yet)", String.Join(vbLf & vbLf, parts))
        End Function

    End Class

End Namespace
