-- Model control (Administration): models an administrator turned off for everyone. A turned-off model is not offered in
-- Chat or the API, custom models do not route to it, and agents, images and videos do not use it.
CREATE TABLE IF NOT EXISTS DisabledModels (
    ModelId         TEXT PRIMARY KEY COLLATE NOCASE,
    DisabledBy      TEXT,
    DisabledUtc     TEXT NOT NULL
);
