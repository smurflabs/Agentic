Option Strict On
Option Explicit On
Option Infer On

Imports System.Text.Json
Imports System.Text.Json.Serialization
Imports Agentic.Utilities

Namespace Work

    ''' <summary>A unit of work in one agent's queue. Delegated tasks carry full lineage for traceability.</summary>
    Public Class AgentTask
        Public Property Id As String
        Public Property AgentId As String
        Public Property Name As String
        Public Property Description As String
        Public Property Kind As TaskKind = TaskKind.OneTime
        Public Property Handler As String = "reasoning"
        Public Property Purpose As AiPurpose?
        Public Property Status As AgentTaskStatus = AgentTaskStatus.Pending
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
        ''' <summary>JSON payload (input).</summary>
        Public Property Payload As String
        ''' <summary>JSON <see cref="TaskResultContract"/>.</summary>
        Public Property Result As String
        <JsonPropertyName("error")>
        Public Property ErrorMessage As String
        Public Property RetryCount As Integer
        Public Property MaxRetries As Integer = 3
        Public Property NextRunUtc As DateTime?
        Public Property CreatedUtc As DateTime
        Public Property StartedUtc As DateTime?
        Public Property CompletedUtc As DateTime?
        Public Property UpdatedUtc As DateTime

        ' Lineage
        Public Property ParentTaskId As String
        Public Property ParentAgentId As String
        Public Property DelegatingAgentId As String
        Public Property CorrelationId As String
        Public Property DelegationDepth As Integer
        Public Property ChainLength As Integer
        Public Property Ancestry As New List(Of String)
        Public Property SourceMessageId As String
        Public Property ScheduleId As String

        ' Model routing
        Public Property StartTier As Integer?
        Public Property CurrentLadderLevel As Integer
        Public Property EscalationCount As Integer

        ' Resumption
        <JsonIgnore>
        Public Property Checkpoint As String
        Public Property WaitingOn As New List(Of String)
        Public Property WaitDeadlineUtc As DateTime?
        Public Property LeaseOwner As String
        Public Property LeaseExpiresUtc As DateTime?

        Public Property CostMicros As Long
        Public Property CreatedBy As String
        Public Property IdempotencyKey As String
        ''' <summary>Explicit queue position (lower runs first). Assigned on insert by priority; administrators may reorder.</summary>
        Public Property QueueRank As Double?
        ''' <summary>Model pinned by an administrator (must be permitted for the agent).</summary>
        Public Property ModelKey As String

        ' Scheduling (DAG dependencies, SLA deadline, off-peak preference, value weight)
        ''' <summary>Task ids that must finish (Completed) before this one may run; it stays Blocked until then.</summary>
        Public Property DependsOn As New List(Of String)
        ''' <summary>Soft deadline: as it nears, the task jumps ahead of the queue (SLA-aware priority).</summary>
        Public Property DeadlineUtc As DateTime?
        ''' <summary>Prefer to run only inside the configured off-peak window (unless a deadline forces it sooner).</summary>
        Public Property OffPeak As Boolean
        ''' <summary>How valuable this task is (0-100); higher justifies spending on a stronger model.</summary>
        Public Property Weight As Integer

        ' Run options set by the person who created the task
        ''' <summary>Tool calls that need approval are approved at once for this task and the work it hands on (same CorrelationId).</summary>
        Public Property AutoApproveTools As Boolean
        ''' <summary>DeadlineUtc is a real deadline: when it passes the task is cancelled, unless RunPastDeadline.</summary>
        Public Property DeadlineEnforced As Boolean
        ''' <summary>Past its deadline the task keeps working (it is marked as late instead of cancelled).</summary>
        Public Property RunPastDeadline As Boolean
        ''' <summary>When the deadline passed and was handled (cancelled or marked late).</summary>
        Public Property DeadlinePassedUtc As DateTime?

        ''' <summary>Review, delivery and attachments (Nothing for ordinary tasks).</summary>
        Public Property Delivery As TaskDelivery
        ''' <summary>Expected outcome for the reviewer only. Never part of the worker's prompt or of task JSON.</summary>
        <JsonIgnore>
        Public Property ReviewCriteria As String

        Public ReadOnly Property HasCheckpoint As Boolean
            Get
                Return Not String.IsNullOrEmpty(Checkpoint)
            End Get
        End Property

        Public ReadOnly Property IsTerminal As Boolean
            Get
                Return Status = AgentTaskStatus.Completed OrElse Status = AgentTaskStatus.Failed OrElse Status = AgentTaskStatus.Cancelled
            End Get
        End Property

        Public Function GetResult() As TaskResultContract
            Return JsonUtil.FromJson(Of TaskResultContract)(Result)
        End Function
    End Class

    ''' <summary>Structured result returned by tasks and agent request/response interactions.</summary>
    Public Class TaskResultContract
        Public Property Success As Boolean
        Public Property Summary As String
        Public Property Data As JsonElement?
        Public Property Warnings As New List(Of String)

        Public Shared Function Ok(summary As String, Optional data As JsonElement? = Nothing) As TaskResultContract
            Return New TaskResultContract With {.Success = True, .Summary = summary, .Data = data}
        End Function

        Public Shared Function Fail(summary As String) As TaskResultContract
            Return New TaskResultContract With {.Success = False, .Summary = summary}
        End Function

        Public Function ToJson() As String
            Return JsonUtil.ToJson(Me)
        End Function
    End Class

    ''' <summary>Request to create a task (from admin API, scheduler, delegation or the agent itself).</summary>
    Public Class NewTaskRequest
        Public Property Name As String
        Public Property Description As String
        Public Property Kind As TaskKind = TaskKind.OneTime
        Public Property Handler As String = "reasoning"
        Public Property Purpose As AiPurpose?
        Public Property Priority As PriorityLevel = PriorityLevel.Normal
        Public Property Payload As String
        Public Property MaxRetries As Integer?
        Public Property NextRunUtc As DateTime?
        Public Property StartTier As Integer?
        Public Property ScheduleId As String
        Public Property CreatedBy As String
        Public Property IdempotencyKey As String
        ''' <summary>Pin the task to one permitted model (admin).</summary>
        Public Property ModelKey As String
        ''' <summary>Queue at the front instead of by priority (admin).</summary>
        Public Property AddToFront As Boolean
        ''' <summary>Join an existing workflow (e.g. the steps of a global task); Nothing starts a new one.</summary>
        Public Property CorrelationId As String
        Public Property Delivery As TaskDelivery
        Public Property ReviewCriteria As String
        ''' <summary>Task ids that must finish before this one runs (creates a dependency DAG).</summary>
        Public Property DependsOn As List(Of String)
        Public Property DeadlineUtc As DateTime?
        Public Property OffPeak As Boolean
        Public Property Weight As Integer
        ''' <summary>Approve the task's tool calls automatically (see AgentTask.AutoApproveTools).</summary>
        Public Property AutoApproveTools As Boolean
        ''' <summary>DeadlineUtc is enforced: cancelled when it passes, unless RunPastDeadline.</summary>
        Public Property DeadlineEnforced As Boolean
        Public Property RunPastDeadline As Boolean
    End Class

    ''' <summary>A file an administrator attached to a task (uploaded once, then copied to the agent).</summary>
    Public Class TaskAttachment
        Public Property UploadId As String
        Public Property Name As String
        Public Property MimeType As String
        Public Property SizeBytes As Long
        ''' <summary>text | code | document | spreadsheet | presentation | pdf | archive | image | binary</summary>
        Public Property Kind As String
        Public Property Summary As String
        ''' <summary>Artifact the agent can read (read_artifact / analyze_image); Nothing when the file was too large.</summary>
        Public Property ArtifactId As String
        ''' <summary>Path in the shared storage (relative), for agents with the storage MCP server.</summary>
        Public Property SharedPath As String
    End Class

    ''' <summary>
    ''' What happens when a task finishes: a reviewer agent checks the result (against criteria only it sees) and can send
    ''' it back for revision; the approved result can be delivered to the administrator's Results page. Also carries the
    ''' task's attachments and its place in a revision chain or review.
    ''' </summary>
    Public Class TaskDelivery
        ''' <summary>Show the final result in Results.</summary>
        Public Property Deliver As Boolean
        ''' <summary>Agent that checks the work before it counts as done (Nothing = no review).</summary>
        Public Property ReviewerAgentId As String
        ''' <summary>How often the reviewer may send the work back.</summary>
        Public Property MaxRevisions As Integer = 1
        Public Property Attachments As New List(Of TaskAttachment)
        ''' <summary>First task of a revision chain (Nothing on the first attempt).</summary>
        Public Property RootTaskId As String
        Public Property Revision As Integer
        ''' <summary>Set on review tasks: the task (or global task) being reviewed.</summary>
        Public Property ReviewOfTaskId As String
        Public Property ReviewOfGlobalTaskId As String
        ''' <summary>The chat the work was handed over from: its result is posted back there (ChatUser owns the chat).</summary>
        Public Property ChatId As String
        Public Property ChatUser As String
        ''' <summary>Reward paid when the work is accepted (approved by its reviewer, or finished without a review).</summary>
        Public Property BountyMicros As Long
        ''' <summary>Agent whose wallet pays the bounty; Nothing = a person offered it (credited as profit).</summary>
        Public Property BountyFrom As String

        <JsonIgnore>
        Public ReadOnly Property IsReview As Boolean
            Get
                Return Not String.IsNullOrEmpty(ReviewOfTaskId) OrElse Not String.IsNullOrEmpty(ReviewOfGlobalTaskId)
            End Get
        End Property

        <JsonIgnore>
        Public ReadOnly Property HasBounty As Boolean
            Get
                Return BountyMicros > 0
            End Get
        End Property

        ''' <summary>A copy for the next attempt or a schedule run (same delivery settings and files, new chain position).</summary>
        Public Function CloneSettings() As TaskDelivery
            Return New TaskDelivery With {.Deliver = Deliver, .ReviewerAgentId = ReviewerAgentId, .MaxRevisions = MaxRevisions,
                                          .Attachments = New List(Of TaskAttachment)(If(Attachments, New List(Of TaskAttachment))),
                                          .ChatId = ChatId, .ChatUser = ChatUser, .BountyMicros = BountyMicros, .BountyFrom = BountyFrom}
        End Function
    End Class

    ''' <summary>Admin edit of a queued task (null = unchanged).</summary>
    Public Class TaskEdit
        Public Property Name As String
        Public Property Description As String
        Public Property Priority As PriorityLevel?
        Public Property Purpose As AiPurpose?
        Public Property ClearPurpose As Boolean
        Public Property NextRunUtc As DateTime?
        Public Property RunNow As Boolean
        Public Property ModelKey As String
        Public Property ClearModel As Boolean
        Public Property MaxRetries As Integer?
    End Class

End Namespace
