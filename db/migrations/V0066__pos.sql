-- Point of sale (founder programme item 7, ADR-0032, A-180): registers (the till's configuration: warehouse, cash box,
-- card account, price list, walk-in customer), sessions (a cashier's shift on a register with the float, the expected
-- and counted cash and the difference), orders synced from the till (immutable once posted; priced on the device from
-- the catalogue it cached, validated and posted here) with their lines and tenders.
CREATE TABLE app.pos_registers (
  tenant_id                 uuid NOT NULL,
  id                        uuid NOT NULL,
  company_id                uuid NOT NULL,
  branch_id                 uuid,
  code                      text NOT NULL,
  name_i18n                 jsonb NOT NULL DEFAULT '{}'::jsonb,
  warehouse_id              uuid NOT NULL,
  bin_id                    uuid,
  cash_account_id           uuid NOT NULL,
  card_account_id           uuid,
  price_list_id             uuid,
  default_partner_id        uuid,
  allow_credit              boolean NOT NULL DEFAULT false,
  allow_price_override      boolean NOT NULL DEFAULT false,
  cash_rounding_increment   numeric(18,6) NOT NULL DEFAULT 0 CHECK (cash_rounding_increment >= 0),
  receipt_footer_i18n       jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active                 boolean NOT NULL DEFAULT true,
  created_at                timestamptz NOT NULL DEFAULT now(),
  updated_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, cash_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, card_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES app.prc_price_lists (tenant_id, id),
  FOREIGN KEY (tenant_id, default_partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX pos_registers_company_idx ON app.pos_registers (tenant_id, company_id, code);
CALL app.enable_tenant_rls('app.pos_registers');
CALL app.track_updated_at('app.pos_registers');

CREATE TABLE app.pos_sessions (
  tenant_id                     uuid NOT NULL,
  id                            uuid NOT NULL,
  company_id                    uuid NOT NULL,
  register_id                   uuid NOT NULL,
  number                        text NOT NULL,
  status                        text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'closed')),
  cashier_user_id               uuid NOT NULL,
  cashier_name                  text NOT NULL,
  opened_at                     timestamptz NOT NULL,
  closed_at                     timestamptz,
  opening_float                 numeric(24,6) NOT NULL DEFAULT 0 CHECK (opening_float >= 0),
  cash_taken                    numeric(24,6) NOT NULL DEFAULT 0,
  card_taken                    numeric(24,6) NOT NULL DEFAULT 0,
  credit_taken                  numeric(24,6) NOT NULL DEFAULT 0,
  orders_count                  integer NOT NULL DEFAULT 0,
  sales_total                   numeric(24,6) NOT NULL DEFAULT 0,
  expected_cash                 numeric(24,6),
  counted_cash                  numeric(24,6),
  difference                    numeric(24,6),
  difference_journal_entry_id   uuid,
  notes                         text,
  created_at                    timestamptz NOT NULL DEFAULT now(),
  updated_at                    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, register_id) REFERENCES app.pos_registers (tenant_id, id)
);
CREATE INDEX pos_sessions_register_idx ON app.pos_sessions (tenant_id, register_id, opened_at DESC);
CREATE UNIQUE INDEX pos_sessions_open_idx ON app.pos_sessions (tenant_id, register_id) WHERE status = 'open';
CALL app.enable_tenant_rls('app.pos_sessions');
CALL app.track_updated_at('app.pos_sessions');

CREATE TABLE app.pos_orders (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  register_id             uuid NOT NULL,
  session_id              uuid NOT NULL,
  number                  text NOT NULL,
  device_order_id         text NOT NULL,
  kind                    text NOT NULL CHECK (kind IN ('sale', 'refund')),
  refunds_order_id        uuid,
  partner_id              uuid,
  cashier_user_id         uuid NOT NULL,
  order_date              date NOT NULL,
  ordered_at              timestamptz NOT NULL,
  posted_at               timestamptz NOT NULL,
  currency                text NOT NULL,
  prices_include_tax      boolean NOT NULL DEFAULT false,
  total_net               numeric(24,6) NOT NULL DEFAULT 0,
  total_tax               numeric(24,6) NOT NULL DEFAULT 0,
  total_gross             numeric(24,6) NOT NULL DEFAULT 0,
  rounding                numeric(24,6) NOT NULL DEFAULT 0,
  total_due               numeric(24,6) NOT NULL DEFAULT 0,
  total_paid              numeric(24,6) NOT NULL DEFAULT 0,
  change_given            numeric(24,6) NOT NULL DEFAULT 0,
  journal_entry_id        uuid,
  stock_posting_id        uuid,
  einvoice_scheme         text,
  einvoice_status         text,
  einvoice_submission_id  text,
  einvoice_qr             text,
  einvoice_messages       jsonb NOT NULL DEFAULT '[]'::jsonb,
  einvoice_submitted_at   timestamptz,
  notes                   text,
  created_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, number),
  UNIQUE (tenant_id, device_order_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, register_id) REFERENCES app.pos_registers (tenant_id, id),
  FOREIGN KEY (tenant_id, session_id) REFERENCES app.pos_sessions (tenant_id, id),
  FOREIGN KEY (tenant_id, refunds_order_id) REFERENCES app.pos_orders (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX pos_orders_session_idx ON app.pos_orders (tenant_id, session_id, ordered_at);
CREATE INDEX pos_orders_company_idx ON app.pos_orders (tenant_id, company_id, order_date DESC);
CALL app.enable_tenant_rls('app.pos_orders');

CREATE TABLE app.pos_order_lines (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  order_id            uuid NOT NULL,
  line_no             integer NOT NULL,
  item_id             uuid NOT NULL,
  variant_id          uuid,
  item_code           text NOT NULL,
  description_i18n    jsonb NOT NULL DEFAULT '{}'::jsonb,
  uom_id              uuid NOT NULL,
  uom_code            text NOT NULL,
  quantity            numeric(24,9) NOT NULL CHECK (quantity > 0),
  base_quantity       numeric(24,9) NOT NULL CHECK (base_quantity > 0),
  unit_price          numeric(24,6) NOT NULL CHECK (unit_price >= 0),
  list_unit_price     numeric(24,6),
  discount_pct        numeric(9,4) NOT NULL DEFAULT 0 CHECK (discount_pct >= 0 AND discount_pct <= 100),
  discount_amount     numeric(24,6) NOT NULL DEFAULT 0,
  net_amount          numeric(24,6) NOT NULL,
  tax_code_id         uuid,
  tax_rate_pct        numeric(9,4) NOT NULL DEFAULT 0,
  tax_amount          numeric(24,6) NOT NULL DEFAULT 0,
  gross_amount        numeric(24,6) NOT NULL,
  is_stock_item       boolean NOT NULL DEFAULT true,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_id, line_no),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pos_orders (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.pos_order_lines');

CREATE TABLE app.pos_payments (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  order_id              uuid NOT NULL,
  line_no               integer NOT NULL,
  method                text NOT NULL CHECK (method IN ('cash', 'card', 'credit')),
  bank_account_id       uuid,
  amount                numeric(24,6) NOT NULL,
  reference             text,
  bank_transaction_id   uuid,
  receivable_item_id    uuid,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_id, line_no),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pos_orders (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, bank_account_id) REFERENCES app.bnk_bank_accounts (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pos_payments');
