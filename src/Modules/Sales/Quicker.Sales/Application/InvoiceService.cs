using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Collaboration.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Pricing.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Sales.Domain;
using Quicker.Sales.Persistence;
using Quicker.Tax.Contracts;

namespace Quicker.Sales.Application;

/// <summary>
/// Sales invoices and credit notes (roadmap 5.6a, POSTING_RULES §2 "Sales invoice" and "Credit note", A-159). An invoice
/// bills posted shipment lines (in part or in full, from one or several shipments and orders of the customer in one
/// currency), service lines of orders, non-stock items priced here by the pricing engine, and free service lines on an
/// account role; a credit note credits a posted invoice's lines or stands on its own (price or goodwill). Lines from
/// an order take the order's price, so the order is billed at what was agreed; each invoice of a line takes its share
/// of the line's net and the last one the exact remainder. Tax runs through the tax engine on the document date.
/// Posting books Dr receivables gross / Cr revenue net per line / Cr output tax per code, writes the tax ledger and
/// opens one receivable open item per payment-terms instalment, the open items' functional value allocated from the
/// receivables line actually booked so the control equals its items to the minor unit. A credit note that names the
/// invoice it credits is applied to that invoice's open items as soon as it posts. Reversal mirrors all of it.
/// </summary>
public sealed class InvoiceService(
    SalesDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IPartnerDirectory partners,
    ICustomerDirectory customers,
    IPricing pricing,
    ITaxDetermination tax,
    ITaxDirectory taxDirectory,
    ITaxLedger taxLedger,
    IPostingService posting,
    IReceivables receivables,
    INumberAllocator numbering,
    ICustomFieldValidator customFields,
    IDimensionSets dimensionSets,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock)
{
    public const string DocumentType = "sales_invoice";

    /// <summary>Credit notes are numbered in a series of their own (CN-), as tax regimes expect, though they live with the invoices.</summary>
    public const string CreditNoteNumberingType = "sales_credit_note";

    public static readonly IReadOnlyList<string> Kinds = ["invoice", "credit_note"];

    public static readonly IReadOnlyList<string> LineKinds = ["shipment", "order", "item", "service", "invoice"];

    private static readonly string[] OpenOrderStatuses = ["confirmed", "partially_shipped", "shipped"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<SalesInvoiceSummary>> ListAsync(Guid? companyId, string? status, string? kind, Guid? partnerId, CancellationToken cancellationToken)
    {
        var query = db.Invoices.AsNoTracking().Include(static i => i.Lines).AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(i => i.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(i => i.Kind == kind);
        }

        if (partnerId is { } p)
        {
            query = query.Where(i => i.PartnerId == p);
        }

        var invoices = await query.OrderByDescending(static i => i.PostingDate).ThenByDescending(static i => i.Number).Take(500).ToListAsync(cancellationToken);
        var result = new List<SalesInvoiceSummary>(invoices.Count);
        foreach (var invoice in invoices)
        {
            result.Add(await MapAsync(invoice, cancellationToken));
        }

        return result;
    }

    /// <summary>What a customer can still be invoiced: posted shipment lines with an uninvoiced quantity, and open service lines of its orders (delivered on the invoice).</summary>
    public async Task<IReadOnlyList<InvoicableSalesLine>> InvoicableAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken)
    {
        var result = new List<InvoicableSalesLine>();
        var shipmentRows = await (from l in db.ShipmentLines.AsNoTracking()
                                  join s in db.Shipments.AsNoTracking() on new { l.TenantId, Id = l.ShipmentId } equals new { s.TenantId, s.Id }
                                  join ol in db.OrderLines.AsNoTracking() on new { l.TenantId, Id = l.OrderLineId } equals new { ol.TenantId, ol.Id }
                                  join o in db.Orders.AsNoTracking() on new { s.TenantId, Id = s.OrderId } equals new { o.TenantId, o.Id }
                                  where s.CompanyId == companyId && s.PartnerId == partnerId && s.Status == "posted" && l.QtyInvoiced < l.Quantity
                                  orderby s.PostingDate, s.Number, l.LineNo
                                  select new { Line = l, Shipment = s, OrderLine = ol, Order = o }).ToListAsync(cancellationToken);
        foreach (var row in shipmentRows)
        {
            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            result.Add(new InvoicableSalesLine("shipment", row.Line.Id, row.Shipment.Number, row.Shipment.Id, row.OrderLine.Id, row.Order.Number, row.Order.Id, row.OrderLine.LineNo, row.Line.ItemId, item?.Code ?? string.Empty, item?.Name.Values ?? Empty(),
                item?.BaseUomId ?? row.Line.UomId, item?.BaseUomCode ?? string.Empty, row.Line.Quantity, row.Line.QtyInvoiced, row.Line.Quantity - row.Line.QtyInvoiced, BaseUnitPrice(row.OrderLine), row.OrderLine.DiscountPct, row.Order.Currency, row.Shipment.PostingDate));
        }

        var orderRows = await (from l in db.OrderLines.AsNoTracking()
                               join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id }
                               where o.CompanyId == companyId && o.PartnerId == partnerId && OpenOrderStatuses.Contains(o.Status) && l.Status != "cancelled" && l.QtyInvoiced + l.QtyCancelled < l.QuantityBase
                               orderby o.Number, l.LineNo
                               select new { Line = l, Order = o }).ToListAsync(cancellationToken);
        foreach (var row in orderRows)
        {
            var item = await items.FindAsync(row.Line.ItemId, cancellationToken);
            if (item is null || item.IsStockItem)
            {
                continue;
            }

            result.Add(new InvoicableSalesLine("order", null, null, null, row.Line.Id, row.Order.Number, row.Order.Id, row.Line.LineNo, row.Line.ItemId, item.Code, item.Name.Values, item.BaseUomId, item.BaseUomCode,
                row.Line.QuantityBase - row.Line.QtyCancelled, row.Line.QtyInvoiced, row.Line.QuantityBase - row.Line.QtyCancelled - row.Line.QtyInvoiced, BaseUnitPrice(row.Line), row.Line.DiscountPct, row.Order.Currency, null));
        }

        return result;
    }

    public async Task<Result<SalesInvoiceSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.AsNoTracking().Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        return invoice is null ? Error.NotFound(DocumentType, id) : await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result<SalesInvoiceSummary>> CreateAsync(SaveSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invoice = new SalesInvoice { Id = Guid.CreateVersion7(), CreatedBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        var applied = await ApplyAsync(invoice, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var company = (await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken))!;
        var isCredit = invoice.Kind == "credit_note";
        var numberingType = isCredit ? CreditNoteNumberingType : DocumentType;
        await numbering.EnsureDefaultSeriesAsync(numberingType, company.Id, isCredit ? "CN" : "INV", isCredit ? "CN-{yyyy}-{seq:5}" : "INV-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(numberingType, company.Id, null, invoice.DocumentDate, invoice.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        invoice.Number = number.Value.Text;
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Created, After: new { invoice.Kind, invoice.PartnerId, invoice.Currency, invoice.TotalGross, lines = invoice.Lines.Count }, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result<SalesInvoiceSummary>> UpdateAsync(Guid id, SaveSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invoice = await db.Invoices.Include(static i => i.Lines).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status != "draft")
        {
            return Error.Conflict("invoice.not_draft", "Only a draft invoice is edited; a posted one is credited or reversed.").WithWhy(("status", invoice.Status));
        }

        if (request.CompanyId != invoice.CompanyId || !string.Equals(request.Kind, invoice.Kind, StringComparison.Ordinal))
        {
            return Error.Validation("invoice.identity_fixed", "An invoice keeps its company and kind.").WithWhy(("companyId", invoice.CompanyId), ("kind", invoice.Kind));
        }

        db.InvoiceLines.RemoveRange(invoice.Lines);
        invoice.Lines.Clear();
        var applied = await ApplyAsync(invoice, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Updated, After: new { invoice.TotalGross, lines = invoice.Lines.Count }, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    /// <summary>A draft that will never post is cancelled, keeping its number (a gapless series shows the cancelled document rather than a hole).</summary>
    public async Task<Result<SalesInvoiceSummary>> CancelAsync(Guid id, CancelSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("invoice.reason_required", "A cancellation names its reason.");
        }

        var invoice = await db.Invoices.Include(static i => i.Lines).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status != "draft")
        {
            return Error.Conflict("invoice.not_draft", "Only a draft invoice is cancelled; a posted one is credited or reversed.").WithWhy(("status", invoice.Status));
        }

        invoice.Status = "cancelled";
        invoice.ReversalReason = reason;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.StateChanged, After: new { status = invoice.Status }, Reason: reason, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    public async Task<Result<SalesInvoiceSummary>> PostAsync(Guid id, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status != "draft")
        {
            return Error.Conflict("invoice.not_draft", "Only a draft invoice is posted.").WithWhy(("status", invoice.Status));
        }

        // Re-validated and re-taxed as it stands now: what another invoice billed since the draft was saved changes what is
        // left of a line, and the last invoice of a line takes the exact remainder.
        var kept = invoice.Lines.ToDictionary(static l => l.LineNo, static l => l.Id);
        var request = Request(invoice);
        db.InvoiceLines.RemoveRange(invoice.Lines);
        invoice.Lines.Clear();
        var applied = await ApplyAsync(invoice, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        foreach (var line in invoice.Lines)
        {
            if (kept.TryGetValue(line.LineNo, out var lineId))
            {
                line.Id = lineId;
            }
        }

        var company = (await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken))!;
        var customer = await customers.EnsureCustomerAsync(invoice.CompanyId, invoice.PartnerId, CustomerPurposes.Invoice, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error!;
        }

        var rate = await RateAsync(company, invoice.Currency, invoice.PostingDate, cancellationToken);
        if (rate.IsFailure)
        {
            return rate.Error!;
        }

        invoice.ExchangeRate = rate.Value;
        var sign = invoice.Kind == "credit_note" ? -1m : 1m;
        var sameCurrency = string.Equals(invoice.Currency, company.FunctionalCurrency.Code, StringComparison.Ordinal);

        // 1. The journal in the invoice currency: revenue per line with the line's dimensions, output tax per code, receivables gross.
        var postingLines = new List<PostingLine>();
        var dimensionsBySet = new Dictionary<Guid, IReadOnlyDictionary<string, Guid>?>();
        foreach (var line in invoice.Lines)
        {
            IReadOnlyDictionary<string, Guid>? dimensions = null;
            if (line.DimensionSetId is { } setId)
            {
                if (!dimensionsBySet.TryGetValue(setId, out dimensions))
                {
                    dimensions = await dimensionSets.GetAsync(setId, cancellationToken);
                    dimensionsBySet[setId] = dimensions;
                }
            }

            var item = line.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : null;
            postingLines.Add(new PostingLine(line.AccountRole ?? AccountRoles.Revenue, -sign * line.NetAmount, new PostingKeys(DocumentType, item?.ItemPostingGroupId, customer.Value.PostingGroupId), Dimensions: dimensions, PartnerId: invoice.PartnerId,
                Description: line.Description is null ? null : LocalizedText.Bilingual(line.Description, line.Description)));
        }

        // Tax per code (POSTING_RULES §2): output tax the customer is charged; tax the customer accounts for itself (reverse
        // charge) is reported, never booked. Each line's tax in the company's currency is rounded once, and the journal takes
        // their sum, so the tax ledger ties to it.
        var taxFc = invoice.Lines.Where(static l => l.TaxCodeId is not null).ToDictionary(static l => l.Id, l => sameCurrency ? l.TaxAmount : Shared.Round(l.TaxAmount * rate.Value, company.FunctionalCurrency));
        foreach (var byCode in invoice.Lines.Where(static l => l.TaxCodeId is not null && l.TaxAmount != 0m && !l.TaxReverseCharge).GroupBy(static l => l.TaxCodeId!.Value).OrderBy(static g => g.Key))
        {
            var code = await tax.FindCodeAsync(byCode.Key, invoice.DocumentDate, cancellationToken);
            if (code.IsFailure)
            {
                return code.Error!;
            }

            var amount = byCode.Sum(static l => l.TaxAmount);
            var amountFc = byCode.Sum(l => taxFc[l.Id]);
            postingLines.Add(new PostingLine(code.Value.OutputAccountRole, -sign * amount, new PostingKeys(DocumentType), PartnerId: invoice.PartnerId, TaxCodeId: byCode.Key, TaxBase: sign * byCode.Sum(static l => l.NetAmount), AmountFc: sameCurrency ? null : -sign * amountFc));
        }

        postingLines.Add(new PostingLine(AccountRoles.AR, sign * invoice.TotalGross, new PostingKeys(DocumentType, PartnerPostingGroupId: customer.Value.PostingGroupId), SubledgerType: SubledgerTypes.Receivables, SubledgerRef: invoice.Id, PartnerId: invoice.PartnerId, DueDate: invoice.DueDate));
        var reference = invoice.CustomerReference is null ? string.Empty : $" ({invoice.CustomerReference})";
        var description = invoice.Kind == "credit_note"
            ? LocalizedText.Bilingual($"Credit note {invoice.Number}{reference}", $"إشعار دائن {invoice.Number}{reference}")
            : LocalizedText.Bilingual($"Sales invoice {invoice.Number}{reference}", $"فاتورة مبيعات {invoice.Number}{reference}");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "sales", DocumentType, invoice.Id, invoice.PostingDate, invoice.Currency, postingLines, invoice.Number, invoice.DocumentDate, description,
            invoice.BranchId is { } b ? new BranchId(b) : null, RateTypes.Spot, sameCurrency ? null : rate.Value, sameCurrency ? null : "Invoice rate", $"sales_invoice:{invoice.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        // The tax ledger: one entry per taxed line, refused when the posting date falls in a filed return period.
        var taxEntries = invoice.Lines.Where(static l => l.TaxCodeId is not null)
            .Select(l => new TaxEntryRequest(l.TaxCodeId!.Value, TaxDirections.Sales, l.TaxRatePct, sign * l.NetAmount, sign * l.TaxAmount,
                sign * (sameCurrency ? l.NetAmount : Shared.Round(l.NetAmount * rate.Value, company.FunctionalCurrency)), sign * taxFc[l.Id], l.TaxReverseCharge, l.TaxRecoverable, l.Id))
            .ToList();
        var recorded = await taxLedger.RecordAsync(new TaxPostingRequest(invoice.CompanyId, invoice.PostingDate, invoice.DocumentDate, "sales", DocumentType, invoice.Id, invoice.Number, posted.Value.EntryId, invoice.PartnerId, invoice.Currency, taxEntries), cancellationToken);
        if (recorded.IsFailure)
        {
            return recorded.Error!;
        }

        // 2. What this invoice billed, on the shipment and order lines it bills and the invoice lines it credits.
        var shipmentLineIds = invoice.Lines.Where(static l => l.ShipmentLineId is not null).Select(static l => l.ShipmentLineId!.Value).Distinct().ToList();
        var shipmentLines = await db.ShipmentLines.Where(l => shipmentLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var orderLineIds = invoice.Lines.Where(static l => l.OrderLineId is not null).Select(static l => l.OrderLineId!.Value).Distinct().ToList();
        var orderLines = await db.OrderLines.Where(l => orderLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var creditedLineIds = invoice.Lines.Where(static l => l.InvoiceLineId is not null).Select(static l => l.InvoiceLineId!.Value).Distinct().ToList();
        var creditedLines = await db.InvoiceLines.Where(l => creditedLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        foreach (var line in invoice.Lines)
        {
            line.NetAmountFc = Shared.Round(line.NetAmount * rate.Value, company.FunctionalCurrency);
            switch (line.Kind)
            {
                case "shipment":
                    shipmentLines[line.ShipmentLineId!.Value].QtyInvoiced += line.QuantityBase;
                    orderLines[line.OrderLineId!.Value].QtyInvoiced += line.QuantityBase;
                    orderLines[line.OrderLineId!.Value].NetInvoiced += line.NetAmount;
                    break;
                case "order":
                    orderLines[line.OrderLineId!.Value].QtyInvoiced += line.QuantityBase;
                    orderLines[line.OrderLineId!.Value].NetInvoiced += line.NetAmount;
                    break;
                case "invoice":
                    creditedLines[line.InvoiceLineId!.Value].QtyCredited += line.Quantity;
                    creditedLines[line.InvoiceLineId!.Value].NetCredited += line.NetAmount;
                    break;
                default:
                    break;
            }
        }

        // 3. Receivables: one open item per instalment of the payment terms, their functional value allocated from the
        // receivables line actually booked so the control equals its items to the minor unit.
        var instalments = await InstalmentsAsync(invoice, customer.Value, cancellationToken);
        if (instalments.IsFailure)
        {
            return instalments.Error!;
        }

        var bookedFc = posted.Value.Lines.Where(l => l.SubledgerType == SubledgerTypes.Receivables && l.SubledgerRef == invoice.Id).Sum(static l => l.DebitFc - l.CreditFc);
        var shares = instalments.Value.Count == 0 || instalments.Value.Sum(static i => i.Amount) == 0m
            ? instalments.Value.Select(_ => new Money(0m, company.FunctionalCurrency)).ToList()
            : RoundingPolicy.Default.Allocate(new Money(bookedFc, company.FunctionalCurrency), instalments.Value.Select(static i => i.Amount).ToList());
        var openItems = new List<ReceivableInfo>();
        for (var i = 0; i < instalments.Value.Count; i++)
        {
            var instalment = instalments.Value[i];
            openItems.Add(await receivables.OpenAsync(new NewReceivable(invoice.CompanyId, invoice.PartnerId, invoice.Kind == "credit_note" ? ReceivableKinds.CreditNote : ReceivableKinds.Invoice, DocumentType, invoice.Id, invoice.Number, i + 1, invoice.CustomerReference,
                invoice.PostingDate, invoice.DocumentDate, instalment.DueOn, instalment.DiscountUntil, instalment.DiscountPct, invoice.Currency, sign * instalment.Amount, shares[i].Amount, rate.Value, posted.Value.EntryId, invoice.BranchId, null, invoice.SalesRepId), cancellationToken));
        }

        invoice.DueDate = instalments.Value.Count > 0 ? instalments.Value[^1].DueOn : invoice.DocumentDate;
        invoice.JournalEntryId = posted.Value.EntryId;
        invoice.Status = "posted";
        invoice.PostedAt = clock.UtcNow;
        invoice.PostedBy = principal.Principal?.MembershipId.Value;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // 4. A credit note naming the invoice it credits is applied to that invoice's open items at once, oldest due first.
        var applications = 0;
        if (invoice.Kind == "credit_note" && invoice.CreditsInvoiceId is { } creditedId && openItems.Count > 0)
        {
            var credit = openItems[0];
            var remaining = -credit.RemainingTc;
            foreach (var target in (await receivables.ItemsOfAsync(DocumentType, creditedId, cancellationToken)).Where(static t => t.RemainingTc > 0m && !t.OnHold).OrderBy(static t => t.DueDate).ThenBy(static t => t.Instalment))
            {
                if (remaining <= 0m)
                {
                    break;
                }

                var amount = Math.Min(remaining, target.RemainingTc);
                var appliedCredit = await receivables.ApplyAsync(new ApplyReceivableRequest(credit.Id, target.Id, amount, invoice.PostingDate, $"Credit note {invoice.Number}"), cancellationToken);
                if (appliedCredit.IsFailure)
                {
                    return appliedCredit.Error!;
                }

                remaining -= amount;
                applications++;
            }
        }

        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Posted, After: new { status = invoice.Status, journal = posted.Value.Number, invoice.TotalNet, invoice.TotalTax, invoice.TotalGross, invoice.Currency, instalments = openItems.Count, applications }, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    /// <summary>Reverses a posted invoice: the journal and tax ledger mirrored, the open items closed, the billed quantities given back.</summary>
    public async Task<Result<SalesInvoiceSummary>> ReverseAsync(Guid id, ReverseSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = Shared.Trim(request.Reason);
        if (reason is null)
        {
            return Error.Validation("invoice.reason_required", "A reversal names its reason.");
        }

        var invoice = await db.Invoices.Include(static i => i.Lines.OrderBy(static l => l.LineNo)).SingleOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return Error.NotFound(DocumentType, id);
        }

        if (invoice.Status != "posted")
        {
            return Error.Conflict("invoice.not_posted", "Only a posted invoice is reversed.").WithWhy(("status", invoice.Status));
        }

        var company = await companies.FindAsync(new CompanyId(invoice.CompanyId), cancellationToken);
        var reversalDate = request.ReversalDate ?? clock.TodayIn(company?.TimeZone ?? "UTC");
        var itemsReversed = await receivables.ReverseDocumentAsync(DocumentType, invoice.Id, reversalDate, cancellationToken);
        if (itemsReversed.IsFailure)
        {
            return itemsReversed.Error!.Code == "receivables.item_settled"
                ? Error.Conflict("invoice.settled", "The invoice has been paid or credited in part; reverse those settlements first.").WithWhy(("items", itemsReversed.Error!.Why))
                : itemsReversed.Error!;
        }

        var reversed = await posting.ReverseAsync(invoice.JournalEntryId!.Value, reversalDate, reason, false, cancellationToken);
        if (reversed.IsFailure)
        {
            return reversed.Error!;
        }

        var taxReversed = await taxLedger.ReverseAsync(DocumentType, invoice.Id, reversed.Value.PostingDate, reversed.Value.EntryId, cancellationToken);
        if (taxReversed.IsFailure)
        {
            return taxReversed.Error!;
        }

        var shipmentLineIds = invoice.Lines.Where(static l => l.ShipmentLineId is not null).Select(static l => l.ShipmentLineId!.Value).Distinct().ToList();
        var shipmentLines = await db.ShipmentLines.Where(l => shipmentLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var orderLineIds = invoice.Lines.Where(static l => l.OrderLineId is not null).Select(static l => l.OrderLineId!.Value).Distinct().ToList();
        var orderLines = await db.OrderLines.Where(l => orderLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var creditedLineIds = invoice.Lines.Where(static l => l.InvoiceLineId is not null).Select(static l => l.InvoiceLineId!.Value).Distinct().ToList();
        var creditedLines = await db.InvoiceLines.Where(l => creditedLineIds.Contains(l.Id)).ToDictionaryAsync(static l => l.Id, cancellationToken);
        foreach (var line in invoice.Lines)
        {
            switch (line.Kind)
            {
                case "shipment":
                    shipmentLines[line.ShipmentLineId!.Value].QtyInvoiced -= line.QuantityBase;
                    orderLines[line.OrderLineId!.Value].QtyInvoiced -= line.QuantityBase;
                    orderLines[line.OrderLineId!.Value].NetInvoiced -= line.NetAmount;
                    break;
                case "order":
                    orderLines[line.OrderLineId!.Value].QtyInvoiced -= line.QuantityBase;
                    orderLines[line.OrderLineId!.Value].NetInvoiced -= line.NetAmount;
                    break;
                case "invoice":
                    creditedLines[line.InvoiceLineId!.Value].QtyCredited -= line.Quantity;
                    creditedLines[line.InvoiceLineId!.Value].NetCredited -= line.NetAmount;
                    break;
                default:
                    break;
            }
        }

        invoice.Status = "reversed";
        invoice.ReversalEntryId = reversed.Value.EntryId;
        invoice.ReversalReason = reason;
        invoice.ReversedAt = clock.UtcNow;
        invoice.ReversedBy = principal.Principal?.MembershipId.Value;
        invoice.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, invoice.Id, invoice.Number, AuditActions.Reversed, After: new { status = invoice.Status, reversalDate, journal = reversed.Value.Number }, Reason: reason, CompanyId: invoice.CompanyId), cancellationToken);
        return await MapAsync(invoice, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private sealed record Instalment(DateOnly DueOn, decimal Amount, DateOnly? DiscountUntil, decimal DiscountPct);

    private sealed record Priced(decimal Net, decimal DiscountAmount, decimal UnitPrice, decimal QuantityBase, Guid? UomId, Guid? ItemId, Guid? VariantId, Guid? TaxCodeId, Guid? DimensionSetId, string? AccountRole, string Breakdown);

    private async Task<Result> ApplyAsync(SalesInvoice invoice, SaveSalesInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (!Kinds.Contains(request.Kind, StringComparer.Ordinal))
        {
            return Error.Validation("invoice.kind_invalid", "The kind is invoice or credit_note.").WithWhy(("kind", request.Kind));
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("invoice.lines_required", "An invoice has at least one line.");
        }

        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("invoice.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        var customer = await customers.EnsureCustomerAsync(request.CompanyId, request.PartnerId, CustomerPurposes.Invoice, cancellationToken);
        if (customer.IsFailure)
        {
            return customer.Error!;
        }

        var custom = await customFields.ValidateAsync(DocumentType, request.CustomFields, cancellationToken);
        if (custom.IsFailure)
        {
            return custom.Error!;
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, customer.Value.Currency, "invoice", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        var documentDate = request.DocumentDate ?? clock.TodayIn(company.TimeZone);
        var postingDate = request.PostingDate ?? documentDate;
        var isCredit = request.Kind == "credit_note";
        SalesInvoice? credited = null;
        if (request.CreditsInvoiceId is { } creditsId)
        {
            if (!isCredit)
            {
                return Error.Validation("invoice.credits_invoice_on_invoice", "Only a credit note names the invoice it credits.");
            }

            credited = await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == creditsId, cancellationToken);
            if (credited is null || credited.CompanyId != request.CompanyId || credited.PartnerId != request.PartnerId || credited.Kind != "invoice")
            {
                return Error.Validation("invoice.credited_invoice_unknown", "The credited invoice does not belong to this customer and company.").WithWhy(("creditsInvoiceId", creditsId));
            }

            if (credited.Status != "posted")
            {
                return Error.Conflict("invoice.credited_invoice_not_posted", "Only a posted invoice is credited.").WithWhy(("invoice", credited.Number), ("status", credited.Status));
            }

            if (!string.Equals(credited.Currency, currency.Value.Code, StringComparison.Ordinal))
            {
                return Error.Validation("invoice.currency_mismatch", "A credit note is in the currency of the invoice it credits.").WithWhy(("invoiceCurrency", credited.Currency), ("creditNoteCurrency", currency.Value.Code));
            }
        }

        // Every line resolved and priced before anything changes. Lines of orders and shipments take the order's price;
        // item lines are priced by the pricing engine together; service lines are what they say.
        var lines = new List<SalesInvoiceLine>();
        var priced = new Dictionary<int, Priced>();
        var pricingLines = new List<PricingLineRequest>();
        var pricingItems = new Dictionary<string, (ItemInfo Item, SaveSalesInvoiceLineRequest Request, int LineNo)>(StringComparer.Ordinal);
        var lineNo = 0;
        foreach (var l in request.Lines)
        {
            lineNo++;
            if (!LineKinds.Contains(l.Kind, StringComparer.Ordinal))
            {
                return Error.Validation("invoice.line_kind_invalid", "A line is against a shipment, an order, an item, a service account or an invoice line.").WithWhy(("lineNo", lineNo), ("kind", l.Kind));
            }

            if (isCredit && l.Kind is "shipment" or "order")
            {
                return Error.Validation("invoice.credit_note_lines_only", "A credit note credits invoice lines, items or service accounts, never shipments or orders.").WithWhy(("lineNo", lineNo), ("kind", l.Kind));
            }

            if (!isCredit && l.Kind == "invoice")
            {
                return Error.Validation("invoice.credit_needs_credit_note", "An invoice line is credited on a credit note.").WithWhy(("lineNo", lineNo));
            }

            if (l.Quantity <= 0m || l.DiscountPct is < 0m or > 100m || l.UnitPrice is < 0m)
            {
                return Error.Validation("invoice.line_invalid", "Quantities are positive, prices not negative and discounts between 0 and 100.").WithWhy(("lineNo", lineNo));
            }

            var line = new SalesInvoiceLine { Id = Guid.CreateVersion7(), TenantId = invoice.TenantId, InvoiceId = invoice.Id, LineNo = lineNo, Kind = l.Kind, Quantity = l.Quantity, Description = Shared.Trim(l.Description), CreatedAt = clock.UtcNow };
            var explicitDimensions = l.Dimensions is { Count: > 0 };
            var dimensionSet = await Shared.DimensionSetAsync(dimensionSets, l.Dimensions, lineNo, cancellationToken);
            if (dimensionSet.IsFailure)
            {
                return dimensionSet.Error!;
            }

            switch (l.Kind)
            {
                case "shipment":
                    {
                        if (l.ShipmentLineId is null)
                        {
                            return Error.Validation("invoice.shipment_line_required", "A shipment line names the shipment line it invoices.").WithWhy(("lineNo", lineNo));
                        }

                        var shipmentLine = await db.ShipmentLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.ShipmentLineId, cancellationToken);
                        var shipment = shipmentLine is null ? null : await db.Shipments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shipmentLine.ShipmentId, cancellationToken);
                        if (shipmentLine is null || shipment is null || shipment.CompanyId != request.CompanyId || shipment.PartnerId != request.PartnerId)
                        {
                            return Error.Validation("invoice.shipment_line_unknown", "The shipment line does not belong to this customer and company.").WithWhy(("lineNo", lineNo), ("shipmentLineId", l.ShipmentLineId));
                        }

                        if (shipment.Status != "posted")
                        {
                            return Error.Conflict("invoice.shipment_not_posted", "Only posted shipments are invoiced.").WithWhy(("lineNo", lineNo), ("shipment", shipment.Number), ("status", shipment.Status));
                        }

                        var orderLine = await db.OrderLines.AsNoTracking().SingleAsync(x => x.Id == shipmentLine.OrderLineId, cancellationToken);
                        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderLine.OrderId, cancellationToken);
                        if (!string.Equals(order.Currency, currency.Value.Code, StringComparison.Ordinal))
                        {
                            return Error.Validation("invoice.currency_mismatch", "A line of an order is invoiced in the order's currency.").WithWhy(("lineNo", lineNo), ("orderCurrency", order.Currency), ("invoiceCurrency", currency.Value.Code));
                        }

                        var alreadyHere = lines.Where(x => x.ShipmentLineId == shipmentLine.Id).Sum(static x => x.QuantityBase);
                        var remaining = shipmentLine.Quantity - shipmentLine.QtyInvoiced - alreadyHere;
                        if (l.Quantity > remaining)
                        {
                            return Error.Validation("invoice.quantity_exceeds_uninvoiced", "The quantity exceeds what the shipment line has left to invoice.").WithWhy(("lineNo", lineNo), ("shipment", shipment.Number), ("remaining", remaining));
                        }

                        line.ShipmentLineId = shipmentLine.Id;
                        line.OrderLineId = orderLine.Id;
                        line.ItemId = orderLine.ItemId;
                        line.VariantId = orderLine.VariantId;
                        line.Description ??= orderLine.Description;
                        priced[lineNo] = ShareOfOrderLine(orderLine, l.Quantity, lines.Where(x => x.OrderLineId == orderLine.Id).Sum(static x => x.NetAmount), lines.Where(x => x.OrderLineId == orderLine.Id).Sum(static x => x.QuantityBase), currency.Value, explicitDimensions ? dimensionSet.Value : orderLine.DimensionSetId);
                        break;
                    }

                case "order":
                    {
                        if (l.OrderLineId is null)
                        {
                            return Error.Validation("invoice.order_line_required", "An order line names the order line it invoices.").WithWhy(("lineNo", lineNo));
                        }

                        var orderLine = await db.OrderLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.OrderLineId, cancellationToken);
                        var order = orderLine is null ? null : await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderLine.OrderId, cancellationToken);
                        if (orderLine is null || order is null || order.CompanyId != request.CompanyId || order.PartnerId != request.PartnerId)
                        {
                            return Error.Validation("invoice.order_line_unknown", "The order line does not belong to this customer and company.").WithWhy(("lineNo", lineNo), ("orderLineId", l.OrderLineId));
                        }

                        var item = await items.FindAsync(orderLine.ItemId, cancellationToken);
                        if (item is null || item.IsStockItem)
                        {
                            return Error.Validation("invoice.stock_line_needs_shipment", "A stock item is invoiced against its shipment.").WithWhy(("lineNo", lineNo), ("item", item?.Code));
                        }

                        if (!OpenOrderStatuses.Contains(order.Status, StringComparer.Ordinal) || orderLine.Status == "cancelled")
                        {
                            return Error.Conflict("invoice.order_not_open", "The order is not open for invoicing.").WithWhy(("lineNo", lineNo), ("order", order.Number), ("status", order.Status));
                        }

                        if (!string.Equals(order.Currency, currency.Value.Code, StringComparison.Ordinal))
                        {
                            return Error.Validation("invoice.currency_mismatch", "A line of an order is invoiced in the order's currency.").WithWhy(("lineNo", lineNo), ("orderCurrency", order.Currency), ("invoiceCurrency", currency.Value.Code));
                        }

                        var alreadyHere = lines.Where(x => x.OrderLineId == orderLine.Id).Sum(static x => x.QuantityBase);
                        var remaining = orderLine.QuantityBase - orderLine.QtyCancelled - orderLine.QtyInvoiced - alreadyHere;
                        if (l.Quantity > remaining)
                        {
                            return Error.Validation("invoice.quantity_exceeds_uninvoiced", "The quantity exceeds what the order line has left to invoice.").WithWhy(("lineNo", lineNo), ("order", order.Number), ("remaining", remaining));
                        }

                        line.OrderLineId = orderLine.Id;
                        line.ItemId = orderLine.ItemId;
                        line.VariantId = orderLine.VariantId;
                        line.Description ??= orderLine.Description;
                        priced[lineNo] = ShareOfOrderLine(orderLine, l.Quantity, lines.Where(x => x.OrderLineId == orderLine.Id).Sum(static x => x.NetAmount), alreadyHere, currency.Value, explicitDimensions ? dimensionSet.Value : orderLine.DimensionSetId);
                        break;
                    }

                case "invoice":
                    {
                        if (l.InvoiceLineId is null)
                        {
                            return Error.Validation("invoice.invoice_line_required", "A credit line names the invoice line it credits.").WithWhy(("lineNo", lineNo));
                        }

                        var creditedLine = await db.InvoiceLines.AsNoTracking().SingleOrDefaultAsync(x => x.Id == l.InvoiceLineId, cancellationToken);
                        var creditedInvoice = creditedLine is null ? null : await db.Invoices.AsNoTracking().SingleOrDefaultAsync(i => i.Id == creditedLine.InvoiceId, cancellationToken);
                        if (creditedLine is null || creditedInvoice is null || creditedInvoice.CompanyId != request.CompanyId || creditedInvoice.PartnerId != request.PartnerId || creditedInvoice.Kind != "invoice")
                        {
                            return Error.Validation("invoice.invoice_line_unknown", "The invoice line does not belong to this customer and company.").WithWhy(("lineNo", lineNo), ("invoiceLineId", l.InvoiceLineId));
                        }

                        if (creditedInvoice.Status != "posted")
                        {
                            return Error.Conflict("invoice.credited_invoice_not_posted", "Only a posted invoice is credited.").WithWhy(("lineNo", lineNo), ("invoice", creditedInvoice.Number), ("status", creditedInvoice.Status));
                        }

                        if (credited is not null && credited.Id != creditedInvoice.Id)
                        {
                            return Error.Validation("invoice.credited_invoice_mismatch", "Every credited line belongs to the invoice the credit note names.").WithWhy(("lineNo", lineNo), ("invoice", creditedInvoice.Number), ("creditsInvoice", credited.Number));
                        }

                        if (!string.Equals(creditedInvoice.Currency, currency.Value.Code, StringComparison.Ordinal))
                        {
                            return Error.Validation("invoice.currency_mismatch", "A credit note is in the currency of the invoice it credits.").WithWhy(("lineNo", lineNo), ("invoiceCurrency", creditedInvoice.Currency), ("creditNoteCurrency", currency.Value.Code));
                        }

                        var alreadyHere = lines.Where(x => x.InvoiceLineId == creditedLine.Id).ToList();
                        var remaining = creditedLine.Quantity - creditedLine.QtyCredited - alreadyHere.Sum(static x => x.Quantity);
                        if (l.Quantity > remaining)
                        {
                            return Error.Validation("invoice.quantity_exceeds_invoiced", "The quantity exceeds what the invoice line has left to credit.").WithWhy(("lineNo", lineNo), ("invoice", creditedInvoice.Number), ("remaining", remaining));
                        }

                        credited ??= creditedInvoice;
                        line.InvoiceLineId = creditedLine.Id;
                        line.ItemId = creditedLine.ItemId;
                        line.VariantId = creditedLine.VariantId;
                        line.Description ??= creditedLine.Description;
                        var completes = l.Quantity == remaining;
                        var net = completes ? creditedLine.NetAmount - creditedLine.NetCredited - alreadyHere.Sum(static x => x.NetAmount) : Shared.Round(creditedLine.NetAmount * l.Quantity / creditedLine.Quantity, currency.Value);
                        var discount = completes ? creditedLine.DiscountAmount : Shared.Round(creditedLine.DiscountAmount * l.Quantity / creditedLine.Quantity, currency.Value);
                        var baseQuantity = creditedLine.Quantity == 0m ? 0m : creditedLine.QuantityBase * l.Quantity / creditedLine.Quantity;
                        priced[lineNo] = new Priced(net, discount, creditedLine.UnitPrice, baseQuantity, creditedLine.UomId, creditedLine.ItemId, creditedLine.VariantId, l.TaxCodeId ?? creditedLine.TaxCodeId, explicitDimensions ? dimensionSet.Value : creditedLine.DimensionSetId, creditedLine.AccountRole, "[]");
                        line.DiscountPct = creditedLine.DiscountPct;
                        break;
                    }

                case "item":
                    {
                        var resolved = await Shared.ResolveLineAsync(items, "invoice", l.ItemId, l.ItemCode, l.Quantity, l.Uom, l.UomId, cancellationToken);
                        if (resolved.IsFailure)
                        {
                            return resolved.Error!.WithWhy(("lineNo", lineNo));
                        }

                        var (item, unit, _) = resolved.Value;
                        if (item.IsStockItem)
                        {
                            return Error.Validation("invoice.stock_item_needs_shipment", "A stock item is invoiced from its shipment, so the stock moves with the invoice.").WithWhy(("lineNo", lineNo), ("item", item.Code));
                        }

                        if (l.VariantId is { } variantId)
                        {
                            var variant = await items.FindVariantAsync(variantId, cancellationToken);
                            if (variant is null || variant.ItemId != item.Id)
                            {
                                return Error.Validation("invoice.variant_invalid", "The variant must belong to the item.").WithWhy(("lineNo", lineNo));
                            }
                        }

                        var key = lineNo.ToString(CultureInfo.InvariantCulture);
                        pricingLines.Add(new PricingLineRequest(key, item.Id, l.VariantId, unit.UomId, l.Quantity, l.UnitPrice, l.DiscountPct > 0m ? l.DiscountPct : null));
                        pricingItems[key] = (item, l, lineNo);
                        line.ItemId = item.Id;
                        line.VariantId = l.VariantId;
                        line.DiscountPct = l.DiscountPct;
                        line.DimensionSetId = dimensionSet.Value;
                        break;
                    }

                default:
                    {
                        if (l.UnitPrice is not { } unitPrice)
                        {
                            return Error.Validation("invoice.unit_price_required", "A service line names its price.").WithWhy(("lineNo", lineNo));
                        }

                        if (line.Description is null)
                        {
                            return Error.Validation("invoice.description_required", "A service line says what it is for.").WithWhy(("lineNo", lineNo));
                        }

                        var gross = l.Quantity * unitPrice;
                        var net = Shared.Round(gross * (1m - l.DiscountPct / 100m), currency.Value);
                        priced[lineNo] = new Priced(net, Shared.Round(gross, currency.Value) - net, unitPrice, 0m, null, null, null, l.TaxCodeId, dimensionSet.Value, Shared.Trim(l.AccountRole) ?? AccountRoles.Revenue, "[]");
                        line.DiscountPct = l.DiscountPct;
                        break;
                    }
            }

            // Shipment and order lines are provisionally added so later lines of the same source see what this one takes.
            if (priced.TryGetValue(lineNo, out var p))
            {
                Fill(line, p);
            }

            lines.Add(line);
        }

        var pricesIncludeTax = false;
        if (pricingLines.Count > 0)
        {
            var pricingDate = documentDate;
            var result = await pricing.PriceAsync(new PricingRequest(company.Id.Value, request.PartnerId, currency.Value.Code, pricingDate, pricingLines, request.PriceListId), cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            if (result.Value.UnpricedLines > 0)
            {
                var problem = result.Value.Lines.First(static l => l.Problem is not null).Problem!;
                return Error.Validation(problem.Code, problem.Message).WithWhy(("unpricedLines", result.Value.UnpricedLines));
            }

            pricesIncludeTax = result.Value.PricesIncludeTax ?? false;
            if (pricesIncludeTax && lines.Any(static l => l.Kind != "item"))
            {
                return Error.Validation("invoice.mixed_tax_basis", "Items priced with tax included are invoiced apart from lines whose amounts are net of tax.");
            }

            foreach (var pl in result.Value.Lines)
            {
                var (item, l, no) = pricingItems[pl.Key];
                var line = lines[no - 1];
                Fill(line, new Priced(Shared.Round(pl.NetAmount, currency.Value), pl.LineDiscountAmount + pl.PromotionDiscountAmount + pl.DocumentDiscountAmount, pl.UnitPrice, pl.BaseQuantity, pl.UomId, item.Id, l.VariantId, l.TaxCodeId, line.DimensionSetId, Shared.Trim(l.AccountRole) ?? AccountRoles.Revenue, JsonSerializer.Serialize(pl.Steps, Json)));
            }
        }

        // Tax on the document date (the tax point of an invoice), each line's code pinned where the order or the credited invoice fixed it.
        var taxLines = lines.Select(l => new TaxDocumentLine(l.LineNo.ToString(CultureInfo.InvariantCulture), l.NetAmount, l.ItemId, TaxCodeId: l.TaxCodeId)).ToList();
        var taxed = await tax.CalculateAsync(new TaxDocumentRequest(company.Id.Value, TaxDirections.Sales, documentDate, currency.Value.Code, pricesIncludeTax, taxLines, request.PartnerId), cancellationToken);
        if (taxed.IsFailure)
        {
            return taxed.Error!;
        }

        var taxedByKey = taxed.Value.Document.Lines.ToDictionary(static l => l.Key, StringComparer.Ordinal);
        var totalNet = 0m;
        var totalTax = 0m;
        foreach (var line in lines)
        {
            var key = line.LineNo.ToString(CultureInfo.InvariantCulture);
            var t = taxedByKey[key];
            line.NetAmount = Shared.Round(t.Net, currency.Value);
            line.TaxAmount = Shared.Round(t.Tax, currency.Value);
            line.TaxCodeId = t.TaxCodeId ?? line.TaxCodeId;
            line.TaxRatePct = t.RatePct;
            line.TaxReverseCharge = t.IsReverseCharge;
            line.TaxRecoverable = t.IsRecoverable;
            line.TaxReason = taxed.Value.Determinations.SingleOrDefault(d => d.Key == key)?.Reason;
            totalNet += line.NetAmount;
            if (!line.TaxReverseCharge)
            {
                totalTax += line.TaxAmount;
            }
        }

        invoice.CompanyId = company.Id.Value;
        invoice.BranchId = request.BranchId;
        invoice.Kind = request.Kind;
        invoice.PartnerId = request.PartnerId;
        invoice.CustomerReference = Shared.Trim(request.CustomerReference);
        invoice.CreditsInvoiceId = credited?.Id;
        invoice.DocumentDate = documentDate;
        invoice.PostingDate = postingDate;
        invoice.Currency = currency.Value.Code;
        invoice.PaymentTermsId = isCredit ? null : request.PaymentTermsId ?? customer.Value.PaymentTermsId;
        invoice.SalesRepId = request.SalesRepId ?? customer.Value.SalesRepId;
        invoice.PriceListId = request.PriceListId;
        invoice.TotalNet = totalNet;
        invoice.TotalTax = totalTax;
        invoice.TotalGross = totalNet + totalTax;
        invoice.TaxRoundingLevel = taxed.Value.Document.RoundingLevel;
        invoice.PricesIncludeTax = pricesIncludeTax;
        invoice.CustomerSnapshot = JsonSerializer.Serialize(new { code = customer.Value.PartnerCode, name = customer.Value.PartnerName.Values }, Json);
        invoice.Notes = Shared.Trim(request.Notes);
        invoice.CustomFields = custom.Value;
        invoice.Lines.AddRange(lines);
        return Result.Success();
    }

    private static void Fill(SalesInvoiceLine line, Priced p)
    {
        line.NetAmount = p.Net;
        line.DiscountAmount = p.DiscountAmount;
        line.UnitPrice = p.UnitPrice;
        line.QuantityBase = p.QuantityBase;
        line.UomId = p.UomId;
        line.ItemId = p.ItemId;
        line.VariantId = p.VariantId;
        line.TaxCodeId = p.TaxCodeId;
        line.DimensionSetId = p.DimensionSetId;
        line.AccountRole = p.AccountRole;
        line.PriceBreakdown = p.Breakdown;
    }

    /// <summary>
    /// A quantity of an order line (in base units) at the order's price: its share of the line's net, and the exact
    /// remainder when it completes the line (cancelled quantity never billed); the unit price per base unit and the
    /// discount as the order agreed them.
    /// </summary>
    private static Priced ShareOfOrderLine(SalesOrderLine orderLine, decimal quantityBase, decimal alreadyHereNet, decimal alreadyHereQty, Currency currency, Guid? dimensionSetId)
    {
        var billable = orderLine.QuantityBase - orderLine.QtyCancelled;
        var billableNet = orderLine.QuantityBase == 0m ? 0m : Shared.Round(orderLine.NetAmount * billable / orderLine.QuantityBase, currency);
        var completes = orderLine.QtyInvoiced + alreadyHereQty + quantityBase >= billable;
        var net = completes ? billableNet - orderLine.NetInvoiced - alreadyHereNet : Shared.Round(orderLine.NetAmount * quantityBase / orderLine.QuantityBase, currency);
        var discount = orderLine.QuantityBase == 0m ? 0m : Shared.Round(orderLine.DiscountAmount * quantityBase / orderLine.QuantityBase, currency);
        return new Priced(net, discount, BaseUnitPrice(orderLine), quantityBase, null, orderLine.ItemId, orderLine.VariantId, orderLine.TaxCodeId, dimensionSetId, AccountRoles.Revenue, orderLine.PriceBreakdown);
    }

    /// <summary>The order's unit price per base unit (a dozen at 12.00 is 1.00 a piece).</summary>
    private static decimal BaseUnitPrice(SalesOrderLine orderLine) => orderLine.QuantityBase == 0m ? orderLine.UnitPrice : RoundingPolicy.Default.Round(orderLine.UnitPrice * orderLine.Quantity / orderLine.QuantityBase, 6);

    private static SaveSalesInvoiceRequest Request(SalesInvoice invoice) =>
        new(invoice.CompanyId, invoice.PartnerId,
            invoice.Lines.OrderBy(static l => l.LineNo).Select(l => new SaveSalesInvoiceLineRequest(l.Kind, l.Kind is "shipment" or "order" ? l.QuantityBase : l.Quantity, l.ShipmentLineId, l.OrderLineId, l.InvoiceLineId, l.ItemId, null, l.VariantId, null, l.Kind == "item" ? l.UomId : null,
                l.Kind is "item" or "service" ? l.UnitPrice : null, l.Kind is "item" or "service" ? l.DiscountPct : 0m, l.Kind is "item" or "service" ? l.AccountRole : null, l.Description, l.Kind is "item" or "service" ? l.TaxCodeId : null, null)).ToList(),
            invoice.Kind, invoice.CustomerReference, invoice.CreditsInvoiceId, invoice.DocumentDate, invoice.PostingDate, invoice.Currency, invoice.PaymentTermsId, invoice.SalesRepId, invoice.PriceListId, invoice.BranchId, invoice.Notes, Shared.Parse(invoice.CustomFields));

    private async Task<Result<IReadOnlyList<Instalment>>> InstalmentsAsync(SalesInvoice invoice, CustomerTermsInfo customer, CancellationToken cancellationToken)
    {
        // A credit note is due at once: it is applied to invoices or refunded, never scheduled.
        if (invoice.PaymentTermsId is not { } termsId || invoice.Kind == "credit_note")
        {
            return new List<Instalment> { new(invoice.DocumentDate, invoice.TotalGross, null, 0m) };
        }

        var schedule = await partners.ScheduleAsync(invoice.CompanyId, termsId, invoice.DocumentDate, null, invoice.TotalGross, invoice.Currency, cancellationToken);
        if (schedule.IsFailure)
        {
            return schedule.Error!;
        }

        return schedule.Value.Instalments.Select(i => new Instalment(i.DueOn, i.Amount, schedule.Value.EarlyDiscountUntil, schedule.Value.EarlyDiscountPct)).ToList();
    }

    private async Task<Result<decimal>> RateAsync(CompanyInfo company, string currency, DateOnly date, CancellationToken cancellationToken)
    {
        if (string.Equals(currency, company.FunctionalCurrency.Code, StringComparison.Ordinal))
        {
            return 1m;
        }

        var resolved = await rates.ResolveAsync(company.Id, currency, company.FunctionalCurrency.Code, date, RateTypes.Spot, cancellationToken);
        return resolved.IsFailure
            ? Error.Validation("invoice.rate_missing", "No spot rate from the invoice currency to the company's currency on the posting date.").WithWhy(("from", currency), ("to", company.FunctionalCurrency.Code), ("date", date))
            : resolved.Value.Rate.Rate;
    }

    private static Dictionary<string, string> Empty() => new(StringComparer.Ordinal);

    private async Task<SalesInvoiceSummary> MapAsync(SalesInvoice i, CancellationToken cancellationToken)
    {
        var partner = await customers.FindCustomerAsync(i.CompanyId, i.PartnerId, cancellationToken) is { } account ? (account.PartnerCode, account.PartnerName.Values) : (await partners.FindAsync(i.PartnerId, cancellationToken)) is { } p ? (p.Code, p.LegalName.Values) : (string.Empty, Empty());
        var company = await companies.FindAsync(new CompanyId(i.CompanyId), cancellationToken);
        var terms = i.PaymentTermsId is { } t ? (await partners.DescribeAsync([t], cancellationToken)).GetValueOrDefault(t)?.Code : null;
        var credited = i.CreditsInvoiceId is { } c ? await db.Invoices.AsNoTracking().Where(x => x.Id == c).Select(static x => x.Number).SingleOrDefaultAsync(cancellationToken) : null;
        var shipmentLineIds = i.Lines.Where(static l => l.ShipmentLineId is not null).Select(static l => l.ShipmentLineId!.Value).ToList();
        var shipmentNumbers = shipmentLineIds.Count == 0 ? new Dictionary<Guid, string>() : await (from l in db.ShipmentLines.AsNoTracking() join s in db.Shipments.AsNoTracking() on new { l.TenantId, Id = l.ShipmentId } equals new { s.TenantId, s.Id } where shipmentLineIds.Contains(l.Id) select new { l.Id, s.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var orderLineIds = i.Lines.Where(static l => l.OrderLineId is not null).Select(static l => l.OrderLineId!.Value).ToList();
        var orderNumbers = orderLineIds.Count == 0 ? new Dictionary<Guid, string>() : await (from l in db.OrderLines.AsNoTracking() join o in db.Orders.AsNoTracking() on new { l.TenantId, Id = l.OrderId } equals new { o.TenantId, o.Id } where orderLineIds.Contains(l.Id) select new { l.Id, o.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var creditedLineIds = i.Lines.Where(static l => l.InvoiceLineId is not null).Select(static l => l.InvoiceLineId!.Value).ToList();
        var creditedNumbers = creditedLineIds.Count == 0 ? new Dictionary<Guid, string>() : await (from l in db.InvoiceLines.AsNoTracking() join inv in db.Invoices.AsNoTracking() on new { l.TenantId, Id = l.InvoiceId } equals new { inv.TenantId, inv.Id } where creditedLineIds.Contains(l.Id) select new { l.Id, inv.Number }).ToDictionaryAsync(static x => x.Id, static x => x.Number, cancellationToken);
        var taxCodes = await taxDirectory.DescribeCodesAsync(i.Lines.Where(static l => l.TaxCodeId is not null).Select(static l => l.TaxCodeId!.Value).Distinct().ToList(), cancellationToken);
        var lines = new List<SalesInvoiceLineSummary>(i.Lines.Count);
        foreach (var l in i.Lines.OrderBy(static l => l.LineNo))
        {
            var item = l.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : null;
            var uomId = l.UomId ?? (l.Kind is "shipment" or "order" ? item?.BaseUomId : null);
            var uom = item is null || uomId is null ? null : (await items.UomsAsync(item.Id, cancellationToken)).FirstOrDefault(u => u.UomId == uomId);
            lines.Add(new SalesInvoiceLineSummary(l.Id, l.LineNo, l.Kind, l.ShipmentLineId, l.ShipmentLineId is { } sl ? shipmentNumbers.GetValueOrDefault(sl) : null, l.OrderLineId, l.OrderLineId is { } ol ? orderNumbers.GetValueOrDefault(ol) : null,
                l.InvoiceLineId, l.InvoiceLineId is { } il ? creditedNumbers.GetValueOrDefault(il) : null, l.ItemId, item?.Code, item?.Name.Values, l.VariantId, l.AccountRole, l.Description,
                l.Kind is "shipment" or "order" ? l.QuantityBase : l.Quantity, uomId, uom?.UomCode, l.QuantityBase, l.UnitPrice, l.DiscountPct, l.DiscountAmount, l.NetAmount, l.TaxAmount, l.QtyCredited,
                l.TaxCodeId, l.TaxCodeId is { } code ? taxCodes.GetValueOrDefault(code)?.Code : null, l.TaxRatePct, l.TaxReverseCharge, l.TaxRecoverable, l.TaxReason,
                l.DimensionSetId is { } set ? await dimensionSets.GetAsync(set, cancellationToken) : null));
        }

        var openItems = i.Status is "posted" or "reversed" ? await receivables.ItemsOfAsync(DocumentType, i.Id, cancellationToken) : [];
        return new SalesInvoiceSummary(i.Id, i.CompanyId, i.Number, i.Kind, i.Status, i.PartnerId, partner.Item1, partner.Item2, i.CustomerReference, i.CreditsInvoiceId, credited, i.DocumentDate, i.PostingDate, i.DueDate, i.Currency, i.ExchangeRate, company?.FunctionalCurrency.Code ?? i.Currency,
            i.PaymentTermsId, terms, i.SalesRepId, i.TotalNet, i.TotalTax, i.TotalGross, i.TaxRoundingLevel, i.PricesIncludeTax, i.JournalEntryId, i.ReversalEntryId, i.ReversalReason, i.EinvoiceStatus, i.Notes, Shared.Parse(i.CustomFields), lines, openItems, i.PostedAt, i.UpdatedAt);
    }
}
