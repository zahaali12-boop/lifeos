using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Reporting.Tests;

/// <summary>
/// Financial statements (founder programme item 9, slice 9c; ADR-0034 §5): the shipped profit and loss, balance sheet
/// and trial balance layouts computed over the journal facts for a company and a period; the profit and loss ties to
/// the ledger's trial balance, the balance sheet balances with the earnings not yet closed, the trial balance layout
/// equals the inquiry account by account; a custom layout saved with a formula row and a prior-period variance, run,
/// drilled to its accounts, sliced by a dimension; the designer's checks; a reader without the journal permission
/// refused; the owner's delete.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class StatementTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static JsonElement Row(JsonElement statement, string code) => statement.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("code").GetString() == code);

    private static decimal? Cell(JsonElement statement, string row, int column)
    {
        var value = Row(statement, row).GetProperty("values")[column];
        return value.ValueKind == JsonValueKind.Null ? null : value.GetDecimal();
    }

    [Fact]
    public async Task Shipped_statements_tie_to_the_ledger_and_a_custom_layout_is_designed_run_drilled_and_sliced()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "STM", legalName = Name("Statements Co", "شركة القوائم"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "fifo" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var alpha = (await owner.PostAsync("/api/v1/partners", new { code = "CUS-A", legalName = Name("Alpha Retail", "ألفا"), isCustomer = true })).GetProperty("id").GetGuid();
        await owner.PutAsync($"/api/v1/partners/{alpha}/customer-accounts/{companyId}", new { currency = "IQD" });
        var tea = (await owner.PostAsync("/api/v1/items", new { code = "TEA", name = Name("Tea service", "خدمة شاي"), type = "service", baseUom = "PCS", listPrice = 1_000m, listPriceCurrency = "IQD" })).GetProperty("id").GetGuid();

        async Task PostInvoiceAsync(DateOnly on, decimal quantity)
        {
            var invoice = await owner.PostAsync("/api/v1/sales/invoices", new { companyId, partnerId = alpha, kind = "invoice", documentDate = Iso(on), lines = new[] { new { kind = "item", itemId = tea, quantity, unitPrice = 1_000m } } });
            await owner.PostAsync($"/api/v1/sales/invoices/{invoice.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK);
        }

        async Task PostJournalAsync(string date, string description, object[] lines)
        {
            var draft = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new { kind = "manual", postingDate = date, currency = "IQD", description = Name(description, description), lines });
            (await owner.PostAsync($"/api/v1/accounting/journals/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");
        }

        // The books: capital subscribed by the shareholder, three invoices over two months, September's rent accrued and tagged with a cost centre.
        var costCentre = (await owner.GetOkAsync("/api/v1/organization/dimensions")).EnumerateArray().Single(static d => d.GetProperty("code").GetString() == "COST_CENTER").GetProperty("id").GetGuid();
        var hq = (await owner.PostAsync($"/api/v1/organization/dimensions/{costCentre}/values", new { code = "HQ", name = Name("Head office", "المقر") })).GetProperty("id").GetGuid();
        await PostJournalAsync("2026-08-01", "Capital", [new { accountCode = "1250", debit = 10_000m, credit = 0m }, new { accountCode = "3100", debit = 0m, credit = 10_000m }]);
        await PostInvoiceAsync(new DateOnly(2026, 8, 15), 5m);
        await PostInvoiceAsync(new DateOnly(2026, 9, 10), 2m);
        await PostInvoiceAsync(new DateOnly(2026, 9, 22), 3m);
        await PostJournalAsync("2026-09-05", "Rent", [new { accountCode = "6110", debit = 1_200m, credit = 0m, dimensions = new { COST_CENTER = hq } }, new { accountCode = "2170", debit = 0m, credit = 1_200m }]);

        // The shipped layouts are listed for every company, in order, with their definitions.
        var layouts = await owner.GetOkAsync($"/api/v1/reporting/statements/layouts?companyId={companyId}");
        layouts.EnumerateArray().Select(static l => l.GetProperty("code").GetString()).ShouldBe(["PL", "BS", "TB"]);
        layouts[0].GetProperty("isSystem").GetBoolean().ShouldBeTrue();
        layouts[0].GetProperty("name").GetProperty("ar").GetString().ShouldBe("قائمة الأرباح والخسائر");
        layouts[0].GetProperty("definition").GetProperty("rows").EnumerateArray().Select(static r => r.GetProperty("code").GetString()).ShouldContain("NET");

        // The profit and loss for September: sales 5,000, rent 1,200, net profit 3,800; the year to date 10,000 and 8,800; nothing a year earlier.
        var pl = await owner.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "PL", from = "2026-09-01", to = "2026-09-30" }, HttpStatusCode.OK);
        pl.GetProperty("kind").GetString().ShouldBe("pl");
        pl.GetProperty("from").GetString().ShouldBe("2026-09-01");
        pl.GetProperty("fiscalYearStart").GetString().ShouldBe("2026-01-01");
        pl.GetProperty("includeClosing").GetBoolean().ShouldBeFalse();
        pl.GetProperty("columns").EnumerateArray().Select(static c => c.GetProperty("code").GetString()).ShouldBe(["PERIOD", "YTD", "PRIOR_YEAR", "VAR", "VAR_PCT"]);
        pl.GetProperty("columns")[1].GetProperty("from").GetString().ShouldBe("2026-01-01");
        pl.GetProperty("columns")[2].GetProperty("to").GetString().ShouldBe("2025-09-30");
        Cell(pl, "SALES", 0).ShouldBe(5_000m);
        Cell(pl, "SALES", 1).ShouldBe(10_000m);
        Cell(pl, "SALES", 2).ShouldBe(0m);
        Cell(pl, "SALES", 3).ShouldBe(5_000m);
        Cell(pl, "SALES", 4).ShouldBeNull();
        Cell(pl, "NET_REVENUE", 0).ShouldBe(5_000m);
        Cell(pl, "OPEX", 0).ShouldBe(1_200m);
        Cell(pl, "EBT", 0).ShouldBe(3_800m);
        Cell(pl, "NET", 0).ShouldBe(3_800m);
        Cell(pl, "NET", 1).ShouldBe(8_800m);
        Row(pl, "REV_HEAD").GetProperty("rowType").GetString().ShouldBe("header");
        pl.GetProperty("rows").EnumerateArray().Select(static r => r.GetProperty("code").GetString()).ShouldNotContain("TAX");
        var salesAccounts = Row(pl, "SALES").GetProperty("accounts").EnumerateArray().ToList();
        salesAccounts.Single().GetProperty("code").GetString().ShouldBe("4100");
        salesAccounts.Single().GetProperty("values")[0].GetDecimal().ShouldBe(5_000m);
        Row(pl, "OPEX").GetProperty("accounts").EnumerateArray().Single().GetProperty("code").GetString().ShouldBe("6110");

        // It ties to the ledger: the net profit equals the credit less debit movement of every revenue and expense account in the inquiry.
        var tb = await owner.GetOkAsync($"/api/v1/accounting/companies/{companyId}/reports/trial-balance?from=2026-09-01&asOf=2026-09-30");
        var inquiryRows = tb.GetProperty("rows").EnumerateArray().ToList();
        var inquiryProfit = inquiryRows.Where(static r => r.GetProperty("accountType").GetString() is "revenue" or "expense").Sum(static r => r.GetProperty("credit").GetDecimal() - r.GetProperty("debit").GetDecimal());
        inquiryProfit.ShouldBe(3_800m);
        Cell(pl, "NET", 0).ShouldBe(inquiryProfit);

        // The balance sheet at the end of September balances: the earnings not yet closed sit in equity, and the check row is hidden at zero.
        var bs = await owner.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "BS", to = "2026-09-30" }, HttpStatusCode.OK);
        bs.GetProperty("includeClosing").GetBoolean().ShouldBeTrue();
        Cell(bs, "CURRENT_ASSETS", 0).ShouldBe(20_000m);
        Cell(bs, "TOTAL_ASSETS", 0).ShouldBe(20_000m);
        Cell(bs, "CURRENT_LIABILITIES", 0).ShouldBe(1_200m);
        Cell(bs, "TOTAL_LIABILITIES", 0).ShouldBe(1_200m);
        Cell(bs, "EQUITY", 0).ShouldBe(10_000m);
        Cell(bs, "CURRENT_EARNINGS", 0).ShouldBe(8_800m);
        Cell(bs, "TOTAL_EQUITY", 0).ShouldBe(18_800m);
        Cell(bs, "TOTAL_LIAB_EQUITY", 0).ShouldBe(20_000m);
        bs.GetProperty("rows").EnumerateArray().Select(static r => r.GetProperty("code").GetString()).ShouldNotContain("CHECK");
        Row(bs, "CURRENT_ASSETS").GetProperty("accounts").EnumerateArray().Select(static a => a.GetProperty("code").GetString()).ShouldBe(["1210", "1250"]);
        Cell(bs, "TOTAL_ASSETS", 1).ShouldBe(0m);

        // The trial balance layout equals the inquiry account by account, and its total row balances.
        var tbLayout = await owner.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "TB", from = "2026-09-01", to = "2026-09-30" }, HttpStatusCode.OK);
        Cell(tbLayout, "TOTAL", 0).ShouldBe(0m);
        Cell(tbLayout, "TOTAL", 1).ShouldBe(6_200m);
        Cell(tbLayout, "TOTAL", 2).ShouldBe(6_200m);
        Cell(tbLayout, "TOTAL", 3).ShouldBe(0m);
        var layoutClosing = tbLayout.GetProperty("rows").EnumerateArray().Where(static r => r.GetProperty("rowType").GetString() == "accounts")
            .SelectMany(static r => r.GetProperty("accounts").EnumerateArray()).ToDictionary(static a => a.GetProperty("code").GetString()!, static a => a.GetProperty("values")[3].GetDecimal());
        var inquiryClosing = inquiryRows.ToDictionary(static r => r.GetProperty("accountCode").GetString()!, static r => r.GetProperty("closing").GetDecimal());
        layoutClosing.Count.ShouldBe(inquiryClosing.Count);
        foreach (var (code, closing) in inquiryClosing)
        {
            layoutClosing[code].ShouldBe(closing, code);
        }

        // The designer's checks: a duplicate row code, a formula over an unknown row, a variance over a missing column.
        var plDefinition = layouts[0].GetProperty("definition");
        var rows = plDefinition.GetProperty("rows").EnumerateArray().Select(static r => JsonSerializer.Deserialize<object>(r.GetRawText())!).ToList();
        var columns = plDefinition.GetProperty("columns").EnumerateArray().Select(static c => JsonSerializer.Deserialize<object>(c.GetRawText())!).ToList();
        (await owner.PostErrorAsync("/api/v1/reporting/statements/layouts", new { code = "X1", kind = "pl", name = Name("Bad", "سيئ"), definition = new { rows = rows.Concat([new { code = "NET", rowType = "formula", label = Name("Twice", "مرتين"), formula = "EBT" }]).ToList(), columns } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("layout.row_code_duplicate");
        (await owner.PostErrorAsync("/api/v1/reporting/statements/layouts", new { code = "X1", kind = "pl", name = Name("Bad", "سيئ"), definition = new { rows = rows.Concat([new { code = "RATIO", rowType = "formula", label = Name("Ratio", "نسبة"), formula = "NET / NOPE" }]).ToList(), columns } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("layout.formula_invalid");
        (await owner.PostErrorAsync("/api/v1/reporting/statements/layouts", new { code = "X1", kind = "pl", name = Name("Bad", "سيئ"), definition = new { rows, columns = new object[] { new { code = "PERIOD", columnType = "period", label = Name("Period", "الفترة") }, new { code = "VAR", columnType = "variance", label = Name("Change", "التغير"), parameters = new { of = "PERIOD", against = "NOPE" } } } } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("layout.variance_reference_invalid");
        (await owner.PostErrorAsync("/api/v1/reporting/statements/layouts", new { code = "bad code", kind = "pl", name = Name("Bad", "سيئ"), definition = new { rows, columns } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("layout.code_invalid");

        // A custom profit and loss: the shipped rows plus a margin row, against the prior period with a variance; saved, listed after the shipped ones, run.
        var custom = await owner.PostAsync("/api/v1/reporting/statements/layouts", new
        {
            code = "pl_margin",
            kind = "pl",
            name = Name("Profit and loss with margin", "الأرباح والخسائر مع الهامش"),
            templateCode = "PL",
            companyId,
            definition = new
            {
                rows = rows.Concat([new { code = "MARGIN_PCT", rowType = "formula", label = Name("Net margin %", "هامش الربح الصافي %"), formula = "NET / NET_REVENUE * 100", bold = true }]).ToList(),
                columns = new object[]
                {
                    new { code = "PERIOD", columnType = "period", label = Name("Period", "الفترة") },
                    new { code = "PRIOR", columnType = "prior_period", label = Name("Prior period", "الفترة السابقة") },
                    new { code = "VAR", columnType = "variance", label = Name("Change", "التغير"), parameters = new { of = "PERIOD", against = "PRIOR" } },
                },
            },
        });
        var customId = custom.GetProperty("id").GetGuid();
        custom.GetProperty("code").GetString().ShouldBe("PL_MARGIN");
        custom.GetProperty("isSystem").GetBoolean().ShouldBeFalse();
        custom.GetProperty("templateCode").GetString().ShouldBe("PL");
        (await owner.GetOkAsync($"/api/v1/reporting/statements/layouts?companyId={companyId}")).EnumerateArray().Select(static l => l.GetProperty("code").GetString()).ShouldBe(["PL", "BS", "TB", "PL_MARGIN"]);
        var options = await owner.GetOkAsync($"/api/v1/reporting/statements/layouts/options?companyId={companyId}");
        options.GetProperty("subtypes").EnumerateArray().Select(static s => s.GetString()).ShouldContain("cogs");
        options.GetProperty("categories").EnumerateArray().Select(static c => c.GetProperty("code").GetString()).ShouldContain("current_assets");

        var margin = await owner.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutId = customId, from = "2026-09-01", to = "2026-09-30" }, HttpStatusCode.OK);
        margin.GetProperty("columns")[1].GetProperty("from").GetString().ShouldBe("2026-08-01");
        margin.GetProperty("columns")[1].GetProperty("to").GetString().ShouldBe("2026-08-31");
        Cell(margin, "SALES", 1).ShouldBe(5_000m);
        Cell(margin, "SALES", 2).ShouldBe(0m);
        Cell(margin, "NET", 1).ShouldBe(5_000m);
        Cell(margin, "NET", 2).ShouldBe(-1_200m);
        Cell(margin, "MARGIN_PCT", 0).ShouldBe(76m);
        Cell(margin, "MARGIN_PCT", 1).ShouldBe(100m);

        // Drilling into the sales cell lists the account behind it with the column's window, ready for the ledger.
        var drill = await owner.PostAsync("/api/v1/reporting/statements/drill", new { request = new { companyId, layoutId = customId, from = "2026-09-01", to = "2026-09-30" }, rowCode = "OPEX", columnCode = "PRIOR" }, HttpStatusCode.OK);
        drill.GetProperty("column").GetProperty("from").GetString().ShouldBe("2026-08-01");
        drill.GetProperty("accounts").GetArrayLength().ShouldBe(0);
        drill = await owner.PostAsync("/api/v1/reporting/statements/drill", new { request = new { companyId, layoutId = customId, from = "2026-09-01", to = "2026-09-30" }, rowCode = "SALES", columnCode = "PERIOD" }, HttpStatusCode.OK);
        drill.GetProperty("accounts").EnumerateArray().Single().GetProperty("code").GetString().ShouldBe("4100");
        drill.GetProperty("accounts")[0].GetProperty("values")[0].GetDecimal().ShouldBe(5_000m);
        drill.GetProperty("row").GetProperty("values")[0].GetDecimal().ShouldBe(5_000m);

        // Sliced by the cost centre: only the rent carries it.
        var sliced = await owner.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "PL", from = "2026-09-01", to = "2026-09-30", filters = new { COST_CENTER = hq } }, HttpStatusCode.OK);
        Cell(sliced, "SALES", 0).ShouldBe(0m);
        Cell(sliced, "OPEX", 0).ShouldBe(1_200m);
        Cell(sliced, "NET", 0).ShouldBe(-1_200m);
        (await owner.PostErrorAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "PL", filters = new { NOPE = hq } }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("statement.dimension_unknown");

        // A member who may run reports but not read the journal is refused; one with the journal permission sees the published layouts only.
        var role = await owner.PostAsync("/api/v1/roles", new { code = "analyst", name = Name("Analyst", "محلل"), description = "", grants = new[] { "reporting.report.run" } });
        var email = $"analyst-{ws.Slug}@example.test";
        await owner.PostAsync("/api/v1/users/invite", new { email, displayName = "Analyst", roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, ApiFixture.Json)).ReadJsonAsync();
        using var analyst = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);
        (await analyst.PostErrorAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "PL" }, HttpStatusCode.Forbidden)).Code.ShouldBe("statement.forbidden");
        await owner.PutAsync($"/api/v1/reporting/statements/layouts/{customId}", new { code = "PL_MARGIN", kind = "pl", name = Name("Profit and loss with margin", "الأرباح والخسائر مع الهامش"), companyId, isPublished = false, definition = custom.GetProperty("definition") });
        var reader = await owner.PostAsync("/api/v1/roles", new { code = "reader", name = Name("Reader", "قارئ"), description = "", grants = new[] { "reporting.report.run", "accounting.journal.read" } });
        var membershipId = (await owner.GetOkAsync("/api/v1/users")).EnumerateArray().Single(m => m.GetProperty("email").GetString() == email).GetProperty("membershipId").GetGuid();
        await owner.PostAsync($"/api/v1/users/{membershipId}/assignments", new { roleId = reader.GetProperty("id").GetGuid(), scopes = Array.Empty<object>() });
        (await analyst.GetOkAsync($"/api/v1/reporting/statements/layouts?companyId={companyId}")).EnumerateArray().Select(static l => l.GetProperty("code").GetString()).ShouldBe(["PL", "BS", "TB"]);
        Cell(await analyst.PostAsync("/api/v1/reporting/statements/run", new { companyId, layoutCode = "PL", from = "2026-09-01", to = "2026-09-30" }, HttpStatusCode.OK), "NET", 0).ShouldBe(3_800m);

        // The audit trail keeps the layout's changes; deleted, it is gone.
        (await owner.GetOkAsync($"/api/v1/audit/records/financial_layout/{customId}")).EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ShouldContain("updated");
        await owner.DeleteAsync($"/api/v1/reporting/statements/layouts/{customId}", HttpStatusCode.NoContent);
        (await owner.PostErrorAsync("/api/v1/reporting/statements/run", new { companyId, layoutId = customId }, HttpStatusCode.NotFound)).Code.ShouldBe("financial_layout.not_found");
    }
}
