using Dapper;
using Npgsql;
using Quicker.Identity.Security;
using Quicker.Migrator.Demo;

namespace Quicker.Demo.Tests;

/// <summary>
/// Roadmap 1.12: the seeder builds the demo tenant on a migrated database in under a minute, leaves it alone
/// on a plain run, and rebuilds it from scratch with the same identifiers when asked to reseed.
/// </summary>
public sealed class DemoSeederTests(DatabaseFixture fixture) : IClassFixture<DatabaseFixture>
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task Seeds_the_demo_tenant_then_keeps_it_then_rebuilds_it()
    {
        var first = await DemoSeeder.SeedAsync(fixture.Db.OwnerConnectionString, fixture.Db.AppConnectionString, reseed: false, cancellationToken: TestContext.Current.CancellationToken);
        first.Created.ShouldBeTrue();
        first.TenantId.ShouldBe(DemoData.TenantId);
        first.Elapsed.ShouldBeLessThan(Budget);
        (first.Companies, first.Branches, first.Users).ShouldBe((3, 5, 10));

        await using var db = new NpgsqlConnection(fixture.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await db.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = DemoData.TenantId.ToString() });

        // Tenant, people and roles.
        (await db.ExecuteScalarAsync<string>("SELECT status FROM control.tenants WHERE id = @t", new { t = DemoData.TenantId })).ShouldBe("active");
        var users = (await db.QueryAsync<(Guid Id, string Email, string PasswordHash, string Locale, string DigitStyle)>(
            "SELECT u.id, u.email, u.password_hash, u.locale, u.digit_style FROM control.users u JOIN control.tenant_memberships m ON m.user_id = u.id WHERE m.tenant_id = @t AND m.status = 'active'", new { t = DemoData.TenantId })).ToList();
        users.Count.ShouldBe(10);
        users.Select(static u => u.Email).ShouldBe(DemoData.Users.Select(static u => u.Email), ignoreOrder: true);
        users.Select(static u => u.Id).ShouldBe(DemoData.Users.Select(DemoData.UserId), ignoreOrder: true);
        new PasswordHasher().Verify(DemoData.Password, users.Single(static u => u.Email == DemoData.Owner.Email).PasswordHash).ShouldBeTrue();
        users.Single(static u => u.Email.StartsWith("accountant@", StringComparison.Ordinal)).ShouldSatisfyAllConditions(
            static u => u.Locale.ShouldBe("ar"),
            static u => u.DigitStyle.ShouldBe("eastern_arabic"));
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM control.tenant_memberships WHERE tenant_id = @t AND is_owner", new { t = DemoData.TenantId })).ShouldBe(1);
        var assignments = (await db.QueryAsync<(string Email, string Role, int Scopes)>("""
            SELECT u.email, r.code, (SELECT count(*) FROM app.idn_assignment_scopes s WHERE s.tenant_id = a.tenant_id AND s.assignment_id = a.id)
            FROM app.idn_role_assignments a
            JOIN app.idn_roles r ON r.tenant_id = a.tenant_id AND r.id = a.role_id
            JOIN control.tenant_memberships m ON m.id = a.membership_id
            JOIN control.users u ON u.id = m.user_id
            WHERE a.tenant_id = @t
            """, new { t = DemoData.TenantId })).ToList();
        assignments.Count.ShouldBe(10);
        assignments.Select(static a => a.Role).ShouldBe(DemoData.Users.Select(static u => u.Role), ignoreOrder: true);
        assignments.Single(static a => a.Role == "sales_rep").Scopes.ShouldBe(1, "the sales representative is scoped to the trading company");
        assignments.Single(static a => a.Role == "accountant").Scopes.ShouldBe(0);

        // Companies, branches and currencies (ASSUMPTIONS Q7: IQD, USD and AED functional).
        var companies = (await db.QueryAsync<(Guid Id, string Code, string Functional, string? Reporting, string Country)>(
            "SELECT id, code, functional_currency, reporting_currency, country FROM app.org_companies WHERE tenant_id = @t ORDER BY code", new { t = DemoData.TenantId })).ToList();
        companies.Select(static c => (c.Code, c.Functional, c.Reporting)).ShouldBe([("AEG", "AED", "USD"), ("IQT", "IQD", "USD"), ("USI", "USD", "IQD")]);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_branches WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBe(5);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_branches b JOIN app.org_companies c ON c.tenant_id = b.tenant_id AND c.id = b.company_id WHERE b.tenant_id = @t AND c.code = 'IQT'", new { t = DemoData.TenantId })).ShouldBe(3);
        (await db.ExecuteScalarAsync<decimal>("SELECT cash_rounding_increment FROM app.org_company_currencies cc JOIN app.org_companies c ON c.tenant_id = cc.tenant_id AND c.id = cc.company_id WHERE cc.tenant_id = @t AND c.code = 'IQT' AND cc.currency = 'IQD'", new { t = DemoData.TenantId })).ShouldBe(250m);

        // A year of rates: daily spot USD/IQD ending today (Baghdad), the official CBI rate, month-end closings.
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById(DemoData.BaghdadTimeZone)).DateTime);
        var spot = (await db.QueryAsync<(DateOnly ValidFrom, decimal Rate)>("""
            SELECT r.valid_from, r.rate FROM app.org_exchange_rates r JOIN app.org_exchange_rate_types t ON t.tenant_id = r.tenant_id AND t.id = r.rate_type_id
            WHERE r.tenant_id = @t AND t.code = 'spot' AND r.from_currency = 'USD' AND r.to_currency = 'IQD' ORDER BY r.valid_from
            """, new { t = DemoData.TenantId })).ToList();
        spot.Count.ShouldBe(DemoData.RateDays);
        spot[^1].ValidFrom.ShouldBe(today);
        spot[0].ValidFrom.ShouldBe(today.AddDays(-(DemoData.RateDays - 1)));
        spot.ShouldAllBe(static r => r.Rate >= 1450m && r.Rate <= 1560m);
        spot.Select(static r => r.Rate).Distinct().Count().ShouldBeGreaterThan(50, "the market walk moves");
        (await db.ExecuteScalarAsync<decimal>("""
            SELECT r.rate FROM app.org_exchange_rates r JOIN app.org_exchange_rate_types t ON t.tenant_id = r.tenant_id AND t.id = r.rate_type_id
            WHERE r.tenant_id = @t AND t.code = @type AND r.from_currency = 'USD' AND r.to_currency = 'IQD'
            """, new { t = DemoData.TenantId, type = DemoData.OfficialRateType })).ShouldBe(DemoRates.OfficialUsdIqd);
        (await db.ExecuteScalarAsync<int>("""
            SELECT count(*) FROM app.org_exchange_rates r JOIN app.org_exchange_rate_types t ON t.tenant_id = r.tenant_id AND t.id = r.rate_type_id
            WHERE r.tenant_id = @t AND t.code = 'closing'
            """, new { t = DemoData.TenantId })).ShouldBeGreaterThanOrEqualTo(33, "eleven or twelve month ends for three pairs");
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_exchange_rates WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBe(first.Rates);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.aud_events WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBeGreaterThan(20, "creations went through the services and were audited");

        // The books (roadmap 2.7): a chart and a profile per company, dimension values, a year of journals, routines, period control, and the harness green.
        first.Journals.ShouldBeGreaterThan(250, "twelve months of journals for three companies");
        first.Entries.ShouldBeGreaterThan(first.Journals, "reversals, recurring journals and deferral lines post entries of their own");
        first.PeriodsClosed.ShouldBeGreaterThanOrEqualTo(30, "every month before the current one is closed for each company");
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_companies WHERE tenant_id = @t AND chart_id IS NOT NULL AND posting_profile_id IS NOT NULL", new { t = DemoData.TenantId })).ShouldBe(3);
        (await db.QueryAsync<string>("SELECT c.template_code FROM app.gl_charts c WHERE c.tenant_id = @t ORDER BY c.code", new { t = DemoData.TenantId })).ShouldBe(["GCC", "IRAQ_UAS", "IFRS_SME"]);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_dimension_values WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBeGreaterThanOrEqualTo(9 + 5, "cost centres, departments and projects next to the branch values");
        var offBalance = (await db.QueryAsync<(Guid CompanyId, decimal Off)>("SELECT company_id, sum(debit_fc - credit_fc) FROM app.gl_journal_lines WHERE tenant_id = @t GROUP BY company_id", new { t = DemoData.TenantId })).ToList();
        offBalance.Count.ShouldBe(3);
        offBalance.ShouldAllBe(static c => c.Off == 0m);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.gl_recurring_templates WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBe(3);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.gl_deferral_schedules WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBe(4);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.gl_journal_entries WHERE tenant_id = @t AND is_auto_reversal", new { t = DemoData.TenantId })).ShouldBeGreaterThanOrEqualTo(30, "the utility accruals of the closed months reversed on their date");
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_period_module_states WHERE tenant_id = @t AND state = 'hard_closed'", new { t = DemoData.TenantId })).ShouldBeGreaterThanOrEqualTo(27);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.gl_manual_journals WHERE tenant_id = @t AND corrects_journal_id IS NOT NULL AND status = 'posted'", new { t = DemoData.TenantId })).ShouldBe(1, "scenario 7 lives in the demo");
        var verified = await DemoSeeder.VerifyAsync(fixture.Db.OwnerConnectionString, fixture.Db.AppConnectionString, cancellationToken: TestContext.Current.CancellationToken);
        verified.Passed.ShouldBeTrue(string.Join(" | ", verified.Checks.Where(static c => !c.Passed).Select(static c => c.Code + ": " + string.Join("; ", c.Problems))));
        verified.Checks.Count.ShouldBe(8);

        // A plain run (make up) leaves the tenant as it is: same company ids, nothing added.
        var companyIds = companies.Select(static c => c.Id).ToList();
        var second = await DemoSeeder.SeedAsync(fixture.Db.OwnerConnectionString, fixture.Db.AppConnectionString, reseed: false, cancellationToken: TestContext.Current.CancellationToken);
        second.Created.ShouldBeFalse();
        second.TenantId.ShouldBe(DemoData.TenantId);
        (await db.QueryAsync<Guid>("SELECT id FROM app.org_companies WHERE tenant_id = @t ORDER BY code", new { t = DemoData.TenantId })).ShouldBe(companyIds);

        // A reseed (make demo) purges everything, audit chain and a queued job included, and rebuilds with the same ids.
        await db.ExecuteAsync("INSERT INTO ops.jobs (id, tenant_id, type, payload, state, run_after) VALUES (@id, @t, 'demo.probe', '{}', 'queued', now())", new { id = Guid.CreateVersion7(), t = DemoData.TenantId });
        var auditBefore = await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.aud_events WHERE tenant_id = @t", new { t = DemoData.TenantId });
        var third = await DemoSeeder.SeedAsync(fixture.Db.OwnerConnectionString, fixture.Db.AppConnectionString, reseed: true, cancellationToken: TestContext.Current.CancellationToken);
        third.Created.ShouldBeTrue();
        third.TenantId.ShouldBe(DemoData.TenantId);
        third.Elapsed.ShouldBeLessThan(Budget);
        third.Rates.ShouldBe(first.Rates);
        third.Journals.ShouldBe(first.Journals, "the books are deterministic for the same day");
        third.Entries.ShouldBe(first.Entries);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM control.tenants WHERE slug = @slug", new { slug = DemoData.Slug })).ShouldBe(1);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM control.users WHERE email LIKE @p", new { p = "%@" + DemoData.EmailDomain })).ShouldBe(10);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM ops.jobs WHERE tenant_id = @t AND type = 'demo.probe'", new { t = DemoData.TenantId })).ShouldBe(0);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.org_companies WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBe(3);
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.aud_events WHERE tenant_id = @t", new { t = DemoData.TenantId })).ShouldBe(auditBefore, "a rebuilt tenant starts a fresh audit chain of the same length");
        (await db.QueryAsync<Guid>("SELECT m.user_id FROM control.tenant_memberships m WHERE m.tenant_id = @t ORDER BY m.user_id", new { t = DemoData.TenantId }))
            .ShouldBe(DemoData.Users.Select(DemoData.UserId).Order());
    }

    [Fact]
    public void Identifiers_and_rate_series_are_the_same_on_every_run()
    {
        DemoIds.For("tenant").ShouldBe(DemoData.TenantId);
        DemoIds.For("user:owner", 0).ShouldNotBe(DemoIds.For("user:admin", 1));
        DemoIds.For("tenant").ToString()[14].ShouldBe('7', "ids keep the version-7 layout the platform pages by");

        var day = new DateOnly(2026, 9, 22);
        var a = DemoRates.Build(day);
        var b = DemoRates.Build(day);
        a.ShouldBe(b);
        a.Count(static r => r.RateType == "spot" && r.From == "USD" && r.To == "IQD").ShouldBe(DemoData.RateDays);
        a.Count(static r => r.RateType == "average").ShouldBe(26, "thirteen calendar months touch a 365-day window, two pairs each");
        a.Where(static r => r.RateType == "closing").Select(static r => r.ValidFrom).ShouldAllBe(static d => d.AddDays(1).Month != d.Month);
        a.Single(static r => r.RateType == "spot" && r.From == "USD" && r.To == "AED").Rate.ShouldBe(DemoRates.UsdAedPeg);
        a.ShouldAllBe(static r => r.Rate > 0m);
    }
}
