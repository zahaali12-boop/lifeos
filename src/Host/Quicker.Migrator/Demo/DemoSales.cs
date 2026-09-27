using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Items.Application;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Persistence;
using Quicker.Sales.Application;

namespace Quicker.Migrator.Demo;

/// <summary>What the sales quotations and orders seed produced.</summary>
public sealed record DemoSalesOutcome(int Quotations, int Orders);

/// <summary>
/// Demo seed for roadmap 5.4: four quotations for Iraq Trading's own customers (two of Baghdad Mall's still waiting on
/// a decision, one draft and one sent; Kurdistan Distribution's accepted and converted to a confirmed order; Al-Noor's
/// rejected on price), and a direct order for Basra Oil mixing a normally stocked line with a drop-ship line — so the
/// order screen's own "create purchase order" button has something to click the first time anyone opens the demo.
/// Every line is priced and taxed by the real pricing and tax engines already seeded (<see cref="DemoPricing"/>,
/// <see cref="DemoTax"/>), not given a price here.
/// </summary>
internal static class DemoSales
{
    private sealed record Catalogue(Guid Id, string Code);

    public static async Task<DemoSalesOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        var tenant = unitOfWork.Context.TenantId.Value;
        var quotations = services.GetRequiredService<QuotationService>();
        var orders = services.GetRequiredService<OrderService>();
        var categories = services.GetRequiredService<CategoryService>();
        var iqt = companies.Single(c => c.Definition.Code == "IQT").Company.Id;
        Guid Customer(string key) => DemoBooks.Customers.Single(c => c.Key == key).Ref;

        // Classify the categories these lines sell from as standard-rated, the way an admin going live with tax would:
        // an item with neither its own tax group nor its category's is refused by tax determination (`tax.item_unclassified`).
        var standardItemTaxGroup = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM app.tax_groups WHERE tenant_id = @tenant AND kind = 'item' AND code = 'STANDARD'", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        foreach (var code in new[] { "BEV", "ACC", "STAT" })
        {
            var categoryId = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "SELECT id FROM app.itm_item_categories WHERE tenant_id = @tenant AND code = @code", new { tenant, code }, unitOfWork.Transaction, cancellationToken: cancellationToken));
            var category = await categories.GetAsync(categoryId, cancellationToken) ?? throw new InvalidOperationException($"Demo seed failed: category {code} not found.");
            Require(await categories.UpdateAsync(categoryId, new SaveItemCategoryRequest(category.Code, category.Name, category.ParentId, null, category.CostingMethodOverride, category.ItemPostingGroupId, standardItemTaxGroup, category.IsActive), cancellationToken));
        }

        async Task<IReadOnlyList<Catalogue>> ItemsAsync(string family, int count) =>
            (await unitOfWork.Connection.QueryAsync<Catalogue>(new CommandDefinition("""
                SELECT id AS Id, code AS Code
                FROM app.itm_items
                WHERE tenant_id = @tenant AND code LIKE @prefix AND list_price IS NOT NULL AND NOT has_variants
                ORDER BY code
                LIMIT @count
                """, new { tenant, prefix = family + "-%", count }, unitOfWork.Transaction, cancellationToken: cancellationToken))).ToList();

        var warehouse = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM app.inv_warehouses WHERE tenant_id = @tenant AND code = 'BSR-WH'", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        var beverages = await ItemsAsync("BEV", 4);
        var accessories = await ItemsAsync("ACC", 1);

        SaveQuotationLineRequest Line(Catalogue item, decimal quantity) => new(ItemCode: item.Code, Quantity: quantity);

        // Baghdad Mall: quoted, still open — the demo shows a quotation waiting on a decision, not only finished ones.
        Require(await quotations.CreateAsync(new SaveQuotationRequest(iqt, Customer("cust:baghdad-mall"),
            [Line(beverages[0], 30m), Line(beverages[1], 20m)], QuoteDate: today, ValidUntil: today.AddDays(21)), cancellationToken));
        var sent1 = Require(await quotations.CreateAsync(new SaveQuotationRequest(iqt, Customer("cust:baghdad-mall"),
            [Line(beverages[0], 30m), Line(beverages[1], 20m)], QuoteDate: today, ValidUntil: today.AddDays(21)), cancellationToken));
        Require(await quotations.SendAsync(sent1.Id, cancellationToken));

        // Al-Noor: sent, then rejected on price — the demo shows the other side of a quotation's life too.
        var rejected = Require(await quotations.CreateAsync(new SaveQuotationRequest(iqt, Customer("cust:al-noor"),
            [Line(beverages[2], 25m)], QuoteDate: today, ValidUntil: today.AddDays(14)), cancellationToken));
        Require(await quotations.SendAsync(rejected.Id, cancellationToken));
        Require(await quotations.RejectAsync(rejected.Id, new RejectQuotationRequest("Asked for a lower price than the standard list allows"), cancellationToken));

        // Kurdistan Distribution: accepted and converted, the order confirmed — the full quote-to-order path, once through.
        var accepted = Require(await quotations.CreateAsync(new SaveQuotationRequest(iqt, Customer("cust:kurdistan-dist"),
            [Line(beverages[0], 50m), Line(beverages[3], 40m)], QuoteDate: today, ValidUntil: today.AddDays(21)), cancellationToken));
        Require(await quotations.SendAsync(accepted.Id, cancellationToken));
        Require(await quotations.AcceptAsync(accepted.Id, cancellationToken));
        var converted = Require(await orders.ConvertAsync(accepted.Id, new ConvertQuotationRequest(warehouse), cancellationToken));
        Require(await orders.ConfirmAsync(converted.Id, cancellationToken));

        // Basra Oil: a normally stocked line beside a drop-ship one, confirmed but not yet linked to a purchase order —
        // the order screen's own "create purchase order" button has something to click the first time anyone looks.
        var mixed = Require(await orders.CreateAsync(new SaveOrderRequest(iqt, Customer("cust:basra-oil"), warehouse,
            [new SaveOrderLineRequest(ItemCode: beverages[1].Code, Quantity: 15m), new SaveOrderLineRequest(ItemCode: accessories[0].Code, Quantity: 5m, DropShip: true)], OrderDate: today), cancellationToken));
        Require(await orders.ConfirmAsync(mixed.Id, cancellationToken));

        return new DemoSalesOutcome(Quotations: 4, Orders: 2);
    }

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
