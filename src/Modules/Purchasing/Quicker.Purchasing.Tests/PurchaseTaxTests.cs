using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// Purchase documents taxed by the tax engine (roadmap 5.3, A-147), for a Saudi company: an order taxed on its date; a
/// goods invoice whose input VAT is posted by code and written to the tax ledger; imported services self-assessed (input
/// and output tax, nothing more to pay the supplier); entertainment whose VAT cannot be recovered and so costs more; a
/// code chosen on the order carried to the invoice; a reversal taking the tax back; a filed return period refusing a
/// posting into it; and the ledger tying to the tax accounts of the general ledger to the halala.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PurchaseTaxTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid RegimeId, JsonElement Regime, Guid Laptop, Guid Cleaning, Guid Domestic, Guid Foreign);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = "KSA", legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "SAR", validFrom = "2026-01-01", rate = 3.75m });
        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "RUH", name = Name("Riyadh warehouse", "مستودع الرياض") })).GetProperty("id").GetGuid();

        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        var laptop = (await owner.PostAsync("/api/v1/items", new { code = "LAPTOP", name = Name("Laptop", "حاسوب محمول"), baseUom = "PCS", itemTaxGroupId = Group("item", "STANDARD") })).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Office cleaning", "تنظيف المكتب"), baseUom = "HR", type = "service", itemTaxGroupId = Group("item", "SERVICES") })).GetProperty("id").GetGuid();
        var domestic = (await owner.PostAsync("/api/v1/partners", new { code = "RIYADH-IT", legalName = Name("Riyadh IT Supplies", "الرياض لتقنية المعلومات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{domestic}/supplier-accounts/{companyId}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC") });
        var foreign = (await owner.PostAsync("/api/v1/partners", new { code = "DUBLIN-SOFT", legalName = Name("Dublin Software", "دبلن للبرمجيات"), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{foreign}/supplier-accounts/{companyId}", new { currency = "USD", taxGroupId = Group("partner", "FOREIGN") });
        return new Setup(ws, owner, companyId, warehouse, regimeId, regime, laptop, cleaning, domestic, foreign);
    }

    private static Guid CodeId(JsonElement regime, string code) => regime.GetProperty("codes").EnumerateArray().Single(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    private async Task<T> QueryAsync<T>(Setup s, string sql, object? extra = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<T>(sql, new { t = s.Ws.TenantId, c = s.CompanyId, r = (extra as Guid?) ?? Guid.Empty }) ?? default!;
    }

    private Task<decimal> RoleAsync(Setup s, string role) => QueryAsync<decimal>(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = '{role}'");

    private Task<decimal> LedgerAsync(Setup s, string where) => QueryAsync<decimal>(s, $"SELECT coalesce(sum(tax_fc), 0) FROM app.tax_entries WHERE tenant_id = @t AND company_id = @c AND {where}");

    private static async Task<JsonElement> PostInvoiceAsync(HttpClient owner, object body)
    {
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", body);
        var id = invoice.GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{id}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        return await owner.PostAsync($"/api/v1/purchasing/invoices/{id}/post", new { }, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Purchases_carry_input_vat_reverse_charge_and_unrecoverable_vat_to_the_journal_and_the_tax_ledger_which_tie_to_each_other()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // The order: ten laptops at 3,000 riyals, 15% VAT on the order date; cleaning on a code the buyer chose (exempt).
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new
        {
            companyId = s.CompanyId,
            partnerId = s.Domestic,
            warehouseId = s.WarehouseId,
            orderDate = "2026-09-01",
            lines = new object[] { new { itemId = s.Laptop, quantity = 10m, uom = "PCS", unitPrice = 3_000m }, new { itemId = s.Cleaning, quantity = 4m, uom = "HR", unitPrice = 100m, taxCodeId = CodeId(s.Regime, "SA-E") } },
        });
        (order.GetProperty("totalNet").GetDecimal(), order.GetProperty("totalTax").GetDecimal(), order.GetProperty("totalGross").GetDecimal()).ShouldBe((30_400m, 4_500m, 34_900m));
        var laptopLine = order.GetProperty("lines")[0];
        (laptopLine.GetProperty("taxCode").GetString(), laptopLine.GetProperty("taxRatePct").GetDecimal(), laptopLine.GetProperty("taxAmount").GetDecimal(), laptopLine.GetProperty("taxReason").GetString()).ShouldBe(("SA-S", 15m, 4_500m, "rule"));
        var cleaningLine = order.GetProperty("lines")[1];
        (cleaningLine.GetProperty("taxCode").GetString(), cleaningLine.GetProperty("taxReason").GetString()).ShouldBe(("SA-E", "chosen"));
        var orderId = order.GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");

        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-05", lines = new[] { new { orderLineId = laptopLine.GetProperty("id").GetGuid(), quantity = 10m } } });
        var receiptLine = (await owner.PostAsync($"/api/v1/purchasing/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("lines").Only().GetProperty("id").GetGuid();

        // The goods invoice: input VAT by code on the journal, the payable is what the supplier charged.
        var goods = await PostInvoiceAsync(owner, new
        {
            companyId = s.CompanyId,
            partnerId = s.Domestic,
            supplierInvoiceNumber = "RIT-7781",
            documentDate = "2026-09-06",
            postingDate = "2026-09-06",
            applyWht = false,
            lines = new object[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 10m, unitPrice = 3_000m }, new { kind = "order", orderLineId = cleaningLine.GetProperty("id").GetGuid(), quantity = 4m, unitPrice = 100m } },
        });
        (goods.GetProperty("totalTax").GetDecimal(), goods.GetProperty("totalGross").GetDecimal(), goods.GetProperty("totalPayable").GetDecimal()).ShouldBe((4_500m, 34_900m, 34_900m));
        goods.GetProperty("lines")[1].GetProperty("taxCode").GetString().ShouldBe("SA-E", "the order's chosen code carries to the invoice");
        (await RoleAsync(s, "InputTax")).ShouldBe(4_500m);
        (await QueryAsync<decimal>(s, "SELECT tax_base_tc FROM app.gl_journal_lines WHERE tenant_id = @t AND company_id = @c AND account_role = 'InputTax'")).ShouldBe(30_000m);
        (await QueryAsync<long>(s, "SELECT count(*) FROM app.tax_entries WHERE tenant_id = @t AND company_id = @c AND source_document_id = @r", goods.GetProperty("id").GetGuid())).ShouldBe(2L, "zero-tax lines are in the ledger too: the return reports their base");

        // Imported software support: reverse charge. The supplier is paid 2,000 dollars; 15% is self-assessed both ways.
        var support = await PostInvoiceAsync(owner, new
        {
            companyId = s.CompanyId,
            partnerId = s.Foreign,
            kind = "expense",
            supplierInvoiceNumber = "DS-2026-118",
            documentDate = "2026-09-10",
            postingDate = "2026-09-10",
            applyWht = false,
            lines = new[] { new { kind = "expense", description = "Software support, September", quantity = 1m, unitPrice = 2_000m } },
        });
        (support.GetProperty("totalTax").GetDecimal(), support.GetProperty("totalReverseChargeTax").GetDecimal(), support.GetProperty("totalPayable").GetDecimal()).ShouldBe((0m, 300m, 2_000m));
        var supportLine = support.GetProperty("lines").Only();
        (supportLine.GetProperty("taxCode").GetString(), supportLine.GetProperty("taxReverseCharge").GetBoolean()).ShouldBe(("SA-RC", true));
        (await RoleAsync(s, "InputTax")).ShouldBe(4_500m + 1_125m);
        (await RoleAsync(s, "OutputTax")).ShouldBe(-1_125m);

        // Entertainment: KSA does not let the company recover this VAT, so it is part of the expense.
        var nonRecoverable = await owner.PostAsync($"/api/v1/tax/regimes/{s.RegimeId}/codes", new
        {
            code = "SA-NR",
            name = Name("Standard rate, not recoverable", "النسبة الأساسية، غير قابلة للاسترداد"),
            kind = "vat",
            treatment = "standard",
            isRecoverable = false,
            rates = new[] { new { validFrom = "2020-07-01", ratePct = 15m } },
            purchaseBaseBox = "7",
        });
        var dinner = await PostInvoiceAsync(owner, new
        {
            companyId = s.CompanyId,
            partnerId = s.Domestic,
            kind = "expense",
            supplierInvoiceNumber = "RIT-7790",
            documentDate = "2026-09-12",
            postingDate = "2026-09-12",
            applyWht = false,
            lines = new[] { new { kind = "expense", description = "Client dinner", quantity = 1m, unitPrice = 1_000m, accountRole = "PurchaseExpense", taxCodeId = nonRecoverable.GetProperty("id").GetGuid() } },
        });
        (dinner.GetProperty("totalTax").GetDecimal(), dinner.GetProperty("totalPayable").GetDecimal()).ShouldBe((150m, 1_150m));
        (await QueryAsync<decimal>(s, "SELECT sum(l.debit_fc - l.credit_fc) FROM app.gl_journal_lines l JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = 'PurchaseExpense' AND e.source_document_id = @r", dinner.GetProperty("id").GetGuid())).ShouldBe(1_150m);
        (await RoleAsync(s, "InputTax")).ShouldBe(4_500m + 1_125m, "no input tax for what cannot be recovered");

        // Goods bought on that code: the VAT the company cannot recover is part of what the stock cost (IAS 2), and the
        // receipt's goods-received-not-invoiced balance still clears to nothing.
        var inventoryBefore = await RoleAsync(s, "Inventory");
        var giftOrder = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Domestic, warehouseId = s.WarehouseId, orderDate = "2026-09-12", lines = new object[] { new { itemId = s.Laptop, quantity = 2m, uom = "PCS", unitPrice = 3_000m, taxCodeId = nonRecoverable.GetProperty("id").GetGuid() } } });
        var giftOrderId = giftOrder.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/orders/{giftOrderId}/submit", new { }, HttpStatusCode.OK);
        var giftReceipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId = giftOrderId, postingDate = "2026-09-13", lines = new[] { new { orderLineId = giftOrder.GetProperty("lines").Only().GetProperty("id").GetGuid(), quantity = 2m } } });
        var giftReceiptId = giftReceipt.GetProperty("id").GetGuid();
        var giftReceiptLine = (await owner.PostAsync($"/api/v1/purchasing/receipts/{giftReceiptId}/post", new { }, HttpStatusCode.OK)).GetProperty("lines").Only().GetProperty("id").GetGuid();
        await PostInvoiceAsync(owner, new { companyId = s.CompanyId, partnerId = s.Domestic, supplierInvoiceNumber = "RIT-7795", documentDate = "2026-09-14", postingDate = "2026-09-14", applyWht = false, lines = new[] { new { kind = "receipt", receiptLineId = giftReceiptLine, quantity = 2m, unitPrice = 3_000m } } });
        (await RoleAsync(s, "Inventory") - inventoryBefore).ShouldBe(6_900m);
        (await QueryAsync<decimal>(s, "SELECT coalesce(sum(credit_fc - debit_fc), 0) FROM app.gl_journal_lines WHERE tenant_id = @t AND company_id = @c AND subledger_type = 'GRNI' AND subledger_ref = @r", giftReceiptId)).ShouldBe(0m);

        // The ledger ties to the journal: recoverable tax to input tax, self-assessed tax to output tax.
        (await LedgerAsync(s, "is_recoverable")).ShouldBe(await RoleAsync(s, "InputTax"));
        (await LedgerAsync(s, "is_reverse_charge")).ShouldBe(-await RoleAsync(s, "OutputTax"));

        // Reversing the goods invoice takes its tax back out of both, on the reversal's date.
        await owner.PostAsync($"/api/v1/purchasing/invoices/{goods.GetProperty("id").GetGuid()}/reverse", new { reason = "Wrong supplier", reversalDate = "2026-09-15" }, HttpStatusCode.OK);
        (await RoleAsync(s, "InputTax")).ShouldBe(1_125m);
        (await LedgerAsync(s, "is_recoverable")).ShouldBe(1_125m);

        // September is filed: an invoice dated in it no longer posts, and nothing of it is written; October's does.
        await using (var db = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            await db.ExecuteAsync("""
                INSERT INTO app.tax_return_periods (tenant_id, id, company_id, regime_id, period_start, period_end, status, filed_at, reference)
                VALUES (@t, @id, @c, @r, DATE '2026-09-01', DATE '2026-09-30', 'filed', now(), 'ZATCA-2026-09')
                """, new { t = s.Ws.TenantId, id = Guid.CreateVersion7(), c = s.CompanyId, r = s.RegimeId });
        }

        var late = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Domestic, kind = "expense", supplierInvoiceNumber = "RIT-7801", documentDate = "2026-09-29", postingDate = "2026-09-29", applyWht = false, lines = new[] { new { kind = "expense", description = "Printer paper", quantity = 1m, unitPrice = 200m } } });
        var lateId = late.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/invoices/{lateId}/submit", new { }, HttpStatusCode.OK);
        var (refused, problem) = await owner.PostErrorAsync($"/api/v1/purchasing/invoices/{lateId}/post", new { }, HttpStatusCode.Conflict);
        refused.ShouldBe("tax.period_filed");
        problem.GetProperty("why").GetProperty("reference").GetString().ShouldBe("ZATCA-2026-09");
        (await QueryAsync<long>(s, "SELECT count(*) FROM app.gl_journal_entries WHERE tenant_id = @t AND company_id = @c AND source_document_id = @r", lateId)).ShouldBe(0L);
        var october = await PostInvoiceAsync(owner, new { companyId = s.CompanyId, partnerId = s.Domestic, kind = "expense", supplierInvoiceNumber = "RIT-7802", documentDate = "2026-09-29", postingDate = "2026-10-01", applyWht = false, lines = new[] { new { kind = "expense", description = "Printer paper", quantity = 1m, unitPrice = 200m } } });
        october.GetProperty("totalTax").GetDecimal().ShouldBe(30m, "the supplier's September invoice is claimed in October, the open period");

        (await LedgerAsync(s, "is_recoverable")).ShouldBe(await RoleAsync(s, "InputTax"));
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task An_item_without_a_tax_group_is_refused_once_the_company_is_registered_and_lines_without_items_take_the_default_rows()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var plain = (await owner.PostAsync("/api/v1/items", new { code = "PLAIN", name = Name("Unclassified", "غير مصنف"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var (code, problem) = await owner.PostErrorAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Domestic, warehouseId = s.WarehouseId, lines = new object[] { new { itemId = plain, quantity = 1m, uom = "PCS", unitPrice = 10m } } }, HttpStatusCode.UnprocessableEntity);
        code.ShouldBe("tax.item_unclassified");
        problem.GetProperty("why").GetProperty("line").GetString().ShouldBe("1");

        var expense = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Domestic, kind = "expense", documentDate = "2026-09-06", applyWht = false, lines = new[] { new { kind = "expense", description = "Office rent", quantity = 1m, unitPrice = 10_000m } } });
        var line = expense.GetProperty("lines").Only();
        (line.GetProperty("taxCode").GetString(), line.GetProperty("taxAmount").GetDecimal()).ShouldBe(("SA-S", 1_500m));
        expense.GetProperty("totalGross").GetDecimal().ShouldBe(11_500m);
    }
}
