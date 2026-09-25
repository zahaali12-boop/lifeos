using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Web;
using Quicker.Web.Exports;

namespace Quicker.Accounting.Application;

/// <summary>
/// General ledger inquiry (roadmap 2.5): the trial balance at any date with a movement window, comparatives, dimension
/// filters and grouping; the account ledger with a running balance and the source document of every line; balances
/// by dimension; CSV and XLSX exports. Every figure carries the parameters of the ledger it drills to. Amounts are
/// read from the journal lines (functional or reporting currency), so the figures agree with the derived balances
/// only when those are intact; the invariant harness checks that.
/// </summary>
public sealed class InquiryService(AccountingDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, IDimensionDirectory dimensions, IAuditSink audit, IClock clock, IIssuedNumbers issuedNumbers)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly IReadOnlyDictionary<string, Guid> NoFilters = new Dictionary<string, Guid>(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, string> NoText = new Dictionary<string, string>(StringComparer.Ordinal);

    private sealed record Scope(CompanyInfo Company, string Basis, string Currency, DateOnly AsOf, DateOnly? From, IReadOnlyDictionary<string, Guid> Filters, string? GroupBy, bool IncludeClosing);

    private sealed record AggregateRow(Guid Account, string? Value, decimal Opening, decimal Debit, decimal Credit);

    private sealed record DimensionValueRow(Guid Id, string Code, string Name);

    private sealed record LedgerCursor(string Date, string Number, int Line);

    private sealed record LedgerRow(
        Guid LineId, Guid EntryId, string Number, DateOnly PostingDate, DateOnly DocumentDate, string SourceModule, string SourceDocumentType, Guid SourceDocumentId, string? SourceDocumentNumber,
        string Description, string CurrencyTc, decimal DebitTc, decimal CreditTc, decimal Debit, decimal Credit, string? Dimensions, Guid? BranchId, Guid? PartnerId, string? SubledgerType, Guid? SubledgerRef,
        bool IsReversal, bool IsRounding, int LineNo);

    // ------------------------------------------------------------------ trial balance

    public async Task<Result<TrialBalance>> TrialBalanceAsync(Guid companyId, InquiryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var scoped = await ScopeAsync(companyId, query, cancellationToken);
        if (scoped.IsFailure)
        {
            return scoped.Error!;
        }

        var scope = scoped.Value;
        var current = await AggregateAsync(scope, scope.AsOf, scope.From, cancellationToken);
        Dictionary<(Guid Account, string? Value), TrialBalanceAmounts>? compare = null;
        DateOnly? compareFrom = null;
        if (query.CompareAsOf is { } compareAsOf)
        {
            compareFrom = scope.From is { } from ? compareAsOf.AddDays(-(scope.AsOf.DayNumber - from.DayNumber)) : null;
            compare = await AggregateAsync(scope, compareAsOf, compareFrom, cancellationToken);
        }

        var keys = compare is null ? current.Keys.ToList() : current.Keys.Union(compare.Keys).ToList();
        var accountIds = keys.Select(static k => k.Account).Distinct().ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        var values = await DimensionValuesAsync(keys.Select(static k => k.Value), cancellationToken);

        var rows = new List<TrialBalanceRow>(keys.Count);
        foreach (var key in keys)
        {
            var account = accounts[key.Account];
            var amounts = current.GetValueOrDefault(key) ?? TrialBalanceAmounts.Zero;
            var value = key.Value is { } v && Guid.TryParse(v, out var valueId) ? values.GetValueOrDefault(valueId) : null;
            var drillFilters = new Dictionary<string, Guid>(scope.Filters, StringComparer.Ordinal);
            if (scope.GroupBy is { } groupBy && value is not null)
            {
                drillFilters[groupBy] = value.Id;
            }

            rows.Add(new TrialBalanceRow(account.Id, account.Code, account.Name.Values, account.Type, account.IsControl,
                value?.Id, value?.Code, value is null ? null : Text(value.Name),
                amounts.Opening, amounts.Debit, amounts.Credit, amounts.Closing,
                compare?.GetValueOrDefault(key) ?? (compare is null ? null : TrialBalanceAmounts.Zero),
                new LedgerDrill(account.Id, scope.From, scope.AsOf, drillFilters)));
        }

        rows.Sort(static (a, b) =>
        {
            var byCode = string.CompareOrdinal(a.AccountCode, b.AccountCode);
            return byCode != 0 ? byCode : string.CompareOrdinal(a.DimensionValueCode ?? string.Empty, b.DimensionValueCode ?? string.Empty);
        });

        var totals = rows.Aggregate(TrialBalanceAmounts.Zero, static (sum, r) => sum.Add(new TrialBalanceAmounts(r.Opening, r.Debit, r.Credit, r.Closing)));
        var compareTotals = compare is null ? null : rows.Aggregate(TrialBalanceAmounts.Zero, static (sum, r) => sum.Add(r.Compare ?? TrialBalanceAmounts.Zero));
        // Books that balance show a zero opening total and equal movements, whatever the window; a dimension filter is a slice and need not.
        var balanced = totals.Opening == 0m && totals.Debit == totals.Credit;
        return new TrialBalance(scope.Company.Id.Value, scope.Currency, scope.Basis, scope.AsOf, scope.From, query.CompareAsOf, compareFrom, scope.GroupBy, scope.Filters, scope.IncludeClosing, rows, totals, compareTotals, balanced);
    }

    // ------------------------------------------------------------------ account ledger

    public async Task<Result<AccountLedger>> LedgerAsync(Guid companyId, Guid? accountId, string? accountCode, InquiryQuery query, PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        var scoped = await ScopeAsync(companyId, query with { GroupBy = null }, cancellationToken);
        if (scoped.IsFailure)
        {
            return scoped.Error!;
        }

        var scope = scoped.Value;
        var chartId = scope.Company.ChartId;
        var account = accountId is { } id
            ? await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken)
            : await db.Accounts.AsNoTracking().SingleOrDefaultAsync(a => a.ChartId == chartId && a.Code == (accountCode ?? string.Empty).Trim(), cancellationToken);
        if (account is null)
        {
            return Error.NotFound("account", (object?)accountId ?? accountCode ?? string.Empty);
        }

        LedgerCursor? cursor = null;
        if (!string.IsNullOrEmpty(page.Cursor))
        {
            var decoded = Cursor.Decode<LedgerCursor>(page.Cursor);
            if (decoded.IsFailure)
            {
                return decoded.Error!;
            }

            cursor = decoded.Value;
        }

        var uow = unitOfWork.Current;
        var (where, parameters) = Where(scope, scope.AsOf);
        parameters.Add("account", account.Id);
        parameters.Add("from", (scope.From ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var sfx = scope.Basis;
        var summary = await uow.Connection.QuerySingleAsync<(decimal Opening, decimal Debit, decimal Credit)>(new CommandDefinition($"""
            SELECT coalesce(sum(CASE WHEN l.posting_date < @from::date THEN l.debit_{sfx} - l.credit_{sfx} ELSE 0 END), 0) AS opening,
                   coalesce(sum(CASE WHEN l.posting_date >= @from::date THEN l.debit_{sfx} ELSE 0 END), 0) AS debit,
                   coalesce(sum(CASE WHEN l.posting_date >= @from::date THEN l.credit_{sfx} ELSE 0 END), 0) AS credit
            FROM app.gl_journal_lines l
            JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
            LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = l.tenant_id AND ds.id = l.dimension_set_id
            WHERE {where} AND l.account_id = @account
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));

        var balanceBefore = summary.Opening;
        var keyset = string.Empty;
        if (cursor is not null)
        {
            parameters.Add("cd", cursor.Date);
            parameters.Add("cn", cursor.Number);
            parameters.Add("cl", cursor.Line);
            keyset = " AND (l.posting_date, e.number, l.line_no) > (@cd::date, @cn, @cl)";
            var before = await uow.Connection.QuerySingleAsync<(decimal Debit, decimal Credit)>(new CommandDefinition($"""
                SELECT coalesce(sum(l.debit_{sfx}), 0) AS debit, coalesce(sum(l.credit_{sfx}), 0) AS credit
                FROM app.gl_journal_lines l
                JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
                LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = l.tenant_id AND ds.id = l.dimension_set_id
                WHERE {where} AND l.account_id = @account AND l.posting_date >= @from::date AND (l.posting_date, e.number, l.line_no) <= (@cd::date, @cn, @cl)
                """, parameters, uow.Transaction, cancellationToken: cancellationToken));
            balanceBefore += before.Debit - before.Credit;
        }

        var size = page.Size();
        parameters.Add("take", size + 1);
        var rows = (await uow.Connection.QueryAsync<LedgerRow>(new CommandDefinition($"""
            SELECT l.id AS line_id, e.id AS entry_id, e.number, l.posting_date, e.document_date, e.source_module, e.source_document_type, e.source_document_id, e.source_document_number,
                   (CASE WHEN l.description_i18n = jsonb_build_object() THEN e.description_i18n ELSE l.description_i18n END)::text AS description, l.currency_tc, l.debit_tc, l.credit_tc, l.debit_{sfx} AS debit, l.credit_{sfx} AS credit, ds.values::text AS dimensions,
                   l.branch_id, l.partner_id, l.subledger_type, l.subledger_ref, e.is_reversal, l.is_rounding, l.line_no
            FROM app.gl_journal_lines l
            JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
            LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = l.tenant_id AND ds.id = l.dimension_set_id
            WHERE {where} AND l.account_id = @account AND l.posting_date >= @from::date{keyset}
            ORDER BY l.posting_date, e.number, l.line_no
            LIMIT @take
            """, parameters, uow.Transaction, cancellationToken: cancellationToken))).ToList();

        var hasMore = rows.Count > size;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        // Stock postings of the costing engine carry no source number; the one numbering issued the document is shown.
        var sourceNumbers = await issuedNumbers.NumbersOfAsync(rows.Where(static r => r.SourceDocumentNumber is null).Select(static r => r.SourceDocumentId).Distinct().ToList(), cancellationToken);
        var items = new List<LedgerItem>(rows.Count);
        var balance = balanceBefore;
        foreach (var row in rows)
        {
            balance += row.Debit - row.Credit;
            items.Add(new LedgerItem(row.LineId, row.EntryId, row.Number, row.PostingDate, row.DocumentDate, row.SourceModule, row.SourceDocumentType, row.SourceDocumentId, row.SourceDocumentNumber ?? sourceNumbers.GetValueOrDefault(row.SourceDocumentId),
                SourceDocumentLinks.For(row.SourceDocumentType, row.SourceDocumentId, row.EntryId), Text(row.Description), row.CurrencyTc, row.DebitTc, row.CreditTc, row.Debit, row.Credit, balance,
                Dimensions(row.Dimensions), row.BranchId, row.PartnerId, row.SubledgerType, row.SubledgerRef, row.IsReversal, row.IsRounding));
        }

        var last = rows.Count > 0 ? rows[^1] : null;
        var next = hasMore && last is not null ? Cursor.Encode(new LedgerCursor(last.PostingDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), last.Number, last.LineNo)) : null;
        return new AccountLedger(scope.Company.Id.Value, account.Id, account.Code, account.Name.Values, scope.Currency, scope.Basis, scope.From, scope.AsOf, scope.Filters,
            summary.Opening, summary.Debit, summary.Credit, summary.Opening + summary.Debit - summary.Credit, items, next);
    }

    // ------------------------------------------------------------------ balances by dimension

    public async Task<Result<DimensionBalances>> DimensionBalancesAsync(Guid companyId, string dimension, string? accountType, Guid? accountId, InquiryQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var scoped = await ScopeAsync(companyId, query with { GroupBy = dimension }, cancellationToken);
        if (scoped.IsFailure)
        {
            return scoped.Error!;
        }

        var scope = scoped.Value;
        var type = string.IsNullOrWhiteSpace(accountType) ? null : accountType.Trim().ToLowerInvariant();
        if (type is not null && !AccountTypes.All.Contains(type, StringComparer.Ordinal))
        {
            return Error.Validation("inquiry.account_type_invalid", $"Account types are {string.Join(", ", AccountTypes.All)}.").WithWhy(("accountType", accountType));
        }

        var uow = unitOfWork.Current;
        var (where, parameters) = Where(scope, scope.AsOf);
        parameters.Add("from", (scope.From ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        parameters.Add("groupBy", scope.GroupBy);
        parameters.Add("type", type);
        parameters.Add("account", accountId);
        var sfx = scope.Basis;
        var rows = (await uow.Connection.QueryAsync<AggregateRow>(new CommandDefinition($"""
            SELECT '00000000-0000-0000-0000-000000000000'::uuid AS account, ds.values->>@groupBy AS value,
                   sum(CASE WHEN l.posting_date < @from::date THEN l.debit_{sfx} - l.credit_{sfx} ELSE 0 END) AS opening,
                   sum(CASE WHEN l.posting_date >= @from::date THEN l.debit_{sfx} ELSE 0 END) AS debit,
                   sum(CASE WHEN l.posting_date >= @from::date THEN l.credit_{sfx} ELSE 0 END) AS credit
            FROM app.gl_journal_lines l
            JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
            JOIN app.gl_accounts a ON a.tenant_id = l.tenant_id AND a.id = l.account_id
            LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = l.tenant_id AND ds.id = l.dimension_set_id
            WHERE {where} AND (@type::text IS NULL OR a.type = @type) AND (@account::uuid IS NULL OR l.account_id = @account)
            GROUP BY ds.values->>@groupBy
            """, parameters, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var values = await DimensionValuesAsync(rows.Select(static r => r.Value), cancellationToken);
        var result = rows.Select(r =>
        {
            var value = r.Value is { } v && Guid.TryParse(v, out var valueId) ? values.GetValueOrDefault(valueId) : null;
            return new DimensionBalanceRow(value?.Id, value?.Code, value is null ? null : Text(value.Name), r.Opening, r.Debit, r.Credit, r.Opening + r.Debit - r.Credit);
        }).OrderBy(static r => r.ValueCode is null ? 1 : 0).ThenBy(static r => r.ValueCode, StringComparer.Ordinal).ToList();
        var totals = result.Aggregate(TrialBalanceAmounts.Zero, static (sum, r) => sum.Add(new TrialBalanceAmounts(r.Opening, r.Debit, r.Credit, r.Closing)));
        return new DimensionBalances(scope.Company.Id.Value, scope.GroupBy!, scope.Currency, scope.Basis, scope.AsOf, scope.From, type, accountId, result, totals);
    }

    // ------------------------------------------------------------------ exports

    public async Task<ExportFile> ExportTrialBalanceAsync(TrialBalance report, string format, bool rightToLeft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        var compare = report.CompareAsOf is not null;
        var columns = new List<ExportColumn>
        {
            new("account_code"), new("account_name_en"), new("account_name_ar"), new("account_type"),
        };
        if (report.GroupBy is not null)
        {
            columns.Add(new ExportColumn(report.GroupBy.ToLowerInvariant() + "_code"));
            columns.Add(new ExportColumn(report.GroupBy.ToLowerInvariant() + "_name"));
        }

        columns.AddRange([new ExportColumn("opening", ExportCellType.Number), new ExportColumn("debit", ExportCellType.Number), new ExportColumn("credit", ExportCellType.Number), new ExportColumn("closing", ExportCellType.Number)]);
        if (compare)
        {
            columns.AddRange([new ExportColumn("compare_opening", ExportCellType.Number), new ExportColumn("compare_debit", ExportCellType.Number), new ExportColumn("compare_credit", ExportCellType.Number), new ExportColumn("compare_closing", ExportCellType.Number)]);
        }

        var rows = report.Rows.Select(r =>
        {
            var cells = new List<object?> { r.AccountCode, r.AccountName.GetValueOrDefault("en"), r.AccountName.GetValueOrDefault("ar"), r.AccountType };
            if (report.GroupBy is not null)
            {
                cells.Add(r.DimensionValueCode);
                cells.Add(r.DimensionValueName is null ? null : r.DimensionValueName.GetValueOrDefault(rightToLeft ? "ar" : "en") ?? r.DimensionValueName.GetValueOrDefault("en"));
            }

            cells.AddRange([r.Opening, r.Debit, r.Credit, r.Closing]);
            if (compare)
            {
                var c = r.Compare ?? TrialBalanceAmounts.Zero;
                cells.AddRange([c.Opening, c.Debit, c.Credit, c.Closing]);
            }

            return (IReadOnlyList<object?>)cells;
        }).ToList();
        var totals = new List<object?> { "TOTAL", null, null, null };
        if (report.GroupBy is not null)
        {
            totals.AddRange([null, null]);
        }

        totals.AddRange([report.Totals.Opening, report.Totals.Debit, report.Totals.Credit, report.Totals.Closing]);
        if (compare)
        {
            var c = report.CompareTotals ?? TrialBalanceAmounts.Zero;
            totals.AddRange([c.Opening, c.Debit, c.Credit, c.Closing]);
        }

        rows.Add(totals);
        var file = TabularExport.Build(format, $"trial-balance-{report.AsOf:yyyy-MM-dd}", "Trial balance", columns, rows, rightToLeft);
        await audit.RecordAsync(new AuditEntry("report", report.CompanyId, "trial_balance", AuditActions.Exported, After: new { format, report.AsOf, report.From, report.CompareAsOf, report.Basis, report.GroupBy, report.Filters, rows = report.Rows.Count }, CompanyId: report.CompanyId), cancellationToken);
        return file;
    }

    public async Task<ExportFile> ExportLedgerAsync(AccountLedger ledger, string format, bool rightToLeft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        var columns = new List<ExportColumn>
        {
            new("posting_date", ExportCellType.Date), new("entry_number"), new("source_document_type"), new("source_document_number"), new("description"),
            new("currency"), new("debit_tc", ExportCellType.Number), new("credit_tc", ExportCellType.Number), new("debit", ExportCellType.Number), new("credit", ExportCellType.Number), new("balance", ExportCellType.Number),
        };
        var language = rightToLeft ? "ar" : "en";
        var rows = new List<IReadOnlyList<object?>> { new object?[] { null, "OPENING", null, null, null, ledger.Currency, null, null, null, null, ledger.Opening } };
        rows.AddRange(ledger.Items.Select(i => (IReadOnlyList<object?>)new object?[]
        {
            i.PostingDate, i.EntryNumber, i.SourceDocumentType, i.SourceDocumentNumber, i.Description.GetValueOrDefault(language) ?? i.Description.GetValueOrDefault("en"),
            i.CurrencyTc, i.DebitTc, i.CreditTc, i.Debit, i.Credit, i.Balance,
        }));
        rows.Add(new object?[] { null, "CLOSING", null, null, null, ledger.Currency, null, null, ledger.Debit, ledger.Credit, ledger.Closing });
        var file = TabularExport.Build(format, $"ledger-{ledger.AccountCode}-{ledger.To:yyyy-MM-dd}", "Ledger " + ledger.AccountCode, columns, rows, rightToLeft);
        await audit.RecordAsync(new AuditEntry("report", ledger.CompanyId, "ledger " + ledger.AccountCode, AuditActions.Exported, After: new { format, ledger.AccountId, ledger.From, ledger.To, ledger.Basis, ledger.Filters, items = ledger.Items.Count }, CompanyId: ledger.CompanyId), cancellationToken);
        return file;
    }

    // ------------------------------------------------------------------ steps

    private async Task<Result<Scope>> ScopeAsync(Guid companyId, InquiryQuery query, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var basis = string.IsNullOrWhiteSpace(query.Basis) ? "fc" : query.Basis.Trim().ToLowerInvariant();
        if (basis is not ("fc" or "rc"))
        {
            return Error.Validation("inquiry.basis_invalid", "The basis is fc (functional currency) or rc (reporting currency).").WithWhy(("basis", query.Basis));
        }

        if (basis == "rc" && company.ReportingCurrency is null)
        {
            return Error.Conflict("inquiry.basis_unavailable", $"Company '{company.Code}' has no reporting currency.").WithWhy(("companyId", companyId));
        }

        var asOf = query.AsOf ?? clock.TodayIn(company.TimeZone);
        if (query.From is { } from && from > asOf)
        {
            return Error.Validation("inquiry.window_inverted", $"The window start {from:yyyy-MM-dd} is after {asOf:yyyy-MM-dd}.").WithWhy(("from", from), ("asOf", asOf));
        }

        var known = (await dimensions.ListAsync(cancellationToken)).Select(static d => d.Code).ToHashSet(StringComparer.Ordinal);
        var filters = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, valueId) in query.Filters ?? NoFilters)
        {
            var normalized = code.Trim().ToUpperInvariant();
            if (!known.Contains(normalized))
            {
                return Error.Validation("inquiry.dimension_unknown", $"Dimension '{code}' does not exist.").WithWhy(("dimension", code), ("known", known.Order(StringComparer.Ordinal).ToList()));
            }

            filters[normalized] = valueId;
        }

        string? groupBy = null;
        if (!string.IsNullOrWhiteSpace(query.GroupBy))
        {
            groupBy = query.GroupBy.Trim().ToUpperInvariant();
            if (!known.Contains(groupBy))
            {
                return Error.Validation("inquiry.dimension_unknown", $"Dimension '{query.GroupBy}' does not exist.").WithWhy(("dimension", query.GroupBy), ("known", known.Order(StringComparer.Ordinal).ToList()));
            }
        }

        var currency = basis == "rc" ? company.ReportingCurrency!.Value.Code : company.FunctionalCurrency.Code;
        return new Scope(company, basis, currency, asOf, query.From, filters, groupBy, query.IncludeClosing);
    }

    /// <summary>The shared predicate: company, date ceiling, closing entries, dimension filters on the set's values; parameters named for Dapper.</summary>
    private static (string Where, DynamicParameters Parameters) Where(Scope scope, DateOnly asOf)
    {
        var parameters = new DynamicParameters();
        parameters.Add("company", scope.Company.Id.Value);
        parameters.Add("asOf", asOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        var where = "l.company_id = @company AND l.posting_date <= @asOf::date";
        if (!scope.IncludeClosing)
        {
            where += " AND NOT e.is_closing_entry";
        }

        var i = 0;
        foreach (var (code, valueId) in scope.Filters)
        {
            parameters.Add("fk" + i, code);
            parameters.Add("fv" + i, valueId.ToString("D"));
            where += $" AND ds.values->>@fk{i} = @fv{i}";
            i++;
        }

        return (where, parameters);
    }

    private async Task<Dictionary<(Guid Account, string? Value), TrialBalanceAmounts>> AggregateAsync(Scope scope, DateOnly asOf, DateOnly? from, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var (where, parameters) = Where(scope, asOf);
        parameters.Add("from", (from ?? DateOnly.MinValue).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        parameters.Add("groupBy", scope.GroupBy);
        var groupExpr = scope.GroupBy is null ? "NULL::text" : "ds.values->>@groupBy";
        var sfx = scope.Basis;
        var rows = await uow.Connection.QueryAsync<AggregateRow>(new CommandDefinition($"""
            SELECT l.account_id AS account, {groupExpr} AS value,
                   sum(CASE WHEN l.posting_date < @from::date THEN l.debit_{sfx} - l.credit_{sfx} ELSE 0 END) AS opening,
                   sum(CASE WHEN l.posting_date >= @from::date THEN l.debit_{sfx} ELSE 0 END) AS debit,
                   sum(CASE WHEN l.posting_date >= @from::date THEN l.credit_{sfx} ELSE 0 END) AS credit
            FROM app.gl_journal_lines l
            JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
            LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = l.tenant_id AND ds.id = l.dimension_set_id
            WHERE {where}
            GROUP BY l.account_id, {groupExpr}
            """, parameters, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(static r => (r.Account, r.Value), static r => new TrialBalanceAmounts(r.Opening, r.Debit, r.Credit, r.Opening + r.Debit - r.Credit));
    }

    private async Task<Dictionary<Guid, DimensionValueRow>> DimensionValuesAsync(IEnumerable<string?> values, CancellationToken cancellationToken)
    {
        var ids = values.Where(static v => v is not null).Select(static v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(static id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<Guid, DimensionValueRow>();
        }

        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<DimensionValueRow>(new CommandDefinition(
            "SELECT id, code, name_i18n::text AS name FROM app.org_dimension_values WHERE id = ANY(@ids)", new { ids }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(static r => r.Id);
    }

    private static IReadOnlyDictionary<string, string> Text(string? json) =>
        string.IsNullOrEmpty(json) ? NoText : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? NoText;

    private static IReadOnlyDictionary<string, Guid> Dimensions(string? json) =>
        string.IsNullOrEmpty(json) ? NoFilters : JsonSerializer.Deserialize<Dictionary<string, Guid>>(json, Json) ?? NoFilters;
}

/// <summary>Where a journal line's source document is read: the API path of the document for the types the platform knows.</summary>
public static class SourceDocumentLinks
{
    public static string? For(string sourceDocumentType, Guid sourceDocumentId, Guid entryId) => sourceDocumentType switch
    {
        ManualJournalService.EntityType => $"/api/v1/accounting/journals/{sourceDocumentId}",
        PostingService.JournalDocumentType => $"/api/v1/accounting/journal-entries/{entryId}",
        _ => null,
    };
}
