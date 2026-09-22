using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Collaboration.Application;
using Quicker.Kernel.Results;
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
            ApiProblems.From(await service.ListAsync(entityType, entityId, ct), static c => Results.Ok(c)))
            .RequirePermission(CollaborationPermissions.CommentRead);
        comments.MapPost("/", async (AddCommentRequest request, CommentService service, CancellationToken ct) =>
            ApiProblems.From(await service.AddAsync(request, ct), static c => Results.Created($"/api/v1/collaboration/comments/{c.Id}", c)))
            .RequirePermission(CollaborationPermissions.CommentWrite)
            .WithSummary("Comment on a record; mentioned members are notified");
        comments.MapPut("/{commentId:guid}", async (Guid commentId, EditCommentRequest request, CommentService service, CancellationToken ct) =>
            ApiProblems.From(await service.EditAsync(commentId, request, ct), static c => Results.Ok(c)))
            .RequirePermission(CollaborationPermissions.CommentWrite);
        comments.MapDelete("/{commentId:guid}", async (Guid commentId, CommentService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteAsync(commentId, ct), static () => Results.NoContent()))
            .RequirePermission(CollaborationPermissions.CommentWrite);

        group.MapGet("/activities", async (string? entityType, Guid entityId, int? limit, ActivityService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListAsync(entityType, entityId, limit ?? 100, ct), static a => Results.Ok(a)))
            .RequirePermission(CollaborationPermissions.ActivityRead)
            .WithSummary("A record's timeline, newest first");

        var links = group.MapGroup("/links");
        links.MapGet("/", async (string? entityType, Guid entityId, DocumentLinkService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListAsync(entityType, entityId, ct), static l => Results.Ok(l)))
            .RequirePermission(CollaborationPermissions.LinkRead);
        links.MapPost("/", async (LinkRequest request, DocumentLinkService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static r => r.Created ? Results.Created($"/api/v1/collaboration/links/{r.Link.Id}", r.Link) : Results.Ok(r.Link)))
            .RequirePermission(CollaborationPermissions.LinkManage)
            .WithSummary("Link two records (201) or return the existing link (200)");
        links.MapDelete("/{linkId:guid}", async (Guid linkId, DocumentLinkService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteAsync(linkId, ct), static () => Results.NoContent()))
            .RequirePermission(CollaborationPermissions.LinkManage);

        var views = group.MapGroup("/views");
        views.MapGet("/", async (string? entityType, SavedViewService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListAsync(entityType, ct), static v => Results.Ok(v)))
            .WithSummary("My views and the shared views of an entity type");
        views.MapPost("/", async (SaveViewRequest request, SavedViewService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static v => Results.Created($"/api/v1/collaboration/views/{v.Id}", v)));
        views.MapPut("/{viewId:guid}", async (Guid viewId, SaveViewRequest request, SavedViewService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(viewId, request, ct), static v => Results.Ok(v)));
        views.MapDelete("/{viewId:guid}", async (Guid viewId, SavedViewService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteAsync(viewId, ct), static () => Results.NoContent()));

        var fields = group.MapGroup("/custom-fields");
        fields.MapGet("/", async (string? entityType, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.From(await service.ListAsync(entityType, ct), static f => Results.Ok(f)))
            .WithSummary("Custom-field definitions of an entity type (any member: forms are rendered from them)");
        fields.MapGet("/{entityType}/schema", async (string entityType, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.From(await service.SchemaAsync(entityType, ct), static s => Results.Ok(s)))
            .WithSummary("JSON Schema of the entity's custom fields");
        fields.MapGet("/{fieldId:guid}", async (Guid fieldId, CustomFieldService service, CancellationToken ct) =>
            await service.GetAsync(fieldId, ct) is { } f ? Results.Ok(f) : ApiProblems.From(Error.NotFound("custom_field", fieldId)));
        fields.MapPost("/", async (SaveCustomFieldRequest request, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.From(await service.CreateAsync(request, ct), static f => Results.Created($"/api/v1/collaboration/custom-fields/{f.Id}", f)))
            .RequirePermission(CollaborationPermissions.CustomFieldManage)
            .WithSummary("Define a custom field; indexed=true adds an expression index on the host table");
        fields.MapPut("/{fieldId:guid}", async (Guid fieldId, SaveCustomFieldRequest request, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.From(await service.UpdateAsync(fieldId, request, ct), static f => Results.Ok(f)))
            .RequirePermission(CollaborationPermissions.CustomFieldManage);
        fields.MapDelete("/{fieldId:guid}", async (Guid fieldId, CustomFieldService service, CancellationToken ct) =>
            ApiProblems.From(await service.DeleteAsync(fieldId, ct), static () => Results.NoContent()))
            .RequirePermission(CollaborationPermissions.CustomFieldManage);

        return group;
    }
}
