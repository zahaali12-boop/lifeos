using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Receivables.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// The life of a cheque after its document posted (roadmap 6.3, POSTING_RULES §5, hard scenario 10). A received
/// cheque sits on PDC receivable from the receipt, moves to cheques under collection on the deposit slip and to the
/// bank when it clears; when it bounces the receipt is reversed (its invoices reopen at their booked values), the
/// cheque's journey from PDC to the bank is unwound, the bank's charge is booked and a fee can be re-charged to the
/// customer as a new receivable. An issued cheque sits on PDC payable from the payment and reaches the bank when
/// paid; returned unpaid it goes back to PDC payable, and cancelling it reverses the payment. Every step is an event
/// on the cheque, and the registers and the maturity forecast read from the cheques themselves.
/// </summary>
public sealed class ChequeService(
    BankingDbContext db,
    ChequeRegistry registry,
    PaymentService payments,
    CustomerReceiptService receipts,
    ICompanyDirectory companies,
    IReceivables receivables,
    INumberAllocator numbering,
    IPostingService posting,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    private const string Module = "banking";

    public async Task<IReadOnlyList<ChequeSummary>> ListAsync(Guid? companyId, string? direction, string? status, Guid? partnerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.Cheques.AsNoTracking().Where(static c => c.Status != ChequeStatuses.Draft);
        if (companyId is { } c)
        {
            query = query.Where(x => x.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(direction))
        {
            query = query.Where(x => x.Direction == direction);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(x => x.Status == status);
        }

        if (partnerId is { } p)
        {
            query = query.Where(x => x.PartnerId == p);
        }

        if (from is { } f)
        {
            query = query.Where(x => x.DueDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(x => x.DueDate <= t);
        }

        var cheques = await query.OrderBy(static x => x.DueDate).ThenBy(static x => x.ChequeNumber).Take(2000).ToListAsync(cancellationToken);
        var result = new List<ChequeSummary>(cheques.Count);
        foreach (var cheque in cheques)
        {
            result.Add(await registry.MapAsync(cheque, false, cancellationToken));
        }

        return result;
    }

    public async Task<Result<ChequeSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var cheque = await db.Cheques.AsNoTracking().Include(static c => c.Events).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        return cheque is null ? Error.NotFound(BankDocumentTypes.Cheque, id) : await registry.MapAsync(cheque, true, cancellationToken);
    }

    /// <summary>Cheques falling due month by month: received ones still to clear, issued ones still to be paid.</summary>
    public async Task<IReadOnlyList<ChequeForecastRow>> ForecastAsync(Guid companyId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var cheques = await db.Cheques.AsNoTracking()
            .Where(c => c.CompanyId == companyId && c.DueDate >= from && c.DueDate <= to
                && ((c.Direction == ChequeDirections.Received && (c.Status == ChequeStatuses.InHand || c.Status == ChequeStatuses.Deposited))
                    || (c.Direction == ChequeDirections.Issued && (c.Status == ChequeStatuses.Issued || c.Status == ChequeStatuses.Bounced))))
            .ToListAsync(cancellationToken);
        var rows = new List<ChequeForecastRow>();
        for (var month = new DateOnly(from.Year, from.Month, 1); month <= to; month = month.AddMonths(1))
        {
            var next = month.AddMonths(1);
            var inMonth = cheques.Where(c => c.DueDate >= month && c.DueDate < next).ToList();
            var received = inMonth.Where(static c => c.Direction == ChequeDirections.Received).ToList();
            var issued = inMonth.Where(static c => c.Direction == ChequeDirections.Issued).ToList();
            var receivedFc = received.Sum(static c => c.AmountFc);
            var issuedFc = issued.Sum(static c => c.AmountFc);
            rows.Add(new ChequeForecastRow(month, received.Count, receivedFc, issued.Count, issuedFc, receivedFc - issuedFc));
        }

        return rows;
    }

    // ------------------------------------------------------------------ deposits

    public async Task<IReadOnlyList<ChequeDepositSummary>> ListDepositsAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.ChequeDeposits.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(d => d.CompanyId == c);
        }

        var deposits = await query.OrderByDescending(static d => d.DepositDate).ThenByDescending(static d => d.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<ChequeDepositSummary>(deposits.Count);
        foreach (var deposit in deposits)
        {
            result.Add(await MapDepositAsync(deposit, false, cancellationToken));
        }

        return result;
    }

    public async Task<Result<ChequeDepositSummary>> GetDepositAsync(Guid id, CancellationToken cancellationToken)
    {
        var deposit = await db.ChequeDeposits.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        return deposit is null ? Error.NotFound(BankDocumentTypes.ChequeDeposit, id) : await MapDepositAsync(deposit, true, cancellationToken);
    }

    /// <summary>Takes cheques in hand to a bank account on one slip: each moves from PDC receivable to cheques under collection at its booked value.</summary>
    public async Task<Result<ChequeDepositSummary>> DepositAsync(DepositChequesRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ChequeIds is not { Count: > 0 })
        {
            return Error.Validation("deposit.no_cheques", "A deposit slip names at least one cheque.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.BankAccountId, cancellationToken);
        if (bank is null || bank.CompanyId != request.CompanyId || !bank.IsActive || bank.Kind != BankAccountKinds.Bank)
        {
            return Error.Validation("deposit.bank_account_invalid", "Cheques are deposited to an active bank account of the company.").WithWhy(("bankAccountId", request.BankAccountId));
        }

        var ids = request.ChequeIds.Distinct().ToList();
        var cheques = await db.Cheques.Include(static c => c.Events).Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken);
        if (cheques.Count != ids.Count)
        {
            return Error.NotFound(BankDocumentTypes.Cheque, ids.First(i => cheques.All(c => c.Id != i)));
        }

        foreach (var cheque in cheques)
        {
            if (cheque.CompanyId != request.CompanyId || cheque.Direction != ChequeDirections.Received)
            {
                return Error.Validation("deposit.cheque_invalid", "Only received cheques of the company are deposited.").WithWhy(("cheque", cheque.ChequeNumber));
            }

            if (cheque.Status != ChequeStatuses.InHand)
            {
                return Error.Conflict("deposit.cheque_not_in_hand", "Only a cheque in hand is deposited.").WithWhy(("cheque", cheque.ChequeNumber), ("status", cheque.Status));
            }

            if (!string.Equals(cheque.Currency, bank.Currency, StringComparison.Ordinal))
            {
                return Error.Validation("cheque.bank_currency_mismatch", "A cheque is deposited to an account in its own currency.").WithWhy(("cheque", cheque.ChequeNumber), ("chequeCurrency", cheque.Currency), ("bankCurrency", bank.Currency));
            }
        }

        var depositDate = request.DepositDate ?? clock.TodayIn(company.TimeZone);
        var early = cheques.FirstOrDefault(c => c.DueDate > depositDate);
        if (early is not null)
        {
            return Error.Conflict("deposit.before_due", "A post-dated cheque is deposited on or after its date.").WithWhy(("cheque", early.ChequeNumber), ("dueDate", early.DueDate), ("depositDate", depositDate));
        }

        var deposit = new ChequeDeposit
        {
            Id = Guid.CreateVersion7(),
            CompanyId = request.CompanyId,
            BankAccountId = bank.Id,
            DepositDate = depositDate,
            Currency = bank.Currency,
            Total = cheques.Sum(static c => c.Amount),
            Status = "deposited",
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        await numbering.EnsureDefaultSeriesAsync(BankDocumentTypes.ChequeDeposit, new CompanyId(deposit.CompanyId), "CHD", "CHD-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(BankDocumentTypes.ChequeDeposit, new CompanyId(deposit.CompanyId), null, depositDate, deposit.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        deposit.Number = number.Value.Text;
        var keys = new PostingKeys(BankDocumentTypes.ChequeDeposit);
        var lines = new List<PostingLine>();
        foreach (var cheque in cheques)
        {
            lines.Add(new PostingLine(AccountRoles.ChequesUnderCollection, cheque.Amount, keys, PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: cheque.AmountFc, Description: LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} deposited", $"إيداع الشيك {cheque.ChequeNumber}")));
            lines.Add(new PostingLine(AccountRoles.PdcReceivable, -cheque.Amount, keys, PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: -cheque.AmountFc));
        }

        var rate = await RateOfAsync(cheques[0], company, cancellationToken);
        var posted = await posting.PostAsync(new PostingRequest(company.Id, Module, BankDocumentTypes.ChequeDeposit, deposit.Id, depositDate, deposit.Currency, lines, deposit.Number, depositDate,
            LocalizedText.Bilingual($"Cheque deposit {deposit.Number} to {bank.Code}", $"إيداع شيكات {deposit.Number} في {bank.Code}"), null, RateTypes.Spot, rate, rate is null ? null : "Cheques' booked rate", $"bank_cheque_deposit:{deposit.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        deposit.JournalEntryId = posted.Value.EntryId;
        db.ChequeDeposits.Add(deposit);
        foreach (var cheque in cheques)
        {
            cheque.Status = ChequeStatuses.Deposited;
            cheque.DepositId = deposit.Id;
            cheque.BankAccountId = bank.Id;
            cheque.UpdatedAt = clock.UtcNow;
            cheque.Events.Add(registry.Event(cheque, "deposited", depositDate, 0m, 0m, deposit.Number, posted.Value.EntryId));
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.ChequeDeposit, deposit.Id, deposit.Number, AuditActions.Posted, After: new { deposit.BankAccountId, deposit.DepositDate, deposit.Total, cheques = cheques.Count, journal = posted.Value.Number }, CompanyId: deposit.CompanyId), cancellationToken);
        return await MapDepositAsync(deposit, true, cancellationToken);
    }

    /// <summary>Clears every deposited cheque of a slip on one day.</summary>
    public async Task<Result<ChequeDepositSummary>> ClearDepositAsync(Guid depositId, ChequeActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deposit = await db.ChequeDeposits.SingleOrDefaultAsync(d => d.Id == depositId, cancellationToken);
        if (deposit is null)
        {
            return Error.NotFound(BankDocumentTypes.ChequeDeposit, depositId);
        }

        var pending = await db.Cheques.AsNoTracking().Where(c => c.DepositId == depositId && c.Status == ChequeStatuses.Deposited).Select(static c => c.Id).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return Error.Conflict("deposit.nothing_to_clear", "No cheque of this slip is waiting to clear.").WithWhy(("status", deposit.Status));
        }

        var charge = request.BankCharge;
        foreach (var id in pending)
        {
            // The slip's charge, when one is given, is taken once, with the first cheque.
            var cleared = await ClearAsync(id, request with { BankCharge = charge }, cancellationToken);
            if (cleared.IsFailure)
            {
                return cleared.Error!;
            }

            charge = 0m;
        }

        return await GetDepositAsync(depositId, cancellationToken);
    }

    // ------------------------------------------------------------------ received cheques

    /// <summary>The bank credited a deposited cheque: cheques under collection to the bank, less what the bank charged.</summary>
    public async Task<Result<ChequeSummary>> ClearAsync(Guid id, ChequeActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, ChequeDirections.Received, ChequeStatuses.Deposited, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (cheque, company, bank, payment) = loaded.Value;
        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var dated = DatedAfter(cheque, date);
        if (dated.IsFailure)
        {
            return dated.Error!;
        }

        if (request.BankCharge < 0m || request.BankCharge >= cheque.Amount)
        {
            return Error.Validation("cheque.charge_invalid", "The bank's charge is less than the cheque.").WithWhy(("bankCharge", request.BankCharge), ("amount", cheque.Amount));
        }

        var money = Money(company, payment);
        var transactionId = Guid.CreateVersion7();
        var chargeId = Guid.CreateVersion7();
        var keys = new PostingKeys(BankDocumentTypes.Cheque, BankAccountId: bank.Id);
        var lines = new List<PostingLine>
        {
            new(BankAccountService.RoleOf(bank.Kind), cheque.Amount, keys, SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: cheque.AmountFc, Description: LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} cleared", $"تحصيل الشيك {cheque.ChequeNumber}")),
            new(AccountRoles.ChequesUnderCollection, -cheque.Amount, new PostingKeys(BankDocumentTypes.Cheque), PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: -cheque.AmountFc),
        };
        AddCharge(lines, request.BankCharge, money, bank, chargeId);
        var posted = await posting.PostAsync(new PostingRequest(company.Id, Module, BankDocumentTypes.Cheque, cheque.Id, date, cheque.Currency, lines, cheque.ChequeNumber, date,
            LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} cleared at {bank.Code}", $"تحصيل الشيك {cheque.ChequeNumber} في {bank.Code}"), null, RateTypes.Spot, money.Rate, money.Rate is null ? null : "Cheque's booked rate", $"bank_cheque:{cheque.Id}:cleared:{cheque.Events.Count}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        AddTransaction(transactionId, bank, cheque, date, "receipt", cheque.Amount, cheque.AmountFc, posted.Value.EntryId, null);
        if (request.BankCharge > 0m)
        {
            AddTransaction(chargeId, bank, cheque, date, "charge", -request.BankCharge, -money.Fc(request.BankCharge), posted.Value.EntryId, null);
        }

        cheque.Status = ChequeStatuses.Cleared;
        cheque.BankTransactionId = transactionId;
        cheque.UpdatedAt = clock.UtcNow;
        cheque.Events.Add(registry.Event(cheque, "cleared", date, request.BankCharge, 0m, request.Reason, posted.Value.EntryId));
        await RefreshDepositAsync(cheque.DepositId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Cheque, cheque.Id, cheque.ChequeNumber, AuditActions.Posted, After: new { status = cheque.Status, date, request.BankCharge, journal = posted.Value.Number }, CompanyId: cheque.CompanyId), cancellationToken);
        return await registry.MapAsync(cheque, true, cancellationToken);
    }

    /// <summary>
    /// The bank returned the cheque unpaid. Received: the receipt is reversed (its invoices reopen at their booked
    /// values), the cheque's move from PDC receivable to collection or the bank is unwound, the bank's charge is booked
    /// and the fee re-charged to the customer opens a receivable on them. Issued (paid): the bank movement is unwound and
    /// the cheque is back on PDC payable until paid again or cancelled.
    /// </summary>
    public async Task<Result<ChequeSummary>> BounceAsync(Guid id, BounceChequeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cheque = await db.Cheques.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (cheque is null)
        {
            return Error.NotFound(BankDocumentTypes.Cheque, id);
        }

        return cheque.Direction == ChequeDirections.Issued ? await BounceIssuedAsync(id, request, cancellationToken) : await BounceReceivedAsync(id, request, cancellationToken);
    }

    /// <summary>A cheque in hand given back to the customer: the receipt is reversed and the cheque returned.</summary>
    public async Task<Result<ChequeSummary>> ReturnAsync(Guid id, ChequeActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, ChequeDirections.Received, ChequeStatuses.InHand, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var reversed = await receipts.ReverseAsync(loaded.Value.Payment.Id, new ReversePaymentRequest(request.Reason ?? "Cheque returned to the customer", request.Date), cancellationToken);
        return reversed.IsFailure ? reversed.Error! : await GetAsync(id, cancellationToken);
    }

    // ------------------------------------------------------------------ issued cheques

    /// <summary>The bank paid an issued cheque: PDC payable to the bank, plus what the bank charged.</summary>
    public async Task<Result<ChequeSummary>> PayAsync(Guid id, ChequeActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, ChequeDirections.Issued, null, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (cheque, company, bank, payment) = loaded.Value;
        if (cheque.Status is not (ChequeStatuses.Issued or ChequeStatuses.Bounced))
        {
            return Error.Conflict("cheque.not_payable", "Only an issued cheque not yet paid is paid.").WithWhy(("status", cheque.Status));
        }

        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var dated = DatedAfter(cheque, date);
        if (dated.IsFailure)
        {
            return dated.Error!;
        }

        if (request.BankCharge < 0m)
        {
            return Error.Validation("cheque.charge_invalid", "The bank's charge is not negative.").WithWhy(("bankCharge", request.BankCharge));
        }

        var money = Money(company, payment);
        var transactionId = Guid.CreateVersion7();
        var chargeId = Guid.CreateVersion7();
        var lines = new List<PostingLine>
        {
            new(AccountRoles.PdcPayable, cheque.Amount, new PostingKeys(BankDocumentTypes.Cheque), PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: cheque.AmountFc, Description: LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} paid", $"صرف الشيك {cheque.ChequeNumber}")),
            new(BankAccountService.RoleOf(bank.Kind), -cheque.Amount, new PostingKeys(BankDocumentTypes.Cheque, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: -cheque.AmountFc),
        };
        AddCharge(lines, request.BankCharge, money, bank, chargeId);
        var posted = await posting.PostAsync(new PostingRequest(company.Id, Module, BankDocumentTypes.Cheque, cheque.Id, date, cheque.Currency, lines, cheque.ChequeNumber, date,
            LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} paid from {bank.Code}", $"صرف الشيك {cheque.ChequeNumber} من {bank.Code}"), null, RateTypes.Spot, money.Rate, money.Rate is null ? null : "Cheque's booked rate", $"bank_cheque:{cheque.Id}:paid:{cheque.Events.Count}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        AddTransaction(transactionId, bank, cheque, date, "disbursement", -cheque.Amount, -cheque.AmountFc, posted.Value.EntryId, null);
        if (request.BankCharge > 0m)
        {
            AddTransaction(chargeId, bank, cheque, date, "charge", -request.BankCharge, -money.Fc(request.BankCharge), posted.Value.EntryId, null);
        }

        cheque.Status = ChequeStatuses.Paid;
        cheque.BankTransactionId = transactionId;
        cheque.UpdatedAt = clock.UtcNow;
        cheque.Events.Add(registry.Event(cheque, "paid", date, request.BankCharge, 0m, request.Reason, posted.Value.EntryId));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Cheque, cheque.Id, cheque.ChequeNumber, AuditActions.Posted, After: new { status = cheque.Status, date, request.BankCharge, journal = posted.Value.Number }, CompanyId: cheque.CompanyId), cancellationToken);
        return await registry.MapAsync(cheque, true, cancellationToken);
    }

    /// <summary>An issued cheque not paid by the bank is cancelled: the payment is reversed and its invoices reopen.</summary>
    public async Task<Result<ChequeSummary>> CancelAsync(Guid id, ChequeActionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, ChequeDirections.Issued, null, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var reversed = await payments.ReverseAsync(loaded.Value.Payment.Id, new ReversePaymentRequest(request.Reason ?? "Cheque cancelled", request.Date), cancellationToken);
        return reversed.IsFailure ? reversed.Error! : await GetAsync(id, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result<ChequeSummary>> BounceReceivedAsync(Guid id, BounceChequeRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, ChequeDirections.Received, null, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (cheque, company, bank, receipt) = loaded.Value;
        if (cheque.Status is not (ChequeStatuses.Deposited or ChequeStatuses.Cleared))
        {
            return Error.Conflict("cheque.not_bounceable", "A cheque bounces after it was deposited; one still in hand is returned to the customer instead.").WithWhy(("status", cheque.Status));
        }

        if (request.BankCharge < 0m || request.CustomerFee < 0m)
        {
            return Error.Validation("cheque.charge_invalid", "Charges are not negative.").WithWhy(("bankCharge", request.BankCharge), ("customerFee", request.CustomerFee));
        }

        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var dated = DatedAfter(cheque, date);
        if (dated.IsFailure)
        {
            return dated.Error!;
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? $"Cheque {cheque.ChequeNumber} bounced" : request.Reason.Trim();

        // 1. The receipt is undone: its journal mirrored, the invoices it settled reopened, its own item reversed.
        var reversed = await posting.ReverseAsync(receipt.JournalEntryId!.Value, date, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var settlements = await receivables.ReverseSettlementsOfAsync(receipt.JournalEntryId.Value, reversed.Value.PostingDate, reversed.Value.EntryId, reason, cancellationToken);
        if (settlements.IsFailure)
        {
            return settlements.Error!.Code == "receivables.remainder_applied"
                ? Error.Conflict("cheque.receipt_applied", "The receipt's remainder was applied to invoices; reverse those applications first.").WithWhy(("settlements", settlements.Error!.Why))
                : settlements.Error!;
        }

        var own = await receivables.ReverseDocumentAsync(BankDocumentTypes.Receipt, receipt.Id, reversed.Value.PostingDate, cancellationToken);
        if (own.IsFailure)
        {
            return own.Error!;
        }

        receipt.Status = "reversed";
        receipt.ReversalEntryId = reversed.Value.EntryId;
        receipt.ReversalReason = reason;
        receipt.ReversedAt = clock.UtcNow;
        receipt.ReversedBy = principal.Principal?.MembershipId.Value;
        receipt.UpdatedAt = clock.UtcNow;

        // 2. The cheque's journey is unwound: back out of collection (or the bank), the charge booked, the fee re-charged.
        var money = Money(company, receipt);
        var wasCleared = cheque.Status == ChequeStatuses.Cleared;
        var reversalId = Guid.CreateVersion7();
        var chargeId = Guid.CreateVersion7();
        var keys = new PostingKeys(BankDocumentTypes.Cheque);
        var lines = new List<PostingLine>
        {
            new(AccountRoles.PdcReceivable, cheque.Amount, keys, PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: cheque.AmountFc, Description: LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} bounced", $"ارتداد الشيك {cheque.ChequeNumber}")),
            wasCleared
                ? new PostingLine(BankAccountService.RoleOf(bank.Kind), -cheque.Amount, new PostingKeys(BankDocumentTypes.Cheque, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: reversalId, AmountFc: -cheque.AmountFc)
                : new PostingLine(AccountRoles.ChequesUnderCollection, -cheque.Amount, keys, PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: -cheque.AmountFc),
        };
        AddCharge(lines, request.BankCharge, money, bank, chargeId);
        if (request.CustomerFee > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.AR, request.CustomerFee, keys, PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.Receivables, SubledgerRef: cheque.Id, AmountFc: money.Fc(request.CustomerFee), Description: LocalizedText.Bilingual("Returned cheque fee re-charged", "رسم شيك مرتد محمّل على العميل")));
            lines.Add(new PostingLine(AccountRoles.InterestIncome, -request.CustomerFee, keys, PartnerId: cheque.PartnerId, AmountFc: -money.Fc(request.CustomerFee)));
        }

        var posted = await posting.PostAsync(new PostingRequest(company.Id, Module, BankDocumentTypes.Cheque, cheque.Id, date, cheque.Currency, lines, cheque.ChequeNumber, date,
            LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} bounced", $"ارتداد الشيك {cheque.ChequeNumber}"), null, RateTypes.Spot, money.Rate, money.Rate is null ? null : "Cheque's booked rate", $"bank_cheque:{cheque.Id}:bounced:{cheque.Events.Count}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        if (wasCleared)
        {
            AddTransaction(reversalId, bank, cheque, date, "reversal", -cheque.Amount, -cheque.AmountFc, posted.Value.EntryId, cheque.BankTransactionId);
        }

        if (request.BankCharge > 0m)
        {
            AddTransaction(chargeId, bank, cheque, date, "charge", -request.BankCharge, -money.Fc(request.BankCharge), posted.Value.EntryId, null);
        }

        if (request.CustomerFee > 0m)
        {
            var fee = await receivables.OpenAsync(new NewReceivable(cheque.CompanyId, cheque.PartnerId, ReceivableKinds.Adjustment, BankDocumentTypes.Cheque, cheque.Id, $"CHQ {cheque.ChequeNumber}", 1, null, date, date, date, null, 0m, cheque.Currency, request.CustomerFee, money.Fc(request.CustomerFee), money.Rate ?? 1m, posted.Value.EntryId), cancellationToken);
            cheque.FeeOpenItemId = fee.Id;
        }

        cheque.Status = ChequeStatuses.Bounced;
        cheque.UpdatedAt = clock.UtcNow;
        cheque.Events.Add(registry.Event(cheque, "bounced", date, request.BankCharge, request.CustomerFee, reason, posted.Value.EntryId));
        await RefreshDepositAsync(cheque.DepositId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Cheque, cheque.Id, cheque.ChequeNumber, AuditActions.Reversed, After: new { status = cheque.Status, date, request.BankCharge, request.CustomerFee, receipt = receipt.Number, journal = posted.Value.Number }, Reason: reason, CompanyId: cheque.CompanyId), cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Receipt, receipt.Id, receipt.Number, AuditActions.Reversed, After: new { status = receipt.Status, reversalDate = date, journal = reversed.Value.Number }, Reason: reason, CompanyId: receipt.CompanyId), cancellationToken);
        return await registry.MapAsync(cheque, true, cancellationToken);
    }

    private async Task<Result<ChequeSummary>> BounceIssuedAsync(Guid id, BounceChequeRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, ChequeDirections.Issued, ChequeStatuses.Paid, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (cheque, company, bank, payment) = loaded.Value;
        if (request.BankCharge < 0m || request.CustomerFee != 0m)
        {
            return Error.Validation("cheque.charge_invalid", "An issued cheque returned unpaid carries the bank's charge only.").WithWhy(("bankCharge", request.BankCharge), ("customerFee", request.CustomerFee));
        }

        var date = request.Date ?? clock.TodayIn(company.TimeZone);
        var dated = DatedAfter(cheque, date);
        if (dated.IsFailure)
        {
            return dated.Error!;
        }

        var money = Money(company, payment);
        var reversalId = Guid.CreateVersion7();
        var chargeId = Guid.CreateVersion7();
        var lines = new List<PostingLine>
        {
            new(BankAccountService.RoleOf(bank.Kind), cheque.Amount, new PostingKeys(BankDocumentTypes.Cheque, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: reversalId, AmountFc: cheque.AmountFc, Description: LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} returned unpaid", $"إعادة الشيك {cheque.ChequeNumber} دون صرف")),
            new(AccountRoles.PdcPayable, -cheque.Amount, new PostingKeys(BankDocumentTypes.Cheque), PartnerId: cheque.PartnerId, SubledgerType: SubledgerTypes.PostDatedCheques, SubledgerRef: cheque.Id, AmountFc: -cheque.AmountFc),
        };
        AddCharge(lines, request.BankCharge, money, bank, chargeId);
        var posted = await posting.PostAsync(new PostingRequest(company.Id, Module, BankDocumentTypes.Cheque, cheque.Id, date, cheque.Currency, lines, cheque.ChequeNumber, date,
            LocalizedText.Bilingual($"Cheque {cheque.ChequeNumber} returned unpaid", $"إعادة الشيك {cheque.ChequeNumber} دون صرف"), null, RateTypes.Spot, money.Rate, money.Rate is null ? null : "Cheque's booked rate", $"bank_cheque:{cheque.Id}:bounced:{cheque.Events.Count}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        AddTransaction(reversalId, bank, cheque, date, "reversal", cheque.Amount, cheque.AmountFc, posted.Value.EntryId, cheque.BankTransactionId);
        if (request.BankCharge > 0m)
        {
            AddTransaction(chargeId, bank, cheque, date, "charge", -request.BankCharge, -money.Fc(request.BankCharge), posted.Value.EntryId, null);
        }

        cheque.Status = ChequeStatuses.Bounced;
        cheque.UpdatedAt = clock.UtcNow;
        cheque.Events.Add(registry.Event(cheque, "bounced", date, request.BankCharge, 0m, request.Reason, posted.Value.EntryId));
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Cheque, cheque.Id, cheque.ChequeNumber, AuditActions.Reversed, After: new { status = cheque.Status, date, request.BankCharge, journal = posted.Value.Number }, Reason: request.Reason, CompanyId: cheque.CompanyId), cancellationToken);
        return await registry.MapAsync(cheque, true, cancellationToken);
    }

    private sealed record Loaded(Cheque Cheque, CompanyInfo Company, BankAccount Bank, Payment Payment);

    private async Task<Result<Loaded>> LoadAsync(Guid id, string direction, string? requiredStatus, CancellationToken cancellationToken)
    {
        var cheque = await db.Cheques.Include(static c => c.Events).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (cheque is null)
        {
            return Error.NotFound(BankDocumentTypes.Cheque, id);
        }

        if (cheque.Direction != direction)
        {
            return Error.Conflict("cheque.wrong_direction", direction == ChequeDirections.Received ? "This is an issued cheque." : "This is a received cheque.").WithWhy(("direction", cheque.Direction));
        }

        if (requiredStatus is not null && cheque.Status != requiredStatus)
        {
            return Error.Conflict("cheque.wrong_status", $"The cheque is {cheque.Status}, not {requiredStatus}.").WithWhy(("status", cheque.Status), ("required", requiredStatus));
        }

        var company = await companies.FindAsync(new CompanyId(cheque.CompanyId), cancellationToken);
        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == cheque.BankAccountId, cancellationToken);
        var payment = await db.Payments.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == cheque.PaymentId, cancellationToken);
        if (company is null || bank is null || payment is null)
        {
            return Error.NotFound(company is null ? "company" : bank is null ? "bank_account" : "bank_payment", cheque.Id);
        }

        return new Loaded(cheque, company, bank, payment);
    }

    private static Result DatedAfter(Cheque cheque, DateOnly date)
    {
        var last = cheque.Events.Count == 0 ? cheque.IssueDate : cheque.Events.Max(static e => e.EffectiveDate);
        return date < last
            ? Error.Validation("cheque.date_before_last_event", "A step of a cheque's life is dated on or after the one before it.").WithWhy(("date", date), ("lastEvent", last))
            : Result.Success();
    }

    private sealed record MoneyOf(decimal? Rate, Func<decimal, decimal> Fc);

    /// <summary>Functional values at the document's own rate, so every leg of the cheque carries what the receipt or payment booked.</summary>
    private static MoneyOf Money(CompanyInfo company, Payment document)
    {
        var fc = company.FunctionalCurrency;
        var same = string.Equals(document.Currency, fc.Code, StringComparison.Ordinal);
        var policy = new RoundingPolicy(company.RoundingMode);
        return new MoneyOf(same ? null : document.ExchangeRate, tc => same ? tc : policy.Round(tc * document.ExchangeRate, fc.MinorUnits));
    }

    private async Task<decimal?> RateOfAsync(Cheque cheque, CompanyInfo company, CancellationToken cancellationToken)
    {
        if (string.Equals(cheque.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            return null;
        }

        return await db.Payments.AsNoTracking().Where(p => p.Id == cheque.PaymentId).Select(static p => p.ExchangeRate).SingleAsync(cancellationToken);
    }

    private static void AddCharge(List<PostingLine> lines, decimal charge, MoneyOf money, BankAccount bank, Guid transactionId)
    {
        if (charge <= 0m)
        {
            return;
        }

        lines.Add(new PostingLine(AccountRoles.BankCharges, charge, new PostingKeys(BankDocumentTypes.Cheque), AmountFc: money.Fc(charge), Description: LocalizedText.Bilingual("Bank charge", "عمولة بنكية")));
        lines.Add(new PostingLine(BankAccountService.RoleOf(bank.Kind), -charge, new PostingKeys(BankDocumentTypes.Cheque, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: -money.Fc(charge)));
    }

    private void AddTransaction(Guid id, BankAccount bank, Cheque cheque, DateOnly date, string kind, decimal amountTc, decimal amountFc, Guid entryId, Guid? reverses)
    {
        db.Transactions.Add(new BankTransaction
        {
            Id = id,
            BankAccountId = bank.Id,
            CompanyId = cheque.CompanyId,
            PostingDate = date,
            ValueDate = date,
            Kind = kind,
            AmountTc = amountTc,
            AmountFc = amountFc,
            Reference = $"CHQ {cheque.ChequeNumber}",
            SourceDocumentType = BankDocumentTypes.Cheque,
            SourceDocumentId = cheque.Id,
            JournalEntryId = entryId,
            ReversesTransactionId = reverses,
            CreatedAt = clock.UtcNow,
        });
    }

    private async Task RefreshDepositAsync(Guid? depositId, CancellationToken cancellationToken)
    {
        if (depositId is not { } id)
        {
            return;
        }

        var deposit = await db.ChequeDeposits.SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (deposit is null)
        {
            return;
        }

        var statuses = db.Cheques.Local.Where(c => c.DepositId == id).Select(static c => c.Status).ToList();
        var stored = await db.Cheques.AsNoTracking().Where(c => c.DepositId == id && !db.Cheques.Local.Select(static l => l.Id).Contains(c.Id)).Select(static c => c.Status).ToListAsync(cancellationToken);
        statuses.AddRange(stored);
        deposit.Status = statuses.All(static s => s == ChequeStatuses.Bounced) ? "bounced"
            : statuses.Any(static s => s == ChequeStatuses.Bounced) ? "partially_bounced"
            : statuses.All(static s => s == ChequeStatuses.Cleared) ? "cleared"
            : "deposited";
        deposit.UpdatedAt = clock.UtcNow;
    }

    private async Task<ChequeDepositSummary> MapDepositAsync(ChequeDeposit d, bool withCheques, CancellationToken cancellationToken)
    {
        var bank = await db.BankAccounts.AsNoTracking().Where(b => b.Id == d.BankAccountId).Select(static b => b.Code).SingleOrDefaultAsync(cancellationToken);
        var cheques = await db.Cheques.AsNoTracking().Where(c => c.DepositId == d.Id).OrderBy(static c => c.ChequeNumber).ToListAsync(cancellationToken);
        var mapped = new List<ChequeSummary>();
        if (withCheques)
        {
            foreach (var cheque in cheques)
            {
                mapped.Add(await registry.MapAsync(cheque, false, cancellationToken));
            }
        }

        return new ChequeDepositSummary(d.Id, d.CompanyId, d.Number, d.BankAccountId, bank ?? string.Empty, d.DepositDate, d.Currency, d.Total, cheques.Count, d.Status, d.JournalEntryId, d.CreatedAt, mapped);
    }
}
