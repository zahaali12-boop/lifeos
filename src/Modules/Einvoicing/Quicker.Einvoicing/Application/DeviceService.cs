using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Einvoicing.Contracts;
using Quicker.Einvoicing.Domain;
using Quicker.Einvoicing.Persistence;
using Quicker.Einvoicing.Zatca;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Tax.Contracts;

namespace Quicker.Einvoicing.Application;

/// <summary>
/// The devices a company onboards with ZATCA (A-179): each is a key pair and a certificate request named after the
/// company's VAT registration; a six-digit OTP from the scheme's portal buys the compliance certificate; the compliance
/// checks send one sample of each document kind the device will issue; the production certificate then makes the
/// device live, to be renewed before it expires. Secrets are stored protected and never shown.
/// </summary>
public sealed class DeviceService(
    EinvoicingDbContext db,
    ZatcaGateway gateway,
    SubmissionService submissions,
    ICompanyDirectory companies,
    ITaxDirectory taxDirectory,
    ISecretProtector secrets,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = "einvoice_device";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public async Task<IReadOnlyList<EinvoiceDeviceSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Devices.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(d => d.CompanyId == c);
        }

        var devices = await query.OrderBy(static d => d.Name).ToListAsync(cancellationToken);
        var result = new List<EinvoiceDeviceSummary>(devices.Count);
        foreach (var device in devices)
        {
            result.Add(await MapAsync(device, cancellationToken));
        }

        return result;
    }

    public async Task<Result<EinvoiceDeviceSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return device is null ? Error.NotFound(AuditEntity, id) : await MapAsync(device, cancellationToken);
    }

    /// <summary>A new device: its key and certificate request, named after the company's VAT registration in the regime that uses the scheme.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> CreateAsync(SaveDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("einvoice.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var checks = Validate(request);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        var registration = await taxDirectory.FindRegistrationAsync(company.Id.Value, "zatca", cancellationToken);
        if (registration?.RegistrationNumber is not { Length: > 0 } vat)
        {
            return Error.Validation("einvoice.not_registered", "The company has no VAT registration in a regime that uses the ZATCA scheme; register it in the tax set-up first.").WithWhy(("companyId", request.CompanyId));
        }

        if (vat.Length != 15 || !vat.All(char.IsAsciiDigit) || vat[0] != '3' || vat[^1] != '3')
        {
            return Error.Validation("einvoice.vat_number_invalid", "A Saudi VAT registration number is 15 digits starting and ending with 3.").WithWhy(("registrationNumber", vat));
        }

        var seller = request.Seller ?? new SellerDetails();
        var address = company.Address ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var registrations = company.RegistrationNumbers ?? new Dictionary<string, string>(StringComparer.Ordinal);
        seller = seller with
        {
            RegistrationNumber = Default(seller.RegistrationNumber, registrations.GetValueOrDefault("crn")),
            RegistrationScheme = string.IsNullOrWhiteSpace(seller.RegistrationScheme) ? "CRN" : seller.RegistrationScheme.Trim(),
            Street = Default(seller.Street, address.GetValueOrDefault("street") ?? address.GetValueOrDefault("line1")),
            BuildingNumber = Default(seller.BuildingNumber, address.GetValueOrDefault("buildingNumber")),
            Plot = Default(seller.Plot, address.GetValueOrDefault("plot")),
            District = Default(seller.District, address.GetValueOrDefault("district")),
            City = Default(seller.City, address.GetValueOrDefault("city")),
            PostalCode = Default(seller.PostalCode, address.GetValueOrDefault("postalCode")),
        };

        using var key = ZatcaCrypto.CreateKey();
        var device = new EinvoiceDevice
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            Name = request.Name.Trim(),
            Environment = request.Environment.Trim().ToLowerInvariant(),
            Status = DeviceStatuses.Csr,
            CommonName = Sanitize(company.Code + "-" + request.Name.Trim()),
            OrganizationUnit = Default(request.OrganizationUnit, company.LegalName.Resolve("en")) ?? company.Code,
            Organization = company.LegalName.Resolve("en"),
            Industry = Default(request.Industry, "Trading") ?? "Trading",
            InvoiceTypes = request.InvoiceTypes,
            Seller = JsonSerializer.Serialize(seller, Json),
            AutoSubmit = request.AutoSubmit,
            PrivateKeyEnc = secrets.ProtectString(ZatcaCrypto.ExportKey(key)),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        device.SerialNumber = $"1-Quicker|2-{Sanitize(Default(request.Model, "ERP") ?? "ERP")}|3-{device.Id:N}";
        device.Csr = ZatcaCrypto.BuildCsr(key, Profile(device, vat, seller, company.Country), production: device.Environment == DeviceEnvironments.Production);
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.Created, After: new { device.Environment, device.SerialNumber, device.InvoiceTypes, vat }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    /// <summary>Changes what a device may change after its certificate: its name, the seller's details printed on invoices and whether posted documents are submitted on their own.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> UpdateAsync(Guid id, SaveDeviceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        var checks = Validate(request);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        device.Name = request.Name.Trim();
        device.AutoSubmit = request.AutoSubmit;
        if (request.Seller is { } seller)
        {
            device.Seller = JsonSerializer.Serialize(seller with { RegistrationScheme = string.IsNullOrWhiteSpace(seller.RegistrationScheme) ? "CRN" : seller.RegistrationScheme.Trim() }, Json);
        }

        if (device.Status == DeviceStatuses.Csr)
        {
            // Nothing was issued yet: the certificate request can still change.
            var company = await companies.FindAsync(new CompanyId(device.CompanyId), cancellationToken);
            var registration = await taxDirectory.FindRegistrationAsync(device.CompanyId, "zatca", cancellationToken);
            if (company is not null && registration?.RegistrationNumber is { Length: 15 } vat)
            {
                device.Environment = request.Environment.Trim().ToLowerInvariant();
                device.OrganizationUnit = Default(request.OrganizationUnit, device.OrganizationUnit) ?? device.OrganizationUnit;
                device.Industry = Default(request.Industry, device.Industry) ?? device.Industry;
                device.InvoiceTypes = request.InvoiceTypes;
                using var key = ZatcaCrypto.ImportKey(secrets.UnprotectString(device.PrivateKeyEnc));
                device.Csr = ZatcaCrypto.BuildCsr(key, Profile(device, vat, SellerOf(device), company.Country), production: device.Environment == DeviceEnvironments.Production);
            }
        }

        device.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.Updated, After: new { device.Environment, device.AutoSubmit, device.InvoiceTypes }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    /// <summary>The compliance certificate, bought with the OTP the scheme's portal shows for the company.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> RequestComplianceCsidAsync(Guid id, OtpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (device.Status is DeviceStatuses.Active or DeviceStatuses.Revoked)
        {
            return Error.Conflict("einvoice.device_state", "The device already has its production certificate, or is revoked.").WithWhy(("status", device.Status));
        }

        if (string.IsNullOrWhiteSpace(request.Otp) || device.Csr is null)
        {
            return Error.Validation("einvoice.otp_required", "The OTP from the scheme's portal is required.");
        }

        ZatcaCsid csid;
        try
        {
            csid = await gateway.For(device).ComplianceCsidAsync(device.Csr, request.Otp.Trim(), cancellationToken);
        }
        catch (ZatcaRefusedException e)
        {
            return Error.Validation("einvoice.csid_refused", "The scheme refused the certificate request.").WithWhy(("detail", e.Detail), ("status", e.Message));
        }
        catch (ZatcaUnavailableException e)
        {
            return Error.Conflict("einvoice.unreachable", e.Message);
        }

        device.ComplianceRequestId = csid.RequestId;
        device.ComplianceTokenEnc = secrets.ProtectString(csid.Token);
        device.ComplianceSecretEnc = secrets.ProtectString(csid.Secret);
        device.Certificate = csid.CertificateBase64;
        device.CertificateExpiresAt = ExpiryOf(csid.CertificateBase64);
        device.Status = DeviceStatuses.Compliance;
        device.ComplianceChecks = "[]";
        device.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.StateChanged, After: new { device.Status, requestId = csid.RequestId }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    /// <summary>One sample of each document kind the device will issue, sent to the scheme's compliance check; the device is checked when every sample is accepted.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> RunComplianceChecksAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (device.Status is not (DeviceStatuses.Compliance or DeviceStatuses.Checked))
        {
            return Error.Conflict("einvoice.device_state", "The compliance checks run once the device has its compliance certificate and before its production one.").WithWhy(("status", device.Status));
        }

        var company = await companies.FindAsync(new CompanyId(device.CompanyId), cancellationToken);
        var registration = await taxDirectory.FindRegistrationAsync(device.CompanyId, "zatca", cancellationToken);
        if (company is null || registration?.RegistrationNumber is not { } vat)
        {
            return Error.Validation("einvoice.not_registered", "The company is no longer registered in a regime that uses the ZATCA scheme.");
        }

        var results = new List<ComplianceCheck>();
        var seller = SellerOf(device);
        var sellerParty = new EinvoiceParty(company.LegalName.Resolve("en"), vat, seller.RegistrationNumber, seller.RegistrationScheme, seller.Street, seller.BuildingNumber, seller.Plot, seller.District, seller.City, seller.PostalCode, company.Country);
        var kinds = new List<(bool Simplified, string Kind)>();
        if (device.InvoiceTypes.Length > 0 && device.InvoiceTypes[0] == '1')
        {
            kinds.AddRange([(false, EinvoiceDocumentKinds.Invoice), (false, EinvoiceDocumentKinds.CreditNote), (false, EinvoiceDocumentKinds.DebitNote)]);
        }

        if (device.InvoiceTypes.Length > 1 && device.InvoiceTypes[1] == '1')
        {
            kinds.AddRange([(true, EinvoiceDocumentKinds.Invoice), (true, EinvoiceDocumentKinds.CreditNote), (true, EinvoiceDocumentKinds.DebitNote)]);
        }

        var sample = 0;
        foreach (var (simplified, kind) in kinds)
        {
            sample++;
            var buyer = simplified ? null : new EinvoiceParty("Compliance check customer", "399999999900003", "1010000000", "CRN", "King Fahd Road", "1234", null, "Al Olaya", "Riyadh", "12211", "SA");
            var number = $"CHK-{sample:00}";
            var document = new EinvoiceDocument(
                SubmissionService.ComplianceSampleType, Guid.CreateVersion7(), device.CompanyId, number, kind, DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), clock.UtcNow, "SAR",
                sellerParty, buyer,
                [new EinvoiceLine(1, "Compliance check item", 2m, "PCE", 50m, 0m, 100m, EinvoiceTaxCategories.Standard, 15m, 15m, null, null)],
                100m, 15m, 115m, null, kind == EinvoiceDocumentKinds.Invoice ? null : "CHK-01", kind == EinvoiceDocumentKinds.Invoice ? null : DateOnly.FromDateTime(clock.UtcNow.UtcDateTime), kind == EinvoiceDocumentKinds.Invoice ? null : "Compliance check", DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
            var submitted = await submissions.SubmitAsync(device, document, simplified ? "simplified" : "standard", cancellationToken);
            if (submitted.IsFailure)
            {
                return submitted.Error!;
            }

            var s = submitted.Value;
            results.Add(new ComplianceCheck(simplified ? "simplified" : "standard", s.TypeCode, s.Status, s.Warnings, s.Errors, clock.UtcNow));
        }

        device.ComplianceChecks = JsonSerializer.Serialize(results, Json);
        device.Status = results.All(static r => r.Status is SubmissionStatuses.Cleared or SubmissionStatuses.Reported) ? DeviceStatuses.Checked : DeviceStatuses.Compliance;
        device.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.StateChanged, After: new { device.Status, checks = results.Count, passed = results.Count(static r => r.Status is SubmissionStatuses.Cleared or SubmissionStatuses.Reported) }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    /// <summary>The production certificate: the device is live from here, submitting to clearance and reporting.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> RequestProductionCsidAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (device.Status != DeviceStatuses.Checked || device.ComplianceTokenEnc is null || device.ComplianceSecretEnc is null || device.ComplianceRequestId is null)
        {
            return Error.Conflict("einvoice.device_state", "The production certificate follows the compliance checks.").WithWhy(("status", device.Status));
        }

        ZatcaCsid csid;
        try
        {
            csid = await gateway.For(device).ProductionCsidAsync(secrets.UnprotectString(device.ComplianceTokenEnc), secrets.UnprotectString(device.ComplianceSecretEnc), device.ComplianceRequestId, cancellationToken);
        }
        catch (ZatcaRefusedException e)
        {
            return Error.Validation("einvoice.csid_refused", "The scheme refused the production certificate.").WithWhy(("detail", e.Detail), ("status", e.Message));
        }
        catch (ZatcaUnavailableException e)
        {
            return Error.Conflict("einvoice.unreachable", e.Message);
        }

        device.ProductionTokenEnc = secrets.ProtectString(csid.Token);
        device.ProductionSecretEnc = secrets.ProtectString(csid.Secret);
        device.Certificate = csid.CertificateBase64;
        device.CertificateExpiresAt = ExpiryOf(csid.CertificateBase64);
        device.Status = DeviceStatuses.Active;
        device.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.StateChanged, After: new { device.Status, expires = device.CertificateExpiresAt }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    /// <summary>A new production certificate for a live device before the old one expires, with a fresh OTP.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> RenewAsync(Guid id, OtpRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (device.Status != DeviceStatuses.Active || device.ProductionTokenEnc is null || device.ProductionSecretEnc is null)
        {
            return Error.Conflict("einvoice.device_state", "Only a live device renews its certificate.").WithWhy(("status", device.Status));
        }

        if (string.IsNullOrWhiteSpace(request.Otp))
        {
            return Error.Validation("einvoice.otp_required", "The OTP from the scheme's portal is required.");
        }

        var company = await companies.FindAsync(new CompanyId(device.CompanyId), cancellationToken);
        var registration = await taxDirectory.FindRegistrationAsync(device.CompanyId, "zatca", cancellationToken);
        if (company is null || registration?.RegistrationNumber is not { Length: 15 } vat)
        {
            return Error.Validation("einvoice.not_registered", "The company is no longer registered in a regime that uses the ZATCA scheme.");
        }

        using var key = ZatcaCrypto.ImportKey(secrets.UnprotectString(device.PrivateKeyEnc));
        var csr = ZatcaCrypto.BuildCsr(key, Profile(device, vat, SellerOf(device), company.Country), production: device.Environment == DeviceEnvironments.Production);
        ZatcaCsid csid;
        try
        {
            csid = await gateway.For(device).RenewProductionCsidAsync(secrets.UnprotectString(device.ProductionTokenEnc), secrets.UnprotectString(device.ProductionSecretEnc), request.Otp.Trim(), csr, cancellationToken);
        }
        catch (ZatcaRefusedException e)
        {
            return Error.Validation("einvoice.csid_refused", "The scheme refused the renewal.").WithWhy(("detail", e.Detail), ("status", e.Message));
        }
        catch (ZatcaUnavailableException e)
        {
            return Error.Conflict("einvoice.unreachable", e.Message);
        }

        device.Csr = csr;
        device.ProductionTokenEnc = secrets.ProtectString(csid.Token);
        device.ProductionSecretEnc = secrets.ProtectString(csid.Secret);
        device.Certificate = csid.CertificateBase64;
        device.CertificateExpiresAt = ExpiryOf(csid.CertificateBase64);
        device.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.StateChanged, After: new { renewed = true, expires = device.CertificateExpiresAt }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    /// <summary>Takes a device out of service here; the scheme keeps no revocation call, so its certificate simply stops being used.</summary>
    public async Task<Result<EinvoiceDeviceSummary>> RevokeAsync(Guid id, CancellationToken cancellationToken)
    {
        var device = await db.Devices.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (device is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        device.Status = DeviceStatuses.Revoked;
        device.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, device.Id, device.Name, AuditActions.StateChanged, After: new { device.Status }, CompanyId: device.CompanyId), cancellationToken);
        return await MapAsync(device, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    internal static SellerDetails SellerOf(EinvoiceDevice device) => JsonSerializer.Deserialize<SellerDetails>(device.Seller, Json) ?? new SellerDetails();

    /// <summary>The seller as the device prints it: the document's party with the device's registration and address parts where the device names them.</summary>
    internal static EinvoiceParty SellerOf(EinvoiceDevice device, EinvoiceParty party)
    {
        var seller = SellerOf(device);
        return party with
        {
            RegistrationNumber = Default(seller.RegistrationNumber, party.RegistrationNumber),
            RegistrationScheme = Default(seller.RegistrationScheme, party.RegistrationScheme) ?? "CRN",
            Street = Default(seller.Street, party.Street),
            BuildingNumber = Default(seller.BuildingNumber, party.BuildingNumber),
            Plot = Default(seller.Plot, party.Plot),
            District = Default(seller.District, party.District),
            City = Default(seller.City, party.City),
            PostalCode = Default(seller.PostalCode, party.PostalCode),
        };
    }

    private static CsrProfile Profile(EinvoiceDevice device, string vat, SellerDetails seller, string country)
    {
        var location = string.Join(", ", new[] { seller.Street, seller.BuildingNumber, seller.District, seller.City }.Where(static s => !string.IsNullOrWhiteSpace(s)));
        return new CsrProfile(device.CommonName, device.OrganizationUnit, device.Organization, string.IsNullOrWhiteSpace(country) ? "SA" : country, device.SerialNumber, vat, device.InvoiceTypes, location.Length == 0 ? "Saudi Arabia" : location, device.Industry);
    }

    private static Result Validate(SaveDeviceRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 80)
        {
            return Error.Validation("einvoice.name_invalid", "A device has a name of up to 80 characters.");
        }

        if (!DeviceEnvironments.All.Contains(request.Environment.Trim().ToLowerInvariant(), StringComparer.Ordinal))
        {
            return Error.Validation("einvoice.environment_invalid", "The environment is simulator, sandbox, simulation or production.").WithWhy(("environment", request.Environment));
        }

        if (request.InvoiceTypes.Length != 4 || !request.InvoiceTypes.All(static c => c is '0' or '1') || request.InvoiceTypes[0] == '0' && request.InvoiceTypes[1] == '0')
        {
            return Error.Validation("einvoice.invoice_types_invalid", "The invoice types are four flags (standard, simplified, then two reserved) with at least one of the first two set, such as 1100.").WithWhy(("invoiceTypes", request.InvoiceTypes));
        }

        return Result.Success();
    }

    private static string? Default(string? value, string? fallback) => string.IsNullOrWhiteSpace(value) ? (string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim()) : value.Trim();

    private static string Sanitize(string value)
    {
        var clean = new string(value.Select(static c => char.IsWhiteSpace(c) ? '-' : c).Where(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return clean.Length > 0 ? clean : "device";
    }

    private static DateTimeOffset? ExpiryOf(string certificateBase64)
    {
        try
        {
            using var certificate = ZatcaCrypto.LoadCertificate(certificateBase64);
            return new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private async Task<EinvoiceDeviceSummary> MapAsync(EinvoiceDevice d, CancellationToken cancellationToken)
    {
        var registration = await taxDirectory.FindRegistrationAsync(d.CompanyId, "zatca", cancellationToken);
        string? subject = null;
        string? issuer = null;
        if (d.Certificate is not null)
        {
            try
            {
                using var certificate = ZatcaCrypto.LoadCertificate(d.Certificate);
                subject = certificate.Subject;
                issuer = certificate.Issuer;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                subject = null;
            }
            catch (FormatException)
            {
                subject = null;
            }
        }

        var checks = JsonSerializer.Deserialize<List<ComplianceCheck>>(d.ComplianceChecks, Json) ?? [];
        return new EinvoiceDeviceSummary(d.Id, d.CompanyId, d.Scheme, d.Name, d.Environment, d.Status, d.SerialNumber, d.CommonName, d.OrganizationUnit, d.Organization, d.Industry, d.InvoiceTypes, registration?.RegistrationNumber ?? string.Empty, SellerOf(d), d.AutoSubmit, d.Csr, subject, issuer, d.CertificateExpiresAt, checks, d.InvoiceCounter, d.LastInvoiceHash, d.CreatedAt, d.UpdatedAt);
    }
}
