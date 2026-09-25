using System.Text.Json;
using Dapper;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Results;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Inventory.Application;

/// <summary>Stock balances, the global stock search, availability and the stock ledger as the reporting layer reads them (SQL over the ledger tables).</summary>
public sealed class StockInquiryService(IUnitOfWorkAccessor unitOfWork, ReservationService reservations)
{
    private sealed class BalanceRecord
    {
        public Guid CompanyId { get; set; }

        public Guid ItemId { get; set; }

        public string ItemCode { get; set; } = string.Empty;

        public string ItemName { get; set; } = string.Empty;

        public Guid VariantId { get; set; }

        public string? VariantSku { get; set; }

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public Guid BinId { get; set; }

        public string? BinCode { get; set; }

        public Guid LotId { get; set; }

        public Guid SerialId { get; set; }

        public string BaseUom { get; set; } = string.Empty;

        public decimal OnHand { get; set; }

        public decimal Reserved { get; set; }

        public decimal QualityHold { get; set; }

        public DateTimeOffset? LastMovementAt { get; set; }

        public string? LotNumber { get; set; }

        public DateOnly? LotExpiresOn { get; set; }

        public string? LotStatus { get; set; }

        public string? SerialNumber { get; set; }
    }

    private sealed class SearchRecord
    {
        public Guid CompanyId { get; set; }

        public Guid ItemId { get; set; }

        public string ItemCode { get; set; } = string.Empty;

        public string ItemName { get; set; } = string.Empty;

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public string WarehouseName { get; set; } = string.Empty;

        public string BaseUom { get; set; } = string.Empty;

        public decimal OnHand { get; set; }

        public decimal Reserved { get; set; }

        public decimal QualityHold { get; set; }

        public DateTimeOffset? LastMovementAt { get; set; }
    }

    private sealed class LedgerRecord
    {
        public Guid Id { get; set; }

        public long Sequence { get; set; }

        public Guid PostingId { get; set; }

        public Guid CompanyId { get; set; }

        public Guid ItemId { get; set; }

        public string ItemCode { get; set; } = string.Empty;

        public Guid? VariantId { get; set; }

        public Guid WarehouseId { get; set; }

        public string WarehouseCode { get; set; } = string.Empty;

        public Guid? BinId { get; set; }

        public string? BinCode { get; set; }

        public Guid? LotId { get; set; }

        public Guid? SerialId { get; set; }

        public string EntryType { get; set; } = string.Empty;

        public decimal Quantity { get; set; }

        public string BaseUom { get; set; } = string.Empty;

        public decimal EnteredQuantity { get; set; }

        public string EnteredUom { get; set; } = string.Empty;

        public DateOnly PostingDate { get; set; }

        public string SourceDocumentType { get; set; } = string.Empty;

        public Guid SourceDocumentId { get; set; }

        public Guid? SourceLineId { get; set; }

        public Guid? TransferPairId { get; set; }

        public Guid? ReservationId { get; set; }

        public Guid? PostedBy { get; set; }

        public DateTimeOffset PostedAt { get; set; }

        public string? LotNumber { get; set; }

        public string? SerialNumber { get; set; }

        public Guid? PartnerId { get; set; }
    }

    private sealed record SearchCursor(string ItemCode, string WarehouseCode);

    private sealed record LedgerCursor(long Sequence);

    public async Task<IReadOnlyList<StockBalanceRow>> BalancesAsync(Guid companyId, Guid? itemId, Guid? warehouseId, bool includeZero, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<BalanceRecord>(new CommandDefinition("""
            SELECT b.company_id, b.item_id, i.code AS item_code, i.name_i18n::text AS item_name, b.variant_id, v.sku AS variant_sku, b.warehouse_id, w.code AS warehouse_code,
                   b.bin_id, bn.code AS bin_code, b.lot_id, b.serial_id, u.code AS base_uom, b.on_hand, b.reserved, b.quality_hold, b.last_movement_at,
                   lt.lot_number, lt.expires_on AS lot_expires_on, lt.status AS lot_status, sr.serial_number
            FROM app.inv_stock_balances b
            JOIN app.itm_items i ON i.tenant_id = b.tenant_id AND i.id = b.item_id
            JOIN app.org_uoms u ON u.tenant_id = i.tenant_id AND u.id = i.base_uom_id
            JOIN app.inv_warehouses w ON w.tenant_id = b.tenant_id AND w.id = b.warehouse_id
            LEFT JOIN app.itm_item_variants v ON v.tenant_id = b.tenant_id AND v.id = b.variant_id
            LEFT JOIN app.inv_bins bn ON bn.tenant_id = b.tenant_id AND bn.id = b.bin_id
            LEFT JOIN app.inv_lots lt ON lt.tenant_id = b.tenant_id AND lt.id = b.lot_id
            LEFT JOIN app.inv_serials sr ON sr.tenant_id = b.tenant_id AND sr.id = b.serial_id
            WHERE b.company_id = @company AND (@item::uuid IS NULL OR b.item_id = @item) AND (@warehouse::uuid IS NULL OR b.warehouse_id = @warehouse)
              AND (@includeZero OR b.on_hand <> 0 OR b.reserved <> 0)
            ORDER BY i.code, w.code, bn.code NULLS FIRST, lt.lot_number NULLS FIRST, sr.serial_number NULLS FIRST
            """, new { company = companyId, item = itemId, warehouse = warehouseId, includeZero }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(static r => new StockBalanceRow(r.CompanyId, r.ItemId, r.ItemCode, Parse(r.ItemName), r.VariantId == Guid.Empty ? null : r.VariantId, r.VariantSku, r.WarehouseId, r.WarehouseCode,
            r.BinId == Guid.Empty ? null : r.BinId, r.BinCode, r.LotId == Guid.Empty ? null : r.LotId, r.SerialId == Guid.Empty ? null : r.SerialId, r.BaseUom,
            N(r.OnHand), N(r.Reserved), N(r.QualityHold), N(r.OnHand - r.Reserved - r.QualityHold), r.LastMovementAt, r.LotNumber, r.LotExpiresOn, r.LotStatus, r.SerialNumber)).ToList();
    }

    /// <summary>One row per item and warehouse, by item code then warehouse code; <paramref name="q"/> matches item code or name in any language.</summary>
    public async Task<Result<Page<StockSearchRow>>> SearchAsync(Guid? companyId, string? q, Guid? warehouseId, bool onlyAvailable, PageRequest page, CancellationToken cancellationToken)
    {
        SearchCursor? after = null;
        if (!string.IsNullOrWhiteSpace(page.Cursor))
        {
            var decoded = Cursor.Decode<SearchCursor>(page.Cursor);
            if (decoded.IsFailure)
            {
                return decoded.Error!;
            }

            after = decoded.Value;
        }

        var size = page.Size();
        var uow = unitOfWork.Current;
        var pattern = string.IsNullOrWhiteSpace(q) ? null : "%" + q.Trim() + "%";
        var rows = (await uow.Connection.QueryAsync<SearchRecord>(new CommandDefinition("""
            SELECT b.company_id, b.item_id, i.code AS item_code, i.name_i18n::text AS item_name, b.warehouse_id, w.code AS warehouse_code, w.name_i18n::text AS warehouse_name, u.code AS base_uom,
                   sum(b.on_hand) AS on_hand, sum(b.reserved) AS reserved, sum(b.quality_hold) AS quality_hold, max(b.last_movement_at) AS last_movement_at
            FROM app.inv_stock_balances b
            JOIN app.itm_items i ON i.tenant_id = b.tenant_id AND i.id = b.item_id
            JOIN app.org_uoms u ON u.tenant_id = i.tenant_id AND u.id = i.base_uom_id
            JOIN app.inv_warehouses w ON w.tenant_id = b.tenant_id AND w.id = b.warehouse_id
            WHERE (@company::uuid IS NULL OR b.company_id = @company) AND (@warehouse::uuid IS NULL OR b.warehouse_id = @warehouse)
              AND (@pattern::text IS NULL OR i.code ILIKE @pattern OR i.name_i18n->>'en' ILIKE @pattern OR i.name_i18n->>'ar' ILIKE @pattern)
              AND (@afterItem::text IS NULL OR (i.code, w.code) > (@afterItem, @afterWarehouse))
            GROUP BY b.company_id, b.item_id, i.code, i.name_i18n, b.warehouse_id, w.code, w.name_i18n, u.code
            HAVING NOT @onlyAvailable OR sum(b.on_hand) - sum(b.reserved) - sum(b.quality_hold) > 0
            ORDER BY i.code, w.code
            LIMIT @limit
            """, new { company = companyId, warehouse = warehouseId, pattern, afterItem = after?.ItemCode, afterWarehouse = after?.WarehouseCode, onlyAvailable, limit = size + 1 }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var hasMore = rows.Count > size;
        var items = rows.Take(size).Select(static r => new StockSearchRow(r.CompanyId, r.ItemId, r.ItemCode, Parse(r.ItemName), r.WarehouseId, r.WarehouseCode, Parse(r.WarehouseName), r.BaseUom,
            N(r.OnHand), N(r.Reserved), N(r.QualityHold), N(r.OnHand - r.Reserved - r.QualityHold), r.LastMovementAt)).ToList();
        return new Page<StockSearchRow>(items, hasMore ? Cursor.Encode(new SearchCursor(items[^1].ItemCode, items[^1].WarehouseCode)) : null);
    }

    public Task<StockAvailability> AvailabilityAsync(Guid companyId, Guid itemId, Guid warehouseId, Guid? variantId, CancellationToken cancellationToken) =>
        reservations.AvailabilityAsync(companyId, itemId, warehouseId, variantId, cancellationToken);

    /// <summary>The ledger newest first (by global sequence), paged by a keyset cursor.</summary>
    public async Task<Result<Page<StockLedgerRow>>> LedgerAsync(Guid companyId, Guid? itemId, Guid? warehouseId, DateOnly? from, DateOnly? to, string? sourceDocumentType, Guid? sourceDocumentId, PageRequest page, CancellationToken cancellationToken)
    {
        long? before = null;
        if (!string.IsNullOrWhiteSpace(page.Cursor))
        {
            var decoded = Cursor.Decode<LedgerCursor>(page.Cursor);
            if (decoded.IsFailure)
            {
                return decoded.Error!;
            }

            before = decoded.Value.Sequence;
        }

        var size = page.Size();
        var uow = unitOfWork.Current;
        var rows = (await uow.Connection.QueryAsync<LedgerRecord>(new CommandDefinition("""
            SELECT e.id, e.sequence, e.posting_id, e.company_id, e.item_id, i.code AS item_code, e.variant_id, e.warehouse_id, w.code AS warehouse_code, e.bin_id, bn.code AS bin_code, e.lot_id, e.serial_id,
                   e.entry_type, e.quantity, u.code AS base_uom, e.entered_quantity, eu.code AS entered_uom, e.posting_date, e.source_document_type, e.source_document_id, e.source_line_id,
                   e.transfer_pair_id, e.reservation_id, e.posted_by, e.posted_at, lt.lot_number, sr.serial_number, e.partner_id
            FROM app.inv_stock_ledger_entries e
            JOIN app.itm_items i ON i.tenant_id = e.tenant_id AND i.id = e.item_id
            JOIN app.org_uoms u ON u.tenant_id = i.tenant_id AND u.id = i.base_uom_id
            JOIN app.org_uoms eu ON eu.tenant_id = e.tenant_id AND eu.id = e.entered_uom_id
            JOIN app.inv_warehouses w ON w.tenant_id = e.tenant_id AND w.id = e.warehouse_id
            LEFT JOIN app.inv_bins bn ON bn.tenant_id = e.tenant_id AND bn.id = e.bin_id
            LEFT JOIN app.inv_lots lt ON lt.tenant_id = e.tenant_id AND lt.id = e.lot_id
            LEFT JOIN app.inv_serials sr ON sr.tenant_id = e.tenant_id AND sr.id = e.serial_id
            WHERE e.company_id = @company AND (@item::uuid IS NULL OR e.item_id = @item) AND (@warehouse::uuid IS NULL OR e.warehouse_id = @warehouse)
              AND (@from::date IS NULL OR e.posting_date >= @from) AND (@to::date IS NULL OR e.posting_date <= @to)
              AND (@sourceType::text IS NULL OR e.source_document_type = @sourceType) AND (@sourceId::uuid IS NULL OR e.source_document_id = @sourceId)
              AND (@before::bigint IS NULL OR e.sequence < @before)
            ORDER BY e.sequence DESC
            LIMIT @limit
            """, new { company = companyId, item = itemId, warehouse = warehouseId, from, to, sourceType = string.IsNullOrWhiteSpace(sourceDocumentType) ? null : sourceDocumentType.Trim(), sourceId = sourceDocumentId, before, limit = size + 1 }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var hasMore = rows.Count > size;
        var items = rows.Take(size).Select(static r => new StockLedgerRow(r.Id, r.Sequence, r.PostingId, r.CompanyId, r.ItemId, r.ItemCode, r.VariantId, r.WarehouseId, r.WarehouseCode, r.BinId, r.BinCode, r.LotId, r.SerialId,
            r.EntryType, N(r.Quantity), r.BaseUom, N(r.EnteredQuantity), r.EnteredUom, r.PostingDate, r.SourceDocumentType, r.SourceDocumentId, r.SourceLineId, r.TransferPairId, r.ReservationId, r.PostedBy, r.PostedAt, r.LotNumber, r.SerialNumber, r.PartnerId)).ToList();
        return new Page<StockLedgerRow>(items, hasMore ? Cursor.Encode(new LedgerCursor(items[^1].Sequence)) : null);
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static IReadOnlyDictionary<string, string> Parse(string json) =>
        string.IsNullOrEmpty(json) ? new Dictionary<string, string>(StringComparer.Ordinal) : JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? new Dictionary<string, string>(StringComparer.Ordinal);

    private static decimal N(decimal value) => ItemUomMath.Normalize(value);
}
