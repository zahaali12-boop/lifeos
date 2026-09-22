-- V0001: schemas, extensions and the tenant catalogue.
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
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  CONSTRAINT tenants_slug_format CHECK (slug ~ '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$')
);
CREATE UNIQUE INDEX tenants_slug_key ON control.tenants (slug);

CREATE TABLE control.tenant_databases (
  tenant_id         uuid PRIMARY KEY REFERENCES control.tenants (id) ON DELETE CASCADE,
  connection_name   text NOT NULL,
  region            text NOT NULL,
  created_at        timestamptz NOT NULL DEFAULT now()
);

-- Tenant deletion is a job, never a cascade from the UI; the app role cannot delete tenants at all.
REVOKE DELETE ON control.tenants FROM quicker_app;
