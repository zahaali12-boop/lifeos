-- V0015: warehouses and the stock ledger (roadmap 3.2, DOMAIN_MODEL §9, ADR-0008 for the quantity side).
-- Warehouses (kinds, bins), stock postings and their append-only ledger entries in the item's base unit with a
-- global sequence, derived balances per (company, item, variant, warehouse, bin, lot, serial) maintained under row
-- locks (hard scenario 4), reservations, and two-step transfers through an in-transit warehouse. Value entries and
-- the GL side arrive with the costing engine (3.3); lots and serials with 3.5 (their ids are plain uuids until then).

CREATE TABLE app.inv_warehouses (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  branch_id             uuid,
  code                  text NOT NULL,
  name_i18n             jsonb NOT NULL DEFAULT '{}'::jsonb,
  kind                  text NOT NULL DEFAULT 'standard' CHECK (kind IN ('standard', 'in_transit', 'consignment', 'quarantine', 'virtual')),
  bins_enabled          boolean NOT NULL DEFAULT false,
  dimension_value_id    uuid,
  allow_negative_stock  boolean,
  address               jsonb NOT NULL DEFAULT '{}'::jsonb,
  is_active             boolean NOT NULL DEFAULT true,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, company_id, code),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, branch_id) REFERENCES app.org_branches (tenant_id, id),
  FOREIGN KEY (tenant_id, dimension_value_id) REFERENCES app.org_dimension_values (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_warehouses');
CALL app.track_updated_at('app.inv_warehouses');

CREATE TABLE app.inv_bins (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  warehouse_id   uuid NOT NULL,
  code           text NOT NULL,
  zone           text,
  kind           text NOT NULL DEFAULT 'storage' CHECK (kind IN ('storage', 'receiving', 'shipping', 'quarantine', 'returns')),
  pick_sequence  int NOT NULL DEFAULT 0,
  is_active      boolean NOT NULL DEFAULT true,
  created_at     timestamptz NOT NULL DEFAULT now(),
  updated_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, warehouse_id, code),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.inv_bins');
CALL app.track_updated_at('app.inv_bins');

-- Item settings that name a warehouse now reference it.
ALTER TABLE app.itm_item_warehouse_settings
  ADD FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id) ON DELETE CASCADE;
ALTER TABLE app.itm_item_company_settings
  ADD FOREIGN KEY (tenant_id, default_warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id);

-- One posting = the entries one document movement wrote in one transaction (replayable by idempotency key).
CREATE TABLE app.inv_stock_postings (
  tenant_id             uuid NOT NULL REFERENCES control.tenants (id),
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  posting_date          date NOT NULL,
  fiscal_period_id      uuid,
  source_document_type  text NOT NULL,
  source_document_id    uuid NOT NULL,
  entry_count           int NOT NULL CHECK (entry_count > 0),
  idempotency_key       text,
  posted_by             uuid,
  posted_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id)
);
CREATE UNIQUE INDEX inv_stock_postings_idempotency_idx ON app.inv_stock_postings (tenant_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX inv_stock_postings_source_idx ON app.inv_stock_postings (tenant_id, source_document_type, source_document_id);
CALL app.enable_tenant_rls('app.inv_stock_postings');
CALL app.make_append_only('app.inv_stock_postings');

CREATE TABLE app.inv_stock_ledger_entries (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  sequence              bigint GENERATED ALWAYS AS IDENTITY,
  posting_id            uuid NOT NULL,
  company_id            uuid NOT NULL,
  item_id               uuid NOT NULL,
  variant_id            uuid,
  warehouse_id          uuid NOT NULL,
  bin_id                uuid,
  lot_id                uuid,
  serial_id             uuid,
  entry_type            text NOT NULL CHECK (entry_type IN ('purchase_receipt', 'purchase_return', 'sale_shipment', 'sale_return', 'transfer_out', 'transfer_in', 'positive_adjustment', 'negative_adjustment', 'scrap', 'count_variance', 'assembly_consumption', 'assembly_output', 'consignment_in', 'consignment_out', 'drop_ship', 'opening')),
  quantity              numeric(24, 9) NOT NULL CHECK (quantity <> 0),
  entered_uom_id        uuid NOT NULL,
  entered_quantity      numeric(24, 9) NOT NULL,
  posting_date          date NOT NULL,
  fiscal_period_id      uuid,
  source_document_type  text NOT NULL,
  source_document_id    uuid NOT NULL,
  source_line_id        uuid,
  ownership             text NOT NULL DEFAULT 'own' CHECK (ownership IN ('own', 'consigned_in', 'consigned_out')),
  owner_partner_id      uuid,
  remaining_quantity    numeric(24, 9) NOT NULL DEFAULT 0,
  cost_is_expected      boolean NOT NULL DEFAULT false,
  costed_at_expected    boolean NOT NULL DEFAULT false,
  transfer_pair_id      uuid,
  reservation_id        uuid,
  posted_by             uuid,
  posted_at             timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, sequence),
  FOREIGN KEY (tenant_id, posting_id) REFERENCES app.inv_stock_postings (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, bin_id) REFERENCES app.inv_bins (tenant_id, id),
  FOREIGN KEY (tenant_id, entered_uom_id) REFERENCES app.org_uoms (tenant_id, id),
  -- Inbound types add stock, outbound types take it; only a count variance and a drop-ship pair go either way.
  CHECK (CASE
           WHEN entry_type IN ('purchase_receipt', 'sale_return', 'transfer_in', 'positive_adjustment', 'assembly_output', 'consignment_in', 'opening') THEN quantity > 0
           WHEN entry_type IN ('purchase_return', 'sale_shipment', 'transfer_out', 'negative_adjustment', 'scrap', 'assembly_consumption', 'consignment_out') THEN quantity < 0
           ELSE true END),
  CHECK (remaining_quantity >= 0)
);
CREATE INDEX inv_sle_item_idx ON app.inv_stock_ledger_entries (tenant_id, company_id, item_id, warehouse_id, posting_date);
CREATE INDEX inv_sle_source_idx ON app.inv_stock_ledger_entries (tenant_id, source_document_type, source_document_id);
CREATE INDEX inv_sle_posting_idx ON app.inv_stock_ledger_entries (tenant_id, posting_id);
CALL app.enable_tenant_rls('app.inv_stock_ledger_entries');
CALL app.make_append_only('app.inv_stock_ledger_entries');

-- Derived balances: the nil uuid stands for "none" in the optional key parts so the key stays NOT NULL.
CREATE TABLE app.inv_stock_balances (
  tenant_id         uuid NOT NULL,
  company_id        uuid NOT NULL,
  item_id           uuid NOT NULL,
  variant_id        uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'::uuid,
  warehouse_id      uuid NOT NULL,
  bin_id            uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'::uuid,
  lot_id            uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'::uuid,
  serial_id         uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000'::uuid,
  on_hand           numeric(24, 9) NOT NULL DEFAULT 0,
  reserved          numeric(24, 9) NOT NULL DEFAULT 0 CHECK (reserved >= 0),
  quality_hold      numeric(24, 9) NOT NULL DEFAULT 0 CHECK (quality_hold >= 0),
  last_movement_at  timestamptz,
  updated_at        timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CREATE INDEX inv_stock_balances_warehouse_idx ON app.inv_stock_balances (tenant_id, warehouse_id, item_id);
CALL app.enable_tenant_rls('app.inv_stock_balances');
CALL app.track_updated_at('app.inv_stock_balances');

CREATE TABLE app.inv_reservations (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  item_id               uuid NOT NULL,
  variant_id            uuid,
  warehouse_id          uuid NOT NULL,
  bin_id                uuid,
  lot_id                uuid,
  serial_id             uuid,
  quantity              numeric(24, 9) NOT NULL CHECK (quantity > 0),
  consumed_quantity     numeric(24, 9) NOT NULL DEFAULT 0 CHECK (consumed_quantity >= 0 AND consumed_quantity <= quantity),
  source_document_type  text NOT NULL,
  source_document_id    uuid NOT NULL,
  source_line_id        uuid,
  status                text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'consumed', 'released')),
  expires_on            date,
  reason                text,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  closed_at             timestamptz,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, bin_id) REFERENCES app.inv_bins (tenant_id, id)
);
CREATE INDEX inv_reservations_stock_idx ON app.inv_reservations (tenant_id, company_id, item_id, warehouse_id) WHERE status = 'active';
CREATE INDEX inv_reservations_source_idx ON app.inv_reservations (tenant_id, source_document_type, source_document_id);
CALL app.enable_tenant_rls('app.inv_reservations');

CREATE TABLE app.inv_transfers (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  company_id            uuid NOT NULL,
  number                text,
  from_warehouse_id     uuid NOT NULL,
  to_warehouse_id       uuid NOT NULL,
  transit_warehouse_id  uuid,
  ship_date             date,
  receive_date          date,
  status                text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'shipped', 'partially_received', 'received', 'cancelled')),
  ship_posting_id       uuid,
  receive_posting_id    uuid,
  reference             text,
  notes                 text,
  custom_fields         jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_by            uuid,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, company_id) REFERENCES app.org_companies (tenant_id, id),
  FOREIGN KEY (tenant_id, from_warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, to_warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  FOREIGN KEY (tenant_id, transit_warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id),
  CHECK (from_warehouse_id <> to_warehouse_id)
);
CREATE UNIQUE INDEX inv_transfers_number_idx ON app.inv_transfers (tenant_id, company_id, number) WHERE number IS NOT NULL;
CALL app.enable_tenant_rls('app.inv_transfers');
CALL app.track_updated_at('app.inv_transfers');

CREATE TABLE app.inv_transfer_lines (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  transfer_id    uuid NOT NULL,
  line_no        int NOT NULL,
  item_id        uuid NOT NULL,
  variant_id     uuid,
  qty_requested  numeric(24, 9) NOT NULL CHECK (qty_requested > 0),
  qty_shipped    numeric(24, 9) NOT NULL DEFAULT 0 CHECK (qty_shipped >= 0),
  qty_received   numeric(24, 9) NOT NULL DEFAULT 0 CHECK (qty_received >= 0 AND qty_received <= qty_shipped),
  uom_id         uuid NOT NULL,
  from_bin_id    uuid,
  to_bin_id      uuid,
  tracking       jsonb NOT NULL DEFAULT '{}'::jsonb,
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, transfer_id, line_no),
  FOREIGN KEY (tenant_id, transfer_id) REFERENCES app.inv_transfers (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, variant_id) REFERENCES app.itm_item_variants (tenant_id, id),
  FOREIGN KEY (tenant_id, uom_id) REFERENCES app.org_uoms (tenant_id, id),
  FOREIGN KEY (tenant_id, from_bin_id) REFERENCES app.inv_bins (tenant_id, id),
  FOREIGN KEY (tenant_id, to_bin_id) REFERENCES app.inv_bins (tenant_id, id)
);
CALL app.enable_tenant_rls('app.inv_transfer_lines');
