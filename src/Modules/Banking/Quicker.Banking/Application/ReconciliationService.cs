using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// Bank reconciliation (roadmap 6.2): statement lines paired with the book's movements by the matcher (the same
/// amount within a few days, a deposited cheque or a slip the line clears, the sum of a day's movements, or a rule
/// that books a fee or interest and pairs it at once) or by hand; movements the books lacked created from a line;
/// the reconciliation of an account at a date, completed when the bank's balance agrees with the book's once what is
/// in transit either way is allowed for; and the aging of movements the bank has not confirmed.
/// </summary>
public sealed class ReconciliationService(
    BankingDbContext db,
    StatementService statements,
    ChequeService cheques,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    INumberAllocator numbering,
    IPostingService posting,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    /// <summary>How far apart a statement line and a book movement may be dated and still be the same thing.</summary>
    private const int MatchWindowDays = 5;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ matching

    /// <summary>Runs the matcher over the unmatched lines of a statement (or of every statement of an account).</summary>
    public async Task<Result<AutoMatchResult>> AutoMatchAsync(Guid? statementId, Guid? bankAccountId, CancellationToken cancellationToken)
    {
        Guid accountId;
        IQueryable<StatementLine> query = db.StatementLines.Where(static l => l.Status == "unmatched");
        if (statementId is { } s)
        {
            var statement = await db.Statements.AsNoTracking().SingleOrDefaultAsync(x => x.Id == s, cancellationToken);
            if (statement is null)
            {
                return Error.NotFound(BankDocumentTypes.Statement, s);
            }

            accountId = statement.BankAccountId;
            query = query.Where(l => l.StatementId == s);
        }
        else if (bankAccountId is { } a)
        {
            accountId = a;
            query = query.Where(l => l.BankAccountId == a);
        }
        else
        {
            return Error.Validation("reconciliation.scope_required", "Name the statement or the bank account to match.");
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(b => b.Id == accountId, cancellationToken);
        if (bank is null)
        {
            return Error.NotFound("bank_account", accountId);
        }

        var lines = await query.OrderBy(static l => l.BookingDate).ThenBy(static l => l.LineNo).ToListAsync(cancellationToken);
        var rules = await db.ReconciliationRules.AsNoTracking().Where(r => r.CompanyId == bank.CompanyId && r.IsActive && (r.BankAccountId == null || r.BankAccountId == bank.Id)).OrderBy(static r => r.Priority).ThenBy(static r => r.CreatedAt).ToListAsync(cancellationToken);
        var matched = 0;
        var created = 0;
        var clearedCheques = 0;
        foreach (var line in lines)
        {
            var candidates = await AvailableTransactionsAsync(bank.Id, line.BookingDate.AddDays(-MatchWindowDays), line.BookingDate.AddDays(MatchWindowDays), cancellationToken);

            // 1. The same amount, close in date; the one whose reference the line quotes wins a tie.
            var exact = candidates.Where(t => t.AmountTc == line.Amount).ToList();
            var chosen = exact.Count == 1 ? exact[0] : exact.FirstOrDefault(t => Quotes(line, t.Reference));
            if (chosen is not null)
            {
                await PairAsync(line, [chosen], null, "auto", chosen.PostingDate == line.BookingDate ? 1m : 0.9m, cancellationToken);
                matched++;
                continue;
            }

            // 2. A cheque the bank cleared or paid: the line is the cheque (or a whole deposit slip).
            var cheque = await ClearChequeAsync(bank, line, cancellationToken);
            if (cheque.IsFailure)
            {
                return cheque.Error!;
            }

            if (cheque.Value)
            {
                clearedCheques++;
                matched++;
                continue;
            }

            // 3. A day's movements that add up to the line (a batch the bank booked as one).
            var sum = candidates.GroupBy(static t => t.PostingDate).Where(g => g.Count() >= 2 && g.Sum(static t => t.AmountTc) == line.Amount).OrderBy(g => Math.Abs(g.Key.DayNumber - line.BookingDate.DayNumber)).FirstOrDefault();
            if (sum is not null)
            {
                await PairAsync(line, sum.ToList(), null, "auto", 0.8m, cancellationToken);
                matched++;
                continue;
            }

            // 4. A rule that knows the line: book it and pair it.
            var rule = rules.FirstOrDefault(r => RuleMatches(r, line));
            if (rule is not null && rule.AccountRole is { } role)
            {
                var made = await CreateMovementAsync(bank, line, role, rule.Name.Resolve("en"), rule.Id, cancellationToken);
                if (made.IsFailure)
                {
                    return made.Error!;
                }

                created++;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var remaining = await query.CountAsync(cancellationToken);
        return new AutoMatchResult(matched, created, clearedCheques, remaining);
    }

    /// <summary>Pairs a line with the movements chosen by hand; their sum must be the line.</summary>
    public async Task<Result<StatementLineSummary>> MatchAsync(Guid lineId, MatchLinesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var line = await db.StatementLines.SingleOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line is null)
        {
            return Error.NotFound("statement_line", lineId);
        }

        if (line.Status != "unmatched")
        {
            return Error.Conflict("reconciliation.line_not_open", "The line is matched or excluded already.").WithWhy(("status", line.Status));
        }

        var ids = request.BankTransactionIds?.Distinct().ToList() ?? [];
        if (ids.Count == 0)
        {
            return Error.Validation("reconciliation.transactions_required", "Choose the movements the line stands for.");
        }

        var transactions = await db.Transactions.Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken);
        if (transactions.Count != ids.Count || transactions.Any(t => t.BankAccountId != line.BankAccountId))
        {
            return Error.Validation("reconciliation.transactions_invalid", "Every movement must belong to the line's bank account.");
        }

        if (transactions.Any(static t => t.ReconciliationStatus != "unreconciled"))
        {
            return Error.Conflict("reconciliation.transaction_taken", "A movement is matched already.").WithWhy(("ids", transactions.Where(static t => t.ReconciliationStatus != "unreconciled").Select(static t => t.Id).ToList()));
        }

        var sum = transactions.Sum(static t => t.AmountTc);
        if (sum != line.Amount)
        {
            return Error.Validation("reconciliation.amount_mismatch", "The movements chosen do not add up to the line.").WithWhy(("line", line.Amount), ("movements", sum));
        }

        await PairAsync(line, transactions, null, "manual", 1m, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await statements.LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.Id == lineId), cancellationToken)).Single();
    }

    /// <summary>Undoes a line's matches: the movements are open again; a movement a rule created stays in the books (reverse it from its journal if it was wrong).</summary>
    public async Task<Result<StatementLineSummary>> UnmatchAsync(Guid lineId, CancellationToken cancellationToken)
    {
        var line = await db.StatementLines.SingleOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line is null)
        {
            return Error.NotFound("statement_line", lineId);
        }

        var matches = await db.ReconciliationMatches.Where(m => m.StatementLineId == lineId).ToListAsync(cancellationToken);
        var transactionIds = matches.Select(static m => m.BankTransactionId).ToList();
        var transactions = await db.Transactions.Where(t => transactionIds.Contains(t.Id)).ToListAsync(cancellationToken);
        if (transactions.Any(static t => t.ReconciliationStatus == "reconciled"))
        {
            return Error.Conflict("reconciliation.completed", "The movement is part of a completed reconciliation.");
        }

        foreach (var t in transactions)
        {
            t.ReconciliationStatus = "unreconciled";
        }

        db.ReconciliationMatches.RemoveRange(matches);
        line.Status = "unmatched";
        line.ExcludeReason = null;
        line.MatchedAt = null;
        await db.SaveChangesAsync(cancellationToken);
        return (await statements.LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.Id == lineId), cancellationToken)).Single();
    }

    /// <summary>Books a movement the books lacked, from the line: the bank against the account role named (a charge, interest, another income or expense), paired at once.</summary>
    public async Task<Result<StatementLineSummary>> CreateFromLineAsync(Guid lineId, CreateFromLineRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var line = await db.StatementLines.SingleOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line is null)
        {
            return Error.NotFound("statement_line", lineId);
        }

        if (line.Status != "unmatched")
        {
            return Error.Conflict("reconciliation.line_not_open", "The line is matched or excluded already.").WithWhy(("status", line.Status));
        }

        var roleCheck = ValidRole(request.AccountRole);
        if (roleCheck.IsFailure)
        {
            return roleCheck.Error!;
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == line.BankAccountId, cancellationToken);
        var made = await CreateMovementAsync(bank, line, request.AccountRole.Trim(), request.Description, null, cancellationToken);
        if (made.IsFailure)
        {
            return made.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (await statements.LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.Id == lineId), cancellationToken)).Single();
    }

    public async Task<Result<StatementLineSummary>> ExcludeAsync(Guid lineId, ExcludeLineRequest request, bool exclude, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var line = await db.StatementLines.SingleOrDefaultAsync(l => l.Id == lineId, cancellationToken);
        if (line is null)
        {
            return Error.NotFound("statement_line", lineId);
        }

        if (exclude && line.Status != "unmatched")
        {
            return Error.Conflict("reconciliation.line_not_open", "Only an unmatched line is excluded.").WithWhy(("status", line.Status));
        }

        if (!exclude && line.Status != "excluded")
        {
            return Error.Conflict("reconciliation.line_not_excluded", "The line is not excluded.").WithWhy(("status", line.Status));
        }

        line.Status = exclude ? "excluded" : "unmatched";
        line.ExcludeReason = exclude ? (string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()) : null;
        await db.SaveChangesAsync(cancellationToken);
        return (await statements.LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.Id == lineId), cancellationToken)).Single();
    }

    /// <summary>The account's unmatched statement lines, for pairing by hand.</summary>
    public async Task<IReadOnlyList<StatementLineSummary>> UnmatchedLinesAsync(Guid bankAccountId, CancellationToken cancellationToken) =>
        await statements.LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.BankAccountId == bankAccountId && l.Status == "unmatched"), cancellationToken);

    /// <summary>The account's movements the bank has not confirmed, oldest first.</summary>
    public async Task<IReadOnlyList<BankTransactionSummary>> UnmatchedTransactionsAsync(Guid bankAccountId, DateOnly? asOf, CancellationToken cancellationToken)
    {
        var query = db.Transactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId && t.ReconciliationStatus == "unreconciled");
        if (asOf is { } day)
        {
            query = query.Where(t => t.PostingDate <= day);
        }

        return (await query.OrderBy(static t => t.PostingDate).ThenBy(static t => t.CreatedAt).Take(2000).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    // ------------------------------------------------------------------ rules

    public async Task<IReadOnlyList<ReconciliationRuleSummary>> ListRulesAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.ReconciliationRules.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        return (await query.OrderBy(static r => r.Priority).ThenBy(static r => r.CreatedAt).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<ReconciliationRuleSummary>> SaveRuleAsync(Guid? id, SaveReconciliationRuleRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Name is null || !request.Name.TryGetValue("en", out var en) || string.IsNullOrWhiteSpace(en))
        {
            return Error.Validation("reconciliation_rule.name_required", "A rule has an English name (and an Arabic one).");
        }

        if (request.Direction is not ("in" or "out" or "any"))
        {
            return Error.Validation("reconciliation_rule.direction_invalid", "The direction is in, out or any.").WithWhy(("direction", request.Direction));
        }

        var roleCheck = ValidRole(request.AccountRole);
        if (roleCheck.IsFailure)
        {
            return roleCheck.Error!;
        }

        var conditions = request.Conditions ?? new RuleConditions();
        if ((conditions.DescriptionContains is null || conditions.DescriptionContains.All(string.IsNullOrWhiteSpace)) && string.IsNullOrWhiteSpace(conditions.ReferencePattern) && conditions.AmountMin is null && conditions.AmountMax is null)
        {
            return Error.Validation("reconciliation_rule.conditions_required", "A rule looks for something: words in the text, a reference pattern or an amount range.");
        }

        if (!string.IsNullOrWhiteSpace(conditions.ReferencePattern))
        {
            try
            {
                _ = new Regex(conditions.ReferencePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException)
            {
                return Error.Validation("reconciliation_rule.pattern_invalid", "The reference pattern is not a valid regular expression.").WithWhy(("pattern", conditions.ReferencePattern));
            }
        }

        if (request.BankAccountId is { } b && !await db.BankAccounts.AsNoTracking().AnyAsync(a => a.Id == b && a.CompanyId == request.CompanyId, cancellationToken))
        {
            return Error.Validation("reconciliation_rule.bank_account_invalid", "The bank account is not one of the company's.").WithWhy(("bankAccountId", b));
        }

        ReconciliationRule rule;
        if (id is { } existing)
        {
            var found = await db.ReconciliationRules.SingleOrDefaultAsync(r => r.Id == existing, cancellationToken);
            if (found is null)
            {
                return Error.NotFound("reconciliation_rule", existing);
            }

            rule = found;
        }
        else
        {
            rule = new ReconciliationRule { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
            db.ReconciliationRules.Add(rule);
        }

        rule.Name = new LocalizedText(request.Name);
        rule.Priority = request.Priority;
        rule.IsActive = request.IsActive;
        rule.Direction = request.Direction;
        rule.Conditions = JsonSerializer.Serialize(conditions with { DescriptionContains = conditions.DescriptionContains?.Where(static w => !string.IsNullOrWhiteSpace(w)).Select(static w => w.Trim()).ToList() }, Json);
        rule.Action = "post_to_account";
        rule.AccountRole = request.AccountRole.Trim();
        rule.BankAccountId = request.BankAccountId;
        rule.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("bank_reconciliation_rule", rule.Id, en, id is null ? AuditActions.Created : AuditActions.Updated, After: new { rule.Priority, rule.Direction, rule.AccountRole, rule.IsActive }, CompanyId: rule.CompanyId), cancellationToken);
        return Map(rule);
    }

    public async Task<Result> DeleteRuleAsync(Guid id, CancellationToken cancellationToken)
    {
        var rule = await db.ReconciliationRules.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (rule is null)
        {
            return Error.NotFound("reconciliation_rule", id);
        }

        db.ReconciliationRules.Remove(rule);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ reconciliation

    /// <summary>The account at a date: the bank's balance brought to the book's by what is in transit either way, and what still differs.</summary>
    public async Task<Result<BankReconciliationReport>> ReportAsync(Guid bankAccountId, DateOnly asOf, decimal? statementBalance, CancellationToken cancellationToken)
    {
        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(b => b.Id == bankAccountId, cancellationToken);
        if (bank is null)
        {
            return Error.NotFound("bank_account", bankAccountId);
        }

        var balance = statementBalance ?? await db.Statements.AsNoTracking().Where(s => s.BankAccountId == bankAccountId && s.ToDate <= asOf && s.ClosingBalance != null).OrderByDescending(static s => s.ToDate).Select(static s => s.ClosingBalance).FirstOrDefaultAsync(cancellationToken) ?? 0m;
        var book = await db.Transactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId && t.PostingDate <= asOf).SumAsync(static t => t.AmountTc, cancellationToken);
        var outstanding = await UnmatchedTransactionsAsync(bankAccountId, asOf, cancellationToken);
        var inTransit = outstanding.Where(static t => t.AmountTc > 0m).Sum(static t => t.AmountTc);
        var payments = -outstanding.Where(static t => t.AmountTc < 0m).Sum(static t => t.AmountTc);
        var unmatchedLines = await statements.LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.BankAccountId == bankAccountId && l.Status == "unmatched" && l.BookingDate <= asOf), cancellationToken);
        var unmatchedItems = unmatchedLines.Sum(static l => l.Amount);
        var adjustedBank = balance + inTransit - payments;
        var difference = adjustedBank - (book + unmatchedItems);
        return new BankReconciliationReport(bankAccountId, bank.Currency, asOf, balance, book, inTransit, payments, adjustedBank, unmatchedItems, difference, difference == 0m && unmatchedLines.Count == 0, outstanding, unmatchedLines);
    }

    public async Task<IReadOnlyList<BankReconciliationSummary>> ListReconciliationsAsync(Guid? bankAccountId, Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Reconciliations.AsNoTracking().AsQueryable();
        if (bankAccountId is { } a)
        {
            query = query.Where(r => r.BankAccountId == a);
        }

        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        var list = await query.OrderByDescending(static r => r.AsOf).ThenByDescending(static r => r.CreatedAt).Take(200).ToListAsync(cancellationToken);
        var banks = await db.BankAccounts.AsNoTracking().Where(b => list.Select(r => r.BankAccountId).Contains(b.Id)).ToDictionaryAsync(static b => b.Id, static b => b.Code, cancellationToken);
        return list.Select(r => Map(r, banks.GetValueOrDefault(r.BankAccountId, string.Empty))).ToList();
    }

    /// <summary>Records the account's reconciliation at a date with the statement balance given; completed at once when nothing differs.</summary>
    public async Task<Result<BankReconciliationSummary>> ReconcileAsync(CreateReconciliationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var report = await ReportAsync(request.BankAccountId, request.AsOf, request.StatementBalance, cancellationToken);
        if (report.IsFailure)
        {
            return report.Error!;
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleAsync(b => b.Id == request.BankAccountId, cancellationToken);
        var later = await db.Reconciliations.AsNoTracking().AnyAsync(r => r.BankAccountId == bank.Id && r.Status == "completed" && r.AsOf >= request.AsOf, cancellationToken);
        if (later)
        {
            return Error.Conflict("reconciliation.later_completed", "The account was reconciled at this date or later already.").WithWhy(("asOf", request.AsOf));
        }

        var r = report.Value;
        var reconciliation = new BankReconciliation
        {
            Id = Guid.CreateVersion7(),
            CompanyId = bank.CompanyId,
            BankAccountId = bank.Id,
            AsOf = request.AsOf,
            StatementBalance = r.StatementBalance,
            BookBalance = r.BookBalance,
            DepositsInTransit = r.DepositsInTransit,
            OutstandingPayments = r.OutstandingPayments,
            UnmatchedBankItems = r.UnmatchedBankItems,
            Difference = r.Difference,
            Status = "open",
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(BankDocumentTypes.Reconciliation, new CompanyId(bank.CompanyId), "REC", "REC-{yyyy}-{seq:4}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(BankDocumentTypes.Reconciliation, new CompanyId(bank.CompanyId), null, request.AsOf, reconciliation.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        reconciliation.Number = number.Value.Text;
        db.Reconciliations.Add(reconciliation);
        if (r.CanComplete)
        {
            await CompleteCoreAsync(reconciliation, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Reconciliation, reconciliation.Id, reconciliation.Number, reconciliation.Status == "completed" ? AuditActions.Posted : AuditActions.Created, After: new { reconciliation.AsOf, reconciliation.StatementBalance, reconciliation.BookBalance, reconciliation.Difference, reconciliation.Status }, CompanyId: reconciliation.CompanyId), cancellationToken);
        return Map(reconciliation, bank.Code);
    }

    /// <summary>Completes an open reconciliation once the matching is done: the figures are taken afresh and must agree.</summary>
    public async Task<Result<BankReconciliationSummary>> CompleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var reconciliation = await db.Reconciliations.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (reconciliation is null)
        {
            return Error.NotFound(BankDocumentTypes.Reconciliation, id);
        }

        if (reconciliation.Status == "completed")
        {
            return Error.Conflict("reconciliation.already_completed", "The reconciliation is completed.");
        }

        var report = await ReportAsync(reconciliation.BankAccountId, reconciliation.AsOf, reconciliation.StatementBalance, cancellationToken);
        if (report.IsFailure)
        {
            return report.Error!;
        }

        var r = report.Value;
        reconciliation.BookBalance = r.BookBalance;
        reconciliation.DepositsInTransit = r.DepositsInTransit;
        reconciliation.OutstandingPayments = r.OutstandingPayments;
        reconciliation.UnmatchedBankItems = r.UnmatchedBankItems;
        reconciliation.Difference = r.Difference;
        reconciliation.UpdatedAt = clock.UtcNow;
        if (!r.CanComplete)
        {
            await db.SaveChangesAsync(cancellationToken);
            return Error.Conflict("reconciliation.not_balanced", "The bank and the books still differ, or bank lines are still unmatched.").WithWhy(("difference", r.Difference), ("unmatchedLines", r.UnmatchedLines.Count));
        }

        await CompleteCoreAsync(reconciliation, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var bank = await db.BankAccounts.AsNoTracking().Where(b => b.Id == reconciliation.BankAccountId).Select(static b => b.Code).SingleAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Reconciliation, reconciliation.Id, reconciliation.Number, AuditActions.Posted, After: new { reconciliation.AsOf, reconciliation.StatementBalance, reconciliation.BookBalance }, CompanyId: reconciliation.CompanyId), cancellationToken);
        return Map(reconciliation, bank);
    }

    /// <summary>Movements the bank has not confirmed, by how long they have waited at the date, per account.</summary>
    public async Task<IReadOnlyList<UnreconciledAgingRow>> AgingAsync(Guid companyId, DateOnly asOf, CancellationToken cancellationToken)
    {
        var accounts = await db.BankAccounts.AsNoTracking().Where(b => b.CompanyId == companyId && b.IsActive).OrderBy(static b => b.Code).ToListAsync(cancellationToken);
        var rows = new List<UnreconciledAgingRow>();
        foreach (var account in accounts)
        {
            var open = await db.Transactions.AsNoTracking().Where(t => t.BankAccountId == account.Id && t.ReconciliationStatus == "unreconciled" && t.PostingDate <= asOf).Select(static t => new { t.PostingDate, t.AmountTc }).ToListAsync(cancellationToken);
            decimal Bucket(int fromDays, int? toDays) => open.Where(t => { var age = asOf.DayNumber - t.PostingDate.DayNumber; return age >= fromDays && (toDays is null || age <= toDays); }).Sum(static t => t.AmountTc);
            rows.Add(new UnreconciledAgingRow(account.Id, account.Code, account.Currency, open.Count, Bucket(0, 30), Bucket(31, 60), Bucket(61, 90), Bucket(91, null), open.Sum(static t => t.AmountTc)));
        }

        return rows;
    }

    // ------------------------------------------------------------------ internals

    private async Task<List<BankTransaction>> AvailableTransactionsAsync(Guid bankAccountId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var fromDb = await db.Transactions.Where(t => t.BankAccountId == bankAccountId && t.ReconciliationStatus == "unreconciled" && t.PostingDate >= from && t.PostingDate <= to).ToListAsync(cancellationToken);
        // Movements paired earlier in this same run are tracked as matched but not yet saved: keep them out.
        return fromDb.Where(static t => t.ReconciliationStatus == "unreconciled").ToList();
    }

    private static bool Quotes(StatementLine line, string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference.Length < 3)
        {
            return false;
        }

        return line.Description.Contains(reference, StringComparison.OrdinalIgnoreCase) || (line.Reference?.Contains(reference, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static bool RuleMatches(ReconciliationRule rule, StatementLine line)
    {
        if (rule.Direction == "in" && line.Amount <= 0m)
        {
            return false;
        }

        if (rule.Direction == "out" && line.Amount >= 0m)
        {
            return false;
        }

        var conditions = JsonSerializer.Deserialize<RuleConditions>(rule.Conditions, Json) ?? new RuleConditions();
        var text = $"{line.Description} {line.Counterparty} {line.Reference}";
        if (conditions.DescriptionContains is { Count: > 0 } words && !words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(conditions.ReferencePattern) && !Regex.IsMatch(text, conditions.ReferencePattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)))
        {
            return false;
        }

        var magnitude = Math.Abs(line.Amount);
        if (conditions.AmountMin is { } min && magnitude < min)
        {
            return false;
        }

        return conditions.AmountMax is not { } max || magnitude <= max;
    }

    private static Result ValidRole(string? role)
    {
        if (string.IsNullOrWhiteSpace(role) || !AccountRoles.All.Contains(role.Trim(), StringComparer.Ordinal))
        {
            return Error.Validation("reconciliation.role_unknown", "The account role is not one the chart knows.").WithWhy(("accountRole", role));
        }

        if (AccountRoles.ControlSubledgers.ContainsKey(role.Trim()))
        {
            return Error.Validation("reconciliation.role_is_control", "A control account is fed by its documents, not from a statement line; name an income or expense role.").WithWhy(("accountRole", role));
        }

        return Result.Success();
    }

    /// <summary>A cheque the line settles: a deposited received cheque (or a whole slip) for a credit, an issued cheque for a debit; cleared or paid on the line's date and paired. A cheque that refuses the step is left for a person.</summary>
    private async Task<Result<bool>> ClearChequeAsync(BankAccount bank, StatementLine line, CancellationToken cancellationToken)
    {
        if (line.Amount > 0m)
        {
            var deposited = await db.Cheques.AsNoTracking().Where(c => c.BankAccountId == bank.Id && c.Direction == ChequeDirections.Received && c.Status == ChequeStatuses.Deposited && c.DueDate <= line.BookingDate).ToListAsync(cancellationToken);
            var byAmount = deposited.Where(c => c.Amount == line.Amount).ToList();
            var one = byAmount.Count == 1 ? byAmount[0] : byAmount.FirstOrDefault(c => Quotes(line, c.ChequeNumber));
            if (one is not null)
            {
                var cleared = await cheques.ClearAsync(one.Id, new ChequeActionRequest(line.BookingDate, 0m, "Cleared from the bank statement"), cancellationToken);
                if (cleared.IsFailure)
                {
                    // A cheque the line cannot clear (dated before its deposit, a closed period) is left for a person; the run goes on.
                    return false;
                }

                await PairAsync(line, await db.Transactions.Where(t => t.Id == cleared.Value.BankTransactionId).ToListAsync(cancellationToken), null, "cheque", 1m, cancellationToken);
                return true;
            }

            var slips = deposited.GroupBy(static c => c.DepositId).Where(g => g.Key is not null && g.Sum(static c => c.Amount) == line.Amount).ToList();
            if (slips.Count == 1)
            {
                var slip = slips[0];
                var cleared = await cheques.ClearDepositAsync(slip.Key!.Value, new ChequeActionRequest(line.BookingDate, 0m, "Cleared from the bank statement"), cancellationToken);
                if (cleared.IsFailure)
                {
                    return false;
                }

                var ids = cleared.Value.Cheques.Select(static c => c.BankTransactionId).Where(static id => id is not null).Select(static id => id!.Value).ToList();
                await PairAsync(line, await db.Transactions.Where(t => ids.Contains(t.Id)).ToListAsync(cancellationToken), null, "cheque", 1m, cancellationToken);
                return true;
            }

            return false;
        }

        var issued = await db.Cheques.AsNoTracking().Where(c => c.BankAccountId == bank.Id && c.Direction == ChequeDirections.Issued && (c.Status == ChequeStatuses.Issued || c.Status == ChequeStatuses.Bounced) && c.Amount == -line.Amount).ToListAsync(cancellationToken);
        var pay = issued.Count == 1 ? issued[0] : issued.FirstOrDefault(c => Quotes(line, c.ChequeNumber));
        if (pay is null)
        {
            return false;
        }

        var paid = await cheques.PayAsync(pay.Id, new ChequeActionRequest(line.BookingDate, 0m, "Paid per the bank statement"), cancellationToken);
        if (paid.IsFailure)
        {
            return false;
        }

        await PairAsync(line, await db.Transactions.Where(t => t.Id == paid.Value.BankTransactionId).ToListAsync(cancellationToken), null, "cheque", 1m, cancellationToken);
        return true;
    }

    /// <summary>Posts the bank against the role for the line's amount and pairs the movement with the line.</summary>
    private async Task<Result> CreateMovementAsync(BankAccount bank, StatementLine line, string role, string? description, Guid? ruleId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(bank.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", bank.CompanyId);
        }

        var fc = company.FunctionalCurrency;
        var same = string.Equals(bank.Currency, fc.Code, StringComparison.Ordinal);
        decimal? rate = null;
        var amountFc = line.Amount;
        if (!same)
        {
            var resolved = await rates.ResolveAsync(company.Id, bank.Currency, fc.Code, line.BookingDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return Error.Validation("reconciliation.rate_missing", "No spot rate from the account's currency to the company's on the line's date.").WithWhy(("from", bank.Currency), ("to", fc.Code), ("date", line.BookingDate));
            }

            rate = resolved.Value.Rate.Rate;
            amountFc = new RoundingPolicy(company.RoundingMode).Round(line.Amount * rate.Value, fc.MinorUnits);
        }

        var transactionId = Guid.CreateVersion7();
        var text = string.IsNullOrWhiteSpace(description) ? line.Description : description.Trim();
        var kind = role == AccountRoles.BankCharges ? "charge" : role is AccountRoles.InterestIncome or AccountRoles.InterestExpense ? "interest" : line.Amount < 0m ? "disbursement" : "receipt";
        var lines = new List<PostingLine>
        {
            new(BankAccountService.RoleOf(bank.Kind), line.Amount, new PostingKeys(BankDocumentTypes.Transaction, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: amountFc, Description: LocalizedText.Bilingual(text, text)),
            new(role, -line.Amount, new PostingKeys(BankDocumentTypes.Transaction), AmountFc: -amountFc, Description: LocalizedText.Bilingual(text, text)),
        };
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "banking", BankDocumentTypes.Transaction, transactionId, line.BookingDate, bank.Currency, lines, line.Reference, line.BookingDate,
            LocalizedText.Bilingual($"Bank statement: {text}", $"كشف البنك: {text}"), null, RateTypes.Spot, rate, rate is null ? null : "Statement date rate", $"bank_transaction:{transactionId}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var transaction = new BankTransaction
        {
            Id = transactionId,
            BankAccountId = bank.Id,
            CompanyId = bank.CompanyId,
            PostingDate = line.BookingDate,
            ValueDate = line.ValueDate ?? line.BookingDate,
            Kind = kind,
            AmountTc = line.Amount,
            AmountFc = amountFc,
            Reference = line.Reference ?? (text.Length > 120 ? text[..120] : text),
            SourceDocumentType = BankDocumentTypes.Statement,
            SourceDocumentId = line.StatementId,
            JournalEntryId = posted.Value.EntryId,
            CreatedAt = clock.UtcNow,
        };
        db.Transactions.Add(transaction);
        await PairAsync(line, [transaction], ruleId, "created", 1m, cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Transaction, transactionId, line.Reference ?? text, AuditActions.Posted, After: new { line.BookingDate, line.Amount, role, journal = posted.Value.Number, fromStatement = line.StatementId }, CompanyId: bank.CompanyId), cancellationToken);
        return Result.Success();
    }

    private Task PairAsync(StatementLine line, IReadOnlyList<BankTransaction> transactions, Guid? ruleId, string kind, decimal confidence, CancellationToken cancellationToken)
    {
        foreach (var t in transactions)
        {
            t.ReconciliationStatus = "matched";
            db.ReconciliationMatches.Add(new ReconciliationMatch
            {
                Id = Guid.CreateVersion7(),
                StatementLineId = line.Id,
                BankTransactionId = t.Id,
                RuleId = ruleId,
                MatchKind = kind,
                Confidence = confidence,
                MatchedBy = principal.Principal?.MembershipId.Value,
                MatchedAt = clock.UtcNow,
            });
        }

        line.Status = "matched";
        line.MatchedAt = clock.UtcNow;
        return Task.CompletedTask;
    }

    private async Task CompleteCoreAsync(BankReconciliation reconciliation, CancellationToken cancellationToken)
    {
        var matched = await db.Transactions.Where(t => t.BankAccountId == reconciliation.BankAccountId && t.ReconciliationStatus == "matched" && t.PostingDate <= reconciliation.AsOf).ToListAsync(cancellationToken);
        foreach (var t in matched)
        {
            t.ReconciliationStatus = "reconciled";
        }

        var statementsDone = await db.Statements.Where(s => s.BankAccountId == reconciliation.BankAccountId && s.Status == "imported" && s.ToDate <= reconciliation.AsOf).ToListAsync(cancellationToken);
        foreach (var s in statementsDone)
        {
            s.Status = "reconciled";
        }

        reconciliation.Status = "completed";
        reconciliation.CompletedBy = principal.Principal?.MembershipId.Value;
        reconciliation.CompletedAt = clock.UtcNow;
        reconciliation.UpdatedAt = clock.UtcNow;
    }

    private static BankTransactionSummary Map(BankTransaction t) => new(t.Id, t.BankAccountId, t.PostingDate, t.ValueDate, t.Kind, t.AmountTc, t.AmountFc, t.Reference, t.SourceDocumentType, t.SourceDocumentId, t.JournalEntryId, t.ReversesTransactionId, t.ReconciliationStatus);

    private static ReconciliationRuleSummary Map(ReconciliationRule r) => new(r.Id, r.CompanyId, r.Name.Values, r.Priority, r.IsActive, r.Direction, JsonSerializer.Deserialize<RuleConditions>(r.Conditions, Json) ?? new RuleConditions(), r.Action, r.AccountRole, r.BankAccountId, r.UpdatedAt);

    private static BankReconciliationSummary Map(BankReconciliation r, string bankCode) => new(r.Id, r.CompanyId, r.BankAccountId, bankCode, r.Number, r.AsOf, r.StatementBalance, r.BookBalance, r.DepositsInTransit, r.OutstandingPayments, r.UnmatchedBankItems, r.Difference, r.Status, r.CompletedAt, r.CreatedAt);
}
