namespace Quicker.Einvoicing.Application;

/// <summary>The seller as the scheme prints it on every invoice of the device: the commercial registration and the address parts ZATCA requires.</summary>
public sealed record SellerDetails(
    string? RegistrationNumber = null,
    string? RegistrationScheme = "CRN",
    string? Street = null,
    string? BuildingNumber = null,
    string? Plot = null,
    string? District = null,
    string? City = null,
    string? PostalCode = null);

public sealed record SaveDeviceRequest(
    Guid CompanyId,
    string Name,
    string Environment = "simulator",
    string? OrganizationUnit = null,
    string? Industry = null,
    string InvoiceTypes = "1100",
    SellerDetails? Seller = null,
    bool AutoSubmit = true,
    string? Model = null);

public sealed record OtpRequest(string Otp);

/// <summary>One sample document the compliance checks sent and what the scheme answered.</summary>
public sealed record ComplianceCheck(string Kind, string TypeCode, string Status, IReadOnlyList<string> Warnings, IReadOnlyList<string> Errors, DateTimeOffset CheckedAt);

public sealed record EinvoiceDeviceSummary(
    Guid Id,
    Guid CompanyId,
    string Scheme,
    string Name,
    string Environment,
    string Status,
    string SerialNumber,
    string CommonName,
    string OrganizationUnit,
    string Organization,
    string Industry,
    string InvoiceTypes,
    string VatNumber,
    SellerDetails Seller,
    bool AutoSubmit,
    string? Csr,
    string? CertificateSubject,
    string? CertificateIssuer,
    DateTimeOffset? CertificateExpiresAt,
    IReadOnlyList<ComplianceCheck> ComplianceChecks,
    long InvoiceCounter,
    string? LastInvoiceHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EinvoiceSubmissionSummary(
    Guid Id,
    Guid CompanyId,
    Guid DeviceId,
    string DeviceName,
    string DocumentType,
    Guid DocumentId,
    string DocumentNumber,
    string Kind,
    string TypeCode,
    long InvoiceCounter,
    string InvoiceHash,
    string PreviousHash,
    string Status,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    bool HasClearedXml,
    string? Qr,
    string? RequestId,
    DateTimeOffset SubmittedAt,
    DateTimeOffset? RespondedAt);

/// <summary>The XML of a document as the scheme would receive it, before any submission: to inspect what the adapter builds.</summary>
public sealed record EinvoicePreview(string DocumentType, Guid DocumentId, string Kind, string TypeCode, string Xml, string InvoiceHash);
