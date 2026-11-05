-- Finer control over what each User account may do.
--   AllowedMcpServers: the MCP servers its chats (and the agents it creates) may use; NULL = every server.
--   DailyTokenLimit:   chat and API tokens per day (UTC); NULL or 0 = no limit.
--   RequestsPerMinute: chat messages and API calls per minute; NULL or 0 = no limit.
ALTER TABLE AdminUsers ADD COLUMN AllowedMcpServers TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN DailyTokenLimit INTEGER NULL;
ALTER TABLE AdminUsers ADD COLUMN RequestsPerMinute INTEGER NULL;

-- New permissions split out of "Chat & model API" (models.use) and "Chat tools & code" (chat.tools), and two that were
-- implicit: every User account keeps exactly what it could do before. Administrators have every permission anyway.
UPDATE AdminUsers SET Permissions = '[]' WHERE Role = 'User' AND (Permissions IS NULL OR NOT json_valid(Permissions));

UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'api.use')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'api.use');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.web')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.web');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.uploads')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.uploads');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.images')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.images');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.video')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.video');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.knowledge')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.knowledge');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.memory')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.memory');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.share')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.share');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.devices')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.use') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.devices');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'chat.code')
 WHERE Role = 'User' AND EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.tools') AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'chat.code');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'account.apikey')
 WHERE Role = 'User' AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'account.apikey');
UPDATE AdminUsers SET Permissions = json_insert(Permissions, '$[#]', 'models.own')
 WHERE Role = 'User' AND NOT EXISTS (SELECT 1 FROM json_each(Permissions) WHERE value = 'models.own');
