using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Quicker.Identity.TestSupport;
using Quicker.Workflow.Application;
using Quicker.Workflow.Contracts;

namespace Quicker.Workflow.Tests;

/// <summary>The workflow engine (roadmap 4.0, ADR-0020): definitions in the safe grammar, routing, decisions, blocks and overrides, delegation, escalation, and a real document (stock adjustments).</summary>
[Collection(ApiCollection.Name)]
public sealed class WorkflowTests(ApiHostFixture host)
{
    private static readonly JsonSerializerOptions Json = ApiFixture.Json;

    private ApiFixture Api => host.Api;

    private static object Name(string en, string ar) => new { en, ar };

    private sealed record Member(HttpClient Client, Guid MembershipId, string Email);

    /// <summary>Invites a member with a fresh role holding the grants, accepts the invitation and signs them in.</summary>
    private async Task<Member> InviteAsync(HttpClient owner, Workspace ws, string roleCode, params string[] grants)
    {
        var role = await owner.PostAsync("/api/v1/roles", new { code = roleCode, name = Name(roleCode, roleCode), description = "", grants });
        var email = $"{roleCode}-{Guid.CreateVersion7().ToString("N")[^6..]}-{ws.Slug}@example.test";
        var invited = await owner.PostAsync("/api/v1/users/invite", new { email, displayName = roleCode, roleIds = new[] { role.GetProperty("id").GetGuid() } });
        var token = Api.Emails.LastTo(email).ShouldNotBeNull().TextBody.Split("token=")[1].Trim();
        var accepted = await (await Api.Client.PostAsJsonAsync("/api/v1/auth/invitations/accept", new { token, password = "member-passphrase-long-enough" }, Json)).ReadJsonAsync();
        return new Member(Api.ClientFor(accepted.GetProperty("accessToken").GetString()!), invited.GetProperty("membershipId").GetGuid(), email);
    }

    private static object Step(string name, object approvers, string mode = "any", int? timeoutHours = null, object? escalation = null, bool allowDelegate = true, bool requireComment = false) =>
        new { name = Name(name, name), approverKind = approvers is string ? "role" : "users", approvers = approvers is string role ? new { roleCode = role } : approvers, mode, timeoutHours, escalation, allowDelegate, requireComment };

    private static object Definition(string entityType, string trigger, string name, params object[] rules) => new { entityType, trigger, name = Name(name, name), rules };

    private static object Rule(string name, string condition, params object[] steps) => new { name = Name(name, name), condition, steps };

    private static async Task<Guid> ActivateAsync(HttpClient owner, object definition)
    {
        var created = await owner.PostAsync("/api/v1/workflow/definitions", definition);
        created.GetProperty("status").GetString().ShouldBe("draft");
        var id = created.GetProperty("id").GetGuid();
        var active = await owner.PostAsync($"/api/v1/workflow/definitions/{id}/activate", new { }, HttpStatusCode.OK);
        active.GetProperty("status").GetString().ShouldBe("active");
        return id;
    }

    private Task<WorkflowOutcome> SubmitAsync(Workspace ws, TestDocument document, Guid requestedBy)
    {
        TestDocumentProvider.Documents[document.Id] = document;
        return host.InTenantAsync(ws.TenantId, async (sp, ct) =>
        {
            var outcome = await sp.GetRequiredService<IWorkflowEngine>().SubmitAsync(document.ToSubject() with { RequestedBy = requestedBy }, WorkflowTriggers.OnSubmit, ct);
            outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Code + ": " + outcome.Error?.Message);
            return outcome.Value;
        });
    }

    private static async Task<JsonElement> InboxAsync(HttpClient client) => await client.GetOkAsync("/api/v1/workflow/requests");

    private static JsonElement Detail(JsonElement response) => response.GetProperty("request");

    [Fact]
    public async Task Definitions_come_from_the_catalogue_and_conditions_are_checked_before_they_can_run()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);

        var catalogue = await owner.GetOkAsync("/api/v1/workflow/catalogue");
        var subjects = catalogue.GetProperty("subjects").EnumerateArray().ToDictionary(static s => s.GetProperty("entityType").GetString()!);
        subjects.ShouldContainKey("test_document");
        subjects.ShouldContainKey("stock_adjustment");
        subjects["stock_adjustment"].GetProperty("fields").EnumerateArray().Select(static f => f.GetProperty("name").GetString()).ShouldContain("amount");
        subjects["test_document"].GetProperty("blockKinds").EnumerateArray().Select(static k => k.GetString()).ShouldBe(["credit_limit", "price_floor"]);
        catalogue.GetProperty("triggers").EnumerateArray().Select(static t => t.GetString()).ShouldBe(["on_submit", "on_post_attempt", "on_block"]);
        catalogue.GetProperty("functions").EnumerateArray().Select(static f => f.GetString()).ShouldContain("amount_in");

        var ok = await owner.PostAsync("/api/v1/workflow/expressions/validate", new { entityType = "test_document", expression = "amount_in('USD') > 10000 and category in ('capex', 'opex') and not urgent" }, HttpStatusCode.OK);
        ok.GetProperty("valid").GetBoolean().ShouldBeTrue(ok.ToString());
        ok.GetProperty("fields").EnumerateArray().Select(static f => f.GetString()).ShouldBe(["amount", "category", "currency", "urgent"]);

        var unknownField = await owner.PostAsync("/api/v1/workflow/expressions/validate", new { entityType = "test_document", expression = "amoun > 1" }, HttpStatusCode.OK);
        unknownField.GetProperty("valid").GetBoolean().ShouldBeFalse();
        unknownField.GetProperty("position").GetInt32().ShouldBe(0);

        var notBoolean = await owner.PostAsync("/api/v1/workflow/expressions/validate", new { entityType = "test_document", expression = "amount + 1" }, HttpStatusCode.OK);
        notBoolean.GetProperty("valid").GetBoolean().ShouldBeFalse();

        var typeClash = await owner.PostAsync("/api/v1/workflow/expressions/validate", new { entityType = "test_document", expression = "amount > 'ten'" }, HttpStatusCode.OK);
        typeClash.GetProperty("valid").GetBoolean().ShouldBeFalse();

        (await owner.PostErrorAsync("/api/v1/workflow/definitions", Definition("test_document", "on_submit", "Bad field", Rule("r", "amoun > 1", Step("s", new { membershipIds = new[] { ws.MembershipId } }))), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.condition_invalid");
        (await owner.PostErrorAsync("/api/v1/workflow/definitions", Definition("test_document", "on_submit", "No role", Rule("r", "amount > 1", Step("s", "nobody"))), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.role_unknown");
        (await owner.PostErrorAsync("/api/v1/workflow/definitions", Definition("test_document", "on_submit", "No steps", Rule("r", "amount > 1")), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.steps_required");
        (await owner.PostErrorAsync("/api/v1/workflow/definitions", Definition("nothing", "on_submit", "No type", Rule("r", "amount > 1", Step("s", new { membershipIds = new[] { ws.MembershipId } }))), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.entity_type_unknown");
        (await owner.PostErrorAsync("/api/v1/workflow/definitions", Definition("test_document", "on_block", "No kind", Rule("r", "amount > 1", Step("s", new { membershipIds = new[] { ws.MembershipId } }))), HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.block_kind_invalid");

        // A draft is edited in place; once active, editing yields a new draft version in the lineage, and activating it retires the previous version.
        var v1 = await owner.PostAsync("/api/v1/workflow/definitions", Definition("test_document", "on_submit", "Spend", Rule("Large", "amount > 100", Step("Owner", new { membershipIds = new[] { ws.MembershipId } }))));
        var v1Id = v1.GetProperty("id").GetGuid();
        var edited = await owner.PutAsync($"/api/v1/workflow/definitions/{v1Id}", Definition("test_document", "on_submit", "Spend", Rule("Large", "amount > 200", Step("Owner", new { membershipIds = new[] { ws.MembershipId } }))));
        edited.GetProperty("id").GetGuid().ShouldBe(v1Id);
        edited.GetProperty("version").GetInt32().ShouldBe(1);
        await owner.PostAsync($"/api/v1/workflow/definitions/{v1Id}/activate", new { }, HttpStatusCode.OK);
        var v2 = await owner.PutAsync($"/api/v1/workflow/definitions/{v1Id}", Definition("test_document", "on_submit", "Spend", Rule("Large", "amount > 300", Step("Owner", new { membershipIds = new[] { ws.MembershipId } }))));
        v2.GetProperty("id").GetGuid().ShouldNotBe(v1Id);
        v2.GetProperty("version").GetInt32().ShouldBe(2);
        v2.GetProperty("status").GetString().ShouldBe("draft");
        v2.GetProperty("lineageId").GetGuid().ShouldBe(v1.GetProperty("lineageId").GetGuid());
        await owner.PostAsync($"/api/v1/workflow/definitions/{v2.GetProperty("id").GetGuid()}/activate", new { }, HttpStatusCode.OK);
        (await owner.GetOkAsync($"/api/v1/workflow/definitions/{v1Id}")).GetProperty("status").GetString().ShouldBe("retired");
        var active = await owner.GetOkAsync("/api/v1/workflow/definitions?entityType=test_document&status=active");
        active.GetArrayLength().ShouldBe(1);
        active[0].GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task A_rule_over_the_threshold_routes_to_the_finance_role_and_the_requester_cannot_approve_their_own_document()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var finance = await InviteAsync(owner, ws, "finance", "workflow.request.read");
        var clerk = await InviteAsync(owner, ws, "clerk");
        var companyId = Guid.CreateVersion7();

        await ActivateAsync(owner, Definition("test_document", "on_submit", "Spend control",
            Rule("Over ten thousand", "amount_in('USD') > 10000", Step("Finance", "finance", requireComment: false)),
            Rule("Capex", "category = 'capex'", Step("Owner", new { membershipIds = new[] { ws.MembershipId } }))));

        // Below every rule: recorded and approved at once.
        var small = new TestDocument(Guid.CreateVersion7(), companyId, 5000m, "USD");
        var auto = await SubmitAsync(ws, small, clerk.MembershipId);
        auto.Status.ShouldBe(WorkflowOutcomes.AutoApproved);
        auto.RequestId.ShouldNotBeNull();
        (await owner.GetOkAsync($"/api/v1/workflow/requests/{auto.RequestId}")).GetProperty("request").GetProperty("status").GetString().ShouldBe("auto_approved");

        // Over the threshold: the finance member alone sees it in the inbox, the clerk who submitted sees it but cannot act.
        var big = new TestDocument(Guid.CreateVersion7(), companyId, 25000m, "USD");
        var pending = await SubmitAsync(ws, big, clerk.MembershipId);
        pending.Status.ShouldBe(WorkflowOutcomes.Pending);
        pending.RuleName!.Resolve("en").ShouldBe("Over ten thousand");
        var requestId = pending.RequestId!.Value;

        var inbox = await InboxAsync(finance.Client);
        inbox.GetArrayLength().ShouldBe(1);
        inbox[0].GetProperty("id").GetGuid().ShouldBe(requestId);
        inbox[0].GetProperty("canAct").GetBoolean().ShouldBeTrue();
        inbox[0].GetProperty("requestedBy").GetGuid().ShouldBe(clerk.MembershipId);
        (await InboxAsync(clerk.Client)).GetArrayLength().ShouldBe(0);
        (await InboxAsync(owner)).GetArrayLength().ShouldBe(0);
        (await owner.GetOkAsync("/api/v1/workflow/requests?mine=false&entityType=test_document")).GetArrayLength().ShouldBe(2);
        (await clerk.Client.GetErrorAsync("/api/v1/workflow/requests?mine=false", HttpStatusCode.Forbidden)).Code.ShouldBe("auth.forbidden");

        var seenByRequester = await clerk.Client.GetOkAsync($"/api/v1/workflow/requests/{requestId}");
        Detail(seenByRequester).GetProperty("canAct").GetBoolean().ShouldBeFalse();
        seenByRequester.GetProperty("evaluation").GetProperty("matched").GetInt32().ShouldBe(1);
        seenByRequester.GetProperty("subject").GetProperty("amount").GetDecimal().ShouldBe(25000m);
        seenByRequester.GetProperty("steps")[0].GetProperty("approvers").EnumerateArray().Select(static a => a.GetGuid()).ShouldBe([finance.MembershipId]);

        // Nobody decides on their own document, and only an assigned approver decides at all.
        var selfApproval = await host.InTenantAsync(ws.TenantId, (sp, ct) => sp.GetRequiredService<WorkflowEngine>().ActAsync(requestId, "approve", null, null, ct));
        selfApproval.IsFailure.ShouldBeTrue();
        selfApproval.Error!.Code.ShouldBe("workflow.actor_required");
        (await clerk.Client.PostErrorAsync($"/api/v1/workflow/requests/{requestId}/approve", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("workflow.self_approval");
        (await owner.PostErrorAsync($"/api/v1/workflow/requests/{requestId}/approve", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("workflow.not_an_approver");

        var approved = await finance.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Within budget." }, HttpStatusCode.OK);
        Detail(approved).GetProperty("status").GetString().ShouldBe("approved");
        Detail(approved).GetProperty("decidedBy").GetGuid().ShouldBe(finance.MembershipId);
        approved.GetProperty("actions").EnumerateArray().Select(static a => a.GetProperty("action").GetString()).ShouldBe(["submit", "approve"]);
        var decision = TestDocumentProvider.Decisions[big.Id].ShouldHaveSingleItem();
        decision.Status.ShouldBe(WorkflowDecisions.Approved);
        decision.Comment.ShouldBe("Within budget.");
        decision.DecidedBy.ShouldBe(finance.MembershipId);
        (await InboxAsync(finance.Client)).GetArrayLength().ShouldBe(0);
        (await finance.Client.PostErrorAsync($"/api/v1/workflow/requests/{requestId}/approve", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("workflow.not_pending");

        // A rejection says why; the document's module hears it.
        var second = new TestDocument(Guid.CreateVersion7(), companyId, 40000m, "USD");
        var secondRequest = (await SubmitAsync(ws, second, clerk.MembershipId)).RequestId!.Value;
        (await finance.Client.PostErrorAsync($"/api/v1/workflow/requests/{secondRequest}/reject", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.comment_required");
        var rejected = await finance.Client.PostAsync($"/api/v1/workflow/requests/{secondRequest}/reject", new { comment = "Not this quarter." }, HttpStatusCode.OK);
        Detail(rejected).GetProperty("status").GetString().ShouldBe("rejected");
        TestDocumentProvider.Decisions[second.Id].ShouldHaveSingleItem().Comment.ShouldBe("Not this quarter.");

        // The books refuse the document on approval: the decision fails and the request stays pending.
        var stuck = new TestDocument(Guid.CreateVersion7(), companyId, 40000m, "USD", FailOnDecision: true);
        var stuckRequest = (await SubmitAsync(ws, stuck, clerk.MembershipId)).RequestId!.Value;
        (await finance.Client.PostErrorAsync($"/api/v1/workflow/requests/{stuckRequest}/approve", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("test_document.cannot_post");
        Detail(await finance.Client.GetOkAsync($"/api/v1/workflow/requests/{stuckRequest}")).GetProperty("status").GetString().ShouldBe("pending");

        // The requester withdraws; the second rule (capex) routes to the owner even for small amounts.
        var cancelled = await clerk.Client.PostAsync($"/api/v1/workflow/requests/{stuckRequest}/cancel", new { reason = "Withdrawn." }, HttpStatusCode.OK);
        Detail(cancelled).GetProperty("status").GetString().ShouldBe("cancelled");
        var capex = await SubmitAsync(ws, new TestDocument(Guid.CreateVersion7(), companyId, 10m, "USD", Category: "capex"), clerk.MembershipId);
        capex.Status.ShouldBe(WorkflowOutcomes.Pending);
        (await InboxAsync(owner)).Only().GetProperty("id").GetGuid().ShouldBe(capex.RequestId!.Value);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Blocks_route_to_their_definition_and_an_approval_grants_an_override_that_is_used_once()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var credit = await InviteAsync(owner, ws, "credit", "workflow.request.read");
        var clerk = await InviteAsync(owner, ws, "clerk");
        var companyId = Guid.CreateVersion7();
        var definition = await ActivateAsync(owner, new
        {
            entityType = "test_document",
            trigger = "on_block",
            blockKind = "credit_limit",
            name = Name("Credit limit overrides", "تجاوز حد الائتمان"),
            overrideValidHours = 48,
            rules = new[] { Rule("Any excess", "over_by > 0", Step("Credit control", new { membershipIds = new[] { credit.MembershipId } }, requireComment: true)) },
        });

        var document = new TestDocument(Guid.CreateVersion7(), companyId, 1500m, "USD");
        TestDocumentProvider.Documents[document.Id] = document;
        var why = new Dictionary<string, object?>(StringComparer.Ordinal) { ["limit"] = 1000m, ["exposure"] = 1500m, ["over_by"] = 500m, ["customer"] = "ACME" };
        var raised = await host.InTenantAsync(ws.TenantId, async (sp, ct) =>
        {
            var engine = sp.GetRequiredService<IWorkflowEngine>();
            (await engine.ConsumeOverrideAsync("credit_limit", "test_document", document.Id, ct)).ShouldBeNull();
            var outcome = await engine.RaiseBlockAsync(new BlockRequest("credit_limit", "test_document", document.Id, companyId, "SO-1 for ACME", why, clerk.MembershipId), ct);
            outcome.IsSuccess.ShouldBeTrue(outcome.Error?.Code);
            var again = await engine.RaiseBlockAsync(new BlockRequest("credit_limit", "test_document", document.Id, companyId, "SO-1 for ACME", why, clerk.MembershipId), ct);
            again.Value.BlockId.ShouldBe(outcome.Value.BlockId);
            var unrouted = await engine.RaiseBlockAsync(new BlockRequest("price_floor", "test_document", document.Id, companyId, "SO-1 for ACME", new Dictionary<string, object?>(StringComparer.Ordinal) { ["floor"] = 10m, ["price"] = 8m }, clerk.MembershipId), ct);
            unrouted.Value.Status.ShouldBe(BlockStatuses.Open);
            unrouted.Value.RequestId.ShouldBeNull();
            return outcome.Value;
        });
        raised.Status.ShouldBe(BlockStatuses.Pending);
        var requestId = raised.RequestId.ShouldNotBeNull();

        var blocks = await owner.GetOkAsync($"/api/v1/workflow/blocks?entityId={document.Id}");
        blocks.GetArrayLength().ShouldBe(2);
        blocks.EnumerateArray().Single(static b => b.GetProperty("kind").GetString() == "credit_limit").GetProperty("why").GetProperty("over_by").GetDecimal().ShouldBe(500m);

        var detail = await credit.Client.GetOkAsync($"/api/v1/workflow/requests/{requestId}");
        Detail(detail).GetProperty("definitionId").GetGuid().ShouldBe(definition);
        detail.GetProperty("block").GetProperty("kind").GetString().ShouldBe("credit_limit");
        detail.GetProperty("subject").GetProperty("customer").GetString().ShouldBe("ACME");
        (await credit.Client.PostErrorAsync($"/api/v1/workflow/requests/{requestId}/approve", new { }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.comment_required");
        var approved = await credit.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Pays weekly; ship." }, HttpStatusCode.OK);
        var granted = approved.GetProperty("override");
        granted.GetProperty("approvedBy").GetGuid().ShouldBe(credit.MembershipId);
        granted.GetProperty("reason").GetString().ShouldBe("Pays weekly; ship.");
        granted.GetProperty("consumed").GetBoolean().ShouldBeFalse();
        approved.GetProperty("block").GetProperty("status").GetString().ShouldBe("overridden");
        TestDocumentProvider.Decisions[document.Id].ShouldHaveSingleItem().OverrideId.ShouldBe(granted.GetProperty("id").GetGuid());

        var consumed = await host.InTenantAsync(ws.TenantId, async (sp, ct) =>
        {
            var engine = sp.GetRequiredService<IWorkflowEngine>();
            var first = await engine.ConsumeOverrideAsync("credit_limit", "test_document", document.Id, ct);
            var second = await engine.ConsumeOverrideAsync("credit_limit", "test_document", document.Id, ct);
            return (first, second);
        });
        consumed.first.ShouldBe(granted.GetProperty("id").GetGuid());
        consumed.second.ShouldBeNull();
        var overrides = await owner.GetOkAsync($"/api/v1/workflow/overrides?blockId={raised.BlockId}");
        overrides.Only().GetProperty("consumed").GetBoolean().ShouldBeTrue();
        (await owner.GetOkAsync($"/api/v1/workflow/blocks?entityId={document.Id}&status=cleared")).Only().GetProperty("kind").GetString().ShouldBe("credit_limit");
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task A_stand_in_acts_under_a_delegation_and_overdue_steps_escalate_to_their_target()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var approver = await InviteAsync(owner, ws, "reviewer");
        var standIn = await InviteAsync(owner, ws, "standin");
        var escalationTarget = await InviteAsync(owner, ws, "director");
        var clerk = await InviteAsync(owner, ws, "clerk");
        var companyId = Guid.CreateVersion7();
        await ActivateAsync(owner, Definition("test_document", "on_submit", "Two-step spend",
            Rule("All", "amount > 0",
                Step("Approver", new { membershipIds = new[] { approver.MembershipId } }, timeoutHours: 4, escalation: new { membershipIds = new[] { escalationTarget.MembershipId } }),
                Step("Owner", new { membershipIds = new[] { ws.MembershipId } }, allowDelegate: false))));

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        (await approver.Client.PostErrorAsync("/api/v1/workflow/delegations", new { toMembershipId = approver.MembershipId, validFrom = today, validTo = today }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.delegate_required");
        (await standIn.Client.PostErrorAsync("/api/v1/workflow/delegations", new { fromMembershipId = approver.MembershipId, toMembershipId = standIn.MembershipId, validFrom = today, validTo = today }, HttpStatusCode.Forbidden)).Code.ShouldBe("workflow.delegation_forbidden");
        var delegation = await approver.Client.PostAsync("/api/v1/workflow/delegations", new { toMembershipId = standIn.MembershipId, validFrom = today.AddDays(-1), validTo = today.AddDays(7), reason = "Leave" });
        (await standIn.Client.GetOkAsync("/api/v1/workflow/delegations")).Only().GetProperty("id").GetGuid().ShouldBe(delegation.GetProperty("id").GetGuid());

        // The stand-in sees the approver's request and acts on their behalf; the second step (owner) opens.
        var first = new TestDocument(Guid.CreateVersion7(), companyId, 100m, "USD");
        var firstRequest = (await SubmitAsync(ws, first, clerk.MembershipId)).RequestId!.Value;
        (await InboxAsync(standIn.Client)).Only().GetProperty("id").GetGuid().ShouldBe(firstRequest);
        var afterStandIn = await standIn.Client.PostAsync($"/api/v1/workflow/requests/{firstRequest}/approve", new { comment = "Covering for the approver." }, HttpStatusCode.OK);
        Detail(afterStandIn).GetProperty("status").GetString().ShouldBe("pending");
        Detail(afterStandIn).GetProperty("currentStepNo").GetInt32().ShouldBe(2);
        var act = afterStandIn.GetProperty("actions").EnumerateArray().Single(static a => a.GetProperty("action").GetString() == "approve");
        act.GetProperty("actor").GetGuid().ShouldBe(standIn.MembershipId);
        act.GetProperty("onBehalfOf").GetGuid().ShouldBe(approver.MembershipId);
        afterStandIn.GetProperty("steps")[0].GetProperty("approvedBy").EnumerateArray().Select(static a => a.GetGuid()).ShouldBe([approver.MembershipId]);
        (await standIn.Client.PostErrorAsync($"/api/v1/workflow/requests/{firstRequest}/approve", new { }, HttpStatusCode.Forbidden)).Code.ShouldBe("workflow.not_an_approver");
        (await owner.PostErrorAsync($"/api/v1/workflow/requests/{firstRequest}/delegate", new { toMembershipId = standIn.MembershipId }, HttpStatusCode.Conflict)).Code.ShouldBe("workflow.delegation_not_allowed");
        Detail(await owner.PostAsync($"/api/v1/workflow/requests/{firstRequest}/approve", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("approved");
        TestDocumentProvider.Decisions[first.Id].ShouldHaveSingleItem().Status.ShouldBe(WorkflowDecisions.Approved);

        // An approver hands one request to a colleague explicitly.
        var handed = new TestDocument(Guid.CreateVersion7(), companyId, 100m, "USD");
        var handedRequest = (await SubmitAsync(ws, handed, clerk.MembershipId)).RequestId!.Value;
        (await approver.Client.PostErrorAsync($"/api/v1/workflow/requests/{handedRequest}/delegate", new { toMembershipId = clerk.MembershipId }, HttpStatusCode.UnprocessableEntity)).Code.ShouldBe("workflow.self_approval");
        var delegated = await approver.Client.PostAsync($"/api/v1/workflow/requests/{handedRequest}/delegate", new { toMembershipId = escalationTarget.MembershipId, comment = "Your call." }, HttpStatusCode.OK);
        delegated.GetProperty("steps")[0].GetProperty("approvers").EnumerateArray().Select(static a => a.GetGuid()).ShouldBe([approver.MembershipId, escalationTarget.MembershipId]);
        Detail(await escalationTarget.Client.PostAsync($"/api/v1/workflow/requests/{handedRequest}/approve", new { }, HttpStatusCode.OK)).GetProperty("currentStepNo").GetInt32().ShouldBe(2);

        // Past its due time the step escalates: the target joins the approvers, the history says so, and the SLA restarts.
        var late = new TestDocument(Guid.CreateVersion7(), companyId, 100m, "USD");
        var lateRequest = (await SubmitAsync(ws, late, clerk.MembershipId)).RequestId!.Value;
        (await InboxAsync(escalationTarget.Client)).ShouldBeEmptyArray();
        await using (var db = new NpgsqlConnection(Api.Db.OwnerConnectionString))
        {
            await db.OpenAsync(TestContext.Current.CancellationToken);
            (await db.ExecuteAsync("UPDATE app.wf_request_steps SET due_at = '2000-01-01' WHERE tenant_id = @t AND request_id = @r AND step_no = 1", new { t = ws.TenantId, r = lateRequest })).ShouldBe(1);
            await db.ExecuteAsync("UPDATE app.wf_requests SET due_at = '2000-01-01' WHERE tenant_id = @t AND id = @r", new { t = ws.TenantId, r = lateRequest });
        }

        await host.EnqueueAsync("workflow.escalate", null);
        await host.RunWorkerAsync();
        var escalated = await escalationTarget.Client.GetOkAsync($"/api/v1/workflow/requests/{lateRequest}");
        var step = escalated.GetProperty("steps")[0];
        step.GetProperty("escalatedAt").ValueKind.ShouldBe(JsonValueKind.String);
        step.GetProperty("approvers").EnumerateArray().Select(static a => a.GetGuid()).ShouldBe([approver.MembershipId, escalationTarget.MembershipId]);
        step.GetProperty("dueAt").GetDateTimeOffset().ShouldBe(step.GetProperty("escalatedAt").GetDateTimeOffset().AddHours(4));
        escalated.GetProperty("actions").EnumerateArray().Select(static a => a.GetProperty("action").GetString()).ShouldBe(["submit", "escalate"]);
        (await InboxAsync(escalationTarget.Client)).Only().GetProperty("id").GetGuid().ShouldBe(lateRequest);
        await host.EnqueueAsync("workflow.escalate", null);
        await host.RunWorkerAsync();
        (await escalationTarget.Client.GetOkAsync($"/api/v1/workflow/requests/{lateRequest}")).GetProperty("actions").GetArrayLength().ShouldBe(2);
        await owner.AssertInvariantsAsync();
    }

    [Fact]
    public async Task Stock_adjustments_go_through_the_active_definition_and_post_when_finance_approves()
    {
        var ws = await Api.SignupAsync();
        var owner = Api.ClientFor(ws.AccessToken);
        var finance = await InviteAsync(owner, ws, "finance", "workflow.request.read", "inventory.adjustment.read");
        var company = await owner.PostAsync("/api/v1/organization/companies", new { code = "WFC", legalName = Name("Workflow Co", "شركة سير العمل"), country = "IQ", functionalCurrency = "IQD", timeZone = "Asia/Baghdad", costingMethod = "average" });
        var companyId = company.GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/accounting/charts/from-template", new { templateCode = "IFRS_SME", code = "MAIN", companyId });
        var warehouseId = (await owner.PostAsync("/api/v1/inventory/warehouses", new { companyId, code = "MAIN", name = Name("Main", "الرئيسي") })).GetProperty("id").GetGuid();
        await owner.PostAsync("/api/v1/items", new { code = "PUMP", name = Name("Pump", "مضخة"), baseUom = "PCS", type = "stock" });
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "FOUND", name = Name("Found", "عُثر عليه"), appliesTo = "adjustment" });
        await ActivateAsync(owner, Definition("stock_adjustment", "on_submit", "Adjustment control",
            Rule("Large adjustments", "amount >= 100000 or kind = 'scrap'", Step("Finance", "finance", requireComment: true))));

        static object Adjustment(Guid companyId, Guid warehouseId, decimal quantity, decimal unitCost) => new
        {
            companyId,
            warehouseId,
            kind = "positive",
            postingDate = new DateOnly(2026, 9, 10),
            reference = "WF",
            lines = new[] { new { itemCode = "PUMP", quantity, uom = "PCS", unitCost, reasonCode = "FOUND" } },
        };

        // Under the rule: submitted and posted in one go.
        var small = await owner.PostAsync("/api/v1/inventory/adjustments", Adjustment(companyId, warehouseId, 2m, 1000m));
        var smallPosted = await owner.PostAsync($"/api/v1/inventory/adjustments/{small.GetProperty("id").GetGuid()}/submit", new { }, HttpStatusCode.OK);
        smallPosted.GetProperty("status").GetString().ShouldBe("posted");
        smallPosted.GetProperty("number").GetString()!.ShouldStartWith("ADJ-");

        // Over the rule: pending with its request; the inventory approve endpoint defers to the workflow.
        var big = await owner.PostAsync("/api/v1/inventory/adjustments", Adjustment(companyId, warehouseId, 10m, 25000m));
        var bigId = big.GetProperty("id").GetGuid();
        var submitted = await owner.PostAsync($"/api/v1/inventory/adjustments/{bigId}/submit", new { }, HttpStatusCode.OK);
        submitted.GetProperty("status").GetString().ShouldBe("pending_approval");
        var request = (await owner.GetOkAsync($"/api/v1/workflow/requests?mine=false&entityType=stock_adjustment&entityId={bigId}")).Only();
        var requestId = request.GetProperty("id").GetGuid();
        request.GetProperty("status").GetString().ShouldBe("pending");
        (await owner.PostErrorAsync($"/api/v1/inventory/adjustments/{bigId}/approve", new { }, HttpStatusCode.Conflict)).Code.ShouldBe("adjustment.decided_by_workflow");
        (await owner.PostErrorAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Mine" }, HttpStatusCode.Forbidden)).Code.ShouldBe("workflow.self_approval");

        var detail = await finance.Client.GetOkAsync($"/api/v1/workflow/requests/{requestId}");
        detail.GetProperty("subject").GetProperty("amount").GetDecimal().ShouldBe(250000m);
        detail.GetProperty("subject").GetProperty("currency").GetString().ShouldBe("IQD");
        detail.GetProperty("subject").GetProperty("warehouseCode").GetString().ShouldBe("MAIN");
        var approved = await finance.Client.PostAsync($"/api/v1/workflow/requests/{requestId}/approve", new { comment = "Counted twice." }, HttpStatusCode.OK);
        Detail(approved).GetProperty("status").GetString().ShouldBe("approved");
        var posted = await owner.GetOkAsync($"/api/v1/inventory/adjustments/{bigId}");
        posted.GetProperty("status").GetString().ShouldBe("posted");
        posted.GetProperty("journalEntryId").ValueKind.ShouldBe(JsonValueKind.String);
        posted.GetProperty("approvedAt").ValueKind.ShouldBe(JsonValueKind.String);
        (await owner.GetOkAsync($"/api/v1/inventory/stock/balances?companyId={companyId}")).EnumerateArray().Sum(static b => b.GetProperty("onHand").GetDecimal()).ShouldBe(12m);

        // A rejection returns the adjustment with the reason; a cancelled adjustment withdraws its request.
        var scrap = await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId, warehouseId, kind = "scrap", postingDate = new DateOnly(2026, 9, 11), lines = new[] { new { itemCode = "PUMP", quantity = 1m, uom = "PCS", reasonCode = "BROKEN" } } }, HttpStatusCode.UnprocessableEntity);
        scrap.GetProperty("code").GetString().ShouldBe("adjustment.reason_required");
        await owner.PostAsync("/api/v1/inventory/reason-codes", new { code = "BROKEN", name = Name("Broken", "مكسور"), appliesTo = "scrap" });
        var scrapId = (await owner.PostAsync("/api/v1/inventory/adjustments", new { companyId, warehouseId, kind = "scrap", postingDate = new DateOnly(2026, 9, 11), lines = new[] { new { itemCode = "PUMP", quantity = 1m, uom = "PCS", reasonCode = "BROKEN" } } })).GetProperty("id").GetGuid();
        (await owner.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("pending_approval");
        var scrapRequest = (await finance.Client.GetOkAsync("/api/v1/workflow/requests")).Only().GetProperty("id").GetGuid();
        await finance.Client.PostAsync($"/api/v1/workflow/requests/{scrapRequest}/reject", new { comment = "Recount first." }, HttpStatusCode.OK);
        var rejected = await owner.GetOkAsync($"/api/v1/inventory/adjustments/{scrapId}");
        rejected.GetProperty("status").GetString().ShouldBe("rejected");
        rejected.GetProperty("rejectionReason").GetString().ShouldBe("Recount first.");
        (await owner.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/submit", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("pending_approval");
        (await owner.PostAsync($"/api/v1/inventory/adjustments/{scrapId}/cancel", new { }, HttpStatusCode.OK)).GetProperty("status").GetString().ShouldBe("cancelled");
        (await finance.Client.GetOkAsync("/api/v1/workflow/requests")).ShouldBeEmptyArray();
        (await owner.GetOkAsync($"/api/v1/workflow/requests?mine=false&entityId={scrapId}&status=cancelled")).GetArrayLength().ShouldBe(1);
        await owner.AssertInvariantsAsync();
    }
}
