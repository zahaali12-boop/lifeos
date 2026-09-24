using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Contracts;
using Quicker.Purchasing.Contracts;

namespace Quicker.Purchasing.Tests;

/// <summary>Supplier invoices (roadmap 4.4, scenario 5): three-way match against a receipt and two-way against a service line, a price breach blocked and cleared by a workflow override, posting that re-prices a partly sold receipt (Inventory and COGS), settles GRNI, withholds tax, opens payables per instalment and consumes commitments; duplicates blocked; expense invoices; reversal.</summary>
[Collection(ApiCollection.Name)]
public sealed class InvoiceTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId);

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid WarehouseId, Guid Tea, Guid Cleaning, Guid Supplier);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "INV", legalName = Name("Invoicing Co", "شركة الفوترة"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1300m });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var cleaning = (await owner.PostAsync("/api/v1/items", new { code = "CLEAN", name = Name("Office cleaning", "تنظيف المكتب"), baseUom = "HR", type = "service" })).GetProperty("id").GetGuid();
        var wht = (await owner.PostAsync("/api/v1/partners/wht-codes", new { code = "WHT5", name = Name("5 % at invoice", "٥٪ عند الفاتورة"), ratePct = 5m, withholdAt = "invoice" })).GetProperty("id").GetGuid();
        var terms = (await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "HALF", name = Name("Half now, half in 30 days", "نصف الآن ونصف بعد ٣٠ يومًا"), dueBasis = "invoice_date", dueDays = 30, lines = new[] { new { sequence = 1, percentage = 50m, days = 0 }, new { sequence = 2, percentage = 50m, days = 30 } } })).GetProperty("id").GetGuid();
        var partner = await owner.PostAsync("/api/v1/partners", new { code = "SUP-A", legalName = Name("Alpha Supplies", "ألفا"), isSupplier = true, email = "alpha@example.test" });
        var supplier = partner.GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplier}/supplier-accounts/{companyId}", new { currency = "USD", leadTimeDays = 7, qtyTolerancePct = 10m, priceTolerancePct = 5m, whtCodeId = wht, paymentTermsId = terms });
        return new Setup(ws, owner, companyId, main.GetProperty("id").GetGuid(), tea, cleaning, supplier);
    }

    private async Task<Member> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return new Member(Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), invited.GetProperty("membershipId").GetGuid());
    }

    [Fact]
    public async Task Receipts_returns_and_invoices_keep_their_custom_fields_as_validated()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var reference = Guid.NewGuid();
        foreach (var entityType in new[] { "purchase_receipt", "purchase_return", "purchase_invoice" })
        {
            await owner.PostAsync("/api/v1/collaboration/custom-fields", new { entityType, key = "channels", label = Name("Channels", "القنوات"), type = "multi_select", options = new[] { new { value = "email", label = Name("Email", "بريد") }, new { value = "portal", label = Name("Portal", "بوابة") } } });
            await owner.PostAsync("/api/v1/collaboration/custom-fields", new { entityType, key = "note", label = Name("Note", "ملاحظة"), type = "text" });
            await owner.PostAsync("/api/v1/collaboration/custom-fields", new { entityType, key = "contract", label = Name("Contract", "العقد"), type = "reference", rules = new { referenceType = "contract" } });
        }

        // What is stored is what the validator returns: no nulls, each option once, the id in its canonical form.
        var given = new Dictionary<string, object?> { ["channels"] = new[] { "email", "email", "portal" }, ["note"] = null, ["contract"] = reference.ToString().ToUpperInvariant() };
        static void Kept(System.Text.Json.JsonElement document)
        {
            var fields = document.GetProperty("customFields");
            fields.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["channels", "contract"]);
            fields.GetProperty("channels").EnumerateArray().Select(static c => c.GetString()).ShouldBe(["email", "portal"]);
        }

        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Supplier, warehouseId = s.WarehouseId, lines = new object[] { new { itemId = s.Tea, quantity = 10m, uom = "PCS", unitPrice = 2m } } });
        var orderId = order.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK);
        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-10", lines = new[] { new { orderLineId = order.GetProperty("lines").Only().GetProperty("id").GetGuid(), quantity = 10m } }, customFields = given });
        Kept(receipt);
        receipt.GetProperty("customFields").GetProperty("contract").GetString().ShouldBe(reference.ToString());
        var posted = await owner.PostAsync($"/api/v1/purchasing/receipts/{receipt.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        Kept(posted);

        var ret = await owner.PostAsync("/api/v1/purchasing/returns", new { receiptId = receipt.GetProperty("id").GetGuid(), postingDate = "2026-09-11", reason = "Damaged", lines = new[] { new { receiptLineId = posted.GetProperty("lines").Only().GetProperty("id").GetGuid(), quantity = 1m } }, customFields = given });
        Kept(ret);

        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", supplierInvoiceNumber = "CF-1", applyWht = false, lines = new[] { new { kind = "expense", description = "Courier", quantity = 1m, unitPrice = 50m } }, customFields = given });
        Kept(invoice);
        Kept(await owner.GetOkAsync($"/api/v1/purchasing/invoices/{invoice.GetProperty("id").GetGuid()}"));
    }

    private async Task<decimal> BookedAsync(Setup s, string sql, object? extra = null)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        return await db.ExecuteScalarAsync<decimal>(sql, new { t = s.Ws.TenantId, c = s.CompanyId, r = (extra as Guid?) ?? Guid.Empty });
    }

    private Task<decimal> RoleAsync(Setup s, string role) => BookedAsync(s, $"SELECT coalesce(sum(l.debit_fc - l.credit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.account_role = '{role}'");

    private Task<decimal> GrniAsync(Setup s, Guid receiptId) => BookedAsync(s, "SELECT coalesce(sum(l.credit_fc - l.debit_fc), 0) FROM app.gl_journal_lines l WHERE l.tenant_id = @t AND l.company_id = @c AND l.subledger_type = 'GRNI' AND l.subledger_ref = @r", receiptId);

    [Fact]
    public async Task An_invoice_is_matched_blocked_on_price_cleared_by_an_override_posted_with_the_receipt_repriced_and_reversed()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var finance = await InviteAsync(owner, s.Ws, "finance", "workflow.request.read", "purchasing.invoice.read");

        // An order with a stock item (received) and a service (invoiced without a receipt).
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = s.CompanyId, partnerId = s.Supplier, warehouseId = s.WarehouseId, lines = new object[] { new { itemId = s.Tea, quantity = 10m, uom = "PCS", unitPrice = 2m, discountPct = 10m }, new { itemId = s.Cleaning, quantity = 2m, uom = "HR", unitPrice = 30m, discountPct = 0m } } });
        var orderId = order.GetProperty("id").GetGuid();
        var teaLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Tea).GetProperty("id").GetGuid();
        var cleaningLine = order.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("itemId").GetGuid() == s.Cleaning).GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/purchasing/orders/{orderId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        var receipt = await owner.PostAsync("/api/v1/purchasing/receipts", new { orderId, postingDate = "2026-09-10", lines = new[] { new { orderLineId = teaLine, quantity = 10m } } });
        var receiptId = receipt.GetProperty("id").GetGuid();
        var posted = await owner.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/post", new { }, HttpStatusCode.OK);
        posted.GetProperty("totalExpectedCost").GetDecimal().ShouldBe(23400m); // 10 × 1.80 × 1300
        var receiptLine = posted.GetProperty("lines").Only().GetProperty("id").GetGuid();

        // Four of the ten are sold before the invoice arrives, at the expected cost.
        await host.InTenantAsync(s.Ws.TenantId, async (sp, ct) =>
        {
            var shipped = await sp.GetRequiredService<IInventoryPosting>().PostAsync(new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 15), "test_document", Guid.CreateVersion7(), [new StockLine(s.Tea, StockEntryTypes.SaleShipment, 4m, s.WarehouseId)]), ct);
            shipped.IsSuccess.ShouldBeTrue(shipped.Error?.Code);
            return shipped.Value;
        });
        (await RoleAsync(s, "Cogs")).ShouldBe(9360m);

        // What the supplier can invoice: the receipt line and the service line.
        var invoicable = await owner.GetOkAsync($"/api/v1/purchasing/invoices/invoicable?companyId={s.CompanyId}&partnerId={s.Supplier}");
        invoicable.GetArrayLength().ShouldBe(2);
        invoicable.EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "receipt").GetProperty("remaining").GetDecimal().ShouldBe(10m);
        invoicable.EnumerateArray().Single(l => l.GetProperty("kind").GetString() == "order").GetProperty("unitPrice").GetDecimal().ShouldBe(30m);

        // The invoice at 2.00 instead of 1.80 (+11.1 %) with the service at the agreed price; WHT 5 % at invoice; two instalments.
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, currency = "IQD", lines = new[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 10m, unitPrice = 2600m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.currency_mismatch");
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, lines = new[] { new { kind = "order", orderLineId = teaLine, quantity = 10m, unitPrice = 2m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.stock_line_needs_receipt");
        var invoice = await owner.PostAsync("/api/v1/purchasing/invoices", new
        {
            companyId = s.CompanyId,
            partnerId = s.Supplier,
            supplierInvoiceNumber = "INV-778",
            documentDate = "2026-09-20",
            lines = new object[] { new { kind = "receipt", receiptLineId = receiptLine, quantity = 10m, unitPrice = 2m }, new { kind = "order", orderLineId = cleaningLine, quantity = 2m, unitPrice = 30m } },
        });
        var invoiceId = invoice.GetProperty("id").GetGuid();
        invoice.GetProperty("number").GetString().ShouldStartWith("PI-2026-");
        invoice.GetProperty("currency").GetString().ShouldBe("USD");
        invoice.GetProperty("totalNet").GetDecimal().ShouldBe(80m);
        invoice.GetProperty("totalWht").GetDecimal().ShouldBe(4m);
        invoice.GetProperty("totalPayable").GetDecimal().ShouldBe(76m);
        invoice.GetProperty("whtCode").GetString().ShouldBe("WHT5");
        invoice.GetProperty("paymentTermsCode").GetString().ShouldBe("HALF");
        invoice.GetProperty("dueDate").GetString().ShouldBe("2026-09-20");

        // Submitted: the price breach blocks it; nothing routes the block yet.
        var blocked = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK);
        blocked.GetProperty("status").GetString().ShouldBe("blocked");
        blocked.GetProperty("blockKind").GetString().ShouldBe("price_variance");
        var match = blocked.GetProperty("matches")[0];
        match.GetProperty("status").GetString().ShouldBe("price_variance");
        match.GetProperty("priceVarianceAmount").GetDecimal().ShouldBe(2m);
        match.GetProperty("details")[0].GetProperty("priceVariancePct").GetDecimal().ShouldBe(11.111111m);
        (await owner.PostErrorAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("invoice.not_approved");

        // A definition routes price-variance blocks to finance; the block is raised again, approved with a comment, and the override lets the invoice through.
        var definition = await owner.PostAsync("/api/v1/workflow/definitions", new
        {
            entityType = "purchase_invoice",
            trigger = "on_block",
            blockKind = "price_variance",
            name = Name("Price variance overrides", "تجاوز فروق الأسعار"),
            overrideValidHours = 48,
            rules = new[] { new { name = Name("Any breach", "أي تجاوز"), condition = "priceVariancePct > 0", steps = new[] { new { name = Name("Finance", "المالية"), approverKind = "users", approvers = new { membershipIds = new[] { finance.MembershipId } }, mode = "any", requireComment = true } } } },
        });
        await owner.PostAsync($"/api/v1/workflow/definitions/{definition.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK);
        var pending = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK);
        pending.GetProperty("status").GetString().ShouldBe("blocked");
        var blocks = await owner.GetOkAsync($"/api/v1/workflow/blocks?entityType=purchase_invoice&entityId={invoiceId}");
        var requestId = blocks.EnumerateArray().Single(b => b.GetProperty("status").GetString() == "pending").GetProperty("requestId").GetGuid();
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Market price moved; accept." }, HttpStatusCode.OK);
        var approved = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/submit", new { }, HttpStatusCode.OK);
        approved.GetProperty("status").GetString().ShouldBe("approved");
        approved.GetProperty("matches")[0].GetProperty("overrideId").ValueKind.ShouldBe(JsonValueKind.String);

        // Posted: the receipt re-priced to 2.00 × 1300 (the 4 sold move COGS by 4 × 260), GRNI settled, WHT and AP booked, two open items.
        var postedInvoice = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/post", new { }, HttpStatusCode.OK);
        postedInvoice.GetProperty("status").GetString().ShouldBe("posted");
        postedInvoice.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        var openItems = postedInvoice.GetProperty("openItems").EnumerateArray().ToList();
        openItems.Count.ShouldBe(2);
        openItems[0].GetProperty("originalTc").GetDecimal().ShouldBe(38m);
        openItems[0].GetProperty("originalFc").GetDecimal().ShouldBe(49400m);
        openItems[0].GetProperty("dueDate").GetString().ShouldBe("2026-09-20");
        openItems[1].GetProperty("dueDate").GetString().ShouldBe("2026-10-20");
        (await RoleAsync(s, "Cogs")).ShouldBe(10400m);
        (await RoleAsync(s, "Inventory")).ShouldBe(15600m);
        (await RoleAsync(s, "GRNI")).ShouldBe(0m);
        (await GrniAsync(s, receiptId)).ShouldBe(0m);
        (await RoleAsync(s, "PurchaseExpense")).ShouldBe(78000m);
        (await RoleAsync(s, "WhtPayable")).ShouldBe(-5200m);
        (await RoleAsync(s, "AP")).ShouldBe(-98800m);
        (await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPurchaseReceiptDirectory>().OpenLinesAsync(s.CompanyId, s.Supplier, null, ct))).ShouldBeEmpty();
        var orderAfter = await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}");
        orderAfter.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == cleaningLine).GetProperty("qtyInvoiced").GetDecimal().ShouldBe(2m);
        orderAfter.GetProperty("commitments").EnumerateArray().Select(static c => c.GetProperty("status").GetString()).ShouldAllBe(static st => st == "consumed");
        (await owner.GetOkAsync($"/api/v1/payables/open-items?companyId={s.CompanyId}&partnerId={s.Supplier}")).GetArrayLength().ShouldBe(2);
        await owner.AssertInvariantsAsync();

        // The same supplier reference again is a duplicate suspect; an expense invoice posts to its account.
        var duplicate = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", supplierInvoiceNumber = "inv-778 ", lines = new[] { new { kind = "expense", description = "Delivery surcharge", quantity = 1m, unitPrice = 5m } } });
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{duplicate.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK)).GetProperty("blockKind").GetString().ShouldBe("duplicate_suspect");
        await owner.DeleteOkAsync($"/api/v1/purchasing/invoices/{duplicate.GetProperty("id").GetGuid()}");
        (await owner.PostErrorAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", lines = new[] { new { kind = "expense", accountRole = "AP", description = "Wrong", quantity = 1m, unitPrice = 5m } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.control_role");
        var expense = await owner.PostAsync("/api/v1/purchasing/invoices", new { companyId = s.CompanyId, partnerId = s.Supplier, kind = "expense", supplierInvoiceNumber = "RENT-09", applyWht = false, lines = new[] { new { kind = "expense", description = "September rent", quantity = 1m, unitPrice = 500m } } });
        var expenseId = expense.GetProperty("id").GetGuid();
        expense.GetProperty("totalWht").GetDecimal().ShouldBe(0m);
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{expenseId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        (await owner.PostAsync($"/api/v1/purchasing/invoices/{expenseId}/post", new { }, HttpStatusCode.OK)).GetProperty("openItems").GetArrayLength().ShouldBe(2);
        (await RoleAsync(s, "PurchaseExpense")).ShouldBe(78000m + 650000m);
        (await RoleAsync(s, "AP")).ShouldBe(-98800m - 650000m);

        // Reversal: the journal mirrored, the receipt priced back to the expected cost (COGS back too), GRNI open again, payables closed.
        (await owner.PostErrorAsync($"/api/v1/purchasing/invoices/{invoiceId}/reverse", new { reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("invoice.reason_required");
        var reversed = await owner.PostAsync($"/api/v1/purchasing/invoices/{invoiceId}/reverse", new { reason = "Supplier re-issued the invoice" }, HttpStatusCode.OK);
        reversed.GetProperty("status").GetString().ShouldBe("reversed");
        reversed.GetProperty("openItems").EnumerateArray().Select(static o => o.GetProperty("status").GetString()).ShouldAllBe(static st => st == "reversed");
        (await GrniAsync(s, receiptId)).ShouldBe(23400m);
        (await RoleAsync(s, "Cogs")).ShouldBe(9360m);
        (await RoleAsync(s, "AP")).ShouldBe(-650000m);
        (await RoleAsync(s, "WhtPayable")).ShouldBe(0m);
        (await host.InTenantAsync(s.Ws.TenantId, (sp, ct) => sp.GetRequiredService<IPurchaseReceiptDirectory>().OpenLinesAsync(s.CompanyId, s.Supplier, null, ct))).ShouldHaveSingleItem().QtyInvoiced.ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/purchasing/orders/{orderId}")).GetProperty("commitments").EnumerateArray().Select(static c => c.GetProperty("status").GetString()).ShouldAllBe(static st => st == "open");
        await owner.AssertInvariantsAsync();
    }
}
