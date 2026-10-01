using Quicker.Persistence.EntityFramework;

namespace Quicker.Numbering.Domain;

/// <summary>app.num_series: how one document type is numbered for a company, optionally a branch and a fiscal year.</summary>
public sealed class Series : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? FiscalYearId { get; set; }

    public string Template { get; set; } = string.Empty;

    public long StartNumber { get; set; } = 1;

    public bool Gapless { get; set; } = true;

    public string ResetPolicy { get; set; } = "never";

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsValidOn(DateOnly date) => (ValidFrom is null || ValidFrom <= date) && (ValidTo is null || ValidTo >= date);
}

public sealed class SeriesCounter : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid SeriesId { get; set; }

    public string PeriodKey { get; set; } = string.Empty;

    public long NextNumber { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Allocation : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid SeriesId { get; set; }

    public string PeriodKey { get; set; } = string.Empty;

    public long Number { get; set; }

    public string Text { get; set; } = string.Empty;

    public string DocumentType { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public Guid? AllocatedBy { get; set; }

    public DateTimeOffset AllocatedAt { get; set; }
}
