-- M5 slice 5.4a: sales quotations (DOMAIN_MODEL §11, refined by A-151: the column is partner_id, not a customer
-- account id, matching ICustomerDirectory's own key (company + partner) exactly as Quicker.Purchasing's documents
-- key a supplier by partner_id, not a separate account row). A quotation prices its lines through the pricing engine
-- and tax through the tax engine on save; item, variant, unit and tax code references stay plain uuids validated at
-- the application layer, the same convention Purchasing's documents use for the same modules.

CREATE TABLE app.sls_quotations (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  branch_id             uuid,
  number                text NOT NULL,
  partner_id            uuid NOT NULL,
  opportunity_id        uuid,
  currency              text NOT NULL,
  quote_date            date NOT NULL,
  valid_until           date,
  pricing_date          date NOT NULL,
  price_list_id         uuid,
  status                text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'sent', 'accepted', 'rejected', 'expired', 'converted')),
  rejection_reason      text,
  total_net             numeric(24,6) NOT NULL DEFAULT 0,
  total_tax             numeric(24,6) NOT NULL DEFAULT 0,
  total_gross           numeric(24,6) NOT NULL DEFAULT 0,
  customer_snapshot     jsonb NOT NULL DEFAULT '{}'::jsonb,
  order_id              uuid,
  notes                 text,
  custom_fields         jsonb NOT NULL DEFAULT '{}'::jsonb,
  sent_at               timestamptz,
  decided_at            timestamptz,
  decided_by            uuid,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, price_list_id) REFERENCES app.prc_price_lists (tenant_id, id)
);
CREATE INDEX sls_quotations_status_idx ON app.sls_quotations (tenant_id, company_id, status);
CREATE INDEX sls_quotations_customer_idx ON app.sls_quotations (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.sls_quotations');
CALL app.track_updated_at('app.sls_quotations');

CREATE TABLE app.sls_quotation_lines (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  quotation_id      uuid NOT NULL,
  line_no           int NOT NULL,
  item_id           uuid NOT NULL,
  variant_id        uuid,
  description       text,
  quantity          numeric(24,9) NOT NULL CHECK (quantity > 0),
  uom_id            uuid NOT NULL,
  quantity_base     numeric(24,9) NOT NULL CHECK (quantity_base > 0),
  unit_price        numeric(24,6) NOT NULL DEFAULT 0,
  discount_pct      numeric(9,6) NOT NULL DEFAULT 0 CHECK (discount_pct >= 0 AND discount_pct <= 100),
  discount_amount   numeric(24,6) NOT NULL DEFAULT 0,
  tax_code_id       uuid,
  tax_rate_pct      numeric(9,6) NOT NULL DEFAULT 0,
  tax_reverse_charge boolean NOT NULL DEFAULT false,
  tax_recoverable   boolean NOT NULL DEFAULT true,
  tax_reason        text,
  net_amount        numeric(24,6) NOT NULL DEFAULT 0,
  tax_amount        numeric(24,6) NOT NULL DEFAULT 0,
  promotion_id      uuid,
  price_breakdown   jsonb NOT NULL DEFAULT '[]'::jsonb,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, quotation_id, line_no),
  FOREIGN KEY (tenant_id, quotation_id) REFERENCES app.sls_quotations (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX sls_quotation_lines_item_idx ON app.sls_quotation_lines (tenant_id, item_id);
CALL app.enable_tenant_rls('app.sls_quotation_lines');
