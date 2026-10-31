using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.FixedAssets.Contracts;
using Quicker.FixedAssets.Domain;
using Quicker.FixedAssets.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;

namespace Quicker.FixedAssets.Application;

/// <summary>
/// Depreciation (A-175): the plan of every asset in every book, regenerated prospectively after any change; the monthly
/// run that posts a book's planned months through a date as one journal (Dr depreciation expense per asset, with the
/// asset's dimensions / Cr accumulated depreciation in the FA subledger); its reversal; the forecast.
/// </summary>
public sealed class DepreciationService(
    FixedAssetsDbContext db,
    ICompanyDirectory companies,
    IPostingService posting,
    INumberAllocator numbering,
    IDimensionSets dimensionSets,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string RunType = FixedAssetDocumentTypes.DepreciationRun;

    // ------------------------------------------------------------------ plans

    /// <summary>
    /// Rebuilds the book's plan from its first unposted month: what is left to depreciate (net book value less salvage)
    /// over the life that is left, by the book's method. A book that has not started yet plans from its in-service month
    /// with the convention's first-month weight.
    /// </summary>
    public async Task RegenerateAsync(AssetBookValue value, int minorUnits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var posted = value.Schedule.Where(static s => s.Status == ScheduleStatuses.Posted).ToList();
        foreach (var stale in value.Schedule.Where(static s => s.Status != ScheduleStatuses.Posted).ToList())
        {
            value.Schedule.Remove(stale);
            db.Schedule.Remove(stale);
        }

        if (value.Status is AssetStatuses.Disposed or AssetStatuses.Draft)
        {
            return;
        }

        var started = posted.Count > 0 || value.MonthsConsumed > 0m;
        var start = started
            ? (posted.Count > 0 ? posted.Max(static s => s.PeriodStart).AddMonths(1) : (value.DepreciatedThrough is { } through ? new DateOnly(through.Year, through.Month, 1).AddMonths(1) : Depreciation.FirstMonth(value.Convention, value.DepreciationStart)))
            : Depreciation.FirstMonth(value.Convention, value.DepreciationStart);
        var firstWeight = started ? 1m : Depreciation.FirstMonthWeight(value.Convention, value.DepreciationStart);
        var depreciable = value.NetBookValue - value.SalvageValue;
        var lifeLeft = value.LifeMonths - value.MonthsConsumed;
        IReadOnlyDictionary<DateOnly, decimal>? units = null;
        if (value.Method == DepreciationMethods.UnitsOfProduction)
        {
            var usage = await db.Usage.AsNoTracking().Where(u => u.AssetBookId == value.Id && u.PeriodStart >= start).ToListAsync(cancellationToken);
            units = usage.ToDictionary(static u => u.PeriodStart, static u => u.Units);
        }

        var plan = Depreciation.Plan(new DepreciationPlanRequest(depreciable, lifeLeft, value.Method, start, firstWeight, minorUnits, value.NetBookValue, value.SalvageValue, value.RatePct, value.TotalUnits, units));
        foreach (var period in plan)
        {
            value.Schedule.Add(new ScheduleLine
            {
                Id = Guid.CreateVersion7(),
                AssetBookId = value.Id,
                PeriodStart = period.Start,
                PeriodEnd = period.End,
                Weight = period.Weight,
                PlannedAmount = period.Amount,
                Status = ScheduleStatuses.Planned,
            });
        }

        if (depreciable <= 0m && value.Status == AssetStatuses.Active && value.Method != DepreciationMethods.None)
        {
            value.Status = AssetStatuses.FullyDepreciated;
        }

        value.UpdatedAt = clock.UtcNow;
    }

    // ------------------------------------------------------------------ runs

    public async Task<IReadOnlyList<DepreciationRunSummary>> RunsAsync(Guid companyId, Guid? bookId, CancellationToken cancellationToken)
    {
        var books = await db.Books.AsNoTracking().Where(b => b.CompanyId == companyId).ToDictionaryAsync(static b => b.Id, static b => b.Code, cancellationToken);
        var query = db.Runs.AsNoTracking().Where(r => r.CompanyId == companyId);
        if (bookId is { } b)
        {
            query = query.Where(r => r.BookId == b);
        }

        return (await query.OrderByDescending(static r => r.PeriodEnd).ThenByDescending(static r => r.CreatedAt).Take(200).ToListAsync(cancellationToken)).Select(r => Map(r, books.GetValueOrDefault(r.BookId, ""))).ToList();
    }

    public async Task<Result<DepreciationRunSummary>> GetRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.Runs.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(RunType, runId);
        }

        var book = await db.Books.AsNoTracking().SingleAsync(b => b.Id == run.BookId, cancellationToken);
        return Map(run, book.Code);
    }

    /// <summary>Posts every planned month of the book through the date (catching up months left behind) as one run.</summary>
    public async Task<Result<DepreciationRunSummary>> PostRunAsync(DepreciationRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var book = request.BookId is { } id
            ? await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id && b.CompanyId == request.CompanyId, cancellationToken)
            : await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == request.CompanyId && b.PostsToGl, cancellationToken);
        if (book is null)
        {
            return Error.NotFound(BookService.EntityType, request.BookId ?? Guid.Empty);
        }

        var through = request.Through;
        var postingDate = request.PostingDate ?? through;
        if (postingDate < through)
        {
            return Error.Validation("depreciation.posting_date_early", "The run posts on or after the last day it covers.");
        }

        return await PostAsync(company, book, through, postingDate, null, null, cancellationToken);
    }

    /// <summary>
    /// The run itself. With <paramref name="onlyAsset"/> and <paramref name="leavingWeight"/> set, it is the catch-up of one asset
    /// about to leave (a disposal): the month it leaves in counts by the convention's leaving weight.
    /// </summary>
    internal async Task<Result<DepreciationRunSummary>> PostAsync(CompanyInfo company, AssetBook book, DateOnly through, DateOnly postingDate, Guid? onlyAsset, decimal? leavingWeight, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        ArgumentNullException.ThrowIfNull(book);
        var minor = company.FunctionalCurrency.MinorUnits;
        var monthStart = new DateOnly(through.Year, through.Month, 1);
        var values = await db.AssetBooks.Include(static v => v.Schedule)
            .Where(v => v.BookId == book.Id && v.Status == AssetStatuses.Active && (onlyAsset == null || v.AssetId == onlyAsset))
            .ToListAsync(cancellationToken);
        var assetIds = values.Select(static v => v.AssetId).Distinct().ToList();
        var assets = await db.Assets.Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        var categories = await db.Categories.AsNoTracking().ToDictionaryAsync(static c => c.Id, cancellationToken);

        var due = new List<(AssetBookValue Value, ScheduleLine Line, decimal Amount)>();
        foreach (var value in values.OrderBy(v => assets[v.AssetId].Number, StringComparer.Ordinal))
        {
            foreach (var line in value.Schedule.Where(static s => s.Status == ScheduleStatuses.Planned).OrderBy(static s => s.PeriodStart))
            {
                if (leavingWeight is { } weight && line.PeriodStart == monthStart)
                {
                    // The month the asset leaves in: a share of the month by the convention, the rest of the plan is dropped with the asset.
                    var share = RoundingPolicy.Default.Round(line.PlannedAmount * weight / line.Weight, minor);
                    if (share > 0m)
                    {
                        line.Weight = weight;
                        line.PlannedAmount = share;
                        due.Add((value, line, share));
                    }
                }
                else if (line.PeriodEnd <= through)
                {
                    due.Add((value, line, line.PlannedAmount));
                }
            }
        }

        due = due.Where(static d => d.Amount != 0m).ToList();
        if (due.Count == 0)
        {
            return Error.Conflict("depreciation.nothing_due", "No planned depreciation falls on or before that date in this book.").WithWhy(("book", book.Code), ("through", through));
        }

        if (await db.Runs.AnyAsync(r => r.BookId == book.Id && r.Status == "posted" && r.PeriodEnd > through, cancellationToken))
        {
            return Error.Conflict("depreciation.later_run_exists", "A later run of this book is posted; reverse it first to post an earlier month.").WithWhy(("through", through));
        }

        var run = new DepreciationRun
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            BookId = book.Id,
            PeriodStart = due.Min(static d => d.Line.PeriodStart),
            PeriodEnd = through,
            PostingDate = postingDate,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(RunType, company.Id, "DEP", "DEP-{yyyy}-{seq:4}", "yearly", cancellationToken);
        var allocated = await numbering.AllocateAsync(new NumberRequest(RunType, company.Id, null, postingDate, run.Id), cancellationToken);
        if (allocated.IsFailure)
        {
            return allocated.Error!;
        }

        run.Number = allocated.Value.Text;

        // One transaction per asset for the run; one pair of lines per asset in the journal.
        var transactions = new Dictionary<Guid, AssetTransaction>();
        var lines = new List<PostingLine>();
        foreach (var byAsset in due.GroupBy(static d => d.Value.AssetId))
        {
            var asset = assets[byAsset.Key];
            var amount = byAsset.Sum(static d => d.Amount);
            var transaction = new AssetTransaction
            {
                Id = Guid.CreateVersion7(),
                CompanyId = company.Id.Value,
                AssetId = asset.Id,
                BookId = book.Id,
                PostingDate = postingDate,
                Kind = AssetTransactionKinds.Depreciation,
                AccumulatedDepreciationDelta = amount,
                RunId = run.Id,
                Reference = run.Number,
                Detail = $"{{\"through\":\"{through:yyyy-MM-dd}\",\"months\":{byAsset.Count()}}}",
                CreatedAt = clock.UtcNow,
            };
            transactions[asset.Id] = transaction;
            if (book.PostsToGl)
            {
                var dimensions = asset.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null;
                var keys = new PostingKeys(RunType, AssetCategoryId: asset.CategoryId, BranchId: asset.BranchId);
                var description = LocalizedText.Bilingual($"Depreciation {asset.Number}", $"إهلاك {asset.Number}");
                lines.Add(new PostingLine(AccountRoles.DepreciationExpense, amount, keys, Dimensions: dimensions, Description: description));
                lines.Add(new PostingLine(AccountRoles.FaAccDep, -amount, keys, Dimensions: dimensions, SubledgerType: SubledgerTypes.FixedAssets, SubledgerRef: transaction.Id, Description: description));
            }

            foreach (var (value, line, posted) in byAsset)
            {
                line.Status = ScheduleStatuses.Posted;
                line.PostedAmount = posted;
                line.TransactionId = transaction.Id;
                line.RunId = run.Id;
                value.AccumulatedDepreciation += posted;
                value.MonthsConsumed += line.Weight;
                value.DepreciatedThrough = value.DepreciatedThrough is { } t && t > line.PeriodEnd ? t : line.PeriodEnd;
                value.UpdatedAt = clock.UtcNow;
            }
        }

        if (book.PostsToGl)
        {
            var description = LocalizedText.Bilingual($"Depreciation run {run.Number} through {through:yyyy-MM-dd} ({book.Code})", $"عملية إهلاك {run.Number} حتى {through:yyyy-MM-dd} ({book.Code})");
            var posted = await posting.PostAsync(new PostingRequest(company.Id, "fixed_assets", RunType, run.Id, postingDate, company.FunctionalCurrency.Code, lines, run.Number, through, description, IdempotencyKey: $"fa_depreciation_run:{run.Id}"), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            run.JournalEntryId = posted.Value.EntryId;
            foreach (var transaction in transactions.Values)
            {
                transaction.JournalEntryId = posted.Value.EntryId;
            }
        }

        foreach (var value in due.Select(static d => d.Value).Distinct())
        {
            if (value.NetBookValue - value.SalvageValue <= 0m && !value.Schedule.Any(static s => s.Status == ScheduleStatuses.Planned))
            {
                value.Status = AssetStatuses.FullyDepreciated;
                if (book.PostsToGl && assets[value.AssetId].Status == AssetStatuses.Active)
                {
                    assets[value.AssetId].Status = AssetStatuses.FullyDepreciated;
                    assets[value.AssetId].UpdatedAt = clock.UtcNow;
                }
            }
        }

        run.AssetCount = transactions.Count;
        run.Total = transactions.Values.Sum(static t => t.AccumulatedDepreciationDelta);
        db.Runs.Add(run);
        db.Transactions.AddRange(transactions.Values);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(RunType, run.Id, run.Number, AuditActions.Posted, After: new { book = book.Code, through, run.AssetCount, run.Total, run.JournalEntryId }, CompanyId: company.Id.Value), cancellationToken);
        return Map(run, book.Code);
    }

    /// <summary>Reverses the book's latest run: the mirror journal, the plan back to planned, the values and statuses restored.</summary>
    public async Task<Result<DepreciationRunSummary>> ReverseRunAsync(Guid runId, ReverseDepreciationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = await db.Runs.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(RunType, runId);
        }

        if (run.Status != "posted")
        {
            return Error.Conflict("depreciation.run_not_posted", "The run is already reversed.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("depreciation.reason_required", "A reversal names its reason.");
        }

        if (await db.Runs.AnyAsync(r => r.BookId == run.BookId && r.Status == "posted" && r.PeriodEnd > run.PeriodEnd, cancellationToken))
        {
            return Error.Conflict("depreciation.not_latest", "Only the latest run of a book is reversed; reverse the later ones first.");
        }

        var book = await db.Books.AsNoTracking().SingleAsync(b => b.Id == run.BookId, cancellationToken);
        var transactions = await db.Transactions.Where(t => t.RunId == run.Id && t.Kind == AssetTransactionKinds.Depreciation).ToListAsync(cancellationToken);
        var lines = await db.Schedule.Where(s => s.RunId == run.Id).ToListAsync(cancellationToken);
        var valueIds = lines.Select(static l => l.AssetBookId).Distinct().ToList();
        var values = await db.AssetBooks.Where(v => valueIds.Contains(v.Id)).ToDictionaryAsync(static v => v.Id, cancellationToken);
        var assetIds = transactions.Select(static t => t.AssetId).Distinct().ToList();
        var assets = await db.Assets.Where(a => assetIds.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, cancellationToken);
        if (assets.Values.Any(static a => a.Status == AssetStatuses.Disposed))
        {
            return Error.Conflict("depreciation.asset_disposed", "An asset of this run has been disposed of since; its depreciation stays.");
        }

        var reversalDate = request.ReversalDate;
        Guid? reversalEntry = null;
        if (run.JournalEntryId is { } entry)
        {
            var reversed = await posting.ReverseAsync(entry, request.ReversalDate, request.Reason, cancellationToken: cancellationToken);
            if (reversed.IsFailure)
            {
                return reversed.Error!;
            }

            reversalEntry = reversed.Value.EntryId;
            reversalDate = reversed.Value.PostingDate;
        }

        foreach (var transaction in transactions)
        {
            db.Transactions.Add(new AssetTransaction
            {
                Id = Guid.CreateVersion7(),
                CompanyId = transaction.CompanyId,
                AssetId = transaction.AssetId,
                BookId = transaction.BookId,
                PostingDate = reversalDate ?? run.PostingDate,
                Kind = AssetTransactionKinds.Reversal,
                AccumulatedDepreciationDelta = -transaction.AccumulatedDepreciationDelta,
                JournalEntryId = reversalEntry,
                ReversesId = transaction.Id,
                RunId = run.Id,
                Reference = run.Number,
                Detail = "{}",
                CreatedAt = clock.UtcNow,
            });
        }

        foreach (var line in lines)
        {
            var value = values[line.AssetBookId];
            value.AccumulatedDepreciation -= line.PostedAmount;
            value.MonthsConsumed -= line.Weight;
            line.Status = ScheduleStatuses.Planned;
            line.PostedAmount = 0m;
            line.TransactionId = null;
            line.RunId = null;
        }

        foreach (var value in values.Values)
        {
            var remaining = await db.Schedule.Where(s => s.AssetBookId == value.Id && s.Status == ScheduleStatuses.Posted).Select(static s => (DateOnly?)s.PeriodEnd).MaxAsync(cancellationToken);
            value.DepreciatedThrough = remaining;
            if (value.Status == AssetStatuses.FullyDepreciated)
            {
                value.Status = AssetStatuses.Active;
            }

            value.UpdatedAt = clock.UtcNow;
        }

        if (book.PostsToGl)
        {
            foreach (var asset in assets.Values.Where(static a => a.Status == AssetStatuses.FullyDepreciated))
            {
                asset.Status = AssetStatuses.Active;
                asset.UpdatedAt = clock.UtcNow;
            }
        }

        run.Status = "reversed";
        run.ReversalEntryId = reversalEntry;
        run.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(RunType, run.Id, run.Number, AuditActions.Reversed, After: new { reversalEntryId = reversalEntry }, Reason: request.Reason, CompanyId: run.CompanyId), cancellationToken);
        return Map(run, book.Code);
    }

    // ------------------------------------------------------------------ reads

    /// <summary>Planned and posted depreciation of a book by month over a range, for the forecast.</summary>
    public async Task<IReadOnlyList<ForecastRow>> ForecastAsync(Guid companyId, Guid? bookId, DateOnly rangeStart, DateOnly rangeEnd, CancellationToken cancellationToken)
    {
        var book = bookId is { } id
            ? await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id && b.CompanyId == companyId, cancellationToken)
            : await db.Books.AsNoTracking().SingleOrDefaultAsync(b => b.CompanyId == companyId && b.PostsToGl, cancellationToken);
        if (book is null)
        {
            return [];
        }

        var rows = await (from s in db.Schedule.AsNoTracking()
                          join v in db.AssetBooks.AsNoTracking() on s.AssetBookId equals v.Id
                          where v.BookId == book.Id && s.PeriodStart >= rangeStart && s.PeriodStart <= rangeEnd && s.Status != ScheduleStatuses.Skipped
                          select new { s.PeriodStart, s.PeriodEnd, s.PlannedAmount, s.PostedAmount, s.Status, v.AssetId })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(static r => r.PeriodStart).OrderBy(static g => g.Key)
            .Select(g => new ForecastRow(g.Key, g.First().PeriodEnd, g.Sum(static r => r.Status == ScheduleStatuses.Posted ? r.PostedAmount : r.PlannedAmount), g.Sum(static r => r.PostedAmount), g.Select(static r => r.AssetId).Distinct().Count()))
            .ToList();
    }

    private static DepreciationRunSummary Map(DepreciationRun r, string bookCode) => new(r.Id, r.CompanyId, r.BookId, bookCode, r.Number, r.PeriodStart, r.PeriodEnd, r.PostingDate, r.Status, r.JournalEntryId, r.ReversalEntryId, r.AssetCount, r.Total, r.CreatedAt);
}
