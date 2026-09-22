-- Statutory charts (platform reference data, idempotent).
-- IRAQ_UAS: the top-level groups of the Iraqi Unified Accounting System, as the mapping target for statutory
-- reports. The group level is what the mapping mechanism needs today; the detailed statutory codes below the
-- groups are added once validated with an Iraqi accountant (ASSUMPTIONS Q9), so this list is marked provisional.
INSERT INTO control.gl_statutory_charts (code, name_i18n, accounts, notes) VALUES
  ('IRAQ_UAS',
   '{"en":"Iraq Unified Accounting System","ar":"النظام المحاسبي الموحد – العراق"}',
   '[
     {"code":"1","level":1,"name":{"en":"Fixed assets","ar":"الموجودات الثابتة"}},
     {"code":"2","level":1,"name":{"en":"Inventories","ar":"المخزون"}},
     {"code":"3","level":1,"name":{"en":"Receivables and cash","ar":"الحسابات المدينة والنقد"}},
     {"code":"4","level":1,"name":{"en":"Capital, reserves and long-term financing","ar":"رأس المال والاحتياطيات والتمويل طويل الأجل"}},
     {"code":"5","level":1,"name":{"en":"Current liabilities","ar":"الحسابات الدائنة والمطلوبات المتداولة"}},
     {"code":"6","level":1,"name":{"en":"Current uses (expenses)","ar":"الاستخدامات الجارية (المصروفات)"}},
     {"code":"7","level":1,"name":{"en":"Current resources (revenues)","ar":"الموارد الجارية (الإيرادات)"}},
     {"code":"8","level":1,"name":{"en":"Result accounts","ar":"حسابات النتيجة"}},
     {"code":"9","level":1,"name":{"en":"Memorandum accounts","ar":"الحسابات النظامية"}}
   ]',
   'provisional: group level only; detailed codes pending validation (ASSUMPTIONS Q9)')
ON CONFLICT (code) DO UPDATE SET name_i18n = EXCLUDED.name_i18n, accounts = EXCLUDED.accounts, notes = EXCLUDED.notes, updated_at = now();
