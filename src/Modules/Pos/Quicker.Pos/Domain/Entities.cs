using Quicker.Kernel.Text;
using Quicker.Persistence.EntityFramework;

namespace Quicker.Pos.Domain;

public static class SessionStatuses
{
    public const string Open = "open";
    public const string Closed = "closed";
}

/// <summary>A till's configuration (DOMAIN_MODEL §11.9): where it sells from, where its money goes, how it prices and what its cashier may do.</summary>
public sealed class PosRegister : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid? BranchId { get; set; }

    public string Code { get; set; } = string.Empty;

    public LocalizedText Name { get; set; } = new();

    public Guid WarehouseId { get; set; }

    public Guid? BinId { get; set; }

    public Guid CashAccountId { get; set; }

    public Guid? CardAccountId { get; set; }

    public Guid? PriceListId { get; set; }

    public Guid? DefaultPartnerId { get; set; }

    public bool AllowCredit { get; set; }

    public bool AllowPriceOverride { get; set; }

    public decimal CashRoundingIncrement { get; set; }

    public LocalizedText ReceiptFooter { get; set; } = new();

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A cashier's shift on a register: opened with a float, closed with the cash counted against what the orders say should be there.</summary>
public sealed class PosSession : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RegisterId { get; set; }

    public string Number { get; set; } = string.Empty;

    public string Status { get; set; } = SessionStatuses.Open;

    public Guid CashierUserId { get; set; }

    public string CashierName { get; set; } = string.Empty;

    public DateTimeOffset OpenedAt { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public decimal OpeningFloat { get; set; }

    public decimal CashTaken { get; set; }

    public decimal CardTaken { get; set; }

    public decimal CreditTaken { get; set; }

    public int OrdersCount { get; set; }

    public decimal SalesTotal { get; set; }

    public decimal? ExpectedCash { get; set; }

    public decimal? CountedCash { get; set; }

    public decimal? Difference { get; set; }

    public Guid? DifferenceJournalEntryId { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An order a till synced: a sale or a refund, immutable once posted, with the journal, the stock movement and the e-invoice outcome it produced.</summary>
public sealed class PosOrder : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid RegisterId { get; set; }

    public Guid SessionId { get; set; }

    public string Number { get; set; } = string.Empty;

    public string DeviceOrderId { get; set; } = string.Empty;

    public string Kind { get; set; } = Contracts.PosOrderKinds.Sale;

    public Guid? RefundsOrderId { get; set; }

    public Guid? PartnerId { get; set; }

    public Guid CashierUserId { get; set; }

    public DateOnly OrderDate { get; set; }

    public DateTimeOffset OrderedAt { get; set; }

    public DateTimeOffset PostedAt { get; set; }

    public string Currency { get; set; } = string.Empty;

    public bool PricesIncludeTax { get; set; }

    public decimal TotalNet { get; set; }

    public decimal TotalTax { get; set; }

    public decimal TotalGross { get; set; }

    public decimal Rounding { get; set; }

    public decimal TotalDue { get; set; }

    public decimal TotalPaid { get; set; }

    public decimal ChangeGiven { get; set; }

    public Guid? JournalEntryId { get; set; }

    public Guid? StockPostingId { get; set; }

    public string? EinvoiceScheme { get; set; }

    public string? EinvoiceStatus { get; set; }

    public string? EinvoiceSubmissionId { get; set; }

    public string? EinvoiceQr { get; set; }

    public string EinvoiceMessages { get; set; } = "[]";

    public DateTimeOffset? EinvoiceSubmittedAt { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<PosOrderLine> Lines { get; } = [];

    public List<PosPayment> Payments { get; } = [];
}

public sealed class PosOrderLine : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public int LineNo { get; set; }

    public Guid ItemId { get; set; }

    public Guid? VariantId { get; set; }

    public string ItemCode { get; set; } = string.Empty;

    public LocalizedText Description { get; set; } = new();

    public Guid UomId { get; set; }

    public string UomCode { get; set; } = string.Empty;

    public decimal Quantity { get; set; }

    public decimal BaseQuantity { get; set; }

    public decimal UnitPrice { get; set; }

    public decimal? ListUnitPrice { get; set; }

    public decimal DiscountPct { get; set; }

    public decimal DiscountAmount { get; set; }

    public decimal NetAmount { get; set; }

    public Guid? TaxCodeId { get; set; }

    public decimal TaxRatePct { get; set; }

    public decimal TaxAmount { get; set; }

    public decimal GrossAmount { get; set; }

    public bool IsStockItem { get; set; } = true;
}

public sealed class PosPayment : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public int LineNo { get; set; }

    public string Method { get; set; } = Contracts.PosPaymentMethods.Cash;

    public Guid? BankAccountId { get; set; }

    public decimal Amount { get; set; }

    public string? Reference { get; set; }

    public Guid? BankTransactionId { get; set; }

    public Guid? ReceivableItemId { get; set; }
}
