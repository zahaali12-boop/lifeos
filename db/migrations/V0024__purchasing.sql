-- M4 slice 4.2: requisition to purchase order (DOMAIN_MODEL §10). Requisitions with approval, requests for quotation
-- with supplier invitations, quotes and a comparison, blanket agreements with releases, purchase orders with approval,
-- revisions (change orders) and budget commitments. Receipts, invoices, landed costs and returns follow in 4.3–4.6.
-- Quantities are numeric(24,9) in the entered unit with the exact base quantity beside them (A-096); money is
-- numeric(24,6) in the document currency with the rate to the company's functional currency on the header.

CREATE TABLE app.pur_requisitions (
  tenant_id                uuid NOT NULL REFERENCES control.tenants (id),
  id                       uuid NOT NULL,
  company_id               uuid NOT NULL,
  branch_id                uuid,
  number                   text NOT NULL,
  requester_membership_id  uuid,
  department_value_id      uuid,
  needed_by                date,
  status                   text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'pending_approval', 'approved', 'ordered', 'rejected', 'cancelled')),
  justification            text,
  currency                 text NOT NULL,
  total_estimated          numeric(24,6) NOT NULL DEFAULT 0,
  approval_request_id      uuid,
  rejection_reason         text,
  custom_fields            jsonb NOT NULL DEFAULT '{}'::jsonb,
  submitted_by             uuid,
  submitted_at             timestamptz,
  approved_at              timestamptz,
  created_by               uuid,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX pur_requisitions_status_idx ON app.pur_requisitions (tenant_id, company_id, status);
CALL app.enable_tenant_rls('app.pur_requisitions');
CALL app.track_updated_at('app.pur_requisitions');

CREATE TABLE app.pur_requisition_lines (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  requisition_id         uuid NOT NULL,
  line_no                int NOT NULL,
  item_id                uuid,
  description            text,
  quantity               numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id                 uuid NOT NULL,
  quantity_base          numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  estimated_price        numeric(24,6),
  warehouse_id           uuid,
  dimension_set_id       uuid,
  suggested_supplier_id  uuid,
  qty_ordered            numeric(24,9) NOT NULL DEFAULT 0,
  status                 text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'ordered', 'cancelled')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, requisition_id, line_no),
  FOREIGN KEY (tenant_id, requisition_id) REFERENCES app.pur_requisitions (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, suggested_supplier_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_requisition_lines');

CREATE TABLE app.pur_rfqs (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  company_id        uuid NOT NULL,
  number            text NOT NULL,
  title             text,
  due_on            date,
  status            text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'sent', 'closed', 'awarded', 'cancelled')),
  awarded_quote_id  uuid,
  notes             text,
  created_by        uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_rfqs');
CALL app.track_updated_at('app.pur_rfqs');

CREATE TABLE app.pur_rfq_lines (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  rfq_id               uuid NOT NULL,
  line_no              int NOT NULL,
  item_id              uuid,
  description          text,
  quantity             numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id               uuid NOT NULL,
  quantity_base        numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  requisition_line_id  uuid,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, rfq_id, line_no),
  FOREIGN KEY (tenant_id, rfq_id) REFERENCES app.pur_rfqs (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.pur_rfq_lines');

CREATE TABLE app.pur_rfq_suppliers (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  rfq_id         uuid NOT NULL,
  partner_id     uuid NOT NULL,
  contact_email  text,
  sent_at        timestamptz,
  status         text NOT NULL DEFAULT 'invited' CHECK (status IN ('invited', 'responded', 'declined')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, rfq_id, partner_id),
  FOREIGN KEY (tenant_id, rfq_id) REFERENCES app.pur_rfqs (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_rfq_suppliers');

-- A quote as the buyer records it (the supplier portal is later): prices per line, the supplier's lead time and the
-- freight and other charges quoted, from which the comparison computes a landed total in the company's currency.
CREATE TABLE app.pur_supplier_quotes (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  rfq_supplier_id      uuid NOT NULL,
  supplier_reference   text,
  currency             text NOT NULL,
  valid_until          date,
  payment_terms_id     uuid,
  lead_time_days       int NOT NULL DEFAULT 0 CHECK (lead_time_days >= 0),
  freight_amount       numeric(24,6) NOT NULL DEFAULT 0,
  other_charges        numeric(24,6) NOT NULL DEFAULT 0,
  notes                text,
  comparison_score     jsonb,
  received_at          timestamptz NOT NULL DEFAULT now(),
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, rfq_supplier_id),
  FOREIGN KEY (tenant_id, rfq_supplier_id) REFERENCES app.pur_rfq_suppliers (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_supplier_quotes');
CALL app.track_updated_at('app.pur_supplier_quotes');

CREATE TABLE app.pur_supplier_quote_lines (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  quote_id        uuid NOT NULL,
  rfq_line_id     uuid NOT NULL,
  unit_price      numeric(24,6) NOT NULL CHECK (unit_price >= 0),
  quantity        numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id          uuid NOT NULL,
  lead_time_days  int,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, quote_id, rfq_line_id),
  FOREIGN KEY (tenant_id, quote_id) REFERENCES app.pur_supplier_quotes (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, rfq_line_id) REFERENCES app.pur_rfq_lines (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_supplier_quote_lines');

CREATE TABLE app.pur_blanket_agreements (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  company_id        uuid NOT NULL,
  number            text NOT NULL,
  partner_id        uuid NOT NULL,
  valid_from        date NOT NULL,
  valid_to          date NOT NULL,
  currency          text NOT NULL,
  committed_amount  numeric(24,6) NOT NULL DEFAULT 0 CHECK (committed_amount >= 0),
  released_amount   numeric(24,6) NOT NULL DEFAULT 0,
  status            text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'active', 'closed', 'cancelled')),
  notes             text,
  created_by        uuid,
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  CHECK (valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.pur_blanket_agreements');
CALL app.track_updated_at('app.pur_blanket_agreements');

CREATE TABLE app.pur_blanket_lines (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  agreement_id  uuid NOT NULL,
  line_no       int NOT NULL,
  item_id       uuid NOT NULL,
  uom_id        uuid NOT NULL,
  agreed_qty    numeric(24,9) NOT NULL CHECK (agreed_qty > 0),
  agreed_price  numeric(24,6) NOT NULL CHECK (agreed_price >= 0),
  released_qty  numeric(24,9) NOT NULL DEFAULT 0 CHECK (released_qty >= 0),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, agreement_id, line_no),
  FOREIGN KEY (tenant_id, agreement_id) REFERENCES app.pur_blanket_agreements (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.pur_blanket_lines');

CREATE TABLE app.pur_orders (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  branch_id            uuid,
  number               text NOT NULL,
  partner_id           uuid NOT NULL,
  currency             text NOT NULL,
  exchange_rate        numeric(24,12) NOT NULL DEFAULT 1 CHECK (exchange_rate > 0),
  order_date           date NOT NULL,
  expected_date        date,
  payment_terms_id     uuid,
  delivery_terms_id    uuid,
  warehouse_id         uuid,
  status               text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'pending_approval', 'approved', 'sent', 'partially_received', 'received', 'closed', 'cancelled', 'rejected')),
  revision             int NOT NULL DEFAULT 1,
  total_net            numeric(24,6) NOT NULL DEFAULT 0,
  total_tax            numeric(24,6) NOT NULL DEFAULT 0,
  total_gross          numeric(24,6) NOT NULL DEFAULT 0,
  supplier_snapshot    jsonb NOT NULL DEFAULT '{}'::jsonb,
  approval_request_id  uuid,
  rejection_reason     text,
  requisition_id       uuid,
  rfq_id               uuid,
  agreement_id         uuid,
  notes                text,
  custom_fields        jsonb NOT NULL DEFAULT '{}'::jsonb,
  submitted_by         uuid,
  submitted_at         timestamptz,
  approved_at          timestamptz,
  sent_at              timestamptz,
  sent_to              text,
  created_by           uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, delivery_terms_id) REFERENCES app.ptr_delivery_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, requisition_id) REFERENCES app.pur_requisitions (tenant_id, id),
  FOREIGN KEY (tenant_id, rfq_id) REFERENCES app.pur_rfqs (tenant_id, id),
  FOREIGN KEY (tenant_id, agreement_id) REFERENCES app.pur_blanket_agreements (tenant_id, id)
);
CREATE INDEX pur_orders_status_idx ON app.pur_orders (tenant_id, company_id, status);
CREATE INDEX pur_orders_partner_idx ON app.pur_orders (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.pur_orders');
CALL app.track_updated_at('app.pur_orders');

CREATE TABLE app.pur_order_lines (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  order_id             uuid NOT NULL,
  line_no              int NOT NULL,
  item_id              uuid NOT NULL,
  variant_id           uuid,
  description          text,
  quantity             numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id               uuid NOT NULL,
  quantity_base        numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  unit_price           numeric(24,6) NOT NULL CHECK (unit_price >= 0),
  discount_pct         numeric(9,6) NOT NULL DEFAULT 0 CHECK (discount_pct >= 0 AND discount_pct <= 100),
  tax_code_id          uuid,
  net_amount           numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount           numeric(24,6) NOT NULL DEFAULT 0,
  expected_date        date,
  warehouse_id         uuid,
  dimension_set_id     uuid,
  qty_received         numeric(24,9) NOT NULL DEFAULT 0,
  qty_invoiced         numeric(24,9) NOT NULL DEFAULT 0,
  qty_cancelled        numeric(24,9) NOT NULL DEFAULT 0,
  requisition_line_id  uuid,
  blanket_line_id      uuid,
  status               text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'partially_received', 'received', 'closed', 'cancelled')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_id, line_no),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pur_orders (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, requisition_line_id) REFERENCES app.pur_requisition_lines (tenant_id, id),
  FOREIGN KEY (tenant_id, blanket_line_id) REFERENCES app.pur_blanket_lines (tenant_id, id)
);
CREATE INDEX pur_order_lines_item_idx ON app.pur_order_lines (tenant_id, item_id, status);
CALL app.enable_tenant_rls('app.pur_order_lines');

-- Every change order keeps the version it replaced: what was approved, sent or received against is never lost.
CREATE TABLE app.pur_order_revisions (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  order_id    uuid NOT NULL,
  revision    int NOT NULL,
  snapshot    jsonb NOT NULL,
  reason      text,
  changed_by  uuid,
  changed_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_id, revision),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pur_orders (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.pur_order_revisions');
CALL app.make_append_only('app.pur_order_revisions');

-- What an approved order commits per line, in the document currency and the functional currency, by month and
-- dimension set; consumed by receipts and invoices, released when the order is cancelled or closed short.
CREATE TABLE app.pur_commitments (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  company_id        uuid NOT NULL,
  order_id          uuid NOT NULL,
  order_line_id     uuid NOT NULL,
  account_role      text NOT NULL,
  dimension_set_id  uuid,
  period_key        text NOT NULL,
  amount_fc         numeric(24,6) NOT NULL,
  currency          text NOT NULL,
  amount_rc         numeric(24,6) NOT NULL,
  consumed_rc       numeric(24,6) NOT NULL DEFAULT 0,
  status            text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'consumed', 'released')),
  created_at        timestamptz NOT NULL DEFAULT now(),
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, order_line_id),
  FOREIGN KEY (tenant_id, order_id) REFERENCES app.pur_orders (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.pur_order_lines (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX pur_commitments_period_idx ON app.pur_commitments (tenant_id, company_id, period_key, status);
CALL app.enable_tenant_rls('app.pur_commitments');
CALL app.track_updated_at('app.pur_commitments');
