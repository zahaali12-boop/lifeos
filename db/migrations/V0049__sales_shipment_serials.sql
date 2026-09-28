-- M5 slice 5.5b (part 2): a serial-tracked item can now ship. ShipmentService picks, at post time, the item's own
-- on-hand serials in the line's warehouse, oldest received first (first in, first out -- serials carry no expiry the
-- way a lot does, so receipt order is the natural stand-in), through ISerialSuggestions (already built for 3.5's own
-- serial screen, never called from another module until now); not enough on hand is refused. A lot-and-serial item
-- (both tracked at once) remains deferred, since it needs a lot chosen first and then serials from within it -- a
-- real pick-list document with bin-sequence ordering, a mobile picking screen and packages all remain deferred too.

ALTER TABLE app.sls_shipment_lines ADD COLUMN serial_numbers jsonb NOT NULL DEFAULT '[]'::jsonb;
