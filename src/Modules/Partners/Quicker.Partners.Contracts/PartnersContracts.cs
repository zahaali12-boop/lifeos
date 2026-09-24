using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Partners.Contracts;

public static class PartnerKinds
{
    public const string Organization = "organization";
    public const string Person = "person";

    public static readonly IReadOnlyList<string> All = [Organization, Person];
}

/// <summary>What a supplier account may be held from: purchasing documents, payments, or both.</summary>
public static class HoldStatuses
{
    public const string None = "none";
    public const string Purchase = "purchase";
    public const string Payment = "payment";
    public const string All = "all";

    public static readonly IReadOnlyList<string> Values = [None, Purchase, Payment, All];
}

/// <summary>What a module is about to do with a supplier, so a hold of that kind refuses it.</summary>
public static class SupplierPurposes
{
    public const string Purchase = "purchase";
    public const string Payment = "payment";
}

public static class DueBases
{
    public const string InvoiceDate = "invoice_date";
    public const string EndOfMonth = "end_of_month";
    public const string Delivery = "delivery";

    public static readonly IReadOnlyList<string> All = [InvoiceDate, EndOfMonth, Delivery];
}

public static class WithholdingPoints
{
    public const string Invoice = "invoice";
    public const string Payment = "payment";

    public static readonly IReadOnlyList<string> All = [Invoice, Payment];
}

/// <summary>A partner as documents name it.</summary>
public sealed record PartnerInfo(Guid Id, string Code, LocalizedText LegalName, LocalizedText TradeName, string Kind, bool IsSupplier, bool IsCustomer, bool IsActive, string? Email, string DefaultLanguage, Guid? IntercompanyCompanyId);

/// <summary>A company's supplier terms with the group's defaults applied: what a purchase order, receipt, invoice or payment reads.</summary>
public sealed record SupplierTermsInfo(
    Guid PartnerId,
    string PartnerCode,
    LocalizedText PartnerName,
    Guid CompanyId,
    string Currency,
    Guid? SupplierGroupId,
    Guid? PaymentTermsId,
    string? PaymentTermsCode,
    Guid? DeliveryTermsId,
    string? DeliveryTermsCode,
    Guid? PostingGroupId,
    Guid? TaxGroupId,
    Guid? WhtCodeId,
    int LeadTimeDays,
    decimal PriceTolerancePct,
    decimal QtyTolerancePct,
    bool RequiresPo,
    string HoldStatus,
    string? HoldReason,
    bool IsActive);

public sealed record WhtCodeInfo(Guid Id, string Code, LocalizedText Name, decimal RatePct, string WithholdAt, decimal? ThresholdAmount, string? ThresholdCurrency, bool IsActive);

/// <summary>One instalment of a payment schedule: its share and the amount after the currency's rounding (the parts sum to the whole).</summary>
public sealed record PaymentInstalment(int Sequence, DateOnly DueOn, decimal Percentage, decimal Amount);

public sealed record PaymentSchedule(Guid PaymentTermsId, string Code, DateOnly BaseDate, IReadOnlyList<PaymentInstalment> Instalments, DateOnly? EarlyDiscountUntil, decimal EarlyDiscountPct)
{
    public DateOnly DueOn => Instalments[^1].DueOn;
}

/// <summary>Read access to partners for the modules that buy from and pay them; the supplier side of the master (roadmap 4.1).</summary>
public interface IPartnerDirectory
{
    Task<PartnerInfo?> FindAsync(Guid partnerId, CancellationToken cancellationToken = default);

    Task<PartnerInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>The partner's supplier account in the company with group defaults applied, or null when the partner is not a supplier there.</summary>
    Task<SupplierTermsInfo?> FindSupplierAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken = default);

    /// <summary>The supplier account when it exists, is active and is not held for the purpose; otherwise the business error a document shows (<c>supplier.not_registered</c>, <c>supplier.inactive</c>, <c>supplier.on_hold</c>).</summary>
    Task<Result<SupplierTermsInfo>> EnsureSupplierAsync(Guid companyId, Guid partnerId, string purpose, CancellationToken cancellationToken = default);

    Task<WhtCodeInfo?> FindWhtCodeAsync(Guid whtCodeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The due dates and amounts of payment terms for an amount: instalments after the base date (invoice date, the
    /// end of its month, or the delivery date), moved to the company's next working day when the terms say so, the
    /// amount split by the currency's rounding so the parts sum to the whole.
    /// </summary>
    Task<Result<PaymentSchedule>> ScheduleAsync(Guid companyId, Guid paymentTermsId, DateOnly invoiceDate, DateOnly? deliveryDate, decimal amount, string currency, CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------ customer side (roadmap 5.1)

/// <summary>A customer account's credit standing: on hold sends new orders to a credit hold for release; blocked refuses orders, shipments and invoices.</summary>
public static class CreditStatuses
{
    public const string Ok = "ok";
    public const string OnHold = "on_hold";
    public const string Blocked = "blocked";

    public static readonly IReadOnlyList<string> Values = [Ok, OnHold, Blocked];
}

/// <summary>What a credit limit is measured against: open receivables, or open receivables plus confirmed unbilled orders.</summary>
public static class CreditExposureBases
{
    public const string OpenAr = "open_ar";
    public const string OpenArPlusOrders = "open_ar_plus_orders";

    public static readonly IReadOnlyList<string> All = [OpenAr, OpenArPlusOrders];
}

public static class StatementFrequencies
{
    public const string None = "none";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";

    public static readonly IReadOnlyList<string> All = [None, Weekly, Monthly];
}

/// <summary>What a module is about to do with a customer, so a blocked account refuses it.</summary>
public static class CustomerPurposes
{
    public const string Quote = "quote";
    public const string Order = "order";
    public const string Shipment = "shipment";
    public const string Invoice = "invoice";
    public const string Receipt = "receipt";
}

public static class CommissionBases
{
    public const string Revenue = "revenue";
    public const string Margin = "margin";
    public const string Collected = "collected";

    public static readonly IReadOnlyList<string> All = [Revenue, Margin, Collected];
}

public static class CommissionAccrualPoints
{
    public const string Invoice = "invoice";
    public const string Payment = "payment";

    public static readonly IReadOnlyList<string> All = [Invoice, Payment];
}

public static class CommissionTierPeriods
{
    public const string Month = "month";
    public const string Quarter = "quarter";
    public const string Year = "year";

    public static readonly IReadOnlyList<string> All = [Month, Quarter, Year];
}

/// <summary>Where an opportunity stands: the outcome of its stage.</summary>
public static class OpportunityOutcomes
{
    public const string Open = "open";
    public const string Won = "won";
    public const string Lost = "lost";

    public static readonly IReadOnlyList<string> All = [Open, Won, Lost];
}

public static class CrmActivityKinds
{
    public const string Call = "call";
    public const string Meeting = "meeting";
    public const string Email = "email";
    public const string Task = "task";
    public const string Note = "note";

    public static readonly IReadOnlyList<string> All = [Call, Meeting, Email, Task, Note];
}

public static class CrmActivityStatuses
{
    public const string Open = "open";
    public const string Done = "done";
    public const string Cancelled = "cancelled";
}

/// <summary>A company's customer terms with the group's defaults applied: what a quote, order, shipment, invoice or receipt reads.</summary>
public sealed record CustomerTermsInfo(
    Guid PartnerId,
    string PartnerCode,
    LocalizedText PartnerName,
    Guid CompanyId,
    string Currency,
    Guid? CustomerGroupId,
    Guid? PaymentTermsId,
    string? PaymentTermsCode,
    Guid? DeliveryTermsId,
    string? DeliveryTermsCode,
    Guid? PostingGroupId,
    Guid? TaxGroupId,
    Guid? SalesRepId,
    Guid? DefaultWarehouseId,
    decimal? CreditLimit,
    string CreditExposureBasis,
    int? OverdueBlockDays,
    string CreditStatus,
    string? CreditStatusReason,
    string StatementFrequency,
    int DunningLevel,
    bool IsActive);

public sealed record SalesRepInfo(Guid Id, string Code, LocalizedText Name, Guid? MembershipId, Guid? PartnerId, Guid? CompanyId, Guid? CommissionPlanId, bool IsActive);

/// <summary>One band of a commission: the part of the basis that fell between two thresholds, at its rate.</summary>
public sealed record CommissionBand(decimal FromAmount, decimal? ToAmount, decimal RatePct, decimal Basis, decimal Commission);

/// <summary>
/// What a sale earns under a plan: the scope that matched (the most specific rules for the item's category and the
/// customer's group), the bands the sale crossed given the rep's basis so far in the tier period, and the total rounded
/// in the plan's currency. A negative basis (a credit note) gives back what the same bands earned.
/// </summary>
public sealed record CommissionQuote(Guid PlanId, string PlanCode, string Currency, Guid? MatchedCategoryId, Guid? MatchedCustomerGroupId, IReadOnlyList<CommissionBand> Bands, decimal Commission);

/// <summary>Read access to the customer side of the partner master for sales, receivables and commissions (roadmap 5.1).</summary>
public interface ICustomerDirectory
{
    /// <summary>The partner's customer account in the company with group defaults applied, or null when the partner is not a customer there.</summary>
    Task<CustomerTermsInfo?> FindCustomerAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The customer account when it exists, is active and is not blocked (<c>customer.not_registered</c>,
    /// <c>customer.inactive</c>, <c>customer.credit_blocked</c>); a receipt is always taken. An account on credit
    /// hold is returned: holding the order is the credit check's decision, not a refusal.
    /// </summary>
    Task<Result<CustomerTermsInfo>> EnsureCustomerAsync(Guid companyId, Guid partnerId, string purpose, CancellationToken cancellationToken = default);

    Task<SalesRepInfo?> FindSalesRepAsync(Guid salesRepId, CancellationToken cancellationToken = default);

    /// <summary>The commission a sale earns under the plan (see <see cref="CommissionQuote"/>).</summary>
    Task<Result<CommissionQuote>> QuoteCommissionAsync(Guid planId, Guid? itemCategoryId, Guid? customerGroupId, decimal periodToDate, decimal basis, CancellationToken cancellationToken = default);
}
