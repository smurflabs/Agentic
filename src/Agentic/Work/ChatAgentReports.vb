Option Strict On
Option Explicit On
Option Infer On

Imports Agentic.Persistence
Imports Agentic.Utilities

Namespace Work

    ' Agents in the chat: work handed to agents from a chat comes back into that chat, and finished agent work can be opened
    ' in a new chat to talk about it. Both appear as a message from the agent (FinishReason "agent_report").
    Partial Public NotInheritable Class ChatService

        Public Const AgentReportFinish As String = "agent_report"
        ''' <summary>A user message the server wrote itself: the goal's automatic continuation ("Keep working on the goal…").</summary>
        Public Const AutoGoalFinish As String = "auto_goal"
        ''' <summary>A user message that is monitor events (the monitor tool), written by the server.</summary>
        Public Const AutoMonitorFinish As String = "auto_monitor"

        ''' <summary>A message an agent posted into the chat (a delegated task's result, a global task's answer, work opened to discuss).</summary>
        Public Shared Function IsAgentReport(m As ChatMessageRecord) As Boolean
            Return m IsNot Nothing AndAlso String.Equals(m.FinishReason, AgentReportFinish, StringComparison.Ordinal)
        End Function

        ''' <summary>The agent of a report message ("agent:ResearchAgent" → ResearchAgent).</summary>
        Public Shared Function ReportAgentOf(m As ChatMessageRecord) As String
            Dim model = If(m?.Model, "")
            Return If(model.StartsWith("agent:", StringComparison.OrdinalIgnoreCase), model.Substring(6), model)
        End Function

        ''' <summary>
        ''' Posts work an agent did into one of the user's chats as a message from that agent. Every window showing the chat
        ''' loads it. Returns Nothing when the chat no longer exists.
        ''' </summary>
        Public Async Function PostAgentReportAsync(username As String, chatId As String, agentId As String, content As String) As Task(Of ChatMessageRecord)
            If String.IsNullOrWhiteSpace(username) OrElse String.IsNullOrWhiteSpace(chatId) OrElse String.IsNullOrWhiteSpace(content) Then Return Nothing
            Dim chat = Await GetAsync(username, chatId, False).ConfigureAwait(False)
            If chat Is Nothing Then Return Nothing
            Dim now = DateTime.UtcNow
            Dim m As New ChatMessageRecord With {
                .Id = Ids.NewId("msg"), .ChatId = chat.Id, .Role = "assistant", .Content = TextUtil.Truncate(content.Trim(), 60000),
                .Model = "agent:" & If(String.IsNullOrWhiteSpace(agentId), "host", agentId), .FinishReason = AgentReportFinish, .CreatedUtc = now}
            Await SaveMessageAsync(m).ConfigureAwait(False)
            Await _db.ExecuteAsync("UPDATE Chats SET UpdatedUtc=@n WHERE Id=@id", ("@n", CObj(Db.Ts(now))), ("@id", chat.Id)).ConfigureAwait(False)
            Live.Publish(username, chat.Id, "changed", New With {.agentReport = True, .agentId = agentId, .title = chat.Title})
            Return m
        End Function

        ''' <summary>A report as the model sees it in the chat's history: who posted it and that it came from outside the conversation.</summary>
        Friend Shared Function ReportForHistory(m As ChatMessageRecord) As String
            Return $"(Posted into this chat by the agent {ReportAgentOf(m)} - its work, not something you wrote in this conversation.)" & vbLf & If(m.Content, "")
        End Function

    End Class

End Namespace
