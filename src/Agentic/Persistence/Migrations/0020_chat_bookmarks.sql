-- Chat bookmarks: chats the user bookmarked; the chat list can show only these.
ALTER TABLE Chats ADD COLUMN Bookmarked INTEGER NOT NULL DEFAULT 0;
CREATE INDEX IF NOT EXISTS IX_Chats_Bookmarked ON Chats(Username, Bookmarked);
