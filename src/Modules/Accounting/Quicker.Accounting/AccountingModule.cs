using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Application;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Accounting;

public static class AccountingModule
{
    public static IServiceCollection AddAccountingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(AccountingPermissions.All);
        services.AddModuleDbContext<AccountingDbContext>();
        services.AddScoped<ChartService>();
        services.AddScoped<IChartOfAccounts>(static sp => sp.GetRequiredService<ChartService>());
        services.AddScoped<ProfileService>();
        services.AddScoped<IPostingGroupDirectory>(static sp => sp.GetRequiredService<ProfileService>());
        services.AddScoped<PostingService>();
        services.AddScoped<IPostingService>(static sp => sp.GetRequiredService<PostingService>());
        services.AddScoped<JournalService>();
        services.AddScoped<ManualJournalService>();
        services.AddScoped<RecurringService>();
        services.AddScoped<DeferralService>();
        services.AddScoped<AccountingRoutines>();
        services.AddScoped<InquiryService>();
        services.AddJobHandler<AccountingDailyJob, AccountingDailyPayload>();
        return services;
    }
}
