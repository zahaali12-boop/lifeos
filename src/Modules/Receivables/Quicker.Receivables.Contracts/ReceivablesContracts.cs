using Quicker.Kernel.Results;

namespace Quicker.Receivables.Contracts;

/// <summary>Kinds of receivable open items (DOMAIN_MODEL §13). Positive amounts are owed by the customer (a debit on the control account), negative ones are the customer's credits, deposits and receipts (a credit).</summary>
public static class ReceivableKinds
{
    public const string Invoice = "invoice";
    public const string CreditNote = "credit_note";
    public const string Deposit = "deposit";
    public const string ReceiptOnAccount = "receipt_on_account";
    public const string Adjustment = "adjustment";
    public const string Opening = "opening";

    /// <summary>Kinds whose items sit on the trade receivables control account; deposits sit on the customer-deposits control.</summary>
    public static readonly IReadOnlyList<string> OnReceivables = [Invoice, CreditNote, ReceiptOnAccount, Adjustment, Opening];
}

public static class ReceivableSettlementKinds
{
    public const string Receipt = "receipt";
    public const string CreditApplication = "credit_application";
    public const string DepositApplication = "deposit_application";
    public const string Netting = "netting";
    public const string WriteOff = "write_off";
    public const string Revaluation = "revaluation";
}

public static class ReceivablesDocumentTypes
{
    public const string Settlement = "ar_settlement";
}

/// <summary>A new open item: <paramref name="AmountTc"/> and <paramref name="AmountFc"/> are signed (positive owed by the customer, negative owed to them).</summary>
public sealed record NewReceivable(
    Guid CompanyId,
    Guid PartnerId,
    string Kind,
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    int Instalment,
    string? CustomerReference,
    DateOnly PostingDate,
    DateOnly DocumentDate,
    DateOnly DueDate,
    DateOnly? DiscountDate,
    decimal DiscountPct,
    string Currency,
    decimal AmountTc,
    decimal AmountFc,
    decimal BookedRate,
    Guid? JournalEntryId,
    Guid? BranchId = null,
    Guid? DimensionSetId = null,
    Guid? SalesRepId = null);

public sealed record ReceivableInfo(
    Guid Id,
    Guid CompanyId,
    Guid PartnerId,
    string Kind,
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    int Instalment,
    string? CustomerReference,
    DateOnly PostingDate,
    DateOnly DocumentDate,
    DateOnly DueDate,
    DateOnly? DiscountDate,
    decimal DiscountPct,
    string Currency,
    decimal OriginalTc,
    decimal OriginalFc,
    decimal BookedRate,
    decimal SettledTc,
    decimal SettledFc,
    decimal RemainingTc,
    decimal RemainingFc,
    bool OnHold,
    string? HoldReason,
    int DunningLevel,
    DateOnly? PromisedPaymentDate,
    Guid? JournalEntryId,
    Guid? SalesRepId,
    Guid? BranchId,
    string Status);

public sealed record ReceivableSettlementInfo(
    Guid Id,
    Guid CompanyId,
    Guid SettlingItemId,
    string SettlingDocumentNumber,
    string SettlingKind,
    Guid SettledItemId,
    string SettledDocumentNumber,
    DateOnly SettlementDate,
    string Kind,
    string Currency,
    decimal AmountTc,
    decimal AmountFcSettledItem,
    decimal AmountFcSettlingItem,
    decimal SettlementRate,
    decimal FxGainLossFc,
    decimal DiscountTakenTc,
    decimal WhtDeductedTc,
    decimal WriteOffTc,
    decimal BankChargeTc,
    Guid? JournalEntryId,
    Guid? ReversesSettlementId,
    string? Reason,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// A settlement carried by a journal the caller posted (ADR-0031): the settled invoice is relieved by
/// <paramref name="AmountFcSettledItem"/> (its booked value), the settling receipt or credit by
/// <paramref name="AmountFcSettlingItem"/> (the value at the settlement rate); discount and withholding are what the
/// settled amount included besides cash.
/// </summary>
public sealed record RecordReceivableSettlementRequest(
    Guid SettlingItemId,
    Guid SettledItemId,
    DateOnly SettlementDate,
    string Kind,
    decimal AmountTc,
    decimal AmountFcSettledItem,
    decimal AmountFcSettlingItem,
    decimal SettlementRate,
    decimal DiscountTakenTc,
    decimal WhtDeductedTc,
    Guid JournalEntryId,
    string? Reason = null);

/// <summary>Applies a credit note, a receipt on account or a deposit to an invoice open item of the same customer and currency; a rate difference is realised FX, booked by the subledger itself.</summary>
public sealed record ApplyReceivableRequest(Guid SettlingItemId, Guid SettledItemId, decimal Amount, DateOnly? SettlementDate = null, string? Reason = null);

/// <summary>The receivables subledger as the documents that feed it see it: invoices open items, credit notes and receipts settle them.</summary>
public interface IReceivables
{
    Task<ReceivableInfo> OpenAsync(NewReceivable item, CancellationToken cancellationToken = default);

    Task<ReceivableInfo?> FindAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReceivableInfo>> ItemsOfAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Reverses the open items of a document: refused while any of them carries a settlement that stands (<c>receivables.item_settled</c>).</summary>
    Task<Result<IReadOnlyList<ReceivableInfo>>> ReverseDocumentAsync(string documentType, Guid documentId, DateOnly reversalDate, CancellationToken cancellationToken = default);

    /// <summary>Records a settlement the caller's journal carries; both items move, nothing is posted here.</summary>
    Task<Result<ReceivableSettlementInfo>> RecordAsync(RecordReceivableSettlementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Applies a credit or deposit to an invoice item, posting the realised exchange difference when the two were booked at different rates.</summary>
    Task<Result<ReceivableSettlementInfo>> ApplyAsync(ApplyReceivableRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses every settlement a journal carried (a receipt reversed): refused while a settling item's remainder was applied further (<c>receivables.remainder_applied</c>).</summary>
    Task<Result> ReverseSettlementsOfAsync(Guid journalEntryId, DateOnly reversalDate, Guid reversalEntryId, string reason, CancellationToken cancellationToken = default);

    /// <summary>What the customer owes the company in a currency, net of its unapplied credits and receipts (the open-AR basis of credit control, A-152).</summary>
    Task<decimal> OpenBalanceAsync(Guid companyId, Guid partnerId, string currency, CancellationToken cancellationToken = default);
}
