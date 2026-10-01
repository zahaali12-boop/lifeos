-- Cost centres (and the other dimensions) on sales documents, carried to the cost of goods sold (A-157).
--
-- A quotation, order or shipment line names its dimension set; an order converted from a quotation and a shipment
-- drawn from an order inherit the line's set. The stock engine keeps the set on the movement it writes and on every
-- value entry of that movement, so the cost of goods sold of a shipment -- and every later cost adjustment of it --
-- lands in the journal on the line's cost centre. The inventory side of those journals stays without dimensions (a
-- balance-sheet account). Rows written before this change keep no set.

ALTER TABLE app.sls_quotation_lines ADD COLUMN dimension_set_id uuid;
ALTER TABLE app.sls_quotation_lines ADD CONSTRAINT sls_quotation_lines_dimension_set_fk FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id);

ALTER TABLE app.sls_order_lines ADD COLUMN dimension_set_id uuid;
ALTER TABLE app.sls_order_lines ADD CONSTRAINT sls_order_lines_dimension_set_fk FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id);

ALTER TABLE app.sls_shipment_lines ADD COLUMN dimension_set_id uuid;
ALTER TABLE app.sls_shipment_lines ADD CONSTRAINT sls_shipment_lines_dimension_set_fk FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id);

ALTER TABLE app.inv_stock_ledger_entries ADD COLUMN dimension_set_id uuid;
ALTER TABLE app.inv_stock_ledger_entries ADD CONSTRAINT inv_stock_ledger_entries_dimension_set_fk FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id);

ALTER TABLE app.inv_stock_value_entries ADD COLUMN dimension_set_id uuid;
ALTER TABLE app.inv_stock_value_entries ADD CONSTRAINT inv_stock_value_entries_dimension_set_fk FOREIGN KEY (tenant_id, dimension_set_id) REFERENCES app.org_dimension_sets (tenant_id, id);
