-- App previews: a web app running in a sandbox container (a published port on the sandbox's Docker host) opened through
-- the host at /container/{Id}/ on its own port. The Id is the link's secret (a capability): whoever has it can open the
-- app until it expires or is revoked. Only containers the host started (not infrastructure) can be previewed.
CREATE TABLE IF NOT EXISTS SandboxPreviews (
    Id              TEXT PRIMARY KEY,       -- "<name>-<random>": the path segment after /container/
    OwnerUser       TEXT NOT NULL,
    ChatId          TEXT NULL,
    ContainerId     TEXT NOT NULL,
    ContainerName   TEXT NULL,
    TargetHost      TEXT NOT NULL,          -- the sandbox's Docker host
    TargetPort      INTEGER NOT NULL,       -- the published host port
    ContainerPort   INTEGER NULL,
    Transport       TEXT NULL,              -- direct (this host reaches the port) | mcp (through the sandbox connection)
    CreatedUtc      TEXT NOT NULL,
    ExpiresUtc      TEXT NOT NULL,
    LastUsedUtc     TEXT NULL,
    RevokedUtc      TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_SandboxPreviews_Owner ON SandboxPreviews(OwnerUser);
CREATE INDEX IF NOT EXISTS IX_SandboxPreviews_Container ON SandboxPreviews(ContainerId);
