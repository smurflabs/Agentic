-- Agentic schema v9: chat context compaction. Older messages of a long conversation are summarized; the model then
-- gets the summary plus the messages after it, so the conversation fits the model's context window.
ALTER TABLE Chats ADD COLUMN Summary TEXT NULL;
ALTER TABLE Chats ADD COLUMN SummaryThroughUtc TEXT NULL;   -- messages created up to this time are in the summary
ALTER TABLE Chats ADD COLUMN SummaryTokens INTEGER NULL;
ALTER TABLE Chats ADD COLUMN CompactedUtc TEXT NULL;
ALTER TABLE Chats ADD COLUMN LastPromptTokens INTEGER NULL; -- what the model last received (reported or estimated)
