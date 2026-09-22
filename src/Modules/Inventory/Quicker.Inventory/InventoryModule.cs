using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Application;
using Quicker.Inventory.Contracts;
using Quicker.Inventory.Persistence;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(InventoryPermissions.All);
        CustomFieldHosts.Register(new CustomFieldHost("stock_transfer", "app.inv_transfers", "custom_fields"));
        services.AddModuleDbContext<InventoryDbContext>();
        services.AddScoped<WarehouseService>();
        services.AddScoped<IWarehouseDirectory>(static sp => sp.GetRequiredService<WarehouseService>());
        services.AddScoped<StockPostingService>();
        services.AddScoped<IInventoryPosting>(static sp => sp.GetRequiredService<StockPostingService>());
        services.AddScoped<ReservationService>();
        services.AddScoped<IStockReservations>(static sp => sp.GetRequiredService<ReservationService>());
        services.AddScoped<TransferService>();
        services.AddScoped<StockInquiryService>();
        services.AddJobHandler<ReservationExpiryJob, ReservationExpiryPayload>();
        return services;
    }
}
