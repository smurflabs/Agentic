-- What each cloud model's provider really accepts, learned from agents' and chat users' calls (AI/ContextLimits.vb):
--   MaxAccepted:  the largest prompt the provider counted and answered (tokens) - the window is at least this.
--   MinRejected:  the smallest prompt refused as too long (estimated tokens) when the refusal named no limit; 0 = none.
--   Reported:     the limit the provider itself named in a refusal ("maximum context length is 65536 tokens"); 0 = none.
--   Accepted/Rejected: how many calls taught it so far.
CREATE TABLE IF NOT EXISTS ModelContextLimits (
    ModelId     TEXT PRIMARY KEY COLLATE NOCASE,
    MaxAccepted INTEGER NOT NULL DEFAULT 0,
    MinRejected INTEGER NOT NULL DEFAULT 0,
    Reported    INTEGER NOT NULL DEFAULT 0,
    Accepted    INTEGER NOT NULL DEFAULT 0,
    Rejected    INTEGER NOT NULL DEFAULT 0,
    LastError   TEXT,
    Source      TEXT,
    UpdatedUtc  TEXT NOT NULL
);
