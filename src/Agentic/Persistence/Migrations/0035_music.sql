-- Songs from Suno (the Music tab): requests (each makes two songs, watched until they end, also after a restart), the
-- songs kept (data\music: the audio and its cover, with title, style, lyrics), and what music costs (set by
-- administrators, kept with its history; the latest row wins).
CREATE TABLE IF NOT EXISTS MusicJobs (
    Id TEXT PRIMARY KEY,
    Username TEXT NOT NULL,
    Status TEXT NOT NULL,
    BatchId TEXT NULL,
    RequestJson TEXT NOT NULL,
    ClipIdsJson TEXT NOT NULL,
    SongIdsJson TEXT NULL,
    Error TEXT NULL,
    CostMicros INTEGER NULL,
    CreatedUtc TEXT NOT NULL,
    FinishedUtc TEXT NULL
);
CREATE INDEX IF NOT EXISTS IX_MusicJobs_User ON MusicJobs(Username, CreatedUtc);
CREATE INDEX IF NOT EXISTS IX_MusicJobs_Status ON MusicJobs(Status);

CREATE TABLE IF NOT EXISTS MusicSongs (
    Id TEXT PRIMARY KEY,
    Username TEXT NOT NULL,
    JobId TEXT NOT NULL,
    ClipId TEXT NOT NULL,
    Title TEXT NULL,
    Tags TEXT NULL,
    Lyrics TEXT NULL,
    Description TEXT NULL,
    Instrumental INTEGER NOT NULL DEFAULT 0,
    Model TEXT NULL,
    DurationMs INTEGER NOT NULL,
    Mime TEXT NOT NULL,
    Bytes INTEGER NOT NULL,
    HasCover INTEGER NOT NULL DEFAULT 0,
    Starred INTEGER NOT NULL DEFAULT 0,
    CreatedUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_MusicSongs_User ON MusicSongs(Username, CreatedUtc);

CREATE TABLE IF NOT EXISTS MusicPricing (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PerSong TEXT NULL,
    PerLyrics TEXT NULL,
    EffectiveFromUtc TEXT NOT NULL,
    SetBy TEXT NOT NULL
);
