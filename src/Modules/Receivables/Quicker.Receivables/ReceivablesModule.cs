using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Printing.Contracts;
using Quicker.Receivables.Application;
using Quicker.Receivables.Contracts;
using Quicker.Receivables.Persistence;

namespace Quicker.Receivables;

public static class ReceivablesModule
{
    public static IServiceCollection AddReceivablesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Entries of this module belong to its settlements, which keep their own subledger (A-140).
        services.AddSingleton(new PostingDocumentModule("receivables"));
        PermissionCatalog.Register(ReceivablesPermissions.All);
        services.AddModuleDbContext<ReceivablesDbContext>();
        services.AddScoped<ReceivablesService>();
        services.AddScoped<SettlementService>();
        services.AddScoped<WriteOffService>();
        services.AddScoped<DunningService>();
        services.AddScoped<NettingService>();
        services.AddScoped<IPrintModelProvider, StatementPrintModel>();
        NumberedDocumentTypes.Register(new NumberedDocumentType(ReceivablesDocumentTypes.WriteOff, ReceivablesPermissions.OpenItemRead));
        NumberedDocumentTypes.Register(new NumberedDocumentType(ReceivablesDocumentTypes.DunningRun, ReceivablesPermissions.DunningRead));
        NumberedDocumentTypes.Register(new NumberedDocumentType(ReceivablesDocumentTypes.Netting, ReceivablesPermissions.OpenItemRead));
        services.AddScoped<IReceivables, ReceivablesFacade>();
        services.AddScoped<IJournalSubledger, ReceivablesJournalSubledger>();
        services.AddScoped<IPartnerActivitySource, ReceivablesPartnerActivity>();
        return services;
    }
}
