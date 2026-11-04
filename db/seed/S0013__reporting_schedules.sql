-- Reporting projections (ADR-0034): every two minutes, across every tenant, project what was posted since each
-- fact's watermark; a posting also kicks the projection of its tenant through the outbox, and a query refreshes facts
-- older than a minute before it runs. Idempotent: the code is stable, cron and payload are refreshed on every run.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000013', NULL, 'reporting.project', 'reporting.project', '*/2 * * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
