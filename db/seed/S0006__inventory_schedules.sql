-- Inventory routines (roadmap 3.2): once a day, across every tenant, release the reservations whose expiry date has
-- passed so the quantity is available again. Idempotent: the code is stable, cron and payload are refreshed on every run.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000007', NULL, 'inventory.reservations.expire', 'inventory.reservations.expire', '15 0 * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
