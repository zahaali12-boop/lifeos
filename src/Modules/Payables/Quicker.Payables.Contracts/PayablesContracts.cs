using Quicker.Kernel.Results;

namespace Quicker.Payables.Contracts;

/// <summary>Kinds of payable open items (DOMAIN_MODEL §13). Positive amounts are owed to the supplier (a credit on the control account), negative ones are the supplier's credits, advances and payments (a debit).</summary>
public static class PayableKinds
{
    public const string Invoice = "invoice";
    public const string DebitNote = "debit_note";
    public const string Advance = "advance";
    public const string PaymentOnAccount = "payment_on_account";
    public const string Adjustment = "adjustment";
    public const string Opening = "opening";

    /// <summary>Kinds whose items sit on the trade payables control account; advances sit on the supplier-advances control.</summary>
    public static readonly IReadOnlyList<string> OnPayables = [Invoice, DebitNote, PaymentOnAccount, Adjustment, Opening];
}

public static class SettlementKinds
{
    public const string Payment = "payment";
    public const string CreditApplication = "credit_application";
    public const string AdvanceApplication = "advance_application";
    public const string Netting = "netting";
    public const string WriteOff = "write_off";
    public const string Revaluation = "revaluation";
}

public static class PayablesDocumentTypes
{
    public const string Settlement = "ap_settlement";
    public const string Proposal = "payment_proposal";
}

/// <summary>A new open item: <paramref name="AmountTc"/> and <paramref name="AmountFc"/> are signed (positive owed to the supplier, negative owed by them).</summary>
public sealed record NewOpenItem(
    Guid CompanyId,
    Guid PartnerId,
    string Kind,
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    int Instalment,
    string? SupplierReference,
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
    Guid? DimensionSetId = null);

public sealed record OpenItemInfo(
    Guid Id,
    Guid CompanyId,
    Guid PartnerId,
    string Kind,
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    int Instalment,
    string? SupplierReference,
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
    bool PaymentBlocked,
    string? BlockReason,
    Guid? JournalEntryId,
    Guid? BranchId,
    string Status);

public sealed record SettlementInfo(
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
    decimal WhtWithheldTc,
    decimal WriteOffTc,
    decimal BankChargeTc,
    Guid? JournalEntryId,
    Guid? ReversesSettlementId,
    string? Reason,
    string Status,
    DateTimeOffset CreatedAt);

/// <summary>
/// A settlement carried by a journal the caller posted (ADR-0031): the settled item is relieved by
/// <paramref name="AmountFcSettledItem"/> (its booked value), the settling item by <paramref name="AmountFcSettlingItem"/>
/// (the value at the settlement rate); discount and withholding are what the settled amount included besides cash.
/// </summary>
public sealed record RecordSettlementRequest(
    Guid SettlingItemId,
    Guid SettledItemId,
    DateOnly SettlementDate,
    string Kind,
    decimal AmountTc,
    decimal AmountFcSettledItem,
    decimal AmountFcSettlingItem,
    decimal SettlementRate,
    decimal DiscountTakenTc,
    decimal WhtWithheldTc,
    Guid JournalEntryId,
    string? Reason = null);

public sealed record ProposalLineInfo(Guid Id, Guid OpenItemId, Guid PartnerId, decimal AmountTc, decimal DiscountTc, bool Selected, Guid? PaymentId);

public sealed record ProposalInfo(Guid Id, Guid CompanyId, string Number, DateOnly RunDate, DateOnly PayThrough, string Currency, Guid? BankAccountId, Guid? PartnerId, string Status, decimal TotalTc, decimal DiscountTc, IReadOnlyList<ProposalLineInfo> Lines);

/// <summary>The payables subledger as the documents that feed it see it: invoices open items, payments settle them.</summary>
public interface IPayables
{
    Task<OpenItemInfo> OpenAsync(NewOpenItem item, CancellationToken cancellationToken = default);

    Task<OpenItemInfo?> FindAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenItemInfo>> ItemsOfAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default);

    /// <summary>Reverses the open items of a document: refused while any of them carries a settlement that stands (<c>payables.item_settled</c>).</summary>
    Task<Result<IReadOnlyList<OpenItemInfo>>> ReverseDocumentAsync(string documentType, Guid documentId, DateOnly reversalDate, CancellationToken cancellationToken = default);

    /// <summary>Records a settlement the caller's journal carries; both items move, nothing is posted here.</summary>
    Task<Result<SettlementInfo>> RecordAsync(RecordSettlementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reverses every settlement a journal carried (a payment reversed): refused while a settling item's remainder was applied further (<c>payables.remainder_applied</c>).</summary>
    Task<Result> ReverseSettlementsOfAsync(Guid journalEntryId, DateOnly reversalDate, Guid reversalEntryId, string reason, CancellationToken cancellationToken = default);

    Task<ProposalInfo?> ProposalAsync(Guid proposalId, CancellationToken cancellationToken = default);

    /// <summary>Marks proposal lines as paid by the given payments; the proposal is executed once every selected line is.</summary>
    Task<Result> MarkProposalPaidAsync(Guid proposalId, IReadOnlyDictionary<Guid, Guid> paymentByLine, CancellationToken cancellationToken = default);
}
