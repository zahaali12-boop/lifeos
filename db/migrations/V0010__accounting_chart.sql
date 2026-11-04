-- V0010: chart of accounts (ADR-0006, DOMAIN_MODEL §6, roadmap 2.1).
-- A chart is shared by the tenant's companies or dedicated to one; accounts form a tree of headers and postable
-- leaves typed asset/liability/equity/revenue/expense. Control accounts name the subledger they reconcile to.
-- Dimension rules say which dimensions a line on the account must, may or must not carry. Statutory charts are
-- platform reference data (control schema); a tenant maps its accounts to them for statutory reports.

CREATE TABLE app.gl_charts (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  code                 text NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  template_code        text,
  account_code_format  text NOT NULL DEFAULT '',
  company_id           uuid,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  -- Deferrable: a company points at its chart and a dedicated chart points at its company, so a tenant purge
  -- (demo reseed) must be able to delete both sides in one transaction.
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) DEFERRABLE INITIALLY IMMEDIATE
);
CALL app.enable_tenant_rls('app.gl_charts');
CALL app.track_updated_at('app.gl_charts');

CREATE TABLE app.gl_account_categories (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  code        text NOT NULL CHECK (code ~ '^[a-z][a-z0-9_]{0,31}$'),
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  statement   text NOT NULL CHECK (statement IN ('bs', 'pl', 'ocf')),
  sort_order  int NOT NULL DEFAULT 0,
  is_system   boolean NOT NULL DEFAULT false,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.gl_account_categories');
CALL app.track_updated_at('app.gl_account_categories');

CREATE TABLE app.gl_accounts (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  chart_id              uuid NOT NULL,
  parent_id             uuid,
  code                  text NOT NULL,
  name_i18n             jsonb NOT NULL DEFAULT '{}'::jsonb,
  type                  text NOT NULL CHECK (type IN ('asset', 'liability', 'equity', 'revenue', 'expense')),
  subtype               text NOT NULL DEFAULT '',
  category_id           uuid,
  is_header             boolean NOT NULL DEFAULT false,
  is_control            boolean NOT NULL DEFAULT false,
  subledger_type        text CHECK (subledger_type IN ('AR', 'AP', 'INV', 'FA', 'BANK', 'PDC', 'GRNI', 'IC', 'WHT')),
  currency_restriction  text REFERENCES control.currencies (code),
  allow_manual_posting  boolean NOT NULL DEFAULT true,
  revalue_fx            boolean NOT NULL DEFAULT false,
  cash_flow_category    text CHECK (cash_flow_category IN ('cash', 'operating', 'investing', 'financing')),
  default_role          text,
  company_id            uuid,
  is_active             boolean NOT NULL DEFAULT true,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, chart_id, code),
  FOREIGN KEY (tenant_id, chart_id) REFERENCES app.gl_charts (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, parent_id) REFERENCES app.gl_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.gl_account_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  CHECK (NOT is_control OR subledger_type IS NOT NULL),
  CHECK (NOT is_header OR NOT is_control)
);
CREATE INDEX gl_accounts_parent_idx ON app.gl_accounts (tenant_id, chart_id, parent_id);
CREATE INDEX gl_accounts_role_idx ON app.gl_accounts (tenant_id, chart_id, default_role) WHERE default_role IS NOT NULL;
CALL app.enable_tenant_rls('app.gl_accounts');
CALL app.track_updated_at('app.gl_accounts');

CREATE TABLE app.gl_account_dimension_rules (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  account_id        uuid NOT NULL,
  dimension_id      uuid NOT NULL,
  rule              text NOT NULL CHECK (rule IN ('required', 'optional', 'blocked')),
  default_value_id  uuid,
  PRIMARY KEY (tenant_id, account_id, dimension_id),
  FOREIGN KEY (tenant_id, account_id) REFERENCES app.gl_accounts (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, dimension_id) REFERENCES app.org_dimensions (tenant_id, id),
  FOREIGN KEY (tenant_id, default_value_id) REFERENCES app.org_dimension_values (tenant_id, id),
  CHECK (rule <> 'blocked' OR default_value_id IS NULL)
);
CALL app.enable_tenant_rls('app.gl_account_dimension_rules');

-- Statutory charts (Iraq Unified Accounting System and others) are reference data shared by every tenant.
CREATE TABLE control.gl_statutory_charts (
  code        text PRIMARY KEY,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  accounts    jsonb NOT NULL DEFAULT '[]'::jsonb,
  notes       text NOT NULL DEFAULT '',
  updated_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE app.gl_account_mappings (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  account_id            uuid NOT NULL,
  statutory_chart_code  text NOT NULL REFERENCES control.gl_statutory_charts (code),
  statutory_code        text NOT NULL,
  PRIMARY KEY (tenant_id, account_id, statutory_chart_code),
  FOREIGN KEY (tenant_id, account_id) REFERENCES app.gl_accounts (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.gl_account_mappings');

-- The company's chart (column reserved in V0004) now points at a real chart.
ALTER TABLE app.org_companies
  ADD CONSTRAINT org_companies_chart_fk FOREIGN KEY (tenant_id, chart_id) REFERENCES app.gl_charts (tenant_id, id) DEFERRABLE INITIALLY IMMEDIATE;
