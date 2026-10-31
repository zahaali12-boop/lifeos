using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.FixedAssets.Domain;
using Quicker.FixedAssets.Persistence;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Organization.Contracts;

namespace Quicker.FixedAssets.Application;

/// <summary>Asset books (A-175): one per company posts to the ledger; memo books (a tax book) only compute their own depreciation.</summary>
public sealed class BookService(FixedAssetsDbContext db, ICompanyDirectory companies, IAuditSink audit, IClock clock)
{
    public const string EntityType = "asset_book";

    public async Task<IReadOnlyList<AssetBookSummary>> ListAsync(Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Books.AsNoTracking().AsQueryable();
        if (companyId is { } c)
        {
            query = query.Where(b => b.CompanyId == c);
        }

        return (await query.OrderBy(static b => b.CompanyId).ThenByDescending(static b => b.PostsToGl).ThenBy(static b => b.Code).ToListAsync(cancellationToken)).Select(Map).ToList();
    }

    public async Task<Result<AssetBookSummary>> SaveAsync(Guid? id, SaveAssetBookRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var code = Validation.UpperCode(request.Code, EntityType);
        if (code.IsFailure)
        {
            return code.Error!;
        }

        var name = Validation.Name(request.Name, EntityType);
        if (name.IsFailure)
        {
            return name.Error!;
        }

        if (await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken) is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        AssetBook? book = null;
        if (id is { } existing)
        {
            book = await db.Books.SingleOrDefaultAsync(b => b.Id == existing, cancellationToken);
            if (book is null)
            {
                return Error.NotFound(EntityType, existing);
            }

            if (book.CompanyId != request.CompanyId)
            {
                return Error.Validation("asset_book.company_fixed", "A book stays in its company.");
            }

            if (book.PostsToGl != request.PostsToGl && await db.AssetBooks.AnyAsync(v => v.BookId == book.Id, cancellationToken))
            {
                return Error.Conflict("asset_book.in_use", "Whether a book posts to the ledger cannot change once assets are valued in it.");
            }
        }

        if (await db.Books.AnyAsync(b => b.CompanyId == request.CompanyId && b.Code == code.Value && (book == null || b.Id != book.Id), cancellationToken))
        {
            return Error.Conflict("asset_book.code_taken", $"The company already has a book '{code.Value}'.");
        }

        if (request.PostsToGl && await db.Books.AnyAsync(b => b.CompanyId == request.CompanyId && b.PostsToGl && (book == null || b.Id != book.Id), cancellationToken))
        {
            return Error.Conflict("asset_book.gl_book_exists", "One book per company posts to the ledger; make this one a memo book.");
        }

        var isNew = book is null;
        book ??= new AssetBook { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
        book.Code = code.Value;
        book.Name = name.Value;
        book.PostsToGl = request.PostsToGl;
        book.IsActive = request.IsActive;
        book.UpdatedAt = clock.UtcNow;
        if (isNew)
        {
            db.Books.Add(book);
        }

        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, book.Id, book.Code, isNew ? AuditActions.Created : AuditActions.Updated, After: new { book.Code, book.PostsToGl, book.IsActive }, CompanyId: book.CompanyId), cancellationToken);
        return Map(book);
    }

    /// <summary>The company's book that posts to the ledger, created as "MAIN" the first time the company needs one.</summary>
    public async Task<AssetBook> EnsureGlBookAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var book = await db.Books.SingleOrDefaultAsync(b => b.CompanyId == companyId && b.PostsToGl, cancellationToken);
        if (book is not null)
        {
            return book;
        }

        book = new AssetBook
        {
            Id = Guid.CreateVersion7(),
            CompanyId = companyId,
            Code = "MAIN",
            Name = LocalizedText.Bilingual("Main book (posts to the ledger)", "الدفتر الرئيسي (يرحّل إلى الأستاذ)"),
            PostsToGl = true,
            IsActive = true,
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
        };
        db.Books.Add(book);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(EntityType, book.Id, book.Code, AuditActions.Created, After: new { book.Code, book.PostsToGl }, CompanyId: companyId), cancellationToken);
        return book;
    }

    private static AssetBookSummary Map(AssetBook b) => new(b.Id, b.CompanyId, b.Code, b.Name.Values, b.PostsToGl, b.IsActive, b.UpdatedAt);
}
