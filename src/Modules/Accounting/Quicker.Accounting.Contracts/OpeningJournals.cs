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

/// <summary>
/// A journal another module creates and posts through Accounting in the company's currency (the trader's owner
/// movements, A-195): a manual journal with the lines as <see cref="OpeningJournalLine"/> hands them over (a bank or
/// cash line names its box, a control line its partner), a bilingual description and a reference the module finds
/// it by again. It is numbered, listed, reversed and audited like any manual journal.
/// </summary>
public sealed record ModuleJournalRequest(Guid CompanyId, DateOnly PostingDate, IReadOnlyList<OpeningJournalLine> Lines, IReadOnlyDictionary<string, string> Description, string? Reference = null);

public sealed record ModuleJournalResult(Guid JournalId, string Number, Guid JournalEntryId);

/// <summary>A line of a posted journal as a module reads it back: the account by code, one side, and the subledger item it names.</summary>
public sealed record JournalBriefLine(int LineNo, string AccountCode, decimal Debit, decimal Credit, string? SubledgerType, Guid? SubledgerRef);

/// <summary>A journal as a module reads it back by its reference: the header, its status and its lines.</summary>
public sealed record JournalBrief(Guid JournalId, string? Number, DateOnly PostingDate, IReadOnlyDictionary<string, string> Description, string? Reference, string Status, Guid? JournalEntryId, DateTimeOffset CreatedAt, IReadOnlyList<JournalBriefLine> Lines);

/// <summary>Manual journals for another module (A-195): created and posted in one go, and read back by the reference the module gave them.</summary>
public interface IJournals
{
    Task<Result<ModuleJournalResult>> PostAsync(ModuleJournalRequest request, CancellationToken cancellationToken = default);

    /// <summary>The company's journals whose reference starts with the prefix, newest first.</summary>
    Task<IReadOnlyList<JournalBrief>> ListByReferenceAsync(Guid companyId, string referencePrefix, int limit = 200, CancellationToken cancellationToken = default);
}
