using Microsoft.Extensions.DependencyInjection;
using Quicker.Banking.Application;
using Quicker.Banking.Contracts;
using Quicker.Banking.Persistence;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Banking;

public static class BankingModule
{
    public static IServiceCollection AddBankingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(BankingPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(BankDocumentTypes.Payment, BankingPermissions.PaymentRead));
        CustomFieldHosts.Register(new CustomFieldHost(BankDocumentTypes.Payment, "app.bnk_payments", "custom_fields"));
        services.AddModuleDbContext<BankingDbContext>();
        services.AddScoped<BankAccountService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<IBankAccountDirectory>(static sp => sp.GetRequiredService<BankAccountService>());
        return services;
    }
}
