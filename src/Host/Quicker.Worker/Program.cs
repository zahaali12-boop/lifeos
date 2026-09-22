using Microsoft.Extensions.Options;
using Quicker.Accounting;
using Quicker.Audit;
using Quicker.Collaboration;
using Quicker.Identity;
using Quicker.Integration;
using Quicker.Integrity;
using Quicker.Inventory;
using Quicker.Items;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering;
using Quicker.Observability;
using Quicker.Organization;
using Quicker.Persistence;
using Quicker.Storage;
using Quicker.Tenancy;
using Quicker.Web;

var builder = WebApplication.CreateBuilder(args);
builder.AddQuickerObservability("quicker-worker");

// Infrastructure: the same composition as the API host, minus the HTTP pipeline.
builder.Services.Configure<DbOptions>(builder.Configuration.GetSection(DbOptions.SectionName));
builder.Services.AddSingleton(static sp => sp.GetRequiredService<IOptions<DbOptions>>().Value);
builder.Services.AddSingleton(static sp => DataSources.ForApp(sp.GetRequiredService<DbOptions>()));
builder.Services.AddSingleton<IUnitOfWorkFactory, UnitOfWorkFactory>();
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<ITenantContextAccessor, TenantContextAccessor>();
builder.Services.AddQuickerEmail(builder.Configuration);
builder.Services.AddQuickerStorage(builder.Configuration);
builder.Services.AddQuickerWebCore();
builder.Services.AddQuickerMessaging(builder.Configuration);
builder.Services.AddQuickerWorker();

// Modules: their handlers and job types.
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

var app = builder.Build();

app.MapQuickerHealth();

app.Run();
