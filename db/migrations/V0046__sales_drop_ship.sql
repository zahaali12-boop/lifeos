-- M5 slice 5.4c: drop-ship. A line marked drop-ship skips reservation on confirmation (the supplier ships straight
-- to the customer, never touching the company's own warehouse) and, once the buyer raises the purchase order for it
-- from the order screen, carries that purchase order line's id for traceability -- DOMAIN_MODEL §11's own sketch,
-- built now that the order exists to carry it. No FK: item, uom and, here, another module's order line all stay
-- plain uuids validated at the application layer, the convention every sls_* and pur_* document already follows.

ALTER TABLE app.sls_order_lines
  ADD COLUMN drop_ship boolean NOT NULL DEFAULT false,
  ADD COLUMN purchase_order_line_id uuid;
