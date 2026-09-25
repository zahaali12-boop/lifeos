using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Pricing.Application;

/// <summary>Which companies' pricing the caller may read or change; pricing records belong to one company each.</summary>
public sealed class PricingAccess(ICurrentPrincipal principal)
{
    /// <summary>The companies the permission reaches, or null for all of them (a workspace-wide grant or the system itself).</summary>
    public IReadOnlySet<Guid>? CompaniesFor(string permission)
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

    public bool MayIn(Guid companyId, string permission) => CompaniesFor(permission) is not { } companies || companies.Contains(companyId);

    public Result Require(Guid companyId, string permission) =>
        MayIn(companyId, permission) ? Result.Success() : Error.Forbidden("pricing.company_forbidden", "You may not do this in that company.").WithWhy(("companyId", companyId), ("permission", permission));
}
