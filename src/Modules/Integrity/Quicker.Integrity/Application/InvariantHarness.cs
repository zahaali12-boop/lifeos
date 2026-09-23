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
            await StockBalancesMatchLedgerAsync(uow, companyId, cancellationToken),
            await InventoryMatchesGlAsync(uow, companyId, cancellationToken),
            await GrniMatchesReceiptsAsync(uow, companyId, cancellationToken),
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

    private sealed record StockMismatch(Guid ItemId, Guid WarehouseId, Guid BinId, Guid LotId, Guid SerialId, string Column, decimal? Stored, decimal? Rebuilt);

    /// <summary>
    /// The stock balances against the ledger: on hand must equal the sum of the entries of the key and reserved the
    /// remaining quantity of its active reservations; a balance row without entries, or entries without a row, are
    /// problems too (full outer join on the key).
    /// </summary>
    private static async Task<InvariantResult> StockBalancesMatchLedgerAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var checkedCount = await uow.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app.inv_stock_balances b WHERE (@company::uuid IS NULL OR b.company_id = @company)", new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        var mismatches = (await uow.Connection.QueryAsync<StockMismatch>(new CommandDefinition("""
            WITH ledger AS (
              SELECT company_id, item_id, coalesce(variant_id, '00000000-0000-0000-0000-000000000000'::uuid) AS variant_id, warehouse_id,
                     coalesce(bin_id, '00000000-0000-0000-0000-000000000000'::uuid) AS bin_id, coalesce(lot_id, '00000000-0000-0000-0000-000000000000'::uuid) AS lot_id,
                     coalesce(serial_id, '00000000-0000-0000-0000-000000000000'::uuid) AS serial_id, sum(quantity) AS on_hand
              FROM app.inv_stock_ledger_entries
              WHERE (@company::uuid IS NULL OR company_id = @company)
              GROUP BY 1, 2, 3, 4, 5, 6, 7
            ), held AS (
              SELECT company_id, item_id, coalesce(variant_id, '00000000-0000-0000-0000-000000000000'::uuid) AS variant_id, warehouse_id,
                     coalesce(bin_id, '00000000-0000-0000-0000-000000000000'::uuid) AS bin_id, coalesce(lot_id, '00000000-0000-0000-0000-000000000000'::uuid) AS lot_id,
                     coalesce(serial_id, '00000000-0000-0000-0000-000000000000'::uuid) AS serial_id, sum(quantity - consumed_quantity) AS reserved
              FROM app.inv_reservations
              WHERE status = 'active' AND (@company::uuid IS NULL OR company_id = @company)
              GROUP BY 1, 2, 3, 4, 5, 6, 7
            ), keys AS (
              SELECT company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id FROM app.inv_stock_balances WHERE (@company::uuid IS NULL OR company_id = @company)
              UNION SELECT company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id FROM ledger
              UNION SELECT company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id FROM held
            )
            SELECT k.item_id, k.warehouse_id, k.bin_id, k.lot_id, k.serial_id, c.column_name AS "Column", c.stored, c.rebuilt
            FROM keys k
            LEFT JOIN app.inv_stock_balances b ON b.company_id = k.company_id AND b.item_id = k.item_id AND b.variant_id = k.variant_id AND b.warehouse_id = k.warehouse_id AND b.bin_id = k.bin_id AND b.lot_id = k.lot_id AND b.serial_id = k.serial_id
            LEFT JOIN ledger l ON l.company_id = k.company_id AND l.item_id = k.item_id AND l.variant_id = k.variant_id AND l.warehouse_id = k.warehouse_id AND l.bin_id = k.bin_id AND l.lot_id = k.lot_id AND l.serial_id = k.serial_id
            LEFT JOIN held h ON h.company_id = k.company_id AND h.item_id = k.item_id AND h.variant_id = k.variant_id AND h.warehouse_id = k.warehouse_id AND h.bin_id = k.bin_id AND h.lot_id = k.lot_id AND h.serial_id = k.serial_id
            CROSS JOIN LATERAL (VALUES ('on_hand', b.on_hand, coalesce(l.on_hand, 0)), ('reserved', b.reserved, coalesce(h.reserved, 0))) AS c (column_name, stored, rebuilt)
            WHERE c.stored IS DISTINCT FROM c.rebuilt
            ORDER BY k.item_id, k.warehouse_id, c.column_name
            LIMIT @limit
            """, new { company = companyId, limit = ProblemLimit }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = mismatches.Select(m => $"item {m.ItemId} in warehouse {m.WarehouseId}{(m.BinId == Guid.Empty ? string.Empty : " bin " + m.BinId)}: {m.Column} stored {N(m.Stored)}, ledger says {N(m.Rebuilt)}").ToList();
        return Result(InvariantCodes.StockBalancesMatchLedger, checkedCount, problems, $"{checkedCount} stock balance rows, every one equal to its ledger entries and active reservations");
    }

    private sealed record InventoryMismatch(Guid CompanyId, Guid ItemId, decimal Valued, decimal Booked);

    /// <summary>
    /// Σ value entries on the inventory accounts (actual + expected) per company and item equals Σ (debit − credit) in the
    /// functional currency of the journal lines that reference the item on an INV subledger account, in both directions.
    /// Value entries and journal lines share the GL date, so the equality holds at every date.
    /// </summary>
    private static async Task<InvariantResult> InventoryMatchesGlAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var checkedCount = await uow.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(DISTINCT (company_id, item_id)) FROM app.inv_stock_value_entries WHERE (@company::uuid IS NULL OR company_id = @company)", new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        var mismatches = (await uow.Connection.QueryAsync<InventoryMismatch>(new CommandDefinition("""
            WITH valued AS (
              SELECT company_id, item_id, sum(cost_amount_actual + cost_amount_expected) AS valued
              FROM app.inv_stock_value_entries
              WHERE account_role IN ('Inventory', 'InventoryInTransit') AND (@company::uuid IS NULL OR company_id = @company)
              GROUP BY 1, 2
            ), booked AS (
              SELECT l.company_id, l.subledger_ref AS item_id, sum(l.debit_fc - l.credit_fc) AS booked
              FROM app.gl_journal_lines l
              JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
              WHERE l.subledger_type = 'INV' AND e.source_module = 'inventory' AND (@company::uuid IS NULL OR l.company_id = @company)
              GROUP BY 1, 2
            ), keys AS (
              SELECT company_id, item_id FROM valued UNION SELECT company_id, item_id FROM booked
            )
            SELECT k.company_id, k.item_id, coalesce(v.valued, 0) AS valued, coalesce(b.booked, 0) AS booked
            FROM keys k
            LEFT JOIN valued v ON v.company_id = k.company_id AND v.item_id = k.item_id
            LEFT JOIN booked b ON b.company_id = k.company_id AND b.item_id = k.item_id
            WHERE coalesce(v.valued, 0) <> coalesce(b.booked, 0)
            ORDER BY k.company_id, k.item_id
            LIMIT @limit
            """, new { company = companyId, limit = ProblemLimit }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = mismatches.Select(m => $"company {m.CompanyId} item {m.ItemId}: value entries {N(m.Valued)}, inventory accounts {N(m.Booked)}").ToList();
        return Result(InvariantCodes.InventoryMatchesGl, checkedCount, problems, $"{checkedCount} item(s) valued, every one equal to its inventory account lines");
    }

    private sealed record GrniMismatch(Guid CompanyId, Guid ReceiptId, string? Number, decimal Booked, decimal Uninvoiced);

    /// <summary>
    /// Per company and goods receipt, Σ (credit − debit) in functional currency of the journal lines on the GRNI subledger
    /// that reference the receipt equals Σ over its lines of the expected cost booked less what invoices and returns have
    /// settled; a reversed receipt nets to zero on both sides. Only GRNI lines whose subledger item is a purchasing receipt
    /// are checked (the stock engine also takes direct purchase receipts from other callers); the check runs in both directions.
    /// </summary>
    private static async Task<InvariantResult> GrniMatchesReceiptsAsync(IUnitOfWork uow, Guid? companyId, CancellationToken cancellationToken)
    {
        var checkedCount = await uow.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM app.pur_receipts WHERE status IN ('posted', 'reversed') AND (@company::uuid IS NULL OR company_id = @company)", new { company = companyId }, uow.Transaction, cancellationToken: cancellationToken));
        var mismatches = (await uow.Connection.QueryAsync<GrniMismatch>(new CommandDefinition("""
            WITH refs AS (
              -- GRNI is referenced by the receipt (receipt and invoice lines) or by a return of it (return and debit-note lines).
              SELECT tenant_id, id AS ref, id AS receipt_id FROM app.pur_receipts
              UNION ALL
              SELECT tenant_id, id AS ref, receipt_id FROM app.pur_returns
            ), booked AS (
              SELECT l.company_id, x.receipt_id, sum(l.credit_fc - l.debit_fc) AS booked
              FROM app.gl_journal_lines l
              JOIN refs x ON x.tenant_id = l.tenant_id AND x.ref = l.subledger_ref
              WHERE l.subledger_type = 'GRNI' AND (@company::uuid IS NULL OR l.company_id = @company)
              GROUP BY 1, 2
            ), credited AS (
              SELECT t.receipt_id, sum(tl.credited_amount_fc) AS credited
              FROM app.pur_returns t
              JOIN app.pur_return_lines tl ON tl.tenant_id = t.tenant_id AND tl.return_id = t.id
              WHERE t.status = 'posted'
              GROUP BY 1
            ), expected AS (
              -- What is still owed on the receipt: received at expected cost, less what invoices settled, less what went back,
              -- plus what the supplier credited for the returned goods (that credit cleared the return's relief of GRNI).
              SELECT r.company_id, r.id AS receipt_id, r.number,
                     CASE WHEN r.status = 'posted' THEN coalesce(sum(rl.expected_cost_amount - rl.invoiced_cost_amount - rl.returned_cost_amount), 0) + coalesce(max(c.credited), 0) ELSE 0 END AS uninvoiced
              FROM app.pur_receipts r
              LEFT JOIN app.pur_receipt_lines rl ON rl.tenant_id = r.tenant_id AND rl.receipt_id = r.id
              LEFT JOIN credited c ON c.receipt_id = r.id
              WHERE r.status IN ('posted', 'reversed') AND (@company::uuid IS NULL OR r.company_id = @company)
              GROUP BY 1, 2, 3, r.status
            ), keys AS (
              SELECT company_id, receipt_id FROM booked UNION SELECT company_id, receipt_id FROM expected
            )
            SELECT k.company_id, k.receipt_id, e.number, coalesce(b.booked, 0) AS booked, coalesce(e.uninvoiced, 0) AS uninvoiced
            FROM keys k
            LEFT JOIN booked b ON b.company_id = k.company_id AND b.receipt_id = k.receipt_id
            LEFT JOIN expected e ON e.company_id = k.company_id AND e.receipt_id = k.receipt_id
            WHERE coalesce(b.booked, 0) <> coalesce(e.uninvoiced, 0)
            ORDER BY k.company_id, k.receipt_id
            LIMIT @limit
            """, new { company = companyId, limit = ProblemLimit }, uow.Transaction, cancellationToken: cancellationToken))).ToList();
        var problems = mismatches.Select(m => $"company {m.CompanyId} receipt {m.Number ?? m.ReceiptId.ToString()}: GRNI lines {N(m.Booked)}, uninvoiced receipt value {N(m.Uninvoiced)}").ToList();
        return Result(InvariantCodes.GrniMatchesReceipts, checkedCount, problems, $"{checkedCount} receipt(s), GRNI equal to the uninvoiced receipt value (net of returns and their credits) for every one");
    }

    private static InvariantResult Result(string code, long checkedCount, IReadOnlyList<string> problems, string summary) =>
        new(code, problems.Count == 0, checkedCount, problems, problems.Count == 0 ? summary : $"{problems.Count} problem(s) found (first {Math.Min(problems.Count, ProblemLimit)} listed)");

    private static string N(decimal? value) => value is { } v ? (v / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : "none";
}
