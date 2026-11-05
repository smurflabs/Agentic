-- Standing approval rules: an administrator lets an agent (or every agent) use a tool that needs approval without asking each
-- time, for a while or until revoked. Mode 'safe' approves only calls whose command reads and changes nothing.
CREATE TABLE IF NOT EXISTS ApprovalRules (
    Id          TEXT PRIMARY KEY,
    AgentId     TEXT NOT NULL,                    -- an agent id, or '*' for every agent
    Tool        TEXT NOT NULL,                    -- a tool name (mcp__shell__run_command), a prefix ending in '*', or '*'
    Mode        TEXT NOT NULL DEFAULT 'all',      -- all | safe
    ExpiresUtc  TEXT NULL,                        -- NULL = until revoked
    Note        TEXT NULL,
    CreatedBy   TEXT NOT NULL,
    CreatedUtc  TEXT NOT NULL,
    RevokedUtc  TEXT NULL,
    RevokedBy   TEXT NULL,
    Uses        INTEGER NOT NULL DEFAULT 0,
    LastUsedUtc TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_ApprovalRules_Active ON ApprovalRules(RevokedUtc, ExpiresUtc);
