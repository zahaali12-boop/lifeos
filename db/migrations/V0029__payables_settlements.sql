-- M4 slice 4.7: payables as a module of its own (DOMAIN_MODEL §13, POSTING_RULES §4, ADR-0031).
-- The AP open items and settlements created by purchasing (V0026, V0028) move under the Payables module; settlements
-- gain what a payment carries (rate, discount, withholding, charge, write-off); payment proposals select what to pay;
-- journal lines may carry a functional amount with no transaction amount (a realised exchange difference).

DO $$
DECLARE c record;
BEGIN
  FOR c IN
    SELECT conname FROM pg_constraint
    WHERE conrelid = 'app.gl_journal_lines'::regclass AND contype = 'c' AND pg_get_constraintdef(oid) LIKE '%is_rounding%'
  LOOP
    EXECUTE format('ALTER TABLE app.gl_journal_lines DROP CONSTRAINT %I', c.conname);
  END LOOP;
END $$;
ALTER TABLE app.gl_journal_lines
  ADD CONSTRAINT gl_journal_lines_amount_present_check CHECK (is_rounding OR debit_tc <> 0 OR credit_tc <> 0 OR debit_fc <> 0 OR credit_fc <> 0);

-- A reversal is a mirror row (negative amounts) that names the settlement it reverses, so any balance at a date is a plain sum.
ALTER TABLE app.ap_settlements DROP CONSTRAINT ap_settlements_amount_tc_check;
ALTER TABLE app.ap_settlements
  ADD COLUMN settlement_rate     numeric(24,12) NOT NULL DEFAULT 1,
  ADD COLUMN discount_taken_tc   numeric(24,6) NOT NULL DEFAULT 0,
  ADD COLUMN wht_withheld_tc     numeric(24,6) NOT NULL DEFAULT 0,
  ADD COLUMN write_off_tc        numeric(24,6) NOT NULL DEFAULT 0,
  ADD COLUMN bank_charge_tc      numeric(24,6) NOT NULL DEFAULT 0,
  ADD COLUMN reason              text,
  ADD COLUMN status              text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed'));

ALTER TABLE app.ap_open_items
  ADD COLUMN held_by      uuid,
  ADD COLUMN held_at      timestamptz,
  ADD COLUMN reversed_on  date;

CREATE TABLE app.ap_payment_proposals (
  tenant_id        uuid NOT NULL REFERENCES control.tenants (id),
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  number           text NOT NULL,
  run_date         date NOT NULL,
  pay_through      date NOT NULL,
  currency         text NOT NULL,
  bank_account_id  uuid,
  partner_id       uuid,
  status           text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'approved', 'executed', 'cancelled')),
  total_tc         numeric(24,6) NOT NULL DEFAULT 0,
  discount_tc      numeric(24,6) NOT NULL DEFAULT 0,
  notes            text,
  approved_by      uuid,
  approved_at      timestamptz,
  created_by       uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX ap_payment_proposals_company_idx ON app.ap_payment_proposals (tenant_id, company_id, status, run_date);
CALL app.enable_tenant_rls('app.ap_payment_proposals');
CALL app.track_updated_at('app.ap_payment_proposals');

CREATE TABLE app.ap_payment_proposal_lines (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  proposal_id    uuid NOT NULL,
  open_item_id   uuid NOT NULL,
  partner_id     uuid NOT NULL,
  amount_tc      numeric(24,6) NOT NULL,
  discount_tc    numeric(24,6) NOT NULL DEFAULT 0,
  selected       boolean NOT NULL DEFAULT true,
  payment_id     uuid,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, proposal_id, open_item_id),
  FOREIGN KEY (tenant_id, proposal_id) REFERENCES app.ap_payment_proposals (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, open_item_id) REFERENCES app.ap_open_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.ap_payment_proposal_lines');
