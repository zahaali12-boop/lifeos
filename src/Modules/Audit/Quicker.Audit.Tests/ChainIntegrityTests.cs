using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Audit.Application;
using Quicker.Audit.TestSupport;
using Quicker.Identity.TestSupport;

namespace Quicker.Audit.Tests;

/// <summary>ADR-0015: the chain, its anchors and the verifier. Tampering with a stored row or the anchor file is detected.</summary>
[Collection(ApiCollection.Name)]
public sealed class ChainIntegrityTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static async Task<JsonElement> VerifyAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/v1/audit/chain/verify", null);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await response.ReadJsonAsync();
    }

    [Fact]
    public async Task Chain_verifies_clean_anchors_once_per_head_and_detects_row_file_and_tail_tampering()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        foreach (var code in new[] { "a", "b" })
        {
            (await owner.PostAsJsonAsync("/api/v1/roles", new { code, name = new { en = code }, description = "", grants = new[] { "identity.user.read" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);
        }

        var clean = await VerifyAsync(owner);
        clean.GetProperty("status").GetString().ShouldBe("ok");
        var length = clean.GetProperty("toSeq").GetInt64();
        length.ShouldBeGreaterThan(3);
        clean.GetProperty("fromSeq").GetInt64().ShouldBe(1);
        clean.GetProperty("anchorSeq").ValueKind.ShouldBe(JsonValueKind.Null);
        clean.GetProperty("headHash").GetString()!.Length.ShouldBe(64);

        // Anchor: once per head; a second call at the same head returns the same anchor.
        var anchor = await (await owner.PostAsync("/api/v1/audit/chain/anchor", null)).ReadJsonAsync();
        anchor.GetProperty("seq").GetInt64().ShouldBe(length);
        anchor.GetProperty("store").GetString().ShouldBe("file");
        anchor.GetProperty("headHash").GetString().ShouldBe(clean.GetProperty("headHash").GetString());
        var again = await (await owner.PostAsync("/api/v1/audit/chain/anchor", null)).ReadJsonAsync();
        again.GetProperty("id").GetGuid().ShouldBe(anchor.GetProperty("id").GetGuid());
        var anchorFile = Path.Combine(Api.AnchorDirectory, "anchors.jsonl");
        File.Exists(anchorFile).ShouldBeTrue();
        (await File.ReadAllBytesAsync(anchorFile))[0].ShouldBe((byte)'{'); // plain JSON lines, no byte-order mark

        var anchored = await VerifyAsync(owner);
        anchored.GetProperty("status").GetString().ShouldBe("ok");
        anchored.GetProperty("anchorSeq").GetInt64().ShouldBe(length);
        anchored.GetProperty("anchorMatched").GetBoolean().ShouldBeTrue();

        var status = await (await owner.GetAsync("/api/v1/audit/chain")).ReadJsonAsync();
        status.GetProperty("head").GetProperty("seq").GetInt64().ShouldBe(length);
        status.GetProperty("lastAnchor").GetProperty("seq").GetInt64().ShouldBe(length);
        status.GetProperty("lastVerification").GetProperty("status").GetString().ShouldBe("ok");
        (await (await owner.GetAsync("/api/v1/audit/chain/verifications")).ReadJsonAsync()).GetArrayLength().ShouldBe(2);
        (await (await owner.GetAsync("/api/v1/audit/chain/anchors")).ReadJsonAsync()).GetArrayLength().ShouldBe(1);

        // 1. The anchor file is edited: the chain is intact but no longer matches what was anchored.
        var original = await File.ReadAllTextAsync(anchorFile);
        await File.WriteAllTextAsync(anchorFile, original.Replace(anchor.GetProperty("headHash").GetString()!, new string('0', 64), StringComparison.Ordinal));
        var fileTampered = await VerifyAsync(owner);
        fileTampered.GetProperty("status").GetString().ShouldBe("anchor_mismatch");
        fileTampered.GetProperty("anchorMatched").GetBoolean().ShouldBeFalse();
        await File.WriteAllTextAsync(anchorFile, original);
        (await VerifyAsync(owner)).GetProperty("status").GetString().ShouldBe("ok");

        // 2. A stored event is altered by someone with owner rights in maintenance mode.
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "UPDATE app.aud_events SET reason = 'tampered' WHERE tenant_id = @t AND seq = 2", new { t = ws.TenantId });
        var rowTampered = await VerifyAsync(owner);
        rowTampered.GetProperty("status").GetString().ShouldBe("broken");
        rowTampered.GetProperty("firstBrokenSeq").GetInt64().ShouldBe(2);
        rowTampered.GetProperty("message").GetString()!.ShouldContain("altered");
        rowTampered.GetProperty("anchorMatched").GetBoolean().ShouldBeFalse();
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "UPDATE app.aud_events SET reason = NULL WHERE tenant_id = @t AND seq = 2", new { t = ws.TenantId });
        (await VerifyAsync(owner)).GetProperty("status").GetString().ShouldBe("ok");

        // 3. The chain head is rewritten to hide a change: the head no longer matches the recomputed last link.
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "UPDATE app.aud_chain_heads SET head_hash = '\\x00' WHERE tenant_id = @t", new { t = ws.TenantId });
        (await VerifyAsync(owner)).GetProperty("status").GetString().ShouldBe("truncated");
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "UPDATE app.aud_chain_heads SET head_hash = decode(@h, 'hex') WHERE tenant_id = @t", new { t = ws.TenantId, h = anchor.GetProperty("headHash").GetString() });
        (await VerifyAsync(owner)).GetProperty("status").GetString().ShouldBe("ok");

        // 4. The newest event is deleted: the head still counts it.
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "DELETE FROM app.aud_events WHERE tenant_id = @t AND seq = @seq", new { t = ws.TenantId, seq = length });
        var truncated = await VerifyAsync(owner);
        truncated.GetProperty("status").GetString().ShouldBe("truncated");
        truncated.GetProperty("toSeq").GetInt64().ShouldBe(length - 1);
        truncated.GetProperty("message").GetString()!.ShouldContain("removed");

        // 5. An event in the middle is deleted: a sequence gap.
        await AuditRowFactories.TamperAsync(Api.Db.OwnerConnectionString, ws.TenantId, "DELETE FROM app.aud_events WHERE tenant_id = @t AND seq = 3", new { t = ws.TenantId });
        var gap = await VerifyAsync(owner);
        gap.GetProperty("status").GetString().ShouldBe("broken");
        gap.GetProperty("firstBrokenSeq").GetInt64().ShouldBe(3);
        gap.GetProperty("message").GetString()!.ShouldContain("missing");
    }

    [Fact]
    public async Task Application_role_cannot_change_or_remove_events_or_move_the_head()
    {
        var ws = await Api.SignupAsync();
        var (connection, transaction) = await Quicker.Testing.TestTenants.OpenAsync(Api.Db, new Kernel.Ids.TenantId(ws.TenantId));
        await using (connection)
        {
            foreach (var sql in new[]
            {
                "UPDATE app.aud_events SET reason = 'x' WHERE seq = 1",
                "DELETE FROM app.aud_events WHERE seq = 1",
                "UPDATE app.aud_chain_heads SET seq = 0",
                "INSERT INTO app.aud_chain_heads (tenant_id) VALUES (gen_random_uuid())",
            })
            {
                await connection.ExecuteAsync("SAVEPOINT probe", transaction: transaction);
                var ex = await Should.ThrowAsync<Npgsql.PostgresException>(async () => await connection.ExecuteAsync(sql, transaction: transaction));
                ex.SqlState.ShouldBe("42501", sql);
                await connection.ExecuteAsync("ROLLBACK TO SAVEPOINT probe", transaction: transaction);
            }

            // Inserting is allowed, and the trigger, not the caller, decides the sequence and the hashes.
            var id = await AuditRowFactories.InsertEventAsync(connection, transaction, ws.TenantId, "probe");
            var link = await connection.QuerySingleAsync<(long Seq, byte[] Prev, byte[] Hash)>("SELECT seq, prev_hash, hash FROM app.aud_events WHERE id = @id", new { id }, transaction);
            var head = await connection.QuerySingleAsync<(long Seq, byte[] Hash)>("SELECT seq, head_hash FROM app.aud_chain_heads", transaction: transaction);
            head.Seq.ShouldBe(link.Seq);
            head.Hash.ShouldBe(link.Hash);
            link.Hash.Length.ShouldBe(32);
            link.Prev.Length.ShouldBe(32);
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task Platform_jobs_anchor_and_verify_every_tenant_and_the_platform_chain()
    {
        var first = await Api.SignupAsync();
        var second = await Api.SignupAsync();
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = first.OwnerEmail, password = "wrong-password-for-platform-event" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var jobs = Api.Services.GetRequiredService<AuditChainJobs>();
        var anchors = await jobs.AnchorAllAsync();
        anchors.Select(static a => a.TenantId).ShouldContain(first.TenantId);
        anchors.Select(static a => a.TenantId).ShouldContain(second.TenantId);
        anchors.ShouldContain(static a => a.Chain == "platform");

        var verifications = await jobs.VerifyAllAsync();
        var mine = verifications.Where(v => v.TenantId == first.TenantId || v.TenantId == second.TenantId || v.Chain == "platform").ToList();
        mine.Count.ShouldBe(3);
        mine.ShouldAllBe(static v => v.Status == "ok" && v.AnchorMatched == true);

        // Anchoring again is a no-op until new events arrive.
        var again = await jobs.AnchorAllAsync();
        again.Single(a => a.TenantId == first.TenantId).Id.ShouldBe(anchors.Single(a => a.TenantId == first.TenantId).Id);
    }
}
