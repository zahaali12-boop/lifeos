using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Results;
using Quicker.Tax.Contracts;
using Quicker.Tax.Domain;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Tests;

/// <summary>
/// The tax ledger as documents write it (ADR-0018): once per document, never into a filed return period, reversed by
/// opposite rows on the reversal's date, never updated or deleted, and only in a regime the company is registered in.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TaxLedgerTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task Documents_write_their_tax_once_never_into_a_filed_period_and_reverse_it_with_opposite_rows()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "KSA", legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh" })).GetProperty("id").GetGuid();
        var saudi = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var uae = await owner.PostAsync("/api/v1/tax/templates/AE-VAT/install", new { });
        var regimeId = saudi.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = company, regimeId, registrationNumber = "310123456700003" });
        var standard = CodeId(saudi, "SA-S");
        var zero = CodeId(saudi, "SA-Z");
        var tenant = ws.TenantId;

        TaxPostingRequest Invoice(Guid id, string number, string date, Guid code) => new(
            company, DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture), null, "sales", "sales_invoice", id, number, null, null, "SAR",
            [new TaxEntryRequest(code, TaxDirections.Sales, 15m, 1_000m, 150m, 1_000m, 150m, false, true), new TaxEntryRequest(zero, TaxDirections.Sales, 0m, 200m, 0m, 200m, 0m, false, true)]);

        var first = Guid.CreateVersion7();
        (await Ledger(tenant, l => l.RecordAsync(Invoice(first, "INV-1", "2026-07-10", standard)))).IsSuccess.ShouldBeTrue();
        (await Ledger(tenant, l => l.RecordAsync(Invoice(first, "INV-1", "2026-07-10", standard)))).Error!.Code.ShouldBe("tax.document_recorded");

        // A code of a regime the company is not registered in is refused.
        (await Ledger(tenant, l => l.RecordAsync(Invoice(Guid.CreateVersion7(), "INV-X", "2026-07-11", CodeId(uae, "AE-S"))))).Error!.Code.ShouldBe("tax.company_not_registered");

        // The third quarter is filed: nothing dated in it may be written, a reversal included; the fourth is open.
        await host.InTenantAsync(tenant, async (services, ct) =>
        {
            var db = services.GetRequiredService<TaxDbContext>();
            db.ReturnPeriods.Add(new TaxReturnPeriod { Id = Guid.CreateVersion7(), CompanyId = company, RegimeId = regimeId, PeriodStart = new DateOnly(2026, 7, 1), PeriodEnd = new DateOnly(2026, 9, 30), Status = "filed", FiledAt = DateTimeOffset.UtcNow, Reference = "ZATCA-Q3", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            return true;
        });
        var late = await Ledger(tenant, l => l.RecordAsync(Invoice(Guid.CreateVersion7(), "INV-2", "2026-09-30", standard)));
        late.Error!.Code.ShouldBe("tax.period_filed");
        late.Error.Why!["reference"].ShouldBe("ZATCA-Q3");
        (await Ledger(tenant, l => l.ReverseAsync("sales_invoice", first, new DateOnly(2026, 9, 15), null))).Error!.Code.ShouldBe("tax.period_filed");
        (await Ledger(tenant, l => l.RecordAsync(Invoice(Guid.CreateVersion7(), "INV-3", "2026-10-01", standard)))).IsSuccess.ShouldBeTrue();

        // Reversed in the open quarter: opposite rows, each pointing at the row it reverses; a second reversal is refused.
        (await Ledger(tenant, l => l.ReverseAsync("sales_invoice", first, new DateOnly(2026, 10, 2), null))).IsSuccess.ShouldBeTrue();
        (await Ledger(tenant, l => l.ReverseAsync("sales_invoice", first, new DateOnly(2026, 10, 3), null))).Error!.Code.ShouldBe("tax.document_reversed");
        (await Ledger(tenant, l => l.ReverseAsync("sales_invoice", Guid.CreateVersion7(), new DateOnly(2026, 10, 3), null))).IsSuccess.ShouldBeTrue("a document without tax reverses nothing");

        await using var connection = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var rows = (await connection.QueryAsync<(string PostingDate, decimal TaxTc, Guid? ReversesEntryId)>(
            "SELECT posting_date::text, tax_tc, reverses_entry_id FROM app.tax_entries WHERE tenant_id = @tenant AND source_document_id = @first ORDER BY posting_date, tax_tc", new { tenant, first })).ToList();
        rows.Count.ShouldBe(4);
        rows.Where(static r => r.ReversesEntryId is null).Sum(static r => r.TaxTc).ShouldBe(150m);
        rows.Where(static r => r.ReversesEntryId is not null).Select(static r => (r.PostingDate, r.TaxTc)).ShouldBe([("2026-10-02", -150m), ("2026-10-02", 0m)]);

        // Append-only: the ledger is never edited in place.
        var update = await Should.ThrowAsync<PostgresException>(() => connection.ExecuteAsync("UPDATE app.tax_entries SET tax_tc = 0 WHERE tenant_id = @tenant", new { tenant }));
        update.MessageText.ShouldContain("append_only_violation");
    }

    private static Guid CodeId(JsonElement regime, string code) => regime.GetProperty("codes").ByCode(code).GetProperty("id").GetGuid();

    private Task<Result> Ledger(Guid tenant, Func<ITaxLedger, Task<Result>> work) =>
        host.InTenantAsync(tenant, (services, _) => work(services.GetRequiredService<ITaxLedger>()));
}
