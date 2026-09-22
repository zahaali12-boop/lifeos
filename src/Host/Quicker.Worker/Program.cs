using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Audit;
using Quicker.Identity;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Messaging.Jobs;
using Quicker.Numbering;
using Quicker.Organization;
using Quicker.Persistence;
using Quicker.Tenancy;
using Quicker.Web;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure: the same composition as the API host, minus the HTTP pipeline.
builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
builder.Services.AddSingleton(static sp => sp.GetRequiredService<IOptions<DbOptions>>().Value);
builder.Services.AddSingleton(static sp => DataSources.ForApp(sp.GetRequiredService<DbOptions>()));
builder.Services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddSingleton<CapturingEmailSender>();
builder.Services.AddSingleton<IEmailSender>(static sp => sp.GetRequiredService<CapturingEmailSender>());
builder.Services.AddQuickerWebCore();
builder.Services.AddQuickerMessaging(builder.Configuration);
builder.Services.AddQuickerWorker();

// Modules: their handlers and job types.
builder.Services.AddTenancyModule();
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddOrganizationModule(builder.Configuration);
builder.Services.AddNumberingModule();

var app = builder.Build();

app.MapGet("/health/live", static () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", static async (NpgsqlDataSource dataSource, JobRunner runner, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var queued = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ops.jobs WHERE state = 'queued' AND run_after <= now()");
        var pending = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ops.outbox_messages WHERE published_at IS NULL AND dead_at IS NULL");
        return Results.Ok(new { status = "ready", instance = runner.InstanceName, queuedJobs = queued, pendingMessages = pending });
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        return Results.Json(new { status = "unready", error = ex.GetType().Name }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
