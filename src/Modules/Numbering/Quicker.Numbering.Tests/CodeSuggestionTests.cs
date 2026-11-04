using System.Net;
using System.Text.Json;
using Quicker.Identity.TestSupport;
using Quicker.Numbering.Application;

namespace Quicker.Numbering.Tests;

/// <summary>A-165 through the API: the next code continues the existing pattern, a typed code is checked for duplicates in its scope.</summary>
[Collection(ApiCollection.Name)]
public sealed class CodeSuggestionTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    [Fact]
    public async Task Every_coded_record_kind_answers_with_its_default_on_an_empty_workspace()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        foreach (var type in CodeCatalogue.All)
        {
            // The scope only has to be a value of the scope column's type: a fresh id (or a kind name) has no records.
            var scope = type.ScopeColumn is null ? string.Empty : type.ScopeColumn == "kind" ? "&scope=none" : $"&scope={Guid.NewGuid()}";
            var answer = await owner.GetOkAsync($"/api/v1/numbering/codes/{type.Kind}?code=X1{scope}");
            answer.GetProperty("kind").GetString().ShouldBe(type.Kind);
            answer.GetProperty("autoFill").GetBoolean().ShouldBe(type.AutoFill);
            answer.GetProperty("taken").GetBoolean().ShouldBeFalse($"{type.Kind}: nothing is taken in an empty scope");
            answer.GetProperty("next").GetString().ShouldNotBeNullOrEmpty(type.Kind);
        }
    }

    [Fact]
    public async Task Customer_and_supplier_codes_continue_their_own_pattern_and_duplicates_are_found_across_partners()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        (await Code(owner, "customer")).GetProperty("next").GetString().ShouldBe("CUS-0001");

        await Partner(owner, "CUST-007", customer: true);
        var eight = await Partner(owner, "CUST-008", customer: true);
        await Partner(owner, "ACME", customer: true);
        await Partner(owner, "SUP-0100", customer: false);
        await Partner(owner, "SUP-0200", customer: false);

        // The most common pattern of the customers goes on by one; the suppliers' tens go on in tens.
        (await Code(owner, "customer")).GetProperty("next").GetString().ShouldBe("CUST-009");
        (await Code(owner, "supplier")).GetProperty("next").GetString().ShouldBe("SUP-0300");

        // Partners share one code space: a supplier's code is taken for a customer too, whatever the case.
        (await Code(owner, "customer", "cust-008")).GetProperty("taken").GetBoolean().ShouldBeTrue();
        (await Code(owner, "customer", "SUP-0100")).GetProperty("taken").GetBoolean().ShouldBeTrue();
        (await Code(owner, "customer", "CUST-010")).GetProperty("taken").GetBoolean().ShouldBeFalse();
        // The record being edited keeps its own code.
        (await Code(owner, "customer", $"CUST-008&except={eight}")).GetProperty("taken").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_account_code_continues_its_siblings_under_the_parent_and_a_first_child_takes_the_parents_first_zero()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var (companyId, _) = await owner.CompanyWithBranchAsync("ACC");
        var chart = await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var chartId = chart.GetProperty("id").GetGuid();
        var accounts = (await owner.GetOkAsync($"/api/v1/accounting/charts/{chartId}?expand=accounts")).GetProperty("accounts").EnumerateArray()
            .Select(static a => (Code: a.GetProperty("code").GetString()!, Parent: a.GetProperty("parentCode").GetString()))
            .ToList();

        // A parent with several numeric children of one length: the next is past the highest, as long, not taken.
        var family = accounts.Where(static a => a.Parent is not null && a.Code.All(char.IsAsciiDigit))
            .GroupBy(static a => a.Parent!)
            .First(static g => g.Count() >= 2 && g.Select(static a => a.Code.Length).Distinct().Count() == 1);
        var next = (await owner.GetOkAsync($"/api/v1/numbering/codes/account?scope={chartId}&parent={family.Key}")).GetProperty("next").GetString()!;
        next.Length.ShouldBe(family.First().Code.Length);
        long.Parse(next, System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(family.Max(static a => long.Parse(a.Code, System.Globalization.CultureInfo.InvariantCulture)));
        accounts.ShouldNotContain(a => a.Code == next);

        // An account without children: its first trailing zero becomes 1 (6110 gives 6111, 1100 would give 1110).
        var leaf = accounts.First(a => a.Code.Length == 4 && a.Code.EndsWith('0') && a.Code.All(char.IsAsciiDigit) && accounts.All(c => c.Parent != a.Code));
        var firstChild = (await owner.GetOkAsync($"/api/v1/numbering/codes/account?scope={chartId}&parent={leaf.Code}")).GetProperty("next").GetString()!;
        var trimmed = leaf.Code.TrimEnd('0');
        var expected = trimmed + "1" + leaf.Code[(trimmed.Length + 1)..];
        firstChild.ShouldBe(accounts.Any(a => a.Code == expected) ? firstChild : expected);

        // Taken in this chart, free in another.
        (await owner.GetOkAsync($"/api/v1/numbering/codes/account?scope={chartId}&code={family.First().Code}")).GetProperty("taken").GetBoolean().ShouldBeTrue();
        (await owner.GetOkAsync($"/api/v1/numbering/codes/account?scope={Guid.NewGuid()}&code={family.First().Code}")).GetProperty("taken").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_kind_is_not_found_and_a_scoped_kind_needs_its_scope()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        (await owner.GetAsync("/api/v1/numbering/codes/nothing")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var response = await owner.GetAsync("/api/v1/numbering/codes/warehouse?code=WH01");
        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await response.ReadJsonAsync()).GetProperty("code").GetString().ShouldBe("codes.scope_required");
    }

    private static Task<JsonElement> Code(HttpClient client, string kind, string? code = null) =>
        client.GetOkAsync($"/api/v1/numbering/codes/{kind}" + (code is null ? string.Empty : $"?code={code}"));

    private static async Task<Guid> Partner(HttpClient client, string code, bool customer)
    {
        var partner = await client.PostAsync("/api/v1/partners", new { code, legalName = new { en = code, ar = code }, isCustomer = customer, isSupplier = !customer });
        return partner.GetProperty("id").GetGuid();
    }
}
