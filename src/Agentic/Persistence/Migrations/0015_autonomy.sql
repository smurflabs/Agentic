-- Autonomy upgrade. Everything an agent needs to run with less human prodding:
--   * Hooks and FileWatches  -> outside events (a webhook call, a file landing on disk) wake an agent with a task.
--   * AgentGoals             -> standing objectives the host reviews on a cadence, so the agent re-plans toward them.
--   * ProfileProposals       -> an agent proposes a change to its own behaviour; an administrator approves before it applies.
--   * AgentPromptPatches     -> the approved behaviour addendum, appended to the agent's system prompt.
--   * AgentSkills            -> reusable procedures an agent writes for itself and loads later (instructions only, never code).
--   * AgentMemory.Embedding  -> memories carry a vector so recall can be semantic, not just keyword (falls back when no model).
--   * AgentTasks scheduling   -> dependencies (a DAG), deadlines (SLA), an off-peak flag (run when cheap) and a value weight.

-- -------- inbound event triggers ------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS Hooks (
    Id                  TEXT PRIMARY KEY,
    Token               TEXT NOT NULL UNIQUE,          -- the secret in POST /api/hooks/{token}; knowing it is the authorisation
    AgentId             TEXT NOT NULL,
    Name                TEXT NOT NULL,
    TaskName            TEXT NOT NULL,
    Template            TEXT NULL,                      -- optional task-description template; {{payload}} is replaced with the posted body
    Priority            INTEGER NOT NULL DEFAULT 1,     -- PriorityLevel
    Enabled             INTEGER NOT NULL DEFAULT 1,
    MinIntervalSeconds  INTEGER NOT NULL DEFAULT 0,     -- rate limit: ignore fires that arrive sooner than this after the last
    CreatedBy           TEXT NULL,
    CreatedUtc          TEXT NOT NULL,
    LastFiredUtc        TEXT NULL,
    FireCount           INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_Hooks_Agent ON Hooks(AgentId);

CREATE TABLE IF NOT EXISTS FileWatches (
    Id                  TEXT PRIMARY KEY,
    AgentId             TEXT NOT NULL,
    Path                TEXT NOT NULL,                  -- directory to watch
    Filter              TEXT NULL,                      -- e.g. *.csv (default *.*)
    TaskName            TEXT NOT NULL,
    Template            TEXT NULL,                      -- {{path}} and {{change}} are replaced
    Priority            INTEGER NOT NULL DEFAULT 1,
    Enabled             INTEGER NOT NULL DEFAULT 1,
    DebounceSeconds     INTEGER NOT NULL DEFAULT 5,
    CreatedBy           TEXT NULL,
    CreatedUtc          TEXT NOT NULL,
    LastFiredUtc        TEXT NULL,
    FireCount           INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_FileWatches_Agent ON FileWatches(AgentId);

-- -------- standing goals --------------------------------------------------------------------------
CREATE TABLE IF NOT EXISTS AgentGoals (
    Id                  TEXT PRIMARY KEY,
    AgentId             TEXT NOT NULL,
    Title               TEXT NOT NULL,
    Detail              TEXT NULL,
    Metric              TEXT NULL,                      -- how progress is judged (free text)
    Status              TEXT NOT NULL DEFAULT 'active', -- active | paused | done | abandoned
    Progress            TEXT NULL,                      -- the agent's latest progress note
    ProgressPct         INTEGER NOT NULL DEFAULT 0,
    CadenceMinutes      INTEGER NOT NULL DEFAULT 1440,  -- how often the host injects a review task
    CreatedBy           TEXT NULL,
    CreatedUtc          TEXT NOT NULL,
    UpdatedUtc          TEXT NOT NULL,
    LastReviewUtc       TEXT NULL,
    NextReviewUtc       TEXT NULL,
    Reviews             INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_AgentGoals_Due ON AgentGoals(Status, NextReviewUtc);
CREATE INDEX IF NOT EXISTS IX_AgentGoals_Agent ON AgentGoals(AgentId);

-- -------- self-modification (approval-gated) ------------------------------------------------------
CREATE TABLE IF NOT EXISTS ProfileProposals (
    Id                  TEXT PRIMARY KEY,
    AgentId             TEXT NOT NULL,
    Kind                TEXT NOT NULL,                  -- behavior (system-prompt addendum) for now
    Proposed            TEXT NOT NULL,
    Rationale           TEXT NULL,
    Status              TEXT NOT NULL DEFAULT 'pending',-- pending | approved | rejected
    ApprovalId          TEXT NULL,
    CreatedUtc          TEXT NOT NULL,
    DecidedBy           TEXT NULL,
    DecidedUtc          TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_ProfileProposals_Agent ON ProfileProposals(AgentId, Status);

-- The approved behaviour addendum, appended to the agent's system prompt at prompt-build time.
CREATE TABLE IF NOT EXISTS AgentPromptPatches (
    AgentId             TEXT PRIMARY KEY,
    Append              TEXT NULL,
    UpdatedUtc          TEXT NOT NULL
);

-- Reusable procedures an agent writes for itself (instructions, never executable code).
CREATE TABLE IF NOT EXISTS AgentSkills (
    Id                  TEXT PRIMARY KEY,
    AgentId             TEXT NOT NULL,
    Name                TEXT NOT NULL,
    Description         TEXT NULL,
    Instructions        TEXT NOT NULL,
    Uses                INTEGER NOT NULL DEFAULT 0,
    CreatedUtc          TEXT NOT NULL,
    UpdatedUtc          TEXT NOT NULL,
    UNIQUE(AgentId, Name)
);

-- -------- semantic memory -------------------------------------------------------------------------
ALTER TABLE AgentMemory ADD COLUMN Embedding BLOB NULL;          -- unit-normalised float32, little-endian (same format as KnowledgeChunks)
ALTER TABLE AgentMemory ADD COLUMN EmbeddingModel TEXT NULL;     -- the model it was embedded with (NULL = keyword-only)

-- -------- scheduling (DAG, SLA, off-peak, value) --------------------------------------------------
ALTER TABLE AgentTasks ADD COLUMN DependsOn TEXT NULL;           -- JSON array of task ids that must finish first
ALTER TABLE AgentTasks ADD COLUMN DeadlineUtc TEXT NULL;         -- SLA: near-deadline tasks jump the queue
ALTER TABLE AgentTasks ADD COLUMN OffPeak INTEGER NOT NULL DEFAULT 0;  -- 1 = prefer to run only inside the off-peak window
ALTER TABLE AgentTasks ADD COLUMN Weight INTEGER NOT NULL DEFAULT 0;   -- task value (0-100); higher justifies a stronger/paid model
