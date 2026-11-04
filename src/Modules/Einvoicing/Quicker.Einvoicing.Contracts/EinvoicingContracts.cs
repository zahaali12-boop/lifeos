using Quicker.Kernel.Results;

namespace Quicker.Einvoicing.Contracts;

/// <summary>The kinds of document an e-invoicing scheme clears or reports.</summary>
public static class EinvoiceDocumentKinds
{
    public const string Invoice = "invoice";
    public const string CreditNote = "credit_note";
    public const string DebitNote = "debit_note";
}

/// <summary>How a scheme classifies a line's tax: standard rated, zero rated, exempt (with the reason) or out of scope.</summary>
public static class EinvoiceTaxCategories
{
    public const string Standard = "S";
    public const string ZeroRated = "Z";
    public const string Exempt = "E";
    public const string OutOfScope = "O";
}

/// <summary>A party on an e-invoice: the seller or the buyer with the identifiers and the address a scheme prints.</summary>
public sealed record EinvoiceParty(
    string Name,
    string? VatNumber,
    string? RegistrationNumber,
    string? RegistrationScheme,
    string? Street,
    string? BuildingNumber,
    string? Plot,
    string? District,
    string? City,
    string? PostalCode,
    string Country);

/// <summary>One line as the scheme wants it: quantity and unit, the price before discount, the discount, the net, and the tax category with its rate and amount.</summary>
public sealed record EinvoiceLine(
    int Number,
    string Name,
    decimal Quantity,
    string UnitCode,
    decimal UnitPrice,
    decimal Discount,
    decimal Net,
    string TaxCategory,
    decimal TaxRatePct,
    decimal TaxAmount,
    string? ExemptionReasonCode,
    string? ExemptionReason);

/// <summary>The document another module hands to the scheme adapter: everything a UBL e-invoice carries, in the document's own currency.</summary>
public sealed record EinvoiceDocument(
    string DocumentType,
    Guid DocumentId,
    Guid CompanyId,
    string Number,
    string Kind,
    DateOnly IssueDate,
    DateTimeOffset IssuedAt,
    string Currency,
    EinvoiceParty Seller,
    EinvoiceParty? Buyer,
    IReadOnlyList<EinvoiceLine> Lines,
    decimal TotalNet,
    decimal TotalTax,
    decimal TotalGross,
    string? Note,
    string? OriginalDocumentNumber,
    DateOnly? OriginalDocumentDate,
    string? Reason,
    DateOnly? DeliveryDate,
    string PaymentMeansCode = "30");

/// <summary>
/// Gives an e-invoicing adapter the full content of a posted document (parties, lines, totals), since the generic
/// tax export carries only the header and the tax summary. A module registers one per document type it clears.
/// </summary>
public interface IEinvoiceSource
{
    string DocumentType { get; }

    Task<Result<EinvoiceDocument>> LoadAsync(Guid documentId, CancellationToken cancellationToken = default);
}
