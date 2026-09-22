using Dapper;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Api;
using Quicker.Audit;
using Quicker.Audit.Api;
using Quicker.Collaboration;
using Quicker.Collaboration.Api;
using Quicker.Identity;
using Quicker.Identity.Api;
using Quicker.Integration;
using Quicker.Integration.Api;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering;
using Quicker.Numbering.Api;
using Quicker.Organization;
using Quicker.Organization.Api;
using Quicker.Persistence;
using Quicker.Storage;
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
builder.Services.AddQuickerEmail(builder.Configuration);
builder.Services.AddQuickerStorage(builder.Configuration);
builder.Services.AddQuickerWebCore();
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi("v1", static options => options.AddOperationTransformer<OperationIdTransformer>());
builder.Services.AddQuickerMessaging(builder.Configuration);
builder.Services.AddQuickerIdempotency(builder.Configuration);
builder.Services.AddQuickerRateLimiting(builder.Configuration);
builder.Services.AddQuickerResponseShaping();

// The web app is served from its own origin in development (Vite) and may be in production (CDN); ETag and the
// idempotency/rate-limit headers are exposed so the typed client can read them.
var allowedOrigins = (builder.Configuration["Quicker:Api:AllowedOrigins"] ?? builder.Configuration["Quicker:Auth:PublicOrigin"] ?? string.Empty)
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod().WithExposedHeaders("ETag", "Idempotent-Replayed", "Retry-After", "Location")));
if (builder.Configuration.GetValue<bool>("Quicker:Worker:Embedded"))
{
    // Single-node installs run the dispatcher, job slots and scheduler inside the API process (ADR-0010).
    builder.Services.AddQuickerWorker();
}

// Modules
builder.Services.AddTenancyModule();
builder.Services.AddAuditModule(builder.Configuration);
builder.Services.AddIdentityModule(builder.Configuration);
builder.Services.AddOrganizationModule(builder.Configuration);
builder.Services.AddNumberingModule();
builder.Services.AddIntegrationModule();
builder.Services.AddCollaborationModule();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseQuickerUnitOfWork("/api");
app.UseQuickerIdempotency("/api");
app.UseQuickerResponseShaping("/api");

app.MapOpenApi("/api/{documentName}/openapi.json");

// Liveness: the process is up. Readiness: the database answers under the application role and the schema is migrated.
app.MapGet("/health/live", static () => Results.Ok(new HealthStatus("live", null))).WithTags("Health").WithSummary("The process is up");
app.MapGet("/health/ready", static async Task<Results<Ok<HealthStatus>, JsonHttpResult<HealthStatus>>> (NpgsqlDataSource dataSource, CancellationToken cancellationToken) =>
{
    try
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var migrations = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM ops.schemaversions");
        return TypedResults.Ok(new HealthStatus("ready", migrations));
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
    {
        return TypedResults.Json(new HealthStatus("unready", null, ex.GetType().Name), statusCode: StatusCodes.Status503ServiceUnavailable);
    }
}).WithTags("Health").WithSummary("The database answers under the application role and the schema is migrated").Produces<HealthStatus>(StatusCodes.Status503ServiceUnavailable);

// Every /api/v1 endpoint runs inside one unit of work with the principal resolved (ADR-0009, ADR-0014).
var api = app.MapGroup("/api/v1").AddEndpointFilter<UnitOfWorkFilter>().RequireQuickerRateLimit();
api.MapIdentityEndpoints();
api.MapAuditEndpoints();
api.MapOrganizationEndpoints();
api.MapNumberingEndpoints();
api.MapPlatformEndpoints();
api.MapIntegrationEndpoints();
api.MapCollaborationEndpoints();

app.Run();

/// <summary>Marker for integration tests (WebApplicationFactory).</summary>
public partial class Program
{
}
