using Quicker.Persistence.EntityFramework;

namespace Quicker.Einvoicing.Domain;

/// <summary>The environments a device talks to: the in-process simulator, ZATCA's developer sandbox, its simulation portal, or production.</summary>
public static class DeviceEnvironments
{
    public const string Simulator = "simulator";
    public const string Sandbox = "sandbox";
    public const string Simulation = "simulation";
    public const string Production = "production";
    public static readonly IReadOnlyList<string> All = [Simulator, Sandbox, Simulation, Production];
}

/// <summary>A device's life: a key and CSR, a compliance certificate, the compliance checks passed, a production certificate, or revoked.</summary>
public static class DeviceStatuses
{
    public const string Csr = "csr";
    public const string Compliance = "compliance";
    public const string Checked = "checked";
    public const string Active = "active";
    public const string Revoked = "revoked";
}

public static class SubmissionStatuses
{
    public const string Pending = "pending";
    public const string Cleared = "cleared";
    public const string Reported = "reported";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
}

/// <summary>app.einv_devices: an e-invoicing device (ZATCA "EGS unit") of a company, with its key, certificates and hash chain.</summary>
public sealed class EinvoiceDevice : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public string Scheme { get; set; } = "zatca";

    public string Name { get; set; } = string.Empty;

    public string Environment { get; set; } = DeviceEnvironments.Simulator;

    public string Status { get; set; } = DeviceStatuses.Csr;

    public string SerialNumber { get; set; } = string.Empty;

    public string CommonName { get; set; } = string.Empty;

    public string OrganizationUnit { get; set; } = string.Empty;

    public string Organization { get; set; } = string.Empty;

    public string Industry { get; set; } = string.Empty;

    /// <summary>The ZATCA invoice-type flags of the certificate: standard, simplified, then two reserved digits ("1100" both kinds, "1000" standard only, "0100" simplified only).</summary>
    public string InvoiceTypes { get; set; } = "1100";

    /// <summary>The seller as printed on the invoice: registration number and address parts, as JSON.</summary>
    public string Seller { get; set; } = "{}";

    public bool AutoSubmit { get; set; } = true;

    public string? Csr { get; set; }

    public string PrivateKeyEnc { get; set; } = string.Empty;

    public string? ComplianceRequestId { get; set; }

    public string? ComplianceTokenEnc { get; set; }

    public string? ComplianceSecretEnc { get; set; }

    public string? ProductionTokenEnc { get; set; }

    public string? ProductionSecretEnc { get; set; }

    /// <summary>The certificate in use (compliance until the production one arrives), base64 DER.</summary>
    public string? Certificate { get; set; }

    public DateTimeOffset? CertificateExpiresAt { get; set; }

    public string ComplianceChecks { get; set; } = "[]";

    public long InvoiceCounter { get; set; }

    public string? LastInvoiceHash { get; set; }

    public Guid? CreatedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>app.einv_submissions: one attempt to clear or report a document, with the XML as signed and as the scheme returned it.</summary>
public sealed class EinvoiceSubmission : ITenantEntity
{
    public Guid TenantId { get; set; }

    public Guid Id { get; set; }

    public Guid CompanyId { get; set; }

    public Guid DeviceId { get; set; }

    public string DocumentType { get; set; } = string.Empty;

    public Guid DocumentId { get; set; }

    public string DocumentNumber { get; set; } = string.Empty;

    /// <summary>standard (cleared by the scheme) or simplified (signed here and reported).</summary>
    public string Kind { get; set; } = "standard";

    /// <summary>UN/CEFACT 388 invoice, 381 credit note, 383 debit note.</summary>
    public string TypeCode { get; set; } = "388";

    public long InvoiceCounter { get; set; }

    public string InvoiceHash { get; set; } = string.Empty;

    public string PreviousHash { get; set; } = string.Empty;

    public string Status { get; set; } = SubmissionStatuses.Pending;

    public string Warnings { get; set; } = "[]";

    public string Errors { get; set; } = "[]";

    public string SignedXml { get; set; } = string.Empty;

    public string? ClearedXml { get; set; }

    public string? Qr { get; set; }

    public string? RequestId { get; set; }

    public DateTimeOffset SubmittedAt { get; set; }

    public DateTimeOffset? RespondedAt { get; set; }

    public Guid? CreatedBy { get; set; }
}
