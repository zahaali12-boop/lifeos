using Dapper;
using Quicker.Testing;

namespace Quicker.Reporting.TestSupport;

/// <summary>Minimal valid rows for every tenant table of the reporting schema (the projected facts and their watermarks), so the isolation suite (hard scenario 18) covers them.</summary>
public static class ReportingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("reporting.projections", static async (c, tx, t) =>
        {
            await c.ExecuteAsync("INSERT INTO reporting.projections (tenant_id, fact) VALUES (@t, 'journal_lines')", new { t }, tx);
            return new RowRef("reporting.projections", "fact = 'journal_lines'");
        });
        IsolationRegistry.Register("reporting.fact_journal_lines", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO reporting.fact_journal_lines (tenant_id, line_id, entry_id, line_no, company_id, posting_date, document_date, entry_number, account_id, account_role, currency_tc, currency_fc, posted_at)
                VALUES (@t, @id, @entry, 1, @company, '2026-01-15', '2026-01-15', @number, @account, 'Revenue', 'IQD', 'IQD', now())
                """, new { t, id, entry = Guid.CreateVersion7(), company = Guid.CreateVersion7(), number = Suffix(id), account = Guid.CreateVersion7() }, tx);
            return new RowRef("reporting.fact_journal_lines", $"line_id = '{id}'");
        });
        IsolationRegistry.Register("reporting.fact_sales_lines", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO reporting.fact_sales_lines (tenant_id, line_id, invoice_id, line_no, company_id, invoice_number, kind, status, document_date, posting_date, partner_id, currency, line_kind, posted_at)
                VALUES (@t, @id, @invoice, 1, @company, @number, 'invoice', 'posted', '2026-01-15', '2026-01-15', @partner, 'IQD', 'item', now())
                """, new { t, id, invoice = Guid.CreateVersion7(), company = Guid.CreateVersion7(), number = Suffix(id), partner = Guid.CreateVersion7() }, tx);
            return new RowRef("reporting.fact_sales_lines", $"line_id = '{id}'");
        });
        IsolationRegistry.Register("reporting.fact_purchase_lines", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO reporting.fact_purchase_lines (tenant_id, line_id, invoice_id, line_no, company_id, invoice_number, kind, status, document_date, posting_date, partner_id, currency, line_kind, posted_at)
                VALUES (@t, @id, @invoice, 1, @company, @number, 'invoice', 'posted', '2026-01-15', '2026-01-15', @partner, 'IQD', 'item', now())
                """, new { t, id, invoice = Guid.CreateVersion7(), company = Guid.CreateVersion7(), number = Suffix(id), partner = Guid.CreateVersion7() }, tx);
            return new RowRef("reporting.fact_purchase_lines", $"line_id = '{id}'");
        });
        IsolationRegistry.Register("reporting.fact_stock_movements", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO reporting.fact_stock_movements (tenant_id, entry_id, sequence, posting_id, company_id, item_id, warehouse_id, entry_type, quantity, posting_date, source_document_type, source_document_id, posted_at)
                VALUES (@t, @id, 1, @posting, @company, @item, @warehouse, 'purchase_receipt', 1, '2026-01-15', 'probe', @doc, now())
                """, new { t, id, posting = Guid.CreateVersion7(), company = Guid.CreateVersion7(), item = Guid.CreateVersion7(), warehouse = Guid.CreateVersion7(), doc = Guid.CreateVersion7() }, tx);
            return new RowRef("reporting.fact_stock_movements", $"entry_id = '{id}'");
        });
        IsolationRegistry.Register("reporting.fact_stock_values", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO reporting.fact_stock_values (tenant_id, value_id, company_id, item_id, warehouse_id, posting_date, valuation_date, value_type, currency, account_role, offset_role, source_document_type, source_document_id, created_at)
                VALUES (@t, @id, @company, @item, @warehouse, '2026-01-15', '2026-01-15', 'receipt', 'IQD', 'Inventory', 'GRNI', 'probe', @doc, now())
                """, new { t, id, company = Guid.CreateVersion7(), item = Guid.CreateVersion7(), warehouse = Guid.CreateVersion7(), doc = Guid.CreateVersion7() }, tx);
            return new RowRef("reporting.fact_stock_values", $"value_id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();
}
