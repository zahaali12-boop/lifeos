using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Organization.Tests;

/// <summary>Roadmap 1.6: a company with a July fiscal year, period states per module (ADR-0026), tenant defaults, branches as dimension values.</summary>
[Collection(ApiCollection.Name)]
public sealed class CompanyAndCalendarTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Sign_up_seeds_the_organization_defaults()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        var dimensions = (await owner.GetOkAsync("/api/v1/organization/dimensions")).EnumerateArray().ToList();
        dimensions.Select(static d => d.GetProperty("code").GetString()).ShouldBe(["BRANCH", "COST_CENTER", "DEPARTMENT", "PROJECT"]);
        dimensions.ShouldAllBe(static d => d.GetProperty("isSystem").GetBoolean());
        dimensions.Single(static d => d.GetProperty("code").GetString() == "COST_CENTER").GetProperty("isHierarchical").GetBoolean().ShouldBeTrue();

        var rateTypes = (await owner.GetOkAsync("/api/v1/organization/rate-types")).EnumerateArray().Select(static t => (t.GetProperty("code").GetString(), t.GetProperty("isSystem").GetBoolean())).ToList();
        rateTypes.ShouldContain(("spot", true));
        rateTypes.ShouldContain(("closing", true));
        rateTypes.ShouldContain(("official", false));
        rateTypes.ShouldContain(("market", false));

        var calendars = (await owner.GetOkAsync("/api/v1/organization/business-calendars")).EnumerateArray().ToList();
        calendars.Single(static c => c.GetProperty("code").GetString() == "sun_thu").GetProperty("workingDays").EnumerateArray().Select(static d => d.GetInt32()).ShouldBe([0, 1, 2, 3, 4]);
        calendars.Single(static c => c.GetProperty("code").GetString() == "mon_fri").GetProperty("name").GetProperty("ar").GetString().ShouldNotBeNullOrWhiteSpace();

        var fiscal = (await owner.GetOkAsync("/api/v1/organization/fiscal-calendars")).EnumerateArray().Single();
        fiscal.GetProperty("code").GetString().ShouldBe("standard");
        fiscal.GetProperty("years").GetArrayLength().ShouldBe(0); // years open with the first company

        var uoms = (await owner.GetOkAsync("/api/v1/organization/uoms")).EnumerateArray().Select(static u => u.GetProperty("code").GetString()).ToList();
        uoms.ShouldContain("PCS");
        uoms.ShouldContain("KG");
        (await owner.GetOkAsync("/api/v1/organization/currencies")).GetArrayLength().ShouldBeGreaterThan(150);
    }

    [Fact]
    public async Task A_company_with_a_July_fiscal_year_gets_its_year_and_periods_and_resolves_posting_dates()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        var calendar = await owner.PostAsync("/api/v1/organization/fiscal-calendars", new { code = "july", name = new { en = "July to June", ar = "تموز إلى حزيران" }, startMonth = 7, periodsPerYear = 13 });
        var calendarId = calendar.GetProperty("id").GetGuid();

        var company = await owner.CreateCompanyAsync("IQCO", fiscalCalendarId: calendarId);
        var companyId = company.GetProperty("id").GetGuid();
        company.GetProperty("functionalCurrency").GetString().ShouldBe("IQD");
        company.GetProperty("fiscalCalendarId").GetGuid().ShouldBe(calendarId);

        // Today for the fixture is 2026-09-22 in Asia/Baghdad, so FY2026/27 (2026-07-01..2027-06-30) is opened automatically.
        var years = (await owner.GetOkAsync($"/api/v1/organization/fiscal-calendars/{calendarId}")).GetProperty("years").EnumerateArray().ToList();
        var year = years.ShouldHaveSingleItem();
        year.GetProperty("code").GetString().ShouldBe("FY2026/27");
        year.GetProperty("startsOn").GetString().ShouldBe("2026-07-01");
        year.GetProperty("endsOn").GetString().ShouldBe("2027-06-30");
        year.GetProperty("status").GetString().ShouldBe("open");
        var periods = year.GetProperty("periods").EnumerateArray().ToList();
        periods.Count.ShouldBe(13);
        periods[0].GetProperty("startsOn").GetString().ShouldBe("2026-07-01");
        periods[0].GetProperty("endsOn").GetString().ShouldBe("2026-07-31");
        periods[11].GetProperty("startsOn").GetString().ShouldBe("2027-06-01");
        periods[11].GetProperty("endsOn").GetString().ShouldBe("2027-06-30");
        periods[12].GetProperty("isAdjustment").GetBoolean().ShouldBeTrue();
        periods[12].GetProperty("startsOn").GetString().ShouldBe("2027-06-30");

        var resolved = await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2026-09-22&module=GL");
        resolved.GetProperty("number").GetInt32().ShouldBe(3);
        resolved.GetProperty("fiscalYearCode").GetString().ShouldBe("FY2026/27");
        resolved.GetProperty("state").GetString().ShouldBe("open");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2027-06-30&module=INV")).GetProperty("number").GetInt32().ShouldBe(12); // never the adjustment period by date

        (await owner.GetErrorAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2026-05-01&module=GL", HttpStatusCode.Conflict)).ShouldBe("period.no_fiscal_year");

        // Opening the next year explicitly; overlapping years are refused.
        var next = await owner.PostAsync($"/api/v1/organization/fiscal-calendars/{calendarId}/years", new { startYear = 2027, status = "future" });
        next.GetProperty("code").GetString().ShouldBe("FY2027/28");
        (await (await owner.PostAsJsonAsync($"/api/v1/organization/fiscal-calendars/{calendarId}/years", new { startYear = 2027 }, Json)).ErrorCodeAsync()).ShouldBe("fiscal_year.overlaps");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2027-08-15&module=GL")).GetProperty("state").GetString().ShouldBe("never_opened");

        // Calendar shape is frozen once it has years.
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/fiscal-calendars/{calendarId}", new { code = "july", name = new { en = "July" }, startMonth = 1, periodsPerYear = 12 }, Json)).ErrorCodeAsync()).ShouldBe("fiscal_calendar.in_use");
    }

    [Fact]
    public async Task Period_states_are_per_company_and_module_and_reopening_needs_a_reason_and_is_audited()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.CreateCompanyAsync("STATECO")).GetProperty("id").GetGuid();
        var otherCompanyId = (await owner.CreateCompanyAsync("OTHERCO")).GetProperty("id").GetGuid();
        var period = await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2026-09-22&module=GL");
        var periodId = period.GetProperty("periodId").GetGuid();

        var states = await owner.PutAsync($"/api/v1/organization/periods/{periodId}/states", new { companyId, modules = new[] { "GL", "AR" }, state = "hard_closed", reason = "September closed" });
        states.EnumerateArray().Single(static s => s.Str("module") == "GL").GetProperty("state").GetString().ShouldBe("hard_closed");
        states.EnumerateArray().Single(static s => s.Str("module") == "AR").GetProperty("isExplicit").GetBoolean().ShouldBeTrue();
        states.EnumerateArray().Single(static s => s.Str("module") == "AP").GetProperty("state").GetString().ShouldBe("open");
        states.EnumerateArray().Single(static s => s.Str("module") == "AP").GetProperty("isExplicit").GetBoolean().ShouldBeFalse();

        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2026-09-05&module=GL")).GetProperty("state").GetString().ShouldBe("hard_closed");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2026-09-05&module=INV")).GetProperty("state").GetString().ShouldBe("open");
        (await owner.GetOkAsync($"/api/v1/organization/companies/{otherCompanyId}/periods/resolve?date=2026-09-05&module=GL")).GetProperty("state").GetString().ShouldBe("open"); // other company untouched

        // Hard-closed cannot be changed through the ordinary state change; reopening requires a reason.
        var blocked = await owner.PutAsJsonAsync($"/api/v1/organization/periods/{periodId}/states", new { companyId, modules = new[] { "GL" }, state = "open" }, Json);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await blocked.ReadJsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("period.hard_closed");
        problem.GetProperty("why").GetProperty("requiredPermission").GetString().ShouldBe("accounting.period.reopen");

        var noReason = await owner.PostAsJsonAsync($"/api/v1/organization/periods/{periodId}/reopen", new { companyId, modules = new[] { "GL" }, reason = " " }, Json);
        noReason.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await noReason.ErrorCodeAsync()).ShouldBe("period.reason_required");

        var reopened = await owner.PostAsync($"/api/v1/organization/periods/{periodId}/reopen", new { companyId, modules = new[] { "GL" }, reason = "Late supplier invoice", state = "soft_closed" }, HttpStatusCode.OK);
        reopened.EnumerateArray().Single(static s => s.Str("module") == "GL").GetProperty("state").GetString().ShouldBe("soft_closed");
        reopened.EnumerateArray().Single(static s => s.Str("module") == "GL").GetProperty("reason").GetString().ShouldBe("Late supplier invoice");
        reopened.EnumerateArray().Single(static s => s.Str("module") == "AR").GetProperty("state").GetString().ShouldBe("hard_closed");

        var timeline = (await owner.GetOkAsync($"/api/v1/audit/records/fiscal_period/{periodId}")).EnumerateArray().ToList();
        timeline.Select(static e => e.Str("action")).ShouldBe(["state_changed", "state_changed", "override"]);
        timeline[2].GetProperty("reason").GetString().ShouldBe("Late supplier invoice");
        timeline[2].GetProperty("before").GetProperty("state").GetString().ShouldBe("hard_closed");
        timeline[2].GetProperty("after").GetProperty("state").GetString().ShouldBe("soft_closed");
        timeline[2].GetProperty("companyId").GetGuid().ShouldBe(companyId);

        // A member who can read the organization but holds no period permission is refused.
        var clerk = await InviteWithGrantsAsync(owner, ws, "organization.company.read");
        using var clerkClient = Api.ClientFor(clerk);
        (await clerkClient.GetOkAsync($"/api/v1/organization/periods/{periodId}/states?companyId={companyId}")).GetArrayLength().ShouldBe(7);
        (await clerkClient.PutAsJsonAsync($"/api/v1/organization/periods/{periodId}/states", new { companyId, modules = new[] { "AP" }, state = "soft_closed" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await clerkClient.PostAsJsonAsync($"/api/v1/organization/periods/{periodId}/reopen", new { companyId, modules = new[] { "AR" }, reason = "x" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Branches_create_their_BRANCH_dimension_value_and_company_currencies_keep_the_functional_one()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = (await owner.CreateCompanyAsync("BRCO", reportingCurrency: "USD")).GetProperty("id").GetGuid();

        var branch = await owner.PostAsync($"/api/v1/organization/companies/{companyId}/branches", new { code = "bgw", name = new { en = "Baghdad", ar = "بغداد" }, address = new { city = "Baghdad" } });
        branch.GetProperty("code").GetString().ShouldBe("BGW");
        var valueId = branch.GetProperty("dimensionValueId").GetGuid();
        var branchDimension = (await owner.DimensionAsync("BRANCH")).GetProperty("id").GetGuid();
        var values = (await owner.GetOkAsync($"/api/v1/organization/dimensions/{branchDimension}/values")).EnumerateArray().ToList();
        var value = values.ShouldHaveSingleItem();
        value.GetProperty("id").GetGuid().ShouldBe(valueId);
        value.GetProperty("code").GetString().ShouldBe("BRCO-BGW");
        value.GetProperty("companyId").GetGuid().ShouldBe(companyId);
        value.GetProperty("name").GetProperty("ar").GetString().ShouldBe("بغداد");

        (await (await owner.PostAsJsonAsync($"/api/v1/organization/dimensions/{branchDimension}/values", new { code = "X", name = new { en = "X" } }, Json)).ErrorCodeAsync()).ShouldBe("dimension.branch_values_are_branches");
        (await (await owner.PostAsJsonAsync($"/api/v1/organization/companies/{companyId}/branches", new { code = "BGW", name = new { en = "Dup" } }, Json)).ErrorCodeAsync()).ShouldBe("branch.code_taken");

        var renamed = await owner.PutAsync($"/api/v1/organization/companies/{companyId}/branches/{branch.GetProperty("id").GetGuid()}", new { code = "BGD", name = new { en = "Baghdad HQ" }, isActive = false });
        renamed.GetProperty("code").GetString().ShouldBe("BGD");
        var updatedValue = (await owner.GetOkAsync($"/api/v1/organization/dimensions/{branchDimension}/values")).EnumerateArray().Single();
        updatedValue.GetProperty("code").GetString().ShouldBe("BRCO-BGD");
        updatedValue.GetProperty("isActive").GetBoolean().ShouldBeFalse();

        var currencies = (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/currencies")).EnumerateArray().ToList();
        currencies.Select(static c => c.Str("currency")).ShouldBe(["IQD", "USD"]);
        currencies[0].GetProperty("isFunctional").GetBoolean().ShouldBeTrue();
        currencies[0].GetProperty("minorUnits").GetInt32().ShouldBe(3);

        var iqd = await owner.PutAsync($"/api/v1/organization/companies/{companyId}/currencies", new { currency = "IQD", displayDecimals = 0, cashRoundingIncrement = 250 });
        iqd.GetProperty("displayDecimals").GetInt32().ShouldBe(0);
        iqd.GetProperty("cashRoundingIncrement").GetDecimal().ShouldBe(250m);
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/companies/{companyId}/currencies", new { currency = "IQD", isEnabled = false }, Json)).ErrorCodeAsync()).ShouldBe("company_currency.functional_required");
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/companies/{companyId}/currencies", new { currency = "ZZZ" }, Json)).ErrorCodeAsync()).ShouldBe("company.currency_unknown");

        // The functional currency is immutable; other attributes change.
        var update = new { code = "BRCO", legalName = new { en = "BRCO Trading LLC" }, country = "IQ", functionalCurrency = "USD", timeZone = "Asia/Baghdad" };
        (await (await owner.PutAsJsonAsync($"/api/v1/organization/companies/{companyId}", update, Json)).ErrorCodeAsync()).ShouldBe("company.functional_currency_locked");
        var changed = await owner.PutAsync($"/api/v1/organization/companies/{companyId}", update with { functionalCurrency = "IQD" });
        changed.GetProperty("reportingCurrency").ValueKind.ShouldBe(JsonValueKind.Null);
        (await (await owner.PostAsJsonAsync("/api/v1/organization/companies", new { code = "TZ", legalName = new { en = "x" }, country = "IQ", functionalCurrency = "IQD", timeZone = "Mars/Olympus" }, Json)).ErrorCodeAsync()).ShouldBe("company.time_zone_invalid");

        // Settings: typed values at tenant and company level.
        var setting = await owner.PutAsync($"/api/v1/organization/companies/{companyId}/settings/inventory.negative_stock_grace_days", new { value = 3, valueType = "number" });
        setting.GetProperty("value").GetInt32().ShouldBe(3);
        (await (await owner.PutAsJsonAsync("/api/v1/organization/settings/ui.theme", new { value = 1, valueType = "string" }, Json)).ErrorCodeAsync()).ShouldBe("setting.value_type_mismatch");
        (await owner.PutAsync("/api/v1/organization/settings/ui.theme", new { value = "dark", valueType = "string" })).GetProperty("companyId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetOkAsync("/api/v1/organization/settings")).GetArrayLength().ShouldBe(1);
        (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/settings")).GetArrayLength().ShouldBe(1);

        // Another tenant sees none of it.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.GetAsync($"/api/v1/organization/companies/{companyId}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.GetOkAsync("/api/v1/organization/companies")).GetArrayLength().ShouldBe(0);
        (await outsider.GetOkAsync($"/api/v1/organization/companies/{companyId}/branches")).GetArrayLength().ShouldBe(0);
    }

    private async Task<string> InviteWithGrantsAsync(HttpClient owner, Workspace ws, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = "reader", name = new { en = "Reader" }, description = "", grants });
        var email = $"reader-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Reader", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "reader-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return accepted.GetProperty("accessToken").GetString()!;
    }
}
