using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Application;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Persistence;
using Quicker.Messaging;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Workflow.Contracts;

namespace Quicker.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(InventoryPermissions.All);
        NumberedDocumentTypes.Register(
            new(AdjustmentService.DocumentType, InventoryPermissions.AdjustmentRead),
            new(RevaluationService.DocumentType, InventoryPermissions.CostingRead),
            new(AssemblyService.DocumentType, InventoryPermissions.AssemblyRead),
            new(TransferService.DocumentType, InventoryPermissions.TransferRead),
            new(CountService.DocumentType, InventoryPermissions.CountRead));
        CustomFieldHosts.Register(new CustomFieldHost("stock_transfer", "app.inv_transfers", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost("stock_adjustment", "app.inv_adjustments", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost("lot", "app.inv_lots", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost("serial", "app.inv_serials", "custom_fields"));
        services.AddModuleDbContext<InventoryDbContext>();
        services.AddScoped<WarehouseService>();
        services.AddScoped<IWarehouseDirectory>(static sp => sp.GetRequiredService<WarehouseService>());
        services.AddScoped<StockPostingService>();
        services.AddScoped<IInventoryPosting>(static sp => sp.GetRequiredService<StockPostingService>());
        services.AddScoped<ReservationService>();
        services.AddScoped<IStockReservations>(static sp => sp.GetRequiredService<ReservationService>());
        services.AddScoped<TransferService>();
        services.AddScoped<StockInquiryService>();
        services.AddSingleton(static sp => sp.GetRequiredService<IConfiguration>().GetSection(InventoryOptions.SectionName).Get<InventoryOptions>() ?? new InventoryOptions());
        services.AddScoped<CostingService>();
        services.AddScoped<IInventoryCosting>(static sp => sp.GetRequiredService<CostingService>());
        services.AddScoped<CostInquiryService>();
        services.AddScoped<ReasonCodeService>();
        services.AddScoped<AdjustmentService>();
        services.AddScoped<IWorkflowSubjectProvider, AdjustmentWorkflowSubject>();
        services.AddScoped<RevaluationService>();
        services.AddScoped<AssemblyService>();
        services.AddScoped<TrackingResolver>();
        services.AddScoped<LotService>();
        services.AddScoped<SerialService>();
        services.AddScoped<CountService>();
        services.AddScoped<ReplenishmentService>();
        services.TryAddScoped<IIncomingSupply, NoIncomingSupply>();
        services.AddJobHandler<ReplenishmentJob, ReplenishmentPayload>();
        services.AddJobHandler<LotExpiryJob, LotExpiryPayload>();
        services.AddJobHandler<ReservationExpiryJob, ReservationExpiryPayload>();
        services.AddJobHandler<CostRecostJob, CostRecostPayload>();
        return services;
    }
}
