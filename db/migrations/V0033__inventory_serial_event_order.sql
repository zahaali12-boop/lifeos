-- A serial's history is read in the order its events were written. Several events can share an instant (one posting
-- moves a serial out of a warehouse and into transit, and a test clock stands still), and ids made in the same
-- millisecond are not ordered, so the events get an insertion sequence to sort by. Existing rows are numbered in
-- their stored order.
ALTER TABLE app.inv_serial_events ADD COLUMN seq bigint GENERATED ALWAYS AS IDENTITY;
DROP INDEX app.inv_serial_events_serial_idx;
CREATE INDEX inv_serial_events_serial_idx ON app.inv_serial_events (tenant_id, serial_id, seq);
