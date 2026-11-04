using Quicker.Receivables.Contracts;

namespace Quicker.Receivables.Application;

public sealed record ReceivableSummary(ReceivableInfo Item, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string FunctionalCurrency, DateTimeOffset UpdatedAt);

public sealed record HoldReceivableRequest(string Reason);

/// <summary>One customer's receivables at a date, in the company's currency, by days past due; deposits are shown apart because they sit on their own control account.</summary>
public sealed record ArAgingRow(Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, int ItemCount, decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal TotalFc, decimal DepositsFc);

public sealed record ArAgingTotals(decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal TotalFc, decimal DepositsFc);

public sealed record ArAgingReport(Guid CompanyId, DateOnly AsOf, string FunctionalCurrency, IReadOnlyList<ArAgingRow> Rows, ArAgingTotals Totals);

/// <summary>One movement on a customer's account: a document booked (what they owe goes up for an invoice, down for a credit note or a receipt) or its reversal.</summary>
public sealed record ArStatementLine(DateOnly Date, string Kind, string DocumentType, Guid DocumentId, string DocumentNumber, string? CustomerReference, DateOnly? DueDate, bool Reversal, decimal Amount, decimal Balance);

/// <summary>The account in one of the documents' currencies: what was owed before the period, the period's movements and what is owed at its end.</summary>
public sealed record ArStatementCurrency(string Currency, decimal Opening, decimal Increases, decimal Decreases, decimal Closing, IReadOnlyList<ArStatementLine> Lines);

public sealed record CustomerStatement(Guid CompanyId, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, DateOnly From, DateOnly To, IReadOnlyList<ArStatementCurrency> Currencies);

public sealed record ReverseReceivableSettlementRequest(string Reason, DateOnly? ReversalDate = null);

/// <summary>A promise to pay on an item: the date the customer promised, and what they said; a null date clears the promise.</summary>
public sealed record PromiseToPayRequest(DateOnly? PromisedOn, string? Note = null);

/// <summary>Writes off what is still open on an item, or part of it: as bad debt, or as a small balance not worth chasing.</summary>
public sealed record WriteOffRequest(string Reason, decimal? Amount = null, string Kind = "bad_debt", DateOnly? WriteOffDate = null);

public sealed record ReverseWriteOffRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record WriteOffSummary(Guid Id, Guid CompanyId, string Number, string Status, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid OpenItemId, string DocumentNumber, Guid? AdjustmentItemId, Guid? SettlementId, DateOnly WriteOffDate, string Kind, string Currency, decimal AmountTc, decimal AmountFc, string FunctionalCurrency, string Reason, Guid? JournalEntryId, Guid? ReversalEntryId, string? ReversalReason, DateTimeOffset CreatedAt);

public sealed record SaveDunningLevelRequest(Guid CompanyId, int Level, int DaysOverdue, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, string>? Text = null, decimal FeeAmount = 0m, bool BlockCustomer = false, bool IsActive = true);

public sealed record DunningLevelSummary(Guid Id, Guid CompanyId, int Level, int DaysOverdue, IReadOnlyDictionary<string, string> Name, IReadOnlyDictionary<string, string> Text, decimal FeeAmount, string FeeCurrency, bool BlockCustomer, bool IsActive, DateTimeOffset UpdatedAt);

/// <summary>Issues the letters due on a date for the company's overdue customers, or one customer; fees are charged unless told otherwise.</summary>
public sealed record DunningRunRequest(Guid CompanyId, DateOnly? RunDate = null, Guid? PartnerId = null, bool ChargeFees = true);

/// <summary>One overdue item as a letter names it: what was open on the date, how late, and the level it reached.</summary>
public sealed record DunningLetterItem(Guid ItemId, string DocumentType, Guid DocumentId, string DocumentNumber, int Instalment, DateOnly DueDate, int DaysOverdue, string Currency, decimal RemainingTc, decimal RemainingFc, int PreviousLevel, int Level);

public sealed record DunningLetterSummary(Guid Id, Guid RunId, string RunNumber, DateOnly RunDate, Guid CompanyId, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string Number, int Level, IReadOnlyDictionary<string, string> LevelName, IReadOnlyDictionary<string, string> Text, IReadOnlyList<DunningLetterItem> Items, int ItemCount, decimal OverdueFc, string FunctionalCurrency, decimal FeeAmount, Guid? FeeItemId, bool BlockedCustomer, DateTimeOffset? SentAt, DateTimeOffset CreatedAt);

public sealed record DunningRunSummary(Guid Id, Guid CompanyId, string Number, DateOnly RunDate, Guid? PartnerId, string Status, int Letters, decimal FeesFc, string FunctionalCurrency, DateTimeOffset CreatedAt, IReadOnlyList<DunningLetterSummary> LetterList);

/// <summary>What a run on the date would issue, before anything is written.</summary>
public sealed record DunningPreview(Guid CompanyId, DateOnly RunDate, string FunctionalCurrency, IReadOnlyList<DunningLetterSummary> Letters);

/// <summary>Sets a customer's open receivable item against the same partner's open payable item, in their common currency; the amount defaults to the smaller remainder.</summary>
public sealed record NettingRequest(Guid ReceivableItemId, Guid PayableItemId, decimal? Amount = null, DateOnly? NettingDate = null, string? Reason = null);

public sealed record ReverseNettingRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record NettingSummary(Guid Id, Guid CompanyId, string Number, string Status, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, Guid ReceivableItemId, string ReceivableNumber, Guid PayableItemId, string PayableNumber, DateOnly NettingDate, string Currency, decimal AmountTc, decimal AmountFcReceivable, decimal AmountFcPayable, decimal FxGainLossFc, string FunctionalCurrency, string? Reason, Guid? JournalEntryId, Guid? ReversalEntryId, string? ReversalReason, DateTimeOffset CreatedAt);
