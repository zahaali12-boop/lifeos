using Quicker.Kernel.Ids;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Purchasing.Application;

/// <summary>What every new tenant starts with: the usual landed-cost charge types, system rows an admin may deactivate but not recode.</summary>
public sealed class PurchasingDefaults(PurchasingDbContext db, IClock clock) : ITenantSetupStep
{
    public Task SetUpAsync(TenantId tenantId, string defaultLanguage, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        foreach (var (code, en, ar, basis) in new[]
        {
            ("FREIGHT", "Freight", "الشحن", "value"),
            ("CUSTOMS", "Customs", "الجمارك", "value"),
            ("DUTY", "Import duty", "الرسوم الجمركية", "value"),
            ("INSURANCE", "Insurance", "التأمين", "value"),
            ("HANDLING", "Handling", "المناولة", "quantity"),
        })
        {
            db.ChargeTypes.Add(new ChargeType { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), DefaultAllocationBasis = basis, IsSystem = true, CreatedAt = now, UpdatedAt = now });
        }

        return db.SaveChangesAsync(cancellationToken);
    }
}
