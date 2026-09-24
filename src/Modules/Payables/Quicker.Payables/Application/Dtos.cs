using Quicker.Payables.Contracts;

namespace Quicker.Payables.Application;

public sealed record OpenItemSummary(OpenItemInfo Item, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string FunctionalCurrency, DateTimeOffset UpdatedAt);

public sealed record HoldOpenItemRequest(string Reason);

/// <summary>One supplier's payables at a date, in the company's currency, by days past due; advances are shown apart because they sit on their own control account.</summary>
public sealed record AgingRow(Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, int ItemCount, decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal TotalFc, decimal AdvancesFc);

public sealed record AgingTotals(decimal NotDue, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90, decimal TotalFc, decimal AdvancesFc);

public sealed record AgingReport(Guid CompanyId, DateOnly AsOf, string FunctionalCurrency, IReadOnlyList<AgingRow> Rows, AgingTotals Totals);

/// <summary>One movement on a supplier's account: a document booked (what we owe goes up for an invoice, down for a debit note or a payment) or its reversal.</summary>
public sealed record StatementLine(DateOnly Date, string Kind, string DocumentType, Guid DocumentId, string DocumentNumber, string? SupplierReference, DateOnly? DueDate, bool Reversal, decimal Amount, decimal Balance);

/// <summary>The account in one of the documents' currencies: what was owed before the period, the period's movements and what is owed at its end.</summary>
public sealed record StatementCurrency(string Currency, decimal Opening, decimal Increases, decimal Decreases, decimal Closing, IReadOnlyList<StatementLine> Lines);

public sealed record SupplierStatement(Guid CompanyId, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, DateOnly From, DateOnly To, IReadOnlyList<StatementCurrency> Currencies);

/// <summary>Applies a debit note, a payment on account or an advance to an invoice open item of the same supplier and currency.</summary>
public sealed record ApplyRequest(Guid SettlingItemId, Guid SettledItemId, decimal Amount, DateOnly? SettlementDate = null, string? Reason = null);

public sealed record ReverseSettlementRequest(string Reason, DateOnly? ReversalDate = null);

public sealed record SaveProposalRequest(Guid CompanyId, DateOnly PayThrough, string Currency, Guid? PartnerId = null, Guid? BankAccountId = null, DateOnly? RunDate = null, string? Notes = null, bool TakeDiscounts = true);

public sealed record ProposalLineChange(Guid LineId, bool Selected, decimal? AmountTc = null);

public sealed record UpdateProposalLinesRequest(IReadOnlyList<ProposalLineChange> Lines);

public sealed record ProposalLineSummary(Guid Id, Guid OpenItemId, Guid PartnerId, string PartnerCode, IReadOnlyDictionary<string, string> PartnerName, string DocumentNumber, string Kind, DateOnly DueDate, DateOnly? DiscountDate, decimal DiscountPct, decimal RemainingTc, decimal AmountTc, decimal DiscountTc, bool Selected, Guid? PaymentId);

public sealed record ProposalSummary(Guid Id, Guid CompanyId, string Number, DateOnly RunDate, DateOnly PayThrough, string Currency, Guid? BankAccountId, Guid? PartnerId, string Status, decimal TotalTc, decimal DiscountTc, string? Notes, IReadOnlyList<ProposalLineSummary> Lines, DateTimeOffset? ApprovedAt, DateTimeOffset UpdatedAt);
