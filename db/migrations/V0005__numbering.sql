-- V0005: numbering series (ADR-0016).
-- A series describes how documents of one type are numbered for a company (optionally a branch and a fiscal
-- year). Counters live per series and reset period ('' when the series never resets) and are advanced under a
-- row lock inside the caller's transaction, so a rolled-back posting rolls back its number: no gaps, no
-- duplicates. Every allocation is recorded append-only; the unique constraint makes duplicates impossible and
-- the gapless audit report is a query over the recorded numbers.

CREATE TABLE app.num_series (
  tenant_id       uuid NOT NULL REFERENCES control.tenants (id),
  id              uuid NOT NULL,
  code            text NOT NULL,
  document_type   text NOT NULL,
  company_id      uuid NOT NULL,
  branch_id       uuid,
  fiscal_year_id  uuid,
  template        text NOT NULL,
  start_number    bigint NOT NULL DEFAULT 1 CHECK (start_number >= 1),
  gapless         boolean NOT NULL DEFAULT true,
  reset_policy    text NOT NULL DEFAULT 'never' CHECK (reset_policy IN ('never', 'yearly', 'monthly')),
  valid_from      date,
  valid_to        date,
  is_default      boolean NOT NULL DEFAULT false,
  is_active       boolean NOT NULL DEFAULT true,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id),
  FOREIGN KEY (tenant_id, fiscal_year_id) REFERENCES app.org_fiscal_years (tenant_id, id),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CREATE INDEX num_series_lookup_idx ON app.num_series (tenant_id, document_type, company_id);
CALL app.enable_tenant_rls('app.num_series');
CALL app.track_updated_at('app.num_series');

CREATE TABLE app.num_series_counters (
  tenant_id    uuid NOT NULL,
  series_id    uuid NOT NULL,
  period_key   text NOT NULL DEFAULT '',
  next_number  bigint NOT NULL CHECK (next_number >= 1),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, series_id, period_key),
  FOREIGN KEY (tenant_id, series_id) REFERENCES app.num_series (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.num_series_counters');

CREATE TABLE app.num_allocations (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  series_id      uuid NOT NULL,
  period_key     text NOT NULL DEFAULT '',
  number         bigint NOT NULL,
  text           text NOT NULL,
  document_type  text NOT NULL,
  document_id    uuid NOT NULL,
  allocated_by   uuid,
  allocated_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, series_id, period_key, number),
  UNIQUE (tenant_id, series_id, document_id),
  FOREIGN KEY (tenant_id, series_id) REFERENCES app.num_series (tenant_id, id)
);
CALL app.enable_tenant_rls('app.num_allocations');
CALL app.make_append_only('app.num_allocations');

-- Takes the next number of a series for a period under the counter's row lock, records the allocation and
-- returns the number. Runs as the application role inside the caller's transaction (RLS applies; a rollback
-- undoes the counter advance and the allocation together).
CREATE OR REPLACE FUNCTION app.num_allocate(
  p_allocation_id uuid, p_series_id uuid, p_period_key text, p_start bigint, p_text_template text,
  p_document_type text, p_document_id uuid, p_allocated_by uuid)
RETURNS TABLE (number bigint, text text)
LANGUAGE plpgsql AS $$
DECLARE
  v_tenant uuid := app.current_tenant();
  v_number bigint;
  v_text   text;
BEGIN
  INSERT INTO app.num_series_counters (tenant_id, series_id, period_key, next_number)
  VALUES (v_tenant, p_series_id, p_period_key, p_start)
  ON CONFLICT (tenant_id, series_id, period_key) DO NOTHING;

  SELECT c.next_number INTO v_number
  FROM app.num_series_counters c
  WHERE c.tenant_id = v_tenant AND c.series_id = p_series_id AND c.period_key = p_period_key
  FOR UPDATE;

  UPDATE app.num_series_counters c
  SET next_number = v_number + 1, updated_at = now()
  WHERE c.tenant_id = v_tenant AND c.series_id = p_series_id AND c.period_key = p_period_key;

  -- The template already carries every token but the sequence; {seq} and {seq:N} are rendered here so the
  -- stored text is exactly what the caller receives.
  v_text := app.num_render_seq(p_text_template, v_number);

  INSERT INTO app.num_allocations (tenant_id, id, series_id, period_key, number, text, document_type, document_id, allocated_by)
  VALUES (v_tenant, p_allocation_id, p_series_id, p_period_key, v_number, v_text, p_document_type, p_document_id, p_allocated_by);

  number := v_number;
  text := v_text;
  RETURN NEXT;
END
$$;

-- Replaces {seq} and {seq:N} (zero-padded to N digits, never truncated) in a template.
CREATE OR REPLACE FUNCTION app.num_render_seq(p_template text, p_number bigint) RETURNS text
LANGUAGE plpgsql IMMUTABLE AS $$
DECLARE
  v_result text := p_template;
  v_match  text[];
  v_width  int;
BEGIN
  FOR v_match IN SELECT regexp_matches(p_template, '\{seq(?::(\d+))?\}', 'g') LOOP
    v_width := COALESCE(v_match[1]::int, 0);
    v_result := replace(v_result,
      CASE WHEN v_match[1] IS NULL THEN '{seq}' ELSE '{seq:' || v_match[1] || '}' END,
      lpad(p_number::text, GREATEST(v_width, length(p_number::text)), '0'));
  END LOOP;
  RETURN v_result;
END
$$;
