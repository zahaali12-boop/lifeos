using System.Net;
using System.Text;
using System.Text.Json;
using Quicker.Accounting.Application;
using Quicker.Accounting.Contracts;
using Quicker.Identity.TestSupport;

namespace Quicker.Accounting.Tests;

/// <summary>Roadmap 2.1 through the API: templates, the account tree and its rules, dimension rules and line checks, import/export, statutory mappings, company assignment.</summary>
[Collection(ApiCollection.Name)]
public sealed class ChartOfAccountsTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public void Every_template_covers_every_posting_role_with_a_control_account_where_the_role_needs_one()
    {
        foreach (var template in ChartTemplates.All)
        {
            template.Roles.ShouldBe(AccountRoles.All.Order(StringComparer.Ordinal), ignoreOrder: false, $"{template.Code} names a default account for every role");
            template.Accounts.Select(static a => a.Code).ShouldBeUnique();
            foreach (var account in template.Accounts.Where(static a => a.Role is not null))
            {
                account.IsHeader.ShouldBeFalse(account.Code);
                var expected = AccountRoles.ControlSubledgers.GetValueOrDefault(account.Role!);
                account.Subledger.ShouldBe(expected, $"{template.Code} {account.Code} ({account.Role})");
                account.IsControl.ShouldBe(expected is not null, account.Code);
                account.AllowManualPosting.ShouldBe(expected is null, $"control accounts take documents only ({account.Code})");
            }

            foreach (var account in template.Accounts.Where(static a => a.Parent is not null))
            {
                var parent = template.Accounts.Single(p => p.Code == account.Parent);
                parent.IsHeader.ShouldBeTrue(parent.Code);
                parent.Type.ShouldBe(account.Type, account.Code);
            }

            template.Accounts.ShouldAllBe(static a => !string.IsNullOrWhiteSpace(a.Name.Get("en")) && !string.IsNullOrWhiteSpace(a.Name.Get("ar")), "every account is bilingual");
        }

        var iraq = ChartTemplates.Find("iraq_uas")!;
        iraq.StatutoryChartCode.ShouldBe("IRAQ_UAS");
        iraq.StatutoryMapping.Keys.ShouldBe(iraq.Accounts.Where(static a => !a.IsHeader && a.Code != "9100").Select(static a => a.Code), ignoreOrder: true, "every postable account except suspense maps to a statutory group");
        ChartTemplates.Find("GCC")!.Accounts.ShouldContain(static a => a.Code == "2235" && a.Name.Get("en") == "Zakat payable");
    }

    [Fact]
    public async Task A_company_created_from_a_template_has_a_complete_chart_and_exports_and_reimports_losslessly()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await owner.CompanyAsync("IQT");

        var templates = await owner.GetOkAsync("/api/v1/accounting/chart-templates");
        templates.EnumerateArray().Select(static t => t.GetProperty("code").GetString()).ShouldBe(["IFRS_SME", "GCC", "IRAQ_UAS"]);
        templates.EnumerateArray().First().GetProperty("roles").GetArrayLength().ShouldBe(AccountRoles.All.Count);

        var chart = await owner.ChartFromTemplateAsync("IFRS_SME", "MAIN", companyId);
        var chartId = chart.GetProperty("id").GetGuid();
        chart.GetProperty("templateCode").GetString().ShouldBe("IFRS_SME");
        chart.GetProperty("accountCodeFormat").GetString().ShouldBe("####");
        chart.GetProperty("accountCount").GetInt32().ShouldBe(ChartTemplates.Find("IFRS_SME")!.Accounts.Count);
        chart.GetProperty("companyId").ValueKind.ShouldBe(JsonValueKind.Null, "shared by default");

        var settings = await owner.GetOkAsync($"/api/v1/accounting/companies/{companyId}/settings");
        settings.GetProperty("chartId").GetGuid().ShouldBe(chartId);
        settings.GetProperty("chartCode").GetString().ShouldBe("MAIN");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}")).GetProperty("chartId").GetGuid().ShouldBe(chartId);

        var tree = (await owner.GetOkAsync($"/api/v1/accounting/charts/{chartId}?expand=accounts")).GetProperty("accounts").EnumerateArray().ToList();
        var receivables = tree.Single(static a => a.GetProperty("code").GetString() == "1210");
        receivables.GetProperty("path").GetString().ShouldBe("1000/1100/1200/1210");
        receivables.GetProperty("level").GetInt32().ShouldBe(3);
        receivables.GetProperty("isControl").GetBoolean().ShouldBeTrue();
        receivables.GetProperty("subledgerType").GetString().ShouldBe("AR");
        receivables.GetProperty("defaultRole").GetString().ShouldBe("AR");
        receivables.GetProperty("categoryCode").GetString().ShouldBe("current_assets");
        receivables.GetProperty("name").GetProperty("ar").GetString().ShouldBe("ذمم العملاء");
        tree.Count(static a => a.GetProperty("isHeader").GetBoolean()).ShouldBeGreaterThan(15);
        (await owner.GetOkAsync("/api/v1/accounting/account-categories")).GetArrayLength().ShouldBe(ChartTemplates.Categories.Count);

        // Export, then import into an empty chart: the same accounts come back, and a second import changes nothing.
        var csvResponse = await owner.GetAsync($"/api/v1/accounting/charts/{chartId}/export");
        csvResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        csvResponse.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var csv = await csvResponse.Content.ReadAsStringAsync();
        csv.Split('\n')[0].ShouldBe(string.Join(',', AccountCsv.Columns));
        csv.ShouldContain("1210,1200,Trade receivables,ذمم العملاء,asset,receivable,current_assets,false,true,AR,,false,true,operating,AR,true");

        var copy = await owner.PostAsync("/api/v1/accounting/charts", new { code = "COPY", name = new { en = "Copy", ar = "نسخة" }, accountCodeFormat = "####" });
        var copyId = copy.GetProperty("id").GetGuid();
        using var upload = new StringContent(csv, Encoding.UTF8, "text/csv");
        var imported = await owner.PostContentAsync($"/api/v1/accounting/charts/{copyId}/import", upload);
        imported.GetProperty("created").GetInt32().ShouldBe(tree.Count);
        using var uploadAgain = new StringContent(csv, Encoding.UTF8, "text/csv");
        var again = await owner.PostContentAsync($"/api/v1/accounting/charts/{copyId}/import", uploadAgain);
        again.GetProperty("unchanged").GetInt32().ShouldBe(tree.Count);
        var reexported = await owner.GetAsync($"/api/v1/accounting/charts/{copyId}/export");
        (await reexported.Content.ReadAsStringAsync()).ShouldBe(csv);

        // JSON import with parents listed after children still resolves; an unknown parent fails the whole file.
        var json = await owner.PostAsync($"/api/v1/accounting/charts/{copyId}/import", new
        {
            accounts = new object[]
            {
                new { code = "6191", parentCode = "6195", name = new { en = "Mobile", ar = "الهاتف النقال" }, type = "expense" },
                new { code = "6195", parentCode = "6000", name = new { en = "Telecom", ar = "الاتصالات" }, type = "expense", isHeader = true },
            },
        }, HttpStatusCode.OK);
        json.GetProperty("created").GetInt32().ShouldBe(2);
        var (failed, problem) = await owner.PostErrorAsync($"/api/v1/accounting/charts/{copyId}/import", new
        {
            accounts = new object[] { new { code = "6199", parentCode = "6999", name = new { en = "Orphan" }, type = "expense" } },
        }, HttpStatusCode.UnprocessableEntity);
        failed.ShouldBe("import.parent_unknown");
        problem.GetProperty("why").GetProperty("row").GetInt32().ShouldBe(1);
        (await owner.GetOkAsync($"/api/v1/accounting/charts/{copyId}/accounts")).EnumerateArray().Count(static a => a.GetProperty("code").GetString() == "6199").ShouldBe(0);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Accounts_follow_the_chart_rules_and_dimension_rules_decide_what_a_line_may_carry()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await owner.CompanyAsync("RUL");
        var otherCompanyId = await owner.CompanyAsync("OTH");
        var chartId = (await owner.ChartFromTemplateAsync("IFRS_SME", "MAIN", companyId)).GetProperty("id").GetGuid();
        var accounts = (await owner.GetOkAsync($"/api/v1/accounting/charts/{chartId}/accounts")).EnumerateArray().ToDictionary(static a => a.GetProperty("code").GetString()!, static a => a.GetProperty("id").GetGuid(), StringComparer.Ordinal);

        // Structural rules.
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "61A0", name = new { en = "Bad code" }, type = "expense", parentCode = "6000" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.code_format");
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "6101", name = new { en = "Wrong parent" }, type = "expense", parentCode = "6100" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.parent_not_header");
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "6101", name = new { en = "Wrong type" }, type = "asset", parentCode = "6000" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.type_mismatch");
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "6101", name = new { en = "Control without subledger" }, type = "expense", parentCode = "6000", isControl = true }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.subledger_required");
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "6101", name = new { en = "Header control" }, type = "expense", parentCode = "6000", isHeader = true, isControl = true, subledgerType = "AR" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.header_control");
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "6100", name = new { en = "Duplicate" }, type = "expense", parentCode = "6000" }, HttpStatusCode.Conflict)).Code.ShouldBe("account.code_taken");
        (await owner.PostErrorAsync($"/api/v1/accounting/charts/{chartId}/accounts", new { code = "6101", name = new { en = "Bad role" }, type = "expense", parentCode = "6000", defaultRole = "Nope" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.default_role.invalid");
        (await owner.PutErrorAsync($"/api/v1/accounting/accounts/{accounts["6000"]}", new { code = "6000", name = new { en = "Operating expenses" }, type = "expense", isHeader = false }, HttpStatusCode.Conflict)).Code.ShouldBe("account.has_children");
        (await owner.PutErrorAsync($"/api/v1/accounting/accounts/{accounts["6000"]}", new { code = "6000", name = new { en = "Operating expenses" }, type = "expense", isHeader = true, parentCode = "6500" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.parent_cycle");

        var marketing = await owner.PostAsync($"/api/v1/accounting/charts/{chartId}/accounts", new
        {
            code = "6131",
            name = new { en = "Digital marketing", ar = "التسويق الرقمي" },
            type = "expense",
            parentCode = "6000",
            subtype = "opex",
            categoryCode = "operating_expenses",
            currencyRestriction = "usd",
        });
        var marketingId = marketing.GetProperty("id").GetGuid();
        marketing.GetProperty("currencyRestriction").GetString().ShouldBe("USD");
        marketing.GetProperty("path").GetString().ShouldBe("6000/6131");

        // Dimension rules: a cost centre is required (a default fills it), projects are blocked.
        var (_, costCentre) = await owner.DimensionValueAsync("COST_CENTER", "CC-SALES");
        var (_, project) = await owner.DimensionValueAsync("PROJECT", "PRJ-1");
        var (_, otherCostCentre) = await owner.DimensionValueAsync("COST_CENTER", "CC-ADMIN");
        (await owner.PutErrorAsync($"/api/v1/accounting/accounts/{marketingId}/dimension-rules", new[] { new { dimensionCode = "COST_CENTER", rule = "sometimes" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dimension_rule.invalid");
        (await owner.PutErrorAsync($"/api/v1/accounting/accounts/{marketingId}/dimension-rules", new[] { new { dimensionCode = "PROJECT", rule = "blocked", defaultValueId = project } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dimension_rule.blocked_default");
        (await owner.PutErrorAsync($"/api/v1/accounting/accounts/{marketingId}/dimension-rules", new[] { new { dimensionCode = "COST_CENTER", rule = "required", defaultValueId = project } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("dimension_rule.default_mismatch");
        var rules = await owner.PutAsync($"/api/v1/accounting/accounts/{marketingId}/dimension-rules", new object[]
        {
            new { dimensionCode = "COST_CENTER", rule = "required" },
            new { dimensionCode = "PROJECT", rule = "blocked" },
        });
        rules.EnumerateArray().Select(static r => r.GetProperty("dimensionCode").GetString()).ShouldBe(["COST_CENTER", "PROJECT"]);

        var check = $"/api/v1/accounting/accounts/{marketingId}/check";
        var (missing, why) = await owner.PostErrorAsync(check, new { companyId }, HttpStatusCode.UnprocessableEntity);
        missing.ShouldBe("account.dimension_required");
        why.GetProperty("why").GetProperty("dimension").GetString().ShouldBe("COST_CENTER");
        (await owner.PostErrorAsync(check, new { companyId, dimensions = new { COST_CENTER = costCentre, PROJECT = project } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.dimension_blocked");
        (await owner.PostErrorAsync(check, new { companyId, dimensions = new { COST_CENTER = costCentre }, currency = "IQD" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.currency_restricted");
        var ok = await owner.PostAsync(check, new { companyId, dimensions = new { cost_center = costCentre }, currency = "USD", manual = true }, HttpStatusCode.OK);
        ok.GetProperty("dimensions").GetProperty("COST_CENTER").GetGuid().ShouldBe(costCentre);

        // A default value fills a missing dimension; the caller's value wins over the default.
        await owner.PutAsync($"/api/v1/accounting/accounts/{marketingId}/dimension-rules", new object[] { new { dimensionCode = "COST_CENTER", rule = "required", defaultValueId = otherCostCentre } });
        (await owner.PostAsync(check, new { companyId, currency = "USD" }, HttpStatusCode.OK)).GetProperty("dimensions").GetProperty("COST_CENTER").GetGuid().ShouldBe(otherCostCentre);
        (await owner.PostAsync(check, new { companyId, currency = "USD", dimensions = new { COST_CENTER = costCentre } }, HttpStatusCode.OK)).GetProperty("dimensions").GetProperty("COST_CENTER").GetGuid().ShouldBe(costCentre);

        // What the posting engine refuses: headers, control accounts on manual journals, inactive accounts, other companies' accounts and charts.
        (await owner.PostErrorAsync($"/api/v1/accounting/accounts/{accounts["6000"]}/check", new { companyId }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.header");
        (await owner.PostErrorAsync($"/api/v1/accounting/accounts/{accounts["1210"]}/check", new { companyId, manual = true }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.manual_posting_blocked");
        (await owner.PostAsync($"/api/v1/accounting/accounts/{accounts["1210"]}/check", new { companyId, manual = false }, HttpStatusCode.OK)).GetProperty("accountCode").GetString().ShouldBe("1210");
        (await owner.PostErrorAsync($"/api/v1/accounting/accounts/{accounts["1210"]}/check", new { companyId = otherCompanyId }, HttpStatusCode.Conflict)).Code.ShouldBe("company.chart_missing");
        await owner.PutAsync($"/api/v1/accounting/accounts/{accounts["6600"]}", new { code = "6600", name = new { en = "Other operating expenses", ar = "مصروفات تشغيلية أخرى" }, type = "expense", parentCode = "6000", isActive = false });
        (await owner.PostErrorAsync($"/api/v1/accounting/accounts/{accounts["6600"]}/check", new { companyId }, HttpStatusCode.Conflict)).Code.ShouldBe("account.inactive");
        await owner.PutAsync($"/api/v1/accounting/accounts/{accounts["6190"]}", new { code = "6190", name = new { en = "Communication", ar = "الاتصالات" }, type = "expense", parentCode = "6000", companyId = otherCompanyId });
        (await owner.PostErrorAsync($"/api/v1/accounting/accounts/{accounts["6190"]}/check", new { companyId }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.company_mismatch");

        // A dedicated chart cannot be assigned to another company; a shared one can be swapped in and detached.
        var dedicated = await owner.PostAsync("/api/v1/accounting/charts", new { code = "OTH", name = new { en = "Other's chart" }, companyId = otherCompanyId });
        (await owner.PutErrorAsync($"/api/v1/accounting/companies/{companyId}/chart", new { chartId = dedicated.GetProperty("id").GetGuid() }, HttpStatusCode.Conflict)).Code.ShouldBe("chart.other_company");
        (await owner.PutAsync($"/api/v1/accounting/companies/{otherCompanyId}/chart", new { chartId })).GetProperty("chartCode").GetString().ShouldBe("MAIN");
        (await owner.PostAsync($"/api/v1/accounting/accounts/{accounts["1210"]}/check", new { companyId = otherCompanyId }, HttpStatusCode.OK)).GetProperty("accountCode").GetString().ShouldBe("1210");
        (await owner.PutAsync($"/api/v1/accounting/companies/{otherCompanyId}/chart", new { chartId = (Guid?)null })).GetProperty("chartId").ValueKind.ShouldBe(JsonValueKind.Null);

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Statutory_mappings_come_with_the_iraq_template_and_can_be_edited_per_account()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var chartId = (await owner.ChartFromTemplateAsync("IRAQ_UAS", "IRQ")).GetProperty("id").GetGuid();

        var statutory = await owner.GetOkAsync("/api/v1/accounting/statutory-charts");
        statutory.EnumerateArray().Single().GetProperty("code").GetString().ShouldBe("IRAQ_UAS");
        statutory.EnumerateArray().Single().GetProperty("accounts").GetArrayLength().ShouldBe(9);

        var mappings = (await owner.GetOkAsync($"/api/v1/accounting/charts/{chartId}/mappings/IRAQ_UAS")).EnumerateArray().ToList();
        mappings.ShouldAllBe(static m => m.GetProperty("accountCode").GetString() != "6000", "headers are not mapped");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "1510").GetProperty("statutoryCode").GetString().ShouldBe("1");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "1310").GetProperty("statutoryCode").GetString().ShouldBe("2");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "1210").GetProperty("statutoryCode").GetString().ShouldBe("3");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "2110").GetProperty("statutoryCode").GetString().ShouldBe("5");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "4100").GetProperty("statutoryCode").GetString().ShouldBe("7");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "9100").GetProperty("statutoryCode").ValueKind.ShouldBe(JsonValueKind.Null, "suspense stays unmapped");
        mappings.Single(static m => m.GetProperty("accountCode").GetString() == "1210").GetProperty("statutoryName").GetProperty("ar").GetString().ShouldNotBeNullOrEmpty();

        (await owner.PutErrorAsync($"/api/v1/accounting/charts/{chartId}/mappings/IRAQ_UAS", new[] { new { accountCode = "9100", statutoryCode = "42" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("mapping.statutory_code_unknown");
        (await owner.PutErrorAsync($"/api/v1/accounting/charts/{chartId}/mappings/IRAQ_UAS", new[] { new { accountCode = "6000", statutoryCode = "6" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("mapping.header");
        var changed = await owner.PutAsync($"/api/v1/accounting/charts/{chartId}/mappings/IRAQ_UAS", new[] { new { accountCode = "9100", statutoryCode = "9" }, new { accountCode = "1210", statutoryCode = "" } });
        changed.EnumerateArray().Single(static m => m.GetProperty("accountCode").GetString() == "9100").GetProperty("statutoryCode").GetString().ShouldBe("9");
        changed.EnumerateArray().Single(static m => m.GetProperty("accountCode").GetString() == "1210").GetProperty("statutoryCode").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetAsync($"/api/v1/accounting/charts/{chartId}/mappings/NOPE")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        await owner.AssertInvariantsAsync();
    }
}
