-- M5 slice 5.3c: the return computed from the tax ledger reconciles to the tax accounts of the general ledger by
-- summing gl_journal_lines by tax code and account role for a period (Accounting.Contracts.ILedgerReader). The
-- journal is a large partitioned table with no index on tax_code_id yet; declared on the partitioned parent, it
-- covers every existing and future partition (PostgreSQL propagates it automatically).

CREATE INDEX gl_journal_lines_tax_idx ON app.gl_journal_lines (tenant_id, company_id, tax_code_id, posting_date) WHERE tax_code_id IS NOT NULL;
