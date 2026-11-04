using System.Globalization;
using System.Text;
using System.Text.Json;
using Quicker.Kernel.Results;

namespace Quicker.Reporting.Application.Semantic;

/// <summary>A model with the tenant's own dimensions added: what the compiler resolves field keys against.</summary>
public sealed record ResolvedModel(ModelSpec Spec, IReadOnlyList<FieldSpec> Fields, IReadOnlyList<JoinSpec> Joins)
{
    public FieldSpec? Field(string key) => Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
}

/// <summary>The reader's reach, already checked: the company asked for, the companies, branches, warehouses and sides their scopes allow (empty = all), and the date range.</summary>
public sealed record ScopeFilter(Guid CompanyId, IReadOnlyList<Guid> Companies, IReadOnlyList<Guid> Branches, IReadOnlyList<Guid> Warehouses, IReadOnlyList<string> Sides, DateOnly? From, DateOnly? To);

public sealed record CompiledDimension(FieldSpec Field, int Index, bool HasLabel);

public sealed record CompiledMeasure(FieldSpec Field, int Index, string Aggregation, string Key);

public sealed record CompiledQuery(string Sql, Dictionary<string, object?> Parameters, IReadOnlyList<CompiledDimension> Dimensions, IReadOnlyList<CompiledMeasure> Measures, IReadOnlyList<string> Joins, int Limit);

public sealed record CompiledDrill(string Sql, Dictionary<string, object?> Parameters, IReadOnlyList<DrillColumn> Columns, bool HasDocument, int Limit);

/// <summary>
/// Turns a query definition into one parameterised GROUP BY statement over the model's fact (ADR-0034 §3). Field keys
/// are resolved against the catalogue, operators and aggregations against fixed lists, values are parameters typed by
/// the field; nothing from the definition reaches the SQL as text. The tenant comes from row-level security; the
/// company and the reader's scopes are always in the WHERE clause.
/// </summary>
public static class QueryCompiler
{
    public const int MaxRows = 5000;
    public const int MaxDrillRows = 1000;

    private static readonly IReadOnlySet<string> Operators = new HashSet<string>(StringComparer.Ordinal) { "eq", "ne", "in", "not_in", "gt", "gte", "lt", "lte", "between", "contains", "starts_with", "is_null", "not_null" };

    public static Result<CompiledQuery> Compile(ResolvedModel model, QueryDefinition query, ScopeFilter scope)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        var dimensions = new List<CompiledDimension>();
        foreach (var key in (query.Rows ?? []).Concat(query.Columns ?? []).Distinct(StringComparer.Ordinal))
        {
            var field = model.Field(key);
            if (field is null || field.Role != FieldRoles.Dimension)
            {
                return Error.Validation("report.field_unknown", $"'{key}' is not a dimension of model '{model.Spec.Code}'.").WithWhy(("field", key), ("model", model.Spec.Code));
            }

            dimensions.Add(new CompiledDimension(field, dimensions.Count, field.LabelSql is not null));
        }

        var measures = new List<CompiledMeasure>();
        foreach (var value in query.Values ?? [])
        {
            var field = model.Field(value.Field);
            if (field is null || field.Role != FieldRoles.Measure)
            {
                return Error.Validation("report.field_unknown", $"'{value.Field}' is not a measure of model '{model.Spec.Code}'.").WithWhy(("field", value.Field), ("model", model.Spec.Code));
            }

            var aggregation = value.Aggregation ?? field.Aggregation ?? Aggregations.Sum;
            if (!Aggregations.All.Contains(aggregation, StringComparer.Ordinal) || (field.Sql == "*" && aggregation != Aggregations.Count))
            {
                return Error.Validation("report.aggregation_invalid", $"'{aggregation}' is not an aggregation for '{value.Field}'.").WithWhy(("aggregation", aggregation), ("allowed", Aggregations.All));
            }

            var key = aggregation == (field.Aggregation ?? Aggregations.Sum) || measures.All(m => m.Key != field.Key) ? field.Key : field.Key + "_" + aggregation;
            if (measures.Any(m => m.Key == key))
            {
                continue;
            }

            measures.Add(new CompiledMeasure(field, measures.Count, aggregation, key));
        }

        if (measures.Count == 0)
        {
            measures.Add(new CompiledMeasure(new FieldSpec("count", Kernel.Text.LocalizedText.Bilingual("Count", "العدد"), FieldRoles.Measure, FieldTypes.Number, "counts", "*", Aggregation: Aggregations.Count), 0, Aggregations.Count, "count"));
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var where = new List<string>();
        var joins = new HashSet<string>(StringComparer.Ordinal);
        BaseConditions(model, scope, where, parameters);
        var filtered = Filters(model, query.Filters, where, parameters, joins);
        if (filtered.IsFailure)
        {
            return filtered.Error!;
        }

        foreach (var d in dimensions)
        {
            AddJoins(d.Field.Join, joins);
        }

        foreach (var m in measures)
        {
            AddJoins(m.Field.Join, joins);
        }

        var select = new List<string>();
        var groupBy = new List<string>();
        foreach (var d in dimensions)
        {
            select.Add($"{d.Field.Sql} AS d{d.Index}_key");
            groupBy.Add(select.Count.ToString(CultureInfo.InvariantCulture));
            if (d.HasLabel)
            {
                select.Add($"{d.Field.LabelSql} AS d{d.Index}_label");
                groupBy.Add(select.Count.ToString(CultureInfo.InvariantCulture));
            }
        }

        foreach (var m in measures)
        {
            select.Add($"{Aggregate(m)} AS m{m.Index}");
        }

        var orderBy = new List<string>();
        foreach (var sort in query.Sort ?? [])
        {
            var dim = dimensions.FirstOrDefault(d => d.Field.Key == sort.Field);
            var measure = measures.FirstOrDefault(m => m.Key == sort.Field || m.Field.Key == sort.Field);
            if (dim is null && measure is null)
            {
                return Error.Validation("report.sort_unknown", $"'{sort.Field}' is not in the query, so it cannot sort it.").WithWhy(("field", sort.Field));
            }

            var column = dim is not null ? (dim.HasLabel ? $"d{dim.Index}_label" : $"d{dim.Index}_key") : $"m{measure!.Index}";
            orderBy.Add(column + (sort.Descending ? " DESC NULLS LAST" : " ASC NULLS FIRST"));
        }

        if (orderBy.Count == 0)
        {
            if (query.TopN is > 0)
            {
                orderBy.Add("m0 DESC NULLS LAST");
            }
            else
            {
                orderBy.AddRange(dimensions.Select(d => $"d{d.Index}_key ASC NULLS FIRST"));
            }
        }

        var limit = Math.Clamp(query.TopN ?? MaxRows, 1, MaxRows);
        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(string.Join(", ", select)).Append('\n');
        sql.Append("FROM ").Append(model.Spec.Source).Append(" f").Append('\n');
        var ordered = OrderJoins(model, joins);
        foreach (var join in ordered)
        {
            sql.Append("LEFT JOIN ").Append(join.Table).Append(' ').Append(join.Alias).Append(" ON ").Append(join.On).Append('\n');
        }

        sql.Append("WHERE ").Append(string.Join(" AND ", where)).Append('\n');
        if (groupBy.Count > 0)
        {
            sql.Append("GROUP BY ").Append(string.Join(", ", groupBy)).Append('\n');
        }

        if (orderBy.Count > 0)
        {
            sql.Append("ORDER BY ").Append(string.Join(", ", orderBy)).Append('\n');
        }

        sql.Append("LIMIT ").Append((limit + 1).ToString(CultureInfo.InvariantCulture));
        return new CompiledQuery(sql.ToString(), parameters, dimensions, measures, ordered.Select(static j => j.Alias).ToList(), limit);
    }

    public static Result<CompiledDrill> CompileDrill(ResolvedModel model, QueryDefinition query, ScopeFilter scope, IReadOnlyDictionary<string, string?> keys, int limit)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(keys);
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        var where = new List<string>();
        var joins = new HashSet<string>(StringComparer.Ordinal);
        BaseConditions(model, scope, where, parameters);
        var filtered = Filters(model, query.Filters, where, parameters, joins);
        if (filtered.IsFailure)
        {
            return filtered.Error!;
        }

        var k = 0;
        foreach (var (key, value) in keys)
        {
            var field = model.Field(key);
            if (field is null || field.Role != FieldRoles.Dimension)
            {
                return Error.Validation("report.field_unknown", $"'{key}' is not a dimension of model '{model.Spec.Code}'.").WithWhy(("field", key));
            }

            AddJoins(field.Join, joins);
            if (value is null)
            {
                where.Add($"({field.Sql}) IS NULL");
                continue;
            }

            var typed = ParseScalar(field, value);
            if (typed.IsFailure)
            {
                return typed.Error!;
            }

            var name = "k" + k++.ToString(CultureInfo.InvariantCulture);
            parameters[name] = typed.Value;
            where.Add($"({field.Sql}) = @{name}");
        }

        var select = new List<string>();
        for (var i = 0; i < model.Spec.Drill.Count; i++)
        {
            var column = model.Spec.Drill[i];
            AddJoins(column.Join, joins);
            select.Add($"{column.Sql} AS c{i}");
        }

        var hasDocument = model.Spec.DocumentTypeSql is not null && model.Spec.DocumentIdSql is not null;
        if (hasDocument)
        {
            select.Add($"{model.Spec.DocumentTypeSql} AS document_type");
            select.Add($"{model.Spec.DocumentIdSql} AS document_id");
            select.Add($"{model.Spec.DocumentNumberSql ?? "NULL::text"} AS document_number");
        }

        var capped = Math.Clamp(limit, 1, MaxDrillRows);
        var sql = new StringBuilder();
        sql.Append("SELECT ").Append(string.Join(", ", select)).Append('\n');
        sql.Append("FROM ").Append(model.Spec.Source).Append(" f").Append('\n');
        foreach (var join in OrderJoins(model, joins))
        {
            sql.Append("LEFT JOIN ").Append(join.Table).Append(' ').Append(join.Alias).Append(" ON ").Append(join.On).Append('\n');
        }

        sql.Append("WHERE ").Append(string.Join(" AND ", where)).Append('\n');
        sql.Append("ORDER BY ").Append(model.Spec.DateColumn is { } date ? $"f.{date} DESC, c0" : "c0").Append('\n');
        sql.Append("LIMIT ").Append((capped + 1).ToString(CultureInfo.InvariantCulture));
        return new CompiledDrill(sql.ToString(), parameters, model.Spec.Drill, hasDocument, capped);
    }

    /// <summary>The statement with its parameter values written in, for a reader; never executed.</summary>
    public static string WithValues(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        var text = sql;
        foreach (var (name, value) in parameters.OrderByDescending(static p => p.Key.Length))
        {
            text = text.Replace("@" + name, Literal(value), StringComparison.Ordinal);
        }

        return text;
    }

    private static string Aggregate(CompiledMeasure m) => m.Aggregation switch
    {
        Aggregations.Count => m.Field.Sql == "*" ? "count(*)" : $"count({m.Field.Sql})",
        Aggregations.CountDistinct => $"count(DISTINCT {m.Field.Sql})",
        Aggregations.Avg => $"avg({m.Field.Sql})",
        Aggregations.Min => $"min({m.Field.Sql})",
        Aggregations.Max => $"max({m.Field.Sql})",
        _ => $"coalesce(sum({m.Field.Sql}), 0)",
    };

    private static void BaseConditions(ResolvedModel model, ScopeFilter scope, List<string> where, Dictionary<string, object?> parameters)
    {
        var spec = model.Spec;
        parameters["company"] = scope.CompanyId;
        where.Add($"f.{spec.CompanyColumn} = @company");
        if (scope.Companies.Count > 0)
        {
            parameters["scopeCompanies"] = scope.Companies.ToArray();
            where.Add($"f.{spec.CompanyColumn} = ANY(@scopeCompanies)");
        }

        if (spec.BranchColumn is not null && scope.Branches.Count > 0)
        {
            parameters["scopeBranches"] = scope.Branches.ToArray();
            where.Add($"f.{spec.BranchColumn} = ANY(@scopeBranches)");
        }

        if (spec.WarehouseColumn is not null && scope.Warehouses.Count > 0)
        {
            parameters["scopeWarehouses"] = scope.Warehouses.ToArray();
            where.Add($"f.{spec.WarehouseColumn} = ANY(@scopeWarehouses)");
        }

        if (spec.SideColumn is not null && scope.Sides.Count > 0)
        {
            parameters["sides"] = scope.Sides.ToArray();
            where.Add($"f.{spec.SideColumn} = ANY(@sides)");
        }

        if (spec.DateColumn is not null)
        {
            if (scope.From is { } from)
            {
                parameters["from"] = from;
                where.Add($"f.{spec.DateColumn} >= @from");
            }

            if (scope.To is { } to)
            {
                parameters["to"] = to;
                where.Add($"f.{spec.DateColumn} <= @to");
            }
        }
    }

    private static Result Filters(ResolvedModel model, IReadOnlyList<QueryFilter>? filters, List<string> where, Dictionary<string, object?> parameters, HashSet<string> joins)
    {
        var n = 0;
        foreach (var filter in filters ?? [])
        {
            var field = model.Field(filter.Field);
            if (field is null)
            {
                return Error.Validation("report.field_unknown", $"'{filter.Field}' is not a field of model '{model.Spec.Code}'.").WithWhy(("field", filter.Field));
            }

            if (field.Role != FieldRoles.Dimension)
            {
                return Error.Validation("report.filter_on_measure", $"'{filter.Field}' is a measure; filters apply to dimensions.").WithWhy(("field", filter.Field));
            }

            var op = filter.Op?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!Operators.Contains(op))
            {
                return Error.Validation("report.operator_invalid", $"'{filter.Op}' is not a filter operator.").WithWhy(("operator", filter.Op ?? string.Empty), ("allowed", Operators.Order(StringComparer.Ordinal).ToList()));
            }

            AddJoins(field.Join, joins);
            var expr = field.Sql;
            var name = "p" + n++.ToString(CultureInfo.InvariantCulture);
            switch (op)
            {
                case "is_null":
                    where.Add($"({expr}) IS NULL");
                    break;
                case "not_null":
                    where.Add($"({expr}) IS NOT NULL");
                    break;
                case "contains":
                case "starts_with":
                    {
                        var text = filter.Value is { ValueKind: JsonValueKind.String } v ? v.GetString() ?? string.Empty : filter.Value?.ToString() ?? string.Empty;
                        var target = field.LabelSql is not null ? $"coalesce(({field.LabelSql})::text, '')" : $"coalesce(({expr})::text, '')";
                        parameters[name] = text.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);
                        where.Add(op == "contains" ? $"{target} ILIKE '%' || @{name} || '%'" : $"{target} ILIKE @{name} || '%'");
                        break;
                    }

                case "in":
                case "not_in":
                    {
                        var list = ParseList(field, filter.Value);
                        if (list.IsFailure)
                        {
                            return list.Error!;
                        }

                        parameters[name] = list.Value;
                        where.Add(op == "in" ? $"({expr}) = ANY(@{name})" : $"NOT (({expr}) = ANY(@{name}))");
                        break;
                    }

                case "between":
                    {
                        var list = ParseList(field, filter.Value);
                        if (list.IsFailure)
                        {
                            return list.Error!;
                        }

                        if (list.Value.Length != 2)
                        {
                            return Error.Validation("report.between_needs_two", "'between' takes two values.").WithWhy(("field", filter.Field));
                        }

                        parameters[name + "a"] = list.Value.GetValue(0);
                        parameters[name + "b"] = list.Value.GetValue(1);
                        where.Add($"({expr}) BETWEEN @{name}a AND @{name}b");
                        break;
                    }

                default:
                    {
                        if (filter.Value is null || filter.Value.Value.ValueKind == JsonValueKind.Null)
                        {
                            return Error.Validation("report.filter_value_required", $"A value is required to filter '{filter.Field}'.").WithWhy(("field", filter.Field));
                        }

                        var scalar = ParseScalar(field, filter.Value.Value);
                        if (scalar.IsFailure)
                        {
                            return scalar.Error!;
                        }

                        parameters[name] = scalar.Value;
                        var symbol = op switch { "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", _ => "<=" };
                        where.Add(op == "ne" ? $"({expr}) IS DISTINCT FROM @{name}" : $"({expr}) {symbol} @{name}");
                        break;
                    }
            }
        }

        return Result.Success();
    }

    private static Result<Array> ParseList(FieldSpec field, JsonElement? value)
    {
        if (value is not { ValueKind: JsonValueKind.Array } array)
        {
            return Error.Validation("report.filter_list_required", $"A list of values is required to filter '{field.Key}'.").WithWhy(("field", field.Key));
        }

        var items = new List<object>();
        foreach (var element in array.EnumerateArray())
        {
            var parsed = ParseScalar(field, element);
            if (parsed.IsFailure)
            {
                return parsed.Error!;
            }

            items.Add(parsed.Value);
        }

        return field.Type switch
        {
            FieldTypes.Id => items.Cast<Guid>().ToArray(),
            FieldTypes.Number or FieldTypes.Money or FieldTypes.Quantity or FieldTypes.Percent => items.Cast<decimal>().ToArray(),
            FieldTypes.Date => items.Cast<DateOnly>().ToArray(),
            FieldTypes.Boolean => items.Cast<bool>().ToArray(),
            _ => items.Cast<string>().ToArray(),
        };
    }

    private static Result<object> ParseScalar(FieldSpec field, JsonElement element)
    {
        var text = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.ToString();
        return ParseScalar(field, text, element);
    }

    private static Result<object> ParseScalar(FieldSpec field, string text, JsonElement? element = null)
    {
        switch (field.Type)
        {
            case FieldTypes.Id:
                return Guid.TryParse(text, out var id) ? id : Invalid(field, text);
            case FieldTypes.Number:
            case FieldTypes.Money:
            case FieldTypes.Quantity:
            case FieldTypes.Percent:
                if (element is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var number))
                {
                    return number;
                }

                return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedNumber) ? parsedNumber : Invalid(field, text);
            case FieldTypes.Date:
                return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : Invalid(field, text);
            case FieldTypes.Boolean:
                if (element is { ValueKind: JsonValueKind.True })
                {
                    return true;
                }

                if (element is { ValueKind: JsonValueKind.False })
                {
                    return false;
                }

                return bool.TryParse(text, out var flag) ? flag : Invalid(field, text);
            default:
                return text;
        }
    }

    private static Error Invalid(FieldSpec field, string text) => Error.Validation("report.filter_value_invalid", $"'{text}' is not a {field.Type} for '{field.Key}'.").WithWhy(("field", field.Key), ("type", field.Type), ("value", text));

    private static void AddJoins(string? joins, HashSet<string> set)
    {
        if (string.IsNullOrWhiteSpace(joins))
        {
            return;
        }

        foreach (var alias in joins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(alias);
        }
    }

    /// <summary>The joins the query needs, each after the one it requires, in the model's order.</summary>
    private static List<JoinSpec> OrderJoins(ResolvedModel model, HashSet<string> wanted)
    {
        var byAlias = model.Joins.ToDictionary(static j => j.Alias, StringComparer.Ordinal);
        var needed = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(wanted);
        while (queue.Count > 0)
        {
            var alias = queue.Dequeue();
            if (!byAlias.TryGetValue(alias, out var join) || !needed.Add(alias))
            {
                continue;
            }

            if (join.Requires is not null)
            {
                queue.Enqueue(join.Requires);
            }
        }

        return model.Joins.Where(j => needed.Contains(j.Alias)).ToList();
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        Guid g => "'" + g.ToString() + "'::uuid",
        DateOnly d => "'" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'::date",
        DateTimeOffset t => "'" + t.ToString("O", CultureInfo.InvariantCulture) + "'::timestamptz",
        bool b => b ? "true" : "false",
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        Guid[] guids => "ARRAY[" + string.Join(", ", guids.Select(static g => "'" + g.ToString() + "'")) + "]::uuid[]",
        string[] strings => "ARRAY[" + string.Join(", ", strings.Select(static s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'")) + "]::text[]",
        decimal[] numbers => "ARRAY[" + string.Join(", ", numbers.Select(static n => n.ToString(CultureInfo.InvariantCulture))) + "]::numeric[]",
        DateOnly[] dates => "ARRAY[" + string.Join(", ", dates.Select(static d => "'" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'")) + "]::date[]",
        bool[] flags => "ARRAY[" + string.Join(", ", flags.Select(static b => b ? "true" : "false")) + "]",
        _ => "'" + value.ToString()?.Replace("'", "''", StringComparison.Ordinal) + "'",
    };
}
