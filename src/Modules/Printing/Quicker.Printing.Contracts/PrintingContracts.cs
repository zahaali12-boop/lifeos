using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Printing.Contracts;

/// <summary>What is printed: a document by its type and id, or a report by its type and parameters (a customer statement by company, customer and period).</summary>
public sealed record PrintSubject(string DocumentType, Guid? DocumentId, IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>The variants a document type prints as: the tax invoice and the pro forma of an invoice, say.</summary>
public sealed record PrintVariant(string Code, LocalizedText Name);

public static class PrintVariants
{
    public const string Standard = "standard";
    public const string Proforma = "proforma";
}

/// <summary>
/// The data contract of a print (ADR-0022): what the document type exposes to its templates, built fresh from the
/// document each time. <see cref="Data"/> holds strings, numbers, booleans, <see cref="LocalizedText"/> (resolved to
/// the template's language mode by the printing module), nested dictionaries and lists of those; the printing module
/// adds the company's and the partner's blocks, the labels and the formatting. <see cref="IsFinal"/> says the
/// document can no longer change (posted), so its first render is kept as evidence.
/// </summary>
public sealed record PrintModel(
    Guid CompanyId,
    Guid? BranchId,
    LocalizedText Title,
    string Number,
    string Status,
    bool IsFinal,
    DateOnly DocumentDate,
    string Currency,
    IReadOnlyDictionary<string, object?> Data,
    Guid? PartnerId = null,
    string? PartnerEmail = null,
    string? FileName = null);

/// <summary>A document type that prints: implemented by the module that owns the document; discovered by type.</summary>
public interface IPrintModelProvider
{
    string DocumentType { get; }

    IReadOnlyList<PrintVariant> Variants { get; }

    /// <summary>The print model, or why the subject cannot print (unknown, not allowed to the caller, a draft asked for as a tax invoice).</summary>
    Task<Result<PrintModel>> BuildAsync(PrintSubject subject, string variant, CancellationToken cancellationToken = default);
}

/// <summary>Problem codes shared by the providers.</summary>
public static class PrintErrors
{
    public const string DraftNotTaxInvoice = "print.draft_not_tax_invoice";
    public const string DocumentCancelled = "print.document_cancelled";
    public const string ParameterRequired = "print.parameter_required";
}
