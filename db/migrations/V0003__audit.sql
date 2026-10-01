-- V0003: tamper-evident audit log (ADR-0015).
-- One hash chain per tenant in app.aud_events (partitioned by month), one platform chain in control for
-- events recorded outside any tenant (failed logins, password resets), anchors and verification runs.

-- ---------------------------------------------------------------------------------------------
-- Canonical form. The hash of an event covers exactly the fields below, rendered by PostgreSQL, so
-- any verifier recomputes it from the same rendering: each field as <octet length>:<text>, NULL as
-- "-", fields joined by LF and prefixed by the format version. jsonb and inet fields use their
-- PostgreSQL text form, which is deterministic for a stored value.
-- ---------------------------------------------------------------------------------------------
CREATE OR REPLACE FUNCTION app.aud_field(v text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
  SELECT CASE WHEN v IS NULL THEN '-' ELSE octet_length(v)::text || ':' || v END
$$;

CREATE OR REPLACE FUNCTION app.aud_canonical(
  p_id uuid, p_tenant_id uuid, p_seq bigint, p_occurred_at timestamptz,
  p_actor_type text, p_actor_id uuid, p_actor_display text, p_actor_ip inet, p_user_agent text,
  p_request_id text, p_correlation_id text, p_company_id uuid,
  p_entity_type text, p_entity_id uuid, p_entity_display text, p_action text,
  p_before jsonb, p_after jsonb, p_diff jsonb, p_details jsonb, p_reason text)
RETURNS text
LANGUAGE sql STABLE PARALLEL SAFE AS $$
  SELECT concat_ws(E'\n', 'quicker-audit-v1',
    app.aud_field(p_id::text),
    app.aud_field(p_tenant_id::text),
    app.aud_field(p_seq::text),
    app.aud_field(to_char(p_occurred_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US"Z"')),
    app.aud_field(p_actor_type),
    app.aud_field(p_actor_id::text),
    app.aud_field(p_actor_display),
    app.aud_field(p_actor_ip::text),
    app.aud_field(p_user_agent),
    app.aud_field(p_request_id),
    app.aud_field(p_correlation_id),
    app.aud_field(p_company_id::text),
    app.aud_field(p_entity_type),
    app.aud_field(p_entity_id::text),
    app.aud_field(p_entity_display),
    app.aud_field(p_action),
    app.aud_field(p_before::text),
    app.aud_field(p_after::text),
    app.aud_field(p_diff::text),
    app.aud_field(p_details::text),
    app.aud_field(p_reason))
$$;

-- ---------------------------------------------------------------------------------------------
-- Tenant chains
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.aud_chain_heads (
  tenant_id   uuid PRIMARY KEY REFERENCES control.tenants (id),
  seq         bigint NOT NULL DEFAULT 0,
  head_hash   bytea NOT NULL DEFAULT '\x'::bytea,
  updated_at  timestamptz NOT NULL DEFAULT now()
);
CALL app.enable_tenant_rls('app.aud_chain_heads');
-- Only the chaining trigger (owner-defined) moves a head; the application reads it.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON app.aud_chain_heads FROM quicker_app;

CREATE TABLE app.aud_events (
  tenant_id       uuid NOT NULL REFERENCES control.tenants (id),
  id              uuid NOT NULL,
  seq             bigint NOT NULL,
  occurred_at     timestamptz NOT NULL,
  actor_type      text NOT NULL CHECK (actor_type IN ('user', 'api_key', 'system', 'anonymous')),
  actor_id        uuid,
  actor_display   text NOT NULL DEFAULT '',
  actor_ip        inet,
  user_agent      text,
  request_id      text,
  correlation_id  text,
  company_id      uuid,
  entity_type     text NOT NULL,
  entity_id       uuid NOT NULL,
  entity_display  text NOT NULL DEFAULT '',
  action          text NOT NULL,
  before          jsonb,
  after           jsonb,
  diff            jsonb,
  details         jsonb,
  reason          text,
  prev_hash       bytea NOT NULL,
  hash            bytea NOT NULL,
  PRIMARY KEY (tenant_id, occurred_at, id)
) PARTITION BY RANGE (occurred_at);

CREATE UNIQUE INDEX aud_events_seq_key ON app.aud_events (tenant_id, seq, occurred_at);
CREATE INDEX aud_events_id_idx ON app.aud_events (tenant_id, id);
CREATE INDEX aud_events_entity_idx ON app.aud_events (tenant_id, entity_type, entity_id, seq);
CREATE INDEX aud_events_actor_idx ON app.aud_events (tenant_id, actor_id, seq);
CALL app.enable_tenant_rls('app.aud_events');
CALL app.make_append_only('app.aud_events');

-- Assigns the sequence number and links the event to its predecessor under the tenant's head lock, so the
-- chain is linear. SECURITY DEFINER: the application role cannot touch the head row itself.
CREATE OR REPLACE FUNCTION app.aud_chain_event() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  v_seq  bigint;
  v_head bytea;
BEGIN
  IF NEW.tenant_id IS DISTINCT FROM app.current_tenant() THEN
    RAISE EXCEPTION 'audit event for tenant % outside its session', NEW.tenant_id USING ERRCODE = '42501';
  END IF;
  INSERT INTO app.aud_chain_heads (tenant_id) VALUES (NEW.tenant_id) ON CONFLICT (tenant_id) DO NOTHING;
  SELECT seq, head_hash INTO v_seq, v_head FROM app.aud_chain_heads WHERE tenant_id = NEW.tenant_id FOR UPDATE;
  NEW.seq := v_seq + 1;
  NEW.prev_hash := v_head;
  NEW.hash := sha256(v_head || convert_to(app.aud_canonical(
    NEW.id, NEW.tenant_id, NEW.seq, NEW.occurred_at,
    NEW.actor_type, NEW.actor_id, NEW.actor_display, NEW.actor_ip, NEW.user_agent,
    NEW.request_id, NEW.correlation_id, NEW.company_id,
    NEW.entity_type, NEW.entity_id, NEW.entity_display, NEW.action,
    NEW.before, NEW.after, NEW.diff, NEW.details, NEW.reason), 'UTF8'));
  UPDATE app.aud_chain_heads SET seq = NEW.seq, head_hash = NEW.hash, updated_at = now() WHERE tenant_id = NEW.tenant_id;
  RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION app.aud_chain_event() FROM PUBLIC;
CREATE TRIGGER aud_chain BEFORE INSERT ON app.aud_events FOR EACH ROW EXECUTE FUNCTION app.aud_chain_event();

-- Monthly partitions. Created ahead by the migration and on demand by the writer (SECURITY DEFINER, since the
-- application role has no DDL); ATTACH keeps the lock on the parent light. Returns true when the partition
-- already existed, false when this call created it (a caller only caches "exists" once that is committed).
CREATE OR REPLACE FUNCTION app.aud_ensure_partition(p_day date) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  v_from date := date_trunc('month', p_day)::date;
  v_to   date := (date_trunc('month', p_day) + interval '1 month')::date;
  v_name text := 'aud_events_' || to_char(v_from, 'YYYYMM');
BEGIN
  IF to_regclass('app.' || v_name) IS NOT NULL THEN
    RETURN true;
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext('app.aud_events:partitions'));
  IF to_regclass('app.' || v_name) IS NOT NULL THEN
    RETURN true;
  END IF;
  EXECUTE format('CREATE TABLE app.%I (LIKE app.aud_events INCLUDING DEFAULTS INCLUDING CONSTRAINTS)', v_name);
  EXECUTE format('ALTER TABLE app.%I ENABLE ROW LEVEL SECURITY', v_name);
  EXECUTE format('ALTER TABLE app.%I FORCE ROW LEVEL SECURITY', v_name);
  EXECUTE format('CREATE POLICY tenant_isolation ON app.%I USING (tenant_id = app.current_tenant()) WITH CHECK (tenant_id = app.current_tenant())', v_name);
  EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON app.%I FROM quicker_app', v_name);
  EXECUTE format('ALTER TABLE app.aud_events ATTACH PARTITION app.%I FOR VALUES FROM (%L) TO (%L)', v_name, v_from, v_to);
  RETURN false;
END
$$;
REVOKE ALL ON FUNCTION app.aud_ensure_partition(date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.aud_ensure_partition(date) TO quicker_app;

DO $$
DECLARE
  m date := date '2026-01-01';
BEGIN
  WHILE m < date '2028-01-01' LOOP
    PERFORM app.aud_ensure_partition(m);
    m := (m + interval '1 month')::date;
  END LOOP;
END
$$;

-- ---------------------------------------------------------------------------------------------
-- Platform chain: events recorded with no tenant session (sign-in attempts before tenant selection,
-- password resets). Readable by platform operators only.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.aud_platform_chain_head (
  singleton   boolean PRIMARY KEY DEFAULT true CHECK (singleton),
  seq         bigint NOT NULL DEFAULT 0,
  head_hash   bytea NOT NULL DEFAULT '\x'::bytea,
  updated_at  timestamptz NOT NULL DEFAULT now()
);
INSERT INTO control.aud_platform_chain_head DEFAULT VALUES;
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON control.aud_platform_chain_head FROM quicker_app;

CREATE TABLE control.aud_platform_events (
  id              uuid PRIMARY KEY,
  seq             bigint NOT NULL UNIQUE,
  occurred_at     timestamptz NOT NULL,
  actor_type      text NOT NULL CHECK (actor_type IN ('user', 'api_key', 'system', 'anonymous')),
  actor_id        uuid,
  actor_display   text NOT NULL DEFAULT '',
  actor_ip        inet,
  user_agent      text,
  request_id      text,
  correlation_id  text,
  entity_type     text NOT NULL,
  entity_id       uuid NOT NULL,
  entity_display  text NOT NULL DEFAULT '',
  action          text NOT NULL,
  before          jsonb,
  after           jsonb,
  diff            jsonb,
  details         jsonb,
  reason          text,
  prev_hash       bytea NOT NULL,
  hash            bytea NOT NULL
);
CREATE INDEX aud_platform_events_entity_idx ON control.aud_platform_events (entity_type, entity_id, seq);
CREATE INDEX aud_platform_events_time_idx ON control.aud_platform_events (occurred_at);
CALL app.make_append_only('control.aud_platform_events');

CREATE OR REPLACE FUNCTION control.aud_platform_chain_event() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  v_seq  bigint;
  v_head bytea;
BEGIN
  SELECT seq, head_hash INTO v_seq, v_head FROM control.aud_platform_chain_head WHERE singleton FOR UPDATE;
  NEW.seq := v_seq + 1;
  NEW.prev_hash := v_head;
  NEW.hash := sha256(v_head || convert_to(app.aud_canonical(
    NEW.id, NULL, NEW.seq, NEW.occurred_at,
    NEW.actor_type, NEW.actor_id, NEW.actor_display, NEW.actor_ip, NEW.user_agent,
    NEW.request_id, NEW.correlation_id, NULL,
    NEW.entity_type, NEW.entity_id, NEW.entity_display, NEW.action,
    NEW.before, NEW.after, NEW.diff, NEW.details, NEW.reason), 'UTF8'));
  UPDATE control.aud_platform_chain_head SET seq = NEW.seq, head_hash = NEW.hash, updated_at = now() WHERE singleton;
  RETURN NEW;
END
$$;
REVOKE ALL ON FUNCTION control.aud_platform_chain_event() FROM PUBLIC;
CREATE TRIGGER aud_chain BEFORE INSERT ON control.aud_platform_events FOR EACH ROW EXECUTE FUNCTION control.aud_platform_chain_event();

-- ---------------------------------------------------------------------------------------------
-- Anchors (a chain head written to an external append-only store) and verification runs.
-- Control tables: the application filters by tenant; history is append-only.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.aud_anchors (
  id            uuid PRIMARY KEY,
  chain         text NOT NULL CHECK (chain IN ('tenant', 'platform')),
  tenant_id     uuid REFERENCES control.tenants (id),
  seq           bigint NOT NULL,
  head_hash     bytea NOT NULL,
  anchored_at   timestamptz NOT NULL,
  store         text NOT NULL,
  reference     text NOT NULL,
  receipt       text NOT NULL,
  CONSTRAINT aud_anchors_chain_tenant CHECK ((chain = 'tenant') = (tenant_id IS NOT NULL))
);
CREATE INDEX aud_anchors_chain_idx ON control.aud_anchors (chain, tenant_id, seq DESC);
CALL app.make_append_only('control.aud_anchors');

CREATE TABLE control.aud_verifications (
  id                uuid PRIMARY KEY,
  chain             text NOT NULL CHECK (chain IN ('tenant', 'platform')),
  tenant_id         uuid REFERENCES control.tenants (id),
  verified_at       timestamptz NOT NULL,
  from_seq          bigint NOT NULL,
  to_seq            bigint NOT NULL,
  status            text NOT NULL CHECK (status IN ('ok', 'empty', 'broken', 'truncated', 'anchor_mismatch')),
  first_broken_seq  bigint,
  message           text,
  anchor_seq        bigint,
  anchor_matched    boolean,
  duration_ms       integer NOT NULL,
  CONSTRAINT aud_verifications_chain_tenant CHECK ((chain = 'tenant') = (tenant_id IS NOT NULL))
);
CREATE INDEX aud_verifications_chain_idx ON control.aud_verifications (chain, tenant_id, verified_at DESC);
CALL app.make_append_only('control.aud_verifications');
