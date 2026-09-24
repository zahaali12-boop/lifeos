using Microsoft.EntityFrameworkCore;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Partners.Contracts;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>Replenishment suggestions to order; suggestions without a supplier take <paramref name="SupplierId"/>.</summary>
public sealed record OrderSuggestionsRequest(IReadOnlyList<Guid> SuggestionIds, Guid? SupplierId = null);

public sealed record SuggestionsOrdered(IReadOnlyList<PurchaseOrderSummary> Orders);

/// <summary>
/// Turns the planner's replenishment suggestions into draft purchase orders, as a buyer does from the replenishment
/// screen: one order per company, supplier and warehouse, each line in the item's base unit for the quantity decided
/// (or suggested), wanted by the date the suggestion needs it, at the price the supplier last charged for the item
/// (zero when it never did, for the buyer to fill in before submitting). Each suggestion records the order line it
/// became, so the planner counts the order as incoming once it is approved.
/// </summary>
public sealed class ReplenishmentOrderService(PurchasingDbContext db, PurchaseOrderService orders, IReplenishmentSuggestions suggestions, IItemDirectory items, IPartnerDirectory partners)
{
    public const int MaxSuggestions = 200;

    private static readonly string[] PricedStatuses = ["approved", "sent", "partially_received", "received", "closed"];

    public async Task<Result<SuggestionsOrdered>> OrderAsync(OrderSuggestionsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = (request.SuggestionIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > MaxSuggestions)
        {
            return Error.Validation("replenishment.selection_invalid", $"Choose between 1 and {MaxSuggestions} suggestions.").WithWhy(("count", ids.Count));
        }

        var found = (await suggestions.ForOrderingAsync(ids, cancellationToken)).ToDictionary(static s => s.Id);
        if (ids.FirstOrDefault(id => !found.ContainsKey(id)) is var missing && missing != Guid.Empty)
        {
            return Error.NotFound("replenishment_suggestion", missing);
        }

        var chosen = ids.Select(id => found[id]).ToList();
        if (chosen.FirstOrDefault(static s => s.Status is not ("open" or "accepted") || s.PurchaseOrderLineId is not null) is { } decided)
        {
            return Error.Conflict("replenishment.not_orderable", "The suggestion was dismissed, superseded or already ordered.").WithWhy(("suggestionId", decided.Id), ("status", decided.Status));
        }

        if (chosen.FirstOrDefault(s => s.SupplierId is null && request.SupplierId is null) is { } unsupplied)
        {
            var item = await items.FindAsync(unsupplied.ItemId, cancellationToken);
            return Error.Validation("replenishment.supplier_missing", "The item has no preferred supplier; choose the supplier to order it from.").WithWhy(("suggestionId", unsupplied.Id), ("item", item?.Code));
        }

        var created = new List<PurchaseOrderSummary>();
        foreach (var group in chosen.GroupBy(s => (s.CompanyId, SupplierId: s.SupplierId ?? request.SupplierId!.Value, s.WarehouseId)).OrderBy(static g => g.Key.CompanyId).ThenBy(static g => g.Key.SupplierId).ThenBy(static g => g.Key.WarehouseId))
        {
            var (companyId, supplierId, warehouseId) = group.Key;
            var currency = (await partners.FindSupplierAsync(companyId, supplierId, cancellationToken))?.Currency;
            var lines = new List<SavePurchaseOrderLineRequest>();
            foreach (var suggestion in group)
            {
                var baseUnit = (await items.UomsAsync(suggestion.ItemId, cancellationToken)).FirstOrDefault(static u => u.IsBase);
                var price = currency is null ? 0m : await LastBasePriceAsync(companyId, supplierId, suggestion.ItemId, currency, cancellationToken);
                lines.Add(new SavePurchaseOrderLineRequest(ItemId: suggestion.ItemId, Quantity: suggestion.Quantity, UomId: baseUnit?.UomId, UnitPrice: price, ExpectedDate: suggestion.NeededBy, WarehouseId: warehouseId));
            }

            var order = await orders.CreateAsync(new SavePurchaseOrderRequest(companyId, supplierId, lines, Currency: currency, ExpectedDate: group.Min(static s => s.NeededBy), WarehouseId: warehouseId, Notes: "From replenishment suggestions"), cancellationToken);
            if (order.IsFailure)
            {
                return order.Error!;
            }

            // Lines keep the order they were given in, one per suggestion.
            foreach (var (suggestion, line) in group.Zip(order.Value.Lines.OrderBy(static l => l.LineNo)))
            {
                var marked = await suggestions.MarkOrderedAsync(suggestion.Id, suggestion.Quantity, supplierId, line.Id, cancellationToken);
                if (marked.IsFailure)
                {
                    return marked.Error!;
                }
            }

            created.Add(order.Value);
        }

        return new SuggestionsOrdered(created);
    }

    /// <summary>What the supplier last charged per base unit for the item on an order in the same currency; zero when it never did.</summary>
    private async Task<decimal> LastBasePriceAsync(Guid companyId, Guid supplierId, Guid itemId, string currency, CancellationToken cancellationToken)
    {
        var last = await (from l in db.OrderLines.AsNoTracking()
                          join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                          where o.CompanyId == companyId && o.PartnerId == supplierId && o.Currency == currency && l.ItemId == itemId && PricedStatuses.Contains(o.Status) && l.QuantityBase > 0m
                          orderby o.OrderDate descending, o.Id descending, l.LineNo
                          select new { l.UnitPrice, l.DiscountPct, l.Quantity, l.QuantityBase }).FirstOrDefaultAsync(cancellationToken);
        return last is null ? 0m : RoundingPolicy.Default.Round(last.UnitPrice * (1m - (last.DiscountPct / 100m)) * last.Quantity / last.QuantityBase, 4);
    }
}
