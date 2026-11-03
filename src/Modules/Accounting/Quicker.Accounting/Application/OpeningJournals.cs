using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;

namespace Quicker.Accounting.Application;

/// <summary>
/// Journals for another module, created and posted in one go in the company's currency: opening balances for the
/// setup wizard (A-173, one journal of the <c>opening</c> kind; what the lines do not balance, posting sends to
/// opening balance equity, and the result says how much that was) and manual journals a module posts under a
/// reference of its own (A-195, the trader's owner movements). A line on a control account carries the partner or
/// the bank account as the subledger item, so the open items and the bank movements are born with the lines.
/// </summary>
public sealed class OpeningJournals(AccountingDbContext db, ICompanyDirectory companies, IChartOfAccounts chart, ManualJournalService journals) : IOpeningJournals, IJournals
{
    public async Task<Result<OpeningJournalResult>> PostAsync(OpeningJournalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var chartId = await chart.ChartForCompanyAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (chartId is null)
        {
            return Error.Validation("opening.chart_required", "The company has no chart of accounts yet; create one from a template first.");
        }

        if (request.Lines.Count == 0)
        {
            return Error.Validation("import.empty", "Nothing to import.");
        }

        var lines = await LinesAsync(chartId.Value, request.Lines, "opening", cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var description = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = $"Opening balances as of {request.AsOf:yyyy-MM-dd}", ["ar"] = $"الأرصدة الافتتاحية كما في {request.AsOf:yyyy-MM-dd}" };
        var posted = await CreateAndPostAsync(request.CompanyId, request.AsOf, company.FunctionalCurrency.Code, lines.Value, JournalKinds.Opening, description, request.Reference, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var debit = request.Lines.Sum(static l => l.Debit);
        var credit = request.Lines.Sum(static l => l.Credit);
        return new OpeningJournalResult(posted.Value.Id, posted.Value.Number!, debit, credit, debit - credit);
    }

    public async Task<Result<ModuleJournalResult>> PostAsync(ModuleJournalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var chartId = await chart.ChartForCompanyAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (chartId is null)
        {
            return Error.Validation("journal.chart_required", "The company has no chart of accounts yet; create one from a template first.");
        }

        if (request.Lines.Count < 2)
        {
            return Error.Validation("journal.lines_required", "A journal has at least two lines.");
        }

        var lines = await LinesAsync(chartId.Value, request.Lines, "journal", cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var posted = await CreateAndPostAsync(request.CompanyId, request.PostingDate, company.FunctionalCurrency.Code, lines.Value, JournalKinds.Manual, request.Description, request.Reference, cancellationToken);
        return posted.IsSuccess ? new ModuleJournalResult(posted.Value.Id, posted.Value.Number!, posted.Value.JournalEntryId!.Value) : posted.Error!;
    }

    public async Task<IReadOnlyList<JournalBrief>> ListByReferenceAsync(Guid companyId, string referencePrefix, int limit = 200, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(referencePrefix);
        var found = await db.Journals.AsNoTracking().Include(static j => j.Lines)
            .Where(j => j.CompanyId == companyId && j.Reference != null && j.Reference.StartsWith(referencePrefix))
            .OrderByDescending(static j => j.PostingDate).ThenByDescending(static j => j.CreatedAt)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);
        var accountIds = found.SelectMany(static j => j.Lines).Select(static l => l.AccountId).Distinct().ToList();
        var codes = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, static a => a.Code, cancellationToken);
        return found.Select(j => new JournalBrief(j.Id, j.Number, j.PostingDate, j.Description.Values, j.Reference, j.Status, j.JournalEntryId, j.CreatedAt,
            j.Lines.OrderBy(static l => l.LineNo).Select(l => new JournalBriefLine(l.LineNo, codes.GetValueOrDefault(l.AccountId) ?? string.Empty, l.Debit, l.Credit, l.SubledgerType, l.SubledgerRef)).ToList())).ToList();
    }

    /// <summary>The lines by account code, each control line carrying the subledger item its balance belongs to; stock and assets have their own documents, so their controls are refused.</summary>
    private async Task<Result<List<JournalLineRequest>>> LinesAsync(Guid chartId, IReadOnlyList<OpeningJournalLine> requested, string errorPrefix, CancellationToken cancellationToken)
    {
        var codes = requested.Select(static l => l.AccountCode.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.ChartId == chartId && codes.Contains(a.Code)).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        var lines = new List<JournalLineRequest>(requested.Count);
        for (var i = 0; i < requested.Count; i++)
        {
            var line = requested[i];
            if (!accounts.TryGetValue(line.AccountCode.Trim(), out var account))
            {
                return Error.NotFound("account", line.AccountCode).WithWhy(("line", i + 1), ("account", line.AccountCode));
            }

            Guid? subledgerRef = null;
            if (account.IsControl)
            {
                switch (account.SubledgerType)
                {
                    case SubledgerTypes.Receivables:
                    case SubledgerTypes.Payables:
                        if (line.PartnerId is null)
                        {
                            return Error.Validation($"{errorPrefix}.partner_required", $"Line {i + 1}: '{account.Code}' is a control account; the line names the partner the balance is owed by or to.").WithWhy(("line", i + 1), ("account", account.Code));
                        }

                        subledgerRef = line.PartnerId;
                        break;
                    case SubledgerTypes.Bank:
                        if (line.BankAccountId is null)
                        {
                            return Error.Validation($"{errorPrefix}.bank_account_required", $"Line {i + 1}: '{account.Code}' is a bank control account; the line names the bank account or cash box the balance sits in.").WithWhy(("line", i + 1), ("account", account.Code));
                        }

                        subledgerRef = line.BankAccountId;
                        break;
                    default:
                        return Error.Validation($"{errorPrefix}.subledger_unsupported", $"Line {i + 1}: '{account.Code}' reconciles to the {account.SubledgerType} subledger; opening stock is entered by a stock count and assets in the asset register.").WithWhy(("line", i + 1), ("account", account.Code), ("subledgerType", account.SubledgerType));
                }
            }

            lines.Add(new JournalLineRequest(line.Debit, line.Credit, account.Code, null, null, line.PartnerId,
                account.IsControl ? account.SubledgerType : null, subledgerRef, null,
                line.Description is { } text ? new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = text, ["ar"] = text } : null, line.DueDate));
        }

        return lines;
    }

    private async Task<Result<ManualJournalSummary>> CreateAndPostAsync(Guid companyId, DateOnly postingDate, string currency, IReadOnlyList<JournalLineRequest> lines, string kind, IReadOnlyDictionary<string, string> description, string? reference, CancellationToken cancellationToken)
    {
        var created = await journals.CreateAsync(companyId, new SaveJournalRequest(postingDate, currency, lines, kind, postingDate, description, reference), null, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        return await journals.PostAsync(created.Value.Id, cancellationToken);
    }
}
