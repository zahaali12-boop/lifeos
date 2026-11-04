-- V0008: collaboration, part one: in-app notifications with per-member channel preferences, the email log written
-- by the job 'collaboration.email.send', and attachments (metadata here, bytes in the object store under
-- tenants/<tenant>/attachments/<id>). Comments, activities, document links, saved views and custom fields follow.

CREATE TABLE app.col_notifications (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  membership_id        uuid NOT NULL,
  kind                 text NOT NULL,
  title_i18n           jsonb NOT NULL DEFAULT '{}'::jsonb,
  body_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  link                 text,
  entity_type          text,
  entity_id            uuid,
  data                 jsonb NOT NULL DEFAULT '{}'::jsonb,
  actor_membership_id  uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  read_at              timestamptz,
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX col_notifications_inbox_idx ON app.col_notifications (tenant_id, membership_id, created_at DESC);
CREATE INDEX col_notifications_unread_idx ON app.col_notifications (tenant_id, membership_id) WHERE read_at IS NULL;
CALL app.enable_tenant_rls('app.col_notifications');

CREATE TABLE app.col_notification_preferences (
  tenant_id      uuid NOT NULL REFERENCES control.tenants (id),
  id             uuid NOT NULL,
  membership_id  uuid NOT NULL,
  kind           text NOT NULL DEFAULT '*',
  in_app         boolean NOT NULL DEFAULT true,
  email          boolean NOT NULL DEFAULT true,
  updated_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, membership_id, kind)
);
CALL app.enable_tenant_rls('app.col_notification_preferences');
CALL app.track_updated_at('app.col_notification_preferences');

CREATE TABLE app.col_email_log (
  tenant_id        uuid NOT NULL REFERENCES control.tenants (id),
  id               uuid NOT NULL,
  notification_id  uuid,
  membership_id    uuid,
  to_address       text NOT NULL,
  subject          text NOT NULL,
  text_body        text NOT NULL,
  status           text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'sent', 'failed')),
  attempts         int NOT NULL DEFAULT 0,
  last_error       text,
  job_id           uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  sent_at          timestamptz,
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX col_email_log_created_idx ON app.col_email_log (tenant_id, created_at DESC);
CALL app.enable_tenant_rls('app.col_email_log');

CREATE TABLE app.col_attachments (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  id            uuid NOT NULL,
  entity_type   text NOT NULL,
  entity_id     uuid NOT NULL,
  file_name     text NOT NULL,
  content_type  text NOT NULL,
  size_bytes    bigint NOT NULL CHECK (size_bytes >= 0),
  sha256        text NOT NULL,
  storage_key   text NOT NULL,
  uploaded_by   uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX col_attachments_entity_idx ON app.col_attachments (tenant_id, entity_type, entity_id, created_at);
CALL app.enable_tenant_rls('app.col_attachments');
