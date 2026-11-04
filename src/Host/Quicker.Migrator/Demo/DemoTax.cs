using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Tax.Application;

namespace Quicker.Migrator.Demo;

/// <summary>What the tax seed produced.</summary>
public sealed record DemoTaxOutcome(int Regimes, int Registrations);

/// <summary>
/// Demo seed for roadmap 5.3d: the country templates for the tenant's two jurisdictions (Iraq sales tax for IQT and
/// USI, UAE VAT for AEG), and each company registered with its real-looking tax number from <see cref="DemoData"/>.
/// This only sets tax up; the live month's purchasing (<see cref="DemoPurchasing"/>) is seeded before this step runs
/// and so predates the regime, exactly as an ERP going live mid-year would carry unposted history without tax codes.
/// </summary>
internal static class DemoTax
{
    public static async Task<DemoTaxOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var setup = services.GetRequiredService<TaxSetupService>();
        var registeredFrom = new DateOnly(today.Year - 2, 1, 1);
        var regimes = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var registrations = 0;

        async Task<Guid> RegimeForAsync(string templateCode)
        {
            if (regimes.TryGetValue(templateCode, out var existing))
            {
                return existing;
            }

            var installed = Require(await setup.InstallTemplateAsync(templateCode, cancellationToken));
            regimes[templateCode] = installed.Regime.Id;
            return installed.Regime.Id;
        }

        foreach (var (definition, company) in companies)
        {
            var templateCode = definition.Country switch
            {
                "IQ" => "IQ-ST",
                "AE" => "AE-VAT",
                _ => throw new InvalidOperationException($"Demo seed has no tax template for country {definition.Country}."),
            };
            var regimeId = await RegimeForAsync(templateCode);
            Require(await setup.SaveRegistrationAsync(null, new SaveCompanyTaxRegistrationRequest(company.Id, regimeId, definition.TaxId, registeredFrom, IsPrimary: true), cancellationToken));
            registrations++;
        }

        return new DemoTaxOutcome(regimes.Count, registrations);
    }

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
