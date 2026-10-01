using Microsoft.EntityFrameworkCore;
using Quicker.Identity.Contracts;
using Quicker.Identity.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Persistence;

namespace Quicker.Identity.Application;

/// <summary>Members of the current tenant for other modules (notifications, mentions, assignments).</summary>
public sealed class MemberDirectory(IdentityDbContext db, IUnitOfWorkAccessor unitOfWork) : IMemberDirectory
{
    private Guid TenantId => unitOfWork.Current.Context.TenantId.Value;

    public async Task<MemberInfo?> FindAsync(MembershipId membershipId, CancellationToken cancellationToken = default)
    {
        var membership = await db.Memberships.Include(static m => m.User).SingleOrDefaultAsync(m => m.TenantId == TenantId && m.Id == membershipId.Value, cancellationToken);
        return membership is null ? null : Map(membership);
    }

    public async Task<IReadOnlyList<MemberInfo>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        var memberships = await db.Memberships.Include(static m => m.User).Where(m => m.TenantId == TenantId && m.Status == "active").OrderBy(static m => m.User.Email).ToListAsync(cancellationToken);
        return memberships.Select(Map).ToList();
    }

    private static MemberInfo Map(Domain.TenantMembership m) =>
        new(new MembershipId(m.Id), new UserId(m.UserId), m.User.Email, m.User.DisplayName, m.User.Locale, m.User.TimeZone, m.Status == "active" && m.User.Status == "active");
}
