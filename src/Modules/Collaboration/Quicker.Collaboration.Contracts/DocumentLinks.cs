namespace Quicker.Collaboration.Contracts;

public sealed record DocumentRef(string Type, Guid Id);

public static class LinkRelations
{
    public const string Related = "related";
    public const string Source = "source";
    public const string Fulfils = "fulfils";
    public const string Reverses = "reverses";
    public const string Settles = "settles";

    public static bool IsValid(string? relation) =>
        !string.IsNullOrEmpty(relation) && relation.Length <= 40 && relation.All(static c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');
}

public sealed record DocumentLink(Guid Id, DocumentRef From, DocumentRef To, string Relation, Guid? CreatedBy, DateTimeOffset CreatedAt);

/// <summary>Directed links between records of any module (invoice → journal, order → delivery), listed from either end.</summary>
public interface IDocumentLinks
{
    /// <summary>Creates the link or returns the existing one with the same ends and relation.</summary>
    Task<DocumentLink> LinkAsync(DocumentRef from, DocumentRef to, string relation, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DocumentLink>> ListAsync(DocumentRef document, CancellationToken cancellationToken = default);
}
