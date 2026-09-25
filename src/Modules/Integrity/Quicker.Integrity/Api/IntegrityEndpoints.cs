using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Integrity.Contracts;
using Quicker.Web;

namespace Quicker.Integrity.Api;

/// <summary>The invariant harness on demand under /api/v1/platform/integrity (the same checks the daily job runs).</summary>
public static class IntegrityEndpoints
{
    public static RouteGroupBuilder MapIntegrityEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var invariants = api.MapGroup("/platform/integrity").WithTags("Platform").RequireAuthorization();
        invariants.MapPost("/run", async (Guid? companyId, IInvariantHarness harness, CancellationToken ct) => TypedResults.Ok(await harness.RunAsync(companyId, ct)))
            .RequirePermission(IntegrityPermissions.Run)
            .WithSummary("Runs the invariant harness for the tenant (or one company) and answers with every check: balanced entries, trial balance zero, derived balances equal the lines, audit chain intact, tenant isolation, gapless numbering");
        return api;
    }
}
