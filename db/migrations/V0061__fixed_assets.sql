-- Fixed assets module (founder programme item 3, roadmap 6.6, A-175): books, asset categories, the register, the
-- values and depreciation plan per book, the FA subledger (fa_transactions, append-only), depreciation runs,
-- production usage and physical verifications. Every financial effect goes through the posting engine; the control
-- accounts of the FaCost, FaAccDep and FaAccImpairment roles equal the subledger at any moment (harness check).

CREATE TABLE app.fa_books (
  tenant_id    uuid NOT NULL,
  id           uuid NOT NULL,
  company_id   uuid NOT NULL,
  code         text NOT NULL,
  name_i18n    jsonb NOT NULL DEFAULT '{}'::jsonb,
  posts_to_gl  boolean NOT NULL DEFAULT false,
  is_active    boolean NOT NULL DEFAULT true,
  created_at   timestamptz NOT NULL DEFAULT now(),
  updated_at   timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
-- One book per company posts to the ledger.
CREATE UNIQUE INDEX fa_books_gl_idx ON app.fa_books (tenant_id, company_id) WHERE posts_to_gl;
CALL app.enable_tenant_rls('app.fa_books');
CALL app.track_updated_at('app.fa_books');

CREATE TABLE app.fa_asset_categories (
  tenant_id                              uuid NOT NULL,
  id                                     uuid NOT NULL,
  code                                   text NOT NULL,
  name_i18n                              jsonb NOT NULL DEFAULT '{}'::jsonb,
  default_method                         text NOT NULL CHECK (default_method IN ('straight_line', 'declining_balance', 'double_declining', 'sum_of_years', 'units_of_production', 'none')),
  default_life_months                    integer NOT NULL CHECK (default_life_months >= 0),
  default_salvage_pct                    numeric(9,4) NOT NULL DEFAULT 0 CHECK (default_salvage_pct >= 0 AND default_salvage_pct < 100),
  default_rate_pct                       numeric(9,4) CHECK (default_rate_pct IS NULL OR default_rate_pct > 0),
  convention                             text NOT NULL CHECK (convention IN ('full_month', 'mid_month', 'daily', 'next_month')),
  cost_account_code                      text,
  accumulated_depreciation_account_code  text,
  depreciation_expense_account_code      text,
  is_active                              boolean NOT NULL DEFAULT true,
  created_at                             timestamptz NOT NULL DEFAULT now(),
  updated_at                             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.fa_asset_categories');
CALL app.track_updated_at('app.fa_asset_categories');

CREATE TABLE app.fa_assets (
  tenant_id                          uuid NOT NULL,
  id                                 uuid NOT NULL,
  company_id                         uuid NOT NULL,
  number                             text NOT NULL,
  name_i18n                          jsonb NOT NULL DEFAULT '{}'::jsonb,
  description                        text,
  category_id                        uuid NOT NULL,
  parent_asset_id                    uuid,
  acquisition_date                   date NOT NULL,
  in_service_date                    date NOT NULL,
  cost                               numeric(24,6) NOT NULL DEFAULT 0 CHECK (cost >= 0),
  currency                           text NOT NULL,
  quantity                           numeric(18,4) NOT NULL DEFAULT 1 CHECK (quantity > 0),
  unit                               text,
  branch_id                          uuid,
  dimension_set_id                   uuid,
  custodian_membership_id            uuid,
  serial_number                      text,
  supplier_partner_id                uuid,
  supplier_reference                 text,
  warranty_until                     date,
  status                             text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'active', 'fully_depreciated', 'disposed')),
  acquisition_source                 text NOT NULL CHECK (acquisition_source IN ('supplier', 'bank', 'clearing', 'opening')),
  acquisition_bank_account_id        uuid,
  acquisition_tax_code_id            uuid,
  acquisition_due_date               date,
  opening_accumulated_depreciation   numeric(24,6) NOT NULL DEFAULT 0 CHECK (opening_accumulated_depreciation >= 0),
  opening_depreciated_through        date,
  acquisition_entry_id               uuid,
  disposed_on                        date,
  disposal_kind                      text CHECK (disposal_kind IS NULL OR disposal_kind IN ('sale', 'scrap')),
  notes                              text,
  custom_fields                      jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_by                         uuid,
  created_at                         timestamptz NOT NULL DEFAULT now(),
  updated_at                         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.fa_asset_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, parent_asset_id) REFERENCES app.fa_assets (tenant_id, id),
  FOREIGN KEY (tenant_id, supplier_partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, acquisition_bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, acquisition_tax_code_id) REFERENCES app.tax_codes (tenant_id, id),
  FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id)
);
CREATE INDEX fa_assets_company_idx ON app.fa_assets (tenant_id, company_id, status);
CREATE INDEX fa_assets_category_idx ON app.fa_assets (tenant_id, category_id);
CALL app.enable_tenant_rls('app.fa_assets');
CALL app.track_updated_at('app.fa_assets');

CREATE TABLE app.fa_asset_books (
  tenant_id                 uuid NOT NULL,
  id                        uuid NOT NULL,
  asset_id                  uuid NOT NULL,
  book_id                   uuid NOT NULL,
  method                    text NOT NULL CHECK (method IN ('straight_line', 'declining_balance', 'double_declining', 'sum_of_years', 'units_of_production', 'none')),
  life_months               integer NOT NULL CHECK (life_months >= 0),
  salvage_value             numeric(24,6) NOT NULL DEFAULT 0 CHECK (salvage_value >= 0),
  rate_pct                  numeric(9,4),
  convention                text NOT NULL CHECK (convention IN ('full_month', 'mid_month', 'daily', 'next_month')),
  depreciation_start        date NOT NULL,
  total_units               numeric(24,6),
  cost                      numeric(24,6) NOT NULL DEFAULT 0,
  accumulated_depreciation  numeric(24,6) NOT NULL DEFAULT 0,
  accumulated_impairment    numeric(24,6) NOT NULL DEFAULT 0,
  revaluation_surplus       numeric(24,6) NOT NULL DEFAULT 0,
  impairment_recognised     numeric(24,6) NOT NULL DEFAULT 0,
  months_consumed           numeric(10,4) NOT NULL DEFAULT 0,
  depreciated_through       date,
  status                    text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'active', 'fully_depreciated', 'disposed')),
  updated_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, asset_id, book_id),
  FOREIGN KEY (tenant_id, asset_id) REFERENCES app.fa_assets (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, book_id) REFERENCES app.fa_books (tenant_id, id)
);
CALL app.enable_tenant_rls('app.fa_asset_books');
CALL app.track_updated_at('app.fa_asset_books');

CREATE TABLE app.fa_schedule_lines (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  asset_book_id   uuid NOT NULL,
  period_start    date NOT NULL,
  period_end      date NOT NULL,
  weight          numeric(10,6) NOT NULL DEFAULT 1,
  planned_amount  numeric(24,6) NOT NULL DEFAULT 0,
  posted_amount   numeric(24,6) NOT NULL DEFAULT 0,
  status          text NOT NULL DEFAULT 'planned' CHECK (status IN ('planned', 'posted', 'skipped')),
  transaction_id  uuid,
  run_id          uuid,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, asset_book_id, period_start),
  FOREIGN KEY (tenant_id, asset_book_id) REFERENCES app.fa_asset_books (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.fa_schedule_lines');

CREATE TABLE app.fa_transactions (
  tenant_id                        uuid NOT NULL,
  id                               uuid NOT NULL,
  company_id                       uuid NOT NULL,
  asset_id                         uuid NOT NULL,
  book_id                          uuid NOT NULL,
  posting_date                     date NOT NULL,
  kind                             text NOT NULL CHECK (kind IN ('acquisition', 'addition', 'depreciation', 'impairment', 'revaluation', 'transfer', 'disposal', 'reversal')),
  cost_delta                       numeric(24,6) NOT NULL DEFAULT 0,
  accumulated_depreciation_delta   numeric(24,6) NOT NULL DEFAULT 0,
  impairment_delta                 numeric(24,6) NOT NULL DEFAULT 0,
  surplus_delta                    numeric(24,6) NOT NULL DEFAULT 0,
  proceeds                         numeric(24,6) NOT NULL DEFAULT 0,
  gain_loss                        numeric(24,6) NOT NULL DEFAULT 0,
  journal_entry_id                 uuid,
  reverses_id                      uuid,
  run_id                           uuid,
  reference                        text,
  detail                           jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at                       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, asset_id) REFERENCES app.fa_assets (tenant_id, id),
  FOREIGN KEY (tenant_id, book_id) REFERENCES app.fa_books (tenant_id, id),
  FOREIGN KEY (tenant_id, reverses_id) REFERENCES app.fa_transactions (tenant_id, id)
);
CREATE INDEX fa_transactions_asset_idx ON app.fa_transactions (tenant_id, asset_id, book_id, posting_date);
CREATE INDEX fa_transactions_company_idx ON app.fa_transactions (tenant_id, company_id, posting_date);
CALL app.enable_tenant_rls('app.fa_transactions');

CREATE TABLE app.fa_depreciation_runs (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  book_id            uuid NOT NULL,
  number             text NOT NULL,
  period_start       date NOT NULL,
  period_end         date NOT NULL,
  posting_date       date NOT NULL,
  status             text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
  journal_entry_id   uuid,
  reversal_entry_id  uuid,
  asset_count        integer NOT NULL DEFAULT 0,
  total              numeric(24,6) NOT NULL DEFAULT 0,
  created_by         uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, book_id) REFERENCES app.fa_books (tenant_id, id)
);
CREATE INDEX fa_depreciation_runs_book_idx ON app.fa_depreciation_runs (tenant_id, book_id, period_end);
CALL app.enable_tenant_rls('app.fa_depreciation_runs');
CALL app.track_updated_at('app.fa_depreciation_runs');

CREATE TABLE app.fa_usage (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  asset_book_id  uuid NOT NULL,
  period_start   date NOT NULL,
  units          numeric(24,6) NOT NULL CHECK (units >= 0),
  note           text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, asset_book_id, period_start),
  FOREIGN KEY (tenant_id, asset_book_id) REFERENCES app.fa_asset_books (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.fa_usage');

CREATE TABLE app.fa_verifications (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  asset_id       uuid NOT NULL,
  verified_on    date NOT NULL,
  verified_by    uuid,
  condition      text NOT NULL DEFAULT 'good' CHECK (condition IN ('good', 'damaged', 'missing')),
  location_note  text,
  note           text,
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, asset_id) REFERENCES app.fa_assets (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX fa_verifications_asset_idx ON app.fa_verifications (tenant_id, asset_id, verified_on);
CALL app.enable_tenant_rls('app.fa_verifications');
