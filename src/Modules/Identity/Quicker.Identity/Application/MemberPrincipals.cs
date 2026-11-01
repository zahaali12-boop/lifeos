using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Identity.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Identity.Application;

/// <summary>Builds a member's principal from their membership and effective grants, as a request would, for work that runs on their behalf (a scheduled report).</summary>
public sealed class MemberPrincipals(IdentityDbContext db, RoleService roles, IUnitOfWorkAccessor unitOfWork, IClock clock) : IMemberPrincipals
{
    public async Task<Principal?> ForMemberAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        var tenantId = unitOfWork.Current.Context.TenantId;
        var membership = await db.Memberships.AsNoTracking().Include(static m => m.User)
            .SingleOrDefaultAsync(m => m.Id == membershipId.Value && m.TenantId == tenantId.Value, cancellationToken);
        if (membership is null || !membership.IsActive || !membership.User.IsActive)
        {
            return null;
        }

        var (grants, fieldRules, documentRules, roleIds) = await roles.EffectiveAsync(membership.Id, cancellationToken);
        return new Principal(
            tenantId, new UserId(membership.UserId), membershipId,
            membership.User.Email, membership.User.DisplayName, membership.User.Locale,
            membership.IsOwner, membership.User.IsPlatformOperator, TenantContext.ActorUser,
            grants, fieldRules, documentRules, clock.UtcNow, "schedule", roleIds);
    }
}
