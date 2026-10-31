using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Einvoicing.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Pos.Application;
using Quicker.Pos.Contracts;
using Quicker.Pos.Persistence;
using Quicker.Printing.Contracts;

namespace Quicker.Pos;

public static class PosModule
{
    public static IServiceCollection AddPosModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Entries of this module belong to its orders and sessions, which keep their subledgers through Banking, Receivables and Inventory (A-140).
        services.AddSingleton(new PostingDocumentModule("pos"));
        PermissionCatalog.Register(PosPermissions.All);
        NumberedDocumentTypes.Register(new NumberedDocumentType(PosDocumentTypes.Order, PosPermissions.OrderRead), new NumberedDocumentType(PosDocumentTypes.Session, PosPermissions.SessionRead));
        services.AddModuleDbContext<PosDbContext>();
        services.AddScoped<RegisterService>();
        services.AddScoped<SessionService>();
        services.AddScoped<OrderService>();
        services.AddScoped<IEinvoiceSource, PosEinvoiceSource>();
        services.AddScoped<IPrintModelProvider, ReceiptPrintModel>();
        services.AddJobHandler<PosEinvoiceJob, PosEinvoicePayload>();
        // Comments, files, history and links on these records are shown to those who may read the records.
        services.AddSingleton(new RecordReadPermission(RegisterService.AuditEntity, PosPermissions.RegisterRead));
        services.AddSingleton(new RecordReadPermission(SessionService.AuditEntity, PosPermissions.SessionRead));
        services.AddSingleton(new RecordReadPermission(OrderService.AuditEntity, PosPermissions.OrderRead));
        services.AddScoped<IRecordCompanies, PosRecordCompanies>();
        return services;
    }
}
