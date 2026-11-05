-- My models (bring your own API): models a user adds with their own key for OpenAI (or an OpenAI-compatible API),
-- Anthropic or Amazon Bedrock. Only the owner sees and uses them (in Chat, the API, the desktop app, the CLI and VS Code),
-- and agents the owner owns when the model is in their AllowedModels. They are free: the user pays the provider.
-- SecretJson holds the key(s), encrypted (AES-GCM, key in data/secrets.key); it never leaves the server.
CREATE TABLE IF NOT EXISTS UserModels (
    Id              TEXT PRIMARY KEY,           -- byo-xxxxxxxxxxxx: the model id in Chat and agents
    Owner           TEXT NOT NULL,
    Name            TEXT NOT NULL,
    Provider        TEXT NOT NULL,              -- openai | anthropic | bedrock
    ModelName       TEXT NOT NULL,              -- the provider's own model id
    BaseUrl         TEXT,                       -- openai / anthropic: another endpoint (OpenAI-compatible APIs, a proxy)
    Region          TEXT,                       -- bedrock: us-east-1 …
    SecretJson      TEXT NOT NULL,
    KeyHint         TEXT,                       -- the key's last characters, to recognise it
    ContextWindow   INTEGER NOT NULL DEFAULT 128000,
    MaxTokens       INTEGER NOT NULL DEFAULT 8192,
    Vision          INTEGER NOT NULL DEFAULT 0,
    Enabled         INTEGER NOT NULL DEFAULT 1,
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT
);
CREATE INDEX IF NOT EXISTS IX_UserModels_Owner ON UserModels(Owner);
