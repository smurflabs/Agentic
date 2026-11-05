-- Run options a person sets when creating a task or a global task:
--   AutoApproveTools: tool calls that need approval are approved at once for this task and the work it hands on (same
--                     CorrelationId), so the agents never wait in the Approvals inbox.
--   DeadlineEnforced: DeadlineUtc is a real deadline (not only a queue hint): when it passes, the task is cancelled ...
--   RunPastDeadline:  ... unless this is set: then it is marked as past its deadline and keeps working.
--   DeadlinePassedUtc: when the deadline was handled (cancelled or marked), so it happens once.
ALTER TABLE AgentTasks ADD COLUMN AutoApproveTools INTEGER NOT NULL DEFAULT 0;
ALTER TABLE AgentTasks ADD COLUMN DeadlineEnforced INTEGER NOT NULL DEFAULT 0;
ALTER TABLE AgentTasks ADD COLUMN RunPastDeadline INTEGER NOT NULL DEFAULT 0;
ALTER TABLE AgentTasks ADD COLUMN DeadlinePassedUtc TEXT NULL;
CREATE INDEX IF NOT EXISTS IX_AgentTasks_AutoApprove ON AgentTasks(CorrelationId) WHERE AutoApproveTools = 1;
CREATE INDEX IF NOT EXISTS IX_AgentTasks_DeadlineDue ON AgentTasks(DeadlineUtc) WHERE DeadlineEnforced = 1 AND DeadlinePassedUtc IS NULL;
-- A global task past its deadline that keeps working (RunPastDeadline in its options) is marked once.
ALTER TABLE GlobalTasks ADD COLUMN DeadlinePassedUtc TEXT NULL;
