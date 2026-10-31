-- Expense categories as a posting key (A-174): an expense category names the account its lines land on; the
-- Expenses module keeps that as a posting rule (role Expense, key expense_category_id) in the company's profile,
-- so documents keep posting by role and the admin sees the mapping among the other rules.
ALTER TABLE app.gl_posting_rules ADD COLUMN expense_category_id uuid;
ALTER TABLE app.gl_posting_rules ADD FOREIGN KEY (tenant_id, expense_category_id) REFERENCES app.exp_categories (tenant_id, id);

DROP INDEX app.gl_posting_rules_unique_idx;
CREATE UNIQUE INDEX gl_posting_rules_unique_idx ON app.gl_posting_rules (
  tenant_id, profile_id, account_role, coalesce(document_type, ''),
  coalesce(item_posting_group_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(partner_posting_group_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(tax_code_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(warehouse_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(branch_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(bank_account_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(asset_category_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(charge_type_id, '00000000-0000-0000-0000-000000000000'::uuid),
  coalesce(expense_category_id, '00000000-0000-0000-0000-000000000000'::uuid));
