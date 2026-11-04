using Quicker.Messaging.Jobs;

namespace Quicker.Organization.Application;

/// <summary>Tenant job (scheduled or on demand): imports a provider's rates into a rate type, the same way the API does.</summary>
public sealed class RateImportJob(CurrencyService currencies) : IJobHandler<ImportRatesRequest>
{
    public static string JobType => "organization.rates.import";

    public async Task<object?> ExecuteAsync(ImportRatesRequest payload, IJobContext context, CancellationToken cancellationToken)
    {
        if (context.TenantId is null)
        {
            throw new JobFailedException("Rate import runs per tenant; schedule it inside a tenant.");
        }

        var result = await currencies.ImportAsync(payload, cancellationToken);
        if (result.IsFailure)
        {
            // Configuration problems (unknown provider, missing app id, unsupported base) do not fix themselves; feed outages retry.
            throw result.Error!.Code is "rate_provider.unavailable" or "rate_provider.malformed" or "rate_provider.no_rates"
                ? new InvalidOperationException(result.Error.Message)
                : new JobFailedException($"{result.Error.Code}: {result.Error.Message}");
        }

        return result.Value;
    }
}
