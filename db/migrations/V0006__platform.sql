-- V0006: platform services (ADR-0009, ADR-0010): transactional outbox, inbox, jobs, schedules, idempotency keys.
-- ops tables are platform-owned queues read across tenants by the worker; they carry tenant_id for scoping but
-- no RLS (the application role reads them under a system context). Every write to a business row and its
-- outbox message or job commits in one transaction; NOTIFY fires on commit and wakes the worker.

CREATE TABLE ops.outbox_messages (
  id               uuid PRIMARY KEY,
  seq              bigint GENERATED ALWAYS AS IDENTITY,
  tenant_id        uuid,
  occurred_at      timestamptz NOT NULL DEFAULT now(),
  event_type       text NOT NULL,
  event_version    int NOT NULL DEFAULT 1,
  aggregate_type   text NOT NULL,
  aggregate_id     uuid NOT NULL,
  payload          jsonb NOT NULL,
  correlation_id   text,
  causation_id     text,
  actor            text NOT NULL DEFAULT '',
  published_at     timestamptz,
  attempts         int NOT NULL DEFAULT 0,
  next_attempt_at  timestamptz NOT NULL DEFAULT now(),
  last_error       text,
  dead_at          timestamptz
);
CREATE INDEX outbox_messages_pending_idx ON ops.outbox_messages (seq) WHERE published_at IS NULL AND dead_at IS NULL;
CREATE INDEX outbox_messages_aggregate_idx ON ops.outbox_messages (aggregate_id, seq) WHERE published_at IS NULL;
CREATE INDEX outbox_messages_published_idx ON ops.outbox_messages (published_at) WHERE published_at IS NOT NULL;
CREATE INDEX outbox_messages_tenant_idx ON ops.outbox_messages (tenant_id, occurred_at DESC);

-- Exactly-once effect: a handler records the event it processed in the same transaction as its effect.
CREATE TABLE ops.inbox (
  handler     text NOT NULL,
  event_id    uuid NOT NULL,
  handled_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (handler, event_id)
);

CREATE TABLE ops.jobs (
  id               uuid PRIMARY KEY,
  tenant_id        uuid,
  type             text NOT NULL,
  payload          jsonb NOT NULL DEFAULT '{}'::jsonb,
  priority         int NOT NULL DEFAULT 0,
  state            text NOT NULL DEFAULT 'queued' CHECK (state IN ('queued', 'running', 'succeeded', 'failed', 'dead')),
  idempotency_key  text,
  run_after        timestamptz NOT NULL DEFAULT now(),
  attempts         int NOT NULL DEFAULT 0,
  max_attempts     int NOT NULL DEFAULT 5 CHECK (max_attempts >= 1),
  locked_by        text,
  locked_at        timestamptz,
  heartbeat_at     timestamptz,
  progress         jsonb,
  result           jsonb,
  error            text,
  schedule_id      uuid,
  correlation_id   text,
  actor            text NOT NULL DEFAULT '',
  created_at       timestamptz NOT NULL DEFAULT now(),
  started_at       timestamptz,
  finished_at      timestamptz
);
CREATE INDEX jobs_queue_idx ON ops.jobs (priority DESC, run_after) WHERE state = 'queued';
CREATE INDEX jobs_running_idx ON ops.jobs (heartbeat_at) WHERE state = 'running';
CREATE INDEX jobs_tenant_idx ON ops.jobs (tenant_id, created_at DESC);
CREATE UNIQUE INDEX jobs_idempotency_key ON ops.jobs (tenant_id, type, idempotency_key) NULLS NOT DISTINCT WHERE idempotency_key IS NOT NULL;

CREATE TABLE ops.schedules (
  id           uuid PRIMARY KEY,
  tenant_id    uuid,
  code         text NOT NULL,
  job_type     text NOT NULL,
  cron         text NOT NULL,
  time_zone    text NOT NULL DEFAULT 'UTC',
  payload      jsonb NOT NULL DEFAULT '{}'::jsonb,
  enabled      boolean NOT NULL DEFAULT true,
  next_run_at  timestamptz,
  last_run_at  timestamptz,
  last_job_id  uuid,
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  UNIQUE NULLS NOT DISTINCT (tenant_id, code)
);
CREATE INDEX schedules_due_idx ON ops.schedules (next_run_at) WHERE enabled;
CALL app.track_updated_at('ops.schedules');

CREATE TABLE ops.idempotency_keys (
  tenant_id        uuid NOT NULL,
  principal        text NOT NULL,
  key              text NOT NULL,
  request_hash     bytea NOT NULL,
  response_status  int,
  response_body    jsonb,
  response_content_type text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  expires_at       timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, principal, key)
);
CREATE INDEX idempotency_keys_expiry_idx ON ops.idempotency_keys (expires_at);

-- Wake the worker as soon as the producing transaction commits (NOTIFY is delivered on commit only).
CREATE OR REPLACE FUNCTION ops.notify_outbox() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM pg_notify('quicker_outbox', '');
  RETURN NULL;
END
$$;
CREATE TRIGGER outbox_notify AFTER INSERT ON ops.outbox_messages FOR EACH STATEMENT EXECUTE FUNCTION ops.notify_outbox();

CREATE OR REPLACE FUNCTION ops.notify_jobs() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM pg_notify('quicker_jobs', '');
  RETURN NULL;
END
$$;
CREATE TRIGGER jobs_notify AFTER INSERT OR UPDATE OF state, run_after ON ops.jobs FOR EACH STATEMENT EXECUTE FUNCTION ops.notify_jobs();
