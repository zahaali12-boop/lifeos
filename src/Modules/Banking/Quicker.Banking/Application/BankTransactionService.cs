using Microsoft.EntityFrameworkCore;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Banking.Application;

/// <summary>
/// The bank transactions subledger for other modules' documents (A-174): a petty cash voucher or a fund replenishment
/// records the movement its journal line references, and its reversal mirrors every movement it made.
/// </summary>
public sealed class BankTransactionService(BankingDbContext db, IClock clock) : IBankTransactions
{
    public async Task RecordAsync(NewBankTransaction transaction, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        db.Transactions.Add(new BankTransaction
        {
            Id = transaction.Id,
            BankAccountId = transaction.BankAccountId,
            CompanyId = transaction.CompanyId,
            PostingDate = transaction.PostingDate,
            ValueDate = transaction.PostingDate,
            Kind = transaction.Kind,
            AmountTc = transaction.AmountTc,
            AmountFc = transaction.AmountFc,
            Reference = transaction.Reference,
            SourceDocumentType = transaction.SourceDocumentType,
            SourceDocumentId = transaction.SourceDocumentId,
            JournalEntryId = transaction.JournalEntryId,
            CreatedAt = clock.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ReverseDocumentAsync(string sourceDocumentType, Guid sourceDocumentId, DateOnly reversalDate, Guid? reversalEntryId, CancellationToken cancellationToken = default)
    {
        var originals = await db.Transactions.AsNoTracking()
            .Where(t => t.SourceDocumentType == sourceDocumentType && t.SourceDocumentId == sourceDocumentId && t.Kind != "reversal" && t.ReversesTransactionId == null)
            .ToListAsync(cancellationToken);
        var reversed = await db.Transactions.AsNoTracking().Where(t => t.SourceDocumentType == sourceDocumentType && t.SourceDocumentId == sourceDocumentId && t.ReversesTransactionId != null).Select(static t => t.ReversesTransactionId!.Value).ToListAsync(cancellationToken);
        foreach (var original in originals.Where(o => !reversed.Contains(o.Id)))
        {
            db.Transactions.Add(new BankTransaction
            {
                Id = Guid.CreateVersion7(),
                BankAccountId = original.BankAccountId,
                CompanyId = original.CompanyId,
                PostingDate = reversalDate,
                ValueDate = reversalDate,
                Kind = "reversal",
                AmountTc = -original.AmountTc,
                AmountFc = -original.AmountFc,
                Reference = original.Reference,
                SourceDocumentType = sourceDocumentType,
                SourceDocumentId = sourceDocumentId,
                JournalEntryId = reversalEntryId,
                ReversesTransactionId = original.Id,
                CreatedAt = clock.UtcNow,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<decimal> BalanceAsync(Guid bankAccountId, DateOnly? asOf = null, CancellationToken cancellationToken = default)
    {
        var query = db.Transactions.AsNoTracking().Where(t => t.BankAccountId == bankAccountId);
        if (asOf is { } day)
        {
            query = query.Where(t => t.PostingDate <= day);
        }

        return await query.SumAsync(static t => t.AmountTc, cancellationToken);
    }
}

/// <summary>Supplier payments for other modules (an expense paid from its own screen): one open item, created and posted in one go.</summary>
public sealed class PaymentsGateway(PaymentService payments) : IPayments
{
    public async Task<Result<PaymentRef>> PayAsync(PayOpenItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var created = await payments.CreateAsync(new SavePaymentRequest(request.CompanyId, request.PartnerId, request.BankAccountId, "supplier_payment", request.PaymentDate, request.Method, request.Reference,
            ExchangeRate: request.ExchangeRate, Lines: [new SavePaymentLineRequest(request.OpenItemId, request.Amount)], BankAmount: request.BankAmount), cancellationToken);
        return created.IsFailure ? created.Error! : await PostAsync(created.Value.Id, cancellationToken);
    }

    public async Task<Result<PaymentRef>> AdvanceAsync(SupplierAdvanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var created = await payments.CreateAsync(new SavePaymentRequest(request.CompanyId, request.PartnerId, request.BankAccountId, "supplier_advance", request.PaymentDate, request.Method, request.Reference,
            request.Currency, request.ExchangeRate, OnAccount: request.Amount, BankAmount: request.BankAmount), cancellationToken);
        return created.IsFailure ? created.Error! : await PostAsync(created.Value.Id, cancellationToken);
    }

    private async Task<Result<PaymentRef>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var posted = await payments.PostAsync(id, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var p = posted.Value;
        return new PaymentRef(p.Id, p.Number, p.OpenItemId, p.ExchangeRate, p.BankAmount, p.BankCurrency);
    }
}
