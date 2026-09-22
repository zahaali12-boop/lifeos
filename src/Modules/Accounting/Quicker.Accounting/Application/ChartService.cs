using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Accounting.Application;

/// <summary>
/// Charts of accounts (roadmap 2.1): charts shared or per company, the account tree with types, categories,
/// control flags and dimension rules, templates, statutory mappings, import/export, and the line checks the
/// posting engine and manual journals run before writing a line (<see cref="IChartOfAccounts"/>).
/// </summary>
public sealed class ChartService(
    AccountingDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    IDimensionDirectory dimensionDirectory,
    IAuditSink audit,
    IClock clock) : IChartOfAccounts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ templates

    public static IReadOnlyList<TemplateSummary> Templates() =>
        ChartTemplates.All.Select(static t => new TemplateSummary(t.Code, t.Name.Values, t.Description.Values, t.Accounts.Count, t.Roles, t.StatutoryChartCode)).ToList();

    public async Task<Result<ChartSummary>> CreateFromTemplateAsync(FromTemplateRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var template = ChartTemplates.Find(request.TemplateCode);
        if (template is null)
        {
            return Error.NotFound("chart_template", request.TemplateCode);
        }

        var created = await CreateChartAsync(new SaveChartRequest(request.Code, request.Name ?? template.Name.Values, template.AccountCodeFormat, request.Shared ? null : request.CompanyId), template.Code, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var chart = created.Value;
        var now = clock.UtcNow;
        var categories = await EnsureCategoriesAsync(template.Categories, now, cancellationToken);
        var byCode = new Dictionary<string, Account>(StringComparer.Ordinal);
        foreach (var t in template.Accounts)
        {
            var account = new Account
            {
                Id = Guid.CreateVersion7(),
                ChartId = chart.Id,
                ParentId = t.Parent is { } parent ? byCode[parent].Id : null,
                Code = t.Code,
                Name = t.Name,
                Type = t.Type,
                Subtype = t.Subtype,
                CategoryId = t.Category is { } category ? categories[category] : null,
                IsHeader = t.IsHeader,
                IsControl = t.IsControl,
                SubledgerType = t.Subledger,
                AllowManualPosting = t.AllowManualPosting,
                RevalueFx = t.RevalueFx,
                CashFlowCategory = t.CashFlow,
                DefaultRole = t.Role,
                CreatedAt = now,
                UpdatedAt = now,
            };
            byCode[t.Code] = account;
            db.Accounts.Add(account);
        }

        if (template.StatutoryChartCode is { } statutory)
        {
            foreach (var (code, statutoryCode) in template.StatutoryMapping)
            {
                db.Mappings.Add(new AccountMapping { AccountId = byCode[code].Id, StatutoryChartCode = statutory, StatutoryCode = statutoryCode });
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_chart", chart.Id, chart.Code, "seeded_from_template", After: new { template = template.Code, accounts = template.Accounts.Count }), cancellationToken);

        if (request.CompanyId is { } companyId)
        {
            var assigned = await AssignToCompanyAsync(companyId, chart.Id, cancellationToken);
            if (assigned.IsFailure)
            {
                return assigned.Error!;
            }
        }

        return (await GetChartAsync(chart.Id, null, cancellationToken))!;
    }

    private async Task<Dictionary<string, Guid>> EnsureCategoriesAsync(IReadOnlyList<TemplateCategory> wanted, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var existing = await db.Categories.ToDictionaryAsync(static c => c.Code, static c => c.Id, StringComparer.Ordinal, cancellationToken);
        foreach (var category in wanted.Where(c => !existing.ContainsKey(c.Code)))
        {
            var row = new AccountCategory { Id = Guid.CreateVersion7(), Code = category.Code, Name = category.Name, Statement = category.Statement, SortOrder = category.SortOrder, IsSystem = true, CreatedAt = now, UpdatedAt = now };
            db.Categories.Add(row);
            existing[category.Code] = row.Id;
        }

        return existing;
    }

    // ------------------------------------------------------------------ charts

    public async Task<IReadOnlyList<ChartSummary>> ListChartsAsync(CancellationToken cancellationToken)
    {
        var counts = await db.Accounts.GroupBy(static a => a.ChartId).Select(static g => new { ChartId = g.Key, Count = g.Count() }).ToDictionaryAsync(static x => x.ChartId, static x => x.Count, cancellationToken);
        var charts = await db.Charts.OrderBy(static c => c.Code).ToListAsync(cancellationToken);
        return charts.Select(c => Map(c, counts.GetValueOrDefault(c.Id))).ToList();
    }

    public async Task<ChartSummary?> GetChartAsync(Guid chartId, string? expand, CancellationToken cancellationToken)
    {
        var chart = await db.Charts.SingleOrDefaultAsync(c => c.Id == chartId, cancellationToken);
        if (chart is null)
        {
            return null;
        }

        var count = await db.Accounts.CountAsync(a => a.ChartId == chartId, cancellationToken);
        var summary = Map(chart, count);
        if (expand?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("accounts", StringComparer.OrdinalIgnoreCase) == true)
        {
            summary = summary with { Accounts = await ListAccountsAsync(chartId, cancellationToken) };
        }

        return summary;
    }

    public Task<Result<ChartSummary>> CreateChartAsync(SaveChartRequest request, CancellationToken cancellationToken) => CreateChartAsync(request, null, cancellationToken);

    private async Task<Result<ChartSummary>> CreateChartAsync(SaveChartRequest request, string? templateCode, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = new Chart { Id = Guid.CreateVersion7(), TemplateCode = templateCode, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(chart, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (await db.Charts.AnyAsync(c => c.Code == chart.Code, cancellationToken))
        {
            return Error.Conflict("chart.code_taken", $"A chart with code '{chart.Code}' already exists.");
        }

        db.Charts.Add(chart);
        await db.SaveChangesAsync(cancellationToken);
        return Map(chart, 0);
    }

    public async Task<Result<ChartSummary>> UpdateChartAsync(Guid chartId, SaveChartRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = await db.Charts.SingleOrDefaultAsync(c => c.Id == chartId, cancellationToken);
        if (chart is null)
        {
            return Error.NotFound("chart", chartId);
        }

        var applied = await ApplyAsync(chart, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        if (await db.Charts.AnyAsync(c => c.Code == chart.Code && c.Id != chartId, cancellationToken))
        {
            return Error.Conflict("chart.code_taken", $"A chart with code '{chart.Code}' already exists.");
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await GetChartAsync(chartId, null, cancellationToken))!;
    }

    private async Task<Result> ApplyAsync(Chart chart, SaveChartRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.Code(request.Code, "chart");
        var name = Validation.Name(request.Name, "chart");
        var format = Validation.CodeFormat(request.AccountCodeFormat);
        if (code.IsFailure || name.IsFailure || format.IsFailure)
        {
            return code.Error ?? name.Error ?? format.Error!;
        }

        if (request.CompanyId is { } companyId && await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        chart.Code = code.Value.ToUpperInvariant();
        chart.Name = name.Value;
        chart.AccountCodeFormat = format.Value;
        chart.CompanyId = request.CompanyId;
        chart.IsActive = request.IsActive;
        chart.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    // ------------------------------------------------------------------ accounts

    public async Task<IReadOnlyList<AccountSummary>> ListAccountsAsync(Guid chartId, CancellationToken cancellationToken)
    {
        var accounts = await db.Accounts.Where(a => a.ChartId == chartId).OrderBy(static a => a.Code).ToListAsync(cancellationToken);
        var categories = await db.Categories.ToDictionaryAsync(static c => c.Id, static c => c.Code, cancellationToken);
        return Tree(accounts, categories);
    }

    public async Task<AccountSummary?> GetAccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        return (await ListAccountsAsync(account.ChartId, cancellationToken)).Single(a => a.Id == accountId);
    }

    public async Task<Result<AccountSummary>> CreateAccountAsync(Guid chartId, SaveAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var chart = await db.Charts.SingleOrDefaultAsync(c => c.Id == chartId, cancellationToken);
        if (chart is null)
        {
            return Error.NotFound("chart", chartId);
        }

        var siblings = await db.Accounts.Where(a => a.ChartId == chartId).ToListAsync(cancellationToken);
        var account = new Account { Id = Guid.CreateVersion7(), ChartId = chartId, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(chart, account, request, siblings, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Accounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);
        return (await GetAccountAsync(account.Id, cancellationToken))!;
    }

    public async Task<Result<AccountSummary>> UpdateAccountAsync(Guid accountId, SaveAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("account", accountId);
        }

        var chart = await db.Charts.SingleAsync(c => c.Id == account.ChartId, cancellationToken);
        var all = await db.Accounts.Where(a => a.ChartId == account.ChartId).ToListAsync(cancellationToken);
        var applied = await ApplyAsync(chart, account, request, all, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await GetAccountAsync(account.Id, cancellationToken))!;
    }

    /// <summary>Every rule an account must satisfy; <paramref name="all"/> holds the chart's accounts (tracked, so the current one is among them on update).</summary>
    private async Task<Result> ApplyAsync(Chart chart, Account account, SaveAccountRequest request, List<Account> all, CancellationToken cancellationToken)
    {
        var code = Validation.AccountCode(request.Code, chart.AccountCodeFormat);
        var name = Validation.Name(request.Name, "account");
        var type = Validation.OneOf(request.Type, "account.type", AccountTypes.All);
        var subledger = Validation.OptionalOneOf(request.SubledgerType, "account.subledger_type", SubledgerTypes.All);
        var cashFlow = Validation.OptionalOneOf(request.CashFlowCategory, "account.cash_flow_category", CashFlowCategories.All);
        var role = Validation.OptionalOneOf(request.DefaultRole, "account.default_role", AccountRoles.All);
        if (code.IsFailure || name.IsFailure || type.IsFailure || subledger.IsFailure || cashFlow.IsFailure || role.IsFailure)
        {
            return code.Error ?? name.Error ?? type.Error ?? subledger.Error ?? cashFlow.Error ?? role.Error!;
        }

        if (all.Any(a => a.Id != account.Id && a.Code == code.Value))
        {
            return Error.Conflict("account.code_taken", $"Account '{code.Value}' already exists on this chart.");
        }

        Account? parent = null;
        if (request.ParentId is { } parentId || !string.IsNullOrWhiteSpace(request.ParentCode))
        {
            parent = request.ParentId is { } pid ? all.FirstOrDefault(a => a.Id == pid) : all.FirstOrDefault(a => a.Code == request.ParentCode!.Trim());
            if (parent is null)
            {
                return Error.NotFound("account", (object?)request.ParentId ?? request.ParentCode!);
            }

            if (parent.Id == account.Id)
            {
                return Error.Validation("account.parent_self", "An account cannot be its own parent.");
            }

            if (!parent.IsHeader)
            {
                return Error.Validation("account.parent_not_header", $"Parent '{parent.Code}' is a postable account; only headers group accounts.").WithWhy(("parent", parent.Code));
            }

            if (parent.Type != type.Value)
            {
                return Error.Validation("account.type_mismatch", $"'{code.Value}' is {type.Value} but its parent '{parent.Code}' is {parent.Type}.").WithWhy(("parent", parent.Code), ("parentType", parent.Type), ("type", type.Value));
            }

            for (var ancestor = parent; ancestor is not null; ancestor = all.FirstOrDefault(a => a.Id == ancestor.ParentId))
            {
                if (ancestor.ParentId == account.Id)
                {
                    return Error.Validation("account.parent_cycle", "Moving the account under its own descendant would create a cycle.");
                }
            }
        }

        if (request.IsHeader && request.IsControl)
        {
            return Error.Validation("account.header_control", "A header account cannot be a control account.");
        }

        if (request.IsControl && subledger.Value is null)
        {
            return Error.Validation("account.subledger_required", "A control account names the subledger it reconciles to (AR, AP, INV, FA, BANK, PDC, GRNI, IC, WHT).");
        }

        if (!request.IsControl && subledger.Value is not null)
        {
            return Error.Validation("account.subledger_without_control", "Only control accounts carry a subledger type.");
        }

        var children = all.Where(a => a.ParentId == account.Id && a.Id != account.Id).ToList();
        if (!request.IsHeader && children.Count > 0)
        {
            return Error.Conflict("account.has_children", $"'{account.Code}' groups {children.Count} account(s); move them first.").WithWhy(("children", children.Select(static c => c.Code).ToList()));
        }

        if (children.Any(c => c.Type != type.Value))
        {
            return Error.Conflict("account.type_mismatch", "Child accounts have another type.").WithWhy(("children", children.Where(c => c.Type != type.Value).Select(static c => c.Code).ToList()));
        }

        Guid? categoryId = null;
        if (!string.IsNullOrWhiteSpace(request.CategoryCode))
        {
            categoryId = await db.Categories.Where(c => c.Code == request.CategoryCode.Trim()).Select(static c => (Guid?)c.Id).SingleOrDefaultAsync(cancellationToken);
            if (categoryId is null)
            {
                return Error.NotFound("account_category", request.CategoryCode);
            }
        }

        string? currency = null;
        if (!string.IsNullOrWhiteSpace(request.CurrencyRestriction))
        {
            if (await companies.FindCurrencyAsync(request.CurrencyRestriction.Trim().ToUpperInvariant(), cancellationToken) is not { } found)
            {
                return Error.Validation("account.currency_unknown", $"Currency '{request.CurrencyRestriction}' is not in the ISO 4217 list.");
            }

            currency = found.Code;
        }

        if (request.CompanyId is { } companyId)
        {
            var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
            if (company is null)
            {
                return Error.NotFound("company", companyId);
            }

            if (chart.CompanyId is { } chartCompany && chartCompany != companyId)
            {
                return Error.Validation("account.company_outside_chart", "The chart belongs to another company.").WithWhy(("chartCompanyId", chartCompany));
            }
        }

        account.Code = code.Value;
        account.Name = name.Value;
        account.Type = type.Value;
        account.Subtype = request.Subtype?.Trim() ?? string.Empty;
        account.ParentId = parent?.Id;
        account.CategoryId = categoryId;
        account.IsHeader = request.IsHeader;
        account.IsControl = request.IsControl;
        account.SubledgerType = subledger.Value;
        account.CurrencyRestriction = currency;
        account.AllowManualPosting = !request.IsHeader && request.AllowManualPosting;
        account.RevalueFx = request.RevalueFx;
        account.CashFlowCategory = cashFlow.Value;
        account.DefaultRole = role.Value;
        account.CompanyId = request.CompanyId;
        account.IsActive = request.IsActive;
        account.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    // ------------------------------------------------------------------ dimension rules

    public async Task<Result<IReadOnlyList<DimensionRuleSummary>>> ListDimensionRulesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        if (!await db.Accounts.AnyAsync(a => a.Id == accountId, cancellationToken))
        {
            return Error.NotFound("account", accountId);
        }

        var codes = (await dimensionDirectory.ListAsync(cancellationToken)).ToDictionary(static d => d.Id, static d => d.Code);
        var rules = await db.DimensionRules.Where(r => r.AccountId == accountId).ToListAsync(cancellationToken);
        return rules.Select(r => new DimensionRuleSummary(r.DimensionId, codes.GetValueOrDefault(r.DimensionId, string.Empty), r.Rule, r.DefaultValueId)).OrderBy(static r => r.DimensionCode, StringComparer.Ordinal).ToList();
    }

    public async Task<Result<IReadOnlyList<DimensionRuleSummary>>> SetDimensionRulesAsync(Guid accountId, IReadOnlyList<DimensionRuleRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var account = await db.Accounts.Include(static a => a.DimensionRules).SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("account", accountId);
        }

        var known = (await dimensionDirectory.ListAsync(cancellationToken)).ToDictionary(static d => d.Code, StringComparer.Ordinal);
        var rules = new List<AccountDimensionRule>();
        foreach (var request in requests)
        {
            var code = request.DimensionCode?.Trim().ToUpperInvariant() ?? string.Empty;
            if (!known.TryGetValue(code, out var dimension))
            {
                return Error.NotFound("dimension", code);
            }

            var rule = Validation.OneOf(request.Rule, "dimension_rule", DimensionRules.All);
            if (rule.IsFailure)
            {
                return rule.Error!;
            }

            if (rules.Any(r => r.DimensionId == dimension.Id))
            {
                return Error.Validation("dimension_rule.duplicate", $"Dimension {code} appears twice.").WithWhy(("dimension", code));
            }

            if (request.DefaultValueId is { } defaultValueId)
            {
                if (rule.Value == DimensionRules.Blocked)
                {
                    return Error.Validation("dimension_rule.blocked_default", "A blocked dimension cannot have a default value.").WithWhy(("dimension", code));
                }

                var value = await dimensionDirectory.FindValueAsync(defaultValueId, cancellationToken);
                if (value is null || value.DimensionId != dimension.Id)
                {
                    return Error.Validation("dimension_rule.default_mismatch", $"Default value {defaultValueId} does not belong to dimension {code}.").WithWhy(("dimension", code), ("valueId", defaultValueId));
                }
            }

            rules.Add(new AccountDimensionRule { AccountId = accountId, DimensionId = dimension.Id, Rule = rule.Value, DefaultValueId = request.DefaultValueId });
        }

        var before = account.DimensionRules.Select(r => new { dimension = known.Values.FirstOrDefault(d => d.Id == r.DimensionId)?.Code, r.Rule, r.DefaultValueId }).ToList();
        db.DimensionRules.RemoveRange(account.DimensionRules);
        account.DimensionRules.Clear();
        account.DimensionRules.AddRange(rules);
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_account", account.Id, account.Code, "dimension_rules_changed", Before: before,
            After: rules.Select(r => new { dimension = known.Values.First(d => d.Id == r.DimensionId).Code, r.Rule, r.DefaultValueId }).ToList()), cancellationToken);
        return await ListDimensionRulesAsync(accountId, cancellationToken);
    }

    // ------------------------------------------------------------------ categories

    public async Task<IReadOnlyList<CategorySummary>> ListCategoriesAsync(CancellationToken cancellationToken) =>
        await db.Categories.OrderBy(static c => c.SortOrder).ThenBy(static c => c.Code).Select(static c => new CategorySummary(c.Id, c.Code, c.Name.Values, c.Statement, c.SortOrder, c.IsSystem)).ToListAsync(cancellationToken);

    public async Task<Result<CategorySummary>> SaveCategoryAsync(string code, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var normalized = code?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length is 0 or > 32 || !char.IsAsciiLetterLower(normalized[0]) || !normalized.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
        {
            return Error.Validation("account_category.code_invalid", "Category codes are lower-case letters, digits and '_'.");
        }

        var name = Validation.Name(request.Name, "account_category");
        var statement = Validation.OneOf(request.Statement, "account_category.statement", ["bs", "pl", "ocf"]);
        if (name.IsFailure || statement.IsFailure)
        {
            return name.Error ?? statement.Error!;
        }

        var now = clock.UtcNow;
        var category = await db.Categories.SingleOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        if (category is null)
        {
            category = new AccountCategory { Id = Guid.CreateVersion7(), Code = normalized, CreatedAt = now };
            db.Categories.Add(category);
        }

        category.Name = name.Value;
        category.Statement = statement.Value;
        category.SortOrder = request.SortOrder;
        category.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return new CategorySummary(category.Id, category.Code, category.Name.Values, category.Statement, category.SortOrder, category.IsSystem);
    }

    // ------------------------------------------------------------------ import / export

    public async Task<Result<string>> ExportCsvAsync(Guid chartId, CancellationToken cancellationToken)
    {
        if (!await db.Charts.AnyAsync(c => c.Id == chartId, cancellationToken))
        {
            return Error.NotFound("chart", chartId);
        }

        return AccountCsv.Write(await ListAccountsAsync(chartId, cancellationToken));
    }

    /// <summary>Upserts accounts by code, parents before children, all or nothing: the first invalid row fails the whole import with its position.</summary>
    public async Task<Result<ImportResult>> ImportAsync(Guid chartId, IReadOnlyList<SaveAccountRequest> rows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var chart = await db.Charts.SingleOrDefaultAsync(c => c.Id == chartId, cancellationToken);
        if (chart is null)
        {
            return Error.NotFound("chart", chartId);
        }

        var codes = rows.Select(static r => r.Code?.Trim() ?? string.Empty).ToList();
        var duplicate = codes.GroupBy(static c => c, StringComparer.Ordinal).FirstOrDefault(static g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Error.Validation("import.duplicate_code", $"Account '{duplicate.Key}' appears more than once in the file.").WithWhy(("code", duplicate.Key));
        }

        var all = await db.Accounts.Where(a => a.ChartId == chartId).ToListAsync(cancellationToken);
        var existingCodes = all.Select(static a => a.Code).ToHashSet(StringComparer.Ordinal);
        var ordered = Order(rows, existingCodes);
        if (ordered.IsFailure)
        {
            return ordered.Error!;
        }

        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var now = clock.UtcNow;
        foreach (var (row, position) in ordered.Value)
        {
            var account = all.FirstOrDefault(a => a.Code == row.Code.Trim());
            var isNew = account is null;
            account ??= new Account { Id = Guid.CreateVersion7(), ChartId = chartId, CreatedAt = now };
            var fingerprint = isNew ? null : Fingerprint(account);
            var applied = await ApplyAsync(chart, account, row, all, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error!.WithWhy(("row", position), ("code", row.Code));
            }

            if (isNew)
            {
                db.Accounts.Add(account);
                all.Add(account);
                created++;
            }
            else if (Fingerprint(account) == fingerprint)
            {
                unchanged++;
            }
            else
            {
                updated++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_chart", chart.Id, chart.Code, "imported", After: new { created, updated, unchanged }), cancellationToken);
        return new ImportResult(created, updated, unchanged, rows.Count);
    }

    private static Result<List<(SaveAccountRequest Row, int Position)>> Order(IReadOnlyList<SaveAccountRequest> rows, HashSet<string> existing)
    {
        var pending = rows.Select(static (r, i) => (Row: r, Position: i + 1)).ToList();
        var placed = new HashSet<string>(existing, StringComparer.Ordinal);
        var ordered = new List<(SaveAccountRequest, int)>(rows.Count);
        while (pending.Count > 0)
        {
            var ready = pending.Where(p => string.IsNullOrWhiteSpace(p.Row.ParentCode) || placed.Contains(p.Row.ParentCode.Trim()) || p.Row.ParentId is not null).ToList();
            if (ready.Count == 0)
            {
                var first = pending[0];
                return Error.Validation("import.parent_unknown", $"Row {first.Position}: parent '{first.Row.ParentCode}' is neither in the file nor on the chart.").WithWhy(("row", first.Position), ("parentCode", first.Row.ParentCode));
            }

            foreach (var item in ready)
            {
                ordered.Add(item);
                placed.Add(item.Row.Code?.Trim() ?? string.Empty);
                pending.Remove(item);
            }
        }

        return ordered;
    }

    private static string Fingerprint(Account a) => JsonSerializer.Serialize(new
    {
        a.Code,
        Name = a.Name.Values,
        a.Type,
        a.Subtype,
        a.ParentId,
        a.CategoryId,
        a.IsHeader,
        a.IsControl,
        a.SubledgerType,
        a.CurrencyRestriction,
        a.AllowManualPosting,
        a.RevalueFx,
        a.CashFlowCategory,
        a.DefaultRole,
        a.CompanyId,
        a.IsActive,
    }, Json);

    // ------------------------------------------------------------------ statutory mappings

    public async Task<IReadOnlyList<StatutoryChartSummary>> ListStatutoryChartsAsync(CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<(string Code, string Name, string Accounts, string Notes)>(new CommandDefinition(
            "SELECT code, name_i18n::text, accounts::text, notes FROM control.gl_statutory_charts ORDER BY code", transaction: uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(static r => new StatutoryChartSummary(r.Code, JsonSerializer.Deserialize<Dictionary<string, string>>(r.Name)!, r.Notes, ParseStatutoryAccounts(r.Accounts))).ToList();
    }

    private static List<StatutoryAccount> ParseStatutoryAccounts(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateArray().Select(static e => new StatutoryAccount(
            e.GetProperty("code").GetString()!,
            e.TryGetProperty("level", out var level) ? level.GetInt32() : 1,
            e.GetProperty("name").EnumerateObject().ToDictionary(static p => p.Name, static p => p.Value.GetString() ?? string.Empty, StringComparer.Ordinal))).ToList();
    }

    public async Task<Result<IReadOnlyList<MappingSummary>>> ListMappingsAsync(Guid chartId, string statutoryChartCode, CancellationToken cancellationToken)
    {
        var statutory = (await ListStatutoryChartsAsync(cancellationToken)).FirstOrDefault(s => string.Equals(s.Code, statutoryChartCode, StringComparison.OrdinalIgnoreCase));
        if (statutory is null)
        {
            return Error.NotFound("statutory_chart", statutoryChartCode);
        }

        if (!await db.Charts.AnyAsync(c => c.Id == chartId, cancellationToken))
        {
            return Error.NotFound("chart", chartId);
        }

        var names = statutory.Accounts.ToDictionary(static a => a.Code, static a => a.Name, StringComparer.Ordinal);
        var accounts = await db.Accounts.Where(a => a.ChartId == chartId && !a.IsHeader).OrderBy(static a => a.Code).ToListAsync(cancellationToken);
        var ids = accounts.Select(static a => a.Id).ToList();
        var mappings = await db.Mappings.Where(m => ids.Contains(m.AccountId) && m.StatutoryChartCode == statutory.Code).ToDictionaryAsync(static m => m.AccountId, static m => m.StatutoryCode, cancellationToken);
        return accounts.Select(a =>
        {
            var code = mappings.GetValueOrDefault(a.Id);
            return new MappingSummary(a.Id, a.Code, a.Name.Values, code, code is null ? null : names.GetValueOrDefault(code));
        }).ToList();
    }

    /// <summary>Replaces the mapping of the listed accounts; an empty statutory code removes the mapping. Unlisted accounts keep theirs.</summary>
    public async Task<Result<IReadOnlyList<MappingSummary>>> SetMappingsAsync(Guid chartId, string statutoryChartCode, IReadOnlyList<MappingRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var statutory = (await ListStatutoryChartsAsync(cancellationToken)).FirstOrDefault(s => string.Equals(s.Code, statutoryChartCode, StringComparison.OrdinalIgnoreCase));
        if (statutory is null)
        {
            return Error.NotFound("statutory_chart", statutoryChartCode);
        }

        var chart = await db.Charts.SingleOrDefaultAsync(c => c.Id == chartId, cancellationToken);
        if (chart is null)
        {
            return Error.NotFound("chart", chartId);
        }

        var valid = statutory.Accounts.Select(static a => a.Code).ToHashSet(StringComparer.Ordinal);
        var accounts = await db.Accounts.Where(a => a.ChartId == chartId).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        var ids = accounts.Values.Select(static a => a.Id).ToList();
        var existing = await db.Mappings.Where(m => ids.Contains(m.AccountId) && m.StatutoryChartCode == statutory.Code).ToDictionaryAsync(static m => m.AccountId, cancellationToken);
        foreach (var request in requests)
        {
            if (!accounts.TryGetValue(request.AccountCode?.Trim() ?? string.Empty, out var account))
            {
                return Error.NotFound("account", request.AccountCode ?? string.Empty);
            }

            if (account.IsHeader)
            {
                return Error.Validation("mapping.header", $"'{account.Code}' is a header; map postable accounts.").WithWhy(("code", account.Code));
            }

            var code = request.StatutoryCode?.Trim() ?? string.Empty;
            if (code.Length == 0)
            {
                if (existing.Remove(account.Id, out var removed))
                {
                    db.Mappings.Remove(removed);
                }

                continue;
            }

            if (!valid.Contains(code))
            {
                return Error.Validation("mapping.statutory_code_unknown", $"'{code}' is not an account of {statutory.Code}.").WithWhy(("code", account.Code), ("statutoryCode", code));
            }

            if (existing.TryGetValue(account.Id, out var mapping))
            {
                mapping.StatutoryCode = code;
            }
            else
            {
                mapping = new AccountMapping { AccountId = account.Id, StatutoryChartCode = statutory.Code, StatutoryCode = code };
                db.Mappings.Add(mapping);
                existing[account.Id] = mapping;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("gl_chart", chart.Id, chart.Code, "mappings_changed", After: new { statutory = statutory.Code, changed = requests.Count }), cancellationToken);
        return await ListMappingsAsync(chartId, statutory.Code, cancellationToken);
    }

    // ------------------------------------------------------------------ companies

    public async Task<Result<CompanyAccountingSettings>> CompanySettingsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var chartCode = company.ChartId is { } chartId ? await db.Charts.Where(c => c.Id == chartId).Select(static c => c.Code).SingleOrDefaultAsync(cancellationToken) : null;
        return new CompanyAccountingSettings(companyId, company.Code, company.ChartId, chartCode, null);
    }

    public async Task<Result<CompanyAccountingSettings>> AssignToCompanyAsync(Guid companyId, Guid? chartId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        if (chartId is { } id)
        {
            var chart = await db.Charts.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (chart is null)
            {
                return Error.NotFound("chart", id);
            }

            if (!chart.IsActive)
            {
                return Error.Conflict("chart.inactive", $"Chart '{chart.Code}' is inactive.");
            }

            if (chart.CompanyId is { } owner && owner != companyId)
            {
                return Error.Conflict("chart.other_company", $"Chart '{chart.Code}' is dedicated to another company.").WithWhy(("chartCompanyId", owner));
            }
        }

        var assigned = await companies.AssignChartAsync(new CompanyId(companyId), chartId, cancellationToken);
        return assigned.IsFailure ? assigned.Error! : await CompanySettingsAsync(companyId, cancellationToken);
    }

    // ------------------------------------------------------------------ IChartOfAccounts

    public async Task<Guid?> ChartForCompanyAsync(CompanyId companyId, CancellationToken cancellationToken = default) =>
        (await companies.FindAsync(companyId, cancellationToken))?.ChartId;

    public async Task<AccountInfo?> FindAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        return account is null ? null : Info(account);
    }

    public async Task<AccountInfo?> FindAccountByCodeAsync(Guid chartId, string code, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.SingleOrDefaultAsync(a => a.ChartId == chartId && a.Code == code, cancellationToken);
        return account is null ? null : Info(account);
    }

    public async Task<IReadOnlyList<AccountInfo>> AccountsForRoleAsync(Guid chartId, string role, CancellationToken cancellationToken = default) =>
        (await db.Accounts.Where(a => a.ChartId == chartId && a.DefaultRole == role && a.IsActive).OrderBy(static a => a.Code).ToListAsync(cancellationToken)).Select(Info).ToList();

    public async Task<Result<LineCheck>> CheckLineAsync(Guid accountId, CompanyId companyId, IReadOnlyDictionary<string, Guid>? dimensions, string? currency, bool manual, CancellationToken cancellationToken = default)
    {
        var account = await db.Accounts.Include(static a => a.DimensionRules).SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);
        if (account is null)
        {
            return Error.NotFound("account", accountId);
        }

        var company = await companies.FindAsync(companyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId.Value);
        }

        if (company.ChartId is null)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.").WithWhy(("companyId", companyId.Value));
        }

        if (company.ChartId != account.ChartId)
        {
            return Error.Validation("account.chart_mismatch", $"Account '{account.Code}' is not on the chart of company '{company.Code}'.").WithWhy(("account", account.Code), ("chartId", account.ChartId), ("companyChartId", company.ChartId));
        }

        if (!account.IsActive)
        {
            return Error.Conflict("account.inactive", $"Account '{account.Code}' is inactive.").WithWhy(("account", account.Code));
        }

        if (account.IsHeader)
        {
            return Error.Validation("account.header", $"'{account.Code}' is a header account; post to one of its accounts.").WithWhy(("account", account.Code));
        }

        if (account.CompanyId is { } owner && owner != companyId.Value)
        {
            return Error.Validation("account.company_mismatch", $"Account '{account.Code}' is reserved for another company.").WithWhy(("account", account.Code), ("accountCompanyId", owner));
        }

        if (manual && !account.AllowManualPosting)
        {
            return Error.Validation("account.manual_posting_blocked", $"Account '{account.Code}' takes postings from documents only.").WithWhy(("account", account.Code), ("subledgerType", account.SubledgerType));
        }

        if (account.CurrencyRestriction is { } restricted && currency is not null && !string.Equals(restricted, currency, StringComparison.Ordinal))
        {
            return Error.Validation("account.currency_restricted", $"Account '{account.Code}' takes {restricted} only.").WithWhy(("account", account.Code), ("currency", currency), ("restrictedTo", restricted));
        }

        var known = (await dimensionDirectory.ListAsync(cancellationToken)).ToDictionary(static d => d.Id, static d => d.Code);
        var effective = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, valueId) in dimensions ?? new Dictionary<string, Guid>(StringComparer.Ordinal))
        {
            effective[code.Trim().ToUpperInvariant()] = valueId;
        }

        foreach (var rule in account.DimensionRules)
        {
            var code = known.GetValueOrDefault(rule.DimensionId, rule.DimensionId.ToString());
            var present = effective.ContainsKey(code);
            switch (rule.Rule)
            {
                case DimensionRules.Blocked when present:
                    return Error.Validation("account.dimension_blocked", $"Account '{account.Code}' does not take dimension {code}.").WithWhy(("account", account.Code), ("dimension", code));
                case DimensionRules.Required when !present && rule.DefaultValueId is null:
                    return Error.Validation("account.dimension_required", $"Account '{account.Code}' requires dimension {code} on every line.").WithWhy(("account", account.Code), ("dimension", code));
                case DimensionRules.Required or DimensionRules.Optional when !present && rule.DefaultValueId is { } fallback:
                    effective[code] = fallback;
                    break;
                default:
                    break;
            }
        }

        return new LineCheck(Info(account), effective);
    }

    // ------------------------------------------------------------------ mapping

    private static ChartSummary Map(Chart c, int accountCount) => new(c.Id, c.Code, c.Name.Values, c.TemplateCode, c.AccountCodeFormat, c.CompanyId, c.IsActive, accountCount, c.UpdatedAt);

    private static AccountInfo Info(Account a) => new(a.Id, a.ChartId, a.Code, a.Name, a.Type, a.Subtype, a.IsHeader, a.IsControl, a.SubledgerType, a.CurrencyRestriction, a.AllowManualPosting, a.RevalueFx, a.DefaultRole, a.CompanyId, a.IsActive);

    /// <summary>Flat list in code order with the level and the code path of every account.</summary>
    private static List<AccountSummary> Tree(List<Account> accounts, Dictionary<Guid, string> categories)
    {
        var byId = accounts.ToDictionary(static a => a.Id);
        var paths = new Dictionary<Guid, (int Level, string Path)>();
        (int Level, string Path) PathOf(Account a)
        {
            if (paths.TryGetValue(a.Id, out var known))
            {
                return known;
            }

            var result = a.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent)
                ? (PathOf(parent).Level + 1, PathOf(parent).Path + "/" + a.Code)
                : (0, a.Code);
            paths[a.Id] = result;
            return result;
        }

        return accounts.Select(a =>
        {
            var (level, path) = PathOf(a);
            return new AccountSummary(a.Id, a.ChartId, a.ParentId, a.ParentId is { } p && byId.TryGetValue(p, out var parent) ? parent.Code : null, a.Code, a.Name.Values, a.Type, a.Subtype,
                a.CategoryId is { } c ? categories.GetValueOrDefault(c) : null, a.IsHeader, a.IsControl, a.SubledgerType, a.CurrencyRestriction, a.AllowManualPosting, a.RevalueFx,
                a.CashFlowCategory, a.DefaultRole, a.CompanyId, a.IsActive, level, path, a.UpdatedAt);
        }).ToList();
    }
}
