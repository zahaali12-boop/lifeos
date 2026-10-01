-- V0004: organization (ADR-0005, ADR-0011, ADR-0017, ADR-0026).
-- Companies, branches, fiscal calendars with per-module period states, currencies and effective-dated
-- exchange rates, dimensions and dimension sets, units of measure, business calendars and settings.

-- ---------------------------------------------------------------------------------------------
-- ISO 4217 reference data: platform-wide, seeded by db/seed (A-064). Tenants enable currencies per
-- company in app.org_company_currencies with their own display precision and cash rounding.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE control.currencies (
  code          text PRIMARY KEY CHECK (code ~ '^[A-Z]{3}$'),
  numeric_code  text NOT NULL DEFAULT '',
  minor_units   int NOT NULL CHECK (minor_units BETWEEN 0 AND 4),
  symbol        text NOT NULL DEFAULT '',
  name_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active     boolean NOT NULL DEFAULT true
);
-- Reference data is maintained by seeds, never by the application.
REVOKE INSERT, UPDATE, DELETE, TRUNCATE ON control.currencies FROM quicker_app;

-- ---------------------------------------------------------------------------------------------
-- Fiscal calendars: years and periods are ordinary data; the state of a period is per company and
-- per module. A missing state row means the default for the year's status (see the resolver).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_fiscal_calendars (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  id                uuid NOT NULL,
  code              text NOT NULL,
  name_i18n         jsonb NOT NULL DEFAULT '{}'::jsonb,
  start_month       int NOT NULL CHECK (start_month BETWEEN 1 AND 12),
  periods_per_year  int NOT NULL CHECK (periods_per_year IN (12, 13)),
  is_system         boolean NOT NULL DEFAULT false,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.org_fiscal_calendars');
CALL app.track_updated_at('app.org_fiscal_calendars');

CREATE TABLE app.org_fiscal_years (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  calendar_id       uuid NOT NULL,
  code              text NOT NULL,
  starts_on         date NOT NULL,
  ends_on           date NOT NULL,
  status            text NOT NULL DEFAULT 'future' CHECK (status IN ('future', 'open', 'closed')),
  closing_entry_id  uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, calendar_id, code),
  FOREIGN KEY (tenant_id, calendar_id) REFERENCES app.org_fiscal_calendars (tenant_id, id) ON DELETE CASCADE,
  CHECK (ends_on > starts_on),
  EXCLUDE USING gist (tenant_id WITH =, calendar_id WITH =, daterange(starts_on, ends_on, '[]') WITH &&)
);
CALL app.enable_tenant_rls('app.org_fiscal_years');
CALL app.track_updated_at('app.org_fiscal_years');

CREATE TABLE app.org_fiscal_periods (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  fiscal_year_id  uuid NOT NULL,
  number          int NOT NULL CHECK (number BETWEEN 1 AND 13),
  starts_on       date NOT NULL,
  ends_on         date NOT NULL,
  is_adjustment   boolean NOT NULL DEFAULT false,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, fiscal_year_id, number),
  FOREIGN KEY (tenant_id, fiscal_year_id) REFERENCES app.org_fiscal_years (tenant_id, id) ON DELETE CASCADE,
  CHECK (ends_on >= starts_on)
);
CALL app.enable_tenant_rls('app.org_fiscal_periods');

-- ---------------------------------------------------------------------------------------------
-- Business calendars: working days (0 = Sunday … 6 = Saturday) and public holidays (A-005).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_business_calendars (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  id            uuid NOT NULL,
  code          text NOT NULL,
  name_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  working_days  int[] NOT NULL CHECK (cardinality(working_days) BETWEEN 1 AND 7 AND working_days <@ ARRAY[0, 1, 2, 3, 4, 5, 6]),
  is_system     boolean NOT NULL DEFAULT false,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.org_business_calendars');
CALL app.track_updated_at('app.org_business_calendars');

CREATE TABLE app.org_holidays (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  calendar_id   uuid NOT NULL,
  on_date       date NOT NULL,
  name_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, calendar_id, on_date),
  FOREIGN KEY (tenant_id, calendar_id) REFERENCES app.org_business_calendars (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.org_holidays');

-- ---------------------------------------------------------------------------------------------
-- Companies and branches. chart_id and posting_profile_id reference Accounting (slice 1.7) and
-- gain their foreign keys there.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_companies (
  tenant_id                  uuid NOT NULL REFERENCES control.tenants (id),
  id                         uuid NOT NULL,
  code                       text NOT NULL,
  legal_name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  trade_name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  country                    text NOT NULL CHECK (country ~ '^[A-Z]{2}$'),
  functional_currency        text NOT NULL REFERENCES control.currencies (code),
  reporting_currency         text REFERENCES control.currencies (code),
  chart_id                   uuid,
  fiscal_calendar_id         uuid NOT NULL,
  business_calendar_id       uuid NOT NULL,
  time_zone                  text NOT NULL,
  default_language           text NOT NULL DEFAULT 'en',
  costing_method             text NOT NULL DEFAULT 'average' CHECK (costing_method IN ('fifo', 'average', 'standard')),
  costing_scope              text NOT NULL DEFAULT 'company' CHECK (costing_scope IN ('company', 'warehouse')),
  revenue_recognition_point  text NOT NULL DEFAULT 'invoice' CHECK (revenue_recognition_point IN ('invoice', 'shipment')),
  tax_rounding_mode          text NOT NULL DEFAULT 'line' CHECK (tax_rounding_mode IN ('line', 'document')),
  rounding_mode              text NOT NULL DEFAULT 'half_away' CHECK (rounding_mode IN ('half_away', 'half_even')),
  negative_stock_policy      text NOT NULL DEFAULT 'block' CHECK (negative_stock_policy IN ('block', 'allow', 'approve')),
  bank_revaluation_mode      text NOT NULL DEFAULT 'permanent' CHECK (bank_revaluation_mode IN ('permanent', 'reversing')),
  posting_profile_id         uuid,
  registration_numbers       jsonb NOT NULL DEFAULT '{}'::jsonb,
  address                    jsonb NOT NULL DEFAULT '{}'::jsonb,
  custom_fields              jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active                  boolean NOT NULL DEFAULT true,
  created_at                 timestamptz NOT NULL DEFAULT now(),
  updated_at                 timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, fiscal_calendar_id) REFERENCES app.org_fiscal_calendars (tenant_id, id),
  FOREIGN KEY (tenant_id, business_calendar_id) REFERENCES app.org_business_calendars (tenant_id, id)
);
CALL app.enable_tenant_rls('app.org_companies');
CALL app.track_updated_at('app.org_companies');

-- ---------------------------------------------------------------------------------------------
-- Dimensions. BRANCH, COST_CENTER, DEPARTMENT and PROJECT are system dimensions created per tenant;
-- a branch always has its BRANCH value. Sets deduplicate combinations by hash (DOMAIN_MODEL §5).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_dimensions (
  tenant_id        uuid NOT NULL REFERENCES control.tenants (id),
  id               uuid NOT NULL,
  code             text NOT NULL CHECK (code ~ '^[A-Z][A-Z0-9_]{0,31}$'),
  name_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_system        boolean NOT NULL DEFAULT false,
  is_hierarchical  boolean NOT NULL DEFAULT false,
  sort_order       int NOT NULL DEFAULT 0,
  is_active        boolean NOT NULL DEFAULT true,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.org_dimensions');
CALL app.track_updated_at('app.org_dimensions');

CREATE TABLE app.org_dimension_values (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  dimension_id         uuid NOT NULL,
  parent_id            uuid,
  code                 text NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  company_id           uuid,
  owner_membership_id  uuid,
  valid_from           date,
  valid_to             date,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, dimension_id, code),
  FOREIGN KEY (tenant_id, dimension_id) REFERENCES app.org_dimensions (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, parent_id) REFERENCES app.org_dimension_values (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.org_dimension_values');
CALL app.track_updated_at('app.org_dimension_values');

CREATE TABLE app.org_dimension_sets (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  hash        bytea NOT NULL,
  values      jsonb NOT NULL,
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, hash)
);
CALL app.enable_tenant_rls('app.org_dimension_sets');

CREATE TABLE app.org_branches (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  code                text NOT NULL,
  name_i18n           jsonb NOT NULL DEFAULT '{}'::jsonb,
  address             jsonb NOT NULL DEFAULT '{}'::jsonb,
  tax_registrations   jsonb NOT NULL DEFAULT '{}'::jsonb,
  dimension_value_id  uuid NOT NULL,
  is_active           boolean NOT NULL DEFAULT true,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  UNIQUE (tenant_id, dimension_value_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, dimension_value_id) REFERENCES app.org_dimension_values (tenant_id, id)
);
CALL app.enable_tenant_rls('app.org_branches');
CALL app.track_updated_at('app.org_branches');

-- ---------------------------------------------------------------------------------------------
-- Period states per company and module (ADR-0026). Rows exist only where a state was set.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_period_module_states (
  tenant_id   uuid NOT NULL,
  period_id   uuid NOT NULL,
  company_id  uuid NOT NULL,
  module      text NOT NULL CHECK (module IN ('GL', 'AR', 'AP', 'INV', 'FA', 'BANK', 'TAX')),
  state       text NOT NULL CHECK (state IN ('open', 'soft_closed', 'hard_closed', 'never_opened')),
  changed_by  uuid,
  changed_at  timestamptz NOT NULL DEFAULT now(),
  reason      text,
  PRIMARY KEY (tenant_id, period_id, company_id, module),
  FOREIGN KEY (tenant_id, period_id) REFERENCES app.org_fiscal_periods (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.org_period_module_states');

-- ---------------------------------------------------------------------------------------------
-- Currencies per company, rate types and effective-dated rates (ADR-0017): 1 from = rate to.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_company_currencies (
  tenant_id                uuid NOT NULL,
  company_id               uuid NOT NULL,
  currency                 text NOT NULL REFERENCES control.currencies (code),
  display_decimals         int NOT NULL CHECK (display_decimals BETWEEN 0 AND 6),
  cash_rounding_increment  numeric(20, 6) NOT NULL DEFAULT 0 CHECK (cash_rounding_increment >= 0),
  is_enabled               boolean NOT NULL DEFAULT true,
  PRIMARY KEY (tenant_id, company_id, currency),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.org_company_currencies');

CREATE TABLE app.org_exchange_rate_types (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  code        text NOT NULL CHECK (code ~ '^[a-z][a-z0-9_]{0,31}$'),
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_system   boolean NOT NULL DEFAULT false,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.org_exchange_rate_types');
CALL app.track_updated_at('app.org_exchange_rate_types');

CREATE TABLE app.org_exchange_rates (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  rate_type_id   uuid NOT NULL,
  from_currency  text NOT NULL REFERENCES control.currencies (code),
  to_currency    text NOT NULL REFERENCES control.currencies (code),
  valid_from     date NOT NULL,
  rate           numeric(24, 12) NOT NULL CHECK (rate > 0),
  source         text NOT NULL DEFAULT 'manual',
  entered_by     uuid,
  reason         text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, rate_type_id, from_currency, to_currency, valid_from),
  FOREIGN KEY (tenant_id, rate_type_id) REFERENCES app.org_exchange_rate_types (tenant_id, id),
  CHECK (from_currency <> to_currency)
);
CREATE INDEX org_exchange_rates_lookup_idx ON app.org_exchange_rates (tenant_id, rate_type_id, from_currency, to_currency, valid_from DESC);
CALL app.enable_tenant_rls('app.org_exchange_rates');

-- ---------------------------------------------------------------------------------------------
-- Units of measure and conversions (rational factors, ADR-0005).
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_uoms (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  family      text NOT NULL CHECK (family IN ('count', 'weight', 'volume', 'length', 'area', 'time', 'other')),
  precision   int NOT NULL DEFAULT 0 CHECK (precision BETWEEN 0 AND 9),
  is_system   boolean NOT NULL DEFAULT false,
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.org_uoms');
CALL app.track_updated_at('app.org_uoms');

CREATE TABLE app.org_uom_conversions (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  from_uom_id  uuid NOT NULL,
  to_uom_id    uuid NOT NULL,
  numerator    numeric(24, 12) NOT NULL CHECK (numerator > 0),
  denominator  numeric(24, 12) NOT NULL CHECK (denominator > 0),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, from_uom_id, to_uom_id),
  FOREIGN KEY (tenant_id, from_uom_id) REFERENCES app.org_uoms (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, to_uom_id) REFERENCES app.org_uoms (tenant_id, id) ON DELETE CASCADE,
  CHECK (from_uom_id <> to_uom_id)
);
CALL app.enable_tenant_rls('app.org_uom_conversions');

-- ---------------------------------------------------------------------------------------------
-- Settings: typed key/value per tenant (company_id null) or per company.
-- ---------------------------------------------------------------------------------------------
CREATE TABLE app.org_settings (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  company_id  uuid,
  key         text NOT NULL CHECK (key ~ '^[a-z][a-z0-9_.]{0,127}$'),
  value       jsonb NOT NULL,
  value_type  text NOT NULL CHECK (value_type IN ('string', 'number', 'boolean', 'json')),
  updated_by  uuid,
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, key),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.org_settings');
