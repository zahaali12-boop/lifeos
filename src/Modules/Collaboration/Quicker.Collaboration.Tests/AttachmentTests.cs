using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Application;
using Quicker.Identity.TestSupport;
using Quicker.Storage;

namespace Quicker.Collaboration.Tests;

/// <summary>Attachments: upload with hash and size limit, listing per record, download, delete removes the object, isolation, permissions, audit.</summary>
[Collection(ApiCollection.Name)]
public sealed class AttachmentTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static MultipartFormDataContent Form(byte[] bytes, string fileName, string contentType, string entityType, Guid entityId)
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        return new MultipartFormDataContent
        {
            { new StringContent(entityType), "entityType" },
            { new StringContent(entityId.ToString()), "entityId" },
            { file, "file", fileName },
        };
    }

    [Fact]
    public async Task Files_are_stored_hashed_listed_per_record_downloaded_and_removed_with_their_object()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var invoiceId = Guid.NewGuid();
        var bytes = new byte[100_000];
        Random.Shared.NextBytes(bytes);

        using var form = Form(bytes, "invoice-1001.pdf", "application/pdf; charset=binary", "sales_invoice", invoiceId);
        var uploaded = await owner.PostAsync("/api/v1/collaboration/attachments", form);
        var json = await uploaded.ReadJsonAsync();
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created, json.ToString());
        var id = json.GetProperty("id").GetGuid();
        json.GetProperty("fileName").GetString().ShouldBe("invoice-1001.pdf");
        json.GetProperty("contentType").GetString().ShouldBe("application/pdf");
        json.GetProperty("sizeBytes").GetInt64().ShouldBe(bytes.Length);
        json.GetProperty("sha256").GetString().ShouldBe(Convert.ToHexStringLower(SHA256.HashData(bytes)));
        json.GetProperty("uploadedBy").GetGuid().ShouldBe(ws.MembershipId);

        var list = (await (await owner.GetAsync($"/api/v1/collaboration/attachments?entityType=sales_invoice&entityId={invoiceId}")).ReadJsonAsync()).EnumerateArray().ToList();
        list.ShouldHaveSingleItem().GetProperty("id").GetGuid().ShouldBe(id);
        (await (await owner.GetAsync($"/api/v1/collaboration/attachments?entityType=sales_invoice&entityId={Guid.NewGuid()}")).ReadJsonAsync()).GetArrayLength().ShouldBe(0);

        var download = await owner.GetAsync($"/api/v1/collaboration/attachments/{id}/content");
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        download.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        download.Content.Headers.ContentDisposition!.FileName!.Trim('"').ShouldBe("invoice-1001.pdf");
        (await download.Content.ReadAsByteArrayAsync()).ShouldBe(bytes);

        // Limits and validation.
        using var big = Form(new byte[1_048_577], "big.bin", "application/octet-stream", "sales_invoice", invoiceId);
        (await (await owner.PostAsync("/api/v1/collaboration/attachments", big)).ErrorCodeAsync()).ShouldBe("attachment.too_large");
        using var badType = Form([1, 2, 3], "x.bin", "application/octet-stream", "Sales Invoice", invoiceId);
        (await (await owner.PostAsync("/api/v1/collaboration/attachments", badType)).ErrorCodeAsync()).ShouldBe("attachment.entity_invalid");
        (await (await owner.PostAsJsonAsync("/api/v1/collaboration/attachments", new { entityType = "sales_invoice" }, Json)).ErrorCodeAsync()).ShouldBe("attachment.form_required");

        // Another tenant sees nothing; a reader without the manage permission cannot upload or delete.
        var other = await Api.SignupAsync();
        using var outsider = Api.ClientFor(other.AccessToken);
        (await outsider.GetAsync($"/api/v1/collaboration/attachments/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.GetAsync($"/api/v1/collaboration/attachments/{id}/content")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await outsider.DeleteAsync($"/api/v1/collaboration/attachments/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var (readerToken, _, _) = await host.InviteAsync(ws, "reader", "collaboration.attachment.read");
        using var reader = Api.ClientFor(readerToken);
        (await reader.GetAsync($"/api/v1/collaboration/attachments/{id}")).StatusCode.ShouldBe(HttpStatusCode.OK);
        using var readerForm = Form([1], "x.bin", "application/octet-stream", "sales_invoice", invoiceId);
        (await reader.PostAsync("/api/v1/collaboration/attachments", readerForm)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await reader.DeleteAsync($"/api/v1/collaboration/attachments/{id}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Delete: the row and the object go together; the audit trail keeps both changes.
        var storage = Api.Services.GetRequiredService<IObjectStorage>();
        var key = AttachmentService.KeyFor(ws.TenantId, id);
        (await storage.HeadAsync(key))!.Length.ShouldBe(bytes.Length);
        (await owner.DeleteAsync($"/api/v1/collaboration/attachments/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetAsync($"/api/v1/collaboration/attachments/{id}/content")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await storage.HeadAsync(key)).ShouldBeNull();
        var trail = (await (await owner.GetAsync($"/api/v1/audit/records/attachment/{id}")).ReadJsonAsync()).EnumerateArray().Select(static e => e.GetProperty("action").GetString()).ToList();
        trail.ShouldBe(["created", "deleted"]);
    }
}
