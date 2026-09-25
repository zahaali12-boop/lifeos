using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Collaboration.Application;
using Quicker.Collaboration.Contracts;
using Quicker.Web;

namespace Quicker.Collaboration.Api;

/// <summary>/api/v1/collaboration: comments, activity timeline, document links, saved views and custom-field definitions.</summary>
public static class RecordEndpoints
{
    public static RouteGroupBuilder MapRecordEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var comments = group.MapGroup("/comments");
        comments.MapGet("/", async (string? entityType, Guid entityId, CommentService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(entityType, entityId, ct)))
            .RequirePermission(CollaborationPermissions.CommentRead);
        comments.MapGet("/mentionable", async (CommentService service, CancellationToken ct) => TypedResults.Ok(await service.MentionableAsync(ct)))
            .RequirePermission(CollaborationPermissions.CommentWrite)
            .WithSummary("Members a comment can mention: the active members by name, for anyone who comments");
        comments.MapPost("/", async (AddCommentRequest request, CommentService service, CancellationToken ct) =>
            ApiProblems.Created(await service.AddAsync(request, ct), static c => $"/api/v1/collaboration/comments/{c.Id}"))
            .RequirePermission(CollaborationPermissions.CommentWrite)
            .WithSummary("Comment on a record; mentioned members are notified");
        comments.MapPut("/{commentId:guid}", async (Guid commentId, EditCommentRequest request, CommentService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.EditAsync(commentId, request, ct)))
            .RequirePermission(CollaborationPermissions.CommentWrite);
        comments.MapDelete("/{commentId:guid}", async (Guid commentId, CommentService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(commentId, ct)))
            .RequirePermission(CollaborationPermissions.CommentWrite);

        group.MapGet("/activities", async (string? entityType, Guid entityId, [AsParameters] PageRequest page, ActivityService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(entityType, entityId, page, ct)))
            .RequirePermission(CollaborationPermissions.ActivityRead)
            .WithSummary("A record's timeline, newest first");

        var links = group.MapGroup("/links");
        links.MapGet("/", async (string? entityType, Guid entityId, DocumentLinkService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(entityType, entityId, ct)))
            .RequirePermission(CollaborationPermissions.LinkRead);
        links.MapPost("/", async (LinkRequest request, DocumentLinkService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static r => r.Created ? Results.Created($"/api/v1/collaboration/links/{r.Link.Id}", r.Link) : Results.Ok(r.Link)))
            .Produces<Contracts.DocumentLink>(StatusCodes.Status201Created)
            .Produces<Contracts.DocumentLink>()
            .RequirePermission(CollaborationPermissions.LinkManage)
            .WithSummary("Link two records (201) or return the existing link (200)");
        links.MapDelete("/{linkId:guid}", async (Guid linkId, DocumentLinkService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(linkId, ct)))
            .RequirePermission(CollaborationPermissions.LinkManage);

        var views = group.MapGroup("/views");
        views.MapGet("/", async (string? entityType, SavedViewService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(entityType, ct)))
            .WithSummary("My views and the shared views of an entity type");
        views.MapPost("/", async (SaveViewRequest request, SavedViewService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static v => $"/api/v1/collaboration/views/{v.Id}"));
        views.MapPut("/{viewId:guid}", async (Guid viewId, SaveViewRequest request, SavedViewService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(viewId, request, ct)));
        views.MapDelete("/{viewId:guid}", async (Guid viewId, SavedViewService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(viewId, ct)));

        var fields = group.MapGroup("/custom-fields");
        fields.MapGet("/", async (string? entityType, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(entityType, ct)))
            .WithSummary("Custom-field definitions of an entity type (any member: forms are rendered from them)");
        fields.MapGet("/hosts", () => TypedResults.Ok(new CustomFieldHostList(CustomFieldHosts.EntityTypes)))
            .WithSummary("The record types custom fields can be defined for (each module registers the tables that carry them)");
        fields.MapGet("/{entityType}/schema", async (string entityType, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SchemaAsync(entityType, ct)))
            .WithSummary("JSON Schema of the entity's custom fields");
        fields.MapGet("/{fieldId:guid}", async (Guid fieldId, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(fieldId, ct), "custom_field", fieldId));
        fields.MapPost("/", async (SaveCustomFieldRequest request, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.Created(await service.CreateAsync(request, ct), static f => $"/api/v1/collaboration/custom-fields/{f.Id}"))
            .RequirePermission(CollaborationPermissions.CustomFieldManage)
            .WithSummary("Define a custom field; indexed=true adds an expression index on the host table");
        fields.MapPut("/{fieldId:guid}", async (Guid fieldId, SaveCustomFieldRequest request, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UpdateAsync(fieldId, request, ct)))
            .RequirePermission(CollaborationPermissions.CustomFieldManage);
        fields.MapDelete("/{fieldId:guid}", async (Guid fieldId, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(fieldId, ct)))
            .RequirePermission(CollaborationPermissions.CustomFieldManage);

        return group;
    }
}
