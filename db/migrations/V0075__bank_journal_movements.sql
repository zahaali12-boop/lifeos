-- V0075: the bank subledger behind journals (A-194, ADR-0035 §11).
-- A journal line on a bank or cash account (an opening balance from the setup wizard, a manual or recurring journal)
-- names the bank account or cash box it moves. The posting engine wrote the ledger line and no movement, so the box's
-- balance (bank screens, the trader's money and health) missed it while the ledger had it. From this version the bank
-- subledger follows journals like the payables and receivables ones (A-140); this script records the movement of
-- every such line already posted, so each box's balance equals its ledger account again. A reversal's lines become
-- the mirror of the movements they undo. Lines in a currency that is neither the box's nor the company's are left for
-- a person: the bank invariant names them.

DO $$
DECLARE
  t record;
BEGIN
  FOR t IN SELECT id FROM control.tenants LOOP
    PERFORM set_config('app.tenant_id', t.id::text, true);

    -- The journals' own movements: an opening balance, a receipt (debit) or a disbursement (credit).
    INSERT INTO app.bnk_bank_transactions (tenant_id, id, bank_account_id, company_id, posting_date, value_date, kind, amount_tc, amount_fc,
                                           reference, source_document_type, source_document_id, journal_entry_id)
    SELECT l.tenant_id,
           encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) PLACING substring(int8send((extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6), 52, 1), 53, 1), 'hex')::uuid,
           a.id, l.company_id, e.posting_date, e.posting_date,
           CASE WHEN e.is_opening_entry THEN 'opening' WHEN l.debit_fc - l.credit_fc >= 0 THEN 'receipt' ELSE 'disbursement' END,
           CASE WHEN e.currency_tc = a.currency THEN l.debit_tc - l.credit_tc ELSE l.debit_fc - l.credit_fc END,
           l.debit_fc - l.credit_fc,
           e.number, 'journal_entry', e.id, e.id
    FROM app.gl_journal_lines l
    JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
    JOIN app.bnk_bank_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.subledger_ref
    WHERE l.tenant_id = t.id AND l.subledger_type = 'BANK'
      AND (e.currency_tc = a.currency OR e.currency_fc = a.currency)
      AND NOT EXISTS (SELECT 1 FROM app.gl_entry_links k WHERE k.tenant_id = e.tenant_id AND k.from_entry_id = e.id AND k.relation = 'reverses')
      AND NOT EXISTS (SELECT 1 FROM app.bnk_bank_transactions x WHERE x.tenant_id = l.tenant_id AND x.journal_entry_id = e.id AND x.bank_account_id = a.id);

    -- Reversals of journals: the mirror of the movement they undo, filed under the reversed journal as the subledger
    -- files them from now on.
    INSERT INTO app.bnk_bank_transactions (tenant_id, id, bank_account_id, company_id, posting_date, value_date, kind, amount_tc, amount_fc,
                                           reference, source_document_type, source_document_id, journal_entry_id, reverses_transaction_id)
    SELECT l.tenant_id,
           encode(set_bit(set_bit(overlay(uuid_send(gen_random_uuid()) PLACING substring(int8send((extract(epoch FROM clock_timestamp()) * 1000)::bigint) FROM 3) FROM 1 FOR 6), 52, 1), 53, 1), 'hex')::uuid,
           a.id, l.company_id, e.posting_date, e.posting_date, 'reversal',
           CASE WHEN e.currency_tc = a.currency THEN l.debit_tc - l.credit_tc ELSE l.debit_fc - l.credit_fc END,
           l.debit_fc - l.credit_fc,
           o.number, 'journal_entry', o.id, e.id,
           (SELECT x.id FROM app.bnk_bank_transactions x
            WHERE x.tenant_id = l.tenant_id AND x.source_document_type = 'journal_entry' AND x.source_document_id = o.id
              AND x.bank_account_id = a.id AND x.kind <> 'reversal' AND x.amount_fc = l.credit_fc - l.debit_fc
            ORDER BY x.id LIMIT 1)
    FROM app.gl_journal_lines l
    JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
    JOIN app.gl_entry_links k ON k.tenant_id = e.tenant_id AND k.from_entry_id = e.id AND k.relation = 'reverses'
    JOIN app.gl_journal_entries o ON o.tenant_id = k.tenant_id AND o.id = k.to_entry_id
    JOIN app.bnk_bank_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.subledger_ref
    WHERE l.tenant_id = t.id AND l.subledger_type = 'BANK'
      AND (e.currency_tc = a.currency OR e.currency_fc = a.currency)
      AND NOT EXISTS (SELECT 1 FROM app.bnk_bank_transactions x WHERE x.tenant_id = l.tenant_id AND x.journal_entry_id = e.id AND x.bank_account_id = a.id);
  END LOOP;
  PERFORM set_config('app.tenant_id', '', true);
END
$$;
