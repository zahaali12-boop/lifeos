using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Quicker.Kernel.Time;

namespace Quicker.Einvoicing.Zatca;

/// <summary>
/// An in-process stand-in for the ZATCA gateway (A-179), for development, demonstrations and tests: it issues
/// certificates from a certificate request the way the scheme does (a compliance one for a six-digit OTP, then a
/// production one), and it checks a submitted invoice the way the scheme's validator does on the points this adapter
/// is responsible for: the hash against the canonical XML, the UUID, the counter and previous-hash references, the
/// seller's VAT number, the totals, and for a simplified invoice the signature against the certificate and the QR
/// against the hash and the signature. It is not the scheme: a device meant for a real tax authority is onboarded
/// against the sandbox and then production, and the simulator says so in every answer.
/// </summary>
public sealed class ZatcaSimulator : HttpMessageHandler
{
    private readonly ECDsa _caKey;
    private readonly X509Certificate2 _ca;
    private readonly Dictionary<string, string> _secretsByToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _requestsByToken = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();
    private readonly IClock _clock;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private long _serial = 1000;

    public ZatcaSimulator(IClock clock)
    {
        _clock = clock;
        _caKey = ZatcaCrypto.CreateKey();
        var request = new CertificateRequest("CN=QUICKER-SIMULATOR-CA, DC=simulator, DC=quicker, DC=local", _caKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        _ca = request.CreateSelfSigned(_clock.UtcNow.AddDays(-1), _clock.UtcNow.AddYears(10));
    }

    public const string Marker = "SIMULATOR: this answer comes from the in-process simulator, not from ZATCA.";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.RequestUri?.AbsolutePath ?? string.Empty;
        var body = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        var root = json.RootElement;
        if (path.EndsWith("/compliance", StringComparison.Ordinal) && request.Method == HttpMethod.Post)
        {
            var otp = request.Headers.TryGetValues("OTP", out var otps) ? otps.FirstOrDefault() ?? string.Empty : string.Empty;
            if (otp.Length != 6 || !otp.All(char.IsAsciiDigit))
            {
                return Reply(HttpStatusCode.BadRequest, new { errors = new[] { new { code = "INVALID_OTP", message = "The OTP must be the six digits shown by the simulator (any six digits)." } } });
            }

            return Issue(root, production: false);
        }

        if (path.EndsWith("/production/csids", StringComparison.Ordinal))
        {
            if (!Authorized(request, out _))
            {
                return Reply(HttpStatusCode.Unauthorized, new { message = "Unauthorized" });
            }

            if (request.Method == HttpMethod.Post)
            {
                var requestId = root.TryGetProperty("compliance_request_id", out var rid) ? rid.ToString() : string.Empty;
                lock (_lock)
                {
                    if (!_requestsByToken.ContainsValue(requestId))
                    {
                        return Reply(HttpStatusCode.BadRequest, new { errors = new[] { new { code = "INVALID_REQUEST_ID", message = "No compliance request with this id." } } });
                    }
                }

                return Reissue(request, production: true);
            }

            return Issue(root, production: true);
        }

        if (!Authorized(request, out var certificate))
        {
            return Reply(HttpStatusCode.Unauthorized, new { message = "Unauthorized" });
        }

        var clearance = path.EndsWith("/invoices/clearance/single", StringComparison.Ordinal);
        var reporting = path.EndsWith("/invoices/reporting/single", StringComparison.Ordinal);
        var compliance = path.EndsWith("/compliance/invoices", StringComparison.Ordinal);
        if (!clearance && !reporting && !compliance)
        {
            return Reply(HttpStatusCode.NotFound, new { message = $"Unknown path {path}" });
        }

        return Validate(root, certificate, clearance, compliance);
    }

    // ------------------------------------------------------------------ certificates

    private HttpResponseMessage Issue(JsonElement root, bool production)
    {
        if (!root.TryGetProperty("csr", out var csrElement) || csrElement.GetString() is not { Length: > 0 } csrBase64)
        {
            return Reply(HttpStatusCode.BadRequest, new { errors = new[] { new { code = "MISSING_CSR", message = "The request carries no csr." } } });
        }

        string pem;
        try
        {
            pem = Encoding.UTF8.GetString(Convert.FromBase64String(csrBase64));
        }
        catch (FormatException)
        {
            return Reply(HttpStatusCode.BadRequest, new { errors = new[] { new { code = "INVALID_CSR", message = "The csr is not base64." } } });
        }

        var alternative = ZatcaCrypto.ReadAlternativeName(pem);
        var problems = new List<string>();
        if (!alternative.TryGetValue(ZatcaCrypto.SerialNumberOid, out var serial))
        {
            problems.Add("The alternative name has no serial number (SN).");
        }
        else if (!serial.StartsWith("1-", StringComparison.Ordinal) || !serial.Contains("|2-", StringComparison.Ordinal) || !serial.Contains("|3-", StringComparison.Ordinal))
        {
            problems.Add("The serial number must read 1-<solution>|2-<model>|3-<unit>.");
        }

        var vat = alternative.GetValueOrDefault(ZatcaCrypto.UserIdOid);
        if (vat is null || vat.Length != 15 || !vat.All(char.IsAsciiDigit) || vat[0] != '3' || vat[^1] != '3')
        {
            problems.Add("The VAT registration number (UID) must be 15 digits starting and ending with 3.");
        }

        var types = alternative.GetValueOrDefault(ZatcaCrypto.TitleOid);
        if (types is null || types.Length != 4 || !types.All(static c => c is '0' or '1') || types[0] == '0' && types[1] == '0')
        {
            problems.Add("The invoice types (title) must be four flags with standard or simplified set, such as 1100.");
        }

        if (problems.Count > 0)
        {
            return Reply(HttpStatusCode.BadRequest, new { errors = problems.Select(p => new { code = "INVALID_CSR", message = p }).ToArray() });
        }

        var request = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        X509Certificate2 issued;
        long serialNumber;
        lock (_lock)
        {
            serialNumber = ++_serial;
        }

        issued = request.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_caKey), _clock.UtcNow.AddMinutes(-5), _clock.UtcNow.AddYears(production ? 3 : 1), BitConverter.GetBytes(serialNumber).Reverse().ToArray());

        var certificateBase64 = Convert.ToBase64String(issued.RawData);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(certificateBase64));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var requestId = serialNumber.ToString(CultureInfo.InvariantCulture);
        lock (_lock)
        {
            _secretsByToken[token] = secret;
            _requestsByToken[token] = requestId;
        }

        return Reply(HttpStatusCode.OK, new { requestID = requestId, dispositionMessage = "ISSUED", binarySecurityToken = token, secret, errors = (object?)null, simulator = Marker });
    }

    /// <summary>A production certificate for a device whose compliance certificate authenticated the call: the same subject, reissued.</summary>
    private HttpResponseMessage Reissue(HttpRequestMessage request, bool production)
    {
        Authorized(request, out var compliance);
        if (compliance is null)
        {
            return Reply(HttpStatusCode.Unauthorized, new { message = "Unauthorized" });
        }

        var publicKey = compliance.GetECDsaPublicKey() ?? throw new InvalidOperationException("The compliance certificate has no EC key.");
        var csr = new CertificateRequest(compliance.SubjectName, publicKey, HashAlgorithmName.SHA256);
        foreach (var extension in compliance.Extensions.Where(static e => e.Oid?.Value is "2.5.29.17" or "1.3.6.1.4.1.311.20.2"))
        {
            csr.CertificateExtensions.Add(extension);
        }

        long serialNumber;
        lock (_lock)
        {
            serialNumber = ++_serial;
        }

        var issued = csr.Create(_ca.SubjectName, X509SignatureGenerator.CreateForECDsa(_caKey), _clock.UtcNow.AddMinutes(-5), _clock.UtcNow.AddYears(production ? 3 : 1), BitConverter.GetBytes(serialNumber).Reverse().ToArray());
        var certificateBase64 = Convert.ToBase64String(issued.RawData);
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(certificateBase64));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (_lock)
        {
            _secretsByToken[token] = secret;
        }

        return Reply(HttpStatusCode.OK, new { requestID = serialNumber.ToString(CultureInfo.InvariantCulture), dispositionMessage = "ISSUED", binarySecurityToken = token, secret, errors = (object?)null, simulator = Marker });
    }

    private bool Authorized(HttpRequestMessage request, out X509Certificate2? certificate)
    {
        certificate = null;
        if (request.Headers.Authorization is not { Scheme: "Basic", Parameter: { } parameter })
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(parameter));
        }
        catch (FormatException)
        {
            return false;
        }

        var colon = decoded.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        var token = decoded[..colon];
        var secret = decoded[(colon + 1)..];
        lock (_lock)
        {
            if (!_secretsByToken.TryGetValue(token, out var expected) || !string.Equals(expected, secret, StringComparison.Ordinal))
            {
                return false;
            }
        }

        certificate = ZatcaCrypto.LoadCertificate(Encoding.UTF8.GetString(Convert.FromBase64String(token)));
        return true;
    }

    // ------------------------------------------------------------------ documents

    private HttpResponseMessage Validate(JsonElement root, X509Certificate2? deviceCertificate, bool clearance, bool compliance)
    {
        var errors = new List<object>();
        var warnings = new List<object>();
        string? xml = null;
        try
        {
            xml = root.TryGetProperty("invoice", out var inv) && inv.GetString() is { Length: > 0 } encoded ? Encoding.UTF8.GetString(Convert.FromBase64String(encoded)) : null;
        }
        catch (FormatException)
        {
            xml = null;
        }

        if (xml is null)
        {
            return Reply(HttpStatusCode.BadRequest, new { validationResults = new { status = "ERROR", errorMessages = new[] { Message("XSD", "The request carries no invoice.") } }, simulator = Marker });
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
        }
        catch (System.Xml.XmlException e)
        {
            return Reply(HttpStatusCode.BadRequest, new { validationResults = new { status = "ERROR", errorMessages = new[] { Message("XSD", $"The invoice is not well-formed XML: {e.Message}") } }, simulator = Marker });
        }

        var cbc = ZatcaCrypto.Cbc;
        var cac = ZatcaCrypto.Cac;
        var invoice = document.Root!;
        var declaredHash = root.TryGetProperty("invoiceHash", out var h) ? h.GetString() ?? string.Empty : string.Empty;
        var computedHash = ZatcaCrypto.InvoiceHash(xml);
        if (!string.Equals(declaredHash, computedHash, StringComparison.Ordinal))
        {
            errors.Add(Message("HASH-MISMATCH", "The invoice hash does not match the invoice."));
        }

        var uuid = root.TryGetProperty("uuid", out var u) ? u.GetString() : null;
        if (!string.Equals(uuid, invoice.Element(cbc + "UUID")?.Value, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(Message("UUID-MISMATCH", "The uuid does not match the invoice's UUID."));
        }

        var typeCode = invoice.Element(cbc + "InvoiceTypeCode");
        var typeName = typeCode?.Attribute("name")?.Value ?? string.Empty;
        var simplified = typeName.StartsWith("02", StringComparison.Ordinal);
        if (typeCode is null || typeCode.Value is not ("388" or "381" or "383"))
        {
            errors.Add(Message("BR-KSA-05", "The invoice type code must be 388, 381 or 383."));
        }

        if (typeName.Length != 7 || !(typeName.StartsWith("01", StringComparison.Ordinal) || simplified))
        {
            errors.Add(Message("BR-KSA-06", "The invoice type name must be seven flags starting 01 (standard) or 02 (simplified)."));
        }

        if (string.IsNullOrWhiteSpace(invoice.Element(cbc + "ID")?.Value))
        {
            errors.Add(Message("BR-01", "The invoice has no number."));
        }

        if (invoice.Element(cbc + "IssueDate") is null || invoice.Element(cbc + "IssueTime") is null)
        {
            errors.Add(Message("BR-KSA-F-06", "The invoice needs an issue date and an issue time."));
        }

        var supplier = invoice.Element(cac + "AccountingSupplierParty")?.Element(cac + "Party");
        var sellerVat = supplier?.Element(cac + "PartyTaxScheme")?.Element(cbc + "CompanyID")?.Value;
        if (sellerVat is null || sellerVat.Length != 15 || !sellerVat.All(char.IsAsciiDigit) || sellerVat[0] != '3' || sellerVat[^1] != '3')
        {
            errors.Add(Message("BR-KSA-39", "The seller's VAT registration number must be 15 digits starting and ending with 3."));
        }

        if (deviceCertificate is not null && sellerVat is not null)
        {
            var alternative = ZatcaCrypto.ReadAlternativeName(deviceCertificate);
            if (alternative.TryGetValue(ZatcaCrypto.UserIdOid, out var certificateVat) && !string.Equals(certificateVat, sellerVat, StringComparison.Ordinal))
            {
                errors.Add(Message("CSID-VAT", "The seller's VAT number is not the one of the device's certificate."));
            }
        }

        if (string.IsNullOrWhiteSpace(supplier?.Element(cac + "PartyLegalEntity")?.Element(cbc + "RegistrationName")?.Value))
        {
            errors.Add(Message("BR-06", "The seller has no registration name."));
        }

        var address = supplier?.Element(cac + "PostalAddress");
        if (address is null || string.IsNullOrWhiteSpace(address.Element(cbc + "CityName")?.Value) || string.IsNullOrWhiteSpace(address.Element(cbc + "StreetName")?.Value))
        {
            errors.Add(Message("BR-KSA-09", "The seller's address needs at least a street and a city."));
        }

        if (string.IsNullOrWhiteSpace(address?.Element(cbc + "BuildingNumber")?.Value) || string.IsNullOrWhiteSpace(address.Element(cbc + "PostalZone")?.Value))
        {
            warnings.Add(Message("BR-KSA-37", "The seller's address should carry a building number and a postal zone."));
        }

        var references = invoice.Elements(cac + "AdditionalDocumentReference").ToDictionary(e => e.Element(cbc + "ID")?.Value ?? string.Empty, e => e, StringComparer.Ordinal);
        if (!references.ContainsKey("ICV"))
        {
            errors.Add(Message("BR-KSA-33", "The invoice counter value (ICV) reference is missing."));
        }

        if (!references.TryGetValue("PIH", out var pih) || string.IsNullOrWhiteSpace(pih.Element(cac + "Attachment")?.Element(cbc + "EmbeddedDocumentBinaryObject")?.Value))
        {
            errors.Add(Message("BR-KSA-26", "The previous invoice hash (PIH) reference is missing."));
        }

        if (!simplified)
        {
            var buyer = invoice.Element(cac + "AccountingCustomerParty")?.Element(cac + "Party");
            if (string.IsNullOrWhiteSpace(buyer?.Element(cac + "PartyTaxScheme")?.Element(cbc + "CompanyID")?.Value))
            {
                errors.Add(Message("BR-KSA-46", "A standard invoice names the buyer's VAT registration number."));
            }

            if (invoice.Element(cac + "Delivery")?.Element(cbc + "ActualDeliveryDate") is null)
            {
                warnings.Add(Message("BR-KSA-15", "A standard invoice should carry the actual delivery date."));
            }
        }

        if (typeCode?.Value is "381" or "383")
        {
            if (invoice.Element(cac + "BillingReference") is null)
            {
                errors.Add(Message("BR-KSA-56", "A credit or debit note references the invoice it corrects."));
            }

            if (string.IsNullOrWhiteSpace(invoice.Element(cac + "PaymentMeans")?.Element(cbc + "InstructionNote")?.Value))
            {
                errors.Add(Message("BR-KSA-17", "A credit or debit note states the reason in the payment means' instruction note."));
            }
        }

        var lines = invoice.Elements(cac + "InvoiceLine").ToList();
        if (lines.Count == 0)
        {
            errors.Add(Message("BR-16", "The invoice has no lines."));
        }

        var totals = invoice.Element(cac + "LegalMonetaryTotal");
        decimal Amount(XElement? parent, string name) => decimal.TryParse(parent?.Element(cbc + name)?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
        var lineSum = lines.Sum(l => Amount(l, "LineExtensionAmount"));
        var lineTax = lines.Sum(l => Amount(l.Element(cac + "TaxTotal"), "TaxAmount"));
        var taxTotals = invoice.Elements(cac + "TaxTotal").ToList();
        var taxAmount = taxTotals.Count > 0 ? Amount(taxTotals[0], "TaxAmount") : 0m;
        if (Math.Abs(lineSum - Amount(totals, "LineExtensionAmount")) > 0.01m)
        {
            errors.Add(Message("BR-CO-10", "The sum of the lines is not the line extension amount."));
        }

        if (Math.Abs(Amount(totals, "TaxExclusiveAmount") + taxAmount - Amount(totals, "TaxInclusiveAmount")) > 0.01m)
        {
            errors.Add(Message("BR-CO-15", "The tax inclusive amount is not the tax exclusive amount plus the tax."));
        }

        if (Math.Abs(Amount(totals, "PayableAmount") - Amount(totals, "TaxInclusiveAmount")) > 0.01m)
        {
            errors.Add(Message("BR-CO-16", "The payable amount is not the tax inclusive amount (nothing is prepaid)."));
        }

        if (Math.Abs(lineTax - taxAmount) > 0.01m)
        {
            warnings.Add(Message("BR-KSA-EN16931-08", "The lines' tax does not add up to the tax total."));
        }

        if (taxTotals.Count < 2 || !taxTotals[1].Elements(cac + "TaxSubtotal").Any())
        {
            errors.Add(Message("BR-KSA-80", "The tax total by category is missing."));
        }

        string? qr = null;
        if (simplified)
        {
            var signature = invoice.Descendants(ZatcaCrypto.Ds + "SignatureValue").FirstOrDefault()?.Value.Trim();
            var certificateText = invoice.Descendants(ZatcaCrypto.Ds + "X509Certificate").FirstOrDefault()?.Value.Trim();
            var digest = invoice.Descendants(ZatcaCrypto.Ds + "Reference").FirstOrDefault(r => r.Attribute("Id")?.Value == "invoiceSignedData")?.Element(ZatcaCrypto.Ds + "DigestValue")?.Value.Trim();
            qr = references.TryGetValue("QR", out var qrReference) ? qrReference.Element(cac + "Attachment")?.Element(cbc + "EmbeddedDocumentBinaryObject")?.Value.Trim() : null;
            if (signature is null || certificateText is null)
            {
                errors.Add(Message("BR-KSA-30", "A simplified invoice carries the seller's signature and certificate."));
            }
            else
            {
                if (!string.Equals(digest, computedHash, StringComparison.Ordinal))
                {
                    errors.Add(Message("BR-KSA-60", "The signed digest is not the invoice hash."));
                }

                X509Certificate2? certificate = null;
                try
                {
                    certificate = ZatcaCrypto.LoadCertificate(certificateText);
                }
                catch (CryptographicException)
                {
                    errors.Add(Message("BR-KSA-61", "The certificate in the signature cannot be read."));
                }
                catch (FormatException)
                {
                    errors.Add(Message("BR-KSA-61", "The certificate in the signature cannot be read."));
                }

                if (certificate is not null)
                {
                    using var publicKey = certificate.GetECDsaPublicKey();
                    if (publicKey is null || !ZatcaCrypto.VerifyHashSignature(publicKey, computedHash, signature))
                    {
                        errors.Add(Message("BR-KSA-62", "The signature does not verify against the certificate for this invoice hash."));
                    }

                    if (!string.Equals(certificate.Issuer, _ca.Subject, StringComparison.Ordinal))
                    {
                        warnings.Add(Message("CSID-ISSUER", "The certificate was not issued by the simulator's authority."));
                    }

                    if (deviceCertificate is not null && !string.Equals(certificate.Thumbprint, deviceCertificate.Thumbprint, StringComparison.Ordinal))
                    {
                        errors.Add(Message("CSID-MISMATCH", "The invoice is signed with a certificate other than the one that authenticated the call."));
                    }
                }

                if (qr is null)
                {
                    errors.Add(Message("BR-KSA-27", "A simplified invoice carries the QR code."));
                }
                else
                {
                    try
                    {
                        var tags = ZatcaCrypto.ReadQr(qr);
                        if (!tags.TryGetValue(6, out var tag6) || !string.Equals(tag6, computedHash, StringComparison.Ordinal))
                        {
                            errors.Add(Message("BR-KSA-28", "The QR's sixth tag is not the invoice hash."));
                        }

                        if (!tags.TryGetValue(7, out var tag7) || !string.Equals(tag7, signature, StringComparison.Ordinal))
                        {
                            errors.Add(Message("BR-KSA-28", "The QR's seventh tag is not the signature."));
                        }

                        if (!tags.ContainsKey(8) || !tags.ContainsKey(9))
                        {
                            errors.Add(Message("BR-KSA-28", "The QR of a simplified invoice carries the public key and the certificate's signature."));
                        }
                    }
                    catch (FormatException)
                    {
                        errors.Add(Message("BR-KSA-27", "The QR code is not base64 TLV."));
                    }
                }
            }
        }
        else if (invoice.Element(ZatcaCrypto.Ext + "UBLExtensions") is not null)
        {
            warnings.Add(Message("BR-KSA-30", "A standard invoice is signed by the scheme on clearance; the seller's signature is ignored."));
        }

        var status = errors.Count > 0 ? "ERROR" : warnings.Count > 0 ? "WARNING" : "PASS";
        string? clearedInvoice = null;
        if (errors.Count == 0 && !simplified)
        {
            clearedInvoice = Convert.ToBase64String(Encoding.UTF8.GetBytes(Clear(xml, invoice, sellerVat ?? string.Empty, computedHash, taxAmount, Amount(totals, "TaxInclusiveAmount"))));
        }

        var outcome = errors.Count > 0 ? (clearance ? "NOT_CLEARED" : "NOT_REPORTED") : clearance ? "CLEARED" : "REPORTED";
        var httpStatus = errors.Count > 0 ? HttpStatusCode.BadRequest : warnings.Count > 0 ? HttpStatusCode.Accepted : HttpStatusCode.OK;
        var answer = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["validationResults"] = new { infoMessages = Array.Empty<object>(), warningMessages = warnings, errorMessages = errors, status },
            ["requestID"] = _clock.UtcNow.ToUnixTimeMilliseconds(),
            ["simulator"] = Marker,
        };
        if (compliance)
        {
            answer[simplified ? "reportingStatus" : "clearanceStatus"] = outcome.Replace("NOT_", "NOT_", StringComparison.Ordinal);
            if (!simplified)
            {
                answer["clearedInvoice"] = clearedInvoice;
            }
        }
        else if (clearance)
        {
            answer["clearanceStatus"] = outcome;
            answer["clearedInvoice"] = clearedInvoice;
        }
        else
        {
            answer["reportingStatus"] = outcome;
        }

        return Reply(httpStatus, answer);
    }

    /// <summary>The scheme's own stamp on a cleared standard invoice: its signature block and the QR it computed, as the seller must print them.</summary>
    private string Clear(string xml, XElement invoice, string sellerVat, string hash, decimal taxTotal, decimal totalWithTax)
    {
        var cbc = ZatcaCrypto.Cbc;
        var cac = ZatcaCrypto.Cac;
        var sellerName = invoice.Element(cac + "AccountingSupplierParty")?.Element(cac + "Party")?.Element(cac + "PartyLegalEntity")?.Element(cbc + "RegistrationName")?.Value ?? string.Empty;
        var issued = DateTimeOffset.TryParse($"{invoice.Element(cbc + "IssueDate")?.Value}T{invoice.Element(cbc + "IssueTime")?.Value}Z", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when : _clock.UtcNow;
        var signature = ZatcaCrypto.SignHash(_caKey, hash);
        var qr = ZatcaCrypto.BuildQr(new QrFacts(sellerName, sellerVat, issued, totalWithTax, taxTotal, hash, signature, _ca.GetECDsaPublicKey()?.ExportSubjectPublicKeyInfo(), ZatcaCrypto.CertificateSignature(_ca)));
        var qrReference = $"    <cac:AdditionalDocumentReference>\n        <cbc:ID>QR</cbc:ID>\n        <cac:Attachment>\n            <cbc:EmbeddedDocumentBinaryObject mimeCode=\"text/plain\">{qr}</cbc:EmbeddedDocumentBinaryObject>\n        </cac:Attachment>\n    </cac:AdditionalDocumentReference>\n    <cac:Signature>\n        <cbc:ID>urn:oasis:names:specification:ubl:signature:Invoice</cbc:ID>\n        <cbc:SignatureMethod>urn:oasis:names:specification:ubl:dsig:enveloped:xades</cbc:SignatureMethod>\n    </cac:Signature>\n";
        var marker = "    <cac:AccountingSupplierParty>";
        var at = xml.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? xml : xml[..at] + qrReference + xml[at..];
    }

    private static object Message(string code, string message) => new { type = "ERROR", code, category = "SIMULATOR", message, status = "ERROR" };

    private static HttpResponseMessage Reply(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json") };

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ca.Dispose();
            _caKey.Dispose();
        }

        base.Dispose(disposing);
    }
}
