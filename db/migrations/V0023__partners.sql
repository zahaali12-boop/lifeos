-- M4 slice 4.1: the partner master, supplier side (DOMAIN_MODEL §7). One partner record per legal person, tenant-wide,
-- with contacts, addresses, bank accounts (numbers encrypted under the platform key, ADR-0025), tax registrations, and
-- one supplier account per company carrying terms, tolerances, holds and posting groups. Payment terms, delivery
-- terms, supplier groups and withholding-tax codes are tenant configuration. Customer accounts arrive with M5.

CREATE TABLE app.ptr_partners (
  tenant_id                uuid NOT NULL REFERENCES control.tenants (id),
  id                       uuid NOT NULL,
  code                     text NOT NULL,
  legal_name_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  trade_name_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind                     text NOT NULL DEFAULT 'organization' CHECK (kind IN ('organization', 'person')),
  is_customer              boolean NOT NULL DEFAULT false,
  is_supplier              boolean NOT NULL DEFAULT false,
  is_employee              boolean NOT NULL DEFAULT false,
  intercompany_company_id  uuid,
  default_language         text NOT NULL DEFAULT 'en',
  website                  text,
  email                    text,
  phone                    text,
  parent_partner_id        uuid,
  notes                    text,
  custom_fields            jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active                boolean NOT NULL DEFAULT true,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, intercompany_company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, parent_partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  CHECK (parent_partner_id IS NULL OR parent_partner_id <> id)
);
CREATE INDEX ptr_partners_search_idx ON app.ptr_partners (tenant_id, is_supplier, is_customer, is_active);
CALL app.enable_tenant_rls('app.ptr_partners');
CALL app.track_updated_at('app.ptr_partners');

CREATE TABLE app.ptr_contacts (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  partner_id           uuid NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  role                 text,
  email                text,
  phone                text,
  mobile               text,
  is_primary           boolean NOT NULL DEFAULT false,
  receives_statements  boolean NOT NULL DEFAULT false,
  notes                text,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX ptr_contacts_partner_idx ON app.ptr_contacts (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.ptr_contacts');
CALL app.track_updated_at('app.ptr_contacts');

CREATE TABLE app.ptr_partner_addresses (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  partner_id  uuid NOT NULL,
  role        text NOT NULL DEFAULT 'legal' CHECK (role IN ('billing', 'shipping', 'legal', 'other')),
  address     jsonb NOT NULL DEFAULT '{}'::jsonb,
  country     text NOT NULL,
  region      text,
  is_default  boolean NOT NULL DEFAULT false,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX ptr_partner_addresses_partner_idx ON app.ptr_partner_addresses (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.ptr_partner_addresses');
CALL app.track_updated_at('app.ptr_partner_addresses');

-- Account numbers and IBANs are stored only encrypted (AES-GCM under the platform key) beside a masked form for display.
CREATE TABLE app.ptr_partner_bank_accounts (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  partner_id             uuid NOT NULL,
  account_holder         text,
  bank_name              text NOT NULL,
  branch                 text,
  swift_bic              text,
  currency               text NOT NULL,
  account_number_enc     text,
  account_number_masked  text,
  iban_enc               text,
  iban_masked            text,
  is_default             boolean NOT NULL DEFAULT false,
  is_active              boolean NOT NULL DEFAULT true,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE,
  CHECK (account_number_enc IS NOT NULL OR iban_enc IS NOT NULL)
);
CREATE INDEX ptr_partner_bank_accounts_partner_idx ON app.ptr_partner_bank_accounts (tenant_id, partner_id);
CALL app.enable_tenant_rls('app.ptr_partner_bank_accounts');
CALL app.track_updated_at('app.ptr_partner_bank_accounts');

CREATE TABLE app.ptr_partner_tax_registrations (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  partner_id         uuid NOT NULL,
  country            text NOT NULL,
  registration_type  text NOT NULL CHECK (registration_type IN ('vat', 'tin', 'crn', 'other')),
  number             text NOT NULL,
  valid_from         date,
  valid_to           date,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, partner_id, country, registration_type, number),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE,
  CHECK (valid_to IS NULL OR valid_from IS NULL OR valid_to >= valid_from)
);
CALL app.enable_tenant_rls('app.ptr_partner_tax_registrations');
CALL app.track_updated_at('app.ptr_partner_tax_registrations');

CREATE TABLE app.ptr_payment_terms (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  code                 text NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  due_basis            text NOT NULL DEFAULT 'invoice_date' CHECK (due_basis IN ('invoice_date', 'end_of_month', 'delivery')),
  due_days             int NOT NULL DEFAULT 0 CHECK (due_days >= 0),
  early_discount_pct   numeric(9,6) NOT NULL DEFAULT 0 CHECK (early_discount_pct >= 0 AND early_discount_pct < 100),
  early_discount_days  int NOT NULL DEFAULT 0 CHECK (early_discount_days >= 0),
  business_days_only   boolean NOT NULL DEFAULT false,
  is_system            boolean NOT NULL DEFAULT false,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.ptr_payment_terms');
CALL app.track_updated_at('app.ptr_payment_terms');

-- Instalments: when present they replace the single due date; percentages sum to 100 (checked by the service).
CREATE TABLE app.ptr_payment_term_lines (
  tenant_id   uuid NOT NULL,
  terms_id    uuid NOT NULL,
  sequence    int NOT NULL CHECK (sequence >= 1),
  percentage  numeric(9,6) NOT NULL CHECK (percentage > 0 AND percentage <= 100),
  days        int NOT NULL CHECK (days >= 0),
  PRIMARY KEY (tenant_id, terms_id, sequence),
  FOREIGN KEY (tenant_id, terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.ptr_payment_term_lines');

CREATE TABLE app.ptr_delivery_terms (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  code        text NOT NULL,
  name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_system   boolean NOT NULL DEFAULT false,
  is_active   boolean NOT NULL DEFAULT true,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code)
);
CALL app.enable_tenant_rls('app.ptr_delivery_terms');
CALL app.track_updated_at('app.ptr_delivery_terms');

-- Withholding tax codes as suppliers carry them; the tax engine (M5) applies them at invoice or payment.
CREATE TABLE app.ptr_wht_codes (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  code                text NOT NULL,
  name_i18n           jsonb NOT NULL DEFAULT '{}'::jsonb,
  rate_pct            numeric(9,6) NOT NULL CHECK (rate_pct >= 0 AND rate_pct <= 100),
  withhold_at         text NOT NULL DEFAULT 'payment' CHECK (withhold_at IN ('invoice', 'payment')),
  threshold_amount    numeric(24,6),
  threshold_currency  text,
  is_active           boolean NOT NULL DEFAULT true,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  CHECK ((threshold_amount IS NULL) = (threshold_currency IS NULL))
);
CALL app.enable_tenant_rls('app.ptr_wht_codes');
CALL app.track_updated_at('app.ptr_wht_codes');

CREATE TABLE app.ptr_supplier_groups (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  code               text NOT NULL,
  name_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  posting_group_id   uuid,
  payment_terms_id   uuid,
  delivery_terms_id  uuid,
  is_active          boolean NOT NULL DEFAULT true,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, delivery_terms_id) REFERENCES app.ptr_delivery_terms (tenant_id, id)
);
CALL app.enable_tenant_rls('app.ptr_supplier_groups');
CALL app.track_updated_at('app.ptr_supplier_groups');

-- The partner as a supplier of one company: terms, tolerances and holds default from the group where left blank.
CREATE TABLE app.ptr_supplier_accounts (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  partner_id           uuid NOT NULL,
  company_id           uuid NOT NULL,
  supplier_group_id    uuid,
  payment_terms_id     uuid,
  delivery_terms_id    uuid,
  posting_group_id     uuid,
  tax_group_id         uuid,
  wht_code_id          uuid,
  currency             text NOT NULL,
  lead_time_days       int NOT NULL DEFAULT 0 CHECK (lead_time_days >= 0),
  price_tolerance_pct  numeric(9,6) NOT NULL DEFAULT 0 CHECK (price_tolerance_pct >= 0 AND price_tolerance_pct <= 100),
  qty_tolerance_pct    numeric(9,6) NOT NULL DEFAULT 0 CHECK (qty_tolerance_pct >= 0 AND qty_tolerance_pct <= 100),
  requires_po          boolean NOT NULL DEFAULT false,
  hold_status          text NOT NULL DEFAULT 'none' CHECK (hold_status IN ('none', 'purchase', 'payment', 'all')),
  hold_reason          text,
  held_at              timestamptz,
  held_by              uuid,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, partner_id, company_id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, supplier_group_id) REFERENCES app.ptr_supplier_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, delivery_terms_id) REFERENCES app.ptr_delivery_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, wht_code_id) REFERENCES app.ptr_wht_codes (tenant_id, id),
  CHECK ((hold_status = 'none') = (hold_reason IS NULL))
);
CREATE INDEX ptr_supplier_accounts_company_idx ON app.ptr_supplier_accounts (tenant_id, company_id, is_active);
CALL app.enable_tenant_rls('app.ptr_supplier_accounts');
CALL app.track_updated_at('app.ptr_supplier_accounts');
