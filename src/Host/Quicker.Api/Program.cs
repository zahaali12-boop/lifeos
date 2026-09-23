using Microsoft.Extensions.Options;
using Quicker.Accounting;
using Quicker.Accounting.Api;
using Quicker.Api;
using Quicker.Audit;
using Quicker.Audit.Api;
using Quicker.Banking;
using Quicker.Banking.Api;
using Quicker.Collaboration;
using Quicker.Collaboration.Api;
using Quicker.Identity;
using Quicker.Identity.Api;
using Quicker.Integration;
using Quicker.Integration.Api;
using Quicker.Integrity;
using Quicker.Integrity.Api;
using Quicker.Inventory;
using Quicker.Inventory.Api;
using Quicker.Items;
using Quicker.Items.Api;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering;
using Quicker.Numbering.Api;
using Quicker.Observability;
using Quicker.Organization;
using Quicker.Organization.Api;
using Quicker.Partners;
using Quicker.Partners.Api;
using Quicker.Payables;
using Quicker.Payables.Api;
using Quicker.Persistence;
using Quicker.Purchasing;
using Quicker.Purchasing.Api;
using Quicker.Storage;
using Quicker.Tenancy;
using Quicker.Web;
using Quicker.Workflow;
using Quicker.Workflow.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddQuickerObservability("quicker-api");

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
builder.Services.AddOpenApi("v1", static options =>
{
    options.AddOperationTransformer<OperationIdTransformer>();
    options.AddOperationTransformer<MultipartFormTransformer>();
    options.CreateSchemaReferenceId = SchemaReferenceIds.Create;
});
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
builder.Services.AddAccountingModule();
builder.Services.AddIntegrityModule();
builder.Services.AddItemsModule();
builder.Services.AddInventoryModule();
builder.Services.AddWorkflowModule();
builder.Services.AddPartnersModule();
builder.Services.AddPayablesModule();
builder.Services.AddBankingModule();
builder.Services.AddPurchasingModule();

var app = builder.Build();

app.UseExceptionHandler();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.UseQuickerUnitOfWork("/api");
app.UseQuickerRequestLogging();
app.UseQuickerIdempotency("/api");
app.UseQuickerResponseShaping("/api");

app.MapOpenApi("/api/{documentName}/openapi.json");

app.MapQuickerHealth();

// Every /api/v1 endpoint runs inside one unit of work with the principal resolved (ADR-0009, ADR-0014).
var api = app.MapGroup("/api/v1").AddEndpointFilter<UnitOfWorkFilter>().RequireQuickerRateLimit();
api.MapIdentityEndpoints();
api.MapAuditEndpoints();
api.MapOrganizationEndpoints();
api.MapNumberingEndpoints();
api.MapPlatformEndpoints();
api.MapIntegrationEndpoints();
api.MapCollaborationEndpoints();
api.MapAccountingEndpoints();
api.MapIntegrityEndpoints();
api.MapItemsEndpoints();
api.MapInventoryEndpoints();
api.MapWorkflowEndpoints();
api.MapPartnersEndpoints();
api.MapPayablesEndpoints();
api.MapBankingEndpoints();
api.MapPurchasingEndpoints();

app.Run();

/// <summary>Marker for integration tests (WebApplicationFactory).</summary>
public partial class Program
{
}
