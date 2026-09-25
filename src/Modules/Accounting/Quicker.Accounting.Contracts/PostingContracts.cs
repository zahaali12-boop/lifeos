using Quicker.Kernel.Events;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Accounting.Contracts;

/// <summary>Determination keys of a line (ADR-0006): a rule matches when every key it names equals the line's.</summary>
public sealed record PostingKeys(
    string? DocumentType = null,
    Guid? ItemPostingGroupId = null,
    Guid? PartnerPostingGroupId = null,
    Guid? TaxCodeId = null,
    Guid? WarehouseId = null,
    Guid? BranchId = null,
    Guid? BankAccountId = null,
    Guid? AssetCategoryId = null,
    Guid? ChargeTypeId = null)
{
    public static readonly PostingKeys None = new();

    /// <summary>How many keys the rule names: the most specific matching rule wins.</summary>
    public int Specificity =>
        (DocumentType is null ? 0 : 1) + (ItemPostingGroupId is null ? 0 : 1) + (PartnerPostingGroupId is null ? 0 : 1) + (TaxCodeId is null ? 0 : 1)
        + (WarehouseId is null ? 0 : 1) + (BranchId is null ? 0 : 1) + (BankAccountId is null ? 0 : 1) + (AssetCategoryId is null ? 0 : 1) + (ChargeTypeId is null ? 0 : 1);

    /// <summary>True when every key this rule names equals the line's key.</summary>
    public bool Covers(PostingKeys line)
    {
        ArgumentNullException.ThrowIfNull(line);
        return (DocumentType is null || string.Equals(DocumentType, line.DocumentType, StringComparison.OrdinalIgnoreCase))
            && (ItemPostingGroupId is null || ItemPostingGroupId == line.ItemPostingGroupId)
            && (PartnerPostingGroupId is null || PartnerPostingGroupId == line.PartnerPostingGroupId)
            && (TaxCodeId is null || TaxCodeId == line.TaxCodeId)
            && (WarehouseId is null || WarehouseId == line.WarehouseId)
            && (BranchId is null || BranchId == line.BranchId)
            && (BankAccountId is null || BankAccountId == line.BankAccountId)
            && (AssetCategoryId is null || AssetCategoryId == line.AssetCategoryId)
            && (ChargeTypeId is null || ChargeTypeId == line.ChargeTypeId);
    }
}

/// <summary>
/// One line of a posting request: a role and a signed transaction-currency amount (positive debit, negative credit).
/// <paramref name="AmountFc"/> fixes the line's functional-currency value instead of converting the amount at the entry's
/// rate (ADR-0031): a subledger item is relieved at the value it was booked at, a bank movement at what the bank took;
/// a line with no transaction amount and a functional amount is the realised exchange difference those lines leave.
/// </summary>
public sealed record PostingLine(
    string AccountRole,
    decimal Amount,
    PostingKeys? Keys = null,
    Guid? AccountId = null,
    IReadOnlyDictionary<string, Guid>? Dimensions = null,
    Guid? PartnerId = null,
    string? SubledgerType = null,
    Guid? SubledgerRef = null,
    Guid? TaxCodeId = null,
    decimal? TaxBase = null,
    LocalizedText? Description = null,
    DateOnly? DueDate = null,
    decimal? AmountFc = null);

public sealed record PostingRequest(
    CompanyId CompanyId,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    DateOnly PostingDate,
    string Currency,
    IReadOnlyList<PostingLine> Lines,
    string? SourceDocumentNumber = null,
    DateOnly? DocumentDate = null,
    LocalizedText? Description = null,
    BranchId? BranchId = null,
    string RateType = "spot",
    decimal? RateOverride = null,
    string? RateOverrideReason = null,
    string? IdempotencyKey = null,
    bool IsManual = false,
    bool IsOpeningEntry = false,
    bool IsClosingEntry = false,
    DateOnly? AutoReverseOn = null);

public sealed record PostedLine(
    Guid LineId,
    int LineNo,
    Guid AccountId,
    string AccountCode,
    string AccountRole,
    Guid? PostingRuleId,
    decimal DebitTc,
    decimal CreditTc,
    decimal DebitFc,
    decimal CreditFc,
    decimal DebitRc,
    decimal CreditRc,
    Guid? DimensionSetId,
    string? SubledgerType,
    Guid? SubledgerRef,
    bool IsRounding);

public sealed record PostingResult(
    Guid EntryId,
    string Number,
    Guid CompanyId,
    DateOnly PostingDate,
    Guid FiscalYearId,
    Guid FiscalPeriodId,
    string CurrencyTc,
    string CurrencyFc,
    string? CurrencyRc,
    decimal RateTcFc,
    decimal? RateFcRc,
    IReadOnlyList<PostedLine> Lines,
    bool IsReversal,
    bool Replayed);

/// <summary>The only way a journal entry comes into existence (ADR-0006).</summary>
public interface IPostingService
{
    Task<Result<PostingResult>> PostAsync(PostingRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts the mirror entry (sides swapped, all three currencies copied) on the original date when its period is open
    /// for the actor, otherwise on the first day of the first open period, and links both entries (ADR-0026).
    /// </summary>
    Task<Result<PostingResult>> ReverseAsync(Guid entryId, DateOnly? reversalDate, string reason, bool automatic = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// The correction policy (ADR-0026, scenario 7): reverses the entry into the first open period on or after its
    /// date and posts the replacement there (or on the replacement's own later date), linking the replacement to the
    /// original as its correction. The replacement posts in the original's company.
    /// </summary>
    Task<Result<CorrectionResult>> CorrectAsync(Guid entryId, PostingRequest replacement, string reason, CancellationToken cancellationToken = default);
}

public sealed record CorrectionResult(PostingResult Reversal, PostingResult Replacement);

public sealed record JournalEntryPosted(
    Guid AggregateId,
    Guid CompanyId,
    string Number,
    DateOnly PostingDate,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    bool IsReversal,
    Guid? ReversesEntryId) : IIntegrationEvent
{
    public static string EventType => "accounting.journal.posted";

    public static int EventVersion => 1;

    public string AggregateType => "journal_entry";
}

/// <summary>
/// Registered by a module that posts entries for its own documents (a supplier invoice, a payment, a stock movement):
/// the document keeps its subledger and reverses itself, so its entries are not posted, reversed or corrected
/// through the journal-entry API, and the journal subledgers do not follow them (A-140).
/// </summary>
public sealed record PostingDocumentModule(string Module);

/// <summary>A line of an Accounting-owned entry on a control account; <paramref name="AmountTc"/> and <paramref name="AmountFc"/> are signed, positive debit.</summary>
public sealed record JournalSubledgerLine(int LineNo, string SubledgerType, Guid SubledgerRef, string AccountRole, decimal AmountTc, decimal AmountFc, DateOnly? DueDate);

/// <summary>An entry Accounting owns (a manual, opening, recurring or correcting journal) as the subledger behind a control account sees it; the id and number are known once it is written.</summary>
public sealed record JournalSubledgerEntry(Guid CompanyId, Guid EntryId, string Number, DateOnly PostingDate, DateOnly DocumentDate, string Currency, decimal RateTcFc, bool IsOpeningEntry, Guid? BranchId, IReadOnlyList<JournalSubledgerLine> Lines);

/// <summary>
/// A module that keeps the subledger behind a control account follows the entries Accounting owns (POSTING_RULES §8,
/// A-140): a journal line on its control account opens the subledger item it names, and reversing the entry reverses
/// it, so the subledger keeps equal to its control. Documents of other modules keep their own subledger themselves.
/// </summary>
public interface IJournalSubledger
{
    string SubledgerType { get; }

    /// <summary>Checked before the entry is written: refuses a line whose reference the subledger cannot carry.</summary>
    Task<Result> CheckAsync(Guid companyId, IReadOnlyList<JournalSubledgerLine> lines, CancellationToken cancellationToken = default);

    /// <summary>The entry was written: open the items its lines name.</summary>
    Task PostedAsync(JournalSubledgerEntry entry, CancellationToken cancellationToken = default);

    /// <summary>Before the reversal of an entry is written: reverse the items it opened, or refuse (an item already settled).</summary>
    Task<Result> ReverseAsync(Guid entryId, DateOnly reversalDate, CancellationToken cancellationToken = default);
}

/// <summary>One account role's net movement (debit − credit, in the company's functional currency) on a tax code posted in a period (for reconciling a tax return to the books).</summary>
public sealed record TaxAccountMovement(Guid TaxCodeId, string AccountRole, decimal AmountFc);

/// <summary>
/// Read access to the ledger's tax movement, for the tax module to reconcile a return to the books without reaching
/// into Accounting's own tables (module boundary; ADR-0018).
/// </summary>
public interface ILedgerReader
{
    /// <summary>Every account role and tax code posted with a tax code in the period, net debit − credit in functional currency.</summary>
    Task<IReadOnlyList<TaxAccountMovement>> TaxMovementAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

/// <summary>Lets a module that owns a keyed account (a bank account, later an asset category) register the rule that routes a role to it, so its documents keep posting by role (ADR-0006).</summary>
public interface IPostingRules
{
    /// <summary>Adds the rule to the company's current profile unless an identical one exists; refused while the company has no active profile.</summary>
    Task<Result> EnsureRuleAsync(CompanyId companyId, string accountRole, PostingKeys keys, Guid accountId, CancellationToken cancellationToken = default);
}
