-- Agentic schema v2: subagents, admin-registered (custom) models with per-agent grants, explicit task queue order,
-- and per-spender attribution on ledger entries (subagent spend is charged to the parent's wallet).

-- Explicit queue order. Lower rank runs first; new tasks are inserted by priority, admins can reorder freely.
ALTER TABLE AgentTasks ADD COLUMN QueueRank REAL NULL;
-- Model pinned by an administrator for this task (must be permitted for the agent).
ALTER TABLE AgentTasks ADD COLUMN ModelKey TEXT NULL;

UPDATE AgentTasks SET QueueRank = (
    SELECT x.rn * 1000.0 FROM (
        SELECT Id, ROW_NUMBER() OVER (PARTITION BY AgentId ORDER BY Priority DESC, CreatedUtc ASC) AS rn
        FROM AgentTasks WHERE Status IN ('Pending','Retrying','Waiting','Blocked')
    ) x WHERE x.Id = AgentTasks.Id)
WHERE Status IN ('Pending','Retrying','Waiting','Blocked');

CREATE INDEX IF NOT EXISTS IX_AgentTasks_Rank ON AgentTasks(AgentId, Status, QueueRank);

-- Which agent actually incurred a charge (a subagent spends from its parent's wallet).
ALTER TABLE LedgerEntries ADD COLUMN SpentByAgentId TEXT NULL;
CREATE INDEX IF NOT EXISTS IX_Ledger_SpentBy ON LedgerEntries(SpentByAgentId);

CREATE TABLE IF NOT EXISTS Subagents (
    Id              TEXT PRIMARY KEY,
    ParentAgentId   TEXT NOT NULL,
    ParentTaskId    TEXT NULL,
    TaskId          TEXT NOT NULL,
    Name            TEXT NOT NULL,
    SpecJson        TEXT NOT NULL,           -- SubagentSpec (models, purpose, cap, timeout) to rebuild after a restart
    SpendCapMicros  INTEGER NULL,
    SpentMicros     INTEGER NOT NULL DEFAULT 0,
    Status          TEXT NOT NULL,           -- Running | Completed | Failed | Cancelled
    ResultJson      TEXT NULL,
    IdempotencyKey  TEXT NULL,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL,
    DeadlineUtc     TEXT NULL,
    EndedUtc        TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Subagents_Parent ON Subagents(ParentAgentId, Status);
CREATE INDEX IF NOT EXISTS IX_Subagents_ParentTask ON Subagents(ParentTaskId);
CREATE INDEX IF NOT EXISTS IX_Subagents_Status ON Subagents(Status, CreatedUtc);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Subagents_Idempotency ON Subagents(ParentAgentId, IdempotencyKey) WHERE IdempotencyKey IS NOT NULL;

-- Additional cloud models registered by administrators at runtime. Only agents with an explicit grant may use them.
CREATE TABLE IF NOT EXISTS CustomModels (
    Key             TEXT PRIMARY KEY,
    DefinitionJson  TEXT NOT NULL,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL,
    ModifiedUtc     TEXT NOT NULL,
    DeletedUtc      TEXT NULL
);

-- Per-agent grants of additional models, each with what the model should be used for. Revoked rows are kept for audit.
CREATE TABLE IF NOT EXISTS AgentModelGrants (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    AgentId         TEXT NOT NULL,
    ModelKey        TEXT NOT NULL,
    UseFor          TEXT NOT NULL,
    AllowSubagents  INTEGER NOT NULL DEFAULT 0,
    GrantedBy       TEXT NOT NULL,
    GrantedUtc      TEXT NOT NULL,
    RevokedBy       TEXT NULL,
    RevokedUtc      TEXT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ModelGrants_Active ON AgentModelGrants(AgentId, ModelKey) WHERE RevokedUtc IS NULL;
CREATE INDEX IF NOT EXISTS IX_ModelGrants_Model ON AgentModelGrants(ModelKey);
