-- Bank statement import and reconciliation (roadmap 6.2): saved CSV mappings, imported statements and their lines
-- (deduplicated by an import key), matching rules, the matches between statement lines and the book's movements,
-- and the reconciliations of an account at a date.
CREATE TABLE app.bnk_statement_mappings (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  name        text NOT NULL,
  settings    jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, name)
);
CALL app.enable_tenant_rls('app.bnk_statement_mappings');
CALL app.track_updated_at('app.bnk_statement_mappings');

CREATE TABLE app.bnk_statements (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  bank_account_id  uuid NOT NULL,
  format           text NOT NULL CHECK (format IN ('csv', 'ofx', 'mt940', 'camt053')),
  file_name        text NOT NULL,
  from_date        date,
  to_date          date,
  opening_balance  numeric(24,6),
  closing_balance  numeric(24,6),
  line_count       integer NOT NULL DEFAULT 0,
  skipped_count    integer NOT NULL DEFAULT 0,
  status           text NOT NULL DEFAULT 'imported' CHECK (status IN ('imported', 'reconciled')),
  imported_by      uuid,
  imported_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE INDEX bnk_statements_account_idx ON app.bnk_statements (tenant_id, bank_account_id, imported_at DESC);
CALL app.enable_tenant_rls('app.bnk_statements');

CREATE TABLE app.bnk_statement_lines (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  statement_id     uuid NOT NULL,
  bank_account_id  uuid NOT NULL,
  line_no          integer NOT NULL,
  booking_date     date NOT NULL,
  value_date       date,
  amount           numeric(24,6) NOT NULL,
  description      text NOT NULL DEFAULT '',
  counterparty     text,
  reference        text,
  bank_code        text,
  external_id      text,
  import_key       text NOT NULL,
  status           text NOT NULL DEFAULT 'unmatched' CHECK (status IN ('unmatched', 'matched', 'excluded')),
  exclude_reason   text,
  matched_at       timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, statement_id) REFERENCES app.bnk_statements (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE UNIQUE INDEX bnk_statement_lines_key_idx ON app.bnk_statement_lines (tenant_id, bank_account_id, import_key);
CREATE INDEX bnk_statement_lines_status_idx ON app.bnk_statement_lines (tenant_id, bank_account_id, status, booking_date);
CALL app.enable_tenant_rls('app.bnk_statement_lines');

CREATE TABLE app.bnk_reconciliation_rules (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  name_i18n        jsonb NOT NULL,
  priority         integer NOT NULL DEFAULT 100,
  is_active        boolean NOT NULL DEFAULT true,
  direction        text NOT NULL DEFAULT 'any' CHECK (direction IN ('in', 'out', 'any')),
  conditions       jsonb NOT NULL DEFAULT '{}'::jsonb,
  action           text NOT NULL DEFAULT 'post_to_account' CHECK (action IN ('post_to_account')),
  account_role     text,
  bank_account_id  uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE INDEX bnk_reconciliation_rules_company_idx ON app.bnk_reconciliation_rules (tenant_id, company_id, priority);
CALL app.enable_tenant_rls('app.bnk_reconciliation_rules');
CALL app.track_updated_at('app.bnk_reconciliation_rules');

CREATE TABLE app.bnk_reconciliation_matches (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  statement_line_id    uuid NOT NULL,
  bank_transaction_id  uuid NOT NULL,
  rule_id              uuid,
  match_kind           text NOT NULL CHECK (match_kind IN ('auto', 'manual', 'created', 'cheque')),
  confidence           numeric(5,4) NOT NULL DEFAULT 1,
  matched_by           uuid,
  matched_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, statement_line_id) REFERENCES app.bnk_statement_lines (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, bank_transaction_id) REFERENCES app.bnk_bank_transactions (tenant_id, id)
);
CREATE UNIQUE INDEX bnk_reconciliation_matches_transaction_idx ON app.bnk_reconciliation_matches (tenant_id, bank_transaction_id);
CREATE INDEX bnk_reconciliation_matches_line_idx ON app.bnk_reconciliation_matches (tenant_id, statement_line_id);
CALL app.enable_tenant_rls('app.bnk_reconciliation_matches');

CREATE TABLE app.bnk_reconciliations (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  bank_account_id       uuid NOT NULL,
  number                text NOT NULL,
  as_of                 date NOT NULL,
  statement_balance     numeric(24,6) NOT NULL,
  book_balance          numeric(24,6) NOT NULL,
  deposits_in_transit   numeric(24,6) NOT NULL DEFAULT 0,
  outstanding_payments  numeric(24,6) NOT NULL DEFAULT 0,
  unmatched_bank_items  numeric(24,6) NOT NULL DEFAULT 0,
  difference            numeric(24,6) NOT NULL DEFAULT 0,
  status                text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'completed')),
  completed_by          uuid,
  completed_at          timestamptz,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE INDEX bnk_reconciliations_account_idx ON app.bnk_reconciliations (tenant_id, bank_account_id, as_of DESC);
CALL app.enable_tenant_rls('app.bnk_reconciliations');
CALL app.track_updated_at('app.bnk_reconciliations');
