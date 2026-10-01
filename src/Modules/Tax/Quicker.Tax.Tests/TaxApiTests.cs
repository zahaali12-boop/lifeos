using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Tax.Contracts;

namespace Quicker.Tax.Tests;

/// <summary>
/// Tax through the API (roadmap 5.3): the Saudi and UAE templates installed and run on real documents' lines (dated
/// rates, zero-rating, exports, reverse charge on imported services and goods, exemption certificates, lines that name
/// their code, tax-inclusive prices, document rounding), the matrix refusing to guess, the set-up rules, and who may do
/// what.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class TaxApiTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Saudi(Workspace Ws, HttpClient Owner, Guid CompanyId, Guid RegimeId, JsonElement Regime, Guid Laptop, Guid Insulin, Guid Syringe, Guid Consulting, Guid Unclassified, Guid Domestic, Guid Foreign, Guid Ministry, Guid ForeignSupplier);

    private async Task<Saudi> SaudiAsync()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var company = (await owner.PostAsync("/api/v1/organization/companies", new { code = "KSA", legalName = Name("Riyadh Trading", "الرياض للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh" })).GetProperty("id").GetGuid();

        var installed = await owner.PostAsync("/api/v1/tax/templates/SA-VAT/install", new { });
        var regimeId = installed.GetProperty("regime").GetProperty("id").GetGuid();
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        var medical = (await owner.PostAsync("/api/v1/items/categories", new { code = "MED", name = Name("Medical", "طبي"), itemTaxGroupId = Group("item", "ZERO") })).GetProperty("id").GetGuid();
        var laptop = await ItemAsync(owner, "LAPTOP", Name("Laptop", "حاسوب محمول"), new { itemTaxGroupId = Group("item", "STANDARD") });
        var insulin = await ItemAsync(owner, "INSULIN", Name("Insulin pen", "قلم أنسولين"), new { itemTaxGroupId = Group("item", "ZERO") });
        var syringe = await ItemAsync(owner, "SYRINGE", Name("Syringe", "حقنة"), new { categoryId = medical });
        var consulting = await ItemAsync(owner, "CONSULT", Name("Consulting hour", "ساعة استشارة"), new { type = "service", itemTaxGroupId = Group("item", "SERVICES") });
        var unclassified = await ItemAsync(owner, "MISC", Name("Miscellaneous", "متفرقات"), new { });

        var domestic = await CustomerAsync(owner, company, "RIYADH-RETAIL", Name("Riyadh Retail", "تجزئة الرياض"), Group("partner", "DOMESTIC"));
        var foreign = await CustomerAsync(owner, company, "LONDON-TRADING", Name("London Trading Ltd", "لندن للتجارة"), Group("partner", "FOREIGN"));
        var ministry = await CustomerAsync(owner, company, "MOH", Name("Ministry of Health", "وزارة الصحة"), Group("partner", "DOMESTIC"));
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "DUBLIN-SOFT", legalName = Name("Dublin Software", "دبلن للبرمجيات"), isSupplier = true })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/partners/{supplier}/supplier-accounts/{company}", new { currency = "EUR", taxGroupId = Group("partner", "FOREIGN") }, Json)).EnsureSuccessStatusCode();
        return new Saudi(ws, owner, company, regimeId, installed, laptop, insulin, syringe, consulting, unclassified, domestic, foreign, ministry, supplier);
    }

    private static async Task<Guid> ItemAsync(HttpClient owner, string code, object name, object extra)
    {
        var body = JsonSerializer.SerializeToElement(extra, Json).EnumerateObject().ToDictionary(static p => p.Name, static p => (object?)p.Value, StringComparer.Ordinal);
        body["code"] = code;
        body["name"] = name;
        body["baseUom"] = "PCS";
        return (await owner.PostAsync("/api/v1/items", body)).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CustomerAsync(HttpClient owner, Guid company, string code, object name, Guid taxGroup)
    {
        var id = (await owner.PostAsync("/api/v1/partners", new { code, legalName = name, isCustomer = true })).GetProperty("id").GetGuid();
        (await owner.PutAsJsonAsync($"/api/v1/partners/{id}/customer-accounts/{company}", new { currency = "SAR", taxGroupId = taxGroup }, Json)).EnsureSuccessStatusCode();
        return id;
    }

    private static Guid CodeId(JsonElement regime, string code) => regime.GetProperty("codes").ByCode(code).GetProperty("id").GetGuid();

    private static async Task<JsonElement> CalculateAsync(HttpClient client, object request) => await client.PostAsync("/api/v1/tax/calculate", request, HttpStatusCode.OK);

    private static JsonElement Line(JsonElement result, string key) => result.GetProperty("document").GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("key").GetString() == key);

    private static JsonElement Why(JsonElement result, string key) => result.GetProperty("determinations").EnumerateArray().Single(l => l.GetProperty("key").GetString() == key);

    private static (decimal Net, decimal Tax, decimal Gross) Amounts(JsonElement line) => (line.GetProperty("net").GetDecimal(), line.GetProperty("tax").GetDecimal(), line.GetProperty("gross").GetDecimal());

    [Fact]
    public async Task Saudi_VAT_taxes_goods_services_zero_rated_lines_and_exports_by_the_matrix_at_the_rate_of_the_date()
    {
        var s = await SaudiAsync();
        var owner = s.Owner;
        var lines = new object[]
        {
            new { key = "laptop", amount = 4_000m, itemId = s.Laptop },
            new { key = "insulin", amount = 250m, itemId = s.Insulin },
            new { key = "syringe", amount = 10m, itemId = s.Syringe },
            new { key = "consult", amount = 1_200m, itemId = s.Consulting },
        };

        // Not registered yet: the company charges no tax, and says why.
        var unregistered = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, lines });
        unregistered.GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(0m);
        Why(unregistered, "laptop").GetProperty("reason").GetString().ShouldBe(TaxReasons.NotRegistered);

        var registration = await owner.PostAsync("/api/v1/tax/registrations", new { companyId = s.CompanyId, regimeId = s.RegimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });
        registration.GetProperty("regimeCode").GetString().ShouldBe("SA-VAT");

        var sale = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, lines });
        sale.GetProperty("regimeCode").GetString().ShouldBe("SA-VAT");
        Amounts(Line(sale, "laptop")).ShouldBe((4_000m, 600m, 4_600m));
        Line(sale, "laptop").GetProperty("taxCode").GetString().ShouldBe("SA-S");
        Amounts(Line(sale, "insulin")).ShouldBe((250m, 0m, 250m));
        Line(sale, "insulin").GetProperty("taxCode").GetString().ShouldBe("SA-Z");
        Line(sale, "syringe").GetProperty("taxCode").GetString().ShouldBe("SA-Z", "the item has no group of its own, so its category's applies");
        Amounts(Line(sale, "consult")).ShouldBe((1_200m, 180m, 1_380m));
        var laptopWhy = Why(sale, "laptop");
        laptopWhy.GetProperty("reason").GetString().ShouldBe(TaxReasons.Rule);
        laptopWhy.GetProperty("ruleId").GetGuid().ShouldNotBe(Guid.Empty);
        var document = sale.GetProperty("document");
        (document.GetProperty("net").GetDecimal(), document.GetProperty("tax").GetDecimal(), document.GetProperty("gross").GetDecimal()).ShouldBe((5_460m, 780m, 6_240m));
        document.GetProperty("byCode").EnumerateArray().Select(static c => (c.GetProperty("taxCode").GetString(), c.GetProperty("tax").GetDecimal())).ShouldBe([("SA-S", 780m), ("SA-Z", 0m)]);

        // The rate is the one in force on the tax date: 5% until the end of June 2020.
        var before = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2020-06-30", currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, lines });
        Amounts(Line(before, "laptop")).ShouldBe((4_000m, 200m, 4_200m));
        Line(before, "laptop").GetProperty("ratePct").GetDecimal().ShouldBe(5m);

        // The same goods and services to a customer abroad are exports, zero-rated; the more specific row wins.
        var export = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Foreign, lines });
        Line(export, "laptop").GetProperty("taxCode").GetString().ShouldBe("SA-X");
        Line(export, "consult").GetProperty("taxCode").GetString().ShouldBe("SA-X");
        Line(export, "insulin").GetProperty("taxCode").GetString().ShouldBe("SA-Z");
        export.GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(0m);

        // A shop price that includes VAT: the customer pays what the tag says and the tax is carved out of it.
        var inclusive = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = true, partnerId = s.Domestic, lines = new[] { new { key = "1", amount = 4_600m, itemId = s.Laptop }, new { key = "2", amount = 99.99m, itemId = s.Laptop } } });
        Amounts(Line(inclusive, "1")).ShouldBe((4_000m, 600m, 4_600m));
        Amounts(Line(inclusive, "2")).ShouldBe((86.95m, 13.04m, 99.99m));

        // An item with no tax group anywhere is refused, never taxed by guess.
        var (code, problem) = await owner.PostErrorAsync("/api/v1/tax/calculate", new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, lines = new[] { new { key = "misc", amount = 10m, itemId = s.Unclassified } } }, HttpStatusCode.UnprocessableEntity);
        code.ShouldBe("tax.item_unclassified");
        problem.GetProperty("why").GetProperty("line").GetString().ShouldBe("misc");
        problem.GetProperty("why").GetProperty("regime").GetString().ShouldBe("SA-VAT");

        // A sale without an item matches no row (the templates have none for sales), so the line must name its code.
        (code, problem) = await owner.PostErrorAsync("/api/v1/tax/calculate", new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, lines = new[] { new { key = "fee", amount = 10m } } }, HttpStatusCode.UnprocessableEntity);
        code.ShouldBe("tax.no_determination");
        problem.GetProperty("why").GetProperty("direction").GetString().ShouldBe("sales");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Imported_services_and_goods_are_self_assessed_and_an_exemption_certificate_or_a_chosen_code_overrides_the_matrix()
    {
        var s = await SaudiAsync();
        var owner = s.Owner;
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = s.CompanyId, regimeId = s.RegimeId, registrationNumber = "310123456700003", registeredFrom = "2018-01-01" });

        // Software support bought from Ireland: reverse charge, 15% self-assessed, nothing added to what the supplier is paid.
        var purchase = await CalculateAsync(owner, new
        {
            companyId = s.CompanyId,
            direction = "purchase",
            taxDate = "2026-09-25",
            currency = "EUR",
            pricesIncludeTax = false,
            partnerId = s.ForeignSupplier,
            lines = new object[] { new { key = "support", amount = 2_000m, itemId = s.Consulting }, new { key = "server", amount = 5_000m, itemId = s.Laptop } },
        });
        var support = Line(purchase, "support");
        support.GetProperty("taxCode").GetString().ShouldBe("SA-RC");
        support.GetProperty("isReverseCharge").GetBoolean().ShouldBeTrue();
        Amounts(support).ShouldBe((2_000m, 300m, 2_000m));
        Line(purchase, "server").GetProperty("taxCode").GetString().ShouldBe("SA-IMP", "goods from abroad take the import code, whose VAT is paid at customs");
        purchase.GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(750m, "the import VAT is charged; the reverse charge is not");

        // The Ministry holds an exemption certificate: taxed lines become exempt, zero-rated lines stay zero-rated.
        var exempt = CodeId(s.Regime, "SA-E");
        var certificate = await owner.PostAsync("/api/v1/tax/exemptions", new { partnerId = s.Ministry, regimeId = s.RegimeId, taxCodeId = exempt, certificateNumber = "MOH-EX-2026", validFrom = "2026-01-01", validTo = "2026-12-31" });
        certificate.GetProperty("partnerCode").GetString().ShouldBe("MOH");
        var ministry = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Ministry, lines = new[] { new { key = "laptop", amount = 4_000m, itemId = s.Laptop }, new { key = "insulin", amount = 250m, itemId = s.Insulin } } });
        Line(ministry, "laptop").GetProperty("taxCode").GetString().ShouldBe("SA-E");
        Why(ministry, "laptop").GetProperty("reason").GetString().ShouldBe(TaxReasons.Exemption);
        Why(ministry, "laptop").GetProperty("certificateNumber").GetString().ShouldBe("MOH-EX-2026");
        Line(ministry, "insulin").GetProperty("taxCode").GetString().ShouldBe("SA-Z");
        ministry.GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(0m);

        // Outside the certificate's dates the matrix applies again.
        var lapsed = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2027-01-01", currency = "SAR", pricesIncludeTax = false, partnerId = s.Ministry, lines = new[] { new { key = "laptop", amount = 4_000m, itemId = s.Laptop } } });
        Line(lapsed, "laptop").GetProperty("taxCode").GetString().ShouldBe("SA-S");

        // A line may name its code, which must be one of the company's regime.
        var chosen = await CalculateAsync(owner, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, lines = new[] { new { key = "donation", amount = 500m, taxCodeId = CodeId(s.Regime, "SA-O") } } });
        Why(chosen, "donation").GetProperty("reason").GetString().ShouldBe(TaxReasons.Chosen);
        var uae = await owner.PostAsync("/api/v1/tax/templates/AE-VAT/install", new { });
        (await owner.PostErrorAsync("/api/v1/tax/calculate", new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, lines = new[] { new { key = "x", amount = 1m, taxCodeId = CodeId(uae, "AE-S") } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax.code_not_regime");

        // Installing a second country reuses the tenant's groups by code instead of duplicating them.
        (await owner.GetOkAsync("/api/v1/tax/groups?kind=item")).GetArrayLength().ShouldBe(5);
        (await owner.GetOkAsync("/api/v1/tax/templates")).EnumerateArray().Where(static t => t.GetProperty("installed").GetBoolean()).Select(static t => t.GetProperty("code").GetString()).Order(StringComparer.Ordinal).ShouldBe(["AE-VAT", "SA-VAT"]);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task UAE_VAT_rounds_per_line_or_per_document_as_the_regime_or_the_company_asks_and_self_assesses_imported_goods()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var lineCompany = (await owner.PostAsync("/api/v1/organization/companies", new { code = "DXB", legalName = Name("Dubai Traders", "تجار دبي"), country = "AE", functionalCurrency = "AED", timeZone = "Asia/Dubai" })).GetProperty("id").GetGuid();
        var documentCompany = (await owner.PostAsync("/api/v1/organization/companies", new { code = "AUH", legalName = Name("Abu Dhabi Traders", "تجار أبوظبي"), country = "AE", functionalCurrency = "AED", timeZone = "Asia/Dubai", taxRoundingMode = "document" })).GetProperty("id").GetGuid();
        var regime = await owner.PostAsync("/api/v1/tax/templates/AE-VAT/install", new { });
        var regimeId = regime.GetProperty("regime").GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = lineCompany, regimeId, registrationNumber = "100123456700003", registeredFrom = "2018-01-01" });
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = documentCompany, regimeId, registrationNumber = "100765432100003", registeredFrom = "2018-01-01" });
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();

        // Three small lines at 5%: 0.005 each. Line by line that is 0.03; on the document it is 5% of 0.30 = 0.02.
        object Request(Guid company) => new
        {
            companyId = company,
            direction = "sales",
            taxDate = "2026-09-25",
            currency = "AED",
            pricesIncludeTax = false,
            partnerTaxGroupId = Group("partner", "DOMESTIC"),
            lines = new[] { "a", "b", "c" }.Select(k => new { key = k, amount = 0.10m, itemTaxGroupId = Group("item", "STANDARD") }).ToArray(),
        };
        var perLine = await CalculateAsync(owner, Request(lineCompany));
        perLine.GetProperty("document").GetProperty("roundingLevel").GetString().ShouldBe("line");
        perLine.GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(0.03m);
        var perDocument = await CalculateAsync(owner, Request(documentCompany));
        perDocument.GetProperty("document").GetProperty("roundingLevel").GetString().ShouldBe("document");
        perDocument.GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(0.02m);

        // The regime can ask for document rounding for every company registered in it.
        var summary = regime.GetProperty("regime");
        await owner.PutAsync($"/api/v1/tax/regimes/{regimeId}", new { name = Name("United Arab Emirates VAT", "ضريبة القيمة المضافة - الإمارات"), roundingLevel = "document", taxPoint = summary.GetProperty("taxPoint").GetString(), returnFrequency = summary.GetProperty("returnFrequency").GetString(), einvoicingScheme = (string?)null });
        (await CalculateAsync(owner, Request(lineCompany))).GetProperty("document").GetProperty("tax").GetDecimal().ShouldBe(0.02m);

        // Goods and services bought from abroad are both reverse charged, in their own boxes of the VAT 201.
        var imports = await CalculateAsync(owner, new
        {
            companyId = lineCompany,
            direction = "purchase",
            taxDate = "2026-09-25",
            currency = "USD",
            pricesIncludeTax = false,
            partnerTaxGroupId = Group("partner", "FOREIGN"),
            lines = new[] { new { key = "goods", amount = 10_000m, itemTaxGroupId = Group("item", "STANDARD") }, new { key = "svc", amount = 800m, itemTaxGroupId = Group("item", "SERVICES") } },
        });
        Line(imports, "goods").GetProperty("taxCode").GetString().ShouldBe("AE-IMP");
        Amounts(Line(imports, "goods")).ShouldBe((10_000m, 500m, 10_000m));
        Line(imports, "svc").GetProperty("taxCode").GetString().ShouldBe("AE-RC");
        imports.GetProperty("document").GetProperty("gross").GetDecimal().ShouldBe(10_800m);
        var codes = regime.GetProperty("codes");
        (codes.ByCode("AE-IMP").GetProperty("salesTaxBox").GetString(), codes.ByCode("AE-IMP").GetProperty("purchaseTaxBox").GetString()).ShouldBe(("6", "10"));
        (codes.ByCode("AE-RC").GetProperty("salesTaxBox").GetString(), codes.ByCode("AE-RC").GetProperty("purchaseTaxBox").GetString()).ShouldBe(("3", "10"));

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Codes_rates_matrix_rows_registrations_and_exemptions_are_checked_as_they_are_saved()
    {
        var s = await SaudiAsync();
        var owner = s.Owner;
        var path = $"/api/v1/tax/regimes/{s.RegimeId}";

        (await owner.PostErrorAsync("/api/v1/tax/templates/SA-VAT/install", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("tax.regime_exists");
        (await owner.PostErrorAsync("/api/v1/tax/templates/XX-NOPE/install", new { }, HttpStatusCode.NotFound)).Code.ShouldNotBeNull();

        // Codes: at least one rate, one per date; zero-rated and exempt codes at 0%; exempt codes print a reason.
        (await owner.PostErrorAsync($"{path}/codes", new { code = "SA-T", name = Name("Tourism", "سياحة"), kind = "vat", treatment = "standard", rates = Array.Empty<object>() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_code.rates_invalid");
        (await owner.PostErrorAsync($"{path}/codes", new { code = "SA-T", name = Name("Tourism", "سياحة"), kind = "vat", treatment = "standard", rates = new[] { new { validFrom = "2026-01-01", ratePct = 5 }, new { validFrom = "2026-01-01", ratePct = 6 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_code.rates_invalid");
        (await owner.PostErrorAsync($"{path}/codes", new { code = "SA-T", name = Name("Tourism", "سياحة"), kind = "vat", treatment = "zero_rated", rates = new[] { new { validFrom = "2026-01-01", ratePct = 5 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_code.rate_not_zero");
        (await owner.PostErrorAsync($"{path}/codes", new { code = "SA-T", name = Name("Tourism", "سياحة"), kind = "vat", treatment = "exempt", rates = new[] { new { validFrom = "2026-01-01", ratePct = 0 } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_code.exemption_reason_required");
        (await owner.PostErrorAsync($"{path}/codes", new { code = "SA-S", name = Name("Standard", "أساسي"), kind = "vat", treatment = "standard", rates = new[] { new { validFrom = "2026-01-01", ratePct = 15 } } }, HttpStatusCode.Conflict)).Code.ShouldBe("tax_code.code_taken");

        // A new rate from a date: the code keeps its history and the current rate follows the calendar.
        var tourism = await owner.PostAsync($"{path}/codes", new { code = "SA-T", name = Name("Tourism levy", "رسوم السياحة"), kind = "vat", treatment = "standard", rates = new[] { new { validFrom = "2024-01-01", ratePct = 5 } }, salesBaseBox = "1", salesTaxBox = "1" });
        var tourismId = tourism.GetProperty("id").GetGuid();
        var updated = await owner.PutAsync($"{path}/codes/{tourismId}", new { code = "SA-T", name = Name("Tourism levy", "رسوم السياحة"), kind = "vat", treatment = "standard", rates = new[] { new { validFrom = "2024-01-01", ratePct = 5 }, new { validFrom = "2099-01-01", ratePct = 7 } }, salesBaseBox = "1", salesTaxBox = "1" });
        updated.GetProperty("rates").GetArrayLength().ShouldBe(2);
        updated.GetProperty("currentRatePct").GetDecimal().ShouldBe(5m);

        // Matrix rows: the item group is an item group, countries are ISO codes, and a combination has one code per date.
        var groups = await owner.GetOkAsync("/api/v1/tax/groups");
        Guid Group(string kind, string code) => groups.EnumerateArray().Single(g => g.GetProperty("kind").GetString() == kind && g.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
        (await owner.PostErrorAsync($"{path}/rules", new { direction = "sales", taxCodeId = tourismId, itemTaxGroupId = Group("partner", "GCC") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_rule.group_invalid");
        (await owner.PostErrorAsync($"{path}/rules", new { direction = "sales", taxCodeId = tourismId, shipToCountry = "Saudi" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_rule.country_invalid");
        (await owner.PostErrorAsync($"{path}/rules", new { direction = "sales", taxCodeId = tourismId, itemTaxGroupId = Group("item", "STANDARD") }, HttpStatusCode.Conflict)).Code.ShouldBe("tax_rule.duplicate");

        // A row that ships to Bahrain is more specific than the plain standard row, from its date only.
        var hotel = await owner.PostAsync($"{path}/rules", new { direction = "sales", taxCodeId = tourismId, itemTaxGroupId = Group("item", "STANDARD"), shipToCountry = "bh", validFrom = "2026-01-01" });
        hotel.GetProperty("shipToCountry").GetString().ShouldBe("BH");
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = s.CompanyId, regimeId = s.RegimeId, registrationNumber = "310123456700003" });
        object ToBahrain(string date) => new { companyId = s.CompanyId, direction = "sales", taxDate = date, currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, shipToCountry = "BH", lines = new[] { new { key = "1", amount = 100m, itemId = s.Laptop } } };
        Line(await CalculateAsync(owner, ToBahrain("2026-09-25")), "1").GetProperty("taxCode").GetString().ShouldBe("SA-T");
        Line(await CalculateAsync(owner, ToBahrain("2025-12-31")), "1").GetProperty("taxCode").GetString().ShouldBe("SA-S");
        await owner.DeleteOkAsync($"{path}/rules/{hotel.GetProperty("id").GetGuid()}");
        Line(await CalculateAsync(owner, ToBahrain("2026-09-25")), "1").GetProperty("taxCode").GetString().ShouldBe("SA-S");

        // One registration per company and regime; exemptions name an exempt kind of code and do not overlap.
        (await owner.PostErrorAsync("/api/v1/tax/registrations", new { companyId = s.CompanyId, regimeId = s.RegimeId, registrationNumber = "310000000000003" }, HttpStatusCode.Conflict)).Code.ShouldBe("tax_registration.duplicate");
        (await owner.PostErrorAsync("/api/v1/tax/exemptions", new { partnerId = s.Ministry, regimeId = s.RegimeId, taxCodeId = CodeId(s.Regime, "SA-S"), certificateNumber = "X", validFrom = "2026-01-01" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("tax_exemption.code_invalid");
        await owner.PostAsync("/api/v1/tax/exemptions", new { partnerId = s.Ministry, regimeId = s.RegimeId, taxCodeId = CodeId(s.Regime, "SA-E"), certificateNumber = "A", validFrom = "2026-01-01", validTo = "2026-06-30" });
        (await owner.PostErrorAsync("/api/v1/tax/exemptions", new { partnerId = s.Ministry, regimeId = s.RegimeId, taxCodeId = CodeId(s.Regime, "SA-E"), certificateNumber = "B", validFrom = "2026-06-01" }, HttpStatusCode.Conflict)).Code.ShouldBe("tax_exemption.overlap");
        await owner.PostAsync("/api/v1/tax/exemptions", new { partnerId = s.Ministry, regimeId = s.RegimeId, taxCodeId = CodeId(s.Regime, "SA-E"), certificateNumber = "B", validFrom = "2026-07-01" });

        // Items and accounts point only at tax groups of their kind.
        (await owner.PostErrorAsync("/api/v1/items", new { code = "BAD", name = Name("Bad", "سيئ"), baseUom = "PCS", itemTaxGroupId = Group("partner", "GCC") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("item.tax_group_invalid");
        (await owner.PostErrorAsync("/api/v1/items/categories", new { code = "BAD", name = Name("Bad", "سيئ"), itemTaxGroupId = Guid.CreateVersion7() }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("category.tax_group_invalid");
        (await owner.PutErrorAsync($"/api/v1/partners/{s.Domestic}/customer-accounts/{s.CompanyId}", new { currency = "SAR", taxGroupId = Group("item", "ZERO") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("customer.tax_group_invalid");
        (await owner.PutErrorAsync($"/api/v1/partners/{s.ForeignSupplier}/supplier-accounts/{s.CompanyId}", new { currency = "EUR", taxGroupId = Group("item", "ZERO") }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("supplier.tax_group_invalid");
    }

    [Fact]
    public async Task A_member_with_tax_read_calculates_in_their_companies_only_and_cannot_change_the_set_up()
    {
        var s = await SaudiAsync();
        var owner = s.Owner;
        var other = (await owner.PostAsync("/api/v1/organization/companies", new { code = "JED", legalName = Name("Jeddah Trading", "جدة للتجارة"), country = "SA", functionalCurrency = "SAR", timeZone = "Asia/Riyadh" })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/tax/registrations", new { companyId = s.CompanyId, regimeId = s.RegimeId, registrationNumber = "310123456700003" });

        var role = (await owner.PostAsync("/api/v1/roles", new { code = "tax_reader", name = Name("Tax reader", "قارئ الضرائب"), description = "", grants = new[] { TaxPermissions.SetupRead } })).GetProperty("id").GetGuid();
        var email = $"tax-{s.Ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Tax reader", roleIds = Array.Empty<Guid>() }, HttpStatusCode.Created);
        (await owner.PostAsJsonAsync($"/api/v1/users/{invited.GetProperty("membershipId").GetGuid()}/assignments", new { roleId = role, scopes = new[] { new { scopeType = "company", scopeId = s.CompanyId } } }, Json)).EnsureSuccessStatusCode();
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        var reader = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);

        (await reader.GetOkAsync("/api/v1/tax/regimes")).Only().GetProperty("code").GetString().ShouldBe("SA-VAT");
        (await reader.GetOkAsync("/api/v1/tax/registrations")).Only().GetProperty("companyId").GetGuid().ShouldBe(s.CompanyId);
        Line(await CalculateAsync(reader, new { companyId = s.CompanyId, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, partnerId = s.Domestic, lines = new[] { new { key = "1", amount = 100m, itemId = s.Laptop } } }), "1").GetProperty("tax").GetDecimal().ShouldBe(15m);
        (await reader.PostErrorAsync("/api/v1/tax/calculate", new { companyId = other, direction = "sales", taxDate = "2026-09-25", currency = "SAR", pricesIncludeTax = false, lines = Array.Empty<object>() }, HttpStatusCode.Forbidden)).Code.ShouldBe("tax.company_forbidden");
        (await reader.PostErrorAsync("/api/v1/tax/templates/AE-VAT/install", new { }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
        (await reader.PostErrorAsync("/api/v1/tax/groups", new { kind = "item", code = "MINE", name = Name("Mine", "لي") }, HttpStatusCode.Forbidden)).Code.ShouldNotBeNull();
    }
}
