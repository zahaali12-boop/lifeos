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
using Quicker.Receivables.Contracts;

namespace Quicker.Banking.Application;

/// <summary>
/// Customer receipts and deposits (roadmap 5.7, POSTING_RULES §2 "Customer receipt", hard scenario 3 on the customer
/// side). A receipt settles invoice open items in their currency: each settled item leaves the books at its booked
/// functional value, the early-payment discount given and the tax the customer withheld reduce the cash, the bank
/// charges are what the bank kept, the bank moves by what it credited, and the functional difference all of that
/// leaves is realised FX (ADR-0031). Whatever is received beyond the lines stays on account as the receipt's own open
/// item; a deposit is such an item on the customer-deposits control, applied to an invoice later. Reversal mirrors the
/// journal and reopens the items at their booked values. Receipts share the payments' table and shape, told apart by
/// kind and numbered in their own series.
/// </summary>
public sealed class CustomerReceiptService(
    BankingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IPartnerDirectory partners,
    ICustomerDirectory customers,
    IReceivables receivables,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IPostingService posting,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = BankDocumentTypes.Receipt;

    public static readonly IReadOnlyList<string> Kinds = ["customer_receipt", "customer_deposit"];

    public async Task<IReadOnlyList<PaymentSummary>> ListAsync(Guid? companyId, string? status, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Payments.AsNoTracking().Include(static p => p.Lines).Where(static p => Kinds.Contains(p.Kind));
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

        var receipts = await query.OrderByDescending(static p => p.PaymentDate).ThenByDescending(static p => p.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<PaymentSummary>(receipts.Count);
        foreach (var receipt in receipts)
        {
            result.Add(await MapAsync(receipt, cancellationToken));
        }

        return result;
    }

    public async Task<Result<PaymentSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await db.Payments.AsNoTracking().Include(static p => p.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(p => p.Id == id && Kinds.Contains(p.Kind), cancellationToken);
        return receipt is null ? Error.NotFound(DocumentType, id) : await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result<PaymentSummary>> CreateAsync(SaveCustomerReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = new Payment { Id = Guid.CreateVersion7(), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(receipt, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await numbering.EnsureDefaultSeriesAsync(DocumentType, new CompanyId(receipt.CompanyId), "RCT", "RCT-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, new CompanyId(receipt.CompanyId), null, receipt.PaymentDate, receipt.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        receipt.Number = number.Value.Text;
        db.Payments.Add(receipt);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Created, After: new { receipt.Kind, receipt.AmountTc, receipt.Currency, lines = receipt.Lines.Count }, CompanyId: receipt.CompanyId), cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result<PaymentSummary>> UpdateAsync(Guid id, SaveCustomerReceiptRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var receipt = await db.Payments.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id && Kinds.Contains(p.Kind), cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "draft")
        {
            return Error.Conflict("receipt.not_draft", "Only a draft receipt is edited.").WithWhy(("status", receipt.Status));
        }

        if (receipt.CompanyId != request.CompanyId)
        {
            return Error.Validation("receipt.company_locked", "A receipt stays with its company.");
        }

        db.PaymentLines.RemoveRange(receipt.Lines);
        receipt.Lines.Clear();
        var applied = await ApplyAsync(receipt, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        receipt.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await db.Payments.Include(static p => p.Lines).SingleOrDefaultAsync(p => p.Id == id && Kinds.Contains(p.Kind), cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "draft")
        {
            return Error.Conflict("receipt.not_draft", "Only a draft receipt is deleted.").WithWhy(("status", receipt.Status));
        }

        db.Payments.Remove(receipt);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Deleted, CompanyId: receipt.CompanyId), cancellationToken);
        return Result.Success();
    }

    public async Task<Result<PaymentSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var receipt = await db.Payments.Include(static p => p.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(p => p.Id == id && Kinds.Contains(p.Kind), cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "draft")
        {
            return Error.Conflict("receipt.not_draft", "Only a draft receipt is posted.").WithWhy(("status", receipt.Status));
        }

        var company = await companies.FindAsync(new CompanyId(receipt.CompanyId), cancellationToken);
        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == receipt.BankAccountId, cancellationToken);
        if (company is null || bank is null)
        {
            return Error.NotFound(company is null ? "company" : "bank_account", company is null ? receipt.CompanyId : receipt.BankAccountId);
        }

        var customer = await customers.FindCustomerAsync(receipt.CompanyId, receipt.PartnerId, cancellationToken);
        var fc = company.FunctionalCurrency;
        var policy = new RoundingPolicy(company.RoundingMode);
        var tcCurrency = await companies.FindCurrencyAsync(receipt.Currency, cancellationToken);
        if (tcCurrency is null)
        {
            return Error.Validation("receipt.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", receipt.Currency));
        }

        var sameCurrency = string.Equals(receipt.Currency, fc.Code, StringComparison.Ordinal);
        var rate = receipt.ExchangeRate;
        decimal Fc(decimal tc) => sameCurrency ? tc : policy.Round(tc * rate, fc.MinorUnits);

        // The items settled, each at what it still carries in the company's currency.
        var items = new Dictionary<Guid, ReceivableInfo>();
        foreach (var line in receipt.Lines)
        {
            var item = await receivables.FindAsync(line.OpenItemId, cancellationToken);
            if (item is null || item.PartnerId != receipt.PartnerId || item.CompanyId != receipt.CompanyId)
            {
                return Error.Validation("receipt.item_unknown", "A line names an open item of another customer or company.").WithWhy(("lineNo", line.LineNo), ("openItemId", line.OpenItemId));
            }

            if (item.Status is not ("open" or "partially_settled") || item.OnHold || line.AmountTc > item.RemainingTc)
            {
                return Error.Conflict("receipt.item_not_receivable", "The item is settled, held or has less open than the line collects.").WithWhy(("lineNo", line.LineNo), ("document", item.DocumentNumber), ("status", item.Status), ("held", item.OnHold), ("remaining", item.RemainingTc), ("amount", line.AmountTc));
            }

            items[line.OpenItemId] = item;
        }

        var transactionId = Guid.CreateVersion7();
        var lines = new List<PostingLine>();
        var settledFcByLine = new Dictionary<Guid, decimal>();
        var keys = new PostingKeys(DocumentType, PartnerPostingGroupId: customer?.PostingGroupId);
        var fcTotal = 0m;
        foreach (var line in receipt.Lines)
        {
            var item = items[line.OpenItemId];
            var settledFc = line.AmountTc == item.RemainingTc ? item.RemainingFc : policy.Round(line.AmountTc * item.BookedRate, fc.MinorUnits);
            settledFcByLine[line.Id] = settledFc;
            lines.Add(new PostingLine(AccountRoles.AR, -line.AmountTc, keys, SubledgerType: SubledgerTypes.Receivables, SubledgerRef: item.DocumentId, PartnerId: receipt.PartnerId, AmountFc: -settledFc));
            fcTotal -= settledFc;
            if (line.DiscountTc > 0m)
            {
                lines.Add(new PostingLine(AccountRoles.DiscountGiven, line.DiscountTc, keys, PartnerId: receipt.PartnerId, AmountFc: Fc(line.DiscountTc)));
                fcTotal += Fc(line.DiscountTc);
            }

            if (line.WhtTc > 0m)
            {
                lines.Add(new PostingLine(AccountRoles.WhtReceivable, line.WhtTc, keys, PartnerId: receipt.PartnerId, AmountFc: Fc(line.WhtTc)));
                fcTotal += Fc(line.WhtTc);
            }
        }

        // What the customer paid beyond the lines: on account, or the deposit itself.
        var cash = receipt.Lines.Sum(static l => l.AmountTc - l.DiscountTc - l.WhtTc) + receipt.OnAccountTc;
        var cashFc = receipt.Lines.Sum(l => Fc(l.AmountTc - l.DiscountTc - l.WhtTc)) + Fc(receipt.OnAccountTc);
        if (receipt.OnAccountTc > 0m)
        {
            var role = receipt.Kind == "customer_deposit" ? AccountRoles.CustomerDeposits : AccountRoles.AR;
            lines.Add(new PostingLine(role, -receipt.OnAccountTc, keys, SubledgerType: SubledgerTypes.Receivables, SubledgerRef: receipt.Id, PartnerId: receipt.PartnerId, AmountFc: -Fc(receipt.OnAccountTc)));
            fcTotal -= Fc(receipt.OnAccountTc);
        }

        // Charges the bank kept and the bank movement in the bank's currency: the same as the receipt's, or the company's.
        var bankIsTc = string.Equals(bank.Currency, receipt.Currency, StringComparison.Ordinal);
        var chargesTc = bankIsTc ? receipt.ChargesBank : sameCurrency ? receipt.ChargesBank : policy.Round(receipt.ChargesBank / rate, tcCurrency.Value.MinorUnits);
        var chargesFc = bankIsTc ? Fc(receipt.ChargesBank) : receipt.ChargesBank;
        if (receipt.ChargesBank > 0m)
        {
            lines.Add(new PostingLine(AccountRoles.BankCharges, chargesTc, keys, PartnerId: receipt.PartnerId, AmountFc: chargesFc));
            fcTotal += chargesFc;
        }

        var bankTc = cash - chargesTc;
        var bankFc = bankIsTc ? Fc(bankTc) : receipt.BankAmount;
        lines.Add(new PostingLine(BankAccountService.RoleOf(bank.Kind), bankTc, new PostingKeys(DocumentType, BankAccountId: bank.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: transactionId, AmountFc: bankFc));
        fcTotal += bankFc;
        if (fcTotal != 0m)
        {
            // What the bank credited at today's value against the receivables relieved at booked value: more is a gain (credit), less a loss (debit).
            lines.Add(new PostingLine(fcTotal > 0m ? AccountRoles.FxGainRealized : AccountRoles.FxLossRealized, 0m, new PostingKeys(DocumentType), PartnerId: receipt.PartnerId, AmountFc: -fcTotal, Description: LocalizedText.Bilingual("Realised exchange difference", "فرق صرف محقق")));
        }

        var partner = await partners.FindAsync(receipt.PartnerId, cancellationToken);
        var description = receipt.Kind == "customer_deposit"
            ? LocalizedText.Bilingual($"Deposit from {partner?.Code} {receipt.Number}", $"عربون من {partner?.Code} {receipt.Number}")
            : LocalizedText.Bilingual($"Receipt from {partner?.Code} {receipt.Number}", $"قبض من {partner?.Code} {receipt.Number}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "banking", DocumentType, receipt.Id, receipt.PaymentDate, receipt.Currency, lines, receipt.Number, receipt.PaymentDate, description,
            null, RateTypes.Spot, sameCurrency ? null : rate, sameCurrency ? null : "Receipt rate", $"bank_receipt:{receipt.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        // The receipt's own open item: what the customer paid; the lines use it up, the rest stays on account.
        var kind = receipt.Kind == "customer_deposit" ? ReceivableKinds.Deposit : ReceivableKinds.ReceiptOnAccount;
        var own = await receivables.OpenAsync(new NewReceivable(receipt.CompanyId, receipt.PartnerId, kind, DocumentType, receipt.Id, receipt.Number, 1, receipt.Reference, receipt.PaymentDate, receipt.PaymentDate, receipt.PaymentDate, null, 0m, receipt.Currency, -cash, -cashFc, rate, posted.Value.EntryId, SalesRepId: customer?.SalesRepId), cancellationToken);
        foreach (var line in receipt.Lines)
        {
            var cashLine = line.AmountTc - line.DiscountTc - line.WhtTc;
            var recorded = await receivables.RecordAsync(new RecordReceivableSettlementRequest(own.Id, line.OpenItemId, receipt.PaymentDate, ReceivableSettlementKinds.Receipt, line.AmountTc, settledFcByLine[line.Id], Fc(cashLine), rate, line.DiscountTc, line.WhtTc, posted.Value.EntryId), cancellationToken);
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
            CompanyId = receipt.CompanyId,
            PostingDate = receipt.PaymentDate,
            ValueDate = receipt.PaymentDate,
            Kind = "receipt",
            AmountTc = bankIsTc ? bankTc : receipt.BankAmount,
            AmountFc = bankFc,
            Reference = receipt.Reference ?? receipt.Number,
            SourceDocumentType = DocumentType,
            SourceDocumentId = receipt.Id,
            JournalEntryId = posted.Value.EntryId,
            CreatedAt = clock.UtcNow,
        });
        receipt.BankAmount = bankIsTc ? bankTc : receipt.BankAmount;
        receipt.OpenItemId = own.Id;
        receipt.JournalEntryId = posted.Value.EntryId;
        receipt.BankTransactionId = transactionId;
        receipt.Status = "posted";
        receipt.PostedAt = clock.UtcNow;
        receipt.PostedBy = principal.Principal?.MembershipId.Value;
        receipt.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Posted, After: new { status = receipt.Status, journal = posted.Value.Number, receipt.AmountTc, receipt.Currency, receipt.BankAmount, receipt.BankCurrency, fx = fcTotal }, CompanyId: receipt.CompanyId), cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    public async Task<Result<PaymentSummary>> ReverseAsync(Guid id, ReversePaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (reason is null)
        {
            return Error.Validation("receipt.reason_required", "A reversal names its reason.");
        }

        var receipt = await db.Payments.Include(static p => p.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(p => p.Id == id && Kinds.Contains(p.Kind), cancellationToken);
        if (receipt is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (receipt.Status != "posted")
        {
            return Error.Conflict("receipt.not_posted", "Only a posted receipt is reversed.").WithWhy(("status", receipt.Status));
        }

        var company = await companies.FindAsync(new CompanyId(receipt.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        if (reversalDate < receipt.PaymentDate)
        {
            return Error.Validation("receipt.reversal_date_invalid", "A receipt is reversed on or after its date.").WithWhy(("receiptDate", receipt.PaymentDate), ("reversalDate", reversalDate));
        }

        var reversed = await posting.ReverseAsync(receipt.JournalEntryId!.Value, reversalDate, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var settlements = await receivables.ReverseSettlementsOfAsync(receipt.JournalEntryId.Value, reversed.Value.PostingDate, reversed.Value.EntryId, reason, cancellationToken);
        if (settlements.IsFailure)
        {
            return settlements.Error!.Code == "receivables.remainder_applied" ? Error.Conflict("receipt.applied", "The receipt's remainder was applied to invoices; reverse those applications first.").WithWhy(("settlements", settlements.Error!.Why)) : settlements.Error!;
        }

        var own = await receivables.ReverseDocumentAsync(DocumentType, receipt.Id, reversed.Value.PostingDate, cancellationToken);
        if (own.IsFailure)
        {
            return own.Error!;
        }

        var original = await db.Transactions.SingleAsync(t => t.Id == receipt.BankTransactionId, cancellationToken);
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
            SourceDocumentId = receipt.Id,
            JournalEntryId = reversed.Value.EntryId,
            ReversesTransactionId = original.Id,
            CreatedAt = clock.UtcNow,
        });
        receipt.Status = "reversed";
        receipt.ReversalEntryId = reversed.Value.EntryId;
        receipt.ReversalReason = reason;
        receipt.ReversedAt = clock.UtcNow;
        receipt.ReversedBy = principal.Principal?.MembershipId.Value;
        receipt.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, receipt.Id, receipt.Number, AuditActions.Reversed, After: new { status = receipt.Status, reversalDate, journal = reversed.Value.Number }, Reason: reason, CompanyId: receipt.CompanyId), cancellationToken);
        return await MapAsync(receipt, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private async Task<Result> ApplyAsync(Payment receipt, SaveCustomerReceiptRequest request, CancellationToken cancellationToken)
    {
        if (!Kinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("receipt.kind_invalid", "The kind is customer_receipt or customer_deposit.").WithWhy(("kind", request.Kind));
        }

        if (!PaymentService.Methods.Contains(request.Method, StringComparer.Ordinal))
        {
            return Error.Validation("receipt.method_invalid", "The method is transfer, cash or cheque.").WithWhy(("method", request.Method));
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("receipt.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        // A receipt is always taken, from a held or blocked customer too: money in is never refused.
        var customer = await customers.FindCustomerAsync(request.CompanyId, request.PartnerId, cancellationToken);
        if (customer is null)
        {
            return Error.Validation("customer.not_registered", "The partner is not a customer of this company.").WithWhy(("partnerId", request.PartnerId), ("companyId", request.CompanyId));
        }

        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == request.BankAccountId, cancellationToken);
        if (bank is null || bank.CompanyId != request.CompanyId || !bank.IsActive)
        {
            return Error.Validation("receipt.bank_account_invalid", "The bank account is not an active account of the company.").WithWhy(("bankAccountId", request.BankAccountId));
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var receiptDate = request.ReceiptDate ?? clock.TodayIn(company.TimeZone);
        var isDeposit = request.Kind == "customer_deposit";
        if (isDeposit && request.Lines is { Count: > 0 })
        {
            return Error.Validation("receipt.deposit_no_lines", "A deposit settles nothing yet; it is applied to invoices later.");
        }

        if (request.OnAccount < 0m || request.Charges < 0m)
        {
            return Error.Validation("receipt.amount_invalid", "Amounts are not negative.").WithWhy(("onAccount", request.OnAccount), ("charges", request.Charges));
        }

        // Tax the customer withheld from what they paid: by the code named on the receipt, at the rate the code says.
        WhtCodeInfo? wht = null;
        if (request.WhtCodeId is { } w)
        {
            wht = await partners.FindWhtCodeAsync(w, cancellationToken);
            if (wht is null || !wht.IsActive)
            {
                return Error.Validation("receipt.wht_code_invalid", "The withholding code does not exist or is inactive.").WithWhy(("whtCodeId", w));
            }
        }

        string? currency = null;
        var lines = new List<PaymentLine>();
        var lineNo = 0;
        foreach (var l in request.Lines ?? [])
        {
            lineNo++;
            var item = await receivables.FindAsync(l.OpenItemId, cancellationToken);
            if (item is null || item.CompanyId != request.CompanyId || item.PartnerId != request.PartnerId)
            {
                return Error.Validation("receipt.item_unknown", "A line names an open item of another customer or company.").WithWhy(("lineNo", lineNo), ("openItemId", l.OpenItemId));
            }

            if (item.OriginalTc <= 0m || item.Status is not ("open" or "partially_settled"))
            {
                return Error.Conflict("receipt.item_not_receivable", "Only an open invoice item is collected.").WithWhy(("lineNo", lineNo), ("document", item.DocumentNumber), ("kind", item.Kind), ("status", item.Status));
            }

            if (item.OnHold)
            {
                return Error.Conflict("receipt.item_held", "The item is held.").WithWhy(("lineNo", lineNo), ("document", item.DocumentNumber), ("reason", item.HoldReason));
            }

            if (lines.Any(x => x.OpenItemId == item.Id))
            {
                return Error.Validation("receipt.item_duplicate", "An item is collected once on a receipt.").WithWhy(("lineNo", lineNo));
            }

            if (currency is not null && !string.Equals(currency, item.Currency, StringComparison.Ordinal))
            {
                return Error.Validation("receipt.currency_mixed", "A receipt settles items of one currency.").WithWhy(("lineNo", lineNo), ("currency", currency), ("itemCurrency", item.Currency));
            }

            currency = item.Currency;
            if (l.Amount <= 0m || l.Amount > item.RemainingTc)
            {
                return Error.Validation("receipt.line_amount_invalid", "A line collects a positive amount up to what is open on the item.").WithWhy(("lineNo", lineNo), ("amount", l.Amount), ("remaining", item.RemainingTc));
            }

            var tc = await companies.FindCurrencyAsync(item.Currency, cancellationToken);
            var minor = tc?.MinorUnits ?? 2;
            var discount = l.DiscountTc ?? (l.Amount == item.RemainingTc && item.DiscountDate is { } d && d >= receiptDate && item.DiscountPct > 0m ? RoundingPolicy.Default.Round(l.Amount * item.DiscountPct / 100m, minor) : 0m);
            if (discount < 0m || discount > l.Amount)
            {
                return Error.Validation("receipt.discount_invalid", "A discount is between zero and the amount collected.").WithWhy(("lineNo", lineNo), ("discount", discount));
            }

            var withheld = l.WhtTc ?? (wht is not null ? RoundingPolicy.Default.Round(l.Amount * wht.RatePct / 100m, minor) : 0m);
            if (withheld < 0m || withheld > l.Amount - discount)
            {
                return Error.Validation("receipt.wht_invalid", "Tax withheld is between zero and the amount collected after discount.").WithWhy(("lineNo", lineNo), ("wht", withheld));
            }

            lines.Add(new PaymentLine { Id = Guid.CreateVersion7(), TenantId = receipt.TenantId, PaymentId = receipt.Id, LineNo = lineNo, OpenItemId = item.Id, AmountTc = l.Amount, DiscountTc = discount, WhtTc = withheld });
        }

        currency ??= (string.IsNullOrWhiteSpace(request.Currency) ? customer.Currency : request.Currency).Trim().ToUpperInvariant();
        var tcCurrency = await companies.FindCurrencyAsync(currency, cancellationToken);
        if (tcCurrency is null)
        {
            return Error.Validation("receipt.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", currency));
        }

        if (lines.Count == 0 && request.OnAccount <= 0m)
        {
            return Error.Validation("receipt.nothing_received", isDeposit ? "A deposit names the amount received." : "A receipt settles at least one item or takes something on account.");
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
                return Error.Validation("receipt.rate_invalid", "The exchange rate is positive.").WithWhy(("exchangeRate", given));
            }

            rate = given;
        }
        else
        {
            var resolved = await rates.ResolveAsync(company.Id, currency, company.FunctionalCurrency.Code, receiptDate, RateTypes.Spot, cancellationToken);
            if (resolved.IsFailure)
            {
                return Error.Validation("receipt.rate_missing", "No spot rate from the receipt currency to the company's currency on the receipt date; give one.").WithWhy(("from", currency), ("to", company.FunctionalCurrency.Code), ("date", receiptDate));
            }

            rate = resolved.Value.Rate.Rate;
        }

        var bankIsTc = string.Equals(bank.Currency, currency, StringComparison.Ordinal);
        var bankIsFc = string.Equals(bank.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);
        if (!bankIsTc && !bankIsFc)
        {
            return Error.Validation("receipt.bank_currency_unsupported", "A receipt goes into an account in the receipt currency or in the company's currency.").WithWhy(("bankCurrency", bank.Currency), ("receiptCurrency", currency), ("functionalCurrency", company.FunctionalCurrency.Code));
        }

        var cash = lines.Sum(static l => l.AmountTc - l.DiscountTc - l.WhtTc) + request.OnAccount;
        decimal bankAmount;
        if (bankIsTc)
        {
            if (request.Charges >= cash)
            {
                return Error.Validation("receipt.charges_invalid", "The bank's charges are less than what the customer paid.").WithWhy(("charges", request.Charges), ("cash", cash));
            }

            bankAmount = cash - request.Charges;
        }
        else
        {
            if (request.BankAmount is not { } given || given <= 0m)
            {
                return Error.Validation("receipt.bank_amount_required", "The account is in the company's currency: name what the bank credited, after its charges.").WithWhy(("bankCurrency", bank.Currency));
            }

            bankAmount = given;
        }

        receipt.CompanyId = request.CompanyId;
        receipt.Kind = request.Kind;
        receipt.PartnerId = request.PartnerId;
        receipt.BankAccountId = bank.Id;
        receipt.PaymentDate = receiptDate;
        receipt.Method = request.Method;
        receipt.Reference = string.IsNullOrWhiteSpace(request.Reference) ? null : request.Reference.Trim();
        receipt.Currency = currency;
        receipt.ExchangeRate = rate;
        receipt.AmountTc = cash;
        receipt.OnAccountTc = request.OnAccount;
        receipt.DiscountTc = lines.Sum(static l => l.DiscountTc);
        receipt.WhtTc = lines.Sum(static l => l.WhtTc);
        receipt.ChargesBank = request.Charges;
        receipt.BankAmount = bankAmount;
        receipt.BankCurrency = bank.Currency;
        receipt.ApplyWht = wht is not null;
        receipt.WhtCodeId = wht?.Id;
        receipt.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        receipt.CustomFields = custom.Value;
        receipt.Lines.AddRange(lines);
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
            var item = await receivables.FindAsync(l.OpenItemId, cancellationToken);
            lines.Add(new PaymentLineSummary(l.Id, l.LineNo, l.OpenItemId, item?.DocumentNumber ?? string.Empty, item?.Instalment ?? 1, item?.DueDate ?? p.PaymentDate, item?.RemainingTc ?? 0m, l.AmountTc, l.DiscountTc, l.WhtTc, l.AmountTc - l.DiscountTc - l.WhtTc, l.SettlementId));
        }

        return new PaymentSummary(p.Id, p.CompanyId, p.Number, p.Kind, p.Status, p.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), p.BankAccountId, bank?.Code ?? string.Empty, p.PaymentDate, p.Method, p.Reference, p.Currency, p.ExchangeRate, company?.FunctionalCurrency.Code ?? p.Currency,
            p.AmountTc, p.OnAccountTc, p.DiscountTc, p.WhtTc, p.ChargesBank, p.BankAmount, p.BankCurrency, p.ApplyWht, p.WhtCodeId, wht?.Code, p.OpenItemId, p.JournalEntryId, p.BankTransactionId, p.ReversalEntryId, p.ReversalReason, p.ProposalId, p.Notes, JsonDocument.Parse(p.CustomFields).RootElement.Clone(), lines, p.PostedAt, p.UpdatedAt);
    }
}
