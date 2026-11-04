using System.Diagnostics;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Accounting.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Messaging.Outbox;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Reporting.Application.Facts;

/// <summary>One projected fact (ADR-0034): the statement that copies source rows after a watermark, and where the watermark comes from.</summary>
public sealed record FactProjection(string Fact, string Table, string WatermarkSql, string InsertSql)
{
    /// <summary>Late commits: rows whose stamp is older than the watermark by up to this much are scanned again (the conflict clause keeps them single).</summary>
    public static readonly TimeSpan Overlap = TimeSpan.FromMinutes(2);
}

/// <summary>The state of one fact's projection for the tenant.</summary>
public sealed record ProjectionStatus(string Fact, DateTimeOffset Watermark, long RowCount, DateTimeOffset? LastRunAt, int? LastDurationMs);

/// <summary>What a refresh did: rows added or updated per fact.</summary>
public sealed record ProjectionRun(IReadOnlyList<ProjectionStatus> Facts, long RowsProjected, int DurationMs);

/// <summary>
/// Keeps the facts of the `reporting` schema current for the tenant of the unit of work: each fact is an
/// INSERT … SELECT from its append-only source after a watermark, with a two-minute overlap and a conflict clause,
/// so a late commit is never missed and a row is never doubled; invoices are upserted so a status change (a reversal)
/// reaches the fact. A rebuild empties the tenant's facts and projects them again from the start. Row-level security
/// scopes every statement to the tenant; the owner of a fact table is the migration, like any other table.
/// </summary>
public sealed class ProjectionService(IUnitOfWorkAccessor unitOfWork, IClock clock)
{
    public static readonly IReadOnlyList<FactProjection> Facts =
    [
        new("journal_lines", "reporting.fact_journal_lines",
            "SELECT max(posted_at) FROM app.gl_journal_entries",
            """
            INSERT INTO reporting.fact_journal_lines (tenant_id, line_id, entry_id, line_no, company_id, branch_id, posting_date, document_date, fiscal_year_id, fiscal_period_id, entry_number,
              source_module, source_document_type, source_document_id, source_document_number, is_manual, is_reversal, is_closing_entry, is_opening_entry,
              account_id, account_role, partner_id, dimension_set_id, tax_code_id, subledger_type, currency_tc, debit_tc, credit_tc, currency_fc, debit_fc, credit_fc, currency_rc, debit_rc, credit_rc, description_i18n, posted_at)
            SELECT l.tenant_id, l.id, l.entry_id, l.line_no, l.company_id, l.branch_id, l.posting_date, e.document_date, e.fiscal_year_id, e.fiscal_period_id, e.number,
              e.source_module, e.source_document_type, e.source_document_id, e.source_document_number, e.is_manual, e.is_reversal, e.is_closing_entry, e.is_opening_entry,
              l.account_id, l.account_role, l.partner_id, l.dimension_set_id, l.tax_code_id, l.subledger_type, l.currency_tc, l.debit_tc, l.credit_tc, e.currency_fc, l.debit_fc, l.credit_fc, e.currency_rc, l.debit_rc, l.credit_rc, l.description_i18n, e.posted_at
            FROM app.gl_journal_lines l
            JOIN app.gl_journal_entries e ON e.tenant_id = l.tenant_id AND e.id = l.entry_id
            WHERE e.posted_at > @since
            ON CONFLICT (tenant_id, line_id) DO NOTHING
            """),
        new("sales_lines", "reporting.fact_sales_lines",
            "SELECT max(updated_at) FROM app.sls_invoices WHERE posted_at IS NOT NULL",
            """
            INSERT INTO reporting.fact_sales_lines (tenant_id, line_id, invoice_id, line_no, company_id, branch_id, invoice_number, kind, status, document_date, posting_date, due_date, partner_id, sales_rep_id, payment_terms_id, price_list_id,
              currency, exchange_rate, item_id, variant_id, uom_id, account_role, line_kind, description, quantity, unit_price, discount_pct, discount_amount, tax_code_id, tax_rate_pct, net_amount, tax_amount, gross_amount, net_amount_fc, dimension_set_id, posted_at)
            SELECT l.tenant_id, l.id, i.id, l.line_no, i.company_id, i.branch_id, i.number, i.kind, i.status, i.document_date, i.posting_date, i.due_date, i.partner_id, i.sales_rep_id, i.payment_terms_id, i.price_list_id,
              i.currency, i.exchange_rate, l.item_id, l.variant_id, l.uom_id, l.account_role, l.kind, l.description,
              s.sign * l.quantity, l.unit_price, l.discount_pct, s.sign * l.discount_amount, l.tax_code_id, l.tax_rate_pct, s.sign * l.net_amount, s.sign * l.tax_amount, s.sign * (l.net_amount + l.tax_amount), s.sign * l.net_amount_fc, l.dimension_set_id, i.posted_at
            FROM app.sls_invoice_lines l
            JOIN app.sls_invoices i ON i.tenant_id = l.tenant_id AND i.id = l.invoice_id
            CROSS JOIN LATERAL (SELECT CASE WHEN i.kind = 'credit_note' THEN -1 ELSE 1 END AS sign) s
            WHERE i.posted_at IS NOT NULL AND i.status IN ('posted', 'reversed') AND i.updated_at > @since
            ON CONFLICT (tenant_id, line_id) DO UPDATE SET status = EXCLUDED.status, due_date = EXCLUDED.due_date, sales_rep_id = EXCLUDED.sales_rep_id, posted_at = EXCLUDED.posted_at
            """),
        new("purchase_lines", "reporting.fact_purchase_lines",
            "SELECT max(updated_at) FROM app.pur_invoices WHERE posted_at IS NOT NULL",
            """
            INSERT INTO reporting.fact_purchase_lines (tenant_id, line_id, invoice_id, line_no, company_id, branch_id, invoice_number, kind, status, document_date, posting_date, due_date, partner_id, payment_terms_id,
              currency, exchange_rate, item_id, uom_id, account_role, line_kind, description, quantity, unit_price, discount_pct, tax_code_id, net_amount, tax_amount, wht_amount, gross_amount, net_amount_fc, dimension_set_id, posted_at)
            SELECT l.tenant_id, l.id, i.id, l.line_no, i.company_id, i.branch_id, i.number, i.kind, i.status, i.document_date, i.posting_date, i.due_date, i.partner_id, i.payment_terms_id,
              i.currency, i.exchange_rate, l.item_id, l.uom_id, l.account_role, l.kind, l.description,
              s.sign * l.quantity, l.unit_price, l.discount_pct, l.tax_code_id, s.sign * l.net_amount, s.sign * l.tax_amount, s.sign * l.wht_amount, s.sign * (l.net_amount + l.tax_amount), s.sign * l.net_amount_fc, l.dimension_set_id, i.posted_at
            FROM app.pur_invoice_lines l
            JOIN app.pur_invoices i ON i.tenant_id = l.tenant_id AND i.id = l.invoice_id
            CROSS JOIN LATERAL (SELECT CASE WHEN i.kind = 'credit_note' THEN -1 ELSE 1 END AS sign) s
            WHERE i.posted_at IS NOT NULL AND i.status IN ('posted', 'reversed') AND i.updated_at > @since
            ON CONFLICT (tenant_id, line_id) DO UPDATE SET status = EXCLUDED.status, due_date = EXCLUDED.due_date, posted_at = EXCLUDED.posted_at
            """),
        new("stock_movements", "reporting.fact_stock_movements",
            "SELECT max(posted_at) FROM app.inv_stock_ledger_entries",
            """
            INSERT INTO reporting.fact_stock_movements (tenant_id, entry_id, sequence, posting_id, company_id, item_id, variant_id, warehouse_id, bin_id, lot_id, serial_id, entry_type, quantity, entered_uom_id, entered_quantity,
              posting_date, fiscal_period_id, source_document_type, source_document_id, source_line_id, owner_partner_id, posted_at)
            SELECT e.tenant_id, e.id, e.sequence, e.posting_id, e.company_id, e.item_id, e.variant_id, e.warehouse_id, e.bin_id, e.lot_id, e.serial_id, e.entry_type, e.quantity, e.entered_uom_id, e.entered_quantity,
              e.posting_date, e.fiscal_period_id, e.source_document_type, e.source_document_id, e.source_line_id, e.owner_partner_id, e.posted_at
            FROM app.inv_stock_ledger_entries e
            WHERE e.posted_at > @since
            ON CONFLICT (tenant_id, entry_id) DO NOTHING
            """),
        new("stock_values", "reporting.fact_stock_values",
            "SELECT max(created_at) FROM app.inv_stock_value_entries",
            """
            INSERT INTO reporting.fact_stock_values (tenant_id, value_id, entry_id, company_id, item_id, warehouse_id, posting_date, valuation_date, value_type, valued_quantity, unit_cost, cost_amount, currency, account_role, offset_role,
              item_posting_group_id, journal_entry_id, source_document_type, source_document_id, created_at)
            SELECT v.tenant_id, v.id, v.sle_id, v.company_id, v.item_id, v.warehouse_id, v.posting_date, v.valuation_date, v.value_type, v.valued_quantity, v.unit_cost, v.cost_amount_actual, v.currency, v.account_role, v.offset_role,
              v.item_posting_group_id, v.gl_journal_entry_id, v.source_document_type, v.source_document_id, v.created_at
            FROM app.inv_stock_value_entries v
            WHERE v.created_at > @since
            ON CONFLICT (tenant_id, value_id) DO NOTHING
            """),
    ];

    private static readonly DateTimeOffset Epoch = new(1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task<IReadOnlyList<ProjectionStatus>> StatusAsync(CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var rows = (await uow.Connection.QueryAsync<StatusRow>(new CommandDefinition("SELECT fact AS Fact, watermark AS Watermark, row_count AS RowCount, last_run_at AS LastRunAt, last_duration_ms AS LastDurationMs FROM reporting.projections", transaction: uow.Transaction, cancellationToken: cancellationToken)))
            .ToDictionary(static r => r.Fact, StringComparer.Ordinal);
        return Facts.Select(f => rows.TryGetValue(f.Fact, out var r) ? new ProjectionStatus(r.Fact, r.Watermark, r.RowCount, r.LastRunAt, r.LastDurationMs) : new ProjectionStatus(f.Fact, Epoch, 0, null, null)).ToList();
    }

    /// <summary>
    /// Projects what the books hold that the facts do not yet: each fact's source is asked for its latest stamp (an
    /// indexed max), and a refresh runs only when one is newer than its watermark or a fact was never projected. A
    /// query therefore always sees what was posted before it, however recently; nothing is projected when nothing changed.
    /// </summary>
    public async Task<ProjectionRun?> EnsureFreshAsync(CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var status = (await StatusAsync(cancellationToken)).ToDictionary(static s => s.Fact, StringComparer.Ordinal);
        foreach (var fact in Facts)
        {
            var current = status[fact.Fact];
            if (current.LastRunAt is null)
            {
                return await RefreshAsync(cancellationToken);
            }

            var latestRaw = await uow.Connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(fact.WatermarkSql, transaction: uow.Transaction, cancellationToken: cancellationToken));
            if (latestRaw is { } raw && new DateTimeOffset(DateTime.SpecifyKind(raw, DateTimeKind.Utc), TimeSpan.Zero) > current.Watermark)
            {
                return await RefreshAsync(cancellationToken);
            }
        }

        return null;
    }

    public async Task<ProjectionRun> RefreshAsync(CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var tenant = uow.Context.TenantId;
        var total = 0L;
        var started = Stopwatch.StartNew();
        var results = new List<ProjectionStatus>();
        foreach (var fact in Facts)
        {
            var factWatch = Stopwatch.StartNew();
            var current = await uow.Connection.QuerySingleOrDefaultAsync<StatusRow>(new CommandDefinition("SELECT fact AS Fact, watermark AS Watermark, row_count AS RowCount, last_run_at AS LastRunAt, last_duration_ms AS LastDurationMs FROM reporting.projections WHERE fact = @fact FOR UPDATE", new { fact = fact.Fact }, uow.Transaction, cancellationToken: cancellationToken));
            var since = (current?.Watermark ?? Epoch) - FactProjection.Overlap;
            var inserted = await uow.Connection.ExecuteAsync(new CommandDefinition(fact.InsertSql, new { since }, uow.Transaction, cancellationToken: cancellationToken));
            // Npgsql hands a timestamptz scalar over as a UTC DateTime.
            var latestRaw = await uow.Connection.ExecuteScalarAsync<DateTime?>(new CommandDefinition(fact.WatermarkSql, transaction: uow.Transaction, cancellationToken: cancellationToken));
            var latest = latestRaw is { } raw ? new DateTimeOffset(DateTime.SpecifyKind(raw, DateTimeKind.Utc), TimeSpan.Zero) : current?.Watermark ?? Epoch;
            var watermark = latest > (current?.Watermark ?? Epoch) ? latest : current?.Watermark ?? Epoch;
            var count = await uow.Connection.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT count(*) FROM {fact.Table}", transaction: uow.Transaction, cancellationToken: cancellationToken));
            var duration = (int)factWatch.ElapsedMilliseconds;
            await uow.Connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO reporting.projections (tenant_id, fact, watermark, row_count, last_run_at, last_duration_ms)
                VALUES (@tenant, @fact, @watermark, @count, @now, @duration)
                ON CONFLICT (tenant_id, fact) DO UPDATE SET watermark = EXCLUDED.watermark, row_count = EXCLUDED.row_count, last_run_at = EXCLUDED.last_run_at, last_duration_ms = EXCLUDED.last_duration_ms
                """, new { tenant = tenant.Value, fact = fact.Fact, watermark, count, now = clock.UtcNow, duration }, uow.Transaction, cancellationToken: cancellationToken));
            total += inserted;
            results.Add(new ProjectionStatus(fact.Fact, watermark, count, clock.UtcNow, duration));
        }

        return new ProjectionRun(results, total, (int)started.ElapsedMilliseconds);
    }

    /// <summary>Empties the tenant's facts and projects them again from the beginning; the result must equal what the increments built (verified by the tests).</summary>
    public async Task<ProjectionRun> RebuildAsync(CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        foreach (var fact in Facts)
        {
            await uow.Connection.ExecuteAsync(new CommandDefinition($"DELETE FROM {fact.Table}", transaction: uow.Transaction, cancellationToken: cancellationToken));
        }

        await uow.Connection.ExecuteAsync(new CommandDefinition("DELETE FROM reporting.projections", transaction: uow.Transaction, cancellationToken: cancellationToken));
        return await RefreshAsync(cancellationToken);
    }

    private sealed class StatusRow
    {
        public string Fact { get; set; } = string.Empty;

        public DateTimeOffset Watermark { get; set; }

        public long RowCount { get; set; }

        public DateTimeOffset? LastRunAt { get; set; }

        public int? LastDurationMs { get; set; }
    }
}

public sealed record ProjectionPayload(bool Rebuild = false);

/// <summary>Job <c>reporting.project</c>: the platform schedule runs it for every tenant every few minutes; a posting kicks it for its tenant; the API runs it on demand.</summary>
public sealed class ProjectionJob(IUnitOfWorkFactory unitOfWorkFactory, IServiceScopeFactory scopeFactory, ITenantContextAccessor tenantContext, ProjectionService projections) : IJobHandler<ProjectionPayload>
{
    public static string JobType => "reporting.project";

    public async Task<object?> ExecuteAsync(ProjectionPayload payload, IJobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var rebuild = payload?.Rebuild == true;
        if (context.TenantId is not null)
        {
            var run = rebuild ? await projections.RebuildAsync(cancellationToken) : await projections.RefreshAsync(cancellationToken);
            return new { tenants = 1, run.RowsProjected, run.DurationMs };
        }

        var tenants = await InTenantAsync(null, static (sp, ct) => sp.GetRequiredService<ITenantDirectory>().ListAsync(ct), cancellationToken);
        var rows = 0L;
        var count = 0;
        foreach (var tenant in tenants.Where(static t => t.Status == "active"))
        {
            var run = await InTenantAsync(tenant.Id.Value, (sp, ct) => rebuild ? sp.GetRequiredService<ProjectionService>().RebuildAsync(ct) : sp.GetRequiredService<ProjectionService>().RefreshAsync(ct), cancellationToken);
            rows += run.RowsProjected;
            count++;
        }

        return new { tenants = count, rowsProjected = rows };
    }

    private async Task<T> InTenantAsync<T>(Guid? tenantId, Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        var requestId = "job-" + Guid.CreateVersion7().ToString("N")[^12..];
        var context = tenantId is { } id ? TenantContext.System(new TenantId(id), requestId) : TenantContext.Anonymous(requestId);
        await using var scope = scopeFactory.CreateAsyncScope();
        await using var unitOfWork = await unitOfWorkFactory.BeginAsync(context, cancellationToken: cancellationToken);
        scope.ServiceProvider.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var ambient = tenantContext.Use(context);
        var result = await work(scope.ServiceProvider, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return result;
    }
}

/// <summary>A posting kicks the tenant's projection so the facts follow the books within seconds; one job per tenant and minute at most.</summary>
public sealed class ProjectOnJournalPosted(IJobQueue jobs, IClock clock) : IIntegrationEventHandler<JournalEntryPosted>
{
    public Task HandleAsync(JournalEntryPosted integrationEvent, EventContext context, CancellationToken cancellationToken) => ProjectionKick.EnqueueAsync(jobs, clock, cancellationToken);
}

public sealed class ProjectOnStockPosted(IJobQueue jobs, IClock clock) : IIntegrationEventHandler<StockPosted>
{
    public Task HandleAsync(StockPosted integrationEvent, EventContext context, CancellationToken cancellationToken) => ProjectionKick.EnqueueAsync(jobs, clock, cancellationToken);
}

internal static class ProjectionKick
{
    public static Task EnqueueAsync(IJobQueue jobs, IClock clock, CancellationToken cancellationToken) =>
        jobs.EnqueueAsync(new JobRequest(ProjectionJob.JobType, new ProjectionPayload(), IdempotencyKey: "reporting.project:" + clock.UtcNow.ToString("yyyyMMddHHmm", System.Globalization.CultureInfo.InvariantCulture)), cancellationToken);
}
