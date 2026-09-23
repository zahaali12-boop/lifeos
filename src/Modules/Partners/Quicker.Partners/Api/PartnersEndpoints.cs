using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Partners.Application;
using Quicker.Partners.Contracts;
using Quicker.Web;

namespace Quicker.Partners.Api;

/// <summary>The partner master under /api/v1/partners: partners with contacts, addresses, bank accounts and tax registrations; supplier accounts per company; groups, payment and delivery terms, withholding codes.</summary>
public static class PartnersEndpoints
{
    public static RouteGroupBuilder MapPartnersEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var partners = api.MapGroup("/partners").WithTags("Partners").RequireAuthorization();

        // ------------------------------------------------------------------ configuration (before /{partnerId} so the literal segments win)
        partners.MapGet("/supplier-groups", async (SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListGroupsAsync(ct)))
            .RequirePermission(PartnersPermissions.SupplierRead);
        partners.MapPost("/supplier-groups", async (SaveSupplierGroupRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Created(await service.SaveGroupAsync(null, request, ct), static g => $"/api/v1/partners/supplier-groups/{g.Id}"))
            .RequirePermission(PartnersPermissions.TermsManage)
            .WithSummary("A supplier group: the posting group, payment and delivery terms its members default to");
        partners.MapPut("/supplier-groups/{groupId:guid}", async (Guid groupId, SaveSupplierGroupRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveGroupAsync(groupId, request, ct)))
            .RequirePermission(PartnersPermissions.TermsManage);

        partners.MapGet("/payment-terms", async (SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListPaymentTermsAsync(ct)))
            .RequirePermission(PartnersPermissions.SupplierRead);
        partners.MapPost("/payment-terms", async (SavePaymentTermsRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Created(await service.SavePaymentTermsAsync(null, request, ct), static t => $"/api/v1/partners/payment-terms/{t.Id}"))
            .RequirePermission(PartnersPermissions.TermsManage)
            .WithSummary("Payment terms: due days from the invoice date, the end of its month or the delivery; optional instalments (percentages summing to 100), early-payment discount, business days only");
        partners.MapPut("/payment-terms/{termsId:guid}", async (Guid termsId, SavePaymentTermsRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.SavePaymentTermsAsync(termsId, request, ct)))
            .RequirePermission(PartnersPermissions.TermsManage);
        partners.MapPost("/payment-terms/{termsId:guid}/schedule", async (Guid termsId, SchedulePreviewRequest request, IPartnerDirectory directory, CancellationToken ct) =>
            ApiProblems.Ok(await directory.ScheduleAsync(request.CompanyId, termsId, request.InvoiceDate, request.DeliveryDate, request.Amount, request.Currency, ct)))
            .RequirePermission(PartnersPermissions.SupplierRead)
            .WithSummary("The due dates and instalment amounts an invoice of this amount would carry (the parts sum to the whole in the currency's minor unit)");

        partners.MapGet("/delivery-terms", async (SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListDeliveryTermsAsync(ct)))
            .RequirePermission(PartnersPermissions.SupplierRead);
        partners.MapPost("/delivery-terms", async (SaveDeliveryTermsRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Created(await service.SaveDeliveryTermsAsync(null, request, ct), static t => $"/api/v1/partners/delivery-terms/{t.Id}"))
            .RequirePermission(PartnersPermissions.TermsManage);
        partners.MapPut("/delivery-terms/{termsId:guid}", async (Guid termsId, SaveDeliveryTermsRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveDeliveryTermsAsync(termsId, request, ct)))
            .RequirePermission(PartnersPermissions.TermsManage);

        partners.MapGet("/wht-codes", async (SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListWhtCodesAsync(ct)))
            .RequirePermission(PartnersPermissions.SupplierRead);
        partners.MapPost("/wht-codes", async (SaveWhtCodeRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Created(await service.SaveWhtCodeAsync(null, request, ct), static c => $"/api/v1/partners/wht-codes/{c.Id}"))
            .RequirePermission(PartnersPermissions.TermsManage)
            .WithSummary("A withholding tax code: rate, withheld at invoice or payment, optional threshold");
        partners.MapPut("/wht-codes/{whtCodeId:guid}", async (Guid whtCodeId, SaveWhtCodeRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveWhtCodeAsync(whtCodeId, request, ct)))
            .RequirePermission(PartnersPermissions.TermsManage);

        partners.MapGet("/suppliers", async (Guid? companyId, string? q, string? holdStatus, bool? isActive, SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListAccountsAsync(companyId, q, holdStatus, isActive, ct)))
            .RequirePermission(PartnersPermissions.SupplierRead)
            .WithSummary("Supplier accounts with their partner and effective terms; holdStatus=held for every held one");

        // ------------------------------------------------------------------ partners
        partners.MapGet("/", async (string? q, string? role, bool? isActive, int? limit, string? cursor, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.ListAsync(q, role, isActive, new PageRequest(limit, cursor), ct)))
            .RequirePermission(PartnersPermissions.SupplierRead)
            .WithSummary("Partners newest first, paged: q matches the code, a name in any language or the email; role=supplier|customer|employee");
        partners.MapPost("/", async (SavePartnerRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static p => $"/api/v1/partners/{p.Id}"))
            .RequirePermission(PartnersPermissions.SupplierManage)
            .WithSummary("A partner: one record per legal person wearing the supplier, customer and employee roles; custom fields under host 'partner'");
        partners.MapGet("/by-code/{code}", async (string code, PartnerService service, CancellationToken ct) => ApiProblems.Found(await service.GetByCodeAsync(code, ct), "partner", code))
            .RequirePermission(PartnersPermissions.SupplierRead);
        partners.MapGet("/{partnerId:guid}", async (Guid partnerId, PartnerService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(partnerId, ct), "partner", partnerId))
            .RequirePermission(PartnersPermissions.SupplierRead)
            .WithSummary("The partner with contacts, addresses, masked bank accounts, tax registrations and supplier accounts");
        partners.MapPut("/{partnerId:guid}", async (Guid partnerId, SavePartnerRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(partnerId, request, ct)))
            .RequirePermission(PartnersPermissions.SupplierManage);

        var contacts = partners.MapGroup("/{partnerId:guid}/contacts").RequirePermission(PartnersPermissions.SupplierManage);
        contacts.MapPost("/", async (Guid partnerId, SaveContactRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveContactAsync(partnerId, null, request, ct), c => $"/api/v1/partners/{partnerId}/contacts/{c.Id}"));
        contacts.MapPut("/{contactId:guid}", async (Guid partnerId, Guid contactId, SaveContactRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveContactAsync(partnerId, contactId, request, ct)));
        contacts.MapDelete("/{contactId:guid}", async (Guid partnerId, Guid contactId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteContactAsync(partnerId, contactId, ct)));

        var addresses = partners.MapGroup("/{partnerId:guid}/addresses").RequirePermission(PartnersPermissions.SupplierManage);
        addresses.MapPost("/", async (Guid partnerId, SaveAddressRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAddressAsync(partnerId, null, request, ct), a => $"/api/v1/partners/{partnerId}/addresses/{a.Id}"))
            .WithSummary("role billing|shipping|legal|other; address is a structured bilingual object; one default per role");
        addresses.MapPut("/{addressId:guid}", async (Guid partnerId, Guid addressId, SaveAddressRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAddressAsync(partnerId, addressId, request, ct)));
        addresses.MapDelete("/{addressId:guid}", async (Guid partnerId, Guid addressId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAddressAsync(partnerId, addressId, ct)));

        var bankAccounts = partners.MapGroup("/{partnerId:guid}/bank-accounts");
        bankAccounts.MapPost("/", async (Guid partnerId, SaveBankAccountRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveBankAccountAsync(partnerId, null, request, ct), a => $"/api/v1/partners/{partnerId}/bank-accounts/{a.Id}"))
            .RequirePermission(PartnersPermissions.SupplierManage)
            .WithSummary("Account number and IBAN are checked (IBAN mod-97), stored encrypted and shown masked");
        bankAccounts.MapPut("/{accountId:guid}", async (Guid partnerId, Guid accountId, SaveBankAccountRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveBankAccountAsync(partnerId, accountId, request, ct)))
            .RequirePermission(PartnersPermissions.SupplierManage);
        bankAccounts.MapDelete("/{accountId:guid}", async (Guid partnerId, Guid accountId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteBankAccountAsync(partnerId, accountId, ct)))
            .RequirePermission(PartnersPermissions.SupplierManage);
        bankAccounts.MapPost("/{accountId:guid}/reveal", async (Guid partnerId, Guid accountId, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.RevealBankAccountAsync(partnerId, accountId, ct)))
            .RequirePermission(PartnersPermissions.SupplierRevealBankAccount)
            .WithSummary("The full account number and IBAN, once, with an audit event naming who revealed them");

        var registrations = partners.MapGroup("/{partnerId:guid}/tax-registrations").RequirePermission(PartnersPermissions.SupplierManage);
        registrations.MapPost("/", async (Guid partnerId, SaveTaxRegistrationRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveTaxRegistrationAsync(partnerId, null, request, ct), r => $"/api/v1/partners/{partnerId}/tax-registrations/{r.Id}"))
            .WithSummary("registrationType vat|tin|crn|other, unique per partner, country, type and number");
        registrations.MapPut("/{registrationId:guid}", async (Guid partnerId, Guid registrationId, SaveTaxRegistrationRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveTaxRegistrationAsync(partnerId, registrationId, request, ct)));
        registrations.MapDelete("/{registrationId:guid}", async (Guid partnerId, Guid registrationId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteTaxRegistrationAsync(partnerId, registrationId, ct)));

        var accounts = partners.MapGroup("/{partnerId:guid}/supplier-accounts");
        accounts.MapGet("/", async (Guid partnerId, SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListAccountsOfPartnerAsync(partnerId, ct)))
            .RequirePermission(PartnersPermissions.SupplierRead);
        accounts.MapPut("/{companyId:guid}", async (Guid partnerId, Guid companyId, SaveSupplierAccountRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAccountAsync(partnerId, companyId, request, ct)))
            .RequirePermission(PartnersPermissions.SupplierManage)
            .WithSummary("The partner as a supplier of the company: group, terms, posting group, withholding code, currency, lead time, tolerances, requires PO; blanks default from the group");
        accounts.MapPost("/{companyId:guid}/hold", async (Guid partnerId, Guid companyId, HoldRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.HoldAsync(partnerId, companyId, request, ct)))
            .RequirePermission(PartnersPermissions.SupplierManage)
            .WithSummary("status purchase|payment|all with a reason; documents of that kind refuse the supplier (supplier.on_hold)");
        accounts.MapPost("/{companyId:guid}/release", async (Guid partnerId, Guid companyId, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.ReleaseAsync(partnerId, companyId, ct)))
            .RequirePermission(PartnersPermissions.SupplierManage);

        return api;
    }
}
