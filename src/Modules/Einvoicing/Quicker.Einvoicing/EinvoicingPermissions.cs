using Quicker.Identity.Contracts;

namespace Quicker.Einvoicing;

/// <summary>Permission keys of e-invoicing: the devices a company onboards with the scheme and the submissions of its documents; the accountant template grants <c>einvoicing.*</c>.</summary>
public static class EinvoicingPermissions
{
    public const string DeviceRead = "einvoicing.device.read";
    public const string DeviceManage = "einvoicing.device.manage";
    public const string SubmissionRead = "einvoicing.submission.read";

    public static readonly PermissionDefinition[] All =
    [
        new(DeviceRead, "einvoicing", "Read the e-invoicing devices of a company: their status, certificate and invoice counter"),
        new(DeviceManage, "einvoicing", "Onboard e-invoicing devices: request certificates with an OTP, run compliance checks, renew and revoke"),
        new(SubmissionRead, "einvoicing", "Read e-invoice submissions with what the scheme answered, and download the signed and cleared XML"),
    ];
}
