-- Platform schedule: lots past their expiry date are marked expired every day at 00:20 UTC across tenants (roadmap 3.5).
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-000000000008', NULL, 'inventory.lots.expire', 'inventory.lots.expire', '20 0 * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
