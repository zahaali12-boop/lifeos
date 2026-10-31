
namespace Quicker.Pos.Application;

/// <summary>A register to create or change; the code is given or taken from the name; <see cref="CashRoundingIncrement"/> null or 0 means the company's setting for the currency.</summary>
public sealed record SaveRegisterRequest(
    Guid CompanyId,
    IReadOnlyDictionary<string, string> Name,
    Guid WarehouseId,
    Guid CashAccountId,
    string? Code = null,
    Guid? BranchId = null,
    Guid? BinId = null,
    Guid? CardAccountId = null,
    Guid? PriceListId = null,
    Guid? DefaultPartnerId = null,
    bool AllowCredit = false,
    bool AllowPriceOverride = false,
    decimal? CashRoundingIncrement = null,
    IReadOnlyDictionary<string, string>? ReceiptFooter = null,
    bool IsActive = true);

public sealed record PosRegisterSummary(
    Guid Id,
    Guid CompanyId,
    Guid? BranchId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid WarehouseId,
    string? WarehouseCode,
    Guid? BinId,
    Guid CashAccountId,
    string? CashAccountCode,
    Guid? CardAccountId,
    string? CardAccountCode,
    Guid? PriceListId,
    Guid? DefaultPartnerId,
    IReadOnlyDictionary<string, string>? DefaultPartnerName,
    bool AllowCredit,
    bool AllowPriceOverride,
    decimal CashRoundingIncrement,
    string Currency,
    IReadOnlyDictionary<string, string> ReceiptFooter,
    bool IsActive,
    Guid? OpenSessionId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>One sellable entry of the catalogue a till caches: an item, or one variant of it, in its sales unit, with the price and the tax the till applies offline.</summary>
public sealed record PosCatalogueItem(
    Guid ItemId,
    Guid? VariantId,
    string Code,
    IReadOnlyDictionary<string, string> Name,
    Guid UomId,
    string UomCode,
    decimal? UnitPrice,
    Guid? TaxCodeId,
    string? TaxCode,
    decimal TaxRatePct,
    bool IsStockItem,
    string Tracking,
    Guid? CategoryId,
    IReadOnlyList<string> Barcodes);

/// <summary>What a till needs to sell offline: the register's rules and every sellable item priced and taxed as of now.</summary>
public sealed record PosCatalogue(
    Guid RegisterId,
    Guid CompanyId,
    string Currency,
    int MinorUnits,
    bool PricesIncludeTax,
    string TaxRoundingLevel,
    string RoundingMode,
    decimal CashRoundingIncrement,
    bool AllowCredit,
    bool AllowPriceOverride,
    Guid? DefaultPartnerId,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<PosCatalogueItem> Items);

public sealed record OpenSessionRequest(decimal OpeningFloat = 0m, string? Notes = null);

public sealed record CloseSessionRequest(decimal CountedCash, string? Notes = null);

public sealed record PosSessionSummary(
    Guid Id,
    Guid CompanyId,
    Guid RegisterId,
    string RegisterCode,
    IReadOnlyDictionary<string, string> RegisterName,
    string Number,
    string Status,
    Guid CashierUserId,
    string CashierName,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    decimal OpeningFloat,
    decimal CashTaken,
    decimal CardTaken,
    decimal CreditTaken,
    int OrdersCount,
    decimal SalesTotal,
    decimal? ExpectedCash,
    decimal? CountedCash,
    decimal? Difference,
    Guid? DifferenceJournalEntryId,
    string Currency,
    string? Notes);

/// <summary>A line as the till sold it: the unit price it charged (the catalogue's unless the cashier overrode it), the discount, and the tax code the catalogue gave the item.</summary>
public sealed record SubmitOrderLineRequest(
    Guid ItemId,
    decimal Quantity,
    decimal UnitPrice,
    Guid? VariantId = null,
    Guid? UomId = null,
    decimal DiscountPct = 0m,
    Guid? TaxCodeId = null,
    IReadOnlyDictionary<string, string>? Description = null);

public sealed record SubmitOrderPaymentRequest(string Method, decimal Amount, string? Reference = null);

/// <summary>
/// An order as the till synced it. <see cref="DeviceOrderId"/> is the till's own id, unique per tenant, so a sync that
/// was cut off can be sent again and is answered with the order already posted. <see cref="TotalDue"/> is what the
/// receipt showed the customer: the server recomputes the totals and refuses the order when they differ, so a stale
/// catalogue is caught rather than booked.
/// </summary>
public sealed record SubmitOrderRequest(
    Guid RegisterId,
    Guid SessionId,
    string DeviceOrderId,
    DateTimeOffset OrderedAt,
    IReadOnlyList<SubmitOrderLineRequest> Lines,
    IReadOnlyList<SubmitOrderPaymentRequest> Payments,
    decimal TotalDue,
    string Kind = "sale",
    string? RefundsDeviceOrderId = null,
    Guid? PartnerId = null,
    decimal ChangeGiven = 0m,
    string? Notes = null);

public sealed record PosOrderLineSummary(
    Guid Id,
    int LineNo,
    Guid ItemId,
    Guid? VariantId,
    string ItemCode,
    IReadOnlyDictionary<string, string> Description,
    Guid UomId,
    string UomCode,
    decimal Quantity,
    decimal BaseQuantity,
    decimal UnitPrice,
    decimal? ListUnitPrice,
    decimal DiscountPct,
    decimal DiscountAmount,
    decimal NetAmount,
    Guid? TaxCodeId,
    decimal TaxRatePct,
    decimal TaxAmount,
    decimal GrossAmount,
    bool IsStockItem);

public sealed record PosPaymentSummary(Guid Id, int LineNo, string Method, Guid? BankAccountId, decimal Amount, string? Reference, Guid? BankTransactionId, Guid? ReceivableItemId);

public sealed record PosOrderSummary(
    Guid Id,
    Guid CompanyId,
    Guid RegisterId,
    string RegisterCode,
    Guid SessionId,
    string SessionNumber,
    string Number,
    string DeviceOrderId,
    string Kind,
    Guid? RefundsOrderId,
    string? RefundsOrderNumber,
    Guid? PartnerId,
    IReadOnlyDictionary<string, string>? PartnerName,
    Guid CashierUserId,
    DateOnly OrderDate,
    DateTimeOffset OrderedAt,
    DateTimeOffset PostedAt,
    string Currency,
    bool PricesIncludeTax,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    decimal Rounding,
    decimal TotalDue,
    decimal TotalPaid,
    decimal ChangeGiven,
    Guid? JournalEntryId,
    Guid? StockPostingId,
    string? EinvoiceScheme,
    string? EinvoiceStatus,
    string? EinvoiceSubmissionId,
    string? EinvoiceQr,
    IReadOnlyList<string> EinvoiceMessages,
    string? Notes,
    IReadOnlyList<PosOrderLineSummary> Lines,
    IReadOnlyList<PosPaymentSummary> Payments,
    bool Replayed);
