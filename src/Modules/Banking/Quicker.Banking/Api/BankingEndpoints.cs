using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Banking.Application;
using Quicker.Web;

namespace Quicker.Banking.Api;

/// <summary>Bank and cash under /api/v1/banking: accounts with their transactions, and supplier payments and advances.</summary>
public static class BankingEndpoints
{
    public static RouteGroupBuilder MapBankingEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var banking = api.MapGroup("/banking").WithTags("Banking").RequireAuthorization();

        var accounts = banking.MapGroup("/bank-accounts");
        accounts.MapGet("/", async (Guid? companyId, BankAccountService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, ct)))
            .RequirePermission(BankingPermissions.BankAccountRead)
            .WithSummary("Bank, cash and petty-cash accounts with their balances from the bank subledger.");
        accounts.MapPost("/", async (SaveCompanyBankAccountRequest request, BankAccountService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static a => $"/api/v1/banking/bank-accounts/{a.Id}"))
            .RequirePermission(BankingPermissions.BankAccountManage)
            .WithSummary("Creates an account on the chart's control account of its kind (or the one named) and the posting rule that routes to it.");
        accounts.MapGet("/{bankAccountId:guid}", async (Guid bankAccountId, BankAccountService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(bankAccountId, ct)))
            .RequirePermission(BankingPermissions.BankAccountRead);
        accounts.MapPut("/{bankAccountId:guid}", async (Guid bankAccountId, SaveCompanyBankAccountRequest request, BankAccountService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(bankAccountId, request, ct)))
            .RequirePermission(BankingPermissions.BankAccountManage);
        accounts.MapGet("/{bankAccountId:guid}/transactions", async (Guid bankAccountId, DateOnly? from, DateOnly? to, BankAccountService service, CancellationToken ct) => TypedResults.Ok(await service.TransactionsAsync(bankAccountId, from, to, ct)))
            .RequirePermission(BankingPermissions.BankAccountRead)
            .WithSummary("The account's movements (its subledger), newest first.");

        var payments = banking.MapGroup("/payments");
        payments.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, PaymentService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(BankingPermissions.PaymentRead);
        payments.MapPost("/", async (SavePaymentRequest request, PaymentService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static p => $"/api/v1/banking/payments/{p.Id}"))
            .RequirePermission(BankingPermissions.PaymentManage)
            .WithSummary("Drafts a supplier payment (lines against open invoice items, discounts, withholding at payment, charges, on account) or an advance.");
        payments.MapPost("/from-proposal", async (PayProposalRequest request, PaymentService service, CancellationToken ct) => ApiProblems.Ok(await service.FromProposalAsync(request, ct)))
            .RequirePermission(BankingPermissions.PaymentManage)
            .WithSummary("Drafts one payment per supplier from an approved payment proposal.");
        payments.MapGet("/{paymentId:guid}", async (Guid paymentId, PaymentService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(paymentId, ct)))
            .RequirePermission(BankingPermissions.PaymentRead);
        payments.MapPut("/{paymentId:guid}", async (Guid paymentId, SavePaymentRequest request, PaymentService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(paymentId, request, ct)))
            .RequirePermission(BankingPermissions.PaymentManage);
        payments.MapDelete("/{paymentId:guid}", async (Guid paymentId, PaymentService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(paymentId, ct)))
            .RequirePermission(BankingPermissions.PaymentManage);
        payments.MapPost("/{paymentId:guid}/post", async (Guid paymentId, PaymentService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(paymentId, ct)))
            .RequirePermission(BankingPermissions.PaymentPost)
            .WithSummary("Posts the payment: payables relieved at booked value, discount, withholding, charges, the bank movement and the realised exchange difference.");
        payments.MapPost("/{paymentId:guid}/reverse", async (Guid paymentId, ReversePaymentRequest request, PaymentService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(paymentId, request, ct)))
            .RequirePermission(BankingPermissions.PaymentPost)
            .WithSummary("Reverses a posted payment whose remainder was not applied further: items reopen at their booked values.");

        return api;
    }
}
