-- Expenses module (founder programme item 2, roadmap 6.4, A-174): expense categories with their accounts and
-- policies, petty cash funds, expenses (supplier bills without an order, employee claims, petty cash vouchers)
-- with their lines, and fund replenishments. Every financial effect goes through the posting engine; the
-- payable open items live in ap_open_items and the cash movements in bnk_bank_transactions.

CREATE TABLE app.exp_categories (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  code              text NOT NULL,
  name_i18n         jsonb NOT NULL DEFAULT '{}'::jsonb,
  account_code      text NOT NULL,
  tax_code_id       uuid,
  receipt_required  boolean NOT NULL DEFAULT false,
  max_amount        numeric(24,6) CHECK (max_amount IS NULL OR max_amount > 0),
  is_active         boolean NOT NULL DEFAULT true,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id)
);
CALL app.enable_tenant_rls('app.exp_categories');
CALL app.track_updated_at('app.exp_categories');

CREATE TABLE app.exp_funds (
  tenant_id                uuid NOT NULL,
  id                       uuid NOT NULL,
  company_id               uuid NOT NULL,
  code                     text NOT NULL,
  name_i18n                jsonb NOT NULL DEFAULT '{}'::jsonb,
  bank_account_id          uuid NOT NULL,
  custodian_membership_id  uuid,
  float_amount             numeric(24,6) NOT NULL DEFAULT 0 CHECK (float_amount >= 0),
  currency                 text NOT NULL,
  is_active                boolean NOT NULL DEFAULT true,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  UNIQUE (tenant_id, bank_account_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CALL app.enable_tenant_rls('app.exp_funds');
CALL app.track_updated_at('app.exp_funds');

CREATE TABLE app.exp_expenses (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  number               text,
  kind                 text NOT NULL CHECK (kind IN ('supplier', 'employee', 'petty_cash')),
  partner_id           uuid,
  fund_id              uuid,
  expense_date         date NOT NULL,
  due_date             date,
  currency             text NOT NULL,
  reference            text,
  description          text,
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'pending_approval', 'confirmed', 'reversed', 'rejected', 'cancelled')),
  total_net            numeric(24,6) NOT NULL DEFAULT 0,
  total_tax            numeric(24,6) NOT NULL DEFAULT 0,
  total_gross          numeric(24,6) NOT NULL DEFAULT 0,
  journal_entry_id     uuid,
  reversal_entry_id    uuid,
  approval_request_id  uuid,
  bank_transaction_id  uuid,
  rejection_reason     text,
  custom_fields        jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_by           uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  CHECK ((kind = 'petty_cash') = (fund_id IS NOT NULL)),
  CHECK ((kind <> 'petty_cash') = (partner_id IS NOT NULL)),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, fund_id) REFERENCES app.exp_funds (tenant_id, id)
);
CREATE INDEX exp_expenses_company_idx ON app.exp_expenses (tenant_id, company_id, status, expense_date);
CREATE INDEX exp_expenses_partner_idx ON app.exp_expenses (tenant_id, partner_id) WHERE partner_id IS NOT NULL;
CALL app.enable_tenant_rls('app.exp_expenses');
CALL app.track_updated_at('app.exp_expenses');

CREATE TABLE app.exp_expense_lines (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  expense_id          uuid NOT NULL,
  line_no             integer NOT NULL,
  category_id         uuid NOT NULL,
  account_id          uuid,
  description         text,
  amount              numeric(24,6) NOT NULL CHECK (amount > 0),
  tax_code_id         uuid,
  tax_rate_pct        numeric(9,4) NOT NULL DEFAULT 0,
  tax_amount          numeric(24,6) NOT NULL DEFAULT 0,
  tax_recoverable     boolean NOT NULL DEFAULT false,
  tax_reverse_charge  boolean NOT NULL DEFAULT false,
  dimension_set_id    uuid,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, expense_id, line_no),
  FOREIGN KEY (tenant_id, expense_id) REFERENCES app.exp_expenses (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, category_id) REFERENCES app.exp_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id),
  FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id)
);
CALL app.enable_tenant_rls('app.exp_expense_lines');

CREATE TABLE app.exp_replenishments (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  fund_id               uuid NOT NULL,
  number                text NOT NULL,
  replenished_on        date NOT NULL,
  amount                numeric(24,6) NOT NULL CHECK (amount > 0),
  from_bank_account_id  uuid NOT NULL,
  reference             text,
  status                text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
  journal_entry_id      uuid,
  reversal_entry_id     uuid,
  fund_transaction_id   uuid NOT NULL,
  bank_transaction_id   uuid NOT NULL,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, fund_id) REFERENCES app.exp_funds (tenant_id, id),
  FOREIGN KEY (tenant_id, from_bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE INDEX exp_replenishments_fund_idx ON app.exp_replenishments (tenant_id, fund_id, replenished_on);
CALL app.enable_tenant_rls('app.exp_replenishments');
CALL app.track_updated_at('app.exp_replenishments');
