using Quicker.Items.Contracts;
using Quicker.Partners.Contracts;

namespace Quicker.Pricing.Application;

/// <summary>Names the items, categories, brands, customers, groups and terms pricing records point at, in two lookups per screen.</summary>
public sealed class PricingRefs(IItemDirectory items, IPartnerDirectory partners)
{
    public async Task<RefBook> ReadAsync(IEnumerable<Guid?> catalogIds, IEnumerable<Guid?> partnerIds, CancellationToken cancellationToken)
    {
        var catalog = catalogIds.OfType<Guid>().Distinct().ToList();
        var partnerSide = partnerIds.OfType<Guid>().Distinct().ToList();
        return new RefBook(
            catalog.Count == 0 ? new Dictionary<Guid, CatalogRef>() : await items.DescribeAsync(catalog, cancellationToken),
            partnerSide.Count == 0 ? new Dictionary<Guid, PartnerRecordRef>() : await partners.DescribeAsync(partnerSide, cancellationToken));
    }
}

public sealed class RefBook(IReadOnlyDictionary<Guid, CatalogRef> catalog, IReadOnlyDictionary<Guid, PartnerRecordRef> partners)
{
    public RecordRef? Catalog(Guid? id) => id is { } key && catalog.TryGetValue(key, out var found) ? new RecordRef(found.Id, found.Code, found.Name.Values) : null;

    public RecordRef? Partner(Guid? id) => id is { } key && partners.TryGetValue(key, out var found) ? new RecordRef(found.Id, found.Code, found.Name.Values) : null;
}
