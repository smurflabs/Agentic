-- Scheduled prompts (/schedule in the Chat tab, the CLI): the server sends a prompt to a chat on a schedule, also while no
-- window is open. When: "every 30m", "daily 09:00", "weekdays 08:30" or "once 2026-11-01 09:00" in the user's time zone.
CREATE TABLE IF NOT EXISTS ChatSchedules (
    Id           TEXT PRIMARY KEY,
    Username     TEXT NOT NULL,
    ChatId       TEXT NOT NULL,
    Prompt       TEXT NOT NULL,
    WhenText     TEXT NOT NULL,          -- normalized, e.g. "every 1h", "daily 09:00"
    TimeZone     TEXT NULL,              -- the user's time zone (IANA or Windows id) for daily/weekdays/once; NULL = UTC
    NextRunUtc   TEXT NOT NULL,
    LastRunUtc   TEXT NULL,
    LastStatus   TEXT NULL,              -- ok | the kind of failure | why it was skipped
    Runs         INTEGER NOT NULL DEFAULT 0,
    Enabled      INTEGER NOT NULL DEFAULT 1,
    CreatedUtc   TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ChatSchedules_Due ON ChatSchedules(Enabled, NextRunUtc);
CREATE INDEX IF NOT EXISTS IX_ChatSchedules_User ON ChatSchedules(Username, ChatId);
