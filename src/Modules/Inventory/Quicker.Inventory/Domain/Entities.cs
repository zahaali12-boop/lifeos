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

    /// <summary>For inbound entries under FIFO: how much is still unapplied (the costing engine maintains it).</summary>
    public decimal RemainingQuantity { get; set; }

    public bool CostIsExpected { get; set; }

    public bool CostedAtExpected { get; set; }

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
