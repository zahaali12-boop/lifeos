-- Accounting routines (ADR-0010, roadmap 2.3): once a day, across every tenant, reverse the entries whose reversal
-- date has come, generate the recurring journals that are due and post the deferral lines that are due.
-- Idempotent: the code is stable, cron and payload are refreshed on every run.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000005', NULL, 'accounting.daily', 'accounting.daily_routines', '30 0 * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
