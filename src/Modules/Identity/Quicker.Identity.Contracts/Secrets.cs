using Quicker.Kernel.Ids;

namespace Quicker.Identity.Contracts;

/// <summary>Column-level secret protection (AES-GCM under the platform key, ADR-0025) for modules that store credentials: webhook secrets, provider keys.</summary>
public interface ISecretProtector
{
    string ProtectString(string value);

    string UnprotectString(string protectedBase64);
}

/// <summary>A member of the current tenant as other modules need it: to notify, to name, to email.</summary>
public sealed record MemberInfo(MembershipId MembershipId, UserId UserId, string Email, string DisplayName, string Locale, string TimeZone, bool IsActive);

public interface IMemberDirectory
{
    Task<MemberInfo?> FindAsync(MembershipId membershipId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemberInfo>> ListActiveAsync(CancellationToken cancellationToken = default);
}
