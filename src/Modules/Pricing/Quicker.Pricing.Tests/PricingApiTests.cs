using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Pricing.Contracts;

namespace Quicker.Pricing.Tests;

/// <summary>
/// Pricing through the API (roadmap 5.2): hard scenario 14 end to end, the rule set managed and validated per
/// company, the permissions around it, and promotion usage limits holding when documents are confirmed at once.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PricingApiTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Setup(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid OtherCompanyId, Guid Tea, Guid Cup, Guid Beverages, Guid Customer);

    private async Task<Setup> SetUpAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "TRD", legalName = Name("Trading Co", "شركة التجارة"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" })).GetProperty("id").GetGuid();
        var other = (await owner.PostAsync("/api/v1/organization/companies", new { code = "EXP", legalName = Name("Export Co", "شركة التصدير"), country = "AE", functionalCurrency = "AED", timeZone = "Asia/Dubai" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = "IQD", validFrom = "2026-01-01", rate = 1310m });
        var beverages = (await owner.PostAsync("/api/v1/items/categories", new { code = "BEV", name = Name("Beverages", "مشروبات") })).GetProperty("id").GetGuid();
        var teaCategory = (await owner.PostAsync("/api/v1/items/categories", new { code = "TEA", name = Name("Tea", "شاي"), parentId = beverages })).GetProperty("id").GetGuid();
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA-100", name = Name("Black tea 100 bags", "شاي أسود ١٠٠ كيس"), baseUom = "PCS", categoryId = teaCategory, uoms = new object[] { new { uom = "CTN", numerator = 12, denominator = 1 } } })).GetProperty("id").GetGuid();
        var cup = (await owner.PostAsync("/api/v1/items", new { code = "CUP", name = Name("Tea glass", "استكان"), baseUom = "PCS" })).GetProperty("id").GetGuid();
        var customer = (await owner.PostAsync("/api/v1/partners", new { code = "BASRA-OIL", legalName = Name("Basra Oil Services", "خدمات نفط البصرة"), isCustomer = true })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/partners/{customer}/customer-accounts/{company}", new { currency = "USD" }, Json)).EnsureSuccessStatusCode();
        return new Setup(ws, owner, company, other, tea, cup, beverages, customer);
    }

    private static async Task<Guid> UomAsync(HttpClient client, string code) =>
        (await client.GetOkAsync("/api/v1/organization/uoms")).EnumerateArray().Single(u => u.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

    [Fact]
    public async Task Scenario_14_a_quantity_break_on_a_customer_price_a_promotion_and_a_foreign_currency_on_one_line_price_the_same_every_time_and_explain_themselves()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var ctn = await UomAsync(owner, "CTN");

        // The company's own prices, which the customer's agreement beats.
        var standard = (await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "STD", name = Name("Standard", "قائمة الأسعار العامة"), currency = "IQD", isDefault = true })).GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/pricing/price-lists/{standard}/items", new { itemId = s.Tea, uomId = ctn, minQuantity = 0, price = 30_000 });

        // Basra Oil's agreement, in dinars per carton: 27,000 from one carton, 25,000 from ten.
        await owner.PostAsync("/api/v1/pricing/agreements", new { companyId = s.CompanyId, partnerId = s.Customer, reference = "BOS-2026", itemId = s.Tea, uomId = ctn, minQuantity = 0, price = 27_000, currency = "IQD" });
        await owner.PostAsync("/api/v1/pricing/agreements", new { companyId = s.CompanyId, partnerId = s.Customer, reference = "BOS-2026-10", itemId = s.Tea, uomId = ctn, minQuantity = 10, price = 25_000, currency = "IQD" });

        // A volume promotion on beverages: 5% from a hundred pieces.
        await owner.PostAsync("/api/v1/pricing/promotions", new { companyId = s.CompanyId, code = "BEV-VOLUME", name = Name("Beverage volume", "خصم كميات المشروبات"), kind = "volume_tier", categoryId = s.Beverages, tiers = new[] { new { minQuantity = 100, discountPct = 5 } } });

        // Twelve cartons (144 pieces) for Basra Oil, whose account is in dollars.
        var request = new { companyId = s.CompanyId, partnerId = s.Customer, lines = new[] { new { key = "1", itemId = s.Tea, uomId = ctn, quantity = 12 } } };
        var first = await owner.PostAsync("/api/v1/pricing/calculate", request, HttpStatusCode.OK);
        first.GetProperty("currency").GetString().ShouldBe("USD");
        var line = first.GetProperty("lines").Only();
        line.GetProperty("priceSource").GetString().ShouldBe(PriceSources.Agreement);

        // 25,000 IQD at 1/1310 is 19.0840 USD a carton (dollar prices keep four decimals); twelve are 229.01; 5% off is 11.45.
        line.GetProperty("unitPrice").GetDecimal().ShouldBe(19.084m);
        line.GetProperty("grossAmount").GetDecimal().ShouldBe(229.01m);
        line.GetProperty("promotionDiscountAmount").GetDecimal().ShouldBe(11.45m);
        line.GetProperty("netAmount").GetDecimal().ShouldBe(217.56m);
        first.GetProperty("netAmount").GetDecimal().ShouldBe(217.56m);

        // Why this price: the agreement's ten-carton break, the rate that converted it, the volume tier reached.
        var steps = line.GetProperty("steps").EnumerateArray().ToList();
        steps.Select(static st => st.GetProperty("kind").GetString()).ShouldBe([PriceStepKinds.BasePrice, PriceStepKinds.Currency, PriceStepKinds.Promotion]);
        steps[0].GetProperty("refCode").GetString().ShouldBe("BOS-2026-10");
        Facts(steps[0]).ShouldContainKeyAndValue("minQuantity", "10");
        Facts(steps[0]).ShouldContainKeyAndValue("uom", "CTN");
        Facts(steps[0]).ShouldContainKeyAndValue("currency", "IQD");
        Facts(steps[1]).ShouldContainKeyAndValue("from", "IQD");
        Facts(steps[1]).ShouldContainKeyAndValue("to", "USD");
        Facts(steps[1]).ShouldContainKeyAndValue("rateMethod", "inverse");
        steps[2].GetProperty("refCode").GetString().ShouldBe("BEV-VOLUME");
        Facts(steps[2]).ShouldContainKeyAndValue("quantity", "144");
        Facts(steps[2]).ShouldContainKeyAndValue("tier", "100");

        // Deterministic: the same request prices the same, to the last character of the explanation.
        var again = await owner.PostAsync("/api/v1/pricing/calculate", request, HttpStatusCode.OK);
        again.GetRawText().ShouldBe(first.GetRawText());

        // Nine cartons fall under the ten-carton break: 27,000 IQD a carton.
        var nine = await owner.PostAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, partnerId = s.Customer, lines = new[] { new { key = "1", itemId = s.Tea, uomId = ctn, quantity = 9 } } }, HttpStatusCode.OK);
        nine.GetProperty("lines").Only().GetProperty("unitPrice").GetDecimal().ShouldBe(20.6107m);

        // Another customer, in dinars, gets the standard list and the same promotion.
        var walkIn = await owner.PostAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, lines = new[] { new { key = "1", itemId = s.Tea, uomId = ctn, quantity = 12 } } }, HttpStatusCode.OK);
        var standardLine = walkIn.GetProperty("lines").Only();
        standardLine.GetProperty("priceSource").GetString().ShouldBe(PriceSources.DefaultList);
        standardLine.GetProperty("netAmount").GetDecimal().ShouldBe(342_000m);
    }

    private static Dictionary<string, string> Facts(JsonElement step) =>
        step.GetProperty("facts").EnumerateArray().ToDictionary(static f => f.GetProperty("key").GetString()!, static f => f.GetProperty("value").GetString()!, StringComparer.Ordinal);

    [Fact]
    public async Task The_rule_set_is_managed_per_company_and_refuses_what_would_make_prices_ambiguous()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        var pcs = await UomAsync(owner, "PCS");

        var baseList = (await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "base", name = Name("Base", "الأساس"), currency = "IQD", isDefault = true })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "BASE", name = Name("Again", "مرة أخرى"), currency = "IQD" }, HttpStatusCode.Conflict)).Code.ShouldBe("price_list.code_taken");
        (await owner.PostErrorAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "ODD", name = Name("Odd", "غريب"), currency = "IQD", parentAdjustmentPct = 5 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_list.derivation_without_parent");
        var foreign = (await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.OtherCompanyId, code = "AED", name = Name("Dirham", "درهم"), currency = "AED" })).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "X", name = Name("X", "X"), currency = "IQD", parentListId = foreign, parentAdjustmentPct = 5 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_list.parent_not_company");

        // A wholesale list 8% under the base list, rounded to 250 dinars; the base list cannot then derive from it.
        var wholesale = await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "WHOLESALE", name = Name("Wholesale", "الجملة"), currency = "IQD", parentListId = baseList, parentAdjustmentPct = -8, roundingIncrement = 250, partnerIds = new[] { s.Customer } });
        var wholesaleId = wholesale.GetProperty("id").GetGuid();
        wholesale.GetProperty("parentCode").GetString().ShouldBe("BASE");
        wholesale.GetProperty("customers").Only().GetProperty("code").GetString().ShouldBe("BASRA-OIL");
        (await owner.PutErrorAsync($"/api/v1/pricing/price-lists/{baseList}", new { companyId = s.CompanyId, code = "BASE", name = Name("Base", "الأساس"), currency = "IQD", parentListId = wholesaleId, parentAdjustmentPct = 1 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_list.derivation_cycle");

        // Prices: one per item, unit, break and start; a unit the item does not have is refused.
        await owner.PostAsync($"/api/v1/pricing/price-lists/{baseList}/items", new { itemId = s.Tea, uomId = pcs, minQuantity = 0, price = 2_500 });
        (await owner.PostErrorAsync($"/api/v1/pricing/price-lists/{baseList}/items", new { itemId = s.Tea, uomId = pcs, minQuantity = 0, price = 2_400 }, HttpStatusCode.Conflict)).Code.ShouldBe("price_list_item.duplicate");
        (await owner.PostErrorAsync($"/api/v1/pricing/price-lists/{baseList}/items", new { itemId = s.Cup, uomId = await UomAsync(owner, "CTN"), minQuantity = 0, price = 100 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_list_item.uom_not_item_unit");

        // The customer on the wholesale list pays 2,500 less 8% = 2,300, rounded to 2,250.
        var derived = await owner.PostAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, partnerId = s.Customer, currency = "IQD", lines = new[] { new { key = "1", itemId = s.Tea, quantity = 1 } } }, HttpStatusCode.OK);
        derived.GetProperty("lines").Only().GetProperty("unitPrice").GetDecimal().ShouldBe(2_250m);

        // A yearly increase of 10%, to the nearest 250: 2,500 becomes 2,750.
        (await owner.PostAsync($"/api/v1/pricing/price-lists/{baseList}/adjust", new { pct = 10, roundingIncrement = 250 }, HttpStatusCode.OK)).GetProperty("changed").GetInt32().ShouldBe(1);
        (await owner.GetOkAsync($"/api/v1/pricing/price-lists/{baseList}/items")).Only().GetProperty("price").GetDecimal().ShouldBe(2_750m);

        // Making another list the default hands the role over; a parent list is not deleted under its children.
        await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "NEW", name = Name("New", "جديد"), currency = "IQD", isDefault = true });
        (await owner.GetOkAsync($"/api/v1/pricing/price-lists?companyId={s.CompanyId}")).ByCode("BASE").GetProperty("isDefault").GetBoolean().ShouldBeFalse();
        var blocked = await owner.DeleteAsync(new Uri($"/api/v1/pricing/price-lists/{baseList}", UriKind.Relative));
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Agreements, rules, promotions and floors refuse what cannot be priced unambiguously.
        (await owner.PostErrorAsync("/api/v1/pricing/agreements", new { companyId = s.CompanyId, partnerId = s.Customer, itemId = s.Tea, minQuantity = 0, price = 2_000, discountPct = 5 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_agreement.value_invalid");
        (await owner.PostErrorAsync("/api/v1/pricing/agreements", new { companyId = s.CompanyId, partnerId = s.Customer, categoryId = s.Beverages, minQuantity = 0, price = 2_000 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_agreement.price_invalid");
        var agreement = await owner.PostAsync("/api/v1/pricing/agreements", new { companyId = s.CompanyId, partnerId = s.Customer, categoryId = s.Beverages, minQuantity = 0, discountPct = 4 });
        agreement.GetProperty("category").GetProperty("code").GetString().ShouldBe("BEV");
        (await owner.PostErrorAsync("/api/v1/pricing/discount-rules", new { companyId = s.CompanyId, code = "DOC", name = Name("Doc", "مستند"), level = "document", valueType = "fixed_price", value = 1 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("discount_rule.document_scope_invalid");
        (await owner.PostErrorAsync("/api/v1/pricing/discount-rules", new { companyId = s.CompanyId, code = "BIG", name = Name("Big", "كبير"), level = "line", valueType = "percentage", value = 150 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("discount_rule.value_invalid");
        var rule = await owner.PostAsync("/api/v1/pricing/discount-rules", new { companyId = s.CompanyId, code = "weekend", name = Name("Weekend", "عطلة"), level = "line", valueType = "percentage", value = 3, combination = "stackable", weekdays = new[] { 5, 6, 5 }, categoryId = s.Beverages });
        rule.GetProperty("weekdays").EnumerateArray().Select(static d => d.GetInt32()).ShouldBe([5, 6]);
        (await owner.PostErrorAsync("/api/v1/pricing/promotions", new { companyId = s.CompanyId, code = "B1", name = Name("Bundle", "حزمة"), kind = "bundle", bundlePrice = 10, components = new[] { new { itemId = s.Tea, quantity = 1 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("promotion.bundle_invalid");
        await owner.PostAsync("/api/v1/pricing/promotions", new { companyId = s.CompanyId, code = "R10", name = Name("Ramadan", "رمضان"), kind = "coupon", couponCode = "ramadan10", discountPct = 10 });
        (await owner.PostErrorAsync("/api/v1/pricing/promotions", new { companyId = s.CompanyId, code = "R11", name = Name("Again", "مرة أخرى"), kind = "coupon", couponCode = "RAMADAN10", discountPct = 11 }, HttpStatusCode.Conflict)).Code.ShouldBe("promotion.coupon_taken");
        var bundle = await owner.PostAsync("/api/v1/pricing/promotions", new { companyId = s.CompanyId, code = "TEA-SET", name = Name("Tea set", "طقم شاي"), kind = "bundle", bundlePrice = 20_000, components = new[] { new { itemId = s.Tea, quantity = 6 }, new { itemId = s.Cup, quantity = 6 } } });
        bundle.GetProperty("components").GetArrayLength().ShouldBe(2);
        bundle.GetProperty("currency").GetString().ShouldBe("IQD");
        (await owner.PostErrorAsync("/api/v1/pricing/floors", new { companyId = s.CompanyId, itemId = s.Tea, categoryId = s.Beverages, minMarginPct = 5 }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("price_floor.scope_invalid");
        await owner.PostAsync("/api/v1/pricing/floors", new { companyId = s.CompanyId, itemId = s.Tea, minPrice = 2_000 });
        (await owner.PostErrorAsync("/api/v1/pricing/floors", new { companyId = s.CompanyId, itemId = s.Tea, minMarginPct = 5 }, HttpStatusCode.Conflict)).Code.ShouldBe("price_floor.duplicate");
    }

    [Fact]
    public async Task A_member_sees_and_prices_only_in_their_companies_and_types_prices_by_hand_only_with_the_override()
    {
        var s = await SetUpAsync();
        var owner = s.Owner;
        await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "IQD", name = Name("Dinar", "دينار"), currency = "IQD", isDefault = true });
        await owner.PostAsync("/api/v1/pricing/price-lists", new { companyId = s.OtherCompanyId, code = "AED", name = Name("Dirham", "درهم"), currency = "AED", isDefault = true });

        var role = (await owner.PostAsync("/api/v1/roles", new { code = "price_reader", name = Name("Price reader", "قارئ الأسعار"), description = "", grants = new[] { PricingPermissions.Read } })).GetProperty("id").GetGuid();
        var email = $"reader-{s.Ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Reader", roleIds = Array.Empty<Guid>() }, HttpStatusCode.Created);
        (await owner.PostAsJsonAsync($"/api/v1/users/{invited.GetProperty("membershipId").GetGuid()}/assignments", new { roleId = role, scopes = new[] { new { scopeType = "company", scopeId = s.CompanyId } } }, Json)).EnsureSuccessStatusCode();
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        var reader = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);

        (await reader.GetOkAsync("/api/v1/pricing/price-lists")).Only().GetProperty("code").GetString().ShouldBe("IQD");
        (await reader.PostErrorAsync("/api/v1/pricing/price-lists", new { companyId = s.CompanyId, code = "MINE", name = Name("Mine", "لي"), currency = "IQD" }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
        (await reader.PostErrorAsync("/api/v1/pricing/calculate", new { companyId = s.OtherCompanyId, lines = new[] { new { key = "1", itemId = s.Tea, quantity = 1 } } }, HttpStatusCode.Forbidden)).Code.ShouldBe("pricing.company_forbidden");
        (await reader.PostErrorAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, lines = new[] { new { key = "1", itemId = s.Tea, quantity = 1, manualUnitPrice = 5 } } }, HttpStatusCode.Forbidden)).Code.ShouldBe("pricing.override_forbidden");

        // What the reader may do: price a basket in their company, and see why a line has no price.
        var unpriced = await reader.PostAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, lines = new[] { new { key = "1", itemId = s.Tea, quantity = 1 } } }, HttpStatusCode.OK);
        unpriced.GetProperty("unpricedLines").GetInt32().ShouldBe(1);
        unpriced.GetProperty("lines").Only().GetProperty("problem").GetProperty("code").GetString().ShouldBe("pricing.no_price");

        // The owner may type a price, and the explanation says it was typed.
        var typed = await owner.PostAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, lines = new[] { new { key = "1", itemId = s.Tea, quantity = 2, manualUnitPrice = 4_000 } } }, HttpStatusCode.OK);
        var typedLine = typed.GetProperty("lines").Only();
        typedLine.GetProperty("priceSource").GetString().ShouldBe(PriceSources.Manual);
        typedLine.GetProperty("netAmount").GetDecimal().ShouldBe(8_000m);
    }

    [Fact]
    public async Task A_promotion_with_one_use_left_is_taken_by_one_of_two_documents_confirmed_at_once_and_given_back_on_cancellation()
    {
        var s = await SetUpAsync();
        var promotion = (await s.Owner.PostAsync("/api/v1/pricing/promotions", new { companyId = s.CompanyId, code = "LAST-ONE", name = Name("Last one", "الأخيرة"), kind = "coupon", couponCode = "LAST", discountPct = 20, usageLimit = 1 })).GetProperty("id").GetGuid();
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        Task<bool> Confirm(Guid document) => host.InTenantAsync(s.Ws.TenantId, async (services, ct) =>
            (await services.GetRequiredService<IPricing>().RecordPromotionUsageAsync(new PromotionUsageRequest(s.CompanyId, s.Customer, "sales_order", document, [promotion]), ct)).IsSuccess);

        var outcomes = await Task.WhenAll(Confirm(first), Confirm(second));
        outcomes.Count(static ok => ok).ShouldBe(1);
        var winner = outcomes[0] ? first : second;
        var loser = outcomes[0] ? second : first;

        // Confirming the same document again changes nothing; the promotion now shows its one use.
        (await Confirm(winner)).ShouldBeTrue();
        (await s.Owner.GetOkAsync($"/api/v1/pricing/promotions?companyId={s.CompanyId}")).Only().GetProperty("used").GetInt32().ShouldBe(1);
        (await s.Owner.PostAsync("/api/v1/pricing/calculate", new { companyId = s.CompanyId, couponCodes = new[] { "LAST" }, lines = new[] { new { key = "1", itemId = s.Tea, quantity = 1, manualUnitPrice = 1_000 } } }, HttpStatusCode.OK))
            .GetProperty("documentSteps").Only().GetProperty("candidates").Only().GetProperty("outcome").GetString().ShouldBe(CandidateOutcomes.UsageLimitReached);

        // The winner is cancelled: the use comes back and the other document takes it.
        await host.InTenantAsync(s.Ws.TenantId, async (services, ct) => (await services.GetRequiredService<IPricing>().ReleasePromotionUsageAsync("sales_order", winner, ct)).IsSuccess);
        (await Confirm(loser)).ShouldBeTrue();
        (await Confirm(winner)).ShouldBeFalse();

        // A promotion that documents used is deactivated, not deleted.
        var delete = await s.Owner.DeleteAsync(new Uri($"/api/v1/pricing/promotions/{promotion}", UriKind.Relative));
        delete.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}
