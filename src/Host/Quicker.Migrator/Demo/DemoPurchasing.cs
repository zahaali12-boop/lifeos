using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Banking.Application;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Results;
using Quicker.Organization.Application;
using Quicker.Partners.Application;
using Quicker.Payables.Contracts;
using Quicker.Persistence;
using Quicker.Purchasing.Application;
using Quicker.Purchasing.Contracts;

namespace Quicker.Migrator.Demo;

/// <summary>What the purchasing seed produced: purchase orders (history and live), receipts, supplier invoices and debit notes, and payments.</summary>
public sealed record DemoPurchasingOutcome(int Orders, int Receipts, int Invoices, int Payments);

/// <summary>
/// Demo seed v4 (roadmap 4.9): a year of buying for each company. Eleven months of purchase orders from three
/// suppliers, approved and closed, carry the price history (orders do not post, so they sit safely in the closed
/// periods); prices drift upward over the year and each supplier prices a little differently. The live month then
/// runs the whole procure-to-pay cycle through the modules' own services: an on-time supplier (received, invoiced,
/// freight landed two weeks later, paid in full), a late one (received after the promised date, part of it sent
/// back against a debit note, paid in part) and one still open (partly received, invoiced, unpaid, so the aging has
/// something to show), plus an RFQ with three quotes waiting to be compared. Around them, the rest of the chain a
/// buyer meets: a requisition turned into an order that arrives in two deliveries, each invoiced; a blanket agreement
/// with its first release; an invoice five percent above the order price, blocked by the match for an override; and
/// an approved requisition waiting to be ordered. Everything posts through the posting engine and the harness runs
/// before the seed commits (ASSUMPTIONS A-115).
/// </summary>
internal static class DemoPurchasing
{
    private sealed record Plan(string Company, string Warehouse, string Family, decimal BasePrice, decimal Step, int Decimals);

    private static readonly Plan[] Plans =
    [
        new("IQT", "BSR-WH", "BEV", 1500m, 250m, 0),
        new("USI", "BGD-DIST", "ACC", 12m, 3m, 2),
        new("AEG", "DXB-SHOP", "STAT", 20m, 5m, 2),
    ];

    private static readonly (string Key, int LeadTimeDays, decimal PriceFactor, string Email)[] Suppliers =
    [
        ("supp:turkish-foods", 5, 1.00m, "quotes@turkish-foods.example"),
        ("supp:jebel-ali", 7, 1.04m, "sales@jebel-ali.example"),
        ("supp:al-furat", 10, 0.97m, "orders@al-furat.example"),
    ];

    private static readonly RoundingPolicy Rounding = RoundingPolicy.Default;

    public static async Task<DemoPurchasingOutcome> SeedAsync(IServiceProvider services, IReadOnlyList<(DemoCompany Definition, CompanySummary Company)> companies, DateOnly today, CancellationToken cancellationToken)
    {
        var unitOfWork = services.GetRequiredService<IUnitOfWorkAccessor>().Current;
        var tenant = unitOfWork.Context.TenantId.Value;
        var supplierService = services.GetRequiredService<SupplierService>();
        var orderService = services.GetRequiredService<PurchaseOrderService>();
        var receiptService = services.GetRequiredService<ReceiptService>();
        var invoiceService = services.GetRequiredService<InvoiceService>();
        var returnService = services.GetRequiredService<ReturnService>();
        var landedCostService = services.GetRequiredService<LandedCostService>();
        var rfqService = services.GetRequiredService<RfqService>();
        var requisitionService = services.GetRequiredService<RequisitionService>();
        var agreementService = services.GetRequiredService<BlanketAgreementService>();
        var bankAccountService = services.GetRequiredService<BankAccountService>();
        var paymentService = services.GetRequiredService<PaymentService>();
        var payables = services.GetRequiredService<IPayables>();

        var opening = new DateOnly(today.Year, today.Month, 1);
        DateOnly Day(int offset) => opening.AddDays(offset) < today ? opening.AddDays(offset) : today;
        var freight = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
            "SELECT id FROM app.pur_charge_types WHERE tenant_id = @tenant AND code = 'FREIGHT'", new { tenant }, unitOfWork.Transaction, cancellationToken: cancellationToken));
        var suppliers = Suppliers.Select(static s => DemoIds.For("party:" + s.Key)).ToArray();
        int orders = 0, receipts = 0, invoices = 0, payments = 0;

        foreach (var plan in Plans)
        {
            var companyId = companies.Single(c => c.Definition.Code == plan.Company).Company.Id;
            var currency = companies.Single(c => c.Definition.Code == plan.Company).Definition.FunctionalCurrency;
            var warehouseId = await unitOfWork.Connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "SELECT id FROM app.inv_warehouses WHERE tenant_id = @tenant AND company_id = @companyId AND code = @code", new { tenant, companyId, code = plan.Warehouse }, unitOfWork.Transaction, cancellationToken: cancellationToken));
            var items = (await unitOfWork.Connection.QueryAsync<Guid>(new CommandDefinition(
                "SELECT id FROM app.itm_items WHERE tenant_id = @tenant AND code LIKE @prefix AND tracking = 'none' ORDER BY code LIMIT 4", new { tenant, prefix = plan.Family + "-%" }, unitOfWork.Transaction, cancellationToken: cancellationToken))).ToArray();
            for (var s = 0; s < Suppliers.Length; s++)
            {
                Require(await supplierService.SaveAccountAsync(suppliers[s], companyId, new SaveSupplierAccountRequest(Currency: currency, LeadTimeDays: Suppliers[s].LeadTimeDays), cancellationToken));
            }

            // Prices rise about one percent a month; each supplier sits a little above or below the market.
            decimal Price(int item, int supplier, int monthsBack) =>
                Rounding.Round((plan.BasePrice + (plan.Step * item)) * Suppliers[supplier].PriceFactor * (1m + (0.01m * (12 - monthsBack))), plan.Decimals);

            SavePurchaseOrderLineRequest OrderLine(int item, int supplier, int monthsBack, decimal quantity) =>
                new(ItemId: items[item], Quantity: quantity, Uom: "PCS", UnitPrice: Price(item, supplier, monthsBack));

            // Eleven months of history: approved and closed orders, which carry prices and never post.
            for (var monthsBack = 11; monthsBack >= 1; monthsBack--)
            {
                var month = opening.AddMonths(-monthsBack);
                for (var s = 0; s < Suppliers.Length; s++)
                {
                    var orderDate = month.AddDays(3 + (s * 8));
                    var first = (monthsBack + s) % items.Length;
                    var order = Require(await orderService.CreateAsync(new SavePurchaseOrderRequest(companyId, suppliers[s],
                        [OrderLine(first, s, monthsBack, 40m + (10m * s)), OrderLine((first + 1) % items.Length, s, monthsBack, 24m + (6m * s))],
                        Currency: currency, OrderDate: orderDate, ExpectedDate: orderDate.AddDays(Suppliers[s].LeadTimeDays), WarehouseId: warehouseId), cancellationToken));
                    Require(await orderService.SubmitAsync(order.Id, cancellationToken));
                    Require(await orderService.CloseAsync(order.Id, cancellationToken));
                    orders++;
                }
            }

            async Task<(PurchaseOrderSummary Order, ReceiptSummary Receipt)> ReceiveAsync(int supplier, DateOnly ordered, DateOnly expected, DateOnly received, int[] lines, decimal quantity, decimal receivedQuantity)
            {
                var order = Require(await orderService.CreateAsync(new SavePurchaseOrderRequest(companyId, suppliers[supplier],
                    [.. lines.Select(i => OrderLine(i, supplier, 0, quantity))], Currency: currency, OrderDate: ordered, ExpectedDate: expected, WarehouseId: warehouseId), cancellationToken));
                order = Require(await orderService.SubmitAsync(order.Id, cancellationToken));
                orders++;
                var receipt = Require(await receiptService.CreateAsync(new SaveReceiptRequest(order.Id,
                    [.. order.Lines.Select(l => new SaveReceiptLineRequest(l.Id, receivedQuantity))], WarehouseId: warehouseId, PostingDate: received, SupplierDeliveryNote: "DN-" + order.Number), cancellationToken));
                receipt = Require(await receiptService.PostAsync(receipt.Id, cancellationToken));
                receipts++;
                return (order, receipt);
            }

            async Task<InvoiceSummary> InvoiceAsync(int supplier, ReceiptSummary receipt, PurchaseOrderSummary order, DateOnly date, string? reference = null)
            {
                var prices = order.Lines.ToDictionary(static l => l.Id, static l => l.UnitPrice);
                var invoice = Require(await invoiceService.CreateAsync(new SaveInvoiceRequest(companyId, suppliers[supplier],
                    [.. receipt.Lines.Select(l => new SaveInvoiceLineRequest("receipt", l.Quantity, prices[l.OrderLineId], ReceiptLineId: l.Id))],
                    SupplierInvoiceNumber: reference ?? $"{plan.Company}-{order.Number}", DocumentDate: date, PostingDate: date, Currency: currency), cancellationToken));
                Require(await invoiceService.SubmitAsync(invoice.Id, cancellationToken));
                invoices++;
                return Require(await invoiceService.PostAsync(invoice.Id, cancellationToken));
            }

            async Task<decimal> RemainingAsync(Guid invoiceId) =>
                (await payables.ItemsOfAsync(PurchaseDocumentTypes.Invoice, invoiceId, cancellationToken)).Sum(static i => i.RemainingTc);

            // The live month. On time: ordered before the month, received a day early, invoiced, freight landed two weeks on.
            var (onTimeOrder, onTimeReceipt) = await ReceiveAsync(0, opening.AddDays(-8), opening.AddDays(2), Day(1), [0, 1, 2], 100m, 100m);
            var onTimeInvoice = await InvoiceAsync(0, onTimeReceipt, onTimeOrder, Day(2));
            var landed = Require(await landedCostService.CreateAsync(new SaveLandedCostRequest(companyId,
                [new SaveLandedCostChargeRequest(freight, Rounding.Round(plan.BasePrice * 12m, plan.Decimals), PartnerId: suppliers[1], Description: "Inbound freight")],
                [.. onTimeReceipt.Lines.Select(static l => l.Id)], PostingDate: Day(15), Currency: currency, Reference: "FRT-" + onTimeOrder.Number), cancellationToken));
            Require(await landedCostService.PostAsync(landed.Id, cancellationToken));

            // Late: promised for the 2nd, received on the 7th; ten of the first item go back against a debit note.
            var (lateOrder, lateReceipt) = await ReceiveAsync(1, opening.AddDays(-5), opening.AddDays(1), Day(6), [1, 3], 60m, 60m);
            var lateInvoice = await InvoiceAsync(1, lateReceipt, lateOrder, Day(7));
            var returned = Require(await returnService.CreateAsync(new SaveReturnRequest(lateReceipt.Id,
                [new SaveReturnLineRequest(lateReceipt.Lines[0].Id, 10m, Reason: "Damaged in transit")], PostingDate: Day(8), Reason: "Damaged in transit", SupplierRma: "RMA-" + lateOrder.Number), cancellationToken));
            returned = Require(await returnService.PostAsync(returned.Id, cancellationToken));
            var returnPrice = lateOrder.Lines.Single(l => l.Id == lateReceipt.Lines[0].OrderLineId).UnitPrice;
            var debitNote = Require(await invoiceService.CreateAsync(new SaveInvoiceRequest(companyId, suppliers[1],
                [new SaveInvoiceLineRequest("return", 10m, returnPrice, ReturnLineId: returned.Lines[0].Id)], Kind: "debit_note",
                SupplierInvoiceNumber: $"CN-{plan.Company}-{lateOrder.Number}", DocumentDate: Day(8), PostingDate: Day(8), Currency: currency), cancellationToken));
            Require(await invoiceService.SubmitAsync(debitNote.Id, cancellationToken));
            Require(await invoiceService.PostAsync(debitNote.Id, cancellationToken));
            invoices++;

            // Still open: sixty of eighty received, invoiced, not yet paid.
            var (openOrder, openReceipt) = await ReceiveAsync(2, opening.AddDays(-3), opening.AddDays(4), Day(3), [2], 80m, 60m);
            await InvoiceAsync(2, openReceipt, openOrder, Day(4));

            // The bank account payments go out of: the on-time supplier paid in full, the late one in part.
            var bank = Require(await bankAccountService.SaveAsync(null, new SaveCompanyBankAccountRequest(companyId, "OPS",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = "Operating account", ["ar"] = "الحساب التشغيلي" }, "bank", currency, BankName: "Rafidain Bank"), cancellationToken));
            foreach (var (supplier, invoiceId, share, date) in new[] { (0, onTimeInvoice.Id, 1m, Day(10)), (1, lateInvoice.Id, 0.5m, Day(12)) })
            {
                var item = (await payables.ItemsOfAsync(PurchaseDocumentTypes.Invoice, invoiceId, cancellationToken))[0];
                var amount = Rounding.Round(await RemainingAsync(invoiceId) * share, plan.Decimals);
                var payment = Require(await paymentService.CreateAsync(new SavePaymentRequest(companyId, suppliers[supplier], bank.Id, PaymentDate: date, Reference: $"TT-{plan.Company}-{supplier + 1}",
                    Currency: currency, Lines: [new SavePaymentLineRequest(item.Id, amount)]), cancellationToken));
                Require(await paymentService.PostAsync(payment.Id, cancellationToken));
                payments++;
            }

            // An RFQ for next month's restock: three suppliers invited and quoted, waiting for comparison and award.
            var rfq = Require(await rfqService.CreateAsync(new SaveRfqRequest(companyId,
                [new SaveRfqLineRequest(ItemId: items[0], Quantity: 200m, Uom: "PCS"), new SaveRfqLineRequest(ItemId: items[3], Quantity: 120m, Uom: "PCS")],
                Title: "Restock " + opening.AddMonths(1).ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture), DueOn: today.AddDays(7), PartnerIds: suppliers), cancellationToken));
            if (rfq.Suppliers.Count == 0)
            {
                rfq = Require(await rfqService.InviteAsync(rfq.Id, new InviteSuppliersRequest(suppliers), cancellationToken));
            }

            rfq = Require(await rfqService.SendAsync(rfq.Id, new SendRfqRequest(Enumerable.Range(0, Suppliers.Length).ToDictionary(s => suppliers[s], s => Suppliers[s].Email)), cancellationToken));
            for (var s = 0; s < Suppliers.Length; s++)
            {
                Require(await rfqService.RecordQuoteAsync(rfq.Id, new SaveQuoteRequest(suppliers[s], currency,
                    [.. rfq.Lines.Select((l, i) => new SaveQuoteLineRequest(l.Id, Price(i == 0 ? 0 : 3, s, 0)))], SupplierReference: $"Q-{plan.Company}-{s + 1}",
                    ValidUntil: today.AddDays(30), LeadTimeDays: Suppliers[s].LeadTimeDays), cancellationToken));
            }

            // A shelf restock requested by the warehouse, ordered from the suggested supplier (an order dated today, as the
            // requisition service dates it) and delivered in two parts, each invoiced on its own.
            var requisition = Require(await requisitionService.CreateAsync(new SaveRequisitionRequest(companyId,
                [
                    new SaveRequisitionLineRequest(ItemId: items[0], Quantity: 50m, Uom: "PCS", EstimatedPrice: Price(0, 0, 0), WarehouseId: warehouseId, SuggestedSupplierId: suppliers[0]),
                    new SaveRequisitionLineRequest(ItemId: items[2], Quantity: 30m, Uom: "PCS", EstimatedPrice: Price(2, 0, 0), WarehouseId: warehouseId, SuggestedSupplierId: suppliers[0]),
                ],
                NeededBy: today.AddDays(10), Justification: "Shelf restock"), cancellationToken));
            Require(await requisitionService.SubmitAsync(requisition.Id, cancellationToken));
            var restock = Require(await requisitionService.CreateOrdersAsync(requisition.Id, new CreateOrdersFromRequisitionRequest(), cancellationToken)).Orders.Single();
            restock = Require(await orderService.SubmitAsync(restock.Id, cancellationToken));
            orders++;
            var firstDelivery = restock.Lines.ToDictionary(static l => l.Id, static l => decimal.Floor(l.Quantity * 0.6m));
            for (var delivery = 1; delivery <= 2; delivery++)
            {
                var part = delivery;
                var receipt = Require(await receiptService.CreateAsync(new SaveReceiptRequest(restock.Id,
                    [.. restock.Lines.Select(l => new SaveReceiptLineRequest(l.Id, part == 1 ? firstDelivery[l.Id] : l.Quantity - firstDelivery[l.Id]))],
                    WarehouseId: warehouseId, PostingDate: today, SupplierDeliveryNote: $"DN-{restock.Number}-{part}"), cancellationToken));
                receipt = Require(await receiptService.PostAsync(receipt.Id, cancellationToken));
                receipts++;
                await InvoiceAsync(0, receipt, restock, today, $"{plan.Company}-{restock.Number}-{part}");
            }

            // A half-year blanket agreement at a fixed price, and the first order released against it.
            var agreement = Require(await agreementService.CreateAsync(new SaveBlanketAgreementRequest(companyId, suppliers[2], opening, opening.AddMonths(6).AddDays(-1),
                [new SaveBlanketLineRequest(ItemId: items[1], AgreedQty: 600m, Uom: "PCS", AgreedPrice: Price(1, 2, 0))], Currency: currency, Notes: "Half-year supply at a fixed price"), cancellationToken));
            agreement = Require(await agreementService.SetStatusAsync(agreement.Id, "activate", cancellationToken));
            var release = Require(await orderService.CreateAsync(new SavePurchaseOrderRequest(companyId, suppliers[2],
                [new SavePurchaseOrderLineRequest(ItemId: items[1], Quantity: 100m, Uom: "PCS", UnitPrice: agreement.Lines[0].AgreedPrice, BlanketLineId: agreement.Lines[0].Id)],
                Currency: currency, OrderDate: opening, ExpectedDate: today.AddDays(Suppliers[2].LeadTimeDays), WarehouseId: warehouseId, AgreementId: agreement.Id), cancellationToken));
            Require(await orderService.SubmitAsync(release.Id, cancellationToken));
            orders++;

            // An invoice five percent above the order price: the match blocks it until someone overrides or corrects it.
            var (dearOrder, dearReceipt) = await ReceiveAsync(1, opening.AddDays(-2), opening.AddDays(5), Day(9), [3], 40m, 40m);
            var dearPrices = dearOrder.Lines.ToDictionary(static l => l.Id, static l => l.UnitPrice);
            var dear = Require(await invoiceService.CreateAsync(new SaveInvoiceRequest(companyId, suppliers[1],
                [.. dearReceipt.Lines.Select(l => new SaveInvoiceLineRequest("receipt", l.Quantity, Rounding.Round(dearPrices[l.OrderLineId] * 1.05m, plan.Decimals), ReceiptLineId: l.Id))],
                SupplierInvoiceNumber: $"{plan.Company}-{dearOrder.Number}", DocumentDate: Day(10), PostingDate: Day(10), Currency: currency), cancellationToken));
            Require(await invoiceService.SubmitAsync(dear.Id, cancellationToken));
            invoices++;

            // A requisition approved and waiting for a buyer to turn it into an order.
            var waiting = Require(await requisitionService.CreateAsync(new SaveRequisitionRequest(companyId,
                [new SaveRequisitionLineRequest(ItemId: items[3], Quantity: 24m, Uom: "PCS", EstimatedPrice: Price(3, 1, 0), WarehouseId: warehouseId, SuggestedSupplierId: suppliers[1])],
                NeededBy: today.AddDays(14), Justification: "Promotion next month"), cancellationToken));
            Require(await requisitionService.SubmitAsync(waiting.Id, cancellationToken));
        }

        return new DemoPurchasingOutcome(orders, receipts, invoices, payments);
    }

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
