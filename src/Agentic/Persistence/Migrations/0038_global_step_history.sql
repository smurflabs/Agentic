-- The steps of earlier attempts at a global task. A revision (the reviewer sent the work back) plans the steps again from
-- scratch and replaced the old ones, so the page could no longer show what the first attempt did (LedgerLite revision 0).
CREATE TABLE IF NOT EXISTS GlobalTaskStepHistory (
    GlobalTaskId TEXT NOT NULL,
    Revision INTEGER NOT NULL,
    StepId TEXT NOT NULL,
    Ordinal INTEGER NOT NULL,
    AgentId TEXT NOT NULL,
    Title TEXT NOT NULL,
    DependsOn TEXT NULL,
    TaskId TEXT NULL,
    Status TEXT NOT NULL,
    Result TEXT NULL,
    Error TEXT NULL,
    StartedUtc TEXT NULL,
    CompletedUtc TEXT NULL,
    PRIMARY KEY (GlobalTaskId, Revision, StepId)
);
