-- The daily routines' run log, per company: when a run happened, whether the schedule or a person started it, for
-- which date, and what it posted or generated and what waited and why. Append-only: it is the record of what the
-- routines did, read by accountants checking that the month's reversals, recurring journals and deferrals went out.
CREATE TABLE app.gl_routine_runs (
  tenant_id    uuid NOT NULL REFERENCES control.tenants (id),
  id           uuid NOT NULL,
  company_id   uuid NOT NULL,
  as_of        date NOT NULL,
  trigger      text NOT NULL CHECK (trigger IN ('schedule', 'manual')),
  run_by       uuid,
  run_by_name  text,
  ran_at       timestamptz NOT NULL DEFAULT now(),
  posted       int NOT NULL DEFAULT 0 CHECK (posted >= 0),
  waiting      int NOT NULL DEFAULT 0 CHECK (waiting >= 0),
  items        jsonb NOT NULL DEFAULT '[]',
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  CHECK ((trigger = 'manual') = (run_by IS NOT NULL))
);
CREATE INDEX gl_routine_runs_company_idx ON app.gl_routine_runs (tenant_id, company_id, ran_at DESC);
CALL app.enable_tenant_rls('app.gl_routine_runs');
CALL app.make_append_only('app.gl_routine_runs');
