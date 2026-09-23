-- M4 slice 4.5: landed costs (DOMAIN_MODEL §10, POSTING_RULES "Landed cost document", hard scenario 2).
-- Charge types per tenant; landed-cost documents with charges (estimated against the clearing account until the
-- charge invoice settles them) allocated to posted receipt lines by value, weight, volume or quantity with
-- largest-remainder rounding; each allocation records what went to stock on hand and what the cost adjustment run
-- pushed to consumption.

CREATE TABLE app.pur_charge_types (
  tenant_id                 uuid NOT NULL REFERENCES control.tenants (id),
  id                        uuid NOT NULL,
  code                      text NOT NULL,
  name_i18n                 jsonb NOT NULL DEFAULT '{}'::jsonb,
  default_allocation_basis  text NOT NULL DEFAULT 'value' CHECK (default_allocation_basis IN ('value', 'weight', 'volume', 'quantity')),
  is_system                 boolean NOT NULL DEFAULT false,
  is_active                 boolean NOT NULL DEFAULT true,
  created_at                timestamptz NOT NULL DEFAULT now(),
  updated_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.pur_charge_types');
CALL app.track_updated_at('app.pur_charge_types');

CREATE TABLE app.pur_landed_cost_docs (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  number              text NOT NULL,
  posting_date        date NOT NULL,
  status              text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'posted', 'reversed')),
  currency            text NOT NULL,
  exchange_rate       numeric(24,12) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
  total_amount        numeric(24,6) NOT NULL DEFAULT 0,
  total_amount_fc     numeric(24,6) NOT NULL DEFAULT 0,
  on_hand_portion_fc  numeric(24,6) NOT NULL DEFAULT 0,
  sold_portion_fc     numeric(24,6) NOT NULL DEFAULT 0,
  reference           text,
  notes               text,
  reversal_reason     text,
  reversed_at         timestamptz,
  reversed_by         uuid,
  custom_fields       jsonb NOT NULL DEFAULT '{}'::jsonb,
  posted_at           timestamptz,
  posted_by           uuid,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX pur_landed_cost_docs_company_idx ON app.pur_landed_cost_docs (tenant_id, company_id, status, posting_date);
CALL app.enable_tenant_rls('app.pur_landed_cost_docs');
CALL app.track_updated_at('app.pur_landed_cost_docs');

CREATE TABLE app.pur_landed_cost_charges (
  tenant_id                 uuid NOT NULL,
  id                        uuid NOT NULL,
  landed_cost_id            uuid NOT NULL,
  line_no                   int NOT NULL,
  charge_type_id            uuid NOT NULL,
  partner_id                uuid,
  description               text,
  amount                    numeric(24,6) NOT NULL CHECK (amount > 0),
  amount_fc                 numeric(24,6) NOT NULL DEFAULT 0,
  allocation_basis          text NOT NULL CHECK (allocation_basis IN ('value', 'weight', 'volume', 'quantity')),
  is_estimate               boolean NOT NULL DEFAULT true,
  supplier_invoice_line_id  uuid,
  invoiced_amount_fc        numeric(24,6) NOT NULL DEFAULT 0,
  created_at                timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, landed_cost_id, line_no),
  FOREIGN KEY (tenant_id, landed_cost_id) REFERENCES app.pur_landed_cost_docs (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, charge_type_id) REFERENCES app.pur_charge_types (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_landed_cost_charges');

CREATE TABLE app.pur_landed_cost_allocations (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  landed_cost_id        uuid NOT NULL,
  charge_id             uuid NOT NULL,
  receipt_line_id       uuid NOT NULL,
  basis_value           numeric(24,9) NOT NULL DEFAULT 0,
  allocated_amount_fc   numeric(24,6) NOT NULL DEFAULT 0,
  on_hand_portion_fc    numeric(24,6) NOT NULL DEFAULT 0,
  sold_portion_fc       numeric(24,6) NOT NULL DEFAULT 0,
  adjustment_run_id     uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, charge_id, receipt_line_id),
  FOREIGN KEY (tenant_id, landed_cost_id) REFERENCES app.pur_landed_cost_docs (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, charge_id) REFERENCES app.pur_landed_cost_charges (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, receipt_line_id) REFERENCES app.pur_receipt_lines (tenant_id, id)
);
CREATE INDEX pur_landed_cost_allocations_receipt_line_idx ON app.pur_landed_cost_allocations (tenant_id, receipt_line_id);
CALL app.enable_tenant_rls('app.pur_landed_cost_allocations');

-- Invoice lines may settle a landed-cost charge (kind 'charge').
ALTER TABLE app.pur_invoice_lines DROP CONSTRAINT pur_invoice_lines_kind_check;
ALTER TABLE app.pur_invoice_lines ADD CONSTRAINT pur_invoice_lines_kind_check CHECK (kind IN ('receipt', 'order', 'expense', 'charge'));
ALTER TABLE app.pur_invoice_lines ADD COLUMN landed_cost_charge_id uuid;
