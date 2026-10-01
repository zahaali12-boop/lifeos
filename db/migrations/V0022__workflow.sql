-- M4 slice 4.0: the workflow and approval engine (ADR-0020). Definitions carry ordered rules (a safe expression each)
-- and the steps a matching rule requires; a request is one document's run through a definition; every action is
-- append-only; blocks raised by modules route to their definition and an approval grants an override the module
-- honours once; delegations let one member act for another for a period.

CREATE TABLE app.wf_definitions (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  lineage_id           uuid NOT NULL,
  entity_type          text NOT NULL,
  trigger              text NOT NULL CHECK (trigger IN ('on_submit', 'on_post_attempt', 'on_block')),
  block_kind           text,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  version              int NOT NULL DEFAULT 1,
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'active', 'retired')),
  reapproval_policy    text NOT NULL DEFAULT 'reset' CHECK (reapproval_policy IN ('reset', 'none')),
  override_valid_hours int NOT NULL DEFAULT 168 CHECK (override_valid_hours > 0),
  activated_at         timestamptz,
  retired_at           timestamptz,
  created_by           uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  CHECK ((trigger = 'on_block') = (block_kind IS NOT NULL))
);
CREATE UNIQUE INDEX wf_definitions_active_idx ON app.wf_definitions (tenant_id, entity_type, trigger, coalesce(block_kind, '')) WHERE status = 'active';
CREATE INDEX wf_definitions_lineage_idx ON app.wf_definitions (tenant_id, lineage_id, version);
CALL app.enable_tenant_rls('app.wf_definitions');
CALL app.track_updated_at('app.wf_definitions');

CREATE TABLE app.wf_rules (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  definition_id  uuid NOT NULL,
  sort_order     int NOT NULL,
  name_i18n      jsonb NOT NULL DEFAULT '{}'::jsonb,
  condition      text NOT NULL,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, definition_id, sort_order),
  FOREIGN KEY (tenant_id, definition_id) REFERENCES app.wf_definitions (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.wf_rules');

CREATE TABLE app.wf_steps (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  rule_id          uuid NOT NULL,
  sort_order       int NOT NULL,
  name_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  approver_kind    text NOT NULL CHECK (approver_kind IN ('users', 'role')),
  approver_spec    jsonb NOT NULL DEFAULT '{}'::jsonb,
  mode             text NOT NULL DEFAULT 'any' CHECK (mode IN ('any', 'all', 'quorum')),
  quorum           int,
  timeout_hours    int CHECK (timeout_hours IS NULL OR timeout_hours > 0),
  escalation       jsonb,
  allow_delegate   boolean NOT NULL DEFAULT true,
  require_comment  boolean NOT NULL DEFAULT false,
  require_step_up  boolean NOT NULL DEFAULT false,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, rule_id, sort_order),
  FOREIGN KEY (tenant_id, rule_id) REFERENCES app.wf_rules (tenant_id, id) ON DELETE CASCADE,
  CHECK (mode <> 'quorum' OR quorum >= 1)
);
CALL app.enable_tenant_rls('app.wf_steps');

CREATE TABLE app.wf_blocks (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  kind          text NOT NULL,
  entity_type   text NOT NULL,
  entity_id     uuid NOT NULL,
  company_id    uuid,
  display       text NOT NULL,
  why           jsonb NOT NULL DEFAULT '{}'::jsonb,
  status        text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'pending', 'overridden', 'cleared')),
  request_id    uuid,
  raised_by     uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id)
);
CREATE INDEX wf_blocks_entity_idx ON app.wf_blocks (tenant_id, entity_type, entity_id, kind);
CALL app.enable_tenant_rls('app.wf_blocks');
CALL app.track_updated_at('app.wf_blocks');

CREATE TABLE app.wf_requests (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  definition_id       uuid NOT NULL,
  definition_version  int NOT NULL,
  rule_id             uuid,
  rule_name_i18n      jsonb NOT NULL DEFAULT '{}'::jsonb,
  entity_type         text NOT NULL,
  entity_id           uuid NOT NULL,
  company_id          uuid,
  display             text NOT NULL,
  requested_by        uuid,
  status              text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'approved', 'rejected', 'cancelled', 'expired', 'auto_approved')),
  current_step_no     int,
  evaluation          jsonb NOT NULL DEFAULT '{}'::jsonb,
  subject             jsonb NOT NULL DEFAULT '{}'::jsonb,
  block_id            uuid,
  due_at              timestamptz,
  decided_at          timestamptz,
  decided_by          uuid,
  decision_action     text,
  decision_comment    text,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, definition_id) REFERENCES app.wf_definitions (tenant_id, id),
  FOREIGN KEY (tenant_id, block_id) REFERENCES app.wf_blocks (tenant_id, id)
);
CREATE INDEX wf_requests_entity_idx ON app.wf_requests (tenant_id, entity_type, entity_id, status);
CREATE INDEX wf_requests_status_idx ON app.wf_requests (tenant_id, status, due_at);
CALL app.enable_tenant_rls('app.wf_requests');
CALL app.track_updated_at('app.wf_requests');

CREATE TABLE app.wf_request_steps (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  request_id       uuid NOT NULL,
  step_no          int NOT NULL,
  step_id          uuid,
  name_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  mode             text NOT NULL,
  quorum           int,
  approvers        jsonb NOT NULL DEFAULT '[]'::jsonb,
  approved_by      jsonb NOT NULL DEFAULT '[]'::jsonb,
  status           text NOT NULL DEFAULT 'waiting' CHECK (status IN ('waiting', 'pending', 'approved', 'rejected', 'skipped')),
  timeout_hours    int,
  escalation       jsonb,
  escalated_at     timestamptz,
  due_at           timestamptz,
  allow_delegate   boolean NOT NULL DEFAULT true,
  require_comment  boolean NOT NULL DEFAULT false,
  require_step_up  boolean NOT NULL DEFAULT false,
  decided_at       timestamptz,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, request_id, step_no),
  FOREIGN KEY (tenant_id, request_id) REFERENCES app.wf_requests (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.wf_request_steps');

CREATE TABLE app.wf_actions (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  request_id     uuid NOT NULL,
  step_no        int,
  actor          uuid,
  on_behalf_of   uuid,
  action         text NOT NULL CHECK (action IN ('submit', 'approve', 'reject', 'request_changes', 'delegate', 'escalate', 'comment', 'cancel', 'expire')),
  comment        text,
  channel        text NOT NULL DEFAULT 'web',
  ip             inet,
  acted_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, request_id) REFERENCES app.wf_requests (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX wf_actions_request_idx ON app.wf_actions (tenant_id, request_id, acted_at);
CALL app.enable_tenant_rls('app.wf_actions');
CALL app.make_append_only('app.wf_actions');

CREATE TABLE app.wf_overrides (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  block_id      uuid NOT NULL,
  request_id    uuid NOT NULL,
  approved_by   uuid,
  reason        text NOT NULL,
  expires_at    timestamptz NOT NULL,
  consumed      boolean NOT NULL DEFAULT false,
  consumed_at   timestamptz,
  created_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, block_id) REFERENCES app.wf_blocks (tenant_id, id),
  FOREIGN KEY (tenant_id, request_id) REFERENCES app.wf_requests (tenant_id, id)
);
CREATE INDEX wf_overrides_block_idx ON app.wf_overrides (tenant_id, block_id, consumed);
CALL app.enable_tenant_rls('app.wf_overrides');

CREATE TABLE app.wf_delegations (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  from_membership_id  uuid NOT NULL,
  to_membership_id    uuid NOT NULL,
  definition_id       uuid,
  valid_from          date NOT NULL,
  valid_to            date NOT NULL,
  reason              text,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, definition_id) REFERENCES app.wf_definitions (tenant_id, id),
  CHECK (valid_to >= valid_from),
  CHECK (from_membership_id <> to_membership_id)
);
CREATE INDEX wf_delegations_to_idx ON app.wf_delegations (tenant_id, to_membership_id, valid_from, valid_to);
CALL app.enable_tenant_rls('app.wf_delegations');

-- The first consumer: stock adjustments submitted through the engine remember their request.
ALTER TABLE app.inv_adjustments ADD COLUMN approval_request_id uuid;
