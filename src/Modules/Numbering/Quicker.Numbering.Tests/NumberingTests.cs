using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Numbering.Contracts;
using Quicker.Persistence;

namespace Quicker.Numbering.Tests;

/// <summary>ADR-0016 through the API: selection, templates, concurrency without gaps or duplicates, rollback, resets and the gapless audit.</summary>
[Collection(ApiCollection.Name)]
public sealed class NumberingTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    [Fact]
    public async Task The_most_specific_series_wins_and_templates_render_company_branch_year_and_padded_sequence()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (companyId, branchId) = await owner.CompanyWithBranchAsync("IQCO");
        var yearId = (await owner.GetOkAsync("/api/v1/organization/fiscal-calendars")).EnumerateArray().Single().GetProperty("years").EnumerateArray().Single().GetProperty("id").GetGuid();

        var general = await owner.CreateSeriesAsync("inv", "sales_invoice", companyId, "INV-{company}-{yy}-{seq:6}", isDefault: true);
        general.GetProperty("code").GetString().ShouldBe("INV");
        general.GetProperty("documentType").GetString().ShouldBe("sales_invoice");
        var branchSeries = await owner.CreateSeriesAsync("INV-BGW", "sales_invoice", companyId, "INV-{branch}-{yy}-{seq:5}", branchId: branchId);
        var yearSeries = await owner.CreateSeriesAsync("INV-FY", "sales_invoice", companyId, "INV-{fy}-{seq}", fiscalYearId: yearId);
        var next = await owner.CreateSeriesAsync("INV-NEXT", "sales_invoice", companyId, "NEW-{seq}", validFrom: "2027-01-01");

        // No branch: the year-bound series (score 1) beats the general default (score 0).
        var preview = await owner.PostAsync("/api/v1/numbering/preview", new { documentType = "sales_invoice", companyId, date = "2026-09-22", documentId = Guid.NewGuid() }, HttpStatusCode.OK);
        preview.GetProperty("seriesCode").GetString().ShouldBe("INV-FY");
        preview.GetProperty("text").GetString().ShouldBe("INV-2026-1");
        preview.GetProperty("nextNumber").GetInt64().ShouldBe(1);

        // With the branch: the branch series (score 2) wins; {yy} and zero padding come from the date and template.
        var allocated = await owner.AllocateAsync("sales_invoice", companyId, "2026-09-22", branchId);
        allocated.GetProperty("seriesCode").GetString().ShouldBe("INV-BGW");
        allocated.GetProperty("text").GetString().ShouldBe("INV-BGW-26-00001");
        allocated.GetProperty("number").GetInt64().ShouldBe(1);
        allocated.GetProperty("gapless").GetBoolean().ShouldBeTrue();
        (await owner.AllocateAsync("sales_invoice", companyId, "2026-09-22", branchId)).GetProperty("text").GetString().ShouldBe("INV-BGW-26-00002");

        // An explicit override to any applicable series; not to one outside its validity, branch or type.
        var overridden = await owner.AllocateAsync("sales_invoice", companyId, "2026-09-22", branchId, seriesId: general.GetProperty("id").GetGuid());
        overridden.GetProperty("text").GetString().ShouldBe("INV-IQCO-26-000001");
        (await owner.PostErrorAsync("/api/v1/numbering/allocate", new { documentType = "sales_invoice", companyId, date = "2026-09-22", documentId = Guid.NewGuid(), seriesId = next.GetProperty("id").GetGuid() }, HttpStatusCode.Conflict)).ShouldBe("series.not_applicable");
        (await owner.PostErrorAsync("/api/v1/numbering/allocate", new { documentType = "sales_invoice", companyId, date = "2026-09-22", documentId = Guid.NewGuid(), seriesId = branchSeries.GetProperty("id").GetGuid() }, HttpStatusCode.Conflict)).ShouldBe("series.not_applicable");
        (await owner.PostErrorAsync("/api/v1/numbering/allocate", new { documentType = "credit_note", companyId, date = "2026-09-22", documentId = Guid.NewGuid() }, HttpStatusCode.Conflict)).ShouldBe("series.none_applicable");

        // In 2027 the year-bound series no longer applies (no fiscal year covers the date yet); INV and INV-NEXT tie at score 0 and the default wins.
        var future = await owner.PostAsync("/api/v1/numbering/preview", new { documentType = "sales_invoice", companyId, date = "2027-03-01", documentId = Guid.NewGuid() }, HttpStatusCode.OK);
        future.GetProperty("seriesCode").GetString().ShouldBe("INV");
        future.GetProperty("text").GetString().ShouldBe("INV-IQCO-27-000002");

        // Validation of definitions.
        (await owner.PostErrorAsync("/api/v1/numbering/series", new { code = "X1", documentType = "sales_invoice", companyId, template = "INV-{seq}-{seq}" }, HttpStatusCode.UnprocessableEntity)).ShouldBe("series.template_sequence_required");
        (await owner.PostErrorAsync("/api/v1/numbering/series", new { code = "X2", documentType = "sales_invoice", companyId, template = "INV-{warehouse}-{seq}" }, HttpStatusCode.UnprocessableEntity)).ShouldBe("series.template_token_unknown");
        (await owner.PostErrorAsync("/api/v1/numbering/series", new { code = "X3", documentType = "sales_invoice", companyId, template = "INV-{branch}-{seq}" }, HttpStatusCode.UnprocessableEntity)).ShouldBe("series.branch_required");
        (await owner.PostErrorAsync("/api/v1/numbering/series", new { code = "INV", documentType = "sales_invoice", companyId, template = "X-{seq}" }, HttpStatusCode.Conflict)).ShouldBe("series.code_taken");
        (await owner.PostErrorAsync("/api/v1/numbering/series", new { code = "X4", documentType = "sales_invoice", companyId, template = "X-{seq}", resetPolicy = "weekly" }, HttpStatusCode.UnprocessableEntity)).ShouldBe("series.reset_policy_invalid");
        (await owner.PostErrorAsync("/api/v1/numbering/series", new { code = "X5", documentType = "sales_invoice", companyId = Guid.NewGuid(), template = "X-{seq}" }, HttpStatusCode.NotFound)).ShouldBe("company.not_found");
        (await owner.PutErrorAsync($"/api/v1/numbering/series/{branchSeries.GetProperty("id").GetGuid()}", new { code = "INV-BGW", documentType = "sales_invoice", companyId, branchId, template = "INV-{branch}-{yy}-{seq:5}", gapless = false }, HttpStatusCode.Conflict)).ShouldBe("series.in_use");

        // Series changes are audited (captured), and the draft identifier is stable.
        var events = (await owner.GetOkAsync($"/api/v1/audit/records/numbering_series/{general.GetProperty("id").GetGuid()}")).EnumerateArray().ToList();
        events[0].GetProperty("action").GetString().ShouldBe("created");
        events[0].GetProperty("after").GetProperty("template").GetString().ShouldBe("INV-{company}-{yy}-{seq:6}");
        var documentId = Guid.Parse("0199a5d2-1234-7abc-8def-0123456789ab");
        (await owner.GetOkAsync($"/api/v1/numbering/drafts/{documentId}")).GetProperty("identifier").GetString().ShouldBe("DRAFT-0199A5D2");
        DraftIdentifiers.IsDraft("DRAFT-0199A5D2").ShouldBeTrue();
        DraftIdentifiers.IsDraft("INV-BGW-26-00001").ShouldBeFalse();

        // Another tenant cannot see or use the series.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.GetAsync($"/api/v1/numbering/series/{general.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.GetOkAsync("/api/v1/numbering/series")).GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Fifty_concurrent_allocations_are_consecutive_without_gaps_or_duplicates_and_a_rollback_leaves_no_gap()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (companyId, _) = await owner.CompanyWithBranchAsync("PARCO");
        var series = await owner.CreateSeriesAsync("JV", "journal", companyId, "JV-{yyyy}-{seq:4}", startNumber: 100);
        var seriesId = series.GetProperty("id").GetGuid();

        var requests = Enumerable.Range(0, 50).Select(async _ =>
        {
            using var client = Api.ClientFor(ws.AccessToken);
            var allocated = await client.AllocateAsync("journal", companyId, "2026-09-22");
            return allocated.GetProperty("number").GetInt64();
        });
        var numbers = (await Task.WhenAll(requests)).OrderBy(static n => n).ToList();
        numbers.ShouldBe(Enumerable.Range(100, 50).Select(static n => (long)n).ToList());

        // A number taken inside a unit of work that rolls back is issued again to the next caller.
        long rolledBack;
        await using (var scope = Api.Services.CreateAsyncScope())
        {
            var factory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
            await using var uow = await factory.BeginAsync(TenantContext.System(new TenantId(ws.TenantId), "rollback-test"));
            scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
            var allocator = scope.ServiceProvider.GetRequiredService<INumberAllocator>();
            var inside = await allocator.AllocateAsync(new NumberRequest("journal", new CompanyId(companyId), null, new DateOnly(2026, 9, 22), Guid.NewGuid()));
            inside.IsSuccess.ShouldBeTrue(inside.Error?.Code);
            rolledBack = inside.Value.Number;
            rolledBack.ShouldBe(150);
            await uow.RollbackAsync();
        }

        var after = await owner.AllocateAsync("journal", companyId, "2026-09-22");
        after.GetProperty("number").GetInt64().ShouldBe(rolledBack);
        after.GetProperty("text").GetString().ShouldBe("JV-2026-0150");

        // Every issued number is on record, the counter is one past the last, and the gapless audit finds nothing missing.
        var summary = await owner.GetOkAsync($"/api/v1/numbering/series/{seriesId}");
        var counter = summary.GetProperty("counters").EnumerateArray().Single();
        counter.GetProperty("periodKey").GetString().ShouldBe("");
        counter.GetProperty("nextNumber").GetInt64().ShouldBe(151);
        counter.GetProperty("allocated").GetInt64().ShouldBe(51);
        var gaps = await owner.GetOkAsync($"/api/v1/numbering/series/{seriesId}/gaps");
        var period = gaps.GetProperty("periods").EnumerateArray().Single();
        period.GetProperty("first").GetInt64().ShouldBe(100);
        period.GetProperty("last").GetInt64().ShouldBe(150);
        period.GetProperty("missing").GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync($"/api/v1/numbering/series/{seriesId}/allocations?limit=5")).GetArrayLength().ShouldBe(5);

        // The same document never gets two numbers from one series, and issued numbers cannot be altered or removed by the application role.
        var documentId = Guid.NewGuid();
        await owner.AllocateAsync("journal", companyId, "2026-09-22", documentId: documentId);
        (await owner.PostErrorAsync("/api/v1/numbering/allocate", new { documentType = "journal", companyId, date = "2026-09-22", documentId }, HttpStatusCode.Conflict)).ShouldBe("numbering.already_allocated");
        await using var app = new NpgsqlConnection(Api.Db.AppConnectionString);
        await app.OpenAsync();
        await app.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = ws.TenantId.ToString() });
        var forbidden = await Should.ThrowAsync<PostgresException>(() => app.ExecuteAsync("DELETE FROM app.num_allocations WHERE series_id = @s", new { s = seriesId }));
        forbidden.SqlState.ShouldBeOneOf(PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.RaiseException); // privilege revoked and append-only trigger
        var altered = await Should.ThrowAsync<PostgresException>(() => app.ExecuteAsync("UPDATE app.num_allocations SET number = 9999 WHERE series_id = @s AND number = 100", new { s = seriesId }));
        altered.SqlState.ShouldBeOneOf(PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.RaiseException);
        (await app.ExecuteScalarAsync<long>("SELECT count(*) FROM app.num_allocations WHERE series_id = @s", new { s = seriesId })).ShouldBe(52);
    }

    [Fact]
    public async Task Counters_reset_by_fiscal_year_or_month_and_moving_backwards_needs_the_reset_permission_and_a_reason()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var july = await owner.PostAsync("/api/v1/organization/fiscal-calendars", new { code = "july", name = new { en = "July" }, startMonth = 7, periodsPerYear = 12 });
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "FYCO", legalName = new { en = "FY Co" }, country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", fiscalCalendarId = july.GetProperty("id").GetGuid() });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync($"/api/v1/organization/fiscal-calendars/{july.GetProperty("id").GetGuid()}/years", new { startYear = 2027, status = "future" });

        var yearly = await owner.CreateSeriesAsync("PO", "purchase_order", companyId, "PO-{fy}-{seq:4}", resetPolicy: "yearly", gapless: false);
        (await owner.AllocateAsync("purchase_order", companyId, "2026-09-22")).GetProperty("text").GetString().ShouldBe("PO-2026/27-0001");
        (await owner.AllocateAsync("purchase_order", companyId, "2027-06-30")).GetProperty("text").GetString().ShouldBe("PO-2026/27-0002");
        var nextYear = await owner.AllocateAsync("purchase_order", companyId, "2027-07-01");
        nextYear.GetProperty("text").GetString().ShouldBe("PO-2027/28-0001");
        nextYear.GetProperty("periodKey").GetString().ShouldBe("FY2027/28");
        (await owner.PostErrorAsync("/api/v1/numbering/allocate", new { documentType = "purchase_order", companyId, date = "2025-01-15", documentId = Guid.NewGuid() }, HttpStatusCode.Conflict)).ShouldBe("series.fiscal_year_required");

        var monthly = await owner.CreateSeriesAsync("RCT", "receipt", companyId, "RCT-{yyyy}{mm}-{seq:3}", resetPolicy: "monthly");
        (await owner.AllocateAsync("receipt", companyId, "2026-09-22")).GetProperty("text").GetString().ShouldBe("RCT-202609-001");
        (await owner.AllocateAsync("receipt", companyId, "2026-09-30")).GetProperty("text").GetString().ShouldBe("RCT-202609-002");
        (await owner.AllocateAsync("receipt", companyId, "2026-10-01")).GetProperty("text").GetString().ShouldBe("RCT-202610-001");
        var counters = (await owner.GetOkAsync($"/api/v1/numbering/series/{monthly.GetProperty("id").GetGuid()}")).GetProperty("counters").EnumerateArray().Select(static c => (c.Str("periodKey"), c.GetProperty("nextNumber").GetInt64())).ToList();
        counters.ShouldBe([("2026-09", 3L), ("2026-10", 2L)]);

        // Moving a counter forward is an ordinary change; backwards is a reset with a reason, never below an issued number.
        var yearlyId = yearly.GetProperty("id").GetGuid();
        var moved = await owner.PutAsync($"/api/v1/numbering/series/{yearlyId}/counter", new { periodKey = "FY2026/27", nextNumber = 500 });
        moved.GetProperty("counters").EnumerateArray().Single(static c => c.Str("periodKey") == "FY2026/27").GetProperty("nextNumber").GetInt64().ShouldBe(500);
        (await owner.PutErrorAsync($"/api/v1/numbering/series/{yearlyId}/counter", new { periodKey = "FY2026/27", nextNumber = 10 }, HttpStatusCode.UnprocessableEntity)).ShouldBe("series.reason_required");
        (await owner.PutErrorAsync($"/api/v1/numbering/series/{yearlyId}/counter", new { periodKey = "FY2026/27", nextNumber = 2, reason = "typo" }, HttpStatusCode.Conflict)).ShouldBe("series.counter_below_issued");
        var reset = await owner.PutAsync($"/api/v1/numbering/series/{yearlyId}/counter", new { periodKey = "FY2026/27", nextNumber = 10, reason = "Counter was advanced by mistake" });
        reset.GetProperty("counters").EnumerateArray().Single(static c => c.Str("periodKey") == "FY2026/27").GetProperty("nextNumber").GetInt64().ShouldBe(10);
        (await owner.AllocateAsync("purchase_order", companyId, "2026-10-02")).GetProperty("text").GetString().ShouldBe("PO-2026/27-0010");

        var timeline = (await owner.GetOkAsync($"/api/v1/audit/records/numbering_series/{yearlyId}")).EnumerateArray().ToList();
        timeline.Select(static e => e.Str("action")).ShouldBe(["created", "updated", "override"]);
        timeline[2].GetProperty("reason").GetString().ShouldBe("Counter was advanced by mistake");
        timeline[2].GetProperty("before").GetProperty("nextNumber").GetInt64().ShouldBe(500);
        var gaps = (await owner.GetOkAsync($"/api/v1/numbering/series/{yearlyId}/gaps")).GetProperty("periods").EnumerateArray().Single(static p => p.Str("periodKey") == "FY2026/27");
        gaps.GetProperty("missing").EnumerateArray().Select(static m => m.GetInt64()).ShouldBe([3, 4, 5, 6, 7, 8, 9]); // non-gapless: the report shows what the reset skipped

        // Without numbering.counter.reset the backwards move is refused even with a reason; forward moves and reads still work, allocation does not.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "numbering_admin", name = new { en = "Numbering admin" }, description = "", grants = new[] { "numbering.series.read", "numbering.series.manage", "organization.company.read" } });
        var email = $"numbering-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Numbering", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "numbering-passphrase-long-enough" }, Json)).ReadJsonAsync();
        using var admin = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await admin.PutAsync($"/api/v1/numbering/series/{yearlyId}/counter", new { periodKey = "FY2026/27", nextNumber = 20 })).GetProperty("code").GetString().ShouldBe("PO");
        (await admin.PutErrorAsync($"/api/v1/numbering/series/{yearlyId}/counter", new { periodKey = "FY2026/27", nextNumber = 15, reason = "x" }, HttpStatusCode.Forbidden)).ShouldBe("series.reset_forbidden");
        (await admin.PostAsJsonAsync("/api/v1/numbering/allocate", new { documentType = "purchase_order", companyId, date = "2026-10-03", documentId = Guid.NewGuid() }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_default_series_is_ensured_for_every_company_of_the_tenant_not_only_the_first()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (first, _) = await owner.CompanyWithBranchAsync("FIRSTCO");
        var (second, _) = await owner.CompanyWithBranchAsync("SECONDCO");

        await using var scope = Api.Services.CreateAsyncScope();
        await using var uow = await scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(TenantContext.System(new TenantId(ws.TenantId), "ensure-test"));
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        var allocator = scope.ServiceProvider.GetRequiredService<INumberAllocator>();
        foreach (var companyId in new[] { first, second })
        {
            // Series codes are unique in the tenant: a bare GRN for the first company must not leave the second without a series.
            var ensured = await allocator.EnsureDefaultSeriesAsync("purchase_receipt", new CompanyId(companyId), "GRN", "GRN-{yyyy}-{seq:5}");
            ensured.IsSuccess.ShouldBeTrue(ensured.Error?.Code);
            (await allocator.EnsureDefaultSeriesAsync("purchase_receipt", new CompanyId(companyId), "GRN", "GRN-{yyyy}-{seq:5}")).Value.ShouldBe(ensured.Value, "ensuring again finds the same series");
            var number = await allocator.AllocateAsync(new NumberRequest("purchase_receipt", new CompanyId(companyId), null, new DateOnly(2026, 9, 22), Guid.NewGuid()));
            number.IsSuccess.ShouldBeTrue(number.Error?.Code);
            number.Value.Text.ShouldBe("GRN-2026-00001");
        }

        (await uow.Connection.QueryAsync<string>(new CommandDefinition("SELECT code FROM app.num_series WHERE tenant_id = @t AND document_type = 'purchase_receipt' ORDER BY code", new { t = ws.TenantId }, uow.Transaction)))
            .ShouldBe(["GRN-FIRSTCO", "GRN-SECONDCO"]);
        await uow.CommitAsync();
    }
}
