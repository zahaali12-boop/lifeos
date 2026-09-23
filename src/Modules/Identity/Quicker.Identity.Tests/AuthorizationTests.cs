using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Quicker.Identity.TestSupport;

namespace Quicker.Identity.Tests;

[Collection(ApiCollection.Name)]
public sealed class AuthorizationTests(ApiHostFixture host)
{
    private ApiFixture Api => host.Api;

    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private async Task<(Workspace Owner, string ClerkToken, Guid ClerkMembership)> OwnerAndClerkAsync(params string[] clerkGrants)
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var role = await (await owner.PostAsJsonAsync("/api/v1/roles", new { code = "clerk", name = new { en = "Clerk" }, description = "", grants = clerkGrants }, Json)).ReadJsonAsync();
        var roleId = role.GetProperty("id").GetGuid();

        var clerkEmail = $"clerk-{ws.Slug}@example.test";
        var invited = await (await owner.PostAsJsonAsync("/api/v1/users/invite", new { email = clerkEmail, displayName = "Clerk", roleIds = new[] { roleId } }, Json)).ReadJsonAsync();
        var membershipId = invited.GetProperty("membershipId").GetGuid();
        var mail = Api.Emails.LastTo(clerkEmail).ShouldNotBeNull();
        var token = mail.TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "clerk-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return (ws, accepted.GetProperty("accessToken").GetString()!, membershipId);
    }

    [Fact]
    public async Task Invited_member_gets_only_the_granted_permissions_and_403_carries_the_missing_key()
    {
        var (_, clerkToken, _) = await OwnerAndClerkAsync("identity.user.read");
        using var clerk = Api.ClientFor(clerkToken);

        (await clerk.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var forbidden = await clerk.GetAsync("/api/v1/roles");
        forbidden.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var body = await forbidden.ReadJsonAsync();
        body.GetProperty("code").GetString().ShouldBe("auth.forbidden");
        body.GetProperty("why").GetProperty("permission").GetString().ShouldBe("identity.role.read");

        var me = await (await clerk.GetAsync("/api/v1/me")).ReadJsonAsync();
        me.GetProperty("isOwner").GetBoolean().ShouldBeFalse();
        me.GetProperty("permissions").EnumerateArray().Select(static p => p.GetString()).ShouldBe(["identity.user.read"]);
    }

    [Fact]
    public async Task Permission_changes_take_effect_on_the_next_request_without_re_login()
    {
        var (ws, clerkToken, membershipId) = await OwnerAndClerkAsync("identity.user.read");
        using var owner = Api.ClientFor(ws.AccessToken);
        using var clerk = Api.ClientFor(clerkToken);
        (await clerk.GetAsync("/api/v1/roles")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var roles = await (await owner.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var auditor = roles.EnumerateArray().Single(r => r.GetProperty("code").GetString() == "auditor").GetProperty("id").GetGuid();
        var assign = await owner.PostAsJsonAsync($"/api/v1/users/{membershipId}/assignments", new { roleId = auditor, scopes = new[] { new { scopeType = "branch", scopeId = Guid.NewGuid() } } }, Json);
        assign.StatusCode.ShouldBe(HttpStatusCode.Created);

        (await clerk.GetAsync("/api/v1/roles")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await (await clerk.GetAsync("/api/v1/me")).ReadJsonAsync();
        var grant = me.GetProperty("grants").EnumerateArray().Single(g => g.GetProperty("permission").GetString() == "identity.role.read");
        grant.GetProperty("scopes").GetProperty("branchIds").GetArrayLength().ShouldBe(1);

        var assignmentId = assign.Headers.Location!.ToString().Split('/').Last();
        (await owner.DeleteAsync($"/api/v1/assignments/{assignmentId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await clerk.GetAsync("/api/v1/roles")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Disabling_a_member_revokes_sessions_and_the_last_owner_cannot_be_disabled()
    {
        var (ws, clerkToken, membershipId) = await OwnerAndClerkAsync("identity.user.read");
        using var owner = Api.ClientFor(ws.AccessToken);
        using var clerk = Api.ClientFor(clerkToken);

        (await owner.PostAsync($"/api/v1/users/{membershipId}/disable", null)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await clerk.GetAsync("/api/v1/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var self = await owner.PostAsync($"/api/v1/users/{ws.MembershipId}/disable", null);
        self.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await self.ErrorCodeAsync()).ShouldBe("member.self_disable");
    }

    [Fact]
    public async Task Segregation_of_duties_blocks_conflicting_roles_and_warns_on_assignments_until_acknowledged()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);

        (await owner.PostAsJsonAsync("/api/v1/sod/rules", new { permissionA = "identity.apikey.manage", permissionB = "identity.sso.manage", severity = "block", rationale = new { en = "keys and sso" } }, Json)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var blocked = await owner.PostAsJsonAsync("/api/v1/roles", new { code = "risky", name = new { en = "Risky" }, description = "", grants = new[] { "identity.apikey.manage", "identity.sso.manage" } }, Json);
        blocked.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var why = await blocked.ReadJsonAsync();
        why.GetProperty("code").GetString().ShouldBe("role.sod_conflict");
        why.GetProperty("why").GetProperty("conflicts")[0].GetProperty("severity").GetString().ShouldBe("block");

        // Default rule: role.manage × assignment.manage is a warning; two roles that together hit it need an acknowledgement.
        var roleA = await (await owner.PostAsJsonAsync("/api/v1/roles", new { code = "role_editor", name = new { en = "Role editor" }, description = "", grants = new[] { "identity.role.manage" } }, Json)).ReadJsonAsync();
        var roleB = await (await owner.PostAsJsonAsync("/api/v1/roles", new { code = "assigner", name = new { en = "Assigner" }, description = "", grants = new[] { "identity.assignment.manage" } }, Json)).ReadJsonAsync();
        var invited = await (await owner.PostAsJsonAsync("/api/v1/users/invite", new { email = $"sod-{ws.Slug}@example.test", displayName = "S", roleIds = new[] { roleA.GetProperty("id").GetGuid() } }, Json)).ReadJsonAsync();
        var membership = invited.GetProperty("membershipId").GetGuid();

        var warned = await owner.PostAsJsonAsync($"/api/v1/users/{membership}/assignments", new { roleId = roleB.GetProperty("id").GetGuid() }, Json);
        warned.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await warned.ErrorCodeAsync()).ShouldBe("assignment.sod_warning");

        var acknowledged = await owner.PostAsJsonAsync($"/api/v1/users/{membership}/assignments", new { roleId = roleB.GetProperty("id").GetGuid(), acknowledgeWarnings = true }, Json);
        acknowledged.StatusCode.ShouldBe(HttpStatusCode.Created);

        var report = await (await owner.GetAsync("/api/v1/sod/report")).ReadJsonAsync();
        report.EnumerateArray().Single(r => r.GetProperty("membershipId").GetGuid() == membership).GetProperty("conflicts").GetArrayLength().ShouldBe(1);
        report.EnumerateArray().Single(r => r.GetProperty("membershipId").GetGuid() == ws.MembershipId).GetProperty("isSuperUser").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task Every_role_template_saves_as_a_role_as_it_stands()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var templates = await (await owner.GetAsync("/api/v1/meta/role-templates")).ReadJsonAsync();
        templates.GetArrayLength().ShouldBeGreaterThan(0);

        // The role designer copies a template's grants into a new role unchanged, so each must save as it stands.
        foreach (var template in templates.EnumerateArray())
        {
            var code = template.GetProperty("code").GetString()!;
            var saved = await owner.PostAsJsonAsync("/api/v1/roles", new
            {
                code = $"from_{code}",
                name = template.GetProperty("name"),
                description = template.GetProperty("description").GetString(),
                grants = template.GetProperty("grants"),
            }, Json);
            saved.StatusCode.ShouldBe(HttpStatusCode.Created, $"template {code}: {await saved.Content.ReadAsStringAsync()}");
        }

        var accountant = templates.EnumerateArray().Single(static t => t.GetProperty("code").GetString() == "accountant");
        accountant.GetProperty("pendingGrants").EnumerateArray().Select(static g => g.GetString()).ShouldContain("finance.*");
        accountant.GetProperty("grants").EnumerateArray().Select(static g => g.GetString()).ShouldContain("accounting.journal.*");
    }

    [Fact]
    public async Task A_system_role_keeps_its_reserved_grants_when_edited_but_new_grants_must_exist()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var roles = await (await owner.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var accountant = roles.EnumerateArray().Single(static r => r.GetProperty("code").GetString() == "accountant");
        var id = accountant.GetProperty("id").GetGuid();
        var grants = accountant.GetProperty("grants").EnumerateArray().Select(static g => g.GetString()!).ToList();
        grants.ShouldContain("finance.*"); // reserved for a module still to come

        var edited = await owner.PutAsJsonAsync($"/api/v1/roles/{id}", new { code = "accountant", name = accountant.GetProperty("name"), description = "Keeps the books.", grants }, Json);
        edited.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await edited.ReadJsonAsync()).GetProperty("grants").EnumerateArray().Select(static g => g.GetString()).ShouldContain("finance.*");

        var typo = await owner.PutAsJsonAsync($"/api/v1/roles/{id}", new { code = "accountant", name = accountant.GetProperty("name"), description = "Keeps the books.", grants = grants.Append("acounting.journal.post").ToList() }, Json);
        typo.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = await typo.ReadJsonAsync();
        problem.GetProperty("code").GetString().ShouldBe("role.grant_unknown");
        problem.GetProperty("why").GetProperty("keys").EnumerateArray().Select(static k => k.GetString()).ShouldBe(["acounting.journal.post"]);
    }

    [Fact]
    public async Task Default_sod_rules_name_permissions_that_exist_once_their_module_has_shipped()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var catalogue = (await (await owner.GetAsync("/api/v1/meta/permissions")).ReadJsonAsync()).EnumerateArray().Select(static p => p.GetProperty("key").GetString()!).ToHashSet(StringComparer.Ordinal);
        var rules = (await (await owner.GetAsync("/api/v1/sod/rules")).ReadJsonAsync()).EnumerateArray().Where(static r => r.GetProperty("isSystem").GetBoolean()).ToList();
        rules.ShouldNotBeEmpty();

        // A rule naming a key its (shipped) module does not register can never trip: the control silently does nothing.
        var dead = rules
            .SelectMany(static r => new[] { r.GetProperty("permissionA").GetString()!, r.GetProperty("permissionB").GetString()! })
            .Where(k => catalogue.Any(c => c.StartsWith(k.Split('.')[0] + ".", StringComparison.Ordinal)) && !catalogue.Contains(k))
            .ToList();
        dead.ShouldBeEmpty();
        rules.ShouldContain(static r => r.GetProperty("permissionA").GetString() == "partners.supplier.manage" && r.GetProperty("permissionB").GetString() == "banking.payment.post");
    }

    [Fact]
    public async Task Api_keys_act_as_their_member_with_narrowed_scopes_and_die_when_revoked()
    {
        var ws = await Api.SignupAsync();
        using var owner = Api.ClientFor(ws.AccessToken);
        var created = await (await owner.PostAsJsonAsync("/api/v1/api-keys", new { name = "integration", scopes = new[] { "identity.user.read" } }, Json)).ReadJsonAsync();
        var key = created.GetProperty("key").GetString()!;
        key.ShouldStartWith("qk_");

        using var viaKey = Api.ClientForApiKey(key);
        (await viaKey.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await (await viaKey.GetAsync("/api/v1/me")).ReadJsonAsync();
        me.GetProperty("authMethods").GetString().ShouldBe("api_key");
        me.GetProperty("isOwner").GetBoolean().ShouldBeFalse();
        (await viaKey.GetAsync("/api/v1/roles")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await viaKey.PostAsJsonAsync("/api/v1/api-keys", new { name = "escalate" }, Json)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        (await owner.DeleteAsync($"/api/v1/api-keys/{created.GetProperty("id").GetGuid()}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await viaKey.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var garbage = Api.ClientForApiKey("qk_not-a-key.nope");
        (await garbage.GetAsync("/api/v1/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_records_through_the_api()
    {
        var a = await Api.SignupAsync();
        var b = await Api.SignupAsync();
        using var ownerA = Api.ClientFor(a.AccessToken);
        using var ownerB = Api.ClientFor(b.AccessToken);

        var rolesA = await (await ownerA.GetAsync("/api/v1/roles")).ReadJsonAsync();
        var roleIdA = rolesA.EnumerateArray().First().GetProperty("id").GetGuid();
        (await ownerB.GetAsync($"/api/v1/roles/{roleIdA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ownerB.DeleteAsync($"/api/v1/roles/{roleIdA}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var membersB = await (await ownerB.GetAsync("/api/v1/users")).ReadJsonAsync();
        membersB.EnumerateArray().Select(static m => m.GetProperty("email").GetString()).ShouldNotContain(a.OwnerEmail);

        var assign = await ownerB.PostAsJsonAsync($"/api/v1/users/{a.MembershipId}/assignments", new { roleId = roleIdA }, Json);
        assign.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // A user with seats in two tenants must choose one at login and gets a token bound to it.
        var inviteFromB = await (await ownerB.PostAsJsonAsync("/api/v1/users/invite", new { email = a.OwnerEmail, displayName = "A in B", roleIds = Array.Empty<Guid>() }, Json)).ReadJsonAsync();
        inviteFromB.GetProperty("status").GetString().ShouldBe("invited");
        var token = Api.Emails.LastTo(a.OwnerEmail)!.TextBody.Split("token=")[1].Trim();
        (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = a.OwnerPassword }, Json)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var login = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/login", new { email = a.OwnerEmail, password = a.OwnerPassword }, Json)).ReadJsonAsync();
        login.GetProperty("status").GetString().ShouldBe("select_tenant");
        login.GetProperty("tenants").GetArrayLength().ShouldBe(2);
        var chosen = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/select-tenant", new { challengeToken = login.GetProperty("challengeToken").GetString(), tenantSlug = b.Slug }, Json)).ReadJsonAsync();
        chosen.GetProperty("tokens").GetProperty("tenant").GetProperty("slug").GetString().ShouldBe(b.Slug);
        chosen.GetProperty("tokens").GetProperty("user").GetProperty("email").GetString().ShouldBe(a.OwnerEmail);
    }
}
