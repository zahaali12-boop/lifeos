using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Quicker.Einvoicing.Contracts;

namespace Quicker.Einvoicing.Zatca;

/// <summary>Facts of one submission the document itself does not carry: its UUID, the device's counter and the previous hash, and whether it is simplified.</summary>
public sealed record SubmissionFacts(Guid Uuid, long InvoiceCounter, string PreviousHash, bool Simplified);

/// <summary>
/// The UBL 2.1 invoice in ZATCA's profile (A-179): the document's parties, lines and totals as <see cref="EinvoiceDocument"/>
/// gives them, the type code with the standard-or-simplified flags, the counter and previous hash references, the
/// billing reference of a credit or debit note, the two tax totals and the monetary totals. A simplified invoice is
/// written with the room its signature, extensions and QR take, so the hash computed on it survives their filling in.
/// </summary>
public static class ZatcaInvoiceXml
{
    private static readonly XNamespace Inv = ZatcaCrypto.Inv;
    private static readonly XNamespace Cac = ZatcaCrypto.Cac;
    private static readonly XNamespace Cbc = ZatcaCrypto.Cbc;
    private static readonly XNamespace Ext = ZatcaCrypto.Ext;

    public static string TypeCodeOf(string kind) => kind switch
    {
        EinvoiceDocumentKinds.CreditNote => "381",
        EinvoiceDocumentKinds.DebitNote => "383",
        _ => "388",
    };

    public static string Build(EinvoiceDocument document, SubmissionFacts facts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(facts);
        var currency = document.Currency;
        var typeCode = TypeCodeOf(document.Kind);
        var export = document.Buyer is { } b && !string.Equals(b.Country, document.Seller.Country, StringComparison.OrdinalIgnoreCase);
        var typeName = (facts.Simplified ? "02" : "01") + "00" + (export ? "1" : "0") + "00";

        var root = new XElement(
            Inv + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cac", Cac.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "ext", Ext.NamespaceName));
        if (facts.Simplified)
        {
            root.Add(new XElement(Ext + "UBLExtensions"));
        }

        root.Add(
            new XElement(Cbc + "ProfileID", "reporting:1.0"),
            new XElement(Cbc + "ID", document.Number),
            new XElement(Cbc + "UUID", facts.Uuid.ToString()),
            new XElement(Cbc + "IssueDate", document.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "IssueTime", document.IssuedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)),
            new XElement(Cbc + "InvoiceTypeCode", new XAttribute("name", typeName), typeCode));
        if (!string.IsNullOrWhiteSpace(document.Note))
        {
            root.Add(new XElement(Cbc + "Note", new XAttribute("languageID", "ar"), document.Note));
        }

        root.Add(
            new XElement(Cbc + "DocumentCurrencyCode", currency),
            new XElement(Cbc + "TaxCurrencyCode", currency));
        if (typeCode != "388" && !string.IsNullOrWhiteSpace(document.OriginalDocumentNumber))
        {
            root.Add(new XElement(Cac + "BillingReference", new XElement(Cac + "InvoiceDocumentReference", new XElement(Cbc + "ID", document.OriginalDocumentNumber))));
        }

        root.Add(
            new XElement(Cac + "AdditionalDocumentReference", new XElement(Cbc + "ID", "ICV"), new XElement(Cbc + "UUID", facts.InvoiceCounter.ToString(CultureInfo.InvariantCulture))),
            new XElement(Cac + "AdditionalDocumentReference", new XElement(Cbc + "ID", "PIH"), new XElement(Cac + "Attachment", new XElement(Cbc + "EmbeddedDocumentBinaryObject", new XAttribute("mimeCode", "text/plain"), facts.PreviousHash))));
        if (facts.Simplified)
        {
            root.Add(
                new XElement(Cac + "AdditionalDocumentReference", new XElement(Cbc + "ID", "QR"), new XElement(Cac + "Attachment", new XElement(Cbc + "EmbeddedDocumentBinaryObject", new XAttribute("mimeCode", "text/plain"), ZatcaCrypto.QrPlaceholder))),
                new XElement(Cac + "Signature", new XElement(Cbc + "ID", "urn:oasis:names:specification:ubl:signature:Invoice"), new XElement(Cbc + "SignatureMethod", "urn:oasis:names:specification:ubl:dsig:enveloped:xades")));
        }

        root.Add(new XElement(Cac + "AccountingSupplierParty", Party(document.Seller, true)));
        root.Add(new XElement(Cac + "AccountingCustomerParty", document.Buyer is { } buyer ? Party(buyer, false) : new XElement(Cac + "Party", new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", "Cash customer")))));
        if (!facts.Simplified || document.DeliveryDate is not null)
        {
            root.Add(new XElement(Cac + "Delivery", new XElement(Cbc + "ActualDeliveryDate", (document.DeliveryDate ?? document.IssueDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))));
        }

        var paymentMeans = new XElement(Cac + "PaymentMeans", new XElement(Cbc + "PaymentMeansCode", document.PaymentMeansCode));
        if (typeCode != "388")
        {
            paymentMeans.Add(new XElement(Cbc + "InstructionNote", string.IsNullOrWhiteSpace(document.Reason) ? (typeCode == "381" ? "Credit note" : "Debit note") : document.Reason));
        }

        root.Add(paymentMeans);

        var byCategory = document.Lines
            .GroupBy(l => (l.TaxCategory, l.TaxRatePct, l.ExemptionReasonCode, l.ExemptionReason))
            .OrderBy(static g => g.Key.TaxCategory, StringComparer.Ordinal).ThenByDescending(static g => g.Key.TaxRatePct)
            .ToList();
        root.Add(new XElement(Cac + "TaxTotal", Amount(Cbc + "TaxAmount", currency, document.TotalTax)));
        root.Add(new XElement(
            Cac + "TaxTotal",
            Amount(Cbc + "TaxAmount", currency, document.TotalTax),
            byCategory.Select(g => new XElement(
                Cac + "TaxSubtotal",
                Amount(Cbc + "TaxableAmount", currency, g.Sum(static l => l.Net)),
                Amount(Cbc + "TaxAmount", currency, g.Sum(static l => l.TaxAmount)),
                new XElement(
                    Cac + "TaxCategory",
                    new XElement(Cbc + "ID", g.Key.TaxCategory),
                    new XElement(Cbc + "Percent", g.Key.TaxRatePct.ToString("0.00", CultureInfo.InvariantCulture)),
                    g.Key.TaxCategory is EinvoiceTaxCategories.Exempt or EinvoiceTaxCategories.ZeroRated or EinvoiceTaxCategories.OutOfScope && g.Key.ExemptionReasonCode is { } code ? new XElement(Cbc + "TaxExemptionReasonCode", code) : null,
                    g.Key.TaxCategory is EinvoiceTaxCategories.Exempt or EinvoiceTaxCategories.ZeroRated or EinvoiceTaxCategories.OutOfScope && g.Key.ExemptionReason is { } reason ? new XElement(Cbc + "TaxExemptionReason", reason) : null,
                    new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")))))));
        var lineTotal = document.Lines.Sum(static l => l.Net);
        root.Add(new XElement(
            Cac + "LegalMonetaryTotal",
            Amount(Cbc + "LineExtensionAmount", currency, lineTotal),
            Amount(Cbc + "TaxExclusiveAmount", currency, document.TotalNet),
            Amount(Cbc + "TaxInclusiveAmount", currency, document.TotalGross),
            Amount(Cbc + "AllowanceTotalAmount", currency, Math.Max(0m, lineTotal - document.TotalNet)),
            Amount(Cbc + "PrepaidAmount", currency, 0m),
            Amount(Cbc + "PayableAmount", currency, document.TotalGross)));
        foreach (var line in document.Lines)
        {
            var unitPrice = line.Quantity == 0m ? 0m : line.Net / line.Quantity;
            root.Add(new XElement(
                Cac + "InvoiceLine",
                new XElement(Cbc + "ID", line.Number.ToString(CultureInfo.InvariantCulture)),
                new XElement(Cbc + "InvoicedQuantity", new XAttribute("unitCode", line.UnitCode), line.Quantity.ToString("0.######", CultureInfo.InvariantCulture)),
                Amount(Cbc + "LineExtensionAmount", currency, line.Net),
                new XElement(Cac + "TaxTotal", Amount(Cbc + "TaxAmount", currency, line.TaxAmount), Amount(Cbc + "RoundingAmount", currency, line.Net + line.TaxAmount)),
                new XElement(
                    Cac + "Item",
                    new XElement(Cbc + "Name", line.Name),
                    new XElement(
                        Cac + "ClassifiedTaxCategory",
                        new XElement(Cbc + "ID", line.TaxCategory),
                        new XElement(Cbc + "Percent", line.TaxRatePct.ToString("0.00", CultureInfo.InvariantCulture)),
                        new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")))),
                new XElement(
                    Cac + "Price",
                    new XElement(Cbc + "PriceAmount", new XAttribute("currencyID", currency), unitPrice.ToString("0.00####", CultureInfo.InvariantCulture)),
                    line.Discount > 0m
                        ? new XElement(Cac + "AllowanceCharge", new XElement(Cbc + "ChargeIndicator", "false"), new XElement(Cbc + "AllowanceChargeReason", "discount"), Amount(Cbc + "Amount", currency, line.Discount), Amount(Cbc + "BaseAmount", currency, line.Net + line.Discount))
                        : null)));
        }

        return Serialize(new XDocument(new XDeclaration("1.0", "UTF-8", null), root));
    }

    private static XElement Party(EinvoiceParty party, bool seller)
    {
        var element = new XElement(Cac + "Party");
        if (!string.IsNullOrWhiteSpace(party.RegistrationNumber))
        {
            element.Add(new XElement(Cac + "PartyIdentification", new XElement(Cbc + "ID", new XAttribute("schemeID", party.RegistrationScheme ?? "CRN"), party.RegistrationNumber)));
        }

        var address = new XElement(Cac + "PostalAddress");
        Add(address, Cbc + "StreetName", party.Street);
        Add(address, Cbc + "BuildingNumber", party.BuildingNumber);
        Add(address, Cbc + "PlotIdentification", party.Plot);
        Add(address, Cbc + "CitySubdivisionName", party.District);
        Add(address, Cbc + "CityName", party.City);
        Add(address, Cbc + "PostalZone", party.PostalCode);
        address.Add(new XElement(Cac + "Country", new XElement(Cbc + "IdentificationCode", party.Country)));
        element.Add(address);
        if (!string.IsNullOrWhiteSpace(party.VatNumber))
        {
            element.Add(new XElement(Cac + "PartyTaxScheme", new XElement(Cbc + "CompanyID", party.VatNumber), new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT"))));
        }
        else if (seller)
        {
            element.Add(new XElement(Cac + "PartyTaxScheme", new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT"))));
        }

        element.Add(new XElement(Cac + "PartyLegalEntity", new XElement(Cbc + "RegistrationName", party.Name)));
        return element;
    }

    private static void Add(XElement parent, XName name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent.Add(new XElement(name, value));
        }
    }

    private static XElement Amount(XName name, string currency, decimal value) => new(name, new XAttribute("currencyID", currency), value.ToString("0.00", CultureInfo.InvariantCulture));

    /// <summary>Four-space indentation and LF line ends, the layout the scheme's samples use; the empty extensions element renders as the placeholder the signer fills.</summary>
    private static string Serialize(XDocument document)
    {
        var settings = new XmlWriterSettings { Indent = true, IndentChars = "    ", NewLineChars = "\n", NewLineHandling = NewLineHandling.Replace, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, settings))
        {
            document.Save(writer);
        }

        return builder.ToString().Replace("<?xml version=\"1.0\" encoding=\"utf-16\"?>", "<?xml version=\"1.0\" encoding=\"UTF-8\"?>", StringComparison.Ordinal);
    }
}
