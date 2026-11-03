-- V0076: transfers between the company's own bank accounts and cash boxes (ADR-0035 §24, A-210).
-- Cash paid into the bank, cash drawn from it, money moved between boxes, or one currency changed for another: what
-- left the first account and what arrived in the second, each in its own currency, both valued in the company's
-- currency, and the realised exchange difference between the two. Posted at once through the posting engine; a
-- transfer is undone by a reversal, never edited or deleted.
CREATE TABLE app.bnk_transfers (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  number                  text NOT NULL,
  transfer_date           date NOT NULL,
  from_bank_account_id    uuid NOT NULL,
  to_bank_account_id      uuid NOT NULL,
  amount_sent             numeric(24,6) NOT NULL CHECK (amount_sent > 0),
  currency_sent           text NOT NULL,
  amount_received         numeric(24,6) NOT NULL CHECK (amount_received > 0),
  currency_received       text NOT NULL,
  exchange_rate           numeric(24,12) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
  amount_sent_fc          numeric(24,6) NOT NULL,
  amount_received_fc      numeric(24,6) NOT NULL,
  exchange_difference_fc  numeric(24,6) NOT NULL DEFAULT 0,
  reference               text,
  notes                   text,
  status                  text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
  journal_entry_id        uuid,
  from_transaction_id     uuid,
  to_transaction_id       uuid,
  reversal_entry_id       uuid,
  reversal_reason         text,
  reversed_at             timestamptz,
  reversed_by             uuid,
  posted_at               timestamptz,
  posted_by               uuid,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, from_bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, to_bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  CHECK (from_bank_account_id <> to_bank_account_id),
  -- Between two accounts of one currency what arrives is what left.
  CHECK (currency_sent <> currency_received OR amount_sent = amount_received),
  CHECK (amount_received_fc - amount_sent_fc = exchange_difference_fc)
);
CREATE INDEX bnk_transfers_company_idx ON app.bnk_transfers (tenant_id, company_id, transfer_date DESC);
CREATE INDEX bnk_transfers_from_idx ON app.bnk_transfers (tenant_id, from_bank_account_id);
CREATE INDEX bnk_transfers_to_idx ON app.bnk_transfers (tenant_id, to_bank_account_id);
CALL app.enable_tenant_rls('app.bnk_transfers');
CALL app.track_updated_at('app.bnk_transfers');
