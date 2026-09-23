using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// Bank, cash and petty-cash accounts (roadmap 4.7, DOMAIN_MODEL §14): each on a control account of the matching role
/// (the chart's default one unless another is named), reached by a posting rule keyed on the bank account so documents
/// keep posting by role; the bank transactions are its subledger and give its balance.
/// </summary>
public sealed class BankAccountService(BankingDbContext db, ICompanyDirectory companies, IChartOfAccounts chart, IPostingRules rules, IClock clock) : IBankAccountDirectory
{
    public async Task<BankAccountInfo?> FindAsync(Guid bankAccountId, CancellationToken cancellationToken = default)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == bankAccountId, cancellationToken);
        return account is null ? null : Info(account);
    }

    async Task<IReadOnlyList<BankAccountInfo>> IBankAccountDirectory.ListAsync(Guid companyId, CancellationToken cancellationToken) =>
        (await db.BankAccounts.AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(static a => a.Code).ToListAsync(cancellationToken)).Select(Info).ToList();

    public async Task<IReadOnlyList<CompanyBankAccountSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.BankAccounts.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(a => a.CompanyId == c);
        }

        var accounts = await query.OrderBy(static a => a.CompanyId).ThenBy(static a => a.Code).ToListAsync(cancellationToken);
        var result = new List<CompanyBankAccountSummary>(accounts.Count);
        foreach (var account in accounts)
        {
            result.Add(await MapAsync(account, cancellationToken));
        }

        return result;
    }

    public async Task<Result<CompanyBankAccountSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var account = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, cancellationToken);
        return account is null ? Error.NotFound("bank_account", id) : await MapAsync(account, cancellationToken);
    }

    public async Task<IReadOnlyList<BankTransactionSummary>> TransactionsAsync(Guid bankAccountId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.Transactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId);
        if (from is { } f)
        {
            query = query.Where(t => t.PostingDate >= f);
        }

        if (to is { } u)
        {
            query = query.Where(t => t.PostingDate <= u);
        }

        return (await query.OrderByDescending(static t => t.PostingDate).ThenByDescending(static t => t.CreatedAt).Take(1000).ToListAsync(cancellationToken))
            .Select(static t => new BankTransactionSummary(t.Id, t.BankAccountId, t.PostingDate, t.ValueDate, t.Kind, t.AmountTc, t.AmountFc, t.Reference, t.SourceDocumentType, t.SourceDocumentId, t.JournalEntryId, t.ReversesTransactionId, t.ReconciliationStatus)).ToList();
    }

    public async Task<Result<CompanyBankAccountSummary>> SaveAsync(Guid? id, SaveCompanyBankAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("bank_account.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var code = (request.Code ?? string.Empty).Trim().ToUpperInvariant();
        if (code.Length is < 1 or > 20)
        {
            return Error.Validation("bank_account.code_invalid", "A code is 1 to 20 characters.").WithWhy(("code", request.Code));
        }

        if (!BankAccountKinds.All.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("bank_account.kind_invalid", "The kind is bank, cash or petty_cash.").WithWhy(("kind", request.Kind));
        }

        var currency = await companies.FindCurrencyAsync((request.Currency ?? string.Empty).Trim().ToUpperInvariant(), cancellationToken);
        if (currency is null)
        {
            return Error.Validation("bank_account.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", request.Currency));
        }

        if (request.Name is null || !request.Name.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en) || !request.Name.TryGetValue("ar", out var ar) || string.IsNullOrWhiteSpace(ar))
        {
            return Error.Validation("bank_account.name_required", "A name in English and Arabic is required.");
        }

        var role = RoleOf(request.Kind);
        AccountInfo? glAccount;
        if (request.GlAccountId is { } given)
        {
            glAccount = await chart.FindAccountAsync(given, cancellationToken);
            if (glAccount is null || !string.Equals(glAccount.DefaultRole, role, StringComparison.Ordinal) || !string.Equals(glAccount.SubledgerType, SubledgerTypes.Bank, StringComparison.Ordinal))
            {
                return Error.Validation("bank_account.gl_account_invalid", $"The account must carry the {role} role and reconcile to the bank subledger.").WithWhy(("accountId", given), ("role", role));
            }
        }
        else
        {
            var chartId = await chart.ChartForCompanyAsync(company.Id, cancellationToken);
            var candidates = chartId is { } ch ? await chart.AccountsForRoleAsync(ch, role, cancellationToken) : [];
            glAccount = candidates.Count > 0 ? candidates[0] : null;
            if (glAccount is null)
            {
                return Error.Validation("bank_account.gl_account_missing", $"The company's chart has no account with the {role} role; name one.").WithWhy(("role", role));
            }
        }

        var duplicate = await db.BankAccounts.AsNoTracking().AnyAsync(a => a.CompanyId == request.CompanyId && a.Code == code && a.Id != id, cancellationToken);
        if (duplicate)
        {
            return Error.Conflict("bank_account.code_taken", "Another bank account of the company has this code.").WithWhy(("code", code));
        }

        BankAccount account;
        if (id is { } existing)
        {
            var found = await db.BankAccounts.SingleOrDefaultAsync(a => a.Id == existing, cancellationToken);
            if (found is null)
            {
                return Error.NotFound("bank_account", existing);
            }

            if (found.CompanyId != request.CompanyId || !string.Equals(found.Currency, currency.Value.Code, StringComparison.Ordinal))
            {
                var moved = await db.Transactions.AsNoTracking().AnyAsync(t => t.BankAccountId == existing, cancellationToken);
                if (moved)
                {
                    return Error.Conflict("bank_account.in_use", "An account with transactions keeps its company and currency.");
                }
            }

            account = found;
        }
        else
        {
            account = new BankAccount { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
            db.BankAccounts.Add(account);
        }

        account.CompanyId = request.CompanyId;
        account.Code = code;
        account.Name = new LocalizedText(request.Name);
        account.Kind = request.Kind;
        account.Currency = currency.Value.Code;
        account.GlAccountId = glAccount.Id;
        account.BankName = Trim(request.BankName);
        account.BranchName = Trim(request.BranchName);
        account.AccountNumber = Trim(request.AccountNumber);
        account.Iban = Trim(request.Iban)?.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
        account.Swift = Trim(request.Swift)?.ToUpperInvariant();
        account.BranchId = request.BranchId;
        account.IsActive = request.IsActive;
        account.UpdatedAt = clock.UtcNow;

        // The rule that routes the role to this account when a line names the bank account (ADR-0006 keys).
        var rule = await rules.EnsureRuleAsync(company.Id, role, new PostingKeys(BankAccountId: account.Id), glAccount.Id, cancellationToken);
        if (rule.IsFailure)
        {
            return rule.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(account, cancellationToken);
    }

    public static string RoleOf(string kind) => kind switch
    {
        BankAccountKinds.Cash => AccountRoles.Cash,
        BankAccountKinds.PettyCash => AccountRoles.PettyCash,
        _ => AccountRoles.Bank,
    };

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? Mask(string? number) => number is null ? null : number.Length <= 4 ? new string('•', number.Length) : new string('•', number.Length - 4) + number[^4..];

    private static BankAccountInfo Info(BankAccount a) => new(a.Id, a.CompanyId, a.Code, a.Name, a.Kind, a.Currency, a.GlAccountId, a.BankName, a.Iban is null ? null : Mask(a.Iban), a.BranchId, a.IsActive);

    private async Task<CompanyBankAccountSummary> MapAsync(BankAccount a, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(a.CompanyId), cancellationToken);
        var gl = await chart.FindAccountAsync(a.GlAccountId, cancellationToken);
        var balance = await db.Transactions.AsNoTracking().Where(t => t.BankAccountId == a.Id).GroupBy(static t => t.BankAccountId).Select(static g => new { Tc = g.Sum(static t => t.AmountTc), Fc = g.Sum(static t => t.AmountFc) }).FirstOrDefaultAsync(cancellationToken);
        return new CompanyBankAccountSummary(a.Id, a.CompanyId, a.Code, a.Name.Values, a.Kind, a.Currency, a.GlAccountId, gl?.Code ?? string.Empty, a.BankName, a.BranchName, Mask(a.AccountNumber), a.Iban, a.Swift, a.BranchId, a.IsActive, balance?.Tc ?? 0m, balance?.Fc ?? 0m, company?.FunctionalCurrency.Code ?? a.Currency, a.UpdatedAt);
    }
}
