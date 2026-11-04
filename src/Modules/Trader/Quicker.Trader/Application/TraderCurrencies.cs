using System.Text.Json;
using Quicker.Organization.Contracts;
using Quicker.Trader.Contracts;

namespace Quicker.Trader.Application;

/// <summary>
/// The currencies the trader buys in (ADR-0035 §14): those named in the setting and those seen on shipments so far,
/// never the company's own; the dollar when nothing is named or seen yet and the company is not kept in dollars.
/// </summary>
internal static class TraderCurrencies
{
    private const string Dollar = "USD";

    public static async Task<IReadOnlyList<string>> ForAsync(ICompanySettings settings, Guid companyId, string functionalCurrency, IEnumerable<string> seen, CancellationToken cancellationToken)
    {
        var setting = await settings.GetAsync(companyId, TraderSettingKeys.RatesCurrencies, cancellationToken);
        IEnumerable<string> named = setting is { ValueKind: JsonValueKind.String } s
            ? (s.GetString() ?? string.Empty).Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(static c => c.ToUpperInvariant())
            : [];
        var list = named.Concat(seen)
            .Where(c => c.Length == 3 && !string.Equals(c, functionalCurrency, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (list.Count == 0 && !string.Equals(functionalCurrency, Dollar, StringComparison.Ordinal))
        {
            list.Add(Dollar);
        }

        return list;
    }
}
