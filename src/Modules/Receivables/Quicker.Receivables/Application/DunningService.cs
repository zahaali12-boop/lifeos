using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Quicker.Accounting.Contracts;
using Quicker.Audit.Contracts;
using Quicker.Identity.Contracts;
using Quicker.Kernel.Ids;
using Quicker.Kernel.Results;
using Quicker.Kernel.Text;
using Quicker.Kernel.Time;
using Quicker.Numbering.Contracts;
using Quicker.Organization.Contracts;
using Quicker.Partners.Contracts;
using Quicker.Receivables.Contracts;
using Quicker.Receivables.Domain;
using Quicker.Receivables.Persistence;

namespace Quicker.Receivables.Application;

/// <summary>
/// Dunning (roadmap 5.7b, POSTING_RULES §2 "Dunning fee", A-162): a company's reminder ladder of levels by days past
/// due, and runs that issue one letter per customer whose overdue items climbed a rung since the last letter. A letter
/// names every overdue, unheld, unpromised item of the customer as it stood on the run date with the level each
/// reached, carries the level of the highest, charges the level's fee as a receivable adjustment in the company's
/// currency (Dr <c>AR</c> / Cr <c>InterestIncome</c>) and, when the level says so, puts the customer on credit hold.
/// Items and the customer's account remember their level; the letters are the record a print pipeline (5.9) renders.
/// </summary>
public sealed class DunningService(ReceivablesDbContext db, ReceivablesService receivables, ICompanyDirectory companies, IPartnerDirectory partners, ICustomerDirectory customers, INumberAllocator numbering, IPostingService posting, ICurrentPrincipal principal, IAuditSink audit, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ------------------------------------------------------------------ levels

    public async Task<IReadOnlyList<DunningLevelSummary>> ListLevelsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var rows = await db.DunningLevels.AsNoTracking().Where(l => l.CompanyId == companyId).OrderBy(static l => l.Level).ToListAsync(cancellationToken);
        return rows.Select(l => Map(l, company?.FunctionalCurrency.Code ?? string.Empty)).ToList();
    }

    public async Task<Result<DunningLevelSummary>> SaveLevelAsync(Guid? id, SaveDunningLevelRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.Validation("dunning.company_unknown", "The company does not exist.").WithWhy(("companyId", request.CompanyId));
        }

        if (request.Level is < 1 or > 9)
        {
            return Error.Validation("dunning.level_invalid", "A dunning level is 1 to 9.").WithWhy(("level", request.Level));
        }

        if (request.DaysOverdue < 1)
        {
            return Error.Validation("dunning.days_invalid", "A level is reached at least one day past due.").WithWhy(("daysOverdue", request.DaysOverdue));
        }

        if (request.FeeAmount < 0m)
        {
            return Error.Validation("dunning.fee_invalid", "A fee is not negative.").WithWhy(("feeAmount", request.FeeAmount));
        }

        if (request.Name.Count == 0 || !request.Name.TryGetValue("en", out var english) || string.IsNullOrWhiteSpace(english))
        {
            return Error.Validation("dunning.name_required", "A level has a name, in English at least.");
        }

        var row = id is { } existing ? await db.DunningLevels.SingleOrDefaultAsync(l => l.Id == existing, cancellationToken) : null;
        if (id is not null && row is null)
        {
            return Error.NotFound("dunning_level", id.Value);
        }

        if (row is not null && row.CompanyId != request.CompanyId)
        {
            return Error.Validation("dunning.company_locked", "A level stays with its company.");
        }

        var clash = await db.DunningLevels.AsNoTracking().AnyAsync(l => l.CompanyId == request.CompanyId && l.Level == request.Level && (row == null || l.Id != row.Id), cancellationToken);
        if (clash)
        {
            return Error.Conflict("dunning.level_taken", "The company already has a level with that number.").WithWhy(("level", request.Level));
        }

        // The ladder climbs: a higher level is reached later than a lower one.
        var others = await db.DunningLevels.AsNoTracking().Where(l => l.CompanyId == request.CompanyId && l.IsActive && (row == null || l.Id != row.Id)).ToListAsync(cancellationToken);
        if (request.IsActive && others.Any(o => (o.Level < request.Level && o.DaysOverdue >= request.DaysOverdue) || (o.Level > request.Level && o.DaysOverdue <= request.DaysOverdue)))
        {
            return Error.Validation("dunning.ladder_order", "Levels are reached in order: a higher level needs more days past due than a lower one.").WithWhy(("level", request.Level), ("daysOverdue", request.DaysOverdue));
        }

        if (row is null)
        {
            row = new ArDunningLevel { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, CreatedAt = clock.UtcNow };
            db.DunningLevels.Add(row);
        }

        row.Level = request.Level;
        row.DaysOverdue = request.DaysOverdue;
        row.Name = new LocalizedText(request.Name);
        row.Text = new LocalizedText(request.Text ?? new Dictionary<string, string>(StringComparer.Ordinal));
        row.FeeAmount = request.FeeAmount;
        row.BlockCustomer = request.BlockCustomer;
        row.IsActive = request.IsActive;
        row.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry("dunning_level", row.Id, $"L{row.Level}", id is null ? AuditActions.Created : AuditActions.Updated, After: new { row.Level, row.DaysOverdue, row.FeeAmount, row.BlockCustomer, row.IsActive }, CompanyId: row.CompanyId), cancellationToken);
        return Map(row, company.FunctionalCurrency.Code);
    }

    // ------------------------------------------------------------------ runs

    public async Task<IReadOnlyList<DunningRunSummary>> ListRunsAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var rows = await db.DunningRuns.AsNoTracking().Where(r => r.CompanyId == companyId).OrderByDescending(static r => r.RunDate).ThenByDescending(static r => r.Number).Take(200).ToListAsync(cancellationToken);
        return rows.Select(r => new DunningRunSummary(r.Id, r.CompanyId, r.Number, r.RunDate, r.PartnerId, r.Status, r.Letters, r.FeesFc, company?.FunctionalCurrency.Code ?? string.Empty, r.CreatedAt, [])).ToList();
    }

    public async Task<Result<DunningRunSummary>> GetRunAsync(Guid id, CancellationToken cancellationToken)
    {
        var run = await db.DunningRuns.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (run is null)
        {
            return Error.NotFound(ReceivablesDocumentTypes.DunningRun, id);
        }

        var company = await companies.FindAsync(new CompanyId(run.CompanyId), cancellationToken);
        var letters = await db.DunningLetters.AsNoTracking().Where(l => l.RunId == run.Id).OrderBy(static l => l.Number).ToListAsync(cancellationToken);
        var levels = await db.DunningLevels.AsNoTracking().Where(l => l.CompanyId == run.CompanyId).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var names = await receivables.PartnersAsync(letters.Select(static l => l.PartnerId), cancellationToken);
        var fc = company?.FunctionalCurrency.Code ?? string.Empty;
        return new DunningRunSummary(run.Id, run.CompanyId, run.Number, run.RunDate, run.PartnerId, run.Status, run.Letters, run.FeesFc, fc, run.CreatedAt,
            letters.Select(l => Map(l, run, levels.GetValueOrDefault(l.LevelId), names.GetValueOrDefault(l.PartnerId), fc)).ToList());
    }

    public async Task<IReadOnlyList<DunningLetterSummary>> LettersAsync(Guid companyId, Guid? partnerId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        var query = db.DunningLetters.AsNoTracking().Where(l => l.CompanyId == companyId);
        if (partnerId is { } p)
        {
            query = query.Where(l => l.PartnerId == p);
        }

        var letters = await query.OrderByDescending(static l => l.CreatedAt).Take(500).ToListAsync(cancellationToken);
        var runIds = letters.Select(static l => l.RunId).Distinct().ToList();
        var runs = await db.DunningRuns.AsNoTracking().Where(r => runIds.Contains(r.Id)).ToDictionaryAsync(static r => r.Id, cancellationToken);
        var levels = await db.DunningLevels.AsNoTracking().Where(l => l.CompanyId == companyId).ToDictionaryAsync(static l => l.Id, cancellationToken);
        var names = await receivables.PartnersAsync(letters.Select(static l => l.PartnerId), cancellationToken);
        var fc = company?.FunctionalCurrency.Code ?? string.Empty;
        return letters.Select(l => Map(l, runs[l.RunId], levels.GetValueOrDefault(l.LevelId), names.GetValueOrDefault(l.PartnerId), fc)).ToList();
    }

    public async Task<Result<DunningLetterSummary>> MarkSentAsync(Guid letterId, CancellationToken cancellationToken)
    {
        var letter = await db.DunningLetters.SingleOrDefaultAsync(l => l.Id == letterId, cancellationToken);
        if (letter is null)
        {
            return Error.NotFound(ReceivablesDocumentTypes.DunningLetter, letterId);
        }

        letter.SentAt ??= clock.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        var run = await db.DunningRuns.AsNoTracking().SingleAsync(r => r.Id == letter.RunId, cancellationToken);
        var company = await companies.FindAsync(new CompanyId(letter.CompanyId), cancellationToken);
        var level = await db.DunningLevels.AsNoTracking().SingleOrDefaultAsync(l => l.Id == letter.LevelId, cancellationToken);
        return Map(letter, run, level, await partners.FindAsync(letter.PartnerId, cancellationToken), company?.FunctionalCurrency.Code ?? string.Empty);
    }

    /// <summary>What a run on the date would issue: one letter per customer with an item that climbs a rung, nothing written.</summary>
    public async Task<Result<DunningPreview>> PreviewAsync(Guid companyId, DateOnly? runDate, Guid? partnerId, CancellationToken cancellationToken)
    {
        var company = await companies.FindAsync(new CompanyId(companyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", companyId);
        }

        var date = runDate ?? clock.TodayIn(company.TimeZone);
        var proposed = await ProposeAsync(companyId, date, partnerId, cancellationToken);
        if (proposed.IsFailure)
        {
            return proposed.Error!;
        }

        var names = await receivables.PartnersAsync(proposed.Value.Select(static p => p.PartnerId), cancellationToken);
        var preview = new ArDunningRun { Id = Guid.Empty, CompanyId = companyId, Number = string.Empty, RunDate = date };
        var fc = company.FunctionalCurrency.Code;
        return new DunningPreview(companyId, date, fc, proposed.Value.Select(p => Map(p.ToLetter(preview, string.Empty, clock.UtcNow), preview, p.Level, names.GetValueOrDefault(p.PartnerId), fc)).ToList());
    }

    public async Task<Result<DunningRunSummary>> RunAsync(DunningRunRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var company = await companies.FindAsync(new CompanyId(request.CompanyId), cancellationToken);
        if (company is null)
        {
            return Error.NotFound("company", request.CompanyId);
        }

        var date = request.RunDate ?? clock.TodayIn(company.TimeZone);
        if (date > clock.TodayIn(company.TimeZone))
        {
            return Error.Validation("dunning.run_date_future", "A run is dated today or earlier.").WithWhy(("runDate", date));
        }

        var proposed = await ProposeAsync(request.CompanyId, date, request.PartnerId, cancellationToken);
        if (proposed.IsFailure)
        {
            return proposed.Error!;
        }

        if (proposed.Value.Count == 0)
        {
            return Error.Conflict("dunning.nothing_due", "No customer has an overdue item that climbs a level on that date.").WithWhy(("runDate", date));
        }

        var run = new ArDunningRun { Id = Guid.CreateVersion7(), CompanyId = request.CompanyId, RunDate = date, PartnerId = request.PartnerId, RunBy = principal.Principal?.MembershipId.Value, CreatedAt = clock.UtcNow, UpdatedAt = clock.UtcNow };
        await numbering.EnsureDefaultSeriesAsync(ReceivablesDocumentTypes.DunningRun, new CompanyId(run.CompanyId), "DUN", "DUN-{yyyy}-{seq:5}", "yearly", cancellationToken);
        var number = await numbering.AllocateAsync(new NumberRequest(ReceivablesDocumentTypes.DunningRun, new CompanyId(run.CompanyId), null, date, run.Id), cancellationToken);
        if (number.IsFailure)
        {
            return number.Error!;
        }

        run.Number = number.Value.Text;
        db.DunningRuns.Add(run);
        var sequence = 0;
        var fees = 0m;
        foreach (var candidate in proposed.Value)
        {
            sequence++;
            var letter = candidate.ToLetter(run, $"{run.Number}-{sequence:00}", clock.UtcNow);
            var partner = await partners.FindAsync(candidate.PartnerId, cancellationToken);

            // Every item named climbs to its level and remembers the day; the customer's account carries the letter's level.
            foreach (var item in candidate.Items)
            {
                var tracked = await db.OpenItems.SingleAsync(i => i.Id == item.ItemId, cancellationToken);
                tracked.DunningLevel = item.Level;
                tracked.LastDunnedOn = date;
                tracked.UpdatedAt = clock.UtcNow;
            }

            if (request.ChargeFees && candidate.Level.FeeAmount > 0m)
            {
                var fee = await ChargeFeeAsync(company, candidate, letter, partner, date, cancellationToken);
                if (fee.IsFailure)
                {
                    return fee.Error!.WithWhy(("partnerId", candidate.PartnerId));
                }

                fees += candidate.Level.FeeAmount;
            }

            if (candidate.Level.BlockCustomer)
            {
                var hold = await customers.RecordDunningAsync(run.CompanyId, candidate.PartnerId, candidate.Level.Level, $"Dunning level {candidate.Level.Level} reached on {date:yyyy-MM-dd} ({letter.Number})", cancellationToken);
                if (hold.IsFailure)
                {
                    return hold.Error!;
                }

                letter.BlockedCustomer = true;
            }
            else
            {
                var recorded = await customers.RecordDunningAsync(run.CompanyId, candidate.PartnerId, candidate.Level.Level, null, cancellationToken);
                if (recorded.IsFailure)
                {
                    return recorded.Error!;
                }
            }

            db.DunningLetters.Add(letter);
        }

        run.Letters = sequence;
        run.FeesFc = fees;
        await db.SaveChangesAsync(cancellationToken);
        await audit.RecordAsync(new AuditEntry(ReceivablesDocumentTypes.DunningRun, run.Id, run.Number, AuditActions.Posted, After: new { run.RunDate, run.Letters, run.FeesFc, partnerId = run.PartnerId }, CompanyId: run.CompanyId), cancellationToken);
        return await GetRunAsync(run.Id, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    /// <summary>A customer whose overdue items climb a rung on the date, with every overdue item of theirs as the letter names it.</summary>
    private sealed record Candidate(Guid PartnerId, ArDunningLevel Level, IReadOnlyList<DunningLetterItem> Items)
    {
        public ArDunningLetter ToLetter(ArDunningRun run, string number, DateTimeOffset now) => new()
        {
            Id = Guid.CreateVersion7(),
            RunId = run.Id,
            CompanyId = run.CompanyId,
            PartnerId = PartnerId,
            Number = number,
            Level = Level.Level,
            LevelId = Level.Id,
            Items = JsonSerializer.Serialize(Items, Json),
            ItemCount = Items.Count,
            OverdueFc = Items.Sum(static i => i.RemainingFc),
            FeeAmount = Level.FeeAmount,
            CreatedAt = now,
        };
    }

    private async Task<Result<IReadOnlyList<Candidate>>> ProposeAsync(Guid companyId, DateOnly date, Guid? partnerId, CancellationToken cancellationToken)
    {
        var levels = await db.DunningLevels.AsNoTracking().Where(l => l.CompanyId == companyId && l.IsActive).OrderBy(static l => l.DaysOverdue).ToListAsync(cancellationToken);
        if (levels.Count == 0)
        {
            return Error.Conflict("dunning.no_levels", "The company has no active dunning level; set up the ladder first.").WithWhy(("companyId", companyId));
        }

        // Overdue, unheld, unpromised invoice items of the company (or the customer) at the date.
        var query = db.OpenItems.AsNoTracking().Where(i => i.CompanyId == companyId && i.OriginalTc > 0m && (i.Status == "open" || i.Status == "partially_settled") && !i.OnHold && i.DueDate < date && i.Kind != ReceivableKinds.Deposit);
        if (partnerId is { } p)
        {
            query = query.Where(i => i.PartnerId == p);
        }

        var items = await query.ToListAsync(cancellationToken);
        var candidates = new List<Candidate>();
        foreach (var group in items.Where(i => i.PromisedPaymentDate is null || i.PromisedPaymentDate < date).GroupBy(static i => i.PartnerId))
        {
            var letterItems = new List<DunningLetterItem>();
            var climbs = false;
            foreach (var item in group.OrderBy(static i => i.DueDate).ThenBy(static i => i.DocumentNumber, StringComparer.Ordinal))
            {
                var days = date.DayNumber - item.DueDate.DayNumber;
                var reached = levels.Where(l => l.DaysOverdue <= days).Select(static l => l.Level).DefaultIfEmpty(0).Max();
                if (reached == 0)
                {
                    continue;
                }

                climbs |= reached > item.DunningLevel;
                letterItems.Add(new DunningLetterItem(item.Id, item.DocumentType, item.DocumentId, item.DocumentNumber, item.Instalment, item.DueDate, days, item.Currency, item.RemainingTc, item.RemainingFc, item.DunningLevel, reached));
            }

            if (!climbs)
            {
                continue;
            }

            var top = letterItems.Max(static i => i.Level);
            candidates.Add(new Candidate(group.Key, levels.Single(l => l.Level == top), letterItems));
        }

        return candidates.OrderBy(static c => c.PartnerId).ToList();
    }

    /// <summary>The level's fee charged to the customer: Dr receivables / Cr interest income in the company's currency, an adjustment item of the letter's own.</summary>
    private async Task<Result> ChargeFeeAsync(CompanyInfo company, Candidate candidate, ArDunningLetter letter, PartnerInfo? partner, DateOnly date, CancellationToken cancellationToken)
    {
        var customer = await customers.FindCustomerAsync(company.Id.Value, candidate.PartnerId, cancellationToken);
        var keys = new PostingKeys(ReceivablesDocumentTypes.DunningLetter, PartnerPostingGroupId: customer?.PostingGroupId);
        var lines = new List<PostingLine>
        {
            new(AccountRoles.AR, candidate.Level.FeeAmount, keys, SubledgerType: SubledgerTypes.Receivables, SubledgerRef: letter.Id, PartnerId: candidate.PartnerId, DueDate: date),
            new(AccountRoles.InterestIncome, -candidate.Level.FeeAmount, keys, PartnerId: candidate.PartnerId),
        };
        var description = LocalizedText.Bilingual($"Dunning fee, level {candidate.Level.Level}, {partner?.Code} ({letter.Number})", $"رسم تذكير، المستوى {candidate.Level.Level}، {partner?.Code} ({letter.Number})");
        var posted = await posting.PostAsync(new PostingRequest(company.Id, "receivables", ReceivablesDocumentTypes.DunningLetter, letter.Id, date, company.FunctionalCurrency.Code, lines, letter.Number, date, description,
            null, RateTypes.Spot, null, null, $"ar_dunning_letter:{letter.Id}"), cancellationToken);
        if (posted.IsFailure)
        {
            return posted.Error!;
        }

        var item = await receivables.OpenAsync(new NewReceivable(company.Id.Value, candidate.PartnerId, ReceivableKinds.Adjustment, ReceivablesDocumentTypes.DunningLetter, letter.Id, letter.Number, 1, null, date, date, date, null, 0m,
            company.FunctionalCurrency.Code, candidate.Level.FeeAmount, candidate.Level.FeeAmount, 1m, posted.Value.EntryId, SalesRepId: customer?.SalesRepId), cancellationToken);
        letter.FeeItemId = item.Id;
        letter.FeeJournalEntryId = posted.Value.EntryId;
        return Result.Success();
    }

    private static DunningLevelSummary Map(ArDunningLevel l, string currency) => new(l.Id, l.CompanyId, l.Level, l.DaysOverdue, l.Name.Values, l.Text.Values, l.FeeAmount, currency, l.BlockCustomer, l.IsActive, l.UpdatedAt);

    private static DunningLetterSummary Map(ArDunningLetter l, ArDunningRun run, ArDunningLevel? level, PartnerInfo? partner, string fc) => new(
        l.Id, l.RunId, run.Number, run.RunDate, l.CompanyId, l.PartnerId, partner?.Code ?? string.Empty, partner?.LegalName.Values ?? ReceivablesService.Empty(), l.Number, l.Level,
        level?.Name.Values ?? ReceivablesService.Empty(), level?.Text.Values ?? ReceivablesService.Empty(), JsonSerializer.Deserialize<List<DunningLetterItem>>(l.Items, Json) ?? [], l.ItemCount, l.OverdueFc, fc, l.FeeAmount, l.FeeItemId, l.BlockedCustomer, l.SentAt, l.CreatedAt);
}
