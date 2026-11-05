-- Long-lived things started in the code sandbox (Open Terminal): background processes and Docker containers/compose projects.
-- The reaper stops the ones it started once they go past their idle or maximum lifetime, so the sandbox host is not left with
-- containers and processes running forever. Only rows recorded here are ever acted on; pre-existing infrastructure is never touched.
CREATE TABLE IF NOT EXISTS SandboxResources (
    Id              TEXT PRIMARY KEY,
    ServerId        TEXT NOT NULL,          -- the code MCP server (e.g. code-exec)
    Kind            TEXT NOT NULL,          -- process | container | compose
    Ref             TEXT NOT NULL,          -- process id | container id | compose project name
    Name            TEXT NULL,              -- a readable name (container name, compose project, command)
    WorkingDir      TEXT NULL,              -- compose working dir, for `compose down`
    Command         TEXT NULL,              -- the command that started it (truncated)
    OwnerUser       TEXT NULL,              -- the chat user it is attributed to, when known
    OwnerChatId     TEXT NULL,              -- the chat it was started from, when known
    OwnerAgentId    TEXT NULL,              -- the agent that started it, when known
    Protected       INTEGER NOT NULL DEFAULT 0,  -- 1 = never reap (existed before the host started, or on the keep list)
    State           TEXT NOT NULL DEFAULT 'running',  -- running | stopped | reaped | gone | failed
    IdleMinutes     INTEGER NULL,           -- override for this resource (NULL = the configured default)
    MaxLifeMinutes  INTEGER NULL,           -- override for this resource (NULL = the configured default)
    CreatedUtc      TEXT NULL,              -- when the resource itself started (its StartedAt, when known)
    FirstSeenUtc    TEXT NOT NULL,          -- when the host first recorded it
    LastSeenUtc     TEXT NOT NULL,          -- last time it was seen running or used
    StoppedUtc      TEXT NULL,
    Reason          TEXT NULL,              -- why it was stopped (idle / max lifetime / manual)
    Notified        INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS IX_SandboxResources_State ON SandboxResources(State);
CREATE UNIQUE INDEX IF NOT EXISTS UX_SandboxResources_RefKind ON SandboxResources(ServerId, Kind, Ref);
