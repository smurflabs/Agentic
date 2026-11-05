-- Shared artifacts (the Artifacts page): public links that run an HTML or SVG artifact from a chat answer, or a website
-- (a folder with index.html) the desktop app, VS Code or the CLI uploaded from a project. Anyone with the link can open it;
-- it runs as an isolated origin (Content-Security-Policy sandbox), so it cannot use the viewer's Agentic session.
CREATE TABLE IF NOT EXISTS SharedArtifacts (
    Token        TEXT PRIMARY KEY,
    Username     TEXT NOT NULL,
    Kind         TEXT NOT NULL,          -- code (a block from a chat answer) | site (files: index.html and its assets)
    Title        TEXT NOT NULL,
    Lang         TEXT NULL,              -- code: html | svg
    Content      TEXT NULL,              -- code: the block as it was shared
    ChatId       TEXT NULL,              -- code: where it came from
    MessageId    TEXT NULL,
    BlockIndex   INTEGER NULL,
    Source       TEXT NULL,              -- site: the project it came from (a folder name)
    SourceKey    TEXT NULL,              -- site: the project's id on its computer; sharing it again updates the same link
    Files        INTEGER NOT NULL DEFAULT 0,
    Bytes        INTEGER NOT NULL DEFAULT 0,
    Views        INTEGER NOT NULL DEFAULT 0,
    CreatedUtc   TEXT NOT NULL,
    UpdatedUtc   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SharedArtifacts_User ON SharedArtifacts(Username, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_SharedArtifacts_Source ON SharedArtifacts(Username, SourceKey);
