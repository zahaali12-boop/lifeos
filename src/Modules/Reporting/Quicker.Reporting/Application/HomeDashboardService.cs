using System.Text.Json;
using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Contracts;

namespace Quicker.Reporting.Application;

/// <summary>
/// The home page's read model (founder programme item 1): read-only SQL over the modules' own tables through the
/// tenant's connection, as the invariant harness reads them. Each section is shown only to a member whose grant lets
/// them read that module in the company; the amounts are the modules' own (posted invoices at their booked rate, open
/// items' remaining functional amounts, bank transactions), so every number opens a list that adds up to it.
/// </summary>
public sealed class HomeDashboardService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, ICompanyDirectory companies, IClock clock)
{
    private const int SeriesDays = 30;
    private const int DueSoonDays = 7;

    private static readonly string[] OpenItemStatuses = ["open", "partially_settled"];

    public async Task<Result<HomeDashboard>> ReadAsync(Guid companyId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var actor = principal.Required;
        bool may(string permission) => actor.ScopesFor(permission)?.AllowsCompany(companyId) == true;
        var day = asOf ?? clock.TodayIn(company.TimeZone);

        return new HomeDashboard(
            companyId,
            company.FunctionalCurrency.Code,
            day,
            may("sales.invoice.read") ? await SalesAsync(companyId, day, cancellationToken) : null,
            may("receivables.open_item.read") ? await ReceivablesAsync(companyId, day, cancellationToken) : null,
            may("payables.open_item.read") ? await PayablesAsync(companyId, day, cancellationToken) : null,
            may("banking.bank_account.read") ? await CashAsync(companyId, day, cancellationToken) : null,
            await PipelineAsync(companyId, may, cancellationToken));
    }

    private async Task<HomeSales> SalesAsync(Guid companyId, DateOnly day, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var monthStart = new DateOnly(day.Year, day.Month, 1);
        var previousMonthStart = monthStart.AddMonths(-1);
        var previousSameDay = previousMonthStart.AddDays(Math.Min(day.Day, DateTime.DaysInMonth(previousMonthStart.Year, previousMonthStart.Month)) - 1);
        var seriesStart = day.AddDays(-(SeriesDays - 1));
        var parameters = new { company = companyId, day, monthStart, previousMonthStart, previousSameDay, seriesStart, from = previousMonthStart < seriesStart ? previousMonthStart : seriesStart };

        // Net sales in functional currency: the invoice's net at the rate it was booked with; credit notes count against.
        var totals = await uow.Connection.QuerySingleAsync<(decimal Today, decimal MonthToDate, decimal PreviousMonthToDate, int InvoicesToday)>(new CommandDefinition("""
            SELECT coalesce(sum(signed) FILTER (WHERE posting_date = @day), 0),
                   coalesce(sum(signed) FILTER (WHERE posting_date >= @monthStart AND posting_date <= @day), 0),
                   coalesce(sum(signed) FILTER (WHERE posting_date >= @previousMonthStart AND posting_date <= @previousSameDay), 0),
                   count(*) FILTER (WHERE posting_date = @day AND kind = 'invoice')::int
            FROM (SELECT posting_date, kind, (CASE WHEN kind = 'credit_note' THEN -1 ELSE 1 END) * total_net * exchange_rate AS signed
                  FROM app.sls_invoices
                  WHERE company_id = @company AND status = 'posted' AND posting_date >= @from AND posting_date <= @day) s
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
        var drafts = await uow.Connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*)::int FROM app.sls_invoices WHERE company_id = @company AND status = 'draft'", parameters, uow.Transaction, cancellationToken: cancellationToken));
        var days = (await uow.Connection.QueryAsync<(DateTime Day, decimal Amount)>(new CommandDefinition("""
            SELECT posting_date, sum((CASE WHEN kind = 'credit_note' THEN -1 ELSE 1 END) * total_net * exchange_rate)
            FROM app.sls_invoices
            WHERE company_id = @company AND status = 'posted' AND posting_date >= @seriesStart AND posting_date <= @day
            GROUP BY posting_date
            """, parameters, uow.Transaction, cancellationToken: cancellationToken))).ToDictionary(static r => DateOnly.FromDateTime(r.Day), static r => r.Amount);
        var series = Enumerable.Range(0, SeriesDays).Select(i => seriesStart.AddDays(i)).Select(d => new HomeDay(d, Round(days.GetValueOrDefault(d)))).ToList();
        return new HomeSales(Round(totals.Today), Round(totals.MonthToDate), Round(totals.PreviousMonthToDate), totals.InvoicesToday, drafts, series);
    }

    private async Task<HomeReceivables> ReceivablesAsync(Guid companyId, DateOnly day, CancellationToken cancellationToken)
    {
        var r = await OpenItemsAsync("app.ar_open_items", companyId, day, cancellationToken);
        return new HomeReceivables(r.Overdue, r.OverduePartners, r.DueSoon, r.Total, r.OpenItems);
    }

    private async Task<HomePayables> PayablesAsync(Guid companyId, DateOnly day, CancellationToken cancellationToken)
    {
        var r = await OpenItemsAsync("app.ap_open_items", companyId, day, cancellationToken);
        return new HomePayables(r.Overdue, r.OverduePartners, r.DueSoon, r.Total, r.OpenItems);
    }

    /// <summary>Past due and due soon count the items still owed (positive remaining); the total nets credits and advances against them.</summary>
    private async Task<(decimal Overdue, int OverduePartners, decimal DueSoon, decimal Total, int OpenItems)> OpenItemsAsync(string table, Guid companyId, DateOnly day, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleAsync<(decimal Overdue, int OverduePartners, decimal DueSoon, decimal Total, int OpenItems)>(new CommandDefinition($"""
            SELECT coalesce(sum(remaining_fc) FILTER (WHERE remaining_fc > 0 AND due_date < @day), 0),
                   count(DISTINCT partner_id) FILTER (WHERE remaining_fc > 0 AND due_date < @day)::int,
                   coalesce(sum(remaining_fc) FILTER (WHERE remaining_fc > 0 AND due_date >= @day AND due_date < @soon), 0),
                   coalesce(sum(remaining_fc), 0),
                   count(*)::int
            FROM {table}
            WHERE company_id = @company AND status = ANY(@statuses)
            """, new { company = companyId, day, soon = day.AddDays(DueSoonDays), statuses = OpenItemStatuses }, uow.Transaction, cancellationToken: cancellationToken));
        return (Round(row.Overdue), row.OverduePartners, Round(row.DueSoon), Round(row.Total), row.OpenItems);
    }

    private async Task<HomeCash> CashAsync(Guid companyId, DateOnly day, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var monthStart = new DateOnly(day.Year, day.Month, 1);
        var parameters = new { company = companyId, day, monthStart };
        var accounts = (await uow.Connection.QueryAsync<(Guid Id, string Code, string Name, string Kind, string Currency, decimal Tc, decimal Fc)>(new CommandDefinition("""
            SELECT a.id, a.code, a.name_i18n::text, a.kind, a.currency, coalesce(sum(t.amount_tc), 0), coalesce(sum(t.amount_fc), 0)
            FROM app.bnk_bank_accounts a
            LEFT JOIN app.bnk_bank_transactions t ON t.tenant_id = a.tenant_id AND t.bank_account_id = a.id AND t.posting_date <= @day
            WHERE a.company_id = @company AND a.is_active
            GROUP BY a.id, a.code, a.name_i18n, a.kind, a.currency
            ORDER BY CASE a.kind WHEN 'cash' THEN 0 WHEN 'petty_cash' THEN 1 ELSE 2 END, a.code
            """, parameters, uow.Transaction, cancellationToken: cancellationToken)))
            .Select(a => new HomeCashAccount(a.Id, a.Code, ParseName(a.Name), a.Kind, a.Currency, a.Tc, a.Fc))
            .ToList();
        var flows = await uow.Connection.QuerySingleAsync<(decimal Received, decimal Paid)>(new CommandDefinition("""
            SELECT coalesce(sum(t.amount_fc) FILTER (WHERE t.kind = 'receipt'), 0),
                   coalesce(-sum(t.amount_fc) FILTER (WHERE t.kind = 'disbursement'), 0)
            FROM app.bnk_bank_transactions t
            JOIN app.bnk_bank_accounts a ON a.tenant_id = t.tenant_id AND a.id = t.bank_account_id
            WHERE t.company_id = @company AND a.is_active AND t.posting_date >= @monthStart AND t.posting_date <= @day
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
        return new HomeCash(Round(accounts.Sum(static a => a.BalanceFc)), Round(flows.Received), Round(flows.Paid), accounts);
    }

    private async Task<HomePipeline?> PipelineAsync(Guid companyId, Func<string, bool> may, CancellationToken cancellationToken)
    {
        var quotes = may("sales.quote.read");
        var orders = may("sales.order.read");
        var shipments = may("sales.shipment.read");
        var returns = may("sales.return.read");
        if (!quotes && !orders && !shipments && !returns)
        {
            return null;
        }

        var uow = unitOfWork.Current;
        var parameters = new { company = companyId };
        async Task<int> CountAsync(string sql) => await uow.Connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, parameters, uow.Transaction, cancellationToken: cancellationToken));
        return new HomePipeline(
            quotes ? await CountAsync("SELECT count(*)::int FROM app.sls_quotations WHERE company_id = @company AND status IN ('draft', 'sent')") : null,
            orders ? await CountAsync("SELECT count(*)::int FROM app.sls_orders WHERE company_id = @company AND status IN ('confirmed', 'partially_shipped')") : null,
            shipments ? await CountAsync("SELECT count(*)::int FROM app.sls_shipments WHERE company_id = @company AND status = 'draft'") : null,
            returns ? await CountAsync("SELECT count(*)::int FROM app.sls_returns WHERE company_id = @company AND status IN ('requested', 'authorised', 'received', 'inspected')") : null);
    }

    private static decimal Round(decimal value) => RoundingPolicy.Default.Round(value, 2);

    private static IReadOnlyDictionary<string, string> ParseName(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
