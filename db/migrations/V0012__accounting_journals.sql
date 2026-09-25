-- V0012: journals (roadmap 2.3, DOMAIN_MODEL §6, POSTING_RULES §8).
-- Manual journals are documents: drafted, optionally approved, then posted through the engine (the posted journal
-- entry is immutable; the document keeps the link). Recurring templates generate journals on their cron schedule;
-- deferral schedules amortise a prepayment, accrual or deferred revenue over fiscal periods to the minor unit;
-- entries flagged auto_reverse_on are reversed by the daily routine on that date.

CREATE TABLE app.gl_manual_journals (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  number                text,
  kind                  text NOT NULL DEFAULT 'manual' CHECK (kind IN ('manual', 'recurring', 'reversing', 'accrual', 'opening', 'allocation')),
  posting_date          date NOT NULL,
  document_date         date NOT NULL,
  currency              text NOT NULL REFERENCES control.currencies (code),
  rate_type             text NOT NULL DEFAULT 'spot',
  rate_override         numeric(24, 12) CHECK (rate_override IS NULL OR rate_override > 0),
  rate_override_reason  text,
  branch_id             uuid,
  description_i18n      jsonb NOT NULL DEFAULT '{}'::jsonb,
  reference             text,
  status                text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'pending_approval', 'approved', 'rejected', 'posted', 'cancelled')),
  auto_reverse          boolean NOT NULL DEFAULT false,
  auto_reverse_on       date,
  template_id           uuid,
  journal_entry_id      uuid,
  custom_fields         jsonb NOT NULL DEFAULT '{}'::jsonb,
  submitted_by          uuid,
  submitted_at          timestamptz,
  approved_by           uuid,
  approved_at           timestamptz,
  rejection_reason      text,
  posted_by             uuid,
  posted_at             timestamptz,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id),
  FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES app.gl_journal_entries (tenant_id, id),
  CHECK (NOT auto_reverse OR auto_reverse_on IS NOT NULL),
  CHECK (auto_reverse_on IS NULL OR auto_reverse_on > posting_date)
);
CREATE UNIQUE INDEX gl_manual_journals_number_idx ON app.gl_manual_journals (tenant_id, company_id, number) WHERE number IS NOT NULL;
CREATE INDEX gl_manual_journals_company_idx ON app.gl_manual_journals (tenant_id, company_id, status, posting_date);
CALL app.enable_tenant_rls('app.gl_manual_journals');
CALL app.track_updated_at('app.gl_manual_journals');

CREATE TABLE app.gl_manual_journal_lines (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  id                uuid NOT NULL,
  journal_id        uuid NOT NULL,
  line_no           int NOT NULL CHECK (line_no >= 1),
  account_id        uuid NOT NULL,
  debit             numeric(24, 6) NOT NULL DEFAULT 0 CHECK (debit >= 0),
  credit            numeric(24, 6) NOT NULL DEFAULT 0 CHECK (credit >= 0),
  dimensions        jsonb NOT NULL DEFAULT '{}'::jsonb,
  partner_id        uuid,
  subledger_type    text CHECK (subledger_type IN ('AR', 'AP', 'INV', 'FA', 'BANK', 'PDC', 'GRNI', 'IC', 'WHT')),
  subledger_ref     uuid,
  tax_code_id       uuid,
  description_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  due_date          date,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, journal_id, line_no),
  FOREIGN KEY (tenant_id, journal_id) REFERENCES app.gl_manual_journals (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, account_id) REFERENCES app.gl_accounts (tenant_id, id),
  CHECK (debit = 0 OR credit = 0),
  CHECK ((subledger_type IS NULL) = (subledger_ref IS NULL))
);
CALL app.enable_tenant_rls('app.gl_manual_journal_lines');

CREATE TABLE app.gl_recurring_templates (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  code               text NOT NULL,
  name_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  cron               text NOT NULL,
  time_zone          text NOT NULL,
  next_run_on        date,
  ends_on            date,
  amount_mode        text NOT NULL DEFAULT 'fixed' CHECK (amount_mode IN ('fixed', 'variable', 'percentage')),
  base_amount        numeric(24, 6),
  currency           text NOT NULL REFERENCES control.currencies (code),
  lines              jsonb NOT NULL DEFAULT '[]'::jsonb,
  description_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  requires_review    boolean NOT NULL DEFAULT true,
  auto_reverse       boolean NOT NULL DEFAULT false,
  is_active          boolean NOT NULL DEFAULT true,
  last_generated_on  date,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CALL app.enable_tenant_rls('app.gl_recurring_templates');
CALL app.track_updated_at('app.gl_recurring_templates');

ALTER TABLE app.gl_manual_journals
  ADD CONSTRAINT gl_manual_journals_template_fk FOREIGN KEY (tenant_id, template_id) REFERENCES app.gl_recurring_templates (tenant_id, id);

CREATE TABLE app.gl_deferral_schedules (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  kind                  text NOT NULL CHECK (kind IN ('prepayment', 'accrual', 'deferred_revenue')),
  source_document_type  text,
  source_line_id        uuid,
  balance_account_id    uuid NOT NULL,
  target_account_id     uuid NOT NULL,
  starts_on             date NOT NULL,
  periods               int NOT NULL CHECK (periods BETWEEN 1 AND 120),
  method                text NOT NULL DEFAULT 'straight_line' CHECK (method IN ('straight_line', 'daily')),
  total_amount          numeric(24, 6) NOT NULL CHECK (total_amount > 0),
  currency              text NOT NULL REFERENCES control.currencies (code),
  dimensions            jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n      jsonb NOT NULL DEFAULT '{}'::jsonb,
  status                text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'completed', 'cancelled')),
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, balance_account_id) REFERENCES app.gl_accounts (tenant_id, id),
  FOREIGN KEY (tenant_id, target_account_id) REFERENCES app.gl_accounts (tenant_id, id)
);
CALL app.enable_tenant_rls('app.gl_deferral_schedules');
CALL app.track_updated_at('app.gl_deferral_schedules');

CREATE TABLE app.gl_deferral_lines (
  tenant_id         uuid NOT NULL REFERENCES control.tenants (id),
  schedule_id       uuid NOT NULL,
  sequence          int NOT NULL CHECK (sequence >= 1),
  fiscal_period_id  uuid,
  posting_date      date NOT NULL,
  amount            numeric(24, 6) NOT NULL CHECK (amount >= 0),
  journal_entry_id  uuid,
  status            text NOT NULL DEFAULT 'planned' CHECK (status IN ('planned', 'posted', 'cancelled')),
  PRIMARY KEY (tenant_id, schedule_id, sequence),
  FOREIGN KEY (tenant_id, schedule_id) REFERENCES app.gl_deferral_schedules (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, fiscal_period_id) REFERENCES app.org_fiscal_periods (tenant_id, id),
  FOREIGN KEY (tenant_id, journal_entry_id) REFERENCES app.gl_journal_entries (tenant_id, id),
  CHECK ((status = 'posted') = (journal_entry_id IS NOT NULL))
);
CALL app.enable_tenant_rls('app.gl_deferral_lines');
