using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Application;
using Quicker.Numbering.Contracts;
using Quicker.Numbering.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Numbering;

public static class NumberingModule
{
    public static IServiceCollection AddNumberingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(NumberingPermissions.All);
        services.AddModuleDbContext<NumberingDbContext>();
        services.AddScoped<SeriesService>();
        services.AddScoped<DocumentSearchService>();
        services.AddScoped<IIssuedNumbers, IssuedNumbers>();
        services.AddScoped<NumberAllocator>();
        services.AddScoped<INumberAllocator>(static sp => sp.GetRequiredService<NumberAllocator>());
        return services;
    }
}
