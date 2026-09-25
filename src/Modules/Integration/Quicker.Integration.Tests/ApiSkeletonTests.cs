using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Integration.Tests;

/// <summary>Slice 1.9 API skeleton on companies: the filter language (with custom fields), field selection, expansion, ETags with If-None-Match and If-Match.</summary>
[Collection(ApiCollection.Name)]
public sealed class ApiSkeletonTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Company(string code, string country, string currency, bool isActive = true, object? customFields = null) =>
        new { code, legalName = new { en = "Company " + code }, country, functionalCurrency = currency, timeZone = "Asia/Baghdad", isActive, customFields };

    [Fact]
    public async Task Lists_filter_select_fields_expand_and_carry_validators()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        (await owner.PostAsJsonAsync("/api/v1/collaboration/custom-fields", new { entityType = "company", key = "region", label = new { en = "Region" }, type = "select", indexed = true, options = new[] { new { value = "north", label = new { en = "North" } }, new { value = "south", label = new { en = "South" } } } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var north = await (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("NORTH", "IQ", "IQD", customFields: new { region = "north" }), Json)).ReadJsonAsync();
        (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("SOUTH", "IQ", "IQD", customFields: new { region = "south" }), Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await owner.PostAsJsonAsync("/api/v1/organization/companies", Company("DUBAI", "AE", "AED", isActive: false), Json)).StatusCode.ShouldBe(HttpStatusCode.Created);

        async Task<List<string>> CodesAsync(string filter)
        {
            var response = await owner.GetAsync("/api/v1/organization/companies?filter=" + Uri.EscapeDataString(filter));
            var json = await response.ReadJsonAsync();
            response.StatusCode.ShouldBe(HttpStatusCode.OK, json.ToString());
            return json.EnumerateArray().Select(static c => c.GetProperty("code").GetString()!).ToList();
        }

        // The filter language.
        (await CodesAsync("country eq 'IQ'")).ShouldBe(["NORTH", "SOUTH"]);
        (await CodesAsync("country eq 'IQ' and cf.region eq 'north'")).ShouldBe(["NORTH"]);
        (await CodesAsync("code in ('DUBAI', 'SOUTH')")).ShouldBe(["DUBAI", "SOUTH"]);
        (await CodesAsync("isActive eq false")).ShouldBe(["DUBAI"]);
        (await CodesAsync("code like 'ou'")).ShouldBe(["SOUTH"]);
        (await CodesAsync("reportingCurrency isnull and functionalCurrency ne 'AED'")).ShouldBe(["NORTH", "SOUTH"]);
        (await CodesAsync("code gt 'M'")).ShouldBe(["NORTH", "SOUTH"]);
        (await CodesAsync("cf.region isnull")).ShouldBe(["DUBAI"]);
        (await (await owner.GetAsync("/api/v1/organization/companies?filter=" + Uri.EscapeDataString("colour eq 'red'"))).ErrorCodeAsync()).ShouldBe("filter.field_unknown");
        (await (await owner.GetAsync("/api/v1/organization/companies?filter=" + Uri.EscapeDataString("isActive eq maybe"))).ErrorCodeAsync()).ShouldBe("filter.value_invalid");
        (await (await owner.GetAsync("/api/v1/organization/companies?filter=" + Uri.EscapeDataString("code eq 'unterminated"))).ErrorCodeAsync()).ShouldBe("filter.syntax");
        (await (await owner.GetAsync("/api/v1/organization/companies?filter=" + Uri.EscapeDataString("code eq 'A' or code eq 'B'"))).ErrorCodeAsync()).ShouldBe("filter.syntax");

        // Field selection keeps the id and the named properties only, on arrays and single objects alike.
        var shaped = (await (await owner.GetAsync("/api/v1/organization/companies?fields=code,country")).ReadJsonAsync()).EnumerateArray().First();
        shaped.EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["code", "country", "id"]);
        var northId = north.GetProperty("id").GetGuid();
        (await (await owner.GetAsync($"/api/v1/organization/companies/{northId}?fields=legalName")).ReadJsonAsync()).EnumerateObject().Select(static p => p.Name).Order(StringComparer.Ordinal).ShouldBe(["id", "legalName"]);

        // Expansion embeds the branches.
        (await owner.PostAsJsonAsync($"/api/v1/organization/companies/{northId}/branches", new { code = "HQ", name = new { en = "Head office" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var plain = await (await owner.GetAsync($"/api/v1/organization/companies/{northId}")).ReadJsonAsync();
        plain.GetProperty("branches").ValueKind.ShouldBe(JsonValueKind.Null);
        var expanded = await (await owner.GetAsync($"/api/v1/organization/companies/{northId}?expand=branches")).ReadJsonAsync();
        expanded.GetProperty("branches").EnumerateArray().Single().GetProperty("code").GetString().ShouldBe("HQ");

        // Validators: a version ETag on the single resource, 304 on If-None-Match, 412 on a stale If-Match; a body ETag on lists.
        var get = await owner.GetAsync($"/api/v1/organization/companies/{northId}");
        var etag = get.Headers.ETag.ShouldNotBeNull();
        etag.IsWeak.ShouldBeTrue();
        etag.Tag.ShouldStartWith("\"v");
        using (var conditional = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/organization/companies/{northId}"))
        {
            conditional.Headers.IfNoneMatch.Add(etag);
            var notModified = await owner.SendAsync(conditional);
            notModified.StatusCode.ShouldBe(HttpStatusCode.NotModified);
            (await notModified.Content.ReadAsByteArrayAsync()).Length.ShouldBe(0);
        }

        var update = Company("NORTH", "IQ", "IQD", customFields: new { region = "north" });
        using (var withMatch = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organization/companies/{northId}") { Content = JsonContent.Create(update, options: Json) })
        {
            withMatch.Headers.IfMatch.Add(etag);
            (await owner.SendAsync(withMatch)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        Api.Clock.Advance(TimeSpan.FromSeconds(1));
        using (var stale = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/organization/companies/{northId}") { Content = JsonContent.Create(update, options: Json) })
        {
            stale.Headers.IfMatch.Add(new EntityTagHeaderValue("\"v0\"", isWeak: true));
            var refused = await owner.SendAsync(stale);
            refused.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
            (await refused.ErrorCodeAsync()).ShouldBe("precondition.failed");
        }

        var list = await owner.GetAsync("/api/v1/organization/companies");
        var listTag = list.Headers.ETag.ShouldNotBeNull();
        using (var conditionalList = new HttpRequestMessage(HttpMethod.Get, "/api/v1/organization/companies"))
        {
            conditionalList.Headers.IfNoneMatch.Add(listTag);
            (await owner.SendAsync(conditionalList)).StatusCode.ShouldBe(HttpStatusCode.NotModified);
        }

        // Another tenant sees an empty, differently tagged list.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        var otherList = await outsider.GetAsync("/api/v1/organization/companies");
        otherList.Headers.ETag.ShouldNotBeNull().Tag.ShouldNotBe(listTag.Tag);
        (await otherList.ReadJsonAsync()).GetArrayLength().ShouldBe(0);
    }
}
