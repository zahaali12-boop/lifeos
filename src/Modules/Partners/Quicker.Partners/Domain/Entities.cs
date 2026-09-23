using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Partners.Domain;

/// <summary>app.ptr_partners: one record per legal person, tenant-wide, wearing the customer, supplier and employee roles.</summary>
public sealed class Partner : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText LegalName { get; set; } = new();

    public LocalizedText TradeName { get; set; } = new();

    public string Kind { get; set; } = "organization";

    public bool IsCustomer { get; set; }

    public bool IsSupplier { get; set; }

    public bool IsEmployee { get; set; }

    public Guid? IntercompanyCompanyId { get; set; }

    public string DefaultLanguage { get; set; } = "en";

    public string? Website { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public Guid? ParentPartnerId { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Contact : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PartnerId { get; set; }

    public LocalizedText Name { get; set; } = new();

    public string? Role { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    public string? Mobile { get; set; }

    public bool IsPrimary { get; set; }

    public bool ReceivesStatements { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PartnerAddress : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PartnerId { get; set; }

    /// <summary>billing, shipping, legal or other.</summary>
    public string Role { get; set; } = "legal";

    /// <summary>Structured, bilingual: lines, city, postal code, each a language map.</summary>
    public string Address { get; set; } = "{}";

    public string Country { get; set; } = string.Empty;

    public string? Region { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Numbers live only encrypted (ADR-0025); the masked forms are what lists and prints show.</summary>
public sealed class PartnerBankAccount : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PartnerId { get; set; }

    public string? AccountHolder { get; set; }

    public string BankName { get; set; } = string.Empty;

    public string? Branch { get; set; }

    public string? SwiftBic { get; set; }

    public string Currency { get; set; } = string.Empty;

    public string? AccountNumberEnc { get; set; }

    public string? AccountNumberMasked { get; set; }

    public string? IbanEnc { get; set; }

    public string? IbanMasked { get; set; }

    public bool IsDefault { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PartnerTaxRegistration : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PartnerId { get; set; }

    public string Country { get; set; } = string.Empty;

    /// <summary>vat, tin, crn or other.</summary>
    public string RegistrationType { get; set; } = "vat";

    public string Number { get; set; } = string.Empty;

    public DateOnly? ValidFrom { get; set; }

    public DateOnly? ValidTo { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PaymentTerms : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public string DueBasis { get; set; } = "invoice_date";

    public int DueDays { get; set; }

    public decimal EarlyDiscountPct { get; set; }

    public int EarlyDiscountDays { get; set; }

    public bool BusinessDaysOnly { get; set; }

    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<PaymentTermLine> Lines { get; } = [];
}

public sealed class PaymentTermLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid TermsId { get; set; }

    public int Sequence { get; set; }

    public decimal Percentage { get; set; }

    public int Days { get; set; }
}

public sealed class DeliveryTerms : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public bool IsSystem { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WhtCode : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public decimal RatePct { get; set; }

    /// <summary>invoice or payment.</summary>
    public string WithholdAt { get; set; } = "payment";

    public decimal? ThresholdAmount { get; set; }

    public string? ThresholdCurrency { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class SupplierGroup : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public Guid? PostingGroupId { get; set; }

    public Guid? PaymentTermsId { get; set; }

    public Guid? DeliveryTermsId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.ptr_supplier_accounts: the partner as a supplier of one company.</summary>
public sealed class SupplierAccount : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid PartnerId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? SupplierGroupId { get; set; }

    public Guid? PaymentTermsId { get; set; }

    public Guid? DeliveryTermsId { get; set; }

    public Guid? PostingGroupId { get; set; }

    public Guid? TaxGroupId { get; set; }

    public Guid? WhtCodeId { get; set; }

    public string Currency { get; set; } = string.Empty;

    public int LeadTimeDays { get; set; }

    public decimal PriceTolerancePct { get; set; }

    public decimal QtyTolerancePct { get; set; }

    public bool RequiresPo { get; set; }

    /// <summary>none, purchase, payment or all.</summary>
    public string HoldStatus { get; set; } = "none";

    public string? HoldReason { get; set; }

    public DateTimeOffset? HeldAt { get; set; }

    public Guid? HeldBy { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
