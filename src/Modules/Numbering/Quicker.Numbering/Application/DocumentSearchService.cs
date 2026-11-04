using System.Data;
using System.Globalization;
using Dapper;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Persistence;

namespace Quicker.Numbering.Application;

/// <summary>A numbered document found by its number: its type and id (to open it), the number, its company and when it was issued.</summary>
public sealed record DocumentSearchHit(string DocumentType, Guid DocumentId, string Number, Guid CompanyId, string? CompanyCode, DateTimeOffset IssuedAt);

/// <summary>
/// Finds documents by the numbers this module issued, across every module that numbers through it: "PO-2026-00027",
/// part of it ("00027", "2026-000"), or a prefix and a sequence without its padding ("PO-27" finds PO-2026-00027).
/// Only the types the member may read are listed, and within them only the companies their grant covers.
/// </summary>
public sealed class DocumentSearchService(IUnitOfWorkAccessor unitOfWork, ICurrentPrincipal principal, ICompanyDirectory companies)
{
    public const int MinQueryLength = 2;
    public const int MaxQueryLength = 64;
    public const int MaxResults = 25;

    public async Task<Result<IReadOnlyList<DocumentSearchHit>>> SearchAsync(string? q, int? limit, CancellationToken cancellationToken)
    {
        var term = q?.Trim() ?? string.Empty;
        if (term.Length is < MinQueryLength or > MaxQueryLength)
        {
            return Error.Validation("document_search.query_invalid", $"Search for between {MinQueryLength} and {MaxQueryLength} characters of a document number.").WithWhy(("q", term));
        }

        // The readable types, grouped by the companies the member's grant covers (null: every company).
        var actor = principal.Required;
        var readable = NumberedDocumentTypes.All
            .Select(t => (t.DocumentType, Scopes: actor.ScopesFor(t.ReadPermission)))
            .Where(static t => t.Scopes is not null)
            .GroupBy(static t => t.Scopes!.CompanyIds.Count == 0 ? string.Empty : string.Join(',', t.Scopes!.CompanyIds.Order()), StringComparer.Ordinal)
            .ToList();
        if (readable.Count == 0)
        {
            return Array.Empty<DocumentSearchHit>();
        }

        var parameters = new DynamicParameters();
        var clauses = new List<string>();
        for (var i = 0; i < readable.Count; i++)
        {
            parameters.Add($"t{i}", readable[i].Select(static t => t.DocumentType).ToArray());
            if (readable[i].Key.Length == 0)
            {
                clauses.Add($"a.document_type = ANY(@t{i})");
            }
            else
            {
                parameters.Add($"c{i}", readable[i].First().Scopes!.CompanyIds.ToArray());
                clauses.Add($"(a.document_type = ANY(@t{i}) AND s.company_id = ANY(@c{i}))");
            }
        }

        var escaped = Like(term);
        parameters.Add("q", term);
        parameters.Add("contains", $"%{escaped}%");
        parameters.Add("prefix", $"{escaped}%");
        var (numberStart, seq) = TrailingSequence(term);
        parameters.Add("seq", seq, DbType.Int64);
        parameters.Add("seqPrefix", seq is null ? null : $"{Like(numberStart)}%", DbType.String);
        parameters.Add("limit", Math.Clamp(limit ?? 10, 1, MaxResults));

        var uow = unitOfWork.Current;
        var rows = (await uow.Connection.QueryAsync<(string DocumentType, Guid DocumentId, string Number, Guid CompanyId, DateTime IssuedAt)>(new CommandDefinition($"""
            SELECT a.document_type, a.document_id, a.text, s.company_id, a.allocated_at
            FROM app.num_allocations a
            JOIN app.num_series s ON s.tenant_id = a.tenant_id AND s.id = a.series_id
            WHERE (a.text ILIKE @contains ESCAPE '\' OR (a.number = @seq AND a.text ILIKE @seqPrefix ESCAPE '\'))
              AND ({string.Join(" OR ", clauses)})
            ORDER BY lower(a.text) = lower(@q) DESC,
                     a.number IS NOT DISTINCT FROM @seq DESC,
                     a.text ILIKE @prefix ESCAPE '\' DESC,
                     a.allocated_at DESC
            LIMIT @limit
            """, parameters, uow.Transaction, cancellationToken: cancellationToken))).ToList();

        var codes = new Dictionary<Guid, string?>();
        foreach (var companyId in rows.Select(static r => r.CompanyId).Distinct())
        {
            codes[companyId] = (await companies.FindAsync(new CompanyId(companyId), cancellationToken))?.Code;
        }

        return rows.Select(r => new DocumentSearchHit(r.DocumentType, r.DocumentId, r.Number, r.CompanyId, codes[r.CompanyId], new DateTimeOffset(DateTime.SpecifyKind(r.IssuedAt, DateTimeKind.Utc)))).ToList();
    }

    private static string Like(string value) => value.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);

    /// <summary>The digits at the end of the search ("PO-27", "2026-27", "27") read as a sequence, with what comes before them as the number's start.</summary>
    private static (string Start, long? Sequence) TrailingSequence(string term)
    {
        var digits = 0;
        while (digits < term.Length && char.IsAsciiDigit(term[term.Length - 1 - digits]))
        {
            digits++;
        }

        return digits is > 0 and <= 18 && long.TryParse(term.AsSpan(term.Length - digits), NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? (term[..^digits], sequence)
            : (term, null);
    }
}
