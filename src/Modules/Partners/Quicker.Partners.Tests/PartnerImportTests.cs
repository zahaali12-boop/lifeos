using System.Net;
using System.Text;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Web.Exports;

namespace Quicker.Partners.Tests;

/// <summary>Customers and suppliers from a spreadsheet (setup wizard, A-173): CSV and Excel alike, company accounts kept, a dry run that keeps nothing, the first wrong row named.</summary>
[Collection(ApiCollection.Name)]
public sealed class PartnerImportTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private static async Task<JsonElement> PostContentAsync(HttpClient client, string path, HttpContent content, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsync(new Uri(path, UriKind.Relative), content);
        var json = await response.ReadJsonAsync();
        response.StatusCode.ShouldBe(expected, json.ToString());
        return json;
    }

    private const string Header = "code,name_en,name_ar,kind,customer,supplier,email,phone,currency,payment_terms,credit_limit,customer_group,supplier_group";

    [Fact]
    public async Task Rows_become_partners_and_their_company_accounts_and_a_dry_run_keeps_nothing()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "IMP", legalName = Name("Import Co", "شركة الاستيراد"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/partners/payment-terms", new { code = "IMP30", name = Name("Net 30", "٣٠ يوماً"), dueDays = 30 });
        await owner.PostAsync("/api/v1/partners/customer-groups", new { code = "WHOLESALE", name = Name("Wholesale", "جملة") });

        var csv = Header + "\n"
            + "cus-1,Alpha Markets,أسواق ألفا,organization,yes,no,alpha@example.test,+9647700000001,IQD,IMP30,5000000,WHOLESALE,\n"
            + "SUP-1,Beta Supplies,بيتا للتوريدات,organization,no,yes,beta@example.test,,USD,IMP30,,,\n"
            + "BOTH-1,Gamma Trading,غاما للتجارة,organization,yes,yes,,,IQD,,,,\n";

        // Checked first: the counts say what would happen and nothing is kept.
        var dry = await PostContentAsync(owner, $"/api/v1/partners/import?companyId={companyId}&dryRun=true", new StringContent(csv, Encoding.UTF8, "text/csv"));
        dry.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        dry.GetProperty("created").GetInt32().ShouldBe(3);
        dry.GetProperty("accounts").GetInt32().ShouldBe(4);
        (await owner.GetOkAsync("/api/v1/partners?q=alpha")).GetProperty("items").GetArrayLength().ShouldBe(0, "a dry run keeps nothing");

        var imported = await PostContentAsync(owner, $"/api/v1/partners/import?companyId={companyId}", new StringContent(csv, Encoding.UTF8, "text/csv"));
        imported.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
        imported.GetProperty("created").GetInt32().ShouldBe(3);
        imported.GetProperty("updated").GetInt32().ShouldBe(0);
        imported.GetProperty("accounts").GetInt32().ShouldBe(4);
        var alpha = await owner.GetOkAsync("/api/v1/partners/by-code/CUS-1");
        alpha.GetProperty("partner").GetProperty("isCustomer").GetBoolean().ShouldBeTrue();
        alpha.GetProperty("partner").GetProperty("isSupplier").GetBoolean().ShouldBeFalse();
        alpha.GetProperty("partner").GetProperty("legalName").GetProperty("ar").GetString().ShouldBe("أسواق ألفا");
        var account = alpha.GetProperty("customerAccounts").EnumerateArray().Single(a => a.GetProperty("companyId").GetGuid() == companyId);
        account.GetProperty("currency").GetString().ShouldBe("IQD");
        account.GetProperty("creditLimit").GetDecimal().ShouldBe(5_000_000m);
        account.GetProperty("paymentTermsId").ValueKind.ShouldBe(JsonValueKind.String);
        account.GetProperty("customerGroupCode").GetString().ShouldBe("WHOLESALE");
        var beta = await owner.GetOkAsync("/api/v1/partners/by-code/SUP-1");
        beta.GetProperty("supplierAccounts").EnumerateArray().Single(a => a.GetProperty("companyId").GetGuid() == companyId).GetProperty("currency").GetString().ShouldBe("USD");

        // The same file again, with a change, is an update, not a duplicate.
        var again = await PostContentAsync(owner, $"/api/v1/partners/import?companyId={companyId}", new StringContent(csv.Replace("Alpha Markets", "Alpha Markets Ltd", StringComparison.Ordinal), Encoding.UTF8, "text/csv"));
        again.GetProperty("created").GetInt32().ShouldBe(0);
        again.GetProperty("updated").GetInt32().ShouldBe(3);
        (await owner.GetOkAsync("/api/v1/partners/by-code/CUS-1")).GetProperty("partner").GetProperty("legalName").GetProperty("en").GetString().ShouldBe("Alpha Markets Ltd");
    }

    [Fact]
    public async Task An_excel_workbook_reads_like_the_csv_and_a_wrong_row_refuses_the_whole_file()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var columns = Header.Split(',').Select(static h => new ExportColumn(h, h == "credit_limit" ? ExportCellType.Number : ExportCellType.Text)).ToList();
        var rows = new List<IReadOnlyList<object?>>
        {
            new object?[] { "XL-1", "Excel Customer", "عميل إكسل", "organization", "yes", "no", null, null, "IQD", null, 250000m, null, null },
            new object?[] { "XL-2", "Excel Supplier", "مورد إكسل", "person", "no", "yes", null, null, null, null, null, null, null },
        };
        var workbook = TabularExport.Xlsx("Partners", columns, rows);
        using var upload = new ByteArrayContent(workbook);
        upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(TabularExport.XlsxContentType);
        var imported = await PostContentAsync(owner, "/api/v1/partners/import", upload);
        imported.GetProperty("created").GetInt32().ShouldBe(2);
        imported.GetProperty("accounts").GetInt32().ShouldBe(0, "no company was named, so no accounts");
        (await owner.GetOkAsync("/api/v1/partners/by-code/XL-2")).GetProperty("partner").GetProperty("kind").GetString().ShouldBe("person");

        // A partner with neither role, a duplicate code, and an unknown payment term each refuse the file as a whole, naming the row.
        var neither = await owner.PostAsync("/api/v1/partners/import", new { }, HttpStatusCode.UnprocessableEntity);
        neither.GetProperty("code").GetString().ShouldBe("import.body_required");
        var noRole = await (await owner.PostAsync(new Uri("/api/v1/partners/import", UriKind.Relative), new StringContent(Header + "\nNR-1,No Role,بلا دور,organization,no,no,,,,,,,\n", Encoding.UTF8, "text/csv"))).ReadJsonAsync();
        noRole.GetProperty("code").GetString().ShouldBe("import.role_required");
        noRole.GetProperty("why").GetProperty("row").GetInt32().ShouldBe(1);
        var duplicate = await (await owner.PostAsync(new Uri("/api/v1/partners/import", UriKind.Relative), new StringContent(Header + "\nD-1,One,واحد,organization,yes,no,,,,,,,\nd-1,Two,اثنان,organization,yes,no,,,,,,,\n", Encoding.UTF8, "text/csv"))).ReadJsonAsync();
        duplicate.GetProperty("code").GetString().ShouldBe("import.duplicate_code");
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "XLC", legalName = Name("Excel Co", "شركة إكسل"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad" });
        var unknownTerms = await (await owner.PostAsync(new Uri($"/api/v1/partners/import?companyId={company.GetProperty("id").GetGuid()}", UriKind.Relative), new StringContent(Header + "\nT-1,Terms,شروط,organization,yes,no,,,IQD,NET99,,,\n", Encoding.UTF8, "text/csv"))).ReadJsonAsync();
        unknownTerms.GetProperty("code").GetString().ShouldBe("import.lookup_unknown");
        (await owner.GetAsync("/api/v1/partners/by-code/D-1")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "the duplicate's first row was rolled back with the file");
    }
}
