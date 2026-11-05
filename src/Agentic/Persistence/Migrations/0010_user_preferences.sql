-- Agentic schema v10: per-account preferences kept on the server, so every client (web app, desktop app, CLI) resumes
-- with the same choices, e.g. chat.lastModel: the model the user last chatted with.
CREATE TABLE IF NOT EXISTS UserPreferences (
    Username   TEXT NOT NULL,
    Key        TEXT NOT NULL,
    Value      TEXT NULL,
    UpdatedUtc TEXT NOT NULL,
    PRIMARY KEY (Username, Key)
);
