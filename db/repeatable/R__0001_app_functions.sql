-- Repeatable: contract views and grants that must track the whole schema. Re-run on every migrate; idempotent.
-- Core functions and procedures live in V0001 so versioned scripts can call them on a fresh database.

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
DO $$ BEGIN
  IF to_regclass('ops.schemaversions') IS NOT NULL THEN
    GRANT SELECT ON ops.schemaversions TO quicker_app;
  END IF;
END $$;
