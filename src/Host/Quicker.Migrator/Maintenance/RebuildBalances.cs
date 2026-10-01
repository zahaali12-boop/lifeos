using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Accounting.Application;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Migrator.Demo;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Migrator.Maintenance;

/// <summary>
/// <c>rebuild-balances --tenant slug [--company CODE]</c>: truncates the derived GL balances of a tenant (or one
/// company) and recomputes them from the append-only journal lines in one transaction (ADR-0007). Runs the same
/// service the API exposes, as the system actor, and reports the rows before and after.
/// </summary>
internal static class RebuildBalances
{
    public static async Task<int> RunAsync(string ownerConnection, string appConnection, string? tenantSlug, string? companyCode)
    {
        if (string.IsNullOrWhiteSpace(tenantSlug))
        {
            Console.Error.WriteLine("rebuild-balances needs --tenant <slug>.");
            return 2;
        }

        Guid? tenantId;
        await using (var owner = new NpgsqlConnection(ownerConnection))
        {
            await owner.OpenAsync();
            tenantId = await owner.ExecuteScalarAsync<Guid?>("SELECT id FROM control.tenants WHERE slug = @slug", new { slug = tenantSlug.Trim().ToLowerInvariant() });
        }

        if (tenantId is not { } tenant)
        {
            Console.Error.WriteLine($"Tenant '{tenantSlug}' does not exist.");
            return 1;
        }

        using var host = DemoHost.Build(ownerConnection, appConnection, SystemClock.Instance);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var context = TenantContext.System(new TenantId(tenant), "rebuild-balances-" + Guid.CreateVersion7().ToString("N"));
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(context);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = services.GetRequiredService<ITenantContextAccessor>().Use(context);

        Guid? companyId = null;
        if (!string.IsNullOrWhiteSpace(companyCode))
        {
            var company = (await services.GetRequiredService<ICompanyDirectory>().ListAsync()).FirstOrDefault(c => string.Equals(c.Code, companyCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (company is null)
            {
                Console.Error.WriteLine($"Company '{companyCode}' does not exist in tenant '{tenantSlug}'.");
                return 1;
            }

            companyId = company.Id.Value;
        }

        var result = await services.GetRequiredService<JournalService>().RebuildBalancesAsync(companyId, CancellationToken.None);
        if (result.IsFailure)
        {
            Console.Error.WriteLine($"Rebuild failed: {result.Error!.Code} — {result.Error.Message}");
            return 1;
        }

        await unitOfWork.CommitAsync();
        Console.WriteLine($"Rebuilt gl_balances for tenant '{tenantSlug}'{(companyCode is null ? string.Empty : " company " + companyCode)}: {result.Value.RowsBefore} row(s) replaced by {result.Value.RowsAfter}.");
        return 0;
    }
}
