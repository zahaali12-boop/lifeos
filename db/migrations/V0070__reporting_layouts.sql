-- Financial statement layouts (founder programme item 9, slice 9c; ADR-0034 §5, A-184): a statement is rows (headers,
-- account selections, formulas over other rows) by columns (a period, the year to date, the prior period or year, a
-- fixed range, the trial balance's opening/debits/credits/closing, variances), computed over the journal facts for
-- a company, a window and the reader's scopes. The shipped profit and loss, balance sheet and trial balance layouts
-- live in code; a tenant layout is a copy or a design of its own, listed on the statements screen when published.
CREATE TABLE app.rpt_financial_layouts (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  company_id        uuid,
  code              text NOT NULL CHECK (code ~ '^[A-Z][A-Z0-9_]{0,31}$'),
  kind              text NOT NULL CHECK (kind IN ('pl', 'bs', 'tb', 'cashflow', 'management')),
  name_i18n         jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  template_code     text,
  is_published      boolean NOT NULL DEFAULT true,
  is_active         boolean NOT NULL DEFAULT true,
  sort_order        integer NOT NULL DEFAULT 0,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CALL app.enable_tenant_rls('app.rpt_financial_layouts');
CALL app.track_updated_at('app.rpt_financial_layouts');

CREATE TABLE app.rpt_layout_rows (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  layout_id         uuid NOT NULL,
  sort_order        integer NOT NULL,
  code              text NOT NULL CHECK (code ~ '^[A-Z][A-Z0-9_]{0,31}$'),
  row_type          text NOT NULL CHECK (row_type IN ('header', 'accounts', 'formula', 'blank')),
  label_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  account_filter    jsonb NOT NULL DEFAULT '{}'::jsonb,
  dimension_filter  jsonb NOT NULL DEFAULT '{}'::jsonb,
  formula           text,
  flip_sign         boolean NOT NULL DEFAULT false,
  indent            integer NOT NULL DEFAULT 0,
  bold              boolean NOT NULL DEFAULT false,
  show_accounts     boolean NOT NULL DEFAULT false,
  hide_when_zero    boolean NOT NULL DEFAULT false,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, layout_id, code),
  FOREIGN KEY (tenant_id, layout_id) REFERENCES app.rpt_financial_layouts (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.rpt_layout_rows');

CREATE TABLE app.rpt_layout_columns (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  layout_id      uuid NOT NULL,
  sort_order     integer NOT NULL,
  code           text NOT NULL CHECK (code ~ '^[A-Z][A-Z0-9_]{0,31}$'),
  column_type    text NOT NULL CHECK (column_type IN ('period', 'ytd', 'prior_period', 'prior_year', 'custom_range', 'opening', 'debits', 'credits', 'closing', 'variance', 'variance_pct')),
  label_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  parameters     jsonb NOT NULL DEFAULT '{}'::jsonb,
  currency_mode  text NOT NULL DEFAULT 'functional' CHECK (currency_mode IN ('functional', 'reporting')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, layout_id, code),
  FOREIGN KEY (tenant_id, layout_id) REFERENCES app.rpt_financial_layouts (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.rpt_layout_columns');
