using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Reporting.Application.Facts;

namespace Quicker.Reporting.Application.Semantic;

/// <summary>
/// Runs, explains and drills queries over the semantic models (ADR-0034): resolves the model with the tenant's own
/// dimensions, checks the reader holds a permission of the model and that the company is in their scopes, refreshes
/// the facts when they are older than a minute, compiles, executes, and shapes the rows (dimension labels in both
/// languages, measures as numbers), adds comparison and calculated columns, and reports the scope that applied.
/// </summary>
public sealed class QueryService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, ICompanyDirectory companies, IDimensionDirectory dimensions, ProjectionService projections, IClock clock)
{
    public static readonly TimeSpan FreshnessWindow = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ReportCatalogueModel>> CatalogueAsync(CancellationToken cancellationToken)
    {
        var who = principal.Required;
        var dims = await DimensionFieldsAsync(cancellationToken);
        var result = new List<ReportCatalogueModel>();
        foreach (var model in Catalogue.Models)
        {
            var allowed = model.Permissions.Where(who.Has).ToList();
            if (allowed.Count == 0)
            {
                continue;
            }

            var resolved = Resolve(model, dims);
            result.Add(new ReportCatalogueModel(model.Code, model.Name.Values, model.Description.Values, model.DateColumn is not null, allowed, model.DocumentTypeSql is not null,
                resolved.Fields.Select(static f => new ReportCatalogueField(f.Key, f.Label.Values, f.Role, f.Type, f.Folder, f.Aggregation, f.Description?.Values)).ToList()));
        }

        return result;
    }

    public async Task<Result<QueryResult>> RunAsync(QueryDefinition query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var prepared = await PrepareAsync(query, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var (model, scope, visible, company) = prepared.Value;
        var watch = Stopwatch.StartNew();
        DateTimeOffset? projectedUpTo = null;
        if (FactOf(model) is { } fact)
        {
            await projections.EnsureFreshAsync(FreshnessWindow, cancellationToken);
            projectedUpTo = (await projections.StatusAsync(cancellationToken)).FirstOrDefault(s => s.Fact == fact)?.Watermark;
        }

        var compiled = QueryCompiler.Compile(model, query, scope);
        if (compiled.IsFailure)
        {
            return compiled.Error!;
        }

        var rows = await ExecuteAsync(compiled.Value, cancellationToken);
        var truncated = rows.Count > compiled.Value.Limit;
        if (truncated)
        {
            rows = rows.Take(compiled.Value.Limit).ToList();
        }

        var fields = new List<ResultField>();
        fields.AddRange(compiled.Value.Dimensions.Select(static d => new ResultField(d.Field.Key, d.Field.Label.Values, FieldRoles.Dimension, d.Field.Type, null, d.Field.Folder)));
        fields.AddRange(compiled.Value.Measures.Select(static m => new ResultField(m.Key, m.Field.Label.Values, FieldRoles.Measure, m.Field.Type, m.Aggregation, m.Field.Folder)));

        if (query.Comparison is Comparisons.PriorPeriod or Comparisons.PriorYear && scope.From is { } from && scope.To is { } to)
        {
            var (priorFrom, priorTo) = query.Comparison == Comparisons.PriorYear
                ? (from.AddYears(-1), to.AddYears(-1))
                : (from.AddDays(-(to.DayNumber - from.DayNumber + 1)), from.AddDays(-1));
            var priorParameters = new Dictionary<string, object?>(compiled.Value.Parameters, StringComparer.Ordinal) { ["from"] = priorFrom, ["to"] = priorTo };
            var prior = await ExecuteAsync(compiled.Value with { Parameters = priorParameters }, cancellationToken);
            var dimensionKeys = compiled.Value.Dimensions.Select(static d => d.Field.Key + "_key").ToList();
            string Tuple(IReadOnlyDictionary<string, object?> row) => string.Join("\u001f", dimensionKeys.Select(k => row.GetValueOrDefault(k)?.ToString() ?? string.Empty));
            var priorByKey = new Dictionary<string, Dictionary<string, object?>>(StringComparer.Ordinal);
            foreach (var row in prior)
            {
                priorByKey.TryAdd(Tuple(row), row);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var tuple = Tuple(row);
                seen.Add(tuple);
                var match = priorByKey.GetValueOrDefault(tuple);
                foreach (var m in compiled.Value.Measures)
                {
                    var current = row[m.Key] as decimal?;
                    var previous = match?[m.Key] as decimal?;
                    row[m.Key + "_prior"] = previous;
                    row[m.Key + "_change"] = current is not null && previous is not null ? current - previous : null;
                }
            }

            foreach (var (tuple, row) in priorByKey)
            {
                if (seen.Contains(tuple))
                {
                    continue;
                }

                var extra = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var d in compiled.Value.Dimensions)
                {
                    extra[d.Field.Key] = row[d.Field.Key];
                    extra[d.Field.Key + "_key"] = row[d.Field.Key + "_key"];
                }

                foreach (var m in compiled.Value.Measures)
                {
                    extra[m.Key] = null;
                    extra[m.Key + "_prior"] = row[m.Key];
                    extra[m.Key + "_change"] = null;
                }

                rows.Add(extra);
            }

            foreach (var m in compiled.Value.Measures)
            {
                fields.Add(new ResultField(m.Key + "_prior", Suffix(m.Field.Label, query.Comparison == Comparisons.PriorYear ? " (prior year)" : " (prior period)", query.Comparison == Comparisons.PriorYear ? " (السنة السابقة)" : " (الفترة السابقة)"), FieldRoles.Measure, m.Field.Type, m.Aggregation, m.Field.Folder, false, "prior"));
                fields.Add(new ResultField(m.Key + "_change", Suffix(m.Field.Label, " (change)", " (التغير)"), FieldRoles.Measure, m.Field.Type, m.Aggregation, m.Field.Folder, false, "change"));
            }
        }

        if (query.Calculated is { Count: > 0 } calculated)
        {
            var known = fields.Where(static f => f.Role == FieldRoles.Measure).Select(static f => f.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var calc in calculated)
            {
                if (string.IsNullOrWhiteSpace(calc.Key) || !calc.Key.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_') || !char.IsAsciiLetter(calc.Key[0]) || model.Field(calc.Key) is not null || known.Contains(calc.Key))
                {
                    return Error.Validation("report.calculated_key_invalid", "A calculated field has a key of letters, digits and underscores that no other field uses.").WithWhy(("key", calc.Key));
                }

                var parsed = Arithmetic.Parse(calc.Expression, known);
                if (parsed.IsFailure)
                {
                    return parsed.Error!;
                }

                foreach (var row in rows)
                {
                    row[calc.Key] = Arithmetic.Evaluate(parsed.Value, row);
                }

                fields.Add(new ResultField(calc.Key, calc.Label, FieldRoles.Measure, calc.Type, null, "calculated", true));
                known.Add(calc.Key);
            }
        }

        return new QueryResult(model.Spec.Code, query.CompanyId, company.FunctionalCurrency.Code, scope.From, scope.To, fields, rows, rows.Count, truncated, projectedUpTo, visible, (int)watch.ElapsedMilliseconds);
    }

    public async Task<Result<QueryExplanation>> ExplainAsync(QueryDefinition query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var prepared = await PrepareAsync(query, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var (model, scope, visible, _) = prepared.Value;
        var compiled = QueryCompiler.Compile(model, query, scope);
        return compiled.IsFailure ? compiled.Error! : new QueryExplanation(compiled.Value.Sql, compiled.Value.Parameters, QueryCompiler.WithValues(compiled.Value.Sql, compiled.Value.Parameters), visible, compiled.Value.Joins);
    }

    public async Task<Result<DrillResult>> DrillAsync(DrillRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = await PrepareAsync(request.Definition, cancellationToken);
        if (prepared.IsFailure)
        {
            return prepared.Error!;
        }

        var (model, scope, _, _) = prepared.Value;
        if (FactOf(model) is not null)
        {
            await projections.EnsureFreshAsync(FreshnessWindow, cancellationToken);
        }

        var compiled = QueryCompiler.CompileDrill(model, request.Definition, scope, request.Keys, request.Limit ?? 200);
        if (compiled.IsFailure)
        {
            return compiled.Error!;
        }

        var uow = unitOfWork.Current;
        var raw = (await uow.Connection.QueryAsync(new CommandDefinition(compiled.Value.Sql, new DynamicParameters(compiled.Value.Parameters), uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var truncated = raw.Count > compiled.Value.Limit;
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        foreach (IDictionary<string, object> row in raw.Take(compiled.Value.Limit))
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < compiled.Value.Columns.Count; i++)
            {
                var column = compiled.Value.Columns[i];
                var value = row["c" + i.ToString(CultureInfo.InvariantCulture)];
                map[column.Key] = column.LabelType == "i18n" ? Label(value, "i18n") : Plain(value);
            }

            if (compiled.Value.HasDocument)
            {
                map["document_type"] = row["document_type"]?.ToString();
                map["document_id"] = row["document_id"] is Guid id ? id.ToString() : null;
                map["document_number"] = row["document_number"]?.ToString();
            }

            rows.Add(map);
        }

        var columns = compiled.Value.Columns.Select(static c => new ResultField(c.Key, c.Label.Values, c.Type is FieldTypes.Money or FieldTypes.Quantity or FieldTypes.Number ? FieldRoles.Measure : FieldRoles.Dimension, c.Type)).ToList();
        if (compiled.Value.HasDocument)
        {
            columns.Add(new ResultField("document_type", LocalizedText.Bilingual("Document type", "نوع المستند").Values, FieldRoles.Dimension, FieldTypes.Text));
            columns.Add(new ResultField("document_number", LocalizedText.Bilingual("Document", "المستند").Values, FieldRoles.Dimension, FieldTypes.Text));
        }

        return new DrillResult(columns, rows, rows.Count, truncated);
    }

    private async Task<Result<(ResolvedModel Model, ScopeFilter Scope, QueryScope Visible, CompanyInfo Company)>> PrepareAsync(QueryDefinition query, CancellationToken cancellationToken)
    {
        var spec = Catalogue.Find(query.Model ?? string.Empty);
        if (spec is null)
        {
            return Error.Validation("report.model_unknown", $"'{query.Model}' is not a model of the catalogue.").WithWhy(("model", query.Model ?? string.Empty), ("models", Catalogue.Models.Select(static m => m.Code).ToList()));
        }

        var who = principal.Required;
        var allowed = spec.Permissions.Where(who.Has).ToList();
        if (allowed.Count == 0)
        {
            return Error.Forbidden("report.model_forbidden", $"Reading '{spec.Code}' needs {string.Join(" or ", spec.Permissions)}.").WithWhy(("model", spec.Code), ("permissions", spec.Permissions));
        }

        var company = await companies.FindAsync(new CompanyId(query.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", query.CompanyId);
        }

        var scopes = allowed.Select(p => who.ScopesFor(p) ?? RecordScopes.All).ToList();
        var merged = scopes.Any(static s => s.CompanyIds.Count == 0 && s.BranchIds.Count == 0 && s.WarehouseIds.Count == 0)
            ? RecordScopes.All
            : new RecordScopes(scopes.SelectMany(static s => s.CompanyIds).ToHashSet(), scopes.SelectMany(static s => s.BranchIds).ToHashSet(), scopes.SelectMany(static s => s.WarehouseIds).ToHashSet());
        if (!merged.AllowsCompany(query.CompanyId))
        {
            return Error.Forbidden("report.company_not_in_scope", "The company is outside the reader's scope for this model.").WithWhy(("company", query.CompanyId));
        }

        var sides = spec.SidePermissions is null ? [] : spec.SidePermissions.Where(kv => who.Has(kv.Value)).Select(static kv => kv.Key).ToList();
        DateOnly? from = null;
        DateOnly? to = null;
        if (spec.DateColumn is not null)
        {
            var today = clock.TodayIn(company.TimeZone);
            to = query.To ?? today;
            from = query.From ?? new DateOnly(to.Value.Year, 1, 1);
            if (from > to)
            {
                return Error.Validation("report.range_invalid", "The range starts after it ends.").WithWhy(("from", from), ("to", to));
            }
        }

        var dims = await DimensionFieldsAsync(cancellationToken);
        var scope = new ScopeFilter(query.CompanyId, merged.CompanyIds.ToList(), merged.BranchIds.ToList(), merged.WarehouseIds.ToList(), sides, from, to);
        var visible = new QueryScope(scope.Companies, scope.Branches, scope.Warehouses, sides);
        return (Resolve(spec, dims), scope, visible, company);
    }

    private async Task<IReadOnlyList<(FieldSpec Field, JoinSpec Join)>> DimensionFieldsAsync(CancellationToken cancellationToken) =>
        Catalogue.DimensionFields((await dimensions.ListAsync(cancellationToken)).Where(static d => d.IsActive).Select(static d => (d.Code, d.Name)).ToList());

    private static ResolvedModel Resolve(ModelSpec model, IReadOnlyList<(FieldSpec Field, JoinSpec Join)> dims)
    {
        if (!model.HasDimensionSet || dims.Count == 0)
        {
            return new ResolvedModel(model, model.Fields, model.Joins);
        }

        return new ResolvedModel(model, [.. model.Fields, .. dims.Select(static d => d.Field)], [.. model.Joins, Catalogue.DimensionSet, .. dims.Select(static d => d.Join)]);
    }

    private static string? FactOf(ResolvedModel model) => model.Spec.Source.StartsWith("reporting.fact_", StringComparison.Ordinal) ? model.Spec.Source["reporting.fact_".Length..] : null;

    private async Task<List<Dictionary<string, object?>>> ExecuteAsync(CompiledQuery compiled, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var raw = await uow.Connection.QueryAsync(new CommandDefinition(compiled.Sql, new DynamicParameters(compiled.Parameters), uow.Transaction, cancellationToken: cancellationToken));
        var rows = new List<Dictionary<string, object?>>();
        foreach (IDictionary<string, object> row in raw)
        {
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var d in compiled.Dimensions)
            {
                var key = row["d" + d.Index.ToString(CultureInfo.InvariantCulture) + "_key"];
                map[d.Field.Key + "_key"] = KeyText(key);
                map[d.Field.Key] = d.HasLabel ? Label(row["d" + d.Index.ToString(CultureInfo.InvariantCulture) + "_label"], d.Field.LabelType) : Plain(key);
            }

            foreach (var m in compiled.Measures)
            {
                map[m.Key] = Numeric(row["m" + m.Index.ToString(CultureInfo.InvariantCulture)]);
            }

            rows.Add(map);
        }

        return rows;
    }

    private static string? KeyText(object? value) => value switch
    {
        null => null,
        Guid g => g.ToString(),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime t => DateOnly.FromDateTime(t).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static object? Plain(object? value) => value switch
    {
        DateTime t when t.TimeOfDay == TimeSpan.Zero => DateOnly.FromDateTime(t),
        _ => value,
    };

    private static object? Label(object? value, string? labelType)
    {
        if (value is null)
        {
            return null;
        }

        if (labelType != "i18n")
        {
            return Plain(value);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(value.ToString() ?? "{}", Json);
        }
        catch (JsonException)
        {
            return value.ToString();
        }
    }

    private static decimal? Numeric(object? value) => value switch
    {
        null => null,
        decimal m => m,
        long l => l,
        int i => i,
        _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };

    private static IReadOnlyDictionary<string, string> Suffix(LocalizedText label, string en, string ar)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (language, text) in label.Values)
        {
            values[language] = text + (language == "ar" ? ar : en);
        }

        return values;
    }
}
