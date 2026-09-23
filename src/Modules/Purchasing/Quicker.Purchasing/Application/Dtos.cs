using System.Text.Json;

namespace Quicker.Purchasing.Application;

// ------------------------------------------------------------------ requisitions

public sealed record SaveRequisitionLineRequest(Guid? ItemId = null, string? ItemCode = null, string? Description = null, decimal Quantity = 0m, string? Uom = null, Guid? UomId = null, decimal? EstimatedPrice = null, Guid? WarehouseId = null, Guid? DimensionSetId = null, Guid? SuggestedSupplierId = null);

public sealed record SaveRequisitionRequest(Guid CompanyId, IReadOnlyList<SaveRequisitionLineRequest> Lines, DateOnly? NeededBy = null, string? Justification = null, Guid? DepartmentValueId = null, Guid? BranchId = null, JsonElement? CustomFields = null);

public sealed record RequisitionLineSummary(Guid Id, int LineNo, Guid? ItemId, string? ItemCode, IReadOnlyDictionary<string, string>? ItemName, string? Description, decimal Quantity, Guid UomId, string UomCode, decimal QuantityBase, decimal? EstimatedPrice, Guid? WarehouseId, Guid? DimensionSetId, Guid? SuggestedSupplierId, string? SuggestedSupplierCode, decimal QtyOrdered, string Status);

public sealed record RequisitionSummary(Guid Id, Guid CompanyId, string Number, string Status, Guid? RequesterMembershipId, string? RequesterName, DateOnly? NeededBy, string? Justification, string Currency, decimal TotalEstimated, Guid? ApprovalRequestId, string? RejectionReason, Guid? DepartmentValueId, JsonElement CustomFields, IReadOnlyList<RequisitionLineSummary> Lines, DateTimeOffset? SubmittedAt, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt);

public sealed record CreateOrdersFromRequisitionRequest(IReadOnlyList<Guid>? LineIds = null, Guid? PartnerId = null, Guid? WarehouseId = null);

public sealed record OrdersCreated(IReadOnlyList<PurchaseOrderSummary> Orders);

// ------------------------------------------------------------------ requests for quotation

public sealed record SaveRfqLineRequest(Guid? ItemId = null, string? ItemCode = null, string? Description = null, decimal Quantity = 0m, string? Uom = null, Guid? UomId = null, Guid? RequisitionLineId = null);

public sealed record SaveRfqRequest(Guid CompanyId, IReadOnlyList<SaveRfqLineRequest> Lines, string? Title = null, DateOnly? DueOn = null, string? Notes = null, IReadOnlyList<Guid>? PartnerIds = null);

public sealed record InviteSuppliersRequest(IReadOnlyList<Guid> PartnerIds);

public sealed record SendRfqRequest(IReadOnlyDictionary<Guid, string>? Emails = null);

public sealed record SaveQuoteLineRequest(Guid RfqLineId, decimal UnitPrice, decimal? Quantity = null, int? LeadTimeDays = null);

public sealed record SaveQuoteRequest(Guid PartnerId, string Currency, IReadOnlyList<SaveQuoteLineRequest> Lines, string? SupplierReference = null, DateOnly? ValidUntil = null, Guid? PaymentTermsId = null, int LeadTimeDays = 0, decimal FreightAmount = 0m, decimal OtherCharges = 0m, string? Notes = null);

public sealed record AwardRequest(Guid QuoteId, Guid? WarehouseId = null);

public sealed record RfqLineSummary(Guid Id, int LineNo, Guid? ItemId, string? ItemCode, IReadOnlyDictionary<string, string>? ItemName, string? Description, decimal Quantity, Guid UomId, string UomCode, decimal QuantityBase, Guid? RequisitionLineId);

public sealed record RfqSupplierSummary(Guid Id, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string? ContactEmail, DateTimeOffset? SentAt, string Status, Guid? QuoteId);

public sealed record QuoteLineSummary(Guid Id, Guid RfqLineId, int RfqLineNo, decimal UnitPrice, decimal Quantity, Guid UomId, string UomCode, int? LeadTimeDays, decimal LineTotal);

public sealed record SupplierQuoteSummary(Guid Id, Guid RfqSupplierId, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string? SupplierReference, string Currency, DateOnly? ValidUntil, Guid? PaymentTermsId, int LeadTimeDays, decimal FreightAmount, decimal OtherCharges, decimal GoodsTotal, decimal LandedTotal, string? Notes, JsonElement? Comparison, DateTimeOffset ReceivedAt, IReadOnlyList<QuoteLineSummary> Lines);

/// <summary>One quote in the comparison: the landed total in the company's currency, the rate used, the rank and why.</summary>
public sealed record QuoteRanking(Guid QuoteId, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string Currency, decimal GoodsTotal, decimal LandedTotal, decimal ExchangeRate, decimal LandedTotalRc, decimal LandedUnitAverageRc, int LeadTimeDays, bool Complete, int Rank, IReadOnlyDictionary<string, decimal> LandedUnitPricesRc);

public sealed record QuoteComparison(Guid RfqId, string Currency, IReadOnlyList<QuoteRanking> Rankings, DateOnly RateDate);

public sealed record RfqSummary(Guid Id, Guid CompanyId, string Number, string? Title, DateOnly? DueOn, string Status, Guid? AwardedQuoteId, string? Notes, IReadOnlyList<RfqLineSummary> Lines, IReadOnlyList<RfqSupplierSummary> Suppliers, IReadOnlyList<SupplierQuoteSummary> Quotes, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ blanket agreements

public sealed record SaveBlanketLineRequest(Guid? ItemId = null, string? ItemCode = null, decimal AgreedQty = 0m, string? Uom = null, Guid? UomId = null, decimal AgreedPrice = 0m);

public sealed record SaveBlanketAgreementRequest(Guid CompanyId, Guid PartnerId, DateOnly ValidFrom, DateOnly ValidTo, IReadOnlyList<SaveBlanketLineRequest> Lines, string? Currency = null, decimal CommittedAmount = 0m, string? Notes = null);

public sealed record BlanketLineSummary(Guid Id, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid UomId, string UomCode, decimal AgreedQty, decimal AgreedPrice, decimal ReleasedQty, decimal RemainingQty);

public sealed record BlanketAgreementSummary(Guid Id, Guid CompanyId, string Number, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, DateOnly ValidFrom, DateOnly ValidTo, string Currency, decimal CommittedAmount, decimal ReleasedAmount, string Status, string? Notes, IReadOnlyList<BlanketLineSummary> Lines, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ purchase orders

public sealed record SavePurchaseOrderLineRequest(Guid? ItemId = null, string? ItemCode = null, Guid? VariantId = null, string? Description = null, decimal Quantity = 0m, string? Uom = null, Guid? UomId = null, decimal? UnitPrice = null, decimal DiscountPct = 0m, DateOnly? ExpectedDate = null, Guid? WarehouseId = null, Guid? DimensionSetId = null, Guid? RequisitionLineId = null, Guid? BlanketLineId = null);

public sealed record SavePurchaseOrderRequest(Guid CompanyId, Guid PartnerId, IReadOnlyList<SavePurchaseOrderLineRequest> Lines, string? Currency = null, DateOnly? OrderDate = null, DateOnly? ExpectedDate = null, Guid? PaymentTermsId = null, Guid? DeliveryTermsId = null, Guid? WarehouseId = null, Guid? AgreementId = null, Guid? BranchId = null, string? Notes = null, JsonElement? CustomFields = null);

/// <summary>A change order: the same shape as a save, plus why; the previous revision is kept and the order goes through approval again.</summary>
public sealed record ChangeOrderRequest(SavePurchaseOrderRequest Order, string Reason);

public sealed record SendOrderRequest(string? To = null, string? Message = null);

public sealed record PurchaseOrderLineSummary(Guid Id, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid? VariantId, string? Description, decimal Quantity, Guid UomId, string UomCode, decimal QuantityBase, decimal UnitPrice, decimal DiscountPct, decimal NetAmount, decimal TaxAmount, DateOnly? ExpectedDate, Guid? WarehouseId, Guid? DimensionSetId, decimal QtyReceived, decimal QtyInvoiced, decimal QtyCancelled, Guid? RequisitionLineId, Guid? BlanketLineId, string Status);

public sealed record PurchaseOrderRevisionSummary(int Revision, string? Reason, Guid? ChangedBy, DateTimeOffset ChangedAt, JsonElement Snapshot);

public sealed record CommitmentSummary(Guid Id, Guid OrderLineId, string AccountRole, Guid? DimensionSetId, string PeriodKey, decimal AmountFc, string Currency, decimal AmountRc, decimal ConsumedRc, string Status);

public sealed record PurchaseOrderSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    int Revision,
    string Status,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    string Currency,
    decimal ExchangeRate,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    Guid? PaymentTermsId,
    string? PaymentTermsCode,
    Guid? DeliveryTermsId,
    string? DeliveryTermsCode,
    Guid? WarehouseId,
    string? WarehouseCode,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    decimal TotalGrossRc,
    Guid? ApprovalRequestId,
    string? RejectionReason,
    Guid? RequisitionId,
    Guid? RfqId,
    Guid? AgreementId,
    string? Notes,
    JsonElement CustomFields,
    DateTimeOffset? SubmittedAt,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? SentAt,
    string? SentTo,
    IReadOnlyList<PurchaseOrderLineSummary> Lines,
    IReadOnlyList<PurchaseOrderRevisionSummary> Revisions,
    IReadOnlyList<CommitmentSummary> Commitments,
    DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ goods receipts

public sealed record SaveReceiptLineRequest(Guid OrderLineId, decimal Quantity, string? Uom = null, Guid? UomId = null, Guid? BinId = null, string? LotNumber = null, DateOnly? ExpiresOn = null, IReadOnlyList<string>? SerialNumbers = null);

public sealed record SaveReceiptRequest(Guid OrderId, IReadOnlyList<SaveReceiptLineRequest> Lines, Guid? WarehouseId = null, DateOnly? PostingDate = null, string? SupplierDeliveryNote = null, string? Notes = null, Guid? BranchId = null, JsonElement? CustomFields = null);

public sealed record ReverseReceiptRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record ReceiptLineSummary(Guid Id, int LineNo, Guid OrderLineId, int OrderLineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid? VariantId, decimal Quantity, Guid UomId, string UomCode, decimal QuantityBase, decimal QtyInOrderUom, Guid? BinId, string? LotNumber, DateOnly? ExpiresOn, IReadOnlyList<string> SerialNumbers, decimal UnitPrice, decimal ExpectedUnitCost, decimal ExpectedCostAmount, decimal QtyInvoiced, decimal QtyReturned, Guid? SleId);

public sealed record ReceiptSummary(Guid Id, Guid CompanyId, string Number, string Status, Guid OrderId, string OrderNumber, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid WarehouseId, string? WarehouseCode, DateOnly PostingDate, string? SupplierDeliveryNote, string Currency, decimal ExchangeRate, string FunctionalCurrency, decimal TotalExpectedCost, Guid? StockPostingId, Guid? JournalEntryId, string? ReversalReason, DateTimeOffset? ReversedAt, string? Notes, JsonElement CustomFields, IReadOnlyList<ReceiptLineSummary> Lines, DateTimeOffset? PostedAt, DateTimeOffset UpdatedAt);

/// <summary>An open order line as the receiving screen sees it: what was ordered, what arrived so far and what the tolerance still allows.</summary>
public sealed record ReceivableLine(Guid OrderId, string OrderNumber, Guid OrderLineId, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, string Tracking, Guid UomId, string UomCode, decimal Ordered, decimal Received, decimal Cancelled, decimal Remaining, decimal MaxReceivable, DateOnly? ExpectedDate, Guid? WarehouseId);
