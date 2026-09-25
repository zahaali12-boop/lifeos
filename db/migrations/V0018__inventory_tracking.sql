-- M3 slice 3.5: lots and serials — tracking on every movement, expiry and FEFO, lot status and recall with
-- traceability, the serial history (hard scenarios 12 and 13).

CREATE TABLE app.inv_lots (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  item_id               uuid NOT NULL,
  lot_number            text NOT NULL,
  manufactured_on       date,
  expires_on            date,
  supplier_lot          text,
  supplier_partner_id   uuid,
  status                text NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'quarantine', 'recalled', 'expired', 'consumed')),
  status_reason         text,
  recall_reference      text,
  status_changed_at     timestamptz,
  custom_fields         jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, item_id, lot_number),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  CHECK (status <> 'recalled' OR recall_reference IS NOT NULL)
);
CREATE INDEX inv_lots_expiry_idx ON app.inv_lots (tenant_id, expires_on) WHERE expires_on IS NOT NULL;
CALL app.enable_tenant_rls('app.inv_lots');
CALL app.track_updated_at('app.inv_lots');

CREATE TABLE app.inv_serials (
  tenant_id             uuid NOT NULL,
  id                    uuid NOT NULL,
  item_id               uuid NOT NULL,
  serial_number         text NOT NULL,
  lot_id                uuid,
  status                text NOT NULL DEFAULT 'in_stock' CHECK (status IN ('in_stock', 'in_transit', 'sold', 'returned', 'in_repair', 'scrapped', 'consumed', 'consigned', 'returned_to_supplier')),
  current_warehouse_id  uuid,
  current_bin_id        uuid,
  current_partner_id    uuid,
  warranty_until        date,
  custom_fields         jsonb NOT NULL DEFAULT '{}'::jsonb,
  created_at            timestamptz NOT NULL DEFAULT now(),
  updated_at            timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, item_id, serial_number),
  FOREIGN KEY (tenant_id, item_id) REFERENCES app.itm_items (tenant_id, id),
  FOREIGN KEY (tenant_id, lot_id) REFERENCES app.inv_lots (tenant_id, id),
  FOREIGN KEY (tenant_id, current_warehouse_id) REFERENCES app.inv_warehouses (tenant_id, id)
);
CREATE INDEX inv_serials_status_idx ON app.inv_serials (tenant_id, item_id, status);
CALL app.enable_tenant_rls('app.inv_serials');
CALL app.track_updated_at('app.inv_serials');

-- The serial's timeline: every movement and every status change, so its full history is one query (scenario 13).
CREATE TABLE app.inv_serial_events (
  tenant_id       uuid NOT NULL,
  id              uuid NOT NULL,
  serial_id       uuid NOT NULL,
  at              timestamptz NOT NULL DEFAULT now(),
  posting_date    date,
  kind            text NOT NULL CHECK (kind IN ('movement', 'status')),
  entry_type      text,
  sle_id          uuid,
  from_status     text,
  to_status       text NOT NULL,
  warehouse_id    uuid,
  partner_id      uuid,
  source_document_type text,
  source_document_id   uuid,
  note            text,
  actor_user_id   uuid,
  PRIMARY KEY (tenant_id, id),
  FOREIGN KEY (tenant_id, serial_id) REFERENCES app.inv_serials (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, sle_id) REFERENCES app.inv_stock_ledger_entries (tenant_id, id)
);
CREATE INDEX inv_serial_events_serial_idx ON app.inv_serial_events (tenant_id, serial_id, at);
CALL app.enable_tenant_rls('app.inv_serial_events');
CALL app.make_append_only('app.inv_serial_events');

-- Movements now reference their lot and serial, and carry the document's counterparty for traceability.
ALTER TABLE app.inv_stock_ledger_entries
  ADD COLUMN partner_id uuid,
  ADD FOREIGN KEY (tenant_id, lot_id) REFERENCES app.inv_lots (tenant_id, id),
  ADD FOREIGN KEY (tenant_id, serial_id) REFERENCES app.inv_serials (tenant_id, id);
CREATE INDEX inv_sle_lot_idx ON app.inv_stock_ledger_entries (tenant_id, lot_id) WHERE lot_id IS NOT NULL;
CREATE INDEX inv_sle_serial_idx ON app.inv_stock_ledger_entries (tenant_id, serial_id) WHERE serial_id IS NOT NULL;
ALTER TABLE app.inv_reservations ADD FOREIGN KEY (tenant_id, lot_id) REFERENCES app.inv_lots (tenant_id, id);
ALTER TABLE app.inv_reservations ADD FOREIGN KEY (tenant_id, serial_id) REFERENCES app.inv_serials (tenant_id, id);

-- Document lines name their lot (by number, creating it on receipt with its expiry) and their serial numbers.
ALTER TABLE app.inv_adjustment_lines ADD COLUMN lot_number text, ADD COLUMN expires_on date, ADD COLUMN serial_numbers text[] NOT NULL DEFAULT '{}';
ALTER TABLE app.inv_assembly_lines ADD COLUMN lot_number text, ADD COLUMN expires_on date, ADD COLUMN serial_numbers text[] NOT NULL DEFAULT '{}';
ALTER TABLE app.inv_assemblies ADD COLUMN output_lot_number text, ADD COLUMN output_expires_on date, ADD COLUMN output_serial_numbers text[] NOT NULL DEFAULT '{}';
ALTER TABLE app.inv_transfer_lines ADD COLUMN lot_id uuid, ADD COLUMN serial_numbers text[] NOT NULL DEFAULT '{}',
  ADD FOREIGN KEY (tenant_id, lot_id) REFERENCES app.inv_lots (tenant_id, id);
