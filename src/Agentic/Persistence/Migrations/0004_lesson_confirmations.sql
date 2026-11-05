-- Agentic schema v4: lesson reinforcement. When a later case study reaches a conclusion the library already holds,
-- the existing lesson is confirmed (counted and linked to that study) instead of being written again. Lessons that
-- several independent studies confirmed rank higher when lessons are chosen for agents' prompts.

ALTER TABLE Lessons ADD COLUMN Confirmations INTEGER NOT NULL DEFAULT 1;   -- studies supporting the lesson (1 = its origin)
ALTER TABLE Lessons ADD COLUMN LastConfirmedUtc TEXT NULL;

CREATE TABLE IF NOT EXISTS LessonConfirmations (
    LessonId     TEXT NOT NULL,
    CaseStudyId  TEXT NOT NULL,
    AgentId      TEXT NOT NULL,
    Outcome      TEXT NOT NULL,
    CreatedUtc   TEXT NOT NULL,
    PRIMARY KEY (LessonId, CaseStudyId)
);
CREATE INDEX IF NOT EXISTS IX_LessonConfirmations_Study ON LessonConfirmations(CaseStudyId);
