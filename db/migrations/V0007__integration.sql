-- V0007: integration (ADR-0012 webhooks): subscriptions per tenant and the delivery log.
-- A delivery is created by the outbox fan-out for every subscription whose event types match, then delivered by
-- the job 'integration.webhook.deliver' with an HMAC-SHA256 signature; failed attempts retry with backoff for a day.

CREATE TABLE app.int_webhook_subscriptions (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  id            uuid NOT NULL,
  name          text NOT NULL,
  url           text NOT NULL,
  secret_enc    text NOT NULL,
  event_types   jsonb NOT NULL DEFAULT '["*"]'::jsonb,
  filters       jsonb NOT NULL DEFAULT '{}'::jsonb,
  active        boolean NOT NULL DEFAULT true,
  created_by    uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, name)
);
CALL app.enable_tenant_rls('app.int_webhook_subscriptions');
CALL app.track_updated_at('app.int_webhook_subscriptions');

CREATE TABLE app.int_webhook_deliveries (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  subscription_id   uuid NOT NULL,
  event_id          uuid NOT NULL,
  event_type        text NOT NULL,
  payload           jsonb NOT NULL,
  attempt           int NOT NULL DEFAULT 0,
  status            text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'delivered', 'failed', 'dead')),
  response_status   int,
  response_excerpt  text,
  last_error        text,
  job_id            uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  attempted_at      timestamptz,
  next_attempt_at   timestamptz,
  delivered_at      timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, subscription_id) REFERENCES app.int_webhook_subscriptions (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX int_webhook_deliveries_subscription_idx ON app.int_webhook_deliveries (tenant_id, subscription_id, created_at DESC);
CREATE INDEX int_webhook_deliveries_event_idx ON app.int_webhook_deliveries (tenant_id, event_id);
CALL app.enable_tenant_rls('app.int_webhook_deliveries');
