-- RAG (Retrieval-Augmented Generation): named knowledge bases the user builds from uploaded files. Each document's
-- extracted text is split into chunks and embedded with the provider's embedding model; at chat time the chunks most
-- like the question are retrieved and given to the model as cited context, so large corpora can be read without
-- stuffing everything into the prompt.

CREATE TABLE IF NOT EXISTS KnowledgeBases (
    Id              TEXT PRIMARY KEY,
    Username        TEXT NOT NULL,
    Name            TEXT NOT NULL,
    Description     TEXT NULL,
    EmbeddingModel  TEXT NULL,              -- the model its chunks were embedded with (one base = one model)
    Dimensions      INTEGER NOT NULL DEFAULT 0,
    CreatedUtc      TEXT NOT NULL,
    UpdatedUtc      TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_KnowledgeBases_User ON KnowledgeBases(Username, UpdatedUtc DESC);

CREATE TABLE IF NOT EXISTS KnowledgeDocs (
    Id              TEXT PRIMARY KEY,
    BaseId          TEXT NOT NULL,
    UploadId        TEXT NULL,              -- the source upload (may be deleted later; chunks remain)
    Name            TEXT NOT NULL,
    Sha256          TEXT NULL,
    Chunks          INTEGER NOT NULL DEFAULT 0,
    Chars           INTEGER NOT NULL DEFAULT 0,
    Status          TEXT NOT NULL DEFAULT 'ready',   -- ready | failed
    Error           TEXT NULL,
    CreatedUtc      TEXT NOT NULL,
    FOREIGN KEY (BaseId) REFERENCES KnowledgeBases(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_KnowledgeDocs_Base ON KnowledgeDocs(BaseId, CreatedUtc);

CREATE TABLE IF NOT EXISTS KnowledgeChunks (
    Id              TEXT PRIMARY KEY,
    BaseId          TEXT NOT NULL,
    DocId           TEXT NOT NULL,
    Ordinal         INTEGER NOT NULL,       -- position of the chunk within its document
    Text            TEXT NOT NULL,
    Embedding       BLOB NOT NULL,          -- the unit-normalised vector, little-endian float32
    FOREIGN KEY (BaseId) REFERENCES KnowledgeBases(Id) ON DELETE CASCADE,
    FOREIGN KEY (DocId) REFERENCES KnowledgeDocs(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_Base ON KnowledgeChunks(BaseId);
CREATE INDEX IF NOT EXISTS IX_KnowledgeChunks_Doc ON KnowledgeChunks(DocId);
