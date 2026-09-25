using System.Net;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Accounting.Tests;

/// <summary>
/// Roadmap 2.2 through the API: profiles from the chart, determination, three-currency conversion with a rounding
/// line, the refusals (unresolved role, unbalanced, control cross-check, dimension rules, closed period), the
/// database-level balance guarantee, idempotent replay, reversal into the first open period, rebuild equals incremental.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PostingEngineTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private sealed record Setup(Guid CompanyId, Guid ChartId, Guid ProfileId, Dictionary<string, Guid> Accounts);

    /// <summary>A company on an IFRS chart with an active default profile and a USD rate for the fixture's date (2026-09-22).</summary>
    private static async Task<Setup> SetupAsync(HttpClient owner, string code, string functional, string? reporting, decimal usdRate)
    {
        var company = await owner.PostAsync("/api/v1/organization/companies", new
        {
            code,
            legalName = new { en = code + " Trading", ar = "شركة " + code },
            country = functional == "AED" ? "AE" : "IQ",
            functionalCurrency = functional,
            reportingCurrency = reporting,
            timeZone = "Asia/Baghdad",
        });
        var companyId = company.GetProperty("id").GetGuid();
        var chartId = (await owner.ChartFromTemplateAsync("IFRS_SME", "CH-" + code, companyId)).GetProperty("id").GetGuid();
        var profile = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/posting-profiles/from-chart", new { });
        profile.GetProperty("status").GetString().ShouldBe("active");
        profile.GetProperty("isCurrent").GetBoolean().ShouldBeTrue();
        profile.GetProperty("unresolvedRoles").GetArrayLength().ShouldBe(0, "the template names an account for every role");
        if (functional != "USD")
        {
            await owner.PostAsync("/api/v1/organization/rates", new { rateType = "spot", fromCurrency = "USD", toCurrency = functional, validFrom = "2026-01-01", rate = usdRate });
        }

        var accounts = (await owner.GetOkAsync($"/api/v1/accounting/charts/{chartId}/accounts")).EnumerateArray().ToDictionary(static a => a.GetProperty("code").GetString()!, static a => a.GetProperty("id").GetGuid(), StringComparer.Ordinal);
        return new Setup(companyId, chartId, profile.GetProperty("id").GetGuid(), accounts);
    }

    private static object Line(string role, decimal amount, string? subledgerType = null, Guid? subledgerRef = null, object? dimensions = null, Guid? accountId = null) =>
        new { accountRole = role, amount, subledgerType, subledgerRef, dimensions, accountId };

    private static object Posting(Guid companyId, string currency, object[] lines, string? idempotencyKey = null, bool isManual = false, string postingDate = "2026-09-22") =>
        new { companyId, sourceModule = "tests", sourceDocumentType = "probe", sourceDocumentId = Guid.NewGuid(), postingDate, currency, lines, idempotencyKey, isManual, description = new { en = "Probe", ar = "اختبار" } };

    [Fact]
    public async Task Posts_by_role_converts_to_functional_and_reporting_currency_and_adds_the_rounding_line()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var setup = await SetupAsync(owner, "AEG", "AED", "USD", 3.6725m);
        var customer = Guid.NewGuid();

        // 100.01 USD at 3.6725: the four lines round to 367.29 against 367.28, so the engine adds a 0.01 AED rounding line.
        var posted = await owner.PostAsync("/api/v1/accounting/postings", Posting(setup.CompanyId, "USD",
        [
            Line("AR", 100.01m, "AR", customer),
            Line("Revenue", -33.34m),
            Line("OutputTax", -33.33m),
            Line("Revenue", -33.34m),
        ], idempotencyKey: "inv-1"));
        posted.GetProperty("number").GetString().ShouldBe("JE-2026-000001");
        posted.GetProperty("currencyTc").GetString().ShouldBe("USD");
        posted.GetProperty("currencyFc").GetString().ShouldBe("AED");
        posted.GetProperty("currencyRc").GetString().ShouldBe("USD");
        posted.GetProperty("rateTcFc").GetDecimal().ShouldBe(3.6725m);
        posted.GetProperty("replayed").GetBoolean().ShouldBeFalse();
        var lines = posted.GetProperty("lines").EnumerateArray().ToList();
        lines.Count.ShouldBe(5);
        var ar = lines[0];
        ar.GetProperty("accountCode").GetString().ShouldBe("1210");
        ar.GetProperty("debitTc").GetDecimal().ShouldBe(100.01m);
        ar.GetProperty("debitFc").GetDecimal().ShouldBe(367.29m);
        ar.GetProperty("debitRc").GetDecimal().ShouldBe(100.01m, "reporting currency equals the transaction currency: no drift");
        ar.GetProperty("subledgerRef").GetGuid().ShouldBe(customer);
        lines[2].GetProperty("accountCode").GetString().ShouldBe("2210");
        lines[2].GetProperty("creditFc").GetDecimal().ShouldBe(122.40m);
        var rounding = lines[4];
        rounding.GetProperty("isRounding").GetBoolean().ShouldBeTrue();
        rounding.GetProperty("accountCode").GetString().ShouldBe("6900");
        rounding.GetProperty("debitTc").GetDecimal().ShouldBe(0m);
        rounding.GetProperty("creditTc").GetDecimal().ShouldBe(0m);
        rounding.GetProperty("creditFc").GetDecimal().ShouldBe(0.01m);
        rounding.GetProperty("debitRc").GetDecimal().ShouldBe(0m);
        lines.Sum(static l => l.GetProperty("debitFc").GetDecimal()).ShouldBe(lines.Sum(static l => l.GetProperty("creditFc").GetDecimal()));

        // The same key replays the same entry; the entry reads back with its lines and codes.
        var replay = await owner.PostAsync("/api/v1/accounting/postings", Posting(setup.CompanyId, "USD", [Line("AR", 5m, "AR", customer), Line("Revenue", -5m)], idempotencyKey: "inv-1"));
        replay.GetProperty("replayed").GetBoolean().ShouldBeTrue();
        replay.GetProperty("entryId").GetGuid().ShouldBe(posted.GetProperty("entryId").GetGuid());
        var entry = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{posted.GetProperty("entryId").GetGuid()}");
        entry.GetProperty("lineCount").GetInt32().ShouldBe(5);
        entry.GetProperty("lines").EnumerateArray().First().GetProperty("accountName").GetProperty("ar").GetString().ShouldBe("ذمم العملاء");
        entry.GetProperty("totalDebitFc").GetDecimal().ShouldBe(367.29m);
        entry.GetProperty("reversedByEntryId").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/journal-entries?from=2026-09-01")).GetProperty("items").GetArrayLength().ShouldBe(1);

        // Balances carry the same figures, grouped by account, period and currency.
        var balances = (await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances")).EnumerateArray().ToList();
        balances.Single(static b => b.GetProperty("accountCode").GetString() == "1210").GetProperty("debitFc").GetDecimal().ShouldBe(367.29m);
        balances.Single(static b => b.GetProperty("accountCode").GetString() == "4100").GetProperty("creditTc").GetDecimal().ShouldBe(66.68m);
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances/verify")).GetProperty("isConsistent").GetBoolean().ShouldBeTrue();

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Refuses_what_the_rules_forbid_with_the_reason_named()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var setup = await SetupAsync(owner, "IQT", "IQD", null, 1310m);
        var post = "/api/v1/accounting/postings";

        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m), Line("Inventory", -90m, "INV", Guid.NewGuid())]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.unbalanced");
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m)]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.lines_required");
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100.0001m), Line("Revenue", -100.0001m)]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.amount_precision");
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "XXX", [Line("Cogs", 100m), Line("Revenue", -100m)]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.currency_unknown");

        // Control accounts need their subledger item; other accounts must not carry one.
        var (control, why) = await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("AR", 100m), Line("Revenue", -100m)]), HttpStatusCode.UnprocessableEntity);
        control.ShouldBe("posting.subledger_ref_required");
        why.GetProperty("why").GetProperty("line").GetInt32().ShouldBe(1);
        why.GetProperty("why").GetProperty("subledgerType").GetString().ShouldBe("AR");
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("AR", 100m, "AP", Guid.NewGuid()), Line("Revenue", -100m)]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.subledger_type_mismatch");
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("AR", 100m, "AR", Guid.NewGuid()), Line("Revenue", -100m, "AR", Guid.NewGuid())]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.subledger_unexpected");

        // Documents post by role; naming an account is for manual journals, and a control account takes a manual line only with its subledger item.
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m, accountId: setup.Accounts["6100"]), Line("Revenue", -100m)]), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting.explicit_account_not_allowed");
        (await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("AR", 100m, accountId: setup.Accounts["1210"]), Line("Revenue", -100m)], isManual: true), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("account.manual_posting_blocked");
        (await owner.PostAsync(post, Posting(setup.CompanyId, "IQD", [Line("AR", 100m, "AR", Guid.NewGuid(), accountId: setup.Accounts["1210"]), Line("Revenue", -100m)], isManual: true))).GetProperty("lines").EnumerateArray().First().GetProperty("subledgerType").GetString().ShouldBe("AR", "a manual line on a control account passes when it names the subledger item it adjusts");

        // A required dimension: the account's rule decides, whichever way the line came in (roadmap 2.1 acceptance).
        var (_, costCentre) = await owner.DimensionValueAsync("COST_CENTER", "CC-1");
        await owner.PutAsync($"/api/v1/accounting/accounts/{setup.Accounts["6100"]}/dimension-rules", new object[] { new { dimensionCode = "COST_CENTER", rule = "required" } });
        var (missing, missingWhy) = await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m, accountId: setup.Accounts["6100"]), Line("Revenue", -100m)], isManual: true), HttpStatusCode.UnprocessableEntity);
        missing.ShouldBe("account.dimension_required");
        missingWhy.GetProperty("why").GetProperty("dimension").GetString().ShouldBe("COST_CENTER");
        var withDimension = await owner.PostAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m, dimensions: new { COST_CENTER = costCentre }, accountId: setup.Accounts["6100"]), Line("Revenue", -100m)], isManual: true));
        withDimension.GetProperty("lines").EnumerateArray().First().GetProperty("dimensionSetId").ValueKind.ShouldBe(JsonValueKind.String);
        withDimension.GetProperty("lines").EnumerateArray().First().GetProperty("postingRuleId").ValueKind.ShouldBe(JsonValueKind.Null, "an explicit account uses no rule");

        // An unresolved role names itself; the profile's coverage says the same.
        var profile = await owner.GetOkAsync($"/api/v1/accounting/posting-profiles/{setup.ProfileId}");
        var rules = profile.GetProperty("rules").EnumerateArray().Where(static r => r.GetProperty("accountRole").GetString() != "WhtPayable")
            .Select(static r => new { accountRole = r.GetProperty("accountRole").GetString(), accountCode = r.GetProperty("accountCode").GetString() }).ToList();
        var trimmed = await owner.PutAsync($"/api/v1/accounting/posting-profiles/{setup.ProfileId}/rules", rules);
        trimmed.GetProperty("unresolvedRoles").EnumerateArray().Select(static r => r.GetString()).ShouldBe(["WhtPayable"]);
        var (unresolved, unresolvedWhy) = await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m), Line("WhtPayable", -100m)]), HttpStatusCode.UnprocessableEntity);
        unresolved.ShouldBe("posting.rule_missing");
        unresolvedWhy.GetProperty("why").GetProperty("role").GetString().ShouldBe("WhtPayable");
        unresolvedWhy.GetProperty("why").GetProperty("line").GetInt32().ShouldBe(2);
        (await owner.PutErrorAsync($"/api/v1/accounting/posting-profiles/{setup.ProfileId}/rules", new[] { new { accountRole = "AR", accountCode = "6100" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting_rule.control_mismatch");
        (await owner.PutErrorAsync($"/api/v1/accounting/posting-profiles/{setup.ProfileId}/rules", new[] { new { accountRole = "Cogs", accountCode = "1210" } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("posting_rule.control_unexpected");

        // A closed period refuses with the period, its state and the permission that reopens it.
        var period = await owner.GetOkAsync($"/api/v1/organization/companies/{setup.CompanyId}/periods/resolve?date=2026-09-22&module=GL");
        var periodId = period.GetProperty("periodId").GetGuid();
        await owner.PutAsync($"/api/v1/organization/periods/{periodId}/states", new { companyId = setup.CompanyId, modules = new[] { "GL" }, state = "hard_closed" });
        var (closed, closedWhy) = await owner.PostErrorAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m), Line("Revenue", -100m)]), HttpStatusCode.Conflict);
        closed.ShouldBe("period.closed");
        closedWhy.GetProperty("why").GetProperty("state").GetString().ShouldBe("hard_closed");
        closedWhy.GetProperty("why").GetProperty("requiredPermission").GetString().ShouldBe("accounting.period.reopen");
        (await owner.PostAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 100m), Line("Revenue", -100m)], postingDate: "2026-10-05"))).GetProperty("postingDate").GetString().ShouldBe("2026-10-05");

        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task The_database_refuses_an_unbalanced_or_incomplete_entry_whatever_client_writes_it()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var setup = await SetupAsync(owner, "DBG", "IQD", null, 1310m);
        var period = await owner.GetOkAsync($"/api/v1/organization/companies/{setup.CompanyId}/periods/resolve?date=2026-09-22&module=GL");

        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await db.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = ws.TenantId.ToString() });
        var entryColumns = "(tenant_id, id, company_id, number, posting_date, document_date, fiscal_year_id, fiscal_period_id, source_module, source_document_type, source_document_id, currency_tc, currency_fc, rate_tc_fc, line_count)";
        var lineColumns = "(tenant_id, id, entry_id, company_id, posting_date, line_no, account_id, account_role, debit_tc, credit_tc, currency_tc, rate_tc_fc, debit_fc, credit_fc, rate_date)";

        async Task<string> TryCommitAsync(string number, int declaredLines, (decimal Debit, decimal Credit)[] lines)
        {
            await using var tx = await db.BeginTransactionAsync(TestContext.Current.CancellationToken);
            var entry = Guid.CreateVersion7();
            await db.ExecuteAsync($"INSERT INTO app.gl_journal_entries {entryColumns} VALUES (@t, @id, @c, @n, '2026-09-22', '2026-09-22', @y, @p, 'tests', 'raw', @d, 'IQD', 'IQD', 1, @count)",
                new { t = ws.TenantId, id = entry, c = setup.CompanyId, n = number, y = period.GetProperty("fiscalYearId").GetGuid(), p = period.GetProperty("periodId").GetGuid(), d = Guid.NewGuid(), count = declaredLines }, tx);
            for (var i = 0; i < lines.Length; i++)
            {
                await db.ExecuteAsync($"INSERT INTO app.gl_journal_lines {lineColumns} VALUES (@t, @id, @e, @c, '2026-09-22', @n, @a, 'Suspense', @dr, @cr, 'IQD', 1, @dr, @cr, '2026-09-22')",
                    new { t = ws.TenantId, id = Guid.CreateVersion7(), e = entry, c = setup.CompanyId, n = i + 1, a = setup.Accounts["9100"], dr = lines[i].Debit, cr = lines[i].Credit }, tx);
            }

            try
            {
                await tx.CommitAsync(TestContext.Current.CancellationToken);
                return "committed";
            }
            catch (PostgresException ex)
            {
                return ex.MessageText;
            }
        }

        (await TryCommitAsync("RAW-1", 2, [(100m, 0m), (0m, 100m)])).ShouldBe("committed");
        (await TryCommitAsync("RAW-2", 2, [(100m, 0m), (0m, 90m)])).ShouldStartWith("gl_entry_unbalanced");
        (await TryCommitAsync("RAW-3", 2, [(100m, 0m)])).ShouldStartWith("gl_entry_incomplete");
        (await db.ExecuteScalarAsync<int>("SELECT count(*) FROM app.gl_journal_entries WHERE company_id = @c AND number LIKE 'RAW-%'", new { c = setup.CompanyId })).ShouldBe(1);

        // Posted rows are immutable for everyone, the owner role included.
        var frozen = await Should.ThrowAsync<PostgresException>(() => db.ExecuteAsync("UPDATE app.gl_journal_lines SET debit_tc = 1 WHERE company_id = @c", new { c = setup.CompanyId }));
        frozen.MessageText.ShouldContain("append_only_violation");
    }

    [Fact]
    public async Task Reverses_into_the_first_open_period_and_the_rebuilt_balances_equal_the_incremental_ones()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var setup = await SetupAsync(owner, "REV", "IQD", "USD", 1310m);
        var supplier = (await owner.PostAsync("/api/v1/partners", new { code = "SUP-R", legalName = new { en = "Reversal Supplies", ar = "توريدات العكس" }, isSupplier = true })).GetProperty("id").GetGuid();
        var post = "/api/v1/accounting/postings";

        var first = await owner.PostAsync(post, Posting(setup.CompanyId, "USD", [Line("Inventory", 250m, "INV", Guid.NewGuid()), Line("AP", -250m, "AP", supplier)]));
        var second = await owner.PostAsync(post, Posting(setup.CompanyId, "IQD", [Line("Cogs", 131000m), Line("Inventory", -131000m, "INV", Guid.NewGuid())]));
        first.GetProperty("lines").EnumerateArray().First().GetProperty("debitFc").GetDecimal().ShouldBe(327500m);

        // Close September: the reversal without a date lands on the first day of the first open period.
        var periodId = (await owner.GetOkAsync($"/api/v1/organization/companies/{setup.CompanyId}/periods/resolve?date=2026-09-22&module=GL")).GetProperty("periodId").GetGuid();
        await owner.PutAsync($"/api/v1/organization/periods/{periodId}/states", new { companyId = setup.CompanyId, modules = new[] { "GL" }, state = "hard_closed" });
        var firstId = first.GetProperty("entryId").GetGuid();
        (await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{firstId}/reverse", new { reason = "" }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("reversal.reason_required");
        var reversal = await owner.PostAsync($"/api/v1/accounting/journal-entries/{firstId}/reverse", new { reason = "Duplicate receipt" });
        reversal.GetProperty("isReversal").GetBoolean().ShouldBeTrue();
        reversal.GetProperty("postingDate").GetString().ShouldBe("2026-10-01");
        var mirrored = reversal.GetProperty("lines").EnumerateArray().ToList();
        mirrored[0].GetProperty("creditTc").GetDecimal().ShouldBe(250m);
        mirrored[0].GetProperty("creditFc").GetDecimal().ShouldBe(327500m, "copied, not recomputed");
        mirrored[1].GetProperty("debitTc").GetDecimal().ShouldBe(250m);
        mirrored[1].GetProperty("subledgerRef").GetGuid().ShouldBe(supplier);
        var original = await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{firstId}");
        original.GetProperty("reversedByEntryId").GetGuid().ShouldBe(reversal.GetProperty("entryId").GetGuid());
        (await owner.GetOkAsync($"/api/v1/accounting/journal-entries/{reversal.GetProperty("entryId").GetGuid()}")).GetProperty("reversesEntryId").GetGuid().ShouldBe(firstId);
        (await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{firstId}/reverse", new { reason = "Again" }, HttpStatusCode.Conflict)).Code.ShouldBe("reversal.already_reversed");
        (await owner.PostErrorAsync($"/api/v1/accounting/journal-entries/{reversal.GetProperty("entryId").GetGuid()}/reverse", new { reason = "Undo" }, HttpStatusCode.Conflict)).Code.ShouldBe("reversal.of_reversal");

        // AP nets to zero across the two periods; the derived table equals the lines; a corrupted row is found and repaired.
        var balances = (await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances")).EnumerateArray().Where(static b => b.GetProperty("accountCode").GetString() == "2110").ToList();
        balances.Sum(static b => b.GetProperty("creditTc").GetDecimal() - b.GetProperty("debitTc").GetDecimal()).ShouldBe(0m);
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances/verify")).GetProperty("isConsistent").GetBoolean().ShouldBeTrue();

        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        await db.OpenAsync(TestContext.Current.CancellationToken);
        await db.ExecuteAsync("SELECT set_config('app.tenant_id', @t, false)", new { t = ws.TenantId.ToString() });
        (await db.ExecuteAsync("UPDATE app.gl_balances SET debit_fc = debit_fc + 1 WHERE company_id = @c AND account_id = @a", new { c = setup.CompanyId, a = setup.Accounts["5100"] })).ShouldBe(1);
        var broken = await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances/verify");
        broken.GetProperty("isConsistent").GetBoolean().ShouldBeFalse();
        broken.GetProperty("differences").EnumerateArray().Single().GetProperty("column").GetString().ShouldBe("debit_fc");
        var rebuilt = await owner.PostAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances/rebuild", new { }, HttpStatusCode.OK);
        rebuilt.GetProperty("rowsAfter").GetInt32().ShouldBe(rebuilt.GetProperty("rowsBefore").GetInt32());
        (await owner.GetOkAsync($"/api/v1/accounting/companies/{setup.CompanyId}/balances/verify")).GetProperty("isConsistent").GetBoolean().ShouldBeTrue();
        second.GetProperty("number").GetString().ShouldBe("JE-2026-000002");

        await owner.AssertInvariantsAsync();
    }
}
