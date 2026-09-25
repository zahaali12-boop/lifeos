using System.Text.Json;
using System.Text.Json.Serialization;

namespace Quicker.Tax.Application;

public sealed record TemplateText(string En, string Ar)
{
    public IReadOnlyDictionary<string, string> Values => new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = En, ["ar"] = Ar };
}

public sealed record TemplateGroup(string Code, TemplateText Name);

public sealed record TemplateRate(DateOnly From, decimal Pct);

public sealed record TemplateBoxes(string? SalesBase, string? SalesTax, string? PurchaseBase, string? PurchaseTax);

public sealed record TemplateCode(
    string Code,
    TemplateText Name,
    string Kind,
    string Treatment,
    IReadOnlyList<TemplateRate> Rates,
    bool ReverseCharge,
    bool Recoverable,
    string AppliesTo,
    TemplateBoxes Boxes,
    string? ExemptionReasonCode = null,
    TemplateText? ExemptionReason = null);

public sealed record TemplateRule(string Direction, string Code, string? ItemGroup = null, string? PartnerGroup = null);

/// <summary>A versioned country template (ADR-0018 "seeded templates"): the regime, its groups, codes with dated rates and return boxes, and the determination matrix.</summary>
public sealed record TaxTemplate(
    string Code,
    string Country,
    string Version,
    TemplateText Name,
    string Family,
    string RoundingLevel,
    string TaxPoint,
    string ReturnFrequency,
    string? EinvoicingScheme,
    IReadOnlyList<TemplateGroup> ItemGroups,
    IReadOnlyList<TemplateGroup> PartnerGroups,
    IReadOnlyList<TemplateCode> Codes,
    IReadOnlyList<TemplateRule> Rules);

/// <summary>The templates shipped with the product, read from the module's embedded data files (A-004).</summary>
public static class TemplateCatalog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    private static readonly Lazy<IReadOnlyList<TaxTemplate>> Loaded = new(Load);

    public static IReadOnlyList<TaxTemplate> All => Loaded.Value;

    public static TaxTemplate? Find(string code) => All.FirstOrDefault(t => string.Equals(t.Code, code, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<TaxTemplate> Load()
    {
        var assembly = typeof(TemplateCatalog).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(static n => n.StartsWith("Quicker.Tax.Templates.", StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)!;
                return JsonSerializer.Deserialize<TaxTemplate>(stream, Json) ?? throw new InvalidOperationException($"Tax template {name} is empty.");
            })
            .ToList();
    }
}
