using Microsoft.Extensions.DependencyInjection;
using Quicker.Banking.Application;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Receivables.Application;
using Quicker.Receivables.Contracts;

namespace Quicker.Migrator.Demo;

/// <summary>What the receivables seed produced.</summary>
public sealed record DemoReceivablesOutcome(int Receipts, int WriteOffs, int DunningLevels, int DunningLetters);

/// <summary>
/// Demo seed for roadmap 5.7: Iraq Trading collects, writes off and reminds. Baghdad Mall pays most of its invoice by
/// transfer into the operating account (the rest stays open), the few dinars left after a later remittance are written
/// off as a small balance; Kurdistan Distribution leaves a deposit against its next order; the company's reminder
/// ladder is set up with a fee from the second rung, and one run reminds Baghdad Mall of the year-old sale it still
/// owes, charging the fee. Every amount goes through the real services, so the books, the subledger and the
/// invariants hold as they would in production.
/// </summary>
internal static class DemoReceivables
{
    public static async Task<DemoReceivablesOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var receipts = services.GetRequiredService<CustomerReceiptService>();
        var receivables = services.GetRequiredService<IReceivables>();
        var items = services.GetRequiredService<ReceivablesService>();
        var writeOffs = services.GetRequiredService<WriteOffService>();
        var dunning = services.GetRequiredService<DunningService>();
        var bankAccounts = services.GetRequiredService<BankAccountService>();
        var iqt = companies.Single(c => c.Definition.Code == "IQT").Company.Id;
        Guid Customer(string key) => DemoBooks.Customers.Single(c => c.Key == key).Ref;
        var bank = (await bankAccounts.ListAsync(iqt, cancellationToken)).Single(a => a.Code == "OPS");

        // Baghdad Mall: 200 000 by transfer against its invoice (the bank kept 2 500), the rest still open.
        var mall = Customer("cust:baghdad-mall");
        var invoiceItem = (await items.ListAsync(iqt, mall, "live", ReceivableKinds.Invoice, cancellationToken)).Single(i => i.Item.DocumentType == "sales_invoice").Item;
        var receipt = Require(await receipts.CreateAsync(new SaveCustomerReceiptRequest(iqt, mall, bank.Id, ReceiptDate: today, Reference: "TT-BM-2026-1142",
            Lines: [new SavePaymentLineRequest(invoiceItem.Id, 200_000m)], Charges: 2_500m), cancellationToken));
        Require(await receipts.PostAsync(receipt.Id, cancellationToken));

        // A second remittance a day later settles all but the odd dinars, which are not worth chasing.
        var left = (await receivables.FindAsync(invoiceItem.Id, cancellationToken))!.RemainingTc;
        var odd = left % 1_000m;
        var second = Require(await receipts.CreateAsync(new SaveCustomerReceiptRequest(iqt, mall, bank.Id, ReceiptDate: today, Reference: "TT-BM-2026-1157",
            Lines: [new SavePaymentLineRequest(invoiceItem.Id, left - odd)]), cancellationToken));
        Require(await receipts.PostAsync(second.Id, cancellationToken));
        var writeOffCount = 0;
        if (odd > 0m)
        {
            Require(await writeOffs.WriteOffAsync(invoiceItem.Id, new WriteOffRequest("Remittance short by the bank's rounding; not worth chasing", odd, "small_balance", today), cancellationToken));
            writeOffCount++;
        }

        // Kurdistan Distribution: a deposit against the Erbil stores' next order, waiting on the deposits control.
        var deposit = Require(await receipts.CreateAsync(new SaveCustomerReceiptRequest(iqt, Customer("cust:kurdistan-dist"), bank.Id, "customer_deposit", today, Reference: "TT-KD-DEP-07", OnAccount: 5_000_000m), cancellationToken));
        Require(await receipts.PostAsync(deposit.Id, cancellationToken));

        // The reminder ladder: a courteous reminder after a week, a notice with a fee after a month, a final notice after three.
        Require(await dunning.SaveLevelAsync(null, new SaveDunningLevelRequest(iqt, 1, 7, Text("Reminder", "تذكير"), Text("Our records show the items below are past due. If payment is on its way, please disregard this reminder.", "تشير سجلاتنا إلى تأخر سداد البنود أدناه. إن كان السداد في الطريق فتجاهلوا هذا التذكير.")), cancellationToken));
        Require(await dunning.SaveLevelAsync(null, new SaveDunningLevelRequest(iqt, 2, 30, Text("Second notice", "إشعار ثانٍ"), Text("The items below remain unpaid a month after their due date. A reminder fee has been added to your account.", "لا تزال البنود أدناه غير مسددة بعد شهر من استحقاقها، وقد أُضيف رسم تذكير إلى حسابكم."), FeeAmount: 25_000m), cancellationToken));
        Require(await dunning.SaveLevelAsync(null, new SaveDunningLevelRequest(iqt, 3, 90, Text("Final notice", "إشعار نهائي"), Text("Unless the items below are settled within seven days, further deliveries will need credit control's approval.", "ما لم تُسدَّد البنود أدناه خلال سبعة أيام، ستحتاج التسليمات القادمة إلى موافقة مراقبة الائتمان."), FeeAmount: 50_000m), cancellationToken));

        // One run for Baghdad Mall: the year-old sale from the books is long past its final notice.
        var preview = Require(await dunning.PreviewAsync(iqt, today, mall, cancellationToken));
        var letters = 0;
        if (preview.Letters.Count > 0)
        {
            letters = Require(await dunning.RunAsync(new DunningRunRequest(iqt, today, mall), cancellationToken)).Letters;
        }

        return new DemoReceivablesOutcome(Receipts: 3, WriteOffs: writeOffCount, DunningLevels: 3, DunningLetters: letters);
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
