using Quicker.Accounting.Contracts;
using Quicker.Kernel.Results;

namespace Quicker.Accounting.Application;

/// <summary>A rule of a posting profile as the matcher sees it.</summary>
public sealed record RuleCandidate(Guid RuleId, string AccountRole, PostingKeys Keys, Guid AccountId);

/// <summary>
/// Account determination (ADR-0006): among the rules for a role, those whose keys all match the line qualify; the
/// one naming the most keys wins. No rule is an error naming the role and the keys (never a silent default); two
/// equally specific rules are a configuration conflict the administrator must resolve.
/// </summary>
public static class PostingRuleMatcher
{
    public static Result<RuleCandidate> Resolve(IEnumerable<RuleCandidate> rules, string role, PostingKeys keys)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(keys);
        var matching = rules
            .Where(r => string.Equals(r.AccountRole, role, StringComparison.Ordinal) && r.Keys.Covers(keys))
            .OrderByDescending(static r => r.Keys.Specificity)
            .ToList();
        if (matching.Count == 0)
        {
            return Error.Validation("posting.rule_missing", $"No posting rule resolves role {role} for these keys; add one to the company's posting profile.")
                .WithWhy(("role", role), ("keys", Describe(keys)));
        }

        var best = matching[0];
        if (matching.Count > 1 && matching[1].Keys.Specificity == best.Keys.Specificity)
        {
            return Error.Conflict("posting.rule_ambiguous", $"Two posting rules resolve role {role} with the same specificity; make one more specific.")
                .WithWhy(("role", role), ("keys", Describe(keys)), ("rules", matching.Where(r => r.Keys.Specificity == best.Keys.Specificity).Select(static r => r.RuleId).ToList()));
        }

        return best;
    }

    private static Dictionary<string, object?> Describe(PostingKeys keys)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (keys.DocumentType is not null)
        {
            result["documentType"] = keys.DocumentType;
        }

        if (keys.ItemPostingGroupId is not null)
        {
            result["itemPostingGroupId"] = keys.ItemPostingGroupId;
        }

        if (keys.PartnerPostingGroupId is not null)
        {
            result["partnerPostingGroupId"] = keys.PartnerPostingGroupId;
        }

        if (keys.TaxCodeId is not null)
        {
            result["taxCodeId"] = keys.TaxCodeId;
        }

        if (keys.WarehouseId is not null)
        {
            result["warehouseId"] = keys.WarehouseId;
        }

        if (keys.BranchId is not null)
        {
            result["branchId"] = keys.BranchId;
        }

        if (keys.BankAccountId is not null)
        {
            result["bankAccountId"] = keys.BankAccountId;
        }

        if (keys.AssetCategoryId is not null)
        {
            result["assetCategoryId"] = keys.AssetCategoryId;
        }

        if (keys.ChargeTypeId is not null)
        {
            result["chargeTypeId"] = keys.ChargeTypeId;
        }

        return result;
    }
}
