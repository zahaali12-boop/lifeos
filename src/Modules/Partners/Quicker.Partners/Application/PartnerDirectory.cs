using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Amounts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>What purchasing, receiving, invoicing and payments read from the supplier master (roadmap 4.1).</summary>
public sealed class PartnerDirectory(PartnersDbContext db, SupplierService suppliers, ICompanyDirectory companies, IWorkingDayCalendar calendar) : IPartnerDirectory
{
    public async Task<IReadOnlyDictionary<Guid, PartnerRecordRef>> DescribeAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, PartnerRecordRef>();
        if (ids.Count == 0)
        {
            return result;
        }

        var wanted = ids.Distinct().ToArray();
        foreach (var p in await db.Partners.AsNoTracking().Where(p => wanted.Contains(p.Id)).Select(static p => new { p.Id, p.Code, p.LegalName }).ToListAsync(cancellationToken))
        {
            result[p.Id] = new PartnerRecordRef(p.Id, "partner", p.Code, p.LegalName);
        }

        foreach (var g in await db.CustomerGroups.AsNoTracking().Where(g => wanted.Contains(g.Id)).Select(static g => new { g.Id, g.Code, g.Name }).ToListAsync(cancellationToken))
        {
            result[g.Id] = new PartnerRecordRef(g.Id, "customer_group", g.Code, g.Name);
        }

        foreach (var t in await db.PaymentTerms.AsNoTracking().Where(t => wanted.Contains(t.Id)).Select(static t => new { t.Id, t.Code, t.Name }).ToListAsync(cancellationToken))
        {
            result[t.Id] = new PartnerRecordRef(t.Id, "payment_terms", t.Code, t.Name);
        }

        return result;
    }

    public async Task<PartnerInfo?> FindAsync(Guid partnerId, CancellationToken cancellationToken = default)
    {
        var p = await db.Partners.AsNoTracking().SingleOrDefaultAsync(x => x.Id == partnerId, cancellationToken);
        return p is null ? null : new PartnerInfo(p.Id, p.Code, p.LegalName, p.TradeName, p.Kind, p.IsSupplier, p.IsCustomer, p.IsActive, p.Email, p.DefaultLanguage, p.IntercompanyCompanyId);
    }

    public async Task<PartnerInfo?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        var p = await db.Partners.AsNoTracking().SingleOrDefaultAsync(x => x.Code == normalized, cancellationToken);
        return p is null ? null : new PartnerInfo(p.Id, p.Code, p.LegalName, p.TradeName, p.Kind, p.IsSupplier, p.IsCustomer, p.IsActive, p.Email, p.DefaultLanguage, p.IntercompanyCompanyId);
    }

    public async Task<SupplierTermsInfo?> FindSupplierAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken = default)
    {
        var account = await db.SupplierAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.PartnerId == partnerId && a.CompanyId == companyId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var partner = await db.Partners.AsNoTracking().SingleAsync(p => p.Id == partnerId, cancellationToken);
        var lookups = await suppliers.LookupsAsync(cancellationToken);
        var effective = SupplierService.Effective(account, lookups);
        return new SupplierTermsInfo(partner.Id, partner.Code, partner.LegalName, account.CompanyId, account.Currency, account.SupplierGroupId,
            effective.PaymentTermsId, effective.PaymentTermsCode, effective.DeliveryTermsId, effective.DeliveryTermsCode, effective.PostingGroupId, account.TaxGroupId, account.WhtCodeId,
            account.LeadTimeDays, account.PriceTolerancePct, account.QtyTolerancePct, account.RequiresPo, account.HoldStatus, account.HoldReason, account.IsActive && partner.IsActive);
    }

    public async Task<Result<SupplierTermsInfo>> EnsureSupplierAsync(Guid companyId, Guid partnerId, string purpose, CancellationToken cancellationToken = default)
    {
        var terms = await FindSupplierAsync(companyId, partnerId, cancellationToken);
        if (terms is null)
        {
            return Error.Validation("supplier.not_registered", "The partner is not a supplier of this company.").WithWhy(("partnerId", partnerId), ("companyId", companyId));
        }

        if (!terms.IsActive)
        {
            return Error.Conflict("supplier.inactive", "The supplier account is inactive.").WithWhy(("partner", terms.PartnerCode));
        }

        var blocked = terms.HoldStatus == HoldStatuses.All
            || (purpose == SupplierPurposes.Purchase && terms.HoldStatus == HoldStatuses.Purchase)
            || (purpose == SupplierPurposes.Payment && terms.HoldStatus == HoldStatuses.Payment);
        if (blocked)
        {
            return Error.Conflict("supplier.on_hold", "The supplier is on hold.").WithWhy(("partner", terms.PartnerCode), ("holdStatus", terms.HoldStatus), ("reason", terms.HoldReason), ("purpose", purpose));
        }

        return terms;
    }

    public async Task<WhtCodeInfo?> FindWhtCodeAsync(Guid whtCodeId, CancellationToken cancellationToken = default)
    {
        var c = await db.WhtCodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == whtCodeId, cancellationToken);
        return c is null ? null : new WhtCodeInfo(c.Id, c.Code, c.Name, c.RatePct, c.WithholdAt, c.ThresholdAmount, c.ThresholdCurrency, c.IsActive);
    }

    public async Task<Result<PaymentSchedule>> ScheduleAsync(Guid companyId, Guid paymentTermsId, DateOnly invoiceDate, DateOnly? deliveryDate, decimal amount, string currency, CancellationToken cancellationToken = default)
    {
        var terms = await db.PaymentTerms.AsNoTracking().Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == paymentTermsId, cancellationToken);
        if (terms is null)
        {
            return Error.NotFound("payment_terms", paymentTermsId);
        }

        var iso = await companies.FindCurrencyAsync((currency ?? string.Empty).Trim().ToUpperInvariant(), cancellationToken);
        if (iso is null)
        {
            return Error.Validation("payment_terms.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", currency));
        }

        var baseDate = terms.DueBasis switch
        {
            DueBases.EndOfMonth => new DateOnly(invoiceDate.Year, invoiceDate.Month, DateTime.DaysInMonth(invoiceDate.Year, invoiceDate.Month)),
            DueBases.Delivery => deliveryDate ?? invoiceDate,
            _ => invoiceDate,
        };
        var company = new CompanyId(companyId);
        var parts = terms.Lines.Count == 0
            ? [(Sequence: 1, Percentage: 100m, Days: terms.DueDays)]
            : terms.Lines.OrderBy(static l => l.Sequence).Select(static l => (l.Sequence, l.Percentage, l.Days)).ToList();
        var amounts = RoundingPolicy.Default.Allocate(new Money(amount, iso.Value), parts.Select(static p => p.Percentage).ToList());
        var instalments = new List<PaymentInstalment>(parts.Count);
        for (var i = 0; i < parts.Count; i++)
        {
            var due = terms.BusinessDaysOnly ? await calendar.DueDateAsync(company, baseDate, parts[i].Days, cancellationToken) : baseDate.AddDays(parts[i].Days);
            instalments.Add(new PaymentInstalment(parts[i].Sequence, due, parts[i].Percentage, amounts[i].Amount));
        }

        DateOnly? discountUntil = terms.EarlyDiscountPct > 0m ? baseDate.AddDays(terms.EarlyDiscountDays) : null;
        return new PaymentSchedule(terms.Id, terms.Code, baseDate, instalments, discountUntil, terms.EarlyDiscountPct);
    }
}
