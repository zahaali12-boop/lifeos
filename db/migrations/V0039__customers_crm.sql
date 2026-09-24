-- M5 slice 5.1: the partner master, customer side, and the CRM around it (DOMAIN_MODEL §7). One customer account per
-- partner and company carrying terms, credit settings, the sales rep and the default warehouse; customer groups; sales
-- reps with commission plans (tiered rates by item category and customer group, A-142); a configurable pipeline of
-- stages, opportunities with their stage history, and CRM activities (calls, meetings, emails, tasks, notes) planned
-- against a partner, an opportunity or a contact.

CREATE TABLE app.ptr_customer_groups (
  tenant_id          uuid NOT NULL REFERENCES control.tenants (id),
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
CALL app.enable_tenant_rls('app.ptr_customer_groups');
CALL app.track_updated_at('app.ptr_customer_groups');

-- A commission plan: what it is paid on (revenue, margin or what was collected), when it accrues, and the period over
-- which tier thresholds accumulate. Rates are marginal: each band of the period's basis earns its own rate.
CREATE TABLE app.ptr_commission_plans (
  tenant_id      uuid NOT NULL REFERENCES control.tenants (id),
  id             uuid NOT NULL,
  code           text NOT NULL,
  name_i18n      jsonb NOT NULL DEFAULT '{}'::jsonb,
  basis          text NOT NULL DEFAULT 'revenue' CHECK (basis IN ('revenue', 'margin', 'collected')),
  accrual_point  text NOT NULL DEFAULT 'invoice' CHECK (accrual_point IN ('invoice', 'payment')),
  tier_period    text NOT NULL DEFAULT 'month' CHECK (tier_period IN ('month', 'quarter', 'year')),
  currency       text NOT NULL,
  is_active      boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  CHECK (basis <> 'collected' OR accrual_point = 'payment')
);
CALL app.enable_tenant_rls('app.ptr_commission_plans');
CALL app.track_updated_at('app.ptr_commission_plans');

-- One rate band of a plan: for a scope (an item category and its descendants, a customer group, both, or neither) and
-- from a period-to-date basis amount upwards. The most specific scope that matches a sale decides its bands.
CREATE TABLE app.ptr_commission_rules (
  tenant_id          uuid NOT NULL,
  plan_id            uuid NOT NULL,
  sequence           int NOT NULL CHECK (sequence >= 1),
  item_category_id   uuid,
  customer_group_id  uuid,
  from_amount        numeric(24,6) NOT NULL DEFAULT 0 CHECK (from_amount >= 0),
  rate_pct           numeric(9,6) NOT NULL CHECK (rate_pct >= 0 AND rate_pct <= 100),
  PRIMARY KEY (tenant_id, plan_id, sequence),
  UNIQUE NULLS NOT DISTINCT (tenant_id, plan_id, item_category_id, customer_group_id, from_amount),
  FOREIGN KEY (tenant_id, plan_id) REFERENCES app.ptr_commission_plans (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_category_id) REFERENCES app.itm_item_categories (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_group_id) REFERENCES app.ptr_customer_groups (tenant_id, id)
);
CALL app.enable_tenant_rls('app.ptr_commission_rules');

-- A sales rep: optionally a member of the workspace (so "my customers" and "my pipeline" work) and an employee partner;
-- sells for one company or, when company_id is empty, for all.
CREATE TABLE app.ptr_sales_reps (
  tenant_id           uuid NOT NULL REFERENCES control.tenants (id),
  id                  uuid NOT NULL,
  code                text NOT NULL,
  name_i18n           jsonb NOT NULL DEFAULT '{}'::jsonb,
  membership_id       uuid,
  partner_id          uuid,
  company_id          uuid,
  commission_plan_id  uuid,
  email               text,
  phone               text,
  is_active           boolean NOT NULL DEFAULT true,
  created_at          timestamptz NOT NULL DEFAULT now(),
  updated_at          timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, commission_plan_id) REFERENCES app.ptr_commission_plans (tenant_id, id)
);
CREATE UNIQUE INDEX ptr_sales_reps_membership_idx ON app.ptr_sales_reps (tenant_id, membership_id) WHERE membership_id IS NOT NULL;
CALL app.enable_tenant_rls('app.ptr_sales_reps');
CALL app.track_updated_at('app.ptr_sales_reps');

-- The partner as a customer of one company. Terms and posting default from the group where left blank. The credit
-- limit is in the company's functional currency (empty: no limit); credit_status on_hold holds new orders for release,
-- blocked refuses orders, shipments and invoices (enforced by sales, M5.4).
CREATE TABLE app.ptr_customer_accounts (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  partner_id             uuid NOT NULL,
  company_id             uuid NOT NULL,
  customer_group_id      uuid,
  payment_terms_id       uuid,
  delivery_terms_id      uuid,
  posting_group_id       uuid,
  tax_group_id           uuid,
  sales_rep_id           uuid,
  default_warehouse_id   uuid,
  currency               text NOT NULL,
  credit_limit           numeric(24,6) CHECK (credit_limit IS NULL OR credit_limit >= 0),
  credit_exposure_basis  text NOT NULL DEFAULT 'open_ar_plus_orders' CHECK (credit_exposure_basis IN ('open_ar', 'open_ar_plus_orders')),
  overdue_block_days     int CHECK (overdue_block_days IS NULL OR overdue_block_days >= 0),
  credit_status          text NOT NULL DEFAULT 'ok' CHECK (credit_status IN ('ok', 'on_hold', 'blocked')),
  credit_status_reason   text,
  credit_status_at       timestamptz,
  credit_status_by       uuid,
  statement_frequency    text NOT NULL DEFAULT 'monthly' CHECK (statement_frequency IN ('none', 'weekly', 'monthly')),
  dunning_level          int NOT NULL DEFAULT 0 CHECK (dunning_level >= 0),
  is_active              boolean NOT NULL DEFAULT true,
  created_at             timestamptz NOT NULL DEFAULT now(),
  updated_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, partner_id, company_id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, customer_group_id) REFERENCES app.ptr_customer_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, payment_terms_id) REFERENCES app.ptr_payment_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, delivery_terms_id) REFERENCES app.ptr_delivery_terms (tenant_id, id),
  FOREIGN KEY (tenant_id, posting_group_id) REFERENCES app.gl_posting_groups (tenant_id, id),
  FOREIGN KEY (tenant_id, sales_rep_id) REFERENCES app.ptr_sales_reps (tenant_id, id),
  FOREIGN KEY (tenant_id, default_warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  CHECK ((credit_status = 'ok') = (credit_status_reason IS NULL))
);
CREATE INDEX ptr_customer_accounts_company_idx ON app.ptr_customer_accounts (tenant_id, company_id, is_active);
CREATE INDEX ptr_customer_accounts_rep_idx ON app.ptr_customer_accounts (tenant_id, sales_rep_id) WHERE sales_rep_id IS NOT NULL;
CALL app.enable_tenant_rls('app.ptr_customer_accounts');
CALL app.track_updated_at('app.ptr_customer_accounts');

-- The stages an opportunity moves through; exactly the won and lost stages close it.
CREATE TABLE app.ptr_pipeline_stages (
  tenant_id            uuid NOT NULL REFERENCES control.tenants (id),
  id                   uuid NOT NULL,
  code                 text NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  sort_order           int NOT NULL,
  default_probability  int NOT NULL CHECK (default_probability BETWEEN 0 AND 100),
  outcome              text NOT NULL DEFAULT 'open' CHECK (outcome IN ('open', 'won', 'lost')),
  is_system            boolean NOT NULL DEFAULT false,
  is_active            boolean NOT NULL DEFAULT true,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, code),
  CHECK (outcome <> 'won' OR default_probability = 100),
  CHECK (outcome <> 'lost' OR default_probability = 0)
);
CALL app.enable_tenant_rls('app.ptr_pipeline_stages');
CALL app.track_updated_at('app.ptr_pipeline_stages');

CREATE TABLE app.ptr_opportunities (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  company_id       uuid NOT NULL,
  number           text NOT NULL,
  partner_id       uuid NOT NULL,
  contact_id       uuid,
  title            text NOT NULL,
  stage_id         uuid NOT NULL,
  sales_rep_id     uuid,
  expected_amount  numeric(24,6) NOT NULL DEFAULT 0 CHECK (expected_amount >= 0),
  currency         text NOT NULL,
  probability_pct  int NOT NULL CHECK (probability_pct BETWEEN 0 AND 100),
  expected_close   date,
  source           text,
  status           text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'won', 'lost')),
  lost_reason      text,
  closed_on        date,
  notes            text,
  custom_fields    jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_by       uuid,
  created_at       timestamptz NOT NULL DEFAULT now(),
  updated_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id),
  FOREIGN KEY (tenant_id, contact_id) REFERENCES app.ptr_contacts (tenant_id, id) ON DELETE SET NULL (contact_id),
  FOREIGN KEY (tenant_id, stage_id) REFERENCES app.ptr_pipeline_stages (tenant_id, id),
  FOREIGN KEY (tenant_id, sales_rep_id) REFERENCES app.ptr_sales_reps (tenant_id, id),
  CHECK ((status = 'lost') = (lost_reason IS NOT NULL)),
  CHECK ((status = 'open') = (closed_on IS NULL))
);
CREATE INDEX ptr_opportunities_pipeline_idx ON app.ptr_opportunities (tenant_id, company_id, status, stage_id);
CREATE INDEX ptr_opportunities_partner_idx ON app.ptr_opportunities (tenant_id, partner_id, status);
CALL app.enable_tenant_rls('app.ptr_opportunities');
CALL app.track_updated_at('app.ptr_opportunities');

-- Every move of an opportunity between stages, append-only: time in stage, conversion and velocity read from it.
CREATE TABLE app.ptr_opportunity_stage_changes (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  opportunity_id   uuid NOT NULL,
  from_stage_id    uuid,
  to_stage_id      uuid NOT NULL,
  probability_pct  int NOT NULL,
  expected_amount  numeric(24,6) NOT NULL,
  changed_at       timestamptz NOT NULL DEFAULT now(),
  changed_by       uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, opportunity_id) REFERENCES app.ptr_opportunities (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, from_stage_id) REFERENCES app.ptr_pipeline_stages (tenant_id, id),
  FOREIGN KEY (tenant_id, to_stage_id) REFERENCES app.ptr_pipeline_stages (tenant_id, id)
);
CREATE INDEX ptr_opportunity_stage_changes_opportunity_idx ON app.ptr_opportunity_stage_changes (tenant_id, opportunity_id, changed_at);
CALL app.enable_tenant_rls('app.ptr_opportunity_stage_changes');

-- Calls, meetings, emails and tasks planned or logged with a partner (and optionally one of its opportunities or
-- contacts); notes are logged only. The record timeline (col_activities) stays the system's account of what happened.
CREATE TABLE app.ptr_crm_activities (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  partner_id              uuid NOT NULL,
  company_id              uuid,
  opportunity_id          uuid,
  contact_id              uuid,
  kind                    text NOT NULL CHECK (kind IN ('call', 'meeting', 'email', 'task', 'note')),
  subject                 text NOT NULL,
  body                    text,
  due_at                  timestamptz,
  assigned_membership_id  uuid,
  status                  text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'done', 'cancelled')),
  outcome                 text,
  completed_at            timestamptz,
  completed_by            uuid,
  created_by              uuid,
  created_at              timestamptz NOT NULL DEFAULT now(),
  updated_at              timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, partner_id) REFERENCES app.ptr_partners (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, opportunity_id) REFERENCES app.ptr_opportunities (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, contact_id) REFERENCES app.ptr_contacts (tenant_id, id) ON DELETE SET NULL (contact_id),
  CHECK ((status = 'open') = (completed_at IS NULL)),
  CHECK (kind <> 'note' OR status = 'done')
);
CREATE INDEX ptr_crm_activities_partner_idx ON app.ptr_crm_activities (tenant_id, partner_id, status, due_at);
CREATE INDEX ptr_crm_activities_assignee_idx ON app.ptr_crm_activities (tenant_id, assigned_membership_id, status, due_at) WHERE assigned_membership_id IS NOT NULL;
CALL app.enable_tenant_rls('app.ptr_crm_activities');
CALL app.track_updated_at('app.ptr_crm_activities');

-- Workspaces created before this migration get the pipeline their setup now gives every new one (PartnersDefaults),
-- written under each tenant's own row-level security context with UUIDv7 ids.
DO $$
DECLARE
  t record;
  s record;
BEGIN
  FOR t IN SELECT id FROM control.tenants LOOP
    PERFORM set_config('app.tenant_id', t.id::text, true);
    IF NOT EXISTS (SELECT 1 FROM app.ptr_pipeline_stages WHERE tenant_id = t.id) THEN
      FOR s IN SELECT * FROM (VALUES
        ('LEAD', 'Lead', 'عميل محتمل', 10, 10, 'open'),
        ('QUALIFIED', 'Qualified', 'مؤهل', 20, 25, 'open'),
        ('PROPOSAL', 'Proposal', 'عرض مقدم', 30, 50, 'open'),
        ('NEGOTIATION', 'Negotiation', 'تفاوض', 40, 75, 'open'),
        ('WON', 'Won', 'مكسوبة', 50, 100, 'won'),
        ('LOST', 'Lost', 'خاسرة', 60, 0, 'lost')) AS v(code, en, ar, sort_order, probability, outcome)
      LOOP
        INSERT INTO app.ptr_pipeline_stages (tenant_id, id, code, name_i18n, sort_order, default_probability, outcome, is_system)
        VALUES (
          t.id,
          encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) PLACING substring(int8send((extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6), 52, 1), 53, 1), 'hex')::uuid,
          s.code, jsonb_build_object('en', s.en, 'ar', s.ar), s.sort_order, s.probability, s.outcome, true);
      END LOOP;
    END IF;
  END LOOP;
  PERFORM set_config('app.tenant_id', '', true);
END
$$;
