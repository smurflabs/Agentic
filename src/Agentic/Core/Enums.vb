Option Strict On
Option Explicit On
Option Infer On

' Shared domain enums live in the root Agentic namespace so every component can use them.

Public Enum PriorityLevel
    Low = 0
    Normal = 1
    High = 2
    Critical = 3
End Enum

Public Enum AgentState
    Stopped
    Starting
    Idle
    Running
    Waiting
    Paused
    [Error]
    Unhealthy
    Stuck
    Stopping
    Disabled
    ''' <summary>Ran out of money: shut down until an administrator funds it again.</summary>
    Dead
End Enum

Public Enum AgentTaskStatus
    Pending
    Running
    Waiting
    Completed
    Failed
    Retrying
    Cancelled
    Blocked
End Enum

Public Enum TaskKind
    OneTime
    Recurring
    Scheduled
    Polling
    LongRunning
    Delegated
    MessageTriggered
    ''' <summary>Created by an outside event: a webhook call (Hooks) or a file-system change (FileWatches).</summary>
    EventTriggered
End Enum

Public Enum MessageType
    Information
    Question
    Request
    TaskDelegation
    TaskResult
    Notification
    ApprovalRequest
    StatusRequest
    Response
    [Error]
End Enum

Public Enum MessageStatus
    Pending
    Delivered
    Processing
    Completed
    Failed
    Rejected
    Expired
    Cancelled
End Enum

Public Enum MemoryCategory
    Fact
    Observation
    Lesson
    TaskResult
    Preference
    Decision
    WorkNote
    ExternalReference
End Enum

Public Enum AiPurpose
    GeneralReasoning
    Planning
    Classification
    Extraction
    Summarization
    ToolSelection
    CodeGeneration
    AdvancedCoding
    ReverseEngineering
    ComplexAnalysis
    ComplexLanguage
    Research
    Vision
    ImageGeneration
End Enum

Public Enum ModelCapability
    ToolCalling
    Vision
    Reasoning
    ImageGeneration
    JsonMode
    Streaming
End Enum

Public Enum McpTransportKind
    Stdio
    Http
End Enum

Public Enum ToolSource
    Internal
    Mcp
    Communication
    AiCapability
End Enum

Public Enum LedgerEntryType
    InitialGrant
    ProfitCredit
    AdminAdjustment
    AiCharge
    ImageCharge
    Refund
    ''' <summary>Money moved between two agents' wallets (a hire's starting budget, a retired hire's remainder).</summary>
    Transfer
    ''' <summary>A bounty paid for accepted work (negative on the payer, positive on the worker).</summary>
    Bounty
End Enum

Public Enum ApprovalKind
    AskHuman
    ToolApproval
    EscalationApproval
    ''' <summary>An agent asked for more wallet budget; approving credits its wallet.</summary>
    BudgetRequest
    ''' <summary>An agent proposed a change to its own behaviour; approving appends it to the agent's prompt.</summary>
    ProfileChange
    ''' <summary>An agent wants to hire a helper agent; approving creates it.</summary>
    HireAgent
    ''' <summary>A user sent feedback (/feedback in a chat): a report for the administrators, with the chat and app logs attached.</summary>
    Feedback
End Enum

Public Enum ApprovalStatus
    Pending
    Approved
    Rejected
    Answered
    Expired
    Cancelled
End Enum

Public Enum AiCallReason
    Primary
    Fallback
    Spillover
    Escalation
    Compaction
    Vision
    ImageGeneration
    Probe
    ''' <summary>An administrator-granted additional model chosen explicitly (consult_model / switch_model / pinned task).</summary>
    CustomModel
End Enum

Public Enum SubagentStatus
    Running
    Completed
    Failed
    Cancelled
End Enum
