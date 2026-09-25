using System.Diagnostics;
using System.Diagnostics.Metrics;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace Quicker.Observability;

/// <summary>Settings of the health thresholds (section <c>Quicker:Health</c>).</summary>
public sealed class HealthOptions
{
    public const string SectionName = "Quicker:Health";

    /// <summary>Readiness fails when the oldest unpublished, not dead outbox message is older than this.</summary>
    public int MaxOutboxLagSeconds { get; set; } = 300;

    /// <summary>Readiness fails when more queued jobs than this are past their run time (the workers are not keeping up).</summary>
    public int MaxOverdueJobs { get; set; } = 1000;
}

/// <summary>The names every host shares: activity sources, meters and the tag keys of tenant context on spans.</summary>
public static class Telemetry
{
    public const string ServiceNamespace = "quicker";
    public static readonly ActivitySource Requests = new("Quicker.Requests");
    public static readonly ActivitySource Jobs = new("Quicker.Jobs");
    public static readonly ActivitySource Outbox = new("Quicker.Outbox");
    public static readonly Meter Meter = new("Quicker.Platform");

    public const string TenantTag = "quicker.tenant_id";
    public const string UserTag = "quicker.user_id";
    public const string MembershipTag = "quicker.membership_id";
    public const string ActorTag = "quicker.actor";
    public const string RequestTag = "quicker.request_id";

    /// <summary>Requests per tenant (the per-endpoint latency histograms come from the ASP.NET Core instrumentation).</summary>
    public static readonly Counter<long> TenantRequests = Meter.CreateCounter<long>("quicker.requests", unit: "{request}", description: "API requests by tenant and actor type");

    /// <summary>Tags the current span with the tenant context; no PII, ids only.</summary>
    public static void TagCurrent(Guid? tenantId, Guid? userId, Guid? membershipId, string actorType, string requestId)
    {
        var activity = Activity.Current;
        if (activity is null)
        {
            return;
        }

        if (tenantId is { } tenant && tenant != Guid.Empty)
        {
            activity.SetTag(TenantTag, tenant.ToString());
        }

        if (userId is { } user && user != Guid.Empty)
        {
            activity.SetTag(UserTag, user.ToString());
        }

        if (membershipId is { } membership && membership != Guid.Empty)
        {
            activity.SetTag(MembershipTag, membership.ToString());
        }

        activity.SetTag(ActorTag, actorType);
        activity.SetTag(RequestTag, requestId);
    }
}

public static class ObservabilityRegistration
{
    /// <summary>
    /// Serilog (structured JSON on the console, enriched from the log context), OpenTelemetry traces and metrics for
    /// ASP.NET Core, HttpClient, Npgsql and the Quicker sources, exported over OTLP when OTEL_EXPORTER_OTLP_ENDPOINT
    /// is set, the platform gauges and the health checks.
    /// </summary>
    public static WebApplicationBuilder AddQuickerObservability(this WebApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Host.UseSerilog((context, services, configuration) => configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("service", serviceName)
            .Enrich.WithProperty("environment", context.HostingEnvironment.EnvironmentName)
            .WriteTo.Console(new CompactJsonFormatter()));

        builder.Services.Configure<HealthOptions>(builder.Configuration.GetSection(HealthOptions.SectionName));
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(serviceName, serviceNamespace: Telemetry.ServiceNamespace, serviceInstanceId: Environment.MachineName))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(static options => options.Filter = static context => !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddSource(Telemetry.Requests.Name, Telemetry.Jobs.Name, Telemetry.Outbox.Name))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddNpgsqlInstrumentation()
                .AddMeter(Telemetry.Meter.Name));
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        builder.Services.AddSingleton<PlatformGauges>();
        builder.Services.AddHostedService(static sp => sp.GetRequiredService<PlatformGauges>());
        builder.Services.AddHealthChecks()
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<OutboxLagHealthCheck>("outbox", tags: ["ready"])
            .AddCheck<JobQueueHealthCheck>("jobs", tags: ["ready"]);
        return builder;
    }

    /// <summary>
    /// <c>/health/live</c> (the process answers), <c>/health/ready</c> (database, migrations, outbox lag, job backlog;
    /// 503 when any fails) and <c>/health/deps</c> (every check with its data). Minimal-API endpoints, so the
    /// contract describes them and the typed client can read <see cref="HealthReportView"/>.
    /// </summary>
    public static WebApplication MapQuickerHealth(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapGet("/health/live", static () => TypedResults.Ok(new HealthReportView("live", null, null, [])))
            .WithTags("Health").WithSummary("The process is up");
        app.MapGet("/health/ready", static async (HealthCheckService health, CancellationToken cancellationToken) =>
            HealthResponse.Result(await health.CheckHealthAsync(static check => check.Tags.Contains("ready"), cancellationToken)))
            .WithTags("Health").WithSummary("The database answers under the application role, the schema is migrated, the outbox and job backlog are within limits")
            .Produces<HealthReportView>().Produces<HealthReportView>(StatusCodes.Status503ServiceUnavailable);
        app.MapGet("/health/deps", static async (HealthCheckService health, CancellationToken cancellationToken) =>
            HealthResponse.Result(await health.CheckHealthAsync(cancellationToken)))
            .WithTags("Health").WithSummary("Every dependency check with its data")
            .Produces<HealthReportView>().Produces<HealthReportView>(StatusCodes.Status503ServiceUnavailable);
        return app;
    }

    /// <summary>Request logging with the tenant context on every line: after the unit-of-work middleware has resolved it.</summary>
    public static IApplicationBuilder UseQuickerRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseSerilogRequestLogging(static options =>
        {
            options.MessageTemplate = "{RequestMethod} {RequestPath} answered {StatusCode} in {Elapsed:0.0} ms";
            options.EnrichDiagnosticContext = static (diagnostics, context) =>
            {
                var activity = Activity.Current;
                diagnostics.Set("TraceId", activity?.TraceId.ToString() ?? context.TraceIdentifier);
                foreach (var tag in activity?.Tags ?? [])
                {
                    if (tag.Key.StartsWith("quicker.", StringComparison.Ordinal))
                    {
                        diagnostics.Set(tag.Key.Replace('.', '_'), tag.Value);
                    }
                }
            };
        });
    }
}

/// <summary>The health payload: a status word, the applied migrations (when the database answers), an error name and each check.</summary>
public sealed record HealthReportView(string Status, long? Migrations, string? Error, IReadOnlyList<HealthCheckView> Checks);

public sealed record HealthCheckView(string Name, string Status, string? Description, IReadOnlyDictionary<string, object> Data);

internal static class HealthResponse
{
    public static IResult Result(HealthReport report)
    {
        var view = View(report);
        return TypedResults.Json(view, statusCode: report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
    }

    public static HealthReportView View(HealthReport report)
    {
        var entries = report.Entries.Select(static e => new HealthCheckView(e.Key, Word(e.Value.Status), e.Value.Description ?? e.Value.Exception?.GetType().Name, e.Value.Data)).ToList();
        var migrations = report.Entries.TryGetValue("database", out var db) && db.Data.TryGetValue("migrations", out var m) && m is long count ? count : (long?)null;
        var failing = report.Entries.Values.Where(static e => e.Status != HealthStatus.Healthy).Select(static e => (HealthReportEntry?)e).FirstOrDefault();
        return new HealthReportView(Word(report.Status), migrations, failing?.Exception?.GetType().Name ?? failing?.Description, entries);
    }

    private static string Word(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => "ready",
        HealthStatus.Degraded => "degraded",
        _ => "unready",
    };
}

/// <summary>The database answers under the application role and the schema is migrated.</summary>
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var migrations = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM ops.schemaversions", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy("database answers", new Dictionary<string, object>(StringComparer.Ordinal) { ["migrations"] = migrations });
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("database does not answer", ex);
        }
    }
}

/// <summary>The oldest unpublished, not dead outbox message is younger than the threshold (the dispatcher keeps up).</summary>
public sealed class OutboxLagHealthCheck(NpgsqlDataSource dataSource, Microsoft.Extensions.Options.IOptions<HealthOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var lag = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT floor(extract(epoch FROM (now() - min(occurred_at))))::bigint FROM ops.outbox_messages WHERE published_at IS NULL AND dead_at IS NULL", cancellationToken: cancellationToken)) ?? 0;
            var pending = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM ops.outbox_messages WHERE published_at IS NULL AND dead_at IS NULL", cancellationToken: cancellationToken));
            var data = new Dictionary<string, object>(StringComparer.Ordinal) { ["lagSeconds"] = lag, ["pending"] = pending, ["maxLagSeconds"] = options.Value.MaxOutboxLagSeconds };
            return lag > options.Value.MaxOutboxLagSeconds
                ? HealthCheckResult.Unhealthy($"outbox lag {lag}s exceeds {options.Value.MaxOutboxLagSeconds}s", data: data)
                : HealthCheckResult.Healthy($"outbox lag {lag}s", data);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("outbox cannot be read", ex);
        }
    }
}

/// <summary>Not too many queued jobs are past their run time.</summary>
public sealed class JobQueueHealthCheck(NpgsqlDataSource dataSource, Microsoft.Extensions.Options.IOptions<HealthOptions> options) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var overdue = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM ops.jobs WHERE state = 'queued' AND run_after <= now()", cancellationToken: cancellationToken));
            var dead = await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM ops.jobs WHERE state = 'dead'", cancellationToken: cancellationToken));
            var data = new Dictionary<string, object>(StringComparer.Ordinal) { ["overdue"] = overdue, ["dead"] = dead, ["maxOverdue"] = options.Value.MaxOverdueJobs };
            return overdue > options.Value.MaxOverdueJobs ? HealthCheckResult.Unhealthy($"{overdue} jobs overdue", data: data) : HealthCheckResult.Healthy($"{overdue} jobs overdue, {dead} dead", data);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            return HealthCheckResult.Unhealthy("job queue cannot be read", ex);
        }
    }
}
