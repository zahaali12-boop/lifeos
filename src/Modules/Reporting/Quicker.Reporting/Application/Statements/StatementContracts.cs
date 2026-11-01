namespace Quicker.Reporting.Application.Statements;

/// <summary>Which accounts a row adds up: by type, subtype, category, posting role, a code range or named codes; exclusions narrow any of them. Empty means every account.</summary>
public sealed record AccountFilter(
    IReadOnlyList<string>? Types = null,
    IReadOnlyList<string>? Subtypes = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? Roles = null,
    string? CodeFrom = null,
    string? CodeTo = null,
    IReadOnlyList<string>? AccountCodes = null,
    IReadOnlyList<string>? ExcludeSubtypes = null,
    IReadOnlyList<string>? ExcludeCategories = null,
    IReadOnlyList<string>? ExcludeAccountCodes = null);

/// <summary>A row of a layout: a header, an account selection, a formula over other rows' codes, or a blank line.</summary>
public sealed record LayoutRowDefinition(
    string Code,
    string RowType,
    IReadOnlyDictionary<string, string> Label,
    AccountFilter? Accounts = null,
    IReadOnlyDictionary<string, string>? DimensionFilter = null,
    string? Formula = null,
    bool FlipSign = false,
    int Indent = 0,
    bool Bold = false,
    bool ShowAccounts = false,
    bool HideWhenZero = false);

/// <summary>What a column needs besides its type: the two columns a variance compares (<c>Of</c> minus <c>Against</c>), or the fixed dates of a custom range.</summary>
public sealed record ColumnParameters(string? Of = null, string? Against = null, DateOnly? From = null, DateOnly? To = null);

public sealed record LayoutColumnDefinition(
    string Code,
    string ColumnType,
    IReadOnlyDictionary<string, string> Label,
    ColumnParameters? Parameters = null,
    string CurrencyMode = "functional");

public sealed record LayoutDefinition(IReadOnlyList<LayoutRowDefinition> Rows, IReadOnlyList<LayoutColumnDefinition> Columns);

public sealed record SaveLayoutRequest(
    string? Code,
    string Kind,
    IReadOnlyDictionary<string, string> Name,
    LayoutDefinition Definition,
    IReadOnlyDictionary<string, string>? Description = null,
    Guid? CompanyId = null,
    bool IsPublished = true,
    string? TemplateCode = null,
    int SortOrder = 0);

/// <summary>A layout as listed: a shipped one has no id and <c>IsSystem</c>; a tenant's has an id and may be unpublished.</summary>
public sealed record LayoutSummary(
    Guid? Id,
    string Code,
    string Kind,
    IReadOnlyDictionary<string, string> Name,
    IReadOnlyDictionary<string, string> Description,
    Guid? CompanyId,
    string? TemplateCode,
    bool IsSystem,
    bool IsPublished,
    bool IsActive,
    int SortOrder,
    LayoutDefinition Definition,
    DateTimeOffset? UpdatedAt);

/// <summary>The choices a designer offers: the chart's account types, subtypes, categories and posting roles.</summary>
public sealed record LayoutOptions(
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Subtypes,
    IReadOnlyList<LayoutCategory> Categories,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> RowTypes,
    IReadOnlyList<string> ColumnTypes,
    IReadOnlyList<string> Kinds);

public sealed record LayoutCategory(string Code, IReadOnlyDictionary<string, string> Name, string Statement);

/// <summary>
/// Running a statement: the company, the layout (a tenant's by id or a shipped one by code), the period (its end
/// defaults to today in the company's time zone, its start to the fiscal period that holds the end), dimension
/// filters applied to every row, and whether closing entries count (a balance sheet includes them by default, the
/// profit and loss and the trial balance leave them out, as the ledger inquiry does).
/// </summary>
public sealed record StatementRequest(
    Guid CompanyId,
    Guid? LayoutId = null,
    string? LayoutCode = null,
    DateOnly? From = null,
    DateOnly? To = null,
    IReadOnlyDictionary<string, Guid>? Filters = null,
    bool? IncludeClosing = null);

public sealed record StatementColumn(string Code, string ColumnType, IReadOnlyDictionary<string, string> Label, DateOnly? From, DateOnly? To, string Currency, bool IsPercent);

public sealed record StatementAccountLine(Guid AccountId, string Code, IReadOnlyDictionary<string, string> Name, string Type, IReadOnlyList<decimal?> Values);

public sealed record StatementRow(string Code, string RowType, IReadOnlyDictionary<string, string> Label, int Indent, bool Bold, IReadOnlyList<decimal?> Values, IReadOnlyList<StatementAccountLine>? Accounts);

public sealed record StatementResult(
    Guid? LayoutId,
    string LayoutCode,
    string Kind,
    IReadOnlyDictionary<string, string> Name,
    Guid CompanyId,
    DateOnly From,
    DateOnly To,
    DateOnly FiscalYearStart,
    bool IncludeClosing,
    string Currency,
    IReadOnlyList<StatementColumn> Columns,
    IReadOnlyList<StatementRow> Rows,
    DateTimeOffset? ProjectedUpTo,
    int DurationMs);

public sealed record StatementDrillRequest(StatementRequest Request, string RowCode, string ColumnCode);

/// <summary>A designer's preview: the statement computed from a definition that is not saved yet.</summary>
public sealed record StatementDesignPreview(StatementRequest Request, string Kind, LayoutDefinition Definition);

/// <summary>The accounts behind one cell with the column's window, so each can open in the ledger over the same dates.</summary>
public sealed record StatementDrill(StatementColumn Column, StatementRow Row, IReadOnlyList<StatementAccountLine> Accounts);
