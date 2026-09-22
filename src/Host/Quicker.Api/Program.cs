using Dapper;
using Microsoft.Extensions.Options;
using Npgsql;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
builder.Services.AddSingleton(static sp => sp.GetRequiredService<IOptions<DbOptions>>().Value);
builder.Services.AddSingleton(static sp => DataSources.ForApp(sp.GetRequiredService<DbOptions>()));
builder.Services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddOpenApi("v1");

var app = builder.Build();

app.MapOpenApi("/api/{documentName}/openapi.json");

// Liveness: the process is up. Readiness: the database answers under the application role and the schema is migrated.
app.MapGet("/health/live", static () => Results.Ok(new { status = "live" }));
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
});

app.Run();

/// <summary>Marker for integration tests (WebApplicationFactory).</summary>
public partial class Program
{
}
