using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Xml.Linq;

namespace Quicker.Einvoicing.Zatca;

/// <summary>What a device's certificate request says about it: the subject and the ZATCA-specific alternative name.</summary>
public sealed record CsrProfile(string CommonName, string OrganizationUnit, string Organization, string Country, string SerialNumber, string VatNumber, string InvoiceTypes, string Location, string Industry);

/// <summary>
/// The cryptography of ZATCA phase 2 (ADR-0018 amended, A-179): a secp256k1 key and the certificate request the
/// scheme signs; the invoice hash (SHA-256 of the canonical invoice without its signature, extensions and QR, base64);
/// the previous-hash chain; the XAdES signature block of a simplified invoice, whose signature value is the ECDSA
/// signature of the invoice hash, as the scheme verifies it; and the TLV QR code. Every value is deterministic for
/// its inputs so the tests hold the implementation to known vectors.
/// </summary>
public static class ZatcaCrypto
{
    public static readonly XNamespace Inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    public static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    public static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    public static readonly XNamespace Ext = "urn:oasis:names:specification:ubl:schema:xsd:CommonExtensionComponents-2";
    public static readonly XNamespace Ds = "http://www.w3.org/2000/09/xmldsig#";
    public static readonly XNamespace Xades = "http://uri.etsi.org/01903/v1.3.2#";

    /// <summary>The previous invoice hash of a device's first invoice: base64 of the hex SHA-256 of "0", as the scheme specifies.</summary>
    public static readonly string FirstPreviousHash = Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData("0"u8))));

    private static readonly Oid Secp256k1 = new("1.3.132.0.10");

    // ------------------------------------------------------------------ keys and certificate requests

    public static ECDsa CreateKey() => ECDsa.Create(ECCurve.CreateFromOid(Secp256k1));

    public static string ExportKey(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportECPrivateKeyPem();
    }

    public static ECDsa ImportKey(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }

    /// <summary>The PEM certificate request ZATCA expects: subject C/O/OU/CN, the certificate template name (pre-production or production), and the alternative name carrying the serial, the VAT number, the invoice types, the address and the industry.</summary>
    public static string BuildCsr(ECDsa key, CsrProfile profile, bool production)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(profile);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCountryOrRegion(profile.Country);
        subject.AddOrganizationName(profile.Organization);
        subject.AddOrganizationalUnitName(profile.OrganizationUnit);
        subject.AddCommonName(profile.CommonName);
        var request = new CertificateRequest(subject.Build(), key, HashAlgorithmName.SHA256);

        var template = new AsnWriter(AsnEncodingRules.DER);
        template.WriteCharacterString(UniversalTagNumber.PrintableString, production ? "ZATCA-Code-Signing" : "PREZATCA-Code-Signing");
        request.CertificateExtensions.Add(new X509Extension(new Oid("1.3.6.1.4.1.311.20.2"), template.Encode(), false));

        var alternative = new X500DistinguishedNameBuilder();
        alternative.Add(SerialNumberOid, profile.SerialNumber, UniversalTagNumber.UTF8String);
        alternative.Add(UserIdOid, profile.VatNumber, UniversalTagNumber.UTF8String);
        alternative.Add(TitleOid, profile.InvoiceTypes, UniversalTagNumber.UTF8String);
        alternative.Add(RegisteredAddressOid, profile.Location, UniversalTagNumber.UTF8String);
        alternative.Add(BusinessCategoryOid, profile.Industry, UniversalTagNumber.UTF8String);
        var san = new AsnWriter(AsnEncodingRules.DER);
        using (san.PushSequence())
        using (san.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 4, isConstructed: true)))
        {
            san.WriteEncodedValue(alternative.Build().RawData);
        }

        request.CertificateExtensions.Add(new X509Extension(new Oid("2.5.29.17"), san.Encode(), false));
        return request.CreateSigningRequestPem();
    }

    public const string SerialNumberOid = "2.5.4.5";
    public const string UserIdOid = "0.9.2342.19200300.100.1.1";
    public const string TitleOid = "2.5.4.12";
    public const string RegisteredAddressOid = "2.5.4.26";
    public const string BusinessCategoryOid = "2.5.4.15";

    /// <summary>Reads back the alternative name of a request, keyed by attribute OID, as the scheme (and the simulator) check it.</summary>
    public static IReadOnlyDictionary<string, string> ReadAlternativeName(string csrPem)
    {
        var request = CertificateRequest.LoadSigningRequestPem(csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        var san = request.CertificateExtensions.FirstOrDefault(static e => e.Oid?.Value == "2.5.29.17");
        return san is null ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadAlternativeName(san.RawData);
    }

    /// <summary>The alternative name of a certificate, keyed by attribute OID.</summary>
    public static IReadOnlyDictionary<string, string> ReadAlternativeName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var san = certificate.Extensions.FirstOrDefault(static e => e.Oid?.Value == "2.5.29.17");
        return san is null ? new Dictionary<string, string>(StringComparer.Ordinal) : ReadAlternativeName(san.RawData);
    }

    private static Dictionary<string, string> ReadAlternativeName(byte[] extension)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new AsnReader(extension, AsnEncodingRules.DER).ReadSequence();
        while (names.HasData)
        {
            var tag = names.PeekTag();
            if (tag.TagClass == TagClass.ContextSpecific && tag.TagValue == 4)
            {
                var name = names.ReadSequence(tag).ReadSequence();
                while (name.HasData)
                {
                    var set = name.ReadSetOf();
                    while (set.HasData)
                    {
                        var attribute = set.ReadSequence();
                        var oid = attribute.ReadObjectIdentifier();
                        var valueTag = attribute.PeekTag();
                        if (valueTag.TagClass == TagClass.Universal && valueTag.TagValue is 12 or 19 or 22 or 26 or 30 or 20 or 18)
                        {
                            result[oid] = attribute.ReadCharacterString((UniversalTagNumber)valueTag.TagValue);
                        }
                        else
                        {
                            attribute.ReadEncodedValue();
                        }
                    }
                }
            }
            else
            {
                names.ReadEncodedValue();
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ hashes

    /// <summary>The invoice hash: SHA-256 of the canonical invoice without UBLExtensions, cac:Signature and the QR reference, base64.</summary>
    public static string InvoiceHash(string xml)
    {
        var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        return Convert.ToBase64String(SHA256.HashData(XmlCanonicalizer.Canonicalize(StripForHashing(document))));
    }

    /// <summary>The document without the three parts the hash excludes; a copy, so the original keeps them.</summary>
    public static XDocument StripForHashing(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var copy = new XDocument(document);
        copy.Descendants(Ext + "UBLExtensions").ToList().ForEach(static e => e.Remove());
        copy.Descendants(Cac + "Signature").ToList().ForEach(static e => e.Remove());
        copy.Descendants(Cac + "AdditionalDocumentReference").Where(static e => e.Element(Cbc + "ID")?.Value == "QR").ToList().ForEach(static e => e.Remove());
        return copy;
    }

    /// <summary>The digest of a certificate in the signing certificate reference: base64 of the hex SHA-256 of the certificate's base64 text, as the scheme computes it.</summary>
    public static string CertificateDigest(string certificateBase64) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(certificateBase64)))));

    /// <summary>The digest of the signed properties block: base64 of the hex SHA-256 of its exact text.</summary>
    public static string SignedPropertiesDigest(string signedProperties) => Convert.ToBase64String(Encoding.UTF8.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(signedProperties)))));

    /// <summary>The ECDSA signature of the invoice hash (its bytes, SHA-256, DER), base64: the signature value of a simplified invoice and the seventh QR tag.</summary>
    public static string SignHash(ECDsa key, string invoiceHash)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Convert.ToBase64String(key.SignData(Convert.FromBase64String(invoiceHash), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
    }

    public static bool VerifyHashSignature(ECDsa publicKey, string invoiceHash, string signatureBase64)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        try
        {
            return publicKey.VerifyData(Convert.FromBase64String(invoiceHash), Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ certificates

    public static X509Certificate2 LoadCertificate(string base64) => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(base64));

    /// <summary>The certificate's own signature (the authority's), the ninth QR tag of a simplified invoice.</summary>
    public static byte[] CertificateSignature(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var reader = new AsnReader(certificate.RawData, AsnEncodingRules.DER).ReadSequence();
        reader.ReadEncodedValue(); // tbsCertificate
        reader.ReadEncodedValue(); // signatureAlgorithm
        return reader.ReadBitString(out _);
    }

    public static string SerialNumberDecimal(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return new BigInteger(certificate.GetSerialNumber(), isUnsigned: true).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The issuer name as the scheme prints it in the signed properties ("CN=..., DC=..., DC=...").</summary>
    public static string IssuerName(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        return certificate.IssuerName.Name;
    }

    // ------------------------------------------------------------------ signature block

    /// <summary>The seller-side signature of a simplified invoice: fills the extensions placeholder, the QR reference and the signature of the invoice XML. The invoice hash is computed on the document as it stands, so the placeholders must already be in place.</summary>
    public static SignedInvoice Sign(string invoiceXml, ECDsa key, string certificateBase64, DateTimeOffset signedAt, QrFacts facts)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(facts);
        var hash = InvoiceHash(invoiceXml);
        var certificate = LoadCertificate(certificateBase64);
        var signature = SignHash(key, hash);
        var signedProperties = SignedPropertiesTemplate
            .Replace("{SIGN_TIMESTAMP}", signedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{CERTIFICATE_HASH}", CertificateDigest(certificateBase64), StringComparison.Ordinal)
            .Replace("{CERTIFICATE_ISSUER}", EscapeXml(IssuerName(certificate)), StringComparison.Ordinal)
            .Replace("{CERTIFICATE_SERIAL_NUMBER}", SerialNumberDecimal(certificate), StringComparison.Ordinal);
        var extensions = ExtensionsTemplate
            .Replace("{INVOICE_HASH}", hash, StringComparison.Ordinal)
            .Replace("{SIGNED_PROPERTIES_HASH}", SignedPropertiesDigest(signedProperties), StringComparison.Ordinal)
            .Replace("{SIGNATURE}", signature, StringComparison.Ordinal)
            .Replace("{CERTIFICATE}", certificateBase64, StringComparison.Ordinal)
            .Replace("{SIGNED_PROPERTIES}", signedProperties, StringComparison.Ordinal);
        var qr = BuildQr(facts with { InvoiceHash = hash, Signature = signature, PublicKey = certificate.GetECDsaPublicKey()?.ExportSubjectPublicKeyInfo() ?? [], CertificateSignature = CertificateSignature(certificate) });
        var xml = invoiceXml
            .Replace(ExtensionsPlaceholder, extensions, StringComparison.Ordinal)
            .Replace(QrPlaceholder, qr, StringComparison.Ordinal);
        return new SignedInvoice(xml, hash, signature, qr);
    }

    /// <summary>Where the invoice XML leaves room for the signature block: the empty extensions element as the writer renders it.</summary>
    public const string ExtensionsPlaceholder = "    <ext:UBLExtensions />";

    public const string QrPlaceholder = "QR_CODE_PLACEHOLDER";

    // ------------------------------------------------------------------ QR

    /// <summary>The TLV QR of an invoice: seller, VAT number, time stamp, totals, hash, and for a signed invoice the signature, the public key and the certificate's signature.</summary>
    public static string BuildQr(QrFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var bytes = new List<byte>();
        Tag(bytes, 1, Encoding.UTF8.GetBytes(Truncate(facts.SellerName, 255)));
        Tag(bytes, 2, Encoding.UTF8.GetBytes(facts.VatNumber));
        Tag(bytes, 3, Encoding.UTF8.GetBytes(facts.Timestamp.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)));
        Tag(bytes, 4, Encoding.UTF8.GetBytes(facts.TotalWithTax.ToString("0.00", CultureInfo.InvariantCulture)));
        Tag(bytes, 5, Encoding.UTF8.GetBytes(facts.TaxTotal.ToString("0.00", CultureInfo.InvariantCulture)));
        Tag(bytes, 6, Encoding.UTF8.GetBytes(facts.InvoiceHash ?? string.Empty));
        if (facts.Signature is not null)
        {
            Tag(bytes, 7, Encoding.UTF8.GetBytes(facts.Signature));
        }

        if (facts.PublicKey is { Length: > 0 })
        {
            Tag(bytes, 8, facts.PublicKey);
        }

        if (facts.CertificateSignature is { Length: > 0 })
        {
            Tag(bytes, 9, facts.CertificateSignature);
        }

        return Convert.ToBase64String(bytes.ToArray());
    }

    /// <summary>The tags of a TLV QR, by number; text tags decoded as UTF-8, binary ones as base64.</summary>
    public static IReadOnlyDictionary<int, string> ReadQr(string base64)
    {
        var bytes = Convert.FromBase64String(base64);
        var result = new Dictionary<int, string>();
        var i = 0;
        while (i + 2 <= bytes.Length)
        {
            int tag = bytes[i];
            int length = bytes[i + 1];
            i += 2;
            if (i + length > bytes.Length)
            {
                break;
            }

            var value = bytes.AsSpan(i, length).ToArray();
            result[tag] = tag is 8 or 9 ? Convert.ToBase64String(value) : Encoding.UTF8.GetString(value);
            i += length;
        }

        return result;
    }

    private static void Tag(List<byte> bytes, byte tag, byte[] value)
    {
        var length = Math.Min(value.Length, 255);
        bytes.Add(tag);
        bytes.Add((byte)length);
        bytes.AddRange(value.AsSpan(0, length).ToArray());
    }

    private static string Truncate(string value, int maxBytes)
    {
        var text = value;
        while (Encoding.UTF8.GetByteCount(text) > maxBytes)
        {
            text = text[..^1];
        }

        return text;
    }

    private static string EscapeXml(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    // The templates follow the layout the scheme's own SDK produces, since the signed-properties digest is computed on this exact text.
    private const string SignedPropertiesTemplate = """
        <xades:SignedProperties Id="xadesSignedProperties">
                                            <xades:SignedSignatureProperties>
                                                <xades:SigningTime>{SIGN_TIMESTAMP}</xades:SigningTime>
                                                <xades:SigningCertificate>
                                                    <xades:Cert>
                                                        <xades:CertDigest>
                                                            <ds:DigestMethod Algorithm="http://www.w3.org/2001/04/xmlenc#sha256"/>
                                                            <ds:DigestValue>{CERTIFICATE_HASH}</ds:DigestValue>
                                                        </xades:CertDigest>
                                                        <xades:IssuerSerial>
                                                            <ds:X509IssuerName>{CERTIFICATE_ISSUER}</ds:X509IssuerName>
                                                            <ds:X509SerialNumber>{CERTIFICATE_SERIAL_NUMBER}</ds:X509SerialNumber>
                                                        </xades:IssuerSerial>
                                                    </xades:Cert>
                                                </xades:SigningCertificate>
                                            </xades:SignedSignatureProperties>
                                        </xades:SignedProperties>
        """;

    private const string ExtensionsTemplate = """
            <ext:UBLExtensions>
                <ext:UBLExtension>
                    <ext:ExtensionURI>urn:oasis:names:specification:ubl:dsig:enveloped:xades</ext:ExtensionURI>
                    <ext:ExtensionContent>
                        <sig:UBLDocumentSignatures xmlns:sac="urn:oasis:names:specification:ubl:schema:xsd:SignatureAggregateComponents-2" xmlns:sbc="urn:oasis:names:specification:ubl:schema:xsd:SignatureBasicComponents-2" xmlns:sig="urn:oasis:names:specification:ubl:schema:xsd:CommonSignatureComponents-2">
                            <sac:SignatureInformation>
                                <cbc:ID>urn:oasis:names:specification:ubl:signature:1</cbc:ID>
                                <sbc:ReferencedSignatureID>urn:oasis:names:specification:ubl:signature:Invoice</sbc:ReferencedSignatureID>
                                <ds:Signature Id="signature" xmlns:ds="http://www.w3.org/2000/09/xmldsig#">
                                    <ds:SignedInfo>
                                        <ds:CanonicalizationMethod Algorithm="http://www.w3.org/2006/12/xml-c14n11"/>
                                        <ds:SignatureMethod Algorithm="http://www.w3.org/2001/04/xmldsig-more#ecdsa-sha256"/>
                                        <ds:Reference Id="invoiceSignedData" URI="">
                                            <ds:Transforms>
                                                <ds:Transform Algorithm="http://www.w3.org/TR/1999/REC-xpath-19991116">
                                                    <ds:XPath>not(//ancestor-or-self::ext:UBLExtensions)</ds:XPath>
                                                </ds:Transform>
                                                <ds:Transform Algorithm="http://www.w3.org/TR/1999/REC-xpath-19991116">
                                                    <ds:XPath>not(//ancestor-or-self::cac:Signature)</ds:XPath>
                                                </ds:Transform>
                                                <ds:Transform Algorithm="http://www.w3.org/TR/1999/REC-xpath-19991116">
                                                    <ds:XPath>not(//ancestor-or-self::cac:AdditionalDocumentReference[cbc:ID='QR'])</ds:XPath>
                                                </ds:Transform>
                                                <ds:Transform Algorithm="http://www.w3.org/2006/12/xml-c14n11"/>
                                            </ds:Transforms>
                                            <ds:DigestMethod Algorithm="http://www.w3.org/2001/04/xmlenc#sha256"/>
                                            <ds:DigestValue>{INVOICE_HASH}</ds:DigestValue>
                                        </ds:Reference>
                                        <ds:Reference Type="http://www.w3.org/2000/09/xmldsig#SignatureProperties" URI="#xadesSignedProperties">
                                            <ds:DigestMethod Algorithm="http://www.w3.org/2001/04/xmlenc#sha256"/>
                                            <ds:DigestValue>{SIGNED_PROPERTIES_HASH}</ds:DigestValue>
                                        </ds:Reference>
                                    </ds:SignedInfo>
                                    <ds:SignatureValue>{SIGNATURE}</ds:SignatureValue>
                                    <ds:KeyInfo>
                                        <ds:X509Data>
                                            <ds:X509Certificate>{CERTIFICATE}</ds:X509Certificate>
                                        </ds:X509Data>
                                    </ds:KeyInfo>
                                    <ds:Object>
                                        <xades:QualifyingProperties Target="signature" xmlns:xades="http://uri.etsi.org/01903/v1.3.2#">
                                        {SIGNED_PROPERTIES}
                                        </xades:QualifyingProperties>
                                    </ds:Object>
                                </ds:Signature>
                            </sac:SignatureInformation>
                        </sig:UBLDocumentSignatures>
                    </ext:ExtensionContent>
                </ext:UBLExtension>
            </ext:UBLExtensions>
        """;
}

/// <summary>What goes into the QR besides the hash and the signature.</summary>
public sealed record QrFacts(string SellerName, string VatNumber, DateTimeOffset Timestamp, decimal TotalWithTax, decimal TaxTotal, string? InvoiceHash = null, string? Signature = null, byte[]? PublicKey = null, byte[]? CertificateSignature = null);

public sealed record SignedInvoice(string Xml, string InvoiceHash, string Signature, string Qr);
