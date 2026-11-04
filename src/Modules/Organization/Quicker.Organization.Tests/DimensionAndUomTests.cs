using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Organization.Application;

namespace Quicker.Organization.Tests;

/// <summary>Dimensions with hierarchy, deduplicated dimension sets, and unit conversions with rational factors.</summary>
[Collection(ApiCollection.Name)]
public sealed class DimensionAndUomTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Dimension_sets_are_one_id_per_combination_and_validate_their_values()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var costCentre = (await owner.DimensionAsync("COST_CENTER")).GetProperty("id").GetGuid();
        var project = (await owner.DimensionAsync("PROJECT")).GetProperty("id").GetGuid();

        var ops = await owner.PostAsync($"/api/v1/organization/dimensions/{costCentre}/values", new { code = "OPS", name = new { en = "Operations", ar = "العمليات" } });
        var opsId = ops.GetProperty("id").GetGuid();
        var opsNorth = await owner.PostAsync($"/api/v1/organization/dimensions/{costCentre}/values", new { code = "OPS-N", name = new { en = "Operations North" }, parentId = opsId });
        opsNorth.GetProperty("parentId").GetGuid().ShouldBe(opsId);
        var bridge = await owner.PostAsync($"/api/v1/organization/dimensions/{project}/values", new { code = "BRIDGE", name = new { en = "Bridge" }, validFrom = "2026-01-01", validTo = "2026-12-31" });
        var bridgeId = bridge.GetProperty("id").GetGuid();

        (await (await owner.PostAsJsonAsync($"/api/v1/organization/dimensions/{project}/values", new { code = "X", name = new { en = "x" }, parentId = bridgeId }, Json)).ErrorCodeAsync()).ShouldBe("dimension_value.not_hierarchical");
        (await (await owner.PostAsJsonAsync($"/api/v1/organization/dimensions/{project}/values", new { code = "BRIDGE", name = new { en = "dup" } }, Json)).ErrorCodeAsync()).ShouldBe("dimension_value.code_taken");
        (await (await owner.PostAsJsonAsync($"/api/v1/organization/dimensions/{project}/values", new { code = "LATE", name = new { en = "x" }, validFrom = "2026-02-01", validTo = "2026-01-01" }, Json)).ErrorCodeAsync()).ShouldBe("dimension_value.validity_invalid");

        var set = await owner.PostAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid> { ["COST_CENTER"] = opsId, ["PROJECT"] = bridgeId } }, HttpStatusCode.OK);
        var setId = set.GetProperty("id").GetGuid();
        set.GetProperty("hash").GetString()!.Length.ShouldBe(64);
        var same = await owner.PostAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid> { ["project"] = bridgeId, ["cost_center"] = opsId } }, HttpStatusCode.OK);
        same.GetProperty("id").GetGuid().ShouldBe(setId); // order and case do not matter
        var different = await owner.PostAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid> { ["COST_CENTER"] = opsNorth.GetProperty("id").GetGuid(), ["PROJECT"] = bridgeId } }, HttpStatusCode.OK);
        different.GetProperty("id").GetGuid().ShouldNotBe(setId);
        (await owner.GetOkAsync($"/api/v1/organization/dimension-sets/{setId}")).GetProperty("values").GetProperty("PROJECT").GetGuid().ShouldBe(bridgeId);

        (await (await owner.PostAsJsonAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid> { ["COST_CENTER"] = bridgeId } }, Json)).ErrorCodeAsync()).ShouldBe("dimension_set.value_mismatch");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid> { ["REGION"] = bridgeId } }, Json)).ErrorCodeAsync()).ShouldBe("dimension.not_found");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid>() }, Json)).ErrorCodeAsync()).ShouldBe("dimension_set.empty");

        await owner.PutAsync($"/api/v1/organization/dimensions/{project}/values/{bridgeId}", new { code = "BRIDGE", name = new { en = "Bridge" }, isActive = false });
        (await (await owner.PostAsJsonAsync("/api/v1/organization/dimension-sets", new { values = new Dictionary<string, Guid> { ["PROJECT"] = bridgeId } }, Json)).ErrorCodeAsync()).ShouldBe("dimension_set.value_inactive");

        // Custom dimensions; system ones keep their code.
        var region = await owner.PostAsync("/api/v1/organization/dimensions", new { code = "region", name = new { en = "Region", ar = "المنطقة" }, isHierarchical = true, sortOrder = 10 });
        region.GetProperty("code").GetString().ShouldBe("REGION");
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/dimensions/{costCentre}", new { code = "CC", name = new { en = "Cost centre" } }, Json)).ErrorCodeAsync()).ShouldBe("dimension.system_locked");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/dimensions", new { code = "1BAD", name = new { en = "x" } }, Json)).ErrorCodeAsync()).ShouldBe("dimension.code_invalid");

        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.GetAsync($"/api/v1/organization/dimension-sets/{setId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void Set_hashes_are_canonical()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var first = DimensionService.Hash(new Dictionary<string, Guid> { ["PROJECT"] = b, ["COST_CENTER"] = a });
        var second = DimensionService.Hash(new Dictionary<string, Guid> { ["COST_CENTER"] = a, ["PROJECT"] = b });
        first.ShouldBe(second);
        DimensionService.Hash(new Dictionary<string, Guid> { ["COST_CENTER"] = b, ["PROJECT"] = a }).ShouldNotBe(first);
    }

    [Fact]
    public async Task Units_convert_directly_inversely_and_through_one_intermediate_unit_of_the_same_family()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var pcs = await owner.UomIdAsync("PCS");
        var dz = await owner.UomIdAsync("DZ");
        var ctn = await owner.UomIdAsync("CTN");
        var ton = await owner.UomIdAsync("TON");
        var g = await owner.UomIdAsync("G");
        var kg = await owner.UomIdAsync("KG");

        var direct = await owner.GetOkAsync($"/api/v1/organization/uom-conversions/convert?from={dz}&to={pcs}&value=3");
        direct.GetProperty("result").GetDecimal().ShouldBe(36m);
        direct.GetProperty("method").GetString().ShouldBe("direct");
        var inverse = await owner.GetOkAsync($"/api/v1/organization/uom-conversions/convert?from={pcs}&to={dz}&value=30");
        inverse.GetProperty("result").GetDecimal().ShouldBe(2.5m);
        inverse.GetProperty("method").GetString().ShouldBe("inverse");
        var via = await owner.GetOkAsync($"/api/v1/organization/uom-conversions/convert?from={ton}&to={g}&value=0.5");
        via.GetProperty("result").GetDecimal().ShouldBe(500000m);
        via.GetProperty("method").GetString().ShouldBe("via KG");
        (await owner.GetOkAsync($"/api/v1/organization/uom-conversions/convert?from={kg}&to={kg}&value=7")).GetProperty("method").GetString().ShouldBe("identity");

        (await owner.GetErrorAsync($"/api/v1/organization/uom-conversions/convert?from={kg}&to={pcs}&value=1", HttpStatusCode.UnprocessableEntity)).ShouldBe("uom_conversion.family_mismatch");
        (await owner.GetErrorAsync($"/api/v1/organization/uom-conversions/convert?from={ctn}&to={pcs}&value=1", HttpStatusCode.Conflict)).ShouldBe("uom_conversion.not_found");

        // Define CTN = 24 PCS; CTN→DZ is then derived through PCS. Saving the reverse direction updates the same pair.
        var saved = await owner.PostAsync("/api/v1/organization/uom-conversions", new { fromUomId = ctn, toUomId = pcs, numerator = 24 }, HttpStatusCode.OK);
        saved.GetProperty("fromCode").GetString().ShouldBe("CTN");
        (await owner.GetOkAsync($"/api/v1/organization/uom-conversions/convert?from={ctn}&to={dz}&value=2")).GetProperty("result").GetDecimal().ShouldBe(4m);
        var reversed = await owner.PostAsync("/api/v1/organization/uom-conversions", new { fromUomId = pcs, toUomId = ctn, numerator = 1, denominator = 12 }, HttpStatusCode.OK);
        reversed.GetProperty("id").GetGuid().ShouldBe(saved.GetProperty("id").GetGuid());
        reversed.GetProperty("numerator").GetDecimal().ShouldBe(12m);
        (await owner.GetOkAsync($"/api/v1/organization/uom-conversions/convert?from={ctn}&to={pcs}&value=1")).GetProperty("result").GetDecimal().ShouldBe(12m);

        (await (await owner.PostAsJsonAsync("/api/v1/organization/uom-conversions", new { fromUomId = kg, toUomId = pcs, numerator = 1 }, Json)).ErrorCodeAsync()).ShouldBe("uom_conversion.family_mismatch");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/uom-conversions", new { fromUomId = kg, toUomId = g, numerator = 0 }, Json)).ErrorCodeAsync()).ShouldBe("uom_conversion.factor_invalid");
        (await owner.DeleteAsync($"/api/v1/organization/uom-conversions/{saved.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetErrorAsync($"/api/v1/organization/uom-conversions/convert?from={ctn}&to={pcs}&value=1", HttpStatusCode.Conflict)).ShouldBe("uom_conversion.not_found");

        // Units themselves: bilingual names, families, system protection.
        var bag = await owner.PostAsync("/api/v1/organization/uoms", new { code = "bag", name = new { en = "Bag", ar = "كيس" }, family = "count" });
        bag.GetProperty("code").GetString().ShouldBe("BAG");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/uoms", new { code = "X", name = new { en = "x" }, family = "mass" }, Json)).ErrorCodeAsync()).ShouldBe("uom.family.invalid");
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/uoms/{kg}", new { code = "KILO", name = new { en = "Kilogram" }, family = "weight", precision = 3 }, Json)).ErrorCodeAsync()).ShouldBe("uom.system_locked");
        (await (await owner.PostAsJsonAsync("/api/v1/organization/uoms", new { code = "x", name = new Dictionary<string, string>(), family = "count" }, Json)).ErrorCodeAsync()).ShouldBe("uom.name_required");
    }
}
