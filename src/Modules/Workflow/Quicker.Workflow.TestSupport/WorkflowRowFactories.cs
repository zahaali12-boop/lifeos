using Dapper;
using Npgsql;
using Quicker.Testing;

namespace Quicker.Workflow.TestSupport;

/// <summary>Minimal valid rows for every Workflow tenant table, so the isolation suite (hard scenario 18) covers them.</summary>
public static class WorkflowRowFactories
{
    private static bool _registered;

    public static void RegisterAll()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;

        IsolationRegistry.Register("app.wf_definitions", static async (c, tx, t) => new RowRef("app.wf_definitions", $"id = '{await DefinitionAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.wf_rules", static async (c, tx, t) => new RowRef("app.wf_rules", $"id = '{(await RuleAsync(c, tx, t)).Rule}'"));
        IsolationRegistry.Register("app.wf_steps", static async (c, tx, t) =>
        {
            var (_, rule) = await RuleAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.wf_steps (tenant_id, id, rule_id, sort_order, approver_kind, approver_spec) VALUES (@t, @id, @rule, 1, 'users', '{\"membershipIds\": []}'::jsonb)", new { t, id, rule }, tx);
            return new RowRef("app.wf_steps", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.wf_blocks", static async (c, tx, t) => new RowRef("app.wf_blocks", $"id = '{await BlockAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.wf_requests", static async (c, tx, t) => new RowRef("app.wf_requests", $"id = '{await RequestAsync(c, tx, t)}'"));
        IsolationRegistry.Register("app.wf_request_steps", static async (c, tx, t) =>
        {
            var request = await RequestAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.wf_request_steps (tenant_id, id, request_id, step_no, mode) VALUES (@t, @id, @request, 1, 'any')", new { t, id, request }, tx);
            return new RowRef("app.wf_request_steps", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.wf_actions", static async (c, tx, t) =>
        {
            var request = await RequestAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.wf_actions (tenant_id, id, request_id, action) VALUES (@t, @id, @request, 'submit')", new { t, id, request }, tx);
            return new RowRef("app.wf_actions", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.wf_overrides", static async (c, tx, t) =>
        {
            var block = await BlockAsync(c, tx, t);
            var request = await RequestAsync(c, tx, t);
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.wf_overrides (tenant_id, id, block_id, request_id, reason, expires_at) VALUES (@t, @id, @block, @request, 'probe', now() + interval '1 day')", new { t, id, block, request }, tx);
            return new RowRef("app.wf_overrides", $"id = '{id}'");
        });
        IsolationRegistry.Register("app.wf_delegations", static async (c, tx, t) =>
        {
            var id = Guid.CreateVersion7();
            await c.ExecuteAsync("INSERT INTO app.wf_delegations (tenant_id, id, from_membership_id, to_membership_id, valid_from, valid_to) VALUES (@t, @id, @from, @to, current_date, current_date)", new { t, id, from = Guid.CreateVersion7(), to = Guid.CreateVersion7() }, tx);
            return new RowRef("app.wf_delegations", $"id = '{id}'");
        });
    }

    private static async Task<Guid> DefinitionAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.wf_definitions (tenant_id, id, lineage_id, entity_type, trigger) VALUES (@t, @id, @id, 'probe', 'on_submit')", new { t, id }, tx);
        return id;
    }

    private static async Task<(Guid Definition, Guid Rule)> RuleAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var definition = await DefinitionAsync(c, tx, t);
        var rule = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.wf_rules (tenant_id, id, definition_id, sort_order, condition) VALUES (@t, @rule, @definition, 1, 'true')", new { t, rule, definition }, tx);
        return (definition, rule);
    }

    private static async Task<Guid> BlockAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.wf_blocks (tenant_id, id, kind, entity_type, entity_id, display) VALUES (@t, @id, 'probe', 'probe', @entity, 'probe')", new { t, id, entity = Guid.CreateVersion7() }, tx);
        return id;
    }

    private static async Task<Guid> RequestAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid t)
    {
        var definition = await DefinitionAsync(c, tx, t);
        var id = Guid.CreateVersion7();
        await c.ExecuteAsync("INSERT INTO app.wf_requests (tenant_id, id, definition_id, definition_version, entity_type, entity_id, display) VALUES (@t, @id, @definition, 1, 'probe', @entity, 'probe')", new { t, id, definition, entity = Guid.CreateVersion7() }, tx);
        return id;
    }
}
