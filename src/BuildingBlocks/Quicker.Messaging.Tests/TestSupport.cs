using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Kernel.Events;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Persistence;

namespace Quicker.Messaging.Tests;

/// <summary>
/// One API host per test class (the worker loops are not started; tests drive the dispatcher, runner and
/// scheduler directly). Test handlers record their effect as a tenant setting inside their unit of work, so
/// exactly-once is checked in the database, not in memory.
/// </summary>
public sealed class ApiHostFixture : IAsyncLifetime
{
    public ApiFixture Api { get; private set; } = null!;

    public async ValueTask InitializeAsync() =>
        Api = await ApiFixture.StartAsync(builder =>
        {
            builder.UseSetting("Quicker:Worker:OutboxMaxAttempts", "2");
            builder.UseSetting("Quicker:Worker:JobHeartbeatSeconds", "1");
            builder.UseSetting("Quicker:Worker:JobReclaimAfterSeconds", "3");
            builder.ConfigureServices(static services =>
            {
                services.AddIntegrationEventHandler<RecordingHandler, ThingHappened>();
                services.AddJobHandler<EffectJob, EffectPayload>();
            });
        });

    public async ValueTask DisposeAsync()
    {
        if (Api is not null)
        {
            await Api.DisposeAsync();
        }
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiHostFixture>
{
    public const string Name = "messaging-api";
}

public sealed record ThingHappened(Guid AggregateId, int Sequence, string Note) : IIntegrationEvent
{
    public static string EventType => "test.thing.happened";

    public static int EventVersion => 1;

    public string AggregateType => "thing";
}

/// <summary>Gates and counters the tests use to make a handler block (to simulate a crash) or fail on demand.</summary>
public static class TestGates
{
    public static readonly SemaphoreSlim Release = new(0);

    public static readonly ConcurrentQueue<(Guid Aggregate, int Sequence)> HandledOrder = new();

    public static TaskCompletionSource<bool> Reached { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int _blocksRemaining;

    private static int _failuresRemaining;

    public static void Reset()
    {
        while (Release.CurrentCount > 0)
        {
            Release.Wait(0);
        }

        HandledOrder.Clear();
        Reached = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _blocksRemaining = 0;
        _failuresRemaining = 0;
    }

    /// <summary>The next <paramref name="failures"/> acting handlers throw; the next <paramref name="blocks"/> after that block until released.</summary>
    public static void Arm(int blocks = 0, int failures = 0)
    {
        _blocksRemaining = blocks;
        _failuresRemaining = failures;
    }

    /// <summary>Blocks until the test releases it, or throws, depending on the counters; consumed once per trigger.</summary>
    public static async Task ActAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Decrement(ref _failuresRemaining) >= 0)
        {
            throw new InvalidOperationException("simulated failure");
        }

        if (Interlocked.Decrement(ref _blocksRemaining) >= 0)
        {
            Reached.TrySetResult(true);
            await Release.WaitAsync(cancellationToken);
        }
    }
}

public sealed class RecordingHandler(IUnitOfWorkAccessor unitOfWork) : IIntegrationEventHandler<ThingHappened>
{
    public async Task HandleAsync(ThingHappened integrationEvent, EventContext context, CancellationToken cancellationToken)
    {
        await Effects.RecordAsync(unitOfWork.Current, "test.event." + context.EventId.ToString("N"), cancellationToken);
        TestGates.HandledOrder.Enqueue((integrationEvent.AggregateId, integrationEvent.Sequence));
        if (integrationEvent.Note is "act")
        {
            await TestGates.ActAsync(cancellationToken);
        }
    }
}

public sealed record EffectPayload(string Key, bool Act = false, bool Permanent = false, bool Progress = false);

public sealed class EffectJob(IUnitOfWorkAccessor unitOfWork) : IJobHandler<EffectPayload>
{
    public static string JobType => "test.effect";

    public async Task<object?> ExecuteAsync(EffectPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        if (payload.Permanent)
        {
            throw new JobFailedException("permanently broken");
        }

        await Effects.RecordAsync(unitOfWork.Current, "test.job." + payload.Key, cancellationToken);
        if (payload.Progress)
        {
            await context.ReportProgressAsync(new { percent = 50 }, cancellationToken);
        }

        if (payload.Act)
        {
            await TestGates.ActAsync(cancellationToken);
        }

        return new { key = payload.Key, attempt = context.Attempt };
    }
}

/// <summary>Effects are rows in app.org_settings (tenant table under RLS) counted per key; a rolled-back handler leaves none.</summary>
public static class Effects
{
    public static Task RecordAsync(IUnitOfWork uow, string key, CancellationToken cancellationToken) =>
        uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO app.org_settings (tenant_id, id, company_id, key, value, value_type)
            VALUES (@t, @id, NULL, @key, '1'::jsonb, 'number')
            ON CONFLICT (tenant_id, company_id, key) DO UPDATE SET value = to_jsonb((app.org_settings.value #>> '{}')::int + 1)
            """, new { t = uow.Context.TenantId.Value, id = Guid.CreateVersion7(), key }, uow.Transaction, cancellationToken: cancellationToken));

    public static async Task<Dictionary<string, int>> ReadAsync(ApiFixture api, Guid tenantId, string prefix)
    {
        await using var owner = new NpgsqlConnection(api.Db.OwnerConnectionString);
        var rows = await owner.QueryAsync<(string Key, string Value)>("SELECT key, value::text FROM app.org_settings WHERE tenant_id = @t AND key LIKE @p", new { t = tenantId, p = prefix + "%" });
        return rows.ToDictionary(static r => r.Key, static r => int.Parse(r.Value, System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
    }
}

public static class TenantWork
{
    /// <summary>Runs work inside a committed unit of work under the tenant's system context (as a module would).</summary>
    public static async Task InTenantAsync(ApiFixture api, Guid tenantId, Func<IServiceProvider, IUnitOfWork, Task> work, bool commit = true)
    {
        await using var scope = api.Services.CreateAsyncScope();
        var factory = scope.ServiceProvider.GetRequiredService<IUnitOfWorkFactory>();
        await using var uow = await factory.BeginAsync(TenantContext.System(new TenantId(tenantId), "test-" + Guid.NewGuid().ToString("N")[^8..]));
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(uow);
        await work(scope.ServiceProvider, uow);
        if (commit)
        {
            await uow.CommitAsync();
        }
        else
        {
            await uow.RollbackAsync();
        }
    }

    public static async Task<T> QueryOwnerAsync<T>(ApiFixture api, string sql, object? parameters = null)
    {
        await using var owner = new NpgsqlConnection(api.Db.OwnerConnectionString);
        return (await owner.ExecuteScalarAsync<T>(sql, parameters))!;
    }

    public static async Task<T> QueryOwnerRowAsync<T>(ApiFixture api, string sql, object? parameters = null)
    {
        await using var owner = new NpgsqlConnection(api.Db.OwnerConnectionString);
        return await owner.QuerySingleAsync<T>(sql, parameters);
    }

    public static async Task ExecuteOwnerAsync(ApiFixture api, string sql, object? parameters = null)
    {
        await using var owner = new NpgsqlConnection(api.Db.OwnerConnectionString);
        await owner.ExecuteAsync(sql, parameters);
    }
}
