using System.Text.Json;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>What every purchasing document shares: resolving an item line in one of the item's units, rounding money in the document's currency, JSON.</summary>
internal static class Shared
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static readonly IReadOnlyList<string> RequisitionStatuses = ["draft", "pending_approval", "approved", "ordered", "rejected", "cancelled"];

    public static readonly IReadOnlyList<string> OrderStatuses = ["draft", "pending_approval", "approved", "sent", "partially_received", "received", "closed", "cancelled", "rejected"];

    /// <summary>The item and the unit of a line; anything purchasable (stock, non-stock, service, kit, assembly) is allowed, the quantity is positive and exact in the base unit.</summary>
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
            unit = units.FirstOrDefault(u => u.UomId == item.PurchaseUomId) ?? units.First(static u => u.IsBase);
        }
        else
        {
            var wanted = uom?.Trim().ToUpperInvariant();
            unit = units.FirstOrDefault(u => uomId is { } uid ? u.UomId == uid : u.UomCode == wanted);
            if (unit is null)
            {
                return Error.Validation($"{prefix}.uom_not_item_uom", "The unit must be one of the item's units.").WithWhy(("item", item.Code), ("uom", uom ?? uomId?.ToString()), ("itemUoms", units.Select(static u => u.UomCode)));
            }
        }

        var exact = await items.ToBaseAsync(item.Id, unit.UomId, quantity, cancellationToken);
        if (exact.IsFailure)
        {
            return exact.Error!;
        }

        return (item, unit, exact.Value.Quantity);
    }

    public static async Task<Result<Currency>> CurrencyAsync(ICompanyDirectory companies, string? code, string fallback, string prefix, CancellationToken cancellationToken)
    {
        var iso = (string.IsNullOrWhiteSpace(code) ? fallback : code).Trim().ToUpperInvariant();
        var currency = await companies.FindCurrencyAsync(iso, cancellationToken);
        return currency is null ? Error.Validation($"{prefix}.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", code)) : currency.Value;
    }

    public static decimal Round(decimal amount, Currency currency) => RoundingPolicy.Default.Round(amount, currency.MinorUnits);

    public static string JsonOrEmpty(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } v ? v.GetRawText() : "{}";

    public static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    public static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string PeriodKey(DateOnly date) => date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
}
