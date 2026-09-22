using Quicker.Inventory.Contracts;
using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Inventory.Domain;

public sealed class Warehouse : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    /// <summary>standard, in_transit, consignment, quarantine, virtual.</summary>
    public string Kind { get; set; } = "standard";

    public bool BinsEnabled { get; set; }

    public Guid? DimensionValueId { get; set; }

    /// <summary>Null: the company's negative-stock policy applies.</summary>
    public bool? AllowNegativeStock { get; set; }

    public Dictionary<string, string> Address { get; set; } = new(StringComparer.Ordinal);

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<Bin> Bins { get; } = [];
}

public sealed class Bin : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid WarehouseId { get; set; }

    public string Code { get; set; } = string.Empty;

    public string? Zone { get; set; }

    /// <summary>storage, receiving, shipping, quarantine, returns.</summary>
    public string Kind { get; set; } = "storage";

    public int PickSequence { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StockPosting : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public DateOnly PostingDate { get; set; }

    public Guid? FiscalPeriodId { get; set; }

    public string SourceDocumentType { get; set; } = string.Empty;

    public Guid SourceDocumentId { get; set; }

    public int EntryCount { get; set; }

    public string? IdempotencyKey { get; set; }

    public Guid? PostedBy { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public List<StockLedgerEntry> Entries { get; } = [];
}

public sealed class StockLedgerEntry : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public long Sequence { get; set; }

    public Guid PostingId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid WarehouseId { get; set; }

    public Guid? BinId { get; set; }

    public Guid? LotId { get; set; }

    public Guid? SerialId { get; set; }

    public string EntryType { get; set; } = string.Empty;

    /// <summary>Signed, in the item's base unit.</summary>
    public decimal Quantity { get; set; }

    public Guid EnteredUomId { get; set; }

    public decimal EnteredQuantity { get; set; }

    public DateOnly PostingDate { get; set; }

    public Guid? FiscalPeriodId { get; set; }

    public string SourceDocumentType { get; set; } = string.Empty;

    public Guid SourceDocumentId { get; set; }

    public Guid? SourceLineId { get; set; }

    public string Ownership { get; set; } = "own";

    public Guid? OwnerPartnerId { get; set; }

    /// <summary>The document's cost per entered unit in the functional currency; null when the engine values the entry itself.</summary>
    public decimal? EnteredUnitCost { get; set; }

    public bool CostIsExpected { get; set; }

    /// <summary>For a return: the entry it reverses at its exact cost.</summary>
    public Guid? AppliesToSleId { get; set; }

    public Guid? TransferPairId { get; set; }

    public Guid? ReservationId { get; set; }

    public Guid? PostedBy { get; set; }

    public DateTimeOffset PostedAt { get; set; }
}

public sealed class StockBalance : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid VariantId { get; set; }

    public Guid WarehouseId { get; set; }

    public Guid BinId { get; set; }

    public Guid LotId { get; set; }

    public Guid SerialId { get; set; }

    public decimal OnHand { get; set; }

    public decimal Reserved { get; set; }

    public decimal QualityHold { get; set; }

    public DateTimeOffset? LastMovementAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Reservation : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public Guid WarehouseId { get; set; }

    public Guid? BinId { get; set; }

    public Guid? LotId { get; set; }

    public Guid? SerialId { get; set; }

    public decimal Quantity { get; set; }

    public decimal ConsumedQuantity { get; set; }

    public string SourceDocumentType { get; set; } = string.Empty;

    public Guid SourceDocumentId { get; set; }

    public Guid? SourceLineId { get; set; }

    /// <summary>active, consumed, released.</summary>
    public string Status { get; set; } = "active";

    public DateOnly? ExpiresOn { get; set; }

    public string? Reason { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }
}

public sealed class Transfer : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string? Number { get; set; }

    public Guid FromWarehouseId { get; set; }

    public Guid ToWarehouseId { get; set; }

    public Guid? TransitWarehouseId { get; set; }

    public DateOnly? ShipDate { get; set; }

    public DateOnly? ReceiveDate { get; set; }

    /// <summary>draft, shipped, partially_received, received, cancelled.</summary>
    public string Status { get; set; } = "draft";

    public Guid? ShipPostingId { get; set; }

    public Guid? ReceivePostingId { get; set; }

    public string? Reference { get; set; }

    public string? Notes { get; set; }

    public string CustomFields { get; set; } = "{}";

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<TransferLine> Lines { get; } = [];
}

public sealed class TransferLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid TransferId { get; set; }

    public int LineNo { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public decimal QtyRequested { get; set; }

    public decimal QtyShipped { get; set; }

    public decimal QtyReceived { get; set; }

    public Guid UomId { get; set; }

    public Guid? FromBinId { get; set; }

    public Guid? ToBinId { get; set; }

    public string Tracking { get; set; } = "{}";
}

// ------------------------------------------------------------------ costing (ADR-0008, value side)

/// <summary>One cost scope (company, item, [warehouse]): the row the engine locks, and its pending flag.</summary>
public sealed class ItemCostScope : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    /// <summary>The nil uuid when the company costs per company.</summary>
    public Guid WarehouseId { get; set; }

    public bool ValuationPending { get; set; }

    public Guid? PendingRunId { get; set; }

    public decimal LastCost { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class StockValueEntry : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid? SleId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WarehouseId { get; set; }

    /// <summary>The GL date: the movement's date, or the first open period's start when that period was closed.</summary>
    public DateOnly PostingDate { get; set; }

    /// <summary>The movement's own date.</summary>
    public DateOnly ValuationDate { get; set; }

    public string ValueType { get; set; } = ValueEntryTypes.DirectCost;

    public decimal ValuedQuantity { get; set; }

    public decimal UnitCost { get; set; }

    public decimal CostAmountActual { get; set; }

    public decimal CostAmountExpected { get; set; }

    public string Currency { get; set; } = string.Empty;

    /// <summary><c>Inventory</c> or <c>InventoryInTransit</c>; a variance row names its variance account instead.</summary>
    public string AccountRole { get; set; } = "Inventory";

    public string OffsetRole { get; set; } = string.Empty;

    /// <summary>The subledger item of the offset when it is a control account (the receipt for GRNI, the counterpart item for Inventory).</summary>
    public Guid? OffsetRef { get; set; }

    public Guid? ItemPostingGroupId { get; set; }

    public Guid? GlJournalEntryId { get; set; }

    public Guid? AdjustsSveId { get; set; }

    public Guid? AdjustmentRunId { get; set; }

    public string SourceDocumentType { get; set; } = string.Empty;

    public Guid SourceDocumentId { get; set; }

    public Dictionary<string, object?> Reason { get; set; } = new(StringComparer.Ordinal);

    public bool CostedAtExpected { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public decimal Amount => CostAmountActual + CostAmountExpected;
}

public sealed class ItemApplication : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid OutboundSleId { get; set; }

    public Guid InboundSleId { get; set; }

    public decimal Quantity { get; set; }

    public decimal CostAmount { get; set; }

    public bool IsReapplication { get; set; }

    public Guid? RunId { get; set; }

    public Guid? SupersededBy { get; set; }

    public DateTimeOffset AppliedAt { get; set; }
}

/// <summary>The running quantity, value and average of a cost scope at the end of one day that had a movement.</summary>
public sealed class ItemCost : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WarehouseId { get; set; }

    public DateOnly ValuationDate { get; set; }

    public decimal Quantity { get; set; }

    public decimal Value { get; set; }

    public decimal AverageUnitCost { get; set; }

    public decimal LastCost { get; set; }

    public decimal? StandardCost { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class CostAdjustmentRun : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public Guid WarehouseId { get; set; }

    public string TriggerKind { get; set; } = string.Empty;

    public string TriggerDocumentType { get; set; } = string.Empty;

    public Guid TriggerDocumentId { get; set; }

    public Guid? TriggerSleId { get; set; }

    public DateOnly FromDate { get; set; }

    public string Status { get; set; } = "running";

    public int EntriesWalked { get; set; }

    public int EntriesReapplied { get; set; }

    public int ValueEntriesCreated { get; set; }

    public int JournalEntriesPosted { get; set; }

    public decimal AmountAdjusted { get; set; }

    public Guid? JobId { get; set; }

    public string? Error { get; set; }

    public Guid? StartedBy { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class StandardCostVersion : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid ItemId { get; set; }

    public decimal StandardCost { get; set; }

    public DateOnly EffectiveFrom { get; set; }

    public string? Reason { get; set; }

    public Guid? RevaluationRunId { get; set; }

    public Guid? ApprovedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
