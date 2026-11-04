-- M4 slice 4.7: the banking module's first tables (DOMAIN_MODEL §14, POSTING_RULES §4 "Supplier payment", §5).
-- Bank and cash accounts, each on its own control account with the bank transactions as its subledger; supplier
-- payments (and advances) that settle payable open items with discounts, withholding at payment, charges and
-- realised FX (ADR-0031). Statements, reconciliation, cheques and transfers arrive with M6.

CREATE TABLE app.bnk_bank_accounts (
  tenant_id        uuid NOT NULL REFERENCES control.tenants (id),
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  code             text NOT NULL,
  name_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind             text NOT NULL CHECK (kind IN ('bank', 'cash', 'petty_cash')),
  currency         text NOT NULL,
  gl_account_id    uuid NOT NULL,
  bank_name        text,
  branch_name      text,
  account_number   text,
  iban             text,
  swift            text,
  branch_id        uuid,
  is_active        boolean NOT NULL DEFAULT true,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, gl_account_id) REFERENCES app.gl_accounts (tenant_id, id)
);
CALL app.enable_tenant_rls('app.bnk_bank_accounts');
CALL app.track_updated_at('app.bnk_bank_accounts');

-- The bank subledger: one row per movement, signed in the account's currency; the control account's lines reference it.
CREATE TABLE app.bnk_bank_transactions (
  tenant_id                uuid NOT NULL,
  id                       uuid NOT NULL,
  bank_account_id          uuid NOT NULL,
  company_id               uuid NOT NULL,
  posting_date             date NOT NULL,
  value_date               date NOT NULL,
  kind                     text NOT NULL CHECK (kind IN ('receipt', 'disbursement', 'charge', 'interest', 'transfer_in', 'transfer_out', 'reversal', 'opening')),
  amount_tc                numeric(24,6) NOT NULL,
  amount_fc                numeric(24,6) NOT NULL,
  reference                text,
  source_document_type     text,
  source_document_id       uuid,
  journal_entry_id         uuid,
  reverses_transaction_id  uuid,
  reconciliation_status    text NOT NULL DEFAULT 'unreconciled' CHECK (reconciliation_status IN ('unreconciled', 'matched', 'reconciled')),
  created_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX bnk_bank_transactions_account_idx ON app.bnk_bank_transactions (tenant_id, bank_account_id, posting_date);
CALL app.enable_tenant_rls('app.bnk_bank_transactions');

CREATE TABLE app.bnk_payments (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  number               text NOT NULL,
  kind                 text NOT NULL CHECK (kind IN ('supplier_payment', 'supplier_advance')),
  partner_id           uuid NOT NULL,
  bank_account_id      uuid NOT NULL,
  payment_date         date NOT NULL,
  method               text NOT NULL CHECK (method IN ('transfer', 'cash', 'cheque')),
  reference            text,
  currency             text NOT NULL,
  exchange_rate        numeric(24,12) NOT NULL DEFAULT 1,
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'reversed')),
  amount_tc            numeric(24,6) NOT NULL DEFAULT 0,
  on_account_tc        numeric(24,6) NOT NULL DEFAULT 0,
  discount_tc          numeric(24,6) NOT NULL DEFAULT 0,
  wht_tc               numeric(24,6) NOT NULL DEFAULT 0,
  charges_bank         numeric(24,6) NOT NULL DEFAULT 0,
  bank_amount          numeric(24,6) NOT NULL DEFAULT 0,
  bank_currency        text NOT NULL,
  apply_wht            boolean NOT NULL DEFAULT true,
  wht_code_id          uuid,
  open_item_id         uuid,
  journal_entry_id     uuid,
  bank_transaction_id  uuid,
  reversal_entry_id    uuid,
  reversal_reason      text,
  reversed_at          timestamptz,
  reversed_by          uuid,
  proposal_id          uuid,
  notes                text,
  custom_fields        jsonb NOT NULL DEFAULT '{}'::jsonb,
  posted_at            timestamptz,
  posted_by            uuid,
  created_by           uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE INDEX bnk_payments_company_idx ON app.bnk_payments (tenant_id, company_id, status, payment_date);
CREATE INDEX bnk_payments_partner_idx ON app.bnk_payments (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.bnk_payments');
CALL app.track_updated_at('app.bnk_payments');

CREATE TABLE app.bnk_payment_lines (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  payment_id       uuid NOT NULL,
  line_no          int NOT NULL,
  open_item_id     uuid NOT NULL,
  amount_tc        numeric(24,6) NOT NULL CHECK (amount_tc > 0),
  discount_tc      numeric(24,6) NOT NULL DEFAULT 0,
  wht_tc           numeric(24,6) NOT NULL DEFAULT 0,
  settlement_id    uuid,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, payment_id, line_no),
  FOREIGN KEY (tenant_id, payment_id) REFERENCES app.bnk_payments (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, open_item_id) REFERENCES app.ap_open_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.bnk_payment_lines');
