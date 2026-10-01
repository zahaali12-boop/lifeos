using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Inventory.Application;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;

namespace Quicker.Inventory.Tests;

/// <summary>
/// Roadmap 3.2: warehouses and bins, the stock ledger and its balances under row locks (hard scenario 4 with two-step
/// transfers as the outbound path), the negative-stock policy, reservations and availability, the stock search and
/// ledger, period control for inventory, warehouse scopes; balances equal the ledger after every scenario.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StockLedgerTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid ItemId, Guid Main, Guid Branch, Guid Transit);

    private async Task<Setup> SetUpAsync(string companyCode = "IQT")
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = companyCode, legalName = Name("Rafidain", "الرافدين"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var companyId = company.GetProperty("id").GetGuid();
        var item = await owner.PostAsync("/api/v1/items", new { code = "WATER-500", name = Name("Water 500 ml", "ماء ٥٠٠ مل"), baseUom = "PCS", uoms = new object[] { new { uom = "CTN", numerator = 24, denominator = 1 } } });
        var main = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main warehouse", "المستودع الرئيسي") });
        var branch = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "BASRA", name = Name("Basra branch", "فرع البصرة") });
        var transit = await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "TRANSIT", name = Name("In transit", "في الطريق"), kind = "in_transit" });
        return new Setup(ws, owner, companyId, item.GetProperty("id").GetGuid(), main.GetProperty("id").GetGuid(), branch.GetProperty("id").GetGuid(), transit.GetProperty("id").GetGuid());
    }

    private static object Transfer(Setup s, decimal quantity, Guid? from = null, Guid? to = null) =>
        new { companyId = s.CompanyId, fromWarehouseId = from ?? s.Main, toWarehouseId = to ?? s.Branch, lines = new object[] { new { itemId = s.ItemId, quantity } } };

    [Fact]
    public async Task Twenty_users_ship_the_last_unit_at_once_and_exactly_one_succeeds_until_negative_stock_is_allowed()
    {
        var s = await SetUpAsync();
        await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test_opening", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.Opening, 1m, s.Main)]));
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}")).Dec("available").ShouldBe(1m);

        // Twenty draft transfers of the one unit, shipped at the same moment.
        var transfers = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            transfers.Add((await s.Owner.PostAsync("/api/v1/inventory/transfers", Transfer(s, 1m))).GetProperty("id").GetGuid());
        }

        var ships = transfers.Select(async id =>
        {
            using var client = Api.ClientFor(s.Ws.AccessToken);
            var response = await client.PostAsJsonAsync($"/api/v1/inventory/transfers/{id}/ship", new { }, ApiFixture.Json);
            var json = await response.ReadJsonAsync();
            return (response.StatusCode, Code: response.IsSuccessStatusCode ? null : json.GetProperty("code").GetString(), Json: json);
        });
        var outcomes = await Task.WhenAll(ships);
        outcomes.Count(static o => o.StatusCode == HttpStatusCode.OK).ShouldBe(1, "exactly one ship of the last unit succeeds");
        outcomes.Count(static o => o.Code == "stock.insufficient").ShouldBe(19);
        var refused = outcomes.First(static o => o.Code == "stock.insufficient").Json.GetProperty("why");
        refused.GetProperty("available").GetDecimal().ShouldBe(0m);
        refused.GetProperty("requested").GetDecimal().ShouldBe(1m);
        refused.GetProperty("negativeStockAllowed").GetBoolean().ShouldBeFalse();
        var shipped = outcomes.Single(static o => o.StatusCode == HttpStatusCode.OK).Json;
        shipped.GetProperty("status").GetString().ShouldBe("shipped");
        shipped.GetProperty("number").GetString().ShouldStartWith("TRF-2026-");
        shipped.GetProperty("transitWarehouseCode").GetString().ShouldBe("TRANSIT");

        var mainStock = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}");
        mainStock.Dec("onHand").ShouldBe(0m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Transit}")).Dec("onHand").ShouldBe(1m);
        var branchStock = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Branch}");
        branchStock.Dec("onHand").ShouldBe(0m);
        branchStock.Dec("inTransit").ShouldBe(1m, "one unit is on its way to the branch");
        await s.Owner.AssertInvariantsAsync();

        // The policy allows negative stock on the main warehouse: the other nineteen ship and the balance goes to -19, still equal to the ledger.
        var main = await s.Owner.GetOkAsync($"/api/v1/inventory/warehouses/{s.Main}");
        await s.Owner.PutAsync($"/api/v1/inventory/warehouses/{s.Main}", new { companyId = s.CompanyId, code = "MAIN", name = main.GetProperty("name"), allowNegativeStock = true });
        var second = await Task.WhenAll(transfers.Select(async id =>
        {
            using var client = Api.ClientFor(s.Ws.AccessToken);
            var response = await client.PostAsJsonAsync($"/api/v1/inventory/transfers/{id}/ship", new { }, ApiFixture.Json);
            return (response.StatusCode, Code: await response.ErrorCodeAsync());
        }));
        second.Count(static o => o.StatusCode == HttpStatusCode.OK).ShouldBe(19);
        second.Count(static o => o.Code == "transfer.not_draft").ShouldBe(1, "the one already shipped");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}")).Dec("onHand").ShouldBe(-19m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Transit}")).Dec("onHand").ShouldBe(20m);
        await s.Owner.AssertInvariantsAsync();

        // The ledger: every transfer wrote a paired out/in with the same pair id; the search shows the three warehouses.
        var ledger = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/ledger?companyId={s.CompanyId}&itemId={s.ItemId}&limit=100");
        ledger.GetProperty("items").GetArrayLength().ShouldBe(41);
        var pairs = ledger.GetProperty("items").EnumerateArray().Where(static e => e.GetProperty("entryType").GetString() != "opening").GroupBy(static e => e.GetProperty("transferPairId").GetGuid()).ToList();
        pairs.Count.ShouldBe(20);
        pairs.ShouldAllBe(p => p.Sum(e => e.GetProperty("quantity").GetDecimal()) == 0m, "each pair moves the unit from one warehouse to another");
        var search = await s.Owner.GetOkAsync($"/api/v1/inventory/stock?companyId={s.CompanyId}&q=water");
        search.GetProperty("items").EnumerateArray().Select(static r => (r.GetProperty("warehouseCode").GetString(), r.GetProperty("onHand").GetDecimal())).ShouldBe([("MAIN", -19m), ("TRANSIT", 20m)]);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock?companyId={s.CompanyId}&onlyAvailable=true")).GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Reservations_hold_stock_for_a_document_until_shipped_released_or_expired_and_transfers_receive_in_parts()
    {
        var s = await SetUpAsync();
        var fixture = host;
        await fixture.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test_opening", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.Opening, 1m, s.Main, UomId: null)]));
        var cartons = (await s.Owner.GetOkAsync($"/api/v1/items/{s.ItemId}")).GetProperty("uoms").EnumerateArray().Single(static u => u.GetProperty("uomCode").GetString() == "CTN").GetProperty("uomId").GetGuid();
        await fixture.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test_opening", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.Opening, 2m, s.Main, UomId: cartons)]));
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}")).Dec("onHand").ShouldBe(49m, "one piece and two cartons of 24");

        // A sales document reserves 40 pieces; a transfer of 10 is refused (9 available), a transfer of 9 ships; the reservation is released and the rest flows.
        var order = Guid.CreateVersion7();
        var reservation = await s.Owner.PostAsync("/api/v1/inventory/reservations", new { companyId = s.CompanyId, itemId = s.ItemId, quantity = 40, warehouseId = s.Main, sourceDocumentType = "sales_order", sourceDocumentId = order, expiresOn = "2026-09-30" });
        reservation.GetProperty("status").GetString().ShouldBe("active");
        var availability = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}");
        availability.Dec("reserved").ShouldBe(40m);
        availability.Dec("available").ShouldBe(9m);
        (await s.Owner.PostErrorAsync("/api/v1/inventory/reservations", new { companyId = s.CompanyId, itemId = s.ItemId, quantity = 10, warehouseId = s.Main, sourceDocumentType = "sales_order", sourceDocumentId = Guid.CreateVersion7() }, HttpStatusCode.Conflict)).Code.ShouldBe("stock.insufficient_to_reserve");
        var ten = (await s.Owner.PostAsync("/api/v1/inventory/transfers", Transfer(s, 10m))).GetProperty("id").GetGuid();
        var (code, problem) = await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{ten}/ship", new { }, HttpStatusCode.Conflict);
        code.ShouldBe("stock.insufficient");
        problem.GetProperty("why").GetProperty("reserved").GetDecimal().ShouldBe(40m);
        var nine = (await s.Owner.PostAsync("/api/v1/inventory/transfers", Transfer(s, 9m))).GetProperty("id").GetGuid();
        (await s.Owner.PostAsync($"/api/v1/inventory/transfers/{nine}/ship", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("shipped");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/reservations?sourceDocumentType=sales_order&sourceDocumentId={order}")).GetArrayLength().ShouldBe(1);
        await s.Owner.AssertInvariantsAsync();

        // The shipment consumes the reservation through the engine (a sales shipment in M5 does exactly this); here in process.
        var reservationId = reservation.GetProperty("id").GetGuid();
        var consumed = await fixture.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 10), "sales_shipment", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.SaleShipment, 15m, s.Main, ReservationId: reservationId)]));
        consumed.Entries[0].ReservationId.ShouldBe(reservationId);
        availability = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}");
        availability.Dec("onHand").ShouldBe(25m);
        availability.Dec("reserved").ShouldBe(25m);
        availability.Dec("available").ShouldBe(0m);
        var released = await s.Owner.PostAsync($"/api/v1/inventory/reservations/{reservationId}/release", new { reason = "order cancelled" }, HttpStatusCode.OK);
        released.GetProperty("status").GetString().ShouldBe("released");
        released.Dec("consumedQuantity").ShouldBe(15m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}")).Dec("available").ShouldBe(25m);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/reservations/{reservationId}/release", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("reservation.not_active");
        await s.Owner.AssertInvariantsAsync();

        // Expiry: a reservation past its date is released by the daily job.
        var expiring = await s.Owner.PostAsync("/api/v1/inventory/reservations", new { companyId = s.CompanyId, itemId = s.ItemId, quantity = 5, warehouseId = s.Main, sourceDocumentType = "sales_order", sourceDocumentId = Guid.CreateVersion7(), expiresOn = "2026-09-15" });
        await ExpireInProcessAsync(s.Ws.TenantId, new DateOnly(2026, 9, 22));
        (await s.Owner.GetOkAsync($"/api/v1/inventory/reservations?sourceDocumentType=sales_order&sourceDocumentId={expiring.GetProperty("sourceDocumentId").GetGuid()}"))[0].GetProperty("status").GetString().ShouldBe("released");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Main}")).Dec("reserved").ShouldBe(0m);

        // The transfer of nine is received in two parts into the branch; in transit shrinks accordingly.
        var transfer = await s.Owner.GetOkAsync($"/api/v1/inventory/transfers/{nine}");
        var lineId = transfer.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var partial = await s.Owner.PostAsync($"/api/v1/inventory/transfers/{nine}/receive", new { lines = new object[] { new { lineId, quantity = 4 } } }, HttpStatusCode.OK);
        partial.GetProperty("status").GetString().ShouldBe("partially_received");
        partial.GetProperty("lines")[0].Dec("qtyReceived").ShouldBe(4m);
        var branch = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Branch}");
        branch.Dec("onHand").ShouldBe(4m);
        branch.Dec("inTransit").ShouldBe(5m);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{nine}/receive", new { lines = new object[] { new { lineId, quantity = 6 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("transfer.receive_quantity_invalid");
        var full = await s.Owner.PostAsync($"/api/v1/inventory/transfers/{nine}/receive", new { }, HttpStatusCode.OK);
        full.GetProperty("status").GetString().ShouldBe("received");
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Branch}")).Dec("onHand").ShouldBe(9m);
        (await s.Owner.GetOkAsync($"/api/v1/inventory/stock/availability?companyId={s.CompanyId}&itemId={s.ItemId}&warehouseId={s.Transit}")).Dec("onHand").ShouldBe(0m);
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{nine}/receive", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("transfer.not_shipped");
        (await s.Owner.PostAsync($"/api/v1/inventory/transfers/{ten}/cancel", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("cancelled");
        await s.Owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Bins_periods_scopes_and_the_engine_rules_are_enforced()
    {
        var s = await SetUpAsync();
        // A warehouse with bins: movements name the bin; the search and balances show it.
        var binned = await s.Owner.PostAsync("/api/v1/inventory/warehouses", new { companyId = s.CompanyId, code = "COLD", name = Name("Cold store", "المخزن البارد"), binsEnabled = true });
        var coldId = binned.GetProperty("id").GetGuid();
        var binA = await s.Owner.PostAsync($"/api/v1/inventory/warehouses/{coldId}/bins", new { code = "A-01", zone = "A", pickSequence = 1 });
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/warehouses/{coldId}/bins", new { code = "A-01" }, HttpStatusCode.Conflict)).Code.ShouldBe("bin.code_taken");
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/warehouses/{s.Main}/bins", new { code = "X" }, HttpStatusCode.Conflict)).Code.ShouldBe("warehouse.bins_disabled");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/warehouses", new { companyId = s.CompanyId, code = "MAIN", name = Name("Again", "مرة") }, HttpStatusCode.Conflict)).Code.ShouldBe("warehouse.code_taken");
        (await s.Owner.PostErrorAsync("/api/v1/inventory/warehouses", new { companyId = s.CompanyId, code = "T2", name = Name("T", "T"), kind = "in_transit", binsEnabled = true }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("warehouse.transit_no_bins");
        var binId = binA.GetProperty("id").GetGuid();
        await s.Owner.PostAsync($"/api/v1/inventory/warehouses/{coldId}/bins", new { code = "A-02", zone = "A", pickSequence = 1 });
        var listedBins = await s.Owner.GetOkAsync($"/api/v1/inventory/warehouses/{coldId}/bins");
        listedBins.EnumerateArray().Select(b => b.GetProperty("code").GetString()).ShouldBe(["A-01", "A-02"], "bins list by pick sequence then code (the scanner matches bins from this list)");
        await Should.ThrowAsync<InvalidOperationException>(() => host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test_opening", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.Opening, 5m, coldId)])));
        await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test_opening", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.Opening, 5m, coldId, BinId: binId)]));
        var balances = await s.Owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={s.CompanyId}&warehouseId={coldId}");
        balances.GetArrayLength().ShouldBe(1);
        balances[0].GetProperty("binCode").GetString().ShouldBe("A-01");
        balances[0].Dec("onHand").ShouldBe(5m);
        (await s.Owner.DeleteAsync(new Uri($"/api/v1/inventory/warehouses/{coldId}/bins/{binId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict, "a bin with stock is not deleted");

        // Engine rules: unknown type, a service item, a variant item without its variant, a foreign warehouse, a closed inventory period.
        var service = await s.Owner.PostAsync("/api/v1/items", new { code = "SVC", name = Name("Service", "خدمة"), type = "service", baseUom = "HR" });
        await Refuses(s, "stock.entry_type_invalid", new StockLine(s.ItemId, "teleport", 1m, s.Main));
        await Refuses(s, "stock.item_not_stocked", new StockLine(service.GetProperty("id").GetGuid(), StockEntryTypes.Opening, 1m, s.Main));
        await Refuses(s, "stock.quantity_invalid", new StockLine(s.ItemId, StockEntryTypes.Opening, -1m, s.Main));
        await Refuses(s, "stock.bin_not_used", new StockLine(s.ItemId, StockEntryTypes.Opening, 1m, s.Main, BinId: binId));
        var other = await SetUpAsync("USI");
        await Refuses(s, "stock.warehouse_invalid", new StockLine(s.ItemId, StockEntryTypes.Opening, 1m, other.Main));
        var periods = await s.Owner.GetOkAsync($"/api/v1/organization/companies/{s.CompanyId}/periods/resolve?date=2026-08-15&module=INV");
        await s.Owner.PutAsync($"/api/v1/organization/periods/{periods.GetProperty("periodId").GetGuid()}/states", new { companyId = s.CompanyId, modules = new[] { "INV" }, state = "hard_closed" });
        var closed = await s.Owner.PostAsync("/api/v1/inventory/transfers", Transfer(s, 1m, coldId));
        var (code, problem) = await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{closed.GetProperty("id").GetGuid()}/ship", new { shipDate = "2026-08-15" }, HttpStatusCode.Conflict);
        code.ShouldBe("period.closed");
        problem.GetProperty("why").GetProperty("module").GetString().ShouldBe("INV");
        (await s.Owner.PostErrorAsync($"/api/v1/inventory/transfers/{closed.GetProperty("id").GetGuid()}/ship", new { shipDate = "2026-09-15" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("stock.bin_required", "shipping out of a binned warehouse names the bin on the line");

        // A clerk whose transfer role is scoped to the Basra warehouse cannot ship from MAIN, but can receive into Basra.
        var roles = await s.Owner.GetOkAsync("/api/v1/roles");
        var operatorRole = roles.EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "warehouse_operator").GetProperty("id").GetGuid();
        var auditorRole = roles.EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "auditor").GetProperty("id").GetGuid();
        var email = $"clerk-{s.Ws.Slug}@example.test";
        var invited = await s.Owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Clerk", roleIds = new[] { auditorRole } });
        var membershipId = invited.GetProperty("membershipId").GetGuid();
        await s.Owner.PostAsync($"/api/v1/users/{membershipId}/assignments", new { roleId = operatorRole, scopes = new[] { new { scopeType = "warehouse", scopeId = s.Branch } } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "clerk-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        var clerk = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        await host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test_opening", Guid.CreateVersion7(), [new StockLine(s.ItemId, StockEntryTypes.Opening, 3m, s.Main)]));
        var scoped = (await s.Owner.PostAsync("/api/v1/inventory/transfers", Transfer(s, 2m))).GetProperty("id").GetGuid();
        (await clerk.PostErrorAsync($"/api/v1/inventory/transfers/{scoped}/ship", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("stock.warehouse_scope");
        (await s.Owner.PostAsync($"/api/v1/inventory/transfers/{scoped}/ship", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("shipped");
        (await clerk.PostAsync($"/api/v1/inventory/transfers/{scoped}/receive", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("received");
        (await clerk.PostErrorAsync("/api/v1/inventory/transfers", Transfer(s, 1m), HttpStatusCode.Forbidden)).Code.ShouldBe("stock.warehouse_scope");

        // Another tenant sees nothing; the harness holds.
        var stranger = Api.ClientFor((await Api.SignupAsync()).AccessToken);
        (await stranger.GetOkAsync($"/api/v1/inventory/stock?q=water")).GetProperty("items").GetArrayLength().ShouldBe(0);
        (await stranger.GetAsync(new Uri($"/api/v1/inventory/transfers/{scoped}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await s.Owner.AssertInvariantsAsync();
    }

    private async Task Refuses(Setup s, string code, StockLine line)
    {
        var failure = await Should.ThrowAsync<InvalidOperationException>(() => host.PostStockAsync(s.Ws.TenantId, new StockPostingRequest(s.CompanyId, new DateOnly(2026, 9, 1), "test", Guid.CreateVersion7(), [line])));
        failure.Message.ShouldContain(code);
    }

    private async Task ExpireInProcessAsync(Guid tenantId, DateOnly asOf)
    {
        await using var scope = Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = TenantContext.System(new TenantId(tenantId), "test-expire-" + Guid.CreateVersion7().ToString("N")[^12..]);
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);
        (await services.GetRequiredService<ReservationService>().ExpireAsync(asOf, TestContext.Current.CancellationToken)).ShouldBe(1);
        await unitOfWork.CommitAsync(TestContext.Current.CancellationToken);
    }
}
