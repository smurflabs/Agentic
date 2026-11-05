-- Agentic schema v7: task delivery and review (a reviewer agent checks the work against criteria only it can see, and
-- approved results are delivered to the administrator), files attached to tasks, and the Results inbox where
-- administrators view, download, rate and reward results.

-- {deliver, reviewerAgentId, maxRevisions, attachments[], rootTaskId, revision, reviewOfTaskId, reviewOfGlobalTaskId}
ALTER TABLE AgentTasks ADD COLUMN OptionsJson TEXT NULL;
-- Expected outcome for the reviewer. Never shown to the agent doing the work.
ALTER TABLE AgentTasks ADD COLUMN ReviewCriteria TEXT NULL;

ALTER TABLE GlobalTasks ADD COLUMN Revision INTEGER NOT NULL DEFAULT 0;
ALTER TABLE GlobalTasks ADD COLUMN ReviewTaskId TEXT NULL;
ALTER TABLE GlobalTasks ADD COLUMN ReviewJson TEXT NULL;          -- latest verdict {verdict, score, feedback, reviewer}
ALTER TABLE GlobalTasks ADD COLUMN ResultId TEXT NULL;

-- Files an administrator uploaded to attach to tasks. Text is extracted once (documents, spreadsheets, archives, code).
CREATE TABLE IF NOT EXISTS Uploads (
    Id              TEXT PRIMARY KEY,
    Name            TEXT NOT NULL,
    MimeType        TEXT NOT NULL,
    SizeBytes       INTEGER NOT NULL,
    Sha256          TEXT NOT NULL,
    StorageFile     TEXT NOT NULL,
    Kind            TEXT NOT NULL,          -- text | code | document | spreadsheet | presentation | pdf | archive | image | binary
    Summary         TEXT NULL,              -- one line: "Excel workbook, 3 sheets, 120 rows"
    ExtractedText   TEXT NULL,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Uploads_Created ON Uploads(CreatedUtc);

-- Results delivered to the administrator (from agent tasks and global tasks).
CREATE TABLE IF NOT EXISTS Results (
    Id              TEXT PRIMARY KEY,
    SourceType      TEXT NOT NULL,          -- Task | GlobalTask
    SourceId        TEXT NOT NULL,          -- root task id or global task id
    CorrelationId   TEXT NULL,
    Title           TEXT NOT NULL,
    AgentId         TEXT NULL,              -- agent that produced the result (global tasks: the final answer's author or NULL)
    Participants    TEXT NOT NULL DEFAULT '[]',
    Success         INTEGER NOT NULL DEFAULT 1,
    Summary         TEXT NULL,
    DataJson        TEXT NULL,
    Instructions    TEXT NULL,
    ReviewerAgentId TEXT NULL,
    ReviewVerdict   TEXT NULL,              -- Approved | Rejected | NotReviewed
    ReviewScore     INTEGER NULL,           -- 1-10
    ReviewFeedback  TEXT NULL,
    Revisions       INTEGER NOT NULL DEFAULT 0,
    FolderPath      TEXT NULL,              -- snapshot of the files (data\results\{id})
    FilesJson       TEXT NOT NULL DEFAULT '[]',
    ConversationJson TEXT NULL,
    CostMicros      INTEGER NOT NULL DEFAULT 0,
    Status          TEXT NOT NULL DEFAULT 'New',   -- New | Viewed | Rated | Archived
    Rating          INTEGER NULL,           -- 1-5
    RatingComment   TEXT NULL,
    RatedBy         TEXT NULL,
    RatedUtc        TEXT NULL,
    AwardedMicros   INTEGER NOT NULL DEFAULT 0,
    CreatedBy       TEXT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Results_Status ON Results(Status, CreatedUtc);
CREATE UNIQUE INDEX IF NOT EXISTS UX_Results_Source ON Results(SourceType, SourceId);

-- The reasoning conversation of tasks that are reviewed or delivered (the checkpoint is cleared when a task ends).
CREATE TABLE IF NOT EXISTS TaskTranscripts (
    TaskId          TEXT PRIMARY KEY,
    AgentId         TEXT NOT NULL,
    TranscriptJson  TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS ResultAwards (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    ResultId        TEXT NOT NULL,
    AgentId         TEXT NOT NULL,
    AmountMicros    INTEGER NOT NULL,
    LedgerEntryId   INTEGER NULL,
    Note            TEXT NULL,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ResultAwards_Result ON ResultAwards(ResultId);
