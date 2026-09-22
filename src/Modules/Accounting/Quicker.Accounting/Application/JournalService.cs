using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Web;

namespace Quicker.Accounting.Application;

/// <summary>Reading journal entries and balances; rebuilding and verifying the derived balances (ADR-0007).</summary>
public sealed class JournalService(AccountingDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, IAuditSink audit)
{
    private static readonly Guid NoDimensions = Guid.Empty;

    public async Task<JournalEntrySummary?> GetEntryAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = await db.Set<JournalEntry>().Include(static e => e.Lines).SingleOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return null;
        }

        var links = await db.Set<EntryLink>().Where(l => l.FromEntryId == entryId || l.ToEntryId == entryId).ToListAsync(cancellationToken);
        var ids = entry.Lines.Select(static l => l.AccountId).Distinct().ToList();
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        return Map(entry, links, accounts);
    }

    public async Task<Result<Page<JournalEntrySummary>>> ListEntriesAsync(Guid companyId, DateOnly? from, DateOnly? to, string? sourceDocumentType, PageRequest page, CancellationToken cancellationToken, JournalBrowserFilter? filter = null)
    {
        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var query = db.Set<JournalEntry>().Include(static e => e.Lines).Where(e => e.CompanyId == companyId);
        if (filter is not null)
        {
            if (!string.IsNullOrWhiteSpace(filter.Number))
            {
                var pattern = filter.Number.Trim() + "%";
                query = query.Where(e => EF.Functions.ILike(e.Number, pattern) || (e.SourceDocumentNumber != null && EF.Functions.ILike(e.SourceDocumentNumber, pattern)));
            }

            if (filter.AccountId is { } account)
            {
                query = query.Where(e => e.Lines.Any(l => l.AccountId == account));
            }

            if (filter.IsManual is { } manual)
            {
                query = query.Where(e => e.IsManual == manual);
            }

            if (filter.MinAmount is { } min)
            {
                query = query.Where(e => e.Lines.Sum(l => l.DebitTc) >= min);
            }

            if (!string.IsNullOrWhiteSpace(filter.Text))
            {
                // The description is bilingual jsonb: the matching ids come from SQL, then the query narrows to them (capped; the reporting schema of M7 indexes text).
                var uow = unitOfWork.Current;
                var like = "%" + filter.Text.Trim() + "%";
                var ids = (await uow.Connection.QueryAsync<Guid>(new CommandDefinition("""
                    SELECT id FROM app.gl_journal_entries
                    WHERE company_id = @company AND (description_i18n->>'en' ILIKE @like OR description_i18n->>'ar' ILIKE @like OR number ILIKE @like OR source_document_number ILIKE @like)
                    ORDER BY id DESC LIMIT 10000
                    """, new { company = companyId, like }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
                query = query.Where(e => ids.Contains(e.Id));
            }
        }

        if (from is { } f)
        {
            query = query.Where(e => e.PostingDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(e => e.PostingDate <= t);
        }

        if (!string.IsNullOrWhiteSpace(sourceDocumentType))
        {
            var type = sourceDocumentType.Trim().ToLowerInvariant();
            query = query.Where(e => e.SourceDocumentType == type);
        }

        var result = await KeysetPaging.ByIdDescendingAsync(query, static e => e.Id, page, cancellationToken);
        if (result.IsFailure)
        {
            return result.Error!;
        }

        var entryIds = result.Value.Items.Select(static e => e.Id).ToList();
        var links = await db.Set<EntryLink>().Where(l => entryIds.Contains(l.FromEntryId) || entryIds.Contains(l.ToEntryId)).ToListAsync(cancellationToken);
        return result.Value.Map(e => Map(e, links.Where(l => l.FromEntryId == e.Id || l.ToEntryId == e.Id).ToList(), null));
    }

    public async Task<Result<IReadOnlyList<BalanceRow>>> BalancesAsync(Guid companyId, Guid? periodId, Guid? accountId, CancellationToken cancellationToken)
    {
        if (await companies.FindAsync(new CompanyId(companyId), cancellationToken) is null)
        {
            return Error.NotFound("company", companyId);
        }

        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<BalanceRow>(new CommandDefinition("""
            SELECT b.account_id, a.code AS account_code, b.fiscal_period_id, b.currency_tc, b.dimension_set_id,
                   b.debit_tc, b.credit_tc, b.debit_fc, b.credit_fc, b.debit_rc, b.credit_rc
            FROM app.gl_balances b
            JOIN app.gl_accounts a ON a.tenant_id = b.tenant_id AND a.id = b.account_id
            WHERE b.company_id = @company AND (@period IS NULL OR b.fiscal_period_id = @period) AND (@account IS NULL OR b.account_id = @account)
            ORDER BY a.code, b.fiscal_period_id, b.currency_tc, b.dimension_set_id
            """, new { company = companyId, period = periodId, account = accountId }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    /// <summary>Compares the stored balances with what the lines say, without changing anything (the nightly check of ADR-0007).</summary>
    public async Task<Result<BalanceVerification>> VerifyBalancesAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is { } id && await companies.FindAsync(new CompanyId(id), cancellationToken) is null)
        {
            return Error.NotFound("company", id);
        }

        var uow = unitOfWork.Current;
        var stored = (await uow.Connection.QueryAsync<StoredBalance>(new CommandDefinition("""
            SELECT company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id, debit_tc, credit_tc, debit_fc, credit_fc, debit_rc, credit_rc
            FROM app.gl_balances WHERE (@company IS NULL OR company_id = @company)
            """, new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken))).ToDictionary(static b => b.Key);
        var rebuilt = (await uow.Connection.QueryAsync<StoredBalance>(new CommandDefinition(RebuildSelect, new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken))).ToDictionary(static b => b.Key);

        var differences = new List<BalanceDifference>();
        foreach (var key in stored.Keys.Union(rebuilt.Keys))
        {
            var s = stored.GetValueOrDefault(key) ?? StoredBalance.Empty(key);
            var r = rebuilt.GetValueOrDefault(key) ?? StoredBalance.Empty(key);
            foreach (var (column, storedValue, rebuiltValue) in new[]
            {
                ("debit_tc", s.DebitTc, r.DebitTc), ("credit_tc", s.CreditTc, r.CreditTc), ("debit_fc", s.DebitFc, r.DebitFc),
                ("credit_fc", s.CreditFc, r.CreditFc), ("debit_rc", s.DebitRc, r.DebitRc), ("credit_rc", s.CreditRc, r.CreditRc),
            })
            {
                if (storedValue != rebuiltValue)
                {
                    differences.Add(new BalanceDifference(s.AccountId, s.FiscalPeriodId, s.CurrencyTc, s.DimensionSetId, column, storedValue, rebuiltValue));
                }
            }
        }

        return new BalanceVerification(companyId, stored.Count, rebuilt.Count, differences);
    }

    /// <summary>Truncates the derived balances (tenant- and optionally company-scoped) and recomputes them from the lines, in one transaction.</summary>
    public async Task<Result<RebuildResult>> RebuildBalancesAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        if (companyId is { } id && await companies.FindAsync(new CompanyId(id), cancellationToken) is null)
        {
            return Error.NotFound("company", id);
        }

        var uow = unitOfWork.Current;
        var before = await uow.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM app.gl_balances WHERE (@company IS NULL OR company_id = @company)", new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        var after = await uow.Connection.ExecuteAsync(new CommandDefinition($"""
            INSERT INTO app.gl_balances (tenant_id, company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id, debit_tc, credit_tc, debit_fc, credit_fc, debit_rc, credit_rc)
            SELECT @tenant, company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id, debit_tc, credit_tc, debit_fc, credit_fc, debit_rc, credit_rc
            FROM ({RebuildSelect}) rebuilt
            """, new { company = companyId, tenant = uow.Context.TenantId.Value }, uow.Transaction, cancellationToken: cancellationToken));
        await audit.RecordAsync(new AuditEntry("gl_balances", companyId ?? uow.Context.TenantId.Value, companyId is null ? "tenant" : "company", "rebuilt", After: new { rowsBefore = before, rowsAfter = after }), cancellationToken);
        return new RebuildResult(companyId, before, after);
    }

    private const string RebuildSelect = """
        SELECT l.company_id, l.account_id, e.fiscal_period_id, l.currency_tc,
               coalesce(l.dimension_set_id, '00000000-0000-0000-0000-000000000000'::uuid) AS dimension_set_id,
               sum(l.debit_tc) AS debit_tc, sum(l.credit_tc) AS credit_tc, sum(l.debit_fc) AS debit_fc, sum(l.credit_fc) AS credit_fc, sum(l.debit_rc) AS debit_rc, sum(l.credit_rc) AS credit_rc
        FROM app.gl_journal_lines l
        JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
        WHERE (@company IS NULL OR l.company_id = @company)
        GROUP BY l.company_id, l.account_id, e.fiscal_period_id, l.currency_tc, coalesce(l.dimension_set_id, '00000000-0000-0000-0000-000000000000'::uuid)
        """;

    private sealed record StoredBalance(Guid CompanyId, Guid AccountId, Guid FiscalPeriodId, string CurrencyTc, Guid DimensionSetId, decimal DebitTc, decimal CreditTc, decimal DebitFc, decimal CreditFc, decimal DebitRc, decimal CreditRc)
    {
        public (Guid, Guid, Guid, string, Guid) Key => (CompanyId, AccountId, FiscalPeriodId, CurrencyTc, DimensionSetId);

        public static StoredBalance Empty((Guid Company, Guid Account, Guid Period, string Currency, Guid DimensionSet) key) =>
            new(key.Company, key.Account, key.Period, key.Currency, key.DimensionSet, 0m, 0m, 0m, 0m, 0m, 0m);
    }

    private static JournalEntrySummary Map(JournalEntry e, List<EntryLink> links, Dictionary<Guid, Account>? accounts)
    {
        var reversedBy = links.FirstOrDefault(l => l.ToEntryId == e.Id && l.Relation == EntryRelations.Reverses)?.FromEntryId;
        var reverses = links.FirstOrDefault(l => l.FromEntryId == e.Id && l.Relation == EntryRelations.Reverses)?.ToEntryId;
        var lines = accounts is null ? null : e.Lines.OrderBy(static l => l.LineNo).Select(l =>
        {
            var account = accounts.GetValueOrDefault(l.AccountId);
            return new JournalLineSummary(l.Id, l.LineNo, l.AccountId, account?.Code ?? string.Empty, account?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.AccountRole, l.PostingRuleId,
                l.DebitTc, l.CreditTc, l.DebitFc, l.CreditFc, l.DebitRc, l.CreditRc, l.DimensionSetId == NoDimensions ? null : l.DimensionSetId, l.BranchId, l.PartnerId, l.SubledgerType, l.SubledgerRef,
                l.Description.Values, l.DueDate, l.IsRounding);
        }).ToList();
        return new JournalEntrySummary(e.Id, e.CompanyId, e.Number, e.PostingDate, e.DocumentDate, e.FiscalYearId, e.FiscalPeriodId, e.SourceModule, e.SourceDocumentType, e.SourceDocumentId, e.SourceDocumentNumber,
            e.Description.Values, e.IsReversal, e.IsAutoReversal, e.AutoReverseOn, e.IsClosingEntry, e.IsOpeningEntry, e.IsManual, e.CurrencyTc, e.CurrencyFc, e.CurrencyRc, e.RateType, e.RateTcFc, e.RateFcRc,
            e.PostingProfileId, e.LineCount, e.PostedBy, e.PostedAt, reversedBy, reverses, e.Lines.Sum(static l => l.DebitTc), e.Lines.Sum(static l => l.DebitFc), lines,
            links.Select(static l => new EntryLinkSummary(l.FromEntryId, l.ToEntryId, l.Relation, l.Reason)).ToList());
    }
}
