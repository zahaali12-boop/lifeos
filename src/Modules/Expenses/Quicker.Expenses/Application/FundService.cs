using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Expenses.Contracts;
using Quicker.Expenses.Domain;
using Quicker.Expenses.Persistence;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;

namespace Quicker.Expenses.Application;

/// <summary>
/// Petty cash funds (A-174): a cash box with its custodian and float. Vouchers (petty cash expenses) spend it; a
/// replenishment moves cash from a bank account into it, Dr the fund's cash account / Cr the bank, both movements in
/// the bank transactions subledger, so the fund's balance is always what its movements say.
/// </summary>
public sealed class FundService(
    ExpensesDbContext db,
    ICompanyDirectory companies,
    IBankAccountDirectory bankAccounts,
    IBankTransactions bankTransactions,
    IPostingService posting,
    INumberAllocator numbering,
    IAuditSink audit,
    IClock clock)
{
    public const string EntityType = "petty_cash_fund";
    public const string ReplenishmentType = ExpenseDocumentTypes.Replenishment;

    public async Task<IReadOnlyList<FundSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Funds.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(f => f.CompanyId == c);
        }

        var funds = await query.OrderBy(static f => f.CompanyId).ThenBy(static f => f.Code).ToListAsync(cancellationToken);
        var result = new List<FundSummary>(funds.Count);
        foreach (var fund in funds)
        {
            result.Add(await MapAsync(fund, cancellationToken));
        }

        return result;
    }

    public async Task<Result<FundSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var fund = await db.Funds.AsNoTracking().SingleOrDefaultAsync(f => f.Id == id, cancellationToken);
        return fund is null ? Error.NotFound(EntityType, id) : await MapAsync(fund, cancellationToken);
    }

    public async Task<Result<FundSummary>> SaveAsync(Guid? id, SaveFundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, EntityType);
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, EntityType);
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var box = await bankAccounts.FindAsync(request.BankAccountId, cancellationToken);
        if (box is null || box.CompanyId != request.CompanyId)
        {
            return Error.NotFound("bank_account", request.BankAccountId);
        }

        if (box.Kind is not (BankAccountKinds.Cash or BankAccountKinds.PettyCash))
        {
            return Error.Validation("fund.bank_account_kind", "A petty cash fund sits on a cash box (a cash or petty cash account), not a bank account.").WithWhy(("kind", box.Kind));
        }

        var boxCurrency = await companies.FindCurrencyAsync(box.Currency, cancellationToken);
        if (boxCurrency is null || request.FloatAmount < 0m || !new Money(request.FloatAmount, boxCurrency.Value).IsRoundedToMinorUnit)
        {
            return Error.Validation("fund.float_invalid", "The float is zero or above, in the cash box's currency.");
        }

        PettyCashFund? fund = null;
        if (id is { } existing)
        {
            fund = await db.Funds.SingleOrDefaultAsync(f => f.Id == existing, cancellationToken);
            if (fund is null)
            {
                return Error.NotFound(EntityType, existing);
            }

            if (fund.CompanyId != request.CompanyId)
            {
                return Error.Validation("fund.company_fixed", "A fund stays in its company.");
            }
        }

        if (await db.Funds.AnyAsync(f => f.CompanyId == request.CompanyId && f.Code == code.Value && (fund == null || f.Id != fund.Id), cancellationToken))
        {
            return Error.Conflict("fund.code_taken", $"The company already has a fund '{code.Value}'.");
        }

        if (await db.Funds.AnyAsync(f => f.BankAccountId == request.BankAccountId && (fund == null || f.Id != fund.Id), cancellationToken))
        {
            return Error.Conflict("fund.bank_account_taken", "Another fund already sits on that cash box.");
        }

        var isNew = fund is null;
        fund ??= new PettyCashFund { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        fund.Code = code.Value;
        fund.Name = name.Value;
        fund.BankAccountId = box.Id;
        fund.CustodianMembershipId = request.CustodianMembershipId;
        fund.FloatAmount = request.FloatAmount;
        fund.Currency = box.Currency;
        fund.IsActive = request.IsActive;
        fund.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Funds.Add(fund);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, fund.Id, fund.Code, isNew ? AuditActions.Created : AuditActions.Updated, After: new { fund.Code, fund.FloatAmount, fund.Currency, fund.IsActive }, CompanyId: fund.CompanyId), cancellationToken);
        return await MapAsync(fund, cancellationToken);
    }

    public async Task<IReadOnlyList<ReplenishmentSummary>> ReplenishmentsAsync(Guid fundId, CancellationToken cancellationToken) =>
        (await db.Replenishments.AsNoTracking().Where(r => r.FundId == fundId).OrderByDescending(static r => r.ReplenishedOn).ThenByDescending(static r => r.CreatedAt).ToListAsync(cancellationToken)).Select(Map).ToList();

    /// <summary>Cash from a bank account into the fund on a day: Dr the fund's cash box / Cr the bank, both movements recorded, posted at once.</summary>
    public async Task<Result<ReplenishmentSummary>> ReplenishAsync(Guid fundId, ReplenishFundRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var fund = await db.Funds.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fundId, cancellationToken);
        if (fund is null)
        {
            return Error.NotFound(EntityType, fundId);
        }

        if (!fund.IsActive)
        {
            return Error.Conflict("fund.inactive", "The fund is closed.");
        }

        var company = (await companies.FindAsync(new CompanyId(fund.CompanyId), cancellationToken))!;
        var box = await bankAccounts.FindAsync(fund.BankAccountId, cancellationToken);
        var source = await bankAccounts.FindAsync(request.FromBankAccountId, cancellationToken);
        if (box is null || source is null || source.CompanyId != fund.CompanyId)
        {
            return Error.NotFound("bank_account", request.FromBankAccountId);
        }

        if (source.Id == box.Id)
        {
            return Error.Validation("fund.same_account", "A fund is replenished from another account.");
        }

        if (!string.Equals(source.Currency, fund.Currency, StringComparison.Ordinal))
        {
            return Error.Validation("fund.currency_mismatch", $"The fund keeps {fund.Currency}; replenish it from an account in that currency.").WithWhy(("fundCurrency", fund.Currency), ("sourceCurrency", source.Currency));
        }

        var currency = await companies.FindCurrencyAsync(fund.Currency, cancellationToken);
        if (currency is null || request.Amount <= 0m || !new Money(request.Amount, currency.Value).IsRoundedToMinorUnit)
        {
            return Error.Validation("fund.amount_invalid", "The amount is above zero, in the fund's currency, to its minor unit.");
        }

        var day = request.ReplenishedOn ?? clock.TodayIn(company.TimeZone);
        var sameCurrency = string.Equals(fund.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        if (!sameCurrency)
        {
            return Error.Validation("fund.foreign_currency", "Funds in a currency other than the company's are replenished through a bank transfer (roadmap 6.2).").WithWhy(("currency", fund.Currency));
        }

        var replenishment = new FundReplenishment
        {
            Id = Guid.CreateVersion7(),
            CompanyId = fund.CompanyId,
            FundId = fund.Id,
            ReplenishedOn = day,
            Amount = request.Amount,
            FromBankAccountId = source.Id,
            Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim(),
            FundTransactionId = Guid.CreateVersion7(),
            BankTransactionId = Guid.CreateVersion7(),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(ReplenishmentType, new CompanyId(fund.CompanyId), "PCR", "PCR-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var allocated = await numbering.AllocateAsync(new NumberRequest(ReplenishmentType, new CompanyId(fund.CompanyId), null, day, replenishment.Id), cancellationToken);
        if (allocated.IsFailure)
        {
            return allocated.Error!;
        }

        replenishment.Number = allocated.Value.Text;
        var lines = new List<PostingLine>
        {
            new(RoleOf(box.Kind), request.Amount, new PostingKeys(ReplenishmentType, BankAccountId: box.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: replenishment.FundTransactionId),
            new(RoleOf(source.Kind), -request.Amount, new PostingKeys(ReplenishmentType, BankAccountId: source.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: replenishment.BankTransactionId),
        };
        var description = LocalizedText.Bilingual($"Petty cash replenishment {replenishment.Number} – {fund.Code}", $"تغذية الصندوق النثري {replenishment.Number} – {fund.Code}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "expenses", ReplenishmentType, replenishment.Id, day, fund.Currency, lines, replenishment.Number, day, description, IdempotencyKey: $"fund_replenishment:{replenishment.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var reference = replenishment.Reference ?? replenishment.Number;
        await bankTransactions.RecordAsync(new NewBankTransaction(replenishment.FundTransactionId, box.Id, fund.CompanyId, day, "transfer_in", request.Amount, request.Amount, reference, ReplenishmentType, replenishment.Id, posted.Value.EntryId), cancellationToken);
        await bankTransactions.RecordAsync(new NewBankTransaction(replenishment.BankTransactionId, source.Id, fund.CompanyId, day, "transfer_out", -request.Amount, -request.Amount, reference, ReplenishmentType, replenishment.Id, posted.Value.EntryId), cancellationToken);
        replenishment.JournalEntryId = posted.Value.EntryId;
        db.Replenishments.Add(replenishment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReplenishmentType, replenishment.Id, replenishment.Number, AuditActions.Posted, After: new { fund = fund.Code, replenishment.Amount, from = source.Code, journalEntryId = replenishment.JournalEntryId }, CompanyId: fund.CompanyId), cancellationToken);
        return Map(replenishment);
    }

    public async Task<Result<ReplenishmentSummary>> ReverseReplenishmentAsync(Guid id, ReverseExpenseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var replenishment = await db.Replenishments.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (replenishment is null)
        {
            return Error.NotFound(ReplenishmentType, id);
        }

        if (replenishment.Status != "posted" || replenishment.JournalEntryId is null)
        {
            return Error.Conflict("fund.replenishment_not_posted", "Only a posted replenishment is reversed.").WithWhy(("status", replenishment.Status));
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("fund.reason_required", "A reversal names its reason.");
        }

        var reversed = await posting.ReverseAsync(replenishment.JournalEntryId.Value, request.ReversalDate, request.Reason, cancellationToken: cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        await bankTransactions.ReverseDocumentAsync(ReplenishmentType, replenishment.Id, reversed.Value.PostingDate, reversed.Value.EntryId, cancellationToken);
        replenishment.Status = "reversed";
        replenishment.ReversalEntryId = reversed.Value.EntryId;
        replenishment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReplenishmentType, replenishment.Id, replenishment.Number, AuditActions.Reversed, After: new { reversalEntryId = replenishment.ReversalEntryId }, Reason: request.Reason, CompanyId: replenishment.CompanyId), cancellationToken);
        return Map(replenishment);
    }

    private static string RoleOf(string kind) => kind switch
    {
        BankAccountKinds.Cash => AccountRoles.Cash,
        BankAccountKinds.PettyCash => AccountRoles.PettyCash,
        _ => AccountRoles.Bank,
    };

    private async Task<FundSummary> MapAsync(PettyCashFund f, CancellationToken cancellationToken)
    {
        var box = await bankAccounts.FindAsync(f.BankAccountId, cancellationToken);
        var balance = await bankTransactions.BalanceAsync(f.BankAccountId, null, cancellationToken);
        var toReplenish = f.FloatAmount - balance;
        return new FundSummary(f.Id, f.CompanyId, f.Code, f.Name.Values, f.BankAccountId, box?.Code ?? string.Empty, f.CustodianMembershipId, f.FloatAmount, f.Currency, balance, toReplenish > 0m ? toReplenish : 0m, f.IsActive, f.UpdatedAt);
    }

    private static ReplenishmentSummary Map(FundReplenishment r) => new(r.Id, r.CompanyId, r.FundId, r.Number, r.ReplenishedOn, r.Amount, r.FromBankAccountId, r.Reference, r.Status, r.JournalEntryId, r.ReversalEntryId, r.CreatedAt);
}
