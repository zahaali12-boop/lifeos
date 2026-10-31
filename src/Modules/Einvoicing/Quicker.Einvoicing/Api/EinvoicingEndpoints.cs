using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Einvoicing.Application;
using Quicker.Web;

namespace Quicker.Einvoicing.Api;

/// <summary>E-invoicing under /api/v1/einvoicing: the devices a company onboards with ZATCA and the submissions of its documents.</summary>
public static class EinvoicingEndpoints
{
    public static RouteGroupBuilder MapEinvoicingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var einvoicing = api.MapGroup("/einvoicing").WithTags("E-invoicing").RequireAuthorization();

        var devices = einvoicing.MapGroup("/devices");
        devices.MapGet("/", async (Guid? companyId, DeviceService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceRead)
            .WithSummary("The e-invoicing devices of a company with their status, certificate and counter; secrets are never returned.");
        devices.MapPost("/", async (SaveDeviceRequest request, DeviceService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static d => $"/api/v1/einvoicing/devices/{d.Id}"))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Creates a device: its key and certificate request, named after the company's VAT registration in the regime that uses the scheme.");
        devices.MapGet("/{deviceId:guid}", async (Guid deviceId, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(deviceId, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceRead);
        devices.MapPut("/{deviceId:guid}", async (Guid deviceId, SaveDeviceRequest request, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(deviceId, request, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Changes the device's name, the seller details printed on its invoices and whether posted documents are submitted on their own; the request itself while nothing is issued yet.");
        devices.MapPost("/{deviceId:guid}/compliance-csid", async (Guid deviceId, OtpRequest request, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.RequestComplianceCsidAsync(deviceId, request, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Requests the compliance certificate with the OTP from the scheme's portal.");
        devices.MapPost("/{deviceId:guid}/compliance-checks", async (Guid deviceId, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.RunComplianceChecksAsync(deviceId, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Sends one sample of each document kind the device will issue to the scheme's compliance check; the device is checked when every sample is accepted.");
        devices.MapPost("/{deviceId:guid}/production-csid", async (Guid deviceId, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.RequestProductionCsidAsync(deviceId, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Requests the production certificate once the compliance checks passed; the device is live from here.");
        devices.MapPost("/{deviceId:guid}/renew", async (Guid deviceId, OtpRequest request, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.RenewAsync(deviceId, request, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Renews a live device's production certificate with a fresh OTP.");
        devices.MapPost("/{deviceId:guid}/revoke", async (Guid deviceId, DeviceService service, CancellationToken ct) => ApiProblems.Ok(await service.RevokeAsync(deviceId, ct)))
            .RequirePermission(EinvoicingPermissions.DeviceManage)
            .WithSummary("Takes the device out of service.");

        var submissions = einvoicing.MapGroup("/submissions");
        submissions.MapGet("/", async (Guid? companyId, string? documentType, Guid? documentId, string? status, SubmissionService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, documentType, documentId, status, ct)))
            .RequirePermission(EinvoicingPermissions.SubmissionRead)
            .WithSummary("Submissions, newest first: one row per attempt with the counter, the hash, the status and what the scheme said.");
        submissions.MapGet("/{submissionId:guid}", async (Guid submissionId, SubmissionService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(submissionId, ct)))
            .RequirePermission(EinvoicingPermissions.SubmissionRead);
        submissions.MapGet("/{submissionId:guid}/xml", async (Guid submissionId, bool? cleared, SubmissionService service, CancellationToken ct) =>
                ApiProblems.From(await service.XmlAsync(submissionId, cleared ?? false, ct), static x => Results.File(System.Text.Encoding.UTF8.GetBytes(x.Xml), "application/xml", x.FileName)))
            .Produces<string>(StatusCodes.Status200OK, "application/xml")
            .RequirePermission(EinvoicingPermissions.SubmissionRead)
            .WithSummary("The XML as submitted (signed for a simplified invoice), or with cleared=true as the scheme returned it for a cleared one.");
        submissions.MapGet("/{submissionId:guid}/qr.svg", async (Guid submissionId, SubmissionService service, CancellationToken ct) =>
                ApiProblems.From(await service.QrSvgAsync(submissionId, ct), static svg => Results.Text(svg, "image/svg+xml")))
            .Produces<string>(StatusCodes.Status200OK, "image/svg+xml")
            .RequirePermission(EinvoicingPermissions.SubmissionRead)
            .WithSummary("The submission's QR code as an SVG.");
        einvoicing.MapGet("/documents/{documentType}/{documentId:guid}/preview", async (string documentType, Guid documentId, SubmissionService service, CancellationToken ct) => ApiProblems.Ok(await service.PreviewAsync(documentType, documentId, ct)))
            .RequirePermission(EinvoicingPermissions.SubmissionRead)
            .WithSummary("The XML the adapter would submit for the document, unsigned, with the counter the next submission would take.");

        return api;
    }
}
