using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Web;
using Quicker.Workflow.Application;

namespace Quicker.Workflow.Api;

/// <summary>The workflow surface of /api/v1: the catalogue and expression check, definitions, the approvals inbox and actions, blocks, overrides, delegations.</summary>
public static class WorkflowEndpoints
{
    public static RouteGroupBuilder MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var workflow = api.MapGroup("/workflow").WithTags("Workflow").RequireAuthorization();

        workflow.MapGet("/catalogue", (DefinitionService service) => TypedResults.Ok(service.Catalogue()))
            .RequirePermission(WorkflowPermissions.DefinitionRead)
            .WithSummary("The entity types modules register for approval, their fields for rules, the block kinds they raise, the triggers and the functions of the expression grammar");
        workflow.MapPost("/expressions/validate", (ValidateExpressionRequest request, DefinitionService service) => TypedResults.Ok(service.Validate(request)))
            .RequirePermission(WorkflowPermissions.DefinitionRead)
            .WithSummary("Parses and type-checks a condition against an entity type's fields");

        var definitions = workflow.MapGroup("/definitions");
        definitions.MapGet("/", async (string? entityType, string? status, DefinitionService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(entityType, status, ct)))
            .RequirePermission(WorkflowPermissions.DefinitionRead);
        definitions.MapPost("/", async (SaveDefinitionRequest request, DefinitionService service, CancellationToken ct) => ApiProblems.Created(await service.CreateAsync(request, ct), static d => $"/api/v1/workflow/definitions/{d.Id}"))
            .RequirePermission(WorkflowPermissions.DefinitionManage)
            .WithSummary("A draft definition: entity type, trigger, ordered rules with conditions in the safe expression grammar, and the steps each rule requires");
        definitions.MapGet("/{definitionId:guid}", async (Guid definitionId, DefinitionService service, CancellationToken ct) => ApiProblems.Found(await service.GetAsync(definitionId, ct), "workflow_definition", definitionId))
            .RequirePermission(WorkflowPermissions.DefinitionRead);
        definitions.MapPut("/{definitionId:guid}", async (Guid definitionId, SaveDefinitionRequest request, DefinitionService service, CancellationToken ct) => ApiProblems.Ok(await service.UpdateAsync(definitionId, request, ct)))
            .RequirePermission(WorkflowPermissions.DefinitionManage)
            .WithSummary("Edits a draft in place; an active definition gets a new draft version in its lineage");
        definitions.MapPost("/{definitionId:guid}/activate", async (Guid definitionId, DefinitionService service, CancellationToken ct) => ApiProblems.Ok(await service.ActivateAsync(definitionId, ct)))
            .RequirePermission(WorkflowPermissions.DefinitionManage)
            .WithSummary("Activates a draft and retires the version active for the same entity type, trigger and block kind");
        definitions.MapPost("/{definitionId:guid}/retire", async (Guid definitionId, DefinitionService service, CancellationToken ct) => ApiProblems.Ok(await service.RetireAsync(definitionId, ct)))
            .RequirePermission(WorkflowPermissions.DefinitionManage);

        var requests = workflow.MapGroup("/requests");
        requests.MapGet("/", async Task<Results<Ok<IReadOnlyList<RequestSummary>>, ProblemHttpResult>> (bool? mine, string? status, string? entityType, Guid? entityId, WorkflowEngine engine, ICurrentPrincipal principal, CancellationToken ct) =>
        {
            if (mine != false)
            {
                return TypedResults.Ok(await engine.ListMineAsync(ct));
            }

            if (principal.Principal?.Has(WorkflowPermissions.RequestRead) != true)
            {
                return ApiProblems.From(Error.Forbidden("auth.forbidden", "Listing every request needs the permission " + WorkflowPermissions.RequestRead + ".").WithWhy(("permission", WorkflowPermissions.RequestRead)));
            }

            return TypedResults.Ok(await engine.ListAsync(status, entityType, entityId, ct));
        }).WithSummary("The approvals inbox: the requests the caller may decide (default), or every request with filters for readers");
        requests.MapGet("/{requestId:guid}", async (Guid requestId, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.GetAsync(requestId, ct)))
            .WithSummary("A request with its evaluation (which rule, which values), steps, approvers, history, block and override");
        requests.MapPost("/{requestId:guid}/approve", async (Guid requestId, ActionRequest? request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.DetailAfterAsync(await engine.ActAsync(requestId, "approve", request?.Comment, null, ct), ct)));
        requests.MapPost("/{requestId:guid}/reject", async (Guid requestId, ActionRequest request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.DetailAfterAsync(await engine.ActAsync(requestId, "reject", request.Comment, null, ct), ct)));
        requests.MapPost("/{requestId:guid}/request-changes", async (Guid requestId, ActionRequest request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.DetailAfterAsync(await engine.ActAsync(requestId, "request_changes", request.Comment, null, ct), ct)));
        requests.MapPost("/{requestId:guid}/delegate", async (Guid requestId, DelegateRequest request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.DetailAfterAsync(await engine.ActAsync(requestId, "delegate", request.Comment, request.ToMembershipId, ct), ct)));
        requests.MapPost("/{requestId:guid}/comment", async (Guid requestId, ActionRequest request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.DetailAfterAsync(await engine.ActAsync(requestId, "comment", request.Comment, null, ct), ct)));
        requests.MapPost("/{requestId:guid}/cancel", async (Guid requestId, CancelRequest request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Ok(await engine.DetailAfterAsync(await engine.ActAsync(requestId, "cancel", request.Reason, null, ct), ct)));

        workflow.MapGet("/blocks", async (string? entityType, Guid? entityId, string? status, WorkflowEngine engine, CancellationToken ct) => TypedResults.Ok(await engine.ListBlocksAsync(entityType, entityId, status, ct)))
            .RequirePermission(WorkflowPermissions.RequestRead);
        workflow.MapGet("/overrides", async (Guid? blockId, WorkflowEngine engine, CancellationToken ct) => TypedResults.Ok(await engine.ListOverridesAsync(blockId, ct)))
            .RequirePermission(WorkflowPermissions.RequestRead);

        var delegations = workflow.MapGroup("/delegations");
        delegations.MapGet("/", async (bool? all, WorkflowEngine engine, ICurrentPrincipal principal, CancellationToken ct) => TypedResults.Ok(await engine.ListDelegationsAsync(all == true && principal.Principal?.Has(WorkflowPermissions.RequestManage) == true, ct)));
        delegations.MapPost("/", async (SaveDelegationRequest request, WorkflowEngine engine, CancellationToken ct) => ApiProblems.Created(await engine.CreateDelegationAsync(request, ct), static d => $"/api/v1/workflow/delegations/{d.Id}"))
            .WithSummary("One member acts for another between two dates, for every definition or one; managers may delegate for others");
        delegations.MapDelete("/{delegationId:guid}", async (Guid delegationId, WorkflowEngine engine, CancellationToken ct) => ApiProblems.NoContent(await engine.DeleteDelegationAsync(delegationId, ct)));

        return api;
    }
}
