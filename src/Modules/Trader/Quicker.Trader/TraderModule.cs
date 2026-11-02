using Microsoft.Extensions.DependencyInjection;
using Quicker.Trader.Application;

namespace Quicker.Trader;

public static class TraderModule
{
    public static IServiceCollection AddTraderModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PricingBoardService>();
        return services;
    }
}
