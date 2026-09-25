using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;
using Quicker.Tax.Contracts;

namespace Quicker.Partners.Application;

/// <summary>
/// Supplier accounts per company (terms, tolerances, holds, posting) and the configuration they draw on: supplier
/// groups, payment terms with instalments, delivery terms and withholding tax codes.
/// </summary>
public sealed class SupplierService(PartnersDbContext db, ICompanyDirectory companies, IPostingGroupDirectory postingGroups, ITaxDirectory taxGroups, ICurrentPrincipal principal, IClock clock)
{
    public const string SupplierPostingGroupKind = "partner_supplier";

    // ------------------------------------------------------------------ supplier accounts

    public async Task<IReadOnlyList<SupplierAccountSummary>> ListAccountsAsync(Guid? companyId, string? q, string? holdStatus, bool? isActive, CancellationToken cancellationToken)
    {
        var partners = db.Partners.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            partners = db.Partners.FromSqlInterpolated($"SELECT * FROM app.ptr_partners WHERE code ILIKE {pattern} OR legal_name_i18n->>'en' ILIKE {pattern} OR legal_name_i18n->>'ar' ILIKE {pattern} OR trade_name_i18n->>'en' ILIKE {pattern} OR trade_name_i18n->>'ar' ILIKE {pattern}").AsNoTracking();
        }

        var query = from a in db.SupplierAccounts.AsNoTracking()
                    join p in partners on new { a.TenantId, Id = a.PartnerId } equals new { p.TenantId, p.Id }
                    select new { Account = a, Partner = p };
        if (companyId is { } c)
        {
            query = query.Where(x => x.Account.CompanyId == c);
        }

        if (!string.IsNullOrWhiteSpace(holdStatus))
        {
            var h = holdStatus.Trim().ToLowerInvariant();
            query = h == "held" ? query.Where(static x => x.Account.HoldStatus != HoldStatuses.None) : query.Where(x => x.Account.HoldStatus == h);
        }

        if (isActive is { } active)
        {
            query = query.Where(x => x.Account.IsActive == active);
        }

        var rows = await query.OrderBy(static x => x.Partner.Code).Take(500).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(cancellationToken);
        return rows.Select(x => Map(x.Account, x.Partner, lookups)).ToList();
    }

    public async Task<IReadOnlyList<SupplierAccountSummary>> ListAccountsOfPartnerAsync(Guid partnerId, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        if (partner is null)
        {
            return [];
        }

        var accounts = await db.SupplierAccounts.AsNoTracking().Where(a => a.PartnerId == partnerId).OrderBy(static a => a.CreatedAt).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(cancellationToken);
        return accounts.Select(a => Map(a, partner, lookups)).ToList();
    }

    public async Task<Result<SupplierAccountSummary>> SaveAccountAsync(Guid partnerId, Guid companyId, SaveSupplierAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        if (partner is null)
        {
            return Error.NotFound(PartnerService.EntityType, partnerId);
        }

        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var currency = (request.Currency ?? company.FunctionalCurrency.Code).Trim().ToUpperInvariant();
        if (await companies.FindCurrencyAsync(currency, cancellationToken) is null)
        {
            return Error.Validation("supplier.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", request.Currency));
        }

        var checks = await CheckReferencesAsync(request.SupplierGroupId, request.PaymentTermsId, request.DeliveryTermsId, request.PostingGroupId, request.WhtCodeId, "supplier", cancellationToken);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        if (request.TaxGroupId is { } taxGroupId && await taxGroups.FindGroupAsync(taxGroupId, cancellationToken) is not { Kind: TaxGroupKinds.Partner })
        {
            return Error.Validation("supplier.tax_group_invalid", "The tax group must be a partner tax group.").WithWhy(("taxGroupId", taxGroupId));
        }

        if (request.LeadTimeDays < 0)
        {
            return Error.Validation("supplier.lead_time_invalid", "Lead time is zero or more days.");
        }

        foreach (var (value, field) in new[] { (request.PriceTolerancePct, "supplier.price_tolerance"), (request.QtyTolerancePct, "supplier.qty_tolerance") })
        {
            var pct = Validation.Percentage(value, field);
            if (pct.IsFailure)
            {
                return pct.Error!;
            }
        }

        var account = await db.SupplierAccounts.SingleOrDefaultAsync(a => a.PartnerId == partnerId && a.CompanyId == companyId, cancellationToken);
        var isNew = account is null;
        account ??= new SupplierAccount { Id = Guid.CreateVersion7(), PartnerId = partnerId, CompanyId = companyId, CreatedAt = clock.UtcNow };
        account.SupplierGroupId = request.SupplierGroupId;
        account.PaymentTermsId = request.PaymentTermsId;
        account.DeliveryTermsId = request.DeliveryTermsId;
        account.PostingGroupId = request.PostingGroupId;
        account.TaxGroupId = request.TaxGroupId;
        account.WhtCodeId = request.WhtCodeId;
        account.Currency = currency;
        account.LeadTimeDays = request.LeadTimeDays;
        account.PriceTolerancePct = request.PriceTolerancePct;
        account.QtyTolerancePct = request.QtyTolerancePct;
        account.RequiresPo = request.RequiresPo;
        account.IsActive = request.IsActive;
        account.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.SupplierAccounts.Add(account);
        }

        if (!partner.IsSupplier)
        {
            // Registering a supplier account gives the partner the role; the flag is what lists and documents read.
            partner.IsSupplier = true;
            partner.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(account, partner, await LookupsAsync(cancellationToken));
    }

    public async Task<Result<SupplierAccountSummary>> HoldAsync(Guid partnerId, Guid companyId, HoldRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = Validation.OneOf(request.Status, "supplier.hold_status", HoldStatuses.Values);
        if (status.IsFailure)
        {
            return status.Error!;
        }

        if (status.Value == HoldStatuses.None)
        {
            return await ReleaseAsync(partnerId, companyId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("supplier.hold_reason_required", "A hold says why.");
        }

        var (account, partner) = await AccountAsync(partnerId, companyId, cancellationToken);
        if (account is null || partner is null)
        {
            return Error.NotFound("supplier_account", partnerId);
        }

        account.HoldStatus = status.Value;
        account.HoldReason = request.Reason.Trim();
        account.HeldAt = clock.UtcNow;
        account.HeldBy = principal.Principal?.UserId.Value;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(account, partner, await LookupsAsync(cancellationToken));
    }

    public async Task<Result<SupplierAccountSummary>> ReleaseAsync(Guid partnerId, Guid companyId, CancellationToken cancellationToken)
    {
        var (account, partner) = await AccountAsync(partnerId, companyId, cancellationToken);
        if (account is null || partner is null)
        {
            return Error.NotFound("supplier_account", partnerId);
        }

        account.HoldStatus = HoldStatuses.None;
        account.HoldReason = null;
        account.HeldAt = null;
        account.HeldBy = null;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(account, partner, await LookupsAsync(cancellationToken));
    }

    private async Task<(SupplierAccount? Account, Partner? Partner)> AccountAsync(Guid partnerId, Guid companyId, CancellationToken cancellationToken)
    {
        var account = await db.SupplierAccounts.SingleOrDefaultAsync(a => a.PartnerId == partnerId && a.CompanyId == companyId, cancellationToken);
        var partner = account is null ? null : await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        return (account, partner);
    }

    // ------------------------------------------------------------------ supplier groups

    public async Task<IReadOnlyList<SupplierGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await db.SupplierGroups.AsNoTracking().OrderBy(static g => g.Code).ToListAsync(cancellationToken);
        var counts = await db.SupplierAccounts.AsNoTracking().Where(static a => a.SupplierGroupId != null).GroupBy(static a => a.SupplierGroupId!.Value).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        return groups.Select(g => Map(g, counts.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<Result<SupplierGroupSummary>> SaveGroupAsync(Guid? groupId, SaveSupplierGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "supplier_group");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "supplier_group");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var checks = await CheckReferencesAsync(null, request.PaymentTermsId, request.DeliveryTermsId, request.PostingGroupId, null, "supplier_group", cancellationToken);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        SupplierGroup? group = null;
        if (groupId is { } id)
        {
            group = await db.SupplierGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return Error.NotFound("supplier_group", id);
            }
        }

        if (await db.SupplierGroups.AnyAsync(g => g.Code == code.Value && g.Id != groupId, cancellationToken))
        {
            return Error.Conflict("supplier_group.code_taken", $"A supplier group with code '{code.Value}' already exists.");
        }

        var isNew = group is null;
        group ??= new SupplierGroup { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        group.Code = code.Value;
        group.Name = name.Value;
        group.PostingGroupId = request.PostingGroupId;
        group.PaymentTermsId = request.PaymentTermsId;
        group.DeliveryTermsId = request.DeliveryTermsId;
        group.IsActive = request.IsActive;
        group.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.SupplierGroups.Add(group);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(group, await db.SupplierAccounts.CountAsync(a => a.SupplierGroupId == group.Id, cancellationToken));
    }

    // ------------------------------------------------------------------ payment terms

    public async Task<IReadOnlyList<PaymentTermsSummary>> ListPaymentTermsAsync(CancellationToken cancellationToken) =>
        (await db.PaymentTerms.AsNoTracking().Include(static t => t.Lines).OrderBy(static t => t.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<PaymentTermsSummary>> SavePaymentTermsAsync(Guid? termsId, SavePaymentTermsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "payment_terms");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "payment_terms");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var basis = Validation.OneOf(request.DueBasis, "payment_terms.due_basis", DueBases.All);
        if (basis.IsFailure)
        {
            return basis.Error!;
        }

        if (request.DueDays < 0 || request.EarlyDiscountDays < 0)
        {
            return Error.Validation("payment_terms.days_invalid", "Days are zero or more.");
        }

        var discount = Validation.Percentage(request.EarlyDiscountPct, "payment_terms.early_discount");
        if (discount.IsFailure || request.EarlyDiscountPct >= 100m)
        {
            return discount.IsFailure ? discount.Error! : Error.Validation("payment_terms.early_discount_invalid", "An early-payment discount is below 100%.");
        }

        var lines = (request.Lines ?? []).OrderBy(static l => l.Sequence).ToList();
        if (lines.Count > 0)
        {
            if (lines.Select(static l => l.Sequence).Distinct().Count() != lines.Count || lines.Any(static l => l.Sequence < 1 || l.Days < 0 || l.Percentage <= 0m))
            {
                return Error.Validation("payment_terms.lines_invalid", "Instalments have distinct sequences, days of zero or more and a positive percentage.");
            }

            if (lines.Sum(static l => l.Percentage) != 100m)
            {
                return Error.Validation("payment_terms.lines_sum", "Instalment percentages sum to 100.").WithWhy(("sum", lines.Sum(static l => l.Percentage)));
            }
        }

        PaymentTerms? terms = null;
        if (termsId is { } id)
        {
            terms = await db.PaymentTerms.Include(static t => t.Lines).SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (terms is null)
            {
                return Error.NotFound("payment_terms", id);
            }

            if (terms.IsSystem && code.Value != terms.Code)
            {
                return Error.Conflict("payment_terms.system_code_locked", "A system payment term keeps its code.");
            }
        }

        if (await db.PaymentTerms.AnyAsync(t => t.Code == code.Value && t.Id != termsId, cancellationToken))
        {
            return Error.Conflict("payment_terms.code_taken", $"Payment terms with code '{code.Value}' already exist.");
        }

        var isNew = terms is null;
        terms ??= new PaymentTerms { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        terms.Code = code.Value;
        terms.Name = name.Value;
        terms.DueBasis = basis.Value;
        terms.DueDays = request.DueDays;
        terms.EarlyDiscountPct = request.EarlyDiscountPct;
        terms.EarlyDiscountDays = request.EarlyDiscountDays;
        terms.BusinessDaysOnly = request.BusinessDaysOnly;
        terms.IsActive = request.IsActive;
        terms.UpdatedAt = clock.UtcNow;
        terms.Lines.Clear();
        terms.Lines.AddRange(lines.Select(l => new PaymentTermLine { TermsId = terms.Id, Sequence = l.Sequence, Percentage = l.Percentage, Days = l.Days }));
        if (isNew)
        {
            db.PaymentTerms.Add(terms);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(terms);
    }

    // ------------------------------------------------------------------ delivery terms

    public async Task<IReadOnlyList<DeliveryTermsSummary>> ListDeliveryTermsAsync(CancellationToken cancellationToken) =>
        (await db.DeliveryTerms.AsNoTracking().OrderBy(static t => t.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<DeliveryTermsSummary>> SaveDeliveryTermsAsync(Guid? termsId, SaveDeliveryTermsRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "delivery_terms", 16);
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "delivery_terms");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        DeliveryTerms? terms = null;
        if (termsId is { } id)
        {
            terms = await db.DeliveryTerms.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
            if (terms is null)
            {
                return Error.NotFound("delivery_terms", id);
            }

            if (terms.IsSystem && code.Value != terms.Code)
            {
                return Error.Conflict("delivery_terms.system_code_locked", "A system delivery term keeps its code.");
            }
        }

        if (await db.DeliveryTerms.AnyAsync(t => t.Code == code.Value && t.Id != termsId, cancellationToken))
        {
            return Error.Conflict("delivery_terms.code_taken", $"Delivery terms with code '{code.Value}' already exist.");
        }

        var isNew = terms is null;
        terms ??= new DeliveryTerms { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        terms.Code = code.Value;
        terms.Name = name.Value;
        terms.IsActive = request.IsActive;
        terms.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.DeliveryTerms.Add(terms);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(terms);
    }

    // ------------------------------------------------------------------ withholding tax codes

    public async Task<IReadOnlyList<WhtCodeSummary>> ListWhtCodesAsync(CancellationToken cancellationToken) =>
        (await db.WhtCodes.AsNoTracking().OrderBy(static c => c.Code).ToListAsync(cancellationToken)).Select(Map).ToList();

    public async Task<Result<WhtCodeSummary>> SaveWhtCodeAsync(Guid? whtCodeId, SaveWhtCodeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "wht_code");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "wht_code");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var rate = Validation.Percentage(request.RatePct, "wht_code.rate");
        if (rate.IsFailure)
        {
            return rate.Error!;
        }

        var point = Validation.OneOf(request.WithholdAt, "wht_code.withhold_at", WithholdingPoints.All);
        if (point.IsFailure)
        {
            return point.Error!;
        }

        string? thresholdCurrency = null;
        if (request.ThresholdAmount is { } threshold)
        {
            if (threshold < 0m)
            {
                return Error.Validation("wht_code.threshold_invalid", "A threshold is zero or more.");
            }

            thresholdCurrency = (request.ThresholdCurrency ?? string.Empty).Trim().ToUpperInvariant();
            if (await companies.FindCurrencyAsync(thresholdCurrency, cancellationToken) is null)
            {
                return Error.Validation("wht_code.threshold_currency_unknown", "A threshold names its currency.").WithWhy(("currency", request.ThresholdCurrency));
            }
        }

        WhtCode? wht = null;
        if (whtCodeId is { } id)
        {
            wht = await db.WhtCodes.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (wht is null)
            {
                return Error.NotFound("wht_code", id);
            }
        }

        if (await db.WhtCodes.AnyAsync(c => c.Code == code.Value && c.Id != whtCodeId, cancellationToken))
        {
            return Error.Conflict("wht_code.code_taken", $"A withholding code '{code.Value}' already exists.");
        }

        var isNew = wht is null;
        wht ??= new WhtCode { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        wht.Code = code.Value;
        wht.Name = name.Value;
        wht.RatePct = request.RatePct;
        wht.WithholdAt = point.Value;
        wht.ThresholdAmount = request.ThresholdAmount;
        wht.ThresholdCurrency = thresholdCurrency;
        wht.IsActive = request.IsActive;
        wht.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.WhtCodes.Add(wht);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(wht);
    }

    // ------------------------------------------------------------------ shared

    private async Task<Result> CheckReferencesAsync(Guid? groupId, Guid? paymentTermsId, Guid? deliveryTermsId, Guid? postingGroupId, Guid? whtCodeId, string field, CancellationToken cancellationToken)
    {
        if (groupId is { } g && !await db.SupplierGroups.AnyAsync(x => x.Id == g && x.IsActive, cancellationToken))
        {
            return Error.Validation($"{field}.group_unknown", "The supplier group does not exist or is inactive.").WithWhy(("supplierGroupId", g));
        }

        if (paymentTermsId is { } p && !await db.PaymentTerms.AnyAsync(x => x.Id == p && x.IsActive, cancellationToken))
        {
            return Error.Validation($"{field}.payment_terms_unknown", "The payment terms do not exist or are inactive.").WithWhy(("paymentTermsId", p));
        }

        if (deliveryTermsId is { } d && !await db.DeliveryTerms.AnyAsync(x => x.Id == d && x.IsActive, cancellationToken))
        {
            return Error.Validation($"{field}.delivery_terms_unknown", "The delivery terms do not exist or are inactive.").WithWhy(("deliveryTermsId", d));
        }

        if (postingGroupId is { } pg)
        {
            var group = await postingGroups.FindAsync(pg, cancellationToken);
            if (group is null || group.Kind != SupplierPostingGroupKind)
            {
                return Error.Validation($"{field}.posting_group_invalid", "The posting group must be a supplier posting group.").WithWhy(("postingGroupId", pg), ("kind", group?.Kind));
            }
        }

        if (whtCodeId is { } w && !await db.WhtCodes.AnyAsync(x => x.Id == w && x.IsActive, cancellationToken))
        {
            return Error.Validation($"{field}.wht_code_unknown", "The withholding code does not exist or is inactive.").WithWhy(("whtCodeId", w));
        }

        return Result.Success();
    }

    internal sealed record Lookups(IReadOnlyDictionary<Guid, SupplierGroup> Groups, IReadOnlyDictionary<Guid, string> PaymentTerms, IReadOnlyDictionary<Guid, string> DeliveryTerms, IReadOnlyDictionary<Guid, string> WhtCodes, IReadOnlyDictionary<Guid, string> Companies);

    internal async Task<Lookups> LookupsAsync(CancellationToken cancellationToken)
    {
        var groups = await db.SupplierGroups.AsNoTracking().ToDictionaryAsync(static g => g.Id, cancellationToken);
        var payment = await db.PaymentTerms.AsNoTracking().ToDictionaryAsync(static t => t.Id, static t => t.Code, cancellationToken);
        var delivery = await db.DeliveryTerms.AsNoTracking().ToDictionaryAsync(static t => t.Id, static t => t.Code, cancellationToken);
        var wht = await db.WhtCodes.AsNoTracking().ToDictionaryAsync(static c => c.Id, static c => c.Code, cancellationToken);
        var companyRows = await companies.ListAsync(cancellationToken);
        return new Lookups(groups, payment, delivery, wht, companyRows.ToDictionary(static c => c.Id.Value, static c => c.Code));
    }

    /// <summary>The account's own values, else its group's: what documents read.</summary>
    internal static EffectiveTerms Effective(SupplierAccount a, Lookups lookups)
    {
        var group = a.SupplierGroupId is { } g ? lookups.Groups.GetValueOrDefault(g) : null;
        var paymentTermsId = a.PaymentTermsId ?? group?.PaymentTermsId;
        var deliveryTermsId = a.DeliveryTermsId ?? group?.DeliveryTermsId;
        var postingGroupId = a.PostingGroupId ?? group?.PostingGroupId;
        return new EffectiveTerms(
            paymentTermsId,
            paymentTermsId is { } p ? lookups.PaymentTerms.GetValueOrDefault(p) : null,
            deliveryTermsId,
            deliveryTermsId is { } d ? lookups.DeliveryTerms.GetValueOrDefault(d) : null,
            postingGroupId,
            a.WhtCodeId is { } w ? lookups.WhtCodes.GetValueOrDefault(w) : null);
    }

    internal static SupplierAccountSummary Map(SupplierAccount a, Partner p, Lookups lookups) => new(
        a.Id, a.PartnerId, p.Code, p.LegalName.Values, a.CompanyId, lookups.Companies.GetValueOrDefault(a.CompanyId, string.Empty),
        a.SupplierGroupId, a.SupplierGroupId is { } g ? lookups.Groups.GetValueOrDefault(g)?.Code : null,
        a.PaymentTermsId, a.DeliveryTermsId, a.PostingGroupId, a.TaxGroupId, a.WhtCodeId, a.Currency, a.LeadTimeDays, a.PriceTolerancePct, a.QtyTolerancePct, a.RequiresPo,
        a.HoldStatus, a.HoldReason, a.HeldAt, a.IsActive, Effective(a, lookups), a.UpdatedAt);

    private static SupplierGroupSummary Map(SupplierGroup g, int suppliers) => new(g.Id, g.Code, g.Name.Values, g.PostingGroupId, g.PaymentTermsId, g.DeliveryTermsId, g.IsActive, suppliers, g.UpdatedAt);

    private static PaymentTermsSummary Map(PaymentTerms t) => new(t.Id, t.Code, t.Name.Values, t.DueBasis, t.DueDays, t.EarlyDiscountPct, t.EarlyDiscountDays, t.BusinessDaysOnly,
        t.Lines.OrderBy(static l => l.Sequence).Select(static l => new PaymentTermLineSummary(l.Sequence, l.Percentage, l.Days)).ToList(), t.IsSystem, t.IsActive, t.UpdatedAt);

    private static DeliveryTermsSummary Map(DeliveryTerms t) => new(t.Id, t.Code, t.Name.Values, t.IsSystem, t.IsActive, t.UpdatedAt);

    private static WhtCodeSummary Map(WhtCode c) => new(c.Id, c.Code, c.Name.Values, c.RatePct, c.WithholdAt, c.ThresholdAmount, c.ThresholdCurrency, c.IsActive, c.UpdatedAt);
}
