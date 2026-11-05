Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Threading
Imports Agentic.Artifacts
Imports Agentic.Configuration
Imports Agentic.Events
Imports Agentic.Logging
Imports Agentic.Persistence
Imports Agentic.Utilities
Imports Agentic.Work

Namespace Communication

    ''' <summary>Read-only lookup of effective agent profiles (host infrastructure; never exposed to agents).</summary>
    Public Interface IProfileProvider
        Function TryGetProfile(agentId As String) As EffectiveProfile
        Function AllProfiles() As IReadOnlyList(Of EffectiveProfile)
    End Interface

    ''' <summary>Lets host services nudge an agent loop without holding a reference to the agent.</summary>
    Public Interface IAgentWaker
        Sub Wake(agentId As String)
        Function IsRunning(agentId As String) As Boolean
        Function DescribeStatus(agentId As String) As String
    End Interface

    ''' <summary>Lineage of the work that is sending a message (the sender's current task).</summary>
    Public NotInheritable Class SendContext
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property DelegationDepth As Integer
        Public Property ChainLength As Integer
        Public Property Ancestry As New List(Of String)
        Public Property ToolCallId As String

        Public Shared Function FromTask(t As AgentTask, Optional toolCallId As String = Nothing) As SendContext
            If t Is Nothing Then Return New SendContext()
            Return New SendContext With {
                .TaskId = t.Id, .CorrelationId = t.CorrelationId, .DelegationDepth = t.DelegationDepth,
                .ChainLength = t.ChainLength, .Ancestry = New List(Of String)(If(t.Ancestry, New List(Of String))), .ToolCallId = toolCallId}
        End Function
    End Class

    Public NotInheritable Class OutgoingMessage
        Public Property RecipientAgentId As String
        Public Property MessageType As MessageType = MessageType.Information
        Public Property Subject As String
        Public Property Content As String
        Public Property PayloadJson As String
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
        Public Property Attachments As New List(Of String)
        Public Property TimeoutSeconds As Integer?
        Public Property IdempotencyKey As String
        Public Property ReplyToMessageId As String
        Public Property ParentMessageId As String
        ' Delegation-only
        Public Property TaskName As String
        Public Property TaskPurpose As AiPurpose?
        Public Property StartTier As Integer?
        ''' <summary>Bounty (and review) settings of the delegated task.</summary>
        Public Property Delivery As Global.Agentic.Work.TaskDelivery
    End Class

    Public NotInheritable Class SendResult
        Public Property Success As Boolean
        Public Property MessageId As String
        Public Property TaskId As String
        Public Property CorrelationId As String
        Public Property ErrorCode As String
        Public Property ErrorMessage As String
        Public Property Duplicate As Boolean
        Public Property ExpiresUtc As DateTime?

        Public Shared Function Fail(code As String, message As String) As SendResult
            Return New SendResult With {.Success = False, .ErrorCode = code, .ErrorMessage = message}
        End Function
    End Class

    Public NotInheritable Class AgentDirectoryEntry
        Public Property Id As String
        Public Property Name As String
        Public Property Role As String
        Public Property Description As String
        Public Property Status As String
        Public Property AcceptsMessageTypes As List(Of String)
    End Class

    ''' <summary>
    ''' The single, host-controlled path for all agent-to-agent interaction. Every send passes:
    ''' sender permission → recipient acceptance → size → priority cap → rate limits → mailbox capacity →
    ''' outstanding requests → loop/depth/chain checks → persistence → event → wake recipient.
    ''' No method exposes another agent's runtime, memory or tasks.
    ''' </summary>
    Public Interface IAgentCommunicationService
        Function SendMessageAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult)
        Function SendRequestAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult)
        Function DelegateTaskAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult)
        Function ReplyAsync(senderId As String, originalMessageId As String, result As TaskResultContract, ct As CancellationToken, Optional idempotencyKey As String = Nothing) As Task(Of SendResult)
        Function BroadcastAsync(senderId As String, ctx As SendContext, group As String, subject As String, content As String, ct As CancellationToken) As Task(Of List(Of SendResult))
        Function GetMessagesAsync(agentId As String, query As MessageQuery) As Task(Of List(Of AgentMessage))
        Function AcknowledgeMessageAsync(agentId As String, messageId As String) As Task(Of Boolean)
        Function CancelMessageAsync(agentId As String, messageId As String, reason As String) As Task(Of Boolean)
        Function GetContacts(agentId As String) As List(Of AgentDirectoryEntry)
    End Interface

    Public NotInheritable Class AgentCommunicationService
        Implements IAgentCommunicationService

        Public Const AdminSender As String = "admin"

        Private ReadOnly _db As Database
        Private ReadOnly _messages As MessageRepository
        Private ReadOnly _tasks As TaskRepository
        Private ReadOnly _profiles As IProfileProvider
        Private ReadOnly _artifacts As ArtifactStore
        Private ReadOnly _events As IAgentEventBus
        Private ReadOnly _log As ILog
        Private ReadOnly _opt As CommunicationOptions
        Private ReadOnly _limiter As SlidingWindowRateLimiter
        Private ReadOnly _clock As IClock
        Private ReadOnly _concurrency As SemaphoreSlim
        Private _waker As IAgentWaker
        Private _acceptingNew As Boolean = True

        Public Sub New(db As Database, messages As MessageRepository, tasks As TaskRepository, profiles As IProfileProvider, artifacts As ArtifactStore,
                       events As IAgentEventBus, log As ILog, options As CommunicationOptions, limits As LimitsOptions, clock As IClock)
            _db = db
            _messages = messages
            _tasks = tasks
            _profiles = profiles
            _artifacts = artifacts
            _events = events
            _log = log
            _opt = options
            _clock = clock
            _limiter = New SlidingWindowRateLimiter(clock)
            _concurrency = New SemaphoreSlim(Math.Max(1, limits.MaxConcurrentAgentMessages))
        End Sub

        Friend Sub SetWaker(waker As IAgentWaker)
            _waker = waker
        End Sub

        ''' <summary>Shutdown: stop accepting new requests/delegations (replies are still allowed so in-flight work can finish).</summary>
        Friend Sub StopAcceptingNewWork()
            _acceptingNew = False
        End Sub

        Public ReadOnly Property Options As CommunicationOptions
            Get
                Return _opt
            End Get
        End Property

        ' ------------------------------------------------------------------------------------------
        ' Public API
        ' ------------------------------------------------------------------------------------------

        Public Function SendMessageAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult) Implements IAgentCommunicationService.SendMessageAsync
            If AgentMessage.IsRequestType(msg.MessageType) AndAlso msg.MessageType <> MessageType.StatusRequest Then
                Return SendRequestAsync(senderId, ctx, msg, ct)
            End If
            If msg.MessageType = MessageType.TaskDelegation Then Return DelegateTaskAsync(senderId, ctx, msg, ct)
            Return SendCoreAsync(senderId, ctx, msg, requiresResponse:=(msg.MessageType = MessageType.StatusRequest), delegation:=False, ct:=ct)
        End Function

        Public Function SendRequestAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult) Implements IAgentCommunicationService.SendRequestAsync
            If msg.MessageType = MessageType.Information OrElse msg.MessageType = MessageType.Notification Then msg.MessageType = MessageType.Request
            Return SendCoreAsync(senderId, ctx, msg, requiresResponse:=True, delegation:=False, ct:=ct)
        End Function

        Public Function DelegateTaskAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult) Implements IAgentCommunicationService.DelegateTaskAsync
            msg.MessageType = MessageType.TaskDelegation
            Return SendCoreAsync(senderId, ctx, msg, requiresResponse:=True, delegation:=True, ct:=ct)
        End Function

        Public Async Function ReplyAsync(senderId As String, originalMessageId As String, result As TaskResultContract, ct As CancellationToken, Optional idempotencyKey As String = Nothing) As Task(Of SendResult) Implements IAgentCommunicationService.ReplyAsync
            Dim original = Await _messages.GetAsync(originalMessageId).ConfigureAwait(False)
            If original Is Nothing Then Return SendResult.Fail("MESSAGE_NOT_FOUND", $"Message '{originalMessageId}' does not exist.")
            If Not String.Equals(original.RecipientAgentId, senderId, StringComparison.OrdinalIgnoreCase) Then
                Return SendResult.Fail("NOT_RECIPIENT", "You can only reply to messages that were sent to you.")
            End If
            If Not String.IsNullOrEmpty(original.ResponseMessageId) Then
                Return New SendResult With {.Success = True, .Duplicate = True, .MessageId = original.ResponseMessageId, .CorrelationId = original.CorrelationId}
            End If
            If original.SenderAgentId = AdminSender Then
                ' Replies to admin go nowhere but are recorded on the original message.
                Await _messages.MarkStatusAsync(original.Id, If(result.Success, MessageStatus.Completed, MessageStatus.Failed)).ConfigureAwait(False)
                Return New SendResult With {.Success = True, .MessageId = original.Id, .CorrelationId = original.CorrelationId}
            End If
            Dim replyType = If(original.MessageType = MessageType.TaskDelegation, MessageType.TaskResult, If(result.Success, MessageType.Response, MessageType.Error))
            Dim reply As New OutgoingMessage With {
                .RecipientAgentId = original.SenderAgentId, .MessageType = replyType,
                .Subject = "Re: " & TextUtil.Truncate(If(original.Subject, original.MessageType.ToString()), 180),
                .Content = TextUtil.Truncate(result.Summary, _opt.MaxMessageCharacters),
                .PayloadJson = result.ToJson(), .Priority = original.Priority, .ReplyToMessageId = original.Id, .ParentMessageId = original.Id,
                .IdempotencyKey = If(idempotencyKey, "reply:" & original.Id)}
            Dim ctx As New SendContext With {
                .TaskId = original.RelatedTaskId, .CorrelationId = original.CorrelationId, .DelegationDepth = original.DelegationDepth,
                .ChainLength = original.ChainLength, .Ancestry = original.Ancestry}
            Dim sent = Await SendCoreAsync(senderId, ctx, reply, requiresResponse:=False, delegation:=False, ct:=ct, isReply:=True).ConfigureAwait(False)
            If sent.Success Then
                Await _messages.MarkStatusAsync(original.Id, If(result.Success, MessageStatus.Completed, MessageStatus.Failed), responseId:=sent.MessageId).ConfigureAwait(False)
                _events.Emit(AgentEventType.MessageCompleted, $"{senderId} answered {original.SenderAgentId}: {TextUtil.Truncate(original.Subject, 80)}", senderId,
                             messageId:=original.Id, correlationId:=original.CorrelationId,
                             data:=New Dictionary(Of String, Object) From {{"success", result.Success}, {"durationMs", CLng((_clock.UtcNow - original.CreatedUtc).TotalMilliseconds)}})
                If original.MessageType = MessageType.TaskDelegation Then
                    _events.Emit(AgentEventType.DelegationCompleted, $"Delegated task from {original.SenderAgentId} finished ({If(result.Success, "success", "failed")})",
                                 senderId, original.RelatedTaskId, original.Id, original.CorrelationId)
                End If
            End If
            Return sent
        End Function

        Public Async Function BroadcastAsync(senderId As String, ctx As SendContext, group As String, subject As String, content As String, ct As CancellationToken) As Task(Of List(Of SendResult)) Implements IAgentCommunicationService.BroadcastAsync
            Dim results As New List(Of SendResult)
            Dim p = _profiles.TryGetProfile(senderId)
            If p Is Nothing OrElse Not p.CanBroadcast Then
                results.Add(SendResult.Fail("BROADCAST_NOT_ALLOWED", "This agent is not permitted to broadcast."))
                Return results
            End If
            Dim members As List(Of String) = Nothing
            If Not _opt.Groups.TryGetValue(If(group, ""), members) OrElse Not p.Groups.Contains(group) Then
                results.Add(SendResult.Fail("UNKNOWN_GROUP", $"Group '{group}' does not exist or this agent is not a member."))
                Return results
            End If
            For Each m In members
                If String.Equals(m, senderId, StringComparison.OrdinalIgnoreCase) Then Continue For
                Dim r = Await SendCoreAsync(senderId, ctx, New OutgoingMessage With {
                    .RecipientAgentId = m, .MessageType = MessageType.Notification, .Subject = subject, .Content = content,
                    .IdempotencyKey = If(ctx.ToolCallId Is Nothing, Nothing, $"bc:{ctx.TaskId}:{ctx.ToolCallId}:{m}")},
                    requiresResponse:=False, delegation:=False, ct:=ct, viaGroup:=True).ConfigureAwait(False)
                results.Add(r)
            Next
            Return results
        End Function

        Public Function GetMessagesAsync(agentId As String, query As MessageQuery) As Task(Of List(Of AgentMessage)) Implements IAgentCommunicationService.GetMessagesAsync
            ' Scope strictly to messages the agent sent or received.
            query.AgentId = agentId
            Return _messages.QueryAsync(query)
        End Function

        Public Async Function AcknowledgeMessageAsync(agentId As String, messageId As String) As Task(Of Boolean) Implements IAgentCommunicationService.AcknowledgeMessageAsync
            Dim m = Await _messages.GetAsync(messageId).ConfigureAwait(False)
            If m Is Nothing OrElse Not String.Equals(m.RecipientAgentId, agentId, StringComparison.OrdinalIgnoreCase) Then Return False
            If m.Status = MessageStatus.Pending Then Await _messages.MarkStatusAsync(messageId, MessageStatus.Delivered).ConfigureAwait(False)
            Return True
        End Function

        Public Async Function CancelMessageAsync(agentId As String, messageId As String, reason As String) As Task(Of Boolean) Implements IAgentCommunicationService.CancelMessageAsync
            Dim m = Await _messages.GetAsync(messageId).ConfigureAwait(False)
            If m Is Nothing Then Return False
            Dim isAdmin = agentId = AdminSender
            If Not isAdmin AndAlso Not String.Equals(m.SenderAgentId, agentId, StringComparison.OrdinalIgnoreCase) Then Return False
            If m.Status <> MessageStatus.Pending AndAlso m.Status <> MessageStatus.Delivered Then Return False
            Await _messages.MarkStatusAsync(messageId, MessageStatus.Cancelled, If(reason, "Cancelled")).ConfigureAwait(False)
            If m.MessageType = MessageType.TaskDelegation AndAlso Not String.IsNullOrEmpty(m.RelatedTaskId) Then
                Await _tasks.TryTransitionAsync(m.RelatedTaskId, {AgentTaskStatus.Pending, AgentTaskStatus.Retrying, AgentTaskStatus.Waiting, AgentTaskStatus.Blocked},
                                                AgentTaskStatus.Cancelled, "Delegation cancelled by " & agentId).ConfigureAwait(False)
            End If
            Return True
        End Function

        Public Function GetContacts(agentId As String) As List(Of AgentDirectoryEntry) Implements IAgentCommunicationService.GetContacts
            Dim result As New List(Of AgentDirectoryEntry)
            Dim p = _profiles.TryGetProfile(agentId)
            If p Is Nothing Then Return result
            Dim all = _profiles.AllProfiles()
            For Each other In all
                If String.Equals(other.Id, agentId, StringComparison.OrdinalIgnoreCase) OrElse other.IsSubagent Then Continue For
                If Not (p.AllowedRecipients.Contains(other.Id) OrElse p.AllowedRecipients.Contains("*")) Then Continue For
                result.Add(New AgentDirectoryEntry With {
                    .Id = other.Id, .Name = other.Name, .Role = other.Role, .Description = TextUtil.Truncate(other.Description, 300),
                    .Status = If(_waker Is Nothing, "unknown", _waker.DescribeStatus(other.Id)),
                    .AcceptsMessageTypes = other.AllowedReceiveMessageTypes.Select(Function(t) t.ToString()).ToList()})
            Next
            Return result
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Core pipeline
        ' ------------------------------------------------------------------------------------------

        Private Async Function SendCoreAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, requiresResponse As Boolean, delegation As Boolean,
                                             ct As CancellationToken, Optional isReply As Boolean = False, Optional viaGroup As Boolean = False) As Task(Of SendResult)
            If ctx Is Nothing Then ctx = New SendContext()
            Await _concurrency.WaitAsync(ct).ConfigureAwait(False)
            Try
                Dim validation = Await ValidateAsync(senderId, ctx, msg, requiresResponse, isReply, viaGroup).ConfigureAwait(False)
                If validation IsNot Nothing Then
                    Await RecordRejectionAsync(senderId, ctx, msg, validation).ConfigureAwait(False)
                    Return validation
                End If

                Dim now = _clock.UtcNow
                Dim correlation = If(String.IsNullOrEmpty(ctx.CorrelationId), Ids.NewId("cor"), ctx.CorrelationId)
                Dim ancestry = New List(Of String)(If(ctx.Ancestry, New List(Of String)))
                If Not isReply Then ancestry.Add(senderId)
                Dim ttlSeconds = If(msg.TimeoutSeconds.HasValue AndAlso msg.TimeoutSeconds.Value > 0,
                                    Math.Min(msg.TimeoutSeconds.Value, _opt.MaxAgentToAgentWaitSeconds),
                                    If(requiresResponse, _opt.MaxAgentToAgentWaitSeconds, _opt.DefaultMessageTtlMinutes * 60))
                ' A request (or delegated task) waits at least MinAgentToAgentWaitSeconds for its answer, even when the asking
                ' agent chose less: a 3-minute wait expired while the other agent was still working on the answer.
                If requiresResponse AndAlso senderId <> AdminSender Then
                    ttlSeconds = Math.Max(ttlSeconds, Math.Min(_opt.MinAgentToAgentWaitSeconds, _opt.MaxAgentToAgentWaitSeconds))
                End If
                Dim m As New AgentMessage With {
                    .Id = Ids.NewId("msg"), .CorrelationId = correlation, .ParentMessageId = msg.ParentMessageId, .SenderAgentId = senderId,
                    .RecipientAgentId = msg.RecipientAgentId, .MessageType = msg.MessageType, .Subject = TextUtil.Truncate(msg.Subject, 300),
                    .Content = msg.Content, .StructuredPayload = msg.PayloadJson, .Attachments = If(msg.Attachments, New List(Of String)),
                    .Priority = msg.Priority, .CreatedUtc = now, .Status = MessageStatus.Pending,
                    .ExpiresUtc = now.AddSeconds(ttlSeconds), .ReplyToMessageId = msg.ReplyToMessageId, .RequiresResponse = requiresResponse,
                    .SenderTaskId = ctx.TaskId, .DelegationDepth = If(isReply, ctx.DelegationDepth, ctx.DelegationDepth + If(AgentMessage.IsRequestType(msg.MessageType), 1, 0)),
                    .ChainLength = If(isReply, ctx.ChainLength, ctx.ChainLength + 1), .Ancestry = ancestry,
                    .IdempotencyKey = If(msg.IdempotencyKey, If(ctx.ToolCallId IsNot Nothing AndAlso ctx.TaskId IsNot Nothing, $"{ctx.TaskId}:{ctx.ToolCallId}:{msg.RecipientAgentId}", Nothing))}

                Dim delegatedTask As AgentTask = Nothing
                If delegation Then
                    Dim payload As New Dictionary(Of String, Object) From {
                        {"from", senderId}, {"subject", m.Subject}, {"instructions", m.Content}, {"messageId", m.Id}}
                    If Not String.IsNullOrEmpty(m.StructuredPayload) Then payload("payload") = JsonUtil.ParseElementOrNull(m.StructuredPayload)
                    If m.Attachments.Count > 0 Then payload("attachments") = m.Attachments
                    delegatedTask = New AgentTask With {
                        .Id = Ids.NewId("tsk"), .AgentId = msg.RecipientAgentId, .Name = TextUtil.Truncate(If(msg.TaskName, If(m.Subject, "Delegated task")), 200),
                        .Description = m.Content, .Kind = TaskKind.Delegated, .Handler = "reasoning", .Purpose = msg.TaskPurpose, .Status = AgentTaskStatus.Pending,
                        .Priority = m.Priority, .Payload = JsonUtil.ToJson(payload), .MaxRetries = 2, .CreatedUtc = now, .UpdatedUtc = now,
                        .ParentTaskId = ctx.TaskId, .ParentAgentId = senderId, .DelegatingAgentId = senderId, .CorrelationId = correlation,
                        .DelegationDepth = m.DelegationDepth, .ChainLength = m.ChainLength, .Ancestry = New List(Of String)(ancestry),
                        .SourceMessageId = m.Id, .StartTier = msg.StartTier, .CreatedBy = senderId, .Delivery = msg.Delivery}
                    m.RelatedTaskId = delegatedTask.Id
                End If

                ' Persist message (+ delegated task) atomically. Idempotent on (sender, idempotency key).
                Dim duplicate As AgentMessage = Nothing
                Await _db.InTransactionAsync(Of Boolean)(
                    Async Function(conn, tx)
                        If Not String.IsNullOrEmpty(m.IdempotencyKey) Then
                            Dim existing = Await MessageRepository.TxGetByIdempotencyAsync(conn, tx, senderId, m.IdempotencyKey).ConfigureAwait(False)
                            If existing.Count > 0 Then
                                duplicate = existing(0)
                                Return False
                            End If
                        End If
                        Await MessageRepository.TxInsertAsync(conn, tx, m).ConfigureAwait(False)
                        If delegatedTask IsNot Nothing Then Await TaskRepository.TxInsertAsync(conn, tx, delegatedTask).ConfigureAwait(False)
                        Return True
                    End Function).ConfigureAwait(False)

                If duplicate IsNot Nothing Then
                    Return New SendResult With {.Success = True, .Duplicate = True, .MessageId = duplicate.Id, .TaskId = duplicate.RelatedTaskId,
                                                .CorrelationId = duplicate.CorrelationId, .ExpiresUtc = duplicate.ExpiresUtc}
                End If

                ' Grant attachment access to the recipient (sender ownership was validated).
                For Each art In m.Attachments
                    Try
                        Await _artifacts.GrantAsync(senderId, art, m.RecipientAgentId, m.Id).ConfigureAwait(False)
                    Catch ex As ArtifactAccessException
                        _log.Warn("Comms", $"Attachment grant failed for {art}: {ex.Message}", senderId)
                    End Try
                Next

                If delegatedTask IsNot Nothing Then
                    Await _tasks.AddHistoryAsync(delegatedTask.Id, delegatedTask.AgentId, Nothing, AgentTaskStatus.Pending, $"Delegated by {senderId}").ConfigureAwait(False)
                    _events.Emit(AgentEventType.DelegationCreated, $"{senderId} delegated '{delegatedTask.Name}' to {m.RecipientAgentId}", senderId, ctx.TaskId, m.Id, correlation,
                                 data:=New Dictionary(Of String, Object) From {{"recipient", m.RecipientAgentId}, {"childTaskId", delegatedTask.Id}, {"depth", m.DelegationDepth}})
                End If
                _events.Emit(AgentEventType.MessageSent, $"{senderId} → {m.RecipientAgentId} [{m.MessageType}] {TextUtil.Truncate(m.Subject, 80)}", senderId, ctx.TaskId, m.Id, correlation,
                             data:=New Dictionary(Of String, Object) From {{"recipient", m.RecipientAgentId}, {"type", m.MessageType.ToString()}, {"priority", m.Priority.ToString()}})
                _waker?.Wake(m.RecipientAgentId)
                Return New SendResult With {.Success = True, .MessageId = m.Id, .TaskId = delegatedTask?.Id, .CorrelationId = correlation, .ExpiresUtc = m.ExpiresUtc}
            Finally
                _concurrency.Release()
            End Try
        End Function

        ''' <summary>
        ''' The sender's artifact a mistyped reference means, or Nothing when it is not clear: the same id with characters
        ''' dropped or changed (models garble long ids), or the artifact's exact name. Only one candidate may fit.
        ''' </summary>
        Friend Shared Function ResolveOwnArtifact(reference As String, mine As IEnumerable(Of ArtifactInfo)) As ArtifactInfo
            Dim r = If(reference, "").Trim()
            If r.Length = 0 OrElse mine Is Nothing Then Return Nothing
            Dim list = mine.ToList()
            Dim exact = list.Where(Function(x) String.Equals(x.Id, r, StringComparison.OrdinalIgnoreCase)).ToList()
            If exact.Count = 1 Then Return exact(0)
            Dim byName = list.Where(Function(x) String.Equals(x.Name, r, StringComparison.OrdinalIgnoreCase)).ToList()
            If byName.Count = 1 Then Return byName(0)
            If Not r.StartsWith("art_", StringComparison.OrdinalIgnoreCase) OrElse r.Length < 12 Then Return Nothing
            ' Close: few edits apart (at most a quarter of the id), and clearly closer than any other artifact.
            Dim scored = list.Where(Function(x) x.Id IsNot Nothing).Select(Function(x) (Info:=x, Dist:=EditDistance(r.ToLowerInvariant(), x.Id.ToLowerInvariant()))).
                OrderBy(Function(x) x.Dist).ToList()
            If scored.Count = 0 Then Return Nothing
            Dim best = scored(0)
            If best.Dist > Math.Max(2, best.Info.Id.Length \ 4) Then Return Nothing
            If scored.Count > 1 AndAlso scored(1).Dist <= best.Dist + 2 Then Return Nothing
            Return best.Info
        End Function

        Private Shared Function EditDistance(a As String, b As String) As Integer
            Dim prev(b.Length) As Integer
            Dim cur(b.Length) As Integer
            For j = 0 To b.Length
                prev(j) = j
            Next
            For i = 1 To a.Length
                cur(0) = i
                For j = 1 To b.Length
                    Dim cost = If(a(i - 1) = b(j - 1), 0, 1)
                    cur(j) = Math.Min(Math.Min(cur(j - 1) + 1, prev(j) + 1), prev(j - 1) + cost)
                Next
                Dim t = prev : prev = cur : cur = t
            Next
            Return prev(b.Length)
        End Function

        ''' <summary>Returns Nothing when the message is allowed, otherwise a failed SendResult describing why.</summary>
        Private Async Function ValidateAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, requiresResponse As Boolean, isReply As Boolean, viaGroup As Boolean) As Task(Of SendResult)
            If Not _opt.Enabled Then Return SendResult.Fail("COMMUNICATION_DISABLED", "Inter-agent communication is disabled.")
            If String.IsNullOrWhiteSpace(msg.RecipientAgentId) Then Return SendResult.Fail("INVALID_RECIPIENT", "Recipient is required.")
            Dim isAdmin = senderId = AdminSender
            If Not isReply AndAlso Not _acceptingNew AndAlso AgentMessage.IsRequestType(msg.MessageType) Then
                Return SendResult.Fail("HOST_SHUTTING_DOWN", "The host is shutting down and does not accept new requests or delegations.")
            End If

            Dim sender As EffectiveProfile = Nothing
            If Not isAdmin Then
                sender = _profiles.TryGetProfile(senderId)
                If sender Is Nothing Then Return SendResult.Fail("UNKNOWN_SENDER", "Sender is not a known agent.")
            End If
            Dim recipient = _profiles.TryGetProfile(msg.RecipientAgentId)
            If recipient Is Nothing Then Return SendResult.Fail("UNKNOWN_RECIPIENT", $"Agent '{msg.RecipientAgentId}' does not exist.")
            If Not recipient.Enabled Then Return SendResult.Fail("RECIPIENT_DISABLED", $"Agent '{msg.RecipientAgentId}' is disabled.")
            ' Subagents only work on the task their parent gave them; nobody messages them directly.
            If recipient.IsSubagent AndAlso Not isReply Then Return SendResult.Fail("RECIPIENT_NOT_ALLOWED", $"'{recipient.Id}' is a subagent and cannot receive messages.")
            If sender IsNot Nothing AndAlso sender.IsSubagent AndAlso Not isReply Then Return SendResult.Fail("SENDER_NOT_ALLOWED", "Subagents cannot send messages.")

            If Not isAdmin Then
                ' 1. Sender permission (replies to a received message are always allowed back to that sender).
                If Not isReply AndAlso Not viaGroup AndAlso Not (sender.AllowedRecipients.Contains(recipient.Id) OrElse sender.AllowedRecipients.Contains("*")) Then
                    Return SendResult.Fail("RECIPIENT_NOT_ALLOWED", $"You are not permitted to communicate with '{recipient.Id}'.")
                End If
                If Not isReply AndAlso Not sender.AllowedSendMessageTypes.Contains(msg.MessageType) Then
                    Return SendResult.Fail("MESSAGE_TYPE_NOT_ALLOWED", $"You are not permitted to send {msg.MessageType} messages.")
                End If
                ' 2. Priority cap (agents cannot arbitrarily declare everything Critical).
                If msg.Priority > sender.MaxSendPriority Then
                    Return SendResult.Fail("PRIORITY_NOT_ALLOWED", $"Maximum priority you may use is {sender.MaxSendPriority}.")
                End If
            End If
            ' 3. Recipient accepts this type.
            If Not isReply AndAlso Not recipient.AllowedReceiveMessageTypes.Contains(msg.MessageType) Then
                Return SendResult.Fail("RECIPIENT_REJECTS_TYPE", $"'{recipient.Id}' does not accept {msg.MessageType} messages.")
            End If
            ' 4. Size limits.
            If (If(msg.Content, "")).Length > _opt.MaxMessageCharacters Then
                Return SendResult.Fail("MESSAGE_TOO_LARGE", $"Message content exceeds {_opt.MaxMessageCharacters} characters. Store large data as an artifact and send its id instead.")
            End If
            If TextUtil.Utf8Length(msg.PayloadJson) > _opt.MaxStructuredPayloadBytes Then
                Return SendResult.Fail("PAYLOAD_TOO_LARGE", $"Structured payload exceeds {_opt.MaxStructuredPayloadBytes} bytes. Use an artifact reference.")
            End If
            If Not String.IsNullOrEmpty(msg.PayloadJson) Then
                Dim el As JsonElement
                If Not JsonUtil.TryParseElement(msg.PayloadJson, el) Then Return SendResult.Fail("INVALID_PAYLOAD", "Structured payload must be valid JSON.")
            End If
            If msg.Attachments IsNot Nothing AndAlso msg.Attachments.Count > 0 Then
                If msg.Attachments.Count > _opt.MaxAttachments Then Return SendResult.Fail("TOO_MANY_ATTACHMENTS", $"At most {_opt.MaxAttachments} attachments are allowed.")
                Dim mine As List(Of ArtifactInfo) = Nothing
                For i = 0 To msg.Attachments.Count - 1
                    Dim a = msg.Attachments(i)
                    Dim info = Await _artifacts.GetInfoAsync(a).ConfigureAwait(False)
                    If info Is Nothing OrElse Not String.Equals(info.OwnerAgentId, senderId, StringComparison.OrdinalIgnoreCase) Then
                        ' Models often garble long ids (dropped characters) or send the file name: find the sender's own artifact.
                        If mine Is Nothing Then mine = Await _artifacts.ListAllAsync(200, senderId).ConfigureAwait(False)
                        Dim match = ResolveOwnArtifact(a, mine)
                        If match Is Nothing Then
                            Dim recent = String.Join(", ", mine.Take(5).Select(Function(x) $"{x.Id} ({x.Name})"))
                            Return SendResult.Fail("ATTACHMENT_NOT_OWNED", $"Artifact '{a}' does not exist or is not owned by you; only owners can share artifacts." &
                                                   If(recent.Length > 0, $" Your most recent artifacts: {recent}. Copy the id exactly.", " You have no artifacts."))
                        End If
                        msg.Attachments(i) = match.Id
                    End If
                Next
            End If
            If isReply Then Return Nothing

            ' 5. Rate limits.
            Dim limitMsgs = If(sender Is Nothing, 600, sender.MaxMessagesPerMinute)
            If Not _limiter.TryAcquire(senderId & ":msg", limitMsgs, TimeSpan.FromMinutes(1)) Then
                Return SendResult.Fail("RATE_LIMITED", $"Message rate limit reached ({limitMsgs}/minute). Try again later.")
            End If
            If requiresResponse AndAlso msg.MessageType <> MessageType.TaskDelegation Then
                Dim limitReq = If(sender Is Nothing, 300, sender.MaxRequestsPerMinute)
                If Not _limiter.TryAcquire(senderId & ":req", limitReq, TimeSpan.FromMinutes(1)) Then
                    Return SendResult.Fail("RATE_LIMITED", $"Request rate limit reached ({limitReq}/minute).")
                End If
            End If
            If msg.MessageType = MessageType.TaskDelegation Then
                Dim limitDel = If(sender Is Nothing, 1000, sender.MaxDelegationsPerHour)
                If Not _limiter.TryAcquire(senderId & ":del", limitDel, TimeSpan.FromHours(1)) Then
                    Return SendResult.Fail("RATE_LIMITED", $"Delegation rate limit reached ({limitDel}/hour).")
                End If
            End If
            ' 6. Mailbox capacity.
            Dim inbox = Await _messages.CountInboxAsync(recipient.Id).ConfigureAwait(False)
            If inbox >= _opt.MaxMailboxSize Then Return SendResult.Fail("MAILBOX_FULL", $"'{recipient.Id}' has a full mailbox ({inbox} pending). Try again later.")
            ' 7. Outstanding requests.
            If requiresResponse AndAlso Not isAdmin Then
                Dim outstanding = Await _messages.CountOutstandingRequestsAsync(senderId).ConfigureAwait(False)
                If outstanding >= sender.MaxOutstandingRequests Then
                    Return SendResult.Fail("TOO_MANY_OUTSTANDING_REQUESTS", $"You already have {outstanding} unanswered requests (limit {sender.MaxOutstandingRequests}).")
                End If
            End If
            ' 8. Loop / depth / chain protection.
            If Not isAdmin Then
                Dim lc = LoopDetector.Check(senderId, recipient.Id, msg.MessageType, ctx, _opt.MaxDelegationDepth, _opt.MaxCommunicationChainLength, _opt.AllowCycleRevisit)
                If Not lc.Allowed Then Return SendResult.Fail(lc.Code, lc.Reason)
                If Not String.IsNullOrEmpty(ctx.CorrelationId) Then
                    Dim perCorr = Await _messages.CountByCorrelationAsync(ctx.CorrelationId).ConfigureAwait(False)
                    If perCorr >= _opt.MaxMessagesPerCorrelation Then
                        Return SendResult.Fail("CORRELATION_LIMIT", $"This workflow already exchanged {perCorr} messages (limit {_opt.MaxMessagesPerCorrelation}).")
                    End If
                End If
                If msg.MessageType = MessageType.TaskDelegation AndAlso Not String.IsNullOrEmpty(ctx.TaskId) Then
                    Dim children = Await _tasks.CountChildrenAsync(ctx.TaskId).ConfigureAwait(False)
                    If children >= _opt.MaxChildTasksPerTask Then
                        Return SendResult.Fail("TOO_MANY_CHILD_TASKS", $"This task already delegated {children} child tasks (limit {_opt.MaxChildTasksPerTask}).")
                    End If
                End If
            End If
            Return Nothing
        End Function

        Private Async Function RecordRejectionAsync(senderId As String, ctx As SendContext, msg As OutgoingMessage, result As SendResult) As Task
            Try
                Dim now = _clock.UtcNow
                Dim m As New AgentMessage With {
                    .Id = Ids.NewId("msg"), .CorrelationId = If(ctx.CorrelationId, Ids.NewId("cor")), .ParentMessageId = msg.ParentMessageId,
                    .SenderAgentId = senderId, .RecipientAgentId = If(msg.RecipientAgentId, "?"), .MessageType = msg.MessageType,
                    .Subject = TextUtil.Truncate(msg.Subject, 300), .Content = TextUtil.Truncate(msg.Content, 300), .Priority = msg.Priority,
                    .CreatedUtc = now, .CompletedUtc = now, .Status = MessageStatus.Rejected, .SenderTaskId = ctx.TaskId,
                    .DelegationDepth = ctx.DelegationDepth, .ChainLength = ctx.ChainLength, .Ancestry = If(ctx.Ancestry, New List(Of String)),
                    .RejectionReason = $"{result.ErrorCode}: {result.ErrorMessage}"}
                Await _messages.InsertAsync(m).ConfigureAwait(False)
                result.MessageId = m.Id
                Dim lvl = If(result.ErrorCode = "LOOP_DETECTED" OrElse result.ErrorCode = "DELEGATION_DEPTH_EXCEEDED" OrElse result.ErrorCode = "CHAIN_TOO_LONG", EventLevel.Warning, EventLevel.Info)
                _events.Emit(AgentEventType.MessageRejected, $"{senderId} → {msg.RecipientAgentId} rejected: {result.ErrorCode}", senderId, ctx.TaskId, m.Id, ctx.CorrelationId, lvl,
                             New Dictionary(Of String, Object) From {{"code", result.ErrorCode}, {"reason", result.ErrorMessage}})
            Catch ex As Exception
                _log.Warn("Comms", "Failed to record rejected message", senderId, ex:=ex)
            End Try
        End Function

        ' ------------------------------------------------------------------------------------------
        ' Host-internal helpers (used by mailbox processing and inline waits)
        ' ------------------------------------------------------------------------------------------

        ''' <summary>Looks for a reply to a given message addressed to the agent (used by inline request waits).</summary>
        Friend Async Function FindReplyAsync(agentId As String, originalMessageId As String) As Task(Of AgentMessage)
            Dim list = Await _db.QueryAsync("SELECT * FROM AgentMessages WHERE RecipientAgentId=@a AND ReplyToMessageId=@m AND Status IN ('Pending','Delivered') LIMIT 1",
                                            AddressOf MessageRepository.Map, ("@a", CObj(agentId)), ("@m", originalMessageId)).ConfigureAwait(False)
            Return list.FirstOrDefault()
        End Function

        Friend ReadOnly Property Messages As MessageRepository
            Get
                Return _messages
            End Get
        End Property
    End Class

    ''' <summary>
    ''' Agent-bound communication client handed to an agent's tools. The sender id is fixed at construction,
    ''' so an agent can never impersonate another agent.
    ''' </summary>
    Public NotInheritable Class AgentCommunicationClient
        Private ReadOnly _service As AgentCommunicationService
        Private ReadOnly _agentId As String

        Friend Sub New(service As AgentCommunicationService, agentId As String)
            _service = service
            _agentId = agentId
        End Sub

        Public Function SendMessageAsync(ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult)
            Return _service.SendMessageAsync(_agentId, ctx, msg, ct)
        End Function

        Public Function SendRequestAsync(ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult)
            Return _service.SendRequestAsync(_agentId, ctx, msg, ct)
        End Function

        Public Function DelegateTaskAsync(ctx As SendContext, msg As OutgoingMessage, ct As CancellationToken) As Task(Of SendResult)
            Return _service.DelegateTaskAsync(_agentId, ctx, msg, ct)
        End Function

        Public Function ReplyAsync(originalMessageId As String, result As TaskResultContract, ct As CancellationToken) As Task(Of SendResult)
            Return _service.ReplyAsync(_agentId, originalMessageId, result, ct)
        End Function

        Public Function BroadcastAsync(ctx As SendContext, group As String, subject As String, content As String, ct As CancellationToken) As Task(Of List(Of SendResult))
            Return _service.BroadcastAsync(_agentId, ctx, group, subject, content, ct)
        End Function

        Public Function GetContacts() As List(Of AgentDirectoryEntry)
            Return _service.GetContacts(_agentId)
        End Function

        Friend Function FindReplyAsync(originalMessageId As String) As Task(Of AgentMessage)
            Return _service.FindReplyAsync(_agentId, originalMessageId)
        End Function

        Friend Function ConsumeReplyAsync(replyId As String) As Task(Of Integer)
            Return _service.Messages.MarkStatusAsync(replyId, MessageStatus.Completed)
        End Function

        Public ReadOnly Property InlineWaitSeconds As Integer
            Get
                Return _service.Options.InlineWaitSeconds
            End Get
        End Property
    End Class

End Namespace
