-- Organization schema: companies, branches, calendars, currencies, rates, dimensions, UoM (M1.6)
-- ADR-0011 (identifiers), ADR-0017 (multi-currency), ADR-0026 (periods), ADR-0027 (localization).

-- ================================================================ currencies (control plane, shared)

CREATE TABLE IF NOT EXISTS control.org_currencies (
  code text PRIMARY KEY,
  numeric_code text NOT NULL,
  minor_units int NOT NULL CHECK (minor_units >= 0 AND minor_units <= 4),
  symbol text,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active bool NOT NULL DEFAULT true
);

COMMENT ON TABLE control.org_currencies IS 'ISO 4217 currencies, seeded at migration time. Shared across all tenants.';
COMMENT ON COLUMN control.org_currencies.code IS 'Three-letter ISO 4217 code (e.g. "IQD", "USD").';
COMMENT ON COLUMN control.org_currencies.minor_units IS 'Decimal places of the minor unit (0, 2, 3, etc).';
COMMENT ON COLUMN control.org_currencies.name_i18n IS 'Bilingual name: {"en": "...", "ar": "..."}.';

-- ================================================================ organization schema (tenant-scoped)

CREATE TABLE IF NOT EXISTS app.org_companies (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  code text NOT NULL,
  legal_name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  trade_name_i18n jsonb DEFAULT '{}'::jsonb,
  country text NOT NULL,
  functional_currency text NOT NULL REFERENCES control.org_currencies(code),
  reporting_currency text REFERENCES control.org_currencies(code),
  fiscal_calendar_id uuid,
  business_calendar_id uuid,
  time_zone text NOT NULL DEFAULT 'UTC',
  default_language text NOT NULL DEFAULT 'en',
  costing_method text NOT NULL DEFAULT 'fifo' CHECK (costing_method IN ('fifo', 'average', 'standard')),
  costing_scope text NOT NULL DEFAULT 'company' CHECK (costing_scope IN ('company', 'warehouse')),
  revenue_recognition_point text NOT NULL DEFAULT 'invoice' CHECK (revenue_recognition_point IN ('invoice', 'shipment')),
  tax_rounding_mode text NOT NULL DEFAULT 'line' CHECK (tax_rounding_mode IN ('line', 'document')),
  rounding_mode text NOT NULL DEFAULT 'half_away' CHECK (rounding_mode IN ('half_away', 'half_even')),
  negative_stock_policy text NOT NULL DEFAULT 'block' CHECK (negative_stock_policy IN ('block', 'allow', 'approve')),
  bank_revaluation_mode text NOT NULL DEFAULT 'permanent' CHECK (bank_revaluation_mode IN ('permanent', 'reversing')),
  registration_numbers jsonb DEFAULT '{}'::jsonb,
  address jsonb DEFAULT '{}'::jsonb,
  custom_fields jsonb DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_companies_tenant ON app.org_companies(tenant_id);
ALTER TABLE app.org_companies ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_companies_isolation ON app.org_companies USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_companies IS 'Legal entities that post journals and keep balances. One tenant may operate one or more companies.';
COMMENT ON COLUMN app.org_companies.code IS 'Human-readable code, unique per tenant (e.g. "MAIN", "IMP").';
COMMENT ON COLUMN app.org_companies.functional_currency IS 'The company''s native accounting currency (immutable after first posting).';

CREATE TABLE IF NOT EXISTS app.org_fiscal_calendars (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  code text NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  start_month int NOT NULL CHECK (start_month >= 1 AND start_month <= 12),
  periods_per_year int NOT NULL DEFAULT 12 CHECK (periods_per_year IN (12, 13)),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_fiscal_calendars_tenant ON app.org_fiscal_calendars(tenant_id);
ALTER TABLE app.org_fiscal_calendars ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_fiscal_calendars_isolation ON app.org_fiscal_calendars USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_fiscal_calendars IS 'Fiscal year templates: calendar month to start and 12 or 13 periods.';

CREATE TABLE IF NOT EXISTS app.org_fiscal_years (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  calendar_id uuid NOT NULL REFERENCES app.org_fiscal_calendars(id) ON DELETE RESTRICT,
  code text NOT NULL,
  starts_on date NOT NULL,
  ends_on date NOT NULL,
  status text NOT NULL DEFAULT 'future' CHECK (status IN ('future', 'open', 'closed')),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE,
  CHECK (starts_on < ends_on)
);

CREATE INDEX ix_org_fiscal_years_tenant_calendar ON app.org_fiscal_years(tenant_id, calendar_id);
ALTER TABLE app.org_fiscal_years ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_fiscal_years_isolation ON app.org_fiscal_years USING (tenant_id = current_setting('app.tenant_id')::uuid);

CREATE TABLE IF NOT EXISTS app.org_fiscal_periods (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  fiscal_year_id uuid NOT NULL REFERENCES app.org_fiscal_years(id) ON DELETE CASCADE,
  number int NOT NULL CHECK (number >= 1 AND number <= 13),
  starts_on date NOT NULL,
  ends_on date NOT NULL,
  is_adjustment bool NOT NULL DEFAULT false,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, fiscal_year_id, number),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE,
  CHECK (starts_on < ends_on)
);

CREATE INDEX ix_org_fiscal_periods_tenant_year ON app.org_fiscal_periods(tenant_id, fiscal_year_id);
ALTER TABLE app.org_fiscal_periods ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_fiscal_periods_isolation ON app.org_fiscal_periods USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_fiscal_periods IS 'Periods within a fiscal year, indexed by number 1..12 or 1..13. Period 13 is the adjustment (closing) period if used.';

CREATE TABLE IF NOT EXISTS app.org_period_module_states (
  tenant_id uuid NOT NULL,
  period_id uuid NOT NULL REFERENCES app.org_fiscal_periods(id) ON DELETE CASCADE,
  company_id uuid NOT NULL REFERENCES app.org_companies(id) ON DELETE CASCADE,
  module text NOT NULL CHECK (module IN ('GL', 'AR', 'AP', 'INV', 'FA', 'BANK', 'TAX')),
  state text NOT NULL DEFAULT 'open' CHECK (state IN ('open', 'soft_closed', 'hard_closed', 'never_opened')),
  changed_by uuid,
  reason text,
  changed_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, period_id, company_id, module),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE,
  PRIMARY KEY (tenant_id, period_id, company_id, module)
);

ALTER TABLE app.org_period_module_states ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_period_module_states_isolation ON app.org_period_module_states USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_period_module_states IS 'Period state per company and module (GL/AR/AP/INV/FA/BANK/TAX). Tracks which modules are open/soft-closed/hard-closed.';

-- ================================================================ exchange rates

CREATE TABLE IF NOT EXISTS app.org_exchange_rate_types (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  code text NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_system bool NOT NULL DEFAULT false,
  sort_order int DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_exchange_rate_types_tenant ON app.org_exchange_rate_types(tenant_id);
ALTER TABLE app.org_exchange_rate_types ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_exchange_rate_types_isolation ON app.org_exchange_rate_types USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_exchange_rate_types IS 'Rate types: spot, closing, average, budget, or custom (official, market).';

CREATE TABLE IF NOT EXISTS app.org_exchange_rates (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  rate_type_id uuid NOT NULL REFERENCES app.org_exchange_rate_types(id) ON DELETE RESTRICT,
  from_currency text NOT NULL REFERENCES control.org_currencies(code),
  to_currency text NOT NULL REFERENCES control.org_currencies(code),
  valid_from date NOT NULL,
  rate numeric(24, 12) NOT NULL CHECK (rate > 0),
  source text NOT NULL DEFAULT 'manual',
  entered_by uuid,
  reason text,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, rate_type_id, from_currency, to_currency, valid_from),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE,
  CHECK (from_currency <> to_currency)
);

CREATE INDEX ix_org_exchange_rates_tenant_rate_type_date ON app.org_exchange_rates(tenant_id, rate_type_id, from_currency, to_currency, valid_from DESC);
ALTER TABLE app.org_exchange_rates ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_exchange_rates_isolation ON app.org_exchange_rates USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_exchange_rates IS 'Dated exchange rates. 1 from_currency = rate to_currency. Effective-dated with exclusion constraints.';

-- ================================================================ dimensions and values

CREATE TABLE IF NOT EXISTS app.org_dimensions (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  code text NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_system bool NOT NULL DEFAULT false,
  is_hierarchical bool NOT NULL DEFAULT false,
  sort_order int DEFAULT 0,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_dimensions_tenant ON app.org_dimensions(tenant_id);
ALTER TABLE app.org_dimensions ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_dimensions_isolation ON app.org_dimensions USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_dimensions IS 'Analytic dimensions: BRANCH (auto-created), COST_CENTER, DEPARTMENT, PROJECT, or custom.';

CREATE TABLE IF NOT EXISTS app.org_dimension_values (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  dimension_id uuid NOT NULL REFERENCES app.org_dimensions(id) ON DELETE CASCADE,
  parent_id uuid REFERENCES app.org_dimension_values(id) ON DELETE SET NULL,
  code text NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  company_id uuid REFERENCES app.org_companies(id) ON DELETE CASCADE,
  owner_membership_id uuid,
  valid_from date,
  valid_to date,
  is_active bool NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, dimension_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_dimension_values_tenant_dimension ON app.org_dimension_values(tenant_id, dimension_id);
CREATE INDEX ix_org_dimension_values_active ON app.org_dimension_values(tenant_id, dimension_id, is_active) WHERE is_active = true;
ALTER TABLE app.org_dimension_values ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_dimension_values_isolation ON app.org_dimension_values USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_dimension_values IS 'Values of a dimension. Hierarchical (parent_id), optionally company-scoped, with effective dates.';

CREATE TABLE IF NOT EXISTS app.org_dimension_sets (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  hash bytea UNIQUE NOT NULL,
  values jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_dimension_sets_tenant_hash ON app.org_dimension_sets(tenant_id, hash);
ALTER TABLE app.org_dimension_sets ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_dimension_sets_isolation ON app.org_dimension_sets USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_dimension_sets IS 'Deduplicated dimension combinations by hash. Journal lines reference a set_id instead of individual dimensions.';

-- ================================================================ branches (auto-create BRANCH dimension)

CREATE TABLE IF NOT EXISTS app.org_branches (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  company_id uuid NOT NULL REFERENCES app.org_companies(id) ON DELETE CASCADE,
  code text NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  address jsonb DEFAULT '{}'::jsonb,
  tax_registrations jsonb DEFAULT '{}'::jsonb,
  dimension_value_id uuid,
  is_active bool NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  updated_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, company_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_branches_tenant_company ON app.org_branches(tenant_id, company_id);
ALTER TABLE app.org_branches ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_branches_isolation ON app.org_branches USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_branches IS 'Organizational units and automatic BRANCH dimension values.';

-- ================================================================ UoM

CREATE TABLE IF NOT EXISTS app.org_uoms (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  code text NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  family text NOT NULL CHECK (family IN ('count', 'weight', 'volume', 'length', 'time', 'other')),
  precision int NOT NULL DEFAULT 0 CHECK (precision >= 0 AND precision <= 9),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, code),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_uoms_tenant ON app.org_uoms(tenant_id);
ALTER TABLE app.org_uoms ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_uoms_isolation ON app.org_uoms USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_uoms IS 'Units of measure (PCS, KG, CTN, etc.) with precision and family.';

CREATE TABLE IF NOT EXISTS app.org_uom_conversions (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  from_uom_id uuid NOT NULL REFERENCES app.org_uoms(id) ON DELETE CASCADE,
  to_uom_id uuid NOT NULL REFERENCES app.org_uoms(id) ON DELETE CASCADE,
  numerator numeric(24, 9) NOT NULL CHECK (numerator > 0),
  denominator numeric(24, 9) NOT NULL CHECK (denominator > 0),
  created_at timestamptz NOT NULL DEFAULT now(),
  UNIQUE(tenant_id, from_uom_id, to_uom_id),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE,
  CHECK (from_uom_id <> to_uom_id)
);

CREATE INDEX ix_org_uom_conversions_tenant ON app.org_uom_conversions(tenant_id);
ALTER TABLE app.org_uom_conversions ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_uom_conversions_isolation ON app.org_uom_conversions USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_uom_conversions IS 'Rational conversion factors: 1 from = (numerator / denominator) to.';

-- ================================================================ business calendars

CREATE TABLE IF NOT EXISTS app.org_business_calendars (
  tenant_id uuid NOT NULL,
  id uuid PRIMARY KEY,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  working_days jsonb NOT NULL DEFAULT '{"sun": true, "mon": true, "tue": true, "wed": true, "thu": true, "fri": false, "sat": false}'::jsonb,
  created_at timestamptz NOT NULL DEFAULT now(),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

CREATE INDEX ix_org_business_calendars_tenant ON app.org_business_calendars(tenant_id);
ALTER TABLE app.org_business_calendars ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_business_calendars_isolation ON app.org_business_calendars USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_business_calendars IS 'Working days per week (sun-sat as bool). Used for due-date and delivery-date calculations.';

CREATE TABLE IF NOT EXISTS app.org_holidays (
  tenant_id uuid NOT NULL,
  calendar_id uuid NOT NULL REFERENCES app.org_business_calendars(id) ON DELETE CASCADE,
  on_date date NOT NULL,
  name_i18n jsonb NOT NULL DEFAULT '{}'::jsonb,
  PRIMARY KEY(tenant_id, calendar_id, on_date),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

ALTER TABLE app.org_holidays ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_holidays_isolation ON app.org_holidays USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_holidays IS 'Public holidays per calendar.';

-- ================================================================ company currencies (overrides)

CREATE TABLE IF NOT EXISTS app.org_company_currencies (
  tenant_id uuid NOT NULL,
  company_id uuid NOT NULL REFERENCES app.org_companies(id) ON DELETE CASCADE,
  currency text NOT NULL REFERENCES control.org_currencies(code),
  display_decimals int CHECK (display_decimals >= 0 AND display_decimals <= 4),
  cash_rounding_increment numeric(20, 6),
  is_enabled bool NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY(tenant_id, company_id, currency),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

ALTER TABLE app.org_company_currencies ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_company_currencies_isolation ON app.org_company_currencies USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_company_currencies IS 'Currencies enabled for a company, with display precision and cash-rounding overrides.';

-- ================================================================ settings (key-value)

CREATE TABLE IF NOT EXISTS app.org_settings (
  tenant_id uuid NOT NULL,
  company_id uuid REFERENCES app.org_companies(id) ON DELETE CASCADE,
  key text NOT NULL,
  value jsonb,
  value_type text DEFAULT 'json',
  PRIMARY KEY(tenant_id, company_id, key),
  FOREIGN KEY (tenant_id) REFERENCES control.tenants(id) ON DELETE CASCADE
);

ALTER TABLE app.org_settings ENABLE ROW LEVEL SECURITY;
CREATE POLICY org_settings_isolation ON app.org_settings USING (tenant_id = current_setting('app.tenant_id')::uuid);

COMMENT ON TABLE app.org_settings IS 'Configuration settings per company (or global if company_id is NULL).';
