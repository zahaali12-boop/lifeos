using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Messaging;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Sales.Application;
using Quicker.Sales.Persistence;
using Quicker.Workflow.Contracts;

namespace Quicker.Sales;

public static class SalesModule
{
    public static IServiceCollection AddSalesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(SalesPermissions.All);
        services.AddModuleDbContext<SalesDbContext>();
        services.AddScoped<QuotationService>();
        services.AddScoped<OrderService>();
        services.AddScoped<ShipmentService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<RecurringInvoiceService>();
        services.AddScoped<ReturnService>();
        services.AddJobHandler<RecurringInvoiceJob, RecurringInvoicePayload>();
        // Entries of this module belong to its invoices, which keep their own subledger (A-140).
        services.AddSingleton(new PostingDocumentModule("sales"));
        NumberedDocumentTypes.Register(
            new(InvoiceService.DocumentType, SalesPermissions.InvoiceRead),
            new(InvoiceService.CreditNoteNumberingType, SalesPermissions.InvoiceRead),
            new(ReturnService.DocumentType, SalesPermissions.ReturnRead));
        services.AddScoped<IWorkflowSubjectProvider, SalesOrderWorkflowSubject>();
        return services;
    }
}
