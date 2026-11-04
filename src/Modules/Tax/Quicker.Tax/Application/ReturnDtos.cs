namespace Quicker.Tax.Application;

/// <summary>One figure of a return: a box's taxable base and the tax on it, in the company's functional currency.</summary>
public sealed record TaxReturnBox(string Code, decimal BaseAmount, decimal TaxAmount);

/// <summary>A tax code's ledger movement on one account role against what the tax entries say it should be; a difference means a posting outside the tax module touched the account.</summary>
public sealed record TaxCodeReconciliation(Guid TaxCodeId, string TaxCode, string AccountRole, decimal LedgerMovement, decimal EntriesMovement, decimal Difference);

/// <summary>
/// A return computed from the tax ledger for a company, regime and period (ADR-0018): the boxes it reports, reconciled
/// to the tax accounts of the general ledger, and the net amount owed (positive) or due back (negative).
/// </summary>
public sealed record TaxReturnPreview(
    Guid CompanyId,
    Guid RegimeId,
    string RegimeCode,
    IReadOnlyDictionary<string, string> RegimeName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Currency,
    IReadOnlyList<TaxReturnBox> Boxes,
    IReadOnlyList<TaxCodeReconciliation> Reconciliation,
    bool Reconciled,
    decimal NetPayable);

/// <summary>One tax entry behind a box figure, for the return's drill-down.</summary>
public sealed record TaxReturnDrillDownLine(
    Guid Id,
    string Kind,
    string Direction,
    DateOnly PostingDate,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    string? SourceDocumentNumber,
    Guid TaxCodeId,
    string TaxCode,
    decimal RatePct,
    decimal BaseFc,
    decimal TaxFc,
    bool IsReversal);

public sealed record TaxReturnPeriodSummary(
    Guid Id,
    Guid CompanyId,
    Guid RegimeId,
    string RegimeCode,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string Status,
    DateTimeOffset? FiledAt,
    Guid? FiledBy,
    string? Reference,
    decimal? NetPayable);

public sealed record FileTaxReturnRequest(Guid CompanyId, Guid RegimeId, DateOnly PeriodStart, DateOnly PeriodEnd, string? Reference = null);

/// <summary>The generic UBL 2.1 export of a taxed document's header, parties and tax summary (ADR-0018); line-level detail composes with the source document when printing is built.</summary>
public sealed record UblDocument(string FileName, string ContentType, string Xml);

public sealed record ClearanceSubmission(string Scheme, string Status, string? SubmissionId, string? QrPayload, IReadOnlyList<string> Messages);
