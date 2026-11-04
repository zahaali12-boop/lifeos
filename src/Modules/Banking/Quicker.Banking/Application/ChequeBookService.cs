using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Banking.Application;

/// <summary>Cheque books of a bank account: a numbered run of leaves issued cheques are taken from, in order.</summary>
public sealed class ChequeBookService(BankingDbContext db, IAuditSink audit, IClock clock)
{
    public async Task<IReadOnlyList<ChequeBookSummary>> ListAsync(Guid? companyId, Guid? bankAccountId, CancellationToken cancellationToken)
    {
        var query = db.ChequeBooks.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(b => b.CompanyId == c);
        }

        if (bankAccountId is { } a)
        {
            query = query.Where(b => b.BankAccountId == a);
        }

        var books = await query.OrderBy(static b => b.BankAccountId).ThenBy(static b => b.FirstNumber).ToListAsync(cancellationToken);
        var accounts = await db.BankAccounts.AsNoTracking().Where(a => books.Select(b => b.BankAccountId).Contains(a.Id)).ToDictionaryAsync(static a => a.Id, static a => a.Code, cancellationToken);
        return books.Select(b => Map(b, accounts.GetValueOrDefault(b.BankAccountId, string.Empty))).ToList();
    }

    public async Task<Result<ChequeBookSummary>> SaveAsync(Guid? id, SaveChequeBookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.FirstNumber <= 0 || request.LastNumber < request.FirstNumber)
        {
            return Error.Validation("cheque_book.range_invalid", "A cheque book runs from a first number above zero to a last number not below it.").WithWhy(("firstNumber", request.FirstNumber), ("lastNumber", request.LastNumber));
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.BankAccountId, cancellationToken);
        if (bank is null || bank.CompanyId != request.CompanyId || bank.Kind != "bank")
        {
            return Error.Validation("cheque_book.bank_account_invalid", "A cheque book belongs to a bank account of the company (not a cash box).").WithWhy(("bankAccountId", request.BankAccountId));
        }

        var prefix = (request.Prefix ?? string.Empty).Trim().ToUpperInvariant();
        ChequeBook book;
        if (id is { } existingId)
        {
            var found = await db.ChequeBooks.SingleOrDefaultAsync(b => b.Id == existingId, cancellationToken);
            if (found is null)
            {
                return Error.NotFound("cheque_book", existingId);
            }

            if (found.CompanyId != request.CompanyId || found.BankAccountId != request.BankAccountId)
            {
                return Error.Validation("cheque_book.account_locked", "A cheque book stays with its bank account.");
            }

            if (found.NextNumber > found.FirstNumber && request.FirstNumber != found.FirstNumber)
            {
                return Error.Conflict("cheque_book.in_use", "Leaves were issued from this book; its first number is fixed.").WithWhy(("nextNumber", found.NextNumber));
            }

            book = found;
            book.NextNumber = Math.Max(book.NextNumber, request.FirstNumber);
        }
        else
        {
            var overlapping = await db.ChequeBooks.AsNoTracking().AnyAsync(b => b.BankAccountId == request.BankAccountId && b.Prefix == prefix && b.FirstNumber <= request.LastNumber && b.LastNumber >= request.FirstNumber, cancellationToken);
            if (overlapping)
            {
                return Error.Conflict("cheque_book.overlaps", "Another book of this account covers some of these numbers.").WithWhy(("prefix", prefix), ("firstNumber", request.FirstNumber), ("lastNumber", request.LastNumber));
            }

            book = new ChequeBook { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, BankAccountId = request.BankAccountId, NextNumber = request.FirstNumber, CreatedAt = clock.UtcNow };
            db.ChequeBooks.Add(book);
        }

        book.Prefix = prefix;
        book.FirstNumber = request.FirstNumber;
        book.LastNumber = request.LastNumber;
        book.IsActive = request.IsActive;
        book.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("bank_cheque_book", book.Id, $"{book.Prefix}{book.FirstNumber}-{book.LastNumber}", id is null ? AuditActions.Created : AuditActions.Updated, After: new { book.BankAccountId, book.Prefix, book.FirstNumber, book.LastNumber, book.NextNumber, book.IsActive }, CompanyId: book.CompanyId), cancellationToken);
        return Map(book, bank.Code);
    }

    private static ChequeBookSummary Map(ChequeBook b, string bankCode) =>
        new(b.Id, b.CompanyId, b.BankAccountId, bankCode, b.Prefix, b.FirstNumber, b.LastNumber, b.NextNumber, Math.Max(0, b.LastNumber - b.NextNumber + 1), b.IsActive);
}
