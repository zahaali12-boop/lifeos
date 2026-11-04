using Microsoft.Extensions.DependencyInjection;
using Quicker.Onboarding.Application;

namespace Quicker.Onboarding;

public static class OnboardingModule
{
    /// <summary>No tables of its own: a status read model and imports that drive other modules through their contracts.</summary>
    public static IServiceCollection AddOnboardingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<SetupStatusService>();
        services.AddScoped<OpeningBalanceService>();
        return services;
    }
}
