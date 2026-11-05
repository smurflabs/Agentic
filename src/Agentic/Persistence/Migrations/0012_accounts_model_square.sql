-- Account balances (what a user's chats, API calls and agents are paid from), per-account model access, agent owners,
-- and Model Square: the price of every model for the people who use it.
ALTER TABLE AdminUsers ADD COLUMN BalanceMicros INTEGER NOT NULL DEFAULT 0;   -- the account's balance in micro-USD; starts at 0
ALTER TABLE AdminUsers ADD COLUMN AllowedModels TEXT NULL;                    -- JSON array of model ids the account may use; NULL = every model
ALTER TABLE Agents ADD COLUMN Owner TEXT NULL;                                -- the account that pays for the agent; NULL = the administrators
ALTER TABLE ChatMessages ADD COLUMN CostMicros INTEGER NULL;                  -- what the answer cost its account

CREATE TABLE IF NOT EXISTS AccountLedger (
    Id                  INTEGER PRIMARY KEY AUTOINCREMENT,
    Username            TEXT NOT NULL COLLATE NOCASE,
    EntryType           TEXT NOT NULL,          -- Credit (an administrator added or removed money) | Chat | Api | Agent
    AmountMicros        INTEGER NOT NULL,       -- + adds to the balance, - takes from it
    BalanceAfterMicros  INTEGER NOT NULL,
    Model               TEXT NULL,
    PromptTokens        INTEGER NULL,
    CompletionTokens    INTEGER NULL,
    AgentId             TEXT NULL,
    Description         TEXT NULL,
    CreatedBy           TEXT NOT NULL,
    CreatedUtc          TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AccountLedger_User ON AccountLedger(Username, Id);
CREATE INDEX IF NOT EXISTS IX_AccountLedger_Created ON AccountLedger(CreatedUtc);

-- USD per 1M tokens, per model id (a provider model, or a custom model with its own price). No row = free.
CREATE TABLE IF NOT EXISTS ModelSquarePrices (
    ModelId             TEXT PRIMARY KEY COLLATE NOCASE,
    InputPerMillion     TEXT NOT NULL,
    OutputPerMillion    TEXT NOT NULL,
    UpdatedBy           TEXT NOT NULL,
    UpdatedUtc          TEXT NOT NULL
);
