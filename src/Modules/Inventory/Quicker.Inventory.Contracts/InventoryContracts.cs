using Quicker.Kernel.Events;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Inventory.Contracts;

/// <summary>Stock ledger entry types (DOMAIN_MODEL §9.1) and the sign each carries in the base unit.</summary>
public static class StockEntryTypes
{
    public const string PurchaseReceipt = "purchase_receipt";
    public const string PurchaseReturn = "purchase_return";
    public const string SaleShipment = "sale_shipment";
    public const string SaleReturn = "sale_return";
    public const string TransferOut = "transfer_out";
    public const string TransferIn = "transfer_in";
    public const string PositiveAdjustment = "positive_adjustment";
    public const string NegativeAdjustment = "negative_adjustment";
    public const string Scrap = "scrap";
    public const string CountVariance = "count_variance";
    public const string AssemblyConsumption = "assembly_consumption";
    public const string AssemblyOutput = "assembly_output";
    public const string ConsignmentIn = "consignment_in";
    public const string ConsignmentOut = "consignment_out";
    public const string DropShip = "drop_ship";
    public const string Opening = "opening";

    public static readonly IReadOnlyList<string> Inbound = [PurchaseReceipt, SaleReturn, TransferIn, PositiveAdjustment, AssemblyOutput, ConsignmentIn, Opening];
    public static readonly IReadOnlyList<string> Outbound = [PurchaseReturn, SaleShipment, TransferOut, NegativeAdjustment, Scrap, AssemblyConsumption, ConsignmentOut];
    public static readonly IReadOnlyList<string> Signed = [CountVariance, DropShip];
    public static readonly IReadOnlyList<string> All = [.. Inbound, .. Outbound, .. Signed];

    /// <summary>+1 for inbound, -1 for outbound, 0 when the caller gives the sign (count variance, drop ship).</summary>
    public static int SignOf(string entryType) => Inbound.Contains(entryType, StringComparer.Ordinal) ? 1 : Outbound.Contains(entryType, StringComparer.Ordinal) ? -1 : 0;
}

/// <summary>
/// One movement a document asks for: a positive quantity in one of the item's units (a signed one for count
/// variances and drop-ship pairs); the engine converts it exactly to the base unit and applies the type's sign.
/// </summary>
public sealed record StockLine(
    Guid ItemId,
    string EntryType,
    decimal Quantity,
    Guid WarehouseId,
    Guid? UomId = null,
    Guid? VariantId = null,
    Guid? BinId = null,
    Guid? LotId = null,
    Guid? SerialId = null,
    Guid? SourceLineId = null,
    Guid? ReservationId = null,
    Guid? TransferPairId = null,
    string Ownership = "own",
    Guid? OwnerPartnerId = null,
    decimal? UnitCost = null,
    bool CostIsExpected = false,
    Guid? AppliesToSleId = null,
    string? OffsetRoleOverride = null,
    string? LotNumber = null,
    DateOnly? ExpiresOn = null,
    DateOnly? ManufacturedOn = null,
    string? SupplierLot = null,
    IReadOnlyList<string>? SerialNumbers = null,
    Guid? PartnerId = null);

public sealed record StockPostingRequest(
    Guid CompanyId,
    DateOnly PostingDate,
    string SourceDocumentType,
    Guid SourceDocumentId,
    IReadOnlyList<StockLine> Lines,
    string? IdempotencyKey = null);

public sealed record StockEntryInfo(
    Guid Id,
    long Sequence,
    Guid ItemId,
    Guid? VariantId,
    Guid WarehouseId,
    Guid? BinId,
    Guid? LotId,
    Guid? SerialId,
    string EntryType,
    decimal Quantity,
    Guid EnteredUomId,
    string EnteredUomCode,
    decimal EnteredQuantity,
    DateOnly PostingDate,
    Guid? SourceLineId,
    Guid? TransferPairId,
    Guid? ReservationId,
    decimal CostAmount = 0m,
    decimal? UnitCost = null,
    bool CostedAtExpected = false,
    string? LotNumber = null,
    string? SerialNumber = null);

public sealed record StockPostingResult(Guid PostingId, Guid CompanyId, DateOnly PostingDate, Guid? FiscalPeriodId, IReadOnlyList<StockEntryInfo> Entries, bool Replayed, Guid? JournalEntryId = null, string? JournalNumber = null);

/// <summary>
/// The stock ledger engine (ADR-0008, quantity side): writes the entries of one document movement in the caller's
/// unit of work, maintains the balances under row locks so two users cannot both take the last unit (hard scenario 4),
/// applies the negative-stock policy, consumes reservations and refuses closed inventory periods. Value entries and the
/// GL side are added by the costing engine (roadmap 3.3).
/// </summary>
public interface IInventoryPosting
{
    Task<Result<StockPostingResult>> PostAsync(StockPostingRequest request, CancellationToken cancellationToken = default);
}

public sealed record StockAvailability(
    Guid CompanyId,
    Guid ItemId,
    Guid? VariantId,
    Guid WarehouseId,
    decimal OnHand,
    decimal Reserved,
    decimal QualityHold,
    decimal InTransit,
    decimal Available);

public sealed record ReservationRequest(
    Guid CompanyId,
    Guid ItemId,
    decimal Quantity,
    Guid WarehouseId,
    string SourceDocumentType,
    Guid SourceDocumentId,
    Guid? SourceLineId = null,
    Guid? UomId = null,
    Guid? VariantId = null,
    Guid? BinId = null,
    Guid? LotId = null,
    Guid? SerialId = null,
    DateOnly? ExpiresOn = null,
    string? Reason = null);

public sealed record ReservationInfo(
    Guid Id,
    Guid CompanyId,
    Guid ItemId,
    Guid? VariantId,
    Guid WarehouseId,
    Guid? BinId,
    Guid? LotId,
    Guid? SerialId,
    decimal Quantity,
    decimal ConsumedQuantity,
    string SourceDocumentType,
    Guid SourceDocumentId,
    Guid? SourceLineId,
    string Status,
    DateOnly? ExpiresOn,
    string? Reason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt)
{
    public decimal Remaining => Quantity - ConsumedQuantity;
}

/// <summary>Reservations hold available quantity for a document until it ships (consumes) or lets go (releases); they never make stock negative.</summary>
public interface IStockReservations
{
    Task<Result<ReservationInfo>> ReserveAsync(ReservationRequest request, CancellationToken cancellationToken = default);

    Task<Result<ReservationInfo>> ReleaseAsync(Guid reservationId, string? reason, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReservationInfo>> ForDocumentAsync(string sourceDocumentType, Guid sourceDocumentId, CancellationToken cancellationToken = default);

    Task<StockAvailability> AvailabilityAsync(Guid companyId, Guid itemId, Guid warehouseId, Guid? variantId = null, CancellationToken cancellationToken = default);
}

public sealed record WarehouseInfo(Guid Id, Guid CompanyId, Guid? BranchId, string Code, LocalizedText Name, string Kind, bool BinsEnabled, bool? AllowNegativeStock, bool IsActive);

public sealed record BinInfo(Guid Id, Guid WarehouseId, string Code, string? Zone, string Kind, bool IsActive);

public interface IWarehouseDirectory
{
    Task<WarehouseInfo?> FindAsync(Guid warehouseId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WarehouseInfo>> ListAsync(Guid companyId, CancellationToken cancellationToken = default);

    Task<BinInfo?> FindBinAsync(Guid binId, CancellationToken cancellationToken = default);
}

/// <summary>Raised inside the posting's unit of work; costing, reporting and integrations react through the outbox.</summary>
public sealed record StockPosted(
    Guid AggregateId,
    Guid CompanyId,
    DateOnly PostingDate,
    string SourceDocumentType,
    Guid SourceDocumentId,
    int EntryCount) : IIntegrationEvent
{
    public static string EventType => "inventory.stock.posted";

    public static int EventVersion => 1;

    public string AggregateType => "stock_posting";
}

// ------------------------------------------------------------------ costing (ADR-0008, value side)

public static class ValueEntryTypes
{
    public const string DirectCost = "direct_cost";
    public const string IndirectCost = "indirect_cost";
    public const string ExpectedCost = "expected_cost";
    public const string ExpectedCostReversal = "expected_cost_reversal";
    public const string Revaluation = "revaluation";
    public const string Variance = "variance";
    public const string Rounding = "rounding";
    public const string CostAdjustment = "cost_adjustment";
}

public static class CostingMethods
{
    public const string Fifo = "fifo";
    public const string Average = "average";
    public const string Standard = "standard";
    public static readonly IReadOnlyList<string> All = [Fifo, Average, Standard];
}

/// <summary>One value entry of a stock ledger entry, with the reason it exists.</summary>
public sealed record StockValueEntryInfo(
    Guid Id,
    Guid? SleId,
    DateOnly PostingDate,
    DateOnly ValuationDate,
    string ValueType,
    decimal ValuedQuantity,
    decimal UnitCost,
    decimal CostAmountActual,
    decimal CostAmountExpected,
    string Currency,
    string AccountRole,
    string OffsetRole,
    Guid? OffsetRef,
    Guid? JournalEntryId,
    Guid? AdjustsValueEntryId,
    Guid? AdjustmentRunId,
    string SourceDocumentType,
    Guid SourceDocumentId,
    IReadOnlyDictionary<string, object?> Reason,
    bool CostedAtExpected,
    DateTimeOffset CreatedAt);

/// <summary>The cost of an item in one cost scope at a date: the running quantity, value and average, the last and the standard cost.</summary>
public sealed record ItemCostInfo(
    Guid CompanyId,
    Guid ItemId,
    Guid? WarehouseId,
    string CostingMethod,
    string CostingScope,
    DateOnly AsOf,
    decimal Quantity,
    decimal Value,
    decimal AverageUnitCost,
    decimal LastCost,
    decimal? StandardCost,
    decimal ExpectedUnitCost,
    bool ValuationPending);

/// <summary>A late supplier invoice (<c>invoice</c>: the actual unit cost replaces the expected one) or a landed cost (<c>landed_cost</c>: an amount added to the layer).</summary>
public sealed record InboundCostAdjustmentRequest(
    Guid SleId,
    string Kind,
    string TriggerDocumentType,
    Guid TriggerDocumentId,
    decimal? ActualUnitCost = null,
    decimal? Amount = null,
    DateOnly? PostingDate = null,
    string? Reason = null,
    string? IdempotencyKey = null);

public sealed record CostAdjustmentRunInfo(
    Guid Id,
    Guid CompanyId,
    Guid ItemId,
    Guid? WarehouseId,
    string TriggerKind,
    string TriggerDocumentType,
    Guid TriggerDocumentId,
    Guid? TriggerSleId,
    DateOnly FromDate,
    string Status,
    int EntriesWalked,
    int EntriesReapplied,
    int ValueEntriesCreated,
    int JournalEntriesPosted,
    decimal AmountAdjusted,
    Guid? JobId,
    string? Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record InboundCostAdjustmentResult(IReadOnlyList<StockValueEntryInfo> ValueEntries, Guid? JournalEntryId, CostAdjustmentRunInfo? Run);

/// <summary>
/// The costing engine (ADR-0008): values every stock movement as it posts, re-applies costs when a value-affecting
/// event lands at an earlier date, and posts the inventory side of the books through the accounting engine.
/// </summary>
public interface IInventoryCosting
{
    /// <summary>The cost of an item in its cost scope as of a date (today by default).</summary>
    Task<ItemCostInfo?> CostAsync(Guid companyId, Guid itemId, Guid? warehouseId = null, DateOnly? asOf = null, CancellationToken cancellationToken = default);

    /// <summary>Settles or adds to an inbound entry's cost after the fact (late invoice, landed cost) and re-applies everything it fed.</summary>
    Task<Result<InboundCostAdjustmentResult>> AdjustInboundCostAsync(InboundCostAdjustmentRequest request, CancellationToken cancellationToken = default);
}

// ------------------------------------------------------------------ lots and serials (roadmap 3.5)

public static class LotStatuses
{
    public const string Active = "active";
    public const string Quarantine = "quarantine";
    public const string Recalled = "recalled";
    public const string Expired = "expired";
    public const string Consumed = "consumed";
    public static readonly IReadOnlyList<string> All = [Active, Quarantine, Recalled, Expired, Consumed];

    /// <summary>Stock of a lot in one of these statuses is on quality hold: not available, not shipped.</summary>
    public static bool Blocks(string status) => status is Quarantine or Recalled or Expired;
}

public static class SerialStatuses
{
    public const string InStock = "in_stock";
    public const string InTransit = "in_transit";
    public const string Sold = "sold";
    public const string Returned = "returned";
    public const string InRepair = "in_repair";
    public const string Scrapped = "scrapped";
    public const string Consumed = "consumed";
    public const string Consigned = "consigned";
    public const string ReturnedToSupplier = "returned_to_supplier";
    public static readonly IReadOnlyList<string> All = [InStock, InTransit, Sold, Returned, InRepair, Scrapped, Consumed, Consigned, ReturnedToSupplier];

    /// <summary>Statuses in which the serial is physically in a warehouse of ours.</summary>
    public static bool IsOnHand(string status) => status is InStock or InTransit or Returned or InRepair;
}

public sealed record LotInfo(Guid Id, Guid ItemId, string ItemCode, string LotNumber, DateOnly? ManufacturedOn, DateOnly? ExpiresOn, string? SupplierLot, Guid? SupplierPartnerId, string Status, string? StatusReason, string? RecallReference, DateTimeOffset? StatusChangedAt, System.Text.Json.JsonElement CustomFields, DateTimeOffset UpdatedAt);

public sealed record SerialInfo(Guid Id, Guid ItemId, string ItemCode, string SerialNumber, Guid? LotId, string? LotNumber, string Status, Guid? CurrentWarehouseId, string? CurrentWarehouseCode, Guid? CurrentBinId, Guid? CurrentPartnerId, DateOnly? WarrantyUntil, System.Text.Json.JsonElement CustomFields, DateTimeOffset UpdatedAt);
