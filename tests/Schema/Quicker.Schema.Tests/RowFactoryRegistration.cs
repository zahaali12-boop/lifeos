using System.Runtime.CompilerServices;

namespace Quicker.Schema.Tests;

/// <summary>Registers every module's tenant-row factories before any test runs, so coverage checks see them all.</summary>
internal static class RowFactoryRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register()
    {
        // The Dapper convention lives in Quicker.Persistence's module initializer, which only runs once that assembly
        // is loaded; apply it here so the first query of this suite maps snake_case columns whatever the test order.
        Quicker.Persistence.DapperConventions.Apply();
        Quicker.Identity.TestSupport.IdentityRowFactories.RegisterAll();
        Quicker.Audit.TestSupport.AuditRowFactories.RegisterAll();
        Quicker.Organization.TestSupport.OrganizationRowFactories.RegisterAll();
        Quicker.Accounting.TestSupport.AccountingRowFactories.RegisterAll();
        Quicker.Items.TestSupport.ItemsRowFactories.RegisterAll();
        Quicker.Inventory.TestSupport.InventoryRowFactories.RegisterAll();
        Quicker.Workflow.TestSupport.WorkflowRowFactories.RegisterAll();
        Quicker.Partners.TestSupport.PartnersRowFactories.RegisterAll();
        Quicker.Purchasing.TestSupport.PurchasingRowFactories.RegisterAll();
        Quicker.Numbering.TestSupport.NumberingRowFactories.RegisterAll();
        Quicker.Integration.TestSupport.IntegrationRowFactories.RegisterAll();
        Quicker.Collaboration.TestSupport.CollaborationRowFactories.RegisterAll();
    }
#pragma warning restore CA2255
}
