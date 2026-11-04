using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Messaging.Jobs;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Persistence;
using Quicker.Pos.Contracts;
using Quicker.Pos.Domain;
using Quicker.Pos.Persistence;
using Quicker.Pricing.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Tax.Contracts;

namespace Quicker.Pos.Application;

/// <summary>
/// Orders synced from a till (A-180, POSTING_RULES §2 "POS order"). The till priced the order from the catalogue it
/// cached; here the lines are resolved again, the tax is calculated by the tax engine with the codes the catalogue
/// gave, the totals are compared with what the receipt showed, and the order is posted in one unit of work: the
/// journal (revenue, output tax, rounding, cash, card, receivable), the stock movement (the costing engine adds the
/// cost of sales), the cash box and card account movements, the receivable of a credit sale, the tax ledger, and
/// the e-invoice submission the scheme requires. An order is immutable once posted; a refund is a new order of the
/// opposite sign that names the lines it gives back.
/// </summary>
public sealed class OrderService(
    PosDbContext db,
    IUnitOfWorkAccessor unitOfWork,
    RegisterService registers,
    ICompanyDirectory companies,
    IBankAccountDirectory bankAccounts,
    IBankTransactions bankTransactions,
    IPartnerDirectory partners,
    ICustomerDirectory customers,
    IItemDirectory items,
    IPricing pricing,
    ITaxDetermination tax,
    ITaxLedger taxLedger,
    ITaxClearance clearance,
    IPostingService posting,
    IInventoryPosting inventory,
    IReceivables receivables,
    INumberAllocator numbering,
    IJobQueue jobs,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string AuditEntity = PosDocumentTypes.Order;
    public const string DocumentType = PosDocumentTypes.Order;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<PosOrderSummary>> ListAsync(Guid? companyId, Guid? registerId, Guid? sessionId, Guid? partnerId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var query = db.Orders.AsNoTracking().Include(static o => o.Lines.OrderBy(static l => l.LineNo)).Include(static o => o.Payments.OrderBy(static p => p.LineNo)).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(o => o.CompanyId == c);
        }

        if (registerId is { } r)
        {
            query = query.Where(o => o.RegisterId == r);
        }

        if (sessionId is { } s)
        {
            query = query.Where(o => o.SessionId == s);
        }

        if (partnerId is { } p)
        {
            query = query.Where(o => o.PartnerId == p);
        }

        if (from is { } f)
        {
            query = query.Where(o => o.OrderDate >= f);
        }

        if (to is { } t)
        {
            query = query.Where(o => o.OrderDate <= t);
        }

        var orders = await query.OrderByDescending(static o => o.OrderedAt).Take(500).ToListAsync(cancellationToken);
        var result = new List<PosOrderSummary>(orders.Count);
        foreach (var order in orders)
        {
            result.Add(await MapAsync(order, false, cancellationToken));
        }

        return result;
    }

    public async Task<Result<PosOrderSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await LoadAsync(id, cancellationToken);
        return order is null ? Error.NotFound(AuditEntity, id) : await MapAsync(order, false, cancellationToken);
    }

    /// <summary>Posts an order the till synced, or answers with the order already posted under the same device id.</summary>
    public async Task<Result<PosOrderSummary>> SubmitAsync(SubmitOrderRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var deviceOrderId = Validation.Trim(request.DeviceOrderId);
        if (deviceOrderId is null || deviceOrderId.Length > 64)
        {
            return Error.Validation("pos.device_order_id_required", "The till names each order with its own id (up to 64 characters).");
        }

        var existing = await db.Orders.AsNoTracking().Include(static o => o.Lines.OrderBy(static l => l.LineNo)).Include(static o => o.Payments.OrderBy(static p => p.LineNo)).SingleOrDefaultAsync(o => o.DeviceOrderId == deviceOrderId, cancellationToken);
        if (existing is not null)
        {
            return await MapAsync(existing, true, cancellationToken);
        }

        var kind = Validation.OneOf(request.Kind, "pos.kind", PosOrderKinds.All);
        if (kind.IsFailure)
        {
            return kind.Error!;
        }

        var register = await db.Registers.AsNoTracking().SingleOrDefaultAsync(r => r.Id == request.RegisterId, cancellationToken);
        if (register is null)
        {
            return Error.NotFound(RegisterService.AuditEntity, request.RegisterId);
        }

        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == request.SessionId, cancellationToken);
        if (session is null || session.RegisterId != register.Id)
        {
            return Error.NotFound(SessionService.AuditEntity, request.SessionId);
        }

        if (session.Status != SessionStatuses.Open)
        {
            return Error.Conflict("pos.session_closed", "The session is closed; open a new one on the register and sell again.").WithWhy(("session", session.Number));
        }

        var company = (await companies.FindAsync(new CompanyId(register.CompanyId), cancellationToken))!;
        var currency = company.FunctionalCurrency;
        var now = clock.UtcNow;
        if (request.OrderedAt > now.AddDays(1) || request.OrderedAt < session.OpenedAt.AddDays(-1))
        {
            return Error.Validation("pos.ordered_at_invalid", "The order's time is outside the session: check the till's clock.").WithWhy(("orderedAt", request.OrderedAt), ("sessionOpenedAt", session.OpenedAt));
        }

        var orderDate = DateOnly.FromDateTime(ToCompanyTime(request.OrderedAt, company.TimeZone).DateTime);
        if (request.Lines is not { Count: > 0 })
        {
            return Error.Validation("pos.lines_required", "An order has at least one line.");
        }

        if (request.Payments is not { Count: > 0 })
        {
            return Error.Validation("pos.payments_required", "An order names how it was paid.");
        }

        var sign = kind.Value == PosOrderKinds.Refund ? -1m : 1m;
        PosOrder? refunded = null;
        if (kind.Value == PosOrderKinds.Refund)
        {
            var original = Validation.Trim(request.RefundsDeviceOrderId);
            if (original is null)
            {
                return Error.Validation("pos.refund_original_required", "A refund names the order it gives back.");
            }

            refunded = await db.Orders.AsNoTracking().Include(static o => o.Lines).SingleOrDefaultAsync(o => o.DeviceOrderId == original, cancellationToken);
            if (refunded is null)
            {
                return Error.Conflict("pos.refund_original_unknown", "The order being refunded has not reached the server yet; sync it first.").WithWhy(("deviceOrderId", original));
            }

            if (refunded.Kind != PosOrderKinds.Sale || refunded.CompanyId != register.CompanyId)
            {
                return Error.Validation("pos.refund_original_invalid", "Only a sale of this company is refunded.").WithWhy(("order", refunded.Number));
            }
        }

        // The customer: named for a credit sale or a customer who wants their purchase on their account; the register's walk-in customer otherwise.
        var partnerId = request.PartnerId ?? register.DefaultPartnerId;
        CustomerTermsInfo? customer = null;
        if (partnerId is { } pid)
        {
            var wantsCredit = request.Payments.Any(static p => p.Method == PosPaymentMethods.Credit);
            var ensured = await customers.EnsureCustomerAsync(register.CompanyId, pid, wantsCredit ? CustomerPurposes.Invoice : CustomerPurposes.Receipt, cancellationToken);
            if (ensured.IsFailure)
            {
                return ensured.Error!;
            }

            customer = ensured.Value;
        }

        // 1. The lines: items resolved again, quantities in their units, the engine's list price beside the price charged.
        var order = new PosOrder
        {
            Id = Guid.CreateVersion7(),
            CompanyId = register.CompanyId,
            RegisterId = register.Id,
            SessionId = session.Id,
            DeviceOrderId = deviceOrderId,
            Kind = kind.Value,
            RefundsOrderId = refunded?.Id,
            PartnerId = partnerId,
            CashierUserId = principal.Required.UserId.Value,
            OrderDate = orderDate,
            OrderedAt = request.OrderedAt.ToUniversalTime(),
            PostedAt = now,
            Currency = currency.Code,
            Notes = Validation.Trim(request.Notes),
            CreatedAt = now,
        };
        var resolved = new List<(PosOrderLine Line, ItemInfo Item, SubmitOrderLineRequest Source)>();
        var refundedByLine = refunded is null ? null : await RefundedSoFarAsync(refunded.Id, cancellationToken);
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            if (l.Quantity <= 0m)
            {
                return Error.Validation("pos.quantity_invalid", "Quantities are positive.").WithWhy(("lineNo", lineNo));
            }

            if (l.UnitPrice < 0m || l.DiscountPct is < 0m or > 100m)
            {
                return Error.Validation("pos.price_invalid", "A unit price is zero or above and a discount between 0 and 100 percent.").WithWhy(("lineNo", lineNo));
            }

            var item = await items.FindAsync(l.ItemId, cancellationToken);
            if (item is null || !item.IsActive)
            {
                return Error.Validation("pos.item_unknown", "The item does not exist or is inactive.").WithWhy(("lineNo", lineNo), ("itemId", l.ItemId));
            }

            if (item.Tracking != "none")
            {
                return Error.Validation("pos.tracked_item", "Lot- and serial-tracked items are not sold at the till yet; sell them from a sales order.").WithWhy(("lineNo", lineNo), ("item", item.Code));
            }

            ItemVariantInfo? variant = null;
            if (l.VariantId is { } variantId)
            {
                variant = await items.FindVariantAsync(variantId, cancellationToken);
                if (variant is null || variant.ItemId != item.Id || !variant.IsActive)
                {
                    return Error.Validation("pos.variant_invalid", "The variant is not one of the item's variants.").WithWhy(("lineNo", lineNo), ("item", item.Code));
                }
            }
            else if (item.HasVariants)
            {
                return Error.Validation("pos.variant_required", "The item has variants; the line names the one sold.").WithWhy(("lineNo", lineNo), ("item", item.Code));
            }

            var units = await items.UomsAsync(item.Id, cancellationToken);
            var uomId = l.UomId ?? item.SalesUomId ?? item.BaseUomId;
            var unit = units.FirstOrDefault(u => u.UomId == uomId);
            if (unit is null)
            {
                return Error.Validation("pos.uom_not_item_unit", "The unit is not one of the item's units.").WithWhy(("lineNo", lineNo), ("item", item.Code));
            }

            var toBase = await items.ToBaseAsync(item.Id, unit.UomId, l.Quantity, cancellationToken);
            if (toBase.IsFailure)
            {
                return toBase.Error!.WithWhy(("lineNo", lineNo));
            }

            var line = new PosOrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = order.Id,
                LineNo = lineNo,
                ItemId = item.Id,
                VariantId = variant?.Id,
                ItemCode = variant?.Sku ?? item.Code,
                Description = l.Description is { Count: > 0 } ? Validation.Text(l.Description) : variant is null || variant.Name.IsEmpty ? item.Name : variant.Name,
                UomId = unit.UomId,
                UomCode = unit.UomCode,
                Quantity = l.Quantity,
                BaseQuantity = toBase.Value.Quantity,
                UnitPrice = l.UnitPrice,
                DiscountPct = l.DiscountPct,
                TaxCodeId = l.TaxCodeId,
                IsStockItem = item.IsStockItem,
            };
            resolved.Add((line, item, l));
        }

        if (refunded is not null && refundedByLine is not null)
        {
            // A refund gives back what a sale sold: each line, at the sale's own price, never more than what is left of it.
            var check = ApplyRefundLimits(resolved, refunded, refundedByLine);
            if (check.IsFailure)
            {
                return check.Error!;
            }
        }

        // 2. The engine's price of each line today, to record beside the price charged and to refuse an override the register does not allow.
        var pricingLines = resolved.Select(static r => new PricingLineRequest(r.Line.LineNo.ToString(CultureInfo.InvariantCulture), r.Item.Id, r.Line.VariantId, r.Line.UomId, 1m)).ToList();
        var priced = await pricing.PriceAsync(new PricingRequest(register.CompanyId, partnerId, currency.Code, orderDate, pricingLines, register.PriceListId, RegisterService.Channel), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        var pricesIncludeTax = priced.Value.PricesIncludeTax ?? false;
        var pricedByKey = priced.Value.Lines.ToDictionary(static p => p.Key, StringComparer.Ordinal);
        foreach (var (line, _, _) in resolved)
        {
            var p = pricedByKey.GetValueOrDefault(line.LineNo.ToString(CultureInfo.InvariantCulture));
            line.ListUnitPrice = p is null || p.Problem is not null ? null : p.UnitPrice;
            var overridden = refunded is null && (line.DiscountPct != 0m || line.ListUnitPrice is null || line.ListUnitPrice.Value != line.UnitPrice);
            if (overridden && !register.AllowPriceOverride)
            {
                return Error.Validation("pos.price_override_not_allowed", "This register sells at the catalogue's prices; refresh the till's catalogue or allow price changes on the register.")
                    .WithWhy(("lineNo", line.LineNo), ("item", line.ItemCode), ("charged", line.UnitPrice), ("listPrice", line.ListUnitPrice), ("discountPct", line.DiscountPct));
            }

            var gross = RegisterService.Round(line.Quantity * line.UnitPrice, company);
            var amount = RegisterService.Round(line.Quantity * line.UnitPrice * (1m - line.DiscountPct / 100m), company);
            line.DiscountAmount = gross - amount;
            line.NetAmount = amount;
        }

        // 3. Tax by the tax engine with the codes the catalogue gave (pinned), on the order's date, for the customer.
        var taxLines = resolved.Select(static r => new TaxDocumentLine(r.Line.LineNo.ToString(CultureInfo.InvariantCulture), r.Line.NetAmount, r.Item.Id, TaxCodeId: r.Line.TaxCodeId)).ToList();
        var taxed = await tax.CalculateAsync(new TaxDocumentRequest(register.CompanyId, TaxDirections.Sales, orderDate, currency.Code, pricesIncludeTax, taxLines, partnerId), cancellationToken);
        if (taxed.IsFailure)
        {
            return taxed.Error!;
        }

        var taxedByKey = taxed.Value.Document.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        decimal totalNet = 0m, totalTax = 0m, totalGross = 0m;
        foreach (var (line, _, _) in resolved)
        {
            var t = taxedByKey[line.LineNo.ToString(CultureInfo.InvariantCulture)];
            if (t.IsReverseCharge)
            {
                return Error.Validation("pos.reverse_charge", "A till does not sell under reverse charge.").WithWhy(("lineNo", line.LineNo));
            }

            line.NetAmount = RegisterService.Round(t.Net, company);
            line.TaxAmount = RegisterService.Round(t.Tax, company);
            line.TaxCodeId = t.TaxCodeId;
            line.TaxRatePct = t.RatePct;
            line.GrossAmount = line.NetAmount + line.TaxAmount;
            totalNet += line.NetAmount;
            totalTax += line.TaxAmount;
            totalGross += line.GrossAmount;
        }

        order.PricesIncludeTax = pricesIncludeTax;
        order.EinvoiceScheme = taxed.Value.EinvoicingScheme;

        // 4. Totals, cash rounding when the whole order is paid in cash, and the check against what the receipt showed.
        var payments = Validation.OneOfEach(request.Payments, out var methodError);
        if (methodError is not null)
        {
            return methodError;
        }

        var allCash = payments.All(static p => p.Method == PosPaymentMethods.Cash);
        var increment = allCash ? await registers.RoundingIncrementAsync(register, company, cancellationToken) : 0m;
        var totalDue = increment > 0m ? RoundToIncrement(totalGross, increment, new RoundingPolicy(company.RoundingMode)) : totalGross;
        order.TotalNet = totalNet;
        order.TotalTax = totalTax;
        order.TotalGross = totalGross;
        order.Rounding = totalDue - totalGross;
        order.TotalDue = totalDue;
        if (request.TotalDue != totalDue)
        {
            return Error.Conflict("pos.totals_mismatch", "The receipt's total differs from the total computed here: the till's catalogue is out of date. Refresh it and review the order.")
                .WithWhy(("deviceTotalDue", request.TotalDue), ("totalDue", totalDue), ("totalNet", totalNet), ("totalTax", totalTax), ("rounding", order.Rounding));
        }

        var paid = payments.Sum(static p => p.Amount);
        var change = request.ChangeGiven;
        if (change < 0m || (change > 0m && (kind.Value == PosOrderKinds.Refund || change > payments.Where(static p => p.Method == PosPaymentMethods.Cash).Sum(static p => p.Amount))))
        {
            return Error.Validation("pos.change_invalid", "Change is given from the cash tendered on a sale.").WithWhy(("changeGiven", change));
        }

        if (RegisterService.Round(paid - change, company) != totalDue)
        {
            return Error.Validation("pos.payments_mismatch", "What was paid, less the change, is the order's total.").WithWhy(("paid", paid), ("changeGiven", change), ("totalDue", totalDue));
        }

        if (payments.Any(static p => p.Method == PosPaymentMethods.Card) && register.CardAccountId is null)
        {
            return Error.Validation("pos.card_account_missing", "The register has no card account to take card payments into.").WithWhy(("register", register.Code));
        }

        if (payments.Any(static p => p.Method == PosPaymentMethods.Credit))
        {
            if (!register.AllowCredit)
            {
                return Error.Validation("pos.credit_not_allowed", "This register does not sell on account.").WithWhy(("register", register.Code));
            }

            if (customer is null || request.PartnerId is null)
            {
                return Error.Validation("pos.customer_required", "A sale on account names the customer.");
            }
        }

        order.TotalPaid = paid;
        order.ChangeGiven = change;

        // 5. Number, then the journal: revenue per line, output tax per code, rounding, and the tenders (POSTING_RULES §2).
        await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "POS", "POS-{yyyy}-{seq:6}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, register.BranchId is { } b0 ? new BranchId(b0) : null, orderDate, order.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        order.Number = number.Value.Text;
        var postingLines = new List<PostingLine>();
        foreach (var (line, item, _) in resolved)
        {
            postingLines.Add(new PostingLine(AccountRoles.Revenue, -sign * line.NetAmount, new PostingKeys(DocumentType, item.ItemPostingGroupId, customer?.PostingGroupId), PartnerId: partnerId, Description: line.Description));
        }

        var taxEntries = new List<TaxEntryRequest>();
        foreach (var byCode in resolved.Where(static r => r.Line.TaxCodeId is not null).GroupBy(static r => r.Line.TaxCodeId!.Value).OrderBy(static g => g.Key))
        {
            var code = await tax.FindCodeAsync(byCode.Key, orderDate, cancellationToken);
            if (code.IsFailure)
            {
                return code.Error!;
            }

            var amount = byCode.Sum(static r => r.Line.TaxAmount);
            var basis = byCode.Sum(static r => r.Line.NetAmount);
            if (amount != 0m)
            {
                postingLines.Add(new PostingLine(code.Value.OutputAccountRole, -sign * amount, new PostingKeys(DocumentType), PartnerId: partnerId, TaxCodeId: byCode.Key, TaxBase: sign * basis));
            }

            taxEntries.Add(new TaxEntryRequest(byCode.Key, TaxDirections.Sales, code.Value.RatePct, sign * basis, sign * amount, sign * basis, sign * amount, false, code.Value.IsRecoverable));
        }

        if (order.Rounding != 0m)
        {
            postingLines.Add(new PostingLine(AccountRoles.RoundingDifferences, -sign * order.Rounding, new PostingKeys(DocumentType), Description: LocalizedText.Bilingual("Cash rounding", "تقريب نقدي")));
        }

        var cashBox = (await bankAccounts.FindAsync(register.CashAccountId, cancellationToken))!;
        var card = register.CardAccountId is { } cardId ? await bankAccounts.FindAsync(cardId, cancellationToken) : null;
        var cashNet = payments.Where(static p => p.Method == PosPaymentMethods.Cash).Sum(static p => p.Amount) - change;
        var cardTotal = payments.Where(static p => p.Method == PosPaymentMethods.Card).Sum(static p => p.Amount);
        var creditTotal = payments.Where(static p => p.Method == PosPaymentMethods.Credit).Sum(static p => p.Amount);
        Guid? cashTransactionId = cashNet != 0m ? Guid.CreateVersion7() : null;
        Guid? cardTransactionId = cardTotal != 0m ? Guid.CreateVersion7() : null;
        if (cashTransactionId is { } cashTx)
        {
            postingLines.Add(new PostingLine(SessionService.RoleOf(cashBox.Kind), sign * cashNet, new PostingKeys(DocumentType, BankAccountId: cashBox.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: cashTx));
        }

        if (cardTransactionId is { } cardTx)
        {
            postingLines.Add(new PostingLine(SessionService.RoleOf(card!.Kind), sign * cardTotal, new PostingKeys(DocumentType, BankAccountId: card.Id), SubledgerType: SubledgerTypes.Bank, SubledgerRef: cardTx));
        }

        if (creditTotal != 0m)
        {
            postingLines.Add(new PostingLine(AccountRoles.AR, sign * creditTotal, new PostingKeys(DocumentType, PartnerPostingGroupId: customer!.PostingGroupId), SubledgerType: SubledgerTypes.Receivables, SubledgerRef: order.Id, PartnerId: partnerId, DueDate: orderDate));
        }

        var description = kind.Value == PosOrderKinds.Refund
            ? LocalizedText.Bilingual($"POS refund {order.Number} of {refunded!.Number}", $"استرجاع نقطة بيع {order.Number} عن {refunded.Number}")
            : LocalizedText.Bilingual($"POS receipt {order.Number}", $"إيصال نقطة بيع {order.Number}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "pos", DocumentType, order.Id, orderDate, currency.Code, postingLines, order.Number, orderDate, description,
            register.BranchId is { } b ? new BranchId(b) : null, IdempotencyKey: $"pos_order:{order.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        order.JournalEntryId = posted.Value.EntryId;

        // 6. The subledgers the journal's lines point at: cash box and card movements, the receivable, the tax ledger.
        var paymentNo = 0;
        foreach (var p in payments)
        {
            paymentNo++;
            var payment = new PosPayment { Id = Guid.CreateVersion7(), OrderId = order.Id, LineNo = paymentNo, Method = p.Method, Amount = p.Amount, Reference = Validation.Trim(p.Reference) };
            payment.BankAccountId = p.Method switch { PosPaymentMethods.Cash => cashBox.Id, PosPaymentMethods.Card => card!.Id, _ => null };
            payment.BankTransactionId = p.Method switch { PosPaymentMethods.Cash => cashTransactionId, PosPaymentMethods.Card => cardTransactionId, _ => null };
            order.Payments.Add(payment);
        }

        if (cashTransactionId is { } cashTx2)
        {
            await bankTransactions.RecordAsync(new NewBankTransaction(cashTx2, cashBox.Id, order.CompanyId, orderDate, sign * cashNet > 0m ? "receipt" : "disbursement", sign * cashNet, sign * cashNet, order.Number, DocumentType, order.Id, posted.Value.EntryId), cancellationToken);
        }

        if (cardTransactionId is { } cardTx2)
        {
            await bankTransactions.RecordAsync(new NewBankTransaction(cardTx2, card!.Id, order.CompanyId, orderDate, sign > 0m ? "receipt" : "disbursement", sign * cardTotal, sign * cardTotal, order.Number, DocumentType, order.Id, posted.Value.EntryId), cancellationToken);
        }

        if (creditTotal != 0m)
        {
            var item = await receivables.OpenAsync(new NewReceivable(order.CompanyId, partnerId!.Value, sign > 0m ? ReceivableKinds.Invoice : ReceivableKinds.CreditNote, DocumentType, order.Id, order.Number, 1, null,
                orderDate, orderDate, orderDate, null, 0m, currency.Code, sign * creditTotal, sign * creditTotal, 1m, posted.Value.EntryId, register.BranchId), cancellationToken);
            foreach (var payment in order.Payments.Where(static p => p.Method == PosPaymentMethods.Credit))
            {
                payment.ReceivableItemId = item.Id;
            }
        }

        if (taxEntries.Count > 0)
        {
            var recorded = await taxLedger.RecordAsync(new TaxPostingRequest(order.CompanyId, orderDate, orderDate, "pos", DocumentType, order.Id, order.Number, posted.Value.EntryId, partnerId, currency.Code, taxEntries), cancellationToken);
            if (recorded.IsFailure)
            {
                return recorded.Error!;
            }
        }

        // 7. Stock: the goods leave the register's warehouse (or come back on a refund); the costing engine books the cost of sales.
        var stockLines = resolved.Where(static r => r.Line.IsStockItem)
            .Select(r => new StockLine(r.Item.Id, sign > 0m ? StockEntryTypes.SaleShipment : StockEntryTypes.SaleReturn, r.Line.Quantity, register.WarehouseId, UomId: r.Line.UomId, VariantId: r.Line.VariantId, BinId: register.BinId, SourceLineId: r.Line.Id, PartnerId: partnerId))
            .ToList();
        if (stockLines.Count > 0)
        {
            var moved = await inventory.PostAsync(new StockPostingRequest(order.CompanyId, orderDate, DocumentType, order.Id, stockLines, IdempotencyKey: $"pos_order:{order.Id}"), cancellationToken);
            if (moved.IsFailure)
            {
                return moved.Error!;
            }

            order.StockPostingId = moved.Value.PostingId;
        }

        // 8. The session's takings, under its row lock, so the close reads one consistent total.
        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition("SELECT id FROM app.pos_sessions WHERE tenant_id = @tenant AND id = @id FOR UPDATE", new { tenant = uow.Context.TenantId.Value, id = session.Id }, uow.Transaction, cancellationToken: cancellationToken));
        session.CashTaken += sign * cashNet;
        session.CardTaken += sign * cardTotal;
        session.CreditTaken += sign * creditTotal;
        session.OrdersCount += 1;
        session.SalesTotal += sign * totalDue;
        session.UpdatedAt = now;

        foreach (var (line, _, _) in resolved)
        {
            order.Lines.Add(line);
        }

        if (order.EinvoiceScheme is not null)
        {
            order.EinvoiceStatus = "pending";
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, order.Id, order.Number, AuditActions.Posted,
            After: new { order.Kind, register = register.Code, session = session.Number, order.TotalNet, order.TotalTax, order.TotalGross, order.Rounding, order.TotalDue, journalEntryId = order.JournalEntryId, stockPostingId = order.StockPostingId, refunds = refunded?.Number },
            CompanyId: order.CompanyId), cancellationToken);
        if (order.EinvoiceScheme is not null)
        {
            await jobs.EnqueueAsync(new JobRequest(PosEinvoiceJob.JobType, new PosEinvoicePayload(order.Id), IdempotencyKey: $"pos-einvoice:{order.Id}", MaxAttempts: 8), cancellationToken);
        }

        return await MapAsync(order, false, cancellationToken);
    }

    /// <summary>Submits the order to its e-invoicing scheme and records the outcome; <paramref name="automatic"/> marks the submission posting triggered, which a device set to submit by hand refuses.</summary>
    public async Task<Result<PosOrderSummary>> SubmitEinvoiceAsync(Guid id, bool automatic, CancellationToken cancellationToken)
    {
        var order = await db.Orders.Include(static o => o.Lines.OrderBy(static l => l.LineNo)).Include(static o => o.Payments.OrderBy(static p => p.LineNo)).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(AuditEntity, id);
        }

        if (order.EinvoiceScheme is null)
        {
            return Error.Conflict("pos.einvoice_not_required", "The order's tax regime names no e-invoicing scheme.");
        }

        var outcome = await clearance.SubmitAsync(DocumentType, order.Id, automatic, cancellationToken);
        if (outcome.IsFailure)
        {
            return outcome.Error!;
        }

        order.EinvoiceStatus = outcome.Value.Status;
        order.EinvoiceSubmissionId = outcome.Value.SubmissionId;
        order.EinvoiceQr = outcome.Value.QrPayload;
        order.EinvoiceMessages = JsonSerializer.Serialize(outcome.Value.Messages, Json);
        order.EinvoiceSubmittedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(AuditEntity, order.Id, order.Number, AuditActions.StateChanged, After: new { einvoiceStatus = order.EinvoiceStatus, scheme = outcome.Value.Scheme, submissionId = order.EinvoiceSubmissionId }, CompanyId: order.CompanyId), cancellationToken);
        return await MapAsync(order, false, cancellationToken);
    }

    internal Task<PosOrder?> LoadAsync(Guid id, CancellationToken cancellationToken) =>
        db.Orders.AsNoTracking().Include(static o => o.Lines.OrderBy(static l => l.LineNo)).Include(static o => o.Payments.OrderBy(static p => p.LineNo)).SingleOrDefaultAsync(o => o.Id == id, cancellationToken);

    /// <summary>The quantity of each line of a sale already given back by earlier refunds, by the sale's line id.</summary>
    private async Task<Dictionary<Guid, decimal>> RefundedSoFarAsync(Guid saleId, CancellationToken cancellationToken)
    {
        var refunds = await db.Orders.AsNoTracking().Where(o => o.RefundsOrderId == saleId).Select(static o => o.Id).ToListAsync(cancellationToken);
        if (refunds.Count == 0)
        {
            return [];
        }

        // A refund line keeps the sale's line id in its description map under "refunds_line"; simpler and exact: match by item, variant and unit price.
        var lines = await db.OrderLines.AsNoTracking().Where(l => refunds.Contains(l.OrderId)).ToListAsync(cancellationToken);
        var sale = await db.OrderLines.AsNoTracking().Where(l => l.OrderId == saleId).OrderBy(static l => l.LineNo).ToListAsync(cancellationToken);
        var result = new Dictionary<Guid, decimal>();
        foreach (var refundLine in lines)
        {
            var target = sale.FirstOrDefault(s => s.ItemId == refundLine.ItemId && s.VariantId == refundLine.VariantId && s.UnitPrice == refundLine.UnitPrice && s.UomId == refundLine.UomId && (result.GetValueOrDefault(s.Id) + refundLine.Quantity) <= s.Quantity)
                ?? sale.FirstOrDefault(s => s.ItemId == refundLine.ItemId && s.VariantId == refundLine.VariantId);
            if (target is not null)
            {
                result[target.Id] = result.GetValueOrDefault(target.Id) + refundLine.Quantity;
            }
        }

        return result;
    }

    /// <summary>Each refund line takes the sale line's price and discount and may not exceed what is left of that line.</summary>
    private static Result ApplyRefundLimits(List<(PosOrderLine Line, ItemInfo Item, SubmitOrderLineRequest Source)> resolved, PosOrder sale, Dictionary<Guid, decimal> refundedSoFar)
    {
        var left = sale.Lines.ToDictionary(static l => l.Id, l => l.Quantity - refundedSoFar.GetValueOrDefault(l.Id));
        foreach (var (line, _, _) in resolved)
        {
            var candidates = sale.Lines.Where(s => s.ItemId == line.ItemId && s.VariantId == line.VariantId && s.UomId == line.UomId && left[s.Id] > 0m).OrderByDescending(s => s.UnitPrice == line.UnitPrice).ThenBy(static s => s.LineNo).ToList();
            var source = candidates.FirstOrDefault();
            if (source is null)
            {
                return Error.Conflict("pos.refund_exceeds_sale", "The refund gives back more than the sale sold.").WithWhy(("lineNo", line.LineNo), ("item", line.ItemCode), ("sale", sale.Number));
            }

            if (line.Quantity > left[source.Id])
            {
                return Error.Conflict("pos.refund_exceeds_sale", "The refund gives back more than the sale sold.").WithWhy(("lineNo", line.LineNo), ("item", line.ItemCode), ("sale", sale.Number), ("left", left[source.Id]));
            }

            left[source.Id] -= line.Quantity;
            line.UnitPrice = source.UnitPrice;
            line.DiscountPct = source.DiscountPct;
            line.TaxCodeId = source.TaxCodeId;
            line.ListUnitPrice = source.ListUnitPrice;
        }

        return Result.Success();
    }

    /// <summary>Rounds to the nearest multiple of the increment, halves away from zero (IQD 250, SAR 0.05).</summary>
    internal static decimal RoundToIncrement(decimal amount, decimal increment, RoundingPolicy policy) => policy.Round(amount / increment, 0) * increment;

    private static DateTimeOffset ToCompanyTime(DateTimeOffset utc, string timeZone)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(utc, TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        }
        catch (TimeZoneNotFoundException)
        {
            return utc;
        }
        catch (InvalidTimeZoneException)
        {
            return utc;
        }
    }

    internal async Task<PosOrderSummary> MapAsync(PosOrder o, bool replayed, CancellationToken cancellationToken)
    {
        var register = await db.Registers.AsNoTracking().Where(r => r.Id == o.RegisterId).Select(static r => r.Code).SingleAsync(cancellationToken);
        var session = await db.Sessions.AsNoTracking().Where(s => s.Id == o.SessionId).Select(static s => s.Number).SingleAsync(cancellationToken);
        var refunds = o.RefundsOrderId is { } refundsId ? await db.Orders.AsNoTracking().Where(r => r.Id == refundsId).Select(static r => r.Number).SingleOrDefaultAsync(cancellationToken) : null;
        var partner = o.PartnerId is { } partnerId ? await partners.FindAsync(partnerId, cancellationToken) : null;
        var messages = JsonSerializer.Deserialize<List<string>>(o.EinvoiceMessages, Json) ?? [];
        var lines = o.Lines.OrderBy(static l => l.LineNo).Select(static l => new PosOrderLineSummary(l.Id, l.LineNo, l.ItemId, l.VariantId, l.ItemCode, l.Description.Values, l.UomId, l.UomCode, l.Quantity, l.BaseQuantity, l.UnitPrice, l.ListUnitPrice,
            l.DiscountPct, l.DiscountAmount, l.NetAmount, l.TaxCodeId, l.TaxRatePct, l.TaxAmount, l.GrossAmount, l.IsStockItem)).ToList();
        var payments = o.Payments.OrderBy(static p => p.LineNo).Select(static p => new PosPaymentSummary(p.Id, p.LineNo, p.Method, p.BankAccountId, p.Amount, p.Reference, p.BankTransactionId, p.ReceivableItemId)).ToList();
        return new PosOrderSummary(o.Id, o.CompanyId, o.RegisterId, register, o.SessionId, session, o.Number, o.DeviceOrderId, o.Kind, o.RefundsOrderId, refunds, o.PartnerId, partner?.LegalName.Values, o.CashierUserId, o.OrderDate, o.OrderedAt, o.PostedAt,
            o.Currency, o.PricesIncludeTax, o.TotalNet, o.TotalTax, o.TotalGross, o.Rounding, o.TotalDue, o.TotalPaid, o.ChangeGiven, o.JournalEntryId, o.StockPostingId, o.EinvoiceScheme, o.EinvoiceStatus, o.EinvoiceSubmissionId, o.EinvoiceQr, messages, o.Notes, lines, payments, replayed);
    }
}
