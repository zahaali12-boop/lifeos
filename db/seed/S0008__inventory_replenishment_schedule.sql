-- Platform schedule: the replenishment planner runs every day at 01:00 UTC for every active tenant (roadmap 3.7).
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000009', NULL, 'inventory.replenishment.plan', 'inventory.replenishment.plan', '0 1 * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
