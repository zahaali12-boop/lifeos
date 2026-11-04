-- M3 slice 3.9: planner statistics after a bulk load. A load that inserts tens of thousands of rows inside one
-- transaction (the demo seed, an import) leaves the planner believing the tables are empty until autovacuum runs
-- after the commit, so every query in the rest of that transaction (the costing walks, the invariant harness) is
-- planned as if for a handful of rows. The application role does not own the tables and may not ANALYZE them;
-- this owner-defined function does it for the ledgers and masters a bulk load fills. ANALYZE sees the caller's
-- own uncommitted rows, so the refreshed statistics help the rest of the same transaction.
CREATE OR REPLACE FUNCTION app.refresh_statistics() RETURNS void
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, app AS $$
DECLARE
  t text;
BEGIN
  FOREACH t IN ARRAY ARRAY[
    'app.gl_journal_entries', 'app.gl_journal_lines', 'app.gl_balances',
    'app.inv_stock_ledger_entries', 'app.inv_stock_value_entries', 'app.inv_item_applications', 'app.inv_item_costs', 'app.inv_item_cost_scopes', 'app.inv_stock_balances',
    'app.inv_lots', 'app.inv_serials', 'app.inv_serial_events',
    'app.itm_items', 'app.itm_item_uoms', 'app.itm_item_barcodes', 'app.itm_item_variants', 'app.itm_item_suppliers', 'app.itm_item_warehouse_settings',
    'app.aud_events', 'app.num_allocations']
  LOOP
    IF to_regclass(t) IS NOT NULL THEN
      EXECUTE format('ANALYZE %s', t);
    END IF;
  END LOOP;
END $$;

REVOKE ALL ON FUNCTION app.refresh_statistics() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION app.refresh_statistics() TO quicker_app;
