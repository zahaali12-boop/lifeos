-- Payroll and HR, simple (founder programme item 8, ADR-0033, A-181): pay components (earnings, deductions and
-- employer contributions, each landing on an account through a posting rule keyed by the component), employees over
-- the partner with the employee role, the components an employee carries, leave, and payroll runs with their lines
-- (one per employee, the net pay opening a payable the bank pays) and items (one per component).
CREATE TABLE app.pay_components (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  company_id      uuid NOT NULL,
  code            text NOT NULL,
  name_i18n       jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind            text NOT NULL CHECK (kind IN ('earning', 'deduction', 'employer')),
  calculation     text NOT NULL CHECK (calculation IN ('basic', 'fixed', 'percent_of_basic', 'percent_of_gross')),
  default_amount  numeric(24,6) NOT NULL DEFAULT 0 CHECK (default_amount >= 0),
  default_rate_pct numeric(9,4) NOT NULL DEFAULT 0 CHECK (default_rate_pct >= 0),
  applies_to_all  boolean NOT NULL DEFAULT false,
  expense_account_code  text,
  liability_account_code text,
  sort_order      integer NOT NULL DEFAULT 0,
  is_active       boolean NOT NULL DEFAULT true,
  created_at      timestamptz NOT NULL DEFAULT now(),
  updated_at      timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pay_components');
CALL app.track_updated_at('app.pay_components');

-- The component as a posting key: role PayrollExpense (earnings, employer contributions) and PayrollLiability
-- (deductions withheld, employer contributions payable) resolve by it in the company's posting profile.
ALTER TABLE app.gl_posting_rules ADD COLUMN pay_component_id uuid;
ALTER TABLE app.gl_posting_rules ADD FOREIGN KEY (tenant_id, pay_component_id) REFERENCES app.pay_components (tenant_id, id);
DROP INDEX app.gl_posting_rules_unique_idx;
CREATE UNIQUE INDEX gl_posting_rules_unique_idx ON app.gl_posting_rules (
  tenant_id, profile_id, account_role, coalesce(document_type, ''),
  coalesce(item_posting_group_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(partner_posting_group_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(tax_code_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(warehouse_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(branch_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(bank_account_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(asset_category_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(charge_type_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(expense_category_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(pay_component_id, '00000000-0000-0000-0000-000000000000'::uuid));

CREATE TABLE app.pay_employees (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  company_id              uuid NOT NULL,
  partner_id              uuid NOT NULL,
  number                  text NOT NULL,
  branch_id               uuid,
  department_value_id     uuid,
  dimension_set_id        uuid,
  job_title_i18n          jsonb NOT NULL DEFAULT '{}'::jsonb,
  hire_date               date NOT NULL,
  termination_date        date,
  basic_salary            numeric(24,6) NOT NULL DEFAULT 0 CHECK (basic_salary >= 0),
  currency                text NOT NULL,
  payment_method          text NOT NULL DEFAULT 'transfer' CHECK (payment_method IN ('transfer', 'cash')),
  bank_name               text,
  iban                    text,
  national_id             text,
  leave_entitlement_days  integer NOT NULL DEFAULT 21 CHECK (leave_entitlement_days >= 0),
  status                  text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'left')),
  notes                   text,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, number),
  UNIQUE (tenant_id, company_id, partner_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id)
);
CREATE INDEX pay_employees_company_idx ON app.pay_employees (tenant_id, company_id, status, number);
CALL app.enable_tenant_rls('app.pay_employees');
CALL app.track_updated_at('app.pay_employees');

CREATE TABLE app.pay_employee_components (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  employee_id   uuid NOT NULL,
  component_id  uuid NOT NULL,
  amount        numeric(24,6) CHECK (amount IS NULL OR amount >= 0),
  rate_pct      numeric(9,4) CHECK (rate_pct IS NULL OR rate_pct >= 0),
  valid_from    date,
  valid_to      date,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, employee_id) REFERENCES app.pay_employees (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, component_id) REFERENCES app.pay_components (tenant_id, id)
);
CREATE INDEX pay_employee_components_employee_idx ON app.pay_employee_components (tenant_id, employee_id);
CALL app.enable_tenant_rls('app.pay_employee_components');

CREATE TABLE app.pay_leaves (
  tenant_id     uuid NOT NULL,
  id            uuid NOT NULL,
  company_id    uuid NOT NULL,
  employee_id   uuid NOT NULL,
  kind          text NOT NULL CHECK (kind IN ('annual', 'sick', 'unpaid')),
  from_date     date NOT NULL,
  to_date       date NOT NULL CHECK (to_date >= from_date),
  days          numeric(6,2) NOT NULL CHECK (days > 0),
  status        text NOT NULL DEFAULT 'approved' CHECK (status IN ('approved', 'cancelled')),
  notes         text,
  created_by    uuid,
  created_at    timestamptz NOT NULL DEFAULT now(),
  updated_at    timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, employee_id) REFERENCES app.pay_employees (tenant_id, id)
);
CREATE INDEX pay_leaves_employee_idx ON app.pay_leaves (tenant_id, employee_id, from_date);
CALL app.enable_tenant_rls('app.pay_leaves');
CALL app.track_updated_at('app.pay_leaves');

CREATE TABLE app.pay_runs (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  number                text NOT NULL,
  period_year           integer NOT NULL,
  period_month          integer NOT NULL CHECK (period_month BETWEEN 1 AND 12),
  period_start          date NOT NULL,
  period_end            date NOT NULL,
  posting_date          date NOT NULL,
  pay_date              date NOT NULL,
  status                text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'calculated', 'pending_approval', 'approved', 'posted', 'paid', 'reversed', 'cancelled')),
  approval_request_id   uuid,
  rejection_reason      text,
  employees_count       integer NOT NULL DEFAULT 0,
  total_basic           numeric(24,6) NOT NULL DEFAULT 0,
  total_earnings        numeric(24,6) NOT NULL DEFAULT 0,
  total_gross           numeric(24,6) NOT NULL DEFAULT 0,
  total_deductions      numeric(24,6) NOT NULL DEFAULT 0,
  total_employer        numeric(24,6) NOT NULL DEFAULT 0,
  total_net             numeric(24,6) NOT NULL DEFAULT 0,
  journal_entry_id      uuid,
  reversal_entry_id     uuid,
  notes                 text,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE UNIQUE INDEX pay_runs_period_idx ON app.pay_runs (tenant_id, company_id, period_year, period_month) WHERE status NOT IN ('cancelled', 'reversed');
CALL app.enable_tenant_rls('app.pay_runs');
CALL app.track_updated_at('app.pay_runs');

CREATE TABLE app.pay_run_lines (
  tenant_id           uuid NOT NULL,
  id                  uuid NOT NULL,
  run_id              uuid NOT NULL,
  line_no             integer NOT NULL,
  employee_id         uuid NOT NULL,
  partner_id          uuid NOT NULL,
  employee_number     text NOT NULL,
  employee_name_i18n  jsonb NOT NULL DEFAULT '{}'::jsonb,
  branch_id           uuid,
  dimension_set_id    uuid,
  days_in_period      integer NOT NULL DEFAULT 30,
  paid_days           numeric(6,2) NOT NULL DEFAULT 30,
  unpaid_days         numeric(6,2) NOT NULL DEFAULT 0,
  basic_salary        numeric(24,6) NOT NULL DEFAULT 0,
  basic_amount        numeric(24,6) NOT NULL DEFAULT 0,
  earnings_total      numeric(24,6) NOT NULL DEFAULT 0,
  gross               numeric(24,6) NOT NULL DEFAULT 0,
  deductions_total    numeric(24,6) NOT NULL DEFAULT 0,
  employer_total      numeric(24,6) NOT NULL DEFAULT 0,
  net                 numeric(24,6) NOT NULL DEFAULT 0,
  open_item_id        uuid,
  payment_id          uuid,
  payment_number      text,
  paid_on             date,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, run_id, line_no),
  UNIQUE (tenant_id, run_id, employee_id),
  FOREIGN KEY (tenant_id, run_id) REFERENCES app.pay_runs (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, employee_id) REFERENCES app.pay_employees (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pay_run_lines');

CREATE TABLE app.pay_run_items (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  line_id               uuid NOT NULL,
  component_id          uuid NOT NULL,
  component_code        text NOT NULL,
  component_name_i18n   jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind                  text NOT NULL CHECK (kind IN ('earning', 'deduction', 'employer')),
  calculation           text NOT NULL,
  base_amount           numeric(24,6) NOT NULL DEFAULT 0,
  rate_pct              numeric(9,4) NOT NULL DEFAULT 0,
  amount                numeric(24,6) NOT NULL DEFAULT 0,
  sort_order            integer NOT NULL DEFAULT 0,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, line_id) REFERENCES app.pay_run_lines (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, component_id) REFERENCES app.pay_components (tenant_id, id)
);
CREATE INDEX pay_run_items_line_idx ON app.pay_run_items (tenant_id, line_id);
CALL app.enable_tenant_rls('app.pay_run_items');
