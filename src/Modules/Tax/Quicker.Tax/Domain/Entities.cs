using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Tax.Domain;

/// <summary>app.tax_regimes: a country's tax as the tenant runs it, loaded from a template and then its own.</summary>
public sealed class TaxRegime : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public string Country { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Family { get; set; } = "vat";

    public string RoundingLevel { get; set; } = "line";

    public string TaxPoint { get; set; } = "invoice";

    public string ReturnFrequency { get; set; } = "quarterly";

    public string? EinvoicingScheme { get; set; }

    public string? TemplateCode { get; set; }

    public string? TemplateVersion { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.tax_codes: a code of a regime with its treatment, accounts and return boxes; rates are dated rows.</summary>
public sealed class TaxCode : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RegimeId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string Kind { get; set; } = "vat";

    public string Treatment { get; set; } = "standard";

    public bool IsRecoverable { get; set; } = true;

    public bool IsReverseCharge { get; set; }

    public string AppliesTo { get; set; } = "both";

    public string? ExemptionReasonCode { get; set; }

    public LocalizedText ExemptionReason { get; set; } = new();

    public string OutputAccountRole { get; set; } = "OutputTax";

    public string InputAccountRole { get; set; } = "InputTax";

    public string? SalesBaseBox { get; set; }

    public string? SalesTaxBox { get; set; }

    public string? PurchaseBaseBox { get; set; }

    public string? PurchaseTaxBox { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<TaxRate> Rates { get; set; } = [];
}

public sealed class TaxRate : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid TaxCodeId { get; set; }

    public DateOnly ValidFrom { get; set; }

    public decimal RatePct { get; set; }
}

/// <summary>app.tax_groups: an item or partner tax group the determination matrix pairs.</summary>
public sealed class TaxGroup : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Kind { get; set; } = "item";

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.tax_determination_rules: one cell of the determination matrix.</summary>
public sealed class TaxRule : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid RegimeId { get; set; }

    public string Direction { get; set; } = "sales";

    public Guid? ItemTaxGroupId { get; set; }

    public Guid? PartnerTaxGroupId { get; set; }

    public string? ShipFromCountry { get; set; }

    public string? ShipToCountry { get; set; }

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public Guid TaxCodeId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.tax_registrations: a company registered in a regime.</summary>
public sealed class TaxRegistration : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RegimeId { get; set; }

    public string? RegistrationNumber { get; set; }

    public DateOnly? RegisteredFrom { get; set; }

    public bool IsPrimary { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.tax_exemptions: a partner's certificate that puts its lines on an exempt code.</summary>
public sealed class TaxExemption : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PartnerId { get; set; }

    public Guid RegimeId { get; set; }

    public Guid TaxCodeId { get; set; }

    public string CertificateNumber { get; set; } = string.Empty;

    public DateOnly ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.tax_return_periods: a return period, locked once filed.</summary>
public sealed class TaxReturnPeriod : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RegimeId { get; set; }

    public DateOnly PeriodStart { get; set; }

    public DateOnly PeriodEnd { get; set; }

    public string Status { get; set; } = "open";

    public DateTimeOffset? FiledAt { get; set; }

    public Guid? FiledBy { get; set; }

    public string? Reference { get; set; }

    public string Totals { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.tax_entries: the tax ledger, append-only.</summary>
public sealed class TaxEntry : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RegimeId { get; set; }

    public Guid TaxCodeId { get; set; }

    public string Direction { get; set; } = "sales";

    public DateOnly PostingDate { get; set; }

    public DateOnly? DocumentDate { get; set; }

    public string SourceModule { get; set; } = string.Empty;

    public string SourceDocumentType { get; set; } = string.Empty;

    public Guid SourceDocumentId { get; set; }

    public string? SourceDocumentNumber { get; set; }

    public Guid? SourceLineRef { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? PartnerId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public decimal RatePct { get; set; }

    public decimal BaseTc { get; set; }

    public decimal TaxTc { get; set; }

    public decimal BaseFc { get; set; }

    public decimal TaxFc { get; set; }

    public bool IsReverseCharge { get; set; }

    public bool IsRecoverable { get; set; } = true;

    public Guid? ReversesEntryId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
