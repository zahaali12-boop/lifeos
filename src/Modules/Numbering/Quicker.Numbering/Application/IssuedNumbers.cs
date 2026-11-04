using Dapper;
using Quicker.Numbering.Contracts;
using Quicker.Persistence;

namespace Quicker.Numbering.Application;

/// <summary>Issued numbers by document and documents by number, from the allocation log (RLS keeps them to the tenant).</summary>
public sealed class IssuedNumbers(IUnitOfWorkAccessor unitOfWork) : IIssuedNumbers
{
    public async Task<IReadOnlyDictionary<Guid, string>> NumbersOfAsync(IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentIds);
        if (documentIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var uow = unitOfWork.Current;
        var rows = await uow.Connection.QueryAsync<(Guid DocumentId, string Text)>(new CommandDefinition("""
            SELECT DISTINCT ON (document_id) document_id, text
            FROM app.num_allocations
            WHERE document_id = ANY(@ids)
            ORDER BY document_id, allocated_at DESC
            """, new { ids = documentIds.Distinct().ToArray() }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToDictionary(static r => r.DocumentId, static r => r.Text);
    }

    public async Task<IReadOnlyList<Guid>> DocumentsNumberedAsync(string prefix, int limit = 500, CancellationToken cancellationToken = default)
    {
        var start = prefix?.Trim() ?? string.Empty;
        if (start.Length == 0)
        {
            return [];
        }

        var uow = unitOfWork.Current;
        var escaped = start.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
        var rows = await uow.Connection.QueryAsync<Guid>(new CommandDefinition("""
            SELECT DISTINCT document_id FROM app.num_allocations WHERE text ILIKE @pattern ESCAPE '\' LIMIT @limit
            """, new { pattern = escaped + "%", limit = Math.Clamp(limit, 1, 5000) }, uow.Transaction, cancellationToken: cancellationToken));
        return rows.ToList();
    }
}
