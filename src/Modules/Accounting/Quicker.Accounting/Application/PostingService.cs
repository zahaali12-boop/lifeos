using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Outbox;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Accounting.Application;

/// <summary>
/// The posting engine (ADR-0006): account determination through the company's profile, three-currency conversion
/// with a rounding line, period and permission checks, control-account cross-checks, append-only entry and lines,
/// balance increments and the reversal service (ADR-0026). Every step that refuses names the reason and the facts.
/// </summary>
public sealed class PostingService(
    AccountingDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ICompanyDirectory companies,
    IFiscalPeriodResolver periods,
    IExchangeRateResolver rates,
    IDimensionSets dimensionSets,
    IChartOfAccounts chart,
    ProfileService profiles,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IOutbox outbox,
    IClock clock) : IPostingService
{
    public const string JournalDocumentType = "journal_entry";
    private const string BranchDimension = "BRANCH";

    private sealed record ResolvedLine(PostingLine Source, AccountInfo Account, Guid? RuleId, decimal Tc, decimal Fc, decimal Rc, Guid? DimensionSetId, Guid? BranchId, bool IsRounding);

    public async Task<Result<PostingResult>> PostAsync(PostingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count < 2)
        {
            return Error.Validation("posting.lines_required", "A journal entry needs at least two lines.");
        }

        if (string.IsNullOrWhiteSpace(request.SourceModule) || string.IsNullOrWhiteSpace(request.SourceDocumentType))
        {
            return Error.Validation("posting.source_required", "Every entry names its source module and document type.");
        }

        var company = await companies.FindAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId.Value);
        }

        if (!company.IsActive)
        {
            return Error.Conflict("company.inactive", $"Company '{company.Code}' is inactive.");
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var replay = await db.Set<JournalEntry>().Include(static e => e.Lines).SingleOrDefaultAsync(e => e.CompanyId == company.Id.Value && e.IdempotencyKey == request.IdempotencyKey, cancellationToken);
            if (replay is not null)
            {
                return await ToResultAsync(replay, replayed: true, cancellationToken);
            }
        }

        if (company.ChartId is null)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.").WithWhy(("companyId", company.Id.Value));
        }

        var currencyCode = Validation.OneOf(request.Currency?.Trim().ToUpperInvariant(), "posting.currency", [request.Currency?.Trim().ToUpperInvariant() ?? string.Empty]);
        var transactionCurrency = await companies.FindCurrencyAsync(currencyCode.Value, cancellationToken);
        if (transactionCurrency is not { } tc)
        {
            return Error.Validation("posting.currency_unknown", $"Currency '{request.Currency}' is not in the ISO 4217 list.").WithWhy(("currency", request.Currency));
        }

        var fc = company.FunctionalCurrency;
        var rc = company.ReportingCurrency;
        var policy = new RoundingPolicy(company.RoundingMode);

        // Amounts first: a request that does not balance never touches the database.
        var total = 0m;
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line.Amount == 0m)
            {
                return Error.Validation("posting.line_zero", $"Line {i + 1} has no amount.").WithWhy(("line", i + 1));
            }

            if (!new Money(line.Amount, tc).IsRoundedToMinorUnit)
            {
                return Error.Validation("posting.amount_precision", $"Line {i + 1}: {line.Amount} has more decimals than {tc.Code} allows ({tc.MinorUnits}).").WithWhy(("line", i + 1), ("amount", line.Amount), ("minorUnits", tc.MinorUnits));
            }

            total += line.Amount;
        }

        if (total != 0m)
        {
            return Error.Validation("posting.unbalanced", $"Debits and credits differ by {total} {tc.Code}.").WithWhy(("difference", total), ("currency", tc.Code));
        }

        // Period and permission (ADR-0026).
        var period = await PeriodForAsync(company, request.PostingDate, cancellationToken);
        if (period.IsFailure)
        {
            return period.Error!;
        }

        // Rates (ADR-0017).
        if (request.RateOverride is { } overrideRate && (overrideRate <= 0m || string.IsNullOrWhiteSpace(request.RateOverrideReason)))
        {
            return Error.Validation("posting.rate_override_invalid", "A rate override is a positive rate with a reason.");
        }

        var rateType = string.IsNullOrWhiteSpace(request.RateType) ? RateTypes.Spot : request.RateType.Trim().ToLowerInvariant();
        var rateTcFc = await RateAsync(company, tc, fc, request.PostingDate, rateType, request.RateOverride, cancellationToken);
        if (rateTcFc.IsFailure)
        {
            return rateTcFc.Error!;
        }

        decimal? rateFcRc = null;
        if (rc is { } reporting)
        {
            var resolved = reporting == tc ? 1m / rateTcFc.Value : await RateAsync(company, fc, reporting, request.PostingDate, rateType, null, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            rateFcRc = resolved.Value;
        }

        // Determination and validation of every line.
        var rules = await profiles.ActiveRulesAsync(company, request.PostingDate, cancellationToken);
        if (rules.IsFailure)
        {
            return rules.Error!;
        }

        var resolvedLines = new List<ResolvedLine>(request.Lines.Count + 1);
        for (var i = 0; i < request.Lines.Count; i++)
        {
            var resolved = await ResolveLineAsync(request, request.Lines[i], i + 1, company, tc, fc, rc, rateTcFc.Value, rateFcRc, policy, rules.Value.Rules, cancellationToken);
            if (resolved.IsFailure)
            {
                return resolved.Error!;
            }

            resolvedLines.Add(resolved.Value);
        }

        // Functional and reporting totals may be off by rounding: the engine appends the rounding line (POSTING_RULES §10).
        var offFc = resolvedLines.Sum(static l => l.Fc);
        var offRc = resolvedLines.Sum(static l => l.Rc);
        if (offFc != 0m || offRc != 0m)
        {
            var rounding = await RoundingLineAsync(company, -offFc, -offRc, rules.Value.Rules, cancellationToken);
            if (rounding.IsFailure)
            {
                return rounding.Error!;
            }

            resolvedLines.Add(rounding.Value);
        }

        var entry = await WriteEntryAsync(request, company, period.Value, tc, fc, rc, rateType, rateTcFc.Value, rateFcRc, rules.Value.ProfileId, resolvedLines, isReversal: false, cancellationToken);
        if (entry.IsFailure)
        {
            return entry.Error!;
        }

        await outbox.PublishAsync(new JournalEntryPosted(entry.Value.Id, company.Id.Value, entry.Value.Number, entry.Value.PostingDate, entry.Value.SourceModule, entry.Value.SourceDocumentType, entry.Value.SourceDocumentId, false, null), cancellationToken);
        return await ToResultAsync(entry.Value, replayed: false, cancellationToken);
    }

    public async Task<Result<PostingResult>> ReverseAsync(Guid entryId, DateOnly? reversalDate, string reason, bool automatic = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("reversal.reason_required", "A reversal needs a reason.");
        }

        var original = await db.Set<JournalEntry>().Include(static e => e.Lines).SingleOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (original is null)
        {
            return Error.NotFound("journal_entry", entryId);
        }

        var reversedBy = await db.Set<EntryLink>().Where(l => l.ToEntryId == entryId && l.Relation == EntryRelations.Reverses).Select(static l => (Guid?)l.FromEntryId).FirstOrDefaultAsync(cancellationToken);
        if (reversedBy is { } existing)
        {
            return Error.Conflict("reversal.already_reversed", $"Entry {original.Number} was already reversed.").WithWhy(("reversedByEntryId", existing));
        }

        if (original.IsReversal)
        {
            return Error.Conflict("reversal.of_reversal", $"Entry {original.Number} is itself a reversal; post a correction instead.");
        }

        var company = (await companies.FindAsync(new CompanyId(original.CompanyId), cancellationToken))!;
        DateOnly date;
        if (reversalDate is { } requested)
        {
            if (requested < original.PostingDate)
            {
                return Error.Validation("reversal.date_before_original", "A reversal is dated on or after the entry it reverses.").WithWhy(("originalDate", original.PostingDate));
            }

            date = requested;
        }
        else
        {
            var onOriginal = await PeriodForAsync(company, original.PostingDate, cancellationToken);
            if (onOriginal.IsSuccess)
            {
                date = original.PostingDate;
            }
            else
            {
                var next = await periods.FirstOpenPeriodAsync(company.Id, original.PostingDate, PostingModules.GeneralLedger, cancellationToken);
                if (next.IsFailure)
                {
                    return next.Error!;
                }

                date = next.Value.StartsOn > original.PostingDate ? next.Value.StartsOn : original.PostingDate;
            }
        }

        var period = await PeriodForAsync(company, date, cancellationToken);
        if (period.IsFailure)
        {
            return period.Error!;
        }

        var accounts = await db.Accounts.Where(a => original.Lines.Select(static l => l.AccountId).Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        var mirrored = original.Lines.OrderBy(static l => l.LineNo).Select(l =>
        {
            var account = accounts[l.AccountId];
            var info = new AccountInfo(account.Id, account.ChartId, account.Code, account.Name, account.Type, account.Subtype, account.IsHeader, account.IsControl, account.SubledgerType, account.CurrencyRestriction, account.AllowManualPosting, account.RevalueFx, account.DefaultRole, account.CompanyId, account.IsActive);
            var source = new PostingLine(l.AccountRole, l.CreditTc - l.DebitTc, null, l.AccountId, null, l.PartnerId, l.SubledgerType, l.SubledgerRef, l.TaxCodeId, l.TaxBaseTc, l.Description, l.DueDate);
            return new ResolvedLine(source, info, l.PostingRuleId, l.CreditTc - l.DebitTc, l.CreditFc - l.DebitFc, l.CreditRc - l.DebitRc, l.DimensionSetId, l.BranchId, l.IsRounding);
        }).ToList();

        var tc = (await companies.FindCurrencyAsync(original.CurrencyTc, cancellationToken))!.Value;
        var request = new PostingRequest(company.Id, original.SourceModule, original.SourceDocumentType, original.SourceDocumentId, date, original.CurrencyTc, [],
            original.SourceDocumentNumber, date, LocalizedText.Bilingual($"Reversal of {original.Number}: {reason.Trim()}", $"عكس القيد {original.Number}: {reason.Trim()}"), null, original.RateType, IsManual: original.IsManual);
        var entry = await WriteEntryAsync(request, company, period.Value, tc, company.FunctionalCurrency, company.ReportingCurrency, original.RateType, original.RateTcFc, original.RateFcRc, original.PostingProfileId, mirrored, isReversal: true, cancellationToken, isAutoReversal: automatic);
        if (entry.IsFailure)
        {
            return entry.Error!;
        }

        var now = clock.UtcNow;
        db.Set<EntryLink>().Add(new EntryLink { FromEntryId = entry.Value.Id, ToEntryId = original.Id, Relation = EntryRelations.Reverses, Reason = reason.Trim(), CreatedBy = principal.Principal?.UserId.Value, CreatedAt = now });
        if (automatic)
        {
            db.Set<EntryLink>().Add(new EntryLink { FromEntryId = entry.Value.Id, ToEntryId = original.Id, Relation = EntryRelations.AutoReversalOf, Reason = reason.Trim(), CreatedBy = principal.Principal?.UserId.Value, CreatedAt = now });
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("journal_entry", original.Id, original.Number, AuditActions.Reversed, After: new { reversalEntryId = entry.Value.Id, reversalNumber = entry.Value.Number, date, reason = reason.Trim() }), cancellationToken);
        await outbox.PublishAsync(new JournalEntryPosted(entry.Value.Id, company.Id.Value, entry.Value.Number, entry.Value.PostingDate, entry.Value.SourceModule, entry.Value.SourceDocumentType, entry.Value.SourceDocumentId, true, original.Id), cancellationToken);
        return await ToResultAsync(entry.Value, replayed: false, cancellationToken);
    }

    // ------------------------------------------------------------------ steps

    private async Task<Result<PeriodState>> PeriodForAsync(CompanyInfo company, DateOnly date, CancellationToken cancellationToken)
    {
        var resolved = await periods.ResolveAsync(company.Id, date, PostingModules.GeneralLedger, cancellationToken);
        if (resolved.IsFailure)
        {
            return resolved.Error!;
        }

        var state = resolved.Value;
        var mayPostInSoftClosed = principal.Principal?.Has(AccountingPermissions.PostInSoftClosed) ?? true;
        if (state.AllowsPosting(mayPostInSoftClosed))
        {
            return state;
        }

        var code = state.State == PeriodStates.SoftClosed ? "period.soft_closed" : "period.closed";
        return Error.Conflict(code, $"Period {state.Period.Number} of {state.Period.FiscalYearCode} is {state.State} for GL in company {company.Code}.")
            .WithWhy(("company", company.Code), ("date", date), ("periodId", state.Period.PeriodId), ("period", state.Period.Number), ("fiscalYear", state.Period.FiscalYearCode), ("state", state.State),
                ("requiredPermission", state.State == PeriodStates.SoftClosed ? AccountingPermissions.PostInSoftClosed : "accounting.period.reopen"));
    }

    private async Task<Result<decimal>> RateAsync(CompanyInfo company, Currency from, Currency to, DateOnly date, string rateType, decimal? overrideRate, CancellationToken cancellationToken)
    {
        if (from == to)
        {
            return 1m;
        }

        if (overrideRate is { } fixedRate)
        {
            return fixedRate;
        }

        var resolved = await rates.ResolveAsync(company.Id, from.Code, to.Code, date, rateType, cancellationToken);
        return resolved.IsFailure ? resolved.Error! : resolved.Value.Rate.Rate;
    }

    private async Task<Result<ResolvedLine>> ResolveLineAsync(PostingRequest request, PostingLine line, int lineNo, CompanyInfo company, Currency tc, Currency fc, Currency? rc, decimal rateTcFc, decimal? rateFcRc, RoundingPolicy policy, IReadOnlyList<RuleCandidate> rules, CancellationToken cancellationToken)
    {
        var keys = line.Keys ?? PostingKeys.None;
        var branchId = keys.BranchId ?? request.BranchId?.Value;
        keys = keys with { BranchId = branchId };

        Guid accountId;
        Guid? ruleId = null;
        if (line.AccountId is { } explicitAccount)
        {
            if (!request.IsManual)
            {
                return Error.Validation("posting.explicit_account_not_allowed", $"Line {lineNo}: documents post by role; only manual journals name an account.").WithWhy(("line", lineNo));
            }

            accountId = explicitAccount;
        }
        else
        {
            var rule = PostingRuleMatcher.Resolve(rules, line.AccountRole, keys);
            if (rule.IsFailure)
            {
                return rule.Error!.WithWhy(("line", lineNo));
            }

            accountId = rule.Value.AccountId;
            ruleId = rule.Value.RuleId;
        }

        var dimensions = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var (code, valueId) in line.Dimensions ?? new Dictionary<string, Guid>(StringComparer.Ordinal))
        {
            dimensions[code.Trim().ToUpperInvariant()] = valueId;
        }

        if (branchId is { } b && !dimensions.ContainsKey(BranchDimension))
        {
            var branch = await companies.FindBranchAsync(new BranchId(b), cancellationToken);
            if (branch is null || branch.CompanyId != company.Id)
            {
                return Error.Validation("posting.branch_mismatch", $"Line {lineNo}: branch {b} does not belong to company '{company.Code}'.").WithWhy(("line", lineNo), ("branchId", b));
            }

            dimensions[BranchDimension] = branch.DimensionValueId;
        }

        // A manual journal may touch a control account only when the line names the subledger item it adjusts (POSTING_RULES §8).
        var check = await chart.CheckLineAsync(accountId, company.Id, dimensions, tc.Code, request.IsManual && line.SubledgerRef is null, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error!.WithWhy(("line", lineNo));
        }

        var account = check.Value.Account;
        if (account.IsControl)
        {
            if (line.SubledgerRef is null)
            {
                return Error.Validation("posting.subledger_ref_required", $"Line {lineNo}: '{account.Code}' is a {account.SubledgerType} control account; the line must reference its subledger item.").WithWhy(("line", lineNo), ("account", account.Code), ("subledgerType", account.SubledgerType));
            }

            if (!string.Equals(line.SubledgerType, account.SubledgerType, StringComparison.Ordinal))
            {
                return Error.Validation("posting.subledger_type_mismatch", $"Line {lineNo}: '{account.Code}' reconciles to {account.SubledgerType}, the line says {line.SubledgerType ?? "nothing"}.").WithWhy(("line", lineNo), ("account", account.Code), ("expected", account.SubledgerType), ("actual", line.SubledgerType));
            }
        }
        else if (line.SubledgerRef is not null || line.SubledgerType is not null)
        {
            return Error.Validation("posting.subledger_unexpected", $"Line {lineNo}: '{account.Code}' is not a control account; it takes no subledger reference.").WithWhy(("line", lineNo), ("account", account.Code));
        }

        var fcAmount = policy.Round(line.Amount * rateTcFc, fc.MinorUnits);
        var rcAmount = rc is { } reporting
            ? reporting == tc ? line.Amount : reporting == fc ? fcAmount : policy.Round(fcAmount * rateFcRc!.Value, reporting.MinorUnits)
            : 0m;
        Guid? setId = null;
        if (check.Value.Dimensions.Count > 0)
        {
            var set = await dimensionSets.GetOrCreateAsync(check.Value.Dimensions, cancellationToken);
            if (set.IsFailure)
            {
                return set.Error!.WithWhy(("line", lineNo));
            }

            setId = set.Value;
        }

        return new ResolvedLine(line, account, ruleId, line.Amount, fcAmount, rcAmount, setId, branchId, IsRounding: false);
    }

    private async Task<Result<ResolvedLine>> RoundingLineAsync(CompanyInfo company, decimal fc, decimal rc, IReadOnlyList<RuleCandidate> rules, CancellationToken cancellationToken)
    {
        var rule = PostingRuleMatcher.Resolve(rules, AccountRoles.RoundingDifferences, PostingKeys.None);
        if (rule.IsFailure)
        {
            return rule.Error!.WithWhy(("reason", "rounding line"));
        }

        var check = await chart.CheckLineAsync(rule.Value.AccountId, company.Id, null, null, false, cancellationToken);
        if (check.IsFailure)
        {
            return check.Error!;
        }

        var line = new PostingLine(AccountRoles.RoundingDifferences, 0m, PostingKeys.None, Description: LocalizedText.Bilingual("Rounding difference", "فرق تقريب"));
        return new ResolvedLine(line, check.Value.Account, rule.Value.RuleId, 0m, fc, rc, null, null, IsRounding: true);
    }

    private async Task<Result<JournalEntry>> WriteEntryAsync(PostingRequest request, CompanyInfo company, PeriodState period, Currency tc, Currency fc, Currency? rc, string rateType, decimal rateTcFc, decimal? rateFcRc, Guid? profileId, List<ResolvedLine> lines, bool isReversal, CancellationToken cancellationToken, bool isAutoReversal = false)
    {
        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition("SELECT app.gl_ensure_partition(@day)", new { day = request.PostingDate }, uow.Transaction, cancellationToken: cancellationToken));

        var entryId = Guid.CreateVersion7();
        var ensured = await numbering.EnsureDefaultSeriesAsync(JournalDocumentType, company.Id, "JE-" + company.Code, "JE-{yyyy}-{seq:6}", "yearly", cancellationToken);
        if (ensured.IsFailure)
        {
            return ensured.Error!;
        }

        var number = await numbering.AllocateAsync(new NumberRequest(JournalDocumentType, company.Id, request.BranchId, request.PostingDate, entryId), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        var now = clock.UtcNow;
        var entry = new JournalEntry
        {
            Id = entryId,
            CompanyId = company.Id.Value,
            Number = number.Value.Text,
            PostingDate = request.PostingDate,
            DocumentDate = request.DocumentDate ?? request.PostingDate,
            FiscalYearId = period.Period.FiscalYearId,
            FiscalPeriodId = period.Period.PeriodId,
            SourceModule = request.SourceModule.Trim().ToLowerInvariant(),
            SourceDocumentType = request.SourceDocumentType.Trim().ToLowerInvariant(),
            SourceDocumentId = request.SourceDocumentId,
            SourceDocumentNumber = request.SourceDocumentNumber,
            Description = request.Description ?? new LocalizedText(),
            IsReversal = isReversal,
            IsAutoReversal = isAutoReversal,
            AutoReverseOn = request.AutoReverseOn,
            IsClosingEntry = request.IsClosingEntry,
            IsOpeningEntry = request.IsOpeningEntry,
            IsManual = request.IsManual,
            CurrencyTc = tc.Code,
            CurrencyFc = fc.Code,
            CurrencyRc = rc?.Code,
            RateType = rateType,
            RateTcFc = rateTcFc,
            RateFcRc = rateFcRc,
            RateOverrideReason = request.RateOverride is null ? null : request.RateOverrideReason?.Trim(),
            PostingProfileId = profileId,
            LineCount = lines.Count,
            PostedBy = principal.Principal?.UserId.Value,
            PostedAt = now,
            IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim(),
        };
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            entry.Lines.Add(new JournalLine
            {
                Id = Guid.CreateVersion7(),
                EntryId = entry.Id,
                CompanyId = entry.CompanyId,
                PostingDate = entry.PostingDate,
                LineNo = i + 1,
                AccountId = l.Account.Id,
                AccountRole = l.Source.AccountRole,
                PostingRuleId = l.RuleId,
                DebitTc = l.Tc > 0m ? l.Tc : 0m,
                CreditTc = l.Tc < 0m ? -l.Tc : 0m,
                CurrencyTc = tc.Code,
                RateTcFc = rateTcFc,
                DebitFc = l.Fc > 0m ? l.Fc : 0m,
                CreditFc = l.Fc < 0m ? -l.Fc : 0m,
                RateFcRc = rateFcRc,
                DebitRc = l.Rc > 0m ? l.Rc : 0m,
                CreditRc = l.Rc < 0m ? -l.Rc : 0m,
                RateType = rateType,
                RateDate = entry.PostingDate,
                DimensionSetId = l.DimensionSetId,
                BranchId = l.BranchId,
                PartnerId = l.Source.PartnerId,
                SubledgerType = l.Source.SubledgerType,
                SubledgerRef = l.Source.SubledgerRef,
                TaxCodeId = l.Source.TaxCodeId,
                TaxBaseTc = l.Source.TaxBase,
                Description = l.Source.Description ?? new LocalizedText(),
                DueDate = l.Source.DueDate,
                IsRounding = l.IsRounding,
            });
        }

        db.Set<JournalEntry>().Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        await ApplyBalancesAsync(entry, uow, cancellationToken);
        await audit.RecordAsync(new AuditEntry("journal_entry", entry.Id, entry.Number, AuditActions.Posted, After: new
        {
            entry.CompanyId,
            entry.PostingDate,
            entry.SourceModule,
            entry.SourceDocumentType,
            entry.SourceDocumentId,
            entry.CurrencyTc,
            entry.RateTcFc,
            entry.IsReversal,
            lines = entry.LineCount,
            debitTc = entry.Lines.Sum(static l => l.DebitTc),
            debitFc = entry.Lines.Sum(static l => l.DebitFc),
        }), cancellationToken);
        return entry;
    }

    /// <summary>Increments the derived balances under the row lock of each balance row, in the posting transaction (ADR-0007).</summary>
    private static async Task ApplyBalancesAsync(JournalEntry entry, IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var groups = entry.Lines.GroupBy(static l => (l.AccountId, l.CurrencyTc, DimensionSetId: l.DimensionSetId ?? Guid.Empty)).Select(g => new
        {
            tenant = uow.Context.TenantId.Value,
            company = entry.CompanyId,
            account = g.Key.AccountId,
            period = entry.FiscalPeriodId,
            currency = g.Key.CurrencyTc,
            dimset = g.Key.DimensionSetId,
            dtc = g.Sum(static l => l.DebitTc),
            ctc = g.Sum(static l => l.CreditTc),
            dfc = g.Sum(static l => l.DebitFc),
            cfc = g.Sum(static l => l.CreditFc),
            drc = g.Sum(static l => l.DebitRc),
            crc = g.Sum(static l => l.CreditRc),
        });
        foreach (var group in groups)
        {
            await uow.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO app.gl_balances (tenant_id, company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id, debit_tc, credit_tc, debit_fc, credit_fc, debit_rc, credit_rc)
                VALUES (@tenant, @company, @account, @period, @currency, @dimset, @dtc, @ctc, @dfc, @cfc, @drc, @crc)
                ON CONFLICT (tenant_id, company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id) DO UPDATE SET
                  debit_tc = gl_balances.debit_tc + EXCLUDED.debit_tc, credit_tc = gl_balances.credit_tc + EXCLUDED.credit_tc,
                  debit_fc = gl_balances.debit_fc + EXCLUDED.debit_fc, credit_fc = gl_balances.credit_fc + EXCLUDED.credit_fc,
                  debit_rc = gl_balances.debit_rc + EXCLUDED.debit_rc, credit_rc = gl_balances.credit_rc + EXCLUDED.credit_rc,
                  updated_at = now()
                """, group, uow.Transaction, cancellationToken: cancellationToken));
        }
    }

    private async Task<PostingResult> ToResultAsync(JournalEntry entry, bool replayed, CancellationToken cancellationToken)
    {
        var ids = entry.Lines.Select(static l => l.AccountId).Distinct().ToList();
        var codes = await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, static a => a.Code, cancellationToken);
        var lines = entry.Lines.OrderBy(static l => l.LineNo).Select(l => new PostedLine(l.Id, l.LineNo, l.AccountId, codes.GetValueOrDefault(l.AccountId, string.Empty), l.AccountRole, l.PostingRuleId,
            l.DebitTc, l.CreditTc, l.DebitFc, l.CreditFc, l.DebitRc, l.CreditRc, l.DimensionSetId, l.SubledgerType, l.SubledgerRef, l.IsRounding)).ToList();
        return new PostingResult(entry.Id, entry.Number, entry.CompanyId, entry.PostingDate, entry.FiscalYearId, entry.FiscalPeriodId, entry.CurrencyTc, entry.CurrencyFc, entry.CurrencyRc, entry.RateTcFc, entry.RateFcRc, lines, entry.IsReversal, replayed);
    }
}
