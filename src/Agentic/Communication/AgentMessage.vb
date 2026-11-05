Option Strict On
Option Explicit On
Option Infer On

Namespace Communication

    ''' <summary>
    ''' Persistent inter-agent message. The only way information moves between agents.
    ''' Content is plain text; StructuredPayload is optional JSON; Attachments are artifact IDs (access is granted explicitly).
    ''' </summary>
    Public Class AgentMessage
        Public Property Id As String
        Public Property CorrelationId As String
        Public Property ParentMessageId As String
        Public Property SenderAgentId As String
        Public Property RecipientAgentId As String
        Public Property MessageType As MessageType
        Public Property Subject As String
        Public Property Content As String
        Public Property StructuredPayload As String
        Public Property Attachments As New List(Of String)
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
        Public Property CreatedUtc As DateTime
        Public Property DeliveredUtc As DateTime?
        Public Property StartedUtc As DateTime?
        Public Property CompletedUtc As DateTime?
        Public Property Status As MessageStatus = MessageStatus.Pending
        Public Property ExpiresUtc As DateTime?
        Public Property ReplyToMessageId As String
        Public Property RequiresResponse As Boolean
        Public Property ResponseMessageId As String
        ''' <summary>The sender's task that produced this message (for resuming the sender when the reply arrives).</summary>
        Public Property SenderTaskId As String
        ''' <summary>For TaskDelegation: the task created in the recipient's queue.</summary>
        Public Property RelatedTaskId As String
        Public Property DelegationDepth As Integer
        Public Property ChainLength As Integer
        Public Property Ancestry As New List(Of String)
        Public Property IdempotencyKey As String
        Public Property RejectionReason As String
        Public Property LeaseExpiresUtc As DateTime?
        Public Property ContentPurgedUtc As DateTime?

        Public ReadOnly Property DurationMs As Long?
            Get
                If Not CompletedUtc.HasValue Then Return Nothing
                Return CLng((CompletedUtc.Value - CreatedUtc).TotalMilliseconds)
            End Get
        End Property

        Public Shared Function IsRequestType(t As MessageType) As Boolean
            Return t = MessageType.Request OrElse t = MessageType.Question OrElse t = MessageType.TaskDelegation OrElse
                   t = MessageType.ApprovalRequest OrElse t = MessageType.StatusRequest
        End Function

        Public Shared Function IsReplyType(t As MessageType) As Boolean
            Return t = MessageType.Response OrElse t = MessageType.TaskResult OrElse t = MessageType.Error
        End Function
    End Class

End Namespace
