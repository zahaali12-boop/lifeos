-- Post-M4 fix: two of the default segregation-of-duties rules named permissions the catalogue never had, so they could
-- never trip. Supplier payments are released with banking.payment.post (4.7), not payables.payment.post; purchase-order
-- approval is a workflow step, not a permission, so the buying-versus-payables rule pairs raising orders
-- (purchasing.order.manage) with posting supplier invoices. New tenants get the corrected pairs from DefaultSodRules;
-- this corrects the system rules existing tenants were seeded with. A tenant that already added a corrected pair
-- itself keeps its own rule, and the stale system rule is left for it to retire. The table forces row-level security,
-- so the update runs tenant by tenant.
DO $$
DECLARE
  t uuid;
BEGIN
  FOR t IN SELECT id FROM control.tenants LOOP
    PERFORM set_config('app.tenant_id', t::text, true);

    UPDATE app.idn_sod_rules r
    SET permission_b = 'banking.payment.post'
    WHERE r.is_system AND r.permission_a = 'partners.supplier.manage' AND r.permission_b = 'payables.payment.post'
      AND NOT EXISTS (
        SELECT 1 FROM app.idn_sod_rules x
        WHERE x.tenant_id = r.tenant_id AND x.permission_a = 'partners.supplier.manage' AND x.permission_b = 'banking.payment.post');

    UPDATE app.idn_sod_rules r
    SET permission_a = 'purchasing.order.manage',
        rationale_i18n = jsonb_build_object(
          'en', 'Raising purchase orders and posting supplier invoices should be separated.',
          'ar', 'يفضل فصل إصدار أوامر الشراء عن ترحيل فواتير الموردين.')
    WHERE r.is_system AND r.permission_a = 'purchasing.order.approve' AND r.permission_b = 'purchasing.invoice.post'
      AND NOT EXISTS (
        SELECT 1 FROM app.idn_sod_rules x
        WHERE x.tenant_id = r.tenant_id AND x.permission_a = 'purchasing.order.manage' AND x.permission_b = 'purchasing.invoice.post');
  END LOOP;
  PERFORM set_config('app.tenant_id', '', true);
END
$$;
