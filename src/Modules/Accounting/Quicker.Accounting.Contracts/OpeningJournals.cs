using Quicker.Kernel.Results;

namespace Quicker.Accounting.Contracts;

/// <summary>
/// A line of an opening journal as another module hands it over: the account by code, one side, and what its
/// subledger needs when the account is a control: the partner a receivable or payable is owed by or to (with the
/// day it falls due), or the bank account or cash box a bank balance sits in. Accounting fills the subledger type
/// from the account.
/// </summary>
public sealed record OpeningJournalLine(string AccountCode, decimal Debit, decimal Credit, Guid? PartnerId = null, Guid? BankAccountId = null, DateOnly? DueDate = null, string? Description = null);

/// <summary>The opening journal to create and post: balances as of a day in the company's currency; what does not balance goes to opening balance equity.</summary>
public sealed record OpeningJournalRequest(Guid CompanyId, DateOnly AsOf, IReadOnlyList<OpeningJournalLine> Lines, string? Reference = null);

/// <summary>What was posted: the journal, its number, both sides and the difference that opening balance equity absorbed (debits less credits, signed).</summary>
public sealed record OpeningJournalResult(Guid JournalId, string Number, decimal Debit, decimal Credit, decimal Difference);

/// <summary>Opening balances as a posted journal of the <c>opening</c> kind (go-live, A-173): the one way a migration brings balances in.</summary>
public interface IOpeningJournals
{
    Task<Result<OpeningJournalResult>> PostAsync(OpeningJournalRequest request, CancellationToken cancellationToken = default);
}
