-- M5 slice 5.5b (part 3): shipments are picked and packed (A-154).
--
-- Picking. A draft shipment can be released for picking: the inventory module plans a pick list (V0050) and the
-- shipment waits in 'picking' until every line is picked or short-picked; posting then ships exactly what was picked
-- (short picks reduce the line, and the rest of the order line stays reserved for a later shipment). A shipment not
-- released for picking still posts directly, with its stock located the same way at the moment it posts.
--
-- Allocations. One shipment line can now take stock from several bins and lots (a lot-and-serial item ships at last:
-- lot first, then serials from within it). Each line keeps where its quantity came from in `allocations`: per bin
-- and lot, the serials and the quantity, and once posted the stock ledger entries each part produced -- which is what
-- a reversal returns, entry by entry, at each entry's own cost. `bin_id`, `lot_number` and `serial_numbers` stay as a
-- summary (the single bin or lot when there is only one).
--
-- Packages. What went into which box, pallet or envelope, with weight, dimensions and the carrier's tracking number
-- per package; the contents name order lines, which stay the same however often the draft's lines are rebuilt.

ALTER TABLE app.sls_shipments DROP CONSTRAINT sls_shipments_status_check;
ALTER TABLE app.sls_shipments ADD CONSTRAINT sls_shipments_status_check CHECK (status IN ('draft', 'picking', 'posted', 'reversed'));
ALTER TABLE app.sls_shipments ADD COLUMN pick_list_id uuid;
ALTER TABLE app.sls_shipments ADD CONSTRAINT sls_shipments_pick_list_fk FOREIGN KEY (tenant_id, pick_list_id) REFERENCES app.inv_pick_lists (tenant_id, id);

ALTER TABLE app.sls_shipment_lines ADD COLUMN allocations jsonb NOT NULL DEFAULT '[]'::jsonb;

-- Shipments posted before this change: their allocations are rebuilt from the ledger entries they produced, so a
-- reversal of an older shipment follows the same entry-by-entry path as a new one. The tables force row-level
-- security, so this runs tenant by tenant.
DO $$
DECLARE
  t uuid;
BEGIN
  FOR t IN SELECT id FROM control.tenants LOOP
    PERFORM set_config('app.tenant_id', t::text, true);

    WITH entries AS (
      SELECT sl.id AS line_id, le.id AS sle_id, le.sequence, le.bin_id, b.code AS bin_code, le.lot_id, l.lot_number, l.expires_on,
             s.serial_number, -le.quantity AS quantity
      FROM app.sls_shipment_lines sl
      JOIN app.sls_shipments sh ON sh.tenant_id = sl.tenant_id AND sh.id = sl.shipment_id AND sh.status IN ('posted', 'reversed')
      CROSS JOIN LATERAL jsonb_array_elements_text(sl.sle_ids) AS ids (sle_id)
      JOIN app.inv_stock_ledger_entries le ON le.tenant_id = sl.tenant_id AND le.id = ids.sle_id::uuid
      LEFT JOIN app.inv_bins b ON b.tenant_id = le.tenant_id AND b.id = le.bin_id
      LEFT JOIN app.inv_lots l ON l.tenant_id = le.tenant_id AND l.id = le.lot_id
      LEFT JOIN app.inv_serials s ON s.tenant_id = le.tenant_id AND s.id = le.serial_id
    ), parts AS (
      SELECT line_id, min(sequence) AS first_sequence,
             jsonb_build_object(
               'binId', bin_id, 'binCode', bin_code, 'lotId', lot_id, 'lotNumber', lot_number, 'expiresOn', expires_on,
               'serialNumbers', coalesce(jsonb_agg(serial_number ORDER BY sequence) FILTER (WHERE serial_number IS NOT NULL), '[]'::jsonb),
               'quantity', sum(quantity),
               'entries', jsonb_agg(jsonb_build_object('sleId', sle_id, 'serialNumber', serial_number, 'quantity', quantity) ORDER BY sequence)) AS allocation
      FROM entries
      GROUP BY line_id, bin_id, bin_code, lot_id, lot_number, expires_on
    ), lines AS (
      SELECT line_id, jsonb_agg(allocation ORDER BY first_sequence) AS allocations FROM parts GROUP BY line_id
    )
    UPDATE app.sls_shipment_lines sl SET allocations = lines.allocations FROM lines WHERE sl.id = lines.line_id;
  END LOOP;
  PERFORM set_config('app.tenant_id', '', true);
END
$$;

CREATE TABLE app.sls_shipment_packages (
  tenant_id        uuid NOT NULL,
  id               uuid NOT NULL,
  shipment_id      uuid NOT NULL,
  package_no       int NOT NULL CHECK (package_no > 0),
  package_number   text NOT NULL,
  package_type     text NOT NULL DEFAULT 'box' CHECK (package_type IN ('box', 'carton', 'pallet', 'envelope', 'crate', 'drum', 'bag', 'other')),
  weight_kg        numeric(18, 3) CHECK (weight_kg IS NULL OR weight_kg > 0),
  length_cm        numeric(12, 2) CHECK (length_cm IS NULL OR length_cm > 0),
  width_cm         numeric(12, 2) CHECK (width_cm IS NULL OR width_cm > 0),
  height_cm        numeric(12, 2) CHECK (height_cm IS NULL OR height_cm > 0),
  tracking_number  text,
  created_at       timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, shipment_id, package_no),
  FOREIGN KEY (tenant_id, shipment_id) REFERENCES app.sls_shipments (tenant_id, id) ON DELETE CASCADE
);
CALL app.enable_tenant_rls('app.sls_shipment_packages');

CREATE TABLE app.sls_shipment_package_lines (
  tenant_id      uuid NOT NULL,
  id             uuid NOT NULL,
  package_id     uuid NOT NULL,
  order_line_id  uuid NOT NULL,
  quantity       numeric(24, 9) NOT NULL CHECK (quantity > 0),
  PRIMARY KEY (tenant_id, id),
  UNIQUE (tenant_id, package_id, order_line_id),
  FOREIGN KEY (tenant_id, package_id) REFERENCES app.sls_shipment_packages (tenant_id, id) ON DELETE CASCADE,
  FOREIGN KEY (tenant_id, order_line_id) REFERENCES app.sls_order_lines (tenant_id, id)
);
CREATE INDEX sls_shipment_package_lines_order_line_idx ON app.sls_shipment_package_lines (tenant_id, order_line_id);
CALL app.enable_tenant_rls('app.sls_shipment_package_lines');
