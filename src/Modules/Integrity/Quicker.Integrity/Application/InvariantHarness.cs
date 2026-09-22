using System.Globalization;
using Dapper;
using Quicker.Audit.Contracts;
using Quicker.Integrity.Contracts;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Integrity.Application;

/// <summary>
/// The invariant harness v1 (roadmap 2.6, ADR-0029): six read-only checks over the current tenant, each answering
/// with what it examined and the first problems it found. It runs after every scenario test, on demand through the
/// API, and daily as the job <c>integrity.check_all</c> over every tenant. Row-level security scopes every query to
/// the tenant of the unit of work; the isolation check reads the catalogue view the schema suite reads.
/// </summary>
public sealed class InvariantHarness(IUnitOfWorkAccessor unitOfWork, IAuditChainVerifier auditChain, IClock clock) : IInvariantHarness
{
    private const int ProblemLimit = 20;

    private sealed record UnbalancedEntry(Guid Id, string Number, Guid CompanyId, int LineCount, long Lines, decimal OffTc, decimal OffFc, decimal OffRc);

    private sealed record CompanyOff(Guid CompanyId, decimal OffFc, decimal OffRc);

    private sealed record BalanceMismatch(Guid CompanyId, string AccountCode, Guid FiscalPeriodId, string CurrencyTc, Guid DimensionSetId, string Column, decimal? Stored, decimal? Rebuilt);

    private sealed record TableRow(string SchemaName, string TableName, bool HasTenantId, bool RlsEnabled, bool RlsForced, bool HasPolicy);

    private sealed record NumberingGap(string Code, string PeriodKey, long Issued, long First, long Last, long? NextNumber);

    public async Task<InvariantReport> RunAsync(Guid? companyId = null, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        var checks = new List<InvariantResult>
        {
            await EntriesBalancedAsync(uow, companyId, cancellationToken),
            await TrialBalanceZeroAsync(uow, companyId, cancellationToken),
            await BalancesMatchLinesAsync(uow, companyId, cancellationToken),
            await AuditChainIntactAsync(cancellationToken),
            await TenantIsolationAsync(uow, cancellationToken),
            await GaplessNumberingAsync(uow, companyId, cancellationToken),
        };
        return new InvariantReport(uow.Context.TenantId.Value, companyId, clock.UtcNow, checks, checks.TrueForAll(static c => c.Passed));
    }

    private static async Task<InvariantResult> EntriesBalancedAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var checkedCount = await uow.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app.gl_journal_entries e WHERE (@company::uuid IS NULL OR e.company_id = @company)", new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        var offenders = (await uow.Connection.QueryAsync<UnbalancedEntry>(new CommandDefinition("""
            SELECT e.id, e.number, e.company_id, e.line_count, count(l.id) AS lines,
                   coalesce(sum(l.debit_tc), 0) - coalesce(sum(l.credit_tc), 0) AS off_tc,
                   coalesce(sum(l.debit_fc), 0) - coalesce(sum(l.credit_fc), 0) AS off_fc,
                   coalesce(sum(l.debit_rc), 0) - coalesce(sum(l.credit_rc), 0) AS off_rc
            FROM app.gl_journal_entries e
            LEFT JOIN app.gl_journal_lines l ON l.tenant_id = e.tenant_id AND l.entry_id = e.id
            WHERE (@company::uuid IS NULL OR e.company_id = @company)
            GROUP BY e.id, e.number, e.company_id, e.line_count
            HAVING count(l.id) <> e.line_count
                OR coalesce(sum(l.debit_tc), 0) <> coalesce(sum(l.credit_tc), 0)
                OR coalesce(sum(l.debit_fc), 0) <> coalesce(sum(l.credit_fc), 0)
                OR coalesce(sum(l.debit_rc), 0) <> coalesce(sum(l.credit_rc), 0)
            ORDER BY e.number
            LIMIT @limit
            """, new { company = companyId, limit = ProblemLimit }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = offenders.Select(o => o.Lines != o.LineCount
                ? $"{o.Number}: declares {o.LineCount} lines, has {o.Lines}"
                : $"{o.Number}: off by {N(o.OffTc)} tc, {N(o.OffFc)} fc, {N(o.OffRc)} rc")
            .ToList();
        return Result(InvariantCodes.EntriesBalanced, checkedCount, problems, $"{checkedCount} entries, every one balanced in tc, fc and rc with its declared lines");
    }

    private static async Task<InvariantResult> TrialBalanceZeroAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var companies = (await uow.Connection.QueryAsync<CompanyOff>(new CommandDefinition("""
            SELECT l.company_id, sum(l.debit_fc - l.credit_fc) AS off_fc, sum(l.debit_rc - l.credit_rc) AS off_rc
            FROM app.gl_journal_lines l
            WHERE (@company::uuid IS NULL OR l.company_id = @company)
            GROUP BY l.company_id
            """, new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = companies.Where(static c => c.OffFc != 0m || c.OffRc != 0m).Take(ProblemLimit).Select(c => $"company {c.CompanyId}: trial balance off by {N(c.OffFc)} fc, {N(c.OffRc)} rc").ToList();
        return Result(InvariantCodes.TrialBalanceZero, companies.Count, problems, $"{companies.Count} companies with lines, every trial balance sums to zero in fc and rc");
    }

    private static async Task<InvariantResult> BalancesMatchLinesAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var stored = await uow.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app.gl_balances b WHERE (@company::uuid IS NULL OR b.company_id = @company)", new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        var mismatches = (await uow.Connection.QueryAsync<BalanceMismatch>(new CommandDefinition("""
            WITH rebuilt AS (
              SELECT l.company_id, l.account_id, e.fiscal_period_id, l.currency_tc,
                     coalesce(l.dimension_set_id, '00000000-0000-0000-0000-000000000000'::uuid) AS dimension_set_id,
                     sum(l.debit_tc) AS debit_tc, sum(l.credit_tc) AS credit_tc, sum(l.debit_fc) AS debit_fc, sum(l.credit_fc) AS credit_fc, sum(l.debit_rc) AS debit_rc, sum(l.credit_rc) AS credit_rc
              FROM app.gl_journal_lines l
              JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
              WHERE (@company::uuid IS NULL OR l.company_id = @company)
              GROUP BY l.company_id, l.account_id, e.fiscal_period_id, l.currency_tc, coalesce(l.dimension_set_id, '00000000-0000-0000-0000-000000000000'::uuid)
            ),
            stored AS (
              SELECT company_id, account_id, fiscal_period_id, currency_tc, dimension_set_id, debit_tc, credit_tc, debit_fc, credit_fc, debit_rc, credit_rc
              FROM app.gl_balances
              WHERE (@company::uuid IS NULL OR company_id = @company)
            ),
            joined AS (
              SELECT coalesce(s.company_id, r.company_id) AS company_id, coalesce(s.account_id, r.account_id) AS account_id,
                     coalesce(s.fiscal_period_id, r.fiscal_period_id) AS fiscal_period_id, coalesce(s.currency_tc, r.currency_tc) AS currency_tc,
                     coalesce(s.dimension_set_id, r.dimension_set_id) AS dimension_set_id,
                     s.debit_tc AS s_debit_tc, r.debit_tc AS r_debit_tc, s.credit_tc AS s_credit_tc, r.credit_tc AS r_credit_tc,
                     s.debit_fc AS s_debit_fc, r.debit_fc AS r_debit_fc, s.credit_fc AS s_credit_fc, r.credit_fc AS r_credit_fc,
                     s.debit_rc AS s_debit_rc, r.debit_rc AS r_debit_rc, s.credit_rc AS s_credit_rc, r.credit_rc AS r_credit_rc
              FROM stored s
              FULL OUTER JOIN rebuilt r ON r.company_id = s.company_id AND r.account_id = s.account_id AND r.fiscal_period_id = s.fiscal_period_id AND r.currency_tc = s.currency_tc AND r.dimension_set_id = s.dimension_set_id
            )
            SELECT j.company_id, a.code AS account_code, j.fiscal_period_id, j.currency_tc, j.dimension_set_id, c.column_name AS "column", c.stored, c.rebuilt
            FROM joined j
            JOIN app.gl_accounts a ON a.id = j.account_id
            CROSS JOIN LATERAL (VALUES
              ('debit_tc', j.s_debit_tc, j.r_debit_tc), ('credit_tc', j.s_credit_tc, j.r_credit_tc),
              ('debit_fc', j.s_debit_fc, j.r_debit_fc), ('credit_fc', j.s_credit_fc, j.r_credit_fc),
              ('debit_rc', j.s_debit_rc, j.r_debit_rc), ('credit_rc', j.s_credit_rc, j.r_credit_rc)) AS c(column_name, stored, rebuilt)
            WHERE c.stored IS DISTINCT FROM c.rebuilt
            ORDER BY a.code, j.fiscal_period_id, c.column_name
            LIMIT @limit
            """, new { company = companyId, limit = ProblemLimit }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = mismatches.Select(m => $"account {m.AccountCode} period {m.FiscalPeriodId} {m.CurrencyTc} {m.Column}: stored {N(m.Stored)}, lines say {N(m.Rebuilt)}").ToList();
        return Result(InvariantCodes.BalancesMatchLines, stored, problems, $"{stored} balance rows equal the journal lines in every column");
    }

    private async Task<InvariantResult> AuditChainIntactAsync(CancellationToken cancellationToken)
    {
        var chain = await auditChain.VerifyTenantChainAsync(cancellationToken);
        var problems = chain.Intact ? [] : new List<string> { $"chain {chain.Status} at sequence {chain.FirstBrokenSeq?.ToString(CultureInfo.InvariantCulture) ?? "?"}: {chain.Message}" };
        return Result(InvariantCodes.AuditChainIntact, chain.ToSeq, problems, $"{chain.ToSeq} audit events, chain {chain.Status}");
    }

    private static async Task<InvariantResult> TenantIsolationAsync(IUnitOfWork uow, CancellationToken cancellationToken)
    {
        var tables = (await uow.Connection.QueryAsync<TableRow>(new CommandDefinition(
            "SELECT schema_name, table_name, has_tenant_id, rls_enabled, rls_forced, has_policy FROM ops.tenant_tables ORDER BY schema_name, table_name", transaction: uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = tables.Where(static t => !t.HasTenantId || !t.RlsEnabled || !t.RlsForced || !t.HasPolicy).Take(ProblemLimit)
            .Select(static t => $"{t.SchemaName}.{t.TableName}: tenant_id={t.HasTenantId} rls={t.RlsEnabled} forced={t.RlsForced} policy={t.HasPolicy}").ToList();
        return Result(InvariantCodes.TenantIsolation, tables.Count, problems, $"{tables.Count} tenant tables with tenant_id, row security enabled, forced and policed");
    }

    private static async Task<InvariantResult> GaplessNumberingAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var series = (await uow.Connection.QueryAsync<NumberingGap>(new CommandDefinition("""
            SELECT s.code, a.period_key, count(*) AS issued, min(a.number) AS first, max(a.number) AS last, c.next_number
            FROM app.num_allocations a
            JOIN app.num_series s ON s.tenant_id = a.tenant_id AND s.id = a.series_id
            LEFT JOIN app.num_series_counters c ON c.tenant_id = a.tenant_id AND c.series_id = a.series_id AND c.period_key = a.period_key
            WHERE s.gapless AND (@company::uuid IS NULL OR s.company_id = @company)
            GROUP BY s.code, a.period_key, c.next_number
            ORDER BY s.code, a.period_key
            """, new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = series.Where(static g => g.Issued != g.Last - g.First + 1 || (g.NextNumber is { } next && next != g.Last + 1)).Take(ProblemLimit)
            .Select(static g => g.Issued != g.Last - g.First + 1
                ? $"series {g.Code} {g.PeriodKey}: {g.Issued} numbers issued between {g.First} and {g.Last}, {g.Last - g.First + 1 - g.Issued} missing"
                : $"series {g.Code} {g.PeriodKey}: counter at {g.NextNumber} after last number {g.Last}")
            .ToList();
        return Result(InvariantCodes.GaplessNumbering, series.Count, problems, $"{series.Count} gapless series periods without a missing number");
    }

    private static InvariantResult Result(string code, long checkedCount, IReadOnlyList<string> problems, string summary) =>
        new(code, problems.Count == 0, checkedCount, problems, problems.Count == 0 ? summary : $"{problems.Count} problem(s) found (first {Math.Min(problems.Count, ProblemLimit)} listed)");

    private static string N(decimal? value) => value is { } v ? (v / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : "none";
}
