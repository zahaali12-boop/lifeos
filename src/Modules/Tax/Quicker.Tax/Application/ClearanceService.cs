using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Tax.Contracts;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>Finds the clearance provider registered for a regime's e-invoicing scheme (ADR-0018); a module adds an adapter with <c>services.AddScoped&lt;ITaxClearanceProvider, MyProvider&gt;()</c>.</summary>
public interface ITaxClearanceRegistry
{
    ITaxClearanceProvider? Find(string scheme);
}

public sealed class TaxClearanceRegistry(IEnumerable<ITaxClearanceProvider> providers) : ITaxClearanceRegistry
{
    private readonly IReadOnlyDictionary<string, ITaxClearanceProvider> _byScheme = providers.ToDictionary(static p => p.Scheme, StringComparer.OrdinalIgnoreCase);

    public ITaxClearanceProvider? Find(string scheme) => _byScheme.GetValueOrDefault(scheme);
}

/// <summary>
/// Submits a taxed document to its regime's clearance scheme (ADR-0018), when an adapter is registered for it: the
/// adapter receives the document's type and id with the generic UBL export, and loads the full content from the module
/// that owns the document when the scheme needs lines and parties (ZATCA does). A regime naming a scheme with no
/// adapter is refused rather than cleared.
/// </summary>
public sealed class ClearanceService(TaxDbContext db, UblExportService ubl, ITaxClearanceRegistry registry) : ITaxClearance
{
    async Task<Result<ClearanceOutcome>> ITaxClearance.SubmitAsync(string sourceDocumentType, Guid sourceDocumentId, bool automatic, CancellationToken cancellationToken)
    {
        var submitted = await SubmitAsync(sourceDocumentType, sourceDocumentId, automatic, cancellationToken);
        return submitted.IsFailure ? submitted.Error! : new ClearanceOutcome(submitted.Value.Scheme, submitted.Value.Status, submitted.Value.SubmissionId, submitted.Value.QrPayload, submitted.Value.Messages);
    }

    public Task<Result<ClearanceSubmission>> SubmitAsync(string sourceDocumentType, Guid sourceDocumentId, CancellationToken cancellationToken) => SubmitAsync(sourceDocumentType, sourceDocumentId, false, cancellationToken);

    public async Task<Result<ClearanceSubmission>> SubmitAsync(string sourceDocumentType, Guid sourceDocumentId, bool automatic, CancellationToken cancellationToken)
    {
        var exported = await ubl.ExportAsync(sourceDocumentType, sourceDocumentId, cancellationToken);
        if (exported.IsFailure)
        {
            return exported.Error!;
        }

        var entry = await db.Entries.AsNoTracking().Where(e => e.SourceDocumentType == sourceDocumentType && e.SourceDocumentId == sourceDocumentId).FirstAsync(cancellationToken);
        var regime = await db.Regimes.AsNoTracking().SingleAsync(r => r.Id == entry.RegimeId, cancellationToken);
        if (string.IsNullOrWhiteSpace(regime.EinvoicingScheme))
        {
            return Error.Validation("tax.clearance_not_required", "The regime names no e-invoicing scheme; the UBL export is the document's own record.").WithWhy(("regime", regime.Code));
        }

        var provider = registry.Find(regime.EinvoicingScheme);
        if (provider is null)
        {
            return Error.Validation("tax.clearance_not_configured", $"No clearance adapter is registered yet for the '{regime.EinvoicingScheme}' scheme.").WithWhy(("regime", regime.Code), ("scheme", regime.EinvoicingScheme));
        }

        var submitted = await provider.SubmitAsync(new ClearanceDocument(sourceDocumentType, sourceDocumentId, entry.SourceDocumentNumber ?? sourceDocumentId.ToString(), exported.Value.Xml, null, automatic), cancellationToken);
        return submitted.IsFailure ? submitted.Error! : new ClearanceSubmission(provider.Scheme, submitted.Value.Status, submitted.Value.SubmissionId, submitted.Value.QrPayload, submitted.Value.Messages);
    }
}
