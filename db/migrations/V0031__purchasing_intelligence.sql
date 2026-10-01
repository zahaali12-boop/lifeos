-- M4 slice 4.8: supplier intelligence. Price history, lead-time statistics and the scorecard are computed from the
-- documents as they stand (orders, receipts, invoices, matches, returns, quotes): nothing is snapshotted. Only the
-- scoring weights, the on-time tolerance and the look-back window are configuration, one row per company.

CREATE TABLE app.pur_scoring_settings (
  tenant_id                uuid NOT NULL REFERENCES control.tenants (id),
  id                       uuid NOT NULL,
  company_id               uuid NOT NULL,
  on_time_weight           numeric(5,2) NOT NULL DEFAULT 30 CHECK (on_time_weight >= 0),
  quantity_weight          numeric(5,2) NOT NULL DEFAULT 25 CHECK (quantity_weight >= 0),
  price_weight             numeric(5,2) NOT NULL DEFAULT 20 CHECK (price_weight >= 0),
  invoice_weight           numeric(5,2) NOT NULL DEFAULT 25 CHECK (invoice_weight >= 0),
  on_time_tolerance_days   int NOT NULL DEFAULT 0 CHECK (on_time_tolerance_days >= 0),
  lookback_months          int NOT NULL DEFAULT 12 CHECK (lookback_months BETWEEN 1 AND 60),
  updated_by               uuid,
  created_at               timestamptz NOT NULL DEFAULT now(),
  updated_at               timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CALL app.enable_tenant_rls('app.pur_scoring_settings');
CALL app.track_updated_at('app.pur_scoring_settings');
