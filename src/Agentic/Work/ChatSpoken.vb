Option Strict On
Option Explicit On
Option Infer On

Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A spoken summary and what writing it took (charged at the model's price).</summary>
    Public NotInheritable Class SpokenSummary
        Public Property Text As String
        ''' <summary>The model asked (a custom model's id when one was).</summary>
        Public Property Model As String
        ''' <summary>The model behind a custom model that wrote it; Nothing otherwise.</summary>
        Public Property RoutedModel As String
        Public Property PromptTokens As Long
        Public Property CompletionTokens As Long
        Public Property CachedTokens As Long
        ''' <summary>No model wrote it in time: the answer's first sentences instead (not kept with the answer, not charged).</summary>
        Public Property Fallback As Boolean
    End Class

    ''' <summary>Every model that could write the spoken summary is off limits to the user now (no access, or an empty balance).</summary>
    Public NotInheritable Class SpokenSummaryBlockedException
        Inherits Exception

        Public Sub New(message As String)
            MyBase.New(message)
        End Sub
    End Class

    ' Dictation mode's spoken summary (TL;DR): after each answer a model writes a few sentences about it in the voice's
    ' personality, and only those are spoken - not the answer's paragraphs, code and tables.
    Partial Public NotInheritable Class ChatService

        Private Shared ReadOnly ThinkBlock As New Regex("<think>[\s\S]*?(</think>|$)", RegexOptions.Compiled Or RegexOptions.IgnoreCase)
        Private Shared ReadOnly SummaryLabel As New Regex("^\s*(tl;?dr|summary|spoken summary)\s*[:\-–—]\s*", RegexOptions.Compiled Or RegexOptions.IgnoreCase)

        ''' <summary>The language names spoken summaries are asked for (the speech server's language codes).</summary>
        Private Shared ReadOnly LanguageNames As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase) From {
            {"en", "English"}, {"es", "Spanish"}, {"fr", "French"}, {"de", "German"}, {"it", "Italian"}, {"pt", "Portuguese"}, {"pl", "Polish"},
            {"tr", "Turkish"}, {"ru", "Russian"}, {"nl", "Dutch"}, {"cs", "Czech"}, {"ar", "Arabic"}, {"zh-cn", "Chinese (Mandarin)"},
            {"hu", "Hungarian"}, {"ko", "Korean"}, {"ja", "Japanese"}, {"hi", "Hindi"}}

        ''' <summary>
        ''' Writes the spoken summary of an answer (chatId and messageId; it is saved with the message) or of a text (a test of a
        ''' voice's personality). persona is the voice's personality; models are tried in order (the first that answers wins),
        ''' then the answer's model, Chat.TaskModel and Chat.DefaultModel. mayUse says why the user may not use a model now (no
        ''' access, an empty balance) or Nothing; such models are skipped, and when every model was skipped that reason is the
        ''' error (SpokenSummaryBlockedException). Returns the call's token use, for charging it at the model's price.
        ''' </summary>
        Public Async Function SpokenSummaryAsync(username As String, chatId As String, messageId As String, text As String, voice As String, persona As String,
                                                 models As IEnumerable(Of String), words As Integer, language As String, mayUse As Func(Of String, Task(Of String)),
                                                 ct As CancellationToken, Optional budget As TimeSpan = Nothing) As Task(Of SpokenSummary)
            Dim answer As String = text
            Dim question As String = Nothing
            Dim toolsUsed As String = Nothing
            Dim answerModel As String = Nothing
            Dim message As ChatMessageRecord = Nothing
            If Not String.IsNullOrEmpty(chatId) Then
                Dim chat = Await GetAsync(username, chatId).ConfigureAwait(False)
                If chat Is Nothing Then Throw New KeyNotFoundException("Conversation not found.")
                Dim at = chat.Messages.FindIndex(Function(m) m.Id = messageId AndAlso m.Role = "assistant")
                If at < 0 Then Throw New KeyNotFoundException("That answer was not found.")
                message = chat.Messages(at)
                answer = message.Content
                answerModel = message.Model
                question = chat.Messages.Take(at).LastOrDefault(Function(m) m.Role = "user")?.Content
                Dim calls = message.ToolCalls.Where(Function(c) Not String.IsNullOrEmpty(c.Name)).GroupBy(Function(c) c.Name).
                    Select(Function(g) If(g.Count() > 1, $"{g.Key} ×{g.Count()}", g.Key)).ToList()
                If calls.Count > 0 Then toolsUsed = String.Join(", ", calls)
                If String.IsNullOrWhiteSpace(answer) AndAlso Not String.IsNullOrWhiteSpace(message.Error) Then answer = "(The answer failed: " & message.Error & ")"
            End If
            If String.IsNullOrWhiteSpace(answer) AndAlso toolsUsed Is Nothing Then Throw New ArgumentException("There is nothing to summarize.")

            Dim n = Math.Clamp(words, 10, 300)
            Dim sys As New StringBuilder()
            sys.Append(If(String.IsNullOrWhiteSpace(persona), "You are the voice of the user's assistant.", persona.Trim())).Append(vbLf & vbLf)
            sys.Append($"In that voice, tell the user out loud what the assistant's answer below says or did: the result, what matters most, and anything they have to do or decide. ")
            sys.Append($"Use at most {n} words. It will be spoken by text-to-speech, so write plain spoken sentences: no Markdown, lists, headings, code, file paths, URLs or emoji; ")
            sys.Append("spell out symbols, units and abbreviations the way they are said. If the answer asks the user a question or needs their input, end with that question. ")
            Dim langName As String = Nothing
            If Not String.IsNullOrWhiteSpace(language) AndAlso Not language.Equals("en", StringComparison.OrdinalIgnoreCase) Then
                If Not LanguageNames.TryGetValue(language, langName) Then langName = language
                sys.Append($"Speak {langName}. ")
            End If
            sys.Append("Reply with only the words to speak.")
            Dim usr As New StringBuilder()
            If Not String.IsNullOrWhiteSpace(question) Then usr.Append("The user said:" & vbLf).Append(TextUtil.Truncate(question, 2000)).Append(vbLf & vbLf)
            usr.Append("The assistant's answer:" & vbLf).Append(TextUtil.Truncate(If(answer, ""), 14000))
            If toolsUsed IsNot Nothing Then usr.Append(vbLf & vbLf & "Tools it used: " & toolsUsed)

            ' The models to ask, first that answers wins: the given ones (the personality's, the configured one), the answer's own,
            ' the task model, the chat's default model. All of them together get the budget (40 s by default): the user waits for
            ' it to be spoken, and a proxy in front (Cloudflare) cuts a request off at 100 s. When none wrote one in time, the
            ' answer's first sentences are spoken instead.
            Dim tried As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
            Dim blocked As New List(Of String)
            Dim candidates = If(models, Enumerable.Empty(Of String)()).Concat({answerModel, _opt.TaskModel, _opt.DefaultModel}).Where(Function(m) Not String.IsNullOrWhiteSpace(m)).ToList()
            Using timeLeft = CancellationTokenSource.CreateLinkedTokenSource(ct)
                timeLeft.CancelAfter(If(budget > TimeSpan.Zero, budget, TimeSpan.FromSeconds(40)))
                Dim r = Await AskForSummaryAsync(candidates, tried, blocked, mayUse, sys.ToString(), usr.ToString(), n, message, chatId, voice, timeLeft.Token).ConfigureAwait(False)
                If r IsNot Nothing Then Return r
            End Using
            ct.ThrowIfCancellationRequested()
            If blocked.Count > 0 AndAlso blocked.Count = tried.Count Then Throw New SpokenSummaryBlockedException(blocked(0))
            Dim first = FirstSentences(answer, n)
            If first IsNot Nothing Then Return New SpokenSummary With {.Text = first, .Fallback = True}
            Throw New InvalidOperationException(If(candidates.Count = 0, "No model is set to write the spoken summary (TextToSpeech.SummaryModel).", "No model wrote a spoken summary: " & String.Join(", ", tried) & " gave nothing back."))
        End Function

        ''' <summary>The first sentences of an answer, about words long, ready to speak (Nothing when it has no words).</summary>
        Friend Shared Function FirstSentences(answer As String, words As Integer) As String
            Dim t = CleanSpoken(Regex.Replace(If(answer, ""), "```[\s\S]*?(```|$)", " "), Integer.MaxValue \ 4)
            If t Is Nothing Then Return Nothing
            Dim result As New Text.StringBuilder()
            For Each s In Regex.Split(t, "(?<=[.!?])\s+")
                If result.Length > 0 AndAlso (result.ToString() & " " & s).Split(" "c).Length > Math.Max(10, words) Then Exit For
                If result.Length > 0 Then result.Append(" "c)
                result.Append(s)
            Next
            Dim parts = result.ToString().Split(" "c)
            Return If(parts.Length > words * 2, String.Join(" ", parts.Take(words * 2)) & "…", result.ToString())
        End Function

        Private Async Function AskForSummaryAsync(candidates As List(Of String), tried As HashSet(Of String), blocked As List(Of String), mayUse As Func(Of String, Task(Of String)),
                                                  sys As String, usr As String, n As Integer, message As ChatMessageRecord, chatId As String, voice As String,
                                                  ct As CancellationToken) As Task(Of SpokenSummary)
            For Each candidate In candidates
                If ct.IsCancellationRequested Then Return Nothing
                If Not tried.Add(candidate) Then Continue For
                Dim problem = If(mayUse Is Nothing, Nothing, Await mayUse(candidate).ConfigureAwait(False))
                If problem IsNot Nothing Then
                    blocked.Add(problem)
                    Continue For
                End If
                ' A custom (router) model writes through its default model.
                Dim custom = Await _hub.GetCustomAsync(candidate).ConfigureAwait(False)
                Dim model = If(custom Is Nothing, candidate, custom.Definition.DefaultModel)
                If String.IsNullOrWhiteSpace(model) Then Continue For
                Dim said = Await _hub.CompleteTextWithUsageAsync(model, sys, usr, Math.Max(400, n * 6), TimeSpan.FromSeconds(90), ct, suppressThinking:=True).ConfigureAwait(False)
                Dim clean = CleanSpoken(said.Text, n)
                If clean Is Nothing Then Continue For
                If message IsNot Nothing Then
                    Await _db.ExecuteAsync("UPDATE ChatMessages SET SpokenSummary=@t, SpokenVoice=@v WHERE Id=@id AND ChatId=@c",
                                           ("@t", CObj(clean)), ("@v", voice), ("@id", message.Id), ("@c", chatId)).ConfigureAwait(False)
                End If
                Return New SpokenSummary With {.Text = clean, .Model = candidate, .RoutedModel = If(custom Is Nothing, Nothing, model),
                                               .PromptTokens = said.PromptTokens, .CompletionTokens = said.CompletionTokens, .CachedTokens = said.CachedTokens}
            Next
            Return Nothing
        End Function

        ''' <summary>What a model wrote, ready to speak: no thinking, labels, Markdown marks or quotes around it, at most about twice the words asked.</summary>
        Friend Shared Function CleanSpoken(said As String, words As Integer) As String
            If String.IsNullOrWhiteSpace(said) Then Return Nothing
            Dim t = ThinkBlock.Replace(said, "")
            t = Regex.Replace(t, "```[\s\S]*?(```|$)", " ")
            t = Regex.Replace(t, "\[([^\]]+)\]\([^)]*\)", "$1")
            t = Regex.Replace(t, "^\s*(#{1,6}\s+|[-*+]\s+|\d+[.)]\s+|>\s?)", "", RegexOptions.Multiline)
            t = Regex.Replace(t, "[*_`~#|]+", "")
            t = Regex.Replace(t, "\s+", " ").Trim()
            t = SummaryLabel.Replace(t, "")
            t = t.Trim(""""c, "'"c, ChrW(&H201C), ChrW(&H201D), " "c)
            If t.Length = 0 Then Return Nothing
            Dim parts = t.Split(" "c)
            If parts.Length > words * 2 Then t = String.Join(" ", parts.Take(words * 2)).TrimEnd(","c, ";"c, ":"c) & "…"
            Return t
        End Function
    End Class

End Namespace
