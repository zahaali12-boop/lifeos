using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Application.Facts;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Domain;
using Quicker.Reporting.Persistence;

namespace Quicker.Reporting.Application.Statements;

/// <summary>
/// Computes a financial statement (ADR-0034 §5) from a layout over the journal facts: every column resolves to a
/// window (the period, the year to date, the prior period or year, a fixed range) and a reading (a balance at the
/// window's end, the movement within it, the opening balance, the debits, the credits); every account row adds the
/// accounts its filter selects; every formula row evaluates over the other rows; variances are derived from two
/// columns. A balance sheet reads balances for every account (so the earnings not yet closed sit in equity); a
/// profit and loss reads movements for revenue and expense accounts and balances for the rest. The reader's company
/// and branch scopes apply, as they do to every report, so the figures tie to the ledger inquiry for the same reader.
/// </summary>
public sealed class StatementService(IUnitOfWorkAccessor unitOfWork, ReportingDbContext db, ICurrentPrincipal principal, ICompanyDirectory companies, IFiscalPeriodResolver periods, IDimensionDirectory dimensions, ProjectionService projections, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyDictionary<string, string> NoText = new Dictionary<string, string>(StringComparer.Ordinal);

    public async Task<Result<StatementResult>> RunAsync(StatementRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var computed = await ComputeAsync(request, null, null, cancellationToken);
        return computed.IsFailure ? computed.Error! : computed.Value.Result;
    }

    /// <summary>A designer's preview of a definition that is not saved: checked like a save, then computed like a run.</summary>
    public async Task<Result<StatementResult>> PreviewAsync(StatementDesignPreview request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkedRequest = LayoutService.Validate(new SaveLayoutRequest("PREVIEW", request.Kind, new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Preview" }, request.Definition));
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!;
        }

        var layout = new LayoutSummary(null, "PREVIEW", request.Kind, new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Preview", ["ar"] = "معاينة" }, NoText, null, null, false, false, true, 0, Normalise(request.Definition), null);
        var computed = await ComputeAsync(request.Request, null, layout, cancellationToken);
        return computed.IsFailure ? computed.Error! : computed.Value.Result;
    }

    /// <summary>Codes upper-cased as the save would keep them, so formulas and variances written in lower case still resolve.</summary>
    private static LayoutDefinition Normalise(LayoutDefinition definition) => new(
        definition.Rows.Select(static r => r with { Code = r.Code.Trim().ToUpperInvariant(), Formula = r.Formula?.Trim() }).ToList(),
        definition.Columns.Select(static c => c with { Code = c.Code.Trim().ToUpperInvariant(), Parameters = c.Parameters is null ? null : c.Parameters with { Of = c.Parameters.Of?.Trim().ToUpperInvariant(), Against = c.Parameters.Against?.Trim().ToUpperInvariant() } }).ToList());

    /// <summary>The accounts behind one cell: the row computed again with its accounts shown, narrowed to the column.</summary>
    public async Task<Result<StatementDrill>> DrillAsync(StatementDrillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var computed = await ComputeAsync(request.Request, request.RowCode, null, cancellationToken);
        if (computed.IsFailure)
        {
            return computed.Error!;
        }

        var result = computed.Value.Result;
        var columnIndex = result.Columns.ToList().FindIndex(c => c.Code == request.ColumnCode);
        var row = computed.Value.AllRows.FirstOrDefault(r => r.Code == request.RowCode);
        if (columnIndex < 0 || row is null)
        {
            return Error.NotFound("statement_cell", request.RowCode + "/" + request.ColumnCode);
        }

        var column = result.Columns[columnIndex];
        var accounts = (row.Accounts ?? []).Select(a => new StatementAccountLine(a.AccountId, a.Code, a.Name, a.Type, [a.Values[columnIndex]])).Where(a => a.Values[0] is not null and not 0m).ToList();
        return new StatementDrill(column, row with { Values = [row.Values[columnIndex]], Accounts = null }, accounts);
    }

    private sealed record Computed(StatementResult Result, IReadOnlyList<StatementRow> AllRows);

    private sealed record Window(DateOnly From, DateOnly To, string Suffix);

    private sealed record ResolvedColumn(LayoutColumnDefinition Definition, int WindowIndex, string Reading);

    private sealed class AccountRow
    {
        public Guid Id { get; set; }

        public string Code { get; set; } = string.Empty;

        public string Name { get; set; } = "{}";

        public string Type { get; set; } = string.Empty;

        public string Subtype { get; set; } = string.Empty;

        public string? Category { get; set; }

        public string? Role { get; set; }
    }

    private async Task<Result<Computed>> ComputeAsync(StatementRequest request, string? expandRow, LayoutSummary? preview, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var who = principal.Required;
        var spec = Catalogue.Find("journal_lines")!;
        var allowed = spec.Permissions.Where(who.Has).ToList();
        if (allowed.Count == 0 || !who.Has(ReportingPermissions.ReportRun))
        {
            return Error.Forbidden("statement.forbidden", $"Running a statement needs {ReportingPermissions.ReportRun} and {string.Join(" or ", spec.Permissions)}.").WithWhy(("permissions", spec.Permissions));
        }

        var scopes = QueryService.MergedScopes(who, allowed);
        if (!scopes.AllowsCompany(request.CompanyId))
        {
            return Error.Forbidden("statement.company_not_in_scope", "The company is outside the reader's scope.").WithWhy(("company", request.CompanyId));
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (company.ChartId is not { } chartId)
        {
            return Error.Conflict("statement.no_chart", $"Company '{company.Code}' has no chart of accounts yet.").WithWhy(("company", company.Code));
        }

        var layout = preview is null ? await FindLayoutAsync(request, cancellationToken) : preview;
        if (layout.IsFailure)
        {
            return layout.Error!;
        }

        var definition = layout.Value;
        if (definition.Definition.Columns.Any(c => c.CurrencyMode == CurrencyModes.Reporting) && company.ReportingCurrency is null)
        {
            return Error.Conflict("statement.reporting_currency_unavailable", $"Company '{company.Code}' has no reporting currency.").WithWhy(("company", company.Code));
        }

        // The window: the period end defaults to today, its start to the fiscal period holding the end, the year to the fiscal year.
        var to = request.To ?? clock.TodayIn(company.TimeZone);
        var period = await periods.ResolveAsync(company.Id, to, PostingModules.GeneralLedger, cancellationToken);
        var periodStart = period.IsSuccess ? period.Value.Period.StartsOn : new DateOnly(to.Year, to.Month, 1);
        var fiscalYearStart = period.IsSuccess ? await FiscalYearStartAsync(period.Value.Period.FiscalYearId, cancellationToken) ?? new DateOnly(to.Year, 1, 1) : new DateOnly(to.Year, 1, 1);
        var from = request.From ?? periodStart;
        if (from > to)
        {
            return Error.Validation("statement.range_invalid", "The period starts after it ends.").WithWhy(("from", from), ("to", to));
        }

        var includeClosing = request.IncludeClosing ?? definition.Kind == LayoutKinds.BalanceSheet;
        var known = (await dimensions.ListAsync(cancellationToken)).Select(static d => d.Code).ToHashSet(StringComparer.Ordinal);
        var filters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (code, valueId) in request.Filters ?? new Dictionary<string, Guid>(StringComparer.Ordinal))
        {
            var normalized = code.Trim().ToUpperInvariant();
            if (!known.Contains(normalized))
            {
                return Error.Validation("statement.dimension_unknown", $"Dimension '{code}' does not exist.").WithWhy(("dimension", code), ("known", known.Order(StringComparer.Ordinal).ToList()));
            }

            filters[normalized] = valueId.ToString("D");
        }

        await projections.EnsureFreshAsync(cancellationToken);
        var accounts = await AccountsAsync(chartId, cancellationToken);
        var windows = new List<Window>();
        var columns = new List<ResolvedColumn>();
        foreach (var column in definition.Definition.Columns)
        {
            var resolved = Resolve(column, from, to, fiscalYearStart, windows);
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            columns.Add(resolved.Value);
        }

        // One aggregate pass per distinct dimension filter (the request's, plus a row's own when it has one).
        var groups = new Dictionary<string, Dictionary<Guid, decimal[]>>(StringComparer.Ordinal);
        foreach (var row in definition.Definition.Rows.Where(static r => r.RowType == RowTypes.Accounts))
        {
            var merged = new Dictionary<string, string>(filters, StringComparer.Ordinal);
            foreach (var (code, value) in row.DimensionFilter ?? NoText)
            {
                merged[code.Trim().ToUpperInvariant()] = value;
            }

            var key = GroupKey(merged);
            if (!groups.ContainsKey(key))
            {
                groups[key] = await AggregateAsync(request.CompanyId, scopes, includeClosing, merged, windows, cancellationToken);
            }
        }

        var statementColumns = columns.Select(c => new StatementColumn(
            c.Definition.Code, c.Definition.ColumnType, c.Definition.Label,
            c.WindowIndex >= 0 ? windows[c.WindowIndex].From : null, c.WindowIndex >= 0 ? windows[c.WindowIndex].To : null,
            c.Definition.CurrencyMode == CurrencyModes.Reporting ? company.ReportingCurrency!.Value.Code : company.FunctionalCurrency.Code,
            c.Definition.ColumnType == ColumnTypes.VariancePct)).ToList();

        // Base columns first for every row (accounts, then formulas over them), then the derived columns.
        var baseIndexes = columns.Select((c, i) => (c, i)).Where(static x => !ColumnTypes.IsDerived(x.c.Definition.ColumnType)).Select(static x => x.i).ToList();
        var values = new Dictionary<string, decimal?[]>(StringComparer.Ordinal);
        var accountLines = new Dictionary<string, List<StatementAccountLine>>(StringComparer.Ordinal);
        var rowCodes = definition.Definition.Rows.Select(static r => r.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var row in definition.Definition.Rows)
        {
            var cells = new decimal?[columns.Count];
            if (row.RowType == RowTypes.Accounts)
            {
                var merged = new Dictionary<string, string>(filters, StringComparer.Ordinal);
                foreach (var (code, value) in row.DimensionFilter ?? NoText)
                {
                    merged[code.Trim().ToUpperInvariant()] = value;
                }

                var amounts = groups[GroupKey(merged)];
                var selected = accounts.Where(a => Matches(row.Accounts ?? new AccountFilter(), a)).ToList();
                var lines = new List<StatementAccountLine>();
                foreach (var account in selected)
                {
                    var perColumn = new decimal?[columns.Count];
                    var any = false;
                    foreach (var i in baseIndexes)
                    {
                        var amount = Amount(amounts, account, columns[i], windows, definition.Kind, row.FlipSign);
                        perColumn[i] = amount;
                        any |= amount != 0m;
                        cells[i] = (cells[i] ?? 0m) + amount;
                    }

                    if (any && (row.ShowAccounts || row.Code == expandRow))
                    {
                        lines.Add(new StatementAccountLine(account.Id, account.Code, Text(account.Name), account.Type, perColumn));
                    }
                }

                foreach (var i in baseIndexes)
                {
                    cells[i] ??= 0m;
                }

                accountLines[row.Code] = lines;
            }

            values[row.Code] = cells;
        }

        foreach (var row in definition.Definition.Rows.Where(static r => r.RowType == RowTypes.Formula))
        {
            var parsed = Arithmetic.Parse(row.Formula ?? string.Empty, rowCodes);
            if (parsed.IsFailure)
            {
                continue;
            }

            var cells = values[row.Code];
            foreach (var i in baseIndexes)
            {
                var scope = values.ToDictionary(static kv => kv.Key, kv => (object?)kv.Value[i], StringComparer.Ordinal);
                cells[i] = Arithmetic.Evaluate(parsed.Value, scope);
            }
        }

        for (var i = 0; i < columns.Count; i++)
        {
            if (!ColumnTypes.IsDerived(columns[i].Definition.ColumnType))
            {
                continue;
            }

            var of = columns.FindIndex(c => c.Definition.Code == columns[i].Definition.Parameters?.Of);
            var against = columns.FindIndex(c => c.Definition.Code == columns[i].Definition.Parameters?.Against);
            foreach (var (code, cells) in values)
            {
                cells[i] = of >= 0 && against >= 0 ? Derive(columns[i].Definition.ColumnType, cells[of], cells[against]) : null;
                if (accountLines.TryGetValue(code, out var lines))
                {
                    foreach (var line in lines)
                    {
                        var perColumn = (decimal?[])line.Values;
                        perColumn[i] = of >= 0 && against >= 0 ? Derive(columns[i].Definition.ColumnType, perColumn[of], perColumn[against]) : null;
                    }
                }
            }
        }

        var allRows = new List<StatementRow>();
        var shown = new List<StatementRow>();
        foreach (var row in definition.Definition.Rows)
        {
            var cells = values[row.Code];
            var lines = accountLines.GetValueOrDefault(row.Code);
            var statementRow = new StatementRow(row.Code, row.RowType, row.Label, row.Indent, row.Bold, cells, row.RowType == RowTypes.Accounts && (row.ShowAccounts || row.Code == expandRow) ? lines : null);
            allRows.Add(statementRow);
            var empty = row.RowType is RowTypes.Accounts or RowTypes.Formula && cells.All(static v => v is null or 0m) && (lines is null || lines.Count == 0);
            if (!(row.HideWhenZero && empty))
            {
                shown.Add(statementRow);
            }
        }

        var projectedUpTo = (await projections.StatusAsync(cancellationToken)).FirstOrDefault(static s => s.Fact == "journal_lines")?.Watermark;
        var result = new StatementResult(definition.Id, definition.Code, definition.Kind, definition.Name, request.CompanyId, from, to, fiscalYearStart, includeClosing, company.FunctionalCurrency.Code, statementColumns, shown, projectedUpTo, (int)watch.ElapsedMilliseconds);
        return new Computed(result, allRows);
    }

    private async Task<Result<LayoutSummary>> FindLayoutAsync(StatementRequest request, CancellationToken cancellationToken)
    {
        if (request.LayoutId is { } id)
        {
            var layout = await db.Layouts.AsNoTracking().Include(static l => l.Rows).Include(static l => l.Columns).SingleOrDefaultAsync(l => l.Id == id && l.IsActive, cancellationToken);
            return layout is null ? Error.NotFound(LayoutService.AuditEntity, id) : LayoutService.Map(layout);
        }

        var code = (request.LayoutCode ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            return Error.Validation("statement.layout_required", "A statement names its layout by id or by code.");
        }

        var own = await db.Layouts.AsNoTracking().Include(static l => l.Rows).Include(static l => l.Columns).SingleOrDefaultAsync(l => l.Code == code && l.IsActive, cancellationToken);
        if (own is not null)
        {
            return LayoutService.Map(own);
        }

        return ShippedLayouts.Find(code) is { } shipped ? shipped : Error.NotFound(LayoutService.AuditEntity, code);
    }

    private async Task<DateOnly?> FiscalYearStartAsync(Guid fiscalYearId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var raw = await uow.Connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition("SELECT starts_on FROM app.org_fiscal_years WHERE id = @id", new { id = fiscalYearId }, uow.Transaction, cancellationToken: cancellationToken));
        return raw is { } d ? DateOnly.FromDateTime(d) : null;
    }

    private async Task<List<AccountRow>> AccountsAsync(Guid chartId, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        return (await uow.Connection.QueryAsync<AccountRow>(new CommandDefinition("""
            SELECT a.id AS Id, a.code AS Code, a.name_i18n::text AS Name, a.type AS Type, a.subtype AS Subtype, c.code AS Category, a.default_role AS Role
            FROM app.gl_accounts a
            LEFT JOIN app.gl_account_categories c ON c.tenant_id = a.tenant_id AND c.id = a.category_id
            WHERE a.chart_id = @chart AND NOT a.is_header
            ORDER BY a.code
            """, new { chart = chartId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
    }

    /// <summary>Resolves a column to its window (shared with every column over the same dates and currency) and its reading.</summary>
    private static Result<ResolvedColumn> Resolve(LayoutColumnDefinition column, DateOnly from, DateOnly to, DateOnly fiscalYearStart, List<Window> windows)
    {
        var suffix = column.CurrencyMode == CurrencyModes.Reporting ? "rc" : "fc";
        (DateOnly From, DateOnly To)? range = column.ColumnType switch
        {
            ColumnTypes.Period or ColumnTypes.Opening or ColumnTypes.Debits or ColumnTypes.Credits or ColumnTypes.Closing => (from, to),
            ColumnTypes.YearToDate => (fiscalYearStart <= to ? fiscalYearStart : from, to),
            ColumnTypes.PriorPeriod => PriorPeriod(from, to),
            ColumnTypes.PriorYear => (from.AddYears(-1), to.AddYears(-1)),
            ColumnTypes.CustomRange => column.Parameters is { From: { } f, To: { } t } && f <= t ? (f, t) : null,
            _ => null,
        };
        if (ColumnTypes.IsDerived(column.ColumnType))
        {
            return new ResolvedColumn(column, -1, column.ColumnType);
        }

        if (range is null)
        {
            return Error.Validation("layout.column_range_required", $"Column '{column.Code}' needs a from and to date.").WithWhy(("column", column.Code), ("type", column.ColumnType));
        }

        var window = new Window(range.Value.From, range.Value.To, suffix);
        var index = windows.IndexOf(window);
        if (index < 0)
        {
            windows.Add(window);
            index = windows.Count - 1;
        }

        var reading = column.ColumnType switch
        {
            ColumnTypes.Opening => "opening",
            ColumnTypes.Debits => "debits",
            ColumnTypes.Credits => "credits",
            ColumnTypes.Closing => "closing",
            _ => "statement",
        };
        return new ResolvedColumn(column, index, reading);
    }

    /// <summary>Whole months back when the window is whole months, otherwise the same number of days immediately before it.</summary>
    private static (DateOnly From, DateOnly To) PriorPeriod(DateOnly from, DateOnly to)
    {
        var endOfMonth = to.Day == DateTime.DaysInMonth(to.Year, to.Month);
        if (from.Day == 1 && endOfMonth)
        {
            var months = (to.Year - from.Year) * 12 + to.Month - from.Month + 1;
            var priorFrom = from.AddMonths(-months);
            var priorToMonth = to.AddMonths(-months);
            return (priorFrom, new DateOnly(priorToMonth.Year, priorToMonth.Month, DateTime.DaysInMonth(priorToMonth.Year, priorToMonth.Month)));
        }

        var length = to.DayNumber - from.DayNumber + 1;
        return (from.AddDays(-length), from.AddDays(-1));
    }

    private static string GroupKey(Dictionary<string, string> filters) => string.Join(";", filters.OrderBy(static kv => kv.Key, StringComparer.Ordinal).Select(static kv => kv.Key + "=" + kv.Value));

    /// <summary>Per account: balance at the end, balance before the start, debits and credits within, for every window; one statement over the facts.</summary>
    private async Task<Dictionary<Guid, decimal[]>> AggregateAsync(Guid companyId, RecordScopes scopes, bool includeClosing, Dictionary<string, string> filters, List<Window> windows, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var parameters = new DynamicParameters();
        parameters.Add("company", companyId);
        var select = new StringBuilder("SELECT f.account_id AS account");
        for (var i = 0; i < windows.Count; i++)
        {
            var w = windows[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            parameters.Add("f" + n, w.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            parameters.Add("t" + n, w.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            select.Append(CultureInfo.InvariantCulture, $", sum(CASE WHEN f.posting_date <= @t{n}::date THEN f.debit_{w.Suffix} - f.credit_{w.Suffix} ELSE 0 END) AS bal{n}");
            select.Append(CultureInfo.InvariantCulture, $", sum(CASE WHEN f.posting_date < @f{n}::date THEN f.debit_{w.Suffix} - f.credit_{w.Suffix} ELSE 0 END) AS op{n}");
            select.Append(CultureInfo.InvariantCulture, $", sum(CASE WHEN f.posting_date >= @f{n}::date AND f.posting_date <= @t{n}::date THEN f.debit_{w.Suffix} ELSE 0 END) AS dr{n}");
            select.Append(CultureInfo.InvariantCulture, $", sum(CASE WHEN f.posting_date >= @f{n}::date AND f.posting_date <= @t{n}::date THEN f.credit_{w.Suffix} ELSE 0 END) AS cr{n}");
        }

        var where = new StringBuilder("f.company_id = @company");
        if (scopes.BranchIds.Count > 0)
        {
            parameters.Add("branches", scopes.BranchIds.ToArray());
            where.Append(" AND f.branch_id = ANY(@branches)");
        }

        if (!includeClosing)
        {
            where.Append(" AND NOT f.is_closing_entry");
        }

        var k = 0;
        foreach (var (code, value) in filters)
        {
            parameters.Add("fk" + k, code);
            parameters.Add("fv" + k, value);
            where.Append(CultureInfo.InvariantCulture, $" AND ds.values->>@fk{k} = @fv{k}");
            k++;
        }

        var sql = $"""
            {select}
            FROM reporting.fact_journal_lines f
            LEFT JOIN app.org_dimension_sets ds ON ds.tenant_id = f.tenant_id AND ds.id = f.dimension_set_id
            WHERE {where}
            GROUP BY f.account_id
            """;
        var result = new Dictionary<Guid, decimal[]>();
        foreach (IDictionary<string, object> row in await uow.Connection.QueryAsync(new CommandDefinition(sql, parameters, uow.Transaction, cancellationToken: cancellationToken)))
        {
            var amounts = new decimal[windows.Count * 4];
            for (var i = 0; i < windows.Count; i++)
            {
                var n = i.ToString(CultureInfo.InvariantCulture);
                amounts[i * 4] = Decimal(row["bal" + n]);
                amounts[(i * 4) + 1] = Decimal(row["op" + n]);
                amounts[(i * 4) + 2] = Decimal(row["dr" + n]);
                amounts[(i * 4) + 3] = Decimal(row["cr" + n]);
            }

            result[(Guid)row["account"]] = amounts;
        }

        return result;
    }

    private static decimal Decimal(object? value) => value is decimal d ? d : value is null ? 0m : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    /// <summary>One account's figure for one column: the reading over the column's window, the sign flipped when the row says so.</summary>
    private static decimal Amount(Dictionary<Guid, decimal[]> amounts, AccountRow account, ResolvedColumn column, List<Window> windows, string kind, bool flip)
    {
        if (!amounts.TryGetValue(account.Id, out var all))
        {
            return 0m;
        }

        var i = column.WindowIndex * 4;
        var balance = all[i];
        var opening = all[i + 1];
        var debits = all[i + 2];
        var credits = all[i + 3];
        _ = windows;
        var value = column.Reading switch
        {
            "opening" => opening,
            "debits" => debits,
            "credits" => credits,
            "closing" => balance,
            // A balance sheet carries every balance to its date; the other statements read the movement of revenue and expense accounts.
            _ => kind == LayoutKinds.BalanceSheet || AccountTypes.IsBalanceSheet(account.Type) ? balance : debits - credits,
        };
        return flip ? -value : value;
    }

    private static decimal? Derive(string type, decimal? of, decimal? against)
    {
        if (of is null || against is null)
        {
            return null;
        }

        if (type == ColumnTypes.Variance)
        {
            return of - against;
        }

        return against == 0m ? null : (of - against) / Math.Abs(against.Value) * 100m;
    }

    /// <summary>Whether an account falls under a row's filter: every stated inclusion must hold and no exclusion may.</summary>
    public static bool Matches(AccountFilter filter, string code, string type, string subtype, string? category, string? role)
    {
        ArgumentNullException.ThrowIfNull(filter);
        static bool In(IReadOnlyList<string>? list, string? value) => list is null || list.Count == 0 || (value is not null && list.Contains(value, StringComparer.OrdinalIgnoreCase));
        static bool NotIn(IReadOnlyList<string>? list, string? value) => list is null || list.Count == 0 || value is null || !list.Contains(value, StringComparer.OrdinalIgnoreCase);
        return In(filter.Types, type)
            && In(filter.Subtypes, subtype)
            && In(filter.Categories, category)
            && In(filter.Roles, role)
            && In(filter.AccountCodes, code)
            && (string.IsNullOrEmpty(filter.CodeFrom) || string.CompareOrdinal(code, filter.CodeFrom) >= 0)
            && (string.IsNullOrEmpty(filter.CodeTo) || string.CompareOrdinal(code, filter.CodeTo) <= 0)
            && NotIn(filter.ExcludeSubtypes, subtype)
            && NotIn(filter.ExcludeCategories, category)
            && NotIn(filter.ExcludeAccountCodes, code);
    }

    private static bool Matches(AccountFilter filter, AccountRow account) => Matches(filter, account.Code, account.Type, account.Subtype, account.Category, account.Role);

    private static IReadOnlyDictionary<string, string> Text(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>(StringComparer.Ordinal);
}
