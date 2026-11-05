-- Chat features people know from ChatGPT and Claude: projects, earlier versions of answers and edited questions,
-- read-only share links, feedback on answers, temporary chats, a memory about the user, and saved prompts.
ALTER TABLE Chats ADD COLUMN ProjectId TEXT NULL;
ALTER TABLE Chats ADD COLUMN Temporary INTEGER NOT NULL DEFAULT 0;      -- not in the history; deleted after a day
ALTER TABLE Chats ADD COLUMN ShareToken TEXT NULL;                      -- a read-only public link (NULL = not shared)
ALTER TABLE Chats ADD COLUMN SharedUtc TEXT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_Chats_Share ON Chats(ShareToken) WHERE ShareToken IS NOT NULL;
CREATE INDEX IF NOT EXISTS IX_Chats_Project ON Chats(ProjectId) WHERE ProjectId IS NOT NULL;

ALTER TABLE ChatMessages ADD COLUMN Feedback INTEGER NULL;              -- 1 good, -1 bad
ALTER TABLE ChatMessages ADD COLUMN FeedbackNote TEXT NULL;
ALTER TABLE ChatMessages ADD COLUMN FinishReason TEXT NULL;             -- stop | length | stopped | error

-- Earlier versions of a conversation from one point on: editing a question or regenerating an answer keeps what it
-- replaced (with the versions inside it), so the user can go back to it.
CREATE TABLE IF NOT EXISTS ChatBranches (
    Id              TEXT PRIMARY KEY,
    ChatId          TEXT NOT NULL,
    AnchorIndex     INTEGER NOT NULL,       -- position in the conversation where the versions start to differ
    PayloadJson     TEXT NOT NULL,          -- {messages: [...], branches: [...]} as they were
    CreatedUtc      TEXT NOT NULL           -- when that version was first written
);
CREATE INDEX IF NOT EXISTS IX_ChatBranches_Chat ON ChatBranches(ChatId, AnchorIndex);

CREATE TABLE IF NOT EXISTS ChatProjects (
    Id              TEXT PRIMARY KEY,
    Username        TEXT NOT NULL COLLATE NOCASE,
    Name            TEXT NOT NULL,
    Description     TEXT NULL,
    Instructions    TEXT NULL,              -- added to every chat in the project
    FilesJson       TEXT NULL,              -- knowledge files: [{uploadId, name, kind, summary, sizeBytes}]
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ChatProjects_User ON ChatProjects(Username);

-- What the assistant remembers about a user across chats.
CREATE TABLE IF NOT EXISTS UserMemories (
    Id              TEXT PRIMARY KEY,
    Username        TEXT NOT NULL COLLATE NOCASE,
    Content         TEXT NOT NULL,
    Source          TEXT NOT NULL,          -- chat (the model saved it when asked) | user (typed in Personalization)
    ChatId          TEXT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_UserMemories_User ON UserMemories(Username, CreatedUtc);

-- Saved prompts: typing /command in the message box inserts them.
CREATE TABLE IF NOT EXISTS ChatPrompts (
    Id              TEXT PRIMARY KEY,
    Username        TEXT NOT NULL COLLATE NOCASE,
    Command         TEXT NOT NULL COLLATE NOCASE,
    Title           TEXT NULL,
    Content         TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS UX_ChatPrompts_Command ON ChatPrompts(Username, Command);
