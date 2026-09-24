using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

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
}
