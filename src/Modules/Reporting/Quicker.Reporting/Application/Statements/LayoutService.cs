using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Application.Semantic;
using Quicker.Reporting.Domain;
using Quicker.Reporting.Persistence;

namespace Quicker.Reporting.Application.Statements;

/// <summary>
/// The tenant's statement layouts (ADR-0034 §5): listed with the shipped ones, saved after every row and column is
/// checked (codes, types, formulas over known rows, variances over known columns, ranges), published to the
/// statements screen or kept in the designer, deleted. The shipped layouts are code and never change; a tenant
/// customises one by saving a copy.
/// </summary>
public sealed class LayoutService(ReportingDbContext db, IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, ICompanyDirectory companies, IAuditSink audit, IClock clock)
{
    public const string AuditEntity = "financial_layout";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A layout, row or column code: up to 32 upper-case letters, digits and underscores, starting with a letter.</summary>
    public static bool IsCode(string code) =>
        !string.IsNullOrEmpty(code) && code.Length <= 32 && code[0] is >= 'A' and <= 'Z' && code.All(static ch => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_');

    public async Task<IReadOnlyList<LayoutSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var canManage = who.Has(ReportingPermissions.StatementManage);
        var query = db.Layouts.AsNoTracking().Include(static l => l.Rows).Include(static l => l.Columns).Where(static l => l.IsActive);
        if (companyId is { } c)
        {
            query = query.Where(l => l.CompanyId == null || l.CompanyId == c);
        }

        if (!canManage)
        {
            query = query.Where(static l => l.IsPublished);
        }

        // The shipped statements first, in their order; then the tenant's own, in theirs.
        var own = (await query.ToListAsync(cancellationToken)).Select(Map).OrderBy(static l => l.SortOrder).ThenBy(static l => l.Code, StringComparer.Ordinal);
        return ShippedLayouts.All.OrderBy(static l => l.SortOrder).Concat(own).ToList();
    }

    public async Task<Result<LayoutSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var layout = await db.Layouts.AsNoTracking().Include(static l => l.Rows).Include(static l => l.Columns).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        return layout is null ? Error.NotFound(AuditEntity, id) : Map(layout);
    }

    /// <summary>What the designer may pick from for a company's chart: the types, the subtypes and categories in use, the posting roles.</summary>
    public async Task<Result<LayoutOptions>> OptionsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var uow = unitOfWork.Current;
        var subtypes = (await uow.Connection.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT subtype FROM app.gl_accounts WHERE chart_id = @chart AND subtype <> '' ORDER BY subtype", new { chart = company.ChartId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var roles = (await uow.Connection.QueryAsync<string>(new CommandDefinition("SELECT DISTINCT default_role FROM app.gl_accounts WHERE chart_id = @chart AND default_role IS NOT NULL ORDER BY default_role", new { chart = company.ChartId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var categories = (await uow.Connection.QueryAsync<(string Code, string Name, string Statement)>(new CommandDefinition("SELECT code, name_i18n::text, statement FROM app.gl_account_categories ORDER BY sort_order", transaction: uow.Transaction, cancellationToken: cancellationToken)))
            .Select(c => new LayoutCategory(c.Code, JsonSerializer.Deserialize<Dictionary<string, string>>(c.Name, Json) ?? new Dictionary<string, string>(StringComparer.Ordinal), c.Statement)).ToList();
        return new LayoutOptions(AccountTypes.All, subtypes, categories, roles, RowTypes.All, ColumnTypes.All, LayoutKinds.All);
    }

    public async Task<Result<LayoutSummary>> SaveAsync(Guid? id, SaveLayoutRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var checkedRequest = Validate(request);
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!;
        }

        var code = checkedRequest.Value;
        if (request.CompanyId is { } companyId && await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        FinancialLayout? layout = null;
        if (id is { } existing)
        {
            layout = await db.Layouts.Include(static l => l.Rows).Include(static l => l.Columns).SingleOrDefaultAsync(l => l.Id == existing, cancellationToken);
            if (layout is null)
            {
                return Error.NotFound(AuditEntity, existing);
            }
        }

        if (await db.Layouts.AnyAsync(l => l.Code == code && l.Id != (id ?? Guid.Empty), cancellationToken))
        {
            return Error.Conflict("layout.code_taken", $"A layout with code '{code}' exists.").WithWhy(("code", code));
        }

        var before = layout is null ? null : Snapshot(layout);
        var now = clock.UtcNow;
        layout ??= new FinancialLayout { Id = Guid.CreateVersion7(), CreatedAt = now };
        layout.Code = code;
        layout.Kind = request.Kind;
        layout.Name = Text(request.Name);
        layout.Description = Text(request.Description);
        layout.CompanyId = request.CompanyId;
        layout.TemplateCode = string.IsNullOrWhiteSpace(request.TemplateCode) ? null : request.TemplateCode.Trim().ToUpperInvariant();
        layout.IsPublished = request.IsPublished;
        layout.SortOrder = request.SortOrder;
        layout.UpdatedAt = now;
        db.LayoutRows.RemoveRange(layout.Rows);
        db.LayoutColumns.RemoveRange(layout.Columns);
        layout.Rows.Clear();
        layout.Columns.Clear();
        var order = 0;
        foreach (var row in request.Definition.Rows)
        {
            layout.Rows.Add(new LayoutRow
            {
                Id = Guid.CreateVersion7(),
                LayoutId = layout.Id,
                SortOrder = order++,
                Code = row.Code.Trim().ToUpperInvariant(),
                RowType = row.RowType,
                Label = Text(row.Label),
                AccountFilter = JsonSerializer.Serialize(row.Accounts ?? new AccountFilter(), Json),
                DimensionFilter = JsonSerializer.Serialize(row.DimensionFilter ?? new Dictionary<string, string>(StringComparer.Ordinal), Json),
                Formula = row.RowType == RowTypes.Formula ? row.Formula?.Trim() : null,
                FlipSign = row.FlipSign,
                Indent = Math.Clamp(row.Indent, 0, 6),
                Bold = row.Bold,
                ShowAccounts = row.ShowAccounts,
                HideWhenZero = row.HideWhenZero,
            });
        }

        order = 0;
        foreach (var column in request.Definition.Columns)
        {
            layout.Columns.Add(new LayoutColumn
            {
                Id = Guid.CreateVersion7(),
                LayoutId = layout.Id,
                SortOrder = order++,
                Code = column.Code.Trim().ToUpperInvariant(),
                ColumnType = column.ColumnType,
                Label = Text(column.Label),
                Parameters = JsonSerializer.Serialize(column.Parameters ?? new ColumnParameters(), Json),
                CurrencyMode = column.CurrencyMode,
            });
        }

        if (id is null)
        {
            db.Layouts.Add(layout);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, layout.Id, layout.Code, id is null ? AuditActions.Created : AuditActions.Updated, Before: before, After: Snapshot(layout), CompanyId: layout.CompanyId), cancellationToken);
        return Map(layout);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var layout = await db.Layouts.Include(static l => l.Rows).Include(static l => l.Columns).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (layout is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        db.LayoutRows.RemoveRange(layout.Rows);
        db.LayoutColumns.RemoveRange(layout.Columns);
        db.Layouts.Remove(layout);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, layout.Id, layout.Code, AuditActions.Deleted, Before: Snapshot(layout), CompanyId: layout.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Every rule a layout must meet before it is kept; returns the normalised code.</summary>
    internal static Result<string> Validate(SaveLayoutRequest request)
    {
        if (request.Name is null || request.Name.Values.All(string.IsNullOrWhiteSpace))
        {
            return Error.Validation("layout.name_required", "A name in at least one language is required.");
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (!IsCode(code))
        {
            return Error.Validation("layout.code_invalid", "A layout code is up to 32 upper-case letters, digits and underscores, starting with a letter.").WithWhy(("code", request.Code ?? string.Empty));
        }

        if (!LayoutKinds.All.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("layout.kind_invalid", "A layout is a profit and loss, a balance sheet, a trial balance, a cash flow or a management statement.").WithWhy(("kind", request.Kind), ("allowed", LayoutKinds.All));
        }

        if (request.Definition is null || request.Definition.Rows.Count == 0 || request.Definition.Columns.Count == 0)
        {
            return Error.Validation("layout.definition_required", "A layout has at least one row and one column.");
        }

        var rowCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in request.Definition.Rows)
        {
            var rowCode = (row.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (!IsCode(rowCode))
            {
                return Error.Validation("layout.row_code_invalid", "A row code is up to 32 upper-case letters, digits and underscores, starting with a letter.").WithWhy(("row", row.Code ?? string.Empty));
            }

            if (!rowCodes.Add(rowCode))
            {
                return Error.Validation("layout.row_code_duplicate", $"Row code '{rowCode}' is used twice.").WithWhy(("row", rowCode));
            }

            if (!RowTypes.All.Contains(row.RowType, StringComparer.Ordinal))
            {
                return Error.Validation("layout.row_type_invalid", "A row is a header, accounts, a formula or blank.").WithWhy(("row", rowCode), ("rowType", row.RowType));
            }

            if (row.RowType == RowTypes.Accounts && row.Accounts?.Types is { Count: > 0 } types && types.Any(t => !AccountTypes.All.Contains(t, StringComparer.OrdinalIgnoreCase)))
            {
                return Error.Validation("layout.account_type_invalid", "Account types are asset, liability, equity, revenue and expense.").WithWhy(("row", rowCode), ("types", types));
            }
        }

        foreach (var row in request.Definition.Rows.Where(static r => r.RowType == RowTypes.Formula))
        {
            var parsed = Arithmetic.Parse(row.Formula ?? string.Empty, rowCodes);
            if (parsed.IsFailure)
            {
                return Error.Validation("layout.formula_invalid", $"Row '{row.Code}': {parsed.Error!.Message}").WithWhy(("row", row.Code.Trim().ToUpperInvariant()), ("formula", row.Formula ?? string.Empty), ("rows", rowCodes.ToList()));
            }
        }

        var columnCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in request.Definition.Columns)
        {
            var columnCode = (column.Code ?? string.Empty).Trim().ToUpperInvariant();
            if (!IsCode(columnCode))
            {
                return Error.Validation("layout.column_code_invalid", "A column code is up to 32 upper-case letters, digits and underscores, starting with a letter.").WithWhy(("column", column.Code ?? string.Empty));
            }

            if (!columnCodes.Add(columnCode))
            {
                return Error.Validation("layout.column_code_duplicate", $"Column code '{columnCode}' is used twice.").WithWhy(("column", columnCode));
            }

            if (!ColumnTypes.All.Contains(column.ColumnType, StringComparer.Ordinal))
            {
                return Error.Validation("layout.column_type_invalid", "The column type is not one the engine knows.").WithWhy(("column", columnCode), ("columnType", column.ColumnType), ("allowed", ColumnTypes.All));
            }

            if (!CurrencyModes.All.Contains(column.CurrencyMode, StringComparer.Ordinal))
            {
                return Error.Validation("layout.currency_mode_invalid", "A column is in the functional or the reporting currency.").WithWhy(("column", columnCode), ("currencyMode", column.CurrencyMode));
            }

            if (column.ColumnType == ColumnTypes.CustomRange && !(column.Parameters is { From: { } f, To: { } t } && f <= t))
            {
                return Error.Validation("layout.column_range_required", $"Column '{columnCode}' is a fixed range and needs a from and to date in order.").WithWhy(("column", columnCode));
            }
        }

        var baseColumns = request.Definition.Columns.Where(static c => !ColumnTypes.IsDerived(c.ColumnType)).Select(static c => c.Code.Trim().ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
        foreach (var column in request.Definition.Columns.Where(static c => ColumnTypes.IsDerived(c.ColumnType)))
        {
            var of = (column.Parameters?.Of ?? string.Empty).Trim().ToUpperInvariant();
            var against = (column.Parameters?.Against ?? string.Empty).Trim().ToUpperInvariant();
            if (!baseColumns.Contains(of) || !baseColumns.Contains(against) || of == against)
            {
                return Error.Validation("layout.variance_reference_invalid", $"Column '{column.Code}' compares two other, different columns of the layout that are not variances themselves.").WithWhy(("column", column.Code.Trim().ToUpperInvariant()), ("columns", baseColumns.ToList()));
            }
        }

        return code;
    }

    public static LayoutSummary Map(FinancialLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var rows = layout.Rows.OrderBy(static r => r.SortOrder).Select(r => new LayoutRowDefinition(
            r.Code, r.RowType, r.Label.Values,
            JsonSerializer.Deserialize<AccountFilter>(r.AccountFilter, Json),
            JsonSerializer.Deserialize<Dictionary<string, string>>(r.DimensionFilter, Json),
            r.Formula, r.FlipSign, r.Indent, r.Bold, r.ShowAccounts, r.HideWhenZero)).ToList();
        var columns = layout.Columns.OrderBy(static c => c.SortOrder).Select(c => new LayoutColumnDefinition(
            c.Code, c.ColumnType, c.Label.Values, JsonSerializer.Deserialize<ColumnParameters>(c.Parameters, Json), c.CurrencyMode)).ToList();
        return new LayoutSummary(layout.Id, layout.Code, layout.Kind, layout.Name.Values, layout.Description.Values, layout.CompanyId, layout.TemplateCode, false, layout.IsPublished, layout.IsActive, layout.SortOrder, new LayoutDefinition(rows, columns), layout.UpdatedAt);
    }

    private static object Snapshot(FinancialLayout l) => new { l.Code, l.Kind, name = l.Name, l.CompanyId, l.IsPublished, rows = l.Rows.Count, columns = l.Columns.Count };

    private static LocalizedText Text(IReadOnlyDictionary<string, string>? values)
    {
        var text = new LocalizedText();
        foreach (var (language, value) in values ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                text = text.Set(language, value.Trim());
            }
        }

        return text;
    }
}
