using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Banking.Application;
using Quicker.Kernel.Results;
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

        var transfers = banking.MapGroup("/transfers");
        transfers.MapGet("/", async (Guid? companyId, Guid? bankAccountId, DateOnly? from, DateOnly? to, BankTransferService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(companyId, bankAccountId, from, to, ct)))
            .RequirePermission(BankingPermissions.TransferRead)
            .WithSummary("Transfers between the company's bank accounts and cash boxes, newest first; with an account, those in or out of it.");
        transfers.MapPost("/", async (RecordBankTransferRequest request, BankTransferService service, CancellationToken ct) => ApiProblems.Created(await service.RecordAsync(request, ct), static t => $"/api/v1/banking/transfers/{t.Id}"))
            .RequirePermission(BankingPermissions.TransferPost)
            .WithSummary("Records and posts a transfer: what left one account and what arrived in the other (asked for when the currencies differ), both boxes moved, and the realised exchange difference.");
        transfers.MapGet("/{transferId:guid}", async (Guid transferId, BankTransferService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(transferId, ct)))
            .RequirePermission(BankingPermissions.TransferRead);
        transfers.MapPost("/{transferId:guid}/reverse", async (Guid transferId, ReversePaymentRequest request, BankTransferService service, CancellationToken ct) => ApiProblems.Ok(await service.ReverseAsync(transferId, request, ct)))
            .RequirePermission(BankingPermissions.TransferPost)
            .WithSummary("Reverses a transfer: the mirror entry and both boxes' movements given back, on its own date or a later one.");

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

        // ------------------------------------------------------------------ statements and reconciliation (roadmap 6.2)
        var reconciliation = banking.MapGroup("/reconciliation");
        reconciliation.MapGet("/mappings", async (StatementService service, CancellationToken ct) => TypedResults.Ok(await service.ListMappingsAsync(ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("Saved CSV mappings: how each bank's export is read.");
        reconciliation.MapPost("/mappings", async (SaveStatementMappingRequest request, StatementService service, CancellationToken ct) => ApiProblems.Created(await service.SaveMappingAsync(null, request, ct), static m => $"/api/v1/banking/reconciliation/mappings/{m.Id}"))
            .RequirePermission(BankingPermissions.ReconciliationManage);
        reconciliation.MapPut("/mappings/{mappingId:guid}", async (Guid mappingId, SaveStatementMappingRequest request, StatementService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveMappingAsync(mappingId, request, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage);
        reconciliation.MapPost("/preview", (StatementPreviewRequest request, string? fileName) => ApiProblems.Ok(StatementService.Preview(request, fileName ?? "statement.csv")))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("Reads a file with a mapping (or by its format) without importing it: the first lines as they would be taken, or why they cannot be.");
        reconciliation.MapGet("/statements", async (Guid? bankAccountId, Guid? companyId, StatementService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(bankAccountId, companyId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("Imported statements, newest first, with how many lines are matched.");
        reconciliation.MapPost("/statements/import", async (HttpRequest request, StatementService service, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return ApiProblems.From(Error.Validation("statement.form_required", "Send multipart/form-data with bankAccountId and file."));
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null)
            {
                return ApiProblems.From(Error.Validation("statement.file_required", "The 'file' part is required."));
            }

            if (!Guid.TryParse(form["bankAccountId"].ToString(), out var bankAccountId))
            {
                return ApiProblems.From(Error.Validation("statement.bank_account_required", "bankAccountId must be a bank account id."));
            }

            Guid? mappingId = Guid.TryParse(form["mappingId"].ToString(), out var m) ? m : null;
            var settingsJson = form["settings"].ToString();
            var settings = string.IsNullOrWhiteSpace(settingsJson) ? null : StatementMappingSettings.Parse(settingsJson);
            using var reader = new StreamReader(file.OpenReadStream(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var content = await reader.ReadToEndAsync(ct);
            return ApiProblems.Created(await service.ImportAsync(bankAccountId, form["format"].ToString(), file.FileName, content, mappingId, settings, ct), static s => $"/api/v1/banking/reconciliation/statements/{s.Id}");
        }).RequirePermission(BankingPermissions.ReconciliationManage)
          .WithMetadata(new MultipartFormMetadata(typeof(StatementImportForm)))
          .WithSummary("Imports a statement file for a bank account (multipart/form-data: bankAccountId, file, optional format, mappingId or settings): lines already known are skipped.");
        reconciliation.MapGet("/statements/{statementId:guid}", async (Guid statementId, StatementService service, CancellationToken ct) => ApiProblems.Ok(await service.GetAsync(statementId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("The statement with its lines and their matches.");
        reconciliation.MapDelete("/statements/{statementId:guid}", async (Guid statementId, StatementService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteAsync(statementId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Removes a statement nothing was matched from yet.");
        reconciliation.MapPost("/statements/{statementId:guid}/auto-match", async (Guid statementId, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.AutoMatchAsync(statementId, null, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Runs the matcher over the statement's unmatched lines: same amount near in date, cheques the bank cleared or paid, a day's movements that add up, then the rules that book fees and interest.");
        reconciliation.MapPost("/auto-match", async (Guid bankAccountId, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.AutoMatchAsync(null, bankAccountId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Runs the matcher over every unmatched line of the bank account.");
        reconciliation.MapGet("/lines", async (Guid bankAccountId, ReconciliationService service, CancellationToken ct) => TypedResults.Ok(await service.UnmatchedLinesAsync(bankAccountId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("The account's unmatched statement lines, oldest first.");
        reconciliation.MapGet("/transactions", async (Guid bankAccountId, DateOnly? asOf, ReconciliationService service, CancellationToken ct) => TypedResults.Ok(await service.UnmatchedTransactionsAsync(bankAccountId, asOf, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("The account's book movements the bank has not confirmed, oldest first.");
        reconciliation.MapPost("/lines/{lineId:guid}/match", async (Guid lineId, MatchLinesRequest request, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.MatchAsync(lineId, request, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Pairs the line with the movements chosen; their sum must be the line.");
        reconciliation.MapPost("/lines/{lineId:guid}/unmatch", async (Guid lineId, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.UnmatchAsync(lineId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Undoes the line's matches; the movements are open again.");
        reconciliation.MapPost("/lines/{lineId:guid}/create", async (Guid lineId, CreateFromLineRequest request, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.CreateFromLineAsync(lineId, request, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Books a movement the books lacked from the line (a bank charge, interest, another income or expense by its account role) and pairs it at once.");
        reconciliation.MapPost("/lines/{lineId:guid}/exclude", async (Guid lineId, ExcludeLineRequest request, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.ExcludeAsync(lineId, request, true, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Sets a line aside (the bank's own error, a duplicate) so it does not count as unmatched.");
        reconciliation.MapPost("/lines/{lineId:guid}/include", async (Guid lineId, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.ExcludeAsync(lineId, new ExcludeLineRequest(), false, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Takes an excluded line back into matching.");
        reconciliation.MapGet("/rules", async (Guid? companyId, ReconciliationService service, CancellationToken ct) => TypedResults.Ok(await service.ListRulesAsync(companyId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("Matching rules by priority: what each looks for in a line and the account role it books to.");
        reconciliation.MapPost("/rules", async (SaveReconciliationRuleRequest request, ReconciliationService service, CancellationToken ct) => ApiProblems.Created(await service.SaveRuleAsync(null, request, ct), static r => $"/api/v1/banking/reconciliation/rules/{r.Id}"))
            .RequirePermission(BankingPermissions.ReconciliationManage);
        reconciliation.MapPut("/rules/{ruleId:guid}", async (Guid ruleId, SaveReconciliationRuleRequest request, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.SaveRuleAsync(ruleId, request, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage);
        reconciliation.MapDelete("/rules/{ruleId:guid}", async (Guid ruleId, ReconciliationService service, CancellationToken ct) => ApiProblems.NoContent(await service.DeleteRuleAsync(ruleId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage);
        reconciliation.MapGet("/report", async (Guid bankAccountId, DateOnly asOf, decimal? statementBalance, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.ReportAsync(bankAccountId, asOf, statementBalance, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("The account at a date: the bank's balance brought to the book's by deposits in transit and outstanding payments, the bank lines still unmatched, and what still differs.");
        reconciliation.MapGet("/reconciliations", async (Guid? bankAccountId, Guid? companyId, ReconciliationService service, CancellationToken ct) => TypedResults.Ok(await service.ListReconciliationsAsync(bankAccountId, companyId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("Reconciliations recorded, newest first.");
        reconciliation.MapPost("/reconciliations", async (CreateReconciliationRequest request, ReconciliationService service, CancellationToken ct) => ApiProblems.Created(await service.ReconcileAsync(request, ct), static r => $"/api/v1/banking/reconciliation/reconciliations/{r.Id}"))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Records the account's reconciliation at a date with the statement balance given; completed at once when nothing differs, otherwise left open to finish.");
        reconciliation.MapPost("/reconciliations/{reconciliationId:guid}/complete", async (Guid reconciliationId, ReconciliationService service, CancellationToken ct) => ApiProblems.Ok(await service.CompleteAsync(reconciliationId, ct)))
            .RequirePermission(BankingPermissions.ReconciliationManage)
            .WithSummary("Completes an open reconciliation once the bank and the books agree: the matched movements up to its date are reconciled for good.");
        reconciliation.MapGet("/aging", async (Guid companyId, DateOnly asOf, ReconciliationService service, CancellationToken ct) => TypedResults.Ok(await service.AgingAsync(companyId, asOf, ct)))
            .RequirePermission(BankingPermissions.ReconciliationRead)
            .WithSummary("Book movements the bank has not confirmed, per account, by how long they have waited: up to 30, 60, 90 days and beyond.");

        return api;
    }
}

/// <summary>The multipart form a statement import sends, declared so the contract shows its parts (the endpoint reads the form itself).</summary>
public sealed record StatementImportForm(Guid BankAccountId, IFormFile File, string? Format = null, Guid? MappingId = null, string? Settings = null);
