using System.Text.Json;
using Quicker.Partners.Contracts;

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
    int CustomerCompanies,
    DateTimeOffset UpdatedAt);

public sealed record PartnerDetail(
    PartnerSummary Partner,
    IReadOnlyList<ContactSummary> Contacts,
    IReadOnlyList<AddressSummary> Addresses,
    IReadOnlyList<BankAccountSummary> BankAccounts,
    IReadOnlyList<TaxRegistrationSummary> TaxRegistrations,
    IReadOnlyList<SupplierAccountSummary> SupplierAccounts,
    IReadOnlyList<CustomerAccountSummary> CustomerAccounts);

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

// ------------------------------------------------------------------ customer accounts (roadmap 5.1)

/// <summary>
/// The partner as a customer of one company. Blank terms and posting default from the group. The credit fields
/// (limit in the company's functional currency, empty for none; exposure basis; overdue days that block) change only
/// with <c>partners.credit.manage</c>; send them as they stand otherwise.
/// </summary>
public sealed record SaveCustomerAccountRequest(
    Guid? CustomerGroupId = null,
    Guid? PaymentTermsId = null,
    Guid? DeliveryTermsId = null,
    Guid? PostingGroupId = null,
    Guid? TaxGroupId = null,
    Guid? SalesRepId = null,
    Guid? DefaultWarehouseId = null,
    string? Currency = null,
    decimal? CreditLimit = null,
    string CreditExposureBasis = "open_ar_plus_orders",
    int? OverdueBlockDays = null,
    string StatementFrequency = "monthly",
    bool IsActive = true);

public sealed record CustomerAccountSummary(
    Guid Id,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid CompanyId,
    string CompanyCode,
    string FunctionalCurrency,
    Guid? CustomerGroupId,
    string? CustomerGroupCode,
    Guid? PaymentTermsId,
    Guid? DeliveryTermsId,
    Guid? PostingGroupId,
    Guid? TaxGroupId,
    Guid? SalesRepId,
    string? SalesRepCode,
    Guid? DefaultWarehouseId,
    string Currency,
    decimal? CreditLimit,
    string CreditExposureBasis,
    int? OverdueBlockDays,
    string CreditStatus,
    string? CreditStatusReason,
    DateTimeOffset? CreditStatusAt,
    string StatementFrequency,
    int DunningLevel,
    bool IsActive,
    EffectiveCustomerTerms Effective,
    DateTimeOffset UpdatedAt);

public sealed record EffectiveCustomerTerms(Guid? PaymentTermsId, string? PaymentTermsCode, Guid? DeliveryTermsId, string? DeliveryTermsCode, Guid? PostingGroupId);

/// <summary>status on_hold (new orders wait for release) or blocked (orders, shipments and invoices refused), with a reason; ok releases.</summary>
public sealed record CreditStatusRequest(string Status, string? Reason = null);

public sealed record SaveCustomerGroupRequest(string Code, IReadOnlyDictionary<string, string> Name, Guid? PostingGroupId = null, Guid? PaymentTermsId = null, Guid? DeliveryTermsId = null, bool IsActive = true);

public sealed record CustomerGroupSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, Guid? PostingGroupId, Guid? PaymentTermsId, Guid? DeliveryTermsId, bool IsActive, int Customers, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ sales reps and commission plans

public sealed record SaveSalesRepRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid? MembershipId = null,
    Guid? PartnerId = null,
    Guid? CompanyId = null,
    Guid? CommissionPlanId = null,
    string? Email = null,
    string? Phone = null,
    bool IsActive = true);

public sealed record SalesRepSummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid? MembershipId,
    string? MemberName,
    Guid? PartnerId,
    string? PartnerCode,
    Guid? CompanyId,
    string? CompanyCode,
    Guid? CommissionPlanId,
    string? CommissionPlanCode,
    string? Email,
    string? Phone,
    bool IsActive,
    int Customers,
    int OpenOpportunities,
    DateTimeOffset UpdatedAt);

/// <summary>One band: for sales in the item category (and its descendants) and/or the customer group, from a period-to-date amount upwards.</summary>
public sealed record SaveCommissionRuleRequest(decimal RatePct, decimal FromAmount = 0m, Guid? ItemCategoryId = null, Guid? CustomerGroupId = null);

public sealed record SaveCommissionPlanRequest(
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Currency,
    string Basis = "revenue",
    string AccrualPoint = "invoice",
    string TierPeriod = "month",
    IReadOnlyList<SaveCommissionRuleRequest>? Rules = null,
    bool IsActive = true);

public sealed record CommissionRuleSummary(int Sequence, Guid? ItemCategoryId, string? ItemCategoryCode, Guid? CustomerGroupId, string? CustomerGroupCode, decimal FromAmount, decimal RatePct);

public sealed record CommissionPlanSummary(
    Guid Id,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Basis,
    string AccrualPoint,
    string TierPeriod,
    string Currency,
    IReadOnlyList<CommissionRuleSummary> Rules,
    bool IsActive,
    int SalesReps,
    DateTimeOffset UpdatedAt);

public sealed record CommissionQuoteRequest(decimal Amount, decimal PeriodToDate = 0m, Guid? ItemCategoryId = null, Guid? CustomerGroupId = null);

// ------------------------------------------------------------------ pipeline

public sealed record SavePipelineStageRequest(string Code, IReadOnlyDictionary<string, string> Name, int DefaultProbability, string Outcome = "open", int? SortOrder = null, bool IsActive = true);

public sealed record PipelineStageSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, int SortOrder, int DefaultProbability, string Outcome, bool IsSystem, bool IsActive, int OpenOpportunities, DateTimeOffset UpdatedAt);

/// <summary>The stages in the order the board shows them; every stage of the tenant, each once.</summary>
public sealed record ReorderStagesRequest(IReadOnlyList<Guid> StageIds);

public sealed record CreateOpportunityRequest(
    Guid CompanyId,
    Guid PartnerId,
    string Title,
    decimal ExpectedAmount = 0m,
    string? Currency = null,
    Guid? StageId = null,
    int? ProbabilityPct = null,
    Guid? ContactId = null,
    Guid? SalesRepId = null,
    DateOnly? ExpectedClose = null,
    string? Source = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

public sealed record UpdateOpportunityRequest(
    string Title,
    decimal ExpectedAmount,
    string Currency,
    int ProbabilityPct,
    Guid? ContactId = null,
    Guid? SalesRepId = null,
    DateOnly? ExpectedClose = null,
    string? Source = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

/// <summary>Moves the opportunity to a stage; a lost stage needs the reason; the probability defaults to the stage's.</summary>
public sealed record MoveOpportunityRequest(Guid StageId, int? ProbabilityPct = null, string? LostReason = null);

public sealed record OpportunitySummary(
    Guid Id,
    Guid CompanyId,
    string CompanyCode,
    string Number,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid? ContactId,
    string Title,
    Guid StageId,
    string StageCode,
    IReadOnlyDictionary<string, string> StageName,
    Guid? SalesRepId,
    string? SalesRepCode,
    decimal ExpectedAmount,
    string Currency,
    int ProbabilityPct,
    decimal WeightedAmount,
    DateOnly? ExpectedClose,
    bool IsOverdue,
    string? Source,
    string Status,
    string? LostReason,
    DateOnly? ClosedOn,
    string? Notes,
    JsonElement CustomFields,
    DateTimeOffset StageSince,
    DateTimeOffset UpdatedAt);

public sealed record StageChangeSummary(Guid Id, Guid? FromStageId, string? FromStageCode, Guid ToStageId, string ToStageCode, int ProbabilityPct, decimal ExpectedAmount, DateTimeOffset ChangedAt, Guid? ChangedBy);

public sealed record OpportunityDetail(OpportunitySummary Opportunity, IReadOnlyList<StageChangeSummary> StageHistory, IReadOnlyList<CrmActivitySummary> Activities);

/// <summary>A total of opportunities in one currency: how many, their expected amount and the probability-weighted amount.</summary>
public sealed record PipelineTotal(string Currency, int Count, decimal Amount, decimal Weighted);

public sealed record PipelineColumn(PipelineStageSummary Stage, IReadOnlyList<OpportunitySummary> Opportunities, IReadOnlyList<PipelineTotal> Totals);

/// <summary>The board: every active stage in order with its opportunities (won and lost columns show the last 90 days) and the open pipeline's totals.</summary>
public sealed record PipelineBoard(IReadOnlyList<PipelineColumn> Columns, IReadOnlyList<PipelineTotal> OpenTotals);

// ------------------------------------------------------------------ CRM activities

public sealed record SaveCrmActivityRequest(
    Guid PartnerId,
    string Kind,
    string Subject,
    string? Body = null,
    DateTimeOffset? DueAt = null,
    Guid? AssignedMembershipId = null,
    Guid? OpportunityId = null,
    Guid? ContactId = null,
    Guid? CompanyId = null);

public sealed record CompleteCrmActivityRequest(string? Outcome = null);

public sealed record CrmActivitySummary(
    Guid Id,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid? CompanyId,
    Guid? OpportunityId,
    string? OpportunityNumber,
    Guid? ContactId,
    string Kind,
    string Subject,
    string? Body,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    Guid? AssignedMembershipId,
    string? AssignedName,
    string Status,
    string? Outcome,
    DateTimeOffset? CompletedAt,
    Guid? CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ Customer 360

/// <summary>
/// The partner at a glance: its record with contacts, addresses and the accounts the member may read; its pipeline;
/// open and recent activities; and each module's balances and latest documents with it.
/// </summary>
public sealed record Customer360(
    PartnerDetail Partner,
    Customer360Pipeline Pipeline,
    IReadOnlyList<CrmActivitySummary> OpenActivities,
    IReadOnlyList<CrmActivitySummary> RecentActivities,
    IReadOnlyList<PartnerActivityPanel> Panels);

/// <summary>Open opportunities with their totals per currency, those closed in the last year, and the win rate over them.</summary>
public sealed record Customer360Pipeline(
    IReadOnlyList<OpportunitySummary> Open,
    IReadOnlyList<PipelineTotal> OpenTotals,
    IReadOnlyList<OpportunitySummary> RecentlyClosed,
    int WonLastYear,
    int LostLastYear,
    decimal? WinRatePct);
