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

        var receipts = banking.MapGroup("/receipts");
        receipts.MapGet("/", async (Guid? companyId, string? status, Guid? partnerId, CustomerReceiptService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, status, partnerId, ct)))
            .RequirePermission(BankingPermissions.ReceiptRead);
        receipts.MapPost("/", async (SaveCustomerReceiptRequest request, CustomerReceiptService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static p => $"/api/v1/banking/receipts/{p.Id}"))
            .RequirePermission(BankingPermissions.ReceiptManage)
            .WithSummary("Drafts a customer receipt (lines against open invoice items, discount given, tax withheld by the customer, bank charges, on account) or a deposit.");
        receipts.MapGet("/{receiptId:guid}", async (Guid receiptId, CustomerReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(receiptId, ct)))
            .RequirePermission(BankingPermissions.ReceiptRead);
        receipts.MapPut("/{receiptId:guid}", async (Guid receiptId, SaveCustomerReceiptRequest request, CustomerReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(receiptId, request, ct)))
            .RequirePermission(BankingPermissions.ReceiptManage);
        receipts.MapDelete("/{receiptId:guid}", async (Guid receiptId, CustomerReceiptService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(receiptId, ct)))
            .RequirePermission(BankingPermissions.ReceiptManage);
        receipts.MapPost("/{receiptId:guid}/post", async (Guid receiptId, CustomerReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.PostAsync(receiptId, ct)))
            .RequirePermission(BankingPermissions.ReceiptPost)
            .WithSummary("Posts the receipt: receivables relieved at booked value, discount given, withholding, charges, the bank movement and the realised exchange difference.");
        receipts.MapPost("/{receiptId:guid}/reverse", async (Guid receiptId, ReversePaymentRequest request, CustomerReceiptService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(receiptId, request, ct)))
            .RequirePermission(BankingPermissions.ReceiptPost)
            .WithSummary("Reverses a posted receipt whose remainder was not applied further: items reopen at their booked values.");

        var books = banking.MapGroup("/cheque-books");
        books.MapGet("/", async (Guid? companyId, Guid? bankAccountId, ChequeBookService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, bankAccountId, ct)))
            .RequirePermission(BankingPermissions.ChequeRead)
            .WithSummary("Cheque books of the bank accounts, with the leaves left in each.");
        books.MapPost("/", async (SaveChequeBookRequest request, ChequeBookService service, CancellationToken ct) => ApiProblems.Created(await service.SaveAsync(null, request, ct), static b => $"/api/v1/banking/cheque-books/{b.Id}"))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("A new cheque book: a run of numbered leaves of a bank account, issued in order.");
        books.MapPut("/{bookId:guid}", async (Guid bookId, SaveChequeBookRequest request, ChequeBookService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveAsync(bookId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage);

        var cheques = banking.MapGroup("/cheques");
        cheques.MapGet("/", async (Guid? companyId, string? direction, string? status, Guid? partnerId, DateOnly? from, DateOnly? to, ChequeService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, direction, status, partnerId, from, to, ct)))
            .RequirePermission(BankingPermissions.ChequeRead)
            .WithSummary("The cheque registers: received and issued cheques by status, partner and due date.");
        cheques.MapGet("/forecast", async (Guid companyId, DateOnly from, DateOnly to, ChequeService service, CancellationToken ct) => TypedResults.Ok(await service.ForecastAsync(companyId, from, to, ct)))
            .RequirePermission(BankingPermissions.ChequeRead)
            .WithSummary("Cheques falling due month by month: received ones still to clear and issued ones still to be paid, in the company's currency.");
        cheques.MapGet("/deposits", async (Guid? companyId, ChequeService service, CancellationToken ct) => TypedResults.Ok(await service.ListDepositsAsync(companyId, ct)))
            .RequirePermission(BankingPermissions.ChequeRead)
            .WithSummary("Deposit slips, newest first.");
        cheques.MapPost("/deposits", async (DepositChequesRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Created(await service.DepositAsync(request, ct), static d => $"/api/v1/banking/cheques/deposits/{d.Id}"))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("Deposits cheques in hand to a bank account on one slip: each moves from PDC receivable to cheques under collection at its booked value.");
        cheques.MapGet("/deposits/{depositId:guid}", async (Guid depositId, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.GetDepositAsync(depositId, ct)))
            .RequirePermission(BankingPermissions.ChequeRead);
        cheques.MapPost("/deposits/{depositId:guid}/clear", async (Guid depositId, ChequeActionRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.ClearDepositAsync(depositId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("Clears every cheque of the slip still under collection on one day; a charge given is taken once.");
        cheques.MapGet("/{chequeId:guid}", async (Guid chequeId, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(chequeId, ct)))
            .RequirePermission(BankingPermissions.ChequeRead)
            .WithSummary("The cheque with every step of its life.");
        cheques.MapPost("/{chequeId:guid}/clear", async (Guid chequeId, ChequeActionRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.ClearAsync(chequeId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("The bank credited a deposited cheque: cheques under collection to the bank, less what the bank charged.");
        cheques.MapPost("/{chequeId:guid}/bounce", async (Guid chequeId, BounceChequeRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.BounceAsync(chequeId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("The bank returned the cheque unpaid: a received one reverses its receipt (the invoices reopen), books the charge and re-charges a fee to the customer; an issued one goes back on PDC payable.");
        cheques.MapPost("/{chequeId:guid}/return", async (Guid chequeId, ChequeActionRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.ReturnAsync(chequeId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("A cheque in hand given back to the customer: its receipt is reversed.");
        cheques.MapPost("/{chequeId:guid}/pay", async (Guid chequeId, ChequeActionRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.PayAsync(chequeId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("The bank paid an issued cheque: PDC payable to the bank, plus what the bank charged.");
        cheques.MapPost("/{chequeId:guid}/cancel", async (Guid chequeId, ChequeActionRequest request, ChequeService service, CancellationToken ct) => ApiProblems.Ok(await service.CancelAsync(chequeId, request, ct)))
            .RequirePermission(BankingPermissions.ChequeManage)
            .WithSummary("An issued cheque the bank did not pay is cancelled: its payment is reversed and the invoices reopen.");

        return api;
    }
}
