using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Audit;
using Quicker.Audit.Api;
using Quicker.Identity;
using Quicker.Identity.Api;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Persistence;
using Quicker.Tenancy;
using Quicker.Web;

var builder = WebApplication.CreateBuilder(args);

// Infrastructure
builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
builder.Services.AddSingleton(static sp => sp.GetRequiredService<IOptions<DbOptions>>().Value);
builder.Services.AddSingleton(static sp => DataSources.ForApp(sp.GetRequiredService<DbOptions>()));
builder.Services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddSingleton<CapturingEmailSender>();
builder.Services.AddSingleton<IEmailSender>(static sp => sp.GetRequiredService<CapturingEmailSender>());
builder.Services.AddQuickerWebCore();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1");

// Modules
builder.Services.AddTenancyModule();
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseQuickerUnitOfWork("/api");

app.MapOpenApi("/api/{documentName}/openapi.json");

// Liveness: the process is up. Readiness: the database answers under the application role and the schema is migrated.
app.MapGet("/health/live", static () => Results.Ok(new { status = "live" })).ExcludeFromDescription();
app.MapGet("/health/ready", static async (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var migrations = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ops.schemaversions");
        return Results.Ok(new { status = "ready", migrations });
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        return Results.Json(new { status = "unready", error = ex.GetType().Name }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).ExcludeFromDescription();

// Every /api/v1 endpoint runs inside one unit of work with the principal resolved (ADR-0009, ADR-0014).
var api = app.MapGroup("/api/v1").AddEndpointFilter<UnitOfWorkFilter>();
api.MapIdentityEndpoints();
api.MapAuditEndpoints();

app.Run();

/// <summary>Marker for integration tests (WebApplicationFactory).</summary>
public partial class Program
{
}
