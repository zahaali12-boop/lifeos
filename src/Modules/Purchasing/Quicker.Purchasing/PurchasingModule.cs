using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Numbering.Contracts;
using Quicker.Persistence.EntityFramework;
using Quicker.Purchasing.Application;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Persistence;
using Quicker.Tenancy.Contracts;
using Quicker.Workflow.Contracts;

namespace Quicker.Purchasing;

public static class PurchasingModule
{
    public static IServiceCollection AddPurchasingModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        PermissionCatalog.Register(PurchasingPermissions.All);
        NumberedDocumentTypes.Register(
            new(PurchaseDocumentTypes.Requisition, PurchasingPermissions.RequisitionRead),
            new(PurchaseDocumentTypes.Rfq, PurchasingPermissions.RfqRead),
            new(PurchaseDocumentTypes.BlanketAgreement, PurchasingPermissions.AgreementRead),
            new(PurchaseDocumentTypes.Order, PurchasingPermissions.OrderRead),
            new(PurchaseDocumentTypes.Receipt, PurchasingPermissions.ReceiptRead),
            new(PurchaseDocumentTypes.Invoice, PurchasingPermissions.InvoiceRead),
            new(PurchaseDocumentTypes.LandedCost, PurchasingPermissions.LandedCostRead),
            new(PurchaseDocumentTypes.Return, PurchasingPermissions.ReturnRead));
        CustomFieldHosts.Register(new CustomFieldHost(PurchaseDocumentTypes.Requisition, "app.pur_requisitions", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost(PurchaseDocumentTypes.Order, "app.pur_orders", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost(PurchaseDocumentTypes.Receipt, "app.pur_receipts", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost(PurchaseDocumentTypes.Invoice, "app.pur_invoices", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost(PurchaseDocumentTypes.LandedCost, "app.pur_landed_cost_docs", "custom_fields"));
        CustomFieldHosts.Register(new CustomFieldHost(PurchaseDocumentTypes.Return, "app.pur_returns", "custom_fields"));
        services.AddModuleDbContext<PurchasingDbContext>();
        services.AddScoped<RequisitionService>();
        services.AddScoped<DocumentFlowService>();
        services.AddScoped<RfqService>();
        services.AddScoped<BlanketAgreementService>();
        services.AddScoped<PurchaseOrderService>();
        services.AddScoped<ReceiptService>();
        services.AddScoped<InvoiceService>();
        services.AddScoped<LandedCostService>();
        services.AddScoped<ReturnService>();
        services.AddScoped<SupplierIntelligenceService>();
        services.AddScoped<ITenantSetupStep, PurchasingDefaults>();
        services.AddScoped<IPurchaseReceiptDirectory>(static sp => sp.GetRequiredService<ReceiptService>());
        services.AddScoped<PurchasingSupply>();
        services.AddScoped<IPurchaseOrderDirectory>(static sp => sp.GetRequiredService<PurchasingSupply>());
        // The planner's incoming supply: purchase orders replace the inventory module's empty default.
        services.Replace(ServiceDescriptor.Scoped<IIncomingSupply>(static sp => sp.GetRequiredService<PurchasingSupply>()));
        services.AddScoped<IWorkflowSubjectProvider, RequisitionWorkflowSubject>();
        services.AddScoped<IWorkflowSubjectProvider, PurchaseOrderWorkflowSubject>();
        services.AddScoped<IWorkflowSubjectProvider, InvoiceWorkflowSubject>();
        // Comments, files, history and links on purchasing documents are shown to those who may read the documents.
        foreach (var (documentType, permission) in DocumentFlowService.ReadPermissions)
        {
            services.AddSingleton(new RecordReadPermission(documentType, permission));
        }

        return services;
    }
}
