-- The Stable Diffusion server makes one job at a time, so jobs wait in line here and are sent when the one before them
-- ends: a job's own id is no longer the server's (RemoteId, set when it is sent; SubmittedUtc starts its time limit).
-- A job still waiting keeps its full request in data\images\queue until it is sent.
ALTER TABLE SdJobs ADD COLUMN RemoteId TEXT NULL;
ALTER TABLE SdJobs ADD COLUMN SubmittedUtc TEXT NULL;
