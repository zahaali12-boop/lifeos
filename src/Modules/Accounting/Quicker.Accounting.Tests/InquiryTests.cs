using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Accounting.Tests;

/// <summary>
/// Roadmap 2.5 through the API: the trial balance balances at three sampled dates and every figure drills to the
/// ledger lines behind it; movement windows, comparatives, dimension filters and grouping; the account ledger with
/// its running balance, paging and source document; balances by dimension; the journal browser; CSV and XLSX exports.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InquiryTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static object Line(string account, decimal debit = 0m, decimal credit = 0m, object? dimensions = null, string? subledgerType = null, Guid? subledgerRef = null) =>
        new { accountCode = account, debit, credit, dimensions, subledgerType, subledgerRef };

    private static async Task PostJournalAsync(HttpClient owner, Guid companyId, string date, string description, object[] lines, string kind = "manual")
    {
        var draft = await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/journals", new { kind, postingDate = date, currency = "IQD", description = new { en = description, ar = description }, lines });
        (await owner.PostAsync($"/api/v1/accounting/journals/{draft.GetProperty("id").GetGuid()}/post", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("posted");
    }

    private static List<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().ToList();

    private static JsonElement Row(JsonElement report, string code) => Rows(report).Single(r => r.GetProperty("accountCode").GetString() == code && r.GetProperty("dimensionValueCode").ValueKind == JsonValueKind.Null);

    [Fact]
    public async Task The_trial_balance_balances_at_any_date_and_every_figure_drills_to_its_lines()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var companyId = await owner.CompanyAsync("INQ", "IQD");
        await owner.ChartFromTemplateAsync("IFRS_SME", "CH-INQ", companyId);
        await owner.PostAsync($"/api/v1/accounting/companies/{companyId}/posting-profiles/from-chart", new { });
        var (_, cc1) = await owner.DimensionValueAsync("COST_CENTER", "CC-1");
        var (_, cc2) = await owner.DimensionValueAsync("COST_CENTER", "CC-2");
        var reports = $"/api/v1/accounting/companies/{companyId}/reports";

        await PostJournalAsync(owner, companyId, "2026-01-15", "Go-live balances", [Line("1111", debit: 5000000m, subledgerType: "BANK", subledgerRef: Guid.NewGuid()), Line("2510", credit: 3000000m)], kind: "opening");
        await PostJournalAsync(owner, companyId, "2026-09-05", "Rent September", [Line("6110", debit: 1500000m, dimensions: new { COST_CENTER = cc1 }), Line("2170", credit: 1500000m, dimensions: new { COST_CENTER = cc1 })]);
        await PostJournalAsync(owner, companyId, "2026-10-12", "Salaries October", [Line("6100", debit: 2000000m, dimensions: new { COST_CENTER = cc2 }), Line("2190", credit: 2000000m, dimensions: new { COST_CENTER = cc2 })]);
        await PostJournalAsync(owner, companyId, "2026-11-20", "Cash sale", [Line("1210", debit: 900000m, subledgerType: "AR", subledgerRef: Guid.NewGuid()), Line("4100", credit: 900000m)]);
        await PostJournalAsync(owner, companyId, "2026-12-05", "Rent December", [Line("6110", debit: 1500000m, dimensions: new { COST_CENTER = cc1 }), Line("2170", credit: 1500000m, dimensions: new { COST_CENTER = cc1 })]);

        // Three sampled dates: the books balance and the figures move as the journals land.
        foreach (var (date, rent, salaries, rows) in new[] { ("2026-09-30", 1500000m, 0m, 5), ("2026-10-31", 1500000m, 2000000m, 7), ("2026-12-31", 3000000m, 2000000m, 9) })
        {
            var tb = await owner.GetOkAsync($"{reports}/trial-balance?asOf={date}");
            tb.GetProperty("balanced").GetBoolean().ShouldBeTrue(date);
            tb.GetProperty("currency").GetString().ShouldBe("IQD");
            tb.GetProperty("totals").GetProperty("closing").GetDecimal().ShouldBe(0m, date);
            tb.GetProperty("totals").GetProperty("debit").GetDecimal().ShouldBe(tb.GetProperty("totals").GetProperty("credit").GetDecimal(), date);
            Rows(tb).Count.ShouldBe(rows, date);
            Row(tb, "6110").GetProperty("closing").GetDecimal().ShouldBe(rent, date);
            if (salaries > 0m)
            {
                Row(tb, "6100").GetProperty("closing").GetDecimal().ShouldBe(salaries, date);
            }

            Row(tb, "3400").GetProperty("closing").GetDecimal().ShouldBe(-2000000m, "opening balance equity balanced the go-live journal");

            // Every figure drills to lines: the ledger over the row's drill parameters closes on the row's closing balance.
            foreach (var row in Rows(tb))
            {
                var drill = row.GetProperty("drill");
                var ledger = await owner.GetOkAsync($"{reports}/ledger?accountId={drill.GetProperty("accountId").GetGuid()}&to={drill.GetProperty("to").GetString()}");
                ledger.GetProperty("closing").GetDecimal().ShouldBe(row.GetProperty("closing").GetDecimal(), row.GetProperty("accountCode").GetString());
                ledger.GetProperty("items").EnumerateArray().Last().GetProperty("balance").GetDecimal().ShouldBe(row.GetProperty("closing").GetDecimal());
            }
        }

        // A movement window: opening before it, movements inside, closing after.
        var q4 = await owner.GetOkAsync($"{reports}/trial-balance?asOf=2026-12-31&from=2026-10-01");
        q4.GetProperty("balanced").GetBoolean().ShouldBeTrue();
        var rentRow = Row(q4, "6110");
        rentRow.GetProperty("opening").GetDecimal().ShouldBe(1500000m);
        rentRow.GetProperty("debit").GetDecimal().ShouldBe(1500000m);
        rentRow.GetProperty("closing").GetDecimal().ShouldBe(3000000m);
        Row(q4, "6100").GetProperty("opening").GetDecimal().ShouldBe(0m);
        q4.GetProperty("totals").GetProperty("opening").GetDecimal().ShouldBe(0m);

        // A comparative: the same window one quarter earlier, on every row and in the totals.
        var compared = await owner.GetOkAsync($"{reports}/trial-balance?asOf=2026-12-31&from=2026-10-01&compareAsOf=2026-09-30");
        compared.GetProperty("compareFrom").GetString().ShouldBe("2026-07-01", "the same number of days before the comparative date");
        Row(compared, "6110").GetProperty("compare").GetProperty("closing").GetDecimal().ShouldBe(1500000m);
        Row(compared, "6100").GetProperty("compare").GetProperty("closing").GetDecimal().ShouldBe(0m);
        compared.GetProperty("compareTotals").GetProperty("closing").GetDecimal().ShouldBe(0m);

        // Dimension filters slice; grouping shows each value; both name their drill.
        var sliced = await owner.GetOkAsync($"{reports}/trial-balance?asOf=2026-12-31&d.COST_CENTER={cc1}");
        Rows(sliced).Select(static r => r.GetProperty("accountCode").GetString()).ShouldBe(["2170", "6110"]);
        Row(sliced, "6110").GetProperty("closing").GetDecimal().ShouldBe(3000000m);
        Row(sliced, "6110").GetProperty("drill").GetProperty("dimensions").GetProperty("COST_CENTER").GetGuid().ShouldBe(cc1);
        var grouped = await owner.GetOkAsync($"{reports}/trial-balance?asOf=2026-12-31&groupBy=COST_CENTER");
        grouped.GetProperty("balanced").GetBoolean().ShouldBeTrue();
        var groupedRent = Rows(grouped).Single(r => r.GetProperty("accountCode").GetString() == "6110");
        groupedRent.GetProperty("dimensionValueCode").GetString().ShouldBe("CC-1");
        groupedRent.GetProperty("dimensionValueName").GetProperty("en").GetString().ShouldBe("CC-1");
        Rows(grouped).Single(r => r.GetProperty("accountCode").GetString() == "1210").GetProperty("dimensionValueCode").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetOkAsync($"{reports}/ledger?accountCode=6110&to=2026-12-31&d.COST_CENTER={cc2}")).GetProperty("closing").GetDecimal().ShouldBe(0m, "rent never carried CC-2");
        (await owner.GetErrorAsync($"{reports}/trial-balance?asOf=2026-12-31&groupBy=NOPE", HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("inquiry.dimension_unknown");
        (await owner.GetErrorAsync($"{reports}/trial-balance?asOf=2026-12-31&basis=rc", HttpStatusCode.Conflict)).Code.ShouldBe("inquiry.basis_unavailable");

        // Balances by dimension: expenses per cost centre, the unassigned line separate.
        var byCentre = await owner.GetOkAsync($"{reports}/dimension-balances?dimension=COST_CENTER&asOf=2026-12-31&accountType=expense");
        var centres = byCentre.GetProperty("rows").EnumerateArray().ToList();
        centres.Single(r => r.GetProperty("valueCode").GetString() == "CC-1").GetProperty("closing").GetDecimal().ShouldBe(3000000m);
        centres.Single(r => r.GetProperty("valueCode").GetString() == "CC-2").GetProperty("closing").GetDecimal().ShouldBe(2000000m);
        byCentre.GetProperty("totals").GetProperty("closing").GetDecimal().ShouldBe(5000000m);
        var allByCentre = await owner.GetOkAsync($"{reports}/dimension-balances?dimension=COST_CENTER&asOf=2026-12-31");
        allByCentre.GetProperty("rows").EnumerateArray().Single(static r => r.GetProperty("valueCode").ValueKind == JsonValueKind.Null).GetProperty("closing").GetDecimal().ShouldBe(0m, "the unassigned lines balance among themselves");

        // The account ledger: opening, running balance, the source document of each line, and paging that keeps the balance running.
        var ledgerPage = await owner.GetOkAsync($"{reports}/ledger?accountCode=6110&from=2026-10-01&to=2026-12-31&limit=1");
        ledgerPage.GetProperty("opening").GetDecimal().ShouldBe(1500000m);
        ledgerPage.GetProperty("closing").GetDecimal().ShouldBe(3000000m);
        ledgerPage.GetProperty("items").GetArrayLength().ShouldBe(1);
        var first = ledgerPage.GetProperty("items")[0];
        first.GetProperty("postingDate").GetString().ShouldBe("2026-12-05");
        first.GetProperty("balance").GetDecimal().ShouldBe(3000000m);
        first.GetProperty("sourceDocumentType").GetString().ShouldBe("manual_journal");
        first.GetProperty("sourceLink").GetString().ShouldStartWith("/api/v1/accounting/journals/");
        first.GetProperty("dimensions").GetProperty("COST_CENTER").GetGuid().ShouldBe(cc1);
        (await owner.GetOkAsync(first.GetProperty("sourceLink").GetString()!)).GetProperty("number").GetString().ShouldBe(first.GetProperty("sourceDocumentNumber").GetString());
        ledgerPage.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        var fullLedger = await owner.GetOkAsync($"{reports}/ledger?accountCode=6110&to=2026-12-31&limit=1");
        fullLedger.GetProperty("items")[0].GetProperty("balance").GetDecimal().ShouldBe(1500000m);
        var secondPage = await owner.GetOkAsync($"{reports}/ledger?accountCode=6110&to=2026-12-31&limit=1&cursor={fullLedger.GetProperty("nextCursor").GetString()}");
        secondPage.GetProperty("items")[0].GetProperty("balance").GetDecimal().ShouldBe(3000000m, "the balance keeps running across pages");
        secondPage.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        (await owner.GetErrorAsync($"{reports}/ledger?to=2026-12-31", HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("inquiry.account_required");
        (await owner.GetErrorAsync($"{reports}/ledger?accountCode=9999&to=2026-12-31", HttpStatusCode.NotFound)).Code.ShouldBe("account.not_found");

        // The journal browser: by number prefix, by account, by text.
        var entries = $"/api/v1/accounting/companies/{companyId}/journal-entries";
        (await owner.GetOkAsync($"{entries}?number=JE-2026-000002")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await owner.GetOkAsync($"{entries}?number=MJ-2026")).GetProperty("items").GetArrayLength().ShouldBe(5, "source document numbers match too");
        var salariesAccount = Row(q4, "6100").GetProperty("accountId").GetGuid();
        (await owner.GetOkAsync($"{entries}?accountId={salariesAccount}")).GetProperty("items").GetArrayLength().ShouldBe(1);
        (await owner.GetOkAsync($"{entries}?text=rent")).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await owner.GetOkAsync($"{entries}?isManual=true&minAmount=2000000")).GetProperty("items").GetArrayLength().ShouldBe(2, "the opening journal and the salaries");

        // Exports: CSV with the totals line, XLSX with real number cells.
        using var csv = await owner.GetAsync($"{reports}/trial-balance?asOf=2026-12-31&format=csv");
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        csv.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("trial-balance-2026-12-31.csv");
        var csvLines = (await csv.Content.ReadAsStringAsync()).TrimEnd().Split("\r\n");
        csvLines[0].ShouldBe("account_code,account_name_en,account_name_ar,account_type,opening,debit,credit,closing");
        csvLines.Length.ShouldBe(1 + 9 + 1);
        csvLines[^1].ShouldStartWith("TOTAL,,,,0,");
        csvLines.Single(static l => l.StartsWith("6110,", StringComparison.Ordinal)).ShouldEndWith(",0,3000000,0,3000000");

        using var xlsxRequest = new HttpRequestMessage(HttpMethod.Get, $"{reports}/ledger?accountCode=6110&to=2026-12-31&format=xlsx");
        xlsxRequest.Headers.AcceptLanguage.ParseAdd("ar");
        using var xlsx = await owner.SendAsync(xlsxRequest);
        xlsx.StatusCode.ShouldBe(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        zip.Entries.Select(static e => e.FullName).ShouldContain("xl/worksheets/sheet1.xml");
        using var sheetReader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(), Encoding.UTF8);
        var sheet = await sheetReader.ReadToEndAsync();
        sheet.ShouldContain("rightToLeft=\"1\"");
        sheet.ShouldContain("<t xml:space=\"preserve\">posting_date</t>");
        sheet.Contains("<c r=\"K3\" s=\"1\"><v>1500000</v></c>", StringComparison.Ordinal).ShouldBeTrue("the running balance is a number cell: " + sheet);
        sheet.Contains("s=\"2\"><v>46361</v>", StringComparison.Ordinal).ShouldBeTrue("2026-12-05 as a date cell");
        (await owner.GetErrorAsync($"{reports}/trial-balance?asOf=2026-12-31&format=pdf", HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("export.format_invalid");
        var exported = (await owner.GetOkAsync($"/api/v1/audit/records/report/{companyId}")).EnumerateArray().ToList();
        exported.Count.ShouldBe(2);
        exported.ShouldAllBe(static e => e.GetProperty("action").GetString() == "exported");

        await owner.AssertInvariantsAsync();
    }
}
