using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Accounting.Tests;

/// <summary>
/// Roadmap 2.4 and hard scenario 7 through the API: a closed period blocks with the reason and the way out named, the
/// correction posts into the open period referencing the original, reopening needs the permission and a reason and is
/// audited with who and why, and allow-posting windows hold a role to its dates.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PeriodControlTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Line(string account, decimal debit = 0m, decimal credit = 0m) => new { accountCode = account, debit, credit };

    private static object Journal(string date, string account, string credit, decimal amount) =>
        new { postingDate = date, currency = "IQD", description = new { en = "Rent", ar = "إيجار" }, lines = new[] { Line(account, debit: amount), Line(credit, credit: amount) } };

    [Fact]
    public async Task Scenario_7_a_closed_period_blocks_and_the_correction_posts_into_the_open_period_referencing_the_original()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await owner.CompanyAsync("PER", "IQD");
        await owner.ChartFromTemplateAsync("IFRS_SME", "CH-PER", companyId);
        await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/posting-profiles/from-chart", new { });
        var journals = $"/api/v1/accounting/companies/{companyId}/journals";

        // September: the rent journal is posted, then September is hard-closed for the general ledger.
        var original = await owner.PostAsync(journals, Journal("2026-09-10", "6110", "2170", 1500000m));
        var originalId = original.GetProperty("id").GetGuid();
        var originalPosted = await owner.PostAsync($"/api/v1/accounting/journals/{originalId}/post", new { }, HttpStatusCode.OK);
        var originalEntryId = originalPosted.GetProperty("journalEntryId").GetGuid();
        var periodId = (await owner.GetOkAsync($"/api/v1/organization/companies/{companyId}/periods/resolve?date=2026-09-10&module=GL")).GetProperty("periodId").GetGuid();
        await owner.PutAsync($"/api/v1/organization/periods/{periodId}/states", new { companyId, modules = new[] { "GL" }, state = "hard_closed", reason = "September closed" });

        // Someone tries to post into the closed period: blocked, with the period, its state and the permission that reopens it.
        var late = await owner.PostAsync(journals, Journal("2026-09-15", "6120", "2170", 90000m));
        var (blocked, why) = await owner.PostErrorAsync($"/api/v1/accounting/journals/{late.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.Conflict);
        blocked.ShouldBe("period.closed");
        why.GetProperty("why").GetProperty("periodId").GetGuid().ShouldBe(periodId);
        why.GetProperty("why").GetProperty("state").GetString().ShouldBe("hard_closed");
        why.GetProperty("why").GetProperty("requiredPermission").GetString().ShouldBe("accounting.period.reopen");
        var seriesList = await owner.GetOkAsync("/api/v1/numbering/series");
        var mjSeries = seriesList.EnumerateArray().Single(static x => x.GetProperty("code").GetString()!.StartsWith("MJ-", StringComparison.Ordinal)).GetProperty("id").GetGuid();
        (await owner.GetOkAsync($"/api/v1/numbering/series/{mjSeries}/allocations")).GetArrayLength().ShouldBe(1, "a refused posting rolls back with the number it had taken (gapless)");

        // The correction: a draft copied from the original into the first open period, edited, then posted.
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{late.GetProperty("id").GetGuid()}/correct", new { reason = "x" }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.not_posted");
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{originalId}/correct", new { reason = " " }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("journal.reason_required");
        var draft = await owner.PostAsync($"/api/v1/accounting/journals/{originalId}/correct", new { reason = "Wrong expense account" });
        var draftId = draft.GetProperty("id").GetGuid();
        draft.GetProperty("status").GetString().ShouldBe("draft");
        draft.GetProperty("postingDate").GetString().ShouldBe("2026-10-01", "the first open period on or after the original date");
        draft.GetProperty("correctsJournalId").GetGuid().ShouldBe(originalId);
        draft.GetProperty("correctionReason").GetString().ShouldBe("Wrong expense account");
        draft.GetProperty("description").GetProperty("en").GetString().ShouldStartWith("Correction of MJ-2026-00001");
        draft.GetProperty("lines").EnumerateArray().Select(static l => l.GetProperty("accountCode").GetString()).ShouldBe(["6110", "2170"]);
        await owner.PutAsync($"/api/v1/accounting/journals/{draftId}", new { postingDate = "2026-10-01", currency = "IQD", description = new { en = "Correction of September rent" }, lines = new[] { Line("6130", debit: 1500000m), Line("2170", credit: 1500000m) } });
        var corrected = await owner.PostAsync($"/api/v1/accounting/journals/{draftId}/post", new { }, HttpStatusCode.OK);
        corrected.GetProperty("status").GetString().ShouldBe("posted");
        corrected.GetProperty("number").GetString().ShouldBe("MJ-2026-00002");
        corrected.GetProperty("postingDate").GetString().ShouldBe("2026-10-01");
        var replacementEntryId = corrected.GetProperty("journalEntryId").GetGuid();

        // Both directions are navigable: the replacement corrects the original, the reversal reverses it, all dated in October.
        var replacement = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{replacementEntryId}");
        replacement.GetProperty("postingDate").GetString().ShouldBe("2026-10-01");
        replacement.GetProperty("lines").EnumerateArray().First().GetProperty("accountCode").GetString().ShouldBe("6130");
        var corrects = replacement.GetProperty("links").EnumerateArray().Single(static l => l.GetProperty("relation").GetString() == "corrects");
        corrects.GetProperty("toEntryId").GetGuid().ShouldBe(originalEntryId);
        corrects.GetProperty("reason").GetString().ShouldBe("Wrong expense account");
        var originalEntry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{originalEntryId}");
        originalEntry.GetProperty("links").EnumerateArray().Select(static l => l.GetProperty("relation").GetString()).OrderBy(static r => r, StringComparer.Ordinal).ShouldBe(["corrects", "reverses"]);
        var reversalEntryId = originalEntry.GetProperty("reversedByEntryId").GetGuid();
        var reversal = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{reversalEntryId}");
        reversal.GetProperty("isReversal").GetBoolean().ShouldBeTrue();
        reversal.GetProperty("postingDate").GetString().ShouldBe("2026-10-01", "the reversal cannot land in the closed month either");
        reversal.GetProperty("lines").EnumerateArray().First().GetProperty("creditTc").GetDecimal().ShouldBe(1500000m);
        (await owner.GetOkAsync($"/api/v1/accounting/journals/{originalId}")).GetProperty("correctedByJournalId").GetGuid().ShouldBe(draftId);
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{originalId}/correct", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("journal.already_corrected");

        // The engine offers the same to documents: an entry corrected with a replacement request.
        var (twice, _) = await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{originalEntryId}/correct", new { reason = "Again", replacement = new { companyId, sourceModule = "accounting", sourceDocumentType = "journal_entry", sourceDocumentId = Guid.NewGuid(), postingDate = "2026-09-10", currency = "IQD", lines = new[] { new { accountRole = "Cogs", amount = 10m }, new { accountRole = "Revenue", amount = -10m } } } }, HttpStatusCode.Conflict);
        twice.ShouldBe("correction.already_corrected");

        // Reopening: refused without the permission; with it, a reason is required and the audit says who and why.
        var (clerkToken, clerkRoleId) = await InviteAsync(owner, ws, "clerk", "organization.company.read", "accounting.journal.read", "accounting.journal.manage", "accounting.journal.post");
        using var clerk = Api.ClientFor(clerkToken);
        (await clerk.PostAsJsonAsync($"/api/v1/organization/periods/{periodId}/reopen", new { companyId, modules = new[] { "GL" }, reason = "Please" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var reopened = await owner.PostAsync($"/api/v1/organization/periods/{periodId}/reopen", new { companyId, modules = new[] { "GL" }, reason = "Auditor adjustment", state = "open" }, HttpStatusCode.OK);
        reopened.EnumerateArray().Single(static s => s.GetProperty("module").GetString() == "GL").GetProperty("state").GetString().ShouldBe("open");
        var timeline = (await owner.GetOkAsync($"/api/v1/audit/records/fiscal_period/{periodId}")).EnumerateArray().ToList();
        var override_ = timeline.Last();
        override_.GetProperty("action").GetString().ShouldBe("override");
        override_.GetProperty("reason").GetString().ShouldBe("Auditor adjustment");
        override_.GetProperty("actor").GetProperty("id").GetGuid().ShouldBe(ws.UserId);
        var lateJournalId = late.GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/accounting/journals/{lateJournalId}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted", "September is open again");

        // Allow-posting windows: the clerk's role posts from 15 October only; the owner is not restricted until everyone is.
        var windowsPath = $"/api/v1/organization/companies/{companyId}/posting-windows";
        (await owner.PutErrorAsync(windowsPath, new[] { new { roleId = (Guid?)clerkRoleId, allowFrom = (string?)null, allowTo = (string?)null } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting_window.bounds_required");
        (await owner.PutErrorAsync(windowsPath, new[] { new { roleId = (Guid?)clerkRoleId, allowFrom = "2026-10-15", allowTo = "2026-10-01" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting_window.bounds_inverted");
        (await owner.PutErrorAsync(windowsPath, new[] { new { roleId = (Guid?)null, allowFrom = "2026-10-15" }, new { roleId = (Guid?)null, allowFrom = "2026-10-16" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting_window.duplicate");
        (await owner.PutErrorAsync(windowsPath, new[] { new { roleId = (Guid?)Guid.NewGuid(), allowFrom = "2026-10-15" } }, HttpStatusCode.NotFound)).Code.ShouldBe("role.not_found");
        (await clerk.PutAsJsonAsync(windowsPath, new[] { new { roleId = (Guid?)clerkRoleId, allowFrom = "2026-01-01" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var windows = await owner.PutAsync(windowsPath, new[] { new { roleId = (Guid?)clerkRoleId, allowFrom = "2026-10-15", reason = "Clerks post the current fortnight only" } });
        windows.GetArrayLength().ShouldBe(1);
        windows[0].GetProperty("roleId").GetGuid().ShouldBe(clerkRoleId);
        (await owner.GetOkAsync(windowsPath)).GetArrayLength().ShouldBe(1);

        var early = await clerk.PostAsync(journals, Journal("2026-10-05", "6120", "2170", 5000m));
        var (outside, outsideWhy) = await clerk.PostErrorAsync($"/api/v1/accounting/journals/{early.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.Conflict);
        outside.ShouldBe("posting.outside_window");
        outsideWhy.GetProperty("why").GetProperty("allowFrom").GetString().ShouldBe("2026-10-15");
        outsideWhy.GetProperty("why").GetProperty("roleId").GetGuid().ShouldBe(clerkRoleId);
        var inWindow = await clerk.PostAsync(journals, Journal("2026-10-20", "6120", "2170", 5000m));
        (await clerk.PostAsync($"/api/v1/accounting/journals/{inWindow.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");
        var ownerEarly = await owner.PostAsync(journals, Journal("2026-10-05", "6120", "2170", 5000m));
        (await owner.PostAsync($"/api/v1/accounting/journals/{ownerEarly.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted", "no window applies to the owner");

        await owner.PutAsync(windowsPath, new object[] { new { roleId = (Guid?)clerkRoleId, allowFrom = "2026-10-15" }, new { roleId = (Guid?)null, allowTo = "2026-10-31" } });
        var ownerLate = await owner.PostAsync(journals, Journal("2026-11-05", "6120", "2170", 5000m));
        (await owner.PostErrorAsync($"/api/v1/accounting/journals/{ownerLate.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("posting.outside_window");
        var clerkLate = await clerk.PostAsync(journals, Journal("2026-11-05", "6120", "2170", 5000m));
        (await clerk.PostAsync($"/api/v1/accounting/journals/{clerkLate.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted", "a role window replaces the company window for its holders");

        await owner.AssertInvariantsAsync();
    }

    private async Task<(string Token, Guid RoleId)> InviteAsync(HttpClient owner, Workspace ws, string code, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code, name = new { en = code, ar = code }, description = "", grants });
        var email = $"{code}-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = code, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "clerk-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return (accepted.GetProperty("accessToken").GetString()!, role.GetProperty("id").GetGuid());
    }
}
