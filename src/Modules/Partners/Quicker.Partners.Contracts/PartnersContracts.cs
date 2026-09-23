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
