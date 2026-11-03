using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Payables.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>
/// A shipment is a purchase order's journey (trader edition, ADR-0035 §4): ordered, in production, shipped, at port,
/// customs, received, as events with dates and notes; the costs along the way (freight, duty, port, transport), each
/// in its own currency at the rate actually paid; what was paid for the goods in the company's currency; and, on
/// arrival, the goods receipt and the landed-cost document posted through the purchasing services, then the
/// supplier's invoice. Nothing is posted outside those services: the shipment adds the timeline, the money paid, the
/// landed cost per unit and the hints a trader should act on. Money paid from the shipment is real (ADR-0035 §11):
/// the goods are paid to the supplier as an advance before the invoice (applied to it when it is posted) or as a
/// payment of the invoice after; a cost is paid to its agent the same way, the agent's invoice clearing the cost when
/// the goods arrive; every payment goes through Banking and every application through Payables.
/// </summary>
public sealed class ShipmentService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IPartnerDirectory partners,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    PurchaseOrderService orders,
    ReceiptService receipts,
    LandedCostService landedCosts,
    InvoiceService invoices,
    IPayments payments,
    IPayables payables,
    IPayableSettlements settlements,
    IBankAccountDirectory bankAccounts) : IPurchaseLandedCosts, IPurchaseShipments
{
    public const string DocumentType = PurchaseDocumentTypes.Shipment;

    /// <summary>The stages in order; a shipment moves forward only, and reaches received through <see cref="ReceiveAsync"/>.</summary>
    public static readonly string[] Stages = ["ordered", "in_production", "shipped", "at_port", "customs", "received"];

    private static readonly string[] ClosedOrderStatuses = ["cancelled", "closed", "rejected"];

    public async Task<IReadOnlyList<PurchaseShipmentSummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Shipments.Include(static s => s.Events).Include(static s => s.Costs).Include(static s => s.Payments).AsSplitQuery().AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(s => s.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(s => s.Status == status);
        }

        var shipments = await query.OrderByDescending(static s => s.UpdatedAt).ToListAsync(cancellationToken);
        var result = new List<PurchaseShipmentSummary>(shipments.Count);
        foreach (var shipment in shipments)
        {
            var order = await db.Orders.Include(static o => o.Lines).AsNoTracking().SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
            result.Add(await MapAsync(shipment, order, cancellationToken));
        }

        return result;
    }

    public async Task<Result<PurchaseShipmentSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await LoadAsync(id, true, cancellationToken);
        return shipment is null ? Error.NotFound(DocumentType, id) : await MapAsync(shipment, await OrderOfAsync(shipment, cancellationToken), cancellationToken);
    }

    /// <summary>Starts tracking an order. A draft order is submitted first, so "ordered" is true; one shipment per order.</summary>
    public async Task<Result<PurchaseShipmentSummary>> CreateAsync(CreatePurchaseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var order = await db.Orders.Include(static o => o.Lines).AsNoTracking().SingleOrDefaultAsync(o => o.Id == request.OrderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(PurchaseDocumentTypes.Order, request.OrderId);
        }

        if (ClosedOrderStatuses.Contains(order.Status, StringComparer.Ordinal))
        {
            return Error.Conflict("shipment.order_closed", "A cancelled or closed order is not shipped.").WithWhy(("order", order.Number), ("status", order.Status));
        }

        if (await db.Shipments.AnyAsync(s => s.OrderId == order.Id, cancellationToken))
        {
            return Error.Conflict("shipment.exists", "The order is already tracked as a shipment.").WithWhy(("order", order.Number));
        }

        if (order.Status is "draft" or "rejected")
        {
            var submitted = await orders.SubmitAsync(order.Id, cancellationToken);
            if (submitted.IsFailure)
            {
                return submitted.Error!;
            }
        }

        var now = clock.UtcNow;
        var shipment = new Shipment
        {
            Id = Guid.CreateVersion7(),
            CompanyId = order.CompanyId,
            OrderId = order.Id,
            Status = "ordered",
            ExpectedArrival = request.ExpectedArrival ?? order.ExpectedDate,
            TrackingReference = Shared.Trim(request.TrackingReference),
            Notes = Shared.Trim(request.Notes),
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = now,
            UpdatedAt = now,
        };
        shipment.Events.Add(new ShipmentEvent { Id = Guid.CreateVersion7(), ShipmentId = shipment.Id, Status = "ordered", OccurredOn = order.OrderDate, CreatedBy = shipment.CreatedBy, CreatedAt = now });
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.Created, After: new { order = order.Number, shipment.ExpectedArrival, shipment.TrackingReference }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, await OrderOfAsync(shipment, cancellationToken), cancellationToken);
    }

    public async Task<Result<PurchaseShipmentSummary>> UpdateAsync(Guid id, UpdatePurchaseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        shipment.ExpectedArrival = request.ExpectedArrival;
        shipment.TrackingReference = Shared.Trim(request.TrackingReference);
        shipment.Notes = Shared.Trim(request.Notes);
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(shipment, await OrderOfAsync(shipment, cancellationToken), cancellationToken);
    }

    /// <summary>Moves the shipment to a later stage (never back, never to received: arrival goes through <see cref="ReceiveAsync"/>).</summary>
    public async Task<Result<PurchaseShipmentSummary>> AdvanceAsync(Guid id, AdvancePurchaseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        var target = Array.IndexOf(Stages, request.Status);
        if (target < 1 || request.Status == "received")
        {
            return Error.Validation("shipment.status_invalid", "The next stage is in_production, shipped, at_port or customs; arrival is recorded by receiving the goods.").WithWhy(("status", request.Status), ("allowed", Stages[1..^1]));
        }

        if (shipment.Status is "received" or "cancelled")
        {
            return Error.Conflict("shipment.closed", "A received or cancelled shipment does not move.").WithWhy(("status", shipment.Status));
        }

        if (target <= Array.IndexOf(Stages, shipment.Status))
        {
            return Error.Validation("shipment.status_backwards", "A shipment moves forward only.").WithWhy(("from", shipment.Status), ("to", request.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var now = clock.UtcNow;
        shipment.Events.Add(new ShipmentEvent { Id = Guid.CreateVersion7(), ShipmentId = shipment.Id, Status = request.Status, OccurredOn = request.OccurredOn ?? clock.TodayIn(company.TimeZone), Note = Shared.Trim(request.Note), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = now });
        var before = shipment.Status;
        shipment.Status = request.Status;
        shipment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var order = await OrderOfAsync(shipment, cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.StateChanged, Before: new { status = before }, After: new { status = shipment.Status, request.OccurredOn, request.Note }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    /// <summary>Adds a cost of the journey; in a foreign currency the rate actually paid is given, or the spot rate of the day stands in.</summary>
    public async Task<Result<PurchaseShipmentSummary>> AddCostAsync(Guid id, SavePurchaseShipmentCostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status is "received" or "cancelled")
        {
            return Error.Conflict("shipment.costs_locked", "The costs of a received shipment were landed on its stock; add a later cost as a landed-cost document.").WithWhy(("status", shipment.Status));
        }

        if (request.Amount <= 0m)
        {
            return Error.Validation("shipment.amount_invalid", "A cost is a positive amount.");
        }

        var chargeType = await db.ChargeTypes.AsNoTracking().SingleOrDefaultAsync(c => c.Id == request.ChargeTypeId && c.IsActive, cancellationToken);
        if (chargeType is null)
        {
            return Error.Validation("shipment.charge_type_invalid", "The cost type does not exist or is inactive.").WithWhy(("chargeTypeId", request.ChargeTypeId));
        }

        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var currency = await Shared.CurrencyAsync(companies, request.Currency, company.FunctionalCurrency.Code, "shipment", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        if (request.ExchangeRate is { } given && given <= 0m)
        {
            return Error.Validation("shipment.rate_invalid", "An exchange rate is positive.");
        }

        var paidOn = request.PaidOn ?? clock.TodayIn(company.TimeZone);
        var rate = 1m;
        if (!string.Equals(currency.Value.Code, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            if (request.ExchangeRate is { } paidRate)
            {
                rate = paidRate;
            }
            else
            {
                var resolved = await rates.ResolveAsync(company.Id, currency.Value.Code, company.FunctionalCurrency.Code, paidOn, RateTypes.Spot, cancellationToken);
                if (resolved.IsFailure)
                {
                    return Error.Validation("shipment.rate_missing", "Give the rate you paid, or enter the day's rate under Rates.").WithWhy(("from", currency.Value.Code), ("to", company.FunctionalCurrency.Code), ("date", paidOn));
                }

                rate = resolved.Value.Rate.Rate;
            }
        }

        if (request.PartnerId is { } partnerId && await partners.FindAsync(partnerId, cancellationToken) is null)
        {
            return Error.Validation("shipment.partner_unknown", "The cost's supplier does not exist.").WithWhy(("partnerId", partnerId));
        }

        var now = clock.UtcNow;
        var cost = new ShipmentCost
        {
            Id = Guid.CreateVersion7(),
            ShipmentId = shipment.Id,
            LineNo = shipment.Costs.Count == 0 ? 1 : shipment.Costs.Max(static c => c.LineNo) + 1,
            ChargeTypeId = chargeType.Id,
            Description = Shared.Trim(request.Description),
            Amount = Shared.Round(request.Amount, currency.Value),
            Currency = currency.Value.Code,
            ExchangeRate = rate,
            AmountRc = Shared.Round(request.Amount * rate, company.FunctionalCurrency),
            PaidOn = request.PaidOn,
            PartnerId = request.PartnerId,
            CreatedAt = now,
        };
        shipment.Costs.Add(cost);
        shipment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var order = await OrderOfAsync(shipment, cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.Updated, After: new { cost = chargeType.Code, cost.Amount, cost.Currency, cost.ExchangeRate, cost.AmountRc }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    public async Task<Result<PurchaseShipmentSummary>> RemoveCostAsync(Guid id, Guid costId, CancellationToken cancellationToken)
    {
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status is "received" or "cancelled")
        {
            return Error.Conflict("shipment.costs_locked", "The costs of a received shipment were landed on its stock.").WithWhy(("status", shipment.Status));
        }

        var cost = shipment.Costs.SingleOrDefault(c => c.Id == costId);
        if (cost is null)
        {
            return Error.NotFound("shipment_cost", costId);
        }

        if (shipment.Payments.Any(p => p.CostId == costId))
        {
            return Error.Conflict("shipment.cost_paid", "The cost was paid from the shipment; the payment stands, so the cost stays.").WithWhy(("costId", costId));
        }

        shipment.Costs.Remove(cost);
        db.ShipmentCosts.Remove(cost);
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(shipment, await OrderOfAsync(shipment, cancellationToken), cancellationToken);
    }

    /// <summary>Records what was paid for the goods in the company's currency; the effective rate and the landed cost per unit follow it.</summary>
    public async Task<Result<PurchaseShipmentSummary>> SetPaymentAsync(Guid id, SetPurchaseShipmentPaymentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (request.PaidAmountRc is { } paid && paid < 0m)
        {
            return Error.Validation("shipment.paid_invalid", "The amount paid is zero or more.");
        }

        if (shipment.Payments.Any(static p => p.Kind == "goods"))
        {
            return Error.Conflict("shipment.paid_by_payments", "The goods were paid from the shipment; what was paid follows those payments.");
        }

        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        shipment.PaidAmountRc = request.PaidAmountRc is { } amount ? Shared.Round(amount, company.FunctionalCurrency) : null;
        shipment.PaidOn = request.PaidAmountRc is null ? null : request.PaidOn ?? clock.TodayIn(company.TimeZone);
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var order = await OrderOfAsync(shipment, cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.Updated, After: new { shipment.PaidAmountRc, shipment.PaidOn }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    /// <summary>
    /// Pays the supplier for the goods from a cash box or bank account: before the supplier's invoice is posted, an
    /// advance in the order's currency (applied to the invoice when it is); after, a payment of the invoice's open item.
    /// When the account is in the company's currency and the order is not, what left the account is the rate paid.
    /// </summary>
    public async Task<Result<PurchaseShipmentSummary>> PayAsync(Guid id, PayPurchaseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status == "cancelled")
        {
            return Error.Conflict("shipment.closed", "A cancelled shipment is not paid.").WithWhy(("status", shipment.Status));
        }

        var order = await OrderOfAsync(shipment, cancellationToken);
        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var fc = company.FunctionalCurrency;
        var currency = await Shared.CurrencyAsync(companies, order.Currency, fc.Code, "shipment", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        // Once the supplier's invoice is posted, what is left is what its open items still ask for, whoever paid the
        // rest (this screen, Money or the full edition's payments); before, the order's total less this screen's payments.
        var invoiceItems = await InvoiceItemsAsync(shipment.InvoiceId, cancellationToken);
        var outstanding = invoiceItems.Count > 0
            ? invoiceItems.Sum(static i => i.RemainingTc)
            : order.TotalGross - shipment.Payments.Where(static p => p.Kind == "goods").Sum(static p => p.AmountTc);
        if (outstanding <= 0m)
        {
            return Error.Conflict("shipment.goods_paid", "The goods are paid in full.").WithWhy(("order", order.Number), ("total", order.TotalGross));
        }

        var amount = request.Amount is { } given ? Shared.Round(given, currency.Value) : outstanding;
        if (amount <= 0m)
        {
            return Error.Validation("shipment.amount_invalid", "An amount is positive.");
        }

        if (amount > outstanding)
        {
            return Error.Validation("shipment.overpaid", "That is more than is left to pay for the goods.").WithWhy(("amount", amount), ("outstanding", outstanding), ("currency", order.Currency));
        }

        var bank = await AccountAsync(shipment, request.BankAccountId, cancellationToken);
        if (bank.IsFailure)
        {
            return bank.Error!;
        }

        var foreign = !string.Equals(order.Currency, fc.Code, StringComparison.Ordinal);
        decimal? bankAmount = null;
        decimal? rate = null;
        if (foreign && string.Equals(bank.Value.Currency, fc.Code, StringComparison.Ordinal))
        {
            if (request.PaidRc is not { } paidRc || paidRc <= 0m)
            {
                return Error.Validation("shipment.paid_rc_required", "The account is in the company's currency: give what left it, so the rate you paid is kept.").WithWhy(("bankCurrency", bank.Value.Currency), ("orderCurrency", order.Currency));
            }

            bankAmount = Shared.Round(paidRc, fc);
            rate = RoundingPolicy.Default.Round(bankAmount.Value / amount, 12);
        }
        else if (!string.Equals(bank.Value.Currency, order.Currency, StringComparison.Ordinal) && !string.Equals(bank.Value.Currency, fc.Code, StringComparison.Ordinal))
        {
            return Error.Validation("shipment.bank_currency", "Pay from an account in the order's currency or in the company's.").WithWhy(("bankCurrency", bank.Value.Currency), ("orderCurrency", order.Currency));
        }

        var paidOn = request.PaidOn ?? clock.TodayIn(company.TimeZone);
        var method = request.Method ?? (bank.Value.Kind == BankAccountKinds.Bank ? "transfer" : "cash");
        var reference = Shared.Trim(request.Reference) ?? order.Number;
        var invoiceItem = invoiceItems.Where(static i => i.RemainingTc > 0m).OrderBy(static i => i.Instalment).FirstOrDefault();
        if (invoiceItem is not null && amount > invoiceItem.RemainingTc)
        {
            return Error.Validation("shipment.overpaid", "That is more than the invoice still asks for.").WithWhy(("amount", amount), ("outstanding", invoiceItem.RemainingTc), ("currency", order.Currency));
        }

        var paid = invoiceItem is not null
            ? await payments.PayAsync(new PayOpenItemRequest(shipment.CompanyId, order.PartnerId, bank.Value.Id, invoiceItem.Id, amount, paidOn, method, reference, bankAmount, rate), cancellationToken)
            : await payments.AdvanceAsync(new SupplierAdvanceRequest(shipment.CompanyId, order.PartnerId, bank.Value.Id, order.Currency, amount, paidOn, rate, bankAmount, method, reference), cancellationToken);
        if (paid.IsFailure)
        {
            return paid.Error!;
        }

        var amountRc = !foreign ? amount : bankAmount ?? Shared.Round(amount * paid.Value.ExchangeRate, fc);
        AddPayment(shipment, "goods", null, order.PartnerId, bank.Value.Id, paid.Value, invoiceItem is null ? "supplier_advance" : "supplier_payment", order.Currency, amount, amountRc, paidOn);
        shipment.PaidAmountRc = shipment.Payments.Where(static p => p.Kind == "goods").Sum(static p => p.AmountRc);
        shipment.PaidOn = paidOn;
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.Updated, After: new { payment = paid.Value.Number, amount, order.Currency, amountRc }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    /// <summary>
    /// Pays one cost of the journey to its agent from an account in the company's currency: an advance while the goods
    /// are on the way (applied to the agent's invoice when they arrive), or a payment of that invoice after. A cost
    /// that arrived without an agent gets its invoice now, so the money paid clears it.
    /// </summary>
    public async Task<Result<PurchaseShipmentSummary>> PayCostAsync(Guid id, Guid costId, PayPurchaseShipmentCostRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status == "cancelled")
        {
            return Error.Conflict("shipment.closed", "A cancelled shipment is not paid.").WithWhy(("status", shipment.Status));
        }

        var cost = shipment.Costs.SingleOrDefault(c => c.Id == costId);
        if (cost is null)
        {
            return Error.NotFound("shipment_cost", costId);
        }

        if (shipment.Payments.Any(p => p.CostId == costId))
        {
            return Error.Conflict("shipment.cost_paid", "The cost is paid already.").WithWhy(("costId", costId));
        }

        var partnerId = request.PartnerId ?? cost.PartnerId;
        if (partnerId is null)
        {
            return Error.Validation("shipment.cost_partner_required", "Name who was paid (the freight forwarder, the customs broker): what is paid to them is kept on their account.").WithWhy(("costId", costId));
        }

        if (cost.InvoiceId is not null && cost.PartnerId != partnerId)
        {
            return Error.Validation("shipment.cost_partner_fixed", "The cost is invoiced by another agent already.").WithWhy(("costId", costId));
        }

        if (await partners.FindAsync(partnerId.Value, cancellationToken) is null)
        {
            return Error.Validation("shipment.partner_unknown", "The cost's supplier does not exist.").WithWhy(("partnerId", partnerId));
        }

        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var fc = company.FunctionalCurrency;
        var bank = await AccountAsync(shipment, request.BankAccountId, cancellationToken);
        if (bank.IsFailure)
        {
            return bank.Error!;
        }

        if (!string.Equals(bank.Value.Currency, fc.Code, StringComparison.Ordinal))
        {
            return Error.Validation("shipment.cost_bank_currency", "A cost is paid from an account in the company's currency, at the amount it was recorded at.").WithWhy(("bankCurrency", bank.Value.Currency), ("currency", fc.Code));
        }

        var order = await OrderOfAsync(shipment, cancellationToken);
        var paidOn = request.PaidOn ?? clock.TodayIn(company.TimeZone);
        cost.PartnerId = partnerId;
        if (cost.InvoiceId is null && cost.LandedCostChargeId is not null)
        {
            var invoiced = await InvoiceCostsAsync(shipment, order, [cost], paidOn, fc, cancellationToken);
            if (invoiced.IsFailure)
            {
                return invoiced.Error!;
            }
        }

        var method = request.Method ?? (bank.Value.Kind == BankAccountKinds.Bank ? "transfer" : "cash");
        var reference = Shared.Trim(request.Reference) ?? order.Number;
        var invoiceItem = await OpenInvoiceItemAsync(cost.InvoiceId, cancellationToken);
        if (cost.InvoiceId is not null && invoiceItem is null)
        {
            return Error.Conflict("shipment.cost_settled", "The agent's invoice for this cost has nothing left to pay.").WithWhy(("costId", costId));
        }

        var amount = invoiceItem is null ? cost.AmountRc : Math.Min(cost.AmountRc, invoiceItem.RemainingTc);
        var paid = invoiceItem is not null
            ? await payments.PayAsync(new PayOpenItemRequest(shipment.CompanyId, partnerId.Value, bank.Value.Id, invoiceItem.Id, amount, paidOn, method, reference), cancellationToken)
            : await payments.AdvanceAsync(new SupplierAdvanceRequest(shipment.CompanyId, partnerId.Value, bank.Value.Id, fc.Code, amount, paidOn, Method: method, Reference: reference), cancellationToken);
        if (paid.IsFailure)
        {
            return paid.Error!;
        }

        AddPayment(shipment, "cost", cost.Id, partnerId.Value, bank.Value.Id, paid.Value, invoiceItem is null ? "supplier_advance" : "supplier_payment", fc.Code, amount, amount, paidOn);
        cost.PaidOn = paidOn;
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.Updated, After: new { costPayment = paid.Value.Number, cost.LineNo, amount }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    /// <summary>
    /// The goods arrived: a receipt for everything still open on the order is created and posted into the warehouse,
    /// and the costs recorded so far become a landed-cost document posted onto that receipt, so stock carries the
    /// true cost. Both go through the receipt and landed-cost services exactly as if entered by hand.
    /// </summary>
    public async Task<Result<PurchaseShipmentSummary>> ReceiveAsync(Guid id, ReceivePurchaseShipmentRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status is "received" or "cancelled")
        {
            return Error.Conflict("shipment.closed", "The shipment is already received or cancelled.").WithWhy(("status", shipment.Status));
        }

        var order = await OrderOfAsync(shipment, cancellationToken);
        var open = order.Lines
            .Where(static l => l.Status is not ("cancelled" or "closed"))
            .Select(static l => (Line: l, Open: l.Quantity - l.QtyReceived - l.QtyCancelled))
            .Where(static x => x.Open > 0m)
            .ToList();
        if (open.Count == 0)
        {
            return Error.Conflict("shipment.nothing_to_receive", "Every line of the order has been received already.").WithWhy(("order", order.Number));
        }

        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var postingDate = request.PostingDate ?? clock.TodayIn(company.TimeZone);
        var receipt = await receipts.CreateAsync(new SaveReceiptRequest(order.Id, open.Select(static x => new SaveReceiptLineRequest(x.Line.Id, x.Open)).ToList(), request.WarehouseId, postingDate, Notes: shipment.TrackingReference), cancellationToken);
        if (receipt.IsFailure)
        {
            return receipt.Error!;
        }

        var posted = await receipts.PostAsync(receipt.Value.Id, cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        shipment.ReceiptId = posted.Value.Id;
        if (shipment.Costs.Count > 0)
        {
            var charges = shipment.Costs.OrderBy(static c => c.LineNo).Select(static c => new SaveLandedCostChargeRequest(c.ChargeTypeId, c.AmountRc, c.PartnerId, c.Description)).ToList();
            var landed = await landedCosts.CreateAsync(new SaveLandedCostRequest(shipment.CompanyId, charges, posted.Value.Lines.Select(static l => l.Id).ToList(), postingDate, company.FunctionalCurrency.Code, order.Number, shipment.TrackingReference), cancellationToken);
            if (landed.IsFailure)
            {
                return landed.Error!;
            }

            var landedPosted = await landedCosts.PostAsync(landed.Value.Id, cancellationToken);
            if (landedPosted.IsFailure)
            {
                return landedPosted.Error!;
            }

            shipment.LandedCostId = landed.Value.Id;

            // Each cost is now a charge of the landed-cost document, waiting in the clearing account; the agents named
            // on the costs invoice them, so what is owed to them (or what they were paid in advance) settles it.
            var landedCharges = landedPosted.Value.Charges.OrderBy(static c => c.LineNo).ToList();
            var orderedCosts = shipment.Costs.OrderBy(static c => c.LineNo).ToList();
            for (var i = 0; i < orderedCosts.Count && i < landedCharges.Count; i++)
            {
                orderedCosts[i].LandedCostChargeId = landedCharges[i].Id;
            }

            var invoiced = await InvoiceCostsAsync(shipment, order, orderedCosts.Where(static c => c.PartnerId is not null).ToList(), postingDate, company.FunctionalCurrency, cancellationToken);
            if (invoiced.IsFailure)
            {
                return invoiced.Error!;
            }
        }

        var now = clock.UtcNow;
        shipment.Events.Add(new ShipmentEvent { Id = Guid.CreateVersion7(), ShipmentId = shipment.Id, Status = "received", OccurredOn = postingDate, Note = posted.Value.Number, CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = now });
        shipment.Status = "received";
        shipment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.StateChanged, After: new { status = shipment.Status, receipt = posted.Value.Number, landedCost = shipment.LandedCostId }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, await OrderOfAsync(shipment, cancellationToken), cancellationToken);
    }

    /// <summary>The supplier's invoice for the received goods, at the ordered prices: created, submitted and, when the match passes, posted into the payable.</summary>
    public async Task<Result<PurchaseShipmentSummary>> RecordInvoiceAsync(Guid id, RecordPurchaseShipmentInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.ReceiptId is null)
        {
            return Error.Conflict("shipment.not_received", "Receive the goods first; the invoice is matched against the receipt.").WithWhy(("status", shipment.Status));
        }

        if (shipment.InvoiceId is not null)
        {
            return Error.Conflict("shipment.invoiced", "The shipment's invoice is already recorded.").WithWhy(("invoiceId", shipment.InvoiceId));
        }

        var receipt = await receipts.GetAsync(shipment.ReceiptId.Value, cancellationToken);
        if (receipt.IsFailure)
        {
            return receipt.Error!;
        }

        var order = await OrderOfAsync(shipment, cancellationToken);
        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var today = clock.TodayIn(company.TimeZone);
        var lines = receipt.Value.Lines.Select(static l => new SaveInvoiceLineRequest("receipt", l.QtyInOrderUom, l.UnitPrice, ReceiptLineId: l.Id)).ToList();
        var invoice = await invoices.CreateAsync(new SaveInvoiceRequest(shipment.CompanyId, order.PartnerId, lines, "invoice", Shared.Trim(request.SupplierInvoiceNumber), request.DocumentDate ?? today, request.PostingDate ?? today, order.Currency), cancellationToken);
        if (invoice.IsFailure)
        {
            return invoice.Error!;
        }

        var submitted = await invoices.SubmitAsync(invoice.Value.Id, cancellationToken);
        if (submitted.IsFailure)
        {
            return submitted.Error!;
        }

        if (submitted.Value.Status == "approved")
        {
            var posted = await invoices.PostAsync(invoice.Value.Id, cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            // What was paid in advance for the goods settles the invoice; a rate difference is realised there.
            var applied = await ApplyAdvancesAsync(shipment.Payments.Where(static p => p.Kind == "goods").ToList(), invoice.Value.Id, posted.Value.PostingDate, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }
        }

        shipment.InvoiceId = invoice.Value.Id;
        shipment.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.Updated, After: new { invoice = invoice.Value.Number }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    public async Task<Result<PurchaseShipmentSummary>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var shipment = await LoadAsync(id, false, cancellationToken);
        if (shipment is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (shipment.Status is "received" or "cancelled")
        {
            return Error.Conflict("shipment.closed", "A received or cancelled shipment is not cancelled.").WithWhy(("status", shipment.Status));
        }

        var company = (await companies.FindAsync(new CompanyId(shipment.CompanyId), cancellationToken))!;
        var now = clock.UtcNow;
        shipment.Events.Add(new ShipmentEvent { Id = Guid.CreateVersion7(), ShipmentId = shipment.Id, Status = "cancelled", OccurredOn = clock.TodayIn(company.TimeZone), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = now });
        shipment.Status = "cancelled";
        shipment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        var order = await OrderOfAsync(shipment, cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, shipment.Id, order.Number, AuditActions.StateChanged, After: new { status = shipment.Status }, CompanyId: shipment.CompanyId), cancellationToken);
        return await MapAsync(shipment, order, cancellationToken);
    }

    /// <summary>For pricing: each item's landed unit cost from the most recently tracked shipment that carries it (cancelled shipments aside).</summary>
    public async Task<IReadOnlyList<LandedUnitCostInfo>> LatestByItemAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var shipments = await db.Shipments.Include(static s => s.Events).Include(static s => s.Costs).Include(static s => s.Payments).AsSplitQuery().AsNoTracking()
            .Where(s => s.CompanyId == companyId && s.Status != "cancelled").OrderByDescending(static s => s.CreatedAt).ToListAsync(cancellationToken);
        var seen = new HashSet<Guid>();
        var result = new List<LandedUnitCostInfo>();
        foreach (var shipment in shipments)
        {
            var order = await db.Orders.Include(static o => o.Lines).AsNoTracking().SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);
            var summary = await MapAsync(shipment, order, cancellationToken);
            var asOf = summary.Events.Count > 0 ? summary.Events[^1].OccurredOn : summary.OrderedOn;
            var ratePaid = summary.EffectiveRate ?? summary.OrderExchangeRate;
            foreach (var line in summary.Lines)
            {
                if (line.Quantity <= 0m || !seen.Add(line.ItemId))
                {
                    continue;
                }

                result.Add(new LandedUnitCostInfo(line.ItemId, shipment.Id, summary.OrderNumber, summary.Status, asOf, summary.Currency, line.UnitPrice, ratePaid,
                    line.Quantity > 0m ? line.GoodsRc / line.Quantity : 0m, line.Quantity > 0m ? line.CostsRc / line.Quantity : 0m, line.LandedUnitCostRc, line.Quantity));
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<ShipmentUnderwayInfo>> UnderwayAsync(Guid companyId, CancellationToken cancellationToken = default)
    {
        var result = new List<ShipmentUnderwayInfo>();
        foreach (var s in await ListAsync(companyId, null, cancellationToken))
        {
            if (s.Status is "received" or "cancelled")
            {
                continue;
            }

            result.Add(new ShipmentUnderwayInfo(s.Id, s.OrderNumber, s.PartnerId, s.PartnerCode, s.PartnerName, s.Status, s.OrderedOn, s.ExpectedArrival, s.Currency, s.GoodsTotal, s.GoodsTotalRc,
                s.PaidAmountRc, s.CostsTotalRc, s.LandedTotalRc, s.Hints.Select(static h => h.Code).ToList(), s.Lines.Select(static l => new ShipmentUnderwayLine(l.ItemId, l.ItemCode, l.Quantity)).ToList()));
        }

        return result;
    }

    /// <summary>The agents' invoices for costs that became charges on arrival: one per agent, posted, with what was paid them in advance applied.</summary>
    private async Task<Result> InvoiceCostsAsync(Shipment shipment, PurchaseOrder order, IReadOnlyList<ShipmentCost> costs, DateOnly date, Currency fc, CancellationToken cancellationToken)
    {
        foreach (var group in costs.Where(static c => c.PartnerId is not null && c.InvoiceId is null && c.LandedCostChargeId is not null).GroupBy(static c => c.PartnerId!.Value))
        {
            var lines = group.OrderBy(static c => c.LineNo).Select(static c => new SaveInvoiceLineRequest("charge", 1m, c.AmountRc, Description: c.Description, LandedCostChargeId: c.LandedCostChargeId)).ToList();
            var notes = string.Join(" · ", new[] { order.Number, shipment.TrackingReference }.Where(static x => !string.IsNullOrWhiteSpace(x)));
            var created = await invoices.CreateAsync(new SaveInvoiceRequest(shipment.CompanyId, group.Key, lines, "invoice", null, date, date, fc.Code, Notes: notes), cancellationToken);
            if (created.IsFailure)
            {
                return created.Error!;
            }

            var submitted = await invoices.SubmitAsync(created.Value.Id, cancellationToken);
            if (submitted.IsFailure)
            {
                return submitted.Error!;
            }

            foreach (var cost in group)
            {
                cost.InvoiceId = created.Value.Id;
            }

            if (submitted.Value.Status != "approved")
            {
                continue;
            }

            var posted = await invoices.PostAsync(created.Value.Id, cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }

            var costIds = group.Select(static c => c.Id).ToHashSet();
            var applied = await ApplyAdvancesAsync(shipment.Payments.Where(p => p.CostId is { } c && costIds.Contains(c)).ToList(), created.Value.Id, date, cancellationToken);
            if (applied.IsFailure)
            {
                return applied.Error!;
            }
        }

        return Result.Success();
    }

    /// <summary>Applies the advances of the payments given to the invoice's open items, oldest instalment first, as far as each reaches.</summary>
    private async Task<Result> ApplyAdvancesAsync(IReadOnlyList<ShipmentPayment> paid, Guid invoiceId, DateOnly date, CancellationToken cancellationToken)
    {
        var open = (await payables.ItemsOfAsync(PurchaseDocumentTypes.Invoice, invoiceId, cancellationToken)).Where(static i => i.RemainingTc > 0m && i.Status != "reversed").OrderBy(static i => i.Instalment).ToList();
        var remaining = open.ToDictionary(static i => i.Id, static i => i.RemainingTc);
        foreach (var payment in paid.Where(static p => p.AdvanceItemId is not null && p.AppliedTc < p.AmountTc).OrderBy(static p => p.LineNo))
        {
            foreach (var item in open)
            {
                var left = payment.AmountTc - payment.AppliedTc;
                if (left <= 0m || remaining[item.Id] <= 0m || !string.Equals(item.Currency, payment.Currency, StringComparison.Ordinal))
                {
                    continue;
                }

                var take = Math.Min(left, remaining[item.Id]);
                var on = date > payment.PaidOn ? date : payment.PaidOn;
                var applied = await settlements.ApplyAsync(new ApplyPayableRequest(payment.AdvanceItemId!.Value, item.Id, take, on, $"Advance {payment.PaymentNumber}"), cancellationToken);
                if (applied.IsFailure)
                {
                    return applied.Error!;
                }

                payment.AppliedTc += take;
                remaining[item.Id] -= take;
            }
        }

        return Result.Success();
    }

    /// <summary>The live (not reversed) open items of a posted invoice, none while it is not posted.</summary>
    private async Task<IReadOnlyList<OpenItemInfo>> InvoiceItemsAsync(Guid? invoiceId, CancellationToken cancellationToken) =>
        invoiceId is { } id ? (await payables.ItemsOfAsync(PurchaseDocumentTypes.Invoice, id, cancellationToken)).Where(static i => i.Status != "reversed").ToList() : [];

    private async Task<OpenItemInfo?> OpenInvoiceItemAsync(Guid? invoiceId, CancellationToken cancellationToken) =>
        invoiceId is { } id ? (await payables.ItemsOfAsync(PurchaseDocumentTypes.Invoice, id, cancellationToken)).Where(static i => i.RemainingTc > 0m && i.Status != "reversed").OrderBy(static i => i.Instalment).FirstOrDefault() : null;

    private async Task<Result<BankAccountInfo>> AccountAsync(Shipment shipment, Guid bankAccountId, CancellationToken cancellationToken)
    {
        var bank = await bankAccounts.FindAsync(bankAccountId, cancellationToken);
        return bank is null || bank.CompanyId != shipment.CompanyId || !bank.IsActive
            ? Error.Validation("shipment.bank_account_invalid", "Pay from an active cash box or bank account of the company.").WithWhy(("bankAccountId", bankAccountId))
            : bank;
    }

    private void AddPayment(Shipment shipment, string kind, Guid? costId, Guid partnerId, Guid bankAccountId, PaymentRef paid, string paymentKind, string currency, decimal amountTc, decimal amountRc, DateOnly paidOn)
    {
        var payment = new ShipmentPayment
        {
            Id = Guid.CreateVersion7(),
            ShipmentId = shipment.Id,
            LineNo = shipment.Payments.Count == 0 ? 1 : shipment.Payments.Max(static p => p.LineNo) + 1,
            Kind = kind,
            CostId = costId,
            PartnerId = partnerId,
            BankAccountId = bankAccountId,
            PaymentId = paid.Id,
            PaymentNumber = paid.Number,
            PaymentKind = paymentKind,
            Currency = currency,
            AmountTc = amountTc,
            AmountRc = amountRc,
            PaidOn = paidOn,
            AdvanceItemId = paymentKind == "supplier_advance" ? paid.OpenItemId : null,
            CreatedBy = principal.Principal?.MembershipId.Value,
            CreatedAt = clock.UtcNow,
        };
        shipment.Payments.Add(payment);
        db.ShipmentPayments.Add(payment);
    }

    private async Task<Shipment?> LoadAsync(Guid id, bool readOnly, CancellationToken cancellationToken)
    {
        var query = db.Shipments.Include(static s => s.Events).Include(static s => s.Costs).Include(static s => s.Payments).AsSplitQuery();
        return readOnly ? await query.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken) : await query.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    private Task<PurchaseOrder> OrderOfAsync(Shipment shipment, CancellationToken cancellationToken) =>
        db.Orders.Include(static o => o.Lines).AsNoTracking().SingleAsync(o => o.Id == shipment.OrderId, cancellationToken);

    private async Task<PurchaseShipmentSummary> MapAsync(Shipment s, PurchaseOrder order, CancellationToken cancellationToken)
    {
        var company = (await companies.FindAsync(new CompanyId(s.CompanyId), cancellationToken))!;
        var fc = company.FunctionalCurrency;
        var today = clock.TodayIn(company.TimeZone);
        var partner = await partners.FindAsync(order.PartnerId, cancellationToken);
        var goodsTotal = order.TotalGross;
        var goodsRc = Shared.Round(goodsTotal * order.ExchangeRate, fc);
        var goodsPayments = s.Payments.Where(static p => p.Kind == "goods").ToList();
        var paidTc = goodsPayments.Sum(static p => p.AmountTc);
        decimal? effectiveRate;
        decimal basisRc;
        if (goodsPayments.Count > 0)
        {
            // Paid from the shipment: the goods cost what left the accounts, and what is still to pay at the order's rate.
            var paidRc = goodsPayments.Sum(static p => p.AmountRc);
            effectiveRate = paidTc > 0m ? RoundingPolicy.Default.Round(paidRc / paidTc, 6) : null;
            basisRc = paidRc + Shared.Round((goodsTotal - paidTc) * order.ExchangeRate, fc);
        }
        else
        {
            effectiveRate = s.PaidAmountRc is { } paid && goodsTotal > 0m ? RoundingPolicy.Default.Round(paid / goodsTotal, 6) : null;
            basisRc = s.PaidAmountRc ?? goodsRc;
        }
        var costsRc = s.Costs.Sum(static c => c.AmountRc);
        var landedRc = basisRc + costsRc;

        // Each line's share of the goods paid and of the costs, by value, allocated so the shares add up exactly.
        var live = order.Lines.Where(static l => l.Status != "cancelled").OrderBy(static l => l.LineNo).ToList();
        var weights = live.Select(static l => l.NetAmount <= 0m ? 0m : l.NetAmount).ToList();
        var allocatable = weights.Any(static w => w > 0m);
        var goodsShares = allocatable ? RoundingPolicy.Default.Allocate(new Money(basisRc, fc), weights) : null;
        var costShares = allocatable && costsRc > 0m ? RoundingPolicy.Default.Allocate(new Money(costsRc, fc), weights) : null;
        var lines = new List<PurchaseShipmentLineSummary>(live.Count);
        for (var i = 0; i < live.Count; i++)
        {
            var l = live[i];
            var item = await items.FindAsync(l.ItemId, cancellationToken);
            var uom = item is null ? null : (await items.UomsAsync(item.Id, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            var lineGoods = goodsShares?[i].Amount ?? 0m;
            var lineCosts = costShares?[i].Amount ?? 0m;
            var lineLanded = lineGoods + lineCosts;
            lines.Add(new PurchaseShipmentLineSummary(l.Id, l.LineNo, l.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), l.Quantity, uom?.UomCode ?? string.Empty, l.UnitPrice,
                lineGoods, lineCosts, lineLanded, l.Quantity > 0m ? Shared.Round(lineLanded / l.Quantity, fc) : 0m));
        }

        var chargeTypes = await db.ChargeTypes.AsNoTracking().Where(c => s.Costs.Select(static x => x.ChargeTypeId).Contains(c.Id)).ToDictionaryAsync(static c => c.Id, cancellationToken);
        var partnerCodes = new Dictionary<Guid, PartnerInfo?>();
        foreach (var pid in s.Costs.Where(static c => c.PartnerId is not null).Select(static c => c.PartnerId!.Value).Concat(s.Payments.Select(static p => p.PartnerId)).Distinct())
        {
            partnerCodes[pid] = await partners.FindAsync(pid, cancellationToken);
        }

        var costInvoiceIds = s.Costs.Where(static c => c.InvoiceId is not null).Select(static c => c.InvoiceId!.Value).Distinct().ToList();
        var costInvoices = costInvoiceIds.Count == 0 ? new Dictionary<Guid, string>() : await db.Invoices.AsNoTracking().Where(x => costInvoiceIds.Contains(x.Id)).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var costs = s.Costs.OrderBy(static c => c.LineNo).Select(c =>
        {
            var type = chargeTypes.GetValueOrDefault(c.ChargeTypeId);
            var payment = s.Payments.Where(p => p.CostId == c.Id).OrderBy(static p => p.LineNo).LastOrDefault();
            return new PurchaseShipmentCostSummary(c.Id, c.LineNo, c.ChargeTypeId, type?.Code ?? string.Empty, type?.Name.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), c.Description, c.Amount, c.Currency, c.ExchangeRate, c.AmountRc, c.PaidOn, c.PartnerId,
                c.PartnerId is { } pid ? partnerCodes.GetValueOrDefault(pid)?.Code : null, c.InvoiceId, c.InvoiceId is { } iid ? costInvoices.GetValueOrDefault(iid) : null, payment?.PaymentNumber);
        }).ToList();
        var paymentRows = s.Payments.OrderBy(static p => p.LineNo).Select(p =>
        {
            var who = partnerCodes.GetValueOrDefault(p.PartnerId);
            return new PurchaseShipmentPaymentSummary(p.Id, p.LineNo, p.Kind, p.CostId, p.PartnerId, who?.Code ?? string.Empty, who?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), p.BankAccountId, p.PaymentId, p.PaymentNumber, p.PaymentKind, p.Currency, p.AmountTc, p.AmountRc, p.PaidOn, p.AppliedTc);
        }).ToList();
        var events = s.Events.OrderBy(static e => e.OccurredOn).ThenBy(static e => e.CreatedAt).Select(static e => new PurchaseShipmentEventSummary(e.Id, e.Status, e.OccurredOn, e.Note, e.CreatedAt)).ToList();
        var receipt = s.ReceiptId is { } receiptId ? await db.Receipts.AsNoTracking().Where(r => r.Id == receiptId).Select(static r => r.Number).SingleOrDefaultAsync(cancellationToken) : null;
        var invoice = s.InvoiceId is { } invoiceId ? await db.Invoices.AsNoTracking().Where(x => x.Id == invoiceId).Select(static x => new { x.Number, x.Status }).SingleOrDefaultAsync(cancellationToken) : null;
        var ordered = events.Count > 0 ? events[0].OccurredOn : order.OrderDate;
        // What is left on the goods: the posted invoice's open items when there is one (payments from Money count too).
        var invoiceItems = await InvoiceItemsAsync(s.InvoiceId, cancellationToken);
        var outstandingTc = invoiceItems.Count > 0 ? invoiceItems.Sum(static i => i.RemainingTc) : goodsTotal - paidTc;

        return new PurchaseShipmentSummary(s.Id, s.CompanyId, order.Id, order.Number, order.Status, order.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal),
            order.Currency, order.ExchangeRate, goodsTotal, goodsRc, fc.Code, s.Status, ordered, s.ExpectedArrival, s.TrackingReference, s.Notes, s.PaidAmountRc, s.PaidOn, effectiveRate, costsRc, landedRc,
            s.ReceiptId, receipt, s.LandedCostId, s.InvoiceId, invoice?.Number, invoice?.Status, events, costs, lines, Hints(s, order, today, effectiveRate, costsRc), s.UpdatedAt,
            paymentRows, goodsTotal - outstandingTc, outstandingTc);
    }

    /// <summary>What a trader should look at, in codes the client words with the value where one helps.</summary>
    private static List<PurchaseShipmentHint> Hints(Shipment s, PurchaseOrder order, DateOnly today, decimal? effectiveRate, decimal costsRc)
    {
        var hints = new List<PurchaseShipmentHint>();
        if (s.Status == "cancelled")
        {
            return hints;
        }

        var stage = Array.IndexOf(Stages, s.Status);
        var underway = s.Status != "received";
        if (underway && s.ExpectedArrival is { } eta && eta < today)
        {
            hints.Add(new PurchaseShipmentHint("arrival_overdue", today.DayNumber - eta.DayNumber));
        }

        if (underway && stage >= 2 && costsRc == 0m)
        {
            hints.Add(new PurchaseShipmentHint("no_costs", null));
        }

        if (stage >= 2 && s.PaidAmountRc is null)
        {
            hints.Add(new PurchaseShipmentHint("payment_missing", null));
        }

        // The rate paid matters while the goods are still to be priced; once received, the landed cost per unit says it.
        if (underway && effectiveRate is { } rate && order.ExchangeRate > 0m && rate != order.ExchangeRate)
        {
            var pct = RoundingPolicy.Default.Round((rate - order.ExchangeRate) * 100m / order.ExchangeRate, 1);
            hints.Add(new PurchaseShipmentHint(pct > 0m ? "rate_above_order" : "rate_below_order", Math.Abs(pct)));
        }

        if (s.Status == "received" && s.InvoiceId is null)
        {
            hints.Add(new PurchaseShipmentHint("invoice_missing", null));
        }

        if (underway && s.Events.Count > 0)
        {
            var last = s.Events.Max(static e => e.OccurredOn);
            var idle = today.DayNumber - last.DayNumber;
            if (idle > 30)
            {
                hints.Add(new PurchaseShipmentHint("stale", idle));
            }
        }

        return hints;
    }
}
