using System.Text.Json;

namespace Quicker.Sales.Application;

public sealed record SaveQuotationLineRequest(
    Guid? ItemId = null,
    string? ItemCode = null,
    Guid? VariantId = null,
    string? Description = null,
    decimal Quantity = 0m,
    string? Uom = null,
    Guid? UomId = null,
    decimal? UnitPrice = null,
    decimal DiscountPct = 0m,
    Guid? TaxCodeId = null);

public sealed record SaveQuotationRequest(
    Guid CompanyId,
    Guid PartnerId,
    IReadOnlyList<SaveQuotationLineRequest> Lines,
    string? Currency = null,
    DateOnly? QuoteDate = null,
    DateOnly? ValidUntil = null,
    DateOnly? PricingDate = null,
    Guid? PriceListId = null,
    Guid? OpportunityId = null,
    Guid? BranchId = null,
    string? Notes = null,
    JsonElement? CustomFields = null);

public sealed record RejectQuotationRequest(string Reason);

public sealed record QuotationLineSummary(
    Guid Id,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    string? Description,
    decimal Quantity,
    Guid UomId,
    string UomCode,
    decimal QuantityBase,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal NetAmount,
    decimal TaxAmount,
    Guid? PromotionId,
    string? PromotionCode,
    Guid? TaxCodeId = null,
    string? TaxCode = null,
    decimal TaxRatePct = 0m,
    bool TaxReverseCharge = false,
    bool TaxRecoverable = true,
    string? TaxReason = null);

public sealed record QuotationSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid? OpportunityId,
    string Currency,
    DateOnly QuoteDate,
    DateOnly? ValidUntil,
    DateOnly PricingDate,
    Guid? PriceListId,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    string? RejectionReason,
    string? Notes,
    JsonElement CustomFields,
    DateTimeOffset? SentAt,
    DateTimeOffset? DecidedAt,
    Guid? OrderId,
    IReadOnlyList<QuotationLineSummary> Lines,
    DateTimeOffset UpdatedAt,
    bool IsExpired = false);
