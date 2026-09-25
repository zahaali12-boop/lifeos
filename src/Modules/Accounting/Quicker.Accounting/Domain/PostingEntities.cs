using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Accounting.Domain;

public sealed class PostingGroup : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PostingProfile : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public int Version { get; set; } = 1;

    public DateOnly ValidFrom { get; set; }

    public string Status { get; set; } = "draft";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PostingRule> Rules { get; } = [];
}

public sealed class PostingRule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid ProfileId { get; set; }

    public string AccountRole { get; set; } = string.Empty;

    public string? DocumentType { get; set; }

    public Guid? ItemPostingGroupId { get; set; }

    public Guid? PartnerPostingGroupId { get; set; }

    public Guid? TaxCodeId { get; set; }

    public Guid? WarehouseId { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? BankAccountId { get; set; }

    public Guid? AssetCategoryId { get; set; }

    public Guid? ChargeTypeId { get; set; }

    public Guid AccountId { get; set; }

    public int Specificity { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class JournalEntry : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Number { get; set; } = string.Empty;

    public DateOnly PostingDate { get; set; }

    public DateOnly DocumentDate { get; set; }

    public Guid FiscalYearId { get; set; }

    public Guid FiscalPeriodId { get; set; }

    public string SourceModule { get; set; } = string.Empty;

    public string SourceDocumentType { get; set; } = string.Empty;

    public Guid SourceDocumentId { get; set; }

    public string? SourceDocumentNumber { get; set; }

    public LocalizedText Description { get; set; } = new();

    public bool IsReversal { get; set; }

    public bool IsAutoReversal { get; set; }

    public DateOnly? AutoReverseOn { get; set; }

    public bool IsClosingEntry { get; set; }

    public bool IsOpeningEntry { get; set; }

    public bool IsManual { get; set; }

    public string CurrencyTc { get; set; } = string.Empty;

    public string CurrencyFc { get; set; } = string.Empty;

    public string? CurrencyRc { get; set; }

    public string RateType { get; set; } = "spot";

    public decimal RateTcFc { get; set; }

    public decimal? RateFcRc { get; set; }

    public string? RateOverrideReason { get; set; }

    public Guid? PostingProfileId { get; set; }

    public int LineCount { get; set; }

    public Guid? PostedBy { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public string? IdempotencyKey { get; set; }

    public List<JournalLine> Lines { get; } = [];
}

public sealed class JournalLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid EntryId { get; set; }

    public Guid CompanyId { get; set; }

    public DateOnly PostingDate { get; set; }

    public int LineNo { get; set; }

    public Guid AccountId { get; set; }

    public string AccountRole { get; set; } = string.Empty;

    public Guid? PostingRuleId { get; set; }

    public decimal DebitTc { get; set; }

    public decimal CreditTc { get; set; }

    public string CurrencyTc { get; set; } = string.Empty;

    public decimal RateTcFc { get; set; }

    public decimal DebitFc { get; set; }

    public decimal CreditFc { get; set; }

    public decimal? RateFcRc { get; set; }

    public decimal DebitRc { get; set; }

    public decimal CreditRc { get; set; }

    public string RateType { get; set; } = "spot";

    public DateOnly RateDate { get; set; }

    public Guid? DimensionSetId { get; set; }

    public Guid? BranchId { get; set; }

    public Guid? PartnerId { get; set; }

    public string? SubledgerType { get; set; }

    public Guid? SubledgerRef { get; set; }

    public Guid? TaxCodeId { get; set; }

    public decimal? TaxBaseTc { get; set; }

    public LocalizedText Description { get; set; } = new();

    public DateOnly? DueDate { get; set; }

    public bool IsRounding { get; set; }
}

public sealed class EntryLink : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid FromEntryId { get; set; }

    public Guid ToEntryId { get; set; }

    public string Relation { get; set; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class EntryRelations
{
    public const string Reverses = "reverses";
    public const string Corrects = "corrects";
    public const string AutoReversalOf = "auto_reversal_of";
    public const string ClosingOf = "closing_of";
}
