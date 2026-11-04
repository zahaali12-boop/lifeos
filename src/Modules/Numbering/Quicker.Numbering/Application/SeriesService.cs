using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Numbering.Domain;
using Quicker.Numbering.Persistence;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Numbering.Application;

/// <summary>Series administration: definitions, counters (with the audited reset), allocation log and the gapless audit.</summary>
public sealed class SeriesService(NumberingDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, IAuditSink audit, IClock clock)
{
    private static readonly string[] ResetPolicies = ["never", "yearly", "monthly"];

    private Guid? ActorUserId => unitOfWork.Current.Context.UserId?.Value;

    public async Task<IReadOnlyList<SeriesSummary>> ListAsync(string? documentType, Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Series.AsQueryable();
        if (!string.IsNullOrWhiteSpace(documentType))
        {
            var type = documentType.Trim().ToLowerInvariant();
            query = query.Where(s => s.DocumentType == type);
        }

        if (companyId is { } company)
        {
            query = query.Where(s => s.CompanyId == company);
        }

        var series = await query.OrderBy(static s => s.DocumentType).ThenBy(static s => s.Code).ToListAsync(cancellationToken);
        var result = new List<SeriesSummary>(series.Count);
        foreach (var s in series)
        {
            result.Add(await MapAsync(s, cancellationToken));
        }

        return result;
    }

    public async Task<SeriesSummary?> GetAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        return series is null ? null : await MapAsync(series, cancellationToken);
    }

    public async Task<Result<SeriesSummary>> CreateAsync(SaveSeriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var series = new Series { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(series, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        db.Series.Add(series);
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(series, cancellationToken);
    }

    public async Task<Result<SeriesSummary>> UpdateAsync(Guid seriesId, SaveSeriesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series is null)
        {
            return Error.NotFound("series", seriesId);
        }

        var issued = await db.Allocations.AnyAsync(a => a.SeriesId == seriesId, cancellationToken);
        if (issued && (series.Gapless != request.Gapless || series.ResetPolicy != request.ResetPolicy || series.DocumentType != request.DocumentType?.Trim().ToLowerInvariant()))
        {
            // Changing what a number means after numbers exist would make the issued sequence unreadable for auditors.
            return Error.Conflict("series.in_use", "Gapless, reset policy and document type cannot change once numbers were issued; create a new series and close this one with valid_to.");
        }

        var applied = await ApplyAsync(series, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        series.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(series, cancellationToken);
    }

    private async Task<Result> ApplyAsync(Series series, SaveSeriesRequest request, CancellationToken cancellationToken)
    {
        var code = ValidateCode(request.Code, "series");
        var documentType = ValidateCode(request.DocumentType, "series.document_type");
        if (code.IsFailure || documentType.IsFailure)
        {
            return code.Error ?? documentType.Error!;
        }

        var template = Templates.Validate(request.Template);
        if (template.IsFailure)
        {
            return template.Error!;
        }

        if (!ResetPolicies.Contains(request.ResetPolicy, StringComparer.Ordinal))
        {
            return Error.Validation("series.reset_policy_invalid", "Reset policy is never, yearly or monthly.");
        }

        if (request.StartNumber < 1)
        {
            return Error.Validation("series.start_number_invalid", "The start number is at least 1.");
        }

        if (request.ValidFrom is { } from && request.ValidTo is { } to && to < from)
        {
            return Error.Validation("series.validity_invalid", "valid_to must be on or after valid_from.");
        }

        var company = await companies.FindAsync(new Kernel.Ids.CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        if (request.BranchId is { } branchId)
        {
            var branch = await companies.FindBranchAsync(new Kernel.Ids.BranchId(branchId), cancellationToken);
            if (branch is null || branch.CompanyId.Value != request.CompanyId)
            {
                return Error.NotFound("branch", branchId);
            }
        }
        else if (Templates.Needs(request.Template, "branch"))
        {
            return Error.Validation("series.branch_required", "A template with {branch} needs a branch on the series.");
        }

        if (request.FiscalYearId is { } fiscalYearId)
        {
            var exists = await unitOfWork.Current.Connection.ExecuteScalarAsync<bool>(
                "SELECT EXISTS (SELECT 1 FROM app.org_fiscal_years y JOIN app.org_companies c ON c.tenant_id = y.tenant_id AND c.fiscal_calendar_id = y.calendar_id WHERE y.id = @id AND c.id = @company)",
                new { id = fiscalYearId, company = request.CompanyId }, unitOfWork.Current.Transaction);
            if (!exists)
            {
                return Error.NotFound("fiscal_year", fiscalYearId);
            }
        }

        var normalizedCode = code.Value.ToUpperInvariant();
        if (await db.Series.AnyAsync(s => s.Code == normalizedCode && s.Id != series.Id, cancellationToken))
        {
            return Error.Conflict("series.code_taken", $"A series with code '{normalizedCode}' already exists.");
        }

        series.Code = normalizedCode;
        series.DocumentType = documentType.Value.ToLowerInvariant();
        series.CompanyId = request.CompanyId;
        series.BranchId = request.BranchId;
        series.FiscalYearId = request.FiscalYearId;
        series.Template = request.Template.Trim();
        series.StartNumber = request.StartNumber;
        series.Gapless = request.Gapless;
        series.ResetPolicy = request.ResetPolicy;
        series.ValidFrom = request.ValidFrom;
        series.ValidTo = request.ValidTo;
        series.IsDefault = request.IsDefault;
        series.IsActive = request.IsActive;
        return Result.Success();
    }

    internal static Result<string> ValidateCode(string? value, string field)
    {
        var code = value?.Trim() ?? string.Empty;
        var valid = code.Length is >= 1 and <= 32 && char.IsAsciiLetterOrDigit(code[0]) && code.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-');
        return valid ? code : Error.Validation($"{field}.code_invalid", "Codes are 1–32 letters, digits, '_', '.' or '-'.");
    }

    // ------------------------------------------------------------------ counters

    /// <summary>Sets the next number of one period. Going backwards is a reset: reason required, only down to one past the highest issued number.</summary>
    public async Task<Result<SeriesSummary>> SetCounterAsync(Guid seriesId, SetCounterRequest request, bool mayReset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series is null)
        {
            return Error.NotFound("series", seriesId);
        }

        if (request.NextNumber < 1)
        {
            return Error.Validation("series.counter_invalid", "The next number is at least 1.");
        }

        var periodKey = request.PeriodKey?.Trim() ?? string.Empty;
        var counter = await db.Counters.SingleOrDefaultAsync(c => c.SeriesId == seriesId && c.PeriodKey == periodKey, cancellationToken);
        var current = counter?.NextNumber ?? series.StartNumber;
        var highest = await db.Allocations.Where(a => a.SeriesId == seriesId && a.PeriodKey == periodKey).Select(static a => (long?)a.Number).MaxAsync(cancellationToken);
        if (highest is { } issued && request.NextNumber <= issued)
        {
            return Error.Conflict("series.counter_below_issued", $"Number {issued} was already issued in this period; the counter cannot go below {issued + 1}.")
                .WithWhy(("highestIssued", issued), ("periodKey", periodKey));
        }

        if (request.NextNumber < current)
        {
            if (!mayReset)
            {
                return Error.Forbidden("series.reset_forbidden", $"Moving a counter backwards requires the {NumberingPermissions.Reset} permission.").WithWhy(("requiredPermission", NumberingPermissions.Reset));
            }

            if (string.IsNullOrWhiteSpace(request.Reason))
            {
                return Error.Validation("series.reason_required", "Moving a counter backwards requires a reason.");
            }
        }

        if (counter is null)
        {
            counter = new SeriesCounter { SeriesId = seriesId, PeriodKey = periodKey };
            db.Counters.Add(counter);
        }

        counter.NextNumber = request.NextNumber;
        counter.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("numbering_series", seriesId, series.Code, request.NextNumber < current ? AuditActions.Override : AuditActions.Updated,
            Before: new { periodKey, nextNumber = current }, After: new { periodKey, nextNumber = request.NextNumber }, Reason: request.Reason?.Trim(), CompanyId: series.CompanyId), cancellationToken);
        return await MapAsync(series, cancellationToken);
    }

    // ------------------------------------------------------------------ allocations and gaps

    public async Task<Result<IReadOnlyList<AllocationSummary>>> ListAllocationsAsync(Guid seriesId, string? periodKey, int limit, CancellationToken cancellationToken)
    {
        if (!await db.Series.AnyAsync(s => s.Id == seriesId, cancellationToken))
        {
            return Error.NotFound("series", seriesId);
        }

        var query = db.Allocations.Where(a => a.SeriesId == seriesId);
        if (periodKey is not null)
        {
            query = query.Where(a => a.PeriodKey == periodKey);
        }

        var rows = await query.OrderByDescending(static a => a.AllocatedAt).ThenByDescending(static a => a.Number).Take(Math.Clamp(limit, 1, 1000)).ToListAsync(cancellationToken);
        return rows.Select(static a => new AllocationSummary(a.Id, a.SeriesId, a.PeriodKey, a.Number, a.Text, a.DocumentType, a.DocumentId, a.AllocatedBy, a.AllocatedAt)).ToList();
    }

    /// <summary>For each period: first and last issued number, how many, and every number between start and counter that was never issued.</summary>
    public async Task<Result<SeriesGapReport>> GapsAsync(Guid seriesId, CancellationToken cancellationToken)
    {
        var series = await db.Series.SingleOrDefaultAsync(s => s.Id == seriesId, cancellationToken);
        if (series is null)
        {
            return Error.NotFound("series", seriesId);
        }

        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<(string PeriodKey, long First, long Last, long Allocated, long NextNumber, long[] Missing)>("""
            SELECT c.period_key,
                   COALESCE(min(a.number), c.next_number) AS first,
                   COALESCE(max(a.number), c.next_number - 1) AS last,
                   count(a.number) AS allocated,
                   c.next_number,
                   COALESCE((SELECT array_agg(n ORDER BY n)
                             FROM generate_series(@start, c.next_number - 1) AS g(n)
                             WHERE NOT EXISTS (SELECT 1 FROM app.num_allocations x WHERE x.series_id = c.series_id AND x.period_key = c.period_key AND x.number = g.n)), ARRAY[]::bigint[]) AS missing
            FROM app.num_series_counters c
            LEFT JOIN app.num_allocations a ON a.series_id = c.series_id AND a.period_key = c.period_key
            WHERE c.series_id = @series
            GROUP BY c.series_id, c.period_key, c.next_number
            ORDER BY c.period_key
            """, new { series = seriesId, start = series.StartNumber }, uow.Transaction);
        return new SeriesGapReport(series.Id, series.Code, series.Gapless, rows.Select(static r => new PeriodGaps(r.PeriodKey, r.First, r.Last, r.Allocated, r.NextNumber, r.Missing)).ToList());
    }

    // ------------------------------------------------------------------ mapping

    private async Task<SeriesSummary> MapAsync(Series s, CancellationToken cancellationToken)
    {
        var counters = await db.Counters.Where(c => c.SeriesId == s.Id).OrderBy(static c => c.PeriodKey).ToListAsync(cancellationToken);
        var allocated = await db.Allocations.Where(a => a.SeriesId == s.Id).GroupBy(static a => a.PeriodKey).Select(static g => new { PeriodKey = g.Key, Count = g.LongCount() }).ToDictionaryAsync(static g => g.PeriodKey, static g => g.Count, StringComparer.Ordinal, cancellationToken);
        return new SeriesSummary(s.Id, s.Code, s.DocumentType, s.CompanyId, s.BranchId, s.FiscalYearId, s.Template, s.StartNumber, s.Gapless, s.ResetPolicy, s.ValidFrom, s.ValidTo, s.IsDefault, s.IsActive,
            counters.Select(c => new CounterSummary(c.PeriodKey, c.NextNumber, allocated.GetValueOrDefault(c.PeriodKey), c.UpdatedAt)).ToList());
    }
}
