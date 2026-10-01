using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Tax.Contracts;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>Finds the clearance provider registered for a regime's e-invoicing scheme (ADR-0018); a module adds an adapter with <c>services.AddSingleton&lt;ITaxClearanceProvider, MyProvider&gt;()</c>.</summary>
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
/// Submits a taxed document's generic UBL export to its regime's clearance scheme (ADR-0018), when one is registered.
/// No adapter (ZATCA, Peppol, ETA) ships yet, so a regime that names a scheme is refused until one is: this wires the
/// interface end to end without claiming compliance no adapter provides.
/// </summary>
public sealed class ClearanceService(TaxDbContext db, UblExportService ubl, ITaxClearanceRegistry registry)
{
    public async Task<Result<ClearanceSubmission>> SubmitAsync(string sourceDocumentType, Guid sourceDocumentId, CancellationToken cancellationToken)
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

        var submitted = await provider.SubmitAsync(new ClearanceDocument(sourceDocumentId, entry.SourceDocumentNumber ?? sourceDocumentId.ToString(), exported.Value.Xml, null), cancellationToken);
        return submitted.IsFailure ? submitted.Error! : new ClearanceSubmission(provider.Scheme, submitted.Value.Status, submitted.Value.SubmissionId, submitted.Value.QrPayload, submitted.Value.Messages);
    }
}
