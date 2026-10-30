using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Sales.Application;

namespace Quicker.Migrator.Demo;

/// <summary>What the invoicing seed produced.</summary>
public sealed record DemoInvoicingOutcome(int Invoices, int CreditNotes, int RecurringInvoices);

/// <summary>
/// Demo seed for roadmap 5.6: Iraq Trading's invoices at every stage the screens show. Baghdad Mall's posted shipment
/// is billed in full and posted (its receivable open, the rep's commission accrued), then two units of the first line
/// are credited for the damaged carton and the credit note applied to the invoice as it posts; Kurdistan Distribution
/// is billed a year of shelf placement up front, deferred over twelve months beside a one-off design fee; Al-Noor's
/// delivery charge waits as a draft to review; and Basra Oil's monthly logistics retainer runs on a schedule, this
/// month's invoice issued as a draft. Prices and tax come from the engines already seeded, never typed here, except
/// the service lines' own agreed amounts.
/// </summary>
internal static class DemoInvoicing
{
    public static async Task<DemoInvoicingOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var invoices = services.GetRequiredService<InvoiceService>();
        var recurring = services.GetRequiredService<RecurringInvoiceService>();
        var iqt = companies.Single(c => c.Definition.Code == "IQT").Company.Id;
        Guid Customer(string key) => DemoBooks.Customers.Single(c => c.Key == key).Ref;

        // Baghdad Mall: everything the posted shipment delivered, billed in one invoice and posted.
        var mall = Customer("cust:baghdad-mall");
        var shipped = (await invoices.InvoicableAsync(iqt, mall, cancellationToken)).Where(static l => l.Kind == "shipment").ToList();
        if (shipped.Count == 0)
        {
            throw new InvalidOperationException("Demo seed failed: Baghdad Mall has no posted shipment line left to invoice.");
        }

        var mallInvoice = Require(await invoices.CreateAsync(new SaveSalesInvoiceRequest(iqt, mall,
            shipped.Select(static l => new SaveSalesInvoiceLineRequest("shipment", l.Remaining, ShipmentLineId: l.ShipmentLineId, OrderLineId: l.OrderLineId)).ToList(),
            CustomerReference: "BM-PO-2026-0917", DocumentDate: today), cancellationToken));
        var mallPosted = Require(await invoices.PostAsync(mallInvoice.Id, cancellationToken));

        // Two units of the first line credited for the carton that arrived damaged; the credit note settles that much of the invoice as it posts.
        var damaged = mallPosted.Lines[0];
        var creditNote = Require(await invoices.CreateAsync(new SaveSalesInvoiceRequest(iqt, mall,
            [new SaveSalesInvoiceLineRequest("invoice", Math.Min(2m, damaged.Quantity), InvoiceLineId: damaged.Id)],
            Kind: "credit_note", CreditsInvoiceId: mallPosted.Id, CustomerReference: "BM-PO-2026-0917", DocumentDate: today,
            Notes: "Two units crushed in carton IPX-4471-0002; credited at the invoiced price."), cancellationToken));
        Require(await invoices.PostAsync(creditNote.Id, cancellationToken));

        // Kurdistan Distribution: a year of shelf placement in the Erbil stores billed up front and deferred month by month, beside the design fee earned at once.
        var placement = Require(await invoices.CreateAsync(new SaveSalesInvoiceRequest(iqt, Customer("cust:kurdistan-dist"),
            [
                new SaveSalesInvoiceLineRequest("service", 1m, UnitPrice: 6_000_000m, Description: "Shelf placement, Erbil stores, twelve months", DeferralPeriods: 12),
                new SaveSalesInvoiceLineRequest("service", 1m, UnitPrice: 450_000m, Description: "Planogram design"),
            ],
            CustomerReference: "KD-PLC-2026", DocumentDate: today), cancellationToken));
        Require(await invoices.PostAsync(placement.Id, cancellationToken));

        // Al-Noor: the express delivery charge, drafted and left for someone to check before it posts.
        Require(await invoices.CreateAsync(new SaveSalesInvoiceRequest(iqt, Customer("cust:al-noor"),
            [new SaveSalesInvoiceLineRequest("service", 1m, UnitPrice: 75_000m, Description: "Express delivery, Najaf branch")],
            DocumentDate: today), cancellationToken));

        // Basra Oil: the logistics retainer invoiced on the first of every month; this month's issued as a draft to review.
        var firstOfMonth = new DateOnly(today.Year, today.Month, 1);
        var retainer = Require(await recurring.CreateAsync(new SaveRecurringInvoiceRequest(iqt, Customer("cust:basra-oil"), "RET-BASRA",
            Text("Basra logistics retainer", "أتعاب الخدمات اللوجستية – البصرة"), "0 9 1 * *",
            [new SaveSalesInvoiceLineRequest("service", 1m, UnitPrice: 1_500_000m, Description: "Monthly logistics retainer, Basra depot")],
            StartsOn: firstOfMonth, CustomerReference: "BO-RET-2026", AutoPost: false,
            Notes: "Agreed for 2026 with the procurement lead; reviewed before each invoice posts."), cancellationToken));
        Require(await recurring.GenerateAsync(retainer.Id, new GenerateRecurringInvoiceRequest(firstOfMonth), cancellationToken));

        return new DemoInvoicingOutcome(Invoices: 4, CreditNotes: 1, RecurringInvoices: 1);
    }

    private static Dictionary<string, string> Text(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
