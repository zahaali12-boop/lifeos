using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Collaboration.Tests;

/// <summary>Custom fields on companies: definitions, JSONB validation on create/update, the expression index, the JSON Schema, isolation.</summary>
[Collection(ApiCollection.Name)]
public sealed class CustomFieldTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Company(string code, object? customFields) =>
        new { code, legalName = new { en = "Company " + code }, country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", customFields };

    private async Task<bool> IndexExistsAsync(string name)
    {
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        return await db.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE schemaname = 'app' AND tablename = 'org_companies' AND indexname = @name)", new { name });
    }

    [Fact]
    public async Task Definitions_validate_company_values_index_the_host_column_and_publish_a_schema()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        async Task<JsonElement> DefineAsync(object field)
        {
            var response = await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", field, Json);
            var json = await response.ReadJsonAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.Created, json.ToString());
            return json;
        }

        var region = await DefineAsync(new { entityType = "company", key = "region", label = new { en = "Region", ar = "المنطقة" }, type = "select", required = true, indexed = true, options = new[] { new { value = "north", label = new { en = "North" } }, new { value = "south", label = new { en = "South" } } } });
        await DefineAsync(new { entityType = "company", key = "founded_on", label = new { en = "Founded on" }, type = "date" });
        await DefineAsync(new { entityType = "company", key = "headcount", label = new { en = "Headcount" }, type = "number", rules = new { min = 1, max = 100000 } });
        await DefineAsync(new { entityType = "company", key = "website", label = new { en = "Website" }, type = "text", rules = new { maxLength = 200, pattern = "^https://" } });
        await DefineAsync(new { entityType = "company", key = "vip", label = new { en = "VIP" }, type = "boolean" });
        await DefineAsync(new { entityType = "company", key = "tags", label = new { en = "Tags" }, type = "multi_select", options = new[] { new { value = "retail", label = new { en = "Retail" } }, new { value = "wholesale", label = new { en = "Wholesale" } } } });
        (await IndexExistsAsync("org_companies_cf_region_idx")).ShouldBeTrue("indexed=true creates the expression index on the host table");

        // Definition validation.
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "sales_invoice", key = "x", label = new { en = "X" }, type = "text" }, Json)).ErrorCodeAsync()).ShouldBe("custom_field.entity_unsupported");
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "company", key = "Region", label = new { en = "X" }, type = "text" }, Json)).ErrorCodeAsync()).ShouldBe("custom_field.key_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "company", key = "region", label = new { en = "X" }, type = "text" }, Json)).ErrorCodeAsync()).ShouldBe("custom_field.key_taken");
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "company", key = "size", label = new { en = "X" }, type = "select" }, Json)).ErrorCodeAsync()).ShouldBe("custom_field.options_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "company", key = "code2", label = new { en = "X" }, type = "text", rules = new { pattern = "(" } }, Json)).ErrorCodeAsync()).ShouldBe("custom_field.rules_invalid");

        // Values are validated when a company is created or changed.
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.required");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "east" }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.value_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "north", colour = "red" }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.unknown");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "north", headcount = 0 }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.value_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "north", website = "http://insecure" }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.value_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "north", founded_on = "2020-13-01" }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.value_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "north", tags = new[] { "retail", "online" } }), Json)).ErrorCodeAsync()).ShouldBe("custom_field.value_invalid");
        var valid = await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", new { region = "north", founded_on = "2020-05-17", headcount = 42, website = "https://c1.example.test", vip = true, tags = new[] { "retail", "retail", "wholesale" } }), Json);
        var company = await valid.ReadJsonAsync();
        valid.StatusCode.ShouldBe(HttpStatusCode.Created, company.ToString());
        var stored = company.GetProperty("customFields");
        stored.GetProperty("region").GetString().ShouldBe("north");
        stored.GetProperty("headcount").GetDecimal().ShouldBe(42m);
        stored.GetProperty("tags").EnumerateArray().Select(static t => t.GetString()).ShouldBe(["retail", "wholesale"]);
        stored.GetProperty("vip").GetBoolean().ShouldBeTrue();

        // The index is usable for the tenant's queries; a change keeps validating; a missing customFields keeps the stored ones.
        await using (var db = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            (await db.ExecuteScalarAsync<long>("SELECT count(*) FROM app.org_companies WHERE tenant_id = @t AND custom_fields->>'region' = 'north'", new { t = ws.TenantId })).ShouldBe(1);
        }

        var companyId = company.GetProperty("id").GetGuid();
        var update = Company("C1", new { region = "south" });
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/companies/{companyId}", update, Json)).ReadJsonAsync()).GetProperty("customFields").GetProperty("region").GetString().ShouldBe("south");
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/companies/{companyId}", Company("C1", null), Json)).ReadJsonAsync()).GetProperty("customFields").GetProperty("region").GetString().ShouldBe("south");

        // The schema follows the definitions; dropping the index and deleting the definition remove the index.
        var schema = await (await owner.GetAsync("/api/v1/collaboration/custom-fields/company/schema")).ReadJsonAsync();
        schema.GetProperty("required").EnumerateArray().Single().GetString().ShouldBe("region");
        schema.GetProperty("properties").GetProperty("region").GetProperty("enum").GetArrayLength().ShouldBe(2);
        schema.GetProperty("properties").GetProperty("headcount").GetProperty("maximum").GetDecimal().ShouldBe(100000m);
        schema.GetProperty("properties").GetProperty("founded_on").GetProperty("format").GetString().ShouldBe("date");
        schema.GetProperty("properties").GetProperty("region").GetProperty("x-label").GetProperty("ar").GetString().ShouldBe("المنطقة");
        var regionId = region.GetProperty("id").GetGuid();
        (await (await owner.PutAsJsonAsync($"/api/v1/collaboration/custom-fields/{regionId}", new { entityType = "company", key = "region", label = new { en = "Region" }, type = "text" }, Json)).ErrorCodeAsync()).ShouldBe("custom_field.identity_locked");
        (await owner.PutAsJsonAsync($"/api/v1/collaboration/custom-fields/{regionId}", new { entityType = "company", key = "region", label = new { en = "Region" }, type = "select", required = true, indexed = false, options = new[] { new { value = "north", label = new { en = "North" } }, new { value = "south", label = new { en = "South" } } } }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await IndexExistsAsync("org_companies_cf_region_idx")).ShouldBeFalse();
        (await (await owner.GetAsync($"/api/v1/audit/records/custom_field/{regionId}")).ReadJsonAsync()).EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldBe(["created", "updated"]);
        (await owner.DeleteAsync($"/api/v1/collaboration/custom-fields/{regionId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await owner.GetAsync("/api/v1/collaboration/custom-fields?entityType=company")).ReadJsonAsync()).GetArrayLength().ShouldBe(5);

        // Definitions are per tenant: another workspace has none and creates companies freely; a clerk cannot define fields.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.PostAsJsonAsync("/api/v1/organization/companies", Company("C1", null), Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await (await outsider.GetAsync("/api/v1/collaboration/custom-fields?entityType=company")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);
        var (clerkToken, _, _) = await host.InviteAsync(ws, "clerk", "organization.company.read");
        using var clerk = Api.ClientFor(clerkToken);
        (await clerk.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "company", key = "z", label = new { en = "Z" }, type = "text" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await (await clerk.GetAsync("/api/v1/collaboration/custom-fields?entityType=company")).ReadJsonAsync()).GetArrayLength().ShouldBe(5, "every member reads the definitions to render forms");
    }
}
