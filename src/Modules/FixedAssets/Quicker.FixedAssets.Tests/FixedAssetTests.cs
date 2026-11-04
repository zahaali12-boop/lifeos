using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.FixedAssets.Tests;

/// <summary>
/// Fixed assets (founder programme item 3, roadmap 6.6, A-175): an asset bought from a supplier with VAT, activated
/// and depreciated to the cent by monthly runs that reverse; an asset paid from the bank under the mid-month convention
/// with an addition, an impairment and a sale with catch-up depreciation; opening balances, a partial scrap by quantity
/// and a revaluation. The FA subledger reconciles to its control accounts after every scenario (harness).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FixedAssetTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Supplier, Guid Customer, Guid Main, Guid Vehicles, Guid Equipment, Guid Furniture, Guid Standard);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "KSA", legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        var domestic = groups.EnumerateArray().Single(static g => g.GetProperty("kind").GetString() == "partner" && g.GetProperty("code").GetString() == "DOMESTIC").GetProperty("id").GetGuid();
        var standard = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "ALRAJHI-CARS", legalName = Name("Al Rajhi Cars", "الراجحي للسيارات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "SAR", taxGroupId = domestic });
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "USED-MKT", legalName = Name("Used Market", "سوق المستعمل"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "SAR" });
        var main = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "MAIN", name = Name("Main account", "الحساب الرئيسي"), kind = "bank", currency = "SAR" })).GetProperty("id").GetGuid();
        var vehicles = (await owner.PostAsync("/api/v1/fixed-assets/categories", new { code = "VEH", name = Name("Vehicles", "مركبات"), defaultMethod = "straight_line", defaultLifeMonths = 60, defaultSalvagePct = 10m, convention = "full_month", costAccountCode = "1510", accumulatedDepreciationAccountCode = "1520", depreciationExpenseAccountCode = "6200" })).GetProperty("id").GetGuid();
        var equipment = (await owner.PostAsync("/api/v1/fixed-assets/categories", new { code = "EQP", name = Name("Equipment", "معدات"), defaultMethod = "straight_line", defaultLifeMonths = 24, convention = "mid_month" })).GetProperty("id").GetGuid();
        var furniture = (await owner.PostAsync("/api/v1/fixed-assets/categories", new { code = "FURN", name = Name("Furniture", "أثاث"), defaultMethod = "straight_line", defaultLifeMonths = 60, convention = "full_month" })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/fixed-assets/categories", new { code = "veh", name = Name("Again", "مرة أخرى"), defaultMethod = "straight_line", defaultLifeMonths = 12 }, HttpStatusCode.Conflict)).Code.ShouldBe("asset_category.code_taken");
        (await owner.PostErrorAsync("/api/v1/fixed-assets/categories", new { code = "BAD", name = Name("Bad", "سيء"), defaultMethod = "straight_line", defaultLifeMonths = 0 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("asset_category.life_invalid");
        return new Setup(ws, owner, companyId, supplier, customer, main, vehicles, equipment, furniture, standard);
    }

    private async Task<T> QueryAsync<T>(Setup s, string sql, object? extra = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<T>(sql, new { t = s.Ws.TenantId, c = s.CompanyId, r = (extra as Guid?) ?? Guid.Empty }) ?? default!;
    }

    private Task<decimal> RoleAsync(Setup s, string role) => QueryAsync<decimal>(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = '{role}'");

    private static JsonElement GlBook(JsonElement asset) => asset.GetProperty("books").EnumerateArray().Single(static b => b.GetProperty("postsToGl").GetBoolean());

    private static decimal PlannedSum(JsonElement book) => book.GetProperty("schedule").EnumerateArray().Where(static l => l.GetProperty("status").GetString() != "skipped").Sum(static l => l.GetProperty("status").GetString() == "posted" ? l.GetProperty("postedAmount").GetDecimal() : l.GetProperty("plannedAmount").GetDecimal());

    [Fact]
    public async Task A_vehicle_bought_with_vat_is_activated_depreciated_to_the_cent_by_runs_that_reverse_and_reconciles_to_the_ledger()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var draft = await owner.PostAsync("/api/v1/fixed-assets/assets", new
        {
            companyId = s.CompanyId,
            name = Name("Delivery van", "شاحنة توصيل"),
            categoryId = s.Vehicles,
            acquisitionDate = "2026-01-15",
            cost = 120_000m,
            acquisitionSource = "supplier",
            supplierPartnerId = s.Supplier,
            supplierReference = "CAR-7781",
            acquisitionTaxCodeId = s.Standard,
            acquisitionDueDate = "2026-02-14",
            serialNumber = "VIN-123",
        });
        var id = draft.GetProperty("id").GetGuid();
        (draft.GetProperty("status").GetString(), draft.GetProperty("number").GetString()).ShouldBe(("draft", "FA-000001"));
        var draftBook = GlBook(draft);
        (draftBook.GetProperty("method").GetString(), draftBook.GetProperty("lifeMonths").GetInt32(), draftBook.GetProperty("salvageValue").GetDecimal(), draftBook.GetProperty("bookCode").GetString()).ShouldBe(("straight_line", 60, 12_000m, "MAIN"));
        draftBook.GetProperty("schedule").GetArrayLength().ShouldBe(0);
        (await owner.PostErrorAsync("/api/v1/fixed-assets/assets", new { companyId = s.CompanyId, name = Name("No supplier", "بلا مورد"), categoryId = s.Vehicles, acquisitionDate = "2026-01-15", cost = 10m, acquisitionSource = "supplier" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("asset.supplier_required");

        // Activation posts Dr cost 120,000, Dr input VAT 18,000 / Cr AP 138,000 and plans 60 months of 1,800.
        var active = await owner.PostAsync($"/api/v1/fixed-assets/assets/{id}/activate", new { }, HttpStatusCode.OK);
        active.GetProperty("status").GetString().ShouldBe("active");
        active.GetProperty("acquisitionEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        var book = GlBook(active);
        book.GetProperty("schedule").GetArrayLength().ShouldBe(60);
        PlannedSum(book).ShouldBe(108_000m);
        book.GetProperty("schedule").EnumerateArray().All(static l => l.GetProperty("plannedAmount").GetDecimal() == 1_800m).ShouldBeTrue();
        book.GetProperty("schedule").EnumerateArray().First().GetProperty("periodStart").GetString().ShouldBe("2026-01-01");
        (await RoleAsync(s, "FaCost")).ShouldBe(120_000m);
        (await RoleAsync(s, "InputTax")).ShouldBe(18_000m);
        (await RoleAsync(s, "AP")).ShouldBe(-138_000m);
        var item = (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).Only().GetProperty("item");
        (item.GetProperty("documentType").GetString(), item.GetProperty("remainingTc").GetDecimal(), item.GetProperty("dueDate").GetString()).ShouldBe(("fa_asset", 138_000m, "2026-02-14"));
        (await QueryAsync<long>(s, "SELECT count(*) FROM app.gl_posting_rules r JOIN app.gl_posting_profiles p ON p.tenant_id = r.tenant_id AND p.id = r.profile_id WHERE r.tenant_id = @t AND p.company_id = @c AND r.asset_category_id = @r", s.Vehicles)).ShouldBe(3L, "the category's accounts became rules keyed by the category");
        (await owner.PostErrorAsync($"/api/v1/fixed-assets/assets/{id}/activate", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("asset.not_draft");

        // The register at January: cost and net book value, nothing depreciated yet.
        var register = await owner.GetOkAsync($"/api/v1/fixed-assets/register?companyId={s.CompanyId}&asOf=2026-01-31");
        var row = register.GetProperty("rows").Only();
        (row.GetProperty("cost").GetDecimal(), row.GetProperty("netBookValue").GetDecimal(), register.GetProperty("totals").GetProperty("cost").GetDecimal()).ShouldBe((120_000m, 120_000m, 120_000m));

        // A run through March posts the three months as one journal; a second through the same date has nothing to post.
        var run = await owner.PostAsync("/api/v1/fixed-assets/depreciation-runs", new { companyId = s.CompanyId, through = "2026-03-31" });
        (run.GetProperty("number").GetString(), run.GetProperty("assetCount").GetInt32(), run.GetProperty("total").GetDecimal(), run.GetProperty("status").GetString()).ShouldBe(("DEP-2026-0001", 1, 5_400m, "posted"));
        var depreciated = await owner.GetOkAsync($"/api/v1/fixed-assets/assets/{id}");
        (depreciated.GetProperty("accumulatedDepreciation").GetDecimal(), depreciated.GetProperty("netBookValue").GetDecimal(), depreciated.GetProperty("depreciatedThrough").GetString()).ShouldBe((5_400m, 114_600m, "2026-03-31"));
        (await RoleAsync(s, "DepreciationExpense")).ShouldBe(5_400m);
        (await RoleAsync(s, "FaAccDep")).ShouldBe(-5_400m);
        (await owner.PostErrorAsync("/api/v1/fixed-assets/depreciation-runs", new { companyId = s.CompanyId, through = "2026-03-31" }, HttpStatusCode.Conflict)).Code.ShouldBe("depreciation.nothing_due");
        (await owner.PostErrorAsync($"/api/v1/fixed-assets/assets/{id}/impair", new { amount = 1_000m, date = "2026-02-15", reason = "Dented" }, HttpStatusCode.Conflict)).Code.ShouldBe("asset.dated_before_depreciation");

        // Reversed, the months are planned again; re-run through June.
        var reversed = await owner.PostAsync($"/api/v1/fixed-assets/depreciation-runs/{run.GetProperty("id").GetGuid()}/reverse", new { reason = "Wrong month" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        (await owner.GetOkAsync($"/api/v1/fixed-assets/assets/{id}")).GetProperty("accumulatedDepreciation").GetDecimal().ShouldBe(0m);
        (await RoleAsync(s, "FaAccDep")).ShouldBe(0m);
        var second = await owner.PostAsync("/api/v1/fixed-assets/depreciation-runs", new { companyId = s.CompanyId, through = "2026-06-30" });
        second.GetProperty("total").GetDecimal().ShouldBe(10_800m);
        var forecast = await owner.GetOkAsync($"/api/v1/fixed-assets/forecast?companyId={s.CompanyId}&from=2026-01-01&to=2026-12-31");
        forecast.GetArrayLength().ShouldBe(12);
        forecast.EnumerateArray().Sum(static f => f.GetProperty("posted").GetDecimal()).ShouldBe(10_800m);
        forecast.EnumerateArray().Sum(static f => f.GetProperty("planned").GetDecimal()).ShouldBe(21_600m);
        (await owner.GetOkAsync($"/api/v1/fixed-assets/depreciation-runs?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(2);

        var reconciliation = await owner.GetOkAsync($"/api/v1/fixed-assets/reconciliation?companyId={s.CompanyId}");
        reconciliation.GetProperty("ledgerAgrees").GetBoolean().ShouldBeTrue();
        reconciliation.GetProperty("rows").EnumerateArray().Single(static r => r.GetProperty("role").GetString() == "FaCost").GetProperty("register").GetDecimal().ShouldBe(120_000m);
        reconciliation.GetProperty("rows").EnumerateArray().Single(static r => r.GetProperty("role").GetString() == "FaAccDep").GetProperty("register").GetDecimal().ShouldBe(10_800m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task An_asset_paid_from_the_bank_under_mid_month_takes_an_addition_an_impairment_and_is_sold_with_catch_up_depreciation()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var asset = await owner.PostAsync("/api/v1/fixed-assets/assets", new { companyId = s.CompanyId, name = Name("Packing machine", "آلة تغليف"), categoryId = s.Equipment, acquisitionDate = "2026-02-10", cost = 24_000m, acquisitionSource = "bank", acquisitionBankAccountId = s.Main });
        var id = asset.GetProperty("id").GetGuid();
        var active = await owner.PostAsync($"/api/v1/fixed-assets/assets/{id}/activate", new { }, HttpStatusCode.OK);
        var book = GlBook(active);
        var schedule = book.GetProperty("schedule").EnumerateArray().ToList();
        schedule.Count.ShouldBe(25);
        (schedule[0].GetProperty("plannedAmount").GetDecimal(), schedule[0].GetProperty("weight").GetDecimal(), schedule[1].GetProperty("plannedAmount").GetDecimal(), schedule[^1].GetProperty("plannedAmount").GetDecimal()).ShouldBe((500m, 0.5m, 1_000m, 500m));
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Main}")).GetProperty("balanceTc").GetDecimal().ShouldBe(-24_000m);
        (await RoleAsync(s, "Bank")).ShouldBe(-24_000m);

        // Through April: half of February, March, April.
        (await owner.PostAsync("/api/v1/fixed-assets/depreciation-runs", new { companyId = s.CompanyId, through = "2026-04-30" })).GetProperty("total").GetDecimal().ShouldBe(2_500m);

        // An addition from the clearing account spreads what is left (27,500) over the 21.5 months left.
        var added = await owner.PostAsync($"/api/v1/fixed-assets/assets/{id}/additions", new { amount = 6_000m, date = "2026-05-05", source = "clearing", description = "Conveyor" }, HttpStatusCode.OK);
        added.GetProperty("cost").GetDecimal().ShouldBe(30_000m);
        (await RoleAsync(s, "FaClearing")).ShouldBe(-6_000m);
        var afterAddition = GlBook(added);
        PlannedSum(afterAddition).ShouldBe(30_000m);
        afterAddition.GetProperty("schedule").EnumerateArray().Count(static l => l.GetProperty("status").GetString() == "planned").ShouldBe(22);
        afterAddition.GetProperty("schedule").EnumerateArray().First(static l => l.GetProperty("status").GetString() == "planned").GetProperty("periodStart").GetString().ShouldBe("2026-05-01");

        // An impairment of 2,000 is a loss (no surplus yet) and lowers the plan.
        var impaired = await owner.PostAsync($"/api/v1/fixed-assets/assets/{id}/impair", new { amount = 2_000m, date = "2026-05-20", reason = "Motor damaged" }, HttpStatusCode.OK);
        (impaired.GetProperty("accumulatedImpairment").GetDecimal(), impaired.GetProperty("netBookValue").GetDecimal()).ShouldBe((2_000m, 25_500m));
        (await RoleAsync(s, "ImpairmentLoss")).ShouldBe(2_000m);
        (await RoleAsync(s, "FaAccImpairment")).ShouldBe(-2_000m);
        PlannedSum(GlBook(impaired)).ShouldBe(28_000m, "2,500 posted and 25,500 still to go");

        var verified = await owner.PostAsync($"/api/v1/fixed-assets/assets/{id}/verify", new { verifiedOn = "2026-06-01", condition = "good", locationNote = "Line 2" }, HttpStatusCode.OK);
        verified.GetProperty("verifications").Only().GetProperty("condition").GetString().ShouldBe("good");
        (await owner.PostErrorAsync($"/api/v1/fixed-assets/assets/{id}/dispose", new { date = "2026-08-15", kind = "sale", proceeds = 20_000m }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("asset.proceeds_source_required");

        // Sold in mid-August: May, June, July and half of August are caught up first, then the asset leaves at its net book value.
        var sold = await owner.PostAsync($"/api/v1/fixed-assets/assets/{id}/dispose", new { date = "2026-08-15", kind = "sale", proceeds = 20_000m, proceedsSource = "bank", bankAccountId = s.Main, reason = "Replaced" }, HttpStatusCode.OK);
        (sold.GetProperty("status").GetString(), sold.GetProperty("disposedOn").GetString(), sold.GetProperty("disposalKind").GetString()).ShouldBe(("disposed", "2026-08-15", "sale"));
        var disposal = sold.GetProperty("transactions").EnumerateArray().Single(static t => t.GetProperty("kind").GetString() == "disposal");
        var catchUp = sold.GetProperty("transactions").EnumerateArray().Where(static t => t.GetProperty("kind").GetString() == "depreciation").Sum(static t => t.GetProperty("accumulatedDepreciationDelta").GetDecimal());
        catchUp.ShouldBeGreaterThan(2_500m);
        var netAtSale = 30_000m - catchUp - 2_000m;
        disposal.GetProperty("gainLoss").GetDecimal().ShouldBe(20_000m - netAtSale);
        (await RoleAsync(s, "FaCost")).ShouldBe(0m);
        (await RoleAsync(s, "FaAccDep")).ShouldBe(0m);
        (await RoleAsync(s, "FaAccImpairment")).ShouldBe(0m);
        (await RoleAsync(s, "GainLossOnDisposal")).ShouldBe(-(20_000m - netAtSale));
        (await RoleAsync(s, "Bank")).ShouldBe(-4_000m);
        (await owner.GetOkAsync($"/api/v1/banking/bank-accounts/{s.Main}/transactions")).GetArrayLength().ShouldBe(2);
        GlBook(sold).GetProperty("schedule").EnumerateArray().Count(static l => l.GetProperty("status").GetString() == "planned").ShouldBe(0);
        (await owner.PostErrorAsync($"/api/v1/fixed-assets/assets/{id}/dispose", new { date = "2026-09-01", kind = "scrap" }, HttpStatusCode.Conflict)).Code.ShouldBe("asset.not_active");
        (await owner.GetOkAsync($"/api/v1/fixed-assets/depreciation-runs?companyId={s.CompanyId}")).GetArrayLength().ShouldBe(2, "the monthly run and the catch-up at the sale");
        (await owner.GetOkAsync($"/api/v1/fixed-assets/register?companyId={s.CompanyId}&asOf=2026-08-31")).GetProperty("totals").GetProperty("netBookValue").GetDecimal().ShouldBe(0m);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Opening_balances_a_partial_scrap_by_quantity_and_a_revaluation_with_a_later_impairment_against_the_surplus()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // Ten chairs held since 2022: cost and accumulated depreciation to date against opening equity; twelve months of 2,500 left.
        var chairs = await owner.PostAsync("/api/v1/fixed-assets/assets", new { companyId = s.CompanyId, name = Name("Office chairs", "كراسي مكتب"), categoryId = s.Furniture, acquisitionDate = "2022-01-01", cost = 50_000m, quantity = 10m, unit = "PCS", acquisitionSource = "opening", openingAccumulatedDepreciation = 20_000m, openingDepreciatedThrough = "2025-12-31" });
        var chairsId = chairs.GetProperty("id").GetGuid();
        var opened = await owner.PostAsync($"/api/v1/fixed-assets/assets/{chairsId}/activate", new { }, HttpStatusCode.OK);
        (opened.GetProperty("accumulatedDepreciation").GetDecimal(), opened.GetProperty("netBookValue").GetDecimal()).ShouldBe((20_000m, 30_000m));
        var plan = GlBook(opened).GetProperty("schedule").EnumerateArray().ToList();
        plan.Count.ShouldBe(12);
        plan.All(static l => l.GetProperty("plannedAmount").GetDecimal() == 2_500m).ShouldBeTrue();
        plan[0].GetProperty("periodStart").GetString().ShouldBe("2026-01-01");
        (await RoleAsync(s, "FaCost")).ShouldBe(50_000m);
        (await RoleAsync(s, "FaAccDep")).ShouldBe(-20_000m);
        (await RoleAsync(s, "OpeningBalanceEquity")).ShouldBe(-30_000m);
        (await owner.PostAsync("/api/v1/fixed-assets/depreciation-runs", new { companyId = s.CompanyId, through = "2026-02-28" })).GetProperty("total").GetDecimal().ShouldBe(5_000m);

        // Four of the ten chairs scrapped at the end of March (no depreciation that month under full-month): 40 % of cost and depreciation leave, the rest is a loss; the six left continue over the ten months left.
        var scrapped = await owner.PostAsync($"/api/v1/fixed-assets/assets/{chairsId}/dispose", new { date = "2026-03-31", kind = "scrap", quantity = 4m, reason = "Broken" }, HttpStatusCode.OK);
        (scrapped.GetProperty("status").GetString(), scrapped.GetProperty("quantity").GetDecimal(), scrapped.GetProperty("cost").GetDecimal(), scrapped.GetProperty("accumulatedDepreciation").GetDecimal()).ShouldBe(("active", 6m, 30_000m, 15_000m));
        (await RoleAsync(s, "GainLossOnDisposal")).ShouldBe(10_000m, "a loss of the 10,000 net book value scrapped");
        var remaining = GlBook(scrapped).GetProperty("schedule").EnumerateArray().Where(static l => l.GetProperty("status").GetString() == "planned").ToList();
        remaining.Count.ShouldBe(10);
        remaining.All(static l => l.GetProperty("plannedAmount").GetDecimal() == 1_500m).ShouldBeTrue();
        remaining[0].GetProperty("periodStart").GetString().ShouldBe("2026-03-01", "the six chairs left keep depreciating from the month of the scrap under the full-month convention");

        // A building-like asset revalued upward after a year: depreciation eliminated against cost, the rise to the surplus; a later impairment uses the surplus first.
        var rig = await owner.PostAsync("/api/v1/fixed-assets/assets", new { companyId = s.CompanyId, name = Name("Workshop", "ورشة"), categoryId = s.Furniture, acquisitionDate = "2026-01-01", cost = 100_000m, acquisitionSource = "bank", acquisitionBankAccountId = s.Main, books = new[] { new { bookId = GlBook(opened).GetProperty("bookId").GetGuid(), lifeMonths = 120, salvageValue = 0m } } });
        var rigId = rig.GetProperty("id").GetGuid();
        GlBook(rig).GetProperty("lifeMonths").GetInt32().ShouldBe(120);
        await owner.PostAsync($"/api/v1/fixed-assets/assets/{rigId}/activate", new { }, HttpStatusCode.OK);
        (await owner.PostAsync("/api/v1/fixed-assets/depreciation-runs", new { companyId = s.CompanyId, through = "2026-12-31" })).GetProperty("total").GetDecimal().ShouldBe(15_000m + 10_000m, "the chairs' ten months of 1,500 and the workshop's twelve of 833.33");
        var revalued = await owner.PostAsync($"/api/v1/fixed-assets/assets/{rigId}/revalue", new { fairValue = 120_000m, date = "2026-12-31", reason = "Valuer's report" }, HttpStatusCode.OK);
        var revaluedBook = GlBook(revalued);
        (revaluedBook.GetProperty("cost").GetDecimal(), revaluedBook.GetProperty("accumulatedDepreciation").GetDecimal(), revaluedBook.GetProperty("revaluationSurplus").GetDecimal(), revaluedBook.GetProperty("netBookValue").GetDecimal()).ShouldBe((120_000m, 0m, 30_000m, 120_000m));
        (await RoleAsync(s, "RevaluationSurplus")).ShouldBe(-30_000m);
        PlannedSum(revaluedBook).ShouldBe(10_000m + 120_000m, "ten thousand posted and the new amount over the 108 months left");
        revaluedBook.GetProperty("schedule").EnumerateArray().Count(static l => l.GetProperty("status").GetString() == "planned").ShouldBe(108);

        var impaired = await owner.PostAsync($"/api/v1/fixed-assets/assets/{rigId}/impair", new { amount = 5_000m, date = "2026-12-31", reason = "Roof damage" }, HttpStatusCode.OK);
        GlBook(impaired).GetProperty("revaluationSurplus").GetDecimal().ShouldBe(25_000m);
        (await RoleAsync(s, "RevaluationSurplus")).ShouldBe(-25_000m);
        (await RoleAsync(s, "ImpairmentLoss")).ShouldBe(0m, "the surplus absorbed it");
        (await RoleAsync(s, "FaAccImpairment")).ShouldBe(-5_000m);

        // A change of estimate: the chairs' remaining life stretched to twenty months from the next unposted month (none left in 2026: they are fully depreciated); refused on the disposed state only.
        (await owner.GetOkAsync($"/api/v1/fixed-assets/assets/{chairsId}")).GetProperty("status").GetString().ShouldBe("fully_depreciated");
        (await owner.GetOkAsync($"/api/v1/fixed-assets/reconciliation?companyId={s.CompanyId}")).GetProperty("ledgerAgrees").GetBoolean().ShouldBeTrue();
        await owner.AssertInvariantsAsync();
    }
}
