-- E-invoicing (roadmap 8.6, ADR-0018 amended): the devices (ZATCA "EGS units") a company onboards with the scheme,
-- each with its key, its certificate and its invoice counter and hash chain; and the submissions of documents,
-- one per attempt, with the signed XML, what the scheme answered and the QR.
CREATE TABLE app.einv_devices (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  scheme                  text NOT NULL DEFAULT 'zatca' CHECK (scheme IN ('zatca')),
  name                    text NOT NULL,
  environment             text NOT NULL CHECK (environment IN ('simulator', 'sandbox', 'simulation', 'production')),
  status                  text NOT NULL DEFAULT 'csr' CHECK (status IN ('csr', 'compliance', 'checked', 'active', 'revoked')),
  serial_number           text NOT NULL,
  common_name             text NOT NULL,
  organization_unit       text NOT NULL,
  organization            text NOT NULL,
  industry                text NOT NULL,
  invoice_types           text NOT NULL DEFAULT '1100',
  seller                  jsonb NOT NULL DEFAULT '{}'::jsonb,
  auto_submit             boolean NOT NULL DEFAULT true,
  csr                     text,
  private_key_enc         text NOT NULL,
  compliance_request_id   text,
  compliance_token_enc    text,
  compliance_secret_enc   text,
  production_token_enc    text,
  production_secret_enc   text,
  certificate             text,
  certificate_expires_at  timestamptz,
  compliance_checks       jsonb NOT NULL DEFAULT '[]'::jsonb,
  invoice_counter         bigint NOT NULL DEFAULT 0,
  last_invoice_hash       text,
  created_by              uuid,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, serial_number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX einv_devices_company_idx ON app.einv_devices (tenant_id, company_id, status);
CALL app.enable_tenant_rls('app.einv_devices');
CALL app.track_updated_at('app.einv_devices');

CREATE TABLE app.einv_submissions (
  tenant_id         uuid NOT NULL,
  id                uuid NOT NULL,
  company_id        uuid NOT NULL,
  device_id         uuid NOT NULL,
  document_type     text NOT NULL,
  document_id       uuid NOT NULL,
  document_number   text NOT NULL,
  kind              text NOT NULL CHECK (kind IN ('standard', 'simplified')),
  type_code         text NOT NULL CHECK (type_code IN ('388', '381', '383')),
  invoice_counter   bigint NOT NULL,
  invoice_hash      text NOT NULL,
  previous_hash     text NOT NULL,
  status            text NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'cleared', 'reported', 'rejected', 'failed')),
  warnings          jsonb NOT NULL DEFAULT '[]'::jsonb,
  errors            jsonb NOT NULL DEFAULT '[]'::jsonb,
  signed_xml        text NOT NULL,
  cleared_xml       text,
  qr                text,
  request_id        text,
  submitted_at      timestamptz NOT NULL DEFAULT now(),
  responded_at      timestamptz,
  created_by        uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, device_id) REFERENCES app.einv_devices (tenant_id, id)
);
CREATE INDEX einv_submissions_document_idx ON app.einv_submissions (tenant_id, document_type, document_id, submitted_at DESC);
CREATE INDEX einv_submissions_company_idx ON app.einv_submissions (tenant_id, company_id, submitted_at DESC);
CREATE UNIQUE INDEX einv_submissions_counter_idx ON app.einv_submissions (tenant_id, device_id, invoice_counter);
CALL app.enable_tenant_rls('app.einv_submissions');
