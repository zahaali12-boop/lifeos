using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Quicker.Collaboration.Application;
using Quicker.Kernel.Results;
using Quicker.Web;

namespace Quicker.Collaboration.Api;

/// <summary>/api/v1/collaboration: the member's notifications and preferences, announcements, attachments.</summary>
public static class CollaborationEndpoints
{
    public static RouteGroupBuilder MapCollaborationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var group = api.MapGroup("/collaboration").WithTags("Collaboration").RequireAuthorization();

        var notifications = group.MapGroup("/notifications");
        notifications.MapGet("/", async (bool? unreadOnly, [AsParameters] PageRequest page, NotificationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListMineAsync(unreadOnly ?? false, page, ct)))
            .WithSummary("My notifications, newest first; page with limit and the returned nextCursor");
        notifications.MapGet("/unread-count", async (NotificationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.UnreadCountAsync(ct)));
        notifications.MapPost("/{notificationId:guid}/read", async (Guid notificationId, NotificationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.MarkReadAsync(notificationId, ct)));
        notifications.MapPost("/read-all", async (NotificationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.MarkAllReadAsync(ct)));
        notifications.MapGet("/preferences", async (NotificationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.GetPreferencesAsync(ct)));
        notifications.MapPut("/preferences", async (IReadOnlyList<PreferenceInput> preferences, NotificationService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.SetPreferencesAsync(preferences, ct)))
            .WithSummary("Replace my channel preferences: one row per kind, '*' for the default");
        notifications.MapPost("/announce", async (AnnounceRequest request, NotificationService service, CancellationToken ct) =>
            ApiProblems.Accepted(await service.AnnounceAsync(request, ct), static _ => null))
            .RequirePermission(CollaborationPermissions.NotificationAnnounce)
            .WithSummary("Notify every active member (in-app and email per their preferences)");

        var attachments = group.MapGroup("/attachments");
        attachments.MapPost("/", async (HttpRequest request, AttachmentService service, CancellationToken ct) =>
        {
            if (!request.HasFormContentType)
            {
                return ApiProblems.From(Error.Validation("attachment.form_required", "Send multipart/form-data with entityType, entityId and file."));
            }

            var form = await request.ReadFormAsync(ct);
            var file = form.Files.GetFile("file");
            if (file is null)
            {
                return ApiProblems.From(Error.Validation("attachment.file_required", "The 'file' part is required."));
            }

            if (!Guid.TryParse(form["entityId"].ToString(), out var entityId))
            {
                return ApiProblems.From(Error.Validation("attachment.entity_invalid", "entityId must be a record id."));
            }

            await using var content = file.OpenReadStream();
            return ApiProblems.Created(await service.UploadAsync(form["entityType"].ToString(), entityId, file.FileName, file.ContentType, content, ct), static a => $"/api/v1/collaboration/attachments/{a.Id}");
        }).RequirePermission(CollaborationPermissions.AttachmentManage)
          .WithMetadata(new MultipartFormMetadata(typeof(AttachmentUploadForm)))
          .WithSummary("Upload a file for a record (multipart/form-data: entityType, entityId, file)");
        attachments.MapGet("/", async (string? entityType, Guid entityId, AttachmentService service, CancellationToken ct) =>
            ApiProblems.Ok(await service.ListAsync(entityType, entityId, ct)))
            .RequirePermission(CollaborationPermissions.AttachmentRead);
        attachments.MapGet("/{attachmentId:guid}", async (Guid attachmentId, AttachmentService service, CancellationToken ct) =>
            ApiProblems.Found(await service.GetAsync(attachmentId, ct), "attachment", attachmentId))
            .RequirePermission(CollaborationPermissions.AttachmentRead);
        attachments.MapGet("/{attachmentId:guid}/content", async (Guid attachmentId, AttachmentService service, CancellationToken ct) =>
            ApiProblems.From(await service.OpenAsync(attachmentId, ct), static opened => Results.Stream(opened.Content.Content, opened.Attachment.ContentType, opened.Attachment.FileName)))
            .Produces<Stream>(StatusCodes.Status200OK, "application/octet-stream")
            .RequirePermission(CollaborationPermissions.AttachmentRead)
            .WithSummary("Download the file");
        attachments.MapDelete("/{attachmentId:guid}", async (Guid attachmentId, AttachmentService service, CancellationToken ct) =>
            ApiProblems.NoContent(await service.DeleteAsync(attachmentId, ct)))
            .RequirePermission(CollaborationPermissions.AttachmentManage);

        group.MapRecordEndpoints();
        return api;
    }
}

/// <summary>The multipart form an upload sends, declared so the contract shows its parts (the endpoint reads the form itself).</summary>
public sealed record AttachmentUploadForm(string EntityType, Guid EntityId, IFormFile File);
