using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Collaboration.Tests;

/// <summary>Comments with mentions feed the activity timeline and notify the mentioned members; authorship rules; isolation.</summary>
[Collection(ApiCollection.Name)]
public sealed class CommentTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Comments_mention_members_land_on_the_timeline_and_follow_authorship_rules()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (clerkToken, clerkMembership, _) = await host.InviteAsync(ws, "clerk", "collaboration.comment.read", "collaboration.comment.write", "collaboration.activity.read", "collaboration.attachment.manage", "collaboration.attachment.read");
        using var clerk = Api.ClientFor(clerkToken);
        var invoiceId = Guid.NewGuid();

        // Whom the clerk can mention: every active member by name, without the member list's permission or emails.
        (await clerk.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var mentionable = (await (await clerk.GetAsync("/api/v1/collaboration/comments/mentionable")).ReadJsonAsync()).EnumerateArray().ToList();
        mentionable.Select(static m => m.GetProperty("membershipId").GetGuid()).ShouldBe([ws.MembershipId, clerkMembership], ignoreOrder: true);
        mentionable.Single(m => m.GetProperty("membershipId").GetGuid() == clerkMembership).GetProperty("displayName").GetString().ShouldBe("clerk");
        mentionable.Any(static m => m.TryGetProperty("email", out _)).ShouldBeFalse();

        // An attachment and a comment mentioning the owner.
        using var form = new MultipartFormDataContent { { new StringContent("sales_invoice"), "entityType" }, { new StringContent(invoiceId.ToString()), "entityId" } };
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        form.Add(file, "file", "note.txt");
        (await clerk.PostAsync("/api/v1/collaboration/attachments", form)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var posted = await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "sales_invoice", entityId = invoiceId, body = "Please approve this one", mentions = new[] { ws.MembershipId } }, Json);
        var comment = await posted.ReadJsonAsync();
        posted.StatusCode.ShouldBe(HttpStatusCode.Created, comment.ToString());
        comment.GetProperty("authorMembershipId").GetGuid().ShouldBe(clerkMembership);
        comment.GetProperty("authorName").GetString().ShouldBe("clerk");
        comment.GetProperty("mentions").EnumerateArray().Single().GetGuid().ShouldBe(ws.MembershipId);

        var mention = (await (await owner.GetAsync("/api/v1/collaboration/notifications?unreadOnly=true")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "collaboration.mention");
        mention.GetProperty("title").Map()["en"].ShouldBe("clerk mentioned you");
        mention.GetProperty("entityId").GetGuid().ShouldBe(invoiceId);
        mention.GetProperty("data").GetProperty("commentId").GetGuid().ShouldBe(comment.GetProperty("id").GetGuid());

        var timeline = (await (await clerk.GetAsync($"/api/v1/collaboration/activities?entityType=sales_invoice&entityId={invoiceId}")).ReadJsonAsync()).GetProperty("items").EnumerateArray().ToList();
        timeline.Select(static a => a.GetProperty("kind").GetString()).ShouldBe(["comment.added", "attachment.added"]);
        timeline[0].GetProperty("summary").Map()["ar"].ShouldBe("علّق clerk");
        timeline[1].GetProperty("data").GetProperty("fileName").GetString().ShouldBe("note.txt");
        timeline[0].GetProperty("actorMembershipId").GetGuid().ShouldBe(clerkMembership);

        // A reply, then edits: the author edits their own; the owner (manage) edits anyone's; the clerk cannot edit the owner's.
        var reply = await (await owner.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "sales_invoice", entityId = invoiceId, body = "Approved.", parentId = comment.GetProperty("id").GetGuid() }, Json)).ReadJsonAsync();
        reply.GetProperty("parentId").GetGuid().ShouldBe(comment.GetProperty("id").GetGuid());
        (await clerk.PutAsJsonAsync($"/api/v1/collaboration/comments/{comment.GetProperty("id").GetGuid()}", new { body = "Please approve this one today" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await clerk.PutAsJsonAsync($"/api/v1/collaboration/comments/{reply.GetProperty("id").GetGuid()}", new { body = "nope" }, Json)).ErrorCodeAsync()).ShouldBe("comment.not_author");
        (await owner.PutAsJsonAsync($"/api/v1/collaboration/comments/{comment.GetProperty("id").GetGuid()}", new { body = "Please approve this one today (edited by owner)" }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "sales_invoice", entityId = invoiceId, body = " " }, Json)).ErrorCodeAsync()).ShouldBe("comment.body_invalid");
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "sales_invoice", entityId = invoiceId, body = "x", mentions = new[] { Guid.NewGuid() } }, Json)).ErrorCodeAsync()).ShouldBe("comment.mention_unknown");
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/comments", new { entityType = "sales_invoice", entityId = invoiceId, body = "x", parentId = Guid.NewGuid() }, Json)).ErrorCodeAsync()).ShouldBe("comment.not_found");

        // Soft delete keeps the thread's shape; another tenant sees nothing.
        (await clerk.DeleteAsync($"/api/v1/collaboration/comments/{comment.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var thread = (await (await owner.GetAsync($"/api/v1/collaboration/comments?entityType=sales_invoice&entityId={invoiceId}")).ReadJsonAsync()).EnumerateArray().ToList();
        thread.Count.ShouldBe(2);
        thread[0].GetProperty("deletedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        thread[0].GetProperty("body").GetString().ShouldBe(string.Empty);
        thread[1].GetProperty("editedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        (await (await owner.GetAsync($"/api/v1/collaboration/activities?entityType=sales_invoice&entityId={invoiceId}")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Select(static a => a.GetProperty("kind").GetString()).ShouldBe(["comment.deleted", "comment.edited", "comment.edited", "comment.added", "comment.added", "attachment.added"]);

        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await (await outsider.GetAsync($"/api/v1/collaboration/comments?entityType=sales_invoice&entityId={invoiceId}")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await outsider.DeleteAsync($"/api/v1/collaboration/comments/{reply.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Links_join_records_from_both_ends_once_and_write_both_timelines()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var order = new { type = "sales_order", id = Guid.NewGuid() };
        var invoice = new { type = "sales_invoice", id = Guid.NewGuid() };

        var created = await owner.PostAsJsonAsync("/api/v1/collaboration/links", new { from = invoice, to = order, relation = "source" }, Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var link = await created.ReadJsonAsync();
        var again = await owner.PostAsJsonAsync("/api/v1/collaboration/links", new { from = invoice, to = order, relation = "source" }, Json);
        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await again.ReadJsonAsync()).GetProperty("id").GetGuid().ShouldBe(link.GetProperty("id").GetGuid());

        var fromOrder = (await (await owner.GetAsync($"/api/v1/collaboration/links?entityType=sales_order&entityId={order.id}")).ReadJsonAsync()).EnumerateArray().Single();
        fromOrder.GetProperty("from").GetProperty("id").GetGuid().ShouldBe(invoice.id);
        fromOrder.GetProperty("relation").GetString().ShouldBe("source");
        (await (await owner.GetAsync($"/api/v1/collaboration/links?entityType=sales_invoice&entityId={invoice.id}")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);
        (await (await owner.GetAsync($"/api/v1/collaboration/activities?entityType=sales_order&entityId={order.id}")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Single().GetProperty("kind").GetString().ShouldBe("link.added");

        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/links", new { from = invoice, to = invoice, relation = "related" }, Json)).ErrorCodeAsync()).ShouldBe("link.self");
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/links", new { from = invoice, to = order, relation = "Is Source Of" }, Json)).ErrorCodeAsync()).ShouldBe("link.relation_invalid");

        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.DeleteAsync($"/api/v1/collaboration/links/{link.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.DeleteAsync($"/api/v1/collaboration/links/{link.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await owner.GetAsync($"/api/v1/collaboration/links?entityType=sales_order&entityId={order.id}")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await (await owner.GetAsync($"/api/v1/collaboration/activities?entityType=sales_invoice&entityId={invoice.id}")).ReadJsonAsync()).GetProperty("items").EnumerateArray().Select(static a => a.GetProperty("kind").GetString()).ShouldBe(["link.removed", "link.added"]);
    }
}
