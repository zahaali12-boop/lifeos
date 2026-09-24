using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Partners.Application;
using Quicker.Partners.Contracts;
using Quicker.Web;

namespace Quicker.Partners.Api;

/// <summary>The partner master under /api/v1/partners: partners with contacts, addresses, bank accounts and tax registrations; supplier and customer accounts per company; groups, payment and delivery terms, withholding codes; sales reps, commission plans, the pipeline, opportunities and CRM activities.</summary>
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
            .RequireAnyPermission(PartnersPermissions.SupplierRead, PartnersPermissions.CustomerRead);
        partners.MapPost("/payment-terms", async (SavePaymentTermsRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Created(await service.SavePaymentTermsAsync(null, request, ct), static t => $"/api/v1/partners/payment-terms/{t.Id}"))
            .RequirePermission(PartnersPermissions.TermsManage)
            .WithSummary("Payment terms: due days from the invoice date, the end of its month or the delivery; optional instalments (percentages summing to 100), early-payment discount, business days only");
        partners.MapPut("/payment-terms/{termsId:guid}", async (Guid termsId, SavePaymentTermsRequest request, SupplierService service, CancellationToken ct) => ApiProblems.Ok(await service.SavePaymentTermsAsync(termsId, request, ct)))
            .RequirePermission(PartnersPermissions.TermsManage);
        partners.MapPost("/payment-terms/{termsId:guid}/schedule", async (Guid termsId, SchedulePreviewRequest request, IPartnerDirectory directory, CancellationToken ct) =>
            ApiProblems.Ok(await directory.ScheduleAsync(request.CompanyId, termsId, request.InvoiceDate, request.DeliveryDate, request.Amount, request.Currency, ct)))
            .RequireAnyPermission(PartnersPermissions.SupplierRead, PartnersPermissions.CustomerRead)
            .WithSummary("The due dates and instalment amounts an invoice of this amount would carry (the parts sum to the whole in the currency's minor unit)");

        partners.MapGet("/delivery-terms", async (SupplierService service, CancellationToken ct) => TypedResults.Ok(await service.ListDeliveryTermsAsync(ct)))
            .RequireAnyPermission(PartnersPermissions.SupplierRead, PartnersPermissions.CustomerRead);
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

        // ------------------------------------------------------------------ customer side (roadmap 5.1)
        partners.MapGet("/customer-groups", async (CustomerService service, CancellationToken ct) => TypedResults.Ok(await service.ListGroupsAsync(ct)))
            .RequireAnyPermission(PartnersPermissions.CustomerRead, PartnersPermissions.TermsManage);
        partners.MapPost("/customer-groups", async (SaveCustomerGroupRequest request, CustomerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveGroupAsync(null, request, ct), static g => $"/api/v1/partners/customer-groups/{g.Id}"))
            .RequirePermission(PartnersPermissions.TermsManage)
            .WithSummary("A customer group: the posting group, payment and delivery terms its members default to");
        partners.MapPut("/customer-groups/{groupId:guid}", async (Guid groupId, SaveCustomerGroupRequest request, CustomerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveGroupAsync(groupId, request, ct)))
            .RequirePermission(PartnersPermissions.TermsManage);

        partners.MapGet("/customers", async (Guid? companyId, string? q, string? creditStatus, Guid? salesRepId, Guid? customerGroupId, bool? mine, bool? isActive, CustomerService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAccountsAsync(companyId, q, creditStatus, salesRepId, customerGroupId, mine, isActive, ct)))
            .RequirePermission(PartnersPermissions.CustomerRead)
            .WithSummary("Customer accounts in the companies the member may read, with their partner and effective terms; creditStatus=held for every held or blocked one; mine=true for the signed-in rep's customers");

        partners.MapGet("/sales-reps", async (SalesSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListRepsAsync(ct)))
            .RequirePermission(PartnersPermissions.CustomerRead);
        partners.MapPost("/sales-reps", async (SaveSalesRepRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveRepAsync(null, request, ct), static r => $"/api/v1/partners/sales-reps/{r.Id}"))
            .RequirePermission(PartnersPermissions.SalesSetupManage)
            .WithSummary("A sales rep: optionally a workspace member (for 'my customers' and 'my pipeline') and an employee partner, for one company or all, paid under a commission plan");
        partners.MapPut("/sales-reps/{repId:guid}", async (Guid repId, SaveSalesRepRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveRepAsync(repId, request, ct)))
            .RequirePermission(PartnersPermissions.SalesSetupManage);

        partners.MapGet("/commission-plans", async (SalesSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListPlansAsync(ct)))
            .RequirePermission(PartnersPermissions.CustomerRead);
        partners.MapPost("/commission-plans", async (SaveCommissionPlanRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SavePlanAsync(null, request, ct), static p => $"/api/v1/partners/commission-plans/{p.Id}"))
            .RequirePermission(PartnersPermissions.SalesSetupManage)
            .WithSummary("A commission plan: paid on revenue, margin or what was collected, accruing at invoice or payment, with marginal rate bands by item category and customer group from a period-to-date threshold");
        partners.MapPut("/commission-plans/{planId:guid}", async (Guid planId, SaveCommissionPlanRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SavePlanAsync(planId, request, ct)))
            .RequirePermission(PartnersPermissions.SalesSetupManage);
        partners.MapPost("/commission-plans/{planId:guid}/quote", async (Guid planId, CommissionQuoteRequest request, SalesSetupService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.QuoteAsync(planId, request.ItemCategoryId, request.CustomerGroupId, request.PeriodToDate, request.Amount, ct)))
            .RequirePermission(PartnersPermissions.CustomerRead)
            .WithSummary("What a sale of this amount earns under the plan given the rep's basis so far in the tier period: the matching scope, the bands crossed and the rounded commission");

        partners.MapGet("/pipeline-stages", async (SalesSetupService service, CancellationToken ct) => TypedResults.Ok(await service.ListStagesAsync(ct)))
            .RequirePermission(PartnersPermissions.CustomerRead);
        partners.MapPost("/pipeline-stages", async (SavePipelineStageRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Created(await service.SaveStageAsync(null, request, ct), static s => $"/api/v1/partners/pipeline-stages/{s.Id}"))
            .RequirePermission(PartnersPermissions.SalesSetupManage)
            .WithSummary("A pipeline stage: open, won (100%) or lost (0%), with the probability opportunities take on entering it");
        partners.MapPut("/pipeline-stages/order", async (ReorderStagesRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.ReorderStagesAsync(request, ct)))
            .RequirePermission(PartnersPermissions.SalesSetupManage)
            .WithSummary("The order of the board's columns: every stage, each once");
        partners.MapPut("/pipeline-stages/{stageId:guid}", async (Guid stageId, SavePipelineStageRequest request, SalesSetupService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveStageAsync(stageId, request, ct)))
            .RequirePermission(PartnersPermissions.SalesSetupManage);

        partners.MapGet("/pipeline", async (Guid? companyId, Guid? salesRepId, bool? mine, OpportunityService service, CancellationToken ct) => TypedResults.Ok(await service.BoardAsync(companyId, salesRepId, mine, ct)))
            .RequirePermission(PartnersPermissions.CustomerRead)
            .WithSummary("The pipeline board: each active stage with its opportunities (won and lost for the last 90 days) and totals per currency, weighted by probability");

        var opportunities = partners.MapGroup("/opportunities");
        opportunities.MapGet("/", async (Guid? companyId, Guid? partnerId, string? status, Guid? stageId, Guid? salesRepId, bool? mine, string? q, OpportunityService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(companyId, partnerId, status, stageId, salesRepId, mine, q, ct)))
            .RequirePermission(PartnersPermissions.CustomerRead)
            .WithSummary("Opportunities most recently changed first; q matches the number, the title or the partner");
        opportunities.MapPost("/", async (CreateOpportunityRequest request, OpportunityService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static o => $"/api/v1/partners/opportunities/{o.Id}"))
            .RequirePermission(PartnersPermissions.CustomerManage)
            .WithSummary("An opportunity, numbered in the company's series, in the first open stage unless another is named; the rep and currency default from the customer account; custom fields under host 'opportunity'");
        opportunities.MapGet("/{opportunityId:guid}", async (Guid opportunityId, OpportunityService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(opportunityId, ct), OpportunityService.EntityType, opportunityId))
            .RequirePermission(PartnersPermissions.CustomerRead)
            .WithSummary("The opportunity with every stage it passed through and its activities");
        opportunities.MapPut("/{opportunityId:guid}", async (Guid opportunityId, UpdateOpportunityRequest request, OpportunityService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(opportunityId, request, ct)))
            .RequirePermission(PartnersPermissions.CustomerManage);
        opportunities.MapPost("/{opportunityId:guid}/move", async (Guid opportunityId, MoveOpportunityRequest request, OpportunityService service, CancellationToken ct) => ApiProblems.Ok(await service.MoveAsync(opportunityId, request, ct)))
            .RequirePermission(PartnersPermissions.CustomerManage)
            .WithSummary("Moves the opportunity to a stage: a won stage wins it, a lost stage loses it (with the reason), an open stage reopens it; every move is kept");

        var activities = partners.MapGroup("/crm-activities");
        activities.MapGet("/", async (Guid? partnerId, Guid? opportunityId, string? status, bool? mine, string? due, CrmActivityService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(partnerId, opportunityId, status, mine, due, ct)))
            .RequirePermission(PartnersPermissions.CustomerRead)
            .WithSummary("CRM activities, open ones first by due date; mine=true for those assigned to the signed-in member; due=overdue|upcoming");
        activities.MapPost("/", async (SaveCrmActivityRequest request, CrmActivityService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static a => $"/api/v1/partners/crm-activities/{a.Id}"))
            .RequirePermission(PartnersPermissions.CustomerManage)
            .WithSummary("kind call|meeting|email|task|note with a partner, optionally about one of its opportunities or with a contact; a note is logged done; an assignee other than the author is notified");
        activities.MapPut("/{activityId:guid}", async (Guid activityId, SaveCrmActivityRequest request, CrmActivityService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(activityId, request, ct)))
            .RequirePermission(PartnersPermissions.CustomerManage);
        activities.MapPost("/{activityId:guid}/complete", async (Guid activityId, CompleteCrmActivityRequest request, CrmActivityService service, CancellationToken ct) => ApiProblems.Ok(await service.CompleteAsync(activityId, request, ct)))
            .RequirePermission(PartnersPermissions.CustomerManage);
        activities.MapPost("/{activityId:guid}/cancel", async (Guid activityId, CrmActivityService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(activityId, ct)))
            .RequirePermission(PartnersPermissions.CustomerManage);

        // ------------------------------------------------------------------ partners
        partners.MapGet("/", async (string? q, string? role, bool? isActive, int? limit, string? cursor, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.ListAsync(q, role, isActive, new PageRequest(limit, cursor), ct)))
            .RequireAnyPermission(PartnersPermissions.SupplierRead, PartnersPermissions.CustomerRead)
            .WithSummary("Partners newest first, paged: q matches the code, a name in any language or the email; role=supplier|customer|employee");
        partners.MapPost("/", async (SavePartnerRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static p => $"/api/v1/partners/{p.Id}"))
            .RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage)
            .WithSummary("A partner: one record per legal person wearing the supplier, customer and employee roles (each role given only with its side's permission); custom fields under host 'partner'");
        partners.MapGet("/by-code/{code}", async (string code, PartnerService service, CancellationToken ct) => ApiProblems.Found(await service.GetByCodeAsync(code, ct), "partner", code))
            .RequireAnyPermission(PartnersPermissions.SupplierRead, PartnersPermissions.CustomerRead);
        partners.MapGet("/{partnerId:guid}", async (Guid partnerId, PartnerService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(partnerId, ct), "partner", partnerId))
            .RequireAnyPermission(PartnersPermissions.SupplierRead, PartnersPermissions.CustomerRead)
            .WithSummary("The partner with contacts, addresses, masked bank accounts, tax registrations, and the supplier and customer accounts the member may read");
        partners.MapPut("/{partnerId:guid}", async (Guid partnerId, SavePartnerRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(partnerId, request, ct)))
            .RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage);

        var contacts = partners.MapGroup("/{partnerId:guid}/contacts").RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage);
        contacts.MapPost("/", async (Guid partnerId, SaveContactRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveContactAsync(partnerId, null, request, ct), c => $"/api/v1/partners/{partnerId}/contacts/{c.Id}"));
        contacts.MapPut("/{contactId:guid}", async (Guid partnerId, Guid contactId, SaveContactRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveContactAsync(partnerId, contactId, request, ct)));
        contacts.MapDelete("/{contactId:guid}", async (Guid partnerId, Guid contactId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteContactAsync(partnerId, contactId, ct)));

        var addresses = partners.MapGroup("/{partnerId:guid}/addresses").RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage);
        addresses.MapPost("/", async (Guid partnerId, SaveAddressRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAddressAsync(partnerId, null, request, ct), a => $"/api/v1/partners/{partnerId}/addresses/{a.Id}"))
            .WithSummary("role billing|shipping|legal|other; address is a structured bilingual object; one default per role");
        addresses.MapPut("/{addressId:guid}", async (Guid partnerId, Guid addressId, SaveAddressRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAddressAsync(partnerId, addressId, request, ct)));
        addresses.MapDelete("/{addressId:guid}", async (Guid partnerId, Guid addressId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAddressAsync(partnerId, addressId, ct)));

        var bankAccounts = partners.MapGroup("/{partnerId:guid}/bank-accounts");
        bankAccounts.MapPost("/", async (Guid partnerId, SaveBankAccountRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Created(await service.SaveBankAccountAsync(partnerId, null, request, ct), a => $"/api/v1/partners/{partnerId}/bank-accounts/{a.Id}"))
            .RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage)
            .WithSummary("Account number and IBAN are checked (IBAN mod-97), stored encrypted and shown masked; a supplier's accounts change only with the supplier permission");
        bankAccounts.MapPut("/{accountId:guid}", async (Guid partnerId, Guid accountId, SaveBankAccountRequest request, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveBankAccountAsync(partnerId, accountId, request, ct)))
            .RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage);
        bankAccounts.MapDelete("/{accountId:guid}", async (Guid partnerId, Guid accountId, PartnerService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteBankAccountAsync(partnerId, accountId, ct)))
            .RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage);
        bankAccounts.MapPost("/{accountId:guid}/reveal", async (Guid partnerId, Guid accountId, PartnerService service, CancellationToken ct) => ApiProblems.Ok(await service.RevealBankAccountAsync(partnerId, accountId, ct)))
            .RequirePermission(PartnersPermissions.SupplierRevealBankAccount)
            .WithSummary("The full account number and IBAN, once, with an audit event naming who revealed them");

        var registrations = partners.MapGroup("/{partnerId:guid}/tax-registrations").RequireAnyPermission(PartnersPermissions.SupplierManage, PartnersPermissions.CustomerManage);
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

        var customerAccounts = partners.MapGroup("/{partnerId:guid}/customer-accounts");
        customerAccounts.MapGet("/", async (Guid partnerId, CustomerService service, CancellationToken ct) => TypedResults.Ok(await service.ListAccountsOfPartnerAsync(partnerId, ct)))
            .RequirePermission(PartnersPermissions.CustomerRead);
        customerAccounts.MapPut("/{companyId:guid}", async (Guid partnerId, Guid companyId, SaveCustomerAccountRequest request, CustomerService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAccountAsync(partnerId, companyId, request, ct)))
            .RequireAnyPermission(PartnersPermissions.CustomerManage, PartnersPermissions.CreditManage)
            .WithSummary("The partner as a customer of the company. The customer permission keeps group, terms, posting group, sales rep, default warehouse, currency and statements; the credit permission the credit limit (functional currency, empty for none), exposure basis and overdue block; send the other side's fields as they stand");
        customerAccounts.MapPost("/{companyId:guid}/credit-status", async (Guid partnerId, Guid companyId, CreditStatusRequest request, CustomerService service, CancellationToken ct) => ApiProblems.Ok(await service.SetCreditStatusAsync(partnerId, companyId, request, ct)))
            .RequirePermission(PartnersPermissions.CreditManage)
            .WithSummary("status on_hold|blocked with a reason, or ok to release: on hold sends new orders to credit hold, blocked refuses orders, shipments and invoices (customer.credit_blocked)");

        return api;
    }
}
