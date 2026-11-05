-- Work a global task already got done in an earlier attempt (revision) that the reviewer sent back: the results of its
-- finished steps (and what unfinished steps got done). The steps table is replaced when the task is planned again, so
-- without this the next revision, its final answer and the reviewer never see that work (Markdown, newest last).
ALTER TABLE GlobalTasks ADD COLUMN PriorResults TEXT NULL;
