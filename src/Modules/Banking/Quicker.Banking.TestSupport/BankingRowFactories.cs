using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Banking.TestSupport;

/// <summary>Minimal valid rows for every Banking tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class BankingRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        IsolationRegistry.Register("app.bnk_bank_accounts", static async (c, tx, t) => new RowRef("app.bnk_bank_accounts", $"id = '{(await BankAccountAsync(c, tx, t)).Account}'"));
        IsolationRegistry.Register("app.bnk_bank_transactions", static async (c, tx, t) =>
        {
            var (account, company) = await BankAccountAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_bank_transactions (tenant_id, id, bank_account_id, company_id, posting_date, value_date, kind, amount_tc, amount_fc) VALUES (@t, @id, @account, @company, '2026-09-22', '2026-09-22', 'disbursement', -1, -1)", new { t, id, account, company }, tx);
            return new RowRef("app.bnk_bank_transactions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_payments", static async (c, tx, t) => new RowRef("app.bnk_payments", $"id = '{(await PaymentAsync(c, tx, t)).Payment}'"));
        IsolationRegistry.Register("app.bnk_payment_lines", static async (c, tx, t) => new RowRef("app.bnk_payment_lines", $"id = '{(await PaymentAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.bnk_cheque_books", static async (c, tx, t) =>
        {
            var (account, company) = await BankAccountAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_cheque_books (tenant_id, id, company_id, bank_account_id, prefix, first_number, last_number, next_number) VALUES (@t, @id, @company, @account, 'P', 1, 50, 1)", new { t, id, company, account }, tx);
            return new RowRef("app.bnk_cheque_books", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_cheque_deposits", static async (c, tx, t) =>
        {
            var (account, company) = await BankAccountAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_cheque_deposits (tenant_id, id, company_id, number, bank_account_id, deposit_date, currency, total) VALUES (@t, @id, @company, @number, @account, '2026-09-22', 'IQD', 10)", new { t, id, company, account, number = Suffix(id) }, tx);
            return new RowRef("app.bnk_cheque_deposits", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_cheques", static async (c, tx, t) => new RowRef("app.bnk_cheques", $"id = '{await ChequeAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.bnk_cheque_events", static async (c, tx, t) =>
        {
            var cheque = await ChequeAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_cheque_events (tenant_id, id, cheque_id, event, effective_date) VALUES (@t, @id, @cheque, 'issued', '2026-09-22')", new { t, id, cheque }, tx);
            return new RowRef("app.bnk_cheque_events", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_statement_mappings", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_statement_mappings (tenant_id, id, name) VALUES (@t, @id, @name)", new { t, id, name = Suffix(id) }, tx);
            return new RowRef("app.bnk_statement_mappings", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_statements", static async (c, tx, t) => new RowRef("app.bnk_statements", $"id = '{(await StatementLineAsync(c, tx, t)).Statement}'"));
        IsolationRegistry.Register("app.bnk_statement_lines", static async (c, tx, t) => new RowRef("app.bnk_statement_lines", $"id = '{(await StatementLineAsync(c, tx, t)).Line}'"));
        IsolationRegistry.Register("app.bnk_reconciliation_rules", static async (c, tx, t) =>
        {
            var company = await CompanyAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_reconciliation_rules (tenant_id, id, company_id, name_i18n, account_role) VALUES (@t, @id, @company, '{\"en\":\"Probe\"}', 'BankCharges')", new { t, id, company }, tx);
            return new RowRef("app.bnk_reconciliation_rules", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_reconciliation_matches", static async (c, tx, t) =>
        {
            var (_, line, account, company) = await StatementLineAsync(c, tx, t);
            var transaction = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_bank_transactions (tenant_id, id, bank_account_id, company_id, posting_date, value_date, kind, amount_tc, amount_fc) VALUES (@t, @id, @account, @company, '2026-09-22', '2026-09-22', 'disbursement', -1, -1)", new { t, id = transaction, account, company }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_reconciliation_matches (tenant_id, id, statement_line_id, bank_transaction_id, match_kind) VALUES (@t, @id, @line, @transaction, 'manual')", new { t, id, line, transaction }, tx);
            return new RowRef("app.bnk_reconciliation_matches", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_reconciliations", static async (c, tx, t) =>
        {
            var (account, company) = await BankAccountAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_reconciliations (tenant_id, id, company_id, bank_account_id, number, as_of, statement_balance, book_balance) VALUES (@t, @id, @company, @account, @number, '2026-09-30', 0, 0)", new { t, id, company, account, number = Suffix(id) }, tx);
            return new RowRef("app.bnk_reconciliations", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.bnk_transfers", static async (c, tx, t) =>
        {
            var (from, company) = await BankAccountAsync(c, tx, t);
            var gl = await c.QuerySingleAsync<Guid>("SELECT gl_account_id FROM app.bnk_bank_accounts WHERE tenant_id = @t AND id = @from", new { t, from }, tx);
            var to = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_bank_accounts (tenant_id, id, company_id, code, name_i18n, kind, currency, gl_account_id) VALUES (@t, @to, @company, @code, '{\"en\":\"Probe\"}', 'cash', 'IQD', @gl)", new { t, to, company, code = Suffix(to), gl }, tx);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.bnk_transfers (tenant_id, id, company_id, number, transfer_date, from_bank_account_id, to_bank_account_id, amount_sent, currency_sent, amount_received, currency_received, amount_sent_fc, amount_received_fc) VALUES (@t, @id, @company, @number, '2026-09-22', @from, @to, 10, 'IQD', 10, 'IQD', 10, 10)", new { t, id, company, number = Suffix(id), from, to }, tx);
            return new RowRef("app.bnk_transfers", $"id = '{id}'");
        });
    }

    private static async Task<Guid> ChequeAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (payment, _) = await PaymentAsync(c, tx, t);
        var row = await c.QuerySingleAsync<(Guid Company, Guid Partner, Guid Account)>("SELECT company_id, partner_id, bank_account_id FROM app.bnk_payments WHERE tenant_id = @t AND id = @payment", new { t, payment }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_cheques (tenant_id, id, company_id, direction, cheque_number, partner_id, currency, amount, issue_date, due_date, status, payment_id, bank_account_id) VALUES (@t, @id, @company, 'issued', @number, @partner, 'IQD', 10, '2026-09-22', '2026-09-22', 'issued', @payment, @account)",
            new { t, id, company = row.Company, partner = row.Partner, account = row.Account, payment, number = Suffix(id) }, tx);
        return id;
    }

    private static async Task<(Guid Statement, Guid Line, Guid Account, Guid Company)> StatementLineAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (account, company) = await BankAccountAsync(c, tx, t);
        var statement = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_statements (tenant_id, id, company_id, bank_account_id, format, file_name) VALUES (@t, @id, @company, @account, 'csv', 'probe.csv')", new { t, id = statement, company, account }, tx);
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_statement_lines (tenant_id, id, statement_id, bank_account_id, line_no, booking_date, amount, import_key) VALUES (@t, @id, @statement, @account, 1, '2026-09-22', -1, @key)", new { t, id = line, statement, account, key = line.ToString("N") }, tx);
        return (statement, line, account, company);
    }

    private static string Suffix(Guid id) => "B" + id.ToString("N")[^8..].ToUpperInvariant();

    private static async Task<(Guid Account, Guid Company)> BankAccountAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var company = await CompanyAsync(c, tx, t);
        var chart = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_charts (tenant_id, id, code, name_i18n) VALUES (@t, @chart, @code, '{\"en\":\"Probe\"}')", new { t, chart, code = Suffix(chart) }, tx);
        var gl = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.gl_accounts (tenant_id, id, chart_id, code, name_i18n, type) VALUES (@t, @gl, @chart, @code, '{\"en\":\"Probe\"}', 'asset')", new { t, gl, chart, code = gl.ToString("N")[^6..] }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_bank_accounts (tenant_id, id, company_id, code, name_i18n, kind, currency, gl_account_id) VALUES (@t, @id, @company, @code, '{\"en\":\"Probe\"}', 'bank', 'IQD', @gl)", new { t, id, company, code = Suffix(id), gl }, tx);
        return (id, company);
    }

    private static async Task<(Guid Payment, Guid Line)> PaymentAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var (account, company) = await BankAccountAsync(c, tx, t);
        var partner = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ptr_partners (tenant_id, id, code, legal_name_i18n, is_supplier) VALUES (@t, @partner, @code, '{\"en\":\"Probe\"}', true)", new { t, partner, code = Suffix(partner) }, tx);
        var item = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.ap_open_items (tenant_id, id, company_id, partner_id, kind, document_type, document_id, document_number, posting_date, document_date, due_date, currency, original_tc, original_fc, remaining_tc, remaining_fc) VALUES (@t, @item, @company, @partner, 'invoice', 'purchase_invoice', @doc, @number, '2026-09-22', '2026-09-22', '2026-10-22', 'IQD', 10, 10, 10, 10)", new { t, item, company, partner, doc = Guid.CreateVersion7(), number = Suffix(item) }, tx);
        var id = Guid.CreateVersion7();
        var line = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.bnk_payments (tenant_id, id, company_id, number, kind, partner_id, bank_account_id, payment_date, method, currency, bank_currency) VALUES (@t, @id, @company, @number, 'supplier_payment', @partner, @account, '2026-09-22', 'transfer', 'IQD', 'IQD')", new { t, id, company, number = Suffix(id), partner, account }, tx);
        await c.ExecuteAsync("INSERT INTO app.bnk_payment_lines (tenant_id, id, payment_id, line_no, open_item_id, amount_tc) VALUES (@t, @line, @id, 1, @item, 10)", new { t, line, id, item }, tx);
        return (id, line);
    }

    private static async Task<Guid> CompanyAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var fiscal = Guid.CreateVersion7();
        var business = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.org_fiscal_calendars (tenant_id, id, code, name_i18n, start_month, periods_per_year) VALUES (@t, @fiscal, @code, '{}', 1, 12)", new { t, fiscal, code = Suffix(fiscal).ToLowerInvariant() }, tx);
        await c.ExecuteAsync("INSERT INTO app.org_business_calendars (tenant_id, id, code, name_i18n, working_days) VALUES (@t, @business, @code, '{}', ARRAY[0,1,2,3,4])", new { t, business, code = Suffix(business).ToLowerInvariant() }, tx);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("""
            INSERT INTO app.org_companies (tenant_id, id, code, legal_name_i18n, country, functional_currency, fiscal_calendar_id, business_calendar_id, time_zone)
            VALUES (@t, @id, @code, '{"en":"Probe"}', 'IQ', 'IQD', @fiscal, @business, 'Asia/Baghdad')
            """, new { t, id, code = Suffix(id), fiscal, business }, tx);
        return id;
    }
}
