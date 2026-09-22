-- Repeatable: helper functions and trigger functions. Re-run on every migrate; must stay idempotent.

-- The tenant of the current transaction, set by the unit of work with SET LOCAL app.tenant_id.
-- Returns NULL (never all rows) when unset, so a missing context yields no rows under RLS.
CREATE OR REPLACE FUNCTION app.current_tenant() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT NULLIF(current_setting('app.tenant_id', true), '')::uuid
$$;

CREATE OR REPLACE FUNCTION app.current_user_id() RETURNS uuid
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT NULLIF(current_setting('app.user_id', true), '')::uuid
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

-- Schema contract views used by the conventions test and by operators.
CREATE OR REPLACE VIEW ops.tenant_tables AS
SELECT n.nspname AS schema_name,
       c.relname AS table_name,
       EXISTS (SELECT 1 FROM pg_attribute a WHERE a.attrelid = c.oid AND a.attname = 'tenant_id' AND NOT a.attisdropped) AS has_tenant_id,
       c.relrowsecurity AS rls_enabled,
       c.relforcerowsecurity AS rls_forced,
       EXISTS (SELECT 1 FROM pg_policies p WHERE p.schemaname = n.nspname AND p.tablename = c.relname AND p.policyname = 'tenant_isolation') AS has_policy,
       EXISTS (SELECT 1 FROM pg_trigger t WHERE t.tgrelid = c.oid AND t.tgname = 'append_only') AS is_append_only
FROM pg_class c
JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname IN ('app', 'reporting') AND c.relkind IN ('r', 'p') AND c.relispartition = false;

CREATE OR REPLACE VIEW ops.floating_point_columns AS
SELECT table_schema, table_name, column_name, data_type
FROM information_schema.columns
WHERE table_schema IN ('control', 'app', 'reporting', 'ops')
  AND data_type IN ('real', 'double precision');

GRANT SELECT ON ops.tenant_tables, ops.floating_point_columns TO quicker_app;
-- The migration journal is created by the migrator before default privileges apply; readiness reads it.
GRANT SELECT ON ops.schemaversions TO quicker_app;
