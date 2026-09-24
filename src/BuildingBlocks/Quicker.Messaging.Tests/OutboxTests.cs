using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.TestSupport;
using Quicker.Messaging.Outbox;

namespace Quicker.Messaging.Tests;

/// <summary>ADR-0010 kill-the-worker proof: no event lost, no duplicate effect; ordering per aggregate; dead letters and retry.</summary>
[Collection(ApiCollection.Name)]
public sealed class OutboxTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private OutboxDispatcher Dispatcher => Api.Services.GetRequiredService<OutboxDispatcher>();

    [Fact]
    public async Task Events_commit_with_their_transaction_and_are_handled_exactly_once_even_when_the_worker_dies_mid_batch()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();

        // 3 aggregates × 10 events in one transaction; the 15th (by order) blocks so we can kill the worker while it runs.
        var aggregates = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
        {
            var outbox = sp.GetRequiredService<IOutbox>();
            for (var sequence = 1; sequence <= 10; sequence++)
            {
                foreach (var aggregate in aggregates)
                {
                    await outbox.PublishAsync(new ThingHappened(aggregate, sequence, sequence == 5 && aggregate == aggregates[2] ? "act" : "ok"));
                }
            }
        });
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) => await sp.GetRequiredService<IOutbox>().PublishAsync(new ThingHappened(Guid.NewGuid(), 99, "rolled back")), commit: false);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.outbox_messages WHERE tenant_id = @t", new { t = ws.TenantId })).ShouldBe(30);

        // First worker: dies (cancellation) while the blocking handler is inside its transaction.
        TestGates.Arm(blocks: 1);
        using var firstRun = new CancellationTokenSource();
        var first = Task.Run(async () =>
        {
            while (await Dispatcher.RunOnceAsync(firstRun.Token) > 0)
            {
            }
        });
        await TestGates.Reached.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await firstRun.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => first);

        var afterCrash = await Effects.ReadAsync(Api, ws.TenantId, "test.event.");
        afterCrash.Count.ShouldBeLessThan(30, "the crash happened mid-batch");
        afterCrash.Values.ShouldAllBe(static v => v == 1);
        // Published marks commit batch by batch (one message per aggregate per batch); the crashed batch rolled back,
        // so a message is published exactly when its handler's effect committed, never before.
        var publishedAfterCrash = (await TenantWork.QueryOwnerRowAsync<string>(Api, "SELECT COALESCE(string_agg(id::text, ','), '') FROM ops.outbox_messages WHERE tenant_id = @t AND published_at IS NOT NULL", new { t = ws.TenantId })).Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        publishedAfterCrash.Count.ShouldBeLessThan(30);
        publishedAfterCrash.ShouldAllBe(id => afterCrash.ContainsKey("test.event." + id.ToString("N")));

        // Second worker: finishes the batch; handlers that already committed are skipped through the inbox.
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        var effects = await Effects.ReadAsync(Api, ws.TenantId, "test.event.");
        effects.Count.ShouldBe(30);
        effects.Values.ShouldAllBe(static v => v == 1);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.outbox_messages WHERE tenant_id = @t AND published_at IS NOT NULL", new { t = ws.TenantId })).ShouldBe(30);
        // One inbox row per (handler, event): the typed handler is asserted here; observers such as the webhook fan-out add their own rows.
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.inbox i JOIN ops.outbox_messages o ON o.id = i.event_id WHERE o.tenant_id = @t AND i.handler = @h", new { t = ws.TenantId, h = typeof(RecordingHandler).FullName })).ShouldBe(30);

        // Order held per aggregate even across the crash (a redelivered event never overtakes its predecessor).
        foreach (var aggregate in aggregates)
        {
            var sequence = TestGates.HandledOrder.Where(h => h.Aggregate == aggregate).Select(static h => h.Sequence).ToList();
            sequence.ShouldNotBeEmpty();
            sequence.Zip(sequence.Skip(1)).ShouldAllBe(static pair => pair.Second >= pair.First);
            sequence.Distinct().ShouldBe(Enumerable.Range(1, 10));
        }

        // Redelivery of a handled event is a no-op: unpublish one and dispatch again.
        var redelivered = await TenantWork.QueryOwnerAsync<Guid>(Api, "UPDATE ops.outbox_messages SET published_at = NULL WHERE id = (SELECT id FROM ops.outbox_messages WHERE tenant_id = @t ORDER BY seq DESC LIMIT 1) RETURNING id", new { t = ws.TenantId });
        (await Dispatcher.RunOnceAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
        (await TenantWork.QueryOwnerAsync<bool>(Api, "SELECT published_at IS NOT NULL FROM ops.outbox_messages WHERE id = @id", new { id = redelivered })).ShouldBeTrue();
        (await Effects.ReadAsync(Api, ws.TenantId, "test.event." + redelivered.ToString("N")))["test.event." + redelivered.ToString("N")].ShouldBe(1);
    }

    [Fact]
    public async Task Two_workers_share_the_queue_without_duplicates_and_keep_aggregate_order()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();
        var aggregates = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        for (var sequence = 1; sequence <= 15; sequence++)
        {
            var s = sequence;
            await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
            {
                var outbox = sp.GetRequiredService<IOutbox>();
                foreach (var aggregate in aggregates)
                {
                    await outbox.PublishAsync(new ThingHappened(aggregate, s, "ok"));
                }
            });
        }

        var workers = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
            {
            }
        })).ToList();
        await Task.WhenAll(workers);
        // The two loops stop when they see an empty claim while the other holds locks; drain what is left.
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        var effects = await Effects.ReadAsync(Api, ws.TenantId, "test.event.");
        effects.Count.ShouldBe(60);
        effects.Values.ShouldAllBe(static v => v == 1);
        foreach (var aggregate in aggregates)
        {
            var sequence = TestGates.HandledOrder.Where(h => h.Aggregate == aggregate).Select(static h => h.Sequence).ToList();
            sequence.ShouldBe(Enumerable.Range(1, 15));
        }
    }

    [Fact]
    public async Task A_failing_handler_backs_off_then_dead_letters_and_an_operator_retry_delivers_it()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();
        var aggregate = Guid.NewGuid();
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
        {
            var outbox = sp.GetRequiredService<IOutbox>();
            await outbox.PublishAsync(new ThingHappened(aggregate, 1, "act"));
            await outbox.PublishAsync(new ThingHappened(aggregate, 2, "ok"));
        });

        TestGates.Arm(failures: 2);
        (await Dispatcher.RunOnceAsync(CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);
        var first = await TenantWork.QueryOwnerRowAsync<(int Attempts, string Error, bool Dead)>(Api, "SELECT attempts, last_error, dead_at IS NOT NULL FROM ops.outbox_messages WHERE aggregate_id = @a ORDER BY seq LIMIT 1", new { a = aggregate });
        first.Attempts.ShouldBe(1);
        first.Error.ShouldContain("simulated failure");
        first.Dead.ShouldBeFalse();
        (await Effects.ReadAsync(Api, ws.TenantId, "test.event.")).ShouldBeEmpty("the failed handler's transaction rolled back, and event 2 waits behind event 1");
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        (await Effects.ReadAsync(Api, ws.TenantId, "test.event.")).ShouldBeEmpty("backed off: not due yet");

        await Task.Delay(TimeSpan.FromSeconds(1.2));
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        var admin = Api.Services.GetRequiredService<OutboxAdmin>();
        var deadLetters = await admin.ListAsync("dead", ws.TenantId, 10, CancellationToken.None);
        var letter = deadLetters.ShouldHaveSingleItem();
        letter.Attempts.ShouldBe(2);
        letter.AggregateId.ShouldBe(aggregate);

        // Dead letters block their aggregate's later events until an operator retries (or the message is dropped).
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        (await Effects.ReadAsync(Api, ws.TenantId, "test.event.")).ShouldBeEmpty();
        (await admin.RetryAsync(letter.Id, CancellationToken.None)).ShouldBeTrue();
        (await admin.RetryAsync(letter.Id, CancellationToken.None)).ShouldBeFalse("no longer dead");
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        (await Effects.ReadAsync(Api, ws.TenantId, "test.event.")).Count.ShouldBe(2);
        TestGates.HandledOrder.Where(h => h.Aggregate == aggregate).Select(static h => h.Sequence).ShouldBe([1, 1, 1, 2]);
    }

    [Fact]
    public async Task Events_without_handlers_are_published_untouched_and_archiving_removes_old_ones()
    {
        var ws = await Api.SignupAsync();
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (_, uow) =>
            await uow.Connection.ExecuteAsync("INSERT INTO ops.outbox_messages (id, tenant_id, event_type, aggregate_type, aggregate_id, payload) VALUES (@id, @t, 'nobody.listens', 'x', @a, '{}')", new { id = Guid.CreateVersion7(), t = ws.TenantId, a = Guid.NewGuid() }, uow.Transaction));
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }

        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.outbox_messages WHERE tenant_id = @t AND published_at IS NOT NULL", new { t = ws.TenantId })).ShouldBe(1);

        await TenantWork.ExecuteOwnerAsync(Api, "UPDATE ops.outbox_messages SET published_at = now() - interval '40 days' WHERE tenant_id = @t", new { t = ws.TenantId });
        var removed = await Api.Services.GetRequiredService<OutboxAdmin>().ArchiveAsync(30, CancellationToken.None);
        removed.ShouldBeGreaterThanOrEqualTo(1);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.outbox_messages WHERE tenant_id = @t", new { t = ws.TenantId })).ShouldBe(0);
    }

    [Fact]
    public async Task An_operator_discards_a_dead_letter_with_a_reason_and_its_aggregate_moves_on()
    {
        TestGates.Reset();
        var ws = await Api.SignupAsync();
        var aggregate = Guid.NewGuid();
        await TenantWork.InTenantAsync(Api, ws.TenantId, async (sp, _) =>
        {
            var outbox = sp.GetRequiredService<IOutbox>();
            await outbox.PublishAsync(new ThingHappened(aggregate, 1, "act"));
            await outbox.PublishAsync(new ThingHappened(aggregate, 2, "ok"));
        });

        // Event 1 fails twice and is dead-lettered; event 2 waits behind it.
        TestGates.Arm(failures: 2);
        await DrainAsync();
        await Task.Delay(TimeSpan.FromSeconds(1.2));
        await DrainAsync();
        var admin = Api.Services.GetRequiredService<OutboxAdmin>();
        var letter = (await admin.ListAsync("dead", ws.TenantId, 10, CancellationToken.None)).ShouldHaveSingleItem();
        (await Effects.ReadAsync(Api, ws.TenantId, "test.event.")).ShouldBeEmpty();

        // Only platform operators act on the outbox, and a reason is required.
        using var member = Api.ClientFor(ws.AccessToken);
        (await (await member.PostAsJsonAsync($"/api/v1/platform/ops/outbox/{letter.Id}/discard", new { reason = "not needed" }, ApiFixture.Json)).ErrorCodeAsync()).ShouldBe("auth.operator_required");
        await TenantWork.ExecuteOwnerAsync(Api, "UPDATE control.users SET is_platform_operator = true WHERE id = @id", new { id = ws.UserId });
        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = ws.OwnerEmail, password = ws.OwnerPassword }, ApiFixture.Json)).ReadJsonAsync();
        using var operatorClient = Api.ClientFor(login.GetProperty("tokens").GetProperty("accessToken").GetString()!);
        var unexplained = await operatorClient.PostAsJsonAsync($"/api/v1/platform/ops/outbox/{letter.Id}/discard", new { reason = "  " }, ApiFixture.Json);
        unexplained.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await unexplained.ErrorCodeAsync()).ShouldBe("outbox.reason_required");

        var discard = await operatorClient.PostAsJsonAsync($"/api/v1/platform/ops/outbox/{letter.Id}/discard", new { reason = "Effect applied by hand (ticket OPS-42)" }, ApiFixture.Json);
        discard.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await (await operatorClient.PostAsJsonAsync($"/api/v1/platform/ops/outbox/{letter.Id}/discard", new { reason = "again" }, ApiFixture.Json)).ErrorCodeAsync()).ShouldBe("outbox.not_dead");
        (await (await operatorClient.PostAsync($"/api/v1/platform/ops/outbox/{letter.Id}/retry", null)).ErrorCodeAsync()).ShouldBe("outbox.not_dead");

        // It leaves the dead letters, keeps who, when and why, and the aggregate's next event is delivered; event 1's handler never ran.
        (await admin.ListAsync("dead", ws.TenantId, 10, CancellationToken.None)).ShouldBeEmpty();
        var discarded = (await (await operatorClient.GetAsync($"/api/v1/platform/ops/outbox?state=discarded&tenantId={ws.TenantId}")).ReadJsonAsync()).EnumerateArray().ShouldHaveSingleItem();
        discarded.GetProperty("id").GetGuid().ShouldBe(letter.Id);
        discarded.GetProperty("discardReason").GetString().ShouldBe("Effect applied by hand (ticket OPS-42)");
        discarded.GetProperty("discardedBy").GetString()!.ShouldContain(ws.OwnerEmail);
        discarded.GetProperty("discardedAt").ValueKind.ShouldBe(JsonValueKind.String);
        await DrainAsync();
        var effects = await Effects.ReadAsync(Api, ws.TenantId, "test.event.");
        effects.Count.ShouldBe(1);
        TestGates.HandledOrder.Where(h => h.Aggregate == aggregate).Select(static h => h.Sequence).ShouldBe([1, 1, 2]);

        // Discarded messages are archived with the published ones once past the retention.
        await TenantWork.ExecuteOwnerAsync(Api, "UPDATE ops.outbox_messages SET discarded_at = now() - interval '40 days' WHERE id = @id", new { id = letter.Id });
        await admin.ArchiveAsync(30, CancellationToken.None);
        (await TenantWork.QueryOwnerAsync<long>(Api, "SELECT count(*) FROM ops.outbox_messages WHERE id = @id", new { id = letter.Id })).ShouldBe(0);
    }

    private async Task DrainAsync()
    {
        while (await Dispatcher.RunOnceAsync(CancellationToken.None) > 0)
        {
        }
    }
}
