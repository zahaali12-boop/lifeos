-- M3 slice 3.6: stock counts — sessions with a freeze snapshot and the ledger sequence at freeze, count sheets,
-- recounts, variance review and approval, posting with reason codes (hard scenario 11).

CREATE TABLE app.inv_counts (
  tenant_id          uuid NOT NULL,
  id                 uuid NOT NULL,
  company_id         uuid NOT NULL,
  number             text,
  warehouse_id       uuid NOT NULL,
  scope              text NOT NULL DEFAULT 'full' CHECK (scope IN ('full', 'cycle', 'bins', 'items')),
  scope_filter       jsonb NOT NULL DEFAULT '{}'::jsonb,
  posting_date       date NOT NULL,
  blind              boolean NOT NULL DEFAULT false,
  block_movements    boolean NOT NULL DEFAULT false,
  frozen_at          timestamptz,
  last_sequence      bigint,
  status             text NOT NULL DEFAULT 'planned' CHECK (status IN ('planned', 'frozen', 'counting', 'review', 'approved', 'posted', 'cancelled')),
  notes              text,
  approved_by        uuid,
  approved_at        timestamptz,
  stock_posting_id   uuid,
  journal_entry_id   uuid,
  posted_by          uuid,
  posted_at          timestamptz,
  created_by         uuid,
  created_at         timestamptz NOT NULL DEFAULT now(),
  updated_at         timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CREATE UNIQUE INDEX inv_counts_number_idx ON app.inv_counts (tenant_id, company_id, number) WHERE number IS NOT NULL;
CREATE INDEX inv_counts_warehouse_idx ON app.inv_counts (tenant_id, warehouse_id, status);
CALL app.enable_tenant_rls('app.inv_counts');
CALL app.track_updated_at('app.inv_counts');

-- What the ledger said at freeze, per balance key (nil uuids for "none"), never changed afterwards.
CREATE TABLE app.inv_count_snapshots (
  tenant_id      uuid NOT NULL,
  count_id       uuid NOT NULL,
  item_id        uuid NOT NULL,
  variant_id     uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  bin_id         uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  lot_id         uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  serial_id      uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
  expected_qty   numeric(24, 9) NOT NULL,
  PRIMARY KEY (tenant_id, count_id, item_id, variant_id, bin_id, lot_id, serial_id),
  FOREIGN KEY (tenant_id, count_id) REFERENCES app.inv_counts (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_count_snapshots');
CALL app.make_append_only('app.inv_count_snapshots');

CREATE TABLE app.inv_count_lines (
  tenant_id               uuid NOT NULL,
  id                      uuid NOT NULL,
  count_id                uuid NOT NULL,
  line_no                 int NOT NULL,
  item_id                 uuid NOT NULL,
  variant_id              uuid,
  bin_id                  uuid,
  lot_id                  uuid,
  serial_id               uuid,
  expected_qty            numeric(24, 9) NOT NULL DEFAULT 0,
  counted_qty             numeric(24, 9),
  previous_counted_qty    numeric(24, 9),
  movement_since_freeze   numeric(24, 9) NOT NULL DEFAULT 0,
  variance_qty            numeric(24, 9) NOT NULL DEFAULT 0,
  variance_value          numeric(24, 6) NOT NULL DEFAULT 0,
  counted_by              uuid,
  counted_at              timestamptz,
  recount_requested       boolean NOT NULL DEFAULT false,
  reason_code_id          uuid,
  note                    text,
  status                  text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'counted', 'recount', 'skipped', 'posted')),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, count_id, line_no),
  FOREIGN KEY (tenant_id, count_id) REFERENCES app.inv_counts (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, bin_id) REFERENCES app.inv_bins (tenant_id, id),
  FOREIGN KEY (tenant_id, lot_id) REFERENCES app.inv_lots (tenant_id, id),
  FOREIGN KEY (tenant_id, serial_id) REFERENCES app.inv_serials (tenant_id, id),
  FOREIGN KEY (tenant_id, reason_code_id) REFERENCES app.inv_reason_codes (tenant_id, id)
);
CREATE UNIQUE INDEX inv_count_lines_key_idx ON app.inv_count_lines (tenant_id, count_id, item_id, coalesce(variant_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(bin_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(lot_id, '00000000-0000-0000-0000-000000000000'::uuid), coalesce(serial_id, '00000000-0000-0000-0000-000000000000'::uuid));
CALL app.enable_tenant_rls('app.inv_count_lines');
