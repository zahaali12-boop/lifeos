-- M5 slice 5.9a: the print pipeline (ADR-0022, DOMAIN_MODEL §21 "prt_*", A-164). Templates are HTML and CSS in the
-- Scriban language, one per document type and variant, shipped with the product and copied into the tenant the first
-- time a document type is printed so an admin can edit them (every edit is a new version, rollback restores an older
-- one); a render is the PDF of one document as it stood (the rendered HTML is kept with it, so the file can be made
-- again exactly), produced by headless Chromium in the worker, cached by the fingerprint of the template version and
-- the document's print model, and kept as evidence under retention when the document was final; an email is one send
-- of a render to an address, logged on the document.

CREATE TABLE app.prt_templates (
  tenant_id        uuid NOT NULL REFERENCES control.tenants (id),
  id               uuid NOT NULL,
  code             text NOT NULL,
  document_type    text NOT NULL,
  variant          text NOT NULL DEFAULT 'standard',
  name_i18n        jsonb NOT NULL DEFAULT '{}'::jsonb,
  language_mode    text NOT NULL DEFAULT 'bilingual' CHECK (language_mode IN ('en', 'ar', 'bilingual')),
  paper            text NOT NULL DEFAULT 'A4' CHECK (paper IN ('A4', 'Letter')),
  company_id       uuid,
  branch_id        uuid,
  is_default       boolean NOT NULL DEFAULT false,
  is_system        boolean NOT NULL DEFAULT false,
  is_active        boolean NOT NULL DEFAULT true,
  current_version  int NOT NULL DEFAULT 1,
  created_by       uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX prt_templates_type_idx ON app.prt_templates (tenant_id, document_type, variant, is_active);
CALL app.enable_tenant_rls('app.prt_templates');
CALL app.track_updated_at('app.prt_templates');

CREATE TABLE app.prt_template_versions (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  template_id   uuid NOT NULL,
  version       int NOT NULL,
  html          text NOT NULL,
  css           text NOT NULL DEFAULT '',
  sample_data   jsonb NOT NULL DEFAULT '{}'::jsonb,
  note          text,
  published_by  uuid,
  published_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, template_id, version),
  FOREIGN KEY (tenant_id, template_id) REFERENCES app.prt_templates (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.prt_template_versions');

CREATE TABLE app.prt_renders (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid NOT NULL,
  document_type        text NOT NULL,
  document_id          uuid,
  parameters           jsonb NOT NULL DEFAULT '{}'::jsonb,
  variant              text NOT NULL DEFAULT 'standard',
  template_id          uuid NOT NULL,
  template_version_id  uuid NOT NULL,
  template_version     int NOT NULL,
  language_mode        text NOT NULL,
  title                text NOT NULL,
  number               text NOT NULL,
  file_name            text NOT NULL,
  is_final             boolean NOT NULL DEFAULT false,
  fingerprint          text NOT NULL,
  html                 text NOT NULL,
  status               text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'ready', 'failed')),
  storage_key          text,
  evidence_key         text,
  sha256               text,
  size_bytes           bigint,
  error                text,
  job_id               uuid,
  requested_by         uuid,
  requested_at         timestamptz NOT NULL DEFAULT now(),
  rendered_at          timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, template_id) REFERENCES app.prt_templates (tenant_id, id),
  FOREIGN KEY (tenant_id, template_version_id) REFERENCES app.prt_template_versions (tenant_id, id)
);
CREATE INDEX prt_renders_document_idx ON app.prt_renders (tenant_id, document_type, document_id, requested_at DESC);
CREATE INDEX prt_renders_fingerprint_idx ON app.prt_renders (tenant_id, fingerprint, status);
CALL app.enable_tenant_rls('app.prt_renders');

CREATE TABLE app.prt_emails (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  company_id     uuid NOT NULL,
  render_id      uuid NOT NULL,
  document_type  text NOT NULL,
  document_id    uuid,
  to_address     text NOT NULL,
  to_name        text,
  subject        text NOT NULL,
  message        text,
  status         text NOT NULL DEFAULT 'queued' CHECK (status IN ('queued', 'sent', 'failed')),
  error          text,
  job_id         uuid,
  requested_by   uuid,
  requested_at   timestamptz NOT NULL DEFAULT now(),
  sent_at        timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, render_id) REFERENCES app.prt_renders (tenant_id, id)
);
CREATE INDEX prt_emails_document_idx ON app.prt_emails (tenant_id, document_type, document_id, requested_at DESC);
CALL app.enable_tenant_rls('app.prt_emails');
