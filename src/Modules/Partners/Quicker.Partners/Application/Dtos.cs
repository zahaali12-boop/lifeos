using System.Text.Json;

namespace Quicker.Partners.Application;

// ------------------------------------------------------------------ partners

public sealed record SavePartnerRequest(
    string Code,
    IReadOnlyDictionary<string, string> LegalName,
    IReadOnlyDictionary<string, string>? TradeName = null,
    string Kind = "organization",
    bool IsSupplier = false,
    bool IsCustomer = false,
    bool IsEmployee = false,
    Guid? IntercompanyCompanyId = null,
    string DefaultLanguage = "en",
    string? Website = null,
    string? Email = null,
    string? Phone = null,
    Guid? ParentPartnerId = null,
    string? Notes = null,
    JsonElement? CustomFields = null,
    bool IsActive = true);

public sealed record PartnerSummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> LegalName,
    IReadOnlyDictionary<string, string> TradeName,
    string Kind,
    bool IsSupplier,
    bool IsCustomer,
    bool IsEmployee,
    Guid? IntercompanyCompanyId,
    string DefaultLanguage,
    string? Website,
    string? Email,
    string? Phone,
    Guid? ParentPartnerId,
    string? ParentPartnerCode,
    string? Notes,
    JsonElement CustomFields,
    bool IsActive,
    int SupplierCompanies,
    DateTimeOffset UpdatedAt);

public sealed record PartnerDetail(
    PartnerSummary Partner,
    IReadOnlyList<ContactSummary> Contacts,
    IReadOnlyList<AddressSummary> Addresses,
    IReadOnlyList<BankAccountSummary> BankAccounts,
    IReadOnlyList<TaxRegistrationSummary> TaxRegistrations,
    IReadOnlyList<SupplierAccountSummary> SupplierAccounts);

public sealed record SaveContactRequest(IReadOnlyDictionary<string, string> Name, string? Role = null, string? Email = null, string? Phone = null, string? Mobile = null, bool IsPrimary = false, bool ReceivesStatements = false, string? Notes = null, bool IsActive = true);

public sealed record ContactSummary(Guid Id, Guid PartnerId, IReadOnlyDictionary<string, string> Name, string? Role, string? Email, string? Phone, string? Mobile, bool IsPrimary, bool ReceivesStatements, string? Notes, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveAddressRequest(string Role, string Country, JsonElement? Address = null, string? Region = null, bool IsDefault = false);

public sealed record AddressSummary(Guid Id, Guid PartnerId, string Role, JsonElement Address, string Country, string? Region, bool IsDefault, DateTimeOffset UpdatedAt);

/// <summary>Numbers are sent in clear over TLS once and stored only encrypted; omit them on an update to keep what is stored.</summary>
public sealed record SaveBankAccountRequest(string BankName, string Currency, string? AccountHolder = null, string? Branch = null, string? SwiftBic = null, string? AccountNumber = null, string? Iban = null, bool IsDefault = false, bool IsActive = true);

public sealed record BankAccountSummary(Guid Id, Guid PartnerId, string? AccountHolder, string BankName, string? Branch, string? SwiftBic, string Currency, string? AccountNumberMasked, string? IbanMasked, bool IsDefault, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record BankAccountReveal(Guid Id, string? AccountNumber, string? Iban);

public sealed record SaveTaxRegistrationRequest(string Country, string RegistrationType, string Number, DateOnly? ValidFrom = null, DateOnly? ValidTo = null);

public sealed record TaxRegistrationSummary(Guid Id, Guid PartnerId, string Country, string RegistrationType, string Number, DateOnly? ValidFrom, DateOnly? ValidTo, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ supplier accounts

public sealed record SaveSupplierAccountRequest(
    Guid? SupplierGroupId = null,
    Guid? PaymentTermsId = null,
    Guid? DeliveryTermsId = null,
    Guid? PostingGroupId = null,
    Guid? TaxGroupId = null,
    Guid? WhtCodeId = null,
    string? Currency = null,
    int LeadTimeDays = 0,
    decimal PriceTolerancePct = 0m,
    decimal QtyTolerancePct = 0m,
    bool RequiresPo = false,
    bool IsActive = true);

/// <summary>The account as saved plus the effective terms once the group's defaults are applied.</summary>
public sealed record SupplierAccountSummary(
    Guid Id,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid CompanyId,
    string CompanyCode,
    Guid? SupplierGroupId,
    string? SupplierGroupCode,
    Guid? PaymentTermsId,
    Guid? DeliveryTermsId,
    Guid? PostingGroupId,
    Guid? TaxGroupId,
    Guid? WhtCodeId,
    string Currency,
    int LeadTimeDays,
    decimal PriceTolerancePct,
    decimal QtyTolerancePct,
    bool RequiresPo,
    string HoldStatus,
    string? HoldReason,
    DateTimeOffset? HeldAt,
    bool IsActive,
    EffectiveTerms Effective,
    DateTimeOffset UpdatedAt);

public sealed record EffectiveTerms(Guid? PaymentTermsId, string? PaymentTermsCode, Guid? DeliveryTermsId, string? DeliveryTermsCode, Guid? PostingGroupId, string? WhtCode);

public sealed record HoldRequest(string Status, string Reason);

// ------------------------------------------------------------------ configuration

public sealed record SaveSupplierGroupRequest(string Code, IReadOnlyDictionary<string, string> Name, Guid? PostingGroupId = null, Guid? PaymentTermsId = null, Guid? DeliveryTermsId = null, bool IsActive = true);

public sealed record SupplierGroupSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, Guid? PostingGroupId, Guid? PaymentTermsId, Guid? DeliveryTermsId, bool IsActive, int Suppliers, DateTimeOffset UpdatedAt);

public sealed record SavePaymentTermLineRequest(int Sequence, decimal Percentage, int Days);

public sealed record SavePaymentTermsRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string DueBasis = "invoice_date",
    int DueDays = 0,
    decimal EarlyDiscountPct = 0m,
    int EarlyDiscountDays = 0,
    bool BusinessDaysOnly = false,
    IReadOnlyList<SavePaymentTermLineRequest>? Lines = null,
    bool IsActive = true);

public sealed record PaymentTermLineSummary(int Sequence, decimal Percentage, int Days);

public sealed record PaymentTermsSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string DueBasis, int DueDays, decimal EarlyDiscountPct, int EarlyDiscountDays, bool BusinessDaysOnly, IReadOnlyList<PaymentTermLineSummary> Lines, bool IsSystem, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SchedulePreviewRequest(Guid CompanyId, DateOnly InvoiceDate, decimal Amount, string Currency, DateOnly? DeliveryDate = null);

public sealed record SaveDeliveryTermsRequest(string Code, IReadOnlyDictionary<string, string> Name, bool IsActive = true);

public sealed record DeliveryTermsSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, bool IsSystem, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveWhtCodeRequest(string Code, IReadOnlyDictionary<string, string> Name, decimal RatePct, string WithholdAt = "payment", decimal? ThresholdAmount = null, string? ThresholdCurrency = null, bool IsActive = true);

public sealed record WhtCodeSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, decimal RatePct, string WithholdAt, decimal? ThresholdAmount, string? ThresholdCurrency, bool IsActive, DateTimeOffset UpdatedAt);
