using Quicker.Kernel.Ids;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Organization.Application;

/// <summary>
/// What every new tenant starts with: a standard fiscal calendar, the regional working weeks, the system
/// dimensions, the system rate types (plus Iraq's official/market pair, Q7), and the common units.
/// </summary>
public sealed class OrganizationDefaults(OrganizationDbContext db, IClock clock) : ITenantSetupStep
{
    public const string BranchDimension = "BRANCH";
    public const string DefaultFiscalCalendar = "standard";
    public const string DefaultBusinessCalendar = "sun_thu";

    public async Task SetUpAsync(TenantId tenantId, string defaultLanguage, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;

        db.FiscalCalendars.Add(new FiscalCalendar { Id = Guid.CreateVersion7(), Code = DefaultFiscalCalendar, Name = LocalizedText.Bilingual("Calendar year (January–December)", "السنة الميلادية (يناير – ديسمبر)"), StartMonth = 1, PeriodsPerYear = 12, IsSystem = true, CreatedAt = now, UpdatedAt = now });

        db.BusinessCalendars.Add(new BusinessCalendar { Id = Guid.CreateVersion7(), Code = DefaultBusinessCalendar, Name = LocalizedText.Bilingual("Sunday to Thursday", "الأحد إلى الخميس"), WorkingDays = [0, 1, 2, 3, 4], IsSystem = true, CreatedAt = now, UpdatedAt = now });
        db.BusinessCalendars.Add(new BusinessCalendar { Id = Guid.CreateVersion7(), Code = "mon_fri", Name = LocalizedText.Bilingual("Monday to Friday", "الاثنين إلى الجمعة"), WorkingDays = [1, 2, 3, 4, 5], IsSystem = true, CreatedAt = now, UpdatedAt = now });

        var dimensions = new (string Code, string En, string Ar, bool Hierarchical)[]
        {
            (BranchDimension, "Branch", "الفرع", false),
            ("COST_CENTER", "Cost centre", "مركز التكلفة", true),
            ("DEPARTMENT", "Department", "القسم", true),
            ("PROJECT", "Project", "المشروع", false),
        };
        for (var i = 0; i < dimensions.Length; i++)
        {
            var (code, en, ar, hierarchical) = dimensions[i];
            db.Dimensions.Add(new Dimension { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), IsSystem = true, IsHierarchical = hierarchical, SortOrder = i + 1, CreatedAt = now, UpdatedAt = now });
        }

        var rateTypes = new (string Code, string En, string Ar, bool System)[]
        {
            (RateTypes.Spot, "Spot", "فوري", true),
            (RateTypes.Closing, "Closing", "إقفال", true),
            (RateTypes.Average, "Average", "متوسط", true),
            (RateTypes.Budget, "Budget", "موازنة", true),
            ("official", "Official (central bank)", "رسمي (البنك المركزي)", false),
            ("market", "Market", "السوق", false),
        };
        foreach (var (code, en, ar, system) in rateTypes)
        {
            db.RateTypes.Add(new ExchangeRateType { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), IsSystem = system, CreatedAt = now, UpdatedAt = now });
        }

        var units = new Dictionary<string, Uom>(StringComparer.Ordinal);
        foreach (var (code, en, ar, family, precision) in new (string, string, string, string, int)[]
        {
            ("PCS", "Piece", "قطعة", "count", 0),
            ("DZ", "Dozen", "دزينة", "count", 0),
            ("CTN", "Carton", "كرتون", "count", 0),
            ("PLT", "Pallet", "منصة", "count", 0),
            ("KG", "Kilogram", "كيلوغرام", "weight", 3),
            ("G", "Gram", "غرام", "weight", 3),
            ("TON", "Tonne", "طن", "weight", 3),
            ("L", "Litre", "لتر", "volume", 3),
            ("ML", "Millilitre", "مليلتر", "volume", 3),
            ("M", "Metre", "متر", "length", 3),
            ("CM", "Centimetre", "سنتيمتر", "length", 3),
            ("M2", "Square metre", "متر مربع", "area", 3),
            ("HR", "Hour", "ساعة", "time", 2),
            ("DAY", "Day", "يوم", "time", 0),
        })
        {
            var uom = new Uom { Id = Guid.CreateVersion7(), Code = code, Name = LocalizedText.Bilingual(en, ar), Family = family, Precision = precision, IsSystem = true, CreatedAt = now, UpdatedAt = now };
            units[code] = uom;
            db.Uoms.Add(uom);
        }

        foreach (var (from, to, factor) in new (string, string, decimal)[] { ("DZ", "PCS", 12m), ("KG", "G", 1000m), ("TON", "KG", 1000m), ("L", "ML", 1000m), ("M", "CM", 100m), ("DAY", "HR", 24m) })
        {
            db.UomConversions.Add(new UomConversionRow { Id = Guid.CreateVersion7(), FromUomId = units[from].Id, ToUomId = units[to].Id, Numerator = factor, Denominator = 1m });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
