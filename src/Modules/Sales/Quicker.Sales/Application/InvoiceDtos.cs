using System.Text.Json;
using Quicker.Receivables.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// One line of an invoice or credit note. <c>shipment</c> and <c>order</c> lines take their price from the order line
/// and their quantity in the item's base unit (like the shipment and the order's fulfilment quantities, A-152);
/// <c>item</c> lines are priced here by the pricing engine (non-stock items only until direct stock invoices ship);
/// <c>service</c> lines are free lines on an account role; <c>invoice</c> lines credit a posted invoice's line.
/// </summary>
public sealed record SaveSalesInvoiceLineRequest(
    string Kind,
    decimal Quantity = 0m,
    Guid? ShipmentLineId = null,
    Guid? OrderLineId = null,
    Guid? InvoiceLineId = null,
    Guid? ItemId = null,
    string? ItemCode = null,
    Guid? VariantId = null,
    string? Uom = null,
    Guid? UomId = null,
    decimal? UnitPrice = null,
    decimal DiscountPct = 0m,
    string? AccountRole = null,
    string? Description = null,
    Guid? TaxCodeId = null,
    IReadOnlyDictionary<string, Guid>? Dimensions = null,
    int? DeferralPeriods = null,
    DateOnly? DeferralStartsOn = null);

public sealed record SaveSalesInvoiceRequest(
    Guid CompanyId,
    Guid PartnerId,
    IReadOnlyList<SaveSalesInvoiceLineRequest> Lines,
    string Kind = "invoice",
    string? CustomerReference = null,
    Guid? CreditsInvoiceId = null,
    DateOnly? DocumentDate = null,
    DateOnly? PostingDate = null,
    string? Currency = null,
    Guid? PaymentTermsId = null,
    Guid? SalesRepId = null,
    Guid? PriceListId = null,
    Guid? BranchId = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

public sealed record ReverseSalesInvoiceRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record CancelSalesInvoiceRequest(string Reason);

public sealed record SalesInvoiceLineSummary(
    Guid Id,
    int LineNo,
    string Kind,
    Guid? ShipmentLineId,
    string? ShipmentNumber,
    Guid? OrderLineId,
    string? OrderNumber,
    Guid? InvoiceLineId,
    string? CreditedInvoiceNumber,
    Guid? ItemId,
    string? ItemCode,
    IReadOnlyDictionary<string, string>? ItemName,
    Guid? VariantId,
    string? AccountRole,
    string? Description,
    decimal Quantity,
    Guid? UomId,
    string? UomCode,
    decimal QuantityBase,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal DiscountAmount,
    decimal NetAmount,
    decimal TaxAmount,
    decimal QtyCredited,
    Guid? TaxCodeId,
    string? TaxCode,
    decimal TaxRatePct,
    bool TaxReverseCharge,
    bool TaxRecoverable,
    string? TaxReason,
    IReadOnlyDictionary<string, Guid>? Dimensions,
    int? DeferralPeriods = null,
    DateOnly? DeferralStartsOn = null,
    Guid? DeferralScheduleId = null);

/// <summary>What a sales rep earned on a line of the invoice under their plan, in the plan's currency.</summary>
public sealed record CommissionEntrySummary(Guid Id, Guid InvoiceLineId, int LineNo, Guid SalesRepId, Guid PlanId, string Basis, decimal BasisAmount, decimal CommissionAmount, string Currency, string Status, Guid? JournalEntryId);

public sealed record SalesInvoiceSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Kind,
    string Status,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    string? CustomerReference,
    Guid? CreditsInvoiceId,
    string? CreditsInvoiceNumber,
    DateOnly DocumentDate,
    DateOnly PostingDate,
    DateOnly? DueDate,
    string Currency,
    decimal ExchangeRate,
    string FunctionalCurrency,
    Guid? PaymentTermsId,
    string? PaymentTermsCode,
    Guid? SalesRepId,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    string TaxRoundingLevel,
    bool PricesIncludeTax,
    Guid? JournalEntryId,
    Guid? ReversalEntryId,
    string? ReversalReason,
    string EinvoiceStatus,
    string? Notes,
    JsonElement CustomFields,
    IReadOnlyList<SalesInvoiceLineSummary> Lines,
    IReadOnlyList<ReceivableInfo> OpenItems,
    DateTimeOffset? PostedAt,
    DateTimeOffset UpdatedAt,
    string? EinvoiceScheme = null,
    string? EinvoiceSubmissionId = null,
    string? EinvoiceQr = null,
    IReadOnlyList<string>? EinvoiceMessages = null,
    DateTimeOffset? EinvoiceSubmittedAt = null,
    Guid? RecurringInvoiceId = null,
    IReadOnlyList<CommissionEntrySummary>? Commissions = null,
    Guid? ReturnId = null);

// ------------------------------------------------------------------ recurring invoices (5.6b)

public sealed record SaveRecurringInvoiceRequest(
    Guid CompanyId,
    Guid PartnerId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Cron,
    IReadOnlyList<SaveSalesInvoiceLineRequest> Lines,
    string? Currency = null,
    DateOnly? StartsOn = null,
    DateOnly? EndsOn = null,
    string? CustomerReference = null,
    Guid? PaymentTermsId = null,
    bool AutoPost = false,
    bool IsActive = true,
    string? Notes = null);

public sealed record GenerateRecurringInvoiceRequest(DateOnly? RunDate = null);

public sealed record RecurringInvoiceSummary(
    Guid Id,
    Guid CompanyId,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Cron,
    string TimeZone,
    DateOnly? NextRunOn,
    DateOnly? EndsOn,
    string Currency,
    IReadOnlyList<SaveSalesInvoiceLineRequest> Lines,
    string? CustomerReference,
    Guid? PaymentTermsId,
    bool AutoPost,
    bool IsActive,
    DateOnly? LastGeneratedOn,
    string? Notes,
    int Generated,
    DateTimeOffset UpdatedAt);

/// <summary>One recurring invoice's run: generated (the invoice), or waiting with the problem that stopped it.</summary>
public sealed record RecurringInvoiceRun(Guid RecurringInvoiceId, string Code, DateOnly RunDate, string Outcome, Guid? InvoiceId, string? InvoiceNumber, string? Problem);

/// <summary>What can still be invoiced to a customer: posted shipment lines with an uninvoiced quantity and open service lines of its orders; quantities in the item's base unit.</summary>
public sealed record InvoicableSalesLine(
    string Kind,
    Guid? ShipmentLineId,
    string? ShipmentNumber,
    Guid? ShipmentId,
    Guid OrderLineId,
    string OrderNumber,
    Guid OrderId,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid UomId,
    string UomCode,
    decimal Quantity,
    decimal QtyInvoiced,
    decimal Remaining,
    decimal UnitPrice,
    decimal DiscountPct,
    string Currency,
    DateOnly? PostingDate);
