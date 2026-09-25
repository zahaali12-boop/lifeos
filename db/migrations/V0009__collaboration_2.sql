-- V0009: collaboration, part two: comments with mentions, the activity timeline, links between documents, saved
-- views and custom-field definitions. Custom-field values live in the host table's jsonb column (org_companies
-- .custom_fields first); the definitions here drive validation and, when indexed, an expression index on the host.

CREATE TABLE app.col_comments (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  entity_type           text NOT NULL,
  entity_id             uuid NOT NULL,
  author_membership_id  uuid NOT NULL,
  body                  text NOT NULL,
  mentions              jsonb NOT NULL DEFAULT '[]'::jsonb,
  parent_id             uuid,
  edited_at             timestamptz,
  deleted_at            timestamptz,
  created_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, parent_id) REFERENCES app.col_comments (tenant_id, id)
);
CREATE INDEX col_comments_entity_idx ON app.col_comments (tenant_id, entity_type, entity_id, created_at);
CALL app.enable_tenant_rls('app.col_comments');

CREATE TABLE app.col_activities (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  entity_type          text NOT NULL,
  entity_id            uuid NOT NULL,
  kind                 text NOT NULL,
  actor_membership_id  uuid,
  summary_i18n         jsonb NOT NULL DEFAULT '{}'::jsonb,
  data                 jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX col_activities_entity_idx ON app.col_activities (tenant_id, entity_type, entity_id, created_at DESC);
CALL app.enable_tenant_rls('app.col_activities');

CREATE TABLE app.col_document_links (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  from_type   text NOT NULL,
  from_id     uuid NOT NULL,
  to_type     text NOT NULL,
  to_id       uuid NOT NULL,
  relation    text NOT NULL,
  created_by  uuid,
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, from_type, from_id, to_type, to_id, relation),
  CHECK (NOT (from_type = to_type AND from_id = to_id))
);
CREATE INDEX col_document_links_to_idx ON app.col_document_links (tenant_id, to_type, to_id);
CALL app.enable_tenant_rls('app.col_document_links');

CREATE TABLE app.col_saved_views (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  entity_type          text NOT NULL,
  name                 text NOT NULL,
  owner_membership_id  uuid NOT NULL,
  shared               boolean NOT NULL DEFAULT false,
  is_default           boolean NOT NULL DEFAULT false,
  definition           jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, entity_type, owner_membership_id, name)
);
CREATE INDEX col_saved_views_entity_idx ON app.col_saved_views (tenant_id, entity_type);
CALL app.enable_tenant_rls('app.col_saved_views');
CALL app.track_updated_at('app.col_saved_views');

CREATE TABLE app.col_custom_fields (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  id                uuid NOT NULL,
  entity_type       text NOT NULL,
  key               text NOT NULL CHECK (key ~ '^[a-z][a-z0-9_]{0,39}$'),
  label_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  type              text NOT NULL CHECK (type IN ('text', 'number', 'date', 'boolean', 'select', 'multi_select', 'reference')),
  required          boolean NOT NULL DEFAULT false,
  options           jsonb NOT NULL DEFAULT '[]'::jsonb,
  rules             jsonb NOT NULL DEFAULT '{}'::jsonb,
  indexed           boolean NOT NULL DEFAULT false,
  position          int NOT NULL DEFAULT 0,
  active            boolean NOT NULL DEFAULT true,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, entity_type, key)
);
CALL app.enable_tenant_rls('app.col_custom_fields');
CALL app.track_updated_at('app.col_custom_fields');
