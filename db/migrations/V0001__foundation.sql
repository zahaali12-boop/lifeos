-- V0001: schemas, extensions, the tenant catalogue and the contract procedures every later migration calls.
-- Runs as quicker_owner. The application connects as quicker_app (no table ownership, no BYPASSRLS).

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE EXTENSION IF NOT EXISTS pgcrypto;

CREATE SCHEMA IF NOT EXISTS control;   -- tenant catalogue and global identities (no tenant RLS)
CREATE SCHEMA IF NOT EXISTS app;       -- tenant data (every table: tenant_id + forced RLS)
CREATE SCHEMA IF NOT EXISTS reporting; -- read models (same rules as app)
CREATE SCHEMA IF NOT EXISTS ops;       -- outbox, jobs, idempotency, migration journal

GRANT USAGE ON SCHEMA control, app, reporting, ops TO quicker_app;

-- Default privileges: the app role gets DML on future tables and usage on sequences, never DDL or ownership.
ALTER DEFAULT PRIVILEGES FOR ROLE quicker_owner IN SCHEMA control, app, reporting, ops
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO quicker_app;
ALTER DEFAULT PRIVILEGES FOR ROLE quicker_owner IN SCHEMA control, app, reporting, ops
  GRANT USAGE, SELECT ON SEQUENCES TO quicker_app;
ALTER DEFAULT PRIVILEGES FOR ROLE quicker_owner IN SCHEMA control, app, reporting, ops
  GRANT EXECUTE ON FUNCTIONS TO quicker_app;

-- ---------------------------------------------------------------------------------------------
-- Session helpers (ADR-0004). The unit of work sets these with SET LOCAL; a missing value yields
-- NULL, never "all rows".
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION app.current_tenant() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

CREATE OR REPLACE FUNCTION app.current_user_id() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT NULLIF(current_setting('app.user_id', true), '')::uuid
$$;

CREATE OR REPLACE FUNCTION app.current_request_id() RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT NULLIF(current_setting('app.request_id', true), '')
$$;

-- Maintenance escape hatch for audited backfills (ADR-0007). Never set by the application role.
CREATE OR REPLACE FUNCTION app.maintenance_mode() RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT COALESCE(current_setting('app.maintenance', true), '') = 'on'
$$;

-- Append-only guard: refuses UPDATE and DELETE on ledgers and audit tables.
CREATE OR REPLACE FUNCTION app.forbid_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF app.maintenance_mode() THEN
    RETURN COALESCE(NEW, OLD);
  END IF;
  RAISE EXCEPTION 'append_only_violation: % on %.% is not allowed', TG_OP, TG_TABLE_SCHEMA, TG_TABLE_NAME
    USING ERRCODE = 'P0001', HINT = 'Posted records are immutable; post a reversal or adjustment instead.';
END
$$;

-- updated_at maintenance for mutable tables.
CREATE OR REPLACE FUNCTION app.touch_updated_at() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  NEW.updated_at := now();
  RETURN NEW;
END
$$;

-- Applies the tenancy contract to a table: RLS enabled and forced, one policy on app.current_tenant().
CREATE OR REPLACE PROCEDURE app.enable_tenant_rls(target regclass)
LANGUAGE plpgsql AS $$
DECLARE
  policy_exists boolean;
BEGIN
  EXECUTE format('ALTER TABLE %s ENABLE ROW LEVEL SECURITY', target);
  EXECUTE format('ALTER TABLE %s FORCE ROW LEVEL SECURITY', target);
  SELECT EXISTS (
    SELECT 1 FROM pg_policies p
    JOIN pg_class c ON c.relname = p.tablename
    JOIN pg_namespace n ON n.oid = c.relnamespace AND n.nspname = p.schemaname
    WHERE c.oid = target AND p.policyname = 'tenant_isolation'
  ) INTO policy_exists;
  IF NOT policy_exists THEN
    EXECUTE format(
      'CREATE POLICY tenant_isolation ON %s USING (tenant_id = app.current_tenant()) WITH CHECK (tenant_id = app.current_tenant())',
      target);
  END IF;
END
$$;

-- Marks a table append-only: trigger plus privilege revocation for the app role.
CREATE OR REPLACE PROCEDURE app.make_append_only(target regclass)
LANGUAGE plpgsql AS $$
BEGIN
  EXECUTE format('DROP TRIGGER IF EXISTS append_only ON %s', target);
  EXECUTE format('CREATE TRIGGER append_only BEFORE UPDATE OR DELETE ON %s FOR EACH ROW EXECUTE FUNCTION app.forbid_change()', target);
  EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON %s FROM quicker_app', target);
END
$$;

-- Adds the updated_at trigger to a mutable table.
CREATE OR REPLACE PROCEDURE app.track_updated_at(target regclass)
LANGUAGE plpgsql AS $$
BEGIN
  EXECUTE format('DROP TRIGGER IF EXISTS touch_updated_at ON %s', target);
  EXECUTE format('CREATE TRIGGER touch_updated_at BEFORE UPDATE ON %s FOR EACH ROW EXECUTE FUNCTION app.touch_updated_at()', target);
END
$$;

-- ---------------------------------------------------------------------------------------------
-- Tenant catalogue (ADR-0004)
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.tenants (
  id                uuid PRIMARY KEY,
  slug              text NOT NULL,
  name              text NOT NULL,
  tier              text NOT NULL DEFAULT 'shared' CHECK (tier IN ('shared', 'dedicated')),
  region            text NOT NULL DEFAULT 'me',
  status            text NOT NULL DEFAULT 'active' CHECK (status IN ('provisioning', 'active', 'suspended', 'deleting')),
  default_language  text NOT NULL DEFAULT 'en',
  settings          jsonb NOT NULL DEFAULT '{}'::jsonb,
  permissions_epoch bigint NOT NULL DEFAULT 1,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT tenants_slug_format CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$')
);
CREATE UNIQUE INDEX tenants_slug_key ON control.tenants (slug);
CALL app.track_updated_at('control.tenants');

CREATE TABLE control.tenant_databases (
  tenant_id         uuid PRIMARY KEY REFERENCES control.tenants (id) ON DELETE CASCADE,
  connection_name   text NOT NULL,
  region            text NOT NULL,
  created_at        timestamptz NOT NULL DEFAULT now()
);

-- Tenant deletion is a job, never a cascade from the UI; the app role cannot delete tenants at all.
REVOKE DELETE ON control.tenants FROM quicker_app;
