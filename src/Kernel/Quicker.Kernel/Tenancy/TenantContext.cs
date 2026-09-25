using Quicker.Kernel.Ids;

namespace Quicker.Kernel.Tenancy;

/// <summary>
/// Who is acting, in which tenant, for which request. Set once per unit of work from the authenticated principal
/// (never from request bodies) and used by persistence to set the database session variables that drive
/// row-level security (ADR-0004).
/// </summary>
public sealed record TenantContext(
    TenantId TenantId,
    MembershipId? MembershipId,
    UserId? UserId,
    string ActorType,
    string ActorDisplay,
    string RequestId,
    string? CorrelationId,
    string Language)
{
    public const string ActorUser = "user";
    public const string ActorApiKey = "api_key";
    public const string ActorSystem = "system";

    public static TenantContext System(TenantId tenantId, string requestId, string language = "en") =>
        new(tenantId, null, null, ActorSystem, "system", requestId, null, language);

    /// <summary>No tenant: used for sign-up, login and other control-plane work. RLS hides every tenant table.</summary>
    public static TenantContext Anonymous(string requestId, string language = "en") =>
        new(new TenantId(Guid.Empty), null, null, ActorAnonymous, "anonymous", requestId, null, language);

    public const string ActorAnonymous = "anonymous";

    /// <summary>Network address of the client, when the context comes from an HTTP request (audit only).</summary>
    public string? ClientIp { get; init; }

    /// <summary>User agent of the client, when the context comes from an HTTP request (audit only).</summary>
    public string? UserAgent { get; init; }

    public bool IsAnonymous => TenantId.Value == Guid.Empty;
}

/// <summary>Ambient access to the current context; implemented with AsyncLocal by the hosting layer.</summary>
public interface ITenantContextAccessor
{
    TenantContext? Current { get; }

    /// <summary>The current context, or an exception when none is set (a programming error in the host).</summary>
    TenantContext Required { get; }

    IDisposable Use(TenantContext context);
}

public sealed class TenantContextAccessor : ITenantContextAccessor
{
    private static readonly AsyncLocal<TenantContext?> Ambient = new();

    public TenantContext? Current => Ambient.Value;

    public TenantContext Required => Current ?? throw new InvalidOperationException("No tenant context is set for this unit of work.");

    public IDisposable Use(TenantContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = Ambient.Value;
        Ambient.Value = context;
        return new Scope(() => Ambient.Value = previous);
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            _onDispose?.Invoke();
            _onDispose = null;
        }
    }
}
