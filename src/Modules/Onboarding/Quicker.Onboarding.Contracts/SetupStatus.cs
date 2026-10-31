namespace Quicker.Onboarding.Contracts;

/// <summary>
/// Where a tenant stands on the way to its first posted document (setup wizard, A-173): what exists already, counted
/// so a step can show itself done. Company-bound parts (the chart, the opening journal) read for the company asked
/// for, or the tenant's first company when none is.
/// </summary>
public sealed record SetupStatus(Guid? CompanyId, int Companies, bool HasChart, int TaxRegimes, int Customers, int Suppliers, int Items, int OpeningJournals);

/// <summary>One line of an opening-balance import: the account by code, one side, and what its control needs: the partner's code with the due date, or the bank account or cash box's code.</summary>
public sealed record OpeningBalanceRow(string Account, decimal Debit, decimal Credit, string? Partner = null, string? BankAccount = null, DateOnly? DueDate = null, string? Description = null);

public sealed record OpeningBalanceImportRequest(IReadOnlyList<OpeningBalanceRow> Lines);

/// <summary>
/// What an opening-balance import did, or would do when <see cref="DryRun"/>: the journal posted (none on a dry run),
/// both sides, the difference opening balance equity absorbed, and how many lines and partners were involved.
/// </summary>
public sealed record OpeningBalanceResult(Guid? JournalId, string? Number, decimal Debit, decimal Credit, decimal Difference, int Lines, int PartnerLines, bool DryRun = false);
