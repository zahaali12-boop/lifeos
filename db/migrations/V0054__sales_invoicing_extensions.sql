-- M5 slice 5.6b: recurring invoices, deferred revenue on invoice lines, commission accrual at invoice and the
-- e-invoice status of a posted invoice (DOMAIN_MODEL §11, POSTING_RULES §2, A-160).

-- A posted invoice's standing with its regime's e-invoicing scheme: not_required when the regime names none,
-- pending once posted under a scheme, then whatever the clearance adapter answers (cleared, reported, rejected...).
ALTER TABLE app.sls_invoices
  ADD COLUMN einvoice_scheme         text,
  ADD COLUMN einvoice_submission_id  text,
  ADD COLUMN einvoice_qr             text,
  ADD COLUMN einvoice_messages       jsonb NOT NULL DEFAULT '[]'::jsonb,
  ADD COLUMN einvoice_submitted_at   timestamptz,
  ADD COLUMN recurring_invoice_id    uuid;

-- A line deferred over periods parks its net on deferred revenue and asks Accounting for the schedule that releases it.
ALTER TABLE app.sls_invoice_lines
  ADD COLUMN deferral_periods      int CHECK (deferral_periods IS NULL OR deferral_periods BETWEEN 1 AND 120),
  ADD COLUMN deferral_starts_on    date,
  ADD COLUMN deferral_schedule_id  uuid;

-- A recurring invoice: the lines of a direct invoice (items and service lines) issued to a customer on a cron
-- schedule in the company's time zone, as the recurring journals of V0012 are.
CREATE TABLE app.sls_recurring_invoices (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  company_id          uuid NOT NULL,
  partner_id          uuid NOT NULL,
  code                text NOT NULL,
  name_i18n           jsonb NOT NULL DEFAULT '{}'::jsonb,
  cron                text NOT NULL,
  time_zone           text NOT NULL,
  next_run_on         date,
  ends_on             date,
  currency            text NOT NULL,
  lines               jsonb NOT NULL DEFAULT '[]'::jsonb,
  customer_reference  text,
  payment_terms_id    uuid,
  auto_post           boolean NOT NULL DEFAULT false,
  is_active           boolean NOT NULL DEFAULT true,
  last_generated_on   date,
  notes               text,
  created_by          uuid,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id)
);
CREATE INDEX sls_recurring_invoices_due_idx ON app.sls_recurring_invoices (tenant_id, is_active, next_run_on);
CALL app.enable_tenant_rls('app.sls_recurring_invoices');
CALL app.track_updated_at('app.sls_recurring_invoices');

ALTER TABLE app.sls_invoices
  ADD CONSTRAINT sls_invoices_recurring_fk FOREIGN KEY (tenant_id, recurring_invoice_id) REFERENCES app.sls_recurring_invoices (tenant_id, id);

-- What a sales rep earned on an invoice line under their plan (DOMAIN_MODEL §11 sls_commission_entries): the basis
-- and the commission in the plan's currency, booked Dr commission expense / Cr commission payable with the invoice.
CREATE TABLE app.sls_commission_entries (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  invoice_id         uuid NOT NULL,
  invoice_line_id    uuid NOT NULL,
  sales_rep_id       uuid NOT NULL,
  plan_id            uuid NOT NULL,
  posting_date       date NOT NULL,
  basis              text NOT NULL,
  basis_amount       numeric(24,6) NOT NULL,
  commission_amount  numeric(24,6) NOT NULL,
  currency           text NOT NULL,
  status             text NOT NULL DEFAULT 'accrued' CHECK (status IN ('accrued', 'reversed')),
  journal_entry_id   uuid,
  reversal_entry_id  uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, invoice_id) REFERENCES app.sls_invoices (tenant_id, id),
  FOREIGN KEY (tenant_id, invoice_line_id) REFERENCES app.sls_invoice_lines (tenant_id, id)
);
CREATE INDEX sls_commission_entries_rep_idx ON app.sls_commission_entries (tenant_id, sales_rep_id, plan_id, posting_date);
CREATE INDEX sls_commission_entries_invoice_idx ON app.sls_commission_entries (tenant_id, invoice_id);
CALL app.enable_tenant_rls('app.sls_commission_entries');
