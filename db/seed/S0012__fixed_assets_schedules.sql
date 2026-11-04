-- Fixed assets (roadmap 6.6, A-175): on the first of every month, depreciate every company's ledger book through the
-- month just ended, across every tenant. Idempotent: the code is stable, cron and payload are refreshed on every run.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000012', NULL, 'fixed_assets.monthly', 'fixed_assets.monthly_depreciation', '0 2 1 * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
