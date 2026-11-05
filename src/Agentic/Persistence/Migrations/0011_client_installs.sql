-- Desktop apps and CLIs that checked for updates: which version each install runs, so admins can see what is outdated.
CREATE TABLE IF NOT EXISTS ClientInstalls (
    App           TEXT NOT NULL,
    InstallId     TEXT NOT NULL,
    Username      TEXT NULL,
    Machine       TEXT NULL,
    Version       TEXT NULL,
    Sha256        TEXT NULL,
    RemoteAddress TEXT NULL,
    FirstSeenUtc  TEXT NOT NULL,
    LastSeenUtc   TEXT NOT NULL,
    PRIMARY KEY (App, InstallId)
);
CREATE INDEX IF NOT EXISTS IX_ClientInstalls_LastSeen ON ClientInstalls(LastSeenUtc);
