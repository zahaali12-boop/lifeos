using Microsoft.EntityFrameworkCore;
using Quicker.Kernel.Results;
using Quicker.Partners.Contracts;
using Quicker.Partners.Persistence;

namespace Quicker.Partners.Application;

/// <summary>What sales, receivables and commissions read from the customer master (roadmap 5.1).</summary>
public sealed class CustomerDirectory(PartnersDbContext db, CustomerService customers, SalesSetupService salesSetup) : ICustomerDirectory
{
    public async Task<CustomerTermsInfo?> FindCustomerAsync(Guid companyId, Guid partnerId, CancellationToken cancellationToken = default)
    {
        var account = await db.CustomerAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.PartnerId == partnerId && a.CompanyId == companyId, cancellationToken);
        if (account is null)
        {
            return null;
        }

        var partner = await db.Partners.AsNoTracking().SingleAsync(p => p.Id == partnerId, cancellationToken);
        var lookups = await customers.LookupsAsync(cancellationToken);
        var effective = CustomerService.Effective(account, lookups);
        return new CustomerTermsInfo(partner.Id, partner.Code, partner.LegalName, account.CompanyId, account.Currency, account.CustomerGroupId,
            effective.PaymentTermsId, effective.PaymentTermsCode, effective.DeliveryTermsId, effective.DeliveryTermsCode, effective.PostingGroupId, account.TaxGroupId,
            account.SalesRepId, account.DefaultWarehouseId, account.CreditLimit, account.CreditExposureBasis, account.OverdueBlockDays,
            account.CreditStatus, account.CreditStatusReason, account.StatementFrequency, account.DunningLevel, account.IsActive && partner.IsActive);
    }

    public async Task<Result<CustomerTermsInfo>> EnsureCustomerAsync(Guid companyId, Guid partnerId, string purpose, CancellationToken cancellationToken = default)
    {
        var terms = await FindCustomerAsync(companyId, partnerId, cancellationToken);
        if (terms is null)
        {
            return Error.Validation("customer.not_registered", "The partner is not a customer of this company.").WithWhy(("partnerId", partnerId), ("companyId", companyId));
        }

        if (!terms.IsActive)
        {
            return Error.Conflict("customer.inactive", "The customer account is inactive.").WithWhy(("partner", terms.PartnerCode));
        }

        // Money owed is always taken in; everything that adds to what is owed stops at a block.
        if (terms.CreditStatus == CreditStatuses.Blocked && purpose != CustomerPurposes.Receipt)
        {
            return Error.Conflict("customer.credit_blocked", "The customer is blocked for credit.").WithWhy(("partner", terms.PartnerCode), ("reason", terms.CreditStatusReason), ("purpose", purpose));
        }

        return terms;
    }

    public async Task<SalesRepInfo?> FindSalesRepAsync(Guid salesRepId, CancellationToken cancellationToken = default)
    {
        var r = await db.SalesReps.AsNoTracking().SingleOrDefaultAsync(x => x.Id == salesRepId, cancellationToken);
        return r is null ? null : new SalesRepInfo(r.Id, r.Code, r.Name, r.MembershipId, r.PartnerId, r.CompanyId, r.CommissionPlanId, r.IsActive);
    }

    public Task<Result<CommissionQuote>> QuoteCommissionAsync(Guid planId, Guid? itemCategoryId, Guid? customerGroupId, decimal periodToDate, decimal basis, CancellationToken cancellationToken = default) =>
        salesSetup.QuoteAsync(planId, itemCategoryId, customerGroupId, periodToDate, basis, cancellationToken);
}
