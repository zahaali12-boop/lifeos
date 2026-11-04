using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Pos.Contracts;
using Quicker.Pos.Domain;
using Quicker.Pos.Persistence;

namespace Quicker.Pos.Application;

/// <summary>
/// Sessions (A-180): a cashier opens a register with a float and closes it with the cash counted. The expected cash is
/// the float plus the cash the session's orders took, net of change and refunds; a difference is booked on the cash
/// box against the suspense account for the accountant to clear, so the box's book balance is what was counted.
/// </summary>
public sealed class SessionService(
    PosDbContext db,
    ICompanyDirectory companies,
    IBankAccountDirectory bankAccounts,
    IBankTransactions bankTransactions,
    IPostingService posting,
    INumberAllocator numbering,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = PosDocumentTypes.Session;

    public async Task<IReadOnlyList<PosSessionSummary>> ListAsync(Guid? companyId, Guid? registerId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Sessions.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(s => s.CompanyId == c);
        }

        if (registerId is { } r)
        {
            query = query.Where(s => s.RegisterId == r);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status == status);
        }

        var sessions = await query.OrderByDescending(static s => s.OpenedAt).Take(500).ToListAsync(cancellationToken);
        var result = new List<PosSessionSummary>(sessions.Count);
        foreach (var session in sessions)
        {
            result.Add(await MapAsync(session, cancellationToken));
        }

        return result;
    }

    public async Task<Result<PosSessionSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var session = await db.Sessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return session is null ? Error.NotFound(AuditEntity, id) : await MapAsync(session, cancellationToken);
    }

    public async Task<Result<PosSessionSummary>> OpenAsync(Guid registerId, OpenSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var register = await db.Registers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == registerId, cancellationToken);
        if (register is null)
        {
            return Error.NotFound(RegisterService.AuditEntity, registerId);
        }

        if (!register.IsActive)
        {
            return Error.Conflict("session.register_inactive", "The register is inactive.").WithWhy(("register", register.Code));
        }

        var company = (await companies.FindAsync(new CompanyId(register.CompanyId), cancellationToken))!;
        if (request.OpeningFloat < 0m || !new Money(request.OpeningFloat, company.FunctionalCurrency).IsRoundedToMinorUnit)
        {
            return Error.Validation("session.float_invalid", "The float is zero or above, in the company's currency.");
        }

        if (await db.Sessions.AnyAsync(s => s.RegisterId == registerId && s.Status == SessionStatuses.Open, cancellationToken))
        {
            return Error.Conflict("session.already_open", "The register already has an open session; close it first.").WithWhy(("register", register.Code));
        }

        var who = principal.Required;
        var now = clock.UtcNow;
        var today = clock.TodayIn(company.TimeZone);
        var session = new PosSession
        {
            Id = Guid.CreateVersion7(),
            CompanyId = register.CompanyId,
            RegisterId = register.Id,
            CashierUserId = who.UserId.Value,
            CashierName = who.DisplayName,
            OpenedAt = now,
            OpeningFloat = request.OpeningFloat,
            Notes = Validation.Trim(request.Notes),
            CreatedAt = now,
            UpdatedAt = now,
        };
        await numbering.EnsureDefaultSeriesAsync(PosDocumentTypes.Session, company.Id, "SES", "SES-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(PosDocumentTypes.Session, company.Id, register.BranchId is { } b ? new BranchId(b) : null, today, session.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        session.Number = number.Value.Text;
        db.Sessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, session.Id, session.Number, AuditActions.Created, After: new { register = register.Code, session.OpeningFloat, cashier = session.CashierName }, CompanyId: session.CompanyId), cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    /// <summary>Closes the session: the expected cash from the orders synced so far, the difference against what was counted, booked on the cash box when it is not zero.</summary>
    public async Task<Result<PosSessionSummary>> CloseAsync(Guid id, CloseSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (session is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (session.Status != SessionStatuses.Open)
        {
            return Error.Conflict("session.not_open", "The session is already closed.").WithWhy(("status", session.Status));
        }

        var register = (await db.Registers.AsNoTracking().SingleAsync(r => r.Id == session.RegisterId, cancellationToken))!;
        var company = (await companies.FindAsync(new CompanyId(session.CompanyId), cancellationToken))!;
        var currency = company.FunctionalCurrency;
        if (request.CountedCash < 0m || !new Money(request.CountedCash, currency).IsRoundedToMinorUnit)
        {
            return Error.Validation("session.counted_invalid", "The counted cash is zero or above, in the company's currency.");
        }

        var now = clock.UtcNow;
        var today = clock.TodayIn(company.TimeZone);
        session.ExpectedCash = session.OpeningFloat + session.CashTaken;
        session.CountedCash = request.CountedCash;
        session.Difference = RegisterService.Round(request.CountedCash - session.ExpectedCash.Value, company);
        if (session.Difference != 0m)
        {
            // Over: Dr Cash / Cr Suspense; short: Dr Suspense / Cr Cash, both on the cash box so its book balance is what was counted (POSTING_RULES §5).
            var box = (await bankAccounts.FindAsync(register.CashAccountId, cancellationToken))!;
            var transactionId = Guid.CreateVersion7();
            var difference = session.Difference.Value;
            var lines = new List<PostingLine>
            {
                new(RoleOf(box.Kind), difference, new PostingKeys(PosDocumentTypes.Session, BankAccountId: box.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId),
                new(AccountRoles.Suspense, -difference, new PostingKeys(PosDocumentTypes.Session), Description: LocalizedText.Bilingual("Cash difference at session close", "فرق النقد عند إغلاق الوردية")),
            };
            var description = LocalizedText.Bilingual($"Session {session.Number}: cash {(difference > 0m ? "over" : "short")} at close", $"الوردية {session.Number}: {(difference > 0m ? "زيادة" : "عجز")} نقدي عند الإغلاق");
            var posted = await posting.PostAsync(new PostingRequest(company.Id, "pos", PosDocumentTypes.Session, session.Id, today, currency.Code, lines, session.Number, today, description,
                register.BranchId is { } b ? new BranchId(b) : null, IdempotencyKey: $"pos_session:{session.Id}"), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            await bankTransactions.RecordAsync(new NewBankTransaction(transactionId, box.Id, session.CompanyId, today, difference > 0m ? "receipt" : "disbursement", difference, difference, session.Number, PosDocumentTypes.Session, session.Id, posted.Value.EntryId), cancellationToken);
            session.DifferenceJournalEntryId = posted.Value.EntryId;
        }

        session.Status = SessionStatuses.Closed;
        session.ClosedAt = now;
        session.UpdatedAt = now;
        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            session.Notes = string.IsNullOrWhiteSpace(session.Notes) ? request.Notes.Trim() : session.Notes + "\n" + request.Notes.Trim();
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, session.Id, session.Number, AuditActions.StateChanged, After: new { status = session.Status, session.ExpectedCash, session.CountedCash, session.Difference, journalEntryId = session.DifferenceJournalEntryId }, CompanyId: session.CompanyId), cancellationToken);
        return await MapAsync(session, cancellationToken);
    }

    internal static string RoleOf(string kind) => kind switch
    {
        BankAccountKinds.Cash => AccountRoles.Cash,
        BankAccountKinds.PettyCash => AccountRoles.PettyCash,
        _ => AccountRoles.Bank,
    };

    internal async Task<PosSessionSummary> MapAsync(PosSession s, CancellationToken cancellationToken)
    {
        var register = await db.Registers.AsNoTracking().Where(r => r.Id == s.RegisterId).Select(static r => new { r.Code, r.Name }).SingleAsync(cancellationToken);
        var company = await companies.FindAsync(new CompanyId(s.CompanyId), cancellationToken);
        return new PosSessionSummary(s.Id, s.CompanyId, s.RegisterId, register.Code, register.Name.Values, s.Number, s.Status, s.CashierUserId, s.CashierName, s.OpenedAt, s.ClosedAt, s.OpeningFloat, s.CashTaken, s.CardTaken, s.CreditTaken,
            s.OrdersCount, s.SalesTotal, s.ExpectedCash, s.CountedCash, s.Difference, s.DifferenceJournalEntryId, company?.FunctionalCurrency.Code ?? string.Empty, s.Notes);
    }
}
