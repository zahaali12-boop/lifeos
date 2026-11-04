using System.Net;
using System.Net.Http.Headers;
using Quicker.Identity.TestSupport;
using Quicker.Items.Application;
using Quicker.Web.Exports;

namespace Quicker.Items.Tests;

/// <summary>The item import from an Excel workbook (setup wizard, A-173): the export's columns read back from .xlsx, numbers and booleans as typed, and a dry run that keeps nothing.</summary>
[Collection(ApiCollection.Name)]
public sealed class ItemImportXlsxTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public async Task A_workbook_with_the_export_columns_imports_and_a_dry_run_is_checked_but_not_kept()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var columns = ItemCsv.Columns.Select(static c => new ExportColumn(c, c is "list_price" or "shelf_life_days" or "weight_kg" ? ExportCellType.Number : ExportCellType.Text)).ToList();
        object?[] Row(string code, string en, string ar, decimal price) =>
        [
            code, en, ar, null, null, "stock", null, null, "PCS", null, "CTN", "CTN:12/1", null, "none", "false", null, "false", price, "IQD", 0.5m, null, null, null, "true",
        ];
        var workbook = TabularExport.Xlsx("Items", columns, [Row("XL-TEA", "Tea 500g", "شاي ٥٠٠غ", 2500m), Row("XL-RICE", "Rice 5kg", "رز ٥ كغ", 12000m)]);

        using var dryUpload = new ByteArrayContent(workbook);
        dryUpload.Headers.ContentType = new MediaTypeHeaderValue(TabularExport.XlsxContentType);
        var dry = await owner.PostContentAsync("/api/v1/items/import?dryRun=true", dryUpload);
        dry.GetProperty("dryRun").GetBoolean().ShouldBeTrue();
        dry.GetProperty("created").GetInt32().ShouldBe(2);
        (await owner.GetAsync("/api/v1/items/by-code/XL-TEA")).StatusCode.ShouldBe(HttpStatusCode.NotFound, "a dry run keeps nothing");

        using var upload = new ByteArrayContent(workbook);
        upload.Headers.ContentType = new MediaTypeHeaderValue(TabularExport.XlsxContentType);
        var imported = await owner.PostContentAsync("/api/v1/items/import", upload);
        imported.GetProperty("dryRun").GetBoolean().ShouldBeFalse();
        imported.GetProperty("created").GetInt32().ShouldBe(2);
        var tea = await owner.GetOkAsync("/api/v1/items/by-code/XL-TEA");
        tea.GetProperty("name").GetProperty("ar").GetString().ShouldBe("شاي ٥٠٠غ");
        tea.GetProperty("listPrice").GetDecimal().ShouldBe(2500m);
        tea.GetProperty("weightKg").GetDecimal().ShouldBe(0.5m);
        tea.GetProperty("purchaseUom").GetString().ShouldBe("CTN");
        tea.GetProperty("uoms").GetArrayLength().ShouldBe(2);

        // Something that is not a workbook is refused as a file, not as a row.
        using var broken = new ByteArrayContent([1, 2, 3, 4]);
        broken.Headers.ContentType = new MediaTypeHeaderValue(TabularExport.XlsxContentType);
        var refused = await owner.PostContentAsync("/api/v1/items/import", broken, HttpStatusCode.UnprocessableEntity);
        refused.GetProperty("code").GetString().ShouldBe("import.file_invalid");
    }
}
