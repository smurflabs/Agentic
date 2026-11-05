-- Agentic schema v3: organizational learning. When an agent goes broke or becomes very profitable, the host studies
-- the decisions that led there (a case study) and publishes generalized lessons every agent can learn from.

CREATE TABLE IF NOT EXISTS CaseStudies (
    Id              TEXT PRIMARY KEY,
    AgentId         TEXT NOT NULL,
    Outcome         TEXT NOT NULL,           -- Failure | Success | Review
    TriggerKind     TEXT NOT NULL,           -- Broke | LowBalance | Profit | RoiMilestone | Manual
    TriggerDetail   TEXT NULL,
    Status          TEXT NOT NULL,           -- Pending | Analyzing | Published | AwaitingReview | Failed
    WindowStartUtc  TEXT NOT NULL,
    WindowEndUtc    TEXT NOT NULL,
    EvidenceJson    TEXT NULL,               -- metrics and decision trace the analysis was based on
    ReportJson      TEXT NULL,               -- structured analysis (headline, key decisions, root causes, lessons, recommendations)
    Headline        TEXT NULL,
    AnalysisModel   TEXT NULL,               -- model key, or "rules" for the deterministic fallback
    Error           TEXT NULL,
    Note            TEXT NULL,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL,
    CompletedUtc    TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_CaseStudies_Agent ON CaseStudies(AgentId, Outcome, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_CaseStudies_Status ON CaseStudies(Status, CreatedUtc);

CREATE TABLE IF NOT EXISTS Lessons (
    Id              TEXT PRIMARY KEY,
    CaseStudyId     TEXT NULL,
    SourceAgentId   TEXT NOT NULL,
    Outcome         TEXT NOT NULL,           -- Failure | Success | Review
    Kind            TEXT NOT NULL,           -- Do | Avoid
    Title           TEXT NOT NULL,
    Body            TEXT NOT NULL,
    Tags            TEXT NULL,               -- purposes / roles / topics, comma separated
    Status          TEXT NOT NULL,           -- Active | PendingReview | Retracted
    Pinned          INTEGER NOT NULL DEFAULT 0,
    UseCount        INTEGER NOT NULL DEFAULT 0,
    LastUsedUtc     TEXT NULL,
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT NOT NULL,
    UpdatedBy       TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_Lessons_Status ON Lessons(Status, Pinned, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_Lessons_Study ON Lessons(CaseStudyId);

CREATE VIRTUAL TABLE IF NOT EXISTS LessonsFts USING fts5(Title, Body, Tags, content='Lessons', content_rowid='rowid');
CREATE TRIGGER IF NOT EXISTS TR_Lessons_AI AFTER INSERT ON Lessons BEGIN
    INSERT INTO LessonsFts(rowid, Title, Body, Tags) VALUES (new.rowid, new.Title, new.Body, new.Tags);
END;
CREATE TRIGGER IF NOT EXISTS TR_Lessons_AD AFTER DELETE ON Lessons BEGIN
    INSERT INTO LessonsFts(LessonsFts, rowid, Title, Body, Tags) VALUES ('delete', old.rowid, old.Title, old.Body, old.Tags);
END;
CREATE TRIGGER IF NOT EXISTS TR_Lessons_AU AFTER UPDATE OF Title, Body, Tags ON Lessons BEGIN
    INSERT INTO LessonsFts(LessonsFts, rowid, Title, Body, Tags) VALUES ('delete', old.rowid, old.Title, old.Body, old.Tags);
    INSERT INTO LessonsFts(rowid, Title, Body, Tags) VALUES (new.rowid, new.Title, new.Body, new.Tags);
END;
