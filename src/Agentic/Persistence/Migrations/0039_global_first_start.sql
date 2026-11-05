-- When a global task first started. A revision (the reviewer sent the work back) starts the steps again, and its deadline
-- used to start over too, so a 10-hour task with two revisions could run 30 hours. A revision now gets what is left of
-- the first deadline (at least a quarter of the time).
ALTER TABLE GlobalTasks ADD COLUMN FirstStartedUtc TEXT NULL;
UPDATE GlobalTasks SET FirstStartedUtc = StartedUtc WHERE FirstStartedUtc IS NULL AND StartedUtc IS NOT NULL;
