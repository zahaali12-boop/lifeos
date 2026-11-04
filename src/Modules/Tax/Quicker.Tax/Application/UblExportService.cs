using System.Globalization;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Tax.Contracts;
using Quicker.Tax.Domain;
using Quicker.Tax.Persistence;

namespace Quicker.Tax.Application;

/// <summary>
/// The generic UBL 2.1 Invoice export every tenant gets (ADR-0018): built from a document's tax entries, so it exists
/// for any taxed document whatever module posted it, with no clearance adapter required. It carries the header, the
/// two parties and the tax summary by code; line-level detail composes with the source document's own lines when
/// printing is built (roadmap 5.9). A document with nothing in the tax ledger (never taxed, or the company was not
/// registered when it posted) has nothing to export.
/// </summary>
public sealed class UblExportService(TaxDbContext db, ICompanyDirectory companies, IPartnerDirectory partners)
{
    private static readonly XNamespace Inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public async Task<Result<UblDocument>> ExportAsync(string sourceDocumentType, Guid sourceDocumentId, CancellationToken cancellationToken)
    {
        var live = await LiveEntriesAsync(sourceDocumentType, sourceDocumentId, cancellationToken);
        if (live.Count == 0)
        {
            return Error.Validation("tax.document_not_taxed", "The document has no tax entries to export: it was never taxed, or the company was not registered when it posted.")
                .WithWhy(("sourceDocumentType", sourceDocumentType), ("sourceDocumentId", sourceDocumentId));
        }

        var first = live[0];
        if (await companies.FindAsync(new CompanyId(first.CompanyId), cancellationToken) is not { } company)
        {
            return Error.Validation("tax.company_unknown", "The company does not exist.").WithWhy(("companyId", first.CompanyId));
        }

        var regime = await db.Regimes.AsNoTracking().SingleAsync(r => r.Id == first.RegimeId, cancellationToken);
        var registration = await db.Registrations.AsNoTracking().FirstOrDefaultAsync(r => r.CompanyId == first.CompanyId && r.RegimeId == first.RegimeId, cancellationToken);
        var codeIds = live.Select(static l => l.TaxCodeId).Distinct().ToArray();
        var codes = await db.Codes.AsNoTracking().Where(c => codeIds.Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        var partner = first.PartnerId is { } partnerId ? await partners.FindAsync(partnerId, cancellationToken) : null;

        var xml = Build(first, company, registration?.RegistrationNumber, regime.Family, partner, live, codes);
        return new UblDocument($"{Sanitize(first.SourceDocumentNumber ?? first.SourceDocumentId.ToString())}.xml", "application/xml", xml);
    }

    private async Task<List<TaxEntry>> LiveEntriesAsync(string sourceDocumentType, Guid sourceDocumentId, CancellationToken cancellationToken)
    {
        var entries = await db.Entries.AsNoTracking().Where(e => e.SourceDocumentType == sourceDocumentType && e.SourceDocumentId == sourceDocumentId).ToListAsync(cancellationToken);
        var reversed = entries.Where(static e => e.ReversesEntryId is not null).Select(static e => e.ReversesEntryId!.Value).ToHashSet();
        return entries.Where(e => e.ReversesEntryId is null && !reversed.Contains(e.Id)).ToList();
    }

    private static string Build(TaxEntry first, CompanyInfo company, string? companyRegistrationNumber, string regimeFamily, PartnerInfo? partner, IReadOnlyList<TaxEntry> live, IReadOnlyDictionary<Guid, TaxCode> codes)
    {
        var totalBase = live.Sum(static l => l.BaseTc);
        var totalTax = live.Sum(static l => l.TaxTc);
        var invoiceTypeCode = totalBase < 0m || totalTax < 0m ? "381" : "380"; // UN/CEFACT 380 invoice, 381 credit note

        var isSale = first.Direction == TaxDirections.Sales;
        var companyParty = Party(company.Code, NameOf(company.LegalName), isSale ? companyRegistrationNumber : null);
        var partnerParty = Party(partner?.Code ?? "unknown", partner is null ? "Unknown party" : NameOf(partner.LegalName), null);

        var byCode = live.GroupBy(static l => l.TaxCodeId)
            .Select(g => (Code: codes[g.Key], Rate: g.First().RatePct, Base: g.Sum(static l => l.BaseTc), Tax: g.Sum(static l => l.TaxTc)))
            .OrderBy(static x => x.Code.Code, StringComparer.Ordinal)
            .ToList();

        var invoice = new XElement(
            Inv + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cac", Cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc.NamespaceName),
            new XElement(Cbc + "UBLVersionID", "2.1"),
            new XElement(Cbc + "ID", first.SourceDocumentNumber ?? first.SourceDocumentId.ToString()),
            new XElement(Cbc + "IssueDate", (first.DocumentDate ?? first.PostingDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "InvoiceTypeCode", invoiceTypeCode),
            new XElement(Cbc + "DocumentCurrencyCode", first.Currency),
            new XElement(Cac + "AccountingSupplierParty", new XElement(Cac + "Party", isSale ? companyParty : partnerParty)),
            new XElement(Cac + "AccountingCustomerParty", new XElement(Cac + "Party", isSale ? partnerParty : companyParty)),
            new XElement(
                Cac + "TaxTotal",
                Amount(Cbc + "TaxAmount", first.Currency, Math.Abs(totalTax)),
                byCode.Select(x => new XElement(
                    Cac + "TaxSubtotal",
                    Amount(Cbc + "TaxableAmount", first.Currency, Math.Abs(x.Base)),
                    Amount(Cbc + "TaxAmount", first.Currency, Math.Abs(x.Tax)),
                    new XElement(
                        Cac + "TaxCategory",
                        new XElement(Cbc + "ID", x.Code.Code),
                        new XElement(Cbc + "Percent", x.Rate.ToString("0.####", CultureInfo.InvariantCulture)),
                        x.Code.ExemptionReasonCode is { } reason ? new XElement(Cbc + "TaxExemptionReasonCode", reason) : null,
                        new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", regimeFamily.ToUpperInvariant()))))).ToList()),
            new XElement(
                Cac + "LegalMonetaryTotal",
                Amount(Cbc + "TaxExclusiveAmount", first.Currency, totalBase),
                Amount(Cbc + "TaxInclusiveAmount", first.Currency, totalBase + totalTax),
                Amount(Cbc + "PayableAmount", first.Currency, totalBase + totalTax)));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), invoice).ToString();
    }

    private static XElement Party(string code, string name, string? registrationNumber) =>
        new(
            Cac + "PartyLegalEntity",
            new XElement(Cbc + "RegistrationName", name),
            new XElement(Cbc + "CompanyID", code),
            registrationNumber is null ? null : new XElement(Cac + "PartyTaxScheme", new XElement(Cbc + "CompanyID", registrationNumber)));

    private static XElement Amount(XName name, string currency, decimal value) => new(name, new XAttribute("currencyID", currency), value.ToString("0.00", CultureInfo.InvariantCulture));

    private static string NameOf(LocalizedText name) => name.Get("en") ?? name.Values.Values.FirstOrDefault() ?? string.Empty;

    private static string Sanitize(string value) => new(value.Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
