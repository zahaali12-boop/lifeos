-- Attachment files nothing refers to (a save that failed after its upload): once a day, across every workspace, files
-- older than a day under a workspace's attachments with no attachment record are removed. Idempotent.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-00000000000b', NULL, 'collaboration.attachment_sweep', 'collaboration.attachments.sweep', '40 2 * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
