namespace Quicker.Accounting.Application;

/// <summary>
/// The common parameters of the inquiries: the date (today in the company's time zone by default), an optional
/// movement window start, a comparative date, the amount basis (functional or reporting currency), a dimension to
/// group by, whether closing entries count, and dimension filters (query keys <c>d.CODE=valueId</c>).
/// </summary>
public sealed record InquiryQuery(
    DateOnly? AsOf = null,
    DateOnly? From = null,
    DateOnly? CompareAsOf = null,
    string? Basis = null,
    string? GroupBy = null,
    bool IncludeClosing = false,
    IReadOnlyDictionary<string, Guid>? Filters = null);

public sealed record TrialBalanceAmounts(decimal Opening, decimal Debit, decimal Credit, decimal Closing)
{
    public static readonly TrialBalanceAmounts Zero = new(0m, 0m, 0m, 0m);

    public TrialBalanceAmounts Add(TrialBalanceAmounts other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new TrialBalanceAmounts(Opening + other.Opening, Debit + other.Debit, Credit + other.Credit, Closing + other.Closing);
    }
}

/// <summary>What a figure drills to: the ledger of the account over the same window with the same dimension filters.</summary>
public sealed record LedgerDrill(Guid AccountId, DateOnly? From, DateOnly To, IReadOnlyDictionary<string, Guid> Dimensions);

public sealed record TrialBalanceRow(
    Guid AccountId,
    string AccountCode,
    IReadOnlyDictionary<string, string> AccountName,
    string AccountType,
    bool IsControl,
    Guid? DimensionValueId,
    string? DimensionValueCode,
    IReadOnlyDictionary<string, string>? DimensionValueName,
    decimal Opening,
    decimal Debit,
    decimal Credit,
    decimal Closing,
    TrialBalanceAmounts? Compare,
    LedgerDrill Drill);

public sealed record TrialBalance(
    Guid CompanyId,
    string Currency,
    string Basis,
    DateOnly AsOf,
    DateOnly? From,
    DateOnly? CompareAsOf,
    DateOnly? CompareFrom,
    string? GroupBy,
    IReadOnlyDictionary<string, Guid> Filters,
    bool IncludeClosing,
    IReadOnlyList<TrialBalanceRow> Rows,
    TrialBalanceAmounts Totals,
    TrialBalanceAmounts? CompareTotals,
    bool Balanced);

public sealed record LedgerItem(
    Guid LineId,
    Guid EntryId,
    string EntryNumber,
    DateOnly PostingDate,
    DateOnly DocumentDate,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    string? SourceDocumentNumber,
    string? SourceLink,
    IReadOnlyDictionary<string, string> Description,
    string CurrencyTc,
    decimal DebitTc,
    decimal CreditTc,
    decimal Debit,
    decimal Credit,
    decimal Balance,
    IReadOnlyDictionary<string, Guid> Dimensions,
    Guid? BranchId,
    Guid? PartnerId,
    string? SubledgerType,
    Guid? SubledgerRef,
    bool IsReversal,
    bool IsRounding);

public sealed record AccountLedger(
    Guid CompanyId,
    Guid AccountId,
    string AccountCode,
    IReadOnlyDictionary<string, string> AccountName,
    string Currency,
    string Basis,
    DateOnly? From,
    DateOnly To,
    IReadOnlyDictionary<string, Guid> Filters,
    decimal Opening,
    decimal Debit,
    decimal Credit,
    decimal Closing,
    IReadOnlyList<LedgerItem> Items,
    string? NextCursor);

public sealed record DimensionBalanceRow(
    Guid? ValueId,
    string? ValueCode,
    IReadOnlyDictionary<string, string>? ValueName,
    decimal Opening,
    decimal Debit,
    decimal Credit,
    decimal Closing);

public sealed record DimensionBalances(
    Guid CompanyId,
    string Dimension,
    string Currency,
    string Basis,
    DateOnly AsOf,
    DateOnly? From,
    string? AccountType,
    Guid? AccountId,
    IReadOnlyList<DimensionBalanceRow> Rows,
    TrialBalanceAmounts Totals);
