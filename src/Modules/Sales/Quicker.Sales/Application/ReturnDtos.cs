using System.Text.Json;

namespace Quicker.Sales.Application;

/// <summary>
/// One shipment line's goods coming back on a sales return (roadmap 5.8a). The quantity is in the item's base unit like
/// the shipment line's own; a serial-tracked line names the serials coming back; a reason code of the inventory
/// catalogue (<c>return</c>) and free text say why; the disposition proposed here is settled at inspection; the bin is
/// where the goods are put away (the shipment's own bin when none is named); the unit cost, in the company's currency,
/// counts only under the <c>specified</c> cost policy.
/// </summary>
public sealed record SaveSalesReturnLineRequest(
    Guid ShipmentLineId,
    decimal Quantity,
    IReadOnlyList<string>? SerialNumbers = null,
    string? ReasonCode = null,
    string? Reason = null,
    string? Disposition = null,
    Guid? BinId = null,
    decimal? UnitCost = null);

public sealed record SaveSalesReturnRequest(
    Guid CompanyId,
    Guid PartnerId,
    IReadOnlyList<SaveSalesReturnLineRequest> Lines,
    Guid? WarehouseId = null,
    DateOnly? RequestDate = null,
    string CostPolicy = "original",
    string? Reason = null,
    string? CustomerReference = null,
    string? Notes = null,
    Guid? BranchId = null,
    JsonElement? CustomFields = null);

public sealed record RejectSalesReturnRequest(string Reason);

public sealed record ReceiveSalesReturnRequest(DateOnly? PostingDate = null);

public sealed record InspectSalesReturnLineRequest(Guid LineId, string Disposition, string? Note = null);

/// <summary>The inspection's verdict per line; the date is the posting date of what it moves (scrap) and defaults to today.</summary>
public sealed record InspectSalesReturnRequest(IReadOnlyList<InspectSalesReturnLineRequest> Lines, DateOnly? PostingDate = null);

public sealed record CreditSalesReturnRequest(DateOnly? DocumentDate = null);

public sealed record ReverseSalesReturnRequest(string Reason, DateOnly? ReversalDate = null);

/// <summary>A shipment ledger entry the return applies to: the entry, its serial and how much of it came back; once
/// received, the return's own inbound entry that brought it back and where it was put (what scrap and reversal apply to).</summary>
public sealed record SalesReturnAppliedEntry(Guid SleId, string? SerialNumber, decimal Quantity, Guid? ReturnSleId = null, Guid? BinId = null, Guid? LotId = null);

public sealed record SalesReturnLineSummary(
    Guid Id,
    int LineNo,
    Guid ShipmentLineId,
    Guid ShipmentId,
    string ShipmentNumber,
    int ShipmentLineNo,
    Guid OrderLineId,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    string Tracking,
    Guid? VariantId,
    decimal Quantity,
    Guid UomId,
    string UomCode,
    Guid? BinId,
    string? LotNumber,
    string? LotStatus,
    bool LotBlocked,
    IReadOnlyList<string> SerialNumbers,
    Guid? ReasonCodeId,
    string? ReasonCode,
    string? Reason,
    string? Disposition,
    string? DispositionNote,
    decimal? UnitCost,
    IReadOnlyList<SalesReturnAppliedEntry> Applied,
    decimal CostAmount,
    Guid? SleId,
    Guid? ScrapSleId,
    decimal ScrapCostAmount,
    decimal QtyCredited,
    decimal NetCredited);

public sealed record SalesReturnCreditNoteRef(Guid Id, string Number, string Status, decimal TotalNet, decimal TotalGross, string Currency);

public sealed record SalesReturnSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    Guid PartnerId,
    string PartnerCode,
    IReadOnlyDictionary<string, string> PartnerName,
    Guid WarehouseId,
    string? WarehouseCode,
    DateOnly RequestDate,
    DateOnly? PostingDate,
    string CostPolicy,
    string? Reason,
    string? CustomerReference,
    string? Currency,
    string FunctionalCurrency,
    decimal TotalCost,
    decimal TotalCredited,
    IReadOnlyList<SalesReturnCreditNoteRef> CreditNotes,
    Guid? StockPostingId,
    Guid? JournalEntryId,
    string? ReversalReason,
    string? RejectionReason,
    string? Notes,
    JsonElement CustomFields,
    IReadOnlyList<SalesReturnLineSummary> Lines,
    DateTimeOffset? AuthorisedAt,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset? InspectedAt,
    DateTimeOffset? CreditedAt,
    DateTimeOffset? ClosedAt,
    DateTimeOffset UpdatedAt);

/// <summary>A posted shipment line of a customer with goods not yet returned: what can still come back, and which serials.</summary>
public sealed record ReturnableSalesLine(
    Guid ShipmentLineId,
    Guid ShipmentId,
    string ShipmentNumber,
    DateOnly ShipmentDate,
    Guid WarehouseId,
    Guid OrderLineId,
    string OrderNumber,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    string Tracking,
    Guid UomId,
    string UomCode,
    decimal Quantity,
    decimal QtyReturned,
    decimal Remaining,
    decimal QtyInvoiced,
    string? LotNumber,
    IReadOnlyList<string> SerialNumbers);
