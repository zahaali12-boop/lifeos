-- M5 slice 5.5b (part 1): a lot-tracked item can now ship (roadmap "Pick lists (FEFO/bin sequence)", the FEFO half).
-- ShipmentService picks, at post time, the single earliest-expiring lot with enough available stock to cover the
-- whole line (first-expiry-first-out, IFefoSuggestions.SuggestAsync -- already built for 3.5's own lot screen, never
-- called from another module until now); a quantity no single lot covers is refused, asking for a smaller quantity
-- or a second shipment, exactly as Purchasing's own receipt lines name a lot by its number, not by a cross-module id.
-- Serial-tracked items, a real pick-list document with bin-sequence ordering, a mobile picking screen and packages
-- all remain deferred (still 5.5b/5.5c).

ALTER TABLE app.sls_shipment_lines ADD COLUMN lot_number text;
