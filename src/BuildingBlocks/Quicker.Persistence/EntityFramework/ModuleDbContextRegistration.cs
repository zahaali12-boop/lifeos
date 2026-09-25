using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Quicker.Persistence.EntityFramework;

public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Registers a module DbContext bound to the current unit of work's connection, with every <see cref="IInterceptor"/>
    /// registered in the container attached (the audit change capture among them). Modules never configure EF
    /// themselves, so the conventions stay identical across modules.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddDbContext<TContext>(static (sp, options) =>
        {
            var unitOfWork = sp.GetRequiredService<IUnitOfWorkAccessor>().Current;
            options.UseNpgsql(unitOfWork.Connection);
            options.AddInterceptors(sp.GetServices<IInterceptor>());
        });
        return services;
    }
}
