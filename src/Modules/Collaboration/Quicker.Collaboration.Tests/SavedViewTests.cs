using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Collaboration.Tests;

/// <summary>Saved views: private by default, shared on request, one default per owner and entity type, ownership rules, validation.</summary>
[Collection(ApiCollection.Name)]
public sealed class SavedViewTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Views_are_private_until_shared_and_only_their_owner_or_a_manager_changes_them()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (clerkToken, clerkMembership, _) = await host.InviteAsync(ws, "clerk", "collaboration.attachment.read");
        using var clerk = Api.ClientFor(clerkToken);

        var definition = new { filters = new[] { new { field = "status", op = "eq", value = "open" } }, sort = new[] { new { field = "dueDate", dir = "asc" } }, columns = new[] { "number", "customer", "total" }, pageSize = 50 };
        var created = await clerk.PostAsJsonAsync("/api/v1/collaboration/views", new { entityType = "sales_invoice", name = "Open invoices", definition, isDefault = true }, Json);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var view = await created.ReadJsonAsync();
        view.GetProperty("ownerMembershipId").GetGuid().ShouldBe(clerkMembership);
        view.GetProperty("shared").GetBoolean().ShouldBeFalse();
        view.GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        view.GetProperty("definition").GetProperty("columns").GetArrayLength().ShouldBe(3);

        // Private: the owner does not see it; shared: they do.
        (await (await owner.GetAsync("/api/v1/collaboration/views?entityType=sales_invoice")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        var viewId = view.GetProperty("id").GetGuid();
        (await clerk.PutAsJsonAsync($"/api/v1/collaboration/views/{viewId}", new { entityType = "sales_invoice", name = "Open invoices", definition, shared = true, isDefault = true }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await (await owner.GetAsync("/api/v1/collaboration/views?entityType=sales_invoice")).ReadJsonAsync()).EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("Open invoices");

        // A second default replaces the first for that owner; names are unique per owner and entity type.
        var second = await (await clerk.PostAsJsonAsync("/api/v1/collaboration/views", new { entityType = "sales_invoice", name = "Overdue", definition = new { filters = Array.Empty<object>() }, isDefault = true }, Json)).ReadJsonAsync();
        second.GetProperty("isDefault").GetBoolean().ShouldBeTrue();
        var mine = (await (await clerk.GetAsync("/api/v1/collaboration/views?entityType=sales_invoice")).ReadJsonAsync()).EnumerateArray().ToDictionary(static v => v.GetProperty("name").GetString()!, static v => v.GetProperty("isDefault").GetBoolean());
        mine.ShouldBe(new Dictionary<string, bool> { ["Open invoices"] = false, ["Overdue"] = true });
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/views", new { entityType = "sales_invoice", name = "Overdue", definition = new { } }, Json)).ErrorCodeAsync()).ShouldBe("view.name_taken");
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/views", new { entityType = "sales_invoice", name = "Bad", definition = new { sql = "DROP TABLE" } }, Json)).ErrorCodeAsync()).ShouldBe("view.definition_invalid");
        (await (await clerk.PostAsJsonAsync("/api/v1/collaboration/views", new { entityType = "Sales Invoice", name = "Bad", definition = new { } }, Json)).ErrorCodeAsync()).ShouldBe("view.entity_invalid");

        // The owner (manage) may change the clerk's shared view; the clerk may not change the owner's shared view.
        (await owner.PutAsJsonAsync($"/api/v1/collaboration/views/{viewId}", new { entityType = "sales_invoice", name = "Open invoices (team)", definition, shared = true }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var owners = await (await owner.PostAsJsonAsync("/api/v1/collaboration/views", new { entityType = "sales_invoice", name = "Owner's view", definition = new { }, shared = true }, Json)).ReadJsonAsync();
        (await (await clerk.PutAsJsonAsync($"/api/v1/collaboration/views/{owners.GetProperty("id").GetGuid()}", new { entityType = "sales_invoice", name = "Hijacked", definition = new { }, shared = true }, Json)).ErrorCodeAsync()).ShouldBe("view.not_owner");
        (await clerk.DeleteAsync($"/api/v1/collaboration/views/{owners.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await clerk.DeleteAsync($"/api/v1/collaboration/views/{viewId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await (await outsider.GetAsync("/api/v1/collaboration/views?entityType=sales_invoice")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        (await outsider.DeleteAsync($"/api/v1/collaboration/views/{owners.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
