using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Time;
using Quicker.Storage;

namespace Quicker.Collaboration.Tests;

/// <summary>Object storage: the filesystem provider with retention, the S3 provider against MinIO when configured, and the object-lock audit anchor store end to end.</summary>
[Collection(ApiCollection.Name)]
public sealed class StorageTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static MemoryStream Bytes(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Filesystem_provider_stores_reads_deletes_and_refuses_to_touch_a_retained_object()
    {
        var root = Path.Combine(host.StorageRoot, "unit-" + Guid.NewGuid().ToString("N")[..8]);
        var storage = new FileSystemObjectStorage(root, Api.Clock);

        using var content = Bytes("hello");
        var info = await storage.PutAsync("tenants/t1/files/a.txt", content, "text/plain");
        info.ShouldBe(new StoredObjectInfo("tenants/t1/files/a.txt", 5, "text/plain", null, null));
        await using (var stored = (await storage.GetAsync("tenants/t1/files/a.txt"))!)
        {
            stored.Info.ContentType.ShouldBe("text/plain");
            (await new StreamReader(stored.Content).ReadToEndAsync()).ShouldBe("hello");
        }

        using var replaced = Bytes("hello again");
        (await storage.PutAsync("tenants/t1/files/a.txt", replaced, "text/plain")).Length.ShouldBe(11);
        (await storage.HeadAsync("tenants/t1/files/a.txt"))!.Length.ShouldBe(11);
        (await storage.DeleteAsync("tenants/t1/files/a.txt")).ShouldBeTrue();
        (await storage.DeleteAsync("tenants/t1/files/a.txt")).ShouldBeFalse();
        (await storage.HeadAsync("tenants/t1/files/a.txt")).ShouldBeNull();

        // Keys are validated: no escaping the root, no immutable object without a retention.
        using var stray = Bytes("x");
        await Should.ThrowAsync<ArgumentException>(() => storage.PutAsync("../outside", stray, "text/plain"));
        await Should.ThrowAsync<ArgumentException>(() => storage.PutAsync("immutable/anchor.json", stray, "application/json"));
        (await storage.HeadAsync("tenants/../x")).ShouldBeNull();

        // A retained object cannot be replaced or deleted until its date; afterwards it can.
        using var locked = Bytes("{\"seq\":1}");
        var until = Api.Clock.UtcNow.AddDays(1);
        (await storage.PutAsync("immutable/anchors/1.json", locked, "application/json", new ObjectRetention(until))).RetainUntil.ShouldBe(until);
        using var tamper = Bytes("{\"seq\":2}");
        (await Should.ThrowAsync<ObjectRetainedException>(() => storage.PutAsync("immutable/anchors/1.json", tamper, "application/json", new ObjectRetention(until)))).RetainUntil.ShouldBe(until);
        await Should.ThrowAsync<ObjectRetainedException>(() => storage.DeleteAsync("immutable/anchors/1.json"));
        var later = new FileSystemObjectStorage(root, new FakeClock(until.AddSeconds(1)));
        (await later.DeleteAsync("immutable/anchors/1.json")).ShouldBeTrue();
    }

    [Fact]
    public async Task Object_lock_anchor_store_writes_retained_anchors_that_verification_reads_back()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        using var form = new MultipartFormDataContent { { new StringContent("sales_invoice"), "entityType" }, { new StringContent(Guid.NewGuid().ToString()), "entityId" } };
        var file = new ByteArrayContent([1, 2, 3]);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", "note.bin");
        (await owner.PostAsync("/api/v1/collaboration/attachments", form)).EnsureSuccessStatusCode();

        var anchor = await (await owner.PostAsync("/api/v1/audit/chain/anchor", null)).ReadJsonAsync();
        anchor.GetProperty("store").GetString().ShouldBe("object_lock");
        var reference = anchor.GetProperty("reference").GetString()!;
        reference.ShouldStartWith($"immutable/audit-anchors/tenant/{ws.TenantId:N}/");
        anchor.GetProperty("receipt").GetString()!.Length.ShouldBe(64);
        var verified = await (await owner.PostAsync("/api/v1/audit/chain/verify", null)).ReadJsonAsync();
        verified.GetProperty("status").GetString().ShouldBe("ok");
        verified.GetProperty("anchorMatched").GetBoolean().ShouldBeTrue();

        // The stored anchor is retained: the storage refuses to replace or delete it.
        var storage = Api.Services.GetRequiredService<IObjectStorage>();
        (await storage.HeadAsync(reference))!.RetainUntil.ShouldBe(Api.Clock.UtcNow.AddDays(3650));
        using var forged = Bytes("{}");
        await Should.ThrowAsync<ObjectRetainedException>(() => storage.PutAsync(reference, forged, "application/json", new ObjectRetention(Api.Clock.UtcNow.AddDays(1))));
        await Should.ThrowAsync<ObjectRetainedException>(() => storage.DeleteAsync(reference));

        // Someone edits the bytes on disk behind the storage's back: verification no longer matches the anchor.
        var path = Path.Combine(host.StorageRoot, reference.Replace('/', Path.DirectorySeparatorChar));
        var original = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, original.Replace(anchor.GetProperty("headHash").GetString()!, new string('0', 64), StringComparison.Ordinal));
        (await (await owner.PostAsync("/api/v1/audit/chain/verify", null)).ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("anchor_mismatch");
        await File.WriteAllTextAsync(path, original);
        (await (await owner.PostAsync("/api/v1/audit/chain/verify", null)).ReadJsonAsync()).GetProperty("status").GetString().ShouldBe("ok");
    }

    /// <summary>Runs only where an S3-compatible endpoint is provided (CI starts MinIO; set QUICKER_TEST_S3_ENDPOINT locally).</summary>
    [Fact]
    public async Task S3_provider_versions_immutable_objects_and_honours_object_lock()
    {
        var endpoint = Environment.GetEnvironmentVariable("QUICKER_TEST_S3_ENDPOINT");
        Assert.SkipWhen(string.IsNullOrEmpty(endpoint), "QUICKER_TEST_S3_ENDPOINT is not set");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var options = new StorageOptions
        {
            Provider = "s3",
            Endpoint = endpoint,
            AccessKey = Environment.GetEnvironmentVariable("QUICKER_TEST_S3_ACCESS_KEY") ?? "minioadmin",
            SecretKey = Environment.GetEnvironmentVariable("QUICKER_TEST_S3_SECRET_KEY") ?? "minioadmin",
            Bucket = "quicker-test-" + suffix,
            ImmutableBucket = "quicker-test-immutable-" + suffix,
        };
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var storage = new S3ObjectStorage(StorageRegistration.CreateS3Client(options), options, clock);

        using var content = Bytes("hello");
        var info = await storage.PutAsync("tenants/t1/files/a.txt", content, "text/plain");
        info.Length.ShouldBe(5);
        await using (var stored = (await storage.GetAsync("tenants/t1/files/a.txt"))!)
        {
            stored.Info.ContentType.ShouldBe("text/plain");
            (await new StreamReader(stored.Content).ReadToEndAsync()).ShouldBe("hello");
        }

        (await storage.DeleteAsync("tenants/t1/files/a.txt")).ShouldBeTrue();
        (await storage.HeadAsync("tenants/t1/files/a.txt")).ShouldBeNull();

        // Immutable: every write is a retained version; the first version stays readable by id after a second write.
        var until = clock.UtcNow.AddMinutes(2);
        using var first = Bytes("{\"seq\":1}");
        var v1 = await storage.PutAsync("immutable/anchors/1.json", first, "application/json", new ObjectRetention(until));
        v1.VersionId.ShouldNotBeNull();
        using var second = Bytes("{\"seq\":2}");
        var v2 = await storage.PutAsync("immutable/anchors/1.json", second, "application/json", new ObjectRetention(until));
        v2.VersionId.ShouldNotBe(v1.VersionId);
        await using (var pinned = (await storage.GetAsync("immutable/anchors/1.json", v1.VersionId))!)
        {
            (await new StreamReader(pinned.Content).ReadToEndAsync()).ShouldBe("{\"seq\":1}");
        }

        (await storage.HeadAsync("immutable/anchors/1.json"))!.RetainUntil.ShouldNotBeNull();
        await Should.ThrowAsync<ObjectRetainedException>(() => storage.DeleteAsync("immutable/anchors/1.json"));
    }
}
