using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Inventory.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Partners.Domain;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>
/// Customer accounts per company (terms, the sales rep, the default warehouse, statements, credit settings and credit
/// holds) and the customer groups they default from. Accounts are read within the companies the member's customer
/// read permission reaches; credit settings change only with the credit permission.
/// </summary>
public sealed class CustomerService(
    PartnersDbContext db,
    ICompanyDirectory companies,
    IPostingGroupDirectory postingGroups,
    IWarehouseDirectory warehouses,
    ICurrentPrincipal principal,
    IClock clock)
{
    public const string CustomerPostingGroupKind = "partner_customer";

    // ------------------------------------------------------------------ customer accounts

    public async Task<IReadOnlyList<CustomerAccountSummary>> ListAccountsAsync(Guid? companyId, string? q, string? creditStatus, Guid? salesRepId, Guid? customerGroupId, bool? mine, bool? isActive, CancellationToken cancellationToken)
    {
        var partners = db.Partners.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim() + "%";
            partners = db.Partners.FromSqlInterpolated($"SELECT * FROM app.ptr_partners WHERE code ILIKE {pattern} OR legal_name_i18n->>'en' ILIKE {pattern} OR legal_name_i18n->>'ar' ILIKE {pattern} OR trade_name_i18n->>'en' ILIKE {pattern} OR trade_name_i18n->>'ar' ILIKE {pattern} OR email ILIKE {pattern}").AsNoTracking();
        }

        var query = from a in db.CustomerAccounts.AsNoTracking()
                    join p in partners on new { a.TenantId, Id = a.PartnerId } equals new { p.TenantId, p.Id }
                    select new { Account = a, Partner = p };
        if (companyId is { } c)
        {
            query = query.Where(x => x.Account.CompanyId == c);
        }

        if (ReadableCompanies() is { } readable)
        {
            var ids = readable.ToArray();
            query = query.Where(x => ids.Contains(x.Account.CompanyId));
        }

        if (!string.IsNullOrWhiteSpace(creditStatus))
        {
            var status = creditStatus.Trim().ToLowerInvariant();
            query = status == "held" ? query.Where(static x => x.Account.CreditStatus != CreditStatuses.Ok) : query.Where(x => x.Account.CreditStatus == status);
        }

        if (salesRepId is { } rep)
        {
            query = query.Where(x => x.Account.SalesRepId == rep);
        }

        if (mine == true)
        {
            var own = await OwnSalesRepIdAsync(cancellationToken);
            query = query.Where(x => x.Account.SalesRepId == own);
        }

        if (customerGroupId is { } group)
        {
            query = query.Where(x => x.Account.CustomerGroupId == group);
        }

        if (isActive is { } active)
        {
            query = query.Where(x => x.Account.IsActive == active);
        }

        var rows = await query.OrderBy(static x => x.Partner.Code).ThenBy(static x => x.Account.CompanyId).Take(500).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(cancellationToken);
        return rows.Select(x => Map(x.Account, x.Partner, lookups)).ToList();
    }

    public async Task<IReadOnlyList<CustomerAccountSummary>> ListAccountsOfPartnerAsync(Guid partnerId, CancellationToken cancellationToken)
    {
        var partner = await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        if (partner is null)
        {
            return [];
        }

        var query = db.CustomerAccounts.AsNoTracking().Where(a => a.PartnerId == partnerId);
        if (ReadableCompanies() is { } readable)
        {
            var ids = readable.ToArray();
            query = query.Where(a => ids.Contains(a.CompanyId));
        }

        var accounts = await query.OrderBy(static a => a.CreatedAt).ToListAsync(cancellationToken);
        var lookups = await LookupsAsync(cancellationToken);
        return accounts.Select(a => Map(a, partner, lookups)).ToList();
    }

    public async Task<Result<CustomerAccountSummary>> SaveAccountAsync(Guid partnerId, Guid companyId, SaveCustomerAccountRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var partner = await db.Partners.SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        if (partner is null)
        {
            return Error.NotFound(PartnerService.EntityType, partnerId);
        }

        // Two permissions keep an account, each its own fields: the customer permission its terms, rep, warehouse and
        // statements; the credit permission its limit, exposure basis and overdue block.
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var mayKeep = MayManageIn(companyId);
        var mayCredit = MayManageIn(companyId, PartnersPermissions.CreditManage);
        if (company is null || (!mayKeep && !mayCredit))
        {
            return Error.NotFound("company", companyId);
        }

        var currency = (request.Currency ?? company.FunctionalCurrency.Code).Trim().ToUpperInvariant();
        if (await companies.FindCurrencyAsync(currency, cancellationToken) is null)
        {
            return Error.Validation("customer.currency_unknown", "The currency is not an ISO 4217 code the system knows.").WithWhy(("currency", request.Currency));
        }

        var checks = await CheckReferencesAsync(request.CustomerGroupId, request.PaymentTermsId, request.DeliveryTermsId, request.PostingGroupId, "customer", cancellationToken);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        if (request.SalesRepId is { } repId)
        {
            var rep = await db.SalesReps.AsNoTracking().SingleOrDefaultAsync(r => r.Id == repId, cancellationToken);
            if (rep is null || !rep.IsActive)
            {
                return Error.Validation("customer.sales_rep_unknown", "The sales rep does not exist or is inactive.").WithWhy(("salesRepId", repId));
            }

            if (rep.CompanyId is { } repCompany && repCompany != companyId)
            {
                return Error.Validation("customer.sales_rep_other_company", "The sales rep sells for another company.").WithWhy(("salesRep", rep.Code), ("companyId", companyId));
            }
        }

        if (request.DefaultWarehouseId is { } warehouseId)
        {
            var warehouse = await warehouses.FindAsync(warehouseId, cancellationToken);
            if (warehouse is null || !warehouse.IsActive || warehouse.CompanyId != companyId)
            {
                return Error.Validation("customer.warehouse_invalid", "The default warehouse must be an active warehouse of the company.").WithWhy(("warehouseId", warehouseId));
            }
        }

        var basis = Validation.OneOf(request.CreditExposureBasis, "customer.credit_exposure_basis", CreditExposureBases.All);
        if (basis.IsFailure)
        {
            return basis.Error!;
        }

        var frequency = Validation.OneOf(request.StatementFrequency, "customer.statement_frequency", StatementFrequencies.All);
        if (frequency.IsFailure)
        {
            return frequency.Error!;
        }

        if (request.CreditLimit is < 0m)
        {
            return Error.Validation("customer.credit_limit_invalid", "A credit limit is zero or more; leave it empty for no limit.");
        }

        if (request.CreditLimit is { } limit && decimal.Round(limit, company.FunctionalCurrency.MinorUnits) != limit)
        {
            return Error.Validation("customer.credit_limit_precision", "A credit limit is in whole minor units of the company's currency.").WithWhy(("creditLimit", limit), ("currency", company.FunctionalCurrency.Code));
        }

        if (request.OverdueBlockDays is < 0)
        {
            return Error.Validation("customer.overdue_block_days_invalid", "Overdue days that block are zero or more; leave them empty for no block.");
        }

        var account = await db.CustomerAccounts.SingleOrDefaultAsync(a => a.PartnerId == partnerId && a.CompanyId == companyId, cancellationToken);
        var isNew = account is null;
        var keptChanged = isNew
            || account!.CustomerGroupId != request.CustomerGroupId || account.PaymentTermsId != request.PaymentTermsId || account.DeliveryTermsId != request.DeliveryTermsId
            || account.PostingGroupId != request.PostingGroupId || account.TaxGroupId != request.TaxGroupId || account.SalesRepId != request.SalesRepId
            || account.DefaultWarehouseId != request.DefaultWarehouseId || account.Currency != currency || account.StatementFrequency != frequency.Value || account.IsActive != request.IsActive;
        if (keptChanged && !mayKeep)
        {
            return Error.Forbidden("customer.manage_forbidden", "Registering a customer or changing its terms needs the customer permission.").WithWhy(("permission", PartnersPermissions.CustomerManage));
        }

        var creditChanged = isNew
            ? request.CreditLimit is not null || basis.Value != CreditExposureBases.OpenArPlusOrders || request.OverdueBlockDays is not null
            : account!.CreditLimit != request.CreditLimit || account.CreditExposureBasis != basis.Value || account.OverdueBlockDays != request.OverdueBlockDays;
        if (creditChanged && !mayCredit)
        {
            return Error.Forbidden("customer.credit_forbidden", "Changing a customer's credit settings needs the credit permission.").WithWhy(("permission", PartnersPermissions.CreditManage));
        }

        account ??= new CustomerAccount { Id = Guid.CreateVersion7(), PartnerId = partnerId, CompanyId = companyId, CreatedAt = clock.UtcNow };
        account.CustomerGroupId = request.CustomerGroupId;
        account.PaymentTermsId = request.PaymentTermsId;
        account.DeliveryTermsId = request.DeliveryTermsId;
        account.PostingGroupId = request.PostingGroupId;
        account.TaxGroupId = request.TaxGroupId;
        account.SalesRepId = request.SalesRepId;
        account.DefaultWarehouseId = request.DefaultWarehouseId;
        account.Currency = currency;
        account.CreditLimit = request.CreditLimit;
        account.CreditExposureBasis = basis.Value;
        account.OverdueBlockDays = request.OverdueBlockDays;
        account.StatementFrequency = frequency.Value;
        account.IsActive = request.IsActive;
        account.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.CustomerAccounts.Add(account);
        }

        if (!partner.IsCustomer)
        {
            // Registering a customer account gives the partner the role; the flag is what lists and documents read.
            partner.IsCustomer = true;
            partner.UpdatedAt = clock.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(account, partner, await LookupsAsync(cancellationToken));
    }

    public async Task<Result<CustomerAccountSummary>> SetCreditStatusAsync(Guid partnerId, Guid companyId, CreditStatusRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var status = Validation.OneOf(request.Status, "customer.credit_status", CreditStatuses.Values);
        if (status.IsFailure)
        {
            return status.Error!;
        }

        if (status.Value == CreditStatuses.Ok)
        {
            return await ReleaseCreditAsync(partnerId, companyId, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return Error.Validation("customer.credit_reason_required", "A credit hold or block says why.");
        }

        var (account, partner) = await AccountAsync(partnerId, companyId, cancellationToken);
        if (account is null || partner is null)
        {
            return Error.NotFound("customer_account", partnerId);
        }

        account.CreditStatus = status.Value;
        account.CreditStatusReason = request.Reason.Trim();
        account.CreditStatusAt = clock.UtcNow;
        account.CreditStatusBy = principal.Principal?.UserId.Value;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(account, partner, await LookupsAsync(cancellationToken));
    }

    public async Task<Result<CustomerAccountSummary>> ReleaseCreditAsync(Guid partnerId, Guid companyId, CancellationToken cancellationToken)
    {
        var (account, partner) = await AccountAsync(partnerId, companyId, cancellationToken);
        if (account is null || partner is null)
        {
            return Error.NotFound("customer_account", partnerId);
        }

        account.CreditStatus = CreditStatuses.Ok;
        account.CreditStatusReason = null;
        account.CreditStatusAt = clock.UtcNow;
        account.CreditStatusBy = principal.Principal?.UserId.Value;
        account.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(account, partner, await LookupsAsync(cancellationToken));
    }

    private async Task<(CustomerAccount? Account, Partner? Partner)> AccountAsync(Guid partnerId, Guid companyId, CancellationToken cancellationToken)
    {
        if (!MayManageIn(companyId, PartnersPermissions.CreditManage))
        {
            return (null, null);
        }

        var account = await db.CustomerAccounts.SingleOrDefaultAsync(a => a.PartnerId == partnerId && a.CompanyId == companyId, cancellationToken);
        var partner = account is null ? null : await db.Partners.AsNoTracking().SingleOrDefaultAsync(p => p.Id == partnerId, cancellationToken);
        return (account, partner);
    }

    // ------------------------------------------------------------------ customer groups

    public async Task<IReadOnlyList<CustomerGroupSummary>> ListGroupsAsync(CancellationToken cancellationToken)
    {
        var groups = await db.CustomerGroups.AsNoTracking().OrderBy(static g => g.Code).ToListAsync(cancellationToken);
        var counts = await db.CustomerAccounts.AsNoTracking().Where(static a => a.CustomerGroupId != null).GroupBy(static a => a.CustomerGroupId!.Value).Select(static g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(static g => g.Key, static g => g.Count, cancellationToken);
        return groups.Select(g => Map(g, counts.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<Result<CustomerGroupSummary>> SaveGroupAsync(Guid? groupId, SaveCustomerGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.Code(request.Code, "customer_group");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, "customer_group");
        if (name.IsFailure)
        {
            return name.Error!;
        }

        var checks = await CheckReferencesAsync(null, request.PaymentTermsId, request.DeliveryTermsId, request.PostingGroupId, "customer_group", cancellationToken);
        if (checks.IsFailure)
        {
            return checks.Error!;
        }

        CustomerGroup? group = null;
        if (groupId is { } id)
        {
            group = await db.CustomerGroups.SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return Error.NotFound("customer_group", id);
            }
        }

        if (await db.CustomerGroups.AnyAsync(g => g.Code == code.Value && g.Id != groupId, cancellationToken))
        {
            return Error.Conflict("customer_group.code_taken", $"A customer group with code '{code.Value}' already exists.");
        }

        var isNew = group is null;
        group ??= new CustomerGroup { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
        group.Code = code.Value;
        group.Name = name.Value;
        group.PostingGroupId = request.PostingGroupId;
        group.PaymentTermsId = request.PaymentTermsId;
        group.DeliveryTermsId = request.DeliveryTermsId;
        group.IsActive = request.IsActive;
        group.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.CustomerGroups.Add(group);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Map(group, await db.CustomerAccounts.CountAsync(a => a.CustomerGroupId == group.Id, cancellationToken));
    }

    // ------------------------------------------------------------------ shared

    /// <summary>The companies the member's customer read permission reaches, or null for all of them.</summary>
    internal IReadOnlySet<Guid>? ReadableCompanies() => CompaniesFor(PartnersPermissions.CustomerRead);

    internal IReadOnlySet<Guid>? CompaniesFor(string permission)
    {
        if (principal.Principal is not { } current)
        {
            return null;
        }

        var scopes = current.ScopesFor(permission);
        if (scopes is null)
        {
            return new HashSet<Guid>();
        }

        return scopes.CompanyIds.Count == 0 ? null : scopes.CompanyIds;
    }

    internal bool MayManageIn(Guid companyId, string permission = PartnersPermissions.CustomerManage) =>
        principal.Principal is not { } current || current.ScopesFor(permission)?.AllowsCompany(companyId) == true;

    /// <summary>The sales rep the signed-in member is, if any (an empty id otherwise, which matches nothing).</summary>
    internal async Task<Guid> OwnSalesRepIdAsync(CancellationToken cancellationToken)
    {
        if (principal.Principal is not { } current)
        {
            return Guid.Empty;
        }

        var membership = current.MembershipId.Value;
        return await db.SalesReps.AsNoTracking().Where(r => r.MembershipId == membership).Select(static r => r.Id).SingleOrDefaultAsync(cancellationToken);
    }

    private async Task<Result> CheckReferencesAsync(Guid? groupId, Guid? paymentTermsId, Guid? deliveryTermsId, Guid? postingGroupId, string field, CancellationToken cancellationToken)
    {
        if (groupId is { } g && !await db.CustomerGroups.AnyAsync(x => x.Id == g && x.IsActive, cancellationToken))
        {
            return Error.Validation($"{field}.group_unknown", "The customer group does not exist or is inactive.").WithWhy(("customerGroupId", g));
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
            if (group is null || group.Kind != CustomerPostingGroupKind)
            {
                return Error.Validation($"{field}.posting_group_invalid", "The posting group must be a customer posting group.").WithWhy(("postingGroupId", pg), ("kind", group?.Kind));
            }
        }

        return Result.Success();
    }

    internal sealed record Lookups(
        IReadOnlyDictionary<Guid, CustomerGroup> Groups,
        IReadOnlyDictionary<Guid, string> PaymentTerms,
        IReadOnlyDictionary<Guid, string> DeliveryTerms,
        IReadOnlyDictionary<Guid, string> SalesReps,
        IReadOnlyDictionary<Guid, (string Code, string Currency)> Companies);

    internal async Task<Lookups> LookupsAsync(CancellationToken cancellationToken)
    {
        var groups = await db.CustomerGroups.AsNoTracking().ToDictionaryAsync(static g => g.Id, cancellationToken);
        var payment = await db.PaymentTerms.AsNoTracking().ToDictionaryAsync(static t => t.Id, static t => t.Code, cancellationToken);
        var delivery = await db.DeliveryTerms.AsNoTracking().ToDictionaryAsync(static t => t.Id, static t => t.Code, cancellationToken);
        var reps = await db.SalesReps.AsNoTracking().ToDictionaryAsync(static r => r.Id, static r => r.Code, cancellationToken);
        var companyRows = await companies.ListAsync(cancellationToken);
        return new Lookups(groups, payment, delivery, reps, companyRows.ToDictionary(static c => c.Id.Value, static c => (c.Code, c.FunctionalCurrency.Code)));
    }

    /// <summary>The account's own values, else its group's: what documents read.</summary>
    internal static EffectiveCustomerTerms Effective(CustomerAccount a, Lookups lookups)
    {
        var group = a.CustomerGroupId is { } g ? lookups.Groups.GetValueOrDefault(g) : null;
        var paymentTermsId = a.PaymentTermsId ?? group?.PaymentTermsId;
        var deliveryTermsId = a.DeliveryTermsId ?? group?.DeliveryTermsId;
        return new EffectiveCustomerTerms(
            paymentTermsId,
            paymentTermsId is { } p ? lookups.PaymentTerms.GetValueOrDefault(p) : null,
            deliveryTermsId,
            deliveryTermsId is { } d ? lookups.DeliveryTerms.GetValueOrDefault(d) : null,
            a.PostingGroupId ?? group?.PostingGroupId);
    }

    internal static CustomerAccountSummary Map(CustomerAccount a, Partner p, Lookups lookups)
    {
        var company = lookups.Companies.GetValueOrDefault(a.CompanyId);
        return new CustomerAccountSummary(
            a.Id, a.PartnerId, p.Code, p.LegalName.Values, a.CompanyId, company.Code ?? string.Empty, company.Currency ?? string.Empty,
            a.CustomerGroupId, a.CustomerGroupId is { } g ? lookups.Groups.GetValueOrDefault(g)?.Code : null,
            a.PaymentTermsId, a.DeliveryTermsId, a.PostingGroupId, a.TaxGroupId,
            a.SalesRepId, a.SalesRepId is { } r ? lookups.SalesReps.GetValueOrDefault(r) : null,
            a.DefaultWarehouseId, a.Currency, a.CreditLimit, a.CreditExposureBasis, a.OverdueBlockDays,
            a.CreditStatus, a.CreditStatusReason, a.CreditStatusAt, a.StatementFrequency, a.DunningLevel, a.IsActive, Effective(a, lookups), a.UpdatedAt);
    }

    private static CustomerGroupSummary Map(CustomerGroup g, int customers) => new(g.Id, g.Code, g.Name.Values, g.PostingGroupId, g.PaymentTermsId, g.DeliveryTermsId, g.IsActive, customers, g.UpdatedAt);
}
