using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Items.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Messaging;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Purchasing.Contracts;
using Quicker.Purchasing.Domain;
using Quicker.Purchasing.Persistence;

namespace Quicker.Purchasing.Application;

/// <summary>
/// Requests for quotation: lines to price, suppliers invited and sent the request by email, their quotes recorded by
/// the buyer, compared by landed price in the company's currency then lead time, and awarded into a purchase order.
/// </summary>
public sealed class RfqService(
    PurchasingDbContext db,
    ICompanyDirectory companies,
    IExchangeRateResolver rates,
    IItemDirectory items,
    IPartnerDirectory partners,
    INumberAllocator numbering,
    IEmailSender email,
    ICurrentPrincipal principal,
    IAuditSink audit,
    IClock clock,
    PurchaseOrderService orders)
{
    public const string DocumentType = PurchaseDocumentTypes.Rfq;

    public async Task<IReadOnlyList<RfqSummary>> ListAsync(Guid? companyId, string? status, CancellationToken cancellationToken)
    {
        var query = db.Rfqs.Include(static r => r.Lines).Include(static r => r.Suppliers).AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(r => r.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var s = status.Trim().ToLowerInvariant();
            query = query.Where(r => r.Status == s);
        }

        var rows = await query.OrderByDescending(static r => r.Id).Take(200).ToListAsync(cancellationToken);
        var result = new List<RfqSummary>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(await MapAsync(row, cancellationToken));
        }

        return result;
    }

    public async Task<RfqSummary?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var rfq = await LoadAsync(id, true, cancellationToken);
        return rfq is null ? null : await MapAsync(rfq, cancellationToken);
    }

    public async Task<Result<RfqSummary>> CreateAsync(SaveRfqRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var rfq = new Rfq { Id = Guid.CreateVersion7(), CompanyId = company.Id.Value, CreatedBy = principal.Principal?.UserId.Value, CreatedAt = clock.UtcNow };
        var applied = await ApplyAsync(rfq, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var series = await numbering.EnsureDefaultSeriesAsync(DocumentType, company.Id, "RFQ-" + company.Code, "RFQ-{yyyy}-{seq:5}", "yearly", cancellationToken);
        if (series.IsFailure)
        {
            return series.Error!;
        }

        var number = await numbering.AllocateAsync(new NumberRequest(DocumentType, company.Id, null, clock.TodayIn(company.TimeZone), rfq.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        rfq.Number = number.Value.Text;
        db.Rfqs.Add(rfq);
        if (request.PartnerIds is { Count: > 0 })
        {
            var invited = await InviteCoreAsync(rfq, request.PartnerIds, cancellationToken);
            if (invited.IsFailure)
            {
                return invited.Error!;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    public async Task<Result<RfqSummary>> UpdateAsync(Guid id, SaveRfqRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        if (rfq.Status != "draft")
        {
            return Error.Conflict("rfq.not_draft", "Only a draft request is edited.").WithWhy(("status", rfq.Status));
        }

        if (rfq.CompanyId != request.CompanyId)
        {
            return Error.Conflict("rfq.company_locked", "A request cannot move to another company.");
        }

        var applied = await ApplyAsync(rfq, request, cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    private async Task<Result> ApplyAsync(Rfq rfq, SaveRfqRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("rfq.lines_required", "A request has at least one line.");
        }

        var lines = new List<RfqLine>();
        var lineNo = 0;
        foreach (var line in request.Lines)
        {
            lineNo++;
            var entity = new RfqLine { Id = Guid.CreateVersion7(), RfqId = rfq.Id, LineNo = lineNo, Description = Shared.Trim(line.Description), RequisitionLineId = line.RequisitionLineId };
            if (line.ItemId is not null || !string.IsNullOrWhiteSpace(line.ItemCode))
            {
                var resolved = await Shared.ResolveLineAsync(items, "rfq", line.ItemId, line.ItemCode, line.Quantity, line.Uom, line.UomId, cancellationToken);
                if (resolved.IsFailure)
                {
                    return resolved.Error!.WithWhy(("lineNo", lineNo));
                }

                entity.ItemId = resolved.Value.Item.Id;
                entity.Quantity = line.Quantity;
                entity.UomId = resolved.Value.Unit.UomId;
                entity.QuantityBase = resolved.Value.QuantityBase;
            }
            else if (entity.Description is null || line.UomId is null || line.Quantity <= 0m)
            {
                return Error.Validation("rfq.line_invalid", "A line names an item, or a description with a unit and a positive quantity.").WithWhy(("lineNo", lineNo));
            }
            else
            {
                entity.Quantity = line.Quantity;
                entity.UomId = line.UomId.Value;
                entity.QuantityBase = line.Quantity;
            }

            lines.Add(entity);
        }

        rfq.Title = Shared.Trim(request.Title);
        rfq.DueOn = request.DueOn;
        rfq.Notes = Shared.Trim(request.Notes);
        rfq.Lines.Clear();
        rfq.Lines.AddRange(lines);
        rfq.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    public async Task<Result<RfqSummary>> InviteAsync(Guid id, InviteSuppliersRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        if (rfq.Status is not ("draft" or "sent"))
        {
            return Error.Conflict("rfq.closed", "A closed or awarded request takes no more suppliers.").WithWhy(("status", rfq.Status));
        }

        var invited = await InviteCoreAsync(rfq, request.PartnerIds, cancellationToken);
        if (invited.IsFailure)
        {
            return invited.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    private async Task<Result> InviteCoreAsync(Rfq rfq, IReadOnlyList<Guid> partnerIds, CancellationToken cancellationToken)
    {
        foreach (var partnerId in partnerIds.Distinct())
        {
            if (rfq.Suppliers.Any(s => s.PartnerId == partnerId))
            {
                continue;
            }

            var supplier = await partners.EnsureSupplierAsync(rfq.CompanyId, partnerId, SupplierPurposes.Purchase, cancellationToken);
            if (supplier.IsFailure)
            {
                return supplier.Error!;
            }

            var partner = await partners.FindAsync(partnerId, cancellationToken);
            rfq.Suppliers.Add(new RfqSupplier { Id = Guid.CreateVersion7(), RfqId = rfq.Id, PartnerId = partnerId, ContactEmail = partner?.Email });
        }

        rfq.UpdatedAt = clock.UtcNow;
        return Result.Success();
    }

    /// <summary>Sends the request to every invited supplier not yet sent to; each needs an email (on the partner or given here).</summary>
    public async Task<Result<RfqSummary>> SendAsync(Guid id, SendRfqRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        if (rfq.Status is not ("draft" or "sent"))
        {
            return Error.Conflict("rfq.closed", "A closed or awarded request is not sent.").WithWhy(("status", rfq.Status));
        }

        if (rfq.Suppliers.Count == 0)
        {
            return Error.Validation("rfq.suppliers_required", "Invite at least one supplier first.");
        }

        var pending = rfq.Suppliers.Where(static s => s.SentAt is null).ToList();
        foreach (var supplier in pending)
        {
            var to = request.Emails is not null && request.Emails.TryGetValue(supplier.PartnerId, out var given) && !string.IsNullOrWhiteSpace(given) ? given.Trim() : supplier.ContactEmail;
            if (string.IsNullOrWhiteSpace(to))
            {
                var partner = await partners.FindAsync(supplier.PartnerId, cancellationToken);
                return Error.Validation("rfq.supplier_no_email", "A supplier has no email address to send to.").WithWhy(("partner", partner?.Code), ("partnerId", supplier.PartnerId));
            }

            supplier.ContactEmail = to;
        }

        var company = (await companies.FindAsync(new CompanyId(rfq.CompanyId), cancellationToken))!;
        var lineText = new List<string>();
        foreach (var line in rfq.Lines.OrderBy(static l => l.LineNo))
        {
            var item = line.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : null;
            var unit = item is null ? null : (await items.UomsAsync(item.Id, cancellationToken)).FirstOrDefault(u => u.UomId == line.UomId);
            lineText.Add($"{line.LineNo}. {item?.Code ?? string.Empty} {line.Description ?? item?.Name.Resolve("en") ?? string.Empty} — {line.Quantity:0.###} {unit?.UomCode ?? string.Empty}");
        }

        var body = string.Join('\n', new[] { $"Request for quotation {rfq.Number}{(rfq.Title is null ? string.Empty : " — " + rfq.Title)} from {company.LegalName.Resolve("en")} ({company.Code})", rfq.DueOn is { } due ? $"Please quote by {due:yyyy-MM-dd}." : string.Empty, string.Empty }.Concat(lineText).Append(string.Empty).Append(rfq.Notes ?? string.Empty));
        foreach (var supplier in pending)
        {
            await email.SendAsync(new EmailMessage(supplier.ContactEmail!, $"Request for quotation {rfq.Number}", body), cancellationToken);
            supplier.SentAt = clock.UtcNow;
        }

        rfq.Status = "sent";
        rfq.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, rfq.Id, rfq.Number, AuditActions.StateChanged, After: new { status = rfq.Status, sentTo = pending.Select(static s => s.ContactEmail) }, CompanyId: rfq.CompanyId), cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    /// <summary>Records (or replaces) a supplier's quote as the buyer received it.</summary>
    public async Task<Result<RfqSummary>> RecordQuoteAsync(Guid id, SaveQuoteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        if (rfq.Status is not ("draft" or "sent"))
        {
            return Error.Conflict("rfq.closed", "A closed or awarded request takes no more quotes.").WithWhy(("status", rfq.Status));
        }

        var supplier = rfq.Suppliers.SingleOrDefault(s => s.PartnerId == request.PartnerId);
        if (supplier is null)
        {
            return Error.Validation("rfq.supplier_not_invited", "Invite the supplier before recording its quote.").WithWhy(("partnerId", request.PartnerId));
        }

        var currency = await Shared.CurrencyAsync(companies, request.Currency, string.Empty, "quote", cancellationToken);
        if (currency.IsFailure)
        {
            return currency.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Error.Validation("quote.lines_required", "A quote prices at least one line.");
        }

        if (request.LeadTimeDays < 0 || request.FreightAmount < 0m || request.OtherCharges < 0m)
        {
            return Error.Validation("quote.values_invalid", "Lead time, freight and charges are zero or more.");
        }

        var lines = new List<SupplierQuoteLine>();
        foreach (var line in request.Lines)
        {
            var rfqLine = rfq.Lines.SingleOrDefault(l => l.Id == line.RfqLineId);
            if (rfqLine is null)
            {
                return Error.Validation("quote.line_unknown", "The quoted line is not on the request.").WithWhy(("rfqLineId", line.RfqLineId));
            }

            if (line.UnitPrice < 0m || (line.Quantity is { } q && q <= 0m))
            {
                return Error.Validation("quote.line_invalid", "Unit prices are zero or more and quantities positive.").WithWhy(("rfqLineNo", rfqLine.LineNo));
            }

            if (lines.Any(l => l.RfqLineId == rfqLine.Id))
            {
                return Error.Validation("quote.line_duplicate", "A request line is quoted once.").WithWhy(("rfqLineNo", rfqLine.LineNo));
            }

            lines.Add(new SupplierQuoteLine { Id = Guid.CreateVersion7(), RfqLineId = rfqLine.Id, UnitPrice = Shared.Round(line.UnitPrice, currency.Value), Quantity = line.Quantity ?? rfqLine.Quantity, UomId = rfqLine.UomId, LeadTimeDays = line.LeadTimeDays });
        }

        var quote = await db.Quotes.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.RfqSupplierId == supplier.Id, cancellationToken);
        var isNew = quote is null;
        quote ??= new SupplierQuote { Id = Guid.CreateVersion7(), RfqSupplierId = supplier.Id, CreatedAt = clock.UtcNow };
        quote.SupplierReference = Shared.Trim(request.SupplierReference);
        quote.Currency = currency.Value.Code;
        quote.ValidUntil = request.ValidUntil;
        quote.PaymentTermsId = request.PaymentTermsId;
        quote.LeadTimeDays = request.LeadTimeDays;
        quote.FreightAmount = Shared.Round(request.FreightAmount, currency.Value);
        quote.OtherCharges = Shared.Round(request.OtherCharges, currency.Value);
        quote.Notes = Shared.Trim(request.Notes);
        quote.ReceivedAt = clock.UtcNow;
        quote.UpdatedAt = clock.UtcNow;
        quote.ComparisonScore = null;
        quote.Lines.Clear();
        foreach (var line in lines)
        {
            line.QuoteId = quote.Id;
            quote.Lines.Add(line);
        }

        if (isNew)
        {
            db.Quotes.Add(quote);
        }

        supplier.Status = "responded";
        rfq.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, rfq.Id, rfq.Number, AuditActions.Updated, After: new { quoteFrom = request.PartnerId, quote.Currency, lines = lines.Count }, CompanyId: rfq.CompanyId), cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    public async Task<Result<RfqSummary>> DeclineAsync(Guid id, Guid partnerId, CancellationToken cancellationToken)
    {
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        var supplier = rfq.Suppliers.SingleOrDefault(s => s.PartnerId == partnerId);
        if (supplier is null)
        {
            return Error.Validation("rfq.supplier_not_invited", "The supplier was not invited.").WithWhy(("partnerId", partnerId));
        }

        supplier.Status = "declined";
        rfq.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    /// <summary>
    /// Ranks the quotes: complete quotes (every line priced) first, then by the landed total in the company's currency
    /// (goods plus freight and charges, converted at the spot rate of the day), then by lead time. The ranking is
    /// stored on each quote so the award screen and the audit trail show why.
    /// </summary>
    public async Task<Result<QuoteComparison>> CompareAsync(Guid id, CancellationToken cancellationToken)
    {
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        var company = (await companies.FindAsync(new CompanyId(rfq.CompanyId), cancellationToken))!;
        var today = clock.TodayIn(company.TimeZone);
        var quotes = await db.Quotes.Include(static q => q.Lines).Where(q => rfq.Suppliers.Select(static s => s.Id).Contains(q.RfqSupplierId)).ToListAsync(cancellationToken);
        var rankings = new List<QuoteRanking>();
        var totalBase = rfq.Lines.Sum(static l => l.QuantityBase);
        foreach (var quote in quotes)
        {
            var supplier = rfq.Suppliers.Single(s => s.Id == quote.RfqSupplierId);
            var partner = await partners.FindAsync(supplier.PartnerId, cancellationToken);
            var currency = (await companies.FindCurrencyAsync(quote.Currency, cancellationToken))!.Value;
            var rate = 1m;
            if (quote.Currency != company.FunctionalCurrency.Code)
            {
                var resolved = await rates.ResolveAsync(company.Id, quote.Currency, company.FunctionalCurrency.Code, today, RateTypes.Spot, cancellationToken);
                if (resolved.IsFailure)
                {
                    return resolved.Error!.WithWhy(("quoteId", quote.Id), ("currency", quote.Currency));
                }

                rate = resolved.Value.Rate.Rate;
            }

            var goods = Shared.Round(quote.Lines.Sum(static l => l.UnitPrice * l.Quantity), currency);
            var landed = goods + quote.FreightAmount + quote.OtherCharges;
            var landedRc = Shared.Round(landed * rate, company.FunctionalCurrency);
            var complete = rfq.Lines.All(l => quote.Lines.Any(q => q.RfqLineId == l.Id));
            var quotedBase = quote.Lines.Sum(l => { var rl = rfq.Lines.Single(x => x.Id == l.RfqLineId); return rl.QuantityBase * (l.Quantity / rl.Quantity); });
            var perLine = new Dictionary<string, decimal>(StringComparer.Ordinal);
            var charges = goods == 0m ? 0m : (quote.FreightAmount + quote.OtherCharges) / goods;
            foreach (var line in quote.Lines)
            {
                var rfqLine = rfq.Lines.Single(l => l.Id == line.RfqLineId);
                perLine[rfqLine.LineNo.ToString(System.Globalization.CultureInfo.InvariantCulture)] = Shared.Round(line.UnitPrice * (1m + charges) * rate, company.FunctionalCurrency);
            }

            rankings.Add(new QuoteRanking(quote.Id, supplier.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), quote.Currency, goods, landed, rate, landedRc,
                quotedBase == 0m ? 0m : Shared.Round(landedRc / quotedBase, company.FunctionalCurrency), quote.LeadTimeDays, complete, 0, perLine));
        }

        var ordered = rankings.OrderByDescending(static r => r.Complete).ThenBy(static r => r.LandedTotalRc).ThenBy(static r => r.LeadTimeDays).ThenBy(static r => r.PartnerCode, StringComparer.Ordinal).Select((r, i) => r with { Rank = i + 1 }).ToList();
        foreach (var ranking in ordered)
        {
            var quote = quotes.Single(q => q.Id == ranking.QuoteId);
            quote.ComparisonScore = JsonSerializer.Serialize(new { ranking.Rank, ranking.LandedTotalRc, ranking.LandedUnitAverageRc, ranking.LeadTimeDays, ranking.Complete, ranking.ExchangeRate, currency = company.FunctionalCurrency.Code, rateDate = today }, Shared.Json);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new QuoteComparison(rfq.Id, company.FunctionalCurrency.Code, ordered, today);
    }

    /// <summary>Awards a quote: the request is closed as awarded and a purchase order draft is created from the quote, priced as quoted.</summary>
    public async Task<Result<PurchaseOrderSummary>> AwardAsync(Guid id, AwardRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        if (rfq.Status is not ("sent" or "closed"))
        {
            return Error.Conflict("rfq.not_awardable", "Send the request first; an awarded or cancelled one is done.").WithWhy(("status", rfq.Status));
        }

        var quote = await db.Quotes.Include(static q => q.Lines).SingleOrDefaultAsync(q => q.Id == request.QuoteId, cancellationToken);
        var supplier = quote is null ? null : rfq.Suppliers.SingleOrDefault(s => s.Id == quote.RfqSupplierId);
        if (quote is null || supplier is null)
        {
            return Error.NotFound("supplier_quote", request.QuoteId);
        }

        if (quote.Lines.Count == 0 || rfq.Lines.Any(l => quote.Lines.All(q => q.RfqLineId != l.Id) && l.ItemId is not null))
        {
            return Error.Conflict("rfq.quote_incomplete", "The awarded quote must price every line with an item.").WithWhy(("quoteId", quote.Id));
        }

        var company = (await companies.FindAsync(new CompanyId(rfq.CompanyId), cancellationToken))!;
        var today = clock.TodayIn(company.TimeZone);
        var lines = new List<SavePurchaseOrderLineRequest>();
        foreach (var line in quote.Lines.OrderBy(l => rfq.Lines.Single(r => r.Id == l.RfqLineId).LineNo))
        {
            var rfqLine = rfq.Lines.Single(r => r.Id == line.RfqLineId);
            if (rfqLine.ItemId is null)
            {
                continue;
            }

            lines.Add(new SavePurchaseOrderLineRequest(rfqLine.ItemId, null, null, rfqLine.Description, line.Quantity, null, line.UomId, line.UnitPrice, 0m, today.AddDays(line.LeadTimeDays ?? quote.LeadTimeDays), request.WarehouseId, null, rfqLine.RequisitionLineId));
        }

        if (lines.Count == 0)
        {
            return Error.Conflict("rfq.no_item_lines", "Only lines with items become purchase order lines.");
        }

        var order = await orders.CreateAsync(new SavePurchaseOrderRequest(rfq.CompanyId, supplier.PartnerId, lines, quote.Currency, today, today.AddDays(quote.LeadTimeDays), quote.PaymentTermsId, null, request.WarehouseId, null, null, $"Awarded from {rfq.Number}" + (quote.SupplierReference is null ? string.Empty : $" (quote {quote.SupplierReference})")), cancellationToken, null, rfq.Id);
        if (order.IsFailure)
        {
            return order.Error!;
        }

        rfq.Status = "awarded";
        rfq.AwardedQuoteId = quote.Id;
        rfq.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, rfq.Id, rfq.Number, AuditActions.StateChanged, After: new { status = rfq.Status, awardedQuoteId = quote.Id, partnerId = supplier.PartnerId, order = order.Value.Number, comparison = quote.ComparisonScore is null ? (JsonElement?)null : Shared.Parse(quote.ComparisonScore) }, CompanyId: rfq.CompanyId), cancellationToken);
        return order.Value;
    }

    public async Task<Result<RfqSummary>> SetStatusAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        var rfq = await LoadAsync(id, false, cancellationToken);
        if (rfq is null)
        {
            return Error.NotFound("purchase_rfq", id);
        }

        switch (action)
        {
            case "close" when rfq.Status == "sent":
                rfq.Status = "closed";
                break;
            case "cancel" when rfq.Status is "draft" or "sent" or "closed":
                rfq.Status = "cancelled";
                break;
            default:
                return Error.Conflict("rfq.transition_invalid", $"'{action}' is not allowed from '{rfq.Status}'.").WithWhy(("status", rfq.Status), ("action", action));
        }

        rfq.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(DocumentType, rfq.Id, rfq.Number, AuditActions.StateChanged, After: new { status = rfq.Status }, CompanyId: rfq.CompanyId), cancellationToken);
        return await MapAsync(rfq, cancellationToken);
    }

    private async Task<Rfq?> LoadAsync(Guid id, bool readOnly, CancellationToken cancellationToken)
    {
        var query = db.Rfqs.Include(static r => r.Lines).Include(static r => r.Suppliers);
        return readOnly ? await query.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken) : await query.SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
    }

    private async Task<RfqSummary> MapAsync(Rfq r, CancellationToken cancellationToken)
    {
        var lines = new List<RfqLineSummary>(r.Lines.Count);
        var uomCodes = new Dictionary<Guid, string>();
        foreach (var l in r.Lines.OrderBy(static l => l.LineNo))
        {
            var item = l.ItemId is { } itemId ? await items.FindAsync(itemId, cancellationToken) : null;
            var unit = item is null ? null : (await items.UomsAsync(item.Id, cancellationToken)).FirstOrDefault(u => u.UomId == l.UomId);
            uomCodes[l.Id] = unit?.UomCode ?? string.Empty;
            lines.Add(new RfqLineSummary(l.Id, l.LineNo, l.ItemId, item?.Code, item?.Name.Values, l.Description, l.Quantity, l.UomId, unit?.UomCode ?? string.Empty, l.QuantityBase, l.RequisitionLineId));
        }

        var quotes = await db.Quotes.Include(static q => q.Lines).AsNoTracking().Where(q => r.Suppliers.Select(static s => s.Id).Contains(q.RfqSupplierId)).ToListAsync(cancellationToken);
        var suppliers = new List<RfqSupplierSummary>(r.Suppliers.Count);
        var quoteSummaries = new List<SupplierQuoteSummary>(quotes.Count);
        foreach (var s in r.Suppliers)
        {
            var partner = await partners.FindAsync(s.PartnerId, cancellationToken);
            var quote = quotes.SingleOrDefault(q => q.RfqSupplierId == s.Id);
            suppliers.Add(new RfqSupplierSummary(s.Id, s.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), s.ContactEmail, s.SentAt, s.Status, quote?.Id));
            if (quote is not null)
            {
                var goods = quote.Lines.Sum(static l => l.UnitPrice * l.Quantity);
                quoteSummaries.Add(new SupplierQuoteSummary(quote.Id, s.Id, s.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? new Dictionary<string, string>(StringComparer.Ordinal), quote.SupplierReference, quote.Currency, quote.ValidUntil, quote.PaymentTermsId, quote.LeadTimeDays, quote.FreightAmount, quote.OtherCharges, goods, goods + quote.FreightAmount + quote.OtherCharges, quote.Notes,
                    quote.ComparisonScore is null ? null : Shared.Parse(quote.ComparisonScore), quote.ReceivedAt,
                    quote.Lines.Select(l => new QuoteLineSummary(l.Id, l.RfqLineId, r.Lines.Single(x => x.Id == l.RfqLineId).LineNo, l.UnitPrice, l.Quantity, l.UomId, uomCodes.GetValueOrDefault(l.RfqLineId, string.Empty), l.LeadTimeDays, l.UnitPrice * l.Quantity)).OrderBy(static l => l.RfqLineNo).ToList()));
            }
        }

        return new RfqSummary(r.Id, r.CompanyId, r.Number, r.Title, r.DueOn, r.Status, r.AwardedQuoteId, r.Notes, lines, suppliers, quoteSummaries, r.UpdatedAt);
    }
}
