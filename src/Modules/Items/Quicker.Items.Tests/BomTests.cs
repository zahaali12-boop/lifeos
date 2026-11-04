using System.Net;
using Quicker.Identity.TestSupport;

namespace Quicker.Items.Tests;

/// <summary>Bills of material: kits and assemblies, versions with one active, exact unit conversion on lines, no cycles, multi-level explosion with scrap.</summary>
[Collection(ApiCollection.Name)]
public sealed class BomTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    [Fact]
    public async Task A_gift_set_explodes_through_a_nested_assembly_with_exact_units_and_scrap_and_refuses_cycles()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var bottle = await owner.PostAsync("/api/v1/items", new { code = "BOTTLE", name = Name("Bottle", "قارورة"), baseUom = "PCS", uoms = new object[] { new { uom = "CTN", numerator = 24, denominator = 1 } } });
        var cap = await owner.PostAsync("/api/v1/items", new { code = "CAP", name = Name("Cap", "غطاء"), baseUom = "PCS", uoms = new object[] { new { uom = "DZ", numerator = 12, denominator = 1 } } });
        var label = await owner.PostAsync("/api/v1/items", new { code = "LABEL", name = Name("Label", "ملصق"), baseUom = "PCS" });
        var box = await owner.PostAsync("/api/v1/items", new { code = "BOX", name = Name("Gift box", "علبة هدايا"), baseUom = "PCS" });
        var service = await owner.PostAsync("/api/v1/items", new { code = "GIFTWRAP", name = Name("Gift wrapping", "تغليف"), type = "service", baseUom = "HR" });
        var filled = await owner.PostAsync("/api/v1/items", new { code = "FILLED", name = Name("Filled bottle", "قارورة معبأة"), type = "assembly", baseUom = "PCS" });
        var giftSet = await owner.PostAsync("/api/v1/items", new { code = "GIFTSET", name = Name("Gift set", "طقم هدايا"), type = "kit", baseUom = "PCS" });
        var filledId = filled.GetProperty("id").GetGuid();
        var giftSetId = giftSet.GetProperty("id").GetGuid();

        // The assembly: 12 filled bottles take half a carton of bottles, a dozen caps and 12 labels with 5% scrap.
        var v1 = await owner.PostAsync($"/api/v1/items/{filledId}/boms", new
        {
            kind = "assembly",
            outputQty = 12,
            lines = new object[]
            {
                new { componentItemCode = "BOTTLE", quantity = 0.5, uom = "CTN" },
                new { componentItemCode = "CAP", quantity = 1, uom = "DZ" },
                new { componentItemCode = "LABEL", quantity = 12, scrapPct = 5 },
            },
        });
        v1.GetProperty("version").GetInt32().ShouldBe(1);
        v1.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        var lines = v1.GetProperty("lines").EnumerateArray().ToList();
        lines[0].Dec("baseQuantity").ShouldBe(12m);
        lines[0].GetProperty("uomCode").GetString().ShouldBe("CTN");
        lines[1].Dec("baseQuantity").ShouldBe(12m);

        (await owner.PostErrorAsync($"/api/v1/items/{filledId}/boms", new { kind = "kit", lines = new object[] { new { componentItemCode = "BOTTLE" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bom.kind_mismatch");
        (await owner.PostErrorAsync($"/api/v1/items/{filledId}/boms", new { kind = "assembly", lines = new object[] { new { componentItemCode = "GIFTWRAP" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bom.component_not_stock");
        (await owner.PostErrorAsync($"/api/v1/items/{filledId}/boms", new { kind = "assembly", lines = new object[] { new { componentItemCode = "FILLED" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bom.cycle");
        (await owner.PostErrorAsync($"/api/v1/items/{filledId}/boms", new { kind = "assembly", lines = new object[] { new { componentItemCode = "BOTTLE", quantity = 0.3, uom = "CTN" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("quantity.not_exact_in_base");
        (await owner.PostErrorAsync($"/api/v1/items/{filledId}/boms", new { kind = "assembly", lines = new object[] { new { componentItemCode = "CAP", uom = "CTN" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bom.uom_not_item_uom");
        (await owner.PostErrorAsync($"/api/v1/items/{bottle.GetProperty("id").GetGuid()}/boms", new { kind = "assembly", lines = new object[] { new { componentItemCode = "CAP" } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("bom.kind_mismatch");

        // The kit: two filled bottles in a box; exploding three sets walks into the assembly.
        var kit = await owner.PostAsync($"/api/v1/items/{giftSetId}/boms", new { kind = "kit", lines = new object[] { new { componentItemCode = "FILLED", quantity = 2 }, new { componentItemCode = "BOX", quantity = 1 } } });
        var explosion = await owner.GetOkAsync($"/api/v1/items/boms/{kit.GetProperty("id").GetGuid()}/explode?quantity=3");
        var requirements = explosion.GetProperty("requirements").EnumerateArray().ToList();
        requirements.Select(r => r.GetProperty("itemCode").GetString()).ShouldBe(["FILLED", "BOTTLE", "CAP", "LABEL", "BOX"]);
        requirements[0].Dec("quantity").ShouldBe(6m);
        requirements[0].GetProperty("depth").GetInt32().ShouldBe(0);
        requirements[1].Dec("quantity").ShouldBe(6m, "six filled bottles need six bottles: half a carton per dozen");
        requirements[1].GetProperty("depth").GetInt32().ShouldBe(1);
        requirements[1].GetProperty("route").EnumerateArray().Select(static r => r.GetString()).ShouldBe(["GIFTSET", "FILLED", "BOTTLE"]);
        requirements[2].Dec("quantity").ShouldBe(6m);
        requirements[3].Dec("quantity").ShouldBe(6m);
        requirements[3].Dec("quantityWithScrap").ShouldBe(6.3m);
        requirements[4].Dec("quantity").ShouldBe(3m);

        // A cycle through nested bills is refused: the box cannot contain a gift set.
        var boxId = box.GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/items/{boxId}", new { code = "BOX", name = Name("Gift box", "علبة هدايا"), type = "assembly" });
        var (cycle, problem) = await owner.PostErrorAsync($"/api/v1/items/{boxId}/boms", new { kind = "assembly", lines = new object[] { new { componentItemCode = "GIFTSET" } } }, HttpStatusCode.UnprocessableEntity);
        cycle.ShouldBe("bom.cycle");
        problem.GetProperty("why").GetProperty("route").EnumerateArray().Select(static r => r.GetString()).ShouldBe(["BOX", "GIFTSET", "BOX"], "the kit lists the box directly, so that is the route found");

        // Versions: a second version retires the first; only one is active; the old one can be reactivated.
        var v2 = await owner.PostAsync($"/api/v1/items/{filledId}/boms", new { kind = "assembly", outputQty = 12, lines = new object[] { new { componentItemCode = "BOTTLE", quantity = 12 }, new { componentItemCode = "CAP", quantity = 12 } } });
        v2.GetProperty("version").GetInt32().ShouldBe(2);
        var versions = (await owner.GetOkAsync($"/api/v1/items/{filledId}/boms")).EnumerateArray().ToList();
        versions.Count.ShouldBe(2);
        versions.Single(b => b.GetProperty("isActive").GetBoolean()).GetProperty("version").GetInt32().ShouldBe(2);
        (await owner.PostAsync($"/api/v1/items/boms/{v1.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK)).GetProperty("isActive").GetBoolean().ShouldBeTrue();
        (await owner.GetOkAsync($"/api/v1/items/boms/{v2.GetProperty("id").GetGuid()}")).GetProperty("isActive").GetBoolean().ShouldBeFalse();
        (await owner.GetOkAsync($"/api/v1/items/boms/{kit.GetProperty("id").GetGuid()}/explode?quantity=1")).GetProperty("requirements").GetArrayLength().ShouldBe(5, "the active version is the one exploded");

        // A unit used by a bill line cannot be removed from the component; a variant in a bill cannot be deleted.
        var capUoms = (await owner.GetOkAsync($"/api/v1/items/{cap.GetProperty("id").GetGuid()}?expand=uoms")).GetProperty("uoms").EnumerateArray().ToList();
        var dozen = capUoms.Single(u => u.GetProperty("uomCode").GetString() == "DZ").GetProperty("id").GetGuid();
        (await owner.DeleteAsync(new Uri($"/api/v1/items/{cap.GetProperty("id").GetGuid()}/uoms/{dozen}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        _ = label;
        _ = service;
    }
}
