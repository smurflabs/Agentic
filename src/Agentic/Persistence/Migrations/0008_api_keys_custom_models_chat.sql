-- Agentic schema v8: personal API keys for dashboard accounts, custom (routing) models, the Chat tab and the
-- OpenAI-compatible API's usage log.

-- One API key per account; only its SHA-256 is stored. The key acts with the account's role and permissions.
ALTER TABLE AdminUsers ADD COLUMN ApiKeyHash TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN ApiKeyPrefix TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN ApiKeyCreatedUtc TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN ApiKeyLastUsedUtc TEXT NULL;
CREATE UNIQUE INDEX IF NOT EXISTS UX_AdminUsers_ApiKey ON AdminUsers(ApiKeyHash) WHERE ApiKeyHash IS NOT NULL;

-- Custom models: one model name that routes each request to the right underlying model by what it asks for.
CREATE TABLE IF NOT EXISTS CustomRouterModels (
    Id              TEXT PRIMARY KEY,       -- the model id clients use ("agentic/assistant")
    Name            TEXT NOT NULL,
    Description     TEXT NULL,
    DefinitionJson  TEXT NOT NULL,          -- routes, signals, classifier, defaults
    Enabled         INTEGER NOT NULL DEFAULT 1,
    CreatedBy       TEXT NOT NULL,
    CreatedUtc      TEXT NOT NULL,
    UpdatedBy       TEXT NULL,
    UpdatedUtc      TEXT NULL
);

-- Chat tab conversations (private to their owner).
CREATE TABLE IF NOT EXISTS Chats (
    Id              TEXT PRIMARY KEY,
    Username        TEXT NOT NULL,
    Title           TEXT NOT NULL,
    Model           TEXT NULL,
    SettingsJson    TEXT NULL,              -- tools, web search, MCP servers, system prompt
    Pinned          INTEGER NOT NULL DEFAULT 0,
    Archived        INTEGER NOT NULL DEFAULT 0,
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_Chats_User ON Chats(Username, Archived, UpdatedUtc);

CREATE TABLE IF NOT EXISTS ChatMessages (
    Id              TEXT PRIMARY KEY,
    ChatId          TEXT NOT NULL,
    Role            TEXT NOT NULL,          -- user | assistant
    Content         TEXT NOT NULL,
    Model           TEXT NULL,              -- model the user picked
    RoutedModel     TEXT NULL,              -- the model that answered (custom models route)
    RouteInfo       TEXT NULL,              -- category and reason
    AttachmentsJson TEXT NULL,
    ToolCallsJson   TEXT NULL,
    SourcesJson     TEXT NULL,
    ReasoningText   TEXT NULL,
    PromptTokens    INTEGER NOT NULL DEFAULT 0,
    CompletionTokens INTEGER NOT NULL DEFAULT 0,
    DurationMs      INTEGER NOT NULL DEFAULT 0,
    Error           TEXT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ChatMessages_Chat ON ChatMessages(ChatId, CreatedUtc);

-- Calls through the OpenAI-compatible API and the Chat tab.
CREATE TABLE IF NOT EXISTS ModelApiUsage (
    Id              INTEGER PRIMARY KEY AUTOINCREMENT,
    Username        TEXT NOT NULL,
    Source          TEXT NOT NULL,          -- api | chat
    Endpoint        TEXT NOT NULL,
    Model           TEXT NOT NULL,
    RoutedModel     TEXT NULL,
    Category        TEXT NULL,
    Stream          INTEGER NOT NULL DEFAULT 0,
    Status          INTEGER NOT NULL,
    PromptTokens    INTEGER NOT NULL DEFAULT 0,
    CompletionTokens INTEGER NOT NULL DEFAULT 0,
    DurationMs      INTEGER NOT NULL DEFAULT 0,
    Error           TEXT NULL,
    CreatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_ModelApiUsage_Created ON ModelApiUsage(CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_ModelApiUsage_User ON ModelApiUsage(Username, CreatedUtc);
