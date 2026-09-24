using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Npgsql;
using Quicker.Identity.TestSupport;

namespace Quicker.Integration.Tests;

/// <summary>A list's rows written as CSV or XLSX by the server: typed cells, formulas kept inert, rules and the audit trail.</summary>
[Collection(ApiCollection.Name)]
public sealed class ExportTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Table(string format, string? documentType = null, object[][]? rows = null) => new
    {
        format,
        name = "Purchase orders",
        documentType,
        rightToLeft = true,
        columns = new object[] { new { header = "Number" }, new { header = "Supplier" }, new { header = "Total", type = "number" }, new { header = "Date", type = "date" } },
        rows = rows ?? [["PO-2026-00001", "=HYPERLINK(\"http://x\")", 1234.5m, "2026-09-30"], ["PO-2026-00002", "شركة دجلة", "-17.25", "2026-10-01"], ["PO-2026-00003", null!, null!, null!]],
    };

    [Fact]
    public async Task A_list_exports_as_csv_with_formulas_kept_inert_and_as_xlsx_with_typed_cells_and_is_audited()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        var csv = await owner.PostAsJsonAsync("/api/v1/exports/table", Table("csv", "purchase_order"), Json);
        csv.StatusCode.ShouldBe(HttpStatusCode.OK, await csv.Content.ReadAsStringAsync());
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        csv.Content.Headers.ContentDisposition!.FileNameStar.ShouldStartWith("Purchase-orders-");
        var bytes = await csv.Content.ReadAsByteArrayAsync();
        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        Encoding.UTF8.GetString(bytes[3..]).Split("\r\n").ShouldBe(
        [
            "Number,Supplier,Total,Date",
            "PO-2026-00001,\"'=HYPERLINK(\"\"http://x\"\")\",1234.5,2026-09-30",
            "PO-2026-00002,شركة دجلة,-17.25,2026-10-01",
            "PO-2026-00003,,,",
            string.Empty,
        ]);

        var xlsx = await owner.PostAsJsonAsync("/api/v1/exports/table", Table("xlsx", "purchase_order"), Json);
        xlsx.StatusCode.ShouldBe(HttpStatusCode.OK);
        xlsx.Content.Headers.ContentType!.MediaType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        using var zip = new ZipArchive(new MemoryStream(await xlsx.Content.ReadAsByteArrayAsync()));
        using var reader = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var sheet = await reader.ReadToEndAsync();
        sheet.ShouldContain("rightToLeft=\"1\"");
        sheet.ShouldContain("<c r=\"C2\" s=\"1\"><v>1234.5</v></c>");
        sheet.ShouldContain("<c r=\"C3\" s=\"1\"><v>-17.25</v></c>");
        var serial = new DateOnly(2026, 9, 30).DayNumber - new DateOnly(1899, 12, 30).DayNumber;
        sheet.ShouldContain($"<c r=\"D2\" s=\"2\"><v>{serial}</v></c>");
        sheet.ShouldContain("<t xml:space=\"preserve\">=HYPERLINK(&quot;http://x&quot;)</t>", customMessage: "an XLSX text cell is never a formula");

        // Each export is in the audit trail with its format, size and columns.
        await using var db = new NpgsqlConnection(Api.Db.OwnerConnectionString);
        var audited = (await db.QueryAsync<(string EntityType, string Action, string Details)>(
            "SELECT entity_type, action, details::text FROM app.aud_events WHERE tenant_id = @t AND action = 'exported' ORDER BY seq", new { t = ws.TenantId })).ToList();
        audited.Count.ShouldBe(2);
        audited.ShouldAllBe(static a => a.EntityType == "purchase_order");
        audited[0].Details.ShouldContain("\"rows\": 3");
        audited[1].Details.ShouldContain("\"format\": \"xlsx\"");
    }

    [Fact]
    public async Task Exports_are_validated_and_refused_to_a_role_that_may_not_export_the_document_type()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        (await (await owner.PostAsJsonAsync("/api/v1/exports/table", Table("pdf"), Json)).ErrorCodeAsync()).ShouldBe("export.format_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/exports/table", Table("csv", rows: [["only one"]]), Json)).ErrorCodeAsync()).ShouldBe("export.row_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/exports/table", new { format = "csv", columns = Array.Empty<object>(), rows = Array.Empty<object>() }, Json)).ErrorCodeAsync()).ShouldBe("export.columns_invalid");
        (await Api.Client.PostAsJsonAsync("/api/v1/exports/table", Table("csv"), Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // A buyer whose role reads orders but may not export them.
        var role = await (await owner.PostAsJsonAsync("/api/v1/roles", new
        {
            code = "buyer_no_export",
            name = new { en = "Buyer", ar = "مشترٍ" },
            description = "",
            grants = new[] { "purchasing.order.read" },
            documentTypeRules = new[] { new { documentType = "purchase_order", action = "export", allowed = false } },
        }, Json)).ReadJsonAsync();
        var email = $"buyer-{ws.Slug}@example.test";
        (await owner.PostAsJsonAsync("/api/v1/users/invite", new { email, displayName = "Buyer", roleIds = new[] { role.GetProperty("id").GetGuid() } }, Json)).EnsureSuccessStatusCode();
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        using var buyer = Api.ClientFor(accepted.GetProperty("accessToken").GetString()!);

        var refused = await buyer.PostAsJsonAsync("/api/v1/exports/table", Table("csv", "purchase_order"), Json);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await refused.ErrorCodeAsync()).ShouldBe("export.not_allowed");
        (await buyer.PostAsJsonAsync("/api/v1/exports/table", Table("csv", "purchase_invoice"), Json)).StatusCode.ShouldBe(HttpStatusCode.OK, "no rule on invoices");
    }
}
