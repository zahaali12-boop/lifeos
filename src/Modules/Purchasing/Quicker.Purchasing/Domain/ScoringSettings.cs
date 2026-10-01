using Quicker.Persistence.EntityFramework;

namespace Quicker.Purchasing.Domain;

/// <summary>How a company weighs its suppliers (roadmap 4.8): the four scorecard dimensions, the days a receipt may be late and still count as on time, and the look-back window.</summary>
public sealed class ScoringSettings : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public decimal OnTimeWeight { get; set; } = 30m;

    public decimal QuantityWeight { get; set; } = 25m;

    public decimal PriceWeight { get; set; } = 20m;

    public decimal InvoiceWeight { get; set; } = 25m;

    public int OnTimeToleranceDays { get; set; }

    public int LookbackMonths { get; set; } = 12;

    public Guid? UpdatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
