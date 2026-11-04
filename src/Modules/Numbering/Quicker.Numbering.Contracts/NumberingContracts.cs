using System.Collections.Concurrent;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;

namespace Quicker.Numbering.Contracts;

/// <summary>What a document needs to get its number: type, company, optional branch, the posting date and its own id.</summary>
public sealed record NumberRequest(string DocumentType, CompanyId CompanyId, BranchId? BranchId, DateOnly Date, Guid DocumentId, Guid? SeriesId = null);

/// <summary>An allocated number: the series that issued it, its sequence within the reset period, and the rendered text.</summary>
public sealed record AllocatedNumber(Guid SeriesId, string SeriesCode, string PeriodKey, long Number, string Text, bool Gapless);

/// <summary>The series a request would use and the text its next number would have (no lock, no allocation).</summary>
public sealed record NumberPreview(Guid SeriesId, string SeriesCode, string PeriodKey, long NextNumber, string Text, bool Gapless);

public interface INumberAllocator
{
    /// <summary>
    /// Takes the next number of the matching series under its row lock, inside the caller's unit of work (ADR-0016):
    /// a rolled-back posting rolls the number back too. Gapless series must be called from the posting transaction;
    /// non-gapless ones may be called at creation.
    /// </summary>
    Task<Result<AllocatedNumber>> AllocateAsync(NumberRequest request, CancellationToken cancellationToken = default);

    Task<Result<NumberPreview>> PreviewAsync(NumberRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The id of an active series for the document type and company, creating a default one (gapless, the given
    /// template and reset policy) when the company has none yet, so system documents such as journal entries can be
    /// numbered on day one; administrators refine the series afterwards. Series codes are unique in the tenant, so the
    /// company code is appended to <paramref name="code"/> when the caller has not already done so (GRN becomes GRN-IQT).
    /// </summary>
    Task<Result<Guid>> EnsureDefaultSeriesAsync(string documentType, CompanyId companyId, string code, string template, string resetPolicy = "yearly", CancellationToken cancellationToken = default);
}

/// <summary>
/// The numbers issued to documents, read by other modules that show a document by its number without owning it (a
/// journal entry's source document). Read-only; a document without an issued number is absent.
/// </summary>
public interface IIssuedNumbers
{
    /// <summary>The number issued to each of the documents (the latest, should one have been renumbered).</summary>
    Task<IReadOnlyDictionary<Guid, string>> NumbersOfAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default);

    /// <summary>The documents whose number starts with the prefix (case-insensitive), at most <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<Guid>> DocumentsNumberedAsync(string prefix, int limit = 500, CancellationToken cancellationToken = default);
}

/// <summary>Drafts and unposted documents show this instead of a legal number and are never printed as invoices.</summary>
public static class DraftIdentifiers
{
    public const string Prefix = "DRAFT-";

    public static string For(Guid documentId) => Prefix + documentId.ToString("N")[..8].ToUpperInvariant();

    public static bool IsDraft(string? number) => number is not null && number.StartsWith(Prefix, StringComparison.Ordinal);
}

/// <summary>A numbered document type and the permission that lets a member read the document.</summary>
public sealed record NumberedDocumentType(string DocumentType, string ReadPermission);

/// <summary>
/// The document types modules number through this module, with their read permissions. The document search
/// (GET /numbering/documents) lists a number only for the types registered here whose permission the member holds,
/// so a number is never shown to someone who could not open its document; an unregistered type is never listed.
/// </summary>
public static class NumberedDocumentTypes
{
    private static readonly ConcurrentDictionary<string, NumberedDocumentType> Registry = new(StringComparer.Ordinal);

    public static void Register(params NumberedDocumentType[] types)
    {
        ArgumentNullException.ThrowIfNull(types);
        foreach (var type in types)
        {
            Registry[type.DocumentType] = type;
        }
    }

    public static IReadOnlyCollection<NumberedDocumentType> All => Registry.Values.OrderBy(static t => t.DocumentType, StringComparer.Ordinal).ToList();
}
