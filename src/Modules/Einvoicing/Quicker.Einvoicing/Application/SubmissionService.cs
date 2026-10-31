using System.Text.Json;
using System.Xml.Linq;
using Dapper;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using Quicker.Audit.Contracts;
using Quicker.Einvoicing.Contracts;
using Quicker.Einvoicing.Domain;
using Quicker.Einvoicing.Persistence;
using Quicker.Einvoicing.Zatca;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Persistence;
using Quicker.Tax.Contracts;

namespace Quicker.Einvoicing.Application;

/// <summary>
/// Submissions of documents to ZATCA (A-179): the document is loaded from the module that owns it, the device's
/// counter and previous hash are taken under a lock on the device row (the chain never forks), the XML is built,
/// hashed and, for a simplified invoice, signed with the device's certificate and given its QR; standard invoices go
/// to clearance and come back signed by the scheme with its QR, simplified ones to reporting; what the scheme answered
/// is recorded on the submission, one row per attempt.
/// </summary>
public sealed class SubmissionService(
    EinvoicingDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    ZatcaGateway gateway,
    IEnumerable<IEinvoiceSource> sources,
    ISecretProtector secrets,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = "einvoice_submission";

    /// <summary>The document type of the samples the compliance checks send; they take their place in the device's chain like any document.</summary>
    public const string ComplianceSampleType = "zatca_compliance_sample";

    private static readonly JsonSerializerOptions Json = DeviceService.Json;

    // ------------------------------------------------------------------ submitting

    /// <summary>Submits a document through its company's live device; <paramref name="automatic"/> marks the submission a posted document triggers on its own, which a device set to manual refuses.</summary>
    public async Task<Result<EinvoiceSubmissionSummary>> SubmitDocumentAsync(string documentType, Guid documentId, bool automatic, CancellationToken cancellationToken)
    {
        var source = sources.FirstOrDefault(s => string.Equals(s.DocumentType, documentType, StringComparison.Ordinal));
        if (source is null)
        {
            return Error.Validation("einvoice.document_type_unknown", "No module hands documents of this type to the e-invoicing adapter.").WithWhy(("documentType", documentType));
        }

        var loaded = await source.LoadAsync(documentId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var document = loaded.Value;
        var device = await db.Devices.Where(d => d.CompanyId == document.CompanyId && d.Status != DeviceStatuses.Revoked).OrderByDescending(static d => d.Status == DeviceStatuses.Active).ThenBy(static d => d.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            return Error.Validation("einvoice.no_device", "The company has no e-invoicing device; onboard one with the scheme first.").WithWhy(("companyId", document.CompanyId));
        }

        if (device.Status is DeviceStatuses.Csr)
        {
            return Error.Conflict("einvoice.device_not_active", "The device has no certificate yet; request the compliance certificate with the OTP first.").WithWhy(("deviceId", device.Id), ("status", device.Status));
        }

        if (automatic && !device.AutoSubmit)
        {
            return Error.Validation("einvoice.manual_only", "The device submits documents only when asked to.").WithWhy(("deviceId", device.Id));
        }

        var simplified = document.Buyer?.VatNumber is not { Length: > 0 };
        if (simplified && device.InvoiceTypes[1] != '1')
        {
            return Error.Validation("einvoice.simplified_not_enabled", "The customer has no VAT registration, so this is a simplified invoice, which the device's certificate does not cover.").WithWhy(("deviceId", device.Id), ("invoiceTypes", device.InvoiceTypes));
        }

        if (!simplified && device.InvoiceTypes[0] != '1')
        {
            return Error.Validation("einvoice.standard_not_enabled", "The customer is VAT registered, so this is a standard invoice, which the device's certificate does not cover.").WithWhy(("deviceId", device.Id), ("invoiceTypes", device.InvoiceTypes));
        }

        return await SubmitAsync(device, document, simplified ? "simplified" : "standard", cancellationToken);
    }

    /// <summary>Builds, signs and sends one document on a device and records the attempt; the device's counter and hash move on whatever the scheme answers.</summary>
    public async Task<Result<EinvoiceSubmissionSummary>> SubmitAsync(EinvoiceDevice device, EinvoiceDocument document, string kind, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(document);
        var (token, secret) = Credentials(device);
        if (token is null || secret is null)
        {
            return Error.Conflict("einvoice.device_not_active", "The device has no certificate to submit with.").WithWhy(("deviceId", device.Id), ("status", device.Status));
        }

        // The chain: one counter and one previous hash per device, taken under the row lock so two submissions never share them.
        var uow = unitOfWork.Current;
        var chain = await uow.Connection.QuerySingleAsync<(long Counter, string? LastHash)>(new CommandDefinition(
            "SELECT invoice_counter, last_invoice_hash FROM app.einv_devices WHERE tenant_id = @tenant AND id = @id FOR UPDATE",
            new { tenant = uow.Context.TenantId.Value, id = device.Id },
            uow.Transaction,
            cancellationToken: cancellationToken));
        var counter = chain.Counter + 1;
        var previousHash = chain.LastHash ?? ZatcaCrypto.FirstPreviousHash;
        var submission = new EinvoiceSubmission
        {
            Id = Guid.CreateVersion7(),
            CompanyId = device.CompanyId,
            DeviceId = device.Id,
            DocumentType = document.DocumentType,
            DocumentId = document.DocumentId,
            DocumentNumber = document.Number,
            Kind = kind,
            TypeCode = ZatcaInvoiceXml.TypeCodeOf(document.Kind),
            InvoiceCounter = counter,
            PreviousHash = previousHash,
            SubmittedAt = clock.UtcNow,
            CreatedBy = principal.Principal?.MembershipId.Value,
        };

        var simplified = kind == "simplified";
        document = document with { Seller = DeviceService.SellerOf(device, document.Seller) };
        var xml = ZatcaInvoiceXml.Build(document, new SubmissionFacts(submission.Id, counter, previousHash, simplified));
        string hash;
        string? qr = null;
        if (simplified)
        {
            if (device.Certificate is null)
            {
                return Error.Conflict("einvoice.device_not_active", "The device has no certificate to sign with.").WithWhy(("deviceId", device.Id));
            }

            using var key = ZatcaCrypto.ImportKey(secrets.UnprotectString(device.PrivateKeyEnc));
            var signed = ZatcaCrypto.Sign(xml, key, device.Certificate, clock.UtcNow, new QrFacts(document.Seller.Name, document.Seller.VatNumber ?? string.Empty, document.IssuedAt, document.TotalGross, document.TotalTax));
            xml = signed.Xml;
            hash = signed.InvoiceHash;
            qr = signed.Qr;
        }
        else
        {
            hash = ZatcaCrypto.InvoiceHash(xml);
        }

        submission.InvoiceHash = hash;
        submission.SignedXml = xml;
        submission.Qr = qr;
        device.InvoiceCounter = counter;
        device.LastInvoiceHash = hash;
        device.UpdatedAt = clock.UtcNow;
        db.Submissions.Add(submission);

        var api = gateway.For(device);
        ZatcaDocumentResult? answer = null;
        string? failure = null;
        try
        {
            answer = device.Status == DeviceStatuses.Active
                ? simplified ? await api.ReportAsync(token, secret, hash, submission.Id, xml, cancellationToken) : await api.ClearAsync(token, secret, hash, submission.Id, xml, cancellationToken)
                : await api.ComplianceCheckAsync(token, secret, hash, submission.Id, xml, cancellationToken);
        }
        catch (ZatcaUnavailableException e)
        {
            failure = e.Message;
        }

        submission.RespondedAt = clock.UtcNow;
        if (answer is null)
        {
            submission.Status = SubmissionStatuses.Failed;
            submission.Errors = JsonSerializer.Serialize(new[] { failure ?? "The scheme could not be reached." }, Json);
        }
        else
        {
            submission.RequestId = answer.RequestId;
            submission.Warnings = JsonSerializer.Serialize(answer.Warnings, Json);
            submission.Errors = JsonSerializer.Serialize(answer.Errors, Json);
            if (answer.Accepted && answer.Errors.Count == 0)
            {
                submission.Status = simplified ? SubmissionStatuses.Reported : SubmissionStatuses.Cleared;
                if (!simplified && answer.ClearedXml is { } cleared)
                {
                    submission.ClearedXml = cleared;
                    submission.Qr = QrOf(cleared) ?? submission.Qr;
                }
            }
            else
            {
                submission.Status = SubmissionStatuses.Rejected;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, submission.Id, document.Number, AuditActions.Created, After: new { submission.Status, submission.Kind, submission.TypeCode, submission.InvoiceCounter, device = device.Name, warnings = answer?.Warnings.Count ?? 0, errors = answer?.Errors.Count ?? (failure is null ? 0 : 1) }, CompanyId: device.CompanyId), cancellationToken);
        var mapped = Map(submission, device.Name);
        return failure is null ? mapped : Error.Conflict("einvoice.unreachable", failure).WithWhy(("submissionId", submission.Id));
    }

    /// <summary>The XML the adapter would send for a document, unsigned, with the counter the next submission would take: to inspect before posting or submitting.</summary>
    public async Task<Result<EinvoicePreview>> PreviewAsync(string documentType, Guid documentId, CancellationToken cancellationToken)
    {
        var source = sources.FirstOrDefault(s => string.Equals(s.DocumentType, documentType, StringComparison.Ordinal));
        if (source is null)
        {
            return Error.Validation("einvoice.document_type_unknown", "No module hands documents of this type to the e-invoicing adapter.").WithWhy(("documentType", documentType));
        }

        var loaded = await source.LoadAsync(documentId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var document = loaded.Value;
        var device = await db.Devices.AsNoTracking().Where(d => d.CompanyId == document.CompanyId && d.Status != DeviceStatuses.Revoked).OrderByDescending(static d => d.Status == DeviceStatuses.Active).FirstOrDefaultAsync(cancellationToken);
        var simplified = document.Buyer?.VatNumber is not { Length: > 0 };
        if (device is not null)
        {
            document = document with { Seller = DeviceService.SellerOf(device, document.Seller) };
        }

        var xml = ZatcaInvoiceXml.Build(document, new SubmissionFacts(documentId, (device?.InvoiceCounter ?? 0) + 1, device?.LastInvoiceHash ?? ZatcaCrypto.FirstPreviousHash, simplified));
        return new EinvoicePreview(documentType, documentId, simplified ? "simplified" : "standard", ZatcaInvoiceXml.TypeCodeOf(document.Kind), xml, ZatcaCrypto.InvoiceHash(xml));
    }

    // ------------------------------------------------------------------ reading

    public async Task<IReadOnlyList<EinvoiceSubmissionSummary>> ListAsync(Guid? companyId, string? documentType, Guid? documentId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Submissions.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(s => s.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(documentType))
        {
            query = query.Where(s => s.DocumentType == documentType);
        }

        if (documentId is { } d)
        {
            query = query.Where(s => s.DocumentId == d);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status == status);
        }

        var rows = await query.OrderByDescending(static s => s.SubmittedAt).Take(500).ToListAsync(cancellationToken);
        var names = await DeviceNamesAsync(rows.Select(static r => r.DeviceId).Distinct().ToList(), cancellationToken);
        return rows.Select(r => Map(r, names.GetValueOrDefault(r.DeviceId, string.Empty))).ToList();
    }

    public async Task<Result<EinvoiceSubmissionSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var submission = await db.Submissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (submission is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        var names = await DeviceNamesAsync([submission.DeviceId], cancellationToken);
        return Map(submission, names.GetValueOrDefault(submission.DeviceId, string.Empty));
    }

    /// <summary>The XML as sent (signed for a simplified invoice), or as the scheme returned it for a cleared one.</summary>
    public async Task<Result<(string FileName, string Xml)>> XmlAsync(Guid id, bool cleared, CancellationToken cancellationToken)
    {
        var submission = await db.Submissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (submission is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (cleared && submission.ClearedXml is null)
        {
            return Error.NotFound("einvoice_cleared_xml", id);
        }

        var name = new string(submission.DocumentNumber.Where(static c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
        return (cleared ? $"{name}-cleared.xml" : $"{name}.xml", cleared ? submission.ClearedXml! : submission.SignedXml);
    }

    /// <summary>The QR of a submission as an SVG, for the screen; the print pipeline draws its own from the document's payload.</summary>
    public async Task<Result<string>> QrSvgAsync(Guid id, CancellationToken cancellationToken)
    {
        var qr = await db.Submissions.AsNoTracking().Where(s => s.Id == id).Select(static s => s.Qr).SingleOrDefaultAsync(cancellationToken);
        if (qr is null)
        {
            return Error.NotFound("einvoice_qr", id);
        }

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(qr, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(4, "#000000", "#ffffff", false);
    }

    // ------------------------------------------------------------------ internals

    private (string? Token, string? Secret) Credentials(EinvoiceDevice device)
    {
        var tokenEnc = device.Status == DeviceStatuses.Active ? device.ProductionTokenEnc : device.ComplianceTokenEnc;
        var secretEnc = device.Status == DeviceStatuses.Active ? device.ProductionSecretEnc : device.ComplianceSecretEnc;
        return tokenEnc is null || secretEnc is null ? (null, null) : (secrets.UnprotectString(tokenEnc), secrets.UnprotectString(secretEnc));
    }

    /// <summary>The QR the scheme put on a cleared invoice.</summary>
    private static string? QrOf(string clearedXml)
    {
        try
        {
            var document = XDocument.Parse(clearedXml, LoadOptions.PreserveWhitespace);
            return document.Root?.Elements(ZatcaCrypto.Cac + "AdditionalDocumentReference").FirstOrDefault(static e => e.Element(ZatcaCrypto.Cbc + "ID")?.Value == "QR")?.Element(ZatcaCrypto.Cac + "Attachment")?.Element(ZatcaCrypto.Cbc + "EmbeddedDocumentBinaryObject")?.Value.Trim();
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private async Task<Dictionary<Guid, string>> DeviceNamesAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        ids.Count == 0 ? [] : await db.Devices.AsNoTracking().Where(d => ids.Contains(d.Id)).ToDictionaryAsync(static d => d.Id, static d => d.Name, cancellationToken);

    private static EinvoiceSubmissionSummary Map(EinvoiceSubmission s, string deviceName) =>
        new(s.Id, s.CompanyId, s.DeviceId, deviceName, s.DocumentType, s.DocumentId, s.DocumentNumber, s.Kind, s.TypeCode, s.InvoiceCounter, s.InvoiceHash, s.PreviousHash, s.Status,
            JsonSerializer.Deserialize<List<string>>(s.Warnings, Json) ?? [], JsonSerializer.Deserialize<List<string>>(s.Errors, Json) ?? [], s.ClearedXml is not null, s.Qr, s.RequestId, s.SubmittedAt, s.RespondedAt);
}

/// <summary>The ZATCA adapter behind the tax module's clearance interface: a document's submission, its preview, and the status of a submission.</summary>
public sealed class ZatcaClearanceProvider(SubmissionService submissions) : ITaxClearanceProvider
{
    public string Scheme => "zatca";

    public async Task<Result<ClearanceResult>> ValidateAsync(ClearanceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var preview = await submissions.PreviewAsync(document.DocumentType, document.DocumentId, cancellationToken);
        return preview.IsFailure ? preview.Error! : new ClearanceResult("preview", null, preview.Value.InvoiceHash, null, []);
    }

    public async Task<Result<ClearanceResult>> SubmitAsync(ClearanceDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var submitted = await submissions.SubmitDocumentAsync(document.DocumentType, document.DocumentId, document.Automatic, cancellationToken);
        return submitted.IsFailure ? submitted.Error! : Map(submitted.Value);
    }

    public async Task<Result<ClearanceResult>> GetStatusAsync(string submissionId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(submissionId, out var id))
        {
            return Error.NotFound(SubmissionService.AuditEntity, submissionId);
        }

        var found = await submissions.GetAsync(id, cancellationToken);
        return found.IsFailure ? found.Error! : Map(found.Value);
    }

    private static ClearanceResult Map(EinvoiceSubmissionSummary s) => new(s.Status, s.Id.ToString(), s.InvoiceHash, s.Qr, [.. s.Errors, .. s.Warnings]);
}
