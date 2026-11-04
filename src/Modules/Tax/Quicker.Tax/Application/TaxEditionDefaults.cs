using System.Text.Json;
using Quicker.Kernel.Ids;
using Quicker.Organization.Contracts;
using Quicker.Tax.Contracts;
using Quicker.Tenancy.Contracts;

namespace Quicker.Tax.Application;

/// <summary>
/// What the edition means for tax (ADR-0035 §17): the trader sells and buys an item that has no tax group, nor a
/// category with one, with no tax; the full edition refuses the document until the item is classified. Written as the
/// organization-wide <see cref="TaxSettingKeys.UnclassifiedItems"/> when a workspace is made in an edition or switched
/// to one, so a company that chose otherwise for itself keeps its own value.
/// </summary>
public sealed class TaxEditionDefaults(ICompanySettings settings) : ITenantEditionStep
{
    public async Task ApplyAsync(TenantId tenantId, string edition, CancellationToken cancellationToken)
    {
        var choice = edition == TenantEditions.Trader ? TaxSettingKeys.Untaxed : TaxSettingKeys.Refuse;
        var saved = await settings.SetAsync(null, TaxSettingKeys.UnclassifiedItems, JsonSerializer.SerializeToElement(choice), "string", cancellationToken);
        if (saved.IsFailure)
        {
            throw new InvalidOperationException($"The tax default for the {edition} edition could not be written: {saved.Error!.Code}");
        }
    }
}
