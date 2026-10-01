using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Application;
using Quicker.Payables.Contracts;
using Quicker.Payables.Persistence;
using Quicker.Persistence.EntityFramework;
namespace Quicker.Payables;

public static class PayablesModule
{
    public static IServiceCollection AddPayablesModule(this IServiceCollection services)
    {
        // Entries of this module belong to its documents, which keep their own subledger (A-140).
        services.AddSingleton(new PostingDocumentModule("payables"));
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(PayablesPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(PayablesDocumentTypes.Proposal, PayablesPermissions.ProposalRead));
        services.AddModuleDbContext<PayablesDbContext>();
        services.AddScoped<PayablesService>();
        services.AddScoped<SettlementService>();
        services.AddScoped<ProposalService>();
        services.AddScoped<IPayables>(static sp => sp.GetRequiredService<PayablesService>());
        services.AddScoped<IJournalSubledger, PayablesJournalSubledger>();
        services.AddScoped<IPartnerActivitySource, PayablesPartnerActivity>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission("payment_proposal", PayablesPermissions.ProposalRead));
        services.AddScoped<IRecordCompanies, PayablesRecordCompanies>();
        return services;
    }
}
