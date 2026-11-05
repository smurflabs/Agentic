-- Pictures from the Stable Diffusion 1.5 server (the Images tab): jobs (watched until they end, also after a restart),
-- the pictures they made (data\images, with their settings and the PNG's "parameters" text), and what pictures cost
-- (set by administrators, kept with its history; the latest row wins).
CREATE TABLE IF NOT EXISTS SdJobs (
    Id TEXT PRIMARY KEY,
    Username TEXT NOT NULL,
    Status TEXT NOT NULL,
    Prompt TEXT NOT NULL,
    RequestJson TEXT NOT NULL,
    NumImages INTEGER NOT NULL,
    ImageIdsJson TEXT NULL,
    Error TEXT NULL,
    ElapsedMs INTEGER NULL,
    CostMicros INTEGER NULL,
    CreatedUtc TEXT NOT NULL,
    FinishedUtc TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_SdJobs_User ON SdJobs(Username, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_SdJobs_Status ON SdJobs(Status);

CREATE TABLE IF NOT EXISTS SdImages (
    Id TEXT PRIMARY KEY,
    Username TEXT NOT NULL,
    JobId TEXT NOT NULL,
    Idx INTEGER NOT NULL,
    Prompt TEXT NOT NULL,
    NegativePrompt TEXT NULL,
    ParamsJson TEXT NOT NULL,
    Info TEXT NULL,
    Seed INTEGER NULL,
    Width INTEGER NOT NULL,
    Height INTEGER NOT NULL,
    Model TEXT NULL,
    Mode TEXT NOT NULL,
    Bytes INTEGER NOT NULL,
    Starred INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_SdImages_User ON SdImages(Username, CreatedUtc);

CREATE TABLE IF NOT EXISTS SdPricing (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PerImage TEXT NULL,
    PerGpuMinute TEXT NULL,
    EffectiveFromUtc TEXT NOT NULL,
    SetBy TEXT NOT NULL
);
