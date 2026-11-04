-- M5 slice 5.5b (part 3): pick lists (DOMAIN_MODEL §9 "inv_pick_lists", A-154). A pick list is the warehouse's own
-- document for taking stock off the shelves for another document (a sales shipment today; transfers and assembly
-- builds can use the same contract later). Released from its source document, it is planned by the inventory module:
-- lots first-expiry-first-out, then the bins in their pick sequence (the walking order), serials oldest received
-- first, never counting stock another open pick list already claims. A picker then confirms each line on a handheld
-- (quantity, and the bin, lot and serials actually taken), short-picking with a reason when the shelf is short.
-- Nothing moves in the stock ledger here: the source document posts what was picked and closes the list.

CREATE TABLE app.inv_pick_lists (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  warehouse_id          uuid NOT NULL,
  number                text NOT NULL,
  source_document_type  text NOT NULL,
  source_document_id    uuid NOT NULL,
  source_number         text NOT NULL,
  -- The document whose reservations hold the stock (a shipment's sales order): its own holds count as available to it.
  reserved_for_type     text NOT NULL,
  reserved_for_id       uuid NOT NULL,
  status             text NOT NULL DEFAULT 'released' CHECK (status IN ('released', 'in_progress', 'picked', 'closed', 'cancelled')),
  assigned_to           uuid,
  assigned_at           timestamptz,
  started_at            timestamptz,
  completed_at          timestamptz,
  closed_at             timestamptz,
  cancelled_at          timestamptz,
  cancel_reason         text,
  released_by           uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, number),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
-- One live pick list per source document; a cancelled one can be followed by a new release.
CREATE UNIQUE INDEX inv_pick_lists_source_idx ON app.inv_pick_lists (tenant_id, source_document_type, source_document_id) WHERE status <> 'cancelled';
CREATE INDEX inv_pick_lists_warehouse_idx ON app.inv_pick_lists (tenant_id, warehouse_id, status);
CREATE INDEX inv_pick_lists_assignee_idx ON app.inv_pick_lists (tenant_id, assigned_to, status);
CALL app.enable_tenant_rls('app.inv_pick_lists');
CALL app.track_updated_at('app.inv_pick_lists');

-- One line per place to go: line_no is the walking order. The planned bin, lot and serials stay as released; the
-- picked_* columns record what the picker actually took (the same, or another bin, lot or serials).
CREATE TABLE app.inv_pick_lines (
  tenant_id              uuid NOT NULL,
  id                     uuid NOT NULL,
  pick_list_id           uuid NOT NULL,
  line_no                int NOT NULL,
  source_line_id         uuid NOT NULL,
  item_id                uuid NOT NULL,
  variant_id             uuid,
  bin_id                 uuid,
  lot_id                 uuid,
  serial_numbers         jsonb NOT NULL DEFAULT '[]'::jsonb,
  qty_to_pick            numeric(24, 9) NOT NULL CHECK (qty_to_pick > 0),
  qty_picked             numeric(24, 9) NOT NULL DEFAULT 0 CHECK (qty_picked >= 0 AND qty_picked <= qty_to_pick),
  picked_bin_id          uuid,
  picked_lot_id          uuid,
  picked_serial_numbers  jsonb NOT NULL DEFAULT '[]'::jsonb,
  status                 text NOT NULL DEFAULT 'open' CHECK (status IN ('open', 'picked', 'short')),
  short_reason           text,
  picked_by              uuid,
  picked_at              timestamptz,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, pick_list_id, line_no),
  FOREIGN KEY (tenant_id, pick_list_id) REFERENCES app.inv_pick_lists (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, bin_id) REFERENCES app.inv_bins (tenant_id, id),
  FOREIGN KEY (tenant_id, lot_id) REFERENCES app.inv_lots (tenant_id, id),
  FOREIGN KEY (tenant_id, picked_bin_id) REFERENCES app.inv_bins (tenant_id, id),
  FOREIGN KEY (tenant_id, picked_lot_id) REFERENCES app.inv_lots (tenant_id, id)
);
CREATE INDEX inv_pick_lines_item_idx ON app.inv_pick_lines (tenant_id, item_id);
CALL app.enable_tenant_rls('app.inv_pick_lines');
