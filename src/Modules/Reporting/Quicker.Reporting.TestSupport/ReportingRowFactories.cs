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
        IsolationRegistry.Register("app.rpt_reports", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO app.rpt_reports (tenant_id, id, model, name_i18n, owner_membership_id, definition)
                VALUES (@t, @id, 'sales_lines', '{"en":"Probe"}'::jsonb, @owner, '{"model":"sales_lines"}'::jsonb)
                """, new { t, id, owner = Guid.CreateVersion7() }, tx);
            return new RowRef("app.rpt_reports", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.rpt_report_shares", static async (c, tx, t) =>
        {
            var report = Guid.CreateVersion7();
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO app.rpt_reports (tenant_id, id, model, name_i18n, owner_membership_id, definition)
                VALUES (@t, @report, 'sales_lines', '{"en":"Probe"}'::jsonb, @owner, '{"model":"sales_lines"}'::jsonb);
                INSERT INTO app.rpt_report_shares (tenant_id, id, report_id, share_with) VALUES (@t, @id, @report, 'tenant')
                """, new { t, report, id, owner = Guid.CreateVersion7() }, tx);
            return new RowRef("app.rpt_report_shares", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.rpt_financial_layouts", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO app.rpt_financial_layouts (tenant_id, id, code, kind, name_i18n)
                VALUES (@t, @id, @code, 'management', '{"en":"Probe"}'::jsonb)
                """, new { t, id, code = Suffix(id) }, tx);
            return new RowRef("app.rpt_financial_layouts", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.rpt_layout_rows", static async (c, tx, t) =>
        {
            var layout = Guid.CreateVersion7();
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO app.rpt_financial_layouts (tenant_id, id, code, kind, name_i18n) VALUES (@t, @layout, @code, 'management', '{"en":"Probe"}'::jsonb);
                INSERT INTO app.rpt_layout_rows (tenant_id, id, layout_id, sort_order, code, row_type, label_i18n) VALUES (@t, @id, @layout, 0, 'R1', 'header', '{"en":"Probe"}'::jsonb)
                """, new { t, layout, id, code = Suffix(layout) }, tx);
            return new RowRef("app.rpt_layout_rows", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.rpt_layout_columns", static async (c, tx, t) =>
        {
            var layout = Guid.CreateVersion7();
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("""
                INSERT INTO app.rpt_financial_layouts (tenant_id, id, code, kind, name_i18n) VALUES (@t, @layout, @code, 'management', '{"en":"Probe"}'::jsonb);
                INSERT INTO app.rpt_layout_columns (tenant_id, id, layout_id, sort_order, code, column_type, label_i18n) VALUES (@t, @id, @layout, 0, 'C1', 'period', '{"en":"Probe"}'::jsonb)
                """, new { t, layout, id, code = Suffix(layout) }, tx);
            return new RowRef("app.rpt_layout_columns", $"id = '{id}'");
        });
    }

    private static string Suffix(Guid id) => "P" + id.ToString("N")[^8..].ToUpperInvariant();
}
