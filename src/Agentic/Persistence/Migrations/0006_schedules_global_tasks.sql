-- Agentic schema v6: recurring tasks created in the dashboard, and global tasks (the host plans a request, distributes
-- it across agents, runs the steps and combines their results into one final answer).

ALTER TABLE TaskSchedules ADD COLUMN Source TEXT NOT NULL DEFAULT 'Profile';     -- Profile (appsettings / agent editor) | Admin (dashboard)
ALTER TABLE TaskSchedules ADD COLUMN RunCount INTEGER NOT NULL DEFAULT 0;
ALTER TABLE TaskSchedules ADD COLUMN CreatedBy TEXT NULL;
ALTER TABLE TaskSchedules ADD COLUMN CreatedUtc TEXT NULL;

CREATE TABLE IF NOT EXISTS GlobalTasks (
    Id              TEXT PRIMARY KEY,
    Name            TEXT NOT NULL,
    Description     TEXT NULL,
    Status          TEXT NOT NULL,          -- Scheduled | Planning | AwaitingApproval | Running | Synthesizing | Completed | Failed | Cancelled
    OptionsJson     TEXT NOT NULL,
    Understanding   TEXT NULL,              -- the planner's reading of the request
    Approach        TEXT NULL,              -- single | parallel | pipeline
    PlanJson        TEXT NULL,
    PlannerModel    TEXT NULL,              -- model key, or "rules" for the fallback
    Feedback        TEXT NULL,              -- administrator feedback for re-planning
    CorrelationId   TEXT NOT NULL,
    FinalResult     TEXT NULL,
    Error           TEXT NULL,
    ScheduleId      TEXT NULL,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL,
    NotBeforeUtc    TEXT NULL,
    StartedUtc      TEXT NULL,
    CompletedUtc    TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_GlobalTasks_Status ON GlobalTasks(Status, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_GlobalTasks_Correlation ON GlobalTasks(CorrelationId);

CREATE TABLE IF NOT EXISTS GlobalTaskSteps (
    GlobalTaskId    TEXT NOT NULL,
    StepId          TEXT NOT NULL,
    Ordinal         INTEGER NOT NULL,
    AgentId         TEXT NOT NULL,
    Title           TEXT NOT NULL,
    Instructions    TEXT NOT NULL,
    DependsOn       TEXT NULL,              -- JSON array of step ids
    TaskId          TEXT NULL,
    Status          TEXT NOT NULL,          -- Waiting | Running | Completed | Failed | Cancelled
    Result          TEXT NULL,
    Error           TEXT NULL,
    StartedUtc      TEXT NULL,
    CompletedUtc    TEXT NULL,
    PRIMARY KEY (GlobalTaskId, StepId)
);
CREATE INDEX IF NOT EXISTS IX_GlobalTaskSteps_Task ON GlobalTaskSteps(TaskId);

CREATE TABLE IF NOT EXISTS GlobalTaskSchedules (
    Id              TEXT PRIMARY KEY,
    Name            TEXT NOT NULL,
    Description     TEXT NULL,
    OptionsJson     TEXT NOT NULL,
    ScheduleJson    TEXT NOT NULL,          -- recurrence (interval/cron, time zone, start/end, max runs)
    Summary         TEXT NULL,
    Enabled         INTEGER NOT NULL DEFAULT 1,
    NextRunUtc      TEXT NULL,
    LastRunUtc      TEXT NULL,
    LastGlobalTaskId TEXT NULL,
    RunCount        INTEGER NOT NULL DEFAULT 0,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL
);
