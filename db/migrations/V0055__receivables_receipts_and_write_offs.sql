-- M5 slice 5.7a: customer receipts and deposits, write-offs and promises to pay (DOMAIN_MODEL §13, §14; POSTING_RULES §2; A-161).
-- A customer receipt is a bank payment the other way round: the same document table carries both, told apart by kind,
-- numbered in their own series (PAY- and RCT-).

ALTER TABLE app.bnk_payments DROP CONSTRAINT bnk_payments_kind_check;
ALTER TABLE app.bnk_payments ADD CONSTRAINT bnk_payments_kind_check CHECK (kind IN ('supplier_payment', 'supplier_advance', 'customer_receipt', 'customer_deposit'));

-- A line settles a payable item on a payment and a receivable item on a receipt: one column, two subledgers, so the
-- database can no longer name a single parent table; the services check the item against the document's kind.
ALTER TABLE app.bnk_payment_lines DROP CONSTRAINT bnk_payment_lines_tenant_id_open_item_id_fkey;

-- A promise to pay on an item: who promised what by when; dunning leaves a promised item alone until the date passes.
ALTER TABLE app.ar_open_items
  ADD COLUMN promise_note  text,
  ADD COLUMN promised_at   timestamptz,
  ADD COLUMN promised_by   uuid;

-- A write-off is a document of its own: it posts Dr bad debt (or the small-balance account) / Cr receivables, opens a
-- credit item on the customer for the amount and settles the written-off item with it (settlement kind write_off), so
-- the control account still equals its open items and a reversal reopens the item exactly.
CREATE TABLE app.ar_write_offs (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  partner_id          uuid NOT NULL,
  number              text NOT NULL,
  open_item_id        uuid NOT NULL,
  adjustment_item_id  uuid,
  settlement_id       uuid,
  write_off_date      date NOT NULL,
  kind                text NOT NULL CHECK (kind IN ('bad_debt', 'small_balance')),
  currency            text NOT NULL,
  amount_tc           numeric(24,6) NOT NULL CHECK (amount_tc > 0),
  amount_fc           numeric(24,6) NOT NULL,
  reason              text NOT NULL,
  status              text NOT NULL DEFAULT 'posted' CHECK (status IN ('posted', 'reversed')),
  journal_entry_id    uuid,
  reversal_entry_id   uuid,
  reversal_reason     text,
  reversed_at         timestamptz,
  reversed_by         uuid,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, open_item_id) REFERENCES app.ar_open_items (tenant_id, id)
);
CREATE INDEX ar_write_offs_company_idx ON app.ar_write_offs (tenant_id, company_id, status, write_off_date);
CREATE INDEX ar_write_offs_item_idx ON app.ar_write_offs (tenant_id, open_item_id);
CALL app.enable_tenant_rls('app.ar_write_offs');
CALL app.track_updated_at('app.ar_write_offs');
