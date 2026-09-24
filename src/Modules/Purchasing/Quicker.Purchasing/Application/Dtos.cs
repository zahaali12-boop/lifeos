using System.Text.Json;
using Quicker.Payables.Contracts;

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

// ------------------------------------------------------------------ supplier invoices

/// <summary>A line: against a posted receipt line (three-way), an order line without a receipt (services, two-way) or a free expense line on an account role; quantity and price in the invoice currency.</summary>
/// <summary>An invoice line. <paramref name="Dimensions"/> (dimension code to value) or a <paramref name="DimensionSetId"/> assigns the line to cost centres and projects; a line against an order line inherits the order line's when it gives none.</summary>
public sealed record SaveInvoiceLineRequest(string Kind, decimal Quantity, decimal UnitPrice, Guid? ReceiptLineId = null, Guid? OrderLineId = null, string? AccountRole = null, string? Description = null, decimal DiscountPct = 0m, Guid? DimensionSetId = null, Guid? LandedCostChargeId = null, Guid? ReturnLineId = null, IReadOnlyDictionary<string, Guid>? Dimensions = null);

public sealed record SaveInvoiceRequest(Guid CompanyId, Guid PartnerId, IReadOnlyList<SaveInvoiceLineRequest> Lines, string Kind = "invoice", string? SupplierInvoiceNumber = null, DateOnly? DocumentDate = null, DateOnly? PostingDate = null, string? Currency = null, Guid? PaymentTermsId = null, Guid? WhtCodeId = null, bool ApplyWht = true, string? Notes = null, Guid? BranchId = null, JsonElement? CustomFields = null);

public sealed record ReverseInvoiceRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record InvoiceLineSummary(Guid Id, int LineNo, string Kind, Guid? ReceiptLineId, string? ReceiptNumber, Guid? OrderLineId, string? OrderNumber, Guid? ItemId, string? ItemCode, IReadOnlyDictionary<string, string>? ItemName, string? AccountRole, string? Description, decimal Quantity, Guid? UomId, string? UomCode, decimal UnitPrice, decimal DiscountPct, decimal NetAmount, decimal TaxAmount, decimal WhtAmount, decimal? ExpectedUnitPrice, decimal? PriceVariancePct, decimal? QtyVariance, Guid? DimensionSetId, Guid? LandedCostChargeId = null, string? LandedCostNumber = null, Guid? ReturnLineId = null, string? ReturnNumber = null, IReadOnlyDictionary<string, Guid>? Dimensions = null);

public sealed record MatchResultSummary(Guid Id, string Status, decimal PriceTolerancePct, decimal QtyTolerancePct, decimal PriceVarianceAmount, decimal PriceVariancePct, decimal QtyVariance, JsonElement Details, Guid? OverrideId, DateTimeOffset MatchedAt);

public sealed record InvoiceSummary(Guid Id, Guid CompanyId, string Number, string Kind, string Status, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string? SupplierInvoiceNumber, DateOnly DocumentDate, DateOnly PostingDate, DateOnly? DueDate, string Currency, decimal ExchangeRate, string FunctionalCurrency, Guid? PaymentTermsId, string? PaymentTermsCode, Guid? WhtCodeId, string? WhtCode, decimal TotalNet, decimal TotalTax, decimal TotalWht, decimal TotalGross, decimal TotalPayable, string? BlockKind, string? BlockReason, Guid? BlockId, Guid? ApprovalRequestId, string? RejectionReason, Guid? JournalEntryId, Guid? ReversalEntryId, string? ReversalReason, string? Notes, JsonElement CustomFields, IReadOnlyList<InvoiceLineSummary> Lines, IReadOnlyList<MatchResultSummary> Matches, IReadOnlyList<OpenItemInfo> OpenItems, DateTimeOffset? SubmittedAt, DateTimeOffset? PostedAt, DateTimeOffset UpdatedAt);

/// <summary>What can still be invoiced for a supplier: posted receipt lines with an uninvoiced quantity and open service lines of orders.</summary>
public sealed record InvoicableLine(string Kind, Guid? ReceiptLineId, string? ReceiptNumber, Guid? OrderLineId, string? OrderNumber, Guid? OrderId, int LineNo, Guid? ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, Guid? UomId, string UomCode, decimal Quantity, decimal QtyInvoiced, decimal Remaining, decimal UnitPrice, string Currency, DateOnly? PostingDate, Guid? LandedCostChargeId = null, string? LandedCostNumber = null, Guid? ReturnLineId = null, string? ReturnNumber = null, Guid? ReceiptId = null, Guid? ReturnId = null, Guid? LandedCostId = null);

// ------------------------------------------------------------------ landed costs

public sealed record SaveChargeTypeRequest(string Code, IReadOnlyDictionary<string, string>? Name, string DefaultAllocationBasis = "value", bool IsActive = true);

public sealed record ChargeTypeSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Name, string DefaultAllocationBasis, bool IsSystem, bool IsActive, DateTimeOffset UpdatedAt);

public sealed record SaveLandedCostChargeRequest(Guid ChargeTypeId, decimal Amount, Guid? PartnerId = null, string? Description = null, string? AllocationBasis = null);

public sealed record SaveLandedCostRequest(Guid CompanyId, IReadOnlyList<SaveLandedCostChargeRequest> Charges, IReadOnlyList<Guid> ReceiptLineIds, DateOnly? PostingDate = null, string? Currency = null, string? Reference = null, string? Notes = null, JsonElement? CustomFields = null);

public sealed record ReverseLandedCostRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record LandedCostChargeSummary(Guid Id, int LineNo, Guid ChargeTypeId, string ChargeTypeCode, Guid? PartnerId, string? PartnerCode, string? Description, decimal Amount, decimal AmountFc, string AllocationBasis, bool IsEstimate, Guid? SupplierInvoiceLineId, decimal InvoicedAmountFc);

public sealed record LandedCostAllocationSummary(Guid Id, Guid ChargeId, int ChargeLineNo, string ChargeTypeCode, Guid ReceiptLineId, string ReceiptNumber, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal ReceivedQuantity, string UomCode, decimal BasisValue, decimal AllocatedAmountFc, decimal OnHandPortionFc, decimal SoldPortionFc);

public sealed record LandedCostSummary(Guid Id, Guid CompanyId, string Number, string Status, DateOnly PostingDate, string Currency, decimal ExchangeRate, string FunctionalCurrency, decimal TotalAmount, decimal TotalAmountFc, decimal OnHandPortionFc, decimal SoldPortionFc, string? Reference, string? Notes, string? ReversalReason, JsonElement CustomFields, IReadOnlyList<LandedCostChargeSummary> Charges, IReadOnlyList<LandedCostAllocationSummary> Allocations, DateTimeOffset? PostedAt, DateTimeOffset UpdatedAt);

/// <summary>A posted receipt line a landed cost can be allocated to, with the basis values it would count with.</summary>
public sealed record AllocatableReceiptLine(Guid ReceiptLineId, string ReceiptNumber, Guid ReceiptId, DateOnly PostingDate, Guid PartnerId, string PartnerCode, int LineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal Quantity, string UomCode, decimal QuantityBase, decimal ValueFc, decimal? WeightKg, decimal? VolumeM3);

// ------------------------------------------------------------------ supplier returns and settlements

public sealed record SaveReturnLineRequest(Guid ReceiptLineId, decimal Quantity, string? Uom = null, Guid? UomId = null, Guid? BinId = null, string? LotNumber = null, IReadOnlyList<string>? SerialNumbers = null, string? Reason = null);

public sealed record SaveReturnRequest(Guid ReceiptId, IReadOnlyList<SaveReturnLineRequest> Lines, DateOnly? PostingDate = null, string? Reason = null, string? SupplierRma = null, string? Notes = null, JsonElement? CustomFields = null);

public sealed record ReverseReturnRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record ReturnLineSummary(Guid Id, int LineNo, Guid ReceiptLineId, int ReceiptLineNo, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, decimal Quantity, Guid UomId, string UomCode, decimal QuantityBase, Guid? BinId, string? LotNumber, IReadOnlyList<string> SerialNumbers, string? Reason, decimal CostAmountFc, decimal CreditedAmountFc, decimal QtyCredited, Guid? SleId);

public sealed record ReturnSummary(Guid Id, Guid CompanyId, string Number, string Status, Guid ReceiptId, string ReceiptNumber, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid WarehouseId, string? WarehouseCode, DateOnly PostingDate, string Currency, string FunctionalCurrency, string? Reason, string? SupplierRma, decimal TotalCostFc, Guid? StockPostingId, string? ReversalReason, string? Notes, JsonElement CustomFields, IReadOnlyList<ReturnLineSummary> Lines, DateTimeOffset? PostedAt, DateTimeOffset UpdatedAt);

/// <summary>A posted receipt line with something left to return, and what has already gone back.</summary>
public sealed record ReturnableLine(Guid ReceiptId, string ReceiptNumber, Guid ReceiptLineId, int LineNo, Guid PartnerId, string PartnerCode, Guid ItemId, string ItemCode, IReadOnlyDictionary<string, string> ItemName, string Tracking, Guid UomId, string UomCode, decimal Received, decimal Returned, decimal Remaining, string? LotNumber, IReadOnlyList<string> SerialNumbers, DateOnly PostingDate, Guid WarehouseId);

