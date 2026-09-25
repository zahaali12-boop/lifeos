using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Kernel.Results;
using Quicker.Tax.Application;
using Quicker.Tax.Contracts;
using Quicker.Web;

namespace Quicker.Tax.Api;

/// <summary>Tax under /api/v1/tax: country templates, regimes with their codes, rates and matrix, tax groups, registrations, exemptions, and taxing a document with the reason for every line's code.</summary>
public static class TaxEndpoints
{
    public static RouteGroupBuilder MapTaxEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var tax = api.MapGroup("/tax").WithTags("Tax").RequireAuthorization();

        tax.MapPost("/calculate", async (TaxDocumentRequest request, TaxAccess access, ITaxDetermination engine, CancellationToken ct) =>
            {
                var allowed = access.Require(request.CompanyId, TaxPermissions.SetupRead);
                Result<TaxedDocumentResult> result = allowed.IsFailure ? allowed.Error! : await engine.CalculateAsync(request, ct);
                return ApiProblems.Ok(result);
            })
            .RequirePermission(TaxPermissions.SetupRead)
            .WithSummary("Taxes a document for a company on a date: every line's code and why (exemption, matrix row, chosen, not registered), its net, tax and gross on either price basis, the tax per code and the totals");

        // ------------------------------------------------------------------ templates and regimes
        tax.MapGet("/templates", async (TaxSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListTemplatesAsync(ct)))
            .RequirePermission(TaxPermissions.SetupRead)
            .WithSummary("The country templates shipped with the product and whether each is installed");
        tax.MapPost("/templates/{templateCode}/install", async (string templateCode, TaxSetupService service, CancellationToken ct) => ApiProblems.Created(await service.InstallTemplateAsync(templateCode, ct), static r => $"/api/v1/tax/regimes/{r.Regime.Id}"))
            .RequirePermission(TaxPermissions.SetupManage)
            .WithSummary("Installs a country template: its regime, codes with dated rates and return boxes, item and partner tax groups (reusing the tenant's by code) and its determination matrix");

        tax.MapGet("/regimes", async (TaxSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListRegimesAsync(ct)))
            .RequirePermission(TaxPermissions.SetupRead);
        tax.MapGet("/regimes/{regimeId:guid}", async (Guid regimeId, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.RegimeAsync(regimeId, ct)))
            .RequirePermission(TaxPermissions.SetupRead)
            .WithSummary("A regime with its codes (rates by date, treatment, accounts, return boxes) and its determination matrix");
        tax.MapPut("/regimes/{regimeId:guid}", async (Guid regimeId, SaveTaxRegimeRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveRegimeAsync(regimeId, request, ct)))
            .RequirePermission(TaxPermissions.SetupManage);

        tax.MapPost("/regimes/{regimeId:guid}/codes", async (Guid regimeId, SaveTaxCodeRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveCodeAsync(regimeId, null, request, ct), c => $"/api/v1/tax/regimes/{regimeId}/codes/{c.Id}"))
            .RequirePermission(TaxPermissions.SetupManage)
            .WithSummary("A tax code: kind, treatment, rates by date, recoverable or not, reverse charge, the exemption reason printed, the account roles it posts to and the return boxes of its base and tax");
        tax.MapPut("/regimes/{regimeId:guid}/codes/{codeId:guid}", async (Guid regimeId, Guid codeId, SaveTaxCodeRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveCodeAsync(regimeId, codeId, request, ct)))
            .RequirePermission(TaxPermissions.SetupManage);

        tax.MapPost("/regimes/{regimeId:guid}/rules", async (Guid regimeId, SaveTaxRuleRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveRuleAsync(regimeId, null, request, ct), r => $"/api/v1/tax/regimes/{regimeId}/rules/{r.Id}"))
            .RequirePermission(TaxPermissions.SetupManage)
            .WithSummary("A row of the determination matrix: for sales or purchases, an item group, a partner group and where goods ship from and to (each blank for any), from a date, the code a line takes");
        tax.MapPut("/regimes/{regimeId:guid}/rules/{ruleId:guid}", async (Guid regimeId, Guid ruleId, SaveTaxRuleRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveRuleAsync(regimeId, ruleId, request, ct)))
            .RequirePermission(TaxPermissions.SetupManage);
        tax.MapDelete("/regimes/{regimeId:guid}/rules/{ruleId:guid}", async (Guid regimeId, Guid ruleId, TaxSetupService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteRuleAsync(regimeId, ruleId, ct)))
            .RequirePermission(TaxPermissions.SetupManage);

        // ------------------------------------------------------------------ groups
        tax.MapGet("/groups", async (string? kind, TaxSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListGroupsAsync(kind, ct)))
            .RequirePermission(TaxPermissions.SetupRead)
            .WithSummary("Item tax groups (items and categories point at them) and partner tax groups (customer and supplier accounts point at them)");
        tax.MapPost("/groups", async (SaveTaxGroupRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveGroupAsync(null, request, ct), static g => $"/api/v1/tax/groups/{g.Id}"))
            .RequirePermission(TaxPermissions.SetupManage);
        tax.MapPut("/groups/{groupId:guid}", async (Guid groupId, SaveTaxGroupRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveGroupAsync(groupId, request, ct)))
            .RequirePermission(TaxPermissions.SetupManage);

        // ------------------------------------------------------------------ registrations and exemptions
        tax.MapGet("/registrations", async (Guid? companyId, TaxSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListRegistrationsAsync(companyId, ct)))
            .RequirePermission(TaxPermissions.SetupRead);
        tax.MapPost("/registrations", async (SaveCompanyTaxRegistrationRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveRegistrationAsync(null, request, ct), static r => $"/api/v1/tax/registrations/{r.Id}"))
            .RequirePermission(TaxPermissions.SetupManage)
            .WithSummary("A company's registration in a regime: its tax number and from when; without one the company charges and recovers no tax");
        tax.MapPut("/registrations/{registrationId:guid}", async (Guid registrationId, SaveCompanyTaxRegistrationRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveRegistrationAsync(registrationId, request, ct)))
            .RequirePermission(TaxPermissions.SetupManage);

        tax.MapGet("/exemptions", async (Guid? partnerId, TaxSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListExemptionsAsync(partnerId, ct)))
            .RequirePermission(TaxPermissions.SetupRead);
        tax.MapPost("/exemptions", async (SaveTaxExemptionRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveExemptionAsync(null, request, ct), static e => $"/api/v1/tax/exemptions/{e.Id}"))
            .RequirePermission(TaxPermissions.SetupManage)
            .WithSummary("A customer's exemption certificate in a regime: while valid, the lines it would be taxed on take the exempt code it names");
        tax.MapPut("/exemptions/{exemptionId:guid}", async (Guid exemptionId, SaveTaxExemptionRequest request, TaxSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveExemptionAsync(exemptionId, request, ct)))
            .RequirePermission(TaxPermissions.SetupManage);
        tax.MapDelete("/exemptions/{exemptionId:guid}", async (Guid exemptionId, TaxSetupService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteExemptionAsync(exemptionId, ct)))
            .RequirePermission(TaxPermissions.SetupManage);

        return tax;
    }
}
