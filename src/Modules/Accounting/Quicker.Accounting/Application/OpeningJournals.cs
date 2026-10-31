using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;

namespace Quicker.Accounting.Application;

/// <summary>
/// Opening balances for another module (the setup wizard, A-173): one journal of the <c>opening</c> kind in the
/// company's currency, created and posted in one go. A line on a control account carries its partner as the
/// subledger item, so the receivables and payables open items are born with the balances; what the lines do not
/// balance, posting sends to opening balance equity, and the result says how much that was.
/// </summary>
public sealed class OpeningJournals(AccountingDbContext db, ICompanyDirectory companies, IChartOfAccounts chart, ManualJournalService journals) : IOpeningJournals
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

        var codes = request.Lines.Select(static l => l.AccountCode.Trim()).Distinct(StringComparer.Ordinal).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => a.ChartId == chartId && codes.Contains(a.Code)).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        var lines = new List<JournalLineRequest>(request.Lines.Count);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (!accounts.TryGetValue(line.AccountCode.Trim(), out var account))
            {
                return Error.NotFound("account", line.AccountCode).WithWhy(("line", i + 1), ("account", line.AccountCode));
            }

            // A control account's line carries the subledger item its balance belongs to; stock and assets have their own
            // opening documents (a count, an asset register), so their controls are not opened here.
            Guid? subledgerRef = null;
            if (account.IsControl)
            {
                switch (account.SubledgerType)
                {
                    case SubledgerTypes.Receivables:
                    case SubledgerTypes.Payables:
                        if (line.PartnerId is null)
                        {
                            return Error.Validation("opening.partner_required", $"Line {i + 1}: '{account.Code}' is a control account; the line names the partner the balance is owed by or to.").WithWhy(("line", i + 1), ("account", account.Code));
                        }

                        subledgerRef = line.PartnerId;
                        break;
                    case SubledgerTypes.Bank:
                        if (line.BankAccountId is null)
                        {
                            return Error.Validation("opening.bank_account_required", $"Line {i + 1}: '{account.Code}' is a bank control account; the line names the bank account or cash box the balance sits in.").WithWhy(("line", i + 1), ("account", account.Code));
                        }

                        subledgerRef = line.BankAccountId;
                        break;
                    default:
                        return Error.Validation("opening.subledger_unsupported", $"Line {i + 1}: '{account.Code}' reconciles to the {account.SubledgerType} subledger; opening stock is entered by a stock count and assets in the asset register, not here.").WithWhy(("line", i + 1), ("account", account.Code), ("subledgerType", account.SubledgerType));
                }
            }

            lines.Add(new JournalLineRequest(line.Debit, line.Credit, account.Code, null, null, line.PartnerId,
                account.IsControl ? account.SubledgerType : null, subledgerRef, null,
                line.Description is { } text ? new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = text, ["ar"] = text } : null, line.DueDate));
        }

        var description = new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = $"Opening balances as of {request.AsOf:yyyy-MM-dd}", ["ar"] = $"الأرصدة الافتتاحية كما في {request.AsOf:yyyy-MM-dd}" };
        var created = await journals.CreateAsync(request.CompanyId, new SaveJournalRequest(request.AsOf, company.FunctionalCurrency.Code, lines, JournalKinds.Opening, request.AsOf, description, request.Reference), null, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var posted = await journals.PostAsync(created.Value.Id, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var debit = request.Lines.Sum(static l => l.Debit);
        var credit = request.Lines.Sum(static l => l.Credit);
        return new OpeningJournalResult(posted.Value.Id, posted.Value.Number, debit, credit, debit - credit);
    }
}
