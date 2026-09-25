using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Persistence;
using Quicker.Tax.Contracts;
using Quicker.Tax.Domain;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>
/// The tax ledger (ADR-0018): documents write their tax here as they post, in their own transaction, and the return is
/// computed from it. A write dated in a filed return period is refused. Each write share-locks the company's
/// registration in the regime, which filing locks exclusively, so a return is never filed while a posting into its
/// period is still in flight, nor does a posting slip in behind a filing.
/// </summary>
public sealed class TaxLedgerService(TaxDbContext db, IUnitOfWorkAccessor unitOfWork, ICompanyDirectory companies, IClock clock) : ITaxLedger
{
    public async Task<Result> RecordAsync(TaxPostingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entries = request.Entries ?? [];
        if (entries.Count == 0)
        {
            return Result.Success();
        }

        if (string.IsNullOrWhiteSpace(request.SourceModule) || string.IsNullOrWhiteSpace(request.SourceDocumentType))
        {
            return Error.Validation("tax.source_required", "A tax entry names the document that wrote it.");
        }

        if (entries.Any(static e => e.Direction is not (TaxDirections.Sales or TaxDirections.Purchase)))
        {
            return Error.Validation("tax.direction_invalid", "A tax entry is a sale's or a purchase's.");
        }

        if (await companies.FindCurrencyAsync(request.Currency ?? string.Empty, cancellationToken) is null)
        {
            return Error.Validation("tax.currency_unknown", "The currency is not known.").WithWhy(("currency", request.Currency));
        }

        var codeIds = entries.Select(static e => e.TaxCodeId).Distinct().ToArray();
        var codes = await db.Codes.AsNoTracking().Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        if (codes.Count != codeIds.Length)
        {
            return Error.Validation("tax.code_unknown", "A tax entry names a tax code that does not exist.").WithWhy(("taxCodeIds", codeIds.Where(id => !codes.ContainsKey(id)).ToArray()));
        }

        if (await db.Entries.AnyAsync(e => e.SourceDocumentType == request.SourceDocumentType && e.SourceDocumentId == request.SourceDocumentId, cancellationToken))
        {
            return Error.Conflict("tax.document_recorded", "The document's tax is already in the tax ledger.").WithWhy(("document", request.SourceDocumentNumber ?? request.SourceDocumentId.ToString()));
        }

        foreach (var regimeId in codes.Values.Select(static c => c.RegimeId).Distinct().Order())
        {
            var open = await EnsureOpenAsync(request.CompanyId, regimeId, request.PostingDate, cancellationToken);
            if (open.IsFailure)
            {
                return open;
            }
        }

        var now = clock.UtcNow;
        foreach (var e in entries)
        {
            db.Entries.Add(new TaxEntry
            {
                Id = Guid.CreateVersion7(),
                CompanyId = request.CompanyId,
                RegimeId = codes[e.TaxCodeId].RegimeId,
                TaxCodeId = e.TaxCodeId,
                Direction = e.Direction,
                PostingDate = request.PostingDate,
                DocumentDate = request.DocumentDate,
                SourceModule = request.SourceModule,
                SourceDocumentType = request.SourceDocumentType,
                SourceDocumentId = request.SourceDocumentId,
                SourceDocumentNumber = request.SourceDocumentNumber,
                SourceLineRef = e.SourceLineRef,
                JournalEntryId = request.JournalEntryId,
                PartnerId = request.PartnerId,
                Currency = request.Currency!.ToUpperInvariant(),
                RatePct = e.RatePct,
                BaseTc = e.BaseTc,
                TaxTc = e.TaxTc,
                BaseFc = e.BaseFc,
                TaxFc = e.TaxFc,
                IsReverseCharge = e.IsReverseCharge,
                IsRecoverable = e.IsRecoverable,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> ReverseAsync(string sourceDocumentType, Guid sourceDocumentId, DateOnly reversalDate, Guid? journalEntryId, CancellationToken cancellationToken = default)
    {
        var entries = await db.Entries.AsNoTracking().Where(e => e.SourceDocumentType == sourceDocumentType && e.SourceDocumentId == sourceDocumentId).ToListAsync(cancellationToken);
        var reversed = entries.Where(static e => e.ReversesEntryId is not null).Select(static e => e.ReversesEntryId!.Value).ToHashSet();
        var originals = entries.Where(e => e.ReversesEntryId is null && !reversed.Contains(e.Id)).ToList();
        if (originals.Count == 0)
        {
            // A document without tax lines reverses nothing; one already reversed is not reversed twice.
            return entries.Count == 0 ? Result.Success() : Error.Conflict("tax.document_reversed", "The document's tax has already been reversed.");
        }

        foreach (var group in originals.Select(static e => (e.CompanyId, e.RegimeId)).Distinct().Order())
        {
            var open = await EnsureOpenAsync(group.CompanyId, group.RegimeId, reversalDate, cancellationToken);
            if (open.IsFailure)
            {
                return open;
            }
        }

        var now = clock.UtcNow;
        foreach (var e in originals)
        {
            db.Entries.Add(new TaxEntry
            {
                Id = Guid.CreateVersion7(),
                CompanyId = e.CompanyId,
                RegimeId = e.RegimeId,
                TaxCodeId = e.TaxCodeId,
                Direction = e.Direction,
                PostingDate = reversalDate,
                DocumentDate = e.DocumentDate,
                SourceModule = e.SourceModule,
                SourceDocumentType = e.SourceDocumentType,
                SourceDocumentId = e.SourceDocumentId,
                SourceDocumentNumber = e.SourceDocumentNumber,
                SourceLineRef = e.SourceLineRef,
                JournalEntryId = journalEntryId,
                PartnerId = e.PartnerId,
                Currency = e.Currency,
                RatePct = e.RatePct,
                BaseTc = -e.BaseTc,
                TaxTc = -e.TaxTc,
                BaseFc = -e.BaseFc,
                TaxFc = -e.TaxFc,
                IsReverseCharge = e.IsReverseCharge,
                IsRecoverable = e.IsRecoverable,
                ReversesEntryId = e.Id,
                CreatedAt = now,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Share-locks the company's registration in the regime and refuses a date in a filed return period.</summary>
    private async Task<Result> EnsureOpenAsync(Guid companyId, Guid regimeId, DateOnly date, CancellationToken cancellationToken)
    {
        var uow = unitOfWork.Current;
        var registered = await uow.Connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT id FROM app.tax_registrations WHERE tenant_id = @tenant AND company_id = @company AND regime_id = @regime FOR SHARE",
            new { tenant = uow.Context.TenantId.Value, company = companyId, regime = regimeId },
            uow.Transaction,
            cancellationToken: cancellationToken));
        if (registered is null)
        {
            return Error.Validation("tax.company_not_registered", "The company is not registered in the regime of the tax code.").WithWhy(("companyId", companyId), ("regimeId", regimeId));
        }

        var filed = await db.ReturnPeriods.AsNoTracking()
            .Where(p => p.CompanyId == companyId && p.RegimeId == regimeId && p.Status == "filed" && p.PeriodStart <= date && p.PeriodEnd >= date)
            .Select(static p => new { p.PeriodStart, p.PeriodEnd, p.Reference })
            .FirstOrDefaultAsync(cancellationToken);
        return filed is null
            ? Result.Success()
            : Error.Conflict("tax.period_filed", "The tax return for that date is filed; post the correction in an open period.")
                .WithWhy(("date", date), ("periodStart", filed.PeriodStart), ("periodEnd", filed.PeriodEnd), ("reference", filed.Reference));
    }
}
