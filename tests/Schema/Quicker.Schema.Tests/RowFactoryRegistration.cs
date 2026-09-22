using System.Runtime.CompilerServices;

namespace Quicker.Schema.Tests;

/// <summary>Registers every module's tenant-row factories before any test runs, so coverage checks see them all.</summary>
internal static class RowFactoryRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register()
    {
        Quicker.Identity.TestSupport.IdentityRowFactories.RegisterAll();
        Quicker.Audit.TestSupport.AuditRowFactories.RegisterAll();
        Quicker.Organization.TestSupport.OrganizationRowFactories.RegisterAll();
        Quicker.Numbering.TestSupport.NumberingRowFactories.RegisterAll();
    }
#pragma warning restore CA2255
}
