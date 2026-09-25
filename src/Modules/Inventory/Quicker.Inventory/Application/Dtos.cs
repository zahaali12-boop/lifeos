using System.Text.Json;

namespace Quicker.Inventory.Application;

// ------------------------------------------------------------------ warehouses and bins

public sealed record SaveWarehouseRequest(
    Guid CompanyId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind = "standard",
    Guid? BranchId = null,
    bool BinsEnabled = false,
    bool? AllowNegativeStock = null,
    IReadOnlyDictionary<string, string>? Address = null,
    bool IsActive = true);

public sealed record WarehouseSummary(
    Guid Id,
    Guid CompanyId,
    Guid? BranchId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    string Kind,
    bool BinsEnabled,
    bool? AllowNegativeStock,
    IReadOnlyDictionary<string, string> Address,
    bool IsActive,
    int BinCount,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<BinSummary>? Bins = null);

public sealed record SaveBinRequest(string Code, string? Zone = null, string Kind = "storage", int PickSequence = 0, bool IsActive = true);

public sealed record BinSummary(Guid Id, Guid WarehouseId, string Code, string? Zone, string Kind, int PickSequence, bool IsActive, DateTimeOffset UpdatedAt);

// ------------------------------------------------------------------ stock

public sealed record StockBalanceRow(
    Guid CompanyId,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    string? VariantSku,
    Guid WarehouseId,
    string WarehouseCode,
    Guid? BinId,
    string? BinCode,
    Guid? LotId,
    Guid? SerialId,
    string BaseUom,
    decimal OnHand,
    decimal Reserved,
    decimal QualityHold,
    decimal Available,
    DateTimeOffset? LastMovementAt,
    string? LotNumber = null,
    DateOnly? LotExpiresOn = null,
    string? LotStatus = null,
    string? SerialNumber = null);

/// <summary>One row per item and warehouse for the global stock search: what is there, what is promised, what can be taken.</summary>
public sealed record StockSearchRow(
    Guid CompanyId,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid WarehouseId,
    string WarehouseCode,
    IReadOnlyDictionary<string, string> WarehouseName,
    string BaseUom,
    decimal OnHand,
    decimal Reserved,
    decimal QualityHold,
    decimal Available,
    DateTimeOffset? LastMovementAt);

public sealed record StockLedgerRow(
    Guid Id,
    long Sequence,
    Guid PostingId,
    Guid CompanyId,
    Guid ItemId,
    string ItemCode,
    Guid? VariantId,
    Guid WarehouseId,
    string WarehouseCode,
    Guid? BinId,
    string? BinCode,
    Guid? LotId,
    Guid? SerialId,
    string EntryType,
    decimal Quantity,
    string BaseUom,
    decimal EnteredQuantity,
    string EnteredUom,
    DateOnly PostingDate,
    string SourceDocumentType,
    Guid SourceDocumentId,
    Guid? SourceLineId,
    Guid? TransferPairId,
    Guid? ReservationId,
    Guid? PostedBy,
    DateTimeOffset PostedAt,
    string? LotNumber = null,
    string? SerialNumber = null,
    Guid? PartnerId = null);

// ------------------------------------------------------------------ reservations

public sealed record ReserveStockRequest(
    Guid CompanyId,
    Guid ItemId,
    decimal Quantity,
    Guid WarehouseId,
    string SourceDocumentType,
    Guid SourceDocumentId,
    Guid? SourceLineId = null,
    string? Uom = null,
    Guid? UomId = null,
    Guid? VariantId = null,
    Guid? BinId = null,
    Guid? LotId = null,
    Guid? SerialId = null,
    DateOnly? ExpiresOn = null,
    string? Reason = null);

public sealed record ReleaseReservationRequest(string? Reason = null);

// ------------------------------------------------------------------ transfers

public sealed record SaveTransferRequest(
    Guid CompanyId,
    Guid FromWarehouseId,
    Guid ToWarehouseId,
    IReadOnlyList<SaveTransferLineRequest> Lines,
    Guid? TransitWarehouseId = null,
    string? Reference = null,
    string? Notes = null,
    JsonElement? CustomFields = null,
    string Kind = "two_step");

public sealed record SaveTransferLineRequest(
    Guid? ItemId = null,
    string? ItemCode = null,
    decimal Quantity = 0m,
    string? Uom = null,
    Guid? UomId = null,
    Guid? VariantId = null,
    Guid? FromBinId = null,
    Guid? ToBinId = null,
    Guid? LotId = null,
    string? LotNumber = null,
    IReadOnlyList<string>? SerialNumbers = null);

public sealed record ShipTransferRequest(DateOnly? ShipDate = null, IReadOnlyList<TransferQuantityRequest>? Lines = null);

public sealed record ReceiveTransferRequest(DateOnly? ReceiveDate = null, IReadOnlyList<TransferQuantityRequest>? Lines = null);

/// <summary>
/// A quantity for one line of a ship or receive, in the line's unit; lines left out take everything outstanding. On a
/// receipt, <paramref name="Shortage"/> is what shipped but never arrived: it is written off from transit with the reason code.
/// </summary>
public sealed record TransferQuantityRequest(Guid LineId, decimal Quantity, Guid? ToBinId = null, decimal Shortage = 0m, Guid? ShortageReasonCodeId = null, string? ShortageReasonCode = null, string? ShortageNote = null, IReadOnlyList<string>? SerialNumbers = null, IReadOnlyList<string>? ShortageSerialNumbers = null);

public sealed record TransferSummary(
    Guid Id,
    Guid CompanyId,
    string Number,
    string Status,
    string Kind,
    Guid FromWarehouseId,
    string FromWarehouseCode,
    Guid ToWarehouseId,
    string ToWarehouseCode,
    Guid? TransitWarehouseId,
    string? TransitWarehouseCode,
    DateOnly? ShipDate,
    DateOnly? ReceiveDate,
    Guid? ShipPostingId,
    Guid? ReceivePostingId,
    IReadOnlyList<Guid> ShortagePostingIds,
    string? Reference,
    string? Notes,
    JsonElement CustomFields,
    IReadOnlyList<TransferLineSummary> Lines,
    DateTimeOffset UpdatedAt);

public sealed record TransferLineSummary(
    Guid Id,
    int LineNo,
    Guid ItemId,
    string ItemCode,
    IReadOnlyDictionary<string, string> ItemName,
    Guid? VariantId,
    string? VariantSku,
    decimal QtyRequested,
    decimal QtyShipped,
    decimal QtyReceived,
    decimal QtyShortage,
    Guid UomId,
    string UomCode,
    decimal BaseQtyRequested,
    string BaseUom,
    Guid? FromBinId,
    Guid? ToBinId,
    Guid? LotId = null,
    string? LotNumber = null,
    IReadOnlyList<string>? SerialNumbers = null);
