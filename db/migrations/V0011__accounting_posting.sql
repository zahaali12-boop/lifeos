-- V0011: posting engine (ADR-0006, ADR-0007, ADR-0017, ADR-0026; roadmap 2.2).
-- Posting profiles map account roles and their keys to accounts (most specific match wins, resolved in code and
-- recorded on the line). Journal entries and lines are append-only, lines partitioned by posting year, balanced in
-- transaction, functional and reporting currency by a deferred constraint trigger so an unbalanced entry can never
-- be committed by any client. gl_balances is the derived table maintained in the posting transaction and rebuilt
-- from the lines on demand.

CREATE TABLE app.gl_posting_groups (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  kind        text NOT NULL CHECK (kind IN ('item', 'partner_customer', 'partner_supplier', 'bank', 'asset', 'tax', 'charge')),
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, kind, code)
);
CALL app.enable_tenant_rls('app.gl_posting_groups');
CALL app.track_updated_at('app.gl_posting_groups');

CREATE TABLE app.gl_posting_profiles (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  company_id  uuid NOT NULL,
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  version     int NOT NULL DEFAULT 1 CHECK (version >= 1),
  valid_from  date NOT NULL,
  status      text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'active', 'retired')),
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code, version),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) DEFERRABLE INITIALLY IMMEDIATE
);
CALL app.enable_tenant_rls('app.gl_posting_profiles');
CALL app.track_updated_at('app.gl_posting_profiles');

CREATE TABLE app.gl_posting_rules (
  tenant_id                 uuid NOT NULL REFERENCES control.tenants (id),
  id                        uuid NOT NULL,
  profile_id                uuid NOT NULL,
  account_role              text NOT NULL,
  document_type             text,
  item_posting_group_id     uuid,
  partner_posting_group_id  uuid,
  tax_code_id               uuid,
  warehouse_id              uuid,
  branch_id                 uuid,
  bank_account_id           uuid,
  asset_category_id         uuid,
  charge_type_id            uuid,
  account_id                uuid NOT NULL,
  specificity               int NOT NULL DEFAULT 0,
  created_at                timestamptz NOT NULL DEFAULT now(),
  updated_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, profile_id) REFERENCES app.gl_posting_profiles (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, account_id) REFERENCES app.gl_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, item_posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id)
);
-- One rule per (role, key combination) in a profile; NULL keys count as "any", so they are folded for uniqueness.
CREATE UNIQUE INDEX gl_posting_rules_unique_idx ON app.gl_posting_rules (
  tenant_id, profile_id, account_role, coalesce(document_type, ''),
  coalesce(item_posting_group_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(partner_posting_group_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(tax_code_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(warehouse_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(branch_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(bank_account_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(asset_category_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(charge_type_id, '00000000-0000-0000-0000-000000000000'::uuid));
CALL app.enable_tenant_rls('app.gl_posting_rules');
CALL app.track_updated_at('app.gl_posting_rules');

ALTER TABLE app.org_companies
  ADD CONSTRAINT org_companies_posting_profile_fk FOREIGN KEY (tenant_id, posting_profile_id) REFERENCES app.gl_posting_profiles (tenant_id, id) DEFERRABLE INITIALLY IMMEDIATE;

-- ---------------------------------------------------------------------------------------------
-- Journal entries (append-only). The "reversed" state is a link row, never an update (ADR-0007).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.gl_journal_entries (
  tenant_id               uuid NOT NULL REFERENCES control.tenants (id),
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  number                  text NOT NULL,
  posting_date            date NOT NULL,
  document_date           date NOT NULL,
  fiscal_year_id          uuid NOT NULL,
  fiscal_period_id        uuid NOT NULL,
  source_module           text NOT NULL,
  source_document_type    text NOT NULL,
  source_document_id      uuid NOT NULL,
  source_document_number  text,
  description_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_reversal             boolean NOT NULL DEFAULT false,
  is_auto_reversal        boolean NOT NULL DEFAULT false,
  auto_reverse_on         date,
  is_closing_entry        boolean NOT NULL DEFAULT false,
  is_opening_entry        boolean NOT NULL DEFAULT false,
  is_manual               boolean NOT NULL DEFAULT false,
  currency_tc             text NOT NULL REFERENCES control.currencies (code),
  currency_fc             text NOT NULL REFERENCES control.currencies (code),
  currency_rc             text REFERENCES control.currencies (code),
  rate_type               text NOT NULL DEFAULT 'spot',
  rate_tc_fc              numeric(24, 12) NOT NULL CHECK (rate_tc_fc > 0),
  rate_fc_rc              numeric(24, 12) CHECK (rate_fc_rc IS NULL OR rate_fc_rc > 0),
  rate_override_reason    text,
  posting_profile_id      uuid,
  line_count              int NOT NULL CHECK (line_count >= 2),
  posted_by               uuid,
  posted_at               timestamptz NOT NULL DEFAULT now(),
  idempotency_key         text,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, fiscal_year_id) REFERENCES app.org_fiscal_years (tenant_id, id),
  FOREIGN KEY (tenant_id, fiscal_period_id) REFERENCES app.org_fiscal_periods (tenant_id, id),
  FOREIGN KEY (tenant_id, posting_profile_id) REFERENCES app.gl_posting_profiles (tenant_id, id)
);
CREATE UNIQUE INDEX gl_journal_entries_idempotency_idx ON app.gl_journal_entries (tenant_id, company_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX gl_journal_entries_company_date_idx ON app.gl_journal_entries (tenant_id, company_id, posting_date, id);
CREATE INDEX gl_journal_entries_source_idx ON app.gl_journal_entries (tenant_id, source_document_type, source_document_id);
CREATE INDEX gl_journal_entries_auto_reverse_idx ON app.gl_journal_entries (tenant_id, auto_reverse_on) WHERE auto_reverse_on IS NOT NULL;
CALL app.enable_tenant_rls('app.gl_journal_entries');
CALL app.make_append_only('app.gl_journal_entries');

-- Lines: partitioned by posting year (ADR-0007). The primary key carries the partition key, as PostgreSQL requires.
CREATE TABLE app.gl_journal_lines (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  id                uuid NOT NULL,
  entry_id          uuid NOT NULL,
  company_id        uuid NOT NULL,
  posting_date      date NOT NULL,
  line_no           int NOT NULL CHECK (line_no >= 1),
  account_id        uuid NOT NULL,
  account_role      text NOT NULL,
  posting_rule_id   uuid,
  debit_tc          numeric(24, 6) NOT NULL DEFAULT 0 CHECK (debit_tc >= 0),
  credit_tc         numeric(24, 6) NOT NULL DEFAULT 0 CHECK (credit_tc >= 0),
  currency_tc       text NOT NULL REFERENCES control.currencies (code),
  rate_tc_fc        numeric(24, 12) NOT NULL,
  debit_fc          numeric(24, 6) NOT NULL DEFAULT 0 CHECK (debit_fc >= 0),
  credit_fc         numeric(24, 6) NOT NULL DEFAULT 0 CHECK (credit_fc >= 0),
  rate_fc_rc        numeric(24, 12),
  debit_rc          numeric(24, 6) NOT NULL DEFAULT 0 CHECK (debit_rc >= 0),
  credit_rc         numeric(24, 6) NOT NULL DEFAULT 0 CHECK (credit_rc >= 0),
  rate_type         text NOT NULL DEFAULT 'spot',
  rate_date         date NOT NULL,
  dimension_set_id  uuid,
  branch_id         uuid,
  partner_id        uuid,
  subledger_type    text CHECK (subledger_type IN ('AR', 'AP', 'INV', 'FA', 'BANK', 'PDC', 'GRNI', 'IC', 'WHT')),
  subledger_ref     uuid,
  tax_code_id       uuid,
  tax_base_tc       numeric(24, 6),
  description_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  due_date          date,
  is_rounding       boolean NOT NULL DEFAULT false,
  PRIMARY KEY (tenant_id, posting_date, id),
  UNIQUE (tenant_id, posting_date, entry_id, line_no),
  FOREIGN KEY (tenant_id, entry_id) REFERENCES app.gl_journal_entries (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, account_id) REFERENCES app.gl_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id),
  -- One side only (rounding lines carry no transaction amount), the same for fc and rc.
  CHECK (debit_tc = 0 OR credit_tc = 0),
  CHECK (debit_fc = 0 OR credit_fc = 0),
  CHECK (debit_rc = 0 OR credit_rc = 0),
  CHECK (is_rounding OR debit_tc <> 0 OR credit_tc <> 0),
  -- A subledger reference comes with its type and nothing else (DOMAIN_MODEL §6.1 rule 3).
  CHECK ((subledger_type IS NULL) = (subledger_ref IS NULL))
) PARTITION BY RANGE (posting_date);
CREATE INDEX gl_journal_lines_account_idx ON app.gl_journal_lines (tenant_id, company_id, account_id, posting_date);
CREATE INDEX gl_journal_lines_entry_idx ON app.gl_journal_lines (tenant_id, entry_id);
CREATE INDEX gl_journal_lines_subledger_idx ON app.gl_journal_lines (tenant_id, subledger_type, subledger_ref) WHERE subledger_ref IS NOT NULL;
CALL app.enable_tenant_rls('app.gl_journal_lines');
CALL app.make_append_only('app.gl_journal_lines');

-- Yearly partitions, created ahead by the migration and on demand by the engine (SECURITY DEFINER: the
-- application role has no DDL).
CREATE OR REPLACE FUNCTION app.gl_ensure_partition(p_day date) RETURNS boolean
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp AS $$
DECLARE
  v_from date := date_trunc('year', p_day)::date;
  v_to   date := (date_trunc('year', p_day) + interval '1 year')::date;
  v_name text := 'gl_journal_lines_' || to_char(v_from, 'YYYY');
BEGIN
  IF to_regclass('app.' || v_name) IS NOT NULL THEN
    RETURN true;
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext('app.gl_journal_lines:partitions'));
  IF to_regclass('app.' || v_name) IS NOT NULL THEN
    RETURN true;
  END IF;
  EXECUTE format('CREATE TABLE app.%I (LIKE app.gl_journal_lines INCLUDING DEFAULTS INCLUDING CONSTRAINTS)', v_name);
  EXECUTE format('ALTER TABLE app.%I ENABLE ROW LEVEL SECURITY', v_name);
  EXECUTE format('ALTER TABLE app.%I FORCE ROW LEVEL SECURITY', v_name);
  EXECUTE format('CREATE POLICY tenant_isolation ON app.%I USING (tenant_id = app.current_tenant()) WITH CHECK (tenant_id = app.current_tenant())', v_name);
  EXECUTE format('REVOKE UPDATE, DELETE, TRUNCATE ON app.%I FROM quicker_app', v_name);
  EXECUTE format('ALTER TABLE app.gl_journal_lines ATTACH PARTITION app.%I FOR VALUES FROM (%L) TO (%L)', v_name, v_from, v_to);
  RETURN false;
END
$$;
REVOKE ALL ON FUNCTION app.gl_ensure_partition(date) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.gl_ensure_partition(date) TO quicker_app;

DO $$
DECLARE
  y date := date '2020-01-01';
BEGIN
  WHILE y < date '2031-01-01' LOOP
    PERFORM app.gl_ensure_partition(y);
    y := (y + interval '1 year')::date;
  END LOOP;
END
$$;

-- Balanced in every currency, and every line present, checked once per entry when the transaction commits.
CREATE OR REPLACE FUNCTION app.gl_assert_balanced() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
  v_count int;
  v_tc numeric;
  v_fc numeric;
  v_rc numeric;
BEGIN
  SELECT count(*), coalesce(sum(debit_tc - credit_tc), 0), coalesce(sum(debit_fc - credit_fc), 0), coalesce(sum(debit_rc - credit_rc), 0)
  INTO v_count, v_tc, v_fc, v_rc
  FROM app.gl_journal_lines l
  WHERE l.tenant_id = NEW.tenant_id AND l.entry_id = NEW.id;
  IF v_count <> NEW.line_count THEN
    RAISE EXCEPTION 'gl_entry_incomplete: entry % declares % lines, % written', NEW.number, NEW.line_count, v_count USING ERRCODE = '23514';
  END IF;
  IF v_tc <> 0 OR v_fc <> 0 OR v_rc <> 0 THEN
    RAISE EXCEPTION 'gl_entry_unbalanced: entry % is off by % (tc), % (fc), % (rc)', NEW.number, v_tc, v_fc, v_rc USING ERRCODE = '23514';
  END IF;
  RETURN NULL;
END
$$;
CREATE CONSTRAINT TRIGGER gl_entry_balanced AFTER INSERT ON app.gl_journal_entries
  DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION app.gl_assert_balanced();

CREATE TABLE app.gl_entry_links (
  tenant_id      uuid NOT NULL REFERENCES control.tenants (id),
  from_entry_id  uuid NOT NULL,
  to_entry_id    uuid NOT NULL,
  relation       text NOT NULL CHECK (relation IN ('reverses', 'corrects', 'auto_reversal_of', 'closing_of')),
  reason         text NOT NULL DEFAULT '',
  created_by     uuid,
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, from_entry_id, to_entry_id, relation),
  FOREIGN KEY (tenant_id, from_entry_id) REFERENCES app.gl_journal_entries (tenant_id, id),
  FOREIGN KEY (tenant_id, to_entry_id) REFERENCES app.gl_journal_entries (tenant_id, id),
  CHECK (from_entry_id <> to_entry_id)
);
-- A reversed entry cannot be reversed again (POSTING_RULES §9.4).
CREATE UNIQUE INDEX gl_entry_links_reversed_once_idx ON app.gl_entry_links (tenant_id, to_entry_id) WHERE relation = 'reverses';
CALL app.enable_tenant_rls('app.gl_entry_links');
CALL app.make_append_only('app.gl_entry_links');

-- Derived balances: company × account × fiscal period × transaction currency × dimension set (nil uuid = none).
CREATE TABLE app.gl_balances (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  company_id        uuid NOT NULL,
  account_id        uuid NOT NULL,
  fiscal_period_id  uuid NOT NULL,
  currency_tc       text NOT NULL,
  dimension_set_id  uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'::uuid,
  debit_tc          numeric(24, 6) NOT NULL DEFAULT 0,
  credit_tc         numeric(24, 6) NOT NULL DEFAULT 0,
  debit_fc          numeric(24, 6) NOT NULL DEFAULT 0,
  credit_fc         numeric(24, 6) NOT NULL DEFAULT 0,
  debit_rc          numeric(24, 6) NOT NULL DEFAULT 0,
  credit_rc         numeric(24, 6) NOT NULL DEFAULT 0,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, account_id) REFERENCES app.gl_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, fiscal_period_id) REFERENCES app.org_fiscal_periods (tenant_id, id)
);
CREATE INDEX gl_balances_period_idx ON app.gl_balances (tenant_id, company_id, fiscal_period_id);
CALL app.enable_tenant_rls('app.gl_balances');
