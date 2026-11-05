-- Speech (the XTTS text-to-speech server): each user's chosen voice and Dictation mode settings, a personality per voice
-- (the prompt for the short spoken summary Dictation mode says after each answer, the model that writes it, and the
-- voice's speech settings), the Audio tab's clips, the clips a cloned voice was made from (so it can be renewed before
-- the speech server deletes it, 30 days after upload), the spoken summary of a chat answer, and what speech costs
-- (set by administrators, kept with its history; the latest row wins).
CREATE TABLE IF NOT EXISTS AudioPrefs (
    Username TEXT PRIMARY KEY,
    PrefsJson TEXT NOT NULL,
    UpdatedUtc TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS AudioVoiceProfiles (
    Username TEXT NOT NULL,
    Voice TEXT NOT NULL,
    SummaryPrompt TEXT NULL,
    SummaryModel TEXT NULL,
    SettingsJson TEXT NULL,
    UpdatedUtc TEXT NOT NULL,
    PRIMARY KEY (Username, Voice)
);

CREATE TABLE IF NOT EXISTS AudioClips (
    Id TEXT PRIMARY KEY,
    Username TEXT NOT NULL,
    Text TEXT NOT NULL,
    Voice TEXT NOT NULL,
    SettingsJson TEXT NULL,
    Mode TEXT NOT NULL,
    Bytes INTEGER NOT NULL,
    DurationMs INTEGER NOT NULL,
    GenerateMs INTEGER NOT NULL,
    FirstAudioMs INTEGER NULL,
    CostMicros INTEGER NULL,
    CreatedUtc TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS IX_AudioClips_User ON AudioClips(Username, CreatedUtc);

CREATE TABLE IF NOT EXISTS AudioPricing (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    PerMillionChars TEXT NULL,
    PerMinute TEXT NULL,
    PerVoiceUpload TEXT NULL,
    EffectiveFromUtc TEXT NOT NULL,
    SetBy TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS AudioVoiceSources (
    Name TEXT PRIMARY KEY,
    CreatedBy TEXT NOT NULL,
    ClipsJson TEXT NOT NULL,
    SettingsJson TEXT NOT NULL,
    CreatedUtc TEXT NOT NULL,
    UploadedUtc TEXT NOT NULL
);

ALTER TABLE ChatMessages ADD COLUMN SpokenSummary TEXT NULL;
ALTER TABLE ChatMessages ADD COLUMN SpokenVoice TEXT NULL;
