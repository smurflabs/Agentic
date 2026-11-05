-- Fully autonomous agents: approvals decided by policy or by a supervisor agent, a secrets vault for tools, hired agents,
-- evaluation suites that roll back behaviour changes that make an agent worse, and email inboxes that start tasks.

-- Who decided an approval and why (policy, a supervisor agent, a time-out, or a person), and where it waits now.
ALTER TABLE Approvals ADD COLUMN DecidedBy TEXT NULL;            -- human | policy | agent:<id> | timeout
ALTER TABLE Approvals ADD COLUMN Reason TEXT NULL;               -- why the policy decided, or where the request went
ALTER TABLE Approvals ADD COLUMN Stage TEXT NULL;                -- NULL = waiting for a person; supervisor:<agent> = sent to that agent
ALTER TABLE Approvals ADD COLUMN StageTaskId TEXT NULL;          -- the supervisor's decision task

-- Values tools use without the model seeing them: {{secret:NAME}} in http_request, an email inbox's password.
CREATE TABLE IF NOT EXISTS AgentSecrets (
    Name        TEXT PRIMARY KEY,
    Value       TEXT NOT NULL,                    -- encrypted with the host's secrets.key (SecretBox)
    Description TEXT NULL,
    AgentIds    TEXT NOT NULL DEFAULT '[]',       -- JSON array of agents that may use it ("*" = every agent)
    Hosts       TEXT NOT NULL DEFAULT '[]',       -- JSON array of hosts it may be sent to (empty = any host the agent may call)
    CreatedBy   TEXT NULL,
    CreatedUtc  TEXT NOT NULL,
    UpdatedUtc  TEXT NOT NULL,
    LastUsedUtc TEXT NULL,
    Uses        INTEGER NOT NULL DEFAULT 0
);

-- Agents an agent hired (hire_agent) and retired (retire_agent).
CREATE TABLE IF NOT EXISTS AgentHires (
    AgentId       TEXT PRIMARY KEY,
    HiredBy       TEXT NOT NULL,
    Reason        TEXT NULL,
    ApprovalId    TEXT NULL,
    CreatedUtc    TEXT NOT NULL,
    RetiredUtc    TEXT NULL,
    RetiredReason TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_AgentHires_By ON AgentHires(HiredBy);

-- Evaluation suites: fixed test tasks an agent is run on (after a behaviour change, or on demand), judged against criteria.
CREATE TABLE IF NOT EXISTS EvalCases (
    Id           TEXT PRIMARY KEY,
    AgentId      TEXT NOT NULL,
    Name         TEXT NOT NULL,
    Instructions TEXT NOT NULL,
    Criteria     TEXT NULL,                       -- what a good result has (only the judge sees it)
    Enabled      INTEGER NOT NULL DEFAULT 1,
    CreatedBy    TEXT NULL,
    CreatedUtc   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_EvalCases_Agent ON EvalCases(AgentId);

CREATE TABLE IF NOT EXISTS EvalRuns (
    Id            TEXT PRIMARY KEY,
    AgentId       TEXT NOT NULL,
    TriggerKind   TEXT NOT NULL,                  -- manual | change | baseline
    ChangeRef     TEXT NULL,                      -- the behaviour change (proposal id) the run checks
    Status        TEXT NOT NULL,                  -- running | done | failed
    Total         INTEGER NOT NULL DEFAULT 0,
    Passed        INTEGER NOT NULL DEFAULT 0,
    AvgScore      REAL NULL,
    CostMicros    INTEGER NOT NULL DEFAULT 0,
    BaselineRunId TEXT NULL,
    Verdict       TEXT NULL,                      -- baseline | kept | rolled-back | inconclusive
    Note          TEXT NULL,
    CreatedBy     TEXT NULL,
    StartedUtc    TEXT NOT NULL,
    FinishedUtc   TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_EvalRuns_Agent ON EvalRuns(AgentId, StartedUtc);

CREATE TABLE IF NOT EXISTS EvalResults (
    RunId       TEXT NOT NULL,
    CaseId      TEXT NOT NULL,
    TaskId      TEXT NULL,
    Status      TEXT NOT NULL,                    -- pending | passed | failed | error
    Score       INTEGER NULL,
    Feedback    TEXT NULL,
    CostMicros  INTEGER NOT NULL DEFAULT 0,
    FinishedUtc TEXT NULL,
    PRIMARY KEY (RunId, CaseId)
);

-- Mailboxes whose new mail becomes tasks for an agent (IMAP; the password is a vault secret).
CREATE TABLE IF NOT EXISTS EmailInboxes (
    Id             TEXT PRIMARY KEY,
    AgentId        TEXT NOT NULL,
    Host           TEXT NOT NULL,
    Port           INTEGER NOT NULL DEFAULT 993,
    UseSsl         INTEGER NOT NULL DEFAULT 1,
    Username       TEXT NOT NULL,
    PasswordSecret TEXT NOT NULL,
    Folder         TEXT NOT NULL DEFAULT 'INBOX',
    PollSeconds    INTEGER NOT NULL DEFAULT 120,
    AllowedSenders TEXT NULL,                     -- comma-separated addresses or @domains (empty = anyone)
    TaskName       TEXT NULL,
    Template       TEXT NULL,
    Priority       INTEGER NOT NULL DEFAULT 1,
    Enabled        INTEGER NOT NULL DEFAULT 1,
    LastUid        INTEGER NOT NULL DEFAULT 0,
    UidValidity    INTEGER NULL,
    LastPollUtc    TEXT NULL,
    LastError      TEXT NULL,
    Received       INTEGER NOT NULL DEFAULT 0,
    CreatedBy      TEXT NULL,
    CreatedUtc     TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_EmailInboxes_Agent ON EmailInboxes(AgentId);

-- A behaviour change an evaluation showed to make the agent worse is rolled back (Status 'rolled-back').
ALTER TABLE ProfileProposals ADD COLUMN RolledBackUtc TEXT NULL;
ALTER TABLE ProfileProposals ADD COLUMN RollbackReason TEXT NULL;
