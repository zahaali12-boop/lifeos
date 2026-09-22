using System.Diagnostics;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Audit.Contracts;
using Quicker.Identity.Application;
using Quicker.Identity.Domain;
using Quicker.Identity.Persistence;
using Quicker.Identity.Security;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Tenancy;
using Quicker.Kernel.Time;
using Quicker.Organization.Application;
using Quicker.Organization.Domain;
using Quicker.Organization.Persistence;
using Quicker.Persistence;
using Quicker.Tenancy.Contracts;

namespace Quicker.Migrator.Demo;

public sealed record DemoSeedResult(Guid TenantId, bool Created, TimeSpan Elapsed, int Companies, int Branches, int Users, int Rates);

/// <summary>
/// Builds the demo tenant in one transaction through the modules' own services (ADR-0029). With
/// <c>reseed</c> the existing demo tenant is purged first; without it an existing tenant is left untouched so
/// <c>make up</c> keeps whatever the demo user changed.
/// </summary>
public static class DemoSeeder
{
    public static async Task<DemoSeedResult> SeedAsync(string ownerConnection, string appConnection, bool reseed, IClock? clock = null, CancellationToken cancellationToken = default)
    {
        clock ??= SystemClock.Instance;
        var stopwatch = Stopwatch.StartNew();

        await using (var owner = new NpgsqlConnection(ownerConnection))
        {
            await owner.OpenAsync(cancellationToken);
            var existing = await owner.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM control.tenants WHERE slug = @slug", new { slug = DemoData.Slug }, cancellationToken: cancellationToken));
            if (existing is { } present && !reseed)
            {
                return new DemoSeedResult(present, Created: false, stopwatch.Elapsed, 0, 0, 0, 0);
            }

            if (existing is { } stale)
            {
                await TenantPurge.PurgeAsync(ownerConnection, stale, DemoData.EmailDomain, cancellationToken);
            }

            await TenantPurge.RemoveOrphanUsersAsync(owner, null, DemoData.EmailDomain, cancellationToken);
        }

        using var host = DemoHost.Build(ownerConnection, appConnection, clock);
        await using var scope = host.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var ambient = services.GetRequiredService<ITenantContextAccessor>();
        var requestId = "demo-seed-" + Guid.CreateVersion7().ToString("N");
        await using var unitOfWork = await services.GetRequiredService<IUnitOfWorkFactory>().BeginAsync(TenantContext.Anonymous(requestId), cancellationToken: cancellationToken);
        services.GetRequiredService<IUnitOfWorkAccessor>().Set(unitOfWork);
        using var anonymous = ambient.Use(unitOfWork.Context);

        // The tenant and its people: the control plane, before any tenant row exists.
        var provisioner = services.GetRequiredService<ITenantProvisioner>();
        var tenant = Require(await provisioner.ProvisionAsync(new ProvisionTenantRequest(DemoData.Slug, DemoData.TenantName, "en", Id: DemoData.TenantId), cancellationToken));
        var identity = services.GetRequiredService<IdentityDbContext>();
        var now = clock.UtcNow;
        var passwordHash = services.GetRequiredService<PasswordHasher>().Hash(DemoData.Password);
        var memberships = new Dictionary<string, TenantMembership>(StringComparer.Ordinal);
        foreach (var person in DemoData.Users)
        {
            var user = new User
            {
                Id = DemoData.UserId(person),
                Email = person.Email,
                DisplayName = person.DisplayName,
                PasswordHash = passwordHash,
                PasswordUpdatedAt = now,
                Locale = person.Locale,
                TimeZone = DemoData.BaghdadTimeZone,
                DigitStyle = person.DigitStyle,
                EmailVerifiedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            var membership = new TenantMembership
            {
                Id = DemoData.MembershipId(person),
                TenantId = tenant.Id.Value,
                UserId = user.Id,
                Status = "active",
                IsOwner = ReferenceEquals(person, DemoData.Owner),
                AcceptedAt = now,
                CreatedAt = now,
                UpdatedAt = now,
                User = user,
            };
            identity.Users.Add(user);
            identity.Memberships.Add(membership);
            memberships[person.Local] = membership;
        }

        await identity.SaveChangesAsync(cancellationToken);

        // From here on the owner acts inside the tenant, exactly as sign-up does.
        var ownerMembership = memberships[DemoData.Owner.Local];
        await unitOfWork.SwitchTenantAsync(tenant.Id, new UserId(ownerMembership.UserId), new MembershipId(ownerMembership.Id), DemoData.Owner.Email, cancellationToken);
        using var inTenant = ambient.Use(unitOfWork.Context);
        var roles = services.GetRequiredService<RoleService>();
        var ownerRole = await roles.SeedDefaultsAsync(cancellationToken);
        Require(await roles.AssignAsync(ownerMembership.Id, new AssignRoleRequest(ownerRole.Id, AcknowledgeWarnings: true), ownerMembership.UserId, cancellationToken));
        foreach (var step in services.GetRequiredService<IEnumerable<ITenantSetupStep>>())
        {
            await step.SetUpAsync(tenant.Id, "en", cancellationToken);
        }

        await provisioner.ActivateAsync(tenant.Id, cancellationToken);

        // Companies, branches and the currencies each one trades in.
        var companies = services.GetRequiredService<CompanyService>();
        var companyIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var branches = 0;
        foreach (var company in DemoData.Companies)
        {
            var created = Require(await companies.CreateCompanyAsync(new SaveCompanyRequest(
                company.Code,
                Bilingual(company.LegalEn, company.LegalAr),
                company.Country,
                company.FunctionalCurrency,
                company.TimeZone,
                TradeName: Bilingual(company.TradeEn, company.TradeAr),
                ReportingCurrency: company.ReportingCurrency,
                RegistrationNumbers: new Dictionary<string, string>(StringComparer.Ordinal) { ["tax_id"] = company.TaxId, ["commercial_registration"] = company.Registration },
                Address: new Dictionary<string, string>(StringComparer.Ordinal) { ["street"] = company.Street, ["city"] = company.City, ["country"] = company.Country }), cancellationToken));
            companyIds[company.Code] = created.Id;
            foreach (var branch in company.Branches)
            {
                Require(await companies.CreateBranchAsync(created.Id, new SaveBranchRequest(
                    branch.Code,
                    Bilingual(branch.NameEn, branch.NameAr),
                    Address: new Dictionary<string, string>(StringComparer.Ordinal) { ["city"] = branch.CityEn, ["city_ar"] = branch.CityAr, ["country"] = company.Country }), cancellationToken));
                branches++;
            }

            foreach (var currency in company.Currencies)
            {
                Require(await companies.SetCurrencyAsync(created.Id, new CompanyCurrencyRequest(currency.Code, currency.DisplayDecimals, currency.CashRounding), cancellationToken));
            }
        }

        // Everyone else gets one default role; sales and warehouse are scoped to the trading company.
        var roleIds = (await roles.ListRolesAsync(cancellationToken)).ToDictionary(static r => r.Code, static r => r.Id, StringComparer.Ordinal);
        foreach (var person in DemoData.Users)
        {
            if (ReferenceEquals(person, DemoData.Owner))
            {
                continue;
            }

            IReadOnlyList<ScopeInput>? scopes = person.CompanyScope is { } scopedTo ? [new ScopeInput("company", companyIds[scopedTo])] : null;
            Require(await roles.AssignAsync(memberships[person.Local].Id, new AssignRoleRequest(roleIds[person.Role], scopes, AcknowledgeWarnings: true), ownerMembership.UserId, cancellationToken));
        }

        // Rate types the tenant defaults did not create (ASSUMPTIONS Q7 seeds official and market), then a year of rates.
        var currencies = services.GetRequiredService<CurrencyService>();
        var organization = services.GetRequiredService<OrganizationDbContext>();
        var rateTypes = await organization.RateTypes.ToDictionaryAsync(static t => t.Code, static t => t.Id, StringComparer.Ordinal, cancellationToken);
        foreach (var (code, en, ar) in new[] { (DemoData.OfficialRateType, "Official (Central Bank)", "رسمي (البنك المركزي)"), (DemoData.MarketRateType, "Market", "سعر السوق") })
        {
            if (!rateTypes.ContainsKey(code))
            {
                rateTypes[code] = Require(await currencies.CreateRateTypeAsync(new SaveRateTypeRequest(code, Bilingual(en, ar)), cancellationToken)).Id;
            }
        }

        var series = DemoRates.Build(clock.TodayIn(DemoData.BaghdadTimeZone));
        var ordinal = 0;
        foreach (var rate in series)
        {
            organization.Rates.Add(new RateEntry
            {
                Id = DemoIds.For($"rate:{rate.RateType}:{rate.From}:{rate.To}:{rate.ValidFrom:yyyy-MM-dd}", ordinal++),
                RateTypeId = rateTypes[rate.RateType],
                FromCurrency = rate.From,
                ToCurrency = rate.To,
                ValidFrom = rate.ValidFrom,
                Rate = rate.Rate,
                Source = "manual",
                EnteredBy = ownerMembership.UserId,
                Reason = "Demo seed",
                CreatedAt = now,
            });
        }

        await organization.SaveChangesAsync(cancellationToken);

        await services.GetRequiredService<IAuditSink>().RecordAsync(new AuditEntry("tenant", tenant.Id.Value, DemoData.Slug, "seeded",
            After: new { companies = DemoData.Companies.Count, branches, users = DemoData.Users.Count, rates = series.Count }), cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return new DemoSeedResult(tenant.Id.Value, Created: true, stopwatch.Elapsed, DemoData.Companies.Count, branches, DemoData.Users.Count, series.Count);
    }

    private static Dictionary<string, string> Bilingual(string en, string ar) => new(StringComparer.Ordinal) { ["en"] = en, ["ar"] = ar };

    private static T Require<T>(Result<T> result)
    {
        if (result.IsFailure)
        {
            throw new InvalidOperationException($"Demo seed failed: {result.Error!.Code} — {result.Error.Message}");
        }

        return result.Value;
    }
}
