using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Banking.Tests;

/// <summary>
/// The bank subledger behind journals (A-140, A-194): an opening journal puts the opening balance in the cash box it
/// names, a manual journal moves the bank account it names, the journal's reversal moves it back, and a line that names
/// no box of the company, another account's box or a box in another currency is refused before anything is written;
/// each box's balance stays equal to its ledger account, which the harness checks.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class BankJournalTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static object Line(string account, decimal debit = 0m, decimal credit = 0m, string? subledgerType = null, Guid? subledgerRef = null) =>
        new { accountCode = account, debit, credit, subledgerType, subledgerRef };

    private static async Task<Guid> DraftAsync(HttpClient owner, Guid companyId, string kind, string date, object[] lines) =>
        (await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new { kind, postingDate = date, currency = "IQD", description = Name("Box journal", "قيد الصندوق"), lines })).GetProperty("id").GetGuid();

    [Fact]
    public async Task A_journal_on_a_bank_or_cash_account_moves_the_box_it_names_and_its_reversal_moves_it_back()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "BJ", legalName = Name("Box Journals", "قيود الصندوق"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CASH", name = Name("Cash box", "الصندوق"), kind = "cash", currency = "IQD" })).GetProperty("id").GetGuid();
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "RAF", name = Name("Rafidain", "الرافدين"), kind = "bank", currency = "IQD" })).GetProperty("id").GetGuid();
        var dollars = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "USDB", name = Name("Dollar account", "حساب الدولار"), kind = "bank", currency = "USD" })).GetProperty("id").GetGuid();

        // The owner's money at go-live: 5,000,000 in the cash box and 20,000,000 in the bank.
        var opening = await DraftAsync(owner, companyId, "opening", "2026-09-01",
            [Line("1111", debit: 5000000m, subledgerType: "BANK", subledgerRef: cash), Line("1121", debit: 20000000m, subledgerType: "BANK", subledgerRef: bank), Line("3100", credit: 25000000m)]);
        await owner.PostAsync($"/api/v1/accounting/journals/{opening}/post", new { }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(5000000m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{bank}")).GetProperty("balanceTc").GetDecimal().ShouldBe(20000000m);
        var openingMove = (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}/transactions")).EnumerateArray().Single();
        openingMove.GetProperty("kind").GetString().ShouldBe("opening");
        openingMove.GetProperty("sourceDocumentType").GetString().ShouldBe("journal_entry");

        // 1,500,000 taken from the bank into the cash box by a manual journal: the bank pays out, the box takes in.
        var transfer = await DraftAsync(owner, companyId, "manual", "2026-09-10",
            [Line("1111", debit: 1500000m, subledgerType: "BANK", subledgerRef: cash), Line("1121", credit: 1500000m, subledgerType: "BANK", subledgerRef: bank)]);
        var transferEntry = (await owner.PostAsync($"/api/v1/accounting/journals/{transfer}/post", new { }, HttpStatusCode.OK)).GetProperty("journalEntryId").GetGuid();
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(6500000m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{bank}")).GetProperty("balanceTc").GetDecimal().ShouldBe(18500000m);
        var bankMoves = (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{bank}/transactions")).EnumerateArray().ToList();
        bankMoves.Single(static t => t.GetProperty("kind").GetString() == "disbursement").GetProperty("amountTc").GetDecimal().ShouldBe(-1500000m);
        await owner.AssertInvariantsAsync();

        // Reversing the journal moves both back, each movement pointing at the one it undoes.
        await owner.PostAsync($"/api/v1/accounting/journal-entries/{transferEntry}/reverse", new { reason = "Posted twice", reversalDate = "2026-09-12" });
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}")).GetProperty("balanceTc").GetDecimal().ShouldBe(5000000m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{bank}")).GetProperty("balanceTc").GetDecimal().ShouldBe(20000000m);
        var reversal = (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{cash}/transactions")).EnumerateArray().Single(static t => t.GetProperty("kind").GetString() == "reversal");
        reversal.GetProperty("amountTc").GetDecimal().ShouldBe(-1500000m);
        reversal.GetProperty("reversesTransactionId").GetGuid().ShouldNotBe(Guid.Empty);
        reversal.GetProperty("postingDate").GetString().ShouldBe("2026-09-12");

        // A line that names no box of the company, the box of another account, or a box kept in another currency is refused.
        var nowhere = await DraftAsync(owner, companyId, "manual", "2026-09-15", [Line("1111", debit: 1000m, subledgerType: "BANK", subledgerRef: Guid.NewGuid()), Line("3100", credit: 1000m)]);
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{nowhere}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.bank_account_required");
        var elsewhere = await DraftAsync(owner, companyId, "manual", "2026-09-15", [Line("1121", debit: 1000m, subledgerType: "BANK", subledgerRef: cash), Line("3100", credit: 1000m)]);
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{elsewhere}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.bank_account_mismatch");
        var dinarsIntoDollars = await DraftAsync(owner, companyId, "manual", "2026-09-15", [Line("1121", debit: 1000m, subledgerType: "BANK", subledgerRef: dollars), Line("3100", credit: 1000m)]);
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{dinarsIntoDollars}/post", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.bank_currency");
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{dollars}/transactions")).GetArrayLength().ShouldBe(0, "nothing was written");
        await owner.AssertInvariantsAsync();
    }
}
