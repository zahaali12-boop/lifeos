using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Purchasing.Tests;

/// <summary>
/// Documents found by number (GET /numbering/documents) over purchase orders in two companies: the exact number first,
/// a prefix with an unpadded sequence, part of a number; only the types a member may read, only in the companies their
/// grant covers; nothing from another workspace.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DocumentSearchTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static async Task<(Guid CompanyId, Guid SupplierId, Guid WarehouseId)> CompanyAsync(HttpClient owner, string code)
    {
        var companyId = (await owner.PostAsync("/api/v1/organization/companies", new { code, legalName = Name(code + " Co", code), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "CH-" + code, companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "WH-" + code, name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        var supplierId = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-" + code, legalName = Name("Supplier " + code, "مورد " + code), isSupplier = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{supplierId}/supplier-accounts/{companyId}", new { currency = "IQD", leadTimeDays = 7 });
        return (companyId, supplierId, warehouseId);
    }

    private static async Task<(Guid Id, string Number)> OrderAsync(HttpClient owner, (Guid CompanyId, Guid SupplierId, Guid WarehouseId) company, Guid itemId)
    {
        var order = await owner.PostAsync("/api/v1/purchasing/orders", new { companyId = company.CompanyId, partnerId = company.SupplierId, warehouseId = company.WarehouseId, lines = new[] { new { itemId, quantity = 1m, uom = "PCS", unitPrice = 1000m } } });
        return (order.GetProperty("id").GetGuid(), order.GetProperty("number").GetString()!);
    }

    private static async Task<List<JsonElement>> SearchAsync(HttpClient client, string q)
    {
        var response = await client.GetAsync(new Uri($"/api/v1/numbering/documents?q={Uri.EscapeDataString(q)}", UriKind.Relative));
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToString());
        return json.EnumerateArray().ToList();
    }

    [Fact]
    public async Task Documents_are_found_by_number_for_members_who_may_read_them_in_their_companies()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea", "شاي"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var pur = await CompanyAsync(owner, "PUR");
        var sec = await CompanyAsync(owner, "SEC");
        var first = await OrderAsync(owner, pur, tea);
        var second = await OrderAsync(owner, pur, tea);
        var third = await OrderAsync(owner, pur, tea);
        var elsewhere = await OrderAsync(owner, sec, tea);
        second.Number.ShouldEndWith("-00002");

        // The exact number comes first, with its type, id and company.
        var exact = await SearchAsync(owner, second.Number.ToLowerInvariant());
        exact[0].GetProperty("documentType").GetString().ShouldBe("purchase_order");
        exact[0].GetProperty("documentId").GetGuid().ShouldBe(second.Id);
        exact[0].GetProperty("number").GetString().ShouldBe(second.Number);
        exact[0].GetProperty("companyCode").GetString().ShouldBe("PUR");

        // A prefix and an unpadded sequence ("PO-2") put the second order first; every order also contains "PO-2…".
        var bySequence = await SearchAsync(owner, "PO-2");
        bySequence[0].GetProperty("documentId").GetGuid().ShouldBe(second.Id);
        bySequence.Select(static h => h.GetProperty("documentId").GetGuid()).ShouldBe([second.Id, elsewhere.Id, third.Id, first.Id], ignoreOrder: true);

        // Part of a number, newest first when nothing ranks higher.
        var partial = (await SearchAsync(owner, "0000")).Select(static h => h.GetProperty("documentId").GetGuid()).ToList();
        partial.ShouldBe([elsewhere.Id, third.Id, second.Id, first.Id]);

        // Too short (or too long) is refused; a limit caps the hits.
        var shortQuery = await owner.GetAsync(new Uri("/api/v1/numbering/documents?q=P", UriKind.Relative));
        shortQuery.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await shortQuery.ErrorCodeAsync()).ShouldBe("document_search.query_invalid");
        (await (await owner.GetAsync(new Uri("/api/v1/numbering/documents?q=PO-&limit=2", UriKind.Relative))).ReadJsonAsync()).GetArrayLength().ShouldBe(2);

        // A member who reads items only finds no orders; once given order reading limited to SEC, only SEC's order.
        var itemsRole = await owner.PostAsync("/api/v1/roles", new { code = "items_only", name = Name("Items", "الأصناف"), description = "", grants = new[] { "inventory.item.read" } });
        var email = $"clerk-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "clerk", roleIds = new[] { itemsRole.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        using var clerk = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await SearchAsync(clerk, "PO-")).ShouldBeEmpty();

        var ordersRole = await owner.PostAsync("/api/v1/roles", new { code = "order_reader", name = Name("Orders", "الأوامر"), description = "", grants = new[] { "purchasing.order.read" } });
        await owner.PostAsync($"/api/v1/users/{invited.GetProperty("membershipId").GetGuid()}/assignments", new { roleId = ordersRole.GetProperty("id").GetGuid(), scopes = new[] { new { scopeType = "company", scopeId = sec.CompanyId } } });
        var scoped = await SearchAsync(clerk, "PO-");
        scoped.Select(static h => h.GetProperty("documentId").GetGuid()).ShouldBe([elsewhere.Id]);
        scoped[0].GetProperty("companyCode").GetString().ShouldBe("SEC");

        // Another workspace finds nothing of this one.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await SearchAsync(outsider, second.Number)).ShouldBeEmpty();
    }
}
