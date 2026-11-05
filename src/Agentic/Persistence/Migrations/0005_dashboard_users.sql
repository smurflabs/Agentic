-- Agentic schema v5: dashboard user accounts with roles and permissions.
-- Admin: full control, including user management. User: only the permissions and agents the admin assigned.

ALTER TABLE AdminUsers ADD COLUMN Role TEXT NOT NULL DEFAULT 'Admin';            -- Admin | User
ALTER TABLE AdminUsers ADD COLUMN Permissions TEXT NULL;                         -- JSON array of permission keys (User role)
ALTER TABLE AdminUsers ADD COLUMN AllowedAgents TEXT NULL;                       -- JSON array of agent ids; NULL or [] = all agents
ALTER TABLE AdminUsers ADD COLUMN DisplayName TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN Disabled INTEGER NOT NULL DEFAULT 0;
ALTER TABLE AdminUsers ADD COLUMN PasswordChangedUtc TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN UpdatedBy TEXT NULL;
ALTER TABLE AdminUsers ADD COLUMN UpdatedUtc TEXT NULL;
-- For users defined in appsettings.json: the config hash last applied. A password changed in the dashboard is kept
-- across restarts until the hash in the config file itself changes.
ALTER TABLE AdminUsers ADD COLUMN ConfigPasswordHash TEXT NULL;
UPDATE AdminUsers SET ConfigPasswordHash = PasswordHash WHERE Source = 'Config';
