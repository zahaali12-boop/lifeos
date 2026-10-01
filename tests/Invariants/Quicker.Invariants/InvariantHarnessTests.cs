using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.TestSupport;
using Quicker.Identity.TestSupport;
using Quicker.Integrity.Application;
using Quicker.Integrity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Messaging.Jobs;

namespace Quicker.Invariants;

/// <summary>
/// Roadmap 2.6: the harness passes on intact books, names the corrupted balance row, the tampered audit event and the
/// unbalanced entry when someone with owner rights changes stored data behind the engine's back, is clean again after
/// a rebuild, and runs across tenants as the platform job, which fails while any tenant fails.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InvariantHarnessTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Intact_books_pass_every_check_and_a_corrupted_balance_row_fails_the_harness_until_rebuilt()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await owner.CompanyWithPostingsAsync("INV");

        var report = await owner.RunInvariantsAsync();
        report.GetProperty("passed").GetBoolean().ShouldBeTrue(JsonSerializer.Serialize(report));
        report.GetProperty("tenantId").GetGuid().ShouldBe(ws.TenantId);
        report.GetProperty("checks").EnumerateArray().Select(static c => c.GetProperty("code").GetString()).ShouldBe(InvariantCodes.All);
        report.Check("entries_balanced").GetProperty("checked").GetInt64().ShouldBe(2);
        report.Check("trial_balance_zero").GetProperty("checked").GetInt64().ShouldBe(1);
        report.Check("balances_match_lines").GetProperty("checked").GetInt64().ShouldBe(2, "one balance row per account and period");
        report.Check("audit_chain_intact").GetProperty("checked").GetInt64().ShouldBeGreaterThan(5);
        report.Check("tenant_isolation").GetProperty("checked").GetInt64().ShouldBeGreaterThan(40);
        report.Check("gapless_numbering").GetProperty("checked").GetInt64().ShouldBe(2, "the journal entry series and the manual journal series");
        (await owner.RunInvariantsAsync(companyId)).GetProperty("passed").GetBoolean().ShouldBeTrue();

        // A balance row altered as the schema owner in maintenance mode: the harness names the row and the column; a rebuild repairs it.
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId,
            "UPDATE app.gl_balances SET debit_fc = debit_fc + 1 WHERE tenant_id = @t AND account_id = (SELECT id FROM app.gl_accounts WHERE tenant_id = @t AND code = '6110' LIMIT 1)", new { t = ws.TenantId });
        var corrupted = await owner.RunInvariantsAsync();
        corrupted.GetProperty("passed").GetBoolean().ShouldBeFalse();
        var balances = corrupted.Check("balances_match_lines");
        balances.GetProperty("passed").GetBoolean().ShouldBeFalse();
        var problem = balances.GetProperty("problems").EnumerateArray().Single().GetString()!;
        problem.ShouldContain("account 6110");
        problem.ShouldContain("debit_fc: stored 1750001, lines say 1750000");
        corrupted.Check("entries_balanced").GetProperty("passed").GetBoolean().ShouldBeTrue("the lines themselves are still right");
        await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/balances/rebuild", new { }, HttpStatusCode.OK);
        (await owner.RunInvariantsAsync()).GetProperty("passed").GetBoolean().ShouldBeTrue("the rebuild restored the derived balances");
    }

    [Fact]
    public async Task A_tampered_audit_event_and_an_unbalanced_line_are_named_and_the_job_fails_for_that_tenant()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        await owner.CompanyWithPostingsAsync("BAD");
        (await owner.RunInvariantsAsync()).GetProperty("passed").GetBoolean().ShouldBeTrue();

        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "UPDATE app.aud_events SET reason = 'tampered' WHERE tenant_id = @t AND seq = 2", new { t = ws.TenantId });
        var tampered = await owner.RunInvariantsAsync();
        tampered.GetProperty("passed").GetBoolean().ShouldBeFalse();
        var chain = tampered.Check("audit_chain_intact");
        chain.GetProperty("passed").GetBoolean().ShouldBeFalse();
        chain.GetProperty("problems").EnumerateArray().Single().GetString()!.ShouldContain("chain broken at sequence 2");

        // A posted line altered behind the append-only guard (maintenance mode): the entry, the trial balance and the balances all say so.
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId,
            "UPDATE app.gl_journal_lines SET debit_tc = debit_tc + 10, debit_fc = debit_fc + 10 WHERE tenant_id = @t AND debit_tc > 0 AND posting_date = '2026-09-20'", new { t = ws.TenantId });
        var unbalanced = await owner.RunInvariantsAsync();
        unbalanced.GetProperty("failedCodes").EnumerateArray().Select(static c => c.GetString()).ShouldBe(["entries_balanced", "trial_balance_zero", "balances_match_lines", "audit_chain_intact"]);
        unbalanced.Check("entries_balanced").GetProperty("problems").EnumerateArray().Single().GetString()!.ShouldEndWith(": off by 10 tc, 10 fc, 0 rc");
        unbalanced.Check("trial_balance_zero").GetProperty("problems").EnumerateArray().Single().GetString()!.ShouldContain("trial balance off by 10 fc");

        // The platform job runs every active tenant and fails naming this one and its failing checks.
        await using var scope = Api.Services.CreateAsyncScope();
        var job = scope.ServiceProvider.GetRequiredService<IntegrityCheckAllJob>();
        var failure = await Should.ThrowAsync<JobFailedException>(() => job.ExecuteAsync(new IntegrityCheckAllPayload(), new PlatformJobContext(), TestContext.Current.CancellationToken));
        failure.Message.ShouldContain(ws.TenantId.ToString());
        failure.Message.ShouldContain("entries_balanced, trial_balance_zero, balances_match_lines, audit_chain_intact");
    }

    /// <summary>A platform (tenant-less) job context, as the scheduler hands the daily run.</summary>
    private sealed class PlatformJobContext : IJobContext
    {
        public Guid JobId { get; } = Guid.CreateVersion7();

        public TenantId? TenantId => null;

        public int Attempt => 1;

        public Task ReportProgressAsync(object progress, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
