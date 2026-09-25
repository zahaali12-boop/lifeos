using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Persistence;

namespace Quicker.Collaboration.Tests;

/// <summary>
/// What hangs off a record (comments, files, timeline, links) is shown only to members who may read the record itself,
/// by the read permission its module registered; record types nobody registered stay open to the collaboration
/// permissions alone.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RecordAccessTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private static readonly string[] Collaboration =
        ["collaboration.comment.read", "collaboration.comment.write", "collaboration.activity.read", "collaboration.attachment.read", "collaboration.attachment.manage", "collaboration.link.read", "collaboration.link.manage"];

    private ApiFixture Api => host.Api;

    private static MultipartFormDataContent Form(string entityType, Guid entityId)
    {
        var file = new ByteArrayContent("quote"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        return new MultipartFormDataContent { { new StringContent(entityType), "entityType" }, { new StringContent(entityId.ToString()), "entityId" }, { file, "file", "quote.txt" } };
    }

    [Fact]
    public async Task Comments_files_timeline_and_links_follow_the_records_read_permission()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (clerkToken, _, _) = await host.InviteAsync(ws, "clerk", Collaboration);
        using var clerk = Api.ClientFor(clerkToken);
        var (buyerToken, _, _) = await host.InviteAsync(ws, "buyer", [.. Collaboration, "purchasing.order.read"]);
        using var buyer = Api.ClientFor(buyerToken);
        var order = Guid.NewGuid();
        var memo = Guid.NewGuid();

        // The owner discusses a purchase order, attaches the quote and links it to a memo (a type no module registered).
        var comment = await (await owner.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "purchase_order", entityId = order, body = "Supplier agreed to 30 days." }, Json)).ReadJsonAsync();
        var uploaded = await owner.PostAsync("/api/v1/collaboration/attachments", Form("purchase_order", order));
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created);
        var attachment = (await uploaded.ReadJsonAsync()).GetProperty("id").GetGuid();
        var linked = await owner.PostAsJsonAsync("/api/v1/collaboration/links", new { from = new { type = "internal_memo", id = memo }, to = new { type = "purchase_order", id = order }, relation = "related" }, Json);
        linked.StatusCode.ShouldBe(HttpStatusCode.Created);
        var link = (await linked.ReadJsonAsync()).GetProperty("id").GetGuid();

        // A member who may not read purchase orders sees none of it, and cannot add to it.
        var refused = await clerk.GetAsync($"/api/v1/collaboration/comments?entityType=purchase_order&entityId={order}");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var problem = await refused.ReadJsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("record.read_forbidden");
        problem.GetProperty("why").GetProperty("permissions").GetString().ShouldBe("purchasing.order.read");
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "purchase_order", entityId = order, body = "x" }, Json)).ErrorCodeAsync()).ShouldBe("record.read_forbidden");
        (await (await clerk.GetAsync($"/api/v1/collaboration/attachments?entityType=purchase_order&entityId={order}")).ErrorCodeAsync()).ShouldBe("record.read_forbidden");
        (await (await clerk.PostAsync("/api/v1/collaboration/attachments", Form("purchase_order", order))).ErrorCodeAsync()).ShouldBe("record.read_forbidden");
        (await (await clerk.GetAsync($"/api/v1/collaboration/activities?entityType=purchase_order&entityId={order}")).ErrorCodeAsync()).ShouldBe("record.read_forbidden");
        (await (await clerk.GetAsync($"/api/v1/collaboration/links?entityType=purchase_order&entityId={order}")).ErrorCodeAsync()).ShouldBe("record.read_forbidden");
        (await clerk.GetAsync($"/api/v1/collaboration/attachments/{attachment}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.GetAsync($"/api/v1/collaboration/attachments/{attachment}/content")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.DeleteAsync($"/api/v1/collaboration/attachments/{attachment}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.PutAsJsonAsync($"/api/v1/collaboration/comments/{comment.GetProperty("id").GetGuid()}", new { body = "y" }, Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.DeleteAsync($"/api/v1/collaboration/comments/{comment.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.DeleteAsync($"/api/v1/collaboration/links/{link}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/links", new { from = new { type = "internal_memo", id = memo }, to = new { type = "purchase_order", id = Guid.NewGuid() }, relation = "related" }, Json)).ErrorCodeAsync()).ShouldBe("record.read_forbidden");

        // The memo stays open to the clerk, without the link that leads to the order.
        (await (await clerk.GetAsync($"/api/v1/collaboration/links?entityType=internal_memo&entityId={memo}")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await (await owner.GetAsync($"/api/v1/collaboration/links?entityType=internal_memo&entityId={memo}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "internal_memo", entityId = memo, body = "Filed." }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // A buyer, who may read orders, sees the discussion, the file, the timeline and the link.
        (await (await buyer.GetAsync($"/api/v1/collaboration/comments?entityType=purchase_order&entityId={order}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await (await buyer.GetAsync($"/api/v1/collaboration/attachments?entityType=purchase_order&entityId={order}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await buyer.GetAsync($"/api/v1/collaboration/attachments/{attachment}/content")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await buyer.GetAsync($"/api/v1/collaboration/activities?entityType=purchase_order&entityId={order}")).ReadJsonAsync()).GetProperty("items").GetArrayLength().ShouldBe(3);
        (await (await buyer.GetAsync($"/api/v1/collaboration/links?entityType=purchase_order&entityId={order}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await buyer.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "purchase_order", entityId = order, body = "Confirmed with the supplier." }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_role_limited_to_one_company_sees_what_hangs_off_that_companys_records_only()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        async Task<Guid> CompanyAsync(string code) =>
            (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", new { code, legalName = new { en = $"{code} Co", ar = $"شركة {code}" }, country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" }, Json)).ReadJsonAsync()).GetProperty("id").GetGuid();
        var north = await CompanyAsync("NORTH");
        var south = await CompanyAsync("SOUTH");

        // The owner discusses both companies, files South's contract and links the two.
        foreach (var company in new[] { north, south })
        {
            (await owner.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "company", entityId = company, body = "Year-end plan agreed." }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var southComment = (await (await owner.GetAsync($"/api/v1/collaboration/comments?entityType=company&entityId={south}")).ReadJsonAsync())[0].GetProperty("id").GetGuid();
        var attachment = (await (await owner.PostAsync("/api/v1/collaboration/attachments", Form("company", south))).ReadJsonAsync()).GetProperty("id").GetGuid();
        var link = (await (await owner.PostAsJsonAsync("/api/v1/collaboration/links", new { from = new { type = "company", id = north }, to = new { type = "company", id = south }, relation = "related" }, Json)).ReadJsonAsync()).GetProperty("id").GetGuid();

        // A member who may read companies, but only North.
        var (token, membershipId, _) = await host.InviteAsync(ws, "north_clerk", Collaboration);
        using var clerk = Api.ClientFor(token);
        var reader = await (await owner.PostAsJsonAsync("/api/v1/roles", new { code = "company_reader", name = new { en = "Company reader" }, description = "", grants = new[] { "organization.company.read" } }, Json)).ReadJsonAsync();
        var readerId = reader.GetProperty("id").GetGuid();
        (await owner.PostAsJsonAsync($"/api/v1/users/{membershipId}/assignments", new { roleId = readerId, scopes = new[] { new { scopeType = "company", scopeId = north } } }, Json)).EnsureSuccessStatusCode();

        (await (await clerk.GetAsync($"/api/v1/collaboration/comments?entityType=company&entityId={north}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "company", entityId = north, body = "Stock count on the 30th." }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // South's discussion, file, timeline and links answer "not found": whether South has any is not revealed.
        var hidden = await clerk.GetAsync($"/api/v1/collaboration/comments?entityType=company&entityId={south}");
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await hidden.ErrorCodeAsync()).ShouldBe("company.not_found");
        (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "company", entityId = south, body = "x" }, Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.PutAsJsonAsync($"/api/v1/collaboration/comments/{southComment}", new { body = "y" }, Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.GetAsync($"/api/v1/collaboration/attachments?entityType=company&entityId={south}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.PostAsync("/api/v1/collaboration/attachments", Form("company", south))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.GetAsync($"/api/v1/collaboration/attachments/{attachment}/content")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.GetAsync($"/api/v1/collaboration/activities?entityType=company&entityId={south}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.GetAsync($"/api/v1/collaboration/links?entityType=company&entityId={south}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.DeleteAsync($"/api/v1/collaboration/links/{link}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await clerk.PostAsJsonAsync("/api/v1/collaboration/links", new { from = new { type = "company", id = north }, to = new { type = "company", id = south }, relation = "source" }, Json)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // North's links leave out the one that leads to South; the owner sees it.
        (await (await clerk.GetAsync($"/api/v1/collaboration/links?entityType=company&entityId={north}")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await (await owner.GetAsync($"/api/v1/collaboration/links?entityType=company&entityId={north}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);

        // Given South as well, the clerk sees all of it.
        (await owner.PostAsJsonAsync($"/api/v1/users/{membershipId}/assignments", new { roleId = readerId, scopes = new[] { new { scopeType = "company", scopeId = south } } }, Json)).EnsureSuccessStatusCode();
        (await (await clerk.GetAsync($"/api/v1/collaboration/comments?entityType=company&entityId={south}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await clerk.GetAsync($"/api/v1/collaboration/attachments/{attachment}/content")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await clerk.GetAsync($"/api/v1/collaboration/links?entityType=company&entityId={north}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Every_record_type_with_a_read_permission_is_placed_in_a_company_or_shared_by_the_workspace()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", new { code = "HQ", legalName = new { en = "HQ Co", ar = "شركة المقر" }, country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" }, Json)).ReadJsonAsync()).GetProperty("id").GetGuid();

        await using var scope = Api.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = TenantContext.System(new TenantId(ws.TenantId), "test-record-companies");
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context, cancellationToken: TestContext.Current.CancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);

        // Items and partners are shared by every company of a workspace; every other record type names its company.
        var lookups = services.GetServices<IRecordCompanies>().ToList();
        var placed = lookups.SelectMany(static l => l.EntityTypes).ToList();
        placed.ShouldBeUnique();
        var registered = services.GetServices<RecordReadPermission>().Select(static r => r.EntityType).Distinct().ToList();
        registered.Except(placed).OrderBy(static t => t, StringComparer.Ordinal).ShouldBe(["item", "partner"]);

        // Each module's lookup runs against its own table (a record that does not exist has no company).
        foreach (var lookup in lookups)
        {
            foreach (var type in lookup.EntityTypes)
            {
                (await lookup.CompaniesAsync(type, [Guid.NewGuid()], TestContext.Current.CancellationToken)).ShouldBeEmpty(type);
            }
        }

        var companies = lookups.Single(static l => l.EntityTypes.Contains("company"));
        (await companies.CompaniesAsync("company", [company], TestContext.Current.CancellationToken))[company].ShouldBe(company);
    }
}
