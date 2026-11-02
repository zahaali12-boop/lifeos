using Quicker.Kernel.Results;

namespace Quicker.Reporting.Contracts;

/// <summary>
/// One line of a sales invoice or credit note with everything it can be grouped or filtered by (founder programme
/// item 1f: group by customer, item, rep, month and some thirty more): the document, its customer and their set-up,
/// the item and its catalogue, the tax, the dimensions, and the amounts. A credit note's quantities and amounts are
/// negative, so sums net out. Names are in both languages as the records hold them.
/// </summary>
public sealed record SalesLineRow(
    Guid InvoiceId,
    string InvoiceNumber,
    string Kind,
    string Status,
    DateOnly DocumentDate,
    DateOnly PostingDate,
    DateOnly? DueDate,
    string Year,
    string Quarter,
    string Month,
    Guid CustomerId,
    string CustomerCode,
    IReadOnlyDictionary<string, string> CustomerName,
    string? CustomerGroup,
    string? SalesRep,
    IReadOnlyDictionary<string, string>? SalesRepName,
    string? Branch,
    string? PaymentTerms,
    string? PriceList,
    string Currency,
    decimal ExchangeRate,
    Guid LineId,
    int LineNo,
    string LineKind,
    Guid? ItemId,
    string? ItemCode,
    IReadOnlyDictionary<string, string>? ItemName,
    string? ItemType,
    string? Category,
    string? Brand,
    string? Uom,
    string? Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal DiscountPct,
    decimal NetAmount,
    decimal TaxAmount,
    decimal GrossAmount,
    decimal NetAmountFc,
    string? TaxCode,
    decimal TaxRatePct,
    string? CostCentre,
    string? Department,
    string? Project);

/// <summary>The lines asked for, in posting order; <see cref="Truncated"/> says the cap was hit and a narrower range is needed.</summary>
public sealed record SalesLinesReport(Guid CompanyId, string FunctionalCurrency, DateOnly From, DateOnly To, IReadOnlyList<SalesLineRow> Rows, bool Truncated);

/// <summary>The posted sales lines of a period for another module's screen (the trader's health, ADR-0035 §8), with the caller's permission to read invoices checked.</summary>
public interface ISalesLines
{
    Task<Result<SalesLinesReport>> PostedAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
