-- Workflow SLA timers (ADR-0020, roadmap 4.0): every hour, across every tenant, steps past their due time escalate to
-- their escalation target (or remind their approvers once) and the request history records it. Idempotent.
INSERT INTO ops.schedules (id, tenant_id, code, job_type, cron, time_zone, payload, enabled) VALUES
  ('0199a000-0000-7000-8000-00000000000a', NULL, 'workflow.escalation', 'workflow.escalate', '15 * * * *', 'UTC', '{}'::jsonb, true)
ON CONFLICT (tenant_id, code) DO UPDATE SET
  job_type = EXCLUDED.job_type,
  cron = EXCLUDED.cron,
  time_zone = EXCLUDED.time_zone,
  payload = EXCLUDED.payload;
