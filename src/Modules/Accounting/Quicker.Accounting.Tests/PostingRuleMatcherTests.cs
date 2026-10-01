using Quicker.Accounting.Application;
using Quicker.Accounting.Contracts;

namespace Quicker.Accounting.Tests;

/// <summary>The determination matrix of ADR-0006: most specific match, no silent default, no ambiguity.</summary>
public sealed class PostingRuleMatcherTests
{
    private static readonly Guid Group = Guid.NewGuid();
    private static readonly Guid OtherGroup = Guid.NewGuid();
    private static readonly Guid Warehouse = Guid.NewGuid();
    private static readonly Guid Branch = Guid.NewGuid();
    private static readonly Guid DefaultAccount = Guid.NewGuid();
    private static readonly Guid GroupAccount = Guid.NewGuid();
    private static readonly Guid GroupWarehouseAccount = Guid.NewGuid();
    private static readonly Guid BranchAccount = Guid.NewGuid();

    private static readonly RuleCandidate[] Rules =
    [
        new(Guid.NewGuid(), AccountRoles.Inventory, PostingKeys.None, DefaultAccount),
        new(Guid.NewGuid(), AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group), GroupAccount),
        new(Guid.NewGuid(), AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group, WarehouseId: Warehouse), GroupWarehouseAccount),
        new(Guid.NewGuid(), AccountRoles.Inventory, new PostingKeys(BranchId: Branch), BranchAccount),
        new(Guid.NewGuid(), AccountRoles.Revenue, new PostingKeys(DocumentType: "sales_invoice"), Guid.NewGuid()),
    ];

    [Fact]
    public void The_most_specific_matching_rule_wins()
    {
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, PostingKeys.None).Value.AccountId.ShouldBe(DefaultAccount);
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group)).Value.AccountId.ShouldBe(GroupAccount);
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group, WarehouseId: Warehouse)).Value.AccountId.ShouldBe(GroupWarehouseAccount);
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group, WarehouseId: Guid.NewGuid())).Value.AccountId.ShouldBe(GroupAccount, "the warehouse rule names another warehouse");
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: OtherGroup, WarehouseId: Warehouse)).Value.AccountId.ShouldBe(DefaultAccount, "no rule names the other group");
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group, TaxCodeId: Guid.NewGuid())).Value.AccountId.ShouldBe(GroupAccount, "keys the rule does not name are free");
    }

    [Fact]
    public void Document_type_keys_match_case_insensitively_and_unmatched_roles_fail_naming_the_role_and_keys()
    {
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Revenue, new PostingKeys(DocumentType: "SALES_INVOICE")).IsSuccess.ShouldBeTrue();
        var missing = PostingRuleMatcher.Resolve(Rules, AccountRoles.Revenue, new PostingKeys(DocumentType: "credit_note"));
        missing.IsFailure.ShouldBeTrue();
        missing.Error!.Code.ShouldBe("posting.rule_missing");
        missing.Error.Why!["role"].ShouldBe(AccountRoles.Revenue);
        ((IReadOnlyDictionary<string, object?>)missing.Error.Why["keys"]!)["documentType"].ShouldBe("credit_note");
        PostingRuleMatcher.Resolve(Rules, AccountRoles.Cogs, PostingKeys.None).Error!.Code.ShouldBe("posting.rule_missing");
    }

    [Fact]
    public void Two_rules_of_equal_specificity_are_a_configuration_conflict()
    {
        var ambiguous = PostingRuleMatcher.Resolve(Rules, AccountRoles.Inventory, new PostingKeys(ItemPostingGroupId: Group, BranchId: Branch));
        ambiguous.IsFailure.ShouldBeTrue();
        ambiguous.Error!.Code.ShouldBe("posting.rule_ambiguous");
        ((IReadOnlyList<Guid>)ambiguous.Error.Why!["rules"]!).Count.ShouldBe(2);
        PostingKeys.None.Specificity.ShouldBe(0);
        new PostingKeys(ItemPostingGroupId: Group, WarehouseId: Warehouse, BranchId: Branch).Specificity.ShouldBe(3);
    }
}
