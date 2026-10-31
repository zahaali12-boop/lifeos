using System.Text.Json;

namespace Quicker.Reporting.Application.Semantic;

/// <summary>A measure to aggregate; the aggregation defaults to the field's own.</summary>
public sealed record QueryValue(string Field, string? Aggregation = null);

/// <summary>A filter on a dimension: operators eq, ne, in, not_in, gt, gte, lt, lte, between, contains, starts_with, is_null, not_null; values typed by the field.</summary>
public sealed record QueryFilter(string Field, string Op, JsonElement? Value = null);

public sealed record QuerySort(string Field, bool Descending = false);

/// <summary>A field computed after aggregation from the measures of the row: arithmetic over their keys, such as <c>(net_amount - cost_amount) / net_amount * 100</c>.</summary>
public sealed record CalculatedField(string Key, IReadOnlyDictionary<string, string> Label, string Expression, string Type = FieldTypes.Number);

/// <summary>
/// A query over a semantic model (ADR-0034 §3): the dimensions on rows and columns, the measures, the filters, the
/// sort and top-N, calculated fields, an optional comparison (prior_period or prior_year), and the date range on
/// the model's date (the year to date when none is given). The company is always named; the reader's scopes apply.
/// </summary>
public sealed record QueryDefinition(
    string Model,
    Guid CompanyId,
    IReadOnlyList<string>? Rows = null,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<QueryValue>? Values = null,
    IReadOnlyList<QueryFilter>? Filters = null,
    IReadOnlyList<QuerySort>? Sort = null,
    int? TopN = null,
    IReadOnlyList<CalculatedField>? Calculated = null,
    string? Comparison = null,
    DateOnly? From = null,
    DateOnly? To = null);

public static class Comparisons
{
    public const string PriorPeriod = "prior_period";
    public const string PriorYear = "prior_year";
}

/// <summary>A column of a result: a dimension (its label column; the key travels as <c>&lt;key&gt;_key</c>), a measure, a calculated field, or a comparison column (<c>group</c> prior or change).</summary>
public sealed record ResultField(string Key, IReadOnlyDictionary<string, string> Label, string Role, string Type, string? Aggregation = null, string? Folder = null, bool IsCalculated = false, string? Group = null);

public sealed record QueryScope(IReadOnlyList<Guid> Companies, IReadOnlyList<Guid> Branches, IReadOnlyList<Guid> Warehouses, IReadOnlyList<string> Sides);

public sealed record QueryResult(
    string Model,
    Guid CompanyId,
    string Currency,
    DateOnly? From,
    DateOnly? To,
    IReadOnlyList<ResultField> Fields,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    int RowCount,
    bool Truncated,
    DateTimeOffset? ProjectedUpTo,
    QueryScope Scope,
    int DurationMs);

/// <summary>The compiled statement with its parameters, and the same statement with the values written in for reading.</summary>
public sealed record QueryExplanation(string Sql, IReadOnlyDictionary<string, object?> Parameters, string SqlWithValues, QueryScope Scope, IReadOnlyList<string> Joins);

/// <summary>The lines behind a cell: the query's filters plus the cell's dimension keys (a null key means the rows where that dimension is empty).</summary>
public sealed record DrillRequest(QueryDefinition Definition, IReadOnlyDictionary<string, string?> Keys, int? Limit = null);

public sealed record DrillResult(IReadOnlyList<ResultField> Columns, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows, int RowCount, bool Truncated);

public sealed record ReportCatalogueField(string Key, IReadOnlyDictionary<string, string> Label, string Role, string Type, string Folder, string? Aggregation, IReadOnlyDictionary<string, string>? Description);

public sealed record ReportCatalogueModel(string Code, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, string> Description, bool HasDate, IReadOnlyList<string> Permissions, bool CanDrill, IReadOnlyList<ReportCatalogueField> Fields);

public sealed record ProjectionRefreshRequest(bool Rebuild = false);
