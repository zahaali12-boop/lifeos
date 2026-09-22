using Microsoft.EntityFrameworkCore;
using Quicker.Collaboration.Contracts;
using Quicker.Collaboration.Domain;
using Quicker.Collaboration.Persistence;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Persistence;

namespace Quicker.Collaboration.Application;

public sealed record LinkRequest(DocumentRef From, DocumentRef To, string Relation);

/// <summary>Directed links between records; creating one is idempotent per (from, to, relation) and lands on both timelines.</summary>
public sealed class DocumentLinkService(CollaborationDbContext db, IUnitOfWorkAccessor unitOfWork, IActivityLog activities, IClock clock) : IDocumentLinks
{
    public async Task<DocumentLink> LinkAsync(DocumentRef from, DocumentRef to, string relation, CancellationToken cancellationToken = default)
    {
        var created = await CreateAsync(new LinkRequest(from, to, relation), cancellationToken);
        return created.IsSuccess ? created.Value.Link : throw new ArgumentException(created.Error!.Message, nameof(from));
    }

    public async Task<IReadOnlyList<DocumentLink>> ListAsync(DocumentRef document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var listed = await ListAsync(document.Type, document.Id, cancellationToken);
        return listed.IsSuccess ? listed.Value : throw new ArgumentException(listed.Error!.Message, nameof(document));
    }

    public async Task<Result<IReadOnlyList<DocumentLink>>> ListAsync(string? entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var type = entityType?.Trim() ?? string.Empty;
        if (!EntityTypes.IsValid(type) || entityId == Guid.Empty)
        {
            return Error.Validation("link.entity_invalid", "entityType is a lower-case name such as sales_invoice and entityId a record id.");
        }

        var rows = await db.Links.Where(l => (l.FromType == type && l.FromId == entityId) || (l.ToType == type && l.ToId == entityId)).OrderBy(static l => l.Id).ToListAsync(cancellationToken);
        return rows.Select(Map).ToList();
    }

    /// <summary>Returns the link and whether it was created now (false: it already existed).</summary>
    public async Task<Result<(DocumentLink Link, bool Created)>> CreateAsync(LinkRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.From is null || request.To is null || !EntityTypes.IsValid(request.From.Type) || !EntityTypes.IsValid(request.To.Type) || request.From.Id == Guid.Empty || request.To.Id == Guid.Empty)
        {
            return Error.Validation("link.entity_invalid", "Both ends need an entity type (lower-case name) and a record id.");
        }

        if (!LinkRelations.IsValid(request.Relation))
        {
            return Error.Validation("link.relation_invalid", "The relation is a lower-case name such as source, fulfils or related.");
        }

        if (request.From == request.To)
        {
            return Error.Validation("link.self", "A document cannot be linked to itself.");
        }

        var existing = await db.Links.SingleOrDefaultAsync(l => l.FromType == request.From.Type && l.FromId == request.From.Id && l.ToType == request.To.Type && l.ToId == request.To.Id && l.Relation == request.Relation, cancellationToken);
        if (existing is not null)
        {
            return (Map(existing), false);
        }

        var link = new DocumentLinkRecord
        {
            Id = Guid.CreateVersion7(),
            FromType = request.From.Type,
            FromId = request.From.Id,
            ToType = request.To.Type,
            ToId = request.To.Id,
            Relation = request.Relation,
            CreatedBy = unitOfWork.Current.Context.MembershipId?.Value,
            CreatedAt = clock.UtcNow,
        };
        db.Links.Add(link);
        await db.SaveChangesAsync(cancellationToken);
        var data = new { linkId = link.Id, from = request.From, to = request.To, relation = request.Relation };
        await activities.RecordAsync(new ActivityEntry(link.FromType, link.FromId, ActivityKinds.LinkAdded, LocalizedText.Bilingual($"Linked to {link.ToType} ({link.Relation})", $"رُبط بـ {link.ToType} ({link.Relation})"), data), cancellationToken);
        await activities.RecordAsync(new ActivityEntry(link.ToType, link.ToId, ActivityKinds.LinkAdded, LocalizedText.Bilingual($"Linked from {link.FromType} ({link.Relation})", $"رُبط من {link.FromType} ({link.Relation})"), data), cancellationToken);
        return (Map(link), true);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var link = await db.Links.SingleOrDefaultAsync(l => l.Id == id, cancellationToken);
        if (link is null)
        {
            return Error.NotFound("link", id);
        }

        db.Links.Remove(link);
        await db.SaveChangesAsync(cancellationToken);
        var data = new { linkId = link.Id, relation = link.Relation };
        await activities.RecordAsync(new ActivityEntry(link.FromType, link.FromId, ActivityKinds.LinkRemoved, LocalizedText.Bilingual($"Unlinked from {link.ToType}", $"فُكّ الربط مع {link.ToType}"), data), cancellationToken);
        await activities.RecordAsync(new ActivityEntry(link.ToType, link.ToId, ActivityKinds.LinkRemoved, LocalizedText.Bilingual($"Unlinked from {link.FromType}", $"فُكّ الربط مع {link.FromType}"), data), cancellationToken);
        return Result.Success();
    }

    private static DocumentLink Map(DocumentLinkRecord l) => new(l.Id, new DocumentRef(l.FromType, l.FromId), new DocumentRef(l.ToType, l.ToId), l.Relation, l.CreatedBy, l.CreatedAt);
}
