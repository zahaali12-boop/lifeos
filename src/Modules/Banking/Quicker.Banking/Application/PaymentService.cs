using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// Supplier payments and advances (roadmap 4.7, POSTING_RULES §4 "Supplier payment", hard scenario 3). A payment settles
/// invoice open items in their currency: each settled item leaves the books at its booked functional value, discounts
/// taken and tax withheld at payment reduce the cash, charges are what the bank took besides, the bank moves by what
/// the bank actually took, and the functional difference all of that leaves is realised FX (ADR-0031). Whatever is
/// paid beyond the lines stays on account as the payment's own open item; an advance is such an item on the
/// supplier-advances control. Reversal mirrors the journal and reopens the items at their booked values.
/// </summary>
public sealed class PaymentService(
    BankingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IPartnerDirectory partners,
    IPayables payables,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IPostingService posting,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = BankDocumentTypes.Payment;

    public static readonly IReadOnlyList<string> Kinds = ["supplier_payment", "supplier_advance"];

    public static readonly IReadOnlyList<string> Methods = ["transfer", "cash", "cheque"];

    public async Task<IReadOnlyList<PaymentSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Payments.AsNoTracking().Include(static p => p.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(p => p.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(p => p.Status == status);
        }

        if (partnerId is { } pt)
        {
            query = query.Where(p => p.PartnerId == pt);
        }

        var payments = await query.OrderByDescending(static p => p.PaymentDate).ThenByDescending(static p => p.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<PaymentSummary>(payments.Count);
        foreach (var payment in payments)
        {
            result.Add(await MapAsync(payment, cancellationToken));
        }

        return result;
    }

    public async Task<Result<PaymentSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.AsNoTracking().Include(static p => p.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        return payment is null ? Error.NotFound(DocumentType, id) : await MapAsync(payment, cancellationToken);
    }

    public async Task<Result<PaymentSummary>> CreateAsync(SavePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payment = new Payment { Id = Guid.CreateVersion7(), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(payment, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(payment.CompanyId), "PAY", "PAY-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(payment.CompanyId), null, payment.PaymentDate, payment.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        payment.Number = number.Value.Text;
        db.Payments.Add(payment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, payment.Id, payment.Number, AuditActions.Created, After: new { payment.Kind, payment.AmountTc, payment.Currency, lines = payment.Lines.Count }, CompanyId: payment.CompanyId), cancellationToken);
        return await MapAsync(payment, cancellationToken);
    }

    public async Task<Result<PaymentSummary>> UpdateAsync(Guid id, SavePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payment = await db.Payments.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (payment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (payment.Status != "draft")
        {
            return Error.Conflict("payment.not_draft", "Only a draft payment is edited.").WithWhy(("status", payment.Status));
        }

        if (payment.CompanyId != request.CompanyId)
        {
            return Error.Validation("payment.company_locked", "A payment stays with its company.");
        }

        db.PaymentLines.RemoveRange(payment.Lines);
        payment.Lines.Clear();
        var applied = await ApplyAsync(payment, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        payment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(payment, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (payment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (payment.Status != "draft")
        {
            return Error.Conflict("payment.not_draft", "Only a draft payment is deleted.").WithWhy(("status", payment.Status));
        }

        db.Payments.Remove(payment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, payment.Id, payment.Number, AuditActions.Deleted, CompanyId: payment.CompanyId), cancellationToken);
        return Result.Success();
    }

    /// <summary>Drafts one payment per supplier from an approved proposal's selected, unpaid lines and marks the lines with them.</summary>
    public async Task<Result<IReadOnlyList<PaymentSummary>>> FromProposalAsync(PayProposalRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var proposal = await payables.ProposalAsync(request.ProposalId, cancellationToken);
        if (proposal is null)
        {
            return Error.NotFound(PayablesDocumentTypes.Proposal, request.ProposalId);
        }

        if (proposal.Status != "approved")
        {
            return Error.Conflict("payment.proposal_not_approved", "Only an approved proposal is paid.").WithWhy(("status", proposal.Status));
        }

        var open = proposal.Lines.Where(static l => l.Selected && l.PaymentId is null).ToList();
        if (open.Count == 0)
        {
            return Error.Conflict("payment.proposal_paid", "Every selected line of the proposal is already paid.");
        }

        var created = new List<PaymentSummary>();
        var byLine = new Dictionary<Guid, Guid>();
        foreach (var group in open.GroupBy(static l => l.PartnerId))
        {
            var lines = group.Select(static l => new SavePaymentLineRequest(l.OpenItemId, l.AmountTc, l.DiscountTc)).ToList();
            var payment = new Payment { Id = Guid.CreateVersion7(), ProposalId = proposal.Id, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
            var applied = await ApplyAsync(payment, new SavePaymentRequest(proposal.CompanyId, group.Key, request.BankAccountId, "supplier_payment", request.PaymentDate, request.Method, proposal.Number, proposal.Currency, Lines: lines), cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error!.WithWhy(("partnerId", group.Key));
            }

            await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(payment.CompanyId), "PAY", "PAY-{yyyy}-{seq:5}", "yearly", cancellationToken);
            var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(payment.CompanyId), null, payment.PaymentDate, payment.Id), cancellationToken);
            if (number.IsFailure)
            {
                return number.Error!;
            }

            payment.Number = number.Value.Text;
            db.Payments.Add(payment);
            foreach (var line in group)
            {
                byLine[line.Id] = payment.Id;
            }

            await db.SaveChangesAsync(cancellationToken);
            await audit.RecordAsync(new AuditEntry(DocumentType, payment.Id, payment.Number, AuditActions.Created, After: new { proposal = proposal.Number, payment.AmountTc, payment.Currency, lines = payment.Lines.Count }, CompanyId: payment.CompanyId), cancellationToken);
            created.Add(await MapAsync(payment, cancellationToken));
        }

        var marked = await payables.MarkProposalPaidAsync(proposal.Id, byLine, cancellationToken);
        return marked.IsFailure ? marked.Error! : created;
    }

    public async Task<Result<PaymentSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.Include(static p => p.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (payment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (payment.Status != "draft")
        {
            return Error.Conflict("payment.not_draft", "Only a draft payment is posted.").WithWhy(("status", payment.Status));
        }

        var company = await companies.FindAsync(new CompanyId(payment.CompanyId), cancellationToken);
        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == payment.BankAccountId, cancellationToken);
        if (company is null || bank is null)
        {
            return Error.NotFound(company is null ? "company" : "bank_account", company is null ? payment.CompanyId : payment.BankAccountId);
        }

        var supplier = await partners.FindSupplierAsync(payment.CompanyId, payment.PartnerId, cancellationToken);
        var fc = company.FunctionalCurrency;
        var policy = new RoundingPolicy(company.RoundingMode);
        var tcCurrency = await companies.FindCurrencyAsync(payment.Currency, cancellationToken);
        if (tcCurrency is null)
        {
            return Error.Validation("payment.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", payment.Currency));
        }

        var sameCurrency = string.Equals(payment.Currency, fc.Code, StringComparison.Ordinal);
        var rate = payment.ExchangeRate;
        decimal Fc(decimal tc) => sameCurrency ? tc : policy.Round(tc * rate, fc.MinorUnits);

        // The items settled, each at what it still carries in the company's currency.
        var items = new Dictionary<Guid, OpenItemInfo>();
        foreach (var line in payment.Lines)
        {
            var item = await payables.FindAsync(line.OpenItemId, cancellationToken);
            if (item is null || item.PartnerId != payment.PartnerId || item.CompanyId != payment.CompanyId)
            {
                return Error.Validation("payment.item_unknown", "A line names an open item of another supplier or company.").WithWhy(("lineNo", line.LineNo), ("openItemId", line.OpenItemId));
            }

            if (item.Status is not ("open" or "partially_settled") || item.PaymentBlocked || line.AmountTc > item.RemainingTc)
            {
                return Error.Conflict("payment.item_not_payable", "The item is settled, held or has less open than the line pays.").WithWhy(("lineNo", line.LineNo), ("document", item.DocumentNumber), ("status", item.Status), ("held", item.PaymentBlocked), ("remaining", item.RemainingTc), ("amount", line.AmountTc));
            }

            items[line.OpenItemId] = item;
        }

        var transactionId = Guid.CreateVersion7();
        var lines = new List<PostingLine>();
        var settledFcByLine = new Dictionary<Guid, decimal>();
        var keys = new PostingKeys(DocumentType, PartnerPostingGroupId: supplier?.PostingGroupId);
        var fcTotal = 0m;
        foreach (var line in payment.Lines)
        {
            var item = items[line.OpenItemId];
            var settledFc = line.AmountTc == item.RemainingTc ? item.RemainingFc : policy.Round(line.AmountTc * item.BookedRate, fc.MinorUnits);
            settledFcByLine[line.Id] = settledFc;
            lines.Add(new PostingLine(AccountRoles.AP, line.AmountTc, keys, SubledgerType: SubledgerTypes.Payables, SubledgerRef: item.DocumentId, PartnerId: payment.PartnerId, AmountFc: settledFc));
            fcTotal += settledFc;
            if (line.DiscountTc > 0m)
            {
                lines.Add(new PostingLine(AccountRoles.DiscountTaken, -line.DiscountTc, keys, PartnerId: payment.PartnerId, AmountFc: -Fc(line.DiscountTc)));
                fcTotal -= Fc(line.DiscountTc);
            }

            if (line.WhtTc > 0m)
            {
                lines.Add(new PostingLine(AccountRoles.WhtPayable, -line.WhtTc, keys, PartnerId: payment.PartnerId, AmountFc: -Fc(line.WhtTc)));
                fcTotal -= Fc(line.WhtTc);
            }
        }

        // What the supplier receives beyond the lines: on account, or the advance itself.
        var cash = payment.Lines.Sum(static l => l.AmountTc - l.DiscountTc - l.WhtTc) + payment.OnAccountTc;
        var cashFc = payment.Lines.Sum(l => Fc(l.AmountTc - l.DiscountTc - l.WhtTc)) + Fc(payment.OnAccountTc);
        if (payment.OnAccountTc > 0m)
        {
            var role = payment.Kind == "supplier_advance" ? AccountRoles.SupplierAdvances : AccountRoles.AP;
            lines.Add(new PostingLine(role, payment.OnAccountTc, keys, SubledgerType: SubledgerTypes.Payables, SubledgerRef: payment.Id, PartnerId: payment.PartnerId, AmountFc: Fc(payment.OnAccountTc)));
            fcTotal += Fc(payment.OnAccountTc);
        }

        // Charges and the bank movement in the bank's currency: the same as the payment's, or the company's.
        var bankIsTc = string.Equals(bank.Currency, payment.Currency, StringComparison.Ordinal);
        var chargesTc = bankIsTc ? payment.ChargesBank : sameCurrency ? payment.ChargesBank : policy.Round(payment.ChargesBank / rate, tcCurrency.Value.MinorUnits);
        var chargesFc = bankIsTc ? Fc(payment.ChargesBank) : payment.ChargesBank;
        if (payment.ChargesBank > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.BankCharges, chargesTc, keys, PartnerId: payment.PartnerId, AmountFc: chargesFc));
            fcTotal += chargesFc;
        }

        var bankTc = cash + chargesTc;
        var bankFc = bankIsTc ? Fc(bankTc) : payment.BankAmount;
        lines.Add(new PostingLine(BankAccountService.RoleOf(bank.Kind), -bankTc, new PostingKeys(DocumentType, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: -bankFc));
        fcTotal -= bankFc;
        if (fcTotal != 0m)
        {
            // What the payables relieved at booked value do not cover once the bank has moved at today's value: a loss (debit) or a gain (credit).
            lines.Add(new PostingLine(fcTotal < 0m ? AccountRoles.FxLossRealized : AccountRoles.FxGainRealized, 0m, new PostingKeys(DocumentType), PartnerId: payment.PartnerId, AmountFc: -fcTotal, Description: LocalizedText.Bilingual("Realised exchange difference", "فرق صرف محقق")));
        }

        var partner = await partners.FindAsync(payment.PartnerId, cancellationToken);
        var description = payment.Kind == "supplier_advance"
            ? LocalizedText.Bilingual($"Advance to {partner?.Code} {payment.Number}", $"دفعة مقدمة إلى {partner?.Code} {payment.Number}")
            : LocalizedText.Bilingual($"Payment to {partner?.Code} {payment.Number}", $"دفعة إلى {partner?.Code} {payment.Number}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "banking", DocumentType, payment.Id, payment.PaymentDate, payment.Currency, lines, payment.Number, payment.PaymentDate, description,
            null, RateTypes.Spot, sameCurrency ? null : rate, sameCurrency ? null : "Payment rate", $"bank_payment:{payment.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        // The payment's own open item: what the supplier received; the lines use it up, the rest stays on account.
        var kind = payment.Kind == "supplier_advance" ? PayableKinds.Advance : PayableKinds.PaymentOnAccount;
        var own = await payables.OpenAsync(new NewOpenItem(payment.CompanyId, payment.PartnerId, kind, DocumentType, payment.Id, payment.Number, 1, payment.Reference, payment.PaymentDate, payment.PaymentDate, payment.PaymentDate, null, 0m, payment.Currency, -cash, -cashFc, rate, posted.Value.EntryId), cancellationToken);
        foreach (var line in payment.Lines)
        {
            var cashLine = line.AmountTc - line.DiscountTc - line.WhtTc;
            var recorded = await payables.RecordAsync(new RecordSettlementRequest(own.Id, line.OpenItemId, payment.PaymentDate, SettlementKinds.Payment, line.AmountTc, settledFcByLine[line.Id], Fc(cashLine), rate, line.DiscountTc, line.WhtTc, posted.Value.EntryId), cancellationToken);
            if (recorded.IsFailure)
            {
                return recorded.Error!.WithWhy(("lineNo", line.LineNo));
            }

            line.SettlementId = recorded.Value.Id;
        }

        db.Transactions.Add(new BankTransaction
        {
            Id = transactionId,
            BankAccountId = bank.Id,
            CompanyId = payment.CompanyId,
            PostingDate = payment.PaymentDate,
            ValueDate = payment.PaymentDate,
            Kind = "disbursement",
            AmountTc = -(bankIsTc ? bankTc : payment.BankAmount),
            AmountFc = -bankFc,
            Reference = payment.Reference ?? payment.Number,
            SourceDocumentType = DocumentType,
            SourceDocumentId = payment.Id,
            JournalEntryId = posted.Value.EntryId,
            CreatedAt = clock.UtcNow,
        });
        payment.BankAmount = bankIsTc ? bankTc : payment.BankAmount;
        payment.OpenItemId = own.Id;
        payment.JournalEntryId = posted.Value.EntryId;
        payment.BankTransactionId = transactionId;
        payment.Status = "posted";
        payment.PostedAt = clock.UtcNow;
        payment.PostedBy = principal.Principal?.MembershipId.Value;
        payment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, payment.Id, payment.Number, AuditActions.Posted, After: new { status = payment.Status, journal = posted.Value.Number, payment.AmountTc, payment.Currency, payment.BankAmount, payment.BankCurrency, fx = -fcTotal }, CompanyId: payment.CompanyId), cancellationToken);
        return await MapAsync(payment, cancellationToken);
    }

    public async Task<Result<PaymentSummary>> ReverseAsync(Guid id, ReversePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is null)
        {
            return Error.Validation("payment.reason_required", "A reversal names its reason.");
        }

        var payment = await db.Payments.Include(static p => p.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (payment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (payment.Status != "posted")
        {
            return Error.Conflict("payment.not_posted", "Only a posted payment is reversed.").WithWhy(("status", payment.Status));
        }

        var company = await companies.FindAsync(new CompanyId(payment.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < payment.PaymentDate)
        {
            return Error.Validation("payment.reversal_date_invalid", "A payment is reversed on or after its date.").WithWhy(("paymentDate", payment.PaymentDate), ("reversalDate", reversalDate));
        }

        var reversed = await posting.ReverseAsync(payment.JournalEntryId!.Value, reversalDate, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var settlements = await payables.ReverseSettlementsOfAsync(payment.JournalEntryId.Value, reversed.Value.PostingDate, reversed.Value.EntryId, reason, cancellationToken);
        if (settlements.IsFailure)
        {
            return settlements.Error!.Code == "payables.remainder_applied" ? Error.Conflict("payment.applied", "The payment's remainder was applied to invoices; reverse those applications first.").WithWhy(("settlements", settlements.Error!.Why)) : settlements.Error!;
        }

        var own = await payables.ReverseDocumentAsync(DocumentType, payment.Id, reversed.Value.PostingDate, cancellationToken);
        if (own.IsFailure)
        {
            return own.Error!;
        }

        var original = await db.Transactions.SingleAsync(t => t.Id == payment.BankTransactionId, cancellationToken);
        db.Transactions.Add(new BankTransaction
        {
            Id = Guid.CreateVersion7(),
            BankAccountId = original.BankAccountId,
            CompanyId = original.CompanyId,
            PostingDate = reversed.Value.PostingDate,
            ValueDate = reversed.Value.PostingDate,
            Kind = "reversal",
            AmountTc = -original.AmountTc,
            AmountFc = -original.AmountFc,
            Reference = original.Reference,
            SourceDocumentType = DocumentType,
            SourceDocumentId = payment.Id,
            JournalEntryId = reversed.Value.EntryId,
            ReversesTransactionId = original.Id,
            CreatedAt = clock.UtcNow,
        });
        payment.Status = "reversed";
        payment.ReversalEntryId = reversed.Value.EntryId;
        payment.ReversalReason = reason;
        payment.ReversedAt = clock.UtcNow;
        payment.ReversedBy = principal.Principal?.MembershipId.Value;
        payment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, payment.Id, payment.Number, AuditActions.Reversed, After: new { status = payment.Status, reversalDate, journal = reversed.Value.Number }, Reason: reason, CompanyId: payment.CompanyId), cancellationToken);
        return await MapAsync(payment, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result> ApplyAsync(Payment payment, SavePaymentRequest request, CancellationToken cancellationToken)
    {
        if (!Kinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("payment.kind_invalid", "The kind is supplier_payment or supplier_advance.").WithWhy(("kind", request.Kind));
        }

        if (!Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return Error.Validation("payment.method_invalid", "The method is transfer, cash or cheque.").WithWhy(("method", request.Method));
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("payment.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var supplier = await partners.FindSupplierAsync(request.CompanyId, request.PartnerId, cancellationToken);
        if (supplier is null)
        {
            return Error.Validation("supplier.not_registered", "The partner is not registered as a supplier of the company.").WithWhy(("partnerId", request.PartnerId));
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.BankAccountId, cancellationToken);
        if (bank is null || bank.CompanyId != request.CompanyId || !bank.IsActive)
        {
            return Error.Validation("payment.bank_account_invalid", "The bank account is not an active account of the company.").WithWhy(("bankAccountId", request.BankAccountId));
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var paymentDate = request.PaymentDate ?? clock.TodayIn(company.TimeZone);
        var isAdvance = request.Kind == "supplier_advance";
        if (isAdvance && request.Lines is { Count: > 0 })
        {
            return Error.Validation("payment.advance_no_lines", "An advance settles nothing yet; it is applied to invoices later.");
        }

        if (request.OnAccount < 0m || request.Charges < 0m)
        {
            return Error.Validation("payment.amount_invalid", "Amounts are not negative.").WithWhy(("onAccount", request.OnAccount), ("charges", request.Charges));
        }

        // Currency: the settled items' one; an advance or a pure payment on account names its own (the supplier's by default).
        var whtCodeId = request.ApplyWht ? request.WhtCodeId ?? supplier.WhtCodeId : null;
        WhtCodeInfo? wht = null;
        if (whtCodeId is { } w)
        {
            wht = await partners.FindWhtCodeAsync(w, cancellationToken);
            if (wht is null || !wht.IsActive)
            {
                return Error.Validation("payment.wht_code_invalid", "The withholding code does not exist or is inactive.").WithWhy(("whtCodeId", w));
            }
        }

        var withholdAtPayment = wht is not null && wht.WithholdAt == WithholdingPoints.Payment;
        string? currency = null;
        var lines = new List<PaymentLine>();
        var lineNo = 0;
        foreach (var l in request.Lines ?? [])
        {
            lineNo++;
            var item = await payables.FindAsync(l.OpenItemId, cancellationToken);
            if (item is null || item.CompanyId != request.CompanyId || item.PartnerId != request.PartnerId)
            {
                return Error.Validation("payment.item_unknown", "A line names an open item of another supplier or company.").WithWhy(("lineNo", lineNo), ("openItemId", l.OpenItemId));
            }

            if (item.OriginalTc <= 0m || item.Status is not ("open" or "partially_settled"))
            {
                return Error.Conflict("payment.item_not_payable", "Only an open invoice item is paid.").WithWhy(("lineNo", lineNo), ("document", item.DocumentNumber), ("kind", item.Kind), ("status", item.Status));
            }

            if (item.PaymentBlocked)
            {
                return Error.Conflict("payment.item_held", "The item is held.").WithWhy(("lineNo", lineNo), ("document", item.DocumentNumber), ("reason", item.BlockReason));
            }

            if (lines.Any(x => x.OpenItemId == item.Id))
            {
                return Error.Validation("payment.item_duplicate", "An item is paid once on a payment.").WithWhy(("lineNo", lineNo));
            }

            if (currency is not null && !string.Equals(currency, item.Currency, StringComparison.Ordinal))
            {
                return Error.Validation("payment.currency_mixed", "A payment settles items of one currency.").WithWhy(("lineNo", lineNo), ("currency", currency), ("itemCurrency", item.Currency));
            }

            currency = item.Currency;
            if (l.Amount <= 0m || l.Amount > item.RemainingTc)
            {
                return Error.Validation("payment.line_amount_invalid", "A line pays a positive amount up to what is open on the item.").WithWhy(("lineNo", lineNo), ("amount", l.Amount), ("remaining", item.RemainingTc));
            }

            var tc = await companies.FindCurrencyAsync(item.Currency, cancellationToken);
            var minor = tc?.MinorUnits ?? 2;
            var discount = l.DiscountTc ?? (l.Amount == item.RemainingTc && item.DiscountDate is { } d && d >= paymentDate && item.DiscountPct > 0m ? RoundingPolicy.Default.Round(l.Amount * item.DiscountPct / 100m, minor) : 0m);
            if (discount < 0m || discount > l.Amount)
            {
                return Error.Validation("payment.discount_invalid", "A discount is between zero and the amount paid.").WithWhy(("lineNo", lineNo), ("discount", discount));
            }

            var withheld = l.WhtTc ?? (withholdAtPayment ? RoundingPolicy.Default.Round(l.Amount * wht!.RatePct / 100m, minor) : 0m);
            if (withheld < 0m || withheld > l.Amount - discount)
            {
                return Error.Validation("payment.wht_invalid", "Tax withheld is between zero and the amount paid after discount.").WithWhy(("lineNo", lineNo), ("wht", withheld));
            }

            lines.Add(new PaymentLine { Id = Guid.CreateVersion7(), TenantId = payment.TenantId, PaymentId = payment.Id, LineNo = lineNo, OpenItemId = item.Id, AmountTc = l.Amount, DiscountTc = discount, WhtTc = withheld });
        }

        currency ??= (string.IsNullOrWhiteSpace(request.Currency) ? supplier.Currency : request.Currency).Trim().ToUpperInvariant();
        var tcCurrency = await companies.FindCurrencyAsync(currency, cancellationToken);
        if (tcCurrency is null)
        {
            return Error.Validation("payment.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", currency));
        }

        if (lines.Count == 0 && request.OnAccount <= 0m)
        {
            return Error.Validation("payment.nothing_paid", isAdvance ? "An advance names the amount paid." : "A payment settles at least one item or pays something on account.");
        }

        var sameCurrency = string.Equals(currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        decimal rate;
        if (sameCurrency)
        {
            rate = 1m;
        }
        else if (request.ExchangeRate is { } given)
        {
            if (given <= 0m)
            {
                return Error.Validation("payment.rate_invalid", "The exchange rate is positive.").WithWhy(("exchangeRate", given));
            }

            rate = given;
        }
        else
        {
            var resolved = await rates.ResolveAsync(company.Id, currency, company.FunctionalCurrency.Code, paymentDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return Error.Validation("payment.rate_missing", "No spot rate from the payment currency to the company's currency on the payment date; give one.").WithWhy(("from", currency), ("to", company.FunctionalCurrency.Code), ("date", paymentDate));
            }

            rate = resolved.Value.Rate.Rate;
        }

        var bankIsTc = string.Equals(bank.Currency, currency, StringComparison.Ordinal);
        var bankIsFc = string.Equals(bank.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        if (!bankIsTc && !bankIsFc)
        {
            return Error.Validation("payment.bank_currency_unsupported", "A payment goes out of an account in the payment currency or in the company's currency.").WithWhy(("bankCurrency", bank.Currency), ("paymentCurrency", currency), ("functionalCurrency", company.FunctionalCurrency.Code));
        }

        var cash = lines.Sum(static l => l.AmountTc - l.DiscountTc - l.WhtTc) + request.OnAccount;
        decimal bankAmount;
        if (bankIsTc)
        {
            bankAmount = cash + request.Charges;
        }
        else
        {
            if (request.BankAmount is not { } given || given <= 0m)
            {
                return Error.Validation("payment.bank_amount_required", "The account is in the company's currency: name what left it, charges included.").WithWhy(("bankCurrency", bank.Currency));
            }

            if (given <= request.Charges)
            {
                return Error.Validation("payment.bank_amount_invalid", "What left the bank covers more than the charges.").WithWhy(("bankAmount", given), ("charges", request.Charges));
            }

            bankAmount = given;
        }

        payment.CompanyId = request.CompanyId;
        payment.Kind = request.Kind;
        payment.PartnerId = request.PartnerId;
        payment.BankAccountId = bank.Id;
        payment.PaymentDate = paymentDate;
        payment.Method = request.Method;
        payment.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        payment.Currency = currency;
        payment.ExchangeRate = rate;
        payment.AmountTc = cash;
        payment.OnAccountTc = request.OnAccount;
        payment.DiscountTc = lines.Sum(static l => l.DiscountTc);
        payment.WhtTc = lines.Sum(static l => l.WhtTc);
        payment.ChargesBank = request.Charges;
        payment.BankAmount = bankAmount;
        payment.BankCurrency = bank.Currency;
        payment.ApplyWht = request.ApplyWht;
        payment.WhtCodeId = withholdAtPayment ? whtCodeId : null;
        payment.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        payment.CustomFields = request.CustomFields is { ValueKind: JsonValueKind.Object } v ? v.GetRawText() : "{}";
        payment.Lines.AddRange(lines);
        return Result.Success();
    }

    private async Task<PaymentSummary> MapAsync(Payment p, CancellationToken cancellationToken)
    {
        var partner = await partners.FindAsync(p.PartnerId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(p.CompanyId), cancellationToken);
        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == p.BankAccountId, cancellationToken);
        var wht = p.WhtCodeId is { } w ? await partners.FindWhtCodeAsync(w, cancellationToken) : null;
        var lines = new List<PaymentLineSummary>(p.Lines.Count);
        foreach (var l in p.Lines.OrderBy(static l => l.LineNo))
        {
            var item = await payables.FindAsync(l.OpenItemId, cancellationToken);
            lines.Add(new PaymentLineSummary(l.Id, l.LineNo, l.OpenItemId, item?.DocumentNumber ?? string.Empty, item?.Instalment ?? 1, item?.DueDate ?? p.PaymentDate, item?.RemainingTc ?? 0m, l.AmountTc, l.DiscountTc, l.WhtTc, l.AmountTc - l.DiscountTc - l.WhtTc, l.SettlementId));
        }

        return new PaymentSummary(p.Id, p.CompanyId, p.Number, p.Kind, p.Status, p.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), p.BankAccountId, bank?.Code ?? string.Empty, p.PaymentDate, p.Method, p.Reference, p.Currency, p.ExchangeRate, company?.FunctionalCurrency.Code ?? p.Currency,
            p.AmountTc, p.OnAccountTc, p.DiscountTc, p.WhtTc, p.ChargesBank, p.BankAmount, p.BankCurrency, p.ApplyWht, p.WhtCodeId, wht?.Code, p.OpenItemId, p.JournalEntryId, p.BankTransactionId, p.ReversalEntryId, p.ReversalReason, p.ProposalId, p.Notes, JsonDocument.Parse(p.CustomFields).RootElement.Clone(), lines, p.PostedAt, p.UpdatedAt);
    }
}
