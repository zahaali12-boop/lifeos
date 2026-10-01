using System.Text.Json;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;

namespace Quicker.Sales.Application;

/// <summary>Line and currency resolution shared by the sales documents (mirrors Quicker.Purchasing's own).</summary>
internal static class Shared
{
    /// <summary>A line's dimensions given by value, as the set the line keeps (none when it names none).</summary>
    public static async Task<Result<Guid?>> DimensionSetAsync(IDimensionSets dimensionSets, IReadOnlyDictionary<string, Guid>? dimensions, int lineNo, CancellationToken cancellationToken)
    {
        if (dimensions is not { Count: > 0 })
        {
            return (Guid?)null;
        }

        var set = await dimensionSets.GetOrCreateAsync(dimensions, cancellationToken);
        return set.IsFailure ? set.Error!.WithWhy(("lineNo", lineNo)) : set.Value;
    }

    public static async Task<Result<(ItemInfo Item, ItemUomInfo Unit, decimal QuantityBase)>> ResolveLineAsync(IItemDirectory items, string prefix, Guid? itemId, string? itemCode, decimal quantity, string? uom, Guid? uomId, CancellationToken cancellationToken)
    {
        var code = itemCode?.Trim();
        var item = itemId is { } id ? await items.FindAsync(id, cancellationToken) : code is null ? null : await items.FindByCodeAsync(code, cancellationToken);
        if (item is null)
        {
            return Error.Validation($"{prefix}.item_unknown", "The item does not exist.").WithWhy(("item", itemCode ?? itemId?.ToString()));
        }

        if (!item.IsActive)
        {
            return Error.Validation($"{prefix}.item_inactive", "The item is inactive.").WithWhy(("item", item.Code));
        }

        if (quantity <= 0m)
        {
            return Error.Validation($"{prefix}.quantity_invalid", "Quantities are positive.").WithWhy(("item", item.Code));
        }

        var units = await items.UomsAsync(item.Id, cancellationToken);
        ItemUomInfo? unit;
        if (uomId is null && string.IsNullOrWhiteSpace(uom))
        {
            unit = units.FirstOrDefault(u => u.UomId == item.SalesUomId) ?? units.First(static u => u.IsBase);
        }
        else
        {
            unit = uomId is { } id2 ? units.FirstOrDefault(u => u.UomId == id2) : units.FirstOrDefault(u => string.Equals(u.UomCode, uom, StringComparison.OrdinalIgnoreCase));
        }

        if (unit is null)
        {
            return Error.Validation($"{prefix}.uom_not_item_unit", "The unit is not one of the item's units.").WithWhy(("item", item.Code), ("uom", uom ?? uomId?.ToString()));
        }

        var toBase = await items.ToBaseAsync(item.Id, unit.UomId, quantity, cancellationToken);
        return toBase.IsFailure ? toBase.Error! : (item, unit, toBase.Value.Quantity);
    }

    public static async Task<Result<Currency>> CurrencyAsync(ICompanyDirectory companies, string? code, string fallback, string prefix, CancellationToken cancellationToken)
    {
        var iso = (string.IsNullOrWhiteSpace(code) ? fallback : code).Trim().ToUpperInvariant();
        var currency = await companies.FindCurrencyAsync(iso, cancellationToken);
        return currency is null ? Error.Validation($"{prefix}.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", code)) : currency.Value;
    }

    public static decimal Round(decimal amount, Currency currency) => RoundingPolicy.Default.Round(amount, currency.MinorUnits);

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
