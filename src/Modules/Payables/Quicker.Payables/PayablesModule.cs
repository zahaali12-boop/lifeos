using Microsoft.Extensions.DependencyInjection;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Payables.Application;
using Quicker.Payables.Contracts;
using Quicker.Payables.Persistence;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Payables;

public static class PayablesModule
{
    public static IServiceCollection AddPayablesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(PayablesPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(PayablesDocumentTypes.Proposal, PayablesPermissions.ProposalRead));
        services.AddModuleDbContext<PayablesDbContext>();
        services.AddScoped<PayablesService>();
        services.AddScoped<SettlementService>();
        services.AddScoped<ProposalService>();
        services.AddScoped<IPayables>(static sp => sp.GetRequiredService<PayablesService>());
        return services;
    }
}
