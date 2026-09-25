using System.Text.Json;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Tenancy;

/// <summary>Control-plane access to tenants; works inside the current unit of work (control tables carry no RLS).</summary>
public sealed partial class TenantDirectory(IUnitOfWorkAccessor unitOfWork) : ITenantDirectory, ITenantProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class TenantRow
    {
        public Guid Id { get; set; }

        public string Slug { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Tier { get; set; } = string.Empty;

        public string Region { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public string DefaultLanguage { get; set; } = string.Empty;

        public long PermissionsEpoch { get; set; }

        public string Settings { get; set; } = "{}";
    }

    private const string SelectColumns = "id, slug, name, tier, region, status, default_language, permissions_epoch, settings::text AS settings";

    public async Task<TenantInfo?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleOrDefaultAsync<TenantRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM control.tenants WHERE slug = @slug", new { slug = slug.Trim().ToLowerInvariant() }, uow.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task<TenantInfo?> FindByIdAsync(TenantId id, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        var row = await uow.Connection.QuerySingleOrDefaultAsync<TenantRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM control.tenants WHERE id = @id", new { id = id.Value }, uow.Transaction, cancellationToken: cancellationToken));
        return row is null ? null : Map(row);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<TenantRow>(new CommandDefinition(
            $"SELECT {SelectColumns} FROM control.tenants ORDER BY created_at", transaction: uow.Transaction, cancellationToken: cancellationToken));
        return rows.Select(Map).ToList();
    }

    public async Task BumpPermissionsEpochAsync(TenantId id, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE control.tenants SET permissions_epoch = permissions_epoch + 1 WHERE id = @id", new { id = id.Value }, uow.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<Result<TenantInfo>> ProvisionAsync(ProvisionTenantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var slug = request.Slug.Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(slug))
        {
            return Error.Validation("tenant.slug_invalid", "Slug must be 3-63 characters of lower-case letters, digits and hyphens.");
        }

        if (ReservedSlugs.Contains(slug))
        {
            return Error.Validation("tenant.slug_reserved", $"'{slug}' is reserved.");
        }

        var uow = unitOfWork.Current;
        var exists = await uow.Connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM control.tenants WHERE slug = @slug)", new { slug }, uow.Transaction, cancellationToken: cancellationToken));
        if (exists)
        {
            return Error.Conflict("tenant.slug_taken", $"'{slug}' is already in use.");
        }

        var id = request.Id is { } fixedId ? new TenantId(fixedId) : TenantId.New();
        await uow.Connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO control.tenants (id, slug, name, tier, region, status, default_language, settings)
            VALUES (@id, @slug, @name, @tier, @region, 'provisioning', @language, @settings::jsonb)
            """, new
        {
            id = id.Value,
            slug,
            name = request.Name.Trim(),
            tier = request.Tier,
            region = request.Region,
            language = request.DefaultLanguage,
            settings = JsonSerializer.Serialize(TenantSecurityPolicy.Default, JsonOptions),
        }, uow.Transaction, cancellationToken: cancellationToken));

        return (await FindByIdAsync(id, cancellationToken))!;
    }

    public async Task ActivateAsync(TenantId id, CancellationToken cancellationToken = default)
    {
        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE control.tenants SET status = 'active' WHERE id = @id AND status = 'provisioning'", new { id = id.Value }, uow.Transaction, cancellationToken: cancellationToken));
    }

    public async Task<Result> UpdatePolicyAsync(TenantId id, TenantSecurityPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        // A step-up window of zero would lock every sensitive action, this one included; lockout needs a sane floor.
        if (policy.PasswordMinLength is < 8 or > 128 || policy.AccessTokenMinutes is < 1 or > 60 || policy.SessionLifetimeHours is < 1 or > 24 * 90
            || policy.StepUpWindowMinutes is < 1 or > 240 || policy.LockoutThreshold is < 3 or > 100 || policy.LockoutMinutes is < 1 or > 24 * 60)
        {
            return Error.Validation("tenant.policy_invalid", "Policy values are out of range.");
        }

        var invalid = IpAllowlists.Invalid(policy.IpAllowlist);
        if (invalid.Count > 0 || (policy.IpAllowlist?.Count ?? 0) > IpAllowlists.MaxEntries)
        {
            return Error.Validation("tenant.policy_ip_invalid", $"Each allowed network is an address or a CIDR range, at most {IpAllowlists.MaxEntries}.").WithWhy(("invalid", invalid), ("max", IpAllowlists.MaxEntries));
        }

        policy = policy with { IpAllowlist = policy.IpAllowlist is { Count: > 0 } list ? list.Select(static e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList() : null };

        var uow = unitOfWork.Current;
        await uow.Connection.ExecuteAsync(new CommandDefinition(
            "UPDATE control.tenants SET settings = settings || @settings::jsonb WHERE id = @id",
            new { id = id.Value, settings = JsonSerializer.Serialize(policy, JsonOptions) }, uow.Transaction, cancellationToken: cancellationToken));
        return Result.Success();
    }

    private static TenantInfo Map(TenantRow row)
    {
        var policy = JsonSerializer.Deserialize<TenantSecurityPolicy>(row.Settings, JsonOptions) ?? TenantSecurityPolicy.Default;
        return new TenantInfo(new TenantId(row.Id), row.Slug, row.Name, row.Tier, row.Region, row.Status, row.DefaultLanguage, row.PermissionsEpoch, policy);
    }

    private static readonly HashSet<string> ReservedSlugs = new(StringComparer.Ordinal) { "www", "api", "app", "admin", "auth", "login", "static", "docs", "status", "quicker" };

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex SlugPattern();
}

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddScoped<TenantDirectory>();
        services.AddScoped<ITenantDirectory>(static sp => sp.GetRequiredService<TenantDirectory>());
        services.AddScoped<ITenantProvisioner>(static sp => sp.GetRequiredService<TenantDirectory>());
        return services;
    }
}
