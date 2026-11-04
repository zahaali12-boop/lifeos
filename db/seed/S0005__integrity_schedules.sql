-- Invariant harness (ADR-0029, roadmap 2.6): once a day, across every tenant, prove the books balance, the derived
-- balances equal the lines, the audit chain is intact, tenant isolation holds and gapless series have no gaps.
-- The job fails when any tenant fails a check. Idempotent: the code is stable, cron and payload are refreshed.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000006', NULL, 'integrity.daily', 'integrity.check_all', '30 3 * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
