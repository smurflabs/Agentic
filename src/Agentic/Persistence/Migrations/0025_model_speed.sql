-- How fast each model answers, for the model picker: one row per model call (a chat round, an API request, an agent's
-- call). Request sent -> first token streamed back -> last token, and the tokens written. Kept 7 days.
CREATE TABLE IF NOT EXISTS ModelSpeed (
    Id            INTEGER PRIMARY KEY AUTOINCREMENT,
    Model         TEXT NOT NULL,          -- the model that answered
    Via           TEXT NULL,              -- the model asked for when it differs (a custom router model)
    Source        TEXT NOT NULL,          -- chat | api | agents
    FirstTokenMs  INTEGER NULL,           -- NULL: not streamed
    DurationMs    INTEGER NOT NULL,
    OutputTokens  INTEGER NOT NULL,
    CreatedUtc    TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ModelSpeed_Created ON ModelSpeed(CreatedUtc);
