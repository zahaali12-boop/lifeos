using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;
using Quicker.Einvoicing.Contracts;
using Quicker.Einvoicing.Zatca;

namespace Quicker.Einvoicing.Tests;

/// <summary>
/// The cryptography of the ZATCA adapter held to known vectors (A-179): the canonical form, the first previous hash
/// the scheme specifies, a certificate request carrying the scheme's alternative name, the invoice hash unchanged by
/// the signature block it excludes, a signature that verifies with the certificate's key, and the TLV QR read back.
/// </summary>
public sealed class ZatcaCryptoTests
{
    private static readonly EinvoiceParty Seller = new("Riyadh Services", "310123456700003", "1010010000", "CRN", "King Fahd Road", "1234", null, "Al Olaya", "Riyadh", "12211", "SA");

    private static EinvoiceDocument Document(bool withBuyer) => new(
        "sales_invoice", Guid.Parse("018f3a2e-0000-7000-8000-000000000001"), Guid.NewGuid(), "INV-2026-00001", EinvoiceDocumentKinds.Invoice, new DateOnly(2026, 10, 31), new DateTimeOffset(2026, 10, 31, 12, 30, 0, TimeSpan.FromHours(3)), "SAR",
        Seller, withBuyer ? new EinvoiceParty("Dammam Co", "399999999900003", null, "CRN", "Prince Sultan Street", "4321", null, "Al Faisaliyah", "Dammam", "32241", "SA") : null,
        [new EinvoiceLine(1, "Office cleaning", 10m, "PCE", 50m, 0m, 500m, EinvoiceTaxCategories.Standard, 15m, 75m, null, null), new EinvoiceLine(2, "Exempt service", 1m, "PCE", 100m, 0m, 100m, EinvoiceTaxCategories.Exempt, 0m, 0m, "VATEX-SA-29", "Financial services")],
        600m, 75m, 675m, null, null, null, null, new DateOnly(2026, 10, 31));

    [Fact]
    public void Canonical_form_orders_namespaces_and_attributes_escapes_text_and_writes_empty_elements_open()
    {
        var xml = "<?xml version=\"1.0\"?>\n<a xmlns:z=\"urn:z\" xmlns=\"urn:d\" z:b=\"2\" a=\"1 &amp; &quot;two&quot;\"><!-- gone --><z:e/><t>x &lt; y &gt; z</t>\n</a>";
        XmlCanonicalizer.CanonicalizeToString(XDocument.Parse(xml, LoadOptions.PreserveWhitespace))
            .ShouldBe("<a xmlns=\"urn:d\" xmlns:z=\"urn:z\" a=\"1 &amp; &quot;two&quot;\" z:b=\"2\"><z:e></z:e><t>x &lt; y &gt; z</t>\n</a>");
    }

    [Fact]
    public void The_first_previous_hash_is_the_one_the_scheme_specifies()
    {
        ZatcaCrypto.FirstPreviousHash.ShouldBe("NWZlY2ViNjZmZmM4NmYzOGQ5NTI3ODZjNmQ2OTZjNzljMmRiYzIzOWRkNGU5MWI0NjcyOWQ3M2EyN2ZiNTdlOQ==");
        ZatcaCrypto.CertificateDigest("MIIB").ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData("MIIB"u8)))));
    }

    [Fact]
    public void A_certificate_request_carries_the_subject_and_the_alternative_name_the_scheme_reads()
    {
        using var key = ZatcaCrypto.CreateKey();
        var csr = ZatcaCrypto.BuildCsr(key, new CsrProfile("RIY-POS-1", "Riyadh branch", "Riyadh Services", "SA", "1-Quicker|2-ERP|3-abc", "310123456700003", "1100", "King Fahd Road, Riyadh", "Trading"), production: false);
        csr.ShouldStartWith("-----BEGIN CERTIFICATE REQUEST-----");
        var request = CertificateRequest.LoadSigningRequestPem(csr, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        request.SubjectName.Name.ShouldContain("CN=RIY-POS-1");
        request.SubjectName.Name.ShouldContain("O=Riyadh Services");
        request.CertificateExtensions.Select(static e => e.Oid?.Value).ShouldContain("1.3.6.1.4.1.311.20.2");
        var alternative = ZatcaCrypto.ReadAlternativeName(csr);
        alternative.Values.ShouldContain("1-Quicker|2-ERP|3-abc");
        alternative.Values.ShouldContain("310123456700003");
        alternative.Values.ShouldContain("1100");
        alternative.Values.ShouldContain("Trading");
    }

    [Fact]
    public void The_invoice_hash_ignores_the_signature_block_the_qr_and_the_extensions_and_the_signature_verifies()
    {
        var facts = new SubmissionFacts(Guid.Parse("018f3a2e-0000-7000-8000-00000000abcd"), 7, ZatcaCrypto.FirstPreviousHash, Simplified: true);
        var xml = ZatcaInvoiceXml.Build(Document(withBuyer: false), facts);
        xml.ShouldContain(ZatcaCrypto.ExtensionsPlaceholder);
        xml.ShouldContain("<cbc:InvoiceTypeCode name=\"0200000\">388</cbc:InvoiceTypeCode>");
        xml.ShouldContain("<cbc:UUID>7</cbc:UUID>");
        var hashBefore = ZatcaCrypto.InvoiceHash(xml);

        using var key = ZatcaCrypto.CreateKey();
        using var certificate = SelfSigned(key);
        var certificateBase64 = Convert.ToBase64String(certificate.RawData);
        var signed = ZatcaCrypto.Sign(xml, key, certificateBase64, new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero), new QrFacts("Riyadh Services", "310123456700003", new DateTimeOffset(2026, 10, 31, 9, 30, 0, TimeSpan.Zero), 675m, 75m));
        signed.InvoiceHash.ShouldBe(hashBefore, "filling the placeholders changes nothing the hash covers");
        ZatcaCrypto.InvoiceHash(signed.Xml).ShouldBe(hashBefore, "the scheme recomputes the hash from the signed bytes");
        signed.Xml.ShouldNotContain(ZatcaCrypto.QrPlaceholder);
        signed.Xml.ShouldContain("<ds:X509Certificate>" + certificateBase64 + "</ds:X509Certificate>");
        signed.Xml.ShouldContain("<ds:DigestValue>" + hashBefore + "</ds:DigestValue>");
        signed.Xml.ShouldContain("<ds:SignatureValue>" + signed.Signature + "</ds:SignatureValue>");
        signed.Xml.ShouldContain("<ds:X509SerialNumber>" + ZatcaCrypto.SerialNumberDecimal(certificate) + "</ds:X509SerialNumber>");
        XDocument.Parse(signed.Xml).Root.ShouldNotBeNull("the signed document is well formed");
        ZatcaCrypto.VerifyHashSignature(key, hashBefore, signed.Signature).ShouldBeTrue();
        ZatcaCrypto.VerifyHashSignature(key, ZatcaCrypto.InvoiceHash(xml.Replace("675.00", "676.00", StringComparison.Ordinal)), signed.Signature).ShouldBeFalse("a changed invoice has another hash");

        var qr = ZatcaCrypto.ReadQr(signed.Qr);
        qr[1].ShouldBe("Riyadh Services");
        qr[2].ShouldBe("310123456700003");
        qr[3].ShouldBe("2026-10-31T09:30:00Z");
        qr[4].ShouldBe("675.00");
        qr[5].ShouldBe("75.00");
        qr[6].ShouldBe(hashBefore);
        qr[7].ShouldBe(signed.Signature);
        Convert.FromBase64String(qr[8]).ShouldBe(key.ExportSubjectPublicKeyInfo());
        qr.ShouldContainKey(9);
    }

    [Fact]
    public void A_standard_invoice_has_no_signature_room_and_a_credit_note_references_its_invoice()
    {
        var standard = ZatcaInvoiceXml.Build(Document(withBuyer: true), new SubmissionFacts(Guid.NewGuid(), 1, ZatcaCrypto.FirstPreviousHash, Simplified: false));
        standard.ShouldNotContain("UBLExtensions");
        standard.ShouldNotContain("<cbc:ID>QR</cbc:ID>");
        standard.ShouldContain("<cbc:InvoiceTypeCode name=\"0100000\">388</cbc:InvoiceTypeCode>");
        standard.ShouldContain("<cbc:CompanyID>399999999900003</cbc:CompanyID>");
        standard.ShouldContain("<cbc:TaxExemptionReasonCode>VATEX-SA-29</cbc:TaxExemptionReasonCode>");
        standard.ShouldContain("<cbc:ActualDeliveryDate>2026-10-31</cbc:ActualDeliveryDate>");
        var document = XDocument.Parse(standard);
        document.Root!.Elements(ZatcaCrypto.Cac + "TaxTotal").Count().ShouldBe(2);
        document.Root.Elements(ZatcaCrypto.Cac + "InvoiceLine").Count().ShouldBe(2);

        var credit = Document(withBuyer: true) with { Kind = EinvoiceDocumentKinds.CreditNote, Number = "CN-2026-00001", OriginalDocumentNumber = "INV-2026-00001", Reason = "Goods returned" };
        var creditXml = ZatcaInvoiceXml.Build(credit, new SubmissionFacts(Guid.NewGuid(), 2, "previous", Simplified: false));
        creditXml.ShouldContain(">381</cbc:InvoiceTypeCode>");
        creditXml.ShouldContain("<cac:BillingReference>");
        creditXml.ShouldContain("<cbc:ID>INV-2026-00001</cbc:ID>");
        creditXml.ShouldContain("<cbc:InstructionNote>Goods returned</cbc:InstructionNote>");
        creditXml.ShouldContain("<cbc:EmbeddedDocumentBinaryObject mimeCode=\"text/plain\">previous</cbc:EmbeddedDocumentBinaryObject>");
    }

    private static X509Certificate2 SelfSigned(ECDsa key)
    {
        var request = new CertificateRequest("CN=TEST-DEVICE, O=Riyadh Services, C=SA", key, HashAlgorithmName.SHA256);
        return request.CreateSelfSigned(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
