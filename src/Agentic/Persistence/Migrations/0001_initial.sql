-- Agentic schema v1. All timestamps are ISO-8601 UTC text (lexicographically sortable).
-- Money is stored as INTEGER micro-USD (1 USD = 1,000,000).

CREATE TABLE IF NOT EXISTS Agents (
    Id              TEXT PRIMARY KEY,
    Name            TEXT NOT NULL,
    Source          TEXT NOT NULL DEFAULT 'Config',      -- Config | Admin
    ProfileJson     TEXT NOT NULL,                       -- ProfileOptions (overrides) as JSON
    Enabled         INTEGER NOT NULL DEFAULT 1,
    DesiredState    TEXT NOT NULL DEFAULT 'Running',     -- Running | Stopped | Paused
    CreatedUtc      TEXT NOT NULL,
    ModifiedUtc     TEXT NOT NULL,
    DeletedUtc      TEXT NULL
);

CREATE TABLE IF NOT EXISTS BaseProfiles (
    Id              TEXT PRIMARY KEY,
    ProfileJson     TEXT NOT NULL,
    ModifiedUtc     TEXT NOT NULL
);

-- Materialised view of the effective communication topology (rebuilt from profiles at startup / admin edits).
CREATE TABLE IF NOT EXISTS AgentCommunicationPermissions (
    AgentId             TEXT NOT NULL,
    RecipientAgentId    TEXT NOT NULL,
    AllowedMessageTypes TEXT NOT NULL,
    MaxPriority         TEXT NOT NULL,
    PRIMARY KEY (AgentId, RecipientAgentId)
);
CREATE INDEX IF NOT EXISTS IX_CommPerm_Recipient ON AgentCommunicationPermissions(RecipientAgentId);

CREATE TABLE IF NOT EXISTS AgentTasks (
    Id                  TEXT PRIMARY KEY,
    AgentId             TEXT NOT NULL,
    Name                TEXT NOT NULL,
    Description         TEXT NULL,
    Kind                TEXT NOT NULL,
    Handler             TEXT NOT NULL DEFAULT 'reasoning',
    Purpose             TEXT NULL,
    Status              TEXT NOT NULL,
    Priority            INTEGER NOT NULL DEFAULT 1,
    Payload             TEXT NULL,
    Result              TEXT NULL,
    Error               TEXT NULL,
    RetryCount          INTEGER NOT NULL DEFAULT 0,
    MaxRetries          INTEGER NOT NULL DEFAULT 3,
    NextRunUtc          TEXT NULL,
    CreatedUtc          TEXT NOT NULL,
    StartedUtc          TEXT NULL,
    CompletedUtc        TEXT NULL,
    UpdatedUtc          TEXT NOT NULL,
    ParentTaskId        TEXT NULL,
    ParentAgentId       TEXT NULL,
    DelegatingAgentId   TEXT NULL,
    CorrelationId       TEXT NOT NULL,
    DelegationDepth     INTEGER NOT NULL DEFAULT 0,
    ChainLength         INTEGER NOT NULL DEFAULT 0,
    Ancestry            TEXT NOT NULL DEFAULT '[]',
    SourceMessageId     TEXT NULL,
    ScheduleId          TEXT NULL,
    StartTier           INTEGER NULL,
    CurrentLadderLevel  INTEGER NOT NULL DEFAULT 0,
    EscalationCount     INTEGER NOT NULL DEFAULT 0,
    Checkpoint          TEXT NULL,
    WaitingOn           TEXT NULL,
    WaitDeadlineUtc     TEXT NULL,
    LeaseOwner          TEXT NULL,
    LeaseExpiresUtc     TEXT NULL,
    CostMicros          INTEGER NOT NULL DEFAULT 0,
    CreatedBy           TEXT NULL,
    IdempotencyKey      TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_AgentTasks_SourceMessage ON AgentTasks(SourceMessageId) WHERE SourceMessageId IS NOT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_AgentTasks_Idempotency ON AgentTasks(AgentId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_AgentTasks_Queue ON AgentTasks(AgentId, Status, Priority DESC, NextRunUtc);
CREATE INDEX IF NOT EXISTS IX_AgentTasks_Status ON AgentTasks(Status);
CREATE INDEX IF NOT EXISTS IX_AgentTasks_Correlation ON AgentTasks(CorrelationId);
CREATE INDEX IF NOT EXISTS IX_AgentTasks_Parent ON AgentTasks(ParentTaskId);
CREATE INDEX IF NOT EXISTS IX_AgentTasks_Created ON AgentTasks(CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AgentTasks_Schedule ON AgentTasks(ScheduleId, Status);

CREATE TABLE IF NOT EXISTS TaskSchedules (
    Id              TEXT PRIMARY KEY,               -- {AgentId}:{ScheduleId}
    AgentId         TEXT NOT NULL,
    ScheduleId      TEXT NOT NULL,
    DefinitionJson  TEXT NOT NULL,
    Enabled         INTEGER NOT NULL DEFAULT 1,
    LastRunUtc      TEXT NULL,
    NextRunUtc      TEXT NULL,
    LastTaskId      TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_TaskSchedules_Next ON TaskSchedules(Enabled, NextRunUtc);

CREATE TABLE IF NOT EXISTS TaskHistory (
    Id          INTEGER PRIMARY KEY AUTOINCREMENT,
    TaskId      TEXT NOT NULL,
    AgentId     TEXT NOT NULL,
    FromStatus  TEXT NULL,
    ToStatus    TEXT NOT NULL,
    Note        TEXT NULL,
    CreatedUtc  TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_TaskHistory_Task ON TaskHistory(TaskId);
CREATE INDEX IF NOT EXISTS IX_TaskHistory_Agent ON TaskHistory(AgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_TaskHistory_Created ON TaskHistory(CreatedUtc);

CREATE TABLE IF NOT EXISTS AgentMemory (
    Id              TEXT PRIMARY KEY,
    AgentId         TEXT NOT NULL,
    Category        TEXT NOT NULL,
    Content         TEXT NOT NULL,
    Tags            TEXT NULL,
    Importance      INTEGER NOT NULL DEFAULT 3,
    SourceTaskId    TEXT NULL,
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT NOT NULL,
    LastAccessedUtc TEXT NULL,
    ExpiresUtc      TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_AgentMemory_Agent ON AgentMemory(AgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AgentMemory_AgentCategory ON AgentMemory(AgentId, Category);

CREATE VIRTUAL TABLE IF NOT EXISTS AgentMemoryFts USING fts5(Content, Tags, content='AgentMemory', content_rowid='rowid');
CREATE TRIGGER IF NOT EXISTS TR_AgentMemory_AI AFTER INSERT ON AgentMemory BEGIN
    INSERT INTO AgentMemoryFts(rowid, Content, Tags) VALUES (new.rowid, new.Content, new.Tags);
END;
CREATE TRIGGER IF NOT EXISTS TR_AgentMemory_AD AFTER DELETE ON AgentMemory BEGIN
    INSERT INTO AgentMemoryFts(AgentMemoryFts, rowid, Content, Tags) VALUES ('delete', old.rowid, old.Content, old.Tags);
END;
CREATE TRIGGER IF NOT EXISTS TR_AgentMemory_AU AFTER UPDATE ON AgentMemory BEGIN
    INSERT INTO AgentMemoryFts(AgentMemoryFts, rowid, Content, Tags) VALUES ('delete', old.rowid, old.Content, old.Tags);
    INSERT INTO AgentMemoryFts(rowid, Content, Tags) VALUES (new.rowid, new.Content, new.Tags);
END;

CREATE TABLE IF NOT EXISTS AgentMessages (
    Id                  TEXT PRIMARY KEY,
    CorrelationId       TEXT NOT NULL,
    ParentMessageId     TEXT NULL,
    SenderAgentId       TEXT NOT NULL,
    RecipientAgentId    TEXT NOT NULL,
    MessageType         TEXT NOT NULL,
    Subject             TEXT NULL,
    Content             TEXT NULL,
    StructuredPayload   TEXT NULL,
    Attachments         TEXT NULL,
    Priority            INTEGER NOT NULL DEFAULT 1,
    CreatedUtc          TEXT NOT NULL,
    DeliveredUtc        TEXT NULL,
    StartedUtc          TEXT NULL,
    CompletedUtc        TEXT NULL,
    Status              TEXT NOT NULL,
    ExpiresUtc          TEXT NULL,
    ReplyToMessageId    TEXT NULL,
    RequiresResponse    INTEGER NOT NULL DEFAULT 0,
    ResponseMessageId   TEXT NULL,
    SenderTaskId        TEXT NULL,
    RelatedTaskId       TEXT NULL,
    DelegationDepth     INTEGER NOT NULL DEFAULT 0,
    ChainLength         INTEGER NOT NULL DEFAULT 0,
    Ancestry            TEXT NOT NULL DEFAULT '[]',
    IdempotencyKey      TEXT NULL,
    RejectionReason     TEXT NULL,
    LeaseExpiresUtc     TEXT NULL,
    ContentPurgedUtc    TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_AgentMessages_Idempotency ON AgentMessages(SenderAgentId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Mailbox ON AgentMessages(RecipientAgentId, Status, Priority DESC, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Sender ON AgentMessages(SenderAgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Recipient ON AgentMessages(RecipientAgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Correlation ON AgentMessages(CorrelationId);
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Parent ON AgentMessages(ParentMessageId);
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Created ON AgentMessages(CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AgentMessages_Status ON AgentMessages(Status, ExpiresUtc);

CREATE TABLE IF NOT EXISTS ActivityEvents (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    EventType       TEXT NOT NULL,
    Level           TEXT NOT NULL,
    AgentId         TEXT NULL,
    TaskId          TEXT NULL,
    MessageId       TEXT NULL,
    CorrelationId   TEXT NULL,
    Summary         TEXT NOT NULL,
    DataJson        TEXT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Activity_Agent ON ActivityEvents(AgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Activity_Type ON ActivityEvents(EventType, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Activity_Correlation ON ActivityEvents(CorrelationId);
CREATE INDEX IF NOT EXISTS IX_Activity_Created ON ActivityEvents(CreatedUtc);

CREATE TABLE IF NOT EXISTS AiCalls (
    Id                  TEXT PRIMARY KEY,
    AgentId             TEXT NOT NULL,
    TaskId              TEXT NULL,
    CorrelationId       TEXT NULL,
    ModelKey            TEXT NOT NULL,
    ModelId             TEXT NOT NULL,
    Purpose             TEXT NOT NULL,
    Tier                INTEGER NOT NULL,
    Reason              TEXT NOT NULL,
    QueueWaitMs         INTEGER NOT NULL DEFAULT 0,
    DurationMs          INTEGER NOT NULL DEFAULT 0,
    Success             INTEGER NOT NULL,
    ErrorKind           TEXT NULL,
    Error               TEXT NULL,
    PromptTokens        INTEGER NOT NULL DEFAULT 0,
    CompletionTokens    INTEGER NOT NULL DEFAULT 0,
    ReasoningTokens     INTEGER NOT NULL DEFAULT 0,
    UsageEstimated      INTEGER NOT NULL DEFAULT 0,
    CostMicros          INTEGER NOT NULL DEFAULT 0,
    InputPricePerM      TEXT NULL,
    OutputPricePerM     TEXT NULL,
    FinishReason        TEXT NULL,
    RequestPreview      TEXT NULL,
    ResponsePreview     TEXT NULL,
    CreatedUtc          TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AiCalls_Agent ON AiCalls(AgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AiCalls_Model ON AiCalls(ModelKey, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_AiCalls_Task ON AiCalls(TaskId);
CREATE INDEX IF NOT EXISTS IX_AiCalls_Correlation ON AiCalls(CorrelationId);
CREATE INDEX IF NOT EXISTS IX_AiCalls_Created ON AiCalls(CreatedUtc);

CREATE TABLE IF NOT EXISTS ToolCalls (
    Id              TEXT PRIMARY KEY,
    ToolCallId      TEXT NULL,
    AgentId         TEXT NOT NULL,
    TaskId          TEXT NULL,
    ToolName        TEXT NOT NULL,
    Source          TEXT NOT NULL,
    Arguments       TEXT NULL,
    StartedUtc      TEXT NOT NULL,
    CompletedUtc    TEXT NULL,
    Success         INTEGER NULL,
    Result          TEXT NULL,
    Error           TEXT NULL,
    DurationMs      INTEGER NULL
);
CREATE INDEX IF NOT EXISTS IX_ToolCalls_Agent ON ToolCalls(AgentId, StartedUtc);
CREATE INDEX IF NOT EXISTS IX_ToolCalls_Task ON ToolCalls(TaskId, ToolCallId);
CREATE INDEX IF NOT EXISTS IX_ToolCalls_Started ON ToolCalls(StartedUtc);

CREATE TABLE IF NOT EXISTS Statistics (
    AgentId             TEXT NOT NULL,
    BucketUtc           TEXT NOT NULL,     -- hour bucket yyyy-MM-ddTHH:00:00Z
    TasksCompleted      INTEGER NOT NULL DEFAULT 0,
    TasksFailed         INTEGER NOT NULL DEFAULT 0,
    TasksRetried        INTEGER NOT NULL DEFAULT 0,
    AiCalls             INTEGER NOT NULL DEFAULT 0,
    AiFailures          INTEGER NOT NULL DEFAULT 0,
    AiLatencyMsTotal    INTEGER NOT NULL DEFAULT 0,
    ToolCalls           INTEGER NOT NULL DEFAULT 0,
    ToolFailures        INTEGER NOT NULL DEFAULT 0,
    ToolLatencyMsTotal  INTEGER NOT NULL DEFAULT 0,
    MessagesSent        INTEGER NOT NULL DEFAULT 0,
    MessagesReceived    INTEGER NOT NULL DEFAULT 0,
    Delegations         INTEGER NOT NULL DEFAULT 0,
    TaskDurationMsTotal INTEGER NOT NULL DEFAULT 0,
    Tokens              INTEGER NOT NULL DEFAULT 0,
    CostMicros          INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (AgentId, BucketUtc)
);
CREATE INDEX IF NOT EXISTS IX_Statistics_Bucket ON Statistics(BucketUtc);

CREATE TABLE IF NOT EXISTS AgentWallets (
    AgentId                 TEXT PRIMARY KEY,
    BalanceMicros           INTEGER NOT NULL DEFAULT 0,
    ReservedMicros          INTEGER NOT NULL DEFAULT 0,
    LifetimeSpendMicros     INTEGER NOT NULL DEFAULT 0,
    LifetimeProfitMicros    INTEGER NOT NULL DEFAULT 0,
    LifetimeCreditMicros    INTEGER NOT NULL DEFAULT 0,
    SpentTodayMicros        INTEGER NOT NULL DEFAULT 0,
    SpentTodayDate          TEXT NULL,
    DailyLimitMicros        INTEGER NULL,
    UpdatedUtc              TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS LedgerEntries (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    AgentId             TEXT NOT NULL,
    EntryType           TEXT NOT NULL,
    AmountMicros        INTEGER NOT NULL,
    BalanceAfterMicros  INTEGER NOT NULL,
    ModelKey            TEXT NULL,
    AiCallId            TEXT NULL,
    TaskId              TEXT NULL,
    CorrelationId       TEXT NULL,
    Description         TEXT NULL,
    Reference           TEXT NULL,
    IdempotencyKey      TEXT NULL,
    CreatedBy           TEXT NOT NULL,
    CreatedUtc          TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Ledger_Idempotency ON LedgerEntries(IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Ledger_Agent ON LedgerEntries(AgentId, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Ledger_Type ON LedgerEntries(EntryType, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Ledger_Correlation ON LedgerEntries(CorrelationId);

CREATE TABLE IF NOT EXISTS ModelPricing (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    ModelKey            TEXT NOT NULL,
    InputPerMillion     TEXT NULL,
    OutputPerMillion    TEXT NULL,
    PerImage            TEXT NULL,
    EffectiveFromUtc    TEXT NOT NULL,
    SetBy               TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ModelPricing_Model ON ModelPricing(ModelKey, EffectiveFromUtc);

CREATE TABLE IF NOT EXISTS Artifacts (
    Id              TEXT PRIMARY KEY,
    OwnerAgentId    TEXT NOT NULL,
    Name            TEXT NOT NULL,
    MimeType        TEXT NOT NULL,
    SizeBytes       INTEGER NOT NULL,
    Sha256          TEXT NOT NULL,
    StorageFile     TEXT NOT NULL,
    Description     TEXT NULL,
    SourceTaskId    TEXT NULL,
    CreatedUtc      TEXT NOT NULL,
    ExpiresUtc      TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Artifacts_Owner ON Artifacts(OwnerAgentId, CreatedUtc);

CREATE TABLE IF NOT EXISTS ArtifactGrants (
    ArtifactId          TEXT NOT NULL,
    GranteeAgentId      TEXT NOT NULL,
    GrantedByAgentId    TEXT NOT NULL,
    MessageId           TEXT NULL,
    CreatedUtc          TEXT NOT NULL,
    PRIMARY KEY (ArtifactId, GranteeAgentId)
);
CREATE INDEX IF NOT EXISTS IX_ArtifactGrants_Grantee ON ArtifactGrants(GranteeAgentId);

CREATE TABLE IF NOT EXISTS Approvals (
    Id              TEXT PRIMARY KEY,
    AgentId         TEXT NOT NULL,
    TaskId          TEXT NULL,
    ToolCallId      TEXT NULL,
    Kind            TEXT NOT NULL,
    Title           TEXT NOT NULL,
    Question        TEXT NOT NULL,
    Context         TEXT NULL,
    Status          TEXT NOT NULL,
    Response        TEXT NULL,
    CreatedUtc      TEXT NOT NULL,
    ResolvedUtc     TEXT NULL,
    ResolvedBy      TEXT NULL,
    ExpiresUtc      TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Approvals_Status ON Approvals(Status, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Approvals_Agent ON Approvals(AgentId);

CREATE TABLE IF NOT EXISTS AuditLog (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Actor           TEXT NOT NULL,
    Action          TEXT NOT NULL,
    TargetType      TEXT NULL,
    TargetId        TEXT NULL,
    DetailsJson     TEXT NULL,
    RemoteAddress   TEXT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Audit_Created ON AuditLog(CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Audit_Target ON AuditLog(TargetType, TargetId);

CREATE TABLE IF NOT EXISTS AdminUsers (
    Username        TEXT PRIMARY KEY COLLATE NOCASE,
    PasswordHash    TEXT NOT NULL,
    Source          TEXT NOT NULL DEFAULT 'Bootstrap',
    CreatedUtc      TEXT NOT NULL,
    LastLoginUtc    TEXT NULL
);

CREATE TABLE IF NOT EXISTS HostState (
    Key     TEXT PRIMARY KEY,
    Value   TEXT NULL
);
