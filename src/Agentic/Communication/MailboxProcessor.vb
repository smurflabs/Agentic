Option Strict On
Option Explicit On
Option Infer On

Imports System.Threading
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Tools
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Communication

    ''' <summary>
    ''' Processes one agent's mailbox inside that agent's own loop (so the agent's task state is only ever mutated by
    ''' its own loop). Requests become message-triggered tasks, replies resume waiting tasks, information is recorded.
    ''' </summary>
    Friend NotInheritable Class MailboxProcessor
        Private ReadOnly _messages As MessageRepository
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _comms As AgentCommunicationService
        Private ReadOnly _resolver As WaitResolver
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _opt As CommunicationOptions
        Private ReadOnly _clock As IClock

        Public Sub New(messages As MessageRepository, tasks As TaskRepository, comms As AgentCommunicationService, resolver As WaitResolver,
                       events As IAgentEventBus, log As ILog, options As CommunicationOptions, clock As IClock)
            _messages = messages
            _tasks = tasks
            _comms = comms
            _resolver = resolver
            _events = events
            _log = log
            _opt = options
            _clock = clock
        End Sub

        ''' <summary>Returns the number of messages handled.</summary>
        Public Async Function ProcessAsync(agentId As String, profile As EffectiveProfile, scope As AgentScope, statusText As Func(Of String), ct As CancellationToken) As Task(Of Integer)
            Dim inbox = Await _messages.ListInboxAsync(agentId, 25).ConfigureAwait(False)
            Dim handled = 0
            For Each m In inbox
                ct.ThrowIfCancellationRequested()
                Dim failure As Exception = Nothing
                Try
                    Await HandleAsync(agentId, profile, scope, statusText, m).ConfigureAwait(False)
                    handled += 1
                Catch ex As Exception When Not ExceptionUtil.IsCancellation(ex)
                    failure = ex
                End Try
                If failure IsNot Nothing Then
                    _log.Warn("Mailbox", $"Failed to process message {m.Id}: {failure.Message}", agentId, ex:=failure)
                    Await _messages.MarkStatusAsync(m.Id, MessageStatus.Failed, "Recipient could not process the message: " & TextUtil.Truncate(failure.Message, 300)).ConfigureAwait(False)
                End If
            Next
            Return handled
        End Function

        Private Async Function HandleAsync(agentId As String, profile As EffectiveProfile, scope As AgentScope, statusText As Func(Of String), m As AgentMessage) As Task
            If m.Status = MessageStatus.Pending Then
                Await _messages.MarkStatusAsync(m.Id, MessageStatus.Delivered).ConfigureAwait(False)
                _events.Emit(AgentEventType.MessageReceived, $"{agentId} received [{m.MessageType}] from {m.SenderAgentId}: {TextUtil.Truncate(m.Subject, 80)}",
                             agentId, m.RelatedTaskId, m.Id, m.CorrelationId,
                             data:=New Dictionary(Of String, Object) From {{"sender", m.SenderAgentId}, {"type", m.MessageType.ToString()}})
            End If

            Select Case m.MessageType
                Case MessageType.Information, MessageType.Notification
                    If _opt.RecordInformationAsMemory AndAlso m.SenderAgentId <> AgentCommunicationService.AdminSender Then
                        Await scope.Memory.SaveMemoryAsync(MemoryCategory.Observation,
                            $"Message from {m.SenderAgentId} ({m.CreatedUtc:u}): {m.Subject} — {TextUtil.Truncate(m.Content, 1500)}" &
                            If(m.Attachments.Count > 0, " [attachments: " & String.Join(", ", m.Attachments) & "]", ""),
                            "message," & m.SenderAgentId, 2, Nothing, TimeSpan.FromDays(30)).ConfigureAwait(False)
                    ElseIf m.SenderAgentId = AgentCommunicationService.AdminSender Then
                        Await scope.Memory.SaveMemoryAsync(MemoryCategory.Fact, $"Administrator note ({m.CreatedUtc:u}): {m.Subject} — {TextUtil.Truncate(m.Content, 2000)}",
                                                           "admin", 4, Nothing, Nothing).ConfigureAwait(False)
                    End If
                    Await _messages.MarkStatusAsync(m.Id, MessageStatus.Completed).ConfigureAwait(False)

                Case MessageType.StatusRequest
                    ' Deterministic answer; no AI involved.
                    Dim status = statusText()
                    Await _comms.ReplyAsync(agentId, m.Id, TaskResultContract.Ok(status), CancellationToken.None).ConfigureAwait(False)

                Case MessageType.Question, MessageType.Request, MessageType.ApprovalRequest
                    Dim existing = Await _tasks.GetBySourceMessageAsync(m.Id).ConfigureAwait(False)
                    If existing Is Nothing Then
                        Dim payload As New Dictionary(Of String, Object) From {
                            {"from", m.SenderAgentId}, {"messageType", m.MessageType.ToString()}, {"subject", m.Subject}, {"content", m.Content}, {"messageId", m.Id}}
                        If Not String.IsNullOrEmpty(m.StructuredPayload) Then payload("payload") = JsonUtil.ParseElementOrNull(m.StructuredPayload)
                        If m.Attachments.Count > 0 Then payload("attachments") = m.Attachments
                        Dim now = _clock.UtcNow
                        Dim t As New AgentTask With {
                            .Id = Ids.NewId("tsk"), .AgentId = agentId, .Name = TextUtil.Truncate($"{m.MessageType} from {m.SenderAgentId}: {m.Subject}", 200),
                            .Description = m.Content, .Kind = TaskKind.MessageTriggered, .Handler = "reasoning", .Status = AgentTaskStatus.Pending,
                            .Priority = m.Priority, .Payload = JsonUtil.ToJson(payload), .MaxRetries = 2, .CreatedUtc = now, .UpdatedUtc = now,
                            .ParentTaskId = m.SenderTaskId, .ParentAgentId = m.SenderAgentId, .CorrelationId = m.CorrelationId,
                            .DelegationDepth = m.DelegationDepth, .ChainLength = m.ChainLength, .Ancestry = New List(Of String)(m.Ancestry),
                            .SourceMessageId = m.Id, .CreatedBy = m.SenderAgentId}
                        If Await _tasks.InsertAsync(t).ConfigureAwait(False) Then
                            Await _messages.SetRelatedTaskAsync(m.Id, t.Id).ConfigureAwait(False)
                            Await _tasks.AddHistoryAsync(t.Id, agentId, Nothing, AgentTaskStatus.Pending, $"Created from {m.MessageType} {m.Id}").ConfigureAwait(False)
                            _events.Emit(AgentEventType.TaskCreated, $"Task from {m.SenderAgentId}: {TextUtil.Truncate(m.Subject, 100)}", agentId, t.Id, m.Id, m.CorrelationId)
                        End If
                    End If
                    Await _messages.MarkStatusAsync(m.Id, MessageStatus.Processing).ConfigureAwait(False)

                Case MessageType.TaskDelegation
                    ' The delegated task was created atomically with the message.
                    Await _messages.MarkStatusAsync(m.Id, MessageStatus.Processing).ConfigureAwait(False)

                Case MessageType.Response, MessageType.TaskResult, MessageType.Error
                    Dim resumedTask As String = Nothing
                    If Not String.IsNullOrEmpty(m.ReplyToMessageId) Then
                        resumedTask = Await _resolver.ResolveAsync(agentId, "msg:" & m.ReplyToMessageId, Resolutions.AgentReply(m), $"Reply from {m.SenderAgentId}").ConfigureAwait(False)
                    End If
                    If resumedTask Is Nothing Then
                        ' Nobody is waiting (fire-and-forget delegation, or the wait already timed out): keep the result as memory.
                        Await scope.Memory.SaveMemoryAsync(MemoryCategory.TaskResult,
                            $"{m.MessageType} from {m.SenderAgentId} ({m.CreatedUtc:u}) re '{m.Subject}': {TextUtil.Truncate(m.Content, 2000)}",
                            "reply," & m.SenderAgentId, 3, Nothing, TimeSpan.FromDays(60)).ConfigureAwait(False)
                    End If
                    Await _messages.MarkStatusAsync(m.Id, MessageStatus.Completed).ConfigureAwait(False)
            End Select
        End Function
    End Class

End Namespace
