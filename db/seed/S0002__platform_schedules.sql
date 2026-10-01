-- Platform schedules (ADR-0010, ADR-0015): the daily audit anchoring and verification runs across every tenant.
-- Idempotent: codes are stable, cron and payload are refreshed on every run; next_run_at is left to the scheduler.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000001', NULL, 'audit.anchor_daily', 'audit.anchor_all', '0 2 * * *', 'UTC', '{}'::jsonb, true),
  ('0199a000-0000-7000-8000-000000000002', NULL, 'audit.verify_daily', 'audit.verify_all', '0 3 * * *', 'UTC', '{}'::jsonb, true),
  ('0199a000-0000-7000-8000-000000000003', NULL, 'ops.outbox_archive', 'ops.outbox_archive', '30 1 * * *', 'UTC', '{"olderThanDays": 30}'::jsonb, true),
  ('0199a000-0000-7000-8000-000000000004', NULL, 'ops.idempotency_sweep', 'ops.idempotency_sweep', '15 * * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
