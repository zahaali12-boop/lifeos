-- V0002: global identities (control) and tenant-scoped authorization (app). ADR-0014.

-- ---------------------------------------------------------------------------------------------
-- control.users: one identity per person across tenants
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.users (
  id                    uuid PRIMARY KEY,
  email                 text NOT NULL,
  display_name          text NOT NULL,
  password_hash         text,
  password_updated_at   timestamptz,
  locale                text NOT NULL DEFAULT 'en',
  time_zone             text NOT NULL DEFAULT 'Asia/Baghdad',
  digit_style           text NOT NULL DEFAULT 'western' CHECK (digit_style IN ('western', 'eastern_arabic')),
  status                text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'disabled')),
  is_platform_operator  boolean NOT NULL DEFAULT false,
  failed_login_count    int NOT NULL DEFAULT 0,
  locked_until          timestamptz,
  last_login_at         timestamptz,
  email_verified_at     timestamptz,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT users_email_format CHECK (email ~ '^[^@\s]+@[^@\s]+\.[^@\s]+$')
);
CREATE UNIQUE INDEX users_email_key ON control.users (lower(email));
CALL app.track_updated_at('control.users');

CREATE TABLE control.mfa_methods (
  id              uuid PRIMARY KEY,
  user_id         uuid NOT NULL REFERENCES control.users (id) ON DELETE CASCADE,
  kind            text NOT NULL CHECK (kind IN ('totp', 'webauthn', 'recovery')),
  name            text NOT NULL DEFAULT '',
  secret_enc      bytea,                 -- totp secret (encrypted) or recovery code hash
  credential_id   bytea,                 -- webauthn
  public_key      bytea,                 -- webauthn
  sign_count      bigint NOT NULL DEFAULT 0,
  aaguid          uuid,
  transports      text[],
  verified_at     timestamptz,
  last_used_at    timestamptz,
  consumed_at     timestamptz,           -- recovery codes are single use
  created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX mfa_methods_user_idx ON control.mfa_methods (user_id, kind);
CREATE UNIQUE INDEX mfa_methods_credential_key ON control.mfa_methods (credential_id) WHERE credential_id IS NOT NULL;

-- ---------------------------------------------------------------------------------------------
-- control.tenant_memberships: a user's seat in a tenant
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.tenant_memberships (
  id                      uuid PRIMARY KEY,
  tenant_id               uuid NOT NULL REFERENCES control.tenants (id),
  user_id                 uuid NOT NULL REFERENCES control.users (id),
  status                  text NOT NULL DEFAULT 'invited' CHECK (status IN ('invited', 'active', 'disabled')),
  is_owner                boolean NOT NULL DEFAULT false,
  invited_by              uuid REFERENCES control.users (id),
  invited_at              timestamptz,
  accepted_at             timestamptz,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, user_id)
);
CREATE INDEX tenant_memberships_user_idx ON control.tenant_memberships (user_id);
CALL app.track_updated_at('control.tenant_memberships');

-- ---------------------------------------------------------------------------------------------
-- control.sessions: refresh-token sessions with rotation families
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.sessions (
  id                  uuid PRIMARY KEY,
  user_id             uuid NOT NULL REFERENCES control.users (id) ON DELETE CASCADE,
  membership_id       uuid REFERENCES control.tenant_memberships (id) ON DELETE CASCADE,
  family_id           uuid NOT NULL,
  refresh_token_hash  bytea NOT NULL,
  amr                 text NOT NULL DEFAULT 'pwd',
  auth_time           timestamptz NOT NULL,
  ip                  inet,
  user_agent          text,
  created_at          timestamptz NOT NULL DEFAULT now(),
  last_used_at        timestamptz NOT NULL DEFAULT now(),
  expires_at          timestamptz NOT NULL,
  revoked_at          timestamptz,
  revoked_reason      text
);
CREATE UNIQUE INDEX sessions_token_key ON control.sessions (refresh_token_hash);
CREATE INDEX sessions_user_idx ON control.sessions (user_id, revoked_at);
CREATE INDEX sessions_family_idx ON control.sessions (family_id);

-- One-time tokens: invitations, password reset, email verification, MFA challenges, OIDC transactions
CREATE TABLE control.one_time_tokens (
  id            uuid PRIMARY KEY,
  kind          text NOT NULL CHECK (kind IN ('invitation', 'password_reset', 'email_verify', 'mfa_challenge', 'oidc_transaction', 'step_up')),
  user_id       uuid REFERENCES control.users (id) ON DELETE CASCADE,
  tenant_id     uuid REFERENCES control.tenants (id) ON DELETE CASCADE,
  token_hash    bytea NOT NULL,
  payload       jsonb NOT NULL DEFAULT '{}'::jsonb,
  expires_at    timestamptz NOT NULL,
  consumed_at   timestamptz,
  created_at    timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX one_time_tokens_hash_key ON control.one_time_tokens (token_hash);
CREATE INDEX one_time_tokens_expiry_idx ON control.one_time_tokens (expires_at);

-- OIDC federation per tenant
CREATE TABLE control.sso_connections (
  id                  uuid PRIMARY KEY,
  tenant_id           uuid NOT NULL REFERENCES control.tenants (id) ON DELETE CASCADE,
  code                text NOT NULL,
  kind                text NOT NULL DEFAULT 'oidc' CHECK (kind IN ('oidc')),
  display_name        text NOT NULL,
  authority           text NOT NULL,          -- issuer URL with .well-known/openid-configuration
  client_id           text NOT NULL,
  client_secret_enc   bytea,
  scopes              text NOT NULL DEFAULT 'openid profile email',
  email_domains       text[] NOT NULL DEFAULT '{}',
  jit_provisioning    boolean NOT NULL DEFAULT true,
  group_claim         text,
  group_role_map      jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active           boolean NOT NULL DEFAULT true,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  UNIQUE (tenant_id, code)
);
CALL app.track_updated_at('control.sso_connections');

-- ---------------------------------------------------------------------------------------------
-- app.idn_*: tenant-scoped authorization
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.idn_roles (
  tenant_id       uuid NOT NULL REFERENCES control.tenants (id),
  id              uuid NOT NULL,
  code            text NOT NULL,
  name_i18n       jsonb NOT NULL DEFAULT '{}'::jsonb,
  description     text NOT NULL DEFAULT '',
  is_system       boolean NOT NULL DEFAULT false,
  template_code   text,
  is_active       boolean NOT NULL DEFAULT true,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.idn_roles');
CALL app.track_updated_at('app.idn_roles');

-- permission_key is a catalogue key from code ("sales.invoice.post") or a wildcard ("sales.*", "*").
CREATE TABLE app.idn_role_permissions (
  tenant_id       uuid NOT NULL,
  role_id         uuid NOT NULL,
  permission_key  text NOT NULL,
  PRIMARY KEY (tenant_id, role_id, permission_key),
  FOREIGN KEY (tenant_id, role_id) REFERENCES app.idn_roles (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.idn_role_permissions');

CREATE TABLE app.idn_role_assignments (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  membership_id   uuid NOT NULL REFERENCES control.tenant_memberships (id) ON DELETE CASCADE,
  role_id         uuid NOT NULL,
  valid_from      date,
  valid_to        date,
  created_at      timestamptz NOT NULL DEFAULT now(),
  created_by      uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, role_id) REFERENCES app.idn_roles (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX idn_role_assignments_membership_idx ON app.idn_role_assignments (tenant_id, membership_id);
CALL app.enable_tenant_rls('app.idn_role_assignments');

-- Record scopes on an assignment; no rows = all records of that scope type.
CREATE TABLE app.idn_assignment_scopes (
  tenant_id       uuid NOT NULL,
  assignment_id   uuid NOT NULL,
  scope_type      text NOT NULL CHECK (scope_type IN ('company', 'branch', 'warehouse')),
  scope_id        uuid NOT NULL,
  PRIMARY KEY (tenant_id, assignment_id, scope_type, scope_id),
  FOREIGN KEY (tenant_id, assignment_id) REFERENCES app.idn_role_assignments (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.idn_assignment_scopes');

CREATE TABLE app.idn_field_rules (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  role_id       uuid NOT NULL,
  entity_type   text NOT NULL,
  field         text NOT NULL,
  access        text NOT NULL CHECK (access IN ('hidden', 'read_only', 'editable')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, role_id, entity_type, field),
  FOREIGN KEY (tenant_id, role_id) REFERENCES app.idn_roles (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.idn_field_rules');

CREATE TABLE app.idn_document_type_rules (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  role_id         uuid NOT NULL,
  document_type   text NOT NULL,
  action          text NOT NULL CHECK (action IN ('create', 'approve', 'post', 'reverse', 'export')),
  allowed         boolean NOT NULL,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, role_id, document_type, action),
  FOREIGN KEY (tenant_id, role_id) REFERENCES app.idn_roles (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.idn_document_type_rules');

CREATE TABLE app.idn_sod_rules (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  permission_a    text NOT NULL,
  permission_b    text NOT NULL,
  severity        text NOT NULL DEFAULT 'warn' CHECK (severity IN ('warn', 'block')),
  rationale_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_system       boolean NOT NULL DEFAULT false,
  is_active       boolean NOT NULL DEFAULT true,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, permission_a, permission_b)
);
CALL app.enable_tenant_rls('app.idn_sod_rules');

CREATE TABLE app.idn_sod_exceptions (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  sod_rule_id     uuid NOT NULL,
  membership_id   uuid NOT NULL REFERENCES control.tenant_memberships (id) ON DELETE CASCADE,
  reason          text NOT NULL,
  approved_by     uuid NOT NULL,
  expires_on      date,
  created_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, sod_rule_id) REFERENCES app.idn_sod_rules (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.idn_sod_exceptions');

CREATE TABLE app.idn_api_keys (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  name            text NOT NULL,
  prefix          text NOT NULL,          -- first characters shown in the UI
  key_hash        bytea NOT NULL,
  scopes          text[] NOT NULL DEFAULT '{}',
  ip_allowlist    inet[] NOT NULL DEFAULT '{}',
  membership_id   uuid REFERENCES control.tenant_memberships (id),  -- acts as this member (service accounts use a dedicated membership)
  expires_at      timestamptz,
  last_used_at    timestamptz,
  created_by      uuid,
  created_at      timestamptz NOT NULL DEFAULT now(),
  revoked_at      timestamptz,
  PRIMARY KEY (tenant_id, id)
);
CREATE UNIQUE INDEX idn_api_keys_hash_key ON app.idn_api_keys (key_hash);
CALL app.enable_tenant_rls('app.idn_api_keys');

-- The app role must never delete memberships or users directly (deactivate instead; deletion is a job).
REVOKE DELETE ON control.users, control.tenant_memberships FROM quicker_app;
