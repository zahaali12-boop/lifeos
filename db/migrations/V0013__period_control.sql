-- V0013: period control (roadmap 2.4, ADR-0026).
-- Posting windows: a company's "allow posting from/to" dates, for everyone (role_id NULL) or for one role; the
-- posting engine refuses a posting date outside the actor's effective window. Corrections: a posted manual journal
-- is corrected by a new journal that reverses the original into the first open period and posts the replacement
-- there, both journals referencing each other.

CREATE TABLE app.org_posting_windows (
  tenant_id   uuid NOT NULL REFERENCES control.tenants (id),
  id          uuid NOT NULL,
  company_id  uuid NOT NULL,
  role_id     uuid,
  allow_from  date,
  allow_to    date,
  reason      text,
  changed_by  uuid,
  created_at  timestamptz NOT NULL DEFAULT now(),
  updated_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, company_id, role_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, role_id) REFERENCES app.idn_roles (tenant_id, id) ON DELETE CASCADE,
  CHECK (allow_from IS NOT NULL OR allow_to IS NOT NULL),
  CHECK (allow_from IS NULL OR allow_to IS NULL OR allow_from <= allow_to)
);
CALL app.enable_tenant_rls('app.org_posting_windows');
CALL app.track_updated_at('app.org_posting_windows');

ALTER TABLE app.gl_manual_journals
  ADD COLUMN corrects_journal_id      uuid,
  ADD COLUMN corrected_by_journal_id  uuid,
  ADD COLUMN correction_reason        text,
  ADD FOREIGN KEY (tenant_id, corrects_journal_id) REFERENCES app.gl_manual_journals (tenant_id, id),
  ADD FOREIGN KEY (tenant_id, corrected_by_journal_id) REFERENCES app.gl_manual_journals (tenant_id, id),
  ADD CHECK (corrects_journal_id IS NULL OR corrects_journal_id <> id);
