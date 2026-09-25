-- M5 slice 5.3: the tax engine (ADR-0018, DOMAIN_MODEL §12). Regimes per country, loaded from versioned templates and
-- then the tenant's own; tax codes with effective-dated rates, the accounts they post to and the return boxes they
-- report in; item and partner tax groups; the determination matrix that picks a code for a document line; partner
-- exemptions; each company's registrations; the tax entries every posted document writes (the ledger the return is
-- computed from, reconciled to the tax accounts of the general ledger); and return periods, which lock once filed.

CREATE TABLE app.tax_regimes (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
  id                 uuid NOT NULL,
  code               text NOT NULL,
  country            text NOT NULL,
  name_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  family             text NOT NULL CHECK (family IN ('vat', 'gst', 'sales_tax', 'none')),
  rounding_level     text NOT NULL DEFAULT 'line' CHECK (rounding_level IN ('line', 'document')),
  tax_point          text NOT NULL DEFAULT 'invoice' CHECK (tax_point IN ('invoice', 'payment', 'delivery')),
  return_frequency   text NOT NULL DEFAULT 'quarterly' CHECK (return_frequency IN ('monthly', 'quarterly', 'annual')),
  einvoicing_scheme  text,
  template_code      text,
  template_version   text,
  is_active          boolean NOT NULL DEFAULT true,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  CHECK (country ~ '^[A-Z]{2}$' OR country = 'EU')
);
CALL app.enable_tenant_rls('app.tax_regimes');
CALL app.track_updated_at('app.tax_regimes');

-- A tax code: its kind, how it behaves (recoverable, reverse charge, exempt with the reason printed, zero-rated), what
-- it applies to, the account roles it posts to and the boxes of the return its base and tax go in.
CREATE TABLE app.tax_codes (
  tenant_id              uuid NOT NULL REFERENCES control.tenants (id),
  id                     uuid NOT NULL,
  regime_id              uuid NOT NULL,
  code                   text NOT NULL,
  name_i18n              jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind                   text NOT NULL CHECK (kind IN ('vat', 'gst', 'sales_tax', 'excise', 'withholding')),
  treatment              text NOT NULL DEFAULT 'standard' CHECK (treatment IN ('standard', 'zero_rated', 'exempt', 'out_of_scope')),
  is_recoverable         boolean NOT NULL DEFAULT true,
  is_reverse_charge      boolean NOT NULL DEFAULT false,
  applies_to             text NOT NULL DEFAULT 'both' CHECK (applies_to IN ('goods', 'services', 'both')),
  exemption_reason_code  text,
  exemption_reason_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  output_account_role    text NOT NULL DEFAULT 'OutputTax',
  input_account_role     text NOT NULL DEFAULT 'InputTax',
  sales_base_box         text,
  sales_tax_box          text,
  purchase_base_box      text,
  purchase_tax_box       text,
  is_active              boolean NOT NULL DEFAULT true,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, regime_id, code),
  FOREIGN KEY (tenant_id, regime_id) REFERENCES app.tax_regimes (tenant_id, id)
);
CALL app.enable_tenant_rls('app.tax_codes');
CALL app.track_updated_at('app.tax_codes');

-- A code's rate from a date; the rate in force on a document's tax date is the one with the latest start before it.
CREATE TABLE app.tax_rates (
  tenant_id    uuid NOT NULL REFERENCES control.tenants (id),
  tax_code_id  uuid NOT NULL,
  valid_from   date NOT NULL,
  rate_pct     numeric(9,4) NOT NULL CHECK (rate_pct >= 0 AND rate_pct <= 100),
  PRIMARY KEY (tenant_id, tax_code_id, valid_from),
  FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.tax_rates');

-- Item tax groups (standard goods, zero-rated food, exempt services...) and partner tax groups (domestic registered,
-- unregistered, GCC, export, government...), which the determination matrix pairs.
CREATE TABLE app.tax_groups (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  kind        text NOT NULL CHECK (kind IN ('item', 'partner')),
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, kind, code)
);
CALL app.enable_tenant_rls('app.tax_groups');
CALL app.track_updated_at('app.tax_groups');

-- The determination matrix: for a regime and a direction, the item group, the partner group and where goods ship from
-- and to (each blank for any), from a date, the code a line takes. The most specific row wins; a tie is refused when
-- the row is saved.
CREATE TABLE app.tax_determination_rules (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  regime_id             uuid NOT NULL,
  direction             text NOT NULL CHECK (direction IN ('sales', 'purchase')),
  item_tax_group_id     uuid,
  partner_tax_group_id  uuid,
  ship_from_country     text,
  ship_to_country       text,
  valid_from            date,
  valid_to              date,
  tax_code_id           uuid NOT NULL,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, regime_id, direction, item_tax_group_id, partner_tax_group_id, ship_from_country, ship_to_country, valid_from),
  FOREIGN KEY (tenant_id, regime_id) REFERENCES app.tax_regimes (tenant_id, id),
  FOREIGN KEY (tenant_id, item_tax_group_id) REFERENCES app.tax_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_tax_group_id) REFERENCES app.tax_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id),
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.tax_determination_rules');
CALL app.track_updated_at('app.tax_determination_rules');

-- A company's registration in a regime: its tax number, from when, and the regime whose return it files.
CREATE TABLE app.tax_registrations (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  regime_id            uuid NOT NULL,
  registration_number  text,
  registered_from      date,
  is_primary           boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, regime_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, regime_id) REFERENCES app.tax_regimes (tenant_id, id)
);
CALL app.enable_tenant_rls('app.tax_registrations');
CALL app.track_updated_at('app.tax_registrations');
CREATE UNIQUE INDEX tax_registrations_primary_idx ON app.tax_registrations (tenant_id, company_id) WHERE is_primary;

-- A partner's exemption certificate in a regime: while valid, its lines take the exempt code named here.
CREATE TABLE app.tax_exemptions (
  tenant_id           uuid NOT NULL REFERENCES control.tenants (id),
  id                  uuid NOT NULL,
  partner_id          uuid NOT NULL,
  regime_id           uuid NOT NULL,
  tax_code_id         uuid NOT NULL,
  certificate_number  text NOT NULL,
  valid_from          date NOT NULL,
  valid_to            date,
  notes               text,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, partner_id, regime_id, valid_from),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, regime_id) REFERENCES app.tax_regimes (tenant_id, id),
  FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id),
  CHECK (valid_to IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.tax_exemptions');
CALL app.track_updated_at('app.tax_exemptions');

-- A return period of a company in a regime. Filing it stamps the tax entries it covers and locks it: no entry dated
-- in it may be written afterwards; corrections go into the next open period with a reference.
CREATE TABLE app.tax_return_periods (
  tenant_id     uuid NOT NULL REFERENCES control.tenants (id),
  id            uuid NOT NULL,
  company_id    uuid NOT NULL,
  regime_id     uuid NOT NULL,
  period_start  date NOT NULL,
  period_end    date NOT NULL,
  status        text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'filed')),
  filed_at      timestamptz,
  filed_by      uuid,
  reference     text,
  totals        jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, regime_id, period_start),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, regime_id) REFERENCES app.tax_regimes (tenant_id, id),
  CHECK (period_end >= period_start),
  CHECK ((status = 'filed') = (filed_at IS NOT NULL))
);
CALL app.enable_tenant_rls('app.tax_return_periods');
CALL app.track_updated_at('app.tax_return_periods');

-- The tax ledger: one row per document line and tax code as the document posts, in the transaction and the company's
-- currency, with the journal entry it belongs to. Append-only: a reversal writes the opposite rows.
CREATE TABLE app.tax_entries (
  tenant_id                uuid NOT NULL REFERENCES control.tenants (id),
  id                       uuid NOT NULL,
  company_id               uuid NOT NULL,
  regime_id                uuid NOT NULL,
  tax_code_id              uuid NOT NULL,
  direction                text NOT NULL CHECK (direction IN ('sales', 'purchase')),
  posting_date             date NOT NULL,
  document_date            date,
  source_module            text NOT NULL,
  source_document_type     text NOT NULL,
  source_document_id       uuid NOT NULL,
  source_document_number   text,
  source_line_ref          uuid,
  journal_entry_id         uuid,
  partner_id               uuid,
  currency                 text NOT NULL REFERENCES control.currencies (code),
  rate_pct                 numeric(9,4) NOT NULL,
  base_tc                  numeric(24,6) NOT NULL,
  tax_tc                   numeric(24,6) NOT NULL,
  base_fc                  numeric(24,6) NOT NULL,
  tax_fc                   numeric(24,6) NOT NULL,
  is_reverse_charge        boolean NOT NULL DEFAULT false,
  is_recoverable           boolean NOT NULL DEFAULT true,
  reverses_entry_id        uuid,
  created_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, regime_id) REFERENCES app.tax_regimes (tenant_id, id),
  FOREIGN KEY (tenant_id, tax_code_id) REFERENCES app.tax_codes (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CALL app.enable_tenant_rls('app.tax_entries');
CALL app.make_append_only('app.tax_entries');
CREATE INDEX tax_entries_period_idx ON app.tax_entries (tenant_id, company_id, regime_id, posting_date);
CREATE INDEX tax_entries_document_idx ON app.tax_entries (tenant_id, source_document_type, source_document_id);

-- The groups items and partners already point at become real references.
ALTER TABLE app.itm_item_categories ADD FOREIGN KEY (tenant_id, item_tax_group_id) REFERENCES app.tax_groups (tenant_id, id);
ALTER TABLE app.itm_items ADD FOREIGN KEY (tenant_id, item_tax_group_id) REFERENCES app.tax_groups (tenant_id, id);
ALTER TABLE app.ptr_supplier_accounts ADD FOREIGN KEY (tenant_id, tax_group_id) REFERENCES app.tax_groups (tenant_id, id);
ALTER TABLE app.ptr_customer_accounts ADD FOREIGN KEY (tenant_id, tax_group_id) REFERENCES app.tax_groups (tenant_id, id);
