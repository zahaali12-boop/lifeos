using System.Globalization;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Numbering.Contracts;
using Quicker.Numbering.Domain;
using Quicker.Numbering.Persistence;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Numbering.Application;

/// <summary>
/// Picks the series for a request (most specific match wins: branch, then fiscal year, then default) and takes the
/// next number under the counter's row lock inside the current unit of work (ADR-0016).
/// </summary>
public sealed class NumberAllocator(NumberingDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, IFiscalPeriodResolver periods) : INumberAllocator
{
    private sealed record Selection(Series Series, string PeriodKey, string RenderedTemplate);

    public async Task<Result<AllocatedNumber>> AllocateAsync(NumberRequest request, CancellationToken cancellationToken = default)
    {
        var selected = await SelectAsync(request, cancellationToken);
        if (selected.IsFailure)
        {
            return selected.Error!;
        }

        var (series, periodKey, rendered) = selected.Value;
        var existing = await db.Allocations.SingleOrDefaultAsync(a => a.SeriesId == series.Id && a.DocumentId == request.DocumentId, cancellationToken);
        if (existing is not null)
        {
            return Error.Conflict("numbering.already_allocated", $"Document {request.DocumentId} already holds {existing.Text} from series {series.Code}.")
                .WithWhy(("series", series.Code), ("number", existing.Text));
        }

        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleAsync<(long Number, string Text)>(
            "SELECT number, text FROM app.num_allocate(@id, @series, @period, @start, @template, @type, @document, @actor)",
            new
            {
                id = Guid.CreateVersion7(),
                series = series.Id,
                period = periodKey,
                start = series.StartNumber,
                template = rendered,
                type = series.DocumentType,
                document = request.DocumentId,
                actor = uow.Context.UserId?.Value,
            }, uow.Transaction);
        return new AllocatedNumber(series.Id, series.Code, periodKey, row.Number, row.Text, series.Gapless);
    }

    public async Task<Result<NumberPreview>> PreviewAsync(NumberRequest request, CancellationToken cancellationToken = default)
    {
        var selected = await SelectAsync(request, cancellationToken);
        if (selected.IsFailure)
        {
            return selected.Error!;
        }

        var (series, periodKey, rendered) = selected.Value;
        var next = await db.Counters.Where(c => c.SeriesId == series.Id && c.PeriodKey == periodKey).Select(static c => (long?)c.NextNumber).SingleOrDefaultAsync(cancellationToken) ?? series.StartNumber;
        return new NumberPreview(series.Id, series.Code, periodKey, next, Templates.RenderSequence(rendered, next), series.Gapless);
    }

    private async Task<Result<Selection>> SelectAsync(NumberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var documentType = SeriesService.ValidateCode(request.DocumentType, "series.document_type");
        if (documentType.IsFailure)
        {
            return documentType.Error!;
        }

        var type = documentType.Value.ToLowerInvariant();
        var company = await companies.FindAsync(request.CompanyId, cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId.Value);
        }

        BranchInfo? branch = null;
        if (request.BranchId is { } branchId)
        {
            branch = await companies.FindBranchAsync(branchId, cancellationToken);
            if (branch is null || branch.CompanyId != request.CompanyId)
            {
                return Error.NotFound("branch", branchId.Value);
            }
        }

        var candidates = await db.Series
            .Where(s => s.DocumentType == type && s.CompanyId == request.CompanyId.Value && s.IsActive)
            .ToListAsync(cancellationToken);
        candidates = candidates.Where(s => s.IsValidOn(request.Date) && (s.BranchId is null || s.BranchId == request.BranchId?.Value)).ToList();

        // The fiscal year matters for year-bound series, yearly resets and {fy}; resolve it once, lazily.
        PeriodState? period = null;
        var periodResolved = false;
        async Task<PeriodState?> PeriodAsync()
        {
            if (!periodResolved)
            {
                periodResolved = true;
                var resolved = await periods.ResolveAsync(request.CompanyId, request.Date, PostingModules.GeneralLedger, cancellationToken);
                period = resolved.IsSuccess ? resolved.Value : null;
            }

            return period;
        }

        Series? series;
        if (request.SeriesId is { } explicitId)
        {
            series = candidates.Find(s => s.Id == explicitId);
            if (series is null)
            {
                return Error.Conflict("series.not_applicable", $"Series {explicitId} does not number {type} documents of company {company.Code} on {request.Date:yyyy-MM-dd}" + (branch is null ? "." : $" for branch {branch.Code}."))
                    .WithWhy(("seriesId", explicitId), ("documentType", type), ("company", company.Code), ("date", request.Date));
            }

            if (series.FiscalYearId is { } bound && (await PeriodAsync())?.Period.FiscalYearId != bound)
            {
                return Error.Conflict("series.not_applicable", $"Series {series.Code} is bound to another fiscal year.").WithWhy(("seriesId", explicitId), ("date", request.Date));
            }
        }
        else
        {
            var scored = new List<(Series Series, int Score)>();
            foreach (var candidate in candidates)
            {
                var score = candidate.BranchId is null ? 0 : 2;
                if (candidate.FiscalYearId is { } bound)
                {
                    if ((await PeriodAsync())?.Period.FiscalYearId != bound)
                    {
                        continue;
                    }

                    score += 1;
                }

                scored.Add((candidate, score));
            }

            series = scored.OrderByDescending(static s => s.Score).ThenByDescending(static s => s.Series.IsDefault).ThenBy(static s => s.Series.Code, StringComparer.Ordinal).Select(static s => s.Series).FirstOrDefault();
            if (series is null)
            {
                return Error.Conflict("series.none_applicable", $"No numbering series numbers {type} documents of company {company.Code} on {request.Date:yyyy-MM-dd}" + (branch is null ? "." : $" for branch {branch.Code}."))
                    .WithWhy(("documentType", type), ("company", company.Code), ("branch", branch?.Code), ("date", request.Date));
            }
        }

        string? fiscalYearCode = null;
        if (series.ResetPolicy == "yearly" || Templates.Needs(series.Template, "fy"))
        {
            fiscalYearCode = (await PeriodAsync())?.Period.FiscalYearCode;
            if (fiscalYearCode is null)
            {
                return Error.Conflict("series.fiscal_year_required", $"Series {series.Code} numbers by fiscal year but no fiscal year of company {company.Code} covers {request.Date:yyyy-MM-dd}.")
                    .WithWhy(("series", series.Code), ("company", company.Code), ("date", request.Date));
            }
        }

        var periodKey = series.ResetPolicy switch
        {
            "yearly" => fiscalYearCode!,
            "monthly" => request.Date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => string.Empty,
        };

        var rendered = Templates.Render(series.Template, new TemplateContext(company.Code, branch?.Code, request.Date, fiscalYearCode));
        return rendered.IsFailure ? rendered.Error! : new Selection(series, periodKey, rendered.Value);
    }
}
