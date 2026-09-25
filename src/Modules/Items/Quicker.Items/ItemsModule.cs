using Microsoft.Extensions.DependencyInjection;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Application;
using Quicker.Items.Contracts;
using Quicker.Items.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Items;

public static class ItemsModule
{
    public static IServiceCollection AddItemsModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(ItemsPermissions.All);
        CustomFieldHosts.Register(new CustomFieldHost("item", "app.itm_items", "custom_fields"));
        services.AddModuleDbContext<ItemsDbContext>();
        services.AddScoped<CategoryService>();
        services.AddScoped<MasterDataService>();
        services.AddScoped<ItemService>();
        services.AddScoped<BomService>();
        services.AddScoped<IBomDirectory>(static sp => sp.GetRequiredService<BomService>());
        services.AddScoped<ItemDirectory>();
        services.AddScoped<IItemDirectory>(static sp => sp.GetRequiredService<ItemDirectory>());
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission("item", ItemsPermissions.ItemRead));
        return services;
    }
}
