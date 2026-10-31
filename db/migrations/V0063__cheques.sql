-- Cheques and post-dated cheques (roadmap 6.3, POSTING_RULES §5, hard scenario 10): cheque books of a bank account,
-- one cheque per receipt or payment by cheque with its life recorded event by event, and deposit slips that take
-- cheques in hand to the bank. Received cheques sit on PDC receivable, then cheques under collection, then the bank;
-- issued ones on PDC payable until the bank pays them.
CREATE TABLE app.bnk_cheque_books (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  bank_account_id  uuid NOT NULL,
  prefix           text NOT NULL DEFAULT '',
  first_number     bigint NOT NULL CHECK (first_number > 0),
  last_number      bigint NOT NULL,
  next_number      bigint NOT NULL,
  is_active        boolean NOT NULL DEFAULT true,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  CHECK (last_number >= first_number AND next_number >= first_number)
);
CREATE INDEX bnk_cheque_books_account_idx ON app.bnk_cheque_books (tenant_id, bank_account_id);
CALL app.enable_tenant_rls('app.bnk_cheque_books');
CALL app.track_updated_at('app.bnk_cheque_books');

CREATE TABLE app.bnk_cheque_deposits (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  company_id        uuid NOT NULL,
  number            text NOT NULL,
  bank_account_id   uuid NOT NULL,
  deposit_date      date NOT NULL,
  currency          text NOT NULL,
  total             numeric(24,6) NOT NULL,
  status            text NOT NULL DEFAULT 'deposited' CHECK (status IN ('deposited', 'cleared', 'partially_bounced', 'bounced')),
  journal_entry_id  uuid,
  created_by        uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CREATE INDEX bnk_cheque_deposits_company_idx ON app.bnk_cheque_deposits (tenant_id, company_id, deposit_date DESC);
CALL app.enable_tenant_rls('app.bnk_cheque_deposits');
CALL app.track_updated_at('app.bnk_cheque_deposits');

CREATE TABLE app.bnk_cheques (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  direction            text NOT NULL CHECK (direction IN ('received', 'issued')),
  cheque_number        text NOT NULL,
  cheque_book_id       uuid,
  drawer_bank          text,
  drawer_name          text,
  partner_id           uuid NOT NULL,
  currency             text NOT NULL,
  amount               numeric(24,6) NOT NULL CHECK (amount > 0),
  amount_fc            numeric(24,6) NOT NULL DEFAULT 0,
  issue_date           date NOT NULL,
  due_date             date NOT NULL,
  is_post_dated        boolean NOT NULL DEFAULT false,
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'in_hand', 'deposited', 'cleared', 'bounced', 'returned', 'issued', 'paid', 'cancelled')),
  payment_id           uuid NOT NULL,
  deposit_id           uuid,
  bank_account_id      uuid NOT NULL,
  replaced_by_id       uuid,
  replaces_id          uuid,
  fee_open_item_id     uuid,
  bank_transaction_id  uuid,
  notes                text,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_id) REFERENCES app.bnk_payments (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, deposit_id) REFERENCES app.bnk_cheque_deposits (tenant_id, id),
  FOREIGN KEY (tenant_id, cheque_book_id) REFERENCES app.bnk_cheque_books (tenant_id, id)
);
CREATE INDEX bnk_cheques_company_idx ON app.bnk_cheques (tenant_id, company_id, direction, status, due_date);
CREATE UNIQUE INDEX bnk_cheques_payment_idx ON app.bnk_cheques (tenant_id, payment_id);
CALL app.enable_tenant_rls('app.bnk_cheques');
CALL app.track_updated_at('app.bnk_cheques');

CREATE TABLE app.bnk_cheque_events (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  cheque_id         uuid NOT NULL,
  occurred_at       timestamptz NOT NULL DEFAULT now(),
  event             text NOT NULL CHECK (event IN ('received', 'deposited', 'cleared', 'bounced', 'returned', 'replaced', 'issued', 'paid', 'cancelled')),
  effective_date    date NOT NULL,
  bank_charge       numeric(24,6) NOT NULL DEFAULT 0,
  customer_fee      numeric(24,6) NOT NULL DEFAULT 0,
  reason            text,
  journal_entry_id  uuid,
  actor             uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, cheque_id) REFERENCES app.bnk_cheques (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX bnk_cheque_events_cheque_idx ON app.bnk_cheque_events (tenant_id, cheque_id, occurred_at);
CALL app.enable_tenant_rls('app.bnk_cheque_events');
