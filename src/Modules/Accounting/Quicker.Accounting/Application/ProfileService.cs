using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Accounting.Domain;
using Quicker.Accounting.Persistence;
using Quicker.Audit.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.Accounting.Application;

/// <summary>
/// Posting groups and posting profiles (ADR-0006): the configuration that turns a role and its keys into an
/// account. A profile is versioned per company; the current one is the company's <c>posting_profile_id</c>. A
/// template chart seeds a complete default profile so a new company posts on day one; the coverage list names
/// the roles still unresolved.
/// </summary>
public sealed class ProfileService(AccountingDbContext db, ICompanyDirectory companies, IAuditSink audit, IClock clock) : IPostingGroupDirectory
{
    private static readonly string[] Kinds = ["item", "partner_customer", "partner_supplier", "bank", "asset", "tax", "charge"];

    // ------------------------------------------------------------------ groups

    public async Task<PostingGroupInfo?> FindAsync(Guid groupId, CancellationToken cancellationToken = default)
    {
        var group = await db.Set<PostingGroup>().SingleOrDefaultAsync(g => g.Id == groupId, cancellationToken);
        return group is null ? null : new PostingGroupInfo(group.Id, group.Kind, group.Code, group.Name, group.IsActive);
    }

    async Task<IReadOnlyList<PostingGroupInfo>> IPostingGroupDirectory.ListAsync(string kind, CancellationToken cancellationToken)
    {
        var normalized = kind?.Trim().ToLowerInvariant() ?? string.Empty;
        return await db.Set<PostingGroup>().Where(g => g.Kind == normalized).OrderBy(static g => g.Code)
            .Select(static g => new PostingGroupInfo(g.Id, g.Kind, g.Code, g.Name, g.IsActive)).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PostingGroupSummary>> ListGroupsAsync(string? kind, CancellationToken cancellationToken)
    {
        var query = db.Set<PostingGroup>().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var normalized = kind.Trim().ToLowerInvariant();
            query = query.Where(g => g.Kind == normalized);
        }

        return await query.OrderBy(static g => g.Kind).ThenBy(static g => g.Code).Select(static g => new PostingGroupSummary(g.Id, g.Kind, g.Code, g.Name.Values, g.IsActive)).ToListAsync(cancellationToken);
    }

    public async Task<Result<PostingGroupSummary>> SaveGroupAsync(Guid? groupId, SavePostingGroupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var kind = Validation.OneOf(request.Kind, "posting_group.kind", Kinds);
        var code = Validation.Code(request.Code, "posting_group");
        var name = Validation.Name(request.Name, "posting_group");
        if (kind.IsFailure || code.IsFailure || name.IsFailure)
        {
            return kind.Error ?? code.Error ?? name.Error!;
        }

        var normalizedCode = code.Value.ToUpperInvariant();
        var now = clock.UtcNow;
        PostingGroup? group = null;
        if (groupId is { } id)
        {
            group = await db.Set<PostingGroup>().SingleOrDefaultAsync(g => g.Id == id, cancellationToken);
            if (group is null)
            {
                return Error.NotFound("posting_group", id);
            }
        }

        if (await db.Set<PostingGroup>().AnyAsync(g => g.Kind == kind.Value && g.Code == normalizedCode && (group == null || g.Id != group.Id), cancellationToken))
        {
            return Error.Conflict("posting_group.code_taken", $"A {kind.Value} posting group '{normalizedCode}' already exists.");
        }

        if (group is null)
        {
            group = new PostingGroup { Id = Guid.CreateVersion7(), CreatedAt = now };
            db.Set<PostingGroup>().Add(group);
        }

        group.Kind = kind.Value;
        group.Code = normalizedCode;
        group.Name = name.Value;
        group.IsActive = request.IsActive;
        group.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        _rulesCache.Clear();
        return new PostingGroupSummary(group.Id, group.Kind, group.Code, group.Name.Values, group.IsActive);
    }

    // ------------------------------------------------------------------ profiles

    public async Task<Result<IReadOnlyList<PostingProfileSummary>>> ListProfilesAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var profiles = await db.Set<PostingProfile>().Include(static p => p.Rules).Where(p => p.CompanyId == companyId).OrderBy(static p => p.Code).ThenByDescending(static p => p.Version).ToListAsync(cancellationToken);
        return profiles.Select(p => Map(p, company.PostingProfileId, null)).ToList();
    }

    public async Task<PostingProfileSummary?> GetProfileAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await db.Set<PostingProfile>().Include(static p => p.Rules).SingleOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return null;
        }

        var company = await companies.FindAsync(new CompanyId(profile.CompanyId), cancellationToken);
        var codes = await AccountCodesAsync(profile.Rules.Select(static r => r.AccountId), cancellationToken);
        return Map(profile, company?.PostingProfileId, codes);
    }

    public async Task<Result<PostingProfileSummary>> CreateProfileAsync(Guid companyId, SavePostingProfileRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var created = await NewProfileAsync(company, request, cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        await db.SaveChangesAsync(cancellationToken);

        _rulesCache.Clear();
        return (await GetProfileAsync(created.Value.Id, cancellationToken))!;
    }

    /// <summary>A complete, active profile from the chart's default accounts per role (the template's promise: posting on day one).</summary>
    public async Task<Result<PostingProfileSummary>> CreateFromChartAsync(Guid companyId, SavePostingProfileRequest? request, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        if (company.ChartId is not { } chartId)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.").WithWhy(("companyId", companyId));
        }

        var created = await NewProfileAsync(company, request ?? new SavePostingProfileRequest(), cancellationToken);
        if (created.IsFailure)
        {
            return created.Error!;
        }

        var profile = created.Value;
        var defaults = await db.Accounts
            .Where(a => a.ChartId == chartId && a.DefaultRole != null && a.IsActive && !a.IsHeader && (a.CompanyId == null || a.CompanyId == companyId))
            .OrderBy(static a => a.Code)
            .ToListAsync(cancellationToken);
        var now = clock.UtcNow;
        foreach (var role in AccountRoles.All)
        {
            var account = defaults.FirstOrDefault(a => a.DefaultRole == role);
            if (account is not null)
            {
                profile.Rules.Add(new PostingRule { Id = Guid.CreateVersion7(), ProfileId = profile.Id, AccountRole = role, AccountId = account.Id, Specificity = 0, CreatedAt = now, UpdatedAt = now });
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        _rulesCache.Clear();
        var activated = await ActivateAsync(profile.Id, cancellationToken);
        return activated.IsFailure ? activated.Error! : activated.Value;
    }

    private async Task<Result<PostingProfile>> NewProfileAsync(CompanyInfo company, SavePostingProfileRequest request, CancellationToken cancellationToken)
    {
        var code = Validation.Code(request.Code, "posting_profile");
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var normalized = code.Value.ToUpperInvariant();
        var version = await db.Set<PostingProfile>().Where(p => p.CompanyId == company.Id.Value && p.Code == normalized).Select(static p => (int?)p.Version).MaxAsync(cancellationToken) ?? 0;
        var now = clock.UtcNow;
        var profile = new PostingProfile
        {
            Id = Guid.CreateVersion7(),
            CompanyId = company.Id.Value,
            Code = normalized,
            Name = request.Name is { Count: > 0 } ? Validation.Name(request.Name, "posting_profile").Value : LocalizedText.Bilingual("Default posting profile", "ملف الترحيل الافتراضي"),
            Version = version + 1,
            // A company's first version covers its whole history (opening balances, back-dated go-live entries); later versions start today unless dated.
            ValidFrom = request.ValidFrom ?? (version == 0 ? DateOnly.MinValue : clock.TodayIn(company.TimeZone)),
            Status = "draft",
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Set<PostingProfile>().Add(profile);
        return profile;
    }

    /// <summary>Replaces the rules of a profile (draft or active); journal lines keep the rule id they were posted with.</summary>
    public async Task<Result<PostingProfileSummary>> ReplaceRulesAsync(Guid profileId, IReadOnlyList<PostingRuleRequest> requests, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var profile = await db.Set<PostingProfile>().Include(static p => p.Rules).SingleOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return Error.NotFound("posting_profile", profileId);
        }

        if (profile.Status == "retired")
        {
            return Error.Conflict("posting_profile.retired", "A retired profile is history; create a new version.");
        }

        var company = (await companies.FindAsync(new CompanyId(profile.CompanyId), cancellationToken))!;
        if (company.ChartId is not { } chartId)
        {
            return Error.Conflict("company.chart_missing", $"Company '{company.Code}' has no chart of accounts yet.");
        }

        var accounts = await db.Accounts.Where(a => a.ChartId == chartId).ToDictionaryAsync(static a => a.Code, StringComparer.Ordinal, cancellationToken);
        var groups = await db.Set<PostingGroup>().ToDictionaryAsync(static g => g.Id, cancellationToken);
        var now = clock.UtcNow;
        var rules = new List<PostingRule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            var role = Validation.OneOf(request.AccountRole, "posting_rule.account_role", AccountRoles.All);
            if (role.IsFailure)
            {
                return role.Error!.WithWhy(("row", i + 1));
            }

            if (!accounts.TryGetValue(request.AccountCode?.Trim() ?? string.Empty, out var account))
            {
                return Error.NotFound("account", request.AccountCode ?? string.Empty).WithWhy(("row", i + 1));
            }

            if (account.IsHeader || !account.IsActive)
            {
                return Error.Validation("posting_rule.account_not_postable", $"Account '{account.Code}' is a header or inactive.").WithWhy(("row", i + 1), ("account", account.Code));
            }

            if (account.CompanyId is { } owner && owner != profile.CompanyId)
            {
                return Error.Validation("posting_rule.account_other_company", $"Account '{account.Code}' is reserved for another company.").WithWhy(("row", i + 1), ("account", account.Code));
            }

            var expectedSubledger = AccountRoles.ControlSubledgers.GetValueOrDefault(role.Value);
            if (expectedSubledger is not null && account.SubledgerType != expectedSubledger)
            {
                return Error.Validation("posting_rule.control_mismatch", $"Role {role.Value} needs a {expectedSubledger} control account; '{account.Code}' is not one.").WithWhy(("row", i + 1), ("role", role.Value), ("account", account.Code), ("expectedSubledger", expectedSubledger));
            }

            if (expectedSubledger is null && account.IsControl)
            {
                return Error.Validation("posting_rule.control_unexpected", $"Role {role.Value} is not a control role; '{account.Code}' is a control account.").WithWhy(("row", i + 1), ("role", role.Value), ("account", account.Code));
            }

            if (request.ItemPostingGroupId is { } itemGroup && (!groups.TryGetValue(itemGroup, out var ig) || ig.Kind != "item"))
            {
                return Error.NotFound("posting_group", itemGroup).WithWhy(("row", i + 1), ("kind", "item"));
            }

            if (request.PartnerPostingGroupId is { } partnerGroup && (!groups.TryGetValue(partnerGroup, out var pg) || !pg.Kind.StartsWith("partner_", StringComparison.Ordinal)))
            {
                return Error.NotFound("posting_group", partnerGroup).WithWhy(("row", i + 1), ("kind", "partner"));
            }

            if (request.BranchId is { } branchId)
            {
                var branch = await companies.FindBranchAsync(new BranchId(branchId), cancellationToken);
                if (branch is null || branch.CompanyId.Value != profile.CompanyId)
                {
                    return Error.NotFound("branch", branchId).WithWhy(("row", i + 1));
                }
            }

            var keys = new PostingKeys(string.IsNullOrWhiteSpace(request.DocumentType) ? null : request.DocumentType.Trim().ToLowerInvariant(), request.ItemPostingGroupId, request.PartnerPostingGroupId,
                request.TaxCodeId, request.WarehouseId, request.BranchId, request.BankAccountId, request.AssetCategoryId, request.ChargeTypeId);
            var signature = role.Value + "|" + keys;
            if (!seen.Add(signature))
            {
                return Error.Validation("posting_rule.duplicate", $"Two rules for role {role.Value} name the same keys.").WithWhy(("row", i + 1), ("role", role.Value));
            }

            rules.Add(new PostingRule
            {
                Id = Guid.CreateVersion7(),
                ProfileId = profile.Id,
                AccountRole = role.Value,
                DocumentType = keys.DocumentType,
                ItemPostingGroupId = keys.ItemPostingGroupId,
                PartnerPostingGroupId = keys.PartnerPostingGroupId,
                TaxCodeId = keys.TaxCodeId,
                WarehouseId = keys.WarehouseId,
                BranchId = keys.BranchId,
                BankAccountId = keys.BankAccountId,
                AssetCategoryId = keys.AssetCategoryId,
                ChargeTypeId = keys.ChargeTypeId,
                AccountId = account.Id,
                Specificity = keys.Specificity,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        var before = profile.Rules.Count;
        db.Set<PostingRule>().RemoveRange(profile.Rules);
        profile.Rules.Clear();
        profile.Rules.AddRange(rules);
        profile.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        _rulesCache.Clear();
        await audit.RecordAsync(new AuditEntry("gl_posting_profile", profile.Id, $"{profile.Code} v{profile.Version}", "rules_replaced", Before: new { rules = before }, After: new { rules = rules.Count }), cancellationToken);
        return (await GetProfileAsync(profile.Id, cancellationToken))!;
    }

    public async Task<Result<PostingProfileSummary>> ActivateAsync(Guid profileId, CancellationToken cancellationToken)
    {
        var profile = await db.Set<PostingProfile>().Include(static p => p.Rules).SingleOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        if (profile is null)
        {
            return Error.NotFound("posting_profile", profileId);
        }

        if (profile.Status == "retired")
        {
            return Error.Conflict("posting_profile.retired", "A retired profile cannot be activated again; create a new version.");
        }

        var now = clock.UtcNow;
        var siblings = await db.Set<PostingProfile>().Where(p => p.CompanyId == profile.CompanyId && p.Code == profile.Code && p.Id != profile.Id && p.Status == "active").ToListAsync(cancellationToken);
        foreach (var sibling in siblings)
        {
            sibling.Status = "retired";
            sibling.UpdatedAt = now;
        }

        profile.Status = "active";
        profile.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        _rulesCache.Clear();
        var assigned = await companies.AssignPostingProfileAsync(new CompanyId(profile.CompanyId), profile.Id, cancellationToken);
        if (assigned.IsFailure)
        {
            return assigned.Error!;
        }

        await audit.RecordAsync(new AuditEntry("gl_posting_profile", profile.Id, $"{profile.Code} v{profile.Version}", "activated", After: new { retired = siblings.Select(static s => s.Id).ToList() }), cancellationToken);
        return (await GetProfileAsync(profile.Id, cancellationToken))!;
    }

    /// <summary>
    /// The rules the engine resolves against for a company on a date: the company's current profile when it is active
    /// and effective on the date, else the latest version (active or since retired, never a draft) effective on the
    /// date, so a back-dated entry posts with the rules that governed its date after a newer version took over.
    /// </summary>
    // The rules are read for every posting; memoised per company, profile and date for the life of this scoped service, dropped after every write it makes.
    private readonly Dictionary<(Guid Company, Guid? Profile, DateOnly Date), Result<(Guid ProfileId, IReadOnlyList<RuleCandidate> Rules)>> _rulesCache = new();

    public async Task<Result<(Guid ProfileId, IReadOnlyList<RuleCandidate> Rules)>> ActiveRulesAsync(CompanyInfo company, DateOnly date, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(company);
        var key = (company.Id.Value, company.PostingProfileId, date);
        if (_rulesCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = await ActiveRulesUncachedAsync(company, date, cancellationToken);
        _rulesCache[key] = resolved;
        return resolved;
    }

    private async Task<Result<(Guid ProfileId, IReadOnlyList<RuleCandidate> Rules)>> ActiveRulesUncachedAsync(CompanyInfo company, DateOnly date, CancellationToken cancellationToken)
    {
        PostingProfile? profile = null;
        if (company.PostingProfileId is { } current)
        {
            profile = await db.Set<PostingProfile>().Include(static p => p.Rules).SingleOrDefaultAsync(p => p.Id == current && p.Status == "active" && p.ValidFrom <= date, cancellationToken);
        }

        profile ??= await db.Set<PostingProfile>().Include(static p => p.Rules)
            .Where(p => p.CompanyId == company.Id.Value && p.Status != "draft" && p.ValidFrom <= date)
            .OrderByDescending(static p => p.ValidFrom).ThenByDescending(static p => p.Version)
            .FirstOrDefaultAsync(cancellationToken);
        if (profile is null)
        {
            return Error.Conflict("company.posting_profile_missing", $"Company '{company.Code}' has no active posting profile on {date:yyyy-MM-dd}.").WithWhy(("companyId", company.Id.Value), ("date", date));
        }

        return (profile.Id, profile.Rules.Select(static r => new RuleCandidate(r.Id, r.AccountRole, Keys(r), r.AccountId)).ToList());
    }

    private static PostingKeys Keys(PostingRule r) => new(r.DocumentType, r.ItemPostingGroupId, r.PartnerPostingGroupId, r.TaxCodeId, r.WarehouseId, r.BranchId, r.BankAccountId, r.AssetCategoryId, r.ChargeTypeId);

    private async Task<Dictionary<Guid, string>> AccountCodesAsync(IEnumerable<Guid> accountIds, CancellationToken cancellationToken)
    {
        var ids = accountIds.Distinct().ToList();
        return await db.Accounts.Where(a => ids.Contains(a.Id)).ToDictionaryAsync(static a => a.Id, static a => a.Code, cancellationToken);
    }

    private static PostingProfileSummary Map(PostingProfile p, Guid? currentProfileId, Dictionary<Guid, string>? codes)
    {
        var covered = p.Rules.Select(static r => r.AccountRole).ToHashSet(StringComparer.Ordinal);
        var unresolved = AccountRoles.All.Where(r => !covered.Contains(r)).ToList();
        var rules = codes is null ? null : p.Rules.OrderBy(static r => r.AccountRole, StringComparer.Ordinal).ThenByDescending(static r => r.Specificity).Select(r => new PostingRuleSummary(
            r.Id, r.AccountRole, r.AccountId, codes.GetValueOrDefault(r.AccountId, string.Empty), r.DocumentType, r.ItemPostingGroupId, r.PartnerPostingGroupId, r.TaxCodeId, r.WarehouseId, r.BranchId,
            r.BankAccountId, r.AssetCategoryId, r.ChargeTypeId, r.Specificity)).ToList();
        return new PostingProfileSummary(p.Id, p.CompanyId, p.Code, p.Name.Values, p.Version, p.ValidFrom, p.Status, currentProfileId == p.Id, p.Rules.Count, unresolved, p.UpdatedAt, rules);
    }
}
