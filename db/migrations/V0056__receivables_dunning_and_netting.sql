-- M5 slice 5.7b: dunning levels, runs and letters; netting of a customer's receivable against the same partner's
-- payable (DOMAIN_MODEL §13; POSTING_RULES §2 "Dunning fee", §4; A-113, A-162).

-- When an item was last named on a dunning letter, beside the level it reached.
ALTER TABLE app.ar_open_items ADD COLUMN last_dunned_on date;

-- The company's reminder ladder: an item that many days past due reaches the level; the level's fee (in the company's
-- currency) is charged to the customer as a receivable adjustment, and a level may put the customer on credit hold.
CREATE TABLE app.ar_dunning_levels (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  level            int NOT NULL CHECK (level BETWEEN 1 AND 9),
  days_overdue     int NOT NULL CHECK (days_overdue >= 1),
  name_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  text_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  fee_amount       numeric(24,6) NOT NULL DEFAULT 0 CHECK (fee_amount >= 0),
  block_customer   boolean NOT NULL DEFAULT false,
  is_active        boolean NOT NULL DEFAULT true,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, level),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CALL app.enable_tenant_rls('app.ar_dunning_levels');
CALL app.track_updated_at('app.ar_dunning_levels');

CREATE TABLE app.ar_dunning_runs (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  company_id    uuid NOT NULL,
  number        text NOT NULL,
  run_date      date NOT NULL,
  partner_id    uuid,
  status        text NOT NULL DEFAULT 'issued' CHECK (status IN ('issued')),
  letters       int NOT NULL DEFAULT 0,
  fees_fc       numeric(24,6) NOT NULL DEFAULT 0,
  run_by        uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX ar_dunning_runs_company_idx ON app.ar_dunning_runs (tenant_id, company_id, run_date);
CALL app.enable_tenant_rls('app.ar_dunning_runs');
CALL app.track_updated_at('app.ar_dunning_runs');

-- One letter per customer per run: the overdue items it names (a snapshot, with the level each reached), the level of
-- the letter (the highest among them), the fee charged and the item that carries it.
CREATE TABLE app.ar_dunning_letters (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  run_id               uuid NOT NULL,
  company_id           uuid NOT NULL,
  partner_id           uuid NOT NULL,
  number               text NOT NULL,
  level                int NOT NULL,
  level_id             uuid NOT NULL,
  items                jsonb NOT NULL DEFAULT '[]'::jsonb,
  item_count           int NOT NULL DEFAULT 0,
  overdue_fc           numeric(24,6) NOT NULL DEFAULT 0,
  fee_amount           numeric(24,6) NOT NULL DEFAULT 0,
  fee_item_id          uuid,
  fee_journal_entry_id uuid,
  blocked_customer     boolean NOT NULL DEFAULT false,
  sent_at              timestamptz,
  created_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, run_id) REFERENCES app.ar_dunning_runs (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, level_id) REFERENCES app.ar_dunning_levels (tenant_id, id)
);
CREATE INDEX ar_dunning_letters_partner_idx ON app.ar_dunning_letters (tenant_id, company_id, partner_id, created_at);
CALL app.enable_tenant_rls('app.ar_dunning_letters');

-- A netting sets what a partner owes the company as a customer against what the company owes them as a supplier,
-- in one currency: one journal Dr payables / Cr receivables at each item's booked value (the difference is realised
-- FX), a counter item on each subledger used up at once by a settlement of kind netting (A-113).
CREATE TABLE app.ar_nettings (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  company_id             uuid NOT NULL,
  partner_id             uuid NOT NULL,
  number                 text NOT NULL,
  receivable_item_id     uuid NOT NULL,
  payable_item_id        uuid NOT NULL,
  ar_counter_item_id     uuid,
  ap_counter_item_id     uuid,
  ar_settlement_id       uuid,
  netting_date           date NOT NULL,
  currency               text NOT NULL,
  amount_tc              numeric(24,6) NOT NULL CHECK (amount_tc > 0),
  amount_fc_receivable   numeric(24,6) NOT NULL,
  amount_fc_payable      numeric(24,6) NOT NULL,
  fx_gain_loss_fc        numeric(24,6) NOT NULL DEFAULT 0,
  reason                 text,
  status                 text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
  journal_entry_id       uuid,
  reversal_entry_id      uuid,
  reversal_reason        text,
  reversed_at            timestamptz,
  reversed_by            uuid,
  created_by             uuid,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, receivable_item_id) REFERENCES app.ar_open_items (tenant_id, id),
  FOREIGN KEY (tenant_id, payable_item_id) REFERENCES app.ap_open_items (tenant_id, id)
);
CREATE INDEX ar_nettings_company_idx ON app.ar_nettings (tenant_id, company_id, status, netting_date);
CALL app.enable_tenant_rls('app.ar_nettings');
CALL app.track_updated_at('app.ar_nettings');
