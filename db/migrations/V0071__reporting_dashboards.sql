-- Dashboards, schedules and runs (founder programme item 9, slice 9d; ADR-0034 §4, A-185). A dashboard is a set of
-- widgets over saved reports and statement layouts, run as whoever opens it; a schedule runs a saved report or a
-- statement as its owner on a cron, writes the file and emails it; every run is kept with its file and outcome.
CREATE TABLE app.rpt_dashboards (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  owner_membership_id  uuid NOT NULL,
  sharing              text NOT NULL DEFAULT 'private' CHECK (sharing IN ('private', 'tenant')),
  show_on_home         boolean NOT NULL DEFAULT false,
  is_template          boolean NOT NULL DEFAULT false,
  default_period       text NOT NULL DEFAULT 'this_month',
  refresh_seconds      integer CHECK (refresh_seconds IS NULL OR refresh_seconds BETWEEN 30 AND 86400),
  layout               jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX rpt_dashboards_owner_idx ON app.rpt_dashboards (tenant_id, owner_membership_id);
CALL app.enable_tenant_rls('app.rpt_dashboards');
CALL app.track_updated_at('app.rpt_dashboards');

CREATE TABLE app.rpt_dashboard_widgets (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  dashboard_id   uuid NOT NULL,
  sort_order     integer NOT NULL DEFAULT 0,
  code           text NOT NULL,
  title_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  source         text NOT NULL CHECK (source IN ('report', 'statement')),
  report_id      uuid,
  layout_id      uuid,
  layout_code    text,
  viz            text NOT NULL CHECK (viz IN ('kpi', 'bar', 'line', 'area', 'table')),
  period         text NOT NULL DEFAULT 'dashboard',
  width          integer NOT NULL DEFAULT 2 CHECK (width BETWEEN 1 AND 4),
  options        jsonb NOT NULL DEFAULT '{}'::jsonb,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, dashboard_id, code),
  FOREIGN KEY (tenant_id, dashboard_id) REFERENCES app.rpt_dashboards (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, report_id) REFERENCES app.rpt_reports (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, layout_id) REFERENCES app.rpt_financial_layouts (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.rpt_dashboard_widgets');

CREATE TABLE app.rpt_schedules (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  name_i18n             jsonb NOT NULL DEFAULT '{}'::jsonb,
  owner_membership_id   uuid NOT NULL,
  source                text NOT NULL CHECK (source IN ('report', 'statement')),
  report_id             uuid,
  layout_id             uuid,
  layout_code           text,
  period                text NOT NULL DEFAULT 'last_month',
  cron                  text NOT NULL,
  time_zone             text NOT NULL,
  format                text NOT NULL CHECK (format IN ('csv', 'xlsx', 'pdf')),
  recipients            jsonb NOT NULL DEFAULT '[]'::jsonb,
  only_if_rows          boolean NOT NULL DEFAULT false,
  include_closing       boolean,
  enabled               boolean NOT NULL DEFAULT true,
  platform_schedule_id  uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, report_id) REFERENCES app.rpt_reports (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, layout_id) REFERENCES app.rpt_financial_layouts (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX rpt_schedules_owner_idx ON app.rpt_schedules (tenant_id, owner_membership_id);
CALL app.enable_tenant_rls('app.rpt_schedules');
CALL app.track_updated_at('app.rpt_schedules');

CREATE TABLE app.rpt_runs (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  schedule_id           uuid NOT NULL,
  company_id            uuid NOT NULL,
  source                text NOT NULL,
  report_id             uuid,
  layout_id             uuid,
  layout_code           text,
  run_as_membership_id  uuid NOT NULL,
  trigger               text NOT NULL DEFAULT 'schedule' CHECK (trigger IN ('schedule', 'manual')),
  job_id                uuid,
  started_at            timestamptz NOT NULL,
  finished_at           timestamptz,
  status                text NOT NULL CHECK (status IN ('running', 'sent', 'skipped', 'failed')),
  period_from           date,
  period_to             date,
  row_count             integer,
  file_name             text,
  content_type          text,
  storage_key           text,
  size_bytes            bigint,
  recipients            jsonb NOT NULL DEFAULT '[]'::jsonb,
  error                 text,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, schedule_id) REFERENCES app.rpt_schedules (tenant_id, id) ON DELETE CASCADE
);
CREATE INDEX rpt_runs_schedule_idx ON app.rpt_runs (tenant_id, schedule_id, started_at DESC);
CALL app.enable_tenant_rls('app.rpt_runs');
