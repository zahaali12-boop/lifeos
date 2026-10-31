-- Saved reports (founder programme item 9, slice 9b; ADR-0034 §4, A-183): a query definition kept under a name by
-- its owner, with a kind (table, pivot, chart) and chart options, and the shares that let other members see or
-- edit it. Whoever runs a saved report runs it with their own scopes; a share never widens access.
CREATE TABLE app.rpt_reports (
  tenant_id            uuid NOT NULL,
  id                   uuid NOT NULL,
  company_id           uuid,
  model                text NOT NULL,
  name_i18n            jsonb NOT NULL DEFAULT '{}'::jsonb,
  description_i18n     jsonb NOT NULL DEFAULT '{}'::jsonb,
  owner_membership_id  uuid NOT NULL,
  definition           jsonb NOT NULL,
  kind                 text NOT NULL DEFAULT 'pivot' CHECK (kind IN ('table', 'pivot', 'chart')),
  chart                jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_template          boolean NOT NULL DEFAULT false,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE INDEX rpt_reports_owner_idx ON app.rpt_reports (tenant_id, owner_membership_id);
CALL app.enable_tenant_rls('app.rpt_reports');
CALL app.track_updated_at('app.rpt_reports');

CREATE TABLE app.rpt_report_shares (
  tenant_id   uuid NOT NULL,
  id          uuid NOT NULL,
  report_id   uuid NOT NULL,
  share_with  text NOT NULL CHECK (share_with IN ('tenant', 'role', 'membership')),
  target_id   uuid,
  access      text NOT NULL DEFAULT 'view' CHECK (access IN ('view', 'edit')),
  created_at  timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE NULLS NOT DISTINCT (tenant_id, report_id, share_with, target_id),
  FOREIGN KEY (tenant_id, report_id) REFERENCES app.rpt_reports (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.rpt_report_shares');
