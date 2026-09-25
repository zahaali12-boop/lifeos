using Quicker.Kernel.Results;
using Quicker.Kernel.Text;

namespace Quicker.Tax.Contracts;

public static class TaxDirections
{
    public const string Sales = "sales";
    public const string Purchase = "purchase";

    public static readonly IReadOnlyList<string> All = [Sales, Purchase];
}

public static class TaxKinds
{
    public const string Vat = "vat";
    public const string Gst = "gst";
    public const string SalesTax = "sales_tax";
    public const string Excise = "excise";
    public const string Withholding = "withholding";

    public static readonly IReadOnlyList<string> All = [Vat, Gst, SalesTax, Excise, Withholding];
}

/// <summary>How a code treats what it applies to: taxed at its rate, taxed at zero, exempt (with a reason printed), or outside the tax.</summary>
public static class TaxTreatments
{
    public const string Standard = "standard";
    public const string ZeroRated = "zero_rated";
    public const string Exempt = "exempt";
    public const string OutOfScope = "out_of_scope";

    public static readonly IReadOnlyList<string> All = [Standard, ZeroRated, Exempt, OutOfScope];
}

public static class TaxRoundingLevels
{
    public const string Line = "line";
    public const string Document = "document";

    public static readonly IReadOnlyList<string> All = [Line, Document];
}

/// <summary>An item tax group (items and categories name one) or a partner tax group (customer and supplier accounts name one).</summary>
public static class TaxGroupKinds
{
    public const string Item = "item";
    public const string Partner = "partner";
}

public sealed record TaxGroupInfo(Guid Id, string Kind, string Code, LocalizedText Name, bool IsActive);

public sealed record TaxCodeRef(Guid Id, string Code, LocalizedText Name, string Treatment, string? ExemptionReasonCode, LocalizedText ExemptionReason);

/// <summary>
/// The tax groups other modules point at (so an item, a category or an account names one that exists and is of its
/// kind) and the codes their documents show.
/// </summary>
public interface ITaxDirectory
{
    Task<TaxGroupInfo?> FindGroupAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<Guid, TaxCodeRef>> DescribeCodesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
}

/// <summary>A tax code as a document line takes it, with the rate in force on the tax date.</summary>
public sealed record TaxCodeInfo(
    Guid Id,
    Guid RegimeId,
    string RegimeCode,
    string Code,
    string Kind,
    string Treatment,
    decimal RatePct,
    bool IsRecoverable,
    bool IsReverseCharge,
    string? ExemptionReasonCode,
    string OutputAccountRole,
    string InputAccountRole,
    string RoundingLevel);

/// <summary>What decides a line's tax: the company, which way the document goes, its date, the item's and the partner's tax groups and where goods ship from and to.</summary>
public sealed record TaxDeterminationRequest(
    Guid CompanyId,
    string Direction,
    DateOnly TaxDate,
    Guid? ItemTaxGroupId,
    Guid? PartnerTaxGroupId,
    Guid? PartnerId = null,
    string? ShipFromCountry = null,
    string? ShipToCountry = null);

/// <summary>Why a line has the code it has.</summary>
public static class TaxReasons
{
    /// <summary>The partner's exemption certificate in the regime was valid on the tax date.</summary>
    public const string Exemption = "exemption";

    /// <summary>The most specific row of the determination matrix matched.</summary>
    public const string Rule = "rule";

    /// <summary>The document line named its code (an override, checked against the regime).</summary>
    public const string Chosen = "chosen";

    /// <summary>The company is registered in no regime on the tax date, so it charges and recovers no tax.</summary>
    public const string NotRegistered = "not_registered";
}

/// <summary>
/// The code a line takes and why (<see cref="TaxReasons"/>): the partner's exemption certificate, the determination rule
/// that matched, the code the line chose, or none because the company is registered in no regime.
/// </summary>
public sealed record TaxDetermination(TaxCodeInfo? Code, string Reason, Guid? RuleId, string? CertificateNumber);

/// <summary>
/// A document line to tax: its amount on the document's basis, and either its item (whose tax group, the item's or its
/// category's, determines the code), an item tax group, or a code it names. Countries override the document's.
/// </summary>
public sealed record TaxDocumentLine(string Key, decimal Amount, Guid? ItemId = null, Guid? ItemTaxGroupId = null, Guid? TaxCodeId = null, string? ShipFromCountry = null, string? ShipToCountry = null);

/// <summary>
/// A whole document to tax. The partner's tax group is its customer (sales) or supplier (purchase) account's in the
/// company unless given; goods ship from the company's country on a sale and to it on a purchase unless given.
/// </summary>
public sealed record TaxDocumentRequest(
    Guid CompanyId,
    string Direction,
    DateOnly TaxDate,
    string Currency,
    bool PricesIncludeTax,
    IReadOnlyList<TaxDocumentLine> Lines,
    Guid? PartnerId = null,
    Guid? PartnerTaxGroupId = null,
    string? ShipFromCountry = null,
    string? ShipToCountry = null);

public sealed record TaxLineDetermination(string Key, string Reason, Guid? TaxCodeId, string? TaxCode, Guid? RuleId, string? CertificateNumber, Guid? ItemTaxGroupId);

/// <summary>A document taxed: its lines, per-code summary and totals, and why each line took its code. The rounding level is the regime's or the company's, whichever asks for document level (ADR-0005).</summary>
public sealed record TaxedDocumentResult(TaxedDocument Document, IReadOnlyList<TaxLineDetermination> Determinations, Guid? RegimeId, string? RegimeCode, Guid? PartnerTaxGroupId);

/// <summary>One line to tax: its amount on the document's basis (net when prices exclude tax, gross when they include it).</summary>
public sealed record TaxLineInput(string Key, decimal Amount, TaxCodeInfo? Code);

/// <summary>A line's net, tax and gross; reverse charge lines carry the tax self-assessed on both sides but add nothing to the gross.</summary>
public sealed record TaxedLine(string Key, Guid? TaxCodeId, string? TaxCode, decimal RatePct, decimal Net, decimal Tax, decimal Gross, bool IsReverseCharge, bool IsRecoverable);

/// <summary>A document's tax per code (for printing and posting) and its totals.</summary>
public sealed record TaxSummary(Guid TaxCodeId, string TaxCode, decimal RatePct, decimal Net, decimal Tax, bool IsReverseCharge);

public sealed record TaxedDocument(IReadOnlyList<TaxedLine> Lines, IReadOnlyList<TaxSummary> ByCode, decimal Net, decimal Tax, decimal Gross, string RoundingLevel, bool PricesIncludeTax);

/// <summary>One row of the tax ledger a posting document writes (see <see cref="ITaxLedger"/>).</summary>
public sealed record TaxEntryRequest(
    Guid TaxCodeId,
    string Direction,
    decimal RatePct,
    decimal BaseTc,
    decimal TaxTc,
    decimal BaseFc,
    decimal TaxFc,
    bool IsReverseCharge,
    bool IsRecoverable,
    Guid? SourceLineRef = null);

public sealed record TaxPostingRequest(
    Guid CompanyId,
    DateOnly PostingDate,
    DateOnly? DocumentDate,
    string SourceModule,
    string SourceDocumentType,
    Guid SourceDocumentId,
    string? SourceDocumentNumber,
    Guid? JournalEntryId,
    Guid? PartnerId,
    string Currency,
    IReadOnlyList<TaxEntryRequest> Entries);

/// <summary>Finds the tax code a document line takes (ADR-0018 determination) and its rate on the tax date.</summary>
public interface ITaxDetermination
{
    Task<Result<TaxDetermination>> DetermineAsync(TaxDeterminationRequest request, CancellationToken cancellationToken = default);

    /// <summary>A code by id with its rate on the date; <c>tax.code_unknown</c> when there is none.</summary>
    Task<Result<TaxCodeInfo>> FindCodeAsync(Guid taxCodeId, DateOnly taxDate, CancellationToken cancellationToken = default);

    /// <summary>Determines every line's code and calculates the document's tax, as a quote, an order or an invoice does.</summary>
    Task<Result<TaxedDocumentResult>> CalculateAsync(TaxDocumentRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// The tax ledger documents write as they post, in their own transaction: refused (<c>tax.period_filed</c>) when the
/// posting date falls in a return period already filed, so a filed return never changes under the authority's copy.
/// </summary>
public interface ITaxLedger
{
    Task<Result> RecordAsync(TaxPostingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Writes the opposite of every entry of a document on the reversal's date (itself refused in a filed period).</summary>
    Task<Result> ReverseAsync(string sourceDocumentType, Guid sourceDocumentId, DateOnly reversalDate, Guid? journalEntryId, CancellationToken cancellationToken = default);
}

/// <summary>
/// An e-invoicing clearance scheme (ZATCA, Peppol, ETA...): validates, signs and submits an invoice document and reads
/// back its status. A regime names the scheme it uses; the generic UBL 2.1 export needs none.
/// </summary>
public interface ITaxClearanceProvider
{
    string Scheme { get; }

    Task<Result<ClearanceResult>> ValidateAsync(ClearanceDocument document, CancellationToken cancellationToken = default);

    Task<Result<ClearanceResult>> SubmitAsync(ClearanceDocument document, CancellationToken cancellationToken = default);

    Task<Result<ClearanceResult>> GetStatusAsync(string submissionId, CancellationToken cancellationToken = default);
}

public sealed record ClearanceDocument(Guid DocumentId, string DocumentNumber, string Xml, string? PreviousHash);

public sealed record ClearanceResult(string Status, string? SubmissionId, string? Hash, string? QrPayload, IReadOnlyList<string> Messages);
