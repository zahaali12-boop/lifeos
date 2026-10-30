using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Sales.Application;

namespace Quicker.Migrator.Demo;

/// <summary>What the returns seed produced.</summary>
public sealed record DemoReturnsOutcome(int Returns, int CreditNotes);

/// <summary>
/// Demo seed for roadmap 5.8: Baghdad Mall sends goods back from its posted shipment. Six units of the groceries line
/// come back near their date, received at the shipment's own cost, quarantined at inspection and credited on the
/// invoice that billed them; three cartons of the second beverage line come back crushed, received and scrapped at
/// the return's cost, then credited; and one more unit is requested back and waits for someone to authorise it. Every step goes through the real services, so the
/// books, the subledger and the invariants hold as they would in production.
/// </summary>
internal static class DemoReturns
{
    public static async Task<DemoReturnsOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var returns = services.GetRequiredService<ReturnService>();
        var iqt = companies.Single(c => c.Definition.Code == "IQT").Company.Id;
        var mall = DemoBooks.Customers.Single(static c => c.Key == "cust:baghdad-mall").Ref;

        // What Baghdad Mall can still send back: the three lines of its posted shipment, in the shipment's line order.
        var returnable = (await returns.ReturnableAsync(iqt, mall, null, cancellationToken)).OrderBy(static l => l.ShipmentNumber, StringComparer.Ordinal).ThenBy(static l => l.LineNo).ToList();
        if (returnable.Count < 3)
        {
            throw new InvalidOperationException("Demo seed failed: Baghdad Mall has no posted shipment lines left to return.");
        }

        var groceries = returnable.First(static l => l.ItemCode.StartsWith("GROC-", StringComparison.Ordinal));
        var beverages = returnable.Where(static l => l.ItemCode.StartsWith("BEV-", StringComparison.Ordinal)).ToList();

        // Six units of groceries near their date: received at the shipment's cost, quarantined, credited on the invoice at its price.
        var nearDate = Require(await returns.CreateAsync(new SaveSalesReturnRequest(iqt, mall,
            [new SaveSalesReturnLineRequest(groceries.ShipmentLineId, Math.Min(6m, groceries.Remaining), ReasonCode: "NEAR-DATE", Disposition: "quarantine")],
            RequestDate: today, Reason: "Six units within a week of their date on the shelf", CustomerReference: "BM-RMA-2026-14"), cancellationToken));
        Require(await returns.AuthoriseAsync(nearDate.Id, cancellationToken));
        var received = Require(await returns.ReceiveAsync(nearDate.Id, new ReceiveSalesReturnRequest(today), cancellationToken));
        Require(await returns.InspectAsync(nearDate.Id, new InspectSalesReturnRequest(received.Lines.Select(static l => new InspectSalesReturnLineRequest(l.Id, "quarantine", "Held for quality to check the dates")).ToList(), today), cancellationToken));
        var creditNotes = Require(await returns.CreditAsync(nearDate.Id, new CreditSalesReturnRequest(today), cancellationToken)).CreditNotes.Count;

        // Three cartons of the second beverage crushed in transit: received, scrapped at the return's cost, credited.
        var crushedLine = beverages[Math.Min(1, beverages.Count - 1)];
        var crushed = Require(await returns.CreateAsync(new SaveSalesReturnRequest(iqt, mall,
            [new SaveSalesReturnLineRequest(crushedLine.ShipmentLineId, Math.Min(3m, crushedLine.Remaining), ReasonCode: "CRUSHED", Reason: "Carton IPX-4471-0002 arrived crushed", Disposition: "scrap")],
            RequestDate: today, Reason: "Crushed in transit", CustomerReference: "BM-RMA-2026-15"), cancellationToken));
        Require(await returns.AuthoriseAsync(crushed.Id, cancellationToken));
        var crushedReceived = Require(await returns.ReceiveAsync(crushed.Id, new ReceiveSalesReturnRequest(today), cancellationToken));
        Require(await returns.InspectAsync(crushed.Id, new InspectSalesReturnRequest(crushedReceived.Lines.Select(static l => new InspectSalesReturnLineRequest(l.Id, "scrap", "Not fit for sale")).ToList(), today), cancellationToken));
        creditNotes += Require(await returns.CreditAsync(crushed.Id, new CreditSalesReturnRequest(today), cancellationToken)).CreditNotes.Count;

        // One unit of the first beverage the store no longer wants: requested, waiting for sales to authorise it.
        Require(await returns.CreateAsync(new SaveSalesReturnRequest(iqt, mall,
            [new SaveSalesReturnLineRequest(beverages[0].ShipmentLineId, 1m, ReasonCode: "CHANGED-MIND", Disposition: "restock")],
            RequestDate: today, Reason: "Over-ordered for the promotion"), cancellationToken));

        return new DemoReturnsOutcome(Returns: 3, CreditNotes: creditNotes);
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
