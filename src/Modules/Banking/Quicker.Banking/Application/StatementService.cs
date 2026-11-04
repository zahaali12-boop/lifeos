using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Audit.Contracts;
using Quicker.Banking.Contracts;
using Quicker.Banking.Domain;
using Quicker.Banking.Persistence;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Results;
using Quicker.Kernel.Time;

namespace Quicker.Banking.Application;

/// <summary>
/// Bank statements (roadmap 6.2): CSV mappings a bank's export is read with, the import of a file in any of the four
/// formats into lines (the same line twice adds nothing, by its import key), and the statements of an account with
/// what the matcher has done to them.
/// </summary>
public sealed class StatementService(BankingDbContext db, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    // ------------------------------------------------------------------ mappings

    public async Task<IReadOnlyList<StatementMappingSummary>> ListMappingsAsync(CancellationToken cancellationToken)
    {
        var mappings = await db.StatementMappings.AsNoTracking().OrderBy(static m => m.Name).ToListAsync(cancellationToken);
        return mappings.Select(Map).ToList();
    }

    public async Task<Result<StatementMappingSummary>> SaveMappingAsync(Guid? id, SaveStatementMappingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name.Trim();
        if (name.Length is 0 or > 80)
        {
            return Error.Validation("statement_mapping.name_invalid", "A mapping has a name of up to 80 characters.");
        }

        var settings = request.Settings ?? new StatementMappingSettings();
        if (string.IsNullOrWhiteSpace(settings.DateColumn) && !settings.HasHeader)
        {
            return Error.Validation("statement_mapping.date_column_required", "Without a header row, the date column is named by its position.");
        }

        if (string.IsNullOrWhiteSpace(settings.AmountColumn) && (string.IsNullOrWhiteSpace(settings.DebitColumn) || string.IsNullOrWhiteSpace(settings.CreditColumn)) && !settings.HasHeader)
        {
            return Error.Validation("statement_mapping.amount_column_required", "Without a header row, the amount column (or debit and credit) is named by its position.");
        }

        var taken = await db.StatementMappings.AsNoTracking().AnyAsync(m => m.Name == name && m.Id != id, cancellationToken);
        if (taken)
        {
            return Error.Conflict("statement_mapping.name_taken", "A mapping with this name exists.").WithWhy(("name", name));
        }

        StatementMapping mapping;
        if (id is { } existing)
        {
            var found = await db.StatementMappings.SingleOrDefaultAsync(m => m.Id == existing, cancellationToken);
            if (found is null)
            {
                return Error.NotFound("statement_mapping", existing);
            }

            mapping = found;
        }
        else
        {
            mapping = new StatementMapping { Id = Guid.CreateVersion7(), CreatedAt = clock.UtcNow };
            db.StatementMappings.Add(mapping);
        }

        mapping.Name = name;
        mapping.Settings = JsonSerializer.Serialize(settings, StatementMappingSettings.Json);
        mapping.IsActive = request.IsActive;
        mapping.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return Map(mapping);
    }

    /// <summary>Reads a file with a mapping (or by its format) without importing it: the lines as they would be taken.</summary>
    public static Result<StatementPreview> Preview(StatementPreviewRequest request, string fileName)
    {
        ArgumentNullException.ThrowIfNull(request);
        var format = string.IsNullOrWhiteSpace(request.Format) ? StatementFormats.Guess(fileName, request.Content[..Math.Min(request.Content.Length, 2000)]) ?? StatementFormats.Csv : request.Format.Trim().ToLowerInvariant();
        var parsed = StatementParsers.Parse(format, request.Content, request.Settings);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var p = parsed.Value;
        return new StatementPreview(format, p.Lines.Count, p.From, p.To, p.OpeningBalance, p.ClosingBalance, p.Currency, p.AccountNumber, p.Lines.Take(50).ToList());
    }

    // ------------------------------------------------------------------ statements

    public async Task<IReadOnlyList<BankStatementSummary>> ListAsync(Guid? bankAccountId, Guid? companyId, CancellationToken cancellationToken)
    {
        var query = db.Statements.AsNoTracking().AsQueryable();
        if (bankAccountId is { } a)
        {
            query = query.Where(s => s.BankAccountId == a);
        }

        if (companyId is { } c)
        {
            query = query.Where(s => s.CompanyId == c);
        }

        var statements = await query.OrderByDescending(static s => s.ImportedAt).Take(200).ToListAsync(cancellationToken);
        var result = new List<BankStatementSummary>(statements.Count);
        foreach (var statement in statements)
        {
            result.Add(await MapAsync(statement, false, cancellationToken));
        }

        return result;
    }

    public async Task<Result<BankStatementSummary>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var statement = await db.Statements.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        return statement is null ? Error.NotFound(BankDocumentTypes.Statement, id) : await MapAsync(statement, true, cancellationToken);
    }

    /// <summary>Imports a file for an account: parsed by its format (a CSV through a mapping), lines already known skipped, the rest unmatched until the matcher runs.</summary>
    public async Task<Result<BankStatementSummary>> ImportAsync(Guid bankAccountId, string? format, string fileName, string content, Guid? mappingId, StatementMappingSettings? inlineMapping, CancellationToken cancellationToken)
    {
        var bank = await db.BankAccounts.AsNoTracking().SingleOrDefaultAsync(a => a.Id == bankAccountId, cancellationToken);
        if (bank is null || !bank.IsActive)
        {
            return Error.Validation("statement.bank_account_invalid", "The bank account does not exist or is inactive.").WithWhy(("bankAccountId", bankAccountId));
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Error.Validation("statement.empty", "The file is empty.");
        }

        var resolvedFormat = string.IsNullOrWhiteSpace(format) ? StatementFormats.Guess(fileName, content[..Math.Min(content.Length, 2000)]) : format.Trim().ToLowerInvariant();
        if (resolvedFormat is null || !StatementFormats.All.Contains(resolvedFormat, StringComparer.Ordinal))
        {
            return Error.Validation("statement.format_unknown", "The format is csv, ofx, mt940 or camt053; name it when the file does not say.").WithWhy(("fileName", fileName));
        }

        StatementMappingSettings? settings = inlineMapping;
        if (mappingId is { } m)
        {
            var mapping = await db.StatementMappings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == m, cancellationToken);
            if (mapping is null)
            {
                return Error.NotFound("statement_mapping", m);
            }

            settings = StatementMappingSettings.Parse(mapping.Settings);
        }

        var parsed = StatementParsers.Parse(resolvedFormat, content, settings);
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var p = parsed.Value;
        if (p.Currency is { Length: 3 } currency && !string.Equals(currency, bank.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("statement.currency_mismatch", "The statement is in another currency than the account.").WithWhy(("statementCurrency", currency), ("accountCurrency", bank.Currency));
        }

        if (p.Lines.Count == 0)
        {
            return Error.Validation("statement.no_lines", "The file has no statement lines.").WithWhy(("format", resolvedFormat));
        }

        var statement = new BankStatement
        {
            Id = Guid.CreateVersion7(),
            CompanyId = bank.CompanyId,
            BankAccountId = bank.Id,
            Format = resolvedFormat,
            FileName = Path.GetFileName(fileName) is { Length: > 0 } name ? name[..Math.Min(name.Length, 200)] : "statement",
            FromDate = p.From,
            ToDate = p.To,
            OpeningBalance = p.OpeningBalance,
            ClosingBalance = p.ClosingBalance,
            Status = "imported",
            ImportedBy = principal.Principal?.MembershipId.Value,
            ImportedAt = clock.UtcNow,
        };
        var keys = p.Lines.Select(l => ImportKey(bank.Id, l)).ToList();
        var known = (await db.StatementLines.AsNoTracking().Where(l => l.BankAccountId == bank.Id && keys.Contains(l.ImportKey)).Select(static l => l.ImportKey).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var lineNo = 0;
        var skipped = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (line, key) in p.Lines.Zip(keys))
        {
            if (known.Contains(key) || !seen.Add(key))
            {
                skipped++;
                continue;
            }

            lineNo++;
            statement.Lines.Add(new StatementLine
            {
                Id = Guid.CreateVersion7(),
                StatementId = statement.Id,
                BankAccountId = bank.Id,
                LineNo = lineNo,
                BookingDate = line.BookingDate,
                ValueDate = line.ValueDate,
                Amount = line.Amount,
                Description = line.Description.Length > 1000 ? line.Description[..1000] : line.Description,
                Counterparty = Trim(line.Counterparty, 200),
                Reference = Trim(line.Reference, 200),
                BankCode = Trim(line.BankCode, 40),
                ExternalId = Trim(line.ExternalId, 200),
                ImportKey = key,
                Status = "unmatched",
            });
        }

        if (statement.Lines.Count == 0)
        {
            return Error.Conflict("statement.already_imported", "Every line of this file was imported before.").WithWhy(("skipped", skipped));
        }

        statement.LineCount = statement.Lines.Count;
        statement.SkippedCount = skipped;
        db.Statements.Add(statement);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Statement, statement.Id, statement.FileName, AuditActions.Created, After: new { statement.BankAccountId, statement.Format, statement.FromDate, statement.ToDate, statement.LineCount, statement.SkippedCount, statement.ClosingBalance }, CompanyId: statement.CompanyId), cancellationToken);
        return await MapAsync(statement, true, cancellationToken);
    }

    /// <summary>Removes a statement nothing was matched or created from yet.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var statement = await db.Statements.Include(static s => s.Lines).SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (statement is null)
        {
            return Error.NotFound(BankDocumentTypes.Statement, id);
        }

        if (statement.Lines.Any(static l => l.Status == "matched") || statement.Status == "reconciled")
        {
            return Error.Conflict("statement.in_use", "Lines of this statement are matched; unmatch them first.");
        }

        db.Statements.Remove(statement);
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(BankDocumentTypes.Statement, statement.Id, statement.FileName, AuditActions.Deleted, CompanyId: statement.CompanyId), cancellationToken);
        return Result.Success();
    }

    // ------------------------------------------------------------------ mapping

    public async Task<BankStatementSummary> MapAsync(BankStatement s, bool withLines, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(s);
        var bank = await db.BankAccounts.AsNoTracking().Where(b => b.Id == s.BankAccountId).Select(static b => b.Code).SingleOrDefaultAsync(cancellationToken);
        var counts = await db.StatementLines.AsNoTracking().Where(l => l.StatementId == s.Id).GroupBy(static l => l.Status).Select(static g => new { Status = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        int Count(string status) => counts.FirstOrDefault(c => c.Status == status)?.Count ?? 0;
        var lines = withLines ? await LinesAsync(db.StatementLines.AsNoTracking().Where(l => l.StatementId == s.Id), cancellationToken) : [];
        return new BankStatementSummary(s.Id, s.CompanyId, s.BankAccountId, bank ?? string.Empty, s.Format, s.FileName, s.FromDate, s.ToDate, s.OpeningBalance, s.ClosingBalance, s.LineCount, s.SkippedCount, Count("matched"), Count("unmatched"), Count("excluded"), s.Status, s.ImportedAt, lines);
    }

    /// <summary>Statement lines with their matches, oldest first.</summary>
    public async Task<IReadOnlyList<StatementLineSummary>> LinesAsync(IQueryable<StatementLine> query, CancellationToken cancellationToken)
    {
        var lines = await query.OrderBy(static l => l.BookingDate).ThenBy(static l => l.LineNo).ToListAsync(cancellationToken);
        var ids = lines.Select(static l => l.Id).ToList();
        var matches = await db.ReconciliationMatches.AsNoTracking().Where(m => ids.Contains(m.StatementLineId)).ToListAsync(cancellationToken);
        var transactionIds = matches.Select(static m => m.BankTransactionId).Distinct().ToList();
        var transactions = await db.Transactions.AsNoTracking().Where(t => transactionIds.Contains(t.Id)).ToDictionaryAsync(static t => t.Id, cancellationToken);
        return lines.Select(l => new StatementLineSummary(l.Id, l.StatementId, l.BankAccountId, l.LineNo, l.BookingDate, l.ValueDate, l.Amount, l.Description, l.Counterparty, l.Reference, l.BankCode, l.Status, l.ExcludeReason,
            matches.Where(m => m.StatementLineId == l.Id).Select(m =>
            {
                var t = transactions.GetValueOrDefault(m.BankTransactionId);
                return new ReconciliationMatchSummary(m.Id, m.BankTransactionId, m.MatchKind, m.Confidence, t?.PostingDate ?? l.BookingDate, t?.Kind ?? string.Empty, t?.AmountTc ?? 0m, t?.Reference, t?.SourceDocumentType, t?.SourceDocumentId, m.MatchedAt);
            }).ToList())).ToList();
    }

    private static StatementMappingSummary Map(StatementMapping m) => new(m.Id, m.Name, StatementMappingSettings.Parse(m.Settings), m.IsActive, m.UpdatedAt);

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : value.Length > max ? value[..max] : value;

    private static string ImportKey(Guid bankAccountId, ParsedLine line)
    {
        var text = string.Join("|", bankAccountId.ToString("N"), line.BookingDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), line.Amount.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            line.ExternalId ?? string.Empty, line.Reference ?? string.Empty, line.Description.Trim().ToUpperInvariant());
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
