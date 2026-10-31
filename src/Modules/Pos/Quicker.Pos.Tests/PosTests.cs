using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Pos.Tests;

/// <summary>
/// The point of sale (founder programme item 7, A-180): a register set up on a warehouse and a cash box, the catalogue
/// a till caches, a session opened with a float; orders synced from the till are posted on arrival (revenue, output
/// tax, cash rounding, the cash box, the card account, a customer's account), move the stock with the cost of sales,
/// are answered again when sent twice, and are refused when the receipt's total or a price disagrees with the server;
/// a refund gives goods back against the sale; the session closes with the counted cash and its difference booked;
/// the receipt prints; a Saudi company's live device reports each receipt as a simplified invoice with its QR.
/// The harness runs after each scenario.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PosTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid Warehouse, Guid Cash, Guid Bank, Guid Water, Guid Coffee, Guid Cleaning, Guid WalkIn, Guid Customer, Guid StandardCode);

    private async Task<Setup> SetUpAsync(string prefix = "RYD")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code = prefix, legalName = Name("Riyadh Retail", "الرياض للتجزئة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh", costingMethod = "average", registrationNumbers = new { crn = "1010010000" }, address = new { street = "King Fahd Road", buildingNumber = "1234", district = "Al Olaya", city = "Riyadh", postalCode = "12211" } })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PutAsync($"/api/v1/organization/companies/{companyId}/currencies", new { currency = "SAR", cashRoundingIncrement = 0.05m });
        var regime = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId, regimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        var standardCode = regime.GetProperty("codes").EnumerateArray().Single(static c => c.GetProperty("code").GetString() == "SA-S").GetProperty("id").GetGuid();

        var warehouse = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "SHOP", name = Name("Shop floor", "صالة البيع") })).GetProperty("id").GetGuid();
        var cash = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "TILL", name = Name("Till cash", "نقد الصندوق"), kind = "cash", currency = "SAR" })).GetProperty("id").GetGuid();
        var bank = (await owner.PostAsync("/api/v1/banking/bank-accounts", new { companyId, code = "CARDS", name = Name("Card acquirer", "مستحصل البطاقات"), kind = "bank", currency = "SAR" })).GetProperty("id").GetGuid();

        var goods = Group("item", "STANDARD");
        var water = (await owner.PostAsync("/api/v1/items", new { code = "WATER", name = Name("Mineral water 500 ml", "مياه معدنية 500 مل"), baseUom = "PCS", type = "stock", listPrice = 2.30m, listPriceCurrency = "SAR", itemTaxGroupId = goods, barcodes = new[] { new { barcode = "6291041500213" } } })).GetProperty("id").GetGuid();
        var coffee = (await owner.PostAsync("/api/v1/items", new { code = "COFFEE", name = Name("Ground coffee 250 g", "قهوة مطحونة 250 غ"), baseUom = "PCS", type = "stock", listPrice = 24m, listPriceCurrency = "SAR", itemTaxGroupId = goods })).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Shoe cleaning", "تنظيف أحذية"), baseUom = "PCS", type = "service", listPrice = 50m, listPriceCurrency = "SAR", itemTaxGroupId = Group("item", "SERVICES") })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "موجود"), appliesTo = "adjustment" });
        var opening = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId, warehouseId = warehouse, kind = "opening", postingDate = "2026-09-01", lines = new object[] { new { itemId = water, quantity = 100m, unitCost = 1m, reasonCode = "FOUND" }, new { itemId = coffee, quantity = 50m, unitCost = 15m, reasonCode = "FOUND" } } });
        await owner.PostAsync($"/api/v1/inventory/adjustments/{opening.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);

        var walkIn = (await owner.PostAsync("/api/v1/partners", new { code = "WALKIN", legalName = Name("Walk-in customer", "عميل نقدي"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{walkIn}/customer-accounts/{companyId}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC_UNREG") });
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "DAMMAM-CO", legalName = Name("Dammam Co", "شركة الدمام"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{customer}/customer-accounts/{companyId}", new { currency = "SAR", taxGroupId = Group("partner", "DOMESTIC") });
        return new Setup(ws, owner, companyId, warehouse, cash, bank, water, coffee, cleaning, walkIn, customer, standardCode);
    }

    private async Task<T> QueryAsync<T>(Setup s, string sql, object? extra = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<T>(sql, new { t = s.Ws.TenantId, c = s.CompanyId, r = (extra as Guid?) ?? Guid.Empty }) ?? default!;
    }

    private Task<decimal> RoleAsync(Setup s, string role) => QueryAsync<decimal>(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = '{role}'");

    private Task<decimal> OnHandAsync(Setup s, Guid itemId) => QueryAsync<decimal>(s, "SELECT coalesce(sum(on_hand), 0) FROM app.inv_stock_balances WHERE tenant_id = @t AND company_id = @c AND item_id = @r", itemId);

    private static object Order(Guid registerId, Guid sessionId, string deviceOrderId, decimal totalDue, object[] lines, object[] payments, decimal changeGiven = 0m, string kind = "sale", string? refunds = null, Guid? partnerId = null) =>
        new { registerId, sessionId, deviceOrderId, orderedAt = "2026-09-22T10:15:00+03:00", lines, payments, totalDue, kind, refundsDeviceOrderId = refunds, partnerId, changeGiven };

    [Fact]
    public async Task A_till_sells_for_cash_card_and_on_account_refunds_against_the_sale_and_the_session_closes_with_its_difference()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;

        // The register: code from its name, the walk-in customer, prices as the catalogue says, card and credit allowed.
        var register = await owner.PostAsync("/api/v1/pos/registers", new { companyId = s.CompanyId, name = Name("Front till", "الصندوق الأمامي"), warehouseId = s.Warehouse, cashAccountId = s.Cash, cardAccountId = s.Bank, defaultPartnerId = s.WalkIn, allowCredit = true });
        var registerId = register.GetProperty("id").GetGuid();
        register.GetProperty("code").GetString().ShouldBe("FRONT-TILL");
        register.GetProperty("cashRoundingIncrement").GetDecimal().ShouldBe(0.05m, "the company's setting for SAR applies when the register names none");
        (await owner.PostErrorAsync("/api/v1/pos/registers", new { companyId = s.CompanyId, name = Name("Bank till", "صندوق بنكي"), warehouseId = s.Warehouse, cashAccountId = s.Bank }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("register.cash_account_kind");

        // The catalogue the till caches: every sellable item in its sales unit, priced and taxed, with its barcodes.
        var catalogue = await owner.GetOkAsync($"/api/v1/pos/registers/{registerId}/catalogue");
        catalogue.GetProperty("currency").GetString().ShouldBe("SAR");
        catalogue.GetProperty("cashRoundingIncrement").GetDecimal().ShouldBe(0.05m);
        var items = catalogue.GetProperty("items").EnumerateArray().ToDictionary(static i => i.GetProperty("code").GetString()!);
        items["WATER"].GetProperty("unitPrice").GetDecimal().ShouldBe(2.30m);
        items["WATER"].GetProperty("taxRatePct").GetDecimal().ShouldBe(15m);
        items["WATER"].GetProperty("taxCodeId").GetGuid().ShouldBe(s.StandardCode);
        items["WATER"].GetProperty("barcodes")[0].GetString().ShouldBe("6291041500213");
        items["WATER"].GetProperty("isStockItem").GetBoolean().ShouldBeTrue();
        items["CLEAN"].GetProperty("isStockItem").GetBoolean().ShouldBeFalse();

        // A session with a float; a second one on the same register is refused.
        var session = await owner.PostAsync($"/api/v1/pos/registers/{registerId}/sessions", new { openingFloat = 100m });
        var sessionId = session.GetProperty("id").GetGuid();
        session.GetProperty("number").GetString().ShouldStartWith("SES-2026-");
        session.GetProperty("status").GetString().ShouldBe("open");
        (await owner.PostErrorAsync($"/api/v1/pos/registers/{registerId}/sessions", new { openingFloat = 0m }, HttpStatusCode.Conflict)).Code.ShouldBe("session.already_open");
        (await owner.GetOkAsync($"/api/v1/pos/registers/{registerId}")).GetProperty("openSessionId").GetGuid().ShouldBe(sessionId);

        // A cash sale: 3 water at 2.30 (6.90, tax 1.04) and a coffee at 24 (tax 3.60): gross 35.54, rounded to 35.55 for cash, 50 tendered.
        var sale = Order(registerId, sessionId, "dev-1", 35.55m,
            [new { itemId = s.Water, quantity = 3m, unitPrice = 2.30m, taxCodeId = s.StandardCode }, new { itemId = s.Coffee, quantity = 1m, unitPrice = 24m, taxCodeId = s.StandardCode }],
            [new { method = "cash", amount = 50m }], changeGiven: 14.45m);
        var posted = await owner.PostAsync("/api/v1/pos/orders", sale);
        var orderId = posted.GetProperty("id").GetGuid();
        posted.GetProperty("number").GetString().ShouldStartWith("POS-2026-");
        posted.GetProperty("totalNet").GetDecimal().ShouldBe(30.90m);
        posted.GetProperty("totalTax").GetDecimal().ShouldBe(4.64m);
        posted.GetProperty("totalGross").GetDecimal().ShouldBe(35.54m);
        posted.GetProperty("rounding").GetDecimal().ShouldBe(0.01m);
        posted.GetProperty("totalDue").GetDecimal().ShouldBe(35.55m);
        posted.GetProperty("changeGiven").GetDecimal().ShouldBe(14.45m);
        posted.GetProperty("journalEntryId").GetGuid().ShouldNotBe(Guid.Empty);
        posted.GetProperty("stockPostingId").GetGuid().ShouldNotBe(Guid.Empty);
        posted.GetProperty("einvoiceStatus").GetString().ShouldBe("pending", "the Saudi regime names ZATCA; the submission waits for a device");
        posted.GetProperty("replayed").GetBoolean().ShouldBeFalse();
        (await RoleAsync(s, "Revenue")).ShouldBe(-30.90m);
        (await RoleAsync(s, "OutputTax")).ShouldBe(-4.64m);
        (await RoleAsync(s, "RoundingDifferences")).ShouldBe(-0.01m);
        (await RoleAsync(s, "Cash")).ShouldBe(35.55m);
        (await RoleAsync(s, "Cogs")).ShouldBe(18m, "3 water at 1 and a coffee at 15");
        (await OnHandAsync(s, s.Water)).ShouldBe(97m);
        (await OnHandAsync(s, s.Coffee)).ShouldBe(49m);

        // The same order sent again (a sync cut off before the answer arrived) is answered with the order already posted.
        var again = await owner.PostAsync("/api/v1/pos/orders", sale, HttpStatusCode.OK);
        again.GetProperty("id").GetGuid().ShouldBe(orderId);
        again.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        (await RoleAsync(s, "Cash")).ShouldBe(35.55m);

        // A stale catalogue shows in the receipt's total; a price the register does not allow to change is refused.
        (await owner.PostErrorAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-2", 35.50m, [new { itemId = s.Water, quantity = 3m, unitPrice = 2.30m, taxCodeId = s.StandardCode }, new { itemId = s.Coffee, quantity = 1m, unitPrice = 24m, taxCodeId = s.StandardCode }], [new { method = "cash", amount = 35.50m }]), HttpStatusCode.Conflict)).Code.ShouldBe("pos.totals_mismatch");
        (await owner.PostErrorAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-3", 1.15m, [new { itemId = s.Water, quantity = 1m, unitPrice = 1m, taxCodeId = s.StandardCode }], [new { method = "cash", amount = 1.15m }]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pos.price_override_not_allowed");

        // Card and cash together: no cash rounding; the card goes to the acquirer account, the cash to the till.
        var split = await owner.PostAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-4", 27.60m, [new { itemId = s.Coffee, quantity = 1m, unitPrice = 24m, taxCodeId = s.StandardCode }], [new { method = "card", amount = 20m, reference = "AUTH-7781" }, new { method = "cash", amount = 7.60m }]));
        split.GetProperty("rounding").GetDecimal().ShouldBe(0m);
        split.GetProperty("payments").GetArrayLength().ShouldBe(2);
        (await RoleAsync(s, "Bank")).ShouldBe(20m);
        (await RoleAsync(s, "Cash")).ShouldBe(43.15m);

        // On account: the customer's receivable opens for the gross; a nameless credit sale is refused.
        var credit = await owner.PostAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-5", 57.50m, [new { itemId = s.Cleaning, quantity = 1m, unitPrice = 50m, taxCodeId = s.StandardCode }], [new { method = "credit", amount = 57.50m }], partnerId: s.Customer));
        credit.GetProperty("payments")[0].GetProperty("receivableItemId").GetGuid().ShouldNotBe(Guid.Empty);
        credit.GetProperty("stockPostingId").ValueKind.ShouldBe(JsonValueKind.Null, "a service moves no stock");
        (await RoleAsync(s, "AR")).ShouldBe(57.50m);
        var open = await owner.GetOkAsync($"/api/v1/receivables/open-items?companyId={s.CompanyId}&partnerId={s.Customer}");
        open.EnumerateArray().Single().GetProperty("item").GetProperty("documentType").GetString().ShouldBe("pos_order");
        (await owner.PostErrorAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-6", 57.50m, [new { itemId = s.Cleaning, quantity = 1m, unitPrice = 50m, taxCodeId = s.StandardCode }], [new { method = "credit", amount = 57.50m }]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("pos.customer_required");

        // A refund against the first sale: one water back at the sale's price (2.30 + 0.35 = 2.65, no rounding needed), cash out; more than was sold is refused.
        var refund = await owner.PostAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-7", 2.65m, [new { itemId = s.Water, quantity = 1m, unitPrice = 2.30m, taxCodeId = s.StandardCode }], [new { method = "cash", amount = 2.65m }], kind: "refund", refunds: "dev-1"));
        refund.GetProperty("kind").GetString().ShouldBe("refund");
        refund.GetProperty("refundsOrderId").GetGuid().ShouldBe(orderId);
        (await OnHandAsync(s, s.Water)).ShouldBe(98m);
        (await RoleAsync(s, "Cash")).ShouldBe(40.50m);
        (await RoleAsync(s, "Revenue")).ShouldBe(-30.90m - 24m - 50m + 2.30m);
        (await owner.PostErrorAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-8", 7.95m, [new { itemId = s.Water, quantity = 3m, unitPrice = 2.30m, taxCodeId = s.StandardCode }], [new { method = "cash", amount = 7.95m }], kind: "refund", refunds: "dev-1"), HttpStatusCode.Conflict)).Code.ShouldBe("pos.refund_exceeds_sale");

        // The receipt prints from the shipped template.
        var receipt = await owner.GetTextAsync($"/api/v1/print/preview?documentType=pos_order&documentId={orderId}");
        receipt.ShouldContain(posted.GetProperty("number").GetString()!);
        receipt.ShouldContain("35.55");
        receipt.ShouldContain("Mineral water 500 ml");

        // The session: takings by tender, then closed with one riyal short, booked on the till against suspense.
        var before = await owner.GetOkAsync($"/api/v1/pos/sessions/{sessionId}");
        before.GetProperty("ordersCount").GetInt32().ShouldBe(4);
        before.GetProperty("cashTaken").GetDecimal().ShouldBe(40.50m);
        before.GetProperty("cardTaken").GetDecimal().ShouldBe(20m);
        before.GetProperty("creditTaken").GetDecimal().ShouldBe(57.50m);
        var closed = await owner.PostAsync($"/api/v1/pos/sessions/{sessionId}/close", new { countedCash = 139.50m, notes = "One riyal short" }, HttpStatusCode.OK);
        closed.GetProperty("status").GetString().ShouldBe("closed");
        closed.GetProperty("expectedCash").GetDecimal().ShouldBe(140.50m);
        closed.GetProperty("difference").GetDecimal().ShouldBe(-1m);
        closed.GetProperty("differenceJournalEntryId").GetGuid().ShouldNotBe(Guid.Empty);
        (await RoleAsync(s, "Cash")).ShouldBe(39.50m);
        (await RoleAsync(s, "Suspense")).ShouldBe(1m);
        (await owner.PostErrorAsync("/api/v1/pos/orders", Order(registerId, sessionId, "dev-9", 2.65m, [new { itemId = s.Water, quantity = 1m, unitPrice = 2.30m, taxCodeId = s.StandardCode }], [new { method = "cash", amount = 2.65m }]), HttpStatusCode.Conflict)).Code.ShouldBe("pos.session_closed");
        (await owner.PostErrorAsync($"/api/v1/pos/sessions/{sessionId}/close", new { countedCash = 1m }, HttpStatusCode.Conflict)).Code.ShouldBe("session.not_open");

        var orders = await owner.GetOkAsync($"/api/v1/pos/orders?sessionId={sessionId}");
        orders.GetArrayLength().ShouldBe(4);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_live_device_reports_each_receipt_as_a_simplified_invoice_with_its_qr()
    {
        var s = await SetUpAsync("JED");
        var owner = s.Owner;
        var device = await owner.PostAsync("/api/v1/einvoicing/devices", new { companyId = s.CompanyId, name = "Till device", industry = "Retail" });
        var deviceId = device.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-csid", new { otp = "123456" }, HttpStatusCode.OK);
        await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/compliance-checks", new { }, HttpStatusCode.OK);
        (await owner.PostAsync($"/api/v1/einvoicing/devices/{deviceId}/production-csid", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("active");

        var registerId = (await owner.PostAsync("/api/v1/pos/registers", new { companyId = s.CompanyId, code = "T1", name = Name("Till 1", "صندوق 1"), warehouseId = s.Warehouse, cashAccountId = s.Cash, defaultPartnerId = s.WalkIn })).GetProperty("id").GetGuid();
        var sessionId = (await owner.PostAsync($"/api/v1/pos/registers/{registerId}/sessions", new { openingFloat = 0m })).GetProperty("id").GetGuid();
        var posted = await owner.PostAsync("/api/v1/pos/orders", Order(registerId, sessionId, "jed-1", 27.60m, [new { itemId = s.Coffee, quantity = 1m, unitPrice = 24m, taxCodeId = s.StandardCode }], [new { method = "cash", amount = 30m }], changeGiven: 2.40m));
        posted.GetProperty("einvoiceStatus").GetString().ShouldBe("pending");

        // The worker does not run under test: the submission is made by hand, as the back office would.
        var reported = await owner.PostAsync($"/api/v1/pos/orders/{posted.GetProperty("id").GetGuid()}/einvoice", new { }, HttpStatusCode.OK);
        reported.GetProperty("einvoiceStatus").GetString().ShouldBe("reported", string.Join("; ", reported.GetProperty("einvoiceMessages").EnumerateArray().Select(static m => m.GetString())));
        reported.GetProperty("einvoiceQr").GetString().ShouldNotBeNullOrEmpty();
        var submissions = await owner.GetOkAsync($"/api/v1/einvoicing/submissions?companyId={s.CompanyId}&documentType=pos_order");
        var own = submissions.EnumerateArray().Single(e => e.GetProperty("documentNumber").GetString() == posted.GetProperty("number").GetString());
        own.GetProperty("kind").GetString().ShouldBe("simplified");
        own.GetProperty("status").GetString().ShouldBe("reported");

        // The receipt carries the QR.
        var receipt = await owner.GetTextAsync($"/api/v1/print/preview?documentType=pos_order&documentId={posted.GetProperty("id").GetGuid()}");
        receipt.ShouldContain("<svg");
        await owner.AssertInvariantsAsync();
    }
}
