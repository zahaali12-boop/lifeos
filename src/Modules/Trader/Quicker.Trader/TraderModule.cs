using Microsoft.Extensions.DependencyInjection;
using Quicker.Trader.Application;

namespace Quicker.Trader;

public static class TraderModule
{
    public static IServiceCollection AddTraderModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<PricingBoardService>();
        services.AddScoped<HealthService>();
        services.AddScoped<OwnerMoneyService>();
        services.AddScoped<DailyRatesService>();
        return services;
    }
}
