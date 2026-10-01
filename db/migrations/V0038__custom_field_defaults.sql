-- A custom field's default value: filled in whenever a record is saved without a value for the field, so a required
-- field does not refuse the drafts the system creates for someone (orders from replenishment suggestions, payments
-- from a payment proposal) and new forms start from it. Null means no default. Kept normalised by the field's type.
ALTER TABLE app.col_custom_fields ADD COLUMN default_value jsonb;
