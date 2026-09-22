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
    Guid? OwnerPartnerId = null);

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
    Guid? ReservationId);

public sealed record StockPostingResult(Guid PostingId, Guid CompanyId, DateOnly PostingDate, Guid? FiscalPeriodId, IReadOnlyList<StockEntryInfo> Entries, bool Replayed);

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
